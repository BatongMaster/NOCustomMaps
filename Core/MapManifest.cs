using System;
using System.Collections.Generic;
using System.Globalization;

namespace CustomMaps
{
    /// <summary>One validation finding. Errors block registration; warnings do not.</summary>
    public readonly struct MapProblem
    {
        public readonly bool IsError;
        public readonly string Field;
        public readonly string Message;

        public MapProblem(bool isError, string field, string message)
        {
            IsError = isError;
            Field = field;
            Message = message;
        }

        public override string ToString() => $"{(IsError ? "ERROR" : "WARN")} {Field}: {Message}";
    }

    /// <summary>A visual-only water surface at a fixed height, in map metres relative to
    /// the map centre.</summary>
    public sealed class LakeSurface
    {
        public string Name;
        public float SurfaceY;
        public float MinX, MinZ, MaxX, MaxZ;

        public float Width => MaxX - MinX;
        public float Depth => MaxZ - MinZ;

        public override string ToString()
            => $"{Name ?? "lake"} y={SurfaceY:0.#} [{MinX:0},{MinZ:0}]..[{MaxX:0},{MaxZ:0}]";
    }

    /// <summary>
    /// Everything the plugin needs to build a <c>MapSettings</c> at runtime.
    ///
    /// This exists because the AssetBundle deliberately contains <em>no game types</em>
    /// — only meshes, textures, sprites and a plain GameObject hierarchy. A bundle
    /// carrying a serialized <c>MapSettings</c> MonoBehaviour would bind by
    /// (assembly, namespace, class) and, on any game update that reorders the fields,
    /// deserialize to defaults <em>with no error at all</em>: <c>MapSize</c> would
    /// quietly become 0 and the map would load as an empty void. Describing the same
    /// data as JSON and assigning it through a publicized reference turns that silent
    /// corruption into a compile error at build time or a named validation failure at
    /// load time.
    /// </summary>
    public sealed class MapManifest
    {
        // --- identity ---
        public string MapId;
        public string DisplayName;
        public string Version;
        public string Author;

        /// <summary>Contents of the game's <c>build-hash.txt</c> when the bundle was built.
        /// Mismatch is a warning, not an error — most updates do not move these fields.</summary>
        public string BuiltAgainstGameHash;

        /// <summary>
        /// The data notice the map's sources require to travel with it, as one line of plain
        /// text. Empty for a map with nothing to credit.
        ///
        /// Not decoration. A map built from Copernicus DEM tiles and swisstopo's surveys
        /// carries two licences that each want a notice: the Copernicus licence dictates its
        /// wording for derived data and asks for it whenever the data is distributed or shown to
        /// the public, without saying where, and swisstopo asks for a source reference on any
        /// representation, publication or dissemination of its data, derived data included. What
        /// gets distributed is the bundle a player flies, not the repository it was built in. The
        /// game gives a map no description field to print this in
        /// (<c>NuclearOption.SceneLoading.MapDetails</c> has a prefab name, a map name and an
        /// image), so besides travelling in this field the credit is stamped where a player can
        /// meet it, onto the map's chart image, on open water where it hides nothing. This field
        /// is what that stamp is drawn from, and it is also the machine-readable copy: extract
        /// the bundle's <c>map</c> TextAsset and the credit is there in UTF-8, with its copyright
        /// signs intact, whatever the chart's ASCII font had to fold them into.
        ///
        /// Keep it to a caption. What a licence demands beyond the source reference — a
        /// dictated liability sentence, not implying endorsement (a disclaimer is the usual way),
        /// binding whoever is given the right to distribute the data or show it to the public to
        /// the same obligations — goes in <see cref="Notice"/>, which travels in the same file
        /// and is never drawn.
        /// </summary>
        public string Credits;

        /// <summary>
        /// Longest credit worth stamping on a chart: about four lines of a 2048² map image,
        /// which is the size every map's chart is built at.
        ///
        /// Not a hard limit on what the field may hold. The stamp never truncates; it drops to
        /// a smaller glyph and, failing that, to a plate in the corner. But a credit this long
        /// has stopped being a caption and has started to be the paperwork, and the paperwork
        /// belongs in <see cref="Notice"/>, which nothing has to draw. <see cref="Validate"/>
        /// says so rather than letting it happen quietly.
        ///
        /// How much a particular chart can actually take is a property of that map, because
        /// the stamp writes on open water and maps differ in how much they have.
        /// </summary>
        public const int MaxCreditsLength = 440;

