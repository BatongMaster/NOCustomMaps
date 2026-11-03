using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace CustomMaps.Patches
{
    /// <summary>
    /// One log line for each AI aircraft that stops short at a custom map's airbase, saying why, as the
    /// game's taxi code gives no sign of it. An aircraft that has landed stops for good when it reaches its
    /// service point (expected, and not reported) or on any of the game's ways of giving up: no exit ahead
    /// when it starts to taxi, damage, stuck timers, or yielding to another aircraft (all
    /// <c>AIPilotTaxiState.Disembark</c>), or ten seconds on the ground in the landing state and stopped.
    /// One bound for take-off instead holds where it is (<c>AIPilotTaxiState.Wait</c>).
    /// </summary>
    [HarmonyPatch]
    internal static class TaxiReportPatches
    {
        /// <summary>Seconds an aircraft holds on a runway before it is reported.</summary>
        const float LongWait = 30f;

        sealed class Seen
        {
            public bool Stopped, Waited;
            public float WaitingSince = -1f;
        }

        static readonly ConditionalWeakTable<Aircraft, Seen> Aircrafts = new ConditionalWeakTable<Aircraft, Seen>();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AIPilotTaxiState), "Disembark")]
        static void Disembark(AIPilotTaxiState __instance)
        {
            Safely(() =>
            {
                Aircraft aircraft = __instance.aircraft;
                Airbase airbase = __instance.airbase;
                if (aircraft == null || !MissionAirbases.IsOurs(airbase)) return;

                Transform to = __instance.destinationPoint;
                if (to != null && !__instance.toRunway &&
                    Vector3.Distance(to.position, aircraft.transform.position) < aircraft.maxRadius * 2f) return;

                Seen seen = Aircrafts.GetOrCreateValue(aircraft);
                if (seen.Stopped) return;
                seen.Stopped = true;

                string why = __instance.yielding ? "yielded to another aircraft"
                           : __instance.stuckTimerSpeedPercent >= 1f ? "stuck: too slow for 30 s"
                           : __instance.stuckTimerYawPercent >= 1f ? "stuck: steering one way for 60 s"
                           : __instance.disembarking ? "no exit ahead, off the runway, or damaged"
                           : "no destination";
                Plugin.LogInfo($"taxi report: {Name(aircraft)} gave up taxiing ({why}) at '{airbase.SavedAirbase?.UniqueName}', {Where(airbase, aircraft)}, " +
                               $"{Target(__instance)}, speed {aircraft.speed:0.0} m/s");
            });
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AIPilotTaxiState), "Wait")]
        static void Wait(AIPilotTaxiState __instance)
        {
            Safely(() =>
            {
                Aircraft aircraft = __instance.aircraft;
                Airbase airbase = __instance.airbase;
                if (aircraft == null || !MissionAirbases.IsOurs(airbase)) return;

                Seen seen = Aircrafts.GetOrCreateValue(aircraft);
                if (seen.Waited) return;

                if (!airbase.AircraftIsOnRunway(aircraft, false, out _))
                {
                    seen.WaitingSince = -1f;
                    return;
                }

                if (seen.WaitingSince < 0f) seen.WaitingSince = Time.timeSinceLevelLoad;
                if (Time.timeSinceLevelLoad - seen.WaitingSince < LongWait) return;

                seen.Waited = true;
                string why = __instance.waitingForTakeoffClearance ? "waiting for take-off clearance" : "waiting at a runway crossing";
                Plugin.LogInfo($"taxi report: {Name(aircraft)} has held on a runway for {LongWait:0} s ({why}) at " +
                               $"'{airbase.SavedAirbase?.UniqueName}', {Where(airbase, aircraft)}, {Target(__instance)}");
            });
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.StartEjectionSequence))]
        static void Ejecting(Aircraft __instance)
        {
            Safely(() =>
            {
                if (__instance.pilots == null || __instance.pilots.Length == 0) return;
                if (!(__instance.pilots[0]?.currentState is AIPilotLandingState landing)) return;

                Airbase airbase = landing.airbase;
                if (!MissionAirbases.IsOurs(airbase)) return;

                Seen seen = Aircrafts.GetOrCreateValue(__instance);
                if (seen.Stopped) return;
                seen.Stopped = true;

                Plugin.LogInfo($"taxi report: {Name(__instance)} stopped while still landing, before it ever taxied, at " +
                               $"'{airbase.SavedAirbase?.UniqueName}', {Where(airbase, __instance)}");
            });
        }

        static string Name(Aircraft aircraft) => aircraft.name.Replace("(Clone)", "").Trim();

        static string Where(Airbase airbase, Aircraft aircraft)
        {
            if (!airbase.AircraftIsOnRunway(aircraft, false, out Airbase.Runway runway)) return "not on a runway";

            Vector3 along = aircraft.transform.position - runway.Start.position;
            float from = Vector3.Dot(along, runway.GetDirection(false).normalized);
            float length = Vector3.Distance(runway.Start.position, runway.End.position);
            return $"on runway {runway.GetName(false)} {from:0} m of {length:0} m along";
        }

        static string Target(AIPilotTaxiState state)
        {
            Aircraft aircraft = state.aircraft;
            string text = state.toRunway ? "bound for take-off" : "bound for its service point";
            if (state.exitTaxiPoint != null)
                text += $", exit {Vector3.Distance(state.exitTaxiPoint.position, aircraft.transform.position):0} m away";
            if (state.destinationPoint != null)
                text += $", destination {Vector3.Distance(state.destinationPoint.position, aircraft.transform.position):0} m away";
            return text;
        }

        // A report must never be what stops an aircraft.
        static void Safely(Action report)
        {
            try { report(); }
            catch (Exception e) { Plugin.LogDebug($"taxi report: {e.GetType().Name}: {e.Message}"); }
        }
    }
}
