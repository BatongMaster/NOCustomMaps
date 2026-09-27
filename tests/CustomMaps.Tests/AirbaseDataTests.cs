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
    }
}
