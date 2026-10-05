using System;
using System.Collections.Generic;
using Xunit;

namespace CustomMaps.Tests
{
    public class CityProxiesTests
    {
        const float Tile = 3120f;

        // suburbs_block23 as the catalogue has it: 31 x 52 m, 8 m high, base at 0.
        static readonly BuildingBox Terrace = new BuildingBox
        {
            MinX = -15.5f, MinY = 0f, MinZ = -26f, MaxX = 15.5f, MaxY = 8f, MaxZ = 26f,
        };

        // --- cells ---

        [Theory]
        [InlineData(0f, 0)]
        [InlineData(3119.9f, 0)]
        [InlineData(3120f, 1)]
        [InlineData(-0.5f, -1)]
        [InlineData(-3120f, -1)]
        [InlineData(-3120.5f, -2)]
        [InlineData(-97565.49f, -32)]   // Swiss Alps' westernmost building
        public void ACellIndexFloors(float coordinate, int expected)
        {
            Assert.Equal(expected, CityCells.Index(coordinate, Tile));
        }

        // The gate's old key, fx * 73856093 ^ fz * 19349663, sent each of these pairs of real
        // Swiss Alps cells to one key (2,276 buildings on 0.3.0), and a collider keyed that way
        // would have merged two towns 80 km apart into one mesh. Not only odd ones: any cell and
        // its mirror whose indices hold the same power of two, as the last two pairs do.
        [Theory]
        [InlineData(-1, -13, 1, 13)]
        [InlineData(-31, -27, 31, 27)]
        [InlineData(-21, -3, 21, 3)]
        [InlineData(-5, -17, 5, 17)]
        [InlineData(-2, -6, 2, 6)]
        [InlineData(-12, 20, 12, -20)]
        public void CellsTheOldHashMergedHaveKeysOfTheirOwn(int ax, int az, int bx, int bz)
        {
            int OldKey(int x, int z) => unchecked(x * 73856093 ^ z * 19349663);
            Assert.Equal(OldKey(ax, az), OldKey(bx, bz));

            Assert.NotEqual(CityCells.Key(ax, az), CityCells.Key(bx, bz));
        }

        [Fact]
        public void EveryCellOfAWideMapHasItsOwnKey()
        {
            var keys = new HashSet<long>();
            for (int x = -64; x <= 64; x++)
                for (int z = -64; z <= 64; z++)
                    Assert.True(keys.Add(CityCells.Key(x, z)), $"cell {x},{z} shares a key");
        }

        [Fact]
        public void ACellsCentreIsItsMiddle()
        {
            Assert.Equal(1560f, CityCells.Centre(0, Tile));
            Assert.Equal(-1560f, CityCells.Centre(-1, Tile));
            Assert.Equal(CityCells.KeyOf(-1560f, 4680f, Tile), CityCells.Key(-1, 1));
        }

        // --- turning and placing ---

        // Quaternion.Euler(0, 90, 0) * Vector3.forward is Vector3.right, and * Vector3.right is
        // Vector3.back: a box must turn the way the renderer it stands for does.
        [Theory]
        [InlineData(0f, 1.0, 2.0, 1.0, 2.0)]
        [InlineData(90f, 0.0, 1.0, 1.0, 0.0)]
        [InlineData(90f, 1.0, 0.0, 0.0, -1.0)]
        [InlineData(180f, 1.0, 2.0, -1.0, -2.0)]
        [InlineData(-90f, 0.0, 1.0, -1.0, 0.0)]
        public void RotationTurnsAsUnitysYawDoes(float yaw, double x, double z, double expectedX, double expectedZ)
        {
            CityProxies.Rotate(x, z, yaw, out double rx, out double rz);
            Assert.Equal(expectedX, rx, 9);
            Assert.Equal(expectedZ, rz, 9);
        }

        // The pivot correction: wherever the donor's pivot is and whatever the heading, the middle
        // of the footprint lands on the placement.
        [Theory]
        [InlineData(0f)]
        [InlineData(37f)]
        [InlineData(90f)]
        [InlineData(213.5f)]
        public void TheFootprintIsCentredOnThePlacement(float yaw)
        {
            const float px = 4f, pz = -2.5f, x = 81234.5f, z = -40321.25f;

            CityProxies.Origin(x, z, yaw, px, pz, out float ox, out float oz);
            CityProxies.Rotate(px, pz, yaw, out double cx, out double cz);

            // To the float the transform is stored in: 8 mm out here.
            Assert.Equal(x, ox + cx, 0.01);
            Assert.Equal(z, oz + cz, 0.01);
        }

        // --- one box ---

