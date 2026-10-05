using System;
using System.Collections.Generic;

namespace CustomMaps
{
    /// <summary>
    /// The search inside the game's <c>RoadPathfinder.TryPathfind</c>, the same steps on a heap. Free of
    /// game types, so the tests run it side by side with a copy of the game's own loop; the patch that
    /// feeds it a live road network is in <c>Patches/RoadPathfinderPatches.cs</c>.
    ///
    /// From the decompiled game, the search is Dijkstra's over the network's junctions
    /// (<c>RoadNetwork.nodes</c>), each road weighed by its <c>length</c> and nothing else — no road type,
    /// speed or bridge enters it:
    ///
    ///     unvisitedSet.AddRange(network.nodes);
    ///     while (unvisitedSet.Count > 0)
    ///     {
    ///         unvisitedSet.Sort((a, b) => a.dist.CompareTo(b.dist));
    ///         closestNode = unvisitedSet[0];
    ///         unvisitedSet.Remove(closestNode);
    ///         foreach ((Road road, Node other) in closestNode.connectionsLookup)
    ///             if (unvisitedSet.Contains(other) &amp;&amp; closestNode.dist + road.length &lt; other.dist)
    ///                 { other.dist = closestNode.dist + road.length; other.parent = closestNode; }
    ///         if (closestNode.dist == float.MaxValue) → NoConnection, results cleared
    ///         if (closestNode == target) break;
    ///     }
    ///     results = target, its parent, its parent's parent … reversed
    ///
    /// It sorts every junction still unvisited at every step, and <c>Contains</c> and <c>Remove</c> walk
    /// the list too, so one search costs O(J² log J) for J junctions. That is little on Heartland's 506.
    /// On Swiss Alps' 1,338 the loop alone takes 27 ms a search in the player's Mono (51 ms at worst), and
    /// every ground unit searches again whenever its destination moves more than 10 m
    /// (<c>PathfindingAgent.Pathfind</c>), so a column given new orders stalls the frame. A heap takes the
    /// same junctions in the same order of distance for O((J + R) log J), R the roads: 0.09 ms for the
    /// same loop there. (The game's opening, which finds the road nearest each end and which the patch
    /// still runs, brings a whole request to about 0.24 ms, against 33 ms.)
    ///
    /// <para><b>Same answer, or none.</b> The two differ in one thing only: which of two junctions at
    /// exactly the same distance is taken first. The game's <c>List.Sort</c> is unstable, so its choice
    /// comes from the sort's internals and the list's history, and no heap can reproduce it without doing
    /// the sort. Every junction nearer the start than the target gets the same distance in any order.
    /// The order decides a junction's <c>parent</c> only where two neighbours offer it exactly the same
    /// distance, to the last bit of the float, and the game could take either of them first: a later
    /// offer replaces an earlier one only when strictly shorter, and of two neighbours at different
    /// distances the nearer is taken first in any order, so only neighbours exactly as far from the start
    /// as each other can trade places. So every such tie is noted as it happens (an equal offer made after
    /// the junction was taken included, since another order could have made it in time), and if one lies
    /// on the route found, or another junction is exactly as far away as the target, the search answers
    /// <see cref="Outcome.Ambiguous"/> and the patch lets the game run its own loop for that request.
    /// Every route this class does give is therefore the one the game's loop would have found, on any
    /// runtime; equal-length routes come back as the game's own choice.</para>
    ///
    /// <para><b>Junctions off the route.</b> The route, and every junction's <c>dist</c> and
    /// <c>parent</c> along it, are the game's. Off the route they are those of some order of taking
    /// equal-distance junctions, not necessarily the one the game's sort takes: ties there are not
    /// checked, and the game stops at the target, so a junction exactly as far away may or may not have
    /// been taken before it and offered its neighbours their distances. On a runtime that compares the
    /// unrounded sum (below) an off-route parent can even differ from every order's. Nothing in the game
    /// reads these two fields but <c>TryPathfind</c> and its sort.</para>
    ///
    /// <para><b>Floats.</b> Each offer is rounded to a float before it is compared
    /// (<c>(float)(dist + length)</c>). The game's 64-bit Mono runtime, its own <c>mono-2.0-bdwgc.dll</c>
    /// and corlib hosted outside the game with default JIT options, did that float arithmetic in single
    /// precision, as CoreCLR does, so this should be the game's own comparison; how Unity starts the
    /// runtime in the player was not reproduced. A runtime that compared the unrounded sum, as 32-bit Mono
    /// does, could let an offer that only rounds to a junction's distance replace its parent; that offer
    /// is noted as a tie too (<see cref="CouldWin"/>), so the answer holds there as well.</para>
    ///
    /// <para>Not thread-safe: one instance, one search at a time, as the game's own static
    /// <c>unvisitedSet</c> already requires. Its arrays are kept between searches and only grow.</para>
    /// </summary>
    internal sealed class RoadPathSearch
    {
        /// <summary>
        /// The fingerprint (<see cref="IlFingerprint"/>) of the game methods in <see cref="GameMethods"/>,
        /// in the build this search and the patch's copy of the method's opening were checked against. The
        /// patch is applied only while the running game's fingerprint is this one, so a game update that
        /// changes how roads are searched turns the patch off rather than overriding the game with an older
        /// copy.
        /// </summary>
        public const ulong VerifiedGameMethods = 0x0674251804DFFB47UL;

