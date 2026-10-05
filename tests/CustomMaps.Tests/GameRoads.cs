using System;
using System.Collections.Generic;

namespace CustomMaps.Tests
{
    /// <summary>
    /// The game's road graph and its route search, copied from the decompiled <c>RoadPathfinding.Node</c>,
    /// <c>Road</c>, <c>RoadNetwork</c>, <c>FastMath</c> and <c>RoadPathfinder.TryPathfind</c>, with the names
    /// kept. What <see cref="RoadPathSearch"/> is compared against: a junction list built the way
    /// <c>RegenerateNetwork</c> builds it (ends fused within 10 m, the last junction in range winning, each
    /// junction's connections in the order of the roads), and the loop that sorts every unvisited junction
    /// at every step.
    /// </summary>
    internal sealed class GameNode
    {
        public int id;
        public float x, y, z;
        public Dictionary<GameRoad, GameNode> connectionsLookup = new Dictionary<GameRoad, GameNode>();
        public GameNode parent;
        public float dist;

        public GameNode(GameNetwork network, float x, float y, float z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
            id = network.nodes.Count;
            network.nodes.Add(this);
        }

        public void GenerateConnections(GameNetwork network)
        {
            connectionsLookup.Clear();
            foreach (GameRoad road in network.roads)
            {
                if (road.startNode == this || road.endNode == this)
                {
                    GameNode value = road.startNode != this ? road.startNode : road.endNode;
                    connectionsLookup.Add(road, value);
                }
            }
        }

        public void ClearPathfindingData()
        {
            parent = null;
            dist = float.MaxValue;
        }

        /// <summary>A junction links to its roads and they back to it, so a failed assertion must not try
        /// to print one field by field.</summary>
        public override string ToString() => $"junction {id}";
    }

    internal sealed class GameRoad
    {
        public readonly List<(float X, float Y, float Z)> points = new List<(float X, float Y, float Z)>();
        public float length;
        public GameNode startNode, endNode;

        public GameRoad() { }

        public GameRoad(RoadRecord record)
        {
            for (int i = 0; i < record.Count; i++) points.Add((record.X(i), record.Y(i), record.Z(i)));
            CalcLength();
        }

        public override string ToString() => $"road of {length} m";

        public float CalcLength()
        {
            length = 0f;
            for (int i = 0; i < points.Count - 1; i++) length += GameMath.Distance(points[i], points[i + 1]);
            return length;
        }

        public void GenerateNodes(GameNetwork network)
        {
            startNode = null;
            endNode = null;
            for (int i = 0; i < network.nodes.Count; i++)
            {
                GameNode node = network.nodes[i];
                if (GameMath.InRange(points[0], (node.x, node.y, node.z), 10f)) startNode = node;
                if (GameMath.InRange(points[points.Count - 1], (node.x, node.y, node.z), 10f)) endNode = node;
            }

            if (startNode == null) startNode = new GameNode(network, points[0].X, points[0].Y, points[0].Z);
            if (endNode == null)
            {
                var last = points[points.Count - 1];
                endNode = new GameNode(network, last.X, last.Y, last.Z);
            }
        }
    }

    internal sealed class GameNetwork
    {
        public readonly List<GameRoad> roads = new List<GameRoad>();
        public readonly List<GameNode> nodes = new List<GameNode>();

        /// <summary>A network of the roads in a road file, in its order, as the plugin's
        /// <c>RoadNetworkFixup</c> builds one and the game then regenerates it.</summary>
        public static GameNetwork From(IEnumerable<RoadRecord> records)
        {
            var network = new GameNetwork();
            foreach (RoadRecord record in records)
                if (record.Count >= 2) network.roads.Add(new GameRoad(record));
            network.RegenerateNetwork();
            return network;
        }

        public void RegenerateNetwork()
        {
            nodes.Clear();
            foreach (GameRoad road in roads) road.GenerateNodes(this);
            foreach (GameNode node in nodes) node.GenerateConnections(this);
        }

        public void ClearPathfindingData()
        {
            foreach (GameNode node in nodes) node.ClearPathfindingData();
        }
    }

    internal static class GameMath
    {
        public static float Distance((float X, float Y, float Z) a, (float X, float Y, float Z) b)
            => (float)Math.Sqrt(SquareDistance(a, b));

        public static float SquareDistance((float X, float Y, float Z) a, (float X, float Y, float Z) b)
        {
            float num = a.X - b.X;
            float num2 = a.Y - b.Y;
            float num3 = a.Z - b.Z;
            return num * num + num2 * num2 + num3 * num3;
        }

