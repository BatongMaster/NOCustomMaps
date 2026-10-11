using System;
using System.Collections.Generic;
using UnityEngine;

namespace CustomMaps
{
    /// <summary>
    /// The water level anywhere on the map in play: the sea's, or a raised lake's surface over the
    /// lake's footprint. The one place the plugin answers "where is the water here?", and the seam
    /// a later feature that floats boats on a lake is meant to use.
    ///
    /// Nuclear Option has no water objects. Its sea is the plane global y = 0, and the game decides
    /// what is in the water by comparing heights with it: <c>Datum.LocalSeaY</c>, <c>GlobalY()</c>
    /// against 0, <c>Datum.WaterPlane()</c>. A raised lake (NOMapForge's <c>Water/Lake_&lt;name&gt;</c>,
    /// a flat sheet over a dug bed) is invisible to all of that. The patches in
    /// <see cref="Patches.LakeWaterPatches"/> point those comparisons here instead, and every
    /// answer here is the sea's own, to the bit, wherever there is no lake, so a base map, and a
    /// custom map away from its lakes, behave exactly as they did.
    ///
    /// The lakes are read from the sheets themselves when a map is prepared
    /// (<see cref="Read"/>), so any bundle that draws its lakes works, whenever it was built:
    /// nothing is needed from the bundle but the sheet meshes, which ship readable. They become the
    /// lakes of play when the map is applied (<see cref="MapApplied"/>) and stop being so when
    /// another map is, or when the map's instance is destroyed.
    ///
    /// For a boat feature: <see cref="GlobalWaterY"/> and <see cref="TryGetLake"/> answer in
    /// global coordinates, which do not move with the floating origin, and the lake index keeps
    /// two lakes apart (a lane that changes lake index has left the water).
    /// </summary>
    internal static class LakeWater
    {
        /// <summary>Lakes read from each prepared map, by prefab name. A map with none is not here.</summary>
        static readonly Dictionary<string, LakeSurfaces> _prepared = new Dictionary<string, LakeSurfaces>(StringComparer.Ordinal);

        /// <summary>What <see cref="Read"/> found on each prepared map, for its validation line.</summary>
        static readonly Dictionary<string, string> _readings = new Dictionary<string, string>(StringComparer.Ordinal);

        static LakeFrame _frame;
        static GameObject _frameOwner;

        /// <summary>The lakes of the map in play, on the datum, or null when it has none.</summary>
        public static LakeFrame Frame => _frame;

        /// <summary>True while the map in play has raised lakes.</summary>
        public static bool Active => _frame != null;

        // ---------------------------------------------------------------- the seam

        /// <summary>The global height of the water at a global point (x, z): a lake's surface over
        /// a lake, 0 (the sea) anywhere else, and 0 everywhere when no map with lakes is in play.</summary>
        public static float GlobalWaterY(float globalX, float globalZ)
        {
            LakeFrame frame = _frame;
            return frame != null ? frame.GlobalWaterY(globalX, globalZ) : 0f;
        }

        /// <summary>The lake over a global point (its index in <see cref="Frame"/>'s surfaces) and the
        /// global height of its surface. False over the sea or dry land.</summary>
        public static bool TryGetLake(float globalX, float globalZ, out int lake, out float surfaceGlobalY)
        {
            LakeFrame frame = _frame;
            if (frame != null) return frame.TryGetLake(globalX, globalZ, out lake, out surfaceGlobalY);

            lake = -1;
            surfaceGlobalY = 0f;
            return false;
        }

        // ---------------------------------------------------------------- what the patched game calls
        //
        // Each stands in for one of the game's own sea-level expressions and returns exactly what it
        // did when there is no lake under the point. Value and by-reference forms, because the
        // patched IL holds a vector one way or the other.

        /// <summary>Stands in for <c>Datum.LocalSeaY</c>: the local height of the water under a local
        /// point.</summary>
        public static float LocalWaterY(Vector3 position)
        {
            Vector3 origin = Datum.originPosition;
            LakeFrame frame = _frame;
            if (frame == null) return origin.y;
            return frame.LocalWaterY(position.x, position.z, origin.x, origin.y, origin.z);
        }

        public static float LocalWaterY(ref Vector3 position) => LocalWaterY(position);

        /// <summary>Stands in for <c>Datum.LocalSeaY</c> where the game holds a global point (and,
        /// as the game does there, compares a global height with it).</summary>
        public static float LocalWaterY(GlobalPosition position)
        {
            float sea = Datum.originPosition.y;
            LakeFrame frame = _frame;
            return frame != null ? sea + frame.GlobalWaterY(position.x, position.z) : sea;
        }

        public static float LocalWaterY(ref GlobalPosition position) => LocalWaterY(position);

