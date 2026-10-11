using System;
using System.Collections.Generic;

namespace CustomMaps
{
    /// <summary>
    /// A map's raised lakes as the game's water sees them: each a flat surface at one height over
    /// a footprint, the triangles of the sheet the map draws it with.
    ///
    /// The game has one body of water, the sea, and nearly everything that reacts to water asks
    /// one question of it: is this point below global y = 0? A raised lake is a sheet higher up,
    /// so the answer there has to be "below the lake's surface, if the point is over the lake".
    /// This is that answer, in map coordinates, with nothing of Unity or the game in it so it can
    /// be tested; <see cref="LakeFrame"/> places it on the game's datum and <c>LakeWater</c> hands
    /// it to the patched game code.
    ///
    /// The footprint is the sheet's own triangles, not a box or a grid laid over them, so the
    /// water ends exactly where the drawn water does. A point on the edge shared by two triangles
    /// is in both. A lake whose surface is at or below the sea is never water here: the sea
    /// already covers it.
    ///
    /// Lookups are a few comparisons for a point outside every lake's bounds and a handful of
    /// point-in-triangle tests inside one: each lake keeps its triangles in buckets about one
    /// triangle across.
    /// </summary>
    internal sealed class LakeSurfaces
    {
        /// <summary>One lake as read from the map: its name, the height of its surface, and its
        /// sheet's triangles, as vertex X and Z (map metres) and indices three to a triangle.</summary>
        public sealed class Source
        {
            public string Name;
            public float SurfaceY;
            public float[] X;
            public float[] Z;
            public int[] Triangles;
        }

        /// <summary>Most buckets a lake gets; past this they are made bigger.</summary>
        const int MaxBuckets = 1 << 22;

        sealed class Lake
        {
            public string Name;
            public float SurfaceY;
            public float MinX, MinZ, MaxX, MaxZ;
            public double Area;

            // Each triangle's corners, six floats (ax, az, bx, bz, cx, cz), and its bounds, four.
            public float[] Corners;
            public float[] Bounds;

            public float BucketSize;
            public int BucketsX, BucketsZ;
            public int[] BucketStart;   // BucketsX * BucketsZ + 1 offsets into BucketItems
            public int[] BucketItems;   // triangle indices
        }

        readonly Lake[] _lakes;

        /// <summary>The bounds of every lake together, for the quick rejection of a point that is
        /// in none. Empty (min above max) when there are no lakes.</summary>
        public float MinX { get; }
        public float MinZ { get; }
        public float MaxX { get; }
        public float MaxZ { get; }

        /// <summary>The highest lake surface, or negative infinity when there are no lakes.</summary>
        public float HighestSurface { get; }

        public int Count => _lakes.Length;

        LakeSurfaces(Lake[] lakes)
        {
            _lakes = lakes;

            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            float highest = float.NegativeInfinity;
            foreach (Lake lake in lakes)
            {
                minX = Math.Min(minX, lake.MinX);
                minZ = Math.Min(minZ, lake.MinZ);
                maxX = Math.Max(maxX, lake.MaxX);
                maxZ = Math.Max(maxZ, lake.MaxZ);
                highest = Math.Max(highest, lake.SurfaceY);
            }

            MinX = minX;
            MinZ = minZ;
            MaxX = maxX;
            MaxZ = maxZ;
            HighestSurface = highest;
        }

        /// <summary>
        /// The lakes, from their sheets. A lake whose surface is not above <paramref name="seaLevel"/>,
        /// or whose sheet has no triangle with an area, is left out: the sea already covers the
        /// first, and the second covers nothing. Degenerate triangles are dropped, since a point on
        /// a triangle folded flat would pass the test for it.
        /// </summary>
        public static LakeSurfaces Build(IEnumerable<Source> sources, float seaLevel = 0f)
        {
            if (sources == null) throw new ArgumentNullException(nameof(sources));

            var lakes = new List<Lake>();
            foreach (Source source in sources)
            {
                if (source == null || !(source.SurfaceY > seaLevel)) continue;
                Lake lake = BuildLake(source);
                if (lake != null) lakes.Add(lake);
            }

            return new LakeSurfaces(lakes.ToArray());
        }

