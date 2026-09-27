using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine.Rendering;
using NuclearOption.SavedMission;
using UnityEngine;

namespace CustomMaps
{
    /// <summary>
    /// The one place a map's readiness is reported.
    ///
    /// Everything checked here is something that fails <em>silently</em> otherwise —
    /// a map with no colliders loads fine and drops every aircraft through the world,
    /// a map whose colliders lack the terrain PhysicMaterial loads fine and has no
    /// grass. Printing one block per map turns "why is there no grass" into a line of
    /// log.
    /// </summary>
    internal static class MapDiagnostics
    {
        /// <summary>Validates a prepared prefab and logs the block. False means the map
        /// should not be offered.</summary>
        public static bool Validate(LoadedMap map, MapSettings settings, int reboundColliders)
        {
            var report = new StringBuilder();
            bool ok = true;

            report.AppendLine($"validate {map.PrefabName}");
            Line(report, "file", $"{System.IO.Path.GetFileName(map.File)} (sha256 {map.ContentHash?.Substring(0, 12)}…)");
            Line(report, "name", $"\"{map.Details.MapName}\"");
            Line(report, "built against", $"game {map.Manifest.BuiltAgainstGameHash ?? "<unrecorded>"}");

            // --- geometry ---
            float sizeX = settings.MapSize.x, sizeY = settings.MapSize.y;
            if (sizeX <= 0f || sizeY <= 0f)
            {
                ok = false;
                Line(report, "size", $"{sizeX} x {sizeY}  FAIL — MapSize is zero. If the bundle was built " +
                                     "against a different game version, a serialized MapSettings would " +
                                     "deserialize exactly like this.");
            }
            else
            {
                Line(report, "size", $"{sizeX:0} x {sizeY:0} m ({sizeX * sizeY / 1_000_000f:0} km2)");
            }

            float cell = GridMath.ChooseHashGridSize(sizeX);
            int divisions = GridMath.Divisions(sizeX, cell);
            Line(report, "grid", $"cell {cell:0.##} m -> {divisions}x{divisions}" +
                                 (GridMath.DividesEvenly(sizeX, cell) ? ", exact" : ", INEXACT (edge strip will clamp)"));

            // Deliberately not checked against FloatingOrigin's ±90,000 m kill bound.
            // That bound exists in the source and looks like a hard 180 km ceiling, but
            // RBKillBounds() is never called — Awake is the only Unity message on the
            // class and nothing invokes it by name — so nothing enforces it. What does
            // bind is memory, reported below.
            float half = GridMath.HalfExtent(sizeX);
            Line(report, "half-extent", $"{half:0} m " +
                                        $"(world coordinate resolution here is {Resolution(half) * 100f:0.#} cm)");

            float colliderMb = GridMath.EstimateColliderMegabytes(sizeX);
            if (colliderMb > 700f)
                Line(report, "collider cost", $"~{colliderMb:N0} MB estimated — very large; this is allocated on " +
                                              "the dedicated server as well as every client");
            else
                Line(report, "collider cost", $"~{colliderMb:N0} MB estimated");

            // --- colliders ---
            MeshCollider[] colliders = settings.GetComponentsInChildren<MeshCollider>(true);
            if (colliders.Length == 0)
            {
                ok = false;
                Line(report, "colliders", "0  FAIL — nothing to raycast against; aircraft will fall through the world " +
                                          "and the dedicated server cannot simulate weapons or AI");
            }
            else
            {
                Line(report, "colliders", $"{colliders.Length} MeshCollider(s), layer histogram {LayerHistogram(colliders)}");

                int withMaterial = 0, paved = 0;
                PhysicMaterial terrain = MapFixups.TerrainPhysicMaterial;

                foreach (MeshCollider c in colliders)
                {
                    if (terrain != null && c.sharedMaterial == terrain) withMaterial++;
                    else if (IsRoadRibbon(c)) paved++;
                }

                // Road ribbons are the one thing that must NOT carry the terrain material:
                // the vehicle job reads on-road speed straight off that comparison, so a
                // road wearing the terrain material is a field. Counting them separately
                // keeps the real warning — terrain that will not grow grass — meaningful.
                int shouldBeTerrain = colliders.Length - paved;

                Line(report, "PhysicMaterial",
                    terrain == null
                        ? "no donor material resolved  WARN — no grass, no trees"
                        : $"{withMaterial}/{shouldBeTerrain} == '{terrain.name}' ({reboundColliders} rebound)" +
                          (paved > 0 ? $", {paved} paved collider(s) deliberately unmaterialised" : "") +
                          (withMaterial == shouldBeTerrain ? "  OK" : "  WARN — the rest will not grow grass"));
            }

            // --- networking ---
            NuclearOption.SceneLoading.NetworkMap network = settings.NetworkMap;
            if (network == null)
            {
                ok = false;
                Line(report, "NetworkMap", "missing  FAIL — MapSettingsManager.LoadMap dereferences it unconditionally");
            }
            else
            {
                int objects = network.NetworkObjects?.Length ?? 0;
                bool prefixOk = MapIdentity.IsValidPrefix(network.MapPrefix);
                if (!prefixOk) ok = false;
                Line(report, "NetworkMap", $"prefix {network.MapPrefix}" + (prefixOk ? "" : " FAIL — outside [16,111]") +
                                           $", objects {objects}" +
                                           (objects == 0 ? "  OK (no prefab-hash registration)" : "  WARN — server and client must agree on the order"));
            }

            // --- presentation ---
            Line(report, "MapImage", settings.MapImage != null
                ? $"{settings.MapImage.texture.width}x{settings.MapImage.texture.height} sprite"
                : "absent  WARN — the tactical map will render blank");

            if (settings.TerrainColorMap == null)
            {
                Line(report, "TerrainColorMap", "absent  WARN — ground-unit and effect tinting falls back to a default");
            }
            else
            {
                // GetTerrainColorAtCoordinate calls GetPixel, which throws on a texture
                // that is not Read/Write enabled.
                bool readable = settings.TerrainColorMap.isReadable;
                if (!readable) ok = false;
                Line(report, "TerrainColorMap", $"{settings.TerrainColorMap.width}x{settings.TerrainColorMap.height}" +
                                                (readable ? ", readable  OK" : "  FAIL — not Read/Write enabled; GetPixel will throw"));
            }

            Line(report, "ocean", $"basecolor {Describe(settings.OceanBasecolor)}, depth {Describe(settings.OceanDepthmap)}");
            Line(report, "lake water", DescribeLakeVolumes(settings));
            Line(report, "music", $"{settings.factionMusic?.Length ?? 0} faction entries");

            if (!MapIdentity.IsValidMapName(map.Details.MapName, out string nameReason))
                Line(report, "MapName", $"\"{map.Details.MapName}\"  WARN — {nameReason}");

            Line(report, "roads", DescribeNetwork(settings.RoadNetwork,
                "ground AI will drive cross-country"));

            Line(report, "sea lanes", DescribeNetwork(settings.SeaLanes,
                "naval AI cannot path whatever water sits at the datum"));

            Line(report, "ribbon mesh", DescribeRibbons(settings));
            Line(report, "airbases", DescribeAirbases(settings));
            Line(report, "airfields", DescribeAirfieldPaving(settings));

            report.Append("  => ").Append(ok ? "OK" : "REJECTED");

            if (ok) Plugin.LogInfo(report.ToString());
            else Plugin.LogError(report.ToString());

            return ok;
        }

