using System;
using System.Collections.Generic;

namespace CustomMaps
{
    /// <summary>
    /// A building's extent in its own frame, before its heading is applied: the box its donor's
    /// meshes fill, in metres, with the donor's scale already in it. X and Z are the plan, Y is up.
    /// </summary>
    internal struct BuildingBox
    {
        public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;

        /// <summary>The middle of the footprint, which is where the donor's pivot should have been.</summary>
        public float CentreX => (MinX + MaxX) * 0.5f;

        public float CentreZ => (MinZ + MaxZ) * 0.5f;
    }

    /// <summary>
    /// The square cells a map's buildings are filed under, shared by the gate that switches their
    /// renderers (<c>CityTileGate</c>) and the merged colliders (<c>CityColliders</c>), so a tile
    /// that is drawn and the collider under it hold the same buildings.
    ///
    /// A cell is its pair of integer indices and nothing else. The gate used to key its tiles by
    /// <c>fx * 73856093 ^ fz * 19349663</c>, and that hash sends (fx, fz) and (-fx, -fz) to the same
    /// key whenever fx and fz are divisible by the same power of two and no higher (both odd, both
    /// twice an odd number, and so on), because both multipliers are odd and in two's complement
    /// <c>-a ^ -b == a ^ b</c> for any such a and b. On Swiss Alps 0.3.0 its 383 real cells became
    /// 374 keys, so 2,276 buildings were switched by the distance to a cell on the far side of the
    /// map. Harmless for drawing, but a collider keyed that way would be one mesh spanning 190 km.
    /// </summary>
    internal static class CityCells
    {
        /// <summary>The cell index along one axis: floor, so -0.5 m is in cell -1, not 0.</summary>
        public static int Index(float coordinate, float size) => (int)Math.Floor(coordinate / (double)size);

        /// <summary>One cell's key: both indices whole, side by side, so no two cells share one.</summary>
        public static long Key(int x, int z) => ((long)x << 32) | (uint)z;

        /// <summary>The key of the cell holding a point of the map's plan.</summary>
        public static long KeyOf(float x, float z, float size) => Key(Index(x, size), Index(z, size));

        /// <summary>The middle of cell <paramref name="index"/> along its axis.</summary>
        public static float Centre(int index, float size) => (float)((index + 0.5) * size);
    }

    /// <summary>
    /// The geometry of a building's collision proxy: its <see cref="BuildingBox"/>, turned to the
    /// building's heading and stood where its renderer stands. Free of game and Unity types so it is
    /// tested; <c>CityColliders</c> turns the result into meshes.
    ///
    /// Each proxy is eight corners and ten triangles: four walls and a roof, and no floor. Nothing
    /// meets a building from beneath its base but the ground, which has its own collider, and a
    /// floor would add a fifth to the triangles that are cooked at every load. Every triangle faces
    /// out, as a render mesh's do: physics queries in Unity ignore a triangle's back unless
    /// <c>Physics.queriesHitBackfaces</c> is set, so a box wound inside out would be invisible to
    /// every ray the game casts.
    /// </summary>
    internal static class CityProxies
    {
        public const int CornersPerBox = 8;
        public const int TrianglesPerBox = 10;
        public const int IndicesPerBox = TrianglesPerBox * 3;

        /// <summary>The most vertices a mesh with 16-bit indices can address.</summary>
        public const int MaxVertices16 = 65536;

        /// <summary>
        /// The corners round the base, 0 at min X and min Z, 1 at max X, 2 at max X and max Z, 3 at
        /// max Z; 4 to 7 are the same four at the top.
        /// </summary>
        static readonly bool[] CornerMaxX = { false, true, true, false, false, true, true, false };
        static readonly bool[] CornerMaxZ = { false, false, true, true, false, false, true, true };

