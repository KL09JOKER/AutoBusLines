import React, { useState, useMemo, useEffect } from "react";
import { bindValue, trigger, useValue } from "cs2/api";
import { Panel, Button, Icon, Scrollable, Tooltip, ConfirmationDialog, Portal } from "cs2/ui";
import { Checkbox } from "./components/Checkbox";
import { getBusStopModelInfo, BUS_STOP_MODELS } from "./busStopModels";
import styles from "./PlanPanel.module.scss";

interface PlannedStop {
    index: number;
    virtualId: number;
    name: string;
    posX: number;
    posY: number;
    posZ: number;
    isStationBay: boolean;
    isPreExisting: boolean;
    enabled: boolean;
}

interface PlannedRoute {
    id: number;
    name: string;
    color: string;
    lengthKm: number;
    enabled: boolean;
    stops: PlannedStop[];
}

interface PlanPanelProps {
    onClose: () => void;
}

interface StopModelOption {
    name: string;
    icon: string;
}

const planStatusBinding = bindValue<string>("autoBusLines", "planStatus", "idle");
const planJsonBinding = bindValue<string>("autoBusLines", "planJson", "[]");
const statusMessageBinding = bindValue<string>("autoBusLines", "statusMessage", "");
const planSeedBinding = bindValue<number>("autoBusLines", "planSeed", 0);
const lineColorModeBinding = bindValue<string>("autoBusLines", "lineColorMode", "perStation");

// Settings bindings
const enablePlanModeBinding = bindValue<boolean>("autoBusLines", "enablePlanMode", true);
const excludeDeadEndsBinding = bindValue<boolean>("autoBusLines", "excludeDeadEnds", true);
const deadEndThresholdBinding = bindValue<number>("autoBusLines", "deadEndDistanceThreshold", 300);
const minStopsBinding = bindValue<number>("autoBusLines", "minStopsPerLine", 6);
const maxStopsBinding = bindValue<number>("autoBusLines", "maxStopsPerLine", 18);
const maxRouteLengthBinding = bindValue<number>("autoBusLines", "maxRouteLength", 15000);
const stopDensityBinding = bindValue<string>("autoBusLines", "stopDensity", "Balanced");
const targetSpacingBinding = bindValue<number>("autoBusLines", "targetStopSpacing", 200);
const selectedPrefabBinding = bindValue<string>("autoBusLines", "selectedStopPrefab", "All");
const prefabOptionsBinding = bindValue<string>("autoBusLines", "stopPrefabOptions", "[]");

type TabMode = "plan" | "settings";

interface ErrorBoundaryProps {
    children: React.ReactNode;
}

interface ErrorBoundaryState {
    hasError: boolean;
    errorText: string;
}

class ErrorBoundary extends React.Component<ErrorBoundaryProps, ErrorBoundaryState> {
    constructor(props: ErrorBoundaryProps) {
        super(props);
        this.state = { hasError: false, errorText: "" };
    }

    static getDerivedStateFromError(error: any): ErrorBoundaryState {
        return { hasError: true, errorText: String(error?.message || error) };
    }

    componentDidCatch(error: any, errorInfo: any) {
        console.error("AutoBusLines ErrorBoundary caught error:", error, errorInfo);
    }

    render() {
        if (this.state.hasError) {
            return (
                <div style={{ padding: "16rem", color: "#feb2b2" }}>
                    <div style={{ fontWeight: 700, fontSize: "14rem", marginBottom: "8rem" }}>
                        UI Render Error
                    </div>
                    <div style={{ fontSize: "11rem", color: "#e2e8f0" }}>{this.state.errorText}</div>
                    <button
                        type="button"
                        style={{
                            marginTop: "12rem",
                            padding: "6rem 12rem",
                            background: "#2b6cb0",
                            color: "#fff",
                            border: "none",
                            borderRadius: "4rem",
                            cursor: "pointer"
                        }}
                        onClick={() => this.setState({ hasError: false, errorText: "" })}
                    >
                        Retry
                    </button>
                </div>
            );
        }
        return this.props.children;
    }
}

interface SliderInputProps {
    value: number;
    min: number;
    max: number;
    step: number;
    unit: string;
    multiplier?: number;
    onChange: (val: number) => void;
}

