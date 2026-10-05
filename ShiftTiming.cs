using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using UnityEngine;
using UnityEngine.LowLevel;
using UnityEngine.PlayerLoop;

namespace CustomMaps
{
    /// <summary>
    /// Times every floating-origin shift on a custom map, in the player, so a hitch can be measured
    /// on the machine that has it and two builds of the plugin or the map compared there.
    ///
    /// The game moves the world back towards the origin each time the camera passes 1,024 m from
    /// it (<c>FloatingOrigin.OriginShift</c>, from <c>CameraStateManager.LateUpdate</c>). On Swiss
    /// Alps 0.3.0 a player's profile (2026-10) put one shift at about 43 ms: the call itself, mostly
    /// its <c>Physics.SyncTransforms</c>, the renderer update at the end of the same frame, and the
    /// physics step after it. All three are timed here, with a Stopwatch, and handed to
    /// <see cref="ShiftStats"/>, which keeps the figures and words the log lines:
    ///
    ///  - the call, by a prefix and a postfix on <c>OriginShift</c>, and <c>SyncTransforms</c>
    ///    apart by two calls the transpiler puts round it (<c>Patches/ShiftTimingPatches.cs</c>);
    ///  - <c>PostLateUpdate.UpdateAllRenderers</c> and <c>FixedUpdate.PhysicsFixedUpdate</c>, by
    ///    systems of our own put either side of each in Unity's player loop, the way the 2026-10
    ///    bench timed them.
    ///
    /// One line a shift at the debug level, and every <see cref="ShiftStats.Window"/> shifts a
    /// summary at the info level, so it reaches the log without debug logging and without filling
    /// it. No setting turns it on or off: it costs a few clock reads a frame (about a microsecond
    /// or less: 0.18 to 0.42 µs in the quiet runs of the 2026-10-04 and -05 probes, players built
    /// from this file over a synthetic map of Swiss Alps' size, and lost in the noise on a busy
    /// machine), allocates nothing but the lines it writes, and does no work in the game's place.
    ///
    /// Only while a custom map is in play: the player-loop systems go in when one is applied
    /// (<c>LevelInfo.ApplyMapSettings</c>) and come out when it is destroyed or another map is
    /// applied; the patch on <c>OriginShift</c> stays, and returns at once while there are none.
    /// </summary>
    internal static class ShiftTiming
    {
        static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

        /// <summary>Set once the patch on <c>OriginShift</c> is in place; without it there is
        /// nothing to time, and the player loop is left alone.</summary>
        internal static bool Patched;

        /// <summary>True while a custom map is in play and the player-loop systems are in.</summary>
        static bool _on;

        /// <summary>
        /// Renderer updates our systems have seen since they went in, up to 2; shifts are timed only
        /// after the second. A player loop set in the middle of a frame does not run our systems for
        /// the rest of that frame (the probe of 2026-10-04 showed it: a shift in the frame the map
        /// was applied took the next frame's renderer update for its own), and the length of that
        /// frame, which holds the end of the map's load, is no usual frame either.
        /// </summary>
        static int _framesOn;

        static ShiftStats _stats;
        static MapSettings _instance;
        static string _map;

        /// <summary>True once the map's colliders and renderers have been counted (<see cref="CountUnder"/>).</summary>
        static bool _counted;

        static Vector3 _originBefore;
        static long _shiftStart, _syncStart, _syncEnd, _stepStart, _renderersStart;

        // The player-loop systems' types: Unity names a system by its type, and these are how ours
        // are found again to take them out.
        struct ShiftTimingStepStart { }
        struct ShiftTimingStepEnd { }
        struct ShiftTimingRenderersStart { }
        struct ShiftTimingRenderersEnd { }

        static readonly Type[] Systems =
        {
            typeof(ShiftTimingStepStart), typeof(ShiftTimingStepEnd),
            typeof(ShiftTimingRenderersStart), typeof(ShiftTimingRenderersEnd),
        };

