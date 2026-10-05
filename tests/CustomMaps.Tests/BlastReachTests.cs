using Xunit;

namespace CustomMaps.Tests
{
    public class BlastReachTests
    {
        // The warhead in the September log: BlastManager drew a scorch of 8,184.209 m, which
        // Shockwave.Start sets to blastRadius * 0.5, so blastRadius is 16,368 m and the overlap
        // twice that. The game takes blastPower = Pow(yield * 1e6, 0.3333), so the yield is
        // blastPower^(1 / 0.3333) / 1e6 kilotons with blastPower = 16,368 / 13: about 2 Mt.
        [Fact]
        public void ShockwaveRadiusMatchesTheLoggedScorch()
        {
            float blastPower = 8184.209f * 2f / 13f;
            float yieldKilotons = (float)(System.Math.Pow(blastPower, 1.0 / 0.3333f) / 1000000.0);

            Assert.InRange(yieldKilotons, 1900f, 2100f);

            Assert.Equal(8184.209f * 4f, BlastReach.ShockwaveRadius(yieldKilotons), 1f);
        }

        [Fact]
        public void ShockwaveBelowTheGamesFloorQueriesNothing()
        {
            Assert.Equal(0f, BlastReach.ShockwaveRadius(0.0001f));
            Assert.True(BlastReach.ShockwaveRadius(0.0002f) > 0f);
        }

        // A sphere the buffer already holds with room to spare leaves it alone.
        [Theory]
        [InlineData(0)]
        [InlineData(754)]
        [InlineData(4095)]
        public void ASphereThatFitsKeepsTheBuffer(int found)
        {
            Assert.Equal(BlastReach.GameBuffer, BlastReach.BufferFor(found, BlastReach.GameBuffer));
        }

        // A sphere as big as the buffer or bigger gets the next power of two above it, so the
        // game's own query into the new buffer comes back short of full.
        [Theory]
        [InlineData(4096, 8192)]
        [InlineData(22923, 32768)]      // Zurich Airport's city buildings under the logged warhead
        [InlineData(32768, 65536)]
        public void AnOverflowingSphereGetsTheNextPowerOfTwoAbove(int found, int expected)
        {
            int size = BlastReach.BufferFor(found, BlastReach.GameBuffer);
            Assert.Equal(expected, size);
            Assert.True(size > found);
        }

        [Fact]
        public void AGrownBufferIsKeptAndNeverShrinks()
        {
            Assert.Equal(32768, BlastReach.BufferFor(5285, 32768));
            Assert.Equal(32768, BlastReach.BufferFor(22923, 16384));
        }

        [Fact]
        public void GrowthStopsAtTheCeiling()
        {
            Assert.Equal(BlastReach.MaxBuffer, BlastReach.BufferFor(10000000, BlastReach.GameBuffer));
            Assert.Equal(BlastReach.MaxBuffer, BlastReach.BufferFor(BlastReach.MaxBuffer, BlastReach.MaxBuffer));
        }
    }
}
