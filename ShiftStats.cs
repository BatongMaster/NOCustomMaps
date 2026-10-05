using System;
using System.Globalization;
using System.Text;

namespace CustomMaps
{
    /// <summary>
    /// What each floating-origin shift cost, and the summary the log prints every
    /// <see cref="Window"/> shifts, so a hitch can be measured on the machine that has it and two
    /// builds compared there. Pure bookkeeping and wording, free of game and Unity types so it is
    /// tested; <c>ShiftTiming</c> feeds it from the game's player loop.
    ///
    /// The game's <c>FloatingOrigin.OriginShift</c> moves every root of the scene back by a whole
    /// number of 64 m steps once the camera is more than 1,024 m from the origin, then calls
    /// <c>Physics.SyncTransforms</c>. On Swiss Alps 0.3.0 a player's profile (2026-10) put one shift
    /// at about 43 ms, in three places, and each is followed here:
    ///
    ///  - the shift itself, in <c>CameraStateManager.LateUpdate</c>, nearly all of it
    ///    <c>SyncTransforms</c> (15.8 ms): PhysX re-reading the pose of every collider that moved;
    ///  - the renderer update later in the same frame (<c>PostLateUpdate.UpdateAllRenderers</c>,
    ///    6.3 ms): every enabled renderer's bounds recomputed;
    ///  - the first physics step after it (<c>FixedUpdate.PhysicsFixedUpdate</c>, 8.6 ms): the
    ///    scene-query structures rebuilt. Not always in the very next frame: at a high frame rate
    ///    frames without a physics step come between.
    ///
    /// Each comes with its usual cost, the median of the last <see cref="Recent"/> samples of the
    /// same thing in frames and steps that were not a shift's, so the excess is the difference.
    /// Every frame from the shift's to the step's is also given whole, as the game's own
    /// <c>Time.unscaledDeltaTime</c> counts them: the shift's frame, the frames between (summed)
    /// and the step's. They hold whatever the three figures miss. Whichever comes first after a
    /// shift, a ray cast or the physics step, pays for the scene-query rebuild (the 2026-10 bench:
    /// a ray cast just after <c>SyncTransforms</c> took 3.4 ms, and the step after it fell from
    /// 5.8 to 2.5 ms), and at 120 or 144 frames a second that is often a script's ray cast in a
    /// frame between, with no step: the frames between are what show it then, and they stay out of
    /// the usual frame for that reason.
    ///
    /// The order of calls within a frame is the player loop's: <see cref="Stepped"/> for each
    /// physics step (FixedUpdate), <see cref="Shifted"/> when the world moved (LateUpdate), then
    /// <see cref="Rendered"/> once (PostLateUpdate), with the length of the frame before. A shift is
    /// finished once all its frames' lengths are known, which is the frame after its physics step;
    /// a paused game runs no steps, so a shift waits <see cref="StepTimeout"/> frames for one at most.
    /// Nothing here allocates except the two describing methods, which the caller runs once a
    /// shift, and only when the line is wanted.
    /// </summary>
    internal sealed class ShiftStats
    {
        /// <summary>Shifts per summary line. A jet at 250 m/s shifts every four seconds or so, so
        /// twenty make a line every minute or two; a helicopter's come minutes apart.</summary>
        public const int Window = 20;

        /// <summary>Seconds at least between two summary lines, so a free camera racing across the
        /// map in the mission editor (a shift every few frames) does not fill the info log; the
        /// window then grows until the time is up.</summary>
        public const double SummaryInterval = 30.0;

        /// <summary>Frames a shift waits for its physics step before it is written without one.</summary>
        public const int StepTimeout = 30;

        /// <summary>Samples behind each usual cost: half a second of physics steps at the game's
        /// 60 Hz. A median, so a load's frame of several seconds does not stand in for normal.</summary>
        public const int Recent = 31;

        /// <summary>How a shift stopped being followed, which says why a figure it lacks is missing.</summary>
        public enum Ending
        {
            /// <summary>Its frames and its physics step were all seen.</summary>
            Complete,

            /// <summary>No physics step came within <see cref="StepTimeout"/> frames, as in a
            /// paused game.</summary>
            NoStep,

            /// <summary>The next shift came before it was complete.</summary>
            NextShift,

