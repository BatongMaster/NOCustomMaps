using System;

namespace CustomMaps
{
    /// <summary>
    /// How far the nuclear shockwave's target query reaches, and how big a collider buffer has
    /// to be to hold everything inside it. Pure arithmetic, so it is tested; the patch that uses
    /// it is in <c>Patches/BlastBufferPatches.cs</c>.
    ///
    /// The query is <c>Physics.OverlapSphereNonAlloc</c> into a static buffer of 4,096
    /// colliders, and a non-alloc overlap that finds more than its buffer holds keeps an
    /// arbitrary 4,096 and drops the rest without a word. The radius below is the game's own,
    /// copied from the decompiled <c>Shockwave.Start</c>, and must stay equal to it: the patch
    /// sizes the buffer for the same sphere the game is about to query.
    /// </summary>
    internal static class BlastReach
    {
        /// <summary>The size the game gives the buffer.</summary>
        public const int GameBuffer = 4096;

        /// <summary>
        /// The most a grown buffer may hold. Two hundred and fifty thousand references is 2 MB
        /// on a 64-bit player, and is more than every collider a whole custom map carries today
        /// (Swiss Alps: about 5,000, its terrain tiles, roads and bridges and one per city tile;
        /// 72,000 when each of its 67,671 buildings had its own), so it is a guard against a
        /// runaway size rather than a limit a real blast meets.
        /// </summary>
        public const int MaxBuffer = 1 << 18;

        /// <summary>
        /// <c>Shockwave.Start</c>'s query radius, for a yield in kilotons:
        /// <c>blastPower = Pow(yieldKilotons * 1e6, 0.3333)</c>, <c>blastRadius = blastPower * 13</c>,
        /// and the overlap is <c>blastRadius * 2</c>. The game skips the query below 0.0002 kt,
        /// and so does this: zero means no query.
        /// </summary>
        public static float ShockwaveRadius(float yieldKilotons)
        {
            if (!(yieldKilotons >= 0.0002f)) return 0f;
            float blastPower = (float)Math.Pow(yieldKilotons * 1000000f, 0.3333f);
            return blastPower * 13f * 2f;
        }

        /// <summary>
        /// The buffer size that holds <paramref name="found"/> colliders with room to spare, so
        /// the game's non-alloc query into it comes back short of full and so provably complete.
        /// Returns <paramref name="current"/> when it already does; otherwise the smallest power
        /// of two above <paramref name="found"/>, no smaller than the game's size and no larger
        /// than <see cref="MaxBuffer"/>.
        /// </summary>
        public static int BufferFor(int found, int current)
        {
            if (found < current) return current;

            int size = GameBuffer;
            while (size <= found && size < MaxBuffer) size *= 2;
            return Math.Max(size, current);
        }
    }
}
