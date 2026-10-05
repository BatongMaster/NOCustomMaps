using System;
using System.Collections.Generic;
using Xunit;

namespace CustomMaps.Tests
{
    /// <summary>
    /// A mission's own roads for a custom map's airbase (<see cref="MissionTaxiRoads"/>): a copy of the
    /// map's that nothing was done to is told from one the mission edited, so it is saved as none and the
    /// mission goes on following the map; and the runway exits made for the mission's roads lie where
    /// those roads meet the runway, on its centreline, facing the rolls that can turn into them.
    /// </summary>
    public class MissionTaxiRoadsTests
    {
        static List<RoadShape> Network()
        {
            return new List<RoadShape>
            {
                new RoadShape { Points = new[] { (0f, 400.06f, 0f), (0f, 400.06f, 30f), (0f, 400.06f, 150f) } },
                new RoadShape { Points = new[] { (0f, 400.06f, 150f), (500f, 400.06f, 150f) }, Bridge = false },
            };
        }

        static List<RoadShape> Copy(List<RoadShape> roads)
        {
            var copy = new List<RoadShape>();
            foreach (RoadShape road in roads)
                copy.Add(new RoadShape { Points = ((float X, float Y, float Z)[])road.Points.Clone(), Bridge = road.Bridge });
            return copy;
        }

        [Fact]
        public void AnUntouchedCopyIsTheSame()
        {
            Assert.True(MissionTaxiRoads.Same(Network(), Copy(Network())));

            // Through JSON and the network a float can come back a hair off; that is still the same.
            List<RoadShape> rounded = Copy(Network());
            rounded[0].Points[1] = (0.004f, 400.06f, 30.003f);
            Assert.True(MissionTaxiRoads.Same(Network(), rounded));

            Assert.True(MissionTaxiRoads.Same(new List<RoadShape>(), null));
        }

        [Fact]
        public void AnyEditIsADifference()
        {
            List<RoadShape> moved = Copy(Network());
            moved[1].Points[1] = (500f, 400.06f, 160f);
            Assert.False(MissionTaxiRoads.Same(Network(), moved));

            List<RoadShape> added = Copy(Network());
            added.Add(new RoadShape { Points = new[] { (500f, 400.06f, 150f), (500f, 400.06f, 300f) } });
            Assert.False(MissionTaxiRoads.Same(Network(), added));

            List<RoadShape> deleted = Copy(Network());
            deleted.RemoveAt(0);
            Assert.False(MissionTaxiRoads.Same(Network(), deleted));

            List<RoadShape> inserted = Copy(Network());
            inserted[0] = new RoadShape { Points = new[] { (0f, 400.06f, 0f), (0f, 400.06f, 30f), (0f, 400.06f, 90f), (0f, 400.06f, 150f) } };
            Assert.False(MissionTaxiRoads.Same(Network(), inserted));

            List<RoadShape> bridged = Copy(Network());
            bridged[1] = new RoadShape { Points = bridged[1].Points, Bridge = true };
            Assert.False(MissionTaxiRoads.Same(Network(), bridged));

            List<RoadShape> emptied = new List<RoadShape>();
            Assert.False(MissionTaxiRoads.Same(Network(), emptied));
        }

        /// <summary>A 2 km runway running east from (-1000, 0) to (1000, 0), 45 m wide.</summary>
        static readonly (float X, float Z) Start = (-1000f, 0f), End = (1000f, 0f);

        [Fact]
        public void ARoadSquareOffTheRunwayIsAnExitBothWays()
        {
            var roads = new List<(float X, float Z)[]> { new[] { (200f, 2f), (200f, 60f), (200f, 170f) } };
            List<PlannedExit> exits = MissionTaxiRoads.Exits(roads, Start, End, 45f);

            // On the centreline, both ways, and the two ends facing out.
            Assert.Contains(exits, e => e.X == 200f && e.Z == 0f && e.Forward && e.FacingX == 1f);
            Assert.Contains(exits, e => e.X == 200f && e.Z == 0f && !e.Forward && e.FacingX == -1f);
            Assert.Contains(exits, e => e.X == 1000f && e.Forward);
            Assert.Contains(exits, e => e.X == -1000f && !e.Forward);
            Assert.Equal(4, exits.Count);
        }