        public static bool InRange((float X, float Y, float Z) a, (float X, float Y, float Z) b, float range)
        {
            float num = a.X - b.X;
            float num2 = a.Z - b.Z;
            float num3 = a.Y - b.Y;
            return num * num + num3 * num3 + num2 * num2 < range * range;
        }
    }

    internal enum GameResult { NoNetwork, NoConnection, NoRoadNearTarget, Success }

    /// <summary>The loop of <c>RoadPathfinder.TryPathfind</c>, from its start and target junctions on.</summary>
    internal static class GamePathfinder
    {
        static readonly List<GameNode> unvisitedSet = new List<GameNode>();

        /// <summary>
        /// The game's loop. <paramref name="order"/> left null sorts as the game does, with an unstable
        /// <c>List.Sort</c> on distance alone; given, it breaks ties between equal distances at random, a
        /// fresh draw at every step, standing for whatever another runtime's sort might do with them.
        /// <paramref name="wide"/> compares the unrounded sum, as a runtime that does float arithmetic in
        /// double precision (32-bit Mono) would.
        /// </summary>
        public static GameResult TryPathfind(GameNetwork network, GameNode start, GameNode target,
                                             List<GameNode> results, Random order = null, bool wide = false)
        {
            network.ClearPathfindingData();
            GameNode closestNode = start;
            closestNode.dist = 0f;

            var rank = order != null ? new Dictionary<GameNode, int>() : null;

            unvisitedSet.Clear();
            unvisitedSet.AddRange(network.nodes);
            while (unvisitedSet.Count > 0)
            {
                if (order == null)
                {
                    unvisitedSet.Sort((GameNode a, GameNode b) => a.dist.CompareTo(b.dist));
                }
                else
                {
                    foreach (GameNode node in unvisitedSet) rank[node] = order.Next();
                    unvisitedSet.Sort((GameNode a, GameNode b) =>
                    {
                        int byDistance = a.dist.CompareTo(b.dist);
                        return byDistance != 0 ? byDistance : rank[a].CompareTo(rank[b]);
                    });
                }

                closestNode = unvisitedSet[0];
                unvisitedSet.Remove(closestNode);
                foreach (KeyValuePair<GameRoad, GameNode> item in closestNode.connectionsLookup)
                {
                    GameNode value = item.Value;
                    GameRoad key = item.Key;
                    if (!unvisitedSet.Contains(value)) continue;

                    if (wide ? (double)closestNode.dist + key.length < value.dist
                             : closestNode.dist + key.length < value.dist)
                    {
                        value.dist = wide ? (float)((double)closestNode.dist + key.length) : closestNode.dist + key.length;
                        value.parent = closestNode;
                    }
                }

                if (closestNode.dist == float.MaxValue)
                {
                    results.Clear();
                    return GameResult.NoConnection;
                }

                if (closestNode == target) break;
            }

            results.Clear();
            while (closestNode.parent != null)
            {
                results.Add(closestNode);
                closestNode = closestNode.parent;
            }

            results.Add(closestNode);
            results.Reverse();
            return GameResult.Success;
        }
    }

    /// <summary>
    /// <see cref="RoadPathSearch"/> fed from a copied network exactly as the plugin's patch feeds it from the
    /// game's: junctions by <c>id</c>, connections in <c>connectionsLookup</c> order, a neighbour the network
    /// does not list left out, and on a definite answer every junction's <c>dist</c> and <c>parent</c>
    /// written back.
    /// </summary>
    internal static class HeapPathfinder
    {
        public static RoadPathSearch.Outcome TryPathfind(RoadPathSearch search, GameNetwork network, GameNode start,
                                                         GameNode target, List<GameNode> results)
        {
            List<GameNode> nodes = network.nodes;
            var route = new List<int>();

            search.Begin(nodes.Count, start.id, target.id);
            while (search.TryNext(out int taken))
            {
                foreach (KeyValuePair<GameRoad, GameNode> connection in nodes[taken].connectionsLookup)
                {
                    GameNode other = connection.Value;
                    if (!Listed(nodes, other)) continue;
                    search.Relax(taken, other.id, connection.Key.length);
                }
            }

            RoadPathSearch.Outcome outcome = search.Finish(route);

            results.Clear();
            foreach (int node in route) results.Add(nodes[node]);
            if (outcome == RoadPathSearch.Outcome.Ambiguous) return outcome;

            for (int i = 0; i < nodes.Count; i++)
            {
                int parent = search.Parent(i);
                nodes[i].dist = search.Distance(i);
                nodes[i].parent = parent >= 0 ? nodes[parent] : null;
            }

            return outcome;
        }

        static bool Listed(List<GameNode> nodes, GameNode node)
            => node != null && (uint)node.id < (uint)nodes.Count && nodes[node.id] == node;
    }
}