        static Lake BuildLake(Source source)
        {
            float[] xs = source.X, zs = source.Z;
            int[] indices = source.Triangles;
            if (xs == null || zs == null || indices == null || xs.Length != zs.Length) return null;

            var corners = new List<float>();
            var bounds = new List<float>();
            double area = 0.0;
            var extents = new List<float>();

            for (int t = 0; t + 2 < indices.Length; t += 3)
            {
                int a = indices[t], b = indices[t + 1], c = indices[t + 2];
                if (a < 0 || b < 0 || c < 0 || a >= xs.Length || b >= xs.Length || c >= xs.Length) return null;

                double cross = (xs[b] - (double)xs[a]) * (zs[c] - (double)zs[a]) - (zs[b] - (double)zs[a]) * (xs[c] - (double)xs[a]);
                if (Math.Abs(cross) < 1e-6) continue;

                corners.Add(xs[a]); corners.Add(zs[a]);
                corners.Add(xs[b]); corners.Add(zs[b]);
                corners.Add(xs[c]); corners.Add(zs[c]);

                float minX = Math.Min(xs[a], Math.Min(xs[b], xs[c])), maxX = Math.Max(xs[a], Math.Max(xs[b], xs[c]));
                float minZ = Math.Min(zs[a], Math.Min(zs[b], zs[c])), maxZ = Math.Max(zs[a], Math.Max(zs[b], zs[c]));
                bounds.Add(minX); bounds.Add(minZ); bounds.Add(maxX); bounds.Add(maxZ);

                area += Math.Abs(cross) * 0.5;
                extents.Add(Math.Max(maxX - minX, maxZ - minZ));
            }

            int count = corners.Count / 6;
            if (count == 0) return null;

            var lake = new Lake
            {
                Name = source.Name,
                SurfaceY = source.SurfaceY,
                Corners = corners.ToArray(),
                Bounds = bounds.ToArray(),
                Area = area,
                MinX = float.MaxValue, MinZ = float.MaxValue, MaxX = float.MinValue, MaxZ = float.MinValue,
            };

            for (int t = 0; t < count; t++)
            {
                lake.MinX = Math.Min(lake.MinX, lake.Bounds[t * 4]);
                lake.MinZ = Math.Min(lake.MinZ, lake.Bounds[t * 4 + 1]);
                lake.MaxX = Math.Max(lake.MaxX, lake.Bounds[t * 4 + 2]);
                lake.MaxZ = Math.Max(lake.MaxZ, lake.Bounds[t * 4 + 3]);
            }

            // About one triangle to a bucket: the median triangle's longer side.
            extents.Sort();
            float size = Math.Max(extents[extents.Count / 2], 1f);
            double spanX = lake.MaxX - (double)lake.MinX, spanZ = lake.MaxZ - (double)lake.MinZ;
            while ((Math.Floor(spanX / size) + 1) * (Math.Floor(spanZ / size) + 1) > MaxBuckets) size *= 2f;

            lake.BucketSize = size;
            lake.BucketsX = (int)Math.Floor(spanX / size) + 1;
            lake.BucketsZ = (int)Math.Floor(spanZ / size) + 1;

            int buckets = lake.BucketsX * lake.BucketsZ;
            var counts = new int[buckets + 1];
            for (int pass = 0; pass < 2; pass++)
            {
                var fill = pass == 1 ? (int[])lake.BucketStart.Clone() : null;
                for (int t = 0; t < count; t++)
                {
                    BucketRange(lake, lake.Bounds[t * 4], lake.Bounds[t * 4 + 1], lake.Bounds[t * 4 + 2], lake.Bounds[t * 4 + 3],
                                out int x0, out int z0, out int x1, out int z1);
                    for (int bz = z0; bz <= z1; bz++)
                        for (int bx = x0; bx <= x1; bx++)
                        {
                            int at = bz * lake.BucketsX + bx;
                            if (pass == 0) counts[at + 1]++;
                            else lake.BucketItems[fill[at]++] = t;
                        }
                }

                if (pass == 0)
                {
                    for (int i = 0; i < buckets; i++) counts[i + 1] += counts[i];
                    lake.BucketStart = counts;
                    lake.BucketItems = new int[counts[buckets]];
                }
            }

            return lake;
        }

        static void BucketRange(Lake lake, float minX, float minZ, float maxX, float maxZ,
                                out int x0, out int z0, out int x1, out int z1)
        {
            x0 = Bucket(minX, lake.MinX, lake.BucketSize, lake.BucketsX);
            x1 = Bucket(maxX, lake.MinX, lake.BucketSize, lake.BucketsX);
            z0 = Bucket(minZ, lake.MinZ, lake.BucketSize, lake.BucketsZ);
            z1 = Bucket(maxZ, lake.MinZ, lake.BucketSize, lake.BucketsZ);
        }

