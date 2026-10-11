using System;
using Xunit;

namespace CustomMaps.Tests
{
    public class RotorPlanTests
    {
        static (float[] Distance, float[] Ground) Corridor(float lookAhead, Func<float, float> ground)
        {
            int n = RotorPlan.Samples;
            var d = new float[n];
            var g = new float[n];
            for (int i = 0; i < n; i++)
            {
                d[i] = lookAhead * (i + 1) / n;
                g[i] = ground(d[i]);
            }
            return (d, g);
        }

        // Twenty seconds ahead, never under 600 m or over 3 km, and never past the destination.
        [Theory]
        [InlineData(10f, 10000f, 600f)]
        [InlineData(40f, 10000f, 800f)]
        [InlineData(200f, 10000f, 3000f)]
        [InlineData(40f, 300f, 300f)]
        [InlineData(40f, float.PositiveInfinity, 800f)]
        public void TheLookAheadIsTwentySecondsWithinLimits(float speed, float toDestination, float expected)
        {
            Assert.Equal(expected, RotorPlan.LookAhead(speed, toDestination));
        }

        // Over flat ground nothing changes: the hold is what the AI asked for.
        [Fact]
        public void FlatGroundLeavesTheHoldAlone()
        {
            var (d, g) = Corridor(800f, _ => 0f);
            float required = RotorPlan.RequiredAltitude(d, g, d.Length, 30f, 40f, 8f);
            Assert.True(required < 30f);
            Assert.Equal(30f, RotorPlan.Hold(required, 30f, 30f, 30f));
        }

        // A slope the aircraft out-climbs at its speed changes nothing either: 8 m/s at 40 m/s
        // follows a rise of one in five.
        [Fact]
        public void ASlopeWithinTheClimbLeavesTheHoldAlone()
        {
            var (d, g) = Corridor(800f, x => 0.15f * x);
            float required = RotorPlan.RequiredAltitude(d, g, d.Length, 30f, 40f, 8f);
            Assert.Equal(30f, RotorPlan.Hold(required, 30f, 30f, 30f));
            Assert.True(RotorPlan.AllowedSpeed(30f, d, g, d.Length, 30f, 8f, float.PositiveInfinity) >= 40f);
        }

        // A 600 m wall a kilometre ahead, at 40 m/s with 8 m/s of climb: the aircraft has to be at
        // 630 m when it gets there in 25 s, so at 430 m now; and it can make that only at 13 m/s.
        [Fact]
        public void AWallAheadRaisesTheHoldAndTheSpeedAllowed()
        {
            var d = new[] { 500f, 1000f };
            var g = new[] { 0f, 600f };
            float required = RotorPlan.RequiredAltitude(d, g, 2, 30f, 40f, 8f);
            Assert.Equal(430f, required, 3);
            Assert.Equal(430f, RotorPlan.Hold(required, 30f, 30f, 30f), 3);
            Assert.Equal(8f * 1000f / 600f, RotorPlan.AllowedSpeed(30f, d, g, 2, 30f, 8f, float.PositiveInfinity), 3);
        }

        // The game's hold is a height over the ground straight down, so the ground under the
        // aircraft comes off the altitude required.
        [Fact]
        public void TheHoldIsMeasuredFromTheGroundBelow()
        {
            Assert.Equal(330f, RotorPlan.Hold(1530f, 1250f, 50f, 30f), 3);
        }

        [Fact]
        public void TheHoldIsNeverLoweredAndNeverRaisedPastTheLimit()
        {
            Assert.Equal(30f, RotorPlan.Hold(-1000f, 30f, 30f, 30f));
            Assert.Equal(30f + RotorPlan.MaxRaise, RotorPlan.Hold(9000f, 30f, 30f, 30f));
            Assert.Equal(30f, RotorPlan.Hold(float.NegativeInfinity, 30f, 30f, 30f));
            Assert.Equal(30f, RotorPlan.Hold(float.NaN, 30f, 30f, 30f));
        }

        // ---------------------------------------------------------------------------------------
        // The raised hold and the game's waypoint

        // A helicopter passes its hold plus the terrain urgency; the raise comes out, the urgency
        // stays. With no raise the target is left alone.
        [Theory]
        [InlineData(330f, 30f, 330f, 30f)]
        [InlineData(335f, 30f, 330f, 35f)]
        [InlineData(30f, 30f, 30f, 30f)]
        [InlineData(37.5f, 30f, 30f, 37.5f)]
        public void TheRaiseIsTakenOutOfAHelicoptersWaypoint(float target, float own, float given, float expected)
        {
            Assert.Equal(expected, RotorPlan.WaypointHold(target, own, given), 3);
        }

