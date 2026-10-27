using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace CustomMaps.Patches
{
    /// <summary>
    /// One aircraft at a time on a custom map's runway strip (<see cref="RunwayLock"/>): what the patches
    /// below share. The strip is the runway objects <see cref="TakeoffStripPatch"/> links as one, a runway
    /// and its take-off starts and runways laid over one another; the other half of the lock is that the
    /// plugin builds such an airbase's runways with <c>AllowSimultaneousTakeoff</c> off
    /// (<see cref="AirbaseBuilder"/>), so a take-off holds its strip from the moment it is cleared until it
    /// is off the ground.
    ///
    /// Only runways of an airbase a custom map carries (<see cref="CustomMapAirbase"/>) and its map maker
    /// set to one aircraft per runway are touched, told by that flag being off on the runway: every
    /// runway of the base game has it on, and the plugin turns it off nowhere else. Every other airbase,
    /// those of the base maps, those a mission makes and a custom map's without the setting, is left as
    /// the game has it.
    /// </summary>
    internal static class RunwayLocks
    {
        /// <summary>True when the game queues and releases take-offs as the lock was checked against
        /// (<see cref="RunwayLock.VerifiedQueueMethods"/>).</summary>
        internal static readonly bool QueueSupported = Check(RunwayLock.QueueMethods, RunwayLock.VerifiedQueueMethods,
            "take-off queue (Airbase.Runway, AIPilotTaxiState)",
            "a runway's take-off queue keeps aircraft that are gone or have left it, as the game has it");

        /// <summary>True when the AI's approach is the one the go-around was checked against
        /// (<see cref="RunwayLock.VerifiedApproachMethods"/>).</summary>
        internal static readonly bool ApproachSupported = Check(RunwayLock.ApproachMethods, RunwayLock.VerifiedApproachMethods,
            "AI landing approach (AIPilotLandingState)",
            "an AI landing on a custom map does not go around for an aircraft taking off on its strip");

        /// <summary>Each runway's strip mates, as <see cref="TakeoffStripPatch"/> found them when the airbase
        /// started on the server; a runway alone on its strip has none.</summary>
        static readonly ConditionalWeakTable<Airbase.Runway, Airbase.Runway[]> Strips =
            new ConditionalWeakTable<Airbase.Runway, Airbase.Runway[]>();

        static readonly Airbase.Runway[] None = new Airbase.Runway[0];

        static bool Check(string[][] methods, ulong verified, string what, string otherwise)
        {
            try
            {
                var prints = new List<ulong>();
                foreach (string[] m in methods)
                {
                    Type type = m[1] == "Runway" ? typeof(Airbase.Runway) : AccessTools.TypeByName(m[1]);
                    MethodInfo method = type != null ? AccessTools.Method(type, m[2]) : null;
                    if (method == null)
                    {
                        Plugin.LogWarning($"the game's {what} has no {m[1]}.{m[2]}; {otherwise}");
                        return false;
                    }

                    prints.Add(IlFingerprint.OfLoadedMethod(method));
                }

                ulong print = IlFingerprint.OfAll(prints);
                if (print == verified) return true;

                Plugin.LogWarning($"the game's {what} (fingerprint {print:X16}) is not the one this build of Custom Maps " +
                                  $"was checked against; {otherwise}");
                return false;
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"could not read the game's {what} ({e.GetType().Name}: {e.Message}); {otherwise}");
                return false;
            }
        }

        /// <summary>Whether the runway is one aircraft at a time: on a custom map's airbase, built with
        /// <c>AllowSimultaneousTakeoff</c> off.</summary>
        internal static bool Locked(Airbase.Runway runway)
        {
            if (runway == null || runway.AllowSimultaneousTakeoff) return false;
            Airbase airbase = runway.airbase;
            return airbase != null && airbase.GetComponent<CustomMapAirbase>() != null;
        }

        internal static void SetMates(Airbase.Runway runway, Airbase.Runway[] mates)
        {
            if (runway == null) return;
            Strips.Remove(runway);
            if (mates != null && mates.Length > 0) Strips.Add(runway, mates);
        }

        internal static Airbase.Runway[] Mates(Airbase.Runway runway)
            => runway != null && Strips.TryGetValue(runway, out Airbase.Runway[] mates) ? mates : None;

        /// <summary>What the aircraft at the head of a take-off queue is doing.</summary>
        internal static RunwayLock.Head HeadOf(Aircraft aircraft)
        {
            if (aircraft == null || aircraft.disabled) return RunwayLock.Head.Gone;

            Pilot pilot = aircraft.pilots != null && aircraft.pilots.Length > 0 ? aircraft.pilots[0] : null;
            PilotBaseState state = pilot != null ? pilot.currentState : null;
            return RunwayLock.HeadOf(false, pilot != null, state is AIPilotTaxiState, state is AIPilotTakeoffState,
                                     aircraft.radarAlt);
        }

        static bool LanderHolds(Airbase.Runway runway, Aircraft aircraft)
        {
            if (aircraft == null || aircraft.disabled) return false;

            Pilot pilot = aircraft.pilots != null && aircraft.pilots.Length > 0 ? aircraft.pilots[0] : null;
            return RunwayLock.LanderHolds(false, aircraft.Player != null,
                                          pilot != null && pilot.currentState is AIPilotLandingState,
                                          runway.AircraftOnRunway(aircraft), aircraft.radarAlt);
        }

        /// <summary>
        /// Drops from a custom map's runway what no longer uses it: heads of its take-off queue that are gone
        /// or done with it (<see cref="RunwayLock.Holds"/>), as the game drops a destroyed or disabled head
        /// from its own queue, and aircraft in its landing list that no longer land on it
        /// (<see cref="RunwayLock.LanderHolds"/>). Run before the runway is asked whether it is in use, by
        /// its own aircraft or a linked runway's, so that nothing gone holds a strip for ever.
        /// </summary>
        internal static void Release(Airbase.Runway runway)
        {
            if (runway == null) return;

            Queue<Aircraft> queue = runway.takeoffQueue;
            List<Aircraft> landings = runway.landingList;
            bool queued = queue != null && queue.Count > 0, landing = landings != null && landings.Count > 0;
            if (!queued && !landing || !Locked(runway)) return;

            while (queue != null && queue.Count > 0)
            {
                Aircraft head = queue.Peek();
                RunwayLock.Head use = HeadOf(head);
                if (RunwayLock.Holds(use)) break;

                queue.Dequeue();
                Plugin.LogDebug($"runway lock: {Describe(runway)} released by {(head != null ? head.name : "an aircraft gone")} " +
                                $"at the head of its take-off queue ({use})");
            }

            if (landings == null) return;
            for (int i = landings.Count - 1; i >= 0; i--)
            {
                Aircraft lander = landings[i];
                if (lander == null || LanderHolds(runway, lander)) continue;

                landings.RemoveAt(i);
                Plugin.LogDebug($"runway lock: {Describe(runway)} released by {lander.name}, no longer landing on it");
            }
        }

        /// <summary>Whether another aircraft is landing on the runway or on its strip.</summary>
        internal static bool LandingOnStrip(Airbase.Runway runway, Aircraft querier)
        {
            if (Landing(runway, querier)) return true;
            foreach (Airbase.Runway mate in Mates(runway))
            {
                Release(mate);
                if (Landing(mate, querier)) return true;
            }
            return false;
        }

        static bool Landing(Airbase.Runway runway, Aircraft querier)
        {
            List<Aircraft> landings = runway?.landingList;
            if (landings == null) return false;
            foreach (Aircraft lander in landings)
                if (lander != null && lander != querier && !lander.disabled) return true;
            return false;
        }

        /// <summary>Whether another aircraft is lined up or rolling for take-off on the runway or its strip.</summary>
        internal static bool TakeoffOnStrip(Airbase.Runway runway, Aircraft querier, bool includeSelf)
        {
            if (includeSelf)
            {
                Release(runway);
                if (OnRunway(runway, querier)) return true;
            }

            foreach (Airbase.Runway mate in Mates(runway))
            {
                Release(mate);
                if (OnRunway(mate, querier)) return true;
            }
            return false;
        }

        static bool OnRunway(Airbase.Runway runway, Aircraft querier)
        {
            Queue<Aircraft> queue = runway?.takeoffQueue;
            if (queue == null || queue.Count == 0) return false;
            Aircraft head = queue.Peek();
            return head != null && head != querier && HeadOf(head) == RunwayLock.Head.OnRunway;
        }

        internal static string Describe(Airbase.Runway runway)
        {
            Airbase airbase = runway?.airbase;
            return $"'{(airbase != null ? airbase.name : "?")}' runway {(runway != null ? runway.index + 1 : 0)}";
        }
    }

    /// <summary>
    /// A custom map's runway asked whether another aircraft uses it, for a linked runway's take-off
    /// clearance (<c>IsAvailableForTakeoff</c> over its <c>crossingRunways</c>) or a taxiing aircraft about
    /// to cross it (<c>AircraftApproachingRunwayInUse</c>): first rid of what no longer uses it
    /// (<see cref="RunwayLocks.Release"/>). The game's answer counts a disabled head as using the runway,
    /// and only the runway's own clearance drops one, so a crash at one take-off start held every other
    /// start of its strip until the wreck was gone.
    /// </summary>
    [HarmonyPatch(typeof(Airbase.Runway), nameof(Airbase.Runway.OtherAircraftUsingRunway), new[] { typeof(Aircraft) })]
    internal static class RunwayInUsePatch
    {
        static bool Prepare() => RunwayLocks.QueueSupported;

        static void Prefix(Airbase.Runway __instance)
        {
            try { RunwayLocks.Release(__instance); }
            catch (Exception e) { Plugin.LogWarning($"runway lock: {e.GetType().Name}: {e.Message}"); }
        }
    }

    /// <summary>
    /// A custom map's take-off clearance (<c>Runway.IsAvailableForTakeoff</c>, asked by a taxiing AI once a
    /// second within 100 m of its start): first rid of what no longer uses the runway
    /// (<see cref="RunwayLocks.Release"/>), then, where the game says yes to the head of the queue, held
    /// while another aircraft lands anywhere on the strip or is lined up or rolling on another piece of it
    /// (<see cref="RunwayLock.HeadMayGo"/>). The game asks only the head's own runway in that case. The head
    /// keeps its place and waits, short of the runway, as it waits for any clearance.
    /// </summary>
    [HarmonyPatch(typeof(Airbase.Runway), nameof(Airbase.Runway.IsAvailableForTakeoff), new[] { typeof(Aircraft) })]
    internal static class TakeoffClearancePatch
    {
        static bool Prepare() => RunwayLocks.QueueSupported;

        static void Prefix(Airbase.Runway __instance)
        {
            try { RunwayLocks.Release(__instance); }
            catch (Exception e) { Plugin.LogWarning($"runway lock: {e.GetType().Name}: {e.Message}"); }
        }

        static void Postfix(Airbase.Runway __instance, Aircraft querier, ref bool __result)
        {
            if (!__result || RunwayLocks.Mates(__instance).Length == 0) return;
            try
            {
                if (RunwayLock.HeadMayGo(RunwayLocks.LandingOnStrip(__instance, querier),
                                         RunwayLocks.TakeoffOnStrip(__instance, querier, includeSelf: false))) return;
                __result = false;
            }
            catch (Exception e) { Plugin.LogWarning($"runway lock: {e.GetType().Name}: {e.Message}"); }
        }
    }

    /// <summary>
    /// An AI on a stabilized approach to a custom map's runway, its landing registered (within 2500 m of
    /// touchdown), goes around while an aircraft is lined up or rolling for take-off on its strip and it is
    /// within <see cref="RunwayLock.GoAroundDistance"/> of touchdown: switched to <c>Aborting_Landing</c>, as
    /// the game's own approach does for a landing aircraft ahead that it closes on, which takes its landing
    /// off the list, climbs away and comes round for a new approach. Run after the game's check (every
    /// 2 s), and only while the game left the approach as it was.
    ///
    /// A take-off cleared but still taxiing does not send it round: the clearance above holds that aircraft
    /// short of the runway while the landing is registered. A player's landing cannot be held; a player's
    /// registered landing holds the AI's take-offs as on any airbase.
    /// </summary>
    [HarmonyPatch(typeof(AIPilotLandingState), "StabilizedApproach")]
    internal static class LandingGoAroundPatch
    {
        static bool Prepare() => RunwayLocks.QueueSupported && RunwayLocks.ApproachSupported;

        static void Postfix(AIPilotLandingState __instance, bool checkMode)
        {
            if (!checkMode || __instance == null) return;
            try
            {
                if (__instance.landingMode != AIPilotLandingState.LandingMode.Stabilized_Approach || !__instance.requestedLanding) return;

                Airbase.Runway runway = __instance.runwayUsage.Runway;
                Aircraft aircraft = __instance.aircraft;
                if (runway == null || aircraft == null || !RunwayLocks.Locked(runway)) return;

                bool takeoff = RunwayLocks.TakeoffOnStrip(runway, aircraft, includeSelf: true);
                if (!takeoff) return;

                float distance = FastMath.Distance(__instance.runwayUsage.GetTouchdownPoint(), aircraft.GlobalPosition());
                if (!RunwayLock.GoAround(true, takeoff, distance)) return;

                Plugin.LogDebug($"runway lock: {aircraft.name} goes around {distance:N0} m from touchdown on " +
                                $"{RunwayLocks.Describe(runway)}, an aircraft taking off on its strip");
                __instance.SwitchMode(AIPilotLandingState.LandingMode.Aborting_Landing);
            }
            catch (Exception e) { Plugin.LogWarning($"runway lock: {e.GetType().Name}: {e.Message}"); }
        }
    }
}
