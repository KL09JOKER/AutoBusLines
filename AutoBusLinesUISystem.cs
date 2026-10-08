using System;
using System.Collections.Generic;
using Colossal.Logging;
using Colossal.UI.Binding;
using Game.Areas;
using Game.Rendering;
using Game.UI;
using Newtonsoft.Json;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace AutoBusLines
{
    public partial class AutoBusLinesUISystem : UISystemBase
    {
        private static new readonly ILog log = LogManager.GetLogger($"{nameof(AutoBusLines)}.{nameof(AutoBusLinesUISystem)}");

        public const string kGroup = "autoBusLines";

        private ValueBinding<bool> m_PanelVisible;
        private ValueBinding<string> m_PlanStatus; // "idle", "planning", "ready", "building"
        private ValueBinding<string> m_PlanJson;
        private ValueBinding<string> m_StatusMessage;
        private ValueBinding<int> m_PlanSeed;
        private ValueBinding<string> m_LineColorMode;

        // District & Stops-only bindings
        private EntityQuery m_DistrictQuery;
        private ValueBinding<string> m_DistrictsList;
        private ValueBinding<int> m_SelectedDistrict;
        private ValueBinding<string> m_StopsOnlyStatus;

        // Settings bindings
        private ValueBinding<bool> m_EnablePlanMode;
        private ValueBinding<bool> m_ExcludeDeadEnds;
        private ValueBinding<int> m_DeadEndDistanceThreshold;
        private ValueBinding<int> m_MinStopsPerLine;
        private ValueBinding<int> m_MaxStopsPerLine;
        private ValueBinding<int> m_MaxRouteLength;
        private ValueBinding<string> m_StopDensity;
        private ValueBinding<int> m_TargetStopSpacing;
        private ValueBinding<string> m_SelectedStopPrefab;
        private ValueBinding<string> m_StopPrefabOptions; // JSON array of names

        public static AutoBusLinesUISystem Instance { get; private set; }

        protected override void OnCreate()
        {
            base.OnCreate();
            Instance = this;

            AddBinding(m_PanelVisible = new ValueBinding<bool>(kGroup, "panelVisible", false));
            AddBinding(m_PlanStatus = new ValueBinding<string>(kGroup, "planStatus", "idle"));
            AddBinding(m_PlanJson = new ValueBinding<string>(kGroup, "planJson", "[]"));
            AddBinding(m_StatusMessage = new ValueBinding<string>(kGroup, "statusMessage", ""));
            AddBinding(m_PlanSeed = new ValueBinding<int>(kGroup, "planSeed", 0));
            AddBinding(m_LineColorMode = new ValueBinding<string>(kGroup, "lineColorMode", Mod.setting?.LineColoring == LineColorMode.Random ? "random" : "perStation"));

            // Settings bindings
            var s = Mod.setting;
            AddBinding(m_EnablePlanMode = new ValueBinding<bool>(kGroup, "enablePlanMode", s?.EnablePlanMode ?? true));
            AddBinding(m_ExcludeDeadEnds = new ValueBinding<bool>(kGroup, "excludeDeadEnds", s?.ExcludeDeadEnds ?? true));
            AddBinding(m_DeadEndDistanceThreshold = new ValueBinding<int>(kGroup, "deadEndDistanceThreshold", s?.DeadEndDistanceThreshold ?? 300));
            AddBinding(m_MinStopsPerLine = new ValueBinding<int>(kGroup, "minStopsPerLine", s?.MinStopsPerLine ?? 6));
            AddBinding(m_MaxStopsPerLine = new ValueBinding<int>(kGroup, "maxStopsPerLine", s?.MaxStopsPerLine ?? 18));
            AddBinding(m_MaxRouteLength = new ValueBinding<int>(kGroup, "maxRouteLength", s?.MaxRouteLength ?? 15000));
            AddBinding(m_StopDensity = new ValueBinding<string>(kGroup, "stopDensity", (s?.StopDensity ?? StopDensityMode.Balanced).ToString()));
            AddBinding(m_TargetStopSpacing = new ValueBinding<int>(kGroup, "targetStopSpacing", s?.TargetStopSpacing ?? 200));
            AddBinding(m_SelectedStopPrefab = new ValueBinding<string>(kGroup, "selectedStopPrefab", s?.SelectedStopPrefab ?? "All"));
            AddBinding(m_StopPrefabOptions = new ValueBinding<string>(kGroup, "stopPrefabOptions", "[]"));

            AddBinding(new TriggerBinding(kGroup, "togglePanel", OnTogglePanel));
            AddBinding(new TriggerBinding(kGroup, "openPanel", OnOpenPanel));
            AddBinding(new TriggerBinding(kGroup, "closePanel", OnClosePanel));
            AddBinding(new TriggerBinding(kGroup, "generatePlan", OnGeneratePlan));
            AddBinding(new TriggerBinding(kGroup, "newPlan", OnNewPlan));
            AddBinding(new TriggerBinding(kGroup, "discardPlan", OnDiscardPlan));
            AddBinding(new TriggerBinding(kGroup, "buildSelected", OnBuildSelected));
            AddBinding(new TriggerBinding<int, bool>(kGroup, "toggleRoute", OnToggleRoute));
            AddBinding(new TriggerBinding<int, int, bool>(kGroup, "toggleStop", OnToggleStop));
            AddBinding(new TriggerBinding<float, float, float>(kGroup, "focusStop", OnFocusStop));
            AddBinding(new TriggerBinding<string>(kGroup, "setColorMode", OnSetColorMode));
            AddBinding(new TriggerBinding<int>(kGroup, "hoverRoute", OnHoverRoute));
            AddBinding(new TriggerBinding<int, int>(kGroup, "hoverStop", OnHoverStop));

            // Settings trigger bindings
            AddBinding(new TriggerBinding<bool>(kGroup, "setEnablePlanMode", OnSetEnablePlanMode));
            AddBinding(new TriggerBinding<bool>(kGroup, "setExcludeDeadEnds", OnSetExcludeDeadEnds));
            AddBinding(new TriggerBinding<int>(kGroup, "setDeadEndThreshold", OnSetDeadEndThreshold));
            AddBinding(new TriggerBinding<int>(kGroup, "setMinStops", OnSetMinStops));
            AddBinding(new TriggerBinding<int>(kGroup, "setMaxStops", OnSetMaxStops));
            AddBinding(new TriggerBinding<int>(kGroup, "setMaxRouteLength", OnSetMaxRouteLength));
            AddBinding(new TriggerBinding<string>(kGroup, "setStopDensity", OnSetStopDensity));
            AddBinding(new TriggerBinding<int>(kGroup, "setTargetSpacing", OnSetTargetSpacing));
            AddBinding(new TriggerBinding<string>(kGroup, "setStopPrefab", OnSetStopPrefab));
            AddBinding(new TriggerBinding(kGroup, "triggerScan", OnTriggerScan));
            AddBinding(new TriggerBinding(kGroup, "repairRoutes", OnRepairRoutes));
            AddBinding(new TriggerBinding(kGroup, "deleteAll", OnDeleteAll));

            // District & Stops-only bindings and triggers
            m_DistrictQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<Game.Areas.District>(),
                    ComponentType.ReadOnly<Game.Areas.Area>(),
                },
                None = new ComponentType[]
                {
                    ComponentType.ReadOnly<Game.Common.Deleted>(),
                    ComponentType.ReadOnly<Game.Tools.Temp>(),
                }
            });

            AddBinding(m_DistrictsList = new ValueBinding<string>(kGroup, "districtsList", "[]"));
            AddBinding(m_SelectedDistrict = new ValueBinding<int>(kGroup, "selectedDistrict", 0));
            AddBinding(m_StopsOnlyStatus = new ValueBinding<string>(kGroup, "stopsOnlyStatus", ""));

            AddBinding(new TriggerBinding<int>(kGroup, "setSelectedDistrict", OnSetSelectedDistrict));
            AddBinding(new TriggerBinding(kGroup, "placeStopsOnly", OnPlaceStopsOnly));
            AddBinding(new TriggerBinding(kGroup, "clearUnusedStops", OnClearUnusedStops));

            log.Info("AutoBusLinesUISystem created and UI bindings registered successfully!");
            UpdateStopPrefabOptions();
            UpdateDistrictsList();
        }

        protected override void OnGameLoaded(Colossal.Serialization.Entities.Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);
            m_PlanStatus.Update("idle");
            m_PlanJson.Update("[]");
            m_StatusMessage.Update("");
            m_PlanSeed.Update(0);
            m_PanelVisible.Update(false);
            m_StopsOnlyStatus.Update("");
            m_SelectedDistrict.Update(0);
            HoveredRouteId = 0;
            HoveredStopIndex = -1;
            PlanRouteOverlaySystem.ClearCache();

            var generator = World.GetExistingSystemManaged<BusLineGenerator>();
            generator?.UpdateDiscoveredPrefabs();
            UpdateStopPrefabOptions();
            UpdateDistrictsList();
        }

        public static int HoveredRouteId { get; set; } = 0;
        public static int HoveredStopIndex { get; set; } = -1;

        public void OpenPanel()
        {
            SyncSettingsToUI();
            var generator = World.GetExistingSystemManaged<BusLineGenerator>();
            generator?.UpdateDiscoveredPrefabs();
            UpdateStopPrefabOptions();
            UpdateDistrictsList();
            m_PanelVisible.Update(true);
        }

        public void ClosePanel()
        {
            m_PanelVisible.Update(false);
            HoveredRouteId = 0;
            HoveredStopIndex = -1;
        }

        public void SetPlanStatus(string status, string message = null)
        {
            m_PlanStatus.Update(status);
            if (message != null)
            {
                m_StatusMessage.Update(message);
            }
        }

        public void UpdatePlan(List<PlannedRoute> routes, int seed, string message = null)
        {
            PlanRouteOverlaySystem.ClearCache();
            string json = (routes != null) ? JsonConvert.SerializeObject(routes, Formatting.None) : "[]";
            m_PlanJson.Update(json);
            m_PlanSeed.Update(seed);
            m_PlanStatus.Update((routes != null && routes.Count > 0) ? "ready" : "idle");
            if (message != null)
            {
                m_StatusMessage.Update(message);
            }
        }

        public void SetColorMode(string mode)
        {
            m_LineColorMode?.Update(mode);
            PlanRouteOverlaySystem.ClearCache();
        }

        private void OnSetColorMode(string mode)
        {
            LineColorMode colorMode = (mode == "random") ? LineColorMode.Random : LineColorMode.PerStation;
            if (Mod.setting != null)
            {
                Mod.setting.LineColoring = colorMode;
                Mod.setting.Apply();
            }
            SetColorMode(mode);

            var generator = World.GetExistingSystemManaged<BusLineGenerator>();
            generator?.UpdatePlanColors(colorMode);
            PlanRouteOverlaySystem.ClearCache();
        }

        private void OnTogglePanel()
        {
            bool nextState = !m_PanelVisible.value;
            if (nextState)
            {
                SyncSettingsToUI();
                var generator = World.GetExistingSystemManaged<BusLineGenerator>();
                generator?.UpdateDiscoveredPrefabs();
                UpdateStopPrefabOptions();
                UpdateDistrictsList();
            }
            else
            {
                HoveredRouteId = 0;
                HoveredStopIndex = -1;
            }
            m_PanelVisible.Update(nextState);
            log.Info($"Toggle transit planner panel: {nextState}");
        }

        private void OnOpenPanel()
        {
            SyncSettingsToUI();
            var generator = World.GetExistingSystemManaged<BusLineGenerator>();
            generator?.UpdateDiscoveredPrefabs();
            UpdateStopPrefabOptions();
            UpdateDistrictsList();
            m_PanelVisible.Update(true);
        }

        private void OnClosePanel()
        {
            m_PanelVisible.Update(false);
            HoveredRouteId = 0;
            HoveredStopIndex = -1;
        }

        private void OnGeneratePlan()
        {
            HoveredRouteId = 0;
            HoveredStopIndex = -1;
            PlanRouteOverlaySystem.ClearCache();
            var generator = World.GetExistingSystemManaged<BusLineGenerator>();
            if (generator != null)
            {
                generator.TargetDistrictId = m_SelectedDistrict.value;
                SetPlanStatus("planning", "Analyzing road network and calculating transit corridors...");
                m_PlanSeed.Update(0);
                generator.RequestPlan(0);
            }
        }

        private void OnNewPlan()
        {
            HoveredRouteId = 0;
            HoveredStopIndex = -1;
            PlanRouteOverlaySystem.ClearCache();
            var generator = World.GetExistingSystemManaged<BusLineGenerator>();
            if (generator != null)
            {
                generator.TargetDistrictId = m_SelectedDistrict.value;
                int nextSeed = m_PlanSeed.value + 1;
                m_PlanSeed.Update(nextSeed);
                SetPlanStatus("planning", $"Calculating alternative transit network (Variant #{nextSeed + 1})...");
                generator.RequestPlan(nextSeed);
            }
        }

        private void OnDiscardPlan()
        {
            HoveredRouteId = 0;
            HoveredStopIndex = -1;
            PlanRouteOverlaySystem.ClearCache();
            var generator = World.GetExistingSystemManaged<BusLineGenerator>();
            if (generator != null)
            {
                generator.RequestDiscardPlan();
            }
            m_PlanSeed.Update(0);
            m_PlanJson.Update("[]");
            SetPlanStatus("idle", "Transit plan discarded. No lines or stops were placed.");
        }

        private void OnBuildSelected()
        {
            HoveredRouteId = 0;
            HoveredStopIndex = -1;
            PlanRouteOverlaySystem.ClearCache();
            var generator = World.GetExistingSystemManaged<BusLineGenerator>();
            if (generator != null)
            {
                SetPlanStatus("building", "Instantiating bus stops and building selected transit lines...");
                generator.RequestBuildSelectedPlan();
            }
        }

        private void OnToggleRoute(int routeId, bool enabled)
        {
            PlanRouteOverlaySystem.ClearCache();
            var generator = World.GetExistingSystemManaged<BusLineGenerator>();
            if (generator != null)
            {
                generator.ToggleRoute(routeId, enabled);
            }
        }

        private void OnToggleStop(int routeId, int stopIndex, bool enabled)
        {
            PlanRouteOverlaySystem.ClearCache();
            var generator = World.GetExistingSystemManaged<BusLineGenerator>();
            if (generator != null)
            {
                generator.ToggleStop(routeId, stopIndex, enabled);
            }
        }

        private void OnHoverRoute(int routeId)
        {
            HoveredRouteId = routeId;
            if (routeId <= 0)
            {
                HoveredStopIndex = -1;
            }
        }

        private void OnHoverStop(int routeId, int stopIndex)
        {
            HoveredRouteId = routeId;
            HoveredStopIndex = stopIndex;
        }

        private void OnFocusStop(float x, float y, float z)
        {
            try
            {
                var cameraSystem = World.GetExistingSystemManaged<CameraUpdateSystem>();
                if (cameraSystem != null && cameraSystem.activeCameraController != null)
                {
                    cameraSystem.activeCameraController.pivot = new float3(x, y, z);
                }
            }
            catch (Exception ex)
            {
                log.Warn($"FocusCameraOn failed: {ex.Message}");
            }
        }

        // ---- Settings handlers ----

        private void OnSetEnablePlanMode(bool value)
        {
            if (Mod.setting != null)
            {
                Mod.setting.EnablePlanMode = value;
                Mod.setting.Apply();
            }
            m_EnablePlanMode.Update(value);
        }

        private void OnSetExcludeDeadEnds(bool value)
        {
            if (Mod.setting != null)
            {
                Mod.setting.ExcludeDeadEnds = value;
                Mod.setting.Apply();
            }
            m_ExcludeDeadEnds.Update(value);
            log.Info($"Set ExcludeDeadEnds: {value}");
        }

        private void OnSetDeadEndThreshold(int value)
        {
            value = Math.Max(50, Math.Min(2000, value));
            if (Mod.setting != null)
            {
                Mod.setting.DeadEndDistanceThreshold = value;
                Mod.setting.Apply();
            }
            m_DeadEndDistanceThreshold.Update(value);
            log.Info($"Set DeadEndDistanceThreshold: {value}m");
        }

        private void OnSetMinStops(int value)
        {
            value = Math.Max(BusLineGenerator.ABSOLUTE_MIN_STOPS, Math.Min(30, value));
            if (Mod.setting != null)
            {
                Mod.setting.MinStopsPerLine = value;
                Mod.setting.Apply();
            }
            m_MinStopsPerLine.Update(value);
        }

        private void OnSetMaxStops(int value)
        {
            value = Math.Max(4, Math.Min(50, value));
            if (Mod.setting != null)
            {
                Mod.setting.MaxStopsPerLine = value;
                Mod.setting.Apply();
            }
            m_MaxStopsPerLine.Update(value);
        }

        private void OnSetMaxRouteLength(int value)
        {
            value = Math.Max(2000, Math.Min(50000, value));
            if (Mod.setting != null)
            {
                Mod.setting.MaxRouteLength = value;
                Mod.setting.Apply();
            }
            m_MaxRouteLength.Update(value);
        }

        private void OnSetStopDensity(string mode)
        {
            if (Mod.setting != null && Enum.TryParse<StopDensityMode>(mode, true, out var parsed))
            {
                Mod.setting.StopDensity = parsed;
                Mod.setting.Apply();
                m_StopDensity.Update(parsed.ToString());
                m_TargetStopSpacing.Update(Mod.setting.TargetStopSpacing);
            }
        }

        private void OnSetTargetSpacing(int value)
        {
            value = Math.Max(60, Math.Min(500, value));
            if (Mod.setting != null)
            {
                Mod.setting.TargetStopSpacing = value;
                Mod.setting.Apply();
            }
            m_TargetStopSpacing.Update(value);
            // Update density mode to reflect Custom if it changed
            m_StopDensity.Update(Mod.setting?.StopDensity.ToString() ?? "Balanced");
        }

        private void OnSetStopPrefab(string prefabName)
        {
            if (Mod.setting != null)
            {
                Mod.setting.SelectedStopPrefab = prefabName;
                Mod.setting.Apply();
            }
            m_SelectedStopPrefab.Update(prefabName);
        }

        private void OnTriggerScan()
        {
            log.Info("Manual scan triggered via Plan Panel UI.");
            var generator = World.GetExistingSystemManaged<BusLineGenerator>();
            if (generator != null)
            {
                generator.RequestGeneration();
                log.Info("Regeneration requested.");
            }
        }

        private void OnRepairRoutes()
        {
            log.Info("Manual route repair triggered via Plan Panel UI.");
            var generator = World.GetExistingSystemManaged<BusLineGenerator>();
            if (generator != null)
            {
                generator.RequestRepair();
                log.Info("Route repair requested.");
            }
        }

        private void OnDeleteAll()
        {
            log.Info("Delete all bus lines and stops triggered via Plan Panel UI.");
            var generator = World.GetExistingSystemManaged<BusLineGenerator>();
            if (generator != null)
            {
                generator.DeleteAllBusLinesAndStops();
                log.Info("Deletion complete.");
            }
        }

        /// <summary>
        /// Push the discovered stop prefab list with icons to the UI.
        /// Call this after stop prefabs have been discovered or on UI load.
        /// </summary>
        public void UpdateStopPrefabOptions()
        {
            if (Setting.DiscoveredStopPrefabNames.Count == 0)
            {
                var generator = World.GetExistingSystemManaged<BusLineGenerator>();
                generator?.UpdateDiscoveredPrefabs();

                if (Setting.DiscoveredStopPrefabNames.Count == 0)
                {
                    Setting.DiscoveredStopPrefabNames.AddRange(new[]
                    {
                        "EU_BusStop01",
                        "EU_BusStop02",
                        "NA_BusStop01",
                        "NA_BusStop02",
                        "EU_BusStopBicycle01",
                        "NA_BusStopBicycle01",
                        "Pack7-BusStop01"
                    });
                }
            }

            var list = new List<object>();

            foreach (var name in Setting.DiscoveredStopPrefabNames)
            {
                string icon = "Media/Game/Icons/BusStop.svg";
                if (Setting.DiscoveredStopPrefabIcons.TryGetValue(name, out var customIcon) && !string.IsNullOrEmpty(customIcon))
                {
                    icon = customIcon;
                }
                list.Add(new { name = name, icon = icon });
            }

            var optionsJson = JsonConvert.SerializeObject(list);
            m_StopPrefabOptions?.Update(optionsJson);
        }

        public void UpdateDistrictsList()
        {
            var list = new List<object>();
            list.Add(new { id = 0, name = "All City" });

            if (!m_DistrictQuery.IsEmptyIgnoreFilter)
            {
                var entities = m_DistrictQuery.ToEntityArray(Allocator.Temp);
                var nameSystem = World.GetOrCreateSystemManaged<Game.UI.NameSystem>();

                for (int i = 0; i < entities.Length; i++)
                {
                    var e = entities[i];
                    string dName = nameSystem?.GetRenderedLabelName(e);
                    if (string.IsNullOrWhiteSpace(dName))
                    {
                        dName = $"District {e.Index}";
                    }
                    list.Add(new { id = e.Index, name = dName });
                }
                entities.Dispose();
            }

            string json = JsonConvert.SerializeObject(list);
            m_DistrictsList?.Update(json);
        }

        private void OnSetSelectedDistrict(int districtId)
        {
            m_SelectedDistrict?.Update(districtId);
            var generator = World.GetExistingSystemManaged<BusLineGenerator>();
            if (generator != null)
            {
                generator.TargetDistrictId = districtId;
            }
            log.Info($"Selected District filter changed to ID: {districtId}");
        }

        private void OnPlaceStopsOnly()
        {
            var generator = World.GetExistingSystemManaged<BusLineGenerator>();
            if (generator == null) return;

            int distId = m_SelectedDistrict.value;
            Entity districtEntity = generator.FindDistrictEntity(distId);

            var s = Mod.setting;
            string stopModel = s?.SelectedStopPrefab ?? "All";
            StopDensityMode density = s?.StopDensity ?? StopDensityMode.Balanced;
            int spacing = s?.TargetStopSpacing ?? 200;

            m_StopsOnlyStatus?.Update("Analyzing road spans and placing roadside bus stops...");
            var result = generator.PlaceStopsOnly(districtEntity, stopModel, density, spacing);
            string msg = $"Successfully placed {result.placed} bus stops in {result.districtName}! ({result.reused} pre-existing stops preserved)";
            m_StopsOnlyStatus?.Update(msg);
            log.Info(msg);
        }

        private void OnClearUnusedStops()
        {
            var generator = World.GetExistingSystemManaged<BusLineGenerator>();
            if (generator == null) return;

            int distId = m_SelectedDistrict.value;
            Entity districtEntity = generator.FindDistrictEntity(distId);

            m_StopsOnlyStatus?.Update("Scanning for unused bus stops...");
            var result = generator.ClearUnusedStops(districtEntity);
            string msg = $"Removed {result.deletedCount} unused roadside bus stops in {result.districtName}.";
            m_StopsOnlyStatus?.Update(msg);
            log.Info(msg);
        }

        /// <summary>
        /// Sync all settings bindings from the current Mod.setting values.
        /// </summary>
        public void SyncSettingsToUI()
        {
            var s = Mod.setting;
            if (s == null) return;

            if (s.SelectedStopPrefab != null && s.SelectedStopPrefab.IndexOf("Integrated", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                s.SelectedStopPrefab = "All";
                s.Apply();
            }

            m_EnablePlanMode?.Update(s.EnablePlanMode);
            m_ExcludeDeadEnds?.Update(s.ExcludeDeadEnds);
            m_DeadEndDistanceThreshold?.Update(s.DeadEndDistanceThreshold);
            m_MinStopsPerLine?.Update(s.MinStopsPerLine);
            m_MaxStopsPerLine?.Update(s.MaxStopsPerLine);
            m_MaxRouteLength?.Update(s.MaxRouteLength);
            m_StopDensity?.Update(s.StopDensity.ToString());
            m_TargetStopSpacing?.Update(s.TargetStopSpacing);
            m_SelectedStopPrefab?.Update(s.SelectedStopPrefab);
            m_LineColorMode?.Update(s.LineColoring == LineColorMode.Random ? "random" : "perStation");
        }

        protected override void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            base.OnDestroy();
        }
    }
}
