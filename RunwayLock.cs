using System;
using System.Collections.Generic;

namespace CustomMaps
{
    /// <summary>
    /// One aircraft at a time on a custom map's runway strip, taking off or landing, on an airbase the map
    /// maker set so in Map Forge (<see cref="TaxiNetworkData.OneAircraftPerRunway"/>, off by default): the
    /// decisions, free of game types so the tests run them as the plugin does
    /// (<see cref="Patches.RunwayLocks"/> applies them). Every other airbase shares its runways as the game
    /// does.
    ///
    /// How the game shares a runway, from its code (Airbase+Runway, AIPilotTaxiState, AIPilotTakeoffState,
    /// AIPilotLandingState, AirbaseOverlay; 2026-10-22):
    ///
    ///  - Each runway object keeps a take-off queue and a landing list. A taxiing AI asks
    ///    <c>IsAvailableForTakeoff</c> once a second within 100 m of its start, queues itself when told yes,
    ///    and lines up (the take-off state) within 12 m. The answer is no while the runway has a landing
    ///    registered, yes only to the queue's head while it has one, and otherwise no while this runway or
    ///    any of its <c>crossingRunways</c> has another aircraft landing or at the head of its queue
    ///    (<c>OtherAircraftUsingRunway</c>).
    ///  - The head leaves the queue when it lines up (<c>RegisterStartTakeoff</c>, from the take-off
    ///    state's <c>EnterState</c>) if the runway's <c>AllowSimultaneousTakeoff</c> is on, otherwise when
    ///    it is 1 m off the ground (<c>RegisterTakeoffLeftRunway</c>). Every runway of the base game has it
    ///    on, the donor custom maps clone (airbase_boscali_north) included, read from sharedassets1: so the
    ///    runway counted as free from the moment the first aircraft lined up, and the next was cleared
    ///    onto it while the first was still rolling. On an airbase with the flag the plugin builds every
    ///    runway, take-off starts included, with it off, and that is how the patches tell such a runway.
    ///  - A landing is registered on the runway by the AI within 2500 m of touchdown on a stabilized
    ///    approach, and by a player's HUD (<c>CmdRegisterUsage</c>) when cleared to land; the AI leaves the
    ///    list at its runway exit or on a go-around, the player's HUD only on its own machine. Nothing in
    ///    the game holds a landing for a take-off: <c>RequestLanding</c> never looks at the take-off queue,
    ///    and the approach goes around only for another landing aircraft ahead that it closes on.
    ///  - A head that is gone (destroyed, or <c>disabled</c>) is dropped only by its own runway's
    ///    <c>IsAvailableForTakeoff</c> or <c>DequeueTakeoff</c>; <c>OtherAircraftUsingRunway</c>, which is
    ///    how a linked runway sees it, counts a disabled head as using the runway. A head that took off
    ///    from another runway object than the one it queued on is never dropped at all. Players never queue.
    /// </summary>
    internal static class RunwayLock
    {
        /// <summary>Metres from touchdown within which an AI on a stabilized approach goes around, as the
        /// game's own approach does for traffic ahead, while an aircraft is lined up or rolling for take-off
        /// on its strip. About 20 s out at the AI's approach speeds: checked every 2 s, past the 2500 m at
        /// which the landing is registered, which itself holds every later take-off back.</summary>
        public const float GoAroundDistance = 1500f;

        /// <summary>Metres above the ground at which a take-off has left the runway, the game's own
        /// (<c>AIPilotTakeoffState</c> calls <c>RegisterTakeoffLeftRunway</c> past it).</summary>
        public const float Airborne = 1f;

        /// <summary>Most a runway may turn from another and still be the same strip: two degrees.</summary>
        public const float ParallelSine = 0.035f;

        /// <summary>What the aircraft at the head of a take-off queue is doing.</summary>
        public enum Head
        {
            /// <summary>Destroyed or disabled: crashed, shot, ejected from, returned to its hangar.</summary>
            Gone,
            /// <summary>Taxiing to its start, cleared to take off.</summary>
            Taxiing,
            /// <summary>Lined up or rolling: in the take-off state, on the ground.</summary>
            OnRunway,
            /// <summary>In the take-off state and off the ground: it has left the runway.</summary>
            Airborne,
            /// <summary>Doing anything else: parked after an aborted take-off, flying, landing.</summary>
            Elsewhere,
            /// <summary>No pilot to ask; held as the game holds it.</summary>
            Unknown,
        }

