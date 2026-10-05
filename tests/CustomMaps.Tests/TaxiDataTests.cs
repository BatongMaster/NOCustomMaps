using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace CustomMaps.Tests
{
    /// <summary>
    /// <c>taxiways.bin</c> both ways: everything written is read back as it was, and anything the
    /// plugin could not use is refused by the writer and the reader alike. The plugin's copy of these
    /// tests is the same file.
    /// </summary>
    public class TaxiDataTests
    {
        static TaxiNetworkData Field()
        {
            var field = new TaxiNetworkData
            {
                Airbase = "LSZH",
                ServicePoint = (1234.5f, -987.25f),
                LandingPad = (1300f, -950f),
            };
            field.Roads.Add(new[] { (0f, 0f), (0f, 30f), (12.5f, 55f) });
            field.Roads.Add(new[] { (12.5f, 55f), (200f, 55f) });
            field.Exits.Add(new TaxiExit { Runway = 0, X = 0f, Z = 0f, Directions = TaxiExit.Forward | TaxiExit.Reverse });
            field.Exits.Add(new TaxiExit { Runway = 2, X = -1500f, Z = 7.25f, Directions = TaxiExit.Reverse });
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

        [Fact]
        public void EverythingWrittenIsReadBack()
        {
            var bare = new TaxiNetworkData { Airbase = "AB02", LandingPad = (5f, 6f), Source = TaxiSource.Default };
            List<TaxiNetworkData> read = TaxiData.Read(Bytes(new[] { Field(), bare }));

            Assert.Equal(2, read.Count);
            TaxiNetworkData field = read[0];
            Assert.Equal("LSZH", field.Airbase);
            Assert.Equal(TaxiSource.Lanes, field.Source);
            Assert.True(field.HasNetwork);
            Assert.Equal(2, field.Roads.Count);
            Assert.Equal(new[] { (0f, 0f), (0f, 30f), (12.5f, 55f) }, field.Roads[0]);
            Assert.Equal(new[] { (12.5f, 55f), (200f, 55f) }, field.Roads[1]);

            Assert.Equal(2, field.Exits.Count);
            Assert.Equal(0, field.Exits[0].Runway);
            Assert.Equal(TaxiExit.Forward | TaxiExit.Reverse, field.Exits[0].Directions);
            Assert.Equal(2, field.Exits[1].Runway);
            Assert.Equal(-1500f, field.Exits[1].X);
            Assert.Equal(7.25f, field.Exits[1].Z);
            Assert.Equal(TaxiExit.Reverse, field.Exits[1].Directions);

            Assert.Equal((1234.5f, -987.25f), field.ServicePoint);
            Assert.Equal((1300f, -950f), field.LandingPad);

            Assert.Equal("AB02", read[1].Airbase);
            Assert.Equal(TaxiSource.Default, read[1].Source);
            Assert.False(read[1].HasNetwork);
            Assert.Empty(read[1].Exits);
            Assert.Null(read[1].ServicePoint);
            Assert.Equal((5f, 6f), read[1].LandingPad);
        }

        [Fact]
        public void EverySourceIsReadBackAndNoOther()
        {
            foreach (TaxiSource source in new[] { TaxiSource.Lanes, TaxiSource.Taxiways, TaxiSource.Default })
            {
                TaxiNetworkData field = Field();
                field.Source = source;
                Assert.Equal(source, TaxiData.Read(Bytes(new[] { field }))[0].Source);
            }

            // The source byte follows the magic, the count and the name.
            byte[] bytes = Bytes(new[] { Field() });
            int at = 8 + 4 + 1 + "LSZH".Length;
            Assert.Equal((byte)TaxiSource.Lanes, bytes[at]);
            bytes[at] = 3;
            Assert.Throws<InvalidDataException>(() => TaxiData.Read(bytes));

            TaxiNetworkData unknown = Field();
            unknown.Source = (TaxiSource)7;
            Assert.Throws<ArgumentException>(() => Bytes(new[] { unknown }));
        }

        [Fact]
        public void AnEmptySetIsTheMagicAndACount()
        {
            byte[] bytes = Bytes(new List<TaxiNetworkData>());
            Assert.Equal(12, bytes.Length);
            Assert.Empty(TaxiData.Read(bytes));
        }

        [Fact]
        public void ANewerVersionIsRefusedByName()
        {
            byte[] bytes = Bytes(new[] { Field() });
            bytes[7] = (byte)'2';
            var newer = Assert.Throws<InvalidDataException>(() => TaxiData.Read(bytes));
            Assert.Contains("newer version", newer.Message);

            bytes[0] = (byte)'X';
            var wrong = Assert.Throws<InvalidDataException>(() => TaxiData.Read(bytes));
            Assert.Contains("wrong magic", wrong.Message);

            Assert.Throws<InvalidDataException>(() => TaxiData.Read(new byte[] { (byte)'N', (byte)'O' }));
        }

        [Fact]
        public void TheWriterRefusesWhatTheReaderWould()
        {
            void Refused(Action<TaxiNetworkData> spoil)
            {
                TaxiNetworkData field = Field();
                spoil(field);
                Assert.Throws<ArgumentException>(() => Bytes(new[] { field }));
            }

            Refused(f => f.Roads.Add(new[] { (1f, 1f) }));
            Refused(f => f.Roads.Add(new[] { (1f, 1f), (float.NaN, 2f) }));
            Refused(f => f.Exits.Add(new TaxiExit { Runway = 0, Directions = 0 }));
            Refused(f => f.Exits.Add(new TaxiExit { Runway = 0, Directions = 4 }));
            Refused(f => f.Exits.Add(new TaxiExit { Runway = AirbaseData.MaxExtraRunways + 1, Directions = 1 }));
            Refused(f => f.Exits.Add(new TaxiExit { Runway = -1, Directions = 1 }));
            Refused(f => f.Exits.Add(new TaxiExit { Runway = 0, Directions = 1, X = float.PositiveInfinity }));
            Refused(f => f.ServicePoint = (float.NaN, 0f));
            Refused(f => f.LandingPad = (0f, float.NegativeInfinity));
            Refused(f =>
            {
                for (int i = 0; i < TaxiData.MaxExits + 1; i++) f.Exits.Add(new TaxiExit { Directions = 1 });
            });

            Assert.Throws<ArgumentNullException>(() => Bytes(null));
        }

        [Fact]
        public void TheReaderRefusesACorruptFile()
        {
            byte[] good = Bytes(new[] { Field() });

            // The first road's point count, after the magic, the count, the name and the source.
            int roadCount = 8 + 4 + 1 + "LSZH".Length + 1;
            int firstRoad = roadCount + 4;

            byte[] bad = (byte[])good.Clone();
            BitConverter.GetBytes(1).CopyTo(bad, firstRoad);
            Assert.Throws<InvalidDataException>(() => TaxiData.Read(bad));

            bad = (byte[])good.Clone();
            BitConverter.GetBytes(-1).CopyTo(bad, roadCount);
            Assert.Throws<InvalidDataException>(() => TaxiData.Read(bad));

            bad = (byte[])good.Clone();
            BitConverter.GetBytes(TaxiData.MaxAirbases + 1).CopyTo(bad, 8);
            Assert.Throws<InvalidDataException>(() => TaxiData.Read(bad));

            // Cut short anywhere, it does not read.
            for (int length = 9; length < good.Length; length += 7)
            {
                byte[] cut = new byte[length];
                Array.Copy(good, cut, length);
                Assert.ThrowsAny<Exception>(() => TaxiData.Read(cut));
            }
        }

        [Fact]
        public void AFileIsWrittenOnlyWhenTheSetIsGood()
        {
            string path = Path.Combine(Path.GetTempPath(), "taxidata-" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                TaxiNetworkData bad = Field();
                bad.Roads.Add(new[] { (0f, 0f) });
                Assert.Throws<ArgumentException>(() => TaxiData.Write(path, new[] { bad }));
                Assert.False(File.Exists(path));

                TaxiData.Write(path, new[] { Field() });
                Assert.Single(TaxiData.Read(File.ReadAllBytes(path)));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
