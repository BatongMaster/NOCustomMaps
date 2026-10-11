using System;

namespace CustomMaps
{
    /// <summary>
    /// How high, and how fast, an AI helicopter has to fly to get over the ground ahead of it with
    /// the climb it actually has. Pure arithmetic, so it is tested; the patches that sample the
    /// ground and apply it are in <c>Patches/AITerrainPatches.cs</c>.
    ///
    /// The game's rotorcraft AI (<c>AutopilotHelo.AutoAim</c>, decompiled) holds a height over the
    /// ground measured straight down, 30 m for an Ibis, and anticipates only through one sample
    /// about 600 m ahead and a time-to-impact along its velocity that wakes inside 7.5 seconds.
    /// Climbing up a slope the velocity runs parallel to the ground, so that check stays quiet
    /// until the skids touch, and nothing anywhere compares the climb a slope asks for with the
    /// climb the aircraft has. On the base maps that never matters. On Swiss Alps a fifth of the
    /// land is steeper than 30 degrees, and an Ibis at 30 m/s needs 17 m/s of climb to follow one;
    /// in the user's recording 23 Ibis flew into rising ground climbing at full collective.
    ///
    /// So the hold is raised early: to the altitude from which, climbing at the rate the aircraft
    /// has shown it can, it passes every point ahead at its usual height. And where even that is
    /// not enough at the speed it is flying, it is slowed to a speed at which it is.
    /// </summary>
    internal static class RotorPlan
    {
        /// <summary>How far ahead the plan looks, in seconds of flight, and its limits in metres.</summary>
        public const float LookAheadSeconds = 20f;
        public const float MinLookAhead = 600f;
        public const float MaxLookAhead = 3000f;

        /// <summary>The least speed the plan assumes, in m/s, so that an aircraft hovering before it
        /// sets off is not planned as if it would take minutes to reach the slope.</summary>
        public const float PlanningSpeed = 15f;

        /// <summary>The most the hold is raised over what the AI asked for, in metres.</summary>
        public const float MaxRaise = 1500f;

        /// <summary>How many points along each direction are sampled.</summary>
        public const int Samples = 16;

        /// <summary>An aircraft is slowed only once it is this much faster than the speed the plan
        /// allows, so that a plan near its limit does not flick the brake on and off.</summary>
        public const float SpeedMargin = 1.15f;

        /// <summary>
        /// How far ahead the point the aircraft is steered at to slow it stands, in seconds of the
        /// speed allowed, and its limits in metres. <c>AutopilotHelo</c> tilts towards its
        /// destination through a PD controller on the error across (P 0.001, D 0.01, the error
        /// held to 500 m), which comes to rest when the speed is a tenth of the distance: a fixed
        /// point ten seconds of the speed allowed ahead brings it down to that speed, and one a few
        /// metres ahead to a hover.
        /// </summary>
        public const float AnchorSeconds = 10f;
        public const float LeastAnchor = 10f;
        public const float MostAnchor = 500f;

        /// <summary>The point is lifted this high over the aircraft. <c>AutopilotHelo</c> also governs
        /// its collective at speed by the distance to its destination, which a point 50 m ahead
        /// would all but shut; the lift keeps that distance long and the collective open while
        /// the tilt alone, which looks only across, brakes.</summary>
        public const float AnchorLift = 600f;

        /// <summary>The headings tried, either side of the planned one, for a way round ground the
        /// aircraft cannot climb over, in degrees.</summary>
        public static readonly float[] DetourOffsets = { 20f, -20f, 40f, -40f, 60f, -60f, 90f, -90f };

        /// <summary>What turning off the planned heading costs, in metres of ground per degree, so
        /// a slightly higher way nearly straight on beats a lower one at right angles.</summary>
        public const float DetourPenalty = 3f;

        /// <summary>A way round has to be at least this much lower than straight on, in metres.</summary>
        public const float DetourGain = 50f;

        /// <summary>How far is planned for, before the direction it plans along is worth looking at:
        /// with nothing nearer than this ahead, nothing is planned.</summary>
        public const float LeastPlanDistance = 200f;

        /// <summary>How far ahead the plan looks, in metres, for an aircraft at
        /// <paramref name="speed"/> m/s that is <paramref name="toDestination"/> metres from where
        /// it is going: never past it.</summary>
        public static float LookAhead(float speed, float toDestination)
        {
            float l = Clamp(Math.Max(speed, 0f) * LookAheadSeconds, MinLookAhead, MaxLookAhead);
            if (Finite(toDestination)) l = Math.Min(l, Math.Max(toDestination, 0f));
            return l;
        }

