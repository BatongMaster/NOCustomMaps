using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RoadPathfinding;
using Unity.Profiling;

namespace CustomMaps.Patches
{
    /// <summary>
    /// Answers the game's road route requests on a custom map with <see cref="RoadPathSearch"/>, which finds
    /// the same route as the game's <c>RoadPathfinder.TryPathfind</c> in a fraction of the time.
    ///
    /// Every ground unit asks for a route whenever its destination moves more than 10 m
    /// (<c>PathfindingAgent.Pathfind</c>, the method's only caller), and so does an AI aircraft taxiing at
    /// an airbase (on the airbase's <c>taxiNetwork</c>). The game's search sorts every junction still
    /// unvisited at every step. Timed in the player's own 64-bit Mono on Swiss Alps' 1,338 junctions, over
    /// 200 random requests, a copy of its loop took 26.6 ms a request on average and 51 ms at worst; the
    /// heap took 0.09 ms and 0.26 ms for the same loop, with the same answer every time and nothing
    /// allocated. Run end to end in that runtime — the game's own <c>RoadNetwork</c> and method, this patch
    /// applied by HarmonyX — 500 random requests came back identical in result, route and every junction's
    /// <c>dist</c> and <c>parent</c>, at 0.24 ms against 33 ms for the whole method, opening included.
    ///
    /// <para><b>What is copied and what is not.</b> A prefix replaces the whole method, so it repeats the
    /// method's opening step for step, calling the game's own helpers for it: clearing the junctions
    /// (<c>ClearPathfindingData</c>), the nearest road or junction to each end (<c>TryGetNearestRoad</c>,
    /// <c>TryFindNearestNode</c>), the start's <c>dist</c> of 0 and the <c>NoRoadNearTarget</c> test. Only
    /// the loop is the heap's. The prefix leaves <c>results</c> and the result as the game's method would,
    /// and every junction's <c>dist</c> and <c>parent</c> as the game's loop leaves them for some order of
    /// taking junctions at exactly equal distance: along the route that is the game's own state, and off
    /// it, where two junctions tie, possibly another order's than the one the game's sort would take
    /// (<see cref="RoadPathSearch"/> explains). Nothing outside the method reads <c>dist</c> or
    /// <c>parent</c>, and the game clears both before each search, but they are written back all the same.
    /// The game's private <c>unvisitedSet</c> is left alone: it is filled afresh at every search.</para>
    ///
    /// <para><b>When the game's own method runs instead.</b> On the base maps, always: the patch is
    /// active only while a custom map is loaded. On a custom map, whenever the request is anything but
    /// ordinary: no roads (<c>NoNetwork</c>, which costs the game nothing), junctions whose <c>id</c> is
    /// not their place in <c>RoadNetwork.nodes</c>, an end junction the network does not list, a road
    /// with no junctions yet, or a tie the game's unstable sort could break either way bearing on the
    /// route (<see cref="RoadPathSearch.Outcome.Ambiguous"/>). Each of those leaves the game's own code to
    /// give its own answer, its own exception included. And not at all when the game's method is no longer
    /// the one this was checked against: <see cref="Supported"/> compares its fingerprint at start-up.</para>
    ///
    /// <para>Main thread only, as the game's own search is (its <c>unvisitedSet</c> is static).</para>
    /// </summary>
    internal static class FastRoadPathfinder
    {
        /// <summary>
        /// True when the running game's <c>TryPathfind</c>, the comparison it sorts by and the methods that
        /// clear the junctions before it (<see cref="RoadPathSearch.GameMethods"/>) have the fingerprint
        /// this class was checked against (<see cref="RoadPathSearch.VerifiedGameMethods"/>). A game update
        /// that changes how roads are searched — another weight than <c>Road.length</c>, a different
        /// opening, a fix of its own, another value for a junction not yet reached — then turns the patch
        /// off rather than replacing the game's new search with an old copy. Logged as a warning, once,
        /// when it does.
        /// </summary>
        internal static readonly bool Supported = Check();

        /// <summary>True while the map in play is one of ours.</summary>
        internal static bool Active;

        /// <summary>Under <c>DebugLogging</c>, the first this many searches on each map are also run
        /// through the game's own method, timed and compared.</summary>
        const int ChecksPerMap = 5;