const SliderInput: React.FC<SliderInputProps> = ({
    value,
    min,
    max,
    step,
    unit,
    multiplier = 1,
    onChange
}) => {
    const displayValue = multiplier !== 1 ? (value * multiplier).toFixed(1) : String(value);
    const [textValue, setTextValue] = useState<string>(displayValue);
    const [isFocused, setIsFocused] = useState(false);

    useEffect(() => {
        if (!isFocused) {
            setTextValue(multiplier !== 1 ? (value * multiplier).toFixed(1) : String(value));
        }
    }, [value, isFocused, multiplier]);

    const percentage = Math.max(0, Math.min(100, ((value - min) / (max - min)) * 100));

    const handleTrackMouseDown = (e: React.MouseEvent<HTMLDivElement>) => {
        const track = e.currentTarget.getBoundingClientRect();
        const updateFromPos = (clientX: number) => {
            const ratio = Math.max(0, Math.min(1, (clientX - track.left) / track.width));
            const raw = min + ratio * (max - min);
            const stepped = Math.round(raw / step) * step;
            const clamped = Math.max(min, Math.min(max, stepped));
            onChange(clamped);
        };

        updateFromPos(e.clientX);

        const onMouseMove = (ev: MouseEvent) => {
            updateFromPos(ev.clientX);
        };

        const onMouseUp = () => {
            window.removeEventListener("mousemove", onMouseMove);
            window.removeEventListener("mouseup", onMouseUp);
        };

        window.addEventListener("mousemove", onMouseMove);
        window.addEventListener("mouseup", onMouseUp);
    };

    const handleInputCommit = () => {
        setIsFocused(false);
        const num = parseFloat(textValue);
        if (!isNaN(num)) {
            const actualVal = multiplier !== 1 ? num / multiplier : num;
            const stepped = Math.round(actualVal / step) * step;
            const clamped = Math.max(min, Math.min(max, stepped));
            onChange(clamped);
            setTextValue(multiplier !== 1 ? (clamped * multiplier).toFixed(1) : String(clamped));
        } else {
            setTextValue(displayValue);
        }
    };

    return (
        <div className={styles.sliderControlContainer}>
            <div className={styles.sliderTrack} onMouseDown={handleTrackMouseDown}>
                <div className={styles.sliderFill} style={{ width: `${percentage}%` }} />
                <div className={styles.sliderThumb} style={{ left: `${percentage}%` }} />
            </div>
            <div className={styles.sliderInputBox}>
                <input
                    type="text"
                    className={styles.numericTextInput}
                    value={textValue}
                    onFocus={() => setIsFocused(true)}
                    onChange={(e) => setTextValue(e.target.value)}
                    onBlur={handleInputCommit}
                    onKeyDown={(e) => {
                        if (e.key === "Enter") handleInputCommit();
                    }}
                />
                <span className={styles.unitLabel}>{unit}</span>
            </div>
        </div>
    );
};

