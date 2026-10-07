using System;
using System.Collections.Generic;
using Colossal.Logging;
using Game;
using Game.Prefabs;
using Game.Buildings;
using Game.Common;
using Game.Objects;
using Game.Routes;
using Game.Tools;
using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;

namespace AutoBusLines
{
    public struct HubInfo
    {
        public Entity HubEntity;
        public float3 Position;
        public bool IsStation;
    }

    public partial class DepotFinderSystem : GameSystemBase
    {
        private static readonly ILog log = LogManager.GetLogger($"{nameof(AutoBusLines)}.{nameof(DepotFinderSystem)}");

        private EntityQuery _depotQuery;
        private EntityQuery _stationQuery;
        private EntityQuery _publicStationQuery;
        private EntityQuery _busStopQuery;
        private PrefabSystem _prefabSystem;
        private bool _hasRun = false;
        private int _lastLoggedHubCount = -1;
        private int _frameThrottleCounter = 0;

        public NativeList<Entity> BusDepots { get; private set; }
        public NativeList<Entity> BusStations { get; private set; }
        public NativeList<HubInfo> AllHubs { get; private set; }
        public NativeList<Entity> StationPlatformStops { get; private set; }
        public Dictionary<Entity, List<Entity>> StationToPlatforms { get; private set; }

