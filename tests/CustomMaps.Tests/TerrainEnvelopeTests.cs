using System;
using Xunit;

namespace CustomMaps.Tests
{
    public class TerrainEnvelopeTests
    {
        static (float[] Distance, float[] Ground) Profile(float horizon, Func<float, float> ground)
        {
            int n = TerrainEnvelope.Samples;
            var d = new float[n];
            var g = new float[n];
            for (int i = 0; i < n; i++)
            {
                d[i] = TerrainEnvelope.SampleDistance(i + 1, n, horizon);
                g[i] = ground(d[i]);
            }
            return (d, g);
        }

        static float Deg(float radians) => radians * 180f / (float)Math.PI;

        // Twice the game's look-ahead, sampled evenly, puts a sample on the game's own point.
        [Fact]
        public void TheGamesLookAheadPointIsOneOfTheSamples()
        {
            float horizon = TerrainEnvelope.Horizon(1500f);
            Assert.Equal(3000f, horizon);
            Assert.Equal(1500f, TerrainEnvelope.SampleDistance(TerrainEnvelope.Samples / 2, TerrainEnvelope.Samples, horizon), 3);
        }

        // Over open ground below, the envelope lets the aircraft descend: its angle is the
        // shallowest descent that keeps the clearance at the farthest sample.
        [Fact]
        public void OpenGroundBelowLetsTheAircraftDescend()
        {
            var (d, g) = Profile(3000f, _ => 0f);
            float angle = TerrainEnvelope.ClimbAngle(200f, d, g, d.Length, 30f, 0f);
            Assert.Equal(Math.Atan2(30f - 200f, 3000f), angle, 4);
        }

        // A ridge between the aircraft and the game's single point: the game aims down at the
        // valley beyond, through the ridge; the envelope climbs over it with the clearance asked.
        [Fact]
        public void ARidgeBeforeTheLookAheadPointIsClimbedOver()
        {
            const float altitude = 500f, lookAhead = 1500f, hold = 50f;
            var (d, g) = Profile(TerrainEnvelope.Horizon(lookAhead), x => x >= 900f && x <= 1100f ? 900f : 0f);

            float follow = TerrainEnvelope.FollowAngle(altitude, lookAhead, 0f, hold);
            Assert.True(follow < 0f, "the game's point is in the valley beyond, below the aircraft");

            float clearance = TerrainEnvelope.WaypointClearance(hold, TerrainEnvelope.PlaneClearance);
            float envelope = TerrainEnvelope.ClimbAngle(altitude, d, g, d.Length, clearance, TerrainEnvelope.Cone);
            float aim = TerrainEnvelope.WaypointAngle(follow, envelope);

            Assert.Equal(envelope, aim);
            foreach (float x in new[] { 1000f, 1125f })
            {
                float ground = x >= 900f && x <= 1100f ? 900f : 0f;
                Assert.True(altitude + x * Math.Tan(aim) >= ground + hold - 0.5f, $"clears the ridge at {x} m");
            }
        }

        // Over gently falling ground the aim stays within a degree or so of the game's own: the
        // envelope only ever lifts it, here by the cone alone.
        [Fact]
        public void OverOpenGroundTheAimStaysCloseToTheGames()
        {
            const float altitude = 300f, lookAhead = 1500f, hold = 250f;
            var (d, g) = Profile(TerrainEnvelope.Horizon(lookAhead), x => 50f - 0.01f * x);
            float follow = TerrainEnvelope.FollowAngle(altitude, lookAhead, 50f - 0.01f * lookAhead, hold);
            float envelope = TerrainEnvelope.ClimbAngle(altitude, d, g, d.Length, hold, TerrainEnvelope.Cone);
            Assert.True(envelope <= follow + 0.02f);
            Assert.True(TerrainEnvelope.WaypointAngle(follow, envelope) >= follow);
        }

