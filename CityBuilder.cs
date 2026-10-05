using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace CustomMaps
{
    /// <summary>
    /// Puts the map's buildings on the ground by cloning the base game's.
    ///
    /// The bundle carries no game types, so it cannot ship a building — only a table of
    /// where buildings go. The buildings themselves are borrowed at load from Ignus,
    /// whose <c>suburbs_block*</c> family is the only low-rise housing in the game and
    /// the only thing on either shipped map that reads as European.
    ///
    /// What is borrowed is the look and nothing else. Each donor is copied once into a
    /// template stripped down to its renderers, and every building is a copy of that.
    /// The donor's own components could not come along anyway:
    ///
    /// <c>MapBuildingSet.Awake</c> calls <c>base.Identity.OnStartServer.AddListener(...)</c>,
    /// and Mirage's <c>Identity</c> getter throws outright when no <c>NetworkIdentity</c>
    /// exists up the hierarchy — a clone reparented under our map has none, so that is a
    /// hard exception rather than a degraded building. <c>MapBuilding.TakeDamage</c> with
    /// a null set logs an error on <em>every hit</em>, which under fire is a real cost.
    ///
    /// Leaving both out is also what keeps <c>NetworkMap.NetworkObjects</c> empty, and an
    /// empty object table is what makes this map safe to play with strangers. The price
    /// is that these buildings do not take damage, which is the right trade for a first
    /// pass: a destructible city means putting bundle content into the wire protocol.
    ///
    /// The donor's collider is left out as well. A building collides as a box, merged with
    /// the rest of its tile's into one collider (<see cref="CityColliders"/>), because sixty
    /// thousand colliders made every floating-origin shift a visible hitch.
    /// </summary>
    internal static class CityBuilder
    {
        /// <summary>Name of the map root's child holding the buildings.</summary>
        public const string CityRoot = "Cities";

        /// <summary>One catalogue type, ready to be placed.</summary>
        sealed class Template
        {
            /// <summary>The stripped copy of the donor, under a switched-off holder; null when
            /// nothing is drawn.</summary>
            public GameObject Root;

            /// <summary>The template's renderer when it has exactly one, on its root, which is
            /// every donor the game ships: cloning the renderer hands back the clone's own, with
            /// no lookup. Null otherwise, and the clone is searched.</summary>
            public MeshRenderer Single;

            public bool Commercial;

            /// <summary>The donor's extent, which places the building and is its collision.</summary>
            public BuildingBox Box;

            /// <summary>False when the donor has no solid collider: its copies never collided
            /// either, so they get no box.</summary>
            public bool Collides;
        }

        public static void Populate(MapSettings instance)
        {
            if (instance == null || Plugin.Disabled) return;
            if (instance.transform.Find(CityRoot) != null) return;   // already built

            LoadedMap map = MapFor(instance);
            string asset = map?.Manifest?.CityPlacements;
            if (string.IsNullOrEmpty(asset)) return;

            var data = map.Asset<TextAsset>(asset);
            if (data == null)
            {
                Plugin.LogWarning($"{instance.name}: manifest names city placements '{asset}' but the " +
                                  "bundle has no TextAsset by that name — the map will have no buildings");
                return;
            }

            List<Placement> placements;
            try
            {
                placements = CityData.Read(data.bytes);
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"{instance.name}: '{asset}' is not readable city data ({e.Message})");
                return;
            }

            Dictionary<string, GameObject> donors = ResolveDonors(out int missing);
            if (donors.Count == 0)
            {
                Plugin.LogWarning($"{instance.name}: none of the {CityCatalogue.Entries.Length} catalogue " +
                                  $"buildings were found on '{CityCatalogue.DonorMap}' — no buildings");
                return;
            }

            var clock = Stopwatch.StartNew();

            var root = new GameObject(CityRoot);
            root.transform.SetParent(instance.transform, worldPositionStays: false);

            // With nothing to draw for (a dedicated server), the buildings are not made at all, only
            // their collision: sixty thousand renderers nothing would ever switch on cost a third of
            // a second and their memory for nothing.
            bool drawn = !CityTileGate.Headless;

            // Switched off, so nothing on a template ever wakes; gone again before this returns.
            GameObject holder = null;
            if (drawn)
            {
                holder = new GameObject("templates");
                holder.SetActive(false);
                holder.transform.SetParent(root.transform, worldPositionStays: false);
            }

            Template[] templates = BuildTemplates(donors, holder != null ? holder.transform : null);

            CityTileGate gate = drawn ? root.AddComponent<CityTileGate>() : null;
            var proxies = new CityProxyBuilder(CityTileGate.TileSize);

            // Instantiate takes a world pose; the placements are in the map's space.
            Matrix4x4 toWorld = root.transform.localToWorldMatrix;
            Quaternion turn = root.transform.rotation;

            int placed = 0, skipped = 0;

            foreach (Placement placement in placements)
            {
                Template template = placement.Type < templates.Length ? templates[placement.Type] : null;
                if (template == null) { skipped++; continue; }

                // The generator reasons about where a building's FOOTPRINT lands — that is
                // what it clears roads and neighbours against — but a donor's geometry is
                // only centred on its own transform if the artist happened to put the pivot
                // in the middle. Any offset silently slides every copy of that building off
                // the site it was cleared for, in a direction that turns with the yaw. So
                // the pivot is measured once per donor and taken back out here.
                //
                // Plan only: Y is left alone, because placement.Y is the height the base
                // should sit at, not the height of the middle of the building.
                CityProxies.Origin(placement.X, placement.Z, placement.Yaw,
                                   template.Box.CentreX, template.Box.CentreZ, out float x, out float z);

                if (drawn)
                {
                    Vector3 at = toWorld.MultiplyPoint3x4(new Vector3(x, placement.Y, z));
                    Quaternion facing = turn * Quaternion.Euler(0f, placement.Yaw, 0f);

                    if (template.Single != null)
                        gate.Add(x, z, UnityEngine.Object.Instantiate(template.Single, at, facing, root.transform),
                                 template.Commercial);
                    else
                        gate.Add(x, z, UnityEngine.Object.Instantiate(template.Root, at, facing, root.transform)
                                           .GetComponentsInChildren<MeshRenderer>(true), template.Commercial);
                }

                if (template.Collides) proxies.Add(template.Box, x, placement.Y, z, placement.Yaw);

                placed++;
            }

            if (holder != null) UnityEngine.Object.DestroyImmediate(holder);
            double buildings = clock.Elapsed.TotalMilliseconds;

            // Collision before the gate, and on its own guard: it is what the towns cannot do
            // without, and this runs inside the game's LoadMap, which an exception would abort.
            string collision;
            try
            {
                collision = CityColliders.Build(root.transform, proxies);
            }
            catch (Exception e)
            {
                collision = "failed";
                Plugin.LogError($"{instance.name}: building the buildings' collision threw, so some or all of " +
                                $"them will stop nothing: {e}");
            }

            if (gate != null) gate.Bind();

            Plugin.LogDebug($"{instance.name}: {placed:N0} building(s) from {donors.Count} donor type(s) in {buildings:0} ms" +
                            (drawn ? "" : " (no graphics: collision only, none drawn)") +
                            (missing > 0 ? $", {missing} catalogue entr(ies) not found" : "") +
                            (skipped > 0 ? $", {skipped:N0} placement(s) skipped" : "") +
                            $"; collision: {collision}");
        }

        /// <summary>
        /// One template per catalogue type whose donor was found, indexed like the catalogue: the
        /// donor's box and collision, and, unless <paramref name="holder"/> is null because nothing
        /// is to be drawn, the donor copied under it and stripped to its renderers, which are
        /// switched off so every building is born dark (<see cref="CityTileGate"/>).
        /// </summary>
        static Template[] BuildTemplates(Dictionary<string, GameObject> donors, Transform holder)
        {
            var templates = new Template[CityCatalogue.Entries.Length];

            for (int type = 0; type < templates.Length; type++)
            {
                BuildingType entry = CityCatalogue.Entries[type];
                if (!donors.TryGetValue(entry.Name, out GameObject donor)) continue;

                var template = new Template
                {
                    Commercial = entry.Tier == CityCatalogue.Commercial,
                    Box = Measure(donor, entry.Name, out bool measured),
                };
                template.Collides = measured && HasSolidCollider(donor, entry.Name);

                if (holder != null)
                {
                    GameObject copy = UnityEngine.Object.Instantiate(donor, holder);
                    copy.name = entry.Name;
                    StripToRenderers(copy);

                    MeshRenderer[] renderers = copy.GetComponentsInChildren<MeshRenderer>(true);
                    foreach (MeshRenderer renderer in renderers) renderer.enabled = false;

                    template.Root = copy;
                    template.Single = renderers.Length == 1 && renderers[0].gameObject == copy ? renderers[0] : null;
                }

                templates[type] = template;
            }

            return templates;
        }

        /// <summary>
        /// Leaves a copy of a donor with nothing but what draws it: transforms, mesh filters,
        /// mesh renderers and any LODGroup.
        ///
        /// DestroyImmediate, not Destroy: Destroy is deferred to the end of the frame, and the
        /// copies are cloned before then. Behaviours go first and last-added first, because Unity
        /// refuses to remove a component another one on the object still requires. A renderer
        /// the donor had switched off draws nothing and goes too, so that the gate owns every
        /// renderer left.
        /// </summary>
        static void StripToRenderers(GameObject copy)
        {
            MonoBehaviour[] behaviours = copy.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = behaviours.Length - 1; i >= 0; i--)
                if (behaviours[i] != null) UnityEngine.Object.DestroyImmediate(behaviours[i]);

            Component[] components = copy.GetComponentsInChildren<Component>(true);
            for (int i = components.Length - 1; i >= 0; i--)
            {
                Component component = components[i];
                if (component == null || component is Transform || component is MeshFilter || component is LODGroup) continue;
                if (component is MeshRenderer renderer && renderer.enabled) continue;

                UnityEngine.Object.DestroyImmediate(component);
            }
        }

        /// <summary>
        /// True when the donor has a collider that is on and solid, which every donor the game
        /// ships has: one non-convex MeshCollider of its own render mesh on layer <c>Statics</c>
        /// with no PhysicMaterial. The box keeps the layer and the empty material; a donor that
        /// one day differs is logged, because a merged collider cannot carry it.
        /// </summary>
        static bool HasSolidCollider(GameObject donor, string name)
        {
            bool solid = false;

            foreach (Collider collider in donor.GetComponentsInChildren<Collider>(true))
            {
                if (!collider.enabled || collider.isTrigger) continue;
                solid = true;

                if (collider.gameObject.layer != PhysicsLayers.Statics || collider.sharedMaterial != null)
                    Plugin.LogDebug($"donor '{name}' collides on layer {collider.gameObject.layer} with " +
                                    $"material '{(collider.sharedMaterial != null ? collider.sharedMaterial.name : "none")}'; " +
                                    "its box is on Statics with none");
            }

            if (!solid) Plugin.LogDebug($"donor '{name}' has no solid collider — its buildings get no box");
            return solid;
        }

        /// <summary>
        /// The box a donor's geometry fills, in its own frame and at its own scale: every mesh
        /// under it, corner by corner, so a child turned inside the donor is still enclosed.
        /// Its plan centre is where the pivot should have been, and the box is the building's
        /// collision.
        ///
        /// Measured rather than assumed, and cached, because it is a property of art nobody here
        /// controls and it changes with a game update. Logged the first time each donor is seen
        /// so the size of the pivot correction is on the record rather than invisible; a donor
        /// with no mesh is cached as that, so it is reported once rather than at every load.
        /// </summary>
        static BuildingBox Measure(GameObject donor, string name, out bool measured)
        {
            if (_boxes.TryGetValue(name, out BuildingBox? cached))
            {
                measured = cached.HasValue;
                return cached.GetValueOrDefault();
            }

            measured = false;
            Vector3 min = Vector3.zero, max = Vector3.zero;

            Matrix4x4 toDonor = donor.transform.worldToLocalMatrix;

            foreach (MeshFilter filter in donor.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null) continue;

                Bounds local = mesh.bounds;
                Matrix4x4 toLocal = toDonor * filter.transform.localToWorldMatrix;

                // Every corner, because a rotated child's bounds do not survive being
                // transformed by their centre and extent alone.
                for (int corner = 0; corner < 8; corner++)
                {
                    var point = new Vector3(
                        (corner & 1) == 0 ? local.min.x : local.max.x,
                        (corner & 2) == 0 ? local.min.y : local.max.y,
                        (corner & 4) == 0 ? local.min.z : local.max.z);

                    Vector3 at = toLocal.MultiplyPoint3x4(point);

                    if (!measured) { min = max = at; measured = true; continue; }

                    min = Vector3.Min(min, at);
                    max = Vector3.Max(max, at);
                }
            }

            if (!measured)
            {
                _boxes[name] = null;
                Plugin.LogDebug($"donor '{name}' has no mesh — placed as it is, with no box");
                return default;
            }

            // A copy keeps the donor's own scale, so the box is measured at it.
            Vector3 scale = donor.transform.localScale;
            Vector3 a = Vector3.Scale(min, scale), b = Vector3.Scale(max, scale);
            min = Vector3.Min(a, b);
            max = Vector3.Max(a, b);

            var box = new BuildingBox
            {
                MinX = min.x, MinY = min.y, MinZ = min.z,
                MaxX = max.x, MaxY = max.y, MaxZ = max.z,
            };

            _boxes[name] = box;

            if (Mathf.Sqrt(box.CentreX * box.CentreX + box.CentreZ * box.CentreZ) > 0.5f)
                Plugin.LogDebug($"donor '{name}' is off-centre by ({box.CentreX:0.#}, {box.CentreZ:0.#}) m " +
                                $"over {max.x - min.x:0} x {max.z - min.z:0} m — corrected at placement");

            return box;
        }

        /// <summary>Each donor's box by name, or null for a donor with no mesh.</summary>
        static readonly Dictionary<string, BuildingBox?> _boxes =
            new Dictionary<string, BuildingBox?>(StringComparer.Ordinal);

        /// <summary>
        /// Finds the catalogue's buildings on the donor map.
        ///
        /// Resolved by <em>mesh</em> name rather than GameObject name, because the mesh is
        /// what the catalogue's measurements were taken from — matching on anything else
        /// risks cloning a building whose dimensions are not the ones the placement pass
        /// assumed, which would put it through its neighbours.
        /// </summary>
        static Dictionary<string, GameObject> ResolveDonors(out int missing)
        {
            var donors = new Dictionary<string, GameObject>(StringComparer.Ordinal);
            missing = 0;

            MapSettings donorMap = MapFixups.ShippedMap(CityCatalogue.DonorMap);
            if (donorMap == null)
            {
                missing = CityCatalogue.Entries.Length;
                return donors;
            }

            foreach (MapBuilding building in donorMap.GetComponentsInChildren<MapBuilding>(true))
            {
                var renderer = building.GetComponentInChildren<MeshRenderer>(true);
                var filter = renderer != null ? renderer.GetComponent<MeshFilter>() : null;
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null) continue;

                if (CityCatalogue.IndexOf(mesh.name) < 0) continue;
                if (donors.ContainsKey(mesh.name)) continue;

                donors[mesh.name] = building.gameObject;
            }

            foreach (BuildingType entry in CityCatalogue.Entries)
            {
                if (donors.ContainsKey(entry.Name)) continue;

                missing++;
                Plugin.LogWarning($"catalogue building '{entry.Name}' not found on '{CityCatalogue.DonorMap}' — " +
                                  "a game update may have moved or renamed it");
            }

            return donors;
        }

        static LoadedMap MapFor(MapSettings instance)
        {
            foreach (LoadedMap map in BundleLoader.Maps)
            {
                MapSettings prefab = MapFixups.PreparedPrefabFor(map);
                if (prefab == null) continue;

                // The instance is a clone, so its name carries the prefab's with a suffix.
                if (instance.name.StartsWith(prefab.name, StringComparison.Ordinal)) return map;
            }

            return null;
        }
    }
}
