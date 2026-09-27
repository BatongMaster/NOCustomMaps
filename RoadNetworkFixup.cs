using System;
using System.Collections.Generic;
using NuclearOption.SceneLoading;
using RoadPathfinding;
using UnityEngine;

namespace CustomMaps
{
    /// <summary>
    /// Builds the game's road network from the polylines the map ships.
    ///
    /// Roads in Nuclear Option are a pathing graph and nothing else — no renderer ever
    /// reads one. <c>GroundVehicle</c> and <c>MobileArtilleryAI</c> path along them
    /// through <c>RoadPathfinder</c>, and without one every ground unit on the map
    /// navigates cross-country.
    ///
    /// Nothing here needs reflection: <c>RoadNetworkSO.RoadNetwork</c>,
    /// <c>RoadNetwork.roads</c>, <c>Road.AddPoint</c> and <c>Road.CalcLength</c> are all
    /// public. What it does need is care about two things the API does not enforce.
    /// </summary>
    internal static class RoadNetworkFixup
    {
        /// <summary>
        /// Attaches a road network to a prepared prefab, if the map ships one.
        ///
        /// Assigned as a <c>RoadNetworkSO</c> rather than merged in later because that is
        /// the path the game itself takes — <c>LevelInfo.ApplyMapSettings</c> calls
        /// <c>MapSettings.CreateRoadNetwork</c>, which clones the asset so the original
        /// is never mutated, and it is also what the in-game mission editor's road tool
        /// requires; <c>RoadEditor</c> refuses to open on a map whose scriptable object is
        /// null. <see cref="VerifyOrMerge"/> covers the case where that clone does not
        /// survive the trip.
        /// </summary>
        public static void Attach(MapSettings settings, LoadedMap map)
        {
            string asset = map.Manifest?.RoadNetwork;
            if (string.IsNullOrEmpty(asset)) return;

            var data = map.Asset<TextAsset>(asset);
            if (data == null)
            {
                Plugin.LogWarning($"{settings.name}: manifest names a road network '{asset}' but the " +
                                  "bundle has no TextAsset by that name — ground AI will drive cross-country");
                return;
            }

            List<RoadRecord> records;
            try
            {
                records = RoadData.Read(data.bytes);
            }
            catch (System.Exception e)
            {
                Plugin.LogWarning($"{settings.name}: '{asset}' is not readable road data ({e.Message}) — no roads");
                return;
            }

            RoadNetwork network = Build(records);
            if (network.roads.Count == 0) return;

            settings.RoadNetwork = Wrap($"{settings.name}_RoadNetwork", network);
            _shipped[settings.name] = records;

            int bridges = 0, tunnels = 0;
            foreach (RoadRecord record in records)
            {
                if (record.Count < 2) continue;
                if (record.IsBridge) bridges++;
                else if (record.IsTunnel) tunnels++;
            }

            // Tunnels are counted but not flagged as bridges: a unit in a bore should take new
            // orders normally, and SetBridge is what stops one on a deck taking them.
            Plugin.LogDebug($"{settings.name}: road network {network.roads.Count} road(s), " +
                            $"{CountPoints(network)} point(s), {Kilometres(network):N0} km" +
                            (bridges > 0 ? $", {bridges} bridge(s)" : "") +
                            (tunnels > 0 ? $", {tunnels} tunnel(s)" : ""));
        }

        /// <summary>
        /// Gives the map an empty sea-lane network rather than none at all.
        ///
        /// This map ships no lanes — the only water at the datum is Lake Geneva and the
        /// sea beyond the coastal ramp, and neither has been surveyed yet. But leaving
        /// the field null is not the same as leaving it empty.
        /// <c>MapSettings.CreateSeaLanes</c> answers null with <c>new RoadNetwork()</c>,
        /// whose <c>AllowMerge</c> is <c>false</c>; only the branch that clones a real
        /// scriptable object sets it. <c>LevelInfo.LoadFromMission</c> then calls
        /// <c>seaLanes.Merge(mission.missionSettings.missionSeaLanes)</c>, which refuses
        /// and logs an error on every single map load — and, more to the point, throws
        /// away any lanes the mission itself defined.
        ///
        /// An empty network costs nothing and takes the branch that permits merging.
        /// </summary>
        public static void AttachSeaLanes(MapSettings settings, LoadedMap map)
        {
            if (settings.SeaLanes != null) return;

            RoadNetwork lanes = Read(settings, map, map.Manifest?.SeaLanes, "sea lanes")
                                ?? new RoadNetwork { AllowMerge = true };

            settings.SeaLanes = Wrap($"{settings.name}_SeaLanes", lanes);

            if (lanes.roads.Count > 0)
                Plugin.LogDebug($"{settings.name}: sea lanes {lanes.roads.Count} lane(s), " +
                                $"{CountPoints(lanes)} point(s), {Kilometres(lanes):N0} km");
        }

