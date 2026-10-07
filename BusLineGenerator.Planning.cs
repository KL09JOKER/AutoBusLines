// AutoBusLines v0.2.0 - Tour planning layer
//
// Everything in here is pure planning: it decides WHICH stops go on WHICH line and in what
// ORDER. The game's own RoutePathSystem then pathfinds between consecutive stops, so the
// quality of these tours is what decides whether buses drive sensible routes.
//
// Changes vs. the old inline "Step 3" in OnUpdate (v0.1.x):
//   * Stop direction is derived from which side of the road the stop sits on (or the road's
//     one-way flow), NOT from the stop's rotation. Vanilla roadside objects face the road
//     (perpendicular to traffic), so rotation said nothing about travel direction.
//   * FindShortestRoadPath is a proper edge-state search, so the "no hairpin U-turn" rule can
//     no longer hide valid paths, and dead ends (where the game does allow a U-turn) work.
//   * Every candidate loop goes through ValidateCycle: legal one-way direction, contiguous,
//     no illegal hairpin, and <= maxRouteLength.
//   * Phase A orients the second corridor against the first (it always assumed "reversed").
//   * Phase B plans a real turnaround at corridor ends instead of an implicit U-turn.
//   * Phases D/F insert stops where they cost the least detour instead of "after the nearest stop".
//   * Phase C looks at candidates nearest-first and stops early (was O(N^2) path searches).
//   * Diagnostics: a per-run summary plus a warning for every tour with an unreachable leg.

using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Unity.Entities;
using Unity.Mathematics;
using Edge = Game.Net.Edge;

namespace AutoBusLines
{
    public partial class BusLineGenerator
    {
        private const float HAIRPIN_COS = -0.80f;   // turning sharper than ~143 degrees counts as a U-turn

        // Node -> number of distinct road edges touching it (filled by BuildDirectedRoadGraph).
        private Dictionary<Entity, int> _nodeDegree = new Dictionary<Entity, int>();

        private struct SearchState
        {
            public DirectedRoadEdge Edge;
            public float G;
            public int Prev;
            public bool Closed;
        }

        private const float MAX_AVG_STOP_SPACING = 1500f;   // a trunk line must average at least one stop per this many metres of loop
        private const float FEEDER_CLOSING_LIMIT = 4000f;   // longest stop-less drive allowed to close a feeder loop
        private const int LASTMILE_CANDIDATES = 8;        // insertion points re-checked against the real road graph
        private const float LASTMILE_MAX_DETOUR = 2500f;  // never wedge a stop in if it costs the line more than this

        private struct InsertionCandidate
        {
            public int Tour;
            public int Pos;
            public float Cost;
        }

        private struct StopCandidate
        {
            public float Bound;
            public int Index;
        }

        private class PlanContext
        {
            public Dictionary<Entity, List<DirectedRoadEdge>> NodeOutEdges;
            public Dictionary<Entity, float3> NodePositions;
            public Dictionary<Entity, List<PlacedStop>> EdgeForwardStops;
            public Dictionary<Entity, List<PlacedStop>> EdgeBackwardStops;
            public List<PlacedStop> Stops;
            public List<RoadCorridor> Corridors;
            public List<List<PlacedStop>> Tours = new List<List<PlacedStop>>();
            public HashSet<Entity> Served = new HashSet<Entity>();
            public HashSet<Entity> FailedSeeds = new HashSet<Entity>();
            public HashSet<int> UsedCorridors = new HashSet<int>();
            public int MinStops;
            public int MaxStops;
            public float MaxRouteLength;

            // Per-stop caches, indexed like Stops
            public Dictionary<Entity, int> StopIndex = new Dictionary<Entity, int>();
            public bool[] HasEdge;
            public bool[] ServesForward;
            public float[] AlongTravel;       // 0..1 position along the stop's travel direction on its edge
            public DirectedRoadEdge[] StopEdge;
            public float3[] StopFromPos;
        }

        // ------------------------------------------------------------------
        // Small helpers
        // ------------------------------------------------------------------

        private bool IsDeadEndNode(Entity node)
        {
            return _nodeDegree.TryGetValue(node, out int d) && d <= 1;
        }

        private static bool IsHairpin(float3 headingIn, float3 headingOut)
        {
            if (math.lengthsq(headingIn) < 0.001f || math.lengthsq(headingOut) < 0.001f)
                return false;
            return math.dot(headingIn, headingOut) < HAIRPIN_COS;
        }

        // A bus may turn from headingIn to headingOut at this node unless that is a hairpin U-turn.
        // Dead ends are the exception: the game turns vehicles around there.
        private bool CanTurn(Entity node, float3 headingIn, float3 headingOut)
        {
            return !IsHairpin(headingIn, headingOut) || IsDeadEndNode(node);
        }

        private bool IsLeftHandTraffic()
        {
            var cityConfig = World.GetExistingSystemManaged<Game.City.CityConfigurationSystem>();
            return cityConfig != null && cityConfig.leftHandTraffic;
        }

