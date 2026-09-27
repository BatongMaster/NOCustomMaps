using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace CustomMaps.Tests
{
    /// <summary>
    /// The road file format, shared by the generator and the plugin. Identical in both
    /// repositories.
    /// </summary>
    public class RoadDataTests
    {
        static byte[] Bytes(List<RoadRecord> roads)
        {
            using var stream = new MemoryStream();
            RoadData.Write(stream, roads);
            return stream.ToArray();
        }

        static RoadRecord Plain(byte roadClass, float tunnel, params float[] xyz)
            => new RoadRecord { Class = roadClass, TunnelFraction = tunnel, Points = xyz };

        /// <summary>The version 1 layout, written out by hand: what every file before version 2
        /// looked like, and what a plain set of records must still produce byte for byte.</summary>
        static byte[] Version1(List<RoadRecord> roads)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write(new[] { (byte)'N', (byte)'O', (byte)'R', (byte)'O', (byte)'A', (byte)'D', (byte)'0', (byte)'1' });
            writer.Write(roads.Count);
            foreach (RoadRecord road in roads)
            {
                writer.Write(road.Class);
                writer.Write(road.TunnelFraction);
                writer.Write(road.Count);
                foreach (float value in road.Points) writer.Write(value);
            }
            writer.Flush();
            return stream.ToArray();
        }

        [Fact]
        public void PlainRoadsAreWrittenAsVersion1ByteForByte()
        {
            var roads = new List<RoadRecord>
            {
                Plain(0, 0f, 1f, 2f, 3f, 4f, 5f, 6f),
                Plain(3, 0.75f, -10f, 20f, -30f, 40f, 50f, 60f, 70f, 80f, 90f),
            };

            Assert.Equal(1, RoadData.VersionFor(roads));
            Assert.Equal(Version1(roads), Bytes(roads));
        }

        [Fact]
        public void FlagsOrAStartOffsetMakeVersion2()
        {
            var bridge = new List<RoadRecord> { Plain(2, 0f, 0f, 10f, 0f, 0f, 10f, 100f) };
            bridge[0].Flags = RoadData.FlagBridge;
            Assert.Equal(2, RoadData.VersionFor(bridge));

            var offset = new List<RoadRecord> { Plain(2, 0f, 0f, 10f, 0f, 0f, 10f, 100f) };
            offset[0].StartAlong = 125f;
            Assert.Equal(2, RoadData.VersionFor(offset));

            byte[] bytes = Bytes(bridge);
            Assert.Equal((byte)'2', bytes[7]);
        }

        [Fact]
        public void Version2RoundTripsEveryField()
        {
            var roads = new List<RoadRecord>
            {
                new RoadRecord { Class = 1, Flags = RoadData.FlagBridge, TunnelFraction = 0f, StartAlong = 812.5f,
                                 Points = new[] { 0f, 42f, 0f, 0f, 55f, 250f, 0f, 43f, 500f } },
                new RoadRecord { Class = 2, Flags = RoadData.FlagTunnel, TunnelFraction = 1f, StartAlong = 0f,
                                 Points = new[] { 1f, 2f, 3f, 4f, 5f, 6f } },
                Plain(3, 0.25f, 7f, 8f, 9f, 10f, 11f, 12f),
            };

            List<RoadRecord> read = RoadData.Read(Bytes(roads));

            Assert.Equal(roads.Count, read.Count);
            for (int i = 0; i < roads.Count; i++)
            {
                Assert.Equal(roads[i].Class, read[i].Class);
                Assert.Equal(roads[i].Flags, read[i].Flags);
                Assert.Equal(roads[i].TunnelFraction, read[i].TunnelFraction);
                Assert.Equal(roads[i].StartAlong, read[i].StartAlong);
                Assert.Equal(roads[i].Points, read[i].Points);
            }

            Assert.True(read[0].IsBridge && read[0].IsStructure && !read[0].IsTunnel);
            Assert.True(read[1].IsTunnel && read[1].IsStructure);
            Assert.False(read[2].IsStructure);
        }

        [Fact]
        public void Version1ReadsWithNoFlagsAndNoOffset()
        {
            var roads = new List<RoadRecord> { Plain(0, 0.5f, 1f, 2f, 3f, 4f, 5f, 6f) };
            List<RoadRecord> read = RoadData.Read(Version1(roads));

            RoadRecord road = Assert.Single(read);
            Assert.Equal(0, road.Flags);
            Assert.Equal(0f, road.StartAlong);
            Assert.Equal(0.5f, road.TunnelFraction);
            Assert.Equal(roads[0].Points, road.Points);
        }

        [Fact]
        public void ANewerVersionSaysToUpdateRatherThanNotRoadData()
        {
            byte[] bytes = Bytes(new List<RoadRecord> { Plain(0, 0f, 1f, 2f, 3f, 4f, 5f, 6f) });
            bytes[7] = (byte)'9';

            var newer = Assert.Throws<InvalidDataException>(() => RoadData.Read(bytes));
            Assert.Contains("newer", newer.Message);

            bytes[0] = (byte)'X';
            var foreign = Assert.Throws<InvalidDataException>(() => RoadData.Read(bytes));
            Assert.Contains("not road data", foreign.Message);
        }

        [Fact]
        public void TruncatedAndImplausibleFilesAreRefused()
        {
            Assert.Throws<InvalidDataException>(() => RoadData.Read(new byte[] { (byte)'N', (byte)'O' }));

            byte[] bytes = Bytes(new List<RoadRecord> { Plain(0, 0f, 1f, 2f, 3f, 4f, 5f, 6f) });
            BitConverter.GetBytes(RoadData.MaxRoads + 1).CopyTo(bytes, 8);
            Assert.Throws<InvalidDataException>(() => RoadData.Read(bytes));
        }

        [Fact]
        public void JunctionsFuseWithinTheRadiusInThreeDimensions()
        {
            var roads = new List<RoadRecord>
            {
                Plain(0, 0f, 0f, 0f, 0f, 100f, 0f, 0f),
                Plain(0, 0f, 100f, 0f, 9.9f, 200f, 0f, 0f),
                Plain(0, 0f, 100f, 10.1f, 0f, 100f, 0f, 300f),
            };

            // (0,0,0), (100,0,0) fused with (100,0,9.9), (200,0,0), (100,10.1,0) apart, (100,0,300).
            Assert.Equal(5, RoadData.CountNodes(roads));
        }
    }
}
