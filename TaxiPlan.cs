using System;
using System.Collections.Generic;

namespace CustomMaps
{
    /// <summary>An exit transform to make: where, and which way it faces in map metres (x east, z north).</summary>
    internal struct PlannedExit
    {
        public float X, Z;
        public float FacingX, FacingZ;

        /// <summary>For an aircraft rolling from the runway's start towards its end.</summary>
        public bool Forward;
    }

    /// <summary>
    /// A take-off-only runway to build for a take-off start (<see cref="TaxiTakeoff"/>), as the base
    /// game's <c>runway14LTakeoffShort</c> is built on Ignus' <c>airbase_NW</c>: its start where the
    /// taxiway joins the runway, on the centreline, its end the end of the runway it takes off towards.
    /// In map metres.
    /// </summary>
    internal struct PlannedTakeoff
    {
        /// <summary>The runway it is on, by the placement's numbering: 0 the main one.</summary>
        public int Runway;

        public float StartX, StartZ, EndX, EndZ;

        /// <summary>Taking off towards the runway's end, rather than towards its start.</summary>
        public bool TowardsEnd;

        /// <summary>Metres of runway ahead of it.</summary>
        public float Run;

        /// <summary>Metres it was moved square onto the centreline.</summary>
        public float Moved;
    }

    /// <summary>A runway as the take-off starts are told from the rest by: its two thresholds, x and z,
    /// and whether it takes landings.</summary>
    internal struct RunwayLine
    {
        public float StartX, StartZ, EndX, EndZ;
        public bool Landing;
    }

    /// <summary>
    /// What the plugin builds into one airbase from its taxi lanes (<see cref="TaxiData"/>): the roads
    /// of its <c>taxiNetwork</c>, each runway's exit transforms, where its service point and
    /// landing pad go, and a take-off-only runway for each take-off start. Free of game types, so the
    /// tests run it as the plugin does.
    /// </summary>
    internal sealed class TaxiPlan
    {
        public readonly List<(float X, float Z)[]> Roads = new List<(float X, float Z)[]>();

        /// <summary>The take-off runways to build, in the order the map gives the starts; empty for a
        /// map without them, whose airbases are built exactly as before.</summary>
        public readonly List<PlannedTakeoff> Takeoffs = new List<PlannedTakeoff>();

        /// <summary>Why each take-off start the map gives and the plugin leaves out was left out.</summary>
        public readonly List<string> DroppedTakeoffs = new List<string>();

        /// <summary>Each runway's exits, by the placement's numbering: 0 the main runway, then the extra
        /// runways in order. An entry is empty for a runway the data gave none.</summary>
        public List<PlannedExit>[] Exits = new List<PlannedExit>[0];

        public (float X, float Z)? ServicePoint, LandingPad;

        /// <summary>Exits the data put on a runway the airbase does not have, left out.</summary>
        public int DroppedExits;

        public bool HasNetwork => Roads.Count > 0;

        public int ExitCount
        {
            get
            {
                int count = 0;
                foreach (List<PlannedExit> exits in Exits) count += exits.Count;
                return count;
            }
        }
    }

    internal static class TaxiPlans
    {
        /// <summary>
        /// Each airbase's lanes by its unique name, as the placements name them. A name given twice
        /// keeps its first: the export writes one entry an airbase, so a second is a file put together
        /// by hand.
        /// </summary>
        public static Dictionary<string, TaxiNetworkData> ByAirbase(IEnumerable<TaxiNetworkData> data, List<string> notes = null)
        {
            var found = new Dictionary<string, TaxiNetworkData>(StringComparer.Ordinal);
            if (data == null) return found;

            foreach (TaxiNetworkData airbase in data)
            {
                if (airbase == null) continue;
                string name = airbase.Airbase ?? "";
                if (found.ContainsKey(name))
                {
                    notes?.Add($"the taxi lanes name airbase '{name}' twice; the first is used");
                    continue;
                }

                found[name] = airbase;
            }

            return found;
        }

        /// <summary>
        /// The runways of a placement as the plugin lays them out, in map metres: the main one from its
        /// centre, heading and length (as <c>AirbaseBuilder.LayOutRunway</c> places its two thresholds),
        /// then the extra runways from their thresholds.
        /// </summary>
        public static List<(float StartX, float StartZ, float EndX, float EndZ)> Runways(in AirbasePlacement placement)
        {
            ((float X, float Z) start, (float X, float Z) end) = TaxiDefault.MainRunway(placement);
            var runways = new List<(float, float, float, float)> { (start.X, start.Z, end.X, end.Z) };

            if (placement.HasExtraRunways)
                foreach (AirbaseRunway extra in placement.ExtraRunways)
                    runways.Add((extra.StartX, extra.StartZ, extra.EndX, extra.EndZ));

            return runways;
        }