            /// <summary>The timing stopped before it was complete: the map unloaded or another
            /// was applied.</summary>
            Stopped,
        }

        /// <summary>One shift, in milliseconds; NaN for what was not measured.</summary>
        public struct Shift
        {
            /// <summary>1 for the first shift on the map.</summary>
            public int Number;

            /// <summary>The whole of <c>OriginShift</c>, every patch on it included.</summary>
            public double Total;

            /// <summary>Moving the scene's roots: from the start of the call to <c>SyncTransforms</c>.</summary>
            public double Move;

            /// <summary><c>Physics.SyncTransforms</c> alone.</summary>
            public double Sync;

            /// <summary>Roots of the scene moved, or -1 when not known.</summary>
            public int Roots;

            /// <summary><c>PostLateUpdate.UpdateAllRenderers</c> in the shift's frame.</summary>
            public double Renderers;

            /// <summary>The first <c>FixedUpdate.PhysicsFixedUpdate</c> after the shift.</summary>
            public double Step;

            /// <summary>The frame the shift happened in, whole.</summary>
            public double ShiftFrame;

            /// <summary>The frames after the shift's and before the step's, whole and summed; NaN
            /// when there were none, as at 60 frames a second, where the step runs in the next.</summary>
            public double Between;

            /// <summary>How many frames <see cref="Between"/> holds.</summary>
            public int BetweenFrames;

            /// <summary>The frame that ran <see cref="Step"/>, whole: always a later one than the
            /// shift's, since the physics steps of a frame run before its LateUpdate.</summary>
            public double StepFrame;

            /// <summary>The usual costs when the shift was finished.</summary>
            public double UsualRenderers, UsualStep, UsualFrame;

            /// <summary>How it was finished.</summary>
            public Ending End;
        }

        /// <summary>A shift being followed, and how far.</summary>
        struct Followed
        {
            public Shift Shift;
            public int Frames;              // renderer updates since the shift, its own frame's included
            public int StepAt;              // Frames when the shift's physics step ran; -1 until it has
            public bool ShiftFrameSeen, StepFrameSeen;
            public double BetweenSum;
            public int BetweenCount;
        }

        readonly bool _stepsTimed;
        readonly Median _usualStep = new Median(Recent);
        readonly Median _usualRenderers = new Median(Recent);
        readonly Median _usualFrame = new Median(Recent);

        // The shift being followed, and one the next shift cut short, which still waits for the
        // length of its last frame: the next renderer update brings it.
        bool _following, _cut;
        Followed _current, _cutShort;
        int _started;

        // The summary's window.
        Spread _total, _sync, _renderers, _step, _shiftFrames, _between, _stepFrames;
        bool _anyBetween;
        int _windowCount;
        double _lastSummary = double.NegativeInfinity;

        /// <param name="stepsTimed">False when the physics step cannot be timed (its system was not
        /// found in the player loop): shifts are then finished without one, reading n/a, rather
        /// than waiting for a step that never comes and reading like a paused game.</param>
        public ShiftStats(bool stepsTimed = true)
        {
            _stepsTimed = stepsTimed;
        }

        /// <summary>Shifts finished on this map.</summary>
        public int Count { get; private set; }

        /// <summary>The shift finished last.</summary>
        public Shift Last { get; private set; }

        /// <summary>True while a shift waits for its renderer update, physics step or frames.</summary>
        public bool Following => _following || _cut;

        /// <summary>True when any shift waits to be summarised.</summary>
        public bool HasWindow => _windowCount > 0;

        /// <summary>True once <see cref="Window"/> shifts wait to be summarised and the last summary
        /// was <see cref="SummaryInterval"/> seconds or more before <paramref name="now"/>.</summary>
        public bool SummaryDue(double now) => _windowCount >= Window && now - _lastSummary >= SummaryInterval;