        /// <summary>Single-precision resolution at a given world coordinate, in metres.
        /// GlobalPosition is absolute float, so this is the finest distance the game can
        /// represent out at the map's edge.</summary>
        static float Resolution(float coordinate)
        {
            if (coordinate <= 0f) return 0f;

            // A float carries 24 significant bits, so the step between representable
            // values near a magnitude is about that magnitude times 2^-23. Approximated
            // rather than taken from MathF.BitIncrement, which netstandard2.1 does not
            // expose — and this is a log line, not a computation anything depends on.
            return coordinate * 1.1920929e-7f;
        }

        /// <summary>True for a collider under the map's <c>Roads</c> root, which is where
        /// the ribbons that give ground vehicles their on-road speed live.</summary>
        static bool IsRoadRibbon(Component collider)
        {
            for (Transform at = collider.transform; at != null; at = at.parent)
            {
                // The airfield paving keeps a null PhysicMaterial for exactly the same reason
                // the ribbons do, so it is the same kind of deliberate rather than a second
                // kind that needs its own counter.
                if (at.name == MapFixups.RoadRoot) return true;
                if (at.name == MapFixups.AirfieldRoot) return true;
            }

            return false;
        }

        static string Describe(Texture2D t) => t != null ? $"{t.width}x{t.height}" : "absent";

