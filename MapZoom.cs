namespace CustomMaps
{
    /// <summary>
    /// How far out the M map may zoom on a custom map, and how far it may then be panned. Pure
    /// arithmetic, so it is tested; the patches that use it are in <c>Patches/MapZoomPatches.cs</c>
    /// and <c>Patches/MapPanPatches.cs</c>.
    ///
    /// From the decompiled game, <c>DynamicMap.MapControls</c> zooms by scaling the map image:
    ///
    ///     x = mapScaleCenter.transform.localScale.x;
    ///     x *= player.GetAxis("Zoom View") * 0.05f + 1f;
    ///     x = Mathf.Clamp(x, 1f, 40f);
    ///     SetZoomLevel(x);
    ///
    /// and <c>SetZoomLevel</c> leaves the image's scale, inside the map's frame, equal to that
    /// zoom. The image is <c>MapSize / 81920 * 900</c> units across (<c>DynamicMap.LoadMapImage</c>)
    /// and the frame, <c>mapBackground</c>, <c>mapScaleMaximized</c> plus 20 (<c>Maximize</c>).
    /// <c>mapScaleMaximized</c> is set in the scene, but the icons, placed at
    /// <c>mapScaleMaximized / mapDimension</c> units a metre on the same image, say it is 900, so
    /// the frame is 920. At zoom 1 a shipped 81,920 m map just fits it, which is why the game
    /// stops there. Swiss Alps, 199,680 m, is 2,194 units across, and at zoom 1 the frame shows
    /// less than half of it either way. The patch reads the frame's size live rather than
    /// trusting the 920.
    ///
    /// On a custom map the zoom may go down to the scale at which the whole map fills
    /// <see cref="FitShare"/> of the frame, never above the game's own 1. Every base map keeps
    /// exactly the game's 1 to 40.
    /// </summary>
    internal static class MapZoom
    {
        /// <summary>The game's least zoom, from <c>Mathf.Clamp(x, 1f, 40f)</c>.</summary>
        public const float GameFloor = 1f;

        /// <summary>
        /// The share of the frame a whole custom map fills when zoomed fully out. The 5% left on
        /// each side keeps the map's edge clear of the frame's border and of the grid letters and
        /// numbers, which the game stands 440 units from the frame's centre (<c>GridLabels</c>), just
        /// inside its 460.
        /// </summary>
        public const float FitShare = 0.9f;

        /// <summary>A floor under the floor, for a map twenty times a shipped one's width, where the
        /// map would be a speck: well below anything a real map reaches (Swiss Alps: 0.377).</summary>
        public const float Lowest = 0.05f;

        /// <summary>
        /// The least zoom the M map may reach. <paramref name="frame"/> is the frame's smaller
        /// side and <paramref name="imageWidth"/> and <paramref name="imageHeight"/> the map image's
        /// unscaled size, all in the frame's units. Anything but a custom map, or a size that
        /// cannot be read, gets the game's own floor.
        /// </summary>
        public static float Floor(bool customMap, float frame, float imageWidth, float imageHeight)
        {
            if (!customMap) return GameFloor;

            float image = imageWidth > imageHeight ? imageWidth : imageHeight;
            if (!(frame > 0f) || !(image > 0f) || float.IsInfinity(frame) || float.IsInfinity(image))
                return GameFloor;

            float fit = FitShare * frame / image;
            if (!(fit < GameFloor)) return GameFloor;
            return fit > Lowest ? fit : Lowest;
        }

        /// <summary>
        /// The pan limit's factor at <paramref name="zoom"/>, from the factor the game (and the pan
        /// widening for large maps) would use, <paramref name="factor"/>.
        ///
        /// Above the game's floor of 1 it is left alone, so zoomed in a custom map pans as before
        /// and a base map, which never zooms below 1, is never touched. Below 1 it shrinks with the
        /// zoom, in step with how much of the map the frame shows (1 / zoom), and reaches 0 at
        /// <paramref name="floor"/>: fully zoomed out the map is centred and shows whole, wherever
        /// the view was, rather than mostly off one side of the frame.
        /// </summary>
        public static float PanFactor(float factor, float zoom, float floor)
        {
            if (!(floor > 0f) || !(floor < GameFloor) || !(zoom < GameFloor)) return factor;

            float share = (1f / floor - 1f / zoom) / (1f / floor - 1f / GameFloor);
            if (!(share > 0f)) return 0f;
            return share < 1f ? factor * share : factor;
        }

        /// <summary>Unity's <c>Vector2 ==</c> counts two points as one when the square of the
        /// distance between them is under this, and the game's map follows the camera while its
        /// pan offset <c>== Vector2.zero</c>.</summary>
        const float SameSquared = 9.99999944E-11f;

        /// <summary>
        /// Whether the map should go on following the camera after a narrowed pan limit
        /// (<see cref="PanFactor"/>) has held the view in.
        ///
        /// <c>DynamicMap.MapControls</c> follows the camera while its pan offset is zero, clamps
        /// the view, then writes back as the offset however far the clamp moved it. A clamp that
        /// bites so turns following off until the map is next opened. The game's own limit never
        /// bites on a view that follows an aircraft over the map, but the narrowed one bites almost
        /// always, so every zoom right out would leave the view parked at the middle of the map
        /// when zoomed back in. So when the offset was zero (<paramref name="offsetX"/>,
        /// <paramref name="offsetY"/>: the map was following, and nobody dragged it this frame) and
        /// the narrowing is what holds the view (<paramref name="narrowed"/> below the
        /// <paramref name="factor"/> it would otherwise be), the offset is put back to zero: the
        /// narrowing then only moves where the map is drawn. Dragging, or a view the game's own
        /// limit holds, stays the game's.
        /// </summary>
        public static bool KeepsFollowing(float factor, float narrowed, float offsetX, float offsetY)
        {
            if (!(narrowed < factor)) return false;
            return offsetX * offsetX + offsetY * offsetY < SameSquared;
        }
    }
}