        // A tilt-rotor scales its hold near its destination and adds 20 m in a turn before it aims.
        [Fact]
        public void TheRaiseIsTakenOutOfATiltrotorsWaypoint()
        {
            // Turning, far out: 330 + 20 asked, 30 + 20 meant.
            Assert.Equal(50f, RotorPlan.WaypointHold(350f, 30f, 330f), 3);
            // Scaled by a half near its destination: 165 asked, 15 meant.
            Assert.Equal(15f, RotorPlan.WaypointHold(165f, 30f, 330f), 3);
            // Both: 185 asked, 35 meant (half of 30, and 20). The two cannot be told apart; it is
            // read as at least the scaled part, and never over what was meant.
            Assert.InRange(RotorPlan.WaypointHold(185f, 30f, 330f), 15f, 35f);
        }

        [Fact]
        public void AWaypointHoldThatCannotBeWorkedOutIsLeftAlone()
        {
            Assert.Equal(330f, RotorPlan.WaypointHold(330f, float.NaN, 330f));
            Assert.Equal(330f, RotorPlan.WaypointHold(330f, 30f, float.PositiveInfinity));
            Assert.True(float.IsNaN(RotorPlan.WaypointHold(float.NaN, 30f, 330f)));
            Assert.Equal(0f, RotorPlan.WaypointHold(10f, 0f, 330f));
        }

        // The envelope keeps the AI's own clearance; for an aim at a target, whose hold rises with
        // the aircraft, the least height it is planned with.
        [Theory]
        [InlineData(30f, 30f, 30f, 30f)]
        [InlineData(152f, 152f, 30f, 30f)]
        [InlineData(172f, 152f, 30f, 50f)]
        [InlineData(10f, 152f, 30f, 0f)]
        [InlineData(30f, 30f, float.NaN, 30f)]
        public void TheEnvelopeKeepsTheClearancePlannedWith(float own, float ownHold, float clearance, float expected)
        {
            Assert.Equal(expected, RotorPlan.EnvelopeClearance(own, ownHold, clearance), 3);
        }

        // A helicopter cruising at 30 m towards a slope it has to start climbing early for: the
        // plan raises its hold by hundreds of metres. Its waypoint, worked out as the patch does,
        // keeps the AI's own 30 m over every sample and asks for the plan's climb, no more: the
        // raised hold over every sample would have pitched it up at the 60-degree limit.
        [Fact]
        public void ACruisingHelicopterIsNotPitchedUpByTheRaise()
        {
            const float hold = 30f, speed = 45f, climb = 8f, altitude = hold;
            var (d, g) = Corridor(RotorPlan.LookAhead(speed, float.PositiveInfinity), x => x < 300f ? 0f : 0.8f * (x - 300f));
            float required = RotorPlan.RequiredAltitude(d, g, d.Length, hold, speed, climb);
            float given = RotorPlan.Hold(required, altitude, hold, hold);
            Assert.True(given > hold + 200f);

            float lookAhead = Math.Max(speed, 100f) * 6f;
            float horizon = TerrainEnvelope.Horizon(lookAhead);
            var wd = new float[TerrainEnvelope.Samples];
            var wg = new float[TerrainEnvelope.Samples];
            for (int i = 0; i < wd.Length; i++)
            {
                wd[i] = TerrainEnvelope.SampleDistance(i + 1, wd.Length, horizon);
                wg[i] = wd[i] < 300f ? 0f : 0.8f * (wd[i] - 300f);
            }
            float groundThere = 0.8f * (lookAhead - 300f);

            float own = RotorPlan.WaypointHold(given, hold, given);
            float clearance = RotorPlan.EnvelopeClearance(own, hold, hold);
            float plan = RotorPlan.PlanAngle(altitude, required, climb, speed);
            float aim = TerrainEnvelope.WaypointAngle(Math.Max(TerrainEnvelope.FollowAngle(altitude, lookAhead, groundThere, own), plan),
                TerrainEnvelope.ClimbAngle(altitude, wd, wg, wd.Length, clearance, 0f, hold, lookAhead, TerrainEnvelope.Cone));

            float raised = TerrainEnvelope.WaypointAngle(TerrainEnvelope.FollowAngle(altitude, lookAhead, groundThere, given),
                TerrainEnvelope.ClimbAngle(altitude, wd, wg, wd.Length, given, TerrainEnvelope.Cone));
            float ownOnly = TerrainEnvelope.WaypointAngle(TerrainEnvelope.FollowAngle(altitude, lookAhead, groundThere, hold),
                TerrainEnvelope.ClimbAngle(altitude, wd, wg, wd.Length, hold, 0f, hold, lookAhead, TerrainEnvelope.Cone));

            Assert.Equal(TerrainEnvelope.MaxAngle, raised, 4);
            Assert.Equal(Math.Max(ownOnly, plan), aim, 4);
            Assert.True(aim < TerrainEnvelope.MaxAngle - 0.1f);
        }

