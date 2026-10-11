using System;
using System.Linq;
using System.Numerics;
using Xunit;
using Xunit.Abstractions;

namespace CustomMaps.Tests
{
    /// <summary>
    /// How far out the HUD's landing glideslope starts at a custom map's airbases (<see cref="ApproachLine"/>),
    /// and that the game installed here has the methods its patch was checked against.
    /// </summary>
    public class ApproachLineTests
    {
        const float Nm = 1852f;

        readonly ITestOutputHelper _output;

        public ApproachLineTests(ITestOutputHelper output) => _output = output;

        // AirbaseOverlay.DrawGlideslope: the line runs from the touchdown point to num * 0.9 up the slope,
        // num being the aircraft's distance to the touchdown point.
        static float Line(float distanceToTouchdown) => ApproachLine.AimFraction * distanceToTouchdown;

        // Airbase.Runway.RunwayUsage.GetTouchdownPoint: past the threshold by Lerp(20, 300, (L - 500) * 0.0005)
        // for a runway over 500 m, along the runway.
        static float TouchdownOffset(float length)
            => length > 500f ? 20f + 280f * Math.Clamp((length - 500f) * 0.0005f, 0f, 1f) : 0f;

        [Fact]
        public void TheLineIsTenMilesLong()
        {
            Assert.Equal(18520f, ApproachLine.LineLength);
            Assert.Equal(10f, ApproachLine.LineLength / Nm, 3);
        }

        // An airbase whose capture range already reaches past the distance keeps the game's 5,000 m: the
        // patch never brings the runway in closer than the game does.
        [Theory]
        [InlineData(30000f, 1500f)]
        [InlineData(19000f, 1500f)]
        [InlineData(float.MaxValue, 0f)]
        public void NeverLessThanTheGames(float captureRange, float farthestEnd)
        {
            Assert.Equal(ApproachLine.GameExtra, ApproachLine.Extra(captureRange, farthestEnd));
        }

        [Theory]
        [InlineData(float.NaN, 1500f)]
        [InlineData(1500f, float.NaN)]
        public void AnUnreadableInputLeavesTheGames(float captureRange, float farthestEnd)
        {
            Assert.Equal(ApproachLine.GameExtra, ApproachLine.Extra(captureRange, farthestEnd));
        }

        // The distance the aircraft crosses is the capture range plus the extra: 10 nm / 0.9 from the
        // farthest runway end, and the room for a 2 s update. The capture range itself drops out.
        [Theory]
        [InlineData(1693f, 1950f)]
        [InlineData(1000f, 0f)]
        [InlineData(2600f, 3200f)]
        public void TheDistanceIsTheLineOverTheShareFromTheFarthestEnd(float captureRange, float farthestEnd)
        {
            float gate = captureRange + ApproachLine.Extra(captureRange, farthestEnd);
            Assert.Equal(farthestEnd + 18520f / 0.9f + ApproachLine.UpdateMargin, gate, 0.5f);
        }

        // Against the game's own: an airbase like Geneva, its flag at the middle of a 3,900 m runway and a
        // capture range of 1,693 m (airbases.bin), took its runway 6.7 km from the flag, so the first line an
        // aircraft on the 0.06 slope saw was 2.6 nm long. Now, wherever it comes from and however high, the
        // first line it is shown, up to 2 s after it crosses, is at least 10 nm.
        [Fact]
        public void EveryApproachToEveryEndFirstShowsAtLeastTenMiles()
        {
            var random = new Random(1);
            for (int airbase = 0; airbase < 200; airbase++)
            {
                float length = 800f + (float)random.NextDouble() * 3500f;
                float heading = (float)(random.NextDouble() * 2 * Math.PI);
                var middle = new Vector3(0f, (float)random.NextDouble() * 1500f, 0f);
                var along = new Vector3(MathF.Sin(heading), 0f, MathF.Cos(heading));
                Vector3 start = middle - along * length / 2f, end = middle + along * length / 2f;

                // The flag anywhere up to 3 km off the runway, as a moved one may be.
                var flag = middle + new Vector3((float)random.NextDouble() * 6000f - 3000f, 0f,
                                                (float)random.NextDouble() * 6000f - 3000f);
                float captureRange = 500f + (float)random.NextDouble() * 4000f;
                float farthest = Math.Max(Vector3.Distance(flag, start), Vector3.Distance(flag, end));
                float gate = captureRange + ApproachLine.Extra(captureRange, farthest);

                foreach ((Vector3 threshold, Vector3 inbound) in new[] { (start, along), (end, -along) })
                {
                    Vector3 touchdown = threshold + inbound * TouchdownOffset(length);

                    // From every bearing and several heights, the aircraft 2 s inside the distance at 250 m/s.
                    for (int bearing = 0; bearing < 36; bearing++)
                    foreach (float height in new[] { 0f, 1200f, 6000f })
                    {
                        float b = bearing * MathF.PI / 18f;
                        var dir = Vector3.Normalize(new Vector3(MathF.Sin(b), height / gate, MathF.Cos(b)));
                        Vector3 aircraft = flag + dir * (gate - ApproachLine.UpdateMargin);

                        float line = Line(Vector3.Distance(aircraft, touchdown));
                        Assert.True(line >= ApproachLine.LineLength - 1f,
                            $"a {length:F0} m runway, flag {Vector3.Distance(flag, middle):F0} m off, capture range " +
                            $"{captureRange:F0} m: a line of {line / Nm:F2} nm");
                    }
                }
            }
        }