        /// <summary>Whether a head still holds its strip. The game drops a destroyed or disabled head
        /// itself, but only from its own runway's queue; one that has left the runway or the taxi and
        /// take-off states is done with it too.</summary>
        public static bool Holds(Head head) => head == Head.Taxiing || head == Head.OnRunway || head == Head.Unknown;

        /// <summary>The head of the queue from what its aircraft is doing.</summary>
        public static Head HeadOf(bool gone, bool hasPilot, bool taxiing, bool takingOff, float radarAlt)
        {
            if (gone) return Head.Gone;
            if (!hasPilot) return Head.Unknown;
            if (taxiing) return Head.Taxiing;
            if (takingOff) return radarAlt > Airborne ? Head.Airborne : Head.OnRunway;
            return Head.Elsewhere;
        }

        /// <summary>
        /// Whether an aircraft in a runway's landing list still has the runway. An AI keeps it while in
        /// its landing state, which takes it off the list itself at its exit or on a go-around; out of it,
        /// only while still on the runway (it can leave that state without leaving the list: rolling out
        /// slower than 15 m/s before an exit is found, or breaking off for a missile). A player keeps it
        /// while in the air or on the runway, the rule their own HUD applies on their machine, which the
        /// host never hears of.
        /// </summary>
        public static bool LanderHolds(bool gone, bool player, bool landing, bool onRunway, float radarAlt)
        {
            if (gone) return false;
            if (onRunway) return true;
            if (player) return radarAlt > Airborne;
            return landing;
        }

        /// <summary>
        /// Whether the head of a runway's queue, told yes by the game, may go: not while another aircraft
        /// is landing anywhere on its strip, or lined up or rolling on another runway object of it. The
        /// game asks only the head's own runway in this case, which on a strip built of several runway
        /// objects (a runway and its take-off starts) is one piece of it. The head keeps its place.
        /// </summary>
        public static bool HeadMayGo(bool landingOnStrip, bool otherOnRunway) => !landingOnStrip && !otherOnRunway;

        /// <summary>Whether an AI on a stabilized approach, its landing registered, goes around: an
        /// aircraft is lined up or rolling for take-off on its strip and it is within
        /// <see cref="GoAroundDistance"/> of touchdown.</summary>
        public static bool GoAround(bool registered, bool takeoffOnRunway, float distanceToTouchdown)
            => registered && takeoffOnRunway && distanceToTouchdown <= GoAroundDistance;

        /// <summary>
        /// Whether two runways are one strip of tarmac: within <see cref="ParallelSine"/> of parallel, each
        /// centreline within half the wider one's width of the other's, and overlapping along it. Two
        /// runways that only cross are not (the game's <c>FindCrossingRunways</c> links those), nor are
        /// parallel runways side by side.
        /// </summary>
        public static bool OneStrip(in RunwayLine a, float widthA, in RunwayLine b, float widthB)
        {
            double ax = a.EndX - a.StartX, az = a.EndZ - a.StartZ;
            double bx = b.EndX - b.StartX, bz = b.EndZ - b.StartZ;
            double la = Math.Sqrt(ax * ax + az * az), lb = Math.Sqrt(bx * bx + bz * bz);
            if (!(la > 1.0) || !(lb > 1.0)) return false;

            ax /= la; az /= la;
            double sine = Math.Abs(ax * (bz / lb) - az * (bx / lb));
            if (!(sine <= ParallelSine)) return false;

            double reach = Math.Max(Math.Max(widthA, widthB), 0f) * 0.5;
            float sx = a.StartX, sz = a.StartZ;
            double Off(float x, float z) => Math.Abs((x - sx) * az - (z - sz) * ax);
            if (!(Off(b.StartX, b.StartZ) <= reach) || !(Off(b.EndX, b.EndZ) <= reach)) return false;

            double Along(float x, float z) => (x - sx) * ax + (z - sz) * az;
            double t0 = Along(b.StartX, b.StartZ), t1 = Along(b.EndX, b.EndZ);
            double lo = Math.Max(0.0, Math.Min(t0, t1)), hi = Math.Min(la, Math.Max(t0, t1));
            return hi - lo > 1.0;
        }

