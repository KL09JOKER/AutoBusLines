# Auto Bus Lines [Beta]

[![Paradox Mods](https://img.shields.io/badge/Paradox%20Mods-161531-blue.svg)](https://mods.paradoxplaza.com/mods/161531/Windows)
[![Cities: Skylines II](https://img.shields.io/badge/Cities:%20Skylines%20II-Mod-orange.svg)](https://www.paradoxinteractive.com/games/cities-skylines-ii)
[![Status](https://img.shields.io/badge/Status-Beta-yellow.svg)](https://mods.paradoxplaza.com/mods/161531/Windows)

An automated public transit mod for **Cities: Skylines II** that plans, places roadside bus stops with safe mid-span road clearances, generates balanced 2-Opt loop routes, and connects them directly to your city's bus depots and passenger terminals.

Available on Paradox Mods: **[Auto Bus Lines on Paradox Mods](https://mods.paradoxplaza.com/mods/161531/Windows)**

---

## Features

- 🚌 **City-Wide Coverage**: Scans paved road networks across residential, commercial, and industrial districts to deliver complete transit accessibility.
- 🛑 **Smart Roadside Bus Stop Placement**:
  - Dynamically places roadside bus stops safely along road spans outside of intersection conflict zones.
  - Automatically respects right-hand and left-hand traffic rules, divided roads, and one-way streets.
- 🏢 **Depot & Terminal Connectivity**:
  - Automatically locates and links routes to the closest Bus Depots and Bus Terminals without manual district setup.
- 🔄 **2-Opt Transit Loop Optimization**:
  - Intelligent route planning constructs smooth, circular loops that avoid self-intersecting bottlenecks and minimize vehicle bunching.
- 🔁 **Existing Stop & Platform Reuse**:
  - Discovers existing bus stops and station platforms across your city and seamlessly integrates them into newly planned lines.
- 🔧 **In-Game Route Repair Tool**:
  - Built-in "Repair Broken Bus Lines" tool detects broken paths (from bulldozing, road upgrades, or modifications), nudges problematic stops, and re-triggers game pathfinding.
- ⚙️ **Comprehensive Mod Settings**:
  - Accessible via **Options > Mod Settings > Auto Bus Lines**.
  - Configure minimum/maximum stops per line, stop spacing, maximum route length, custom/modded bus stop prefabs, and auto-generation on city save load.

---

## How to Use

1. Build at least one **Bus Depot** or **Passenger Bus Terminal** in your city.
2. Open **Options > Mod Settings > Auto Bus Lines**.
3. Click **"Generate Bus Lines Now"** (or enable *Auto Generate On Load* to automatically plan on city load).
4. Enjoy your automated transit network! If you reconfigure roads later, open settings and click **"Repair Broken Bus Lines"**.

The compiled mod will be automatically deployed by the modding toolchain to:
`%LocalAppData%\..\LocalLow\Colossal Order\Cities Skylines II\Mods\AutoBusLines`

---

## License

This project is licensed under a **Source-Available Collaborative Modding License** (see [LICENSE.md](LICENSE.md)).
- ✅ **Allowed**: Inspecting code, compiling locally for personal play, forking to submit Pull Requests to this official repository.
- ❌ **Prohibited**: Re-uploading, mirroring, or redistributing this mod (source or compiled binaries) to Paradox Mods, Steam Workshop, NexusMods, or any other platform without explicit written permission from the author.
