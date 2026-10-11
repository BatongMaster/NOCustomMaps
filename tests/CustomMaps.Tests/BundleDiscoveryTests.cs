using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace CustomMaps.Tests
{
    public class BundleDiscoveryTests
    {
        // A stand-in filesystem. Injecting it is what makes the precedence and dedupe
        // rules — the parts with actual edge cases — testable without laying down files.
        static Func<string, IEnumerable<string>> Fs(params (string Dir, string[] Files)[] entries)
        {
            var map = entries.ToDictionary(e => e.Dir, e => e.Files, StringComparer.OrdinalIgnoreCase);
            return dir => map.TryGetValue(dir, out string[] files) ? files : null;
        }

        static string Plugins => Path.Combine("C:", "game", "BepInEx", "plugins", "NOCustomMaps", "maps");
        static string LocalLow => Path.Combine("C:", "users", "j", "LocalLow", "CustomMaps");

        [Fact]
        public void FindsBundlesAndIgnoresEverythingElse()
        {
            var fs = Fs((Plugins, new[]
            {
                Path.Combine(Plugins, "kastellan-1.0.0.nomap"),
                Path.Combine(Plugins, "kastellan-1.0.0.nomap.json"),   // sidecar
                Path.Combine(Plugins, "kastellan-1.0.0.manifest"),
                Path.Combine(Plugins, "CustomMaps.dll"),
                Path.Combine(Plugins, "notes.txt"),
            }));

            var found = BundleDiscovery.Scan(new[] { Plugins }, fs);

            Assert.Single(found);
            Assert.EndsWith("kastellan-1.0.0.nomap", found[0].Path);
        }

        // NOMapLoader globs *map_* across the plugin directory, which also matches its
        // own DLL. A dedicated extension cannot do that.
        [Fact]
        public void DoesNotMatchTheSidecar()
        {
            Assert.True(BundleDiscovery.IsBundlePath(@"C:\x\a.nomap"));
            Assert.True(BundleDiscovery.IsBundlePath(@"C:\x\a.NOMAP"));
            Assert.False(BundleDiscovery.IsBundlePath(@"C:\x\a.nomap.json"));
            Assert.False(BundleDiscovery.IsBundlePath(@"C:\x\some_map_thing.dll"));
            Assert.False(BundleDiscovery.IsBundlePath(""));
            Assert.False(BundleDiscovery.IsBundlePath(null));
        }

        [Fact]
        public void ScansInPrecedenceOrder()
        {
            var fs = Fs(
                (Plugins, new[] { Path.Combine(Plugins, "alpha-1.0.0.nomap") }),
                (LocalLow, new[] { Path.Combine(LocalLow, "bravo-1.0.0.nomap") }));

            var found = BundleDiscovery.Scan(new[] { Plugins, LocalLow }, fs);

            Assert.Equal(2, found.Count);
            Assert.EndsWith("alpha-1.0.0.nomap", found[0].Path);
            Assert.Equal(0, found[0].SourceRank);
            Assert.EndsWith("bravo-1.0.0.nomap", found[1].Path);
            Assert.Equal(1, found[1].SourceRank);
        }

        // AssetBundle.LoadFromFile throws on a second load of the same file, so a
        // directory listed twice in config — or reached through a junction — must not
        // produce two entries.
        [Fact]
        public void DedupesTheSameFileReachedTwice()
        {
            var fs = Fs((Plugins, new[] { Path.Combine(Plugins, "kastellan-1.0.0.nomap") }));

            var found = BundleDiscovery.Scan(new[] { Plugins, Plugins }, fs);

            Assert.Single(found);
        }

        [Fact]
        public void DedupeIsCaseInsensitive()
        {
            string upper = Path.Combine(Plugins, "KASTELLAN-1.0.0.NOMAP");
            string lower = Path.Combine(Plugins, "kastellan-1.0.0.nomap");
            var fs = Fs((Plugins, new[] { lower, upper }));

            Assert.Single(BundleDiscovery.Scan(new[] { Plugins }, fs));
        }

        [Fact]
        public void MissingAndBlankDirectoriesAreSkipped()
        {
            var fs = Fs((Plugins, new[] { Path.Combine(Plugins, "kastellan-1.0.0.nomap") }));

            var found = BundleDiscovery.Scan(
                new[] { @"C:\does\not\exist", "", "   ", null, Plugins }, fs);

            Assert.Single(found);
        }

        [Fact]
        public void NullDirectoryListReturnsEmpty()
        {
            Assert.Empty(BundleDiscovery.Scan(null, _ => null));
        }

        [Fact]
        public void RejectsANullLister()
        {
            Assert.Throws<ArgumentNullException>(() => BundleDiscovery.Scan(new[] { Plugins }, null));
        }

        [Theory]
        [InlineData("kastellan-1.0.0.nomap", "kastellan")]
        [InlineData("kastellan-basin-1.0.0.nomap", "kastellan-basin")]   // split on the LAST hyphen
        [InlineData("kastellan-v2.nomap", "kastellan")]
        [InlineData("kastellan.nomap", "kastellan")]                     // no version field
        [InlineData("north-ridge.nomap", "north-ridge")]                 // tail is not a version
        public void ParsesTheMapIdHintFromTheFileName(string fileName, string expected)
        {
            Assert.Equal(expected, BundleDiscovery.ParseMapIdHint(@"C:\x\" + fileName));
        }

        [Fact]
        public void FindsCompressedBundlesToo()
        {
            var fs = Fs((Plugins, new[]
            {
                Path.Combine(Plugins, "alpha-1.0.0.nomap"),
                Path.Combine(Plugins, "bravo-2.0.0.nomapz"),
                Path.Combine(Plugins, "bravo-2.0.0.nomapz.json"),       // a sidecar of it
                Path.Combine(Plugins, "bravo-2.0.0.nomapz.partial"),    // Map Forge's file still being written
            }));

            var found = BundleDiscovery.Scan(new[] { Plugins }, fs);

            Assert.Equal(2, found.Count);
            Assert.EndsWith("alpha-1.0.0.nomap", found[0].Path);
            Assert.False(found[0].Packed);
            Assert.EndsWith("bravo-2.0.0.nomapz", found[1].Path);
            Assert.True(found[1].Packed);
            Assert.Equal("bravo", found[1].FileNameHint);
        }

        [Fact]
        public void TellsTheTwoExtensionsApart()
        {
            Assert.True(BundleDiscovery.IsBundlePath(@"C:\x\a.nomapz"));
            Assert.True(BundleDiscovery.IsBundlePath(@"C:\x\a.NOMAPZ"));
            Assert.False(BundleDiscovery.IsBundlePath(@"C:\x\a.nomapz.json"));
            Assert.True(BundleDiscovery.IsPackedPath(@"C:\x\a.nomapz"));
            Assert.False(BundleDiscovery.IsPackedPath(@"C:\x\a.nomap"));
            Assert.False(BundleDiscovery.IsPackedPath(null));
        }

        // Unity opens one bundle of a map, and the .nomap would be opened first.
        [Fact]
        public void PrefersTheNomapzWhenBothFormsOfABuildAreInstalled()
        {
            var fs = Fs((Plugins, new[]
            {
                Path.Combine(Plugins, "swissalps-0.4.0.nomap"),
                Path.Combine(Plugins, "swissalps-0.4.0.nomapz"),
                Path.Combine(Plugins, "kastellan-1.0.0.nomap"),
            }));

            var found = BundleDiscovery.Scan(new[] { Plugins }, fs);

            Assert.Equal(2, found.Count);
            DiscoveredBundle packed = found.Single(b => b.Packed);
            Assert.EndsWith("swissalps-0.4.0.nomapz", packed.Path);
            Assert.Single(packed.PreferredOver);
            Assert.EndsWith("swissalps-0.4.0.nomap", packed.PreferredOver[0]);
            Assert.Contains(found, b => b.Path.EndsWith("kastellan-1.0.0.nomap"));
        }

        [Fact]
        public void PrefersTheNomapzAcrossDirectoriesAndCase()
        {
            var fs = Fs(
                (Plugins, new[] { Path.Combine(Plugins, "SwissAlps-0.4.0.NOMAP") }),
                (LocalLow, new[] { Path.Combine(LocalLow, "swissalps-0.4.0.nomapz") }));

            var found = BundleDiscovery.Scan(new[] { Plugins, LocalLow }, fs);

            Assert.Single(found);
            Assert.True(found[0].Packed);
            Assert.Equal(1, found[0].SourceRank);
            Assert.Single(found[0].PreferredOver);
        }

        // Another build of the map is not the same file: both are offered, and Unity's refusal of the
        // second is explained where it happens.
        [Fact]
        public void KeepsANomapOfAnotherVersion()
        {
            var fs = Fs((Plugins, new[]
            {
                Path.Combine(Plugins, "swissalps-0.3.0.nomap"),
                Path.Combine(Plugins, "swissalps-0.4.0.nomapz"),
            }));

            var found = BundleDiscovery.Scan(new[] { Plugins }, fs);

            Assert.Equal(2, found.Count);
            Assert.All(found, b => Assert.Empty(b.PreferredOver));
        }

        [Fact]
        public void SidecarPathSitsBesideTheBundle()
        {
            Assert.Equal(@"C:\x\a.nomap.json", BundleDiscovery.SidecarPathFor(@"C:\x\a.nomap"));
            Assert.EndsWith(BundleDiscovery.SidecarSuffix, BundleDiscovery.SidecarPathFor(@"C:\x\a.nomap"));
            Assert.Null(BundleDiscovery.SidecarPathFor(null));
        }
    }
}
