using System;

namespace CustomMaps
{
    /// <summary>
    /// Cell-size selection for <c>BattlefieldGrid</c>.
    ///
    /// The game builds its spatial hash in <c>LevelInfo.Awake</c> from the
    /// <em>scene-serialized</em> fields of the GameWorld scene — <c>mapSize = 81920</c>,
    /// <c>hashGridSize = 4096</c> — and <c>ApplyMapSettings</c> then assigns
    /// <c>mapSize = mapSettings.MapSize.x</c> without ever regenerating the grid.
    /// Both shipped maps happen to be 81920 wide on X, so the bug never bites in
    /// vanilla play. On a 143360 m map it bites hard: <c>TryGetGridXY</c> does
    ///
    ///     Mathf.Clamp((coord.x + mapSize * 0.5f) / gridSize, 0, divisions - 1)
    ///
    /// against the stale values, so everything beyond ±40,960 m collapses into the
    /// border ring, and <c>GetGridSquaresInRangeNonAlloc</c>'s
    /// <c>CeilToInt(range * divisions / mapSize)</c> computes the wrong radius on top
    /// of that. Unit lookup, wreck lookup, target acquisition, refuel proximity and
    /// obstacle queries all degrade together.
    ///
    /// The fix regenerates the grid, and this is how it picks the cell size.
    /// </summary>
    public static class GridMath
    {
        /// <summary>Heartland's serialized <c>hashGridSize</c>, and the size we try to preserve.</summary>
        public const float DefaultCell = 4096f;

        /// <summary>Below this the grid stops being a useful broad-phase.</summary>
        public const int MinDivisions = 8;

        /// <summary>192² = 36,864 GridSquares. Well past anything sane; a backstop, not a target.</summary>
        public const int MaxDivisions = 192;

        /// <summary>
        /// Where map size stops being sane. Not an engine limit — a practicality one.
        ///
        /// <c>FloatingOrigin</c> declares a ±90,000 m rigidbody kill bound, which looks
        /// like a hard 180 km ceiling and was treated as one here. It is not:
        /// <c>RBKillBounds()</c> is never called from anywhere in the assembly, and
        /// <c>Awake</c> is the only Unity message on the class, so nothing enforces it.
        /// The floating origin keeps local coordinates small regardless, and
        /// <c>GlobalPosition</c> is absolute float — at a 200 km half-extent its
        /// resolution is still 2.4 cm.
        ///
        /// What actually binds is memory. Terrain scales as area, and the collider set
        /// dominates: PhysX cooks roughly 20–40 bytes per triangle, so a map's physics
        /// data grows past a gigabyte well before its coordinates become a problem.
        /// See <see cref="EstimateColliderMegabytes"/>.
        /// </summary>
        public const float MaxSupportedMapSize = 400000f;

        /// <summary>Size beyond which the memory cost deserves a warning rather than
        /// silent acceptance. 200 km square is about 40,000 km² — double the design point
        /// that has actually been validated.</summary>
        public const float LargeMapWarningSize = 200000f;

        /// <summary>Heartland's tile size, and the unit terrain is chunked into.</summary>
        public const float TileSize = 5120f;

        /// <summary>Rough cooked-collider footprint in MB for a square map, at the tile
        /// resolution and relief mix the mesher targets. The number that decides whether
        /// a map is shippable.</summary>
        public static float EstimateColliderMegabytes(float mapSize)
        {
            if (!(mapSize > 0f)) return 0f;

            float tiles = mapSize / TileSize;
            float tileCount = tiles * tiles;

            // ~12,500 triangles per tile averaged over the flat/relief split, at ~30 B
            // per triangle for the mesh plus its BVH.
            return tileCount * 12500f * 30f / (1024f * 1024f);
        }

