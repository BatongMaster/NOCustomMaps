using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using NuclearOption.ModScripts;
using NuclearOption.ModScripts.Impl;
using NuclearOption.SavedMission;
using NuclearOption.SceneLoading;
using NuclearOption.Workshop;
using UnityEngine;

namespace CustomMaps.Patches
{
    /// <summary>
    /// The map tag of a mission going up to the workshop. The game works it out afresh from the
    /// mission's file at every upload and every update, and replaces the item's tags with the result
    /// (<c>SteamWorkshop.UpdateItem</c> calls <c>SetItemTags</c> each time), so what is decided here
    /// reaches a mission already published the next time it is updated.
    ///
    /// A mission on a custom map is tagged with the map's display name (<see cref="WorkshopTags"/>),
    /// whichever build its <c>MapKey.Path</c> names. The game's own answer needs that exact build
    /// installed, and refuses the upload without it.
    ///
    /// Anything else goes to the game's method, which needs a map loader. Updating a mission from its
    /// workshop page (<c>ItemDetailsWorkshopPanel.SubmitUpdate</c>) comes here before the upload panel
    /// has ever opened that session, and only the upload panel's <c>Awake</c> gives
    /// <c>MissionMod</c> its <c>MapLoader</c>: any mission on a map other than the default one threw a
    /// NullReferenceException there (game 0.34.5). The map loader is handed over first, with the
    /// custom maps in it.
    /// </summary>
    [HarmonyPatch(typeof(MissionMod), "TryGetMapTag")]
    internal static class MissionMapTagPatch
    {
        /// <summary>Set when the mission just asked about is on a custom map, for
        /// <see cref="MissionItemTagsPatch"/>: the game asks from inside <c>TryGetItem</c>.</summary>
        internal static bool OnCustomMap;

        static bool Prefix(MissionMod __instance, MapKey mapKey, ref string tag, ref string error, ref bool __result)
        {
            if (__instance.MapLoader == null)
                __instance.MapLoader = Resources.FindObjectsOfTypeAll<MapLoader>().FirstOrDefault();
            if (__instance.MapLoader != null) MapRegistrar.Ensure(__instance.MapLoader);

            if (!MapIdentity.TryParse(mapKey.Path, out _, out _)) return true;

            var maps = new List<KeyValuePair<string, string>>();
            foreach (LoadedMap map in BundleLoader.Maps)
                if (map.Details != null)
                    maps.Add(new KeyValuePair<string, string>(map.Details.PrefabName, map.Details.WorkshopTag));

            tag = WorkshopTags.FindMapTag(mapKey.Path, maps, out bool exactBuild);
            if (tag == null)
            {
                error = $"No build of the map {mapKey.Path} is installed, so its name for the workshop tag is not known. " +
                        "Install the map to upload this mission.";
                __result = false;
                return false;
            }

            // Not an error: a mission may name a build its maker does not have. It is said in the log
            // because it is also what a mistyped hash looks like.
            if (!exactBuild)
                Plugin.LogWarning($"workshop: the mission's map {mapKey.Path} is not installed here; another build gives its tag \"{tag}\"");

            OnCustomMap = true;
            error = null;
            __result = true;
            return false;
        }
    }

    /// <summary>
    /// The custom maps go into the map loader before the mission picker reads it. Its panel lists the
    /// loader's maps as filters when it wakes (<c>MissionSelectPanel.Awake</c>) and tags each mission's row
    /// from them, which at the first opening after a start came before anything had registered the custom
    /// maps, so neither showed until a mission had been entered.
    /// </summary>
    [HarmonyPatch(typeof(MissionTag), nameof(MissionTag.SetupMapTags))]
    internal static class MissionPickerTagsPatch
    {
        static void Prefix(MapLoader mapLoader) => MapRegistrar.Ensure(mapLoader);
    }

    /// <summary>Gives a mission on a custom map its fixed tags as the game builds its upload item.</summary>
    [HarmonyPatch(typeof(MissionMod), nameof(MissionMod.TryGetItem))]
    internal static class MissionItemTagsPatch
    {
        static void Prefix() => MissionMapTagPatch.OnCustomMap = false;

        static void Postfix(ref WorkshopUploadItem item, bool __result)
        {
            bool onCustomMap = MissionMapTagPatch.OnCustomMap;
            MissionMapTagPatch.OnCustomMap = false;
            if (!__result || !onCustomMap || item == null) return;

            // The constructor copies the tags it is given into a list of its own.
            if (item.AutoTags is List<string> autoTags)
                WorkshopTags.AddPluginTag(autoTags, item.CustomTags);
            item.ModRequired = true;
        }
    }

    /// <summary>"Mod Required" stays on for those missions at the moment the tags are read for Steam,
    /// whatever became of the toggle.</summary>
    [HarmonyPatch(typeof(WorkshopUploadItem), nameof(WorkshopUploadItem.GetAllTags))]
    internal static class UploadItemModRequiredPatch
    {
        static void Prefix(WorkshopUploadItem __instance)
        {
            if (WorkshopTags.HasPluginTag(__instance.AutoTags)) __instance.ModRequired = true;
        }
    }

    /// <summary>The panel's "Mod Required" toggle cannot be turned off for those missions. The panel is
    /// reused, so every other item gets it back.</summary>
    [HarmonyPatch(typeof(UploadWorkshopPanel.Page3), "SetupTags")]
    internal static class ModRequiredTogglePatch
    {
        static void Postfix(UploadWorkshopPanel.Page3 __instance)
        {
            if (__instance.item != null && __instance.modRequiredToggle != null)
                __instance.modRequiredToggle.interactable = !WorkshopTags.HasPluginTag(__instance.item.AutoTags);
        }
    }
}
