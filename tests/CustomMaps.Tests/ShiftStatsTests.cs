using System.Globalization;
using Xunit;

namespace CustomMaps.Tests
{
    public class ShiftStatsTests
    {
        // One frame as the player loop runs it: the physics step (if the frame has one), then the
        // renderer update, given the length of the frame before.
        static bool Frame(ShiftStats stats, double? step, double renderers, double previousFrame)
        {
            if (step.HasValue) stats.Stepped(step.Value);
            return stats.Rendered(renderers, previousFrame);
        }

        // The user's profile on Swiss Alps 0.3.0, at 60 frames a second: a shift of 15.8 ms in
        // SyncTransforms, 6.3 ms of renderer update in its frame, 8.6 ms in the next physics step.
        [Fact]
        public void AShiftIsFinishedTheFrameAfterItsStepWithEachCost()
        {
            var stats = new ShiftStats();
            for (int i = 0; i < 10; i++) Assert.False(Frame(stats, 1.0, 0.5, 16.7));

            stats.Stepped(1.0);                                     // the shift frame's own step, before it
            Assert.False(stats.Shifted(16.1, 0.3, 15.8, 37));
            Assert.True(stats.Following);
            Assert.False(stats.Rendered(6.3, 16.7));                // the shift frame
            Assert.False(Frame(stats, 8.6, 0.5, 43.0));             // the next: its step; the shift frame was 43 ms
            Assert.True(Frame(stats, 1.0, 0.5, 25.0));              // the step's frame was 25 ms

            ShiftStats.Shift s = stats.Last;
            Assert.False(stats.Following);
            Assert.Equal(1, s.Number);
            Assert.Equal(1, stats.Count);
            Assert.Equal(16.1, s.Total);
            Assert.Equal(0.3, s.Move);
            Assert.Equal(15.8, s.Sync);
            Assert.Equal(37, s.Roots);
            Assert.Equal(6.3, s.Renderers);
            Assert.Equal(8.6, s.Step);
            Assert.Equal(43.0, s.ShiftFrame);
            Assert.Equal(25.0, s.StepFrame);
            Assert.Equal(0.5, s.UsualRenderers);
            Assert.Equal(1.0, s.UsualStep);
            Assert.Equal(16.7, s.UsualFrame);
        }

        // The usual costs are what a frame or step costs without a shift: the shift's renderer
        // update, its step and both its frames stay out of them, or a run of shifts would raise
        // the yardstick they are read against.
        [Fact]
        public void TheShiftsOwnCostsStayOutOfTheUsual()
        {
            var stats = new ShiftStats();

            stats.Shifted(16.0, 0.2, 15.8, 1);
            stats.Rendered(6.3, 16.7);                              // the frame before the shift's: ordinary
            Frame(stats, 8.6, 0.4, 43.0);
            Assert.True(Frame(stats, null, 0.6, 25.0));

            ShiftStats.Shift s = stats.Last;
            Assert.True(double.IsNaN(s.UsualStep));                 // no ordinary step yet
            Assert.Equal(0.4, s.UsualRenderers);                    // 0.4 and 0.6, not 6.3
            Assert.Equal(16.7, s.UsualFrame);                       // not 43 or 25
        }

        // At a high frame rate frames without a physics step come between the shift and its step.
        // The step frame is the one that ran it, and the frames between are the shift's too, summed.
        [Fact]
        public void AStepSomeFramesLaterIsTheShiftsAndItsFrameIsTheStepFrame()
        {
            var stats = new ShiftStats();

            stats.Shifted(12.0, 0.2, 11.8, 5);
            Assert.False(stats.Rendered(5.0, 4.0));
            Assert.False(Frame(stats, null, 0.3, 20.0));            // the shift frame was 20 ms
            Assert.False(Frame(stats, null, 0.3, 4.1));
            Assert.False(Frame(stats, 7.5, 0.3, 4.2));              // the step runs three frames on
            Assert.True(Frame(stats, null, 0.3, 11.0));             // and its frame was 11 ms

            ShiftStats.Shift s = stats.Last;
            Assert.Equal(7.5, s.Step);
            Assert.Equal(20.0, s.ShiftFrame);
            Assert.Equal(8.3, s.Between, 9);                        // 4.1 and 4.2
            Assert.Equal(2, s.BetweenFrames);
            Assert.Equal(11.0, s.StepFrame);
            Assert.Equal(4.0, s.UsualFrame);                        // the frame before the shift's only
        }

