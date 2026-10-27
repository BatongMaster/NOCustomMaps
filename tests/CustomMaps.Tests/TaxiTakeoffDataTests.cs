using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using Released = CustomMaps.Tests.Released140;

namespace CustomMaps.Tests
{
    /// <summary>
    /// The take-off starts in <c>taxiways.bin</c>: read back as written, nothing added to a file without
    /// them, and a file with them still read whole by the plugin as released in 1.4.0
    /// (<see cref="Released.TaxiData"/>), which knows nothing of them. The plugin's copy of these tests
    /// is the same file.
    /// </summary>
    public class TaxiTakeoffDataTests
    {
        static TaxiNetworkData Field(string name = "LSZH")
        {
            var field = new TaxiNetworkData { Airbase = name, ServicePoint = (1234.5f, -987.25f), LandingPad = (1300f, -950f) };
            field.Roads.Add(new[] { (0f, 0f), (0f, 30f), (12.5f, 55f) });
            field.Roads.Add(new[] { (12.5f, 55f), (200f, 55f) });
            field.Exits.Add(new TaxiExit { Runway = 0, X = 0f, Z = 0f, Directions = TaxiExit.Forward | TaxiExit.Reverse });
            field.Exits.Add(new TaxiExit { Runway = 2, X = -1500f, Z = 7.25f, Directions = TaxiExit.Reverse });
            return field;
        }

