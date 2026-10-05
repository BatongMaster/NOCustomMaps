using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace CustomMaps
{
    /// <summary>
    /// A bundle's identity: the SHA-256 of the whole file, worked out once per build of a map and
    /// remembered between game starts.
    ///
    /// The hash names the map in multiplayer (<c>cm.&lt;id&gt;.&lt;hash8&gt;</c>, see
    /// <see cref="MapIdentity"/>), so it has to be the hash of every byte: anything cheaper would let
    /// two builds of a map pass for one, and a server and a client would disagree about where the
    /// ground is. It is also slow in the game. Unity's Mono hashes with the managed
    /// <c>SHA256Managed</c> at about 127 MB/s, so Swiss Alps 0.3.0 (1.36 GB) took 10.2 s, and until
    /// 2026-10-04 that was spent on the main thread at every start of the game.
    ///
    /// <see cref="Resolve"/> therefore keeps each result in a <see cref="BundleHashCache"/> and
    /// trusts it again only for the same file, unchanged: the same full path, length and last-write
    /// time, and the same <see cref="Fingerprint"/>. Anything else, and anything it cannot read,
    /// means the whole file is hashed again. The cache never stands in for a hash it has not seen
    /// computed from that file.
    ///
    /// Kept free of game and Unity types so the tests run it against real files.
    /// </summary>
    internal static class BundleHash
    {
        /// <summary>Bytes taken from each end of a file for its <see cref="Fingerprint"/>.</summary>
        public const int FingerprintSpan = 1 << 20;

        /// <summary>
        /// Bytes read at a time while hashing a whole file. <c>HashAlgorithm.ComputeHash(Stream)</c>
        /// reads 4 KiB at a time; a megabyte is a few hundred thousand fewer calls through the file
        /// stream for a map the size of Swiss Alps, for the same hash.
        /// </summary>
        const int ReadSize = 1 << 20;

        /// <summary>The SHA-256 of everything from the stream's position to its end, as 64 lowercase
        /// hex characters: the form <see cref="MapIdentity"/> takes its eight from.</summary>
        public static string Sha256(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            using (SHA256 sha = SHA256.Create())
            {
                var buffer = new byte[ReadSize];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    sha.TransformBlock(buffer, 0, read, null, 0);

                sha.TransformFinalBlock(buffer, 0, 0);
                return Hex(sha.Hash);
            }
        }

        /// <summary>
        /// A SHA-256 of a file's first and last <see cref="FingerprintSpan"/> bytes and its length:
        /// 35 ms for Swiss Alps under Unity's Mono, in a process of its own (so with the hash code
        /// compiled on first use), where the whole file takes 10 s.
        ///
        /// It is not an identity, only a check that a remembered hash still belongs to the file. Path,
        /// length and last-write time already change with every new build Map Forge writes; this
        /// catches a different file that kept all three (a copy that preserves times over one of the
        /// same size, say), as long as the two differ within a megabyte of either end. A Unity bundle
        /// keeps its header and block table there, which change whenever anything in it changes
        /// size. A file of two spans or less is covered whole.
        /// </summary>
        public static string Fingerprint(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            long length = stream.Length;
            using (SHA256 sha = SHA256.Create())
            {
                var buffer = new byte[FingerprintSpan];

                stream.Position = 0;
                int head = ReadUpTo(stream, buffer, (int)Math.Min(length, FingerprintSpan));
                sha.TransformBlock(buffer, 0, head, null, 0);

                // The tail starts where the head ended for a short file, so no byte is taken twice.
                long tailStart = Math.Max(FingerprintSpan, length - FingerprintSpan);
                if (tailStart < length)
                {
                    stream.Position = tailStart;
                    int tail = ReadUpTo(stream, buffer, (int)(length - tailStart));
                    sha.TransformBlock(buffer, 0, tail, null, 0);
                }

                var size = new byte[8];
                for (int i = 0; i < size.Length; i++) size[i] = (byte)(length >> (8 * i));
                sha.TransformFinalBlock(size, 0, size.Length);

                return Hex(sha.Hash);
            }
        }

        /// <summary>
        /// The full SHA-256 of the file at <paramref name="path"/>: from <paramref name="cache"/> when
        /// it remembers this very file, otherwise computed and handed to the cache.
        /// <paramref name="remembered"/> says which. A null cache always computes.
        ///
        /// Throws what reading the file throws; nothing is remembered then.
        /// </summary>
        public static string Resolve(string path, BundleHashCache cache, out bool remembered)
        {
            remembered = false;
            string full = Path.GetFullPath(path);
            long written = File.GetLastWriteTimeUtc(full).Ticks;

            // FileShare.Read as File.OpenRead has it: the game holds the bundle open for reading.
            using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var stamp = new BundleStamp(full, stream.Length, written, Fingerprint(stream));

                string known = cache?.Lookup(stamp);
                if (known != null)
                {
                    remembered = true;
                    return known;
                }

                stream.Position = 0;
                string hash = Sha256(stream);

                // Remembered only if the file did not change while it was read: otherwise the hash
                // could be of a file that no longer exists, kept under the stamp of one that does.
                if (stream.Length == stamp.Length && File.GetLastWriteTimeUtc(full).Ticks == stamp.WrittenUtcTicks)
                    cache?.Store(stamp, hash);

                return hash;
            }
        }

        /// <summary>True for 64 lowercase hex characters, the only form a hash is kept in.</summary>
        public static bool IsSha256(string text)
        {
            if (text == null || text.Length != 64) return false;
            foreach (char c in text)
                if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            return true;
        }

        static int ReadUpTo(Stream stream, byte[] buffer, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = stream.Read(buffer, total, count - total);
                if (read <= 0) break;
                total += read;
            }
            return total;
        }

        static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    /// <summary>What a bundle file looked like when it was hashed: enough to tell, without reading
    /// all of it, that it is still the same file.</summary>
    internal sealed class BundleStamp : IEquatable<BundleStamp>
    {
        /// <summary>The full path, compared exactly: a different spelling is a different file, which
        /// costs one hash and no more.</summary>
        public readonly string Path;
        public readonly long Length;
        public readonly long WrittenUtcTicks;
        /// <summary><see cref="BundleHash.Fingerprint"/>.</summary>
        public readonly string Fingerprint;

        public BundleStamp(string path, long length, long writtenUtcTicks, string fingerprint)
        {
            Path = path;
            Length = length;
            WrittenUtcTicks = writtenUtcTicks;
            Fingerprint = fingerprint;
        }

        public bool Equals(BundleStamp other) =>
            other != null &&
            string.Equals(Path, other.Path, StringComparison.Ordinal) &&
            Length == other.Length &&
            WrittenUtcTicks == other.WrittenUtcTicks &&
            string.Equals(Fingerprint, other.Fingerprint, StringComparison.Ordinal);

        public override bool Equals(object obj) => Equals(obj as BundleStamp);

        public override int GetHashCode() => (Path ?? "").GetHashCode() ^ Length.GetHashCode();
    }

    /// <summary>
    /// The remembered hashes, one line per bundle, in a text file under BepInEx's <c>cache</c> folder.
    ///
    /// Written to be thrown away. A missing file, a first line that is not <see cref="Header"/>, and
    /// any line that does not read back exactly (a field missing, a number or hash malformed) are all
    /// the same thing: no entry, so the bundle is hashed again. Deleting the file is always safe.
    /// Not shared between threads: the plugin builds, reads and saves it on its hashing thread.
    /// </summary>
    internal sealed class BundleHashCache
    {
        /// <summary>The first line. A file written by another version of this format starts with
        /// something else and is ignored whole.</summary>
        public const string Header = "NOCustomMaps bundle hashes 1";

        readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);

        /// <summary>Whether anything was stored or pruned since the cache was read or last saved.</summary>
        public bool Changed { get; private set; }

        public int Count => _entries.Count;

        sealed class Entry
        {
            public readonly BundleStamp Stamp;
            public readonly string Hash;

            public Entry(BundleStamp stamp, string hash)
            {
                Stamp = stamp;
                Hash = hash;
            }
        }

        /// <summary>The remembered hash of the file <paramref name="stamp"/> describes, or null unless
        /// every part of the stamp matches.</summary>
        public string Lookup(BundleStamp stamp)
        {
            if (stamp?.Path == null) return null;
            return _entries.TryGetValue(stamp.Path, out Entry entry) && entry.Stamp.Equals(stamp) ? entry.Hash : null;
        }

        /// <summary>Remembers <paramref name="hash"/> for the file <paramref name="stamp"/> describes,
        /// replacing whatever was remembered at that path. Anything that would not read back (a hash
        /// or fingerprint not in <see cref="BundleHash.IsSha256"/> form, a path with a line break in
        /// it) is not stored.</summary>
        public void Store(BundleStamp stamp, string hash)
        {
            if (stamp?.Path == null || stamp.Path.Length == 0) return;
            if (!BundleHash.IsSha256(hash) || !BundleHash.IsSha256(stamp.Fingerprint)) return;
            if (stamp.Length < 0 || stamp.WrittenUtcTicks < 0) return;
            if (stamp.Path.IndexOf('\n') >= 0 || stamp.Path.IndexOf('\r') >= 0) return;

            if (_entries.TryGetValue(stamp.Path, out Entry old) && old.Stamp.Equals(stamp) && old.Hash == hash) return;

            _entries[stamp.Path] = new Entry(stamp, hash);
            Changed = true;
        }

        /// <summary>Forgets every bundle <paramref name="stillThere"/> says is gone, so the file does
        /// not keep a line for each build of a map ever installed.</summary>
        public void Prune(Func<string, bool> stillThere)
        {
            if (stillThere == null) throw new ArgumentNullException(nameof(stillThere));

            var gone = new List<string>();
            foreach (string path in _entries.Keys)
            {
                bool there;
                try { there = stillThere(path); }
                catch (Exception) { there = true; }   // cannot tell: keep it, a stale line costs nothing
                if (!there) gone.Add(path);
            }

            foreach (string path in gone) _entries.Remove(path);
            if (gone.Count > 0) Changed = true;
        }

        /// <summary>
        /// The cache as written to its file: <see cref="Header"/>, then per bundle
        /// <c>hash, length, last-write ticks, fingerprint, path</c>, tab-separated, ordered by path.
        /// The path goes last, so it may hold anything but a line break.
        /// </summary>
        public string Serialise()
        {
            var paths = new List<string>(_entries.Keys);
            paths.Sort(StringComparer.Ordinal);

            var sb = new StringBuilder(Header).Append('\n');
            foreach (string path in paths)
            {
                Entry e = _entries[path];
                sb.Append(e.Hash).Append('\t')
                  .Append(e.Stamp.Length.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(e.Stamp.WrittenUtcTicks.ToString(CultureInfo.InvariantCulture)).Append('\t')
                  .Append(e.Stamp.Fingerprint).Append('\t')
                  .Append(path).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Reads what <see cref="Serialise"/> wrote, keeping every line that reads back whole
        /// and dropping the rest. Never throws.</summary>
        public static BundleHashCache Parse(string text)
        {
            var cache = new BundleHashCache();
            if (string.IsNullOrEmpty(text)) return cache;

            string[] lines = text.Split('\n');
            if (lines[0].TrimEnd('\r') != Header) return cache;

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (line.Length == 0) continue;

                string[] f = line.Split(new[] { '\t' }, 5);
                if (f.Length != 5 || f[4].Length == 0) continue;
                if (!BundleHash.IsSha256(f[0]) || !BundleHash.IsSha256(f[3])) continue;
                if (!long.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out long length)) continue;
                if (!long.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)) continue;

                cache._entries[f[4]] = new Entry(new BundleStamp(f[4], length, ticks, f[3]), f[0]);
            }

            return cache;
        }

        /// <summary>The cache in <paramref name="file"/>; empty if it is missing or cannot be read.</summary>
        public static BundleHashCache Load(string file)
        {
            try
            {
                return File.Exists(file) ? Parse(File.ReadAllText(file)) : new BundleHashCache();
            }
            catch (Exception)
            {
                return new BundleHashCache();
            }
        }

        /// <summary>The end of the name of the file <see cref="Save"/> writes before it takes the
        /// cache's place: <c>&lt;cache file&gt;.&lt;32 hex digits&gt;.tmp</c>.</summary>
        public const string TempSuffix = ".tmp";

        /// <summary>How old such a file must be before a save clears it away: far longer than any
        /// save takes, so another game's save under way is never touched.</summary>
        public static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(1);

        /// <summary>
        /// Writes the cache to <paramref name="file"/>, creating its folder. Returns null, or why it
        /// could not; a cache that is not saved only means the next start hashes again.
        ///
        /// Written beside the file first and then put in its place with <c>File.Replace</c>, one step
        /// on NTFS, so a game started at the same moment (a client and a dedicated server on one
        /// machine, say) reads the old file or the new one, never half of one. The first save, and one
        /// where replacing fails, deletes the old file and moves the new one in instead; a start in
        /// between finds no file at all and hashes again, which is all a missing cache ever costs.
        /// A game stopped in the middle of a save leaves its temporary file behind, and the next save
        /// clears those away once they are <see cref="StaleTempAge"/> old.
        /// </summary>
        public string Save(string file)
        {
            string temp = null;
            try
            {
                string full = System.IO.Path.GetFullPath(file);
                string folder = System.IO.Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
                ClearStaleTemps(full, DateTime.UtcNow);

                temp = full + "." + Guid.NewGuid().ToString("N") + TempSuffix;
                File.WriteAllText(temp, Serialise(), new UTF8Encoding(false));

                if (!TryReplace(temp, full))
                {
                    if (File.Exists(full)) File.Delete(full);
                    File.Move(temp, full);
                }

                Changed = false;
                return null;
            }
            catch (Exception e)
            {
                return e.Message;
            }
            finally
            {
                try
                {
                    if (temp != null && File.Exists(temp)) File.Delete(temp);
                }
                catch (Exception)
                {
                    // Left behind for the next save to clear (ClearStaleTemps).
                }
            }
        }

        /// <summary>Puts <paramref name="temp"/> in the place of <paramref name="file"/> in one step.
        /// False, with both left for the caller to deal with, if there is no file to replace yet or
        /// the file system will not do it.</summary>
        static bool TryReplace(string temp, string file)
        {
            if (!File.Exists(file)) return false;
            try
            {
                File.Replace(temp, file, null);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Deletes the temporary files of earlier saves of <paramref name="file"/> (full path) that are
        /// <see cref="StaleTempAge"/> old at <paramref name="nowUtc"/>. Only names <see cref="Save"/>
        /// makes are touched, and a file that cannot be deleted is left; nothing here throws.
        /// </summary>
        internal static void ClearStaleTemps(string file, DateTime nowUtc)
        {
            try
            {
                string folder = System.IO.Path.GetDirectoryName(file);
                string name = System.IO.Path.GetFileName(file);
                if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(name) || !Directory.Exists(folder)) return;

                foreach (string temp in Directory.GetFiles(folder, name + ".*" + TempSuffix))
                {
                    if (!IsTempOf(System.IO.Path.GetFileName(temp), name)) continue;
                    try
                    {
                        if (nowUtc - File.GetLastWriteTimeUtc(temp) >= StaleTempAge) File.Delete(temp);
                    }
                    catch (Exception)
                    {
                        // In use or gone already: the next save looks again.
                    }
                }
            }
            catch (Exception)
            {
                // The folder cannot be listed: nothing to clear that this save could reach either.
            }
        }

        /// <summary>True for <c>&lt;cacheName&gt;.&lt;32 hex digits&gt;.tmp</c>, exactly. The pattern
        /// given to <c>Directory.GetFiles</c> is looser than that on Windows (a three-letter extension
        /// there also matches longer ones).</summary>
        static bool IsTempOf(string candidate, string cacheName)
        {
            int guidLength = 32;
            if (candidate.Length != cacheName.Length + 1 + guidLength + TempSuffix.Length) return false;
            if (!candidate.StartsWith(cacheName + ".", StringComparison.OrdinalIgnoreCase)) return false;
            if (!candidate.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase)) return false;

            for (int i = cacheName.Length + 1; i < cacheName.Length + 1 + guidLength; i++)
            {
                char c = char.ToLowerInvariant(candidate[i]);
                if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) return false;
            }
            return true;
        }
    }
}
