using System.Collections.Generic;
using HarmonyLib;

namespace CustomMaps.Patches
{
    /// <summary>
    /// Has a custom map's runway and the take-off starts on it (<see cref="AirbaseBuilder"/>,
    /// <c>LayOutTakeoffStarts</c>) share their strip's traffic, as runways that cross do.
    ///
    /// Each runway keeps its own landing list and take-off queue, and its take-off clearance
    /// (<c>Runway.IsAvailableForTakeoff</c>, asked by the taxiing AI within 100 m of where it lines up)
    /// looks past its own only at its <c>crossingRunways</c>: no aircraft of theirs landing, and none at
    /// the head of their take-off queue. <c>FindCrossingRunways</c>, run on every runway when the
    /// airbase starts on the server, finds those by where two runways' lines pass within a runway's width
    /// of each other (<c>CrossesRunway</c>, through <c>TargetCalc.ClosestPointsOnTwoLines</c>), which for
    /// two runways on one line is no answer at all: exactly parallel it gives up, and a hair off parallel
    /// it is float rounding. The base game's own pair on one strip, Ignus' runway 14L and its
    /// <c>runway14LTakeoffShort</c>, is left to that rounding; worked through in single precision on their
    /// thresholds, it puts them 84 m apart, not crossing. Unlinked, an aircraft would be cleared to line
    /// up at a take-off start while another landed on the runway it lies on, or took off along it.
    ///
    /// So after the game's own search, every runway of a custom map's airbase that shares a strip with a
    /// take-off start has the others on that strip added to it (<see cref="TaxiPlans.SharedStrips"/>):
    /// the runway its take-off starts, each take-off start its runway and its runway's other take-off
    /// starts. On an airbase set to one aircraft per runway (<see cref="RunwayLocks.Locked"/>) runways
    /// laid over one another are linked too, with each other's starts (<see cref="RunwayLock.StripMates"/>),
    /// and the strip is kept for the lock, which asks the whole strip where the game asks one runway. Told
    /// by where they lie, so the airbase's own instance is read, not a list kept on the prefab. A runway
    /// the game already found is not added twice. Airbases of the base maps and those a mission makes are
    /// left as the game has them, and so is any custom airbase whose runways each have a strip of their
    /// own.
    /// </summary>
    [HarmonyPatch(typeof(Airbase.Runway), nameof(Airbase.Runway.FindCrossingRunways), new System.Type[0])]
    internal static class TakeoffStripPatch
    {
        static bool Prepare()
            => AccessTools.Field(typeof(Airbase.Runway), "crossingRunways")?.FieldType == typeof(List<Airbase.Runway>);

        static void Postfix(Airbase.Runway __instance)
        {
            Airbase airbase = __instance?.airbase;
            Airbase.Runway[] runways = airbase != null ? airbase.runways : null;
            if (runways == null || runways.Length < 2 || airbase.GetComponent<CustomMapAirbase>() == null) return;

            int self = System.Array.IndexOf(runways, __instance);
            if (self < 0) return;

            var lines = new List<RunwayLine>(runways.Length);
            var widths = new List<float>(runways.Length);
            foreach (Airbase.Runway runway in runways)
            {
                if (runway?.Start == null || runway.End == null)
                {
                    // Nowhere: shares no strip, and keeps the others' indices where they are.
                    lines.Add(new RunwayLine { StartX = float.NaN, StartZ = float.NaN, EndX = float.NaN, EndZ = float.NaN, Landing = true });
                    widths.Add(0f);
                    continue;
                }

                lines.Add(new RunwayLine
                {
                    StartX = runway.Start.position.x, StartZ = runway.Start.position.z,
                    EndX = runway.End.position.x, EndZ = runway.End.position.z,
                    Landing = runway.Landing,
                });
                widths.Add(runway.GetWidth());
            }

            bool locked = RunwayLocks.Locked(__instance);
            List<int> shared = locked ? RunwayLock.StripMates(lines, widths)[self] : TaxiPlans.SharedStrips(lines)[self];
            var mates = new Airbase.Runway[shared.Count];
            for (int k = 0; k < shared.Count; k++) mates[k] = runways[shared[k]];
            RunwayLocks.SetMates(__instance, locked ? mates : null);
            if (shared.Count == 0) return;

            List<Airbase.Runway> crossing = __instance.crossingRunways ?? (__instance.crossingRunways = new List<Airbase.Runway>());
            foreach (Airbase.Runway mate in mates)
                if (!crossing.Contains(mate)) crossing.Add(mate);
        }
    }
}
