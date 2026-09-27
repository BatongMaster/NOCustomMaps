using System;
using System.Collections.Generic;
using System.IO;

namespace CustomMaps
{
    /// <summary>A <c>.nomap</c> file found on disk, before anything has been opened.</summary>
    public sealed class DiscoveredBundle
    {
        /// <summary>Absolute, normalised path. The dedupe key.</summary>
        public string Path;

        /// <summary>The directory it came from, in precedence order (0 = highest).</summary>
        public int SourceRank;

        /// <summary>Best-effort mapId parsed from the file name. A hint for logging and
        /// for locating the sidecar only — the authority is <c>MapDetails.PrefabName</c>
        /// inside the bundle.</summary>
        public string FileNameHint;

        public override string ToString() => Path;
    }

    /// <summary>
    /// Locates custom map bundles.
    ///
    /// The filesystem is injected rather than called directly so the precedence and
    /// dedupe rules — the parts that actually have edge cases — are unit-testable
    /// without laying down files.
    /// </summary>
    public static class BundleDiscovery
    {
        /// <summary>
        /// A plain AssetBundle, renamed.
        ///
        /// NOMapLoader globs <c>*map_*</c> across the whole plugin directory, which also
        /// matches <c>NOCustomMaps.dll</c>, any stray <c>.manifest</c>, and anything else
        /// a user has dropped in with "map" in its name. A dedicated extension is
        /// unambiguous and tells a server operator what the file is.
        /// </summary>
        public const string Extension = ".nomap";

        /// <summary>Suffix of the optional metadata file sitting beside a bundle.</summary>
        public const string SidecarSuffix = ".nomap.json";

        /// <summary>
        /// Scans <paramref name="directories"/> in order and returns every bundle found,
        /// highest-precedence first, with duplicates removed.
        ///
        /// Precedence is by directory order, and only decides <em>candidate order</em>;
        /// the actual "same map twice" resolution happens by PrefabName in
        /// <see cref="CatalogMerge"/>, so there is a single place where that rule lives.
        /// </summary>
        /// <param name="directories">Search roots, most-preferred first. Missing ones are skipped.</param>
        /// <param name="listFiles">Returns the file paths directly inside a directory, or
        /// null/empty if it does not exist. Must not throw.</param>
        public static List<DiscoveredBundle> Scan(
            IReadOnlyList<string> directories,
            Func<string, IEnumerable<string>> listFiles)
        {
            if (listFiles == null) throw new ArgumentNullException(nameof(listFiles));

            var results = new List<DiscoveredBundle>();
            if (directories == null) return results;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int rank = 0; rank < directories.Count; rank++)
            {
                string dir = directories[rank];
                if (string.IsNullOrWhiteSpace(dir)) continue;

                IEnumerable<string> files = listFiles(dir);
                if (files == null) continue;

                foreach (string file in files)
                {
                    if (!IsBundlePath(file)) continue;

                    string key = Normalise(file);
                    if (key == null || !seen.Add(key)) continue;

                    results.Add(new DiscoveredBundle
                    {
                        Path = key,
                        SourceRank = rank,
                        FileNameHint = ParseMapIdHint(file),
                    });
                }
            }

            return results;
        }

        /// <summary>True for a path ending in <c>.nomap</c>. Deliberately excludes
        /// <c>.nomap.json</c> sidecars, which end in <c>.json</c>.</summary>
        public static bool IsBundlePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            return path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Path of the sidecar for a bundle, whether or not it exists.</summary>
        public static string SidecarPathFor(string bundlePath)
            => string.IsNullOrWhiteSpace(bundlePath) ? null : bundlePath + ".json";

        /// <summary>
        /// Pulls a probable mapId out of <c>&lt;mapId&gt;-&lt;version&gt;.nomap</c>.
        ///
        /// Splits on the <em>last</em> hyphen, because a mapId may contain hyphens but a
        /// version is conventionally the trailing field. Returns the whole stem when
        /// there is no hyphen or the tail does not look like a version. A hint only —
        /// nothing downstream trusts it.
        /// </summary>
        public static string ParseMapIdHint(string bundlePath)
        {
            if (string.IsNullOrWhiteSpace(bundlePath)) return null;

            string stem;
            try { stem = Path.GetFileNameWithoutExtension(bundlePath); }
            catch (ArgumentException) { return null; }

            if (string.IsNullOrEmpty(stem)) return null;

            int dash = stem.LastIndexOf('-');
            if (dash <= 0 || dash == stem.Length - 1) return stem;

            string tail = stem.Substring(dash + 1);
            bool looksLikeVersion = tail.Length > 0 && (char.IsDigit(tail[0]) || tail[0] == 'v');
            return looksLikeVersion ? stem.Substring(0, dash) : stem;
        }

        // Case-insensitive on Windows, which is the only platform the game and its
        // dedicated server ship for. Two directories that resolve to the same file
        // (a junction, or plugins/ listed twice in config) must not load it twice —
        // AssetBundle.LoadFromFile throws on a second load of the same file.
        internal static string Normalise(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
            {
                return null;
            }
        }
    }
}
