using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomMaps
{
    /// <summary>
    /// The pictures on the custom maps' tiles in the mission editor's New Mission panel (<see cref="MapTileList"/>).
    ///
    /// The game's own tiles show a 512 by 512 picture each (<c>terrain1_thumbnail</c>,
    /// <c>terrain_naval_thumbnail</c>), set in their <c>MapDetails.MapImage</c>, which nothing else in the
    /// game reads. A bundle has no such picture, so one is made, once per build of a map, and kept in
    /// BepInEx's cache folder (<see cref="MapTiles.ThumbnailFolderName"/>), so that every later start reads a
    /// small PNG and never touches the bundle for it. In order:
    ///
    ///  1. the picture already made this session (the main menu's panel and the editor's share them);
    ///  2. the cached PNG of this build of the map;
    ///  3. made from the bundle: its colour map (<c>assets.terrainColorMap</c>, which Map Forge writes at
    ///     1024 by 1024, a coloured relief with lakes and snow); else its chart (<c>assets.mapImage</c>), for a
    ///     bundle without a colour map. Each is read on Unity's loading thread, and said nothing about if the
    ///     bundle has none;
    ///  4. else a plain dark square, the Load menu's own colour (<see cref="Placeholder"/>), with one warning:
    ///     the tile then shows the map's name only.
    ///
    /// Made on the graphics card, so a compressed or unreadable source of any size will do: the middle square
    /// of the source is drawn into a render texture and halved down to 512 (<see cref="MapTiles.DownscaleSteps"/>),
    /// read back, darkened where the tile's name sits as far as the game's own pictures are there and no
    /// further (<see cref="MapTiles.DarkenFor"/>), written as a PNG, and that PNG loaded as a cached one is, so
    /// the first session and every later one show the same picture. On the card as DXT1 with mipmaps, about
    /// 170 KB a map. The source is let go as soon as the picture is made (<see cref="Release"/>), so that
    /// making many maps' pictures holds one source at a time.
    ///
    /// Main thread only, and only while a panel shows (<see cref="MapTileList.AnyShowing"/>): the panel asks
    /// for the rows in and next to its window as they come into reach (<see cref="Request"/>) and takes what
    /// is ready (<see cref="For"/>). The work runs one map a frame on the plugin's own object, so a panel
    /// closed halfway does not leave a picture half made; what is left when no panel shows is dropped, and
    /// asked for again when one does, so a mission started from the panel does not have pictures made
    /// through its loading.
    /// </summary>
    internal static class MapThumbnails
    {
        /// <summary>The Load menu's dark tile colour (its inactive style's image colour).</summary>
        static readonly Color PlaceholderColour = new Color(0.173f, 0.192f, 0.220f, 1f);

        static readonly Dictionary<string, Sprite> _made = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        static readonly List<LoadedMap> _queue = new List<LoadedMap>();
        static readonly HashSet<string> _queued = new HashSet<string>(StringComparer.Ordinal);
        static readonly HashSet<string> _warned = new HashSet<string>(StringComparer.Ordinal);

        static Sprite _placeholder;
        static MonoBehaviour _host;
        static int _worker;
        static bool _running;

        /// <summary>A plain dark square: what a tile shows until its picture is ready, and for good when none
        /// can be made. Never the plain white an image with no sprite draws.</summary>
        internal static Sprite Placeholder
        {
            get
            {
                if (_placeholder != null) return _placeholder;

                var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false)
                {
                    name = "CustomMaps tile placeholder",
                    hideFlags = HideFlags.HideAndDontSave,
                    wrapMode = TextureWrapMode.Clamp,
                };
                var pixels = new Color[16];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = PlaceholderColour;
                texture.SetPixels(pixels);
                texture.Apply(false, true);

                _placeholder = Sprite.Create(texture, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);
                _placeholder.name = texture.name;
                _placeholder.hideFlags = HideFlags.HideAndDontSave;
                return _placeholder;
            }
        }

        /// <summary>The map's picture, or the placeholder if none could be made; null while it is still to
        /// be made.</summary>
        internal static Sprite For(LoadedMap map)
        {
            if (map?.PrefabName == null) return null;
            return _made.TryGetValue(map.PrefabName, out Sprite sprite) && sprite != null ? sprite : null;
        }

        /// <summary>
        /// Asks for the map's picture, if it is not made or on its way. Asked in the order the tiles are shown,
        /// so the top rows get theirs first. The work runs on the plugin's object, or on
        /// <paramref name="fallback"/> if that has gone.
        /// </summary>
        internal static void Request(LoadedMap map, MonoBehaviour fallback)
        {
            if (map?.PrefabName == null || For(map) != null) return;
            if (_queued.Add(map.PrefabName)) _queue.Add(map);

            // The worker stops with its object: a panel's when it is closed, which it is only when the
            // plugin's own has gone. It is started again here, and does again what it was halfway through.
            if (_running && _host != null && _host.isActiveAndEnabled) return;

            MonoBehaviour host = Plugin.Instance != null && Plugin.Instance.isActiveAndEnabled ? Plugin.Instance : fallback;
            if (host == null || !host.isActiveAndEnabled) return;

            _host = host;
            _running = true;
            host.StartCoroutine(Work(++_worker));
        }

        static IEnumerator Work(int id)
        {
            while (_queue.Count > 0 && id == _worker)
            {
                if (!MapTileList.AnyShowing)
                {
                    Plugin.LogDebug($"New Mission pictures: no panel shows them, {_queue.Count} left for when one does");
                    _queue.Clear();
                    _queued.Clear();
                    break;
                }

                LoadedMap map = _queue[0];
                IEnumerator make = Make(map);

                while (true)
                {
                    bool more;
                    try
                    {
                        more = make.MoveNext();
                    }
                    catch (Exception e)
                    {
                        _made[map.PrefabName] = Placeholder;
                        if (_warned.Add(map.PrefabName))
                            Plugin.LogWarning($"{map.Name()}: could not make its picture for the New Mission panel " +
                                              $"({e.GetType().Name}: {e.Message}); its tile shows the name only");
                        more = false;
                    }

                    if (!more) break;
                    yield return make.Current;
                    if (id != _worker) yield break;
                }

                // Made, or a placeholder for good; or, if no panel showed it any more, nothing, and asked for
                // again when one does.
                _queue.Remove(map);
                _queued.Remove(map.PrefabName);

                // One picture a frame: a cached one is a PNG decoded and sent to the card, a new one a few
                // draws, a read-back and a PNG written.
                yield return null;
            }

            if (id == _worker) _running = false;
        }

        static IEnumerator Make(LoadedMap map)
        {
            long start = Stopwatch.GetTimestamp();
            string mapId = map.Manifest?.MapId;
            string file = CacheFile(map);

            if (file != null && File.Exists(file))
            {
                Sprite cached = null;
                try
                {
                    cached = FromPng(File.ReadAllBytes(file), mapId);
                }
                catch (Exception e)
                {
                    Plugin.LogDebug($"{map.Name()}: could not read its cached picture {file}: {e.Message}");
                }

                if (cached != null)
                {
                    _made[map.PrefabName] = cached;
                    Plugin.LogDebug($"{map.Name()}: New Mission picture from {file} in {Ms(start)} ms");
                    yield break;
                }

                // Unreadable: made again below, and written over.
                TryDelete(file);
            }

            // No graphics device: a dedicated server, which has no menu to show a tile in.
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                _made[map.PrefabName] = Placeholder;
                yield break;
            }

            Texture2D colourMap = null;
            Sprite chart = null;

            AssetBundleRequest colour = map.LoadQuietAsync<Texture2D>(map.Manifest?.TerrainColorMap);
            if (colour != null)
            {
                while (!colour.isDone) yield return null;
                colourMap = colour.asset as Texture2D;
            }

            if (colourMap == null)
            {
                // The chart is read only for a panel still showing: it can be 85 MiB.
                if (!MapTileList.AnyShowing) yield break;

                AssetBundleRequest request = map.LoadQuietAsync<Sprite>(map.Manifest?.MapImage);
                if (request != null)
                {
                    while (!request.isDone) yield return null;
                    chart = request.asset as Sprite;
                    if (chart != null && chart.texture == null)
                    {
                        Release(map, null, chart);
                        chart = null;
                    }
                }
            }

            Texture2D source = colourMap != null ? colourMap : chart != null ? chart.texture : null;
            if (source == null)
            {
                _made[map.PrefabName] = Placeholder;
                if (_warned.Add(map.PrefabName))
                    Plugin.LogWarning($"{map.Name()}: no picture for the New Mission panel (the bundle has no terrainColorMap " +
                                      "or mapImage); its tile shows the name only");
                yield break;
            }

            // The panel went while the source was read (a mission started from it, say): the source is let go
            // unused, and the picture made when a panel next shows the tile.
            if (!MapTileList.AnyShowing)
            {
                Release(map, colourMap, chart);
                yield break;
            }

            Rect rect = colourMap != null ? new Rect(0, 0, colourMap.width, colourMap.height) : SpriteRect(chart);
            string from = colourMap != null ? $"its terrainColorMap '{colourMap.name}'" : $"its mapImage '{chart.name}'";
            string size = $"{source.width}x{source.height}, {source.mipmapCount} mip level(s)";

            byte[] png;
            float factor;
            try
            {
                png = Render(source, rect, out factor);
            }
            finally
            {
                Release(map, colourMap, chart);
            }

            Sprite made = FromPng(png, mapId);
            if (made == null) throw new InvalidOperationException("the picture made from the bundle did not load back");

            _made[map.PrefabName] = made;
            string saved = file != null && Save(file, png, mapId) ? $", cached as {file}" : ", not cached";
            Plugin.LogDebug($"{map.Name()}: New Mission picture made from {from} ({size}), darkened x{factor:0.00}, " +
                            $"in {Ms(start)} ms{saved}");
        }

        /// <summary>
        /// Lets go of what a picture was made from. An asset read from a bundle stays in memory until the next
        /// scene load sweeps unused assets, so the first session after many maps were installed would otherwise
        /// hold every one's colour map in the menu, about 5 MiB each, or its chart, 43 to 85 MiB on Swiss Alps.
        /// Left alone once the map's prefab has been asked for (a warm-up, or the map played), which holds the
        /// same textures: the next scene load sweeps them if they are unused then.
        /// </summary>
        static void Release(LoadedMap map, Texture2D colourMap, Sprite chart)
        {
            if (map.PrefabRequested) return;

            try
            {
                Texture2D chartTexture = chart != null ? chart.texture : null;
                if (chart != null) Resources.UnloadAsset(chart);
                if (chartTexture != null) Resources.UnloadAsset(chartTexture);
                if (colourMap != null) Resources.UnloadAsset(colourMap);
            }
            catch (Exception e)
            {
                Plugin.LogDebug($"{map.Name()}: could not let go of its New Mission picture's source: {e.Message}");
            }
        }

        /// <summary>The source's middle square drawn down to <see cref="MapTiles.ThumbnailSize"/>, darkened by
        /// <paramref name="factor"/> (<see cref="MapTiles.DarkenFor"/>), as a PNG. The render textures are sRGB
        /// when the source is, so its bytes pass through as they are.</summary>
        static byte[] Render(Texture2D source, Rect rect, out float factor)
        {
            factor = 1f;
            bool srgb = source.isDataSRGB;
            RenderTextureReadWrite space = srgb ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear;
            int largest = source.mipmapCount > 1 ? MapTiles.LargestStep : MapTiles.LargestStepWithoutMips;
            int[] steps = MapTiles.DownscaleSteps((int)Math.Min(rect.width, rect.height), MapTiles.ThumbnailSize, largest);

            MapTiles.CentreSquare(rect.x, rect.y, rect.width, rect.height, source.width, source.height,
                                  out float scaleX, out float scaleY, out float offsetX, out float offsetY);

            RenderTexture previous = RenderTexture.active;
            RenderTexture target = null;
            Texture2D read = null;
            try
            {
                target = RenderTexture.GetTemporary(steps[0], steps[0], 0, RenderTextureFormat.ARGB32, space);
                target.filterMode = FilterMode.Bilinear;
                Graphics.Blit(source, target, new Vector2(scaleX, scaleY), new Vector2(offsetX, offsetY));

                for (int i = 1; i < steps.Length; i++)
                {
                    RenderTexture half = RenderTexture.GetTemporary(steps[i], steps[i], 0, RenderTextureFormat.ARGB32, space);
                    half.filterMode = FilterMode.Bilinear;
                    Graphics.Blit(target, half);
                    RenderTexture.ReleaseTemporary(target);
                    target = half;
                }

                int size = steps[steps.Length - 1];
                RenderTexture.active = target;
                read = new Texture2D(size, size, TextureFormat.RGB24, false, !srgb);
                read.ReadPixels(new Rect(0, 0, size, size), 0, 0, false);

                byte[] raw = read.GetRawTextureData();
                factor = MapTiles.DarkenFor(raw, size, size, 3);
                if (factor < 1f)
                {
                    MapTiles.Darken(raw, 3, factor);
                    read.LoadRawTextureData(raw);
                }

                return read.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = previous;
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (read != null) UnityEngine.Object.Destroy(read);
            }
        }

        /// <summary>A PNG as a tile's sprite: mipmapped, so it does not shimmer drawn at 160 while the list
        /// scrolls, compressed to DXT1, and on the card only. Null if it is not a picture.</summary>
        static Sprite FromPng(byte[] png, string mapId)
        {
            if (png == null || png.Length == 0) return null;

            // Decoded, then copied into a texture of known form: LoadImage may hand a PNG back with an alpha
            // channel, which compresses to DXT5 at twice DXT1's size, and may or may not keep mipmaps.
            var decoded = new Texture2D(2, 2, TextureFormat.RGB24, false, false);
            Texture2D texture;
            try
            {
                if (!decoded.LoadImage(png, false) || decoded.width < 4 || decoded.height < 4 ||
                    decoded.width > MapTiles.LargestStep || decoded.height > MapTiles.LargestStep)
                    return null;

                texture = new Texture2D(decoded.width, decoded.height, TextureFormat.RGB24, true, false);
                texture.SetPixels32(decoded.GetPixels32());
            }
            finally
            {
                UnityEngine.Object.Destroy(decoded);
            }

            texture.Apply(true);
            if (texture.width % 4 == 0 && texture.height % 4 == 0) texture.Compress(false);
            texture.Apply(false, true);

            texture.name = $"{mapId} New Mission picture";
            texture.wrapMode = TextureWrapMode.Clamp;
            texture.filterMode = FilterMode.Trilinear;
            texture.hideFlags = HideFlags.HideAndDontSave;

            Sprite sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f),
                                          100f, 0, SpriteMeshType.FullRect);
            sprite.name = texture.name;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        /// <summary>Writes the PNG whole or not at all, then removes the map's other pictures: older builds',
        /// older generations', and any left half written.</summary>
        static bool Save(string file, byte[] png, string mapId)
        {
            try
            {
                string folder = Path.GetDirectoryName(file);
                Directory.CreateDirectory(folder);

                string part = file + ".tmp";
                File.WriteAllBytes(part, png);
                if (File.Exists(file)) File.Delete(file);
                File.Move(part, file);

                foreach (string other in Directory.GetFiles(folder))
                    if (!string.Equals(other, file, StringComparison.OrdinalIgnoreCase) &&
                        MapTiles.IsThumbnailOf(Path.GetFileName(other), mapId))
                        TryDelete(other);

                return true;
            }
            catch (Exception e)
            {
                Plugin.LogDebug($"could not keep the picture {file} for the next start: {e.Message}");
                return false;
            }
        }

        /// <summary>Where this build of the map's picture is kept, or null if it cannot be.</summary>
        static string CacheFile(LoadedMap map)
        {
            string name = MapTiles.ThumbnailFileName(map.Manifest?.MapId, map.ContentHash);
            if (name == null) return null;

            try
            {
                string cache = BepInEx.Paths.CachePath;
                return string.IsNullOrEmpty(cache) ? null : Path.Combine(cache, MapTiles.ThumbnailFolderName, name);
            }
            catch (Exception e)
            {
                Plugin.LogDebug($"no BepInEx cache folder, so the New Mission pictures are made at every start: {e.Message}");
                return null;
            }
        }

        /// <summary>Where a sprite's pixels are in its texture. A sprite packed tightly with others has no
        /// rectangle of its own there; its rect then stands in.</summary>
        static Rect SpriteRect(Sprite sprite)
        {
            try { return sprite.textureRect; }
            catch (Exception) { return sprite.rect; }
        }

        static void TryDelete(string file)
        {
            try { File.Delete(file); }
            catch (Exception e) { Plugin.LogDebug($"could not delete {file}: {e.Message}"); }
        }

        static long Ms(long start) => (long)(LoadedMap.Seconds(start, Stopwatch.GetTimestamp()) * 1000);
    }
}
