using Colossal.Logging;
using Game;
using Game.Net;
using Game.Common;
using Game.Tools;
using Game.Prefabs;
using Unity.Entities;
using Unity.Collections;

namespace AutoBusLines
{
    public partial class RoadNetworkScanner : GameSystemBase
    {
        private static readonly ILog log = LogManager.GetLogger($"{nameof(AutoBusLines)}.{nameof(RoadNetworkScanner)}");

        private EntityQuery _roadQuery;
        private PrefabSystem _prefabSystem;
        private bool _hasRun = false;
        private int _lastLoggedRoadCount = -1;
        private int _frameThrottleCounter = 0;

        public NativeList<Entity> RoadSegments { get; private set; }
        public NativeList<Entity> DrivableSegments { get; private set; }

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            _roadQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<Edge>(),
                    ComponentType.ReadOnly<Curve>(),
                    ComponentType.ReadOnly<Road>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadOnly<Game.Tools.Hidden>(),
                    ComponentType.ReadOnly<Game.Common.Owner>(),
                    ComponentType.ReadOnly<Game.Net.OutsideConnection>(),
                }
            });

            RoadSegments = new NativeList<Entity>(Allocator.Persistent);
            DrivableSegments = new NativeList<Entity>(Allocator.Persistent);

            log.Info("RoadNetworkScanner created");
        }

        protected override void OnUpdate()
        {
            if (_hasRun)
                return;

            if (_frameThrottleCounter > 0)
            {
                _frameThrottleCounter--;
                return;
            }

            RoadSegments.Clear();
            DrivableSegments.Clear();
            var entities = _roadQuery.ToEntityArray(Allocator.Temp);

            int gravelExcludedCount = 0;
            int highwayExcludedCount = 0;

            for (int i = 0; i < entities.Length; i++)
            {
                var roadEntity = entities[i];
                if (IsGravelRoad(roadEntity))
                {
                    gravelExcludedCount++;
                    continue;
                }

                DrivableSegments.Add(roadEntity);

                if (IsHighwayOrRamp(roadEntity))
                {
                    highwayExcludedCount++;
                    continue;
                }

                RoadSegments.Add(roadEntity);
            }

            if (RoadSegments.Length > 0)
            {
                if (_lastLoggedRoadCount != RoadSegments.Length)
                {
                    log.Info($"Scanned {entities.Length} total roads -> Added {RoadSegments.Length} municipal paved roads (Excluded {gravelExcludedCount} gravel/unpaved, {highwayExcludedCount} highways/ramps)");
                    _lastLoggedRoadCount = RoadSegments.Length;
                }
                _hasRun = true;
                _frameThrottleCounter = 0;
            }
            else
            {
                if (_lastLoggedRoadCount != 0)
                {
                    log.Info("RoadNetworkScanner: No paved municipal roads found in city yet.");
                    _lastLoggedRoadCount = 0;
                }
                _frameThrottleCounter = 60; // Throttle empty map rescanning to once every ~60 frames
            }

            entities.Dispose();
        }

        private bool IsHighwayOrRamp(Entity roadEntity)
        {
            if (EntityManager.HasComponent<PrefabRef>(roadEntity))
            {
                var prefabRef = EntityManager.GetComponentData<PrefabRef>(roadEntity);
                var prefabEntity = prefabRef.m_Prefab;

                if (prefabEntity != Entity.Null)
                {
                    if (_prefabSystem != null)
                    {
                        if (_prefabSystem.TryGetPrefab<PrefabBase>(prefabEntity, out var prefabBase) && prefabBase != null)
                        {
                            // 1. Check if RoadPrefab has m_HighwayRules == true
                            if (prefabBase is RoadPrefab roadPrefab && roadPrefab.m_HighwayRules)
                            {
                                return true;
                            }

                            // 2. Check prefab name for highway, freeway, motorway, and ramp keywords
                            string name = prefabBase.name;
                            if (!string.IsNullOrEmpty(name))
                            {
                                if (name.IndexOf("Highway", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    name.IndexOf("Freeway", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    name.IndexOf("Motorway", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    name.IndexOf("Ramp", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    name.IndexOf("Sliproad", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    name.IndexOf("Interchange", System.StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    return true;
                                }
                            }
                        }
                        else
                        {
                            string pName = _prefabSystem.GetPrefabName(prefabEntity);
                            if (!string.IsNullOrEmpty(pName))
                            {
                                if (pName.IndexOf("Highway", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    pName.IndexOf("Freeway", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    pName.IndexOf("Motorway", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    pName.IndexOf("Ramp", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    pName.IndexOf("Sliproad", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    pName.IndexOf("Interchange", System.StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }
            }

            return false;
        }

        private bool IsGravelRoad(Entity roadEntity)
        {
            // 1. Check Composition component on edge entity (Composition.m_Edge -> NetCompositionData)
            if (EntityManager.HasComponent<Composition>(roadEntity))
            {
                var comp = EntityManager.GetComponentData<Composition>(roadEntity);
                if (comp.m_Edge != Entity.Null && EntityManager.HasComponent<NetCompositionData>(comp.m_Edge))
                {
                    var compData = EntityManager.GetComponentData<NetCompositionData>(comp.m_Edge);
                    if ((compData.m_Flags.m_General & CompositionFlags.General.Gravel) != 0)
                    {
                        return true;
                    }
                }
            }

            // 2. Check PrefabRef
            if (EntityManager.HasComponent<PrefabRef>(roadEntity))
            {
                var prefabRef = EntityManager.GetComponentData<PrefabRef>(roadEntity);
                var prefabEntity = prefabRef.m_Prefab;

                if (prefabEntity != Entity.Null)
                {
                    // Check if the prefab has NetCompositionData
                    if (EntityManager.HasComponent<NetCompositionData>(prefabEntity))
                    {
                        var compData = EntityManager.GetComponentData<NetCompositionData>(prefabEntity);
                        if ((compData.m_Flags.m_General & CompositionFlags.General.Gravel) != 0)
                        {
                            return true;
                        }
                    }

                    // Check prefab name via PrefabSystem
                    if (_prefabSystem != null)
                    {
                        if (_prefabSystem.TryGetPrefab<PrefabBase>(prefabEntity, out var prefabBase) && prefabBase != null)
                        {
                            string name = prefabBase.name;
                            if (!string.IsNullOrEmpty(name))
                            {
                                if (name.IndexOf("Gravel", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    name.IndexOf("Dirt", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    name.IndexOf("Unpaved", System.StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    return true;
                                }
                            }
                        }
                        else
                        {
                            string pName = _prefabSystem.GetPrefabName(prefabEntity);
                            if (!string.IsNullOrEmpty(pName))
                            {
                                if (pName.IndexOf("Gravel", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    pName.IndexOf("Dirt", System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    pName.IndexOf("Unpaved", System.StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    return true;
                                }
                            }
                        }
                    }
                }
            }

            return false;
        }

        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            Reset();
        }

        public void Reset()
        {
            _hasRun = false;
            _frameThrottleCounter = 0;
            _lastLoggedRoadCount = -1;
            if (RoadSegments.IsCreated)
                RoadSegments.Clear();
            if (DrivableSegments.IsCreated)
                DrivableSegments.Clear();
        }

        public void ScanNow()
        {
            _hasRun = false;
            _frameThrottleCounter = 0;
            OnUpdate();
        }

        protected override void OnDestroy()
        {
            if (RoadSegments.IsCreated)
                RoadSegments.Dispose();
            if (DrivableSegments.IsCreated)
                DrivableSegments.Dispose();
            base.OnDestroy();
        }
    }
}
