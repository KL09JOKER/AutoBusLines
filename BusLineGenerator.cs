// AutoBusLines v0.2.0 - tour planning moved to BusLineGenerator.Planning.cs
using System;
using System.Collections.Generic;
using System.Linq;
using Colossal.Logging;
using Colossal.Mathematics;
using Game;
using Game.Common;
using Game.Net;
using Game.Notifications;
using Game.Objects;
using Game.Pathfind;
using Game.Prefabs;
using Game.Rendering;
using Game.Routes;
using Game.Tools;
using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Edge = Game.Net.Edge;

namespace AutoBusLines
{
    public partial class BusLineGenerator : GameSystemBase
    {
        private static readonly ILog log = LogManager.GetLogger($"{nameof(AutoBusLines)}.{nameof(BusLineGenerator)}");

        private enum GenerationStage
        {
            Idle,
            WaitingForStopIndexing,
            InstantiatingRoutes
        }

        private GenerationStage _generationStage = GenerationStage.Idle;
        private int _waitFrameCounter = 0;
        private const int INDEXING_DELAY_FRAMES = 15;
        private const int ROUTES_PER_FRAME = 2;
        private int _currentTourIndex = 0;
        private List<List<PlacedStop>> _plannedTours = new List<List<PlacedStop>>();
        private Entity _cachedBusLinePrefabEntity = Entity.Null;
        private RouteData _cachedBusLineRouteData;
        private int _totalStopsPlaced = 0;
        private int _totalStopsActive = 0;

        private bool _hasRun = false;
        private bool _isManualRequest = false;
        private bool _isRepairRequested = false;

        private DepotFinderSystem _depotFinder;
        private RoadNetworkScanner _roadScanner;
        private RoadDepotAssigner _roadAssigner;
        private PrefabSystem _prefabSystem;

        private EntityQuery _busStopPrefabQuery;
        private EntityQuery _busLinePrefabQuery;
        private EntityQuery _existingBusStopQuery;

        private const float HUB_EXCLUSION_RADIUS = 60f;             // Exclusion radius around stations & depots (passengers use station bays)
        public const float DEFAULT_MAX_ROUTE_LENGTH = 15000f;       // Default maximum bus loop length: 15 km
        private const int MIN_STOPS_PER_LINE = 6;                   // Minimum stops per bus loop
        private const int MAX_STOPS_PER_LINE = 16;                  // Maximum stops per bus loop
        private const float SIDEWALK_OFFSET = 5.0f;                 // Distance from road centerline to sidewalk stop (meters)
        private const float ABSOLUTE_MIN_STOP_SPACING = 120.0f;     // Safety net: never drop two stops closer than this (halves stop count & prevents corner pile-ups)
        private const float MIN_SEGMENT_LENGTH_FOR_STOP = 40.0f;    // Minimum road length to be a valid block face (filters tiny junction splits)
        private const float STOP_PROXIMITY_SERVED_RADIUS = 220.0f;  // Blocks within 220m are considered served by nearby stop (halves stop density)
        private const float MIN_BLOCK_AREA = 500.0f;                // Faces smaller than this are junction slivers, not city blocks
        private const float MAX_BLOCK_AREA = 400000.0f;             // Faces bigger than this are the outer boundary / rural ring roads, not city blocks
        private const float MIN_BLOCK_THICKNESS_RATIO = 5.0f;       // area/perimeter; filters long thin medians between divided carriageways
        private const float HUB_BLOCK_SERVED_RADIUS = 220.0f;       // Blocks this close to a station/depot are already served by its own platform bays
        private const float ORPHAN_SPAN_MAX_UNSERVED = 220.0f;      // Dead-end / tree branches: place a stop if nothing is within this range
        private const float EXISTING_STOP_SPAN_TOLERANCE = 30.0f;   // An existing stop this close to a span midpoint already serves that span

        private struct CommittedStopInfo
        {
            public float3 Position;
            public float3 Forward;
            public int CorridorIndex;
        }

        private struct PlacedStop
        {
            public Entity StopEntity;
            public float3 Position;
            public float3 Forward;
            public Entity RoadEntity;
            public Entity HubEntity;
            public int CorridorIndex;
            public int SpanOrderInCorridor;
            public float AngleFromHub;
            public float DistFromHub;
            public bool IsOutbound;
        }

        private class RoadSegmentChain
        {
            public Entity RoadEntity;
            public float Length;
            public bool IsReversed;
        }

        private class RoadCorridor
        {
            public float TotalLength;
            public List<RoadSegmentChain> Segments = new List<RoadSegmentChain>();
        }

        private struct EdgeEndpoint
        {
            public Entity EdgeEntity;
            public bool IsStartNode;
            public float3 OutwardTangent;
        }

        private struct SpanEdge
        {
            public Entity EdgeEntity;
            public bool IsReversed;
            public float Length;
        }

        private class BlockSpan
        {
            public int SpanId;
            public List<SpanEdge> Edges = new List<SpanEdge>();
            public Entity StartNode;
            public Entity EndNode;
            public float TotalLength;
            public float3 MidPosition;
            public float3 MidTangent;
            public Entity MidEdge;
            public float MidT;
            public Entity HubEntity;
            public int CorridorIndex = -1;
            public float CorridorLength = 0f;
            public bool IsSuppressed = false;
            public bool IsSideStreet = false;
            public float3 StartPosition;
            public float3 EndPosition;
            public float3 StartTangent;
            public float3 EndTangent;
            public int SpanOrderInCorridor = 0;
            public float3 CorridorForwardTangent;
        }

        private struct DirectedSpanHalfEdge
        {
            public int SpanIndex;
            public bool IsForward; // true: StartNode -> EndNode; false: EndNode -> StartNode
        }

        private struct NodeOutgoingHalfEdge
        {
            public DirectedSpanHalfEdge HalfEdge;
            public float Angle; // atan2(outTangent.z, outTangent.x)
        }

        private class CityBlockFace
        {
            public int FaceId;
            public List<int> SpanIndices = new List<int>();
            public float Area;
            public float Perimeter;
            public float2 Centroid;
            public bool IsServed;
        }

