using Xunit;

namespace CustomMaps.Tests
{
    /// <summary>
    /// Which installed bundle a map key starts reading. A wrong yes costs 2.5 GB for a map nobody
    /// plays; a wrong no only costs the wait it was meant to save.
    /// </summary>
    public class WarmUpPolicyTests
    {
        const string Hash = "aeb1979233df05b98dd71e0144486a580e970bac37ce3582b64e579de31bb326";

        [Fact]
        public void TheKeyOfTheInstalledBuildWantsIt()
        {
            Assert.True(WarmUpPolicy.Wants("cm.swissalps.aeb19792", "swissalps", Hash));
        }

        /// <summary>A mission made on 0.3.0 does not load on 0.4.0 (its name embeds the other hash),
        /// so the 0.4.0 installed is not read for it.</summary>
        [Fact]
        public void AnotherBuildOfTheMapDoesNotOnceTheHashIsIn()
        {
            Assert.False(WarmUpPolicy.Wants("cm.swissalps.0badf00d", "swissalps", Hash));
        }

        /// <summary>A mission picked while the bundles are still being hashed, on the first start
        /// after installing a map.</summary>
        [Fact]
        public void BeforeTheHashIsInTheMapIdDecides()
        {
            Assert.True(WarmUpPolicy.Wants("cm.swissalps.0badf00d", "swissalps", null));
            Assert.True(WarmUpPolicy.Wants("cm.swissalps.aeb19792", "swissalps", ""));
            Assert.False(WarmUpPolicy.Wants("cm.kellys-wake-island.eeaed7cf", "swissalps", null));
        }

        [Fact]
        public void ShippedMapsAndNonsenseNeverDo()
        {
            Assert.False(WarmUpPolicy.Wants("Terrain1", "swissalps", null));
            Assert.False(WarmUpPolicy.Wants("Terrain_naval", "swissalps", Hash));
            Assert.False(WarmUpPolicy.Wants("cm.swissalps", "swissalps", null));
            Assert.False(WarmUpPolicy.Wants("cm.swissalps.xyz", "swissalps", null));
            Assert.False(WarmUpPolicy.Wants("", "swissalps", null));
            Assert.False(WarmUpPolicy.Wants(null, "swissalps", null));
            Assert.False(WarmUpPolicy.Wants("cm.swissalps.aeb19792", null, Hash));
        }

        [Fact]
        public void TheMapIdIsMatchedExactly()
        {
            Assert.False(WarmUpPolicy.Wants("cm.swissalps.aeb19792", "SwissAlps", Hash));
            Assert.False(WarmUpPolicy.Wants("cm.swissalps2.aeb19792", "swissalps", Hash));
        }

        static readonly string[] Installed = { "Swiss Alps", "Kelly's Wake Island" };

        /// <summary>A lobby advertises its map by display name only; joining one on an installed
        /// map starts reading that map.</summary>
        [Fact]
        public void ALobbyOnAnInstalledMapNamesIt()
        {
            Assert.Equal(0, WarmUpPolicy.ForLobby("Swiss Alps", Installed));
            Assert.Equal(1, WarmUpPolicy.ForLobby("Kelly's Wake Island", Installed));
        }

        /// <summary>A shipped map, a map not installed, and a name the game's profanity filter or
        /// rich-text sanitiser changed on the way read nothing: the key the server sends decides.</summary>
        [Fact]
        public void ALobbyOnAnythingElseNamesNone()
        {
            Assert.Equal(-1, WarmUpPolicy.ForLobby("Heartland", Installed));
            Assert.Equal(-1, WarmUpPolicy.ForLobby("Kelly's Wake", Installed));
            Assert.Equal(-1, WarmUpPolicy.ForLobby("swiss alps", Installed));
            Assert.Equal(-1, WarmUpPolicy.ForLobby("Swiss Alps ", Installed));
            Assert.Equal(-1, WarmUpPolicy.ForLobby("Swiss ****", Installed));
            Assert.Equal(-1, WarmUpPolicy.ForLobby("", Installed));
            Assert.Equal(-1, WarmUpPolicy.ForLobby(null, Installed));
            Assert.Equal(-1, WarmUpPolicy.ForLobby("Swiss Alps", null));
            Assert.Equal(-1, WarmUpPolicy.ForLobby("Swiss Alps", new string[0]));
        }

        /// <summary>Two installed maps by one name: reading both would cost twice the memory for one
        /// map played, so neither is read until the key arrives.</summary>
        [Fact]
        public void ANameTwoInstalledMapsShareNamesNeither()
        {
            Assert.Equal(-1, WarmUpPolicy.ForLobby("Swiss Alps", new[] { "Swiss Alps", null, "Swiss Alps" }));
            Assert.Equal(2, WarmUpPolicy.ForLobby("Swiss Alps", new[] { null, "Alps", "Swiss Alps" }));
        }
    }
}