        /// <summary>
        /// The altitude the aircraft has to be at now so that, climbing at <paramref name="climb"/>
        /// m/s from here while it flies on at <paramref name="speed"/> (no less than
        /// <see cref="PlanningSpeed"/>), it passes every sample at least <paramref name="hold"/>
        /// over the ground. Negative infinity with nothing ahead.
        /// </summary>
        public static float RequiredAltitude(float[] distance, float[] ground, int count, float hold,
                                             float speed, float climb)
        {
            float v = Math.Max(speed, PlanningSpeed);
            float c = Math.Max(climb, 0f);
            float required = float.NegativeInfinity;
            int n = Math.Min(count, Math.Min(distance.Length, ground.Length));
            for (int i = 0; i < n; i++)
            {
                if (!(distance[i] >= 0f) || !Finite(ground[i])) continue;
                float y = ground[i] + hold - c * distance[i] / v;
                if (y > required) required = y;
            }
            return required;
        }

        /// <summary>
        /// The hold to give the game for an aircraft at <paramref name="altitude"/> with
        /// <paramref name="radarAltitude"/> of air under it, when it has to be at
        /// <paramref name="required"/>: the game's hold is a height over the ground straight down,
        /// so the ground under the aircraft is taken off. Never less than the AI asked for, and
        /// never more than <see cref="MaxRaise"/> over it.
        /// </summary>
        public static float Hold(float required, float altitude, float radarAltitude, float hold)
        {
            if (!Finite(required) || !Finite(altitude) || !Finite(radarAltitude)) return hold;
            float groundBelow = altitude - radarAltitude;
            return Clamp(required - groundBelow, hold, hold + MaxRaise);
        }

        /// <summary>
        /// The clearance an AI aiming its nose at a target is planned with, given the hold it
        /// asked for and its own least height over the ground. <c>AIHeloCombatState</c> asks then
        /// for its height now plus 2 m, so as not to sink while it aims; planned with that, the
        /// raise for ground ahead would be the same however high the aircraft had already
        /// climbed, and its collective would stay full until the attack ended. Its least height
        /// does not move with it.
        /// </summary>
        public static float BoresightClearance(float hold, float least)
        {
            float floor = Finite(least) && least > 0f ? least : DefaultBoresightClearance;
            return Finite(hold) ? Math.Min(hold, floor) : floor;
        }

        /// <summary>The clearance a boresight aim is planned with when the aircraft has no least
        /// height of its own, in metres: an Ibis's.</summary>
        public const float DefaultBoresightClearance = 30f;

        /// <summary>
        /// The height over the ground the game's own aim asks for (<c>Autopilot.TerrainWaypoint</c>'s
        /// <c>altitudeTarget</c>) without the plan's raise in it. The raise
        /// (<paramref name="holdGiven"/> less <paramref name="ownHold"/>) is a height over the
        /// ground straight down, already counting every rise ahead; added again over the ground
        /// ahead it counts each twice, and asked for at the first sample 50 m out it pitched a
        /// cruising helicopter up at 60 degrees. A tilt-rotor scales its hold near its
        /// destination and adds 20 m in a turn before it aims (<c>AutopilotTiltwing.AutoAim</c>),
        /// so the raise is taken out of whichever leaves more.
        /// </summary>
        public static float WaypointHold(float altitudeTarget, float ownHold, float holdGiven)
        {
            if (!Finite(altitudeTarget)) return altitudeTarget;
            if (!Finite(ownHold) || !Finite(holdGiven) || !(holdGiven > ownHold) || !(holdGiven > 0f)) return altitudeTarget;

            float less = altitudeTarget - (holdGiven - ownHold);
            float scaled = altitudeTarget * Math.Max(ownHold, 0f) / holdGiven;
            return Math.Max(0f, Math.Max(less, scaled));
        }

        /// <summary>
        /// The clearance the envelope keeps over every sample ahead: the game's own target
        /// (<paramref name="own"/>, from <see cref="WaypointHold"/>), less whatever the hold
        /// <paramref name="ownHold"/> the AI asked for stands over the
        /// <paramref name="clearance"/> it is planned with (<see cref="BoresightClearance"/>):
        /// a hold that rises with the aircraft is no clearance to keep over ground ahead.
        /// </summary>
        public static float EnvelopeClearance(float own, float ownHold, float clearance)
        {
            if (!Finite(own) || !Finite(ownHold) || !Finite(clearance) || !(clearance < ownHold)) return own;
            return Math.Max(0f, own - (ownHold - clearance));
        }

