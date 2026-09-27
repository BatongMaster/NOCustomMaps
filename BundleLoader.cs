using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NuclearOption.SceneLoading;
using UnityEngine;

namespace CustomMaps
{
    /// <summary>One loaded <c>.nomap</c> bundle and the map it carries.</summary>
    internal sealed class LoadedMap
    {
        public string File;
        public string ContentHash;          // full SHA-256, lowercase hex
        public AssetBundle Bundle;
        public MapManifest Manifest;
        public MapDetails Details;

        GameObject _root;
        AssetBundleRequest _warm;
        bool _resolveFailed;

        /// <summary>The registered name, which embeds the bundle hash. See <see cref="MapIdentity"/>.</summary>
        public string PrefabName => Details != null ? Details.PrefabName : null;

        internal void BeginWarmUp()
        {
            // LoadFromFile only maps the header, so the 8.5M-vertex terrain is still
            // undeserialized at this point. Kicking the request here and keeping it
            // means the work happens while the player sits in the menu; reading
            // .asset later force-completes it if it has not finished. Fast startup,
            // no "map not ready" race, and no async plumbing on the join path.
            if (Bundle == null || Manifest == null || string.IsNullOrEmpty(Manifest.RootPrefab)) return;
            try { _warm = Bundle.LoadAssetAsync<GameObject>(Manifest.RootPrefab); }
            catch (Exception e) { Plugin.LogWarning($"{Name()}: could not begin warm-up: {e.Message}"); }
        }

        /// <summary>The map's root prefab GameObject, blocking on the warm-up if needed.
        /// Null (once, with a logged reason) if the bundle does not contain it.</summary>
        public GameObject Root()
        {
            if (_root != null || _resolveFailed) return _root;

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
    }

    /// <summary>
    /// Finds, opens and keeps the custom map bundles.
    ///
    /// Everything here runs once, from <c>Plugin.Awake</c>. Bundles are never
    /// unloaded: <c>MapSettingsManager.Maps</c> holds a live reference to the prefab
    /// for the whole session, and <c>Unload(false)</c> — which NOMapLoader calls
    /// after its first <c>LoadAsset</c> — drops the memory mapping out from under any
    /// asset that has not been deserialized yet.
    /// </summary>
    internal static class BundleLoader
    {
        /// <summary>Name of the manifest <c>TextAsset</c> inside a bundle
        /// (<c>map.json</c> imported by Unity becomes a TextAsset named <c>map</c>).</summary>
        public const string ManifestAssetName = "map";

        static readonly List<LoadedMap> _maps = new List<LoadedMap>();
        static readonly HashSet<string> _openedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static bool _scanned;

        /// <summary>Successfully loaded maps, in discovery order. Never null.</summary>
        public static IReadOnlyList<LoadedMap> Maps => _maps;

        public static void ScanAndLoad()
        {
            if (_scanned) return;
            _scanned = true;

            List<string> directories = Plugin.ResolveMapDirectories();
            List<DiscoveredBundle> found = BundleDiscovery.Scan(directories, ListFiles);

            if (found.Count == 0)
            {
                Plugin.LogInfo("no .nomap bundles found. Searched: " + string.Join(" | ", directories));
                return;
            }

            foreach (DiscoveredBundle candidate in found)
            {
                try { Load(candidate); }
                catch (Exception e) { Plugin.LogError($"{candidate.Path}: {e}"); }
            }

            Plugin.LogInfo($"loaded {_maps.Count} of {found.Count} bundle(s): " +
                           string.Join(", ", _maps.ConvertAll(m => m.Name())));
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

        static void Load(DiscoveredBundle candidate)
        {
            // A second AssetBundle.LoadFromFile on the same file throws. Discovery
            // already dedupes by path; this also covers a file reached through a
            // junction under a different root.
            if (!_openedFiles.Add(candidate.Path))
            {
                Plugin.LogWarning($"{candidate.Path}: already loaded, skipping");
                return;
            }

            AssetBundle bundle = AssetBundle.LoadFromFile(candidate.Path);
            if (bundle == null)
            {
                Plugin.LogError($"{candidate.Path}: AssetBundle.LoadFromFile returned null. " +
                                "The file is not an AssetBundle, or was built with a Unity version " +
                                "whose serialization this build of the game cannot read (the game is 2022.3.62f2).");
                return;
            }

            var map = new LoadedMap { File = candidate.Path, Bundle = bundle };

            map.Manifest = ReadManifest(bundle, candidate.Path);
            if (map.Manifest == null) return;

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
                return;
            }

            // Hashing the bundle is what makes server and client agree on which build
            // of a map they are running; see MapIdentity.
            map.ContentHash = Sha256(candidate.Path);
            if (map.ContentHash == null) return;

            map.Details = ScriptableObject.CreateInstance<MapDetails>();
            map.Details.PrefabName = map.Manifest.PrefabNameFor(map.ContentHash);
            map.Details.MapName = map.Manifest.DisplayName;
            map.Details.MapImage = map.Asset<Sprite>(map.Manifest.MapImage);
            // Survives scene loads; without this the MapDetails is collected and every
            // array we appended it to holds a destroyed reference.
            UnityEngine.Object.DontDestroyOnLoad(map.Details);
            map.Details.hideFlags = HideFlags.HideAndDontSave;

            map.BeginWarmUp();
            _maps.Add(map);

            Plugin.LogInfo($"{Path.GetFileName(candidate.Path)} -> {map.Details.PrefabName} " +
                           $"(\"{map.Details.MapName}\", {map.Manifest.MapSizeX:0}x{map.Manifest.MapSizeY:0} m)");
            Plugin.LogDebug($"  sha256={map.ContentHash}");
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

        static string Sha256(string path)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (FileStream stream = File.OpenRead(path))
                {
                    byte[] hash = sha.ComputeHash(stream);
                    var sb = new StringBuilder(hash.Length * 2);
                    foreach (byte b in hash) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch (Exception e)
            {
                Plugin.LogError($"{path}: could not hash the bundle: {e.Message}");
                return null;
            }
        }
    }
}