        protected override void OnCreate()
        {
            base.OnCreate();

            _depotFinder = World.GetOrCreateSystemManaged<DepotFinderSystem>();
            _roadScanner = World.GetOrCreateSystemManaged<RoadNetworkScanner>();
            _roadAssigner = World.GetOrCreateSystemManaged<RoadDepotAssigner>();
            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            _busStopPrefabQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<PrefabData>(),
                    ComponentType.ReadOnly<TransportStopData>(),
                    ComponentType.ReadOnly<ObjectData>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Game.Prefabs.OutsideConnectionData>(),
                }
            });

            _busLinePrefabQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<PrefabData>(),
                    ComponentType.ReadOnly<TransportLineData>(),
                    ComponentType.ReadOnly<RouteData>(),
                }
            });

            _existingBusStopQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Routes.TransportStop>(),
                    ComponentType.ReadOnly<Game.Objects.Transform>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadOnly<Game.Objects.OutsideConnection>(),
                    ComponentType.ReadOnly<Game.Net.OutsideConnection>(),
                }
            });

            log.Info("BusLineGenerator (Balanced 1-Stop-Per-Block Corridor-First System) created");
        }

        public void RequestGeneration()
        {
            _hasRun = false;
            _isManualRequest = true;
            _generationStage = GenerationStage.Idle;
            _waitFrameCounter = 0;
            _currentTourIndex = 0;
            _plannedTours.Clear();
            _depotFinder?.Reset();
            _roadScanner?.Reset();
            _roadAssigner?.Reset();
        }

        public void RequestRepair()
        {
            _isRepairRequested = true;
        }

        public void DeleteAllBusLinesAndStops()
        {
            int lineCount = 0;
            int waypointCount = 0;
            int segmentCount = 0;

            // 1. Delete all TransportLine entities (bus routes) and their child waypoints and segments
            var lineQuery = GetEntityQuery(ComponentType.ReadOnly<Game.Routes.TransportLine>(), ComponentType.Exclude<Deleted>());
            using (var lines = lineQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    var lineEntity = lines[i];
                    if (!EntityManager.Exists(lineEntity) || EntityManager.HasComponent<Deleted>(lineEntity))
                        continue;

                    if (EntityManager.HasComponent<PrefabRef>(lineEntity))
                    {
                        var prefabEntity = EntityManager.GetComponentData<PrefabRef>(lineEntity).m_Prefab;
                        if (EntityManager.HasComponent<TransportLineData>(prefabEntity))
                        {
                            var lineData = EntityManager.GetComponentData<TransportLineData>(prefabEntity);
                            if (lineData.m_TransportType == TransportType.Bus)
                            {
                                // Delete child waypoints and unregister from ConnectedRoute buffers on stops.
                                // Tagging an entity Deleted is a structural change and invalidates every
                                // live DynamicBuffer handle, so the child list is snapshotted to a plain
                                // array before any tagging starts.
                                if (EntityManager.HasBuffer<RouteWaypoint>(lineEntity))
                                {
                                    Entity[] wpEntities;
                                    {
                                        var waypoints = EntityManager.GetBuffer<RouteWaypoint>(lineEntity);
                                        wpEntities = new Entity[waypoints.Length];
                                        for (int w = 0; w < waypoints.Length; w++)
                                            wpEntities[w] = waypoints[w].m_Waypoint;
                                    }

                                    for (int w = 0; w < wpEntities.Length; w++)
                                    {
                                        var wpEntity = wpEntities[w];
                                        if (EntityManager.Exists(wpEntity) && !EntityManager.HasComponent<Deleted>(wpEntity))
                                        {
                                            if (EntityManager.HasComponent<Game.Routes.Connected>(wpEntity))
                                            {
                                                var connectedStop = EntityManager.GetComponentData<Game.Routes.Connected>(wpEntity).m_Connected;
                                                if (connectedStop != Entity.Null && EntityManager.Exists(connectedStop) && EntityManager.HasBuffer<ConnectedRoute>(connectedStop))
                                                {
                                                    // RemoveAt only resizes the buffer's own storage - not a
                                                    // structural change - so this handle stays valid here.
                                                    var connectedRoutes = EntityManager.GetBuffer<ConnectedRoute>(connectedStop);
                                                    for (int cr = connectedRoutes.Length - 1; cr >= 0; cr--)
                                                    {
                                                        if (connectedRoutes[cr].m_Waypoint == wpEntity)
                                                        {
                                                            connectedRoutes.RemoveAt(cr);
                                                        }
                                                    }
                                                }
                                            }

                                            EntityManager.AddComponentData(wpEntity, default(Deleted));
                                            EntityManager.AddComponentData(wpEntity, default(Updated));
                                            waypointCount++;
                                        }
                                    }
                                }

                                // Delete child route segments (same snapshot-then-tag ordering)
                                if (EntityManager.HasBuffer<RouteSegment>(lineEntity))
                                {
                                    Entity[] segEntities;
                                    {
                                        var segments = EntityManager.GetBuffer<RouteSegment>(lineEntity);
                                        segEntities = new Entity[segments.Length];
                                        for (int s = 0; s < segments.Length; s++)
                                            segEntities[s] = segments[s].m_Segment;
                                    }

                                    for (int s = 0; s < segEntities.Length; s++)
                                    {
                                        var segEntity = segEntities[s];
                                        if (EntityManager.Exists(segEntity) && !EntityManager.HasComponent<Deleted>(segEntity))
                                        {
                                            EntityManager.AddComponentData(segEntity, default(Deleted));
                                            EntityManager.AddComponentData(segEntity, default(Updated));
                                            segmentCount++;
                                        }
                                    }
                                }

                                EntityManager.AddComponentData(lineEntity, default(Deleted));
                                EntityManager.AddComponentData(lineEntity, default(Updated));
                                lineCount++;
                            }
                        }
                    }
                }
            }

            // 2. Delete all roadside TransportStop entities (excluding permanent stations/depots)
            var stopQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Routes.TransportStop>(),
                ComponentType.Exclude<Deleted>()
            );
            int stopCount = 0;

            using (var stops = stopQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < stops.Length; i++)
                {
                    var stopEntity = stops[i];
                    if (!EntityManager.Exists(stopEntity) || EntityManager.HasComponent<Deleted>(stopEntity))
                        continue;

                    // Skip stops that are part of permanent stations or depots
                    if (EntityManager.HasComponent<Game.Buildings.TransportStation>(stopEntity) ||
                        EntityManager.HasComponent<Game.Buildings.TransportDepot>(stopEntity))
                        continue;

                    // Check if this stop has an Owner pointing to a station/depot (platform bays)
                    if (EntityManager.HasComponent<Game.Common.Owner>(stopEntity))
                    {
                        var owner = EntityManager.GetComponentData<Game.Common.Owner>(stopEntity).m_Owner;
                        if (owner != Entity.Null && (EntityManager.HasComponent<Game.Buildings.TransportStation>(owner) ||
                                                     EntityManager.HasComponent<Game.Buildings.TransportDepot>(owner)))
                        {
                            continue;
                        }
                    }

                    // Roadside stops are attached to the road via Attached component (without Owner)
                    Entity roadOwner = Entity.Null;
                    if (EntityManager.HasComponent<Attached>(stopEntity))
                    {
                        roadOwner = EntityManager.GetComponentData<Attached>(stopEntity).m_Parent;
                    }
                    else if (EntityManager.HasComponent<Game.Common.Owner>(stopEntity))
                    {
                        roadOwner = EntityManager.GetComponentData<Game.Common.Owner>(stopEntity).m_Owner;
                    }

                    // Delete roadside bus stops only (check prefab TransportType)
                    if (EntityManager.HasComponent<PrefabRef>(stopEntity))
                    {
                        var prefabEntity = EntityManager.GetComponentData<PrefabRef>(stopEntity).m_Prefab;
                        if (prefabEntity != Entity.Null && EntityManager.HasComponent<TransportStopData>(prefabEntity))
                        {
                            var stopData = EntityManager.GetComponentData<TransportStopData>(prefabEntity);
                            if (stopData.m_TransportType == TransportType.Bus)
                            {
                                EntityManager.AddComponentData(stopEntity, default(Deleted));
                                EntityManager.AddComponentData(stopEntity, default(Updated));
                                EntityManager.AddComponentData(stopEntity, default(BatchesUpdated));

                                if (roadOwner != Entity.Null && EntityManager.HasComponent<Edge>(roadOwner))
                                {
                                    ClearRoadDeadEndNotifications(roadOwner);
                                }

                                stopCount++;
                            }
                        }
                    }
                }
            }

            // 3. Reset system state for regeneration
            _hasRun = false;
            _isManualRequest = false;
            _generationStage = GenerationStage.Idle;
            _waitFrameCounter = 0;
            _currentTourIndex = 0;
            _plannedTours.Clear();
            var depotFinder = World.GetExistingSystemManaged<DepotFinderSystem>();
            if (depotFinder != null)
                depotFinder.Reset();

            log.Info($"Deleted {lineCount} bus lines (with {waypointCount} waypoints and {segmentCount} segments) and {stopCount} roadside bus stops across the city.");
        }

        private void ExecuteRepairBrokenRoutes()
        {
            log.Info("=== Starting Manual Route Repair: Scanning bus lines for pathfinding failures ===");

            var lineQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Routes.TransportLine>(),
                ComponentType.ReadOnly<Game.Routes.Route>(),
                ComponentType.Exclude<Deleted>()
            );

            if (lineQuery.IsEmptyIgnoreFilter)
            {
                log.Info("No bus routes found in city to repair.");
                return;
            }

            int totalRoutesExamined = 0;
            int brokenRoutesFound = 0;
            int totalBrokenSegments = 0;
            int stopsNudged = 0;

            var stopsToNudge = new HashSet<Entity>();
            var segmentsToReset = new HashSet<Entity>();
            var routesToUpdate = new HashSet<Entity>();

            using (var routeEntities = lineQuery.ToEntityArray(Allocator.Temp))
            {
                for (int r = 0; r < routeEntities.Length; r++)
                {
                    var routeEntity = routeEntities[r];
                    if (!EntityManager.Exists(routeEntity) || EntityManager.HasComponent<Deleted>(routeEntity))
                        continue;

                    if (!EntityManager.HasComponent<PrefabRef>(routeEntity))
                        continue;

                    var prefabEntity = EntityManager.GetComponentData<PrefabRef>(routeEntity).m_Prefab;
                    if (!EntityManager.HasComponent<TransportLineData>(prefabEntity))
                        continue;

                    var lineData = EntityManager.GetComponentData<TransportLineData>(prefabEntity);
                    if (lineData.m_TransportType != TransportType.Bus)
                        continue;

                    totalRoutesExamined++;

                    if (!EntityManager.HasBuffer<RouteSegment>(routeEntity) || !EntityManager.HasBuffer<RouteWaypoint>(routeEntity))
                        continue;

                    var segBuf = EntityManager.GetBuffer<RouteSegment>(routeEntity);
                    var wpBuf = EntityManager.GetBuffer<RouteWaypoint>(routeEntity);

                    if (segBuf.Length == 0 || wpBuf.Length == 0)
                        continue;

                    bool routeHasBrokenSegment = false;

                    for (int s = 0; s < segBuf.Length; s++)
                    {
                        var segEnt = segBuf[s].m_Segment;
                        if (!EntityManager.Exists(segEnt) || EntityManager.HasComponent<Deleted>(segEnt))
                        {
                            routeHasBrokenSegment = true;
                            totalBrokenSegments++;
                            continue;
                        }

                        bool isBroken = false;
                        if (EntityManager.HasComponent<PathInformation>(segEnt))
                        {
                            var pi = EntityManager.GetComponentData<PathInformation>(segEnt);
                            if (pi.m_Distance <= 0f)
                            {
                                isBroken = true;
                            }
                        }
                        else
                        {
                            isBroken = true;
                        }

                        if (!EntityManager.HasBuffer<PathElement>(segEnt) || EntityManager.GetBuffer<PathElement>(segEnt).Length == 0)
                        {
                            isBroken = true;
                        }

                        if (isBroken)
                        {
                            routeHasBrokenSegment = true;
                            totalBrokenSegments++;
                            segmentsToReset.Add(segEnt);

                            // The failed segment connects Waypoint s to Waypoint (s + 1) % wpBuf.Length
                            int wpIndexA = s;
                            int wpIndexB = (s + 1) % wpBuf.Length;

                            if (wpIndexA < wpBuf.Length)
                            {
                                var wpA = wpBuf[wpIndexA].m_Waypoint;
                                if (EntityManager.Exists(wpA) && !EntityManager.HasComponent<Deleted>(wpA) && EntityManager.HasComponent<Connected>(wpA))
                                {
                                    var stopA = EntityManager.GetComponentData<Connected>(wpA).m_Connected;
                                    if (stopA != Entity.Null && EntityManager.Exists(stopA) && !EntityManager.HasComponent<Deleted>(stopA))
                                    {
                                        stopsToNudge.Add(stopA);
                                    }
                                }
                            }

                            if (wpIndexB < wpBuf.Length)
                            {
                                var wpB = wpBuf[wpIndexB].m_Waypoint;
                                if (EntityManager.Exists(wpB) && !EntityManager.HasComponent<Deleted>(wpB) && EntityManager.HasComponent<Connected>(wpB))
                                {
                                    var stopB = EntityManager.GetComponentData<Connected>(wpB).m_Connected;
                                    if (stopB != Entity.Null && EntityManager.Exists(stopB) && !EntityManager.HasComponent<Deleted>(stopB))
                                    {
                                        stopsToNudge.Add(stopB);
                                    }
                                }
                            }

                            // Also reset adjacent segments touching these two waypoints
                            int prevSegIdx = (s - 1 + segBuf.Length) % segBuf.Length;
                            int nextSegIdx = (s + 1) % segBuf.Length;
                            if (prevSegIdx < segBuf.Length)
                                segmentsToReset.Add(segBuf[prevSegIdx].m_Segment);
                            if (nextSegIdx < segBuf.Length)
                                segmentsToReset.Add(segBuf[nextSegIdx].m_Segment);
                        }
                    }

                    if (routeHasBrokenSegment)
                    {
                        brokenRoutesFound++;
                        routesToUpdate.Add(routeEntity);
                    }
                }
            }

            log.Info($"Scanned {totalRoutesExamined} bus routes: Found {brokenRoutesFound} routes with {totalBrokenSegments} broken segments. Identified {stopsToNudge.Count} problematic roadside stops to adjust.");

            if (totalBrokenSegments == 0)
            {
                log.Info("All bus routes are already healthy! No repair actions needed.");
                return;
            }

            // 1. Nudge problematic roadside bus stops along their road curves
            foreach (var stopEntity in stopsToNudge)
            {
                if (NudgeRoadsideBusStop(stopEntity))
                {
                    stopsNudged++;
                }
            }

            // 2. Reset PathTargets and PathInformation on affected segments to force RoutePathSystem to recalculate
            foreach (var segEnt in segmentsToReset)
            {
                if (!EntityManager.Exists(segEnt) || EntityManager.HasComponent<Deleted>(segEnt))
                    continue;

                if (EntityManager.HasComponent<PathTargets>(segEnt))
                {
                    var pt = EntityManager.GetComponentData<PathTargets>(segEnt);
                    pt.m_StartLane = Entity.Null;
                    pt.m_EndLane = Entity.Null;
                    pt.m_CurvePositions = default;
                    EntityManager.SetComponentData(segEnt, pt);
                }

                if (EntityManager.HasComponent<PathInformation>(segEnt))
                {
                    EntityManager.SetComponentData(segEnt, default(PathInformation));
                }

                if (EntityManager.HasBuffer<PathElement>(segEnt))
                {
                    EntityManager.GetBuffer<PathElement>(segEnt).Clear();
                }

                if (EntityManager.HasBuffer<CurveElement>(segEnt))
                {
                    EntityManager.GetBuffer<CurveElement>(segEnt).Clear();
                }

                if (!EntityManager.HasComponent<Updated>(segEnt))
                    EntityManager.AddComponentData(segEnt, default(Updated));
            }

            // 3. Mark routes as Updated to trigger route updates in native systems
            foreach (var routeEntity in routesToUpdate)
            {
                if (!EntityManager.Exists(routeEntity) || EntityManager.HasComponent<Deleted>(routeEntity))
                    continue;

                if (!EntityManager.HasComponent<Updated>(routeEntity))
                    EntityManager.AddComponentData(routeEntity, default(Updated));
                if (!EntityManager.HasComponent<BatchesUpdated>(routeEntity))
                    EntityManager.AddComponentData(routeEntity, default(BatchesUpdated));
            }

            log.Info($"=== Repair Complete: Nudged {stopsNudged} stops, reset {segmentsToReset.Count} segments across {brokenRoutesFound} routes. Re-enqueued pathfinding. ===");

            // 4. Reset RouteInspector so it inspects repaired routes after simulation runs
            var inspector = World.GetExistingSystemManaged<RouteInspector>();
            if (inspector != null)
            {
                inspector.Reset();
            }
        }

        private bool NudgeRoadsideBusStop(Entity stopEntity)
        {
            if (!EntityManager.Exists(stopEntity) || EntityManager.HasComponent<Deleted>(stopEntity))
                return false;

            if (!EntityManager.HasComponent<Attached>(stopEntity) || !EntityManager.HasComponent<Game.Objects.Transform>(stopEntity))
                return false;

            var attached = EntityManager.GetComponentData<Attached>(stopEntity);
            var roadEntity = attached.m_Parent;
            if (roadEntity == Entity.Null || !EntityManager.Exists(roadEntity) || !EntityManager.HasComponent<Curve>(roadEntity))
                return false;

            var roadCurve = EntityManager.GetComponentData<Curve>(roadEntity);
            float roadLen = MathUtils.Length(roadCurve.m_Bezier);
            if (roadLen < 1.0f)
                return false;

            float curT = attached.m_CurvePosition;

            // Bounds on road segment to avoid intersection conflict zones
            float minT = (roadLen >= 42.0f) ? (20.0f / roadLen) : 0.25f;
            float maxT = (roadLen >= 42.0f) ? (1.0f - (22.0f / roadLen)) : 0.75f;
            if (minT > maxT)
            {
                minT = 0.30f;
                maxT = 0.70f;
            }

            // Nudge distance: 3.0 meters along the road curve
            float nudgeMeters = 3.0f;
            float deltaT = nudgeMeters / roadLen;

            float newT;
            if (curT < 0.45f)
            {
                // Closer to start of segment, nudge forward toward center
                newT = curT + deltaT;
            }
            else if (curT > 0.55f)
            {
                // Closer to end of segment, nudge backward toward center
                newT = curT - deltaT;
            }
            else
            {
                // Around center, nudge slightly forward or alternate
                newT = curT + deltaT;
                if (newT > maxT)
                    newT = curT - deltaT;
            }

            newT = math.clamp(newT, minT, maxT);
            if (math.abs(newT - curT) < 0.0005f)
            {
                newT = (curT < 0.5f) ? math.min(curT + deltaT, 0.5f) : math.max(curT - deltaT, 0.5f);
            }

            var currentTransform = EntityManager.GetComponentData<Game.Objects.Transform>(stopEntity);
            float3 curPos = currentTransform.m_Position;

            // Determine which side of the road the stop was placed on relative to the road curve
            float3 curRoadPos = MathUtils.Position(roadCurve.m_Bezier, curT);
            float3 curTangent = MathUtils.Tangent(roadCurve.m_Bezier, curT);
            float3 curRoadForward = math.normalizesafe(new float3(curTangent.x, 0f, curTangent.z));
            float3 curRightNormal = new float3(curRoadForward.z, 0f, -curRoadForward.x);
            float3 offsetVec = curPos - curRoadPos;

            float lateralDist = math.length(new float2(offsetVec.x, offsetVec.z));
            if (lateralDist < 2.0f)
                lateralDist = 5.0f; // default sidewalk curb offset

            // Dot product with right-hand normal preserves the EXACT same curb:
            // sideDot >= 0 means right side curb, sideDot < 0 means left side curb
            float sideDot = math.dot(offsetVec, curRightNormal);

            // Compute new position and right-hand normal along road curve at newT
            float3 newRoadPos = MathUtils.Position(roadCurve.m_Bezier, newT);
            float3 newTangent = MathUtils.Tangent(roadCurve.m_Bezier, newT);
            float3 newRoadForward = math.normalizesafe(new float3(newTangent.x, 0f, newTangent.z));
            float3 newRightNormal = new float3(newRoadForward.z, 0f, -newRoadForward.x);

            float3 normal = (sideDot >= 0f) ? newRightNormal : -newRightNormal;
            float3 newStopPos = newRoadPos + normal * lateralDist;
            newStopPos.y = newRoadPos.y;

            // Preserve stop rotation along road direction
            quaternion newRot = currentTransform.m_Rotation;
            if (math.lengthsq(newRoadForward) > 0.1f)
            {
                float3 curFacing = math.rotate(currentTransform.m_Rotation, new float3(0, 0, 1));
                if (math.dot(curFacing, curRoadForward) < 0f)
                    newRot = quaternion.LookRotationSafe(-newRoadForward, new float3(0, 1, 0));
                else
                    newRot = quaternion.LookRotationSafe(newRoadForward, new float3(0, 1, 0));
            }

            // Apply updated Attached and Transform to stop
            EntityManager.SetComponentData(stopEntity, new Attached(roadEntity, Entity.Null, newT));
            EntityManager.SetComponentData(stopEntity, new Game.Objects.Transform(newStopPos, newRot));

            if (!EntityManager.HasComponent<Updated>(stopEntity))
                EntityManager.AddComponentData(stopEntity, default(Updated));
            if (!EntityManager.HasComponent<BatchesUpdated>(stopEntity))
                EntityManager.AddComponentData(stopEntity, default(BatchesUpdated));

            // Clean up any legacy SubObject / DeadEnd warnings on parent road
            ClearRoadDeadEndNotifications(roadEntity);

            // Update all connected waypoints
            if (EntityManager.HasBuffer<ConnectedRoute>(stopEntity))
            {
                var connBuf = EntityManager.GetBuffer<ConnectedRoute>(stopEntity);
                for (int c = 0; c < connBuf.Length; c++)
                {
                    var wpEnt = connBuf[c].m_Waypoint;
                    if (EntityManager.Exists(wpEnt) && !EntityManager.HasComponent<Deleted>(wpEnt))
                    {
                        if (EntityManager.HasComponent<Game.Routes.Position>(wpEnt))
                        {
                            EntityManager.SetComponentData(wpEnt, new Game.Routes.Position(newStopPos));
                        }
                        if (!EntityManager.HasComponent<Updated>(wpEnt))
                            EntityManager.AddComponentData(wpEnt, default(Updated));
                    }
                }
            }

            return true;
        }

        private void ClearRoadDeadEndNotifications(Entity roadEntity)
        {
            if (roadEntity == Entity.Null || !EntityManager.Exists(roadEntity))
                return;

            // Remove legacy SubObject entries on roadEntity
            if (EntityManager.HasBuffer<Game.Objects.SubObject>(roadEntity))
            {
                var subObjs = EntityManager.GetBuffer<Game.Objects.SubObject>(roadEntity);
                for (int so = subObjs.Length - 1; so >= 0; so--)
                {
                    var sub = subObjs[so].m_SubObject;
                    if (EntityManager.HasComponent<Game.Routes.BusStop>(sub) || EntityManager.HasComponent<Game.Routes.TransportStop>(sub))
                    {
                        subObjs.RemoveAt(so);
                    }
                }
            }

            // Remove legacy SubObjectsUpdated tag if present
            if (EntityManager.HasComponent<SubObjectsUpdated>(roadEntity))
            {
                EntityManager.RemoveComponent<SubObjectsUpdated>(roadEntity);
            }

            // Look up TrafficConfigurationData singleton to identify m_DeadEndNotification
            var trafficConfigQuery = GetEntityQuery(ComponentType.ReadOnly<TrafficConfigurationData>());
            if (trafficConfigQuery.IsEmptyIgnoreFilter)
                return;

            var trafficConfig = trafficConfigQuery.GetSingleton<TrafficConfigurationData>();
            Entity deadEndPrefab = trafficConfig.m_DeadEndNotification;
            if (deadEndPrefab == Entity.Null)
                return;

            // Check roadEntity itself
            ClearDeadEndIconFromEntity(roadEntity, deadEndPrefab);

            // Check all SubLanes of the road
            if (EntityManager.HasBuffer<Game.Net.SubLane>(roadEntity))
            {
                var subLanes = EntityManager.GetBuffer<Game.Net.SubLane>(roadEntity);
                for (int l = 0; l < subLanes.Length; l++)
                {
                    ClearDeadEndIconFromEntity(subLanes[l].m_SubLane, deadEndPrefab);
                }
            }
        }

        private void ClearDeadEndIconFromEntity(Entity entity, Entity deadEndPrefab)
        {
            if (entity == Entity.Null || !EntityManager.Exists(entity) || !EntityManager.HasBuffer<Game.Notifications.IconElement>(entity))
                return;

            var iconElements = EntityManager.GetBuffer<Game.Notifications.IconElement>(entity);
            for (int i = iconElements.Length - 1; i >= 0; i--)
            {
                Entity icon = iconElements[i].m_Icon;
                if (EntityManager.Exists(icon) && EntityManager.HasComponent<PrefabRef>(icon))
                {
                    var pRef = EntityManager.GetComponentData<PrefabRef>(icon).m_Prefab;
                    if (pRef == deadEndPrefab)
                    {
                        if (!EntityManager.HasComponent<Deleted>(icon))
                            EntityManager.AddComponentData(icon, default(Deleted));
                        iconElements.RemoveAt(i);
                    }
                }
            }
        }

        protected override void OnUpdate()
        {
            if (Setting.DiscoveredStopPrefabNames.Count == 0)
            {
                UpdateDiscoveredPrefabs();
            }

            if (_isRepairRequested)
            {
                _isRepairRequested = false;
                ExecuteRepairBrokenRoutes();
                return;
            }

            // -------------------------------------------------------------
            // MULTI-FRAME STAGED GENERATION DISPATCHER
            // -------------------------------------------------------------
            // Stage 2: Wait for game simulation to index stops in spatial trees (ObjectSearchTree, NetSearchTree)
            if (_generationStage == GenerationStage.WaitingForStopIndexing)
            {
                _waitFrameCounter++;
                if (_waitFrameCounter < INDEXING_DELAY_FRAMES)
                    return;

                log.Info($"Stage 2 Complete: Yielded {INDEXING_DELAY_FRAMES} simulation frames. All roadside bus stops are now fully indexed in spatial quadtrees. Beginning staged bus line creation ({_plannedTours.Count} lines)...");
                _generationStage = GenerationStage.InstantiatingRoutes;
                _currentTourIndex = 0;
                return;
            }

            // Stage 3: Incrementally instantiate bus lines across simulation frames
            if (_generationStage == GenerationStage.InstantiatingRoutes)
            {
                int routesCreatedThisTick = 0;
                while (_currentTourIndex < _plannedTours.Count && routesCreatedThisTick < ROUTES_PER_FRAME)
                {
                    var tour = _plannedTours[_currentTourIndex];
                    if (tour.Count >= 4)
                    {
                        int lineNumber = _currentTourIndex + 1;
                        float routeDistanceKm = CalculateTourLength(tour) / 1000f;
                        CreateBusLine(_cachedBusLinePrefabEntity, _cachedBusLineRouteData, tour, lineNumber, 0, _currentTourIndex);
                        log.Info($"Stage 3: Created Bus Line #{lineNumber} with {tour.Count} stops (Est. Length: {routeDistanceKm:F1} km) [Progress: {_currentTourIndex + 1}/{_plannedTours.Count}]");
                    }
                    _currentTourIndex++;
                    routesCreatedThisTick++;
                }

                if (_currentTourIndex >= _plannedTours.Count)
                {
                    log.Info($"=== Generation Complete: Automatically created {_plannedTours.Count} bus lines covering 100% of all city roads & neighborhoods ({_totalStopsActive}/{_totalStopsPlaced} stops active across staged simulation frames). ===");
                    _generationStage = GenerationStage.Idle;
                    _hasRun = true;
                    _isManualRequest = false;
                    _plannedTours.Clear();

                    // Trigger RouteInspector to inspect newly created routes once pathfinding settles
                    var inspector = World.GetExistingSystemManaged<RouteInspector>();
                    if (inspector != null)
                    {
                        inspector.Reset();
                    }
                }
                return;
            }

            if (_hasRun)
                return;

            if (Mod.setting != null && !Mod.setting.AutoGenerateOnLoad && !_isManualRequest)
                return;

            var roadToHub = _roadAssigner.GetRoadToDepotMap();
            if (_depotFinder.AllHubs.Length == 0 ||
                _roadScanner.RoadSegments.Length == 0 ||
                roadToHub.Count == 0)
                return;

            log.Info($"=== Starting AutoBusLines Generation: Global Unified 1-Stop-Per-Block with Multi-Hub Stop Sharing ===");

            if (!FindBusStopPrefabs(out List<Entity> candidatePrefabs, out Entity defaultPrefab))
            {
                log.Error("Could not find any valid Bus Stop Prefabs with ObjectData archetype!");
                _hasRun = true;
                _isManualRequest = false;
                return;
            }

            if (!FindBusLinePrefab(out Entity busLinePrefabEntity, out RouteData busLineRouteData))
            {
                log.Error("Could not find a valid Bus Line Prefab with RouteData archetypes!");
                _hasRun = true;
                _isManualRequest = false;
                return;
            }

            log.Info($"Found {candidatePrefabs.Count} Bus Stop Candidate Prefabs and Bus Line Prefab {busLinePrefabEntity.Index}");

            var rng = new Unity.Mathematics.Random(185392u);

            // Build exclusion zones around Bus Stations, Depots, and existing Platform Stops
            var exclusionPositions = new List<float3>();
            for (int h = 0; h < _depotFinder.AllHubs.Length; h++)
            {
                exclusionPositions.Add(_depotFinder.AllHubs[h].Position);
            }
            for (int p = 0; p < _depotFinder.StationPlatformStops.Length; p++)
            {
                var platformEntity = _depotFinder.StationPlatformStops[p];
                if (EntityManager.HasComponent<Game.Objects.Transform>(platformEntity))
                {
                    exclusionPositions.Add(EntityManager.GetComponentData<Game.Objects.Transform>(platformEntity).m_Position);
                }
            }

            // -------------------------------------------------------------
            // STEP 0: Discover and Index ALL Existing Bus Stops in the City
            // -------------------------------------------------------------
            var existingStopsByRoad = new Dictionary<Entity, List<PlacedStop>>();
            var allExistingStops = new List<PlacedStop>();

            var existingStopEntities = _existingBusStopQuery.ToEntityArray(Allocator.Temp);
            var existingStopPrefabRefs = _existingBusStopQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);
            var existingStopTransforms = _existingBusStopQuery.ToComponentDataArray<Game.Objects.Transform>(Allocator.Temp);

            for (int i = 0; i < existingStopEntities.Length; i++)
            {
                var stopEntity = existingStopEntities[i];
                var prefabEntity = existingStopPrefabRefs[i].m_Prefab;

                if (EntityManager.HasComponent<Game.Objects.OutsideConnection>(stopEntity) ||
                    EntityManager.HasComponent<Game.Net.OutsideConnection>(stopEntity) ||
                    EntityManager.HasComponent<Game.Prefabs.OutsideConnectionData>(prefabEntity))
                {
                    continue;
                }

                string pName = GetPrefabName(prefabEntity);
                if (!string.IsNullOrEmpty(pName) && (
                    pName.IndexOf("Outside Connection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    pName.IndexOf("OutsideConnection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    pName.IndexOf("Placeholder", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    continue;
                }

                if (!EntityManager.HasComponent<TransportStopData>(prefabEntity))
                    continue;

                var stopData = EntityManager.GetComponentData<TransportStopData>(prefabEntity);
                if (stopData.m_TransportType != TransportType.Bus || !stopData.m_PassengerTransport)
                    continue;

                // Station platform bays are handled separately as hub anchors
                if (_depotFinder.StationPlatformStops.Contains(stopEntity))
                    continue;

                var transform = existingStopTransforms[i];
                float3 stopPos = transform.m_Position;
                float3 stopForward = math.mul(transform.m_Rotation, new float3(0, 0, 1));

                Entity attachedRoad = Entity.Null;
                if (EntityManager.HasComponent<Attached>(stopEntity))
                {
                    attachedRoad = EntityManager.GetComponentData<Attached>(stopEntity).m_Parent;
                }
                else if (EntityManager.HasComponent<Game.Common.Owner>(stopEntity))
                {
                    attachedRoad = EntityManager.GetComponentData<Game.Common.Owner>(stopEntity).m_Owner;
                }

                // If a roadside stop erroneously has Owner(road), strip it so it becomes interactable immediately
                if (EntityManager.HasComponent<Game.Common.Owner>(stopEntity))
                {
                    var owner = EntityManager.GetComponentData<Game.Common.Owner>(stopEntity).m_Owner;
                    if (owner != Entity.Null && EntityManager.HasComponent<Game.Net.Edge>(owner))
                    {
                        EntityManager.RemoveComponent<Game.Common.Owner>(stopEntity);
                        log.Info($"Healed roadside bus stop {stopEntity.Index}: removed erroneous Owner component to restore interactivity.");
                    }
                }

                // Determine nearest Hub
                Entity nearestHub = Entity.Null;
                float minHubDist = float.MaxValue;

                for (int h = 0; h < _depotFinder.AllHubs.Length; h++)
                {
                    var hub = _depotFinder.AllHubs[h];
                    float d = math.distance(stopPos, hub.Position);
                    if (hub.IsStation) d *= 0.85f;
                    if (d < minHubDist)
                    {
                        minHubDist = d;
                        nearestHub = hub.HubEntity;
                    }
                }

                var placed = new PlacedStop
                {
                    StopEntity = stopEntity,
                    Position = stopPos,
                    Forward = stopForward,
                    RoadEntity = attachedRoad,
                    HubEntity = nearestHub
                };

                allExistingStops.Add(placed);

                if (attachedRoad != Entity.Null)
                {
                    if (!existingStopsByRoad.ContainsKey(attachedRoad))
                        existingStopsByRoad[attachedRoad] = new List<PlacedStop>();
                    existingStopsByRoad[attachedRoad].Add(placed);
                }
            }

            existingStopEntities.Dispose();
            existingStopPrefabRefs.Dispose();
            existingStopTransforms.Dispose();

            log.Info($"Discovered {allExistingStops.Count} pre-existing bus stops in the city across all neighborhoods");

            // -------------------------------------------------------------
            // STEP 1: Plan Transit Corridors
            // Corridors are continuous street chains across the city.
            // -------------------------------------------------------------
            int minStopsPerLine = Mod.setting != null ? Mod.setting.MinStopsPerLine : MIN_STOPS_PER_LINE;
            int maxStopsPerLine = Mod.setting != null ? Mod.setting.MaxStopsPerLine : MAX_STOPS_PER_LINE;
            float maxRouteLength = Mod.setting != null ? (float)Mod.setting.MaxRouteLength : DEFAULT_MAX_ROUTE_LENGTH;
            float minStopSpacing = Mod.setting != null ? Mod.setting.MinStopSpacing : ABSOLUTE_MIN_STOP_SPACING;

            if (minStopsPerLine < 4) minStopsPerLine = 4;
            if (maxStopsPerLine < minStopsPerLine) maxStopsPerLine = minStopsPerLine;
            if (maxRouteLength < 2000f) maxRouteLength = 2000f;
            if (minStopSpacing < 5f) minStopSpacing = 5f;

            var allCorridors = BuildCorridors();
            allCorridors.Sort((a, b) => b.TotalLength.CompareTo(a.TotalLength));

            // -------------------------------------------------------------
            // STEP 2: Place Bus Stops Across ALL City Blocks & Neighborhoods
            // Ensures 100% city coverage: every neighborhood block gets transit service.
            // On two-way streets, stops alternate curbs to provide two-way coverage.
            // Stops are spaced at ~150-200m and never placed too close to corners (< minStopSpacing).
            // -------------------------------------------------------------
            var blockSpans = BuildBlockSpans(allCorridors);

            var allGlobalStops = new List<PlacedStop>();
            var committedStops = new List<CommittedStopInfo>();

            for (int e = 0; e < allExistingStops.Count; e++)
            {
                committedStops.Add(new CommittedStopInfo
                {
                    Position = allExistingStops[e].Position,
                    Forward = allExistingStops[e].Forward,
                    CorridorIndex = allExistingStops[e].CorridorIndex
                });
            }

            int totalStopsPlaced = 0;
            int existingStopsReused = 0;

            // Sort spans so arterial/longer spans are processed first, then neighborhood side streets
            blockSpans.Sort((a, b) => b.TotalLength.CompareTo(a.TotalLength));

            for (int s = 0; s < blockSpans.Count; s++)
            {
                var span = blockSpans[s];
                if (span.TotalLength < MIN_SEGMENT_LENGTH_FOR_STOP)
                    continue;

                // Check exclusion radius around hubs (stations & depots)
                bool isNearHub = false;
                for (int ep = 0; ep < exclusionPositions.Count; ep++)
                {
                    if (math.distance(span.MidPosition, exclusionPositions[ep]) < HUB_EXCLUSION_RADIUS)
                    {
                        isNearHub = true;
                        break;
                    }
                }
                if (isNearHub)
                    continue;

                // Halve the stops in the algorithm: if a committed stop is already within STOP_PROXIMITY_SERVED_RADIUS (110m), this block is already served!
                bool isAlreadyServed = false;
                for (int k = 0; k < committedStops.Count; k++)
                {
                    if (math.distance(span.MidPosition, committedStops[k].Position) < STOP_PROXIMITY_SERVED_RADIUS)
                    {
                        isAlreadyServed = true;
                        break;
                    }
                }
                if (isAlreadyServed)
                    continue;

                // Reuse pre-existing stop if present on this span
                if (TryFindExistingStopOnSpan(span, existingStopsByRoad, out PlacedStop reusedStop))
                {
                    reusedStop.CorridorIndex = span.CorridorIndex;
                    reusedStop.SpanOrderInCorridor = span.SpanOrderInCorridor;
                    reusedStop.IsOutbound = (math.dot(reusedStop.Forward, span.CorridorForwardTangent) >= 0f);
                    allGlobalStops.Add(reusedStop);
                    existingStopsReused++;
                    committedStops.Add(new CommittedStopInfo
                    {
                        Position = reusedStop.Position,
                        Forward = reusedStop.Forward,
                        CorridorIndex = span.CorridorIndex
                    });
                    continue;
                }

                // Place alternating kerb stop
                if (TryPlaceAlternatingKerbStop(span, span.MidEdge, span.MidT, span.MidPosition, span.CorridorForwardTangent,
                                                span.SpanOrderInCorridor,
                                                committedStops, candidatePrefabs, ref rng,
                                                minStopSpacing, out PlacedStop placedStop))
                {
                    allGlobalStops.Add(placedStop);
                    totalStopsPlaced++;
                }
            }

            log.Info($"Step 2 Complete: {allGlobalStops.Count} total stops available across the city ({totalStopsPlaced} newly placed, {existingStopsReused} existing reused). 100% neighborhood coverage achieved.");

            // -------------------------------------------------------------
            // STEP 3: Plan the bus loops (see BusLineGenerator.Planning.cs)
            // Builds validated closed cycles of legal, connected road edges: one-way directions are
            // respected, hairpin U-turns are only used at dead ends, and every loop stays within
            // maxRouteLength.
            // -------------------------------------------------------------
            var allTours = PlanTours(allGlobalStops, allCorridors, minStopsPerLine, maxStopsPerLine, maxRouteLength, out var servedStopEntities);

            // Ensure any station platform bays included in planned tours are present in allGlobalStops for accurate tracking
            for (int t = 0; t < allTours.Count; t++)
            {
                var tour = allTours[t];
                for (int s = 0; s < tour.Count; s++)
                {
                    bool found = false;
                    for (int g = 0; g < allGlobalStops.Count; g++)
                    {
                        if (allGlobalStops[g].StopEntity == tour[s].StopEntity)
                        {
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        allGlobalStops.Add(tour[s]);
                    }
                }
            }

            if (allTours.Count == 0)
            {
                log.Warn("No bus tours could be formed from the road network.");
                _generationStage = GenerationStage.Idle;
                _hasRun = true;
                _isManualRequest = false;
                return;
            }

            // Stage 1 Completion: Store planned tours and prefab info for staged execution across subsequent frames
            _plannedTours = new List<List<PlacedStop>>(allTours);
            _cachedBusLinePrefabEntity = busLinePrefabEntity;
            _cachedBusLineRouteData = busLineRouteData;
            _totalStopsPlaced = allGlobalStops.Count;
            _totalStopsActive = servedStopEntities.Count;
            _generationStage = GenerationStage.WaitingForStopIndexing;
            _waitFrameCounter = 0;

            log.Info($"Stage 1 Complete: Placed {_totalStopsPlaced} roadside bus stops and planned {_plannedTours.Count} bus lines. Yielding {INDEXING_DELAY_FRAMES} simulation frames for spatial quadtrees and net indexing to settle before drawing routes...");
            return;
        }

        private bool TryFindExistingStopOnSpan(BlockSpan span,
            Dictionary<Entity, List<PlacedStop>> existingStopsByRoad, out PlacedStop foundStop)
        {
            foundStop = default;
            for (int e = 0; e < span.Edges.Count; e++)
            {
                var roadEntity = span.Edges[e].EdgeEntity;
                if (TryFindExistingStopOnRoad(roadEntity, span.MidPosition, span.HubEntity, existingStopsByRoad, out foundStop))
                {
                    return true;
                }
            }
            return false;
        }

        private bool TryFindExistingStopOnRoad(Entity roadEntity, float3 centerPos, Entity hubEntity,
            Dictionary<Entity, List<PlacedStop>> existingStopsByRoad, out PlacedStop foundStop)
        {
            foundStop = default;

            // 1. Check if attached road directly matches
            if (existingStopsByRoad.ContainsKey(roadEntity) && existingStopsByRoad[roadEntity].Count > 0)
            {
                foundStop = existingStopsByRoad[roadEntity][0];
                return true;
            }

            // 2. Check SubObject buffer on roadEntity
            if (EntityManager.HasBuffer<Game.Objects.SubObject>(roadEntity))
            {
                var subObjects = EntityManager.GetBuffer<Game.Objects.SubObject>(roadEntity);
                for (int sub = 0; sub < subObjects.Length; sub++)
                {
                    var subEnt = subObjects[sub].m_SubObject;
                    if (EntityManager.HasComponent<Game.Routes.TransportStop>(subEnt) &&
                        EntityManager.HasComponent<PrefabRef>(subEnt))
                    {
                        var p = EntityManager.GetComponentData<PrefabRef>(subEnt).m_Prefab;
                        if (EntityManager.HasComponent<TransportStopData>(p))
                        {
                            var td = EntityManager.GetComponentData<TransportStopData>(p);
                            if (td.m_TransportType == TransportType.Bus && td.m_PassengerTransport)
                            {
                                float3 sPos = centerPos;
                                float3 sFwd = new float3(0, 0, 1);
                                if (EntityManager.HasComponent<Game.Objects.Transform>(subEnt))
                                {
                                    var tForm = EntityManager.GetComponentData<Game.Objects.Transform>(subEnt);
                                    sPos = tForm.m_Position;
                                    sFwd = math.mul(tForm.m_Rotation, new float3(0, 0, 1));
                                }

                                foundStop = new PlacedStop
                                {
                                    StopEntity = subEnt,
                                    Position = sPos,
                                    Forward = sFwd,
                                    RoadEntity = roadEntity,
                                    HubEntity = hubEntity,
                                    AngleFromHub = 0f,
                                    DistFromHub = 0f,
                                    IsOutbound = true
                                };
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        private bool IsOneWayRoad(Entity roadEntity, out bool forwardFlow)
        {
            forwardFlow = true;
            if (EntityManager.HasBuffer<Game.Net.SubLane>(roadEntity))
            {
                var subLanes = EntityManager.GetBuffer<Game.Net.SubLane>(roadEntity);
                int forwardCarLanes = 0;
                int backwardCarLanes = 0;

                for (int i = 0; i < subLanes.Length; i++)
                {
                    var laneEntity = subLanes[i].m_SubLane;
                    if (EntityManager.HasComponent<Game.Net.CarLane>(laneEntity))
                    {
                        var carLane = EntityManager.GetComponentData<Game.Net.CarLane>(laneEntity);
                        if ((carLane.m_Flags & Game.Net.CarLaneFlags.Invert) != 0)
                        {
                            backwardCarLanes++;
                        }
                        else
                        {
                            forwardCarLanes++;
                        }
                    }
                }

                if (forwardCarLanes > 0 && backwardCarLanes == 0)
                {
                    forwardFlow = true;
                    return true;
                }
                if (backwardCarLanes > 0 && forwardCarLanes == 0)
                {
                    forwardFlow = false;
                    return true;
                }
            }
            return false;
        }

        private bool TryPlaceAlternatingKerbStop(BlockSpan span, Entity roadEntity, float t,
                                                 float3 roadPos, float3 corridorTan,
                                                 int spanOrderInCorridor,
                                                 List<CommittedStopInfo> committedStops,
                                                 List<Entity> candidatePrefabs,
                                                 ref Unity.Mathematics.Random rng,
                                                 float minSpacingCap,
                                                 out PlacedStop placedStop)
        {
            placedStop = default;
            bool isReverseSide = (spanOrderInCorridor % 2 != 0);

            float3 forward = corridorTan;
            if (math.lengthsq(forward) < 0.001f)
                forward = span.MidTangent;

            bool isOneWay = IsOneWayRoad(roadEntity, out bool forwardFlow);
            bool isOutbound;

            if (isOneWay)
            {
                // One-way road: align stop orientation with the legal traffic flow direction.
                // Never place stops on the reverse-flow side facing oncoming traffic.
                if (EntityManager.HasComponent<Game.Net.Curve>(roadEntity))
                {
                    var curve = EntityManager.GetComponentData<Game.Net.Curve>(roadEntity);
                    float3 roadTan = MathUtils.Tangent(curve.m_Bezier, t);
                    if (math.lengthsq(roadTan) > 0.001f)
                    {
                        roadTan = math.normalizesafe(new float3(roadTan.x, 0f, roadTan.z));
                        forward = forwardFlow ? roadTan : -roadTan;
                    }
                    else
                    {
                        forward = forwardFlow ? forward : -forward;
                    }
                }
                else
                {
                    forward = forwardFlow ? forward : -forward;
                }

                isOutbound = (math.dot(forward, span.CorridorForwardTangent) >= 0f);
            }
            else
            {
                // Two-way road: Staggered / Alternating stop placement along the corridor:
                // Even span index (k % 2 == 0): Stop on forward right sidewalk (outbound travel direction)
                // Odd span index (k % 2 == 1): Stop on reverse right sidewalk (opposite side / inbound travel direction)
                // This ensures balanced two-way transit service across adjacent blocks without clustering!
                if (isReverseSide)
                {
                    forward = -forward;
                    isOutbound = false;
                }
                else
                {
                    isOutbound = true;
                }
            }

            // Calculate road half-width dynamically from NetCompositionData to place stops accurately on the outer curb
            float roadHalfWidth = 6.0f;
            if (EntityManager.HasComponent<Game.Net.Composition>(roadEntity))
            {
                var comp = EntityManager.GetComponentData<Game.Net.Composition>(roadEntity);
                if (comp.m_Edge != Entity.Null && EntityManager.HasComponent<Game.Prefabs.NetCompositionData>(comp.m_Edge))
                {
                    var netComp = EntityManager.GetComponentData<Game.Prefabs.NetCompositionData>(comp.m_Edge);
                    if (netComp.m_Width > 2.0f)
                    {
                        roadHalfWidth = netComp.m_Width * 0.5f;
                    }
                }
            }

            // Check Left-Hand Traffic rule of the city
            bool isLeftHandTraffic = false;
            var cityConfig = World.GetExistingSystemManaged<Game.City.CityConfigurationSystem>();
            if (cityConfig != null)
            {
                isLeftHandTraffic = cityConfig.leftHandTraffic;
            }

            // Normal pointing towards the passenger boarding curb (right side for RHT, left side for LHT)
            float3 curbNormal = isLeftHandTraffic 
                ? new float3(-forward.z, 0f, forward.x) 
                : new float3(forward.z, 0f, -forward.x);

            // Sidewalk curb is typically at outer road boundary minus lane/curb inset
            float curbOffset = math.max(3.2f, roadHalfWidth - 1.2f);
            float3 stopPos = roadPos + curbNormal * curbOffset;

            // Proximity cap check: ensure no other stop is too close (e.g. at an intersection corner)
            if (minSpacingCap > 0f)
            {
                for (int k = 0; k < committedStops.Count; k++)
                {
                    if (math.distance(stopPos, committedStops[k].Position) < minSpacingCap)
                        return false;
                }
            }

            // Select bus stop prefab from candidate pool (randomized if multiple, or exact if 1 selected)
            Entity selectedPrefab = candidatePrefabs[0];
            if (candidatePrefabs.Count > 1)
            {
                int randIdx = rng.NextInt(0, candidatePrefabs.Count);
                selectedPrefab = candidatePrefabs[randIdx];
            }

            if (!TryCreateBusStopEntity(roadEntity, t, stopPos, forward, span.HubEntity, span.CorridorIndex, span.SpanOrderInCorridor, isOutbound, selectedPrefab, out placedStop))
                return false;

            committedStops.Add(new CommittedStopInfo
            {
                Position = stopPos,
                Forward = forward,
                CorridorIndex = span.CorridorIndex
            });

            return true;
        }

        private bool TryCreateBusStopEntity(Entity roadEntity, float t, float3 pos, float3 forward, Entity hubEntity, int corridorIndex, int spanOrderInCorridor, bool isOutbound, Entity busStopPrefabEntity, out PlacedStop stop)
        {
            stop = default;
            quaternion rot = quaternion.LookRotationSafe(forward, new float3(0, 1, 0));

            if (!EntityManager.HasComponent<ObjectData>(busStopPrefabEntity))
            {
                log.Error($"Bus stop prefab {busStopPrefabEntity.Index} is missing ObjectData component.");
                return false;
            }

            var objectData = EntityManager.GetComponentData<ObjectData>(busStopPrefabEntity);
            if (!objectData.m_Archetype.Valid)
            {
                log.Error($"Bus stop prefab {busStopPrefabEntity.Index} has invalid ObjectData archetype.");
                return false;
            }

            // 1. Create runtime world object entity from prefab's registered archetype
            Entity stopEntity = EntityManager.CreateEntity(objectData.m_Archetype);

            // 2. Set prefab reference & runtime transform
            EntityManager.SetComponentData(stopEntity, new PrefabRef(busStopPrefabEntity));
            EntityManager.SetComponentData(stopEntity, new Game.Objects.Transform(pos, rot));

            if (EntityManager.HasComponent<CustomMeshColor>(stopEntity))
            {
                EntityManager.SetComponentEnabled<CustomMeshColor>(stopEntity, false);
            }

            // 3. Set transport stop properties
            if (EntityManager.HasComponent<TransportStopData>(busStopPrefabEntity))
            {
                var stopData = EntityManager.GetComponentData<TransportStopData>(busStopPrefabEntity);
                EntityManager.SetComponentData(stopEntity, new Game.Routes.TransportStop
                {
                    m_ComfortFactor = stopData.m_ComfortFactor,
                    m_LoadingFactor = stopData.m_LoadingFactor
                });
            }
            else
            {
                EntityManager.SetComponentData(stopEntity, new Game.Routes.TransportStop { m_ComfortFactor = 0.5f, m_LoadingFactor = 1.0f });
            }

            // 4. Set road attachment using exact curve projection matching vanilla GenerateObjectsSystem.CreateAttached
            float attachedCurvePos = t;
            if (EntityManager.HasComponent<Curve>(roadEntity))
            {
                var roadCurve = EntityManager.GetComponentData<Curve>(roadEntity);
                MathUtils.Distance(roadCurve.m_Bezier, pos, out attachedCurvePos);
                float roadLen = MathUtils.Length(roadCurve.m_Bezier);
                if (roadLen >= 42.0f)
                {
                    float minT = 20.0f / roadLen;
                    float maxT = 1.0f - (22.0f / roadLen);
                    attachedCurvePos = math.clamp(attachedCurvePos, minT, maxT);
                }
                else
                {
                    attachedCurvePos = math.clamp(attachedCurvePos, 0.30f, 0.70f);
                }
            }

            if (!EntityManager.HasComponent<Attached>(stopEntity))
                EntityManager.AddComponentData(stopEntity, new Attached(roadEntity, Entity.Null, attachedCurvePos));
            else
                EntityManager.SetComponentData(stopEntity, new Attached(roadEntity, Entity.Null, attachedCurvePos));

            if (EntityManager.HasComponent<Game.Common.Owner>(stopEntity))
                EntityManager.RemoveComponent<Game.Common.Owner>(stopEntity);

            // Ensure BusStop marker tag exists for bus transport systems
            if (!EntityManager.HasComponent<Game.Routes.BusStop>(stopEntity))
                EntityManager.AddComponentData(stopEntity, default(Game.Routes.BusStop));

            // 5. Ensure ConnectedRoute buffer and BoardingVehicle component exist
            if (!EntityManager.HasBuffer<ConnectedRoute>(stopEntity))
                EntityManager.AddBuffer<ConnectedRoute>(stopEntity);

            if (!EntityManager.HasComponent<BoardingVehicle>(stopEntity))
                EntityManager.AddComponentData(stopEntity, default(BoardingVehicle));

            // 6. Add lifecycle tags for rendering and spatial search indexing
            if (!EntityManager.HasComponent<Created>(stopEntity))
                EntityManager.AddComponentData(stopEntity, default(Created));
            if (!EntityManager.HasComponent<Updated>(stopEntity))
                EntityManager.AddComponentData(stopEntity, default(Updated));
            if (!EntityManager.HasComponent<BatchesUpdated>(stopEntity))
                EntityManager.AddComponentData(stopEntity, default(BatchesUpdated));

            // 7. Clean up any legacy SubObject / DeadEnd warnings on roadEntity
            ClearRoadDeadEndNotifications(roadEntity);

            stop = new PlacedStop
            {
                StopEntity = stopEntity,
                Position = pos,
                Forward = forward,
                RoadEntity = roadEntity,
                HubEntity = hubEntity,
                CorridorIndex = corridorIndex,
                SpanOrderInCorridor = spanOrderInCorridor,
                AngleFromHub = 0f,
                DistFromHub = 0f,
                IsOutbound = isOutbound
            };
            return true;
        }

        private List<RoadCorridor> BuildCorridors()
        {
            var corridors = new List<RoadCorridor>();
            var visitedEdges = new HashSet<Entity>();

            var nodeToEdges = new Dictionary<Entity, List<EdgeEndpoint>>();

            for (int i = 0; i < _roadScanner.RoadSegments.Length; i++)
            {
                var roadEntity = _roadScanner.RoadSegments[i];
                if (!EntityManager.HasComponent<Edge>(roadEntity) || !EntityManager.HasComponent<Curve>(roadEntity))
                    continue;

                var edge = EntityManager.GetComponentData<Edge>(roadEntity);
                var curve = EntityManager.GetComponentData<Curve>(roadEntity);

                float3 startTan = math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 0f), new float3(0, 0, 1));
                float3 endTan = -math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 1f), new float3(0, 0, 1));

                if (edge.m_Start != Entity.Null)
                {
                    if (!nodeToEdges.ContainsKey(edge.m_Start))
                        nodeToEdges[edge.m_Start] = new List<EdgeEndpoint>();
                    nodeToEdges[edge.m_Start].Add(new EdgeEndpoint { EdgeEntity = roadEntity, IsStartNode = true, OutwardTangent = startTan });
                }

                if (edge.m_End != Entity.Null)
                {
                    if (!nodeToEdges.ContainsKey(edge.m_End))
                        nodeToEdges[edge.m_End] = new List<EdgeEndpoint>();
                    nodeToEdges[edge.m_End].Add(new EdgeEndpoint { EdgeEntity = roadEntity, IsStartNode = false, OutwardTangent = endTan });
                }
            }

            for (int i = 0; i < _roadScanner.RoadSegments.Length; i++)
            {
                var seedEntity = _roadScanner.RoadSegments[i];
                if (visitedEdges.Contains(seedEntity))
                    continue;
                if (!EntityManager.HasComponent<Edge>(seedEntity) || !EntityManager.HasComponent<Curve>(seedEntity))
                    continue;

                var corridor = new RoadCorridor();

                var seedCurve = EntityManager.GetComponentData<Curve>(seedEntity);
                var seedEdge = EntityManager.GetComponentData<Edge>(seedEntity);
                float seedLen = MathUtils.Length(seedCurve.m_Bezier);

                corridor.Segments.Add(new RoadSegmentChain { RoadEntity = seedEntity, Length = seedLen, IsReversed = false });
                corridor.TotalLength += seedLen;
                visitedEdges.Add(seedEntity);

                // Trace forward
                Entity currentEndNode = seedEdge.m_End;
                float3 currentHeading = math.normalizesafe(MathUtils.Tangent(seedCurve.m_Bezier, 1f), new float3(0, 0, 1));

                while (currentEndNode != Entity.Null && nodeToEdges.ContainsKey(currentEndNode))
                {
                    var candidates = nodeToEdges[currentEndNode];
                    Entity bestNextEdge = Entity.Null;
                    bool bestIsReversed = false;
                    Entity nextEndNode = Entity.Null;
                    float3 nextHeading = currentHeading;
                    float bestAlignment = 0.50f;

                    for (int c = 0; c < candidates.Count; c++)
                    {
                        var cand = candidates[c];
                        if (visitedEdges.Contains(cand.EdgeEntity))
                            continue;

                        float alignment = math.dot(currentHeading, cand.OutwardTangent);
                        if (alignment > bestAlignment)
                        {
                            bestAlignment = alignment;
                            bestNextEdge = cand.EdgeEntity;
                            bestIsReversed = !cand.IsStartNode;

                            var candEdge = EntityManager.GetComponentData<Edge>(cand.EdgeEntity);
                            nextEndNode = cand.IsStartNode ? candEdge.m_End : candEdge.m_Start;

                            var candCurve = EntityManager.GetComponentData<Curve>(cand.EdgeEntity);
                            nextHeading = cand.IsStartNode
                                ? math.normalizesafe(MathUtils.Tangent(candCurve.m_Bezier, 1f), new float3(0, 0, 1))
                                : -math.normalizesafe(MathUtils.Tangent(candCurve.m_Bezier, 0f), new float3(0, 0, 1));
                        }
                    }

                    if (bestNextEdge != Entity.Null)
                    {
                        var nextCurve = EntityManager.GetComponentData<Curve>(bestNextEdge);
                        float nextLen = MathUtils.Length(nextCurve.m_Bezier);
                        corridor.Segments.Add(new RoadSegmentChain { RoadEntity = bestNextEdge, Length = nextLen, IsReversed = bestIsReversed });
                        corridor.TotalLength += nextLen;
                        visitedEdges.Add(bestNextEdge);
                        currentEndNode = nextEndNode;
                        currentHeading = nextHeading;
                    }
                    else
                    {
                        break;
                    }
                }

                // Trace backward
                Entity currentStartNode = seedEdge.m_Start;
                float3 currentBackwardHeading = -math.normalizesafe(MathUtils.Tangent(seedCurve.m_Bezier, 0f), new float3(0, 0, 1));

                while (currentStartNode != Entity.Null && nodeToEdges.ContainsKey(currentStartNode))
                {
                    var candidates = nodeToEdges[currentStartNode];
                    Entity bestPrevEdge = Entity.Null;
                    bool bestIsReversed = false;
                    Entity nextStartNode = Entity.Null;
                    float3 nextBackwardHeading = currentBackwardHeading;
                    float bestAlignment = 0.50f;

                    for (int c = 0; c < candidates.Count; c++)
                    {
                        var cand = candidates[c];
                        if (visitedEdges.Contains(cand.EdgeEntity))
                            continue;

                        float alignment = math.dot(currentBackwardHeading, cand.OutwardTangent);
                        if (alignment > bestAlignment)
                        {
                            bestAlignment = alignment;
                            bestPrevEdge = cand.EdgeEntity;
                            bestIsReversed = cand.IsStartNode;

                            var candEdge = EntityManager.GetComponentData<Edge>(cand.EdgeEntity);
                            nextStartNode = cand.IsStartNode ? candEdge.m_End : candEdge.m_Start;

                            var candCurve = EntityManager.GetComponentData<Curve>(cand.EdgeEntity);
                            nextBackwardHeading = cand.IsStartNode
                                ? -math.normalizesafe(MathUtils.Tangent(candCurve.m_Bezier, 1f), new float3(0, 0, 1))
                                : math.normalizesafe(MathUtils.Tangent(candCurve.m_Bezier, 0f), new float3(0, 0, 1));
                        }
                    }

                    if (bestPrevEdge != Entity.Null)
                    {
                        var prevCurve = EntityManager.GetComponentData<Curve>(bestPrevEdge);
                        float prevLen = MathUtils.Length(prevCurve.m_Bezier);
                        corridor.Segments.Insert(0, new RoadSegmentChain { RoadEntity = bestPrevEdge, Length = prevLen, IsReversed = bestIsReversed });
                        corridor.TotalLength += prevLen;
                        visitedEdges.Add(bestPrevEdge);
                        currentStartNode = nextStartNode;
                        currentBackwardHeading = nextBackwardHeading;
                    }
                    else
                    {
                        break;
                    }
                }

                corridors.Add(corridor);
            }

            return corridors;
        }

        private float3 GetCorridorEndpoint(RoadCorridor corr, bool isStart)
        {
            if (corr.Segments.Count == 0)
                return float3.zero;

            var seg = isStart ? corr.Segments[0] : corr.Segments[corr.Segments.Count - 1];
            if (EntityManager.HasComponent<Curve>(seg.RoadEntity))
            {
                var curve = EntityManager.GetComponentData<Curve>(seg.RoadEntity);
                if (isStart)
                    return seg.IsReversed ? MathUtils.Position(curve.m_Bezier, 1f) : MathUtils.Position(curve.m_Bezier, 0f);
                else
                    return seg.IsReversed ? MathUtils.Position(curve.m_Bezier, 0f) : MathUtils.Position(curve.m_Bezier, 1f);
            }
            return float3.zero;
        }

        private struct SpanNodeConnection
        {
            public Entity EdgeEntity;
            public bool IsStartNode;
        }

        private List<BlockSpan> BuildBlockSpans(List<RoadCorridor> corridors)
        {
            var blockSpans = new List<BlockSpan>();
            var visitedEdges = new HashSet<Entity>();

            // Map each edge to its Corridor Index and Corridor Total Length for priority
            var edgeToCorridorIndex = new Dictionary<Entity, int>();
            var edgeToCorridorLength = new Dictionary<Entity, float>();
            for (int c = 0; c < corridors.Count; c++)
            {
                var corr = corridors[c];
                for (int s = 0; s < corr.Segments.Count; s++)
                {
                    edgeToCorridorIndex[corr.Segments[s].RoadEntity] = c;
                    edgeToCorridorLength[corr.Segments[s].RoadEntity] = corr.TotalLength;
                }
            }

            // Build node degree and adjacency for drivable roads
            var nodeToConnections = new Dictionary<Entity, List<SpanNodeConnection>>();
            for (int i = 0; i < _roadScanner.RoadSegments.Length; i++)
            {
                var roadEntity = _roadScanner.RoadSegments[i];
                if (!EntityManager.HasComponent<Edge>(roadEntity) || !EntityManager.HasComponent<Curve>(roadEntity))
                    continue;

                var edge = EntityManager.GetComponentData<Edge>(roadEntity);
                if (edge.m_Start != Entity.Null)
                {
                    if (!nodeToConnections.ContainsKey(edge.m_Start))
                        nodeToConnections[edge.m_Start] = new List<SpanNodeConnection>();
                    nodeToConnections[edge.m_Start].Add(new SpanNodeConnection { EdgeEntity = roadEntity, IsStartNode = true });
                }

                if (edge.m_End != Entity.Null)
                {
                    if (!nodeToConnections.ContainsKey(edge.m_End))
                        nodeToConnections[edge.m_End] = new List<SpanNodeConnection>();
                    nodeToConnections[edge.m_End].Add(new SpanNodeConnection { EdgeEntity = roadEntity, IsStartNode = false });
                }
            }

            int spanIdCounter = 0;
            int mergedDegree2Count = 0;

            for (int i = 0; i < _roadScanner.RoadSegments.Length; i++)
            {
                var seedEntity = _roadScanner.RoadSegments[i];
                if (visitedEdges.Contains(seedEntity))
                    continue;
                if (!EntityManager.HasComponent<Edge>(seedEntity) || !EntityManager.HasComponent<Curve>(seedEntity))
                    continue;

                var seedEdge = EntityManager.GetComponentData<Edge>(seedEntity);
                var seedCurve = EntityManager.GetComponentData<Curve>(seedEntity);
                float seedLen = MathUtils.Length(seedCurve.m_Bezier);

                var spanEdges = new List<SpanEdge>();
                spanEdges.Add(new SpanEdge { EdgeEntity = seedEntity, IsReversed = false, Length = seedLen });
                visitedEdges.Add(seedEntity);

                // 1. Trace forward through degree-2 pass-through nodes
                Entity currentEndNode = seedEdge.m_End;
                Entity currentEdge = seedEntity;

                while (currentEndNode != Entity.Null &&
                       nodeToConnections.ContainsKey(currentEndNode) &&
                       nodeToConnections[currentEndNode].Count == 2)
                {
                    var conns = nodeToConnections[currentEndNode];
                    Entity nextEdge = Entity.Null;
                    bool nextIsStart = false;

                    for (int c = 0; c < conns.Count; c++)
                    {
                        if (conns[c].EdgeEntity != currentEdge)
                        {
                            nextEdge = conns[c].EdgeEntity;
                            nextIsStart = conns[c].IsStartNode;
                            break;
                        }
                    }

                    if (nextEdge == Entity.Null || visitedEdges.Contains(nextEdge))
                        break;
                    if (!EntityManager.HasComponent<Edge>(nextEdge) || !EntityManager.HasComponent<Curve>(nextEdge))
                        break;

                    var nextEdgeData = EntityManager.GetComponentData<Edge>(nextEdge);
                    var nextCurve = EntityManager.GetComponentData<Curve>(nextEdge);
                    float nextLen = MathUtils.Length(nextCurve.m_Bezier);

                    bool isReversed = !nextIsStart;
                    spanEdges.Add(new SpanEdge { EdgeEntity = nextEdge, IsReversed = isReversed, Length = nextLen });
                    visitedEdges.Add(nextEdge);
                    mergedDegree2Count++;

                    currentEdge = nextEdge;
                    currentEndNode = nextIsStart ? nextEdgeData.m_End : nextEdgeData.m_Start;
                }

                // 2. Trace backward through degree-2 pass-through nodes
                Entity currentStartNode = seedEdge.m_Start;
                currentEdge = seedEntity;

                while (currentStartNode != Entity.Null &&
                       nodeToConnections.ContainsKey(currentStartNode) &&
                       nodeToConnections[currentStartNode].Count == 2)
                {
                    var conns = nodeToConnections[currentStartNode];
                    Entity prevEdge = Entity.Null;
                    bool prevIsStart = false;

                    for (int c = 0; c < conns.Count; c++)
                    {
                        if (conns[c].EdgeEntity != currentEdge)
                        {
                            prevEdge = conns[c].EdgeEntity;
                            prevIsStart = conns[c].IsStartNode;
                            break;
                        }
                    }

                    if (prevEdge == Entity.Null || visitedEdges.Contains(prevEdge))
                        break;
                    if (!EntityManager.HasComponent<Edge>(prevEdge) || !EntityManager.HasComponent<Curve>(prevEdge))
                        break;

                    var prevEdgeData = EntityManager.GetComponentData<Edge>(prevEdge);
                    var prevCurve = EntityManager.GetComponentData<Curve>(prevEdge);
                    float prevLen = MathUtils.Length(prevCurve.m_Bezier);

                    bool isReversed = prevIsStart;
                    spanEdges.Insert(0, new SpanEdge { EdgeEntity = prevEdge, IsReversed = isReversed, Length = prevLen });
                    visitedEdges.Add(prevEdge);
                    mergedDegree2Count++;

                    currentEdge = prevEdge;
                    currentStartNode = prevIsStart ? prevEdgeData.m_End : prevEdgeData.m_Start;
                }

                // 3. Compute total length and find exact midpoint along the multi-edge span
                float totalLength = 0f;
                for (int e = 0; e < spanEdges.Count; e++)
                {
                    totalLength += spanEdges[e].Length;
                }

                int startDegree = (currentStartNode != Entity.Null && nodeToConnections.TryGetValue(currentStartNode, out var startConns)) ? startConns.Count : 0;
                int endDegree = (currentEndNode != Entity.Null && nodeToConnections.TryGetValue(currentEndNode, out var endConns)) ? endConns.Count : 0;

                // Exclude isolated road spans where both ends are dead ends (no network connectivity)
                if (startDegree <= 1 && endDegree <= 1)
                {
                    log.Info($"Excluded isolated road span (both ends dead-end): Length {totalLength:F1}m, Edges {spanEdges.Count}");
                    continue;
                }

                // Safe Center-Edge Stop Placement:
                // NEVER place stops near edge boundary nodes or intersection conflict zones.
                // A bus stopping box requires 15m vehicle length + 5m clearance.
                // We pick the best host edge in spanEdges (preferring length >= 42m, closest to span center)
                // and place the stop at safeT (clamped to >= 20m from start node and >= 22m from end node).
                SpanEdge bestEdge = spanEdges[0];
                float targetSpanDist = totalLength * 0.5f;

                // 1. Identify eligible edges with length >= 42m (room for 20m start clearance + 22m end clearance)
                var eligibleEdges = new List<SpanEdge>();
                for (int e = 0; e < spanEdges.Count; e++)
                {
                    if (spanEdges[e].Length >= 42.0f)
                        eligibleEdges.Add(spanEdges[e]);
                }

                if (eligibleEdges.Count > 0)
                {
                    // Pick the eligible edge whose center is closest to span midpoint
                    float bestSpanDistDiff = float.MaxValue;
                    float accDist = 0f;
                    for (int e = 0; e < spanEdges.Count; e++)
                    {
                        var se = spanEdges[e];
                        float edgeCenterSpanDist = accDist + se.Length * 0.5f;
                        if (eligibleEdges.Contains(se))
                        {
                            float diff = math.abs(edgeCenterSpanDist - targetSpanDist);
                            if (diff < bestSpanDistDiff)
                            {
                                bestSpanDistDiff = diff;
                                bestEdge = se;
                            }
                        }
                        accDist += se.Length;
                    }
                }
                else
                {
                    // If no edge is >= 42m, pick the longest edge in the span
                    for (int e = 1; e < spanEdges.Count; e++)
                    {
                        if (spanEdges[e].Length > bestEdge.Length)
                            bestEdge = spanEdges[e];
                    }
                }

                float bestLen = bestEdge.Length;
                float safeT = 0.5f;
                if (bestLen >= 42.0f)
                {
                    float minT = 20.0f / bestLen;
                    float maxT = 1.0f - (22.0f / bestLen);
                    safeT = math.clamp(0.5f, minT, maxT);
                }
                else
                {
                    safeT = 0.5f;
                }

                float actualT = bestEdge.IsReversed ? (1.0f - safeT) : safeT;
                var bestCurve = EntityManager.GetComponentData<Curve>(bestEdge.EdgeEntity);

                Entity midEdge = bestEdge.EdgeEntity;
                float midT = actualT;
                float3 midPos = MathUtils.Position(bestCurve.m_Bezier, actualT);

                float3 curveTan = MathUtils.Tangent(bestCurve.m_Bezier, actualT);
                if (bestEdge.IsReversed)
                    curveTan = -curveTan;

                float3 midTan = math.normalizesafe(curveTan, new float3(0, 0, 1));

                // Determine nearest Hub for this span based on its midpoint
                Entity nearestHub = Entity.Null;
                float minDistance = float.MaxValue;
                for (int j = 0; j < _depotFinder.AllHubs.Length; j++)
                {
                    var hub = _depotFinder.AllHubs[j];
                    float dist = math.distance(midPos, hub.Position);
                    if (hub.IsStation) dist *= 0.85f;
                    if (dist < minDistance)
                    {
                        minDistance = dist;
                        nearestHub = hub.HubEntity;
                    }
                }

                int corrIdx = -1;
                float corrLen = 0f;
                if (edgeToCorridorIndex.ContainsKey(midEdge))
                {
                    corrIdx = edgeToCorridorIndex[midEdge];
                    corrLen = edgeToCorridorLength[midEdge];
                }
                else
                {
                    for (int e = 0; e < spanEdges.Count; e++)
                    {
                        if (edgeToCorridorIndex.ContainsKey(spanEdges[e].EdgeEntity))
                        {
                            corrIdx = edgeToCorridorIndex[spanEdges[e].EdgeEntity];
                            corrLen = edgeToCorridorLength[spanEdges[e].EdgeEntity];
                            break;
                        }
                    }
                }

                // Compute start and end tangents and positions for planar face extraction
                var firstSpanEdge = spanEdges[0];
                var firstCurve = EntityManager.GetComponentData<Curve>(firstSpanEdge.EdgeEntity);
                float3 startPos = firstSpanEdge.IsReversed ? MathUtils.Position(firstCurve.m_Bezier, 1f) : MathUtils.Position(firstCurve.m_Bezier, 0f);
                float3 startTan = firstSpanEdge.IsReversed
                    ? -math.normalizesafe(MathUtils.Tangent(firstCurve.m_Bezier, 1f), new float3(0, 0, 1))
                    : math.normalizesafe(MathUtils.Tangent(firstCurve.m_Bezier, 0f), new float3(0, 0, 1));

                var lastSpanEdge = spanEdges[spanEdges.Count - 1];
                var lastCurve = EntityManager.GetComponentData<Curve>(lastSpanEdge.EdgeEntity);
                float3 endPos = lastSpanEdge.IsReversed ? MathUtils.Position(lastCurve.m_Bezier, 0f) : MathUtils.Position(lastCurve.m_Bezier, 1f);
                float3 endTan = lastSpanEdge.IsReversed
                    ? -math.normalizesafe(MathUtils.Tangent(lastCurve.m_Bezier, 0f), new float3(0, 0, 1))
                    : math.normalizesafe(MathUtils.Tangent(lastCurve.m_Bezier, 1f), new float3(0, 0, 1));

                bool isSideStreet = (corrLen < 160.0f);

                var blockSpan = new BlockSpan
                {
                    SpanId = spanIdCounter++,
                    Edges = spanEdges,
                    StartNode = currentStartNode,
                    EndNode = currentEndNode,
                    TotalLength = totalLength,
                    MidPosition = midPos,
                    MidTangent = midTan,
                    MidEdge = midEdge,
                    MidT = midT,
                    HubEntity = nearestHub,
                    CorridorIndex = corrIdx,
                    CorridorLength = corrLen,
                    IsSuppressed = false,
                    IsSideStreet = isSideStreet,
                    StartPosition = startPos,
                    EndPosition = endPos,
                    StartTangent = startTan,
                    EndTangent = endTan,
                    SpanOrderInCorridor = 0,
                    CorridorForwardTangent = midTan
                };

                blockSpans.Add(blockSpan);
            }

            // Post-process corridors to assign sequential SpanOrderInCorridor and CorridorForwardTangent
            var edgeToSpan = new Dictionary<Entity, BlockSpan>();
            for (int s = 0; s < blockSpans.Count; s++)
            {
                var span = blockSpans[s];
                for (int e = 0; e < span.Edges.Count; e++)
                {
                    edgeToSpan[span.Edges[e].EdgeEntity] = span;
                }
            }

            for (int c = 0; c < corridors.Count; c++)
            {
                var corr = corridors[c];
                var visitedSpansInCorr = new HashSet<int>();
                int order = 0;

                for (int s = 0; s < corr.Segments.Count; s++)
                {
                    var seg = corr.Segments[s];
                    if (edgeToSpan.TryGetValue(seg.RoadEntity, out BlockSpan span))
                    {
                        if (visitedSpansInCorr.Contains(span.SpanId))
                            continue;

                        visitedSpansInCorr.Add(span.SpanId);
                        span.SpanOrderInCorridor = order++;

                        // Compute tangent aligned with corridor forward direction
                        if (EntityManager.HasComponent<Curve>(seg.RoadEntity))
                        {
                            var curve = EntityManager.GetComponentData<Curve>(seg.RoadEntity);
                            float3 segTan = math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 0.5f), new float3(0, 0, 1));
                            if (seg.IsReversed)
                                segTan = -segTan;

                            span.CorridorForwardTangent = segTan;
                        }
                    }
                }
            }

            for (int s = 0; s < blockSpans.Count; s++)
            {
                if (math.lengthsq(blockSpans[s].CorridorForwardTangent) < 0.001f)
                {
                    blockSpans[s].CorridorForwardTangent = blockSpans[s].MidTangent;
                    blockSpans[s].SpanOrderInCorridor = 0;
                }
            }

            log.Info($"Block Spans Built: {blockSpans.Count} spans from {_roadScanner.RoadSegments.Length} edges ({mergedDegree2Count} degree-2 pass-through splits merged)");
            return blockSpans;
        }

        private List<CityBlockFace> BuildCityBlockFaces(List<BlockSpan> blockSpans)
        {
            var faces = new List<CityBlockFace>();

            // Group outgoing half-edges by node
            var nodeToOutgoing = new Dictionary<Entity, List<NodeOutgoingHalfEdge>>();

            for (int i = 0; i < blockSpans.Count; i++)
            {
                var span = blockSpans[i];
                if (span.StartNode != Entity.Null)
                {
                    if (!nodeToOutgoing.ContainsKey(span.StartNode))
                        nodeToOutgoing[span.StartNode] = new List<NodeOutgoingHalfEdge>();

                    float angle = math.atan2(span.StartTangent.z, span.StartTangent.x);
                    nodeToOutgoing[span.StartNode].Add(new NodeOutgoingHalfEdge
                    {
                        HalfEdge = new DirectedSpanHalfEdge { SpanIndex = i, IsForward = true },
                        Angle = angle
                    });
                }

                if (span.EndNode != Entity.Null)
                {
                    if (!nodeToOutgoing.ContainsKey(span.EndNode))
                        nodeToOutgoing[span.EndNode] = new List<NodeOutgoingHalfEdge>();

                    float3 outTan = -span.EndTangent;
                    float angle = math.atan2(outTan.z, outTan.x);
                    nodeToOutgoing[span.EndNode].Add(new NodeOutgoingHalfEdge
                    {
                        HalfEdge = new DirectedSpanHalfEdge { SpanIndex = i, IsForward = false },
                        Angle = angle
                    });
                }
            }

            // Sort outgoing half-edges around each node in counter-clockwise order by angle
            foreach (var kvp in nodeToOutgoing)
            {
                kvp.Value.Sort((a, b) => a.Angle.CompareTo(b.Angle));
            }

            // Track visited directed half-edges
            var visitedHalfEdges = new HashSet<long>();

            long GetHalfEdgeKey(int spanIndex, bool isForward)
            {
                return ((long)spanIndex << 1) | (isForward ? 1L : 0L);
            }

            int faceIdCounter = 0;

            for (int i = 0; i < blockSpans.Count; i++)
            {
                for (int dir = 0; dir < 2; dir++)
                {
                    bool isForward = (dir == 0);
                    long startKey = GetHalfEdgeKey(i, isForward);
                    if (visitedHalfEdges.Contains(startKey))
                        continue;

                    var cycleHalfEdges = new List<DirectedSpanHalfEdge>();
                    var polygonPoints = new List<float2>();
                    var stepVisited = new HashSet<long>();

                    var currHalfEdge = new DirectedSpanHalfEdge { SpanIndex = i, IsForward = isForward };
                    bool cycleFound = false;

                    while (true)
                    {
                        long currKey = GetHalfEdgeKey(currHalfEdge.SpanIndex, currHalfEdge.IsForward);
                        if (stepVisited.Contains(currKey) || visitedHalfEdges.Contains(currKey))
                        {
                            if (currKey == startKey)
                                cycleFound = true;
                            break;
                        }

                        stepVisited.Add(currKey);
                        cycleHalfEdges.Add(currHalfEdge);

                        var span = blockSpans[currHalfEdge.SpanIndex];
                        Entity arrivalNode = currHalfEdge.IsForward ? span.EndNode : span.StartNode;

                        // Add sample points along span for exact polygon area and perimeter
                        if (currHalfEdge.IsForward)
                        {
                            polygonPoints.Add(new float2(span.StartPosition.x, span.StartPosition.z));
                            polygonPoints.Add(new float2(span.MidPosition.x, span.MidPosition.z));
                        }
                        else
                        {
                            polygonPoints.Add(new float2(span.EndPosition.x, span.EndPosition.z));
                            polygonPoints.Add(new float2(span.MidPosition.x, span.MidPosition.z));
                        }

                        if (arrivalNode == Entity.Null || !nodeToOutgoing.ContainsKey(arrivalNode))
                            break;

                        var outgoingList = nodeToOutgoing[arrivalNode];
                        if (outgoingList.Count == 0)
                            break;

                        // Find index of the reverse half-edge leaving arrivalNode
                        DirectedSpanHalfEdge reverseHalfEdge = new DirectedSpanHalfEdge
                        {
                            SpanIndex = currHalfEdge.SpanIndex,
                            IsForward = !currHalfEdge.IsForward
                        };

                        int revIdx = -1;
                        for (int o = 0; o < outgoingList.Count; o++)
                        {
                            if (outgoingList[o].HalfEdge.SpanIndex == reverseHalfEdge.SpanIndex &&
                                outgoingList[o].HalfEdge.IsForward == reverseHalfEdge.IsForward)
                            {
                                revIdx = o;
                                break;
                            }
                        }

                        if (revIdx < 0)
                            break;

                        // Cyclic next in counter-clockwise order
                        int nextIdx = (revIdx + 1) % outgoingList.Count;
                        currHalfEdge = outgoingList[nextIdx].HalfEdge;
                    }

                    if (cycleFound)
                    {
                        visitedHalfEdges.UnionWith(stepVisited);

                        if (cycleHalfEdges.Count >= 3 && polygonPoints.Count >= 3)
                        {
                            // Calculate signed 2D polygon area
                            float signedArea = 0f;
                            float perimeter = 0f;
                            float2 centroidSum = float2.zero;

                            int numPts = polygonPoints.Count;
                            for (int p = 0; p < numPts; p++)
                            {
                                var p1 = polygonPoints[p];
                                var p2 = polygonPoints[(p + 1) % numPts];

                                float cross = (p1.x * p2.y) - (p2.x * p1.y);
                                signedArea += cross;
                                perimeter += math.distance(p1, p2);
                                centroidSum += (p1 + p2) * cross;
                            }

                            signedArea *= 0.5f;
                            float absArea = math.abs(signedArea);

                            // Absolute area within valid block dimensions
                            if (absArea >= MIN_BLOCK_AREA && absArea <= MAX_BLOCK_AREA && perimeter > 0f)
                            {
                                float thicknessRatio = absArea / perimeter;
                                if (thicknessRatio >= MIN_BLOCK_THICKNESS_RATIO)
                                {
                                    float2 centroid = float2.zero;
                                    if (absArea > 0.001f && math.abs(signedArea) > 0.001f)
                                    {
                                        centroid = centroidSum / (6.0f * signedArea);
                                    }

                                    var face = new CityBlockFace
                                    {
                                        FaceId = faceIdCounter++,
                                        Area = absArea,
                                        Perimeter = perimeter,
                                        Centroid = centroid,
                                        IsServed = false
                                    };

                                    for (int c = 0; c < cycleHalfEdges.Count; c++)
                                    {
                                        int sIdx = cycleHalfEdges[c].SpanIndex;
                                        if (!face.SpanIndices.Contains(sIdx))
                                            face.SpanIndices.Add(sIdx);
                                    }

                                    faces.Add(face);
                                }
                            }
                        }
                    }
                }
            }

            log.Info($"City Block Faces Enumerated: {faces.Count} enclosed building blocks discovered across the network");
            return faces;
        }

        public void UpdateDiscoveredPrefabs()
        {
            FindBusStopPrefabs(out _, out _);
        }

        private string GetPrefabName(Entity e)
        {
            if (_prefabSystem != null)
            {
                if (_prefabSystem.TryGetPrefab<PrefabBase>(e, out var pBase) && pBase != null)
                    return pBase.name ?? "";
                return _prefabSystem.GetPrefabName(e) ?? "";
            }
            return "";
        }

        private bool FindBusStopPrefabs(out List<Entity> candidatePrefabs, out Entity defaultPrefab)
        {
            candidatePrefabs = new List<Entity>();
            defaultPrefab = Entity.Null;

            if (_busStopPrefabQuery.IsEmptyIgnoreFilter)
                return false;

            var entities = _busStopPrefabQuery.ToEntityArray(Allocator.Temp);
            var stopDataArray = _busStopPrefabQuery.ToComponentDataArray<TransportStopData>(Allocator.Temp);

            var allValidPrefabs = new List<Entity>();
            var nameToEntity = new Dictionary<string, Entity>();
            var discovered = new List<string>();

            for (int i = 0; i < entities.Length; i++)
            {
                var e = entities[i];
                if (EntityManager.HasComponent<Game.Prefabs.OutsideConnectionData>(e))
                    continue;

                var stopData = stopDataArray[i];

                if (stopData.m_TransportType == TransportType.Bus && stopData.m_PassengerTransport)
                {
                    string pName = GetPrefabName(e);
                    if (string.IsNullOrEmpty(pName))
                        continue;

                    if (pName.IndexOf("Outside Connection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        pName.IndexOf("OutsideConnection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        pName.IndexOf("Placeholder", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }

                    allValidPrefabs.Add(e);

                    if (!discovered.Contains(pName))
                        discovered.Add(pName);

                    if (!nameToEntity.ContainsKey(pName))
                    {
                        nameToEntity[pName] = e;
                    }
                }
            }

            entities.Dispose();
            stopDataArray.Dispose();

            if (discovered.Count > 0)
            {
                bool changed = false;
                if (Setting.DiscoveredStopPrefabNames.Count != discovered.Count)
                {
                    changed = true;
                }
                else
                {
                    for (int i = 0; i < discovered.Count; i++)
                    {
                        if (Setting.DiscoveredStopPrefabNames[i] != discovered[i])
                        {
                            changed = true;
                            break;
                        }
                    }
                }

                if (changed)
                {
                    Setting.DiscoveredStopPrefabNames.Clear();
                    Setting.DiscoveredStopPrefabNames.AddRange(discovered);
                    Setting.DiscoveredStopPrefabVersion++;
                    log.Info($"Discovered {discovered.Count} exact Bus Stop Prefab models in the game: {string.Join(", ", discovered)}");
                }
            }

            if (allValidPrefabs.Count == 0)
                return false;

            defaultPrefab = allValidPrefabs[0];

            string selectedModel = Mod.setting != null ? Mod.setting.SelectedStopPrefab : "All";
            if (string.IsNullOrEmpty(selectedModel) || selectedModel == "All")
            {
                candidatePrefabs.AddRange(allValidPrefabs);
            }
            else if (nameToEntity.TryGetValue(selectedModel, out Entity match))
            {
                candidatePrefabs.Add(match);
            }
            else
            {
                candidatePrefabs.AddRange(allValidPrefabs);
            }

            log.Info($"Active Bus Stop Prefab Pool: {candidatePrefabs.Count} prefabs available for placement (Selected Model: '{selectedModel}')");
            return true;
        }

        private bool FindBusLinePrefab(out Entity prefabEntity, out RouteData routeData)
        {
            prefabEntity = Entity.Null;
            routeData = default;

            var entities = _busLinePrefabQuery.ToEntityArray(Allocator.Temp);
            var lineDataArray = _busLinePrefabQuery.ToComponentDataArray<TransportLineData>(Allocator.Temp);
            var routeDataArray = _busLinePrefabQuery.ToComponentDataArray<RouteData>(Allocator.Temp);

            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if (lineDataArray[i].m_TransportType == TransportType.Bus && lineDataArray[i].m_PassengerTransport)
                    {
                        prefabEntity = entities[i];
                        routeData = routeDataArray[i];
                        return true;
                    }
                }
                return false;
            }
            finally
            {
                entities.Dispose();
                lineDataArray.Dispose();
                routeDataArray.Dispose();
            }
        }

        private List<PlacedStop> SubsampleTour(List<PlacedStop> tour, int maxStops)
        {
            if (tour.Count <= maxStops)
                return tour;

            var sampled = new List<PlacedStop>(maxStops);
            float step = (float)(tour.Count - 1) / (maxStops - 1);
            for (int k = 0; k < maxStops; k++)
            {
                int idx = (int)math.round(k * step);
                idx = math.clamp(idx, 0, tour.Count - 1);
                if (sampled.Count == 0 || sampled[sampled.Count - 1].StopEntity != tour[idx].StopEntity)
                    sampled.Add(tour[idx]);
            }

            if (sampled.Count > 1 && sampled[sampled.Count - 1].StopEntity == sampled[0].StopEntity)
                sampled.RemoveAt(sampled.Count - 1);

            return sampled;
        }

        private List<List<PlacedStop>> ClusterStopsKMeans(List<PlacedStop> stops, int k, int minStops, int maxStops)
        {
            if (k <= 1 || stops.Count <= minStops)
            {
                return new List<List<PlacedStop>> { new List<PlacedStop>(stops) };
            }

            // 1. Initialize k centroids using K-means++
            var centroids = new List<float2>(k);
            centroids.Add(new float2(stops[0].Position.x, stops[0].Position.z));

            for (int c = 1; c < k; c++)
            {
                float maxDistSq = -1f;
                int bestIdx = 0;
                for (int i = 0; i < stops.Count; i++)
                {
                    float2 pt = new float2(stops[i].Position.x, stops[i].Position.z);
                    float nearestCentroidDistSq = float.MaxValue;
                    for (int j = 0; j < centroids.Count; j++)
                    {
                        float dSq = math.distancesq(pt, centroids[j]);
                        if (dSq < nearestCentroidDistSq)
                            nearestCentroidDistSq = dSq;
                    }
                    if (nearestCentroidDistSq > maxDistSq)
                    {
                        maxDistSq = nearestCentroidDistSq;
                        bestIdx = i;
                    }
                }
                centroids.Add(new float2(stops[bestIdx].Position.x, stops[bestIdx].Position.z));
            }

            // 2. Run K-Means iterations
            var assignments = new int[stops.Count];
            for (int iter = 0; iter < 15; iter++)
            {
                bool changed = false;
                for (int i = 0; i < stops.Count; i++)
                {
                    float2 pt = new float2(stops[i].Position.x, stops[i].Position.z);
                    int bestCluster = 0;
                    float bestDistSq = float.MaxValue;
                    for (int c = 0; c < k; c++)
                    {
                        float dSq = math.distancesq(pt, centroids[c]);
                        if (dSq < bestDistSq)
                        {
                            bestDistSq = dSq;
                            bestCluster = c;
                        }
                    }
                    if (assignments[i] != bestCluster)
                    {
                        assignments[i] = bestCluster;
                        changed = true;
                    }
                }

                if (!changed && iter > 0)
                    break;

                var counts = new int[k];
                var sums = new float2[k];
                for (int i = 0; i < stops.Count; i++)
                {
                    int c = assignments[i];
                    counts[c]++;
                    sums[c] += new float2(stops[i].Position.x, stops[i].Position.z);
                }
                for (int c = 0; c < k; c++)
                {
                    if (counts[c] > 0)
                        centroids[c] = sums[c] / counts[c];
                }
            }

            // 3. Assemble clusters
            var clusters = new List<List<PlacedStop>>(k);
            for (int c = 0; c < k; c++)
                clusters.Add(new List<PlacedStop>());

            for (int i = 0; i < stops.Count; i++)
            {
                clusters[assignments[i]].Add(stops[i]);
            }

            // 4. Merge small clusters (count < minStops) into nearest remaining cluster
            for (int c = clusters.Count - 1; c >= 0; c--)
            {
                if (clusters[c].Count < minStops && clusters.Count > 1)
                {
                    var orphaned = clusters[c];
                    clusters.RemoveAt(c);
                    centroids.RemoveAt(c);

                    for (int o = 0; o < orphaned.Count; o++)
                    {
                        float2 pt = new float2(orphaned[o].Position.x, orphaned[o].Position.z);
                        int nearestC = 0;
                        float nearestDistSq = float.MaxValue;
                        for (int j = 0; j < clusters.Count; j++)
                        {
                            float dSq = math.distancesq(pt, centroids[j]);
                            if (dSq < nearestDistSq)
                            {
                                nearestDistSq = dSq;
                                nearestC = j;
                            }
                        }
                        clusters[nearestC].Add(orphaned[o]);
                    }
                }
            }

            return clusters;
        }

        private struct DirectedRoadEdge
        {
            public Entity EdgeEntity;
            public Entity FromNode;
            public Entity ToNode;
            public float Length;
            public float3 TangentAtFrom;
            public float3 TangentAtTo;
        }

        private class SimpleMinHeap
        {
            private struct HeapItem
            {
                public Entity Node;
                public float Priority;
            }

            private List<HeapItem> _items = new List<HeapItem>();

            public int Count => _items.Count;

            public void Enqueue(Entity node, float priority)
            {
                _items.Add(new HeapItem { Node = node, Priority = priority });
                int c = _items.Count - 1;
                while (c > 0)
                {
                    int p = (c - 1) / 2;
                    if (_items[c].Priority >= _items[p].Priority)
                        break;
                    var tmp = _items[c];
                    _items[c] = _items[p];
                    _items[p] = tmp;
                    c = p;
                }
            }

            public Entity Dequeue()
            {
                if (_items.Count == 0) return Entity.Null;
                Entity result = _items[0].Node;
                int last = _items.Count - 1;
                _items[0] = _items[last];
                _items.RemoveAt(last);
                int p = 0;
                while (true)
                {
                    int left = 2 * p + 1;
                    int right = 2 * p + 2;
                    int smallest = p;

                    if (left < _items.Count && _items[left].Priority < _items[smallest].Priority)
                        smallest = left;
                    if (right < _items.Count && _items[right].Priority < _items[smallest].Priority)
                        smallest = right;

                    if (smallest == p)
                        break;

                    var tmp = _items[p];
                    _items[p] = _items[smallest];
                    _items[smallest] = tmp;
                    p = smallest;
                }
                return result;
            }
        }

        private void BuildDirectedRoadGraph(
            out Dictionary<Entity, List<DirectedRoadEdge>> nodeOutEdges,
            out Dictionary<Entity, float3> nodePositions)
        {
            nodeOutEdges = new Dictionary<Entity, List<DirectedRoadEdge>>();
            nodePositions = new Dictionary<Entity, float3>();
            _nodeDegree = new Dictionary<Entity, int>();

            for (int i = 0; i < _roadScanner.RoadSegments.Length; i++)
            {
                var roadEnt = _roadScanner.RoadSegments[i];
                if (!EntityManager.HasComponent<Edge>(roadEnt) || !EntityManager.HasComponent<Curve>(roadEnt))
                    continue;

                var edge = EntityManager.GetComponentData<Edge>(roadEnt);
                var curve = EntityManager.GetComponentData<Curve>(roadEnt);

                if (edge.m_Start == Entity.Null || edge.m_End == Entity.Null)
                    continue;

                float len = MathUtils.Length(curve.m_Bezier);
                if (len < 0.1f)
                    continue;

                float3 startPos = MathUtils.Position(curve.m_Bezier, 0f);
                float3 endPos = MathUtils.Position(curve.m_Bezier, 1f);

                float3 startTan = math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 0f), new float3(0, 0, 1));
                float3 endTan = math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 1f), new float3(0, 0, 1));

                _nodeDegree[edge.m_Start] = (_nodeDegree.TryGetValue(edge.m_Start, out int degStart) ? degStart : 0) + 1;
                _nodeDegree[edge.m_End] = (_nodeDegree.TryGetValue(edge.m_End, out int degEnd) ? degEnd : 0) + 1;

                if (!nodePositions.ContainsKey(edge.m_Start))
                    nodePositions[edge.m_Start] = startPos;
                if (!nodePositions.ContainsKey(edge.m_End))
                    nodePositions[edge.m_End] = endPos;

                bool isOneWay = IsOneWayRoad(roadEnt, out bool forwardFlow);

                if (!isOneWay || forwardFlow)
                {
                    if (!nodeOutEdges.ContainsKey(edge.m_Start))
                        nodeOutEdges[edge.m_Start] = new List<DirectedRoadEdge>();

                    nodeOutEdges[edge.m_Start].Add(new DirectedRoadEdge
                    {
                        EdgeEntity = roadEnt,
                        FromNode = edge.m_Start,
                        ToNode = edge.m_End,
                        Length = len,
                        TangentAtFrom = startTan,
                        TangentAtTo = endTan
                    });
                }

                if (!isOneWay || !forwardFlow)
                {
                    if (!nodeOutEdges.ContainsKey(edge.m_End))
                        nodeOutEdges[edge.m_End] = new List<DirectedRoadEdge>();

                    nodeOutEdges[edge.m_End].Add(new DirectedRoadEdge
                    {
                        EdgeEntity = roadEnt,
                        FromNode = edge.m_End,
                        ToNode = edge.m_Start,
                        Length = len,
                        TangentAtFrom = -endTan,
                        TangentAtTo = -startTan
                    });
                }
            }
        }

        private void IndexStopsByDirectedEdge(
            List<PlacedStop> allGlobalStops,
            out Dictionary<Entity, List<PlacedStop>> edgeForwardStops,
            out Dictionary<Entity, List<PlacedStop>> edgeBackwardStops)
        {
            edgeForwardStops = new Dictionary<Entity, List<PlacedStop>>();
            edgeBackwardStops = new Dictionary<Entity, List<PlacedStop>>();

            for (int i = 0; i < allGlobalStops.Count; i++)
            {
                var stop = allGlobalStops[i];
                var roadEnt = stop.RoadEntity;
                if (roadEnt == Entity.Null || !EntityManager.HasComponent<Curve>(roadEnt))
                    continue;

                var curve = EntityManager.GetComponentData<Curve>(roadEnt);
                float3 roadTan = math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 0.5f), new float3(0, 0, 1));

                float3 startPos = MathUtils.Position(curve.m_Bezier, 0f);
                stop.DistFromHub = math.distance(stop.Position, startPos);

                // Direction comes from the stop's side of the road / the road's flow, not its rotation
                if (StopServesEdgeForward(stop))
                {
                    if (!edgeForwardStops.ContainsKey(roadEnt))
                        edgeForwardStops[roadEnt] = new List<PlacedStop>();
                    edgeForwardStops[roadEnt].Add(stop);
                }
                else
                {
                    if (!edgeBackwardStops.ContainsKey(roadEnt))
                        edgeBackwardStops[roadEnt] = new List<PlacedStop>();
                    edgeBackwardStops[roadEnt].Add(stop);
                }
            }

            foreach (var kvp in edgeForwardStops)
            {
                kvp.Value.Sort((a, b) => a.DistFromHub.CompareTo(b.DistFromHub));
            }

            foreach (var kvp in edgeBackwardStops)
            {
                kvp.Value.Sort((a, b) => b.DistFromHub.CompareTo(a.DistFromHub));
            }
        }

        private List<PlacedStop> GetStopsOnDirectedEdge(
            DirectedRoadEdge dEdge,
            Dictionary<Entity, List<PlacedStop>> edgeForwardStops,
            Dictionary<Entity, List<PlacedStop>> edgeBackwardStops)
        {
            if (!EntityManager.HasComponent<Edge>(dEdge.EdgeEntity))
                return null;

            var edge = EntityManager.GetComponentData<Edge>(dEdge.EdgeEntity);
            bool isForward = (dEdge.FromNode == edge.m_Start);

            if (isForward)
            {
                if (edgeForwardStops.TryGetValue(dEdge.EdgeEntity, out var stops))
                    return stops;
            }
            else
            {
                if (edgeBackwardStops.TryGetValue(dEdge.EdgeEntity, out var stops))
                    return stops;
            }

            return null;
        }

        private List<DirectedRoadEdge> GetCorridorDirectedEdges(RoadCorridor corr, bool forward)
        {
            var list = new List<DirectedRoadEdge>();
            if (corr.Segments.Count == 0) return list;

            if (forward)
            {
                for (int i = 0; i < corr.Segments.Count; i++)
                {
                    var seg = corr.Segments[i];
                    if (!EntityManager.HasComponent<Edge>(seg.RoadEntity) || !EntityManager.HasComponent<Curve>(seg.RoadEntity))
                        continue;

                    var edge = EntityManager.GetComponentData<Edge>(seg.RoadEntity);
                    var curve = EntityManager.GetComponentData<Curve>(seg.RoadEntity);
                    float len = MathUtils.Length(curve.m_Bezier);

                    Entity fromNode = seg.IsReversed ? edge.m_End : edge.m_Start;
                    Entity toNode = seg.IsReversed ? edge.m_Start : edge.m_End;
                    float3 tanFrom = seg.IsReversed
                        ? -math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 1f), new float3(0, 0, 1))
                        : math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 0f), new float3(0, 0, 1));
                    float3 tanTo = seg.IsReversed
                        ? -math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 0f), new float3(0, 0, 1))
                        : math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 1f), new float3(0, 0, 1));

                    list.Add(new DirectedRoadEdge
                    {
                        EdgeEntity = seg.RoadEntity,
                        FromNode = fromNode,
                        ToNode = toNode,
                        Length = len,
                        TangentAtFrom = tanFrom,
                        TangentAtTo = tanTo
                    });
                }
            }
            else
            {
                for (int i = corr.Segments.Count - 1; i >= 0; i--)
                {
                    var seg = corr.Segments[i];
                    if (!EntityManager.HasComponent<Edge>(seg.RoadEntity) || !EntityManager.HasComponent<Curve>(seg.RoadEntity))
                        continue;

                    var edge = EntityManager.GetComponentData<Edge>(seg.RoadEntity);
                    var curve = EntityManager.GetComponentData<Curve>(seg.RoadEntity);
                    float len = MathUtils.Length(curve.m_Bezier);

                    Entity fromNode = seg.IsReversed ? edge.m_Start : edge.m_End;
                    Entity toNode = seg.IsReversed ? edge.m_End : edge.m_Start;
                    float3 tanFrom = seg.IsReversed
                        ? math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 0f), new float3(0, 0, 1))
                        : -math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 1f), new float3(0, 0, 1));
                    float3 tanTo = seg.IsReversed
                        ? math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 1f), new float3(0, 0, 1))
                        : -math.normalizesafe(MathUtils.Tangent(curve.m_Bezier, 0f), new float3(0, 0, 1));

                    list.Add(new DirectedRoadEdge
                    {
                        EdgeEntity = seg.RoadEntity,
                        FromNode = fromNode,
                        ToNode = toNode,
                        Length = len,
                        TangentAtFrom = tanFrom,
                        TangentAtTo = tanTo
                    });
                }
            }

            return list;
        }

        private List<PlacedStop> BuildTourFromRoadCycle(
            List<DirectedRoadEdge> cycleEdges,
            Dictionary<Entity, List<PlacedStop>> edgeForwardStops,
            Dictionary<Entity, List<PlacedStop>> edgeBackwardStops,
            int maxStops)
        {
            var tour = new List<PlacedStop>();
            var inTour = new HashSet<Entity>();   // a stop may appear on a line only once

            for (int i = 0; i < cycleEdges.Count; i++)
            {
                var dEdge = cycleEdges[i];
                var stops = GetStopsOnDirectedEdge(dEdge, edgeForwardStops, edgeBackwardStops);
                if (stops != null && stops.Count > 0)
                {
                    for (int s = 0; s < stops.Count; s++)
                    {
                        var stop = stops[s];
                        if (inTour.Add(stop.StopEntity))
                        {
                            tour.Add(stop);
                        }
                    }
                }
            }

            if (tour.Count > 1 && tour[tour.Count - 1].StopEntity == tour[0].StopEntity)
                tour.RemoveAt(tour.Count - 1);

            if (tour.Count > maxStops)
                tour = SubsampleTour(tour, maxStops);

            return tour;
        }

        private float CalculateTourLength(List<PlacedStop> tour)
        {
            if (tour.Count < 2)
                return 0f;

            float len = 0f;
            for (int i = 0; i < tour.Count; i++)
            {
                len += math.distance(tour[i].Position, tour[(i + 1) % tour.Count].Position);
            }
            return len * 1.25f; // Grid and curve driving factor
        }

        private void CreateBusLine(Entity busLinePrefabEntity, RouteData routeData, List<PlacedStop> orderedStops, int lineNumber, int hubIndex, int lineIndexInHub)
        {
            // 1. Create Route entity using native route archetype
            Entity routeEntity = EntityManager.CreateEntity(routeData.m_RouteArchetype);
            EntityManager.SetComponentData(routeEntity, new PrefabRef(busLinePrefabEntity));
            EntityManager.SetComponentData(routeEntity, new Route { m_Flags = RouteFlags.Complete, m_OptionMask = 0 });

            if (EntityManager.HasComponent<TransportLineData>(busLinePrefabEntity))
            {
                var lineData = EntityManager.GetComponentData<TransportLineData>(busLinePrefabEntity);
                EntityManager.SetComponentData(routeEntity, new TransportLine(lineData));
            }

            EntityManager.SetComponentData(routeEntity, new RouteNumber { m_Number = lineNumber });

            // Distinct, vibrant golden-ratio color scheme across the rainbow for each bus line
            float lineHue = ((lineNumber - 1) * 0.618033988749895f) % 1.0f;
            float saturation = 0.88f;
            float value = 0.95f;
            Color32 lineColor = HSVToRGB(lineHue, saturation, value);

            EntityManager.SetComponentData(routeEntity, new Game.Routes.Color(lineColor));
            EntityManager.SetComponentData(routeEntity, new RouteBufferIndex { m_Index = -1 });

            // NOTE ON BUFFER HANDLES: every EntityManager.CreateEntity / AddBuffer call below is a
            // structural change, and a structural change invalidates *every* outstanding
            // DynamicBuffer handle in the world - not just ones on the entity being changed.
            // So the route's own RouteWaypoint/RouteSegment buffers are deliberately NOT fetched
            // up front; they are filled in a final pass once all entity creation is done.
            var waypointEntities = new List<Entity>(orderedStops.Count);

            // 2. Create connected Waypoints referencing placed stops
            for (int i = 0; i < orderedStops.Count; i++)
            {
                var stop = orderedStops[i];
                Entity wpEntity = EntityManager.CreateEntity(routeData.m_ConnectedArchetype);

                EntityManager.SetComponentData(wpEntity, new PrefabRef(busLinePrefabEntity));
                EntityManager.SetComponentData(wpEntity, new Game.Routes.Waypoint(i));
                EntityManager.SetComponentData(wpEntity, new Game.Routes.Position(stop.Position));
                EntityManager.SetComponentData(wpEntity, new Game.Common.Owner(routeEntity));
                EntityManager.SetComponentData(wpEntity, new Game.Routes.Connected(stop.StopEntity));

                if (!EntityManager.HasComponent<Created>(wpEntity))
                    EntityManager.AddComponentData(wpEntity, default(Created));
                if (!EntityManager.HasComponent<Updated>(wpEntity))
                    EntityManager.AddComponentData(wpEntity, default(Updated));

                waypointEntities.Add(wpEntity);

                if (!EntityManager.HasBuffer<ConnectedRoute>(stop.StopEntity))
                    EntityManager.AddBuffer<ConnectedRoute>(stop.StopEntity);

                var connBuf = EntityManager.GetBuffer<ConnectedRoute>(stop.StopEntity);
                bool alreadyIn = false;
                for (int c = 0; c < connBuf.Length; c++)
                {
                    if (connBuf[c].m_Waypoint == wpEntity)
                    {
                        alreadyIn = true;
                        break;
                    }
                }
                if (!alreadyIn)
                    connBuf.Add(new ConnectedRoute(wpEntity));

                if (!EntityManager.HasComponent<Updated>(stop.StopEntity))
                    EntityManager.AddComponentData(stop.StopEntity, default(Updated));
            }

            // 3. Create Route Segments connecting consecutive waypoints with CurveElement & PathElement buffers
            var segmentEntities = new List<Entity>(waypointEntities.Count);
            for (int i = 0; i < waypointEntities.Count; i++)
            {
                Entity segEntity = EntityManager.CreateEntity(routeData.m_SegmentArchetype);

                EntityManager.SetComponentData(segEntity, new PrefabRef(busLinePrefabEntity));
                EntityManager.SetComponentData(segEntity, new Game.Routes.Segment(i));
                EntityManager.SetComponentData(segEntity, new Game.Common.Owner(routeEntity));

                if (!EntityManager.HasBuffer<Game.Routes.CurveElement>(segEntity))
                    EntityManager.AddBuffer<Game.Routes.CurveElement>(segEntity);
                if (!EntityManager.HasBuffer<Game.Pathfind.PathElement>(segEntity))
                    EntityManager.AddBuffer<Game.Pathfind.PathElement>(segEntity);

                if (!EntityManager.HasComponent<Created>(segEntity))
                    EntityManager.AddComponentData(segEntity, default(Created));
                if (!EntityManager.HasComponent<Updated>(segEntity))
                    EntityManager.AddComponentData(segEntity, default(Updated));

                segmentEntities.Add(segEntity);
            }

            // 4. Fill the route's buffers last. No structural changes happen past this point, so
            //    these handles stay valid; DynamicBuffer.Add only grows the buffer's own storage.
            var waypointsBuffer = EntityManager.GetBuffer<RouteWaypoint>(routeEntity);
            for (int i = 0; i < waypointEntities.Count; i++)
                waypointsBuffer.Add(new RouteWaypoint(waypointEntities[i]));

            var segmentsBuffer = EntityManager.GetBuffer<RouteSegment>(routeEntity);
            for (int i = 0; i < segmentEntities.Count; i++)
                segmentsBuffer.Add(new RouteSegment(segmentEntities[i]));

            if (!EntityManager.HasComponent<Created>(routeEntity))
                EntityManager.AddComponentData(routeEntity, default(Created));
            if (!EntityManager.HasComponent<Updated>(routeEntity))
                EntityManager.AddComponentData(routeEntity, default(Updated));
            if (!EntityManager.HasComponent<BatchesUpdated>(routeEntity))
                EntityManager.AddComponentData(routeEntity, default(BatchesUpdated));
        }

        private static Color32 HSVToRGB(float h, float s, float v)
        {
            float r = 0, g = 0, b = 0;
            if (s == 0)
            {
                r = v; g = v; b = v;
            }
            else
            {
                float sectorPos = h * 6.0f;
                int sectorNumber = (int)math.floor(sectorPos);
                float fractionalSector = sectorPos - sectorNumber;
                float p = v * (1.0f - s);
                float q = v * (1.0f - (s * fractionalSector));
                float num = v * (1.0f - (s * (1.0f - fractionalSector)));

                switch (sectorNumber % 6)
                {
                    case 0: r = v; g = num; b = p; break;
                    case 1: r = q; g = v; b = p; break;
                    case 2: r = p; g = v; b = num; break;
                    case 3: r = p; g = q; b = v; break;
                    case 4: r = num; g = p; b = v; break;
                    case 5: r = v; g = p; b = q; break;
                }
            }

            return new Color32(
                (byte)math.clamp((int)(r * 255f), 0, 255),
                (byte)math.clamp((int)(g * 255f), 0, 255),
                (byte)math.clamp((int)(b * 255f), 0, 255),
                255
            );
        }
    }
}