        static readonly RoadPathSearch Search = new RoadPathSearch();
        static readonly List<int> Route = new List<int>();
        static readonly ProfilerMarker Marker = new ProfilerMarker("RoadPathfinder.TryPathfind (Custom Maps)");

        /// <summary>Set while the game's own method is running on purpose, for a check.</summary>
        static bool _inside;

        static int _checks, _searches, _handedBack;
        static bool _failed, _helperThrew;
        static string _map;

        static bool Check()
        {
            try
            {
                var prints = new List<ulong>();
                foreach (string[] game in RoadPathSearch.GameMethods)
                {
                    string typeName = game[0].Length == 0 ? game[1] : game[0] + "." + game[1];
                    Type type = typeof(RoadPathfinder).Assembly.GetType(typeName);
                    MethodInfo method = type != null ? AccessTools.Method(type, game[2]) : null;

                    // TryPathfind itself is in the start-up check of patch targets, which says so.
                    if (method == null)
                    {
                        if (prints.Count > 0)
                            Plugin.LogWarning($"the game has no {typeName}.{game[2]}, which its road search relied on; " +
                                              "custom maps keep the game's own");
                        return false;
                    }

                    prints.Add(Fingerprint(method));
                }

                ulong print = IlFingerprint.OfAll(prints);
                if (print == RoadPathSearch.VerifiedGameMethods) return true;

                Plugin.LogWarning($"the game's road search (RoadPathfinder.TryPathfind and the clearing before it, " +
                                  $"fingerprint {print:X16}) is not the one this build of Custom Maps was checked " +
                                  "against; custom maps keep the game's own, which is slow on a large road network");
                return false;
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"could not read the game's road search ({e.GetType().Name}: {e.Message}); " +
                                  "custom maps keep the game's own");
                return false;
            }
        }

        /// <summary>The method's fingerprint as <see cref="IlFingerprint"/> reads it, the names resolved
        /// by the running game's own module.</summary>
        static ulong Fingerprint(MethodInfo method)
        {
            Module module = method.Module;
            return IlFingerprint.OfMethod(method.GetMethodBody().GetILAsByteArray(),
                token => Name(module, token),
                token => module.ResolveMethod(token).GetMethodBody().GetILAsByteArray());
        }

        /// <summary>What a token names: a string literal's text, or a member's or type's name (a generic
        /// type's without its arguments, <c>List`1</c>, as <c>Type.Name</c> gives it).</summary>
        static string Name(Module module, int token)
        {
            switch ((uint)token >> 24)
            {
                case 0x70: return module.ResolveString(token);
                case 0x11: return "sig";
                default: return module.ResolveMember(token).Name;
            }
        }

        /// <summary>Called when a map is applied: turns the search on for a custom map and off for any
        /// other, and logs what the last map's searches came to.</summary>
        internal static void MapApplied(MapSettings mapSettings)
        {
            Summary(final: true);

            _checks = _searches = _handedBack = 0;
            _failed = _helperThrew = false;
            _map = mapSettings != null ? mapSettings.name : null;
            Active = !Plugin.Disabled && MapFixups.IsCustom(mapSettings);

            if (Active) Plugin.LogDebug($"road pathfinder: {_map} searches its roads with a heap");
        }

        /// <summary>How many searches the map has had and how many were handed back, at debug level: when
        /// the next map is applied, and on the way at the 100th, 1,000th, 10,000th … search, since the
        /// last map of a session, or a server's only one, is never followed by another.</summary>
        static void Summary(bool final)
        {
            if (_searches == 0) return;
            Plugin.LogDebug($"road pathfinder: {_searches:N0} route search(es) on {_map}{(final ? "" : " so far")}, " +
                            $"{_handedBack:N0} handed to the game's own search (a tie between equal-length routes, " +
                            "or an unusual network)");
        }

        /// <summary>100, 1,000, 10,000 and so on.</summary>
        static bool Milestone(int searches)
        {
            if (searches < 100) return false;
            while (searches % 10 == 0) searches /= 10;
            return searches == 1;
        }