        [Fact]
        public void AnUnturnedBoxStandsWhereItsBuildingDoes()
        {
            var vertices = new float[CityProxies.CornersPerBox * 3];
            var indices = new int[CityProxies.IndicesPerBox];

            CityProxies.AppendBox(Terrace, 1000f, 412f, 2000f, 0f, 900.0, 1500.0, vertices, 0, indices, 0, 0);

            Bounds(vertices, out float[] min, out float[] max);
            Assert.Equal(new[] { 84.5f, 412f, 474f }, min);
            Assert.Equal(new[] { 115.5f, 420f, 526f }, max);
        }

        [Fact]
        public void ATurnedBoxIsTurnedAboutItsBuilding()
        {
            var vertices = new float[CityProxies.CornersPerBox * 3];
            var indices = new int[CityProxies.IndicesPerBox];

            // A quarter turn swaps the footprint's width and depth.
            CityProxies.AppendBox(Terrace, 0f, 0f, 0f, 90f, 0.0, 0.0, vertices, 0, indices, 0, 0);

            Bounds(vertices, out float[] min, out float[] max);
            Assert.Equal(-26f, min[0], 4);
            Assert.Equal(26f, max[0], 4);
            Assert.Equal(-15.5f, min[2], 4);
            Assert.Equal(15.5f, max[2], 4);
        }

        // Unity's physics queries skip a triangle's back face, so every face must look outwards
        // or rays pass into the building; and there is no floor, which faces down.
        [Theory]
        [InlineData(0f)]
        [InlineData(17f)]
        [InlineData(90f)]
        [InlineData(245f)]
        public void EveryTriangleFacesOutAndNoneIsAFloor(float yaw)
        {
            var vertices = new float[CityProxies.CornersPerBox * 3];
            var indices = new int[CityProxies.IndicesPerBox];
            CityProxies.AppendBox(Terrace, 50f, 10f, -70f, yaw, 0.0, 0.0, vertices, 0, indices, 0, 0);

            double[] centre = { 0, 0, 0 };
            for (int v = 0; v < CityProxies.CornersPerBox; v++)
                for (int k = 0; k < 3; k++) centre[k] += vertices[v * 3 + k] / CityProxies.CornersPerBox;

            int roofs = 0, walls = 0;
            for (int t = 0; t < CityProxies.TrianglesPerBox; t++)
            {
                double[] a = Corner(vertices, indices[t * 3]);
                double[] b = Corner(vertices, indices[t * 3 + 1]);
                double[] c = Corner(vertices, indices[t * 3 + 2]);

                double[] normal = Cross(Sub(b, a), Sub(c, a));
                double[] mid = { (a[0] + b[0] + c[0]) / 3, (a[1] + b[1] + c[1]) / 3, (a[2] + b[2] + c[2]) / 3 };

                Assert.True(Dot(normal, Sub(mid, centre)) > 0, $"triangle {t} faces into the box");
                Assert.True(normal[1] > -1e-6, $"triangle {t} faces down");

                if (normal[1] > 1e-6) roofs++;
                else walls++;
            }

            Assert.Equal(2, roofs);
            Assert.Equal(8, walls);
        }

        // --- cells of boxes ---

        [Fact]
        public void BuildingsAreFiledByTheirCellAndWrittenRelativeToItsMiddle()
        {
            var builder = new CityProxyBuilder(Tile);
            builder.Add(Terrace, 100f, 5f, 200f, 0f);           // cell 0,0
            builder.Add(Terrace, -100f, 6f, 200f, 0f);          // cell -1,0
            builder.Add(Terrace, 3000f, 7f, 3000f, 45f);        // cell 0,0 again

            Assert.Equal(3, builder.Boxes);
            Assert.Equal(2, builder.Cells.Count);

            CityProxyBuilder.Cell first = builder.Cells[0];
            Assert.Equal((0, 0), (first.X, first.Z));
            Assert.Equal((1560f, 1560f), (first.OriginX, first.OriginZ));
            Assert.Equal(2, first.Boxes);
            Assert.Equal(2 * CityProxies.CornersPerBox, first.VertexCount);
            Assert.Equal(2 * CityProxies.IndicesPerBox, first.IndexCount);

            var vertices = new float[first.VertexCount * 3];
            var indices = new int[first.IndexCount];
            first.Write(vertices, indices);

            // The first building's corners are relative to the cell's middle.
            Assert.Equal(100f - 15.5f - 1560f, vertices[0], 3);
            Assert.Equal(5f, vertices[1], 3);
            Assert.Equal(200f - 26f - 1560f, vertices[2], 3);

            // The second box's indices count on from the first box's corners.
            for (int i = 0; i < CityProxies.IndicesPerBox; i++)
            {
                Assert.InRange(indices[i], 0, CityProxies.CornersPerBox - 1);
                Assert.InRange(indices[CityProxies.IndicesPerBox + i], CityProxies.CornersPerBox, 2 * CityProxies.CornersPerBox - 1);
            }

            CityProxyBuilder.Cell second = builder.Cells[1];
            Assert.Equal((-1, 0), (second.X, second.Z));
        }