        /// <summary>Stands in for a global 0, the sea's global height, at a global point.</summary>
        public static float GlobalWaterY(GlobalPosition position)
        {
            LakeFrame frame = _frame;
            return frame != null ? frame.GlobalWaterY(position.x, position.z) : 0f;
        }

        public static float GlobalWaterY(ref GlobalPosition position) => GlobalWaterY(position);

        /// <summary>The global height of the water under a local point: stands in for the 0 a
        /// <c>GlobalY()</c> is compared with.</summary>
        public static float GlobalWaterYAt(Vector3 position)
        {
            LakeFrame frame = _frame;
            if (frame == null) return 0f;

            Vector3 origin = Datum.originPosition;
            return frame.GlobalWaterY(position.x - origin.x, position.z - origin.z);
        }

        /// <summary>Stands in for <c>GlobalY()</c> where the game uses it as the height above the
        /// sea: the height above the water under the point.</summary>
        public static float HeightAboveWater(Vector3 position)
        {
            Vector3 origin = Datum.originPosition;
            float globalY = position.y - origin.y;
            LakeFrame frame = _frame;
            if (frame == null) return globalY;
            return globalY - frame.GlobalWaterY(position.x - origin.x, position.z - origin.z);
        }

        /// <summary>Stands in for a <c>GlobalPosition</c>'s y where the game uses it as the height
        /// above the sea.</summary>
        public static float HeightAboveWater(GlobalPosition position)
        {
            LakeFrame frame = _frame;
            if (frame == null) return position.y;
            return position.y - frame.GlobalWaterY(position.x, position.z);
        }

        public static float HeightAboveWater(ref GlobalPosition position) => HeightAboveWater(position);

        /// <summary>A lake's local height under a local point, or <paramref name="elsewhere"/> where
        /// there is no lake. For the game's literal heights that are not the sea's in local terms
        /// (a propeller's water spray at local y = 0): they are left as they are off a lake.</summary>
        public static float LakeLocalYOr(Vector3 position, float elsewhere)
        {
            LakeFrame frame = _frame;
            if (frame == null) return elsewhere;

            Vector3 origin = Datum.originPosition;
            return frame.TryGetLake(position.x - origin.x, position.z - origin.z, out _, out float surface)
                ? origin.y + surface
                : elsewhere;
        }

        public static float LakeLocalYOr(ref Vector3 position, float elsewhere) => LakeLocalYOr(position, elsewhere);

        /// <summary>
        /// Stands in for <c>Plane.Raycast</c> on <c>Datum.WaterPlane()</c>: the sea's plane, and
        /// nearer than it any lake's surface the ray meets inside the lake's footprint. A plane
        /// that is not the sea's is raycast as it is.
        /// </summary>
        public static bool Raycast(ref Plane plane, Ray ray, out float enter)
        {
            bool hit = plane.Raycast(ray, out enter);

            LakeFrame frame = _frame;
            if (frame == null) return hit;

            // The sea's plane: Plane(Vector3.up, Datum.origin.position).
            Vector3 origin = Datum.originPosition;
            if (plane.normal != Vector3.up || Mathf.Abs(plane.distance + origin.y) > 0.01f) return hit;

            Vector3 start = ray.origin, direction = ray.direction;
            if (!frame.RaycastLakes(origin.x, origin.y, origin.z, start.x, start.y, start.z,
                                    direction.x, direction.y, direction.z, out float lake))
                return hit;

            if (hit && !(lake < enter)) return true;

            enter = lake;
            return true;
        }

        /// <summary>
        /// How deep an aircraft part is in a lake by the sea's own measure, <c>AeroJob_Math</c>'s
        /// <c>-(globalY - collisionSize.y)</c> with the lake's surface for the sea's: positive once
        /// the part reaches the water. Negative infinity when the part is not over a lake, or is
        /// clear above every lake (where the depth would only be negative).
        /// </summary>
        public static float LakeDepth(LakeFrame frame, Vector3 liftPosition, float collisionHeight)
        {
            Vector3 origin = Datum.originPosition;

            // Runs for every aircraft part on every physics step while a lake map is in play. A part
            // whose lowest point is above the highest lake is in none, and nearly every part in the air
            // is, so it is let go before the lookup.
            if (liftPosition.y - origin.y - collisionHeight > frame.HighestSurfaceY) return float.NegativeInfinity;

            if (!frame.TryGetLake(liftPosition.x - origin.x, liftPosition.z - origin.z, out _, out float surface))
                return float.NegativeInfinity;

            return 0f - (liftPosition.y - origin.y - surface - collisionHeight);
        }

        // ---------------------------------------------------------------- the lakes of each map