        // Below the altitude required, the plan asks for the climb it assumed, at the speed flown
        // (no less than a walking pace); at or above it, for nothing.
        [Fact]
        public void ThePlansAngleIsTheClimbItAssumed()
        {
            Assert.Equal(Math.Atan2(8f, 45f), RotorPlan.PlanAngle(100f, 200f, 8f, 45f), 5);
            Assert.Equal(Math.Atan2(8f, RotorPlan.PlanningSpeed), RotorPlan.PlanAngle(100f, 200f, 8f, 2f), 5);
            Assert.True(float.IsNegativeInfinity(RotorPlan.PlanAngle(200f, 200f, 8f, 45f)));
            Assert.True(float.IsNegativeInfinity(RotorPlan.PlanAngle(100f, float.NegativeInfinity, 8f, 45f)));
        }

        // ---------------------------------------------------------------------------------------
        // Aiming the nose at a target

        // AIHeloCombatState then asks for its height now plus 2 m; the plan uses its least height.
        [Theory]
        [InlineData(52f, 30f, 30f)]
        [InlineData(20f, 30f, 20f)]
        [InlineData(252f, 0f, RotorPlan.DefaultBoresightClearance)]
        [InlineData(252f, float.NaN, RotorPlan.DefaultBoresightClearance)]
        [InlineData(float.NaN, 30f, 30f)]
        public void AnAimIsPlannedWithTheLeastHeight(float hold, float least, float expected)
        {
            Assert.Equal(expected, RotorPlan.BoresightClearance(hold, least));
        }

        // A helicopter hovering 50 m up, aiming at a target beyond a 300 m rise. Planned with the
        // hold it asks for (its height plus 2 m), the raise was the same however high it climbed,
        // and its collective stayed full; planned with its least height, the raise shrinks as it
        // climbs and is gone once it is high enough.
        [Fact]
        public void TheRaiseForAnAimShrinksAsTheAircraftClimbs()
        {
            var (d, g) = Corridor(RotorPlan.LookAhead(0f, 2000f), x => x < 200f ? 0f : Math.Min(300f, 1.5f * (x - 200f)));
            float Raise(float agl, bool now)
            {
                float hold = agl + 2f;
                float clearance = now ? RotorPlan.BoresightClearance(hold, 30f) : hold;
                float required = RotorPlan.RequiredAltitude(d, g, d.Length, clearance, 0f, 8f);
                return RotorPlan.Hold(required, agl, agl, hold) - hold;
            }

            Assert.True(Raise(50f, now: false) > 38f);
            Assert.Equal(Raise(50f, now: false), Raise(250f, now: false), 3);

            Assert.True(Raise(50f, now: true) > 0f);
            Assert.True(Raise(150f, now: true) < Raise(50f, now: true));
            Assert.Equal(0f, Raise(400f, now: true), 3);
        }

        // The planner assumes at least 15 m/s, so a hovering aircraft about to set off is planned
        // for the climb it will need, not one spread over minutes.
        [Fact]
        public void AHoveringAircraftIsPlannedAtAWalkingPace()
        {
            var d = new[] { 300f };
            var g = new[] { 400f };
            Assert.Equal(RotorPlan.RequiredAltitude(d, g, 1, 30f, 15f, 8f), RotorPlan.RequiredAltitude(d, g, 1, 30f, 0f, 8f));
            Assert.Equal(430f - 8f * 300f / 15f, RotorPlan.RequiredAltitude(d, g, 1, 30f, 0f, 8f), 3);
        }