        /// <summary>
        /// The ten triangles, each wound so that <c>Cross(b - a, c - a)</c> points out of the box,
        /// which is Unity's front face: walls at -Z, +X, +Z, -X, then the roof.
        /// </summary>
        static readonly int[] Triangles =
        {
            0, 4, 5,   0, 5, 1,     // -Z
            1, 5, 6,   1, 6, 2,     // +X
            3, 2, 6,   3, 6, 7,     // +Z
            0, 3, 7,   0, 7, 4,     // -X
            4, 6, 5,   4, 7, 6,     // roof
        };

        /// <summary>
        /// Turns a point of the plan about the vertical axis by <paramref name="yaw"/> degrees,
        /// as <c>Quaternion.Euler(0, yaw, 0)</c> does: +Z turns towards +X, so a yaw of 90 takes
        /// north to east, and +X towards -Z.
        /// </summary>
        public static void Rotate(double x, double z, float yaw, out double rx, out double rz)
        {
            double radians = yaw * (Math.PI / 180.0);
            double cos = Math.Cos(radians), sin = Math.Sin(radians);

            rx = x * cos + z * sin;
            rz = z * cos - x * sin;
        }

        /// <summary>
        /// Where a building's transform goes in the plan so that its footprint, whose middle is
        /// <paramref name="pivotX"/>, <paramref name="pivotZ"/> from the transform in the donor's own
        /// frame, is centred on the placement (<paramref name="x"/>, <paramref name="z"/>) whatever
        /// the heading. The generator cleared roads and neighbours round the footprint, not round
        /// the donor's pivot.
        /// </summary>
        public static void Origin(float x, float z, float yaw, float pivotX, float pivotZ,
                                  out float originX, out float originZ)
        {
            Rotate(pivotX, pivotZ, yaw, out double shiftX, out double shiftZ);

            originX = (float)(x - shiftX);
            originZ = (float)(z - shiftZ);
        }

        /// <summary>
        /// Writes one proxy: <paramref name="box"/> turned to <paramref name="yaw"/> and stood at
        /// the building's transform (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>),
        /// as positions relative to (<paramref name="relativeToX"/>, 0, <paramref name="relativeToZ"/>),
        /// eight corners (x, y, z interleaved) from <paramref name="vertexAt"/> and thirty indices
        /// from <paramref name="indexAt"/>, numbered from <paramref name="firstVertex"/>.
        ///
        /// Worked in doubles and stored relative to the cell, so a building 97 km out is no less
        /// exact than one at the origin: a float there resolves 8 mm, one within 2 km of its cell's
        /// middle a tenth of a millimetre.
        /// </summary>
        public static void AppendBox(BuildingBox box, float x, float y, float z, float yaw,
                                     double relativeToX, double relativeToZ,
                                     float[] vertices, int vertexAt, int[] indices, int indexAt, int firstVertex)
        {
            double radians = yaw * (Math.PI / 180.0);
            double cos = Math.Cos(radians), sin = Math.Sin(radians);

            double atX = x - relativeToX, atZ = z - relativeToZ;

            for (int corner = 0; corner < CornersPerBox; corner++)
            {
                double cx = CornerMaxX[corner] ? box.MaxX : box.MinX;
                double cz = CornerMaxZ[corner] ? box.MaxZ : box.MinZ;
                double cy = corner < 4 ? box.MinY : box.MaxY;

                int at = vertexAt + corner * 3;
                vertices[at] = (float)(atX + cx * cos + cz * sin);
                vertices[at + 1] = (float)(y + cy);
                vertices[at + 2] = (float)(atZ + cz * cos - cx * sin);
            }

            for (int i = 0; i < IndicesPerBox; i++)
                indices[indexAt + i] = firstVertex + Triangles[i];
        }
    }

    /// <summary>
    /// Collects a map's building proxies by cell, as the buildings are placed, and writes each
    /// cell's out as one mesh's vertices and indices.
    /// </summary>
    internal sealed class CityProxyBuilder
    {
        /// <summary>One cell's buildings, and the mesh they make.</summary>
        internal sealed class Cell
        {
            public readonly int X, Z;

            /// <summary>Where the cell's collider stands in the map's plan: the middle of the cell,
            /// which every vertex is written relative to.</summary>
            public readonly float OriginX, OriginZ;

