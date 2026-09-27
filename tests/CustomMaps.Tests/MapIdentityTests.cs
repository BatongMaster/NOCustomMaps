using System;
using System.Collections.Generic;
using Xunit;

namespace CustomMaps.Tests
{
    public class MapIdentityTests
    {
        [Fact]
        public void RoundTrips()
        {
            string name = MapIdentity.MakePrefabName("kastellan", "a1b2c3d4");
            Assert.Equal("cm.kastellan.a1b2c3d4", name);

            Assert.True(MapIdentity.TryParse(name, out string id, out string hash));
            Assert.Equal("kastellan", id);
            Assert.Equal("a1b2c3d4", hash);
        }

        [Fact]
        public void TruncatesAFullSha256()
        {
            const string full = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";
            Assert.Equal("cm.kastellan.a1b2c3d4", MapIdentity.MakePrefabName("kastellan", full));
        }

        [Fact]
        public void AcceptsHyphensInTheMapId()
        {
            string name = MapIdentity.MakePrefabName("kastellan-basin", "0123abcd");
            Assert.True(MapIdentity.TryParse(name, out string id, out _));
            Assert.Equal("kastellan-basin", id);
        }

        // The registrar tells its own entries from the shipped ones by whether the
        // name parses. Both vanilla names must fail.
        [Theory]
        [InlineData("Terrain1")]
        [InlineData("Terrain_naval")]
        [InlineData("")]
        [InlineData("cm.")]
        [InlineData("cm.kastellan")]                  // no hash
        [InlineData("cm.kastellan.a1b2c3")]           // hash too short
        [InlineData("cm.kastellan.a1b2c3d4e")]        // hash too long
        [InlineData("cm.kastellan.A1B2C3D4")]         // uppercase hex
        [InlineData("cm.kastellan.zzzzzzzz")]         // not hex
        [InlineData("cm.Kastellan.a1b2c3d4")]         // uppercase id
        [InlineData("cm.k.a1b2c3d4")]                 // id too short
        [InlineData("cm.-lead.a1b2c3d4")]             // leading hyphen
        [InlineData("cm.trail-.a1b2c3d4")]            // trailing hyphen
        [InlineData("cm.has space.a1b2c3d4")]
        [InlineData("cm.has_underscore.a1b2c3d4")]
        public void RejectsMalformedNames(string prefabName)
        {
            Assert.False(MapIdentity.TryParse(prefabName, out _, out _));
            Assert.Null(MapIdentity.IdOf(prefabName));
            Assert.Null(MapIdentity.HashOf(prefabName));
        }

        [Fact]
        public void TryParseHandlesNull()
        {
            Assert.False(MapIdentity.TryParse(null, out _, out _));
        }

        [Fact]
        public void MakePrefabNameRejectsBadInput()
        {
            Assert.Throws<ArgumentException>(() => MapIdentity.MakePrefabName("Bad Id", "a1b2c3d4"));
            Assert.Throws<ArgumentException>(() => MapIdentity.MakePrefabName("ok", "short"));
            Assert.Throws<ArgumentException>(() => MapIdentity.MakePrefabName("ok", "nothexxx"));
        }

        // The whole point of spelling FNV-1a out by hand rather than calling
        // string.GetHashCode, which .NET Core randomises per process — a server and a
        // client would compute different MapPrefixes from the same id. If this test
        // ever fails, the hash has moved and every already-built map's prefix moved
        // with it.
        //
        // The values are the two-byte-per-UTF-16-code-unit scheme this implementation
        // uses, so they are not the published FNV-1a vectors. Hashing only the low
        // byte of 'a' does reproduce the canonical 0xAF63DC4C8601EC8C, which is how
        // the algorithm itself was checked; the second byte then carries it here.
        [Fact]
        public void HashIsPinnedToTheseExactValues()
        {
            Assert.Equal(0x089BE207B544F1E4UL, MapIdentity.Fnv1a64("a"));
            Assert.Equal(0xCBC4876580C2F202UL, MapIdentity.Fnv1a64("kastellan"));
            Assert.Equal(0x3BC17289284345E0UL, MapIdentity.Fnv1a64("kastellan-basin"));
        }

        [Fact]
        public void PrefixIsPinnedAndStable()
        {
            Assert.Equal(50, MapIdentity.AllocatePrefix("kastellan"));
            Assert.Equal(16, MapIdentity.AllocatePrefix("kastellan-basin"));
            Assert.Equal(MapIdentity.AllocatePrefix("kastellan"), MapIdentity.AllocatePrefix("kastellan"));
        }

        [Fact]
        public void PrefixIsInRangeForALargeCorpus()
        {
            foreach (string id in Corpus())
            {
                int prefix = MapIdentity.AllocatePrefix(id);
                Assert.InRange(prefix, MapIdentity.MinPrefix, MapIdentity.MaxPrefix);
                Assert.True(MapIdentity.IsValidPrefix(prefix));

                // (MapPrefix << 24) | index must stay non-negative, or the PrefabHash
                // reads as a negative number in every log and diff.
                Assert.True((prefix << 24) > 0);
            }
        }

        [Fact]
        public void PrefixNeverLandsOnAShippedMapsBand()
        {
            foreach (string id in Corpus())
                Assert.True(MapIdentity.AllocatePrefix(id) > 2);
        }

        [Fact]
        public void MapNameRejectsWhatTheLobbyWouldMangle()
        {
            Assert.True(MapIdentity.IsValidMapName("Kastellan Basin", out _));

            // SanitizeRichText eats angle brackets.
            Assert.False(MapIdentity.IsValidMapName("Kastellan <b>Basin</b>", out string why));
            Assert.Contains("<", why);

            Assert.False(MapIdentity.IsValidMapName(new string('x', 41), out _));
            Assert.True(MapIdentity.IsValidMapName(new string('x', 40), out _));
            Assert.False(MapIdentity.IsValidMapName("", out _));
            Assert.False(MapIdentity.IsValidMapName(null, out _));
            Assert.False(MapIdentity.IsValidMapName("Kastellan—Basin", out _));   // em dash is not ASCII
        }

        static IEnumerable<string> Corpus()
        {
            yield return "kastellan";
            yield return "kastellan-basin";
            yield return "ab";
            yield return new string('z', MapIdentity.MapIdMaxLength);
            for (int i = 0; i < 500; i++) yield return "map-" + i.ToString("D4");
        }

        // ---- Sanitise ---------------------------------------------------------------------

        [Theory]
        [InlineData("Swiss Alps", "swiss-alps")]
        [InlineData("my_map_v2.png", "my-map-v2-png")]
        [InlineData("--Kastellan--Basin--", "kastellan-basin")]
        [InlineData("Zürich", "z-rich")]
        [InlineData("alps", "alps")]
        public void SanitiseProducesAValidId(string raw, string expected)
        {
            string id = MapIdentity.Sanitise(raw);
            Assert.Equal(expected, id);
            Assert.True(MapIdentity.IsValidMapId(id));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("___")]
        [InlineData("x")]
        public void SanitiseFallsBackWhenNothingUsableIsLeft(string raw)
        {
            Assert.Equal("custom", MapIdentity.Sanitise(raw));
            Assert.Equal("other", MapIdentity.Sanitise(raw, "other"));
        }

        [Fact]
        public void SanitiseTruncatesWithoutLeavingATrailingHyphen()
        {
            string id = MapIdentity.Sanitise(new string('a', 31) + " bbbb");

            Assert.True(id.Length <= MapIdentity.MapIdMaxLength);
            Assert.False(id.EndsWith("-"));
            Assert.True(MapIdentity.IsValidMapId(id));
        }
    }
}