        // What the game gave, against what the patch gives, at an airbase with its flag mid-runway, for an
        // aircraft on the extended centreline on the 0.06 slope as it crosses the distance.
        [Theory]
        [InlineData(3900f, 1693f)]   // Geneva in airbases.bin
        [InlineData(1500f, 1000f)]
        public void StraightInTheLineWasAboutThreeMilesAndIsNowTen(float length, float captureRange)
        {
            float halfLength = length / 2f;
            float centreToTouchdown = halfLength - TouchdownOffset(length);

            float Crossing(float gate)
            {
                // The slant distance x from touchdown with |touchdown + x * u - flag| = gate, u along the
                // centreline away from the runway and up the slope, the flag behind touchdown on it.
                float k = MathF.Sqrt(1f + 0.06f * 0.06f);
                float along = 1f / k;
                // |(c + x * along, x * 0.06 / k)| = gate, c the flag-to-touchdown distance along the runway
                float bq = 2f * centreToTouchdown * along;
                float cq = centreToTouchdown * centreToTouchdown - gate * gate;
                return (-bq + MathF.Sqrt(bq * bq - 4f * cq)) / 2f;
            }

            float before = Line(Crossing(captureRange + ApproachLine.GameExtra)) / Nm;
            float after = Line(Crossing(captureRange + ApproachLine.Extra(captureRange, halfLength))) / Nm;
            _output.WriteLine($"{length:F0} m runway, capture range {captureRange:F0} m: {before:F2} nm before, {after:F2} nm now");

            Assert.InRange(before, 2.3f, 3.2f);
            Assert.InRange(after, 10f, 10.5f);
        }

        /// <summary>
        /// The game's <c>AirbaseOverlay.UpdateNearestAirbase</c> and <c>DrawGlideslope</c>, read from its
        /// DLL, are the ones the patch was checked against (<see cref="ApproachLine.VerifiedGameMethods"/>):
        /// the one <c>5000f</c> the transpiler replaces, after <c>GetRadius</c>, and the <c>0.9f</c> that
        /// <see cref="ApproachLine.AimFraction"/> copies. Reads the Assembly-CSharp.dll the plugin builds
        /// against; passes without checking anything where there is none.
        ///
        /// After a game update that changes either method this fails, here and in CI, until someone has
        /// decompiled them again (NOMapForge/tools/GameProbe), checked the range test and the line, and set
        /// the new value. Until then the plugin leaves the game's line as it is.
        /// </summary>
        [Fact]
        public void TheInstalledGameHasTheOverlayThisWasCheckedAgainst()
        {
            string dll = IlFingerprintTests.GameAssembly();
            if (dll == null)
            {
                _output.WriteLine("Assembly-CSharp.dll not found; nothing checked");
                return;
            }

            ulong print = IlFingerprint.OfAll(ApproachLine.GameMethods.Select(m =>
                IlFingerprintTests.GameMethodPrint(dll, "", "AirbaseOverlay", m)));
            _output.WriteLine($"{dll}: AirbaseOverlay.UpdateNearestAirbase with DrawGlideslope {print:X16}");

            Assert.True(print == ApproachLine.VerifiedGameMethods,
                $"The game's AirbaseOverlay.UpdateNearestAirbase or DrawGlideslope has changed (fingerprint 0x{print:X16}UL). " +
                "Decompile them (NOMapForge/tools/GameProbe), check the range test still adds 5000f to GetRadius() and " +
                "the line still ends at num * 0.9f, then set ApproachLine.VerifiedGameMethods to that value.");
        }
    }
}