        /// <summary>
        /// The obligations that travel with the map but are not drawn on it, as one line of
        /// plain text. Empty for a map that has none.
        ///
        /// A data licence usually asks for two different things, and only one of them is a
        /// caption. <see cref="Credits"/> is the caption — the source reference, short enough
        /// to stamp on the chart where a player can read it. The rest is prose: the Copernicus
        /// licence dictates a liability sentence, forbids implying that those behind its data
        /// officially endorse the activities of whoever uses it, this map's making and sharing
        /// among them, and has whoever lets others distribute the data or show it to the public,
        /// modified or not, make sure they are bound by the same obligations. Seven hundred and
        /// fifty characters of that will not go on a chart, and a chart is the wrong place for it.
        ///
        /// It is still the wrong answer to leave it in the repository. A <c>.nomap</c> is made
        /// to be copied — the server and every client need the identical file — so a notice
        /// that lives in a README beside the download is a notice that stops travelling the
        /// first time somebody passes the map on. This field is inside the bundle, in every
        /// copy of it, and nothing renders it: it is the paperwork, carried where it cannot be
        /// separated from the data it belongs to.
        /// </summary>
        public string Notice;

        /// <summary>
        /// Longest notice worth carrying. Nothing draws it, so the limit is not a layout
        /// question — it is a "somebody has pasted an entire licence into the manifest"
        /// question. The obligations of two data licences run to about a thousand characters;
        /// six times that is a mistake rather than a thorough map.
        /// </summary>
        public const int MaxNoticeLength = 6000;

        // --- geometry ---
        public float MapSizeX;
        public float MapSizeY;
        public int GridSizeX;
        public int GridSizeY;
        public int OffsetX;
        public int OffsetY;
        public float Latitude;

        /// <summary>
        /// Upper bound for the grass and detail height bake, in metres.
        ///
        /// <c>TerrainHeightMap.height</c> is a <c>MinMax</c> whose C# default is already
        /// (0, 5000); only the shipped maps serialise it down to 2,000, which is ample
        /// for Heartland but would cut the bake off less than half way up an alpine map.
        /// The field is public, so the plugin can raise it on the borrowed component.
        /// </summary>
        public float TerrainHeightMax = 2000f;

        // --- networking ---
        /// <summary>Baked, not recomputed at runtime, so server and client cannot disagree.
        /// <see cref="MapIdentity.AllocatePrefix"/> is the authoring-time default.</summary>
        public int MapPrefix;

        // --- bundle asset names ---
        public string RootPrefab;
        public string TerrainRoot;
        public string OceanBasecolor;
        public string OceanDepthmap;
        public string TerrainColorMap;

        /// <summary>Feeds <c>_macro_basecolor</c> on the game's terrain shader — the
        /// texture that actually colours the ground. Without it the map inherits the
        /// donor's imagery. Author at the shipped density (Heartland uses 8192² for
        /// 81,920 m, i.e. 10 m/texel).</summary>
        public string TerrainMacroColor;

        /// <summary>
        /// Ground-cover splat maps for <c>Shader Graphs/TerrainShader</c>: which detail
        /// texture it draws where — grass, bare rock, lush undergrowth, ploughed fields.
        ///
        /// A borrowed terrain material borrows the donor's, so a map that overrides only
        /// the macro colour wears the donor's ground cover sampled at its own coordinates.
        /// That put farmland up the side of the Alps and forest floor on cliffs: wrong from
        /// close up, invisible from far away.
        /// </summary>
        public string SplatGrass, SplatRock, SplatLush, SplatFields;

        /// <summary>
        /// The map's airbases, as a <c>TextAsset</c> of <see cref="AirbaseData"/>.
        ///
        /// Only where they go and what they are called. The airbase itself is borrowed from
        /// a shipped map at load, because <c>Airbase</c> is a game type and this bundle
        /// carries none.
        /// </summary>
        public string Airbases;

        public string MapImage;
        public string TreePositions;

        /// <summary>Baked building placements. See <see cref="CityData"/>.</summary>
        public string CityPlacements;
        public string SeaLanes;
        public string RoadNetwork;

        // --- free camera start ---
        public float CameraX, CameraY, CameraZ;
        public float CameraPitch, CameraYaw, CameraRoll;

