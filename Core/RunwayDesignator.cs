using System;

namespace CustomMaps
{
    /// <summary>
    /// The number a runway end is painted with and called by: its heading in tens of degrees.
    ///
    /// Worked out exactly as the game names a runway (<c>Airbase.Runway.GetName</c>, decompiled
    /// 2026-10-02): <c>Mathf.RoundToInt(Quaternion.LookRotation(End - Start).eulerAngles.y * 0.1f)</c>,
    /// 0 read as 36, and a leading zero below 10 ("Runway 04"). The bearing is true, clockwise from
    /// the map's +Z; the game has no magnetic declination anywhere. <c>Mathf.RoundToInt</c> is
    /// <c>Math.Round</c>, which rounds a half to the even number, so 45° is "04" and 55° is "06".
    /// The number at a runway's start is the direction from its start to its end, the one an
    /// aircraft landing over that threshold flies, and the reciprocal is painted at the other end.
    /// Measured on the shipped airfields, 23 of their 26 numbered ends are painted exactly this; the
    /// other three are exact halves painted by hand the other way (City Airport's 330° runway reads
    /// "34" on the ground and "Runway 33" on the radio). Computing the paint the game's way keeps
    /// the two the same.
    ///
    /// There are no letters for parallel runways: the game's marking texture has none, and its own
    /// parallel runways are painted with the same number twice. Neither does the radio add any.
    ///
    /// Shared by the editor, which paints the numbers, and the plugin, which logs what the game
    /// will call each runway beside what was painted.
    /// </summary>
    public static class RunwayDesignator
    {
        /// <summary>Degrees either side of a half that float noise in the game's transforms can
        /// carry a heading across: a runway drawn within this of 5°, 15°, 25° and so on may be
        /// called by the other number on the radio than the one painted.</summary>
        public const float BoundaryMargin = 0.05f;

        /// <summary>The bearing of a direction in map metres (x east, z north), in degrees clockwise
        /// from north, from 0 up to but not including 360.</summary>
        public static float Bearing(float dx, float dz)
        {
            float degrees = MathF.Atan2(dx, dz) * (180f / MathF.PI);
            return Normalise(degrees);
        }

        /// <summary>A bearing brought into 0 up to but not including 360.</summary>
        public static float Normalise(float bearing)
        {
            if (float.IsNaN(bearing) || float.IsInfinity(bearing)) return 0f;

            float wrapped = bearing % 360f;
            if (wrapped < 0f) wrapped += 360f;

            // -1e-6 % 360 + 360 rounds to exactly 360 in a float.
            return wrapped >= 360f ? 0f : wrapped;
        }

        /// <summary>The bearing the other way.</summary>
        public static float Reciprocal(float bearing) => Normalise(bearing + 180f);

        /// <summary>
        /// The game's number for a bearing: tenths of it rounded the way <c>Mathf.RoundToInt</c>
        /// rounds, a half to even, with 0 read as 36. From 1 to 36.
        /// </summary>
        public static int Number(float bearing)
        {
            float tenths = Normalise(bearing) * 0.1f;
            int number = (int)Math.Round(tenths);
            return number == 0 ? 36 : number;
        }

        /// <summary>A number as painted and called: two digits, "04", "18", "36".</summary>
        public static string Text(int number) => number < 10 ? "0" + number : number.ToString();

        /// <summary>The two digits for a bearing (<see cref="Number"/>).</summary>
        public static string Text(float bearing) => Text(Number(bearing));

        /// <summary>
        /// Both ends of a runway, from its start to its end in map metres: the number painted at
        /// its start (the direction from start to end) and the one at its end (the reciprocal).
        /// </summary>
        public static (string Start, string End) Ends(float startX, float startZ, float endX, float endZ)
        {
            float bearing = Bearing(endX - startX, endZ - startZ);
            return (Text(bearing), Text(Reciprocal(bearing)));
        }

        /// <summary>
        /// Whether a bearing lies so near a half (5°, 15°, ...) that the game, reading it back off
        /// transforms turned in floats, could round it the other way from the number painted:
        /// within <paramref name="margin"/> degrees of it.
        /// </summary>
        public static bool NearBoundary(float bearing, float margin = BoundaryMargin)
        {
            float tenths = Normalise(bearing) * 0.1f;
            float fraction = tenths - MathF.Floor(tenths);
            return MathF.Abs(fraction - 0.5f) * 10f <= margin;
        }
    }
}