        /// <summary>
        /// Reads a prepared map's raised lakes from its sheets (<c>Water/Lake_&lt;name&gt;</c>, each
        /// a flat mesh) and keeps them for when the map is applied. Returns how many it found; a
        /// map without a <c>Water</c> root has none. Runs on the prefab, whose own coordinates are
        /// global ones once the game instantiates it at the datum. Never throws: a map whose lakes
        /// cannot be read still loads, with lakes that are only pictures, as before.
        /// </summary>
        public static int Read(LoadedMap map, GameObject root, string waterRoot)
        {
            string key = map?.PrefabName;
            if (key == null || root == null) return 0;

            _prepared.Remove(key);
            _readings.Remove(key);

            try
            {
                return ReadSheets(map, key, root, waterRoot);
            }
            catch (Exception e)
            {
                _prepared.Remove(key);
                _readings[key] = $"could not be read  WARN — the lakes are pictures, not water ({e.GetType().Name}: {e.Message})";
                Plugin.LogWarning($"{map.Name()}: lake water could not be read: {e}");
                return 0;
            }
        }

        static int ReadSheets(LoadedMap map, string key, GameObject root, string waterRoot)
        {
            Transform water = root.transform.Find(waterRoot);
            if (water == null)
            {
                _readings[key] = "none — this map has no lakes above the sea";
                return 0;
            }

            var sources = new List<LakeSurfaces.Source>();
            var problems = new List<string>();
            Matrix4x4 toRoot = root.transform.worldToLocalMatrix;

            foreach (MeshFilter filter in water.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                string name = filter.name;
                if (mesh == null) { problems.Add($"{name} has no mesh"); continue; }
                if (!mesh.isReadable) { problems.Add($"{name}'s mesh is not readable"); continue; }

                Matrix4x4 toMap = toRoot * filter.transform.localToWorldMatrix;
                Vector3[] vertices = mesh.vertices;
                int[] triangles = mesh.triangles;
                if (vertices.Length == 0 || triangles.Length < 3) { problems.Add($"{name} has no triangles"); continue; }

                var xs = new float[vertices.Length];
                var zs = new float[vertices.Length];
                float low = float.MaxValue, high = float.MinValue;

                // Only the vertices the triangles use: a sheet is a grid over its lake's box and
                // leaves most of it unreferenced.
                var used = new bool[vertices.Length];
                bool indexed = true;
                foreach (int index in triangles)
                {
                    if (index < 0 || index >= vertices.Length) { indexed = false; break; }
                    used[index] = true;
                }

                if (!indexed) { problems.Add($"{name} has triangles past its vertices"); continue; }

                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector3 p = toMap.MultiplyPoint3x4(vertices[i]);
                    xs[i] = p.x;
                    zs[i] = p.z;
                    if (!used[i]) continue;

                    low = Math.Min(low, p.y);
                    high = Math.Max(high, p.y);
                }

                // A lake is flat. A sheet that is not is something else under Water, and water at
                // one height over it would be wrong somewhere.
                if (high - low > 0.05f) { problems.Add($"{name} is not flat ({low:0.##} to {high:0.##} m)"); continue; }

                sources.Add(new LakeSurfaces.Source
                {
                    Name = name.StartsWith("Lake_", StringComparison.Ordinal) ? name.Substring(5) : name,
                    SurfaceY = (low + high) * 0.5f,
                    X = xs,
                    Z = zs,
                    Triangles = triangles,
                });
            }

            LakeSurfaces surfaces = LakeSurfaces.Build(sources);

            if (surfaces.Count > 0) _prepared[key] = surfaces;
            _readings[key] = Describe(surfaces, problems, water);

            if (problems.Count > 0)
                Plugin.LogWarning($"{map.Name()}: lake water left out of {problems.Count} sheet(s): {string.Join("; ", problems)}");

            return surfaces.Count;
        }

        static string Describe(LakeSurfaces surfaces, List<string> problems, Transform water)
        {
            if (surfaces.Count == 0)
                return problems.Count > 0
                    ? $"{problems.Count} lake sheet(s), none usable  WARN — they are pictures: {string.Join("; ", problems)}"
                    : "a Water root with no lake above the sea";

            double area = 0.0;
            int triangles = 0;
            float lowest = float.MaxValue, highest = float.MinValue;
            for (int i = 0; i < surfaces.Count; i++)
            {
                area += surfaces.Area(i);
                triangles += surfaces.Triangles(i);
                lowest = Math.Min(lowest, surfaces.SurfaceY(i));
                highest = Math.Max(highest, surfaces.SurfaceY(i));
            }

            string text = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "{0} lake(s), {1:N1} km2 over {2:N0} triangles, surfaces {3:0.0} to {4:0.0} m",
                surfaces.Count, area / 1e6, triangles, lowest, highest);

            // A bundle built between 27 September and the water lookup ships trigger boxes under
            // each sheet, switched off. They stay off: the lookup is the water now.
            int volumes = water.GetComponentsInChildren<BoxCollider>(true).Length;
            if (volumes > 0) text += $", {volumes} old trigger volume(s) left off";

