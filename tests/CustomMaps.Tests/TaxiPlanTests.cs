using System;
using System.Collections.Generic;
using Xunit;

namespace CustomMaps.Tests
{
    /// <summary>
    /// What the plugin builds into an airbase from its taxi lanes (<see cref="TaxiPlans"/>): the roads as
    /// shipped, an exit transform for each way of rolling each exit serves, facing that way along its own
    /// runway, exits on a runway the airbase does not have left out, and the runways as the plugin lays
    /// them out, numbered as the editor painted them.
    /// </summary>
    public class TaxiPlanTests
    {
        /// <summary>A field with its 2 km main runway running east (heading 90) from (-1000, 0) to (1000, 0)
        /// and one extra runway running north.</summary>
        static AirbasePlacement Placement() => new AirbasePlacement
        {
            UniqueName = "AB01",
            X = 0f, Y = 400f, Z = 0f,
            Heading = 90f,
            RunwayLength = 2000f,
            RunwayWidth = 45f,
            ExtraRunways = new[] { new AirbaseRunway { StartX = 500f, StartZ = -800f, EndX = 500f, EndZ = 800f, Width = 45f } },
        };

        static TaxiNetworkData Lanes()
        {
            var data = new TaxiNetworkData { Airbase = "AB01", ServicePoint = (30f, 280f), LandingPad = (90f, 300f) };
            data.Roads.Add(new[] { (0f, 0f), (0f, 30f), (0f, 60f) });
            data.Roads.Add(new[] { (0f, 60f), (100f, 60f) });
            data.Exits.Add(new TaxiExit { Runway = 0, X = 0f, Z = 0f, Directions = TaxiExit.Forward | TaxiExit.Reverse });
            data.Exits.Add(new TaxiExit { Runway = 0, X = 1000f, Z = 0f, Directions = TaxiExit.Forward });
            data.Exits.Add(new TaxiExit { Runway = 1, X = 500f, Z = -800f, Directions = TaxiExit.Reverse });
            data.Exits.Add(new TaxiExit { Runway = 5, X = 0f, Z = 0f, Directions = TaxiExit.Forward });
            return data;
        }

        [Fact]
        public void TheRunwaysAreLaidOutAsThePluginLaysThemOut()
        {
            List<(float StartX, float StartZ, float EndX, float EndZ)> runways = TaxiPlans.Runways(Placement());
            Assert.Equal(2, runways.Count);
            Assert.Equal(-1000f, runways[0].StartX, 2);
            Assert.Equal(0f, runways[0].StartZ, 2);
            Assert.Equal(1000f, runways[0].EndX, 2);
            Assert.Equal(0f, runways[0].EndZ, 2);
            Assert.Equal((500f, -800f, 500f, 800f), runways[1]);

            Assert.Equal("09/27, 36/18", TaxiPlans.Painted(Placement()));
        }

        [Fact]
        public void EachExitIsATransformForEachWayOfRollingFacingIt()
        {
            TaxiPlan plan = TaxiPlans.Plan(Lanes(), Placement());

            Assert.True(plan.HasNetwork);
            Assert.Equal(2, plan.Roads.Count);
            Assert.Equal(2, plan.Exits.Length);

            // The main runway: both ways at its middle, forward at its eastern end.
            Assert.Equal(3, plan.Exits[0].Count);
            PlannedExit forward = plan.Exits[0].Find(e => e.X == 0f && e.Forward);
            PlannedExit back = plan.Exits[0].Find(e => e.X == 0f && !e.Forward);
            Assert.Equal(1f, forward.FacingX, 4);
            Assert.Equal(0f, forward.FacingZ, 4);
            Assert.Equal(-1f, back.FacingX, 4);
            Assert.Contains(plan.Exits[0], e => e.X == 1000f && e.Forward && e.FacingX > 0.99f);

            // The extra runway runs north, so rolling back is facing south.
            PlannedExit extra = Assert.Single(plan.Exits[1]);
            Assert.False(extra.Forward);
            Assert.Equal(0f, extra.FacingX, 4);
            Assert.Equal(-1f, extra.FacingZ, 4);

            // Runway 5 is not there.
            Assert.Equal(1, plan.DroppedExits);
            Assert.Equal(4, plan.ExitCount);

            Assert.Equal((30f, 280f), plan.ServicePoint);
            Assert.Equal((90f, 300f), plan.LandingPad);
        }

        [Fact]
        public void AFieldWithoutLanesHasNoNetworkButKeepsItsPointsPutByHand()
        {
            var data = new TaxiNetworkData { Airbase = "AB01", ServicePoint = (1f, 2f) };
            TaxiPlan plan = TaxiPlans.Plan(data, Placement());
            Assert.False(plan.HasNetwork);
            Assert.Equal(0, plan.ExitCount);
            Assert.Equal((1f, 2f), plan.ServicePoint);
            Assert.Null(plan.LandingPad);

            TaxiPlan none = TaxiPlans.Plan(null, Placement());
            Assert.False(none.HasNetwork);
            Assert.Equal(2, none.Exits.Length);
        }

