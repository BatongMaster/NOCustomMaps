using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace CustomMaps.Patches
{
    /// <summary>
    /// Keeps a landing gear from rolling on a lake as if it were tarmac.
    ///
    /// A raised lake's water is trigger boxes carrying the game's water PhysicMaterial
    /// (<see cref="MapFixups"/>, <c>BindLakeVolumes</c>): what makes an aircraft that flies into one
    /// ditch, as it would at sea. A landing gear is one line-cast per wheel, in
    /// <c>LandingGear.FixedUpdate</c>:
    ///
    ///     Physics.Linecast(castPoint.position, castPoint.position - castPoint.up * suspensionTravel,
    ///                      out hit, ~(ExclusionZonesMask | IgnoreCollisionsMask))
    ///
    /// the four-argument overload, whose trigger rule is the global one, and the game ships with
    /// <c>queriesHitTriggers</c> on. So the wheel finds the lake's top face, calls it tarmac
    /// (<c>onTarmac</c> is anything that is not the terrain material), and the aircraft lands and
    /// taxis on the water. At sea there is nothing for the ray to meet and it ditches. The call is
    /// swapped here for the same line-cast with <c>QueryTriggerInteraction.Ignore</c>, so the wheel
    /// meets only solid ground: a lake's bed, if the aircraft is already that deep in it.
    ///
    /// A transpiler, and one call, not <c>Physics.queriesHitTriggers</c> switched off around the
    /// method: that setting is global, and the ground vehicles' <c>RaycastCommand</c> jobs, which
    /// run on worker threads while the main thread carries on, read it when they execute, so a
    /// prefix and postfix toggling it would change their answers at random.
    ///
    /// It applies on every map, the shipped ones included, which is safe only because no trigger
    /// the base game ships is anything a wheel should stand on. Checked on 27 September 2026
    /// against the decompiled game and a survey of the colliders in its assets: the gear's mask
    /// already leaves out layers 13 (ExclusionZones) and 15 (IgnoreCollisions), which hold fourteen
    /// of the game's fifteen triggers; the one left is a propeller's disc on layer 0, a trigger only
    /// while it spins (<c>ConstantSpeedProp</c> makes it solid once the blades stop), and no wheel
    /// is meant to roll on a spinning propeller. No code in the game makes a trigger at runtime.
    ///
    /// Fails safe: if the method no longer makes exactly that one call, the method is left as it
    /// is and a warning says so. Registered as non-critical, so a game update that moves the
    /// method costs this and nothing else.
    /// </summary>
    [HarmonyPatch(typeof(LandingGear), "FixedUpdate")]
    internal static class LandingGearTriggerPatch
    {
        /// <summary><c>Physics.Linecast(Vector3, Vector3, out RaycastHit, int)</c>, the call the IL
        /// makes (<c>call bool UnityEngine.Physics::Linecast(Vector3, Vector3, RaycastHit&amp;,
        /// int32)</c>, the only call into <c>Physics</c> in the method).</summary>
        static readonly MethodInfo GameLinecast = AccessTools.Method(typeof(Physics), nameof(Physics.Linecast),
            new[] { typeof(Vector3), typeof(Vector3), typeof(RaycastHit).MakeByRefType(), typeof(int) });

        static readonly MethodInfo IgnoringTriggers = AccessTools.Method(typeof(LandingGearTriggerPatch),
            nameof(LinecastIgnoringTriggers));

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);

            var calls = new List<int>();
            if (GameLinecast != null)
                for (int i = 0; i < code.Count; i++)
                    if (code[i].Calls(GameLinecast)) calls.Add(i);

            if (calls.Count != 1 || IgnoringTriggers == null)
            {
                Plugin.LogWarning($"LandingGear.FixedUpdate makes {calls.Count} call(s) to Physics.Linecast(Vector3, Vector3, " +
                                  "out RaycastHit, int) where one was expected, so it is left as it is: landing gear " +
                                  "will roll on a custom map's lakes as on tarmac");
                return code;
            }

            // Same arguments, same stack, same result: only the trigger rule differs.
            code[calls[0]].operand = IgnoringTriggers;
            Plugin.LogDebug("LandingGear.FixedUpdate: the wheel's line-cast now ignores triggers, so gear meets no lake");
            return code;
        }

        static bool LinecastIgnoringTriggers(Vector3 start, Vector3 end, out RaycastHit hitInfo, int layerMask)
            => Physics.Linecast(start, end, out hitInfo, layerMask, QueryTriggerInteraction.Ignore);
    }
}
