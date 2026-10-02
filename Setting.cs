using System.Collections.Generic;
using Colossal;
using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;
using Game.UI;
using Game.UI.Widgets;
using Game.UI.Localization;

namespace AutoBusLines
{
    public enum StopDensityMode
    {
        Balanced,
        Dense,
        Ultra,
        Low,
        Custom
    }

    [FileLocation(nameof(AutoBusLines))]
    [SettingsUIGroupOrder(kMainGroup, kStopTypeGroup, kRouteGroup, kSpacingGroup)]
    [SettingsUIShowGroupName(kMainGroup, kStopTypeGroup, kRouteGroup, kSpacingGroup)]
    public class Setting : ModSetting
    {
        public const string kSection = "Main";
        public const string kMainGroup = "Main";
        public const string kStopTypeGroup = "StopTypes";
        public const string kRouteGroup = "RouteSettings";
        public const string kSpacingGroup = "StopSpacing";

        public Setting(IMod mod) : base(mod)
        {
        }

        [SettingsUISection(kSection, kMainGroup)]
        public bool AutoGenerateOnLoad { get; set; } = true;

        [SettingsUISection(kSection, kMainGroup)]
        [SettingsUIButton]
        [SettingsUIConfirmation]
        public bool TriggerScanNow
        {
            set
            {
                Mod.log.Info("Manual scan triggered via Mod Settings UI!");

                // Request regeneration by resetting the BusLineGenerator system
                var world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
                if (world != null)
                {
                    var generator = world.GetExistingSystemManaged<BusLineGenerator>();
                    if (generator != null)
                    {
                        generator.RequestGeneration();
                        Mod.log.Info("Regeneration requested!");
                    }
                }
            }
        }

        [SettingsUISection(kSection, kMainGroup)]
        [SettingsUIButton]
        [SettingsUIConfirmation]
        public bool RepairBrokenRoutes
        {
            set
            {
                Mod.log.Info("Manual route repair triggered via Mod Settings UI!");
                var world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
                if (world != null)
                {
                    var generator = world.GetExistingSystemManaged<BusLineGenerator>();
                    if (generator != null)
                    {
                        generator.RequestRepair();
                        Mod.log.Info("Route repair requested!");
                    }
                }
            }
        }

        [SettingsUISection(kSection, kMainGroup)]
        [SettingsUIButton]
        [SettingsUIConfirmation]
        public bool DeleteAllLinesAndStops
        {
            set
            {
                Mod.log.Info("Delete all bus lines and stops triggered via Mod Settings UI!");
                var world = Unity.Entities.World.DefaultGameObjectInjectionWorld;
                if (world != null)
                {
                    var generator = world.GetExistingSystemManaged<BusLineGenerator>();
                    if (generator != null)
                    {
                        generator.DeleteAllBusLinesAndStops();
                        Mod.log.Info("Deletion complete!");
                    }
                }
            }
        }

        public static readonly List<string> DiscoveredStopPrefabNames = new List<string>();
        public static int DiscoveredStopPrefabVersion = 1;

        public static int SpacingVersion = 1;
        public static int GetSpacingVersion() => SpacingVersion;

        [SettingsUISection(kSection, kStopTypeGroup)]
        [SettingsUIDropdown(typeof(Setting), nameof(GetBusStopPrefabDropdownItems))]
        [SettingsUIValueVersion(typeof(Setting), nameof(GetBusStopPrefabDropdownVersion))]
        public string SelectedStopPrefab { get; set; } = "All";

        public static DropdownItem<string>[] GetBusStopPrefabDropdownItems()
        {
            var items = new List<DropdownItem<string>>();
            items.Add(new DropdownItem<string>
            {
                value = "All",
                displayName = LocalizedString.Value("All Available Models (Randomized)")
            });

            for (int i = 0; i < DiscoveredStopPrefabNames.Count; i++)
            {
                string name = DiscoveredStopPrefabNames[i];
                items.Add(new DropdownItem<string>
                {
                    value = name,
                    displayName = LocalizedString.Value(name)
                });
            }

            return items.ToArray();
        }

        public static int GetBusStopPrefabDropdownVersion()
        {
            return DiscoveredStopPrefabVersion;
        }

        [SettingsUISection(kSection, kRouteGroup)]
        [SettingsUISlider(min = 4, max = 30, step = 1, unit = Unit.kInteger)]
        public int MinStopsPerLine { get; set; } = 6;

        [SettingsUISection(kSection, kRouteGroup)]
        [SettingsUISlider(min = 4, max = 50, step = 1, unit = Unit.kInteger)]
        public int MaxStopsPerLine { get; set; } = 18;

        [SettingsUISection(kSection, kRouteGroup)]
        [SettingsUISlider(min = 2000, max = 50000, step = 500, unit = Unit.kLength)]
        public int MaxRouteLength { get; set; } = 15000;

