using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace CustomMaps.Patches
{
    /// <summary>
    /// Keeps a landing gear from rolling on a lake as if it were tarmac.
    ///
    /// A raised lake's water is trigger boxes carrying the game's water PhysicMaterial
    /// (<see cref="MapFixups"/>, <c>BindLakeVolumes</c>): what makes an aircraft that flies into one
    /// ditch, as it would at sea. A landing gear is one line-cast per wheel, in
    /// <c>LandingGear.FixedUpdate</c>:
    ///
    ///     Physics.Linecast(castPoint.position, castPoint.position - castPoint.up * suspensionTravel,
    ///                      out hit, ~(ExclusionZonesMask | IgnoreCollisionsMask))
    ///
    /// the four-argument overload, whose trigger rule is the global one, and the game ships with
    /// <c>queriesHitTriggers</c> on. So the wheel finds the lake's top face, calls it tarmac
    /// (<c>onTarmac</c> is anything that is not the terrain material), and the aircraft lands and
    /// taxis on the water. At sea there is nothing for the ray to meet and it ditches. The call is
    /// swapped here for the same line-cast with <c>QueryTriggerInteraction.Ignore</c>, so the wheel
    /// meets only solid ground: a lake's bed, if the aircraft is already that deep in it.
    ///
    /// A transpiler, and one call, not <c>Physics.queriesHitTriggers</c> switched off around the
    /// method: that setting is global, and the ground vehicles' <c>RaycastCommand</c> jobs, which
    /// run on worker threads while the main thread carries on, read it when they execute, so a
    /// prefix and postfix toggling it would change their answers at random.
    ///
    /// It applies on every map, the shipped ones included, which is safe only because no trigger
    /// the base game ships is anything a wheel should stand on. Checked on 27 September 2026
    /// against the decompiled game and a survey of the colliders in its assets: the gear's mask
    /// already leaves out layers 13 (ExclusionZones) and 15 (IgnoreCollisions), which hold fourteen
    /// of the game's fifteen triggers; the one left is a propeller's disc on layer 0, a trigger only
    /// while it spins (<c>ConstantSpeedProp</c> makes it solid once the blades stop), and no wheel
    /// is meant to roll on a spinning propeller. No code in the game makes a trigger at runtime.
    ///
    /// Fails safe: if the method no longer makes exactly that one call, the method is left as it
    /// is and a warning says so. Registered as non-critical, so a game update that moves the
    /// method costs this and nothing else.
    /// </summary>
    [HarmonyPatch(typeof(LandingGear), "FixedUpdate")]
    internal static class LandingGearTriggerPatch
    {
        /// <summary><c>Physics.Linecast(Vector3, Vector3, out RaycastHit, int)</c>, the call the IL
        /// makes (<c>call bool UnityEngine.Physics::Linecast(Vector3, Vector3, RaycastHit&amp;,
        /// int32)</c>, the only call into <c>Physics</c> in the method).</summary>
        static readonly MethodInfo GameLinecast = AccessTools.Method(typeof(Physics), nameof(Physics.Linecast),
            new[] { typeof(Vector3), typeof(Vector3), typeof(RaycastHit).MakeByRefType(), typeof(int) });

        static readonly MethodInfo IgnoringTriggers = AccessTools.Method(typeof(LandingGearTriggerPatch),
            nameof(LinecastIgnoringTriggers));

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            var calls = new List<int>();
            if (GameLinecast != null)
                for (int i = 0; i < code.Count; i++)
                    if (code[i].Calls(GameLinecast)) calls.Add(i);

            if (calls.Count != 1 || IgnoringTriggers == null)
            {
                Plugin.LogWarning($"LandingGear.FixedUpdate makes {calls.Count} call(s) to Physics.Linecast(Vector3, Vector3, " +
                                  "out RaycastHit, int) where one was expected, so it is left as it is: landing gear " +
                                  "will roll on a custom map's lakes as on tarmac");
                return code;
            }

            // Same arguments, same stack, same result: only the trigger rule differs.
            code[calls[0]].operand = IgnoringTriggers;
            Plugin.LogDebug("LandingGear.FixedUpdate: the wheel's line-cast now ignores triggers, so gear meets no lake");
            return code;
        }

        static bool LinecastIgnoringTriggers(Vector3 start, Vector3 end, out RaycastHit hitInfo, int layerMask)
            => Physics.Linecast(start, end, out hitInfo, layerMask, QueryTriggerInteraction.Ignore);
    }

    /// <summary>
    /// Lets an AI aircraft taxiing at a drawn airfield roll on the field's floor as on paving,
    /// while every other wheel meets it as the grass it is.
    ///
    /// The floor under a drawn field's outline carries the terrain PhysicMaterial
    /// (<see cref="MapFixups"/>, <c>GroundAirfieldFloors</c>), so a wheel on it sinks and the gear of
    /// most aircraft breaks, as on the base game's grass. The base game's AI never rolls there: its
    /// airbases have a taxi network, and <c>AIPilotTaxiState</c> pathfinds along it. A custom airbase
    /// had none until 2026-10 (<see cref="AirbaseBuilder"/>, <c>ClearTaxiNetwork</c>), and without one the taxi state
    /// steers in a straight line (<c>PathfindingAgent.SetMovingTarget</c>) from the hangar to the
    /// runway's threshold, and after landing from the runway to the nearest service point, at 4 to
    /// 15 m/s. Measured on the user's Swiss missions, 126 of 128 such lines leave the paving, so a
    /// heavy AI aircraft would break its gear on the grass, take the damage, and give up
    /// (<c>Taxi_OnTakeDamage</c> sets it disembarking).
    ///
    /// So the one <c>hit.collider.sharedMaterial</c> read in <c>LandingGear.FixedUpdate</c>, the one
    /// that decides <c>onTarmac</c>, goes through <see cref="MaterialUnderWheel"/>: a floor reads as
    /// no material, paving, for an aircraft whose pilot is in <c>AIPilotTaxiState</c> or
    /// <c>AIPilotTakeoffState</c>, and as itself for everyone else. A player, and an AI aircraft
    /// landing or parked, gets the game's own grass; the aircraft the game spares on grass are
    /// spared by their own gear, as before.
    ///
    /// At every drawn field, its taxi network drawn as lanes or not. Every field has one since 2026-10
    /// (<c>AirbaseBuilder.LayOutTaxiNetwork</c>), and its AI keeps to it, but not everywhere: the game
    /// drives the first leg straight from wherever the aircraft stands to the network
    /// (<c>PathfindingAgent.GetStartingWaypoints</c>), and the last from it to the service point, and a
    /// mission's hangars can stand anywhere; the take-off state turns onto the runway from wherever
    /// the taxi state left it, up to 12 m short of the threshold; a mission's own roads, drawn in the
    /// mission editor, lead wherever they were drawn; and a network made from a layout's taxiways or
    /// the default beside the runway has legs across the grass of its own. Grass there would only
    /// ever break an AI aircraft's gear and eject its crew, so a field drawn with lanes keeps the
    /// exemption too: taking it away there, as first built, caught the take-off turn and every field
    /// whose layout was made lanes (reviewed 2026-10-03).
    ///
    /// Fails safe: if the method no longer reads <c>sharedMaterial</c> exactly once, or the gear's
    /// <c>aircraft</c> field is gone, the method is left as it is and <see cref="Applied"/> stays
    /// false, and then the floors are not grounded at all (paving, as before 2026-09-30), since
    /// grass the AI cannot avoid would strand every AI aircraft at a drawn field.
    /// </summary>
    [HarmonyPatch(typeof(LandingGear), "FixedUpdate")]
    internal static class AirfieldTaxiPatch
    {
        /// <summary>True once the method reads the wheel's material through
        /// <see cref="MaterialUnderWheel"/>; <c>GroundAirfieldFloors</c> grounds the floors only then.</summary>
        internal static bool Applied;

        /// <summary><c>Collider.sharedMaterial</c>'s getter, read once in the method, on the collider the
        /// wheel's line-cast met.</summary>
        static readonly MethodInfo SharedMaterial = AccessTools.PropertyGetter(typeof(Collider), nameof(Collider.sharedMaterial));

        static readonly MethodInfo UnderWheel = AccessTools.Method(typeof(AirfieldTaxiPatch), nameof(MaterialUnderWheel));

        /// <summary>The gear's private <c>aircraft</c> field, or null if the game no longer has it.</summary>
        static readonly AccessTools.FieldRef<LandingGear, Aircraft> GearAircraft = GearAircraftRef();

        static AccessTools.FieldRef<LandingGear, Aircraft> GearAircraftRef()
        {
            FieldInfo field = AccessTools.Field(typeof(LandingGear), "aircraft");
            return field != null && field.FieldType == typeof(Aircraft)
                ? AccessTools.FieldRefAccess<LandingGear, Aircraft>(field)
                : null;
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            var reads = new List<int>();
            if (SharedMaterial != null)
                for (int i = 0; i < code.Count; i++)
                    if (code[i].Calls(SharedMaterial)) reads.Add(i);

            if (reads.Count != 1 || UnderWheel == null || GearAircraft == null)
            {
                Applied = false;
                Plugin.LogWarning($"LandingGear.FixedUpdate reads Collider.sharedMaterial {reads.Count} time(s) where once was " +
                                  "expected, or has no 'aircraft' field, so it is left as it is and the airfields' floors " +
                                  "stay paved: AI aircraft could not taxi across a drawn field's grass");
                return code;
            }

            // The collider is on the stack; add the gear and call the helper in place of the getter.
            // A jump to the getter now lands on the added load, which leaves the stack as it was.
            int at = reads[0];
            var loadGear = new CodeInstruction(OpCodes.Ldarg_0);
            code[at].MoveLabelsTo(loadGear);
            code[at].opcode = OpCodes.Call;
            code[at].operand = UnderWheel;
            code.Insert(at, loadGear);

            Applied = true;
            Plugin.LogDebug("LandingGear.FixedUpdate: an AI aircraft taxiing at a drawn airfield meets its floor as paving");
            return code;
        }

        /// <summary>Harmony's last call for the class: if the patch failed after the transpiler ran,
        /// the method is not patched, so the floors must stay paved.</summary>
        static void Cleanup(Exception ex)
        {
            if (ex != null) Applied = false;
        }

        /// <summary>The material the game compares with the terrain's: the collider's own, except an
        /// airfield's floor under an AI aircraft taxiing or lining up, which is paving to it.</summary>
        static PhysicMaterial MaterialUnderWheel(Collider collider, LandingGear gear)
        {
            // A grounded floor carries exactly the terrain PhysicMaterial, so a wheel on anything
            // else, the base game's paving and props included, leaves here with its own material
            // and without the pilot's state or the collider's parents being looked at.
            PhysicMaterial material = collider.sharedMaterial;
            if (material == null || material != MapFixups.TerrainPhysicMaterial || !TaxiingAI(GearAircraft(gear)))
                return material;

            // A floor is known by its mark (AirfieldFloorMark), not by its parents' names, which would
            // make a string on every read, for every wheel, every physics step.
            if (!collider.TryGetComponent(out AirfieldFloorMark _))
                return material;

            return null;
        }

        /// <summary>True while the aircraft's pilot (the first, as the game's own code takes it) is
        /// taxiing, or taking off, under AI control.
        ///
        /// The take-off state is included because the taxi state hands over to it up to 12 m short of
        /// the threshold (<c>WaitingForTakeoffClearance</c> tests for 12 m once a second), and the
        /// thresholds are the very ends of the strips; on the user's missions one line out of 128, at
        /// Zurich, arrives beside the runway's end, and <c>AIPilotTakeoffState</c> turns onto the runway
        /// and opens the throttle from there, on the floor. Damage in that state ejects the crew
        /// (<c>Takeoff_OnTakeDamage</c>). The whole state rather than until its private
        /// <c>startedTakeoffRun</c>: the run starts once the nose points within about 18 degrees of
        /// the aim point, which can still be metres off the paving at full throttle. The state's
        /// own steering holds the aircraft to the runway, and once airborne the wheels meet nothing.</summary>
        static bool TaxiingAI(Aircraft aircraft)
        {
            if (aircraft == null) return false;

            Pilot[] pilots = aircraft.pilots;
            if (pilots == null || pilots.Length == 0 || pilots[0] == null) return false;

            PilotBaseState state = pilots[0].currentState;
            return state is AIPilotTaxiState || state is AIPilotTakeoffState;
        }
    }
}