        // At 144 frames a second the frame after the shift often has no physics step, and a script's
        // ray cast in it pays for the scene-query rebuild the step would have. That frame must show,
        // and must not lift the usual frame the shift is read against.
        [Fact]
        public void AFrameBetweenThatPaysForTheRebuildShowsAndStaysOutOfTheUsual()
        {
            var stats = new ShiftStats();
            for (int i = 0; i < 9; i++) Frame(stats, i % 2 == 0 ? 0.2 : (double?)null, 0.4, 6.9);

            stats.Shifted(12.0, 0.2, 11.8, 5);
            stats.Rendered(2.0, 6.9);                               // the shift's frame
            Frame(stats, null, 0.4, 19.0);                          // no step; the shift frame was 19 ms
            Frame(stats, 0.3, 0.4, 16.0);                           // a cheap step; the frame before paid 9 ms more
            Assert.True(Frame(stats, null, 0.4, 7.0));

            ShiftStats.Shift s = stats.Last;
            Assert.Equal(16.0, s.Between);
            Assert.Equal(1, s.BetweenFrames);
            Assert.Equal(6.9, s.UsualFrame);                        // not 16
            Assert.EndsWith("shift frame 19.00 ms, 1 frame between 16.00 ms, step frame 7.00 ms (usual 6.90)",
                            ShiftStats.Describe(s));
        }

        // In the summary the frames between read as a mean over the shifts, 0.00 for one without,
        // and only once a shift of the window had any.
        [Fact]
        public void TheSummaryGivesTheFramesBetweenOnlyWhenThereWereAny()
        {
            var stats = new ShiftStats();
            CompleteShift(stats, 10.0);
            Assert.DoesNotContain("between", stats.Summarise(0.0));

            CompleteShift(stats, 10.0);
            stats.Shifted(10.0, 0.5, 9.5, 2);
            stats.Rendered(6.0, 16.0);
            Frame(stats, null, 1.0, 40.0);
            Frame(stats, 8.0, 1.0, 12.0);                           // one frame between, 12 ms
            Assert.True(Frame(stats, null, 1.0, 24.0));
            Assert.Contains("shift frame 40.00 (40.00), frames between 6.00 (12.00), step frame 24.00 (24.00)",
                            stats.Summarise(100.0));
        }

        // The usual frame is the median of the last Recent ordinary frames, in whatever order they
        // came: the load's long frames before them are forgotten, not averaged in.
        [Fact]
        public void TheUsualIsTheMedianOfTheRecentSamples()
        {
            var stats = new ShiftStats();
            for (int i = 0; i < 9; i++) stats.Rendered(0.1, 5000.0);
            for (int i = 0; i < ShiftStats.Recent; i++)
                stats.Rendered(0.1, (i * 17 % ShiftStats.Recent) + 1);   // 1 to 31, shuffled

            stats.Shifted(10.0, 0.1, 9.9, 3);
            Assert.True(stats.Flush());
            Assert.Equal(16.0, stats.Last.UsualFrame);

            var few = new ShiftStats();
            foreach (double ms in new[] { 9.0, 3.0, 7.0, 1.0, 5.0 }) few.Rendered(0.1, ms);
            few.Shifted(10.0, 0.1, 9.9, 3);
            few.Flush();
            Assert.Equal(5.0, few.Last.UsualFrame);
        }

        // A paused game runs no physics steps, so a shift waits so many frames for one, then is
        // written without it.
        [Fact]
        public void WithoutAStepAShiftIsFinishedAfterTheTimeout()
        {
            var stats = new ShiftStats();
            stats.Shifted(10.0, 0.1, 9.9, 3);

            for (int i = 0; i < ShiftStats.StepTimeout; i++)
                Assert.False(stats.Rendered(0.2, 16.7));
            Assert.True(stats.Rendered(0.2, 16.7));

            ShiftStats.Shift s = stats.Last;
            Assert.Equal(ShiftStats.Ending.NoStep, s.End);
            Assert.True(double.IsNaN(s.Step));
            Assert.True(double.IsNaN(s.StepFrame));
            Assert.Equal(16.7, s.ShiftFrame);
            Assert.Contains($"no physics step within {ShiftStats.StepTimeout} frames", ShiftStats.Describe(s));
            Assert.DoesNotContain("cut short", ShiftStats.Describe(s));
        }