        [Fact]
        public void TownsTheOldHashMergedGetCollidersOfTheirOwn()
        {
            var builder = new CityProxyBuilder(Tile);
            builder.Add(Terrace, -1 * Tile + 10f, 0f, -13 * Tile + 10f, 0f);
            builder.Add(Terrace, 1 * Tile + 10f, 0f, 13 * Tile + 10f, 0f);

            Assert.Equal(2, builder.Cells.Count);
        }

        // Far from the datum a float resolves centimetres; within a cell of its middle, a tenth of
        // a millimetre. Every vertex stays within the cell's half-width plus one building.
        [Fact]
        public void VerticesStayCloseToTheirCellsMiddleFarOut()
        {
            var builder = new CityProxyBuilder(Tile);
            var rng = new Random(7);
            for (int i = 0; i < 500; i++)
                builder.Add(Terrace, 96000f + (float)rng.NextDouble() * Tile, 600f,
                            -97000f + (float)rng.NextDouble() * Tile, (float)rng.NextDouble() * 360f);

            float reach = Tile * 0.5f + 31f;   // half a cell, plus the terrace's half-diagonal and then some
            foreach (CityProxyBuilder.Cell cell in builder.Cells)
            {
                var vertices = new float[cell.VertexCount * 3];
                cell.Write(vertices, new int[cell.IndexCount]);
                for (int v = 0; v < vertices.Length; v += 3)
                {
                    Assert.InRange(vertices[v], -reach, reach);
                    Assert.InRange(vertices[v + 2], -reach, reach);
                }
            }
        }

        // Every cell's mesh is written through one pair of arrays sized for the largest cell, so a
        // load does not drop a pair per cell. A small cell written after a big one must come out as
        // it would on its own, whatever the big one left past its end.
        [Fact]
        public void OnePairOfArraysServesEveryCell()
        {
            var builder = new CityProxyBuilder(Tile);
            for (int i = 0; i < 7; i++) builder.Add(Terrace, 100f + 60f * i, 3f, 200f, 10f * i);   // cell 0,0
            for (int i = 0; i < 3; i++) builder.Add(Terrace, -900f, 4f, -80f * i - 10f, 33f);     // cell -1,-1

            Assert.Equal(7, builder.MostBoxesInACell);

            var vertices = new float[builder.MostBoxesInACell * CityProxies.CornersPerBox * 3];
            var indices = new int[builder.MostBoxesInACell * CityProxies.IndicesPerBox];

            CityProxyBuilder.Cell big = builder.Cells[0], small = builder.Cells[1];
            big.Write(vertices, indices);
            small.Write(vertices, indices);

            var alone = new float[small.VertexCount * 3];
            var aloneIndices = new int[small.IndexCount];
            small.Write(alone, aloneIndices);

            Assert.Equal(alone, vertices.AsSpan(0, alone.Length).ToArray());
            Assert.Equal(aloneIndices, indices.AsSpan(0, aloneIndices.Length).ToArray());
        }

        [Fact]
        public void ArraysWithTooLittleRoomAreRefused()
        {
            var builder = new CityProxyBuilder(Tile);
            builder.Add(Terrace, 10f, 0f, 10f, 0f);
            builder.Add(Terrace, 90f, 0f, 10f, 0f);
            CityProxyBuilder.Cell cell = builder.Cells[0];

            Assert.Throws<ArgumentException>(() => cell.Write(new float[cell.VertexCount * 3 - 1], new int[cell.IndexCount]));
            Assert.Throws<ArgumentException>(() => cell.Write(new float[cell.VertexCount * 3], new int[cell.IndexCount - 1]));
        }

        [Fact]
        public void AnIndexFormatWideEnoughIsAskedFor()
        {
            var builder = new CityProxyBuilder(Tile);
            for (int i = 0; i < 8192; i++) builder.Add(Terrace, 10f, 0f, 10f, 0f);
            Assert.False(builder.Cells[0].Needs32BitIndices);    // 65,536 vertices: 16 bits still reach

            builder.Add(Terrace, 10f, 0f, 10f, 0f);
            Assert.True(builder.Cells[0].Needs32BitIndices);
        }

        [Fact]
        public void ACellSizeMustBePositive()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CityProxyBuilder(0f));
        }

        static void Bounds(float[] vertices, out float[] min, out float[] max)
        {
            min = new[] { float.MaxValue, float.MaxValue, float.MaxValue };
            max = new[] { float.MinValue, float.MinValue, float.MinValue };
            for (int v = 0; v < vertices.Length; v += 3)
                for (int k = 0; k < 3; k++)
                {
                    min[k] = Math.Min(min[k], vertices[v + k]);
                    max[k] = Math.Max(max[k], vertices[v + k]);
                }
        }

        static double[] Corner(float[] vertices, int index) =>
            new double[] { vertices[index * 3], vertices[index * 3 + 1], vertices[index * 3 + 2] };

        static double[] Sub(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };

        static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

        static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    }
}
