using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace CustomMaps.Patches
{
    /// <summary>
    /// Times <c>FloatingOrigin.OriginShift</c>, and <c>Physics.SyncTransforms</c> inside it, for
    /// <see cref="ShiftTiming"/>. From the decompiled game, the method is
    ///
    ///     if (EditorHandle.DraggingHandle || !ShouldShift(cameraPosition)) return;
    ///     ... root.transform.position -= shift for every root of the active scene ...
    ///     Datum.AfterOriginShift();
    ///     Physics.SyncTransforms();
    ///
    /// called from <c>CameraStateManager.LateUpdate</c> every frame, so the prefix and postfix run
    /// every frame too; they return at once unless a custom map is in play, and the postfix tells a
    /// shift from the usual early return by whether <c>Datum.originPosition</c> moved. The prefix
    /// runs first and the postfix last of any patches on the method, so the time is the whole of it
    /// as the game runs it, other mods' patches included.
    ///
    /// The transpiler puts a call either side of the one <c>Physics.SyncTransforms</c>, which is the
    /// part the city colliders make long. It fails safe: if the method no longer makes exactly that
    /// one call, it is left as it is and the shift is timed whole (the lines say n/a for the
    /// split). Registered as non-critical, and skipped (<c>Prepare</c>) when the game no longer has
    /// the members the patch reads, so a game update costs the timing and nothing else.
    /// </summary>
    [HarmonyPatch(typeof(FloatingOrigin), nameof(FloatingOrigin.OriginShift))]
    internal static class ShiftTimingPatch
    {
        static readonly MethodInfo SyncTransforms = AccessTools.Method(typeof(Physics), nameof(Physics.SyncTransforms), Type.EmptyTypes);
        static readonly MethodInfo SyncStart = AccessTools.Method(typeof(ShiftTiming), nameof(ShiftTiming.SyncStart));
        static readonly MethodInfo SyncEnd = AccessTools.Method(typeof(ShiftTiming), nameof(ShiftTiming.SyncEnd));

        static bool _supported;

        /// <summary>True when the game still has what the prefix and postfix read: the method with
        /// its one <c>Vector3</c>, <c>Datum.originPosition</c>, and the list of roots it moves, without
        /// which Harmony could not hand the postfix its <c>___roots</c>.</summary>
        static bool Prepare()
        {
            FieldInfo roots = AccessTools.Field(typeof(FloatingOrigin), "roots");
            _supported = AccessTools.Method(typeof(FloatingOrigin), nameof(FloatingOrigin.OriginShift), new[] { typeof(Vector3) }) != null
                && AccessTools.PropertyGetter(typeof(Datum), nameof(Datum.originPosition)) != null
                && roots != null && !roots.IsStatic && roots.FieldType == typeof(List<GameObject>);

            if (!_supported)
                Plugin.LogDebug("FloatingOrigin.OriginShift is not as expected, so origin shifts are not timed");
            return _supported;
        }

        [HarmonyPriority(Priority.First)]
        static void Prefix() => ShiftTiming.ShiftStart();

        /// <summary>The private <c>roots</c> is handed over by Harmony, by name, rather than read
        /// through the publicized reference, which is kept to the members the csproj names.</summary>
        [HarmonyPriority(Priority.Last)]
        static void Postfix(List<GameObject> ___roots) => ShiftTiming.ShiftEnd(___roots);

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            var calls = new List<int>();
            if (SyncTransforms != null)
                for (int i = 0; i < code.Count; i++)
                    if (code[i].Calls(SyncTransforms)) calls.Add(i);

            if (calls.Count != 1 || SyncStart == null || SyncEnd == null)
            {
                Plugin.LogDebug($"FloatingOrigin.OriginShift makes {calls.Count} call(s) to Physics.SyncTransforms where one was " +
                                "expected, so the shift is timed whole, without SyncTransforms apart");
                return code;
            }

            // Before the call, taking over anything that jumps to it or opens a block on it; after
            // it, ahead of whatever follows, which only the call itself falls through to.
            int at = calls[0];
            var start = new CodeInstruction(OpCodes.Call, SyncStart);
            code[at].MoveLabelsTo(start);
            code[at].MoveBlocksTo(start);
            code.Insert(at + 1, new CodeInstruction(OpCodes.Call, SyncEnd));
            code.Insert(at, start);
            return code;
        }

        /// <summary>Harmony's last call for the class: the timing goes into the player loop only
        /// once the method is patched.</summary>
        static void Cleanup(Exception ex)
        {
            ShiftTiming.Patched = _supported && ex == null;
        }
    }

    /// <summary>Tells <see cref="ShiftTiming"/> which map is in play. A postfix of its own on
    /// <c>ApplyMapSettings</c>, and the last, so the map it counts is built (its cities come from
    /// the grid fix's postfix) and the timing stands or falls apart from the rest.</summary>
    [HarmonyPatch(typeof(LevelInfo), nameof(LevelInfo.ApplyMapSettings))]
    internal static class ShiftTimingMapPatch
    {
        [HarmonyPriority(Priority.Last)]
        static void Postfix(MapSettings mapSettings)
        {
            try { ShiftTiming.MapApplied(mapSettings); }
            catch (Exception e) { Plugin.LogWarning($"origin shift timing: {e.Message}"); }
        }
    }
}
