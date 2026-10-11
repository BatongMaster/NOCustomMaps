using System;
using System.Collections.Generic;
using Xunit;

namespace CustomMaps.Tests
{
    public class LakeSurfacesTests
    {
        // Swiss Alps' sheet grid: 4,096 heightfield samples spanning 199,680 m, so 4,095 gaps between
        // them (HeightField.Spacing), every second one, so a cell is 97.52 m and vertex positions are
        // measured back from half the map's width, in floats, as NOMapForge's LakeSheet places them.
        // The odd spacing matters: a vertex then rounds a few millimetres either side of where a
        // lake's buckets start, and a triangle can straddle two of them.
        const float Extent = 199680f;
        const float Half = Extent * 0.5f;
        const float Spacing = Extent / (4096 - 1) * 2f;

        static float X(int column) => -Half + column * Spacing;
        static float Z(int row) => -Half + row * Spacing;

        /// <summary>A sheet as NOMapForge builds it: a grid of vertices over a window, and two triangles
        /// (a, b, d) and (d, b, c) for every cell of the lake.</summary>
        static LakeSurfaces.Source Sheet(string name, float surfaceY, int minColumn, int minRow, bool[,] cells)
        {
            int w = cells.GetLength(0) + 1, h = cells.GetLength(1) + 1;
            var xs = new float[w * h];
            var zs = new float[w * h];
            for (int row = 0; row < h; row++)
                for (int column = 0; column < w; column++)
                {
                    xs[row * w + column] = X(minColumn + column);
                    zs[row * w + column] = Z(minRow + row);
                }

            var triangles = new List<int>();
            for (int row = 0; row < h - 1; row++)
                for (int column = 0; column < w - 1; column++)
                {
                    if (!cells[column, row]) continue;
                    int a = row * w + column, b = (row + 1) * w + column, c = (row + 1) * w + column + 1, d = row * w + column + 1;
                    triangles.AddRange(new[] { a, b, d, d, b, c });
                }

            return new LakeSurfaces.Source { Name = name, SurfaceY = surfaceY, X = xs, Z = zs, Triangles = triangles.ToArray() };
        }

        /// <summary>An L-shaped lake with a dry island cell in it, 6 by 5 cells, south-west corner at
        /// column 1000, row 1300 (-2,316 m, 26,941 m).</summary>
        static bool[,] LShape()
        {
            var cells = new bool[6, 5];
            for (int c = 0; c < 6; c++)
                for (int r = 0; r < 5; r++)
                    cells[c, r] = r < 2 || c < 2;
            cells[0, 0] = false;   // a dry corner
            cells[3, 1] = false;   // an island, water all round
            return cells;
        }

        [Fact]
        public void TheFootprintIsExactlyTheSheetsCells()
        {
            bool[,] cells = LShape();
            LakeSurfaces lakes = LakeSurfaces.Build(new[] { Sheet("L", 60.5f, 1000, 1300, cells) });
            Assert.Equal(1, lakes.Count);

            // Every cell's middle and a point a centimetre inside each of its corners, and a ring of
            // cells round the window, which are dry.
            for (int c = -1; c <= 6; c++)
                for (int r = -1; r <= 5; r++)
                {
                    bool water = c >= 0 && c < 6 && r >= 0 && r < 5 && cells[c, r];
                    float x0 = X(1000 + c), z0 = Z(1300 + r), x1 = X(1001 + c), z1 = Z(1301 + r);

                    var points = new[]
                    {
                        ((x0 + x1) * 0.5f, (z0 + z1) * 0.5f),
                        (x0 + 0.01f, z0 + 0.01f), (x1 - 0.01f, z0 + 0.01f), (x0 + 0.01f, z1 - 0.01f), (x1 - 0.01f, z1 - 0.01f),
                    };

                    foreach ((float x, float z) in points)
                        Assert.True(water == (lakes.LakeAt(x, z) == 0), $"cell ({c}, {r}) at ({x}, {z}) should be {(water ? "water" : "dry")}");
                }
        }

