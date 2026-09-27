using HarmonyLib;
using UnityEngine;

namespace CustomMaps.Patches
{
    /// <summary>
    /// Regenerates <c>BattlefieldGrid</c> for the map that is actually loading.
    ///
    /// The bug, verified in <c>LevelInfo</c>: <c>Awake</c> builds the spatial hash from
    /// the <em>scene-serialized</em> fields of GameWorld.unity —
    ///
    ///     BattlefieldGrid.GenerateGrid(this.mapSize, this.hashGridSize);   // 81920, 4096
    ///
    /// and <c>ApplyMapSettings</c> then does
    ///
    ///     this.mapSize = mapSettings.MapSize.x;
    ///
    /// without regenerating. Both shipped maps are 81920 wide on X, so it never bites
    /// in vanilla play. On a wider map it bites hard, because
    /// <c>BattlefieldGrid.TryGetGridXY</c> clamps against the stale values:
    ///
    ///     gridCoordX = (int)Mathf.Clamp((coord.x + mapSize * 0.5f) / gridSize, 0, divisions - 1)
    ///
    /// so on a 143,360 m map everything beyond ±40,960 m collapses into the border
    /// ring, and <c>GetGridSquaresInRangeNonAlloc</c>'s
    /// <c>CeilToInt(range * divisions / mapSize)</c> computes the wrong search radius
    /// on top of that. Unit lookup, wreck lookup, target acquisition, refuel proximity
    /// and obstacle queries all degrade together, silently.
    ///
    /// A <b>prefix</b>, not a postfix: <c>ApplyMapSettings</c> ends with
    /// <c>LoadFromMission</c>, and regenerating after that would leave any unit that
    /// had already registered holding a reference to an orphaned <c>GridSquare</c>.
    /// At prefix time nothing is registered — <c>UnloadMap</c> has already cleared the
    /// registry.
    /// </summary>
    [HarmonyPatch(typeof(LevelInfo), nameof(LevelInfo.ApplyMapSettings))]
    internal static class ApplyMapSettingsPatch
    {
        /// <summary>The scene's pristine cell size, captured before we overwrite it.</summary>
        static float _referenceCell;

        static void Prefix(LevelInfo __instance, MapSettings mapSettings)
        {
            if (__instance == null || mapSettings == null) return;

            if (_referenceCell <= 0f) _referenceCell = __instance.hashGridSize;
            if (_referenceCell <= 0f) _referenceCell = GridMath.DefaultCell;

            float size = mapSettings.MapSize.x;
            if (!(size > 0f)) return;

            float cell = GridMath.ChooseHashGridSize(size, _referenceCell);

            // A no-op for every vanilla map, which is what makes this safe to run
            // unconditionally. It also quietly fixes the same bug for any future
            // shipped map whose MapSize.x is not 81920.
            if (Mathf.Approximately(size, __instance.mapSize) &&
                Mathf.Approximately(cell, __instance.hashGridSize))
                return;

            __instance.mapSize = size;
            __instance.hashGridSize = cell;
            BattlefieldGrid.GenerateGrid(size, cell);

            Plugin.LogInfo($"BattlefieldGrid regenerated for {mapSettings.name}: " +
                           $"mapSize={size:0} cell={cell:0.##} -> {GridMath.Divisions(size, cell)} divisions");
        }

        /// <summary>
        /// Gives the loaded map its detail renderers.
        ///
        /// A postfix, and on the instance rather than the prefab, because Unity refuses
        /// to parent a newly instantiated object to a persistent one — an AssetBundle
        /// prefab is persistent, so the clone would be silently created at the scene
        /// root, outlive the map, and render the donor's content over everything.
        /// <c>ApplyMapSettings</c> is the first hook that receives the live instance.
        /// </summary>
        static void Postfix(MapSettings mapSettings)
        {
            MapFixups.BorrowDetailRenderers(mapSettings);

            // After ApplyMapSettings, not before: it is what assigns the per-map ocean
            // textures to the shared water material, and the lakes' private copy is taken
            // from that material.
            MapFixups.AnchorLakeWater(mapSettings);

            // ApplyMapSettings is where the road network is cloned into LevelInfo and
            // RegenerateNetwork builds its junctions, so this is the first moment the
            // result can be checked.
            RoadNetworkFixup.VerifyOrMerge(mapSettings);

            // On the instance, never the prefab: Unity refuses to parent a newly
            // instantiated object to a persistent AssetBundle prefab, and the clones would
            // land at the scene root and outlive the map.
            CityBuilder.Populate(mapSettings);
        }
    }
}
