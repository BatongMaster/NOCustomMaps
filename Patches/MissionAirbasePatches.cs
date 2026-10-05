using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NuclearOption.MissionEditorScripts;
using NuclearOption.SavedMission;

namespace CustomMaps.Patches
{
    /// <summary>
    /// Has a mission's own flag and capture range win over the map's on a custom map's airbases
    /// (<see cref="MissionAirbases"/>). Every hook returns at once for any other airbase, so the
    /// base-game maps behave exactly as the game has them.
    ///
    /// The work is done in <see cref="MissionAirbases"/> and only called from here, inside a
    /// try: these run inside the game's own mission loading, for every airbase, and a game update
    /// that renames something they touch must cost the feature, not the mission.
    /// </summary>
    [HarmonyPatch(typeof(Airbase))]
    internal static class MissionAirbaseLinkPatch
    {
        /// <summary>The override's own range, before the game copies the map's over it.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(nameof(Airbase.LinkSavedAirbase))]
        static void Link_Prefix(Airbase __instance, SavedAirbase __0, out float __state)
        {
            __state = __0 != null ? __0.CaptureRange : 0f;
            if (!Ours(__instance)) return;
            Try(() => MissionAirbases.BeforeLink(__instance, __0));
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(Airbase.LinkSavedAirbase))]
        static void Link_Postfix(Airbase __instance, SavedAirbase __0, float __state)
        {
            if (!Ours(__instance)) return;
            Try(() => MissionAirbases.AfterLink(__instance, __0, __state));

            // The roads apart from the flag, so that either failing leaves the other working.
            TryRoads(() => MissionAirbaseRoads.AfterLink(__instance, __0));
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(Airbase.UnlinkSavedAirbase))]
        static void Unlink_Postfix(Airbase __instance)
        {
            if (!Ours(__instance)) return;
            Try(() => MissionAirbases.AfterUnlink(__instance));
            TryRoads(() => MissionAirbaseRoads.AfterUnlink(__instance));
        }

        static bool Ours(Airbase airbase)
        {
            try { return MissionAirbases.IsOurs(airbase); }
            catch { return false; }
        }

        internal static void Try(Action action)
        {
            try { action(); }
            catch (Exception e)
            {
                Plugin.LogWarning($"a mission's airbase flag or capture range could not be applied, so the map's " +
                                  $"stands: {e}");
            }
        }

        internal static void TryRoads(Action action)
        {
            try { action(); }
            catch (Exception e)
            {
                Plugin.LogWarning($"a mission's airbase roads could not be applied or edited, so the map's stand: {e}");
            }
        }
    }

    /// <summary>
    /// The mission editor's Roads tool on a custom map's airbase: its roads shown editable, the first
    /// edit making the airbase's override (<see cref="MissionAirbaseRoads"/>). Every hook returns at once
    /// for any other network, so the base game's airbases and the map's roads behave as the game has them.
    /// </summary>
    [HarmonyPatch]
    internal static class MissionAirbaseRoadEditorPatch
    {
        /// <summary>The one overload every other one calls: by the network's name.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(RoadEditor), nameof(RoadEditor.SelectNetwork), new[] { typeof(string), typeof(bool) })]
        static void SelectNetwork_Postfix(RoadEditor __instance, string networkName)
        {
            MissionAirbaseLinkPatch.TryRoads(() => MissionAirbaseRoads.Shown(__instance, networkName));
        }
    }

    /// <summary>
    /// Every way the Roads tool changes a network, each preceded by <see cref="MissionAirbaseRoads.BeforeEdit"/>:
    /// placing a road (from the button that starts it), moving, adding or deleting a point, deleting a
    /// road, and making it a bridge. Patched by name, those the game still has, so one renamed by an
    /// update costs only that edit its override.
    /// </summary>
    [HarmonyPatch]
    internal static class MissionAirbaseRoadEditPatch
    {
        internal static readonly string[] Edits =
        {
            nameof(RoadEditor.EnterPlacingMode), nameof(RoadEditor.MoveRoadPoint), nameof(RoadEditor.InsertPoint),
            nameof(RoadEditor.DeleteSelected), "DeleteRoad", "DeleteSelectedPoints", nameof(RoadEditor.SetBridge),
        };

        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string name in Edits)
            {
                MethodInfo method = AccessTools.Method(typeof(RoadEditor), name);
                if (method != null) yield return method;
            }
        }

        static void Prefix(RoadEditor __instance)
        {
            MissionAirbaseLinkPatch.TryRoads(() => MissionAirbaseRoads.BeforeEdit(__instance));
        }
    }

    /// <summary>An override readied for saving: its roads only when they differ from the map's
    /// (<see cref="MissionAirbaseRoads.BeforeSave"/>).</summary>
    [HarmonyPatch(typeof(SavedAirbase), nameof(SavedAirbase.BeforeSave))]
    internal static class MissionAirbaseRoadSavePatch
    {
        static void Postfix(SavedAirbase __instance)
        {
            MissionAirbaseLinkPatch.TryRoads(() => MissionAirbaseRoads.BeforeSave(__instance));
        }
    }

    /// <summary>
    /// A custom map's airbase selected in the mission editor, by clicking its flag: the move arrows
    /// on the flag, which the game gives no airbase (<c>PositionHandleAllowed</c> is false for every
    /// one), working on the same position as the panel's green handle
    /// (<see cref="MissionAirbases.FlagWrapper"/>). Any other airbase is left as the game has it.
    /// </summary>
    [HarmonyPatch(typeof(AirbaseSelectionDetails))]
    internal static class MissionAirbaseSelectionPatch
    {
        [HarmonyPostfix]
        [HarmonyPatch(MethodType.Constructor, typeof(Airbase))]
        static void Constructor_Postfix(AirbaseSelectionDetails __instance, Airbase __0)
        {
            MissionAirbaseLinkPatch.Try(() =>
            {
                ValueWrapperGlobalPosition flag = MissionAirbases.FlagWrapper(__0);
                if (flag != null) __instance.PositionWrapper = flag;
            });
        }

        [HarmonyPostfix]
        [HarmonyPatch(nameof(AirbaseSelectionDetails.PositionHandleAllowed), MethodType.Getter)]
        static void PositionHandleAllowed_Postfix(AirbaseSelectionDetails __instance, ref bool __result)
        {
            if (__result) return;
            try
            {
                __result = MissionAirbases.IsOurs(__instance.Airbase) &&
                           __instance.PositionWrapper == __instance.Airbase.GetComponent<CustomMapAirbase>().Flag;
            }
            catch { }
        }
    }

    /// <summary>
    /// The flag of a selected custom map airbase counts as selected, so that it is dragged as a
    /// selected unit is. A click lands on the flag's own <c>EditorSelectableProxy</c>, which selects
    /// the airbase, but the editor asks whether the proxy is selected, and the selection's source is
    /// the airbase: so the game read every drag of the flag as a box selection, and every click on it
    /// as a new selection, which rebuilt the airbase panel. Any other airbase is left as it was.
    /// </summary>
    [HarmonyPatch(typeof(UnitSelection), "IsSelected")]
    internal static class MissionAirbaseFlagSelectedPatch
    {
        static void Postfix(UnitSelection __instance, IEditorSelectable __0, ref bool __result)
        {
            if (__result || !(__0 is EditorSelectableProxy proxy)) return;
            try
            {
                __result = proxy.flagOwner is Airbase airbase && MissionAirbases.IsOurs(airbase) &&
                           __instance.SelectionDetails is AirbaseSelectionDetails selected && selected.Airbase == airbase;
            }
            catch { }
        }
    }

    /// <summary>
    /// The mission editor's airbase panel: the flag handle and the capture range slider for a
    /// custom map's airbase, and the record that the mission chose them.
    /// </summary>
    [HarmonyPatch(typeof(AirbasePanel))]
    internal static class MissionAirbasePanelPatch
    {
        [HarmonyPostfix]
        [HarmonyPatch(nameof(AirbasePanel.Setup))]
        static void Setup_Postfix(AirbasePanel __instance, Airbase __0)
        {
            MissionAirbaseLinkPatch.Try(() =>
            {
                if (MissionAirbases.IsOurs(__0)) MissionAirbases.SetUpPanel(__instance, __0);
            });
        }

        [HarmonyPostfix]
        [HarmonyPatch("CaptureRangeChanged")]
        static void CaptureRangeChanged_Postfix(AirbasePanel __instance)
        {
            MissionAirbaseLinkPatch.Try(() => MissionAirbases.RangeSet(__instance));
        }
    }
}