        // The cone: the same ground asks for more clearance the farther away it is.
        [Fact]
        public void TheConeAsksMoreClearanceFartherOut()
        {
            float near = TerrainEnvelope.ClimbAngle(0f, new[] { 1000f }, new[] { 0f }, 1, 30f, TerrainEnvelope.Cone);
            Assert.Equal(Math.Atan2(30f + 20f, 1000f), near, 5);

            float far = TerrainEnvelope.ClimbAngle(0f, new[] { 3000f }, new[] { 0f }, 1, 30f, TerrainEnvelope.Cone);
            Assert.Equal(Math.Atan2(30f + 60f, 3000f), far, 5);
        }

        [Fact]
        public void SamplesThatCannotBeUsedAreSkipped()
        {
            float angle = TerrainEnvelope.ClimbAngle(0f, new[] { 0f, 500f, 1000f }, new[] { 5000f, float.NaN, 100f }, 3, 0f, 0f);
            Assert.Equal(Math.Atan2(100f, 1000f), angle, 5);
            Assert.Equal(-TerrainEnvelope.MaxAngle, TerrainEnvelope.ClimbAngle(0f, new float[0], new float[0], 0, 0f, 0f));
        }

        [Fact]
        public void TheAimIsHeldWithinSixtyDegrees()
        {
            Assert.Equal(TerrainEnvelope.MaxAngle, TerrainEnvelope.WaypointAngle(2f, 0f));
            Assert.Equal(-TerrainEnvelope.MaxAngle, TerrainEnvelope.WaypointAngle(-2f, -2f));
            Assert.Equal(60f, Deg(TerrainEnvelope.MaxAngle), 3);
            Assert.Equal(0f, TerrainEnvelope.WaypointAngle(float.NaN, float.NaN));
        }

        // An aircraft below the height it holds is asked to make it up over the look-ahead, as the
        // game's own aim does, not at the first sample: over flat ground its aim is the game's,
        // plus the cone.
        [Fact]
        public void BelowItsHoldTheClimbIsSpreadOverTheLookAhead()
        {
            const float agl = 60f, hold = 182f, lookAhead = 1080f;
            var (d, g) = Profile(TerrainEnvelope.Horizon(lookAhead), _ => 0f);

            float follow = TerrainEnvelope.FollowAngle(agl, lookAhead, 0f, hold);
            float ramped = TerrainEnvelope.ClimbAngle(agl, d, g, d.Length, hold, TerrainEnvelope.PlaneClearance, agl, lookAhead, TerrainEnvelope.Cone);
            float whole = TerrainEnvelope.ClimbAngle(agl, d, g, d.Length, hold, TerrainEnvelope.Cone);

            Assert.InRange(Deg(ramped), Deg(follow), Deg(follow) + 1.5f);
            Assert.True(Deg(whole) > 45f, "asked for at the first sample, the same hold is a climb of over 45 degrees");
        }

        // The ramp starts at the aircraft's own height and reaches the hold at the look-ahead; an
        // aircraft at or over its hold, or with no height known, is asked for the hold everywhere.
        [Theory]
        [InlineData(200f, 30f, 50f, 0f, 1000f, 50f)]
        [InlineData(200f, 30f, 50f, 500f, 1000f, 125f)]
        [InlineData(200f, 30f, 50f, 1000f, 1000f, 200f)]
        [InlineData(200f, 30f, 50f, 2000f, 1000f, 200f)]
        [InlineData(200f, 30f, 10f, 0f, 1000f, 30f)]
        [InlineData(200f, 30f, 300f, 100f, 1000f, 200f)]
        [InlineData(200f, 30f, float.PositiveInfinity, 100f, 1000f, 200f)]
        [InlineData(200f, 30f, float.NaN, 100f, 1000f, 200f)]
        [InlineData(10f, 30f, 5f, 100f, 1000f, 30f)]
        [InlineData(200f, 30f, 50f, 500f, 0f, 200f)]
        public void TheClearanceRampsFromTheAircraftsHeightToItsHold(float hold, float least, float aboveGround,
                                                                     float distance, float lookAhead, float expected)
        {
            Assert.Equal(expected, TerrainEnvelope.RampedClearance(hold, least, aboveGround, distance, lookAhead), 3);
        }

