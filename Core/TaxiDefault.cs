using System;
using System.Collections.Generic;

namespace CustomMaps
{
    /// <summary>
    /// The taxi network an airbase is given when nothing was drawn for it: no lanes, and no
    /// taxiways to make a network of. Built the same way by Map Forge, which writes it into
    /// <c>taxiways.bin</c> and draws it in the editor, and by the plugin, for a map built before
    /// taxi networks shipped, so that no airbase is ever left without one.
    ///
    /// An airbase without a network is not idle. Its AI drives every leg in a straight line
    /// (<c>AIPilotTaxiState</c>, <c>PathfindingAgent.SetMovingTarget</c>): from wherever it is parked
    /// to the take-off threshold, and after landing from wherever it stopped to the nearest service
    /// point, through hangars, parked aircraft and each other, and it gives up only after 30 to 60 s of
    /// being stuck (<c>IsStuck</c>). The base game's own airfields all have a network, so none of them
    /// behaves like that.
    ///
    /// This one is made in the shape of the base game's networks and of the layouts Map Forge
    /// generates (<c>TarmacLayoutGenerator</c>), from what every airbase has whatever was drawn: its
    /// main runway and its service point.
    /// <list type="bullet">
    /// <item>A lane parallel to the runway on the service point's side, 56 m short of it (the donor's
    /// service point is 206 m off the runway's axis, so the lane runs 150 m off it, where a generated
    /// Medium layout paves its taxiway), the whole length of the runway.</item>
    /// <item>A link from it straight onto the runway at each of its ends, ending exactly on the
    /// threshold, which is where the AI taxis to for take-off, and one every 700 m or so between them,
    /// ending on the centreline.</item>
    /// <item>An exit both ways where each link meets the centreline, ends included: after landing the
    /// AI rolls along the centreline to the nearest exit ahead of it facing its roll
    /// (<c>Runway.TryGetExitTaxiPoint</c>), so an exit off the centreline is never reached.</item>
    /// </list>
    /// Every road runs from one junction to the next, every two junctions lie far further apart than
    /// the 10 m within which the game joins road ends, and no road is longer than 30 m between two of
    /// its points, the game finding the road an aircraft is on by its nearest point. The service point
    /// itself stays off the network, 56 m from the lane, as the base game's lie 7 to 65 m off theirs.
    ///
    /// It knows nothing of the paving or of where a mission puts its hangars, so it can lead across
    /// grass, and the plugin goes on letting AI aircraft taxi on a drawn field's grass, as it does for
    /// every network. A field's other runways get no roads, only the exits at their ends.
    /// </summary>
    public static class TaxiDefault
    {
        /// <summary>Metres the lane lies short of the service point, across the runway.</summary>
        public const float ServiceGap = 56f;

        /// <summary>Fewest metres from the runway's edge to the lane's centreline: a wingspan and some
        /// over, so an aircraft on the lane is clear of one on the runway.</summary>
        public const float EdgeClearance = 60f;

        /// <summary>Fewest metres from the runway's edge to the lane when the levelled ground is too
        /// narrow for <see cref="EdgeClearance"/>.</summary>
        public const float NarrowestClearance = 40f;

        /// <summary>Most metres from the runway's axis to the lane, whatever the service point.</summary>
        public const float FarthestOffset = 400f;

        /// <summary>About this many metres between two links onto the runway.</summary>
        public const float LinkSpacing = 700f;

        /// <summary>Most metres between two points of a road.</summary>
        public const float PointSpacing = 30f;

        /// <summary>A runway shorter than this has no network, only an exit at each end.</summary>
        public const float ShortestRunway = 50f;

        /// <summary>The main runway's two thresholds in map metres, from a placement's centre, heading
        /// and length, the way the plugin lays them out.</summary>
        public static ((float X, float Z) Start, (float X, float Z) End) MainRunway(in AirbasePlacement placement)
        {
            double radians = placement.Heading * Math.PI / 180.0;
            float dx = (float)Math.Sin(radians), dz = (float)Math.Cos(radians);
            float half = placement.RunwayLength * 0.5f;
            return ((placement.X - dx * half, placement.Z - dz * half), (placement.X + dx * half, placement.Z + dz * half));
        }

        /// <summary>The default network of a placed airbase whose service point stands at
        /// <paramref name="servicePoint"/> (map metres).</summary>
        public static TaxiNetworkData Build(in AirbasePlacement placement, (float X, float Z) servicePoint)
        {
            ((float X, float Z) start, (float X, float Z) end) = MainRunway(placement);
            return Build(placement.UniqueName, start, end, placement.RunwayWidth, servicePoint,
                         placement.HasOutline ? placement.Outline : null, placement.FlatHalfAcross);
        }