        /// <summary>
        /// The world moved: <paramref name="total"/> for the whole call, of it <paramref name="move"/>
        /// before <c>SyncTransforms</c> and <paramref name="sync"/> in it (NaN when the two are not
        /// timed apart). A shift still followed is cut short: finished at the next renderer update,
        /// which brings the length of its last frame. Returns true when a shift was finished here,
        /// which takes two shifts with no renderer update between, and <see cref="Last"/> is then
        /// that shift.
        /// </summary>
        public bool Shifted(double total, double move, double sync, int roots)
        {
            bool finished = false;
            if (_cut)
            {
                Finish(ref _cutShort, Ending.NextShift);
                _cut = false;
                finished = true;
            }

            if (_following)
            {
                _cutShort = _current;
                _cut = true;
            }

            _current = new Followed
            {
                Shift = new Shift
                {
                    Number = ++_started,
                    Total = total,
                    Move = move,
                    Sync = sync,
                    Roots = roots,
                    Renderers = double.NaN,
                    Step = double.NaN,
                    ShiftFrame = double.NaN,
                    Between = double.NaN,
                    StepFrame = double.NaN,
                    UsualRenderers = double.NaN,
                    UsualStep = double.NaN,
                    UsualFrame = double.NaN,
                },
                StepAt = -1,
            };
            _following = true;
            return finished;
        }

        /// <summary>A physics step took <paramref name="ms"/>: the shift's, if it is the first
        /// since one, and otherwise an ordinary step.</summary>
        public void Stepped(double ms)
        {
            if (_following && _current.Frames > 0 && _current.StepAt < 0)
            {
                _current.Shift.Step = ms;
                _current.StepAt = _current.Frames;
                return;
            }

            _usualStep.Add(ms);
        }

        /// <summary>
        /// The renderer update took <paramref name="rendererMs"/>, at the end of a frame whose
        /// predecessor lasted <paramref name="previousFrameMs"/>. Returns true when this finished a
        /// shift, and <see cref="Last"/> is then that shift.
        /// </summary>
        public bool Rendered(double rendererMs, double previousFrameMs)
        {
            if (!_following)
            {
                _usualRenderers.Add(rendererMs);
                _usualFrame.Add(previousFrameMs);
                return false;
            }

            if (_current.Frames == 0)
            {
                // The shift's own frame. The frame before it is the last of a shift the new one cut
                // short, if any, which may be its step frame and so complete it; else an ordinary one.
                _current.Shift.Renderers = rendererMs;
                _current.Frames = 1;

                if (!_cut)
                {
                    _usualFrame.Add(previousFrameMs);
                    return false;
                }

                // One cut short before its own renderer update (two shifts in a frame) has no
                // frame of its own yet, and the frame before is an ordinary one.
                _cut = false;
                bool complete = false;
                if (_cutShort.Frames > 0) complete = Advance(ref _cutShort, previousFrameMs);
                else _usualFrame.Add(previousFrameMs);
                Finish(ref _cutShort, complete ? Ending.Complete : Ending.NextShift);
                return true;
            }

            _usualRenderers.Add(rendererMs);
            if (Advance(ref _current, previousFrameMs))
            {
                Finish(ref _current, Ending.Complete);
                _following = false;
                return true;
            }

            if (_current.StepAt >= 0 || _current.Frames <= StepTimeout || !_current.ShiftFrameSeen) return false;
            Finish(ref _current, Ending.NoStep);
            _following = false;
            return true;
        }

        /// <summary>Hands <paramref name="f"/> the length of its next frame after its own: the
        /// shift's frame first, then those between, then the step's. True once it has them all.</summary>
        bool Advance(ref Followed f, double frameMs)
        {
            int index = f.Frames - 1;    // 0 for the shift's frame
            f.Frames++;

            if (index == 0) { f.Shift.ShiftFrame = frameMs; f.ShiftFrameSeen = true; }
            else if (index == f.StepAt) { f.Shift.StepFrame = frameMs; f.StepFrameSeen = true; }
            else if (!double.IsNaN(frameMs)) { f.BetweenSum += frameMs; f.BetweenCount++; }

            return f.ShiftFrameSeen && (f.StepFrameSeen || !_stepsTimed);
        }

        /// <summary>Finishes a shift still followed, with what it has, as when the map unloads:
        /// one cut short by the next first, then the latest. Returns true when there was one, and
        /// <see cref="Last"/> is then that shift; call until false.</summary>
        public bool Flush()
        {
            if (_cut)
            {
                _cut = false;
                Finish(ref _cutShort, Ending.NextShift);
                return true;
            }

            if (!_following) return false;
            Finish(ref _current, Ending.Stopped);
            _following = false;
            return true;
        }