        // The ramp never lowers what the ground ahead asks for below the least: a ridge close in is
        // still cleared by 30 m, however low the aircraft.
        [Fact]
        public void ARidgeCloseInIsStillClearedByTheLeast()
        {
            float angle = TerrainEnvelope.ClimbAngle(0f, new[] { 200f }, new[] { 100f }, 1, 300f, TerrainEnvelope.PlaneClearance, 0f, 2000f, 0f);
            Assert.Equal(Math.Atan2(130f, 200f), angle, 4);
        }

        // Radar evasion asks for 10 m; an aeroplane is held to 30 m. A helicopter coming in to
        // land is let down to what it asked for.
        [Theory]
        [InlineData(10f, 30f, 30f)]
        [InlineData(150f, 30f, 150f)]
        [InlineData(5f, 0f, 5f)]
        [InlineData(float.NaN, 30f, 30f)]
        public void TheClearanceIsWhatWasAskedForWithAFloor(float asked, float least, float expected)
        {
            Assert.Equal(expected, TerrainEnvelope.WaypointClearance(asked, least));
        }

        // Straight flight at a destination gets a floor only far from it: close in it is an attack
        // run at something the aircraft can see, and is left alone.
        [Theory]
        [InlineData(1400f, 200f, false)]
        [InlineData(1600f, 200f, true)]
        [InlineData(1600f, 300f, false)]
        [InlineData(2000f, 300f, true)]
        [InlineData(float.NaN, 200f, false)]
        public void AFloorAppliesOnlyFarFromTheDestination(float distance, float speed, bool expected)
        {
            Assert.Equal(expected, TerrainEnvelope.FloorApplies(distance, speed));
        }

        // The floor samples the envelope's horizon, short of the stretch before the destination,
        // so a target on a hilltop is not a ridge to climb over.
        [Theory]
        [InlineData(10000f, 250f, 3000f, 3000f)]
        [InlineData(2500f, 250f, 3000f, 1500f)]
        [InlineData(3000f, 500f, 3000f, 1500f)]
        [InlineData(1200f, 500f, 3000f, 0f)]
        public void TheFloorStopsShortOfTheDestination(float distance, float speed, float horizon, float expected)
        {
            Assert.Equal(expected, TerrainEnvelope.FloorReach(distance, speed, horizon));
        }

        // Turning in to land, the destination is the pattern's aim point, in the air on the
        // glideslope: the floor samples all the way to it, within the horizon.
        [Theory]
        [InlineData(6000f, 3000f, 3000f)]
        [InlineData(2000f, 3000f, 2000f)]
        [InlineData(-5f, 3000f, 0f)]
        public void TurningInTheFloorReachesTheAimPoint(float distance, float horizon, float expected)
        {
            Assert.Equal(expected, TerrainEnvelope.FloorReachInAir(distance, horizon));
        }

        [Fact]
        public void AFloorLiftsOnlyAnAimBelowIt()
        {
            Assert.True(TerrainEnvelope.Lifts(0.1f, 0.05f));
            Assert.False(TerrainEnvelope.Lifts(-0.1f, 0f));
            Assert.False(TerrainEnvelope.Lifts(float.NegativeInfinity, -0.5f));
            Assert.False(TerrainEnvelope.Lifts(float.NaN, 0f));
        }

        // ---------------------------------------------------------------------------------------
        // Landing approaches

        static (float[] Distance, float[] Ground) Approach(Func<float, float> ground)
        {
            const int n = 60;
            var d = new float[n];
            var g = new float[n];
            for (int i = 0; i < n; i++)
            {
                d[i] = 250f * (i + 1);
                g[i] = ground(d[i]);
            }
            return (d, g);
        }