            if (problems.Count > 0) text += $"  WARN — {problems.Count} sheet(s) left out: {string.Join("; ", problems)}";
            return text;
        }

        /// <summary>The validation report's account of a map's lakes, or null if it was not read.</summary>
        public static string ReadingFor(LoadedMap map)
            => map?.PrefabName != null && _readings.TryGetValue(map.PrefabName, out string text) ? text : null;

        /// <summary>True if a prepared map has lakes the game should treat as water.</summary>
        public static bool HasLakes(LoadedMap map)
            => map?.PrefabName != null && _prepared.ContainsKey(map.PrefabName);

        /// <summary>
        /// Called when the game applies a map: its lakes become the lakes of play, or, for a map
        /// without any (every base map), there are none. The instance's place on the datum is
        /// read once here: the floating origin moves the map and the datum together, so it holds
        /// for as long as the map is loaded.
        ///
        /// Either way a custom map that lists lakes gets a line in the log as it is played, saying
        /// whether they are water. The first fix for these lakes was reported as not working after
        /// a test on a bundle that could not have had it, and the one line that said so was in the
        /// validation block, printed long before the flight; this one is printed as the map loads.
        /// </summary>
        public static void MapApplied(MapSettings instance)
        {
            Clear();
            if (instance == null || Plugin.Disabled) return;

            LoadedMap map = null;
            LakeSurfaces surfaces = null;
            foreach (LoadedMap candidate in BundleLoader.Maps)
            {
                MapSettings prefab = MapFixups.PreparedPrefabFor(candidate);
                if (prefab == null || !IsInstanceOf(instance, prefab)) continue;

                map = candidate;
                _prepared.TryGetValue(candidate.PrefabName, out surfaces);
                break;
            }

            // A base map: the sea is the only water.
            if (map == null) return;

            // A custom map with no lakes read: silent if it lists none either.
            if (surfaces == null)
            {
                int listed = map.Manifest?.Lakes?.Count ?? 0;
                if (listed > 0)
                    Plugin.LogWarning($"lake water: {map.Details.MapName} ({map.PrefabName}) lists {listed} raised lake(s), " +
                                      $"and none is water: aircraft can fly under them ({ReadingFor(map) ?? "not read"})");
                return;
            }

            // The prefab's coordinates are global ones only for an instance at the datum with no
            // rotation and no scale, which is how MapSettingsManager.LoadMap places every map.
            Transform root = instance.transform;
            if (Quaternion.Angle(root.rotation, Quaternion.identity) > 0.01f || (root.lossyScale - Vector3.one).sqrMagnitude > 1e-8f)
            {
                Plugin.LogWarning($"{map.Name()}: the map is rotated or scaled in the scene, so its lakes are not water " +
                                  $"(rotation {root.rotation.eulerAngles}, scale {root.lossyScale})");
                return;
            }

            Vector3 offset = root.position - Datum.originPosition;
            _frame = new LakeFrame(surfaces, offset.x, offset.y, offset.z);
            _frameOwner = instance.gameObject;
            instance.gameObject.AddComponent<LakeWaterAnchor>();

            string line = $"lake water: {map.Details.MapName} ({map.PrefabName}) has {surfaces.Count} raised lake(s); " +
                          $"the sea's water rules apply in them ({Patches.LakeWaterPatches.Summary})";
            if (Patches.LakeWaterPatches.AllApplied) Plugin.LogInfo(line);
            else Plugin.LogWarning($"{line}; what is not patched treats them as dry, as the warnings at start-up name");
        }

        /// <summary>
        /// Whether a map instance was made from this prefab. <c>MapLoader.LoadMap</c> instantiates the
        /// prefab, so Unity names the instance after it with "(Clone)" on the end. Matched exactly, not
        /// by its start: a custom map whose root were called "Terrain" would otherwise take the base
        /// game's "Terrain1(Clone)" for its own and flood Heartland with its lakes.
        /// </summary>
        static bool IsInstanceOf(MapSettings instance, MapSettings prefab)
            => string.Equals(instance.name, prefab.name, StringComparison.Ordinal) ||
               string.Equals(instance.name, prefab.name + "(Clone)", StringComparison.Ordinal);

        /// <summary>No lakes in play.</summary>
        public static void Clear()
        {
            _frame = null;
            _frameOwner = null;
        }

        /// <summary>Called by the anchor on a map instance being destroyed: its lakes go with it.</summary>
        internal static void InstanceDestroyed(GameObject instance)
        {
            if (ReferenceEquals(_frameOwner, instance)) Clear();
        }
    }

    /// <summary>Ends a map's lakes with the map: put on the instance whose lakes are in play.</summary>
    internal sealed class LakeWaterAnchor : MonoBehaviour
    {
        void OnDestroy() => LakeWater.InstanceDestroyed(gameObject);
    }
}
