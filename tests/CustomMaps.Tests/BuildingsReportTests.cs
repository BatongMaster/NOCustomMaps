using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace CustomMaps.Tests
{
    /// <summary>
    /// The validation block's buildings line. The case that matters is the bundle of 2026-10-02:
    /// a manifest naming <c>swissalps_cities</c> and no such asset, which the block used to pass
    /// with <c>=> OK</c> while the map had no towns.
    /// </summary>
    public class BuildingsReportTests
    {
        static byte[] Placements(int count)
        {
            var placements = new List<Placement>();
            for (int i = 0; i < count; i++)
                placements.Add(new Placement { Type = (byte)(i % CityCatalogue.Entries.Length), X = i, Y = 100f, Z = -i, Yaw = 90f });

            string path = Path.Combine(Path.GetTempPath(), "cm-buildings-" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                CityData.Write(path, placements);
                return File.ReadAllBytes(path);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ANamedAssetMissingFromTheBundleWarnsThatTheMapHasNoTowns()
        {
            string line = BuildingsReport.Describe("swissalps_cities", null);

            Assert.Contains("'swissalps_cities' named but not in the bundle", line);
            Assert.Contains("WARN", line);
            Assert.Contains("no towns", line);
            Assert.Contains("Rebuild the map", line);
        }

        [Fact]
        public void PlacementsInTheBundleAreCountedAndPass()
        {
            string line = BuildingsReport.Describe("swissalps_cities", Placements(1234));

            Assert.Equal("1,234 placement(s) in 'swissalps_cities'  OK", line);
        }

        [Fact]
        public void AMapThatNamesNoBuildingsIsNotWarnedAbout()
        {
            Assert.DoesNotContain("WARN", BuildingsReport.Describe(null, null));
            Assert.DoesNotContain("WARN", BuildingsReport.Describe("", null));
        }

        [Fact]
        public void AnEmptyTableWarns()
        {
            Assert.Contains("WARN", BuildingsReport.Describe("swissalps_cities", Placements(0)));
        }

        /// <summary>A foreign file and one cut short both fail as a line, never as an exception that
        /// would take the whole validation block with it.</summary>
        [Fact]
        public void UnreadableDataWarnsInsteadOfThrowing()
        {
            Assert.Contains("is not city data", BuildingsReport.Describe("swissalps_cities", new byte[] { 1, 2, 3 }));

            byte[] whole = Placements(10);
            byte[] cut = new byte[whole.Length - 7];
            Array.Copy(whole, cut, cut.Length);
            string line = BuildingsReport.Describe("swissalps_cities", cut);

            Assert.Contains("is not city data", line);
            Assert.Contains("WARN", line);
        }

        static byte[] Head(byte[] whole)
        {
            var head = new byte[Math.Min(whole.Length, BuildingsReport.HeaderLength)];
            Array.Copy(whole, head, head.Length);
            return head;
        }

        /// <summary>The plugin passes only the header and the asset's length, rather than a copy of
        /// all 1.3 MB of Swiss Alps' placements.</summary>
        [Fact]
        public void TheHeaderAndTheLengthAreEnough()
        {
            byte[] whole = Placements(1234);

            Assert.Equal(12, BuildingsReport.HeaderLength);
            Assert.Equal("1,234 placement(s) in 'swissalps_cities'  OK",
                         BuildingsReport.Describe("swissalps_cities", Head(whole), whole.Length));
        }

        /// <summary>The count from the header is the count <c>CityData.Read</c> returns, and it fails
        /// where that would: this is what pins the plugin's own idea of the layout to Core's.</summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(7)]
        [InlineData(1000)]
        public void TheHeaderCountIsCityDataReadsCount(int count)
        {
            byte[] whole = Placements(count);

            Assert.Equal(CityData.Read(whole).Count, BuildingsReport.CountPlacements(Head(whole), whole.Length));

            if (count == 0) return;
            Assert.Throws<EndOfStreamException>(() => CityData.Read(whole[..^1]));
            Assert.Throws<InvalidDataException>(() => BuildingsReport.CountPlacements(Head(whole), whole.Length - 1));
        }

        [Fact]
        public void ABodyTooShortForItsCountWarns()
        {
            byte[] whole = Placements(10);
            string line = BuildingsReport.Describe("swissalps_cities", Head(whole), whole.Length - 7);

            Assert.Contains("is not city data (cut short: 10 placement(s) need 212 bytes, it has 205)", line);
            Assert.Contains("WARN", line);
        }

        /// <summary>Bytes past the last placement are ignored, as <c>CityData.Read</c> ignores them.</summary>
        [Fact]
        public void BytesPastTheLastPlacementAreIgnored()
        {
            byte[] whole = Placements(3);

            Assert.Equal("3 placement(s) in 'swissalps_cities'  OK",
                         BuildingsReport.Describe("swissalps_cities", Head(whole), whole.Length + 100));
        }

        [Fact]
        public void ABadHeaderWarns()
        {
            byte[] whole = Placements(3);

            byte[] foreign = Head(whole);
            foreign[0] = (byte)'X';
            Assert.Contains("the magic does not match", BuildingsReport.Describe("c", foreign, whole.Length));

            byte[] huge = Head(whole);
            huge[11] = 0x7f;
            Assert.Contains("claims", BuildingsReport.Describe("c", huge, whole.Length));

            Assert.Contains("truncated", BuildingsReport.Describe("c", Head(whole)[..10], 10));
            Assert.Contains("truncated", BuildingsReport.Describe("c", Head(whole), 10));
        }
    }
}