        /// <summary>
        /// The default network of an airbase: its main runway by its two thresholds and its width, its
        /// service point, and the ground levelled for it, the drawn outline when there is one and
        /// otherwise the levelled half-width across the runway (0 for unknown), which the lane is kept
        /// inside where it can be.
        /// </summary>
        public static TaxiNetworkData Build(string airbase, (float X, float Z) start, (float X, float Z) end, float width,
                                            (float X, float Z) servicePoint, (float X, float Z)[] outline = null,
                                            float flatHalfAcross = 0f)
        {
            var data = new TaxiNetworkData { Airbase = airbase ?? "", Source = TaxiSource.Default };
            if (Finite(servicePoint.X) && Finite(servicePoint.Z)) data.ServicePoint = servicePoint;

            if (!Finite(start.X) || !Finite(start.Z) || !Finite(end.X) || !Finite(end.Z)) return data;

            double dx = end.X - start.X, dz = end.Z - start.Z;
            double length = Math.Sqrt(dx * dx + dz * dz);
            if (!(length >= 1.0)) return data;

            // The runway's ends are exits whatever else there is, facing both ways, as the threshold
            // links' feet are below; so an aircraft rolling out always has one ahead.
            data.Exits.Add(new TaxiExit { Runway = 0, X = start.X, Z = start.Z, Directions = TaxiExit.Forward | TaxiExit.Reverse });
            data.Exits.Add(new TaxiExit { Runway = 0, X = end.X, Z = end.Z, Directions = TaxiExit.Forward | TaxiExit.Reverse });
            if (length < ShortestRunway) return data;

            // The runway's frame: along it from its middle towards its end, and across it, to the right
            // looking along it (x east and z north, so the right of (ux, uz) is (uz, -ux)).
            double ux = dx / length, uz = dz / length, rx = uz, rz = -ux;
            double cx = (start.X + end.X) * 0.5, cz = (start.Z + end.Z) * 0.5;
            double half = Math.Max(0.0, Finite(width) ? width : 0f) * 0.5;

            double serviceAcross = data.ServicePoint.HasValue
                ? (servicePoint.X - cx) * rx + (servicePoint.Z - cz) * rz
                : 206.0;
            int side = serviceAcross >= 0.0 ? 1 : -1;

            double nearest = half + EdgeClearance, floor = half + NarrowestClearance;
            double offset = Math.Min(Math.Max(Math.Abs(serviceAcross) - ServiceGap, nearest), Math.Max(nearest, FarthestOffset));

            // Kept on the levelled ground: inside the outline, or the rectangle's half-width, if it can be.
            if (flatHalfAcross > 0f && Finite(flatHalfAcross)) offset = Math.Min(offset, Math.Max(floor, flatHalfAcross - 20.0));

            if (outline != null && outline.Length >= 3)
                while (offset > floor && !SpineInside(outline, cx, cz, ux, uz, rx, rz, length, side * offset))
                    offset = Math.Max(floor, offset - 10.0);

            double across = side * offset;
            int links = Math.Max(0, (int)Math.Round(length / LinkSpacing) - 1) + 2;

            var spine = new (float X, float Z)[links];
            var feet = new (float X, float Z)[links];
            for (int k = 0; k < links; k++)
            {
                double along = -length * 0.5 + length * k / (links - 1);
                spine[k] = ((float)(cx + ux * along + rx * across), (float)(cz + uz * along + rz * across));

                // The two ends exactly on the thresholds, which the AI taxis to for take-off.
                feet[k] = k == 0 ? start
                        : k == links - 1 ? end
                        : ((float)(cx + ux * along), (float)(cz + uz * along));
            }

            for (int k = 0; k + 1 < links; k++) data.Roads.Add(Road(spine[k], spine[k + 1]));

            for (int k = 0; k < links; k++)
            {
                data.Roads.Add(Road(spine[k], feet[k]));
                if (k > 0 && k < links - 1)
                    data.Exits.Add(new TaxiExit { Runway = 0, X = feet[k].X, Z = feet[k].Z, Directions = TaxiExit.Forward | TaxiExit.Reverse });
            }

            return data;
        }

        /// <summary>A straight road between two points, with a point at least every
        /// <see cref="PointSpacing"/> metres and its ends exactly the two given.</summary>
        static (float X, float Z)[] Road((float X, float Z) from, (float X, float Z) to)
        {
            double dx = to.X - from.X, dz = to.Z - from.Z;
            double length = Math.Sqrt(dx * dx + dz * dz);
            int pieces = Math.Max(1, (int)Math.Ceiling(length / PointSpacing));

            var points = new (float X, float Z)[pieces + 1];
            points[0] = from;
            for (int i = 1; i < pieces; i++)
                points[i] = ((float)(from.X + dx * i / pieces), (float)(from.Z + dz * i / pieces));
            points[pieces] = to;
            return points;
        }

        /// <summary>Whether the lane at <paramref name="across"/> lies inside the outline from one end
        /// of the runway to the other, sampled every 50 m.</summary>
        static bool SpineInside((float X, float Z)[] outline, double cx, double cz, double ux, double uz, double rx, double rz,
                                double length, double across)
        {
            int samples = Math.Max(2, (int)Math.Ceiling(length / 50.0) + 1);
            for (int i = 0; i < samples; i++)
            {
                double along = -length * 0.5 + length * i / (samples - 1);
                double x = cx + ux * along + rx * across, z = cz + uz * along + rz * across;
                if (!Inside(outline, x, z)) return false;
            }

            return true;
        }

        /// <summary>Even-odd: whether a point lies inside a polygon in map metres.</summary>
        static bool Inside((float X, float Z)[] outline, double x, double z)
        {
            bool inside = false;
            for (int i = 0, j = outline.Length - 1; i < outline.Length; j = i++)
            {
                double xi = outline[i].X, zi = outline[i].Z, xj = outline[j].X, zj = outline[j].Z;
                if ((zi > z) != (zj > z) && x < (xj - xi) * (z - zi) / (zj - zi) + xi) inside = !inside;
            }

            return inside;
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
