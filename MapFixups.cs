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

        /// <summary>
        /// Prepares the custom map <paramref name="mapName"/>, which <c>EnableMap</c> is about to
        /// enable, once per session. A shipped map's name, or one of ours already prepared or
        /// rejected, prepares nothing.
        ///
        /// Only that map. This used to prepare every installed map at the first <c>EnableMap</c> of
        /// any, Heartland included, and preparing a map reads its whole prefab
        /// (<c>LoadedMap.Root</c>): 6 s and 2.5 GB for Swiss Alps, whether or not it was played.
        /// The shipped maps the preparation borrows from are prefabs, reachable from the manager
        /// whichever map is in the scene, and what it resolves from them is resolved once per
        /// session, so a map prepared after another was played is prepared the same.
        /// </summary>
        public static void PrepareFor(MapSettingsManager manager, string mapName)
        {
            if (manager == null || Plugin.Disabled) return;
            if (BundleLoader.Maps.Count == 0) return;

            ResolveDonor(manager);

            foreach (LoadedMap map in BundleLoader.Maps)
            {
                if (map.PrefabName == null || !string.Equals(map.PrefabName, mapName, StringComparison.Ordinal)) continue;
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
            AncestorNames<Transform> sections = Sections(RoadRoot, AirfieldRoot);
            int rebound = 0, relayered = 0, skipped = 0;

            foreach (MeshCollider collider in colliders)
            {
                // Road ribbons must keep their null PhysicMaterial — that is the entire
                // signal the vehicle job uses to grant on-road speed. They normally sit
                // outside this scope anyway, but the fallback above widens it to the whole
                // map when the terrain root cannot be found, and a warning nobody reads
                // would then quietly cost every vehicle its road speed.
                if (sections.Under(collider.transform, RoadRoot)) { skipped++; continue; }
                if (sections.Under(collider.transform, AirfieldRoot)) { skipped++; continue; }

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

            int grounded = GroundAirfieldFloors(root);
            if (grounded > 0)
                Plugin.LogInfo($"{grounded} airfield floor(s) given the terrain PhysicMaterial: the grass inside an " +
                               "airfield is ground, where landing gear sinks and breaks as on the base game's grass");

            return rebound + grounded;
        }

        /// <summary>
        /// Gives each airfield's floor the terrain PhysicMaterial, so that the grass inside a drawn
        /// airfield is grass to the game and not paving.
        ///
        /// NOMapForge lays under every drawn airfield's outline an invisible floor, a
        /// <c>MeshCollider</c> with no renderer, 1 cm over the ground, so that an aircraft in a
        /// hangar (<c>hangar_med</c> has no floor of its own) would not stand on the terrain. It
        /// shipped with no PhysicMaterial, and under <see cref="AirfieldRoot"/> it kept none, so it
        /// was paving over the whole field. <c>LandingGear.FixedUpdate</c> (decompiled) casts one line
        /// down from each strut and takes the first collider it meets; unless that collider's
        /// <c>sharedMaterial</c> is <c>GameAssets.i.terrainMaterial</c> the wheel is on tarmac.
        /// Off tarmac it sinks by the tyre's pressure, the strut's force over its own
        /// <c>contactArea</c>, and is dragged back by a force growing with the square of the sink,
        /// the square of the pressure and the wheel's speed; past the strut's <c>springRate</c>, or
        /// with the strut compressed past <c>maxCompression</c> or its hinge bent past 10 degrees,
        /// the gear breaks. No aircraft is named anywhere in that: whichever aircraft the base game
        /// spares on grass (the user names the Cricket and the Compass) it spares through their own
        /// gear's numbers, and it does the same here. The user found the difference: on this map
        /// any aircraft taxied, took off and landed on the grass beside the runway, where on the base
        /// game's maps most lose their gear.
        ///
        /// A floor is told from paving by having no <c>Renderer</c> on its object (the runway and the
        /// tarmac are drawn). With the terrain's material it is the ground it lies a centimetre over:
        /// soft for a wheel, off-road for a vehicle, dust for an impact. The paving (6 and 7 cm up),
        /// the road ribbons (2 to 5 cm) and the bridges and tunnels keep a null material, so a wheel
        /// that meets them first is on tarmac as before. The cost is the reason the floor was laid: an
        /// aircraft in a hangar a mission stands on grass sinks, and breaks its gear rolling out over
        /// the grass, as it would in a hangar on the base game's grass. Hangars go on the aprons.
        ///
        /// Only with <see cref="Patches.AirfieldTaxiPatch"/> in place. A custom airbase's AI keeps to its
        /// taxi network but drives its first and last legs, and its take-off turn, straight across
        /// whatever lies there; that patch lets an aircraft taxiing or lining up under AI control meet
        /// the floor as paving. Without it the
        /// floors stay paved, as before, rather than strand every heavy AI aircraft with broken gear.
        /// </summary>
        /// <returns>How many floors were given the material.</returns>
        static int GroundAirfieldFloors(GameObject root)
        {
            if (_terrainPhysicMaterial == null) return 0;

            Transform airfields = root.transform.Find(AirfieldRoot);
            if (airfields == null) return 0;

            if (!Patches.AirfieldTaxiPatch.Applied)
            {
                Plugin.LogWarning("the airfields' floors are left paved, since the AI taxi patch on LandingGear is not in " +
                                  "place: every aircraft can roll on the grass inside a drawn airfield");
                return 0;
            }

            int grounded = 0;
            foreach (MeshCollider collider in airfields.GetComponentsInChildren<MeshCollider>(true))
            {
                if (!IsAirfieldFloor(collider)) continue;

                // Marked once here, so the wheel patch knows a floor by a component rather than by
                // walking its parents' names every physics step (AirfieldFloorMark says why).
                if (collider.GetComponent<AirfieldFloorMark>() == null) collider.gameObject.AddComponent<AirfieldFloorMark>();

                if (collider.sharedMaterial == _terrainPhysicMaterial) continue;

                collider.sharedMaterial = _terrainPhysicMaterial;
                grounded++;
            }

            return grounded;
        }

        /// <summary>True for an airfield's floor: a collider under <see cref="AirfieldRoot"/> with
        /// nothing on its object to draw it, which is ground rather than paving
        /// (<see cref="GroundAirfieldFloors"/>).</summary>
        public static bool IsAirfieldFloor(Collider collider)
            => collider != null && collider.GetComponent<Renderer>() == null && Under(collider.transform, AirfieldRoot);

        /// <summary>True if any ancestor, or the transform itself, carries this name.</summary>
        static bool Under(Transform transform, string name)
        {
            for (Transform at = transform; at != null; at = at.parent)
                if (at.name == name) return true;

            return false;
        }

        /// <summary>
        /// <see cref="Under"/> for a pass that asks it of many transforms: which of these names a
        /// transform or any of its ancestors carries, with each transform's name read once for the
        /// whole pass (<see cref="AncestorNames{T}"/>). Made afresh for each pass, so it never answers
        /// for a hierarchy that has changed since.
        /// </summary>
        internal static AncestorNames<Transform> Sections(params string[] names)
            => new AncestorNames<Transform>(at => at.parent, at => at.name, names);

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
            {
                PhysicMaterial own = collider.sharedMaterial;
                foreach (PhysicMaterial candidate in physicMaterials)
                    if (own == candidate) return true;
            }

            // Asked before the renderer's materials are read, which allocates a new array for each
            // renderer on the map: both shipped maps leave this list empty, so nothing can match.
            if (materials == null || materials.Length == 0) return false;

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
            var blocked = new GrassBlocking();

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

                // No padding round the grass blockers. The game's blocker shader
                // (Hidden/TerrainBlocker) moves every vertex blockerPadding metres, 1 in the
                // donor, straight away from its mesh's pivot. A base-game object is pivoted at
                // its centre, so that grows it by a metre all round; this map's road chunks,
                // bridges, tunnels, paving and grass blockers are pivoted at their terrain tile's
                // south-west corner, kilometres off, so it slid every one of them up to a metre
                // sideways instead: one edge of each road pulled onto the tarmac, which is where
                // the grass on the roads grew. The blockers carry their own margin now, so none
                // is wanted. Read only by SetBlockerMaterialProps, from CommandSetup, which
                // DetailRenderer calls from Start, long after this (the clone is still under its
                // inactive staging holder here), and again whenever the player switches grass
                // back on, which reads the same field. Only this map's cloned TerrainHeightMap is
                // touched; the cost is that a building a mission places here, whose own
                // TerrainHeightMapBlocker registers it, keeps grass off its footprint alone
                // rather than a metre round it.
                heightMap.blockerPadding = 0f;

                blocked.Add(BlockGrass(heightMap, instance.transform));
            }

            foreach (GrassRenderer grass in clone.GetComponentsInChildren<GrassRenderer>(true))
                UseGrassMask(grass, instance);

            foreach (ShaderGlobalManager globals in clone.GetComponentsInChildren<ShaderGlobalManager>(true))
                globals.mapSettings = instance;

            foreach (TerrainScatter scatter in clone.GetComponentsInChildren<TerrainScatter>(true))
                scatter.mapSettings = instance;

            // Silent when it fails, like the terrain recognition beside it, and with the same
            // consequence: grass growing through the tarmac with nothing to say why.
            if (blocked.Shipped > 0)
                Plugin.LogInfo($"grass blocked by {blocked.Shipped} grass blocker(s) and {blocked.Paved} " +
                               "airfield surface(s), no padding");
            else if (blocked.Paved > 0)
                Plugin.LogInfo($"grass blocked by {blocked.Paved} paved surface(s), no padding; this bundle " +
                               "ships no grass blockers, so there is no verge and bridge ends grow grass " +
                               "(rebuild the map)");
            else if (instance.transform.Find(RoadRoot) != null)
                Plugin.LogWarning("no paved surface registered as a grass blocker; grass will grow on the roads");
        }

        /// <summary>What <see cref="BlockGrass"/> registered: how many of the bundle's grass
        /// blockers, and how many paved surfaces besides them or in their place.</summary>
        struct GrassBlocking
        {
            public int Shipped, Paved;

            public void Add(GrassBlocking other)
            {
                Shipped += other.Shipped;
                Paved += other.Paved;
            }
        }

        /// <summary>
        /// Registers the map's grass blockers, or where it ships none its paved surfaces, and
        /// returns how many of each.
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
        /// under the hill and a deck well above it, and the blocker shader turns a blocker away
        /// only where it stands more than <c>blockerHeightVertThreshold</c> (100 m in the
        /// donor) over the ground, so either would shave a grassless stripe along a
        /// mountainside that has a road inside it, or under a viaduct.
        ///
        /// <para>A bundle built since 2026-09-28 ships its own grass blockers under
        /// <see cref="GrassBlockerRoot"/>, one invisible mesh per terrain tile, and they stand in
        /// for everything under <see cref="RoadRoot"/>: each road's corridor with a verge either
        /// side of it (a tuft is tested at its grid point, then moved up to 0.8 m and drawn up
        /// to a couple of metres across, so a blocker that stops at the road's edge leaves tufts
        /// leaning over it), each bridge deck wherever it lies low enough for grass under it to
        /// come through (every deck's ends lie on the ground), and each tunnel's floor where the
        /// ground is at it (its open mouths), never where a hill stands over it. They are laid
        /// flat under the ground, so the height test passes wherever they are drawn and their
        /// plan alone says where grass may not grow. They are registered on an active object with
        /// the renderer switched off (<see cref="HideBlocker"/>): the game files each blocker in
        /// its spatial grid by <c>Renderer.bounds</c>, and draws its mesh itself. A bundle without
        /// them keeps the road ribbons as its blockers, as before. The airfield paving is
        /// registered either way.</para>
        /// </summary>
        static GrassBlocking BlockGrass(TerrainHeightMap heightMap, Transform root)
        {
            var registered = new GrassBlocking();
            int switchedOff = 0, empty = 0, forced = 0;
            MeshRenderer sample = null;

            Transform shipped = root.Find(GrassBlockerRoot);
            if (shipped != null)
                foreach (MeshRenderer renderer in shipped.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (!renderer.TryGetComponent(out MeshFilter filter) || filter.sharedMesh == null) continue;

                    if (!renderer.gameObject.activeSelf) renderer.gameObject.SetActive(true);
                    switch (HideBlocker(renderer, filter.sharedMesh))
                    {
                        case BlockerHidden.SwitchedOff:
                            switchedOff++;
                            if (sample == null) sample = renderer;
                            break;
                        case BlockerHidden.Empty: empty++; break;
                        default: forced++; break;
                    }

                    heightMap.RegisterObject(new TerrainHeightMap.RenderFilter(
                        renderer, TerrainHeightMap.SubmeshFilter.All, TerrainHeightMap.EntryType.Blocker));
                    registered.Shipped++;
                }

            // What HideBlocker's check found, for a player's log to confirm: it was only ever seen in
            // the editor. Built only when it will be printed: the sample's name and bounds are read
            // for nothing otherwise.
            if (registered.Shipped > 0 && Plugin.DebugEnabled)
                Plugin.LogDebug($"{switchedOff} grass blocker(s) switched off with their bounds intact" +
                                (sample != null ? $" ('{sample.name}': {sample.bounds})" : "") +
                                (empty > 0 ? $", {empty} with an empty mesh switched off unchecked" : "") +
                                (forced > 0 ? $", {forced} kept from drawing by forceRenderingOff instead" : ""));

            AncestorNames<Transform> sections = Sections(BridgeRoot, TunnelRoot);
            foreach (string branch in registered.Shipped > 0 ? new[] { AirfieldRoot } : new[] { RoadRoot, AirfieldRoot })
            {
                Transform under = root.Find(branch);
                if (under == null) continue;

                foreach (MeshRenderer renderer in under.GetComponentsInChildren<MeshRenderer>(true))
                {
                    string name = renderer.name;
                    if (name.EndsWith("_LOD1", StringComparison.Ordinal) ||
                        name.EndsWith("_LOD2", StringComparison.Ordinal)) continue;

                    if (sections.Under(renderer.transform, BridgeRoot) || sections.Under(renderer.transform, TunnelRoot))
                        continue;

                    heightMap.RegisterObject(new TerrainHeightMap.RenderFilter(
                        renderer, TerrainHeightMap.SubmeshFilter.All, TerrainHeightMap.EntryType.Blocker));
                    registered.Paved++;
                }
            }

            return registered;
        }

        /// <summary>Set once a grass blocker's bounds have failed <see cref="HideBlocker"/>'s check, so
        /// the warning is given once a session rather than once a blocker.</summary>
        static bool _blockerBoundsWarned;

        /// <summary>How <see cref="HideBlocker"/> kept a grass blocker from drawing.</summary>
        enum BlockerHidden
        {
            /// <summary>Renderer switched off, its bounds seen to hold.</summary>
            SwitchedOff,
            /// <summary>Renderer switched off unchecked: its mesh has empty bounds, so its renderer's
            /// are empty whether it is on or off, and it draws nothing wherever it is filed.</summary>
            Empty,
            /// <summary>Renderer left on and kept from drawing by <c>forceRenderingOff</c>, as every
            /// blocker was before, because its bounds did not hold once it was switched off.</summary>
            Forced,
        }

        /// <summary>
        /// Keeps a grass blocker from drawing by switching its renderer off, or, should Unity then lose
        /// its bounds, by <c>forceRenderingOff</c> as every blocker was kept before.
        ///
        /// Switched off is the cheaper of the two. A renderer kept from drawing by
        /// <c>forceRenderingOff</c> stays in Unity's renderer update, which every origin shift sends
        /// through every renderer that moved, some 700 blockers on Swiss Alps; a switched-off one drops
        /// out of it. (A player's profile of one shift on Swiss Alps put 6.3 ms in that update; the
        /// performance pass's bench, 2026-10-04, had no graphics device, so the blockers' share of it
        /// was not timed.) And the game asks nothing else of a blocker's renderer:
        /// <c>TerrainHeightMap</c> (decompiled) files it in its spatial grid by <c>Renderer.bounds</c>,
        /// at <c>DetailRenderer.Start</c> and again whenever the grass is switched back on, and
        /// <c>BakeWindow</c> draws its mesh itself, with the transform's matrix, never asking whether
        /// the renderer is enabled. The GameObject stays active, as before.
        ///
        /// What this rests on is that Unity keeps a switched-off renderer's bounds. It did in the
        /// editor, for all 709 of Swiss Alps 0.4.0's blockers: after one origin shift and after five
        /// with no read between them, with the datum over 100 km out, and with the map or the
        /// blocker's own object inactive while it moved. But it was never seen in a player. So each
        /// blocker's bounds are checked against its mesh's as it is switched off
        /// (<see cref="BoundsHold"/>), and one whose are empty or elsewhere is switched back on and
        /// kept from drawing as before, with one warning a session: filed by empty bounds, it would
        /// leave grass growing on the road it covers. The check is made here only. That the bounds go
        /// on following later origin shifts, which the game reads again whenever the grass is switched
        /// back on, rests on the editor alone; switching grass off and on after a shift in game is
        /// what would show it.
        ///
        /// A blocker whose mesh has empty bounds (no vertices, or all at one point) is switched off
        /// unchecked: its renderer's bounds are empty switched on or off, so the check would fail on
        /// it and blame Unity, and it draws nothing wherever the game files it.
        /// </summary>
        static BlockerHidden HideBlocker(MeshRenderer renderer, Mesh mesh)
        {
            renderer.enabled = false;
            if (mesh.bounds.extents == Vector3.zero) return BlockerHidden.Empty;
            if (BoundsHold(renderer, mesh, out Bounds got, out Bounds want)) return BlockerHidden.SwitchedOff;

            renderer.enabled = true;
            renderer.forceRenderingOff = true;

            if (!_blockerBoundsWarned)
            {
                _blockerBoundsWarned = true;
                Plugin.LogWarning($"grass blocker '{renderer.name}' switched off reports bounds {got}, not its mesh's {want}; " +
                                  "it and any other that does are kept from drawing with forceRenderingOff instead");
            }

            return BlockerHidden.Forced;
        }

        /// <summary>
        /// True if a renderer's bounds are its mesh's bounds where its transform puts them, which is how
        /// Unity computes a mesh renderer's bounds: nothing empty, nothing left where the object was.
        ///
        /// Within a metre: the bounds are compared in single-precision world coordinates up to a hundred
        /// kilometres out, and the game files them into cells hundreds of metres across.
        /// </summary>
        static bool BoundsHold(Renderer renderer, Mesh mesh, out Bounds got, out Bounds want)
        {
            got = renderer.bounds;

            Bounds local = mesh.bounds;
            Matrix4x4 m = renderer.localToWorldMatrix;
            Vector3 e = local.extents;
            want = new Bounds(m.MultiplyPoint3x4(local.center), 2f * new Vector3(
                Mathf.Abs(m.m00) * e.x + Mathf.Abs(m.m01) * e.y + Mathf.Abs(m.m02) * e.z,
                Mathf.Abs(m.m10) * e.x + Mathf.Abs(m.m11) * e.y + Mathf.Abs(m.m12) * e.z,
                Mathf.Abs(m.m20) * e.x + Mathf.Abs(m.m21) * e.y + Mathf.Abs(m.m22) * e.z));

            const float Tolerance = 1f;
            return got.extents != Vector3.zero &&
                   (got.center - want.center).sqrMagnitude <= Tolerance * Tolerance &&
                   (got.extents - want.extents).sqrMagnitude <= Tolerance * Tolerance;
        }

        /// <summary>
        /// Makes the grass grow where this map's own ground cover says, when the bundle carries its
        /// grass mask, rather than where the donor's does.
        ///
        /// Grass reads neither the splat maps the ground is drawn with nor anything else of the map's:
        /// where it grows at all comes from <c>GrassRenderer.lushMaps</c>, single-channel masks the
        /// <c>GrassGenerator</c> compute shader samples at <c>uv = position / MapSize + 0.5</c> and
        /// turns into the chance of a tuft on each square metre. The clone brings Heartland's,
        /// <c>terrain1_mask_grass</c>, 512² of Heartland's meadows and forests, which then lies
        /// stretched over this map: on Swiss Alps no grass on two thirds of the ground its cover calls
        /// grass or undergrowth, and some on a fifth of its rock. NOMapForge builds the map's own from
        /// its splats and ships it as <c>&lt;mapId&gt;<see cref="GrassMaskSuffix"/></c>.
        ///
        /// Every entry is replaced, not only the first: each kind of tuft names its entry by index
        /// (<c>GrassConfig.LushTextureIndex</c>; all three of the donor's name 0, its only one), and an
        /// entry left over would be Heartland's for some kind of grass. The same texture in every
        /// entry also meets the one thing the game asks of them, that they be one size: it blits them
        /// all into one R8 texture array the size of the first, and logs an error for any that
        /// differs. It reads <c>lushMaps</c> in <c>CommandSetup</c>, each time the grass is switched
        /// on, which <c>DetailRenderer</c> first does from <c>Start</c>, long after this: the clone is
        /// still under its inactive staging holder here. A bundle without the mask keeps the donor's,
        /// exactly as before.
        /// </summary>
        static void UseGrassMask(GrassRenderer grass, MapSettings instance)
        {
            Texture2D[] donor = grass.lushMaps;
            string was = donor != null && donor.Length > 0 && donor[0] != null ? $"'{donor[0].name}'" : "none";

            Texture2D mask = GrassMaskOf(LoadedMapFor(instance));
            if (mask == null)
            {
                Plugin.LogInfo($"this bundle ships no grass mask, so grass grows where the donor's {was} says, " +
                               "stretched over this map (rebuild the map)");
                return;
            }

            // An empty list would make CommandSetup throw on its first entry; leave it to fail as the
            // donor would, rather than invent an array the donor never had.
            if (donor == null || donor.Length == 0)
            {
                Plugin.LogWarning($"the donor's grass renderer has no lush map to replace; '{mask.name}' is not used");
                return;
            }

            var ours = new Texture2D[donor.Length];
            for (int i = 0; i < ours.Length; i++) ours[i] = mask;
            grass.lushMaps = ours;

            Plugin.LogInfo($"grass grows where this map's own mask '{mask.name}' ({mask.width}x{mask.height} " +
                           $"{mask.format}) says, in place of the donor's {was}");

            // Said here, where the mask is loaded anyway, rather than in the validation block, which
            // runs for every installed map in the menu and so only asks whether the bundle has one
            // (ShipsGrassMask). The game blits the mask into an R8 array of its own, sampling it as a
            // shader does, so an sRGB one is decoded on the way and a meadow grows well under what
            // the map was built to.
            if (UnityEngine.Experimental.Rendering.GraphicsFormatUtility.IsSRGBFormat(mask.graphicsFormat))
                Plugin.LogWarning($"'{mask.name}' is sRGB: the grass reads it decoded and grows less than the map means");
        }

        /// <summary>
        /// Whether this map's bundle carries a grass mask, asked of the bundle's list of names
        /// rather than by loading it.
        ///
        /// For the validation block, which <see cref="Prepare"/> prints for every installed map at
        /// registration, in the menu, whether or not it is ever played. Loading the mask to answer
        /// read it off disk and up to the graphics card for each, 16 MB for Swiss Alps' 4096² R8,
        /// and kept it there until something unloaded unused assets. The names are the assets'
        /// paths as the build tagged them, in lower case; <c>LoadAsset</c> finds an asset by the
        /// file name at the end of its path, without the extension, which is what this compares.
        /// </summary>
        public static bool ShipsGrassMask(LoadedMap map)
        {
            if (map?.Bundle == null || string.IsNullOrEmpty(map.Manifest?.MapId)) return false;

            string wanted = map.Manifest.MapId + GrassMaskSuffix;
            try
            {
                foreach (string path in map.Bundle.GetAllAssetNames())
                    if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(path), wanted,
                                      StringComparison.OrdinalIgnoreCase))
                        return true;
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"{map.Name()}: could not list the bundle's assets ({e.GetType().Name}: {e.Message})");
            }

            return false;
        }

        /// <summary>This map's grass mask from its bundle, or null for a bundle without one.</summary>
        public static Texture2D GrassMaskOf(LoadedMap map)
        {
            if (map?.Bundle == null || string.IsNullOrEmpty(map.Manifest?.MapId)) return null;

            // Asked of the bundle directly rather than through LoadedMap.Asset, which warns about a
            // missing name: every bundle built before the mask lacks it, and that is not a fault.
            try
            {
                return map.Bundle.LoadAsset<Texture2D>(map.Manifest.MapId + GrassMaskSuffix);
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"{map.Name()}: could not load its grass mask ({e.GetType().Name}: {e.Message}); " +
                                  "the grass keeps the donor's");
                return null;
            }
        }

        /// <summary>The bundle a live map instance came from, or null if it is not one of ours. Only
        /// maps already prepared are matched, so asking never reads a map's prefab.</summary>
        internal static LoadedMap LoadedMapFor(MapSettings instance)
        {
            foreach (LoadedMap map in BundleLoader.Maps)
            {
                MapSettings prefab = PreparedPrefabFor(map);
                if (prefab == null) continue;

                // The instance is a clone, so its name carries the prefab's with a suffix.
                if (instance.name.StartsWith(prefab.name, StringComparison.Ordinal)) return map;
            }

            return null;
        }

        /// <summary>Whether a live map instance is one of ours (<see cref="LoadedMapFor"/>), for the
        /// patches that act on custom maps only and are told when a map is applied.</summary>
        internal static bool IsCustom(MapSettings instance) => instance != null && LoadedMapFor(instance) != null;

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
                    // nothing there to replace", which are different bugs. Counted by dataSize,
                    // never TextAsset.bytes, which copies the whole asset onto the heap at every
                    // read (30 MB for Swiss Alps' scatter, 6.6 MB for Heartland's), and only when
                    // the line is logged.
                    if (Plugin.DebugEnabled)
                        Plugin.LogDebug($"{instance.name}: {tree.name}.PositionData <- '{scatter.name}' " +
                                        $"({TreeCount(scatter):N0} trees, was {TreeCount(tree.PositionData):N0})");

                    tree.PositionData = scatter;
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

        /// <summary>How many tree positions a scatter holds, 0 for none, from its size alone.</summary>
        static long TreeCount(TextAsset scatter) => scatter != null ? scatter.dataSize / TreePositionStride : 0;

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

                // The size, not the bytes: TextAsset.bytes would copy the whole scatter to measure it.
                long length = scatter.dataSize;
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

        /// <summary>The map root's child holding the grass blockers, named by the generator's
        /// <c>GrassBlockerBuilder.RootName</c>: one invisible mesh per terrain tile, with no
        /// material slot, which <see cref="BlockGrass"/> registers in place of the road ribbons.
        /// A sibling of <see cref="RoadRoot"/> rather than under it, so a plugin that predates
        /// them passes them by.</summary>
        public const string GrassBlockerRoot = "GrassBlockers";

        /// <summary>What a map's id is followed by in the name of its grass mask, the texture
        /// <see cref="UseGrassMask"/> puts in place of the donor's: <c>swissalps_grass_mask</c>. Named
        /// by the generator's <c>GrassMaskTexture.Suffix</c>, and found by name because nothing in the
        /// prefab refers to it and the manifest has no field for it.</summary>
        public const string GrassMaskSuffix = "_grass_mask";

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
        /// load time with the equivalent base-game material. The markers' names are kept in
        /// <see cref="MaterialMarkers"/>, which reads them, apart from the game's types.</summary>
        public const string BorrowMarker = MaterialMarkers.Borrow;

        /// <summary>Placeholder standing in for the game's water surface, used by lake
        /// quads that sit above the datum.</summary>
        public const string WaterMarker = MaterialMarkers.Water;

        /// <summary>Placeholder standing in for a paved surface, used by the road
        /// ribbons.</summary>
        public const string PavedMarker = MaterialMarkers.Paved;

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
        public const string RunwayMarker = MaterialMarkers.Runway;

        /// <summary>Placeholder standing in for taxiway and apron tarmac.</summary>
        public const string TarmacMarker = MaterialMarkers.Tarmac;

        /// <summary>Placeholder for structural trim: bridge parapets, tunnel liners. Resolved to
        /// the shipped <see cref="StructureMaterialName"/>.</summary>
        public const string StructureMarker = MaterialMarkers.Structure;

        /// <summary>Placeholder for bare concrete: bridge decks' edges and undersides, piers,
        /// tunnel portals. Resolved to the shipped <see cref="ConcreteMaterialName"/>.</summary>
        public const string ConcreteMarker = MaterialMarkers.Concrete;

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
        /// The marker the airfields' paint wears in the bundle: every runway end's number, the base
        /// game's threshold and touchdown marks, and the taxi lanes' yellow lines, as one mesh a field
        /// under <see cref="MarkingsRoot"/>. Swapped for the game's own <see cref="MarkingsMaterialName"/>
        /// and switched on, since the renderers ship switched off.
        ///
        /// Not a <see cref="BorrowMarker"/> name, on purpose: a plugin from before the paint gives every
        /// <c>__BORROW__</c> slot it does not know the terrain, and one starting with
        /// <see cref="RunwayMarker"/> the runway's concrete. With its own marker and switched off, the
        /// paint is simply not there to such a plugin.
        /// </summary>
        public const string MarkingsMarker = MaterialMarkers.Markings;

        /// <summary>The base game's runway and taxiway paint (<c>Shader Graphs/vertexColorAlphaClip</c>,
        /// texture <c>runway_markings_b</c>), shared by every one of its airfields' painted meshes, so
        /// only ever assigned, never changed.</summary>
        public const string MarkingsMaterialName = "runway_markings";

        /// <summary>The child of the map root the airfields' paint is under: a sibling of
        /// <see cref="AirfieldRoot"/>, so a plugin from before the paint never registers it as paving.</summary>
        public const string MarkingsRoot = "AirfieldMarkings";

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
                    // A map played earlier may still be in the scene, owning copies that go with it.
                    if (MaterialNames.IsPerRendererCopy(candidate.name)) continue;

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
            // A debug block only: not built at all when it would not be written.
            if (!Plugin.DebugEnabled) return;

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
                // As for the water: never a copy owned by a map on its way out.
                if (MaterialNames.IsPerRendererCopy(candidate.name)) continue;

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
        /// <param name="quiet">Say nothing of a miss, for a caller that says itself what it does
        /// without the material: the airfields' paint stays off, and uses no road surface.</param>
        static Material ResolveNamedShippedMaterial(string exactName, bool quiet = false)
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
            else if (!quiet)
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
            // A debug block only, which reads a readable donor mesh's whole uv channel: not built at
            // all when it would not be written.
            if (!Plugin.DebugEnabled) return;

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

            // The airfields' paint: looked up only when a map ships some, and left switched off, as it
            // ships, when the game's material cannot be found.
            Material markings = null;
            bool markingsResolved = false;
            int paintSwaps = 0, unpainted = 0;

            // Names are read once each: every read of Object.name allocates a new string, and this walks
            // every slot of every renderer on the map. A material's surface is kept per material (the
            // terrain's one placeholder fills thousands of slots), and the subtree a renderer sits in per
            // transform (AncestorNames).
            var slots = new Dictionary<Material, MaterialSlot>(ByReference<Material>.Instance);
            AncestorNames<Transform> sections = Sections(BridgeRoot, TunnelRoot, WaterRoot, PropsRoot);

            int terrainSwaps = 0, waterSwaps = 0, pavedSwaps = 0, runwaySwaps = 0, tarmacSwaps = 0, structureSwaps = 0;
            foreach (MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                Material[] materials = renderer.sharedMaterials;
                bool changed = false;
                bool ground = false, lake = false, paint = false;

                for (int i = 0; i < materials.Length; i++)
                {
                    Material assigned = materials[i];
                    MaterialSlot slot = SlotOf(assigned, slots);

                    if (slot == MaterialSlot.Markings)
                    {
                        if (!markingsResolved)
                        {
                            markingsResolved = true;
                            markings = ResolveNamedShippedMaterial(MarkingsMaterialName, quiet: true);
                        }

                        if (markings == null)
                        {
                            unpainted++;
                            continue;
                        }

                        materials[i] = markings;
                        paintSwaps++;
                        changed = paint = true;
                        continue;
                    }

                    if (slot == MaterialSlot.Own) continue;

                    // The marker names which surface is wanted; an unnamed slot defaults
                    // to terrain, since that is all a map has unless it ships lakes.
                    bool wantsWater = slot == MaterialSlot.Water;
                    bool wantsPaved = slot == MaterialSlot.Paved;
                    bool wantsRunway = slot == MaterialSlot.Runway;
                    bool wantsTarmac = slot == MaterialSlot.Tarmac;
                    bool wantsStructure = slot == MaterialSlot.Structure;
                    bool wantsConcrete = slot == MaterialSlot.Concrete;

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

                // The paint ships switched off; with the game's material it is shown, and painted over by
                // craters and scorches as the ground under it is, as the game's own paint is.
                if (paint)
                {
                    renderer.enabled = true;
                    ground = true;
                }

                Transform at = renderer.transform;
                if (sections.Under(at, BridgeRoot)) ground = true;
                if (lake || sections.Under(at, TunnelRoot) || sections.Under(at, WaterRoot) || sections.Under(at, PropsRoot))
                    ground = false;

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
                            (structureSwaps > 0 ? $", {structureSwaps} structure slot(s)" : "") +
                            (paintSwaps > 0 ? $", {paintSwaps} airfield(s) painted with '{markings.name}'" : ""));

            if (unpainted > 0)
                Plugin.LogWarning($"{root.name}: no shipped material named '{MarkingsMaterialName}', so {unpainted} airfield(s) " +
                                  "are left unpainted: no runway numbers and no lane lines");
        }

        /// <summary>The surface a material slot asks for (<see cref="MaterialMarkers.Of"/>), with each
        /// material's name read once however many slots it fills.</summary>
        static MaterialSlot SlotOf(Material material, Dictionary<Material, MaterialSlot> known)
        {
            if (material == null) return MaterialMarkers.Of(null);

            if (!known.TryGetValue(material, out MaterialSlot slot))
                known[material] = slot = MaterialMarkers.Of(material.name);

            return slot;
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
        ///
        /// The same walk over the map's renderers finds the sheets <see cref="LayLakeUnderlays"/>
        /// lays the sea's floor under, which used to walk them all again. Neither has anything to
        /// do on a shipped map: none of Heartland's 2,698 renderers or Ignus's 4,994 wears the sea's
        /// material (read off the game's own prefabs, 2026-10-04), so a map that is not one of ours
        /// is not walked at all. Until then every map was: a map some other loader brings, with lakes
        /// wearing the sea's material, would now keep them as the game draws them (none is known).
        /// </summary>
        public static void AnchorLakeWater(MapSettings instance)
        {
            if (instance == null) return;
            if (LoadedMapFor(instance) == null) return;

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
            // pass put it there and nothing else on the map uses it. Each renderer's slots are
            // read into one list, where sharedMaterials would allocate an array per renderer
            // only to write back the few that are lakes.
            var materials = new List<Material>();
            var sheets = new List<MeshRenderer>();
            int swapped = 0;
            foreach (MeshRenderer renderer in instance.GetComponentsInChildren<MeshRenderer>(true))
            {
                renderer.GetSharedMaterials(materials);
                bool changed = false;

                for (int i = 0; i < materials.Count; i++)
                {
                    if (materials[i] != ocean) continue;

                    materials[i] = _lakeWater;
                    changed = true;
                    swapped++;
                }

                if (changed) renderer.sharedMaterials = materials.ToArray();

                // A sheet to lay an underlay under: drawn first with the lakes' water, which is
                // what Renderer.sharedMaterial reads.
                if (materials.Count > 0 && materials[0] == _lakeWater) sheets.Add(renderer);
            }

            if (swapped > 0)
                Plugin.LogDebug($"{instance.name}: anchored {swapped} lake surface(s) to map coordinates " +
                                $"on a private '{_lakeWater.name}'");

            LayLakeUnderlays(instance, sheets);
        }

        /// <summary>The child each lake sheet draws its underlay on. See <see cref="LayLakeUnderlays"/>.</summary>
        public const string LakeUnderlayName = "underlay";

        /// <summary>How far under the surface the game's own underlay sits below its sea
        /// (<c>oceanUnderlay</c>'s local position under <c>oceanPlane</c>).</summary>
        const float LakeUnderlayDepth = 2f;

        /// <summary>The game's sea underlay material, by name, for when the renderer cannot be
        /// reached through <c>LevelInfo.waterPlane</c>.</summary>
        const string OceanUnderlayMaterialName = "OceanUnderlay";

        /// <summary>
        /// Lays under each lake the opaque floor the game lays under its sea, so a jet's exhaust
        /// looks the same over both.
        ///
        /// A jet's heat haze (<c>JetNozzle.heatHaze</c>, material <c>HeatDistortion</c>: URP's
        /// particle shader with distortion on and <c>_DistortionBlend</c> at 1) has no colour of
        /// its own. It draws the camera's opaque texture, the scene as it stood before anything
        /// transparent was drawn, bent a little. The water is transparent (queue 2502), so it is
        /// not in that texture and the haze shows the first opaque surface under it. The game's
        /// sea has one for this: <c>oceanPlane</c> carries a child, <c>oceanUnderlay</c>, the same
        /// mesh 2 m lower in the opaque blue-grey <c>OceanUnderlay</c>, and over the sea the haze
        /// is that colour. A lake had nothing between its sheet and the bed it was carved from,
        /// so over a lake the haze showed the meadow: a green plume behind every jet.
        ///
        /// So each sheet gets the same: a child drawing the sheet's own mesh 2 m lower, in the
        /// game's own material, with the game underlay's renderer settings and layer, and no
        /// collider. Where a lake is shallower than that the bed hides it, as the sand does at
        /// sea. Called by <see cref="AnchorLakeWater"/>, whose material is what tells a lake sheet
        /// apart, with the renderers its walk found wearing it. Without the game's underlay the
        /// lakes are left as they were. Laid once per sheet, so a reload of the same instance adds
        /// nothing.
        /// </summary>
        static void LayLakeUnderlays(MapSettings instance, List<MeshRenderer> candidates)
        {
            if (instance == null || _lakeWater == null || candidates.Count == 0) return;

            MeshRenderer game = null;
            try
            {
                game = GameOceanUnderlay();
            }
            catch (Exception e)
            {
                // A game update that renamed LevelInfo.waterPlane: fall back to the material by name.
                Plugin.LogDebug($"could not read LevelInfo.waterPlane ({e.GetType().Name}: {e.Message})");
            }

            Material underlay = game != null ? game.sharedMaterial : null;
            if (underlay == null)
                foreach (Material candidate in Resources.FindObjectsOfTypeAll<Material>())
                {
                    if (candidate == null || candidate.name != OceanUnderlayMaterialName) continue;

                    underlay = candidate;
                    break;
                }

            int laid = 0, sheets = 0;
            foreach (MeshRenderer sheet in candidates)
            {
                if (!sheet.TryGetComponent(out MeshFilter filter) || filter.sharedMesh == null) continue;

                sheets++;
                if (underlay == null || sheet.transform.Find(LakeUnderlayName) != null) continue;

                var child = new GameObject(LakeUnderlayName);
                child.layer = game != null ? game.gameObject.layer : sheet.gameObject.layer;
                child.transform.SetParent(sheet.transform, worldPositionStays: false);

                // Lowered in world space, not local: the depth is metres of water whatever the
                // sheet's scale.
                child.transform.position = sheet.transform.position - Vector3.up * LakeUnderlayDepth;

                child.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
                var renderer = child.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = underlay;

                MeshRenderer like = game != null ? game : sheet;
                renderer.shadowCastingMode = game != null ? game.shadowCastingMode : ShadowCastingMode.Off;
                renderer.receiveShadows = like.receiveShadows;
                renderer.lightProbeUsage = like.lightProbeUsage;
                renderer.reflectionProbeUsage = like.reflectionProbeUsage;
                renderer.motionVectorGenerationMode = like.motionVectorGenerationMode;
                renderer.allowOcclusionWhenDynamic = like.allowOcclusionWhenDynamic;
                renderer.renderingLayerMask = like.renderingLayerMask;
                laid++;
            }

            if (sheets == 0) return;

            if (underlay == null)
                Plugin.LogWarning($"{instance.name}: the game's sea underlay ('{OceanUnderlayMaterialName}') was not " +
                                  $"found, so the {sheets} lake(s) have none and a jet's exhaust over them shows the " +
                                  "lake bed's colour");
            else if (laid > 0)
                Plugin.LogDebug($"{instance.name}: laid '{underlay.name}' {LakeUnderlayDepth:0} m under {laid} lake " +
                                $"surface(s), as under the sea" + (game == null ? " (found by name)" : ""));
        }

        /// <summary>
        /// The renderer the game draws its sea underlay with: the child of <c>LevelInfo.waterPlane</c>
        /// (<c>oceanPlane/oceanUnderlay</c>), or null.
        ///
        /// A method of its own, never inlined, so that a game update removing the member throws at
        /// this call, inside <see cref="LayLakeUnderlays"/>'s guard.
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static MeshRenderer GameOceanUnderlay()
        {
            LevelInfo level = NetworkSceneSingleton<LevelInfo>.i;
            Transform plane = level != null ? level.waterPlane : null;
            if (plane == null) return null;

            foreach (MeshRenderer renderer in plane.GetComponentsInChildren<MeshRenderer>(true))
                if (renderer.transform != plane && renderer.sharedMaterial != null)
                    return renderer;

            return null;
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
            if (!Plugin.DebugEnabled) return;

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