        void Finish(ref Followed f, Ending end)
        {
            Shift s = f.Shift;
            s.End = end;
            s.UsualRenderers = _usualRenderers.Value;
            s.UsualStep = _usualStep.Value;
            s.UsualFrame = _usualFrame.Value;

            // A paused game's frames are not between a shift and a step; nor are they usual, and
            // they are let go.
            bool between = end != Ending.NoStep && f.BetweenCount > 0;
            s.Between = between ? f.BetweenSum : double.NaN;
            s.BetweenFrames = between ? f.BetweenCount : 0;

            _total.Add(s.Total);
            _sync.Add(s.Sync);
            _renderers.Add(s.Renderers);
            _step.Add(s.Step);
            _shiftFrames.Add(s.ShiftFrame);
            _stepFrames.Add(s.StepFrame);

            // In the summary a complete shift with no frame between counts as 0.00, so the mean is
            // over the shifts, not over those that happened to have such frames.
            if (between) _anyBetween = true;
            _between.Add(between ? s.Between : end == Ending.Complete ? 0.0 : double.NaN);
            _windowCount++;

            Last = s;
            Count = s.Number;
        }

        /// <summary>
        /// The line for one shift, for the debug log:
        ///
        ///     origin shift 7: 14.21 ms (roots 1.10 ms, SyncTransforms 13.11 ms; 37 roots);
        ///     renderer update 6.30 ms (usual 0.91); next physics step 8.60 ms (usual 1.20);
        ///     shift frame 31.50 ms, step frame 18.00 ms (usual 8.30)
        ///
        /// With frames between the shift's and the step's, as at a high frame rate, their sum comes
        /// between the two: "shift frame 15.20 ms, 1 frame between 9.00 ms, step frame 7.10 ms".
        /// A shift finished before all its figures were in says why, so a missing step is not taken
        /// for a paused game when the next shift or the map's unloading came first: "no physics step
        /// within 30 frames", or at the end "cut short by the next shift" or "cut short as the
        /// timing stopped".
        /// </summary>
        public static string Describe(Shift s)
        {
            var b = new StringBuilder(256);
            b.Append("origin shift ").Append(s.Number.ToString(CultureInfo.InvariantCulture)).Append(": ")
             .Append(Ms(s.Total)).Append(" ms (roots ").Append(Ms(s.Move))
             .Append(" ms, SyncTransforms ").Append(Ms(s.Sync)).Append(" ms");
            if (s.Roots >= 0) b.Append("; ").Append(s.Roots.ToString(CultureInfo.InvariantCulture)).Append(" roots");
            b.Append("); renderer update ").Append(Ms(s.Renderers)).Append(" ms (usual ").Append(Ms(s.UsualRenderers)).Append(')');

            if (s.End == Ending.NoStep)
                b.Append("; no physics step within ").Append(StepTimeout.ToString(CultureInfo.InvariantCulture)).Append(" frames");
            else
                b.Append("; next physics step ").Append(Ms(s.Step)).Append(" ms (usual ").Append(Ms(s.UsualStep)).Append(')');

            b.Append("; shift frame ").Append(Ms(s.ShiftFrame)).Append(" ms");
            if (s.BetweenFrames > 0)
                b.Append(", ").Append(s.BetweenFrames.ToString(CultureInfo.InvariantCulture))
                 .Append(s.BetweenFrames == 1 ? " frame between " : " frames between ").Append(Ms(s.Between)).Append(" ms");
            b.Append(", step frame ").Append(Ms(s.StepFrame)).Append(" ms (usual ").Append(Ms(s.UsualFrame)).Append(')');

            if (s.End == Ending.NextShift) b.Append("; cut short by the next shift");
            else if (s.End == Ending.Stopped) b.Append("; cut short as the timing stopped");
            return b.ToString();
        }

