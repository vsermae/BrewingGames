# Abyssal Echo — Room-Based Roguelike RPG

<p align="center">
  <img src="https://raw.githubusercontent.com/vsermae/BrewingGames/main/.github/assets/banner.svg" alt="Abyssal Echo Banner" width="960" />
</p>

Abyssal Echo is a room-based roguelike RPG built around descent, survival, and repeated mastery. Each run pushes you deeper into the abyss, where every room can trigger combat, treasure, blessings, inventory pressure, and evolving risk.

- Procedural room generation and layered floor progression
- Deep, tactical combat with buffs, debuffs, status effects, and boss mechanics
- Build-focused loot, upgrades, and equipment decisions
- C#-native Godot implementation with clean separation between logic and rendering
- Windows and Linux export support without relying on the browser or Electron

## Why Play It?

### 1. Every room matters

Each floor is generated with purpose. You never know what awaits behind the next door:

- Enemy rooms for turn-based combat
- Treasure rooms with reward choices
- Shops for buying gear and consumables
- Altars for permanent power or risky sacrifice
- Event rooms with uncertain but meaningful outcomes
- Boss rooms guarding each major progression milestone

### 2. Combat rewards planning

This is not a simple “hit harder” game. Every decision matters:

- Attack, defend, and skill timing all shape outcomes
- Status effects like poison, burn, freeze, stun, and armor break change the flow of battle
- Floors introduce unique mechanics that force different playstyles
- Boss fights are designed around adaptation, not brute force

### 3. Built for long-term iteration

The project uses Godot 4 + C# with strong systems separation:

- `Core/` and `Systems/` stay free of Godot-specific types where possible
- Player logic, enemies, loot, and progression are data-driven
- Build validation, smoke tests, screenshots, and benchmarks are part of the workflow

## Quick Start

### Requirements

- Godot 4.7.2 .NET
- .NET SDK 8

### Run locally

```bash
cd godot
dotnet build
godot --path .
```

### Export builds

```bash
cd godot
godot --headless --path . --export-release "Windows Desktop" build/win/AbyssEcho.exe
godot --headless --path . --export-release "Linux" build/linux/AbyssEcho.x86_64
```

## Core Features

- Room-based procedural progression
- Turn-based combat with skill timing and enemy intent
- Inventory management and equipment optimization
- Progression through floor-specific mechanics and boss challenges
- Data-oriented balance structure for fast iteration
- Native desktop distribution for Windows and Linux

## Project Structure

```text
godot/
├── project.godot
├── AbyssEcho.csproj
├── export_presets.cfg
├── Data/game_data.json
├── Scenes/
│   ├── Boot.tscn
│   ├── Menu.tscn
│   └── Map.tscn
├── Scripts/
│   ├── Autoload/
│   ├── Data/
│   ├── Core/
│   ├── Systems/
│   ├── UI/
│   ├── Dev/
│   └── Scenes/
├── README.md
├── MIGRATION.md
└── ...
```

## Developer Notes

The majority of gameplay data lives in `godot/Data/game_data.json`, including:

- skills
- equipment
- enemy and boss stats
- floor mechanics
- item and room balancing

This keeps the game easy to tune and validates broken configuration early instead of mid-run.

## Documentation

- [README.md](README.md)
- [godot/README.md](godot/README.md)
- [godot/MIGRATION.md](godot/MIGRATION.md)
- [RELEASE_NOTES.md](RELEASE_NOTES.md)

## License

This project is distributed under the repository license.

---

Abyssal Echo — descend deeper. Survive longer. Claim the abyss.