        /// <summary>
        /// The angle, in radians from level, at which an aircraft at <paramref name="altitude"/>
        /// that has to be at <paramref name="required"/> is to climb: the climb it has, at the
        /// speed it flies (no less than <see cref="PlanningSpeed"/>). At speed the game flies a
        /// helicopter's height by pitching at its waypoint, with the collective governed by the
        /// speed (<c>AutopilotHelo.AutoAim</c>), so this is how the raised hold acts there; and
        /// it asks for no more climb than the plan itself assumed. Negative infinity when the
        /// aircraft is high enough.
        /// </summary>
        public static float PlanAngle(float altitude, float required, float climb, float speed)
        {
            if (!Finite(altitude) || !Finite(required) || !(altitude < required)) return float.NegativeInfinity;
            float c = Finite(climb) ? Math.Max(climb, 0f) : 0f;
            float v = Finite(speed) ? Math.Max(speed, PlanningSpeed) : PlanningSpeed;
            return (float)Math.Atan2(c, v);
        }

        /// <summary>
        /// The fastest the aircraft can fly and still, climbing at <paramref name="climb"/> m/s,
        /// reach every sample at least <paramref name="hold"/> over it: positive infinity when
        /// nothing ahead is above that already, and nothing at all when a sample asks for more
        /// than <paramref name="ceiling"/>, the highest it has shown it can climb, since no speed
        /// gets it over that.
        /// </summary>
        public static float AllowedSpeed(float altitude, float[] distance, float[] ground, int count,
                                         float hold, float climb, float ceiling)
        {
            float c = Math.Max(climb, 0f);
            float allowed = float.PositiveInfinity;
            int n = Math.Min(count, Math.Min(distance.Length, ground.Length));
            for (int i = 0; i < n; i++)
            {
                if (!(distance[i] > 0f) || !Finite(ground[i])) continue;
                float need = ground[i] + hold - altitude;
                if (need <= 0f) continue;
                if (ground[i] + hold > ceiling) return 0f;
                float v = c * distance[i] / need;
                if (v < allowed) allowed = v;
            }
            return allowed;
        }

        /// <summary>Under this speed allowed, in m/s, the aircraft is held in a hover where it is
        /// rather than let creep on.</summary>
        public const float HoverSpeed = 1f;

        /// <summary>Once slowed, the aircraft is let go only when the speed allowed is this much over
        /// its own, so that it does not brake and let go over and over at the limit.</summary>
        public const float ReleaseMargin = 1.3f;

        /// <summary>
        /// Whether an aircraft at <paramref name="speed"/> is to be held to
        /// <paramref name="allowed"/>, given whether it already was: it starts once it is
        /// <see cref="SpeedMargin"/> too fast, or the plan wants it hovering, and stops once nothing
        /// ahead limits it or the limit is <see cref="ReleaseMargin"/> over its speed.
        /// </summary>
        public static bool Slowing(bool slowing, float speed, float allowed)
        {
            if (!Finite(allowed)) return false;
            if (allowed < HoverSpeed) return true;
            return slowing ? allowed <= speed * ReleaseMargin : speed > allowed * SpeedMargin;
        }

        /// <summary>How far ahead to put the point that slows the aircraft to
        /// <paramref name="allowed"/> m/s, in metres.</summary>
        public static float AnchorDistance(float allowed)
            => Clamp(allowed * AnchorSeconds, LeastAnchor, MostAnchor);

        /// <summary>The highest the aircraft has to be over a corridor: its highest ground plus the
        /// hold. Negative infinity for an empty one.</summary>
        public static float CorridorTop(float[] ground, int count, float hold)
        {
            float top = float.NegativeInfinity;
            int n = Math.Min(count, ground.Length);
            for (int i = 0; i < n; i++)
                if (Finite(ground[i]) && ground[i] + hold > top) top = ground[i] + hold;
            return top;
        }

