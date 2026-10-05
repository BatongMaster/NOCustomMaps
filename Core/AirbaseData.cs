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

        /// <summary>
        /// Where the airbase's flag was put by hand, in map metres, or null for the runway centre
        /// (<see cref="X"/>, <see cref="Z"/>), where every airbase's flag stood before it could be
        /// moved. It stands on the platform, at <see cref="Y"/>, and so has to be inside the ground
        /// levelled for the base.
        ///
        /// The flag is the game's <c>Airbase.center</c>, the transform the mission editor hangs its
        /// flag and radius decal on (<c>MissionEditor.CreateFlagForAirbase</c>), and more hangs on it
        /// than the marker: the capture zone is the circle of <see cref="CaptureRangeOrDefault"/>
        /// round it (<c>Capture.GetInRangeUnits</c>), the map's airbase icon is drawn on it
        /// (<c>AirbaseMapIcon</c>), an AI returning to base flies to it, and an aircraft let go by
        /// the base once it is 5 km away (<c>Airbase.ControlAircraft</c>) is measured from it. The
        /// runways, the service points and the spawn camera are not: they keep their own transforms.
        ///
        /// It is the map's default. A mission can move the flag in the game's mission editor, and
        /// then the mission's wins (the plugin's <c>MissionAirbases</c>).
        /// </summary>
        public (float X, float Z)? Flag;

        public bool HasFlag => Flag.HasValue;

        /// <summary>
        /// The capture radius round the flag, in metres, or 0 for
        /// <see cref="AutomaticCaptureRange"/>; otherwise <see cref="AirbaseData.MinCaptureRange"/> to
        /// <see cref="AirbaseData.MaxCaptureRange"/>, as the game's own slider allows. The game's
        /// <c>SavedAirbase.CaptureRange</c>. It is the map's default. The game itself lets the map
        /// win for any airbase built into it (<c>Airbase.LinkSavedAirbase</c> copies the map's
        /// range over a mission's, and the mission editor greys the slider out); on a custom map
        /// the plugin turns that round, so a mission that sets its own range in the mission
        /// editor has it (<c>MissionAirbases</c>).
        ///
        /// It is more than the capture zone. The game takes "near the airbase" to mean inside this
        /// circle round the flag (<c>FactionHQ.AnyNearAirbase</c>): an aircraft its pilot leaves
        /// standing still there goes back into the inventory rather than being abandoned, a rearming
        /// aircraft draws nuclear warheads from the base's store only inside it (<c>Rearmer</c>), and
        /// a taxiing AI finds the base whose taxiways to use by it (<c>AIPilotTaxiState</c>). A
        /// circle that leaves the aprons out leaves them out of all of that.
        /// </summary>
        public float CaptureRange;

        public bool HasCaptureRange => CaptureRange > 0f;

        /// <summary>The share of the levelled ground's larger half-extent the automatic capture
        /// radius covers: the zone should take in the airfield and not the town next to it.</summary>
        public const float CaptureFraction = 0.9f;

        /// <summary>
        /// The capture radius an airbase gets when none was chosen, sized to the ground levelled
        /// for it rather than inherited from the donor it is cloned from: the donor's own radius gave
        /// a small field a zone reaching into the next valley and a large one a zone that did not
        /// cover its aprons. Worked out here, beside the data, so the editor shows exactly the
        /// radius the plugin will give. It is the radius every airbase had before one could be
        /// chosen, so a file without one builds exactly the airbases it always did.
        /// </summary>
        public float AutomaticCaptureRange => Math.Max(FlatHalfAlong, FlatHalfAcross) * CaptureFraction;

        /// <summary>The capture radius the airbase is given: the one chosen, or the automatic one.</summary>
        public float CaptureRangeOrDefault => HasCaptureRange ? CaptureRange : AutomaticCaptureRange;

        /// <summary>Where the flag stands on the map: where it was put, or the runway centre.</summary>
        public (float X, float Z) FlagOrCentre => Flag ?? (X, Z);
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
    /// Five versions:
    ///
    ///   NOAIRB03  count, then per airbase: unique name, display name, faction (strings), x, y, z,
    ///             heading, runway length, runway width, flat half along, flat half across (floats)
    ///   NOAIRB04  the same, then an outline point count (int) and that many x, z pairs (floats)
    ///   NOAIRB05  the same as 4, then an extra runway count (int) and per runway start x, start z,
    ///             end x, end z and width (floats)
    ///   NOAIRB06  the same as 5, then whether the flag was put by hand (a byte, 0 or 1) and if so
    ///             its x, z (floats), then the capture range (float, 0 for automatic, otherwise
    ///             <see cref="AirbaseData.MinCaptureRange"/> to <see cref="AirbaseData.MaxCaptureRange"/>)
    ///
    /// The writer emits the lowest version that holds what it is given: 3 when no airbase has an
    /// outline, 4 when none has an extra runway, 5 when none has a flag put by hand or a capture
    /// range of its own. The Swiss Alps file and any plugin that predates outlines, extra runways or
    /// flags are unaffected until a map uses them.
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

        /// <summary>The version 6 magic: a flag put by hand and a capture range of the airbase's own.</summary>
        public static readonly byte[] Magic6 =
            { (byte)'N', (byte)'O', (byte)'A', (byte)'I', (byte)'R', (byte)'B', (byte)'0', (byte)'6' };

        /// <summary>Sanity bound on a corrupt or hostile file.</summary>
        public const int MaxAirbases = 1024;

        /// <summary>Smallest capture range an airbase may be given, in metres, other than 0 for
        /// automatic: the bottom of the game's own capture range slider (<c>AirbasePanel</c>). A
        /// circle of a few metres would do more than stop capture, since the same circle is what
        /// the game takes to be "at the airbase" (see <see cref="AirbasePlacement.CaptureRange"/>).</summary>
        public const float MinCaptureRange = 10f;

        /// <summary>Largest capture range an airbase may be given, in metres: the top of the game's
        /// own capture range slider (<c>AirbasePanel</c>). The base game's airbases run from about
        /// 750 to 1,560 m.</summary>
        public const float MaxCaptureRange = 10000f;

        /// <summary>Most runways an airbase may have besides its main one. The game keeps a
        /// runway's index in a byte, and no base-game field has more than three.</summary>
        public const int MaxExtraRunways = 8;

        /// <summary>Most points an outline may have.</summary>
        public const int MaxOutlinePoints = 512;

        /// <summary>The version a set of airbases is written as: 6 if any has a flag put by hand or
        /// a capture range of its own, otherwise 5 if any has an extra runway, otherwise 4 if any
        /// has an outline, otherwise 3.</summary>
        public static int VersionFor(IReadOnlyList<AirbasePlacement> airbases)
        {
            if (airbases == null) return 3;

            int version = 3;
            foreach (AirbasePlacement airbase in airbases)
            {
                if (airbase.HasFlag || airbase.HasCaptureRange) return 6;
                version = Math.Max(version, airbase.HasExtraRunways ? 5 : airbase.HasOutline ? 4 : 3);
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

            // Checked before a byte is written, so a refused set leaves no half-written file.
            foreach (AirbasePlacement airbase in airbases) CheckFlag(airbase);

            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(version == 6 ? Magic6 : version == 5 ? Magic5 : version == 4 ? Magic4 : Magic);
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

                    if (version >= 5)
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

                    if (version >= 6)
                    {
                        writer.Write((byte)(airbase.HasFlag ? 1 : 0));
                        if (airbase.HasFlag)
                        {
                            writer.Write(airbase.Flag.Value.X);
                            writer.Write(airbase.Flag.Value.Z);
                        }

                        writer.Write(airbase.HasCaptureRange ? airbase.CaptureRange : 0f);
                    }
                }
            }
        }

        /// <summary>
        /// Refuses a flag or a capture range the plugin could not use: a flag that is not a place
        /// on the map, and a range that is not a number or lies outside the game's own slider
        /// (<see cref="MinCaptureRange"/> to <see cref="MaxCaptureRange"/>) without being 0. The
        /// reader refuses the same, so what one writes the other takes.
        /// </summary>
        static void CheckFlag(AirbasePlacement airbase)
        {
            if (airbase.HasFlag && !(Finite(airbase.Flag.Value.X) && Finite(airbase.Flag.Value.Z)))
                throw new ArgumentException($"airbase '{airbase.UniqueName}' has its flag at no place on the map");

            if (!RangeAllowed(airbase.CaptureRange))
                throw new ArgumentException($"airbase '{airbase.UniqueName}' has a capture range of {airbase.CaptureRange}; " +
                                            $"it has to be 0 (automatic) or {MinCaptureRange:0} to {MaxCaptureRange:0} m");
        }

        /// <summary>Whether a capture range can be written: 0 for automatic, or a radius from
        /// <see cref="MinCaptureRange"/> to <see cref="MaxCaptureRange"/>.</summary>
        public static bool RangeAllowed(float range) =>
            range == 0f || (range >= MinCaptureRange && range <= MaxCaptureRange);

        static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

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
                            : SameBytes(magic, Magic6, Magic6.Length) ? 6
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

                    if (version >= 6)
                    {
                        byte placed = reader.ReadByte();
                        if (placed > 1) throw new InvalidDataException($"airbase {i} has flag marker {placed}");

                        if (placed == 1)
                        {
                            float x = reader.ReadSingle(), z = reader.ReadSingle();
                            if (!Finite(x) || !Finite(z)) throw new InvalidDataException($"airbase {i} has its flag at no place");
                            airbase.Flag = (x, z);
                        }

                        airbase.CaptureRange = reader.ReadSingle();
                        if (!RangeAllowed(airbase.CaptureRange))
                            throw new InvalidDataException($"airbase {i} claims a capture range of {airbase.CaptureRange}");
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
