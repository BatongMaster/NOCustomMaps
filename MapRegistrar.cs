using System.Collections.Generic;
using NuclearOption.SceneLoading;

namespace CustomMaps
{
    /// <summary>
    /// Appends our maps to the game's two catalogues.
    ///
    /// <c>MapLoader</c> is a ScriptableObject that lives for the whole process;
    /// <c>MapSettingsManager.Maps</c> is per-scene-instance. Rather than guess a
    /// lifecycle hook that populates both at the right moment, every consumer of
    /// either array is prefixed with a call to <see cref="Ensure(MapLoader)"/> or
    /// <see cref="Ensure(MapSettingsManager)"/>. That converges from any entry point,
    /// survives scene reloads, and — the reason it matters — works identically on a
    /// headless dedicated server, which never loads the main menu.
    /// </summary>
    internal static class MapRegistrar
    {
        public static void Ensure(MapLoader loader)
        {
            if (loader == null || Plugin.Disabled) return;

            IReadOnlyList<LoadedMap> maps = BundleLoader.Maps;
            if (maps.Count == 0) return;

            var candidates = new List<MapDetails>(maps.Count);
            foreach (LoadedMap m in maps)
                if (m.Details != null) candidates.Add(m.Details);

            MapDetails[] merged = CatalogMerge.Append(loader.Maps, candidates, d => d != null ? d.PrefabName : null);
            if (ReferenceEquals(merged, loader.Maps)) return;

            loader.Maps = merged;
            Plugin.LogInfo($"MapLoader now offers {merged.Length} map(s): " +
                           string.Join(", ", CatalogMerge.KeysOf(merged, d => d != null ? d.PrefabName : null)));
        }

        public static void Ensure(MapSettingsManager manager)
        {
            if (manager == null || Plugin.Disabled) return;

            IReadOnlyList<LoadedMap> maps = BundleLoader.Maps;
            if (maps.Count == 0) return;

            var candidates = new List<MapSettingsManager.Map>(maps.Count);
            foreach (LoadedMap m in maps)
            {
                if (m.Details == null) continue;

                // Prepared by MapFixups, which runs first from the same prefix. A null
                // here means the prefab failed validation and was deliberately not made
                // registrable — skip it rather than hand EnableMap something it will
                // dereference.
                MapSettings prefab = MapFixups.PreparedPrefabFor(m);
                if (prefab == null) continue;

                candidates.Add(new MapSettingsManager.Map { Details = m.Details, Prefab = prefab });
            }

            MapSettingsManager.Map[] merged = CatalogMerge.Append(
                manager.Maps, candidates, e => e?.Details != null ? e.Details.PrefabName : null);

            if (ReferenceEquals(merged, manager.Maps)) return;

            manager.Maps = merged;
            Plugin.LogDebug($"MapSettingsManager now holds {merged.Length} map(s)");
        }

        /// <summary>Registers into both catalogues, running prefab fixups in between.
        /// Order matters: a prefab that fails preparation must never reach
        /// <c>MapSettingsManager.Maps</c>.</summary>
        public static void EnsureAll(MapSettingsManager manager)
        {
            if (manager == null || Plugin.Disabled) return;

            Ensure(manager.MapLoader);
            MapFixups.PrepareAll(manager);
            Ensure(manager);
        }
    }
}
