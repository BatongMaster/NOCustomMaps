using System;
using System.Globalization;
using System.Text;

namespace CustomMaps
{
    /// <summary>
    /// The naming scheme for custom maps, and the multiplayer version handshake.
    ///
    /// A custom map's <c>MapDetails.PrefabName</c> is
    ///
    ///     cm.&lt;mapId&gt;.&lt;hash8&gt;          e.g. "cm.kastellan.a1b2c3d4"
    ///
    /// where <c>hash8</c> is the first 8 hex characters of the SHA-256 of the
    /// <c>.nomap</c> bundle, stamped in at bundle build time.
    ///
    /// That embedding is deliberate and load-bearing. <c>LoadMapMessage</c> carries
    /// only <c>MapKey { KeyType Type; string Path; }</c> and cannot be extended: a
    /// custom Mirage message id would charge an unmodded peer
    /// <c>SetError(60, RpcSync)</c> for a message it cannot decode. But the server
    /// already broadcasts <c>Path</c>, and <c>MapLoader.CanLoad</c> resolves it with
    /// <c>Maps.Any(m =&gt; m.PrefabName == key.Path)</c> — an exact string compare.
    /// So putting the content hash in the name makes bundle divergence fail as a
    /// clean, detectable join-time rejection instead of a silent geometry desync
    /// where the server and client disagree about where the ground is.
    ///
    /// The cost is that rebuilding a map changes its <c>PrefabName</c> and any
    /// mission pointing at it must be re-pointed. Only re-hash on release builds.
    /// </summary>
    public static class MapIdentity
    {
        /// <summary>Marks a PrefabName as ours. Short, because MapKey.Path goes over the wire.</summary>
        public const string Prefix = "cm.";

        public const int MapIdMinLength = 2;
        public const int MapIdMaxLength = 32;

        /// <summary>Characters of the SHA-256 kept in the name. 32 bits of collision resistance
        /// against accidental divergence, which is the only threat model here — nobody is
        /// attacking a map name.</summary>
        public const int HashLength = 8;

        /// <summary>
        /// <c>MapDetails.MapName</c> is what a lobby advertises. The display path is
        /// <c>GetData("map_name").ProfanityFilter().SanitizeRichText(64)</c>, so
        /// angle brackets get eaten as rich text and an unlucky substring gets
        /// starred out. Stay well inside 64 and stay ASCII.
        /// </summary>
        public const int MapNameMaxLength = 40;

        // NetworkMap computes a PrefabHash as (MapPrefix << 24) | index. Values at or
        // above 128 make that shift negative — legal, but needlessly confusing in a log.
        // 1 and 2 are assumed taken by the shipped maps, and the low band is left free
        // for anything else that wants a well-known prefix.
        public const int MinPrefix = 16;
        public const int MaxPrefix = 111;

        /// <summary>Builds the PrefabName for a map. <paramref name="contentHash"/> may be a
        /// full SHA-256 hex string; only the first <see cref="HashLength"/> characters are used.</summary>
        public static string MakePrefabName(string mapId, string contentHash)
        {
            if (!IsValidMapId(mapId))
                throw new ArgumentException($"invalid mapId '{mapId}'", nameof(mapId));
            if (contentHash == null || contentHash.Length < HashLength)
                throw new ArgumentException("content hash must be at least 8 hex characters", nameof(contentHash));

            string h = contentHash.Substring(0, HashLength).ToLowerInvariant();
            if (!IsHex(h))
                throw new ArgumentException($"content hash '{contentHash}' is not hexadecimal", nameof(contentHash));

            return Prefix + mapId + "." + h;
        }

        /// <summary>True if <paramref name="prefabName"/> is one of ours. Vanilla names
        /// ("Terrain1", "Terrain_naval") are rejected, which is how the registrar tells
        /// its own entries apart from the shipped ones.</summary>
        public static bool TryParse(string prefabName, out string mapId, out string contentHash)
        {
            mapId = null;
            contentHash = null;

            if (string.IsNullOrEmpty(prefabName) || !prefabName.StartsWith(Prefix, StringComparison.Ordinal))
                return false;

            // The hash is the final dot-separated field, so a mapId may not contain a dot
            // (IsValidMapId enforces that) and the split is unambiguous from the right.
            int dot = prefabName.LastIndexOf('.');
            if (dot <= Prefix.Length - 1) return false;

            string id = prefabName.Substring(Prefix.Length, dot - Prefix.Length);
            string h = prefabName.Substring(dot + 1);

            if (!IsValidMapId(id)) return false;
            if (h.Length != HashLength || !IsHex(h)) return false;

            mapId = id;
            contentHash = h;
            return true;
        }

        /// <summary>The mapId of a PrefabName, or null if it is not one of ours.
        /// Used to recognise "same map, different build" at join time.</summary>
        public static string IdOf(string prefabName)
            => TryParse(prefabName, out string id, out _) ? id : null;