        /// <summary>
        /// Whether the raised lakes hold water, or are sheets an aircraft can fly under.
        ///
        /// Their water is trigger boxes carrying the game's water PhysicMaterial
        /// (<c>MapFixups.BindLakeVolumes</c>). Nothing else shows whether that worked: a volume
        /// left off, or a bundle built before there were any, looks exactly like a lake that holds
        /// water until something flies under it.
        /// </summary>
        static string DescribeLakeVolumes(MapSettings settings)
        {
            Transform water = settings.transform.Find(MapFixups.WaterRoot);
            if (water == null) return "none — this map has no lakes above the sea";

            BoxCollider[] volumes = water.GetComponentsInChildren<BoxCollider>(true);
            if (volumes.Length == 0)
                return $"{water.childCount} lake(s) with no water volumes  WARN — a bundle built before lakes held " +
                       "water: aircraft fly under them. Rebuild the map.";

            PhysicMaterial material = MapFixups.LakeWaterMaterial;
            int on = 0, wet = 0;
            foreach (BoxCollider volume in volumes)
            {
                if (volume.enabled) on++;
                if (material != null && volume.sharedMaterial == material && volume.isTrigger) wet++;
            }

            string counts = $"{volumes.Length} volume(s) under {water.childCount} lake(s), {on} on";
            if (on == volumes.Length && wet == volumes.Length) return $"{counts}, '{material.name}'  OK";
            if (on == 0) return $"{counts}  WARN — no water PhysicMaterial, so they stay off and aircraft fly under the lakes";
            return $"{counts}, {wet} carrying the water material  WARN — a volume on without it is a floor, not water";
        }

        /// <summary>
        /// Summarises a pathing network, with the number that actually constrains it.
        ///
        /// Junctions, not roads: <c>RoadPathfinder.TryPathfind</c> re-sorts its whole
        /// unvisited node list on every iteration, so its cost grows as the square of the
        /// junction count. Heartland ships 506, which is the figure to stay near.
        /// </summary>
        static string DescribeNetwork(RoadPathfinding.RoadNetworkSO so, string ifMissing)
        {
            RoadPathfinding.RoadNetwork network = so?.RoadNetwork;

            if (network == null || network.roads == null)
                return ifMissing != null ? $"absent  WARN — {ifMissing}" : "absent";

            // Empty is not the same as absent, and the difference is the whole reason the
            // map ships one. MapSettings.CreateSeaLanes answers a null scriptable object
            // with a network whose AllowMerge is false, so LoadFromMission then discards
            // any lanes the mission defined and logs an error every load. An empty network
            // takes the branch that permits merging.
            if (network.roads.Count == 0)
                return "empty — carries nothing, but lets a mission merge its own in";

            int points = 0;
            float metres = 0f;
            foreach (RoadPathfinding.Road road in network.roads)
            {
                points += road.points.Count;
                metres += road.length;
            }

            string zeroLength = metres > 0f ? "" : "  WARN — every road has length 0, so all pathfinding weights are 0";

            return $"{network.roads.Count} road(s), {points:N0} point(s), {metres / 1000f:N0} km" +
                   $" (Heartland: 674 / 9,619 / 716 km){zeroLength}";
        }

