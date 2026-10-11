using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace CustomMaps.Tests
{
    /// <summary>
    /// Reading a <c>.nomapz</c>: its header and index (<see cref="NomapzHeader"/>), and the stream Unity
    /// loads the bundle through (<see cref="NomapzStream"/>), against the bytes the file was made from.
    ///
    /// The files are written here with the framework's own Brotli encoder and the header's own
    /// <see cref="NomapzHeader.ToBytes"/>, as Map Forge's writer does, in chunks of the format's
    /// smallest size (4 KiB), so a test file has many chunks and a short last one and stays small.
    /// </summary>
    public sealed class NomapzTests : IDisposable
    {
        const int Chunk = NomapzHeader.MinChunkSize;

        readonly string _folder = Path.Combine(Path.GetTempPath(), "cm-nomapz-" + Guid.NewGuid().ToString("N"));

        public NomapzTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            try { Directory.Delete(_folder, recursive: true); }
            catch (IOException) { }
        }

        static byte[] Payload(int length, int seed)
        {
            var bytes = new byte[length];
            var random = new Random(seed);
            for (int i = 0; i < length; i++)
                bytes[i] = (i / 900) % 3 == 0 ? (byte)random.Next(256) : (byte)(i / 5 + seed);
            return bytes;
        }

        static byte[] Identity(string text) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text));

        /// <summary>A .nomapz of <paramref name="payload"/>, laid out as the format says.</summary>
        string Write(byte[] payload, string name = "map-1.0.0.nomapz", byte[] identity = null)
        {
            int count = NomapzHeader.CountFor(payload.Length, Chunk);
            var header = new NomapzHeader
            {
                ChunkSize = Chunk,
                BundleLength = payload.Length,
                ChunkCount = count,
                Identity = identity ?? Identity("map"),
                PayloadHash = SHA256.HashData(payload),
                Offsets = new long[count],
                Lengths = new int[count],
            };

            var chunks = new byte[count][];
            long at = header.DataStart;
            for (int i = 0; i < count; i++)
            {
                int n = header.ChunkLength(i);
                var into = new byte[BrotliEncoder.GetMaxCompressedLength(n)];
                Assert.True(BrotliEncoder.TryCompress(payload.AsSpan(i * Chunk, n), into, out int written, 5, 16));
                chunks[i] = into.AsSpan(0, written).ToArray();
                header.Offsets[i] = at;
                header.Lengths[i] = written;
                at += written;
            }

            string path = Path.Combine(_folder, name);
            using (var file = new FileStream(path, FileMode.Create))
            {
                byte[] head = header.ToBytes();
                file.Write(head, 0, head.Length);
                foreach (byte[] chunk in chunks) file.Write(chunk, 0, chunk.Length);
            }
            return path;
        }

        static void Patch(string path, long at, Func<byte, byte> change)
        {
            byte[] bytes = File.ReadAllBytes(path);
            bytes[at] = change(bytes[at]);
            File.WriteAllBytes(path, bytes);
        }

        // ---- the header and its index

        [Fact]
        public void TheHeaderAndIndexReadBackAsWritten()
        {
            byte[] payload = Payload(10 * Chunk + 333, 1);
            string path = Write(payload, identity: Identity("swissalps 0.4.0"));

            NomapzHeader header = NomapzHeader.Read(path);

            Assert.Equal(1, header.Version);
            Assert.Equal(NomapzHeader.CodecBrotli, header.Codec);
            Assert.Equal(0, header.Flags);
            Assert.Equal(Chunk, header.ChunkSize);
            Assert.Equal(payload.Length, header.BundleLength);
            Assert.Equal(11, header.ChunkCount);
            Assert.Equal(333, header.ChunkLength(10));
            Assert.Equal(NomapzHeader.FixedBytes + 11 * NomapzHeader.IndexEntryBytes, header.Offsets[0]);
            Assert.Equal(new FileInfo(path).Length, header.DataEnd);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant(), header.PayloadHashHex);
            Assert.Contains("Brotli", header.Describe());
            Assert.Null(NomapzChunks.Check(path, 2, null, CancellationToken.None));
        }

        // The map registers under the identity in the header, the .nomap's hash, with no hash of the
        // .nomapz needed: so the LZ4HC test build and the released .nomapz have one name.
        [Fact]
        public void TheRegisteredNameComesFromTheHeadersIdentity()
        {
            byte[] nomap = Payload(5000, 2);                 // stands in for the .nomap of the build
            string nomapHash = Convert.ToHexString(SHA256.HashData(nomap)).ToLowerInvariant();
            string path = Write(Payload(3 * Chunk, 3), identity: SHA256.HashData(nomap));

            NomapzHeader header = NomapzHeader.Read(path);

            Assert.Equal(nomapHash, header.IdentityHex);
            Assert.True(BundleHash.IsSha256(header.IdentityHex));
            Assert.Equal(MapIdentity.MakePrefabName("swissalps", nomapHash),
                         MapIdentity.MakePrefabName("swissalps", header.IdentityHex));
            Assert.NotEqual(BundleHash.Resolve(path, null, out _), header.IdentityHex);
        }

        [Theory]
        [InlineData(4, 2, "version 2", true)]
        [InlineData(4, 0, "version is 0", false)]
        [InlineData(6, NomapzHeader.CodecZstd, "zstd", true)]
        [InlineData(6, 9, "codec 9", true)]
        [InlineData(7, 1, "flags", true)]
        public void AHeaderThisBuildCannotReadIsRefusedWithAReason(int at, byte value, string reason, bool newerReader)
        {
            string path = Write(Payload(2 * Chunk, 4));
            Patch(path, at, _ => value);

            var e = Assert.Throws<NomapzFormatException>(() => NomapzHeader.Read(path));
            Assert.Contains(reason, e.Message);
            Assert.Equal(newerReader, e.NeedsNewerReader);
        }

        [Theory]
        [InlineData(NomapzHeader.FixedBytes + 0)]                          // chunk 0's offset
        [InlineData(NomapzHeader.FixedBytes + 8)]                          // chunk 0's length
        [InlineData(NomapzHeader.FixedBytes + 3 * 12 + 8)]                 // chunk 3's length
        [InlineData(NomapzHeader.FixedBytes + 5 * 12 + 2)]                 // chunk 5's offset, high byte
        [InlineData(20)]                                                   // the chunk count
        [InlineData(13)]                                                   // the bundle length, second byte
        public void ADamagedIndexIsRefused(int at)
        {
            string path = Write(Payload(6 * Chunk + 10, 5));
            Patch(path, at, b => (byte)(b ^ 0x21));

            var e = Assert.Throws<NomapzFormatException>(() => NomapzHeader.Read(path));
            Assert.False(e.NeedsNewerReader);
        }

        [Fact]
        public void AFileCutShortIsRefused()
        {
            string path = Write(Payload(6 * Chunk, 6));
            byte[] bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, bytes.AsSpan(0, bytes.Length - 1).ToArray());

            var e = Assert.Throws<NomapzFormatException>(() => NomapzHeader.Read(path));
            Assert.Contains("cut short", e.Message);
        }

        [Fact]
        public void ANomapIsNotANomapz()
        {
            string path = Path.Combine(_folder, "map.nomap");
            File.WriteAllBytes(path, System.Text.Encoding.ASCII.GetBytes(new string('U', 200)));

            var e = Assert.Throws<NomapzFormatException>(() => NomapzHeader.Read(path));
            Assert.Contains("NOMZ", e.Message);
        }

        // What the plugin's hashing thread does with a .nomapz: the file hashed once and its chunks checked,
        // remembered only when they pass, so a damaged file is checked (and reported) at every start.
        [Fact]
        public void TheFileIsCheckedOnceAndRememberedOnlyWhenSound()
        {
            byte[] payload = Payload(8 * Chunk, 16);
            string path = Write(payload);
            var cache = new BundleHashCache();
            int checks = 0;
            string Check() { checks++; return NomapzChunks.Check(path, 2, null, CancellationToken.None); }

            string first = BundleHash.Resolve(path, cache, Check, out bool firstRemembered, out string firstProblem);
            string second = BundleHash.Resolve(path, cache, Check, out bool secondRemembered, out string secondProblem);

            Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), first);
            Assert.Equal(first, second);
            Assert.False(firstRemembered);
            Assert.True(secondRemembered);
            Assert.Null(firstProblem);
            Assert.Null(secondProblem);
            Assert.Equal(1, checks);

            // The same length and time, other bytes: the payload hash in the header no longer matches.
            DateTime written = File.GetLastWriteTimeUtc(path);
            Patch(path, 60, b => (byte)(b ^ 1));
            File.SetLastWriteTimeUtc(path, written);

            BundleHash.Resolve(path, cache, Check, out bool damagedRemembered, out string damaged);
            BundleHash.Resolve(path, cache, Check, out bool againRemembered, out string again);

            Assert.False(damagedRemembered);
            Assert.False(againRemembered);
            Assert.NotNull(damaged);
            Assert.NotNull(again);
            Assert.Equal(3, checks);
        }

        [Fact]
        public void ADamagedChunkFailsTheCheckAndTheRead()
        {
            byte[] payload = Payload(12 * Chunk, 7);
            string path = Write(payload);
            NomapzHeader header = NomapzHeader.Read(path);
            Patch(path, header.Offsets[4] + 3, b => (byte)(b ^ 0xff));

            Assert.NotNull(NomapzChunks.Check(path, 2, null, CancellationToken.None));

            using var stream = new NomapzStream(path, NomapzHeader.Read(path));
            var buffer = new byte[payload.Length];
            stream.ReadAt(0, buffer, 0, 4 * Chunk);        // the chunks before it are fine
            Assert.Equal(payload.AsSpan(0, 4 * Chunk).ToArray(), buffer.AsSpan(0, 4 * Chunk).ToArray());

            // A damaged Brotli stream either does not decode or decodes to other bytes; the check above
            // catches both, the read the first.
            try
            {
                stream.ReadAt(4L * Chunk, buffer, 0, Chunk);
                Assert.NotEqual(payload.AsSpan(4 * Chunk, Chunk).ToArray(), buffer.AsSpan(0, Chunk).ToArray());
            }
            catch (IOException e)
            {
                Assert.Contains("chunk 4", e.Message);
                Assert.Contains("chunk 4", stream.Failure);
            }
        }

        // ---- the stream

        [Theory]
        [InlineData(1)]
        [InlineData(100)]
        [InlineData(Chunk - 1)]
        [InlineData(Chunk)]
        [InlineData(Chunk + 1)]
        [InlineData(3 * Chunk + 7)]
        public void ReadsAcrossChunkBoundariesGiveTheOriginalBytes(int piece)
        {
            byte[] payload = Payload(9 * Chunk + 1234, 8);
            using var stream = new NomapzStream(Write(payload), NomapzHeader.Read(Path.Combine(_folder, "map-1.0.0.nomapz")));

            Assert.Equal(payload.Length, stream.Length);
            var copy = new MemoryStream();
            var buffer = new byte[piece];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0) copy.Write(buffer, 0, read);

            Assert.Equal(payload, copy.ToArray());
            Assert.Equal(0, stream.Read(buffer, 0, buffer.Length));
        }

        [Fact]
        public void SeeksAndReadsAnywhere()
        {
            byte[] payload = Payload(20 * Chunk + 99, 9);
            using var stream = new NomapzStream(Write(payload), NomapzHeader.Read(Path.Combine(_folder, "map-1.0.0.nomapz")));
            var random = new Random(9);

            for (int i = 0; i < 500; i++)
            {
                long at = random.Next(payload.Length);
                int want = random.Next(1, 3 * Chunk);
                var buffer = new byte[want];

                stream.Seek(at, SeekOrigin.Begin);
                int got = stream.Read(buffer, 0, want);

                Assert.Equal((int)Math.Min(want, payload.Length - at), got);
                Assert.Equal(payload.AsSpan((int)at, got).ToArray(), buffer.AsSpan(0, got).ToArray());
                Assert.Equal(at + got, stream.Position);
            }

            stream.Seek(-10, SeekOrigin.End);
            Assert.Equal(payload.Length - 10, stream.Position);
            Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
        }

        // Unity reads from its own loading threads; reads at once from many must each get their bytes.
        [Fact]
        public void ConcurrentReadsGiveTheOriginalBytes()
        {
            byte[] payload = Payload(64 * Chunk + 500, 10);
            string path = Write(payload);
            using var stream = new NomapzStream(path, NomapzHeader.Read(path), cacheBytes: 12 * Chunk, readAhead: 4, workers: 3);

            int bad = 0;
            Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, thread =>
            {
                var random = new Random(thread);
                for (int i = 0; i < 400; i++)
                {
                    long at = random.Next(payload.Length);
                    var buffer = new byte[random.Next(1, 2 * Chunk)];
                    int got = stream.ReadAt(at, buffer, 0, buffer.Length);
                    if (got != Math.Min(buffer.Length, payload.Length - at) ||
                        !payload.AsSpan((int)at, got).SequenceEqual(buffer.AsSpan(0, got)))
                        Interlocked.Increment(ref bad);
                }
            });

            // And whole forward passes at once, each through the shared position, taking turns.
            Parallel.For(0, 4, _ =>
            {
                var buffer = new byte[3000];
                for (long at = 0; at < payload.Length; at += buffer.Length)
                {
                    int got;
                    lock (stream)
                    {
                        stream.Position = at;
                        got = stream.Read(buffer, 0, buffer.Length);
                    }
                    if (!payload.AsSpan((int)at, got).SequenceEqual(buffer.AsSpan(0, got))) Interlocked.Increment(ref bad);
                }
            });

            Assert.Equal(0, bad);
            Assert.Null(stream.Failure);
        }

        /// <summary>
        /// The pattern that sized the cache: two forward runs far apart (the serialised file and its
        /// .resS), read in alternation. Each chunk is decoded once, and the cache stays within its
        /// capacity, read-ahead included. (Runs that meet, as the serialised file meets the .resS, decode the
        /// few chunks the first reads ahead into the second again; that is not what is tested here.)
        /// </summary>
        [Fact]
        public void TwoRegionsReadInAlternationAreEachDecodedOnce()
        {
            byte[] payload = Payload(400 * Chunk, 11);
            string path = Write(payload);
            using var stream = new NomapzStream(path, NomapzHeader.Read(path), cacheBytes: 32 * Chunk, readAhead: 8, workers: 4);

            long a = 0, b = 200L * Chunk;
            var buffer = new byte[1500];
            while (a < 150L * Chunk || b < payload.Length)
            {
                foreach (bool first in new[] { true, false })
                {
                    ref long at = ref (first ? ref a : ref b);
                    long end = first ? 150L * Chunk : payload.Length;
                    if (at >= end) continue;

                    int want = (int)Math.Min(buffer.Length, end - at);
                    stream.Seek(at, SeekOrigin.Begin);
                    int got = stream.Read(buffer, 0, want);
                    Assert.Equal(want, got);
                    Assert.True(payload.AsSpan((int)at, got).SequenceEqual(buffer.AsSpan(0, got)));
                    at += got;
                }
            }

            Assert.Equal(0, stream.Redecodes);
            // Every chunk read, and the 8 the first run reads ahead past its end, into the gap between the two.
            Assert.Equal(150 + 8 + 200, stream.Decodes);
            Assert.True(stream.PeakCachedChunks <= stream.CapacityChunks,
                        $"cache peaked at {stream.PeakCachedChunks} of {stream.CapacityChunks}");
        }

        // The same pattern with the cache too small for it still reads right; it only decodes again.
        [Fact]
        public void ACacheTooSmallForTheReadsStillGivesTheOriginalBytes()
        {
            byte[] payload = Payload(120 * Chunk, 12);
            string path = Write(payload);
            using var stream = new NomapzStream(path, NomapzHeader.Read(path), cacheBytes: Chunk, readAhead: 8, workers: 2);

            Assert.Equal(10, stream.CapacityChunks);       // never below the read-ahead and two more
            var buffer = new byte[2500];
            for (int round = 0; round < 3; round++)
            {
                for (long at = 0; at < 60L * Chunk; at += buffer.Length)
                {
                    foreach (long region in new[] { 0L, 60L * Chunk })
                    {
                        int got = stream.ReadAt(region + at, buffer, 0, buffer.Length);
                        Assert.True(payload.AsSpan((int)(region + at), got).SequenceEqual(buffer.AsSpan(0, got)));
                    }
                }
            }

            Assert.True(stream.Redecodes > 0);
            Assert.True(stream.CachedChunks <= stream.CapacityChunks);
        }

        [Fact]
        public void TrimLetsTheCacheGoAndReadsStillWork()
        {
            byte[] payload = Payload(30 * Chunk, 13);
            string path = Write(payload);
            using var stream = new NomapzStream(path, NomapzHeader.Read(path));

            var all = new byte[payload.Length];
            Assert.Equal(payload.Length, stream.ReadAt(0, all, 0, all.Length));
            stream.Trim();
            Assert.Equal(0, stream.CachedChunks);

            Assert.Equal(payload.Length, stream.ReadAt(0, all, 0, all.Length));
            Assert.Equal(payload, all);
        }

        [Fact]
        public void TheDefaultCacheIs64MBOf1MiBChunks()
        {
            byte[] payload = Payload(3 * Chunk, 14);
            string path = Write(payload);
            var header = NomapzHeader.Read(path);
            header.ChunkSize = NomapzHeader.DefaultChunkSize;   // as Map Forge writes them

            using var stream = new NomapzStream(path, header);
            Assert.Equal(64, stream.CapacityChunks);
        }

        [Fact]
        public void AClosedStreamRefusesReads()
        {
            byte[] payload = Payload(5 * Chunk, 15);
            string path = Write(payload);
            var stream = new NomapzStream(path, NomapzHeader.Read(path));
            var buffer = new byte[100];
            stream.ReadAt(0, buffer, 0, buffer.Length);
            stream.Dispose();
            stream.Dispose();

            Assert.Throws<ObjectDisposedException>(() => stream.ReadAt(0, buffer, 0, buffer.Length));
            Assert.False(stream.CanRead);
        }
    }
}
