using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using NuclearOption.SceneLoading;
using UnityEngine;

namespace CustomMaps
{
    /// <summary>
    /// Registers custom maps with Nuclear Option.
    ///
    /// The game has no map mod type — <c>MapKey.AddressableWorkshop</c>,
    /// <c>AddressableAppData</c> and <c>AddressableBuiltin</c> are all
    /// <c>throw new NotImplementedException()</c>, and
    /// <c>NuclearOption.Workshop.SubscribedItemType</c> knows only about missions and
    /// liveries. What does exist is <c>MapKey.KeyType.GameWorldPrefab</c>: a map is a
    /// prefab instantiated into the shared GameWorld scene, looked up by name in two
    /// hard arrays. This plugin appends to those arrays.
    /// </summary>
    [BepInPlugin(Guid, "Custom Maps", Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.javoski.custommaps";
        public const string Version = "1.2.0";

        internal static Plugin Instance { get; private set; }
        static ManualLogSource _log;

        /// <summary>Set when a critical patch target could not be resolved. Registration is
        /// suppressed entirely rather than left half-working — on a dedicated server a
        /// partially registered map kicks every joining client.</summary>
        internal static bool Disabled { get; private set; }

        internal static ConfigEntry<string> MapDirectories;
        internal static ConfigEntry<bool> DebugLogging;
        internal static ConfigEntry<bool> ValidateOnLoad;
        internal static ConfigEntry<string> DonorMap;
        internal static ConfigEntry<bool> BorrowDetailRenderer;

        void Awake()
        {
            Instance = this;
            _log = Logger;

            BindConfig();

            if (!AssertPatchTargets())
            {
                Disabled = true;
                LogError("one or more critical game members could not be resolved — no maps will be registered. " +
                         "This build of Custom Maps does not match this build of the game.");
                return;
            }

            if (!ApplyPatches())
            {
                Disabled = true;
                return;
            }

            // Opening a bundle only maps its header, so this is milliseconds even for a
            // 300 MB map; the terrain deserializes on a background request while the
            // player is still in the menu. Done here rather than lazily so that a
            // missing or corrupt bundle is reported at startup instead of at the moment
            // someone tries to host with it.
            try
            {
                BundleLoader.ScanAndLoad();
            }
            catch (Exception e)
            {
                LogError($"scanning for map bundles failed: {e}");
            }

            LogInfo($"v{Version} ready. Map directories: {string.Join(" | ", ResolveMapDirectories())}");
        }

        /// <summary>
        /// Surfaces a join failure in the UI as well as the log.
        ///
        /// The vanilla path for an unresolvable map is a console error followed by
        /// <c>Client.Disconnect()</c>, which puts the player back at the menu with
        /// nothing on screen to explain it. This is the same channel the build-number
        /// mismatch uses. Headless has no UI, and the singleton may not exist yet, so
        /// every failure here is swallowed — a broken error message must not become a
        /// second error.
        /// </summary>
        internal static void ShowJoinFailure(string message)
        {
            if (GameManager.IsHeadless) return;
            try
            {
                NuclearOption.Networking.Lobbies.FlashErrorMessageSingleton.ShowError(message, 20f);
            }
            catch (Exception e)
            {
                LogDebug($"could not show the error modal: {e.Message}");
            }
        }

        void BindConfig()
        {
            MapDirectories = Config.Bind(
                "General", "MapDirectories", "",
                "Extra directories to scan for .nomap bundles, separated by ';'. These are searched " +
                "before the defaults. Leave empty to use only BepInEx/plugins/NOCustomMaps/maps and " +
                "the CustomMaps folder under the game's persistent data path.");

            DebugLogging = Config.Bind(
                "General", "DebugLogging", false,
                "Verbose logging, prefixed [CM-DBG]. Includes the full map catalogue after every " +
                "change and the MapPrefix of every loaded map.");

            ValidateOnLoad = Config.Bind(
                "General", "ValidateOnLoad", true,
                "Validate each map prefab before registering it, and refuse to register one that " +
                "fails. Turning this off will not make a broken map work; it will move the failure " +
                "from a log line to a hard lock partway through loading.");

            BorrowDetailRenderer = Config.Bind(
                "Advanced", "BorrowDetailRenderer", true,
                "Clone the donor map's DetailRenderer_Base subtree (TerrainHeightMap, DetailRenderer, " +
                "GrassRenderer, TreeRenderer) into custom maps. These are per-map SceneSingletons that " +
                "a bundle cannot author, and without them terrain renders bare — no grass, no trees, " +
                "and no error. Turn off only if a map ships its own.");

            DonorMap = Config.Bind(
                "Advanced", "DonorMap", "Terrain1",
                "PrefabName of the shipped map to copy base-game assets from — the terrain " +
                "PhysicMaterial that TerrainHeightMap and TerrainScatter test against, and the " +
                "faction music array. 'Terrain1' is Heartland, 'Terrain_naval' is Ignus Archipelago.");
        }

        /// <summary>
        /// Search roots for <c>.nomap</c> bundles, highest precedence first.
        ///
        /// The plugin folder comes before the persistent data path deliberately. The
        /// release archive extracts over the game directory, so that is where a server
        /// operator's files land and what they already rsync; a headless server often
        /// runs under a service account whose LocalLow is not the one the person
        /// installing the map was looking at.
        /// </summary>
        internal static List<string> ResolveMapDirectories()
        {
            var dirs = new List<string>();

            string configured = MapDirectories?.Value;
            if (!string.IsNullOrWhiteSpace(configured))
                foreach (string part in configured.Split(';'))
                    if (!string.IsNullOrWhiteSpace(part)) dirs.Add(part.Trim());

            // Both layouts are in the wild and both are reasonable, so both are searched:
            // the DLL dropped straight into BepInEx/plugins/ (which is what the release
            // archive produces), or the DLL in its own subfolder next to a maps/ folder
            // (which is how NOSMR, LoadoutPresets and ConfigurationManager are laid out,
            // and what most people do by hand). Discovery dedupes by full path, so the
            // overlap costs nothing.
            string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(pluginDir))
            {
                dirs.Add(Path.Combine(pluginDir, "maps"));
                dirs.Add(Path.Combine(pluginDir, "NOCustomMaps", "maps"));
            }

            // Application.persistentDataPath is %LOCALAPPDATA%Low\Shockfront\NuclearOption.
            string persistent = Application.persistentDataPath;
            if (!string.IsNullOrEmpty(persistent))
                dirs.Add(Path.Combine(persistent, "CustomMaps"));

            return dirs;
        }

        /// <summary>
        /// Resolves every member the patches and fixups depend on, before Harmony runs.
        ///
        /// Mods break on game updates; the question is only whether they break loudly.
        /// A renamed method would otherwise surface as a Harmony exception buried in a
        /// stack trace, or worse as a patch that silently never applies and a map that
        /// silently never appears. Each miss is named here instead, and a critical miss
        /// takes the whole plugin offline.
        /// </summary>
        static bool AssertPatchTargets()
        {
            bool ok = true;

            // The join path. Without these a custom map cannot load at all.
            ok &= Method(typeof(MapLoader), nameof(MapLoader.CanLoad), critical: true);
            ok &= Method(typeof(MapLoader), nameof(MapLoader.Load), critical: true);
            ok &= Field(typeof(MapLoader), nameof(MapLoader.Maps), critical: true);
            ok &= Method(typeof(MapSettingsManager), nameof(MapSettingsManager.EnableMap), critical: true);
            ok &= Field(typeof(MapSettingsManager), nameof(MapSettingsManager.Maps), critical: true);

            // The BattlefieldGrid fix. Without it two thirds of a 143 km map collapses
            // into the border cells of a grid sized for an 82 km one.
            ok &= Method(typeof(LevelInfo), nameof(LevelInfo.ApplyMapSettings), critical: true);
            ok &= Method(typeof(BattlefieldGrid), nameof(BattlefieldGrid.GenerateGrid), critical: true);

            // Degradations, not failures.
            Method(typeof(MapLoader), nameof(MapLoader.TryGetMapName), critical: false);
            Field(typeof(MapSettings), "factionMusic", critical: false);
            Field(typeof(NuclearOption.Effects.TerrainHeightMap), "terrainPhysicMaterials", critical: false);
            Field(typeof(NuclearOption.Effects.TerrainHeightMap), "terrainMaterials", critical: false);
            Field(typeof(Airbase), AirbaseBuilder.TaxiNetworkField, critical: false);
            Method(typeof(DynamicMap), "MapControls", critical: false);
            Method(typeof(MathExtensions), nameof(MathExtensions.ClampPos), critical: false);
            Field(typeof(GameAssets), "loader", critical: false);
            Field(typeof(GameAssets), nameof(GameAssets.WaterMaterial), critical: false);
            Method(typeof(LandingGear), "FixedUpdate", critical: false);

            return ok;
        }

        /// <summary>
        /// Applies each patch class on its own, rather than through
        /// <c>Harmony.PatchAll</c>.
        ///
        /// <c>PatchAll</c> is all-or-nothing: one class that fails to apply throws out
        /// of the whole call, and the exception names the offending <em>method</em> but
        /// gives no indication of what else did or did not get patched. That turned a
        /// single mistyped parameter name into a total outage of the plugin — including
        /// bundle discovery, which has nothing to do with Harmony at all.
        ///
        /// Patching per class means a failure is named, contained, and only fatal when
        /// the class is actually on the critical path.
        /// </summary>
        static bool ApplyPatches()
        {
            var harmony = new Harmony(Guid);
            bool ok = true;

            // Registration and the BattlefieldGrid fix. Without these a custom map
            // either does not appear or loads into a grid sized for the wrong map.
            ok &= Patch(harmony, typeof(Patches.MapLoaderPatches), critical: true);
            ok &= Patch(harmony, typeof(Patches.EnableMapPatch), critical: true);
            ok &= Patch(harmony, typeof(Patches.ApplyMapSettingsPatch), critical: true);

            // Nothing hooks FactionHQ.AddAirbase any more. The plugin used to stand six hangars
            // and a tower on every custom airbase there at mission start; they were never in the
            // mission, so the mission editor could neither see nor change them. A custom airbase
            // is now furnished the way a base-game one is — by the mission, in the editor.

            // Diagnostics only — turns a silent disconnect into a readable message.
            Patch(harmony, typeof(Patches.JoinFailurePatch), critical: false);

            // The M map's pan limit, sized for 82 km maps, widened for larger ones. Without it
            // the edges of a large map cannot be brought to the middle of the screen.
            Patch(harmony, typeof(Patches.MapPanPatch), critical: false);
            Patch(harmony, typeof(Patches.MapPanClampPatch), critical: false);

            // A wheel's line-cast that ignores triggers, so an aircraft cannot land on a lake's
            // water volumes as on tarmac. Without it lakes still hold water; the gear just rolls
            // on them.
            Patch(harmony, typeof(Patches.LandingGearTriggerPatch), critical: false);

            if (!ok)
                LogError("a critical patch could not be applied, so no maps will be registered. " +
                         "This build of Custom Maps does not match this build of the game.");

            return ok;
        }

        static bool Patch(Harmony harmony, Type patchClass, bool critical)
        {
            try
            {
                harmony.CreateClassProcessor(patchClass).Patch();
                LogDebug($"patched {patchClass.Name}");
                return true;
            }
            catch (Exception e)
            {
                if (critical) LogError($"CRITICAL: {patchClass.Name} failed to apply: {e}");
                else LogWarning($"{patchClass.Name} failed to apply, continuing without it: {e.Message}");
                return !critical;
            }
        }

        static bool Method(Type type, string name, bool critical)
        {
            if (AccessTools.Method(type, name) != null) return true;
            Report(critical, $"method {type.FullName}.{name} not found");
            return !critical;
        }

        static bool Field(Type type, string name, bool critical)
        {
            if (AccessTools.Field(type, name) != null) return true;
            Report(critical, $"field {type.FullName}.{name} not found");
            return !critical;
        }

        static void Report(bool critical, string message)
        {
            if (critical) LogError("CRITICAL: " + message);
            else LogWarning(message + " — the feature depending on it will be skipped");
        }

        internal static void LogInfo(string message) => _log?.LogInfo(message);
        internal static void LogWarning(string message) => _log?.LogWarning(message);
        internal static void LogError(string message) => _log?.LogError(message);

        internal static void LogDebug(string message)
        {
            if (DebugLogging != null && DebugLogging.Value) _log?.LogInfo("[CM-DBG] " + message);
        }
    }
}
