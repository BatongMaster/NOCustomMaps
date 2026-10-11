using System;
using System.Collections.Generic;

namespace CustomMaps
{
    /// <summary>
    /// The arithmetic and naming behind the custom map tiles in the mission editor's New Mission panel
    /// (<c>MapPicker.cs</c>): the tiles' order and labels, the scrolling list's size and where things sit in
    /// it, how far to scroll to show a tile, and the file names of the cached pictures. Free of game and
    /// Unity types, so it is tested.
    ///
    /// From the game (Assembly-CSharp of 14 August 2026, and the panel's assets in level0 and
    /// resources.assets): the panel, <c>MissionEditorNewMenuV2</c>, has two tiles saved in it, 160 by 160
    /// with 6 between them, in a row ('Map options', a HorizontalLayoutGroup) under the 'Map' heading of its
    /// 'Center' column. <c>Awake</c> ties tile <c>i</c> to <c>MapLoader.Maps[i]</c> for the tiles it has and
    /// makes none, so a map past the second is never offered. Each tile draws its selection border 4 units
    /// outside itself (<c>BetterBorder</c>, thickness -4).
    /// </summary>
    internal static class MapTiles
    {
        /// <summary>Tiles per row: the game's row of two, which is also all that fits (three need 492 of
        /// the column's 450).</summary>
        public const int Columns = 2;

        /// <summary>Room round the tiles for the selection border, which the game draws 4 units outside
        /// the tile (<c>BetterBorder.borderThickness</c> = -4), so that the list's edge does not cut it.</summary>
        public const float Margin = 4f;

        /// <summary>The game's tile and the gap between two, used only where they cannot be read from the
        /// panel itself.</summary>
        public const float FallbackCell = 160f;
        public const float FallbackGap = 6f;

        /// <summary>The list's height in the game's panel as saved: 662 high, 30 of padding top and bottom,
        /// 16 children 10 apart, and 266 of children other than the list.</summary>
        public const float FallbackHeight = 186f;

        /// <summary>The Load tab's scrollbar width, and the gap kept between it and the list.</summary>
        public const float ScrollbarWidth = 18f;
        public const float ScrollbarGap = 6f;

        /// <summary>The Load tab's mission list, whose scrolling the map list's follows: its scroll sensitivity
        /// (its ScrollRect's, 5) and its rows' pitch (<c>File_LoadTab_Item</c>'s 30-high main row, 8 apart in
        /// the list's VerticalLayoutGroup).</summary>
        public const float LoadListSensitivity = 5f;
        public const float LoadListRowPitch = 38f;

        /// <summary>
        /// The fingerprint (<see cref="IlFingerprint"/>) of the game methods in <see cref="GameMethods"/>, in
        /// the build the tiles were checked against. They are added only while the running game's
        /// fingerprint is this one, so a game update that changes how the panel binds, selects or creates
        /// leaves the panel as the game has it.
        /// </summary>
        public const ulong VerifiedGameMethods = 0x5DEE66F72F1CEC1CUL;

        /// <summary>
        /// What <see cref="VerifiedGameMethods"/> covers, as namespace, type and method, each
        /// <see cref="IlFingerprint.OfMethod"/> combined in this order: the panel's <c>Awake</c>, which binds
        /// the saved tiles by index and makes none (its click lambda counted with it); <c>MapSelected</c>,
        /// which the plugin's tiles call and which greys or highlights every tile in <c>mapButtons</c>;
        /// <c>Create</c> and <c>CreateUnnamed</c>, which make the selected map's <c>PrefabName</c> the
        /// mission's map key; and the tile's own <c>SetMap</c> and <c>OnMapSelected</c>.
        /// </summary>
        public static readonly string[][] GameMethods =
        {
            new[] { GameNamespace, "MissionEditorNewMenuV2", "Awake" },
            new[] { GameNamespace, "MissionEditorNewMenuV2", "MapSelected" },
            new[] { GameNamespace, "MissionEditorNewMenuV2", "Create" },
            new[] { GameNamespace, "MissionEditorNewMenuV2", "CreateUnnamed" },
            new[] { GameNamespace, "NewMissionMapButton", "SetMap" },
            new[] { GameNamespace, "NewMissionMapButton", "OnMapSelected" },
        };

