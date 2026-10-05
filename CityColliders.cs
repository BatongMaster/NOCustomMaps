using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.Rendering;

namespace CustomMaps
{
    /// <summary>
    /// What a map's buildings collide as: one static MeshCollider per <c>CityTileGate</c> tile,
    /// made of every building's box (<see cref="CityProxies"/>).
    ///
    /// Each building used to carry its donor's own MeshCollider, and on Swiss Alps that was
    /// 65,000 to 68,000 static colliders, fourteen times what the rest of the map has. That count
    /// is what every floating-origin shift paid for: <c>FloatingOrigin.OriginShift</c> moves the
    /// whole map, then calls <c>Physics.SyncTransforms</c>, and PhysX updates the pose of every
    /// shape it moved and rebuilds its query tree on the next step. A player's profile of one
    /// shift read 15.8 ms in SyncTransforms and 8.6 ms more in the next physics step, about once
    /// a kilometre. Merged into two to four hundred colliders, a bench replay of the same shift
    /// fell from 10.8 and 5.7 ms to 0.5 and 0.3 ms.
    ///
    /// The donor meshes cannot be merged as they are: the game ships them unreadable, so their
    /// triangles are out of reach at run time. The proxy is the box each donor's mesh bounds give,
    /// which changes what a building is to a bullet, a missile or an aircraft in two ways.
    /// Everything inside its outline is solid to the roof line, a courtyard or the open corner of
    /// an L-shaped block included, and a pitched roof is flat at its ridge; the outer walls of a
    /// plain block stand where they stood.
    ///
    /// By how much, measured against the real Ignus models over Swiss Alps' placements: the
    /// buildings cover 70 per cent of their boxes' plan and fill 56 per cent of their volume, and
    /// a box top stands a median 1.3 m over the real roof under it (90th percentile 4.5 m, at most
    /// 17 m). Only the plain terrace, a third of all the buildings, and one office block fill, or
    /// nearly fill, their box; the other 24 types cover 39 to 83 per cent of its plan. The game
    /// notices: an AI transport helicopter sets down on the flattest <c>Statics</c> hit it finds
    /// under 20 degrees, and a box top is perfectly level, so in a town it picks a roof; a ground
    /// unit's move order drops onto the first <c>Statics</c> hit from above
    /// (<c>PathfindingAgent.RaycastTerrain</c>), so one given in a courtyard aims at the roof.
    ///
    /// Everything the game reads off a building's hit is the same: layer <c>Statics</c>, no
    /// PhysicMaterial (so it is "not terrain"), no Rigidbody and nothing damageable, as the clones
    /// had once <c>MapBuilding</c> was stripped.
    ///
    /// Colliders stay on everywhere. A host simulates hits far from its own camera, a dedicated
    /// server has none, and a cruise missile must hit a town nobody has flown over.
    /// </summary>
    internal static class CityColliders
    {
        /// <summary>
        /// Builds the colliders for <paramref name="proxies"/> under <paramref name="root"/>, the
        /// map's <c>Cities</c> object, and returns a line for the log.
        ///
        /// The meshes are cooked with <c>Physics.BakeMesh</c> across the job workers before any
        /// collider takes one, and a collider given a mesh already cooked with its own options
        /// uses that result instead of cooking it again. Cooking 68,000 boxes on the main thread
        /// measured 105 to 115 ms; in parallel, 17 to 22 ms.
        /// </summary>
        public static string Build(Transform root, CityProxyBuilder proxies)
        {
            IReadOnlyList<CityProxyBuilder.Cell> cells = proxies.Cells;
            if (cells.Count == 0) return "none (no building placed has a donor with a collider)";

            var clock = Stopwatch.StartNew();

            // Meshes made at run time outlive the objects that use them, so they go, as they are
            // made, to a component that is destroyed with the map and takes them along. It comes
            // first so that a failure part of the way through leaves nothing behind either.
            var meshes = new Mesh[cells.Count];
            root.gameObject.AddComponent<CityCollisionMeshes>().Own(meshes);

            // One pair of arrays, the largest cell's size, that every cell's mesh is written to.
            var vertices = new float[proxies.MostBoxesInACell * CityProxies.CornersPerBox * 3];
            var indices = new int[proxies.MostBoxesInACell * CityProxies.IndicesPerBox];
            int triangles = 0;

            for (int i = 0; i < cells.Count; i++)
            {
                meshes[i] = MeshFor(cells[i], vertices, indices);
                triangles += cells[i].Boxes * CityProxies.TrianglesPerBox;
            }

            double built = clock.Elapsed.TotalMilliseconds;
            bool baked = Bake(meshes);
            double cooked = clock.Elapsed.TotalMilliseconds;

            for (int i = 0; i < cells.Count; i++)
            {
                CityProxyBuilder.Cell cell = cells[i];

                // Layer and parent first, then the collider: a shape is created in its final
                // place rather than created and then moved.
                var holder = new GameObject($"collision {cell.X},{cell.Z}") { layer = PhysicsLayers.Statics };
                holder.transform.SetParent(root, worldPositionStays: false);
                holder.transform.localPosition = new Vector3(cell.OriginX, 0f, cell.OriginZ);

                // Static, solid, no PhysicMaterial: what the donors' own colliders were.
                holder.AddComponent<MeshCollider>().sharedMesh = meshes[i];
            }

            double total = clock.Elapsed.TotalMilliseconds;

            // A bake that fails on a worker is not seen here (Unity reports it in its own log); that
            // collider cooks its mesh when it is assigned, which shows as a longer assign time.
            string cooking = baked
                ? $"baked in parallel {cooked - built:0} ms, assigned {total - cooked:0} ms"
                : $"cooked on the main thread as assigned {total - cooked:0} ms";

            return $"{cells.Count} collider(s) of {proxies.Boxes:N0} box(es), {triangles:N0} triangles; " +
                   $"meshes built {built:0} ms, {cooking}";
        }

