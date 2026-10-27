using System.Collections.Generic;
using Xunit;

namespace CustomMaps.Tests
{
    /// <summary>
    /// The take-off-only runway the plugin builds for each take-off start (<see cref="TaxiPlans.Plan"/>),
    /// as the base game's <c>runway14LTakeoffShort</c> is built on Ignus: from the start, on the
    /// centreline, to the end of the runway it takes off towards. And how such a runway is told from the
    /// rest, and linked to the runway it lies on, from where it lies alone (<see cref="TaxiPlans.TakeoffParents"/>,
    /// <see cref="TaxiPlans.SharedStrips"/>).
    /// </summary>
    public class TakeoffPlanTests
    {
        /// <summary>A 2 km main runway running east from (-1000, 0) to (1000, 0), 45 m wide, and an extra
        /// one running north from (500, -800) to (500, 800), 30 m wide.</summary>
        static AirbasePlacement Placement() => new AirbasePlacement
        {
            UniqueName = "AB01",
            X = 0f, Y = 400f, Z = 0f,
            Heading = 90f,
            RunwayLength = 2000f,
            RunwayWidth = 45f,
            ExtraRunways = new[] { new AirbaseRunway { StartX = 500f, StartZ = -800f, EndX = 500f, EndZ = 800f, Width = 30f } },
        };

        static TaxiNetworkData Lanes(params TaxiTakeoff[] starts)
        {
            var data = new TaxiNetworkData { Airbase = "AB01" };
            data.Roads.Add(new[] { (-900f, 0f), (-900f, 200f) });
            data.Takeoffs.AddRange(starts);
            return data;
        }

        static TaxiTakeoff Start(int runway, float x, float z, bool towardsEnd)
            => new TaxiTakeoff { Runway = runway, X = x, Z = z, Direction = towardsEnd ? TaxiExit.Forward : TaxiExit.Reverse };

        [Fact]
        public void ATakeoffStartRunsFromWhereItIsToTheEndItTakesOffTowards()
        {
            TaxiPlan plan = TaxiPlans.Plan(Lanes(Start(0, -900f, 0f, towardsEnd: true), Start(1, 500f, 600f, towardsEnd: false)), Placement());
            Assert.Equal(2, plan.Takeoffs.Count);
            Assert.Empty(plan.DroppedTakeoffs);

            // 100 m inside the main runway's start, taking off east, to its end: 1,900 m ahead.
            PlannedTakeoff east = plan.Takeoffs[0];
            Assert.Equal(0, east.Runway);
            Assert.True(east.TowardsEnd);
            Assert.Equal(-900f, east.StartX, 2);
            Assert.Equal(0f, east.StartZ, 2);
            Assert.Equal(1000f, east.EndX, 2);
            Assert.Equal(0f, east.EndZ, 2);
            Assert.Equal(1900f, east.Run, 1);

            // 200 m inside the extra runway's end, taking off south, to its start: 1,400 m ahead.
            PlannedTakeoff south = plan.Takeoffs[1];
            Assert.Equal(1, south.Runway);
            Assert.False(south.TowardsEnd);
            Assert.Equal((500f, 600f), (south.StartX, south.StartZ));
            Assert.Equal((500f, -800f), (south.EndX, south.EndZ));
            Assert.Equal(1400f, south.Run, 1);
        }

        [Fact]
        public void ATakeoffStartBesideTheCentrelineIsLinedUpOnIt()
        {
            TaxiPlan plan = TaxiPlans.Plan(Lanes(Start(0, -400f, 12f, towardsEnd: false)), Placement());
            PlannedTakeoff west = Assert.Single(plan.Takeoffs);
            Assert.Equal(-400f, west.StartX, 2);
            Assert.Equal(0f, west.StartZ, 2);
            Assert.Equal(12f, west.Moved, 2);
            Assert.Equal(-1000f, west.EndX, 2);
            Assert.Equal(0f, west.EndZ, 2);
            Assert.Equal(600f, west.Run, 1);
        }

