using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using NuclearOption.MissionEditorScripts;
using NuclearOption.Networking.Lobbies;
using NuclearOption.SavedMission;
using NuclearOption.SceneLoading;

namespace CustomMaps.Patches
{
    /// <summary>
    /// Starts reading a custom map's prefab (<c>LoadedMap.BeginWarmUp</c>) as soon as the game asks
    /// about its key: when a lobby is made for it (<c>TryGetMapName</c>), and as its load begins
    /// (<c>Load</c>, then <c>CanLoad</c>), which on a client joining a server is the server's
    /// <c>LoadMapMessage</c>.
    ///
    /// When the load changes scene (from the menus, and on a joining client), the prefab is read
    /// behind the loading screen while the main thread keeps running: Unity reads it before the
    /// GameWorld scene, one asynchronous load after the other, so the loading bar stands still
    /// meanwhile, and <c>EnableMap</c> finds it read. Inside the mission editor the scene is already
    /// there and <c>EnableMap</c> follows <c>Load</c> in the same frame, so <see cref="EditorLoadWaitPatch"/>
    /// holds that load back until the prefab is read. Every installed map used to be read at game
    /// start instead, so none of this was needed; see <c>BeginWarmUp</c> for why that stopped.
    ///
    /// Apart from <see cref="MapLoaderPatches"/>, which the join path depends on: without this the
    /// map still loads, in one piece, when it is enabled.
    /// </summary>
    [HarmonyPatch(typeof(MapLoader))]
    internal static class MapWarmUpPatch
    {
        // __0, the key, by position: the game names it mapKey in Load and key in the others
        // (see JoinFailurePatch).

        [HarmonyPrefix]
        [HarmonyPatch(nameof(MapLoader.Load))]
        static void Load_Prefix(MapKey __0) => WarmUp(__0, "its load began");

        [HarmonyPrefix]
        [HarmonyPatch(nameof(MapLoader.CanLoad))]
        static void CanLoad_Prefix(MapKey __0) => WarmUp(__0, "the game asked whether it can load it");

        [HarmonyPrefix]
        [HarmonyPatch(nameof(MapLoader.TryGetMapName))]
        static void TryGetMapName_Prefix(MapKey __0) => WarmUp(__0, "a lobby or server named it");