        [Fact]
        public void TheDiagonalBetweenACellsTwoTrianglesIsWater()
        {
            var cells = new bool[1, 1];
            cells[0, 0] = true;
            LakeSurfaces lakes = LakeSurfaces.Build(new[] { Sheet("one", 100f, 0, 0, cells) });

            // The cell's two triangles meet along the diagonal from its north-west to its south-east
            // corner; points on it and either side of it are in.
            for (int k = 1; k < 20; k++)
            {
                float t = k / 20f;
                float x = X(0) + Spacing * t, z = Z(1) - Spacing * t;
                Assert.Equal(0, lakes.LakeAt(x, z));
            }
        }

        [Fact]
        public void AnEdgeIsExactToTheCentimetreAtTheMapsFarSide()
        {
            // Neuchatel's western cells, 52.8 km west of the centre.
            var cells = new bool[3, 3];
            cells[1, 1] = true;
            int column = (int)Math.Round((-52833.52f + Half) / Spacing);
            LakeSurfaces lakes = LakeSurfaces.Build(new[] { Sheet("west", 60.5f, column - 1, 1500, cells) });

            float west = X(column), middleZ = (Z(1501) + Z(1502)) * 0.5f;
            Assert.Equal(0, lakes.LakeAt(west + 0.01f, middleZ));
            Assert.Equal(-1, lakes.LakeAt(west - 0.01f, middleZ));
            Assert.Equal(0, lakes.LakeAt(west, middleZ));
        }

        [Fact]
        public void EachLakeHasItsOwnSurfaceAndTheSeaIsEverywhereElse()
        {
            var a = new bool[2, 2];
            var b = new bool[3, 1];
            a[0, 0] = a[1, 0] = a[0, 1] = a[1, 1] = true;
            b[0, 0] = b[1, 0] = b[2, 0] = true;

            LakeSurfaces lakes = LakeSurfaces.Build(new[]
            {
                Sheet("Neuchatel", 60.5f, 200, 200, a),
                Sheet("Thun", 189.5f, 900, 400, b),
            });

            float inA = (X(200) + X(202)) * 0.5f, zA = (Z(200) + Z(202)) * 0.5f;
            float inB = (X(901) + X(902)) * 0.5f, zB = (Z(400) + Z(401)) * 0.5f;

            Assert.Equal("Neuchatel", lakes.Name(lakes.LakeAt(inA, zA)));
            Assert.Equal("Thun", lakes.Name(lakes.LakeAt(inB, zB)));
            Assert.Equal(60.5f, lakes.WaterLevel(inA, zA));
            Assert.Equal(189.5f, lakes.WaterLevel(inB, zB));
            Assert.Equal(0f, lakes.WaterLevel(0f, 0f));
            Assert.Equal(-1, lakes.LakeAt(0f, 0f));
            Assert.Equal(189.5f, lakes.HighestSurface);
        }

        [Fact]
        public void LakesSharingALevelStayApart()
        {
            // Neuchatel, Biel and Murten all stand at 60.5 m; a lane leaving one by its index has left
            // the water even where the next one has the same height.
            var cells = new bool[2, 2];
            cells[0, 0] = cells[1, 0] = cells[0, 1] = cells[1, 1] = true;
            LakeSurfaces lakes = LakeSurfaces.Build(new[]
            {
                Sheet("Neuchatel", 60.5f, 100, 100, cells),
                Sheet("Biel", 60.5f, 103, 100, cells),
            });

            float z = (Z(100) + Z(102)) * 0.5f;
            Assert.Equal("Neuchatel", lakes.Name(lakes.LakeAt((X(100) + X(102)) * 0.5f, z)));
            Assert.Equal("Biel", lakes.Name(lakes.LakeAt((X(103) + X(105)) * 0.5f, z)));
            Assert.Equal(-1, lakes.LakeAt((X(102) + X(103)) * 0.5f, z));
        }

