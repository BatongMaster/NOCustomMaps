using System;
using System.Collections.Generic;
using System.IO;

namespace CustomMaps
{
    /// <summary>One road: a polyline in map metres, plus what the engine needs to know
    /// about it.</summary>
    public sealed class RoadRecord
    {
        /// <summary>Flat XYZ triples. Length is always a multiple of three.</summary>
        public float[] Points = Array.Empty<float>();

        /// <summary>0 motorway, 1 trunk, 2 primary, 3 other. Drives paint width and the
        /// ribbon, never pathing.</summary>
        public byte Class;

        /// <summary>
        /// Fraction of this road that runs through a tunnel.
        ///
        /// Carried rather than discarded because the two consumers want opposite things
        /// from it. The pathing graph wants tunnels kept — through the Alps they are the
        /// only crossings there are, and dropping them disconnects the north of the map
        /// from the south. Anything that draws a road wants them gone, because a tunnel
        /// draped on the surface climbs the pass the tunnel exists to avoid.
        /// </summary>
        public float TunnelFraction;

        /// <summary>
        /// What the road is built as: <see cref="RoadData.FlagTunnel"/>,
        /// <see cref="RoadData.FlagBridge"/>, or 0 for a road on the ground.
        ///
        /// A flagged record is a structure, drawn by its own geometry rather than draped on the
        /// terrain, and its points carry the real deck or bore height. The plugin marks bridge
        /// records as bridges in the game's road network.
        /// </summary>
        public byte Flags;

        /// <summary>
        /// Metres of road before this record's first point, along the road it was cut from.
        ///
        /// Where a road is cut into pieces — at a junction, at each end of a bridge — the
        /// surface texture of each piece starts from here, so the markings run on across the
        /// cut instead of jumping back to the start of the texture.
        /// </summary>
        public float StartAlong;

        public int Count => Points.Length / 3;

        public float X(int i) => Points[i * 3];
        public float Y(int i) => Points[i * 3 + 1];
        public float Z(int i) => Points[i * 3 + 2];

        public bool IsBridge => (Flags & RoadData.FlagBridge) != 0;
        public bool IsTunnel => (Flags & RoadData.FlagTunnel) != 0;

        /// <summary>A bridge or a tunnel: built as a structure, never draped.</summary>
        public bool IsStructure => (Flags & (RoadData.FlagBridge | RoadData.FlagTunnel)) != 0;
    }

    /// <summary>
    /// The map's road polylines, on disk.
    ///
    /// Written by the generator and read by the plugin, which rebuilds the game's
    /// <c>RoadNetwork</c> from it at load. Deliberately not the game's own serialized
    /// form: the bundle carries no game types, so the polylines travel as plain numbers
    /// and become engine objects only on the far side.
    ///
    /// Two versions, little-endian throughout:
    ///
    ///   NOROAD01  count, then per road: class (byte), tunnel fraction (float),
    ///             point count (int), points (float xyz)
    ///   NOROAD02  count, then per road: class (byte), flags (byte), tunnel fraction (float),
    ///             start along (float), point count (int), points (float xyz)
    ///
    /// The writer emits version 1 whenever no record uses a flag or a start offset, so every
    /// file that needs nothing new — the Swiss Alps roads among them — keeps its exact bytes,
    /// and a plugin that predates version 2 still reads it.
    /// </summary>
    public static class RoadData
    {
        /// <summary>Eight bytes, so a truncated or unrelated file fails immediately
        /// rather than being read as a road with two billion points.</summary>
        public static readonly byte[] Magic = { (byte)'N', (byte)'O', (byte)'R', (byte)'O', (byte)'A', (byte)'D', (byte)'0', (byte)'1' };

        /// <summary>The version 2 magic: flags and start offsets.</summary>
        public static readonly byte[] Magic2 = { (byte)'N', (byte)'O', (byte)'R', (byte)'O', (byte)'A', (byte)'D', (byte)'0', (byte)'2' };

        /// <summary>The road runs through a tunnel.</summary>
        public const byte FlagTunnel = 1;

        /// <summary>The road is carried on a bridge.</summary>
        public const byte FlagBridge = 2;

        /// <summary>Refuses anything implausible before allocating from it.</summary>
        public const int MaxRoads = 100_000;
        public const int MaxPointsPerRoad = 1_000_000;

