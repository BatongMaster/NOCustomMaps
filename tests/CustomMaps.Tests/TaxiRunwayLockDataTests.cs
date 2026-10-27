using System.Collections.Generic;
using System.IO;
using Xunit;
using Released140 = CustomMaps.Tests.Released140;
using Released141 = CustomMaps.Tests.Released141;

namespace CustomMaps.Tests
{
    /// <summary>
    /// The runway flags in <c>taxiways.bin</c> (one aircraft per runway): read back as written, nothing
    /// added to a file with every flag off, and a file with them still read whole by the plugin as
    /// released in 1.4.1 (<see cref="Released141.TaxiData"/>, which reads the take-off starts but not the
    /// flags) and in 1.4.0 (<see cref="Released140.TaxiData"/>, which reads neither). The plugin's copy of
    /// these tests is the same file.
    /// </summary>
    public class TaxiRunwayLockDataTests
    {
        static TaxiNetworkData Field(string name = "LSZH", bool starts = false, bool locked = false)
        {
            var field = new TaxiNetworkData { Airbase = name, ServicePoint = (1234.5f, -987.25f), LandingPad = (1300f, -950f) };
            field.Roads.Add(new[] { (0f, 0f), (0f, 30f), (12.5f, 55f) });
            field.Exits.Add(new TaxiExit { Runway = 0, X = 0f, Z = 0f, Directions = TaxiExit.Forward | TaxiExit.Reverse });
            if (starts)
                field.Takeoffs.Add(new TaxiTakeoff { Runway = 0, X = 400f, Z = 0f, Direction = TaxiExit.Forward });
            field.OneAircraftPerRunway = locked;
            return field;
        }

        static byte[] Bytes(IReadOnlyList<TaxiNetworkData> fields)
        {
            using (var stream = new MemoryStream())
            {
                TaxiData.Write(stream, fields);
                return stream.ToArray();
            }
        }