        [Fact]
        public void ARoadLeavingBackwardsServesOnlyTheRollThatCanTurnIntoIt()
        {
            // Leaves the centreline heading back west at 30° from the runway: a roll east cannot turn
            // into it, a roll west can.
            float dx = -(float)Math.Cos(Math.PI / 6.0) * 100f, dz = (float)Math.Sin(Math.PI / 6.0) * 100f;
            var roads = new List<(float X, float Z)[]> { new[] { (300f, 0f), (300f + dx, dz) } };
            List<PlannedExit> exits = MissionTaxiRoads.Exits(roads, Start, End, 45f);

            Assert.Contains(exits, e => e.X == 300f && !e.Forward);
            Assert.DoesNotContain(exits, e => e.X == 300f && e.Forward);
        }

        [Fact]
        public void ARoadAcrossTheRunwayIsAnExitBothWaysWhereItCrosses()
        {
            var roads = new List<(float X, float Z)[]> { new[] { (-400f, -300f), (-400f, 300f) } };
            List<PlannedExit> exits = MissionTaxiRoads.Exits(roads, Start, End, 45f);

            Assert.Contains(exits, e => Math.Abs(e.X + 400f) < 0.01f && e.Forward);
            Assert.Contains(exits, e => Math.Abs(e.X + 400f) < 0.01f && !e.Forward);
        }

        [Fact]
        public void RoadsOffTheRunwayLeaveOnlyItsEnds()
        {
            var roads = new List<(float X, float Z)[]>
            {
                new[] { (0f, 170f), (500f, 170f) },
                new[] { (1100f, 0f), (1200f, 0f) },
                new[] { (0f, 30f), (0f, 60f) },
            };

            List<PlannedExit> exits = MissionTaxiRoads.Exits(roads, Start, End, 45f);
            Assert.Equal(2, exits.Count);
            Assert.Empty(MissionTaxiRoads.Exits(roads, (0f, 0f), (0f, 0f), 45f));
        }

        [Fact]
        public void AnEndJustOffTheRunwaysSideOrEndIsCarriedOntoIt()
        {
            var roads = new List<(float X, float Z)[]>
            {
                new[] { (0f, 24.5f), (0f, 120f) },
                new[] { (-1002f, -5f), (-1002f, -150f) },
            };

            List<PlannedExit> exits = MissionTaxiRoads.Exits(roads, Start, End, 45f);
            Assert.Contains(exits, e => e.X == 0f && e.Z == 0f && e.Forward);

            // Beyond the end: on the threshold, which already has its exit facing out.
            Assert.Contains(exits, e => e.X == -1000f && e.Z == 0f && e.Forward);
            Assert.Contains(exits, e => e.X == -1000f && e.Z == 0f && !e.Forward);
        }

        /// <summary>
        /// A mission's roads counted in pieces by the nodes the game made of them: the Roads tool joins a
        /// road only at a node marker or within 10 m of the new one's ends, so two drawn roads that cross
        /// are two pieces, and the plugin warns of them.
        /// </summary>
        [Fact]
        public void PiecesAreCountedByTheGamesNodes()
        {
            // A T and a road off on its own: nodes 0 to 3 joined, 4 and 5 apart.
            var roads = new List<(int From, int To)> { (0, 1), (1, 2), (1, 3), (4, 5) };
            Assert.Equal(2, MissionTaxiRoads.Pieces(6, roads));

            // Joined at node 3: one.
            roads.Add((3, 4));
            Assert.Equal(1, MissionTaxiRoads.Pieces(6, roads));

            // A node no road uses is no piece; a road to a node that does not exist is passed over.
            Assert.Equal(1, MissionTaxiRoads.Pieces(9, new[] { (0, 1), (1, 2), (2, 12) }));
            Assert.Equal(0, MissionTaxiRoads.Pieces(0, roads));
            Assert.Equal(0, MissionTaxiRoads.Pieces(4, null));
        }
    }
}