        /// <summary>A failure inside the heap search itself (one in the game's own helpers is handed back
        /// instead, by <see cref="GameHelperThrew"/>): logged once, and the game's own search for the rest
        /// of the map.</summary>
        internal static void Failed(Exception e)
        {
            Active = false;
            if (_failed) return;
            _failed = true;
            Plugin.LogWarning($"road pathfinder: the heap search failed ({e.GetType().Name}: {e.Message}); the game's " +
                              "own search is used for the rest of this map");
        }

        /// <summary>
        /// Answers one request in place of the game's method. False when the game's own method must run
        /// instead, before anything it would not have done itself has been changed.
        /// </summary>
        internal static bool TryPathfind(RoadNetwork network, GlobalPosition startPos, GlobalPosition targetPos,
                                         List<Node> results, ref RoadPathfinder.PathfindResult result)
        {
            if (_inside) return false;

            if (_checks < ChecksPerMap && Plugin.DebugEnabled)
                return Checked(network, startPos, targetPos, results, ref result);

            return Answer(network, startPos, targetPos, results, ref result);
        }

        static bool Answer(RoadNetwork network, GlobalPosition startPos, GlobalPosition targetPos,
                           List<Node> results, ref RoadPathfinder.PathfindResult result)
        {
            // NoNetwork costs the game nothing, and a network this cannot read is the game's to fail on.
            if (network == null || results == null || network.roads == null || network.roads.Count == 0) return false;

            List<Node> nodes = network.nodes;
            if (nodes == null || nodes.Count == 0) return false;

            // A junction's id is its place in the list (new Node takes nodes.Count, and only
            // RegenerateNetwork clears the list). With that checked, a neighbour is one of the network's
            // junctions exactly when nodes[its id] is that very junction, which is the game's
            // unvisitedSet.Contains without the walk along the list.
            for (int i = 0; i < nodes.Count; i++)
                if (nodes[i] == null || nodes[i].id != i || nodes[i].connectionsLookup == null) return false;

            using (Marker.Auto())
            {
                // The searches before this one, all settled.
                if (Milestone(_searches)) Summary(final: false);
                _searches++;

                // The game's opening, step for step. It runs the game's own helpers, so whatever they
                // throw is the game's to throw: the request is handed back, and the game's method meets
                // the same failure on the same request, as it would without the patch. The search stays
                // on for the next request.
                Node start, target;
                bool nearTarget;
                try
                {
                    if (!Opening(network, nodes, startPos, targetPos, out start, out target, out nearTarget))
                        return HandBack();
                }
                catch (Exception e)
                {
                    return GameHelperThrew(e);
                }

                if (nearTarget)
                {
                    result = RoadPathfinder.PathfindResult.NoRoadNearTarget;
                    return true;
                }

                // The loop, on the heap. Connections in the game's own order, so equal offers are met in
                // the order the game meets them.
                Search.Begin(nodes.Count, start.id, target.id);
                while (Search.TryNext(out int taken))
                {
                    foreach (KeyValuePair<Road, Node> connection in nodes[taken].connectionsLookup)
                    {
                        Node other = connection.Value;
                        if (!Listed(nodes, other)) continue;
                        Search.Relax(taken, other.id, connection.Key.length);
                    }
                }

                RoadPathSearch.Outcome outcome = Search.Finish(Route);
                if (outcome == RoadPathSearch.Outcome.Ambiguous) return HandBack();

                for (int i = 0; i < nodes.Count; i++)
                {
                    int parent = Search.Parent(i);
                    nodes[i].dist = Search.Distance(i);
                    nodes[i].parent = parent >= 0 ? nodes[parent] : null;
                }

                results.Clear();
                if (outcome == RoadPathSearch.Outcome.NoConnection)
                {
                    result = RoadPathfinder.PathfindResult.NoConnection;
                    return true;
                }

                for (int i = 0; i < Route.Count; i++) results.Add(nodes[Route[i]]);
                result = RoadPathfinder.PathfindResult.Success;
                return true;
            }
        }

