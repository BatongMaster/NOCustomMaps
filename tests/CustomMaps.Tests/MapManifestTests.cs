using System.Linq;
using Xunit;

namespace CustomMaps.Tests
{
    public class MapManifestTests
    {
        // The reference manifest for the map this repository exists to build.
        // 143360 = 28 tiles x 5120 m = 20,552 km2.
        const string Kastellan = @"{
  ""mapId"": ""kastellan"",
  ""displayName"": ""Kastellan Basin"",
  ""version"": ""0.1.0"",
  ""author"": ""javoski"",
  ""builtAgainstGameHash"": ""211b5aad0ca1"",

  ""mapSizeX"": 143360,
  ""mapSizeY"": 143360,
  ""gridSizeX"": 16,
  ""gridSizeY"": 16,
  ""offsetX"": 80000,
  ""offsetY"": 80000,
  ""latitude"": 45,

  ""mapPrefix"": 50,

  ""assets"": {
    ""rootPrefab"": ""Kastellan"",
    ""terrainRoot"": ""Terrain"",
    ""oceanBasecolor"": ""kastellan_ocean_basecolor"",
    ""oceanDepthmap"": ""kastellan_ocean_depth"",
    ""terrainColorMap"": ""kastellan_color"",
    ""mapImage"": ""kastellan_map"",
    ""treePositions"": ""kastellan_trees"",
    ""seaLanes"": ""kastellan_sealanes"",
    ""roadNetwork"": ""kastellan_roads""
  },

  ""camera"": { ""x"": 0, ""y"": 530.8, ""z"": 0, ""pitch"": 12, ""yaw"": 180, ""roll"": 0 }
}";

        static MapManifest Parsed() => MapManifest.FromJson(Kastellan);

        [Fact]
        public void ReferenceManifestParses()
        {
            MapManifest m = Parsed();

            Assert.Equal("kastellan", m.MapId);
            Assert.Equal("Kastellan Basin", m.DisplayName);
            Assert.Equal(143360f, m.MapSizeX);
            Assert.Equal(143360f, m.MapSizeY);
            Assert.Equal(16, m.GridSizeX);
            Assert.Equal(80000, m.OffsetX);
            Assert.Equal(45f, m.Latitude);
            Assert.Equal(50, m.MapPrefix);
            Assert.Equal("Kastellan", m.RootPrefab);
            Assert.Equal("kastellan_ocean_depth", m.OceanDepthmap);
            Assert.Equal(530.8f, m.CameraY);
        }

        [Fact]
        public void ReferenceManifestIsClean()
        {
            Assert.Empty(Parsed().Validate());
        }

        // The prefix baked into the manifest must be the one the authoring tool would
        // have picked, or the two have drifted.
        [Fact]
        public void ReferencePrefixMatchesTheAllocator()
        {
            Assert.Equal(MapIdentity.AllocatePrefix("kastellan"), Parsed().MapPrefix);
        }

        [Fact]
        public void BuildsItsPrefabName()
        {
            Assert.Equal("cm.kastellan.a1b2c3d4", Parsed().PrefabNameFor("a1b2c3d4"));
        }

        [Fact]
        public void MissingAssetsAreWarningsNotErrors()
        {
            MapManifest m = Parsed();
            m.TerrainColorMap = null;
            m.MapImage = null;
            m.SeaLanes = null;

            var problems = m.Validate();

            Assert.Equal(3, problems.Count);
            Assert.All(problems, p => Assert.False(p.IsError));
        }

        [Fact]
        public void MissingRootPrefabIsAnError()
        {
            MapManifest m = Parsed();
            m.RootPrefab = null;
            Assert.Contains(m.Validate(), p => p.IsError && p.Field == nameof(MapManifest.RootPrefab));
        }

        // FloatingOrigin's +-90,000 m kill bound looks like a hard 180 km ceiling but is
        // never enforced — RBKillBounds() has no callers. So a 200 km map is allowed, and
        // what it gets instead is a warning about what it will cost to build.
        [Fact]
        public void ALargeMapIsWarnedAboutRatherThanRejected()
        {
            MapManifest m = Parsed();
            m.MapSizeX = 256000f;
            m.MapSizeY = 256000f;
            m.GridSizeX = m.GridSizeY = 32;
            m.OffsetX = m.OffsetY = 150000;

            var problems = m.Validate();

            Assert.DoesNotContain(problems, p => p.IsError);
            Assert.Contains(problems, p => !p.IsError && p.Field == "MapSizeX" && p.Message.Contains("collider"));
        }

        [Fact]
        public void RejectsAnAbsurdlyLargeMap()
        {
            MapManifest m = Parsed();
            m.MapSizeX = 500000f;
            m.GridSizeX = 64;
            m.OffsetX = 300000;

            Assert.Contains(m.Validate(), p => p.IsError && p.Field == "MapSizeX");
        }

