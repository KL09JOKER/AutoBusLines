import { ModRegistrar } from "cs2/modding";
import { AutoBusLinesMenuButton, AutoBusLinesPanel } from "./mods/AutoBusLinesMenu";

const register: ModRegistrar = (moduleRegistry) => {
    moduleRegistry.append("UniversalModMenu", AutoBusLinesMenuButton);
    moduleRegistry.append("Game", AutoBusLinesPanel);
};

export default register;