        static void WarmUp(MapKey key, string reason)
        {
            if (key.Type != MapKey.KeyType.GameWorldPrefab) return;

            try
            {
                BundleLoader.WarmUpFor(key.Path, reason);
            }
            catch (Exception e)
            {
                Plugin.LogDebug($"could not begin the warm-up for {key.Path}: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Starts reading a custom map's prefab when a mission on it is picked in the missions list
    /// (<c>MissionsPicker</c>): for single player, for a lobby to host, and in the older mission
    /// editor Load menu that is built on that list.
    ///
    /// Reading the briefing and pressing Start is time the load would otherwise spend behind the
    /// loading screen; on Swiss Alps the prefab takes about 6 s. Picking a mission and then one on
    /// another map leaves the first map read for nothing, all session, as every installed map was at
    /// game start before.
    /// </summary>
    [HarmonyPatch(typeof(MissionsPicker), nameof(MissionsPicker.SelectMission))]
    internal static class MissionPickWarmUpPatch
    {
        static void Postfix(MissionsPicker __instance)
        {
            try
            {
                Mission mission = __instance != null ? __instance.Mission : null;
                if (mission == null || mission.MapKey.Type != MapKey.KeyType.GameWorldPrefab) return;

                BundleLoader.WarmUpFor(mission.MapKey.Path, "a mission on it was picked");
            }
            catch (Exception e)
            {
                Plugin.LogDebug($"could not begin the warm-up for the picked mission: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Starts reading a custom map's prefab when a mission on it is picked in the mission editor's
    /// own Load menu (File &gt; Load, <c>MissionEditorLoadMenuV2</c>), which is a list of its own and
    /// not a <c>MissionsPicker</c>.
    ///
    /// A click picks the mission and shows its preview, a second click or the Load button loads it,
    /// so this buys the time spent looking at the preview: often less than the prefab takes, and
    /// <see cref="EditorLoadWaitPatch"/> waits out the rest. The mission's header is read again
    /// here; the game keeps the one it just read, so that is a lookup.
    /// </summary>
    [HarmonyPatch(typeof(MissionEditorLoadMenuV2), nameof(MissionEditorLoadMenuV2.SelectMission))]
    internal static class EditorMissionPickWarmUpPatch
    {
        // __0, the mission's key, by position as elsewhere.
        static void Postfix(MissionKey __0)
        {
            try
            {
                WarmUp(__0);
            }
            catch (Exception e)
            {
                Plugin.LogDebug($"could not begin the warm-up for the mission picked in the editor: {e.Message}");
            }
        }

        // Apart from the postfix, and never inlined into it, so that a game member gone in an update
        // fails when this is compiled, inside the postfix's try, and not the postfix itself, which
        // would take the menu's click down with it. The same holds for the patches below.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void WarmUp(MissionKey key)
        {
            // A header that does not read has been reported by the game already.
            if (!key.TryQuickLoad(out MissionQuickLoad mission, out _)) return;
            if (mission.MapKey.Type != MapKey.KeyType.GameWorldPrefab) return;

            BundleLoader.WarmUpFor(mission.MapKey.Path, "a mission on it was picked in the mission editor");
        }
    }

    /// <summary>
    /// Holds a mission load inside the mission editor back until its custom map's prefab is read,
    /// behind the game's loading screen with the main thread running, instead of letting the whole
    /// read happen at once on the main thread.
    ///
    /// From the main menu, <c>LoadEditor</c> hosts and changes scene, behind the loading screen, and
    /// none of this applies. Inside the editor the GameWorld scene is already loaded:
    /// <c>MapLoader.Load</c> calls <c>EnableMap</c> in the same frame, and preparing the map there
    /// (<c>LoadedMap.Root</c>) would wait out the rest of the prefab before the editor's fade or the
    /// Load menu's loading message could draw: 7.1 s for Swiss Alps 0.3.0, measured in a 2022.3.62f2
    /// player on 2026-10-04. When every map was read at game start that wait was nothing; now it is
    /// at most one prefab's read per map and session, spent with the game running and the editor
    /// covered (see <see cref="LoadWhenRead"/>).
    ///
    /// The game lets frames pass at this point anyway (its own fade before the map is swapped, two
    /// frames before the mission is set up), and the editor is offline, so a wait here meets nothing
    /// the game does not already allow for. A load asked for while one waits supersedes it; leaving
    /// the editor meanwhile drops it. Without this patch the load still happens, at once, with the
    /// wait on the main thread.
    /// </summary>
    [HarmonyPatch(typeof(MissionEditor), nameof(MissionEditor.LoadEditor), new[] { typeof(Mission) })]
    internal static class EditorLoadWaitPatch
    {
        /// <summary>Counts the editor's mission loads, so a held load knows when another has been
        /// asked for since.</summary>
        static int _latest;

        // __0, the mission, by position as elsewhere.
        static bool Prefix(Mission __0, ref UniTask __result)
        {
            int ticket = ++_latest;
            try
            {
                return !TryHold(__0, ticket, ref __result);
            }
            catch (Exception e)
            {
                Plugin.LogDebug($"could not hold the editor's load for its map's prefab, loading now: {e.Message}");
                return true;
            }
        }

        /// <summary>True, with <paramref name="held"/> the load that waits, if this load is held.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool TryHold(Mission mission, int ticket, ref UniTask held)
        {
            // The game's own test for which way LoadEditor goes.
            if (GameManager.gameState == GameState.Menu) return false;
            if (mission == null || mission.MapKey.Type != MapKey.KeyType.GameWorldPrefab) return false;

            string key = mission.MapKey.Path;
            BundleLoader.WarmUpFor(key, "a mission on it is being opened in the mission editor");
            LoadedMap map = BundleLoader.WarmingUpFor(key);
            if (map == null) return false;

            held = LoadWhenRead(mission, map, ticket, GameManager.gameState);
            return true;
        }

        /// <summary>
        /// Waits for the prefab, then loads the mission, all behind the game's loading screen.
        ///
        /// The Load menu has closed itself by now, and <c>LoadEditor(NewMissionConfig)</c> has already
        /// made the new mission the current one, so an editor left usable meanwhile would show the old
        /// map with nothing pending, and take units placed or a save made into a mission that is not
        /// on it. The loading screen covers the editor and its input, as the frozen main thread did
        /// before. It is the one the game shows around its own loads (playing from the editor, and
        /// back), counted, so each held load shows it once and hides it once, whether it goes on, is
        /// dropped, or fails: a load that supersedes this one keeps it up with its own count, and the
        /// game's fade inside <c>EnableMap</c> runs underneath it.
        /// </summary>
        static async UniTask LoadWhenRead(Mission mission, LoadedMap map, int ticket, GameState state)
        {
            long start = Stopwatch.GetTimestamp();
            Plugin.LogDebug($"{map.Name()}: the mission editor waits for its prefab before loading '{mission.Name}'");

            LoadingScreen screen = ShowLoadingScreen();
            try
            {
                while (map.WarmingUp) await UniTask.Yield();

                double waited = LoadedMap.Seconds(start, Stopwatch.GetTimestamp());
                if (ticket != _latest || GameManager.gameState != state)
                {
                    Plugin.LogDebug($"{map.Name()}: prefab read after {waited:0.0} s; '{mission.Name}' is no longer " +
                                    "to be loaded (another load was asked for, or the editor was left)");
                    return;
                }

                Plugin.LogDebug($"{map.Name()}: prefab read after {waited:0.0} s behind the loading screen; loading '{mission.Name}'");
                await MissionEditor.LoadEditor(mission);
            }
            finally
            {
                HideLoadingScreen(screen);
            }
        }

        /// <summary>The game's loading screen, shown, or null if it could not be.</summary>
        static LoadingScreen ShowLoadingScreen()
        {
            try
            {
                LoadingScreen screen = LoadingScreen.GetLoadingScreen();
                if (screen == null) return null;

                screen.ShowLoadingScreen();
                return screen;
            }
            catch (Exception e)
            {
                Plugin.LogDebug($"could not show the loading screen while the editor waits: {e.Message}");
                return null;
            }
        }

        /// <summary>Takes back this load's showing of <paramref name="screen"/>.</summary>
        static void HideLoadingScreen(LoadingScreen screen)
        {
            // Unity's null: the screen is kept across scenes, but not past the game closing.
            if (screen == null) return;

            try
            {
                screen.HideLoadingScreen();
            }
            catch (Exception e)
            {
                Plugin.LogDebug($"could not hide the loading screen after the editor's wait: {e.Message}");
            }
        }
    }

    /// <summary>
    /// Starts reading a custom map's prefab when a lobby on it is joined, from the map name the lobby
    /// advertises (<see cref="WarmUpPolicy.ForLobby"/>).
    ///
    /// A joining client learns the map's key only from the server's <c>LoadMapMessage</c>, and from
    /// there the prefab is read ahead of the GameWorld scene, one asynchronous load after the other:
    /// about 7 s of a still loading bar for Swiss Alps, measured in a player. Starting at the join
    /// takes the time spent connecting off that. <c>TryJoinLobby</c> is where every join from a
    /// lobby goes (the server list, an invite, a password typed in); only one that got as far as
    /// connecting counts, which is when the game has recorded the lobby as joined. A direct connect
    /// has no lobby and starts at the map's key, as without this.
    /// </summary>
    [HarmonyPatch(typeof(SteamLobby), nameof(SteamLobby.TryJoinLobby))]
    internal static class LobbyJoinWarmUpPatch
    {
        // __0, the lobby, by position as elsewhere.
        static void Postfix(SteamLobby __instance, LobbyInstance __0)
        {
            try
            {
                WarmUp(__instance, __0);
            }
            catch (Exception e)
            {
                Plugin.LogDebug($"could not begin the warm-up for the lobby being joined: {e.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void WarmUp(SteamLobby steam, LobbyInstance lobby)
        {
            if (steam == null || lobby == null || !ReferenceEquals(steam._joinedLobby, lobby)) return;

            BundleLoader.WarmUpForLobby(lobby.MapNameSanitized, "a lobby on it is being joined");
        }
    }
}
