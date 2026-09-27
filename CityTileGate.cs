using System.Collections.Generic;
using UnityEngine;

namespace CustomMaps
{
    /// <summary>
    /// Stops drawing buildings that are too far away to see.
    ///
    /// Not an optimisation that could be left out. Every shipped building prefab has an
    /// <c>LOD 0</c> and nothing else — <c>turbine_tower</c> and <c>pylon1</c> carry
    /// LODGroups, housing does not — so without this, all ten thousand buildings are
    /// submitted from anywhere on a two-hundred-kilometre map at roughly two draws each.
    ///
    /// The gate works on <c>Renderer.forceRenderingOff</c> rather than by deactivating
    /// the object, because deactivating would take the colliders with it. A cruise missile
    /// launched from thirty kilometres away has to hit a city the camera has never
    /// approached.
    ///
    /// Distances are chosen by what a building actually subtends: a 10 m house at 8 km is
    /// about two pixels at 1080p, a 30 m block at 15 km about four.
    /// </summary>
    internal sealed class CityTileGate : MonoBehaviour
    {
        /// <summary>Tile edge, in metres. Buildings are bucketed by tile so the test is
        /// per tile rather than per building.</summary>
        public const float TileSize = 3120f;

        /// <summary>
        /// Beyond this a low-rise building is not worth drawing.
        ///
        /// This is the knob to turn if town centres cost too much: it lives in the plugin,
        /// so changing it needs no bundle rebuild. At 25,000 buildings and roughly two
        /// draws each, five kilometres holds the active set near the 8,291 draws Ignus
        /// ships and runs on.
        /// </summary>
        public const float LowRiseRange = 5000f;

        /// <summary>Commercial blocks are three times the height and stay legible much
        /// further out.</summary>
        public const float CommercialRange = 15000f;

        /// <summary>Added to the range so a tile switches on before its contents are
        /// needed, and switched off only past it. Without the band a camera sitting on a
        /// boundary toggles a whole tile every few frames.</summary>
        const float Hysteresis = 400f;

        /// <summary>How often the gate re-evaluates. Fast enough that a jet at 400 m/s
        /// moves 100 m between checks, which the hysteresis band absorbs.</summary>
        const float Interval = 0.25f;

        sealed class Tile
        {
            public Vector3 Centre;
            public Renderer[] LowRise;
            public Renderer[] Commercial;
            public bool LowRiseOn = true, CommercialOn = true;
        }

        readonly List<Tile> _tiles = new List<Tile>();
        float _next;

        /// <summary>Buckets the buildings already parented under <paramref name="root"/>.</summary>
        public void Bind(Transform root)
        {
            var lowRise = new Dictionary<int, List<Renderer>>();
            var commercial = new Dictionary<int, List<Renderer>>();
            var centres = new Dictionary<int, Vector3>();

            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);

                var renderer = child.GetComponentInChildren<MeshRenderer>(true);
                if (renderer == null) continue;

                Vector3 local = child.localPosition;
                int key = Key(local);

                if (!centres.ContainsKey(key))
                    centres[key] = new Vector3(Snap(local.x) + TileSize * 0.5f, local.y,
                                               Snap(local.z) + TileSize * 0.5f);

                // Tier is read off the catalogue by name, which is what the clone was
                // named when it was placed.
                int type = CityCatalogue.IndexOf(child.name);
                bool tall = type >= 0 && CityCatalogue.Entries[type].Tier == CityCatalogue.Commercial;

                Dictionary<int, List<Renderer>> into = tall ? commercial : lowRise;
                if (!into.TryGetValue(key, out List<Renderer> bucket)) into[key] = bucket = new List<Renderer>(64);
                bucket.Add(renderer);
            }

            foreach (KeyValuePair<int, Vector3> entry in centres)
            {
                lowRise.TryGetValue(entry.Key, out List<Renderer> low);
                commercial.TryGetValue(entry.Key, out List<Renderer> high);

                _tiles.Add(new Tile
                {
                    Centre = entry.Value,
                    LowRise = low != null ? low.ToArray() : System.Array.Empty<Renderer>(),
                    Commercial = high != null ? high.ToArray() : System.Array.Empty<Renderer>(),
                });
            }

            Plugin.LogDebug($"city gate: {_tiles.Count} tile(s), " +
                            $"low-rise beyond {LowRiseRange:N0} m and commercial beyond {CommercialRange:N0} m " +
                            "stop being drawn");
        }

        void LateUpdate()
        {
            if (Time.time < _next || _tiles.Count == 0) return;
            _next = Time.time + Interval;

            Camera camera = Camera.main;
            if (camera == null) return;

            // Tile centres are in the map root's space and so is the camera once it is
            // transformed in, which keeps the gate correct across a floating-origin shift.
            Vector3 eye = transform.InverseTransformPoint(camera.transform.position);

            foreach (Tile tile in _tiles)
            {
                float dx = eye.x - tile.Centre.x, dz = eye.z - tile.Centre.z;
                float distance = Mathf.Sqrt(dx * dx + dz * dz);

                Apply(tile.LowRise, ref tile.LowRiseOn, distance, LowRiseRange);
                Apply(tile.Commercial, ref tile.CommercialOn, distance, CommercialRange);
            }
        }

        static void Apply(Renderer[] renderers, ref bool on, float distance, float range)
        {
            if (renderers.Length == 0) return;

            // The band is asymmetric on purpose: switch on early, switch off late.
            bool wanted = on ? distance < range + Hysteresis : distance < range;
            if (wanted == on) return;

            on = wanted;
            foreach (Renderer renderer in renderers)
                if (renderer != null) renderer.forceRenderingOff = !wanted;
        }

        static int Key(Vector3 local) => Mathf.FloorToInt(local.x / TileSize) * 73856093
                                       ^ Mathf.FloorToInt(local.z / TileSize) * 19349663;

        static float Snap(float value) => Mathf.Floor(value / TileSize) * TileSize;
    }
}