        // Two shifts closer than a step: the first is finished with what it has when the second
        // comes, and the step that follows is the second's.
        [Fact]
        public void ANewShiftFinishesTheOneStillFollowed()
        {
            var stats = new ShiftStats();
            stats.Shifted(10.0, 0.1, 9.9, 3);
            stats.Rendered(4.0, 16.7);

            Assert.False(stats.Shifted(11.0, 0.1, 10.9, 3));
            Assert.True(stats.Following);
            Assert.True(stats.Rendered(4.5, 30.0));                 // brings the first's frame, and finishes it
            Assert.Equal(1, stats.Last.Number);
            Assert.Equal(4.0, stats.Last.Renderers);
            Assert.Equal(30.0, stats.Last.ShiftFrame);
            Assert.True(double.IsNaN(stats.Last.Step));
            Assert.Equal(ShiftStats.Ending.NextShift, stats.Last.End);

            Frame(stats, 9.0, 0.3, 31.0);
            Assert.True(Frame(stats, null, 0.3, 20.0));
            Assert.Equal(2, stats.Last.Number);
            Assert.Equal(9.0, stats.Last.Step);
            Assert.Equal(31.0, stats.Last.ShiftFrame);
            Assert.Equal(ShiftStats.Ending.Complete, stats.Last.End);
        }

        // A shift whose step ran but whose step frame's length had not come yet when the next shift
        // came: the next renderer update brings it, and the shift is complete, its step frame its
        // own and not a usual one.
        [Fact]
        public void AShiftCutShortJustBeforeItsStepFrameIsCompleteWithIt()
        {
            var stats = new ShiftStats();
            for (int i = 0; i < 5; i++) Frame(stats, 1.0, 0.5, 16.7);

            stats.Shifted(10.0, 0.1, 9.9, 3);                       // frame F
            stats.Rendered(4.0, 16.7);
            Frame(stats, 8.0, 0.5, 30.0);                           // F+1 ran the step; F was 30 ms
            stats.Stepped(1.0);                                     // F+2: an ordinary step, then the next shift
            Assert.False(stats.Shifted(11.0, 0.1, 10.9, 3));
            Assert.True(stats.Rendered(4.5, 25.0));                 // F+1 was 25 ms

            ShiftStats.Shift s = stats.Last;
            Assert.Equal(1, s.Number);
            Assert.Equal(ShiftStats.Ending.Complete, s.End);
            Assert.Equal(8.0, s.Step);
            Assert.Equal(25.0, s.StepFrame);
            Assert.Equal(16.7, s.UsualFrame);
            Assert.True(stats.Following);                           // the second
        }

        // When the physics step cannot be timed, a shift is finished at its own frame's length with
        // the step n/a, and does not wait thirty frames and read like a paused game.
        [Fact]
        public void WithStepsNotTimedAShiftDoesNotWaitForOne()
        {
            var stats = new ShiftStats(stepsTimed: false);
            stats.Shifted(10.0, 0.1, 9.9, 3);
            Assert.False(stats.Rendered(4.0, 16.7));
            Assert.True(stats.Rendered(0.5, 30.0));

            ShiftStats.Shift s = stats.Last;
            Assert.Equal(ShiftStats.Ending.Complete, s.End);
            Assert.Equal(30.0, s.ShiftFrame);
            string line = ShiftStats.Describe(s);
            Assert.Contains("next physics step n/a ms (usual n/a); shift frame 30.00 ms, step frame n/a ms", line);
            Assert.DoesNotContain("within", line);
        }