        // Over ground higher than the aircraft has shown it can climb, no speed is fast enough:
        // it is held where it is.
        [Fact]
        public void GroundOverTheCeilingAllowsNoSpeed()
        {
            var d = new[] { 500f, 1000f };
            var g = new[] { 2400f, 3100f };
            Assert.Equal(0f, RotorPlan.AllowedSpeed(2400f, d, g, 2, 30f, 8f, 3000f));
            Assert.True(RotorPlan.AllowedSpeed(2400f, d, g, 2, 30f, 8f, 3200f) > 0f);
        }

        [Fact]
        public void NothingAboveAllowsAnySpeed()
        {
            var (d, g) = Corridor(800f, _ => 0f);
            Assert.True(float.IsPositiveInfinity(RotorPlan.AllowedSpeed(100f, d, g, d.Length, 30f, 8f, float.PositiveInfinity)));
        }

        // The two agree: at or under the speed allowed, the altitude required is no higher than
        // the aircraft already is, so the raised hold asks for no climb it cannot make.
        [Fact]
        public void AtTheSpeedAllowedNoMoreClimbIsAskedThanTheAircraftHas()
        {
            var random = new Random(3);
            for (int run = 0; run < 500; run++)
            {
                float altitude = (float)random.NextDouble() * 2000f;
                var d = new float[RotorPlan.Samples];
                var g = new float[RotorPlan.Samples];
                for (int i = 0; i < d.Length; i++)
                {
                    d[i] = 50f * (i + 1);
                    g[i] = (float)random.NextDouble() * 2500f;
                }
                float climb = 3f + (float)random.NextDouble() * 15f;
                float allowed = RotorPlan.AllowedSpeed(altitude, d, g, d.Length, 30f, climb, float.PositiveInfinity);
                if (float.IsPositiveInfinity(allowed) || allowed < RotorPlan.PlanningSpeed) continue;

                float required = RotorPlan.RequiredAltitude(d, g, d.Length, 30f, allowed, climb);
                Assert.True(required <= altitude + 0.01f, $"run {run}: {required} over {altitude}");
            }
        }

        // Slowing starts once the aircraft is 15% too fast, holds while the limit is near its
        // speed, and lets go once nothing ahead limits it or the limit is well over its speed.
        [Theory]
        [InlineData(false, 20f, 15f, true)]
        [InlineData(false, 16f, 15f, false)]
        [InlineData(true, 15f, 15f, true)]
        [InlineData(true, 10f, 15f, false)]
        [InlineData(true, 30f, float.PositiveInfinity, false)]
        [InlineData(false, 0f, 0.5f, true)]
        [InlineData(false, 0f, 0f, true)]
        public void SlowingHasHysteresis(bool slowing, float speed, float allowed, bool expected)
        {
            Assert.Equal(expected, RotorPlan.Slowing(slowing, speed, allowed));
        }

        // The point that slows the aircraft stands ten seconds of the speed allowed ahead: the
        // game's tilt controller settles at a tenth of the distance a second.
        [Theory]
        [InlineData(0f, 10f)]
        [InlineData(5f, 50f)]
        [InlineData(20f, 200f)]
        [InlineData(100f, 500f)]
        public void TheAnchorIsTenSecondsAhead(float allowed, float expected)
        {
            Assert.Equal(expected, RotorPlan.AnchorDistance(allowed));
        }

        [Fact]
        public void TheCorridorTopIsTheHighestGroundPlusTheHold()
        {
            Assert.Equal(1030f, RotorPlan.CorridorTop(new[] { 200f, 1000f, float.NaN, 500f }, 4, 30f));
            Assert.True(float.IsNegativeInfinity(RotorPlan.CorridorTop(new float[0], 0, 30f)));
        }

        // Straight on is 3,000 m against a 2,600 m ceiling. Of the ways round, one under the
        // ceiling beats one over it however close to straight on; of those under it, the lowest
        // once each degree off course is counted as 3 m.
        [Fact]
        public void AWayRoundUnderTheCeilingIsPreferred()
        {
            var offsets = new[] { 20f, -40f, 90f };
            Assert.Equal(1, RotorPlan.ChooseDetour(offsets, new[] { 2900f, 2500f, 2450f }, 3, 3000f, 2600f));
            Assert.Equal(2, RotorPlan.ChooseDetour(offsets, new[] { 2900f, 2500f, 2000f }, 3, 3000f, 2600f));
            Assert.Equal(0, RotorPlan.ChooseDetour(offsets, new[] { 2900f, 2950f, 2940f }, 3, 3000f, 2600f));
        }

