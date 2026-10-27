using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using NuclearOption.MissionEditorScripts;
using NuclearOption.SavedMission;
using RoadPathfinding;
using UnityEngine;

namespace CustomMaps
{
    /// <summary>
    /// Lets a mission edit a custom map's airbase roads, its taxi network, in the game's mission editor,
    /// and has the mission's roads win over the map's, as <see cref="MissionAirbases"/> does for the
    /// flag and the capture range.
    ///
    /// The game keeps an airbase's roads in two places. The map's are the airbase's own serialised
    /// <c>taxiNetwork</c>, which the plugin builds (<see cref="AirbaseBuilder"/>). A mission's are its
    /// <c>SavedAirbase.roads</c>, which the game reads only for an airbase the mission made
    /// (<c>Airbase.SetupCustomAirbase</c> sets <c>taxiNetwork = saved.roads</c>); for an airbase built
    /// into the map, which is what the plugin's are to the game, it never does, and its mission editor
    /// shows such an airbase's roads read-only (<c>RoadEditor.SelectNetwork</c>: editable only when
    /// <c>IsCustom</c>). And the override it makes of a built-in airbase for any change at all starts
    /// with no roads (<c>SavedAirbase.CreateOverride</c> copies everything but them), and is saved so.
    ///
    /// So, for these airbases only:
    /// <list type="bullet">
    /// <item>Linking an airbase to a mission (loading it in the editor, starting it in single player,
    /// and on the host and every client in multiplayer, since <c>Mission.SetupAirbase</c> runs on each)
    /// gives it the mission's roads when the mission has any and the map's otherwise; letting go of the
    /// mission ("Remove overrides", or the next mission) gives it the map's back. The runways' exits go
    /// with the roads (<see cref="MissionTaxiRoads.Exits"/>): the landing AI turns off where the roads
    /// meet the runway.</item>
    /// <item>The mission editor's Roads tool shows the airbase's roads editable, on a copy of the map's
    /// while the mission has none of its own. The first edit makes the airbase's override, as a change
    /// of faction in the airbase panel does, and hands it the copy.</item>
    /// <item>A mission is saved with its roads only when they differ from the map's
    /// (<see cref="MissionTaxiRoads.Same"/>); otherwise with none, as every override has always been
    /// saved. So a mission that never edited them keeps following the map, and a network changed later
    /// in Map Forge reaches it, as the flag does.</item>
    /// </list>
    /// The roads are the game's own field of the game's own save format, so a mission saved this way
    /// loads in the game without the plugin (which only ignores them, as it does for any built-in
    /// airbase), and the host sends them to every client with the rest of the mission.
    /// </summary>
    internal static class MissionAirbaseRoads
    {
        /// <summary>What the map gave an airbase, kept the first time the airbase is linked to a
        /// mission, before anything here changes it: its network and each runway's exits.</summary>
        sealed class MapRoads
        {
            public RoadNetwork Network;
            public Transform[][] Exits;

            /// <summary>The exit transforms made for a mission's roads, to take away again.</summary>
            public readonly List<GameObject> Made = new List<GameObject>();
        }

        static readonly ConditionalWeakTable<Airbase, MapRoads> Maps = new ConditionalWeakTable<Airbase, MapRoads>();

