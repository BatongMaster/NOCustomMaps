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
    /// <summary>One loaded <c>.nomap</c> bundle and the map it carries.</summary>
    internal sealed class LoadedMap
    {
        public string File;
        public string ContentHash;          // full SHA-256, lowercase hex; null until the bundle is hashed
        public AssetBundle Bundle;
        public MapManifest Manifest;
        public MapDetails Details;

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
            return _root;
        }

        public T Asset<T>(string name) where T : UnityEngine.Object
        {
            if (string.IsNullOrEmpty(name) || Bundle == null) return null;
            T asset = Bundle.LoadAsset<T>(name);
            if (asset == null)
                Plugin.LogWarning($"{Name()}: no {typeof(T).Name} named '{name}' in the bundle");
            return asset;
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

        public static void ScanAndLoad()
        {
            if (_scanned) return;
            _scanned = true;

            List<string> directories = Plugin.ResolveMapDirectories();
            List<DiscoveredBundle> found = BundleDiscovery.Scan(directories, ListFiles);
            _found = found.Count;

            if (found.Count == 0)
            {
                Plugin.LogInfo("no .nomap bundles found. Searched: " + string.Join(" | ", directories));
                return;
            }

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

            AssetBundle bundle = AssetBundle.LoadFromFile(candidate.Path);
            if (bundle == null)
            {
                Plugin.LogError($"{candidate.Path}: AssetBundle.LoadFromFile returned null. " + WhyNotLoaded(candidate.Path));
                return null;
            }

            var map = new LoadedMap { File = candidate.Path, Bundle = bundle };

            map.Manifest = ReadManifest(bundle, candidate.Path);
            if (map.Manifest == null) return null;

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
                return null;
            }

            return map;
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
        /// </summary>
        static void BeginHashing()
        {
            var jobs = new List<HashJob>(_opened.Count);
            foreach (LoadedMap map in _opened) jobs.Add(new HashJob { Map = map, Path = map.File });

            string cacheFile = HashCacheFile();
            _jobs = jobs;
            _hashed = false;
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

        /// <summary>The hashing thread. Touches no Unity API and does not log: everything it learns
        /// goes into the jobs, for <see cref="Settle"/> to act on.</summary>
        static void HashAll(List<HashJob> jobs, string cacheFile)
        {
            try
            {
                BundleHashCache cache = cacheFile != null ? BundleHashCache.Load(cacheFile) : null;

                foreach (HashJob job in jobs)
                {
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
                _hashed = true;
            }
        }

        /// <summary>Registers the hashed maps, once, waiting for the hashing thread if it is still
        /// running. Main thread only.</summary>
        static void Settle()
        {
            if (_settled) return;
            _settled = true;

            if (_hashing != null)
            {
                long start = Stopwatch.GetTimestamp();
                _hashing.Join();
                _hashing = null;

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
            _jobs = null;

            if (_cacheProblem != null)
                Plugin.LogDebug($"could not save the bundle hashes for the next start: {_cacheProblem}");

            Plugin.LogInfo($"loaded {_maps.Count} of {_found} bundle(s): " +
                           string.Join(", ", _maps.ConvertAll(m => m.Name())));
        }

        static void Register(HashJob job)
        {
            LoadedMap map = job.Map;
            if (job.Hash == null)
            {
                Plugin.LogError($"{job.Path}: could not hash the bundle: {job.Error ?? "the hashing thread stopped"}");
                return;
            }

            map.ContentHash = job.Hash;

            map.Details = ScriptableObject.CreateInstance<MapDetails>();
            map.Details.PrefabName = map.Manifest.PrefabNameFor(map.ContentHash);
            map.Details.MapName = map.Manifest.DisplayName;
            map.Details.MapImage = map.Asset<Sprite>(map.Manifest.MapImage);
            // Survives scene loads; without this the MapDetails is collected and every
            // array we appended it to holds a destroyed reference.
            UnityEngine.Object.DontDestroyOnLoad(map.Details);
            map.Details.hideFlags = HideFlags.HideAndDontSave;

            _maps.Add(map);

            Plugin.LogInfo($"{Path.GetFileName(map.File)} -> {map.Details.PrefabName} " +
                           $"(\"{map.Details.MapName}\", {map.Manifest.MapSizeX:0}x{map.Manifest.MapSizeY:0} m)");
            Plugin.LogDebug($"  sha256={map.ContentHash} " +
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

            return "The file is not an AssetBundle, was built with a Unity version whose serialization this " +
                   "build of the game cannot read (the game is 2022.3.62f2), or is another build of a map " +
                   "already loaded (Player.log then says another AssetBundle has the same files).";
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