        /// <summary>Line break inside a report value, so a block can carry detail lines
        /// under its own heading.</summary>
        const string NewLine = "\n";

        static void Line(StringBuilder sb, string label, string value)
            => sb.AppendLine("  " + label.PadRight(16) + value);

        /// <summary>
        /// The airbases the map built, and whether they can actually be used.
        ///
        /// Three things have to be true and none of them is visible from a screenshot: each
        /// airbase has to have runways, each has to carry a unique name because the game
        /// registers them in a dictionary keyed by it, and every networked object under them
        /// has to be in the NetworkMap table or the clients never see it.
        /// </summary>
        static string DescribeAirbases(MapSettings settings)
        {
            Transform root = settings.transform.Find(AirbaseBuilder.AirbaseRoot);
            if (root == null) return "none — this map ships no airbases";

            Airbase[] airbases = root.GetComponentsInChildren<Airbase>(true);
            if (airbases.Length == 0) return $"'{AirbaseBuilder.AirbaseRoot}' exists but holds no Airbase";

            var names = new HashSet<string>(StringComparer.Ordinal);
            var line = new StringBuilder();

            int runwayless = 0, unnamed = 0, duplicated = 0;
            var factions = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (Airbase airbase in airbases)
            {
                if ((airbase.runways?.Length ?? 0) == 0) runwayless++;

                SavedAirbase saved = AirbaseBuilder.Settings(airbase);
                string unique = saved?.UniqueName;
                if (string.IsNullOrEmpty(unique)) unnamed++;
                else if (!names.Add(unique)) duplicated++;

                string faction = saved?.faction ?? "";
                factions.TryGetValue(faction, out int count);
                factions[faction] = count + 1;
            }

            line.Append($"{airbases.Length}");

            var sides = new List<string>();
            foreach (KeyValuePair<string, int> faction in factions)
                sides.Add($"{faction.Value} {(faction.Key.Length > 0 ? faction.Key : "<no faction>")}");

            sides.Sort(StringComparer.Ordinal);
            line.Append(" (").Append(string.Join(", ", sides)).Append(')');

            int networked = 0;
            NuclearOption.SceneLoading.NetworkMap map = settings.NetworkMap;
            if (map?.NetworkObjects != null) networked = map.NetworkObjects.Length;

            line.Append($", {networked} network object(s) registered");

            if (runwayless > 0 || unnamed > 0 || duplicated > 0)
                line.Append($"  WARN — {runwayless} with no runway, {unnamed} unnamed, " +
                            $"{duplicated} sharing a name with another; a duplicate name replaces " +
                            "the airbase already registered under it");
            else
                line.Append("  OK");

            foreach (Airbase airbase in airbases)
            {
                SavedAirbase saved = AirbaseBuilder.Settings(airbase);

                // The runway, not the pivot. An airbase's transform sits wherever the donor
                // happened to be authored — hundreds of metres from its own tarmac and, on the
                // last build, three hundred metres underground — so printing it made every
                // airbase look misplaced and told nobody where the runway was.
                Airbase.Runway runway = Runway(airbase);

                Vector3 at = runway != null
                    ? (runway.Start.position + runway.End.position) * 0.5f
                    : airbase.transform.position;

                float length = runway != null
                    ? Vector3.Distance(runway.Start.position, runway.End.position)
                    : 0f;

                line.Append(NewLine).Append("                  ")
                    .Append($"{saved?.UniqueName,-6} {saved?.faction,-8} ")
                    .Append($"({at.x,8:N0}, {at.z,8:N0}) at {at.y,5:N0} m, ")
                    .Append($"{airbase.runways?.Length ?? 0} runway(s), {length,5:N0} m")
                    .Append(ExtraRunwayLengths(airbase, runway))
                    .Append($", capture {saved?.CaptureRange ?? 0f:N0} m");
            }

            return line.ToString();
        }