export const PlanPanel: React.FC<PlanPanelProps> = ({ onClose }) => {
    const planStatus = useValue(planStatusBinding);
    const planJson = useValue(planJsonBinding);
    const statusMessage = useValue(statusMessageBinding);
    const planSeed = useValue(planSeedBinding);
    const lineColorMode = useValue(lineColorModeBinding);

    // Settings values
    const enablePlanMode = useValue(enablePlanModeBinding);
    const excludeDeadEnds = useValue(excludeDeadEndsBinding);
    const deadEndThreshold = useValue(deadEndThresholdBinding);
    const minStops = useValue(minStopsBinding);
    const maxStops = useValue(maxStopsBinding);
    const maxRouteLength = useValue(maxRouteLengthBinding);
    const stopDensity = useValue(stopDensityBinding);
    const targetSpacing = useValue(targetSpacingBinding);
    const selectedPrefab = useValue(selectedPrefabBinding);
    const prefabOptionsJson = useValue(prefabOptionsBinding);

    const [activeTab, setActiveTab] = useState<TabMode>("plan");
    const [expandedRoutes, setExpandedRoutes] = useState<Record<number, boolean>>({});
    const [position, setPosition] = useState({ x: 260, y: 90 });
    const [confirmDialog, setConfirmDialog] = useState<{
        title: string;
        message: string;
        action: () => void;
    } | null>(null);

    const routes: PlannedRoute[] = useMemo(() => {
        if (!planJson || planJson === "[]") return [];
        try {
            const parsed = JSON.parse(planJson);
            return Array.isArray(parsed) ? parsed : [];
        } catch {
            return [];
        }
    }, [planJson]);

    // Network stats
    const { enabledRouteCount, enabledStopCount, totalDistanceKm } = useMemo(() => {
        let routeCount = 0;
        let stopCount = 0;
        let dist = 0;

        for (const route of routes) {
            if (route.enabled) {
                routeCount++;
                dist += route.lengthKm;
                stopCount += route.stops.filter(s => s.enabled).length;
            }
        }

        return {
            enabledRouteCount: routeCount,
            enabledStopCount: stopCount,
            totalDistanceKm: Math.round(dist * 10) / 10
        };
    }, [routes]);

    const prefabOptions: StopModelOption[] = useMemo(() => {
        try {
            const parsed = JSON.parse(prefabOptionsJson);
            if (Array.isArray(parsed) && parsed.length > 1) {
                const filtered = parsed
                    .map((item: any) => {
                        if (typeof item === "string") {
                            return {
                                name: item,
                                icon: item === "All" ? "Media/Game/Icons/Bus.svg" : "Media/Game/Icons/BusStop.svg"
                            };
                        }
                        return {
                            name: item.name || "",
                            icon: item.icon || "Media/Game/Icons/BusStop.svg"
                        };
                    })
                    .filter(opt => {
                        if (!opt.name) return false;
                        const lower = opt.name.toLowerCase();
                        return !lower.includes("integrated") && !lower.includes("placeholder") && !lower.includes("platform");
                    });
                if (filtered.length > 1) {
                    return filtered;
                }
            }
        } catch {
            // Ignore error and use default fallback below
        }

        return Object.keys(BUS_STOP_MODELS).map(key => ({
            name: key,
            icon: key === "All" ? "Media/Game/Icons/Bus.svg" : "Media/Game/Icons/BusStop.svg"
        }));
    }, [prefabOptionsJson]);

    const densityModes = ["Balanced", "Dense", "Ultra", "Low", "Custom"];

    // Dragging logic
    const handleHeaderMouseDown = (e: React.MouseEvent) => {
        if (e.button !== 0) return;
        const target = e.target as HTMLElement;
        if (target.closest('button')) return;

        e.preventDefault();
        const startX = e.clientX;
        const startY = e.clientY;
        const initialX = position.x;
        const initialY = position.y;

        const handleMouseMove = (ev: MouseEvent) => {
            const dx = ev.clientX - startX;
            const dy = ev.clientY - startY;
            setPosition({
                x: Math.max(10, Math.min(window.innerWidth - 600, initialX + dx)),
                y: Math.max(10, Math.min(window.innerHeight - 200, initialY + dy))
            });
        };

        const handleMouseUp = () => {
            window.removeEventListener("mousemove", handleMouseMove);
            window.removeEventListener("mouseup", handleMouseUp);
        };

        window.addEventListener("mousemove", handleMouseMove);
        window.addEventListener("mouseup", handleMouseUp);
    };

    const toggleExpand = (routeId: number) => {
        setExpandedRoutes(prev => ({
            ...prev,
            [routeId]: !prev[routeId]
        }));
    };

    const handleToggleRoute = (routeId: number, currentEnabled: boolean) => {
        trigger("autoBusLines", "toggleRoute", routeId, !currentEnabled);
    };

    const handleToggleStop = (routeId: number, stopIndex: number, currentEnabled: boolean) => {
        trigger("autoBusLines", "toggleStop", routeId, stopIndex, !currentEnabled);
    };

    const handleFocusStop = (stop: PlannedStop) => {
        trigger("autoBusLines", "focusStop", stop.posX, stop.posY, stop.posZ);
    };

    const handleNewPlan = () => {
        trigger("autoBusLines", "newPlan");
    };

    const handleDiscard = () => {
        trigger("autoBusLines", "discardPlan");
    };

    const handleBuildSelected = () => {
        trigger("autoBusLines", "buildSelected");
    };

    const handleGenerateInitial = () => {
        trigger("autoBusLines", "generatePlan");
    };

    const confirmAndExecute = (title: string, message: string, onConfirm: () => void) => {
        setConfirmDialog({
            title,
            message,
            action: onConfirm,
        });
    };

    const headerContent = (
        <div className={styles.header} onMouseDown={handleHeaderMouseDown}>
            <div className={styles.headerTitleGroup}>
                <Icon src="Media/Game/Icons/Bus.svg" className={styles.headerIcon} tinted={true} />
                <span className={styles.headerText}>Auto Bus Lines</span>
                {planSeed > 0 && activeTab === "plan" && (
                    <span className={styles.badge}>Variant #{planSeed + 1}</span>
                )}
            </div>
            <div className={styles.headerRightGroup}>
                <div className={styles.tabBar}>
                    <button
                        type="button"
                        className={`${styles.tabBtn} ${activeTab === "plan" ? styles.tabActive : ""}`}
                        onClick={(e) => { e.stopPropagation(); setActiveTab("plan"); }}
                    >
                        Plan
                    </button>
                    <button
                        type="button"
                        className={`${styles.tabBtn} ${activeTab === "settings" ? styles.tabActive : ""}`}
                        onClick={(e) => { e.stopPropagation(); setActiveTab("settings"); }}
                    >
                        Settings
                    </button>
                </div>
                <Button
                    variant="round"
                    className={styles.closeBtn}
                    onSelect={onClose}
                >
                    <Icon src="Media/Glyphs/Close.svg" className={styles.closeIcon} tinted={true} />
                </Button>
            </div>
        </div>
    );

    const renderSettings = () => (
        <Scrollable vertical trackVisibility="always" className={styles.settingsScrollable}>
            <div className={styles.settingsList}>
                {/* Line Appearance & Color */}
                <div className={styles.settingsGroup}>
                    <div className={styles.settingsGroupTitle}>Line Appearance & Color</div>

                    <div className={styles.settingsRow}>
                        <div className={styles.settingInfo}>
                            <span className={styles.settingTitle}>Line Color Scheme</span>
                            <span className={styles.settingDesc}>
                                Per Station groups routes by hub/terminal. Random assigns distinct rainbow colors.
                            </span>
                        </div>
                        <div className={styles.settingControl}>
                            <div className={styles.pillGroup}>
                                <button
                                    type="button"
                                    className={`${styles.pillBtn} ${lineColorMode === "perStation" ? styles.pillActive : ""}`}
                                    onClick={() => trigger("autoBusLines", "setColorMode", "perStation")}
                                >
                                    Per Station
                                </button>
                                <button
                                    type="button"
                                    className={`${styles.pillBtn} ${lineColorMode === "random" ? styles.pillActive : ""}`}
                                    onClick={() => trigger("autoBusLines", "setColorMode", "random")}
                                >
                                    Random Rainbow
                                </button>
                            </div>
                        </div>
                    </div>
                </div>

                {/* Route & Line Sizing */}
                <div className={styles.settingsGroup}>
                    <div className={styles.settingsGroupTitle}>Route & Line Sizing</div>

                    <div className={styles.settingsRow}>
                        <div className={styles.settingInfo}>
                            <span className={styles.settingTitle}>Minimum Stops Per Line</span>
                            <span className={styles.settingDesc}>
                                Minimum stops required to form a valid bus transit loop (2–30).
                            </span>
                        </div>
                        <div className={styles.settingControl}>
                            <div className={styles.stepperControl}>
                                <button
                                    type="button"
                                    className={styles.stepperBtn}
                                    onClick={() => trigger("autoBusLines", "setMinStops", Math.max(3, minStops - 1))}
                                >
                                    −
                                </button>
                                <span className={styles.stepperValue}>{minStops}</span>
                                <button
                                    type="button"
                                    className={styles.stepperBtn}
                                    onClick={() => trigger("autoBusLines", "setMinStops", Math.min(30, minStops + 1))}
                                >
                                    +
                                </button>
                            </div>
                        </div>
                    </div>

                    <div className={styles.settingsRow}>
                        <div className={styles.settingInfo}>
                            <span className={styles.settingTitle}>Maximum Stops Per Line</span>
                            <span className={styles.settingDesc}>
                                Maximum stops on a single bus route before closing loop (4–50).
                            </span>
                        </div>
                        <div className={styles.settingControl}>
                            <div className={styles.stepperControl}>
                                <button
                                    type="button"
                                    className={styles.stepperBtn}
                                    onClick={() => trigger("autoBusLines", "setMaxStops", Math.max(4, maxStops - 1))}
                                >
                                    −
                                </button>
                                <span className={styles.stepperValue}>{maxStops}</span>
                                <button
                                    type="button"
                                    className={styles.stepperBtn}
                                    onClick={() => trigger("autoBusLines", "setMaxStops", Math.min(50, maxStops + 1))}
                                >
                                    +
                                </button>
                            </div>
                        </div>
                    </div>

                    <div className={styles.settingsRowStacked}>
                        <div className={styles.settingInfo}>
                            <span className={styles.settingTitle}>Maximum Route Length</span>
                            <span className={styles.settingDesc}>
                                Maximum loop distance. Loops exceeding this length are split into smaller routes.
                            </span>
                        </div>
                        <SliderInput
                            value={maxRouteLength || 15000}
                            min={2000}
                            max={50000}
                            step={500}
                            unit="km"
                            multiplier={0.001}
                            onChange={(val) => trigger("autoBusLines", "setMaxRouteLength", val)}
                        />
                        <div className={styles.presetGroup}>
                            {[5000, 10000, 15000, 20000, 30000].map((dist) => (
                                <button
                                    key={dist}
                                    type="button"
                                    className={`${styles.presetBtn} ${maxRouteLength === dist ? styles.presetActive : ""}`}
                                    onClick={() => trigger("autoBusLines", "setMaxRouteLength", dist)}
                                >
                                    {dist / 1000} km
                                </button>
                            ))}
                        </div>
                    </div>
                </div>

                {/* Stop Placement & Spacing */}
                <div className={styles.settingsGroup}>
                    <div className={styles.settingsGroupTitle}>Stop Placement & Spacing</div>

                    <div className={styles.settingsRow}>
                        <div className={styles.settingInfo}>
                            <span className={styles.settingTitle}>Bus Stop Density</span>
                            <span className={styles.settingDesc}>
                                Preset corridor density or custom target distance.
                            </span>
                        </div>
                        <div className={styles.settingControl}>
                            <div className={styles.pillGroup}>
                                {densityModes.map((mode) => (
                                    <button
                                        key={mode}
                                        type="button"
                                        className={`${styles.pillBtn} ${(stopDensity || "Balanced").toLowerCase() === mode.toLowerCase() ? styles.pillActive : ""}`}
                                        onClick={() => trigger("autoBusLines", "setStopDensity", mode)}
                                    >
                                        {mode}
                                    </button>
                                ))}
                            </div>
                        </div>
                    </div>

                    <div className={styles.settingsRowStacked}>
                        <div className={styles.settingInfo}>
                            <span className={styles.settingTitle}>Target Stop Spacing</span>
                            <span className={styles.settingDesc}>
                                Distance between bus stops along roads and corridors.
                            </span>
                        </div>
                        <SliderInput
                            value={targetSpacing || 200}
                            min={60}
                            max={500}
                            step={10}
                            unit="m"
                            multiplier={1}
                            onChange={(val) => trigger("autoBusLines", "setTargetSpacing", val)}
                        />
                        <div className={styles.presetGroup}>
                            {[75, 120, 200, 350].map((dist) => (
                                <button
                                    key={dist}
                                    type="button"
                                    className={`${styles.presetBtn} ${targetSpacing === dist ? styles.presetActive : ""}`}
                                    onClick={() => trigger("autoBusLines", "setTargetSpacing", dist)}
                                >
                                    {dist}m
                                </button>
                            ))}
                        </div>
                    </div>

                    <div className={styles.settingsRow}>
                        <div className={styles.settingInfo}>
                            <span className={styles.settingTitle}>Prevent Dead End Stops</span>
                            <span className={styles.settingDesc}>
                                Exclude cul-de-sacs and dead-end roads from stop placement to avoid difficult U-turns.
                            </span>
                        </div>
                        <div className={styles.settingControl}>
                            <Checkbox
                                isChecked={excludeDeadEnds}
                                onValueToggle={(checked) => trigger("autoBusLines", "setExcludeDeadEnds", checked)}
                            />
                        </div>
                    </div>

                    {excludeDeadEnds && (
                        <div className={styles.settingsRowStacked}>
                            <div className={styles.settingInfo}>
                                <span className={styles.settingTitle}>Long Dead End Threshold</span>
                                <span className={styles.settingDesc}>
                                    Allow bus stops on dead-end roads if their length exceeds this distance.
                                </span>
                            </div>
                            <SliderInput
                                value={deadEndThreshold || 300}
                                min={50}
                                max={2000}
                                step={25}
                                unit="m"
                                multiplier={1}
                                onChange={(val) => trigger("autoBusLines", "setDeadEndThreshold", val)}
                            />
                            <div className={styles.presetGroup}>
                                {[100, 200, 350, 500, 800].map((dist) => (
                                    <button
                                        key={dist}
                                        type="button"
                                        className={`${styles.presetBtn} ${deadEndThreshold === dist ? styles.presetActive : ""}`}
                                        onClick={() => trigger("autoBusLines", "setDeadEndThreshold", dist)}
                                    >
                                        {dist}m
                                    </button>
                                ))}
                            </div>
                        </div>
                    )}

                    <div className={styles.settingsRowStacked}>
                        <div className={styles.settingInfo}>
                            <span className={styles.settingTitle}>Bus Stop Model</span>
                            <span className={styles.settingDesc}>
                                Select a specific bus stop prefab model or randomized mix.
                            </span>
                        </div>
                        <div className={styles.prefabGrid}>
                            {prefabOptions.map((opt) => {
                                const isSelected = (opt.name === "All" && (!selectedPrefab || selectedPrefab === "All")) || selectedPrefab === opt.name;
                                const model = getBusStopModelInfo(opt.name, opt.icon);
                                return (
                                    <div
                                        key={opt.name}
                                        className={`${styles.prefabCard} ${isSelected ? styles.prefabActive : ""}`}
                                        onClick={() => trigger("autoBusLines", "setStopPrefab", opt.name)}
                                    >
                                        <div className={styles.prefabThumbnail}>
                                            {model.image.startsWith("data:") ? (
                                                <img src={model.image} className={styles.prefabModelImg} alt={model.title} />
                                            ) : (
                                                <div className={styles.prefabLogoWrapper}>
                                                    <Icon src={model.image} className={styles.prefabLogoIcon} />
                                                </div>
                                            )}
                                        </div>
                                        <div className={styles.prefabInfo}>
                                            <span className={styles.prefabName}>{model.title}</span>
                                            <span className={styles.prefabSubtitle}>{model.subtitle}</span>
                                        </div>
                                        {opt.name !== "All" ? (
                                            <span className={`${styles.prefabTag} ${model.isCustom ? styles.prefabTagCustom : ""}`}>
                                                {opt.name}
                                            </span>
                                        ) : (
                                            <span className={`${styles.prefabTag} ${styles.prefabTagAll}`}>Random</span>
                                        )}
                                    </div>
                                );
                            })}
                        </div>
                    </div>
                </div>

                {/* Maintenance & Tools */}
                <div className={styles.settingsGroup}>
                    <div className={styles.settingsGroupTitle}>Network Maintenance & Tools</div>

                    <div className={styles.actionGrid}>

                        <div className={styles.actionCard}>
                            <div className={styles.actionCardLeft}>
                                <div className={`${styles.actionBadge} ${styles.actionBadgeRepair}`}>
                                    <Icon src="Media/Glyphs/Gear.svg" className={styles.actionBadgeIcon} tinted={true} />
                                </div>
                                <div className={styles.actionCardTexts}>
                                    <span className={styles.actionCardTitle}>Repair Broken Bus Lines</span>
                                    <span className={styles.actionCardDesc}>Fix pathfinding failures and nudge conflicted stops.</span>
                                </div>
                            </div>
                            <Button
                                variant="flat"
                                className={`${styles.maintBtn} ${styles.repairBtn}`}
                                onSelect={() => confirmAndExecute(
                                    "Repair Broken Bus Lines",
                                    "This will scan all bus routes, adjust problematic roadside stop positions that failed pathfinding, and re-calculate route paths. Continue?",
                                    () => trigger("autoBusLines", "repairRoutes")
                                )}
                            >
                                <Icon src="Media/Glyphs/Gear.svg" className={styles.maintBtnIcon} tinted={true} />
                                <span>Repair</span>
                            </Button>
                        </div>

                        <div className={styles.actionCard}>
                            <div className={styles.actionCardLeft}>
                                <div className={`${styles.actionBadge} ${styles.actionBadgeDelete}`}>
                                    <Icon src="Media/Glyphs/Trash.svg" className={styles.actionBadgeIcon} tinted={true} />
                                </div>
                                <div className={styles.actionCardTexts}>
                                    <span className={styles.actionCardTitle}>Delete All Lines & Stops</span>
                                    <span className={styles.actionCardDesc}>Wipe all bus lines and roadside stops city-wide, including manual lines. (Stations & depots preserved).</span>
                                </div>
                            </div>
                            <Button
                                variant="flat"
                                className={`${styles.maintBtn} ${styles.deleteBtn}`}
                                onSelect={() => confirmAndExecute(
                                    "Delete All Lines & Stops",
                                    "Warning: This action deletes ALL bus transit lines and roadside bus stops in your city, including lines and stops you created manually. Stations and depots will be preserved. This action cannot be undone.",
                                    () => trigger("autoBusLines", "deleteAll")
                                )}
                            >
                                <Icon src="Media/Glyphs/Trash.svg" className={styles.maintBtnIcon} tinted={true} />
                                <span>Delete All</span>
                            </Button>
                        </div>
                    </div>
                </div>
            </div>
        </Scrollable>
    );

    return (
        <>
            <Panel
                className={styles.panel}
            style={{ left: `${position.x}px`, top: `${position.y}px` }}
            header={headerContent}
        >
            <div className={styles.container}>
                <ErrorBoundary>
                    {activeTab === "settings" ? (
                        renderSettings()
                    ) : routes.length === 0 ? (
                    <div className={styles.emptyState}>
                        <div className={styles.emptyTitle}>
                            {planStatus === "planning"
                                ? "Analyzing City & Planning Routes..."
                                : "No Active Transit Plan"}
                        </div>
                        <div className={styles.emptyDesc}>
                            {planStatus === "planning"
                                ? "Examining road network corridors, curbside bus stop candidates, and depot loop connections."
                                : "Click below to scan your road network and preview proposed bus lines and stops before building."}
                        </div>
                        {planStatus !== "planning" && (
                            <>
                                <div className={styles.settingsHintBanner}>
                                    <div className={styles.settingsHintLeft}>
                                        <div className={styles.settingsHintBadge}>
                                            <Icon src="Media/Glyphs/Gear.svg" className={styles.settingsHintIcon} tinted={true} />
                                        </div>
                                        <div className={styles.settingsHintTexts}>
                                            <span className={styles.settingsHintTitle}>Configure Preferences First</span>
                                            <span className={styles.settingsHintDesc}>
                                                Choose your bus stop model, stop density, and route length in Settings before calculating a plan.
                                            </span>
                                        </div>
                                    </div>
                                    <Button
                                        variant="flat"
                                        className={styles.settingsHintBtn}
                                        onSelect={() => setActiveTab("settings")}
                                    >
                                        <Icon src="Media/Glyphs/Gear.svg" className={styles.settingsHintBtnIcon} tinted={true} />
                                        <span>Open Settings</span>
                                    </Button>
                                </div>
                                <Button
                                    variant="primary"
                                    className={styles.calculateBtn}
                                    onSelect={handleGenerateInitial}
                                >
                                    Calculate Transit Plan
                                </Button>
                            </>
                        )}
                        {statusMessage && (
                            <div className={styles.statusBanner}>{statusMessage}</div>
                        )}
                    </div>
                ) : (
                    <>
                        {/* Stats Summary */}
                        <div className={styles.statsRow}>
                            <div className={styles.statItem}>
                                <span className={styles.statLabel}>Selected Lines</span>
                                <span className={styles.statValue}>
                                    {enabledRouteCount} / {routes.length}
                                </span>
                            </div>
                            <div className={styles.statItem}>
                                <span className={styles.statLabel}>Active Stops</span>
                                <span className={styles.statValue}>{enabledStopCount}</span>
                            </div>
                            <div className={styles.statItem}>
                                <span className={styles.statLabel}>Coverage</span>
                                <span className={styles.statValue}>{totalDistanceKm} km</span>
                            </div>
                        </div>

                        {/* Actions Toolbar */}
                        <div className={styles.actionToolbar}>
                            <Tooltip tooltip="Calculate an alternative layout with different corridor pairings">
                                <Button
                                    variant="flat"
                                    className={styles.actionBtn}
                                    onSelect={handleNewPlan}
                                    disabled={planStatus === "planning" || planStatus === "building"}
                                >
                                    New Plan
                                </Button>
                            </Tooltip>

                            <Tooltip tooltip="Open Settings to adjust stop models, spacing, and constraints">
                                <Button
                                    variant="flat"
                                    className={styles.actionBtn}
                                    onSelect={() => setActiveTab("settings")}
                                    disabled={planStatus === "building"}
                                >
                                    <Icon src="Media/Glyphs/Gear.svg" className={styles.actionBtnIcon} tinted={true} />
                                    <span>Settings</span>
                                </Button>
                            </Tooltip>

                            <Tooltip tooltip="Discard current plan without placing any entities">
                                <Button
                                    variant="flat"
                                    className={styles.actionBtn}
                                    onSelect={handleDiscard}
                                    disabled={planStatus === "building"}
                                >
                                    Discard
                                </Button>
                            </Tooltip>

                            <Tooltip tooltip="Construct only the enabled routes and checked stops">
                                <Button
                                    variant="primary"
                                    className={styles.buildBtn}
                                    onSelect={handleBuildSelected}
                                    disabled={enabledRouteCount === 0 || planStatus === "building"}
                                >
                                    {planStatus === "building" ? "Building..." : `Build Selected (${enabledRouteCount})`}
                                </Button>
                            </Tooltip>
                        </div>

                        {statusMessage && (
                            <div className={styles.statusBanner}>{statusMessage}</div>
                        )}

                        {/* Scrollable Routes List */}
                        <Scrollable vertical trackVisibility="always" className={styles.scrollable}>
                            <div className={styles.routeList}>
                                {routes.map(route => {
                                    const isExpanded = !!expandedRoutes[route.id];
                                    const activeStops = route.stops.filter(s => s.enabled).length;

                                    return (
                                        <div
                                            key={route.id}
                                            className={`${styles.routeCard} ${!route.enabled ? styles.disabled : ""}`}
                                            onMouseEnter={() => trigger("autoBusLines", "hoverRoute", route.id)}
                                            onMouseLeave={() => trigger("autoBusLines", "hoverRoute", 0)}
                                        >
                                            <div
                                                className={styles.routeHeader}
                                                onClick={() => toggleExpand(route.id)}
                                            >
                                                <Checkbox
                                                    isChecked={route.enabled}
                                                    onValueToggle={() => handleToggleRoute(route.id, route.enabled)}
                                                />
                                                <div
                                                    className={styles.colorBar}
                                                    style={{ backgroundColor: route.color }}
                                                />
                                                <div className={styles.routeInfo}>
                                                    <span className={styles.routeName}>{route.name}</span>
                                                    <span className={styles.routeSub}>
                                                        {activeStops} stops · {route.lengthKm} km
                                                    </span>
                                                </div>
                                                <button
                                                    type="button"
                                                    className={styles.expandBtn}
                                                    onClick={(e) => {
                                                        e.stopPropagation();
                                                        toggleExpand(route.id);
                                                    }}
                                                >
                                                    <Icon
                                                        src="Media/Glyphs/FilledArrowRight.svg"
                                                        className={`${styles.expandIcon} ${isExpanded ? styles.expanded : ""}`}
                                                        tinted={true}
                                                    />
                                                </button>
                                            </div>

                                            {isExpanded && (
                                                <div className={styles.stopList}>
                                                    {route.stops.map(stop => (
                                                        <div
                                                            key={stop.index}
                                                            className={`${styles.stopRow} ${!stop.enabled || !route.enabled ? styles.disabled : ""}`}
                                                            onMouseEnter={() => trigger("autoBusLines", "hoverStop", route.id, stop.index)}
                                                            onMouseLeave={() => trigger("autoBusLines", "hoverStop", 0, -1)}
                                                        >
                                                            <Checkbox
                                                                isChecked={stop.enabled}
                                                                onValueToggle={() => handleToggleStop(route.id, stop.index, stop.enabled)}
                                                            />
                                                            <span className={styles.stopIndex}>#{stop.index}</span>
                                                            <span className={styles.stopName}>{stop.name}</span>
                                                            {stop.isStationBay && (
                                                                <span className={`${styles.stopTag} ${styles.stationBay}`}>Terminal</span>
                                                            )}
                                                            {stop.isPreExisting && !stop.isStationBay && (
                                                                <span className={`${styles.stopTag} ${styles.existing}`}>Existing</span>
                                                            )}
                                                            <Tooltip tooltip="Center camera on stop">
                                                                <Button
                                                                    variant="icon"
                                                                    className={styles.focusBtn}
                                                                    onSelect={() => handleFocusStop(stop)}
                                                                >
                                                                    <Icon
                                                                        src="Media/Game/Icons/MapMarker.svg"
                                                                        className={styles.focusIcon}
                                                                        tinted={true}
                                                                    />
                                                                </Button>
                                                            </Tooltip>
                                                        </div>
                                                    ))}
                                                </div>
                                            )}
                                        </div>
                                    );
                                })}
                            </div>
                        </Scrollable>
                    </>
                )}
                </ErrorBoundary>
            </div>
        </Panel>

        {confirmDialog && (
            <Portal>
                <div style={{ position: "fixed", top: 0, left: 0, width: "100vw", height: "100vh", zIndex: 100000, pointerEvents: "auto" }}>
                    <ConfirmationDialog
                        title={confirmDialog.title}
                        message={confirmDialog.message}
                        confirm="Confirm"
                        cancel="Cancel"
                        cancellable={true}
                        dismissible={true}
                        zIndex={100000}
                        onConfirm={() => {
                            const act = confirmDialog.action;
                            setConfirmDialog(null);
                            act();
                        }}
                        onCancel={() => {
                            setConfirmDialog(null);
                        }}
                    />
                </div>
            </Portal>
        )}
        </>
    );
};
