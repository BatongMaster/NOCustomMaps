using System;

namespace CustomMaps
{
    /// <summary>
    /// How high an AI aircraft has to aim to clear the ground ahead of it, from heights sampled
    /// along its path. Pure geometry, so it is tested; the patches that sample the ground and use
    /// it are in <c>Patches/AITerrainPatches.cs</c>.
    ///
    /// The game's AI looks at the terrain in two places only (decompiled, 14 August build):
    ///
    ///  - <c>Autopilot.TerrainWaypoint</c>, which aims a terrain-following aircraft at one point a
    ///    few seconds ahead: one vertical line-cast there, then up to six raises of 30 m, 60 m and
    ///    so on (630 m in all) while that point cannot see the aircraft. A ridge between the two
    ///    that stands higher than that above the far ground is never seen.
    ///  - <c>TerrainWarningSystem.CheckTerrain</c>, a line-cast along the velocity five seconds
    ///    long, whose urgency drops to nothing the moment the line clears.
    ///
    /// Both were tuned on the base maps, whose ground rises at most 966 m within 5 km (the 99th
    /// percentile, Heartland). Swiss Alps rises 1,529 m, on slopes twice as steep, so an aircraft
    /// aiming at one point meets the ridge before it: modelled, 4.4% of a fighter's waypoints and
    /// 6.3% of a bomber's are still behind rock after the six raises, against 0.16% and 0.35% on
    /// Heartland.
    ///
    /// What replaces the one point is an envelope: the steepest climb that clears every sample out
    /// to twice the game's own look-ahead, by the aircraft's clearance plus a cone that grows with
    /// distance, so a far ridge is cleared with room to spare and a near one exactly. An aircraft
    /// over open ground gets the game's own aim back, since the game's point is one of the
    /// samples. One below the height it holds is asked to make that up over the look-ahead, as the
    /// game's own aim does, not by the first sample.
    /// </summary>
    internal static class TerrainEnvelope
    {
        /// <summary>The least a fixed-wing aircraft is let fly over the ground, in metres. Radar
        /// evasion asks for 10 m (<c>EvadeModeRadar</c>), which on a valley floor between 40-degree
        /// walls leaves no room for the pull-up that follows.</summary>
        public const float PlaneClearance = 30f;

        /// <summary>Extra clearance per metre of distance: 20 m a kilometre. The far samples are the
        /// least certain to be on the path the aircraft will fly, which turns towards its
        /// destination as it goes.</summary>
        public const float Cone = 0.02f;

        /// <summary>The steepest the aim may be, up or down: the game's own <c>AutoAim</c> handles
        /// anything, but a 60-degree climb is already more than any aircraft in the game holds.</summary>
        public const float MaxAngle = (float)(Math.PI / 3.0);

        /// <summary>How many points along each direction are sampled.</summary>
        public const int Samples = 24;

        /// <summary>The envelope reaches this many times the game's look-ahead.</summary>
        public const float HorizonFactor = 2f;

        /// <summary>The least distance to a destination at which a fixed-wing aircraft flying
        /// straight at it gets a floor, in metres, and in seconds of flight: closer than that it is
        /// on an attack run at something it can see.</summary>
        public const float FloorMinDistance = 1500f;
        public const float FloorMinSeconds = 6f;

        /// <summary>The part of the way to a destination a floor leaves out, so that the ground the
        /// destination stands on is not a ridge to climb over: in metres, and in seconds.</summary>
        public const float FloorStandoff = 1000f;
        public const float FloorStandoffSeconds = 3f;

        /// <summary>
        /// The steepest angle, in radians from level, at which an aircraft at
        /// <paramref name="altitude"/> clears every sample by <paramref name="clearance"/> plus
        /// <paramref name="cone"/> per metre of distance. Samples at a distance of zero or less are
        /// skipped. With nothing to clear it is minus <see cref="MaxAngle"/>.
        /// </summary>
        public static float ClimbAngle(float altitude, float[] distance, float[] ground, int count,
                                       float clearance, float cone)
            => ClimbAngle(altitude, distance, ground, count, clearance, clearance, float.PositiveInfinity, 0f, cone);

        /// <summary>
        /// <see cref="ClimbAngle(float, float[], float[], int, float, float)"/> for an aircraft
        /// <paramref name="aboveGround"/> over the ground that holds <paramref name="hold"/> over
        /// it, never less than <paramref name="least"/>: each sample asks for the
        /// <see cref="RampedClearance"/> at its distance.
        /// </summary>
        public static float ClimbAngle(float altitude, float[] distance, float[] ground, int count,
                                       float hold, float least, float aboveGround, float lookAhead, float cone)
        {
            float best = -MaxAngle;
            int n = Math.Min(count, Math.Min(distance.Length, ground.Length));
            for (int i = 0; i < n; i++)
            {
                float d = distance[i];
                if (!(d > 0f) || !Finite(ground[i])) continue;

                float clearance = RampedClearance(hold, least, aboveGround, d, lookAhead);
                float rise = ground[i] + clearance + cone * d - altitude;
                float angle = (float)Math.Atan2(rise, d);
                if (angle > best) best = angle;
            }
            return best;
        }

