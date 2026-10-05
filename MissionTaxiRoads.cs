using System;
using System.Collections.Generic;

namespace CustomMaps
{
    /// <summary>One road of an airbase's taxi network as a mission saves it: its points in the game's
    /// global frame, which on a custom map is map metres, and whether it is a bridge.</summary>
    internal struct RoadShape
    {
        public (float X, float Y, float Z)[] Points;
        public bool Bridge;
    }

    /// <summary>
    /// What the plugin decides about a mission's own taxi network for a custom map's airbase, free of
    /// game types so the tests run it as the plugin does (<see cref="MissionAirbaseRoads"/> is the rest).
    ///
    /// A mission edits an airbase's roads in the game's mission editor (its Roads tool), and saves them
    /// in the airbase's <c>SavedAirbase.roads</c>, which the game reads only for an airbase the mission
    /// made (<c>Airbase.SetupCustomAirbase</c>). The plugin reads it for a custom map's airbases too, and
    /// the mission's roads replace the map's. A mission that never edited them saves none, which is
    /// how the game has always saved a built-in airbase's override, and has the map's: so a network
    /// changed later in Map Forge still reaches every mission that left it alone.
    /// </summary>
    internal static class MissionTaxiRoads
    {
        /// <summary>Metres a point may move and still be the same: well under anything an edit in the
        /// mission editor makes, well over a float's rounding through the mission's JSON and the
        /// network.</summary>
        public const float Tolerance = 0.01f;

        /// <summary>Metres beyond a runway's side or end that a road's end still counts as on it.</summary>
        public const float EndReach = 3f;

