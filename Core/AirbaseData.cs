using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CustomMaps
{
    /// <summary>
    /// A runway an airbase has besides its main one, by its two thresholds in map metres. It lies
    /// on the same levelled platform as the main runway, at <see cref="AirbasePlacement.Y"/>.
    /// </summary>
    public struct AirbaseRunway
    {
        public float StartX, StartZ, EndX, EndZ;

        public float Width;
    }

    /// <summary>One airbase, placed on the map.</summary>
    public struct AirbasePlacement
    {
        /// <summary>Key the game registers the airbase under, and the one a mission names
        /// to attach objectives or spawns to it. Must be unique across the map.</summary>
        public string UniqueName;

        /// <summary>What a player sees on the map and in the spawn menu.</summary>
        public string DisplayName;

        /// <summary>Faction that holds it at mission start, by the game's own faction name.</summary>
        public string Faction;

        /// <summary>Centre of the airfield in map metres, with Y the level the runway
        /// platform was cut to.</summary>
        public float X, Y, Z;

        /// <summary>True bearing the runway runs on, in degrees.</summary>
        public float Heading;

        /// <summary>Runway dimensions in metres. Carried so the platform the terrain was
        /// flattened to and the airbase placed on it can be checked against each other.</summary>
        public float RunwayLength, RunwayWidth;

        /// <summary>
        /// The levelled rectangle, as half-extents from the runway centre in metres:
        /// <see cref="FlatHalfAlong"/> down the runway, <see cref="FlatHalfAcross"/> out to
        /// either side of it.
        ///
        /// Two numbers rather than one radius because the ground an airfield gets is not
        /// square and the difference is the whole point. A valley floor is long and narrow,
        /// and a single figure has to describe it by its smaller dimension — which throws
        /// away every metre of length the valley was offering. Measured at Sion, the floor
        /// is dead flat to 800 m either side of the strip but climbs past 1,760 m along it,
        /// so one number collapsed a field that could have had 760 m of apron down to 460.
        ///
        /// These are the authority on where an airfield may put anything: the surface
        /// geometry is cut to them, so nothing can overhang ground that was never levelled.
        /// </summary>
        public float FlatHalfAlong, FlatHalfAcross;

        /// <summary>
        /// The outline the airbase was drawn and levelled to, in map metres, or null for a base
        /// levelled as the rectangle the half-extents describe (the Swiss airfields).
        ///
        /// When present it, not the rectangle, is the ground the base occupies: nothing else is
        /// built inside it, and roads stop at its edge unless they were drawn to cross it; no
        /// bridge or tunnel goes onto it. The half-extents are then the largest runway-aligned
        /// rectangle inside the outline — still what the base's own buildings are laid out in.
        /// </summary>
        public (float X, float Z)[] Outline;

        public bool HasOutline => Outline != null && Outline.Length >= 3;

        /// <summary>
        /// The runways the base has besides the one <see cref="Heading"/> and
        /// <see cref="RunwayLength"/> describe, or null. The main runway stays the first: the
        /// base's buildings, its platform level and every older reader go by it alone.
        /// </summary>
        public AirbaseRunway[] ExtraRunways;

        public bool HasExtraRunways => ExtraRunways != null && ExtraRunways.Length > 0;
    }

    /// <summary>
    /// The map's airbases, as the generator works them out and the plugin builds them.
    ///
    /// Where each airbase goes, what it is called, and how much ground was levelled for it.
    ///
    /// The visible airfield — runway, taxiway, aprons — is built from these figures into the
    /// bundle, so it fits the levelled ground by construction. What cannot be built there is
    /// the <c>Airbase</c> component itself: it is a game type carrying runways, service
    /// points, a tower, lights and a taxi network, and this bundle deliberately contains no
    /// game types. That part is borrowed from a shipped map at load and told where its
    /// runway now is.
    ///
    /// Two versions:
    ///
    ///   NOAIRB03  count, then per airbase: unique name, display name, faction (strings), x, y, z,
    ///             heading, runway length, runway width, flat half along, flat half across (floats)
    ///   NOAIRB04  the same, then an outline point count (int) and that many x, z pairs (floats)
    ///   NOAIRB05  the same as 4, then an extra runway count (int) and per runway start x, start z,
    ///             end x, end z and width (floats)
    ///
    /// The writer emits the lowest version that holds what it is given: 3 when no airbase has an
    /// outline, 4 when none has an extra runway. The Swiss Alps file and any plugin that predates
    /// outlines or extra runways are unaffected until a map uses them.
    /// </summary>
    public static class AirbaseData
    {
        public static readonly byte[] Magic =
            { (byte)'N', (byte)'O', (byte)'A', (byte)'I', (byte)'R', (byte)'B', (byte)'0', (byte)'3' };

        /// <summary>The version 4 magic: outlines.</summary>
        public static readonly byte[] Magic4 =
            { (byte)'N', (byte)'O', (byte)'A', (byte)'I', (byte)'R', (byte)'B', (byte)'0', (byte)'4' };

        /// <summary>The version 5 magic: extra runways.</summary>
        public static readonly byte[] Magic5 =
            { (byte)'N', (byte)'O', (byte)'A', (byte)'I', (byte)'R', (byte)'B', (byte)'0', (byte)'5' };

        /// <summary>Sanity bound on a corrupt or hostile file.</summary>
        public const int MaxAirbases = 1024;

        /// <summary>Most runways an airbase may have besides its main one. The game keeps a
        /// runway's index in a byte, and no base-game field has more than three.</summary>
        public const int MaxExtraRunways = 8;

        /// <summary>Most points an outline may have.</summary>
        public const int MaxOutlinePoints = 512;

        /// <summary>The version a set of airbases is written as: 5 if any has an extra runway,
        /// otherwise 4 if any has an outline, otherwise 3.</summary>
        public static int VersionFor(IReadOnlyList<AirbasePlacement> airbases)
        {
            if (airbases == null) return 3;

            int version = 3;
            foreach (AirbasePlacement airbase in airbases)
            {
                if (airbase.HasExtraRunways) return 5;
                if (airbase.HasOutline) version = 4;
            }

            return version;
        }

        public static void Write(string path, IReadOnlyList<AirbasePlacement> airbases)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                Write(stream, airbases);
        }

        public static void Write(Stream stream, IReadOnlyList<AirbasePlacement> airbases)
        {
            if (airbases == null) throw new ArgumentNullException(nameof(airbases));

            int version = VersionFor(airbases);
            bool version4 = version >= 4;

            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(version == 5 ? Magic5 : version == 4 ? Magic4 : Magic);
                writer.Write(airbases.Count);

                foreach (AirbasePlacement airbase in airbases)
                {
                    writer.Write(airbase.UniqueName ?? "");
                    writer.Write(airbase.DisplayName ?? "");
                    writer.Write(airbase.Faction ?? "");
                    writer.Write(airbase.X);
                    writer.Write(airbase.Y);
                    writer.Write(airbase.Z);
                    writer.Write(airbase.Heading);
                    writer.Write(airbase.RunwayLength);
                    writer.Write(airbase.RunwayWidth);
                    writer.Write(airbase.FlatHalfAlong);
                    writer.Write(airbase.FlatHalfAcross);

                    if (version4)
                    {
                        (float X, float Z)[] outline = airbase.HasOutline ? airbase.Outline : Array.Empty<(float, float)>();
                        if (outline.Length > MaxOutlinePoints)
                            throw new ArgumentException($"airbase '{airbase.UniqueName}' has {outline.Length} outline points; " +
                                                        $"at most {MaxOutlinePoints}");

                        writer.Write(outline.Length);
                        foreach ((float x, float z) in outline)
                        {
                            writer.Write(x);
                            writer.Write(z);
                        }
                    }

                    if (version == 5)
                    {
                        AirbaseRunway[] runways = airbase.ExtraRunways ?? Array.Empty<AirbaseRunway>();
                        if (runways.Length > MaxExtraRunways)
                            throw new ArgumentException($"airbase '{airbase.UniqueName}' has {runways.Length} extra runways; " +
                                                        $"at most {MaxExtraRunways}");

                        writer.Write(runways.Length);
                        foreach (AirbaseRunway runway in runways)
                        {
                            writer.Write(runway.StartX);
                            writer.Write(runway.StartZ);
                            writer.Write(runway.EndX);
                            writer.Write(runway.EndZ);
                            writer.Write(runway.Width);
                        }
                    }
                }
            }
        }

        public static List<AirbasePlacement> Read(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));

            using (var stream = new MemoryStream(bytes, writable: false))
                return Read(stream);
        }

        public static List<AirbasePlacement> Read(Stream stream)
        {
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                byte[] magic = reader.ReadBytes(Magic.Length);
                if (magic.Length != Magic.Length) throw new InvalidDataException("airbase data is truncated");

                int version = SameBytes(magic, Magic, Magic.Length) ? 3
                            : SameBytes(magic, Magic4, Magic4.Length) ? 4
                            : SameBytes(magic, Magic5, Magic5.Length) ? 5
                            : 0;
                if (version == 0)
                {
                    bool newer = SameBytes(magic, Magic, Magic.Length - 2);
                    throw new InvalidDataException(newer
                        ? "airbase data is a newer version than this build reads — update the plugin"
                        : "not airbase data — wrong magic");
                }

                int count = reader.ReadInt32();
                if (count < 0 || count > MaxAirbases)
                    throw new InvalidDataException($"airbase count {count} is out of range");

                var airbases = new List<AirbasePlacement>(count);
                for (int i = 0; i < count; i++)
                {
                    var airbase = new AirbasePlacement
                    {
                        UniqueName = reader.ReadString(),
                        DisplayName = reader.ReadString(),
                        Faction = reader.ReadString(),
                        X = reader.ReadSingle(),
                        Y = reader.ReadSingle(),
                        Z = reader.ReadSingle(),
                        Heading = reader.ReadSingle(),
                        RunwayLength = reader.ReadSingle(),
                        RunwayWidth = reader.ReadSingle(),
                        FlatHalfAlong = reader.ReadSingle(),
                        FlatHalfAcross = reader.ReadSingle(),
                    };

                    if (version >= 4)
                    {
                        int points = reader.ReadInt32();
                        if (points < 0 || points > MaxOutlinePoints)
                            throw new InvalidDataException($"airbase {i} claims {points} outline points");

                        if (points > 0)
                        {
                            airbase.Outline = new (float X, float Z)[points];
                            for (int k = 0; k < points; k++) airbase.Outline[k] = (reader.ReadSingle(), reader.ReadSingle());
                        }
                    }

                    if (version >= 5)
                    {
                        int runways = reader.ReadInt32();
                        if (runways < 0 || runways > MaxExtraRunways)
                            throw new InvalidDataException($"airbase {i} claims {runways} extra runways");

                        if (runways > 0)
                        {
                            airbase.ExtraRunways = new AirbaseRunway[runways];
                            for (int k = 0; k < runways; k++)
                                airbase.ExtraRunways[k] = new AirbaseRunway
                                {
                                    StartX = reader.ReadSingle(),
                                    StartZ = reader.ReadSingle(),
                                    EndX = reader.ReadSingle(),
                                    EndZ = reader.ReadSingle(),
                                    Width = reader.ReadSingle(),
                                };
                        }
                    }

                    airbases.Add(airbase);
                }

                return airbases;
            }
        }

        static bool SameBytes(byte[] actual, byte[] expected, int length)
        {
            for (int i = 0; i < length; i++)
                if (actual[i] != expected[i]) return false;
            return true;
        }
    }
}