        /// <summary>
        /// The clearance asked for at <paramref name="distance"/> ahead: the hold, no less than
        /// <paramref name="least"/> (<see cref="WaypointClearance"/>). An aircraft
        /// <paramref name="aboveGround"/> over the ground, lower than that, is asked for its own
        /// height close in, rising to the hold at <paramref name="lookAhead"/>: the game's own aim
        /// spreads that climb over its look-ahead, and asking for all of it at the first sample
        /// sent an aeroplane going round or climbing out up at about ten times the angle the game
        /// would.
        /// </summary>
        public static float RampedClearance(float hold, float least, float aboveGround, float distance, float lookAhead)
        {
            float c = WaypointClearance(hold, least);
            if (!Finite(aboveGround) || !(aboveGround < c) || !(lookAhead > 0f) || !Finite(distance)) return c;

            float t = Math.Min(Math.Max(distance, 0f) / lookAhead, 1f);
            return Math.Max(least, aboveGround + (c - aboveGround) * t);
        }

        /// <summary>
        /// The angle at which the game's own terrain-following aims, without its line-of-sight
        /// raises: at the ground at the look-ahead point plus one metre plus the altitude asked for.
        /// </summary>
        public static float FollowAngle(float altitude, float lookAhead, float groundAtLookAhead, float altitudeTarget)
        {
            if (!(lookAhead > 0f)) return 0f;
            return (float)Math.Atan2(groundAtLookAhead + 1f + altitudeTarget - altitude, lookAhead);
        }

        /// <summary>
        /// Where a terrain-following aircraft aims: the steeper of the game's own aim and the
        /// envelope's, held within <see cref="MaxAngle"/> either way.
        /// </summary>
        public static float WaypointAngle(float followAngle, float envelopeAngle)
        {
            float a = Math.Max(followAngle, envelopeAngle);
            if (!Finite(a)) return 0f;
            return Math.Min(Math.Max(a, -MaxAngle), MaxAngle);
        }

        /// <summary>The clearance a terrain-following waypoint keeps: the altitude the AI asked for,
        /// no less than <paramref name="least"/>.</summary>
        public static float WaypointClearance(float altitudeTarget, float least)
        {
            if (!Finite(altitudeTarget)) return least;
            return Math.Max(altitudeTarget, least);
        }

        /// <summary>How far ahead the envelope reaches for a look-ahead of
        /// <paramref name="lookAhead"/> metres.</summary>
        public static float Horizon(float lookAhead) => Math.Max(lookAhead, 0f) * HorizonFactor;

        /// <summary>The distance of sample <paramref name="index"/> (from 1) of
        /// <paramref name="count"/> spread evenly out to <paramref name="horizon"/>.</summary>
        public static float SampleDistance(int index, int count, float horizon)
            => count > 0 ? horizon * index / count : 0f;

        /// <summary>
        /// Whether an aircraft flying straight at a destination
        /// <paramref name="distance"/> metres away (across) at <paramref name="speed"/> is far
        /// enough from it to be given a floor at all.
        /// </summary>
        public static bool FloorApplies(float distance, float speed)
            => distance > Math.Max(FloorMinDistance, FloorMinSeconds * Math.Max(speed, 0f));

        /// <summary>How far ahead a floor samples on the way to a destination
        /// <paramref name="distance"/> metres away: the envelope's horizon, short of the last
        /// stretch before the destination.</summary>
        public static float FloorReach(float distance, float speed, float horizon)
        {
            float standoff = Math.Max(FloorStandoff, FloorStandoffSeconds * Math.Max(speed, 0f));
            return Math.Max(0f, Math.Min(horizon, distance - standoff));
        }

        /// <summary>How far ahead a floor samples on the way to a destination in the air, the
        /// pattern's aim point a landing aeroplane turns in to: the envelope's horizon, as far as
        /// the destination itself.</summary>
        public static float FloorReachInAir(float distance, float horizon)
            => Math.Max(0f, Math.Min(horizon, distance));

        /// <summary>
        /// Whether a floor at <paramref name="floorAngle"/> lifts an aircraft's aim, which points at
        /// <paramref name="destinationAngle"/> (both radians from level).
        /// </summary>
        public static bool Lifts(float floorAngle, float destinationAngle)
            => Finite(floorAngle) && Finite(destinationAngle) && floorAngle > destinationAngle;

        // ---------------------------------------------------------------------------------------
        // Landing approaches

        /// <summary>The glideslope the game's AI lands on: <c>RunwayUsage.GetNearestGlideslopePoint</c>
        /// rises 0.06 m per metre back from the touchdown point, 3.4 degrees.</summary>
        public const float GlideslopeGradient = 0.06f;

