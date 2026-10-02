using System;
using System.Collections.Generic;
using Colossal.Logging;
using Colossal.UI.Binding;
using Game.Rendering;
using Game.UI;
using Newtonsoft.Json;
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

            log.Info("AutoBusLinesUISystem created and UI bindings registered successfully!");
        }

        public void OpenPanel()
        {
            m_PanelVisible.Update(true);
        }

        public void ClosePanel()
        {
            m_PanelVisible.Update(false);
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
            string json = (routes != null) ? JsonConvert.SerializeObject(routes, Formatting.None) : "[]";
            m_PlanJson.Update(json);
            m_PlanSeed.Update(seed);
            m_PlanStatus.Update((routes != null && routes.Count > 0) ? "ready" : "idle");
            if (message != null)
            {
                m_StatusMessage.Update(message);
            }
        }

        private void OnTogglePanel()
        {
            bool nextState = !m_PanelVisible.value;
            m_PanelVisible.Update(nextState);
            log.Info($"Toggle transit planner panel: {nextState}");
        }

        private void OnOpenPanel()
        {
            m_PanelVisible.Update(true);
        }

        private void OnClosePanel()
        {
            m_PanelVisible.Update(false);
        }

        private void OnGeneratePlan()
        {
            var generator = World.GetExistingSystemManaged<BusLineGenerator>();
            if (generator != null)
            {
                SetPlanStatus("planning", "Analyzing road network and calculating transit corridors...");
                m_PlanSeed.Update(0);
                generator.RequestPlan(0);
            }
        }

        private void OnNewPlan()
        {
            var generator = World.GetExistingSystemManaged<BusLineGenerator>();
            if (generator != null)
            {
                int nextSeed = m_PlanSeed.value + 1;
                m_PlanSeed.Update(nextSeed);
                SetPlanStatus("planning", $"Calculating alternative transit network (Variant #{nextSeed + 1})...");
                generator.RequestPlan(nextSeed);
            }
        }

        private void OnDiscardPlan()
        {
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
            var generator = World.GetExistingSystemManaged<BusLineGenerator>();
            if (generator != null)
            {
                SetPlanStatus("building", "Instantiating bus stops and building selected transit lines...");
                generator.RequestBuildSelectedPlan();
            }
        }

        private void OnToggleRoute(int routeId, bool enabled)
        {
            var generator = World.GetExistingSystemManaged<BusLineGenerator>();
            if (generator != null)
            {
                generator.ToggleRoute(routeId, enabled);
            }
        }

        private void OnToggleStop(int routeId, int stopIndex, bool enabled)
        {
            var generator = World.GetExistingSystemManaged<BusLineGenerator>();
            if (generator != null)
            {
                generator.ToggleStop(routeId, stopIndex, enabled);
            }
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

        protected override void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
            base.OnDestroy();
        }
    }
}
