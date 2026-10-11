using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace CustomMaps
{
    /// <summary>
    /// The header of a <c>.nomapz</c>: a map's AssetBundle, stored uncompressed and then cut into
    /// chunks that are each compressed on their own, so that any byte of the bundle can be read by
    /// decoding one chunk.
    ///
    /// Map Forge writes it from the LZ4HC <c>.nomap</c> of the same build; the plugin reads it through
    /// <c>AssetBundle.LoadFromStream</c> on a stream that decodes chunks as Unity asks for them. It is
    /// 55% smaller on disk than the <c>.nomap</c> (Swiss Alps 0.4.0: 671 MB against 1,483 MB) and loads
    /// as fast, because the load is bound by Unity's deserialisation, not by reading or decoding.
    ///
    /// Format version 1, little-endian, 88 bytes and then the index:
    ///
    ///     0   magic          4   "NOMZ"
    ///     4   version        2   1; a newer number is refused, never guessed at
    ///     6   codec          1   1 = Brotli; 2 = zstd, reserved
    ///     7   flags          1   0
    ///     8   chunk size     4   1 MiB from Map Forge; every chunk but the last is this long
    ///     12  bundle length  8   the uncompressed UnityFS bundle
    ///     20  chunk count    4   ceil(bundle length / chunk size)
    ///     24  identity      32   SHA-256 of the LZ4HC .nomap of the same build
    ///     56  payload hash  32   SHA-256 of the uncompressed bundle
    ///     88  index         12 x count: per chunk its offset in the file (8) and compressed length (4)
    ///         chunks             in index order, each a whole Brotli stream, nothing after the last
    ///
    /// <para><b>The identity is the <c>.nomap</c>'s hash</b>, not this file's. The map registers as
    /// <c>cm.&lt;mapId&gt;.&lt;first 8 hex of it&gt;</c> (<see cref="MapIdentity"/>), so the LZ4HC test build
    /// and the released <c>.nomapz</c> of one build register under the same name: missions pointed at
    /// the test build stay valid, and a player with either file joins a server with the other.</para>
    ///
    /// <para>Shared by copy between NOMapForge, which writes it, and the NOCustomMaps plugin, which
    /// reads it. A change here is a change to both.</para>
    /// </summary>
    public sealed class NomapzHeader
    {
        /// <summary>The file extension. Plugin 1.4 and later load it; older plugins ignore it.</summary>
        public const string Extension = ".nomapz";

        /// <summary>The format version this code reads and writes.</summary>
        public const ushort CurrentVersion = 1;

        public const byte CodecBrotli = 1;

        /// <summary>Reserved for a zstd fallback, should Brotli be missing where a map must load. Not
        /// written or read by anything yet.</summary>
        public const byte CodecZstd = 2;

        /// <summary>The chunk size Map Forge writes. Measured: 1 MiB chunks with a 64 MB decode cache
        /// load as fast as the LZ4HC bundle; 4 MiB chunks save 36 MB more and need twice the cache.</summary>
        public const int DefaultChunkSize = 1 << 20;

        /// <summary>The fixed part, before the index.</summary>
        public const int FixedBytes = 88;

        /// <summary>One index entry: offset (8) and compressed length (4).</summary>
        public const int IndexEntryBytes = 12;

        public const int MinChunkSize = 4 << 10;
        public const int MaxChunkSize = 64 << 20;

        /// <summary>A sanity bound on the index, far above any map (Swiss Alps 0.4.0: 2,033 chunks).</summary>
        public const int MaxChunkCount = 1 << 24;

        static readonly byte[] Magic = { (byte)'N', (byte)'O', (byte)'M', (byte)'Z' };

        public ushort Version = CurrentVersion;
        public byte Codec = CodecBrotli;
        public byte Flags;
        public int ChunkSize = DefaultChunkSize;
        public long BundleLength;
        public int ChunkCount;
        public byte[] Identity = new byte[32];
        public byte[] PayloadHash = new byte[32];

        /// <summary>Where each chunk starts in the file.</summary>
        public long[] Offsets = new long[0];

        /// <summary>Each chunk's compressed length.</summary>
        public int[] Lengths = new int[0];

        /// <summary>Where the first chunk starts: right after the index.</summary>
        public long DataStart => FixedBytes + (long)IndexEntryBytes * ChunkCount;

        /// <summary>Where the last chunk ends, which is the length of the whole file.</summary>
        public long DataEnd => ChunkCount == 0 ? DataStart : Offsets[ChunkCount - 1] + Lengths[ChunkCount - 1];

        /// <summary>The identity as 64 lowercase hex characters, the form the map's name is made from.</summary>
        public string IdentityHex => Hex(Identity);

        public string PayloadHashHex => Hex(PayloadHash);

        /// <summary>The uncompressed length of chunk <paramref name="chunk"/>: the chunk size, or what is
        /// left of the bundle for the last one.</summary>
        public int ChunkLength(int chunk) => (int)Math.Min(ChunkSize, BundleLength - (long)chunk * ChunkSize);

        /// <summary>The number of chunks a bundle of <paramref name="length"/> bytes is cut into.</summary>
        public static int CountFor(long length, int chunkSize) => (int)((length + chunkSize - 1) / chunkSize);

        /// <summary>A codec's name for the log: "Brotli", "zstd", or its number.</summary>
        public static string CodecName(byte codec)
        {
            switch (codec)
            {
                case CodecBrotli: return "Brotli";
                case CodecZstd: return "zstd";
                default: return "codec " + codec.ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>One line for a log: format, codec, chunks and sizes.</summary>
        public string Describe() =>
            $"nomapz format {Version}, {CodecName(Codec)}, {ChunkCount:N0} chunks of {ChunkSize / 1024:N0} KiB, " +
            $"{BundleLength:N0} B bundle in {DataEnd:N0} B";

        /// <summary>True when the file starts with the <c>.nomapz</c> magic, whatever else it holds.</summary>
        public static bool HasMagic(byte[] head)
        {
            if (head == null || head.Length < Magic.Length) return false;
            for (int i = 0; i < Magic.Length; i++)
                if (head[i] != Magic[i]) return false;
            return true;
        }

        /// <summary>The header and index as written to the file, <see cref="DataStart"/> bytes.</summary>
        public byte[] ToBytes()
        {
            CheckShape();

            var bytes = new byte[DataStart];
            Array.Copy(Magic, 0, bytes, 0, Magic.Length);
            PutU16(bytes, 4, Version);
            bytes[6] = Codec;
            bytes[7] = Flags;
            PutI32(bytes, 8, ChunkSize);
            PutI64(bytes, 12, BundleLength);
            PutI32(bytes, 20, ChunkCount);
            Array.Copy(Identity, 0, bytes, 24, 32);
            Array.Copy(PayloadHash, 0, bytes, 56, 32);

            for (int i = 0; i < ChunkCount; i++)
            {
                int at = FixedBytes + i * IndexEntryBytes;
                PutI64(bytes, at, Offsets[i]);
                PutI32(bytes, at + 8, Lengths[i]);
            }
            return bytes;
        }

        void CheckShape()
        {
            if (Identity == null || Identity.Length != 32) throw new InvalidOperationException("the identity must be 32 bytes");
            if (PayloadHash == null || PayloadHash.Length != 32) throw new InvalidOperationException("the payload hash must be 32 bytes");
            if (Offsets == null || Lengths == null || Offsets.Length != ChunkCount || Lengths.Length != ChunkCount)
                throw new InvalidOperationException("the index must have one entry per chunk");
        }

        /// <summary>Reads the header of the file at <paramref name="path"/>. Throws
        /// <see cref="NomapzFormatException"/> when it is not one this code can read.</summary>
        public static NomapzHeader Read(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                return Read(file, file.Length);
        }

        /// <summary>
        /// Reads and checks a header from the start of <paramref name="stream"/>.
        ///
        /// Everything that would make a read go wrong later is refused here, with a reason a player
        /// can act on: a file that is not a <c>.nomapz</c>, a newer format version or a codec this
        /// code does not decode (<see cref="NomapzFormatException.NeedsNewerReader"/>), and an index that
        /// does not describe <paramref name="fileLength"/> bytes of chunks laid end to end after it,
        /// which is what a download cut short or a damaged index looks like.
        /// </summary>
        public static NomapzHeader Read(Stream stream, long fileLength)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));

            var head = new byte[FixedBytes];
            int got = ReadUpTo(stream, head, head.Length);
            if (got < Magic.Length || !HasMagic(head))
                throw new NomapzFormatException("not a .nomapz file: it does not start with NOMZ");
            if (got < FixedBytes)
                throw new NomapzFormatException($"the file ends inside its header ({got} of {FixedBytes} bytes): damaged or cut short");

            var h = new NomapzHeader
            {
                Version = GetU16(head, 4),
                Codec = head[6],
                Flags = head[7],
                ChunkSize = GetI32(head, 8),
                BundleLength = GetI64(head, 12),
                ChunkCount = GetI32(head, 20),
            };
            Array.Copy(head, 24, h.Identity, 0, 32);
            Array.Copy(head, 56, h.PayloadHash, 0, 32);

            if (h.Version > CurrentVersion)
                throw new NomapzFormatException(
                    $"it is .nomapz format version {h.Version}, and this build reads version {CurrentVersion} only. " +
                    "A newer NOCustomMaps reads it.", needsNewerReader: true);
            if (h.Version == 0)
                throw new NomapzFormatException("its format version is 0, which no .nomapz has: damaged");
            if (h.Codec != CodecBrotli)
                throw new NomapzFormatException(
                    $"its chunks are compressed with {CodecName(h.Codec)}, which this build does not decode. " +
                    "A newer NOCustomMaps may.", needsNewerReader: true);
            if (h.Flags != 0)
                throw new NomapzFormatException(
                    $"it sets flags 0x{h.Flags:x2}, which format version {CurrentVersion} does not have. A newer NOCustomMaps may read it.",
                    needsNewerReader: true);
            if (h.ChunkSize < MinChunkSize || h.ChunkSize > MaxChunkSize)
                throw new NomapzFormatException($"its chunk size, {h.ChunkSize} bytes, is out of range: damaged");
            if (h.BundleLength <= 0)
                throw new NomapzFormatException($"its bundle length, {h.BundleLength}, is not positive: damaged");
            if (h.ChunkCount <= 0 || h.ChunkCount > MaxChunkCount || h.ChunkCount != CountFor(h.BundleLength, h.ChunkSize))
                throw new NomapzFormatException(
                    $"it has {h.ChunkCount} chunks, where a bundle of {h.BundleLength:N0} B in chunks of {h.ChunkSize:N0} B " +
                    "has " + CountFor(h.BundleLength, h.ChunkSize).ToString(CultureInfo.InvariantCulture) + ": damaged");

            var index = new byte[(long)h.ChunkCount * IndexEntryBytes];
            got = ReadUpTo(stream, index, index.Length);
            if (got < index.Length)
                throw new NomapzFormatException("the file ends inside its chunk index: damaged or cut short");

            h.Offsets = new long[h.ChunkCount];
            h.Lengths = new int[h.ChunkCount];
            long expected = h.DataStart;
            for (int i = 0; i < h.ChunkCount; i++)
            {
                long offset = GetI64(index, i * IndexEntryBytes);
                int length = GetI32(index, i * IndexEntryBytes + 8);
                if (offset != expected || length <= 0)
                    throw new NomapzFormatException(
                        $"its chunk index is damaged: chunk {i} is at {offset} with {length} bytes, where it should start at {expected}");
                h.Offsets[i] = offset;
                h.Lengths[i] = length;
                expected = offset + length;
            }

            if (fileLength >= 0 && fileLength != expected)
                throw new NomapzFormatException(fileLength < expected
                    ? $"the file is {fileLength:N0} B and its chunks need {expected:N0} B: cut short (an unfinished download?)"
                    : $"the file is {fileLength:N0} B and its chunks end at {expected:N0} B: damaged");

            return h;
        }

        public static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
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

        static void PutU16(byte[] b, int at, ushort v) { b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); }

        static void PutI32(byte[] b, int at, int v)
        {
            for (int i = 0; i < 4; i++) b[at + i] = (byte)(v >> (8 * i));
        }

        static void PutI64(byte[] b, int at, long v)
        {
            for (int i = 0; i < 8; i++) b[at + i] = (byte)(v >> (8 * i));
        }

        static ushort GetU16(byte[] b, int at) => (ushort)(b[at] | (b[at + 1] << 8));

        static int GetI32(byte[] b, int at) => b[at] | (b[at + 1] << 8) | (b[at + 2] << 16) | (b[at + 3] << 24);

        static long GetI64(byte[] b, int at)
        {
            long v = 0;
            for (int i = 7; i >= 0; i--) v = (v << 8) | b[at + i];
            return v;
        }
    }

    /// <summary>A <c>.nomapz</c> that cannot be read, and why, in words for a log.</summary>
    public sealed class NomapzFormatException : IOException
    {
        /// <summary>True when the file may be fine and this code is too old for it: a newer format
        /// version, codec or flag.</summary>
        public readonly bool NeedsNewerReader;

        public NomapzFormatException(string message, bool needsNewerReader = false) : base(message)
        {
            NeedsNewerReader = needsNewerReader;
        }
    }

    /// <summary>Decoding one chunk, and checking a whole file.</summary>
    public static class NomapzChunks
    {
        /// <summary>
        /// Decodes chunk <paramref name="chunk"/>, whose compressed bytes are the first
        /// <paramref name="sourceLength"/> of <paramref name="source"/>, into the start of
        /// <paramref name="destination"/>. Throws <see cref="InvalidDataException"/> unless it decodes to
        /// exactly the chunk's length.
        ///
        /// <c>BrotliDecoder.TryDecompress</c> decodes in one call into the caller's buffer and allocates
        /// nothing, where a <c>BrotliStream</c> per chunk cost the prototype 38 to 39 garbage collections
        /// per load. It is in .NET Standard 2.1, and the game's Mono has it natively.
        /// </summary>
        public static void Decode(NomapzHeader header, int chunk, byte[] source, int sourceLength, byte[] destination)
        {
            int length = header.ChunkLength(chunk);
            if (destination.Length < length) throw new ArgumentException("the destination is shorter than the chunk", nameof(destination));

            int written;
            bool ok = BrotliDecoder.TryDecompress(new ReadOnlySpan<byte>(source, 0, sourceLength),
                                                  new Span<byte>(destination, 0, length), out written);
            if (!ok || written != length)
                throw new InvalidDataException(
                    $"chunk {chunk} does not decode: {(ok ? written.ToString(CultureInfo.InvariantCulture) + " of " + length + " bytes" : "not a whole Brotli stream of its length")}");
        }

        /// <summary>
        /// Decodes every chunk of the file at <paramref name="path"/> and checks the bundle they make
        /// against the payload hash in its header. Null when it matches; otherwise what is wrong, in
        /// words for a log. Throws only <see cref="OperationCanceledException"/>.
        ///
        /// Chunks are decoded on <paramref name="threads"/> threads of their own, below normal
        /// priority, and hashed in order on the calling thread. <paramref name="progress"/>, if
        /// given, hears the chunks hashed so far and the chunk count, from the calling thread.
        /// </summary>
        public static string Check(string path, int threads, Action<int, int> progress, CancellationToken cancel)
        {
            NomapzHeader header;
            try { header = NomapzHeader.Read(path); }
            catch (Exception e) when (e is InvalidDataException || e is IOException || e is UnauthorizedAccessException)
            {
                return e.Message;
            }
            return Check(path, header, threads, progress, cancel);
        }

        /// <summary><see cref="Check(string,int,Action{int,int},CancellationToken)"/> with the header
        /// already read.</summary>
        public static string Check(string path, NomapzHeader header, int threads, Action<int, int> progress, CancellationToken cancel)
        {
            int count = header.ChunkCount;
            int workers = Math.Max(1, Math.Min(threads, count));
            int window = workers * 4;

            var decoded = new byte[count][];
            var gate = new object();
            int next = 0, hashed = 0;
            string problem = null;
            var pool = new System.Collections.Generic.Stack<byte[]>();

            void Work()
            {
                try
                {
                    using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess))
                    {
                        var source = new byte[64 << 10];
                        while (true)
                        {
                            int chunk;
                            byte[] into;
                            lock (gate)
                            {
                                while (problem == null && !cancel.IsCancellationRequested && next < count && next >= hashed + window)
                                    Monitor.Wait(gate);
                                if (problem != null || cancel.IsCancellationRequested || next >= count) return;
                                chunk = next++;
                                into = pool.Count > 0 ? pool.Pop() : new byte[header.ChunkSize];
                            }

                            int length = header.Lengths[chunk];
                            if (source.Length < length) source = new byte[length];
                            file.Position = header.Offsets[chunk];
                            if (ReadUpTo(file, source, length) != length)
                                throw new InvalidDataException($"the file ends inside chunk {chunk}");
                            Decode(header, chunk, source, length, into);

                            lock (gate)
                            {
                                decoded[chunk] = into;
                                Monitor.PulseAll(gate);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    lock (gate)
                    {
                        if (problem == null) problem = e.Message;
                        Monitor.PulseAll(gate);
                    }
                }
            }

            var running = new Thread[workers];
            for (int i = 0; i < workers; i++)
            {
                running[i] = new Thread(Work) { IsBackground = true, Name = "nomapz check " + i, Priority = ThreadPriority.BelowNormal };
                running[i].Start();
            }

            try
            {
                using (SHA256 sha = SHA256.Create())
                {
                    for (int chunk = 0; chunk < count; chunk++)
                    {
                        byte[] data;
                        lock (gate)
                        {
                            while (decoded[chunk] == null && problem == null && !cancel.IsCancellationRequested)
                                Monitor.Wait(gate, 200);
                            if (problem != null) return problem;
                            cancel.ThrowIfCancellationRequested();
                            data = decoded[chunk];
                            decoded[chunk] = null;
                        }

                        sha.TransformBlock(data, 0, header.ChunkLength(chunk), null, 0);

                        lock (gate)
                        {
                            pool.Push(data);
                            hashed = chunk + 1;
                            Monitor.PulseAll(gate);
                        }
                        progress?.Invoke(chunk + 1, count);
                    }

                    sha.TransformFinalBlock(new byte[0], 0, 0);
                    string got = NomapzHeader.Hex(sha.Hash);
                    return got == header.PayloadHashHex
                        ? null
                        : $"its chunks decode to a bundle whose SHA-256 is {got.Substring(0, 12)}…, where the header says " +
                          $"{header.PayloadHashHex.Substring(0, 12)}…: damaged";
                }
            }
            finally
            {
                lock (gate)
                {
                    if (problem == null) problem = "stopped";
                    Monitor.PulseAll(gate);
                }
                foreach (Thread t in running) t.Join();
            }
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
    }
}
