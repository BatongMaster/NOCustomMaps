using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace CustomMaps.Tests
{
    /// <summary>
    /// A bundle's hash, remembered between starts. The value is a map's multiplayer identity, so
    /// the one thing that must never happen is a remembered hash standing in for a file it was not
    /// computed from: every case here that changes the file expects the hash computed again.
    /// </summary>
    public sealed class BundleHashTests : IDisposable
    {
        const int MiB = 1 << 20;

        readonly string _folder = Path.Combine(Path.GetTempPath(), "cm-hash-" + Guid.NewGuid().ToString("N"));

        public BundleHashTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            try { Directory.Delete(_folder, recursive: true); }
            catch (IOException) { }
        }

        string Write(string name, byte[] bytes)
        {
            string path = Path.Combine(_folder, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        static byte[] Bytes(int length, int seed)
        {
            var bytes = new byte[length];
            new Random(seed).NextBytes(bytes);
            return bytes;
        }

        static string Expected(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        /// <summary>Inverts one byte of a file in place, keeping its length and its last-write time:
        /// the one change the path, length and time alone cannot see.</summary>
        static void Flip(string path, int at)
        {
            DateTime written = File.GetLastWriteTimeUtc(path);
            byte[] bytes = File.ReadAllBytes(path);
            bytes[at] = (byte)~bytes[at];
            File.WriteAllBytes(path, bytes);
            File.SetLastWriteTimeUtc(path, written);
        }

        [Fact]
        public void TheSha256OfAKnownInputIsTheStandardOne()
        {
            using var stream = new MemoryStream(Encoding.ASCII.GetBytes("abc"));

            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", BundleHash.Sha256(stream));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(MiB - 1)]
        [InlineData(MiB)]
        [InlineData(MiB + 1)]
        [InlineData(2 * MiB)]
        [InlineData(2 * MiB + 1)]
        [InlineData(5 * MiB + 7)]
        public void TheHashIsTheWholeFilesSha256WhetherComputedOrRemembered(int length)
        {
            byte[] bytes = Bytes(length, length);
            string path = Write("map.nomap", bytes);
            var cache = new BundleHashCache();

            string first = BundleHash.Resolve(path, cache, out bool firstRemembered);
            string second = BundleHash.Resolve(path, cache, out bool secondRemembered);

            Assert.Equal(Expected(bytes), first);
            Assert.False(firstRemembered);
            Assert.Equal(first, second);
            Assert.True(secondRemembered);
            Assert.Equal(Expected(bytes), BundleHash.Resolve(path, null, out bool none));
            Assert.False(none);
        }

        /// <summary>The point of the cache: an unchanged file is not read again. Shown by a remembered
        /// value that is not the file's hash at all, which only a lookup could return.</summary>
        [Fact]
        public void AnUnchangedFileTakesTheRememberedHashWithoutHashingIt()
        {
            string path = Write("map.nomap", Bytes(3 * MiB, 1));
            var cache = new BundleHashCache();
            string real = BundleHash.Resolve(path, cache, out _);

            string planted = new string('a', 64);
            cache = BundleHashCache.Parse(cache.Serialise().Replace(real, planted));

            Assert.Equal(planted, BundleHash.Resolve(path, cache, out bool remembered));
            Assert.True(remembered);
        }

        [Theory]
        [InlineData(0)]                 // the header
        [InlineData(MiB - 1)]           // the end of the first span
        [InlineData(4 * MiB + 100)]     // the last span
        public void ABundleRewrittenInPlaceIsHashedAgain(int at)
        {
            string path = Write("map.nomap", Bytes(5 * MiB, 2));
            var cache = new BundleHashCache();
            string before = BundleHash.Resolve(path, cache, out _);

            Flip(path, at);
            string after = BundleHash.Resolve(path, cache, out bool remembered);

            Assert.False(remembered);
            Assert.NotEqual(before, after);
            Assert.Equal(Expected(File.ReadAllBytes(path)), after);
        }

        [Fact]
        public void ALongerOrShorterBundleIsHashedAgain()
        {
            string path = Write("map.nomap", Bytes(3 * MiB, 3));
            var cache = new BundleHashCache();
            BundleHash.Resolve(path, cache, out _);

            File.WriteAllBytes(path, Bytes(3 * MiB + 1, 3));
            Assert.Equal(Expected(File.ReadAllBytes(path)), BundleHash.Resolve(path, cache, out bool longer));
            Assert.False(longer);

            File.WriteAllBytes(path, Bytes(3 * MiB - 1, 3));
            Assert.Equal(Expected(File.ReadAllBytes(path)), BundleHash.Resolve(path, cache, out bool shorter));
            Assert.False(shorter);
        }

        [Fact]
        public void ATouchedBundleIsHashedAgainAndKeepsItsHash()
        {
            string path = Write("map.nomap", Bytes(3 * MiB, 4));
            var cache = new BundleHashCache();
            string before = BundleHash.Resolve(path, cache, out _);

            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));

            Assert.Equal(before, BundleHash.Resolve(path, cache, out bool remembered));
            Assert.False(remembered);
            BundleHash.Resolve(path, cache, out bool then);
            Assert.True(then);
        }

        [Fact]
        public void TheSameBundleAtAnotherPathIsHashedOnItsOwn()
        {
            byte[] bytes = Bytes(3 * MiB, 5);
            string a = Write("a.nomap", bytes);
            string b = Write("b.nomap", bytes);
            File.SetLastWriteTimeUtc(b, File.GetLastWriteTimeUtc(a));
            var cache = new BundleHashCache();

            BundleHash.Resolve(a, cache, out _);
            BundleHash.Resolve(b, cache, out bool remembered);

            Assert.False(remembered);
            Assert.Equal(2, cache.Count);
        }

        /// <summary>The fingerprint's documented limit, pinned so it is not mistaken for more: a byte
        /// changed in the middle of a file, with its length and time kept, is not seen by it.</summary>
        [Fact]
        public void TheFingerprintCoversTheEndsAndTheLengthOnly()
        {
            string path = Write("map.nomap", Bytes(5 * MiB, 6));
            string Fingerprint()
            {
                using FileStream s = File.OpenRead(path);
                return BundleHash.Fingerprint(s);
            }

            string original = Fingerprint();
            Flip(path, 2 * MiB);
            Assert.Equal(original, Fingerprint());

            Flip(path, 10);
            Assert.NotEqual(original, Fingerprint());

            File.WriteAllBytes(path, new byte[0]);
            using (FileStream s = File.OpenRead(path))
                Assert.True(BundleHash.IsSha256(BundleHash.Fingerprint(s)));
        }

        [Fact]
        public void TheCacheSurvivesASaveAndALoad()
        {
            string path = Write("map.nomap", Bytes(3 * MiB, 7));
            string file = Path.Combine(_folder, "cache", "hashes.txt");
            var cache = new BundleHashCache();
            string hash = BundleHash.Resolve(path, cache, out _);
            Assert.True(cache.Changed);

            Assert.Null(cache.Save(file));
            Assert.False(cache.Changed);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(file), "*.tmp"));

            BundleHashCache loaded = BundleHashCache.Load(file);
            Assert.Equal(hash, BundleHash.Resolve(path, loaded, out bool remembered));
            Assert.True(remembered);
            Assert.False(loaded.Changed);
        }

        /// <summary>The second and later saves replace the file in place rather than deleting it
        /// first; what is read back is the new cache, whole.</summary>
        [Fact]
        public void ASaveOverAnEarlierOneReplacesIt()
        {
            string h = new string('1', 64), g = new string('3', 64), f = new string('2', 64);
            string file = Path.Combine(_folder, "hashes.txt");
            var first = new BundleHashCache();
            first.Store(new BundleStamp("/maps/old.nomap", 10, 20, f), h);
            Assert.Null(first.Save(file));

            var second = new BundleHashCache();
            second.Store(new BundleStamp("/maps/new.nomap", 30, 40, f), g);
            Assert.Null(second.Save(file));

            BundleHashCache back = BundleHashCache.Load(file);
            Assert.Equal(1, back.Count);
            Assert.Equal(g, back.Lookup(new BundleStamp("/maps/new.nomap", 30, 40, f)));
            Assert.Equal(new[] { file }, Directory.GetFiles(_folder));
        }

        /// <summary>A game stopped half way through a save leaves its temporary file beside the
        /// cache. The next save clears it once it is old, and leaves a newer one (another game's save
        /// under way) and anything not named as a save names its files.</summary>
        [Fact]
        public void ASaveClearsOldTemporaryFilesOfEarlierSavesOnly()
        {
            string file = Path.Combine(_folder, "hashes.txt");
            string Temp(string name, TimeSpan age)
            {
                string path = Write(name, new byte[] { 1 });
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
                return path;
            }

            string guid = Guid.NewGuid().ToString("N");
            string stale = Temp("hashes.txt." + guid + ".tmp", TimeSpan.FromHours(3));
            string staleUpper = Temp("hashes.txt." + Guid.NewGuid().ToString("N").ToUpperInvariant() + ".tmp", TimeSpan.FromDays(2));
            string fresh = Temp("hashes.txt." + Guid.NewGuid().ToString("N") + ".tmp", TimeSpan.FromMinutes(1));
            string longer = Temp("hashes.txt." + guid + ".tmpx", TimeSpan.FromHours(3));
            string notHex = Temp("hashes.txt." + new string('z', 32) + ".tmp", TimeSpan.FromHours(3));
            string otherCache = Temp("other.txt." + guid + ".tmp", TimeSpan.FromHours(3));
            string shortName = Temp("hashes.txt.1234.tmp", TimeSpan.FromHours(3));

            var cache = new BundleHashCache();
            cache.Store(new BundleStamp("/maps/a.nomap", 10, 20, new string('2', 64)), new string('1', 64));
            Assert.Null(cache.Save(file));

            Assert.False(File.Exists(stale));
            Assert.False(File.Exists(staleUpper));
            Assert.True(File.Exists(fresh));
            Assert.True(File.Exists(longer));
            Assert.True(File.Exists(notHex));
            Assert.True(File.Exists(otherCache));
            Assert.True(File.Exists(shortName));
            Assert.Equal(1, BundleHashCache.Load(file).Count);
        }

        [Fact]
        public void ClearingTemporaryFilesBesideAFolderThatIsNotThereDoesNothing()
        {
            BundleHashCache.ClearStaleTemps(Path.Combine(_folder, "missing", "hashes.txt"), DateTime.UtcNow);
            BundleHashCache.ClearStaleTemps("hashes.txt", DateTime.UtcNow);
        }

        [Fact]
        public void AMissingCacheFileIsAnEmptyCache()
        {
            BundleHashCache cache = BundleHashCache.Load(Path.Combine(_folder, "nothing-here.txt"));

            Assert.Equal(0, cache.Count);
            Assert.False(cache.Changed);
        }

        [Fact]
        public void AnotherFormatOrGarbageIsIgnoredWhole()
        {
            string good = new string('1', 64) + "\t10\t20\t" + new string('2', 64) + "\tC:\\maps\\a.nomap\n";

            Assert.Equal(1, BundleHashCache.Parse(BundleHashCache.Header + "\n" + good).Count);
            Assert.Equal(0, BundleHashCache.Parse("NOCustomMaps bundle hashes 2\n" + good).Count);
            Assert.Equal(0, BundleHashCache.Parse(good).Count);
            Assert.Equal(0, BundleHashCache.Parse("").Count);
            Assert.Equal(0, BundleHashCache.Parse(null).Count);
            Assert.Equal(0, BundleHashCache.Parse("\0\0\0garbage").Count);
        }

        [Fact]
        public void ALineThatDoesNotReadBackWholeIsDropped()
        {
            string h = new string('a', 64), f = new string('2', 64);
            string text = BundleHashCache.Header + "\r\n" +
                          $"{h}\t10\t20\t{f}\tC:\\maps\\kept.nomap\r\n" +
                          $"{h}\t10\t20\t{f}\r\n" +                                    // no path
                          $"{h}\t10\t20\t{f}\t\r\n" +                                  // empty path
                          $"{h.ToUpperInvariant()}\t10\t20\t{f}\tC:\\maps\\upper.nomap\n" +
                          $"{h.Substring(1)}\t10\t20\t{f}\tC:\\maps\\short.nomap\n" +
                          $"{h}\t-10\t20\t{f}\tC:\\maps\\negative.nomap\n" +
                          $"{h}\tten\t20\t{f}\tC:\\maps\\word.nomap\n" +
                          $"{h}\t10\t20\t{f.Replace('2', 'g')}\tC:\\maps\\nothex.nomap\n" +
                          $"{h}\t10\t20\t{f}\tC:\\maps\\also kept.nomap";

            BundleHashCache cache = BundleHashCache.Parse(text);

            Assert.Equal(2, cache.Count);
            Assert.Equal(h, cache.Lookup(new BundleStamp(@"C:\maps\kept.nomap", 10, 20, f)));
            Assert.Equal(h, cache.Lookup(new BundleStamp(@"C:\maps\also kept.nomap", 10, 20, f)));
        }

        [Fact]
        public void ALookupNeedsEveryPartOfTheStamp()
        {
            string h = new string('1', 64), f = new string('2', 64), g = new string('3', 64);
            var cache = new BundleHashCache();
            cache.Store(new BundleStamp("/maps/a.nomap", 10, 20, f), h);

            Assert.Equal(h, cache.Lookup(new BundleStamp("/maps/a.nomap", 10, 20, f)));
            Assert.Null(cache.Lookup(new BundleStamp("/maps/A.nomap", 10, 20, f)));
            Assert.Null(cache.Lookup(new BundleStamp("/maps/a.nomap", 11, 20, f)));
            Assert.Null(cache.Lookup(new BundleStamp("/maps/a.nomap", 10, 21, f)));
            Assert.Null(cache.Lookup(new BundleStamp("/maps/a.nomap", 10, 20, g)));
            Assert.Null(cache.Lookup(null));
        }

        [Fact]
        public void PathsWithTabsAndSpacesComeBackAndLineBreaksAreNotStored()
        {
            string h = new string('1', 64), f = new string('2', 64);
            var cache = new BundleHashCache();
            var odd = new BundleStamp("/home/server/my maps/tab\there.nomap", 10, 20, f);
            cache.Store(odd, h);
            cache.Store(new BundleStamp("/maps/line\nbreak.nomap", 10, 20, f), h);
            cache.Store(new BundleStamp("/maps/bad-hash.nomap", 10, 20, f), "not a hash");

            BundleHashCache back = BundleHashCache.Parse(cache.Serialise());

            Assert.Equal(1, back.Count);
            Assert.Equal(h, back.Lookup(odd));
        }

        [Fact]
        public void PruningForgetsBundlesThatAreGone()
        {
            string h = new string('1', 64), f = new string('2', 64);
            var cache = new BundleHashCache();
            cache.Store(new BundleStamp("/maps/here.nomap", 10, 20, f), h);
            cache.Store(new BundleStamp("/maps/gone.nomap", 10, 20, f), h);
            BundleHashCache saved = BundleHashCache.Parse(cache.Serialise());

            saved.Prune(path => path == "/maps/here.nomap");

            Assert.True(saved.Changed);
            Assert.Equal(1, saved.Count);
            Assert.Equal(h, saved.Lookup(new BundleStamp("/maps/here.nomap", 10, 20, f)));

            saved.Prune(path => throw new UnauthorizedAccessException());
            Assert.Equal(1, saved.Count);
        }

        [Fact]
        public void StoringWhatIsAlreadyThereChangesNothing()
        {
            string h = new string('1', 64), f = new string('2', 64);
            var stamp = new BundleStamp("/maps/a.nomap", 10, 20, f);
            BundleHashCache cache = BundleHashCache.Parse(BundleHashCache.Header + "\n" + $"{h}\t10\t20\t{f}\t/maps/a.nomap\n");

            cache.Store(stamp, h);

            Assert.False(cache.Changed);
        }
    }
}