        /// <summary>
        /// Chooses a cell size for a map of <paramref name="mapSize"/> metres.
        ///
        /// Preserves <em>cell size</em>, not cell count. A range query costs
        /// <c>ceil(range / cell)²</c> squares, each holding a linearly scanned unit
        /// list, so holding the cell size fixed holds per-query work fixed as the map
        /// grows — whereas holding the count fixed would make every cell 3× wider on a
        /// 143 km map and every proximity scan 3× longer. The extra <c>GridSquare</c>
        /// objects are irrelevant: 35×35 = 1225 versus 20×20 = 400 is a few hundred KB.
        ///
        /// Divisibility matters because the game truncates: <c>divisions = (int)(mapSize / cell)</c>.
        /// A non-divisor leaves a strip along the top and right edge outside the last
        /// full cell, and everything in it clamps inward — a smaller version of the very
        /// bug being fixed.
        /// </summary>
        public static float ChooseHashGridSize(float mapSize, float referenceCell = DefaultCell)
        {
            if (!(mapSize > 0f) || float.IsNaN(mapSize) || float.IsInfinity(mapSize))
                return referenceCell > 0f ? referenceCell : DefaultCell;

            if (!(referenceCell > 0f) || float.IsNaN(referenceCell) || float.IsInfinity(referenceCell))
                referenceCell = DefaultCell;

            // The happy path, and the one our own map takes: 143360 / 4096 = 35 exactly.
            if (DividesEvenly(mapSize, referenceCell) && InRange(Divisions(mapSize, referenceCell)))
                return referenceCell;

            // Otherwise prefer a power-of-two cell near the reference, so the chosen size
            // stays recognisable in a log next to the vanilla 4096.
            float best = 0f;
            float bestDistance = float.MaxValue;
            for (int e = 5; e <= 16; e++)              // 32 m .. 65536 m
            {
                float cell = 1 << e;
                if (!DividesEvenly(mapSize, cell) || !InRange(Divisions(mapSize, cell))) continue;

                float distance = Math.Abs((float)Math.Log(cell / referenceCell, 2));
                if (distance < bestDistance) { bestDistance = distance; best = cell; }
            }
            if (best > 0f) return best;

            // No power of two divides this map. Fall back to a chosen division count.
            int n = (int)Math.Round(mapSize / referenceCell);
            if (n < MinDivisions) n = MinDivisions;
            if (n > MaxDivisions) n = MaxDivisions;
            return CellForDivisions(mapSize, n);
        }

        /// <summary>
        /// The largest cell size that still yields exactly <paramref name="divisions"/>
        /// cells under the game's truncating division.
        ///
        /// <c>mapSize / divisions</c> is the obvious answer and is wrong often enough to
        /// matter: in single precision the quotient can land a hair <em>above</em> the
        /// true value, so <c>(int)(mapSize / cell)</c> comes back one short and the game
        /// orphans an entire cell along the top and right edge — every unit in that
        /// strip clamps inward, which is a smaller copy of the very bug this class
        /// exists to fix. Nudging down by an ulp at a time costs nothing and removes
        /// the whole failure mode.
        /// </summary>
        public static float CellForDivisions(float mapSize, int divisions)
        {
            if (divisions <= 0 || !(mapSize > 0f)) return DefaultCell;

            float cell = mapSize / divisions;

            // float carries ~1.2e-7 relative precision, so a handful of steps is far
            // more than enough; the bound just guarantees termination.
            for (int i = 0; i < 8 && Divisions(mapSize, cell) < divisions; i++)
                cell -= cell * 1e-7f;

            return cell;
        }

        /// <summary>The game's own division count, truncation included.</summary>
        public static int Divisions(float mapSize, float cell)
        {
            if (!(cell > 0f) || !(mapSize > 0f)) return 0;
            return (int)(mapSize / cell);
        }

        /// <summary>
        /// True if the grid covers the map, leaving no strip outside the last full cell.
        ///
        /// Phrased against the game's truncating division rather than as an exact
        /// modulo, because that is the property that actually matters and an exact
        /// float modulo would reject the fallback cell sizes for being one ulp off
        /// while a 4 km orphaned strip would sail through. The tolerance is sub-metre
        /// on a 4 km cell.
        /// </summary>
        public static bool DividesEvenly(float mapSize, float cell)
        {
            int divisions = Divisions(mapSize, cell);
            if (divisions <= 0) return false;

            float uncovered = mapSize - divisions * cell;
            return uncovered < cell * 1e-3f;
        }

        /// <summary>Half-extent of the map, which is what the floating origin's kill bounds
        /// are measured against.</summary>
        public static float HalfExtent(float mapSize) => mapSize * 0.5f;

        static bool InRange(int divisions) => divisions >= MinDivisions && divisions <= MaxDivisions;
    }
}