        static TaxiNetworkData WithStarts(string name = "LSZH")
        {
            TaxiNetworkData field = Field(name);
            field.Takeoffs.Add(new TaxiTakeoff { Runway = 0, X = 78420.5f, Z = 77534.25f, Direction = TaxiExit.Forward });
            field.Takeoffs.Add(new TaxiTakeoff { Runway = 2, X = -1400f, Z = 7.25f, Direction = TaxiExit.Reverse });
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

        /// <summary>The same fields through the 1.4.0 writer.</summary>
        static byte[] Released140Bytes(IReadOnlyList<TaxiNetworkData> fields)
        {
            var old = new List<Released.TaxiNetworkData>();
            foreach (TaxiNetworkData field in fields)
            {
                var copy = new Released.TaxiNetworkData
                {
                    Airbase = field.Airbase, Source = (Released.TaxiSource)(byte)field.Source,
                    ServicePoint = field.ServicePoint, LandingPad = field.LandingPad,
                };
                copy.Roads.AddRange(field.Roads);
                foreach (TaxiExit exit in field.Exits)
                    copy.Exits.Add(new Released.TaxiExit { Runway = exit.Runway, X = exit.X, Z = exit.Z, Directions = exit.Directions });
                old.Add(copy);
            }

            using (var stream = new MemoryStream())
            {
                Released.TaxiData.Write(stream, old);
                return stream.ToArray();
            }
        }

        [Fact]
        public void TakeoffStartsAreReadBackAsWritten()
        {
            var none = new TaxiNetworkData { Airbase = "AB02", Source = TaxiSource.Default };
            List<TaxiNetworkData> read = TaxiData.Read(Bytes(new[] { WithStarts(), none, WithStarts("LSGS") }));

            Assert.Equal(3, read.Count);
            foreach (int i in new[] { 0, 2 })
            {
                Assert.True(read[i].HasTakeoffs);
                Assert.Equal(2, read[i].Takeoffs.Count);
                Assert.Equal(0, read[i].Takeoffs[0].Runway);
                Assert.Equal(78420.5f, read[i].Takeoffs[0].X);
                Assert.Equal(77534.25f, read[i].Takeoffs[0].Z);
                Assert.True(read[i].Takeoffs[0].TowardsEnd);
                Assert.Equal(2, read[i].Takeoffs[1].Runway);
                Assert.Equal(TaxiExit.Reverse, read[i].Takeoffs[1].Direction);
                Assert.False(read[i].Takeoffs[1].TowardsEnd);
            }

            Assert.False(read[1].HasTakeoffs);
            Assert.Equal("LSGS", read[2].Airbase);

            // Everything else as it was.
            Assert.Equal(2, read[0].Roads.Count);
            Assert.Equal(2, read[0].Exits.Count);
            Assert.Equal((1234.5f, -987.25f), read[0].ServicePoint);
        }

        /// <summary>A map without take-off starts is the very file it was before them, byte for byte.</summary>
        [Fact]
        public void AFileWithoutTakeoffStartsIsTheFileItWasBefore()
        {
            var fields = new[] { Field(), new TaxiNetworkData { Airbase = "AB02", LandingPad = (5f, 6f), Source = TaxiSource.Default } };
            Assert.Equal(Released140Bytes(fields), Bytes(fields));
            Assert.Equal(Released140Bytes(new TaxiNetworkData[0]), Bytes(new TaxiNetworkData[0]));

            // And the file 1.4.0 wrote reads with no take-off starts.
            Assert.All(TaxiData.Read(Released140Bytes(fields)), f => Assert.False(f.HasTakeoffs));
        }

        /// <summary>
        /// The plugin as released (1.4.0) meets a map with take-off starts: it reads every field's network,
        /// exits, service point and landing pad exactly as the new reader does, and stops before the starts,
        /// so the map's airbases and their roads are built as before, only without them.
        /// </summary>
        [Fact]
        public void ThePluginAsReleasedReadsAFileWithTakeoffStartsWhole()
        {
            var fields = new[] { WithStarts(), new TaxiNetworkData { Airbase = "AB02", Source = TaxiSource.Default }, WithStarts("LSGS") };
            byte[] bytes = Bytes(fields);
            Assert.True(bytes.Length > Released140Bytes(fields).Length);

            List<Released.TaxiNetworkData> old = Released.TaxiData.Read(bytes);
            List<TaxiNetworkData> now = TaxiData.Read(bytes);
            Assert.Equal(now.Count, old.Count);
            for (int i = 0; i < now.Count; i++)
            {
                Assert.Equal(now[i].Airbase, old[i].Airbase);
                Assert.Equal((byte)now[i].Source, (byte)old[i].Source);
                Assert.Equal(now[i].Roads, old[i].Roads);
                Assert.Equal(now[i].Exits.Count, old[i].Exits.Count);
                for (int e = 0; e < now[i].Exits.Count; e++)
                {
                    Assert.Equal(now[i].Exits[e].Runway, old[i].Exits[e].Runway);
                    Assert.Equal(now[i].Exits[e].Directions, old[i].Exits[e].Directions);
                    Assert.Equal(now[i].Exits[e].X, old[i].Exits[e].X);
                    Assert.Equal(now[i].Exits[e].Z, old[i].Exits[e].Z);
                }

                Assert.Equal(now[i].ServicePoint, old[i].ServicePoint);
                Assert.Equal(now[i].LandingPad, old[i].LandingPad);
            }
        }

        /// <summary>Something after the airbases that is not the take-off starts' tag is a later addition,
        /// left alone: the networks are still read.</summary>
        [Fact]
        public void AnUnknownSectionAfterTheAirbasesIsLeftAlone()
        {
            byte[] plain = Bytes(new[] { Field() });

            foreach (string tag in new[] { "NOTKOF02", "NOSOMETHINGELSE", "N", "" })
            {
                byte[] extra = System.Text.Encoding.ASCII.GetBytes(tag);
                var bytes = new byte[plain.Length + extra.Length + 8];
                plain.CopyTo(bytes, 0);
                extra.CopyTo(bytes, plain.Length);

                TaxiNetworkData read = Assert.Single(TaxiData.Read(bytes));
                Assert.Equal(2, read.Roads.Count);
                Assert.False(read.HasTakeoffs);
            }
        }

        [Fact]
        public void TheWriterRefusesATakeoffStartTheReaderWould()
        {
            void Refused(TaxiTakeoff start)
            {
                TaxiNetworkData field = Field();
                field.Takeoffs.Add(start);
                Assert.Throws<ArgumentException>(() => Bytes(new[] { field }));
            }

            Refused(new TaxiTakeoff { Runway = 0, Direction = 0 });
            Refused(new TaxiTakeoff { Runway = 0, Direction = TaxiExit.Forward | TaxiExit.Reverse });
            Refused(new TaxiTakeoff { Runway = 0, Direction = 4 });
            Refused(new TaxiTakeoff { Runway = -1, Direction = TaxiExit.Forward });
            Refused(new TaxiTakeoff { Runway = AirbaseData.MaxExtraRunways + 1, Direction = TaxiExit.Forward });
            Refused(new TaxiTakeoff { Runway = 0, Direction = TaxiExit.Forward, X = float.NaN });
            Refused(new TaxiTakeoff { Runway = 0, Direction = TaxiExit.Reverse, Z = float.PositiveInfinity });

            TaxiNetworkData many = Field();
            for (int i = 0; i < TaxiData.MaxTakeoffs + 1; i++) many.Takeoffs.Add(new TaxiTakeoff { Direction = TaxiExit.Forward });
            Assert.Throws<ArgumentException>(() => Bytes(new[] { many }));
        }

        [Fact]
        public void TheReaderRefusesACorruptTakeoffSection()
        {
            byte[] plain = Bytes(new[] { Field() });
            byte[] good = Bytes(new[] { WithStarts() });
            int section = plain.Length;
            Assert.Equal(TaxiData.TakeoffMagic, good[section..(section + 8)]);

            byte[] bad = (byte[])good.Clone();
            BitConverter.GetBytes(-1).CopyTo(bad, section + 8);
            Assert.Throws<InvalidDataException>(() => TaxiData.Read(bad));

            bad = (byte[])good.Clone();
            BitConverter.GetBytes(TaxiData.MaxTakeoffs + 1).CopyTo(bad, section + 8);
            Assert.Throws<InvalidDataException>(() => TaxiData.Read(bad));

            // The first start's direction, after its runway.
            bad = (byte[])good.Clone();
            bad[section + 8 + 4 + 1] = 3;
            Assert.Throws<InvalidDataException>(() => TaxiData.Read(bad));

            // Cut short inside the section, it does not read.
            for (int length = section + 9; length < good.Length; length += 3)
            {
                byte[] cut = new byte[length];
                Array.Copy(good, cut, length);
                Assert.ThrowsAny<Exception>(() => TaxiData.Read(cut));
            }
        }
    }
}