        static int Bucket(float value, float origin, float size, int count)
        {
            int at = (int)Math.Floor((value - (double)origin) / size);
            return at < 0 ? 0 : at >= count ? count - 1 : at;
        }

        public string Name(int lake) => _lakes[lake].Name;

        /// <summary>The height of a lake's surface, in the map's own coordinates.</summary>
        public float SurfaceY(int lake) => _lakes[lake].SurfaceY;

        /// <summary>A lake's footprint, in square metres.</summary>
        public double Area(int lake) => _lakes[lake].Area;

        /// <summary>How many triangles a lake's footprint is made of.</summary>
        public int Triangles(int lake) => _lakes[lake].Corners.Length / 6;

        /// <summary>
        /// The lake whose footprint holds this point of the map, or -1 if none does. Where two
        /// lakes' footprints overlap (no map built so far has any), the higher surface wins, as the
        /// higher water would.
        /// </summary>
        public int LakeAt(float x, float z)
        {
            if (x < MinX || x > MaxX || z < MinZ || z > MaxZ) return -1;

            int found = -1;
            for (int i = 0; i < _lakes.Length; i++)
            {
                if (found >= 0 && _lakes[i].SurfaceY <= _lakes[found].SurfaceY) continue;
                if (Contains(_lakes[i], x, z)) found = i;
            }

            return found;
        }

        /// <summary>Whether this lake's footprint holds the point.</summary>
        public bool Contains(int lake, float x, float z) => Contains(_lakes[lake], x, z);

        static bool Contains(Lake lake, float x, float z)
        {
            if (x < lake.MinX || x > lake.MaxX || z < lake.MinZ || z > lake.MaxZ) return false;

            int bx = Bucket(x, lake.MinX, lake.BucketSize, lake.BucketsX);
            int bz = Bucket(z, lake.MinZ, lake.BucketSize, lake.BucketsZ);
            int at = bz * lake.BucketsX + bx;

            float[] bounds = lake.Bounds, corners = lake.Corners;
            for (int k = lake.BucketStart[at], end = lake.BucketStart[at + 1]; k < end; k++)
            {
                int t = lake.BucketItems[k];
                if (x < bounds[t * 4] || z < bounds[t * 4 + 1] || x > bounds[t * 4 + 2] || z > bounds[t * 4 + 3]) continue;
                if (InTriangle(corners, t * 6, x, z)) return true;
            }

            return false;
        }

        /// <summary>A point in or on a triangle in the XZ plane, either winding.</summary>
        static bool InTriangle(float[] c, int at, double x, double z)
        {
            double ax = c[at], az = c[at + 1], bx = c[at + 2], bz = c[at + 3], cx = c[at + 4], cz = c[at + 5];
            double d1 = (x - bx) * (az - bz) - (ax - bx) * (z - bz);
            double d2 = (x - cx) * (bz - cz) - (bx - cx) * (z - cz);
            double d3 = (x - ax) * (cz - az) - (cx - ax) * (z - az);

            bool negative = d1 < 0 || d2 < 0 || d3 < 0;
            bool positive = d1 > 0 || d2 > 0 || d3 > 0;
            return !(negative && positive);
        }

        /// <summary>
        /// The water level at a point of the map: the surface of the lake over it, or
        /// <paramref name="seaLevel"/> where there is none, or where the lake's is lower.
        /// </summary>
        public float WaterLevel(float x, float z, float seaLevel = 0f)
        {
            int lake = LakeAt(x, z);
            if (lake < 0) return seaLevel;

            float surface = _lakes[lake].SurfaceY;
            return surface > seaLevel ? surface : seaLevel;
        }

        /// <summary>
        /// Where a ray, in map coordinates, first meets a lake's surface inside that lake's
        /// footprint: the distance along it in units of <paramref name="dx"/>, <paramref name="dy"/>,
        /// <paramref name="dz"/> (metres for a unit direction), and which lake. Only ahead of the
        /// origin, as <c>Plane.Raycast</c> counts a hit, and from either side of the surface, as
        /// the sea's plane is met from below too. A ray parallel to the surfaces meets none, and
        /// so does a lake whose surface is not above <paramref name="above"/>.
        /// </summary>
        public bool Raycast(double ox, double oy, double oz, double dx, double dy, double dz,
                            out double distance, out int lake, float above = float.NegativeInfinity)
        {
            distance = double.PositiveInfinity;
            lake = -1;
            if (dy == 0.0) return false;

            for (int i = 0; i < _lakes.Length; i++)
            {
                Lake candidate = _lakes[i];
                if (!(candidate.SurfaceY > above)) continue;

                double t = (candidate.SurfaceY - oy) / dy;
                if (!(t > 0.0) || !(t < distance)) continue;

                double x = ox + dx * t, z = oz + dz * t;
                if (!Contains(candidate, (float)x, (float)z)) continue;

                distance = t;
                lake = i;
            }

            return lake >= 0;
        }
    }

