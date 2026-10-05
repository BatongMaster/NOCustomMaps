using System;

namespace CustomMaps
{
    /// <summary>
    /// What a mission chose for the flag and the capture range of an airbase built into a custom
    /// map, and what it left to the map.
    ///
    /// The precedence is the mission's own value when it set one, otherwise the map's (Map Forge's,
    /// from <c>airbases.bin</c>), otherwise the automatic one the plugin sizes; the last two are
    /// already one value by the time the game has the airbase (<c>AirbasePlacement.CaptureRangeOrDefault</c>
    /// and <c>FlagOrCentre</c>).
    ///
    /// The hard part is telling "the mission chose this" from "the game copied it". The mission
    /// editor makes an override of a built-in airbase for any change at all, a faction for one, and
    /// the override is a copy of everything the airbase had at that moment
    /// (<c>SavedAirbase.CreateOverride</c>): its <c>Center</c> is where the flag stood then and its
    /// <c>CaptureRange</c> the map's range then. Comparing with today's defaults cannot tell a copy
    /// from a choice once the map has changed: Geneva's overrides in the user's missions hold a
    /// centre 300 m from where the airfield is now, because the airfield was moved after they were
    /// made. And the save format has no room for anything else: a mission is Newtonsoft JSON of the
    /// game's own fields, and the host sends it to clients field by field over the network.
    ///
    /// So the choice is recorded in a field the game has no use for on a built-in airbase:
    /// <c>SelectionPosition</c>. Only an airbase a mission creates stands its spawn camera there
    /// (<c>Airbase.SetupCustomAirbase</c>); a built-in one keeps its own transform, and the editor
    /// shows that read-only. It is saved and sent to clients like every other field, so every
    /// machine reads the same choice from the same mission.
    ///
    ///  - Across (X and Z), it is where the map's flag stood when the mission moved it: the flag
    ///    is the mission's when <c>Center</c> is anywhere else. Every override the game made before
    ///    this, and every copy it makes now, has the two equal, so none of them moved the flag.
    ///  - Up (Y), it is level with <c>Center</c>, or <see cref="RangeMark"/> below it once the
    ///    mission has set the capture range. The two were level in every copy for the same reason.
    /// </summary>
    internal static class AirbaseChoice
    {
        /// <summary>How far across the flag has to be from where the map had it to count as moved,
        /// in metres. Well under anything a drag in the editor produces, well over a float's
        /// rounding through JSON and the network.</summary>
        public const float FlagTolerance = 1f;

        /// <summary>How far below <c>Center</c> the record sits once the mission has set the
        /// capture range, in metres.</summary>
        public const float RangeMark = 100f;

        /// <summary>Whether the mission moved the flag: its centre is somewhere other than where the
        /// record says the map had it.</summary>
        public static bool FlagChosen(float centreX, float centreZ, float recordX, float recordZ)
        {
            float dx = centreX - recordX, dz = centreZ - recordZ;
            if (!Finite(dx) || !Finite(dz)) return false;
            return dx * dx + dz * dz > FlagTolerance * FlagTolerance;
        }

        /// <summary>Whether the mission set the capture range: the record is below the centre, by
        /// about <see cref="RangeMark"/>. Anything within half of that counts as level.</summary>
        public static bool RangeChosen(float centreY, float recordY)
        {
            float drop = centreY - recordY;
            return Finite(drop) && drop > RangeMark * 0.5f;
        }

        /// <summary>
        /// The record to save for a choice: where the map's flag stands across, when the mission
        /// moved the flag, or the centre itself when it did not; and the centre's own level, less
        /// <see cref="RangeMark"/> when the mission set the range.
        ///
        /// A flag "moved" onto the map's own spot is not a choice, and reads back as none.
        /// </summary>
        public static (float X, float Y, float Z) Record(float centreX, float centreY, float centreZ,
                                                         float mapFlagX, float mapFlagZ,
                                                         bool flagChosen, bool rangeChosen)
        {
            float x = flagChosen ? mapFlagX : centreX;
            float z = flagChosen ? mapFlagZ : centreZ;
            float y = rangeChosen ? centreY - RangeMark : centreY;
            return (x, y, z);
        }

        /// <summary>The capture range the airbase gets: the mission's when it chose one the game's
        /// slider could have set, otherwise the map's.</summary>
        public static float Range(bool chosen, float missionRange, float mapRange)
        {
            if (!chosen || !Finite(missionRange)) return mapRange;
            return Math.Min(Math.Max(missionRange, AirbaseData.MinCaptureRange), AirbaseData.MaxCaptureRange);
        }

        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
    }
}