        [Fact]
        public void FlushFinishesTheShiftStillFollowed()
        {
            var stats = new ShiftStats();
            Assert.False(stats.Flush());

            stats.Shifted(10.0, 0.1, 9.9, 3);
            stats.Rendered(4.0, 16.7);
            Assert.True(stats.Flush());
            Assert.False(stats.Following);
            Assert.Equal(1, stats.Count);
            Assert.Equal(ShiftStats.Ending.Stopped, stats.Last.End);
            Assert.True(stats.HasWindow);
            Assert.False(stats.Flush());
        }

        // A shift cut short, by the next shift or by the timing stopping, has no step for another
        // reason than a paused game, and its line says which, not "no physics step within 30 frames"
        // after the one or two frames it waited.
        [Fact]
        public void AShiftCutShortSaysWhyAndNotThatItTimedOut()
        {
            var stats = new ShiftStats();
            for (int i = 0; i < 5; i++) Frame(stats, 1.2, 0.91, 8.3);

            stats.Shifted(14.0, 0.2, 13.8, 5);                     // two shifts in one frame
            Assert.False(stats.Shifted(13.0, 0.2, 12.8, 5));
            Assert.True(stats.Rendered(6.0, 8.3));                  // the second's frame finishes the first
            string first = ShiftStats.Describe(stats.Last);
            Assert.EndsWith("renderer update n/a ms (usual 0.91); next physics step n/a ms (usual 1.20); " +
                            "shift frame n/a ms, step frame n/a ms (usual 8.30); cut short by the next shift", first);
            Assert.DoesNotContain("within", first);

            Assert.True(stats.Flush());                             // then the map unloads
            string second = ShiftStats.Describe(stats.Last);
            Assert.EndsWith("renderer update 6.00 ms (usual 0.91); next physics step n/a ms (usual 1.20); " +
                            "shift frame n/a ms, step frame n/a ms (usual 8.30); cut short as the timing stopped", second);
            Assert.DoesNotContain("within", second);
        }

        static void CompleteShift(ShiftStats stats, double total)
        {
            stats.Shifted(total, 0.5, total - 0.5, 2);
            stats.Rendered(6.0, 16.0);
            Frame(stats, 8.0, 1.0, 40.0);
            Assert.True(Frame(stats, 1.0, 1.0, 24.0));
        }

        // One summary every Window shifts, over those shifts only.
        [Fact]
        public void ASummaryIsDueEveryWindowAndCoversItsShifts()
        {
            var stats = new ShiftStats();
            for (int i = 1; i < ShiftStats.Window; i++)
            {
                CompleteShift(stats, i);
                Assert.False(stats.SummaryDue(10.0));
            }
            CompleteShift(stats, ShiftStats.Window);
            Assert.True(stats.SummaryDue(10.0));

            string first = stats.Summarise(10.0);
            Assert.StartsWith($"origin shifts 1-{ShiftStats.Window}, mean (max) ms: shift 10.50 (20.00), SyncTransforms 10.00 (19.50); ", first);
            Assert.Contains("next physics step 8.00 (8.00), usual 1.00", first);
            Assert.False(stats.SummaryDue(10.0));
            Assert.False(stats.HasWindow);

            for (int i = 0; i < ShiftStats.Window; i++) CompleteShift(stats, 100.0);
            string second = stats.Summarise(60.0);
            Assert.StartsWith($"origin shifts {ShiftStats.Window + 1}-{2 * ShiftStats.Window}, mean (max) ms: shift 100.00 (100.00)", second);
        }

        // Shifts every few frames, as under a free camera racing across the map: a summary no more
        // often than every SummaryInterval seconds, the window growing meanwhile.
        [Fact]
        public void SummariesComeNoOftenerThanTheInterval()
        {
            var stats = new ShiftStats();
            for (int i = 0; i < ShiftStats.Window; i++) CompleteShift(stats, 10.0);
            Assert.True(stats.SummaryDue(5.0));
            stats.Summarise(5.0);

            for (int i = 0; i < 2 * ShiftStats.Window; i++) CompleteShift(stats, 10.0);
            Assert.False(stats.SummaryDue(5.0 + ShiftStats.SummaryInterval - 1.0));
            Assert.True(stats.SummaryDue(5.0 + ShiftStats.SummaryInterval));
            Assert.StartsWith($"origin shifts {ShiftStats.Window + 1}-{3 * ShiftStats.Window},",
                              stats.Summarise(5.0 + ShiftStats.SummaryInterval));
        }