        /// <summary>
        /// The game's opening (decompiled <c>TryPathfind</c>), step for step: the clearing, the junction
        /// nearest the start, its <c>dist</c> of 0, then the junction nearest the target and how near the
        /// target's road comes to it, for the <c>NoRoadNearTarget</c> test. False where the network is one
        /// this does not read (a road without both ends or points, a junction not in the list), with
        /// nothing changed beyond what the game's method changes before reaching the same place.
        /// </summary>
        static bool Opening(RoadNetwork network, List<Node> nodes, GlobalPosition startPos, GlobalPosition targetPos,
                            out Node start, out Node target, out bool nearTarget)
        {
            start = target = null;
            nearTarget = false;

            network.ClearPathfindingData();

            if (network.TryGetNearestRoad(startPos, out Road nearestRoad))
            {
                if (nearestRoad.startNode == null || nearestRoad.endNode == null) return false;
                float toStart = FastMath.SquareDistance(startPos, nearestRoad.startNode.position);
                float toEnd = FastMath.SquareDistance(startPos, nearestRoad.endNode.position);
                start = toStart < toEnd ? nearestRoad.startNode : nearestRoad.endNode;
            }
            else
            {
                RoadPathfinder.TryFindNearestNode(network, startPos, out start);
            }

            if (!Listed(nodes, start)) return false;
            start.dist = 0f;

            float range = float.MaxValue;
            if (network.TryGetNearestRoad(targetPos, out Road targetRoad))
            {
                if (targetRoad.startNode == null || targetRoad.endNode == null) return false;
                float toStart = FastMath.SquareDistance(targetPos, targetRoad.startNode.position);
                float toEnd = FastMath.SquareDistance(targetPos, targetRoad.endNode.position);
                target = toStart < toEnd ? targetRoad.startNode : targetRoad.endNode;

                List<GlobalPosition> points = targetRoad.points;
                if (points == null) return false;
                for (int i = 0; i < points.Count; i++)
                    if (FastMath.InRange(points[i], targetPos, range)) range = FastMath.Distance(points[i], targetPos);
            }
            else if (RoadPathfinder.TryFindNearestNode(network, targetPos, out target))
            {
                range = FastMath.Distance(target.position, targetPos);
            }

            // The game answers NoRoadNearTarget before it looks at the target junction at all.
            if (FastMath.InRange(startPos, targetPos, range))
            {
                nearTarget = true;
                return true;
            }

            return Listed(nodes, target);
        }

        /// <summary>One of the game's own helpers threw in the opening. Not the search's failure, so the
        /// search stays on: the request is handed back for the game's method to meet the same failure,
        /// and the first such on a map is noted at debug level.</summary>
        static bool GameHelperThrew(Exception e)
        {
            if (!_helperThrew)
            {
                _helperThrew = true;
                Plugin.LogDebug($"road pathfinder: the game's own nearest-road lookup failed on {_map} " +
                                $"({e.GetType().Name}: {e.Message}); that request goes to the game's own search, " +
                                "which meets the same failure");
            }

            return HandBack();
        }

        static bool Listed(List<Node> nodes, Node node)
            => node != null && (uint)node.id < (uint)nodes.Count && nodes[node.id] == node;

        static bool HandBack()
        {
            _handedBack++;
            return false;
        }