        [Fact]
        public void ALakeNotAboveTheSeaIsLeftToTheSea()
        {
            var cells = new bool[1, 1];
            cells[0, 0] = true;
            LakeSurfaces lakes = LakeSurfaces.Build(new[]
            {
                Sheet("Geneva", 0f, 10, 10, cells),
                Sheet("below", -3f, 20, 20, cells),
            });

            Assert.Equal(0, lakes.Count);
            Assert.Equal(-1, lakes.LakeAt((X(10) + X(11)) * 0.5f, (Z(10) + Z(11)) * 0.5f));
            Assert.Equal(0f, lakes.WaterLevel((X(10) + X(11)) * 0.5f, (Z(10) + Z(11)) * 0.5f));
            Assert.Equal(float.NegativeInfinity, lakes.HighestSurface);
            Assert.Equal(float.NegativeInfinity, new LakeFrame(lakes, 0f, 30f, 0f).HighestSurfaceY);
        }

        [Fact]
        public void WhereFootprintsOverlapTheHigherWaterWins()
        {
            var cells = new bool[1, 1];
            cells[0, 0] = true;
            LakeSurfaces lakes = LakeSurfaces.Build(new[]
            {
                Sheet("low", 50f, 30, 30, cells),
                Sheet("high", 80f, 30, 30, cells),
            });

            float x = (X(30) + X(31)) * 0.5f, z = (Z(30) + Z(31)) * 0.5f;
            Assert.Equal("high", lakes.Name(lakes.LakeAt(x, z)));
            Assert.Equal(80f, lakes.WaterLevel(x, z));
        }

        [Fact]
        public void ATriangleFoldedFlatHoldsNoWater()
        {
            // One proper triangle, and one whose corners are all the same point: a sign test alone would
            // put every point of the map inside the second.
            var source = new LakeSurfaces.Source
            {
                Name = "folded",
                SurfaceY = 10f,
                X = new[] { 0f, 10f, 0f, 500f },
                Z = new[] { 0f, 0f, 10f, 500f },
                Triangles = new[] { 0, 1, 2, 3, 3, 3 },
            };

            LakeSurfaces lakes = LakeSurfaces.Build(new[] { source });
            Assert.Equal(1, lakes.Triangles(0));
            Assert.Equal(0, lakes.LakeAt(2f, 2f));
            Assert.Equal(-1, lakes.LakeAt(500f, 500f));
            Assert.Equal(-1, lakes.LakeAt(-50f, 300f));
            Assert.Equal(50.0, lakes.Area(0), 6);
        }

        [Fact]
        public void EitherWindingCounts()
        {
            var source = new LakeSurfaces.Source
            {
                Name = "wound",
                SurfaceY = 10f,
                X = new[] { 0f, 10f, 0f },
                Z = new[] { 0f, 0f, 10f },
                Triangles = new[] { 0, 2, 1 },
            };

            Assert.Equal(0, LakeSurfaces.Build(new[] { source }).LakeAt(1f, 1f));
        }

        [Fact]
        public void TwoSmallPiecesFarApartAreBucketedWithinBounds()
        {
            // Bounds of 100 km round triangles a metre across: the buckets grow rather than number in
            // the billions, and lookups stay right at both ends.
            var source = new LakeSurfaces.Source
            {
                Name = "spread",
                SurfaceY = 10f,
                X = new[] { 0f, 1f, 0f, 100000f, 100001f, 100000f },
                Z = new[] { 0f, 0f, 1f, 100000f, 100000f, 100001f },
                Triangles = new[] { 0, 1, 2, 3, 4, 5 },
            };

            LakeSurfaces lakes = LakeSurfaces.Build(new[] { source });
            Assert.Equal(0, lakes.LakeAt(0.2f, 0.2f));
            Assert.Equal(0, lakes.LakeAt(100000.2f, 100000.2f));
            Assert.Equal(-1, lakes.LakeAt(50000f, 50000f));
            Assert.Equal(-1, lakes.LakeAt(0.9f, 0.9f));
        }

        // --- the buckets lose nothing -----------------------------------------------------------------

