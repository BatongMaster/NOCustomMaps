using System.Globalization;
using NuclearOption.MissionEditorScripts;
using NuclearOption.SavedMission;
using UnityEngine;

namespace CustomMaps
{
    /// <summary>
    /// Marks an airbase the plugin built for a custom map, so that what a mission may change about
    /// it is changed for these and for no other airbase. Added to the clone in
    /// <see cref="AirbaseBuilder"/>, before the map is instantiated, so every instance has it.
    /// </summary>
    internal sealed class CustomMapAirbase : MonoBehaviour
    {
        /// <summary>Where the flag stands, for the mission editor: what the airbase panel's green
        /// handle and Center field and the move arrows on the selected airbase all work on, made
        /// the first time one of them is needed (<see cref="MissionAirbases.FlagWrapper"/>).</summary>
        internal ValueWrapperGlobalPosition Flag;

        /// <summary>The airbase panel last set up for this airbase, whose override a move makes
        /// while it is open.</summary>
        internal AirbasePanel Panel;
    }

    /// <summary>
    /// Lets a mission move a custom map's airbase flag and set its capture range, and has the
    /// mission's choice win over the map's.
    ///
    /// The game treats the plugin's airbases as built into the map (<c>MapLoader.GetObjectType</c>
    /// says <c>MapObject</c> for anything under a <c>NetworkMap</c>), and for a built-in airbase it
    /// has the map win: <c>Airbase.LinkSavedAirbase</c> copies the map's capture range over the
    /// mission's, a mission's <c>Center</c> is never applied (the link only registers a callback for
    /// a later change), and the editor greys out the range slider and offers no flag handle. That
    /// is right for a base-game map, whose airbases are finished; here the map is the user's own,
    /// and a mission is where a scenario decides which ground a base holds.
    ///
    /// So, for these airbases only: the editor gets the flag handle and the slider a mission-made
    /// airbase has, the airbase itself, selected by clicking its flag, gets the move arrows (the
    /// game gives no airbase any, a mission-made one included: <c>AirbaseSelectionDetails</c> allows
    /// no position handle, so a selected flag stood still however it was dragged), and each time the game links an airbase to a mission (loading it in the editor,
    /// starting it in single player, and on the host and every client in multiplayer, since
    /// <c>Mission.SetupAirbase</c> runs on each) the mission's flag and range are applied where it
    /// chose them and the map's everywhere else. <see cref="AirbaseChoice"/> is how a choice is
    /// told from a copy.
    ///
    /// Everything that reads the flag or the range then agrees, because they all read the same two
    /// things at the time: the capture zone (<c>Capture</c>: <c>center.position</c> and
    /// <c>SavedAirbase.CaptureRange</c>), the map icon, where an AI flies home to, the objective
    /// marker (<c>CaptureAirbaseObjective</c> reads the mission's <c>SavedAirbase.Center</c>, which is
    /// set to the flag either way), and the editor's flag and decal, which hang under the centre.
    /// </summary>
    internal static class MissionAirbases
    {
        public static bool IsOurs(Airbase airbase) =>
            airbase != null && !airbase.AttachedAirbase && airbase.GetComponent<CustomMapAirbase>() != null;

        /// <summary>
        /// Before the game links an airbase to its settings: makes sure a copy of the map's own
        /// reads as no choice, and that an override has a centre of its own.
        ///
        /// The map's settings are what the editor copies an override from, and their
        /// <c>SelectionPosition</c> is the spawn camera's spot, the runway centre, which a copy would
        /// read as "the flag was moved". Levelling it with <c>Center</c> is harmless because nothing
        /// reads it for a built-in airbase.
        ///
        /// <c>SavedAirbase.CreateOverride</c> also hands the override the map's own
        /// <c>CenterWrapper</c>, whose callback writes the <em>map's</em> <c>Center</c> and not the
        /// override's. Moving the flag through it would move the map's default and save nothing in
        /// the mission, so an override that shares it gets its own, as one loaded from a file has.
        /// </summary>
        public static void BeforeLink(Airbase airbase, SavedAirbase saved)
        {
            SavedAirbase map = airbase.airbaseSettings;
            map.SelectionPosition = map.Center;

            if (saved == null || saved == map || saved.CenterWrapper != map.CenterWrapper) return;

            var own = new ValueWrapperGlobalPosition(saved.Center);
            own.RegisterOnChange(saved, v => saved.Center = v);
            saved.CenterWrapper = own;
        }

