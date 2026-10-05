using System;
using System.Collections.Generic;
using Xunit;

namespace CustomMaps.Tests
{
    public class AncestorNamesTests
    {
        /// <summary>A transform, as far as the walk is concerned: a name and a parent.</summary>
        sealed class Node
        {
            public readonly string Name;
            public readonly Node Parent;

            public Node(string name, Node parent = null)
            {
                Name = name;
                Parent = parent;
            }
        }

        /// <summary>A node that claims to equal every other, as a Unity object can claim to equal
        /// null: the cache must still tell nodes apart by reference.</summary>
        sealed class Lookalike
        {
            public readonly string Name;
            public readonly Lookalike Parent;

            public Lookalike(string name, Lookalike parent = null)
            {
                Name = name;
                Parent = parent;
            }

            public override bool Equals(object obj) => obj is Lookalike;
            public override int GetHashCode() => 0;
        }

        int _reads;

        AncestorNames<Node> Sections(params string[] names)
            => new AncestorNames<Node>(n => n.Parent, n => { _reads++; return n.Name; }, names);

        /// <summary>The walk MapFixups.Under did, per question.</summary>
        static bool Walk(Node node, string name)
        {
            for (Node at = node; at != null; at = at.Parent)
                if (at.Name == name) return true;

            return false;
        }

        // The map as BorrowMaterials sees it: SwissAlps/Roads/Bridges/bridge_3/deck, a terrain tile's
        // LOD, a lake, a prop.
        readonly Node _root, _roads, _bridges, _deck, _tile, _lod, _lake, _prop;

        public AncestorNamesTests()
        {
            _root = new Node("SwissAlps");
            _roads = new Node("Roads", _root);
            _bridges = new Node("Bridges", _roads);
            _deck = new Node("deck", new Node("bridge_3", _bridges));
            _tile = new Node("tile_12_7", new Node("Terrain", _root));
            _lod = new Node("tile_12_7_LOD1", _tile);
            _lake = new Node("sheet", new Node("Lake_Zurich", new Node("Water", _root)));
            _prop = new Node("castle", new Node("Props", _root));
        }

        [Fact]
        public void ANameOnTheNodeItselfCounts()
        {
            AncestorNames<Node> sections = Sections("Bridges");

            Assert.True(sections.Under(_bridges, "Bridges"));
        }

        [Fact]
        public void ANameAnyWayUpCounts()
        {
            AncestorNames<Node> sections = Sections("Bridges", "Roads", "Water", "Props");

            Assert.True(sections.Under(_deck, "Bridges"));
            Assert.True(sections.Under(_deck, "Roads"));
            Assert.False(sections.Under(_deck, "Water"));
            Assert.True(sections.Under(_lake, "Water"));
            Assert.True(sections.Under(_prop, "Props"));
            Assert.False(sections.Under(_lod, "Roads"));
        }

        // Ordinal, as == on strings is: the generator's roots are named exactly.
        [Fact]
        public void NamesAreComparedExactly()
        {
            AncestorNames<Node> sections = Sections("roads", "Road");

            Assert.Equal(0, sections.Mask(_deck));
        }

        [Fact]
        public void TheMaskHasABitPerNameInTheOrderGiven()
        {
            AncestorNames<Node> sections = Sections("Water", "Roads", "Bridges");

            Assert.Equal(0b110, sections.Mask(_deck));
            Assert.Equal(0b001, sections.Mask(_lake));
            Assert.Equal(0b000, sections.Mask(_tile));
            Assert.Equal(1 << 2, sections.Bit("Bridges"));
        }

        // The point of it: however many renderers sit under a parent and whatever is asked about each,
        // every transform is named once.
        [Fact]
        public void EachNodeIsNamedOnce()
        {
            AncestorNames<Node> sections = Sections("Bridges", "Tunnels", "Water", "Props");

            var leaves = new List<Node>();
            for (int i = 0; i < 100; i++) leaves.Add(new Node($"deck_{i}", _bridges));

            foreach (Node leaf in leaves)
            {
                sections.Under(leaf, "Bridges");
                sections.Under(leaf, "Tunnels");
                sections.Under(leaf, "Water");
                sections.Under(leaf, "Props");
            }

            // 100 decks, Bridges, Roads and the root, where the plain walk read 1,600: four walks per
            // deck, each of four names up to the root.
            Assert.Equal(103, _reads);
            Assert.Equal(103, sections.NamesRead);
        }

        [Fact]
        public void AnAnsweredNodeIsNotNamedAgain()
        {
            AncestorNames<Node> sections = Sections("Roads");

            sections.Mask(_deck);
            int afterFirst = _reads;
            sections.Mask(_deck);
            sections.Mask(_bridges);
            sections.Mask(_root);

            Assert.Equal(5, afterFirst);
            Assert.Equal(afterFirst, _reads);
        }

        [Fact]
        public void NothingIsUnderANullNode()
        {
            AncestorNames<Node> sections = Sections("Roads");

            Assert.Equal(0, sections.Mask(null));
            Assert.False(sections.Under(null, "Roads"));
            Assert.Equal(0, _reads);
        }

        [Fact]
        public void AskingForANameNotLookedForThrows()
        {
            AncestorNames<Node> sections = Sections("Roads");

            Assert.Throws<ArgumentException>(() => sections.Under(_deck, "Bridges"));
        }

        [Fact]
        public void AtMostThirtyTwoNamesFit()
        {
            var names = new string[33];
            for (int i = 0; i < names.Length; i++) names[i] = "n" + i;

            Assert.Throws<ArgumentException>(() => Sections(names));
            Assert.Equal(1 << 31, Sections(names[..32]).Bit("n31"));
        }

        // Unity's objects override Equals; the cache keys by reference, so two transforms are never
        // taken for one.
        [Fact]
        public void NodesAreToldApartByReference()
        {
            var root = new Lookalike("SwissAlps");
            var roads = new Lookalike("ribbon", new Lookalike("Roads", root));
            var tile = new Lookalike("tile", new Lookalike("Terrain", root));

            var sections = new AncestorNames<Lookalike>(n => n.Parent, n => n.Name, "Roads");

            Assert.True(sections.Under(roads, "Roads"));
            Assert.False(sections.Under(tile, "Roads"));
        }

        // Against the walk it replaces, over random hierarchies and random questions in random order.
        [Fact]
        public void AgreesWithThePlainWalk()
        {
            string[] names = { "Roads", "Bridges", "Tunnels", "Water", "Props", "Airfields" };
            var random = new Random(20261004);

            for (int round = 0; round < 50; round++)
            {
                var nodes = new List<Node> { new Node("root") };
                for (int i = 0; i < 300; i++)
                {
                    Node parent = nodes[random.Next(nodes.Count)];
                    string name = random.Next(4) == 0 ? names[random.Next(names.Length)] : $"node_{i}";
                    nodes.Add(new Node(name, parent));
                }

                AncestorNames<Node> sections = Sections(names);
                for (int q = 0; q < 1000; q++)
                {
                    Node node = nodes[random.Next(nodes.Count)];
                    string name = names[random.Next(names.Length)];
                    Assert.Equal(Walk(node, name), sections.Under(node, name));
                }

                Assert.True(sections.NamesRead <= nodes.Count);
            }
        }
    }
}
