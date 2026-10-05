using System;
using System.Collections.Generic;

namespace CustomMaps
{
    /// <summary>
    /// Which installed bundle a map key is about, so its prefab can start loading before the map does.
    ///
    /// A map's prefab is read in the background from the moment the game shows it is about to be
    /// played (<c>LoadedMap.BeginWarmUp</c>): a mission on it picked, a lobby made for it or joined,
    /// its load begun. What the game hands over then is mostly the map's key,
    /// <c>cm.&lt;id&gt;.&lt;hash8&gt;</c> (<see cref="MapIdentity"/>), and the bundles' hashes may still
    /// be being worked out at that point. So a key is matched by its map id alone until the bundle's
    /// hash is in, and by id and hash after: a mission made on another build of the map does not load
    /// anyway, and the build installed is not read for it. A lobby gives only the map's display name
    /// (<see cref="ForLobby"/>).
    ///
    /// Kept free of game and Unity types so the tests cover it.
    /// </summary>
    internal static class WarmUpPolicy
    {
        /// <summary>
        /// True when the key <paramref name="prefabName"/> names the bundle of map
        /// <paramref name="mapId"/> whose full SHA-256 is <paramref name="contentHash"/>, or null while
        /// it is not known. A shipped map's name (<c>Terrain1</c>) never matches.
        /// </summary>
        public static bool Wants(string prefabName, string mapId, string contentHash)
        {
            if (!MapIdentity.TryParse(prefabName, out string id, out string hash)) return false;
            if (!string.Equals(id, mapId, StringComparison.Ordinal)) return false;
            if (string.IsNullOrEmpty(contentHash)) return true;

            return contentHash.StartsWith(hash, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Which installed map a lobby being joined is on, from the map name it advertises: the index
        /// of the one entry of <paramref name="displayNames"/> (the installed maps' display names, in
        /// any order) that is exactly <paramref name="advertised"/>, or -1.
        ///
        /// A lobby carries its map's display name (<c>map_name</c>, which the host takes from
        /// <c>MapDetails.MapName</c>) but not its key, and a joining client learns the key only when
        /// the server sends the map. The name is enough to start reading sooner. No match, a name two
        /// installed maps share, and a name the game's filter changed on the way all give -1: the map
        /// then starts reading when its key arrives, as without this. A lobby on another build of an
        /// installed map does match, and that build is read for a join that will fail: the lobby says
        /// nothing that would tell the two apart.
        /// </summary>
        public static int ForLobby(string advertised, IReadOnlyList<string> displayNames)
        {
            if (string.IsNullOrEmpty(advertised) || displayNames == null) return -1;

            int found = -1;
            for (int i = 0; i < displayNames.Count; i++)
            {
                if (!string.Equals(displayNames[i], advertised, StringComparison.Ordinal)) continue;
                if (found >= 0) return -1;
                found = i;
            }
            return found;
        }
    }
}