        /// <summary>
        /// Still water bodies that sit above the datum and so need their own surface.
        ///
        /// The engine has exactly one water plane, at global y = 0, and every water
        /// behaviour — buoyancy, ship propulsion, the underwater camera, chaff and flare
        /// despawn — is a test against that height rather than against any mesh. A lake
        /// at a different altitude can therefore be rendered but not simulated. These
        /// are built as flat quads carrying the game's own water material, which is a
        /// deliberate trade: on a mountain map nothing needs to sail Lake Thun, and the
        /// alternative was flattening every lake onto one level that suits none of them.
        /// </summary>
        public List<LakeSurface> Lakes = new List<LakeSurface>();

        /// <summary><c>GridLabels</c> hard-codes a 10 km major cell and a 1 km minor cell.</summary>
        public const int MajorCellMetres = 10000;

        /// <summary>The background grid quads step every 4 major cells, so a size that is
        /// not a multiple of 4 leaves a partial column drawn off the edge of the map.</summary>
        public const int GridSizeMultiple = 4;

        /// <summary>
        /// A tactical grid for a map of this width that <see cref="Validate"/> accepts.
        ///
        /// The offset puts the map's low edge a whole number of major cells in, plus two for
        /// margin; the size is the fewest cells, in a multiple of four, that reach past the
        /// high edge. For 199,680 m that is 24 cells and 120,000 m — the values Swiss Alps was
        /// given by hand, which is what pins the rule.
        /// </summary>
        public static void DefaultGrid(float mapSize, out int gridSize, out int offset)
        {
            double half = Math.Max(0f, mapSize) * 0.5;

            offset = (int)(Math.Ceiling(half / MajorCellMetres) * MajorCellMetres) + 2 * MajorCellMetres;

            int cells = (int)Math.Ceiling((offset + half) / MajorCellMetres);
            gridSize = (cells + GridSizeMultiple - 1) / GridSizeMultiple * GridSizeMultiple;
        }

        public static MapManifest FromJson(string json)
        {
            var root = Json.AsObject(Json.Parse(json), "manifest");
            var camera = Json.Has(root, "camera") ? Json.AsObject(root["camera"], "camera") : null;
            var assets = Json.Has(root, "assets") ? Json.AsObject(root["assets"], "assets") : null;

            var lakes = new List<LakeSurface>();
            if (Json.Has(root, "lakes"))
                foreach (object entry in Json.AsArray(root["lakes"], "lakes"))
                {
                    var lake = Json.AsObject(entry, "lake");
                    lakes.Add(new LakeSurface
                    {
                        Name = Json.GetString(lake, "name"),
                        SurfaceY = Json.GetFloat(lake, "surfaceY"),
                        MinX = Json.GetFloat(lake, "minX"),
                        MinZ = Json.GetFloat(lake, "minZ"),
                        MaxX = Json.GetFloat(lake, "maxX"),
                        MaxZ = Json.GetFloat(lake, "maxZ"),
                    });
                }

            return new MapManifest
            {
                MapId = Json.GetString(root, "mapId"),
                DisplayName = Json.GetString(root, "displayName"),
                Version = Json.GetString(root, "version"),
                Author = Json.GetString(root, "author"),
                BuiltAgainstGameHash = Json.GetString(root, "builtAgainstGameHash"),
                Credits = Json.GetString(root, "credits"),
                Notice = Json.GetString(root, "notice"),

                MapSizeX = Json.GetFloat(root, "mapSizeX"),
                MapSizeY = Json.GetFloat(root, "mapSizeY"),
                GridSizeX = Json.GetInt(root, "gridSizeX", 16),
                GridSizeY = Json.GetInt(root, "gridSizeY", 16),
                OffsetX = Json.GetInt(root, "offsetX", 80000),
                OffsetY = Json.GetInt(root, "offsetY", 80000),
                Latitude = Json.GetFloat(root, "latitude", 45f),
                TerrainHeightMax = Json.GetFloat(root, "terrainHeightMax", 2000f),

                MapPrefix = Json.GetInt(root, "mapPrefix"),

                RootPrefab = Json.GetString(assets, "rootPrefab"),
                TerrainRoot = Json.GetString(assets, "terrainRoot", "Terrain"),
                OceanBasecolor = Json.GetString(assets, "oceanBasecolor"),
                OceanDepthmap = Json.GetString(assets, "oceanDepthmap"),
                TerrainColorMap = Json.GetString(assets, "terrainColorMap"),
                TerrainMacroColor = Json.GetString(assets, "terrainMacroColor"),
                SplatGrass = Json.GetString(assets, "splatGrass"),
                SplatRock = Json.GetString(assets, "splatRock"),
                SplatLush = Json.GetString(assets, "splatLush"),
                SplatFields = Json.GetString(assets, "splatFields"),
                Airbases = Json.GetString(assets, "airbases"),
                MapImage = Json.GetString(assets, "mapImage"),
                TreePositions = Json.GetString(assets, "treePositions"),
                CityPlacements = Json.GetString(assets, "cityPlacements"),
                SeaLanes = Json.GetString(assets, "seaLanes"),
                RoadNetwork = Json.GetString(assets, "roadNetwork"),

                CameraX = Json.GetFloat(camera, "x"),
                CameraY = Json.GetFloat(camera, "y", 530.8f),
                CameraZ = Json.GetFloat(camera, "z"),
                CameraPitch = Json.GetFloat(camera, "pitch"),
                CameraYaw = Json.GetFloat(camera, "yaw"),
                CameraRoll = Json.GetFloat(camera, "roll"),

                Lakes = lakes,
            };
        }

