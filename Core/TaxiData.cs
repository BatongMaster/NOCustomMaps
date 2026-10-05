using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CustomMaps
{
    /// <summary>
    /// A point on a runway's centreline where an aircraft that has landed turns off onto the taxi
    /// lanes, and which way along the runway it has to be rolling to use it.
    ///
    /// The game's <c>Airbase.Runway.exitPoints</c>: after touchdown <c>TryGetExitTaxiPoint</c> takes
    /// the nearest one ahead of the aircraft that faces the way it is rolling and that it can brake
    /// for, the aircraft rolls on unsteered until it is within 20 m of it, and only then looks for a
    /// way along the taxi network. So it has to lie on the centreline, where the roll takes it.
    /// </summary>
    public struct TaxiExit
    {
        /// <summary>Rolling from the runway's start towards its end.</summary>
        public const byte Forward = 1;

        /// <summary>Rolling from its end towards its start.</summary>
        public const byte Reverse = 2;

        /// <summary>Which runway of the airbase: 0 the main one, then its extra runways in order
        /// from 1 (<see cref="AirbasePlacement.ExtraRunways"/>).</summary>
        public int Runway;

        /// <summary>Where on the centreline, in map metres.</summary>
        public float X, Z;

        /// <summary><see cref="Forward"/>, <see cref="Reverse"/> or both: one exit transform each, facing
        /// the way that roll goes.</summary>
        public byte Directions;
    }

    /// <summary>
    /// What an airbase's taxi network was made from, for the log and the map's check report. It
    /// changes nothing in how the airbase is built or its AI treated: every network is made by the
    /// same rules, and a drawn field's AI is let onto its grass whatever the source, since none of
    /// them knows where a mission puts its hangars.
    /// </summary>
    public enum TaxiSource : byte
    {
        /// <summary>Taxi lanes drawn in Map Forge's Lanes mode, with whatever taxiways of the field's
        /// layout they were drawn beside.</summary>
        Lanes = 0,

        /// <summary>The field's taxiways as they were laid out or imported, made a network as they
        /// stand, for a field drawn without lanes.</summary>
        Taxiways = 1,

        /// <summary>The network every field is given when nothing was drawn for it
        /// (<see cref="TaxiDefault"/>): along the runway's side, onto both of its ends, beside the
        /// service point. It knows nothing of the paving, so the AI may still cross grass.</summary>
        Default = 2,
    }

    /// <summary>
    /// One airbase's taxi lanes as the game's AI drives them, and the points it drives between.
    ///
    /// The roads are the game's <c>RoadPathfinding.Road</c>s for the airbase's <c>taxiNetwork</c>:
    /// polylines in map metres, each running from one junction or end to the next. The game makes a
    /// junction only of road ends within 10 m of each other (<c>Road.GenerateNodes</c>), never of a
    /// point in a road's middle, so the editor splits every lane at every junction and keeps every
    /// two junctions further apart than that. Heights are not carried: every point lies on the
    /// airbase's levelled platform, at <see cref="AirbasePlacement.Y"/>.
    /// </summary>
    public sealed class TaxiNetworkData
    {
        /// <summary>The airbase, by its unique name (<see cref="AirbasePlacement.UniqueName"/>).</summary>
        public string Airbase = "";

        /// <summary>What the network was made from.</summary>
        public TaxiSource Source = TaxiSource.Lanes;

        /// <summary>The roads, each at least two points, in map metres. Map Forge writes every field
        /// with some (its default at the least); a field whose runway is too short for even that has
        /// none, and keeps only its exits, service point and landing pad.</summary>
        public List<(float X, float Z)[]> Roads = new List<(float X, float Z)[]>();

        /// <summary>Where landed aircraft leave each runway, every runway's ends included.</summary>
        public List<TaxiExit> Exits = new List<TaxiExit>();

        /// <summary>Where an aircraft that has landed taxis to be serviced, in map metres, or null to
        /// keep the donor airbase's.</summary>
        public (float X, float Z)? ServicePoint;

        /// <summary>Where a vertical-landing aircraft sets down, in map metres, or null to keep the
        /// donor airbase's.</summary>
        public (float X, float Z)? LandingPad;

        public bool HasNetwork => Roads != null && Roads.Count > 0;
    }

    /// <summary>
    /// The map's taxi networks: <c>taxiways.bin</c>, shipped in the bundle as the TextAsset
    /// <c>&lt;mapId&gt;_taxiways</c>, found by that name rather than through the manifest.
    ///
    /// A file of its own rather than a seventh version of the airbases file because a plugin that
    /// meets a newer airbases file refuses it whole and the map has no airbases at all; a plugin
    /// that predates this file never asks for it, and its airbases are built as they always were,
    /// their aircraft taxiing straight across the field as before.
    ///
    ///   NOTAXI01  count (int), then per airbase:
    ///             unique name (string)
    ///             source (byte, <see cref="TaxiSource"/>)
    ///             road count (int), per road a point count (int, 2 or more) and that many x, z (floats)
    ///             exit count (int), per exit the runway (byte, 0 the main one), directions (byte,
    ///             <see cref="TaxiExit.Forward"/> | <see cref="TaxiExit.Reverse"/>), x, z (floats)
    ///             flags (byte: 1 a service point follows, 2 a landing pad follows), then their x, z
    /// </summary>
    public static class TaxiData
    {
        public static readonly byte[] Magic =
            { (byte)'N', (byte)'O', (byte)'T', (byte)'A', (byte)'X', (byte)'I', (byte)'0', (byte)'1' };

        /// <summary>Sanity bounds on a corrupt or hostile file. A base-game airbase has at most 80
        /// roads, 380 points and 20 points a road.</summary>
        public const int MaxAirbases = AirbaseData.MaxAirbases;
        public const int MaxRoads = 4096;
        public const int MaxRoadPoints = 4096;
        public const int MaxExits = 256;

        const byte HasServicePoint = 1, HasLandingPad = 2;

        public static void Write(string path, IReadOnlyList<TaxiNetworkData> airbases)
        {
            // Checked before the file is opened, so a refused set leaves no half-written file.
            Check(airbases);
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                Write(stream, airbases);
        }

        public static void Write(Stream stream, IReadOnlyList<TaxiNetworkData> airbases)
        {
            Check(airbases);

            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(airbases.Count);

                foreach (TaxiNetworkData airbase in airbases)
                {
                    writer.Write(airbase.Airbase ?? "");
                    writer.Write((byte)airbase.Source);

                    List<(float X, float Z)[]> roads = airbase.Roads ?? new List<(float X, float Z)[]>();
                    writer.Write(roads.Count);
                    foreach ((float X, float Z)[] road in roads)
                    {
                        writer.Write(road.Length);
                        foreach ((float x, float z) in road)
                        {
                            writer.Write(x);
                            writer.Write(z);
                        }
                    }

                    List<TaxiExit> exits = airbase.Exits ?? new List<TaxiExit>();
                    writer.Write(exits.Count);
                    foreach (TaxiExit exit in exits)
                    {
                        writer.Write((byte)exit.Runway);
                        writer.Write(exit.Directions);
                        writer.Write(exit.X);
                        writer.Write(exit.Z);
                    }

                    byte flags = (byte)((airbase.ServicePoint.HasValue ? HasServicePoint : 0) |
                                        (airbase.LandingPad.HasValue ? HasLandingPad : 0));
                    writer.Write(flags);

                    if (airbase.ServicePoint.HasValue)
                    {
                        writer.Write(airbase.ServicePoint.Value.X);
                        writer.Write(airbase.ServicePoint.Value.Z);
                    }

                    if (airbase.LandingPad.HasValue)
                    {
                        writer.Write(airbase.LandingPad.Value.X);
                        writer.Write(airbase.LandingPad.Value.Z);
                    }
                }
            }
        }

        /// <summary>Refuses anything the reader would refuse, so what one writes the other takes.</summary>
        static void Check(IReadOnlyList<TaxiNetworkData> airbases)
        {
            if (airbases == null) throw new ArgumentNullException(nameof(airbases));
            if (airbases.Count > MaxAirbases)
                throw new ArgumentException($"{airbases.Count} airbases' taxi lanes; at most {MaxAirbases}");

            foreach (TaxiNetworkData airbase in airbases)
            {
                if (airbase == null) throw new ArgumentException("an airbase's taxi lanes are missing");
                string name = airbase.Airbase ?? "";

                if (!KnownSource((byte)airbase.Source))
                    throw new ArgumentException($"airbase '{name}' has a taxi network made from {(byte)airbase.Source}, which is no source");

                int roads = airbase.Roads?.Count ?? 0;
                if (roads > MaxRoads)
                    throw new ArgumentException($"airbase '{name}' has {roads} taxi roads; at most {MaxRoads}");

                if (airbase.Roads != null)
                    foreach ((float X, float Z)[] road in airbase.Roads)
                    {
                        if (road == null || road.Length < 2 || road.Length > MaxRoadPoints)
                            throw new ArgumentException($"airbase '{name}' has a taxi road of {road?.Length ?? 0} points; " +
                                                        $"2 to {MaxRoadPoints}");
                        foreach ((float x, float z) in road)
                            if (!Finite(x) || !Finite(z))
                                throw new ArgumentException($"airbase '{name}' has a taxi road through no place on the map");
                    }

                int exits = airbase.Exits?.Count ?? 0;
                if (exits > MaxExits)
                    throw new ArgumentException($"airbase '{name}' has {exits} runway exits; at most {MaxExits}");

                if (airbase.Exits != null)
                    foreach (TaxiExit exit in airbase.Exits)
                    {
                        if (exit.Runway < 0 || exit.Runway > AirbaseData.MaxExtraRunways)
                            throw new ArgumentException($"airbase '{name}' has an exit on runway {exit.Runway}");
                        if (exit.Directions == 0 || (exit.Directions & ~(TaxiExit.Forward | TaxiExit.Reverse)) != 0)
                            throw new ArgumentException($"airbase '{name}' has an exit with directions {exit.Directions}");
                        if (!Finite(exit.X) || !Finite(exit.Z))
                            throw new ArgumentException($"airbase '{name}' has an exit at no place on the map");
                    }

                if (airbase.ServicePoint.HasValue && !(Finite(airbase.ServicePoint.Value.X) && Finite(airbase.ServicePoint.Value.Z)))
                    throw new ArgumentException($"airbase '{name}' has its service point at no place on the map");
                if (airbase.LandingPad.HasValue && !(Finite(airbase.LandingPad.Value.X) && Finite(airbase.LandingPad.Value.Z)))
                    throw new ArgumentException($"airbase '{name}' has its landing pad at no place on the map");
            }
        }

        public static List<TaxiNetworkData> Read(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));

            using (var stream = new MemoryStream(bytes, writable: false))
                return Read(stream);
        }

        public static List<TaxiNetworkData> Read(Stream stream)
        {
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                byte[] magic = reader.ReadBytes(Magic.Length);
                if (magic.Length != Magic.Length) throw new InvalidDataException("taxi data is truncated");

                if (!SameBytes(magic, Magic, Magic.Length))
                {
                    bool newer = SameBytes(magic, Magic, Magic.Length - 2);
                    throw new InvalidDataException(newer
                        ? "taxi data is a newer version than this build reads; update the plugin"
                        : "not taxi data: wrong magic");
                }

                int count = reader.ReadInt32();
                if (count < 0 || count > MaxAirbases)
                    throw new InvalidDataException($"taxi airbase count {count} is out of range");

                var airbases = new List<TaxiNetworkData>(count);
                for (int i = 0; i < count; i++)
                {
                    var airbase = new TaxiNetworkData { Airbase = reader.ReadString() };

                    byte source = reader.ReadByte();
                    if (!KnownSource(source)) throw new InvalidDataException($"taxi airbase {i} has a network made from {source}");
                    airbase.Source = (TaxiSource)source;

                    int roads = reader.ReadInt32();
                    if (roads < 0 || roads > MaxRoads)
                        throw new InvalidDataException($"taxi airbase {i} claims {roads} roads");

                    for (int r = 0; r < roads; r++)
                    {
                        int points = reader.ReadInt32();
                        if (points < 2 || points > MaxRoadPoints)
                            throw new InvalidDataException($"taxi airbase {i} has a road of {points} points");

                        var road = new (float X, float Z)[points];
                        for (int p = 0; p < points; p++)
                        {
                            float x = reader.ReadSingle(), z = reader.ReadSingle();
                            if (!Finite(x) || !Finite(z)) throw new InvalidDataException($"taxi airbase {i} has a road through no place");
                            road[p] = (x, z);
                        }

                        airbase.Roads.Add(road);
                    }

                    int exits = reader.ReadInt32();
                    if (exits < 0 || exits > MaxExits)
                        throw new InvalidDataException($"taxi airbase {i} claims {exits} exits");

                    for (int e = 0; e < exits; e++)
                    {
                        var exit = new TaxiExit { Runway = reader.ReadByte(), Directions = reader.ReadByte() };
                        if (exit.Runway > AirbaseData.MaxExtraRunways)
                            throw new InvalidDataException($"taxi airbase {i} has an exit on runway {exit.Runway}");
                        if (exit.Directions == 0 || (exit.Directions & ~(TaxiExit.Forward | TaxiExit.Reverse)) != 0)
                            throw new InvalidDataException($"taxi airbase {i} has an exit with directions {exit.Directions}");

                        exit.X = reader.ReadSingle();
                        exit.Z = reader.ReadSingle();
                        if (!Finite(exit.X) || !Finite(exit.Z)) throw new InvalidDataException($"taxi airbase {i} has an exit at no place");

                        airbase.Exits.Add(exit);
                    }

                    byte flags = reader.ReadByte();
                    if ((flags & ~(HasServicePoint | HasLandingPad)) != 0)
                        throw new InvalidDataException($"taxi airbase {i} has flags {flags}");

                    if ((flags & HasServicePoint) != 0) airbase.ServicePoint = ReadPoint(reader, i, "service point");
                    if ((flags & HasLandingPad) != 0) airbase.LandingPad = ReadPoint(reader, i, "landing pad");

                    airbases.Add(airbase);
                }

                return airbases;
            }
        }

        static (float X, float Z) ReadPoint(BinaryReader reader, int airbase, string what)
        {
            float x = reader.ReadSingle(), z = reader.ReadSingle();
            if (!Finite(x) || !Finite(z)) throw new InvalidDataException($"taxi airbase {airbase} has its {what} at no place");
            return (x, z);
        }

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        static bool KnownSource(byte source) => source <= (byte)TaxiSource.Default;

        static bool SameBytes(byte[] actual, byte[] expected, int length)
        {
            for (int i = 0; i < length; i++)
                if (actual[i] != expected[i]) return false;
            return true;
        }
    }
}
