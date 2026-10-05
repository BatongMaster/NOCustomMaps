using Xunit;

namespace CustomMaps.Tests
{
    /// <summary>
    /// A mission's flag and capture range for a custom map's airbase: the mission's when it chose
    /// them, the map's otherwise, and a copy of the map's never read as a choice.
    /// </summary>
    public class AirbaseChoiceTests
    {
        // Geneva Airport's override, as the mission editor saved it in the user's missions before
        // this: a copy of the airbase as it was, with Center and SelectionPosition equal, from
        // before the airfield was moved 300 m east to where the map has it now.
        const float OldX = -94524.9844f, OldY = 5f, OldZ = -34315.05f;
        const float MapFlagX = -94225f, MapFlagZ = -34315f;

        [Fact]
        public void AnOverrideTheEditorCopiedChoseNothing()
        {
            Assert.False(AirbaseChoice.FlagChosen(OldX, OldZ, OldX, OldZ));
            Assert.False(AirbaseChoice.RangeChosen(OldY, OldY));
        }

        [Fact]
        public void ACopiedCentreFarFromTheMapsFlagIsStillNotAChoice()
        {
            // The old centre is 300 m from the map's flag, and still the map's: nothing the mission
            // did put it there.
            (float x, float y, float z) = AirbaseChoice.Record(OldX, OldY, OldZ, MapFlagX, MapFlagZ,
                                                               flagChosen: false, rangeChosen: false);
            Assert.Equal((OldX, OldY, OldZ), (x, y, z));
            Assert.False(AirbaseChoice.FlagChosen(OldX, OldZ, x, z));
        }

        [Fact]
        public void AFlagMovedInTheEditorReadsBackAsChosen()
        {
            const float movedX = -94100f, movedY = 30f, movedZ = -34500f;
            (float x, float y, float z) = AirbaseChoice.Record(movedX, movedY, movedZ, MapFlagX, MapFlagZ,
                                                               flagChosen: true, rangeChosen: false);

            Assert.True(AirbaseChoice.FlagChosen(movedX, movedZ, x, z));
            Assert.False(AirbaseChoice.RangeChosen(movedY, y));
        }

        [Fact]
        public void AFlagMovedBackOntoTheMapsIsNotAChoice()
        {
            (float x, float _, float z) = AirbaseChoice.Record(MapFlagX + 0.3f, 30f, MapFlagZ - 0.3f, MapFlagX, MapFlagZ,
                                                               flagChosen: true, rangeChosen: false);
            Assert.False(AirbaseChoice.FlagChosen(MapFlagX + 0.3f, MapFlagZ - 0.3f, x, z));
        }

        [Fact]
        public void TheFlagStaysTheMissionsWhenTheMapMovesItsOwnLater()
        {
            // Recorded against the map's flag at the time; the map's flag moving since does not
            // change what the record says, because the record is read against the centre alone.
            const float movedX = -94100f, movedZ = -34500f;
            (float x, float _, float z) = AirbaseChoice.Record(movedX, 30f, movedZ, MapFlagX, MapFlagZ,
                                                               flagChosen: true, rangeChosen: false);
            Assert.True(AirbaseChoice.FlagChosen(movedX, movedZ, x, z));
        }

        [Fact]
        public void TheRangeAndTheFlagAreChosenSeparately()
        {
            (float x, float y, float z) = AirbaseChoice.Record(MapFlagX, 28.7f, MapFlagZ, MapFlagX, MapFlagZ,
                                                               flagChosen: false, rangeChosen: true);

            Assert.False(AirbaseChoice.FlagChosen(MapFlagX, MapFlagZ, x, z));
            Assert.True(AirbaseChoice.RangeChosen(28.7f, y));
        }

        [Fact]
        public void BothChosenSurviveTheFlagBeingDraggedUpOrDown()
        {
            (float x, float y, float z) = AirbaseChoice.Record(-94100f, 60f, -34500f, MapFlagX, MapFlagZ,
                                                               flagChosen: true, rangeChosen: true);

            Assert.True(AirbaseChoice.FlagChosen(-94100f, -34500f, x, z));
            Assert.True(AirbaseChoice.RangeChosen(60f, y));
        }

        [Fact]
        public void TheMissionsRangeWinsOnlyWhenItChoseOne()
        {
            Assert.Equal(2500f, AirbaseChoice.Range(chosen: true, missionRange: 2500f, mapRange: 1440f));
            Assert.Equal(1440f, AirbaseChoice.Range(chosen: false, missionRange: 2500f, mapRange: 1440f));
        }

        [Fact]
        public void AMissionsRangeIsKeptToTheGamesSlider()
        {
            Assert.Equal(AirbaseData.MinCaptureRange, AirbaseChoice.Range(true, 0f, 1440f));
            Assert.Equal(AirbaseData.MaxCaptureRange, AirbaseChoice.Range(true, 50000f, 1440f));
            Assert.Equal(1440f, AirbaseChoice.Range(true, float.NaN, 1440f));
        }

        [Fact]
        public void ARecordThatIsNotANumberChoosesNothing()
        {
            Assert.False(AirbaseChoice.FlagChosen(OldX, OldZ, float.NaN, OldZ));
            Assert.False(AirbaseChoice.RangeChosen(OldY, float.NaN));
        }
    }
}
