using Colossal.Logging;
using Game;
using Game.Net;
using Game.Objects;
using Unity.Entities;
using Unity.Collections;
using Unity.Mathematics;

namespace AutoBusLines
{
    public partial class RoadDepotAssigner : GameSystemBase
    {
        private static readonly ILog log = LogManager.GetLogger($"{nameof(AutoBusLines)}.{nameof(RoadDepotAssigner)}");

        private bool _hasRun = false;

        private NativeHashMap<Entity, Entity> _roadToDepot;

        private DepotFinderSystem _depotFinder;
        private RoadNetworkScanner _roadScanner;

        protected override void OnCreate()
        {
            base.OnCreate();

            _roadToDepot = new NativeHashMap<Entity, Entity>(1000, Allocator.Persistent);

            _depotFinder = World.GetOrCreateSystemManaged<DepotFinderSystem>();
            _roadScanner = World.GetOrCreateSystemManaged<RoadNetworkScanner>();

            log.Info("RoadDepotAssigner (Hub Assigner) created");
        }

        protected override void OnUpdate()
        {
            if (_hasRun)
                return;

            // Wait for transit hub finder and road scanner to complete
            if (_depotFinder.AllHubs.Length == 0 || _roadScanner.RoadSegments.Length == 0)
                return;

            _roadToDepot.Clear();

            // Assign each road to nearest Hub (Bus Station or Bus Depot)
            int assignedCount = 0;
            for (int i = 0; i < _roadScanner.RoadSegments.Length; i++)
            {
                var roadEntity = _roadScanner.RoadSegments[i];

                if (!EntityManager.HasComponent<Curve>(roadEntity))
                    continue;

                var curve = EntityManager.GetComponentData<Curve>(roadEntity);

                // Calculate road midpoint from Bezier curve
                float3 roadMidpoint = curve.m_Bezier.a * 0.125f +
                                     curve.m_Bezier.b * 0.375f +
                                     curve.m_Bezier.c * 0.375f +
                                     curve.m_Bezier.d * 0.125f;

                // Find nearest hub
                float minDistance = float.MaxValue;
                Entity nearestHub = Entity.Null;

                for (int j = 0; j < _depotFinder.AllHubs.Length; j++)
                {
                    var hub = _depotFinder.AllHubs[j];
                    float distance = math.distance(roadMidpoint, hub.Position);

                    // Slight preference to passenger Bus Stations over raw storage Depots
                    if (hub.IsStation)
                    {
                        distance *= 0.85f;
                    }

                    if (distance < minDistance)
                    {
                        minDistance = distance;
                        nearestHub = hub.HubEntity;
                    }
                }

                if (nearestHub != Entity.Null)
                {
                    _roadToDepot.Add(roadEntity, nearestHub);
                    assignedCount++;
                }
            }

            log.Info($"Assigned {assignedCount} roads across {_depotFinder.AllHubs.Length} Transit Hubs ({_depotFinder.BusStations.Length} Stations, {_depotFinder.BusDepots.Length} Depots)");

            _hasRun = true;
        }

        public NativeHashMap<Entity, Entity> GetRoadToDepotMap()
        {
            return _roadToDepot;
        }

        public void Reset()
        {
            _hasRun = false;
        }

        protected override void OnDestroy()
        {
            if (_roadToDepot.IsCreated)
                _roadToDepot.Dispose();
            base.OnDestroy();
        }
    }
}
