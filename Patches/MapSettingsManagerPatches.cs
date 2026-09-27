using HarmonyLib;

namespace CustomMaps.Patches
{
    /// <summary>
    /// The one hook that does registration and prefab preparation together.
    ///
    /// <c>EnableMap</c> is an async UniTask method, so a prefix on the stub runs
    /// before the state machine starts — and therefore before the
    /// <c>Maps.FirstOrDefault(x =&gt; x.Details.PrefabName == mapName)</c> lookup
    /// inside it. That is exactly the moment the fixups need: the GameWorld scene is
    /// loaded, so the shipped maps are available to borrow the terrain PhysicMaterial
    /// and faction music from, and nothing has been instantiated yet, so no
    /// <c>Awake</c> has run on our prefab.
    ///
    /// Note this deliberately does <em>not</em> patch
    /// <c>SceneSingleton&lt;MapSettingsManager&gt;.Awake</c>, which is what NOMapLoader
    /// hooks. On Mono, generic instantiations over reference types share a single
    /// native code body, so a patch there fires for every <c>SceneSingleton&lt;T&gt;</c>
    /// in the game — <c>LevelInfo</c>, <c>TerrainHeightMap</c>, <c>DetailRenderer</c>
    /// and the rest — and has to filter itself out by type check on every one.
    /// </summary>
    [HarmonyPatch(typeof(MapSettingsManager), nameof(MapSettingsManager.EnableMap))]
    internal static class EnableMapPatch
    {
        static void Prefix(MapSettingsManager __instance) => MapRegistrar.EnsureAll(__instance);
    }
}