        /// <summary>The least the glideslope may pass over the ground before it counts as
        /// obstructed, in metres. Low on purpose: the samples meet buildings too, and a tower under
        /// the approach is not a mountain.</summary>
        public const float GlideslopeClearance = 15f;

        /// <summary>The glideslope is not tested nearer the touchdown point than this, in metres,
        /// where it is meant to be low.</summary>
        public const float GlideslopeFrom = 1500f;

        /// <summary>
        /// How an approach is sampled: every <see cref="ApproachStep"/> metres out to
        /// <see cref="ApproachReach"/>, and at each distance at every offset in
        /// <see cref="ApproachLanes"/> (metres either side of the centreline), the highest of
        /// which counts. The game's aircraft does not arrive on the centreline: it turns in at the
        /// pattern's aim point from up to four turning radii to one side
        /// (<c>AIPilotLandingState.JoinPattern</c>), and from within half a turning radius of
        /// that point flies straight at the touchdown point (<c>FinalTurn</c>), a line that closes
        /// on the centreline only at the runway. At Meiringen, landing west, the ground 150 to
        /// 300 m north of the centreline comes within 15 m of the glideslope 4.0 km out; on the
        /// centreline it does so only at 4.9 km.
        /// </summary>
        public const float ApproachStep = 50f;
        public const float ApproachReach = 15000f;
        public static readonly float[] ApproachLanes = { 0f, -150f, 150f, -300f, 300f };

        /// <summary>How far out an approach runs past the pattern's aim point, which the game puts
        /// three turning radii out along the glideslope (<c>JoinPattern</c>): the turn onto the
        /// final, which starts up to half a turning radius beyond it.</summary>
        public const float ApproachMargin = 2000f;

        /// <summary>With no end clear, another end is chosen over the game's only when its
        /// approach is clear this much farther out, in metres: less is no better.</summary>
        public const float LeastGain = 1000f;

        /// <summary>The highest of <paramref name="count"/> heights taken across an approach at
        /// one distance; negative infinity when none is usable.</summary>
        public static float Highest(float[] heights, int count)
        {
            float top = float.NegativeInfinity;
            int n = Math.Min(count, heights.Length);
            for (int i = 0; i < n; i++)
                if (Finite(heights[i]) && heights[i] > top) top = heights[i];
            return top;
        }

        /// <summary>The length of approach an aircraft with this turning radius flies down the
        /// glideslope, in metres.</summary>
        public static float ApproachLength(float turningRadius)
            => 3f * Math.Max(turningRadius, 0f) + ApproachMargin;

        /// <summary>
        /// The distance back from the touchdown point at which the glideslope first passes less
        /// than <paramref name="clearance"/> over the ground, testing only from
        /// <paramref name="from"/> out; positive infinity when it never does. Samples must be in
        /// order of distance.
        /// </summary>
        public static float FirstObstruction(float[] distance, float[] ground, int count, float touchdownY,
                                             float gradient, float clearance, float from)
        {
            int n = Math.Min(count, Math.Min(distance.Length, ground.Length));
            for (int i = 0; i < n; i++)
                if (Obstructs(distance[i], ground[i], touchdownY, gradient, clearance, from)) return distance[i];
            return float.PositiveInfinity;
        }

        /// <summary>Whether ground at <paramref name="ground"/>, <paramref name="distance"/> back
        /// from the touchdown point, stands within <paramref name="clearance"/> of the glideslope
        /// there (from <paramref name="from"/> out only).</summary>
        public static bool Obstructs(float distance, float ground, float touchdownY, float gradient, float clearance, float from)
            => distance >= from && Finite(ground) && touchdownY + gradient * distance - ground < clearance;

        /// <summary>
        /// Which runway end an aircraft lands on, given every end it may use: the game's own choice
        /// (<paramref name="chosen"/>) when its approach is clear for <paramref name="approach"/>
        /// metres, otherwise the clear end it is most nearly lined up with, otherwise the end whose
        /// obstruction is farthest out, if that is at least <see cref="LeastGain"/> farther than
        /// the game's. Angles are degrees off the approach, obstructions as from
        /// <see cref="FirstObstruction"/>.
        /// </summary>
        public static int ChooseLandingEnd(float[] angle, float[] obstruction, int count, int chosen, float approach)
        {
            int n = Math.Min(count, Math.Min(angle.Length, obstruction.Length));
            if (chosen < 0 || chosen >= n) return chosen;
            if (obstruction[chosen] >= approach) return chosen;

            int clear = -1;
            for (int i = 0; i < n; i++)
                if (obstruction[i] >= approach && (clear < 0 || angle[i] < angle[clear])) clear = i;
            if (clear >= 0) return clear;

            int farthest = chosen;
            for (int i = 0; i < n; i++)
                if (obstruction[i] > obstruction[farthest]) farthest = i;
            return obstruction[farthest] - obstruction[chosen] >= LeastGain ? farthest : chosen;
        }

        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }
}
