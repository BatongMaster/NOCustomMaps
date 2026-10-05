using System;
using HarmonyLib;
using UnityEngine;

namespace CustomMaps.Patches
{
    /// <summary>
    /// Lets a nuclear blast over a built-up part of a custom map find the buildings it is meant
    /// to destroy.
    ///
    /// A nuke's damage comes only from its <c>Shockwave</c>: <c>Missile</c> sends any warhead
    /// over 200 kg there and gives <c>DamageEffects.BlastFrag</c> only the smaller ones. From the
    /// decompiled game, <c>Shockwave.Start</c> lists its targets once:
    ///
    ///     Physics.OverlapSphereNonAlloc(position, blastRadius * 2, colliderBuffer)
    ///
    /// into a static array of 4,096 colliders, and a non-alloc overlap that finds more keeps an
    /// arbitrary 4,096 and drops the rest. The shockwave damages only what it listed, so whatever
    /// was dropped is never touched. A conventional bomb's sphere is a hundred metres or so and
    /// holds a handful of colliders, which is why a GBU finishes the same hangar a nuke leaves
    /// standing.
    ///
    /// On the base maps 4,096 is enough. On Swiss Alps it was not while each of its 67,671 city
    /// buildings had a collider of its own: the strategic warhead logged in September (a scorch
    /// radius of 8,184 m, so a query sphere of 32.7 km radius) took in 22,923 of them around
    /// Zurich Airport against 754 around Meiringen. So Meiringen's hangars were in the list and
    /// Zurich's, as often as not, were not.
    ///
    /// The fix is to make the buffer big enough before the game queries it: a prefix counts the
    /// sphere with one allocating query and, when it holds more than the buffer, replaces the
    /// buffer with one that fits. Custom maps only, because the base maps never fill it; the
    /// game's own array is put back when a base map loads.
    ///
    /// The buildings now collide as one merged collider per 3.12 km tile
    /// (<see cref="CustomMaps.CityColliders"/>), so the same sphere holds a few hundred city
    /// colliders at most and the whole of Swiss Alps about five thousand: this has gone from the
    /// fix Swiss Alps needed to a guard for a denser map, and costs one query per detonation.
    /// </summary>
    internal static class BlastBuffers
    {
        /// <summary>
        /// True when the game still has the members the patches use. Checked before patching
        /// (each patch's <c>Prepare</c>), because a prefix that names a field the game no longer
        /// has throws on entry, on every map, and would take all nuclear damage with it.
        /// </summary>
        internal static readonly bool Supported = Check();

        /// <summary>True while the map in play is one of ours.</summary>
        internal static bool Active;

        /// <summary>The game's own array, kept once replaced so a base map gets it back.</summary>
        internal static Collider[] GameShockwaveBuffer;

        static bool Check()
        {
            var start = AccessTools.Method(typeof(Shockwave), "Start");
            var buffer = AccessTools.Field(typeof(Shockwave), "colliderBuffer");
            var yield = AccessTools.Field(typeof(Shockwave), "yieldKilotons");

            return start != null
                && buffer != null && buffer.IsStatic && buffer.FieldType == typeof(Collider[])
                && yield != null && !yield.IsStatic && yield.FieldType == typeof(float);
        }

        /// <summary>Called when a map is applied: turns the growth on for a custom map, and for
        /// any other map puts the game's own buffer back.</summary>
        internal static void MapApplied(MapSettings mapSettings)
        {
            Active = !Plugin.Disabled && MapFixups.IsCustom(mapSettings);
            if (Active) return;

            if (GameShockwaveBuffer != null) Shockwave.colliderBuffer = GameShockwaveBuffer;
        }

        /// <summary>
        /// Counts the sphere the game is about to query and, when <c>colliderBuffer</c> cannot
        /// hold it all, replaces the buffer with one that can. One extra query per detonation.
        /// Logged at info level because it happens once a detonation and is the line that shows
        /// the fix at work.
        /// </summary>
        internal static void Fit(Vector3 centre, float radius)
        {
            Collider[] buffer = Shockwave.colliderBuffer;
            if (buffer == null) return;

            int found = Physics.OverlapSphere(centre, radius).Length;
            int size = BlastReach.BufferFor(found, buffer.Length);
            if (size <= buffer.Length) return;

            if (GameShockwaveBuffer == null) GameShockwaveBuffer = buffer;
            Shockwave.colliderBuffer = new Collider[size];

            Plugin.LogInfo($"nuclear shockwave: {found:N0} collider(s) within {radius:N0} m, more than " +
                           $"the {buffer.Length:N0} the game's buffer held; buffer grown to {size:N0}");
        }
    }

    /// <summary>Tells <see cref="BlastBuffers"/> which map is in play. A postfix of its own on
    /// <c>ApplyMapSettings</c>, so that it stands or falls apart from the grid fix there.</summary>
    [HarmonyPatch(typeof(LevelInfo), nameof(LevelInfo.ApplyMapSettings))]
    internal static class BlastBufferMapPatch
    {
        static bool Prepare() => BlastBuffers.Supported;

        static void Postfix(MapSettings mapSettings)
        {
            try { BlastBuffers.MapApplied(mapSettings); }
            catch (Exception e) { Plugin.LogWarning($"nuclear shockwave buffer: {e.Message}"); }
        }
    }

    /// <summary>The nuclear shockwave: the damage that knocks buildings down.</summary>
    [HarmonyPatch(typeof(Shockwave), "Start")]
    internal static class ShockwaveBufferPatch
    {
        static bool Prepare() => BlastBuffers.Supported;

        static void Prefix(Shockwave __instance)
        {
            if (!BlastBuffers.Active || __instance == null) return;

            // A failure here must never stop Start, which is what lists the targets at all.
            try
            {
                float radius = BlastReach.ShockwaveRadius(__instance.yieldKilotons);
                if (radius > 0f) BlastBuffers.Fit(__instance.transform.position, radius);
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"nuclear shockwave buffer: {e.Message}");
            }
        }
    }
}
