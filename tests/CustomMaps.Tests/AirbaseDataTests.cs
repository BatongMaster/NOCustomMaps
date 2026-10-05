using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace CustomMaps.Tests
{
    /// <summary>
    /// The airbase file format, shared by the generator and the plugin. Identical in both
    /// repositories.
    /// </summary>
    public class AirbaseDataTests
    {
        static AirbasePlacement Placement(string name) => new AirbasePlacement
        {
            UniqueName = name, DisplayName = "Base " + name, Faction = "Boscali",
            X = 1000f, Y = 420.5f, Z = -2000f, Heading = 73f,
            RunwayLength = 2400f, RunwayWidth = 45f, FlatHalfAlong = 1500f, FlatHalfAcross = 400f,
        };

        static byte[] Bytes(List<AirbasePlacement> airbases)
        {
            using var stream = new MemoryStream();
            AirbaseData.Write(stream, airbases);
            return stream.ToArray();
        }

        [Fact]
        public void BasesWithoutOutlinesAreWrittenAsVersion3()
        {
            var airbases = new List<AirbasePlacement> { Placement("a"), Placement("b") };

            Assert.Equal(3, AirbaseData.VersionFor(airbases));
            byte[] bytes = Bytes(airbases);
            Assert.Equal((byte)'3', bytes[7]);

            List<AirbasePlacement> read = AirbaseData.Read(bytes);
            Assert.Equal(2, read.Count);
            Assert.Null(read[0].Outline);
            Assert.Equal(1500f, read[0].FlatHalfAlong);
            Assert.Equal("Boscali", read[1].Faction);
        }

        [Fact]
        public void OutlinesRoundTripAsVersion4()
        {
            AirbasePlacement drawn = Placement("drawn");
            drawn.Outline = new[] { (-500f, -800f), (900f, -700f), (1100f, 1200f), (-400f, 900f) };

            var airbases = new List<AirbasePlacement> { Placement("plain"), drawn };
            Assert.Equal(4, AirbaseData.VersionFor(airbases));

            List<AirbasePlacement> read = AirbaseData.Read(Bytes(airbases));

            Assert.False(read[0].HasOutline);
            Assert.True(read[1].HasOutline);
            Assert.Equal(drawn.Outline, read[1].Outline);
            Assert.Equal(drawn.Heading, read[1].Heading);
            Assert.Equal(drawn.FlatHalfAcross, read[1].FlatHalfAcross);
        }

        [Fact]
        public void AnOutlineWithTooManyPointsIsRefused()
        {
            AirbasePlacement drawn = Placement("big");
            drawn.Outline = new (float, float)[AirbaseData.MaxOutlinePoints + 1];
            for (int i = 0; i < drawn.Outline.Length; i++)
                drawn.Outline[i] = (MathF.Cos(i * 0.01f) * 1000f, MathF.Sin(i * 0.01f) * 1000f);

            Assert.Throws<ArgumentException>(() => Bytes(new List<AirbasePlacement> { drawn }));
        }

        /// <summary>A base with a second runway is written as version 5, and only then: a map
        /// without one stays readable by a plugin that predates extra runways.</summary>
        [Fact]
        public void ExtraRunwaysRoundTripAsVersion5()
        {
            AirbasePlacement drawn = Placement("drawn");
            drawn.Outline = new[] { (-500f, -800f), (900f, -700f), (1100f, 1200f), (-400f, 900f) };

            var outlined = new List<AirbasePlacement> { Placement("plain"), drawn };
            Assert.Equal(4, AirbaseData.VersionFor(outlined));

            AirbasePlacement crossed = drawn;
            crossed.UniqueName = "crossed";
            crossed.ExtraRunways = new[]
            {
                new AirbaseRunway { StartX = 100f, StartZ = -300f, EndX = 900f, EndZ = 500f, Width = 30f },
                new AirbaseRunway { StartX = -200f, StartZ = 0f, EndX = -200f, EndZ = 800f, Width = 25f },
            };

            var airbases = new List<AirbasePlacement> { Placement("plain"), crossed };
            Assert.Equal(5, AirbaseData.VersionFor(airbases));

            byte[] bytes = Bytes(airbases);
            Assert.Equal((byte)'5', bytes[7]);

            List<AirbasePlacement> read = AirbaseData.Read(bytes);
            Assert.False(read[0].HasExtraRunways);
            Assert.Equal(crossed.ExtraRunways, read[1].ExtraRunways);
            Assert.Equal(crossed.Outline, read[1].Outline);
            Assert.Equal(crossed.RunwayLength, read[1].RunwayLength);
        }

        [Fact]
        public void TooManyExtraRunwaysAreRefused()
        {
            AirbasePlacement many = Placement("many");
            many.ExtraRunways = new AirbaseRunway[AirbaseData.MaxExtraRunways + 1];
            Assert.Throws<ArgumentException>(() => Bytes(new List<AirbasePlacement> { many }));
        }

        [Fact]
        public void ANewerVersionSaysToUpdate()
        {
            byte[] bytes = Bytes(new List<AirbasePlacement> { Placement("a") });
            bytes[7] = (byte)'9';
            var newer = Assert.Throws<InvalidDataException>(() => AirbaseData.Read(bytes));
            Assert.Contains("newer", newer.Message);

            bytes[0] = (byte)'X';
            var foreign = Assert.Throws<InvalidDataException>(() => AirbaseData.Read(bytes));
            Assert.Contains("not airbase data", foreign.Message);
        }

        // ---------------------------------------------------------------- the flag and capture range

        static readonly (float X, float Z)[] Square = { (-500f, -800f), (900f, -700f), (1100f, 1200f), (-400f, 900f) };

        static readonly AirbaseRunway[] Crossing =
        {
            new AirbaseRunway { StartX = 100f, StartZ = -300f, EndX = 900f, EndZ = 500f, Width = 30f },
        };

        /// <summary>
        /// A set of airbases that needs exactly the given version: a plain base, then one with what
        /// each later version added, cumulatively, so a version 6 set also has an outline and an
        /// extra runway to carry.
        /// </summary>
        static List<AirbasePlacement> SetFor(int version)
        {
            AirbasePlacement drawn = Placement("drawn");
            if (version >= 4) drawn.Outline = Square;
            if (version >= 5) drawn.ExtraRunways = Crossing;
            if (version >= 6)
            {
                drawn.Flag = (1234.5f, -1987.25f);
                drawn.CaptureRange = 1100f;
            }

            return new List<AirbasePlacement> { Placement("plain"), drawn };
        }

        /// <summary>
        /// The file as the format note lays it out, written here by hand, field by field, for the
        /// versions that were there before the flag: what every older plugin reads. A writer that
        /// emitted anything else for a map without a flag would lose those plugins the map.
        /// </summary>
        static byte[] OldLayout(int version, List<AirbasePlacement> airbases)
        {
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("NOAIRB0" + version));
                writer.Write(airbases.Count);

                foreach (AirbasePlacement airbase in airbases)
                {
                    writer.Write(airbase.UniqueName);
                    writer.Write(airbase.DisplayName);
                    writer.Write(airbase.Faction);
                    foreach (float value in new[] { airbase.X, airbase.Y, airbase.Z, airbase.Heading, airbase.RunwayLength,
                                                    airbase.RunwayWidth, airbase.FlatHalfAlong, airbase.FlatHalfAcross })
                        writer.Write(value);

                    if (version >= 4)
                    {
                        (float X, float Z)[] outline = airbase.Outline ?? Array.Empty<(float, float)>();
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
                        writer.Write(runways.Length);
                        foreach (AirbaseRunway runway in runways)
                            foreach (float value in new[] { runway.StartX, runway.StartZ, runway.EndX, runway.EndZ, runway.Width })
                                writer.Write(value);
                    }
                }
            }

            return stream.ToArray();
        }

        /// <summary>Every version is written only when it is needed, as the lowest that holds the set,
        /// and comes back whole: 3 for plain bases, 4 with an outline, 5 with an extra runway, 6 with
        /// a flag or a capture range.</summary>
        [Theory]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        [InlineData(6)]
        public void EveryVersionRoundTripsAsTheLowestThatHoldsIt(int version)
        {
            List<AirbasePlacement> airbases = SetFor(version);
            Assert.Equal(version, AirbaseData.VersionFor(airbases));

            byte[] bytes = Bytes(airbases);
            Assert.Equal((byte)('0' + version), bytes[7]);

            List<AirbasePlacement> read = AirbaseData.Read(bytes);
            Assert.Equal(2, read.Count);

            AirbasePlacement written = airbases[1], back = read[1];
            Assert.Equal(written.UniqueName, back.UniqueName);
            Assert.Equal(written.X, back.X);
            Assert.Equal(written.Y, back.Y);
            Assert.Equal(written.Heading, back.Heading);
            Assert.Equal(written.FlatHalfAcross, back.FlatHalfAcross);
            Assert.Equal(written.Outline, back.Outline);
            Assert.Equal(written.ExtraRunways, back.ExtraRunways);
            Assert.Equal(written.Flag, back.Flag);
            Assert.Equal(written.CaptureRange, back.CaptureRange);

            // The plain base beside it reads as plain in every version, the flag's version included.
            Assert.False(read[0].HasOutline);
            Assert.False(read[0].HasExtraRunways);
            Assert.False(read[0].HasFlag);
            Assert.Equal(0f, read[0].CaptureRange);
        }

        /// <summary>A map without a flag or a capture range is written exactly as it was before
        /// either existed, byte for byte, in each of the versions an older plugin reads.</summary>
        [Theory]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void AMapWithoutAFlagIsWrittenAsBefore(int version)
        {
            List<AirbasePlacement> airbases = SetFor(version);
            Assert.Equal(OldLayout(version, airbases), Bytes(airbases));
        }

        /// <summary>Version 6 is version 5 with each base's flag and range after its runways, which is
        /// what lets it carry outlines and extra runways unchanged.</summary>
        [Fact]
        public void Version6IsVersion5WithTheFlagAfterEachBase()
        {
            List<AirbasePlacement> airbases = SetFor(6);
            byte[] six = Bytes(airbases);
            byte[] five = OldLayout(5, airbases);

            // Per base a marker byte and the range, and for the base whose flag was put by hand its
            // x and z as well.
            Assert.Equal(five.Length + (1 + 4) + (1 + 8 + 4), six.Length);

            // The plain base, first, is its version 5 bytes and then its five of the flag's: no flag,
            // and an automatic range. It ends where the second base's version 5 bytes begin.
            int second = OldLayout(5, new List<AirbasePlacement> { airbases[1] }).Length - 12;
            int plainEnd = five.Length - second;
            Assert.Equal(five.AsSpan(8, plainEnd - 8).ToArray(), six.AsSpan(8, plainEnd - 8).ToArray());
            Assert.Equal(0, six[plainEnd]);
            Assert.Equal(0f, BitConverter.ToSingle(six, plainEnd + 1));

            List<AirbasePlacement> read = AirbaseData.Read(six);
            Assert.Equal(airbases[1].Outline, read[1].Outline);
            Assert.Equal(airbases[1].ExtraRunways, read[1].ExtraRunways);
        }

        /// <summary>A flag alone or a range alone is enough for version 6, and the other reads back
        /// as not set.</summary>
        [Fact]
        public void AFlagOrARangeAloneMakesVersion6()
        {
            AirbasePlacement flagged = Placement("flagged");
            flagged.Flag = (1500f, -1800f);

            AirbasePlacement ranged = Placement("ranged");
            ranged.CaptureRange = 2500f;

            var airbases = new List<AirbasePlacement> { flagged, ranged };
            Assert.Equal(6, AirbaseData.VersionFor(airbases));
            Assert.Equal(6, AirbaseData.VersionFor(new List<AirbasePlacement> { flagged }));
            Assert.Equal(6, AirbaseData.VersionFor(new List<AirbasePlacement> { ranged }));

            List<AirbasePlacement> read = AirbaseData.Read(Bytes(airbases));

            Assert.True(read[0].HasFlag);
            Assert.Equal((1500f, -1800f), read[0].Flag.Value);
            Assert.Equal((1500f, -1800f), read[0].FlagOrCentre);
            Assert.False(read[0].HasCaptureRange);
            Assert.Equal(read[0].AutomaticCaptureRange, read[0].CaptureRangeOrDefault);

            Assert.False(read[1].HasFlag);
            Assert.Equal((read[1].X, read[1].Z), read[1].FlagOrCentre);
            Assert.Equal(2500f, read[1].CaptureRangeOrDefault);
        }

        /// <summary>
        /// Without a flag put by hand the flag stands on the middle of the runway, which is where the
        /// plugin stood every airbase's centre before the flag could be moved; and the capture range
        /// is the automatic one, the radius every airbase was given before one could be chosen: nine
        /// tenths of the larger half of the levelled rectangle. Checked on a base read from each
        /// older version, so an old map loads with the airbases it always had.
        /// </summary>
        [Theory]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void AnOlderFileKeepsItsFlagOnTheRunwayAndItsAutomaticRange(int version)
        {
            AirbasePlacement read = AirbaseData.Read(OldLayout(version, SetFor(version)))[1];

            Assert.False(read.HasFlag);
            Assert.Null(read.Flag);
            Assert.Equal((1000f, -2000f), read.FlagOrCentre);

            Assert.False(read.HasCaptureRange);
            Assert.Equal(1350f, read.AutomaticCaptureRange);
            Assert.Equal(1350f, read.CaptureRangeOrDefault);

            // The plugin's own sum from before the radius moved in beside the data, to the bit.
            Assert.Equal(MathF.Max(read.FlatHalfAlong, read.FlatHalfAcross) * 0.9f, read.CaptureRangeOrDefault);
        }

        /// <summary>The automatic range follows the larger half-extent, whichever way it runs, and a
        /// range of the base's own replaces it.</summary>
        [Fact]
        public void TheAutomaticRangeIsSizedFromTheLevelledGround()
        {
            AirbasePlacement airbase = Placement("a");
            airbase.FlatHalfAlong = 300f;
            airbase.FlatHalfAcross = 800f;
            Assert.Equal(720f, airbase.AutomaticCaptureRange);
            Assert.Equal(720f, airbase.CaptureRangeOrDefault);

            airbase.CaptureRange = 1560f;
            Assert.True(airbase.HasCaptureRange);
            Assert.Equal(1560f, airbase.CaptureRangeOrDefault);
            Assert.Equal(720f, airbase.AutomaticCaptureRange);
        }

        /// <summary>A flag at no place, or a range that is negative, not a number, or outside the game's
        /// own slider without being 0, is refused before anything is written; and a file claiming one is
        /// refused when read.</summary>
        [Fact]
        public void AFlagOrRangeThePluginCannotUseIsRefused()
        {
            foreach (float range in new[] { -1f, float.NaN, float.PositiveInfinity, float.Epsilon, 5f,
                                            AirbaseData.MinCaptureRange - 0.5f, AirbaseData.MaxCaptureRange + 1f })
            {
                AirbasePlacement bad = Placement("bad");
                bad.CaptureRange = range;

                var stream = new MemoryStream();
                Assert.Throws<ArgumentException>(() => AirbaseData.Write(stream, new List<AirbasePlacement> { bad }));
                Assert.Equal(0, stream.Length);
            }

            AirbasePlacement nowhere = Placement("nowhere");
            nowhere.Flag = (float.NaN, 0f);
            Assert.Throws<ArgumentException>(() => Bytes(new List<AirbasePlacement> { nowhere }));

            AirbasePlacement edge = Placement("edge");
            edge.CaptureRange = AirbaseData.MaxCaptureRange;
            Assert.Equal(AirbaseData.MaxCaptureRange, AirbaseData.Read(Bytes(new List<AirbasePlacement> { edge }))[0].CaptureRange);
            edge.CaptureRange = AirbaseData.MinCaptureRange;
            Assert.Equal(AirbaseData.MinCaptureRange, AirbaseData.Read(Bytes(new List<AirbasePlacement> { edge }))[0].CaptureRange);

            // One base with a flag, so its marker is the byte after its runways and its range the last
            // four bytes of the file.
            AirbasePlacement flagged = Placement("f");
            flagged.Flag = (1f, 2f);
            byte[] bytes = Bytes(new List<AirbasePlacement> { flagged });

            byte[] marker = (byte[])bytes.Clone();
            marker[bytes.Length - 13] = 2;
            Assert.Throws<InvalidDataException>(() => AirbaseData.Read(marker));

            byte[] negative = (byte[])bytes.Clone();
            BitConverter.GetBytes(-5f).CopyTo(negative, bytes.Length - 4);
            Assert.Throws<InvalidDataException>(() => AirbaseData.Read(negative));

            byte[] tiny = (byte[])bytes.Clone();
            BitConverter.GetBytes(float.Epsilon).CopyTo(tiny, bytes.Length - 4);
            Assert.Throws<InvalidDataException>(() => AirbaseData.Read(tiny));
        }
    }
}