        protected override void OnCreate()
        {
            base.OnCreate();

            _prefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            _depotQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Buildings.TransportDepot>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadOnly<Game.Objects.OutsideConnection>(),
                    ComponentType.ReadOnly<Game.Net.OutsideConnection>(),
                }
            });

            _stationQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Buildings.TransportStation>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadOnly<Game.Objects.OutsideConnection>(),
                    ComponentType.ReadOnly<Game.Net.OutsideConnection>(),
                }
            });

            _publicStationQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Buildings.PublicTransportStation>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadOnly<Game.Objects.OutsideConnection>(),
                    ComponentType.ReadOnly<Game.Net.OutsideConnection>(),
                }
            });

            _busStopQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<PrefabRef>(),
                    ComponentType.ReadOnly<Game.Routes.TransportStop>(),
                    ComponentType.ReadOnly<Transform>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                    ComponentType.ReadOnly<Game.Objects.OutsideConnection>(),
                    ComponentType.ReadOnly<Game.Net.OutsideConnection>(),
                }
            });

            BusDepots = new NativeList<Entity>(Allocator.Persistent);
            BusStations = new NativeList<Entity>(Allocator.Persistent);
            AllHubs = new NativeList<HubInfo>(Allocator.Persistent);
            StationPlatformStops = new NativeList<Entity>(Allocator.Persistent);
            StationToPlatforms = new Dictionary<Entity, List<Entity>>();

            log.Info("DepotFinderSystem (Hub Discovery) created");
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

            BusDepots.Clear();
            BusStations.Clear();
            AllHubs.Clear();
            StationPlatformStops.Clear();
            StationToPlatforms.Clear();

            // 1. Discover Bus Depots
            var depotEntities = _depotQuery.ToEntityArray(Allocator.Temp);
            var depotPrefabRefs = _depotQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);
            var depotTransforms = _depotQuery.ToComponentDataArray<Transform>(Allocator.Temp);

            for (int i = 0; i < depotEntities.Length; i++)
            {
                var entity = depotEntities[i];
                var prefabEntity = depotPrefabRefs[i].m_Prefab;

                if (IsOutsideConnection(entity, prefabEntity))
                    continue;

                if (EntityManager.HasComponent<TransportDepotData>(prefabEntity))
                {
                    var depotData = EntityManager.GetComponentData<TransportDepotData>(prefabEntity);
                    if (depotData.m_TransportType == TransportType.Bus)
                    {
                        var pos = depotTransforms[i].m_Position;
                        BusDepots.Add(entity);
                        AllHubs.Add(new HubInfo { HubEntity = entity, Position = pos, IsStation = false });
                        StationToPlatforms[entity] = new List<Entity>();
                        log.Info($"Found Bus Depot {entity.Index} at {pos}");
                    }
                }
            }

            depotEntities.Dispose();
            depotPrefabRefs.Dispose();
            depotTransforms.Dispose();

            // 2. Discover Bus Stations (TransportStation & PublicTransportStation)
            var candidateStationEntities = new List<Entity>();
            var candidateStationPositions = new List<float3>();

            var stationEntities = _stationQuery.ToEntityArray(Allocator.Temp);
            var stationPrefabRefs = _stationQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);
            var stationTransforms = _stationQuery.ToComponentDataArray<Transform>(Allocator.Temp);
            for (int i = 0; i < stationEntities.Length; i++)
            {
                if (IsOutsideConnection(stationEntities[i], stationPrefabRefs[i].m_Prefab))
                    continue;

                candidateStationEntities.Add(stationEntities[i]);
                candidateStationPositions.Add(stationTransforms[i].m_Position);
            }
            stationEntities.Dispose();
            stationPrefabRefs.Dispose();
            stationTransforms.Dispose();

            var publicStationEntities = _publicStationQuery.ToEntityArray(Allocator.Temp);
            var publicStationPrefabRefs = _publicStationQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);
            var publicStationTransforms = _publicStationQuery.ToComponentDataArray<Transform>(Allocator.Temp);
            for (int i = 0; i < publicStationEntities.Length; i++)
            {
                if (IsOutsideConnection(publicStationEntities[i], publicStationPrefabRefs[i].m_Prefab))
                    continue;

                if (!candidateStationEntities.Contains(publicStationEntities[i]))
                {
                    candidateStationEntities.Add(publicStationEntities[i]);
                    candidateStationPositions.Add(publicStationTransforms[i].m_Position);
                }
            }
            publicStationEntities.Dispose();
            publicStationPrefabRefs.Dispose();
            publicStationTransforms.Dispose();

            // 3. Scan Bus Stops to find platform bays belonging to Stations / Depots
            var stopEntities = _busStopQuery.ToEntityArray(Allocator.Temp);
            var stopPrefabRefs = _busStopQuery.ToComponentDataArray<PrefabRef>(Allocator.Temp);
            var stopTransforms = _busStopQuery.ToComponentDataArray<Transform>(Allocator.Temp);

            for (int i = 0; i < stopEntities.Length; i++)
            {
                var stopEntity = stopEntities[i];
                var prefabEntity = stopPrefabRefs[i].m_Prefab;

                if (IsOutsideConnection(stopEntity, prefabEntity))
                    continue;

                if (!EntityManager.HasComponent<TransportStopData>(prefabEntity))
                    continue;

                var stopData = EntityManager.GetComponentData<TransportStopData>(prefabEntity);
                if (stopData.m_TransportType != TransportType.Bus || !stopData.m_PassengerTransport)
                    continue;

                // Check if this stop has an Owner pointing to a Station or Depot
                Entity ownerEntity = Entity.Null;
                if (EntityManager.HasComponent<Owner>(stopEntity))
                {
                    ownerEntity = EntityManager.GetComponentData<Owner>(stopEntity).m_Owner;
                }
                else if (EntityManager.HasComponent<Attached>(stopEntity))
                {
                    var parent = EntityManager.GetComponentData<Attached>(stopEntity).m_Parent;
                    if (parent != Entity.Null && EntityManager.HasComponent<Owner>(parent))
                    {
                        ownerEntity = EntityManager.GetComponentData<Owner>(parent).m_Owner;
                    }
                }

                if (ownerEntity != Entity.Null && candidateStationEntities.Contains(ownerEntity))
                {
                    if (!BusStations.Contains(ownerEntity))
                    {
                        int sIdx = candidateStationEntities.IndexOf(ownerEntity);
                        BusStations.Add(ownerEntity);
                        AllHubs.Add(new HubInfo { HubEntity = ownerEntity, Position = candidateStationPositions[sIdx], IsStation = true });
                        StationToPlatforms[ownerEntity] = new List<Entity>();
                        log.Info($"Found Bus Station {ownerEntity.Index} at {candidateStationPositions[sIdx]}");
                    }

                    StationToPlatforms[ownerEntity].Add(stopEntity);
                    StationPlatformStops.Add(stopEntity);
                    log.Info($"-> Registered Platform Bay Stop {stopEntity.Index} for Bus Station {ownerEntity.Index}");
                }
                else if (ownerEntity != Entity.Null && BusDepots.Contains(ownerEntity))
                {
                    StationToPlatforms[ownerEntity].Add(stopEntity);
                    StationPlatformStops.Add(stopEntity);
                    log.Info($"-> Registered Platform Bay Stop {stopEntity.Index} for Bus Depot {ownerEntity.Index}");
                }
            }

            // Also check sub-objects buffer on candidate stations
            for (int s = 0; s < candidateStationEntities.Count; s++)
            {
                var stEntity = candidateStationEntities[s];
                if (EntityManager.HasBuffer<Game.Objects.SubObject>(stEntity))
                {
                    var subObjects = EntityManager.GetBuffer<Game.Objects.SubObject>(stEntity);
                    for (int sub = 0; sub < subObjects.Length; sub++)
                    {
                        var subEntity = subObjects[sub].m_SubObject;
                        if (EntityManager.HasComponent<Game.Routes.TransportStop>(subEntity) &&
                            EntityManager.HasComponent<PrefabRef>(subEntity))
                        {
                            var prefab = EntityManager.GetComponentData<PrefabRef>(subEntity).m_Prefab;
                            if (IsOutsideConnection(subEntity, prefab))
                                continue;

                            if (EntityManager.HasComponent<TransportStopData>(prefab))
                            {
                                var stopData = EntityManager.GetComponentData<TransportStopData>(prefab);
                                if (stopData.m_TransportType == TransportType.Bus && stopData.m_PassengerTransport)
                                {
                                    if (!BusStations.Contains(stEntity))
                                    {
                                        BusStations.Add(stEntity);
                                        AllHubs.Add(new HubInfo { HubEntity = stEntity, Position = candidateStationPositions[s], IsStation = true });
                                        StationToPlatforms[stEntity] = new List<Entity>();
                                        log.Info($"Found Bus Station {stEntity.Index} from SubObjects at {candidateStationPositions[s]}");
                                    }

                                    if (!StationToPlatforms[stEntity].Contains(subEntity))
                                    {
                                        StationToPlatforms[stEntity].Add(subEntity);
                                        StationPlatformStops.Add(subEntity);
                                        log.Info($"-> Registered SubObject Platform Stop {subEntity.Index} for Bus Station {stEntity.Index}");
                                    }
                                }
                            }
                        }
                    }
                }

                // Also check SubNet buffer on candidate stations for internal driveway platform stops
                if (EntityManager.HasBuffer<Game.Net.SubNet>(stEntity))
                {
                    var subNets = EntityManager.GetBuffer<Game.Net.SubNet>(stEntity);
                    for (int sn = 0; sn < subNets.Length; sn++)
                    {
                        var netEntity = subNets[sn].m_SubNet;
                        if (EntityManager.HasBuffer<Game.Objects.SubObject>(netEntity))
                        {
                            var netSubObjects = EntityManager.GetBuffer<Game.Objects.SubObject>(netEntity);
                            for (int nso = 0; nso < netSubObjects.Length; nso++)
                            {
                                var subEntity = netSubObjects[nso].m_SubObject;
                                if (EntityManager.HasComponent<Game.Routes.TransportStop>(subEntity) &&
                                    EntityManager.HasComponent<PrefabRef>(subEntity))
                                {
                                    var prefab = EntityManager.GetComponentData<PrefabRef>(subEntity).m_Prefab;
                                    if (IsOutsideConnection(subEntity, prefab))
                                        continue;

                                    if (EntityManager.HasComponent<TransportStopData>(prefab))
                                    {
                                        var stopData = EntityManager.GetComponentData<TransportStopData>(prefab);
                                        if (stopData.m_TransportType == TransportType.Bus && stopData.m_PassengerTransport)
                                        {
                                            if (!BusStations.Contains(stEntity))
                                            {
                                                BusStations.Add(stEntity);
                                                AllHubs.Add(new HubInfo { HubEntity = stEntity, Position = candidateStationPositions[s], IsStation = true });
                                                StationToPlatforms[stEntity] = new List<Entity>();
                                                log.Info($"Found Bus Station {stEntity.Index} from SubNet at {candidateStationPositions[s]}");
                                            }

                                            if (!StationToPlatforms[stEntity].Contains(subEntity))
                                            {
                                                StationToPlatforms[stEntity].Add(subEntity);
                                                StationPlatformStops.Add(subEntity);
                                                log.Info($"-> Registered SubNet Platform Stop {subEntity.Index} for Bus Station {stEntity.Index}");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }

            stopEntities.Dispose();
            stopPrefabRefs.Dispose();
            stopTransforms.Dispose();

            if (AllHubs.Length > 0)
            {
                if (_lastLoggedHubCount != AllHubs.Length)
                {
                    log.Info($"Discovery complete: Found {BusStations.Length} Bus Stations (with {StationPlatformStops.Length} total platform bays) and {BusDepots.Length} Bus Depots. Total Hubs: {AllHubs.Length}");
                    _lastLoggedHubCount = AllHubs.Length;
                }
                _hasRun = true;
                _frameThrottleCounter = 0;
            }
            else
            {
                if (_lastLoggedHubCount != 0)
                {
                    log.Info("DepotFinderSystem: No Bus Depots or Bus Stations found in the city yet. Waiting for player to construct transit hubs...");
                    _lastLoggedHubCount = 0;
                }
                _frameThrottleCounter = 60; // Throttle empty map rescanning to once every ~60 frames
            }
        }

        private bool IsOutsideConnection(Entity entity, Entity prefabEntity)
        {
            if (EntityManager.HasComponent<Game.Objects.OutsideConnection>(entity) ||
                EntityManager.HasComponent<Game.Net.OutsideConnection>(entity))
            {
                return true;
            }

            if (prefabEntity != Entity.Null)
            {
                if (EntityManager.HasComponent<Game.Prefabs.OutsideConnectionData>(prefabEntity))
                    return true;

                if (_prefabSystem != null)
                {
                    string pName = _prefabSystem.GetPrefabName(prefabEntity);
                    if (!string.IsNullOrEmpty(pName))
                    {
                        if (pName.IndexOf("Outside Connection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            pName.IndexOf("OutsideConnection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            pName.IndexOf("Placeholder", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return true;
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
            _lastLoggedHubCount = -1;
            if (BusDepots.IsCreated)
                BusDepots.Clear();
            if (BusStations.IsCreated)
                BusStations.Clear();
            if (AllHubs.IsCreated)
                AllHubs.Clear();
            if (StationPlatformStops.IsCreated)
                StationPlatformStops.Clear();
            StationToPlatforms?.Clear();
        }

        public void ScanNow()
        {
            _hasRun = false;
            _frameThrottleCounter = 0;
            OnUpdate();
        }

        protected override void OnDestroy()
        {
            if (BusDepots.IsCreated)
                BusDepots.Dispose();
            if (BusStations.IsCreated)
                BusStations.Dispose();
            if (AllHubs.IsCreated)
                AllHubs.Dispose();
            if (StationPlatformStops.IsCreated)
                StationPlatformStops.Dispose();
            base.OnDestroy();
        }
    }
}
