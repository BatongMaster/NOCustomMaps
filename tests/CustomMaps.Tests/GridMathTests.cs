using Xunit;

namespace CustomMaps.Tests
{
    public class GridMathTests
    {
        // Our map. 143360 / 4096 = 35 exactly, so the vanilla cell size is kept and
        // the grid tiles the map with no clamped edge strip.
        [Fact]
        public void KastellanKeepsTheVanillaCellSize()
        {
            float cell = GridMath.ChooseHashGridSize(143360f);
            Assert.Equal(4096f, cell);
            Assert.Equal(35, GridMath.Divisions(143360f, cell));
            Assert.True(GridMath.DividesEvenly(143360f, cell));
        }

        // Both shipped maps. The chosen size must be a no-op for them, or the patch
        // would regenerate the grid on vanilla play and could regress stock behaviour.
        [Theory]
        [InlineData(81920f, 20)]     // Heartland, and Ignus on Y
        [InlineData(163840f, 40)]    // Ignus on X
        public void ShippedMapsAreUnchanged(float mapSize, int expectedDivisions)
        {
            float cell = GridMath.ChooseHashGridSize(mapSize);
            Assert.Equal(4096f, cell);
            Assert.Equal(expectedDivisions, GridMath.Divisions(mapSize, cell));
        }

        [Fact]
        public void FallsBackWhenNoPowerOfTwoDivides()
        {
            // 100000 is divisible by no power of two that also lands the division
            // count inside [8, 192], so this exercises the fallback path.
            float cell = GridMath.ChooseHashGridSize(100000f);
            Assert.True(GridMath.DividesEvenly(100000f, cell),
                $"chose {cell}, which leaves a remainder and therefore a clamped edge strip");
            Assert.InRange(GridMath.Divisions(100000f, cell), GridMath.MinDivisions, GridMath.MaxDivisions);
        }

        [Fact]
        public void AlwaysCoversTheMapAndStaysInRange()
        {
            // Sweep sizes a map author might plausibly pick, including deliberately
            // awkward ones. The postcondition is the whole point of the function: no
            // strip of the map may fall outside the last full cell, because everything
            // in such a strip clamps inward and goes effectively invisible to the
            // spatial hash.
            for (float size = 20000f; size <= GridMath.MaxSupportedMapSize; size += 1234f)
            {
                float cell = GridMath.ChooseHashGridSize(size);
                Assert.True(cell > 0f, $"size {size} produced cell {cell}");

                int divisions = GridMath.Divisions(size, cell);
                Assert.InRange(divisions, GridMath.MinDivisions, GridMath.MaxDivisions);

                float uncovered = size - divisions * cell;
                Assert.True(GridMath.DividesEvenly(size, cell),
                    $"size {size} cell {cell} ({divisions} divisions) leaves a {uncovered} m strip outside the grid");
            }
        }

        // The single-precision trap that the naive cell = mapSize / divisions falls
        // into: the quotient lands a hair high, the game's truncating division comes
        // back one short, and an entire cell along the top and right edge is orphaned.
        [Fact]
        public void CellForDivisionsNeverLosesACell()
        {
            for (float size = 20000f; size <= GridMath.MaxSupportedMapSize; size += 617f)
            {
                for (int n = GridMath.MinDivisions; n <= GridMath.MaxDivisions; n += 7)
                {
                    float cell = GridMath.CellForDivisions(size, n);
                    Assert.Equal(n, GridMath.Divisions(size, cell));
                    Assert.True(GridMath.DividesEvenly(size, cell));
                }
            }
        }

        [Fact]
        public void CellForDivisionsHandlesDegenerateInput()
        {
            Assert.Equal(GridMath.DefaultCell, GridMath.CellForDivisions(143360f, 0));
            Assert.Equal(GridMath.DefaultCell, GridMath.CellForDivisions(143360f, -3));
            Assert.Equal(GridMath.DefaultCell, GridMath.CellForDivisions(0f, 16));
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-1f)]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        public void DegenerateSizesReturnTheReference(float mapSize)
        {
            Assert.Equal(GridMath.DefaultCell, GridMath.ChooseHashGridSize(mapSize));
        }

        [Fact]
        public void DegenerateReferenceFallsBackToTheDefault()
        {
            Assert.Equal(4096f, GridMath.ChooseHashGridSize(143360f, 0f));
            Assert.Equal(4096f, GridMath.ChooseHashGridSize(143360f, float.NaN));
            Assert.Equal(4096f, GridMath.ChooseHashGridSize(143360f, -8f));
        }

        [Fact]
        public void DivisionsMatchesTheGamesTruncation()
        {
            // BattlefieldGrid.GenerateGrid does (int)(mapSize / gridSize).
            Assert.Equal(35, GridMath.Divisions(143360f, 4096f));
            Assert.Equal(24, GridMath.Divisions(100000f, 4096f));   // 24.41 truncates
            Assert.Equal(0, GridMath.Divisions(143360f, 0f));
            Assert.Equal(0, GridMath.Divisions(0f, 4096f));
        }

        [Fact]
        public void HalfExtentIsHalfTheMap()
        {
            Assert.Equal(71680f, GridMath.HalfExtent(143360f));
            Assert.Equal(128000f, GridMath.HalfExtent(256000f));

            // Single-precision world coordinates stay usable well past any size worth
            // building: GlobalPosition resolution at a 200 km half-extent is ~2.4 cm.
            const float halfExtent = 200000f;
            float ulp = System.MathF.BitIncrement(halfExtent) - halfExtent;
            Assert.True(ulp < 0.05f, $"float resolution at {halfExtent} m is {ulp} m");
        }
    }
}