        /// <summary>The same fields through the 1.4.1 writer, which knows the take-off starts but not the flags.</summary>
        static byte[] Released141Bytes(IReadOnlyList<TaxiNetworkData> fields)
        {
            var old = new List<Released141.TaxiNetworkData>();
            foreach (TaxiNetworkData field in fields)
            {
                var copy = new Released141.TaxiNetworkData
                {
                    Airbase = field.Airbase, Source = (Released141.TaxiSource)(byte)field.Source,
                    ServicePoint = field.ServicePoint, LandingPad = field.LandingPad,
                };
                copy.Roads.AddRange(field.Roads);
                foreach (TaxiExit exit in field.Exits)
                    copy.Exits.Add(new Released141.TaxiExit { Runway = exit.Runway, X = exit.X, Z = exit.Z, Directions = exit.Directions });
                foreach (TaxiTakeoff start in field.Takeoffs)
                    copy.Takeoffs.Add(new Released141.TaxiTakeoff { Runway = start.Runway, X = start.X, Z = start.Z, Direction = start.Direction });
                old.Add(copy);
            }

            using (var stream = new MemoryStream())
            {
                Released141.TaxiData.Write(stream, old);
                return stream.ToArray();
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void RunwayFlagsAreReadBackAsWritten(bool starts)
        {
            List<TaxiNetworkData> read = TaxiData.Read(Bytes(new[]
            {
                Field("LSZH", starts, locked: true), Field("AB02", false, locked: false), Field("LSGS", starts, locked: true),
            }));

            Assert.Equal(3, read.Count);
            Assert.True(read[0].OneAircraftPerRunway);
            Assert.False(read[1].OneAircraftPerRunway);
            Assert.True(read[2].OneAircraftPerRunway);
            Assert.Equal(starts, read[0].HasTakeoffs);
            Assert.False(read[1].HasTakeoffs);
            Assert.Equal(starts ? 1 : 0, read[2].Takeoffs.Count);
            Assert.Equal(1, read[0].Roads.Count);
        }

        /// <summary>A map whose fields all share their runways as the game does is the very file it was
        /// before the flag, byte for byte, with or without take-off starts.</summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void AFileWithEveryFlagOffIsTheFileItWasBefore(bool starts)
        {
            var fields = new[] { Field("LSZH", starts), Field("AB02") };
            Assert.Equal(Released141Bytes(fields), Bytes(fields));
            Assert.All(TaxiData.Read(Released141Bytes(fields)), f => Assert.False(f.OneAircraftPerRunway));
        }

        /// <summary>
        /// The plugin as released in 1.4.1 meets a map with runway flags, after take-off starts or straight
        /// after the airbases: it reads every field's network and take-off starts as the new reader does and
        /// leaves the flags alone. So does 1.4.0, which stops after the airbases.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void ThePluginsAsReleasedReadAFileWithRunwayFlagsWhole(bool starts)
        {
            var fields = new[] { Field("LSZH", starts, locked: true), Field("AB02"), Field("LSGS", starts, locked: true) };
            byte[] bytes = Bytes(fields);
            Assert.Equal(Released141Bytes(fields).Length + 8 + fields.Length, bytes.Length);

            List<TaxiNetworkData> now = TaxiData.Read(bytes);
            List<Released141.TaxiNetworkData> old = Released141.TaxiData.Read(bytes);
            List<Released140.TaxiNetworkData> older = Released140.TaxiData.Read(bytes);
            Assert.Equal(now.Count, old.Count);
            Assert.Equal(now.Count, older.Count);

            for (int i = 0; i < now.Count; i++)
            {
                Assert.Equal(now[i].Airbase, old[i].Airbase);
                Assert.Equal(now[i].Roads, old[i].Roads);
                Assert.Equal(now[i].Exits.Count, old[i].Exits.Count);
                Assert.Equal(now[i].ServicePoint, old[i].ServicePoint);
                Assert.Equal(now[i].LandingPad, old[i].LandingPad);
                Assert.Equal(now[i].Takeoffs.Count, old[i].Takeoffs.Count);
                for (int t = 0; t < now[i].Takeoffs.Count; t++)
                {
                    Assert.Equal(now[i].Takeoffs[t].X, old[i].Takeoffs[t].X);
                    Assert.Equal(now[i].Takeoffs[t].Direction, old[i].Takeoffs[t].Direction);
                }

                Assert.Equal(now[i].Airbase, older[i].Airbase);
                Assert.Equal(now[i].Roads, older[i].Roads);
            }
        }

        /// <summary>Bits of the flags byte this build does not know are later additions, left alone; a
        /// section that ends before its last airbase is refused, as a truncated take-off section is.</summary>
        [Fact]
        public void UnknownFlagBitsAreLeftAloneAndATruncatedSectionIsRefused()
        {
            byte[] bytes = Bytes(new[] { Field(locked: true) });
            bytes[bytes.Length - 1] = 0xFE;
            Assert.False(Assert.Single(TaxiData.Read(bytes)).OneAircraftPerRunway);

            bytes[bytes.Length - 1] = 0xFF;
            Assert.True(Assert.Single(TaxiData.Read(bytes)).OneAircraftPerRunway);

            byte[] two = Bytes(new[] { Field(locked: true), Field("AB02", locked: true) });
            System.Array.Resize(ref two, two.Length - 1);
            Assert.Throws<InvalidDataException>(() => TaxiData.Read(two));
        }

        /// <summary>A section after the runway flags, or one this build does not know in their place, is a
        /// later addition, left alone.</summary>
        [Fact]
        public void ASectionAfterTheFlagsIsLeftAlone()
        {
            byte[] plain = Bytes(new[] { Field(starts: true, locked: true) });
            byte[] extra = System.Text.Encoding.ASCII.GetBytes("NOLATER1xxxxxxxx");
            var bytes = new byte[plain.Length + extra.Length];
            plain.CopyTo(bytes, 0);
            extra.CopyTo(bytes, plain.Length);

            TaxiNetworkData read = Assert.Single(TaxiData.Read(bytes));
            Assert.True(read.OneAircraftPerRunway);
            Assert.True(read.HasTakeoffs);
        }
    }
}
