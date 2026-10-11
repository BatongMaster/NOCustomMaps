using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using NuclearOption.MissionEditorScripts;

namespace CustomMaps.Patches
{
    /// <summary>
    /// Offers the installed custom maps as tiles in the mission editor's New Mission panel, beside the game's
    /// Heartland and Ignus Archipelago, in a list that scrolls (<see cref="MapTileList"/>).
    ///
    /// From the decompiled game, <c>MissionEditorNewMenuV2.Awake</c> ties the panel's saved tiles to
    /// <c>MapLoader.Maps</c> by index and makes none, so a map past the second has never had a tile, and a
    /// mission on a custom map had to have its <c>MapKey.Path</c> set by hand. A postfix, so the game's tiles
    /// are wired as the game wires them before the plugin adds its own, each of which selects its map through
    /// the game's own <c>MapSelected</c>. Create New and Create Default are the game's: they make the key from
    /// the selected map's <c>PrefabName</c>, <c>cm.&lt;mapId&gt;.&lt;hash8&gt;</c> for a custom one, and the load
    /// that follows is the one a hand-set key always went through.
    ///
    /// <c>Awake</c> runs once for each copy of the panel, the main menu's and the editor's, the first time it is
    /// shown; the list's own <c>OnEnable</c> takes every later showing.
    ///
    /// Fails safe. Applied only while the option is on and the game's panel is the one it was checked against
    /// (<see cref="MapPicker.Supported"/>); and the list leaves a panel not laid out as expected as it is.
    /// Registered as non-critical.
    /// </summary>
    [HarmonyPatch(typeof(MissionEditorNewMenuV2), "Awake")]
    internal static class NewMissionMapTilesPatch
    {
        static bool Prepare() => MapPicker.Supported;

        static void Postfix(MissionEditorNewMenuV2 __instance)
        {
            try
            {
                Attach(__instance);
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"New Mission panel: {e.GetType().Name}: {e.Message}; the panel keeps the game's two maps");
            }
        }

        // Apart from the postfix and never inlined into it, so that a game member gone in an update fails
        // when this is compiled, inside the postfix's try, and not the postfix itself.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void Attach(MissionEditorNewMenuV2 menu) => MapTileList.Attach(menu);
    }
}