        // A figure some shifts lack (SyncTransforms not timed apart, no step while paused) says how
        // many it covers, so a mean over two is not read as one over twenty.
        [Fact]
        public void AFigureMeasuredForFewerShiftsSaysForHowMany()
        {
            var stats = new ShiftStats();
            CompleteShift(stats, 10.0);
            stats.Shifted(12.0, double.NaN, double.NaN, -1);
            stats.Rendered(6.0, 16.0);
            Frame(stats, 8.0, 1.0, 40.0);
            Frame(stats, 1.0, 1.0, 24.0);

            string summary = stats.Summarise(0.0);
            Assert.Contains("shift 11.00 (12.00), SyncTransforms 9.50 (9.50) in 1 of 2;", summary);
        }

        // A figure none of the window's shifts had (SyncTransforms never timed apart when the
        // transpiler stood down, no step in a paused game) has no largest either: n/a (n/a), not a
        // maximum of 0.00 that was never measured.
        [Fact]
        public void AFigureMeasuredForNoShiftHasNoMaximum()
        {
            var stats = new ShiftStats();
            for (int i = 0; i < 3; i++)
            {
                stats.Shifted(12.0, double.NaN, double.NaN, -1);
                for (int f = 0; f <= ShiftStats.StepTimeout; f++) stats.Rendered(6.0, 16.0);   // paused: no step
            }

            string summary = stats.Summarise(0.0);
            Assert.Contains("shift 12.00 (12.00), SyncTransforms n/a (n/a) in 0 of 3;", summary);
            Assert.Contains("next physics step n/a (n/a) in 0 of 3", summary);
            Assert.Contains("step frame n/a (n/a) in 0 of 3", summary);
            Assert.DoesNotContain("(0.00)", summary);
        }

        // The words the README explains, pinned.
        [Fact]
        public void TheShiftsLineReadsAsDocumented()
        {
            var stats = new ShiftStats();
            for (int i = 0; i < 5; i++) Frame(stats, 1.2, 0.91, 8.3);
            stats.Shifted(14.21, 1.1, 13.11, 37);
            stats.Rendered(6.3, 8.3);
            Frame(stats, 8.6, 0.91, 31.5);
            Frame(stats, 1.2, 0.91, 18.0);

            Assert.Equal("origin shift 1: 14.21 ms (roots 1.10 ms, SyncTransforms 13.11 ms; 37 roots); " +
                         "renderer update 6.30 ms (usual 0.91); next physics step 8.60 ms (usual 1.20); " +
                         "shift frame 31.50 ms, step frame 18.00 ms (usual 8.30)",
                         ShiftStats.Describe(stats.Last));
        }

        // A log pasted from a German or French install must read the same as one from here.
        [Fact]
        public void FiguresUseAPointWhateverTheLocale()
        {
            CultureInfo before = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");
                var stats = new ShiftStats();
                CompleteShift(stats, 1234.5);

                Assert.Contains("origin shift 1: 1234.50 ms", ShiftStats.Describe(stats.Last));
                Assert.Contains("shift 1234.50 (1234.50)", stats.Summarise(0.0));
            }
            finally
            {
                CultureInfo.CurrentCulture = before;
            }
        }

        // Unmeasured parts read n/a rather than 0 or NaN.
        [Fact]
        public void WhatWasNotMeasuredReadsNotApplicable()
        {
            var stats = new ShiftStats();
            stats.Shifted(14.0, double.NaN, double.NaN, -1);
            stats.Rendered(double.NaN, 16.0);
            Frame(stats, 8.0, double.NaN, 30.0);
            Frame(stats, null, double.NaN, 20.0);

            string line = ShiftStats.Describe(stats.Last);
            Assert.StartsWith("origin shift 1: 14.00 ms (roots n/a ms, SyncTransforms n/a ms); renderer update n/a ms (usual n/a); ", line);
            Assert.DoesNotContain("NaN", line);
        }
    }
}