        public const string GameNamespace = "NuclearOption.MissionEditorScripts";

        // --- order and labels ---

        /// <summary>One custom tile: the map it selects and, under the testing option that shows each map
        /// several times, which copy it is (0 for the first).</summary>
        public struct Tile
        {
            public string Name;
            public string MapId;
            public string PrefabName;
            public string Version;
            public int Copy;

            public Tile(string name, string mapId, string prefabName, string version, int copy = 0)
            {
                Name = name;
                MapId = mapId;
                PrefabName = prefabName;
                Version = version;
                Copy = copy;
            }
        }

        /// <summary>
        /// The order custom tiles are shown in, after the game's own: by the name they show, ignoring case
        /// (map names are ASCII, <c>MapIdentity</c>), then by map id, then by registered name, then by copy.
        /// Returns the indices of <paramref name="tiles"/> in that order.
        /// </summary>
        public static int[] Order(IReadOnlyList<Tile> tiles)
        {
            var order = new int[tiles.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;

            Array.Sort(order, (a, b) =>
            {
                Tile x = tiles[a], y = tiles[b];
                int c = StringComparer.OrdinalIgnoreCase.Compare(BaseLabel(x), BaseLabel(y));
                if (c == 0) c = string.CompareOrdinal(x.MapId, y.MapId);
                if (c == 0) c = string.CompareOrdinal(x.PrefabName, y.PrefabName);
                if (c == 0) c = x.Copy.CompareTo(y.Copy);
                if (c == 0) c = a.CompareTo(b);
                return c;
            });

            return order;
        }

        /// <summary>What a tile says before any collision is resolved: the map's name, or its registered
        /// name when it has none.</summary>
        public static string BaseLabel(Tile tile)
            => !string.IsNullOrWhiteSpace(tile.Name) ? tile.Name.Trim() : tile.PrefabName ?? tile.MapId ?? "?";

        /// <summary>
        /// What each custom tile says. Its map's name, as the game's tiles say theirs; but where two maps
        /// would read the same (the game's included, ignoring case), each custom one of them adds its
        /// version ("Alps 0.4.0"), or its map id where the versions do not tell them apart either, or, last,
        /// its registered name, which is unique. Copies of one map shown more than once for testing are
        /// numbered from the second ("Alps #2"). The map's <c>MapName</c> itself is never changed: lobbies
        /// advertise it. The game's tiles keep their names.
        /// </summary>
        public static string[] Labels(IReadOnlyList<string> gameNames, IReadOnlyList<Tile> tiles)
        {
            var labels = new string[tiles.Count];

            // How many maps read each name. Copies of one map count once.
            var counted = new HashSet<string>(StringComparer.Ordinal);
            var readers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in gameNames)
                if (!string.IsNullOrWhiteSpace(name)) Count(readers, name.Trim());
            foreach (Tile tile in tiles)
                if (counted.Add(tile.PrefabName ?? "")) Count(readers, BaseLabel(tile));

            for (int i = 0; i < tiles.Count; i++)
            {
                string name = BaseLabel(tiles[i]);
                labels[i] = readers[name] < 2 ? name
                          : !string.IsNullOrWhiteSpace(tiles[i].Version) ? $"{name} {tiles[i].Version.Trim()}"
                          : null;
            }

            // A version that does not tell two maps apart (equal, absent, or reading as another tile's
            // name) gives way to the map id, and that to the registered name.
            Resolve(labels, gameNames, tiles, (tile, name) => $"{name} ({tile.MapId})", t => !string.IsNullOrEmpty(t.MapId));
            Resolve(labels, gameNames, tiles, (tile, name) => $"{name} ({tile.PrefabName})", _ => true);

            for (int i = 0; i < tiles.Count; i++)
                if (tiles[i].Copy > 0) labels[i] += $" #{tiles[i].Copy + 1}";

            return labels;
        }

        static void Count(Dictionary<string, int> readers, string name)
            => readers[name] = readers.TryGetValue(name, out int n) ? n + 1 : 1;

        static void Resolve(string[] labels, IReadOnlyList<string> gameNames, IReadOnlyList<Tile> tiles,
                            Func<Tile, string, string> instead, Func<Tile, bool> can)
        {
            // Which maps read each label so far; a label read by two maps, or by a game tile, is not unique.
            var maps = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < tiles.Count; i++)
            {
                if (labels[i] == null) continue;
                if (!maps.TryGetValue(labels[i], out HashSet<string> set))
                    maps[labels[i]] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(tiles[i].PrefabName ?? "");
            }

