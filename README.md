# Auto Bus Lines [Beta]

[![Paradox Mods](https://img.shields.io/badge/Paradox%20Mods-161531-blue.svg)](https://mods.paradoxplaza.com/mods/161531/Windows)
[![Cities: Skylines II](https://img.shields.io/badge/Cities:%20Skylines%20II-Mod-orange.svg)](https://www.paradoxinteractive.com/games/cities-skylines-ii)
[![Version](https://img.shields.io/badge/Version-2.0.4-green.svg)](https://mods.paradoxplaza.com/mods/161531/Windows)
[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](LICENSE.md)

Auto Bus Lines automatically designs and constructs complete, functional bus transit networks across your entire city. Open the planner, calculate a transit plan, review it interactively, tweak what you want, and hit **Build Selected**.

**[Available on Paradox Mods](https://mods.paradoxplaza.com/mods/161531/Windows)**

---

## How to Use

1. Open the **Auto Bus Lines** floating window from the top toolbar bus icon (or via Options → Mod Settings → Auto Bus Lines).
2. **Lines Planner tab**: Automatically design and build complete bus loop routes connecting to your nearest Bus Depot or Terminal. Scope to a specific **District** or plan citywide, preview the proposed network, and hit **Build Selected**.
3. **Stops Generator tab**: Generate curbside bus stops across a selected district without creating transit lines, allowing you to assign lines manually.
4. **Settings tab**: Customize stop density, spacing, stop models, line colors, and maintenance tools.

---

## Features

### 🗂️ 3-Tab Architecture
- **Lines Planner**: Interactive loop route preview, stop and line toggles, multi-variant generator.
- **Stops Generator**: Standalone curbside stop generation across roads without automated lines or depots.
- **Settings & Tools**: Spacing/density tuning, color palette customization, line repair, and unused stop cleanup.

### 🏙️ District Boundary Scoping
- Restrict route planning and stop placement to a selected city District or plan citywide.
- Clean up unassigned stops district-by-district with **Clear Unused Stops**.

### 🚏 Smart Stop Placement
- Automatically places roadside bus stops along road spans, safely away from intersections.
- Respects right-hand and left-hand traffic, divided roads, and one-way streets.
- Reuses existing bus stops and station platforms wherever they already exist.

### 🔁 Route Optimization
- Multi-corridor planner with 2-Opt path smoothing generates smooth, circular loop lines.
- Connects all routes directly to your nearest Bus Depots and Bus Terminals.

### 🎨 Vibrant Line Colors
- **Per Station mode**: Each terminal hub anchors to a bold, distinct color family. Lines sharing a hub stay cohesive — never dull or washed-out.
- **Random mode**: 16 hand-picked vivid transit colors (Electric Blue, Safety Orange, Vivid Emerald, Neon Violet, Crimson, Cyan, Amber, Deep Pink…).

### 🚌 Custom Bus Stop Support
- Supports vanilla EU and NA bus stop models (shelter, sign, bikes).
- Custom/modded bus stop prefabs are detected and shown with their mod thumbnail in the Settings selector.

### ⚙️ Settings Tab
- Stop density presets: Balanced, Dense, Ultra / Every Block, Low / Express, Custom.
- Target stop spacing slider (60m – 500m).
- Min / max stops per line, maximum route length.
- Line color scheme (Per Station or Random Rainbow).
- Custom bus stop model selector with squared visual thumbnails.
- Auto-generate on city load toggle.
- Repair Broken Bus Lines tool.

---

## Contributing

Contributions, bug fixes, feature suggestions, and localization updates are welcome!

- Fork this repository and submit a **Pull Request**.
- Please describe the issue or enhancement clearly in your PR.
- The UI is written in **TypeScript + React (SCSS)** in `ui/src/`.
- The backend is written in **C#** targeting the Cities: Skylines II modding SDK.

### UI Development
```bash
cd ui
npm install
npm run build   # compile once
npm run dev     # watch mode
```

### C# Build
```bash
dotnet build    # compiles + deploys to your local CS2 Mods folder
```

---

## License

This project is open-source software licensed under the **GNU General Public License v3.0 (GPL-3.0)**. See the [LICENSE.md](LICENSE.md) file for details.