        /// <summary>
        /// The way round to take when straight on is over <paramref name="ceiling"/>, the highest
        /// the aircraft has shown it can climb: the index of the heading in
        /// <paramref name="offsets"/> (degrees off straight on) whose corridor top, plus
        /// <see cref="DetourPenalty"/> per degree, is lowest, preferring those under the ceiling.
        /// Minus one when none is at least <see cref="DetourGain"/> lower than straight on.
        /// </summary>
        public static int ChooseDetour(float[] offsets, float[] tops, int count, float straightTop, float ceiling)
        {
            int n = Math.Min(count, Math.Min(offsets.Length, tops.Length));
            int best = -1;
            bool bestUnder = false;
            float bestScore = float.PositiveInfinity;

            for (int i = 0; i < n; i++)
            {
                float top = tops[i];
                if (!Finite(top) || !(top <= straightTop - DetourGain)) continue;

                bool under = top < ceiling;
                float score = top + DetourPenalty * Math.Abs(offsets[i]);
                if (best < 0 || (under && !bestUnder) || (under == bestUnder && score < bestScore))
                {
                    best = i;
                    bestUnder = under;
                    bestScore = score;
                }
            }
            return best;
        }

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;

        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }

    /// <summary>
    /// The climb one helicopter has shown it has, learned in flight, and the altitude above which
    /// it has shown it cannot climb at all.
    ///
    /// No table would do: what a rotorcraft climbs at depends on its load, its fuel, the damage it
    /// carries and the air, which the game thins with altitude (<c>LevelInfo.GetAirDensity</c>; an
    /// Ibis that climbs at 16 to 19 m/s below 3 km manages 6 at 3.5 to 4 km, and a loaded one
    /// could not leave a pad at 2,345 m). So it starts from a middling guess and follows what the
    /// aircraft does: down to the vertical speed it reaches with its collective full, while it is
    /// slow enough that the collective is what holds its height; up to any faster climb it is
    /// seen to make.
    /// </summary>
    internal sealed class ClimbEstimate
    {
        /// <summary>The climb assumed before any is seen, and its limits, in m/s.</summary>
        public const float Start = 8f;
        public const float Least = 3f;
        public const float Most = 25f;

        /// <summary>The estimate follows what is seen with this time constant, in seconds: long
        /// enough that a gust or a moment's zoom climb does not teach it much.</summary>
        public const float LearnSeconds = 5f;

        /// <summary>A throttle at or over this is full collective.</summary>
        public const float Full = 0.95f;

        /// <summary>Seconds of full collective before the climb it gives is believed.</summary>
        public const float Settle = 2f;

        /// <summary>A climb slower than this, in m/s, held at full collective for
        /// <see cref="StallSeconds"/>, means the aircraft is as high as it can go.</summary>
        public const float StallRate = 1f;
        public const float StallSeconds = 6f;

        /// <summary>The ceiling is put this much over where the aircraft stalled, in metres, and
        /// forgotten after <see cref="CeilingLife"/> seconds or once the aircraft is seen that much
        /// over it: burning fuel or dropping cargo raises it.</summary>
        public const float CeilingMargin = 30f;
        public const float CeilingLife = 90f;

        public float Climb { get; private set; } = Start;

        /// <summary>The highest the aircraft can climb to, as an altitude; positive infinity until
        /// it has been seen to stall.</summary>
        public float Ceiling { get; private set; } = float.PositiveInfinity;

        float _full, _stalled, _ceilingAge;

        /// <summary>
        /// Takes one look at the aircraft, <paramref name="seconds"/> after the last, with the
        /// vertical speed it averaged over them. <paramref name="collectiveHolds"/> says the
        /// aircraft is slow enough that its collective, not its speed, is what holds its height (so
        /// a full collective means all the climb it has); <paramref name="climbAsked"/> says the
        /// plan wants it higher than it is.
        /// </summary>
        public void Observe(float seconds, float throttle, float verticalSpeed, float altitude,
                            bool collectiveHolds, bool climbAsked)
        {
            if (!(seconds > 0f) || float.IsNaN(verticalSpeed) || float.IsNaN(altitude)) return;

            float rate = Math.Min(1f, seconds / LearnSeconds);
            bool full = collectiveHolds && throttle >= Full;
            _full = full ? _full + seconds : 0f;

            if (full && _full >= Settle)
                Climb = Math.Max(Least, Climb + (Math.Min(Math.Max(verticalSpeed, 0f), Most) - Climb) * rate);
            else if (collectiveHolds && verticalSpeed > Climb)
                Climb = Math.Min(Most, Climb + (verticalSpeed - Climb) * rate);

            _stalled = full && _full >= Settle && climbAsked && verticalSpeed < StallRate ? _stalled + seconds : 0f;
            if (_stalled >= StallSeconds)
            {
                Ceiling = altitude + CeilingMargin;
                _ceilingAge = 0f;
                _stalled = 0f;
            }
            else if (!float.IsPositiveInfinity(Ceiling))
            {
                _ceilingAge += seconds;
                if (_ceilingAge > CeilingLife || altitude > Ceiling + CeilingMargin)
                    Ceiling = float.PositiveInfinity;
            }
        }
    }

