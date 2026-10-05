using System;
using System.Collections.Generic;
using Xunit;

namespace CustomMaps.Tests
{
    /// <summary>
    /// The network an airbase is given when nothing was drawn for it (<see cref="TaxiDefault"/>): built
    /// by the game's rules, in the place the donor's service point and a generated layout's taxiway
    /// say, and inside the ground levelled for the field. The plugin's copy of these tests is the same
    /// file.
    /// </summary>
    public class TaxiDefaultTests
    {
        /// <summary>A 2,400 m runway on 064° through the origin, 45 m wide, and the donor's service point,
        /// 56 m along and 206 m right of its middle.</summary>
        static AirbasePlacement Placement(float length = 2400f, float heading = 64f)
        {
            return new AirbasePlacement
            {
                UniqueName = "LSGG",
                X = 1000f,
                Y = 400f,
                Z = -2000f,
                Heading = heading,
                RunwayLength = length,
                RunwayWidth = 45f,
                FlatHalfAlong = length * 0.5f + 150f,
                FlatHalfAcross = 400f,
            };
        }

        static (float X, float Z) DonorServicePoint(in AirbasePlacement placement)
        {
            double radians = placement.Heading * Math.PI / 180.0;
            double ux = Math.Sin(radians), uz = Math.Cos(radians);
            return ((float)(placement.X + ux * 56.0 + uz * 206.0), (float)(placement.Z + uz * 56.0 - ux * 206.0));
        }

        /// <summary>Along the runway from its middle and across it, right positive.</summary>
        static (double Along, double Across) Frame(in AirbasePlacement placement, (float X, float Z) point)
        {
            double radians = placement.Heading * Math.PI / 180.0;
            double ux = Math.Sin(radians), uz = Math.Cos(radians);
            double x = point.X - placement.X, z = point.Z - placement.Z;
            return (x * ux + z * uz, x * uz - z * ux);
        }