        /// <summary>The version a set of records is written as: 1 unless any record needs 2.</summary>
        public static int VersionFor(IReadOnlyList<RoadRecord> roads)
        {
            if (roads == null) return 1;
            foreach (RoadRecord road in roads)
                if (road != null && (road.Flags != 0 || road.StartAlong != 0f)) return 2;
            return 1;
        }

        public static void Write(string path, IReadOnlyList<RoadRecord> roads)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                Write(stream, roads);
        }

        public static void Write(Stream stream, IReadOnlyList<RoadRecord> roads)
        {
            if (roads == null) throw new ArgumentNullException(nameof(roads));

            bool version2 = VersionFor(roads) == 2;

            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(version2 ? Magic2 : Magic);
                writer.Write(roads.Count);

                foreach (RoadRecord road in roads)
                {
                    writer.Write(road.Class);
                    if (version2) writer.Write(road.Flags);
                    writer.Write(road.TunnelFraction);
                    if (version2) writer.Write(road.StartAlong);
                    writer.Write(road.Count);

                    foreach (float value in road.Points) writer.Write(value);
                }
            }
        }

        public static List<RoadRecord> Read(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));

            using (var stream = new MemoryStream(bytes, writable: false))
                return Read(stream);
        }

        public static List<RoadRecord> Read(Stream stream)
        {
            using (var reader = new BinaryReader(stream))
            {
                byte[] magic = reader.ReadBytes(Magic.Length);
                if (magic.Length != Magic.Length) throw new InvalidDataException("road data is truncated");

                int version = Matches(magic, Magic) ? 1 : Matches(magic, Magic2) ? 2 : 0;
                if (version == 0)
                {
                    bool newer = Matches(magic, Magic, Magic.Length - 2);
                    throw new InvalidDataException(newer
                        ? "road data is a newer version than this build reads — update the plugin"
                        : "not road data — the magic does not match");
                }

                int count = reader.ReadInt32();
                if (count < 0 || count > MaxRoads)
                    throw new InvalidDataException($"road data claims {count} roads");

                var roads = new List<RoadRecord>(count);
                for (int i = 0; i < count; i++)
                {
                    var road = new RoadRecord { Class = reader.ReadByte() };
                    if (version >= 2) road.Flags = reader.ReadByte();
                    road.TunnelFraction = reader.ReadSingle();
                    if (version >= 2) road.StartAlong = reader.ReadSingle();

                    int points = reader.ReadInt32();
                    if (points < 0 || points > MaxPointsPerRoad)
                        throw new InvalidDataException($"road {i} claims {points} points");

                    road.Points = new float[points * 3];
                    for (int k = 0; k < road.Points.Length; k++) road.Points[k] = reader.ReadSingle();

                    roads.Add(road);
                }

                return roads;
            }
        }

        static bool Matches(byte[] actual, byte[] expected, int length = -1)
        {
            if (length < 0) length = expected.Length;
            for (int i = 0; i < length; i++)
                if (actual[i] != expected[i]) return false;
            return true;
        }

        /// <summary>
        /// How many junction nodes the engine will end up with.
        ///
        /// <c>RoadNetwork.RegenerateNetwork</c> makes a node per road endpoint and fuses
        /// any two within <see cref="FuseRadius"/>. This mirrors that, because the node
        /// count is the one number that decides whether pathfinding is affordable —
        /// <c>RoadPathfinder.TryPathfind</c> re-sorts its whole unvisited list every
        /// iteration, so the cost grows as the square of this.
        /// </summary>
        public static int CountNodes(IReadOnlyList<RoadRecord> roads)
        {
            var nodes = new List<(float X, float Y, float Z)>();

            foreach (RoadRecord road in roads)
            {
                if (road.Count == 0) continue;

                Add(nodes, (road.X(0), road.Y(0), road.Z(0)));
                Add(nodes, (road.X(road.Count - 1), road.Y(road.Count - 1), road.Z(road.Count - 1)));
            }

            return nodes.Count;
        }

        /// <summary>Distance within which the engine treats two road endpoints as one
        /// junction. Read off <c>RegenerateNetwork</c>, and a strict comparison there.</summary>
        public const float FuseRadius = 10f;

        static void Add(List<(float X, float Y, float Z)> nodes, (float X, float Y, float Z) candidate)
        {
            foreach ((float x, float y, float z) in nodes)
            {
                float dx = x - candidate.X, dy = y - candidate.Y, dz = z - candidate.Z;
                if (dx * dx + dy * dy + dz * dz < FuseRadius * FuseRadius) return;
            }

            nodes.Add(candidate);
        }
    }
}