        /// <summary>
        /// The plan for one airbase: its roads as given, each exit made one transform for each way of
        /// rolling it serves, facing that way along its runway, and the service point and landing pad.
        /// An exit on a runway the placement does not have is dropped, and counted.
        /// </summary>
        public static TaxiPlan Plan(TaxiNetworkData data, in AirbasePlacement placement)
        {
            var plan = new TaxiPlan();
            List<(float StartX, float StartZ, float EndX, float EndZ)> runways = Runways(placement);
            plan.Exits = new List<PlannedExit>[runways.Count];
            for (int r = 0; r < runways.Count; r++) plan.Exits[r] = new List<PlannedExit>();

            if (data == null) return plan;

            foreach ((float X, float Z)[] road in data.Roads)
                if (road != null && road.Length >= 2) plan.Roads.Add(road);

            foreach (TaxiExit exit in data.Exits)
            {
                if (exit.Runway < 0 || exit.Runway >= runways.Count)
                {
                    plan.DroppedExits++;
                    continue;
                }

                (float sx, float sz, float ex, float ez) = runways[exit.Runway];
                float ax = ex - sx, az = ez - sz;
                float length = (float)Math.Sqrt(ax * ax + az * az);
                if (length < 1e-3f)
                {
                    plan.DroppedExits++;
                    continue;
                }

                ax /= length;
                az /= length;

                if ((exit.Directions & TaxiExit.Forward) != 0)
                    plan.Exits[exit.Runway].Add(new PlannedExit { X = exit.X, Z = exit.Z, FacingX = ax, FacingZ = az, Forward = true });
                if ((exit.Directions & TaxiExit.Reverse) != 0)
                    plan.Exits[exit.Runway].Add(new PlannedExit { X = exit.X, Z = exit.Z, FacingX = -ax, FacingZ = -az, Forward = false });
            }

            plan.ServicePoint = data.ServicePoint;
            plan.LandingPad = data.LandingPad;

            if (data.Takeoffs != null)
                for (int i = 0; i < data.Takeoffs.Count; i++)
                {
                    if (PlanTakeoff(data.Takeoffs[i], placement, runways, out PlannedTakeoff takeoff, out string why))
                        plan.Takeoffs.Add(takeoff);
                    else plan.DroppedTakeoffs.Add($"take-off start {i + 1} {why}");
                }

            return plan;
        }

        /// <summary>Metres of runway, fewer than which a take-off start leaves too little to be built.</summary>
        public const float MinTakeoffRun = 1f;

        /// <summary>
        /// One take-off start as the runway to build: moved square onto its runway's centreline, which Map
        /// Forge put it on (the user may have moved the runway since, and Map Forge says so), from there to
        /// the end it takes off towards. Left out when it is on a runway the placement does not have,
        /// beside the runway rather than on it, past one of its ends, or with no runway ahead of it.
        /// </summary>
        static bool PlanTakeoff(TaxiTakeoff start, in AirbasePlacement placement,
                                List<(float StartX, float StartZ, float EndX, float EndZ)> runways,
                                out PlannedTakeoff takeoff, out string why)
        {
            takeoff = default;
            why = null;

            if (start.Runway < 0 || start.Runway >= runways.Count)
            {
                why = $"is on runway {start.Runway + 1}, which the airbase does not have";
                return false;
            }

            (float sx, float sz, float ex, float ez) = runways[start.Runway];
            float ax = ex - sx, az = ez - sz;
            float length = (float)Math.Sqrt(ax * ax + az * az);
            if (length < 1e-3f)
            {
                why = $"is on runway {start.Runway + 1}, which has no length";
                return false;
            }

            ax /= length;
            az /= length;
            float px = start.X - sx, pz = start.Z - sz;
            float along = px * ax + pz * az;
            float across = Math.Abs(px * az - pz * ax);

            float half = Width(placement, start.Runway) * 0.5f;
            if (across > half)
            {
                why = $"is {across:F0} m off runway {start.Runway + 1}'s centreline, beside the runway rather than on it";
                return false;
            }

            if (along < 0f || along > length)
            {
                why = $"lies {(along < 0f ? -along : along - length):F0} m past runway {start.Runway + 1}'s {(along < 0f ? "start" : "end")}";
                return false;
            }

            bool towardsEnd = start.Direction == TaxiExit.Forward;
            float run = towardsEnd ? length - along : along;
            if (run < MinTakeoffRun)
            {
                why = $"is at runway {start.Runway + 1}'s {(towardsEnd ? "end" : "start")}, with no runway ahead of it";
                return false;
            }

            takeoff = new PlannedTakeoff
            {
                Runway = start.Runway,
                StartX = sx + ax * along, StartZ = sz + az * along,
                EndX = towardsEnd ? ex : sx, EndZ = towardsEnd ? ez : sz,
                TowardsEnd = towardsEnd, Run = run, Moved = across,
            };
            return true;
        }

