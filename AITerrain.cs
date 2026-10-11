using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace CustomMaps
{
    /// <summary>
    /// Whether <see cref="AITerrain"/> is on. Apart from it, with no static of a game type, so
    /// that every patch can read it even if a game update has taken away a type
    /// <see cref="AITerrain"/>'s own statics are made of: that would make
    /// <see cref="AITerrain"/> fail to load, which the map patch catches, leaving this false and
    /// every patch returning before it touches anything else.
    /// </summary>
    internal static class AITerrainSwitch
    {
        /// <summary>True while the map in play is one of ours and the setting is on.</summary>
        internal static bool Active;
    }

    /// <summary>
    /// Lets the game's AI pilots see a custom map's mountains in time to get over them.
    ///
    /// Nothing on the map is missing for them: the AI reads the ground only through line-casts
    /// against the colliders on the Statics and ExclusionZones layers, which every terrain tile of
    /// ours is on, and no heightmap, grid or map setting of any kind (checked against the
    /// decompiled game, 14 August build; <c>MapSettings</c> has no altitude field, and
    /// <c>BattlefieldGrid</c> holds units, not ground). What fails is how little of the ground they
    /// look at, which the base maps, half the height and half the steepness, never showed. See
    /// <see cref="TerrainEnvelope"/> for aeroplanes and <see cref="RotorPlan"/> for helicopters;
    /// the patches are in <c>Patches/AITerrainPatches.cs</c>.
    ///
    /// On while a custom map is loaded (<see cref="AITerrainSwitch"/>); off on the base game's maps, where every patch returns
    /// before it does anything. The AI flies on the host, so that is the only machine where any
    /// of this runs.
    /// </summary>
    internal static class AITerrain
    {
        /// <summary>
        /// The height over the game's sea level the ground is probed from. The game's own probe
        /// (<c>Autopilot.TerrainWaypoint</c>) starts 5,000 m up, 563 m over Swiss Alps' highest
        /// peak; a map with ground above that would read as sea to it everywhere. This one starts
        /// over the map's <c>terrainHeightMax</c>, which Map Forge puts at least 200 m over its peak.
        /// </summary>
        internal static float ProbeTop = DefaultProbeTop;

        const float DefaultProbeTop = 5500f;
        const float ProbeTopMargin = 500f;

        /// <summary>The ground the AI's own line-casts meet: the map's terrain, roads, bridges and
        /// buildings, its lakes' water (trigger boxes, which these casts meet as the game's do), and
        /// the shells of the ExclusionZones layer the game gives the AI to keep out of.</summary>
        static readonly int FlightMask = (int)PhysicsLayers.StaticsMask | (int)PhysicsLayers.ExclusionZonesMask;

        /// <summary>A runway's approach is tested against the Statics layer only: an airbase's own
        /// keep-out shell is no hill.</summary>
        static readonly int ApproachMask = (int)PhysicsLayers.StaticsMask;

        /// <summary>A plan is redone this often, in seconds: as often as the game redoes its own
        /// terrain-following waypoint.</summary>
        internal const float PlanInterval = 0.5f;

        /// <summary>A velocity slower than this across, in m/s, is no direction worth sampling, and
        /// one within <see cref="SameDirection"/> degrees of the direction already sampled adds
        /// nothing.</summary>
        const float LeastSampledSpeed = 20f;
        const float SameDirection = 3f;

        static readonly float[] _distance = new float[Math.Max(TerrainEnvelope.Samples, RotorPlan.Samples)];
        static readonly float[] _ground = new float[Math.Max(TerrainEnvelope.Samples, RotorPlan.Samples)];
        static readonly float[] _lanes = new float[TerrainEnvelope.ApproachLanes.Length];
        static readonly float[] _tops = new float[RotorPlan.DetourOffsets.Length];

        static readonly ConditionalWeakTable<Autopilot, Plan> _plans = new ConditionalWeakTable<Autopilot, Plan>();
        static readonly Dictionary<Airbase.Runway, float> _approachForward = new Dictionary<Airbase.Runway, float>();
        static readonly Dictionary<Airbase.Runway, float> _approachReverse = new Dictionary<Airbase.Runway, float>();
        static readonly Dictionary<string, int> _faults = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>What one aircraft's AI has planned, kept between frames.</summary>
        internal sealed class Plan
        {
            // Fixed-wing: the floor under a straight flight at a destination.
            public float FloorTime = float.NegativeInfinity;
            public float FloorAngle = float.NegativeInfinity;

            // Rotorcraft: how far its destination was across when it last aimed, and when.
            public float ToDestination = float.PositiveInfinity;
            public float ToDestinationTime = float.NegativeInfinity;

            public float Time = float.NegativeInfinity;
            public float LastAltitude;
            public float Required = float.NegativeInfinity;
            public float Allowed = float.PositiveInfinity;

            // The hold the AI asked for, the clearance it is planned with, the hold given to the
            // game in its place, and when: read by the waypoint the game works out in the same call.
            public float OwnHold;
            public float Clearance;
            public float HoldGiven;
            public float HoldTime = float.NegativeInfinity;

            public Vector3 Direction;
            public bool Slowing;
            public GlobalPosition Anchor;
            public float AnchorTime = float.NegativeInfinity;
            public GlobalPosition DetourPoint;
            public float DetourUntil = float.NegativeInfinity;
            public float NextDetourSearch = float.NegativeInfinity;
            public readonly ClimbEstimate Climb = new ClimbEstimate();
        }

        internal static Plan PlanFor(Autopilot autopilot) => _plans.GetOrCreateValue(autopilot);

        /// <summary>Called when a map is applied: on for a custom map, off for any other.</summary>
        internal static void MapApplied(MapSettings mapSettings)
        {
            AITerrainSwitch.Active = false;
            _approachForward.Clear();
            _approachReverse.Clear();
            _faults.Clear();

            LoadedMap map = Plugin.Disabled
                ? null
                : CustomMapOf(mapSettings);
            if (map == null) return;

            float heightMax = map.Manifest?.TerrainHeightMax ?? 0f;
            ProbeTop = Math.Max(DefaultProbeTop, heightMax + ProbeTopMargin);
            AITerrainSwitch.Active = true;

            string name = string.IsNullOrEmpty(map.Manifest?.DisplayName) ? map.Name() : map.Manifest.DisplayName;
            Plugin.LogInfo($"AI terrain avoidance on for {name}: AI aircraft and helicopters look ahead along their " +
                           $"whole path, with the ground probed from {ProbeTop:0} m");
        }

        /// <summary>
        /// The custom map whose prepared prefab this live map was cloned from, or null for any
        /// other map. The same test <c>CityBuilder</c> and the nuclear blast fix use: a live map is
        /// a clone, so its name starts with its prefab's.
        /// </summary>
        static LoadedMap CustomMapOf(MapSettings mapSettings)
        {
            if (mapSettings == null) return null;

            foreach (LoadedMap map in BundleLoader.Maps)
            {
                MapSettings prefab = MapFixups.PreparedPrefabFor(map);
                if (prefab != null && mapSettings.name.StartsWith(prefab.name, StringComparison.Ordinal))
                    return map;
            }
            return null;
        }

        /// <summary>How many failures one part may have on a map before it is switched off.</summary>
        const int FaultLimit = 10;

        /// <summary>
        /// Reports a failure of one part of this, the first in full, and switches that part off
        /// until the next map once it has failed <see cref="FaultLimit"/> times: an aircraft caught
        /// half destroyed costs one frame of the game's own logic, and a game update that breaks a
        /// part costs a few warnings and the part, not a warning every frame. The game's own logic
        /// carries on whenever a part fails or is off.
        /// </summary>
        internal static void Fault(string part, Exception e)
        {
            _faults.TryGetValue(part, out int count);
            _faults[part] = ++count;

            if (count == 1)
                Plugin.LogWarning($"AI terrain avoidance: {part} failed, the game's own logic stands in: {e}");
            if (count == FaultLimit)
                Plugin.LogWarning($"AI terrain avoidance: {part} has failed {count} times and is off until the next map");
        }

        internal static bool Faulted(string part) => _faults.TryGetValue(part, out int count) && count >= FaultLimit;

        // ---------------------------------------------------------------------------------------
        // Ground

        /// <summary>The height of the ground (or the sea, when that is higher or there is nothing
        /// under the point), in scene coordinates, under the scene point (x, z).</summary>
        static float GroundY(float x, float z, int mask)
        {
            float sea = Datum.LocalSeaY;
            var top = new Vector3(x, sea + ProbeTop, z);
            var bottom = new Vector3(x, sea - 1f, z);
            return Physics.Linecast(top, bottom, out RaycastHit hit, mask) ? Math.Max(hit.point.y, sea) : sea;
        }

        /// <summary>Fills <see cref="_distance"/> and <see cref="_ground"/> with
        /// <paramref name="count"/> samples spread evenly out to <paramref name="reach"/> along the
        /// level direction <paramref name="direction"/>, heights in scene coordinates less
        /// <paramref name="datum"/>.</summary>
        static void Sample(Vector3 origin, Vector3 direction, float reach, int count, float datum)
        {
            for (int i = 0; i < count; i++)
            {
                float d = TerrainEnvelope.SampleDistance(i + 1, count, reach);
                _distance[i] = d;
                _ground[i] = GroundY(origin.x + direction.x * d, origin.z + direction.z * d, FlightMask) - datum;
            }
        }

        static Vector3 Level(Vector3 v)
        {
            v.y = 0f;
            return v;
        }

        /// <summary>The level direction of <paramref name="velocity"/>, when it is fast enough and
        /// far enough from <paramref name="sampled"/> to be worth sampling as well.</summary>
        static bool OtherDirection(Vector3 velocity, Vector3 sampled, float least, out Vector3 direction)
        {
            direction = Level(velocity);
            if (direction.magnitude < least || Vector3.Angle(direction, sampled) < SameDirection) return false;
            direction.Normalize();
            return true;
        }

        // ---------------------------------------------------------------------------------------
        // Every aircraft: the terrain-following waypoint

        /// <summary>
        /// The point a terrain-following AI aircraft aims at, in place of
        /// <c>Autopilot.TerrainWaypoint</c>'s: at the same distance ahead along the same direction,
        /// at the height of the envelope over every sample out to twice that distance along that
        /// direction and along the aircraft's own path. False leaves it to the game.
        /// </summary>
        internal static bool Waypoint(Autopilot autopilot, Vector3 direction, float altitudeTarget, float lookAhead,
                                      out GlobalPosition waypoint)
        {
            waypoint = default;
            Aircraft aircraft = autopilot.aircraft;
            if (aircraft == null || aircraft.rb == null || !(lookAhead > 0f)) return false;

            direction = Level(direction);
            if (direction.sqrMagnitude < 1e-6f) return false;
            direction.Normalize();

            Vector3 position = aircraft.transform.position;
            float horizon = TerrainEnvelope.Horizon(lookAhead);
            int n = TerrainEnvelope.Samples;
            float now = Time.timeSinceLevelLoad;

            // Aeroplanes are held 30 m clear; a helicopter is let down to what it asked for, which
            // is how it comes in to land.
            bool plane = autopilot is AutopilotPlane;
            float least = plane ? TerrainEnvelope.PlaneClearance : 0f;
            float own = altitudeTarget, clearance = altitudeTarget, planAngle = float.NegativeInfinity;

            if (!plane && _plans.TryGetValue(autopilot, out Plan plan))
            {
                // A rotorcraft's envelope stops at where it is going, when that is nearer, so that
                // a ridge beyond the landing zone does not hold it high on the way in; never short
                // of the game's own point, though.
                if (plan.ToDestinationTime == now)
                    horizon = Mathf.Clamp(plan.ToDestination, lookAhead, horizon);

                // The hold the plan raised is a height over the ground below that already counts
                // the ground ahead: the game's own aim and the envelope keep the AI's own height,
                // and the plan asks for its climb as an angle, the one it assumed.
                if (plan.HoldTime == now)
                {
                    own = RotorPlan.WaypointHold(altitudeTarget, plan.OwnHold, plan.HoldGiven);
                    clearance = RotorPlan.EnvelopeClearance(own, plan.OwnHold, plan.Clearance);
                    planAngle = RotorPlan.PlanAngle(aircraft.GlobalPosition().y, plan.Required, plan.Climb.Climb,
                                                    Level(aircraft.rb.velocity).magnitude);
                }
            }

            float aboveGround = aircraft.radarAlt;
            Sample(position, direction, horizon, n, 0f);
            float envelope = TerrainEnvelope.ClimbAngle(position.y, _distance, _ground, n, clearance, least, aboveGround,
                                                        lookAhead, TerrainEnvelope.Cone);

            if (OtherDirection(aircraft.rb.velocity, direction, LeastSampledSpeed, out Vector3 path))
            {
                Sample(position, path, horizon, n, 0f);
                envelope = Math.Max(envelope, TerrainEnvelope.ClimbAngle(position.y, _distance, _ground, n, clearance, least,
                                                                         aboveGround, lookAhead, TerrainEnvelope.Cone));
            }

            Vector3 point = position + direction * lookAhead;
            float groundThere = GroundY(point.x, point.z, FlightMask);
            float follow = TerrainEnvelope.FollowAngle(position.y, lookAhead, groundThere, own);
            float angle = TerrainEnvelope.WaypointAngle(Math.Max(follow, planAngle), envelope);

            point.y = position.y + lookAhead * Mathf.Tan(angle);
            waypoint = point.ToGlobalPosition();
            return true;
        }

        // ---------------------------------------------------------------------------------------
        // Aeroplanes: a floor under straight flight at a destination

        /// <summary>
        /// Lifts the destination an AI aeroplane flies straight at (<c>followTerrain</c> off), when
        /// the ground on the way would otherwise be in its path: to the same distance across, at the
        /// angle that clears it. In combat; and, when <paramref name="landing"/> is allowed, in the
        /// turn in to land, which flies straight at the pattern's aim point from up to four turning
        /// radii to one side of the runway (<c>AIPilotLandingState.FinalTurn</c>).
        /// </summary>
        internal static void Floor(AutopilotPlane autopilot, ref GlobalPosition destination, bool ignoreCollisions, bool landing)
        {
            Aircraft aircraft = autopilot.aircraft;
            if (aircraft == null || aircraft.rb == null || aircraft.radarAlt <= 5f) return;

            PilotBaseState state = FirstPilotState(aircraft);
            bool turningIn = landing && !ignoreCollisions && state is AIPilotLandingState landingState && TurningIn(landingState);
            if (!turningIn && !(state is AIPilotCombatModes)) return;

            GlobalPosition here = aircraft.GlobalPosition();
            Vector3 to = destination - here;
            Vector3 across = Level(to);
            float distance = across.magnitude;
            float speed = aircraft.speed;
            if (turningIn ? !(distance > 1f) : !TerrainEnvelope.FloorApplies(distance, speed)) return;
            across /= distance;

            Plan plan = PlanFor(autopilot);
            float now = Time.timeSinceLevelLoad;
            if (now - plan.FloorTime >= PlanInterval || now < plan.FloorTime)
            {
                plan.FloorTime = now;
                plan.FloorAngle = FloorAngle(aircraft, across, distance, speed, turningIn);
            }

            float destinationAngle = Mathf.Atan2(to.y, distance);
            if (!TerrainEnvelope.Lifts(plan.FloorAngle, destinationAngle)) return;

            float angle = Math.Min(plan.FloorAngle, TerrainEnvelope.MaxAngle);
            destination = here + across * distance + Vector3.up * (distance * Mathf.Tan(angle));
        }

        /// <summary>
        /// Whether a landing aeroplane is still turning in towards the pattern's aim point, which
        /// is in the air on the glideslope: until it reaches it and makes for the runway down the
        /// glideslope, which the choice of runway end looks after. Its own method, so that the
        /// field it reads, private in the game, costs only this part if an update takes it away.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool TurningIn(AIPilotLandingState state) => !state.reachedFinal;

        static float FloorAngle(Aircraft aircraft, Vector3 across, float distance, float speed, bool turningIn)
        {
            AircraftParameters parameters = aircraft.GetAircraftParameters();
            float gLimit = parameters != null && parameters.aircraftGLimit > 0f ? parameters.aircraftGLimit : 9f;

            // The game's own look-ahead for this aircraft (TerrainWarningSystem.GetFollowTerrainWaypoint).
            float lookAhead = Math.Max(speed, 100f) * 6f * (9f / gLimit);
            float horizon = TerrainEnvelope.Horizon(lookAhead);
            float reach = turningIn
                ? TerrainEnvelope.FloorReachInAir(distance, horizon)
                : TerrainEnvelope.FloorReach(distance, speed, horizon);
            if (!(reach > 0f)) return float.NegativeInfinity;

            Vector3 position = aircraft.transform.position;
            int n = TerrainEnvelope.Samples;
            Sample(position, across, reach, n, 0f);
            float angle = TerrainEnvelope.ClimbAngle(position.y, _distance, _ground, n, TerrainEnvelope.PlaneClearance, TerrainEnvelope.Cone);

            if (OtherDirection(aircraft.rb.velocity, across, LeastSampledSpeed, out Vector3 path))
            {
                Sample(position, path, reach, n, 0f);
                angle = Math.Max(angle, TerrainEnvelope.ClimbAngle(position.y, _distance, _ground, n, TerrainEnvelope.PlaneClearance, TerrainEnvelope.Cone));
            }
            return angle;
        }

        static PilotBaseState FirstPilotState(Aircraft aircraft)
        {
            Pilot[] pilots = aircraft.pilots;
            return pilots != null && pilots.Length > 0 && pilots[0] != null ? pilots[0].currentState : null;
        }

        // ---------------------------------------------------------------------------------------
        // Aeroplanes: which end of a runway to land on

        /// <summary>
        /// The runway end an AI aeroplane is sent to, in place of the game's choice when the game's
        /// approach runs into the ground within the aircraft's pattern. The game picks the end the
        /// aircraft is most nearly lined up with and nothing else (<c>Airbase.RequestLanding</c>),
        /// then flies its last miles down a fixed 3.4-degree glideslope with collisions ignored
        /// (<c>AIPilotLandingState.StabilizedApproach</c>). At Meiringen, landing west, the ground
        /// beside that slope stands within 15 m of it from 4 km out.
        ///
        /// Only for the AI's own landing: the player's HUD asks the same question
        /// (<c>AirbaseOverlay.UpdateNearestAirbase</c>) to draw its glideslope and clear the player
        /// to land, and keeps the game's answer.
        /// </summary>
        internal static Airbase.Runway.RunwayUsage LandingEnd(Airbase airbase, Aircraft aircraft, RunwayQuery query,
                                                              Airbase.Runway.RunwayUsage chosen)
        {
            if (airbase == null || airbase.AttachedAirbase || airbase.runways == null || chosen.Runway == null) return chosen;

            PilotBaseState state = FirstPilotState(aircraft);
            if (!(state is AIPilotLandingState) && !(state is AIPilotShortLandingState)) return chosen;

            AircraftParameters parameters = aircraft.GetAircraftParameters();
            float approach = TerrainEnvelope.ApproachLength(parameters != null ? parameters.turningRadius : 0f);

            // The game's own choice is kept, with nothing else looked at, when its approach is clear.
            if (Obstruction(chosen) >= approach) return chosen;

            var ends = new List<Airbase.Runway.RunwayUsage>();
            foreach (Airbase.Runway runway in airbase.runways)
            {
                if (runway == null || !runway.Landing || !runway.IsSuitable(query)) continue;
                ends.Add(new Airbase.Runway.RunwayUsage(runway, false));
                if (runway.Reversable) ends.Add(new Airbase.Runway.RunwayUsage(runway, true));
            }

            int chosenIndex = ends.FindIndex(e => e.Runway == chosen.Runway && e.Reverse == chosen.Reverse);
            if (chosenIndex < 0) return chosen;

            var angles = new float[ends.Count];
            var obstructions = new float[ends.Count];
            Vector3 from = aircraft.transform.position;
            for (int i = 0; i < ends.Count; i++)
            {
                angles[i] = ApproachAngle(ends[i], from);
                obstructions[i] = Obstruction(ends[i]);
            }

            int pick = TerrainEnvelope.ChooseLandingEnd(angles, obstructions, ends.Count, chosenIndex, approach);
            if (pick == chosenIndex || pick < 0) return chosen;

            Plugin.LogDebug($"{aircraft.unitName} lands at {airbase.name} on {ends[pick].GetName()}, not {chosen.GetName()}: " +
                            $"that glideslope meets the ground {obstructions[chosenIndex]:0} m out, inside its {approach:0} m approach");
            return ends[pick];
        }

        /// <summary>How far off an end's approach the aircraft is, in degrees, as the game measures
        /// it in <c>Runway.GetAngle</c>: from the aircraft to the threshold, against the direction
        /// of landing.</summary>
        static float ApproachAngle(Airbase.Runway.RunwayUsage end, Vector3 from)
        {
            Transform threshold = end.Reverse ? end.Runway.End : end.Runway.Start;
            Vector3 toThreshold = Level(threshold.position - from);
            Vector3 landing = Level(end.Runway.GetDirection(end.Reverse));
            return Vector3.Angle(toThreshold, landing);
        }

        /// <summary>The distance back from the touchdown point at which an end's glideslope first
        /// passes too low over the ground anywhere across its approach
        /// (<see cref="TerrainEnvelope.ApproachLanes"/>); worked out once per end per map, and
        /// only as far as that.</summary>
        static float Obstruction(Airbase.Runway.RunwayUsage end)
        {
            Dictionary<Airbase.Runway, float> cache = end.Reverse ? _approachReverse : _approachForward;
            if (cache.TryGetValue(end.Runway, out float known)) return known;

            float[] lanes = TerrainEnvelope.ApproachLanes;
            Vector3 touchdown = end.GetTouchdownPoint().ToLocalPosition();
            Vector3 back = -Level(end.Runway.GetDirection(end.Reverse)).normalized;
            var side = new Vector3(back.z, 0f, -back.x);

            float obstruction = float.PositiveInfinity;
            int count = (int)(TerrainEnvelope.ApproachReach / TerrainEnvelope.ApproachStep);
            for (int i = 0; i < count && float.IsPositiveInfinity(obstruction); i++)
            {
                float d = TerrainEnvelope.ApproachStep * (i + 1);
                if (d < TerrainEnvelope.GlideslopeFrom) continue;

                Vector3 centre = touchdown + back * d;
                for (int k = 0; k < lanes.Length; k++)
                    _lanes[k] = GroundY(centre.x + side.x * lanes[k], centre.z + side.z * lanes[k], ApproachMask);

                if (TerrainEnvelope.Obstructs(d, TerrainEnvelope.Highest(_lanes, lanes.Length), touchdown.y,
                        TerrainEnvelope.GlideslopeGradient, TerrainEnvelope.GlideslopeClearance, TerrainEnvelope.GlideslopeFrom))
                    obstruction = d;
            }
            cache[end.Runway] = obstruction;

            Plugin.LogDebug($"{end.Runway.airbase?.name} {end.GetName()}: glideslope " +
                            (float.IsPositiveInfinity(obstruction)
                                ? $"clear to {TerrainEnvelope.ApproachReach:0} m"
                                : $"meets the ground {obstruction:0} m out"));
            return obstruction;
        }

        // ---------------------------------------------------------------------------------------
        // Helicopters and tilt-rotors

        /// <summary>Notes how far a rotorcraft's destination is across, for the waypoint the game
        /// works out in the same call (<see cref="Waypoint"/>).</summary>
        internal static void NoteDestination(Autopilot autopilot, GlobalPosition destination)
        {
            Aircraft aircraft = autopilot.aircraft;
            if (aircraft == null) return;

            Plan plan = PlanFor(autopilot);
            plan.ToDestination = Level(destination - aircraft.GlobalPosition()).magnitude;
            plan.ToDestinationTime = Time.timeSinceLevelLoad;
        }

        /// <summary>The clearance a rotorcraft aiming its nose at a target is planned with
        /// (<see cref="RotorPlan.BoresightClearance"/>), from its own least height.</summary>
        internal static float BoresightClearance(Autopilot autopilot, float hold)
        {
            AircraftParameters parameters = autopilot.aircraft != null ? autopilot.aircraft.GetAircraftParameters() : null;
            return RotorPlan.BoresightClearance(hold, parameters != null ? parameters.minimumRadarAlt : float.NaN);
        }

        /// <summary>
        /// Raises the height a rotorcraft holds, early enough that the climb it has gets it over
        /// what is ahead with <paramref name="clearance"/> to spare; and, when
        /// <paramref name="steer"/>, slows it where even that is not enough at its speed, and turns
        /// it aside where the ground is higher than it can climb.
        /// </summary>
        internal static void Rotor(Autopilot autopilot, ref GlobalPosition destination, ref float altitudeHold,
                                   float clearance, bool steer)
        {
            Aircraft aircraft = autopilot.aircraft;
            if (aircraft == null || aircraft.rb == null) return;

            GlobalPosition here = aircraft.GlobalPosition();
            Vector3 toDestination = Level(destination - here);
            float distance = toDestination.magnitude;

            Plan plan = PlanFor(autopilot);
            float now = Time.timeSinceLevelLoad;
            plan.OwnHold = altitudeHold;
            plan.Clearance = clearance;
            plan.HoldTime = now;

            // Close in, the game's own approach and hover take over and must be left alone.
            if (distance < RotorPlan.LeastPlanDistance)
            {
                plan.HoldGiven = altitudeHold;
                plan.Required = float.NegativeInfinity;
                plan.Allowed = float.PositiveInfinity;
                plan.Slowing = false;
                plan.AnchorTime = float.NegativeInfinity;
                plan.DetourUntil = float.NegativeInfinity;
                return;
            }

            // The replan reads the hold given last time, which is what the aircraft was asked to climb to.
            if (now - plan.Time >= PlanInterval || now < plan.Time)
                Replan(aircraft, plan, toDestination, distance, clearance, steer, now);

            float hold = RotorPlan.Hold(plan.Required, here.y, aircraft.radarAlt, altitudeHold);
            plan.HoldGiven = hold;
            altitudeHold = hold;

            if (!steer) return;

            if (now < plan.DetourUntil) destination = plan.DetourPoint;

            plan.Slowing = RotorPlan.Slowing(plan.Slowing, Level(aircraft.rb.velocity).magnitude, plan.Allowed);
            if (!plan.Slowing)
            {
                plan.AnchorTime = float.NegativeInfinity;
                return;
            }

            // A fixed point, so that the game's tilt controller sees the aircraft closing on it and
            // brakes; moved on every couple of seconds, or once the aircraft is level with it, except
            // a hover point, which stays where it was put.
            bool first = float.IsNegativeInfinity(plan.AnchorTime) || now < plan.AnchorTime;
            bool due = now - plan.AnchorTime > 2f
                || Vector3.Dot(Level(plan.Anchor - here), plan.Direction) < RotorPlan.LeastAnchor * 0.5f;
            if (first || (due && plan.Allowed >= RotorPlan.HoverSpeed))
            {
                plan.Anchor = here + plan.Direction * RotorPlan.AnchorDistance(plan.Allowed) + Vector3.up * RotorPlan.AnchorLift;
                plan.AnchorTime = now;
            }
            destination = plan.Anchor;
        }

        static void Replan(Aircraft aircraft, Plan plan, Vector3 toDestination, float distance, float hold, bool steer, float now)
        {
            GlobalPosition here = aircraft.GlobalPosition();
            Vector3 velocity = aircraft.rb.velocity;
            float speed = Level(velocity).magnitude;

            // What the aircraft has climbed since the last look, against what it was asked for.
            float elapsed = now - plan.Time;
            if (elapsed > 0f && elapsed < 2f)
            {
                AircraftParameters parameters = aircraft.GetAircraftParameters();
                float maxSpeed = parameters != null && parameters.maxSpeed > 0f ? parameters.maxSpeed : 100f;
                ControlInputs inputs = aircraft.GetInputs();
                plan.Climb.Observe(elapsed, inputs != null ? inputs.throttle : 0f, (here.y - plan.LastAltitude) / elapsed, here.y,
                                   collectiveHolds: speed < 0.4f * maxSpeed, climbAsked: plan.HoldGiven > aircraft.radarAlt + 10f);
            }
            plan.Time = now;
            plan.LastAltitude = here.y;

            // The direction the game itself looks along (AutopilotHelo.AutoAim): the way the
            // aircraft is going, turned at most 0.8 rad towards where it is going to.
            Vector3 current = Level(velocity + aircraft.transform.forward * 20f);
            Vector3 direction = Level(Vector3.RotateTowards(current, toDestination, 0.8f, 0f));
            if (direction.sqrMagnitude < 1e-6f) direction = toDestination;
            direction.Normalize();
            if (now < plan.DetourUntil) direction = Level(plan.DetourPoint - here).normalized;
            plan.Direction = direction;

            Vector3 position = aircraft.transform.position;
            float datum = Datum.LocalSeaY;
            float lookAhead = RotorPlan.LookAhead(speed, distance);
            int n = RotorPlan.Samples;
            float climb = plan.Climb.Climb, ceiling = plan.Climb.Ceiling;

            Sample(position, direction, lookAhead, n, datum);
            float required = RotorPlan.RequiredAltitude(_distance, _ground, n, hold, speed, climb);
            float allowed = RotorPlan.AllowedSpeed(here.y, _distance, _ground, n, hold, climb, ceiling);
            float top = RotorPlan.CorridorTop(_ground, n, hold);

            if (OtherDirection(velocity, direction, 10f, out Vector3 path))
            {
                Sample(position, path, lookAhead, n, datum);
                required = Math.Max(required, RotorPlan.RequiredAltitude(_distance, _ground, n, hold, speed, climb));
                allowed = Math.Min(allowed, RotorPlan.AllowedSpeed(here.y, _distance, _ground, n, hold, climb, ceiling));
            }

            plan.Required = required;
            plan.Allowed = allowed;

            if (steer && top > ceiling && now >= plan.DetourUntil && now >= plan.NextDetourSearch)
                Detour(aircraft, plan, position, direction, lookAhead, hold, top, ceiling, now);
        }

        const float DetourSeconds = 12f;
        const float DetourDistance = 3000f;

        /// <summary>How long a helicopter that found no way round waits before it looks again, in
        /// seconds: hovering in front of the ridge, it sees the same ground each time.</summary>
        const float DetourRetrySeconds = 3f;

        /// <summary>Looks either side for lower ground when straight on is over the aircraft's
        /// ceiling, and if there is some, steers along it for a while.</summary>
        static void Detour(Aircraft aircraft, Plan plan, Vector3 position, Vector3 direction, float lookAhead, float hold,
                           float top, float ceiling, float now)
        {
            float[] offsets = RotorPlan.DetourOffsets;
            float datum = Datum.LocalSeaY;
            int n = RotorPlan.Samples;
            for (int i = 0; i < offsets.Length; i++)
            {
                Vector3 heading = Quaternion.AngleAxis(offsets[i], Vector3.up) * direction;
                Sample(position, heading, lookAhead, n, datum);
                _tops[i] = RotorPlan.CorridorTop(_ground, n, hold);
            }

            int pick = RotorPlan.ChooseDetour(offsets, _tops, offsets.Length, top, ceiling);
            if (pick < 0)
            {
                plan.NextDetourSearch = now + DetourRetrySeconds;
                return;
            }

            Vector3 way = Quaternion.AngleAxis(offsets[pick], Vector3.up) * direction;
            plan.DetourPoint = aircraft.GlobalPosition() + way * DetourDistance;
            plan.DetourUntil = now + DetourSeconds;
            Plugin.LogDebug($"{aircraft.unitName}: ground ahead is {top:0} m, over the {ceiling:0} m it can climb to; " +
                            $"turning {offsets[pick]:0} degrees, where it is {_tops[pick]:0} m");
        }

        // ---------------------------------------------------------------------------------------
        // Helicopters: a pad too high to climb off

        /// <summary>Each take-off state's watch (<see cref="TakeoffWatch"/>).</summary>
        static readonly ConditionalWeakTable<AIHeloTakeoffState, TakeoffWatch> _takeoffs =
            new ConditionalWeakTable<AIHeloTakeoffState, TakeoffWatch>();

        /// <summary>
        /// True when a helicopter taking off is to fly on rather than wait to climb to the 20 m the
        /// game waits for (<see cref="TakeoffWatch"/>): a loaded Ibis on a pad at 2,345 m in the
        /// user's recording could not, and all five were lost to the game's 30-second timeout.
        /// </summary>
        internal static bool TakeoffStuck(AIHeloTakeoffState state, Pilot pilot)
        {
            Aircraft aircraft = pilot != null ? pilot.aircraft : null;
            if (aircraft == null || aircraft.rb == null || pilot.AIHeloCombatState == null) return false;

            RotorShaft rotor = state.rotorShaft;
            ControlInputs inputs = aircraft.GetInputs();
            bool rotorReady = rotor != null && rotor.GetRPM() >= state.maxRPM * 0.9f;

            TakeoffWatch.Verdict verdict = _takeoffs.GetOrCreateValue(state).Update(Time.timeSinceLevelLoad, rotorReady,
                aircraft.radarAlt, inputs != null ? inputs.throttle : 0f, aircraft.rb.velocity.y);
            if (verdict == TakeoffWatch.Verdict.Wait) return false;

            string why = verdict == TakeoffWatch.Verdict.LastChance ? "still on or near its pad after 27 s"
                : verdict == TakeoffWatch.Verdict.Stalled ? "no longer climbing at full collective"
                : "hanging at full collective";
            Plugin.LogDebug($"{aircraft.unitName} cannot climb off its pad ({why}, {aircraft.radarAlt:0.0} m up, " +
                            $"{aircraft.GlobalPosition().y:0} m above sea): flying on instead of waiting for the game to abandon it");
            return true;
        }
    }
}
