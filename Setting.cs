using System;
using System.Collections.Generic;
using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI;
using Game.UI.Localization;

namespace AutoBusLines
{
    public enum LineColorMode
    {
        PerStation,
        Random
    }

    public enum StopDensityMode
    {
        Balanced,
        Dense,
        Ultra,
        Low,
        Custom
    }

    [FileLocation(nameof(AutoBusLines))]
    [SettingsUIGroupOrder(kHowToUseGroup, kVersionGroup)]
    [SettingsUIShowGroupName(kHowToUseGroup, kVersionGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kHowToUseGroup = "HowToUse";
        public const string kVersionGroup = "Version";

        public Setting(IMod mod) : base(mod)
        {
        }

        // ==========================================
        // Settings UI Properties (Options -> Mod Settings)
        // ==========================================

        [SettingsUISection(kSection, kHowToUseGroup)]
        [SettingsUIMultilineText]
        public string HowToUse => string.Empty;

        [SettingsUISection(kSection, kVersionGroup)]
        public string ModVersion => "1.1.0";

        // ==========================================
        // Backend / Custom UI Settings (Persisted to Disk)
        // Hidden from game Options UI via [SettingsUIHidden]
        // ==========================================

        [SettingsUIHidden]
        public bool EnablePlanMode { get; set; } = true;

        public static readonly List<string> DiscoveredStopPrefabNames = new List<string>();
        public static readonly Dictionary<string, string> DiscoveredStopPrefabIcons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static int DiscoveredStopPrefabVersion = 1;

        public static int SpacingVersion = 1;
        public static int GetSpacingVersion() => SpacingVersion;

        [SettingsUIHidden]
        public string SelectedStopPrefab { get; set; } = "All";

        [SettingsUIHidden]
        public int MinStopsPerLine { get; set; } = 6;

        [SettingsUIHidden]
        public int MaxStopsPerLine { get; set; } = 18;

        [SettingsUIHidden]
        public int MaxRouteLength { get; set; } = 15000;

        private LineColorMode _lineColoring = LineColorMode.PerStation;

        [SettingsUIHidden]
        public LineColorMode LineColoring
        {
            get => _lineColoring;
            set
            {
                _lineColoring = value;
                var world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
                if (world != null)
                {
                    var generator = world.GetExistingSystemManaged<BusLineGenerator>();
                    generator?.UpdatePlanColors(value);
                    AutoBusLinesUISystem.Instance?.SetColorMode(value == LineColorMode.PerStation ? "perStation" : "random");
                }
            }
        }

        private StopDensityMode _stopDensity = StopDensityMode.Balanced;
        private int _targetStopSpacing = 200;

        [SettingsUIHidden]
        public StopDensityMode StopDensity
        {
            get => _stopDensity;
            set
            {
                _stopDensity = value;
                switch (value)
                {
                    case StopDensityMode.Balanced:
                        _targetStopSpacing = 200;
                        break;
                    case StopDensityMode.Dense:
                        _targetStopSpacing = 120;
                        break;
                    case StopDensityMode.Ultra:
                        _targetStopSpacing = 75;
                        break;
                    case StopDensityMode.Low:
                        _targetStopSpacing = 350;
                        break;
                    case StopDensityMode.Custom:
                        break;
                }
                SpacingVersion++;
            }
        }

        [SettingsUIHidden]
        public int TargetStopSpacing
        {
            get => _targetStopSpacing;
            set
            {
                _targetStopSpacing = value;
                if (_stopDensity != StopDensityMode.Custom)
                {
                    int expected = _stopDensity switch
                    {
                        StopDensityMode.Balanced => 200,
                        StopDensityMode.Dense => 120,
                        StopDensityMode.Ultra => 75,
                        StopDensityMode.Low => 350,
                        _ => -1
                    };
                    if (value != expected)
                    {
                        _stopDensity = StopDensityMode.Custom;
                    }
                }
                SpacingVersion++;
            }
        }

        // Backward compatibility
        [SettingsUIHidden]
        public int MinStopSpacing
        {
            get => TargetStopSpacing;
            set => TargetStopSpacing = value;
        }

        public override void SetDefaults()
        {
            EnablePlanMode = true;
            LineColoring = LineColorMode.PerStation;
            SelectedStopPrefab = "All";
            MinStopsPerLine = 6;
            MaxStopsPerLine = 18;
            MaxRouteLength = 15000;
            StopDensity = StopDensityMode.Balanced;
            TargetStopSpacing = 200;
        }
    }

    public class LocaleEN : IDictionarySource
    {
        private readonly Setting m_Setting;

        public LocaleEN(Setting setting)
        {
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "Auto Bus Lines" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "General" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kHowToUseGroup), "How To Use" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kVersionGroup), "Information" },

                // How to Use
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.HowToUse)),
                    "1. BUILD A DEPOT OR TERMINAL:\n" +
                    "   Place at least one Bus Depot or Passenger Bus Terminal in your city so buses have an operational base.\n\n" +
                    "2. OPEN THE TRANSIT PLANNER:\n" +
                    "   Click the hexagonal Chirper icon (Universal Mod Menu) in the bottom-right corner of the screen and select the Auto Bus Lines bus icon.\n\n" +
                    "3. CONFIGURE NETWORK PREFERENCES:\n" +
                    "   In the Settings tab, customize stop spacing (Balanced, Dense, Ultra, Low, or Custom), select your preferred bus stop shelter/sign model, and adjust route sizes.\n\n" +
                    "4. PREVIEW & INSPECT ROUTES:\n" +
                    "   Click 'Generate Plan' or 'New Alternative' to generate a route proposal. The plan shows live route lines on the map. You can toggle off individual stops or entire routes, and click stops to focus the camera.\n\n" +
                    "5. BUILD TRANSIT LINES:\n" +
                    "   Click 'Build Selected Routes' to construct the active transit network in your city!\n\n" +
                    "6. MAINTENANCE & REPAIRS:\n" +
                    "   If you alter roads or bulldoze intersections, open the Settings tab and click 'Repair Broken Bus Lines' to re-path routes, or 'Delete All Lines & Stops' to start fresh."
                },

                // Version
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.ModVersion)), "Version" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.ModVersion)), "Current installed release version of the Auto Bus Lines mod." },
            };
        }

        public void Unload()
        {
        }
    }
}