        /// <summary>
        /// For each runway of an airbase, every other runway on its strip: a runway's take-off starts
        /// (<see cref="TaxiPlans.SharedStrips"/>) and runways laid over one another
        /// (<see cref="OneStrip"/>), and everything those share theirs with. Ascending; empty for a runway
        /// alone on its strip, which is every runway of an airbase without starts or overlaps.
        /// </summary>
        /// <param name="widths">Each runway's width; a missing one is taken as 0.</param>
        public static List<int>[] StripMates(IReadOnlyList<RunwayLine> runways, IReadOnlyList<float> widths)
        {
            int count = runways?.Count ?? 0;
            var group = new int[count];
            for (int i = 0; i < count; i++) group[i] = i;

            int Find(int i)
            {
                while (group[i] != i) i = group[i] = group[group[i]];
                return i;
            }

            void Join(int i, int j) => group[Find(i)] = Find(j);

            if (count > 0)
            {
                List<int>[] shared = TaxiPlans.SharedStrips(runways);
                for (int i = 0; i < count; i++)
                    foreach (int j in shared[i]) Join(i, j);

                for (int i = 0; i < count; i++)
                    for (int j = i + 1; j < count; j++)
                    {
                        if (!runways[i].Landing || !runways[j].Landing) continue;
                        float wi = widths != null && i < widths.Count ? widths[i] : 0f;
                        float wj = widths != null && j < widths.Count ? widths[j] : 0f;
                        if (OneStrip(runways[i], wi, runways[j], wj)) Join(i, j);
                    }
            }

            var mates = new List<int>[count];
            for (int i = 0; i < count; i++)
            {
                mates[i] = new List<int>();
                for (int j = 0; j < count; j++)
                    if (j != i && Find(j) == Find(i)) mates[i].Add(j);
            }

            return mates;
        }

        /// <summary>
        /// The fingerprint (<see cref="IlFingerprint"/>) of <see cref="QueueMethods"/>, in the build the lock
        /// was checked against: how the game queues, clears and releases a take-off. The patches that read
        /// the queue are applied only while the running game's is this one.
        /// </summary>
        public const ulong VerifiedQueueMethods = 0x2FB13ABD2B5BF08DUL;

        /// <summary>What <see cref="VerifiedQueueMethods"/> covers, as namespace, type and method, combined in
        /// this order with <see cref="IlFingerprint.OfAll"/>.</summary>
        public static readonly string[][] QueueMethods =
        {
            new[] { "", "Runway", "IsAvailableForTakeoff" },
            new[] { "", "Runway", "OtherAircraftUsingRunway" },
            new[] { "", "Runway", "RegisterStartTakeoff" },
            new[] { "", "Runway", "RegisterTakeoffLeftRunway" },
            new[] { "", "AIPilotTaxiState", "WaitingForTakeoffClearance" },
        };

        /// <summary>
        /// The fingerprint of <see cref="ApproachMethods"/>, in the build the go-around was checked against:
        /// where the AI's approach registers its landing, and what switching to <c>Aborting_Landing</c> does
        /// (the landing taken off the list, a climb away, and a new approach later).
        /// </summary>
        public const ulong VerifiedApproachMethods = 0xB095F1CDB4FB1A02UL;

        /// <summary>What <see cref="VerifiedApproachMethods"/> covers, as <see cref="QueueMethods"/>.</summary>
        public static readonly string[][] ApproachMethods =
        {
            new[] { "", "AIPilotLandingState", "StabilizedApproach" },
            new[] { "", "AIPilotLandingState", "SwitchMode" },
            new[] { "", "AIPilotLandingState", "AbortingLanding" },
        };
    }
}