        /// <summary>Whether two networks are the same roads, in the same order, point for point: a
        /// mission's copy of the map's network that nothing was done to, which is saved as none.</summary>
        public static bool Same(IReadOnlyList<RoadShape> a, IReadOnlyList<RoadShape> b, float tolerance = Tolerance)
        {
            int countA = a?.Count ?? 0, countB = b?.Count ?? 0;
            if (countA != countB) return false;

            for (int r = 0; r < countA; r++)
            {
                RoadShape x = a[r], y = b[r];
                int pointsX = x.Points?.Length ?? 0, pointsY = y.Points?.Length ?? 0;
                if (pointsX != pointsY || x.Bridge != y.Bridge) return false;

                for (int i = 0; i < pointsX; i++)
                {
                    (float X, float Y, float Z) p = x.Points[i], q = y.Points[i];
                    if (!(Math.Abs(p.X - q.X) <= tolerance && Math.Abs(p.Y - q.Y) <= tolerance && Math.Abs(p.Z - q.Z) <= tolerance))
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// A runway's exits for a network a mission drew: where a road leaves the runway, on its
        /// centreline, for each way of rolling the road can be turned into from there (a turn of at
        /// most 120°, as Map Forge's own exits); both ways where a road crosses it; and one at each end
        /// facing out of the runway, so an aircraft rolling out always has one ahead.
        ///
        /// The game's landing AI takes the nearest exit ahead of it that faces its roll and that it can
        /// brake for, rolls on along the centreline until it is within 20 m of it, and only then looks
        /// for a way along the network (<c>Runway.TryGetExitTaxiPoint</c>, <c>AIPilotTaxiState</c>), so the
        /// exits have to be where the mission's roads meet the runway, not where the map's did.
        /// </summary>
        /// <param name="roads">The network's roads, x and z in the same frame as the runway.</param>
        public static List<PlannedExit> Exits(IReadOnlyList<(float X, float Z)[]> roads, (float X, float Z) start,
                                              (float X, float Z) end, float width)
        {
            var exits = new List<PlannedExit>();
            double dx = end.X - start.X, dz = end.Z - start.Z;
            double length = Math.Sqrt(dx * dx + dz * dz);
            if (!(length >= 1.0)) return exits;

            double ux = dx / length, uz = dz / length;
            double half = Math.Max(0.0, width) * 0.5;
            double limit = Math.Cos(120.0 * Math.PI / 180.0);

            void Add(double along, bool forward)
            {
                double clamped = Math.Max(0.0, Math.Min(length, along));
                float x = (float)(start.X + ux * clamped), z = (float)(start.Z + uz * clamped);
                foreach (PlannedExit other in exits)
                    if (other.Forward == forward && Math.Abs(other.X - x) < 1f && Math.Abs(other.Z - z) < 1f) return;

                exits.Add(new PlannedExit
                {
                    X = x, Z = z,
                    FacingX = (float)(forward ? ux : -ux), FacingZ = (float)(forward ? uz : -uz),
                    Forward = forward,
                });
            }

            if (roads != null)
                foreach ((float X, float Z)[] road in roads)
                {
                    if (road == null || road.Length < 2) continue;

                    // A road's end on the runway: an exit for each way it can be turned into.
                    for (int first = 1; first >= 0; first--)
                    {
                        (float X, float Z) at = first == 1 ? road[0] : road[road.Length - 1];
                        double px = at.X - start.X, pz = at.Z - start.Z;
                        double along = px * ux + pz * uz, across = px * uz - pz * ux;
                        if (Math.Abs(across) > half + EndReach || along < -EndReach || along > length + EndReach) continue;

                        (float X, float Z) towards = at;
                        for (int k = 1; k < road.Length; k++)
                        {
                            towards = first == 1 ? road[k] : road[road.Length - 1 - k];
                            if (Distance(at, towards) >= 3.0) break;
                        }

                        double d = Distance(at, towards);
                        if (d < 1e-3) continue;
                        double away = ((towards.X - at.X) * ux + (towards.Z - at.Z) * uz) / d;
                        if (away >= limit) Add(along, true);
                        if (-away >= limit) Add(along, false);
                    }

                    // A road across the centreline: both ways.
                    for (int i = 0; i + 1 < road.Length; i++)
                        if (Crosses(road[i], road[i + 1], start, end, out double t)) { Add(t * length, true); Add(t * length, false); }
                }

            Add(length, true);
            Add(0.0, false);
            return exits;
        }

        /// <summary>
        /// How many separate pieces a network's roads make, by the nodes each road runs between (the
        /// game's own, numbered from 0, after <c>RoadNetwork.RegenerateNetwork</c>); a road with an end
        /// at no node is left out. Between two pieces the game's pathfinder finds no way
        /// (<c>RoadPathfinder.TryPathfind</c> says <c>NoConnection</c>), and the taxiing AI is then left
        /// with no waypoints at all, rolling on unsteered at taxi speed until it hits something. The
        /// mission editor's Roads tool makes pieces easily: it joins a road only to a node marker under
        /// the cursor, or to another road within 10 m of the new one's ends, never where two cross.
        /// </summary>
        public static int Pieces(int nodes, IEnumerable<(int From, int To)> roads)
        {
            if (nodes <= 0 || roads == null) return 0;

            var parent = new int[nodes];
            var used = new bool[nodes];
            for (int i = 0; i < nodes; i++) parent[i] = i;

            int Find(int i)
            {
                while (parent[i] != i) i = parent[i] = parent[parent[i]];
                return i;
            }

            foreach ((int from, int to) in roads)
            {
                if (from < 0 || to < 0 || from >= nodes || to >= nodes) continue;
                used[from] = used[to] = true;
                parent[Find(from)] = Find(to);
            }

            int pieces = 0;
            for (int i = 0; i < nodes; i++)
                if (used[i] && Find(i) == i) pieces++;
            return pieces;
        }

        /// <summary>Whether a segment crosses the centreline strictly inside both, and how far along
        /// the centreline as a share of it.</summary>
        static bool Crosses((float X, float Z) a, (float X, float Z) b, (float X, float Z) c, (float X, float Z) d, out double u)
        {
            u = 0.0;
            double rx = b.X - a.X, rz = b.Z - a.Z, sx = d.X - c.X, sz = d.Z - c.Z;
            double denominator = rx * sz - rz * sx;
            if (Math.Abs(denominator) < 1e-9) return false;

            double qx = c.X - a.X, qz = c.Z - a.Z;
            double t = (qx * sz - qz * sx) / denominator;
            u = (qx * rz - qz * rx) / denominator;
            return t > 1e-6 && t < 1.0 - 1e-6 && u > 1e-6 && u < 1.0 - 1e-6;
        }

        static double Distance((float X, float Z) a, (float X, float Z) b)
            => Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Z - b.Z) * (a.Z - b.Z));
    }
}