        static readonly FieldInfo TaxiNetwork =
            typeof(Airbase).GetField(AirbaseBuilder.TaxiNetworkField, BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>The airbase whose roads the mission editor's Roads tool shows, if one of ours.</summary>
        static Airbase _shown;

        static MapRoads Map(Airbase airbase)
            => Maps.GetValue(airbase, a =>
            {
                var map = new MapRoads { Network = a.GetTaxiNetwork() ?? new RoadNetwork() };
                Airbase.Runway[] runways = a.runways ?? new Airbase.Runway[0];
                map.Exits = new Transform[runways.Length][];
                for (int i = 0; i < runways.Length; i++) map.Exits[i] = runways[i]?.exitPoints;
                return map;
            });

        static void SetNetwork(Airbase airbase, RoadNetwork network)
        {
            if (TaxiNetwork == null) throw new MissingFieldException(nameof(Airbase), AirbaseBuilder.TaxiNetworkField);
            TaxiNetwork.SetValue(airbase, network);
        }

        static bool HasRoads(RoadNetwork network) => network?.roads != null && network.roads.Count > 0;

        // ------------------------------------------------------------------ linking

        /// <summary>After the game has linked an airbase to a mission's override, or to the map's own
        /// settings: the mission's roads when it has any, the map's otherwise.</summary>
        public static void AfterLink(Airbase airbase, SavedAirbase saved)
        {
            MapRoads map = Map(airbase);
            bool mission = saved != null && saved != airbase.airbaseSettings && HasRoads(saved.roads);

            if (mission)
            {
                // The network the Roads tool is editing, linked by its first edit (BeforeEdit), is left
                // as the tool has it; what follows is for a mission's roads coming into use.
                bool editing = airbase.GetTaxiNetwork() == saved.roads;

                // A road of fewer than two points is none the game can make nodes of: with none it
                // throws (Road.GenerateNodes reads its first point), with one it is a road from a node
                // to itself. The editor never saves one; a mission written by hand can.
                if (!editing)
                {
                    int broken = saved.roads.roads.RemoveAll(road => road?.points == null || road.points.Count < 2);
                    if (broken > 0)
                        Plugin.LogWarning($"airbase '{saved.UniqueName}': {broken} of the mission's taxi roads have fewer than " +
                                          "two points and are left out");

                    if (!HasRoads(saved.roads))
                    {
                        Use(airbase, map, map.Network);
                        return;
                    }
                }

                // Lengths and boxes as the game's own editor keeps them; a mission written by hand, or
                // by anything else, may carry neither, and Dijkstra weighs every road by its length.
                foreach (Road road in saved.roads.roads)
                {
                    if (road?.points == null) continue;
                    road.UpdateBB();
                    road.CalcLength();
                }

                // Never left half in use: whatever goes wrong, the map's roads stand, as the log says.
                try { Use(airbase, map, saved.roads); }
                catch
                {
                    Use(airbase, map, map.Network);
                    throw;
                }

                Plugin.LogDebug($"airbase '{saved.UniqueName}': taxi network from the mission, {saved.roads.roads.Count} " +
                                $"road(s), {saved.roads.nodes.Count} junction(s) and end(s)" + Away(airbase, saved.roads));

                int pieces = editing ? 0 : Pieces(saved.roads);
                if (pieces > 1)
                    Plugin.LogWarning($"airbase '{saved.UniqueName}': the mission's taxi roads are {pieces} separate networks. An " +
                                      "AI aircraft on one finds no way to the others, and rolls on unsteered until it hits " +
                                      "something; in the mission editor's Roads tool, join each road's end to a blue node of " +
                                      "the rest, or Remove overrides to have the map's");
            }
            else Use(airbase, map, map.Network);
        }

        /// <summary>How many separate pieces a network in use makes, by the junctions the game made of
        /// it (<see cref="MissionTaxiRoads.Pieces"/>).</summary>
        static int Pieces(RoadNetwork network)
        {
            var roads = new List<(int From, int To)>(network.roads.Count);
            foreach (Road road in network.roads)
                if (road?.startNode != null && road.endNode != null) roads.Add((road.startNode.id, road.endNode.id));
            return MissionTaxiRoads.Pieces(network.nodes.Count, roads);
        }

        /// <summary>After the game has let go of a mission's override: the map's roads again.</summary>
        public static void AfterUnlink(Airbase airbase)
        {
            MapRoads map = Map(airbase);
            Use(airbase, map, map.Network);
        }

        /// <summary>
        /// Puts a network in use: the airbase's <c>taxiNetwork</c>, with its junctions made, as
        /// <c>Airbase.OnStartServer</c> makes them (which has run by the time any mission is linked),
        /// and the runways' exits that lead onto it: the map's own for the map's network, made from the
        /// roads for any other.
        ///
        /// The junctions are made only for a network just put in use, or one that has none: making
        /// them again replaces every <c>Node</c>, and the mission editor's Roads tool holds the ones it
        /// drew. The first edit of a map's roads links the airbase's new override with the very network
        /// the tool is editing (<see cref="BeforeEdit"/>), in the middle of a drag; with its nodes made
        /// anew, the tool moved a node no road ends at any more, and the junction dragged snapped back
        /// when it was let go (reviewed 2026-10-03).
        /// </summary>
        static void Use(Airbase airbase, MapRoads map, RoadNetwork network)
        {
            bool swapped = airbase.GetTaxiNetwork() != network;
            if (swapped) SetNetwork(airbase, network);
            if (swapped || network.nodes == null || network.nodes.Count == 0) network.RegenerateNetwork();

            foreach (GameObject made in map.Made)
                if (made != null) UnityEngine.Object.Destroy(made);
            map.Made.Clear();

            Airbase.Runway[] runways = airbase.runways ?? new Airbase.Runway[0];
            int[] takeoffOf = TakeoffParents(runways);
            if (network == map.Network)
            {
                for (int i = 0; i < runways.Length && i < map.Exits.Length; i++)
                    if (runways[i] != null && map.Exits[i] != null) runways[i].exitPoints = map.Exits[i];
                UseTakeoffs(airbase, runways, takeoffOf, null);
                return;
            }

            var roads = new List<(float X, float Z)[]>(network.roads.Count);
            foreach (Road road in network.roads)
            {
                if (road?.points == null || road.points.Count < 2) continue;
                var points = new (float X, float Z)[road.points.Count];
                for (int p = 0; p < points.Length; p++) points[p] = (road.points[p].x, road.points[p].z);
                roads.Add(points);
            }

            for (int i = 0; i < runways.Length; i++)
            {
                Airbase.Runway runway = runways[i];
                if (runway?.Start == null || runway.End == null) continue;

                // A take-off start takes no landings, so it has no exits to make (AirbaseBuilder).
                if (takeoffOf[i] >= 0) continue;

                GlobalPosition start = runway.Start.GlobalPosition(), end = runway.End.GlobalPosition();
                List<PlannedExit> exits = MissionTaxiRoads.Exits(roads, (start.x, start.z), (end.x, end.z), runway.GetWidth());

                var points = new Transform[exits.Count];
                for (int e = 0; e < exits.Count; e++)
                {
                    PlannedExit exit = exits[e];
                    var point = new GameObject($"Runway {i + 1} mission exit {e + 1} " + (exit.Forward ? "rolling forward" : "rolling back"));
                    point.transform.SetParent(airbase.transform, worldPositionStays: true);
                    point.transform.position = new GlobalPosition(exit.X, start.y, exit.Z).ToLocalPosition();
                    point.transform.rotation = Quaternion.LookRotation(new Vector3(exit.FacingX, 0f, exit.FacingZ), Vector3.up);
                    points[e] = point.transform;
                    map.Made.Add(point);
                }

                runway.exitPoints = points;
            }

            UseTakeoffs(airbase, runways, takeoffOf, roads);
        }

        /// <summary>For each of an airbase's runways, the runway it is a take-off start of, or -1
        /// (<see cref="TaxiPlans.TakeoffParents"/>), by where its thresholds lie.</summary>
        static int[] TakeoffParents(Airbase.Runway[] runways)
        {
            var lines = new List<RunwayLine>(runways.Length);
            foreach (Airbase.Runway runway in runways)
            {
                if (runway?.Start == null || runway.End == null)
                {
                    lines.Add(new RunwayLine { StartX = float.NaN, StartZ = float.NaN, EndX = float.NaN, EndZ = float.NaN, Landing = true });
                    continue;
                }

                GlobalPosition start = runway.Start.GlobalPosition(), end = runway.End.GlobalPosition();
                lines.Add(new RunwayLine { StartX = start.x, StartZ = start.z, EndX = end.x, EndZ = end.z, Landing = runway.Landing });
            }

            return TaxiPlans.TakeoffParents(lines);
        }

        /// <summary>
        /// The take-off starts the map placed (<see cref="AirbaseBuilder"/>), for the network in use: with
        /// the map's own, every one takes take-offs, as built; with a mission's (<paramref name="roads"/>),
        /// only those the mission's roads lead to (<see cref="MissionTaxiRoads.Reaches"/>). One they do not
        /// would have the game route an aircraft to the mission's junction nearest it and drive it straight
        /// on, to line up where the map's lanes meet the runway; switched off, that aircraft goes elsewhere.
        /// A mission that draws its own lane to where the map's met keeps the start.
        ///
        /// A runway with starts takes off only from them (<see cref="TaxiPlans.TakeoffFlags"/>): its own ends
        /// stay switched off while any of its starts is in use, and come back on when none is, so a runway
        /// whose starts the mission's roads all miss takes off from its ends, as on any airbase, and never
        /// loses all its take-offs; with the map's roads back, it is starts only again.
        /// </summary>
        static void UseTakeoffs(Airbase airbase, Airbase.Runway[] runways, int[] takeoffOf, List<(float X, float Z)[]> roads)
        {
            int off = 0, starts = 0;
            var inUse = new bool[runways.Length];
            for (int i = 0; i < runways.Length && i < takeoffOf.Length; i++)
            {
                if (takeoffOf[i] < 0) continue;
                starts++;

                GlobalPosition at = runways[i].Start.GlobalPosition();
                inUse[i] = roads == null || MissionTaxiRoads.Reaches(roads, (at.x, at.z));
                if (!inUse[i]) off++;
            }

            if (starts == 0) return;

            List<int> ends = AirbaseBuilder.UseTakeoffStarts(runways, takeoffOf, inUse);

            if (off > 0)
                Plugin.LogDebug($"airbase '{airbase.name}': {off} of its {starts} take-off start(s) switched off while the " +
                                "mission's taxi roads are in use, which lead to none of them" +
                                (ends.Count > 0
                                    ? $"; runway(s) {string.Join(", ", ends.ConvertAll(i => (i + 1).ToString()))} take off from their own ends there"
                                    : "; the runways they lie on take off from their other starts"));
        }

        /// <summary>A warning for the log when a mission's roads lie far from the airbase: made for a
        /// field the map has since moved, they would lead its AI across the country.</summary>
        static string Away(Airbase airbase, RoadNetwork network)
        {
            if (airbase.center == null) return "";
            GlobalPosition centre = airbase.center.GlobalPosition();

            float nearest = float.MaxValue;
            foreach (Road road in network.roads)
                if (road?.points != null)
                    foreach (GlobalPosition point in road.points)
                    {
                        float dx = point.x - centre.x, dz = point.z - centre.z;
                        nearest = Mathf.Min(nearest, Mathf.Sqrt(dx * dx + dz * dz));
                    }

            float range = Mathf.Max(airbase.SavedAirbase?.CaptureRange ?? 0f, 3000f);
            return nearest > range
                ? $"; WARN its nearest point is {nearest.ToString("F0", CultureInfo.InvariantCulture)} m from the airbase: " +
                  "made for where the field was before the map moved it? Remove overrides gives it the map's roads back"
                : "";
        }

        // ------------------------------------------------------------------ the mission editor

        /// <summary>
        /// After the Roads tool has selected a network: for one of ours, the airbase's roads made
        /// editable, as the tool makes a mission-made airbase's. While the mission has no roads of its
        /// own the tool is given a copy of the map's, so nothing done in it touches the map's own
        /// network, which the plugin goes back to whenever the mission lets go.
        /// </summary>
        public static void Shown(RoadEditor editor, string networkName)
        {
            _shown = null;
            if (editor == null || networkName == null) return;
            if (!FactionRegistry.airbaseLookup.TryGetValue(networkName, out Airbase airbase) || !MissionAirbases.IsOurs(airbase)) return;

            MapRoads map = Map(airbase);
            RoadNetwork network = airbase.GetTaxiNetwork();
            if (network == null || network == map.Network)
            {
                network = Copy(map.Network);
                SetNetwork(airbase, network);
                network.RegenerateNetwork();
            }

            editor.roadNetwork = network;
            editor.placeRoadButton.interactable = true;
            editor.VisualizeNetworks(true);
            _shown = airbase;
        }

        /// <summary>
        /// Before the Roads tool changes a network: for one of ours, the airbase's override made if the
        /// mission has none yet, as the airbase panel makes it for any change, and given the roads being
        /// edited. Never the map's own network, which the tool is never handed (<see cref="Shown"/>).
        /// </summary>
        public static void BeforeEdit(RoadEditor editor)
        {
            Airbase airbase = _shown;
            if (editor == null || airbase == null || !MissionAirbases.IsOurs(airbase)) return;
            if (GameManager.gameState != GameState.Editor) return;

            RoadNetwork network = editor.roadNetwork;
            MapRoads map = Map(airbase);
            if (network == null || network == map.Network) return;

            // A mission let go of since the tool showed it ("Remove overrides"): the copy on screen is
            // what is being edited, so it is what the airbase goes on with.
            if (airbase.GetTaxiNetwork() != network) SetNetwork(airbase, network);

            SavedAirbase saved = airbase.SavedAirbase;
            if (saved != null && saved != airbase.airbaseSettings)
            {
                saved.roads = network;
                return;
            }

            Mission mission = MissionManager.CurrentMission;
            if (mission == null) return;

            // As AirbasePanel.CreateOverride does it, with the roads handed over before the link so the
            // link keeps them (AfterLink).
            string name = airbase.SavedAirbase.UniqueName;
            SavedAirbase own = mission.airbases.Find(x => x.UniqueName == name);
            if (own == null)
            {
                own = SavedAirbase.CreateOverride(airbase.SavedAirbase);
                own.SavedInMission = true;
                mission.airbases.Add(own);
            }

            own.roads = network;
            SavedAirbase old = airbase.SavedAirbase;
            airbase.LinkSavedAirbase(own, customAirbase: false);
            mission.ReferenceReplaced(old, own);

            Plugin.LogDebug($"airbase '{name}': its roads edited in the mission editor, so the mission now overrides it");
        }

        /// <summary>
        /// After an override is readied for saving: the roads in use if they differ from the map's,
        /// none if they are the same, which is how a mission says it follows the map. A mission whose
        /// roads are not the ones in use (nothing here linked them) is left as it is.
        /// </summary>
        public static void BeforeSave(SavedAirbase saved)
        {
            Airbase airbase = saved?.Airbase;
            if (airbase == null || !MissionAirbases.IsOurs(airbase) || airbase.SavedAirbase != saved || saved == airbase.airbaseSettings)
                return;

            MapRoads map = Map(airbase);
            RoadNetwork network = airbase.GetTaxiNetwork();

            if (network == null || network == map.Network)
            {
                if (!HasRoads(saved.roads)) saved.roads = new RoadNetwork();
                return;
            }

            saved.roads = MissionTaxiRoads.Same(Shapes(network), Shapes(map.Network)) ? new RoadNetwork() : network;

            // Said when it is saved too, for whoever edits it to find in the log.
            int pieces = saved.roads == network && network.nodes != null ? Pieces(network) : 0;
            if (pieces > 1)
                Plugin.LogWarning($"airbase '{saved.UniqueName}': the mission is saved with taxi roads that are {pieces} separate " +
                                  "networks; AI aircraft on one find no way to the others. Join each road's end to a blue node of " +
                                  "the rest in the Roads tool");
        }

        static List<RoadShape> Shapes(RoadNetwork network)
        {
            var shapes = new List<RoadShape>(network?.roads?.Count ?? 0);
            if (network?.roads == null) return shapes;

            foreach (Road road in network.roads)
            {
                var points = new (float X, float Y, float Z)[road?.points?.Count ?? 0];
                for (int i = 0; i < points.Length; i++) points[i] = (road.points[i].x, road.points[i].y, road.points[i].z);
                shapes.Add(new RoadShape { Points = points, Bridge = road != null && road.IsBridge() });
            }

            return shapes;
        }

        /// <summary>A network's roads copied point for point, each with its length and box.</summary>
        static RoadNetwork Copy(RoadNetwork network)
        {
            var copy = new RoadNetwork { AllowMerge = true };
            if (network?.roads == null) return copy;

            foreach (Road road in network.roads)
            {
                if (road?.points == null) continue;
                var twin = new Road();
                foreach (GlobalPosition point in road.points) twin.AddPoint(point);
                twin.SetBridge(road.IsBridge());
                twin.CalcLength();
                copy.roads.Add(twin);
            }

            return copy;
        }
    }
}
