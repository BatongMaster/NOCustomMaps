using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace CustomMaps.Tests
{
    /// <summary>
    /// The custom map tiles in the mission editor's New Mission panel (<see cref="MapTiles"/>): their order and
    /// labels, the scrolling list's size and layout, scrolling a tile into view, the cached pictures' names,
    /// and that the game installed here has the panel the tiles were checked against.
    /// </summary>
    public class MapTilesTests
    {
        const float Cell = 160f, Gap = 6f, M = MapTiles.Margin;

        static readonly string[] Game = { "Heartland", "Ignus Archipelago" };

        readonly ITestOutputHelper _output;

        public MapTilesTests(ITestOutputHelper output) => _output = output;

        static MapTiles.Tile T(string name, string id, string version = "1.0.0", string hash = "aaaaaaaa", int copy = 0)
            => new MapTiles.Tile(name, id, $"cm.{id}.{hash}", version, copy);

        // --- order ---

        [Fact]
        public void CustomMapsAreSortedByNameIgnoringCase()
        {
            var tiles = new[] { T("swiss alps", "swissalps"), T("Kellys Wake Island", "kellys-wake-island"), T("Alpine", "alpine") };
            int[] order = MapTiles.Order(tiles);
            Assert.Equal(new[] { "Alpine", "Kellys Wake Island", "swiss alps" }, order.Select(i => tiles[i].Name));
        }

        [Fact]
        public void EqualNamesAreOrderedByMapIdThenRegisteredNameThenCopy()
        {
            var tiles = new[]
            {
                T("Alps", "zeta"),
                T("Alps", "alpha", hash: "bbbbbbbb"),
                T("Alps", "alpha", hash: "aaaaaaaa", copy: 1),
                T("Alps", "alpha", hash: "aaaaaaaa"),
            };
            int[] order = MapTiles.Order(tiles);
            Assert.Equal(new[] { 3, 2, 1, 0 }, order);
        }

        [Fact]
        public void AMapWithoutANameSortsByItsRegisteredName()
        {
            var nameless = new MapTiles.Tile(null, "zz", "cm.zz.00000000", "1");
            Assert.Equal("cm.zz.00000000", MapTiles.BaseLabel(nameless));
            int[] order = MapTiles.Order(new[] { T("Zulu", "zulu"), nameless });
            Assert.Equal(new[] { 1, 0 }, order);
        }

        // --- labels ---

        [Fact]
        public void AMapReadsItsNameWhenNoOtherDoes()
        {
            Assert.Equal(new[] { "Swiss Alps", "Kellys Wake Island" },
                         MapTiles.Labels(Game, new[] { T("Swiss Alps", "swissalps"), T("Kellys Wake Island", "kellys") }));
        }

        [Fact]
        public void AMapNamedLikeAGameMapAddsItsVersionAndTheGameMapKeepsItsName()
        {
            // Only the custom tile's label changes; the game's tiles are not in the result at all.
            Assert.Equal(new[] { "heartland 2.1" }, MapTiles.Labels(Game, new[] { T("heartland", "myheart", "2.1") }));
        }

        [Fact]
        public void TwoMapsOfOneNameAreToldApartByVersion()
        {
            string[] labels = MapTiles.Labels(Game, new[] { T("Alps", "alps-a", "0.4.0"), T("Alps", "alps-b", "0.3.0") });
            Assert.Equal(new[] { "Alps 0.4.0", "Alps 0.3.0" }, labels);
        }

        [Fact]
        public void ThenByMapIdWhenTheVersionsAreEqualOrMissing()
        {
            Assert.Equal(new[] { "Alps (alps-a)", "Alps (alps-b)" },
                         MapTiles.Labels(Game, new[] { T("Alps", "alps-a", "0.4.0"), T("Alps", "alps-b", "0.4.0") }));
            Assert.Equal(new[] { "Alps (alps-a)", "Alps 1.0" },
                         MapTiles.Labels(Game, new[] { T("Alps", "alps-a", null), T("Alps", "alps-b", "1.0") }));
        }

        [Fact]
        public void ThenByRegisteredNameWhenTheMapIdIsTheSame()
        {
            string[] labels = MapTiles.Labels(Game, new[] { T("Alps", "alps", "1", "aaaaaaaa"), T("Alps", "alps", "1", "bbbbbbbb") });
            Assert.Equal(new[] { "Alps (cm.alps.aaaaaaaa)", "Alps (cm.alps.bbbbbbbb)" }, labels);
        }

        [Fact]
        public void EveryLabelIsUniqueWhateverTheNames()
        {
            var random = new Random(3);
            string[] names = { "Alps", "alps", "Heartland", "Isle", "Isle 1", null };
            string[] versions = { "1", "2", "", null };
            for (int round = 0; round < 500; round++)
            {
                var tiles = Enumerable.Range(0, random.Next(1, 8))
                    .Select(i => T(names[random.Next(names.Length)], "m" + random.Next(4), versions[random.Next(versions.Length)], $"{i:x8}"))
                    .ToArray();
                string[] labels = MapTiles.Labels(Game, tiles);
                Assert.Equal(labels.Length, labels.Distinct(StringComparer.OrdinalIgnoreCase).Count());
                Assert.DoesNotContain(labels, l => Game.Contains(l, StringComparer.OrdinalIgnoreCase));
            }
        }

        [Fact]
        public void CopiesOfOneMapAreNumberedAndDoNotCountAsAnotherMap()
        {
            string[] labels = MapTiles.Labels(Game, new[] { T("Swiss Alps", "swissalps"), T("Swiss Alps", "swissalps", copy: 1), T("Swiss Alps", "swissalps", copy: 2) });
            Assert.Equal(new[] { "Swiss Alps", "Swiss Alps #2", "Swiss Alps #3" }, labels);
        }

        // --- the list's size ---

        // Center's children in the game's panel (level0 and resources.assets, identical), top to bottom, the map
        // list ('Map options', 160) left out: h1, hr, Spacer (3), h3, Name InputField, Spacer, h3 (1), mode
        // ToggleGroup, Spacer (2), h3 (2), faction ToggleGroup (1), Spacer (1), h3 (3), Spacer, Buttons.
        static readonly float[] Others = { 30, 2, 10, 21, 30, 10, 21, 40, 10, 21, 40, 10, 21, 0, 0 };

        [Fact]
        public void TheListTakesWhatTheGamesPanelLeaves()
        {
            Assert.Equal(266f, Others.Sum());
            Assert.Equal(186f, MapTiles.ListHeight(662, 30, 30, 10, Others, Cell, Gap));
            Assert.Equal(MapTiles.FallbackHeight, MapTiles.ListHeight(662, 30, 30, 10, Others, Cell, Gap));

            // The game's tiles end at 30 + 10 * 13 + 266 - 0 + 160 = 586 down the column; the list ends lower, so the
            // column's last two (empty) children and its bottom padding take what is left exactly.
            float listTop = 30 + Others.Take(13).Sum() + 10 * 13;
            Assert.Equal(426f, listTop);
            Assert.Equal(662f, listTop + 186 + 10 + 0 + 10 + 0 + 30);
        }

        [Theory]
        [InlineData(400f, 160f)]        // a shorter column: one row at least
        [InlineData(2000f, 326f)]       // a taller one: two rows at most
        [InlineData(float.NaN, 186f)]
        [InlineData(float.PositiveInfinity, 186f)]
        public void TheHeightIsKeptBetweenOneRowAndTwo(float column, float expected)
        {
            Assert.Equal(expected, MapTiles.ListHeight(column, 30, 30, 10, Others, Cell, Gap));
        }

        [Fact]
        public void AnUnreadableTileGivesTheGamesHeight()
        {
            Assert.Equal(MapTiles.FallbackHeight, MapTiles.ListHeight(662, 30, 30, 10, Others, float.NaN, Gap));
            Assert.Equal(MapTiles.FallbackHeight, MapTiles.ListHeight(662, 30, 30, 10, Others, 0f, Gap));
        }

        [Theory]
        [InlineData(0, 0)]
        [InlineData(1, 1)]
        [InlineData(2, 1)]
        [InlineData(3, 2)]
        [InlineData(4, 2)]
        [InlineData(101, 51)]
        public void TwoTilesARow(int tiles, int rows) => Assert.Equal(rows, MapTiles.Rows(tiles));

        [Fact]
        public void TheContentIsItsRowsWithTheirGapsAndMargins()
        {
            Assert.Equal(168f, MapTiles.ContentHeight(2, Cell, Gap));
            Assert.Equal(334f, MapTiles.ContentHeight(3, Cell, Gap));
            Assert.Equal((float)(4 + 50 * 160 + 49 * 6 + 4), MapTiles.ContentHeight(100, Cell, Gap));
            Assert.Equal(8f, MapTiles.ContentHeight(0, Cell, Gap));
        }

        // The window shows the first row whole, where the game's tiles are, and the first 20 units of the second,
        // so that there visibly is one: with three maps or more there always is.
        [Fact]
        public void TheWindowShowsARowAndAPeekOfTheNext()
        {
            float window = MapTiles.ViewportHeight(186);
            Assert.Equal(190f, window);

            float secondRowTop = M + Cell + Gap;
            Assert.Equal(20f, window - secondRowTop);
            Assert.True(MapTiles.ContentHeight(3, Cell, Gap) > window);
        }

        [Fact]
        public void TheWindowAndScrollbarFitTheColumn()
        {
            // Center is about 510 wide at runtime ('Left' inactive, 'Right' 240), 450 inside its padding; as saved
            // in the asset it is 480, 420 inside.
            float width = MapTiles.ViewportWidth(Cell, Gap);
            Assert.Equal(334f, width);
            Assert.Equal(173f, MapTiles.ScrollbarX(0, width));
            Assert.True(MapTiles.Fits(0, width, 450));
            Assert.True(MapTiles.Fits(0, width, 420));

            // A third column (492) would not.
            Assert.False(MapTiles.Fits(0, 3 * Cell + 2 * Gap + 2 * M, 450));
            // Nor a window pushed right by the width of the scrollbar.
            Assert.False(MapTiles.Fits(40, width, 450));
        }

        // --- scrolling a tile into view ---

        static float Scroll(int row, float current, int tiles = 20)
            => MapTiles.ScrollFor(row, MapTiles.ViewportHeight(186), Cell, Gap, M, MapTiles.ContentHeight(tiles, Cell, Gap), current);

        [Fact]
        public void ATileAlreadyShowingDoesNotMove()
        {
            Assert.Equal(0f, Scroll(0, 0));
            Assert.Equal(480f, Scroll(3, 480));     // row 3 spans 498..666 with its border, the window 480..670
        }

        [Fact]
        public void ATileBelowIsBroughtUpToTheBottomEdge()
        {
            // Row 1 with its border spans 166..334; the window is 190 high.
            Assert.Equal(144f, Scroll(1, 0));
            Assert.Equal(166f * 4 + 168 - 190, Scroll(4, 0));
        }

        [Fact]
        public void ATileAboveIsBroughtDownToTheTopEdge()
        {
            Assert.Equal(0f, Scroll(0, 300));
            Assert.Equal(166f * 2, Scroll(2, 600));
        }

        [Fact]
        public void NeverPastEitherEnd()
        {
            float content = MapTiles.ContentHeight(20, Cell, Gap);
            float last = Scroll(9, 0);
            Assert.Equal(content - 190, last);
            Assert.Equal(content - 190, Scroll(9, 10000));
            Assert.Equal(0f, Scroll(-1, 50));
            Assert.Equal(0f, Scroll(0, float.NaN));
        }

        [Fact]
        public void AFewTilesNeverScroll()
        {
            // Two tiles fit the window: nothing to scroll whatever is asked.
            Assert.Equal(0f, Scroll(0, 0, tiles: 2));
            Assert.Equal(0f, Scroll(5, 0, tiles: 2));
        }

        [Fact]
        public void EveryRowEndsUpWhollyInTheWindow()
        {
            float window = MapTiles.ViewportHeight(186);
            float content = MapTiles.ContentHeight(30, Cell, Gap);
            for (int row = 0; row < 15; row++)
                foreach (float from in new[] { 0f, 400f, 1200f, content })
                {
                    float at = MapTiles.ScrollFor(row, window, Cell, Gap, M, content, from);
                    float top = M + row * (Cell + Gap);
                    Assert.True(top - M >= at - 0.001f && top + Cell + M <= at + window + 0.001f, $"row {row} from {from}: {at}");
                }
        }

        [Fact]
        public void AWheelNotchMovesTheSameShareOfARowAsInTheLoadList()
        {
            // The Load list: 5 over rows 38 apart. The tiles: 166 apart.
            float sensitivity = MapTiles.ScrollSensitivity(Cell, Gap);
            Assert.Equal(5f * 166f / 38f, sensitivity, 4);
            Assert.Equal(MapTiles.LoadListSensitivity / MapTiles.LoadListRowPitch, sensitivity / (Cell + Gap), 5);

            // Unreadable sizes fall back to the game's tile.
            Assert.Equal(sensitivity, MapTiles.ScrollSensitivity(float.NaN, Gap), 4);
            Assert.Equal(sensitivity, MapTiles.ScrollSensitivity(-200f, 0f), 4);
        }

        static (int, int) Overlapping(int rows, float from, float to)
            => MapTiles.RowsOverlapping(rows, Cell, Gap, M, from, to, out int first, out int last) ? (first, last) : (-1, -1);

        static (int, int) Centred(int rows, float scroll, float window = 190f)
            => MapTiles.RowsCentred(rows, Cell, Gap, M, window, scroll, out int first, out int last) ? (first, last) : (-1, -1);

        [Fact]
        public void TheRowsInReachAreThoseInTheWindowAndOneEitherSide()
        {
            float pitch = Cell + Gap;
            // At the top: rows 0 and 1 show (1 by 20), row 2 is the one below.
            Assert.Equal((0, 2), Overlapping(10, 0 - pitch, 0 + 190 + pitch));
            // Scrolled to the end of 10 rows.
            float end = MapTiles.ContentHeight(20, Cell, Gap) - 190;
            Assert.Equal((7, 9), Overlapping(10, end - pitch, end + 190 + pitch));
            Assert.Equal((-1, -1), Overlapping(0, 0, 190));
            Assert.Equal((0, 9), Overlapping(10, float.NegativeInfinity, float.PositiveInfinity));
            Assert.Equal((-1, -1), Overlapping(10, float.NaN, 190));
        }

        [Fact]
        public void RowsInReachAgreeWithTheirSpans()
        {
            float pitch = Cell + Gap;
            for (float from = -400f; from < 4000f; from += 13.37f)
                foreach (float length in new[] { 0.5f, 190f, 190f + 2 * pitch, 900f })
                {
                    float to = from + length;
                    int first = -1, last = -1;
                    for (int r = 0; r < 20; r++)
                    {
                        float top = M + r * pitch;
                        if (top < to && top + Cell > from)
                        {
                            if (first < 0) first = r;
                            last = r;
                        }
                    }
                    Assert.Equal((first, last), Overlapping(20, from, to));
                }
        }

        [Fact]
        public void ATileScrolledOutOfTheWindowIsOutOfTheOtherControlsReach()
        {
            // The case found in review: scrolled a row down, row 0 sits over the faction toggles above the list.
            Assert.Equal((1, 1), Centred(10, 166f));
            // Row 1 brought up to the bottom edge from row 0: 20 of row 0 shows, its middle does not.
            Assert.Equal((1, 1), Centred(10, 144f));
            // At the top: row 0; row 1 peeks 20 in, its middle well below.
            Assert.Equal((0, 0), Centred(10, 0f));
            // A taller window takes two rows.
            Assert.Equal((0, 1), Centred(10, 0f, 326f + M));
        }

        [Fact]
        public void SomeRowIsAlwaysInReachFromOutside()
        {
            float window = MapTiles.ViewportHeight(186);
            for (int tiles = 1; tiles <= 41; tiles += 4)
            {
                int rows = MapTiles.Rows(tiles);
                float most = Math.Max(0f, MapTiles.ContentHeight(tiles, Cell, Gap) - window);
                for (float scroll = 0f; scroll <= most; scroll += 7.3f)
                {
                    (int first, int last) = Centred(rows, scroll, window);
                    Assert.True(first >= 0 && first <= last && last < rows, $"{tiles} tiles at {scroll}: {first}..{last}");
                    for (int r = 0; r < rows; r++)
                    {
                        float middle = M + r * (Cell + Gap) + Cell / 2f;
                        Assert.Equal(middle > scroll && middle < scroll + window, r >= first && r <= last);
                    }
                }
            }
        }

        [Fact]
        public void MovesInsideTheGridGoToTheNeighbour()
        {
            // 5 tiles: rows (0 1) (2 3) (4).
            Assert.Equal(1, MapTiles.Neighbour(0, 5, 1, 0));
            Assert.Equal(0, MapTiles.Neighbour(1, 5, -1, 0));
            Assert.Equal(2, MapTiles.Neighbour(0, 5, 0, 1));
            Assert.Equal(3, MapTiles.Neighbour(1, 5, 0, 1));
            Assert.Equal(1, MapTiles.Neighbour(3, 5, 0, -1));
            Assert.Equal(4, MapTiles.Neighbour(3, 5, 0, 1));       // down into a short last row: its last tile
            Assert.Equal(2, MapTiles.Neighbour(4, 5, 0, -1));
        }

        [Fact]
        public void MovesOffTheGridAreLeftToThePanel()
        {
            Assert.Equal(-1, MapTiles.Neighbour(0, 5, -1, 0));
            Assert.Equal(-1, MapTiles.Neighbour(1, 5, 1, 0));
            Assert.Equal(-1, MapTiles.Neighbour(0, 5, 0, -1));
            Assert.Equal(-1, MapTiles.Neighbour(1, 5, 0, -1));
            Assert.Equal(-1, MapTiles.Neighbour(4, 5, 0, 1));
            Assert.Equal(-1, MapTiles.Neighbour(4, 5, 1, 0));       // nothing to the right of a lone last tile
            Assert.Equal(-1, MapTiles.Neighbour(2, 4, 0, 1));
            Assert.Equal(-1, MapTiles.Neighbour(5, 5, 0, 1));       // not a tile
            Assert.Equal(-1, MapTiles.Neighbour(0, 1, 1, 0));
        }

        [Fact]
        public void AGreyedOutTileIsPassedOver()
        {
            // 8 tiles; 2 and 5 failed to load.
            Func<int, bool> usable = i => i != 2 && i != 5;
            Assert.Equal(4, MapTiles.Neighbour(0, 8, 0, 1, usable));
            Assert.Equal(0, MapTiles.Neighbour(4, 8, 0, -1, usable));
            Assert.Equal(7, MapTiles.Neighbour(3, 8, 0, 1, usable));
            Assert.Equal(-1, MapTiles.Neighbour(3, 8, -1, 0, usable));
            Assert.Equal(-1, MapTiles.Neighbour(4, 8, 1, 0, usable));
            Assert.Equal(-1, MapTiles.Neighbour(6, 8, 0, -1, i => i == 6));
        }

        [Fact]
        public void EveryTileIsReachableFromTheFirstInsideTheGrid()
        {
            for (int count = 1; count <= 31; count++)
            {
                var seen = new HashSet<int> { 0 };
                var todo = new Queue<int>(seen);
                while (todo.Count > 0)
                {
                    int at = todo.Dequeue();
                    foreach ((int dx, int dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int to = MapTiles.Neighbour(at, count, dx, dy);
                        Assert.True(to >= -1 && to < count);
                        if (to >= 0 && seen.Add(to)) todo.Enqueue(to);
                    }
                }
                Assert.Equal(count, seen.Count);
            }
        }

        // --- pictures ---

        [Fact]
        public void APictureIsNamedByMapAndBuild()
        {
            string hash = "4F2A9C00D1E2B3A4" + new string('0', 48);
            Assert.Equal("swissalps-4f2a9c00d1e2b3a4-t1.png", MapTiles.ThumbnailFileName("swissalps", hash));
            Assert.Null(MapTiles.ThumbnailFileName("swissalps", "4f2a9c00"));         // too short
            Assert.Null(MapTiles.ThumbnailFileName("swissalps", "zz2a9c00d1e2b3a4ff"));  // not hex
            Assert.Null(MapTiles.ThumbnailFileName("Swiss Alps", hash));               // not a map id
            Assert.Null(MapTiles.ThumbnailFileName(null, hash));
        }

        [Theory]
        [InlineData("kellys-0123456789abcdef-t1.png", "kellys", true)]
        [InlineData("kellys-0123456789ABCDEF-t7.PNG", "kellys", true)]
        [InlineData("kellys-0123456789abcdef-t1.png.tmp", "kellys", true)]
        [InlineData("kellys-0123456789abcdef-t12.png", "kellys", true)]
        [InlineData("kellys-wake-island-0123456789abcdef-t1.png", "kellys", false)]
        [InlineData("kellys-wake-island-0123456789abcdef-t1.png", "kellys-wake-island", true)]
        [InlineData("kellys-0123456789abcdef-t1.png", "kellys-wake-island", false)]
        [InlineData("kellys-0123456789abcde-t1.png", "kellys", false)]
        [InlineData("kellys-0123456789abcdef-x1.png", "kellys", false)]
        [InlineData("kellys-0123456789abcdef-t.png", "kellys", false)]
        [InlineData("kellys-0123456789abcdef-t1.jpg", "kellys", false)]
        [InlineData("com.javoski.custommaps.bundle-hashes.txt", "kellys", false)]
        public void OnlyAMapsOwnPicturesAreReplaced(string file, string mapId, bool replaced)
        {
            Assert.Equal(replaced, MapTiles.IsThumbnailOf(file, mapId));
        }

        [Fact]
        public void TheNameItGivesIsOneItReplaces()
        {
            string name = MapTiles.ThumbnailFileName("kellys-wake-island", new string('a', 64));
            Assert.True(MapTiles.IsThumbnailOf(name, "kellys-wake-island"));
            Assert.False(MapTiles.IsThumbnailOf(name, "kellys"));
        }

        [Fact]
        public void TheGamesPictureSize() => Assert.Equal(512, MapTiles.ThumbnailSize);

        [Fact]
        public void ASquarePictureIsTakenWhole()
        {
            MapTiles.CentreSquare(0, 0, 1024, 1024, 1024, 1024, out float sx, out float sy, out float ox, out float oy);
            Assert.Equal((1f, 1f, 0f, 0f), (sx, sy, ox, oy));
        }

        [Fact]
        public void AWidePictureLosesItsSides()
        {
            MapTiles.CentreSquare(0, 0, 2000, 1000, 2000, 1000, out float sx, out float sy, out float ox, out float oy);
            Assert.Equal((0.5f, 1f, 0.25f, 0f), (sx, sy, ox, oy));
        }

        [Fact]
        public void ASpriteInALargerTextureIsTakenFromItsRect()
        {
            // A 200 by 400 sprite at (100, 50) in a 1000 by 1000 texture: its middle 200 by 200, from (100, 150).
            MapTiles.CentreSquare(100, 50, 200, 400, 1000, 1000, out float sx, out float sy, out float ox, out float oy);
            Assert.Equal(0.2f, sx, 5);
            Assert.Equal(0.2f, sy, 5);
            Assert.Equal(0.1f, ox, 5);
            Assert.Equal(0.15f, oy, 5);
        }

        [Theory]
        [InlineData(1024, new[] { 1024, 512 })]                 // Map Forge's colour map
        [InlineData(8192, new[] { 2048, 1024, 512 })]           // Swiss Alps' chart
        [InlineData(2047, new[] { 1024, 512 })]
        [InlineData(700, new[] { 512 })]
        [InlineData(512, new[] { 512 })]
        [InlineData(200, new[] { 512 })]                        // smaller: drawn once, up
        public void APictureIsHalvedDownToTheTile(int side, int[] steps)
        {
            Assert.Equal(steps, MapTiles.DownscaleSteps(side));
        }

        [Theory]
        [InlineData(8192, new[] { 4096, 2048, 1024, 512 })]     // an 8192 chart without mipmaps: halved from the start
        [InlineData(4096, new[] { 4096, 2048, 1024, 512 })]
        [InlineData(1024, new[] { 1024, 512 })]
        public void APictureWithoutMipmapsIsHalvedFromTheStart(int side, int[] steps)
        {
            Assert.Equal(steps, MapTiles.DownscaleSteps(side, MapTiles.ThumbnailSize, MapTiles.LargestStepWithoutMips));
        }

        [Fact]
        public void MapForgesLargestChartIsAveragedWholeWithoutMipmaps()
        {
            // AirfieldChart.MaxSize is 8192: the first draw is at most two texels to a pixel each way.
            int[] steps = MapTiles.DownscaleSteps(8192, MapTiles.ThumbnailSize, MapTiles.LargestStepWithoutMips);
            Assert.True(8192 / steps[0] <= 2);
        }

        [Fact]
        public void EachStepIsHalfTheOneBeforeAndNoneExceedsTheLargest()
        {
            for (int side = 1; side < 20000; side += 37)
            {
                int[] steps = MapTiles.DownscaleSteps(side);
                Assert.Equal(MapTiles.ThumbnailSize, steps[^1]);
                Assert.True(steps[0] <= MapTiles.LargestStep);
                Assert.True(steps[0] <= Math.Max(side, MapTiles.ThumbnailSize));
                for (int i = 1; i < steps.Length; i++) Assert.Equal(steps[i - 1] / 2, steps[i]);
            }
        }

        [Fact]
        public void DarkeningScalesColourAndLeavesAlpha()
        {
            byte[] rgb = { 255, 100, 0, 10, 20, 30 };
            MapTiles.Darken(rgb, 3, 0.8f);
            Assert.Equal(new byte[] { 204, 80, 0, 8, 16, 24 }, rgb);

            byte[] rgba = { 255, 255, 255, 255, 50, 50, 50, 77 };
            MapTiles.Darken(rgba, 4, 0.8f);
            Assert.Equal(new byte[] { 204, 204, 204, 255, 40, 40, 40, 77 }, rgba);

            byte[] partial = { 100, 100, 100, 100 };
            MapTiles.Darken(partial, 3, 0.5f);
            Assert.Equal(new byte[] { 50, 50, 50, 100 }, partial);       // a trailing partial pixel is left
        }

        static byte[] Picture(int size, Func<int, int, byte> luma, int channels = 3)
        {
            var pixels = new byte[size * size * channels];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    int p = (y * size + x) * channels;
                    byte v = luma(x, y);
                    pixels[p] = pixels[p + 1] = pixels[p + 2] = v;
                    if (channels == 4) pixels[p + 3] = 7;
                }
            return pixels;
        }

        [Fact]
        public void ABrightPictureIsDarkenedToTheGamesUnderTheName()
        {
            float factor = MapTiles.DarkenFor(Picture(512, (x, y) => 200), 512, 512, 3);
            Assert.Equal(MapTiles.LabelBandLuma / 200f, factor, 4);

            // Swiss Alps' colour map reads about 145 there (its 512 picture), the game's 128.4 and 120.4: it is
            // darkened by about 0.86, where it used to be by 0.8 whatever the map.
            Assert.Equal(0.858f, MapTiles.DarkenFor(Picture(512, (x, y) => 145), 512, 512, 3), 3);
        }

        [Fact]
        public void APictureAsDarkAsTheGamesIsLeftAndNoneIsBrightened()
        {
            Assert.Equal(1f, MapTiles.DarkenFor(Picture(256, (x, y) => 124), 256, 256, 3));
            Assert.Equal(1f, MapTiles.DarkenFor(Picture(256, (x, y) => 30), 256, 256, 3));
        }

        [Fact]
        public void APictureAllSnowIsDarkenedNoMoreThanTheLeast()
        {
            Assert.Equal(MapTiles.DarkestFactor, MapTiles.DarkenFor(Picture(256, (x, y) => 255), 256, 256, 3));
        }

        [Fact]
        public void OnlyTheBandUnderTheNameCounts()
        {
            // White above and below the band and at its very ends, mid grey where the name sits.
            int size = 500;
            Func<int, int, byte> picture = (x, y) => y >= 200 && y < 300 && x >= 40 && x < 460 ? (byte)100 : (byte)255;
            Assert.Equal(1f, MapTiles.DarkenFor(Picture(size, picture), size, size, 3));

            // And the other way round.
            Func<int, int, byte> inverse = (x, y) => y >= 200 && y < 300 && x >= 40 && x < 460 ? (byte)180 : (byte)0;
            Assert.Equal(MapTiles.LabelBandLuma / 180f, MapTiles.DarkenFor(Picture(size, inverse), size, size, 3), 4);
        }

        [Fact]
        public void TheBandIsReadAsLumaIgnoringAlpha()
        {
            float grey = MapTiles.DarkenFor(Picture(100, (x, y) => 200, channels: 4), 100, 100, 4);
            Assert.Equal(MapTiles.LabelBandLuma / 200f, grey, 4);

            // Pure green weighs 0.587: 255 * 0.587 = 149.7.
            var green = new byte[100 * 100 * 3];
            for (int p = 0; p < green.Length; p += 3) green[p + 1] = 255;
            Assert.Equal(MapTiles.LabelBandLuma / (255f * 0.587f), MapTiles.DarkenFor(green, 100, 100, 3), 3);
        }

        [Fact]
        public void NothingToReadIsNotDarkened()
        {
            Assert.Equal(1f, MapTiles.DarkenFor(null, 512, 512, 3));
            Assert.Equal(1f, MapTiles.DarkenFor(new byte[10], 512, 512, 3));      // shorter than said
            Assert.Equal(1f, MapTiles.DarkenFor(new byte[48], 4, 4, 1));          // not colour
            Assert.Equal(1f, MapTiles.DarkenFor(new byte[0], 0, 0, 3));
            Assert.Equal(1f, MapTiles.DarkenFor(Picture(1, (x, y) => 255), 1, 1, 3));   // no band at all
        }

        [Fact]
        public void ThePicturesFolderIsThePluginsOwn()
        {
            // BundleLoader.HashCacheFileName is Plugin.Guid + ".bundle-hashes.txt"; the pictures sit beside it.
            Assert.StartsWith("com.javoski.custommaps.", MapTiles.ThumbnailFolderName);
        }

        // --- the game ---

        /// <summary>
        /// The game's New Mission panel (<c>MissionEditorNewMenuV2</c>'s <c>Awake</c>, <c>MapSelected</c>,
        /// <c>Create</c> and <c>CreateUnnamed</c>, and <c>NewMissionMapButton</c>'s <c>SetMap</c> and
        /// <c>OnMapSelected</c>), read from its DLL, is the one the tiles were checked against
        /// (<see cref="MapTiles.VerifiedGameMethods"/>). Passes without checking anything where the game is
        /// not installed. After a game update that changes any of them it fails, here and in CI, until someone
        /// has decompiled them again (NOMapForge/tools/GameProbe) and set the new value.
        /// </summary>
        [Fact]
        public void TheInstalledGameHasTheNewMissionPanelThisWasCheckedAgainst()
        {
            string dll = IlFingerprintTests.GameAssembly();
            if (dll == null)
            {
                _output.WriteLine("Assembly-CSharp.dll not found; nothing checked");
                return;
            }

            ulong print = IlFingerprint.OfAll(MapTiles.GameMethods.Select(m => IlFingerprintTests.GameMethodPrint(dll, m[0], m[1], m[2])));
            _output.WriteLine($"{dll}: MissionEditorNewMenuV2 and NewMissionMapButton {print:X16}");

            Assert.True(print == MapTiles.VerifiedGameMethods,
                $"The game's New Mission panel has changed (fingerprint 0x{print:X16}UL). Decompile " +
                "MissionEditorNewMenuV2 and NewMissionMapButton (NOMapForge/tools/GameProbe), check Awake still binds " +
                "mapButtons[i] to Maps[i] and makes no tile, MapSelected still sets selectedMap and calls OnMapSelected " +
                "on every tile, Create and CreateUnnamed still make the key from selectedMap.PrefabName, and SetMap still " +
                "sets the name and picture; then set MapTiles.VerifiedGameMethods to that value.");
        }

        [Fact]
        public void TheFingerprintCoversEachMethodOnce()
        {
            Assert.Equal(6, MapTiles.GameMethods.Length);
            Assert.Equal(6, MapTiles.GameMethods.Select(m => m[1] + "." + m[2]).Distinct().Count());
            Assert.All(MapTiles.GameMethods, m => Assert.Equal("NuclearOption.MissionEditorScripts", m[0]));
        }
    }
}