        [Fact]
        public void ATakeoffStartOffItsRunwayIsLeftOutAndSaidWhy()
        {
            TaxiPlan plan = TaxiPlans.Plan(Lanes(
                Start(0, 0f, 30f, towardsEnd: true),        // beside the 45 m runway, 30 m off its centreline
                Start(0, -1100f, 0f, towardsEnd: true),     // 100 m before its start
                Start(1, 500f, 900f, towardsEnd: false),    // 100 m past the extra runway's end
                Start(2, 0f, 0f, towardsEnd: true),         // a runway the airbase does not have
                Start(0, 1000f, 0f, towardsEnd: true),      // at the end it takes off towards
                Start(1, 510f, 0f, towardsEnd: true)),      // 10 m off the 30 m extra runway: on it
                Placement());

            PlannedTakeoff kept = Assert.Single(plan.Takeoffs);
            Assert.Equal(1, kept.Runway);
            Assert.Equal(5, plan.DroppedTakeoffs.Count);
            Assert.StartsWith("take-off start 1 is 30 m off runway 1's centreline", plan.DroppedTakeoffs[0]);
            Assert.StartsWith("take-off start 2 lies 100 m past runway 1's start", plan.DroppedTakeoffs[1]);
            Assert.StartsWith("take-off start 3 lies 100 m past runway 2's end", plan.DroppedTakeoffs[2]);
            Assert.StartsWith("take-off start 4 is on runway 3", plan.DroppedTakeoffs[3]);
            Assert.StartsWith("take-off start 5 is at runway 1's end", plan.DroppedTakeoffs[4]);
        }

        /// <summary>A map without take-off starts plans none, and its airbases are built as before.</summary>
        [Fact]
        public void NoTakeoffStartsIsNoTakeoffRunways()
        {
            TaxiPlan plan = TaxiPlans.Plan(Lanes(), Placement());
            Assert.Empty(plan.Takeoffs);
            Assert.Empty(plan.DroppedTakeoffs);

            Assert.Empty(TaxiPlans.Plan(new TaxiNetworkData { Airbase = "AB01", Takeoffs = null }, Placement()).Takeoffs);
            Assert.Empty(TaxiPlans.Plan(null, Placement()).Takeoffs);

            // The default network, which every field without a network in the map is given, has none.
            Assert.Empty(TaxiPlans.Plan(TaxiDefault.Build(Placement(), (56f, -206f)), Placement()).Takeoffs);
        }

        static RunwayLine Line(float sx, float sz, float ex, float ez, bool landing)
            => new RunwayLine { StartX = sx, StartZ = sz, EndX = ex, EndZ = ez, Landing = landing };

        /// <summary>
        /// The runways an airbase is built with, take-off starts last, as the plugin orders them: each start
        /// is told by where it lies, its parent the runway whose end it ends on, and every runway on one strip
        /// shares take-offs with the others there. The crossing runway, and runways no start lies on, share
        /// nothing new.
        /// </summary>
        [Fact]
        public void TakeoffRunwaysAreFoundOnTheirStripAndLinkedToIt()
        {
            var runways = new List<RunwayLine>
            {
                Line(-1000f, 0f, 1000f, 0f, landing: true),     // 0: main
                Line(500f, -800f, 500f, 800f, landing: true),   // 1: extra, crossing it
                Line(-900f, 0f, 1000f, 0f, landing: false),     // 2: take-off start on 0, east
                Line(800f, 0f, -1000f, 0f, landing: false),     // 3: take-off start on 0, west
                Line(500f, 600f, 500f, -800f, landing: false),  // 4: take-off start on 1, south
                Line(0f, 300f, 1000f, 0f, landing: false),      // 5: ends on 0's end but starts off it
            };

            Assert.Equal(new[] { -1, -1, 0, 0, 1, -1 }, TaxiPlans.TakeoffParents(runways));

            List<int>[] shared = TaxiPlans.SharedStrips(runways);
            foreach (List<int> list in shared) list.Sort();
            Assert.Equal(new[] { 2, 3 }, shared[0]);
            Assert.Equal(new[] { 4 }, shared[1]);
            Assert.Equal(new[] { 0, 3 }, shared[2]);
            Assert.Equal(new[] { 0, 2 }, shared[3]);
            Assert.Equal(new[] { 1 }, shared[4]);
            Assert.Empty(shared[5]);

            // An airbase without take-off starts shares nothing new.
            Assert.All(TaxiPlans.SharedStrips(runways.GetRange(0, 2)), s => Assert.Empty(s));
            Assert.Empty(TaxiPlans.SharedStrips(null));
        }