        // Kept for the life of the process: the player loop holds only a native pointer to each.
        static readonly PlayerLoopSystem.UpdateFunction OnStepStart = StepStart;
        static readonly PlayerLoopSystem.UpdateFunction OnStepEnd = StepEnd;
        static readonly PlayerLoopSystem.UpdateFunction OnRenderersStart = RenderersStart;
        static readonly PlayerLoopSystem.UpdateFunction OnRenderersEnd = RenderersEnd;
        static readonly Action OnMapDestroyed = Stop;

        /// <summary>A map was applied: times its shifts if it is one of ours, and stops timing the
        /// one before in any case.</summary>
        internal static void MapApplied(MapSettings mapSettings)
        {
            Stop();
            // Only with debug logging on: a diagnostic for comparing machines, not something every
            // player's log should carry.
            if (!Patched || Plugin.Disabled || mapSettings == null || !Plugin.DebugEnabled) return;

            LoadedMap map = MapFixups.LoadedMapFor(mapSettings);
            if (map == null) return;

            _map = map.Name();
            _counted = false;

            // Without the physics step to anchor on, neither of its systems goes in, and the shifts
            // are finished without a step (n/a) rather than waiting for one as in a paused game.
            // The renderer update's end goes in whatever happens: it is what counts the frames.
            PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
            RemoveFrom(ref loop);
            bool step = Insert(ref loop, typeof(FixedUpdate), typeof(FixedUpdate.PhysicsFixedUpdate),
                               typeof(ShiftTimingStepStart), OnStepStart, typeof(ShiftTimingStepEnd), OnStepEnd,
                               aloneWithoutAnchor: false);
            bool renderers = Insert(ref loop, typeof(PostLateUpdate), typeof(PostLateUpdate.UpdateAllRenderers),
                                    typeof(ShiftTimingRenderersStart), OnRenderersStart, typeof(ShiftTimingRenderersEnd), OnRenderersEnd,
                                    aloneWithoutAnchor: true);
            PlayerLoop.SetPlayerLoop(loop);
            _stats = new ShiftStats(stepsTimed: step);

            _instance = mapSettings;
            mapSettings.BeforeDestroy += OnMapDestroyed;
            _shiftStart = _stepStart = _renderersStart = 0;
            _framesOn = 0;
            _on = true;

            Plugin.LogDebug($"origin shift timing on for {_map}: physics step {(step ? "timed" : "not found, not timed")}, " +
                            $"renderer update {(renderers ? "timed" : "not found, not timed")}");
        }

        /// <summary>Stops timing: the shift still followed and the summary's window are written
        /// out, and the player-loop systems taken out. Also when the map is destroyed.</summary>
        internal static void Stop()
        {
            if (!_on) return;
            _on = false;

            try
            {
                while (_stats.Flush()) WriteShift();
                if (_stats.HasWindow) Plugin.LogInfo($"{_map}: {_stats.Summarise(Time.realtimeSinceStartupAsDouble)}");
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"origin shift timing: {e.Message}");
            }

            try
            {
                PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
                if (RemoveFrom(ref loop)) PlayerLoop.SetPlayerLoop(loop);
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"origin shift timing: could not leave the player loop: {e.Message}");
            }