        /// <summary>A placement's runway's width: the main one's, or an extra runway's own.</summary>
        static float Width(in AirbasePlacement placement, int runway)
        {
            if (runway == 0) return placement.RunwayWidth;
            return placement.HasExtraRunways && runway - 1 < placement.ExtraRunways.Length ? placement.ExtraRunways[runway - 1].Width : 0f;
        }

        /// <summary>Metres within which a take-off runway's thresholds are taken to lie on another runway:
        /// the plugin builds them exactly there, so only float rounding separates them.</summary>
        public const float StripReach = 1f;

        /// <summary>
        /// For each runway of an airbase, the runway it is a take-off start of, or -1: a runway that takes
        /// no landings, whose start lies on a landing runway's centreline between its ends and whose end is
        /// one of that runway's ends. What the plugin builds for a take-off start, told from the rest by
        /// where it lies rather than by a list kept beside it, so the same answer comes from the prefab, an
        /// instance of it or a mission's link, on the host and every client.
        /// </summary>
        public static int[] TakeoffParents(IReadOnlyList<RunwayLine> runways)
        {
            var parents = new int[runways?.Count ?? 0];
            for (int i = 0; i < parents.Length; i++)
            {
                parents[i] = -1;
                RunwayLine start = runways[i];
                if (start.Landing) continue;

                for (int p = 0; p < runways.Count; p++)
                {
                    RunwayLine runway = runways[p];
                    if (p == i || !runway.Landing) continue;

                    bool endsThere = Near(start.EndX, start.EndZ, runway.StartX, runway.StartZ) ||
                                     Near(start.EndX, start.EndZ, runway.EndX, runway.EndZ);
                    if (!endsThere) continue;

                    if (!(DistanceToSegment(start.StartX, start.StartZ, runway.StartX, runway.StartZ, runway.EndX, runway.EndZ) <= StripReach))
                        continue;

                    parents[i] = p;
                    break;
                }
            }

            return parents;
        }

        /// <summary>
        /// What each runway's <c>Takeoff</c> is to be, given which of the airbase's take-off starts are in
        /// use: null for a runway left as it is, which is every runway of an airbase without take-off
        /// starts.
        ///
        /// A runway the map placed take-off starts on takes off only from them: while at least one of its
        /// starts is in use, its own ends are no take-off points (false), so <c>Airbase.GetTakeoffRunway</c>,
        /// which sends an aircraft to the nearest start of any runway that takes take-offs (and its end too
        /// when reversible), sends it to one of the starts and never to the runway's ends. Only what was
        /// placed counts: starts that all face one way leave no take-off the other way there. When none of
        /// its starts is in use (a mission's roads lead to none of them), it takes off from its ends again
        /// (true), as on any airbase, so a runway never loses all its take-offs. A take-off start takes
        /// take-offs while in use and none otherwise. A runway without starts is left alone.
        ///
        /// Setting a runway's ends back on is right because the plugin builds take-off starts only on a
        /// runway that takes take-offs (<c>AirbaseBuilder.LayOutTakeoffStarts</c>).
        /// </summary>
        /// <param name="parents">For each runway, the runway it is a take-off start of, or -1
        /// (<see cref="TakeoffParents"/>).</param>
        /// <param name="inUse">For each runway, whether it is in use as a take-off start; read only for
        /// take-off starts. Null for all of them in use, as the map built them.</param>
        public static bool?[] TakeoffFlags(IReadOnlyList<int> parents, IReadOnlyList<bool> inUse = null)
        {
            var flags = new bool?[parents?.Count ?? 0];

            // Every runway with starts takes off from its ends until one of its starts is found in use.
            for (int i = 0; i < flags.Length; i++)
            {
                int parent = parents[i];
                if (parent < 0 || parent >= flags.Length || parent == i) continue;
                if (flags[parent] == null) flags[parent] = true;
            }

            for (int i = 0; i < flags.Length; i++)
            {
                int parent = parents[i];
                if (parent < 0 || parent >= flags.Length || parent == i) continue;

                bool use = inUse == null || (i < inUse.Count && inUse[i]);
                flags[i] = use;
                if (use) flags[parent] = false;
            }

            return flags;
        }