        static double Distance((float X, float Z) a, (float X, float Z) b)
            => Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Z - b.Z) * (a.Z - b.Z));

        /// <summary>The game's nodes: every road end joins a node already made within 10 m.</summary>
        static List<(float X, float Z)> Nodes(TaxiNetworkData data)
        {
            var nodes = new List<(float X, float Z)>();
            foreach ((float X, float Z)[] road in data.Roads)
                foreach ((float X, float Z) end in new[] { road[0], road[road.Length - 1] })
                {
                    bool found = false;
                    foreach ((float X, float Z) node in nodes)
                        if (Distance(node, end) < 10.0) { found = true; break; }
                    if (!found) nodes.Add(end);
                }

            return nodes;
        }

        [Fact]
        public void ItRunsBesideTheRunwayOntoBothEndsAndHalfWay()
        {
            AirbasePlacement placement = Placement();
            TaxiNetworkData data = TaxiDefault.Build(placement, DonorServicePoint(placement));

            Assert.Equal("LSGG", data.Airbase);
            Assert.Equal(TaxiSource.Default, data.Source);
            Assert.True(data.HasNetwork);
            Assert.Equal(DonorServicePoint(placement), data.ServicePoint);

            // 2,400 m: links at both ends and two between, so three pieces of lane and four links.
            Assert.Equal(7, data.Roads.Count);

            ((float X, float Z) start, (float X, float Z) end) = TaxiDefault.MainRunway(placement);
            var ends = new List<(float X, float Z)>();
            foreach ((float X, float Z)[] road in data.Roads)
            {
                ends.Add(road[0]);
                ends.Add(road[road.Length - 1]);
            }

            // Onto both thresholds exactly, which the AI taxis to for take-off.
            Assert.Contains(start, ends);
            Assert.Contains(end, ends);

            // The lane 150 m right of the runway's axis, where a generated Medium layout paves its taxiway.
            int onLane = 0;
            foreach ((float X, float Z)[] road in data.Roads)
            {
                (double _, double a0) = Frame(placement, road[0]);
                (double _, double a1) = Frame(placement, road[road.Length - 1]);
                if (Math.Abs(a0 - 150.0) < 0.01 && Math.Abs(a1 - 150.0) < 0.01) onLane++;
            }

            Assert.Equal(3, onLane);
        }

        [Fact]
        public void ItKeepsTheGamesRulesForANetwork()
        {
            foreach (float length in new[] { 60f, 900f, 1550f, 2400f, 3700f })
            {
                AirbasePlacement placement = Placement(length);
                TaxiNetworkData data = TaxiDefault.Build(placement, DonorServicePoint(placement));
                List<(float X, float Z)> nodes = Nodes(data);

                // Every two junctions further apart than the game's 10 m: none fused by it.
                for (int i = 0; i < nodes.Count; i++)
                    for (int j = i + 1; j < nodes.Count; j++)
                        Assert.True(Distance(nodes[i], nodes[j]) > 10.5, $"{length} m: two junctions {Distance(nodes[i], nodes[j]):0.0} m apart");

                var pairs = new HashSet<(int, int)>();
                foreach ((float X, float Z)[] road in data.Roads)
                {
                    Assert.True(road.Length >= 2);

                    // From one junction to another: its ends are nodes and not the same one.
                    int a = nodes.FindIndex(n => Distance(n, road[0]) < 1e-3);
                    int b = nodes.FindIndex(n => Distance(n, road[road.Length - 1]) < 1e-3);
                    Assert.True(a >= 0 && b >= 0 && a != b);

                    // Never two roads between the same two junctions.
                    Assert.True(pairs.Add(a < b ? (a, b) : (b, a)));

                    // No interior point is a junction, and none is far from the next.
                    for (int i = 1; i < road.Length; i++)
                        Assert.True(Distance(road[i - 1], road[i]) <= TaxiDefault.PointSpacing + 1e-3);
                    for (int i = 1; i < road.Length - 1; i++)
                        Assert.DoesNotContain(nodes, n => Distance(n, road[i]) < 1e-3);
                }

                // One piece: every node reached from the first.
                var reached = new HashSet<int> { 0 };
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    foreach ((float X, float Z)[] road in data.Roads)
                    {
                        int a = nodes.FindIndex(n => Distance(n, road[0]) < 1e-3);
                        int b = nodes.FindIndex(n => Distance(n, road[road.Length - 1]) < 1e-3);
                        if (reached.Contains(a) && reached.Add(b)) grew = true;
                        if (reached.Contains(b) && reached.Add(a)) grew = true;
                    }
                }

                Assert.Equal(nodes.Count, reached.Count);
            }
        }

        [Fact]
        public void EveryExitIsOnTheCentrelineFacingBothWays()
        {
            AirbasePlacement placement = Placement(3700f);
            TaxiNetworkData data = TaxiDefault.Build(placement, DonorServicePoint(placement));

            // Ends and four links between: round(3700 / 700) - 1 = 4.
            Assert.Equal(6, data.Exits.Count);
            foreach (TaxiExit exit in data.Exits)
            {
                Assert.Equal(0, exit.Runway);
                Assert.Equal(TaxiExit.Forward | TaxiExit.Reverse, exit.Directions);
                (double along, double across) = Frame(placement, (exit.X, exit.Z));
                Assert.InRange(across, -0.05, 0.05);
                Assert.InRange(along, -1850.05, 1850.05);
            }
        }

        [Fact]
        public void ItLiesOnTheServicePointsSide()
        {
            AirbasePlacement placement = Placement();
            (double along, double across) = (56.0, -320.0);
            double radians = placement.Heading * Math.PI / 180.0;
            double ux = Math.Sin(radians), uz = Math.Cos(radians);
            var left = ((float)(placement.X + ux * along + uz * across), (float)(placement.Z + uz * along - ux * across));

            TaxiNetworkData data = TaxiDefault.Build(placement, left);
            foreach ((float X, float Z)[] road in data.Roads)
                foreach ((float X, float Z) point in road)
                    Assert.True(Frame(placement, point).Across <= 0.05);

            // 56 m short of it.
            Assert.Contains(data.Roads, road => Math.Abs(Frame(placement, road[0]).Across + 264.0) < 0.05);
        }

        [Fact]
        public void ItStaysOnTheLevelledGround()
        {
            AirbasePlacement placement = Placement();
            placement.FlatHalfAcross = 120f;

            TaxiNetworkData narrow = TaxiDefault.Build(placement, DonorServicePoint(placement));
            double farthest = 0.0;
            foreach ((float X, float Z)[] road in narrow.Roads)
                foreach ((float X, float Z) point in road)
                    farthest = Math.Max(farthest, Frame(placement, point).Across);
            Assert.InRange(farthest, 99.9, 100.1);

            // A drawn outline 115 m either side of the axis: the lane is brought in until it lies inside.
            placement.FlatHalfAcross = 0f;
            double radians = placement.Heading * Math.PI / 180.0;
            double ux = Math.Sin(radians), uz = Math.Cos(radians);
            (float X, float Z) At(double along, double across)
                => ((float)(placement.X + ux * along + uz * across), (float)(placement.Z + uz * along - ux * across));
            placement.Outline = new[] { At(-1350, -115), At(1350, -115), At(1350, 115), At(-1350, 115) };

            TaxiNetworkData drawn = TaxiDefault.Build(placement, DonorServicePoint(placement));
            foreach ((float X, float Z)[] road in drawn.Roads)
                foreach ((float X, float Z) point in road)
                    Assert.True(Frame(placement, point).Across < 115.0);
        }

        [Fact]
        public void AStubOfARunwayHasOnlyItsEnds()
        {
            AirbasePlacement placement = Placement(30f);
            TaxiNetworkData data = TaxiDefault.Build(placement, DonorServicePoint(placement));
            Assert.False(data.HasNetwork);
            Assert.Equal(2, data.Exits.Count);

            Assert.Empty(TaxiDefault.Build("X", (0f, 0f), (0f, 0f), 45f, (0f, 200f)).Exits);
            Assert.Empty(TaxiDefault.Build("X", (float.NaN, 0f), (0f, 100f), 45f, (0f, 200f)).Roads);
        }

        [Fact]
        public void WhatItBuildsIsWrittenAndReadBack()
        {
            AirbasePlacement placement = Placement();
            TaxiNetworkData data = TaxiDefault.Build(placement, DonorServicePoint(placement));

            using (var stream = new System.IO.MemoryStream())
            {
                TaxiData.Write(stream, new[] { data });
                TaxiNetworkData read = TaxiData.Read(stream.ToArray())[0];
                Assert.Equal(TaxiSource.Default, read.Source);
                Assert.Equal(data.Roads.Count, read.Roads.Count);
                Assert.Equal(data.Exits.Count, read.Exits.Count);
            }
        }
    }
}
