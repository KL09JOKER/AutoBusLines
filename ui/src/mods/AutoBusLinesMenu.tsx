import React from "react";
import { bindValue, trigger, useValue } from "cs2/api";
import { Button, Tooltip, Portal } from "cs2/ui";
import { PlanPanel } from "./PlanPanel";
import styles from "./AutoBusLinesMenu.module.scss";

const panelVisibleBinding = bindValue<boolean>("autoBusLines", "panelVisible", false);
const planStatusBinding = bindValue<string>("autoBusLines", "planStatus", "idle");
const planJsonBinding = bindValue<string>("autoBusLines", "planJson", "[]");

export const AutoBusLinesMenuButton: React.FC = () => {
    const panelVisible = useValue(panelVisibleBinding);
    const planStatus = useValue(planStatusBinding);
    const planJson = useValue(planJsonBinding);

    let routeCount = 0;
    if (planStatus === "ready" && planJson) {
        try {
            const routes = JSON.parse(planJson);
            if (Array.isArray(routes)) {
                routeCount = routes.filter((r: any) => r.enabled).length;
            }
        } catch {
            // Ignore parse error
        }
    }

    const handleToggle = () => {
        trigger("autoBusLines", "togglePanel");
    };

    return (
        <div className={styles.container}>
            <Tooltip tooltip="Auto Bus Lines — Plan Mode">
                <Button
                    variant="floating"
                    src="Media/Game/Icons/Bus.svg"
                    selected={panelVisible}
                    onSelect={handleToggle}
                    className={styles.toolbarButton}
                >
                    {routeCount > 0 && <span className={styles.badge}>{routeCount}</span>}
                </Button>
            </Tooltip>
        </div>
    );
};

export const AutoBusLinesPanel: React.FC = () => {
    const panelVisible = useValue(panelVisibleBinding);

    const handleClose = () => {
        trigger("autoBusLines", "closePanel");
    };

    if (!panelVisible) return null;

    return (
        <Portal>
            <PlanPanel onClose={handleClose} />
        </Portal>
    );
};

export const AutoBusLinesMenu = AutoBusLinesMenuButton;