        /// <summary>
        /// After the game has linked an airbase to a mission's override, or back to the map's own
        /// settings: puts the flag and the range where the mission chose them, or where the map has
        /// them.
        ///
        /// <paramref name="missionRange"/> is the override's range from before the link, which the
        /// game has just overwritten with the map's. An override's <c>Center</c> is set to the map's
        /// flag when the mission did not move it, so that the objective marker, which reads it, is
        /// on the flag, and so that a later save carries the flag the map has now (the record says
        /// "not chosen" either way, so a flag moved later in Map Forge still shows).
        /// </summary>
        public static void AfterLink(Airbase airbase, SavedAirbase saved, float missionRange)
        {
            SavedAirbase map = airbase.airbaseSettings;
            GlobalPosition flag = map.Center;
            float range = map.CaptureRange;

            if (saved != null && saved != map)
            {
                bool flagChosen = AirbaseChoice.FlagChosen(saved.Center.x, saved.Center.z,
                                                           saved.SelectionPosition.x, saved.SelectionPosition.z);
                bool rangeChosen = AirbaseChoice.RangeChosen(saved.Center.y, saved.SelectionPosition.y);

                if (flagChosen) flag = saved.Center;
                else saved.Center = map.Center;

                range = AirbaseChoice.Range(rangeChosen, missionRange, map.CaptureRange);
                saved.CaptureRange = range;

                Record(saved, map, flagChosen, rangeChosen);

                // Invariant, so the log reads the same on a Swiss or French locale.
                CultureInfo inv = CultureInfo.InvariantCulture;
                Plugin.LogDebug($"airbase '{saved.UniqueName}': flag " +
                                (flagChosen ? $"at ({flag.x.ToString("F0", inv)}, {flag.z.ToString("F0", inv)}) from the mission"
                                            : "where the map has it") +
                                $", capture radius {range.ToString("F0", inv)} m " +
                                (rangeChosen ? "from the mission" : "from the map"));
            }

            Place(airbase, saved ?? map, flag, range);
        }

        /// <summary>After the game has let go of a mission's override ("Remove overrides", or the
        /// editor clearing the mission): the flag and the range go back to the map's.</summary>
        public static void AfterUnlink(Airbase airbase)
        {
            SavedAirbase map = airbase.airbaseSettings;
            map.SelectionPosition = map.Center;
            Place(airbase, map, map.Center, map.CaptureRange);
        }

        /// <summary>
        /// Gives the editor's airbase panel what it has for an airbase a mission made: the capture
        /// range slider, and a handle and a field for the centre. The game builds the panel for a
        /// built-in airbase with both switched off (<c>AirbasePanel.Setup</c>), and this switches
        /// them back on afterwards, the same way it would have.
        ///
        /// The handle works on the airbase's own flag position (<see cref="FlagWrapper"/>) rather
        /// than on its <c>CenterWrapper</c>, which until the first change is the map's: a move first
        /// has the panel make the override, exactly as a change of faction does, and then moves that.
        /// </summary>
        public static void SetUpPanel(AirbasePanel panel, Airbase airbase)
        {
            // Whatever the panel is about to copy an override from records no choice (BeforeLink).
            SavedAirbase map = airbase.airbaseSettings;
            map.SelectionPosition = map.Center;

            panel.captureRangeSlider.interactable = true;
            panel.captureRangeSliderLabel.color = panel.captureRangeSliderLabelNormalColor;
            panel.captureRangeSliderFill.color = panel.captureRangeSliderFillNormalColor;

            CustomMapAirbase ours = airbase.GetComponent<CustomMapAirbase>();
            ours.Panel = panel;
            ValueWrapperGlobalPosition centre = FlagWrapper(airbase);

            panel.centerHandle = Object.Instantiate(panel.positionHandlePrefab);
            panel.centerHandle.SetHue(Color.green);
            panel.centerHandle.Setup(centre, () => "Center " + airbase.SavedAirbase.DisplayName, null);
            panel.centerPositionField.Setup("Center", centre);
        }

        /// <summary>
        /// The flag's position as the mission editor moves it, one per airbase: the panel's handle and
        /// Center field and the move arrows on the selected airbase
        /// (<see cref="Patches.MissionAirbaseSelectionPatch"/>) all work on it, so that each follows
        /// the others. It is brought to where the flag stands each time it is handed out and each time
        /// the flag is put somewhere (<see cref="Place"/>); a change from anything else moves the flag.
        /// Null for any airbase but ours.
        /// </summary>
        public static ValueWrapperGlobalPosition FlagWrapper(Airbase airbase)
        {
            if (!IsOurs(airbase) || airbase.center == null) return null;

            CustomMapAirbase ours = airbase.GetComponent<CustomMapAirbase>();
            if (ours.Flag == null)
            {
                ours.Flag = new ValueWrapperGlobalPosition(airbase.center.GlobalPosition());
                ours.Flag.RegisterOnChange(ours, v => Patches.MissionAirbaseLinkPatch.Try(() => MoveFlag(airbase, v)));
            }
            else ours.Flag.SetValue(airbase.center.GlobalPosition(), ours);

            return ours.Flag;
        }