            internal readonly List<Entry> Entries = new List<Entry>();

            internal Cell(int x, int z, float size)
            {
                X = x;
                Z = z;
                OriginX = CityCells.Centre(x, size);
                OriginZ = CityCells.Centre(z, size);
            }

            public int Boxes => Entries.Count;

            public int VertexCount => Entries.Count * CityProxies.CornersPerBox;

            public int IndexCount => Entries.Count * CityProxies.IndicesPerBox;

            /// <summary>True when the mesh has more vertices than 16-bit indices reach: about
            /// eight thousand buildings in one cell, six times the most Swiss Alps has.</summary>
            public bool Needs32BitIndices => VertexCount > CityProxies.MaxVertices16;

            /// <summary>
            /// Writes the cell's mesh to the start of <paramref name="vertices"/> (positions as x, y,
            /// z triples relative to the origin, <see cref="VertexCount"/> of them) and of
            /// <paramref name="indices"/> (<see cref="IndexCount"/> triangle indices); whatever the
            /// arrays hold past that is left alone.
            ///
            /// The arrays are the caller's so that one pair, sized for the largest cell, serves every
            /// cell in turn. A pair per cell made fourteen megabytes of garbage at every load of Swiss
            /// Alps, for meshes that copy them straight away.
            /// </summary>
            public void Write(float[] vertices, int[] indices)
            {
                if (vertices == null || vertices.Length < VertexCount * 3)
                    throw new ArgumentException($"room for {VertexCount * 3} floats is needed", nameof(vertices));
                if (indices == null || indices.Length < IndexCount)
                    throw new ArgumentException($"room for {IndexCount} indices is needed", nameof(indices));

                for (int i = 0; i < Entries.Count; i++)
                {
                    Entry e = Entries[i];
                    CityProxies.AppendBox(e.Box, e.X, e.Y, e.Z, e.Yaw, OriginX, OriginZ,
                                          vertices, i * CityProxies.CornersPerBox * 3,
                                          indices, i * CityProxies.IndicesPerBox,
                                          i * CityProxies.CornersPerBox);
                }
            }
        }

        internal struct Entry
        {
            public BuildingBox Box;
            public float X, Y, Z, Yaw;
        }

        readonly float _size;
        readonly Dictionary<long, Cell> _byKey = new Dictionary<long, Cell>();
        readonly List<Cell> _cells = new List<Cell>();

        public CityProxyBuilder(float cellSize)
        {
            if (!(cellSize > 0f)) throw new ArgumentOutOfRangeException(nameof(cellSize));
            _size = cellSize;
        }

        /// <summary>The cells holding at least one building, in the order they were first used.</summary>
        public IReadOnlyList<Cell> Cells => _cells;

        public int Boxes { get; private set; }

        /// <summary>The most boxes any one cell holds, which is what the arrays every cell's mesh is
        /// written to must have room for (<see cref="Cell.Write"/>).</summary>
        public int MostBoxesInACell { get; private set; }

        /// <summary>
        /// Files one building: <paramref name="box"/> turned to <paramref name="yaw"/> with its
        /// transform at (<paramref name="x"/>, <paramref name="y"/>, <paramref name="z"/>) in the map's
        /// space. The cell is the one holding the transform, as the gate files the renderer.
        /// </summary>
        public void Add(BuildingBox box, float x, float y, float z, float yaw)
        {
            int cx = CityCells.Index(x, _size), cz = CityCells.Index(z, _size);
            long key = CityCells.Key(cx, cz);

            if (!_byKey.TryGetValue(key, out Cell cell))
            {
                cell = new Cell(cx, cz, _size);
                _byKey[key] = cell;
                _cells.Add(cell);
            }

            cell.Entries.Add(new Entry { Box = box, X = x, Y = y, Z = z, Yaw = yaw });
            Boxes++;

            if (cell.Entries.Count > MostBoxesInACell) MostBoxesInACell = cell.Entries.Count;
        }
    }
}
