using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace CustomMaps.Patches
{
    /// <summary>
    /// Lets the M map zoom out on a custom map until the whole map shows, where the game stops at
    /// the scale that fits a shipped 81,920 m map (<see cref="MapZoom"/> has the arithmetic and
    /// the game code it comes from). On Swiss Alps the least zoom goes from 1 to about 0.38.
    ///
    /// <c>DynamicMap.MapControls</c> clamps the zoom with <c>Mathf.Clamp(x, 1f, 40f)</c>, and the
    /// 1 is a constant in the IL, so a transpiler swaps that one constant for a call to
    /// <see cref="MapZoomPatch.ZoomFloor"/>: the game's 1 on any map not ours, the custom map's
    /// own floor on ours. Only the floor moves; the step (5% a notch of the wheel) and the
    /// greatest zoom, 40, stay the game's. The mission editor's map is the same
    /// <c>DynamicMap</c>, opened from its Map tab, so it zooms out the same way.
    ///
    /// Nothing else the map draws needs help below 1: the unit, airbase, waypoint and marker
    /// icons and the grid letters are all scaled by <c>1 / mapImage.localScale.x</c> each update,
    /// so they keep their size on screen at any zoom, and the grid letters stand at fixed places
    /// on the frame. The chart is drawn from its mipmaps, so a whole 8192 px chart shown some
    /// 830 px across is not aliased, and the frame holds the same icons it always draws, so a
    /// zoomed-out map costs no more a frame than a zoomed-in one.
    ///
    /// Panning is handled with the pan widening (<see cref="MapPanClampPatch"/>), which narrows
    /// the pan as the zoom goes below 1 so that fully zoomed out the map is centred. The game
    /// stops a map following the aircraft whenever its pan limit holds the view in, which the
    /// narrowed limit nearly always would; <see cref="MapZoomPatch"/>'s postfix keeps it
    /// following (<see cref="MapZoom.KeepsFollowing"/>), so zooming back in finds the aircraft.
    ///
    /// Fails safe: if the method no longer clamps the zoom to exactly <c>1f, 40f</c>, or the
    /// fields it reads are gone (checked in <c>Prepare</c>), it is left as it is and the map
    /// zooms as in the base game. Registered as non-critical.
    /// </summary>
    internal static class MapZooms
    {
        /// <summary>True when the game still has the members the patches use. A field gone from
        /// <c>DynamicMap</c> would otherwise throw from inside <c>MapControls</c> on every scroll.</summary>
        internal static readonly bool Supported = Check();

        /// <summary>True while the map in play is one of ours.</summary>
        internal static bool CustomMap;

        /// <summary>Set during <c>MapControls</c> when the narrowed pan limit is about to hold in a
        /// view that follows the camera, so that <see cref="MapZoomPatch"/>'s postfix puts the
        /// game's pan offset back to zero after it (<see cref="MapZoom.KeepsFollowing"/>).</summary>
        internal static bool KeepFollowing;

        static bool Check()
        {
            FieldInfo image = AccessTools.Field(typeof(DynamicMap), "mapImage");
            FieldInfo background = AccessTools.Field(typeof(DynamicMap), "mapBackground");
            FieldInfo maximized = AccessTools.Field(typeof(DynamicMap), "mapScaleMaximized");
            FieldInfo offset = AccessTools.Field(typeof(DynamicMap), "positionOffset");

            return AccessTools.Method(typeof(DynamicMap), "MapControls") != null
                && AccessTools.PropertyGetter(typeof(DynamicMap), "mapMaximized") != null
                && image != null && image.FieldType == typeof(GameObject)
                && background != null && background.FieldType == typeof(Image)
                && maximized != null && maximized.FieldType == typeof(float)
                && offset != null && offset.FieldType == typeof(Vector2);
        }

        /// <summary>Called when a map is applied: notes whether it is ours, and logs how far out its
        /// M map now zooms.</summary>
        internal static void MapApplied(MapSettings mapSettings)
        {
            KeepFollowing = false;
            CustomMap = !Plugin.Disabled && mapSettings != null && MapFixups.LoadedMapFor(mapSettings) != null;
            if (!CustomMap) return;

            float floor = SafeFloor();
            if (floor < MapZoom.GameFloor)
                Plugin.LogInfo($"{mapSettings.name}: the M map zooms out to {floor.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)} " +
                               "(the game stops at 1), so the whole map shows");
        }

        /// <summary>
        /// The least zoom for the map in play. The frame is read live while the map is open; at
        /// other times (at load, for the log) it is the size <c>Maximize</c> will give it, since
        /// the minimised map's frame is a different size.
        /// </summary>
        internal static float CurrentFloor()
        {
            if (!CustomMap) return MapZoom.GameFloor;

            DynamicMap map = SceneSingleton<DynamicMap>.i;
            if (map == null || map.mapImage == null || map.mapBackground == null) return MapZoom.GameFloor;
            if (!(map.mapImage.transform is RectTransform image)) return MapZoom.GameFloor;

            float frame;
            if (DynamicMap.mapMaximized)
            {
                Rect rect = map.mapBackground.rectTransform.rect;
                frame = Mathf.Min(rect.width, rect.height);
            }
            else
            {
                frame = map.mapScaleMaximized + 20f;
            }

            return MapZoom.Floor(true, frame, image.sizeDelta.x, image.sizeDelta.y);
        }

        /// <summary><see cref="CurrentFloor"/>, or the game's 1 if it throws: the callers run inside
        /// the map's own controls, and a member gone after a game update would throw on every
        /// frame there.</summary>
        internal static float SafeFloor()
        {
            try { return CurrentFloor(); }
            catch { return MapZoom.GameFloor; }
        }

        /// <summary>
        /// The pan limit's <paramref name="factor"/> narrowed for <paramref name="zoom"/> on a custom
        /// map (<see cref="MapZoom.PanFactor"/>), noting whether the view should go on following
        /// the camera. Called from <c>ClampPos</c> inside <c>MapControls</c>. Never throws: on any
        /// failure the factor is left as it was and the game's following is left alone.
        /// </summary>
        internal static float NarrowPan(DynamicMap map, float factor, float zoom)
        {
            KeepFollowing = false;
            try { return Narrow(map, factor, zoom); }
            catch
            {
                KeepFollowing = false;
                return factor;
            }
        }

        static float Narrow(DynamicMap map, float factor, float zoom)
        {
            float narrowed = MapZoom.PanFactor(factor, zoom, CurrentFloor());
            Vector2 offset = map.positionOffset;
            KeepFollowing = MapZoom.KeepsFollowing(factor, narrowed, offset.x, offset.y);
            return narrowed;
        }

        /// <summary>After <c>MapControls</c>: if the narrowed pan limit held in a view that was
        /// following the camera, its pan offset goes back to zero, so it follows on.</summary>
        internal static void RestoreFollowing(DynamicMap map)
        {
            if (!KeepFollowing) return;
            KeepFollowing = false;
            if (map == null) return;
            try { ClearOffset(map); }
            catch { }
        }

        static void ClearOffset(DynamicMap map) => map.positionOffset = Vector2.zero;
    }

    /// <summary>The zoom's floor itself (<see cref="MapZooms"/>).</summary>
    [HarmonyPatch(typeof(DynamicMap), "MapControls")]
    internal static class MapZoomPatch
    {
        /// <summary><c>Mathf.Clamp(float, float, float)</c>, the call the zoom goes through.</summary>
        static readonly MethodInfo Clamp = AccessTools.Method(typeof(Mathf), nameof(Mathf.Clamp),
            new[] { typeof(float), typeof(float), typeof(float) });

        static readonly MethodInfo Floor = AccessTools.Method(typeof(MapZoomPatch), nameof(ZoomFloor));

        static bool Prepare() => MapZooms.Supported;

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            // ldc.r4 1, ldc.r4 40, call Mathf.Clamp: the zoom's clamp and nothing else in the method.
            var floors = new List<int>();
            if (Clamp != null)
                for (int i = 0; i + 2 < code.Count; i++)
                    if (Loads(code[i], 1f) && Loads(code[i + 1], 40f) && code[i + 2].Calls(Clamp))
                        floors.Add(i);

            if (floors.Count != 1 || Floor == null)
            {
                Plugin.LogWarning($"DynamicMap.MapControls clamps to (1, 40) {floors.Count} time(s) where once was expected, " +
                                  "so it is left as it is: the M map zooms out no further on a custom map than on a base map");
                return code;
            }

            // A float for a float: the stack is as it was, and a jump to the constant lands on the call.
            code[floors[0]].opcode = OpCodes.Call;
            code[floors[0]].operand = Floor;
            Plugin.LogDebug("DynamicMap.MapControls: the M map's least zoom now follows the map's size on a custom map");
            return code;
        }

        static bool Loads(CodeInstruction instruction, float value)
            => instruction.opcode == OpCodes.Ldc_R4 && instruction.operand is float f && f == value;

        /// <summary>The least zoom, in place of the game's 1. Only called on a frame the wheel turns.
        /// Never throws: it runs inside the map's own controls.</summary>
        static float ZoomFloor() => MapZooms.SafeFloor();

        /// <summary>Nothing is pending from an earlier frame: a <c>MapControls</c> that threw, or
        /// returned before its pan clamp, leaves its following to the game.</summary>
        static void Prefix() => MapZooms.KeepFollowing = false;

        static void Postfix(DynamicMap __instance) => MapZooms.RestoreFollowing(__instance);
    }

    /// <summary>Tells <see cref="MapZooms"/> which map is in play. A postfix of its own on
    /// <c>ApplyMapSettings</c>, after <c>DynamicMap.LoadMapImage</c> has sized the image, so that
    /// it stands or falls apart from the grid fix there.</summary>
    [HarmonyPatch(typeof(LevelInfo), nameof(LevelInfo.ApplyMapSettings))]
    internal static class MapZoomMapPatch
    {
        static bool Prepare() => MapZooms.Supported;

        static void Postfix(MapSettings mapSettings)
        {
            try { MapZooms.MapApplied(mapSettings); }
            catch (Exception e) { Plugin.LogWarning($"M map zoom: {e.Message}"); }
        }
    }
}
