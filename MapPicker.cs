using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using NuclearOption.MissionEditorScripts;
using NuclearOption.SceneLoading;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace CustomMaps
{
    /// <summary>
    /// Whether the custom maps are offered as tiles in the mission editor's New Mission panel: the option is
    /// on (<c>General.MissionEditorMapTiles</c>), and the game's panel is the one this was checked against
    /// (<see cref="MapTiles.VerifiedGameMethods"/>). Read once, before patching
    /// (<c>NewMissionMapTilesPatch.Prepare</c>); a change of the option applies at the next start.
    /// </summary>
    internal static class MapPicker
    {
        internal static readonly bool Supported = Check();

        static bool Check()
        {
            if (Plugin.Disabled) return false;

            if (Plugin.MissionEditorMapTiles != null && !Plugin.MissionEditorMapTiles.Value)
            {
                Plugin.LogInfo("custom maps are not offered in the mission editor's New Mission panel " +
                               "(General.MissionEditorMapTiles is off)");
                return false;
            }

            try
            {
                var prints = new List<ulong>();
                Assembly game = typeof(MissionEditorNewMenuV2).Assembly;
                foreach (string[] m in MapTiles.GameMethods)
                {
                    // Each is in the start-up check of patch targets, which names a missing one.
                    Type type = game.GetType(m[0] + "." + m[1]);
                    MethodInfo method = type != null ? AccessTools.Method(type, m[2]) : null;
                    if (method == null) return false;
                    prints.Add(IlFingerprint.OfLoadedMethod(method));
                }

                ulong print = IlFingerprint.OfAll(prints);
                if (print == MapTiles.VerifiedGameMethods) return true;

                Plugin.LogWarning($"the game's New Mission panel (MissionEditorNewMenuV2 and NewMissionMapButton, fingerprint " +
                                  $"{print:X16}) is not the one this build of Custom Maps was checked against; custom maps are " +
                                  "not offered there. Set MapKey.Path by hand as the README says");
                return false;
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"could not read the game's New Mission panel ({e.GetType().Name}: {e.Message}); custom maps " +
                                  "are not offered there. Set MapKey.Path by hand as the README says");
                return false;
            }
        }
    }

    /// <summary>
    /// The custom maps' tiles in one copy of the mission editor's New Mission panel (<c>MissionEditorNewMenuV2</c>,
    /// on 'File_NewTab'), and the scrolling list they sit in. The game has two copies of the panel, the main
    /// menu's and the editor's own (File &gt; New), identical but for a prefab neither uses; each gets one of
    /// these, from its <c>Awake</c> (<c>NewMissionMapTilesPatch</c>).
    ///
    /// The game's panel has a row of two tiles saved in it, Heartland's and Ignus Archipelago's, which its
    /// <c>Awake</c> ties to <c>MapLoader.Maps[0]</c> and <c>[1]</c>; it makes no tile for any other map. A click
    /// calls its <c>MapSelected</c>, which keeps the map (<c>selectedMap</c>) and greys or highlights every tile
    /// in <c>mapButtons</c>; Create New and Create Default then make the mission's map key from that map's
    /// <c>PrefabName</c>. So a custom map's tile is a copy of the first tile, given the map
    /// (<c>NewMissionMapButton.SetMap</c>, which the game has and never calls), a click that calls
    /// <c>MapSelected</c> with it, and a place in <c>mapButtons</c>. Nothing past the click is the plugin's.
    ///
    /// The row becomes a grid of two columns in a list that scrolls up and down, in the row's place under the
    /// 'Map' heading, as tall as the panel leaves (<see cref="MapTiles.ListHeight"/>): the game's two tiles
    /// first, where they were, then every installed custom map, by name. Built the first time there is a
    /// custom map to show, so that with none the panel is the game's.
    ///
    /// Shown again (<see cref="OnEnable"/>), it adds the maps registered since, greys out any whose prefab
    /// failed to load since, and scrolls the selected map into view. While shown it asks for the pictures of
    /// the rows in and next to its window, and keeps the keyboard and controller moves between the tiles to
    /// the ones that show (<see cref="Navigate"/>).
    /// </summary>
    internal sealed class MapTileList : MonoBehaviour
    {
        /// <summary>Under the game's 24-point names, the smallest a long name shrinks to.</summary>
        const float SmallestName = 14f;

        sealed class Tile
        {
            public NewMissionMapButton Button;
            public LoadedMap Map;
            public int Copy;
            public bool Pictured;
        }

        /// <summary>The lists on screen: pictures are made only while there is one (<see cref="MapThumbnails"/>).</summary>
        static readonly List<MapTileList> Showing = new List<MapTileList>();

        MissionEditorNewMenuV2 _menu;
        NewMissionMapButton _template;
        RectTransform _content;
        ScrollRect _scroll;
        float _cellWidth, _cellHeight, _gap, _window;
        bool _built, _failed;
        readonly List<Tile> _tiles = new List<Tile>();
        readonly HashSet<NewMissionMapButton> _ours = new HashSet<NewMissionMapButton>();
        readonly Dictionary<NewMissionMapButton, Tile> _byButton = new Dictionary<NewMissionMapButton, Tile>();
        Coroutine _waiting;

        // The grid in the order shown, the game's tiles included; the custom tiles whose pictures are asked for
        // and not yet in; and what the pictures and moves were last worked out for (LateUpdate).
        readonly List<NewMissionMapButton> _grid = new List<NewMissionMapButton>();
        readonly List<Tile> _pending = new List<Tile>();
        int _nearFirst = -1, _nearLast = -1, _shownFirst = -1, _shownLast = -1;
        GameObject _selected;
        bool _picturesStale = true, _movesStale = true, _picturesFailed, _movesFailed;

        /// <summary>Whether a panel's list is on screen.</summary>
        internal static bool AnyShowing
        {
            get
            {
                Showing.RemoveAll(list => list == null);
                return Showing.Count > 0;
            }
        }

        /// <summary>Gives the panel its list, unless it has one or the plugin has no map to offer.</summary>
        internal static void Attach(MissionEditorNewMenuV2 menu)
        {
            if (menu == null || menu.GetComponent<MapTileList>() != null) return;
            if (BundleLoader.Ready && BundleLoader.Maps.Count == 0) return;

            MapTileList list = menu.gameObject.AddComponent<MapTileList>();
            list._menu = menu;
            list.Sync();
        }

        void Awake() => _menu = GetComponent<MissionEditorNewMenuV2>();

        void OnEnable()
        {
            if (!Showing.Contains(this)) Showing.Add(this);

            try
            {
                Sync();
                ScrollToSelected();
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"New Mission panel: {e.GetType().Name}: {e.Message}");
            }
        }

        // A coroutine stops with its object, and the pictures still to make are dropped with the last list on
        // screen; the next showing asks again for what it shows.
        void OnDisable()
        {
            Showing.Remove(this);
            _waiting = null;
            _pending.Clear();
            _picturesStale = true;
            _movesStale = true;
        }

        /// <summary>Adds the custom maps that have no tile yet, and brings every custom tile up to date.
        /// Safe to call at any time.</summary>
        internal void Sync()
        {
            if (_failed || _menu == null) return;

            // The first start after a map is installed hashes it for a few seconds; reading the maps now
            // would wait for that on the main thread. The panel shows the game's maps meanwhile.
            if (!BundleLoader.Ready)
            {
                if (_waiting == null && isActiveAndEnabled) _waiting = StartCoroutine(SyncWhenReady());
                return;
            }

            // Nothing else puts the custom maps in the game's list on this path: the registrar runs when
            // the game asks to load, name or check a map.
            MapRegistrar.Ensure(_menu.mapLoader);

            List<Tile> wanted = Wanted();
            if (wanted.Count > 0)
            {
                if (!_built && !Build())
                {
                    _failed = true;
                    return;
                }
                Add(wanted);
            }

            if (!_built) return;
            Refresh();
        }

        IEnumerator SyncWhenReady()
        {
            while (!BundleLoader.Ready) yield return null;
            _waiting = null;

            try
            {
                Sync();
                ScrollToSelected();
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"New Mission panel: {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>A tile for each custom map the game's list offers that no tile shows yet, several under the
        /// testing option <c>Advanced.NewMissionTileCopies</c>. A map some other tile already shows (a game
        /// update or another mod's) gets none; maps other mods add get none either.</summary>
        List<Tile> Wanted()
        {
            var wanted = new List<Tile>();

            var shown = new HashSet<string>(StringComparer.Ordinal);
            foreach (NewMissionMapButton button in _menu.mapButtons ?? Array.Empty<NewMissionMapButton>())
                if (button != null && button.mapDetails != null && button.mapDetails.PrefabName != null)
                    shown.Add(button.mapDetails.PrefabName);

            MapDetails[] offered = _menu.mapLoader != null ? _menu.mapLoader.Maps : null;
            if (offered == null) return wanted;

            int copies = Plugin.NewMissionTileCopies != null ? Mathf.Clamp(Plugin.NewMissionTileCopies.Value, 1, 50) : 1;

            foreach (LoadedMap map in BundleLoader.Maps)
            {
                if (map.Details == null || map.PrefabName == null || shown.Contains(map.PrefabName)) continue;
                if (Array.FindIndex(offered, d => d != null && d.PrefabName == map.PrefabName) < 0) continue;

                for (int copy = 0; copy < copies; copy++)
                    wanted.Add(new Tile { Map = map, Copy = copy });
            }

            return wanted;
        }

        /// <summary>
        /// Puts the game's row of tiles into a scrolling list, in the row's place. Every size is read from the
        /// panel; a panel not laid out as expected is left as it is, with one warning. Nothing of the game's is
        /// touched until the list is built whole.
        /// </summary>
        bool Build()
        {
            NewMissionMapButton[] buttons = _menu.mapButtons;
            string why = Unexpected(buttons, out RectTransform row, out RectTransform column, out VerticalLayoutGroup columnLayout);
            if (why != null)
            {
                Plugin.LogWarning($"the New Mission panel is not laid out as this build of Custom Maps expects ({why}); " +
                                  "custom maps are not offered there. Set MapKey.Path by hand as the README says");
                return false;
            }

            _template = buttons[0];
            Vector2 cell = ((RectTransform)_template.transform).rect.size;
            _cellWidth = cell.x;
            _cellHeight = cell.y;

            var rowLayout = row.GetComponent<HorizontalOrVerticalLayoutGroup>();
            _gap = rowLayout != null && rowLayout.spacing >= 0f && !float.IsInfinity(rowLayout.spacing) ? rowLayout.spacing : MapTiles.FallbackGap;

            // The column as laid out now, and the row's middle in it, which the tiles keep.
            LayoutRebuilder.ForceRebuildLayoutImmediate(column);
            RectOffset padding = columnLayout.padding;
            var others = new List<float>();
            foreach (Transform child in column)
            {
                if (child == row || !child.gameObject.activeSelf || Ignored(child) || !(child is RectTransform rect)) continue;
                others.Add(columnLayout.childControlHeight ? LayoutUtility.GetPreferredHeight(rect) : rect.sizeDelta.y);
            }

            float height = MapTiles.ListHeight(column.rect.height, padding.top, padding.bottom, columnLayout.spacing,
                                               others, _cellHeight, _gap);
            float inner = column.rect.width - padding.horizontal;
            float width = MapTiles.ViewportWidth(_cellWidth, _gap);
            _window = MapTiles.ViewportHeight(height);

            float columnMiddle = column.rect.xMin + padding.left + inner / 2f;
            float rowMiddle = row.localPosition.x + (0.5f - row.pivot.x) * row.rect.width;
            float centre = rowMiddle - columnMiddle;
            if (float.IsNaN(centre) || float.IsInfinity(centre)) centre = 0f;

            // Kept inside the column with its scrollbar, where it fits at all.
            float most = inner / 2f - MapTiles.ScrollbarGap - MapTiles.ScrollbarWidth - width / 2f;
            float least = width / 2f - inner / 2f;
            if (least <= most) centre = Mathf.Clamp(centre, least, most);

            int layer = row.gameObject.layer;
            var rootObject = new GameObject("CustomMaps Map List", typeof(RectTransform));
            rootObject.SetActive(false);
            rootObject.layer = layer;

            RectTransform content;
            try
            {
                var root = (RectTransform)rootObject.transform;
                root.SetParent(column, false);
                root.anchorMin = root.anchorMax = new Vector2(0f, 1f);
                root.pivot = new Vector2(0.5f, 0.5f);
                root.sizeDelta = new Vector2(inner, height);
                var element = rootObject.AddComponent<LayoutElement>();
                element.minHeight = height;
                element.preferredHeight = height;

                // The window: one row and a little of the next, reaching the margin above the list so that the
                // first row's border is not cut, and clipping what scrolls past it. Its clear image takes the
                // wheel and drags over the gaps between tiles.
                RectTransform viewport = Child("Viewport", root, layer);
                viewport.anchorMin = new Vector2(0.5f, 0f);
                viewport.anchorMax = new Vector2(0.5f, 1f);
                viewport.pivot = new Vector2(0.5f, 1f);
                viewport.anchoredPosition = new Vector2(centre, MapTiles.Margin);
                viewport.sizeDelta = new Vector2(width, MapTiles.Margin);
                var catcher = viewport.gameObject.AddComponent<Image>();
                catcher.color = Color.clear;
                catcher.raycastTarget = true;
                viewport.GetComponent<CanvasRenderer>().cullTransparentMesh = true;
                viewport.gameObject.AddComponent<RectMask2D>();

                content = Child("Content", viewport, layer);
                content.anchorMin = new Vector2(0f, 1f);
                content.anchorMax = new Vector2(1f, 1f);
                content.pivot = new Vector2(0.5f, 1f);
                content.anchoredPosition = Vector2.zero;
                content.sizeDelta = new Vector2(0f, MapTiles.ContentHeight(buttons.Length, _cellHeight, _gap));

                int margin = Mathf.RoundToInt(MapTiles.Margin);
                var grid = content.gameObject.AddComponent<GridLayoutGroup>();
                grid.padding = new RectOffset(margin, margin, margin, margin);
                grid.cellSize = cell;
                grid.spacing = new Vector2(_gap, _gap);
                grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
                grid.startAxis = GridLayoutGroup.Axis.Horizontal;
                // childAlignment left at its default, upper left, which the grid fills exactly.
                grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
                grid.constraintCount = MapTiles.Columns;

                var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
                fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
                fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

                Scrollbar bar = BuildScrollbar(root, layer, MapTiles.ScrollbarX(centre, width));

                // As the Load tab's list scrolls, but with its bar always shown: with three maps or more there is
                // always more below, and the bar says so.
                var scroll = rootObject.AddComponent<ScrollRect>();
                scroll.content = content;
                scroll.viewport = viewport;
                scroll.horizontal = false;
                scroll.vertical = true;
                scroll.movementType = ScrollRect.MovementType.Clamped;
                scroll.inertia = true;
                scroll.decelerationRate = 0.135f;
                // A notch moves the same share of a row as in the Load list, whose rows are a quarter as tall.
                scroll.scrollSensitivity = MapTiles.ScrollSensitivity(_cellHeight, _gap);
                scroll.verticalScrollbar = bar;
                scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
                scroll.verticalScrollbarSpacing = 0f;
                _scroll = scroll;
            }
            catch (Exception e)
            {
                Destroy(rootObject);
                Plugin.LogWarning($"could not build the New Mission panel's map list ({e.GetType().Name}: {e.Message}); " +
                                  "custom maps are not offered there");
                return false;
            }

            // The swap: the list in the row's place, the game's tiles moved into it (their clicks stay with them),
            // and the emptied row hidden. mapListParent still names the row, which the game never reads.
            int place = row.GetSiblingIndex();
            try
            {
                rootObject.transform.SetSiblingIndex(place);
                foreach (NewMissionMapButton button in buttons)
                {
                    button.transform.SetParent(content, false);
                    Focus(button);
                }
                row.gameObject.SetActive(false);
                rootObject.SetActive(true);
            }
            catch (Exception e)
            {
                foreach (NewMissionMapButton button in buttons)
                    if (button != null) button.transform.SetParent(row, false);
                row.gameObject.SetActive(true);
                Destroy(rootObject);
                Plugin.LogWarning($"could not put the New Mission panel's map list in place ({e.GetType().Name}: {e.Message}); " +
                                  "custom maps are not offered there");
                return false;
            }

            _content = content;
            _built = true;

            if (Plugin.DebugEnabled)
                Plugin.LogDebug($"New Mission panel ({gameObject.scene.name}): map list {height:0} high under '{column.name}' " +
                                $"({column.rect.width:0}x{column.rect.height:0}, {others.Count + 1} children), window " +
                                $"{width:0}x{_window:0} {centre:0.#} from the middle, tiles {_cellWidth:0}x{_cellHeight:0} {_gap:0} apart" +
                                (MapTiles.Fits(centre, width, inner) ? "" : $"; it does not fit {inner:0} wide"));
            return true;
        }

        /// <summary>Why the panel is not the one the list is built for, or null if it is.</summary>
        static string Unexpected(NewMissionMapButton[] buttons, out RectTransform row, out RectTransform column,
                                 out VerticalLayoutGroup columnLayout)
        {
            row = null;
            column = null;
            columnLayout = null;

            if (buttons == null || buttons.Length == 0) return "it has no map tiles";
            foreach (NewMissionMapButton button in buttons)
                if (button == null) return "one of its map tiles is gone";

            row = buttons[0].transform.parent as RectTransform;
            if (row == null) return "its map tiles are not in a row";
            foreach (NewMissionMapButton button in buttons)
                if (button.transform.parent != row) return "its map tiles are not in one row";
            foreach (Transform child in row)
                if (child.gameObject.activeSelf && Array.IndexOf(buttons, child.GetComponent<NewMissionMapButton>()) < 0)
                    return $"the map row holds '{child.name}' as well as the tiles";

            column = row.parent as RectTransform;
            if (column == null) return "the map row is not in a column";
            columnLayout = column.GetComponent<VerticalLayoutGroup>();
            if (columnLayout == null) return $"'{column.name}' is not a vertical layout";

            NewMissionMapButton first = buttons[0];
            if (first.image == null || first.text == null || first.Button == null) return "its first tile has no picture, name or button";

            Vector2 cell = ((RectTransform)first.transform).rect.size;
            if (!(cell.x > 0f) || !(cell.y > 0f)) return "its tiles have no size";
            return null;
        }

        static bool Ignored(Transform child)
        {
            foreach (ILayoutIgnorer ignorer in child.GetComponents<ILayoutIgnorer>())
                if (ignorer.ignoreLayout && (!(ignorer is Behaviour behaviour) || behaviour.enabled)) return true;
            return false;
        }

        static RectTransform Child(string name, Transform parent, int layer)
        {
            var go = new GameObject(name, typeof(RectTransform)) { layer = layer };
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            return rect;
        }

        /// <summary>The Load tab's vertical scrollbar, built from its values: 18 wide, its track and handle colours
        /// and tint, bottom to top. <paramref name="x"/> is its left edge from the list's middle. Left out of
        /// controller and keyboard navigation, which moves between tiles and scrolls to each.</summary>
        static Scrollbar BuildScrollbar(RectTransform root, int layer, float x)
        {
            RectTransform track = Child("Scrollbar Vertical", root, layer);
            track.anchorMin = new Vector2(0.5f, 0f);
            track.anchorMax = new Vector2(0.5f, 1f);
            track.pivot = new Vector2(0f, 1f);
            track.anchoredPosition = new Vector2(x, 0f);
            track.sizeDelta = new Vector2(MapTiles.ScrollbarWidth, 0f);
            track.gameObject.AddComponent<Image>().color = new Color(0.102f, 0.114f, 0.129f, 1f);

            RectTransform sliding = Child("Sliding Area", track, layer);
            sliding.anchorMin = Vector2.zero;
            sliding.anchorMax = Vector2.one;
            sliding.anchoredPosition = Vector2.zero;
            sliding.sizeDelta = new Vector2(-6f, -6f);

            RectTransform handle = Child("Handle", sliding, layer);
            handle.sizeDelta = new Vector2(6f, 6f);
            var handleImage = handle.gameObject.AddComponent<Image>();
            handleImage.color = new Color(0.286f, 0.314f, 0.357f, 1f);

            var bar = track.gameObject.AddComponent<Scrollbar>();
            bar.handleRect = handle;
            bar.targetGraphic = handleImage;
            bar.direction = Scrollbar.Direction.BottomToTop;
            bar.transition = Selectable.Transition.ColorTint;
            bar.colors = new ColorBlock
            {
                normalColor = Color.white,
                highlightedColor = new Color(0.961f, 0.961f, 0.961f, 1f),
                pressedColor = new Color(0.784f, 0.784f, 0.784f, 1f),
                selectedColor = new Color(0.961f, 0.961f, 0.961f, 1f),
                disabledColor = new Color(0.784f, 0.784f, 0.784f, 0.502f),
                colorMultiplier = 1f,
                fadeDuration = 0.1f,
            };
            bar.navigation = new Navigation { mode = Navigation.Mode.None };
            return bar;
        }

        /// <summary>A tile for each wanted map: a copy of the game's first tile given the map by the game's own
        /// <c>SetMap</c>, a click that selects it through the game's <c>MapSelected</c>, and a place in
        /// <c>mapButtons</c> so the game greys or highlights it with the others. One that cannot be made is
        /// left out, with a warning, and takes no other with it.</summary>
        void Add(List<Tile> wanted)
        {
            var added = new List<NewMissionMapButton>(wanted.Count);
            foreach (Tile tile in wanted)
            {
                NewMissionMapButton button = null;
                try
                {
                    // A copy takes none of the game's click listeners, which are added at runtime; the tile
                    // has no saved ones.
                    button = Instantiate(_template, _content, false);
                    button.name = $"MapButtonV2 ({tile.Map.PrefabName})";

                    MapDetails details = tile.Map.Details;
                    if (details.MapImage == null) details.MapImage = MapThumbnails.For(tile.Map) ?? MapThumbnails.Placeholder;
                    button.SetMap(details);

                    // Names stay at the game's size and shrink only where they would not fit the tile.
                    TextMeshProUGUI text = button.text;
                    float size = text.fontSize;
                    text.enableAutoSizing = true;
                    text.fontSizeMax = size;
                    text.fontSizeMin = Mathf.Min(SmallestName, size);

                    button.Button.onClick.AddListener(() => _menu.MapSelected(details));
                    Focus(button);
                    button.OnMapSelected(_menu.selectedMap);

                    tile.Button = button;
                    _tiles.Add(tile);
                    _ours.Add(button);
                    _byButton[button] = tile;
                    added.Add(button);
                }
                catch (Exception e)
                {
                    Plugin.LogWarning($"{tile.Map.Name()}: could not add its tile to the New Mission panel " +
                                      $"({e.GetType().Name}: {e.Message})");
                    if (button != null) Destroy(button.gameObject);
                }
            }

            if (added.Count == 0) return;

            NewMissionMapButton[] before = _menu.mapButtons;
            var all = new NewMissionMapButton[before.Length + added.Count];
            before.CopyTo(all, 0);
            added.CopyTo(all, before.Length);
            _menu.mapButtons = all;

            var names = new List<string>();
            foreach (NewMissionMapButton button in added)
                if (!names.Contains(button.mapDetails.PrefabName)) names.Add(button.mapDetails.PrefabName);
            Plugin.LogInfo($"New Mission panel ({gameObject.scene.name}): {names.Count} custom map(s) offered as tiles " +
                           $"({added.Count} tile(s)): {string.Join(", ", names)}");
        }

        /// <summary>Names, order and state of the custom tiles: the game's tiles first in their order, then the
        /// custom ones by name (<see cref="MapTiles.Order"/>, <see cref="MapTiles.Labels"/>); a map whose prefab
        /// failed to load this session greyed out and named so, and not left selected.</summary>
        void Refresh()
        {
            _tiles.RemoveAll(t => t.Button == null);

            var gameTiles = new List<NewMissionMapButton>();
            var gameNames = new List<string>();
            foreach (NewMissionMapButton button in _menu.mapButtons)
            {
                if (button == null || _ours.Contains(button)) continue;
                gameTiles.Add(button);
                gameNames.Add(button.mapDetails != null ? button.mapDetails.MapName : null);
            }

            var keys = new List<MapTiles.Tile>(_tiles.Count);
            foreach (Tile tile in _tiles)
                keys.Add(new MapTiles.Tile(tile.Map.Details.MapName, tile.Map.Manifest?.MapId, tile.Map.PrefabName,
                                           tile.Map.Manifest?.Version, tile.Copy));

            string[] labels = MapTiles.Labels(gameNames, keys);
            int[] order = MapTiles.Order(keys);

            int index = 0;
            foreach (NewMissionMapButton button in gameTiles)
                if (button.transform.parent == _content) button.transform.SetSiblingIndex(index++);

            bool selectedFailed = false;
            foreach (int i in order)
            {
                Tile tile = _tiles[i];
                bool failed = MapFixups.IsRejected(tile.Map);
                tile.Button.text.text = failed ? labels[i] + " (failed to load)" : labels[i];
                tile.Button.Button.interactable = !failed;
                tile.Button.transform.SetSiblingIndex(index++);
                if (failed && _menu.selectedMap == tile.Map.Details) selectedFailed = true;
            }

            if (selectedFailed && _menu.mapLoader != null && _menu.mapLoader.Maps.Length > 0)
                _menu.MapSelected(_menu.mapLoader.Maps[0]);

            // The content's height for the scroll view now, not at the end of the frame, so that a tile scrolled
            // to in this frame is not clamped back to the old height.
            LayoutRebuilder.ForceRebuildLayoutImmediate(_content);

            _grid.Clear();
            foreach (Transform child in _content)
            {
                var button = child.GetComponent<NewMissionMapButton>();
                if (button != null && child.gameObject.activeSelf) _grid.Add(button);
            }
            _picturesStale = true;
            _movesStale = true;
        }

        // Each frame, cheaply: the pictures of rows coming into reach asked for and those ready put in; the moves
        // worked out again when the rows in the window, the selection or the tiles change. Each apart, so that a
        // failure of one leaves the other.
        void LateUpdate()
        {
            if (!_built || _content == null) return;
            float scroll = _content.anchoredPosition.y;

            if (!_picturesFailed)
            {
                try
                {
                    TrackPictures(scroll);
                }
                catch (Exception e)
                {
                    _picturesFailed = true;
                    Plugin.LogWarning($"New Mission panel: {e.GetType().Name}: {e.Message}; its map tiles keep the pictures they have");
                }
            }

            if (!_movesFailed)
            {
                try
                {
                    TrackMoves(scroll);
                }
                catch (Exception e)
                {
                    _movesFailed = true;
                    Plugin.LogWarning($"New Mission panel: {e.GetType().Name}: {e.Message}; its map tiles keep the keyboard " +
                                      "and controller moves they have");
                }
            }
        }

        void TrackPictures(float scroll)
        {
            float pitch = _cellHeight + _gap;
            MapTiles.RowsOverlapping(MapTiles.Rows(_grid.Count), _cellHeight, _gap, MapTiles.Margin, scroll - pitch,
                                     scroll + _window + pitch, out int first, out int last);
            if (_picturesStale || first != _nearFirst || last != _nearLast)
            {
                _picturesStale = false;
                _nearFirst = first;
                _nearLast = last;
                RequestPictures(first, last);
            }

            if (_pending.Count > 0) DeliverPictures();
        }

        void TrackMoves(float scroll)
        {
            MapTiles.RowsCentred(MapTiles.Rows(_grid.Count), _cellHeight, _gap, MapTiles.Margin, _window, scroll,
                                 out int first, out int last);
            EventSystem events = EventSystem.current;
            GameObject selected = events != null ? events.currentSelectedGameObject : null;
            if (_movesStale || first != _shownFirst || last != _shownLast || selected != _selected)
            {
                _movesStale = false;
                _shownFirst = first;
                _shownLast = last;
                _selected = selected;
                Navigate(first, last, selected);
            }
        }

        /// <summary>Asks for the pictures of the custom tiles in rows <paramref name="first"/> to
        /// <paramref name="last"/>, top first: those in the window and a row either side. A long list's are
        /// made as it is scrolled to them, not all at once when the panel first shows.</summary>
        void RequestPictures(int first, int last)
        {
            if (first > last) return;

            int end = Math.Min(_grid.Count, (last + 1) * MapTiles.Columns);
            for (int i = first * MapTiles.Columns; i < end; i++)
            {
                NewMissionMapButton button = _grid[i];
                if (button == null || !_byButton.TryGetValue(button, out Tile tile)) continue;
                if (tile.Pictured || _pending.Contains(tile) || Picture(tile)) continue;

                MapThumbnails.Request(tile.Map, this);
                _pending.Add(tile);
            }
        }

        void DeliverPictures()
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                Tile tile = _pending[i];
                if (tile.Button == null || tile.Pictured || Picture(tile)) _pending.RemoveAt(i);
            }
        }

        /// <summary>
        /// Keyboard and controller moves between the tiles. The game's tiles move as Unity's automatic navigation
        /// has them: to the nearest control the way pressed, among every one on the panel, clipped or not
        /// (<c>Selectable.FindSelectable</c>). In a list that scrolls, that picks a tile scrolled out of the window,
        /// which then sits over the faction toggles above the list, from those toggles. So a tile whose middle is
        /// outside the window is out of navigation (<c>Navigation.Mode.None</c>), unless it is the one selected;
        /// and a tile that shows moves to its neighbour in the grid (<see cref="MapTiles.Neighbour"/>), even one
        /// out of the window, which scrolls into view as it is selected (<see cref="MapTileFocus"/>), and off the
        /// grid wherever Unity's navigation would go from it, which no longer sees the hidden tiles. A greyed-out
        /// tile is passed over.
        /// </summary>
        void Navigate(int shownFirst, int shownLast, GameObject selected)
        {
            int count = _grid.Count;
            var reach = new bool[count];

            // Every tile in or out first, so that the moves off the grid worked out below see only those in.
            for (int i = 0; i < count; i++)
            {
                NewMissionMapButton tile = _grid[i];
                if (tile == null || tile.Button == null) continue;

                int row = i / MapTiles.Columns;
                reach[i] = (row >= shownFirst && row <= shownLast) || tile.gameObject == selected;
                if (!reach[i])
                    tile.Button.navigation = new Navigation { mode = Navigation.Mode.None };
                else if (tile.Button.navigation.mode == Navigation.Mode.None)
                    tile.Button.navigation = new Navigation { mode = Navigation.Mode.Automatic };
            }

            for (int i = 0; i < count; i++)
            {
                if (!reach[i]) continue;

                Button from = _grid[i].Button;
                from.navigation = new Navigation
                {
                    mode = Navigation.Mode.Explicit,
                    selectOnUp = Move(i, 0, -1, from, Vector3.up),
                    selectOnDown = Move(i, 0, 1, from, Vector3.down),
                    selectOnLeft = Move(i, -1, 0, from, Vector3.left),
                    selectOnRight = Move(i, 1, 0, from, Vector3.right),
                };
            }
        }

        Selectable Move(int index, int dx, int dy, Selectable from, Vector3 direction)
        {
            int to = MapTiles.Neighbour(index, _grid.Count, dx, dy, Usable);
            return to >= 0 ? _grid[to].Button : from.FindSelectable(from.transform.rotation * direction);
        }

        bool Usable(int index)
        {
            NewMissionMapButton tile = _grid[index];
            return tile != null && tile.Button != null && tile.Button.IsInteractable() && tile.gameObject.activeInHierarchy;
        }

        /// <summary>Puts the map's picture in the tile if it is ready: the sprite only, so the label stays.</summary>
        bool Picture(Tile tile)
        {
            Sprite sprite = MapThumbnails.For(tile.Map);
            if (sprite == null) return false;

            tile.Map.Details.MapImage = sprite;
            tile.Button.image.sprite = sprite;
            tile.Pictured = true;
            return true;
        }

        void Focus(NewMissionMapButton button)
        {
            // A copy of a tile already has one, pointing nowhere: copies do not keep it.
            var focus = button.GetComponent<MapTileFocus>();
            if (focus == null) focus = button.gameObject.AddComponent<MapTileFocus>();
            focus.List = this;
        }

        /// <summary>Scrolls the selected map's tile into view, if it has one here.</summary>
        void ScrollToSelected()
        {
            if (!_built || _menu == null || _menu.selectedMap == null) return;

            foreach (Transform child in _content)
            {
                var button = child.GetComponent<NewMissionMapButton>();
                if (button != null && button.mapDetails == _menu.selectedMap)
                {
                    ScrollIntoView(child);
                    return;
                }
            }
        }

        /// <summary>
        /// Scrolls the least that shows the tile whole, with its border (<see cref="MapTiles.ScrollFor"/>), from
        /// its place in the grid, so it needs no layout pass. Nothing in the game scrolls a list to the selected
        /// item, so without this a controller or the arrow keys could select a tile hidden below the window.
        /// </summary>
        internal void ScrollIntoView(Transform tile)
        {
            if (!_built || _content == null || _scroll == null || tile == null || tile.parent != _content) return;

            int index = -1, count = 0;
            foreach (Transform child in _content)
            {
                if (!child.gameObject.activeSelf) continue;
                if (child == tile) index = count;
                count++;
            }
            if (index < 0) return;

            float contentHeight = MapTiles.ContentHeight(count, _cellHeight, _gap);
            Vector2 at = _content.anchoredPosition;
            float y = MapTiles.ScrollFor(index / MapTiles.Columns, _window, _cellHeight, _gap, MapTiles.Margin, contentHeight, at.y);
            if (Mathf.Approximately(y, at.y)) return;

            _scroll.StopMovement();
            _content.anchoredPosition = new Vector2(at.x, y);
        }
    }

    /// <summary>
    /// On each tile in the list: scrolls it into view when it is selected from the keyboard or a controller,
    /// which may move to a tile out of the window (<see cref="MapTileList.Navigate"/>), and when it is clicked.
    /// A click selects a tile when the button goes down, but it is scrolled to only when the click is done, so
    /// that the tile does not move from under the pointer before the click lands. Wheel and drags are not taken
    /// here: they go on up to the list.
    /// </summary>
    internal sealed class MapTileFocus : MonoBehaviour, ISelectHandler, IPointerClickHandler
    {
        internal MapTileList List;

        public void OnSelect(BaseEventData eventData)
        {
            if (eventData is PointerEventData || List == null) return;
            List.ScrollIntoView(transform);
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (List != null && eventData.button == PointerEventData.InputButton.Left) List.ScrollIntoView(transform);
        }
    }
}