    /// <summary>
    /// Whether a helicopter taking off should stop waiting to climb to the 20 m the game's
    /// take-off waits for, and fly on.
    ///
    /// <c>AIHeloTakeoffState</c> holds a helicopter over its pad until it is 20 m up and abandons
    /// it after 30 seconds. A pad high enough for the aircraft's load leaves it hanging a few
    /// metres up at full collective: in the user's recording five loaded Ibis on a pad at
    /// 2,345 m were all lost that way. Three rose to 8 m and sank back; two, their rotors
    /// drooping (the game cuts the collective when the rotor slows), never rose much over a
    /// metre. Flying on, a helicopter gains the lift a rotor gets from forward speed
    /// (<c>RotorShaft</c> divides its downwash by one plus a tenth of the airspeed across it),
    /// and one that stays is lost anyway. Replayed on the recording, the three fly on at 8 m
    /// about 23 seconds in, one of the other two at 27 seconds, and the last, still on its pad,
    /// is left to the game.
    /// </summary>
    internal sealed class TakeoffWatch
    {
        public enum Verdict { Wait, Hanging, Stalled, LastChance }

        /// <summary>Radar altitude over which the aircraft is off its pad, in metres; the game
        /// counts it airborne over 0.2 m.</summary>
        public const float LiftOff = 0.5f;

        /// <summary>The height the game's take-off waits for, in metres.</summary>
        public const float Goal = 20f;

        /// <summary>Seconds off the pad at full collective, still under <see cref="Goal"/>, after
        /// which it flies on whatever it is doing.</summary>
        public const float StuckSeconds = 8f;

        /// <summary>Once it has hung for <see cref="ClimbEstimate.Settle"/> seconds, a climb under
        /// <see cref="StallRate"/> m/s for <see cref="StallSeconds"/> means it is as high as it
        /// will get, and it flies on then: the three that rose to 8 m stopped climbing about two
        /// seconds before they began to sink.</summary>
        public const float StallRate = 1f;
        public const float StallSeconds = 2f;

        /// <summary>Seconds into the take-off after which one off its pad flies on in any case,
        /// before the game abandons it at 30. One still sitting on its pad is left to the game, and
        /// its crew walk away; flown on, it would only slide off.</summary>
        public const float LastChance = 27f;

        /// <summary>A pause this long between looks, in seconds, means a new take-off.</summary>
        public const float Gap = 1f;

        float _seen = float.NegativeInfinity, _started, _hanging = float.NaN, _stalled = float.NaN;

        /// <summary>
        /// Takes one look at the aircraft, at <paramref name="now"/> seconds: whether its rotor is
        /// up to speed, how high it is, its collective and its vertical speed.
        /// </summary>
        public Verdict Update(float now, bool rotorReady, float radarAltitude, float throttle, float verticalSpeed)
        {
            if (!(now >= _seen && now - _seen <= Gap))
            {
                _started = now;
                _hanging = float.NaN;
                _stalled = float.NaN;
            }
            _seen = now;

            Verdict verdict = Look(now, rotorReady, radarAltitude, throttle, verticalSpeed);
            if (verdict != Verdict.Wait) _seen = float.NegativeInfinity;
            return verdict;
        }

        Verdict Look(float now, bool rotorReady, float radarAltitude, float throttle, float verticalSpeed)
        {
            bool off = radarAltitude > LiftOff && radarAltitude < Goal;
            if (off && now - _started >= LastChance) return Verdict.LastChance;

            bool hanging = off && rotorReady && throttle >= ClimbEstimate.Full;
            if (!hanging)
            {
                _hanging = float.NaN;
                _stalled = float.NaN;
                return Verdict.Wait;
            }
            if (float.IsNaN(_hanging)) _hanging = now;
            if (now - _hanging >= StuckSeconds) return Verdict.Hanging;

            if (now - _hanging < ClimbEstimate.Settle || !(verticalSpeed < StallRate))
            {
                _stalled = float.NaN;
                return Verdict.Wait;
            }
            if (float.IsNaN(_stalled)) _stalled = now;
            return now - _stalled >= StallSeconds ? Verdict.Stalled : Verdict.Wait;
        }
    }
}