        /// <summary>
        /// Whether a lake holds a point with no buckets at all: every triangle tried in turn, with the
        /// same bounds check and the same in-or-on test as the lookup, so the two can only disagree
        /// where the buckets have lost a triangle.
        /// </summary>
        static bool EveryTriangleHolds(LakeSurfaces.Source source, float x, float z)
        {
            int[] indices = source.Triangles;
            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                double ax = source.X[indices[t]], az = source.Z[indices[t]];
                double bx = source.X[indices[t + 1]], bz = source.Z[indices[t + 1]];
                double cx = source.X[indices[t + 2]], cz = source.Z[indices[t + 2]];
                if (Math.Abs((bx - ax) * (cz - az) - (bz - az) * (cx - ax)) < 1e-6) continue;

                if (x < Math.Min(ax, Math.Min(bx, cx)) || x > Math.Max(ax, Math.Max(bx, cx)) ||
                    z < Math.Min(az, Math.Min(bz, cz)) || z > Math.Max(az, Math.Max(bz, cz)))
                    continue;

                double d1 = (x - bx) * (az - bz) - (ax - bx) * (z - bz);
                double d2 = (x - cx) * (bz - cz) - (bx - cx) * (z - cz);
                double d3 = (x - ax) * (cz - az) - (cx - ax) * (z - az);
                bool negative = d1 < 0 || d2 < 0 || d3 < 0, positive = d1 > 0 || d2 > 0 || d3 > 0;
                if (!(negative && positive)) return true;
            }

            return false;
        }

        /// <summary>
        /// Points to ask a lake about: every corner of every triangle, points along every edge, its middle,
        /// a centimetre in from each corner, and random points over the lake's bounds and a little beyond.
        /// The edges and corners are where buckets meet and where a lost triangle shows first.
        /// </summary>
        static List<(float X, float Z)> Probes(LakeSurfaces.Source source, Random random, int scattered)
        {
            var points = new List<(float, float)>();
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            int[] indices = source.Triangles;

            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                float mx = 0f, mz = 0f;
                for (int k = 0; k < 3; k++)
                {
                    mx += source.X[indices[t + k]] / 3f;
                    mz += source.Z[indices[t + k]] / 3f;
                }

                points.Add((mx, mz));
                for (int k = 0; k < 3; k++)
                {
                    float x = source.X[indices[t + k]], z = source.Z[indices[t + k]];
                    float nx = source.X[indices[t + (k + 1) % 3]], nz = source.Z[indices[t + (k + 1) % 3]];
                    minX = Math.Min(minX, x); minZ = Math.Min(minZ, z); maxX = Math.Max(maxX, x); maxZ = Math.Max(maxZ, z);

                    points.Add((x, z));
                    foreach (float f in new[] { 0.25f, 0.5f, 0.75f }) points.Add((x + (nx - x) * f, z + (nz - z) * f));

                    double toMiddle = Math.Sqrt((mx - x) * (double)(mx - x) + (mz - z) * (double)(mz - z));
                    if (toMiddle > 0.02) points.Add(((float)(x + (mx - x) * 0.01 / toMiddle), (float)(z + (mz - z) * 0.01 / toMiddle)));
                }
            }

            for (int i = 0; i < scattered; i++)
                points.Add(((float)(minX - 50.0 + random.NextDouble() * (maxX - minX + 100.0)),
                            (float)(minZ - 50.0 + random.NextDouble() * (maxZ - minZ + 100.0))));

