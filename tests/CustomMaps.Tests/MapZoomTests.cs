using Xunit;

namespace CustomMaps.Tests
{
    public class MapZoomTests
    {
        // The frame is mapScaleMaximized (900) plus 20; the image MapSize / 81920 * 900.
        const float Frame = 920f;
        const float ShippedImage = 900f;
        const float SwissImage = 199680f / 81920f * 900f;   // 2,193.75

        // A base map keeps the game's floor whatever its size.
        [Theory]
        [InlineData(ShippedImage)]
        [InlineData(SwissImage)]
        public void ABaseMapKeepsTheGamesFloor(float image)
        {
            Assert.Equal(MapZoom.GameFloor, MapZoom.Floor(false, Frame, image, image));
        }

        // Swiss Alps zooms out until the whole map fills 90% of the frame.
        [Fact]
        public void SwissAlpsFitsTheFrameWithAMargin()
        {
            float floor = MapZoom.Floor(true, Frame, SwissImage, SwissImage);

            Assert.Equal(0.9f * 920f / 2193.75f, floor, 4);
            Assert.InRange(floor, 0.37f, 0.38f);
            Assert.Equal(MapZoom.FitShare * Frame, SwissImage * floor, 2);
        }

        // A custom map the frame already shows whole at zoom 1, with the margin, gets the game's
        // floor and no lower; a larger one goes below it.
        [Theory]
        [InlineData(400f, 1f)]
        [InlineData(828f, 1f)]
        [InlineData(900f, 0.92f)]
        public void NoCustomMapGoesAboveTheGamesFloor(float image, float expected)
        {
            Assert.Equal(expected, MapZoom.Floor(true, Frame, image, image), 4);
        }

        // An oblong map fits by its longer side.
        [Fact]
        public void AnOblongMapFitsByItsLongerSide()
        {
            Assert.Equal(MapZoom.Floor(true, Frame, SwissImage, SwissImage),
                         MapZoom.Floor(true, Frame, 1000f, SwissImage));
        }

        [Theory]
        [InlineData(0f, 2000f)]
        [InlineData(920f, 0f)]
        [InlineData(float.NaN, 2000f)]
        [InlineData(920f, float.NaN)]
        [InlineData(920f, float.PositiveInfinity)]
        public void ASizeThatCannotBeReadGetsTheGamesFloor(float frame, float image)
        {
            Assert.Equal(MapZoom.GameFloor, MapZoom.Floor(true, frame, image, image));
        }

        [Fact]
        public void AVastMapStopsAtTheLowest()
        {
            Assert.Equal(MapZoom.Lowest, MapZoom.Floor(true, Frame, 1000000f, 1000000f));
        }

        // At and above the game's floor the pan is the game's (or the widening's), untouched.
        [Theory]
        [InlineData(1f)]
        [InlineData(2f)]
        [InlineData(40f)]
        public void PanIsUntouchedFromZoomOneUp(float zoom)
        {
            Assert.Equal(3.4f, MapZoom.PanFactor(3.4f, zoom, 0.377f));
        }

        // A base map's floor is the game's, so its pan is never touched, even at a stray zoom
        // below 1 left over from a custom map.
        [Fact]
        public void PanIsUntouchedOnABaseMap()
        {
            Assert.Equal(2f, MapZoom.PanFactor(2f, 0.5f, MapZoom.GameFloor));
        }

        // Fully zoomed out there is no room: the map is centred.
        [Fact]
        public void PanIsNoneAtTheFloor()
        {
            Assert.Equal(0f, MapZoom.PanFactor(3.4f, 0.377f, 0.377f));
            Assert.Equal(0f, MapZoom.PanFactor(3.4f, 0.37f, 0.377f));
        }

        // In between, the room grows with the zoom, without a jump at either end.
        [Fact]
        public void PanGrowsSmoothlyFromTheFloorToOne()
        {
            const float floor = 0.377f;
            float previous = 0f;

            for (float zoom = floor; zoom < 1f; zoom *= 1.05f)
            {
                float factor = MapZoom.PanFactor(3.4f, zoom, floor);
                Assert.True(factor >= previous, $"zoom {zoom}: {factor} after {previous}");
                Assert.True(factor - previous < 0.3f, $"zoom {zoom}: jumped from {previous} to {factor}");
                previous = factor;
            }

            Assert.Equal(3.4f, MapZoom.PanFactor(3.4f, 0.9999f, floor), 2);
        }

        [Fact]
        public void PanIsUntouchedWhenTheZoomCannotBeRead()
        {
            Assert.Equal(2f, MapZoom.PanFactor(2f, float.NaN, 0.377f));
            Assert.Equal(2f, MapZoom.PanFactor(2f, 0.5f, float.NaN));
        }

        // A map following the aircraft (offset zero) that only the narrowing holds in goes on
        // following, so zooming back in finds the aircraft.
        [Theory]
        [InlineData(0f, 0f)]
        [InlineData(0f, -0f)]
        [InlineData(5e-6f, 5e-6f)]
        public void AFollowingMapGoesOnFollowingThroughTheNarrowing(float x, float y)
        {
            float narrowed = MapZoom.PanFactor(3.4f, 0.5f, 0.377f);
            Assert.True(MapZoom.KeepsFollowing(3.4f, narrowed, x, y));
            Assert.True(MapZoom.KeepsFollowing(3.4f, 0f, x, y));
        }

        // A map the player has dragged keeps the game's handling: the clamp's offset stands.
        [Theory]
        [InlineData(0.01f, 0f)]
        [InlineData(0f, -120f)]
        [InlineData(float.NaN, 0f)]
        public void ADraggedMapIsLeftToTheGame(float x, float y)
        {
            Assert.False(MapZoom.KeepsFollowing(3.4f, 1f, x, y));
        }

        // From zoom 1 up, and on a base map, nothing is narrowed, so following is the game's.
        [Theory]
        [InlineData(1f, 0.377f)]
        [InlineData(4f, 0.377f)]
        [InlineData(0.5f, 1f)]
        public void WithoutNarrowingFollowingIsTheGames(float zoom, float floor)
        {
            float narrowed = MapZoom.PanFactor(3.4f, zoom, floor);
            Assert.False(MapZoom.KeepsFollowing(3.4f, narrowed, 0f, 0f));
            Assert.False(MapZoom.KeepsFollowing(float.NaN, 0f, 0f, 0f));
        }
    }
}