        /// <summary>The flag dragged, or typed in, in the editor: into the mission's override, made
        /// now if there is none yet, and onto the map at once.</summary>
        static void MoveFlag(Airbase airbase, GlobalPosition position)
        {
            if (airbase == null) return;
            MakeOverride(airbase);

            SavedAirbase saved = airbase.SavedAirbase;
            SavedAirbase map = airbase.airbaseSettings;
            if (saved == null || saved == map) return;

            bool rangeChosen = AirbaseChoice.RangeChosen(saved.Center.y, saved.SelectionPosition.y);
            saved.Center = position;
            Record(saved, map, flagChosen: true, rangeChosen);

            Place(airbase, saved, position, saved.CaptureRange);
        }

        /// <summary>
        /// Makes the mission's override of the airbase when it has none, as the game's airbase panel
        /// does for any change (<c>AirbasePanel.CreateOverride</c>): through the panel while it is
        /// open on this airbase, so that it shows the override, and the same way here otherwise.
        /// </summary>
        static void MakeOverride(Airbase airbase)
        {
            if (airbase.SavedAirbaseOverride) return;

            AirbasePanel panel = airbase.GetComponent<CustomMapAirbase>().Panel;
            if (panel != null && panel.airbase == airbase)
            {
                panel.CheckOverride();
                return;
            }

            Mission mission = MissionManager.CurrentMission;
            if (mission == null) return;

            string name = airbase.SavedAirbase.UniqueName;
            SavedAirbase saved = mission.airbases.Find(x => x.UniqueName == name);
            if (saved == null)
            {
                saved = SavedAirbase.CreateOverride(airbase.SavedAirbase);
                saved.SavedInMission = true;
                mission.airbases.Add(saved);
            }

            SavedAirbase old = airbase.SavedAirbase;
            airbase.LinkSavedAirbase(saved, customAirbase: false);
            mission.ReferenceReplaced(old, saved);
        }

        /// <summary>The capture range slider moved in the editor. The panel has already made the
        /// override, set its range and resized the decal; this records that the mission chose it.</summary>
        public static void RangeSet(AirbasePanel panel)
        {
            Airbase airbase = panel.airbase;
            SavedAirbase saved = panel.savedAirbase;
            if (!IsOurs(airbase) || saved == null || saved == airbase.airbaseSettings) return;

            bool flagChosen = AirbaseChoice.FlagChosen(saved.Center.x, saved.Center.z,
                                                       saved.SelectionPosition.x, saved.SelectionPosition.z);
            Record(saved, airbase.airbaseSettings, flagChosen, rangeChosen: true);
        }

        static void Record(SavedAirbase saved, SavedAirbase map, bool flagChosen, bool rangeChosen)
        {
            (float x, float y, float z) = AirbaseChoice.Record(saved.Center.x, saved.Center.y, saved.Center.z,
                                                               map.Center.x, map.Center.z, flagChosen, rangeChosen);
            saved.SelectionPosition = new GlobalPosition(x, y, z);
        }

        /// <summary>
        /// Stands the flag and sizes the circle: the centre transform, which the capture zone, the
        /// icon and the editor's flag and decal all go by, the settings' own centre, and the decal.
        /// The transform is set every time, not only when the mission moved it, because it outlives
        /// the mission: the editor loads one mission after another onto the same airbase.
        /// </summary>
        static void Place(Airbase airbase, SavedAirbase saved, GlobalPosition flag, float range)
        {
            // From the airbase itself, so the game's own callback on it does not move the centre a
            // second time with the donor's children in tow.
            saved.CenterWrapper?.SetValue(flag, airbase);

            Vector3 position = flag.ToLocalPosition();
            if (airbase.center != null && (airbase.center.position - position).sqrMagnitude > 0.0001f)
            {
                AirbaseBuilder.MoveCentre(airbase, position);

                // On the server, the battlefield squares the base holds are taken round the centre a
                // second into the mission (Airbase.Update); taken again, round this one.
                airbase.gridSquares = null;
            }

            AirbaseEditorRadius radius = AirbaseEditorRadius.Find(airbase.center);
            if (radius != null) radius.Setup(airbase.CurrentHQ.GetColorOrGray(), range);

            // The editor's handle, field and arrows follow, without this moving the flag again.
            CustomMapAirbase ours = airbase.GetComponent<CustomMapAirbase>();
            if (ours != null && ours.Flag != null) ours.Flag.SetValue(flag, ours);
        }
    }
}