        [Fact]
        public void LanesAreFoundByTheAirbasesNameTheFirstOfTwoKept()
        {
            var first = new TaxiNetworkData { Airbase = "AB01" };
            var second = new TaxiNetworkData { Airbase = "AB01" };
            var other = new TaxiNetworkData { Airbase = "LSZH" };
            var notes = new List<string>();

            Dictionary<string, TaxiNetworkData> found = TaxiPlans.ByAirbase(new[] { first, second, other, null }, notes);
            Assert.Equal(2, found.Count);
            Assert.Same(first, found["AB01"]);
            Assert.Same(other, found["LSZH"]);
            Assert.Single(notes);

            Assert.Empty(TaxiPlans.ByAirbase(null));
        }

        /// <summary>The shipped file read and planned end to end, as the plugin does at load.</summary>
        [Fact]
        public void AFileAsShippedPlansAsWritten()
        {
            byte[] bytes;
            using (var stream = new System.IO.MemoryStream())
            {
                TaxiData.Write(stream, new[] { Lanes() });
                bytes = stream.ToArray();
            }

            Dictionary<string, TaxiNetworkData> lanes = TaxiPlans.ByAirbase(TaxiData.Read(bytes));
            TaxiPlan plan = TaxiPlans.Plan(lanes["AB01"], Placement());
            Assert.Equal(new[] { (0f, 0f), (0f, 30f), (0f, 60f) }, plan.Roads[0]);
            Assert.Equal(4, plan.ExitCount);
        }

        /// <summary>
        /// The default network a field the map carries none for is given at load, from the donor's
        /// service point 56 m along and 206 m right of the runway's middle (south, for a runway running
        /// east): a lane 150 m south, links onto both thresholds and two between, an exit both ways at
        /// each link's foot on the main runway, and nothing on the extra runway, which keeps the exits
        /// it is built with.
        /// </summary>
        [Fact]
        public void TheDefaultNetworkPlansOntoTheMainRunway()
        {
            TaxiNetworkData data = TaxiDefault.Build(Placement(), (56f, -206f));
            TaxiPlan plan = TaxiPlans.Plan(data, Placement());

            Assert.True(plan.HasNetwork);
            Assert.Equal(7, plan.Roads.Count);
            Assert.Equal(8, plan.Exits[0].Count);
            Assert.Empty(plan.Exits[1]);

            foreach (PlannedExit exit in plan.Exits[0])
            {
                Assert.Equal(0f, exit.Z, 3);
                Assert.Equal(exit.Forward ? 1f : -1f, exit.FacingX, 4);
            }

            // The thresholds as the plugin lays them out are ends of roads, for the AI to taxi to.
            List<(float StartX, float StartZ, float EndX, float EndZ)> runways = TaxiPlans.Runways(Placement());
            var ends = new List<(float X, float Z)>();
            foreach ((float X, float Z)[] road in plan.Roads)
            {
                ends.Add(road[0]);
                ends.Add(road[road.Length - 1]);
            }

            Assert.Contains((runways[0].StartX, runways[0].StartZ), ends);
            Assert.Contains((runways[0].EndX, runways[0].EndZ), ends);

            foreach ((float X, float Z)[] road in plan.Roads)
                foreach ((float x, float z) in road)
                    Assert.InRange(z, -150.01f, 0.01f);
        }

        /// <summary>
        /// A network whose exits lie on the field's runways is the field's; one made before the field
        /// was moved, a taxiways.bin from an earlier export beside a later airbases.bin, is not, and the
        /// field is given the default instead. An exit on a runway the field does not have says
        /// nothing either way (it is dropped).
        /// </summary>
        [Fact]
        public void ANetworkMadeForWhereTheFieldWasIsKnown()
        {
            Assert.True(TaxiPlans.Fits(Lanes(), Placement(), out string misfit));
            Assert.Null(misfit);

            AirbasePlacement moved = Placement();
            moved.Z = 300f;
            Assert.False(TaxiPlans.Fits(Lanes(), moved, out misfit));
            Assert.Contains("300 m off runway 1", misfit);

            // Turned a little: the exit at the runway's end is off its centreline by more than rounding.
            AirbasePlacement turned = Placement();
            turned.Heading = 91f;
            Assert.False(TaxiPlans.Fits(Lanes(), turned, out _));

            // The default built from the placement itself fits, as every network Map Forge writes does.
            Assert.True(TaxiPlans.Fits(TaxiDefault.Build(Placement(), (0f, 250f)), Placement(), out _));
            Assert.True(TaxiPlans.Fits(new TaxiNetworkData(), Placement(), out _));
            Assert.True(TaxiPlans.Fits(null, Placement(), out _));
        }
    }
}