        /// <summary>
        /// The runways each runway of an airbase shares its strip with, by <see cref="TakeoffParents"/>: a
        /// take-off start with its runway and every other take-off start on it, and a runway with all of
        /// its take-off starts. Each of them is one strip of tarmac, so an aircraft lined up or landing on
        /// one has it in use for the others: the game's take-off clearance looks at a runway's
        /// <c>crossingRunways</c> for that (<c>Runway.IsAvailableForTakeoff</c>), and finds runways on one
        /// line only by the chance of float rounding (<c>CrossesRunway</c>, through
        /// <c>TargetCalc.ClosestPointsOnTwoLines</c>, which gives up on lines exactly parallel).
        /// </summary>
        public static List<int>[] SharedStrips(IReadOnlyList<RunwayLine> runways)
        {
            int[] parents = TakeoffParents(runways);
            var shared = new List<int>[parents.Length];
            for (int i = 0; i < parents.Length; i++) shared[i] = new List<int>();

            for (int i = 0; i < parents.Length; i++)
            {
                int parent = parents[i];
                if (parent < 0) continue;

                Link(shared, i, parent);
                for (int j = i + 1; j < parents.Length; j++)
                    if (parents[j] == parent) Link(shared, i, j);
            }

            return shared;
        }

        static void Link(List<int>[] shared, int a, int b)
        {
            if (!shared[a].Contains(b)) shared[a].Add(b);
            if (!shared[b].Contains(a)) shared[b].Add(a);
        }

        static bool Near(float ax, float az, float bx, float bz)
            => (ax - bx) * (ax - bx) + (az - bz) * (az - bz) <= StripReach * StripReach;

        /// <summary>Metres off its runway's centreline an exit may lie and the network still be the
        /// field's: Map Forge puts every exit on the centreline, the runway's ends included, from the
        /// same runway the placement was made from, so only float rounding separates them.</summary>
        public const float FitReach = 5f;

        /// <summary>
        /// Whether a network lies on the placement's runways: every exit on a runway the placement has
        /// within <see cref="FitReach"/> of its centreline. One that does not was made for the field
        /// before it was moved or turned: a <c>taxiways.bin</c> from an earlier export left beside the
        /// <c>airbases.bin</c> of a later one, by a Map Forge from before taxi networks or an export
        /// that failed half way. Its roads would lie where the field was.
        /// </summary>
        public static bool Fits(TaxiNetworkData data, in AirbasePlacement placement, out string misfit)
        {
            misfit = null;
            if (data?.Exits == null) return true;

            List<(float StartX, float StartZ, float EndX, float EndZ)> runways = Runways(placement);
            foreach (TaxiExit exit in data.Exits)
            {
                if (exit.Runway < 0 || exit.Runway >= runways.Count) continue;

                (float sx, float sz, float ex, float ez) = runways[exit.Runway];
                float off = DistanceToSegment(exit.X, exit.Z, sx, sz, ex, ez);
                if (off <= FitReach) continue;

                misfit = $"an exit {off:F0} m off runway {exit.Runway + 1}'s centreline";
                return false;
            }

            return true;
        }

        static float DistanceToSegment(float px, float pz, float ax, float az, float bx, float bz)
        {
            double dx = bx - ax, dz = bz - az, lengthSquared = dx * dx + dz * dz;
            double t = lengthSquared < 1e-9 ? 0.0 : Math.Max(0.0, Math.Min(1.0, ((px - ax) * dx + (pz - az) * dz) / lengthSquared));
            double x = ax + dx * t - px, z = az + dz * t - pz;
            return (float)Math.Sqrt(x * x + z * z);
        }

        /// <summary>Every runway's two numbers as painted, "09/27, 04/22", for the log beside what the
        /// game will call them.</summary>
        public static string Painted(in AirbasePlacement placement)
        {
            var parts = new List<string>();
            foreach ((float sx, float sz, float ex, float ez) in Runways(placement))
            {
                (string start, string end) = RunwayDesignator.Ends(sx, sz, ex, ez);
                parts.Add($"{start}/{end}");
            }

            return string.Join(", ", parts);
        }
    }
}
