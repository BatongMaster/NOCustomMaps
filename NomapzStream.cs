using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace CustomMaps
{
    /// <summary>
    /// A <c>.nomapz</c> (<see cref="NomapzHeader"/>) read as the uncompressed bundle it holds: a read-only,
    /// seekable stream for <c>AssetBundle.LoadFromStream</c>, which decodes the chunks a read touches and
    /// keeps them in a cache.
    ///
    /// <para><b>Kept open for the bundle's lifetime.</b> Unity reads through it whenever an asset not yet
    /// read is loaded, from its own loading threads, until the bundle is unloaded. It is disposed after
    /// that, never before.</para>
    ///
    /// <para><b>The cache is 64 MB and must not be smaller.</b> Unity reads two regions of the bundle about
    /// a gigabyte apart in alternation, the serialised file and its <c>.resS</c> resources, each running
    /// forward. With 16 MB the read-ahead of one evicted chunks the other still needed: Swiss Alps 0.4.0 was
    /// ready 1.2 to 1.5 s later, with 572 to 649 chunks decoded twice, for 41 MB less working set
    /// (measured in a Unity 2022.3.62f2 player on the game's Mono, 2026-10-06).</para>
    ///
    /// <para><b>Read-ahead.</b> A read that continues a forward run (its chunk, or the one before it, is
    /// among the last few touched) has the next <see cref="DefaultReadAhead"/> chunks decoded on
    /// <see cref="DefaultWorkers"/> worker threads, so Unity rarely waits for a decode. A chunk decoded
    /// ahead and not yet read is not evicted to make room for more read-ahead, and the chunks a read
    /// is copying from are not evicted at all.</para>
    ///
    /// <para><b>Thread-safe.</b> <see cref="ReadAt"/> may be called from any number of threads at once;
    /// <see cref="Read"/> and <see cref="Seek"/> share one position and take turns on it. Decoding goes
    /// through <c>BrotliDecoder.TryDecompress</c> into buffers kept in a pool (<see cref="NomapzChunks.Decode"/>),
    /// so a load allocates almost nothing for the garbage collector.</para>
    ///
    /// Free of Unity and game types, so the tests run it against real files.
    /// </summary>
    internal sealed class NomapzStream : Stream
    {
        public const int DefaultCacheBytes = 64 << 20;
        public const int DefaultReadAhead = 8;
        public const int DefaultWorkers = 4;

        readonly string _path;
        readonly NomapzHeader _header;
        readonly int _capacity, _readAhead;
        readonly Thread[] _workers;

        // Everything below is guarded by _gate, except the position (_positionGate) and the file
        // handles each thread takes from _files for one decode.
        readonly object _gate = new object();
        readonly object _positionGate = new object();
        long _position;

        sealed class Slot
        {
            public int Chunk;
            public byte[] Data;
            public bool Ready;
            public string Error;
            /// <summary>Read at least once. A chunk decoded ahead and not yet read is not evicted to make
            /// room for more read-ahead.</summary>
            public bool Used;
            /// <summary>Reads copying from it now; it is not evicted while any are.</summary>
            public int Readers;
            public LinkedListNode<Slot> Node;
        }

        readonly Dictionary<int, Slot> _slots = new Dictionary<int, Slot>();
        readonly LinkedList<Slot> _lru = new LinkedList<Slot>();
        readonly Stack<byte[]> _pool = new Stack<byte[]>();
        readonly Stack<FileStream> _files = new Stack<FileStream>();
        readonly Queue<Slot> _work = new Queue<Slot>();
        readonly bool[] _decodedOnce;
        bool _closing, _disposed;

        // Unity reads the serialised file and the .resS as two forward runs, interleaved, so a read
        // continues a run when its chunk, or the one before it, is among the last few distinct chunks
        // touched.
        readonly int[] _recent = { -2, -2, -2, -2 };
        int _recentNext;
        int _lastChunk = -2;

        long _decodes, _redecodes, _aheadDecodes, _hits, _waits, _evictions;
        int _peakCached;
        string _failure;

        /// <param name="header">Read from <paramref name="path"/> already (<see cref="NomapzHeader.Read(string)"/>).</param>
        public NomapzStream(string path, NomapzHeader header,
                            int cacheBytes = DefaultCacheBytes, int readAhead = DefaultReadAhead, int workers = DefaultWorkers)
        {
            _path = path ?? throw new ArgumentNullException(nameof(path));
            _header = header ?? throw new ArgumentNullException(nameof(header));
            _readAhead = workers > 0 ? Math.Max(0, readAhead) : 0;
            _capacity = (int)Math.Max(2 + _readAhead, (long)cacheBytes / header.ChunkSize);
            _decodedOnce = new bool[header.ChunkCount];

            // Opened once here, so a file that cannot be read fails now and not inside Unity.
            _files.Push(OpenFile());

            _workers = new Thread[Math.Max(0, workers)];
            for (int i = 0; i < _workers.Length; i++)
            {
                _workers[i] = new Thread(Work)
                {
                    IsBackground = true,
                    Name = "Custom Maps nomapz decode " + i,
                    Priority = ThreadPriority.BelowNormal,
                };
                _workers[i].Start();
            }
        }

        public NomapzHeader Header => _header;

        /// <summary>How many chunks the cache holds before it evicts.</summary>
        public int CapacityChunks => _capacity;

        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override long Length => _header.BundleLength;

        public override long Position
        {
            get { lock (_positionGate) return _position; }
            set { Seek(value, SeekOrigin.Begin); }
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (_positionGate)
            {
                long target = origin == SeekOrigin.Begin ? offset
                            : origin == SeekOrigin.Current ? _position + offset
                            : _header.BundleLength + offset;
                if (target < 0) throw new IOException("seek before the start of the bundle");
                _position = target;
                return target;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_positionGate)
            {
                int read = ReadAt(_position, buffer, offset, count);
                _position += read;
                return read;
            }
        }

        /// <summary>
        /// Copies up to <paramref name="count"/> bytes of the bundle from <paramref name="position"/>, and
        /// returns how many: fewer only at the end of the bundle. Safe from any thread, and leaves
        /// <see cref="Position"/> alone. Throws <see cref="IOException"/> when a chunk does not decode.
        /// </summary>
        public int ReadAt(long position, byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException(nameof(count));
            if (position < 0) throw new ArgumentOutOfRangeException(nameof(position));

            int total = 0;
            while (count > 0 && position < _header.BundleLength)
            {
                int chunk = (int)(position / _header.ChunkSize);
                int within = (int)(position - (long)chunk * _header.ChunkSize);
                int n = Math.Min(count, _header.ChunkLength(chunk) - within);

                Slot slot = Acquire(chunk);
                try
                {
                    Buffer.BlockCopy(slot.Data, within, buffer, offset, n);
                }
                finally
                {
                    lock (_gate) slot.Readers--;
                }

                offset += n;
                count -= n;
                total += n;
                position += n;
            }
            return total;
        }

        /// <summary>The chunk, decoded: from the cache, from a worker already decoding it, or decoded
        /// here. Counted as being read (<see cref="Slot.Readers"/>) until the caller lets it go.</summary>
        Slot Acquire(int chunk)
        {
            Slot slot;
            bool decodeHere = false;
            lock (_gate)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(NomapzStream));

                if (_slots.TryGetValue(chunk, out slot))
                {
                    if (slot.Ready) _hits++;
                    _lru.Remove(slot.Node);
                    _lru.AddFirst(slot.Node);
                }
                else
                {
                    slot = NewSlot(chunk, ahead: false);
                    decodeHere = true;
                }
                slot.Used = true;
                slot.Readers++;

                ReadAhead(chunk);
                _lastChunk = chunk;
            }

            if (decodeHere)
            {
                Decode(slot);
                lock (_gate)
                {
                    slot.Ready = true;
                    Monitor.PulseAll(_gate);
                }
            }
            else
            {
                lock (_gate)
                {
                    if (!slot.Ready) _waits++;
                    while (!slot.Ready) Monitor.Wait(_gate);
                }
            }

            if (slot.Error != null)
            {
                lock (_gate) slot.Readers--;
                throw new IOException($"{Path.GetFileName(_path)}: {slot.Error}");
            }
            return slot;
        }

        /// <summary>Queues the chunks after <paramref name="chunk"/> for the workers, when it continues a
        /// forward run. Under the gate.</summary>
        void ReadAhead(int chunk)
        {
            bool forward = Array.IndexOf(_recent, chunk) >= 0;
            if (!forward)
            {
                int before = Array.IndexOf(_recent, chunk - 1);
                if (before >= 0)
                {
                    _recent[before] = chunk;
                    forward = true;
                }
                else
                {
                    _recent[_recentNext] = chunk;
                    _recentNext = (_recentNext + 1) % _recent.Length;
                }
            }
            if (!forward || _readAhead == 0 || _closing) return;

            for (int k = 1; k <= _readAhead && chunk + k < _header.ChunkCount; k++)
            {
                if (_slots.ContainsKey(chunk + k)) continue;
                Slot ahead = NewSlot(chunk + k, ahead: true);
                if (ahead == null) break;
                _aheadDecodes++;
                _work.Enqueue(ahead);
                Monitor.PulseAll(_gate);
            }
        }

        /// <summary>A cache slot for <paramref name="chunk"/>, not yet decoded, making room first. Read-ahead
        /// only takes room freed by chunks already read, and gets null when there is none; a read the
        /// caller is waiting for takes a chunk decoded ahead too, or grows the cache past its capacity
        /// for as long as everything in it is being read. Under the gate.</summary>
        Slot NewSlot(int chunk, bool ahead)
        {
            while (_slots.Count >= _capacity)
            {
                if (Evict(allowUnread: false)) continue;
                if (ahead) return null;
                if (!Evict(allowUnread: true)) break;
            }

            var slot = new Slot { Chunk = chunk, Data = _pool.Count > 0 ? _pool.Pop() : new byte[_header.ChunkSize] };
            slot.Node = _lru.AddFirst(slot);
            _slots[chunk] = slot;
            if (_slots.Count > _peakCached) _peakCached = _slots.Count;

            _decodes++;
            if (_decodedOnce[chunk]) _redecodes++;
            _decodedOnce[chunk] = true;
            return slot;
        }

        /// <summary>Drops the least recently used chunk that may go: decoded, not being read, not one of the
        /// runs' latest, and read already unless <paramref name="allowUnread"/>. Under the gate.</summary>
        bool Evict(bool allowUnread)
        {
            for (LinkedListNode<Slot> node = _lru.Last; node != null; node = node.Previous)
            {
                Slot slot = node.Value;
                if (!slot.Ready || slot.Readers > 0 || (!slot.Used && !allowUnread)) continue;
                if (slot.Chunk == _lastChunk || Array.IndexOf(_recent, slot.Chunk) >= 0) continue;

                Drop(slot);
                _evictions++;
                return true;
            }
            return false;
        }

        void Drop(Slot slot)
        {
            _lru.Remove(slot.Node);
            _slots.Remove(slot.Chunk);
            _pool.Push(slot.Data);
            slot.Data = null;
        }

        [ThreadStatic] static byte[] _source;

        /// <summary>Reads and decodes one chunk into its slot, on the calling thread. A failure is kept on
        /// the slot, for the read waiting on it to throw.</summary>
        void Decode(Slot slot)
        {
            FileStream file = null;
            try
            {
                lock (_gate) file = _files.Count > 0 ? _files.Pop() : null;
                if (file == null) file = OpenFile();

                int length = _header.Lengths[slot.Chunk];
                if (_source == null || _source.Length < length) _source = new byte[Math.Max(length, _header.ChunkSize + (64 << 10))];

                file.Position = _header.Offsets[slot.Chunk];
                int got = 0;
                while (got < length)
                {
                    int read = file.Read(_source, got, length - got);
                    if (read <= 0) throw new EndOfStreamException($"the file ends inside chunk {slot.Chunk}");
                    got += read;
                }

                NomapzChunks.Decode(_header, slot.Chunk, _source, length, slot.Data);
            }
            catch (Exception e)
            {
                slot.Error = e.Message;
                lock (_gate)
                    if (_failure == null) _failure = $"chunk {slot.Chunk}: {e.Message}";
            }
            finally
            {
                if (file != null)
                {
                    bool keep;
                    lock (_gate)
                    {
                        keep = !_closing;
                        if (keep) _files.Push(file);
                    }
                    if (!keep) file.Dispose();
                }
            }
        }

        FileStream OpenFile() =>
            new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.RandomAccess);

        void Work()
        {
            while (true)
            {
                Slot slot;
                lock (_gate)
                {
                    while (_work.Count == 0 && !_closing) Monitor.Wait(_gate);
                    if (_closing) return;
                    slot = _work.Dequeue();
                }

                Decode(slot);
                lock (_gate)
                {
                    slot.Ready = true;
                    Monitor.PulseAll(_gate);
                }
            }
        }

        /// <summary>
        /// Lets go of every decoded chunk no read is copying from, and of the buffer pool: the cache's
        /// 64 MB, for when the map is loaded and nothing will read for a while. A later read decodes again.
        /// </summary>
        public void Trim()
        {
            lock (_gate)
            {
                for (LinkedListNode<Slot> node = _lru.Last; node != null;)
                {
                    LinkedListNode<Slot> previous = node.Previous;
                    if (node.Value.Ready && node.Value.Readers == 0) Drop(node.Value);
                    node = previous;
                }
                _pool.Clear();
            }
        }

        /// <summary>The first chunk that did not decode, and why; null while every one has.</summary>
        public string Failure
        {
            get { lock (_gate) return _failure; }
        }

        public long Decodes { get { lock (_gate) return _decodes; } }

        /// <summary>Chunks decoded again after being evicted: what too small a cache costs.</summary>
        public long Redecodes { get { lock (_gate) return _redecodes; } }

        public int PeakCachedChunks { get { lock (_gate) return _peakCached; } }

        public int CachedChunks { get { lock (_gate) return _slots.Count; } }

        /// <summary>One line for the debug log.</summary>
        public string Stats()
        {
            lock (_gate)
                return $"{_decodes:N0} chunk decodes ({_aheadDecodes:N0} read ahead, {_redecodes:N0} again after eviction), " +
                       $"{_hits:N0} cache hits, {_waits:N0} waits for a worker, {_evictions:N0} evictions, " +
                       $"cache {_slots.Count} of {_capacity} x {_header.ChunkSize >> 10} KiB (peak {_peakCached})";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    _disposed = true;
                    _closing = true;

                    // Chunks queued for read-ahead that no worker will now take: anything waiting on
                    // one must not wait forever.
                    while (_work.Count > 0)
                    {
                        Slot slot = _work.Dequeue();
                        slot.Error = "the stream was closed";
                        slot.Ready = true;
                    }
                    Monitor.PulseAll(_gate);
                }

                foreach (Thread worker in _workers) worker.Join();

                lock (_gate)
                {
                    while (_files.Count > 0) _files.Pop().Dispose();
                    _slots.Clear();
                    _lru.Clear();
                    _pool.Clear();
                }
            }
            base.Dispose(disposing);
        }
    }
}
