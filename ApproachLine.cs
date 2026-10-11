namespace CustomMaps
{
    /// <summary>
    /// How far from an airbase the HUD starts drawing its landing glideslope, the line from the runway
    /// toward the aircraft, so that on a custom map the line is ten nautical miles long when it first
    /// shows. Pure arithmetic, so it is tested; the patch that uses it is in
    /// <c>Patches/ApproachLinePatches.cs</c>.
    ///
    /// From the decompiled game (Assembly-CSharp of 14 August 2026), the line's length has no setting of
    /// its own. <c>AirbaseOverlay.DrawGlideslope</c>, every frame while the gear is down, draws it from the
    /// runway's touchdown point to
    ///
    ///     runwayUsage.GetGlideslopeAimpoint(aircraft, num * 0.9f, ...)
    ///
    /// <c>num</c> being the aircraft's distance to that point, so the line reaches nine tenths of the way
    /// back to the aircraft and shortens as it closes. It is drawn only while the overlay holds a runway,
    /// and <c>AirbaseOverlay.UpdateNearestAirbase</c>, every 2 s, asks the nearest suitable airbase for one
    /// only while
    ///
    ///     FastMath.InRange(nearestAirbase.center.position, aircraft.transform.position,
    ///                      nearestAirbase.GetRadius() + 5000f)
    ///
    /// and drops it otherwise. <c>GetRadius</c> is the airbase's capture range. So the longest line the
    /// game draws is nine tenths of the distance to touchdown as the aircraft crosses capture range plus
    /// 5 km round the flag: 2.5 to 3.8 nm at the base maps' airbases, and 2.6 to 3.5 nm at Swiss Alps'.
    ///
    /// The patch replaces that 5,000 m, for a custom map's airbases, with <see cref="Extra"/>.
    /// </summary>
    internal static class ApproachLine
    {
        /// <summary>The <c>5000f</c> in <c>AirbaseOverlay.UpdateNearestAirbase</c>: how far past its capture
        /// range an airbase offers the HUD a runway. What every airbase the patch leaves alone keeps.</summary>
        public const float GameExtra = 5000f;

        /// <summary>The line's length when it first shows: ten nautical miles.</summary>
        public const float LineLength = 10f * 1852f;

        /// <summary>The <c>num * 0.9f</c> in <c>AirbaseOverlay.DrawGlideslope</c>: the share of the distance
        /// to touchdown the line covers.</summary>
        public const float AimFraction = 0.9f;

        /// <summary>
        /// Room for the 2 s between two of the overlay's updates (<c>AirbaseOverlay.OnEnable</c>:
        /// <c>StartSlowUpdateDelayed(2f, UpdateNearestAirbase)</c>). An aircraft can be that long inside
        /// the distance before the runway is asked for, and the line is drawn from where it is by then:
        /// 500 m is 2 s at 250 m/s, well above any approach speed.
        /// </summary>
        public const float UpdateMargin = 500f;

        /// <summary>
        /// What the patch adds to the airbase's capture range in place of <see cref="GameExtra"/>, so that
        /// an aircraft crossing it is at least <see cref="LineLength"/> / <see cref="AimFraction"/> (plus
        /// <see cref="UpdateMargin"/>) from any runway's touchdown point, and the line it is shown is at
        /// least ten miles long. Never less than the game's own 5,000 m.
        ///
        /// <paramref name="farthestRunwayEnd"/> is the distance from the airbase's flag (<c>center</c>) to
        /// the farthest end of any of its runways. A touchdown point lies on its runway, between the two
        /// ends, so it is no farther from the flag than that, and an aircraft capture range plus the extra
        /// from the flag (the game's test is a sphere) is at least that much less this from the point.
        /// </summary>
        public static float Extra(float captureRange, float farthestRunwayEnd)
        {
            float gate = farthestRunwayEnd + LineLength / AimFraction + UpdateMargin;
            float extra = gate - captureRange;

            // Written so that a NaN from either input leaves the game's own value.
            return extra > GameExtra ? extra : GameExtra;
        }

        /// <summary>
        /// The fingerprint (<see cref="IlFingerprint"/>) of the game methods in <see cref="GameMethods"/>,
        /// in the build the patch was checked against. The patch is applied only while the running game's
        /// fingerprint is this one, so a game update that changes how the HUD picks its runway or draws
        /// the line turns the patch off rather than editing a method it no longer understands.
        /// </summary>
        public const ulong VerifiedGameMethods = 0xD8D87C98C2D65919UL;

        /// <summary>
        /// What <see cref="VerifiedGameMethods"/> covers, each a method of <c>AirbaseOverlay</c> (no
        /// namespace) and <see cref="IlFingerprint.OfMethod"/> combined in this order: the method the patch
        /// edits, with its <c>5000f</c>, and the one whose <c>0.9f</c> <see cref="AimFraction"/> copies.
        /// </summary>
        public static readonly string[] GameMethods = { "UpdateNearestAirbase", "DrawGlideslope" };
    }
}
