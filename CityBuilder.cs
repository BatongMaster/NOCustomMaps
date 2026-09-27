using System;
using System.Collections.Generic;
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
    /// Two components come with every clone and both have to go:
    ///
    /// <c>MapBuildingSet.Awake</c> calls <c>base.Identity.OnStartServer.AddListener(...)</c>,
    /// and Mirage's <c>Identity</c> getter throws outright when no <c>NetworkIdentity</c>
    /// exists up the hierarchy — a clone reparented under our map has none, so that is a
    /// hard exception rather than a degraded building. <c>MapBuilding.TakeDamage</c> with
    /// a null set logs an error on <em>every hit</em>, which under fire is a real cost.
    ///
    /// Stripping both is also what keeps <c>NetworkMap.NetworkObjects</c> empty, and an
    /// empty object table is what makes this map safe to play with strangers. The price
    /// is that these buildings do not take damage, which is the right trade for a first
    /// pass: a destructible city means putting bundle content into the wire protocol.
    /// </summary>
    internal static class CityBuilder
    {
        /// <summary>Name of the map root's child holding the buildings.</summary>
        public const string CityRoot = "Cities";

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

            var root = new GameObject(CityRoot);
            root.transform.SetParent(instance.transform, worldPositionStays: false);

            int placed = 0, skipped = 0;
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (Placement placement in placements)
            {
                if (placement.Type >= CityCatalogue.Entries.Length) { skipped++; continue; }

                string name = CityCatalogue.Entries[placement.Type].Name;
                if (!donors.TryGetValue(name, out GameObject donor)) { skipped++; continue; }

                GameObject clone = UnityEngine.Object.Instantiate(donor, root.transform);

                // The generator reasons about where a building's FOOTPRINT lands — that is
                // what it clears roads and neighbours against — but a donor's geometry is
                // only centred on its own transform if the artist happened to put the pivot
                // in the middle. Any offset silently slides every copy of that building off
                // the site it was cleared for, in a direction that turns with the yaw. So
                // the pivot is measured once per donor and taken back out here.
                //
                // Plan only: Y is left alone, because placement.Y is the height the base
                // should sit at, not the height of the middle of the building.
                Vector2 pivot = PivotOffset(donor, name);
                Vector3 shift = Quaternion.Euler(0f, placement.Yaw, 0f) * new Vector3(pivot.x, 0f, pivot.y);

                clone.transform.localPosition =
                    new Vector3(placement.X - shift.x, placement.Y, placement.Z - shift.z);
                clone.transform.localRotation = Quaternion.Euler(0f, placement.Yaw, 0f);
                clone.name = name;

                Strip(clone);

                counts.TryGetValue(name, out int count);
                counts[name] = count + 1;
                placed++;
            }

            root.AddComponent<CityTileGate>().Bind(root.transform);

            Plugin.LogDebug($"{instance.name}: {placed:N0} building(s) from {donors.Count} donor type(s)" +
                            (missing > 0 ? $", {missing} catalogue entr(ies) not found" : "") +
                            (skipped > 0 ? $", {skipped:N0} placement(s) skipped" : ""));
        }

        /// <summary>
        /// Removes the components that would throw, spam, or drag the clone into the
        /// networking layer.
        /// </summary>
        static void Strip(GameObject clone)
        {
            // DestroyImmediate, not Destroy: the clone is born inactive but Destroy is
            // deferred to the end of frame, and the map root is activated before then.
            // A surviving MapBuildingSet would reach Awake and throw.
            foreach (MapBuildingSet set in clone.GetComponentsInChildren<MapBuildingSet>(true))
                UnityEngine.Object.DestroyImmediate(set);

            foreach (MapBuilding building in clone.GetComponentsInChildren<MapBuilding>(true))
                UnityEngine.Object.DestroyImmediate(building);
        }

        /// <summary>
        /// Finds the catalogue's buildings on the donor map.
        ///
        /// Resolved by <em>mesh</em> name rather than GameObject name, because the mesh is
        /// what the catalogue's measurements were taken from — matching on anything else
        /// risks cloning a building whose dimensions are not the ones the placement pass
        /// assumed, which would put it through its neighbours.
        /// </summary>
        /// <summary>
        /// Where a donor's geometry sits relative to its own transform, in the plan, in the
        /// donor's local frame.
        ///
        /// Measured from the combined renderer bounds rather than assumed, and cached,
        /// because it is a property of art nobody here controls and it changes with a game
        /// update. Logged the first time each donor is seen so the size of the correction
        /// is on the record rather than invisible.
        /// </summary>
        static Vector2 PivotOffset(GameObject donor, string name)
        {
            if (_pivots.TryGetValue(name, out Vector2 cached)) return cached;

            bool any = false;
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

                    if (!any) { min = max = at; any = true; continue; }

                    min = Vector3.Min(min, at);
                    max = Vector3.Max(max, at);
                }
            }

            Vector2 offset = any
                ? new Vector2((min.x + max.x) * 0.5f, (min.z + max.z) * 0.5f)
                : Vector2.zero;

            _pivots[name] = offset;

            if (any && offset.magnitude > 0.5f)
                Plugin.LogDebug($"donor '{name}' is off-centre by ({offset.x:0.#}, {offset.y:0.#}) m " +
                                $"over {max.x - min.x:0} x {max.z - min.z:0} m — corrected at placement");

            return offset;
        }

        static readonly Dictionary<string, Vector2> _pivots =
            new Dictionary<string, Vector2>(StringComparer.Ordinal);

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
