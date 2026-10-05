using System;
using System.Collections.Generic;
using System.Reflection;
using Mirage;
using NuclearOption.SavedMission;
using RoadPathfinding;
using UnityEngine;

namespace CustomMaps
{
    /// <summary>
    /// Gives the map its airbases: the logic, not the airfield.
    ///
    /// An <c>Airbase</c> is not a thing a bundle can carry. It is a game type, a
    /// <c>NetworkBehaviour</c>, and it comes with runways that have entry and exit points,
    /// vertical landing pads, service points and a taxi network — none of it describable in
    /// a bundle that deliberately contains no game types. So one is cloned at load from
    /// whichever shipped map has one, the same way the terrain material, the city buildings
    /// and the road asphalt are.
    ///
    /// What is <em>not</em> borrowed any more is the visible airfield. That was tried at
    /// length and the failure is structural: a shipped airbase is authored for the ground it
    /// stands on, so it arrives at its own size, on its own runway length, carrying its own
    /// patch of desert. Half the shipped airbases turned out to have no geometry of their own
    /// at all, and of the rest the ones small enough to fit a Swiss valley were highway
    /// strips whose single mesh <em>is</em> the runway — so the rule that hid the imported
    /// ground was hiding the airfield. The paving now ships in the bundle, cut to the ground
    /// the generator levelled, and every renderer on the clone is switched off.
    ///
    /// What remains is to tell the borrowed component where its runway now is. The donor's
    /// runway transforms describe the donor's strip, and left alone they had Sion declaring a
    /// 2,000 m runway while the geometry under it was a 900 m one.
    ///
    /// All of it happens on the <em>prefab</em>, before the map is ever instantiated, which
    /// is what makes it stick: <c>Airbase.Awake</c> reads its own <c>center</c> transform to
    /// establish its centre and registers under <c>SavedAirbase.UniqueName</c> from
    /// <c>OnStartServer</c>, and both run after <c>Instantiate</c>. By the time the game
    /// looks, each airbase has always been where it is and always been called what it is
    /// called.
    /// </summary>
    internal static class AirbaseBuilder
    {
        /// <summary>Name of the child the airbases are parented under.</summary>
        public const string AirbaseRoot = "Airbases";

        /// <summary>
        /// Clones the donor airbase once per placement.
        ///
        /// Returns the identities that need registering with <c>NetworkMap</c>, in a fixed
        /// order. That order is the contract: <c>NetworkMap</c> hands each one a prefab hash
        /// of <c>(MapPrefix &lt;&lt; 24) | index</c>, and server and client have to agree on
        /// it. They do here for a reason that does not generalise — a custom map's version
        /// handshake is the SHA-256 of its own bundle, so two machines that disagree about
        /// the contents cannot be in the same game at all.
        /// </summary>
        public static List<NetworkIdentity> Build(GameObject root, LoadedMap map)
        {
            var registered = new List<NetworkIdentity>();

            string asset = map.Manifest?.Airbases;
            if (string.IsNullOrEmpty(asset)) return registered;

            List<AirbasePlacement> placements = ReadPlacements(map, asset);
            if (placements == null || placements.Count == 0) return registered;

            Template donor = Donor();
            if (donor == null)
            {
                Plugin.LogWarning($"{root.name}: {placements.Count} airbase(s) in the manifest but no shipped " +
                                  "airbase to borrow from — the map will have nowhere to take off from.");
                return registered;
            }

            var parent = new GameObject(AirbaseRoot);
            parent.transform.SetParent(root.transform, worldPositionStays: false);

            // The fields' taxi networks, by airbase; none for a bundle built before them, whose airbases
            // are each given the default network instead (DefaultNetwork).
            Dictionary<string, TaxiNetworkData> lanes = ReadTaxiways(map);

            int built = 0;
            var placed = new System.Text.StringBuilder();
            var points = new List<string>();
            var taxiing = new List<string>();

            foreach (AirbasePlacement placement in placements)
            {
                lanes.TryGetValue(placement.UniqueName ?? "", out TaxiNetworkData fieldLanes);
                Airbase clone = Clone(donor, parent.transform, placement, fieldLanes, taxiing);
                if (clone == null) continue;

                // Every identity in the subtree, not just the airbase's own: the donor can
                // bring networked children with it, and one in the scene that nothing
                // registers is one the client never learns about.
                foreach (NetworkIdentity identity in clone.GetComponentsInChildren<NetworkIdentity>(true))
                    registered.Add(identity);

                built++;

                points.Add(DescribeDonorPoints(clone, placement));

                placed.AppendLine($"    {placement.UniqueName,-5} {placement.Faction,-8} " +
                                  $"runway centre ({placement.X,8:N0}, {placement.Z,8:N0}) at {placement.Y,5:N0} m, " +
                                  $"{placement.RunwayLength:N0} x {placement.RunwayWidth:N0} m on {placement.Heading:0}deg, " +
                                  $"levelled {placement.FlatHalfAlong * 2f:N0} x {placement.FlatHalfAcross * 2f:N0} m, " +
                                  DescribeRunways(clone) + ", " + DescribeFlag(placement));
            }

            Plugin.LogDebug($"{root.name}: {built} airbase(s) built on '{donor.Airbase.name}', " +
                            $"{registered.Count} network object(s) to register. The airfields themselves " +
                            $"ship in the bundle." + Environment.NewLine + placed.ToString().TrimEnd());

            // One line each, after the summary rather than inside the loop, so the log reads
            // as the list of airbases and then where each one's borrowed points came to rest.
            foreach (string line in points)
                Plugin.LogDebug(line);

            foreach (string line in taxiing)
                Plugin.LogDebug(line);

            return registered;
        }

        /// <summary>
        /// The map's taxi networks, by airbase, from the TextAsset <c>&lt;mapId&gt;_taxiways</c>
        /// (<see cref="TaxiData"/>). Found by that name, not through the manifest, as the grass mask is:
        /// a bundle built before taxi networks has none, and that is no fault, so nothing is said about
        /// it; its airbases get the default network.
        /// </summary>
        static Dictionary<string, TaxiNetworkData> ReadTaxiways(LoadedMap map)
        {
            var none = new Dictionary<string, TaxiNetworkData>(StringComparer.Ordinal);
            if (map?.Bundle == null || string.IsNullOrEmpty(map.Manifest?.MapId)) return none;

            TextAsset data;
            try
            {
                data = map.Bundle.LoadAsset<TextAsset>(map.Manifest.MapId + TaxiwaysSuffix);
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"{map.Name()}: could not load its taxi networks ({e.GetType().Name}: {e.Message}); each " +
                                  "airbase is given the default one");
                return none;
            }

            if (data == null) return none;