        // Meiringen landing west: the valley floor is level for a few kilometres and then the
        // mountain stands across the approach. The 3.4-degree glideslope meets it about 5 km out.
        [Fact]
        public void AMountainAcrossTheApproachObstructsItWhereTheGlideslopeMeetsIt()
        {
            var (d, g) = Approach(x => x < 3000f ? 0f : 0.15f * (x - 3000f));
            float obstruction = TerrainEnvelope.FirstObstruction(d, g, d.Length, 0f,
                TerrainEnvelope.GlideslopeGradient, TerrainEnvelope.GlideslopeClearance, TerrainEnvelope.GlideslopeFrom);
            Assert.Equal(5000f, obstruction);
            Assert.True(obstruction < TerrainEnvelope.ApproachLength(1500f));
        }

        // Geneva: flat out over the lake and the plain.
        [Fact]
        public void AFlatApproachIsClear()
        {
            var (d, g) = Approach(_ => 0f);
            Assert.True(float.IsPositiveInfinity(TerrainEnvelope.FirstObstruction(d, g, d.Length, 0f,
                TerrainEnvelope.GlideslopeGradient, TerrainEnvelope.GlideslopeClearance, TerrainEnvelope.GlideslopeFrom)));
        }

        // A building under the last few hundred metres, where the glideslope is meant to be low,
        // obstructs nothing.
        [Fact]
        public void TheGlideslopeIsNotTestedCloseIn()
        {
            var (d, g) = Approach(x => x <= 1000f ? 40f : 0f);
            Assert.True(float.IsPositiveInfinity(TerrainEnvelope.FirstObstruction(d, g, d.Length, 0f,
                TerrainEnvelope.GlideslopeGradient, TerrainEnvelope.GlideslopeClearance, TerrainEnvelope.GlideslopeFrom)));
        }

        // Ground beside the centreline counts: the highest across the approach at each distance.
        // At Meiringen landing west it stands within 15 m of the glideslope 150 to 300 m north of
        // the line a kilometre nearer the runway than on it.
        [Fact]
        public void GroundBesideTheCentrelineObstructsTheApproach()
        {
            const float step = TerrainEnvelope.ApproachStep;
            int n = (int)(TerrainEnvelope.ApproachReach / step);
            float[] lanes = TerrainEnvelope.ApproachLanes;
            var d = new float[n];
            var centre = new float[n];
            var across = new float[n];
            var heights = new float[lanes.Length];
            for (int i = 0; i < n; i++)
            {
                d[i] = step * (i + 1);
                for (int k = 0; k < lanes.Length; k++)
                    heights[k] = lanes[k] > 100f && d[i] >= 4000f ? 0.06f * d[i] - 10f : 0.04f * d[i] - 100f;
                centre[i] = heights[0];
                across[i] = TerrainEnvelope.Highest(heights, lanes.Length);
            }
            float onTheLine = TerrainEnvelope.FirstObstruction(d, centre, n, 0f,
                TerrainEnvelope.GlideslopeGradient, TerrainEnvelope.GlideslopeClearance, TerrainEnvelope.GlideslopeFrom);
            float beside = TerrainEnvelope.FirstObstruction(d, across, n, 0f,
                TerrainEnvelope.GlideslopeGradient, TerrainEnvelope.GlideslopeClearance, TerrainEnvelope.GlideslopeFrom);

            Assert.True(float.IsPositiveInfinity(onTheLine));
            Assert.Equal(4000f, beside);
        }

        [Fact]
        public void TheHighestAcrossSkipsWhatCannotBeUsed()
        {
            Assert.Equal(40f, TerrainEnvelope.Highest(new[] { 10f, float.NaN, 40f, float.PositiveInfinity }, 4));
            Assert.Equal(10f, TerrainEnvelope.Highest(new[] { 10f, 40f }, 1));
            Assert.True(float.IsNegativeInfinity(TerrainEnvelope.Highest(new[] { float.NaN }, 1)));
        }

