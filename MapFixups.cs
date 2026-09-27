using System;
using System.Collections.Generic;
using NuclearOption.Effects;
using NuclearOption.SavedMission;
using NuclearOption.SceneLoading;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomMaps
{
    /// <summary>
    /// Turns a bundle of plain meshes and textures into something the game will load.
    ///
    /// The bundle deliberately contains no game types (see <see cref="MapManifest"/>),
    /// so <c>MapSettings</c> and <c>NetworkMap</c> are built here, at runtime, against
    /// a publicized reference. Everything happens on the <em>prefab</em> and exactly
    /// once, from the <c>EnableMap</c> prefix — the only moment when the GameWorld
    /// scene is loaded so the shipped maps are available as donors, and
    /// <c>Instantiate</c> has not yet run so no <c>Awake</c> has fired. Every
    /// subsequent load of the map is then free.
    /// </summary>
    internal static class MapFixups
    {
        static readonly Dictionary<string, MapSettings> _prepared =
            new Dictionary<string, MapSettings>(StringComparer.Ordinal);
        static readonly HashSet<string> _rejected = new HashSet<string>(StringComparer.Ordinal);

        static MapSettings _donor;
        static PhysicMaterial _terrainPhysicMaterial;
        static Material[] _terrainMaterials;
        static Material _donorTerrainMaterial;
        static bool _donorResolved;
        static bool _donorTerrainMaterialResolved;
        static uint _groundRenderingLayers;
        static bool _groundRenderingLayersResolved;

        /// <summary>The prepared prefab for a map, or null if it was rejected.</summary>
        public static MapSettings PreparedPrefabFor(LoadedMap map)
        {
            if (map?.PrefabName == null) return null;
            return _prepared.TryGetValue(map.PrefabName, out MapSettings prefab) ? prefab : null;
        }

        public static void PrepareAll(MapSettingsManager manager)
        {
            if (manager == null || Plugin.Disabled) return;
            if (BundleLoader.Maps.Count == 0) return;

            ResolveDonor(manager);

            foreach (LoadedMap map in BundleLoader.Maps)
            {
                if (map.PrefabName == null) continue;
                if (_prepared.ContainsKey(map.PrefabName) || _rejected.Contains(map.PrefabName)) continue;

                try
                {
                    MapSettings prefab = Prepare(map);
                    if (prefab != null) _prepared[map.PrefabName] = prefab;
                    else _rejected.Add(map.PrefabName);
                }
                catch (Exception e)
                {
                    _rejected.Add(map.PrefabName);
                    Plugin.LogError($"{map.Name()}: preparation threw, map will not be offered: {e}");
                }
            }
        }

        static MapSettings Prepare(LoadedMap map)
        {
            GameObject root = map.Root();
            if (root == null) return null;

            MapManifest m = map.Manifest;

            // MapSettingsManager.LoadMap calls SetActive(true) after instantiating, so
            // the shipped prefabs are stored inactive. Match that: an active prefab
            // would run every Awake on the instance before the game expects it to.
            root.SetActive(false);
            root.name = m.RootPrefab;

            MapSettings settings = root.GetComponent<MapSettings>() ?? root.AddComponent<MapSettings>();

            settings.MapSize = new Vector2(m.MapSizeX, m.MapSizeY);
            settings.GridSizeX = m.GridSizeX;
            settings.GridSizeY = m.GridSizeY;
            settings.OffsetX = m.OffsetX;
            settings.OffsetY = m.OffsetY;
            settings.Latitude = m.Latitude;

            settings.OceanBasecolor = map.Asset<Texture2D>(m.OceanBasecolor);
            settings.OceanDepthmap = map.Asset<Texture2D>(m.OceanDepthmap);
            settings.TerrainColorMap = map.Asset<Texture2D>(m.TerrainColorMap);
            settings.MapImage = map.Asset<Sprite>(m.MapImage);

            settings.CameraPositionRotation = new PositionRotation
            {
                Position = new GlobalPosition(m.CameraX, m.CameraY, m.CameraZ),
                Rotation = Quaternion.Euler(m.CameraPitch, m.CameraYaw, m.CameraRoll),
            };

            if (settings.ReflectionProbePoint == null)
                settings.ReflectionProbePoint = root.transform;

            // Airbases first, because they are the one thing on the map that has to be
            // registered with the network table, and PrepareNetworkMap is what writes it.
            List<Mirage.NetworkIdentity> networked = AirbaseBuilder.Build(root, map);

            PrepareNetworkMap(settings, m, networked);
            int rebound = RebindTerrain(root, m.TerrainRoot);
            BindLakeVolumes(root);
            BorrowMaterials(root, map);
            BorrowFactionMusic(settings);
            RoadNetworkFixup.Attach(settings, map);
            RoadNetworkFixup.AttachSeaLanes(settings, map);

            if (!MapDiagnostics.Validate(map, settings, rebound) && Plugin.ValidateOnLoad.Value)
            {
                Plugin.LogError($"{map.Name()}: failed validation, not offering it. " +
                                "Set ValidateOnLoad=false to load it anyway (this will not make it work).");
                return null;
            }

            return settings;
        }

        /// <summary>
        /// <c>NetworkMap</c>, holding whatever on this map has to exist for both sides.
        ///
        /// <c>ServerSpawn</c> and <c>ClientRegister</c> walk <c>NetworkObjects</c>
        /// registering <c>(MapPrefix &lt;&lt; 24) | index</c> as a <em>PrefabHash</em>,
        /// which requires the server and every client to agree on index → object. For a
        /// long time this shipped empty, and that was the right call while the map had
        /// nothing networked on it: everything a mission places is spawned dynamically
        /// through <c>ServerObjectManager</c> and never consults <c>NetworkMap</c>, so an
        /// empty table removed a whole class of desync for free.
        ///
        /// Airbases change that. An <c>Airbase</c> is a <c>NetworkBehaviour</c> and cannot
        /// work unregistered, so the table now carries them — and the agreement it needs
        /// turns out to be cheap here for a reason that does not generalise to a hand-built
        /// map: a custom map's version handshake is the SHA-256 of its own bundle, so two
        /// machines that disagree about what is in it cannot be in the same game. Given
        /// identical bundles and a fixed build order, both sides produce the same list.
        /// </summary>
        static void PrepareNetworkMap(MapSettings settings, MapManifest m,
                                      List<Mirage.NetworkIdentity> networked)
        {
            NetworkMap network = settings.NetworkMap
                                 ?? settings.GetComponentInChildren<NetworkMap>(true)
                                 ?? settings.gameObject.AddComponent<NetworkMap>();

            network.MapPrefix = m.MapPrefix;
            network.NetworkObjects = networked != null && networked.Count > 0
                ? networked.ToArray()
                : Array.Empty<Mirage.NetworkIdentity>();

            settings.NetworkMap = network;

            // The index is the hash, so overflowing the 24-bit field would silently alias
            // two objects onto one id.
            if (network.NetworkObjects.Length > 0xFFFFFF)
                Plugin.LogError($"{settings.name}: {network.NetworkObjects.Length} network objects is more " +
                                "than the 24-bit index can address; the map will desync.");
        }

        /// <summary>
        /// Puts every terrain collider on the Statics layer and gives it the base
        /// game's terrain PhysicMaterial.
        ///
        /// That material is the <em>only</em> thing that marks a mesh as terrain:
        /// <c>TerrainHeightMap.IsTerrain</c> compares <c>MeshCollider.sharedMaterial</c>
        /// against its <c>terrainPhysicMaterials</c> array (both shipped maps leave the
        /// parallel <c>terrainMaterials</c> array empty), and <c>TerrainScatter</c>
        /// independently compares against <c>gameAssets.terrainMaterial</c>. It has to
        /// be the base game's <em>instance</em>, not one with the same name, which is
        /// why it is copied off a donor rather than looked up by name.
        ///
        /// Returns how many colliders were rebound.
        /// </summary>
        static int RebindTerrain(GameObject root, string terrainRootName)
        {
            Transform scope = root.transform;
            if (!string.IsNullOrEmpty(terrainRootName))
            {
                Transform named = root.transform.Find(terrainRootName);
                if (named != null) scope = named;
                else Plugin.LogWarning($"{root.name}: no child named '{terrainRootName}', " +
                                       "rebinding every MeshCollider under the root instead");
            }

            MeshCollider[] colliders = scope.GetComponentsInChildren<MeshCollider>(true);
            int rebound = 0, relayered = 0, skipped = 0;

            foreach (MeshCollider collider in colliders)
            {
                // Road ribbons must keep their null PhysicMaterial — that is the entire
                // signal the vehicle job uses to grant on-road speed. They normally sit
                // outside this scope anyway, but the fallback above widens it to the whole
                // map when the terrain root cannot be found, and a warning nobody reads
                // would then quietly cost every vehicle its road speed.
                if (Under(collider.transform, RoadRoot)) { skipped++; continue; }
                if (Under(collider.transform, AirfieldRoot)) { skipped++; continue; }

                if (_terrainPhysicMaterial != null && collider.sharedMaterial != _terrainPhysicMaterial)
                {
                    collider.sharedMaterial = _terrainPhysicMaterial;
                    rebound++;
                }

                // Layer indices do survive a bundle round-trip — they serialize as ints,
                // not names — so this should report zero. A non-zero count means the
                // authoring project's layer list has drifted from the game's, which is
                // worth seeing rather than silently correcting.
                if (collider.gameObject.layer != PhysicsLayers.Statics)
                {
                    collider.gameObject.layer = PhysicsLayers.Statics;
                    relayered++;
                }
            }

            if (relayered > 0)
                Plugin.LogWarning($"{root.name}: moved {relayered} collider(s) to layer {PhysicsLayers.Statics} " +
                                  "(Statics) — the Unity project's Layers list does not match the game's");

            Plugin.LogDebug($"{root.name}: {colliders.Length} MeshCollider(s), {rebound} rebound to the terrain PhysicMaterial" +
                            (skipped > 0 ? $", {skipped} road ribbon(s) left unmaterialised" : ""));
            return rebound;
        }

        /// <summary>True if any ancestor, or the transform itself, carries this name.</summary>
        static bool Under(Transform transform, string name)
        {
            for (Transform at = transform; at != null; at = at.parent)
                if (at.name == name) return true;

            return false;
        }

        /// <summary>
        /// Fills the raised lakes with water: gives every <c>BoxCollider</c> under the map's
        /// <see cref="WaterRoot"/> the game's water PhysicMaterial and turns it on.
        ///
        /// The game's sea is a plane at global y = 0 and nearly all of its water behaviour tests
        /// that height, so a lake above it was a sheet an aircraft could fly under. One hook
        /// tests geometry instead, and nothing the game ships uses it:
        /// <c>AeroPart.OnTriggerStay</c> takes a part's depth in any trigger whose
        /// <c>sharedMaterial</c> is <c>GameAssets.i.WaterMaterial</c>
        /// (<c>Physics.ComputePenetration</c>), and <c>AeroJob_Math</c> runs the sea's water model
        /// on it: drag, water density, wings that stop lifting, buoyancy, the splash, and above
        /// 83 m/s the airframe breaking up. <c>BulletSim</c> plays the water impact on the same
        /// material, and <c>AeroPart</c>'s splash line-cast finds it. NOMapForge fills each lake's
        /// water with static trigger boxes (<c>Lake_&lt;name&gt;/volume_&lt;k&gt;</c>, layer
        /// Statics, from the surface to below the bed), and ships them switched off with no
        /// material, because the material has to be the game's own instance, compared by
        /// reference, and a bundle cannot carry that.
        ///
        /// The material is found first, and without it nothing is turned on. An enabled trigger
        /// with no water material is not water to anything: it is a floor to every raycast that
        /// hits triggers and armour to every bullet, over a lake that looks like water. So when
        /// <c>GameAssets</c> is not loaded, or has no water material, the volumes stay off and
        /// the lakes are what they were before: a sheet with nothing under it. This runs from the
        /// <c>EnableMap</c> prefix, long after <c>MainMenu</c> has preloaded <c>GameAssets</c>;
        /// the loader is asked first anyway, because <c>GameAssets.i</c> read too early logs an
        /// error of its own.
        ///
        /// A bundle built before the volumes has none, and this does nothing to it. Landing gear
        /// is kept from rolling on the water by <see cref="Patches.LandingGearTriggerPatch"/>.
        ///
        /// Returns how many volumes were turned on.
        /// </summary>
        static int BindLakeVolumes(GameObject root)
        {
            Transform water = root.transform.Find(WaterRoot);
            if (water == null) return 0;

            BoxCollider[] volumes = water.GetComponentsInChildren<BoxCollider>(true);
            if (volumes.Length == 0) return 0;

            PhysicMaterial material = null;
            try
            {
                material = GameWaterMaterial();
            }
            catch (Exception e)
            {
                // A game update that renamed the member: the lakes stay as they were rather than
                // the map failing to load.
                Plugin.LogWarning($"could not read GameAssets.WaterMaterial ({e.GetType().Name}: {e.Message})");
            }

            if (material == null)
            {
                foreach (BoxCollider volume in volumes) volume.enabled = false;

                Plugin.LogWarning($"{root.name}: the game's water PhysicMaterial (GameAssets.WaterMaterial) is not " +
                                  $"available, so the {volumes.Length} lake water volume(s) stay off and aircraft " +
                                  "can fly under the lakes");
                return 0;
            }

            foreach (BoxCollider volume in volumes)
            {
                volume.sharedMaterial = material;
                volume.isTrigger = true;
                volume.gameObject.layer = PhysicsLayers.Statics;
                volume.enabled = true;
            }

            Plugin.LogDebug($"{root.name}: {volumes.Length} lake water volume(s) under {water.childCount} lake(s) " +
                            $"carry '{material.name}' and are on");
            return volumes.Length;
        }

        /// <summary>
        /// <c>GameAssets.i.WaterMaterial</c>, or null while <c>GameAssets</c> is not loaded.
        ///
        /// A method of its own, never inlined, so that if a game update removes either member the
        /// failure to compile it is thrown at its call, inside <see cref="BindLakeVolumes"/>'s
        /// guard, rather than when the method preparing the whole map is compiled.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static PhysicMaterial GameWaterMaterial()
            => GameAssets.loader != null && GameAssets.loader.IsLoaded ? GameAssets.i.WaterMaterial : null;

        /// <summary>The water PhysicMaterial the lake volumes were given, or null: for the
        /// validation report.</summary>
        public static PhysicMaterial LakeWaterMaterial
        {
            get
            {
                try { return GameWaterMaterial(); }
                catch (Exception) { return null; }
            }
        }

        /// <summary>
        /// Clones the donor's detail-rendering objects into a <em>live</em> map instance.
        ///
        /// Grass does not come from the GameWorld scene — each shipped map carries its
        /// own <c>TerrainHeightMap</c>, <c>DetailRenderer</c>, <c>GrassRenderer</c> and
        /// <c>TreeRenderer</c> on a <c>DetailRenderer_Base</c> object inside the map
        /// prefab, plus a <c>ShaderGlobalManager</c> that publishes
        /// <c>_Datum_WorldExtent</c> from <c>MapSettings.MapSize</c>. All are
        /// <c>SceneSingleton</c>s that exist only while that map is loaded, so a map
        /// without them renders bare terrain with no error at all:
        /// <c>SceneSingleton&lt;TerrainHeightMap&gt;.i</c> is simply null.
        ///
        /// They reference compute shaders, render textures, grass meshes and materials
        /// that cannot be authored from outside the game, so they are cloned rather than
        /// rebuilt — <c>Object.Instantiate</c> remaps references <em>within</em> a cloned
        /// subtree, so DetailRenderer's link to its TerrainHeightMap survives.
        ///
        /// This runs against the instantiated map, not the prefab, because Unity refuses
        /// to parent a new object to a persistent one:
        /// <c>"Cannot instantiate objects with a parent which is persistent"</c> — the
        /// clone is silently created at the scene root instead, where it outlives the map
        /// and renders the donor's content over it.
        /// </summary>
        public static void BorrowDetailRenderers(MapSettings instance)
        {
            if (instance == null || Plugin.Disabled) return;
            if (!Plugin.BorrowDetailRenderer.Value) return;

            // Vanilla maps ship their own; this is exactly the test for "needs one",
            // independent of where the map came from.
            if (instance.GetComponentInChildren<TerrainHeightMap>(true) != null) return;

            if (_donor == null || ReferenceEquals(_donor, instance)) return;

            // Distinct donor objects hosting anything we are missing. TerrainHeightMap and
            // the renderers share DetailRenderer_Base, so this normally resolves to one
            // or two objects, but it is written not to care.
            var sources = new List<GameObject>();
            AddSource<TerrainHeightMap>(sources);
            AddSource<DetailRenderer>(sources);
            AddSource<BlastManager>(sources);
            AddSource<ShaderGlobalManager>(sources);

            if (sources.Count == 0)
            {
                Plugin.LogWarning($"{_donor.name} has nothing to borrow — custom terrain " +
                                  "will render without grass");
                return;
            }

            Transform terrain = FindTerrainRoot(instance);

            foreach (GameObject source in sources)
            {
                // Staged under an inactive holder so the clone's Awake is deferred until
                // its map references are wired. Instantiating it directly at the scene
                // root would make it active immediately, and every SceneSingleton on it
                // would register while still pointing at the donor's map.
                var holder = new GameObject("CustomMaps.Staging");
                holder.SetActive(false);

                try
                {
                    GameObject clone = UnityEngine.Object.Instantiate(source, holder.transform);
                    clone.name = source.name;                    // drop Unity's "(Clone)"

                    Rewire(clone, instance, terrain);
                    clone.transform.SetParent(instance.transform, worldPositionStays: false);

                    // Naming the singletons that came across removes the ambiguity of a
                    // single log line: they normally share one GameObject, so one line
                    // could equally mean "both borrowed" or "one silently not found".
                    var carried = new List<string>();
                    if (clone.GetComponentInChildren<TerrainHeightMap>(true) != null) carried.Add(nameof(TerrainHeightMap));
                    if (clone.GetComponentInChildren<DetailRenderer>(true) != null) carried.Add(nameof(DetailRenderer));
                    if (clone.GetComponentInChildren<ShaderGlobalManager>(true) != null) carried.Add(nameof(ShaderGlobalManager));

                    Plugin.LogDebug($"{instance.name}: borrowed '{clone.name}' from {_donor.name} " +
                                    $"[{string.Join(", ", carried)}]" +
                                    (terrain != null ? $" searchRoot={terrain.name}" : ""));
                }
                catch (Exception e)
                {
                    Plugin.LogWarning($"{instance.name}: could not clone '{source.name}', " +
                                      $"terrain may render without grass: {e.Message}");
                }
                finally
                {
                    UnityEngine.Object.Destroy(holder);
                }
            }

            RelinkDetailComponents(instance);
        }

        /// <summary>
        /// Repoints the detail components at each other after cloning.
        ///
        /// <c>Object.Instantiate</c> only remaps references that live <em>inside</em> the
        /// subtree being cloned. These components reference one another across
        /// GameObjects — <c>DetailRenderer.blastManager</c>,
        /// <c>DetailRenderer.terrainHeight</c>, <c>TerrainHeightMap.detailRenderer</c> —
        /// so anything cloned separately still points at the donor's copy, which is
        /// destroyed the moment the donor map unloads.
        ///
        /// The consequence is total and silent: <c>DetailRenderer.Start</c> opens with
        /// <c>this.blastManager.CommandSetup()</c>, so a stale reference throws there and
        /// every subsequent line — the argument buffers, the grass and tree setup, the
        /// call to <c>terrainHeight.CommandSetup()</c> that bakes the heightmap — never
        /// runs. Terrain renders perfectly and there is simply no grass.
        /// </summary>
        static void RelinkDetailComponents(MapSettings instance)
        {
            var blast = instance.GetComponentInChildren<BlastManager>(true);
            var height = instance.GetComponentInChildren<TerrainHeightMap>(true);
            var detail = instance.GetComponentInChildren<DetailRenderer>(true);

            var repaired = new List<string>();

            if (detail != null)
            {
                if (blast != null && detail.blastManager != blast)
                {
                    detail.blastManager = blast;
                    repaired.Add("DetailRenderer.blastManager");
                }
                if (height != null && detail.terrainHeight != height)
                {
                    detail.terrainHeight = height;
                    repaired.Add("DetailRenderer.terrainHeight");
                }
            }

            if (height != null && detail != null && height.detailRenderer != detail)
            {
                height.detailRenderer = detail;
                repaired.Add("TerrainHeightMap.detailRenderer");
            }

            if (blast != null) blast.mapSettings = instance;

            // Must happen before DetailRenderer.Start, which is why this runs here rather
            // than per-clone: Start sizes its argument buffers from treeRenderers.Length
            // and then calls CommandSetup on each one.
            RetargetDonorTrees(instance, detail);

            // Missing pieces are worth naming: without BlastManager there is no grass at
            // all, and the failure produces no error of its own.
            if (detail == null) Plugin.LogWarning($"{instance.name}: no DetailRenderer was borrowed — no grass");
            else if (blast == null) Plugin.LogWarning($"{instance.name}: no BlastManager was borrowed — DetailRenderer.Start will throw and grass will not render");

            Plugin.LogDebug($"{instance.name}: detail components " +
                            $"[blast={(blast != null ? "ok" : "MISSING")}, " +
                            $"height={(height != null ? "ok" : "MISSING")}, " +
                            $"detail={(detail != null ? "ok" : "MISSING")}]" +
                            (repaired.Count > 0 ? $" relinked {string.Join(", ", repaired)}" : " already linked"));

            if (height != null) ReportTerrainRecognition(instance, height);
        }

        /// <summary>
        /// Runs the game's own <c>IsTerrain</c> test over the map and reports the count.
        ///
        /// This exists because the failure it detects is completely silent. Grass is
        /// baked only from renderers that <c>TerrainHeightMap.AutoFindRenderers</c>
        /// accepts, and acceptance hinges on a detail that is easy to get wrong when
        /// authoring a prefab:
        ///
        ///     renderer.TryGetComponent&lt;MeshCollider&gt;(out c)  // the renderer's OWN GameObject
        ///
        /// with a fallback that matches the renderer's materials against
        /// <c>terrainMaterials</c>, which both shipped maps leave empty. Put the collider
        /// on a parent and the renderers on LOD children — an entirely reasonable
        /// layout — and every renderer fails both tests. Colliders are correct, physics
        /// is correct, the PhysicMaterial rebind reports 4/4, and there is simply no
        /// grass, with nothing in any log to say why.
        /// </summary>
        static void ReportTerrainRecognition(MapSettings instance, TerrainHeightMap height)
        {
            PhysicMaterial[] accepted = height.terrainPhysicMaterials;
            Material[] acceptedMaterials = height.terrainMaterials;

            int renderers = 0, recognised = 0;
            foreach (MeshRenderer renderer in instance.GetComponentsInChildren<MeshRenderer>(true))
            {
                renderers++;
                if (IsTerrainRenderer(renderer, accepted, acceptedMaterials)) recognised++;
            }

            if (recognised > 0)
            {
                Plugin.LogDebug($"{instance.name}: {recognised}/{renderers} renderer(s) recognised as terrain");
                return;
            }

            Plugin.LogWarning(
                $"{instance.name}: none of its {renderers} MeshRenderer(s) are recognised as terrain, " +
                "so no grass will render. TerrainHeightMap.IsTerrain requires a MeshCollider carrying the " +
                "terrain PhysicMaterial on the SAME GameObject as the MeshRenderer (its only fallback is " +
                "TerrainHeightMap.terrainMaterials, which the base game leaves empty). A tile with its " +
                "collider on a parent and its renderers on LOD children will fail this.");
        }

        static bool IsTerrainRenderer(MeshRenderer renderer, PhysicMaterial[] physicMaterials, Material[] materials)
        {
            if (renderer.TryGetComponent(out MeshCollider collider) && physicMaterials != null)
                foreach (PhysicMaterial candidate in physicMaterials)
                    if (collider.sharedMaterial == candidate) return true;

            if (materials == null) return false;

            foreach (Material assigned in renderer.sharedMaterials)
                foreach (Material candidate in materials)
                    if (assigned == candidate) return true;

            return false;
        }

        static void AddSource<T>(List<GameObject> sources) where T : Component
        {
            T component = _donor.GetComponentInChildren<T>(true);
            if (component == null) return;

            GameObject go = component.gameObject;
            if (!sources.Contains(go)) sources.Add(go);
        }

        static void Rewire(GameObject clone, MapSettings instance, Transform terrain)
        {
            float heightMax = HeightBakeCeilingFor(instance);
            int blockers = 0;

            foreach (TerrainHeightMap heightMap in clone.GetComponentsInChildren<TerrainHeightMap>(true))
            {
                heightMap.mapSettings = instance;

                // The donor serialises its bake range down to (0, 2000), which is ample
                // for Heartland and less than half way up an alpine map. Terrain above
                // the ceiling gets no height baked, so it grows no grass and no detail —
                // silently, since the bake simply has nothing to report.
                if (heightMax > heightMap.height.Max)
                    heightMap.height = new MinMax(heightMap.height.Min, heightMax);

                // AutoFindRenderers walks AutoSearchRoot's children for MeshRenderers whose
                // collider carries the terrain PhysicMaterial. Left at its default it
                // searches its own transform and finds nothing.
                heightMap.AutoSearchRoot = terrain != null ? terrain : instance.transform;
                heightMap.AutoFindTerrain = true;

                blockers += BlockGrass(heightMap, instance.transform);
            }

            foreach (ShaderGlobalManager globals in clone.GetComponentsInChildren<ShaderGlobalManager>(true))
                globals.mapSettings = instance;

            foreach (TerrainScatter scatter in clone.GetComponentsInChildren<TerrainScatter>(true))
                scatter.mapSettings = instance;

            // Silent when it fails, like the terrain recognition beside it, and with the same
            // consequence: grass growing through the tarmac with nothing to say why.
            if (blockers > 0) Plugin.LogInfo($"grass blocked by {blockers} paved surface(s)");
            else if (clone.transform.Find(RoadRoot) != null)
                Plugin.LogWarning("no paved surface registered as a grass blocker; grass will grow on the roads");
        }

        /// <summary>
        /// Registers the map's paved surfaces as grass blockers, and returns how many.
        ///
        /// Grass does not test colliders. <c>GrassRenderer</c> reads
        /// <c>TerrainHeightMap.blockerMap</c>, which <c>BakeWindow</c> clears to white and
        /// darkens only where it draws the meshes of entries registered as
        /// <c>EntryType.Blocker</c>. Those come from <c>AutoFindRenderers</c>, which walks
        /// <c>AutoSearchRoot</c> and registers anything that is not terrain as a blocker, but
        /// only when <c>AutoFindBlockers</c> is set, and the root is pointed at the terrain
        /// subtree anyway. So on this map nothing was ever a blocker and grass grew through
        /// every road on it.
        ///
        /// Widening the search root instead would be a trap: the terrain's own LOD1 and LOD2
        /// meshes are renderer-only children with no collider, so <c>IsTerrain</c> rejects
        /// them and they would register as blockers over the whole map.
        ///
        /// Only the finest level of each surface is registered. The coarser ones cover the
        /// same ground, and bridges and tunnels are left out on purpose: a tunnel liner lies
        /// under the hill and a deck well above it, so blocking on either would shave a
        /// grassless stripe along a mountainside that has a road inside it.
        /// </summary>
        static int BlockGrass(TerrainHeightMap heightMap, Transform root)
        {
            int registered = 0;

            foreach (string branch in new[] { RoadRoot, AirfieldRoot })
            {
                Transform under = root.Find(branch);
                if (under == null) continue;

                foreach (MeshRenderer renderer in under.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (renderer.name.EndsWith("_LOD1", StringComparison.Ordinal) ||
                        renderer.name.EndsWith("_LOD2", StringComparison.Ordinal)) continue;

                    if (Under(renderer.transform, BridgeRoot) || Under(renderer.transform, TunnelRoot)) continue;

                    heightMap.RegisterObject(new TerrainHeightMap.RenderFilter(
                        renderer, TerrainHeightMap.SubmeshFilter.All, TerrainHeightMap.EntryType.Blocker));
                    registered++;
                }
            }

            return registered;
        }

        /// <summary>
        /// Points the donor's tree renderers at this map's forests, or takes them out.
        ///
        /// The clone arrives with Heartland's baked scatter: 549,867 positions spread
        /// over an 81,920 m map. Left alone on a map that has no terrain under them they
        /// stand in rows on open water, well outside the bounds — which is exactly what
        /// this looked like the first time.
        ///
        /// The renderer itself is worth keeping. It carries the tree mesh, the material,
        /// the two compute shaders and the culling setup, all of which are base-game
        /// assets we could not author without dragging URP into the authoring project.
        /// Only the positions are ours, and <c>PositionData</c> is exactly the seam:
        /// a <c>TextAsset</c> of raw little-endian <c>Vector3</c>s, twelve bytes each,
        /// with no header.
        ///
        /// A map with no scatter of its own still has to have the renderers taken out of
        /// <c>DetailRenderer.treeRenderers</c> rather than merely emptied. Clearing
        /// <c>PositionData</c> alone leaves them in the array, and
        /// <c>TreeRenderer.CommandSetup</c> dereferences it — so
        /// <c>DetailRenderer.Start</c> throws a NullReferenceException, and
        /// <c>LateUpdate</c> then throws an ArgumentNullException through
        /// <c>TreeRenderer.UpdatePositions</c> → <c>ComputeFrustumCulling.RunCull</c> on
        /// every single frame. Both abort the method partway, which takes the grass
        /// render calls further down with them. Removing trees badly costs you grass.
        /// </summary>
        static void RetargetDonorTrees(MapSettings instance, DetailRenderer detail)
        {
            TreeRenderer[] trees = instance.GetComponentsInChildren<TreeRenderer>(true);
            if (trees.Length == 0) return;

            TextAsset scatter = TreeScatterFor(instance, out string mapName);

            if (scatter != null)
            {
                foreach (TreeRenderer tree in trees)
                {
                    // Named so the log can tell "we replaced Heartland's" from "there was
                    // nothing there to replace", which are different bugs.
                    int donor = tree.PositionData != null ? tree.PositionData.bytes.Length / TreePositionStride : 0;
                    tree.PositionData = scatter;

                    Plugin.LogDebug($"{instance.name}: {tree.name}.PositionData <- '{scatter.name}' " +
                                    $"({scatter.bytes.Length / TreePositionStride:N0} trees, was {donor:N0})");
                }

                return;
            }

            // Emptying the array is the functional fix: DetailRenderer only ever touches
            // tree renderers by iterating it.
            if (detail != null) detail.treeRenderers = Array.Empty<TreeRenderer>();

            foreach (TreeRenderer tree in trees)
            {
                tree.enabled = false;
                UnityEngine.Object.Destroy(tree);
            }

            Plugin.LogDebug($"{instance.name}: removed {trees.Length} donor TreeRenderer(s) — " +
                            $"{mapName ?? "this map"} ships no scatter data, so it gets no trees");
        }

        /// <summary>Bytes per baked tree position: a little-endian <c>Vector3</c>.</summary>
        const int TreePositionStride = 12;

        /// <summary>
        /// This map's baked tree positions, or null if it ships none or ships them wrong.
        ///
        /// The length check is not defensive padding. Nothing in the engine validates
        /// this buffer — <c>TreeRenderer</c> hands it to a <c>GraphicsBuffer</c> and
        /// instances a mesh per stride — so a file that is not a whole number of
        /// positions produces a forest that is subtly, silently misplaced rather than an
        /// error. Refusing it here costs the map its trees and says why.
        /// </summary>
        static TextAsset TreeScatterFor(MapSettings instance, out string mapName)
        {
            mapName = null;

            foreach (LoadedMap map in BundleLoader.Maps)
            {
                MapSettings prefab = PreparedPrefabFor(map);
                if (prefab == null) continue;

                // The instance is a clone, so its name carries the prefab's with a suffix.
                if (!instance.name.StartsWith(prefab.name, StringComparison.Ordinal)) continue;

                mapName = map.Details?.MapName;

                string asset = map.Manifest?.TreePositions;
                if (string.IsNullOrEmpty(asset)) return null;

                TextAsset scatter = map.Asset<TextAsset>(asset);
                if (scatter == null)
                {
                    Plugin.LogWarning($"{instance.name}: manifest names tree positions '{asset}' " +
                                      "but the bundle has no TextAsset by that name — no trees");
                    return null;
                }

                int length = scatter.bytes.Length;
                if (length == 0 || length % TreePositionStride != 0)
                {
                    Plugin.LogWarning($"{instance.name}: '{asset}' is {length} bytes, which is not a whole " +
                                      $"number of {TreePositionStride}-byte positions — no trees");
                    return null;
                }

                return scatter;
            }

            return null;
        }

        /// <summary>Bake ceiling requested by whichever of our maps this instance came
        /// from, or 0 if it is not one of ours (leave the donor's value alone).</summary>
        static float HeightBakeCeilingFor(MapSettings instance)
        {
            foreach (LoadedMap map in BundleLoader.Maps)
            {
                MapSettings prefab = PreparedPrefabFor(map);
                if (prefab == null) continue;

                // The instance is a clone, so its name carries the prefab's with a suffix.
                if (instance.name.StartsWith(prefab.name, StringComparison.Ordinal))
                    return map.Manifest?.TerrainHeightMax ?? 0f;
            }
            return 0f;
        }

        static Transform FindTerrainRoot(MapSettings instance)
        {
            foreach (LoadedMap map in BundleLoader.Maps)
            {
                string name = map.Manifest?.TerrainRoot;
                if (string.IsNullOrEmpty(name)) continue;

                Transform found = instance.transform.Find(name);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Name of the map root's child holding the road ribbons.
        ///
        /// They live outside the terrain root on purpose: <see cref="RebindTerrain"/>
        /// gives every MeshCollider beneath that root the terrain PhysicMaterial, and the
        /// vehicle job reads on-road speed off exactly that comparison — so a ribbon
        /// under the terrain root would be silently turned back into a field.
        /// </summary>
        public const string RoadRoot = "Roads";

        /// <summary>The two subtrees under the roads that are not the ground: a deck stands
        /// over it and a liner lies under it. Named here because the grass blocker has to
        /// leave both alone (<see cref="BlockGrass"/>); the builders that make them are in the
        /// generator's Unity project.</summary>
        public const string BridgeRoot = "Bridges";
        public const string TunnelRoot = "Tunnels";

        /// <summary>The map root's children holding the lake surfaces and the hand-placed
        /// models, named by <c>MapForge.BuildLakes</c> and <c>MapProps.RootName</c>. Named here
        /// because neither is ground to the game's decals (<see cref="BorrowMaterials"/>).</summary>
        public const string WaterRoot = "Water";
        public const string PropsRoot = "Props";

        /// <summary>
        /// The rendering layers the shipped maps give their ground, used when the donor's
        /// terrain renderer cannot be read. Bit 1 is the one that matters: the game's scorch
        /// and crater projector (<c>scorchMarkDecal</c>, decal layer mask 2) paints only
        /// renderers that carry it. Every Terrain1 and Terrain_naval tile, road, asphalt,
        /// runway marking and naval bridge ships with 3; buildings keep 1.
        /// </summary>
        const uint GroundRenderingLayers = 3u;

        /// <summary>Materials named with this prefix are placeholders, to be replaced at
        /// load time with the equivalent base-game material.</summary>
        public const string BorrowMarker = "__BORROW__";

        /// <summary>Placeholder standing in for the game's water surface, used by lake
        /// quads that sit above the datum.</summary>
        public const string WaterMarker = "__BORROW__Water";

        /// <summary>Placeholder standing in for a paved surface, used by the road
        /// ribbons.</summary>
        public const string PavedMarker = "__BORROW__Paved";

        /// <summary>
        /// Name of the map root's child holding the airfield paving.
        ///
        /// Exempt from <see cref="RebindTerrain"/> for the same reason the road ribbons are:
        /// a null PhysicMaterial is what grants a ground vehicle paved speed, and giving the
        /// tarmac the terrain material would make an apron behave like a ploughed field.
        ///
        /// Deliberately not <c>AirbaseBuilder.AirbaseRoot</c>. That child holds the borrowed
        /// <c>Airbase</c> components and is created at load; this one ships in the bundle.
        /// </summary>
        public const string AirfieldRoot = "Airfields";

        /// <summary>Placeholder standing in for a runway surface.</summary>
        public const string RunwayMarker = "__BORROW__Runway";

        /// <summary>Placeholder standing in for taxiway and apron tarmac.</summary>
        public const string TarmacMarker = "__BORROW__Tarmac";

        /// <summary>Placeholder for structural trim: bridge parapets, tunnel liners. Resolved to
        /// the shipped <see cref="StructureMaterialName"/>.</summary>
        public const string StructureMarker = "__BORROW__Structure";

        /// <summary>Placeholder for bare concrete: bridge decks' edges and undersides, piers,
        /// tunnel portals. Resolved to the shipped <see cref="ConcreteMaterialName"/>.</summary>
        public const string ConcreteMarker = "__BORROW__Concrete";

        /// <summary>
        /// The shipped materials the structure markers borrow, by exact name. Measured on the
        /// game's own bridges and tunnels, which are bespoke meshes wearing the road surface,
        /// <c>buildings_trim1</c> and <c>concrete_paving</c>.
        /// </summary>
        public const string StructureMaterialName = "buildings_trim1";
        public const string ConcreteMaterialName = "concrete_paving";

        /// <summary>
        /// The shipped runway and apron surfaces, by exact name. Borrowed by name because the
        /// search by shape found the wrong ones: the widest flat airbase mesh wears the
        /// <c>runway_markings</c> decal atlas, and the road surface the aprons fell back to is a
        /// four-lane highway texture. The paving's texture coordinates are laid out for these two
        /// (runway1 about 55 by 442 m, asphalt 22.3 by 177 m).
        /// </summary>
        public const string RunwayMaterialName = "runway1";
        public const string TarmacMaterialName = "asphalt";

        /// <summary>
        /// The game's water material, taken from <c>LevelInfo</c>.
        ///
        /// Lakes above the datum are rendered as their own quads, and they have to use
        /// the same material as the ocean or they read as coloured glass. Sharing the
        /// instance also means they pick up the per-map ocean textures
        /// <c>ApplyMapSettings</c> assigns, so their shoreline fade comes from the same
        /// depth map — which works because that map is sampled by world XZ, and lakes do
        /// not overlap horizontally however different their heights.
        ///
        /// The one thing that must not transfer is <c>_OriginOffset</c>, which the ocean
        /// needs and a static lake does not, so the lakes are moved onto a private copy
        /// of this material at load — see <see cref="AnchorLakeWater"/>.
        /// </summary>
        static Material ResolveWaterMaterial()
        {
            if (_waterMaterialResolved) return _waterMaterial;
            _waterMaterialResolved = true;

            // LevelInfo is a NetworkSceneSingleton, not a plain SceneSingleton.
            LevelInfo level = NetworkSceneSingleton<LevelInfo>.i;
            if (level != null) _waterMaterial = level.waterMaterial;

            if (_waterMaterial == null)
                foreach (Material candidate in Resources.FindObjectsOfTypeAll<Material>())
                {
                    if (candidate == null || candidate.shader == null) continue;
                    if (!candidate.shader.name.Contains("WaterSurface")) continue;

                    _waterMaterial = candidate;
                    break;
                }

            if (_waterMaterial == null)
                Plugin.LogWarning("no water material found — lake surfaces will render untextured");
            else
                DescribeWaterShader(_waterMaterial);

            return _waterMaterial;
        }

        /// <summary>
        /// Logs the water shader's interface, once.
        ///
        /// Everything the lake surfaces depend on lives in a shader compiled into the
        /// game's bundles, where it cannot be read. What that shader does with
        /// <c>_OriginOffset</c> and <c>_macro_depth</c> was worked out from the calls
        /// that feed them, which is inference, not knowledge — and the consequence of
        /// being wrong is a lake that renders invisible rather than one that renders
        /// badly, because the depth map drives alpha.
        ///
        /// Unity will however tell us the property list at runtime. One block of log
        /// turns the inference into something checkable against the install the player
        /// actually has, which matters across game updates as much as it does today.
        /// </summary>
        static void DescribeWaterShader(Material water)
        {
            Shader shader = water.shader;
            if (shader == null) return;

            var report = new System.Text.StringBuilder();
            report.AppendLine($"water material '{water.name}' uses shader '{shader.name}'");

            int count = shader.GetPropertyCount();
            for (int i = 0; i < count; i++)
            {
                string name = shader.GetPropertyName(i);
                UnityEngine.Rendering.ShaderPropertyType type = shader.GetPropertyType(i);

                string value;
                switch (type)
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        value = water.GetColor(name).ToString("F4");
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                        value = water.GetVector(name).ToString("F2");
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        value = water.GetFloat(name).ToString("0.###");
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        Texture texture = water.GetTexture(name);
                        value = texture != null ? $"{texture.name} {texture.width}x{texture.height}" : "none";
                        break;
                    default:
                        value = "?";
                        break;
                }

                report.AppendLine($"    {type,-7} {name,-24} {value}");
            }

            Plugin.LogDebug(report.ToString().TrimEnd());
        }

        static Material _waterMaterial;
        static bool _waterMaterialResolved;

        static Material _pavedMaterial;
        static bool _pavedMaterialResolved;

        /// <summary>
        /// A base-game road surface, for the ribbons.
        ///
        /// Taken from a shipped map's own road geometry rather than from a runway. The
        /// first version borrowed <c>runway1</c>, on the reasoning that
        /// <c>Shader Graphs/Runway</c> was the only asphalt-like shader in the game — and
        /// it resolved, but drew flat untextured grey. A runway material is authored for
        /// a runway's geometry and UV scale, and ours is neither.
        ///
        /// Heartland does render roads: they live under a <c>terrain2_roads</c> root as
        /// real meshes with a real road material, which is precisely the thing being
        /// rebuilt here. The buildings parented under that root are skipped — a city
        /// block's wall material is not a road surface.
        /// </summary>
        static Material ResolvePavedMaterial()
        {
            if (_pavedMaterialResolved) return _pavedMaterial;
            _pavedMaterialResolved = true;

            foreach (MapSettings map in ShippedMaps())
            {
                foreach (Transform candidate in map.GetComponentsInChildren<Transform>(true))
                {
                    if (candidate.name.IndexOf("road", StringComparison.OrdinalIgnoreCase) < 0) continue;

                    foreach (MeshRenderer renderer in candidate.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        // Skip the city blocks that share this root.
                        if (renderer.GetComponentInParent<MapBuilding>(true) != null) continue;

                        Material material = renderer.sharedMaterial;
                        if (material == null || material.shader == null) continue;

                        _pavedMaterial = material;

                        var filter = renderer.GetComponent<MeshFilter>();
                        Plugin.LogDebug($"road surface borrowed: '{material.name}' " +
                                        $"(shader '{material.shader.name}') from " +
                                        $"{map.name}/{candidate.name}/{renderer.name}" +
                                        (filter?.sharedMesh != null ? $", mesh '{filter.sharedMesh.name}'" : ""));

                        DescribeRoadSurface(material, filter?.sharedMesh);
                        AllowRoadTextureToRepeat(material);
                        return _pavedMaterial;
                    }
                }
            }

            // A runway is the nearest thing left: authored for the wrong geometry, but
            // asphalt rather than grass.
            foreach (Material candidate in Resources.FindObjectsOfTypeAll<Material>())
            {
                if (candidate?.shader == null) continue;
                if (!candidate.shader.name.Contains("Runway")) continue;

                _pavedMaterial = candidate;
                Plugin.LogWarning($"no shipped road geometry found; falling back to '{candidate.name}', " +
                                  "which is authored for runways and may draw untextured");
                return _pavedMaterial;
            }

            _pavedMaterial = ResolveDonorTerrainMaterial();
            Plugin.LogWarning("no road or runway material found — ribbons will use the terrain material");
            return _pavedMaterial;
        }

        static readonly Dictionary<string, Material> _namedMaterials =
            new Dictionary<string, Material>(StringComparer.Ordinal);

        /// <summary>
        /// A shipped material found by its exact name, or null.
        ///
        /// Every material slot of every renderer under the shipped maps is searched, not just the
        /// first: the game's bridges and tunnels are multi-material meshes, and the trim and the
        /// concrete are rarely in slot zero. Anything still loaded is the fallback, which finds a
        /// material only a building uses. Cached per name, including a miss.
        /// </summary>
        static Material ResolveNamedShippedMaterial(string exactName)
        {
            if (_namedMaterials.TryGetValue(exactName, out Material cached)) return cached;

            Material found = null;
            string where = null;

            foreach (MapSettings shipped in ShippedMaps())
            {
                foreach (MeshRenderer renderer in shipped.GetComponentsInChildren<MeshRenderer>(true))
                {
                    foreach (Material material in renderer.sharedMaterials)
                    {
                        if (material == null || material.shader == null) continue;
                        if (!string.Equals(material.name, exactName, StringComparison.Ordinal)) continue;

                        found = material;
                        where = $"{shipped.name}/{renderer.name}";
                        break;
                    }

                    if (found != null) break;
                }

                if (found != null) break;
            }

            if (found == null)
            {
                foreach (Material material in Resources.FindObjectsOfTypeAll<Material>())
                {
                    if (material == null || material.shader == null) continue;
                    if (!string.Equals(material.name, exactName, StringComparison.Ordinal)) continue;

                    found = material;
                    where = "loaded materials";
                    break;
                }
            }

            _namedMaterials[exactName] = found;

            if (found != null)
                Plugin.LogDebug($"'{exactName}' borrowed (shader '{found.shader.name}') from {where}");
            else
                Plugin.LogWarning($"no shipped material named '{exactName}'; structures that want it use the road surface");

            return found;
        }

        static Material _runwayMaterial;
        static bool _runwayMaterialResolved;

        /// <summary>
        /// A base-game runway surface, for the airfields.
        ///
        /// Searched on the shipped airbases rather than by material name, because the surface
        /// wanted is whatever a runway is actually drawn with and the naming is the shipped
        /// maps' business. An airbase's widest flat mesh is its tarmac: props are small, and
        /// the strip is the one thing every airfield has.
        ///
        /// Borrowing this was tried once before for the road ribbons and abandoned — it
        /// "drew flat untextured grey", because a runway material is authored for a runway's
        /// UV scale and a road ribbon is not. That objection does not apply here: the
        /// airfield paving is generated, so its UVs are ours to set, and what it is being
        /// asked to surface is a runway.
        /// </summary>
        static Material ResolveRunwayMaterial()
        {
            if (_runwayMaterialResolved) return _runwayMaterial;
            _runwayMaterialResolved = true;

            Material widest = null;
            float widestArea = 0f;
            string found = null;

            foreach (MapSettings map in ShippedMaps())
            {
                foreach (Airbase airbase in map.GetComponentsInChildren<Airbase>(true))
                {
                    foreach (MeshFilter filter in airbase.GetComponentsInChildren<MeshFilter>(true))
                    {
                        Mesh mesh = filter.sharedMesh;
                        if (mesh == null) continue;

                        var renderer = filter.GetComponent<MeshRenderer>();
                        Material material = renderer != null ? renderer.sharedMaterial : null;
                        if (material == null || material.shader == null) continue;

                        // Flat and large: the tarmac, not a hangar. Measured off the mesh
                        // rather than the renderer because these prefabs are inactive, and
                        // Unity does not compute renderer bounds for an inactive object —
                        // which is how an earlier pass here measured every surface as empty.
                        Bounds local = mesh.bounds;
                        if (local.size.y > 20f) continue;

                        float area = local.size.x * local.size.z;
                        if (area <= widestArea) continue;

                        widestArea = area;
                        widest = material;
                        found = $"{map.name}/{airbase.name}/{filter.name}";
                    }
                }
            }

            if (widest == null)
            {
                Plugin.LogWarning("no shipped airbase surface found to borrow a runway material from; " +
                                  "the airfields will use the road surface instead");
                return null;
            }

            _runwayMaterial = widest;

            Plugin.LogDebug($"runway surface borrowed: '{widest.name}' (shader '{widest.shader.name}') " +
                            $"from {found}, over {widestArea / 1_000_000f:0.00} km2");

            AllowRoadTextureToRepeat(widest);
            return _runwayMaterial;
        }

        /// <summary>
        /// Makes the road texture tile.
        ///
        /// The ribbons run their V coordinate in real metres, so a road a kilometre long
        /// reaches V of fifty or more. A clamped texture samples its last row for all of
        /// it, and the road comes out one flat colour — which is exactly the symptom. A
        /// road texture is meant to repeat along its length, so switching the mode is
        /// restoring the intent rather than overriding it, and it costs the shipped maps
        /// nothing: their own meshes keep V inside the first tile either way.
        /// </summary>
        static void AllowRoadTextureToRepeat(Material material)
        {
            foreach (string property in new[] { "_BaseMap", "_MainTex" })
            {
                if (!material.HasProperty(property)) continue;

                Texture texture = material.GetTexture(property);
                if (texture == null || texture.wrapMode == TextureWrapMode.Repeat) continue;

                Plugin.LogDebug($"road texture '{texture.name}' was {texture.wrapMode}; " +
                                "setting it to Repeat so the surface tiles along the carriageway");
                texture.wrapMode = TextureWrapMode.Repeat;
            }
        }

        /// <summary>
        /// Reports how the borrowed road material expects to be mapped.
        ///
        /// The ribbons carry a correct road material and still draw as flat grey, which
        /// means the fault is in the coordinates rather than the surface. Everything that
        /// could cause it is measurable and none of it is readable from outside the game:
        /// how the shipped road meshes lay out their own UVs, what the material scales
        /// them by, and whether the texture repeats or clamps — a clamped texture with our
        /// V running to several hundred would sample one edge row forever, which is
        /// exactly what a flat grey road looks like.
        /// </summary>
        static void DescribeRoadSurface(Material material, Mesh mesh)
        {
            var report = new System.Text.StringBuilder();
            report.AppendLine($"road surface '{material.name}':");

            Texture main = material.mainTexture;
            report.AppendLine($"    mainTexture     {(main != null ? $"{main.name} {main.width}x{main.height}, wrap {main.wrapMode}, filter {main.filterMode}" : "none")}");
            report.AppendLine($"    tiling/offset   {material.mainTextureScale} / {material.mainTextureOffset}");

            foreach (string property in new[] { "_BaseMap", "_MainTex", "_BaseColor", "_Color" })
            {
                if (!material.HasProperty(property)) continue;

                if (property.EndsWith("Map") || property.EndsWith("Tex"))
                {
                    Texture texture = material.GetTexture(property);
                    report.AppendLine($"    {property,-15} {(texture != null ? $"{texture.name}, wrap {texture.wrapMode}" : "none")}" +
                                      $", scale {material.GetTextureScale(property)}, offset {material.GetTextureOffset(property)}");
                }
                else
                {
                    report.AppendLine($"    {property,-15} {material.GetColor(property)}");
                }
            }

            // Whether this material binds a normal map is what decides if the ribbons'
            // tangents matter: URP Lit only builds its tangent basis into the shading when
            // _NORMALMAP is on, and with it on a missing basis gives a NaN normal rather
            // than merely a flat one.
            foreach (string property in new[] { "_BumpMap", "_NormalMap" })
            {
                if (!material.HasProperty(property)) continue;

                Texture texture = material.GetTexture(property);
                report.AppendLine($"    {property,-15} {(texture != null ? texture.name : "none")}");
            }

            string[] keywords = material.shaderKeywords;
            report.AppendLine($"    keywords        {(keywords.Length > 0 ? string.Join(" ", keywords) : "<none>")}");

            // Asked of the vertex layout, not the vertex data. Reading mesh.uv on a shipped
            // mesh throws an engine error and hands back an empty array — which reads as
            // "this surface has no UVs", and that false negative is what sent the search
            // after the wrong suspect for two builds. bridge1 is UV mapped; it is simply
            // not readable, and those are not the same finding.
            if (mesh != null && !mesh.HasVertexAttribute(VertexAttribute.TexCoord0))
            {
                report.AppendLine("    donor mesh UV   none — the surface carries no uv0 at all");
            }
            else if (mesh != null && !mesh.isReadable)
            {
                report.AppendLine($"    donor mesh UV   has uv0, but '{mesh.name}' is not readable, " +
                                  "so the scale cannot be measured from it");
            }
            else if (mesh != null)
            {
                Vector2[] uvs = mesh.uv;
                if (uvs != null && uvs.Length > 0)
                {
                    Vector2 min = uvs[0], max = uvs[0];
                    foreach (Vector2 uv in uvs) { min = Vector2.Min(min, uv); max = Vector2.Max(max, uv); }

                    Bounds bounds = mesh.bounds;
                    report.AppendLine($"    donor mesh UV   {min} .. {max} over {bounds.size.x:0} x {bounds.size.z:0} m");
                    report.AppendLine($"                    so one UV unit is about " +
                                      $"{(max.x - min.x > 0.001f ? bounds.size.x / (max.x - min.x) : 0f):0.#} m across, " +
                                      $"{(max.y - min.y > 0.001f ? bounds.size.z / (max.y - min.y) : 0f):0.#} m along");
                }
                else
                {
                    report.AppendLine("    donor mesh UV   readable but empty");
                }
            }

            Plugin.LogDebug(report.ToString().TrimEnd());
        }

        /// <summary>Every shipped map's prefab.</summary>
        public static IEnumerable<MapSettings> ShippedMaps()
        {
            MapSettingsManager manager = SceneSingleton<MapSettingsManager>.i;
            if (manager?.Maps == null) yield break;

            foreach (MapSettingsManager.Map entry in manager.Maps)
            {
                if (entry?.Details == null || entry.Prefab == null) continue;
                if (MapIdentity.IdOf(entry.Details.PrefabName) != null) continue;

                yield return entry.Prefab;
            }
        }

        /// <summary>
        /// Swaps placeholder materials for the base game's.
        ///
        /// The alternative — shipping real URP materials in the bundle — drags URP into
        /// the authoring project and risks pulling engine shader variants into the
        /// bundle along with them, which is the dependency-bleed problem the design
        /// set out to avoid. Instead the Unity project assigns a material literally
        /// named <c>__BORROW__Terrain</c>, and the real one is taken from the donor at
        /// load time. That also means custom terrain automatically matches whatever the
        /// shipped maps look like after a game update, rather than drifting away from
        /// them.
        /// </summary>
        /// <summary>Macro colour input on <c>Shader Graphs/TerrainShader</c>. Heartland
        /// feeds it an 8192² satellite-style basecolor, which is why a borrowed material
        /// paints Heartland's landscape onto whatever geometry it is applied to.</summary>
        const string MacroBasecolorProperty = "_macro_basecolor";

        /// <summary>Ground-cover inputs on <c>Shader Graphs/TerrainShader</c>, paired with
        /// the manifest names that feed them.</summary>
        static readonly (string Property, Func<MapManifest, string> Asset)[] SplatProperties =
        {
            ("_splat_grass", m => m.SplatGrass),
            ("_splat_rock", m => m.SplatRock),
            ("_splat_lush", m => m.SplatLush),
            ("_splat_fields", m => m.SplatFields),
        };

        /// <summary>
        /// Gives the map its own ground cover.
        ///
        /// Overriding the macro colour alone is not enough, and the gap is easy to miss:
        /// the macro is what the ground looks like from the air, and these four are what it
        /// looks like from a hundred metres. Left borrowed, they draw the donor's cover
        /// sampled at our coordinates — ploughed fields running up an alpine face, forest
        /// undergrowth on bare rock — which reads as wrong close up while every screenshot
        /// taken from altitude looks right.
        ///
        /// Missing textures are left alone rather than cleared. A map without them keeps
        /// the donor's cover, which is wrong but is ground; a null splat is whatever the
        /// shader does with no weights at all.
        /// </summary>
        static void OverrideGroundCover(GameObject root, Material terrain, LoadedMap map)
        {
            int replaced = 0, missing = 0;

            foreach ((string property, Func<MapManifest, string> name) in SplatProperties)
            {
                if (!terrain.HasProperty(property)) continue;

                string asset = name(map.Manifest);
                if (string.IsNullOrEmpty(asset)) { missing++; continue; }

                Texture2D splat = map.Asset<Texture2D>(asset);
                if (splat == null) { missing++; continue; }

                terrain.SetTexture(property, splat);
                replaced++;
            }

            if (replaced > 0)
                Plugin.LogDebug($"{root.name}: overrode {replaced} ground-cover splat map(s)" +
                                (missing > 0 ? $", {missing} absent — those keep the donor's" : ""));
            else if (missing > 0)
                Plugin.LogWarning($"{root.name}: no ground-cover splat maps shipped, so the map wears " +
                                  "the donor's — farmland and undergrowth will fall where the donor " +
                                  "had them, not where this terrain puts them. Run 'MapGen splat' " +
                                  "and rebuild the bundle.");
        }

        static void BorrowMaterials(GameObject root, LoadedMap map)
        {
            Material terrain = ResolveDonorTerrainMaterial();
            if (terrain == null) return;

            // Borrowing the donor's material verbatim also borrows its macro colour
            // texture, so custom terrain renders with the donor's coastlines and fields
            // stretched across it — recognisably somewhere else, carved by our channels.
            // A per-map copy with that one input overridden keeps every other input
            // (splat maps, detail albedo and normals, the shader itself) while giving
            // the map its own surface.
            Texture2D macro = map.Asset<Texture2D>(map.Manifest.TerrainMacroColor)
                              ?? map.Asset<Texture2D>(map.Manifest.TerrainColorMap);

            if (macro != null && terrain.HasProperty(MacroBasecolorProperty))
            {
                terrain = new Material(terrain) { name = $"{terrain.name}__{map.Manifest.MapId}" };
                terrain.SetTexture(MacroBasecolorProperty, macro);
                Plugin.LogDebug($"{root.name}: overrode {MacroBasecolorProperty} with " +
                                $"'{macro.name}' ({macro.width}x{macro.height})");

                OverrideGroundCover(root, terrain, map);
            }
            else if (macro == null)
            {
                Plugin.LogWarning($"{root.name}: no macro colour texture — terrain will render " +
                                  $"with {_donor?.name}'s imagery. Add 'terrainMacroColor' to the manifest.");
            }

            Material water = ResolveWaterMaterial();
            Material paved = ResolvePavedMaterial();
            Material runway = null, tarmac = null;
            bool pavingResolved = false;

            // Only looked up when a map ships structures: a search of every shipped renderer is
            // not free, and most maps have no bridges.
            Material structure = null, concrete = null;
            bool structuresResolved = false;

            // The ground's rendering layers. The game's MainRenderer runs URP's
            // DecalRendererFeature with decal layers on, so a decal paints only renderers
            // whose renderingLayerMask shares a bit with its own. The lasting mark a bomb or
            // a nuke leaves (DecalSpawner's scorchMarkDecal: craters, and the 1,200, 3,000 and
            // 6,000 m scorches) has mask 2; the shockwave's dust ring has 3, which is why it
            // shows on a bundle whose renderers are all at Unity's default of 1 while the
            // scorch never does, and why the burst seemed to vanish when the ring faded, as it
            // does by design some 20 to 33 s after the burst. The shipped maps give every
            // ground renderer 3 (tiles, roads, asphalt, runway markings, naval bridges) and
            // their buildings 1, and nothing in the bundle's authoring sets it, so it is set
            // here: on every renderer that has a slot swapped to terrain, paved, runway or
            // tarmac, and on the bridges, decks and structure alike, as the naval bridges are.
            //
            // Chosen by hierarchy as well as by placeholder. Nothing under the tunnels gets it:
            // a tunnel's deck wears the same paved placeholder as a road, and a scorch box
            // reaches 300 to 600 m above and below the hit, so a burst on the hill would paint
            // the floor inside the bore. The lakes and the hand-placed props keep 1, as the
            // sea and the game's buildings do.
            uint groundLayers = ResolveGroundRenderingLayers();
            int groundRenderers = 0;

            int terrainSwaps = 0, waterSwaps = 0, pavedSwaps = 0, runwaySwaps = 0, tarmacSwaps = 0, structureSwaps = 0;
            foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                Material[] materials = renderer.sharedMaterials;
                bool changed = false;
                bool ground = false, lake = false;

                for (int i = 0; i < materials.Length; i++)
                {
                    Material assigned = materials[i];
                    if (assigned != null && !assigned.name.StartsWith(BorrowMarker, StringComparison.Ordinal))
                        continue;

                    // The marker names which surface is wanted; an unnamed slot defaults
                    // to terrain, since that is all a map has unless it ships lakes.
                    bool wantsWater = assigned != null &&
                                      assigned.name.StartsWith(WaterMarker, StringComparison.Ordinal);
                    bool wantsPaved = assigned != null &&
                                      assigned.name.StartsWith(PavedMarker, StringComparison.Ordinal);
                    bool wantsRunway = assigned != null &&
                                       assigned.name.StartsWith(RunwayMarker, StringComparison.Ordinal);
                    bool wantsTarmac = assigned != null &&
                                       assigned.name.StartsWith(TarmacMarker, StringComparison.Ordinal);
                    bool wantsStructure = assigned != null &&
                                          assigned.name.StartsWith(StructureMarker, StringComparison.Ordinal);
                    bool wantsConcrete = assigned != null &&
                                         assigned.name.StartsWith(ConcreteMarker, StringComparison.Ordinal);

                    if ((wantsRunway || wantsTarmac) && !pavingResolved)
                    {
                        pavingResolved = true;
                        runway = ResolveNamedShippedMaterial(RunwayMaterialName) ?? ResolveRunwayMaterial() ?? paved;
                        tarmac = ResolveNamedShippedMaterial(TarmacMaterialName) ?? paved;
                    }

                    if ((wantsStructure || wantsConcrete) && !structuresResolved)
                    {
                        structuresResolved = true;
                        concrete = ResolveNamedShippedMaterial(ConcreteMaterialName) ?? paved;
                        structure = ResolveNamedShippedMaterial(StructureMaterialName) ?? concrete;
                    }

                    if (wantsStructure && structure != null) { materials[i] = structure; structureSwaps++; }
                    else if (wantsConcrete && concrete != null) { materials[i] = concrete; structureSwaps++; }
                    else if (wantsWater && water != null) { materials[i] = water; waterSwaps++; lake = true; }
                    else if (wantsPaved && paved != null) { materials[i] = paved; pavedSwaps++; ground = true; }
                    else if (wantsRunway && runway != null) { materials[i] = runway; runwaySwaps++; ground = true; }
                    else if (wantsTarmac && tarmac != null) { materials[i] = tarmac; tarmacSwaps++; ground = true; }
                    else { materials[i] = terrain; terrainSwaps++; ground = true; }

                    changed = true;
                }

                if (changed) renderer.sharedMaterials = materials;

                Transform at = renderer.transform;
                if (Under(at, BridgeRoot)) ground = true;
                if (lake || Under(at, TunnelRoot) || Under(at, WaterRoot) || Under(at, PropsRoot)) ground = false;

                if (ground && (renderer.renderingLayerMask & groundLayers) != groundLayers)
                {
                    renderer.renderingLayerMask |= groundLayers;
                    groundRenderers++;
                }
            }

            Plugin.LogDebug($"{root.name}: gave {groundRenderers} ground renderer(s) rendering layers " +
                            $"0x{groundLayers:X}, so craters and scorches paint them");

            Plugin.LogDebug($"{root.name}: swapped {terrainSwaps} slot(s) for '{terrain.name}'" +
                            (waterSwaps > 0 ? $", {waterSwaps} for '{water.name}'" : "") +
                            (pavedSwaps > 0 ? $", {pavedSwaps} for '{paved.name}'" : "") +
                            (runwaySwaps > 0 ? $", {runwaySwaps} runway slot(s) for '{runway.name}'" : "") +
                            (tarmacSwaps > 0 ? $", {tarmacSwaps} tarmac slot(s) for '{tarmac.name}'" : "") +
                            (structureSwaps > 0 ? $", {structureSwaps} structure slot(s)" : ""));
        }

        /// <summary>Wave-space offset on the water shader. See <see cref="AnchorLakeWater"/>.</summary>
        static readonly int OriginOffsetProperty = Shader.PropertyToID("_OriginOffset");

        static Material _lakeWater;

        /// <summary>
        /// Gives the lake surfaces a water material that does not follow the camera.
        ///
        /// The ocean is not a static mesh. <c>LevelInfo.UpdateWaterPlane</c> parks it on
        /// the camera every frame and hands the shader
        /// <c>_OriginOffset = Datum.origin.position - cam.position</c>, which is what
        /// turns the disc's camera-relative vertices back into stable map coordinates for
        /// the macro UV. A lake is already in map coordinates — it is a static mesh under
        /// the map root, which <c>MapSettingsManager.LoadMap</c> parents to
        /// <c>Datum.origin</c> — so that correction is one it never needed, and applying
        /// it drags the water's UV along with the camera. Flying forward makes the lake
        /// appear to slide backwards, and worse, it lands each lake on an arbitrary part
        /// of the macro colour texture, which is why one lake comes out blue and the next
        /// one the sand tone meant for dry land.
        ///
        /// A <c>MaterialPropertyBlock</c> was the first attempt and did not take: the
        /// property lives in the shader's <c>UnityPerMaterial</c> constant buffer, which
        /// the SRP Batcher fills from the material rather than from the renderer. A
        /// separate material has its own buffer and is the only thing that reliably wins.
        ///
        /// Cloned here rather than at prefab time because this runs immediately after
        /// <c>LevelInfo.ApplyMapSettings</c> has assigned the per-map ocean textures, so
        /// the copy picks them up; the properties are re-copied on every map load so the
        /// clone cannot drift from the original.
        /// </summary>
        public static void AnchorLakeWater(MapSettings instance)
        {
            if (instance == null) return;

            Material ocean = ResolveWaterMaterial();
            if (ocean == null) return;

            if (_lakeWater == null)
            {
                _lakeWater = new Material(ocean)
                {
                    name = ocean.name + " (lakes)",
                    hideFlags = HideFlags.HideAndDontSave,
                };
            }

            _lakeWater.CopyPropertiesFromMaterial(ocean);
            _lakeWater.SetVector(OriginOffsetProperty, Vector4.zero);

            // A lake is any renderer still carrying the shared ocean material: the prefab
            // pass put it there and nothing else on the map uses it.
            int swapped = 0;
            foreach (MeshRenderer renderer in instance.GetComponentsInChildren<MeshRenderer>(true))
            {
                Material[] materials = renderer.sharedMaterials;
                bool changed = false;

                for (int i = 0; i < materials.Length; i++)
                {
                    if (materials[i] != ocean) continue;

                    materials[i] = _lakeWater;
                    changed = true;
                    swapped++;
                }

                if (changed) renderer.sharedMaterials = materials;
            }

            if (swapped > 0)
                Plugin.LogDebug($"{instance.name}: anchored {swapped} lake surface(s) to map coordinates " +
                                $"on a private '{_lakeWater.name}'");
        }

        /// <summary>
        /// The donor's terrain material, identified the same way the game identifies
        /// terrain: the renderer whose MeshCollider carries the terrain PhysicMaterial.
        /// Matching by name would be a guess, and the shipped maps do not use an
        /// obvious one.
        /// </summary>
        static Material ResolveDonorTerrainMaterial()
        {
            if (_donorTerrainMaterialResolved) return _donorTerrainMaterial;
            _donorTerrainMaterialResolved = true;

            if (_terrainMaterials != null && _terrainMaterials.Length > 0)
                _donorTerrainMaterial = _terrainMaterials[0];

            if (_donorTerrainMaterial == null && _donor != null && _terrainPhysicMaterial != null)
            {
                foreach (MeshCollider collider in _donor.GetComponentsInChildren<MeshCollider>(true))
                {
                    if (collider.sharedMaterial != _terrainPhysicMaterial) continue;

                    var renderer = collider.GetComponent<MeshRenderer>()
                                   ?? collider.GetComponentInChildren<MeshRenderer>(true);
                    if (renderer == null || renderer.sharedMaterial == null) continue;

                    _donorTerrainMaterial = renderer.sharedMaterial;
                    break;
                }
            }

            if (_donorTerrainMaterial == null)
                Plugin.LogWarning("no base-game terrain material found — custom terrain will render " +
                                  "with whatever the bundle shipped, which is probably magenta");
            else
                DescribeMaterial(_donorTerrainMaterial);

            return _donorTerrainMaterial;
        }

        /// <summary>
        /// The rendering layers of the donor's own terrain, found the way
        /// <see cref="ResolveDonorTerrainMaterial"/> finds its renderer (the MeshCollider
        /// carrying the terrain PhysicMaterial), so a game update that moves the ground to other
        /// layers carries over. Falls back to <see cref="GroundRenderingLayers"/>; and since bit 1
        /// is what the scorch and crater decals need, a donor value without it still has 3
        /// added, with a warning, rather than leaving every crater off the map.
        /// </summary>
        static uint ResolveGroundRenderingLayers()
        {
            if (_groundRenderingLayersResolved) return _groundRenderingLayers;
            _groundRenderingLayersResolved = true;
            _groundRenderingLayers = GroundRenderingLayers;

            MeshRenderer donorTerrain = null;
            if (_donor != null && _terrainPhysicMaterial != null)
            {
                foreach (MeshCollider collider in _donor.GetComponentsInChildren<MeshCollider>(true))
                {
                    if (collider.sharedMaterial != _terrainPhysicMaterial) continue;

                    donorTerrain = collider.GetComponent<MeshRenderer>()
                                   ?? collider.GetComponentInChildren<MeshRenderer>(true);
                    if (donorTerrain != null) break;
                }
            }

            if (donorTerrain == null)
            {
                Plugin.LogDebug($"no donor terrain renderer to read rendering layers from; " +
                                $"ground gets 0x{GroundRenderingLayers:X}");
                return _groundRenderingLayers;
            }

            uint donor = donorTerrain.renderingLayerMask;
            _groundRenderingLayers = donor | GroundRenderingLayers;

            if ((donor & 2u) == 0)
                Plugin.LogWarning($"{donorTerrain.name}'s rendering layers are 0x{donor:X}, without the " +
                                  $"bit the scorch and crater decals paint; ground gets " +
                                  $"0x{_groundRenderingLayers:X} anyway");
            else
                Plugin.LogDebug($"ground rendering layers 0x{_groundRenderingLayers:X}, " +
                                $"from {donorTerrain.name}");

            return _groundRenderingLayers;
        }

        /// <summary>
        /// Dumps the borrowed terrain material's texture inputs.
        ///
        /// Borrowing the donor's material also borrows the macro colour texture baked
        /// into it, which is why custom terrain currently renders with Heartland's
        /// satellite imagery stretched across it — recognisably Heartland, carved by our
        /// channel, with no coastline where our water is. Fixing that means overriding
        /// one texture property on a per-map instance of the material, and this names
        /// the candidates rather than requiring a guess or an asset-ripper session.
        /// </summary>
        static void DescribeMaterial(Material material)
        {
            if (Plugin.DebugLogging == null || !Plugin.DebugLogging.Value) return;

            try
            {
                Shader shader = material.shader;
                var sb = new System.Text.StringBuilder();
                sb.Append("terrain material '").Append(material.name)
                  .Append("' shader '").Append(shader != null ? shader.name : "<null>").AppendLine("'");

                int count = shader != null ? shader.GetPropertyCount() : 0;
                for (int i = 0; i < count; i++)
                {
                    if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Texture) continue;

                    string name = shader.GetPropertyName(i);
                    Texture assigned = material.GetTexture(name);
                    sb.Append("    ").Append(name).Append(" = ")
                      .Append(assigned != null ? $"{assigned.name} ({assigned.width}x{assigned.height})" : "<none>")
                      .AppendLine();
                }

                Plugin.LogDebug(sb.ToString().TrimEnd());
            }
            catch (Exception e)
            {
                Plugin.LogDebug($"could not describe the terrain material: {e.Message}");
            }
        }

        /// <summary>
        /// Copies the donor's faction music array wholesale.
        ///
        /// <c>MapSettings.MapMusic</c> is a private nested class, so wholesale is the
        /// only option — and it is also exactly what is wanted, since every
        /// <c>Faction</c> and <c>AudioClip</c> inside then resolves to a base-game
        /// asset. This is what lets a custom map have music without depending on
        /// Blueprinter, which is the limitation NOMapLoader documents. Missing music is
        /// harmless in any case: <c>MusicManager</c> early-returns on a null clip.
        /// </summary>
        static void BorrowFactionMusic(MapSettings settings)
        {
            if (_donor == null) return;
            if (settings.factionMusic != null && settings.factionMusic.Length > 0) return;

            settings.factionMusic = _donor.factionMusic;
            Plugin.LogDebug($"{settings.name}: borrowed {settings.factionMusic?.Length ?? 0} faction music entries " +
                            $"from {_donor.name}");
        }

        /// <summary>A shipped map's prefab by name, for borrowing from one other than the
        /// configured donor. City buildings come from Ignus even when the terrain
        /// materials come from Heartland, and every shipped prefab is reachable from the
        /// manager regardless of which map is being played.</summary>
        public static MapSettings ShippedMap(string prefabName)
        {
            MapSettingsManager manager = SceneSingleton<MapSettingsManager>.i;
            if (manager?.Maps == null) return null;

            foreach (MapSettingsManager.Map entry in manager.Maps)
            {
                if (entry?.Details == null || entry.Prefab == null) continue;
                if (string.Equals(entry.Details.PrefabName, prefabName, StringComparison.Ordinal))
                    return entry.Prefab;
            }

            return null;
        }

        static void ResolveDonor(MapSettingsManager manager)
        {
            if (_donorResolved) return;
            _donorResolved = true;

            string wanted = Plugin.DonorMap.Value;
            MapSettingsManager.Map[] maps = manager.Maps;

            if (maps != null)
            {
                foreach (MapSettingsManager.Map entry in maps)
                {
                    if (entry?.Details == null || entry.Prefab == null) continue;
                    if (string.Equals(entry.Details.PrefabName, wanted, StringComparison.Ordinal))
                    {
                        _donor = entry.Prefab;
                        break;
                    }
                }

                // Fall back to any shipped map — anything whose name is not one of ours.
                if (_donor == null)
                {
                    foreach (MapSettingsManager.Map entry in maps)
                    {
                        if (entry?.Details == null || entry.Prefab == null) continue;
                        if (MapIdentity.IdOf(entry.Details.PrefabName) != null) continue;
                        _donor = entry.Prefab;
                        Plugin.LogWarning($"donor map '{wanted}' not found, using '{entry.Details.PrefabName}'");
                        break;
                    }
                }
            }

            if (_donor == null)
            {
                Plugin.LogError("no shipped map found to borrow the terrain PhysicMaterial and faction music from. " +
                                "Custom maps will load without grass, trees or music.");
                return;
            }

            ResolveTerrainMaterials();
            Plugin.LogDebug($"donor={_donor.name} " +
                            $"physicMaterial={(_terrainPhysicMaterial != null ? _terrainPhysicMaterial.name : "<none>")} " +
                            $"terrainMaterials={_terrainMaterials?.Length ?? 0}");
        }

        /// <summary>
        /// Finds the terrain PhysicMaterial the game itself tests against.
        ///
        /// <c>TerrainHeightMap</c> is a <c>SceneSingleton</c>, but each shipped map
        /// carries its own instance (on the map prefab's <c>DetailRenderer_Base</c>),
        /// so the donor prefab is checked first. Scanning loaded objects is the
        /// fallback for the case where it lives in the GameWorld scene instead.
        /// </summary>
        static void ResolveTerrainMaterials()
        {
            TerrainHeightMap source = _donor != null
                ? _donor.GetComponentInChildren<TerrainHeightMap>(true)
                : null;

            if (source == null || source.terrainPhysicMaterials == null || source.terrainPhysicMaterials.Length == 0)
            {
                foreach (TerrainHeightMap candidate in Resources.FindObjectsOfTypeAll<TerrainHeightMap>())
                {
                    if (candidate?.terrainPhysicMaterials == null || candidate.terrainPhysicMaterials.Length == 0) continue;
                    source = candidate;
                    break;
                }
            }

            if (source == null)
            {
                Plugin.LogWarning("no TerrainHeightMap with a terrain PhysicMaterial found — " +
                                  "custom terrain will not grow grass or trees");
                return;
            }

            if (source.terrainPhysicMaterials.Length > 0)
                _terrainPhysicMaterial = source.terrainPhysicMaterials[0];
            _terrainMaterials = source.terrainMaterials;
        }

        /// <summary>Exposed for diagnostics.</summary>
        public static PhysicMaterial TerrainPhysicMaterial => _terrainPhysicMaterial;
        public static MapSettings Donor => _donor;
    }
}
