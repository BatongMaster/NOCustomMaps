using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace CustomMaps.Patches
{
    /// <summary>
    /// Makes the HUD's landing glideslope at a custom map's airbases ten nautical miles long when it first
    /// shows, which it does about 12 nm from the airbase's flag, where the game's first shows about three
    /// long.
    ///
    /// The line is the game's (<c>AirbaseOverlay</c>, its <c>glideslope</c> image): from the runway's
    /// touchdown point, up the game's 0.06 slope, nine tenths of the way back to the aircraft, shown while
    /// the gear is down and the overlay holds a runway. What makes it short is when the overlay takes that
    /// runway: <c>UpdateNearestAirbase</c> asks the nearest suitable airbase for one only within its
    /// capture range plus 5,000 m of its flag, so the line first shows 2.6 to 3.5 nm long at Swiss Alps'
    /// airbases, as at the base maps' (<see cref="ApproachLine"/> has the code). Nothing in a map sets it.
    ///
    /// A transpiler puts <see cref="ApproachLines.Extra"/> in place of that one <c>5000f</c>. On a custom
    /// map it gives each airbase standing on the map (not a ship's) as much as it takes for an aircraft
    /// crossing the distance to be 10 nm / 0.9, and a little, from every runway's touchdown point; on any
    /// other map, and for a ship's airbase anywhere, it gives back 5,000.
    ///
    /// What else the wider distance reaches. Inside it the overlay's runway is asked for every 2 s by
    /// <c>Airbase.RequestLanding</c>, which picks a runway and changes nothing. "Cleared for landing", the
    /// runway's outline and the server's landing list (<c>CmdRegisterUsage</c>), and "Aborted Landing"
    /// after them, wait, as before, for the aircraft to be within 2,500 m of a runway end
    /// (<c>Runway.AircraftOnApproach</c>), and the game's capture range plus 5 km cut that short only
    /// at a runway end more than capture range plus 2.5 km from the flag. No Swiss Alps airbase has one
    /// (every runway end there is inside the capture range), so there only the HUD's line changes; where
    /// a mission puts a flag that far from a runway, or shrinks the capture range, the game's own
    /// clearance now works on that end too. AI pilots never use the overlay. Each player's own plugin
    /// draws their own HUD, and a player without it sees the game's line.
    ///
    /// The overlay asks the nearest friendly airbase, by flag (<c>FactionHQ.TryGetNearestAirbase</c>),
    /// before the distance is tested. Where two friendly airbases' flags are less than twice this
    /// distance apart, about 45 km, an aircraft coming in from the neighbour's side gets the line only
    /// once past halfway, so it first shows shorter than ten miles, though longer than the game's. On
    /// Swiss Alps the nearest pair, Payerne and Bern, are 45.1 km apart, against 22.5 and 21.9 km.
    ///
    /// Fails safe. The patch is applied only while the game's <c>UpdateNearestAirbase</c> and
    /// <c>DrawGlideslope</c> are the ones it was checked against (<see cref="ApproachLine.VerifiedGameMethods"/>),
    /// and only if the method has exactly the one <c>5000f</c>, right after <c>GetRadius</c>; otherwise it
    /// is left as it is and a warning says so. Registered as non-critical.
    /// </summary>
    internal static class ApproachLines
    {
        /// <summary>True when the running game's methods are the ones the patch was checked against.
        /// Checked before patching (each patch's <c>Prepare</c>).</summary>
        internal static readonly bool Supported = Check();

        /// <summary>True while the map in play is one of ours.</summary>
        internal static bool Active;

        /// <summary>Airbases whose distance has been logged on this map, under <c>DebugLogging</c>.</summary>
        static readonly HashSet<Airbase> Reported = new HashSet<Airbase>();

        /// <summary>Set by a failure in <see cref="Extra"/>, which then gives the game's value until the next map.</summary>
        static bool _threw;

        static bool Check()
        {
            try
            {
                var prints = new List<ulong>();
                foreach (string name in ApproachLine.GameMethods)
                {
                    // Both are in the start-up check of patch targets, which names a missing one.
                    MethodInfo method = AccessTools.Method(typeof(AirbaseOverlay), name);
                    if (method == null) return false;
                    prints.Add(IlFingerprint.OfLoadedMethod(method));
                }

                ulong print = IlFingerprint.OfAll(prints);
                if (print == ApproachLine.VerifiedGameMethods) return true;

                Plugin.LogWarning($"the game's HUD landing overlay (AirbaseOverlay.UpdateNearestAirbase and DrawGlideslope, " +
                                  $"fingerprint {print:X16}) is not the one this build of Custom Maps was checked against; " +
                                  "the landing glideslope keeps the game's length, about 3 nm, on custom maps");
                return false;
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"could not read the game's HUD landing overlay ({e.GetType().Name}: {e.Message}); " +
                                  "the landing glideslope keeps the game's length on custom maps");
                return false;
            }
        }

        /// <summary>Called when a map is applied: the longer line for a custom map, the game's for any other.</summary>
        internal static void MapApplied(MapSettings mapSettings)
        {
            Active = !Plugin.Disabled && MapFixups.IsCustom(mapSettings);
            Reported.Clear();
            _threw = false;

            if (Active)
                Plugin.LogDebug($"approach line: on {mapSettings.name} the HUD's landing glideslope first shows " +
                                $"{ApproachLine.LineLength / 1852f:F0} nm long");
        }

        /// <summary>
        /// What <c>AirbaseOverlay.UpdateNearestAirbase</c> adds to the nearest airbase's capture range in
        /// place of its <c>5000f</c>, for the distance from the flag within which the HUD holds a landing
        /// runway. Called every 2 s while the player is airborne in an aircraft that lands on a runway and
        /// has a friendly airbase to land at.
        /// </summary>
        internal static float Extra(Airbase airbase)
        {
            if (!Active || _threw || airbase == null) return ApproachLine.GameExtra;

            try
            {
                // A ship's airbase keeps the game's, on a custom map's lakes as at sea: its runway moves,
                // and the game's own line is what it is drawn for.
                if (airbase.AttachedAirbase || airbase.center == null) return ApproachLine.GameExtra;

                float farthest = FarthestRunwayEnd(airbase);
                if (farthest < 0f) return ApproachLine.GameExtra;

                float range = airbase.GetRadius();
                float extra = ApproachLine.Extra(range, farthest);

                if (Plugin.DebugEnabled && Reported.Add(airbase))
                {
                    string name = airbase.SavedAirbase != null ? airbase.SavedAirbase.DisplayName : airbase.name;
                    Plugin.LogDebug($"approach line: {name} holds a landing runway from {(range + extra) / 1000f:F1} km of " +
                                    $"its flag, where the game would from {(range + ApproachLine.GameExtra) / 1000f:F1} km " +
                                    $"(capture range {range:N0} m, farthest runway end {farthest:N0} m from the flag)");
                }

                return extra;
            }
            catch (Exception e)
            {
                _threw = true;
                Plugin.LogWarning($"approach line: {e.GetType().Name}: {e.Message}; the game's length for the rest of the map");
                return ApproachLine.GameExtra;
            }
        }

        /// <summary>The distance from the airbase's flag to the farthest end of any of its runways, or -1
        /// when it has none.</summary>
        static float FarthestRunwayEnd(Airbase airbase)
        {
            Airbase.Runway[] runways = airbase.runways;
            if (runways == null) return -1f;

            Vector3 centre = airbase.center.position;
            float farthest = -1f;
            foreach (Airbase.Runway runway in runways)
            {
                if (runway == null || runway.Start == null || runway.End == null) continue;
                farthest = Mathf.Max(farthest, Mathf.Max(Vector3.Distance(centre, runway.Start.position),
                                                         Vector3.Distance(centre, runway.End.position)));
            }

            return farthest;
        }
    }

    /// <summary>Tells <see cref="ApproachLines"/> which map is in play. A postfix of its own on
    /// <c>ApplyMapSettings</c>, so that it stands or falls apart from the rest there.</summary>
    [HarmonyPatch(typeof(LevelInfo), nameof(LevelInfo.ApplyMapSettings))]
    internal static class ApproachLineMapPatch
    {
        static bool Prepare() => ApproachLines.Supported;

        static void Postfix(MapSettings mapSettings)
        {
            try { ApproachLines.MapApplied(mapSettings); }
            catch (Exception e) { Plugin.LogWarning($"approach line: {e.Message}"); }
        }
    }

    /// <summary>
    /// <c>AirbaseOverlay.UpdateNearestAirbase</c>'s range test, from the decompiled game:
    ///
    ///     FastMath.InRange(nearestAirbase.center.position, aircraft.transform.position,
    ///                      nearestAirbase.GetRadius() + 5000f)
    ///
    /// which is, in IL, <c>ldarg.0; ldfld nearestAirbase; callvirt GetRadius; ldc.r4 5000; add</c>. The
    /// constant becomes <c>ldarg.0; ldfld nearestAirbase; call ApproachLines.Extra</c>, which leaves a
    /// float where it left one, and the <c>add</c> puts it on the capture range as before. The method's
    /// two other range constants, its 2,500 m approach tests for "Cleared for landing" and "Aborted
    /// Landing", are not touched.
    /// </summary>
    [HarmonyPatch(typeof(AirbaseOverlay), "UpdateNearestAirbase")]
    internal static class ApproachLinePatch
    {
        static readonly MethodInfo GetRadius = AccessTools.Method(typeof(Airbase), nameof(Airbase.GetRadius));
        static readonly FieldInfo NearestAirbase = AccessTools.Field(typeof(AirbaseOverlay), "nearestAirbase");
        static readonly MethodInfo Extra = AccessTools.Method(typeof(ApproachLines), nameof(ApproachLines.Extra));

        static bool Prepare() => ApproachLines.Supported;

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            var found = new List<int>();
            for (int i = 0; i < code.Count; i++)
                if (code[i].opcode == OpCodes.Ldc_R4 && code[i].operand is float value && value == ApproachLine.GameExtra)
                    found.Add(i);

            bool fits = found.Count == 1 && found[0] > 0 && GetRadius != null && code[found[0] - 1].Calls(GetRadius)
                && NearestAirbase != null && NearestAirbase.FieldType == typeof(Airbase) && Extra != null;
            if (!fits)
            {
                Plugin.LogWarning($"AirbaseOverlay.UpdateNearestAirbase has {found.Count} 5000f constant(s) where one, right after " +
                                  "Airbase.GetRadius, was expected, so it is left as it is: the landing glideslope keeps the " +
                                  "game's length on custom maps");
                return code;
            }

            // Rewritten in place, so anything that jumps to the constant lands on the first of the three.
            int at = found[0];
            code[at].opcode = OpCodes.Ldarg_0;
            code[at].operand = null;
            code.Insert(at + 1, new CodeInstruction(OpCodes.Ldfld, NearestAirbase));
            code.Insert(at + 2, new CodeInstruction(OpCodes.Call, Extra));

            Plugin.LogDebug("AirbaseOverlay.UpdateNearestAirbase: a custom map's airbase holds the HUD's landing runway " +
                            "far enough out for a 10 nm glideslope");
            return code;
        }
    }
}
