using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CustomMaps.Patches
{
    /// <summary>
    /// The patches that let AI aircraft and helicopters see a custom map's mountains
    /// (<see cref="AITerrain"/>). Every one is registered as non-critical, checks in its
    /// <c>Prepare</c> that the game still has what it touches, and on the base game's maps returns
    /// before it does anything (<see cref="AITerrainSwitch"/>).
    /// </summary>
    internal static class AITerrainMembers
    {
        static readonly Type[] HeloAim = { typeof(GlobalPosition), typeof(float), typeof(Vector3), typeof(Vector3), typeof(bool) };
        static readonly Type[] Boresight = { typeof(GlobalPosition), typeof(float) };

        internal static readonly MethodInfo TerrainWaypoint = AccessTools.DeclaredMethod(typeof(Autopilot), "TerrainWaypoint",
            new[] { typeof(Vector3), typeof(float), typeof(float) });

        internal static readonly MethodInfo PlaneAim = AccessTools.DeclaredMethod(typeof(AutopilotPlane), "AutoAim",
            new[] { typeof(GlobalPosition), typeof(bool), typeof(bool), typeof(bool), typeof(float), typeof(float), typeof(bool), typeof(float), typeof(Vector3) });

        internal static readonly MethodInfo HelicopterAim = AccessTools.DeclaredMethod(typeof(AutopilotHelo), "AutoAim", HeloAim);
        internal static readonly MethodInfo TiltrotorAim = AccessTools.DeclaredMethod(typeof(AutopilotTiltwing), "AutoAim", HeloAim);
        internal static readonly MethodInfo HelicopterBoresight = AccessTools.DeclaredMethod(typeof(AutopilotHelo), "BoresightAim", Boresight);
        internal static readonly MethodInfo TiltrotorBoresight = AccessTools.DeclaredMethod(typeof(AutopilotTiltwing), "BoresightAim", Boresight);

        internal static readonly MethodInfo RequestLanding = AccessTools.DeclaredMethod(typeof(Airbase), "RequestLanding",
            new[] { typeof(Aircraft), typeof(RunwayQuery) });

        internal static readonly MethodInfo CombatUpdate = AccessTools.DeclaredMethod(typeof(AIPilotCombatModes), "FixedUpdateState",
            new[] { typeof(Pilot) });

        internal static readonly MethodInfo TakeoffUpdate = AccessTools.DeclaredMethod(typeof(AIHeloTakeoffState), "FixedUpdateState",
            new[] { typeof(Pilot) });

        /// <summary>The members every part reads, all public in the game.</summary>
        internal static readonly bool Common = Check();

        static bool Check()
        {
            return Field(typeof(Autopilot), "aircraft", typeof(Aircraft))
                && Field(typeof(Unit), "radarAlt", typeof(float))
                && Field(typeof(Unit), "speed", typeof(float))
                && Field(typeof(Aircraft), "pilots", typeof(Pilot[]))
                && Field(typeof(Pilot), "currentState", typeof(PilotBaseState))
                && AccessTools.Method(typeof(Aircraft), "GetAircraftParameters", Type.EmptyTypes) != null
                && AccessTools.Method(typeof(Aircraft), "GetInputs", Type.EmptyTypes) != null;
        }

        internal static bool Field(Type type, string name, Type fieldType)
        {
            FieldInfo field = AccessTools.Field(type, name);
            return field != null && !field.IsStatic && field.FieldType == fieldType;
        }

        internal static bool Returns(MethodInfo method, Type type) => method != null && method.ReturnType == type;
    }

    /// <summary>Tells <see cref="AITerrain"/> which map is in play. A postfix of its own on
    /// <c>ApplyMapSettings</c>, so that it stands or falls apart from the grid fix there.</summary>
    [HarmonyPatch(typeof(LevelInfo), nameof(LevelInfo.ApplyMapSettings))]
    internal static class AITerrainMapPatch
    {
        static bool Prepare() => AITerrainMembers.Common;

        static void Postfix(MapSettings mapSettings)
        {
            try { AITerrain.MapApplied(mapSettings); }
            catch (Exception e)
            {
                AITerrainSwitch.Active = false;
                Plugin.LogWarning($"AI terrain avoidance: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Every terrain-following AI aircraft's aim point (<c>Autopilot.TerrainWaypoint</c>): the game
    /// samples one point ahead, this the whole way there and as far again.
    /// </summary>
    [HarmonyPatch]
    internal static class TerrainWaypointPatch
    {
        const string Part = "the terrain-following aim point";

        static MethodBase TargetMethod() => AITerrainMembers.TerrainWaypoint;

        static bool Prepare() => AITerrainMembers.Common && AITerrainMembers.Returns(AITerrainMembers.TerrainWaypoint, typeof(GlobalPosition));

        static bool Prefix(Autopilot __instance, Vector3 direction, float altitudeTarget, float lookAheadDistance,
                           ref GlobalPosition __result)
        {
            if (!AITerrainSwitch.Active || __instance == null || AITerrain.Faulted(Part)) return true;
            try
            {
                if (!AITerrain.Waypoint(__instance, direction, altitudeTarget, lookAheadDistance, out GlobalPosition waypoint))
                    return true;
                __result = waypoint;
                return false;
            }
            catch (Exception e)
            {
                AITerrain.Fault(Part, e);
                return true;
            }
        }
    }

    /// <summary>
    /// An AI aeroplane flying straight at a destination rather than following the ground
    /// (<c>AutopilotPlane.AutoAim</c> with <c>followTerrain</c> off): given a floor over the ground
    /// on the way. Without one it flies the line to its destination, a point on the ground, and
    /// meets any ridge between at its own height, with only a five-second terrain warning to save it.
    /// In combat, and in the turn in to land, which flies straight at the pattern's aim point from
    /// up to four turning radii to one side of the runway.
    /// </summary>
    [HarmonyPatch]
    internal static class PlaneFloorPatch
    {
        const string Part = "the floor under straight flight";

        /// <summary>Whether the landing state still has the field that tells its turn in from its
        /// final descent; without it only combat gets a floor.</summary>
        static readonly bool Landing = AITerrainMembers.Field(typeof(AIPilotLandingState), "reachedFinal", typeof(bool));

        static MethodBase TargetMethod() => AITerrainMembers.PlaneAim;

        static bool Prepare() => AITerrainMembers.Common && AITerrainMembers.PlaneAim != null
            && AccessTools.Field(typeof(AircraftParameters), "aircraftGLimit") != null;

        static void Prefix(AutopilotPlane __instance, ref GlobalPosition destination, bool ignoreCollisions, bool runwayAlign,
                           bool followTerrain)
        {
            if (!AITerrainSwitch.Active || followTerrain || runwayAlign || __instance == null || AITerrain.Faulted(Part)) return;
            try { AITerrain.Floor(__instance, ref destination, ignoreCollisions, Landing); }
            catch (Exception e) { AITerrain.Fault(Part, e); }
        }
    }

    /// <summary>
    /// Clears two flags the game's fixed-wing combat AI sets and never clears
    /// (<c>AIPilotCombatModes</c>: one instance for the aircraft's whole life, and
    /// <c>EnterState</c> resets neither).
    ///
    ///  - <c>ignoreCollision</c> switches the terrain warning off. A gun run at an aircraft, or a
    ///    retreat to standoff range, sets it; only the gun run ever clears it, so after either the
    ///    warning stays off through every later mode. Here it is cleared in every mode but those two.
    ///  - <c>followTerrain</c> is left as the last attack had it, off for one that ended lined up, and
    ///    "no target", the mode an aircraft flies to its objective or home in, never sets it. Here
    ///    "no target" follows the ground, as "flying to target" and "breaking off" already do.
    ///
    /// Both are base-game bugs the base maps barely show. They are fixed on custom maps only.
    /// </summary>
    [HarmonyPatch]
    internal static class CombatFlagsPatch
    {
        const string Part = "the combat AI's stuck flags";

        static readonly Type Mode = AccessTools.Inner(typeof(AIPilotCombatModes), "AttackMode");
        static readonly int NoTarget = ModeValue("NoTarget");
        static readonly int FixedGuns = ModeValue("UsingFixedGuns");
        static readonly int Standoff = ModeValue("RetreatStandoff");

        static int ModeValue(string name)
        {
            if (Mode == null || !Mode.IsEnum || Enum.GetUnderlyingType(Mode) != typeof(int) || !Enum.IsDefined(Mode, name))
                return -1;
            return (int)Enum.Parse(Mode, name);
        }

        static MethodBase TargetMethod() => AITerrainMembers.CombatUpdate;

        static bool Prepare() => AITerrainMembers.CombatUpdate != null
            && NoTarget >= 0 && FixedGuns >= 0 && Standoff >= 0
            && AITerrainMembers.Field(typeof(AIPilotCombatModes), "attackMode", Mode)
            && AITerrainMembers.Field(typeof(AIPilotCombatModes), "ignoreCollision", typeof(bool))
            && AITerrainMembers.Field(typeof(AIPilotCombatModes), "followTerrain", typeof(bool));

        static void Prefix(AIPilotCombatModes __instance)
        {
            if (!AITerrainSwitch.Active || __instance == null || AITerrain.Faulted(Part)) return;
            try
            {
                int mode = (int)__instance.attackMode;
                if (mode != FixedGuns && mode != Standoff) __instance.ignoreCollision = false;
                if (mode == NoTarget) __instance.followTerrain = true;
            }
            catch (Exception e) { AITerrain.Fault(Part, e); }
        }
    }

    /// <summary>
    /// The runway end an AI aeroplane is sent to land on (<c>Airbase.RequestLanding</c>): another,
    /// when the game's choice has its glideslope in the ground within the aircraft's approach. The
    /// player's HUD asks too, and keeps the game's answer (<see cref="AITerrain.LandingEnd"/>).
    /// </summary>
    [HarmonyPatch]
    internal static class LandingEndPatch
    {
        const string Part = "the choice of runway end";

        static MethodBase TargetMethod() => AITerrainMembers.RequestLanding;

        static bool Prepare() => AITerrainMembers.Common
            && AITerrainMembers.Returns(AITerrainMembers.RequestLanding, typeof(Airbase.Runway.RunwayUsage?))
            && AccessTools.Field(typeof(Airbase), "runways") != null
            && AccessTools.Method(typeof(Airbase.Runway), "IsSuitable", new[] { typeof(RunwayQuery) }) != null
            && AccessTools.Method(typeof(Airbase.Runway.RunwayUsage), "GetTouchdownPoint", Type.EmptyTypes) != null;

        static void Postfix(Airbase __instance, Aircraft aircraft, RunwayQuery runwayQuery,
                            ref Airbase.Runway.RunwayUsage? __result)
        {
            if (!AITerrainSwitch.Active || !__result.HasValue || aircraft == null || AITerrain.Faulted(Part)) return;
            try { __result = AITerrain.LandingEnd(__instance, aircraft, runwayQuery, __result.Value); }
            catch (Exception e) { AITerrain.Fault(Part, e); }
        }
    }

    /// <summary>
    /// An AI helicopter flying (<c>AutopilotHelo.AutoAim</c> following the ground): its hold raised
    /// in time for the ground ahead, and its speed held to what its climb allows.
    /// </summary>
    [HarmonyPatch]
    internal static class HelicopterPlanPatch
    {
        internal const string Part = "the helicopter climb plan";

        static MethodBase TargetMethod() => AITerrainMembers.HelicopterAim;

        static bool Prepare() => AITerrainMembers.Common && AITerrainMembers.HelicopterAim != null;

        static void Prefix(AutopilotHelo __instance, ref GlobalPosition destination, ref float altitudeHold, bool followTerrain)
        {
            if (!AITerrainSwitch.Active || __instance == null || AITerrain.Faulted(Part)) return;
            try
            {
                // Where it is really going, before any slowing or turning aside, for the waypoint
                // the game works out next whether it follows the ground or not.
                AITerrain.NoteDestination(__instance, destination);
                if (followTerrain) AITerrain.Rotor(__instance, ref destination, ref altitudeHold, altitudeHold, steer: true);
            }
            catch (Exception e) { AITerrain.Fault(Part, e); }
        }
    }

    /// <summary>
    /// An AI helicopter aiming its nose at a target (<c>AutopilotHelo.BoresightAim</c>), which holds
    /// its height over the ground with no look ahead at all: its hold raised as in flight, planned
    /// with its least height rather than the hold it asks for then, which rises with it
    /// (<see cref="RotorPlan.BoresightClearance"/>). Its course is the aim's, so it is not steered.
    /// </summary>
    [HarmonyPatch]
    internal static class HelicopterBoresightPatch
    {
        static MethodBase TargetMethod() => AITerrainMembers.HelicopterBoresight;

        static bool Prepare() => AITerrainMembers.Common && AITerrainMembers.HelicopterBoresight != null
            && AITerrainMembers.Field(typeof(AircraftParameters), "minimumRadarAlt", typeof(float));

        static void Prefix(AutopilotHelo __instance, GlobalPosition aimPoint, ref float altitudeHold)
        {
            if (!AITerrainSwitch.Active || __instance == null || AITerrain.Faulted(HelicopterPlanPatch.Part)) return;
            try
            {
                float clearance = AITerrain.BoresightClearance(__instance, altitudeHold);
                AITerrain.Rotor(__instance, ref aimPoint, ref altitudeHold, clearance, steer: false);
            }
            catch (Exception e) { AITerrain.Fault(HelicopterPlanPatch.Part, e); }
        }
    }

    /// <summary>
    /// An AI tilt-rotor flying (<c>AutopilotTiltwing.AutoAim</c> following the ground): its hold raised
    /// as a helicopter's. It flies more like an aeroplane at speed and governs its speed by its own
    /// distance to go, so it is not steered.
    /// </summary>
    [HarmonyPatch]
    internal static class TiltrotorPlanPatch
    {
        const string Part = "the tilt-rotor climb plan";

        static MethodBase TargetMethod() => AITerrainMembers.TiltrotorAim;

        static bool Prepare() => AITerrainMembers.Common && AITerrainMembers.TiltrotorAim != null;

        static void Prefix(AutopilotTiltwing __instance, GlobalPosition destination, ref float altitudeHold, bool followTerrain)
        {
            if (!AITerrainSwitch.Active || __instance == null || AITerrain.Faulted(Part)) return;
            try
            {
                AITerrain.NoteDestination(__instance, destination);
                if (!followTerrain) return;

                float clearance = ReferenceEquals(TiltrotorBoresightPatch.Aiming, __instance)
                    ? AITerrain.BoresightClearance(__instance, altitudeHold)
                    : altitudeHold;
                AITerrain.Rotor(__instance, ref destination, ref altitudeHold, clearance, steer: false);
            }
            catch (Exception e) { AITerrain.Fault(Part, e); }
        }
    }

    /// <summary>
    /// Marks a tilt-rotor aiming its nose at a target (<c>AutopilotTiltwing.BoresightAim</c>, which
    /// flies through its own <c>AutoAim</c> with the hold <c>AIHeloCombatState</c> asks for then,
    /// its height now plus 2 m), so that <see cref="TiltrotorPlanPatch"/> plans it with its least
    /// height, as <see cref="HelicopterBoresightPatch"/> does a helicopter.
    /// </summary>
    [HarmonyPatch]
    internal static class TiltrotorBoresightPatch
    {
        /// <summary>The tilt-rotor inside its <c>BoresightAim</c> now, if any.</summary>
        internal static Autopilot Aiming;

        static MethodBase TargetMethod() => AITerrainMembers.TiltrotorBoresight;

        static bool Prepare() => AITerrainMembers.Common && AITerrainMembers.TiltrotorBoresight != null
            && AITerrainMembers.TiltrotorAim != null
            && AITerrainMembers.Field(typeof(AircraftParameters), "minimumRadarAlt", typeof(float));

        static void Prefix(AutopilotTiltwing __instance)
        {
            Aiming = AITerrainSwitch.Active ? __instance : null;
        }

        // Runs however the aim ends, so that no later call is taken for an aim.
        static void Finalizer()
        {
            Aiming = null;
        }
    }

    /// <summary>
    /// An AI helicopter that cannot climb off its pad (<c>AIHeloTakeoffState</c>): sent on into
    /// combat flight before the game's 30-second timeout abandons it.
    /// </summary>
    [HarmonyPatch]
    internal static class HelicopterTakeoffPatch
    {
        const string Part = "the high-pad take-off";

        static MethodBase TargetMethod() => AITerrainMembers.TakeoffUpdate;

        static bool Prepare() => AITerrainMembers.Common && AITerrainMembers.TakeoffUpdate != null
            && AITerrainMembers.Field(typeof(AIHeloTakeoffState), "rotorShaft", typeof(RotorShaft))
            && AITerrainMembers.Field(typeof(AIHeloTakeoffState), "maxRPM", typeof(float))
            && AITerrainMembers.Field(typeof(Pilot), "AIHeloCombatState", typeof(AIHeloCombatState))
            && AccessTools.Method(typeof(Pilot), "SwitchState", new[] { typeof(PilotBaseState) }) != null;

        static bool Prefix(AIHeloTakeoffState __instance, Pilot pilot)
        {
            if (!AITerrainSwitch.Active || __instance == null || pilot == null || AITerrain.Faulted(Part)) return true;
            try
            {
                if (!AITerrain.TakeoffStuck(__instance, pilot)) return true;
                pilot.SwitchState(pilot.AIHeloCombatState);
                return false;
            }
            catch (Exception e)
            {
                AITerrain.Fault(Part, e);
                return true;
            }
        }
    }
}
