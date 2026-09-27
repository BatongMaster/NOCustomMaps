using HarmonyLib;
using NuclearOption.SceneLoading;

namespace CustomMaps.Patches
{
    /// <summary>
    /// Keeps <c>MapLoader.Maps</c> populated, from whichever entry point comes first.
    ///
    /// A prefix on every consumer rather than a hook on some lifecycle method,
    /// because there is no lifecycle method that is guaranteed to run in all three
    /// contexts that matter: a client at the main menu, a listen server, and a
    /// headless dedicated server — which never loads the main menu at all. That last
    /// case is why NOMapLoader, which injects from a <c>MainMenu.Start</c> postfix,
    /// cannot host a custom map: the server's <c>CanLoad</c> returns false and every
    /// joining client is disconnected.
    ///
    /// <c>MapRegistrar.Ensure</c> is idempotent and returns immediately when there is
    /// nothing to add, so the cost on the steady-state path is a list scan.
    /// </summary>
    [HarmonyPatch(typeof(MapLoader))]
    internal static class MapLoaderPatches
    {
        [HarmonyPrefix]
        [HarmonyPatch(nameof(MapLoader.CanLoad))]
        static void CanLoad_Prefix(MapLoader __instance) => MapRegistrar.Ensure(__instance);

        /// <summary>Runs on the host before it advertises the lobby's map name.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(MapLoader.TryGetMapName))]
        static void TryGetMapName_Prefix(MapLoader __instance) => MapRegistrar.Ensure(__instance);

        /// <summary>Runs on the server before the scene load and on the client when it
        /// handles <c>LoadMapMessage</c>.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(MapLoader.Load))]
        static void Load_Prefix(MapLoader __instance) => MapRegistrar.Ensure(__instance);
    }

    /// <summary>
    /// Explains a failed map lookup instead of letting it become a silent kick.
    ///
    /// When a client cannot resolve the server's map, <c>ClientLoadScene</c> logs
    /// <c>Load {key} failed with result=InvalidKey</c> to the console and calls
    /// <c>Client.Disconnect()</c> — the player is dropped to the menu with nothing on
    /// screen. Since a custom map's name embeds its bundle hash, we can tell "you do
    /// not have this map" from "you have a different build of it", which are very
    /// different problems for whoever is trying to join.
    ///
    /// Separate from <see cref="MapLoaderPatches"/> on purpose: this is a nicety, and
    /// it must not be able to take the join path down with it if it fails to apply.
    /// It can only help a modded client; a vanilla one does not run this code.
    /// </summary>
    [HarmonyPatch(typeof(MapLoader), nameof(MapLoader.CanLoad))]
    internal static class JoinFailurePatch
    {
        // __0 is the first argument by position, not by name.
        //
        // Harmony injects named parameters by matching the *original method's*
        // parameter names, and this game is not consistent about them:
        // Load(MapKey mapKey) but CanLoad(MapKey key) and TryGetMapName(MapKey key, ...).
        // Guessing wrong throws at patch time, so positional injection is used here —
        // it also survives a rename in a future game update, which a name cannot.
        static void Postfix(MapKey __0, bool __result)
        {
            if (__result || __0.Type != MapKey.KeyType.GameWorldPrefab) return;

            string wantedId = MapIdentity.IdOf(__0.Path);
            if (wantedId == null) return;                     // not one of ours

            string wantedHash = MapIdentity.HashOf(__0.Path);
            string mine = null;
            foreach (LoadedMap map in BundleLoader.Maps)
            {
                if (MapIdentity.IdOf(map.PrefabName) != wantedId) continue;
                mine = MapIdentity.HashOf(map.PrefabName);
                break;
            }

            string message = mine == null
                ? $"This server is running the custom map '{wantedId}', which you do not have installed."
                : $"Custom map '{wantedId}' version mismatch — the server has build {wantedHash}, you have {mine}. " +
                  "You need the identical .nomap file.";

            Plugin.LogError(message);
            Plugin.ShowJoinFailure(message);
        }
    }
}
