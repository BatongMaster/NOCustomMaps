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
    /// What the plugin builds into one airbase from its taxi lanes (<see cref="TaxiData"/>): the roads
    /// of its <c>taxiNetwork</c>, each runway's exit transforms, and where its service point and
    /// landing pad go. Free of game types, so the tests run it as the plugin does.
    /// </summary>
    internal sealed class TaxiPlan
    {
        public readonly List<(float X, float Z)[]> Roads = new List<(float X, float Z)[]>();

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
            return plan;
        }

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
