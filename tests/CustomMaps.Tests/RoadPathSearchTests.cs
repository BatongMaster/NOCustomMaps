using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace CustomMaps.Tests
{
    /// <summary>
    /// The heap search against the game's own loop (<see cref="GamePathfinder"/>, copied from the decompiled
    /// game): the same route and result wherever the heap gives an answer (and the same junction state, on
    /// networks without exact ties, where every order leaves the same), and, wherever an exact tie could
    /// have let the game's unstable sort choose another equal-length route, no answer
    /// (<see cref="RoadPathSearch.Outcome.Ambiguous"/>) so that the game's own loop runs.
    /// </summary>
    public class RoadPathSearchTests
    {
        readonly ITestOutputHelper _output;

        public RoadPathSearchTests(ITestOutputHelper output) => _output = output;

        // ---- The search's own rules, step by step -------------------------------------------------

        static RoadPathSearch.Outcome Finish(RoadPathSearch search, List<int> route)
        {
            while (search.TryNext(out _)) { }
            return search.Finish(route);
        }

        [Fact]
        public void TakesJunctionsNearestFirstAndReturnsTheRouteFromTheStart()
        {
            var search = new RoadPathSearch();
            search.Begin(4, 0, 3);

            Assert.True(search.TryNext(out int taken));
            Assert.Equal(0, taken);
            search.Relax(0, 1, 7f);
            search.Relax(0, 2, 3f);

            Assert.True(search.TryNext(out taken));
            Assert.Equal(2, taken);
            search.Relax(2, 1, 2f);
            search.Relax(2, 3, 9f);

            Assert.True(search.TryNext(out taken));
            Assert.Equal(1, taken);
            search.Relax(1, 3, 1f);

            Assert.True(search.TryNext(out taken));
            Assert.Equal(3, taken);
            Assert.False(search.TryNext(out _));

            var route = new List<int>();
            Assert.Equal(RoadPathSearch.Outcome.Found, search.Finish(route));
            Assert.Equal(new[] { 0, 2, 1, 3 }, route);
            Assert.Equal(6f, search.Distance(3));
            Assert.Equal(1, search.Parent(3));
            Assert.Equal(-1, search.Parent(0));
        }

        [Fact]
        public void AnUnreachableTargetIsNoConnection()
        {
            var search = new RoadPathSearch();
            search.Begin(3, 0, 2);
            Assert.True(search.TryNext(out _));
            search.Relax(0, 1, 5f);

            var route = new List<int>();
            Assert.Equal(RoadPathSearch.Outcome.NoConnection, Finish(search, route));
            Assert.Empty(route);
            Assert.Equal(float.MaxValue, search.Distance(2));
        }

        [Fact]
        public void TheStartAsTargetIsARouteOfOne()
        {
            var search = new RoadPathSearch();
            search.Begin(2, 1, 1);

            var route = new List<int>();
            Assert.Equal(RoadPathSearch.Outcome.Found, Finish(search, route));
            Assert.Equal(new[] { 1 }, route);
        }

        [Fact]
        public void TwoNeighboursOfferingTheSameDistanceAreATie()
        {
            // A square with equal sides: 0-1-3 and 0-2-3 are exactly as long, and which the game returns is
            // its sort's choice.
            var search = new RoadPathSearch();
            search.Begin(4, 0, 3);
            Assert.True(search.TryNext(out _));
            search.Relax(0, 1, 5f);
            search.Relax(0, 2, 5f);
            Assert.True(search.TryNext(out int first));
            search.Relax(first, 3, 5f);
            Assert.True(search.TryNext(out int second));
            search.Relax(second, 3, 5f);

            var route = new List<int>();
            Assert.Equal(RoadPathSearch.Outcome.Ambiguous, Finish(search, route));
            Assert.Equal(new[] { 0, first, 3 }, route);
        }

        [Fact]
        public void ASecondRoadFromTheSameParentIsNotATie()
        {
            var search = new RoadPathSearch();
            search.Begin(2, 0, 1);
            Assert.True(search.TryNext(out _));
            search.Relax(0, 1, 5f);
            search.Relax(0, 1, 5f);

            var route = new List<int>();
            Assert.Equal(RoadPathSearch.Outcome.Found, Finish(search, route));
            Assert.Equal(new[] { 0, 1 }, route);
        }

        [Fact]
        public void AShorterOfferAfterATieSettlesIt()
        {
            var search = new RoadPathSearch();
            search.Begin(5, 0, 4);
            Assert.True(search.TryNext(out _));
            search.Relax(0, 1, 2f);
            search.Relax(0, 2, 2f);
            search.Relax(0, 3, 3f);

            Assert.True(search.TryNext(out int first));
            search.Relax(first, 4, 8f);    // 10
            Assert.True(search.TryNext(out int second));
            search.Relax(second, 4, 8f);   // 10 again, from as far: a tie
            Assert.True(search.TryNext(out int taken));
            Assert.Equal(3, taken);
            search.Relax(3, 4, 6f);        // 9: shorter, and alone

            var route = new List<int>();
            Assert.Equal(RoadPathSearch.Outcome.Found, Finish(search, route));
            Assert.Equal(new[] { 0, 3, 4 }, route);
        }

        /// <summary>
        /// 0-1-3 and 0-2-3 are both 3 long, but 1 is nearer the start than 2, so the game takes it first in
        /// any order: its offer to 3 is there first, and 2's equal one never replaces it. Not a tie.
        /// </summary>
        [Fact]
        public void AFartherNeighbourOfferingTheSameDistanceIsNoTie()
        {
            var network = new GameNetwork();
            GameNode s = new GameNode(network, 0f, 0f, 0f), a = new GameNode(network, 1f, 0f, 0f),
                     b = new GameNode(network, 2f, 0f, 0f), t = new GameNode(network, 3f, 0f, 0f);
            Road(s, a, 1f);
            Road(s, b, 2f);
            Road(a, t, 2f);
            Road(b, t, 1f);

            Assert.Equal(new[] { "0,1,3" }, GameRoutes(network, s, t));

            var heap = new List<GameNode>();
            Assert.Equal(RoadPathSearch.Outcome.Found, HeapPathfinder.TryPathfind(new RoadPathSearch(), network, s, t, heap));
            Assert.Equal(new[] { 0, 1, 3 }, Ids(heap));
        }

        /// <summary>
        /// 1 and 2 are both 5 from the start, and a road of no length runs from 2 to 1. Taken in the other
        /// order, 2 would have made its offer while 1 was still waiting, but the start, nearer than either,
        /// offered 1 the same 5 before that in any order, so it cannot win. Not a tie either.
        /// </summary>
        [Fact]
        public void AnOfferMadeTooLateFromFartherThanTheParentIsNoTie()
        {
            var network = new GameNetwork();
            GameNode s = new GameNode(network, 0f, 0f, 0f), a = new GameNode(network, 1f, 0f, 0f),
                     b = new GameNode(network, 2f, 0f, 0f), t = new GameNode(network, 3f, 0f, 0f);
            Road(s, a, 5f);
            Road(s, b, 5f);
            Road(a, t, 1f);
            Road(b, a, 0f);

            Assert.Equal(new[] { "0,1,3" }, GameRoutes(network, s, t));

            var heap = new List<GameNode>();
            Assert.Equal(RoadPathSearch.Outcome.Found, HeapPathfinder.TryPathfind(new RoadPathSearch(), network, s, t, heap));
            Assert.Equal(new[] { 0, 1, 3 }, Ids(heap));

            // The heap took 1 before 2 there; the late offer is the one checked.
            var search = new RoadPathSearch();
            search.Begin(4, 0, 3);
            Assert.True(search.TryNext(out _));
            search.Relax(0, 1, 5f);
            search.Relax(0, 2, 5f);
            Assert.True(search.TryNext(out int taken));
            Assert.Equal(1, taken);
            search.Relax(1, 3, 1f);
            Assert.True(search.TryNext(out taken));
            Assert.Equal(2, taken);
            search.Relax(2, 1, 0f);

            var route = new List<int>();
            Assert.Equal(RoadPathSearch.Outcome.Found, Finish(search, route));
        }

        /// <summary>
        /// The start offers 2 exactly 2²⁴ m; 1, half a metre out, offers it 16,777,215.5 m, which rounds
        /// to the same float. Compared as floats, as the player's runtime compares, that is no shorter and
        /// the start stays the parent. Compared unrounded, as a runtime doing float arithmetic in double
        /// precision would, it is shorter and replaces the start: another route. So it is handed back,
        /// although 1 is farther from the start than the parent.
        /// </summary>
        [Fact]
        public void AnOfferThatOnlyRoundsToTheSameDistanceIsATie()
        {
            var network = new GameNetwork();
            GameNode s = new GameNode(network, 0f, 0f, 0f), a = new GameNode(network, 1f, 0f, 0f),
                     t = new GameNode(network, 2f, 0f, 0f);
            Road(s, t, 16777216f);
            Road(s, a, 0.5f);
            Road(a, t, 16777215f);

            var game = new List<GameNode>();
            Assert.Equal(GameResult.Success, GamePathfinder.TryPathfind(network, s, t, game));
            Assert.Equal(new[] { 0, 2 }, Ids(game));
            Assert.Equal(GameResult.Success, GamePathfinder.TryPathfind(network, s, t, game, wide: true));
            Assert.Equal(new[] { 0, 1, 2 }, Ids(game));

            var heap = new List<GameNode>();
            Assert.Equal(RoadPathSearch.Outcome.Ambiguous, HeapPathfinder.TryPathfind(new RoadPathSearch(), network, s, t, heap));
        }

        [Fact]
        public void ATieOffTheRouteDoesNotMatter()
        {
            // 2 is offered 5 by both 0 and 1, but the route to 3 does not pass through it.
            var search = new RoadPathSearch();
            search.Begin(4, 0, 3);
            Assert.True(search.TryNext(out _));
            search.Relax(0, 1, 0.5f);
            search.Relax(0, 2, 5f);
            Assert.True(search.TryNext(out int taken));
            Assert.Equal(1, taken);
            search.Relax(1, 2, 4.5f);
            search.Relax(1, 3, 1f);

            var route = new List<int>();
            Assert.Equal(RoadPathSearch.Outcome.Found, Finish(search, route));
            Assert.Equal(new[] { 0, 1, 3 }, route);
        }

        [Fact]
        public void AnotherJunctionAsFarAsTheTargetIsHandedBack()
        {
            // The target is taken while 1, exactly as far, is still waiting: the game could take 1 first.
            var search = new RoadPathSearch();
            search.Begin(3, 0, 2);
            Assert.True(search.TryNext(out _));
            search.Relax(0, 2, 10f);
            search.Relax(0, 1, 10f);
            Assert.True(search.TryNext(out int taken));
            Assert.Equal(2, taken);

            var route = new List<int>();
            Assert.Equal(RoadPathSearch.Outcome.Ambiguous, Finish(search, route));
            Assert.Equal(new[] { 0, 2 }, route);
        }

        [Fact]
        public void ALoopRoadAndARoadOfNegativeLength()
        {
            var search = new RoadPathSearch();
            search.Begin(2, 0, 1);
            Assert.True(search.TryNext(out _));
            search.Relax(0, 0, 0f);          // a road from a junction back to itself never counts
            search.Relax(0, 1, float.NaN);   // nor does one of no length at all
            var route = new List<int>();
            Assert.Equal(RoadPathSearch.Outcome.NoConnection, Finish(search, route));

            search.Begin(2, 0, 1);
            Assert.True(search.TryNext(out _));
            search.Relax(0, 1, -1f);         // the game decides
            Assert.Equal(RoadPathSearch.Outcome.Ambiguous, Finish(search, route));
        }

        [Fact]
        public void ReusedForALargerNetwork()
        {
            var search = new RoadPathSearch();
            var route = new List<int>();
            search.Begin(2, 0, 1);
            Assert.True(search.TryNext(out _));
            search.Relax(0, 1, 1f);
            Assert.Equal(RoadPathSearch.Outcome.Found, Finish(search, route));

            // A junction with a road to every other, enough to grow every array, the heap included, and the
            // farthest of them the target.
            const int count = 5000;
            search.Begin(count, 0, count - 1);
            Assert.True(search.TryNext(out int first));
            for (int i = 1; i < count; i++) search.Relax(first, i, i);

            Assert.Equal(RoadPathSearch.Outcome.Found, Finish(search, route));
            Assert.Equal(new[] { 0, count - 1 }, route);
            Assert.Equal(count - 1f, search.Distance(count - 1));
            Assert.Equal(2f, search.Distance(2));
        }

        // ---- Against the game's loop -----------------------------------------------------------------

        sealed class Tally
        {
            public int Pairs, Found, NoConnection, Ambiguous, AmbiguousDiffering;

            public override string ToString() =>
                $"{Pairs} searches: {Found} found, {NoConnection} no connection, {Ambiguous} handed back " +
                $"({AmbiguousDiffering} of them where the game's loop, or a random order of ties, does return another route)";
        }

        /// <summary>
        /// One request both ways. A found route must be the game's, under its own sort and under every
        /// random order of ties; with <paramref name="state"/>, every junction's <c>dist</c> and
        /// <c>parent</c> must also end as the game's leave them.
        /// </summary>
        static void Compare(RoadPathSearch search, GameNetwork network, GameNode start, GameNode target, Tally tally,
                            int orders = 0, int seed = 0, bool wide = false, bool state = false)
        {
            tally.Pairs++;
            var heap = new List<GameNode>();
            RoadPathSearch.Outcome outcome = HeapPathfinder.TryPathfind(search, network, start, target, heap);

            float[] dist = network.nodes.Select(n => n.dist).ToArray();
            GameNode[] parent = network.nodes.Select(n => n.parent).ToArray();

            var game = new List<GameNode>();
            GameResult result = GamePathfinder.TryPathfind(network, start, target, game, null, wide);

            var others = new List<List<GameNode>>();
            for (int k = 0; k < orders; k++)
            {
                var other = new List<GameNode>();
                Assert.Equal(result, GamePathfinder.TryPathfind(network, start, target, other, new Random(seed * 31 + k), wide));
                others.Add(other);
            }

            switch (outcome)
            {
                case RoadPathSearch.Outcome.Found:
                    tally.Found++;
                    Assert.Equal(GameResult.Success, result);
                    Assert.Equal(Ids(game), Ids(heap));
                    foreach (List<GameNode> other in others) Assert.Equal(Ids(other), Ids(heap));

                    if (state)
                    {
                        // Re-run the game's own loop last, so that the junctions hold its state.
                        GamePathfinder.TryPathfind(network, start, target, game, null, wide);
                        for (int i = 0; i < network.nodes.Count; i++)
                        {
                            Assert.Equal(network.nodes[i].dist, dist[i]);
                            Assert.True(ReferenceEquals(network.nodes[i].parent, parent[i]), $"the parent of junction {i}");
                        }
                    }
                    break;

                case RoadPathSearch.Outcome.NoConnection:
                    tally.NoConnection++;
                    Assert.Equal(GameResult.NoConnection, result);
                    Assert.Empty(game);
                    Assert.Empty(heap);
                    break;

                default:
                    tally.Ambiguous++;
                    Assert.Equal(GameResult.Success, result);

                    // An equal-length tie: the game's route, and every other order's, as long to the bit.
                    Assert.Equal(target.dist, search.Distance(target.id));
                    Assert.Equal(start.id, heap[0].id);
                    Assert.Equal(target.id, heap[heap.Count - 1].id);

                    if (!game.SequenceEqual(heap) || others.Any(o => !o.SequenceEqual(heap))) tally.AmbiguousDiffering++;
                    break;
            }
        }

        static int[] Ids(List<GameNode> route) => route.Select(n => n.id).ToArray();

        static IEnumerable<(GameNode Start, GameNode Target)> Pairs(GameNetwork network, int count, int seed)
        {
            var rng = new Random(seed);
            for (int i = 0; i < count; i++)
                yield return (network.nodes[rng.Next(network.nodes.Count)], network.nodes[rng.Next(network.nodes.Count)]);
        }

        /// <summary>
        /// Swiss Alps' pathing roads, as Map Forge writes them, through the game's own network building
        /// (1,801 roads, 1,338 junctions in the current data). Needs the sibling NOMapForge checkout with
        /// its generated output, or NOMAPFORGE set to one; passes without checking anything where there is
        /// none, CI among them.
        /// </summary>
        [Fact]
        public void MatchesTheGameOnSwissAlpsRoads()
        {
            string file = SwissAlpsRoads();
            if (file == null)
            {
                _output.WriteLine("NOMapForge/out/swissalps/roads_path.bin not found; nothing checked");
                return;
            }

            GameNetwork network = GameNetwork.From(RoadData.Read(File.ReadAllBytes(file)));
            _output.WriteLine($"{file}: {network.roads.Count} roads, {network.nodes.Count} junctions");

            var search = new RoadPathSearch();
            var tally = new Tally();
            foreach (var (start, target) in Pairs(network, 150, 1))
                Compare(search, network, start, target, tally, state: true);
            _output.WriteLine("float compare: " + tally);
            Assert.True(tally.Found > tally.Pairs / 2);

            var wide = new Tally();
            foreach (var (start, target) in Pairs(network, 50, 2))
                Compare(search, network, start, target, wide, wide: true);
            _output.WriteLine("unrounded compare: " + wide);
        }

        [Fact]
        public void MatchesTheGameOnRandomRoadNetworks()
        {
            var search = new RoadPathSearch();
            var tally = new Tally();
            for (int seed = 1; seed <= 12; seed++)
            {
                GameNetwork network = RandomRoads(seed, towns: 40 + 15 * seed);
                foreach (var (start, target) in Pairs(network, 25, seed))
                    Compare(search, network, start, target, tally, orders: 2, seed: seed, wide: seed % 3 == 0, state: true);
            }

            _output.WriteLine(tally.ToString());
            Assert.True(tally.Found > tally.Pairs / 2);
            Assert.True(tally.NoConnection > 0);
        }

        /// <summary>
        /// Networks full of exact ties — a grid of equal roads, and one-way graphs with whole-metre lengths,
        /// roads of no length, loops, doubled roads and neighbours the network does not list — where the
        /// game's sort and a random order of ties often return different routes. Every one of those must be
        /// handed back; every route the heap does give must be the one all of them give.
        /// </summary>
        [Fact]
        public void HandsBackEveryRouteATieCouldChange()
        {
            var search = new RoadPathSearch();
            var tally = new Tally();

            for (int seed = 1; seed <= 6; seed++)
            {
                GameNetwork grid = Grid(seed, 9 + seed, 7 + seed);
                foreach (var (start, target) in Pairs(grid, 30, seed))
                    Compare(search, grid, start, target, tally, orders: 6, seed: seed);
            }

            for (int seed = 1; seed <= 400; seed++)
            {
                GameNetwork tangle = Tangle(seed, 6 + seed % 20);
                foreach (var (start, target) in Pairs(tangle, 10, seed))
                    Compare(search, tangle, start, target, tally, orders: 6, seed: seed, wide: seed % 2 == 0);
            }

            _output.WriteLine(tally.ToString());
            Assert.True(tally.Found > 0);
            Assert.True(tally.AmbiguousDiffering > 0);
        }

        /// <summary>
        /// Two chains of roads of no length from the start, both 5 m long: S→p→v and S→r→u→v, then v→t. The
        /// heap takes p, r, then v (through p), and only then u, whose offer to v comes too late to count
        /// here. The game's sort may take r, u and v first instead, and then v's parent is u: another
        /// route, exactly as long.
        /// </summary>
        [Fact]
        public void AnEqualOfferThatComesTooLateHereCanWinInTheGame()
        {
            var network = new GameNetwork();
            GameNode s = new GameNode(network, 0f, 0f, 0f), p = new GameNode(network, 1f, 0f, 0f),
                     r = new GameNode(network, 2f, 0f, 0f), v = new GameNode(network, 3f, 0f, 0f),
                     u = new GameNode(network, 4f, 0f, 0f), t = new GameNode(network, 5f, 0f, 0f);
            Road(s, p, 5f);
            Road(s, r, 5f);
            Road(p, v, 0f);
            Road(r, u, 0f);
            Road(u, v, 0f);
            Road(v, t, 1f);

            Assert.Equal(2, GameRoutes(network, s, t).Length);

            var heap = new List<GameNode>();
            Assert.Equal(RoadPathSearch.Outcome.Ambiguous, HeapPathfinder.TryPathfind(new RoadPathSearch(), network, s, t, heap));
        }

        /// <summary>A one-way connection, as the game's loop reads <c>connectionsLookup</c>.</summary>
        static void Road(GameNode from, GameNode to, float length) => from.connectionsLookup.Add(new GameRoad { length = length }, to);

        /// <summary>Every route the game's loop returns, under its own sort and 64 random orders of ties.</summary>
        static string[] GameRoutes(GameNetwork network, GameNode start, GameNode target)
        {
            var routes = new SortedSet<string>(StringComparer.Ordinal);
            var game = new List<GameNode>();
            for (int k = -1; k < 64; k++)
            {
                GamePathfinder.TryPathfind(network, start, target, game, k < 0 ? null : new Random(k));
                routes.Add(string.Join(",", Ids(game)));
            }

            return routes.ToArray();
        }

        // ---- Networks ----------------------------------------------------------------------------------

        static string SwissAlpsRoads()
        {
            var candidates = new List<string>();
            string configured = Environment.GetEnvironmentVariable("NOMAPFORGE");
            if (!string.IsNullOrEmpty(configured)) candidates.Add(Path.Combine(configured, "out", "swissalps", "roads_path.bin"));
            for (DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                candidates.Add(Path.Combine(dir.FullName, "NOMapForge", "out", "swissalps", "roads_path.bin"));
            return candidates.FirstOrDefault(File.Exists);
        }

        /// <summary>Towns joined to their nearest neighbours by winding roads, with ends a few metres off
        /// the town so that they fuse as the game fuses them, and some dead ends.</summary>
        static GameNetwork RandomRoads(int seed, int towns)
        {
            var rng = new Random(seed);
            var places = new List<(float X, float Y, float Z)>();
            for (int i = 0; i < towns; i++)
                places.Add(((float)(rng.NextDouble() * 40000), (float)(rng.NextDouble() * 900), (float)(rng.NextDouble() * 40000)));

            float Jitter() => (float)(rng.NextDouble() * 12 - 6);

            var records = new List<RoadRecord>();
            var joined = new HashSet<(int, int)>();
            for (int i = 0; i < towns; i++)
            {
                var nearest = Enumerable.Range(0, towns).Where(j => j != i)
                    .OrderBy(j => GameMath.SquareDistance(places[i], places[j])).Take(2 + rng.Next(3));
                foreach (int j in nearest)
                {
                    if (!joined.Add((Math.Min(i, j), Math.Max(i, j)))) continue;

                    // Some towns lose a road, so that the network falls apart in places.
                    if (rng.NextDouble() < 0.03) continue;

                    var points = new List<float>();
                    void Add(float x, float y, float z) { points.Add(x); points.Add(y); points.Add(z); }

                    Add(places[i].X + Jitter(), places[i].Y, places[i].Z + Jitter());
                    int bends = rng.Next(4);
                    for (int b = 1; b <= bends; b++)
                    {
                        float t = b / (bends + 1f);
                        Add(places[i].X + (places[j].X - places[i].X) * t + Jitter() * 50,
                            places[i].Y + (places[j].Y - places[i].Y) * t,
                            places[i].Z + (places[j].Z - places[i].Z) * t + Jitter() * 50);
                    }

                    Add(places[j].X + Jitter(), places[j].Y, places[j].Z + Jitter());
                    records.Add(new RoadRecord { Points = points.ToArray() });
                }

                if (rng.NextDouble() < 0.1)
                    records.Add(new RoadRecord { Points = new[] { places[i].X, places[i].Y, places[i].Z, places[i].X + 400, places[i].Y, places[i].Z + 300 } });
            }

            return GameNetwork.From(records);
        }

        /// <summary>A street grid of 100 m blocks with a few streets missing: every route has many twins of
        /// exactly its length.</summary>
        static GameNetwork Grid(int seed, int width, int height)
        {
            var rng = new Random(seed);
            var records = new List<RoadRecord>();
            for (int x = 0; x < width; x++)
                for (int z = 0; z < height; z++)
                {
                    if (x + 1 < width && rng.NextDouble() < 0.9)
                        records.Add(new RoadRecord { Points = new[] { x * 100f, 0f, z * 100f, (x + 1) * 100f, 0f, z * 100f } });
                    if (z + 1 < height && rng.NextDouble() < 0.9)
                        records.Add(new RoadRecord { Points = new[] { x * 100f, 0f, z * 100f, x * 100f, 0f, (z + 1) * 100f } });
                }

            return GameNetwork.From(records);
        }

        /// <summary>
        /// A graph built by hand rather than from roads: one-way connections of whole metres, half of them of
        /// no length at all, loops, doubled roads, and neighbours that are not in the network's list (a
        /// junction left over from an older network), none of which <c>RegenerateNetwork</c> would make but
        /// all of which the game's loop accepts.
        /// </summary>
        static GameNetwork Tangle(int seed, int count)
        {
            var rng = new Random(seed);
            var network = new GameNetwork();
            for (int i = 0; i < count; i++) new GameNode(network, i, 0f, 0f);

            var stranger = new GameNetwork();
            var outsider = new GameNode(stranger, -1f, 0f, 0f);

            foreach (GameNode node in network.nodes)
            {
                int links = rng.Next(4);
                for (int k = 0; k < links; k++)
                {
                    double pick = rng.NextDouble();
                    GameNode other = pick < 0.05 ? outsider : pick < 0.1 ? node : network.nodes[rng.Next(count)];
                    var road = new GameRoad { length = rng.NextDouble() < 0.5 ? 0f : rng.Next(1, 4) };
                    node.connectionsLookup.Add(road, other);
                    if (rng.NextDouble() < 0.5 && other != outsider && other != node)
                        other.connectionsLookup.Add(road, node);
                }
            }

            return network;
        }
    }
}