        [Fact]
        public void NoWayRoundThatIsNotMuchLower()
        {
            var offsets = new[] { 20f, -20f };
            Assert.Equal(-1, RotorPlan.ChooseDetour(offsets, new[] { 2990f, 3100f }, 2, 3000f, 2600f));
            Assert.Equal(-1, RotorPlan.ChooseDetour(offsets, new[] { float.NaN, float.PositiveInfinity }, 2, 3000f, 2600f));
        }

        // ---------------------------------------------------------------------------------------
        // The climb learned in flight

        static void Fly(ClimbEstimate e, float seconds, float throttle, float climb, float altitude, bool holds, bool asked)
        {
            for (float t = 0f; t < seconds; t += 0.5f)
                e.Observe(0.5f, throttle, climb, altitude, holds, asked);
        }

        [Fact]
        public void TheEstimateStartsMiddling()
        {
            var e = new ClimbEstimate();
            Assert.Equal(ClimbEstimate.Start, e.Climb);
            Assert.True(float.IsPositiveInfinity(e.Ceiling));
        }

        // A loaded aircraft at full collective climbing 4 m/s: the estimate comes down to 4.
        [Fact]
        public void FullCollectiveTeachesTheClimbItGives()
        {
            var e = new ClimbEstimate();
            Fly(e, 40f, 1f, 4f, 1000f, holds: true, asked: false);
            Assert.InRange(e.Climb, 3.9f, 4.2f);
        }

        // ...but never below the least, however little it climbs.
        [Fact]
        public void TheEstimateHasAFloor()
        {
            var e = new ClimbEstimate();
            Fly(e, 60f, 1f, -2f, 1000f, holds: true, asked: false);
            Assert.Equal(ClimbEstimate.Least, e.Climb, 3);
        }

        // A faster climb seen at any throttle shows the aircraft has it.
        [Fact]
        public void AFasterClimbRaisesTheEstimate()
        {
            var e = new ClimbEstimate();
            Fly(e, 40f, 0.6f, 15f, 1000f, holds: true, asked: false);
            Assert.InRange(e.Climb, 14.5f, 15f);
        }

        // At speed the game sets the collective by the distance to go, full in level flight, so
        // a full collective there says nothing about the climb.
        [Fact]
        public void AtSpeedNothingIsLearned()
        {
            var e = new ClimbEstimate();
            Fly(e, 40f, 1f, 0f, 1000f, holds: false, asked: true);
            Assert.Equal(ClimbEstimate.Start, e.Climb);
            Assert.True(float.IsPositiveInfinity(e.Ceiling));
        }

        // Short bursts of full collective are not believed until they have lasted.
        [Fact]
        public void ABriefFullCollectiveIsNotBelieved()
        {
            var e = new ClimbEstimate();
            Fly(e, 1.5f, 1f, 0f, 1000f, holds: true, asked: true);
            Assert.Equal(ClimbEstimate.Start, e.Climb);
        }

        // Full collective, asked to climb, not climbing: once the full collective has settled, six
        // seconds more (the look that settles it counting as the first) put the ceiling where it
        // stalled plus the margin.
        [Fact]
        public void AStallMarksTheCeiling()
        {
            var e = new ClimbEstimate();
            Fly(e, 7f, 1f, 0.2f, 3200f, holds: true, asked: true);
            Assert.True(float.IsPositiveInfinity(e.Ceiling));
            Fly(e, 1f, 1f, 0.2f, 3200f, holds: true, asked: true);
            Assert.Equal(3200f + ClimbEstimate.CeilingMargin, e.Ceiling);
        }

        [Fact]
        public void TheCeilingIsForgottenInTimeOrWhenClimbedPast()
        {
            var e = new ClimbEstimate();
            Fly(e, 8.5f, 1f, 0.2f, 3200f, holds: true, asked: true);
            Assert.False(float.IsPositiveInfinity(e.Ceiling));
            Fly(e, ClimbEstimate.CeilingLife + 1f, 0.5f, 0f, 3100f, holds: true, asked: false);
            Assert.True(float.IsPositiveInfinity(e.Ceiling));

            Fly(e, 8.5f, 1f, 0.2f, 3200f, holds: true, asked: true);
            Assert.False(float.IsPositiveInfinity(e.Ceiling));
            e.Observe(0.5f, 0.5f, 5f, 3300f, true, false);
            Assert.True(float.IsPositiveInfinity(e.Ceiling));
        }

