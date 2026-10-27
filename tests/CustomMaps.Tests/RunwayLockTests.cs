using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace CustomMaps.Tests
{
    /// <summary>
    /// One aircraft at a time on a custom map's runway strip (<see cref="RunwayLock"/>): which runway objects
    /// are one strip, when a take-off or a landing still holds it, and when an approach goes around.
    /// </summary>
    public class RunwayLockTests
    {
        readonly ITestOutputHelper _output;

        public RunwayLockTests(ITestOutputHelper output) => _output = output;

        static RunwayLine Line(float sx, float sz, float ex, float ez, bool landing = true)
            => new RunwayLine { StartX = sx, StartZ = sz, EndX = ex, EndZ = ez, Landing = landing };

        static List<int>[] Mates(List<RunwayLine> lines, params float[] widths) => RunwayLock.StripMates(lines, widths);

        /// <summary>A runway, its two take-off starts, a runway crossing it and one beside it: the starts
        /// and their runway are one strip, the others each a strip of their own.</summary>
        [Fact]
        public void ARunwayAndItsStartsAreOneStripAndOthersAreNot()
        {
            var lines = new List<RunwayLine>
            {
                Line(-1000f, 0f, 1000f, 0f),                // 0: the main runway, east
                Line(500f, -800f, 500f, 800f),              // 1: crosses it
                Line(-400f, 0f, 1000f, 0f, landing: false), // 2: a start on 0, taking off east
                Line(300f, 0f, -1000f, 0f, landing: false), // 3: a start on 0, taking off west
                Line(-1000f, 300f, 1000f, 300f),            // 4: parallel, 300 m north
            };

            List<int>[] mates = Mates(lines, 45f, 30f, 45f, 45f, 45f);
            Assert.Equal(new[] { 2, 3 }, mates[0]);
            Assert.Empty(mates[1]);
            Assert.Equal(new[] { 0, 3 }, mates[2]);
            Assert.Equal(new[] { 0, 2 }, mates[3]);
            Assert.Empty(mates[4]);
        }

        /// <summary>Two runways laid over one another are one strip, with each other's take-off starts;
        /// end to end, side by side or turned away they are not.</summary>
        [Fact]
        public void RunwaysLaidOverOneAnotherAreOneStrip()
        {
            var lines = new List<RunwayLine>
            {
                Line(-1000f, 0f, 1000f, 0f),                // 0
                Line(800f, 5f, -200f, 5f),                  // 1: over 0, the other way, 5 m off its centreline
                Line(0f, 5f, 800f, 5f, landing: false),     // 2: a take-off start on 1
            };

            List<int>[] mates = Mates(lines, 45f, 45f, 45f);
            Assert.Equal(new[] { 1, 2 }, mates[0]);
            Assert.Equal(new[] { 0, 2 }, mates[1]);
            Assert.Equal(new[] { 0, 1 }, mates[2]);

            RunwayLine main = lines[0];
            Assert.False(RunwayLock.OneStrip(main, 45f, Line(1000f, 0f, 2000f, 0f), 45f));     // end to end
            Assert.False(RunwayLock.OneStrip(main, 45f, Line(-1000f, 40f, 1000f, 40f), 45f));  // side by side
            Assert.False(RunwayLock.OneStrip(main, 45f, Line(-1000f, 0f, 1000f, 200f), 45f));  // turned 6 degrees
            Assert.True(RunwayLock.OneStrip(main, 45f, Line(-1000f, 0f, 1000f, 20f), 45f));    // half a degree
            Assert.False(RunwayLock.OneStrip(main, 45f, Line(float.NaN, 0f, 1000f, 0f), 45f));  // nowhere
            Assert.False(RunwayLock.OneStrip(main, 45f, Line(0f, 0f, 0.5f, 0f), 45f));          // a point
        }

        /// <summary>An airbase whose runways each have a strip of their own shares nothing.</summary>
        [Fact]
        public void RunwaysOfTheirOwnShareNothing()
        {
            var lines = new List<RunwayLine> { Line(-1000f, 0f, 1000f, 0f), Line(500f, -800f, 500f, 800f) };
            Assert.All(Mates(lines, 45f, 30f), m => Assert.Empty(m));
            Assert.All(RunwayLock.StripMates(lines, null), m => Assert.Empty(m));
            Assert.Empty(RunwayLock.StripMates(null, null));
        }

        /// <summary>A head holds its strip while taxiing to its start and while lined up or rolling, and
        /// not once gone, off the ground, or out of the taxi and take-off states (parked after an aborted
        /// take-off, flying away from another runway object than the one it queued on).</summary>
        [Fact]
        public void AHeadHoldsItsStripUntilItIsOffTheGroundOrGone()
        {
            Assert.Equal(RunwayLock.Head.Gone, RunwayLock.HeadOf(true, true, true, false, 0f));
            Assert.Equal(RunwayLock.Head.Taxiing, RunwayLock.HeadOf(false, true, true, false, 0f));
            Assert.Equal(RunwayLock.Head.OnRunway, RunwayLock.HeadOf(false, true, false, true, 0.5f));
            Assert.Equal(RunwayLock.Head.Airborne, RunwayLock.HeadOf(false, true, false, true, 1.5f));
            Assert.Equal(RunwayLock.Head.Elsewhere, RunwayLock.HeadOf(false, true, false, false, 0f));
            Assert.Equal(RunwayLock.Head.Unknown, RunwayLock.HeadOf(false, false, false, false, 0f));

            Assert.True(RunwayLock.Holds(RunwayLock.Head.Taxiing));
            Assert.True(RunwayLock.Holds(RunwayLock.Head.OnRunway));
            Assert.True(RunwayLock.Holds(RunwayLock.Head.Unknown));
            Assert.False(RunwayLock.Holds(RunwayLock.Head.Gone));
            Assert.False(RunwayLock.Holds(RunwayLock.Head.Airborne));
            Assert.False(RunwayLock.Holds(RunwayLock.Head.Elsewhere));
        }

        /// <summary>An AI keeps its landing while landing or on the runway; a player while in the air or on
        /// the runway; nothing gone keeps one.</summary>
        [Fact]
        public void ALandingHoldsTheStripWhileItIsLandingOrOnTheRunway()
        {
            // AI: gone, landing, rolled out on the runway, taxiing away, broke off for a missile.
            Assert.False(RunwayLock.LanderHolds(true, false, true, true, 50f));
            Assert.True(RunwayLock.LanderHolds(false, false, true, false, 300f));
            Assert.True(RunwayLock.LanderHolds(false, false, false, true, 0f));
            Assert.False(RunwayLock.LanderHolds(false, false, false, false, 0f));
            Assert.False(RunwayLock.LanderHolds(false, false, false, false, 400f));

            // Player: on approach, on the runway, landed and off it.
            Assert.True(RunwayLock.LanderHolds(false, true, false, false, 300f));
            Assert.True(RunwayLock.LanderHolds(false, true, false, true, 0f));
            Assert.False(RunwayLock.LanderHolds(false, true, false, false, 0f));
        }

        /// <summary>The head goes only while nobody else lands on, lines up on or rolls along its strip;
        /// an approach goes around only near touchdown with a take-off on the runway.</summary>
        [Fact]
        public void TheHeadWaitsForTheStripAndAnApproachGoesAroundNearTouchdown()
        {
            Assert.True(RunwayLock.HeadMayGo(false, false));
            Assert.False(RunwayLock.HeadMayGo(true, false));
            Assert.False(RunwayLock.HeadMayGo(false, true));

            Assert.True(RunwayLock.GoAround(true, true, 1200f));
            Assert.True(RunwayLock.GoAround(true, true, RunwayLock.GoAroundDistance));
            Assert.False(RunwayLock.GoAround(true, true, 2000f));
            Assert.False(RunwayLock.GoAround(true, false, 300f));
            Assert.False(RunwayLock.GoAround(false, true, 300f));
            Assert.False(RunwayLock.GoAround(true, true, float.NaN));
        }

        /// <summary>
        /// The game's take-off queue and AI approach, read from its DLL, are the ones the lock was checked
        /// against (<see cref="RunwayLock.VerifiedQueueMethods"/>, <see cref="RunwayLock.VerifiedApproachMethods"/>).
        /// After a game update that changes them it fails until someone has checked the lock again; passes
        /// without checking anything where the game's DLL is not found.
        /// </summary>
        [Fact]
        public void TheInstalledGameHasTheQueueAndApproachThisWasCheckedAgainst()
        {
            string dll = IlFingerprintTests.GameAssembly();
            if (dll == null)
            {
                _output.WriteLine("Assembly-CSharp.dll not found; nothing checked");
                return;
            }

            ulong Print(string[][] methods)
                => IlFingerprint.OfAll(methods.Select(m => IlFingerprintTests.GameMethodPrint(dll, m[0], m[1], m[2])));

            ulong queue = Print(RunwayLock.QueueMethods), approach = Print(RunwayLock.ApproachMethods);
            _output.WriteLine($"{dll}: queue {queue:X16}, approach {approach:X16}");

            Assert.True(queue == RunwayLock.VerifiedQueueMethods,
                $"The game's take-off queue has changed (fingerprint 0x{queue:X16}UL). Decompile the methods in " +
                "RunwayLock.QueueMethods (NOMapForge/tools/GameProbe), check the lock still holds, then set " +
                "RunwayLock.VerifiedQueueMethods to that value.");
            Assert.True(approach == RunwayLock.VerifiedApproachMethods,
                $"The game's AI approach has changed (fingerprint 0x{approach:X16}UL). Decompile the methods in " +
                "RunwayLock.ApproachMethods, check the go-around still holds, then set RunwayLock.VerifiedApproachMethods " +
                "to that value.");
        }
    }
}
