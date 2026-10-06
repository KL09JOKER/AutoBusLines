using System.Text;
using Colossal.Logging;
using Game;
using Game.Common;
using Game.Pathfind;
using Game.Prefabs;
using Game.Routes;
using Unity.Entities;

namespace AutoBusLines
{
    /// <summary>
    /// Diagnostic system that inspects existing working routes to understand
    /// what components and buffer data the game expects.
    /// </summary>
    public partial class RouteInspector : GameSystemBase
    {
        private static readonly ILog _log = LogManager.GetLogger($"{nameof(AutoBusLines)}.{nameof(RouteInspector)}").SetShowsErrorsInUI(false);
        private bool _hasInspected = false;

        protected override void OnCreate()
        {
            base.OnCreate();
            _log.Info("RouteInspector system created");
        }

        private int _frameDelay = 0;

        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            Reset();
        }

        public void Reset()
        {
            _hasInspected = false;
            _frameDelay = 0;
            _log.Info("RouteInspector reset: scheduled to inspect routes 120 frames after line creation.");
        }

        protected override void OnUpdate()
        {
            if (_hasInspected)
                return;

            var query = SystemAPI.QueryBuilder()
                .WithAll<TransportLine, Route, PrefabRef>()
                .Build();

            if (query.IsEmpty)
                return;

            // Wait 120 frames (~2-3 sec) after routes exist so simulation has run pathfinding
            _frameDelay++;
            if (_frameDelay < 120)
                return;

            _log.Info("=== INSPECTING CREATED BUS ROUTES ===");

            int totalRoutes = 0;
            int totalSegments = 0;
            int successfulSegments = 0;
            int failedSegments = 0;

            var routeEntities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
            for (int r = 0; r < routeEntities.Length; r++)
            {
                var entity = routeEntities[r];
                totalRoutes++;

                if (r < 10)
                {
                    InspectRoute(entity);
                }

                if (EntityManager.HasBuffer<RouteSegment>(entity))
                {
                    var segBuf = EntityManager.GetBuffer<RouteSegment>(entity);
                    for (int s = 0; s < segBuf.Length; s++)
                    {
                        var segEnt = segBuf[s].m_Segment;
                        totalSegments++;
                        if (EntityManager.HasComponent<PathInformation>(segEnt))
                        {
                            var pi = EntityManager.GetComponentData<PathInformation>(segEnt);
                            if (pi.m_Distance > 0f)
                                successfulSegments++;
                            else
                                failedSegments++;
                        }
                        else
                        {
                            failedSegments++;
                        }
                    }
                }
            }

            _log.Info($"=== INSPECTION COMPLETE: Examined {totalRoutes} routes with {totalSegments} segments (Connected/Successful: {successfulSegments}, Failed: {failedSegments}) ===");
            _hasInspected = true;
        }

