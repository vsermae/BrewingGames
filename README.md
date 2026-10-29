# 深渊回想 · Room-Based Roguelike RPG

<p align="center">
  <img src="https://raw.githubusercontent.com/vsermae/BrewingGames/main/.github/assets/banner.png" alt="Abyssal Echo Banner" width="960" />
</p>

Abyssal Echo 是一款以“深渊下探”为核心的房间制 Roguelike RPG。你将穿过不断变化的地图，探索每层房间、击败敌人、搜刮宝物、升级装备，并在第 15 层终局打败深渊之主。

- 玩法独特：房间制地图、随机事件、商店、祭坛、训练室、宝箱和 Boss 组成的层层挑战
- 机制丰富：战斗、升级、毒、灼烧、状态层层叠加，战术选择决定每一场胜负
- 架构稳健：Godot 4 + C# 原生实现，规则逻辑与渲染层分离，适合长期迭代
- 发行友好：Windows / Linux 导出，免浏览器、Chromium、Electron

## 游戏亮点

### 1. 走进每一间房间，决定你的命运

每一层地图都是程序生成的。你可能遇到：

- 敌人房：回合制战斗
- 宝箱：三选一稀有奖励
- 商店：用金币换装备与道具
- 祭坛：强化、净化或代价换取力量
- 事件房：随机结果，影响下一层路线
- Boss 房：每 5 层一座守门者

### 2. 战斗不是简单的“砍砍砍”

Abyssal Echo 的战斗设计强调资源管理与决策：

- 攻击、防御、技能三种核心玩法
- 状态 debuff / buff 叠加：中毒、灼烧、冰封、石肤、眩晕等
- Boss 与每层机制深度绑定，不只是数值堆叠
- 伤害预测、技能冷却、装备比较、回合节奏都被设计得可读

### 3. 数据驱动，不靠“感觉编程”

大部分内容都定义在 `godot/Data/game_data.json` 中：

- 技能
- 装备
- 怪物与 Boss
- 事件与房间权重
- 楼层机制
- 关卡与数值平衡

这样设计的好处是：数值改动不需要重写逻辑，且启动时可以检测配置异常。

### 4. 从“能玩”到“能持续开发”

工程实现上，逻辑层与渲染层分离：

- `Core/` 与 `Systems/` 不依赖 Godot 类型
- 自动化验证与 headless 测试贯穿整个开发流程
- 自检、冒烟测试、截图验证、性能基准、探针模拟都在项目中支持

## 运行方式

### 环境要求

- Godot 4.7.2 .NET 版
- .NET SDK 8

### 快速开始

```bash
cd godot
dotnet build
godot --path .
```

### 导出

```bash
cd godot
godot --headless --path . --export-release "Windows Desktop" build/win/AbyssEcho.exe
godot --headless --path . --export-release "Linux" build/linux/AbyssEcho.x86_64
```

## 结构概览

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

## 玩法节奏

- 第 1 ~ 15 层：逐层推进，机制与敌人强度增强
- 每 5 层一位 Boss：需要铺垫装备与技巧
- 深潜后每层都会带来更高难度与更多副作用
- 进阶玩法中仍然要管理金币、库存、装备、技能和状态

## 适合什么样的玩家

如果你喜欢：

- 经典 Roguelike 的 “小步快跑、不断成长”
- 高自由度装备收集和建造
- 房间式地图探险和随机风险
- 战斗系统中“读信息、算出胜负”的快感

那么 Abyssal Echo 一定值得一试。

## 相关文档

- [README.md](README.md)
- [godot/README.md](godot/README.md)
- [godot/MIGRATION.md](godot/MIGRATION.md)
- [RELEASE_NOTES.md](RELEASE_NOTES.md)

## 许可证

本项目采用仓库中包含的许可证协议。

---

Abyssal Echo — dive deeper, survive longer, and conquer the abyss.