        /// <summary>
        /// What <see cref="VerifiedGameMethods"/> covers, as namespace, type and method, each
        /// <see cref="IlFingerprint.OfMethod"/> combined in this order: the search itself, with the
        /// comparison it sorts by, and the two methods that clear the junctions before it, since the
        /// patch writes back <c>float.MaxValue</c> and no parent for every junction it never reached, as
        /// they leave them today.
        /// </summary>
        public static readonly string[][] GameMethods =
        {
            new[] { "", "RoadPathfinder", "TryPathfind" },
            new[] { "RoadPathfinding", "RoadNetwork", "ClearPathfindingData" },
            new[] { "RoadPathfinding", "Node", "ClearPathfindingData" },
        };

        public enum Outcome
        {
            /// <summary>A route to the target, the same the game's loop finds.</summary>
            Found,

            /// <summary>The target cannot be reached; the game says <c>NoConnection</c> and clears its
            /// results.</summary>
            NoConnection,

            /// <summary>An exact tie that the game's unstable sort could break either way bears on the
            /// answer. The caller must let the game's own loop run.</summary>
            Ambiguous,
        }

        const byte Unseen = 0, Open = 1, Done = 2;

        float[] _dist = new float[0];
        int[] _parent = new int[0];
        byte[] _state = new byte[0];

        /// <summary>A junction offered its current distance by two neighbours the game could take in
        /// either order, or by one taken after it at the same distance: its parent could have gone the
        /// other way.</summary>
        bool[] _tie = new bool[0];

        float[] _heapKey = new float[64];
        int[] _heapNode = new int[64];
        int _heapCount;

        int _count, _start, _target;
        bool _found, _ambiguous;

        /// <summary>Junctions in the search.</summary>
        public int Count => _count;

        /// <summary>A junction's <c>dist</c> after the search: <c>float.MaxValue</c> if never reached, the
        /// start's 0, and otherwise the shortest offer it received. The game's own on the route; off it,
        /// as some order of taking equal-distance junctions leaves it (see the class notes).</summary>
        public float Distance(int node) => _dist[node];

        /// <summary>A junction's <c>parent</c> after the search, or -1 for none. The game's own on the
        /// route; off it, as some order of taking equal-distance junctions leaves it.</summary>
        public int Parent(int node) => _parent[node];

        /// <summary>Starts a search over junctions 0 … <paramref name="count"/> - 1, numbered as the game
        /// lists them (<c>RoadNetwork.nodes</c>, where each junction's <c>id</c> is its index).</summary>
        public void Begin(int count, int start, int target)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            if ((uint)start >= (uint)count) throw new ArgumentOutOfRangeException(nameof(start));
            if ((uint)target >= (uint)count) throw new ArgumentOutOfRangeException(nameof(target));

            if (_dist.Length < count)
            {
                int size = Math.Max(count, _dist.Length * 2);
                _dist = new float[size];
                _parent = new int[size];
                _state = new byte[size];
                _tie = new bool[size];
            }

            for (int i = 0; i < count; i++)
            {
                _dist[i] = float.MaxValue;
                _parent[i] = -1;
                _state[i] = Unseen;
                _tie[i] = false;
            }

            _count = count;
            _start = start;
            _target = target;
            _found = false;
            _ambiguous = false;
            _heapCount = 0;

            _dist[start] = 0f;
            _state[start] = Open;
            Push(0f, start);
        }

