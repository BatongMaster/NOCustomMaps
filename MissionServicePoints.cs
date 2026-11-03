using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using NuclearOption.MissionEditorScripts;
using NuclearOption.SavedMission;
using NuclearOption.SavedMission.Objectives;
using UnityEngine;

namespace CustomMaps
{
    /// <summary>
    /// Lets a mission say where a custom map's airbase has its service points, the places an AI aircraft
    /// taxis to after landing (<c>Airbase.TryGetNearestServicePoint</c>).
    ///
    /// The mission format already has them, <c>SavedAirbase.ServicePoints</c>, but the game reads them only
    /// for an airbase the mission made (<c>Airbase.SetupCustomAirbase</c>), and draws their list in the
    /// airbase panel only for those. <c>SavedAirbase.CreateOverride</c> copies none, so an override with
    /// none follows the map, and that is how a mission says so: any at all replace the map's, all of them.
    /// </summary>
    internal static class MissionServicePoints
    {
        static readonly FieldInfo PointsField =
            typeof(Airbase).GetField(AirbaseBuilder.ServicePointsField, BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>The list drawn in each panel, which the game's <c>AirbasePanel.Cleanup</c> leaves
        /// behind as it does not expect to draw one again.</summary>
        static readonly ConditionalWeakTable<AirbasePanel, EmptyDataList> Lists = new ConditionalWeakTable<AirbasePanel, EmptyDataList>();

        /// <summary>Puts the mission's service points in use, or the map's if it has none.</summary>
        public static void AfterLink(Airbase airbase, SavedAirbase saved)
        {
            if (PointsField == null) return;

            CustomMapAirbase ours = airbase.GetComponent<CustomMapAirbase>();
            if (ours.MapServicePoints == null) ours.MapServicePoints = PointsField.GetValue(airbase) as Transform[];
            RemoveMissionPoints(ours);

            bool mission = saved != null && saved != airbase.airbaseSettings && saved.ServicePoints != null && saved.ServicePoints.Count > 0;
            if (!mission)
            {
                PointsField.SetValue(airbase, ours.MapServicePoints);
                return;
            }

            var points = new Transform[saved.ServicePoints.Count];
            for (int i = 0; i < points.Length; i++)
            {
                points[i] = new GameObject($"Mission service point {i + 1}").transform;
                points[i].SetParent(airbase.transform, worldPositionStays: false);
                points[i].position = saved.ServicePoints[i].ToLocalPosition();
            }

            ours.MissionServicePoints = points;
            PointsField.SetValue(airbase, points);
            Plugin.LogInfo($"airbase '{saved.UniqueName}': the mission's {points.Length} service point(s) replace the map's");
        }

        public static void AfterUnlink(Airbase airbase)
        {
            if (PointsField == null) return;

            CustomMapAirbase ours = airbase.GetComponent<CustomMapAirbase>();
            RemoveMissionPoints(ours);
            if (ours.MapServicePoints != null) PointsField.SetValue(airbase, ours.MapServicePoints);
        }

        static void RemoveMissionPoints(CustomMapAirbase ours)
        {
            if (ours.MissionServicePoints == null) return;

            foreach (Transform point in ours.MissionServicePoints)
                if (point != null) Object.Destroy(point.gameObject);
            ours.MissionServicePoints = null;
        }

        /// <summary>
        /// Draws the game's own Service Points list and handles in the airbase panel, as
        /// <c>AirbasePanel.SetupServicePointsList</c> does for an airbase a mission made.
        ///
        /// A mission with none of its own is shown the map's, on copies that follow nothing: an airbase
        /// merely looked at stays as it was, and the mission goes on following the map if the map's move.
        /// The first change to the list (moving a point, adding one, removing one) makes the override if
        /// there is none and hands it the list, so from there the mission has its own, all of them. Removing
        /// every point goes back to the map's.
        /// </summary>
        public static void SetUpPanel(AirbasePanel panel, Airbase airbase)
        {
            if (panel.dataDrawer == null) return;

            if (Lists.TryGetValue(panel, out EmptyDataList old))
            {
                Lists.Remove(panel);
                if (old != null) Object.Destroy(old.gameObject);
            }

            bool own = airbase.SavedAirbaseOverride && panel.savedAirbase.ServicePointsWrappers.Count > 0;
            List<ValueWrapperGlobalPosition> wrappers = own ? panel.savedAirbase.ServicePointsWrappers : CopiesOfMap(airbase);

            void Adopt()
            {
                if (own) return;
                own = true;

                if (!airbase.SavedAirbaseOverride) panel.CheckOverride();
                panel.savedAirbase.ServicePointsWrappers = wrappers;
            }

            void AddHandle(int index, ValueWrapperGlobalPosition wrapper)
            {
                PositionHandle handle = Object.Instantiate(panel.positionHandlePrefab);
                handle.SetHue(new Color(1f, 0.3f, 0.08f));
                handle.Setup(wrapper, () => $"Service point {index + 1} " + airbase.SavedAirbase.DisplayName, null);
                panel.servicePointsHandles[wrapper] = handle;
            }

            ValueWrapperGlobalPosition Create()
            {
                Adopt();

                var wrapper = new ValueWrapperGlobalPosition(StartingPlace(airbase, panel.savedAirbase, wrappers));
                AddHandle(wrappers.Count, wrapper);
                return wrapper;
            }

            void Delete(ValueWrapperGlobalPosition wrapper)
            {
                Adopt();
                if (!panel.servicePointsHandles.TryGetValue(wrapper, out PositionHandle handle)) return;

                SceneSingleton<UnitSelection>.i.ClearIfSelected(handle);
                panel.servicePointsHandles.Remove(wrapper);
                Object.Destroy(handle.gameObject);
            }

            EmptyDataList list = panel.dataDrawer.DrawList(300, wrappers, AirbasePanel.DrawVector3ListContent, Create, Delete);
            list.AllowSwapItems = false;
            list.TitleText.text = "Service Points";
            list.RefreshList();
            for (int i = 0; i < wrappers.Count; i++)
            {
                AddHandle(i, wrappers[i]);
                if (!own) wrappers[i].RegisterOnChange(panel, (GlobalPosition _) => Adopt());
            }

            Lists.Add(panel, list);
        }

        static Transform[] MapPoints(Airbase airbase) =>
            airbase.GetComponent<CustomMapAirbase>().MapServicePoints ?? PointsField?.GetValue(airbase) as Transform[];

        static List<ValueWrapperGlobalPosition> CopiesOfMap(Airbase airbase)
        {
            var copies = new List<ValueWrapperGlobalPosition>();
            Transform[] map = MapPoints(airbase);
            if (map == null) return copies;

            foreach (Transform point in map)
                if (point != null) copies.Add(new ValueWrapperGlobalPosition(point.position.ToGlobalPosition()));
            return copies;
        }

        /// <summary>A new point starts beside the list's last, not on it, where its handle could not be
        /// told from the other's; with none left, on the map's first.</summary>
        static GlobalPosition StartingPlace(Airbase airbase, SavedAirbase saved, List<ValueWrapperGlobalPosition> wrappers)
        {
            if (wrappers.Count > 0) return wrappers[wrappers.Count - 1].Value + Vector3.right * 30f;

            Transform[] map = MapPoints(airbase);
            if (map != null && map.Length > 0 && map[0] != null) return map[0].position.ToGlobalPosition();
            return saved.CenterWrapper.Value + Vector3.up * 5f;
        }
    }
}