        /// <summary>One cell's mesh, written through the shared <paramref name="vertices"/> and
        /// <paramref name="indices"/>, which the mesh copies.</summary>
        static Mesh MeshFor(CityProxyBuilder.Cell cell, float[] vertices, int[] indices)
        {
            cell.Write(vertices, indices);

            var mesh = new Mesh
            {
                name = $"city collision {cell.X},{cell.Z}",
                indexFormat = cell.Needs32BitIndices ? IndexFormat.UInt32 : IndexFormat.UInt16,
            };

            // Positions straight from the interleaved floats: no Vector3 array to fill and drop.
            mesh.SetVertexBufferParams(cell.VertexCount,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3));
            mesh.SetVertexBufferData(vertices, 0, 0, cell.VertexCount * 3);
            mesh.SetIndices(indices, 0, cell.IndexCount, MeshTopology.Triangles, 0, calculateBounds: false);

            // Set from the vertices explicitly: with the vertex data written this way, the bounds
            // SetIndices works out stay empty (measured). The collider does not use them, but
            // anything that reads the mesh would be misled.
            mesh.RecalculateBounds();

            return mesh;
        }

        /// <summary>
        /// Cooks every mesh in parallel. False if that could not be done, in which case each
        /// collider cooks its own mesh on the main thread when it is assigned, as it always could.
        /// </summary>
        static bool Bake(Mesh[] meshes)
        {
            var ids = new NativeArray<int>(meshes.Length, Allocator.TempJob);
            try
            {
                for (int i = 0; i < meshes.Length; i++) ids[i] = meshes[i].GetInstanceID();

                // One mesh per work item: the cells range from a handful of houses to 1,300.
                new BakeJob { Meshes = ids }.Schedule(ids.Length, 1).Complete();
                return true;
            }
            catch (Exception e)
            {
                Plugin.LogDebug($"city collision: parallel cooking failed ({e.Message}); cooking on the main thread");
                return false;
            }
            finally
            {
                ids.Dispose();
            }
        }

        /// <summary>
        /// Cooks one mesh as a solid, non-convex collider with Unity's default cooking options,
        /// which are the ones a new MeshCollider has, so the collider takes the result as it is.
        /// </summary>
        struct BakeJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<int> Meshes;

            public void Execute(int index) => Physics.BakeMesh(Meshes[index], false);
        }
    }

    /// <summary>
    /// Holds the city's collision meshes and destroys them with the map.
    ///
    /// A mesh made at run time is an asset of its own, and destroying the MeshCollider or the
    /// GameObject that uses it leaves it in memory; without this every load of the map would add
    /// another ten megabytes of mesh, and the physics data cooked from it, that nothing was sure
    /// to free. <c>OnDestroy</c> is the unload hook that covers every way the map goes:
    /// <c>MapSettingsManager.UnloadMap</c> destroys the map's GameObject when another map loads,
    /// and leaving the mission unloads the whole GameWorld scene. The component sits on the
    /// <c>Cities</c> object, which is created under the map once it is active, so Unity calls it
    /// either way.
    /// </summary>
    internal sealed class CityCollisionMeshes : MonoBehaviour
    {
        Mesh[] _meshes;

        internal void Own(Mesh[] meshes) => _meshes = meshes;

        void OnDestroy()
        {
            if (_meshes == null) return;

            foreach (Mesh mesh in _meshes)
                if (mesh != null) Destroy(mesh);

            _meshes = null;
        }
    }
}
