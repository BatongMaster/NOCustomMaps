using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NuclearOption.SceneLoading;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomMaps
{
    /// <summary>One loaded <c>.nomap</c> or <c>.nomapz</c> bundle and the map it carries.</summary>
    internal sealed class LoadedMap
    {
        public string File;
        public string ContentHash;          // full SHA-256, lowercase hex; null until the bundle is hashed
        public AssetBundle Bundle;
        public MapManifest Manifest;
        public MapDetails Details;

        /// <summary>For a <c>.nomapz</c>: its header, and the stream the bundle reads through, open for
        /// as long as the bundle is loaded. Null for a <c>.nomap</c>.</summary>
        public NomapzHeader Packed;
        public NomapzStream Stream;

        /// <summary>The file's format, for the log.</summary>
        public string Format => Packed != null
            ? $"nomapz format {Packed.Version}, {NomapzHeader.CodecName(Packed.Codec)}"
            : "nomap, Unity's LZ4 bundle";

        /// <summary>Unloads the bundle and only then closes the stream it reads through. For a bundle
        /// that is not going to be offered; registered ones stay loaded for the session.</summary>
        internal void Close()
        {
            try { Bundle?.Unload(true); }
            catch (Exception e) { Plugin.LogWarning($"{Name()}: unloading the bundle threw: {e.Message}"); }
            Bundle = null;
            Stream?.Dispose();
            Stream = null;
        }

        GameObject _root;
        AssetBundleRequest _warm;
        long _warmStarted;
        bool _resolveFailed;

        /// <summary>The registered name, which embeds the bundle hash. See <see cref="MapIdentity"/>.</summary>
        public string PrefabName => Details != null ? Details.PrefabName : null;

        /// <summary>
        /// Starts deserialising the map's root prefab on Unity's loading thread. Only the first call
        /// does anything.
        ///
        /// LoadFromFile only maps the header, so the terrain is still undeserialised at this point.
        /// Kicking the request early and keeping it means the work happens while the player is still
        /// in a menu; <see cref="Root"/> force-completes it if it has not finished. So there is no
        /// "map not ready" race and no async plumbing on the join path, only, at worst, a wait.
        ///
        /// It used to start for every installed map at game start. On Swiss Alps 0.4.0 that is 6.0 s
        /// on the loading thread and +2.5 GB of Unity's native memory, measured in the editor on
        /// 2026-10-04, for anyone with the map installed, all session, whether or not they played it.
        /// It also stood in Unity's queue of asynchronous loads ahead of the game's own (inferred, not
        /// measured). It now starts when the map is about to be needed (<see cref="BundleLoader.WarmUpFor"/>),
        /// and at game start only on a dedicated server, which is there to host.
        /// </summary>
        internal void BeginWarmUp(string reason)
        {
            if (_warm != null || _root != null || _resolveFailed) return;
            if (Bundle == null || Manifest == null || string.IsNullOrEmpty(Manifest.RootPrefab)) return;

            try
            {
                _warm = Bundle.LoadAssetAsync<GameObject>(Manifest.RootPrefab);
                _warmStarted = Stopwatch.GetTimestamp();
                Plugin.LogDebug($"{Name()}: warm-up started ({reason})");

                // How long the prefab took to read, for comparing in game with what the game logs
                // of its scene load ("Scene Load duration"): Unity runs asynchronous loads one after
                // another, so a scene load begun during a warm-up waits for it.
                _warm.completed += _ =>
                    Plugin.LogDebug($"{Name()}: warm-up finished {Seconds(_warmStarted, Stopwatch.GetTimestamp()):0.0} s after it started");
            }
            catch (Exception e) { Plugin.LogWarning($"{Name()}: could not begin warm-up: {e.Message}"); }
        }

        /// <summary>True while the warm-up is still reading the prefab, so <see cref="Root"/> would
        /// wait for the rest of it on the main thread.</summary>
        internal bool WarmingUp => _root == null && !_resolveFailed && _warm != null && !_warm.isDone;

        /// <summary>True once the map's prefab has been asked for, by a warm-up or to play the map: from then
        /// on the textures it holds, its colour map and chart among them, are the map's, and nothing else
        /// unloads them (<see cref="MapThumbnails"/>).</summary>
        internal bool PrefabRequested => _warm != null || _root != null || _resolveFailed;

        /// <summary>The map's root prefab GameObject, blocking on the warm-up if needed.
        /// Null (once, with a logged reason) if the bundle does not contain it.</summary>
        public GameObject Root()
        {
            if (_root != null || _resolveFailed) return _root;

            long start = Stopwatch.GetTimestamp();
            string how = _warm == null ? "no warm-up, loaded here"
                       : _warm.isDone ? $"warm-up started {Seconds(_warmStarted, start):0.0} s earlier and done"
                       : $"warm-up started {Seconds(_warmStarted, start):0.0} s earlier, finished here";
            try
            {
                _root = _warm != null
                    ? _warm.asset as GameObject
                    : Bundle.LoadAsset<GameObject>(Manifest.RootPrefab);
            }
            catch (Exception e)
            {
                Plugin.LogError($"{Name()}: loading prefab '{Manifest.RootPrefab}' threw: {e}");
            }

            if (_root == null)
            {
                _resolveFailed = true;
                Plugin.LogError($"{Name()}: prefab '{Manifest.RootPrefab}' is not in the bundle. " +
                                "Check 'assets.rootPrefab' in the map manifest against the bundle contents.");
            }
            else
            {
                Plugin.LogDebug($"{Name()}: prefab ready after {Seconds(start, Stopwatch.GetTimestamp()) * 1000:0} ms " +
                                $"on the main thread ({how})");
            }

            if (Stream != null) AfterPackedLoad();
            return _root;
        }

        /// <summary>
        /// Once a <c>.nomapz</c>'s prefab is read: says whether every chunk decoded, and lets the decode
        /// cache go. Unity reads nothing more of the bundle for the map itself after this (measured: a
        /// second instantiation and a 600-frame fly-over read 0 bytes), so the cache's 64 MB would sit
        /// idle all session; a later read, such as the grass mask, decodes its few chunks again.
        /// </summary>
        void AfterPackedLoad()
        {
            string failure = Stream.Failure;
            if (failure != null)
                Plugin.LogError($"{Name()}: {Path.GetFileName(File)} has a chunk that does not decode ({failure}). " +
                                "The file is damaged: download it again.");

            Plugin.LogDebug($"{Name()}: {Stream.Stats()}; cache let go");
            Stream.Trim();
        }

        public T Asset<T>(string name) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(name) || Bundle == null) return null;
            T asset = Bundle.LoadAsset<T>(name);
            if (asset == null)
                Plugin.LogWarning($"{Name()}: no {typeof(T).Name} named '{name}' in the bundle");
            return asset;
        }

        /// <summary>
        /// Starts reading an asset on Unity's loading thread and says nothing if the bundle has none of that
        /// name, unlike <see cref="Asset{T}"/>: for a picture tried among others, where a missing one is
        /// expected (<see cref="MapThumbnails"/>). Null when there is no name or no bundle; otherwise the
        /// request, whose <c>asset</c> is null once done if the bundle has no such asset.
        /// </summary>
        internal AssetBundleRequest LoadQuietAsync<T>(string name) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(name) || Bundle == null) return null;
            return Bundle.LoadAssetAsync<T>(name);
        }

        public string Name() => PrefabName ?? Path.GetFileName(File);

        internal static double Seconds(long from, long to) => (to - from) / (double)Stopwatch.Frequency;
    }

    /// <summary>
    /// Finds, opens and keeps the custom map bundles.
    ///
    /// The bundles are found and opened once, from <c>Plugin.Awake</c>. Their hashes, which name them
    /// in multiplayer, are worked out on a thread of their own (<see cref="BundleHash"/>), and the maps
    /// are registered once those are in: a frame after start when the hashes are remembered from an
    /// earlier start, or as soon as anything asks for <see cref="Maps"/>, which then waits for them.
    ///
    /// Bundles are never unloaded: <c>MapSettingsManager.Maps</c> holds a live reference to the prefab
    /// for the whole session, and <c>Unload(false)</c> — which NOMapLoader calls after its first
    /// <c>LoadAsset</c> — drops the memory mapping out from under any asset that has not been
    /// deserialized yet.
    /// </summary>
    internal static class BundleLoader
    {
        /// <summary>Name of the manifest <c>TextAsset</c> inside a bundle
        /// (<c>map.json</c> imported by Unity becomes a TextAsset named <c>map</c>).</summary>
        public const string ManifestAssetName = "map";

        /// <summary>The file in BepInEx's <c>cache</c> folder that remembers each bundle's hash
        /// (<see cref="BundleHashCache"/>). Safe to delete.</summary>
        public const string HashCacheFileName = Plugin.Guid + ".bundle-hashes.txt";

        /// <summary>Hashed and registered, in discovery order.</summary>
        static readonly List<LoadedMap> _maps = new List<LoadedMap>();
        /// <summary>Every bundle opened with a readable manifest, hashed or not, in discovery order.</summary>
        static readonly List<LoadedMap> _opened = new List<LoadedMap>();
        static readonly HashSet<string> _openedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static bool _scanned;
        static int _found;

        // The hashing thread's work. Each job is written by that thread only, and read on the main
        // thread only after it has been joined.
        static List<HashJob> _jobs;
        static Thread _hashing;
        static volatile bool _hashed;
        static volatile bool _checked;
        static readonly ManualResetEventSlim _namesIn = new ManualResetEventSlim(true);
        static volatile string _cacheProblem;
        static bool _settled = true;

        sealed class HashJob
        {
            public LoadedMap Map;
            public string Path;
            public string Hash;
            public bool Remembered;
            public double Seconds;
            public string Error;

            // A .nomapz: its name comes from Identity, the header's, and the hashing thread checks the
            // file after the names are in (see HashAll).
            public bool Packed;
            public string Identity;
            public string Problem;
        }

        /// <summary>
        /// Successfully loaded maps, in discovery order. Never null.
        ///
        /// Reading it registers the maps first if their hashes are still being worked out, waiting for
        /// them on this thread. Everything that reads it runs on the main thread, from the game's own
        /// map lookups onwards; nothing in the game asks before a mission or lobby is on its way.
        /// </summary>
        public static IReadOnlyList<LoadedMap> Maps
        {
            get
            {
                Settle();
                return _maps;
            }
        }

        /// <summary>
        /// True when reading <see cref="Maps"/> would not wait: the maps are registered, or their hashes are
        /// in and registering them is a matter of moments. False only in the first seconds after a start
        /// that is hashing a newly installed or updated map. For the mission editor's map tiles, which wait
        /// a frame at a time rather than hold the main thread (<c>MapTileList</c>).
        /// </summary>
        internal static bool Ready => _settled || _hashed;

        public static void ScanAndLoad()
        {
            if (_scanned) return;
            _scanned = true;

            List<string> directories = Plugin.ResolveMapDirectories();
            List<DiscoveredBundle> found = BundleDiscovery.Scan(directories, ListFiles);
            _found = found.Count;

            if (found.Count == 0)
            {
                Plugin.LogInfo("no .nomap or .nomapz bundles found. Searched: " + string.Join(" | ", directories));
                return;
            }

            // Both forms of one build installed: Unity would open one and refuse the other, so discovery
            // kept the .nomapz alone (BundleDiscovery.Scan).
            foreach (DiscoveredBundle candidate in found)
                foreach (string other in candidate.PreferredOver)
                    Plugin.LogInfo($"{Path.GetFileName(candidate.Path)} and {other} are the same build of a map in its two " +
                                   "forms: loading the .nomapz, leaving the .nomap. Remove one of them.");

            // A dedicated server (the game's own test: no graphics device) still reads every map's
            // prefab from the start, as all of them once did: it is there to host, it has no menu to
            // spend the time in, and its memory is spent on whatever it was set up to run.
            bool server = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

            foreach (DiscoveredBundle candidate in found)
            {
                try
                {
                    LoadedMap map = Open(candidate);
                    if (map == null) continue;

                    _opened.Add(map);
                    if (server) map.BeginWarmUp("dedicated server");
                }
                catch (Exception e) { Plugin.LogError($"{candidate.Path}: {e}"); }
            }

            if (_opened.Count == 0)
            {
                Plugin.LogInfo($"loaded 0 of {found.Count} bundle(s)");
                return;
            }

            BeginHashing();
        }

        /// <summary>
        /// Registers the maps as soon as their hashes are in, on the main thread, without waiting
        /// there: a coroutine started from <c>Plugin.Awake</c>. Anything that reads <see cref="Maps"/>
        /// sooner registers them itself.
        /// </summary>
        internal static IEnumerator SettleWhenHashed()
        {
            while (!_settled && !_hashed) yield return null;
            Settle();

            // Then what the hashing thread found of the .nomapz files, which it checks after the names
            // are in (HashAll).
            while (!_checked) yield return null;
            ReportChecks();
        }

        /// <summary>
        /// Starts the warm-up (<see cref="LoadedMap.BeginWarmUp"/>) of the bundle a map key names, if it
        /// is one of ours, because that map is about to be needed; <paramref name="reason"/> says why,
        /// for the debug log. Never waits for the hashes: until they are in, a bundle is matched by its
        /// map id alone (<see cref="WarmUpPolicy"/>).
        /// </summary>
        internal static void WarmUpFor(string prefabName, string reason)
        {
            if (MapIdentity.IdOf(prefabName) == null) return;   // a shipped map

            foreach (LoadedMap map in _settled ? _maps : _opened)
                if (WarmUpPolicy.Wants(prefabName, map.Manifest?.MapId, map.ContentHash))
                    map.BeginWarmUp(reason);
        }

        /// <summary>
        /// Starts the warm-up of the installed map a lobby being joined advertises by its display name
        /// (<see cref="WarmUpPolicy.ForLobby"/>), before the server has sent the map's key. Like
        /// <see cref="WarmUpFor"/>, never waits for the hashes.
        /// </summary>
        internal static void WarmUpForLobby(string mapName, string reason)
        {
            List<LoadedMap> maps = _settled ? _maps : _opened;
            var names = new List<string>(maps.Count);
            foreach (LoadedMap map in maps) names.Add(map.Manifest?.DisplayName);

            int index = WarmUpPolicy.ForLobby(mapName, names);
            if (index >= 0) maps[index].BeginWarmUp(reason);
        }

        /// <summary>The installed map the key <paramref name="prefabName"/> names if its warm-up is
        /// still reading the prefab, or null: not one of ours, not warming up, or already read.</summary>
        internal static LoadedMap WarmingUpFor(string prefabName)
        {
            if (MapIdentity.IdOf(prefabName) == null) return null;

            foreach (LoadedMap map in _settled ? _maps : _opened)
                if (map.WarmingUp && WarmUpPolicy.Wants(prefabName, map.Manifest?.MapId, map.ContentHash))
                    return map;
            return null;
        }

        static IEnumerable<string> ListFiles(string directory)
        {
            try
            {
                return Directory.Exists(directory) ? Directory.GetFiles(directory) : null;
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"could not list '{directory}': {e.Message}");
                return null;
            }
        }

        /// <summary>Opens a bundle and reads its manifest. Null, with the reason logged, if it cannot
        /// be offered.</summary>
        static LoadedMap Open(DiscoveredBundle candidate)
        {
            // A second AssetBundle.LoadFromFile on the same file throws. Discovery
            // already dedupes by path; this also covers a file reached through a
            // junction under a different root.
            if (!_openedFiles.Add(candidate.Path))
            {
                Plugin.LogWarning($"{candidate.Path}: already loaded, skipping");
                return null;
            }

            LoadedMap map = candidate.Packed ? OpenPacked(candidate.Path) : OpenFile(candidate.Path);
            if (map == null) return null;

            map.Manifest = ReadManifest(map.Bundle, candidate.Path);
            if (map.Manifest == null)
            {
                if (map.Stream != null) map.Close();
                return null;
            }

            List<MapProblem> problems = map.Manifest.Validate();
            bool fatal = false;
            foreach (MapProblem p in problems)
            {
                if (p.IsError) { Plugin.LogError($"{candidate.Path}: {p}"); fatal = true; }
                else Plugin.LogWarning($"{candidate.Path}: {p}");
            }
            if (fatal && Plugin.ValidateOnLoad.Value)
            {
                Plugin.LogError($"{candidate.Path}: manifest has errors, not registering this map.");
                if (map.Stream != null) map.Close();
                return null;
            }

            return map;
        }

        /// <summary>A <c>.nomap</c>: Unity's own LZ4 bundle, opened from the file.</summary>
        static LoadedMap OpenFile(string path)
        {
            AssetBundle bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null)
            {
                Plugin.LogError($"{path}: AssetBundle.LoadFromFile returned null. " + WhyNotLoaded(path));
                return null;
            }
            return new LoadedMap { File = path, Bundle = bundle };
        }

        /// <summary>
        /// A <c>.nomapz</c> (<see cref="NomapzHeader"/>): its header read and checked, then the bundle in
        /// it opened with <c>AssetBundle.LoadFromStream</c> through a <see cref="NomapzStream"/>, which
        /// stays open with the bundle. A header this build cannot read is refused here, saying why.
        /// </summary>
        static LoadedMap OpenPacked(string path)
        {
            long start = Stopwatch.GetTimestamp();

            NomapzHeader header;
            try
            {
                header = NomapzHeader.Read(path);
            }
            catch (NomapzFormatException e)
            {
                Plugin.LogError($"{path}: cannot be loaded: " + WhyNotLoaded(path, e));
                return null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Plugin.LogError($"{path}: cannot be read: {e.Message}");
                return null;
            }

            NomapzStream stream;
            try
            {
                stream = new NomapzStream(path, header);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Plugin.LogError($"{path}: cannot be read: {e.Message}");
                return null;
            }

            AssetBundle bundle = null;
            try
            {
                bundle = AssetBundle.LoadFromStream(stream);
            }
            catch (Exception e)
            {
                Plugin.LogError($"{path}: AssetBundle.LoadFromStream threw: {e.Message}");
            }

            if (bundle == null)
            {
                string failure = stream.Failure;
                stream.Dispose();
                Plugin.LogError($"{path}: AssetBundle.LoadFromStream returned null. " +
                                (failure != null
                                    ? $"A chunk of the file does not decode ({failure}): it is damaged. Download it again."
                                    : WhyNotLoaded(path)));
                return null;
            }

            Plugin.LogInfo($"{Path.GetFileName(path)}: {header.Describe()}, opened through a decoding stream " +
                           $"in {LoadedMap.Seconds(start, Stopwatch.GetTimestamp()) * 1000:0} ms");
            return new LoadedMap { File = path, Bundle = bundle, Packed = header, Stream = stream };
        }

        /// <summary>
        /// Hashes the opened bundles on a thread of their own.
        ///
        /// Hashing is what makes server and client agree on which build of a map they are running
        /// (see <see cref="MapIdentity"/>), so it cannot be skipped, but it need not hold up the game.
        /// It ran on the main thread inside <c>Plugin.Awake</c>, which put 10.2 s for Swiss Alps
        /// before the game's own start-up could go on, at every start. Remembered hashes now make that
        /// a fingerprint of 2 MB per bundle, a few hundredths of a second a start for Swiss Alps
        /// (20–35 ms with the cache file read, measured under Unity's Mono, one process per start),
        /// and the first start after a map is installed or updated hashes it while the game loads its
        /// menus.
        ///
        /// A <c>.nomapz</c> needs no hash for its name: that is the identity in its header, the
        /// <c>.nomap</c>'s hash, so it registers as the <c>.nomap</c> of its build does. The thread
        /// checks it afterwards instead (<see cref="HashAll"/>).
        /// </summary>
        static void BeginHashing()
        {
            var jobs = new List<HashJob>(_opened.Count);
            foreach (LoadedMap map in _opened)
                jobs.Add(new HashJob
                {
                    Map = map,
                    Path = map.File,
                    Packed = map.Packed != null,
                    Identity = map.Packed?.IdentityHex,
                });

            string cacheFile = HashCacheFile();
            _jobs = jobs;
            _hashed = false;
            _checked = false;
            _namesIn.Reset();
            _settled = false;

            try
            {
                _hashing = new Thread(() => HashAll(jobs, cacheFile))
                {
                    IsBackground = true,
                    Name = "Custom Maps bundle hash",
                };
                _hashing.Start();
                Plugin.LogDebug($"hashing {jobs.Count} bundle(s) on a thread of their own");
            }
            catch (Exception e)
            {
                _hashing = null;
                Plugin.LogWarning($"could not hash the bundles on a thread of their own ({e.Message}); hashing them now");
                HashAll(jobs, cacheFile);
            }
        }

        /// <summary>
        /// The hashing thread. Touches no Unity API and does not log: everything it learns goes into the
        /// jobs, for <see cref="Settle"/> and <see cref="ReportChecks"/> to act on.
        ///
        /// First the <c>.nomap</c> files, whose names wait for their hashes; then, with the names all in
        /// and <see cref="Settle"/> free to go on, each <c>.nomapz</c> the hash cache does not remember:
        /// hashed whole, and every chunk decoded against the payload hash in its header, which tells a
        /// damaged download from a sound one. Only a file that passes is remembered, so that costs the
        /// first start after a map is installed and no other: about 20 s of this thread for Swiss Alps
        /// 0.4.0 under the game's Mono, estimated (640 MB hashed, 2.1 GB decoded on two more threads and
        /// hashed), while the game loads its menus.
        /// </summary>
        static void HashAll(List<HashJob> jobs, string cacheFile)
        {
            BundleHashCache cache = null;
            try
            {
                cache = cacheFile != null ? BundleHashCache.Load(cacheFile) : null;

                foreach (HashJob job in jobs)
                {
                    if (job.Packed) continue;

                    var watch = Stopwatch.StartNew();
                    try
                    {
                        job.Hash = BundleHash.Resolve(job.Path, cache, out bool remembered);
                        job.Remembered = remembered;
                    }
                    catch (Exception e)
                    {
                        job.Error = e.Message;
                    }
                    job.Seconds = watch.Elapsed.TotalSeconds;
                }
            }
            catch (Exception e)
            {
                _cacheProblem = e.Message;
            }
            finally
            {
                _hashed = true;
                _namesIn.Set();
            }

            try
            {
                foreach (HashJob job in jobs)
                {
                    if (!job.Packed) continue;

                    var watch = Stopwatch.StartNew();
                    try
                    {
                        NomapzHeader header = job.Map.Packed;
                        job.Hash = BundleHash.Resolve(job.Path, cache,
                                                      () => NomapzChunks.Check(job.Path, header, 2, null, CancellationToken.None),
                                                      out bool remembered, out string problem);
                        job.Remembered = remembered;
                        job.Problem = problem;
                    }
                    catch (Exception e)
                    {
                        job.Error = e.Message;
                    }
                    job.Seconds = watch.Elapsed.TotalSeconds;
                }

                if (cache != null)
                {
                    cache.Prune(System.IO.File.Exists);
                    if (cache.Changed) _cacheProblem = cache.Save(cacheFile);
                }
            }
            catch (Exception e)
            {
                _cacheProblem = e.Message;
            }
            finally
            {
                _checked = true;
            }
        }

        /// <summary>Registers the hashed maps, once, waiting for the hashing thread to have their names
        /// if it is still working them out. Main thread only.</summary>
        static void Settle()
        {
            if (_settled) return;
            _settled = true;

            if (_hashing != null)
            {
                long start = Stopwatch.GetTimestamp();
                _namesIn.Wait();

                // Only the first start after a map is installed or updated can wait long, and only if
                // the game asks for a map before its hash is in. A second or more is worth seeing
                // without debug logging: a client joining a server times out after 30 s.
                double waited = LoadedMap.Seconds(start, Stopwatch.GetTimestamp());
                string wait = $"waited {waited:0.00} s on the main thread for the bundles' hashes";
                if (waited >= 1) Plugin.LogInfo(wait);
                else if (waited >= 0.05) Plugin.LogDebug(wait);
            }

            foreach (HashJob job in _jobs)
            {
                try { Register(job); }
                catch (Exception e) { Plugin.LogError($"{job.Path}: {e}"); }
            }

            Plugin.LogInfo($"loaded {_maps.Count} of {_found} bundle(s): " +
                           string.Join(", ", _maps.ConvertAll(m => m.Name())));
        }

        /// <summary>
        /// Says what the hashing thread found of each <c>.nomapz</c>, once it is done: a damaged file as
        /// an error, a sound one in the debug log with its SHA-256 (what a release lists for its
        /// download). Main thread, after <see cref="Settle"/>.
        /// </summary>
        static void ReportChecks()
        {
            if (_jobs == null) return;

            foreach (HashJob job in _jobs)
            {
                if (!job.Packed) continue;
                string file = Path.GetFileName(job.Path);

                if (job.Error != null)
                    Plugin.LogWarning($"{file}: could not check the file: {job.Error}");
                else if (job.Problem != null)
                    Plugin.LogError($"{file} is damaged: {job.Problem}. The map may fail to load or load wrong; " +
                                    "download it again.");
                else
                    Plugin.LogDebug($"{file}: sha256={job.Hash} " +
                                    (job.Remembered
                                        ? $"(checked at an earlier start, file recognised in {job.Seconds:0.00} s)"
                                        : $"(hashed and every chunk checked in {job.Seconds:0.0} s)"));
            }
            _jobs = null;

            if (_cacheProblem != null)
                Plugin.LogDebug($"could not save the bundle hashes for the next start: {_cacheProblem}");
        }

        static void Register(HashJob job)
        {
            LoadedMap map = job.Map;
            if (job.Packed)
            {
                // The .nomap's hash, from the header: the name the .nomap of this build registers under.
                // The job's other fields belong to the hashing thread until it has checked the file.
                map.ContentHash = job.Identity;
            }
            else
            {
                if (job.Hash == null)
                {
                    Plugin.LogError($"{job.Path}: could not hash the bundle: {job.Error ?? "the hashing thread stopped"}");
                    return;
                }
                map.ContentHash = job.Hash;
            }

            map.Details = ScriptableObject.CreateInstance<MapDetails>();
            map.Details.PrefabName = map.Manifest.PrefabNameFor(map.ContentHash);
            map.Details.MapName = map.Manifest.DisplayName;
            // No MapImage here. In the game it is read by one thing, the New Mission panel's map tile
            // (NewMissionMapButton.SetMap); the tactical map reads MapSettings.MapImage, which MapFixups sets
            // when the map is prepared. It used to be the map's chart, read whole on the main thread at every
            // start for every installed map and kept all session (8192 by 8192 on Swiss Alps 0.4.0, 43 to 85
            // MiB), for a tile the game never showed. The tile now gets a 512 by 512 picture of its own,
            // made when the panel first shows it and cached (MapThumbnails).
            // Survives scene loads; without this the MapDetails is collected and every
            // array we appended it to holds a destroyed reference.
            UnityEngine.Object.DontDestroyOnLoad(map.Details);
            map.Details.hideFlags = HideFlags.HideAndDontSave;

            _maps.Add(map);

            Plugin.LogInfo($"{Path.GetFileName(map.File)} -> {map.Details.PrefabName} " +
                           $"(\"{map.Details.MapName}\", {map.Manifest.MapSizeX:0}x{map.Manifest.MapSizeY:0} m, {map.Format})");
            Plugin.LogDebug(job.Packed
                ? $"  identity={map.ContentHash} (from the .nomapz header: the SHA-256 of the .nomap of this build)"
                : $"  sha256={map.ContentHash} " +
                  (job.Remembered
                      ? $"(remembered from an earlier start, file checked in {job.Seconds:0.00} s)"
                      : $"(hashed in full in {job.Seconds:0.00} s)"));
        }

        /// <summary>Where the hashes are remembered, or null (nothing remembered) if BepInEx cannot
        /// say.</summary>
        static string HashCacheFile()
        {
            try
            {
                string folder = BepInEx.Paths.CachePath;
                return string.IsNullOrEmpty(folder) ? null : Path.Combine(folder, HashCacheFileName);
            }
            catch (Exception e)
            {
                Plugin.LogDebug($"no BepInEx cache folder, so the bundle hashes are not remembered: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Why a bundle did not open, as far as the plugin can tell.
        ///
        /// Unity also refuses a second bundle with the same internal name, which Map Forge makes the
        /// map id, and says so only in Player.log. This log used to blame the file or its Unity
        /// version alone, and on 2026-10-02 that pointed away from the real cause: the old 0.3.0
        /// Swiss Alps, still installed under <c>addons/</c> beside the new 0.4.0 in <c>maps/</c>.
        /// Map Forge names its files <c>&lt;id&gt;-&lt;version&gt;.nomap</c>, so a map already loaded whose id
        /// starts this file's name, before a version, is the one in the way.
        /// </summary>
        static string WhyNotLoaded(string path)
        {
            string file = Path.GetFileNameWithoutExtension(path);

            foreach (LoadedMap loaded in _opened)
            {
                string id = loaded.Manifest?.MapId;
                if (string.IsNullOrEmpty(id)) continue;

                bool sameMap = file.Equals(id, StringComparison.OrdinalIgnoreCase) ||
                               (file.Length > id.Length + 1 &&
                                file.StartsWith(id + "-", StringComparison.OrdinalIgnoreCase) &&
                                char.IsDigit(file[id.Length + 1]));
                if (sameMap)
                    return $"Another build of '{id}' is already loaded, from {loaded.File}, and Unity opens " +
                           "only one bundle of a map. Keep one build of each map installed.";
            }

            return (path.EndsWith(NomapzHeader.Extension, StringComparison.OrdinalIgnoreCase)
                       ? "The .nomapz's header and chunks read, but Unity did not take the bundle in it: it "
                       : "The file is not an AssetBundle, ") +
                   "was built with a Unity version whose serialization this " +
                   "build of the game cannot read (the game is 2022.3.62f2), or is another build of a map " +
                   "already loaded (Player.log then says another AssetBundle has the same files).";
        }

        /// <summary>Why a <c>.nomapz</c> whose header this build refused was not loaded: a newer format or
        /// codec than this plugin reads, or a damaged or unfinished file.</summary>
        static string WhyNotLoaded(string path, NomapzFormatException refused)
        {
            return refused.NeedsNewerReader
                ? $"{refused.Message} This is NOCustomMaps {Plugin.Version}: update it to load this map."
                : $"{refused.Message}. Download {Path.GetFileName(path)} again.";
        }

        static MapManifest ReadManifest(AssetBundle bundle, string path)
        {
            TextAsset json = null;
            try { json = bundle.LoadAsset<TextAsset>(ManifestAssetName); }
            catch (Exception e) { Plugin.LogWarning($"{path}: {e.Message}"); }

            // Fall back to any TextAsset that looks like JSON, so a bundle built with a
            // differently named manifest still reports something better than "no map".
            if (json == null)
            {
                foreach (TextAsset candidate in bundle.LoadAllAssets<TextAsset>())
                {
                    string t = candidate != null ? candidate.text : null;
                    if (!string.IsNullOrEmpty(t) && t.TrimStart().StartsWith("{", StringComparison.Ordinal))
                    {
                        json = candidate;
                        Plugin.LogWarning($"{path}: no TextAsset named '{ManifestAssetName}', " +
                                          $"using '{candidate.name}' instead");
                        break;
                    }
                }
            }

            if (json == null)
            {
                Plugin.LogError($"{path}: no manifest. A .nomap bundle must contain a TextAsset " +
                                $"named '{ManifestAssetName}' holding the map.json.");
                return null;
            }

            try
            {
                return MapManifest.FromJson(json.text);
            }
            catch (Exception e)
            {
                Plugin.LogError($"{path}: manifest is not valid JSON: {e.Message}");
                return null;
            }
        }
    }
}
