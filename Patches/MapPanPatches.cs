using System;
using HarmonyLib;
using UnityEngine;

namespace CustomMaps.Patches
{
    /// <summary>
    /// Lets the M map pan as far past a large map's edge as it pans past a shipped map's.
    ///
    /// <c>DynamicMap.MapControls</c> keeps the point the map is centred on inside a fixed frame:
    ///
    ///     ((RectTransform)mapBackground.transform).rect.ClampPos(ref pos, 2f);
    ///
    /// where <c>pos</c> is in the map image's own units, and the image is
    /// <c>MapSize / 81920 * 900</c> of them wide (<c>DynamicMap.LoadMapImage</c>). The frame is
    /// sized for the shipped maps, 81,920 m and 900 units across: it lets their centre go well
    /// past their edge. Swiss Alps is 199,680 m, 2,194 units, and the same frame stops the
    /// centre about 36 km short of every edge. Zoomed in, Geneva, 5.6 km from the west edge,
    /// could never be brought out from under the grid labels and the side of the screen.
    ///
    /// The frame is widened by however much further the map's half-width reaches than a
    /// shipped map's, on each axis, so a larger map keeps the same room past its edge that a
    /// shipped one has. A map no wider than 81,920 m is left exactly as the game has it.
    ///
    /// <c>ClampPos</c> is a general helper, so the widening applies only while
    /// <c>MapControls</c> is running, the one call site this is about.
    /// </summary>
    [HarmonyPatch(typeof(DynamicMap), "MapControls")]
    internal static class MapPanPatch
    {
        /// <summary>True while <c>DynamicMap.MapControls</c> is on the stack.</summary>
        internal static bool InMapControls;

        static void Prefix() => InMapControls = true;

        static Exception Finalizer(Exception __exception)
        {
            InMapControls = false;
            return __exception;
        }
    }

    /// <summary>The widening itself (<see cref="MapPanPatch"/>).</summary>
    [HarmonyPatch(typeof(MathExtensions), nameof(MathExtensions.ClampPos))]
    internal static class MapPanClampPatch
    {
        /// <summary>Half the map image's width, in its own units, on a shipped 81,920 m map.</summary>
        const float ShippedHalf = 450f;

        static void Prefix(Rect rect, ref float factor)
        {
            if (!MapPanPatch.InMapControls) return;

            DynamicMap map = SceneSingleton<DynamicMap>.i;
            if (map == null || map.mapImage == null) return;
            if (!(map.mapImage.transform is RectTransform image)) return;

            Vector2 beyond = image.sizeDelta * 0.5f - new Vector2(ShippedHalf, ShippedHalf);
            float widened = factor;

            if (beyond.x > 0f && rect.xMax > 0f) widened = Mathf.Max(widened, (factor * rect.xMax + beyond.x) / rect.xMax);
            if (beyond.y > 0f && rect.yMax > 0f) widened = Mathf.Max(widened, (factor * rect.yMax + beyond.y) / rect.yMax);

            factor = widened;
        }
    }
}