            return points;
        }

        [Fact]
        public void TheBucketsFindEveryPointThatEveryTriangleTriedInTurnFinds()
        {
            var random = new Random(4095);

            // A lake of irregular cells on the real grid, west of the map's centre where the float vertex
            // positions stray from the ideal grid, so its triangles fall across bucket boundaries as a real
            // sheet's do.
            var cells = new bool[48, 36];
            for (int c = 0; c < 48; c++)
                for (int r = 0; r < 36; r++)
                    cells[c, r] = random.NextDouble() < 0.6;
            LakeSurfaces.Source grid = Sheet("grid", 60.5f, 470, 1490, cells);

            // A lake of mixed triangles: a block of cells with large triangles across and beyond it, either
            // winding, so the buckets are sized to the cells and each large triangle spans dozens of them.
            var block = new bool[20, 20];
            for (int c = 0; c < 20; c++)
                for (int r = 0; r < 20; r++)
                    block[c, r] = true;
            LakeSurfaces.Source mixed = Sheet("mixed", 80f, 1700, 600, block);

            var xs = new List<float>(mixed.X);
            var zs = new List<float>(mixed.Z);
            var triangles = new List<int>(mixed.Triangles);
            for (int i = 0; i < 8; i++)
            {
                float size = 300f + (float)random.NextDouble() * 2700f;
                float x0 = X(1690) + (float)random.NextDouble() * 60f * Spacing, z0 = Z(590) + (float)random.NextDouble() * 50f * Spacing;
                int first = xs.Count;
                for (int k = 0; k < 3; k++)
                {
                    double angle = random.NextDouble() * Math.PI * 2.0;
                    xs.Add(x0 + size * (float)Math.Cos(angle));
                    zs.Add(z0 + size * (float)Math.Sin(angle));
                }

                triangles.AddRange(i % 2 == 0 ? new[] { first, first + 1, first + 2 } : new[] { first, first + 2, first + 1 });
            }

            mixed = new LakeSurfaces.Source { Name = "mixed", SurfaceY = 80f, X = xs.ToArray(), Z = zs.ToArray(), Triangles = triangles.ToArray() };

            LakeSurfaces lakes = LakeSurfaces.Build(new[] { grid, mixed });
            Assert.Equal(2, lakes.Count);

            var examples = new List<string>();
            int wrong = 0, water = 0, asked = 0;
            foreach ((LakeSurfaces.Source source, int index) in new[] { (grid, 0), (mixed, 1) })
                foreach ((float x, float z) in Probes(source, random, 20000))
                {
                    int expected = EveryTriangleHolds(source, x, z) ? index : -1;
                    int found = lakes.LakeAt(x, z);
                    asked++;
                    if (expected >= 0) water++;
                    if (found == expected) continue;

                    wrong++;
                    if (examples.Count < 5) examples.Add($"({x}, {z}) in {source.Name}: lookup {found}, every triangle {expected}");
                }

            Assert.True(wrong == 0, $"{wrong} of {asked} points disagree: {string.Join("; ", examples)}");

            // The probes are mostly water, so a lost triangle could not hide among dry points.
            Assert.True(water > asked / 2, $"{water} of {asked} probes are water");
        }

        // --- raycasts, as Plane.Raycast counts a hit -------------------------------------------------

        static LakeSurfaces TwoLakes()
        {
            var cells = new bool[4, 4];
            for (int c = 0; c < 4; c++)
                for (int r = 0; r < 4; r++)
                    cells[c, r] = true;

            return LakeSurfaces.Build(new[]
            {
                Sheet("low", 50f, 1024, 1024, cells),     // from 24 m east and north of the map's centre, 390 m square
                Sheet("high", 150f, 1036, 1024, cells),   // 1,170 m east of it
            });
        }

        [Fact]
        public void ARayDownOntoALakeMeetsItsSurface()
        {
            LakeSurfaces lakes = TwoLakes();
            Assert.True(lakes.Raycast(100.0, 300.0, 100.0, 0.0, -1.0, 0.0, out double distance, out int lake));
            Assert.Equal(250.0, distance, 6);
            Assert.Equal("low", lakes.Name(lake));
        }

        [Fact]
        public void ARayFromUnderTheSurfaceMeetsItGoingUp()
        {
            LakeSurfaces lakes = TwoLakes();
            Assert.True(lakes.Raycast(100.0, 40.0, 100.0, 0.0, 1.0, 0.0, out double distance, out _));
            Assert.Equal(10.0, distance, 6);
        }

        [Fact]
        public void ARayMeetsNothingOutsideTheFootprintsBehindItOrAlongTheSurface()
        {
            LakeSurfaces lakes = TwoLakes();
            Assert.False(lakes.Raycast(-500.0, 300.0, -500.0, 0.0, -1.0, 0.0, out _, out _));   // over dry land
            Assert.False(lakes.Raycast(100.0, 300.0, 100.0, 0.0, 1.0, 0.0, out _, out _));      // pointing away
            Assert.False(lakes.Raycast(100.0, 60.0, 100.0, 1.0, 0.0, 0.0, out _, out _));       // level
            Assert.False(lakes.Raycast(100.0, 300.0, 100.0, 0.0, -1.0, 0.0, out _, out _, above: 60f));
        }

        [Fact]
        public void ASlopingRayMeetsTheNearerLake()
        {
            LakeSurfaces lakes = TwoLakes();
            float x = (X(1037) + X(1038)) * 0.5f, z = (Z(1025) + Z(1026)) * 0.5f;

            // Straight down from 400 m over the high lake: the high one first.
            Assert.True(lakes.Raycast(x, 400.0, z, 0.0, -1.0, 0.0, out double distance, out int lake));
            Assert.Equal("high", lakes.Name(lake));
            Assert.Equal(250.0, distance, 6);

            // Down and west from beside the high lake, so it misses it and lands in the low one, 1 km on.
            double dx = -1.0, dy = -0.25, length = Math.Sqrt(dx * dx + dy * dy);
            Assert.True(lakes.Raycast(1100.0, 300.0, 200.0, dx / length, dy / length, 0.0, out distance, out lake));
            Assert.Equal("low", lakes.Name(lake));
            Assert.Equal(1000.0 * length, distance, 3);
        }

        // --- on the datum ----------------------------------------------------------------------------

        [Fact]
        public void AwayFromTheLakesTheLocalWaterIsTheSeasToTheBit()
        {
            var frame = new LakeFrame(TwoLakes());
            foreach (float originY in new[] { 0f, -384f, 1216f, 3.3e-3f, -0f })
            {
                float local = frame.LocalWaterY(-20000f, 7000f, 512f, originY, -64f);
                Assert.Equal(BitConverter.SingleToInt32Bits(originY + 0f), BitConverter.SingleToInt32Bits(local));
            }

            Assert.Equal(0f, frame.GlobalWaterY(-20000f, 7000f));
        }

        [Fact]
        public void TheFloatingOriginMovesTheLocalWaterNotTheGlobal()
        {
            var frame = new LakeFrame(TwoLakes());

            // A point over the low lake, seen from three origins: the scene moves by the origin, the
            // global answer does not.
            foreach ((float ox, float oy, float oz) in new[] { (0f, 0f, 0f), (1024f, -128f, -2048f), (-64f, 3200f, 640f) })
            {
                float localX = 100f + ox, localZ = 100f + oz;
                Assert.Equal(oy + 50f, frame.LocalWaterY(localX, localZ, ox, oy, oz), 3);
                Assert.Equal(50f, frame.GlobalWaterY(localX - ox, localZ - oz));

                Assert.True(frame.RaycastLakes(ox, oy, oz, localX, oy + 300f, localZ, 0f, -1f, 0f, out float enter));
                Assert.Equal(250f, enter, 3);
            }
        }

        [Fact]
        public void TheMapsPlaceOnTheDatumShiftsItsLakes()
        {
            // A map instantiated 10 m east and 5 m up of the datum carries its lakes with it.
            var frame = new LakeFrame(TwoLakes(), 10f, 5f, 0f);
            Assert.Equal(55f, frame.GlobalWaterY(105f, 100f));
            Assert.True(frame.TryGetLake(105f, 100f, out int lake, out float surface));
            Assert.Equal("low", frame.Surfaces.Name(lake));
            Assert.Equal(55f, surface);
            Assert.Equal(155f, frame.HighestSurfaceY);

            // Just west of the low lake's footprint once shifted: dry.
            Assert.Equal(0f, frame.GlobalWaterY(X(1024) + 10f - 0.5f, 100f));

            // Sunk 60 m, the low lake (50 m) would be under the sea: the sea has it.
            var sunk = new LakeFrame(TwoLakes(), 0f, -60f, 0f);
            Assert.False(sunk.TryGetLake(100f, 100f, out _, out _));
            Assert.Equal(0f, sunk.GlobalWaterY(100f, 100f));
            Assert.False(sunk.RaycastLakes(0f, 0f, 0f, 100f, 300f, 100f, 0f, -1f, 0f, out _));
        }
    }
}
