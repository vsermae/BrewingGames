# 深渊回响 · Godot 4 + C# 版

房间制 Roguelike RPG 的 Godot 版，也是仓库里**唯一**一套实现。**规则层与表现层都在自动化验证之下**——迁移过程与逐模块对照见 [MIGRATION.md](MIGRATION.md)。

## 结构

```
godot/
├── project.godot            # 引擎配置（InputMap / 窗口 / Autoload）
├── AbyssEcho.csproj         # net8.0 + Godot.NET.Sdk 4.7.2
├── export_presets.cfg       # Windows Desktop + Linux 两套导出预设
├── Data/game_data.json      # 全部游戏数据（唯一真相）
├── Scenes/
│   ├── Boot.tscn            # 入口：加载数据 → 自检或主菜单
│   ├── Menu.tscn            # 主菜单
│   └── Map.tscn             # 地图 + 战斗（战斗作为子节点挂入）
└── Scripts/
    ├── Autoload/
    │   ├── GameStore.cs        # 存档（ConfigFile）+ 当前这一局
    │   └── SaveSerializer.cs   # 局内进度落盘：只存不可推导的状态，地图靠种子重算
    ├── Data/                   # 数据模型 + 加载 + 校验
    ├── Core/                   # Rng / Map / Entity / Game —— 纯 C#，不引用 Godot
    ├── Systems/                # Battle / Skills / Affixes / Loot / … —— 纯 C#
    ├── UI/                     # 主题、共用控件、矢量图标、立绘、粒子、飘字、音效、BGM、键位表
    ├── Dev/                    # UiSmoke（界面冒烟）/ Screenshot（截图）/ BenchStats（性能基线）
    └── Scenes/                 # Boot / MainMenu / MapScene / BattleScene / 自检
```

`Core/` 与 `Systems/` 不 `using Godot`（唯一例外是 `Data/GameDataStore.cs` 用 `FileAccess` 读数据文件），
所以整套规则能在 headless 里跑完整局——这是自检与 CI 的基础。

## 视觉规则

美术全部是 `_Draw` 现画的矢量，**不引入任何贴图**（断网可玩、包里只有数据）。
新增界面元素时守三条：

1. **颜色只在 `UI/Theme.cs` 的 `Palette` 里取。** 场景里不写 `new Color("…")`，
   否则深色底和面板会慢慢漂成十几个不同的蓝。
2. **一块纯色不许当面板用。** 换成 `UI/UiFrame.cs` 里的自绘件：
   `AmbienceBackdrop`（整屏氛围）、`HudBand`（条带）、`PanelPlate`（弹窗底）、
   `PlateButton`（铁牌按钮）、`AbyssStage`（单独一屏的演出）。
   共同配方是「纵向渐变 + 一层斜纹 + 内描边 + 上沿挑亮」——四样凑齐才读得出厚度。
3. **材质笔在 `UI/Paint.cs` 里补，不在调用点手画。** `VGradient` / `Hatches` /
   `Speckle` / `BeveledRect` 这几笔是共用的；在场景里临时写一段 `for` 画条纹，
   下一个人改不动它。

三处例外，别照抄：`StyleBoxFlat` 只用来做「无底色的内边距」（`BgColor` 透明），
进度条用它是因为 `ProgressBar` 只认 StyleBox；`PanelContainer` 只摆第一个非 top-level 子节点，
想在面板里叠一层装饰要画在面板自己身上，另塞一个 `Control` 会被压成 0 高。

## 跑起来

环境：`.NET 8 SDK` + `Godot 4.7.2 .NET (mono)`。CI 用 `.ci/godot/Dockerfile` 里那一份，
本机想照着搭最省事的做法：

```bash
# .NET 8
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 8.0 --install-dir /opt/dotnet
# Godot 4.7.2 mono（linux x86_64），解压后把可执行文件放上 PATH
curl -sSL -o godot.zip https://github.com/godotengine/godot-builds/releases/download/4.7.2-stable/Godot_v4.7.2-stable_mono_linux_x86_64.zip
# 渲染相关（--uismoke / --shot 需要一个显示设备）
apt-get install -y libxcursor1 libxinerama1 libxrandr2 libxi6 libgl1-mesa-dri libxkbcommon0 xvfb xauth fonts-noto-cjk
```