            // The managed object outlives a destroyed map, and its event with it.
            if ((object)_instance != null) _instance.BeforeDestroy -= OnMapDestroyed;
            _instance = null;
            _stats = null;
        }

        // ---- Called from the patch on FloatingOrigin.OriginShift, every frame. ----

        /// <summary>Before the game's code: notes the origin, to tell afterwards whether it moved.</summary>
        internal static void ShiftStart()
        {
            if (!_on || _framesOn < 2) return;
            _originBefore = Datum.originPosition;
            _syncStart = _syncEnd = 0;
            _shiftStart = Stopwatch.GetTimestamp();
        }

        /// <summary>Put in front of the shift's <c>Physics.SyncTransforms</c> by the transpiler.</summary>
        internal static void SyncStart()
        {
            if (_on) _syncStart = Stopwatch.GetTimestamp();
        }

        /// <summary>Put after the shift's <c>Physics.SyncTransforms</c> by the transpiler.</summary>
        internal static void SyncEnd()
        {
            if (_on) _syncEnd = Stopwatch.GetTimestamp();
        }

        /// <summary>After the game's code and every other patch on it, with the list of roots the
        /// method fills and moves (its private <c>roots</c>, which the patch is handed by Harmony).
        /// Nearly every frame the camera is still inside the threshold and the origin has not moved:
        /// then this is one comparison and out.</summary>
        internal static void ShiftEnd(List<GameObject> roots)
        {
            if (!_on || _shiftStart == 0) return;
            long end = Stopwatch.GetTimestamp();
            long start = _shiftStart;
            _shiftStart = 0;

            if (Datum.originPosition == _originBefore) return;

            bool apart = _syncStart != 0 && _syncEnd >= _syncStart;
            double total = (end - start) * TickMs;
            double move = apart ? (_syncStart - start) * TickMs : double.NaN;
            double sync = apart ? (_syncEnd - _syncStart) * TickMs : double.NaN;

            // A shift the next one cuts short waits for the renderer update, which brings its last
            // frame's length; only a third shift with no renderer update between finishes one here.
            if (_stats.Shifted(total, move, sync, roots != null ? roots.Count : -1)) Report();
        }

        // ---- The player-loop systems. ----

        static void StepStart()
        {
            if (_on) _stepStart = Stopwatch.GetTimestamp();
        }

        static void StepEnd()
        {
            if (!_on || _stepStart == 0) return;
            long end = Stopwatch.GetTimestamp();
            _stats.Stepped((end - _stepStart) * TickMs);
            _stepStart = 0;
        }

        static void RenderersStart()
        {
            if (_on) _renderersStart = Stopwatch.GetTimestamp();
        }

        static void RenderersEnd()
        {
            if (!_on) return;
            long end = Stopwatch.GetTimestamp();
            double ms = _renderersStart != 0 ? (end - _renderersStart) * TickMs : double.NaN;
            _renderersStart = 0;

            if (_framesOn < 2)
            {
                _framesOn++;
                return;
            }

            if (!_stats.Rendered(ms, Time.unscaledDeltaTime * 1000.0)) return;
            Report();

            // Here, at the end of the frame after the step's, the count's few tens of milliseconds
            // fall outside all the shift's frames; the frame that holds them goes once into the
            // usual frame's median, which one sample in 31 does not move. Not when a shift cut short
            // was finished in a new shift's frame, which the count would land in.
            if (_on && !_counted && !_stats.Following && Plugin.DebugEnabled)
                CountUnder();
        }

        /// <summary>A shift was finished: its line, and the summary when one is due. Strings are
        /// made only here, once a shift, and the shift's line only with debug logging on.</summary>
        static void Report()
        {
            try
            {
                WriteShift();
                double now = Time.realtimeSinceStartupAsDouble;
                if (_stats.SummaryDue(now)) Plugin.LogInfo($"{_map}: {_stats.Summarise(now)}");
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"origin shift timing stopped: {e.Message}");
                Stop();
            }
        }

        static void WriteShift()
        {
            if (Plugin.DebugEnabled)
                Plugin.LogDebug(ShiftStats.Describe(_stats.Last));
        }

        // ---- Setting up. ----

        /// <summary>
        /// The colliders and renderers enabled under the map, which is what a shift moves: the
        /// physics side scales with the colliders, the renderer update with the enabled renderers.
        /// Counted once, on a line of its own, after the first shift timed with debug logging on,
        /// rather than at load: by then whatever switches the city buildings by distance has run
        /// round the player, as it had not when the map was applied. A snapshot all the same, which
        /// the shifts after it do not follow as the player flies on. Only with debug logging on,
        /// since it walks the whole map: 72,521 colliders and 81,563 renderers took 23 to 27 ms in
        /// the 2026-10-04 probe. Figures in the same invariant form as the shifts' lines.
        /// </summary>
        static void CountUnder()
        {
            _counted = true;
            if (_instance == null) return;

            try
            {
                long start = Stopwatch.GetTimestamp();

                var colliders = new List<Collider>();
                _instance.GetComponentsInChildren(false, colliders);
                int enabledColliders = 0;
                foreach (Collider collider in colliders)
                    if (collider.enabled) enabledColliders++;

                var renderers = new List<Renderer>();
                _instance.GetComponentsInChildren(false, renderers);
                int enabledRenderers = 0;
                foreach (Renderer renderer in renderers)
                    if (renderer.enabled) enabledRenderers++;

                double ms = (Stopwatch.GetTimestamp() - start) * TickMs;
                Plugin.LogDebug(string.Format(CultureInfo.InvariantCulture,
                    "origin shift timing: {0:N0} collider(s) and {1:N0} renderer(s) enabled under the map after shift {2} " +
                    "(counted in {3:0.0} ms)", enabledColliders, enabledRenderers, _stats.Count, ms));
            }
            catch (Exception e)
            {
                Plugin.LogDebug($"origin shift timing: could not count the map's colliders and renderers: {e.Message}");
            }
        }

        /// <summary>
        /// Puts <paramref name="before"/> and <paramref name="after"/> either side of
        /// <paramref name="anchor"/> in <paramref name="phase"/>. When the phase is there but the
        /// anchor is not, <paramref name="after"/> goes at the end of the phase alone if
        /// <paramref name="aloneWithoutAnchor"/>, so the frame is still counted (with no time for the
        /// anchor), and nothing goes in otherwise; returns true only when both went in round the
        /// anchor.
        /// </summary>
        static bool Insert(ref PlayerLoopSystem loop, Type phase, Type anchor,
                           Type before, PlayerLoopSystem.UpdateFunction beforeUpdate,
                           Type after, PlayerLoopSystem.UpdateFunction afterUpdate, bool aloneWithoutAnchor)
        {
            PlayerLoopSystem[] phases = loop.subSystemList;
            if (phases == null) return false;

            for (int i = 0; i < phases.Length; i++)
            {
                if (phases[i].type != phase) continue;

                var systems = new List<PlayerLoopSystem>(phases[i].subSystemList ?? Array.Empty<PlayerLoopSystem>());
                int at = systems.FindIndex(s => s.type == anchor);
                if (at < 0 && !aloneWithoutAnchor) return false;

                var end = new PlayerLoopSystem { type = after, updateDelegate = afterUpdate };
                if (at < 0)
                {
                    systems.Add(end);
                }
                else
                {
                    systems.Insert(at + 1, end);
                    systems.Insert(at, new PlayerLoopSystem { type = before, updateDelegate = beforeUpdate });
                }

                phases[i].subSystemList = systems.ToArray();
                return at >= 0;
            }

            return false;
        }

        /// <summary>Takes our systems out of every phase of <paramref name="loop"/>; true when there
        /// were any. Others' systems, the game's own performance tracker's among them, stay.</summary>
        static bool RemoveFrom(ref PlayerLoopSystem loop)
        {
            PlayerLoopSystem[] phases = loop.subSystemList;
            if (phases == null) return false;

            bool removed = false;
            for (int i = 0; i < phases.Length; i++)
            {
                PlayerLoopSystem[] systems = phases[i].subSystemList;
                if (systems == null) continue;

                var kept = new List<PlayerLoopSystem>(systems.Length);
                foreach (PlayerLoopSystem system in systems)
                    if (Array.IndexOf(Systems, system.type) < 0) kept.Add(system);

                if (kept.Count == systems.Length) continue;
                phases[i].subSystemList = kept.ToArray();
                removed = true;
            }

            return removed;
        }
    }
}