        private void InspectRoute(Entity routeEntity)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"\n--- Route Entity {routeEntity.Index}:{routeEntity.Version} ---");

            if (EntityManager.HasComponent<TransportLine>(routeEntity))
            {
                var line = EntityManager.GetComponentData<TransportLine>(routeEntity);
                sb.AppendLine($"TransportLine: Flags={line.m_Flags}");
            }

            if (EntityManager.HasComponent<Route>(routeEntity))
            {
                var route = EntityManager.GetComponentData<Route>(routeEntity);
                sb.AppendLine($"Route: Flags={route.m_Flags}");
            }

            if (EntityManager.HasComponent<RouteNumber>(routeEntity))
            {
                var num = EntityManager.GetComponentData<RouteNumber>(routeEntity);
                sb.AppendLine($"RouteNumber: {num.m_Number}");
            }

            if (EntityManager.HasComponent<Color>(routeEntity))
            {
                var color = EntityManager.GetComponentData<Color>(routeEntity);
                sb.AppendLine($"Color: {color.m_Color}");
            }

            // Inspect waypoints
            if (EntityManager.HasBuffer<RouteWaypoint>(routeEntity))
            {
                var waypoints = EntityManager.GetBuffer<RouteWaypoint>(routeEntity);
                sb.AppendLine($"\nRouteWaypoint buffer: {waypoints.Length} waypoints");
                for (int i = 0; i < waypoints.Length; i++)
                {
                    var wp = waypoints[i];
                    sb.AppendLine($"  [{i}] Entity={wp.m_Waypoint.Index}:{wp.m_Waypoint.Version}");
                    InspectWaypoint(wp.m_Waypoint, sb);
                }
            }

            // Inspect segments
            if (EntityManager.HasBuffer<RouteSegment>(routeEntity))
            {
                var segments = EntityManager.GetBuffer<RouteSegment>(routeEntity);
                sb.AppendLine($"\nRouteSegment buffer: {segments.Length} segments");
                for (int i = 0; i < segments.Length; i++)
                {
                    var seg = segments[i];
                    sb.AppendLine($"  [{i}] Entity={seg.m_Segment.Index}:{seg.m_Segment.Version}");
                    InspectSegment(seg.m_Segment, sb);
                }
            }

            _log.Info(sb.ToString());
        }

        private void InspectWaypoint(Entity waypointEntity, StringBuilder sb)
        {
            if (!EntityManager.Exists(waypointEntity))
            {
                sb.AppendLine("      (Waypoint entity does not exist)");
                return;
            }

            if (EntityManager.HasComponent<Position>(waypointEntity))
            {
                var pos = EntityManager.GetComponentData<Position>(waypointEntity);
                sb.AppendLine($"      Position: {pos.m_Position}");
            }

            if (EntityManager.HasComponent<Connected>(waypointEntity))
            {
                var conn = EntityManager.GetComponentData<Connected>(waypointEntity);
                sb.AppendLine($"      Connected Stop: {conn.m_Connected.Index}:{conn.m_Connected.Version}");

                if (conn.m_Connected != Entity.Null && EntityManager.Exists(conn.m_Connected))
                {
                    if (EntityManager.HasComponent<Game.Objects.Attached>(conn.m_Connected))
                    {
                        var att = EntityManager.GetComponentData<Game.Objects.Attached>(conn.m_Connected);
                        sb.AppendLine($"      Stop Attached: Parent={att.m_Parent.Index} CurvePos={att.m_CurvePosition:F4}");
                    }
                }
            }

            if (EntityManager.HasComponent<RouteLane>(waypointEntity))
            {
                var rl = EntityManager.GetComponentData<RouteLane>(waypointEntity);
                sb.AppendLine($"      RouteLane: StartLane={rl.m_StartLane.Index} EndLane={rl.m_EndLane.Index} StartCurve={rl.m_StartCurvePos:F2} EndCurve={rl.m_EndCurvePos:F2}");
            }
            else
            {
                sb.AppendLine("      RouteLane: (NOT PRESENT)");
            }

            if (EntityManager.HasComponent<AccessLane>(waypointEntity))
            {
                var al = EntityManager.GetComponentData<AccessLane>(waypointEntity);
                sb.AppendLine($"      AccessLane: Lane={al.m_Lane.Index} CurvePos={al.m_CurvePos:F2}");
            }
        }

        private void InspectSegment(Entity segmentEntity, StringBuilder sb)
        {
            if (!EntityManager.Exists(segmentEntity))
            {
                sb.AppendLine("      (Segment entity does not exist)");
                return;
            }

            if (EntityManager.HasComponent<PathTargets>(segmentEntity))
            {
                var pt = EntityManager.GetComponentData<PathTargets>(segmentEntity);
                sb.AppendLine($"      PathTargets: StartLane={pt.m_StartLane.Index} EndLane={pt.m_EndLane.Index} CurvePos={pt.m_CurvePositions} ReadyStart={pt.m_ReadyStartPosition} ReadyEnd={pt.m_ReadyEndPosition}");
            }

            if (EntityManager.HasComponent<PathInformation>(segmentEntity))
            {
                var pi = EntityManager.GetComponentData<PathInformation>(segmentEntity);
                sb.AppendLine($"      PathInformation: Distance={pi.m_Distance:F1}m Duration={pi.m_Duration:F1}s State={pi.m_State}");
            }

            if (EntityManager.HasBuffer<PathElement>(segmentEntity))
            {
                var pathBuf = EntityManager.GetBuffer<PathElement>(segmentEntity);
                sb.AppendLine($"      PathElement buffer: {pathBuf.Length} elements");
            }
            else
            {
                sb.AppendLine("      PathElement buffer: (NOT PRESENT)");
            }

            if (EntityManager.HasBuffer<CurveElement>(segmentEntity))
            {
                var curveBuf = EntityManager.GetBuffer<CurveElement>(segmentEntity);
                sb.AppendLine($"      CurveElement buffer: {curveBuf.Length} elements");
            }
        }
    }
}
