using System;
using System.Collections.Generic;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Rendering;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace AutoBusLines
{
    /// <summary>
    /// Renders in-game 3D route curves and stop markers projected onto roads when a bus line
    /// is hovered in the Transit Planner UI, matching the native Cities: Skylines II route visualization.
    /// </summary>
    [UpdateBefore(typeof(OverlayRenderSystem))]
    public partial class PlanRouteOverlaySystem : GameSystemBase
    {
        private OverlayRenderSystem m_OverlayRenderSystem;
        private BusLineGenerator m_BusLineGenerator;
        private EntityQuery m_RoadQuery;

        private static readonly Dictionary<int, List<Bezier4x3>> s_CachedCurves = new Dictionary<int, List<Bezier4x3>>();
        private static readonly Dictionary<int, List<float3>> s_CachedStopPositions = new Dictionary<int, List<float3>>();
        private static readonly Dictionary<int, Color> s_CachedColors = new Dictionary<int, Color>();

        public static void ClearCache()
        {
            s_CachedCurves.Clear();
            s_CachedStopPositions.Clear();
            s_CachedColors.Clear();
        }

        protected override void OnCreate()
        {
            base.OnCreate();
            m_OverlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
            m_BusLineGenerator = World.GetOrCreateSystemManaged<BusLineGenerator>();

            m_RoadQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<Road>()
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadOnly<Game.Tools.Hidden>()
                }
            });

            ClearCache();
        }

        protected override void OnUpdate()
        {
            int hoveredRouteId = AutoBusLinesUISystem.HoveredRouteId;
            if (hoveredRouteId <= 0)
                return;

            if (m_OverlayRenderSystem == null)
            {
                m_OverlayRenderSystem = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
                if (m_OverlayRenderSystem == null) return;
            }

            if (m_BusLineGenerator == null)
            {
                m_BusLineGenerator = World.GetOrCreateSystemManaged<BusLineGenerator>();
                if (m_BusLineGenerator == null) return;
            }

            // Ensure route curves and stop positions are cached
            if (!s_CachedCurves.TryGetValue(hoveredRouteId, out var curves) ||
                !s_CachedStopPositions.TryGetValue(hoveredRouteId, out var stopPositions) ||
                !s_CachedColors.TryGetValue(hoveredRouteId, out var routeColor))
            {
                var route = m_BusLineGenerator.GetActivePlannedRoute(hoveredRouteId);
                if (route == null || route.Stops == null || route.Stops.Count < 2)
                    return;

                BuildRouteOverlay(route, out curves, out stopPositions, out routeColor);
                s_CachedCurves[hoveredRouteId] = curves;
                s_CachedStopPositions[hoveredRouteId] = stopPositions;
                s_CachedColors[hoveredRouteId] = routeColor;
            }

            if (curves == null || curves.Count == 0)
                return;

            var overlayBuffer = m_OverlayRenderSystem.GetBuffer(out JobHandle deps);
            deps.Complete();

            Color outlineColor = new Color(0.08f, 0.08f, 0.08f, 0.85f);
            Color stopOutlineColor = Color.white;

            // 1. Draw 3D road line ribbon along the road network
            for (int i = 0; i < curves.Count; i++)
            {
                overlayBuffer.DrawCurve(
                    outlineColor,
                    routeColor,
                    0.8f,
                    OverlayRenderSystem.StyleFlags.Projected,
                    curves[i],
                    4.5f
                );
            }

            // 2. Draw 3D stop markers projected on the road
            int hoveredStopIdx = AutoBusLinesUISystem.HoveredStopIndex;
            for (int i = 0; i < stopPositions.Count; i++)
            {
                bool isHighlightedStop = (hoveredStopIdx > 0 && i + 1 == hoveredStopIdx);
                float diameter = isHighlightedStop ? 10.0f : (i == 0 ? 8.5f : 6.8f);
                Color fill = isHighlightedStop ? new Color(1f, 0.92f, 0.23f, 1f) : routeColor;
                Color outline = isHighlightedStop ? Color.white : stopOutlineColor;
                float outlineW = isHighlightedStop ? 1.5f : 1.0f;

                overlayBuffer.DrawCircle(
                    outline,
                    fill,
                    outlineW,
                    OverlayRenderSystem.StyleFlags.Projected,
                    default,
                    stopPositions[i],
                    diameter
                );
            }

            m_OverlayRenderSystem.AddBufferWriter(default);
        }

        private void BuildRouteOverlay(
            PlannedRoute route,
            out List<Bezier4x3> curves,
            out List<float3> stopPositions,
            out Color routeColor)
        {
            curves = new List<Bezier4x3>();
            stopPositions = new List<float3>();
            routeColor = HexToColor(route.Color);

            var activeStops = new List<BusLineGenerator.PlacedStop>();
            for (int s = 0; s < route.Stops.Count; s++)
            {
                var stData = route.Stops[s];
                if (stData.Enabled)
                {
                    var stop = stData.InternalStop;
                    ResolveStopRoad(ref stop);
                    activeStops.Add(stop);
                    stopPositions.Add(stop.Position);
                }
            }

            if (activeStops.Count < 2)
                return;

            for (int i = 0; i < activeStops.Count; i++)
            {
                var stopA = activeStops[i];
                var stopB = activeStops[(i + 1) % activeStops.Count];

                AppendLegCurves(stopA, stopB, curves);
            }
        }

        private void ResolveStopRoad(ref BusLineGenerator.PlacedStop stop)
        {
            // If stop already has a valid road edge with Curve and Road component
            if (stop.RoadEntity != Entity.Null &&
                EntityManager.Exists(stop.RoadEntity) &&
                EntityManager.HasComponent<Edge>(stop.RoadEntity) &&
                EntityManager.HasComponent<Curve>(stop.RoadEntity) &&
                EntityManager.HasComponent<Road>(stop.RoadEntity))
            {
                var curve = EntityManager.GetComponentData<Curve>(stop.RoadEntity);
                MathUtils.Distance(curve.m_Bezier, stop.Position, out float t);
                stop.RoadT = math.clamp(t, 0.001f, 0.999f);
                return;
            }

            // Otherwise, find the nearest drivable road edge
            Entity nearest = FindNearestRoad(stop.Position, 120f);
            if (nearest != Entity.Null && EntityManager.HasComponent<Curve>(nearest))
            {
                stop.RoadEntity = nearest;
                var curve = EntityManager.GetComponentData<Curve>(nearest);
                MathUtils.Distance(curve.m_Bezier, stop.Position, out float t);
                stop.RoadT = math.clamp(t, 0.001f, 0.999f);
            }
        }

        private Entity FindNearestRoad(float3 position, float maxRadius = 120f)
        {
            if (m_RoadQuery.IsEmptyIgnoreFilter)
                return Entity.Null;

            using var roadEntities = m_RoadQuery.ToEntityArray(Allocator.Temp);
            Entity bestRoad = Entity.Null;
            float bestDistSq = maxRadius * maxRadius;

            for (int i = 0; i < roadEntities.Length; i++)
            {
                var roadEnt = roadEntities[i];
                if (!EntityManager.HasComponent<Curve>(roadEnt))
                    continue;

                var curve = EntityManager.GetComponentData<Curve>(roadEnt);

                // Quick AABB rejection
                Bounds3 bounds = MathUtils.Bounds(curve.m_Bezier);
                if (MathUtils.DistanceSquared(bounds, position) > bestDistSq)
                    continue;

                float distSq = MathUtils.DistanceSquared(curve.m_Bezier, position, out float _);
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestRoad = roadEnt;
                }
            }
            return bestRoad;
        }

        private void AppendLegCurves(
            BusLineGenerator.PlacedStop stopA,
            BusLineGenerator.PlacedStop stopB,
            List<Bezier4x3> outCurves)
        {
            // If either road entity is still missing/invalid, fallback
            if (stopA.RoadEntity == Entity.Null || stopB.RoadEntity == Entity.Null ||
                !EntityManager.Exists(stopA.RoadEntity) || !EntityManager.Exists(stopB.RoadEntity) ||
                !EntityManager.HasComponent<Edge>(stopA.RoadEntity) || !EntityManager.HasComponent<Edge>(stopB.RoadEntity) ||
                !EntityManager.HasComponent<Curve>(stopA.RoadEntity) || !EntityManager.HasComponent<Curve>(stopB.RoadEntity))
            {
                AddFallbackCurve(stopA, stopB, outCurves);
                return;
            }

            // Case 1: Both stops on the exact same road segment
            if (stopA.RoadEntity == stopB.RoadEntity)
            {
                var roadCurve = EntityManager.GetComponentData<Curve>(stopA.RoadEntity);
                float tA = math.clamp(stopA.RoadT, 0f, 1f);
                float tB = math.clamp(stopB.RoadT, 0f, 1f);

                if (math.abs(tA - tB) > 0.005f)
                {
                    float minT = math.min(tA, tB);
                    float maxT = math.max(tA, tB);
                    Bezier4x3 cut = MathUtils.Cut(roadCurve.m_Bezier, new float2(minT, maxT));
                    if (tA > tB) cut = InvertCurve(cut);
                    outCurves.Add(cut);
                    return;
                }
            }

            // Case 2: A* pathfinding along the road network
            var roadPath = FindRoadPath(stopA.RoadEntity, stopB.RoadEntity, stopB.Position, 2500);
            if (roadPath != null && roadPath.Count >= 2)
            {
                AppendPathCurves(roadPath, stopA.RoadT, stopB.RoadT, outCurves);
                return;
            }

            // Fallback if no road path could be found
            AddFallbackCurve(stopA, stopB, outCurves);
        }

        private void AppendPathCurves(
            List<Entity> roadPath,
            float startRoadT,
            float endRoadT,
            List<Bezier4x3> outCurves)
        {
            if (roadPath == null || roadPath.Count == 0) return;

            if (roadPath.Count == 1)
            {
                var curve = EntityManager.GetComponentData<Curve>(roadPath[0]);
                float tA = math.clamp(startRoadT, 0f, 1f);
                float tB = math.clamp(endRoadT, 0f, 1f);
                if (math.abs(tA - tB) > 0.005f)
                {
                    float minT = math.min(tA, tB);
                    float maxT = math.max(tA, tB);
                    Bezier4x3 cut = MathUtils.Cut(curve.m_Bezier, new float2(minT, maxT));
                    if (tA > tB) cut = InvertCurve(cut);
                    outCurves.Add(cut);
                }
                return;
            }

            // Find shared connection node between roadPath[0] and roadPath[1]
            Entity nextNode = GetSharedNode(roadPath[0], roadPath[1]);
            var edge0 = EntityManager.GetComponentData<Edge>(roadPath[0]);
            var curve0 = EntityManager.GetComponentData<Curve>(roadPath[0]);

            float exitT = 1.0f;
            if (nextNode != Entity.Null)
            {
                exitT = (nextNode == edge0.m_End) ? 1.0f : 0.0f;
            }
            else
            {
                var curve1 = EntityManager.GetComponentData<Curve>(roadPath[1]);
                float3 p1 = MathUtils.Position(curve1.m_Bezier, 0.5f);
                exitT = (math.distance(curve0.m_Bezier.d, p1) < math.distance(curve0.m_Bezier.a, p1)) ? 1.0f : 0.0f;
            }

            // 1. Cut start road segment from startRoadT to exitT
            if (math.abs(startRoadT - exitT) > 0.005f)
            {
                float minT = math.min(startRoadT, exitT);
                float maxT = math.max(startRoadT, exitT);
                Bezier4x3 cut0 = MathUtils.Cut(curve0.m_Bezier, new float2(minT, maxT));
                if (startRoadT > exitT) cut0 = InvertCurve(cut0);
                outCurves.Add(cut0);
            }

            Entity currentNode = (exitT >= 0.5f) ? edge0.m_End : edge0.m_Start;

            // 2. Add intermediate road segments
            for (int p = 1; p < roadPath.Count - 1; p++)
            {
                var edgeP = EntityManager.GetComponentData<Edge>(roadPath[p]);
                var curveP = EntityManager.GetComponentData<Curve>(roadPath[p]);

                bool forward = (edgeP.m_Start == currentNode);
                outCurves.Add(forward ? curveP.m_Bezier : InvertCurve(curveP.m_Bezier));
                currentNode = forward ? edgeP.m_End : edgeP.m_Start;
            }

            // 3. Cut destination road segment from enterT to endRoadT
            int lastIdx = roadPath.Count - 1;
            var edgeLast = EntityManager.GetComponentData<Edge>(roadPath[lastIdx]);
            var curveLast = EntityManager.GetComponentData<Curve>(roadPath[lastIdx]);

            float enterT = (edgeLast.m_Start == currentNode) ? 0.0f : 1.0f;
            if (math.abs(enterT - endRoadT) > 0.005f)
            {
                float minT = math.min(enterT, endRoadT);
                float maxT = math.max(enterT, endRoadT);
                Bezier4x3 cutLast = MathUtils.Cut(curveLast.m_Bezier, new float2(minT, maxT));
                if (enterT > endRoadT) cutLast = InvertCurve(cutLast);
                outCurves.Add(cutLast);
            }
        }

        private Entity GetSharedNode(Entity edgeA, Entity edgeB)
        {
            if (!EntityManager.HasComponent<Edge>(edgeA) || !EntityManager.HasComponent<Edge>(edgeB))
                return Entity.Null;

            var eA = EntityManager.GetComponentData<Edge>(edgeA);
            var eB = EntityManager.GetComponentData<Edge>(edgeB);

            if (eA.m_Start == eB.m_Start || eA.m_Start == eB.m_End) return eA.m_Start;
            if (eA.m_End == eB.m_Start || eA.m_End == eB.m_End) return eA.m_End;
            return Entity.Null;
        }

        private List<Entity> FindRoadPath(Entity startRoad, Entity targetRoad, float3 targetPos, int maxIterations = 2500)
        {
            if (startRoad == targetRoad)
                return new List<Entity> { startRoad };

            if (!EntityManager.HasComponent<Edge>(startRoad) || !EntityManager.HasComponent<Edge>(targetRoad))
                return null;

            var gScore = new Dictionary<Entity, float>();
            var parentEdge = new Dictionary<Entity, Entity>();
            var openSet = new PriorityQueue();
            var closedSet = new HashSet<Entity>();

            gScore[startRoad] = 0f;

            float3 startCenter = targetPos;
            if (EntityManager.HasComponent<Curve>(startRoad))
            {
                startCenter = MathUtils.Position(EntityManager.GetComponentData<Curve>(startRoad).m_Bezier, 0.5f);
            }
            openSet.Enqueue(startRoad, math.distance(startCenter, targetPos));

            bool found = false;
            int iterations = 0;

            while (openSet.Count > 0 && iterations < maxIterations)
            {
                var currentEdge = openSet.Dequeue();
                iterations++;

                if (currentEdge == targetRoad)
                {
                    found = true;
                    break;
                }

                if (!closedSet.Add(currentEdge))
                    continue;

                if (!EntityManager.HasComponent<Edge>(currentEdge))
                    continue;

                var edgeData = EntityManager.GetComponentData<Edge>(currentEdge);
                float currentG = gScore[currentEdge];

                Entity[] nodes = new Entity[] { edgeData.m_Start, edgeData.m_End };
                for (int n = 0; n < nodes.Length; n++)
                {
                    Entity node = nodes[n];
                    if (node == Entity.Null || !EntityManager.HasBuffer<ConnectedEdge>(node))
                        continue;

                    var connBuf = EntityManager.GetBuffer<ConnectedEdge>(node);
                    for (int c = 0; c < connBuf.Length; c++)
                    {
                        Entity nextEdge = connBuf[c].m_Edge;
                        if (nextEdge == Entity.Null || nextEdge == currentEdge)
                            continue;

                        // Only follow actual drivable road edges! Exclude pipes, electric wires, train tracks, etc.
                        if (!EntityManager.HasComponent<Road>(nextEdge) ||
                            !EntityManager.HasComponent<Edge>(nextEdge) ||
                            !EntityManager.HasComponent<Curve>(nextEdge))
                            continue;

                        if (EntityManager.HasComponent<Deleted>(nextEdge))
                            continue;

                        if (closedSet.Contains(nextEdge))
                            continue;

                        var nextCurve = EntityManager.GetComponentData<Curve>(nextEdge);
                        float edgeLength = MathUtils.Length(nextCurve.m_Bezier);
                        float tentativeG = currentG + math.max(edgeLength, 5f);

                        if (!gScore.TryGetValue(nextEdge, out float oldG) || tentativeG < oldG)
                        {
                            gScore[nextEdge] = tentativeG;
                            parentEdge[nextEdge] = currentEdge;

                            float3 nextCenter = MathUtils.Position(nextCurve.m_Bezier, 0.5f);
                            float h = math.distance(nextCenter, targetPos);
                            openSet.Enqueue(nextEdge, tentativeG + h);

                            if (nextEdge == targetRoad)
                            {
                                found = true;
                                break;
                            }
                        }
                    }
                    if (found) break;
                }
            }

            if (!found) return null;

            var path = new List<Entity>();
            Entity curr = targetRoad;
            while (curr != Entity.Null)
            {
                path.Add(curr);
                if (curr == startRoad) break;
                if (!parentEdge.TryGetValue(curr, out curr)) break;
            }
            path.Reverse();
            return path;
        }

        private void AddFallbackCurve(
            BusLineGenerator.PlacedStop stopA,
            BusLineGenerator.PlacedStop stopB,
            List<Bezier4x3> outCurves)
        {
            float dist = math.distance(stopA.Position, stopB.Position);
            float handle = math.clamp(dist * 0.35f, 4f, 60f);
            float3 fwdA = math.normalizesafe(stopA.Forward, new float3(0, 0, 1));
            float3 fwdB = math.normalizesafe(stopB.Forward, new float3(0, 0, 1));
            float3 dir = math.normalizesafe(stopB.Position - stopA.Position);
            if (math.dot(fwdA, dir) < -0.2f) fwdA = dir;
            if (math.dot(fwdB, dir) < -0.2f) fwdB = dir;

            outCurves.Add(new Bezier4x3(
                stopA.Position,
                stopA.Position + fwdA * handle,
                stopB.Position - fwdB * handle,
                stopB.Position
            ));
        }

        private class PriorityQueue
        {
            private readonly List<KeyValuePair<float, Entity>> m_Elements = new List<KeyValuePair<float, Entity>>();

            public int Count => m_Elements.Count;

            public void Enqueue(Entity item, float priority)
            {
                m_Elements.Add(new KeyValuePair<float, Entity>(priority, item));
                int ci = m_Elements.Count - 1;
                while (ci > 0)
                {
                    int pi = (ci - 1) / 2;
                    if (m_Elements[ci].Key >= m_Elements[pi].Key)
                        break;
                    var tmp = m_Elements[ci];
                    m_Elements[ci] = m_Elements[pi];
                    m_Elements[pi] = tmp;
                    ci = pi;
                }
            }

            public Entity Dequeue()
            {
                int li = m_Elements.Count - 1;
                var front = m_Elements[0];
                m_Elements[0] = m_Elements[li];
                m_Elements.RemoveAt(li);
                --li;
                int pi = 0;
                while (true)
                {
                    int ci = pi * 2 + 1;
                    if (ci > li) break;
                    int rc = ci + 1;
                    if (rc <= li && m_Elements[rc].Key < m_Elements[ci].Key)
                        ci = rc;
                    if (m_Elements[pi].Key <= m_Elements[ci].Key)
                        break;
                    var tmp = m_Elements[pi];
                    m_Elements[pi] = m_Elements[ci];
                    m_Elements[ci] = tmp;
                    pi = ci;
                }
                return front.Value;
            }
        }

        private static Bezier4x3 InvertCurve(Bezier4x3 b)
        {
            return new Bezier4x3(b.d, b.c, b.b, b.a);
        }

        private static Color HexToColor(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return new Color(0.2f, 0.6f, 1f, 1f);
            hex = hex.TrimStart('#');
            if (hex.Length == 6)
            {
                if (byte.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out byte r) &&
                    byte.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte g) &&
                    byte.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
                {
                    return new Color(r / 255f, g / 255f, b / 255f, 1f);
                }
            }
            return new Color(0.2f, 0.6f, 1f, 1f);
        }
    }
}
