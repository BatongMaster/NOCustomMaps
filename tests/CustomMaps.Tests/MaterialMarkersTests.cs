using Xunit;

namespace CustomMaps.Tests
{
    public class MaterialMarkersTests
    {
        // The names NOMapForge's bundles wear, and the surface BorrowMaterials has always given each.
        [Theory]
        [InlineData("__BORROW__Terrain", MaterialSlot.Terrain)]
        [InlineData("__BORROW__Water", MaterialSlot.Water)]
        [InlineData("__BORROW__Paved", MaterialSlot.Paved)]
        [InlineData("__BORROW__Runway", MaterialSlot.Runway)]
        [InlineData("__BORROW__Tarmac", MaterialSlot.Tarmac)]
        [InlineData("__BORROW__Structure", MaterialSlot.Structure)]
        [InlineData("__BORROW__Concrete", MaterialSlot.Concrete)]
        [InlineData("__MARKINGS__Runway", MaterialSlot.Markings)]
        public void EachMarkerAsksForItsSurface(string name, object expected)
        {
            // object, since a public test cannot take the plugin's internal enum.
            Assert.Equal((MaterialSlot)expected, MaterialMarkers.Of(name));
        }

        // Each marker is a prefix: Unity appends " (Instance)" to a copy, and a bundle may name a
        // variant after its surface.
        [Theory]
        [InlineData("__BORROW__Paved (Instance)", MaterialSlot.Paved)]
        [InlineData("__BORROW__Water_lake", MaterialSlot.Water)]
        [InlineData("__MARKINGS__Runway_field3", MaterialSlot.Markings)]
        public void AMarkerIsAPrefix(string name, object expected)
        {
            Assert.Equal((MaterialSlot)expected, MaterialMarkers.Of(name));
        }

        // An empty slot, a bare marker and one this plugin does not know all get the ground, which is
        // what a plugin from before a surface existed gives it.
        [Theory]
        [InlineData(null)]
        [InlineData("__BORROW__")]
        [InlineData("__BORROW__Snow")]
        public void AnythingElseBorrowedIsTheGround(string name)
        {
            Assert.Equal(MaterialSlot.Terrain, MaterialMarkers.Of(name));
        }

        // Only the start counts, and only in its own case: anything else is the map's own material.
        [Theory]
        [InlineData("")]
        [InlineData("road_4lane")]
        [InlineData("lake__BORROW__Water")]
        [InlineData("__borrow__Water")]
        [InlineData("__MARKINGS__")]
        public void AnythingElseIsTheMapsOwn(string name)
        {
            Assert.Equal(MaterialSlot.Own, MaterialMarkers.Of(name));
        }

        // The paint's marker is not a borrow marker, so a plugin from before the paint leaves it alone.
        [Fact]
        public void ThePaintIsNotBorrowed()
        {
            Assert.False(MaterialMarkers.Markings.StartsWith(MaterialMarkers.Borrow, System.StringComparison.Ordinal));
        }

        // No marker is a prefix of another, so the order they are tested in cannot matter.
        [Fact]
        public void NoMarkerIsAPrefixOfAnother()
        {
            string[] surfaces =
            {
                MaterialMarkers.Water, MaterialMarkers.Paved, MaterialMarkers.Runway, MaterialMarkers.Tarmac,
                MaterialMarkers.Structure, MaterialMarkers.Concrete, MaterialMarkers.Markings,
            };

            foreach (string a in surfaces)
                foreach (string b in surfaces)
                    if (!ReferenceEquals(a, b))
                        Assert.False(a.StartsWith(b, System.StringComparison.Ordinal), $"'{a}' starts with '{b}'");
        }
    }
}