            try
            {
                var notes = new List<string>();
                Dictionary<string, TaxiNetworkData> lanes = TaxiPlans.ByAirbase(TaxiData.Read(data.bytes), notes);
                foreach (string note in notes) Plugin.LogWarning($"{map.Name()}: {note}");
                return lanes;
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"{map.Name()}: '{map.Manifest.MapId}{TaxiwaysSuffix}' is not readable taxi networks ({e.Message}); " +
                                  "each airbase is given the default one");
                return none;
            }
        }

        /// <summary>What the taxi lanes ship as, after the map's id (NOMapForge's <c>MapForge.TaxiwaysSuffix</c>).</summary>
        public const string TaxiwaysSuffix = "_taxiways";

        static List<AirbasePlacement> ReadPlacements(LoadedMap map, string asset)
        {
            TextAsset data = map.Asset<TextAsset>(asset);
            if (data == null)
            {
                Plugin.LogWarning($"{map.Name()}: manifest names airbases '{asset}' but the bundle has no " +
                                  "such TextAsset — no airbases will be built.");
                return null;
            }

            try
            {
                return AirbaseData.Read(data.bytes);
            }
            catch (Exception e)
            {
                Plugin.LogWarning($"{map.Name()}: '{asset}' is not readable airbase data ({e.Message})");
                return null;
            }
        }

        /// <summary>The shipped airbase this map's airbases are cloned from.</summary>
        sealed class Template
        {
            public Airbase Airbase;

            /// <summary>Middle of its runways, in its own frame — the point the clone is
            /// aligned by, because that is what has to land on the levelled ground.</summary>
            public Vector3 Deck;
        }

        /// <summary>
        /// One shipped airbase to clone, for every field on the map.
        ///
        /// There is nothing left to choose between them. The clone contributes no geometry —
        /// every renderer on it is switched off and the airfield is built in the bundle — so
        /// what a donor brings is an <c>Airbase</c> component, a <c>NetworkIdentity</c>, and
        /// a runway whose transforms are about to be moved anyway.
        ///
        /// This used to be a survey of all seventeen shipped airbases, measured and ranked so
        /// the biggest that fitted each platform could be picked. That machinery is gone with
        /// the geometry it was sizing, and so is the failure mode it produced: an airfield
        /// can no longer overhang ground that was never levelled, because nothing is placed
        /// that was not cut to fit.
        ///
        /// The fewest runways, because only the first is moved onto this map's main runway: a
        /// field's other runways are built from the map's own data by
        /// <see cref="LayOutExtraRunways"/>, which drops any spare the donor brought. The donor
        /// this picks (North Boscali) has one.
        /// </summary>
        static Template Donor()
        {
            Airbase best = null;
            int fewest = int.MaxValue;

            foreach (MapSettings shipped in MapFixups.ShippedMaps())
            {
                foreach (Airbase candidate in shipped.GetComponentsInChildren<Airbase>(true))
                {
                    int runways = candidate.runways?.Length ?? 0;
                    if (runways == 0) continue;

                    if (runways >= fewest) continue;

                    fewest = runways;
                    best = candidate;
                }
            }

            if (best == null) return null;

            Plugin.LogDebug($"airbase donor: '{best.name}' with {fewest} runway(s) — cloned for every " +
                            "field, geometry switched off, runway re-pointed at this map's own paving");

            return new Template { Airbase = best, Deck = RunwayCentre(best) };
        }

        /// <summary>
        /// One airbase, put where the map wants it and told what it is called.
        ///
        /// The rotation is the difference between the donor's own runway heading and the
        /// one wanted here, so the whole airfield — runways, taxiways, tower, lights —
        /// turns together and lands on the platform the generator levelled for it.
        /// </summary>
        /// <param name="lanes">The field's taxi network as the map carries it, or null for one it carries
        /// none for, which is given the default.</param>
        /// <param name="log">Gathers a line for each field's network, for after the summary.</param>
        static Airbase Clone(Template template, Transform parent, AirbasePlacement placement, TaxiNetworkData lanes,
                             List<string> log)
        {
            Airbase clone = UnityEngine.Object.Instantiate(template.Airbase, parent);
            if (clone == null) return null;

            clone.name = placement.UniqueName;

            // What lets a mission move this airbase's flag and set its range, and no other's.
            clone.gameObject.AddComponent<CustomMapAirbase>();

            var rotation = Quaternion.Euler(0f, placement.Heading - DonorHeading(template.Airbase), 0f);

            // Where the runway sits in the donor's own frame, which is not the origin: an
            // airbase is authored wherever it happened to be on its own map, so its pivot
            // can be a kilometre from the tarmac and tens of metres off the ground. Putting
            // the pivot on the platform therefore puts the runway somewhere else entirely —
            // which is how seven airbases registered correctly and none of them could be
            // found on the map.
            Vector3 deck = template.Deck;
            Vector3 shift = rotation * new Vector3(deck.x, 0f, deck.z);

            clone.transform.localRotation = rotation;
            clone.transform.localPosition = new Vector3(placement.X - shift.x,
                                                        placement.Y - deck.y,
                                                        placement.Z - shift.z);

            int solid = Hide(clone);
            if (solid > 0)
                Plugin.LogDebug($"airbase '{placement.UniqueName}': {solid} collider(s) the donor brought are " +
                                "switched off with its geometry — nothing draws them, and this field has its own " +
                                "paving, floor and roads to stand on");

            ForgetRunwayLights(clone);
            LayOutRunway(clone, placement);
            PlaceFlag(clone, placement);
            int[] runwayOf = LayOutExtraRunways(clone, placement);

            // The field's own taxi network, its AI roads, with every runway's exits rebuilt onto it: the
            // one the map carries for it (its lanes and taxiways, or Map Forge's default), or for a map
            // from before taxi networks shipped the default built here, so that no airbase is left
            // without one. Only when even that cannot be built is the donor's emptied, as it always was.
            // A network that does not lie on this field's runways was made for where the field was
            // before: a taxiways.bin left from an earlier export beside airbases.bin from a later one.
            if (lanes != null && !TaxiPlans.Fits(lanes, placement, out string misfit))
            {
                Plugin.LogWarning($"airbase '{placement.UniqueName}': its taxi network in the map does not lie on its runways " +
                                  $"({misfit}), so it was made for the field before it moved; it gets the default network " +
                                  "instead. Export the airfields again in Map Forge and rebuild the map");
                lanes = null;
            }

            TaxiNetworkData data = lanes ?? DefaultNetwork(clone, placement);
            TaxiPlan plan = data != null ? TaxiPlans.Plan(data, placement) : null;
            if (plan != null && plan.HasNetwork && LayOutTaxiNetwork(clone, placement, plan))
            {
                int exits = LayOutExits(clone, placement, plan, runwayOf);
                NetworkSources[clone] = new NetworkSource { Source = data.Source, FromMap = lanes != null };
                log.Add(DescribeLanes(clone, placement, plan, data.Source, lanes != null, exits));
            }
            else ClearTaxiNetwork(clone);

            if (plan != null) PlaceServicePoints(clone, placement, plan);

            Rename(clone, placement);
            ForgetDonorTower(clone, placement);

            return clone;
        }

        /// <summary>What an airbase built here was given for a taxi network, and whether the map carried
        /// it: for the map's check report (<see cref="MapDiagnostics"/>), which reads the prefab's
        /// airbases right after they are built.</summary>
        internal struct NetworkSource
        {
            public TaxiSource Source;
            public bool FromMap;
        }

        internal static readonly Dictionary<Airbase, NetworkSource> NetworkSources = new Dictionary<Airbase, NetworkSource>();

        /// <summary>
        /// The default taxi network (<see cref="TaxiDefault"/>) for a field the map carries none for,
        /// which is every field of a bundle built before taxi networks shipped: a lane along the main
        /// runway on the side of the service point, onto both of its ends and every 700 m or so, the
        /// same Map Forge now writes for a field drawn with neither lanes nor taxiways. The service point
        /// is the donor's, where it came to rest on the clone, and stays where it is.
        ///
        /// Without it such a field's AI drives every leg in a straight line, through whatever stands in
        /// the way, and lands its aircraft onto the donor's exits, which on a runway shorter than about
        /// 1.1 km lie past its ends (<see cref="DescribeDonorPoints"/> logs where they came to rest).
        /// </summary>
        static TaxiNetworkData DefaultNetwork(Airbase clone, AirbasePlacement placement)
        {
            Transform frame = clone.transform.parent;
            (float X, float Z) service;

            Transform[] points = typeof(Airbase).GetField(ServicePointsField, BindingFlags.Instance | BindingFlags.NonPublic)
                                                ?.GetValue(clone) as Transform[];
            if (points != null && points.Length > 0 && points[0] != null)
            {
                Vector3 at = frame != null ? frame.InverseTransformPoint(points[0].position) : points[0].position;
                service = (at.x, at.z);
            }
            else
            {
                // Where the donor keeps it: 56 m along and 206 m right of the runway's middle.
                AirfieldDirection(placement.Heading, out float dx, out float dz);
                service = (placement.X + dx * 56f + dz * 206f, placement.Z + dz * 56f - dx * 206f);
            }

            TaxiNetworkData data = TaxiDefault.Build(placement, service);
            data.ServicePoint = null;
            return data;
        }

        /// <summary>
        /// Gives the clone the field's own taxi network: the game's <c>RoadNetwork</c> of <c>Road</c>s,
        /// each from one junction to the next, as Map Forge split them (<c>TaxiGraph</c>) or
        /// <see cref="TaxiDefault"/> laid them, set into the same private field <see cref="ClearTaxiNetwork"/>
        /// empties.
        ///
        /// Set on the prefab, so <c>Instantiate</c> copies it with the airbase as it copied the donor's,
        /// and <c>Airbase.OnStartServer</c> makes its junctions (<c>RegenerateNetwork</c>, which joins road
        /// ends within 10 m; the editor keeps every two junctions further apart). The points are map
        /// coordinates, which is what a <c>GlobalPosition</c> is on a map at the origin, the same frame
        /// <see cref="RoadNetworkFixup"/> builds the roads in, at the level the platform was cut to and
        /// the tarmac's 6 cm over it.
        /// </summary>
        /// <returns>False when the field could not be set, which leaves the donor's network to be emptied.</returns>
        static bool LayOutTaxiNetwork(Airbase clone, AirbasePlacement placement, TaxiPlan plan)
        {
            FieldInfo field = typeof(Airbase).GetField(TaxiNetworkField, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
            {
                Plugin.LogWarning($"Airbase has no '{TaxiNetworkField}' field; airbase '{placement.UniqueName}' keeps no taxi " +
                                  "lanes and its AI taxis straight across the field");
                return false;
            }

            var network = new RoadNetwork { AllowMerge = true };
            float y = placement.Y + TaxiLift;

            foreach ((float X, float Z)[] points in plan.Roads)
            {
                var road = new Road();
                foreach ((float x, float z) in points) road.AddPoint(new GlobalPosition(x, y, z));

                // AddPoint grows the bounding box but leaves the length at 0, and Dijkstra weighs every
                // road by its length (RoadNetworkFixup.Build says what follows without it).
                road.CalcLength();
                network.roads.Add(road);
            }

            field.SetValue(clone, network);
            return true;
        }

        /// <summary>Metres above the platform the taxi network lies: the tarmac's height in the stack
        /// of surfaces (NOMapForge's <c>AirfieldDrape.TarmacLift</c>).</summary>
        const float TaxiLift = 0.06f;

        /// <summary>
        /// Rebuilds every runway's exits from the lanes: one transform for each exit and each way of
        /// rolling it serves, on the runway's centreline where a lane leaves it, facing the roll, and
        /// one at each end facing out of the runway, as <see cref="ExtraRunway"/> gives an extra
        /// runway. They replace the donor's six main-runway exits, which lay where Heartland's
        /// taxiways leave its strip and, on a runway shorter than about 1.1 km, past this runway's ends.
        ///
        /// Exits are what joins a landing to the taxi network. After touchdown the AI takes the nearest
        /// exit ahead that faces its roll and that it can brake for (<c>Runway.TryGetExitTaxiPoint</c>),
        /// rolls on unsteered until it is within 20 m of it, and only there looks for a way along the
        /// lanes to the service point; so an exit has to lie on the centreline, where the roll takes it.
        /// </summary>
        /// <param name="runwayOf">The clone's runway for each of the placement's, -1 for one left out.</param>
        /// <returns>How many exit transforms were made.</returns>
        static int LayOutExits(Airbase clone, AirbasePlacement placement, TaxiPlan plan, int[] runwayOf)
        {
            Transform frame = clone.transform.parent;
            int made = 0;

            for (int r = 0; r < plan.Exits.Length && r < runwayOf.Length; r++)
            {
                int index = runwayOf[r];
                if (index < 0 || clone.runways == null || index >= clone.runways.Length || plan.Exits[r].Count == 0) continue;

                var exits = new List<Transform>(plan.Exits[r].Count);
                foreach (PlannedExit exit in plan.Exits[r])
                {
                    var point = new GameObject($"Runway {index + 1} exit {exits.Count + 1} " +
                                               (exit.Forward ? "rolling forward" : "rolling back")).transform;
                    point.SetParent(clone.transform, worldPositionStays: false);
                    point.position = Local(frame, new Vector3(exit.X, placement.Y, exit.Z));

                    var facing = new Vector3(exit.FacingX, 0f, exit.FacingZ);
                    point.rotation = Quaternion.LookRotation(frame != null ? frame.TransformDirection(facing) : facing, Vector3.up);
                    exits.Add(point);
                }

                clone.runways[index].exitPoints = exits.ToArray();
                made += exits.Count;
            }

            return made;
        }

        /// <summary>
        /// Puts the service point and the landing pad where the lanes reach them, when the field says
        /// where: a field with lanes always does, and a field without may have had them put by hand.
        /// The donor's sit 56 m along and 206 m right of the runway's middle and 89 m along and 243 m
        /// right, which a generated layout is paved round and a drawn one may not be. An aircraft that
        /// has landed taxis to the nearest service point (<c>Airbase.TryGetNearestServicePoint</c>), and
        /// a vertical-landing one sets down on the pad.
        ///
        /// The service point is a transform of the clone's own, set into the private
        /// <c>servicePoints</c>; the pad is the donor's first <c>VerticalLandingPoint</c>, moved.
        /// </summary>
        static void PlaceServicePoints(Airbase clone, AirbasePlacement placement, TaxiPlan plan)
        {
            Transform frame = clone.transform.parent;

            if (plan.ServicePoint.HasValue)
            {
                FieldInfo field = typeof(Airbase).GetField(ServicePointsField, BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null)
                    Plugin.LogWarning($"Airbase has no '{ServicePointsField}' field; airbase '{placement.UniqueName}' keeps the " +
                                      "donor's service point");
                else
                {
                    Transform[] donor = field.GetValue(clone) as Transform[];
                    var point = new GameObject("Service point").transform;
                    point.SetParent(clone.transform, worldPositionStays: false);
                    point.position = Local(frame, new Vector3(plan.ServicePoint.Value.X, placement.Y, plan.ServicePoint.Value.Z));
                    point.rotation = donor != null && donor.Length > 0 && donor[0] != null ? donor[0].rotation : clone.transform.rotation;
                    field.SetValue(clone, new[] { point });
                }
            }

            if (plan.LandingPad.HasValue && clone.verticalLandingPoints != null && clone.verticalLandingPoints.Length > 0 &&
                clone.verticalLandingPoints[0] != null)
            {
                Airbase.VerticalLandingPoint pad = clone.verticalLandingPoints[0];
                Vector3 at = Local(frame, new Vector3(plan.LandingPad.Value.X, placement.Y, plan.LandingPad.Value.Z));

                // The donor's own pad moves; one that is not the clone's to move gets a transform of its own.
                if (pad.point != null && pad.point.IsChildOf(clone.transform)) pad.point.position = at;
                else
                {
                    var point = new GameObject("Landing pad").transform;
                    point.SetParent(clone.transform, worldPositionStays: false);
                    point.position = at;
                    pad.point = point;
                }
            }
        }

        /// <summary>One line for a field: what its network was made from, its size, its exits, and each
        /// runway's numbers as painted beside what the game calls it, which can differ only for a runway
        /// drawn within a hair of a heading ending in 5.</summary>
        static string DescribeLanes(Airbase clone, AirbasePlacement placement, TaxiPlan plan, TaxiSource source, bool fromMap,
                                    int exits)
        {
            int points = 0;
            foreach ((float X, float Z)[] road in plan.Roads) points += road.Length;

            string made = source == TaxiSource.Lanes ? "from its lanes"
                        : source == TaxiSource.Taxiways ? "from its taxiways"
                        : fromMap ? "the default" : "the default, built at load (the map carries none, or none for where it is)";

            var called = new List<string>();
            if (clone.runways != null)
                foreach (Airbase.Runway runway in clone.runways)
                {
                    if (runway?.Start == null || runway.End == null) continue;
                    string forward = runway.GetName(false), back = runway.GetName(true);
                    called.Add($"{forward.Replace("Runway ", "")}/{back.Replace("Runway ", "")}");
                }

            return $"{placement.UniqueName}: taxi network {made}, {plan.Roads.Count} road(s), {points} point(s), {exits} exit(s)" +
                   (plan.DroppedExits > 0 ? $" ({plan.DroppedExits} on a runway it does not have left out)" : "") +
                   $"; painted {TaxiPlans.Painted(placement)}, the game calls them {string.Join(", ", called)}";
        }

        /// <summary>
        /// Lets go of the donor's control tower, so the mission editor's Tower field starts
        /// empty and the user can pick one they have placed.
        ///
        /// <c>Airbase.MapTower</c> is a serialised reference to a <c>Building</c> on the donor's
        /// own map — Heartland's <c>controlTower1</c> — and that building is not in the subtree
        /// <c>Instantiate</c> copies, so the clone kept pointing at a tower on another map. The
        /// settings carried the same thing by name, as <c>&lt;MAP_UNIT&gt;++controlTower1</c>.
        /// Neither can ever resolve here: the editor fills a built-in airbase's Tower field
        /// from <c>MapTower</c> (<c>SavedAirbase.AfterLoadEditor</c>), and at runtime
        /// <c>Airbase.AddBuilding</c> makes a building the tower only when its unique name is
        /// <c>SavedAirbase.Tower</c>. All the inheritance did was show a tower nobody could see.
        ///
        /// Emptied rather than pointed at anything, because the plugin places no buildings: a
        /// tower, like the hangars, is the mission's to place and attach, exactly as on a
        /// base-game airbase. Every base-game mission saves <c>"Tower": ""</c> and this starts
        /// the same way. <c>MapTower</c> is public, so it is simply cleared; the settings go
        /// through <see cref="Settings"/> for the same reason the renaming does.
        /// </summary>
        static void ForgetDonorTower(Airbase clone, AirbasePlacement placement)
        {
            clone.MapTower = null;

            SavedAirbase settings = Settings(clone);
            if (settings == null)
            {
                Plugin.LogWarning($"airbase '{placement.UniqueName}' has no settings — the mission editor " +
                                  "may show the donor map's control tower as its Tower");
                return;
            }

            settings.Tower = "";
        }

        /// <summary>
        /// Where the donor's service points, vertical-landing points, runway exit points and
        /// fixed camera came to rest once the clone was placed, as one line for the log.
        ///
        /// None of these is moved by <see cref="LayOutRunway"/>. They are children of the
        /// clone, so they turned and shifted with it and should be somewhere near this
        /// airfield — but they were authored around Heartland's strip, and whether "near" means
        /// on the paving drawn for this map or out on the grass beside it has not been measured.
        /// It matters: with the taxi network gone, an aircraft that has landed drives straight
        /// to the nearest service point, and a vertical-landing aircraft sets down on its pad
        /// wherever that is. This reports and changes nothing, so the next log can say whether
        /// they need moving before anything moves them.
        ///
        /// Each point is given in map coordinates (x, z, and height) and then relative to the
        /// runway centre in the airfield's own frame: metres along the runway (positive towards
        /// the heading), across it (positive to the right looking down the heading) and above
        /// the level the platform was cut to. A star marks a point off the levelled ground: outside
        /// the drawn outline for a field drawn in the editor, which is the ground levelled for it,
        /// and outside the levelled rectangle for one that has no outline. Neither shape is the
        /// paving itself, which is painted inside the outline, so an unstarred point is on level
        /// ground but not necessarily on tarmac.
        /// </summary>
        static string DescribeDonorPoints(Airbase clone, AirbasePlacement placement)
        {
            try
            {
                Transform frame = clone.transform.parent;

                AirfieldDirection(placement.Heading, out float dx, out float dz);

                string Where(Transform point)
                {
                    if (point == null) return "none";

                    Vector3 at = frame != null ? frame.InverseTransformPoint(point.position) : point.position;

                    float ox = at.x - placement.X, oz = at.z - placement.Z;
                    float along = ox * dx + oz * dz;
                    float across = ox * dz - oz * dx;
                    float up = at.y - placement.Y;

                    // A drawn field was levelled inside its outline, not inside the rectangle, which
                    // is only the largest runway-aligned box that fits in it.
                    bool off = placement.HasOutline
                        ? !InsideOutline(placement.Outline, at.x, at.z)
                        : Mathf.Abs(along) > placement.FlatHalfAlong || Mathf.Abs(across) > placement.FlatHalfAcross;

                    return $"{at.x:0}/{at.z:0}/{at.y:0} -> {along:+0;-0;0}/{across:+0;-0;0}/{up:+0.0;-0.0;0.0}" +
                           (off ? "*" : "");
                }

                string All(IEnumerable<Transform> transforms)
                {
                    if (transforms == null) return "unreadable";

                    var parts = new List<string>();
                    foreach (Transform transform in transforms) parts.Add(Where(transform));

                    return parts.Count == 0 ? "[0]" : $"[{parts.Count}] " + string.Join("  ", parts);
                }

                Transform[] service = typeof(Airbase)
                    .GetField(ServicePointsField, BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(clone) as Transform[];

                var vertical = new List<Transform>();
                if (clone.verticalLandingPoints != null)
                    foreach (Airbase.VerticalLandingPoint pad in clone.verticalLandingPoints)
                        vertical.Add(pad?.point);

                var exits = new List<Transform>();
                if (clone.runways != null)
                    foreach (Airbase.Runway runway in clone.runways)
                        if (runway?.exitPoints != null)
                            exits.AddRange(runway.exitPoints);

                string ground = placement.HasOutline
                    ? $"drawn outline of {placement.Outline.Length} points, * = outside it"
                    : $"levelled ±{placement.FlatHalfAlong:0} x ±{placement.FlatHalfAcross:0}, * = outside that";

                return $"{placement.UniqueName}: donor points after placing, as map x/z/height -> " +
                       $"along/across/up from the runway centre in m (runway ±{placement.RunwayLength * 0.5f:0} x " +
                       $"±{placement.RunwayWidth * 0.5f:0}, {ground}) — " +
                       $"service {All(service)}; " +
                       $"vertical landing {All(vertical)}; " +
                       $"exit {All(exits)}; " +
                       $"camera {Where(clone.fixedCameraTransform)}";
            }
            catch (Exception e)
            {
                // A diagnostic that throws must not take the airbase, or the map, with it.
                return $"{placement.UniqueName}: could not describe the donor's points ({e.Message})";
            }
        }

        /// <summary>The private field <see cref="DescribeDonorPoints"/> reads the service points from,
        /// and <see cref="PlaceServicePoints"/> sets for a field with lanes; checked at startup.
        /// Missing, the line says "unreadable" and a field keeps the donor's service point.</summary>
        public const string ServicePointsField = "servicePoints";

        /// <summary>Whether a map position lies inside a drawn outline (even-odd rule; the outline is
        /// in map metres, x east and z north, the same frame the placement is in).</summary>
        static bool InsideOutline((float X, float Z)[] outline, float x, float z)
        {
            bool inside = false;

            for (int i = 0, j = outline.Length - 1; i < outline.Length; j = i++)
            {
                (float xi, float zi) = outline[i];
                (float xj, float zj) = outline[j];

                if ((zi > z) != (zj > z) && x < (xj - xi) * (z - zi) / (zj - zi) + xi)
                    inside = !inside;
            }

            return inside;
        }

        /// <summary>
        /// Lets go of the donor's runway lights.
        ///
        /// <c>Airbase.runwayLights</c> is a private serialised array of renderers in the donor's
        /// subtree: Heartland's lights, laid out for Heartland's strip, which <see cref="Hide"/> has
        /// just switched off with everything else the donor draws. The game switches them on again
        /// by itself: <c>Airbase.WaitRepair</c>, which runs on every capture including the first at
        /// mission start, enables every one of them five seconds later whenever the airbase has a
        /// tower that can be repaired. With the plugin no longer placing a tower, that happens as
        /// soon as a mission attaches one — and the donor's light rows would appear along a runway
        /// they were never laid out for.
        ///
        /// Emptied rather than left null: the game loops over it on capture, on repair and when the
        /// tower is destroyed, and an empty array is a loop that does nothing.
        /// </summary>
        static void ForgetRunwayLights(Airbase clone)
        {
            FieldInfo field = typeof(Airbase).GetField(RunwayLightsField, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
            {
                Plugin.LogWarning($"Airbase has no '{RunwayLightsField}' field; a tower attached in a mission may " +
                                  "switch the donor map's runway lights back on");
                return;
            }

            field.SetValue(clone, new Renderer[0]);
        }

        /// <summary>The private field <see cref="ForgetRunwayLights"/> empties.</summary>
        const string RunwayLightsField = "runwayLights";

        /// <summary>
        /// Takes away the donor's taxi network, for a field that cannot be given its own
        /// (<see cref="LayOutTaxiNetwork"/>): the game has no field to set, or the runway is too short
        /// for even the default network.
        ///
        /// It is a private serialised field, so the clone copies it — and its roads are
        /// <c>GlobalPosition</c>s, fixed to where the donor stands on its own map. Measured on
        /// Swiss Alps, every airbase carried Heartland's taxiways 13 to 104 km away, so an
        /// aircraft rolling out after landing pathfound towards another airfield entirely.
        ///
        /// Until 2026-10 every field's was emptied here, and the cost of that is why every field now
        /// gets a network of its own. <c>AIPilotTaxiState</c> checks <c>taxiNetwork.Exists()</c> and,
        /// without one, drives straight to the nearest service point after landing and straight to
        /// the threshold for take-off, through whatever stands in the way. The service points are the
        /// donor's own children, so they moved with the clone and are somewhere on this airfield —
        /// where exactly is what <see cref="DescribeDonorPoints"/> logs. Those straight lines cross the
        /// field's grass, so <see cref="Patches.AirfieldTaxiPatch"/> lets an aircraft taxiing or
        /// lining up under AI control meet the field's floor as paving.
        /// </summary>
        static void ClearTaxiNetwork(Airbase clone)
        {
            FieldInfo field = typeof(Airbase).GetField(TaxiNetworkField, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
            {
                Plugin.LogWarning($"Airbase has no '{TaxiNetworkField}' field; aircraft at custom airbases will " +
                                  "taxi along the donor map's taxiways");
                return;
            }

            field.SetValue(clone, new RoadNetwork());
        }

        /// <summary>The private field <see cref="ClearTaxiNetwork"/> empties; checked at startup.</summary>
        public const string TaxiNetworkField = "taxiNetwork";

        /// <summary>
        /// Switches off everything the donor draws.
        ///
        /// All of it, rather than the ground it was standing on. Telling one from the other
        /// by shape was tried — wide, thin, level with the runway — and it cannot work: on a
        /// highway strip, the wide thin surface level with the runway <em>is</em> the runway,
        /// so the rule hid the airfield it was meant to reveal. There is nothing to keep
        /// either way, since the paving this map wants ships in its own bundle, cut to ground
        /// that was levelled for it.
        ///
        /// The colliders go with the renderers. They used to be kept, on the reasoning that a
        /// surface's ground is real even when its appearance belongs to another map. It is not:
        /// the donor's slabs were authored around the donor's own strip, they are turned and
        /// moved with the clone onto ground this map levelled for a field of its own size, and
        /// nothing draws them. A user driving the roads they had laid across the airfield met
        /// Heartland's aprons as walls they could not see. Everything a wheel should meet here is
        /// in this map's own bundle: the runway and the painted tarmac, the floor over the whole
        /// field (ground, since <c>MapFixups.GroundAirfieldFloors</c>), the road ribbons, and the
        /// terrain under all of it.
        ///
        /// Disabled rather than destroyed, so nothing that holds a reference to one of them — the
        /// game's own airbase code, a diagnostic, a later map load — finds a hole where an object
        /// was.
        /// </summary>
        /// <returns>How many colliders were switched off, for the log.</returns>
        static int Hide(Airbase clone)
        {
            foreach (Renderer renderer in clone.GetComponentsInChildren<Renderer>(true))
                renderer.enabled = false;

            int solid = 0;
            foreach (Collider collider in clone.GetComponentsInChildren<Collider>(true))
            {
                if (!collider.enabled) continue;

                collider.enabled = false;
                solid++;
            }

            return solid;
        }

        /// <summary>
        /// Moves the borrowed runway onto this map's runway.
        ///
        /// Without this the clone keeps the donor's strip: its length, its width, and its
        /// thresholds. That is not cosmetic — <c>Airbase.GetTakeoffRunway</c> and the landing
        /// queue are all measured off these two transforms, so an airfield drawn 2,000 m long
        /// over a 900 m borrowed runway lands aircraft short of its own tarmac.
        ///
        /// Done in the clone's local frame, which is already rotated onto the placement's
        /// heading, so the runway simply runs along local Z.
        /// </summary>
        static void LayOutRunway(Airbase clone, AirbasePlacement placement)
        {
            Airbase.Runway runway = FirstRunway(clone);
            if (runway?.Start == null || runway.End == null) return;

            // The placement is in map coordinates and the transforms are children of a clone
            // that has been moved and turned, so the conversion has to go through the world.
            Vector3 centre = new Vector3(placement.X, placement.Y, placement.Z);

            AirfieldDirection(placement.Heading, out float dx, out float dz);
            var along = new Vector3(dx, 0f, dz) * (placement.RunwayLength * 0.5f);

            Transform frame = clone.transform.parent;

            runway.Start.position = Local(frame, centre - along);
            runway.End.position = Local(frame, centre + along);
            runway.width = placement.RunwayWidth;

            if (clone.center != null) clone.center.position = Local(frame, centre);

            if (clone.aircraftSelectionTransform != null)
                clone.aircraftSelectionTransform.position = Local(frame, centre);
        }

        /// <summary>
        /// Stands the airbase's centre, the flag, where the map put it, when the map moved it off the
        /// middle of the runway, where <see cref="LayOutRunway"/> stood it as it always has. On the
        /// platform, at the level it was cut to.
        ///
        /// The centre is <c>Airbase.center</c>, and moving the transform is all it takes, because
        /// nothing copies it before the map is instantiated: <c>Airbase.Awake</c> reads it into the
        /// settings' <c>Center</c>, and everything else asks the transform itself at the time. That
        /// is the capture zone (<c>Capture.GetInRangeUnits</c> measures from it, and
        /// <c>Airbase.Update</c> takes the battlefield grid squares round it a second into the
        /// mission), the map icon (<c>AirbaseMapIcon</c>), the mission editor's flag and radius decal
        /// (made as its children by <c>MissionEditor.CreateFlagForAirbase</c>, so they come with it),
        /// where an AI returning to base flies, and the 5 km past which the base lets an aircraft go
        /// (<c>Airbase.ControlAircraft</c>).
        ///
        /// What stays at the runway is everything with a transform of its own: the thresholds, the
        /// exits, the service points, the landing pads and <c>aircraftSelectionTransform</c>, which
        /// the spawn menu's camera and the aircraft preview stand on. So anything the donor hung
        /// under its centre is put back where it was: which of the donor's children are there has
        /// never been looked at, and none of them is meant to follow the flag. An airbase whose flag
        /// was not moved is not touched here at all, and is built exactly as before.
        /// </summary>
        static void PlaceFlag(Airbase clone, AirbasePlacement placement)
        {
            if (!placement.HasFlag || clone.center == null) return;

            // A donor whose centre is the airbase's own transform would carry the whole airfield
            // off with the flag. None of the shipped ones is built that way; this says so if one is.
            if (clone.center == clone.transform)
            {
                Plugin.LogWarning($"airbase '{placement.UniqueName}': the donor's centre is the airbase itself, so its " +
                                  "flag stays on the runway centre");
                return;
            }

            (float x, float z) = placement.Flag.Value;
            int kept = MoveCentre(clone, Local(clone.transform.parent, new Vector3(x, placement.Y, z)));

            if (kept > 0)
                Plugin.LogDebug($"airbase '{placement.UniqueName}': {kept} of the donor's transform(s) under " +
                                "its centre kept where they were when the flag was moved");
        }

        /// <summary>
        /// Moves an airbase's centre, the flag, to a world position, and leaves the donor's
        /// transforms under it where they were: none of them is meant to follow the flag. The mission
        /// editor's flag and radius decal, which it hangs under the centre, go with it. Used for the
        /// map's flag here, on the prefab, and for a mission's in <see cref="MissionAirbases"/>.
        /// Returns how many of the donor's transforms were kept in place.
        /// </summary>
        internal static int MoveCentre(Airbase airbase, Vector3 position)
        {
            Transform centre = airbase.center;
            if (centre == null || centre == airbase.transform) return 0;

            var children = new List<(Transform Child, Vector3 Position, Quaternion Rotation)>();
            foreach (Transform child in centre)
            {
                if (child.GetComponentInChildren<NuclearOption.MissionEditorScripts.AirbaseEditorFlag>(true) != null ||
                    child.GetComponentInChildren<NuclearOption.MissionEditorScripts.AirbaseEditorRadius>(true) != null)
                    continue;
                children.Add((child, child.position, child.rotation));
            }

            centre.position = position;

            foreach ((Transform child, Vector3 place, Quaternion rotation) in children)
                child.SetPositionAndRotation(place, rotation);

            return children.Count;
        }

        /// <summary>The flag and the capture radius for the log: where the flag was put when it was
        /// moved, and whether the radius is the map's own or the automatic one.</summary>
        static string DescribeFlag(AirbasePlacement placement)
        {
            string flag = placement.HasFlag
                ? $"flag at ({placement.Flag.Value.X:N0}, {placement.Flag.Value.Z:N0})"
                : "flag on the runway centre";

            return flag + $", capture radius {placement.CaptureRangeOrDefault:N0} m " +
                   (placement.HasCaptureRange ? "(the map's own)" : "(automatic)");
        }

        /// <summary>
        /// Gives the clone this map's other runways, after the main one, and nothing else.
        ///
        /// <c>Airbase.runways</c> is a public serialised array of a <c>[Serializable]</c> class, so
        /// a runway added to it here travels with the prefab exactly as the donor's own do:
        /// <c>Instantiate</c> copies the array and remaps its <c>Start</c>/<c>End</c> references to
        /// the copied transforms, because those transforms are children of the clone. Everything
        /// the game derives from a runway is derived later, on the instance: <c>Airbase.Awake</c>
        /// calls <c>Setup(this, i)</c> on each, which sets its airbase, its index (the byte the
        /// landing RPC carries, so server and client agree because both built the same array from
        /// the same bundle) and its <c>Length</c> from the two transforms; <c>OnStartServer</c> then
        /// runs <c>FindCrossingRunways</c> on every runway and on every vertical-landing point.
        /// Take-off (<c>GetTakeoffRunway</c>: nearest threshold of any take-off runway long enough)
        /// and landing (<c>RequestLanding</c>: the landing runway best aligned with the approach)
        /// iterate the whole array, so an extra runway is used by both without anything else.
        ///
        /// Built by hand rather than with <c>Runway.FromSaved</c>, which is what a mission-defined
        /// airbase uses: that one places its transforms from <c>GlobalPosition</c>s through the
        /// floating origin, which means nothing for a prefab that is inactive and not where it
        /// will be, and it leaves <c>entryPoints</c> null. The rest is what it does: no rigidbody,
        /// no lights, an exit point at each threshold, facing out.
        ///
        /// A donor with runways beyond its first would leave them at the donor's own positions,
        /// strips nobody paved. The donor chosen has one, but the array is rebuilt either way, so
        /// any such runway is dropped rather than kept.
        /// </summary>
        /// <returns>For each of the placement's runways, the main one first, its index in the clone's
        /// array, or -1 for one left out; what the taxi lanes' exits are put on by.</returns>
        static int[] LayOutExtraRunways(Airbase clone, AirbasePlacement placement)
        {
            var runwayOf = new int[1 + (placement.HasExtraRunways ? placement.ExtraRunways.Length : 0)];
            for (int i = 0; i < runwayOf.Length; i++) runwayOf[i] = -1;

            Airbase.Runway main = FirstRunway(clone);
            if (main?.Start == null || main.End == null) return runwayOf;

            var runways = new List<Airbase.Runway> { main };
            runwayOf[0] = 0;

            int dropped = (clone.runways?.Length ?? 0) - 1;
            if (dropped > 0)
                Plugin.LogDebug($"airbase '{placement.UniqueName}': {dropped} donor runway(s) beyond the first " +
                                "dropped — they describe the donor's strips, not this field's");

            if (placement.HasExtraRunways)
            {
                Transform frame = clone.transform.parent;

                for (int k = 0; k < placement.ExtraRunways.Length; k++)
                {
                    AirbaseRunway extra = placement.ExtraRunways[k];

                    var start = new Vector3(extra.StartX, placement.Y, extra.StartZ);
                    var end = new Vector3(extra.EndX, placement.Y, extra.EndZ);

                    Vector3 along = end - start;
                    if (along.sqrMagnitude < 1f || !(extra.Width > 0f))
                    {
                        Plugin.LogWarning($"airbase '{placement.UniqueName}': extra runway {k + 1} is degenerate " +
                                          $"({along.magnitude:0} m long, {extra.Width:0} m wide) and is left out");
                        continue;
                    }

                    runwayOf[k + 1] = runways.Count;
                    runways.Add(ExtraRunway(clone, frame, main, start, end, extra.Width, runways.Count));
                }
            }

            clone.runways = runways.ToArray();
            return runwayOf;
        }

        /// <summary>
        /// One extra runway: its two thresholds and two exits as children of the clone, its width,
        /// and the main runway's operating flags.
        ///
        /// Left unnamed, as every base-game runway but one is, so <c>Runway.GetName</c> names it by
        /// the direction in use ("Runway 09" one way, "Runway 27" the other) — the name the
        /// cockpit's "Cleared to taxi to" and "Cleared for landing on" messages show. A fixed name
        /// would be the same in both directions. Arrestor and ski jump are carrier features and
        /// stay off whatever the donor has.
        ///
        /// Both thresholds face down the runway, Start towards End, as the donor's own
        /// <c>runwayStart</c> and <c>runwayEnd</c> do. That is not a matter of taste:
        /// <c>AirbaseOverlay.DrawRunwayBorders</c>, the HUD's landing outline, offsets each threshold
        /// by half the width along that threshold's OWN right vector and joins the four corners in
        /// order. Thresholds facing opposite ways have opposite right vectors, so the far end's two
        /// corners swap sides and the long edges cross: the outline is drawn as an X, which is what
        /// Zurich's two extra runways showed until 2026-09-27, when their thresholds faced out of
        /// the runway so they could double as its exits.
        ///
        /// The exits are therefore transforms of their own at the same two points, as the donor's
        /// <c>exitPoint1</c> to <c>exitPoint6</c> are separate from its thresholds, each facing out of
        /// the runway: <c>TryGetExitTaxiPoint</c> only takes an exit that faces the way the aircraft is
        /// rolling (<c>exitPoints[i].forward</c>) and lies ahead of it, so facing outward the far
        /// threshold's exit is offered whichever way the runway was landed on. One transform cannot
        /// do both jobs, and these two readers are the only ones in the game that look at how a
        /// runway's transforms are turned; everything else reads their positions. The end exit could
        /// have been <c>End</c> itself, which now faces the same way, but a separate transform keeps the
        /// two jobs apart, so a later change to a threshold cannot quietly move an exit, and it reads
        /// the same at both ends.
        /// </summary>
        static Airbase.Runway ExtraRunway(Airbase clone, Transform frame, Airbase.Runway main,
                                          Vector3 start, Vector3 end, float width, int index)
        {
            Vector3 along = frame != null ? frame.TransformDirection(end - start) : end - start;

            // A child of the clone, like the donor's own thresholds and exits, so that when the map
            // prefab is instantiated the runway's references are remapped to the copies with it.
            Transform Point(string label, Vector3 mapPosition, Vector3 facing)
            {
                var point = new GameObject($"Runway {index + 1} {label}").transform;
                point.SetParent(clone.transform, worldPositionStays: false);
                point.position = Local(frame, mapPosition);
                point.rotation = Quaternion.LookRotation(facing, Vector3.up);
                return point;
            }

            var runway = new Airbase.Runway
            {
                Start = Point("start", start, along),
                End = Point("end", end, along),
                Reversable = main.Reversable,
                Takeoff = main.Takeoff,
                Landing = main.Landing,
                AllowSimultaneousTakeoff = main.AllowSimultaneousTakeoff,
                Arrestor = false,
                SkiJump = false,
                entryPoints = new Transform[0],
            };

            runway.exitPoints = new[] { Point("exit start", start, -along), Point("exit end", end, along) };
            runway.width = width;
            runway.name = "";

            return runway;
        }

        /// <summary>
        /// The clone's runways for the log: how many, and each one's length, width and bearing.
        ///
        /// It also warns about any runway whose thresholds are not turned the same way. The HUD's
        /// landing outline (<c>AirbaseOverlay.DrawRunwayBorders</c>) takes each end's corners from
        /// that threshold's own right vector, so thresholds whose right vectors point apart draw the
        /// outline as an X rather than a rectangle; see <see cref="ExtraRunway"/>.
        /// </summary>
        static string DescribeRunways(Airbase clone)
        {
            Airbase.Runway[] runways = clone.runways ?? new Airbase.Runway[0];

            var parts = new List<string>();
            for (int i = 0; i < runways.Length; i++)
            {
                Airbase.Runway runway = runways[i];
                if (runway?.Start == null || runway.End == null) { parts.Add("?"); continue; }

                Vector3 along = runway.End.position - runway.Start.position;
                float bearing = (Mathf.Atan2(along.x, along.z) * Mathf.Rad2Deg + 360f) % 360f;
                parts.Add($"{along.magnitude:N0} x {runway.GetWidth():N0} m on {bearing:0}deg");

                if (Vector3.Dot(runway.Start.right, runway.End.right) <= 0f)
                    Plugin.LogWarning($"airbase '{clone.name}': runway {i + 1} ({bearing:0}deg) has thresholds " +
                                      "turned opposite ways, so the HUD will draw its landing outline as an X");
            }

            return $"{runways.Length} runway(s)" + (parts.Count > 0 ? ": " + string.Join(", ", parts) : "");
        }

        /// <summary>
        /// A map position as a world position, given the map root it belongs to.
        ///
        /// Everything here runs on the prefab, which is not at the origin while it is being
        /// prepared and is not active either. Map coordinates are what the generator wrote
        /// and what the map is laid out in, so they have to be taken through the root's own
        /// transform to mean anything to a child.
        /// </summary>
        static Vector3 Local(Transform frame, Vector3 mapPosition)
            => frame != null ? frame.TransformPoint(mapPosition) : mapPosition;

        /// <summary>Unit vector along a true bearing: +Z north, +X east, clockwise. The same
        /// convention the generator cut the platform on and the bundle paved it on.</summary>
        static void AirfieldDirection(float headingDegrees, out float dx, out float dz)
        {
            float radians = headingDegrees * Mathf.Deg2Rad;
            dx = Mathf.Sin(radians);
            dz = Mathf.Cos(radians);
        }

        /// <summary>
        /// The bearing the donor's own first runway runs on, so the clone can be turned
        /// onto the heading this map wants rather than onto an absolute angle that happens
        /// to be right for the map it came from.
        /// </summary>
        static float DonorHeading(Airbase donor)
        {
            Airbase.Runway runway = FirstRunway(donor);
            if (runway?.Start == null || runway.End == null) return 0f;

            Vector3 along = runway.End.position - runway.Start.position;
            if (along.sqrMagnitude < 1f) return 0f;

            return Mathf.Atan2(along.x, along.z) * Mathf.Rad2Deg;
        }

        static Airbase.Runway FirstRunway(Airbase airbase)
        {
            if (airbase.runways == null) return null;

            foreach (Airbase.Runway runway in airbase.runways)
                if (runway != null) return runway;

            return null;
        }

        /// <summary>
        /// The middle of the donor's runways, in the donor's own frame.
        ///
        /// Averaged across every runway rather than taken from the first, because a field
        /// with three of them has no single "the" runway and centring on one would hang the
        /// other two off the edge of the platform.
        /// </summary>
        static Vector3 RunwayCentre(Airbase donor)
        {
            if (donor.runways == null || donor.runways.Length == 0) return Vector3.zero;

            Vector3 total = Vector3.zero;
            int counted = 0;

            foreach (Airbase.Runway runway in donor.runways)
            {
                if (runway?.Start == null || runway.End == null) continue;

                total += donor.transform.InverseTransformPoint(runway.Start.position);
                total += donor.transform.InverseTransformPoint(runway.End.position);
                counted += 2;
            }

            return counted == 0 ? Vector3.zero : total / counted;
        }

        /// <summary>
        /// The airbase's settings as they exist on a prefab, which is not where the obvious
        /// property looks.
        ///
        /// <c>Airbase.SavedAirbase</c> has a private setter and is filled in <c>Awake</c>,
        /// from the serialized field below it. Everything here runs on the prefab before any
        /// <c>Awake</c> has fired — that is the whole reason it can work at all — so the
        /// property is still null and the field is the only thing there is. Reading the
        /// property instead is exactly what shipped seven airbases all still called "City
        /// Airport", which the registry rejected with an <c>ArgumentException</c> on the
        /// second one.
        /// </summary>
        public static SavedAirbase Settings(Airbase clone)
        {
            if (clone.SavedAirbase != null) return clone.SavedAirbase;

            FieldInfo field = typeof(Airbase).GetField(
                "airbaseSettings", BindingFlags.Instance | BindingFlags.NonPublic);

            if (field == null)
            {
                Plugin.LogWarning("Airbase has no 'airbaseSettings' field — a game update has renamed it, " +
                                  "and custom airbases cannot be named until this is updated to match");
                return null;
            }

            return field.GetValue(clone) as SavedAirbase;
        }

        /// <summary>
        /// Gives the clone its own identity.
        ///
        /// <c>UniqueName</c> is the load-bearing one: <c>Airbase.OnStartServer</c> registers
        /// under it, a mission attaches objectives and spawns by it, and two airbases
        /// sharing one would have the second quietly replace the first in the registry.
        /// Done through the serialized settings rather than the runtime property because
        /// this runs on the prefab, before any <c>Awake</c> has copied it anywhere.
        /// </summary>
        static void Rename(Airbase clone, AirbasePlacement placement)
        {
            SavedAirbase settings = Settings(clone);
            if (settings == null)
            {
                Plugin.LogWarning($"airbase '{placement.UniqueName}' has no settings to rename — it will " +
                                  "keep the donor's name and collide with it in the registry");
                return;
            }

            settings.UniqueName = placement.UniqueName;
            settings.DisplayName = placement.DisplayName;
            settings.faction = placement.Faction ?? "";

            // Built into the map rather than placed by a mission, which is what lets a
            // mission attach to it by name instead of having to define it.
            settings.IsOverride = false;
            settings.Disabled = false;

            // The map's own radius, or one sized to this airfield rather than inherited from the
            // donor. Airbase.GetRadius returns CaptureRange, so leaving it at whatever the donor
            // happened to be gave a small field a capture zone reaching into the next valley, and
            // a large one a zone that did not cover its own aprons. It is the map's default: a
            // mission that sets its own in the editor has that one instead (MissionAirbases).
            settings.Capturable = true;
            settings.CaptureRange = placement.CaptureRangeOrDefault;

            // Awake fills these from the transforms, but only once the map is instantiated
            // and only if they exist. Writing them here means the settings are already
            // self-consistent for anything that reads them before then — the mission loader
            // among them. The centre is the flag (PlaceFlag), the map's default a mission can move.
            // The selection is level with it rather than on the runway: for a built-in airbase the
            // game never reads it, and a mission's copy of it records what the mission chose
            // (AirbaseChoice), which a copy of the map's own must read as nothing.
            (float flagX, float flagZ) = placement.FlagOrCentre;
            settings.Center = new GlobalPosition(new Vector3(flagX, placement.Y, flagZ));
            settings.SelectionPosition = settings.Center;
        }
    }

    /// <summary>
    /// Marks a drawn airfield's floor, the invisible collider over its whole outline
    /// (<see cref="MapFixups.IsAirfieldFloor"/>), so <see cref="Patches.AirfieldTaxiPatch"/> can tell one
    /// by a component, without allocating, instead of reading its parents' names each physics step:
    /// <c>Object.name</c> makes a new string on every read, and the patch runs for every wheel of every
    /// AI aircraft taxiing on the grass, fifty times a second. Added on the prefab by
    /// <c>MapFixups.GroundAirfieldFloors</c>, so every instance has it.
    /// </summary>
    internal sealed class AirfieldFloorMark : MonoBehaviour
    {
    }
}