        /// <summary>
        /// Under <c>DebugLogging</c>, for the first few searches on a map: this search, then the game's own
        /// method, each into a list of its own, and the two compared and timed in the log; the caller's list
        /// is filled once both are in, as the game fills it. The game's run comes second, so every
        /// junction's <c>dist</c> and <c>parent</c> end as the game's own method leaves them. A request
        /// this hands back, or answers without a search (<c>NoRoadNearTarget</c>), is not compared and
        /// does not count: the game's method then runs once, as it would without the check, or not at all.
        /// A difference is logged as a warning, and the game's answer kept, with its own search for the
        /// rest of the map.
        /// </summary>
        static bool Checked(RoadNetwork network, GlobalPosition startPos, GlobalPosition targetPos,
                            List<Node> results, ref RoadPathfinder.PathfindResult result)
        {
            var ours = new List<Node>();
            var watch = Stopwatch.StartNew();
            bool answered = Answer(network, startPos, targetPos, ours, ref result);
            double ms = watch.Elapsed.TotalMilliseconds;

            // Nothing to compare where this hands the request back, or where no search ran (which leaves
            // the caller's list alone, as the game does).
            if (!answered || (result != RoadPathfinder.PathfindResult.Success &&
                              result != RoadPathfinder.PathfindResult.NoConnection))
                return answered;

            var theirs = new List<Node>();
            RoadPathfinder.PathfindResult theirResult = RoadPathfinder.PathfindResult.NoNetwork;
            Exception threw = null;

            watch.Restart();
            _inside = true;
            try { RoadPathfinder.TryPathfind(network, startPos, targetPos, theirs, out theirResult); }
            catch (Exception e) { threw = e; }
            finally { _inside = false; }
            double gameMs = watch.Elapsed.TotalMilliseconds;

            _checks++;

            if (threw != null)
            {
                // Never seen: a request this answers is one the game's method has no reason to fail on.
                // Its failure is then the answer, so it runs once more, for real, with the caller's list
                // still as it was.
                Plugin.LogWarning($"road pathfinder check {_checks}/{ChecksPerMap}: the heap found {result}, the game's " +
                                  $"own search failed ({threw.GetType().Name}: {threw.Message}); the game's own search " +
                                  "is used for the rest of this map");
                Active = false;
                return false;
            }

            bool same = result == theirResult && ours.Count == theirs.Count;
            for (int i = 0; same && i < theirs.Count; i++) same = ours[i] == theirs[i];

            if (same)
            {
                results.Clear();
                results.AddRange(ours);
                Plugin.LogDebug($"road pathfinder check {_checks}/{ChecksPerMap}: {result}, {ours.Count} junction(s) " +
                                $"of {network.nodes.Count:N0}, {ms:0.000} ms against {gameMs:0.000} ms for the game's " +
                                "own search; same route");
                return true;
            }

            Plugin.LogWarning($"road pathfinder check {_checks}/{ChecksPerMap}: the heap found {result} with " +
                              $"{ours.Count} junction(s), the game's own search {theirResult} with {theirs.Count}; " +
                              "the game's own search is used for the rest of this map");
            Active = false;

            // The game's answer, its list filled only where its method fills one.
            result = theirResult;
            if (theirResult == RoadPathfinder.PathfindResult.Success || theirResult == RoadPathfinder.PathfindResult.NoConnection)
            {
                results.Clear();
                results.AddRange(theirs);
            }

            return true;
        }
    }

    /// <summary>Tells <see cref="FastRoadPathfinder"/> which map is in play. A hook of its own on
    /// <c>ApplyMapSettings</c>, so that it stands or falls apart from the others there, and a prefix:
    /// <c>ApplyMapSettings</c> ends by loading the mission, whose units may ask for routes at once, and
    /// those must already be answered as the new map's.</summary>
    [HarmonyPatch(typeof(LevelInfo), nameof(LevelInfo.ApplyMapSettings))]
    internal static class RoadPathfinderMapPatch
    {
        static bool Prepare() => FastRoadPathfinder.Supported;

        static void Prefix(MapSettings mapSettings)
        {
            try { FastRoadPathfinder.MapApplied(mapSettings); }
            catch (Exception e) { Plugin.LogWarning($"road pathfinder: {e.Message}"); }
        }
    }

    /// <summary>The game's road route search, answered by <see cref="FastRoadPathfinder"/> on a custom
    /// map; on any other, and for any request it hands back, the game's own method runs.</summary>
    [HarmonyPatch(typeof(RoadPathfinder), nameof(RoadPathfinder.TryPathfind))]
    internal static class RoadPathfinderPatch
    {
        static bool Prepare() => FastRoadPathfinder.Supported;

        static bool Prefix(RoadNetwork network, GlobalPosition startPos, GlobalPosition targetPos,
                           List<Node> results, ref RoadPathfinder.PathfindResult result)
        {
            if (!FastRoadPathfinder.Active) return true;

            // Nothing is changed that the game's method would not change itself before a failure can
            // happen, so on any failure the game's method simply runs.
            try { return !FastRoadPathfinder.TryPathfind(network, startPos, targetPos, results, ref result); }
            catch (Exception e)
            {
                FastRoadPathfinder.Failed(e);
                return true;
            }
        }
    }
}