```bash
cd godot
dotnet build
godot --path .                                    # 正常启动
godot --path . -- --seed=123                      # 定种子，跳过菜单直接开局
godot --headless --path . --import                # 首次/干净工作区需要
godot --headless --path . -- --selfcheck          # 逻辑自检（107 项）
godot --headless --path . -- --uismoke            # 界面冒烟（53 项）
godot --path . -- --bench                         # 性能基线：首帧/帧耗分位/节点数/托管堆
godot --headless --path . -- --play=7             # 无头跑通一整局，逐层打血量与房型
godot --headless --path . -- --probe=7            # 一局「真的按玩家顺序玩」：逐间房核对 + 每层存读档往返
godot --headless --path . -- --probe=rooms        # 五种补给房「进门就清掉」的静态检查
```

后几类命令（`--uismoke` / `--shot` / `--bench` / `--play` / `--probe`）是本机真装 dotnet + Godot 之后才跑得动的；
只读代码不跑，改出来的东西只能说「看起来对」。

`--probe=N` 与 `--selfcheck` 的分工：自检断言的是「单步结算对不对」，
探针跑的是「连起来那条链子对不对」——走位、开箱、买东西、进店、
每清一层就存一次读一次，然后核对「我说我拿到了，我是不是真的拿到了」。
它不进 CI 断言（跑起来比自检慢），发现新问题时再挑一条写成自检。

## 操作

| 键 | 作用 |
|---|---|
| WASD / 方向键 | 在相邻房间之间移动 |
| E / 回车 | 进入当前房间（敌人房自动开战） |
| 空格 | 原地等待；Boss 出场演出中可跳过 |
| `>` 或 `.` | 本层清空后下楼 / 继续深潜 |
| I | 背包 |
| H | 操作说明（说明面板直接渲染 `UI/Keymap.cs`，不会和实现脱节） |
| M | 开关音效与 BGM |
| F11 | 全屏 |
| Esc | 关闭面板 / 暂停菜单（暂停里可以「保存并回到标题」） |
| 战斗中 1~9 / A / D / Z / Esc | 技能 / 普攻 / 防御 / 消耗品 / 逃跑 |

## 背包与金币

背包 8 格，装备槽 3 个（武器 / 护甲 / 饰品）。这个比例是有意的：
**换一件更好的装备，必然会剩下一件旧的**，而那件旧的要么有去处，要么就会
把格子挤掉——挤掉的那一格最后总是药。

所以背包里的每一件都能**卖**（商店面板里的「收购」那一排，或背包里的「卖 N」），
按模板价三成回收（数据表里写了 `sell` 就照写的来）。三成是刻意的：
商店加价买、三成卖出，一来一回永远是亏的，搬砖换不出钱。

三选一（战斗奖励 / 宝箱）里，**金币与回血那两张保底卡永远排在装备后面**。
原因不在玩法，在接口：宝箱只给一张卡，只取首张的调用方拿到的就该是「一定能拿走的东西」，
而不是满血时那张「回复 0 点」。

## 存档

- 元进展（碎片、解锁、成就）：`user://abyss_echo_meta.cfg`
- 局内进度：`user://abyss_echo_run.cfg`，在下楼、战斗结算、买完东西、地图上喝药/丢道具、
  保存回标题、以及**直接关窗**时写入（关窗走 `NotificationWMCloseRequest`，先存再退）
- 离线不推进游戏时间：读档不刷新商店、不掉血也不回血；战场本身不存档（回到敌人房重开一场新的战斗）
- 存档带版本号，版本对不上就当没有存档

## 导出

```bash
godot --headless --path . --export-release "Windows Desktop" build/win/AbyssEcho.exe
godot --headless --path . --export-release "Linux"           build/linux/AbyssEcho.x86_64
```

产物是**三件套**，缺一不可：可执行文件 + `.pck` + `data_<产品名>_<平台>_<架构>/` 目录。
CI 会把它们一起打成 zip / tar.gz。

## 依赖

- Godot **4.7.2** .NET 版（自带 GodotSharp）
- .NET SDK **8.0**
- 导出模板放 `~/.local/share/godot/export_templates/4.7.2.stable.mono/`
- 中文字体：界面用系统 CJK 字体兜底（Noto Sans CJK / 微软雅黑 / 苹方）；Linux 容器里装 `fonts-noto-cjk`

CI 直接用 `.ci/godot/Dockerfile`，这些都预装好了。
