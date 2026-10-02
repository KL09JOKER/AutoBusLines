import { ModRegistrar } from "cs2/modding";
import { ToolbarButton } from "./mods/ToolbarButton";
import { PlanPanel } from "./mods/PlanPanel";

const register: ModRegistrar = (moduleRegistry) => {
    moduleRegistry.append("GameTopRight", ToolbarButton);
    moduleRegistry.append("Game", PlanPanel);
};

export default register;