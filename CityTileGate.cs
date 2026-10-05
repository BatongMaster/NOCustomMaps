using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

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
    /// The gate switches <c>Renderer.enabled</c>. It used to set <c>forceRenderingOff</c>,
    /// which keeps a renderer from drawing but leaves it registered: Unity still updated its
    /// bounds every time it moved, and a floating-origin shift moves every one of them, so the
    /// 60,000 buildings nobody could see cost as much on a shift as the ones in view. A
    /// disabled renderer drops out of that update. The buildings are only renderers now (their
    /// collision is <see cref="CityColliders"/>, one collider per tile), so nothing that hits a
    /// building notices; their bounds, which nothing reads, stay correct either way.
    ///
    /// Every building is created switched off and the gate is applied once in <see cref="Bind"/>,
    /// so no frame draws the whole country and the first pass does not turn sixty thousand
    /// renderers off. With no camera to draw for, on a dedicated server or anything else
    /// running without graphics (<see cref="Headless"/>), <c>CityBuilder</c> does not create
    /// the buildings at all, only their collision, and there is no gate.
    ///
    /// The gate keeps real time, not game time. The mission editor runs with the game's time
    /// scale at 0 from start to finish (<c>MissionEditor.Start</c> sets
    /// <c>TimeScaleManager.Scale = 0</c>, which writes <c>Time.timeScale</c>), and so does a
    /// paused game: on <c>Time.time</c>, which stops with it, the gate ran once at load and then
    /// never again, and the editor camera flew over bare ground everywhere but the towns in
    /// range of where it had stood while the map loaded.
    ///
    /// Distances are chosen by what a building actually subtends: a 10 m house at 8 km is
    /// about two pixels at 1080p, a 30 m block at 15 km about four.
    /// </summary>
    internal sealed class CityTileGate : MonoBehaviour
    {
        /// <summary>Tile edge, in metres. Buildings are bucketed by tile so the test is
        /// per tile rather than per building, and each tile's buildings share a collider.</summary>
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

        /// <summary>How often the gate re-evaluates, in real seconds. Fast enough that a jet
        /// at 400 m/s moves 100 m between checks, which the hysteresis band absorbs.</summary>
        const float Interval = 0.25f;

        sealed class Tile
        {
            public Vector3 Centre;
            public Renderer[] LowRise;
            public Renderer[] Commercial;

            /// <summary>Off to begin with, as the buildings are created.</summary>
            public bool LowRiseOn, CommercialOn;
        }

        /// <summary>A tile while the buildings are being placed.</summary>
        sealed class Filling
        {
            public int X, Z;
            public readonly List<Renderer> LowRise = new List<Renderer>(64);
            public readonly List<Renderer> Commercial = new List<Renderer>(16);
        }

        Dictionary<long, Filling> _filling = new Dictionary<long, Filling>();
        Tile[] _tiles = System.Array.Empty<Tile>();
        float _next;

        /// <summary>
        /// True with nothing to draw for: no graphics device, or batch mode, which is how a
        /// dedicated server runs. The game's own <c>GameManager.IsHeadless</c> is the first test
        /// and nothing else (<c>GameManager</c> sets it from <c>SystemInfo.graphicsDeviceType</c>
        /// alone), so it is asked of Unity directly, which answers before the game has set it.
        /// </summary>
        internal static bool Headless => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null
                                         || Application.isBatchMode;

        /// <summary>
        /// Files a building's renderer under its tile as it is placed, by its transform's position
        /// in the space of the object this gate is on. Bucketed here rather than found afterwards,
        /// which took a component lookup and a catalogue search by name for each of 65,000.
        /// </summary>
        public void Add(float x, float z, Renderer renderer, bool commercial)
        {
            int cx = CityCells.Index(x, TileSize), cz = CityCells.Index(z, TileSize);
            long key = CityCells.Key(cx, cz);

            if (!_filling.TryGetValue(key, out Filling tile))
                _filling[key] = tile = new Filling { X = cx, Z = cz };

            (commercial ? tile.Commercial : tile.LowRise).Add(renderer);
        }

        /// <summary>Files every renderer of a building made of several.</summary>
        public void Add(float x, float z, Renderer[] renderers, bool commercial)
        {
            foreach (Renderer renderer in renderers) Add(x, z, renderer, commercial);
        }

        /// <summary>
        /// Closes the tiles once every building is placed, and switches on the ones in range of
        /// the camera, if there is one yet. Without one the first <c>LateUpdate</c> that has a
        /// camera does it.
        /// </summary>
        public void Bind()
        {
            _tiles = new Tile[_filling.Count];
            int at = 0;

            foreach (Filling filling in _filling.Values)
                _tiles[at++] = new Tile
                {
                    Centre = new Vector3(CityCells.Centre(filling.X, TileSize), 0f, CityCells.Centre(filling.Z, TileSize)),
                    LowRise = filling.LowRise.ToArray(),
                    Commercial = filling.Commercial.ToArray(),
                };

            _filling = null;

            Evaluate();
            _next = Time.unscaledTime + Interval;

            Plugin.LogDebug($"city gate: {_tiles.Length} tile(s), " +
                            $"low-rise beyond {LowRiseRange:N0} m and commercial beyond {CommercialRange:N0} m " +
                            "are not drawn");
        }

        void LateUpdate()
        {
            // Unscaled: Time.time stands still in the mission editor and while paused.
            if (Time.unscaledTime < _next || _tiles.Length == 0) return;
            _next = Time.unscaledTime + Interval;

            Evaluate();
        }

        void Evaluate()
        {
            Camera camera = Camera.main;
            if (camera == null) return;

            // Tile centres are in this object's space and so is the camera once it is
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
                if (renderer != null) renderer.enabled = wanted;
        }
    }
}