        /// <summary>The base game's own pair on Ignus' airbase_NW, as its data has them: runway 14L and
        /// "Runway14L Short", 198 m inside 14L's start, are found as one strip.</summary>
        [Fact]
        public void TheBaseGamesOwnPairIsOneStrip()
        {
            var runways = new List<RunwayLine>
            {
                Line(-77368.242f, 34220.402f, -75105.234f, 31957.590f, landing: true),  // 14R
                Line(-75473.438f, 33852.492f, -74180.531f, 32559.191f, landing: true),  // 14L
                Line(-75333.336f, 33712.391f, -74180.531f, 32559.191f, landing: false), // 14L Short
            };

            Assert.Equal(new[] { -1, -1, 1 }, TaxiPlans.TakeoffParents(runways));
        }

        /// <summary>
        /// A mission's own roads keep a take-off start the map placed only when they lead to it, within
        /// 30 m of a point or a straight of theirs; the map's own network keeps every one.
        /// </summary>
        [Fact]
        public void AMissionsRoadsKeepATakeoffStartOnlyWhenTheyLeadToIt()
        {
            var roads = new List<(float X, float Z)[]>
            {
                new[] { (-900f, 300f), (-900f, 0f) },          // onto the runway at the start
                new[] { (0f, 300f), (400f, 300f) },             // a parallel way, 300 m off
            };

            Assert.True(MissionTaxiRoads.Reaches(roads, (-900f, 0f)));
            Assert.True(MissionTaxiRoads.Reaches(roads, (-900f, -25f)));
            Assert.True(MissionTaxiRoads.Reaches(roads, (200f, 280f)));    // beside the straight, between its points
            Assert.False(MissionTaxiRoads.Reaches(roads, (-500f, 0f)));
            Assert.False(MissionTaxiRoads.Reaches(roads, (200f, 0f)));
            Assert.False(MissionTaxiRoads.Reaches(new List<(float X, float Z)[]>(), (0f, 0f)));
            Assert.False(MissionTaxiRoads.Reaches(null, (0f, 0f)));
        }

        /// <summary>The runways the plugin builds from a plan for <see cref="Placement"/>: the main one,
        /// the extra one, then a take-off-only runway for each take-off start it planned.</summary>
        static List<RunwayLine> Built(TaxiPlan plan)
        {
            var runways = new List<RunwayLine>
            {
                Line(-1000f, 0f, 1000f, 0f, landing: true),
                Line(500f, -800f, 500f, 800f, landing: true),
            };
            foreach (PlannedTakeoff takeoff in plan.Takeoffs)
                runways.Add(Line(takeoff.StartX, takeoff.StartZ, takeoff.EndX, takeoff.EndZ, landing: false));
            return runways;
        }

        static bool?[] Flags(TaxiPlan plan, IReadOnlyList<bool> inUse = null)
            => TaxiPlans.TakeoffFlags(TaxiPlans.TakeoffParents(Built(plan)), inUse);

        /// <summary>
        /// A runway with a take-off start takes off from its starts only: its own ends are no take-off
        /// points. The other runway, without starts, is left as it is, ends and all.
        /// </summary>
        [Fact]
        public void ARunwayWithStartsTakesOffFromThemOnly()
        {
            TaxiPlan plan = TaxiPlans.Plan(Lanes(Start(0, -900f, 0f, towardsEnd: true), Start(0, 800f, 0f, towardsEnd: false)), Placement());
            Assert.Equal(new bool?[] { false, null, true, true }, Flags(plan));

            // A start on the extra runway alone takes its ends away, and leaves the main runway's.
            plan = TaxiPlans.Plan(Lanes(Start(1, 500f, 600f, towardsEnd: false)), Placement());
            Assert.Equal(new bool?[] { null, false, true }, Flags(plan));
        }