        /// <summary>The PrefabName this map will register under, given its bundle's content hash.</summary>
        public string PrefabNameFor(string contentHash) => MapIdentity.MakePrefabName(MapId, contentHash);

        /// <summary>
        /// Checks everything that can be checked without the game loaded.
        ///
        /// Returns problems rather than throwing so the diagnostics block can print
        /// them all at once — a manifest with three mistakes should not take three
        /// edit-build-launch cycles to fix.
        /// </summary>
        public List<MapProblem> Validate()
        {
            var problems = new List<MapProblem>();

            if (!MapIdentity.IsValidMapId(MapId))
                problems.Add(Error(nameof(MapId),
                    $"'{MapId}' is not a valid map id (2-32 chars, lowercase a-z, 0-9, hyphen, no leading or trailing hyphen)"));

            if (!MapIdentity.IsValidMapName(DisplayName, out string nameReason))
                problems.Add(Error(nameof(DisplayName), $"'{DisplayName}' is unusable as a lobby map name: {nameReason}"));

            ValidateAxis(problems, "X", MapSizeX, GridSizeX, OffsetX);
            ValidateAxis(problems, "Y", MapSizeY, GridSizeY, OffsetY);

            if (!MapIdentity.IsValidPrefix(MapPrefix))
                problems.Add(Error(nameof(MapPrefix),
                    $"{MapPrefix} is outside [{MapIdentity.MinPrefix}, {MapIdentity.MaxPrefix}]; " +
                    $"a prefix of 128 or more makes (MapPrefix << 24) negative, and 1-2 belong to the shipped maps"));

            if (Latitude < -90f || Latitude > 90f)
                problems.Add(Error(nameof(Latitude), $"{Latitude.ToString(CultureInfo.InvariantCulture)} is not a latitude"));

            if (string.IsNullOrEmpty(RootPrefab))
                problems.Add(Error(nameof(RootPrefab), "no root prefab name; nothing to instantiate"));

            // The terrain colour map is sampled on the CPU by MapSettings.GetTerrainColorAtCoordinate
            // via GetPixel, so its absence is a functional loss (ground unit and effect tinting),
            // not a crash.
            if (string.IsNullOrEmpty(TerrainColorMap))
                problems.Add(Warn(nameof(TerrainColorMap), "absent; ground-unit and effect terrain tinting will fall back to a default"));

            if (string.IsNullOrEmpty(MapImage))
                problems.Add(Warn(nameof(MapImage), "absent; the tactical map will render blank"));

            // The credit is stamped into the chart image, and the chart is only so wide. The
            // stamp never truncates — it drops to a smaller glyph and, failing that, to a
            // plate in the corner — but a credit past this length is one that has stopped
            // being a caption, and the detail belongs in Notice, which nothing has to draw.
            if (!string.IsNullOrEmpty(Credits) && Credits.Length > MaxCreditsLength)
                problems.Add(Warn(nameof(Credits),
                    $"{Credits.Length} characters; the chart stamp fits about {MaxCreditsLength}. Shorten it, " +
                    $"and put the obligations that are not a caption in {nameof(Notice)}"));

            if (!string.IsNullOrEmpty(Notice) && Notice.Length > MaxNoticeLength)
                problems.Add(Warn(nameof(Notice),
                    $"{Notice.Length} characters; nothing draws it, but past {MaxNoticeLength} this is a " +
                    "licence pasted into the manifest rather than the obligations that travel with the map"));

            // Ships path along SeaLanes. Without it naval AI cannot use the river network
            // at all, which for a river map is the whole point.
            if (string.IsNullOrEmpty(SeaLanes))
                problems.Add(Warn(nameof(SeaLanes), "absent; naval AI cannot path the waterways"));

            ValidateLakes(problems);

            return problems;
        }