        /// <summary>The content hash of a PrefabName, or null if it is not one of ours.</summary>
        public static string HashOf(string prefabName)
            => TryParse(prefabName, out _, out string h) ? h : null;

        /// <summary>
        /// Deterministic <c>NetworkMap.MapPrefix</c> for a map id, in [16, 111].
        ///
        /// This must be stable across processes and machines, which rules out
        /// <c>string.GetHashCode</c> — .NET Core randomises it per process, so the
        /// server and client would compute different prefixes from the same id.
        /// FNV-1a is spelled out below for that reason.
        ///
        /// Note the prefix is also baked into the manifest, so this is really a
        /// default rather than the authority; the runtime asserts rather than
        /// recomputes. It exists so the authoring tool can pick a sensible value
        /// without a registry.
        /// </summary>
        public static int AllocatePrefix(string mapId)
        {
            if (!IsValidMapId(mapId))
                throw new ArgumentException($"invalid mapId '{mapId}'", nameof(mapId));

            ulong h = Fnv1a64(mapId);
            int span = MaxPrefix - MinPrefix + 1;
            return MinPrefix + (int)(h % (ulong)span);
        }

        public static bool IsValidPrefix(int prefix) => prefix >= MinPrefix && prefix <= MaxPrefix;

        /// <summary>
        /// mapId charset: lowercase ASCII letters, digits and hyphen. No dots (the
        /// name is split on the last dot), no underscores (visually confusable with
        /// hyphen in a log), no uppercase (so two ids cannot differ only by case and
        /// then compare unequal in the Ordinal lookups the registrar uses).
        /// </summary>
        public static bool IsValidMapId(string mapId)
        {
            if (string.IsNullOrEmpty(mapId)) return false;
            if (mapId.Length < MapIdMinLength || mapId.Length > MapIdMaxLength) return false;
            if (mapId[0] == '-' || mapId[mapId.Length - 1] == '-') return false;

            foreach (char c in mapId)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-';
                if (!ok) return false;
            }
            return true;
        }

        /// <summary>
        /// The nearest valid map id to <paramref name="raw"/> — typically a file name.
        ///
        /// Lowercases, turns every run of anything outside <c>[a-z0-9]</c> into one hyphen,
        /// trims hyphens from both ends and truncates to <see cref="MapIdMaxLength"/>. When
        /// nothing usable is left, returns <paramref name="fallback"/>. The result always
        /// passes <see cref="IsValidMapId"/> provided the fallback does.
        /// </summary>
        public static string Sanitise(string raw, string fallback = "custom")
        {
            var sb = new StringBuilder();
            bool pendingHyphen = false;

            foreach (char ch in raw ?? "")
            {
                char c = char.ToLowerInvariant(ch);
                bool keep = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');

                if (!keep)
                {
                    pendingHyphen = sb.Length > 0;
                    continue;
                }

                if (pendingHyphen) sb.Append('-');
                pendingHyphen = false;
                sb.Append(c);
            }

            string id = sb.ToString();
            if (id.Length > MapIdMaxLength) id = id.Substring(0, MapIdMaxLength).TrimEnd('-');

            return IsValidMapId(id) ? id : fallback;
        }

        /// <summary>Validates a display name against the lobby advertisement path.
        /// Returns false with a reason so the diagnostics block can print it.</summary>
        public static bool IsValidMapName(string mapName, out string reason)
        {
            if (string.IsNullOrEmpty(mapName)) { reason = "empty"; return false; }
            if (mapName.Length > MapNameMaxLength)
            {
                reason = $"{mapName.Length} characters, limit is {MapNameMaxLength}";
                return false;
            }
            foreach (char c in mapName)
            {
                if (c == '<' || c == '>')
                {
                    reason = "contains '<' or '>', which SanitizeRichText will eat";
                    return false;
                }
                if (c < 0x20 || c > 0x7E)
                {
                    reason = $"contains non-printable-ASCII U+{((int)c).ToString("X4", CultureInfo.InvariantCulture)}";
                    return false;
                }
            }
            reason = null;
            return true;
        }

        static bool IsHex(string s)
        {
            foreach (char c in s)
            {
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
                if (!ok) return false;
            }
            return true;
        }

        // FNV-1a, 64-bit. Written out rather than taken from the BCL so the value is
        // pinned by this source and cannot drift with a runtime update.
        internal static ulong Fnv1a64(string s)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;

            ulong hash = offset;
            foreach (char c in s)
            {
                // Hash the UTF-16 code unit byte-wise, low byte first, so the result
                // does not depend on machine endianness.
                hash = (hash ^ (byte)(c & 0xFF)) * prime;
                hash = (hash ^ (byte)((c >> 8) & 0xFF)) * prime;
            }
            return hash;
        }
    }
}