        // True if the stop serves traffic travelling edge.m_Start -> edge.m_End.
        // Uses the stop's position (which side of the centreline it sits on) - exactly what the
        // game's lane matching does - and the road's flow on one-way roads. The stop's rotation is
        // deliberately NOT used: vanilla roadside stops face the road, perpendicular to traffic.
        private bool StopServesEdgeForward(PlacedStop stop)
        {
            var roadEnt = stop.RoadEntity;
            if (roadEnt != Entity.Null && EntityManager.HasComponent<Curve>(roadEnt))
            {
                var curve = EntityManager.GetComponentData<Curve>(roadEnt);

                if (IsOneWayRoad(roadEnt, out bool flowForward))
                    return flowForward;

                MathUtils.Distance(curve.m_Bezier, stop.Position, out float t);
                float3 centre = MathUtils.Position(curve.m_Bezier, t);
                float3 tan = MathUtils.Tangent(curve.m_Bezier, t);
                tan = math.normalizesafe(new float3(tan.x, 0f, tan.z), new float3(0, 0, 1));
                float3 offset = stop.Position - centre;
                offset.y = 0f;
                float side = math.dot(offset, new float3(tan.z, 0f, -tan.x)); // > 0: right of travel along start->end
                if (math.abs(side) > 0.5f)
                    return IsLeftHandTraffic() ? side < 0f : side > 0f;

                // Stop sits (almost) on the centreline: fall back to its heading
                float3 roadTan = math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 0.5f), new float3(0, 0, 1));
                return math.dot(stop.Forward, roadTan) >= 0f;
            }
            return true;
        }

        private bool TryGetStopDirectedEdge(PlacedStop stop, bool servesForward, out DirectedRoadEdge dEdge)
        {
            dEdge = default(DirectedRoadEdge);
            var roadEnt = stop.RoadEntity;
            if (roadEnt == Entity.Null || !EntityManager.HasComponent<Edge>(roadEnt) || !EntityManager.HasComponent<Curve>(roadEnt))
                return false;

            var edge = EntityManager.GetComponentData<Edge>(roadEnt);
            var curve = EntityManager.GetComponentData<Curve>(roadEnt);
            if (edge.m_Start == Entity.Null || edge.m_End == Entity.Null)
                return false;

            float3 tan0 = math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 0f), new float3(0, 0, 1));
            float3 tan1 = math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 1f), new float3(0, 0, 1));

            dEdge = new DirectedRoadEdge
            {
                EdgeEntity = roadEnt,
                FromNode = servesForward ? edge.m_Start : edge.m_End,
                ToNode = servesForward ? edge.m_End : edge.m_Start,
                Length = MathUtils.Length(curve.m_Bezier),
                TangentAtFrom = servesForward ? tan0 : -tan1,
                TangentAtTo = servesForward ? tan1 : -tan0
            };
            return true;
        }

        private static float PathLength(List<DirectedRoadEdge> path)
        {
            float len = 0f;
            for (int i = 0; i < path.Count; i++)
                len += path[i].Length;
            return len;
        }

        // ------------------------------------------------------------------
        // Path search (edge-state A*) and cycle validation
        // ------------------------------------------------------------------

        private class IntMinHeap
        {
            private struct Item { public int Id; public float Priority; }
            private readonly List<Item> _items = new List<Item>();
            public int Count { get { return _items.Count; } }

            public void Enqueue(int id, float priority)
            {
                _items.Add(new Item { Id = id, Priority = priority });
                int c = _items.Count - 1;
                while (c > 0)
                {
                    int p = (c - 1) / 2;
                    if (_items[c].Priority >= _items[p].Priority) break;
                    var tmp = _items[c]; _items[c] = _items[p]; _items[p] = tmp;
                    c = p;
                }
            }

            public int Dequeue()
            {
                int result = _items[0].Id;
                int last = _items.Count - 1;
                _items[0] = _items[last];
                _items.RemoveAt(last);
                int p = 0;
                while (true)
                {
                    int l = 2 * p + 1, r = 2 * p + 2, s = p;
                    if (l < _items.Count && _items[l].Priority < _items[s].Priority) s = l;
                    if (r < _items.Count && _items[r].Priority < _items[s].Priority) s = r;
                    if (s == p) break;
                    var tmp = _items[p]; _items[p] = _items[s]; _items[s] = tmp;
                    p = s;
                }
                return result;
            }
        }

        private static long EdgeStateKey(DirectedRoadEdge e)
        {
            return ((long)e.EdgeEntity.Index << 32) ^ (uint)e.FromNode.Index;
        }

        /// <summary>
        /// Shortest legal drive from startNode (arriving with initialHeading) to endNode.
        /// The search state is the directed edge, so turn restrictions are exact.
        /// - startNode == endNode returns an empty path when no turnaround is needed; if the
        ///   required exitHeading would be a hairpin there, it searches for a real loop instead.
        /// - exitHeading (optional): the heading of the edge that will be taken after arriving.
        /// Returns null when no legal path exists within maxSearchDistance.
        /// </summary>
        private List<DirectedRoadEdge> FindShortestRoadPath(
            Entity startNode,
            Entity endNode,
            float3 initialHeading,
            Dictionary<Entity, List<DirectedRoadEdge>> nodeOutEdges,
            Dictionary<Entity, float3> nodePositions,
            float maxSearchDistance = 2000.0f,
            bool requireNonEmpty = false,
            float3 exitHeading = default(float3))
        {
            if (startNode == Entity.Null || endNode == Entity.Null)
                return null;

            bool hasExit = math.lengthsq(exitHeading) > 0.001f;

            if (startNode == endNode && !requireNonEmpty)
            {
                if (!hasExit || CanTurn(startNode, initialHeading, exitHeading))
                    return new List<DirectedRoadEdge>();
                // otherwise fall through: we need an actual loop back to this node
            }

            List<DirectedRoadEdge> startEdges;
            if (!nodeOutEdges.TryGetValue(startNode, out startEdges))
                return null;

            float3 targetPos;
            if (!nodePositions.TryGetValue(endNode, out targetPos))
                targetPos = float3.zero;

            var states = new List<SearchState>();
            var index = new Dictionary<long, int>();
            var heap = new IntMinHeap();

            Action<DirectedRoadEdge, float, int> relax = (edge, g, prev) =>
            {
                long key = EdgeStateKey(edge);
                float h = 0f;
                float3 np;
                if (nodePositions.TryGetValue(edge.ToNode, out np))
                    h = math.distance(np, targetPos);

                int id;
                if (index.TryGetValue(key, out id))
                {
                    var existing = states[id];
                    if (existing.Closed || g >= existing.G)
                        return;
                    existing.G = g;
                    existing.Prev = prev;
                    states[id] = existing;
                }
                else
                {
                    id = states.Count;
                    index[key] = id;
                    states.Add(new SearchState { Edge = edge, G = g, Prev = prev, Closed = false });
                }
                heap.Enqueue(id, g + h);
            };

            for (int i = 0; i < startEdges.Count; i++)
            {
                var e = startEdges[i];
                if (!CanTurn(startNode, initialHeading, e.TangentAtFrom))
                    continue;
                relax(e, e.Length, -1);
            }

            int goal = -1;
            while (heap.Count > 0)
            {
                int id = heap.Dequeue();
                var st = states[id];
                if (st.Closed)
                    continue;
                st.Closed = true;
                states[id] = st;

                var cur = st.Edge;
                if (cur.ToNode == endNode && (!hasExit || CanTurn(endNode, cur.TangentAtTo, exitHeading)))
                {
                    goal = id;
                    break;
                }

                if (st.G > maxSearchDistance)
                    continue;

                List<DirectedRoadEdge> outs;
                if (!nodeOutEdges.TryGetValue(cur.ToNode, out outs))
                    continue;

                for (int i = 0; i < outs.Count; i++)
                {
                    var next = outs[i];
                    if (!CanTurn(cur.ToNode, cur.TangentAtTo, next.TangentAtFrom))
                        continue;
                    relax(next, st.G + next.Length, id);
                }
            }

            if (goal < 0)
                return null;

            var path = new List<DirectedRoadEdge>();
            for (int s = goal; s >= 0; s = states[s].Prev)
                path.Add(states[s].Edge);
            path.Reverse();
            return path;
        }

        private static bool IsLegalDirectedEdge(DirectedRoadEdge e, Dictionary<Entity, List<DirectedRoadEdge>> nodeOutEdges)
        {
            List<DirectedRoadEdge> outs;
            if (!nodeOutEdges.TryGetValue(e.FromNode, out outs))
                return false;
            for (int i = 0; i < outs.Count; i++)
            {
                if (outs[i].EdgeEntity == e.EdgeEntity && outs[i].ToNode == e.ToNode)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// A cycle is only usable if every edge is driven in a legal direction, the edges join up
        /// (and close), no joint is an illegal hairpin, and the loop respects the length cap.
        /// </summary>
        private bool ValidateCycle(List<DirectedRoadEdge> cycle, Dictionary<Entity, List<DirectedRoadEdge>> nodeOutEdges, out float totalLength, float maxRouteLength)
        {
            totalLength = 0f;
            int n = cycle.Count;
            if (n == 0)
                return false;

            for (int i = 0; i < n; i++)
            {
                var e = cycle[i];
                totalLength += e.Length;
                if (!IsLegalDirectedEdge(e, nodeOutEdges))
                    return false;

                var next = cycle[(i + 1) % n];
                if (e.ToNode != next.FromNode)
                    return false;
                if (!CanTurn(e.ToNode, e.TangentAtTo, next.TangentAtFrom))
                    return false;
            }
            return totalLength <= maxRouteLength;
        }

        private static List<DirectedRoadEdge> Concat(params List<DirectedRoadEdge>[] parts)
        {
            var all = new List<DirectedRoadEdge>();
            for (int i = 0; i < parts.Length; i++)
                all.AddRange(parts[i]);
            return all;
        }

        // ------------------------------------------------------------------
        // Planning entry point
        // ------------------------------------------------------------------

        private List<List<PlacedStop>> PlanTours(
            List<PlacedStop> allGlobalStops,
            List<RoadCorridor> allCorridors,
            int minStopsPerLine,
            int maxStopsPerLine,
            float maxRouteLength,
            HashSet<Entity> alreadyCoveredStops,
            out HashSet<Entity> servedStopEntities)
        {
            var ctx = new PlanContext
            {
                Stops = allGlobalStops,
                Corridors = allCorridors,
                MinStops = minStopsPerLine,
                MaxStops = maxStopsPerLine,
                MaxRouteLength = maxRouteLength
            };

            // Pre-seed ctx.Served with already covered curbside stops
            if (alreadyCoveredStops != null)
            {
                for (int i = 0; i < allGlobalStops.Count; i++)
                {
                    var st = allGlobalStops[i];
                    if (!st.IsStationBay && alreadyCoveredStops.Contains(st.StopEntity))
                    {
                        ctx.Served.Add(st.StopEntity);
                    }
                }
            }

            BuildDirectedRoadGraph(out ctx.NodeOutEdges, out ctx.NodePositions);
            IndexStopsByDirectedEdge(allGlobalStops, out ctx.EdgeForwardStops, out ctx.EdgeBackwardStops);
            PrecomputeStopInfo(ctx);

            PlanStationHubLoops(ctx);
            PlanTrunkCouplets(ctx);
            PlanSingleCorridorLoops(ctx);
            PlanFeederLoops(ctx);
            PlanDirectInsertions(ctx);
            PlanLastMileInsertions(ctx);
            PlanResidualLoops(ctx);
            ConsolidateSmallTours(ctx);
            PlanUnservedClusters(ctx);

            // Fallback: create cluster tours for unserved stops so that isolated areas across bridges are never left out
            var remainingUnserved = new List<PlacedStop>();
            for (int i = 0; i < allGlobalStops.Count; i++)
            {
                if (!allGlobalStops[i].IsStationBay && !ctx.Served.Contains(allGlobalStops[i].StopEntity))
                    remainingUnserved.Add(allGlobalStops[i]);
            }

            if (remainingUnserved.Count >= ABSOLUTE_MIN_STOPS)
            {
                var fallbackClusters = ClusterStopsByDistance(remainingUnserved, 2500f);
                for (int c = 0; c < fallbackClusters.Count; c++)
                {
                    var clusterStops = fallbackClusters[c];
                    if (clusterStops.Count >= ABSOLUTE_MIN_STOPS)
                    {
                        if (clusterStops.Count > maxStopsPerLine)
                            clusterStops = SubsampleTour(clusterStops, maxStopsPerLine);
                        ctx.Tours.Add(clusterStops);
                        for (int t = 0; t < clusterStops.Count; t++)
                            ctx.Served.Add(clusterStops[t].StopEntity);
                        log.Info($"Fallback Tour Created: Added fallback tour #{ctx.Tours.Count} with {clusterStops.Count} stops for isolated/unserved cluster.");
                    }
                }
            }

            DiagnoseTours(ctx);

            servedStopEntities = ctx.Served;
            return ctx.Tours;
        }

        private void PrecomputeStopInfo(PlanContext ctx)
        {
            int n = ctx.Stops.Count;
            ctx.HasEdge = new bool[n];
            ctx.ServesForward = new bool[n];
            ctx.AlongTravel = new float[n];
            ctx.StopEdge = new DirectedRoadEdge[n];
            ctx.StopFromPos = new float3[n];

            for (int i = 0; i < n; i++)
            {
                var stop = ctx.Stops[i];
                ctx.StopIndex[stop.StopEntity] = i;

                bool fwd = StopServesEdgeForward(stop);
                ctx.ServesForward[i] = fwd;

                DirectedRoadEdge dEdge;
                if (!TryGetStopDirectedEdge(stop, fwd, out dEdge))
                    continue;

                ctx.HasEdge[i] = true;
                ctx.StopEdge[i] = dEdge;

                var curve = EntityManager.GetComponentData<Curve>(stop.RoadEntity);
                MathUtils.Distance(curve.m_Bezier, stop.Position, out float t);
                ctx.AlongTravel[i] = fwd ? t : 1f - t;

                float3 fp;
                ctx.StopFromPos[i] = ctx.NodePositions.TryGetValue(dEdge.FromNode, out fp) ? fp : stop.Position;
            }
        }

        private void AcceptTour(PlanContext ctx, List<PlacedStop> tour)
        {
            ctx.Tours.Add(tour);
            for (int t = 0; t < tour.Count; t++)
                ctx.Served.Add(tour[t].StopEntity);
        }

        // Number of stops (either side of the road) on a corridor's edges - a cheap upper bound on what a loop through it can serve.
        private int CorridorStopCount(PlanContext ctx, RoadCorridor corr)
        {
            int count = 0;
            for (int i = 0; i < corr.Segments.Count; i++)
            {
                var road = corr.Segments[i].RoadEntity;
                List<PlacedStop> list;
                if (ctx.EdgeForwardStops.TryGetValue(road, out list)) count += list.Count;
                if (ctx.EdgeBackwardStops.TryGetValue(road, out list)) count += list.Count;
            }
            return count;
        }

        // Count only the unserved stops along a corridor
        private int CorridorUnservedStopCount(PlanContext ctx, RoadCorridor corr)
        {
            int count = 0;
            for (int i = 0; i < corr.Segments.Count; i++)
            {
                var road = corr.Segments[i].RoadEntity;
                if (ctx.EdgeForwardStops.TryGetValue(road, out var listFwd))
                {
                    for (int k = 0; k < listFwd.Count; k++)
                        if (!ctx.Served.Contains(listFwd[k].StopEntity)) count++;
                }
                if (ctx.EdgeBackwardStops.TryGetValue(road, out var listRev))
                {
                    for (int k = 0; k < listRev.Count; k++)
                        if (!ctx.Served.Contains(listRev[k].StopEntity)) count++;
                }
            }
            return count;
        }

        // Straight-line distance between two graph nodes; a lower bound on any drive between them.
        private float NodeDistance(PlanContext ctx, Entity a, Entity b)
        {
            float3 pa, pb;
            if (!ctx.NodePositions.TryGetValue(a, out pa) || !ctx.NodePositions.TryGetValue(b, out pb))
                return 0f;
            return math.distance(pa, pb);
        }

        // ------------------------------------------------------------------
        // Phase 0: Bus Station Terminal Lines
        // Every passenger Bus Station gets dedicated neighborhood lines originating from platform bays
        // ------------------------------------------------------------------

        private void PlanStationHubLoops(PlanContext ctx)
        {
            if (_depotFinder == null || _depotFinder.BusStations.Length == 0)
                return;

            int stationCount = _depotFinder.BusStations.Length;
            log.Info($"PlanStationHubLoops: Planning dedicated bus terminal lines for {stationCount} bus stations...");

            int totalStationLines = 0;
            int totalStationsServed = 0;

            for (int s = 0; s < stationCount; s++)
            {
                var stationEntity = _depotFinder.BusStations[s];
                if (!_depotFinder.StationToPlatforms.TryGetValue(stationEntity, out var platforms) || platforms.Count == 0)
                    continue;

                // Collect valid platform bay stops
                var platformStops = new List<PlacedStop>();
                for (int p = 0; p < platforms.Count; p++)
                {
                    var pEntity = platforms[p];
                    if (!EntityManager.Exists(pEntity) || !EntityManager.HasComponent<Game.Objects.Transform>(pEntity))
                        continue;

                    var tr = EntityManager.GetComponentData<Game.Objects.Transform>(pEntity);
                    float3 forward = math.mul(tr.m_Rotation, new float3(0, 0, 1));

                    Entity attachedRoad = Entity.Null;
                    if (EntityManager.HasComponent<Game.Objects.Attached>(pEntity))
                        attachedRoad = EntityManager.GetComponentData<Game.Objects.Attached>(pEntity).m_Parent;
                    else if (EntityManager.HasComponent<Game.Common.Owner>(pEntity))
                        attachedRoad = EntityManager.GetComponentData<Game.Common.Owner>(pEntity).m_Owner;

                    platformStops.Add(new PlacedStop
                    {
                        StopEntity = pEntity,
                        Position = tr.m_Position,
                        Forward = forward,
                        RoadEntity = attachedRoad,
                        HubEntity = stationEntity,
                        IsOutbound = true,
                        IsStationBay = true,
                        IsPreExisting = true
                    });
                }

                if (platformStops.Count == 0)
                    continue;

                float3 stationPos = platformStops[0].Position;
                if (EntityManager.HasComponent<Game.Objects.Transform>(stationEntity))
                {
                    stationPos = EntityManager.GetComponentData<Game.Objects.Transform>(stationEntity).m_Position;
                }

                // 1. Gather unserved candidate curbside stops within maxStationRadius of this station
                float maxStationRadius = math.min(2500f, ctx.MaxRouteLength * 0.30f);
                var candidateStops = new List<PlacedStop>();
                var candidateSet = new HashSet<Entity>();

                for (int i = 0; i < ctx.Stops.Count; i++)
                {
                    var stop = ctx.Stops[i];
                    if (ctx.Served.Contains(stop.StopEntity) || candidateSet.Contains(stop.StopEntity))
                        continue;
                    if (!ctx.HasEdge[i])
                        continue;

                    float dist = math.distance(stop.Position, stationPos);
                    if (dist <= maxStationRadius)
                    {
                        candidateStops.Add(stop);
                        candidateSet.Add(stop.StopEntity);
                    }
                }

                // Suppress any curbside stop that is within 80m of the station (right in front of the platform/driveway)
                for (int i = candidateStops.Count - 1; i >= 0; i--)
                {
                    var cs = candidateStops[i];
                    if (math.distance(cs.Position, stationPos) < 80f)
                    {
                        ctx.Served.Add(cs.StopEntity);
                        candidateSet.Remove(cs.StopEntity);
                        candidateStops.RemoveAt(i);
                        log.Info($"PlanStationHubLoops: Suppressed redundant curbside stop {cs.StopEntity.Index} within 80m of Bus Station {stationEntity.Index}.");
                    }
                }

                if (candidateStops.Count < 3)
                {
                    log.Info($"PlanStationHubLoops: Bus Station {stationEntity.Index} at {stationPos} has only {candidateStops.Count} nearby stops within {maxStationRadius:F0}m; skipping dedicated lines.");
                    continue;
                }

                // Sort candidate stops by proximity to the station
                candidateStops.Sort((a, b) => math.distance(a.Position, stationPos).CompareTo(math.distance(b.Position, stationPos)));

                int bayIndex = 0;
                int stationLinesCreated = 0;

                while (candidateStops.Count >= 3 && bayIndex < platformStops.Count)
                {
                    var bay = platformStops[bayIndex];
                    bayIndex++;

                    // Sort remaining candidates by proximity to the station
                    candidateStops.Sort((a, b) => math.distance(a.Position, stationPos).CompareTo(math.distance(b.Position, stationPos)));

                    var tour = new List<PlacedStop>();
                    tour.Add(bay);

                    var tourSet = new HashSet<Entity>();

                    // Pick the closest unserved candidate stop as the first stop after leaving the station
                    var firstStop = candidateStops[0];
                    tour.Add(firstStop);
                    tourSet.Add(firstStop.StopEntity);

                    int currentCtxIdx = ctx.StopIndex[firstStop.StopEntity];
                    float3 currentPos = firstStop.Position;
                    float totalRoadDist = math.distance(bay.Position, firstStop.Position) * 1.35f;

                    int maxStopsForThisTour = math.min(ctx.MaxStops - 1, candidateStops.Count);

                    for (int step = 1; step < maxStopsForThisTour; step++)
                    {
                        int bestIdx = -1;
                        float bestLegDist = float.MaxValue;

                        for (int c = 0; c < candidateStops.Count; c++)
                        {
                            var cand = candidateStops[c];
                            if (tourSet.Contains(cand.StopEntity))
                                continue;

                            float straightDist = math.distance(currentPos, cand.Position);
                            if (straightDist > 2000f || straightDist >= bestLegDist)
                                continue;

                            if (!ctx.StopIndex.TryGetValue(cand.StopEntity, out int candCtxIdx))
                                continue;

                            // Calculate real driving distance along the road network in the legal travel direction
                            if (!TryLegLength(ctx, currentCtxIdx, candCtxIdx, 2500f, out float legDist))
                                continue;

                            if (legDist < bestLegDist)
                            {
                                bestLegDist = legDist;
                                bestIdx = c;
                            }
                        }

                        if (bestIdx < 0)
                            break;

                        var chosen = candidateStops[bestIdx];

                        // Estimated distance from chosen stop back to the station platform bay
                        float returnDistEst = math.distance(chosen.Position, bay.Position) * 1.35f;

                        // Check if adding this stop would push the total route length beyond MaxRouteLength
                        if (tour.Count >= math.max(4, ctx.MinStops) && (totalRoadDist + bestLegDist + returnDistEst > ctx.MaxRouteLength))
                        {
                            break;
                        }

                        tour.Add(chosen);
                        tourSet.Add(chosen.StopEntity);
                        totalRoadDist += bestLegDist;

                        currentPos = chosen.Position;
                        currentCtxIdx = ctx.StopIndex[chosen.StopEntity];
                    }

                    if (tour.Count >= 4)
                    {
                        // Calculate final total road length (including return to station)
                        float finalLength = totalRoadDist + math.distance(currentPos, bay.Position) * 1.35f;

                        AcceptTour(ctx, tour);
                        ctx.Served.Add(bay.StopEntity);

                        // Remove chosen stops from candidate pool
                        candidateStops.RemoveAll(s => tourSet.Contains(s.StopEntity));

                        stationLinesCreated++;
                        totalStationLines++;
                        log.Info($"PlanStationHubLoops: Created Station Line #{ctx.Tours.Count} for Bus Station {stationEntity.Index} with {tour.Count} stops (Bay {bay.StopEntity.Index} as Origin/Terminal, Est. Road Length: {finalLength / 1000f:F1} km).");
                    }
                    else
                    {
                        break;
                    }
                }

                if (stationLinesCreated > 0)
                    totalStationsServed++;
            }

            log.Info($"PlanStationHubLoops Complete: Successfully planned {totalStationLines} bus terminal lines across {totalStationsServed} of {stationCount} bus stations.");
        }

        // ------------------------------------------------------------------
        // Phase A: paired arterial couplets (two parallel corridors joined into one loop)
        // ------------------------------------------------------------------

        private void PlanTrunkCouplets(PlanContext ctx)
        {
            var used = new HashSet<int>();
            var corridors = ctx.Corridors;
            int minPairStops = math.clamp(ctx.MinStops, 3, 6);

            var stopCounts = new int[corridors.Count];
            for (int c = 0; c < corridors.Count; c++)
                stopCounts[c] = corridors[c].Segments.Count > 0 ? CorridorStopCount(ctx, corridors[c]) : 0;

            for (int i = 0; i < corridors.Count; i++)
            {
                if (used.Contains(i))
                    continue;

                var corrA = corridors[i];
                if (corrA.TotalLength < 250.0f || corrA.Segments.Count == 0)
                    continue;

                float3 pStartA = GetCorridorEndpoint(corrA, true);
                float3 pEndA = GetCorridorEndpoint(corrA, false);
                float3 dirA = math.normalizesafe(new float3(pEndA.x - pStartA.x, 0f, pEndA.z - pStartA.z));
                float3 midA = (pStartA + pEndA) * 0.5f;

                // Candidate partners: roughly parallel, 60-500 m to the side. Nearest first.
                var partners = new List<StopCandidate>();
                for (int j = i + 1; j < corridors.Count; j++)
                {
                    if (used.Contains(j))
                        continue;

                    var corrB = corridors[j];
                    if (corrB.TotalLength < 250.0f || corrB.Segments.Count == 0)
                        continue;

                    // Even serving every stop on both corridors could not make a line
                    if (stopCounts[i] + stopCounts[j] < minPairStops)
                        continue;

                    // Skip pairing if both corridors are already fully served
                    if (CorridorUnservedStopCount(ctx, corrA) == 0 && CorridorUnservedStopCount(ctx, corrB) == 0)
                        continue;

                    float3 pStartB = GetCorridorEndpoint(corrB, true);
                    float3 pEndB = GetCorridorEndpoint(corrB, false);
                    float3 dirB = math.normalizesafe(new float3(pEndB.x - pStartB.x, 0f, pEndB.z - pStartB.z));
                    float3 midB = (pStartB + pEndB) * 0.5f;

                    if (math.abs(math.dot(dirA, dirB)) < 0.70f)
                        continue;

                    float3 delta = midB - midA;
                    float3 latVec = delta - dirA * math.dot(delta, dirA);
                    float latDist = math.length(new float2(latVec.x, latVec.z));
                    if (latDist >= 60.0f && latDist <= 500.0f)
                        partners.Add(new StopCandidate { Bound = latDist, Index = j });
                }
                partners.Sort((a, b) => a.Bound.CompareTo(b.Bound));

                int tries = math.min(partners.Count, 4);
                for (int p = 0; p < tries; p++)
                {
                    int j = partners[p].Index;
                    var corrB = corridors[j];
                    float3 pStartB = GetCorridorEndpoint(corrB, true);
                    float3 pEndB = GetCorridorEndpoint(corrB, false);
                    float3 dirB = math.normalizesafe(new float3(pEndB.x - pStartB.x, 0f, pEndB.z - pStartB.z));

                    List<DirectedRoadEdge> bestCycle = null;
                    float bestLen = float.MaxValue;

                    // Try driving A both ways. B is always driven AGAINST A (that is what makes it a couplet),
                    // whichever way B's own chain happens to be stored. One-way streets reject the wrong
                    // orientation in ValidateCycle.
                    for (int orient = 0; orient < 2; orient++)
                    {
                        bool aFwd = (orient == 0);
                        float3 headingA = aFwd ? dirA : -dirA;
                        bool bFwd = math.dot(dirB, -headingA) >= 0f;

                        var chainA = GetCorridorDirectedEdges(corrA, aFwd);
                        var chainB = GetCorridorDirectedEdges(corrB, bFwd);
                        if (chainA.Count == 0 || chainB.Count == 0)
                            continue;

                        var lastA = chainA[chainA.Count - 1];
                        var firstB = chainB[0];
                        var lastB = chainB[chainB.Count - 1];
                        var firstA = chainA[0];

                        // The cross streets must be short; skip hopeless pairs before any path search
                        if (NodeDistance(ctx, lastA.ToNode, firstB.FromNode) > 2000f || NodeDistance(ctx, lastB.ToNode, firstA.FromNode) > 2000f)
                            continue;

                        var cross1 = FindShortestRoadPath(lastA.ToNode, firstB.FromNode, lastA.TangentAtTo, ctx.NodeOutEdges, ctx.NodePositions, 2000f, false, firstB.TangentAtFrom);
                        var cross2 = FindShortestRoadPath(lastB.ToNode, firstA.FromNode, lastB.TangentAtTo, ctx.NodeOutEdges, ctx.NodePositions, 2000f, false, firstA.TangentAtFrom);
                        if (cross1 == null || cross2 == null)
                            continue;

                        var cycle = Concat(chainA, cross1, chainB, cross2);
                        float len;
                        if (!ValidateCycle(cycle, ctx.NodeOutEdges, out len, ctx.MaxRouteLength))
                            continue;

                        if (len < bestLen)
                        {
                            bestLen = len;
                            bestCycle = cycle;
                        }
                    }

                    if (bestCycle == null)
                        continue;

                    var tour = BuildTourFromRoadCycle(bestCycle, ctx.EdgeForwardStops, ctx.EdgeBackwardStops, ctx.MaxStops);
                    // Stops sit on one kerb per direction, so a couplet can end up with very few stops for its length.
                    // Such a pair is left to the other phases rather than becoming a long line with huge gaps.
                    if (tour.Count >= minPairStops && bestLen / tour.Count <= MAX_AVG_STOP_SPACING)
                    {
                        AcceptTour(ctx, tour);
                        used.Add(i);
                        used.Add(j);
                        log.Info($"Built Paired Arterial Trunk Line #{ctx.Tours.Count} ({corrA.TotalLength:F0}m & {corridors[j].TotalLength:F0}m, {tour.Count} stops, loop {bestLen:F0}m, lateral sep: {partners[p].Bound:F0}m)");
                        break;
                    }
                }
            }

            ctx.UsedCorridors = used;
        }

        // ------------------------------------------------------------------
        // Phase B: out-and-back loops along a single two-way corridor
        // ------------------------------------------------------------------

        private void PlanSingleCorridorLoops(PlanContext ctx)
        {
            var used = ctx.UsedCorridors;
            var corridors = ctx.Corridors;

            for (int i = 0; i < corridors.Count; i++)
            {
                if (used.Contains(i))
                    continue;

                var corr = corridors[i];
                if (corr.TotalLength < 180.0f || corr.Segments.Count == 0)
                    continue;

                // Skip corridor if all its stops are already served
                if (CorridorUnservedStopCount(ctx, corr) == 0)
                    continue;

                // Never drive backwards on one-way streets
                bool isTwoWay = true;
                for (int s = 0; s < corr.Segments.Count; s++)
                {
                    if (IsOneWayRoad(corr.Segments[s].RoadEntity, out _))
                    {
                        isTwoWay = false;
                        break;
                    }
                }
                if (!isTwoWay)
                    continue;

                var chainFwd = GetCorridorDirectedEdges(corr, true);
                var chainRev = GetCorridorDirectedEdges(corr, false);
                if (chainFwd.Count == 0 || chainRev.Count == 0)
                    continue;

                var lastFwd = chainFwd[chainFwd.Count - 1];
                var firstRev = chainRev[0];
                var lastRev = chainRev[chainRev.Count - 1];
                var firstFwd = chainFwd[0];

                // Turnarounds: at a dead end the bus turns in place; elsewhere it needs a real loop
                var endTurn = FindShortestRoadPath(lastFwd.ToNode, firstRev.FromNode, lastFwd.TangentAtTo, ctx.NodeOutEdges, ctx.NodePositions, 2500f, false, firstRev.TangentAtFrom);
                var startTurn = FindShortestRoadPath(lastRev.ToNode, firstFwd.FromNode, lastRev.TangentAtTo, ctx.NodeOutEdges, ctx.NodePositions, 2500f, false, firstFwd.TangentAtFrom);
                if (endTurn == null || startTurn == null)
                    continue;

                var cycle = Concat(chainFwd, endTurn, chainRev, startTurn);
                float len;
                if (!ValidateCycle(cycle, ctx.NodeOutEdges, out len, ctx.MaxRouteLength))
                    continue;

                var tour = BuildTourFromRoadCycle(cycle, ctx.EdgeForwardStops, ctx.EdgeBackwardStops, ctx.MaxStops);
                // Corridors with only a stop or two are better chained into neighbourhood loops (Phase C)
                // than given a 2-stop line of their own.
                if (tour.Count >= math.clamp(ctx.MinStops, 3, 6) && len / tour.Count <= MAX_AVG_STOP_SPACING)
                {
                    AcceptTour(ctx, tour);
                    used.Add(i);
                    log.Info($"Built Single-Corridor Arterial Line #{ctx.Tours.Count} ({corr.TotalLength:F0}m, {tour.Count} stops, loop {len:F0}m)");
                }
            }
        }

        // ------------------------------------------------------------------
        // Phase C: neighbourhood feeder loops (nearest-next-stop chains that close back to the start)
        // ------------------------------------------------------------------

        private void PlanFeederLoops(PlanContext ctx)
        {
            var stops = ctx.Stops;
            int maxChain = math.clamp(ctx.MaxStops, 8, 16);

            for (int s = 0; s < stops.Count; s++)
            {
                var seed = stops[s];
                if (ctx.Served.Contains(seed.StopEntity) || ctx.FailedSeeds.Contains(seed.StopEntity))
                    continue;
                if (!ctx.HasEdge[s])
                {
                    ctx.FailedSeeds.Add(seed.StopEntity);
                    continue;
                }

                var seedD = ctx.StopEdge[s];
                float3 seedFromPos = ctx.StopFromPos[s];

                var legs = new List<List<DirectedRoadEdge>>();
                legs.Add(new List<DirectedRoadEdge> { seedD });
                var legStopIds = new List<Entity> { seed.StopEntity };
                var chainStops = new HashSet<Entity> { seed.StopEntity };

                float length = seedD.Length;
                Entity currNode = seedD.ToNode;
                float3 currHeading = seedD.TangentAtTo;

                for (int step = 0; step < maxChain - 1; step++)
                {
                    float3 curPos;
                    if (!ctx.NodePositions.TryGetValue(currNode, out curPos))
                        break;

                    // Candidates sorted by straight-line distance to their entry node; that distance is a lower
                    // bound on the drive, so once it exceeds the best path found we can stop looking.
                    var cands = new List<StopCandidate>();
                    for (int c = 0; c < stops.Count; c++)
                    {
                        if (!ctx.HasEdge[c])
                            continue;
                        float lb = math.distance(curPos, ctx.StopFromPos[c]);
                        if (lb > 3000f)
                            continue;
                        var cs = stops[c];
                        if (ctx.Served.Contains(cs.StopEntity) || chainStops.Contains(cs.StopEntity))
                            continue;
                        cands.Add(new StopCandidate { Bound = lb, Index = c });
                    }
                    cands.Sort((a, b) => a.Bound.CompareTo(b.Bound));

                    int bestIdx = -1;
                    float bestCost = float.MaxValue;
                    List<DirectedRoadEdge> bestPath = null;

                    for (int k = 0; k < cands.Count; k++)
                    {
                        if (cands[k].Bound >= bestCost)
                            break;

                        int c = cands[k].Index;
                        var cd = ctx.StopEdge[c];

                        // Keep room in the length budget for this leg plus (at least) the straight way home
                        float3 toPos;
                        float homeLb = ctx.NodePositions.TryGetValue(cd.ToNode, out toPos) ? math.distance(toPos, seedFromPos) : 0f;
                        if (length + cands[k].Bound + cd.Length + homeLb > ctx.MaxRouteLength)
                            continue;

                        var path = FindShortestRoadPath(currNode, cd.FromNode, currHeading, ctx.NodeOutEdges, ctx.NodePositions, 4000f, false, cd.TangentAtFrom);
                        if (path == null)
                            continue;

                        float pathLen = PathLength(path);
                        if (pathLen < bestCost)
                        {
                            bestCost = pathLen;
                            bestIdx = c;
                            bestPath = path;
                        }
                    }

                    if (bestIdx < 0)
                        break;

                    var leg = new List<DirectedRoadEdge>(bestPath);
                    leg.Add(ctx.StopEdge[bestIdx]);
                    legs.Add(leg);
                    legStopIds.Add(stops[bestIdx].StopEntity);
                    chainStops.Add(stops[bestIdx].StopEntity);

                    length += bestCost + ctx.StopEdge[bestIdx].Length;
                    var lastEdge = leg[leg.Count - 1];
                    currNode = lastEdge.ToNode;
                    currHeading = lastEdge.TangentAtTo;
                }

                // Close the loop back to the seed. If that is impossible or too long, drop the last stop and retry.
                float closingLimit = math.clamp(ctx.MaxRouteLength * 0.5f, FEEDER_CLOSING_LIMIT, 6000f);
                List<DirectedRoadEdge> acceptedCycle = null;
                while (true)
                {
                    var returnPath = FindShortestRoadPath(currNode, seedD.FromNode, currHeading, ctx.NodeOutEdges, ctx.NodePositions, closingLimit, false, seedD.TangentAtFrom);
                    if (returnPath != null)
                    {
                        var cycle = new List<DirectedRoadEdge>();
                        for (int l = 0; l < legs.Count; l++)
                            cycle.AddRange(legs[l]);
                        cycle.AddRange(returnPath);

                        float len;
                        if (ValidateCycle(cycle, ctx.NodeOutEdges, out len, ctx.MaxRouteLength))
                        {
                            acceptedCycle = cycle;
                            break;
                        }
                    }

                    if (legs.Count <= 1)
                        break;

                    chainStops.Remove(legStopIds[legStopIds.Count - 1]);
                    legStopIds.RemoveAt(legStopIds.Count - 1);
                    legs.RemoveAt(legs.Count - 1);
                    var prevLeg = legs[legs.Count - 1];
                    currNode = prevLeg[prevLeg.Count - 1].ToNode;
                    currHeading = prevLeg[prevLeg.Count - 1].TangentAtTo;
                }

                if (acceptedCycle == null)
                {
                    ctx.FailedSeeds.Add(seed.StopEntity);
                    continue;
                }

                var tour = BuildTourFromRoadCycle(acceptedCycle, ctx.EdgeForwardStops, ctx.EdgeBackwardStops, ctx.MaxStops);
                int minFeederStops = math.clamp(ctx.MinStops, 3, 6);
                if (tour.Count >= minFeederStops)
                {
                    AcceptTour(ctx, tour);
                    log.Info($"Built Neighborhood Feeder Loop #{ctx.Tours.Count} with {tour.Count} stops.");
                }
                else
                {
                    ctx.FailedSeeds.Add(seed.StopEntity);
                }
            }
        }

        // ------------------------------------------------------------------
        // Phase D: a stop on a road an existing tour already drives -> insert it in driving order
        // ------------------------------------------------------------------

        private int FindSameEdgeInsertIndex(PlanContext ctx, List<PlacedStop> tour, int stopIdx)
        {
            var stop = ctx.Stops[stopIdx];
            bool fwd = ctx.ServesForward[stopIdx];
            float sNew = ctx.AlongTravel[stopIdx];
            int lastMatch = -1;

            for (int i = 0; i < tour.Count; i++)
            {
                var a = tour[i];
                if (a.RoadEntity != stop.RoadEntity)
                    continue;

                int ai;
                if (!ctx.StopIndex.TryGetValue(a.StopEntity, out ai) || ctx.ServesForward[ai] != fwd)
                    continue;

                if (ctx.AlongTravel[ai] > sNew)
                    return i;          // the new stop lies upstream of this one
                lastMatch = i;
            }
            return lastMatch >= 0 ? lastMatch + 1 : -1;
        }

        private void PlanDirectInsertions(PlanContext ctx)
        {
            for (int s = 0; s < ctx.Stops.Count; s++)
            {
                var stop = ctx.Stops[s];
                if (ctx.Served.Contains(stop.StopEntity) || !ctx.HasEdge[s])
                    continue;

                for (int ti = 0; ti < ctx.Tours.Count; ti++)
                {
                    var tour = ctx.Tours[ti];
                    if (tour.Count >= ctx.MaxStops + 2)
                        continue;

                    int at = FindSameEdgeInsertIndex(ctx, tour, s);
                    if (at < 0)
                        continue;

                    tour.Insert(at, stop);
                    ctx.Served.Add(stop.StopEntity);
                    break;
                }
            }
        }

        // ------------------------------------------------------------------
        // Phase E: leftover stops get a tiny loop of their own
        // ------------------------------------------------------------------

        private void PlanResidualLoops(PlanContext ctx)
        {
            for (int s = 0; s < ctx.Stops.Count; s++)
            {
                var seed = ctx.Stops[s];
                if (ctx.Served.Contains(seed.StopEntity) || !ctx.HasEdge[s])
                    continue;

                var seedD = ctx.StopEdge[s];
                float maxReturn = math.clamp(ctx.MaxRouteLength * 0.6f, 5000f, 8000f);
                var returnPath = FindShortestRoadPath(seedD.ToNode, seedD.FromNode, seedD.TangentAtTo, ctx.NodeOutEdges, ctx.NodePositions, maxReturn, false, seedD.TangentAtFrom);
                if (returnPath == null || returnPath.Count == 0)
                    continue;

                var cycle = new List<DirectedRoadEdge> { seedD };
                cycle.AddRange(returnPath);

                float len;
                if (!ValidateCycle(cycle, ctx.NodeOutEdges, out len, ctx.MaxRouteLength))
                    continue;

                var tour = BuildTourFromRoadCycle(cycle, ctx.EdgeForwardStops, ctx.EdgeBackwardStops, ctx.MaxStops);
                int minResidualStops = math.clamp(ctx.MinStops, 3, 6);
                if (tour.Count >= minResidualStops)
                {
                    AcceptTour(ctx, tour);
                    log.Info($"Built Residual Feeder Loop #{ctx.Tours.Count} with {tour.Count} stops.");
                }
            }
        }

        // Dissolves any tour with fewer than 4 stops (e.g. 2-3 stops), absorbing its stops into neighboring tours
        private void ConsolidateSmallTours(PlanContext ctx)
        {
            int minAllowed = math.clamp(ctx.MinStops, 3, 4);

            for (int ti = ctx.Tours.Count - 1; ti >= 0; ti--)
            {
                var tour = ctx.Tours[ti];
                if (tour.Count >= minAllowed)
                    continue;

                // Never dissolve a station terminal line
                if (tour.Count > 0 && tour[0].HubEntity != Entity.Null)
                    continue;

                bool allAbsorbed = true;
                for (int s = 0; s < tour.Count; s++)
                {
                    var stop = tour[s];

                    int bestTourIdx = -1;
                    int bestInsertPos = -1;
                    float bestDetour = float.MaxValue;

                    for (int o = 0; o < ctx.Tours.Count; o++)
                    {
                        if (o == ti)
                            continue;
                        var other = ctx.Tours[o];
                        if (other.Count >= ctx.MaxStops)
                            continue;

                        // Do not dump external stops into station terminal lines
                        if (other.Count > 0 && other[0].HubEntity != Entity.Null)
                            continue;

                        int on = other.Count;
                        for (int i = 0; i < on; i++)
                        {
                            var a = other[i];
                            var b = other[(i + 1) % on];
                            float da = math.distance(a.Position, stop.Position);
                            float db = math.distance(b.Position, stop.Position);
                            if (math.min(da, db) > 500f)
                                continue;

                            float detour = (da + db) - math.distance(a.Position, b.Position);
                            if (detour < bestDetour)
                            {
                                bestDetour = detour;
                                bestTourIdx = o;
                                bestInsertPos = i + 1;
                            }
                        }
                    }

                    if (bestTourIdx >= 0 && bestDetour < 1000f && (CalculateTourLength(ctx.Tours[bestTourIdx]) + bestDetour <= ctx.MaxRouteLength))
                    {
                        ctx.Tours[bestTourIdx].Insert(bestInsertPos, stop);
                    }
                    else
                    {
                        allAbsorbed = false;
                        ctx.Served.Remove(stop.StopEntity);
                    }
                }

                if (!allAbsorbed)
                {
                    // If the tour has at least 3 stops and could not be absorbed into nearby lines
                    // (e.g. isolated district across a bridge or river), retain it!
                    if (tour.Count >= 3)
                    {
                        for (int s = 0; s < tour.Count; s++)
                            ctx.Served.Add(tour[s].StopEntity);
                        log.Info($"ConsolidateSmallTours: Retained isolated small tour #{ti + 1} with {tour.Count} stops because it could not be merged into distant lines.");
                        continue;
                    }
                }

                log.Info($"ConsolidateSmallTours: Dissolved small tour #{ti + 1} with only {tour.Count} stops (absorbed into nearby lines: {allAbsorbed}).");
                ctx.Tours.RemoveAt(ti);
            }
        }

        private static List<List<PlacedStop>> ClusterStopsByDistance(List<PlacedStop> stops, float maxClusterDist)
        {
            var result = new List<List<PlacedStop>>();
            var visited = new bool[stops.Count];

            for (int i = 0; i < stops.Count; i++)
            {
                if (visited[i])
                    continue;

                var cluster = new List<PlacedStop>();
                var queue = new Queue<int>();
                queue.Enqueue(i);
                visited[i] = true;

                while (queue.Count > 0)
                {
                    int curr = queue.Dequeue();
                    cluster.Add(stops[curr]);

                    float3 posCurr = stops[curr].Position;
                    for (int j = 0; j < stops.Count; j++)
                    {
                        if (!visited[j])
                        {
                            float d = math.distance(posCurr, stops[j].Position);
                            if (d <= maxClusterDist)
                            {
                                visited[j] = true;
                                queue.Enqueue(j);
                            }
                        }
                    }
                }

                result.Add(cluster);
            }

            return result;
        }

        // Dedicated pass for isolated clusters (e.g. settlements across bridges or rivers)
        private void PlanUnservedClusters(PlanContext ctx)
        {
            var unservedStops = new List<PlacedStop>();
            for (int i = 0; i < ctx.Stops.Count; i++)
            {
                var stop = ctx.Stops[i];
                if (!stop.IsStationBay && !ctx.Served.Contains(stop.StopEntity) && ctx.HasEdge[i])
                {
                    unservedStops.Add(stop);
                }
            }

            if (unservedStops.Count < 3)
                return;

            var clusters = ClusterStopsByDistance(unservedStops, 2000f);
            for (int cl = 0; cl < clusters.Count; cl++)
            {
                var cluster = clusters[cl];
                if (cluster.Count < 3)
                    continue;

                int minStops = math.clamp(ctx.MinStops, 3, 6);
                if (cluster.Count < minStops)
                    minStops = cluster.Count;

                for (int sIdx = 0; sIdx < cluster.Count; sIdx++)
                {
                    var seed = cluster[sIdx];
                    if (ctx.Served.Contains(seed.StopEntity))
                        continue;

                    int seedGlobalIdx;
                    if (!ctx.StopIndex.TryGetValue(seed.StopEntity, out seedGlobalIdx) || !ctx.HasEdge[seedGlobalIdx])
                        continue;

                    var seedD = ctx.StopEdge[seedGlobalIdx];
                    float3 seedFromPos = ctx.StopFromPos[seedGlobalIdx];

                    var legs = new List<List<DirectedRoadEdge>>();
                    legs.Add(new List<DirectedRoadEdge> { seedD });
                    var legStopIds = new List<Entity> { seed.StopEntity };
                    var chainStops = new HashSet<Entity> { seed.StopEntity };

                    float length = seedD.Length;
                    Entity currNode = seedD.ToNode;
                    float3 currHeading = seedD.TangentAtTo;

                    int maxChain = math.min(cluster.Count, ctx.MaxStops);

                    for (int step = 0; step < maxChain - 1; step++)
                    {
                        float3 curPos;
                        if (!ctx.NodePositions.TryGetValue(currNode, out curPos))
                            break;

                        int bestGlobalIdx = -1;
                        float bestCost = float.MaxValue;
                        List<DirectedRoadEdge> bestPath = null;

                        for (int c = 0; c < cluster.Count; c++)
                        {
                            var cand = cluster[c];
                            if (ctx.Served.Contains(cand.StopEntity) || chainStops.Contains(cand.StopEntity))
                                continue;

                            int candGlobalIdx;
                            if (!ctx.StopIndex.TryGetValue(cand.StopEntity, out candGlobalIdx) || !ctx.HasEdge[candGlobalIdx])
                                continue;

                            float lb = math.distance(curPos, ctx.StopFromPos[candGlobalIdx]);
                            if (lb > 3500f || lb >= bestCost)
                                continue;

                            var cd = ctx.StopEdge[candGlobalIdx];
                            float3 toPos;
                            float homeLb = ctx.NodePositions.TryGetValue(cd.ToNode, out toPos) ? math.distance(toPos, seedFromPos) : 0f;
                            if (length + lb + cd.Length + homeLb > ctx.MaxRouteLength)
                                continue;

                            var path = FindShortestRoadPath(currNode, cd.FromNode, currHeading, ctx.NodeOutEdges, ctx.NodePositions, 4000f, false, cd.TangentAtFrom);
                            if (path == null)
                                continue;

                            float pathLen = PathLength(path);
                            if (pathLen < bestCost)
                            {
                                bestCost = pathLen;
                                bestGlobalIdx = candGlobalIdx;
                                bestPath = path;
                            }
                        }

                        if (bestGlobalIdx < 0)
                            break;

                        var leg = new List<DirectedRoadEdge>(bestPath);
                        leg.Add(ctx.StopEdge[bestGlobalIdx]);
                        legs.Add(leg);
                        legStopIds.Add(ctx.Stops[bestGlobalIdx].StopEntity);
                        chainStops.Add(ctx.Stops[bestGlobalIdx].StopEntity);

                        length += bestCost + ctx.StopEdge[bestGlobalIdx].Length;
                        var lastEdge = leg[leg.Count - 1];
                        currNode = lastEdge.ToNode;
                        currHeading = lastEdge.TangentAtTo;
                    }

                    // Close the loop back to seed
                    float closingLimit = math.clamp(ctx.MaxRouteLength * 0.5f, 3000f, 6000f);
                    List<DirectedRoadEdge> acceptedCycle = null;
                    while (true)
                    {
                        var returnPath = FindShortestRoadPath(currNode, seedD.FromNode, currHeading, ctx.NodeOutEdges, ctx.NodePositions, closingLimit, false, seedD.TangentAtFrom);
                        if (returnPath != null)
                        {
                            var cycle = new List<DirectedRoadEdge>();
                            for (int l = 0; l < legs.Count; l++)
                                cycle.AddRange(legs[l]);
                            cycle.AddRange(returnPath);

                            float len;
                            if (ValidateCycle(cycle, ctx.NodeOutEdges, out len, ctx.MaxRouteLength))
                            {
                                acceptedCycle = cycle;
                                break;
                            }
                        }

                        if (legs.Count <= 1)
                            break;

                        chainStops.Remove(legStopIds[legStopIds.Count - 1]);
                        legStopIds.RemoveAt(legStopIds.Count - 1);
                        legs.RemoveAt(legs.Count - 1);
                        var prevLeg = legs[legs.Count - 1];
                        currNode = prevLeg[prevLeg.Count - 1].ToNode;
                        currHeading = prevLeg[prevLeg.Count - 1].TangentAtTo;
                    }

                    if (acceptedCycle != null)
                    {
                        var tour = BuildTourFromRoadCycle(acceptedCycle, ctx.EdgeForwardStops, ctx.EdgeBackwardStops, ctx.MaxStops);
                        if (tour.Count >= minStops)
                        {
                            AcceptTour(ctx, tour);
                            log.Info($"PlanUnservedClusters: Built Isolated Cluster Loop #{ctx.Tours.Count} with {tour.Count} stops.");
                            break;
                        }
                    }
                }
            }
        }

        // Drive length from stop A to stop B along the planner's road graph (stop positions on their edges
        // included). Returns false when no legal path exists within maxSearch.
        private bool TryLegLength(PlanContext ctx, int ai, int bi, float maxSearch, out float length)
        {
            length = 0f;
            if (!ctx.HasEdge[ai] || !ctx.HasEdge[bi])
                return false;

            var ea = ctx.StopEdge[ai];
            var eb = ctx.StopEdge[bi];

            if (ea.EdgeEntity == eb.EdgeEntity && ea.FromNode == eb.FromNode && ctx.AlongTravel[bi] >= ctx.AlongTravel[ai])
            {
                length = (ctx.AlongTravel[bi] - ctx.AlongTravel[ai]) * ea.Length;
                return true;
            }

            var path = FindShortestRoadPath(ea.ToNode, eb.FromNode, ea.TangentAtTo, ctx.NodeOutEdges, ctx.NodePositions, maxSearch, false, eb.TangentAtFrom);
            if (path == null)
                return false;

            length = (1f - ctx.AlongTravel[ai]) * ea.Length + PathLength(path) + ctx.AlongTravel[bi] * eb.Length;
            return true;
        }

        // ------------------------------------------------------------------
        // Phase F: last-mile stops go where they add the least detour
        // ------------------------------------------------------------------

        private void PlanLastMileInsertions(PlanContext ctx)
        {
            for (int s = 0; s < ctx.Stops.Count; s++)
            {
                var stop = ctx.Stops[s];
                if (ctx.Served.Contains(stop.StopEntity) || !ctx.HasEdge[s])
                    continue;

                // 1. Shortlist insertion points by straight-line detour (cheap)
                var shortlist = new List<InsertionCandidate>();
                for (int ti = 0; ti < ctx.Tours.Count; ti++)
                {
                    var tour = ctx.Tours[ti];
                    if (tour.Count >= ctx.MaxStops + 4)
                        continue;

                    int n = tour.Count;
                    for (int i = 0; i < n; i++)
                    {
                        var a = tour[i];
                        var b = tour[(i + 1) % n];
                        float da = math.distance(a.Position, stop.Position);
                        float db = math.distance(b.Position, stop.Position);
                        if (math.min(da, db) > 1200f)
                            continue;

                        shortlist.Add(new InsertionCandidate { Tour = ti, Pos = i + 1, Cost = da + db - math.distance(a.Position, b.Position) });
                    }
                }
                shortlist.Sort((x, y) => x.Cost.CompareTo(y.Cost));

                // 2. Re-rank the best few by the REAL drive: a stop may only join a line if the bus can legally
                //    get to it and on to the next stop (it can't if the stop sits in a one-way pocket).
                InsertionCandidate best = default(InsertionCandidate);
                float bestDetour = float.MaxValue;
                bool found = false;
                int look = math.min(shortlist.Count, LASTMILE_CANDIDATES);
                for (int k = 0; k < look; k++)
                {
                    var cand = shortlist[k];
                    var tour = ctx.Tours[cand.Tour];
                    int n = tour.Count;
                    int ai, bi;
                    if (!ctx.StopIndex.TryGetValue(tour[(cand.Pos - 1 + n) % n].StopEntity, out ai) ||
                        !ctx.StopIndex.TryGetValue(tour[cand.Pos % n].StopEntity, out bi))
                        continue;

                    float toStop, fromStop, direct;
                    if (!TryLegLength(ctx, ai, s, 4000f, out toStop) || !TryLegLength(ctx, s, bi, 4000f, out fromStop))
                        continue;
                    if (!TryLegLength(ctx, ai, bi, 5000f, out direct))
                        direct = 0f;

                    float detour = toStop + fromStop - direct;
                    if (detour < bestDetour)
                    {
                        bestDetour = detour;
                        best = cand;
                        found = true;
                    }
                }

                if (found && bestDetour <= LASTMILE_MAX_DETOUR && (CalculateTourLength(ctx.Tours[best.Tour]) + bestDetour <= ctx.MaxRouteLength))
                {
                    ctx.Tours[best.Tour].Insert(best.Pos, stop);
                    ctx.Served.Add(stop.StopEntity);
                    log.Info($"Phase F: Integrated last-mile stop {stop.StopEntity.Index} into Line #{best.Tour + 1} (detour {bestDetour:F0}m)");
                }
                else
                {
                    log.Warn($"Phase F: stop {stop.StopEntity.Index} could not be joined to any line without an illegal or excessive detour; left unserved");
                }
            }
        }

        // ------------------------------------------------------------------
        // Diagnostics: check every leg with the same graph and report problems in the log
        // ------------------------------------------------------------------

        private void DiagnoseTours(PlanContext ctx)
        {
            int legsChecked = 0, unreachableLegs = 0, problemTours = 0;
            float longestTour = 0f;
            float totalLength = 0f;

            for (int ti = 0; ti < ctx.Tours.Count; ti++)
            {
                var tour = ctx.Tours[ti];
                int n = tour.Count;
                if (n < 2)
                    continue;

                float tourLen = 0f;
                int badLegs = 0;
                for (int i = 0; i < n; i++)
                {
                    int ai, bi;
                    if (!ctx.StopIndex.TryGetValue(tour[i].StopEntity, out ai) || !ctx.StopIndex.TryGetValue(tour[(i + 1) % n].StopEntity, out bi))
                        continue;
                    if (!ctx.HasEdge[ai] || !ctx.HasEdge[bi])
                        continue;

                    legsChecked++;
                    float leg;
                    if (!TryLegLength(ctx, ai, bi, 6000f, out leg))
                    {
                        badLegs++;
                        unreachableLegs++;
                        continue;
                    }
                    tourLen += leg;
                }

                totalLength += tourLen;
                longestTour = math.max(longestTour, tourLen);

                if (badLegs > 0 || tourLen > ctx.MaxRouteLength)
                {
                    problemTours++;
                    log.Warn($"Planner check: Line #{ti + 1} has {n} stops, est. {tourLen / 1000f:F1} km, {badLegs} leg(s) with no legal path in the planner's road graph");
                }
            }

            log.Info($"Planner check: {ctx.Tours.Count} lines, {legsChecked} legs checked, {unreachableLegs} without a legal path, {problemTours} problem lines, longest line {longestTour / 1000f:F1} km, total {totalLength / 1000f:F0} km");
        }
    }
}