        /// <summary>
        /// The summary of the shifts since the last one, for the info log, and a fresh window;
        /// <paramref name="now"/>, in seconds, starts the wait for the next (<see cref="SummaryDue"/>):
        ///
        ///     origin shifts 1-20, mean (max) ms: shift 14.21 (16.00), SyncTransforms 13.11 (14.80);
        ///     renderer update 6.30 (7.10), usual 0.91; next physics step 8.60 (10.20), usual 1.20;
        ///     shift frame 31.50 (40.10), step frame 18.00 (22.00), usual 8.30
        ///
        /// When any of the shifts had frames between its own and its step's, "frames between" comes
        /// after the shift frame: their sum, 0.00 for a shift without. A figure measured for fewer of
        /// the shifts than the window holds says for how many.
        /// </summary>
        public string Summarise(double now)
        {
            int first = Count - _windowCount + 1;
            var b = new StringBuilder(360);
            b.Append("origin shifts ").Append(first.ToString(CultureInfo.InvariantCulture));
            if (_windowCount > 1) b.Append('-').Append(Count.ToString(CultureInfo.InvariantCulture));
            b.Append(", mean (max) ms: shift ");
            Append(b, _total);
            b.Append(", SyncTransforms ");
            Append(b, _sync);
            b.Append("; renderer update ");
            Append(b, _renderers);
            b.Append(", usual ").Append(Ms(_usualRenderers.Value)).Append("; next physics step ");
            Append(b, _step);
            b.Append(", usual ").Append(Ms(_usualStep.Value)).Append("; shift frame ");
            Append(b, _shiftFrames);
            if (_anyBetween)
            {
                b.Append(", frames between ");
                Append(b, _between);
            }
            b.Append(", step frame ");
            Append(b, _stepFrames);
            b.Append(", usual ").Append(Ms(_usualFrame.Value));

            _total = _sync = _renderers = _step = _shiftFrames = _between = _stepFrames = default;
            _anyBetween = false;
            _windowCount = 0;
            _lastSummary = now;
            return b.ToString();
        }

        void Append(StringBuilder b, Spread spread)
        {
            b.Append(Ms(spread.Mean)).Append(" (").Append(Ms(spread.Max)).Append(')');
            if (spread.Count < _windowCount)
                b.Append(" in ").Append(spread.Count.ToString(CultureInfo.InvariantCulture))
                 .Append(" of ").Append(_windowCount.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>Milliseconds to two places, whatever the player's locale; n/a for NaN.</summary>
        static string Ms(double ms) => double.IsNaN(ms) ? "n/a" : ms.ToString("0.00", CultureInfo.InvariantCulture);

        /// <summary>Sum, count and largest of a window's samples, NaN skipped. Mean and largest are
        /// both NaN with no sample, so a figure no shift of the window had reads n/a (n/a), not a
        /// largest of 0.00 that was never measured.</summary>
        struct Spread
        {
            double _sum, _max;

            public int Count;

            public double Mean => Count > 0 ? _sum / Count : double.NaN;

            public double Max => Count > 0 ? _max : double.NaN;

            public void Add(double value)
            {
                if (double.IsNaN(value)) return;
                _sum += value;
                _max = Count == 0 ? value : Math.Max(_max, value);
                Count++;
            }
        }

        /// <summary>The median of the last few samples, NaN skipped. Fixed arrays, so adding a
        /// sample, which happens every frame, allocates nothing, and nor does reading the median,
        /// which happens once a shift: an insertion sort of its own, since the player's
        /// <c>Array.Sort</c> made 128 bytes of garbage a call (a development build's GC counter,
        /// 2026-10-04).</summary>
        sealed class Median
        {
            readonly double[] _ring, _sorted;
            int _count, _next;

            public Median(int size)
            {
                _ring = new double[size];
                _sorted = new double[size];
            }

            public void Add(double value)
            {
                if (double.IsNaN(value)) return;
                _ring[_next] = value;
                _next = (_next + 1) % _ring.Length;
                if (_count < _ring.Length) _count++;
            }

            /// <summary>NaN until there is a sample; the lower middle of an even count.</summary>
            public double Value
            {
                get
                {
                    if (_count == 0) return double.NaN;
                    Array.Copy(_ring, _sorted, _count);
                    for (int i = 1; i < _count; i++)
                    {
                        double value = _sorted[i];
                        int j = i - 1;
                        for (; j >= 0 && _sorted[j] > value; j--) _sorted[j + 1] = _sorted[j];
                        _sorted[j + 1] = value;
                    }
                    return _sorted[(_count - 1) / 2];
                }
            }
        }
    }
}