        // The collider estimate is what makes the size decision concrete, so it has to
        // scale with area and land in the right ballpark at the validated design point.
        [Fact]
        public void ColliderEstimateScalesWithArea()
        {
            float atDesignPoint = GridMath.EstimateColliderMegabytes(143360f);
            Assert.InRange(atDesignPoint, 150f, 450f);

            // Doubling the side quadruples the area, and the cost with it.
            float doubled = GridMath.EstimateColliderMegabytes(286720f);
            Assert.InRange(doubled / atDesignPoint, 3.9f, 4.1f);

            Assert.Equal(0f, GridMath.EstimateColliderMegabytes(0f));
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-1f)]
        public void RejectsANonPositiveSize(float size)
        {
            MapManifest m = Parsed();
            m.MapSizeX = size;
            Assert.Contains(m.Validate(), p => p.IsError && p.Field == "MapSizeX");
        }

        // GridLabels.GetGridPosition returns "" outside 0 <= major < gridSize, so the
        // HUD coordinate readout silently goes blank at the offending edge.
        [Fact]
        public void RejectsAnOffsetThatLeavesTheLowEdgeOffGrid()
        {
            MapManifest m = Parsed();
            m.OffsetX = 60000;                        // needs at least 71680
            Assert.Contains(m.Validate(),
                p => p.IsError && p.Field == "OffsetX" && p.Message.Contains("less than half"));
        }

        [Fact]
        public void RejectsAnOffsetThatPushesTheHighEdgeOffGrid()
        {
            MapManifest m = Parsed();
            m.OffsetX = 100000;                       // 100000 + 71680 = 171680 > 16 * 10000
            Assert.Contains(m.Validate(),
                p => p.IsError && p.Field == "OffsetX" && p.Message.Contains("exceeds"));
        }

        [Fact]
        public void WarnsWhenGridSizeIsNotAMultipleOfFour()
        {
            MapManifest m = Parsed();
            m.GridSizeX = 15;
            m.OffsetX = 78000;                        // keep the offset checks quiet

            var problems = m.Validate();
            Assert.Contains(problems, p => !p.IsError && p.Field == "GridSizeX");
            Assert.DoesNotContain(problems, p => p.IsError);
        }

        [Fact]
        public void RejectsAnOutOfBandMapPrefix()
        {
            foreach (int bad in new[] { 0, 1, 2, 15, 112, 128, 255 })
            {
                MapManifest m = Parsed();
                m.MapPrefix = bad;
                Assert.Contains(m.Validate(), p => p.IsError && p.Field == nameof(MapManifest.MapPrefix));
            }
        }

        [Fact]
        public void RejectsABadIdOrDisplayName()
        {
            MapManifest bad = Parsed();
            bad.MapId = "Kastellan Basin";
            bad.DisplayName = "<color=red>Kastellan</color>";

            var problems = bad.Validate();
            Assert.Contains(problems, p => p.IsError && p.Field == nameof(MapManifest.MapId));
            Assert.Contains(problems, p => p.IsError && p.Field == nameof(MapManifest.DisplayName));
        }

        [Fact]
        public void ReportsEveryProblemAtOnce()
        {
            MapManifest m = Parsed();
            m.MapId = "BAD";
            m.MapPrefix = 999;
            m.Latitude = 120f;
            m.RootPrefab = null;

            var errors = m.Validate().Where(p => p.IsError).Select(p => p.Field).ToList();

            Assert.Contains(nameof(MapManifest.MapId), errors);
            Assert.Contains(nameof(MapManifest.MapPrefix), errors);
            Assert.Contains(nameof(MapManifest.Latitude), errors);
            Assert.Contains(nameof(MapManifest.RootPrefab), errors);
        }

        [Fact]
        public void DefaultsFillInForAMinimalManifest()
        {
            MapManifest m = MapManifest.FromJson(@"{ ""mapId"": ""tiny"", ""mapSizeX"": 10240, ""mapSizeY"": 10240 }");

            Assert.Equal(16, m.GridSizeX);
            Assert.Equal(80000, m.OffsetX);
            Assert.Equal(45f, m.Latitude);
            Assert.Equal("Terrain", m.TerrainRoot);
            Assert.Equal(530.8f, m.CameraY);
        }

        [Fact]
        public void ProblemFormatsReadably()
        {
            var p = new MapProblem(true, "MapSizeX", "must be greater than zero");
            Assert.Equal("ERROR MapSizeX: must be greater than zero", p.ToString());
            Assert.Equal("WARN a: b", new MapProblem(false, "a", "b").ToString());
        }

        // ---- DefaultGrid ------------------------------------------------------------------

        /// <summary>Swiss Alps was given 24 cells at 120,000 m by hand; the rule reproduces
        /// it, which is what pins it.</summary>
        [Fact]
        public void DefaultGridReproducesTheSwissValues()
        {
            MapManifest.DefaultGrid(199680f, out int grid, out int offset);

            Assert.Equal(24, grid);
            Assert.Equal(120000, offset);
        }

        [Theory]
        [InlineData(5120f)]
        [InlineData(81920f)]
        [InlineData(143360f)]
        [InlineData(199680f)]
        [InlineData(327680f)]
        public void DefaultGridAlwaysValidates(float size)
        {
            MapManifest.DefaultGrid(size, out int grid, out int offset);

            var manifest = new MapManifest
            {
                MapId = "grid-test", DisplayName = "Grid Test", RootPrefab = "grid-test", MapPrefix = 40,
                MapSizeX = size, MapSizeY = size, GridSizeX = grid, GridSizeY = grid,
                OffsetX = offset, OffsetY = offset, Latitude = 45f,
            };

            Assert.Equal(0, grid % MapManifest.GridSizeMultiple);
            foreach (MapProblem problem in manifest.Validate())
                Assert.False(problem.IsError, problem.ToString());
        }
    }
}