    /// <summary>
    /// A map's lakes placed on the game's datum.
    ///
    /// The game keeps two frames. Global coordinates are fixed to the map: the sea is at global
    /// y = 0 and a lake's surface at its height above it. Local coordinates are the scene's, which
    /// the floating origin shifts under everything as the camera travels:
    /// <c>global = local - Datum.originPosition</c>, so the sea's local height
    /// (<c>Datum.LocalSeaY</c>) is the origin's y. The map's prefab is instantiated at the
    /// origin with no rotation, so its own coordinates are global ones, give or take
    /// <see cref="OffsetX"/>, <see cref="OffsetY"/>, <see cref="OffsetZ"/> (zero when the game
    /// loads a map the way it loads its own).
    ///
    /// Every query falls back to the sea's own value, arithmetic and all, where there is no lake:
    /// the local water level away from a lake is <c>originY + 0</c>, which is
    /// <c>Datum.LocalSeaY</c> to the bit, so a patched test reads exactly as the game's own
    /// everywhere but over a lake.
    /// </summary>
    internal sealed class LakeFrame
    {
        public LakeSurfaces Surfaces { get; }

        /// <summary>Global coordinates of the map's own origin.</summary>
        public float OffsetX { get; }
        public float OffsetY { get; }
        public float OffsetZ { get; }

        /// <summary>The global height of the highest lake's surface, or negative infinity when there
        /// is no lake: nothing above it is in any lake, which spares a lookup for most of what flies.</summary>
        public float HighestSurfaceY { get; }

        public LakeFrame(LakeSurfaces surfaces, float offsetX = 0f, float offsetY = 0f, float offsetZ = 0f)
        {
            Surfaces = surfaces ?? throw new ArgumentNullException(nameof(surfaces));
            OffsetX = offsetX;
            OffsetY = offsetY;
            OffsetZ = offsetZ;
            HighestSurfaceY = surfaces.HighestSurface + offsetY;
        }

        /// <summary>The lake over a global point, and its surface's global height; false where
        /// there is none.</summary>
        public bool TryGetLake(float globalX, float globalZ, out int lake, out float surfaceGlobalY)
        {
            lake = Surfaces.LakeAt(globalX - OffsetX, globalZ - OffsetZ);
            if (lake < 0)
            {
                surfaceGlobalY = 0f;
                return false;
            }

            surfaceGlobalY = Surfaces.SurfaceY(lake) + OffsetY;
            if (!(surfaceGlobalY > 0f))
            {
                lake = -1;
                surfaceGlobalY = 0f;
                return false;
            }

            return true;
        }

        /// <summary>The global height of the water at a global point: a lake's surface, or the
        /// sea's 0.</summary>
        public float GlobalWaterY(float globalX, float globalZ)
            => TryGetLake(globalX, globalZ, out _, out float surface) ? surface : 0f;

        /// <summary>The local height of the water under a local point, with the datum's origin at
        /// (<paramref name="originX"/>, <paramref name="originY"/>, <paramref name="originZ"/>).</summary>
        public float LocalWaterY(float localX, float localZ, float originX, float originY, float originZ)
            => originY + GlobalWaterY(localX - originX, localZ - originZ);

        /// <summary>
        /// How far a ray, in local coordinates, travels before it meets a lake's surface inside its
        /// footprint, by the arithmetic <c>Plane.Raycast</c> uses for the sea's plane (the
        /// direction is taken as given; <c>Ray</c> has already made it unit length). False when it
        /// meets none ahead of it.
        /// </summary>
        public bool RaycastLakes(float originX, float originY, float originZ, float rayX, float rayY, float rayZ,
                                 float dirX, float dirY, float dirZ, out float enter)
        {
            enter = 0f;
            double ox = rayX - (double)originX - OffsetX, oy = rayY - (double)originY - OffsetY, oz = rayZ - (double)originZ - OffsetZ;
            if (!Surfaces.Raycast(ox, oy, oz, dirX, dirY, dirZ, out double distance, out _, -OffsetY)) return false;

            enter = (float)distance;
            return enter > 0f;
        }
    }
}