        /// <summary>
        /// The next junction to take, nearest first, as the game's loop takes them; the caller then offers
        /// each of its connections, in the game's order, through <see cref="Relax"/>. The target is handed
        /// out too, because the game relaxes its connections before it stops. False once the target has
        /// been taken, when nothing reachable is left, or once the answer is known to be ambiguous.
        /// </summary>
        public bool TryNext(out int node)
        {
            node = -1;
            if (_found || _ambiguous) return false;

            while (_heapCount > 0)
            {
                PopMin(out float key, out int next);

                // A junction is pushed again each time its distance drops; only the entry that still
                // carries its distance is live.
                if (_state[next] == Done || key != _dist[next]) continue;

                _state[next] = Done;

                if (next == _target)
                {
                    _found = true;

                    // Another junction exactly as far as the target: the game's sort could take it first,
                    // and through a road that rounds to nothing at this distance it could even become a
                    // parent on the route. Rare enough to hand back whole.
                    if (PeekLive(out float following) && following == key) _ambiguous = true;
                }

                node = next;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Offers <paramref name="to"/> the route through <paramref name="from"/>, the junction just taken,
        /// along a road of <paramref name="length"/> metres: the body of the game's inner loop, for a
        /// neighbour that is one of the network's junctions. A neighbour the network does not list is never
        /// in the game's <c>unvisitedSet</c>, so the caller leaves it out.
        /// </summary>
        public void Relax(int from, int to, float length)
        {
            // The game removes the junction it takes before it looks at its roads, so a road from a
            // junction back to itself never counts.
            if (to == from) return;

            // A road of negative length would break the order of distance both searches rely on. Road
            // lengths are sums of distances, so it cannot happen; if it ever does, the game decides. A NaN
            // offer is never less than anything, here or in the game.
            if (length < 0f) { _ambiguous = true; return; }

            float offer = (float)(_dist[from] + length);

            if (_state[to] == Done)
            {
                // Taken already, so the game no longer looks at it. But at the same distance the two
                // could have been taken the other way round, and this offer would then have been made in
                // time and could have won.
                if (offer == _dist[to] && to != _start && CouldWin(from, to, length)) _tie[to] = true;
                return;
            }

            if (offer < _dist[to])
            {
                _dist[to] = offer;
                _parent[to] = from;
                _tie[to] = false;
                _state[to] = Open;
                Push(offer, to);
            }
            else if (offer == _dist[to] && _parent[to] != from && CouldWin(from, to, length))
            {
                // Two neighbours offering exactly the same distance: whichever the game takes first is the
                // parent, and that is the unstable sort's choice. A second road from the same parent is
                // not a tie.
                _tie[to] = true;
            }
        }

        /// <summary>
        /// Whether an offer of exactly <paramref name="to"/>'s distance, from <paramref name="from"/>,
        /// which is not its parent, could have made it the parent had the game taken junctions at equal
        /// distance in another order. The parent's offer came first here, and an equal offer never replaces
        /// one already made, so only a junction the game could take before the parent can: one exactly as
        /// far from the start. A junction farther away than the parent (the only other kind, since the
        /// parent was taken first) is taken after it in any order, so its offer comes second in all of them
        /// and loses. Except on a runtime that compares the unrounded sum, as 32-bit Mono does: there an
        /// offer that is less than the distance before it is rounded to the same float does replace the
        /// parent, so it counts as a tie whatever the distances.
        /// </summary>
        bool CouldWin(int from, int to, float length)
        {
            int parent = _parent[to];
            if (parent < 0 || _dist[parent] == _dist[from]) return true;
            return (double)_dist[from] + length < _dist[to];
        }

        /// <summary>
        /// The answer, once <see cref="TryNext"/> has returned false. For <see cref="Outcome.Found"/>,
        /// <paramref name="path"/> is the route from the start to the target as the game lists it in its
        /// results. For <see cref="Outcome.Ambiguous"/> it is this search's own equal-length route (the
        /// tests compare it), which the patch never uses.
        /// </summary>
        public Outcome Finish(List<int> path)
        {
            path.Clear();
            if (!_found) return _ambiguous ? Outcome.Ambiguous : Outcome.NoConnection;

            bool tied = _ambiguous;
            for (int node = _target; node >= 0; node = _parent[node])
            {
                // The start is taken first in any order and never gets a parent.
                if (node != _start && _tie[node]) tied = true;
                path.Add(node);
            }

            path.Reverse();
            return tied ? Outcome.Ambiguous : Outcome.Found;
        }

        void Push(float key, int node)
        {
            if (_heapCount == _heapKey.Length)
            {
                Array.Resize(ref _heapKey, _heapCount * 2);
                Array.Resize(ref _heapNode, _heapCount * 2);
            }

            int i = _heapCount++;
            while (i > 0)
            {
                int up = (i - 1) >> 1;
                if (!(key < _heapKey[up])) break;
                _heapKey[i] = _heapKey[up];
                _heapNode[i] = _heapNode[up];
                i = up;
            }

            _heapKey[i] = key;
            _heapNode[i] = node;
        }

        void PopMin(out float key, out int node)
        {
            key = _heapKey[0];
            node = _heapNode[0];

            int last = --_heapCount;
            if (last == 0) return;

            float lastKey = _heapKey[last];
            int lastNode = _heapNode[last];
            int i = 0;
            while (true)
            {
                int child = 2 * i + 1;
                if (child >= last) break;
                if (child + 1 < last && _heapKey[child + 1] < _heapKey[child]) child++;
                if (!(_heapKey[child] < lastKey)) break;
                _heapKey[i] = _heapKey[child];
                _heapNode[i] = _heapNode[child];
                i = child;
            }

            _heapKey[i] = lastKey;
            _heapNode[i] = lastNode;
        }

        /// <summary>The smallest distance still waiting, after dropping the stale entries above it.</summary>
        bool PeekLive(out float key)
        {
            while (_heapCount > 0)
            {
                int node = _heapNode[0];
                if (_state[node] != Done && _heapKey[0] == _dist[node])
                {
                    key = _heapKey[0];
                    return true;
                }

                PopMin(out _, out _);
            }

            key = 0f;
            return false;
        }
    }
}
