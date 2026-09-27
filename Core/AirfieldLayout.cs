namespace CustomMaps
{
    /// <summary>
    /// Where the pieces of an airfield go, given the ground that was levelled for it.
    ///
    /// One arithmetic, used twice: the bundle pours the paving over it, and a test checks that
    /// the apron it describes never ends up outside the flat rectangle. They have to agree — an
    /// apron running off the levelled ground onto the embankment is the sort of thing that
    /// looks deliberate in a screenshot and is not.
    ///
    /// The plugin used to stand hangars on it as well. It places no buildings any more — since
    /// 2026-09-19 a mission puts its own hangars and tower on an airfield in the game's mission
    /// editor, exactly as on a base-game one — and keeps this file only so the two copies of
    /// <c>Core</c> stay identical.
    ///
    /// Everything is measured from the runway centre in the airfield's own frame: along the
    /// runway, and across it. Nothing here knows about world coordinates or headings, which
    /// is what lets it live in <c>Core</c> and be shared.
    ///
    /// The Unity side cannot reference this assembly — the authoring project deliberately
    /// shares nothing with the plugin — so <c>AirfieldSurface.cs</c> restates these figures.
    /// The restatement says so, the same way <c>HeightFieldReader</c> does for
    /// <c>HeightField</c>.
    /// </summary>
    public static class AirfieldLayout
    {
        /// <summary>Ground left between the paving and the edge of the levelled rectangle, in
        /// metres.</summary>
        public const float Verge = 120f;

        /// <summary>Gap between the runway edge and the parallel taxiway, in metres.</summary>
        public const float TaxiwaySeparation = 110f;

        public const float TaxiwayWidth = 30f;

        /// <summary>Narrowest apron worth paving or standing anything on, in metres.</summary>
        public const float MinimumApron = 120f;

        /// <summary>Distance from the runway centreline to the middle of the taxiway.</summary>
        public static float TaxiwayOffset(float runwayWidth)
            => runwayWidth * 0.5f + TaxiwaySeparation + TaxiwayWidth * 0.5f;

        /// <summary>
        /// The band of ground outboard of the taxiway, where aprons and hangars go.
        ///
        /// Returns false when the platform is too narrow to have one, which is a real answer
        /// rather than a failure: a valley strip is a runway and a taxiway and nothing else,
        /// and squeezing an apron onto ground that was never levelled is how hangars end up
        /// half-buried in a hillside.
        /// </summary>
        public static bool ApronBand(float runwayWidth, float flatHalfAcross,
                                     out float inner, out float outer)
        {
            inner = TaxiwayOffset(runwayWidth) + TaxiwayWidth * 0.5f;
            outer = flatHalfAcross - Verge;

            return outer - inner >= MinimumApron;
        }
    }
}