        /// <summary>The lengths of the runways after the main one, as " + 1,200 + 900 m", or empty
        /// for a field with one.</summary>
        static string ExtraRunwayLengths(Airbase airbase, Airbase.Runway main)
        {
            var lengths = new List<string>();
            foreach (Airbase.Runway other in airbase.runways ?? Array.Empty<Airbase.Runway>())
            {
                if (other == null || other == main) continue;

                lengths.Add(other.Start != null && other.End != null
                    ? Vector3.Distance(other.Start.position, other.End.position).ToString("N0")
                    : "?");
            }

            return lengths.Count == 0 ? "" : " + " + string.Join(" + ", lengths) + " m";
        }

        static Airbase.Runway Runway(Airbase airbase)
        {
            foreach (Airbase.Runway candidate in airbase.runways ?? Array.Empty<Airbase.Runway>())
                if (candidate?.Start != null && candidate.End != null) return candidate;

            return null;
        }

        /// <summary>
        /// The airfield paving that ships in the bundle.
        ///
        /// Worth its own line because it is the one part of an airfield that can go missing
        /// without anything else noticing: the airbases still register, the platform is still
        /// levelled, and the map loads perfectly well with six rectangles of mown grass where
        /// the runways should be.
        /// </summary>
        static string DescribeAirfieldPaving(MapSettings settings)
        {
            Transform root = settings.transform.Find(MapFixups.AirfieldRoot);
            if (root == null) return "none — this map ships no airfield paving  WARN";

            int fields = 0, surfaces = 0, vertices = 0;

            foreach (Transform child in root)
            {
                fields++;

                foreach (MeshFilter filter in child.GetComponentsInChildren<MeshFilter>(true))
                {
                    if (filter.sharedMesh == null) continue;

                    surfaces++;
                    vertices += filter.sharedMesh.vertexCount;
                }
            }

            return $"{fields} field(s), {surfaces} surface(s), {vertices:N0} vertices" +
                   (surfaces == 0 ? "  WARN — nothing paved" : "  OK");
        }

        /// <summary>
        /// Reports the road ribbons' vertex layout as it survived the trip through the
        /// AssetBundle.
        ///
        /// The ribbons are authored with position, normal and uv0, they carry a correct
        /// base-game asphalt material by the time anyone sees them, and they still drew as
        /// one flat dark grey for four builds. That is the signature of a mesh that arrived
        /// with no uv0 and no normals: every fragment samples texel (0,0) of the asphalt,
        /// and there is no shading term left to vary it.
        ///
        /// The cause is Player Settings > Optimize Mesh Data, which strips any vertex
        /// channel the material assigned at <em>build</em> time does not read — and at build
        /// time every ribbon is still wearing an <c>Unlit/Color</c> placeholder whose vertex
        /// input is POSITION and nothing else. Borrowing a material at load and letting a
        /// build-time optimisation reason from the placeholder cannot both be true, so this
        /// checks which one won.
        ///
        /// Deliberately asks <c>HasVertexAttribute</c>, which reads the vertex layout
        /// descriptor rather than the vertex data. <c>mesh.uv</c> on a mesh whose CPU copy
        /// was discarded returns an empty array, which is indistinguishable from a channel
        /// that was stripped — and telling those two apart is the entire point.
        /// </summary>
        static string DescribeRibbons(MapSettings settings)
        {
            Transform roads = settings.transform.Find(MapFixups.RoadRoot);
            if (roads == null) return $"no '{MapFixups.RoadRoot}' child — this map ships no ribbons";

            int meshes = 0, vertices = 0, uv0 = 0, normals = 0, tangents = 0, readable = 0;
            Mesh sample = null;

            foreach (MeshFilter filter in roads.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh mesh = filter.sharedMesh;
                if (mesh == null) continue;

                meshes++;
                vertices += mesh.vertexCount;

                if (mesh.HasVertexAttribute(VertexAttribute.TexCoord0)) uv0++;
                if (mesh.HasVertexAttribute(VertexAttribute.Normal)) normals++;
                if (mesh.HasVertexAttribute(VertexAttribute.Tangent)) tangents++;
                if (mesh.isReadable) readable++;

                if (sample == null || mesh.vertexCount > sample.vertexCount) sample = mesh;
            }

            if (meshes == 0) return $"'{MapFixups.RoadRoot}' has no MeshFilter";

            var line = new StringBuilder();
            line.Append($"{meshes} mesh(es), {vertices:N0} vertices; ")
                .Append($"uv0 {uv0}/{meshes}, normals {normals}/{meshes}, ")
                .Append($"tangents {tangents}/{meshes}, readable {readable}/{meshes}");

            if (uv0 < meshes || normals < meshes)
                line.Append("  FAIL — a ribbon with no uv0 samples a single texel and draws as "
                            + "flat colour; with no normals it gets no shading term either. This is "
                            + "Optimize Mesh Data stripping channels the build-time placeholder "
                            + "does not read. Clear StripUnusedMeshComponents and rebuild.");
            else
                line.Append("  OK");

            if (sample != null) line.Append(NewLine).Append(DescribeMesh(sample));
            return line.ToString();
        }