        void ValidateLakes(List<MapProblem> problems)
        {
            if (Lakes == null) return;

            float halfX = MapSizeX * 0.5f, halfZ = MapSizeY * 0.5f;

            for (int i = 0; i < Lakes.Count; i++)
            {
                LakeSurface lake = Lakes[i];
                string field = $"Lakes[{i}]{(string.IsNullOrEmpty(lake.Name) ? "" : " " + lake.Name)}";

                // A surface at or below zero is already covered by the engine's own water
                // plane. A second one down there renders inside the first and z-fights.
                if (lake.SurfaceY <= 0f)
                    problems.Add(Error(field,
                        $"surface is at {lake.SurfaceY.ToString("0.#", CultureInfo.InvariantCulture)} m, " +
                        "at or below the datum — the engine's ocean plane already fills it"));

                if (lake.Width <= 0f || lake.Depth <= 0f)
                    problems.Add(Error(field, "has an empty footprint"));

                if (lake.MinX < -halfX || lake.MaxX > halfX || lake.MinZ < -halfZ || lake.MaxZ > halfZ)
                    problems.Add(Warn(field, "extends beyond the map bounds and will be clipped"));
            }
        }

        void ValidateAxis(List<MapProblem> problems, string axis, float size, int gridSize, int offset)
        {
            string sizeField = "MapSize" + axis;
            string gridField = "GridSize" + axis;
            string offsetField = "Offset" + axis;

            if (!(size > 0f))
            {
                problems.Add(Error(sizeField, "must be greater than zero"));
                return;
            }

            // No engine limit applies here — FloatingOrigin's ±90,000 m kill bound is
            // never invoked — so what is checked is whether the map can realistically be
            // built and shipped.
            if (size > GridMath.MaxSupportedMapSize)
                problems.Add(Error(sizeField,
                    $"{size.ToString(CultureInfo.InvariantCulture)} m is beyond anything workable " +
                    $"(limit {GridMath.MaxSupportedMapSize} m)"));
            else if (size > GridMath.LargeMapWarningSize)
                problems.Add(Warn(sizeField,
                    $"{size.ToString(CultureInfo.InvariantCulture)} m is a very large map: roughly " +
                    $"{GridMath.EstimateColliderMegabytes(size):N0} MB of cooked collider data alone, " +
                    "before meshes, textures or the bundle itself"));

            if (gridSize <= 0)
            {
                problems.Add(Error(gridField, "must be greater than zero"));
                return;
            }

            if (gridSize % GridSizeMultiple != 0)
                problems.Add(Warn(gridField,
                    $"{gridSize} is not a multiple of {GridSizeMultiple}; GridLabels draws its background quads " +
                    "every 4 major cells, so the last column will be clipped"));

            // GridLabels.GetGridPosition computes major = floor((offset ± coord) / 10000)
            // and returns "" unless 0 <= major < gridSize. Outside that band the grid
            // readout in the HUD silently goes blank.
            float half = size * 0.5f;
            if (offset < half)
                problems.Add(Error(offsetField,
                    $"{offset} is less than half the map ({half.ToString(CultureInfo.InvariantCulture)}); " +
                    "the low edge falls outside the grid and its coordinate readout will be blank"));

            float highEdge = offset + half;
            float gridExtent = (float)gridSize * MajorCellMetres;
            if (highEdge > gridExtent)
                problems.Add(Error(offsetField,
                    $"{offsetField} + MapSize{axis}/2 = {highEdge.ToString(CultureInfo.InvariantCulture)} exceeds " +
                    $"{gridField} * {MajorCellMetres} = {gridExtent.ToString(CultureInfo.InvariantCulture)}; " +
                    "the high edge falls outside the grid and its coordinate readout will be blank"));
        }

        static MapProblem Error(string field, string message) => new MapProblem(true, field, message);
        static MapProblem Warn(string field, string message) => new MapProblem(false, field, message);
    }
}