            var game = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in gameNames)
                if (!string.IsNullOrWhiteSpace(name)) game.Add(name.Trim());

            var change = new List<int>();
            for (int i = 0; i < tiles.Count; i++)
            {
                bool unique = labels[i] != null && maps[labels[i]].Count == 1 && !game.Contains(labels[i]);
                if (!unique && can(tiles[i])) change.Add(i);
            }

            foreach (int i in change) labels[i] = instead(tiles[i], BaseLabel(tiles[i]));
        }

        // --- the list's size and layout ---

        /// <summary>
        /// The list's height: what the panel's column leaves once its padding, its other children and the
        /// spacing between all of them are taken, so that the column is as tall as the game made it. At
        /// least one row, and at most two (the panel has room for one and a fifth of a second as the game
        /// saved it); <see cref="FallbackHeight"/> if the column cannot be read.
        /// </summary>
        public static float ListHeight(float columnHeight, float paddingTop, float paddingBottom, float spacing,
                                       IReadOnlyList<float> otherChildren, float cell, float gap)
        {
            float others = 0f;
            foreach (float h in otherChildren) others += h;

            float height = columnHeight - paddingTop - paddingBottom - others - spacing * otherChildren.Count;
            if (float.IsNaN(height) || float.IsInfinity(height)) height = FallbackHeight;

            float most = Columns * cell + (Columns - 1) * gap;
            if (!(cell > 0f) || float.IsInfinity(most)) return FallbackHeight;
            return Math.Min(Math.Max(height, cell), most);
        }

        /// <summary>Rows <paramref name="tiles"/> tiles take.</summary>
        public static int Rows(int tiles) => tiles <= 0 ? 0 : (tiles + Columns - 1) / Columns;

        /// <summary>The scrolled content's height: the tiles' rows, the gaps between them and the margin
        /// above and below.</summary>
        public static float ContentHeight(int tiles, float cell, float gap, float margin = Margin)
        {
            int rows = Rows(tiles);
            return rows == 0 ? 2f * margin : 2f * margin + rows * cell + (rows - 1) * gap;
        }

        /// <summary>The visible window's width: a row of tiles and the margin either side.</summary>
        public static float ViewportWidth(float cell, float gap, float margin = Margin)
            => Columns * cell + (Columns - 1) * gap + 2f * margin;

        /// <summary>The window's height: the list's, and the margin it reaches above the list's top edge
        /// into the spacing under the 'Map' heading, so that the first row sits where the game's tiles do
        /// and its border is not cut.</summary>
        public static float ViewportHeight(float listHeight, float margin = Margin) => listHeight + margin;

        /// <summary>The scrollbar's left edge, from the middle of the list: past the window's right edge
        /// by <see cref="ScrollbarGap"/>. <paramref name="centre"/> is where the window's middle is.</summary>
        public static float ScrollbarX(float centre, float viewportWidth) => centre + viewportWidth / 2f + ScrollbarGap;

        /// <summary>Whether the window and its scrollbar fit in a column <paramref name="innerWidth"/> wide
        /// with the window's middle <paramref name="centre"/> from the column's.</summary>
        public static bool Fits(float centre, float viewportWidth, float innerWidth)
            => centre - viewportWidth / 2f >= -innerWidth / 2f
            && ScrollbarX(centre, viewportWidth) + ScrollbarWidth <= innerWidth / 2f;

        /// <summary>
        /// Where the content must be scrolled to (how far its top is above the window's, as the scroll view's
        /// content position reads it) for the tile in <paramref name="row"/> to show whole, with its border:
        /// the smallest move from <paramref name="current"/>, and none if it already shows. A tile taller than
        /// the window shows its top. Never past either end.
        /// </summary>
        public static float ScrollFor(int row, float viewportHeight, float cell, float gap, float margin,
                                      float contentHeight, float current)
        {
            float most = Math.Max(0f, contentHeight - viewportHeight);
            if (float.IsNaN(current)) current = 0f;

            // The tile with its border, in the content: the margin above the first row is the border's room.
            float top = Math.Max(0, row) * (cell + gap);
            float bottom = top + cell + 2f * margin;

            float target = current;
            if (bottom > target + viewportHeight) target = bottom - viewportHeight;
            if (top < target) target = top;

            if (float.IsNaN(target)) return 0f;
            return Math.Min(Math.Max(target, 0f), most);
        }

        /// <summary>
        /// The map list's scroll sensitivity: the Load list's, scaled by how much taller a row of tiles is than
        /// one of its rows, so that a notch of the wheel moves the same share of a row in both. A notch's
        /// distance itself is the input module's (the game's <c>InputSystemUIInputModule</c>,
        /// <c>scrollDeltaPerTick</c> 0.03), and the same for both lists.
        /// </summary>
        public static float ScrollSensitivity(float cell, float gap)
        {
            float pitch = cell + gap;
            if (!(pitch > 0f) || float.IsInfinity(pitch)) pitch = FallbackCell + FallbackGap;
            return LoadListSensitivity * pitch / LoadListRowPitch;
        }

        /// <summary>
        /// The rows of <paramref name="rows"/> any part of which is between <paramref name="from"/> and
        /// <paramref name="to"/>, measured down from the content's top as <see cref="ScrollFor"/> measures:
        /// <paramref name="first"/> to <paramref name="last"/>, or false if none is.
        /// </summary>
        public static bool RowsOverlapping(int rows, float cell, float gap, float margin, float from, float to,
                                           out int first, out int last)
        {
            // Row r spans margin + r * pitch to that plus cell.
            float pitch = cell + gap;
            first = 0;
            last = -1;
            if (rows <= 0 || !(pitch > 0f) || float.IsNaN(from) || float.IsNaN(to)) return false;

            first = RowAfter((from - margin - cell) / pitch, rows);
            last = RowBefore((to - margin) / pitch, rows);
            return first <= last;
        }

        /// <summary>
        /// The rows whose middle is inside the window, <paramref name="window"/> high and scrolled to
        /// <paramref name="scroll"/>: the tiles the panel's other controls may move to from the keyboard or a
        /// controller. A window taller than a row and its gap always has one. False if none is.
        /// </summary>
        public static bool RowsCentred(int rows, float cell, float gap, float margin, float window, float scroll,
                                       out int first, out int last)
        {
            // Row r's middle is at margin + r * pitch + cell / 2.
            float pitch = cell + gap;
            first = 0;
            last = -1;
            if (rows <= 0 || !(pitch > 0f) || float.IsNaN(scroll) || float.IsNaN(window)) return false;

            float middle = margin + cell / 2f;
            first = RowAfter((scroll - middle) / pitch, rows);
            last = RowBefore((scroll + window - middle) / pitch, rows);
            return first <= last;
        }

        // The first row past position p, in rows, and the last short of it; each kept to 0..rows - 1, with room
        // for one past either end so that an empty range stays empty, and to what an int holds.
        static int RowAfter(double p, int rows) => (int)Math.Max(0, Math.Min(rows, Math.Floor(p) + 1));
        static int RowBefore(double p, int rows) => (int)Math.Min(rows - 1, Math.Max(-1, Math.Ceiling(p) - 1));

        /// <summary>
        /// The tile a move from tile <paramref name="index"/> of <paramref name="count"/> goes to inside the grid,
        /// <see cref="Columns"/> to a row in reading order: <paramref name="dx"/> -1 or 1 along its row, else
        /// <paramref name="dy"/> -1 (up) or 1 (down) along its column, past any tile <paramref name="usable"/>
        /// turns down. Down into a last row too short for the column goes to that row's last tile. -1 where the
        /// move leaves the grid, for the panel's own navigation to take.
        /// </summary>
        public static int Neighbour(int index, int count, int dx, int dy, Func<int, bool> usable = null)
        {
            if (index < 0 || index >= count) return -1;
            int column = index % Columns;

            if (dx != 0)
            {
                int start = index - column;
                int end = Math.Min(count, start + Columns) - 1;
                int step = Math.Sign(dx);
                for (int j = index + step; j >= start && j <= end; j += step)
                    if (usable == null || usable(j)) return j;
                return -1;
            }

            if (dy != 0)
            {
                int rows = Rows(count);
                int step = Math.Sign(dy);
                for (int row = index / Columns + step; row >= 0 && row < rows; row += step)
                {
                    int j = Math.Min(row * Columns + column, count - 1);
                    if (usable == null || usable(j)) return j;
                }
            }

            return -1;
        }

        // --- pictures ---

        /// <summary>The cached pictures' size, that of the game's own (<c>terrain1_thumbnail</c>,
        /// <c>terrain_naval_thumbnail</c>: 512 by 512).</summary>
        public const int ThumbnailSize = 512;

        /// <summary>
        /// How bright the game's own pictures are where the tile's name sits over them: the mean luma (Rec. 601,
        /// of the sRGB bytes) of the band <see cref="DarkenFor"/> reads, 128.4 on <c>terrain1_thumbnail</c> and
        /// 120.4 on <c>terrain_naval_thumbnail</c>; this is the two's mean. A custom map's picture is darkened
        /// to it there and no further, so that its white name reads as on the game's, and its tile is no
        /// darker than theirs before the game greys every tile but the selected one.
        /// </summary>
        public const float LabelBandLuma = 124.4f;

        /// <summary>The most a picture is darkened, for a map that is nearly all snow or sand.</summary>
        public const float DarkestFactor = 0.6f;

        /// <summary>Which way pictures are made. Change it when they come out different, so that every
        /// cached one is made again.</summary>
        public const string ThumbnailGeneration = "t1";

        /// <summary>Characters of the map's content hash in a cached picture's name.</summary>
        public const int ThumbnailHashLength = 16;

        /// <summary>The folder under BepInEx's <c>cache</c> the pictures are kept in. Safe to delete.</summary>
        public const string ThumbnailFolderName = "com.javoski.custommaps.thumbnails";

        /// <summary>
        /// A map build's cached picture: <c>&lt;mapId&gt;-&lt;16 hex of its content hash&gt;-t1.png</c>. Null when
        /// the map id or the hash cannot name a file, and the picture is then made each session.
        /// </summary>
        public static string ThumbnailFileName(string mapId, string contentHash)
        {
            if (!MapIdentity.IsValidMapId(mapId)) return null;
            if (contentHash == null || contentHash.Length < ThumbnailHashLength) return null;

            string hash = contentHash.Substring(0, ThumbnailHashLength).ToLowerInvariant();
            if (!IsHex(hash)) return null;

            return $"{mapId}-{hash}-{ThumbnailGeneration}.png";
        }

        /// <summary>
        /// Whether <paramref name="fileName"/> is a cached picture of the map <paramref name="mapId"/>, of
        /// any build and any generation, or one left half written (<c>.tmp</c>): what a new picture of the
        /// map replaces. Read strictly, so that one map's id at the start of another's
        /// (<c>kellys</c>, <c>kellys-wake-island</c>) does not take the other's pictures.
        /// </summary>
        public static bool IsThumbnailOf(string fileName, string mapId)
        {
            if (string.IsNullOrEmpty(fileName) || !MapIdentity.IsValidMapId(mapId)) return false;

            string name = fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                ? fileName.Substring(0, fileName.Length - 4) : fileName;
            if (!name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return false;
            name = name.Substring(0, name.Length - 4);

            string prefix = mapId + "-";
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string rest = name.Substring(prefix.Length);

            // <16 hex>-t<digits>
            if (rest.Length < ThumbnailHashLength + 3) return false;
            if (!IsHex(rest.Substring(0, ThumbnailHashLength))) return false;
            if (rest[ThumbnailHashLength] != '-' || rest[ThumbnailHashLength + 1] != 't') return false;
            for (int i = ThumbnailHashLength + 2; i < rest.Length; i++)
                if (rest[i] < '0' || rest[i] > '9') return false;
            return true;
        }

        /// <summary>
        /// The middle square of a picture, as the scale and offset a blit samples it by: the picture is
        /// <paramref name="width"/> by <paramref name="height"/> at <paramref name="x"/>, <paramref name="y"/>
        /// in a texture <paramref name="textureWidth"/> by <paramref name="textureHeight"/> (a sprite's rect
        /// in its texture, or the whole texture).
        /// </summary>
        public static void CentreSquare(float x, float y, float width, float height, float textureWidth, float textureHeight,
                                        out float scaleX, out float scaleY, out float offsetX, out float offsetY)
        {
            float side = Math.Min(width, height);
            scaleX = side / textureWidth;
            scaleY = side / textureHeight;
            offsetX = (x + (width - side) / 2f) / textureWidth;
            offsetY = (y + (height - side) / 2f) / textureHeight;
        }

        /// <summary>The largest intermediate a picture with mipmaps is drawn into on its way down to
        /// <see cref="ThumbnailSize"/>: 2048 by 2048, 16 MiB of video memory for a moment. The first draw
        /// reads the mipmap as fine as that, so a larger source is still averaged whole.</summary>
        public const int LargestStep = 2048;

        /// <summary>The largest for a source without mipmaps: 4096 by 4096, 64 MiB for a moment, so that an
        /// 8192 chart (Map Forge's largest, <c>AirfieldChart.MaxSize</c>) is drawn at half its size first and
        /// every texel counts. Map Forge gives its textures mipmaps; a picture imported by hand as a Sprite
        /// has none unless asked.</summary>
        public const int LargestStepWithoutMips = 4096;

        /// <summary>
        /// The sizes a square of <paramref name="side"/> pixels is drawn into, largest first, ending at
        /// <paramref name="size"/>: the first no larger than the square (nor than <paramref name="largest"/>),
        /// each after it half the one before. Halving, each pixel is the average of the four under it, so that
        /// a picture does not shimmer down to a sparse sample of itself. The first step reads the source's own
        /// mipmaps where it has them; without, it is a halving only if the source is no more than twice
        /// <paramref name="largest"/>, which <see cref="LargestStepWithoutMips"/> is for.
        /// A square smaller than <paramref name="size"/> is drawn once, at <paramref name="size"/>.
        /// </summary>
        public static int[] DownscaleSteps(int side, int size = ThumbnailSize, int largest = LargestStep)
        {
            int first = size;
            while (first * 2 <= side && first * 2 <= largest) first *= 2;

            var steps = new List<int>();
            for (int s = first; s >= size; s /= 2) steps.Add(s);
            return steps.ToArray();
        }

        /// <summary>
        /// How much to darken a picture, <paramref name="width"/> by <paramref name="height"/> pixels
        /// <paramref name="channels"/> bytes each, rows packed: what brings the mean luma of the band the tile's
        /// name sits over (the middle fifth of its height, all but 8% at either side; the name is centred, 24
        /// points high on a 160 tile) down to <see cref="LabelBandLuma"/>. Never brighter (1), never below
        /// <see cref="DarkestFactor"/>; 1 when there is nothing to read.
        /// </summary>
        public static float DarkenFor(byte[] pixels, int width, int height, int channels)
        {
            if (pixels == null || width <= 0 || height <= 0 || channels < 3) return 1f;
            if ((long)width * height * channels > pixels.Length) return 1f;

            int top = (int)(height * 0.4), bottom = (int)(height * 0.6);
            int left = (int)(width * 0.08), right = (int)(width * 0.92);

            double sum = 0;
            long count = 0;
            for (int y = top; y < bottom; y++)
            {
                int row = y * width * channels;
                for (int x = left; x < right; x++)
                {
                    int p = row + x * channels;
                    sum += 0.299 * pixels[p] + 0.587 * pixels[p + 1] + 0.114 * pixels[p + 2];
                    count++;
                }
            }

            if (count == 0) return 1f;
            double mean = sum / count;
            if (!(mean > LabelBandLuma)) return 1f;
            return (float)Math.Max(DarkestFactor, LabelBandLuma / mean);
        }

        /// <summary>Darkens pixels <paramref name="channels"/> bytes each in place, colour only: the first
        /// three bytes of each are scaled by <paramref name="factor"/>, rounded, and a fourth (alpha) is
        /// left.</summary>
        public static void Darken(byte[] pixels, int channels, float factor)
        {
            if (pixels == null || channels < 1) return;
            int colour = Math.Min(3, channels);
            for (int p = 0; p + channels <= pixels.Length; p += channels)
                for (int c = 0; c < colour; c++)
                    pixels[p + c] = (byte)Math.Min(255, (int)(pixels[p + c] * factor + 0.5f));
        }

        static bool IsHex(string s)
        {
            foreach (char c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            return true;
        }
    }
}
