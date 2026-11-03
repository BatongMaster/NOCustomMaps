using System;
using System.Collections.Generic;

namespace CustomMaps
{
    /// <summary>
    /// The tags every mission on a custom map goes up to the workshop with: the map's display name, as
    /// Heartland's missions carry "Heartland", then "Mod Required" and <see cref="PluginTag"/>.
    ///
    /// The display name is also the tag the mission picker files the map's missions under
    /// (<c>MissionTag.SetupMapTags</c> and <c>ApplyAutoTags</c> both read <c>MapDetails.WorkshopTag</c>),
    /// so every build of a map shares one filter.
    ///
    /// Kept free of game and Unity types so the tests run it.
    /// </summary>
    internal static class WorkshopTags
    {
        public const string PluginTag = "NOCustomMaps";

        /// <summary>A map's tag from its display name. Steam hands a mission's tags back joined by
        /// commas and the game splits them on that, so a comma cannot be in one.</summary>
        public static string MapTag(string displayName)
            => (displayName ?? "").Replace(",", "").Trim();

        /// <summary>
        /// The tag of the map a mission's <c>MapKey.Path</c> names, from the installed maps as
        /// (registered name, tag). The same map in another build answers just as well: the mission's
        /// build is in its file, and the tag names only the map. Null when no build of it is installed,
        /// as a map's display name can be read from nowhere else.
        /// </summary>
        public static string FindMapTag(string missionPath, IEnumerable<KeyValuePair<string, string>> maps, out bool exactBuild)
        {
            exactBuild = false;
            string id = MapIdentity.IdOf(missionPath);
            if (id == null || maps == null) return null;

            string tag = null;
            foreach (KeyValuePair<string, string> map in maps)
            {
                if (MapIdentity.IdOf(map.Key) != id || string.IsNullOrEmpty(map.Value)) continue;

                if (string.Equals(map.Key, missionPath, StringComparison.Ordinal))
                {
                    exactBuild = true;
                    return map.Value;
                }
                if (tag == null) tag = map.Value;
            }
            return tag;
        }

        /// <summary>Makes <see cref="PluginTag"/> one of a mission's automatic tags, which the upload
        /// panel offers no way to remove, and drops it from the tags typed by hand.</summary>
        public static void AddPluginTag(IList<string> autoTags, IList<string> customTags)
        {
            for (int i = customTags.Count - 1; i >= 0; i--)
                if (string.Equals(customTags[i], PluginTag, StringComparison.OrdinalIgnoreCase))
                    customTags.RemoveAt(i);

            if (!HasPluginTag(autoTags)) autoTags.Add(PluginTag);
        }

        public static bool HasPluginTag(IEnumerable<string> tags)
        {
            foreach (string tag in tags)
                if (string.Equals(tag, PluginTag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// TEMPORARY, TO BE REMOVED: Swiss Alps 0.4.1 has no <c>tagColor</c> in its manifest and a rebuild
        /// would change its hash8, so the plugin holds its colour, a softer take on the Swiss flag's red, until a release of
        /// the map carries it. Once it does, delete this and its use in <see cref="TryTagColor"/>.
        /// </summary>
        const string SwissAlpsId = "swissalps", SwissAlpsColor = "#cc5247";

        /// <summary>The colour a map's tag is set to: its manifest's, if that is one, otherwise any
        /// the plugin holds for it. False when neither, and the map takes a hue from <see cref="TagHue"/>.</summary>
        public static bool TryTagColor(string manifestColor, string mapId, out byte red, out byte green, out byte blue)
        {
            if (MapManifest.TryParseColor(manifestColor, out red, out green, out blue)) return true;
            return mapId == SwissAlpsId && MapManifest.TryParseColor(SwissAlpsColor, out red, out green, out blue);
        }

        /// <summary>A map's hue in the mission picker, in [0, 1): from its id, so it is the same on every
        /// machine and for every build. Without one the tag stays the blank white of a new MapDetails.</summary>
        public static float TagHue(string mapId) => (float)(MapIdentity.Fnv1a64(mapId ?? "") % 360UL) / 360f;
    }
}