        // 2 km out the glideslope is 120 m up: ground over 105 m obstructs it, and nothing does
        // inside 1.5 km.
        [Theory]
        [InlineData(1000f, 119f, false)]
        [InlineData(2000f, 106f, true)]
        [InlineData(2000f, 105f, false)]
        [InlineData(2000f, 100f, false)]
        [InlineData(2000f, float.NaN, false)]
        public void AnObstructionIsGroundWithinTheClearanceOfTheGlideslope(float distance, float ground, bool expected)
        {
            Assert.Equal(expected, TerrainEnvelope.Obstructs(distance, ground, 0f, TerrainEnvelope.GlideslopeGradient,
                TerrainEnvelope.GlideslopeClearance, TerrainEnvelope.GlideslopeFrom));
        }

        [Fact]
        public void TheApproachRunsThreeTurningRadiiAndTwoKilometres()
        {
            Assert.Equal(6500f, TerrainEnvelope.ApproachLength(1500f));
            Assert.Equal(11000f, TerrainEnvelope.ApproachLength(3000f));
        }

        [Fact]
        public void TheGamesChoiceStandsWhenItsApproachIsClear()
        {
            float inf = float.PositiveInfinity;
            Assert.Equal(0, TerrainEnvelope.ChooseLandingEnd(new[] { 10f, 170f }, new[] { inf, inf }, 2, 0, 5500f));
            Assert.Equal(0, TerrainEnvelope.ChooseLandingEnd(new[] { 10f, 170f }, new[] { 6000f, inf }, 2, 0, 5500f));
        }

        // Meiringen: an aircraft arriving from the east is lined up for 277; that glideslope is in
        // the mountain, so it is sent round to 097.
        [Fact]
        public void AnObstructedChoiceGoesToTheOtherEnd()
        {
            float inf = float.PositiveInfinity;
            Assert.Equal(1, TerrainEnvelope.ChooseLandingEnd(new[] { 10f, 170f }, new[] { 5000f, inf }, 2, 0, 5500f));
        }

        // Of several clear ends, the one most nearly lined up.
        [Fact]
        public void OfTheClearEndsTheBestLinedUpWins()
        {
            float inf = float.PositiveInfinity;
            Assert.Equal(2, TerrainEnvelope.ChooseLandingEnd(new[] { 5f, 170f, 60f, 120f }, new[] { 3000f, inf, inf, inf }, 4, 0, 5500f));
        }

        // With nothing clear, the end obstructed farthest out, if that is farther than the game's.
        [Fact]
        public void WithNothingClearTheFarthestObstructionWins()
        {
            Assert.Equal(1, TerrainEnvelope.ChooseLandingEnd(new[] { 5f, 170f }, new[] { 3000f, 5000f }, 2, 0, 10000f));
            Assert.Equal(0, TerrainEnvelope.ChooseLandingEnd(new[] { 5f, 170f }, new[] { 5000f, 3000f }, 2, 0, 10000f));
        }

        // ...but only by a kilometre or more: Sion's two ends meet the ground 8.30 and 8.45 km out
        // across their approaches, and a fast bomber (an 11 km approach) is not sent round for that.
        [Fact]
        public void WithNothingClearAMarginalGainIsNoReasonToMove()
        {
            Assert.Equal(0, TerrainEnvelope.ChooseLandingEnd(new[] { 5f, 170f }, new[] { 8300f, 8450f }, 2, 0, 11000f));
            Assert.Equal(1, TerrainEnvelope.ChooseLandingEnd(new[] { 5f, 170f }, new[] { 4000f, 9750f }, 2, 0, 11000f));
        }

        [Fact]
        public void AChoiceOutsideTheListIsLeftAlone()
        {
            Assert.Equal(-1, TerrainEnvelope.ChooseLandingEnd(new[] { 5f }, new[] { 100f }, 1, -1, 5500f));
            Assert.Equal(3, TerrainEnvelope.ChooseLandingEnd(new[] { 5f }, new[] { 100f }, 1, 3, 5500f));
        }
    }
}
