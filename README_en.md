# Abyssal Echo — Room-Based Roguelike RPG

Abyssal Echo is a roguelike dungeon crawler built in Godot 4 with C#.
It generates room-based maps, triggers battles, chests, shops, altars, and random events as you descend deeper into the abyss, ultimately reaching the 15th floor to defeat the Abyssal Sovereign.

This project is a full Godot-native implementation of the game logic and systems in C#, with the gameplay rules kept separate from the Godot engine types. Rendering, input, resources, and save data all use Godot's native capabilities.

The previous Phaser 3 + TypeScript / Electron version has been retired. The legacy code and packaging pipeline were removed, and the migration details are documented in [godot/MIGRATION.md](godot/MIGRATION.md).

## Features

- Room-based procedural dungeon generation
- Turn-based combat with enemy skills, status effects, and layered boss mechanics
- Gear, upgrades, inventory, relic-style loot, and combat-oriented progression
- Multiple floor rules, boss-specific mechanics, and deep-run scaling
- Godot 4 + .NET 8 build pipeline with headless validation and release packaging
- Windows and Linux export targets without browser/Electron dependencies

## Quick Start

Requirements:
- Godot 4.7.2 .NET version
- .NET SDK 8

```bash
cd godot
dotnet build
godot --path .
```

For export templates, place them here:
`~/.local/share/godot/export_templates/4.7.2.stable.mono/`

See [godot/README.md](godot/README.md) for detailed setup and development notes.

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
└── README.md
```

Key design points:
- `Core/` and `Systems/` avoid `using Godot` so logic can run in headless validation
- Most gameplay content is driven by `godot/Data/game_data.json`
- UI and scene composition are handled in the Godot project, while core rules remain data-driven and testable

## Build and Release

| Scenario | Command / Trigger | Output |
| --- | --- | --- |
| Local run | `dotnet build && godot --path godot` | — |
| Local export | `godot --headless --path godot --export-release "Windows Desktop" build/win/AbyssEcho.exe`<br>`godot --headless --path godot --export-release "Linux" build/linux/AbyssEcho.x86_64` | Executables + `.pck` + data folder |
| Push to any branch | `.cnb.yml` `Godot Logic Selfcheck` | Validation only |
| Tag push (for example `v1.4.0`) | `tag_push` | Self-check → Export → Release packaging |
| Manual trigger | `web_trigger` | Same as above without publishing |

Artifacts ship as a three-part package:
- Executable
- `.pck` file
- `data_<product>_<platform>_<arch>/` directory

CI builds:
- Windows package: `AbyssEcho-windows-x86_64.zip`
- Linux package: `AbyssEcho-linux-x86_64.tar.gz`

## Validation and CI

The project includes multiple checks to ensure the game logic and exported builds are valid.

```bash
cd godot
dotnet build
godot --headless --path . --import
godot --headless --path . -- --selfcheck
godot --headless --path . -- --uismoke
godot --path . -- --bench
godot --headless --path . -- --play=7
godot --headless --path . -- --probe=3
godot --headless --path . -- --probe=sweep=30
xvfb-run -a godot --path . --rendering-driver opengl3 -- --shot=screenshots
godot --headless --path . -- --from-shots=screenshots
```

These checks cover:
- Logic correctness and combat rules
- UI smoke tests and rendering layer ordering
- Performance benchmarks
- Full-run simulation and probe-driven validation
- Screenshot-based assertion replay through a manifest

## Gameplay Overview

You explore a map composed of connected rooms. Each room can contain:

| Symbol | Room | Content |
| --- | --- | --- |
| Start | Start room | Spawn point on each floor |
| · | Empty room | Nothing happens |
| ! | Enemy room | Turn-based combat |
| $ | Chest room | Choose one of three rewards |
| Shop | Shop | Buy gear with gold |
| ? | Event room | Make a choice with random outcomes |
| † | Altar room | Purify, sacrifice HP, or gain a boon |
| ⚑ | Elite room | Layer-scaled elite enemy |
| ☥ | Training room | Heal, buff, or add a random affix |
| ♁ | Curse room | Rare cursed item with a choice |
| ☠ | Boss room | A boss encounter every five floors |

The goal is to clear the current floor, survive the boss encounter at the appropriate depth, and reach the final boss on floor 15.

## Combat

Turn-based combat uses actions and counters similar to classic roguelikes.

Core systems include:
- Attack / defend / skill actions
- Buffs and debuffs
- Poison, burn, stat modifications, and layered effects
- Boss mechanics tied to specific floors
- Skill unlock progression and gear-defined build choices

Player actions include:
- Move with `WASD` or arrow keys
- Wait with `Space`
- Open inventory with `I`
- Open help with `H`
- Close panels with `Esc`
- Use skills during combat with numeric hotkeys (`1` to `9`)

## Inventory and Equipment

- 8-slot inventory
- 3 equipment slots: weapon, armor, accessory
- Each item defines its effects explicitly
- Equipping gear can change base stats and support build optimization
- Many upgrades and rewards are designed to be comparable and reversible for planning

## Progression and Difficulty

The game is structured around:
- Floor-specific mechanics
- Progression through multiple “deep run” loops
- Increasing enemy strength with depth
- Greater build pressure in later floors
- Boss-specific challenge patterns and room compositions

## Data-Driven Design

Most gameplay configuration lives in `godot/Data/game_data.json`.
This includes:
- Skills
- Items and equipment
- Enemies and boss data
- Events and room weights
- Floor mechanic mappings
- Global balancing values

This keeps the design readable and lets the game validate invalid data at startup.

## Save Data

- Meta progression: `user://abyss_echo_meta.cfg`
- Run progression: `user://abyss_echo_run.cfg`

Run data is saved when you:
- descend floors
- resolve battles
- buy items
- use consumables
- change equipment
- save and return to the title screen
- close the game directly

The game intentionally stores only non-derivable state in the save file, while reconstructing map state from the seed.

## Dependencies

- Godot 4.7.2 .NET
- .NET SDK 8
- Export templates under `~/.local/share/godot/export_templates/4.7.2.stable.mono/`
- Linux CI container includes the required rendering and font dependencies

## License

This project is distributed under the MIT-style license included in the repository.

## Additional Notes

For deeper implementation details, validation strategy, export pipeline, code architecture, and visual rules, see:
- [README.md](README.md)
- [godot/README.md](godot/README.md)
- [godot/MIGRATION.md](godot/MIGRATION.md)
- [RELEASE_NOTES.md](RELEASE_NOTES.md)

This project favors explicit validation, data-driven tuning, and deterministic logic checks over “it looks right” development.
