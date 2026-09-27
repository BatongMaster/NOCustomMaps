using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace CustomMaps.Tests
{
    public class CatalogMergeTests
    {
        sealed class Entry
        {
            public string Name;
            public Entry(string name) => Name = name;
        }

        static string KeyOf(Entry e) => e?.Name;

        static Entry[] Vanilla() => new[] { new Entry("Terrain1"), new Entry("Terrain_naval") };

        // The mission editor walks a fixed-size serialized button array by index and
        // reads mapLoader.Maps[i]. Moving index 0 or 1 mislabels the vanilla buttons,
        // and does so silently.
        [Fact]
        public void VanillaIndicesNeverMove()
        {
            Entry[] existing = Vanilla();
            var mine = new List<Entry> { new Entry("cm.kastellan.a1b2c3d4") };

            Entry[] merged = CatalogMerge.Append(existing, mine, KeyOf);

            Assert.Equal(3, merged.Length);
            Assert.Equal("Terrain1", merged[0].Name);
            Assert.Equal("Terrain_naval", merged[1].Name);
            Assert.Equal("cm.kastellan.a1b2c3d4", merged[2].Name);
        }

        [Fact]
        public void IsIdempotent()
        {
            Entry[] state = Vanilla();
            var mine = new List<Entry> { new Entry("cm.kastellan.a1b2c3d4") };

            state = CatalogMerge.Append(state, mine, KeyOf);
            Entry[] afterFirst = state;

            for (int i = 0; i < 5; i++) state = CatalogMerge.Append(state, mine, KeyOf);

            Assert.Equal(3, state.Length);
            // A no-op must return the same instance so the caller can skip logging.
            Assert.Same(afterFirst, state);
        }

        // The registrar is called from a prefix on every array consumer, so it runs
        // many times per session and may be handed a fresh MapDetails after a scene
        // reload. Identity-based dedupe would double-append.
        [Fact]
        public void DedupesByKeyNotByReference()
        {
            Entry[] state = Vanilla();

            state = CatalogMerge.Append(state, new[] { new Entry("cm.kastellan.a1b2c3d4") }, KeyOf);
            state = CatalogMerge.Append(state, new[] { new Entry("cm.kastellan.a1b2c3d4") }, KeyOf);

            Assert.Equal(3, state.Length);
        }

        [Fact]
        public void TwoBundlesClaimingOneNameResolveDeterministically()
        {
            var first = new Entry("cm.kastellan.a1b2c3d4");
            var second = new Entry("cm.kastellan.a1b2c3d4");

            Entry[] merged = CatalogMerge.Append(Vanilla(), new[] { first, second }, KeyOf);

            Assert.Equal(3, merged.Length);
            Assert.Same(first, merged[2]);           // first in candidate order wins
        }

        [Fact]
        public void AppendsInCandidateOrder()
        {
            var mine = new[]
            {
                new Entry("cm.alpha.00000000"),
                new Entry("cm.bravo.11111111"),
                new Entry("cm.charlie.22222222"),
            };

            Entry[] merged = CatalogMerge.Append(Vanilla(), mine, KeyOf);

            Assert.Equal(
                new[] { "Terrain1", "Terrain_naval", "cm.alpha.00000000", "cm.bravo.11111111", "cm.charlie.22222222" },
                merged.Select(e => e.Name));
        }

        // The game's arrays can contain an unassigned slot, and a key selector that
        // dereferences it will throw. CanLoad is on the join path; a merge that throws
        // there disconnects the player.
        [Fact]
        public void SurvivesNullsOnBothSides()
        {
            var existing = new[] { new Entry("Terrain1"), null, new Entry("Terrain_naval") };
            var mine = new List<Entry> { null, new Entry("cm.kastellan.a1b2c3d4"), new Entry(null) };

            Entry[] merged = CatalogMerge.Append(existing, mine, KeyOf);

            Assert.Equal(4, merged.Length);
            Assert.Equal("cm.kastellan.a1b2c3d4", merged[3].Name);
        }

        [Fact]
        public void SurvivesAKeySelectorThatDereferencesNull()
        {
            var existing = new Entry[] { null };
            Entry[] merged = CatalogMerge.Append(
                existing,
                new[] { new Entry("cm.kastellan.a1b2c3d4") },
                e => e.Name.ToUpperInvariant());          // throws on the null slot

            Assert.Equal(2, merged.Length);
        }

        [Fact]
        public void EmptyInputsAreHandled()
        {
            Assert.Empty(CatalogMerge.Append<Entry>(null, null, KeyOf));
            Assert.Empty(CatalogMerge.Append(Array.Empty<Entry>(), new List<Entry>(), KeyOf));

            Entry[] vanilla = Vanilla();
            Assert.Same(vanilla, CatalogMerge.Append(vanilla, null, KeyOf));
        }

        [Fact]
        public void RejectsANullKeySelector()
        {
            Assert.Throws<ArgumentNullException>(() => CatalogMerge.Append(Vanilla(), new List<Entry>(), null));
        }

        [Fact]
        public void KeysOfReportsNullSlots()
        {
            var existing = new[] { new Entry("Terrain1"), null };
            Assert.Equal(new[] { "Terrain1", "<null>" }, CatalogMerge.KeysOf(existing, KeyOf));
            Assert.Empty(CatalogMerge.KeysOf<Entry>(null, KeyOf));
        }
    }
}
