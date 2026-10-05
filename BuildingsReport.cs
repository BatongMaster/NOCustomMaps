using System;
using System.Globalization;
using System.IO;

namespace CustomMaps
{
    /// <summary>
    /// The validation block's line about a map's buildings: what its manifest names, and whether the
    /// bundle carries it.
    ///
    /// The block had no such line, and that hid a whole map's towns. On 2026-10-02 Swiss Alps was
    /// built with no <c>cities.bin</c> in Map Forge's out folder, so its bundle had no placements
    /// while its manifest still named <c>swissalps_cities</c>. The game showed no cities at all; the
    /// log had one warning from <c>CityBuilder</c> on each load, and a validation block that ended
    /// <c>=> OK</c>. A map without towns is still a map, so this warns rather than rejects.
    ///
    /// The count comes from the data's header, checked against its length, rather than from reading
    /// every placement: <c>CityData.Read</c> over Swiss Alps' 64,524 buildings copied and parsed
    /// 1.3 MB to print one number.
    ///
    /// Kept free of game and Unity types so the wording is tested outside the game.
    /// </summary>
    internal static class BuildingsReport
    {
        /// <summary>Bytes of city data the count is read from: <c>CityData.Magic</c>, then the count
        /// as a little-endian int.</summary>
        public static readonly int HeaderLength = CityData.Magic.Length + sizeof(int);

        /// <summary>Bytes per placement as <c>CityData.Write</c> lays them out: a type byte, three of
        /// padding, then four floats.</summary>
        const int PlacementLength = 20;

        /// <summary>The line for a manifest naming <paramref name="asset"/>, whose bytes in the bundle
        /// are <paramref name="bytes"/>: null when the bundle has no TextAsset by that name.</summary>
        public static string Describe(string asset, byte[] bytes) =>
            Describe(asset, bytes, bytes != null ? bytes.LongLength : 0);

        /// <summary>
        /// The line for a manifest naming <paramref name="asset"/>, from the data's first bytes alone:
        /// <paramref name="head"/> holds at least <see cref="HeaderLength"/> of them, or all there are,
        /// and <paramref name="length"/> is the length of the whole. A null head means the bundle has no
        /// TextAsset by that name.
        /// </summary>
        public static string Describe(string asset, byte[] head, long length)
        {
            if (string.IsNullOrEmpty(asset)) return "none named — this map has no towns";

            if (head == null)
                return $"'{asset}' named but not in the bundle  WARN — the map has no towns: it was built " +
                       "without its city placements (Map Forge's cities.bin). Rebuild the map.";

            int count;
            try
            {
                count = CountPlacements(head, length);
            }
            catch (InvalidDataException e)
            {
                return $"'{asset}' is not city data ({e.Message})  WARN — the map has no towns";
            }

            if (count == 0) return $"'{asset}' places nothing  WARN — the map has no towns";

            return $"{count.ToString("N0", CultureInfo.InvariantCulture)} placement(s) in '{asset}'  OK";
        }

        /// <summary>
        /// The number of placements city data holds, from its header: what <c>CityData.Read</c> would
        /// return for it, and an <see cref="InvalidDataException"/> wherever that would throw, a body
        /// too short for its count included. Bytes past the last placement are ignored there too.
        /// </summary>
        public static int CountPlacements(byte[] head, long length)
        {
            if (head == null) throw new ArgumentNullException(nameof(head));

            byte[] magic = CityData.Magic;
            if (head.Length < magic.Length || length < magic.Length)
                throw new InvalidDataException("city data is truncated");

            for (int i = 0; i < magic.Length; i++)
                if (head[i] != magic[i])
                    throw new InvalidDataException("not city data — the magic does not match");

            if (head.Length < HeaderLength || length < HeaderLength)
                throw new InvalidDataException("city data is truncated");

            int at = magic.Length;
            int count = head[at] | head[at + 1] << 8 | head[at + 2] << 16 | head[at + 3] << 24;
            if (count < 0 || count > CityData.MaxPlacements)
                throw new InvalidDataException($"city data claims {count} placements");

            long needed = HeaderLength + (long)count * PlacementLength;
            if (length < needed)
                throw new InvalidDataException(
                    $"cut short: {count.ToString("N0", CultureInfo.InvariantCulture)} placement(s) need " +
                    $"{needed.ToString("N0", CultureInfo.InvariantCulture)} bytes, it has " +
                    $"{length.ToString("N0", CultureInfo.InvariantCulture)}");

            return count;
        }
    }
}
