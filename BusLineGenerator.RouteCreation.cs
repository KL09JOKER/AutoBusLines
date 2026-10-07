using System;
using System.Collections.Generic;
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
    public partial class BusLineGenerator
    {
        // ==========================================================================
        // Route Colors & Palette Systems
        // ==========================================================================
        // High-visibility, vibrant modern transit palette designed for crisp clarity on 3D terrain and UI
        public static readonly string[] VibrantTransitColors = new string[]
        {
            "#007AFF", // Electric Metro Blue
            "#FF5500", // Bright Safety Orange
            "#00C853", // Vivid Emerald Green
            "#9D00FF", // Neon Violet / Purple
            "#FF0055", // Hot Raspberry / Crimson
            "#00C4D6", // Electric Cyan / Aqua
            "#FFB800", // Vivid Amber Gold
            "#FF1493", // Deep Pink / Fuchsia
            "#00E676", // Bright Spring Green
            "#536DFE", // Royal Indigo Blue
            "#FF3D00", // Flame Vermilion
            "#00B0FF", // Vivid Sky Blue
            "#AA00FF", // Bright Purple
            "#76FF03", // Vivid Lime Green
            "#FF6E40", // Vivid Tangerine
            "#1DE9B6", // Turquoise Teal
        };

        // Curated anchor hues for station hubs, ensuring distinct, vibrant color families
        private static readonly float[] s_StationAnchorHues = new float[]
        {
            0.58f, // Station 0: Electric Metro Blue
            0.38f, // Station 1: Vivid Emerald Green
            0.07f, // Station 2: Warm Tangelo Orange
            0.76f, // Station 3: Electric Violet / Purple
            0.98f, // Station 4: Bright Crimson Red
            0.50f, // Station 5: Bright Cyan / Aqua
            0.12f, // Station 6: Golden Amber
            0.88f, // Station 7: Hot Pink / Fuchsia
            0.24f, // Station 8: Vivid Lime Green
            0.67f, // Station 9: Royal Indigo
        };

        public static string GetLineHexColor(int lineNumber)
        {
            if (lineNumber >= 1 && lineNumber <= VibrantTransitColors.Length)
            {
                return VibrantTransitColors[lineNumber - 1];
            }
            float lineHue = ((lineNumber - 1) * 0.618033988749895f) % 1.0f;
            Color32 c = HSVToRGB(lineHue, 0.92f, 0.98f);
            return $"#{c.r:X2}{c.g:X2}{c.b:X2}";
        }

        public static Color32 HexToColor(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return new Color32(255, 0, 0, 255);
            hex = hex.TrimStart('#');
            if (hex.Length == 6 &&
                byte.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out byte r) &&
                byte.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out byte g) &&
                byte.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
            {
                return new Color32(r, g, b, 255);
            }
            return new Color32(255, 0, 0, 255);
        }

        public static List<string> ComputeTourColors(List<List<PlacedStop>> tours, LineColorMode mode)
        {
            var hexColors = new List<string>(tours.Count);

            if (mode == LineColorMode.Random)
            {
                for (int t = 0; t < tours.Count; t++)
                {
                    if (t < VibrantTransitColors.Length)
                    {
                        hexColors.Add(VibrantTransitColors[t]);
                    }
                    else
                    {
                        float lineHue = ((t * 0.618033988749895f) % 1.0f);
                        Color32 c = HSVToRGB(lineHue, 0.92f, 0.98f);
                        hexColors.Add($"#{c.r:X2}{c.g:X2}{c.b:X2}");
                    }
                }
                return hexColors;
            }

            // Per Station / Hub Mode:
            // 1. Routes connecting to an actual bus station or terminal share that station's color family.
            // 2. Regular street lines without a station hub are treated independently with unique vibrant transit colors.
            var hubToTours = new Dictionary<Entity, List<int>>();
            var distinctHubs = new List<Entity>();
            var nonHubTours = new List<int>();

            for (int t = 0; t < tours.Count; t++)
            {
                var tour = tours[t];
                Entity hub = Entity.Null;
                // Priority 1: Station platform bay
                for (int s = 0; s < tour.Count; s++)
                {
                    if (tour[s].IsStationBay && tour[s].HubEntity != Entity.Null)
                    {
                        hub = tour[s].HubEntity;
                        break;
                    }
                }
                // Priority 2: Any stop with HubEntity
                if (hub == Entity.Null)
                {
                    for (int s = 0; s < tour.Count; s++)
                    {
                        if (tour[s].HubEntity != Entity.Null)
                        {
                            hub = tour[s].HubEntity;
                            break;
                        }
                    }
                }

                if (hub != Entity.Null)
                {
                    if (!hubToTours.TryGetValue(hub, out var list))
                    {
                        list = new List<int>();
                        hubToTours[hub] = list;
                        distinctHubs.Add(hub);
                    }
                    list.Add(t);
                }
                else
                {
                    nonHubTours.Add(t);
                }
            }

            var tourColors = new string[tours.Count];

            // Assign cohesive, vibrant palettes to station hubs
            for (int h = 0; h < distinctHubs.Count; h++)
            {
                Entity hub = distinctHubs[h];
                var tourIndices = hubToTours[hub];
                int countInHub = tourIndices.Count;

                float hubBaseHue = s_StationAnchorHues[h % s_StationAnchorHues.Length];
                if (h >= s_StationAnchorHues.Length)
                {
                    hubBaseHue = (hubBaseHue + 0.618033988749895f * (h / s_StationAnchorHues.Length)) % 1.0f;
                }

                for (int k = 0; k < countInHub; k++)
                {
                    int tourIdx = tourIndices[k];
                    float hueOffset = 0f;
                    if (countInHub > 1)
                    {
                        float spread = math.min(0.12f, 0.035f * (countInHub - 1));
                        float tNorm = (float)k / (countInHub - 1);
                        hueOffset = (tNorm - 0.5f) * spread;
                    }

                    float lineHue = (hubBaseHue + hueOffset + 1.0f) % 1.0f;
                    // Maintain high saturation (0.88 - 0.96) and brightness (0.96 - 1.0) so lines never look dull
                    float saturation = (k % 2 == 0) ? 0.96f : 0.88f;
                    float value = ((k / 2) % 2 == 0) ? 1.0f : 0.96f;

                    Color32 c = HSVToRGB(lineHue, saturation, value);
                    tourColors[tourIdx] = $"#{c.r:X2}{c.g:X2}{c.b:X2}";
                }
            }

            // Assign distinct, punchy transit colors to street lines without a central station hub
            int colorOffset = distinctHubs.Count;
            for (int i = 0; i < nonHubTours.Count; i++)
            {
                int tourIdx = nonHubTours[i];
                int paletteIdx = colorOffset + i;
                if (paletteIdx < VibrantTransitColors.Length)
                {
                    tourColors[tourIdx] = VibrantTransitColors[paletteIdx];
                }
                else
                {
                    float lineHue = ((paletteIdx * 0.618033988749895f) % 1.0f);
                    Color32 c = HSVToRGB(lineHue, 0.92f, 0.98f);
                    tourColors[tourIdx] = $"#{c.r:X2}{c.g:X2}{c.b:X2}";
                }
            }

            hexColors.AddRange(tourColors);
            return hexColors;
        }

        public void UpdatePlanColors(LineColorMode mode)
        {
            if (_activePlan == null || _activePlan.Count == 0 || _plannedTours == null || _plannedTours.Count == 0)
                return;

            var tourColors = ComputeTourColors(_plannedTours, mode);
            for (int i = 0; i < _activePlan.Count && i < tourColors.Count; i++)
            {
                _activePlan[i].Color = tourColors[i];
            }

            AutoBusLinesUISystem.Instance?.UpdatePlan(_activePlan, _currentPlanSeed);
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

        // ==========================================================================
        // Maintenance, Deletion & Nudging Systems
        // ==========================================================================
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

                                if (roadOwner != Entity.Null && EntityManager.Exists(roadOwner) && EntityManager.HasComponent<Edge>(roadOwner))
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

        /// <summary>
        /// Scans all active bus transit lines in the city and returns the set of all stop entities
        /// that are currently served by at least one valid, active bus route.
        /// </summary>
        private HashSet<Entity> GetStopsCoveredByActiveBusLines(out int activeLineCount)
        {
            var coveredStops = new HashSet<Entity>();
            activeLineCount = 0;

            var lineQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Routes.TransportLine>(),
                ComponentType.ReadOnly<Game.Routes.Route>(),
                ComponentType.Exclude<Deleted>()
            );

            if (lineQuery.IsEmptyIgnoreFilter)
                return coveredStops;

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

                    if (!EntityManager.HasBuffer<RouteWaypoint>(routeEntity))
                        continue;

                    var waypoints = EntityManager.GetBuffer<RouteWaypoint>(routeEntity);
                    if (waypoints.Length < 2)
                        continue;

                    activeLineCount++;

                    for (int w = 0; w < waypoints.Length; w++)
                    {
                        var wpEntity = waypoints[w].m_Waypoint;
                        if (!EntityManager.Exists(wpEntity) || EntityManager.HasComponent<Deleted>(wpEntity))
                            continue;

                        if (EntityManager.HasComponent<Game.Routes.Connected>(wpEntity))
                        {
                            var stopEntity = EntityManager.GetComponentData<Game.Routes.Connected>(wpEntity).m_Connected;
                            if (EntityManager.Exists(stopEntity) && !EntityManager.HasComponent<Deleted>(stopEntity))
                            {
                                coveredStops.Add(stopEntity);
                            }
                        }
                    }
                }
            }

            return coveredStops;
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

            if (IsDrawbridge(roadEntity))
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

            // Clean up any legacy DeadEnd warnings on parent road
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

            // Clean up any incorrect SubObject buffer entries on roadEntity
            if (EntityManager.HasBuffer<Game.Objects.SubObject>(roadEntity))
            {
                var subObjs = EntityManager.GetBuffer<Game.Objects.SubObject>(roadEntity);
                for (int so = subObjs.Length - 1; so >= 0; so--)
                {
                    var sub = subObjs[so].m_SubObject;
                    if (EntityManager.Exists(sub) && (EntityManager.HasComponent<Game.Routes.BusStop>(sub) || EntityManager.HasComponent<Game.Routes.TransportStop>(sub)))
                    {
                        subObjs.RemoveAt(so);
                    }
                }
            }

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

        // ==========================================================================
        // Plan Building & Route Instantiation Systems
        // ==========================================================================
        private void ExecuteBuildSelectedPlan()
        {
            if (_activePlan == null || _activePlan.Count == 0)
            {
                log.Warn("ExecuteBuildSelectedPlan: No active transit plan to build.");
                AutoBusLinesUISystem.Instance?.SetPlanStatus("idle", "No active transit plan to build.");
                return;
            }

            if (!FindBusLinePrefab(out Entity busLinePrefabEntity, out RouteData busLineRouteData))
            {
                log.Error("Could not find a valid Bus Line Prefab with RouteData archetypes!");
                AutoBusLinesUISystem.Instance?.SetPlanStatus("idle", "Error: Bus line prefab not found.");
                return;
            }

            var virtualToRealEntityMap = new Dictionary<int, Entity>();
            var toursToBuild = new List<List<PlacedStop>>();
            _plannedTourColors = new List<Color32>();
            int totalNewStopsBuilt = 0;

            for (int r = 0; r < _activePlan.Count; r++)
            {
                var route = _activePlan[r];
                if (!route.Enabled)
                    continue;

                var enabledStops = route.Stops.FindAll(s => s.Enabled);
                if (enabledStops.Count < ABSOLUTE_MIN_STOPS)
                {
                    log.Warn($"Skipping Line #{route.Id}: only {enabledStops.Count} stops enabled (minimum {ABSOLUTE_MIN_STOPS} required).");
                    continue;
                }

                var tourStops = new List<PlacedStop>();
                for (int sIdx = 0; sIdx < enabledStops.Count; sIdx++)
                {
                    var stopData = enabledStops[sIdx];
                    var st = stopData.InternalStop;

                    if (st.StopEntity.Index < 0)
                    {
                        // Issue #2: verify road entity still exists before attempting to place stop
                        if (!EntityManager.Exists(st.RoadEntity) || EntityManager.HasComponent<Deleted>(st.RoadEntity))
                        {
                            log.Warn($"Skipping virtual stop on road {st.RoadEntity.Index} for route #{route.Id}: road entity was deleted or demolished.");
                            continue;
                        }

                        // Virtual stop that needs to be created in ECS
                        if (!virtualToRealEntityMap.TryGetValue(st.StopEntity.Index, out Entity realStopEntity))
                        {
                            if (TryCreateBusStopEntity(st.RoadEntity, st.RoadT, st.Position, st.Forward, st.HubEntity,
                                                       st.CorridorIndex, st.SpanOrderInCorridor, st.IsOutbound,
                                                       st.PrefabEntity, out PlacedStop realPlacedStop))
                            {
                                realStopEntity = realPlacedStop.StopEntity;
                                virtualToRealEntityMap[st.StopEntity.Index] = realStopEntity;
                                st.StopEntity = realStopEntity;
                                totalNewStopsBuilt++;

                                if (!string.IsNullOrEmpty(stopData.Name))
                                {
                                    if (_nameSystem == null)
                                        _nameSystem = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
                                    _nameSystem?.SetCustomName(realStopEntity, stopData.Name);
                                }
                            }
                            else
                            {
                                log.Error($"Failed to instantiate bus stop on road {st.RoadEntity.Index} for route #{route.Id}.");
                                continue;
                            }
                        }
                        else
                        {
                            st.StopEntity = realStopEntity;
                        }
                    }
                    else
                    {
                        // Issue #2: verify pre-existing stop entity still exists in ECS
                        if (!EntityManager.Exists(st.StopEntity) || EntityManager.HasComponent<Deleted>(st.StopEntity))
                        {
                            log.Warn($"Skipping pre-existing stop {st.StopEntity.Index} for route #{route.Id}: entity no longer exists or was demolished.");
                            continue;
                        }
                    }
                    tourStops.Add(st);
                }

                // Issue #3 & #4: Associate colors 1:1 with toursToBuild at the exact moment of adding tour
                if (tourStops.Count >= ABSOLUTE_MIN_STOPS)
                {
                    toursToBuild.Add(tourStops);
                    _plannedTourColors.Add(HexToColor(route.Color));
                }
                else
                {
                    log.Warn($"Route #{route.Id} dropped: only {tourStops.Count} valid stops remained after pruning deleted entities (minimum {ABSOLUTE_MIN_STOPS} required).");
                }
            }

            if (toursToBuild.Count == 0)
            {
                log.Warn("No valid bus lines remained after filtering disabled stops and routes.");
                AutoBusLinesUISystem.Instance?.SetPlanStatus("idle", "No valid bus lines remained to build.");
                return;
            }

            log.Info($"Building Selected Plan: Instantiating {toursToBuild.Count} bus lines with {totalNewStopsBuilt} newly created bus stops in ECS...");

            _plannedTours = toursToBuild;
            _cachedBusLinePrefabEntity = busLinePrefabEntity;
            _cachedBusLineRouteData = busLineRouteData;
            _totalStopsPlaced = totalNewStopsBuilt;
            _totalStopsActive = totalNewStopsBuilt;
            _currentTourIndex = 0;
            _createdLineCount = 0;
            _waitFrameCounter = 0;
            _activePlan = null;
            _currentPlanSeed = 0;

            _generationStage = GenerationStage.WaitingForStopIndexing;
            AutoBusLinesUISystem.Instance?.SetPlanStatus("building", $"Building {toursToBuild.Count} selected bus lines in city...");
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

            var segments = (_roadScanner != null && _roadScanner.DrivableSegments.IsCreated && _roadScanner.DrivableSegments.Length > 0)
                ? _roadScanner.DrivableSegments
                : _roadScanner.RoadSegments;

            for (int i = 0; i < segments.Length; i++)
            {
                var roadEnt = segments[i];
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

        private void CreateBusLine(Entity busLinePrefabEntity, RouteData routeData, List<PlacedStop> orderedStops, int lineNumber, int hubIndex = 0, int tourIndex = 0, Color32? customColor = null)
        {
            if (orderedStops == null || orderedStops.Count == 0)
                return;

            // Issue #2: Prune any stops that may have been demolished between planning and building
            orderedStops.RemoveAll(s => !EntityManager.Exists(s.StopEntity) || EntityManager.HasComponent<Deleted>(s.StopEntity));
            if (orderedStops.Count < ABSOLUTE_MIN_STOPS)
            {
                log.Warn($"CreateBusLine: Line #{lineNumber} aborted because fewer than {ABSOLUTE_MIN_STOPS} valid stops exist ({orderedStops.Count} found).");
                return;
            }

            // 1. Create the Route entity (TransportLine)
            Entity routeEntity = EntityManager.CreateEntity(routeData.m_RouteArchetype);

            EntityManager.SetComponentData(routeEntity, new PrefabRef(busLinePrefabEntity));
            EntityManager.SetComponentData(routeEntity, new Game.Routes.Route { m_Flags = RouteFlags.Complete });
            EntityManager.SetComponentData(routeEntity, new Game.Routes.TransportLine());
            EntityManager.SetComponentData(routeEntity, new RouteNumber { m_Number = lineNumber });

            Color32 lineColor = customColor ?? HexToColor(GetLineHexColor(lineNumber));
            EntityManager.SetComponentData(routeEntity, new Game.Routes.Color(lineColor));
            EntityManager.SetComponentData(routeEntity, new RouteBufferIndex { m_Index = -1 });

            var waypointEntities = new List<Entity>(orderedStops.Count);

            // Assign custom names to curbside stops if not already set
            var stopStreetNames = new string[orderedStops.Count];
            var streetCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int s = 0; s < orderedStops.Count; s++)
            {
                var st = orderedStops[s];
                if (!st.IsStationBay)
                {
                    string street = GetRoadStreetName(st.RoadEntity) ?? "Road";
                    stopStreetNames[s] = street;
                    streetCounts[street] = streetCounts.TryGetValue(street, out int c) ? c + 1 : 1;
                }
            }

            var streetIndices = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < orderedStops.Count; i++)
            {
                var stop = orderedStops[i];
                if (!stop.IsStationBay && EntityManager.Exists(stop.StopEntity) && !EntityManager.HasComponent<Deleted>(stop.StopEntity))
                {
                    string street = stopStreetNames[i] ?? "Road";
                    int totalOnStreet = streetCounts[street];
                    int currentIndex = streetIndices.TryGetValue(street, out int cur) ? cur + 1 : 1;
                    streetIndices[street] = currentIndex;

                    if (!EntityManager.HasComponent<Game.UI.CustomName>(stop.StopEntity))
                    {
                        string prefix = stop.IsPreExisting ? "Stop" : "New Stop";
                        string stopName = (totalOnStreet > 1)
                            ? $"{prefix} {street} {currentIndex}"
                            : $"{prefix} {street}";

                        if (_nameSystem == null)
                            _nameSystem = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();
                        _nameSystem?.SetCustomName(stop.StopEntity, stopName);
                    }
                }
            }

            // 2. Create connected Waypoints referencing placed stops
            for (int i = 0; i < orderedStops.Count; i++)
            {
                var stop = orderedStops[i];
                if (!EntityManager.Exists(stop.StopEntity) || EntityManager.HasComponent<Deleted>(stop.StopEntity))
                    continue;

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

            // 4. Fill the route's buffers last.
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
    }
}