        /// <summary>
        /// One ribbon mesh in full.
        ///
        /// The uv0 range is the second number that matters. V runs in real metres over the
        /// texture's period, so a road a kilometre long reaches V in the tens — and a
        /// surface that tiles correctly and one that samples a single row are impossible to
        /// tell apart from a screenshot.
        /// </summary>
        static string DescribeMesh(Mesh mesh)
        {
            const string Indent = "                    ";

            var sb = new StringBuilder();
            sb.Append("                  sample '").Append(mesh.name).Append("': ")
              .Append(mesh.vertexCount).Append(" verts, ")
              .Append(mesh.GetIndexCount(0) / 3).Append(" tris, ")
              .Append(mesh.indexFormat).Append(", readable ").Append(mesh.isReadable);

            foreach (VertexAttributeDescriptor a in mesh.GetVertexAttributes())
                sb.Append(NewLine).Append(Indent)
                  .Append(a.attribute).Append(" dim ").Append(a.dimension)
                  .Append(' ').Append(a.format).Append(" stream ").Append(a.stream);

            if (!mesh.isReadable)
            {
                sb.Append(NewLine).Append(Indent)
                  .Append("not readable — uv range unavailable. m_IsReadable is serialised on the "
                          + "mesh and the bundle build writes it through, so this would mean the "
                          + "authoring side called UploadMeshData.");
                return sb.ToString();
            }

            Vector2[] uv = mesh.uv;
            sb.Append(NewLine).Append(Indent)
              .Append("uv ").Append(uv.Length)
              .Append(", normals ").Append(mesh.normals.Length)
              .Append(", tangents ").Append(mesh.tangents.Length);

            if (uv.Length > 0)
            {
                Vector2 min = uv[0], max = uv[0];
                foreach (Vector2 t in uv) { min = Vector2.Min(min, t); max = Vector2.Max(max, t); }

                sb.Append(NewLine).Append(Indent)
                  .Append("uv range ").Append(min.ToString("F2")).Append(" .. ").Append(max.ToString("F2"))
                  .Append("   bounds ").Append(mesh.bounds.size.ToString("F0")).Append(" m");
            }

            return sb.ToString();
        }

        static string LayerHistogram(MeshCollider[] colliders)
        {
            var counts = new Dictionary<int, int>();
            foreach (MeshCollider c in colliders)
            {
                int layer = c.gameObject.layer;
                counts.TryGetValue(layer, out int n);
                counts[layer] = n + 1;
            }

            var sb = new StringBuilder("{ ");
            foreach (KeyValuePair<int, int> pair in counts)
            {
                string name = LayerMask.LayerToName(pair.Key);
                sb.Append(pair.Key.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(name)) sb.Append(' ').Append(name);
                sb.Append(": ").Append(pair.Value).Append(' ');
            }
            return sb.Append('}').ToString();
        }
    }
}
