using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Xunit;

namespace CustomMaps.Tests
{
    public class JsonTests
    {
        [Fact]
        public void ParsesTheShapesAManifestUses()
        {
            var root = Json.AsObject(Json.Parse(@"{
                ""mapId"": ""kastellan"",
                ""mapSizeX"": 143360,
                ""latitude"": -12.5,
                ""enabled"": true,
                ""absent"": null,
                ""assets"": { ""rootPrefab"": ""Kastellan"" },
                ""list"": [1, ""two"", false, null]
            }"));

            Assert.Equal("kastellan", Json.GetString(root, "mapId"));
            Assert.Equal(143360f, Json.GetFloat(root, "mapSizeX"));
            Assert.Equal(-12.5f, Json.GetFloat(root, "latitude"));
            Assert.True(Json.GetBool(root, "enabled"));

            Assert.False(Json.Has(root, "absent"));
            Assert.False(Json.Has(root, "missing"));
            Assert.Equal("fallback", Json.GetString(root, "absent", "fallback"));

            var assets = Json.AsObject(root["assets"], "assets");
            Assert.Equal("Kastellan", Json.GetString(assets, "rootPrefab"));

            var list = Json.AsArray(root["list"], "list");
            Assert.Equal(4, list.Count);
            Assert.Equal(1d, list[0]);
            Assert.Equal("two", list[1]);
            Assert.Equal(false, list[2]);
            Assert.Null(list[3]);
        }

        [Fact]
        public void HandlesEscapes()
        {
            var root = Json.AsObject(Json.Parse(@"{ ""s"": ""a\""b\\c\/d\ne\tf\u00e9"" }"));
            Assert.Equal("a\"b\\c/d\ne\tf\u00e9", Json.GetString(root, "s"));
        }

        [Fact]
        public void HandlesEmptyContainersAndWhitespace()
        {
            Assert.Empty(Json.AsObject(Json.Parse("  {  }  ")));
            Assert.Empty(Json.AsArray(Json.Parse("\n[\t]\r\n")));
        }

        [Theory]
        [InlineData("1e3", 1000d)]
        [InlineData("1E3", 1000d)]
        [InlineData("1.5e-2", 0.015d)]
        [InlineData("-0.25", -0.25d)]
        [InlineData("0", 0d)]
        public void ParsesNumberForms(string literal, double expected)
        {
            var root = Json.AsObject(Json.Parse("{\"n\":" + literal + "}"));
            Assert.Equal(expected, Json.GetNumber(root, "n"), 10);
        }

        // The generator writes manifests on a Swiss dev machine. A culture-sensitive
        // parse would read "0.5" as 5 and put the map's camera 10x too high.
        [Fact]
        public void IsCultureInvariant()
        {
            CultureInfo original = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-CH");
                var root = Json.AsObject(Json.Parse(@"{ ""n"": 0.5 }"));
                Assert.Equal(0.5f, Json.GetFloat(root, "n"));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("{")]
        [InlineData("{\"a\"}")]
        [InlineData("{\"a\":}")]
        [InlineData("{\"a\":1,}")]
        [InlineData("[1,]")]
        [InlineData("{\"a\":1} trailing")]
        [InlineData("\"unterminated")]
        [InlineData("{\"a\":\"\\q\"}")]
        [InlineData("{\"a\":tru}")]
        [InlineData("{a:1}")]
        public void RejectsMalformedInput(string text)
        {
            Assert.Throws<JsonException>(() => Json.Parse(text));
        }

        [Fact]
        public void WrongTypeIsAnErrorEvenThoughAbsentIsNot()
        {
            var root = Json.AsObject(Json.Parse(@"{ ""n"": ""not a number"", ""s"": 1 }"));

            Assert.Throws<JsonException>(() => Json.GetNumber(root, "n"));
            Assert.Throws<JsonException>(() => Json.GetString(root, "s"));
            Assert.Throws<JsonException>(() => Json.GetBool(root, "s"));

            // Absent, however, quietly takes the fallback.
            Assert.Equal(7d, Json.GetNumber(root, "missing", 7d));
        }

        [Fact]
        public void GetIntRejectsANonInteger()
        {
            var root = Json.AsObject(Json.Parse(@"{ ""i"": 1.5, ""j"": 42 }"));
            Assert.Throws<JsonException>(() => Json.GetInt(root, "i"));
            Assert.Equal(42, Json.GetInt(root, "j"));
        }

        [Fact]
        public void LastDuplicateKeyWins()
        {
            var root = Json.AsObject(Json.Parse(@"{ ""a"": 1, ""a"": 2 }"));
            Assert.Equal(2d, Json.GetNumber(root, "a"));
        }

        [Fact]
        public void AccessorsToleratePassingNull()
        {
            Assert.Null(Json.GetString(null, "a"));
            Assert.Equal(0d, Json.GetNumber(null, "a"));
            Assert.False(Json.GetBool(null, "a"));
            Assert.False(Json.Has(null, "a"));
        }

        [Fact]
        public void AsObjectAndAsArrayRejectTheWrongShape()
        {
            Assert.Throws<JsonException>(() => Json.AsObject(Json.Parse("[]"), "manifest"));
            Assert.Throws<JsonException>(() => Json.AsArray(Json.Parse("{}"), "list"));
        }
    }
}