        // ---------------------------------------------------------------------------------------
        // Taking off from a pad too high to climb off

        static TakeoffWatch.Verdict Watch(TakeoffWatch w, float from, float to, float agl, float throttle, float climb,
                                          bool rotor = true, float step = 0.02f)
        {
            for (float t = from; t < to; t += step)
            {
                TakeoffWatch.Verdict v = w.Update(t, rotor, agl, throttle, climb);
                if (v != TakeoffWatch.Verdict.Wait) return v;
            }
            return TakeoffWatch.Verdict.Wait;
        }

        // Climbing away at full collective it is left to the game, which sends it on at 20 m.
        [Fact]
        public void AHelicopterClimbingAwayIsLeftToTheGame()
        {
            var w = new TakeoffWatch();
            Assert.Equal(TakeoffWatch.Verdict.Wait, Watch(w, 0f, 7.9f, 5f, 1f, 3f));
        }

        // Hanging off the pad at full collective, still rising slowly: on after eight seconds.
        [Fact]
        public void HangingAtFullCollectiveItFliesOn()
        {
            var w = new TakeoffWatch();
            Assert.Equal(TakeoffWatch.Verdict.Hanging, Watch(w, 0f, 9f, 5f, 1f, 1.5f));
        }

        // As the three Ibis that rose to 8 m: once it has stopped climbing for two seconds, after
        // the two it takes to settle, it flies on, before it starts to sink.
        [Fact]
        public void StoppedClimbingItFliesOnBeforeItSinks()
        {
            var w = new TakeoffWatch();
            Assert.Equal(TakeoffWatch.Verdict.Wait, Watch(w, 10f, 14f, 6f, 1f, 1.5f));
            Assert.Equal(TakeoffWatch.Verdict.Wait, Watch(w, 14f, 15.9f, 8f, 1f, 0.2f));
            Assert.Equal(TakeoffWatch.Verdict.Stalled, Watch(w, 15.9f, 16.5f, 8f, 1f, 0.2f));
        }

        // Not off the pad, its rotor not up to speed, or its collective not full: no verdict, and
        // the count starts again.
        [Fact]
        public void OnlyHangingAtFullCollectiveCounts()
        {
            var w = new TakeoffWatch();
            Assert.Equal(TakeoffWatch.Verdict.Wait, Watch(w, 0f, 7f, 0.3f, 1f, 0f));
            Assert.Equal(TakeoffWatch.Verdict.Wait, Watch(w, 7f, 14f, 5f, 1f, 1.5f, rotor: false));
            Assert.Equal(TakeoffWatch.Verdict.Wait, Watch(w, 14f, 21f, 5f, 0.8f, 1.5f));
            Assert.Equal(TakeoffWatch.Verdict.Wait, Watch(w, 21f, 26.9f, 5f, 1f, 1.5f));
        }

        // Three seconds before the game gives up, one just off its pad flies on whatever it is
        // doing; one still on it is left to the game, whose crew walk away.
        [Fact]
        public void JustBeforeTheGameGivesUpOneOffThePadFliesOn()
        {
            var off = new TakeoffWatch();
            Assert.Equal(TakeoffWatch.Verdict.Wait, Watch(off, 0f, 26.9f, 0.3f, 0.6f, 0f));
            Assert.Equal(TakeoffWatch.Verdict.LastChance, Watch(off, 26.9f, 27.2f, 1.1f, 0.6f, 0f, rotor: false));

            var on = new TakeoffWatch();
            Assert.Equal(TakeoffWatch.Verdict.Wait, Watch(on, 0f, 40f, 0.3f, 1f, 0f));
        }

        // A pause means a new take-off: the clocks start again.
        [Fact]
        public void APauseStartsANewTakeoff()
        {
            var w = new TakeoffWatch();
            Assert.Equal(TakeoffWatch.Verdict.Wait, Watch(w, 0f, 26f, 0.3f, 1f, 0f));
            Assert.Equal(TakeoffWatch.Verdict.Wait, Watch(w, 100f, 120f, 0.3f, 1f, 0f));
            Assert.Equal(TakeoffWatch.Verdict.Wait, Watch(w, 120f, 126.9f, 1.1f, 0.6f, 0f));
            Assert.Equal(TakeoffWatch.Verdict.LastChance, Watch(w, 126.9f, 127.2f, 1.1f, 0.6f, 0f));
        }
    }
}
