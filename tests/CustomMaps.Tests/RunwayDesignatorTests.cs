using System;
using Xunit;

namespace CustomMaps.Tests
{
    /// <summary>
    /// The runway numbers, against the game's own rule (<c>Airbase.Runway.GetName</c>: tenths of the
    /// heading rounded half to even, 0 read as 36) and the shipped airfields it was measured on. The
    /// plugin shares the file and these cases.
    /// </summary>
    public class RunwayDesignatorTests
    {
        [Theory]
        [InlineData(90f, "09")]
        [InlineData(270f, "27")]
        [InlineData(45f, "04")]   // 4.5, half to even
        [InlineData(55f, "06")]   // 5.5, half to even
        [InlineData(15f, "02")]   // 1.5, half to even
        [InlineData(25f, "02")]   // 2.5, half to even
        [InlineData(5f, "36")]    // 0.5 rounds to 0, which is 36
        [InlineData(355f, "36")]  // 35.5 rounds to 36
        [InlineData(0f, "36")]
        [InlineData(359.9f, "36")]
        [InlineData(4.3f, "36")]  // Sandrift's runway, painted 36
        [InlineData(184.3f, "18")]
        [InlineData(108.4f, "11")] // North Boscali, painted 11 / 29
        [InlineData(288.4f, "29")]
        [InlineData(244.7f, "24")] // the naval NE airbase, painted 24 / 06
        [InlineData(64.7f, "06")]
        [InlineData(330f, "33")]  // what the game calls City Airport's runway (painted 34 by hand)
        [InlineData(135f, "14")]
        [InlineData(94.9f, "09")]
        [InlineData(95.1f, "10")]
        public void TheNumberIsTheHeadingInTensRoundedAsTheGameDoes(float bearing, string expected)
            => Assert.Equal(expected, RunwayDesignator.Text(bearing));

        [Fact]
        public void EveryHeadingHasANumberFromOneTo36InTwoDigits()
        {
            for (int tenth = 0; tenth < 3600; tenth++)
            {
                float bearing = tenth * 0.1f;
                int number = RunwayDesignator.Number(bearing);
                Assert.InRange(number, 1, 36);
                Assert.Equal(2, RunwayDesignator.Text(bearing).Length);

                // The nearest ten degrees, worked out apart from the code under test, in decimal
                // tenths of a degree: away from a half there is only one, and the halves are the
                // cases above (where float noise is what NearBoundary flags).
                if (tenth % 100 == 50) continue;
                int nearest = (tenth + 50) / 100;
                Assert.Equal(nearest == 0 ? 36 : nearest, number);
            }
        }

        [Theory]
        [InlineData(0f, 1f, 0f)]
        [InlineData(1f, 0f, 90f)]
        [InlineData(0f, -1f, 180f)]
        [InlineData(-1f, 0f, 270f)]
        [InlineData(1f, 1f, 45f)]
        public void BearingsAreClockwiseFromNorth(float dx, float dz, float expected)
            => Assert.Equal(expected, RunwayDesignator.Bearing(dx, dz), 3);

        [Fact]
        public void BothEndsAreReciprocal()
        {
            Assert.Equal(("09", "27"), RunwayDesignator.Ends(-1000f, 0f, 1000f, 0f));
            Assert.Equal(("27", "09"), RunwayDesignator.Ends(1000f, 0f, -1000f, 0f));
            Assert.Equal(("36", "18"), RunwayDesignator.Ends(0f, -1000f, 0f, 1000f));

            // 45 is 04 and 225 is 22, both halves rounded to the even number.
            Assert.Equal(("04", "22"), RunwayDesignator.Ends(0f, 0f, 1000f, 1000f));
        }

        [Fact]
        public void BearingsAreBroughtIntoOneTurn()
        {
            Assert.Equal(10f, RunwayDesignator.Normalise(370f), 3);
            Assert.Equal(350f, RunwayDesignator.Normalise(-10f), 3);
            Assert.Equal(0f, RunwayDesignator.Normalise(360f));
            Assert.InRange(RunwayDesignator.Normalise(-1e-6f), 0f, 359.9999f);
            Assert.Equal(0f, RunwayDesignator.Normalise(float.NaN));
            Assert.Equal(180f, RunwayDesignator.Reciprocal(0f), 3);
            Assert.Equal(10f, RunwayDesignator.Reciprocal(190f), 3);
        }

        /// <summary>The game reads the heading back off transforms turned in floats, so near a half it
        /// may round the other way from the paint: NW Airbase's 135.0° runway is painted 14 and called
        /// "Runway 13".</summary>
        [Fact]
        public void HeadingsNearAHalfAreFlagged()
        {
            Assert.True(RunwayDesignator.NearBoundary(135f));
            Assert.True(RunwayDesignator.NearBoundary(134.97f));
            Assert.True(RunwayDesignator.NearBoundary(5.03f));
            Assert.False(RunwayDesignator.NearBoundary(135.1f));
            Assert.False(RunwayDesignator.NearBoundary(130f));
            Assert.False(RunwayDesignator.NearBoundary(90f));
        }
    }
}
