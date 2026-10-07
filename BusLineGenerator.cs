// AutoBusLines v2.0.3 - Modular transit planning and generation system
using System;
using System.Collections.Generic;
using System.Linq;
using Colossal.Logging;
using Colossal.Mathematics;
using Game;
using Game.Areas;
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

        public const int ABSOLUTE_MIN_STOPS = 3;
        private const int INDEXING_DELAY_FRAMES = 15;
        private const int ROUTES_PER_FRAME = 2;

        public int TargetDistrictId { get; set; } = 0;

        public Entity FindDistrictEntity(int districtId)
        {
            if (districtId <= 0) return Entity.Null;

            var districtQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<Game.Areas.District>(),
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                }
            });

            if (districtQuery.IsEmptyIgnoreFilter) return Entity.Null;

            using (var entities = districtQuery.ToEntityArray(Allocator.Temp))
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    if (entities[i].Index == districtId)
                    {
                        return entities[i];
                    }
                }
            }
            return Entity.Null;
        }

        /// <summary>
        /// Checks whether a 2D world position lies inside the boundary or area of a given district entity.
        /// Uses bounding box pre-filtering and exact triangulation buffers (Game.Areas.Node, Game.Areas.Triangle).
        /// </summary>
        public bool IsPointInDistrict(Entity districtEntity, float3 worldPos)
        {
            if (districtEntity == Entity.Null) return true;
            if (!EntityManager.Exists(districtEntity)) return false;
            if (!EntityManager.HasBuffer<Game.Areas.Node>(districtEntity) ||
                !EntityManager.HasBuffer<Game.Areas.Triangle>(districtEntity))
                return false;

            var nodes = EntityManager.GetBuffer<Game.Areas.Node>(districtEntity);
            var triangles = EntityManager.GetBuffer<Game.Areas.Triangle>(districtEntity);
            if (nodes.Length < 3 || triangles.Length == 0) return false;

            float2 p = worldPos.xz;

            // Fast Bounding Box Pre-Check
            float minX = float.MaxValue, maxX = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;
            for (int i = 0; i < nodes.Length; i++)
            {
                float2 np = nodes[i].m_Position.xz;
                if (np.x < minX) minX = np.x;
                if (np.x > maxX) maxX = np.x;
                if (np.y < minZ) minZ = np.y;
                if (np.y > maxZ) maxZ = np.y;
            }

            // Expand bounds slightly to account for road widths and curb offsets (approx 35m)
            const float kMargin = 35f;
            if (p.x < minX - kMargin || p.x > maxX + kMargin ||
                p.y < minZ - kMargin || p.y > maxZ + kMargin)
            {
                return false;
            }

            // Triangulated area check
            for (int i = 0; i < triangles.Length; i++)
            {
                var tri = triangles[i];
                if (tri.m_Indices.x < nodes.Length && tri.m_Indices.y < nodes.Length && tri.m_Indices.z < nodes.Length)
                {
                    var tri2 = AreaUtils.GetTriangle2(nodes, tri);
                    if (MathUtils.Intersect(tri2, p))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Checks whether a road edge belongs to or touches the specified district entity.
        /// Checks BorderDistrict (vanilla road district component), CurrentDistrict, endpoints, and geometry.
        /// </summary>
        public bool IsRoadInDistrict(Entity roadEntity, Entity districtEntity)
        {
            if (districtEntity == Entity.Null) return true;
            if (roadEntity == Entity.Null || !EntityManager.Exists(roadEntity)) return false;

            // 1. Check BorderDistrict on road edge (standard CS2 road component)
            if (EntityManager.HasComponent<BorderDistrict>(roadEntity))
            {
                var bd = EntityManager.GetComponentData<BorderDistrict>(roadEntity);
                if (bd.m_Left == districtEntity || bd.m_Right == districtEntity)
                    return true;
            }

            // 2. Check CurrentDistrict on road edge
            if (EntityManager.HasComponent<CurrentDistrict>(roadEntity))
            {
                var cd = EntityManager.GetComponentData<CurrentDistrict>(roadEntity);
                if (cd.m_District == districtEntity)
                    return true;
            }

            // 3. Check start and end nodes of the road edge
            if (EntityManager.HasComponent<Edge>(roadEntity))
            {
                var edge = EntityManager.GetComponentData<Edge>(roadEntity);
                if (edge.m_Start != Entity.Null && EntityManager.Exists(edge.m_Start))
                {
                    if (EntityManager.HasComponent<CurrentDistrict>(edge.m_Start) &&
                        EntityManager.GetComponentData<CurrentDistrict>(edge.m_Start).m_District == districtEntity)
                        return true;
                    if (EntityManager.HasComponent<BorderDistrict>(edge.m_Start))
                    {
                        var nbd = EntityManager.GetComponentData<BorderDistrict>(edge.m_Start);
                        if (nbd.m_Left == districtEntity || nbd.m_Right == districtEntity)
                            return true;
                    }
                }
                if (edge.m_End != Entity.Null && EntityManager.Exists(edge.m_End))
                {
                    if (EntityManager.HasComponent<CurrentDistrict>(edge.m_End) &&
                        EntityManager.GetComponentData<CurrentDistrict>(edge.m_End).m_District == districtEntity)
                        return true;
                    if (EntityManager.HasComponent<BorderDistrict>(edge.m_End))
                    {
                        var nbd = EntityManager.GetComponentData<BorderDistrict>(edge.m_End);
                        if (nbd.m_Left == districtEntity || nbd.m_Right == districtEntity)
                            return true;
                    }
                }
            }

            // 4. Fallback: Geometric containment via Curve midpoint
            if (EntityManager.HasComponent<Curve>(roadEntity))
            {
                var curve = EntityManager.GetComponentData<Curve>(roadEntity);
                float3 midPos = MathUtils.Position(curve.m_Bezier, 0.5f);
                if (IsPointInDistrict(districtEntity, midPos))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Checks whether a block span belongs to or touches the specified district entity.
        /// </summary>
        private bool IsSpanInDistrict(BlockSpan span, Entity districtEntity)
        {
            if (districtEntity == Entity.Null) return true;
            if (span == null) return false;

            for (int e = 0; e < span.Edges.Count; e++)
            {
                if (IsRoadInDistrict(span.Edges[e].EdgeEntity, districtEntity))
                    return true;
            }

            if (IsPointInDistrict(districtEntity, span.MidPosition))
                return true;

            return false;
        }

        private enum GenerationStage
        {
            Idle,
            WaitingForCleanup,
            WaitingForStopIndexing,
            InstantiatingRoutes
        }

        private GenerationStage _generationStage = GenerationStage.Idle;
        private int _waitFrameCounter = 0;
        private int _currentTourIndex = 0;
        private int _createdLineCount = 0;
        private List<List<PlacedStop>> _plannedTours = new List<List<PlacedStop>>();
        private List<Color32> _plannedTourColors = new List<Color32>();
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
        private Game.UI.NameSystem _nameSystem;

        private EntityQuery _busStopPrefabQuery;
        private EntityQuery _busLinePrefabQuery;
        private EntityQuery _existingBusStopQuery;

        private const float HUB_EXCLUSION_RADIUS = 120f;            // Exclusion radius around stations & depots (passengers use station bays)
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
            public int SpanId;
        }

        public struct PlacedStop
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
            public float RoadT;
            public Entity PrefabEntity;
            public bool IsStationBay;
            public bool IsPreExisting;
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
            public int StartDegree;
            public int EndDegree;
            public bool IsDeadEnd;
            public float DeadEndBranchLength = 0f;
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
            _nameSystem = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();

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

        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            ResetOnSaveLoad();
            CleanLegacySubObjectStops();
        }

        public void ResetOnSaveLoad()
        {
            _hasRun = false;
            _isManualRequest = false;
            _isPlanRequest = false;
            _isBuildPlanRequest = false;
            _isDiscardPlanRequest = false;
            _isRepairRequested = false;
            _generationStage = GenerationStage.Idle;
            _waitFrameCounter = 0;
            _currentTourIndex = 0;
            _activePlan = null;
            _currentPlanSeed = 0;
            _plannedTours.Clear();
            _plannedTourColors?.Clear();

            _depotFinder?.Reset();
            _roadScanner?.Reset();
            _roadAssigner?.Reset();
        }

        private void CleanLegacySubObjectStops()
        {
            try
            {
                var stopQuery = GetEntityQuery(new EntityQueryDesc
                {
                    All = new ComponentType[]
                    {
                        ComponentType.ReadOnly<Game.Routes.BusStop>(),
                        ComponentType.ReadOnly<Game.Objects.Attached>(),
                    },
                    None = new ComponentType[]
                    {
                        ComponentType.ReadOnly<Deleted>(),
                        ComponentType.ReadOnly<Game.Tools.Temp>(),
                    }
                });

                if (stopQuery.IsEmptyIgnoreFilter)
                    return;

                var stopEntities = stopQuery.ToEntityArray(Allocator.Temp);
                var attachedArray = stopQuery.ToComponentDataArray<Game.Objects.Attached>(Allocator.Temp);

                var cleanedRoads = new HashSet<Entity>();

                for (int i = 0; i < stopEntities.Length; i++)
                {
                    var roadEntity = attachedArray[i].m_Parent;
                    if (roadEntity != Entity.Null && EntityManager.Exists(roadEntity) && cleanedRoads.Add(roadEntity))
                    {
                        ClearRoadDeadEndNotifications(roadEntity);
                    }
                }

                stopEntities.Dispose();
                attachedArray.Dispose();

                if (cleanedRoads.Count > 0)
                {
                    log.Info($"CleanLegacySubObjectStops: Cleaned legacy SubObject entries and unblocked road lanes on {cleanedRoads.Count} parent roads.");
                }
            }
            catch (Exception ex)
            {
                log.Warn($"CleanLegacySubObjectStops encountered non-critical issue: {ex.Message}");
            }
        }

        public void RequestGeneration()
        {
            // Wipe previously generated bus lines and roadside stops so new settings/density apply cleanly
            DeleteAllBusLinesAndStops();

            _hasRun = false;
            _isManualRequest = true;
            _generationStage = GenerationStage.WaitingForCleanup;
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

        private List<PlannedRoute> _activePlan = null;
        private int _currentPlanSeed = 0;
        private bool _isPlanRequest = false;
        private int _requestedSeed = 0;
        private bool _isBuildPlanRequest = false;
        private bool _isDiscardPlanRequest = false;

        public void RequestPlan(int seedOffset = 0)
        {
            _requestedSeed = seedOffset;
            _isPlanRequest = true;
            _hasRun = false;
            _depotFinder?.Reset();
            _roadScanner?.Reset();
            _roadAssigner?.Reset();
        }

        public void RequestBuildSelectedPlan()
        {
            _isBuildPlanRequest = true;
        }

        public void RequestDiscardPlan()
        {
            _isDiscardPlanRequest = true;
        }

        public void ToggleRoute(int routeId, bool enabled)
        {
            if (_activePlan == null) return;
            var route = _activePlan.Find(r => r.Id == routeId);
            if (route != null)
            {
                route.Enabled = enabled;
                AutoBusLinesUISystem.Instance?.UpdatePlan(_activePlan, _currentPlanSeed);
            }
        }

        public void ToggleStop(int routeId, int stopIndex, bool enabled)
        {
            if (_activePlan == null) return;
            var route = _activePlan.Find(r => r.Id == routeId);
            if (route != null)
            {
                var stop = route.Stops.Find(s => s.Index == stopIndex);
                if (stop != null)
                {
                    stop.Enabled = enabled;
                    AutoBusLinesUISystem.Instance?.UpdatePlan(_activePlan, _currentPlanSeed);
                }
            }
        }

        public List<PlannedRoute> GetCurrentPlan() => _activePlan;
        public bool HasActivePlan() => _activePlan != null && _activePlan.Count > 0;
        public PlannedRoute GetActivePlannedRoute(int routeId) => _activePlan?.Find(r => r.Id == routeId);


        protected override void OnUpdate()
        {
            // Stage 1: Wait for any pending cleanup from previous runs to settle
            if (_generationStage == GenerationStage.WaitingForCleanup)
            {
                _waitFrameCounter++;
                if (_waitFrameCounter >= 5)
                {
                    _generationStage = GenerationStage.Idle;
                    _waitFrameCounter = 0;
                    ExecuteGeneration(planOnly: false, seedOffset: 0, autoOpenPanel: false);
                }
                return;
            }

            // Stage 2: Wait for newly created bus stops to be indexed in the spatial world
            if (_generationStage == GenerationStage.WaitingForStopIndexing)
            {
                _waitFrameCounter++;
                if (_waitFrameCounter >= INDEXING_DELAY_FRAMES)
                {
                    _generationStage = GenerationStage.InstantiatingRoutes;
                    _currentTourIndex = 0;
                    _createdLineCount = 0;
                    _waitFrameCounter = 0;
                    log.Info($"Stage 2: Bus stop indexing settled across {INDEXING_DELAY_FRAMES} frames. Beginning staged route instantiation...");
                }
                return;
            }

            // Stage 3: Incrementally instantiate bus lines across simulation frames
            if (_generationStage == GenerationStage.InstantiatingRoutes)
            {
                int routesCreatedThisTick = 0;
                while (_currentTourIndex < _plannedTours.Count && routesCreatedThisTick < ROUTES_PER_FRAME)
                {
                    var tour = _plannedTours[_currentTourIndex];
                    if (tour.Count >= ABSOLUTE_MIN_STOPS)
                    {
                        _createdLineCount++;
                        int lineNumber = _createdLineCount;
                        float routeDistanceKm = CalculateTourLength(tour) / 1000f;
                        Color32? tourColor = (_plannedTourColors != null && _currentTourIndex < _plannedTourColors.Count)
                            ? _plannedTourColors[_currentTourIndex]
                            : (Color32?)null;
                        CreateBusLine(_cachedBusLinePrefabEntity, _cachedBusLineRouteData, tour, lineNumber, 0, _currentTourIndex, tourColor);
                        log.Info($"Stage 3: Created Bus Line #{lineNumber} with {tour.Count} stops (Est. Length: {routeDistanceKm:F1} km) [Progress: {_currentTourIndex + 1}/{_plannedTours.Count}]");
                    }
                    _currentTourIndex++;
                    routesCreatedThisTick++;
                }

                if (_currentTourIndex >= _plannedTours.Count)
                {
                    int lineCount = _createdLineCount;
                    log.Info($"=== Generation Complete: Successfully created {lineCount} bus lines across city neighborhoods ({_totalStopsActive}/{_totalStopsPlaced} stops active across staged simulation frames). ===");
                    _generationStage = GenerationStage.Idle;
                    _hasRun = true;
                    _isManualRequest = false;
                    _plannedTours.Clear();

                    AutoBusLinesUISystem.Instance?.UpdatePlan(null, 0, $"Successfully built {lineCount} bus lines!");
                    AutoBusLinesUISystem.Instance?.SetPlanStatus("idle", $"Successfully built {lineCount} bus lines!");
                }
                return;
            }

            if (_isDiscardPlanRequest)
            {
                _isDiscardPlanRequest = false;
                _activePlan = null;
                _currentPlanSeed = 0;
                _plannedTours.Clear();
                _plannedTourColors.Clear();
                AutoBusLinesUISystem.Instance?.UpdatePlan(null, 0, "Plan discarded.");
                AutoBusLinesUISystem.Instance?.SetPlanStatus("idle", "Plan discarded.");
                return;
            }

            if (_isRepairRequested)
            {
                _isRepairRequested = false;
                ExecuteRepairBrokenRoutes();
                return;
            }

            if (_isBuildPlanRequest)
            {
                _isBuildPlanRequest = false;
                ExecuteBuildSelectedPlan();
                return;
            }

            if (_isPlanRequest)
            {
                _isPlanRequest = false;
                ExecuteGeneration(planOnly: true, seedOffset: _requestedSeed, autoOpenPanel: true);
                return;
            }

            if (_hasRun)
                return;

            if (!_isManualRequest)
                return;

            if (Mod.setting != null && Mod.setting.EnablePlanMode)
            {
                _hasRun = true;
                _isManualRequest = false;
                ExecuteGeneration(planOnly: true, seedOffset: 0, autoOpenPanel: false);
                return;
            }

            ExecuteGeneration(planOnly: false, seedOffset: 0, autoOpenPanel: false);
        }
        private void ExecuteGeneration(bool planOnly, int seedOffset, bool autoOpenPanel)
        {
            if (_depotFinder == null) _depotFinder = World.GetOrCreateSystemManaged<DepotFinderSystem>();
            if (_roadScanner == null) _roadScanner = World.GetOrCreateSystemManaged<RoadNetworkScanner>();
            if (_roadAssigner == null) _roadAssigner = World.GetOrCreateSystemManaged<RoadDepotAssigner>();

            _depotFinder.ScanNow();
            _roadScanner.ScanNow();

            if (_depotFinder.AllHubs.Length > 0 && _roadScanner.RoadSegments.Length > 0)
                _roadAssigner.AssignNow();

            var roadToHub = _roadAssigner.GetRoadToDepotMap();

            if (_depotFinder.AllHubs.Length == 0)
            {
                log.Warn("ExecuteGeneration: No bus depots or stations found in city.");
                _hasRun = true;
                _isManualRequest = false;
                AutoBusLinesUISystem.Instance?.SetPlanStatus("idle", "No Bus Depot or Bus Station found! Please build at least one Bus Depot or Bus Station first.");
                return;
            }

            if (_roadScanner.RoadSegments.Length == 0)
            {
                log.Warn("ExecuteGeneration: No municipal paved roads found in city.");
                _hasRun = true;
                _isManualRequest = false;
                AutoBusLinesUISystem.Instance?.SetPlanStatus("idle", "No municipal paved roads found in city.");
                return;
            }

            if (roadToHub.Count == 0)
            {
                log.Warn("ExecuteGeneration: Could not map any roads to transit hubs.");
                _hasRun = true;
                _isManualRequest = false;
                AutoBusLinesUISystem.Instance?.SetPlanStatus("idle", "Could not connect city roads to available transit depots.");
                return;
            }

            log.Info($"=== Starting AutoBusLines {(planOnly ? "Plan Mode Preview" : "Direct Generation")} (Variant #{seedOffset + 1}) ===");

            if (!FindBusStopPrefabs(out List<Entity> candidatePrefabs, out Entity defaultPrefab))
            {
                log.Error("Could not find any valid Bus Stop Prefabs with ObjectData archetype!");
                _hasRun = true;
                _isManualRequest = false;
                AutoBusLinesUISystem.Instance?.SetPlanStatus("idle", "Error: No bus stop prefabs found.");
                return;
            }

            if (!FindBusLinePrefab(out Entity busLinePrefabEntity, out RouteData busLineRouteData))
            {
                log.Error("Could not find a valid Bus Line Prefab with RouteData archetypes!");
                _hasRun = true;
                _isManualRequest = false;
                AutoBusLinesUISystem.Instance?.SetPlanStatus("idle", "Error: Bus line prefab not found.");
                return;
            }

            log.Info($"Found {candidatePrefabs.Count} Bus Stop Candidate Prefabs and Bus Line Prefab {busLinePrefabEntity.Index}");

            var rng = new Unity.Mathematics.Random(185392u + (uint)(seedOffset * 7919));

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
            var allExistingStops = DiscoverExistingBusStops(out var existingStopsByRoad);
            log.Info($"Discovered {allExistingStops.Count} pre-existing bus stops in the city across all neighborhoods");

            // -------------------------------------------------------------
            // STEP 1: Plan Transit Corridors
            // Corridors are continuous street chains across the city.
            // -------------------------------------------------------------
            int minStopsPerLine = Mod.setting != null ? Mod.setting.MinStopsPerLine : MIN_STOPS_PER_LINE;
            int maxStopsPerLine = Mod.setting != null ? Mod.setting.MaxStopsPerLine : MAX_STOPS_PER_LINE;
            float maxRouteLength = Mod.setting != null ? (float)Mod.setting.MaxRouteLength : DEFAULT_MAX_ROUTE_LENGTH;
            float targetSpacing = Mod.setting != null ? (float)Mod.setting.TargetStopSpacing : 200f;

            if (minStopsPerLine < ABSOLUTE_MIN_STOPS) minStopsPerLine = ABSOLUTE_MIN_STOPS;
            if (maxStopsPerLine < minStopsPerLine) maxStopsPerLine = minStopsPerLine;
            if (maxRouteLength < 2000f) maxRouteLength = 2000f;
            if (targetSpacing < 50f) targetSpacing = 50f;
            if (targetSpacing > 600f) targetSpacing = 600f;

            float servedRadius = targetSpacing * 0.85f;
            float minCollisionSpacing = math.clamp(targetSpacing * 0.40f, 35f, 120f);
            float minSpanLength = math.min(MIN_SEGMENT_LENGTH_FOR_STOP, targetSpacing * 0.5f);

            var allCorridors = BuildCorridors();
            allCorridors.Sort((a, b) => b.TotalLength.CompareTo(a.TotalLength));

            if (seedOffset > 0)
            {
                // Deterministic shuffle with seedOffset to produce alternative corridor pairings and route loops
                var prng = new System.Random(seedOffset * 1013);
                for (int i = allCorridors.Count - 1; i > 0; i--)
                {
                    int j = prng.Next(i + 1);
                    var temp = allCorridors[i];
                    allCorridors[i] = allCorridors[j];
                    allCorridors[j] = temp;
                }
            }

            // -------------------------------------------------------------
            // STEP 2: Place Bus Stops Across ALL City Blocks & Neighborhoods
            // Ensures full city coverage based on user's target density/spacing.
            // On two-way streets, stops alternate curbs to provide two-way coverage.
            // Multi-stop placement supports long arterial spans.
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
                    CorridorIndex = allExistingStops[e].CorridorIndex,
                    SpanId = -1
                });
            }

            int totalStopsPlaced = 0;
            int existingStopsReused = 0;

            // Precompute bridge nodes to identify long bridge approach connector spans
            var bridgeNodes = new HashSet<Entity>();
            if (_roadScanner != null && _roadScanner.DrivableSegments.IsCreated)
            {
                for (int i = 0; i < _roadScanner.DrivableSegments.Length; i++)
                {
                    var e = _roadScanner.DrivableSegments[i];
                    if (IsElevatedOrBridge(e) && EntityManager.HasComponent<Edge>(e))
                    {
                        var edge = EntityManager.GetComponentData<Edge>(e);
                        if (edge.m_Start != Entity.Null) bridgeNodes.Add(edge.m_Start);
                        if (edge.m_End != Entity.Null) bridgeNodes.Add(edge.m_End);
                    }
                }
            }

            // Sort spans so major arterial corridors are processed first, then neighborhood side streets.
            // Sorting by CorridorLength first ensures dominant avenues receive clean transit stops,
            // while minor perpendicular side streets within servedRadius are properly served without cluttering "the sides".
            blockSpans.Sort((a, b) =>
            {
                int cmp = b.CorridorLength.CompareTo(a.CorridorLength);
                if (cmp != 0) return cmp;
                return b.TotalLength.CompareTo(a.TotalLength);
            });

            bool excludeDeadEnds = Mod.setting != null ? Mod.setting.ExcludeDeadEnds : true;
            float deadEndThreshold = Mod.setting != null ? (float)Mod.setting.DeadEndDistanceThreshold : 500f;

            Entity districtFilter = FindDistrictEntity(TargetDistrictId);
            string districtName = districtFilter != Entity.Null ? (_nameSystem?.GetRenderedLabelName(districtFilter) ?? $"District {districtFilter.Index}") : "All City";
            if (districtFilter != Entity.Null)
            {
                log.Info($"[ExecuteGeneration] Scoping transit network generation to District: '{districtName}' (Entity {districtFilter.Index})");
            }

            int totalDistrictSpans = 0;
            for (int s = 0; s < blockSpans.Count; s++)
            {
                var span = blockSpans[s];
                if (span.TotalLength < minSpanLength)
                    continue;

                // Skip spans outside the target district if filtered
                if (districtFilter != Entity.Null)
                {
                    if (!IsSpanInDistrict(span, districtFilter))
                        continue;
                    totalDistrictSpans++;
                }

                // Skip cul-de-sacs and dead-end roads unless they exceed the user's distance threshold
                if (excludeDeadEnds && span.IsDeadEnd)
                {
                    float effectiveLength = math.max(span.DeadEndBranchLength, span.TotalLength);
                    if (effectiveLength < deadEndThreshold)
                    {
                        log.Info($"[ExcludeDeadEnds] Skipping stop on short dead-end span #{span.SpanId}: branch={effectiveLength:F1}m (span={span.TotalLength:F1}m) (< threshold {deadEndThreshold:F0}m)");
                        continue;
                    }
                    else
                    {
                        log.Info($"[ExcludeDeadEnds] Allowing stop on long dead-end span #{span.SpanId}: branch={effectiveLength:F1}m (span={span.TotalLength:F1}m) (>= threshold {deadEndThreshold:F0}m)");
                    }
                }

                // Long connector spans connecting directly to an elevated bridge deck:
                // Use wider spacing (450m) so long approach links don't spam 6+ stops in empty terrain,
                // while still maintaining stepping-stone connectivity for bus lines across the river.
                bool touchesBridge = (span.StartNode != Entity.Null && bridgeNodes.Contains(span.StartNode)) ||
                                     (span.EndNode != Entity.Null && bridgeNodes.Contains(span.EndNode));
                float effectiveSpacing = (touchesBridge && span.TotalLength >= 350f)
                    ? math.max(targetSpacing * 2.2f, 450f)
                    : targetSpacing;

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
                        CorridorIndex = span.CorridorIndex,
                        SpanId = span.SpanId
                    });

                    // For short to moderate spans, the reused stop covers this span
                    if (span.TotalLength <= effectiveSpacing * 1.6f)
                        continue;
                }

                // Determine how many stops this span should receive based on effectiveSpacing
                int stopsToPlace = math.max(1, (int)math.round(span.TotalLength / effectiveSpacing));
                if (touchesBridge && span.TotalLength >= 350f)
                {
                    stopsToPlace = math.min(stopsToPlace, 2); // Cap long bridge approaches at at most 2 stops
                }

                for (int i = 0; i < stopsToPlace; i++)
                {
                    Entity roadEdge;
                    float edgeT;
                    float3 roadPos;
                    float3 roadTan;

                    if (stopsToPlace == 1)
                    {
                        roadEdge = span.MidEdge;
                        edgeT = span.MidT;
                        roadPos = span.MidPosition;
                        roadTan = span.MidTangent;

                        // If the span's midpoint edge happens to be a bridge or drawbridge, search for an alternative non-bridge edge in the span
                        if (IsElevatedOrBridge(roadEdge))
                        {
                            bool foundAlternative = false;
                            for (int e = 0; e < span.Edges.Count; e++)
                            {
                                var candEdge = span.Edges[e].EdgeEntity;
                                if (!IsElevatedOrBridge(candEdge) && EntityManager.HasComponent<Curve>(candEdge))
                                {
                                    var candCurve = EntityManager.GetComponentData<Curve>(candEdge);
                                    roadEdge = candEdge;
                                    edgeT = 0.5f;
                                    roadPos = MathUtils.Position(candCurve.m_Bezier, 0.5f);
                                    float3 cTan = MathUtils.Tangent(candCurve.m_Bezier, 0.5f);
                                    if (span.Edges[e].IsReversed) cTan = -cTan;
                                    roadTan = math.normalizesafe(cTan, new float3(0, 0, 1));
                                    foundAlternative = true;
                                    break;
                                }
                            }
                            if (!foundAlternative)
                                continue; // Entire span consists of bridge edges; skip placing stops on bridge deck
                        }
                    }
                    else
                    {
                        float targetDist = (i + 1.0f) / (stopsToPlace + 1.0f) * span.TotalLength;
                        if (!TryGetSpanPointAtDistance(span, targetDist, out roadEdge, out edgeT, out roadPos, out roadTan))
                            continue;

                        if (IsElevatedOrBridge(roadEdge))
                            continue;
                    }

                    // Check hub exclusion
                    bool ptNearHub = false;
                    for (int ep = 0; ep < exclusionPositions.Count; ep++)
                    {
                        if (math.distance(roadPos, exclusionPositions[ep]) < HUB_EXCLUSION_RADIUS)
                        {
                            ptNearHub = true;
                            break;
                        }
                    }
                    if (ptNearHub)
                        continue;

                    // Check if this candidate location is already served by a committed stop
                    // Stops on the same span are allowed if they exceed minCollisionSpacing;
                    // Other spans are checked against servedRadius.
                    bool isAlreadyServed = false;
                    for (int k = 0; k < committedStops.Count; k++)
                    {
                        float checkDist = (committedStops[k].SpanId == span.SpanId) ? minCollisionSpacing : servedRadius;
                        if (math.distance(roadPos, committedStops[k].Position) < checkDist)
                        {
                            isAlreadyServed = true;
                            break;
                        }
                    }
                    if (isAlreadyServed)
                        continue;

                    // Place alternating kerb stop
                    int order = span.SpanOrderInCorridor + i;
                    float3 forwardTan = (math.lengthsq(span.CorridorForwardTangent) > 0.001f) ? span.CorridorForwardTangent : roadTan;

                    if (TryPlaceAlternatingKerbStop(span, roadEdge, edgeT, roadPos, forwardTan,
                                                    order,
                                                    committedStops, candidatePrefabs, ref rng,
                                                    minCollisionSpacing, planOnly, allGlobalStops.Count, out PlacedStop placedStop))
                    {
                        allGlobalStops.Add(placedStop);
                        totalStopsPlaced++;
                    }
                }
            }

            string scopeDesc = districtFilter != Entity.Null ? $"in District '{districtName}' ({totalDistrictSpans} matching spans)" : "across the city";
            log.Info($"Step 2 Complete: {allGlobalStops.Count} total stops available {scopeDesc} ({totalStopsPlaced} newly placed, {existingStopsReused} existing reused) with target spacing {targetSpacing:F0}m.");

            // -------------------------------------------------------------
            // Coverage & Active Routes Verification
            // Prevent generating duplicate lines if roads and stops already have active service.
            // -------------------------------------------------------------
            var alreadyCoveredStops = GetStopsCoveredByActiveBusLines(out int activeBusLinesInCity);
            log.Info($"Coverage Check: Discovered {activeBusLinesInCity} active bus lines currently serving {alreadyCoveredStops.Count} stops in the city.");

            int unservedStopCount = 0;
            for (int i = 0; i < allGlobalStops.Count; i++)
            {
                if (!allGlobalStops[i].IsStationBay && !alreadyCoveredStops.Contains(allGlobalStops[i].StopEntity))
                {
                    unservedStopCount++;
                }
            }

            if (activeBusLinesInCity > 0 && unservedStopCount == 0)
            {
                log.Info($"ExecuteGeneration: All {allGlobalStops.Count} stops are already covered by {activeBusLinesInCity} active bus lines. Skipping duplicate line generation.");
                _generationStage = GenerationStage.Idle;
                _hasRun = true;
                _isManualRequest = false;
                string statusMsg = (districtFilter != Entity.Null)
                    ? $"All stops in '{districtName}' are already covered by active bus lines."
                    : $"City transit network is already fully covered by {activeBusLinesInCity} active bus lines ({alreadyCoveredStops.Count} stops served). Use 'Delete All' if you wish to redesign from scratch.";
                AutoBusLinesUISystem.Instance?.SetPlanStatus("idle", statusMsg);
                return;
            }

            // -------------------------------------------------------------
            // STEP 3: Plan the bus loops (see BusLineGenerator.Planning.cs)
            // Builds validated closed cycles of legal, connected road edges: one-way directions are
            // respected, hairpin U-turns are only used at dead ends, and every loop stays within
            // maxRouteLength.
            // -------------------------------------------------------------
            var allTours = PlanTours(allGlobalStops, allCorridors, minStopsPerLine, maxStopsPerLine, maxRouteLength, alreadyCoveredStops, districtFilter, out var servedStopEntities);

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
                log.Warn($"No new bus tours formed. Total available stops in scope: {allGlobalStops.Count}. Active lines in city: {activeBusLinesInCity}.");
                _generationStage = GenerationStage.Idle;
                _hasRun = true;
                _isManualRequest = false;
                string msg;
                if (allGlobalStops.Count < ABSOLUTE_MIN_STOPS)
                {
                    msg = districtFilter != Entity.Null
                        ? $"District '{districtName}' contains only {allGlobalStops.Count} bus stops (minimum {ABSOLUTE_MIN_STOPS} required to form a bus route)."
                        : $"Only {allGlobalStops.Count} bus stops could be placed across the city (minimum {ABSOLUTE_MIN_STOPS} required).";
                }
                else if (activeBusLinesInCity > 0 && unservedStopCount == 0)
                {
                    msg = districtFilter != Entity.Null
                        ? $"All stops in District '{districtName}' are already served by active bus lines."
                        : $"All road corridors and stops are already covered by {activeBusLinesInCity} active bus lines.";
                }
                else
                {
                    msg = districtFilter != Entity.Null
                        ? $"No valid bus loops could be closed within District '{districtName}'. Try including adjacent roads or placing more stops."
                        : "No valid bus tours could be formed from the road network.";
                }
                AutoBusLinesUISystem.Instance?.SetPlanStatus("idle", msg);
                return;
            }

            if (planOnly)
            {
                _plannedTours = new List<List<PlacedStop>>(allTours);
                LineColorMode colorMode = Mod.setting != null ? Mod.setting.LineColoring : LineColorMode.PerStation;
                var tourColors = ComputeTourColors(allTours, colorMode);

                var plannedRoutes = new List<PlannedRoute>();
                for (int t = 0; t < allTours.Count; t++)
                {
                    var tour = allTours[t];
                    if (tour.Count < ABSOLUTE_MIN_STOPS) continue;

                    int lineNumber = plannedRoutes.Count + 1;
                    float routeDistKm = CalculateTourLength(tour) / 1000f;
                    string hexColor = (t < tourColors.Count) ? tourColors[t] : GetLineHexColor(lineNumber);

                    var routeData = new PlannedRoute
                    {
                        Id = lineNumber,
                        Name = $"Line {lineNumber}",
                        Color = hexColor,
                        LengthKm = (float)Math.Round(routeDistKm, 1),
                        Enabled = true
                    };

                    // First pass: collect street names and count occurrences on this tour
                    var stopStreetNames = new string[tour.Count];
                    var streetCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                    for (int s = 0; s < tour.Count; s++)
                    {
                        var st = tour[s];
                        if (!st.IsStationBay)
                        {
                            string street = GetRoadStreetName(st.RoadEntity) ?? "Road";
                            stopStreetNames[s] = street;
                            streetCounts[street] = streetCounts.TryGetValue(street, out int c) ? c + 1 : 1;
                        }
                    }

                    // Second pass: assign formatted names with incremental numbering if duplicated
                    var streetIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                    for (int s = 0; s < tour.Count; s++)
                    {
                        var st = tour[s];
                        string stopDesc;

                        if (st.IsStationBay)
                        {
                            stopDesc = "Station Bay";
                        }
                        else
                        {
                            string street = stopStreetNames[s] ?? "Road";
                            int totalOnStreet = streetCounts[street];
                            int currentIndex = streetIndices.TryGetValue(street, out int cur) ? cur + 1 : 1;
                            streetIndices[street] = currentIndex;

                            string prefix = st.IsPreExisting ? "Stop" : "New Stop";
                            if (totalOnStreet > 1)
                            {
                                stopDesc = $"{prefix} {street} {currentIndex}";
                            }
                            else
                            {
                                stopDesc = $"{prefix} {street}";
                            }
                        }

                        routeData.Stops.Add(new PlannedStopData
                        {
                            Index = s + 1,
                            VirtualId = st.StopEntity.Index,
                            Name = stopDesc,
                            PosX = (float)Math.Round(st.Position.x, 1),
                            PosY = (float)Math.Round(st.Position.y, 1),
                            PosZ = (float)Math.Round(st.Position.z, 1),
                            IsStationBay = st.IsStationBay,
                            IsPreExisting = st.IsPreExisting,
                            Enabled = true,
                            InternalStop = st
                        });
                    }
                    plannedRoutes.Add(routeData);
                }

                _activePlan = plannedRoutes;
                _currentPlanSeed = seedOffset;
                _generationStage = GenerationStage.Idle;
                _hasRun = true;
                _isManualRequest = false;

                log.Info($"Plan Mode: Successfully generated {plannedRoutes.Count} proposed lines with {servedStopEntities.Count} stops (Variant #{seedOffset + 1}).");
                AutoBusLinesUISystem.Instance?.UpdatePlan(plannedRoutes, seedOffset, $"Preview Plan Ready: {plannedRoutes.Count} lines proposed ({servedStopEntities.Count} stops).");
                if (autoOpenPanel)
                {
                    AutoBusLinesUISystem.Instance?.OpenPanel();
                }
                return;
            }

            // Stage 1 Completion: Store planned tours and prefab info for staged execution across subsequent frames
            _plannedTours = new List<List<PlacedStop>>(allTours);
            LineColorMode directColorMode = Mod.setting != null ? Mod.setting.LineColoring : LineColorMode.PerStation;
            var directTourColors = ComputeTourColors(allTours, directColorMode);
            _plannedTourColors = new List<Color32>();
            for (int i = 0; i < directTourColors.Count; i++)
            {
                _plannedTourColors.Add(HexToColor(directTourColors[i]));
            }
            _cachedBusLinePrefabEntity = busLinePrefabEntity;
            _cachedBusLineRouteData = busLineRouteData;
            _totalStopsPlaced = allGlobalStops.Count;
            _totalStopsActive = servedStopEntities.Count;
            _generationStage = GenerationStage.WaitingForStopIndexing;
            _waitFrameCounter = 0;

            log.Info($"Stage 1 Complete: Placed {_totalStopsPlaced} roadside bus stops and planned {_plannedTours.Count} bus lines. Yielding {INDEXING_DELAY_FRAMES} simulation frames for spatial quadtrees and net indexing to settle before drawing routes...");
            return;
        }

    }
}