        /// <summary>Reads a named polyline asset out of the bundle, or null if the map
        /// does not ship one.</summary>
        static RoadNetwork Read(MapSettings settings, LoadedMap map, string asset, string what)
        {
            if (string.IsNullOrEmpty(asset)) return null;

            var data = map.Asset<TextAsset>(asset);
            if (data == null)
            {
                Plugin.LogWarning($"{settings.name}: manifest names {what} '{asset}' but the bundle " +
                                  "has no TextAsset by that name");
                return null;
            }

            try
            {
                return Build(RoadData.Read(data.bytes));
            }
            catch (System.Exception e)
            {
                Plugin.LogWarning($"{settings.name}: '{asset}' is not readable {what} ({e.Message})");
                return null;
            }
        }

        static RoadNetworkSO Wrap(string name, RoadNetwork network)
        {
            var so = ScriptableObject.CreateInstance<RoadNetworkSO>();
            so.name = name;
            so.hideFlags = HideFlags.HideAndDontSave;
            so.RoadNetwork = network;
            return so;
        }

        static RoadNetwork Build(List<RoadRecord> records)
        {
            var network = new RoadNetwork { AllowMerge = true };

            foreach (RoadRecord record in records)
            {
                if (record.Count < 2) continue;

                var road = new Road();
                for (int i = 0; i < record.Count; i++)
                    road.AddPoint(new GlobalPosition(record.X(i), record.Y(i), record.Z(i)));

                // AddPoint refreshes the bounding box but NOT the length, and Dijkstra
                // weights every edge by Road.length. Skip this and the pathfinder sees a
                // network where every road costs nothing, so it returns whichever route
                // it happened to expand first.
                road.CalcLength();

                // A bridge only when the map says so. PathfindingAgent.IsOnBridge makes
                // GroundVehicle.UnitCommand_ProcessSetDestination return early, so a unit on a
                // flagged road ignores new orders until it is off it: right for a bridge deck,
                // where stopping to re-route is how a convoy ends up in the river, and wrong
                // for anything else. Heartland ships none across its 674 roads, and version 1
                // road files carry no flags, so every older map keeps this false.
                road.SetBridge(record.IsBridge);

                network.roads.Add(road);
            }

            return network;
        }

        /// <summary>
        /// Confirms the network survived into <c>LevelInfo</c>, and merges it in if not.
        ///
        /// <c>MapSettings.CreateRoadNetwork</c> goes through
        /// <c>Object.Instantiate(RoadNetworkSO)</c>, which deep-clones by serialization.
        /// The points are <c>GlobalPosition</c>, a <c>[StructLayout(LayoutKind.Explicit)]</c>
        /// struct, and while Unity serialises by field name and should handle it, "should"
        /// is not something to discover on a dedicated server. If the clone arrives empty
        /// the roads are merged in directly, which touches no serialisation at all.
        ///
        /// <c>Merge</c> refuses unless <c>AllowMerge</c> is set, and the game only sets it
        /// on the branch where the scriptable object was not null — so it is set here
        /// rather than assumed.
        /// </summary>
        public static void VerifyOrMerge(MapSettings settings)
        {
            if (settings == null) return;

            List<RoadRecord> records = ShippedFor(settings);
            if (records == null || records.Count == 0) return;

            LevelInfo level = NetworkSceneSingleton<LevelInfo>.i;
            RoadNetwork live = level?.roadNetwork;
            if (live == null) return;

            if (live.roads.Count > 0)
            {
                Plugin.LogDebug($"{settings.name}: {live.roads.Count} road(s), {live.nodes.Count} junction(s) live");
                return;
            }

            live.AllowMerge = true;
            live.Merge(Build(records));

            Plugin.LogWarning($"{settings.name}: the road network did not survive being cloned into " +
                              $"LevelInfo, so it was merged in directly — {live.roads.Count} road(s), " +
                              $"{live.nodes.Count} junction(s)");
        }

        /// <summary>What each prefab shipped, so the fallback can rebuild it without
        /// re-reading the bundle. Keyed by prefab name rather than held as a single field:
        /// more than one custom map can be installed, and a shared field would merge one
        /// map's motorways into another map's terrain.</summary>
        static readonly Dictionary<string, List<RoadRecord>> _shipped =
            new Dictionary<string, List<RoadRecord>>(StringComparer.Ordinal);

        /// <summary>The instance is a clone, so its name carries the prefab's with a
        /// suffix — the same match the rest of the fixups use.</summary>
        static List<RoadRecord> ShippedFor(MapSettings instance)
        {
            foreach (KeyValuePair<string, List<RoadRecord>> entry in _shipped)
                if (instance.name.StartsWith(entry.Key, StringComparison.Ordinal)) return entry.Value;

            return null;
        }

        static int CountPoints(RoadNetwork network)
        {
            int points = 0;
            foreach (Road road in network.roads) points += road.points.Count;
            return points;
        }

        static float Kilometres(RoadNetwork network)
        {
            float metres = 0f;
            foreach (Road road in network.roads) metres += road.length;
            return metres / 1000f;
        }
    }
}