        /// <summary>Starts that all face one way are all the runway has: none takes off the other way,
        /// and its ends stay off. A start at the far end gives the full length back, one way.</summary>
        [Fact]
        public void StartsFacingOneWayLeaveNoTakeoffTheOtherWay()
        {
            TaxiPlan plan = TaxiPlans.Plan(Lanes(Start(0, -900f, 0f, towardsEnd: true), Start(0, -200f, 0f, towardsEnd: true)), Placement());
            List<RunwayLine> runways = Built(plan);
            bool?[] flags = TaxiPlans.TakeoffFlags(TaxiPlans.TakeoffParents(runways));
            Assert.Equal(new bool?[] { false, null, true, true }, flags);

            // Every runway that still takes take-offs on the main runway's strip takes off east.
            for (int i = 2; i < runways.Count; i++) Assert.True(runways[i].EndX > runways[i].StartX);

            // At the runway's start, taking off east: the whole length.
            plan = TaxiPlans.Plan(Lanes(Start(0, -1000f, 0f, towardsEnd: true)), Placement());
            PlannedTakeoff full = Assert.Single(plan.Takeoffs);
            Assert.Equal(2000f, full.Run, 1);
            Assert.Equal(new bool?[] { false, null, true }, Flags(plan));
        }

        /// <summary>Starts the plugin leaves out do not count: a runway whose starts were all left out
        /// keeps its ends, as one with none.</summary>
        [Fact]
        public void ARunwayWhoseStartsWereAllLeftOutKeepsItsEnds()
        {
            TaxiPlan plan = TaxiPlans.Plan(Lanes(Start(0, 0f, 30f, towardsEnd: true), Start(0, 1000f, 0f, towardsEnd: true)), Placement());
            Assert.Empty(plan.Takeoffs);
            Assert.Equal(2, plan.DroppedTakeoffs.Count);
            Assert.Equal(new bool?[] { null, null }, Flags(plan));

            // One left out beside one kept: the kept one is all the runway takes off from.
            plan = TaxiPlans.Plan(Lanes(Start(0, 0f, 30f, towardsEnd: true), Start(0, -900f, 0f, towardsEnd: true)), Placement());
            Assert.Equal(new bool?[] { false, null, true }, Flags(plan));
        }

        /// <summary>
        /// A mission's roads that lead to none of a runway's starts switch them all off, and the runway
        /// takes off from its ends again, so it never loses all its take-offs; leading to one of them, the
        /// runway still takes off from that one only. The map's roads back, it is starts only again.
        /// </summary>
        [Fact]
        public void AMissionsRoadsMissingEveryStartGiveTheRunwayItsEndsBack()
        {
            TaxiPlan plan = TaxiPlans.Plan(Lanes(
                Start(0, -900f, 0f, towardsEnd: true),          // 2
                Start(0, 800f, 0f, towardsEnd: false),          // 3
                Start(1, 500f, 600f, towardsEnd: false)),       // 4
                Placement());

            // None reached: both runways take off from their ends.
            Assert.Equal(new bool?[] { true, true, false, false, false }, Flags(plan, new[] { false, false, false, false, false }));

            // One of the main runway's starts reached: the main runway from it only, the extra from its ends.
            Assert.Equal(new bool?[] { false, true, false, true, false }, Flags(plan, new[] { false, false, false, true, false }));

            // The map's roads again: every start in use, no runway's ends.
            Assert.Equal(new bool?[] { false, false, true, true, true }, Flags(plan));
            Assert.Equal(Flags(plan), Flags(plan, new[] { true, true, true, true, true }));

            // What is read for a runway that is no start does not matter.
            Assert.Equal(new bool?[] { true, true, false, false, false }, Flags(plan, new[] { true, true, false, false, false }));
        }

        /// <summary>A map without take-off starts leaves every runway as the game has it.</summary>
        [Fact]
        public void NoTakeoffStartsLeavesEveryRunwayAsItIs()
        {
            Assert.Equal(new bool?[] { null, null }, Flags(TaxiPlans.Plan(Lanes(), Placement())));
            Assert.Equal(new bool?[] { null, null }, TaxiPlans.TakeoffFlags(new[] { -1, -1 }, new[] { false, false }));
            Assert.Empty(TaxiPlans.TakeoffFlags(null));

            // The base game's own pair on Ignus, were it ours, would take 14L's ends away too.
            Assert.Equal(new bool?[] { null, false, true }, TaxiPlans.TakeoffFlags(new[] { -1, -1, 1 }));
        }
    }
}
