import React, { useState, useMemo, useEffect, useRef } from "react";
import { bindValue, trigger, useValue } from "cs2/api";
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

const panelVisibleBinding = bindValue<boolean>("autoBusLines", "panelVisible", false);
const planStatusBinding = bindValue<string>("autoBusLines", "planStatus", "idle");
const planJsonBinding = bindValue<string>("autoBusLines", "planJson", "[]");
const statusMessageBinding = bindValue<string>("autoBusLines", "statusMessage", "");
const planSeedBinding = bindValue<number>("autoBusLines", "planSeed", 0);

export const PlanPanel: React.FC = () => {
    const visible = useValue(panelVisibleBinding);
    const planStatus = useValue(planStatusBinding);
    const planJson = useValue(planJsonBinding);
    const statusMessage = useValue(statusMessageBinding);
    const planSeed = useValue(planSeedBinding);

    const [expandedRoutes, setExpandedRoutes] = useState<Record<number, boolean>>({});
    const [position, setPosition] = useState({ x: 90, y: 90 });
    const isDraggingRef = useRef(false);
    const dragOffsetRef = useRef({ x: 0, y: 0 });

    const routes: PlannedRoute[] = useMemo(() => {
        if (!planJson || planJson === "[]") return [];
        try {
            const parsed = JSON.parse(planJson);
            return Array.isArray(parsed) ? parsed : [];
        } catch {
            return [];
        }
    }, [planJson]);

    // Statistics
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
            totalDistanceKm: dist
        };
    }, [routes]);

    // Drag handlers
    const handleMouseDown = (e: React.MouseEvent) => {
        if ((e.target as HTMLElement).tagName.toLowerCase() === "button") return;
        isDraggingRef.current = true;
        dragOffsetRef.current = {
            x: e.clientX - position.x,
            y: e.clientY - position.y
        };

        const handleMouseMove = (ev: MouseEvent) => {
            if (!isDraggingRef.current) return;
            setPosition({
                x: Math.max(10, ev.clientX - dragOffsetRef.current.x),
                y: Math.max(10, ev.clientY - dragOffsetRef.current.y)
            });
        };

        const handleMouseUp = () => {
            isDraggingRef.current = false;
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

    const handleToggleRoute = (e: React.MouseEvent, routeId: number, currentEnabled: boolean) => {
        e.stopPropagation();
        trigger("autoBusLines", "toggleRoute", routeId, !currentEnabled);
    };

    const handleToggleStop = (e: React.MouseEvent, routeId: number, stopIndex: number, currentEnabled: boolean) => {
        e.stopPropagation();
        trigger("autoBusLines", "toggleStop", routeId, stopIndex, !currentEnabled);
    };

    const handleFocusStop = (e: React.MouseEvent, posX: number, posY: number, posZ: number) => {
        e.stopPropagation();
        trigger("autoBusLines", "focusStop", posX, posY, posZ);
    };

    if (!visible) return null;

    return (
        <div
            className={styles.container}
            style={{ left: `${position.x}px`, top: `${position.y}px` }}
        >
            <div className={styles.header} onMouseDown={handleMouseDown}>
                <div className={styles.titleArea}>
                    <div className={styles.title}>
                        <span>🚌</span>
                        <span>Auto Bus Lines Transit Planner</span>
                    </div>
                    <div className={styles.subtitle}>
                        {planStatus === "ready" 
                            ? `Variant #${planSeed + 1} • Interactive Preview Mode` 
                            : "Preview, Customize & Staged Network Builder"}
                    </div>
                </div>
                <button
                    className={styles.closeButton}
                    onClick={() => trigger("autoBusLines", "closePanel")}
                    title="Close"
                >
                    ✕
                </button>
            </div>

            <div className={styles.body}>
                {statusMessage && (
                    <div className={styles.statusBar}>
                        <span className={styles.statusText}>{statusMessage}</span>
                    </div>
                )}

                {planStatus === "idle" && (
                    <div className={styles.emptyState}>
                        <div className={styles.emptyIcon}>🗺️</div>
                        <div className={styles.emptyMessage}>
                            Plan Mode allows you to preview the mod&apos;s proposed bus lines and stop locations across the city before constructing anything.
                            <br /><br />
                            You can inspect route paths, toggle off unnecessary stops, disable entire lines, or generate alternative layouts with one click.
                        </div>
                        <button
                            className={styles.primaryButton}
                            onClick={() => trigger("autoBusLines", "generatePlan")}
                        >
                            <span>🗺️</span>
                            <span>Generate Transit Plan</span>
                        </button>
                    </div>
                )}

                {planStatus === "planning" && (
                    <div className={styles.emptyState}>
                        <div className={styles.loadingSpinner} />
                        <div className={styles.emptyMessage}>
                            Scanning city roads, connecting depots, and optimizing bus loops...
                        </div>
                    </div>
                )}

                {planStatus === "building" && (
                    <div className={styles.emptyState}>
                        <div className={styles.loadingSpinner} />
                        <div className={styles.emptyMessage}>
                            Building bus stops and establishing transit lines in the game world...
                        </div>
                    </div>
                )}

                {planStatus === "ready" && (
                    <>
                        <div className={styles.actionBar}>
                            <div className={styles.actionStats}>
                                <div className={styles.statItem}>
                                    <span>🏷️</span>
                                    <span>{enabledRouteCount} / {routes.length} Lines</span>
                                </div>
                                <div className={styles.statItem}>
                                    <span>📍</span>
                                    <span>{enabledStopCount} Stops</span>
                                </div>
                                <div className={styles.statItem}>
                                    <span>🛣️</span>
                                    <span>{totalDistanceKm.toFixed(1)} km</span>
                                </div>
                            </div>

                            <div className={styles.buttonGroup}>
                                <button
                                    className={styles.secondaryButton}
                                    onClick={() => trigger("autoBusLines", "newPlan")}
                                    title="Calculate alternative corridor routes and loops"
                                >
                                    <span>⟳</span>
                                    <span>New Plan</span>
                                </button>
                                <button
                                    className={styles.dangerButton}
                                    onClick={() => trigger("autoBusLines", "discardPlan")}
                                    title="Discard preview without building anything"
                                >
                                    Discard
                                </button>
                                <button
                                    className={styles.primaryButton}
                                    disabled={enabledRouteCount === 0 || enabledStopCount === 0}
                                    onClick={() => trigger("autoBusLines", "buildSelected")}
                                    title="Build only checked lines and stops"
                                >
                                    <span>🚌</span>
                                    <span>Build Selected</span>
                                </button>
                            </div>
                        </div>

                        <div className={styles.routesList}>
                            {routes.map(route => {
                                const isExpanded = !!expandedRoutes[route.id];
                                const activeStops = route.stops.filter(s => s.enabled).length;

                                return (
                                    <div
                                        key={route.id}
                                        className={`${styles.routeCard} ${!route.enabled ? styles.disabledRoute : ""}`}
                                    >
                                        <div
                                            className={styles.routeHeader}
                                            onClick={() => toggleExpand(route.id)}
                                        >
                                            <input
                                                type="checkbox"
                                                className={styles.checkbox}
                                                checked={route.enabled}
                                                onChange={() => {}}
                                                onClick={(e) => handleToggleRoute(e, route.id, route.enabled)}
                                            />
                                            <div
                                                className={styles.lineBadge}
                                                style={{ backgroundColor: route.color }}
                                            >
                                                {route.id}
                                            </div>
                                            <div className={styles.routeName}>
                                                {route.name}
                                            </div>
                                            <div className={styles.routeDetails}>
                                                {activeStops}/{route.stops.length} stops • {route.lengthKm} km
                                            </div>
                                            <div className={`${styles.chevron} ${isExpanded ? styles.open : ""}`}>
                                                ▼
                                            </div>
                                        </div>

                                        {isExpanded && (
                                            <div className={styles.stopsList}>
                                                {route.stops.map(stop => (
                                                    <div
                                                        key={stop.index}
                                                        className={`${styles.stopItem} ${!stop.enabled || !route.enabled ? styles.disabledStop : ""}`}
                                                    >
                                                        <input
                                                            type="checkbox"
                                                            className={styles.checkbox}
                                                            checked={stop.enabled}
                                                            disabled={!route.enabled}
                                                            onChange={() => {}}
                                                            onClick={(e) => handleToggleStop(e, route.id, stop.index, stop.enabled)}
                                                        />
                                                        <span className={styles.stopBadge}>
                                                            Stop {stop.index}
                                                        </span>
                                                        <span className={stop.isStationBay ? styles.stationTag : styles.curbTag}>
                                                            {stop.isStationBay ? "Station Bay" : (stop.isPreExisting ? "Pre-Existing" : "Curbside")}
                                                        </span>
                                                        <span className={styles.stopCoords}>
                                                            ({Math.round(stop.posX)}, {Math.round(stop.posZ)})
                                                        </span>
                                                        <button
                                                            className={styles.focusButton}
                                                            onClick={(e) => handleFocusStop(e, stop.posX, stop.posY, stop.posZ)}
                                                            title="Jump camera to this stop"
                                                        >
                                                            🔍 Focus
                                                        </button>
                                                    </div>
                                                ))}
                                            </div>
                                        )}
                                    </div>
                                );
                            })}
                        </div>
                    </>
                )}
            </div>

            <div className={styles.footer}>
                <span>
                    {planStatus === "ready" 
                        ? "Unchecked lines & stops will not be placed." 
                        : "Auto Bus Lines Preview & Customizer"}
                </span>
                <span>v1.1.0</span>
            </div>
        </div>
    );
};
