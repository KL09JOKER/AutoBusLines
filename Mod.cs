using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;

namespace AutoBusLines
{
    public class Mod : IMod
    {
        public static ILog log = LogManager.GetLogger($"{nameof(AutoBusLines)}.{nameof(Mod)}").SetShowsErrorsInUI(false);
        public static Setting setting { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info(nameof(OnLoad));

            if (GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
                log.Info($"Current mod asset at {asset.path}");

            // Register visual settings in Options -> Mod Settings
            setting = new Setting(this);
            setting.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(setting));
            AssetDatabase.global.LoadSettings(nameof(AutoBusLines), setting, new Setting(this));

            // Register all AutoBusLines systems in dependency order
            updateSystem.UpdateAt<DepotFinderSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<RoadNetworkScanner>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<RoadDepotAssigner>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<BusLineGenerator>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateAt<RouteInspector>(SystemUpdatePhase.GameSimulation);

            log.Info("AutoBusLines loaded and settings registered to Options UI!");
        }

        public void OnDispose()
        {
            log.Info(nameof(OnDispose));
            if (setting != null)
            {
                setting.UnregisterInOptionsUI();
                setting = null;
            }
        }
    }
}