        private StopDensityMode _stopDensity = StopDensityMode.Balanced;
        private int _targetStopSpacing = 200;

        [SettingsUISection(kSection, kSpacingGroup)]
        [SettingsUIValueVersion(typeof(Setting), nameof(GetSpacingVersion))]
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

        [SettingsUISection(kSection, kSpacingGroup)]
        [SettingsUISlider(min = 60, max = 500, step = 10, unit = Unit.kLength)]
        [SettingsUIValueVersion(typeof(Setting), nameof(GetSpacingVersion))]
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
        public int MinStopSpacing
        {
            get => TargetStopSpacing;
            set => TargetStopSpacing = value;
        }

        // Custom line naming removed - game auto-generates names from RouteNumber

        public override void SetDefaults()
        {
            AutoGenerateOnLoad = true;
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
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.AutoGenerateOnLoad)), "Auto Generate On Load" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.AutoGenerateOnLoad)), "Automatically place bus stops and generate bus lines connecting all roads to the nearest bus depot when a save is loaded." },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TriggerScanNow)), "Generate Bus Lines Now" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.TriggerScanNow)), "Manually trigger bus stop placement and bus line generation across all paved roads in the city." },
                { m_Setting.GetOptionWarningLocaleID(nameof(Setting.TriggerScanNow)), "This will place bus stops on paved roads and create bus transit lines connected to the nearest depot/station. Continue?" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.RepairBrokenRoutes)), "Repair Broken Bus Lines" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.RepairBrokenRoutes)), "Scans existing bus routes for pathfinding failures, nudges problematic roadside bus stops away from road conflict zones, and re-triggers pathfinding." },
                { m_Setting.GetOptionWarningLocaleID(nameof(Setting.RepairBrokenRoutes)), "This will scan all bus routes, adjust problematic roadside stop positions that failed pathfinding, and re-calculate route paths. Continue?" },
                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.DeleteAllLinesAndStops)), "Delete All Bus Lines & Stops" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.DeleteAllLinesAndStops)), "Wipes and removes all generated and existing bus transit lines and roadside bus stops across the entire city. (Permanent stations and depots are preserved)." },
                { m_Setting.GetOptionWarningLocaleID(nameof(Setting.DeleteAllLinesAndStops)), "Are you sure you want to delete ALL bus transit lines and roadside bus stops in your city? This action cannot be undone." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.SelectedStopPrefab)), "Bus Stop Model" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.SelectedStopPrefab)), "Select the specific bus stop model/prefab to place across the road network, or choose 'All Available Models (Randomized)'." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.MinStopsPerLine)), "Minimum Stops Per Line" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.MinStopsPerLine)), "Minimum number of bus stops required to form a valid bus transit line (default: 6)." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.MaxStopsPerLine)), "Maximum Stops Per Line" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.MaxStopsPerLine)), "Maximum number of bus stops on a single bus route before closing the loop (default: 18)." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.MaxRouteLength)), "Maximum Route Length" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.MaxRouteLength)), "Maximum perimeter distance of a bus loop (default: 15,000m / 15km). Loops exceeding this length are split into smaller routes." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.StopDensity)), "Bus Stop Density" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.StopDensity)), "Select how densely bus stops are placed across the city: Balanced (~200m), Dense (~120m), Ultra / Every Block (~75m), Low (~350m express), or Custom (fine-tune with slider below)." },

                { m_Setting.GetEnumValueLocaleID(StopDensityMode.Balanced), "Balanced (~200m)" },
                { m_Setting.GetEnumValueLocaleID(StopDensityMode.Dense), "Dense (~120m)" },
                { m_Setting.GetEnumValueLocaleID(StopDensityMode.Ultra), "Ultra / Every Block (~75m)" },
                { m_Setting.GetEnumValueLocaleID(StopDensityMode.Low), "Low / Express (~350m)" },
                { m_Setting.GetEnumValueLocaleID(StopDensityMode.Custom), "Custom (Slider)" },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.TargetStopSpacing)), "Target Stop Spacing" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.TargetStopSpacing)), "Distance between bus stops along roads and corridors (default: 200m). Lower values place more stops closer together; higher values space stops farther apart." },

                { m_Setting.GetOptionLabelLocaleID(nameof(Setting.MinStopSpacing)), "Target Stop Spacing" },
                { m_Setting.GetOptionDescLocaleID(nameof(Setting.MinStopSpacing)), "Distance between bus stops along roads and corridors (default: 200m)." },

                { m_Setting.GetOptionGroupLocaleID(Setting.kMainGroup), "Bus Transit Automation" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kStopTypeGroup), "Bus Stop Model Selection" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kRouteGroup), "Route & Line Size Settings" },
                { m_Setting.GetOptionGroupLocaleID(Setting.kSpacingGroup), "Stop Placement & Spacing" },
                { m_Setting.GetOptionTabLocaleID(Setting.kSection), "General" },
            };
        }

        public void Unload()
        {
        }
    }
}
