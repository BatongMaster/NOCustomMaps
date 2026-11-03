using System.Collections.Generic;
using Xunit;

namespace CustomMaps.Tests
{
    public class WorkshopTagsTests
    {
        static KeyValuePair<string, string> Map(string prefab, string tag) => new KeyValuePair<string, string>(prefab, tag);

        [Fact]
        public void MapTagDropsCommasAndEdges()
        {
            Assert.Equal("Swiss Alps", WorkshopTags.MapTag("Swiss Alps"));
            Assert.Equal("Alps North", WorkshopTags.MapTag(" Alps, North "));
            Assert.Equal("", WorkshopTags.MapTag(null));
        }

        [Fact]
        public void TheExactBuildIsFound()
        {
            var maps = new[] { Map("cm.other.11111111", "Other"), Map("cm.swissalps.3363c1ec", "Swiss Alps") };
            Assert.Equal("Swiss Alps", WorkshopTags.FindMapTag("cm.swissalps.3363c1ec", maps, out bool exact));
            Assert.True(exact);
        }

        [Fact]
        public void AnotherBuildOfTheMapGivesItsName()
        {
            var maps = new[] { Map("cm.other.11111111", "Other"), Map("cm.swissalps.aaaaaaaa", "Swiss Alps") };
            Assert.Equal("Swiss Alps", WorkshopTags.FindMapTag("cm.swissalps.3363c1ec", maps, out bool exact));
            Assert.False(exact);
        }

        [Fact]
        public void TheExactBuildWinsOverAnEarlierOne()
        {
            var maps = new[] { Map("cm.swissalps.aaaaaaaa", "Old Name"), Map("cm.swissalps.3363c1ec", "Swiss Alps") };
            Assert.Equal("Swiss Alps", WorkshopTags.FindMapTag("cm.swissalps.3363c1ec", maps, out bool exact));
            Assert.True(exact);
        }

        [Theory]
        [InlineData("cm.unknown.3363c1ec")]
        [InlineData("Terrain1")]
        [InlineData(null)]
        public void NoBuildInstalledGivesNoTag(string path)
        {
            var maps = new[] { Map("cm.swissalps.aaaaaaaa", "Swiss Alps") };
            Assert.Null(WorkshopTags.FindMapTag(path, maps, out bool exact));
            Assert.False(exact);
        }

        [Fact]
        public void PluginTagMovesFromTypedToAutomatic()
        {
            var auto = new List<string> { "Mission", "Swiss Alps", "PvP" };
            var custom = new List<string> { "nocustommaps", "Coop" };

            WorkshopTags.AddPluginTag(auto, custom);
            WorkshopTags.AddPluginTag(auto, custom);

            Assert.Equal(new[] { "Mission", "Swiss Alps", "PvP", "NOCustomMaps" }, auto);
            Assert.Equal(new[] { "Coop" }, custom);
        }

        [Fact]
        public void TagColorComesFromTheManifestFirst()
        {
            Assert.True(WorkshopTags.TryTagColor("#010203", "swissalps", out byte r, out byte g, out byte b));
            Assert.Equal((1, 2, 3), ((int)r, (int)g, (int)b));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("red")]
        public void SwissAlpsIsRedUntilItsManifestSays(string manifest)
        {
            Assert.True(WorkshopTags.TryTagColor(manifest, "swissalps", out byte r, out byte g, out byte b));
            Assert.Equal((0xcc, 0x52, 0x47), ((int)r, (int)g, (int)b));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("red")]
        public void AnyOtherMapWithoutAColorHasNone(string manifest)
        {
            Assert.False(WorkshopTags.TryTagColor(manifest, "cascadia", out _, out _, out _));
        }

        [Fact]
        public void TagHueIsStableAndInRange()
        {
            float hue = WorkshopTags.TagHue("swissalps");
            Assert.Equal(hue, WorkshopTags.TagHue("swissalps"));
            Assert.InRange(hue, 0f, 0.9999f);
        }
    }
}
