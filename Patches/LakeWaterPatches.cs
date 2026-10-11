using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace CustomMaps.Patches
{
    /// <summary>What a patched method does with one of its sea-level reads.</summary>
    internal enum Water
    {
        /// <summary>Left as it is: the read is an altitude, not water (air density, thrust lapse).</summary>
        Keep,

        /// <summary>The water level under the vector whose x or y the method reads just before:
        /// <c>position.y &lt; Datum.LocalSeaY</c>, <c>new Vector3(p.x, Datum.LocalSeaY, p.z)</c>.</summary>
        Before,

        /// <summary>The water level under the vector the read is stored into:
        /// <c>position.y = Datum.LocalSeaY</c>.</summary>
        Into,

        /// <summary>The water level under the vector the method reads just after, through a chain of
        /// getters: <c>Datum.LocalSeaY - suspendedUnit.transform.position.y</c>.</summary>
        After,

        /// <summary>The water level under the local vector whose y the method compares the read with
        /// next: <c>float floor = Datum.LocalSeaY + 1.7f; if (position.y &lt; floor)</c>.</summary>
        NextLocal,

        /// <summary>The level the previous site in the method worked out, for a second read at the
        /// same x and z.</summary>
        Same,

        /// <summary>For <c>GlobalY()</c> or a <c>GlobalPosition</c>'s y used as the height above the
        /// sea: the height above the water.</summary>
        Height,

        /// <summary>For a <c>GlobalY()</c> that is both a test against the sea and an altitude: the
        /// value kept, its comparison with 0 made against the water.</summary>
        CompareOnly,
    }

    /// <summary>
    /// One game method whose sea-level reads are pointed at <see cref="LakeWater"/>, and what each
    /// read becomes. Each kind of read is listed in the order the method's IL makes it, and the
    /// count is part of the contract: a method that no longer makes exactly these reads is left as
    /// it is. A kind given as null is not looked at.
    /// </summary>
    internal sealed class WaterTarget
    {
        /// <summary>Type (Assembly-CSharp full name, nested types after '+') and method, as the log
        /// names it.</summary>
        public readonly string TypeName, MethodName;

        /// <summary>What the game does there with water, for the log.</summary>
        public readonly string Purpose;

        /// <summary><c>Datum.LocalSeaY</c> and <c>Datum.origin.position.y</c> reads.</summary>
        public Water[] Sea;

        /// <summary><c>GlobalPositionExtensions.GlobalY(Vector3)</c> calls.</summary>
        public Water[] GlobalY;

        /// <summary><c>GlobalPosition.y</c> loads.</summary>
        public Water[] GlobalPositionY;

        /// <summary>A literal height (<see cref="Literal"/>) loaded straight after a vector's x or y:
        /// the sea's global 0 written as a number.</summary>
        public Water[] Literals;
        public float Literal;

        /// <summary>The literal is a local height off the lakes rather than the sea's global one, so
        /// it is replaced only over a lake (<see cref="LakeWater.LakeLocalYOr(Vector3, float)"/>).</summary>
        public bool LiteralOnlyOverLakes;

        /// <summary><c>Plane.Raycast</c> calls on the sea's plane, all of which are pointed at
        /// <see cref="LakeWater.Raycast"/>.</summary>
        public int Raycasts;

        /// <summary>Set by the transpiler: whether the method now reads the lakes.</summary>
        public bool Applied;

        public MethodBase Method;

        public WaterTarget(string typeName, string methodName, string purpose)
        {
            TypeName = typeName;
            MethodName = methodName;
            Purpose = purpose;
        }

        public string Name => $"{TypeName.Substring(TypeName.LastIndexOf('.') + 1)}.{MethodName}";

        public WaterTarget Reads(params Water[] sea) { Sea = sea; return this; }
        public WaterTarget Globals(params Water[] globalY) { GlobalY = globalY; return this; }
        public WaterTarget Positions(params Water[] globalPositionY) { GlobalPositionY = globalPositionY; return this; }
        public WaterTarget Rays(int raycasts) { Raycasts = raycasts; return this; }

        public WaterTarget Literals0(float literal, bool onlyOverLakes, params Water[] literals)
        {
            Literal = literal;
            LiteralOnlyOverLakes = onlyOverLakes;
            Literals = literals;
            return this;
        }

        /// <summary>The method, from Assembly-CSharp by name; a compiler-made state machine
        /// (<c>&lt;Name&gt;d__N</c>, whose number a game update may change) by its name's start.</summary>
        public MethodBase Resolve()
        {
            Assembly game = typeof(Datum).Assembly;
            string[] path = TypeName.Split(new[] { '+' });
            Type type = game.GetType(path[0], false);
            for (int i = 1; type != null && i < path.Length; i++)
            {
                string nested = path[i];
                type = nested.EndsWith(">", StringComparison.Ordinal)
                    ? type.GetNestedTypes(AccessTools.all).FirstOrDefault(t => t.Name.StartsWith(nested, StringComparison.Ordinal))
                    : type.GetNestedType(nested, AccessTools.all);
            }

            if (type == null) return null;

            MethodInfo[] candidates = type.GetMethods(AccessTools.allDeclared).Where(m => m.Name == MethodName).ToArray();
            return candidates.Length == 1 ? candidates[0] : null;
        }
    }

    /// <summary>
    /// Makes the game's water tests see a custom map's raised lakes.
    ///
    /// Nearly everything Nuclear Option does with water compares a height with the sea's global
    /// y = 0 (see <see cref="LakeWater"/>). This rewrites those comparisons, method by method, to
    /// ask <see cref="LakeWater"/> for the water level at the point being tested, which is the
    /// sea's own everywhere but over a lake. What stays on the sea, and why:
    ///
    /// - Altitude, not water: air density and the speed of sound, a turbojet's thrust lapse, the
    ///   radar horizon, the HUD and map altitude readouts, the clouds, the ocean's render plane,
    ///   the ballistic shells' fall-to-sea-level aim, the escape capsule's 3,000 m chute.
    /// - Ground vehicles: the user decided on 27 September 2026 that they stay on a lake's bed, so
    ///   their own ground and water checks (<c>GroundVehicleJob_Math1</c>, <c>GroundVehicle</c>,
    ///   <c>PathfindingAgent</c>) are not touched.
    /// - Ships, hovercraft and landing craft, and the mission editor placing units on the water:
    ///   boats on lakes are a later feature, which will use the same lookup.
    ///
    /// The aircraft's own water model is a Burst job (<c>AeroJob_Math</c>), which Harmony cannot
    /// patch; it reads a part's depth from a field the part fills before the job runs, and
    /// <see cref="AeroPartLakePatch"/> fills it with the lake's depth by the job's own formula.
    ///
    /// Applied once, at start-up, and only when an installed map has raised lakes, so a game
    /// without one runs its own code untouched. Each method is patched on its own and fails safe:
    /// if its IL is not what this was written against (a game update), it is left as it is, a
    /// warning names it, and only that behaviour stays on the sea.
    /// </summary>
    internal static class LakeWaterPatches
    {
        static readonly Dictionary<MethodBase, WaterTarget> _targets = new Dictionary<MethodBase, WaterTarget>();
        static bool _attempted, _aeroApplied;
        static int _total;

        /// <summary>
        /// How many of the game's water tests read the lakes now. Counted from the targets each time it
        /// is asked rather than once: Harmony runs a method's transpilers again whenever anything else
        /// patches that method later (another plugin, say), and a rewrite refused then leaves the method
        /// on the sea, which the validation line and the map-load line should say.
        /// </summary>
        static int Patched
        {
            get
            {
                int patched = _aeroApplied ? 1 : 0;
                foreach (WaterTarget target in _targets.Values)
                    if (target.Applied) patched++;
                return patched;
            }
        }

        /// <summary>For the log: how many of the game's water tests now see the lakes.</summary>
        public static string Summary => !_attempted
            ? "the game's water tests are not patched"
            : $"{Patched} of {_total} water tests patched";

        public static bool AllApplied => _attempted && _total > 0 && Patched == _total;

        /// <summary>
        /// Every game method that reacts to water, and what each of its sea-level reads becomes.
        /// The reads were found by scanning the IL of the whole assembly for <c>Datum.LocalSeaY</c>,
        /// <c>Datum.origin.position.y</c>, <c>GlobalY()</c>, <c>Datum.WaterPlane()</c> and literal
        /// heights against a global y, and each was read in the decompiled method (game build of
        /// 14 August 2026).
        /// </summary>
        internal static List<WaterTarget> Targets()
        {
            const Water K = Water.Keep, B = Water.Before, I = Water.Into, A = Water.After, N = Water.NextLocal,
                        S = Water.Same, H = Water.Height, C = Water.CompareOnly;

            return new List<WaterTarget>
            {
                // --- aircraft in the water ---------------------------------------------------
                new WaterTarget("AeroPart", "ApplyJobFields",
                    "a ditching part's splash: on the water's surface, unless its line-cast meets ground above it").Reads(B, I),
                new WaterTarget("AeroPart", "OnCollisionEnter", "impact dust only above the water").Reads(B),
                new WaterTarget("AeroPart", "OnCollisionStay", "contact dust and sparks only above the water").Reads(B),
                new WaterTarget("Pilot", "Pilot_OnAeroInputsApplied", "a pilot drowns 10 m under the water").Reads(B),
                new WaterTarget("Turbofan", "FixedUpdate", "a running turbofan is destroyed under the water").Reads(B),
                new WaterTarget("Turbojet", "FixedUpdate",
                    "a running turbojet dies under the water (its thrust lapse with altitude stays on the sea)").Reads(B, K),
                new WaterTarget("TurbineEngine", "Update", "a running turbine dies under the water").Reads(B),
                new WaterTarget("TurbineEngine", "KillEngine", "no failure fire under the water").Reads(B),
                new WaterTarget("FuelTank", "IgnitionEffects", "no fuel fire under the water").Reads(B),
                new WaterTarget("FuelTank", "Fireball", "no fireball under the water").Reads(B),
                new WaterTarget("FuelTank", "FixedUpdate", "a tank ruptures on impact only above the water").Reads(B),
                new WaterTarget("FuelTank+<FuelTankFire>", "MoveNext", "a fuel fire goes out under the water").Reads(B),
                new WaterTarget("UnitPart", "SpawnFragments",
                    "a part breaking up under the water splashes on its surface instead of burning").Reads(B, I),
                new WaterTarget("DamageParticles", "SlowUpdate", "smoke and fire stop under the water").Reads(B, B),
                new WaterTarget("DamageParticles", "Update", "an oil slick rides the water's surface").Reads(B),
                new WaterTarget("Aircraft+<EjectionSequence>", "MoveNext",
                    "an abandoned aircraft near an airbase is returned only if it is above the water").Reads(B),
                new WaterTarget("Hangar", "IsFunctional", "a hangar works only above the water").Reads(B),
                new WaterTarget("WarheadStorage", "IsFunctional", "a warhead store works only above the water").Reads(B),
                new WaterTarget("Building", "CheckUnderground", "a submerged building's depth under the water").Reads(B, B),
                new WaterTarget("NuclearOption.NetworkTransforms.AircraftNetworkTransform", "ProcessNextClientAuth",
                    "a client loses authority over its aircraft 10 m under the water").Literals0(-10f, false, B),

                // --- altimeters, warnings, AI ------------------------------------------------
                new WaterTarget("Unit", "CheckRadarAlt", "radar altitude above the water").Globals(H, H),
                new WaterTarget("Aircraft", "CheckRadarAlt",
                    "radar altitude above the water: the HUD, airborne and touchdown, radar clutter").Globals(H, H),
                new WaterTarget("Missile", "UpdateRadarAlt", "a missile's radar altitude above the water").Globals(H, H),
                new WaterTarget("TerrainWarningSystem", "CheckTerrain", "the ground-proximity warning meets the water").Rays(1),
                new WaterTarget("Autopilot", "TerrainAvoidanceCheck",
                    "AI terrain avoidance: time to the water's surface").Reads(B).Globals(H).Rays(1),
                new WaterTarget("Autopilot", "TerrainWaypoint", "AI terrain following stays above the water").Reads(I, S),
                new WaterTarget("OpticalSeekerCruiseMissile", "TerrainWaypoint",
                    "a cruise missile's terrain following stays above the water").Reads(B, I, B, I, B),
                new WaterTarget("OpticalSeekerCruiseMissile", "TerminalMode",
                    "a cruise missile's aim point at least 1 m above the water").Literals0(1f, false, B),
                new WaterTarget("OpticalSeekerCruiseMissile", "OpticalSeekerCruiseMissile_ProcessSetDestination",
                    "a cruise missile's waypoint at least 1 m above the water").Literals0(1f, false, B),
                new WaterTarget("ARHSeeker", "Seek", "a radar missile's aim point not under the water").Literals0(0f, false, B),
                new WaterTarget("IRSeeker", "Seek", "an infrared missile's aim point not under the water").Literals0(0f, false, B),
                new WaterTarget("SARHSeeker", "SendTargetInfo", "a semi-active missile's aim point not under the water")
                    .Literals0(0f, false, B),
                new WaterTarget("AIHeloCombatState", "EjectionCheck", "an AI crew ejects once the cockpit is under the water").Reads(B),
                new WaterTarget("AIHeloTransportState", "EjectionCheck", "an AI crew ejects once the cockpit is under the water").Reads(B),
                new WaterTarget("AIPilotCombatModes", "EjectionCheck", "an AI pilot ejects once the cockpit is under the water").Reads(B),
                new WaterTarget("AIPilotLandingState", "EjectionCheck", "an AI pilot ejects once the cockpit is under the water").Reads(B),
                new WaterTarget("AIPilotShortLandingState", "EjectionCheck", "an AI pilot ejects once the aircraft is under the water").Reads(B),
                new WaterTarget("AIHeloTransportState+TransportDestination", "UpdateTouchdownPoint",
                    "a transport helicopter's landing zone must be above the water").Reads(B),
                new WaterTarget("HUDBombingState", "CCIPTrajectory", "the bombing pipper falls onto the water's surface")
                    .Literals0(0f, false, B, B).Rays(1),

                // --- weapons -------------------------------------------------------------------
                new WaterTarget("BulletSim+Bullet", "TrajectoryTrace", "bullets and shells strike the water")
                    .Reads(B).Literals0(0f, false, B, B),
                new WaterTarget("Missile", "DetectCollisions",
                    "a missile or bomb under the water detonates or burns out and drags; a hit below the water is water").Reads(B, I, B),
                new WaterTarget("Missile+Warhead", "Detonate", "underwater and water-surface blasts at the water's surface")
                    .Reads(B, B, B, B, B),
                new WaterTarget("Missile+Motor", "Destruct", "a motor's break-up effects only above the water").Reads(B),
                new WaterTarget("VLSBooster", "Thrust", "a booster burns out under the water").Reads(B),
                new WaterTarget("VLSBooster", "FixedUpdate", "a spent booster splashes into the water").Reads(B),
                new WaterTarget("VLSBooster", "Splash", "a booster's splash on the water's surface").Reads(B),
                new WaterTarget("IRFlare", "Update", "a flare goes out in the water").Globals(H),
                new WaterTarget("SpecialFlare", "Update", "a flare goes out in the water").Globals(H),
                new WaterTarget("RadarChaff", "Update", "chaff stops in the water").Globals(H),
                new WaterTarget("Shockwave", "Start", "a blast near the water marks the water's surface").Reads(B),
                new WaterTarget("MushroomCloud", "Start", "a nuclear burst over water is a water burst, its height above the water")
                    .Reads(B, B),
                new WaterTarget("MushroomCloud+WaterEffect", "Initialize", "a water burst's effects on the water's surface")
                    .Reads(B, B),
                new WaterTarget("WaterEffect", "Start", "splashes and plumes stand on the water's surface").Reads(B),
                new WaterTarget("ConstrainToSeaLevel", "Update", "an effect held to the water's surface").Reads(B),

                // --- propellers, rotors, downwash ---------------------------------------------
                new WaterTarget("ConstantSpeedProp", "FixedUpdate", "a propeller near the water checks its blades").Reads(B),
                new WaterTarget("ConstantSpeedProp+PropBlade", "CheckWaterCollision",
                    "a propeller blade strikes the water, and its spray stands on it").Rays(1).Literals0(0f, true, B),
                new WaterTarget("SwashRotor", "CheckCollisions", "a rotor blade strikes the water").Rays(1),
                new WaterTarget("SoftBodyRotor", "CheckCollisions", "a rotor tip dips into the water").Reads(B, B).Rays(1),
                new WaterTarget("Downwash+DownwashSource", "GetSurfaceContact", "rotor downwash sprays the water, raises dust on the ground")
                    .Reads(B, B).Rays(1),
                new WaterTarget("Downwash", "Update", "the downwash spray rides the water's surface").Reads(B),
                new WaterTarget("DownwashEffect", "Update", "a jet's low-pass spray over the water").Rays(1),

                // --- people and cargo in the water ---------------------------------------------
                new WaterTarget("PilotDismounted", "FixedUpdate", "an ejected pilot is in the water").Globals(H),
                new WaterTarget("PilotDismounted", "CheckRadarAlt", "an ejected pilot's height above the water").Globals(H),
                new WaterTarget("PilotDismounted", "LocalFixedUpdate", "an ejected pilot floats").Reads(A),
                new WaterTarget("Parachute", "CheckRadarAlt", "a parachute's height above the water").Positions(H, H),
                new WaterTarget("EscapeCapsule", "CheckRadarAlt", "an escape capsule's height above the water").Positions(H),
                new WaterTarget("EscapeCapsule", "WaterPhysics", "an escape capsule floats, and splashes on the water's surface")
                    .Positions(H).Reads(B),
                new WaterTarget("SlingloadHook", "Update", "a slung load enters and leaves the water").Reads(B, B),
                new WaterTarget("SlingloadHook", "ConnectedState", "a slung load floats").Reads(A),
                new WaterTarget("SlingloadHook", "RescuePilotState", "a pilot being rescued floats").Reads(A),
                new WaterTarget("Container", "FixedUpdate", "a container floats, and splashes on the water's surface").Reads(A, B),

                // --- the camera ------------------------------------------------------------------
                new WaterTarget("CameraStateManager", "LateUpdate", "the underwater view").Reads(B, B),
                new WaterTarget("LevelInfo", "UpdateTimeOfDayLighting",
                    "the sky's lighting and fog wait while the camera is under the water (its altitude stays an altitude)").Globals(C),
                new WaterTarget("LevelInfo", "UpdateReflectionProbe", "no reflection probe render under the water").Reads(B),
                new WaterTarget("CameraFreeState", "UpdateState", "the free camera stays 1.7 m above the water").Reads(N),
                new WaterTarget("NuclearOption.MissionEditorScripts.EditorCameraNavigator", "Update",
                    "the mission editor's camera stays 1.7 m above the water").Reads(N),
                new WaterTarget("CameraTVState", "GetTVCamPoint",
                    "the TV camera stays 1.7 m above the water (and 1.7 m over the ground it finds, as before)").Literals0(1.7f, false, K, B),
            };
        }

        /// <summary>
        /// Applies every target, once. Called at start-up when an installed map has raised lakes, and
        /// again (doing nothing the second time) when a map with lakes is prepared. Never throws: the
        /// worst it can cost is the lakes.
        /// </summary>
        public static void Apply(Harmony harmony)
        {
            if (_attempted || Plugin.Disabled) return;
            _attempted = true;

            try
            {
                ApplyAll(harmony);
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"lake water: patching stopped part way ({Summary}): {e}");
            }
        }

        static void ApplyAll(Harmony harmony)
        {
            if (!HelpersCompile(out string problem))
            {
                Plugin.LogWarning($"lake water: {problem}; the game's water tests are left as they are, and raised lakes " +
                                  "stay sheets an aircraft can fly under");
                return;
            }

            var transpiler = new HarmonyMethod(AccessTools.Method(typeof(LakeWaterPatches), nameof(Transpiler)));
            var missing = new List<string>();
            var refused = new List<string>();

            foreach (WaterTarget target in Targets())
            {
                _total++;
                MethodBase method = target.Resolve();
                if (method == null)
                {
                    missing.Add(target.Name);
                    continue;
                }

                target.Method = method;
                _targets[method] = target;

                try
                {
                    harmony.Patch(method, transpiler: transpiler);
                    if (!target.Applied) refused.Add(target.Name);
                }
                catch (Exception e)
                {
                    target.Applied = false;
                    refused.Add(target.Name);
                    Plugin.LogWarning($"lake water: {target.Name} could not be patched: {e.Message}");
                }
            }

            _total++;
            _aeroApplied = AeroPartLakePatch.Apply(harmony);
            if (!_aeroApplied) refused.Add("AeroPart.UpdateJobFields");

            if (missing.Count > 0)
                Plugin.LogWarning($"lake water: not found in this build of the game, so left on the sea: {string.Join(", ", missing)}");
            if (refused.Count > 0)
                Plugin.LogWarning($"lake water: not what this plugin expects, so left on the sea: {string.Join(", ", refused)}");

            Plugin.LogInfo($"lake water: {Summary}; a raised lake is water to them as the sea is");
        }

        /// <summary>The <see cref="LakeWater"/> methods the patched game code calls.</summary>
        static readonly HashSet<string> HelperNames = new HashSet<string>(StringComparer.Ordinal)
        {
            nameof(LakeWater.LocalWaterY), nameof(LakeWater.GlobalWaterY), nameof(LakeWater.GlobalWaterYAt),
            nameof(LakeWater.HeightAboveWater), nameof(LakeWater.LakeLocalYOr), nameof(LakeWater.Raycast),
            nameof(LakeWater.LakeDepth),
        };

        /// <summary>
        /// Compiles and runs every helper the patched game code will call. A helper that names a
        /// member this build of the game no longer has would otherwise throw from inside the game's
        /// own methods, on every map; found here, it costs the lakes and nothing else.
        /// </summary>
        static bool HelpersCompile(out string problem)
        {
            try
            {
                foreach (MethodInfo helper in typeof(LakeWater).GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (HelperNames.Contains(helper.Name)) RuntimeHelpers.PrepareMethod(helper.MethodHandle);

                LakeWater.LocalWaterY(Vector3.zero);
                LakeWater.HeightAboveWater(Vector3.zero);
                problem = null;
                return true;
            }
            catch (Exception e)
            {
                problem = $"the lake water helpers do not run against this build of the game ({e.GetType().Name}: {e.Message})";
                return false;
            }
        }

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator,
                                                       MethodBase __originalMethod)
        {
            var code = instructions.ToList();
            if (__originalMethod == null || !_targets.TryGetValue(__originalMethod, out WaterTarget target)) return code;

            if (!Rewrite(code, generator, __originalMethod, target, out List<CodeInstruction> result, out string problem))
            {
                target.Applied = false;
                Plugin.LogWarning($"lake water: {target.Name} is left as it is ({problem}), so {target.Purpose} only at sea");
                return code;
            }

            target.Applied = true;
            Plugin.LogDebug($"lake water: {target.Name}: {target.Purpose}");
            return result;
        }

        // ------------------------------------------------------------------------------------------
        // The rewrite. Each site's edit is worked out first, against the original list; nothing is
        // changed unless every site of the method checks out.

        static readonly MethodInfo LocalSeaY = AccessTools.PropertyGetter(typeof(Datum), nameof(Datum.LocalSeaY));
        static readonly MethodInfo Origin = AccessTools.PropertyGetter(typeof(Datum), nameof(Datum.origin));
        static readonly MethodInfo Position = AccessTools.PropertyGetter(typeof(Transform), nameof(Transform.position));
        static readonly MethodInfo GlobalYOf = AccessTools.Method(typeof(GlobalPositionExtensions), nameof(GlobalPositionExtensions.GlobalY),
                                                                  new[] { typeof(Vector3) });
        static readonly MethodInfo PlaneRaycast = AccessTools.Method(typeof(Plane), nameof(Plane.Raycast),
                                                                     new[] { typeof(Ray), typeof(float).MakeByRefType() });
        static readonly FieldInfo VectorX = AccessTools.Field(typeof(Vector3), nameof(Vector3.x));
        static readonly FieldInfo VectorY = AccessTools.Field(typeof(Vector3), nameof(Vector3.y));
        static readonly FieldInfo GlobalX = AccessTools.Field(typeof(GlobalPosition), nameof(GlobalPosition.x));
        static readonly FieldInfo GlobalYField = AccessTools.Field(typeof(GlobalPosition), nameof(GlobalPosition.y));

        static MethodInfo Helper(string name, params Type[] parameters)
            => AccessTools.Method(typeof(LakeWater), name, parameters)
               ?? throw new MissingMethodException(nameof(LakeWater), name);

        static readonly Type VectorRef = typeof(Vector3).MakeByRefType(), GlobalRef = typeof(GlobalPosition).MakeByRefType();

        // Harmony's labels are a List<System.Reflection.Emit.Label> from the runtime's mscorlib, a type
        // a netstandard build cannot name, so they are looked at through reflection, and local and
        // argument indices are read here rather than through Harmony's helpers that take a LocalBuilder.
        static readonly FieldInfo LabelsField = AccessTools.Field(typeof(CodeInstruction), "labels");

        /// <summary>Whether a jump lands on the instruction.</summary>
        static bool HasLabels(CodeInstruction ins)
            => LabelsField == null || !(LabelsField.GetValue(ins) is System.Collections.ICollection labels) || labels.Count > 0;

        static bool IsLoadLocal(CodeInstruction ins)
        {
            OpCode op = ins.opcode;
            return op == OpCodes.Ldloc_0 || op == OpCodes.Ldloc_1 || op == OpCodes.Ldloc_2 || op == OpCodes.Ldloc_3 ||
                   op == OpCodes.Ldloc_S || op == OpCodes.Ldloc || op == OpCodes.Ldloca_S || op == OpCodes.Ldloca;
        }

        static bool IsStoreLocal(CodeInstruction ins)
        {
            OpCode op = ins.opcode;
            return op == OpCodes.Stloc_0 || op == OpCodes.Stloc_1 || op == OpCodes.Stloc_2 || op == OpCodes.Stloc_3 ||
                   op == OpCodes.Stloc_S || op == OpCodes.Stloc;
        }

        static bool IsLoadArgument(CodeInstruction ins)
        {
            OpCode op = ins.opcode;
            return op == OpCodes.Ldarg_0 || op == OpCodes.Ldarg_1 || op == OpCodes.Ldarg_2 || op == OpCodes.Ldarg_3 ||
                   op == OpCodes.Ldarg_S || op == OpCodes.Ldarg;
        }

        /// <summary>The local an ldloc, ldloca or stloc names, or -1.</summary>
        static int LocalIndexOf(CodeInstruction ins)
        {
            OpCode op = ins.opcode;
            if (op == OpCodes.Ldloc_0 || op == OpCodes.Stloc_0) return 0;
            if (op == OpCodes.Ldloc_1 || op == OpCodes.Stloc_1) return 1;
            if (op == OpCodes.Ldloc_2 || op == OpCodes.Stloc_2) return 2;
            if (op == OpCodes.Ldloc_3 || op == OpCodes.Stloc_3) return 3;
            return IndexIn(ins.operand);
        }

        /// <summary>The argument an ldarg names, counting the instance as 0, or -1.</summary>
        static int ArgumentIndexOf(CodeInstruction ins, MethodBase method)
        {
            OpCode op = ins.opcode;
            if (op == OpCodes.Ldarg_0) return 0;
            if (op == OpCodes.Ldarg_1) return 1;
            if (op == OpCodes.Ldarg_2) return 2;
            if (op == OpCodes.Ldarg_3) return 3;
            if (ins.operand is ParameterInfo parameter) return parameter.Position + (method.IsStatic ? 0 : 1);
            return IndexIn(ins.operand);
        }

        static int IndexIn(object operand)
        {
            switch (operand)
            {
                case LocalVariableInfo local: return local.LocalIndex;
                case byte b: return b;
                case sbyte sb: return sb;
                case short s: return s;
                case ushort us: return us;
                case int i: return i;
                default: return -1;
            }
        }

        sealed class Edit
        {
            public readonly List<CodeInstruction> Insert = new List<CodeInstruction>();
            public List<CodeInstruction> Replace;
            public bool Remove;
        }

        sealed class Rewriter
        {
            readonly List<CodeInstruction> _code;
            readonly ILGenerator _generator;
            readonly MethodBase _method;
            readonly Edit[] _edits;
            LocalBuilder _last;
            public string Problem;

            public Rewriter(List<CodeInstruction> code, ILGenerator generator, MethodBase method)
            {
                _code = code;
                _generator = generator;
                _method = method;
                _edits = new Edit[code.Count];
            }

            Edit At(int index) => _edits[index] ?? (_edits[index] = new Edit());

            bool Fail(string problem)
            {
                Problem = Problem ?? problem;
                return false;
            }

            /// <summary>Instructions <paramref name="index"/> to <paramref name="index"/> + <paramref name="length"/>
            /// - 1 become <paramref name="replacement"/>; the first one's labels move to it.</summary>
            bool Replace(int index, int length, List<CodeInstruction> replacement)
            {
                for (int i = index; i < index + length; i++)
                {
                    Edit edit = At(i);
                    if (edit.Replace != null || edit.Remove) return Fail($"two edits meet at IL {i}");
                    if (_code[i].blocks.Count > 0) return Fail($"an exception block starts at IL {i}");
                    if (i > index && HasLabels(_code[i])) return Fail($"a jump lands inside the read at IL {i}");
                    if (i > index) edit.Remove = true;
                }

                At(index).Replace = replacement;
                return true;
            }

            bool InsertBefore(int index, params CodeInstruction[] inserted)
            {
                Edit edit = At(index);
                if (edit.Insert.Count > 0) return Fail($"two insertions meet at IL {index}");
                if (_code[index].blocks.Count > 0) return Fail($"an exception block starts at IL {index}");
                edit.Insert.AddRange(inserted);
                return true;
            }

            LocalBuilder Remember()
            {
                _last = _generator.DeclareLocal(typeof(float));
                return _last;
            }

            /// <summary>Whether the value the instruction leaves on the stack is a managed pointer to
            /// a struct (true), the struct itself (false), or something this cannot tell (null).</summary>
            bool? IsAddress(CodeInstruction producer)
            {
                OpCode op = producer.opcode;
                if (op == OpCodes.Ldloca || op == OpCodes.Ldloca_S || op == OpCodes.Ldarga || op == OpCodes.Ldarga_S ||
                    op == OpCodes.Ldflda || op == OpCodes.Ldsflda || op == OpCodes.Ldelema)
                    return true;

                if (op == OpCodes.Call || op == OpCodes.Callvirt)
                    return producer.operand is MethodInfo called ? called.ReturnType.IsByRef : (bool?)null;

                if (op == OpCodes.Ldfld || op == OpCodes.Ldsfld)
                    return producer.operand is FieldInfo field ? field.FieldType.IsByRef : (bool?)null;

                if (IsLoadLocal(producer))
                {
                    int local = LocalIndexOf(producer);
                    IList<LocalVariableInfo> locals = _method.GetMethodBody()?.LocalVariables;
                    return locals != null && local >= 0 && local < locals.Count ? locals[local].LocalType.IsByRef : (bool?)null;
                }

                if (IsLoadArgument(producer))
                {
                    int argument = ArgumentIndexOf(producer, _method);
                    if (argument < 0) return null;
                    if (!_method.IsStatic)
                    {
                        if (argument == 0) return _method.DeclaringType.IsValueType;
                        argument--;
                    }

                    ParameterInfo[] parameters = _method.GetParameters();
                    return argument >= 0 && argument < parameters.Length ? parameters[argument].ParameterType.IsByRef : (bool?)null;
                }

                if (op == OpCodes.Newobj || op == OpCodes.Ldobj) return false;
                return null;
            }

            /// <summary>
            /// The level under the vector loaded just before <paramref name="read"/> (by an
            /// <c>ldfld x</c> or <c>ldfld y</c>), worked out before that load into a new local: the
            /// load is copied first, so the method's own arithmetic on it is untouched.
            /// </summary>
            bool Before(int read, int length, Func<Type, bool, (MethodInfo Helper, CodeInstruction[] Extra)> helperFor,
                        List<CodeInstruction> replacement)
            {
                int load = read - 1;
                if (load < 1) return Fail($"nothing is loaded before IL {read}");

                CodeInstruction field = _code[load];
                FieldInfo loaded = field.opcode == OpCodes.Ldfld ? field.operand as FieldInfo : null;
                if (loaded == null || !(loaded.Name == "x" || loaded.Name == "y") ||
                    !(loaded.DeclaringType == typeof(Vector3) || loaded.DeclaringType == typeof(GlobalPosition)))
                    return Fail($"IL {read} does not follow a vector's x or y");

                bool? address = IsAddress(_code[load - 1]);
                if (address == null) return Fail($"cannot tell how the vector before IL {load} is held");

                (MethodInfo helper, CodeInstruction[] extra) = helperFor(loaded.DeclaringType, address.Value);
                if (helper == null) return Fail($"no helper for {loaded.DeclaringType.Name} at IL {load}");

                LocalBuilder level = Remember();
                var inserted = new List<CodeInstruction> { new CodeInstruction(OpCodes.Dup) };
                inserted.AddRange(extra);
                inserted.Add(new CodeInstruction(OpCodes.Call, helper));
                inserted.Add(new CodeInstruction(OpCodes.Stloc, level));

                replacement.Insert(0, new CodeInstruction(OpCodes.Ldloc, level));
                return InsertBefore(load, inserted.ToArray()) && Replace(read, length, replacement);
            }

            public bool Sea(int read, int length, Water action)
            {
                switch (action)
                {
                    case Water.Keep:
                        return true;

                    case Water.Before:
                        return Before(read, length, (type, address) =>
                            (Helper(nameof(LakeWater.LocalWaterY), type == typeof(Vector3)
                                ? (address ? VectorRef : typeof(Vector3))
                                : (address ? GlobalRef : typeof(GlobalPosition))), Array.Empty<CodeInstruction>()),
                            new List<CodeInstruction>());

                    case Water.Into:
                    {
                        int store = read + length;
                        if (store >= _code.Count || _code[store].opcode != OpCodes.Stfld || !Equals(_code[store].operand, VectorY))
                            return Fail($"IL {read} is not stored into a vector's y");

                        LocalBuilder level = Remember();
                        return Replace(read, length, new List<CodeInstruction>
                        {
                            new CodeInstruction(OpCodes.Dup),
                            new CodeInstruction(OpCodes.Ldobj, typeof(Vector3)),
                            new CodeInstruction(OpCodes.Call, Helper(nameof(LakeWater.LocalWaterY), typeof(Vector3))),
                            new CodeInstruction(OpCodes.Dup),
                            new CodeInstruction(OpCodes.Stloc, level),
                        });
                    }

                    case Water.After:
                    {
                        // A chain of plain loads and argument-free getters ending in a Vector3, then its y.
                        int end = read + length;
                        int y = end;
                        while (y < _code.Count && y < end + 8 && !(_code[y].opcode == OpCodes.Ldfld && Equals(_code[y].operand, VectorY))) y++;
                        if (y >= _code.Count || y == end || !(_code[y].opcode == OpCodes.Ldfld && Equals(_code[y].operand, VectorY)))
                            return Fail($"no vector's y follows IL {read}");

                        var chain = new List<CodeInstruction>();
                        for (int i = end; i < y; i++)
                        {
                            CodeInstruction step = _code[i];
                            if (HasLabels(step) || step.blocks.Count > 0) return Fail($"a jump lands in the vector after IL {read}");
                            bool plain = IsLoadArgument(step) || (IsLoadLocal(step) && step.opcode != OpCodes.Ldloca && step.opcode != OpCodes.Ldloca_S) ||
                                         (step.opcode == OpCodes.Ldfld && step.operand is FieldInfo f && !f.FieldType.IsValueType);
                            bool getter = (step.opcode == OpCodes.Call || step.opcode == OpCodes.Callvirt) && step.operand is MethodInfo m &&
                                          m.Name.StartsWith("get_", StringComparison.Ordinal) && m.GetParameters().Length == 0 && !m.IsStatic;
                            if (!plain && !getter) return Fail($"the vector after IL {read} is not a plain chain of loads ({step.opcode})");
                            chain.Add(step.Clone());
                        }

                        CodeInstruction last = _code[y - 1];
                        if (!(last.operand is MethodInfo producer && producer.ReturnType == typeof(Vector3)))
                            return Fail($"the chain after IL {read} does not end in a Vector3");

                        LocalBuilder level = Remember();
                        chain.Add(new CodeInstruction(OpCodes.Call, Helper(nameof(LakeWater.LocalWaterY), typeof(Vector3))));
                        chain.Add(new CodeInstruction(OpCodes.Dup));
                        chain.Add(new CodeInstruction(OpCodes.Stloc, level));
                        return Replace(read, length, chain);
                    }

                    case Water.NextLocal:
                    {
                        int end = read + length;
                        int y = end;
                        while (y < _code.Count && y < end + 12 && !(_code[y].opcode == OpCodes.Ldfld && Equals(_code[y].operand, VectorY))) y++;
                        if (y >= _code.Count || !(_code[y].opcode == OpCodes.Ldfld && Equals(_code[y].operand, VectorY)) || y == end)
                            return Fail($"no local vector's y follows IL {read}");

                        CodeInstruction holder = _code[y - 1];
                        if (!IsLoadLocal(holder)) return Fail($"the vector compared after IL {read} is not a local");

                        int local = LocalIndexOf(holder);
                        IList<LocalVariableInfo> locals = _method.GetMethodBody()?.LocalVariables;
                        if (locals == null || local < 0 || local >= locals.Count || locals[local].LocalType != typeof(Vector3))
                            return Fail($"local {local} after IL {read} is not a Vector3");

                        // Nothing may store into it, or take its address (a constructor or an out parameter
                        // writes through that), between the read and the use.
                        for (int i = end; i < y - 1; i++)
                            if ((IsStoreLocal(_code[i]) || _code[i].opcode == OpCodes.Ldloca || _code[i].opcode == OpCodes.Ldloca_S) &&
                                LocalIndexOf(_code[i]) == local)
                                return Fail($"local {local} may change between IL {read} and its use");

                        // The same local, loaded by value: the holder itself, or its ldloca made an ldloc.
                        CodeInstruction value = holder.opcode == OpCodes.Ldloca_S ? new CodeInstruction(OpCodes.Ldloc_S, holder.operand)
                                              : holder.opcode == OpCodes.Ldloca ? new CodeInstruction(OpCodes.Ldloc, holder.operand)
                                              : holder.Clone();

                        LocalBuilder level = Remember();
                        return Replace(read, length, new List<CodeInstruction>
                        {
                            value,
                            new CodeInstruction(OpCodes.Call, Helper(nameof(LakeWater.LocalWaterY), typeof(Vector3))),
                            new CodeInstruction(OpCodes.Dup),
                            new CodeInstruction(OpCodes.Stloc, level),
                        });
                    }

                    case Water.Same:
                        if (_last == null) return Fail($"IL {read} reuses a level no earlier read worked out");
                        return Replace(read, length, new List<CodeInstruction> { new CodeInstruction(OpCodes.Ldloc, _last) });

                    default:
                        return Fail($"{action} is not a sea-level read's action");
                }
            }

            public bool GlobalY(int call, Water action)
            {
                switch (action)
                {
                    case Water.Keep:
                        return true;

                    case Water.Height:
                        return Replace(call, 1, new List<CodeInstruction>
                        {
                            new CodeInstruction(OpCodes.Call, Helper(nameof(LakeWater.HeightAboveWater), typeof(Vector3))),
                        });

                    case Water.CompareOnly:
                    {
                        // GlobalY(v); stloc x; ldloc x; ldc.r4 0; <compare>: the comparison's 0 becomes the
                        // water's global height under v, worked out before the call.
                        if (call + 3 >= _code.Count || !IsStoreLocal(_code[call + 1]) || !IsLoadLocal(_code[call + 2]) ||
                            LocalIndexOf(_code[call + 1]) != LocalIndexOf(_code[call + 2]) ||
                            _code[call + 3].opcode != OpCodes.Ldc_R4 || !(_code[call + 3].operand is float zero) || zero != 0f)
                            return Fail($"GlobalY at IL {call} is not stored and compared with 0");

                        LocalBuilder level = Remember();
                        return InsertBefore(call,
                                   new CodeInstruction(OpCodes.Dup),
                                   new CodeInstruction(OpCodes.Call, Helper(nameof(LakeWater.GlobalWaterYAt), typeof(Vector3))),
                                   new CodeInstruction(OpCodes.Stloc, level))
                               && Replace(call + 3, 1, new List<CodeInstruction> { new CodeInstruction(OpCodes.Ldloc, level) });
                    }

                    default:
                        return Fail($"{action} is not a GlobalY action");
                }
            }

            public bool GlobalPositionY(int load, Water action)
            {
                switch (action)
                {
                    case Water.Keep:
                        return true;

                    case Water.Height:
                    {
                        bool? address = load > 0 ? IsAddress(_code[load - 1]) : null;
                        if (address == null) return Fail($"cannot tell how the GlobalPosition before IL {load} is held");
                        return Replace(load, 1, new List<CodeInstruction>
                        {
                            new CodeInstruction(OpCodes.Call, Helper(nameof(LakeWater.HeightAboveWater),
                                                                     address.Value ? GlobalRef : typeof(GlobalPosition))),
                        });
                    }

                    default:
                        return Fail($"{action} is not a GlobalPosition.y action");
                }
            }

            public bool Literal(int literal, float value, bool onlyOverLakes, Water action)
            {
                switch (action)
                {
                    case Water.Keep:
                        return true;

                    case Water.Before:
                    {
                        var replacement = new List<CodeInstruction>();
                        if (!onlyOverLakes && value != 0f)
                        {
                            // The water's height plus the game's own margin over the sea's 0.
                            replacement.Add(new CodeInstruction(OpCodes.Ldc_R4, value));
                            replacement.Add(new CodeInstruction(OpCodes.Add));
                        }

                        return Before(literal, 1, (type, address) =>
                        {
                            if (onlyOverLakes)
                                return type == typeof(Vector3)
                                    ? (Helper(nameof(LakeWater.LakeLocalYOr), address ? VectorRef : typeof(Vector3), typeof(float)),
                                       new[] { new CodeInstruction(OpCodes.Ldc_R4, value) })
                                    : (null, null);

                            return type == typeof(GlobalPosition)
                                ? (Helper(nameof(LakeWater.GlobalWaterY), address ? GlobalRef : typeof(GlobalPosition)), Array.Empty<CodeInstruction>())
                                : (null, null);
                        }, replacement);
                    }

                    default:
                        return Fail($"{action} is not a literal's action");
                }
            }

            public bool Raycast(int call)
                => Replace(call, 1, new List<CodeInstruction>
                {
                    new CodeInstruction(OpCodes.Call, Helper(nameof(LakeWater.Raycast), typeof(Plane).MakeByRefType(),
                                                             typeof(Ray), typeof(float).MakeByRefType())),
                });

            public List<CodeInstruction> Result()
            {
                var result = new List<CodeInstruction>(_code.Count + 32);
                for (int i = 0; i < _code.Count; i++)
                {
                    CodeInstruction original = _code[i];
                    Edit edit = _edits[i];
                    if (edit == null)
                    {
                        result.Add(original);
                        continue;
                    }

                    // Whatever comes first in place of the original takes its labels and blocks.
                    List<CodeInstruction> inPlace = new List<CodeInstruction>(edit.Insert);
                    if (edit.Replace != null) inPlace.AddRange(edit.Replace);
                    else if (!edit.Remove) inPlace.Add(original.Clone());

                    if (inPlace.Count > 0)
                    {
                        original.MoveLabelsTo(inPlace[0]);
                        original.MoveBlocksTo(inPlace[0]);
                    }

                    result.AddRange(inPlace);
                }

                return result;
            }
        }

        /// <summary>
        /// Rewrites one method's IL as its target says, or returns false, naming what did not match,
        /// with nothing changed. Internal so an offline check can run it over the game's real IL.
        /// </summary>
        internal static bool Rewrite(List<CodeInstruction> code, ILGenerator generator, MethodBase method, WaterTarget target,
                                     out List<CodeInstruction> result, out string problem)
        {
            result = null;
            var rewriter = new Rewriter(code, generator, method);

            // Every site of every kind, in IL order, so a Same reuses the level just before it.
            var sites = new List<(int Index, Func<bool> Apply)>();
            int sea = 0, globalY = 0, globalPositionY = 0, literals = 0, raycasts = 0;

            for (int i = 0; i < code.Count; i++)
            {
                CodeInstruction ins = code[i];
                int index = i;

                int seaLength = 0;
                if (ins.Calls(LocalSeaY)) seaLength = 1;
                else if (ins.Calls(Origin) && i + 2 < code.Count && code[i + 1].Calls(Position) &&
                         code[i + 2].opcode == OpCodes.Ldfld && Equals(code[i + 2].operand, VectorY))
                    seaLength = 3;

                if (seaLength > 0)
                {
                    int k = sea++;
                    int length = seaLength;
                    if (target.Sea != null && k < target.Sea.Length)
                        sites.Add((index, () => rewriter.Sea(index, length, target.Sea[k])));
                    i += seaLength - 1;
                    continue;
                }

                if (GlobalYOf != null && ins.Calls(GlobalYOf))
                {
                    int k = globalY++;
                    if (target.GlobalY != null && k < target.GlobalY.Length)
                        sites.Add((index, () => rewriter.GlobalY(index, target.GlobalY[k])));
                }
                else if (ins.opcode == OpCodes.Ldfld && Equals(ins.operand, GlobalYField))
                {
                    int k = globalPositionY++;
                    if (target.GlobalPositionY != null && k < target.GlobalPositionY.Length)
                        sites.Add((index, () => rewriter.GlobalPositionY(index, target.GlobalPositionY[k])));
                }
                else if (PlaneRaycast != null && ins.Calls(PlaneRaycast))
                {
                    raycasts++;
                    if (target.Raycasts > 0) sites.Add((index, () => rewriter.Raycast(index)));
                }

                if (target.Literals != null && ins.opcode == OpCodes.Ldc_R4 && ins.operand is float value && value == target.Literal &&
                    i > 0 && code[i - 1].opcode == OpCodes.Ldfld && code[i - 1].operand is FieldInfo before &&
                    (before == VectorX || before == VectorY || before == GlobalX || before == GlobalYField))
                {
                    int k = literals++;
                    if (k < target.Literals.Length)
                        sites.Add((index, () => rewriter.Literal(index, target.Literal, target.LiteralOnlyOverLakes, target.Literals[k])));
                }
            }

            if (target.Sea != null && sea != target.Sea.Length) { problem = $"{sea} sea-level read(s) where {target.Sea.Length} were expected"; return false; }
            if (target.GlobalY != null && globalY != target.GlobalY.Length) { problem = $"{globalY} GlobalY call(s) where {target.GlobalY.Length} were expected"; return false; }
            if (target.GlobalPositionY != null && globalPositionY != target.GlobalPositionY.Length) { problem = $"{globalPositionY} GlobalPosition.y load(s) where {target.GlobalPositionY.Length} were expected"; return false; }
            if (target.Literals != null && literals != target.Literals.Length) { problem = $"{literals} literal height(s) of {target.Literal} where {target.Literals.Length} were expected"; return false; }
            if (target.Raycasts > 0 && raycasts != target.Raycasts) { problem = $"{raycasts} Plane.Raycast call(s) where {target.Raycasts} were expected"; return false; }

            foreach (var site in sites.OrderBy(s => s.Index))
            {
                bool ok;
                try { ok = site.Apply(); }
                catch (Exception e) { ok = false; rewriter.Problem = rewriter.Problem ?? $"{e.GetType().Name}: {e.Message}"; }

                if (!ok)
                {
                    problem = rewriter.Problem ?? $"the site at IL {site.Index} does not match";
                    return false;
                }
            }

            result = rewriter.Result();
            problem = null;
            return true;
        }
    }

    /// <summary>
    /// An aircraft part in a lake, for the game's water model.
    ///
    /// <c>AeroJob_Math</c> (Burst, so not patchable) takes a part's depth as
    /// <c>max(submergedAmount, -(globalY - collisionSize.y))</c>: the sea from the part's height,
    /// and anything else from <c>submergedAmount</c>, a field the part copies into the job in
    /// <c>UpdateJobFields</c> and then zeroes. In the base game only a water trigger fills it, and
    /// none ships. This fills it, before the copy, with the same expression measured from the
    /// lake's surface, so a part in a lake gets exactly the sea's treatment: drag, water density,
    /// wings that stop lifting, buoyancy, the splash, and above 83 m/s the airframe breaking up.
    /// </summary>
    internal static class AeroPartLakePatch
    {
        public static bool Apply(Harmony harmony)
        {
            try
            {
                MethodInfo method = AccessTools.Method(typeof(AeroPart), "UpdateJobFields");
                if (method == null || AccessTools.Field(typeof(AeroPart), "submergedAmount") == null ||
                    AccessTools.Field(typeof(AeroPart), "collisionSize") == null || AccessTools.Field(typeof(AeroPart), "liftNormal") == null)
                {
                    Plugin.LogWarning("lake water: AeroPart.UpdateJobFields or its fields are gone, so aircraft fly through lakes");
                    return false;
                }

                harmony.Patch(method, prefix: new HarmonyMethod(AccessTools.Method(typeof(AeroPartLakePatch), nameof(Prefix))));
                return true;
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"lake water: AeroPart.UpdateJobFields could not be patched ({e.Message}), so aircraft fly through lakes");
                return false;
            }
        }

        static void Prefix(ref float ___submergedAmount, Vector3 ___collisionSize, Transform ___liftNormal)
        {
            LakeFrame frame = LakeWater.Frame;
            if (frame == null || ___liftNormal == null) return;

            float depth = LakeWater.LakeDepth(frame, ___liftNormal.position, ___collisionSize.y);
            if (depth > ___submergedAmount) ___submergedAmount = depth;
        }
    }

    /// <summary>Tells <see cref="LakeWater"/> which map is in play. A postfix of its own on
    /// <c>ApplyMapSettings</c>, so it stands or falls apart from the other work there.</summary>
    [HarmonyPatch(typeof(LevelInfo), nameof(LevelInfo.ApplyMapSettings))]
    internal static class LakeWaterMapPatch
    {
        static void Postfix(MapSettings mapSettings)
        {
            try { LakeWater.MapApplied(mapSettings); }
            catch (Exception e)
            {
                LakeWater.Clear();
                Plugin.LogWarning($"lake water: {e.Message}");
            }
        }
    }
}
