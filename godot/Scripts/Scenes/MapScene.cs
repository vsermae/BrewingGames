// 地图场景：房间格 + 玩家 + HUD + 各房间的交互面板。
//
// 这是垂直切片里最大的一块，也是规则最密的地方。写法上刻意分成三层：
//   1. BuildXxx 建结构（只跑一次）
//   2. Refresh 刷新数据（每次状态变化跑）
//   3. EnterRoom / OpenXxx 处理交互（玩家动作触发）
// 混在一起写的话，「读 HUD」和「改状态」会互相触发，最后没人说得清哪次刷新是必要的。
namespace AbyssEcho.Scenes;

using System.Linq;
using Godot;
using AbyssEcho.Core;
using AbyssEcho.Data;
using AbyssEcho.Systems;
using AbyssEcho.UI;

public partial class MapScene : Control
{
    private Game _run = null!;
    private GameStore _store = null!;

    private Control _roomLayer = null!;
    /// <summary>走廊 / 门框 / 可达光点：单独一层，房间块会盖住线头</summary>
    private MapLines _lineLayer = null!;
    private Control _decoLayer = null!;
    private MapLegend? _legend;
    /// <summary>当前这一帧的格距与房间块尺寸（自适应缩放的结果，随视口变）</summary>
    private MapMetrics _metrics = MapMetrics.Design;
    /// <summary>玩家方块单独一层：重建房间层时它会跟着被 QueueFree，之后再用就是访问已释放对象</summary>
    private Control _actorLayer = null!;
    private Control _playerDot = null!;
    private Label _hint = null!;
    private Label _statText = null!;
    private Label _floorText = null!;
    private Label _mechText = null!;
    private Label _bagText = null!;
    private ProgressBar _hpBar = null!;
    private ProgressBar _xpBar = null!;
    private Label _hpText = null!;
    private Label _xpText = null!;
    private ToastLayer _toast = null!;
    /// <summary>顶部 HUD：内容（PanelContainer）与它的条带底</summary>
    private Control? _topHud;
    private Control? _topBand;
    /// <summary>底部提示条：内容与它的条带底</summary>
    private Control? _bottomHint;
    private Control? _hintBand;
    private bool _busy;

    private EnemyTier _lastTier = EnemyTier.Normal;
    private int _lastMembers = 1;
    private bool _beatenBoss;

    /// <summary>设计格距：地图宽度富余时按它画（原版 TILE 也是无缩放的定值）</summary>
    private const int Tile = 92;

    /// <summary>格距下限：再小就撑不住矢量图标与骑士立绘</summary>
    private const int MinTile = 44;

    /// <summary>
    /// 格距上限。这里不是设计值 92，而是「按可用区域撑满」——
    /// 一张 5×4 的图在 1280×720 下按 92 算只占 340px 宽，剩下的 400 多像素全成了留白，
    /// 玩家看到的就是一张小图漂在大屏中间。上限只用来防「3×3 的图被放大到糊」，
    /// 所以取「可用区域的高度 / 行数」这个物理上限。
    /// </summary>
    public const int MaxTile = 132;

    /// <summary>房间块占格距的比例（原版 tile − 10）</summary>
    private const float RoomSpan = 0.86f;

    /// <summary>顶部 HUD 高度（与 BuildHud 里的 OffsetBottom 同一个数，改一处就够）</summary>
    public const float HudBandHeight = 74f;

    /// <summary>底部提示条高度（BuildHud 里的 OffsetTop = -HintHeight）</summary>
    public const float HintBandHeight = 46f;

    private const float HudHeight = HudBandHeight;
    private const float HintHeight = HintBandHeight;

    /// <summary>截图/调试用：让场景绑到指定这一局，而不是去 GameStore 拿</summary>
    public void BindRun(Game run) => _run = run;

    public override void _Ready()
    {
        _store = GetNode<GameStore>("/root/GameStore");
        // 进地图时手上的局优先：内存里有就用内存，没有就读盘（直接进地图的调试入口），
        // 都没有才开新局——这一步不能反过来，否则「继续」会变成「重开」。
        _run ??= _store.Run ?? _store.ContinueRun() ?? _store.NewRun();
        _store.SaveRun();
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        // 地图底：冷色的氛围层（比菜单暗、辉光压得更低），
        // 它衬着地砖——纯色底上的地砖看着像「贴在一张纸上」。
        var mapBg = new AmbienceBackdrop
        {
            Name = "Backdrop",
            Tint = new Color("5d86c8"),
            GlowStrength = 0.7f,
        };
        AddChild(mapBg);
        mapBg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        BuildHud();
        // 注意：Ignore 会让「这一层和它所有子节点」都不收鼠标事件，
        // 之前房间格点不动就是这个原因。这里要 Pass，让子按钮拿到点击。
        // 分层（从下到上）：走廊 → 房间砖 → 玩家 → 图例 → 提示。
        // 走廊单独一层，因为房间砖会盖住线头——「路铺在地砖底下」这个层次
        // 上一版是靠 Line2D 的 ZIndex 凑的，改一次布局就容易翻车。
        // 现在跨「层」的先后全部写进 UI/Layers.cs（StageOrder）：同一个父节点下
        // 谁先 AddChild 决定谁在下，而重建房间层一次就会把那个顺序换掉。
        _lineLayer = new MapLines { Name = "MapLines", MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_lineLayer);
        _lineLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _roomLayer = new Control { MouseFilter = MouseFilterEnum.Pass, Name = "RoomLayer" };
        AddChild(_roomLayer);
        _roomLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _actorLayer = new Control { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_actorLayer);
        _actorLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _decoLayer = new Control { MouseFilter = MouseFilterEnum.Ignore, Name = "MapDeco" };
        AddChild(_decoLayer);
        _decoLayer.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var mech = _run.FloorMechanicInfo;
        if (!string.IsNullOrEmpty(mech.Name) && _run.FloorMechanicId != "none")
        {
            ShowToast($"本层机制「{mech.Name}」：{mech.Desc}", Palette.AccentSoft);
        }
        // 地图是常驻场景（战斗挂在它下面），音频宿主挂在这里最省：
        // 战斗场景会被反复创建销毁，把播放器挂上去就等于每次进战斗都重建一次音频设备。
        Sfx.Attach(this);
        // BGM 跟着楼层机制走（Bgm.CueFor 是纯函数，映射写在 UI/Bgm.cs 里）：
        // 「这一层听起来该是什么样」和机制说明是同一件事的两种表达。
        EnsureFloorMusic();
        // 第一次进地图讲清「品质 = 词条数」：这是整个构筑的核心，
        // TS 版有这张卡，Godot 版一直没有——玩家只会按攻击力大小挑装备。
        // 标记跟着存档走（GameStore），重启游戏也不重复烦人。
        //
        // 同步弹，不走 `CallDeferred`。
        //
        // 以前是延后一帧的：那张卡要等布局，延后一帧看起来无害，实际是**在这一帧里
        // 跟所有别的东西赛跑**——`_Ready` 之后的那一帧，正是「场景建好了」的那一帧，
        // 也是玩家第一次按键、冒烟与截图工具第一次调 `OpenShop` / `OpenInventory` 的那一帧。
        // 卡先弹、面板后弹，屏幕上就是两张卡叠着（实测标题条是
        // `深渊回响 · 启程 | 商店`）。同一条断言因此时绿时红，而红的时候只报个数。
        // 引导卡该在**地图第一次看见玩家**的时刻出现，不是「一帧之后某个不确定的时刻」。
        ShowIntroIfFirstRun();
    }

    /// <summary>
    /// 新手引导的时机：**前两局**（`Meta.Runs <= 2`）各讲一次。
    ///
    /// 以前是「第一个存档只讲一次」——可那张卡讲的三件事（品质=词条数、词条才是构筑、
    /// 每五层一个 Boss）是**第一局里同时要用的三套概念**，一张卡讲完就再也不出现。
    /// 第一局死在第三层的人回来时，卡片和他都记不清那两句了。
    /// 第二局再讲一次，第三局起不再出现：讲第二遍是复习，讲第三遍就是烦人。
    /// 标记仍然跟着存档走，重启游戏不会让计数归零。
    /// </summary>
    private void ShowIntroIfFirstRun()
    {
        if (_store.IntroSeen && _store.Meta.Runs > 2) return;
        _store.MarkIntroSeen();
        ShowIntroCard();
    }

    /// <summary>
    /// 新手引导：三条最容易被漏掉的规则，各配一个图标。
    /// 只在第一次开局弹（TS 版同一条，标记键 abyss_echo_intro_seen）。
    /// </summary>
    private void ShowIntroCard()
    {
        var (root, body) = Widgets.Overlay(this, "深渊回响 · 启程", 640);
        ShowModal(root);
        (string Icon, string Title, string Desc)[] lines =
        {
            ("sword", "品质看颜色", "白装没有词条；蓝装 1 条、紫装 2 条。颜色越亮，可挂的词条越多。"),
            ("heart", "词条才是构筑", "「攻击 +X」之外，词条会改暴击、治疗、抗性——同色的两把剑可能完全不同。"),
            ("coin", "下一层更深", "每 5 层一个 Boss，敌人随层数变强。紫装通常出现在精英房与 Boss 房。"),
        };
        foreach (var (icon, title, desc) in lines)
        {
            body.AddChild(Widgets.IconWithBody(icon, Palette.Gold, title, desc, 440, 16, 30));
        }
        // 按钮与截图钩子走同一条关闭路径：以前这里直接 QueueFree，
        // 而 `_modal` 只在 TreeExited 里被清——节点排队释放的那几帧里，
        // `_modal is not null` 仍然拦着按键，截图工具于是把这张卡当成「还开着」。
        body.AddChild(UiTheme.Btn("开始探索", CloseModal, 48));
    }

    /// <summary>
    /// 地图上该放哪一段：只看「这一层的机制」。
    /// 第二个参数是「是不是 Boss 层」，不是「是不是最终层」——
    /// 以前传的是 Floor >= FinalFloor，于是第 1~24 层永远当不成 Boss 层。
    /// </summary>
    private void EnsureFloorMusic() =>
        Sfx.Music(Bgm.CueFor(_run.FloorMechanicId, _run.IsBossFloor, false));

    private bool _mapLayerBuilt;

    /// <summary>这一帧建房间层时算出来的「每条岔路底下还剩几场仗」，与房间下标对齐</summary>
    private int[] _pendingThisBuild = Array.Empty<int>();

    /// <summary>
    /// 首帧布局之后再建房间层：_Ready 那一刻 Size 还是 0，
    /// 而房间格的位置是用 Size 算出来的，直接建会把整张网格算到视口外面。
    /// </summary>
    public override void _Process(double delta)
    {
        if (_mapLayerBuilt || Size.X <= 0 || Size.Y <= 0) return;
        _mapLayerBuilt = true;
        BuildRoomLayer();
        BuildPlayerDot();
        UpdatePlayerPosition();
        RefreshHud();
    }

    // ---- 结构 ----

    private void BuildHud()
    {
        // HUD 条带不用 StyleBox：自绘件能画出「上亮下暗 + 贴边发光线」，
        // 而纯色 StyleBox 与背景只差几档亮度，玩家分不清哪里是面板。
        // 这里 PanelContainer 只负责内边距与布局，条带本身画在它下面一层。
        var top = new PanelContainer();
        top.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0),
            ContentMarginLeft = 22, ContentMarginRight = 22,
            ContentMarginTop = 8, ContentMarginBottom = 8,
        });
        AddChild(top);
        _topHud = top;
        // 内容层必须**压在自己的条带之上**。
        //
        // 以前谁在上是「谁先 AddChild」决定的：条带后加 → 条带在上 → 它把地板文字、
        // 血条、经验条整块盖住。截图里 HUD 是空的摆了不知道多少轮，而
        // 血条的经验值、攻击力、金币这些数一直在写、也一直在画——**只是被垫在上面了**。
        // 上一轮把层序收进 `StageOrder` 时只顾着「面板压背景」，漏了这一步：
        // 面板的底和面板的内容是**两层**，`Panel` 说的是底那一层。
        top.ZIndex = Layers.Of(StageOrder.PanelContent);
        // 条带本体：垫在内容下面、与内容同一个 rect。用自绘件是因为它要画
        // 「纵向渐变 + 斜纹理 + 贴边发光线」，StyleBoxFlat 一样都办不到。
        // 条带是「面板」的底：和战斗里那两块一样，它得压在背景层之上
        // （地图底是 AmbienceBackdrop，同层 0 时谁先加谁在下）。
        var topBand = new HudBand
        {
            Name = "HudBand", LineAtBottom = true, LineTint = Palette.Accent,
            ZIndex = Layers.Of(StageOrder.Panel),
        };
        AddChild(topBand);
        _topBand = topBand;
        topBand.AnchorLeft = 0; topBand.AnchorRight = 1; topBand.AnchorTop = 0; topBand.AnchorBottom = 0;
        topBand.OffsetLeft = 0; topBand.OffsetRight = 0; topBand.OffsetTop = 0; topBand.OffsetBottom = HudHeight;
        top.MoveToFront();

        // TopWide：锚点撑满左右、贴顶，高度 74（四条 offset 手写，见 BattleScene 里的同一处坑）
        top.AnchorLeft = 0; top.AnchorRight = 1; top.AnchorTop = 0; top.AnchorBottom = 0;
        top.OffsetLeft = 0; top.OffsetRight = 0; top.OffsetTop = 0; top.OffsetBottom = HudHeight;
        top.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", 22);
        top.AddChild(row);

        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        left.AddThemeConstantOverride("separation", 4);
        row.AddChild(left);

        _floorText = UiTheme.Text("", 18, Palette.Gold);
        left.AddChild(_floorText);

        // 血条与经验条各占一行，左端对齐到 HUD 的最左边缘。
        // 早先四条（条、数字、条、数字）全塞在同一个 HBox 里：经验条被挤到 330px 处，
        // 于是 HUD 左上角是「一条血、一半空、一条经验」——两条本该并排读的比例尺
        // 变成了两段错开的色带，玩家分不清哪条是谁的。
        var hpRow = new HBoxContainer();
        hpRow.AddThemeConstantOverride("separation", 8);
        left.AddChild(hpRow);
        // 两条进度条不许被 HBox 拉高：这一排的高度由内容里最高的那个决定（`_hpText` 有 20px 高），
        // 而 `ProgressBar` 的最小高是 0，容器就把多出来的 4px 摊给了条子——
        // 16px 的血条和 8px 的经验条于是都长成 17px，HUD 左上角是「两条一样粗的条」。
        // `SizeFlagsVertical = ShrinkCenter` 让它按自己声明的高度站好，别跟着一行拉。
        _hpBar = UiTheme.Bar(Palette.Danger, 16);
        _hpBar.CustomMinimumSize = new Vector2(230, 16);

        hpRow.AddChild(_hpBar);
        _hpText = UiTheme.Text("", 12, Palette.Muted);
        hpRow.AddChild(_hpText);

        var xpRow = new HBoxContainer();
        xpRow.AddThemeConstantOverride("separation", 8);
        left.AddChild(xpRow);
        _xpBar = UiTheme.Bar(Palette.Accent, 8);
        _xpBar.CustomMinimumSize = new Vector2(230, 8);

        xpRow.AddChild(_xpBar);
        _xpText = UiTheme.Text("", 12, Palette.Muted);
        xpRow.AddChild(_xpText);

        // 楼层机制放在**右上**，不放在左列第四行。
        //
        // 它在左列里排第四行的时候，HUD 条带（74px）装不下：地图画幅的可用高度是自动算的，
        // 多一行文字就等于多要 17px，而 74 这个数正是从「画幅要多大」倒推出来的配额。
        // 塞不下的结果是**整条 HUD 被挤成两截**：血条和机制说明叠在一起，
        // 左边一列「第 1 层 / 血条 / Lv 1」挤成三行，右边「攻击 8 防御 2 金币 20」被推到屏幕外。
        // 现在它占右上角的第二行——那一列本来只有两行（属性 + 背包），下面是空的。
        // 机制是「这一层要你多想什么」，它和属性并列，比塞在血条下面更该被看见。
        _mechText = UiTheme.Text("", 12, Palette.AccentSoft);
        _mechText.HorizontalAlignment = HorizontalAlignment.Right;
        _mechText.AutowrapMode = TextServer.AutowrapMode.Off;
        _mechText.ClipText = true;
        // HBox 按最小宽分配：右列里最长的是「攻击 8 防御 2 金币 20」，机制名比它长得多，
        // 不给一个**上限**它就会把左列挤到只剩 60px（第一版跑出来：血条缩成一条、
        // 「第 1 层 / 15」被截成「第 1 」。）。上限按屏宽的比例取，字自然跟着窗口走。
        _mechText.CustomMinimumSize = new Vector2(0, 0);

        // 右列固定宽度：它是「读数」，不是「可伸缩的内容」。
        // 不给宽度时 HBox 按**内部最长的那一行**分份额，而它会随机制名变长——
        // 第一版跑出来左列被挤成 60px（血条缩成一条、「第 1 层 / 15」被截断）。
        // 240 是「攻击 8 防御 2 金币 20」在 15px 下的实测宽度再加一点，够读数也够机制名。
        var right = new VBoxContainer { CustomMinimumSize = new Vector2(240, 0) };
        right.AddThemeConstantOverride("separation", 4);
        row.AddChild(right);
        _statText = UiTheme.Text("", 15, Palette.Text);
        _statText.HorizontalAlignment = HorizontalAlignment.Right;
        right.AddChild(_statText);
        _bagText = UiTheme.Text("", 13, Palette.Muted);
        _bagText.HorizontalAlignment = HorizontalAlignment.Right;
        right.AddChild(_bagText);
        right.AddChild(_mechText);

        // 底部提示条：一句话说清「刚才发生了什么」
        var bottom = new PanelContainer();
        bottom.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0),
            ContentMarginLeft = 16, ContentMarginRight = 16,
        });
        AddChild(bottom);
        _bottomHint = bottom;
        // 同顶部 HUD：内容是「面板」之上的一层，不靠添加顺序抢
        bottom.ZIndex = Layers.Of(StageOrder.PanelContent);
        bottom.AnchorLeft = 0; bottom.AnchorRight = 1; bottom.AnchorTop = 1; bottom.AnchorBottom = 1;
        bottom.OffsetLeft = 0; bottom.OffsetRight = 0; bottom.OffsetTop = -HintHeight; bottom.OffsetBottom = 0;
        var hintBand = new HudBand
        {
            Name = "HintBand", LineAtBottom = false, LineTint = Palette.AccentSoft, CornerTicks = false,
            ZIndex = Layers.Of(StageOrder.Panel),
        };
        AddChild(hintBand);
        _hintBand = hintBand;
        hintBand.AnchorLeft = 0; hintBand.AnchorRight = 1; hintBand.AnchorTop = 1; hintBand.AnchorBottom = 1;
        hintBand.OffsetLeft = 0; hintBand.OffsetRight = 0; hintBand.OffsetTop = -HintHeight; hintBand.OffsetBottom = 0;
        bottom.MoveToFront();
        // 名字是给冒烟用的：它要断言「提示条里真的写了『还差几场』」——
        // 找 Label 得靠名字，不然一旦别的标签先建出来，取到的就是另一个。
        _hint = UiTheme.Text("", 14, Palette.Muted);
        _hint.Name = "HintBar";
        _hint.VerticalAlignment = VerticalAlignment.Center;
        // 提示条默认会贴着左边缘：截图里「在「起点」·…」的「在」被切掉半个字。
        // 给左右留边距，和顶部 HUD 的内边距对齐。
        var hintPad = new MarginContainer();
        hintPad.AddThemeConstantOverride("margin_left", 16);
        hintPad.AddThemeConstantOverride("margin_right", 16);
        hintPad.AddChild(_hint);
        bottom.AddChild(hintPad);

        // 浮动提示：TS 版是一条会自己淡出、会排队的 toast。
        // 这里原来是一个永远只有最后一句的 Label——连着点两下，前一句就被后一句盖掉，
        // 而「那边没有路」这种重复消息又不能靠不断重播来提醒。
        // 队列与节流的规则在 UI/ToastRules（纯函数，可断言），落点也在那儿。
        _toast = new ToastLayer { Name = "ToastLayer", ZIndex = Layers.Of(StageOrder.Toast), HintBandHeight = HintHeight };
        // 提示挂在网格下面，不是挂在提示条上面：地图垂直居中，最后一排砖会一直长到
        // 提示条跟前（1280×720 下 y≈628），两者之间根本没有一条「空带」。
        SyncToastAnchor();
        AddChild(_toast);
    }

    public override void _ExitTree()
    {
        // 场景被停掉 / 重开时把队列收干净：残留的「正在演」标记会把之后所有提示吞掉
        // （TS 版 Issue #61 的症状就是这一条）。
        _toast?.Clear();
    }

    private Vector2 RoomCenter(Room room) => _metrics.Center(room.X, room.Y);

    /// <summary>
    /// 房间实际占用的包围盒（最小列 / 最小行 / 列数 / 行数）。
    /// 地图是稀疏的，包围盒比声明的网格小一圈，居中必须按它算。
    /// </summary>
    private (int MinX, int MinY, int Cols, int Rows) MapBounds()
    {
        var rooms = _run.Map.Rooms;
        var minX = int.MaxValue; var minY = int.MaxValue;
        var maxX = int.MinValue; var maxY = int.MinValue;
        foreach (var r in rooms)
        {
            if (r.X < minX) minX = r.X;
            if (r.Y < minY) minY = r.Y;
            if (r.X > maxX) maxX = r.X;
            if (r.Y > maxY) maxY = r.Y;
        }
        if (rooms.Count == 0) return (0, 0, 1, 1);
        return (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>
    /// 重算这一帧的格距与原点。
    /// 尺寸来自「可用区域」而不是写死 92：上一版一张 4×4 的图只画 450px，
    /// 1280 宽里两边各空 400 多，玩家看到的是一张小图漂在大屏中间。
    /// 这条也是 UiSmoke 的判据（`MapMetrics.Coverage`）——画幅占比是可算的数。
    /// </summary>
    private void UpdateMetrics()
    {
        var view = Size.X > 1f && Size.Y > 1f
            ? Size
            : GetViewport().GetVisibleRect().Size;
        var (minX, minY, cols, rows) = MapBounds();
        _metrics = MapMetrics.Fit(view, HudHeight, HintHeight, minX, minY, cols, rows,
            MapLegend.WidthFor(MapLegend.ColumnsFor(view)) + 36f, MaxTile, MinTile);
    }

    private void BuildRoomLayer()
    {
        foreach (var child in _roomLayer.GetChildren())
        {
            _roomLayer.RemoveChild(child);
            child.QueueFree();
        }
        var map = _run.Map;
        UpdateMetrics();
        var reachable = new HashSet<int> { _run.PlayerIndex };
        foreach (var n in map.Links[_run.PlayerIndex]) reachable.Add(n);
        // 「这条岔路底下还剩几场仗」：岔口该往哪边走，地图生成那一刻就已经知道。
        // 以前这个数只存在于玩家自己的记忆里，于是唯一的探索方式是「挨个死胡同撞一遍」。
        _pendingThisBuild = _run.PendingBattlesBelow();

        // 走廊先画：后画的房间块要盖住线头，视觉上才是「路铺在房间底下」。
        // 原版只连「至少一端已探索」的房间，未探索的那截几乎只剩暗影——迷雾感靠这个，
        // 不是靠把线画淡。上一版是一条 2px 直线，十几个房间看着像电路图。
        _lineLayer.Clear();
        for (var idx = 0; idx < map.Rooms.Count; idx++)
        {
            foreach (var to in map.Links[idx])
            {
                if (to <= idx) continue;
                var a = RoomCenter(map.Rooms[idx]);
                var b = RoomCenter(map.Rooms[to]);
                var known = map.Rooms[idx].Visited || map.Rooms[to].Visited;
                _lineLayer.AddLine(a, b, known, Palette.AccentSoft);
            }
        }
        foreach (var to in map.Links[_run.PlayerIndex])
            _lineLayer.AddStep((RoomCenter(map.Rooms[_run.PlayerIndex]) + RoomCenter(map.Rooms[to])) / 2f, Palette.AccentSoft);
        _lineLayer.Set(_metrics.Span);

        for (var i = 0; i < map.Rooms.Count; i++)
        {
            var room = map.Rooms[i];
            var index = i;
            var size = new Vector2(_metrics.Room, _metrics.Room);
            // 点击区比可见的砖大一圈：手指/鼠标不需要精确命中地砖边缘，
            // 但也不能重叠（重叠会让相邻房间抢点击）。命中区就是格距本身。
            var hitSize = new Vector2(_metrics.Span, _metrics.Span);
            var hit = new Button
            {
                Flat = true,
                Text = "",
                Position = RoomCenter(room) - hitSize / 2f,
                Size = hitSize,
                MouseFilter = MouseFilterEnum.Stop,
                TooltipText = $"{MapGen.RoomLabel[room.Type]}{(room.Cleared ? "（已处理）" : "")}",
            };
            var blank = new StyleBoxEmpty();
            hit.AddThemeStyleboxOverride("normal", blank);
            hit.AddThemeStyleboxOverride("hover", blank);
            hit.AddThemeStyleboxOverride("pressed", blank);
            hit.AddThemeStyleboxOverride("focus", blank);

            // 房间块本身是一个 `_Draw` 节点：底板、砖缝、徽章、图标、状态角标都在一处。
            // 上一版底板在 StyleBox 上、图标在子节点里，两层圆角对不齐，整块发灰。
            var tile = new RoomTile
            {
                Kind = IconArt.ForRoom(room.Type.ToString()),
                Tint = RoomColor(room, index),
                Accent = MapLegend.AccentFor(room.Type),
                Visited = room.Visited,
                Cleared = room.Cleared,
                Reachable = reachable.Contains(index) && index != _run.PlayerIndex,
                IsStart = room.Type == RoomType.Start,
                IsEnemy = room.Type is RoomType.Enemy or RoomType.Elite,
                Standing = index == _run.PlayerIndex,
                // 骑士站的那一格不画房间图标：「起点」标签和图标都在徽章那套坐标里，
                // 立绘正好从中间劈过去（截图放大看，那块金字断成两半）。
                // 自己这一格还挡路时不标红点：它本身就是那场仗，红点说的是「这边还有」。
                // 不减去自己的话，每间没打的敌人房右下角都挂一个「1」——那是废话。
                PendingBelow = PendingBelowHere(index, room),
                OnBossPath = OnPathToBoss(index, room),
                // 未探索的砖只报「这里有什么值得去」，不报「那边有几场仗」。
                // 判据写在一处：`FaintTypeFor`。地图上凡是「值不值得走一趟」与
                // 「这一趟有多贵」的分界，都从这里读。
                FaintType = FaintTypeFor(room),
                FaintAccent = MapLegend.AccentFor(room.Type),
                MouseFilter = MouseFilterEnum.Ignore,
                Size = size,
                Position = (hitSize - size) / 2f,
            };
            hit.AddChild(tile);
            hit.MouseEntered += () => { tile.Hovered = true; tile.QueueRedraw(); };
            hit.MouseExited += () => { tile.Hovered = false; tile.QueueRedraw(); };
            hit.Pressed += () => OnRoomClicked(index);
            _roomLayer.AddChild(hit);
        }

        // 图例：原版地图右下角有一张 10 行的类型图例。Godot 版一直没有——
        // 玩家只能靠图标猜「这个货摊是不是商店」。位置按视口算，贴着提示条上方。
        _legend?.QueueFree();
        var view = Size.X > 1f && Size.Y > 1f ? Size : GetViewport().GetVisibleRect().Size;
        // 图例是「浮在网格之上的一层说明牌」：房间砖（含玩家那一刻）都在默认的 0 层，
        // 图例与它同层时只靠「谁后 AddChild」定先后——重建房间层一次就可能被砖压住，
        // 而那种错只在截图里看得出来。这一层说清楚它自己是谁。
        _legend = new MapLegend { Name = "MapLegend", ZIndex = Layers.Of(StageOrder.Legend) };
        AddChild(_legend);
        // 两列的门槛看宽度：1024 宽地图本身要 500px，再让图例占 236 就挤了
        var columns = MapLegend.ColumnsFor(view);
        var legendRows = (11 + columns - 1) / columns;
        var legendSize = new Vector2(MapLegend.WidthFor(columns), MapLegend.HeightFor(legendRows));
        // 两列时它是贴在底部的一条横带，单列时才是右下角的竖带：
        // 横屏下竖带会独吃一块宽度，把地图挤成「漂在中间的小图」。
        // 底边对齐到**底部提示条的上沿**：提示条占着最后 46px，而这里原先只让了 12px，
        // 图例于是骑在那条提示上——「在「起点」·最近可去……」那半句的末两格被它切成了两截。
        // 让出整条 + 12px 的气，图例与提示条才是两块各站各的。
        _legend.Position = new Vector2(view.X - legendSize.X - 18f, view.Y - HintHeight - legendSize.Y - 12f);
        _legend.Size = legendSize;
        // 网格这一帧落在哪：提示层要知道它上下沿，提示才挂得到砖外面（见 ToastRules.PillTopFor）
        SyncToastAnchor();
    }

    /// <summary>
    /// 未探索的砖上露不露类型：露 = 这一格的那条路值得走；不露 = 走了才知道。
    ///
    /// 分界线是「信息属于谁」：补给（商店 / 训练 / 祭坛）、宝箱、事件、诅咒、Boss——
    /// 它们说的是**方向**，玩家据此决定往哪边去，藏起来只会变成走迷宫；
    /// 而敌人房与精英房说的是**代价**，代价要走进去才知道，这是这类游戏里
    /// 唯一一个「探路」真正值钱的地方。以前两者都露，于是迷雾只是装饰。
    /// </summary>
    public static RoomType? FaintTypeFor(Room room) => room.Type switch
    {
        RoomType.Empty or RoomType.Enemy or RoomType.Elite or RoomType.Start => null,
        _ => room.Type,
    };

    /// <summary>
    /// 这一格是不是「从玩家脚下到 Boss 的必经之路上的下一间战斗房」。
    ///
    /// 红点说的是「这边还有多少仗」，是数量；玩家真正要做的决定是「哪一场躲不过」，
    /// 是位置。一层 11 间房、3.2 场仗，其中只有 1.1 场挡在去 Boss 的路上——
    /// 这个数游戏一直算得出来（`BattlesToBoss`），却只报了个总数。
    /// </summary>
    private bool OnPathToBoss(int index, Room room)
    {
        if (room.Cleared) return false;
        if (room.Type is not (RoomType.Enemy or RoomType.Elite or RoomType.Boss)) return false;
        if (room.Type == RoomType.Boss && _run.BossStage(_run.Floor) is null) return false;
        var dist = MapGen.BfsDistances(_run.Map.Links, _run.PlayerIndex);
        var boss = _run.Map.BossIndex;
        if (boss < 0 || dist[boss] < 0) return false;
        // Boss 已经在脚下了（玩家正站在 Boss 房门口）：这一格本身就是那一条路的终点
        return dist[index] == dist[boss] - 1;
    }

    /// <summary>
    /// 这一格右下角该标的数：这条岔路底下还剩几场仗，减去「它自己」。
    /// 站在起点时也要算：玩家在地图上第一眼就该看见「往左还有 2 场、往右只有 1 场」。
    /// </summary>
    private int PendingBelowHere(int index, Room room)
    {
        var total = _pendingThisBuild[index];
        // 只在「自己还挡路」时减掉自己，别的情况下 pending 里本来就没算它
        var selfBlocks = !room.Cleared && room.Type is RoomType.Enemy or RoomType.Elite
                         || (room.Type == RoomType.Boss && !room.Cleared && _run.BossStage(_run.Floor) is not null);
        return selfBlocks ? total - 1 : total;
    }

    private Color RoomColor(Room room, int index)
    {
        if (index == _run.PlayerIndex) return Palette.Gold;
        if (!room.Visited) return Palette.Dim;
        if (room.Cleared) return new Color("4a5670");
        return room.Type switch
        {
            RoomType.Enemy => Palette.Danger,
            RoomType.Elite => Palette.Crit,
            RoomType.Boss => Palette.Boss,
            RoomType.Treasure => Palette.Gold,
            RoomType.Shop => Palette.Accent,
            RoomType.Event => Palette.AccentSoft,
            RoomType.Altar => Palette.Good,
            RoomType.Training => Palette.Good,
            RoomType.Curse => new Color("c39bff"),
            _ => Palette.Muted,
        };
    }

    private void BuildPlayerDot()
    {
        // 位置由 UpdatePlayerPosition 定，这里只建节点。
        // 它挂在 _actorLayer 上，所以重建房间层不会再把它一起清掉。
        // 骑士立绘替掉一个 14px 的方块：地图上「你」是个方块时，
        // 玩家很难把地图和战斗里的自己当成同一个角色。
        // 尺寸必须按「人在这个框里有多高」算：
        // 画 0.66 倍时骑士只有 58*0.66 ≈ 38px 高，头在框内 y≈24、脚在 y≈62。
        // 上一版给的是 48×64 的框，人挤在框的下半截，于是地图上看到的
        // 是一个「头盔顶出房间格、身子压在格子里」的虫子。
        // 尺寸跟格距走：格距缩到 44 时还按 40×44 画，骑士会从格子里顶出去。
        // 盒子比例照骑士本体（58 高、连投影 65），以前用 0.9 倍高的窄盒子，
        // 人被横向压扁——地图上那个「蓝团子」就是这么来的。
        var bb = new Vector2(_metrics.Room * 0.86f, _metrics.Room * 0.98f);
        var hero = new HeroSprite { Size = bb, CustomMinimumSize = bb };
        // 比例尺要按「整套设计坐标」算（含脚下投影 22px），否则人比格子高一截，
        // 走进下一格时头盔会压住上面那块地砖——截图里看得一清二楚。
        hero.SetHeroScale(Mathf.Min(bb.X / 40f, bb.Y / HeroArt.LayoutHeight) * 0.98f);
        hero.ZIndex = Layers.Of(StageOrder.Actor);
        _actorLayer.AddChild(hero);
        _playerDot = hero;
    }

    private void UpdatePlayerPosition()
    {
        var center = RoomCenter(_run.CurrentRoom);
        // 站在格子里：位置按「脚」算——骑士的脚在控件框底部，
        // 所以只要把框底对齐到格子下沿稍微收一点，人就站在格子里。
        // 差 2px 的余量给投影：投影落在格内，人就不会看起来站到下一格去
        var box = _playerDot.Size;
        _playerDot.Position = center + new Vector2(-box.X / 2f, _metrics.Room / 2f - box.Y + 1f);
        if (_playerDot is HeroSprite hs) hs.PlayStep();
    }

    // ---- 刷新 ----

    private void RefreshHud()
    {
        var stats = _run.Stats;
        var p = _run.Player;
        _floorText.Text = _run.Floor >= _run.FinalFloor
            ? $"第 {_run.Floor} 层 · 深渊之底"
            : $"第 {_run.Floor} 层 / {_run.FinalFloor}";
        if (_run.Loop > 1) _floorText.Text += $" · 第 {_run.Loop} 周目";

        _hpBar.MaxValue = Math.Max(1, stats.MaxHp);
        _hpBar.Value = p.Hp;
        _hpText.Text = $"{p.Hp}/{stats.MaxHp}";
        var xp = _run.Xp;
        _xpBar.MaxValue = Math.Max(1, xp.Needed);
        // 截图用：把经验条填满，让它在图上看得见（真实开局是 0/30，填充是空的）
        _xpBar.Value = _shotMode ? xp.Needed : xp.Current;
        _xpText.Text = $"Lv {p.Level} · {xp.Current}/{xp.Needed}";

        var mech = _run.FloorMechanicInfo;
        // 机制只报名字：一整个 HUD 条带里塞一段 60 字的说明，字会小到读不出。
        // 详细说明在 `H` 里（帮助面板），`H` 里那块是「这一层要你多想什么」的正文。
        _mechText.Text = _run.FloorMechanicId == "none" ? "" : $"机制「{mech.Name}」";
        var curse = _run.LoopCurse;
        if (curse is not null) _mechText.Text += $" · 周目「{curse.Name}」";

        // 本层的临时加成必须写在面板上：苦练 +15%、疫病层 +4 都会让实际伤害
        // 高于「攻击 xx」这个数字，玩家看不到来源就会以为是数值算错了。
        var bonus = _run.TrainingAttackBuff > 0
            ? $"  ·  苦练 +{(int)Math.Round(_run.TrainingAttackBuff * 100)}%"
            : "";
        _statText.Text = $"攻击 {stats.Attack}  防御 {stats.Defense}  金币 {p.Gold}{bonus}";
        _bagText.Text = $"背包 {p.Inventory.Count}/{_run.Capacity}";
        // 指向未处理的房间：地图一屏十几格，玩家最常问的一句是「我下一步该走哪边」。
        // 这里按 BFS 找最近一间「还没清空」的房，直接说方向——是提示，不是限制。
        var next = NearestPendingRoom();
        var toward = next is null ? "本层已清空" : $"最近可去：{MapGen.RoomLabel[_run.Map.Rooms[next.Value].Type]}（{DirectionTo(next.Value)}）";
        var left = _run.RemainingBattleRooms;
        // 剩几场仗之外，还要说「在哪一边」：一层里一半的房间是死胡同，
        // 只说数量的话，玩家仍然得挨个走进去看一眼。「最近可去」也有同样的毛病——
        // 它挑的是图上的最近步数，可能是间宝箱房，而三场仗都在另一边。
        var side = RemainingSides();
        // 「剩余战斗房 3」和「下楼还要打 1 场」是两个数，而以前只报前者。
        // 玩家的读法是前者——于是把 3 场都打完，中途被消耗掉，再也没走到 Boss 面前。
        // 后者才是这一层的真实票价（40 种子 × 14 层：平均 1.1 场），必须说出来。
        var toBoss = _run.BattlesToBoss();
        var toll = toBoss > 0 ? $"（下楼还差 {toBoss} 场）" : "";
        // 小屏上再塞一句会被吃掉尾巴，而 `I 背包 · > 下楼` 那半句是操作提示，丢不得。
        // 所以只有宽屏才多说一句「剩下的路有多贵」——那是这一轮真正新增的信息。
        var wide = (Size.X > 1f ? Size.X : GetViewport().GetVisibleRect().Size.X) >= 1100f;
        var cost = wide ? RemainingCostBySide() : "";
        _hint.Text = $"在「{MapGen.RoomLabel[_run.CurrentRoom.Type]}」· {toward} · 剩余战斗房 {left}{toll}{side}{cost} · I 背包 · > {(_run.Floor >= _run.FinalFloor ? "继续深潜" : "下楼")}";
    }

    /// <summary>
    /// 最近一间「还没处理完」的房间（BFS，按图上的步数算，不是直线距离）。
    /// 已清空的房间不算：清完再让玩家走回去，提示就成了噪声。
    /// </summary>
    private int? NearestPendingRoom()
    {
        var links = _run.Map.Links;
        var dist = MapGen.BfsDistances(links, _run.PlayerIndex);
        var best = -1;
        for (var i = 0; i < links.Count; i++)
        {
            if (dist[i] <= 0) continue;
            var room = _run.Map.Rooms[i];
            if (room.Cleared) continue;
            if (room.Type == RoomType.Start) continue;
            if (best < 0 || dist[i] < dist[best]) best = i;
        }
        return best < 0 ? null : best;
    }

    /// <summary>
    /// 「剩下的仗分别在哪一边」：站在有岔口的地方时，把每个相邻房间
    /// 底下还压着几场仗报出来，例如 `（右 2 · 左 1）`。
    /// 不是岔口就什么都不说——一句话里塞三个方向，比不说还难读。
    /// </summary>
    private string RemainingSides()
    {
        var pending = _run.PendingBattlesBelow();
        var cur = _run.CurrentRoom;
        var parts = new List<string>();
        foreach (var n in _run.Map.Links[_run.PlayerIndex])
        {
            if (pending[n] <= 0) continue;
            var dx = _run.Map.Rooms[n].X - cur.X;
            var dy = _run.Map.Rooms[n].Y - cur.Y;
            var dir = dx > 0 ? "右" : dx < 0 ? "左" : dy > 0 ? "下" : "上";
            parts.Add($"{dir} {pending[n]}");
        }
        // 只有一个方向时就别加了：那句话在「最近可去」里已经说过一遍
        return parts.Count >= 2 ? $"（{string.Join(" · ", parts)}）" : "";
    }

    /// <summary>
    /// 「剩下的仗要花多少」：每条岔路底下压着的那几场，按本层实测的掉血比例折算成血量。
    ///
    /// 「剩余战斗房 3」和「这 3 场大概要掉 130 点血」是两句话。玩家看到前一句，
    /// 得自己估后面那半句——而估这半句需要的东西（敌人面板、防御、浮动、暴击、机制）
    /// 全在程序里。它一直算得出来，只是没人问过它。
    ///
    /// 折算用的是 `RoomCostRatio`：**同一份实测**，探针也在用它，
    /// 于是打出来的平衡数字与屏上的那句话指向的是同一个东西。
    /// 数字只加在「净掉血 > 0」的那几档上，空房间不给 0 分——0 分会让玩家以为那条路是免费的。
    /// </summary>
    private string RemainingCostBySide()
    {
        var pending = _run.PendingBattlesBelow();
        var cheap = 0;
        var big = 0;
        foreach (var n in _run.Map.Links[_run.PlayerIndex])
        {
            if (pending[n] <= 0) continue;
            var ratio = _run.RoomCostRatio(_run.Map.Rooms[n].Type);
            var hp = (int)Math.Round(_run.Stats.MaxHp * ratio * pending[n]);
            if (hp <= 0) continue;
            if (ratio >= 0.25) big += hp; else cheap += hp;
        }
        if (cheap <= 0 && big <= 0) return "";
        var parts = new List<string>();
        if (cheap > 0) parts.Add($"{cheap}");
        if (big > 0) parts.Add($"{big}");
        return $" · 剩下的仗约 {string.Join(" + ", parts)} 点血";
    }

    /// <summary>从当前房间走到目标房间的第一方向（只说一步，不说全程）</summary>
    private string DirectionTo(int index)
    {
        var cur = _run.CurrentRoom;
        var target = _run.Map.Rooms[index];
        var dx = target.X - cur.X;
        var dy = target.Y - cur.Y;
        if (dx == 0 && dy == 0) return "就在脚下";
        if (Math.Abs(dx) >= Math.Abs(dy))
            return dx > 0 ? $"先往右键走 {Math.Abs(dx)} 格" : $"先往左键走 {Math.Abs(dx)} 格";
        return dy > 0 ? $"先往下键走 {Math.Abs(dy)} 格" : $"先往上键走 {Math.Abs(dy)} 格";
    }

    /// <summary>
    /// 一条反馈的三重确认之一（另两重是底部提示条与音效）。
    /// 队列、节流、落点全在 UI/ToastRules 里：这里只把文字和颜色交进去。
    /// </summary>
    private void ShowToast(string text, Color? color = null)
    {
        // 落点要按**这一帧**的布局算，而 SyncToastAnchor 读的是 _metrics —— 它在
        // BuildRoomLayer 之前可能还是上一帧（甚至是空的）。这里先刷新一次。
        UpdateMetrics();
        SyncToastAnchor();
        _toast.Show(text, color);
    }

    /// <summary>截图模式：把「本来就是 0」的进度条填满，好让它在图上可判</summary>
    private bool _shotMode;

    /// <summary>截图工具用：打开截图模式并重刷一次 HUD</summary>
    public void EnableShotMode()
    {
        _shotMode = true;
        RefreshHud();
    }

    /// <summary>
    /// 截图判据读的那两条提示文本（`02-map.png` 用）。
    ///
    /// 加这两个出口是因为这一轮新写在地图上的两句，都只有**肉眼**能看：
    /// 「迷雾里那块砖到底报了什么」与「剩下的仗约 N 点血」。前者是像素，
    /// 后者是文案——文案不落到一个读得到的地方，判据就只能靠人看，
    /// 而那正是这个仓库里最贵的一类回归（「三张图一起错，流水线全绿」）。
    /// </summary>
    public string HintTextForShot => _hint.Text;

    /// <summary>
    /// 迷雾里这一格报出来的类型（`null` = 什么都没说）。
    /// 判据拿它比「该藏的那几类」，而不是比颜色——颜色是渲染，类型才是规则。
    /// </summary>
    public string FaintKindForShot(int index)
    {
        var room = _run.Map.Rooms[index];
        var faint = room.Type is RoomType.Empty or RoomType.Enemy or RoomType.Elite or RoomType.Start
            ? null
            : (RoomType?)room.Type;
        return faint is null ? "" : faint.Value.ToString();
    }

    /// <summary>截图用：这一帧的格距与原点（提示落点那条判据要知道砖画到哪）</summary>
    public MapMetrics MetricsForShot => _metrics;

    /// <summary>把这一帧网格的下沿交给提示层：提示的落点由「砖画到哪」决定，不由面板边界决定</summary>
    private void SyncToastAnchor()
    {
        if (_toast is null || !GodotObject.IsInstanceValid(_toast)) return;
        // 图例是一块浮在地图右下角的面板：它和网格一样不许被提示盖住。
        // 1280×720 下网格画到 629、两列图例垂到 662，而提示条上沿在 674——
        // 「地图这一层画到哪」是这两条里更靠下的那条，不是网格自己的下沿。
        var legendBottom = _legend is not null && GodotObject.IsInstanceValid(_legend)
            ? _legend.Position.Y + _legend.Size.Y
            : 0f;
        _toast.GridBottom = _metrics.GridBottom;
        _toast.BelowFloor = MathF.Max(_metrics.GridBottom, legendBottom);
        _toast.GridTop = _metrics.GridTop;
        // 「网格上方那条空带」的上边界是顶部 HUD 的下沿，不是网格自己的上沿：
        // 少传这一个数，胶囊会按「离网格 Clearance」算，1280×720 下正好落进 HUD 里。
        _toast.HudBottom = HudHeight;
    }

    // ---- 输入 ----

    /// <summary>当前有没有弹窗。弹窗期间地图输入一律不响应，否则会「面板弹着人还在走」。</summary>
    private Control? _modal;

    private void ShowModal(Control root)
    {
        // 先关掉上一张再换新的：弹窗只有一个位置。
        //
        // 顺手关掉是这里唯一的正确做法——调 `ShowModal` 就表示「现在这一张才是要展示的」，
        // 旧的留着只会变成「屏幕上同时压着两张卡」。
        // 这一步以前不存在，于是「弹窗叠弹窗」这件事只取决于两件事的先后：
        //   · `ShowIntroCard` 走的是 `CallDeferred`（`_Ready` 之后一帧才弹），
        //     而那一帧恰好是**任何**别的弹窗可能被打开的帧——玩家按 I、按 Esc，
        //     界面上任何一个入口，以及冒烟与截图工具里那些「建好场景、隔一帧再开面板」的步骤。
        //   · 真实玩家路径也一样：引导卡弹出来的那一瞬按 H/Esc，就会得到两层。
        // 于是同一条判据（「屏上应当有 1 层覆盖层」）有时候绿、有时候红——而它红的时候
        // 报的是「实际 2」，看不出是哪两张。（红过的那一次正是 `深渊回响 · 启程 | 商店`：
        // 引导卡叠着商店面板，新人第一次开局就撞得上。）
        //
        // 为什么不去逐个入口检查「有没有上一张」：入口有二十多个，而「弹窗只有一个」
        // 是 `ShowModal` 这一个函数该守的不变式。判据写在别处，迟早漏一个。
        if (_modal is not null && GodotObject.IsInstanceValid(_modal) && !ReferenceEquals(_modal, root))
            CloseModal();
        _modal = root;
        root.TreeExited += () => { if (ReferenceEquals(_modal, root)) _modal = null; };
    }

    private void CloseModal()
    {
        if (_modal is null) return;
        var root = _modal;
        _modal = null;
        // 先摘再删，和 `CloseBattleForShot` 同一条理由：`QueueFree` 要到帧末才真的
        // 把节点从树上摘下来，而「已经关掉了」与「还盖着」在树上就成了同一件事——
        // 同一帧里问「屏上还有几层覆盖层」，答案永远是「旧的那层还在」。
        // 这不只是判据问早了：`Widgets.Overlays` 是截图工具与冒烟唯一的「屏上干净吗」，
        // 它数到一张已经没人管、下一帧就要消失的卡，就等于让判据去追一个正在跑的靶子。
        if (root.GetParent() is { } parent) parent.RemoveChild(root);
        root.QueueFree();
        Sfx.Play("click");
    }

    /// <summary>
    /// 原地等待。TS 版有这一手（空格）：它的用处是「停下来确认自己在哪」。
    /// 规则上什么都不改——不给回血、不给刷新，只把当前房间的处境说一遍。
    /// </summary>
    private void WaitHere()
    {
        Sfx.Play("click");
        var room = _run.CurrentRoom;
        var left = _run.RemainingBattleRooms;
        var tail = left > 0 ? $"本层还剩 {left} 间战斗房" : "本层已清空，按 > 下楼";
        ShowToast($"「{RoomLabel(room.Type)}」· 你停下来观察了一会儿 · {tail}", Palette.AccentSoft);
    }

    private static string RoomLabel(RoomType type) => type switch
    {
        RoomType.Start => "起点",
        RoomType.Empty => "空房间",
        RoomType.Enemy => "战斗房",
        RoomType.Treasure => "宝箱",
        RoomType.Shop => "商人",
        RoomType.Event => "异象",
        RoomType.Altar => "祭坛",
        RoomType.Training => "训练房",
        RoomType.Curse => "诅咒房",
        RoomType.Elite => "精英房",
        RoomType.Boss => "Boss 房",
        _ => "房间",
    };

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        // 弹窗开着时 Esc 要能关掉它（键位表写着「Esc 关闭面板」）。
        // 以前这里先被 _modal 拦下，结果是「面板弹着，Esc 和 H 一起失灵」。
        if (key.Keycode == Key.Escape && _modal is not null)
        {
            CloseModal();
            return;
        }
        if (key.Keycode == Key.H && _modal is not null)
        {
            // 暂停面板里按 H 直接看键位表：说明与实现是同一份数据
            CloseModal();
            ShowHelp();
            return;
        }
        if (_busy || _modal is not null || _run.IsGameOver()) return;

        // 空格原地等待：键位表里写着，TS 版也有（wait()）。以前只写在说明里没接。
        if (key.Keycode == Key.Space) WaitHere();
        else if (key.IsAction("move_up")) TryMove(0, -1);
        else if (key.IsAction("move_down")) TryMove(0, 1);
        else if (key.IsAction("move_left")) TryMove(-1, 0);
        else if (key.IsAction("move_right")) TryMove(1, 0);
        else if (key.IsAction("interact")) EnterRoom();
        else if (key.IsAction("descend")) TryDescend();
        else if (key.IsAction("bag")) OpenInventory();
        else if (key.Keycode == Key.H) ShowHelp();
        else if (key.Keycode == Key.M) ToggleMute();
        else if (key.Keycode == Key.F11) ToggleFullscreen();
        else if (key.Keycode == Key.Escape) OpenPause();
    }

    private void TryMove(int dx, int dy)
    {
        var (moved, room, index) = _run.Move(dx, dy);
        if (!moved || room is null)
        {
            ShowToast($"那边没有路（当前位置 {_run.CurrentRoom.X},{_run.CurrentRoom.Y} → 目标 {_run.CurrentRoom.X + dx},{_run.CurrentRoom.Y + dy}）", Palette.Danger);
            Sfx.Play("deny");
            return;
        }
        Sfx.Play("step");
        BuildRoomLayer();
        UpdatePlayerPosition();
        RefreshHud();
        // 第一次踏进房间就自动进：TS 版这里也是这样，
        // 让玩家少按一次键（不然「走过去」和「进去」是两件事，很容易忘）
        EnterRoom();
    }

    private void TryDescend()
    {
        // 已经结束的局不许再走动。
        //
        // 「一局结束了不能下楼」这条判据写在 Game.Descend() 里，而它会先查
        // 「这一层清空了吗」——于是玩家在地图上被打死的那一刻，如果那一层刚好已经清空
        // （事件房把人打死、祭坛的血祭把人耗死），按 `>` 仍然下得去：楼层继续涨，
        // 而状态永远是 Dead。UI 这条路先在这里拦一次，理由跟 Descend 里那句注释一样：
        // 规则只写对一半，迟早会被下一条路踩到。
        if (_run.IsGameOver())
        {
            ShowOutcome(victory: _run.Status == GameStatus.Won);
            return;
        }
        if (!_run.FloorCleared)
        {
            // 「还有房间没有处理」是一句玩家没法据以行动的话：他不知道还剩几间、
            // 也不知道在哪一边。同一个地图生成器已经把这两个答案都算出来了，
            // 而以前它们只被用在地图上的红点和提示条里，拒绝下楼这个现场一个字都不说。
            var left = _run.RemainingBattleRooms;
            var sides = RemainingSides();
            // 「下楼还差几场」也要说：剩 3 间战斗中可能只有 1 间挡在去 Boss 的路上，
            // 而玩家按「3」这个数去打，代价是整整一层楼的血量。
            var toll = _run.BattlesToBoss();
            var need = toll > 0 ? $"，打到 Boss 面前还差 {toll} 场" : "";
            ShowToast(left > 0 ? $"这一层还有 {left} 间战斗房没有处理{sides}{need}" : "这一层还有房间没有处理", Palette.Danger);
            Sfx.Play("deny");
            return;
        }
        if (_run.Floor >= _run.FinalFloor && _run.Status == GameStatus.Won)
        {
            _run.ContinueDive();
            _run.ResetFloorBuffsFromUi();
            ShowToast($"继续深潜 · 第 {_run.Loop} 周目", Palette.Accent);
        }
        else if (_run.FloorCleared)
        {
            var (ok, reason) = _run.Descend();
            if (!ok)
            {
                ShowToast(reason ?? "现在下不去", Palette.Danger);
                return;
            }
            var mech = _run.FloorMechanicInfo;
            ShowToast(string.IsNullOrEmpty(mech.Name) ? $"第 {_run.Floor} 层" : $"第 {_run.Floor} 层 · 「{mech.Name}」{mech.Desc}", Palette.AccentSoft);
            Sfx.Play("descend");
            // 新的一层：Boss 出场演出重新可用，BGM 也得跟着换（以前只有回地图那条路换曲）
            _bossIntroShown = false;
            EnsureFloorMusic();
            // 下楼是「一个可以被打断的安全点」：这时候落盘，玩家关窗也能从这一层接着走
            _store.SaveRun();
        }
        BuildRoomLayer();
        UpdatePlayerPosition();
        RefreshHud();
    }

    private void OnRoomClicked(int index)
    {
        GD.Print($"[click] room#{index} type={_run.Map.Rooms[index].Type} at=({_run.Map.Rooms[index].X},{_run.Map.Rooms[index].Y}) player=#{_run.PlayerIndex} busy={_busy} modal={_modal is not null}");
        if (_busy || _run.IsGameOver()) return;
        if (index == _run.PlayerIndex)
        {
            EnterRoom();
            return;
        }
        // 相邻才能走：点远处的格子不该瞬移（否则「树的形状」就白设计了）
        if (!_run.Map.Links[_run.PlayerIndex].Contains(index))
        {
            GD.Print($"[click] 不相邻：player#{_run.PlayerIndex} links=[{string.Join(",", _run.Map.Links[_run.PlayerIndex])}]");
            ShowToast("只能走向相邻的房间", Palette.Danger);
            Sfx.Play("deny");
            return;
        }
        var target = _run.Map.Rooms[index];
        var cur = _run.CurrentRoom;
        TryMove(target.X - cur.X, target.Y - cur.Y);
    }

    // ---- 房间分发 ----

    private void EnterRoom()
    {
        var room = _run.CurrentRoom;
        var index = _run.PlayerIndex;
        switch (room.Type)
        {
            case RoomType.Empty:
                ShowToast("空无一物的房间", Palette.Dim);
                break;
            case RoomType.Start:
            {
                var left = _run.RemainingBattleRooms;
                var action = _run.Floor >= _run.FinalFloor ? "继续深潜" : "下楼";
                ShowToast(left > 0 ? $"起点 · 本层还有 {left} 间战斗房，清空后按 > {action}" : $"起点 · 本层已清空，按 > {action}", Palette.AccentSoft);
                break;
            }
            case RoomType.Enemy:
            case RoomType.Elite:
            case RoomType.Boss:
                if (room.Cleared) ShowToast("这里已经被你清理过了", Palette.Dim);
                else StartBattle(index);
                break;
            case RoomType.Treasure:
                if (room.Cleared) ShowToast("宝箱已经空了", Palette.Dim);
                else OpenTreasure(index);
                break;
            case RoomType.Shop:
                // 清空过的商店也要能进：商店房永远不进 FloorCleared 的判据，
                // 而走过、买过之后房间会被标成 cleared（OpenEvent 同款），
                // 于是「卖完了再看一眼」就成了一个死房间——点了没反应，也不说为什么。
                if (room.Cleared) ShowToast("货架已经空了，商人正在打盹", Palette.Dim);
                else OpenShop(index);
                break;
            case RoomType.Event:
                if (room.Cleared) ShowToast("这里什么事情都没有发生", Palette.Dim);
                else OpenEvent(index);
                break;
            case RoomType.Altar:
                if (room.Cleared) ShowToast("祭坛已经沉寂了", Palette.Dim);
                else OpenAltar(index);
                break;
            case RoomType.Training:
                if (room.Cleared) ShowToast("木桩已经被你打到散架了", Palette.Dim);
                else OpenTraining(index);
                break;
            case RoomType.Curse:
                if (room.Cleared) ShowToast("诅咒之物已经离开了", Palette.Dim);
                else OpenCurseRoom(index);
                break;
        }
    }

    private void StartBattle(int index)
    {
        var room = _run.Map.Rooms[index];
        var enc = _run.CreateEncounter();
        if (enc is null)
        {
            ShowToast("这里没有敌人", Palette.Dim);
            Sfx.Play("deny");
            return;
        }
        _beatenBoss = room.Type == RoomType.Boss;
        _lastTier = enc.Tier;
        _lastMembers = enc.Allies.Count + 1;
        if (room.Type == RoomType.Elite)
        {
            var m = enc.Monster;
            ShowToast($"精英房 · {m.Name} 生命 {m.Hp} 攻击 {m.BaseAttack} 防御 {m.BaseDefense}", Palette.Crit);
        }

        _busy = true;
        // Boss 房先播出场演出：TS 版有独立的 BossIntroScene，直接切战斗会让玩家
        // 觉得「少了一段」——这是「玩家分不出是不是原版」里唯一纯观感的一条。
        if (room.Type == RoomType.Boss && !_bossIntroShown)
        {
            _bossIntroShown = true;
            Sfx.Play("boss");
            var intro = new BossIntro { ZIndex = Layers.Of(StageOrder.BattleRoot) };
            intro.Configure(enc.Def.Id);
            intro.Finished += () => MountBattle(enc);
            AddChild(intro);
            intro.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
            return;
        }
        MountBattle(enc);
    }

    // ---- 截图钩子（--shot 用；正常运行路径不会走这里）----

    /// <summary>截图用：按一次「进房间」，走的是玩家自己那条路（含战斗前的各种判定）</summary>
    public void EnterRoomForShot() => EnterRoom();

    public void ShowBattleForShot(EncounterDef enc) => MountBattle(enc);

    public void ShowBossIntroForShot(string bossId)
    {
        var intro = new BossIntro { ZIndex = Layers.Of(StageOrder.BattleRoot) };
        intro.Configure(bossId);
        AddChild(intro);
        intro.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    /// <summary>
    /// 截图用：弹一个真实的覆盖层，验面板 / 标题条 / 暗角。
    ///
    /// 上一版这里调的是 ShowIntroCard()，于是 `05-panel.png` 拍的是一张
    /// **独立于游戏流程之外的新手引导卡**——同一张卡随后还盖住了战斗和 Boss 出场，
    /// 五张图里三张画面是错的，而流水线只数张数。现在拍的是一次真的房间交互（商店）：
    /// 它由玩家自己的操作路径弹出来，也就随同一条路径关得掉。
    /// </summary>
    public void ShowPanelForShot(int extraLines = 0)
    {
        var index = _run.Map.Rooms.FindIndex(r => r.Type == RoomType.Shop && !r.Cleared);
        if (index < 0) index = _run.Map.Rooms.FindIndex(r => r.Type == RoomType.Shop);
        if (index < 0) { ShowToast("这一层没有商店房，弹窗截图跳过", Palette.Danger); return; }
        _run.TeleportForDebug(index);
        OpenShop(index, extraLines);
    }

    /// <summary>截图/冒烟用：打开背包面板——走的是玩家按 I 的同一个入口（`OpenInventory`）。</summary>
    public void OpenInventoryForShot() => OpenInventory();

    /// <summary>
    /// 截图用：弹一条真实的提示（走玩家那条路径，不是直接塞一行字进 Label）。
    /// 提示的落点见 `ToastRules.PillTopFor`：它必须挂在房间砖与图例**之外**。
    /// </summary>
    public void ShowToastForShot(string text) => ShowToast(text, Palette.Danger);

    /// <summary>
    /// 截图用：关掉当前弹窗。
    ///
    /// 这张卡是走 Overlay 弹的，它挂在场景根上、不是 BattleScene 的子节点，
    /// 所以 CloseBattleForShot 管不到它。少了这一步，后面那张「Boss 出场」拍到的
    /// 其实是新手引导卡盖在深渊之主身上——而 CI 只数张数（≥5 就绿），
    /// 于是一张内容完全不对的截图在流水线里躺了好几轮。
    /// </summary>
    public void CloseModalForShot() => CloseModal();

    /// <summary>
    /// 截图用：把新手引导关掉——走的是玩家自己那条路（`CloseModal`），
    /// 不是「从树上摘掉一个节点」。
    ///
    /// 为什么非有这一手：引导卡走 `Widgets.Overlay` 挂在**场景根**上，
    /// 而关闭它的按钮只有玩家点得到。截图工具绕过玩家路径直接拼场景，
    /// 于是这张卡一路盖到地图、战斗和 Boss 出场——三张图内容全错，
    /// 而流水线只数张数。
    ///
    /// 关完必须等一帧才能验证：`CloseModal` 是 `QueueFree`，节点退出场景树之前
    /// `Widgets.Overlays` 照样数得到它（同一件事上一轮踩过反面：排队释放那几帧里
    /// `_modal` 仍非空，按键一直被吞）。同一帧里问「关掉了吗」，答案永远是「没关」。
    /// </summary>
    public void CloseIntroForShot()
    {
        if (_modal is not null) CloseModal();
    }

    public void CloseBattleForShot()
    {
        // 立刻摘掉，而不是 `QueueFree`。
        //
        // `QueueFree` 要到**帧末**才真的把节点从树上摘下来，而截图状态机的下一步
        // 可能在同一帧里就问「屏上还有没有战斗」（`FindBattle` 读的是子节点列表）。
        // 于是「刚关掉」和「还挂着」在树上是同一件事——自爆那一屏拍完之后的补拍
        // 就栽在这里：`CloseBattleForShot()` 已经调过，`FindBattle` 仍然拿得到
        // 那个还在等着释放的节点。`RemoveChild` + `QueueFree` 两件事一起做，
        // 树上的事立刻生效，内存的事仍在帧末。
        foreach (var c in GetChildren())
        {
            if (c is not (BattleScene or BossIntro)) continue;
            RemoveChild(c);
            c.QueueFree();
        }
        // 地图的零件要放回来。这条走的是「直接摘掉战斗节点」而不是玩家的结算路径
        // （`OnBattleFinished` 也会放回来），两条路都得管——
        // 只管一条的话，截图工具关掉战斗之后那棵树就永远缺一块，
        // 而下一张图（弹窗）拍到的是一张没有图例、没有网格的地图。
        SetMapChromeVisible(true);
    }

    /// <summary>每层只播一次 Boss 出场：刷层时不会每把都看三秒动画</summary>
    private bool _bossIntroShown;

    /// <summary>
    /// 地图自己的所有可视零件：收起来 / 放出来。
    ///
    /// 战斗挂着的时候它们一个都不该出现。列举而不是建一个「地图根」再挂进去，
    /// 是因为这些层是逐个 `AddChild` 到本场景下的（各有各的层号与锚点），
    /// 搬家会连带改掉它们与 HUD 的对齐；这里只动 `Visible`，不碰结构。
    /// </summary>
    private void SetMapChromeVisible(bool visible)
    {
        foreach (var node in new Node?[]
                 {
                     _lineLayer, _roomLayer, _actorLayer, _decoLayer,
                     _legend, _toast,
                 })
        {
            if (node is CanvasItem ci && GodotObject.IsInstanceValid(ci)) ci.Visible = visible;
        }
        // HUD 与提示条：两条条带的底（`HudBand`）和它们的内容（`PanelContainer`）。
        // 记引用而不是在 `GetChildren()` 里猜——猜的那一版靠「stylebox 是透明的」
        // 认内容，而透明度是绘制细节，明天换个写法就认不出来。
        foreach (var node in new Node?[] { _topHud, _topBand, _bottomHint, _hintBand })
            if (node is CanvasItem ci && GodotObject.IsInstanceValid(ci)) ci.Visible = visible;
    }

    private void MountBattle(EncounterDef enc)
    {
        // 战斗作为子节点挂在本场景下：本场景是铺满视口的 Control，
        // 战斗场景自己也手写了四条锚点，于是它跟着父级一起铺满。
        // 之前开了 TopLevel，反而因为「没有父级可参照」把面板算成了 0 宽。
        // 战斗一开场，地图这一层整体让位。
        //
        // 层号只管得住一棵树：`BattleRoot`（30）让战斗压得住地图的 HUD（2）与舞台，
        // 但地图的图例是 `Legend`（40）——它比 `BattleRoot` 还高。那是上一轮为了
        // 「图例压在地砖之上」定的层号，是对的；它和 `BattleRoot` 之间没有过对话，
        // 于是 `03-battle.png` 上右边一直挂着那条 11 行的房间类型图例，
        // 盖住巨鼠那半张卡。两张图各修各的、都对，放在一起错。
        //
        // 真正的意思是「战斗时地图整块不该被看见」，而那不是层级能表达的
        // （层级只回答谁在上，不回答谁该消失）。所以直接把地图的可视层收起来：
        // 图例、网格、玩家、装饰、上下两条 HUD 条带——它们都是地图的零件，
        // 战斗中一个都不该出现。`Hidden` 不影响布局与逻辑，淡出交给战斗自己的入场。
        SetMapChromeVisible(false);
        var battleScene = new BattleScene { ZIndex = Layers.Of(StageOrder.BattleRoot) };
        battleScene.Configure(_run, enc);
        battleScene.Finished += OnBattleFinished;
        AddChild(battleScene);
    }

    private void OnBattleFinished(BattleOutcome outcome)
    {
        _busy = false;
        SetMapChromeVisible(true);
        // 一场战斗刚结算完就落盘：这是玩家最容易顺手关窗的时刻
        _store.SaveRun();
        // 回到地图就要把「战斗曲」换回「楼层曲」——以前这里传的是
        // 「是不是最终层」，于是第 25 层的连战曲在打完第一只之后一直挂着，
        // 玩家在地图上听到的还是战斗用的那一段。
        EnsureFloorMusic();
        RefreshHud();
        BuildRoomLayer();
        UpdatePlayerPosition();

        if (_run.Status == GameStatus.Dead)
        {
            ShowOutcome(victory: false);
            return;
        }
        if (outcome == BattleOutcome.Escaped)
        {
            ShowToast("你撤离了战斗，房间没有被清空", Palette.Danger);
            return;
        }
        if (_run.Status == GameStatus.Won)
        {
            ShowOutcome(victory: true);
            return;
        }
        if (_run.GauntletMidway)
        {
            // 三 Boss 连战：打掉一只，下一位接着上。这里既不发奖励也不刷新楼层机制，
            // 只把进度落盘——玩家此刻关窗，回来还是「下一位」而不是从第一只重打。
            _store.SaveRun();
            _beatenBoss = false;
            var g = _run.GauntletProgress;
            ShowToast($"Boss 已倒下 · 连战 {g?.Index + 1}/{g?.Total}，下一位正在赶来", Palette.Crit);
            RefreshHud();
            BuildRoomLayer();
            UpdatePlayerPosition();
            return;
        }
        if (_beatenBoss)
        {
            _beatenBoss = false;
            ShowToast($"Boss 已倒下 · 还差 {_run.FinalFloor - _run.Floor} 层抵达深渊之主", Palette.Gold);
        }
        var ups = _run.TakeLevelUps();
        if (ups.Count > 0)
        {
            ShowLevelUps(ups, () => ShowReward());
            return;
        }
        ShowReward();
    }

    // ---- 面板 ----

    private void ShowLevelUps(List<LevelUpResult> ups, Action done)
    {
        var last = ups[^1];
        var (root, body) = Widgets.Overlay(this, "升级！", 460);
        ShowModal(root);
        body.AddChild(Widgets.IconRow("trophy", $"等级 {last.Level}", 34, 30, Palette.AccentSoft, Palette.AccentSoft));
        foreach (var up in ups)
        {
            var line = $"生命 +{up.MaxHpGain}  攻击 +{up.AttackGain}";
            if (up.DefenseGain > 0) line += $"  防御 +{up.DefenseGain}";
            body.AddChild(Widgets.IconRow("heart", line, 22, 16, Palette.Good, Palette.Good));
            if (up.Learned is not null)
            {
                // 新技能是最值得看一眼的东西：用技能自己的图标，而不是一个泛用符文
                body.AddChild(Widgets.IconRow(IconArt.ForSkill(up.Learned.Id, SkillTypeKey(up.Learned.Type)),
                    $"习得「{up.Learned.Name}」：{up.Learned.Desc}", 24, 14, Palette.Gold, Palette.Gold));
            }
        }
        body.AddChild(UiTheme.Btn("继续", () =>
        {
            root.QueueFree();
            done();
        }, 42));
    }

    private void ShowReward()
    {
        var extra = Math.Max(0, _lastMembers - 1);
        var rewards = _run.Rewards(_lastTier, extra);
        var (root, body) = Widgets.Overlay(this, "选择你的奖励", 760);
        ShowModal(root);
        body.AddChild(UiTheme.Text(rewards.Count > 3 ? $"战利品只能带走一件（组合战斗 {rewards.Count} 选 1）" : "战利品只能带走一件", 13, Palette.Dim));
        MountRewardCards(body, rewards, root.QueueFree);
        _hint.Text = "奖励只能拿一件——选择本身就是这一层的资源。";
    }

    /// <summary>
    /// 一组「带走一件」的卡。战后三选一与宝箱房共用它。
    ///
    /// 抽出来是因为两份实现迟早会分叉：宝箱房以前走的是 `rewards[0]` 那条捷径，
    /// 于是「回血卡被上限截掉」这个修复只在战后生效，箱子里那张仍然写着
    /// 「回复 42 生命」而实际只回了 12 点。同一张卡长两个样，玩家只会得出
    /// 「这游戏在骗我」这一个结论。
    /// </summary>
    private void MountRewardCards(Container parent, List<Reward> rewards, Action close, Action? onTaken = null)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 12);
        parent.AddChild(row);
        foreach (var reward in rewards)
        {
            var card = Widgets.Card(box =>
            {
                switch (reward)
                {
                    case Reward.Item item:
                        // 三选一里最该看的就是词条：奖励面板以前只写名字，
                        // 玩家只能凭装备名去猜这件紫装带的是什么。
                        box.AddChild(Widgets.IconWithBody(
                            IconArt.ForItem(item.Def.Id, item.Def.Slot.ToString().ToLowerInvariant(),
                                item.Def.Cleanse == true, item.Def.Damage is not null),
                            Widgets.QualityColor(Inventory.QualityOf(item.Def)),
                            item.Def.Name,
                            Widgets.ItemSummary(item.Def, GameDataStore.Get().Affixes), 190, 17, 26,
                            Widgets.AffixLines(item.Def, GameDataStore.Get().Affixes)));
                        break;
                    case Reward.Gold gold:
                        box.AddChild(Widgets.IconWithBody("coin", Palette.Gold, $"{gold.Amount} 金币", "拿去花", 190, 17));
                        break;
                    case Reward.Heal heal:
                        // 满血时这张卡是「0 点回血」：以前它照样写着「回复 10 生命」，
                        // 玩家拿走才发现什么都回不了。现在把这件事写在卡上。
                        // 上限截掉的那部分要说出来：卡上写 42、实际回 30（血满了），
                        // 不说清楚就成了「这游戏在偷偷吞我的回血」。
                        if (!heal.Effective)
                            box.AddChild(Widgets.IconWithBody("heart", Palette.Dim, "生命已满", "这张卡现在什么也回不了", 190, 17));
                        else if (heal.Clipped > 0)
                            box.AddChild(Widgets.IconWithBody("heart", Palette.Good, $"回复 {heal.Amount} 生命", $"再多 {heal.Clipped} 点就超出上限了", 190, 17));
                        else
                            box.AddChild(Widgets.IconWithBody("heart", Palette.Good, $"回复 {heal.Amount} 生命", "血线太低就别客气", 190, 17));
                        break;
                }
                box.AddChild(UiTheme.Btn("带走", () =>
                {
                    close();
                    TakeReward(reward, onTaken);
                }, 36));
            });
            card.CustomMinimumSize = new Vector2(210, 0);
            row.AddChild(card);
        }
    }

    /// <summary>
    /// 结算一张奖励卡。onTaken 只在「东西真的进了背包（或金币/回血真的到账）」之后调用——
    /// 宝箱房用它来清房，于是「换装面板上点不要」不会把箱子一起吞掉。
    ///
    /// 钩子由 `MountRewardCards` 一路传下来，不再挂在字段上：字段版是「先挂上、再在
    /// 结算时取一次」，谁忘了取或者多结算一张，清房的就不再是那只箱子。
    /// </summary>
    private void TakeReward(Reward reward, Action? onTaken = null)
    {
        switch (reward)
        {
            case Reward.Gold gold:
                // 只走 EarnGold：它同时记 Player.GoldEarned 与本局的 GoldEarned，
                // 手写两行的话总有一处会漏（存档里就出现两份对不上的账）。
                _run.EarnGold(gold.Amount);
                ShowToast($"获得 {gold.Amount} 金币", Palette.Gold);
                onTaken?.Invoke();
                break;
            case Reward.Heal heal:
            {
                var before = _run.Player.Hp;
                _run.HealPlayer(heal.Amount);
                var gained = _run.Player.Hp - before;
                // 说「实际回了多少」，而不是卡片上那个数：满血时前者是 0。
                // 以前固定写「回复 {heal.Amount} 生命」，回不了的时候弹出一条「回复 10 生命」，
                // 玩家会以为血条坏了。
                ShowToast(gained > 0 ? $"回复 {gained} 生命" : "生命已满，这张卡回不了血", gained > 0 ? Palette.Good : Palette.Dim);
                onTaken?.Invoke();
                break;
            }
            case Reward.Item item:
                if (Inventory.AddItem(_run.Player, item.Def.Id, _run.Capacity))
                {
                    ShowToast($"获得「{item.Def.Name}」", Palette.Good);
                    onTaken?.Invoke();
                }
                else
                {
                    ShowDiscardPrompt(item.Def, onTaken);
                }
                break;
        }
        RefreshHud();
        BuildRoomLayer();
        UpdatePlayerPosition();
    }

    /// <summary>
    /// 这一类房间是不是「一进门就算用掉了」。
    /// 只为自检/探针暴露：玩家真的什么都没决定就离开时，房间必须还在。
    /// </summary>
    private static bool ClearsRoomOnEnter(RoomType type) => false;

    private void OpenTreasure(int index)
    {
        // 开箱是三选一，和战后奖励是同一块面板。
        //
        // 这里以前只取 `rewards[0]`：箱子只给一张卡，而洗牌之后首张常常是「回血」——
        // 上一个修复把金币/回血挪到了最后，首张才稳定成装备。但「只给一张」本身
        // 还是不对：同一层的事件房是三选一，而箱子是全层唯一一个白拿的房，
        // 没有理由比别的房更窄。更重要的是，那张单独渲染的卡漏掉了
        // 「回血被上限截掉多少」这个修复——同一张回血卡在两个面板上写两个数。
        var rewards = _run.Rewards();
        var (root, body) = Widgets.Overlay(this, "宝箱", 760);
        ShowModal(root);
        body.AddChild(UiTheme.Text("箱子里是三件东西，你只拿得走一件。", 13, Palette.Dim));
        // 清房与落盘只在「真的拿走了」之后：背包满 + 换装面板上点「算了，不要了」，
        // 这只箱子还得在（以前先清房再结算，那条退路把箱子一起吞了）。
        MountRewardCards(body, rewards, root.QueueFree, () =>
        {
            // 开箱也要落盘：宝箱给的是金币 / 回血 / 一件装备，三样都是场外状态，
            // 而这条路上以前一次 SaveRun 都没有——开完箱子顺手关窗，
            // 回来箱子还开着、钱和血却退回去了。清房与落盘必须成对。
            _store.SaveRun();
            _run.ClearRoom(index);
            BuildRoomLayer();
        });
    }

    private void OpenAltar(int index)
    {
        var altar = _run.RollAltar();
        // 祭坛是「用一次」：喝泉水 / 献血的当下才清房。
        // 以前一进门就清，于是「转身离开」之后这座祭坛就消失了——
        // 玩家以为自己在考虑，其实那一刻已经放弃了它。
        void Used()
        {
            _run.ClearRoom(index);
            BuildRoomLayer();
        }
        var (root, body) = Widgets.Overlay(this, altar.Name, 640);
        ShowModal(root);
        // 祭坛分两类（净化 / 献祭），给两个不同的符号：一眼能看出这次是喝泉水还是要放血
        body.AddChild(Widgets.IconRow(altar.Kind == AltarKind.Cleanse ? "flask" : "heart",
            altar.Name, 26, 13, altar.Kind == AltarKind.Cleanse ? Palette.Good : Palette.Danger, Palette.Muted));
        body.AddChild(UiTheme.Wrap(altar.Text, 15, Palette.Text, 560));
        var sac = _run.Sacrifice;
        if (altar.Kind == AltarKind.Cleanse)
        {
            body.AddChild(UiTheme.Text(_run.Player.PoisonTurns > 0 ? "解除当前的中毒" : "你当前没有中毒", 13, Palette.Muted));
            body.AddChild(UiTheme.Btn("饮下泉水", () =>
            {
                root.QueueFree();
                // 没中毒时不该消耗这座祭坛：泉水还在，下次中毒了再来。
                if (_run.Player.PoisonTurns <= 0)
                {
                    ShowToast("你本来就没有中毒，泉水留着吧", Palette.Dim);
                    return;
                }
                EntityRules.ClearPoison(_run.Player);
                Used();
                _store.SaveRun();
                ShowToast("毒素被涤荡一空", Palette.Good);
                RefreshHud();
            }, 46));
        }
        else if (altar.Kind == AltarKind.Offering)
        {
            // 泉眼：价钱、给什么、还能换几次，全部写在按钮上。
            // 这不是「又一条升级曲线」——它是深层那笔攒不出去的钱的出口，
            // 所以按钮上必须写清「这已经是第几次」，玩家才知道越往后越贵。
            var cost = _run.OfferingCost();
            var off = _run.Offering;
            body.AddChild(UiTheme.Text(
                _run.CanOffer
                    ? $"投入 {cost} 金币：生命上限 +{off.HpPerStep}，攻击 +{off.AttackPerStep}（这一局第 {_run.OfferingSteps + 1} 次，最多 {off.MaxSteps} 次）"
                    : $"这座泉眼已经喝干了（这一局换过 {_run.OfferingSteps} 次）",
                13, _run.CanOffer ? Palette.Muted : Palette.Dim));
            var affordable = _run.CanOffer && _run.Player.Gold >= cost;
            if (!_run.CanOffer) affordable = false;
            body.AddChild(UiTheme.Btn(affordable ? $"投入 {cost} 金币" : $"金币不够（还差 {Math.Max(0, cost - _run.Player.Gold)}）", () =>
            {
                if (!affordable) return;
                var (ok, spent, hp, atk) = _run.TakeOffering();
                if (!ok) { ShowToast("泉眼没有回应", Palette.Dim); return; }
                root.QueueFree();
                Used();
                // 上限与攻击都改在 BaseMaxHp / BaseAttack 上，不落盘等于白拿
                _store.SaveRun();
                ShowToast($"投入 {spent} 金币：生命上限 +{hp}，攻击 +{atk}", Palette.Gold);
                RefreshHud();
            }, 46));
        }
        else
        {
            // 把代价写进按钮：玩家要在点下去之前知道要付多少，而不是点完才发现上限掉了一截
            // 代价写成具体点数：比例制之后玩家没法心算「8% 是多少」，而按点数算的
            // 那一版落地后，屏幕上写着「-8% 生命上限」反而与结算不再是同一个数。
            body.AddChild(UiTheme.Btn($"献上鲜血（-{_run.SacrificeCost()} 生命上限，攻击 +{sac.Attack}）", () =>
            {
                root.QueueFree();
                // 代价按「玩家看得见的上限」算，与说明里那个百分比是同一个数。
                // 结算本身收在 Game.TakeSacrifice 里：地图、模拟器、自检走同一条路径。
                var lost = _run.TakeSacrifice();
                Used();
                // 血祭改的是 BaseMaxHp 与 BaseAttack：不落盘的话，关窗再来一次
                // 等于「白拿 6 点攻击、代价退回原样」——祭坛成了刷属性的工具。
                _store.SaveRun();
                ShowToast($"最大生命 -{lost}，攻击力 +{sac.Attack}", Palette.Gold);
                RefreshHud();
            }, 46));
        }
        body.AddChild(UiTheme.Btn("转身离开", () => root.QueueFree(), 40));
    }

    private void OpenTraining(int index)
    {
        // 同理：选了哪一个才清房。「什么都不做」要真的什么都不做。
        void Used()
        {
            _run.ClearRoom(index);
            BuildRoomLayer();
        }
        var (root, body) = Widgets.Overlay(this, "训练房", 520);
        ShowModal(root);
        body.AddChild(Widgets.IconRow("muscle", "没有敌人，只有一次三选一。三个选项都不碰长期成长曲线。", 26, 13, Palette.Good, Palette.Muted));
        foreach (var (id, label, detail) in _run.TrainingOptions())
        {
            var choiceId = id;
            body.AddChild(Widgets.Card(box =>
            {
                box.AddChild(UiTheme.Text(label, 16, Palette.Text));
                box.AddChild(UiTheme.Text(detail, 12, Palette.Muted));
                box.AddChild(UiTheme.Btn("选它", () =>
                {
                    root.QueueFree();
                    var text = _run.TakeTraining(choiceId);
                    Used();
                    // 训练房三种收益全是「状态」：苦练是 TrainingAttackPct、符文是队列、
                    // 休整是当前生命。它们都只活在内存里，不写档就等于玩家选完关窗白选。
                    _store.SaveRun();
                    ShowToast(text, Palette.Good);
                    RefreshHud();
                }, 34));
            }));
        }
        body.AddChild(UiTheme.Btn("什么都不做", () => root.QueueFree(), 38));
    }

    private void OpenCurseRoom(int index)
    {
        var item = _run.CurseRoomItem();
        if (item is null)
        {
            // 数据表里一条诅咒词条都没有：这间房本来就不该生成，说清楚并清掉它
            _run.ClearRoom(index);
            BuildRoomLayer();
            ShowToast("诅咒之物已经消散了", Palette.Dim);
            return;
        }
        var (root, body) = Widgets.Overlay(this, "诅咒房", 560);
        ShowModal(root);
        body.AddChild(Widgets.IconWithBody(
            IconArt.ForItem(item.Id, item.Slot.ToString().ToLowerInvariant(), item.Cleanse == true, item.Damage is not null),
            Widgets.QualityColor(Quality.Purple), item.Name,
            Widgets.ItemSummary(item, GameDataStore.Get().Affixes), 460, 18, 30));
        body.AddChild(UiTheme.Text("接受它就等于接受它的代价——负面词条是常驻的。", 12, Palette.Danger));
        body.AddChild(UiTheme.Btn("收下", () =>
        {
            root.QueueFree();
            // 诅咒装是运行时生成的（CurseRoomItem 已经登记过），这里只兜一层：
            // 面板开着的时候状态可能被别处改过，确认一次比出事再查便宜。
            if (!_run.ItemIndex.TryGetValue(item.Id, out var known) || !ReferenceEquals(known, item))
            {
                _run.ItemIndex[item.Id] = _run.RegisterRuntimeItemCopy(item);
            }
            // 收下才清房：「不要」那条路要留着它。
            if (Inventory.AddItem(_run.Player, item.Id, _run.Capacity))
            {
                _run.ClearRoom(index);
                BuildRoomLayer();
                ShowToast($"获得「{item.Name}」", Palette.Good);
            }
            else ShowDiscardPrompt(item, () =>
            {
                _run.ClearRoom(index);
                BuildRoomLayer();
            });
            RefreshHud();
        }, 44));
        body.AddChild(UiTheme.Btn("不要", () => root.QueueFree(), 38));
    }

    private void OpenShop(int index, int extraLines = 0)
    {
        var stock = _run.ShopStock(index);
        var (root, body) = Widgets.Overlay(this, "商店", 720);
        ShowModal(root);
        // 金币行归 Render 管：它要跟着「买完重开面板」一起刷新，
        // 建在 Render 外面的话，重建出来的面板第一行就永远是旧余额。
        void Render()
        {
            // 先摘再删（见 BattleScene.RenderActions）：QueueFree 要到帧末才生效，
            // 同一帧里重建会把新内容追加在旧内容后面
            foreach (var child in body.GetChildren().ToList())
            {
                body.RemoveChild(child);
                child.QueueFree();
            }
            body.AddChild(UiTheme.Text($"金币 {_run.Player.Gold}", 15, Palette.Gold));
            // 截图用：往店里多铺几行说明，把内容顶到比工作区更高。
            // 长度是内容层的活，这里只借它造一个「长内容」的现场——
            // 短卡永远装得下，于是「内容比面板高」那条分支一次都没被拍过。
            for (var i = 0; i < extraLines; i++)
                body.AddChild(UiTheme.Wrap(Widgets.LongContentLines[i % Widgets.LongContentLines.Length], 12, Palette.Dim, 640));
            foreach (var entry in stock)
            {
                var e = entry;
                body.AddChild(Widgets.Card(box =>
                {
                    var row = new HBoxContainer();
                    row.AddThemeConstantOverride("separation", 10);
                    box.AddChild(row);
                    var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                    row.AddChild(col);
                    col.AddChild(Widgets.IconRow(
                        IconArt.ForItem(e.Item.Id, e.Item.Slot.ToString().ToLowerInvariant(), e.Item.Cleanse == true, e.Item.Damage is not null),
                        e.Item.Name, 22, 16, Widgets.QualityColor(Inventory.QualityOf(e.Item))));
                    col.AddChild(UiTheme.Wrap(Widgets.ItemSummary(e.Item, GameDataStore.Get().Affixes), 12, Palette.Muted, 420));
                    // 词条效果逐条写出来：只给名字，玩家没法判断这件带「脆弱躯壳」的紫装
                    // 到底是变强了还是变脆了。
                    foreach (var line in Widgets.AffixLines(e.Item, GameDataStore.Get().Affixes))
                        col.AddChild(UiTheme.Wrap(line, 11, Palette.Dim, 420));
                    if (e.Sold)
                    {
                        row.AddChild(UiTheme.Text("已售出", 14, Palette.Dim));
                        return;
                    }
                    // 钱不够 / 格子不够都要写在按钮上并压暗它。
                    //
                    // 以前这里只有「钱不够才禁用」：格子满了按钮照样亮着，点下去只弹一句
                    // 「背包满了，先腾一格」——而这一排按钮没有「腾一格」的入口，
                    // 玩家得先按 I 去背包里丢东西再回来。商店面板开着的时候
                    // 那个入口看起来不存在，于是「买不了」会像「按钮坏了」。
                    var price = _run.Player.Gold < e.Price
                        ? $"还差 {e.Price - _run.Player.Gold} 金币"
                        : _run.Player.Inventory.Count >= _run.Capacity ? "背包已满" : $"{e.Price} 金币";
                    var btn = UiTheme.Btn(price, () =>
                    {
                        if (_run.Player.Gold < e.Price) return;
                        if (_run.Player.Inventory.Count >= _run.Capacity)
                        {
                            // 商店面板里直接给出口：点「背包已满」就地打开背包，
                            // 不用先关面板再翻背包再回来（店还在，货架也是缓存的）。
                            ShowToast("背包满了：在这里卖掉一件再回来买", Palette.Danger);
                            OpenInventory();
                            return;
                        }
                        // 金币的进出只走 Game.SpendGold：它顺手记 GoldLost，
                        // 结算面板与存档才不会各说各话。
                        _run.SpendGold(e.Price);
                        // 货架上的东西也是被缓存的：它可能是读档前就 roll 出来的，
                        // 那时候 ItemIndex 里还没有它。买下前先确认能按 id 查到，
                        // 否则背包里会多出一格「没有名字、点不动、存档也写不下」的道具。
                        if (!_run.ItemIndex.TryGetValue(e.Item.Id, out var known) || !ReferenceEquals(known, e.Item))
                        {
                            _run.ItemIndex[e.Item.Id] = _run.RegisterRuntimeItemCopy(e.Item);
                        }
                        Inventory.AddItem(_run.Player, e.Item.Id, _run.Capacity);
                        e.Sold = true;
                        ShowToast($"买下了「{e.Item.Name}」", Palette.Gold);
                        // 买完就落盘：货架已经进存档（连 Sold 一起），但玩家身上多出来的
                        // 那件与少掉的那笔钱靠的是「下一次存档」——不该让买完关窗等于退货。
                        _store.SaveRun();
                        RefreshHud();
                        root.QueueFree();
                        OpenShop(index);
                    }, 38);
                    // 钱够了但没格子时按钮**不能**禁用：它要负责把玩家带到背包去腾位置。
                    btn.Disabled = _run.Player.Gold < e.Price;
                    row.AddChild(btn);
                }));
            }
            // 收购：只在这间店里才出手。
            //
            // 这一排以前不存在，而「买」那一排在有六格限制的世界里是没有出口的：
            // 玩家钱够、药就在架上、背包满——横扫 30 局里这个状态出现 314 次采样、
            // 299 次「钱够买一瓶」，而买成的药只有 56 瓶。缺的不是钱，是一条把
            // 「换下来的那件旧货」变成金币的路。价格 = 模板价三成（Inventory.SellValue），
            // 卖掉按同一套登记口径落盘。
            var sellables = _run.Player.Inventory
                .Select((id, i) => (Id: id, Index: i))
                .Where(x => _run.ItemIndex.ContainsKey(x.Id))
                .ToList();
            body.AddChild(UiTheme.Text($"收购（按原价三成）｜背包 {_run.Player.Inventory.Count}/{_run.Capacity}", 13, Palette.Gold));
            var sellRow = new HBoxContainer();
            sellRow.AddThemeConstantOverride("separation", 8);
            body.AddChild(sellRow);
            foreach (var (id, slotIndex) in sellables)
            {
                if (!_run.ItemIndex.TryGetValue(id, out var def)) continue;
                var price = Inventory.SellValue(def);
                if (price <= 0) continue;
                var equipped = Inventory.IsEquipped(_run.Player, slotIndex) is not null;
                var btn = UiTheme.Btn($"{def.Name} · {price}" + (equipped ? "（穿着）" : ""), () =>
                {
                    if (!_run.SellItem(slotIndex)) return;
                    _store.SaveRun();
                    root.QueueFree();
                    ShowToast($"卖掉了「{def.Name}」，得到 {price} 金币", Palette.Gold);
                    RefreshHud();
                    OpenShop(index);
                }, 32);
                sellRow.AddChild(btn);
            }
            if (sellables.Count == 0) sellRow.AddChild(UiTheme.Text("背包里没有可卖的东西", 12, Palette.Dim));
        }
        Render();
        body.AddChild(UiTheme.Btn("离开", () => root.QueueFree(), 40));
    }

    private void OpenEvent(int index)
    {
        var ev = _run.RandomEvent();
        // 事件是「选了一项才算发生」：以前进门就清房，
        // 玩家点「走开」之后这间房就变成了空的——他其实还没做任何决定。
        void Render()
        {
            var (root, body) = Widgets.Overlay(this, ev.Name, 700);
            ShowModal(root);
            body.AddChild(UiTheme.Wrap(ev.Text, 15, Palette.Text, 620));
            for (var i = 0; i < ev.Choices.Count; i++)
            {
                var choice = ev.Choices[i];
                var affordable = choice.Cost is null || _run.Player.Gold >= choice.Cost;
                var hasItem = choice.RequiresItem != true || _run.Player.Inventory.Count > 0;
                // 副标题说清「为什么不能点」：金币不够和没道具是两回事，
                // 只写「条件不足」玩家不知道该去攒钱还是去打怪（TS 版同一条文案）。
                var reason = !affordable ? $"还差 {choice.Cost - _run.Player.Gold} 金币"
                    : !hasItem ? "需要一件道具"
                    : null;
                var label = choice.Cost is { } c ? $"{choice.Label}（{c} 金币）" : choice.Label;
                var btn = UiTheme.Btn(reason is null ? label : $"{label} — {reason}", () =>
                {
                    if (!affordable || !hasItem)
                    {
                        // 条件不满足时按钮是灰的，鼠标点不进这里；但键盘焦点仍然能触发
                        // Pressed（Godot 的 Disabled 只挡鼠标输入）。以前没有这道判，
                        // 于是「还差 35 金币」的选项可以被回车选中——金币照扣成负数。
                        ShowToast(reason ?? "现在选不了这一项", Palette.Danger);
                        return;
                    }
                    root.QueueFree();
                    // 清房在 ResolveEvent 里，不在这里：交换类选项要先弹一次
                    // 「交出哪一件」，玩家在那一步点「算了」应该还回到这张选项面板上。
                    // 以前清房写在这里，于是「选了交换、又在选卡面板上反悔」= 房没了。
                    if (choice.RequiresItem == true) PickItemForEvent(ev, i, Render);
                    else ResolveEvent(ev, i, Render);
                }, 46);
                btn.Disabled = reason is not null;
                body.AddChild(btn);
            }
            body.AddChild(UiTheme.Btn("走开", () => root.QueueFree(), 36));
        }
        Render();
    }

    /// <summary>
    /// 「用一件东西换一件未知的东西」——先把要交出去的那件挑出来。
    /// TS 版这里是一张选卡面板（showSwapPicker），Godot 版原来直接跳过了这一步：
    /// 点了「交换」就把背包里随机一件换掉，玩家既不知道自己交出了什么，
    /// 也不知道那张卡本来是为他准备的。这一步是透明的代价，不能省。
    /// </summary>
    private void PickItemForEvent(EventDef ev, int choiceIndex, Action render)
    {
        if (_run.Player.Inventory.Count == 0)
        {
            var (emptyRoot, emptyBody) = Widgets.Overlay(this, "选择要交换出去的道具", 620);
            ShowModal(emptyRoot);
            emptyBody.AddChild(UiTheme.Text("你没有道具可以交换", 16, Palette.Muted));
            emptyBody.AddChild(UiTheme.Btn("离开", () => emptyRoot.QueueFree(), 42));
            return;
        }

        var (root, body) = Widgets.Overlay(this, "选择要交换出去的道具", 780);
        ShowModal(root);
        body.AddChild(UiTheme.Text("被选中的那件会永久失去——换回来的是什么东西，谁也不知道。", 12, Palette.Danger));
        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 10);
        body.AddChild(grid);
        for (var i = 0; i < _run.Player.Inventory.Count; i++)
        {
            var index = i;
            var id = _run.Player.Inventory[index];
            if (!_run.ItemIndex.TryGetValue(id, out var def)) continue;
            var equipped = Inventory.IsEquipped(_run.Player, index);
            grid.AddChild(Widgets.Card(box =>
            {
                box.AddChild(UiTheme.Text(def.Name, 14, Widgets.QualityColor(Inventory.QualityOf(def))));
                box.AddChild(UiTheme.Wrap(Widgets.ItemSummary(def, GameDataStore.Get().Affixes), 11, Palette.Muted, 200));
                // 装备中的先卸下再说：不卸的话槽位里留着一个已经不存在的下标
                if (equipped is not null) box.AddChild(UiTheme.Text("装备中（会卸下）", 11, Palette.Dim));
                box.AddChild(UiTheme.Btn("就换它", () =>
                {
                    root.QueueFree();
                    ResolveEvent(ev, choiceIndex, render, index);
                }, 32));
            }));
        }
        body.AddChild(UiTheme.Btn("算了", () => root.QueueFree(), 36));
    }

    private void ResolveEvent(EventDef ev, int choiceIndex, Action render, int? swapOutIndex = null)
    {
        var choice = ev.Choices[choiceIndex];
        // 事件房必须真的落到「已清空」上。
        //
        // 这一条以前是空的：注释写着「选了哪一项，这间房才算发生过」，而那一句
        // `_run.ClearRoom(index)` 在调用这个函数的同一个 lambda 里，早被别的改动
        // 一起删掉了——于是事件房永远不清空。地图上它一直是个「?」，
        // 玩家可以站进去反复掷同一口井：投 10 金币换一件宝物，捡完再投一次。
        // 而 `RandomEvent()` 每进一次都会推进随机序列，所以「同一间房」实际上是
        // 一台永远不会关的扭蛋机。清房放在这里，是因为「真的做了一次选择」
        // 这件事只有结算的那一刻说得准（反悔、选卡面板上点「算了」都不算）。
        _run.ClearRoom();
        BuildRoomLayer();
        // 花掉的金币要记进 GoldLost：商店扣钱一直记，事件选项却没记，
        // 于是「金币获得」这一栏把付费选项付出去的钱算成了净收入。
        if (choice.Cost is { } cost) _run.SpendGold(cost);
        // 玩家挑好的那一件先卸下：交换是原地替换，不卸会让装备槽指向别人的下标
        if (swapOutIndex is { } picked && Inventory.IsEquipped(_run.Player, picked) is { } equippedSlot)
        {
            Inventory.Unequip(_run.Player, equippedSlot, _run.ItemIndex);
        }
        var outcome = GameEvents.ResolveChoice(_run.Rng, choice.Outcomes);
        var result = GameEvents.ApplyEffect(_run.Player, outcome.Effect, new EventApplyContext
        {
            Rng = _run.Rng,
            Items = GameDataStore.Get().Items,
            Capacity = _run.Capacity,
            ItemsById = _run.ItemIndex,
            PanelMaxHp = _run.Stats.MaxHp,
            SwapOutIndex = swapOutIndex,
            // 事件给的装备走和战斗掉落同一条登记路：本层符文 + 唯一 id。
            // 两步都要传：只传 PrepareItem 的话，副本用的是模板 id，
            // 存档写不下它（只存 _rN 的运行时装备），读档后背包里那一格会消失。
            PrepareItem = _run.ApplyBonusAffix,
            RegisterItem = _run.RegisterRuntimeItemCopy,
            // 事件挣/花的钱也必须进 Game 那本账：结算面板与存档读的是 Game.GoldEarned，
            // 而在 Player 上直接加减的那一版，事件挣的钱从来没被算进去过。
            AddGold = delta =>
            {
                if (delta >= 0) _run.EarnGold(delta);
                else _run.SpendGold(-delta);
            },
        });

        var (root, body) = Widgets.Overlay(this, "结果", 640);
        ShowModal(root);
        body.AddChild(UiTheme.Wrap(outcome.Text, 15, Palette.Text, 560));
        if (!string.IsNullOrEmpty(result.Text))
        {
            // 结算行给它自己的符号：拿到金子和掉了血不该长同一个样。
            // 金币以前被画成 sparkle + 绿色，和「捡到东西」长得一样；「获得 -10」更是读不通。
            var lostHp = result.Text.StartsWith("失去") || result.Text.Contains("最大生命 -");
            var gold = result.Text.Contains("金币");
            var icon = lostHp ? "skull" : gold ? "coin" : "sparkle";
            var tint = lostHp ? Palette.Danger : gold ? Palette.Gold : Palette.Good;
            body.AddChild(Widgets.IconRow(icon, result.Text, 24, 16, tint, tint));
        }
        body.AddChild(UiTheme.Btn("离开", () => root.QueueFree(), 42));
        ShowToast(result.Text, Palette.AccentSoft);
        RefreshHud();
        // 事件也会把人打死：「赌徒的钱袋」炸开 12 点血，残血时点了就是死。
        var died = _run.CollapseIfDead();
        // 事件是「走廊上」改状态的地方（血量/金币/背包），得跟着落盘——
        // 否则掷出一个好结果之后直接关窗，等于这件好事从没发生过。
        // 死了的局让 SaveRun 自己删档，所以这里不用分支。
        _store.SaveRun();
        if (died)
        {
            ShowOutcome(victory: false);
            return;
        }
        // NeedDiscard 只在这个「拿到的东西确实放不进背包」的场合才弹换装面板。
        // 以前 SwapItem 也会带上 NeedDiscard（交换本身就已经原地替换了），
        // 于是玩家挑完要交出的道具，又被问一次「背包满了要放下什么」——
        // 而那时候背包根本没满。
        if (result.NeedDiscard && swapOutIndex is null
            && result.GainedItemId is not null && _run.ItemIndex.TryGetValue(result.GainedItemId, out var gained))
        {
            ShowDiscardPrompt(gained);
        }
    }

    /// <summary>
    /// 背包满了：让玩家挑一件换掉。
    /// onDone 只在「换掉了那一件、新的真的进了背包」之后调用：宝箱房靠它清房，
    /// 于是「算了，不要了」这条退路是真的退路，而不是把箱子白扔。
    ///
    /// 反过来的那一半以前是坏的：点「算了，不要了」只是把面板关掉，
    /// `onDone`（也就是清房那一步）一次都没走。而房间没被标成清空，
    /// 于是**再进一次就重新滚一批掉落**——「算了」变成了「换一批货再来一次」，
    /// 那件被放弃的宝物还会在下一轮里以另一种形态出现。放弃也是决定，
    /// 它得和「换掉」一样把房间收掉。
    /// </summary>
    private void ShowDiscardPrompt(ItemDef newItem, Action? onDone = null)
    {
        var (root, body) = Widgets.Overlay(this, $"背包满了：放下什么换「{newItem.Name}」？", 720);
        ShowModal(root);
        body.AddChild(UiTheme.Text("被替换的那件会永久失去。", 12, Palette.Danger));
        var grid = new GridContainer { Columns = 3 };
        grid.AddThemeConstantOverride("h_separation", 10);
        grid.AddThemeConstantOverride("v_separation", 10);
        body.AddChild(grid);
        for (var i = 0; i < _run.Player.Inventory.Count; i++)
        {
            var index = i;
            var id = _run.Player.Inventory[index];
            if (!_run.ItemIndex.TryGetValue(id, out var def)) continue;
            var equipped = Inventory.IsEquipped(_run.Player, index);
            grid.AddChild(Widgets.Card(box =>
            {
                box.AddChild(Widgets.IconRow(
                    IconArt.ForItem(def.Id, def.Slot.ToString().ToLowerInvariant(), def.Cleanse == true, def.Damage is not null),
                    def.Name, 20, 14, Widgets.QualityColor(Inventory.QualityOf(def))));
                if (equipped is not null) box.AddChild(UiTheme.Text("装备中（会卸下）", 11, Palette.Dim));
                box.AddChild(UiTheme.Btn("换掉它", () =>
                {
                    root.QueueFree();
                    Inventory.SwapInItem(_run.Player, index, newItem.Id);
                    // 走登记入口而不是直接塞表：入口会保证它带唯一 id，
                    // 直接赋值只是「这个进程里查得到」，存档那一份仍写不下来。
                    _run.ItemIndex[newItem.Id] = _run.RegisterRuntimeItemCopy(newItem);
                    ShowToast($"丢掉了「{def.Name}」，换来了「{newItem.Name}」", Palette.AccentSoft);
                    onDone?.Invoke();
                    RefreshHud();
                }, 32));
            }));
        }
        body.AddChild(UiTheme.Btn("算了，不要了", () =>
        {
            root.QueueFree();
            // 「放弃」也是做完了决定：不清房的话这一次掉落会被丢掉、而房间还开着，
            // 下一次进去又是新的一批。见上面的注释。
            onDone?.Invoke();
            RefreshHud();
        }, 36));
    }

    private void OpenInventory()
    {
        var (root, body) = Widgets.Overlay(this, "背包", 780);
        ShowModal(root);
        void Render()
        {
            // 先摘再删（见 BattleScene.RenderActions）：QueueFree 要到帧末才生效，
            // 同一帧里重建会把新内容追加在旧内容后面
            foreach (var child in body.GetChildren().ToList())
            {
                body.RemoveChild(child);
                child.QueueFree();
            }
            var stats = _run.Stats;
            body.AddChild(UiTheme.Text($"攻击 {stats.Attack}  防御 {stats.Defense}  生命上限 {stats.MaxHp}  金币 {_run.Player.Gold}", 14, Palette.Muted));

            body.AddChild(UiTheme.Text("装备中", 14, Palette.Gold));
            var equipRow = new HBoxContainer();
            equipRow.AddThemeConstantOverride("separation", 10);
            body.AddChild(equipRow);
            foreach (var slot in EntityRules.EquipSlots)
            {
                var def = Inventory.EquippedDef(_run.Player, slot, _run.ItemIndex);
                equipRow.AddChild(Widgets.Card(box =>
                {
                    box.AddChild(UiTheme.Text(SlotLabel(slot), 12, Palette.Dim));
                    if (def is null)
                    {
                        // 空槽画一个「这个位置该是什么」的淡色轮廓，比一个括号更清楚
                        box.AddChild(Widgets.IconRow(SlotIcon(slot), "（空）", 22, 14, Palette.Dim, Palette.Dim));
                        return;
                    }
                    box.AddChild(Widgets.IconRow(
                        IconArt.ForItem(def.Id, def.Slot.ToString().ToLowerInvariant(), def.Cleanse == true, def.Damage is not null),
                        def.Name, 22, 15, Widgets.QualityColor(Inventory.QualityOf(def))));
                    box.AddChild(UiTheme.Wrap(Widgets.ItemSummary(def, GameDataStore.Get().Affixes), 11, Palette.Muted, 220));
                    // 穿在身上那几件给了什么，要在这里逐条算出来：
                    // 「攻击 124」是总数，玩家没法从中看出「哪一件贡献了 16」。
                    // 换装是这款游戏里唯一一个能反复撤销的决定，它必须可比较。
                    box.AddChild(UiTheme.Wrap(Widgets.ItemGain(def), 11, Palette.Good, 220));
                    box.AddChild(UiTheme.Btn("卸下", () =>
                    {
                        Inventory.Unequip(_run.Player, slot, _run.ItemIndex);
                        // 同「装备」：上限变了就得落盘，否则关窗等于没换过
                        _store.SaveRun();
                        root.QueueFree();
                        OpenInventory();
                        RefreshHud();
                    }, 28));
                }));
            }

            body.AddChild(UiTheme.Text("背包", 14, Palette.Gold));
            var grid = new GridContainer { Columns = 4 };
            grid.AddThemeConstantOverride("h_separation", 8);
            grid.AddThemeConstantOverride("v_separation", 8);
            body.AddChild(grid);
            for (var i = 0; i < _run.Player.Inventory.Count; i++)
            {
                var index = i;
                var id = _run.Player.Inventory[index];
                if (!_run.ItemIndex.TryGetValue(id, out var def)) continue;
                var equipped = Inventory.IsEquipped(_run.Player, index);
                grid.AddChild(Widgets.Card(box =>
                {
                    box.AddChild(Widgets.IconRow(
                        IconArt.ForItem(def.Id, def.Slot.ToString().ToLowerInvariant(), def.Cleanse == true, def.Damage is not null),
                        def.Name, 20, 14, Widgets.QualityColor(Inventory.QualityOf(def))));
                    if (equipped is not null) box.AddChild(UiTheme.Text("装备中", 11, Palette.Good));
                    // 背包里那件「比身上的强多少 / 弱多少」：不带这个数，六格里的取舍只能靠背数值。
                    if (def.Slot != Slot.Consumable)
                    {
                        var wearing = Inventory.EquippedDef(_run.Player, Inventory.SlotKey(def.Slot), _run.ItemIndex);
                        box.AddChild(UiTheme.Wrap(Widgets.ItemGain(def, wearing), 11,
                            wearing is null || Widgets.ItemScore(def) > Widgets.ItemScore(wearing) ? Palette.Good : Palette.Dim, 200));
                    }
                    // 词条效果一行一条：背包是玩家做取舍的地方，看不到效果就等于没得选
                    foreach (var line in Widgets.AffixLines(def, GameDataStore.Get().Affixes))
                        box.AddChild(UiTheme.Wrap(line, 10, Palette.Muted, 200));
                    var row = new HBoxContainer();
                    row.AddThemeConstantOverride("separation", 6);
                    box.AddChild(row);
                    if (def.Slot != Slot.Consumable)
                    {
                        var equipBtn = UiTheme.Btn(equipped is not null ? "卸下" : "装备", () =>
                        {
                            if (equipped is not null) Inventory.Unequip(_run.Player, equipped, _run.ItemIndex);
                            else Inventory.Equip(_run.Player, index, _run.ItemIndex);
                            // 换装改的是面板上限（也会压低当前生命），必须当场落盘：
                            // 「喝药立刻存」「丢弃立刻存」「买完立刻存」都做了，唯独换装没有——
                            // 于是换上一件更好的剑之后直接关窗，回来又成了旧装备。
                            _store.SaveRun();
                            root.QueueFree();
                            OpenInventory();
                            RefreshHud();
                        }, 28);
                        row.AddChild(equipBtn);
                    }
                    else
                    {
                        row.AddChild(UiTheme.Btn("使用", () =>
                        {
                            root.QueueFree();
                            UseConsumable(index);
                        }, 28));
                    }
                    // 卖掉：背包 6 格、装备槽只有 3 个，「换装剩下的那件旧货」必须有个去处。
                    // 以前只有「丢弃」——一条纯损失的路，于是玩家宁可六格全塞装备，
                    // 也不肯给一瓶药腾地方。价格与逻辑都在 Inventory.SellValue / Game.SellItem，
                    // 界面只负责说清楚「能换多少钱」。
                    var price = Inventory.SellValue(def);
                    if (price > 0)
                    {
                        row.AddChild(UiTheme.Btn($"卖 {price}", () =>
                        {
                            if (!_run.SellItem(index)) return;
                            _store.SaveRun();
                            root.QueueFree();
                            OpenInventory();
                            RefreshHud();
                            ShowToast($"卖掉了「{def.Name}」，得到 {price} 金币", Palette.Gold);
                        }, 28));
                    }
                    row.AddChild(UiTheme.Btn("丢弃", () =>
                    {
                        var removed = Inventory.RemoveItem(_run.Player, index);
                        // 同理：丢掉的东西不能靠「下一次存档」才算数
                        _store.SaveRun();
                        root.QueueFree();
                        OpenInventory();
                        RefreshHud();
                        if (removed is not null) ShowToast("丢掉了那件东西", Palette.Dim);
                    }, 28));
                }));
            }
            if (_run.Player.Inventory.Count == 0) body.AddChild(UiTheme.Text("空空如也", 13, Palette.Dim));
            body.AddChild(UiTheme.Btn("关闭", () => root.QueueFree(), 40));
        }
        Render();
    }

    /// <summary>技能类型 → 图标表的键（IconArt.ForSkill 按字符串查表，与 TS 版一致）</summary>
    private static string SlotIcon(string slot) => slot switch
    {
        "weapon" => "sword",
        "armor" => "armor",
        "trinket" => "ring",
        _ => "flask",
    };

    private static string SkillTypeKey(SkillType type) => type switch
    {
        SkillType.Damage => "damage",
        SkillType.Heal => "heal",
        SkillType.Buff => "buff",
        SkillType.Poison => "poison",
        SkillType.Drain => "drain",
        SkillType.DebuffDefense => "debuff_defense",
        SkillType.DebuffAttack => "debuff_attack",
        SkillType.Evade => "evade",
        SkillType.Stun => "stun",
        SkillType.Dispel => "dispel",
        _ => "",
    };

    private static string SlotLabel(string slot) => slot switch
    {
        "weapon" => "武器",
        "armor" => "护甲",
        "trinket" => "饰品",
        _ => slot,
    };

    private void UseConsumable(int index)
    {
        var id = _run.Player.Inventory[index];
        if (!_run.ItemIndex.TryGetValue(id, out var def)) return;
        if (def.Heal is { } heal)
        {
            var before = _run.Player.Hp;
            if (before >= _run.Stats.MaxHp)
            {
                // 满血时喝药是纯浪费：说清楚，并且不消耗那瓶药
                ShowToast($"生命已满，{def.Name}先留着吧", Palette.Dim);
                return;
            }
            _run.Player.Hp = Math.Min(_run.Stats.MaxHp, _run.Player.Hp + heal);
            ShowToast($"喝下{def.Name}，回复 {_run.Player.Hp - before} 点生命", Palette.Good);
        }
        else if (def.Cleanse == true)
        {
            var had = EntityRules.ClearPoison(_run.Player);
            // 地图上本来不会有中毒（中毒是战斗内状态）：没有毒就别把药浪费掉。
            // TS 版同一条——「提前说清楚，而不是让玩家点了没反应」。
            if (!had)
            {
                ShowToast($"你没有中毒，{def.Name}留着吧", Palette.Dim);
                return;
            }
            ShowToast("毒素被涤荡一空", Palette.Good);
        }
        else
        {
            ShowToast($"{def.Name}只能在战斗中使用", Palette.Dim);
            return;
        }
        // 地图上喝掉的药必须落盘：存档里存的是背包 id 列表，
        // 不立刻写一遍的话，按 Esc「保存并回到标题」倒是会存，
        // 而直接关窗（或崩溃）就会把已经喝下去的药又送回背包——喝药等于白喝。
        Inventory.RemoveItem(_run.Player, index);
        _store.SaveRun();
        RefreshHud();
    }

    private void ShowOutcome(bool victory)
    {
        var (root, body) = Widgets.Overlay(this, victory ? "通关" : "你倒下了", 620);
        ShowModal(root);
        if (victory)
        {
            body.AddChild(UiTheme.Text($"你打穿了第 {_run.FinalFloor} 层的深渊之主。", 16, Palette.Gold));
        }
        else
        {
            body.AddChild(UiTheme.Text($"倒在第 {_run.Floor} 层。", 16, Palette.Danger));
        }
        body.AddChild(UiTheme.Text($"击杀 {_run.Kills} · 金币 {_run.Player.GoldEarned} · 等级 {_run.Player.Level} · 碎片 +{_run.ShardsEarned}", 14, Palette.Muted));
        if (victory)
        {
            body.AddChild(UiTheme.Btn("继续深潜（下一周目）", () =>
            {
                root.QueueFree();
                _run.ContinueDive();
                _store.SaveRun();
                EnsureFloorMusic();
                BuildRoomLayer();
                UpdatePlayerPosition();
                RefreshHud();
                ShowToast($"第 {_run.Loop} 周目 · 第 {_run.Floor} 层", Palette.Accent);
            }, 46));
        }
        body.AddChild(UiTheme.Btn("回到标题", () =>
        {
            root.QueueFree();
            // 这一局已经结束：存档不再可继续，ClearRun 会把盘上的那份也删掉
            _store.ClearRun();
            GetTree().ChangeSceneToFile("res://Scenes/Boot.tscn");
        }, 42));
    }

    private void OpenPause()
    {
        var (root, body) = Widgets.Overlay(this, "暂停", 460);
        ShowModal(root);
        body.AddChild(UiTheme.Text($"第 {_run.Floor} 层 · 第 {_run.Loop} 周目 · 等级 {_run.Player.Level}", 13, Palette.Muted));
        body.AddChild(UiTheme.Btn("继续游戏", () => root.QueueFree(), 46));
        body.AddChild(UiTheme.Btn("操作说明（H）", () =>
        {
            root.QueueFree();
            ShowHelp();
        }, 40));
        body.AddChild(UiTheme.Btn(Sfx.Muted ? "打开音效（M）" : "静音（M）", () =>
        {
            ToggleMute();
            root.QueueFree();
            OpenPause();
        }, 40));
        // 进度会存盘：原 TS 版没有局内存档，这里既然做了，文案就不能再写「会丢」
        body.AddChild(UiTheme.Btn("保存并回到标题（之后可以「继续」）", () =>
        {
            root.QueueFree();
            _store.SaveRun();
            GetTree().ChangeSceneToFile("res://Scenes/Boot.tscn");
        }, 42));
        body.AddChild(UiTheme.Btn("放弃这一局，回到标题", () =>
        {
            root.QueueFree();
            _store.ClearRun();
            GetTree().ChangeSceneToFile("res://Scenes/Boot.tscn");
        }, 38));
    }

    /// <summary>
    /// 操作说明。内容来自 Keymap（键位表），不在这里另抄一份——
    /// TS 版就是因为说明与实现各写各的，「写了但没接」的键位骗了玩家一次。
    /// </summary>
    private void ShowHelp()
    {
        var (root, body) = Widgets.Overlay(this, "操作说明", 640);
        ShowModal(root);
        foreach (var (title, list) in new[] { ("地图", Keymap.Map), ("战斗", Keymap.Battle) })
        {
            body.AddChild(UiTheme.Text(title, 15, Palette.Gold));
            foreach (var b in list)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 12);
                var key = UiTheme.Text(b.Keys, 13, Palette.AccentSoft);
                key.CustomMinimumSize = new Vector2(150, 0);
                row.AddChild(key);
                row.AddChild(UiTheme.Wrap(b.Action, 13, Palette.Text, 420));
                body.AddChild(row);
            }
        }
        // 这一段以前写着「机制写在**左上角**」，而它上一轮被挪到了右上（左列装不下第四行，
        // 装不下的结果是整条 HUD 被挤成两截）。说明里的方位和屏上的方位对不上，
        // 是那种「读起来没问题、照着找找不到」的错。
        var mech = _run.FloorMechanicInfo;
        var mechLine = _run.FloorMechanicId == "none"
            ? "这一层没有额外机制。"
            : $"本层「{mech.Name}」：{mech.Desc}";
        body.AddChild(UiTheme.Wrap($"每一层都有一条楼层机制写在右上角（层数下面）：{mechLine}\n" +
                                   "它们不是数值放大，是逼你换打法。", 13, Palette.Muted, 560));
        body.AddChild(UiTheme.Btn("知道了", () => root.QueueFree(), 40));
    }

    private void ToggleMute()
    {
        var muted = Sfx.Toggle();
        ShowToast(muted ? "音效已关闭" : "音效已打开", muted ? Palette.Dim : Palette.AccentSoft);
    }

    private static void ToggleFullscreen()
    {
        var mode = DisplayServer.WindowGetMode();
        DisplayServer.WindowSetMode(mode == DisplayServer.WindowMode.Fullscreen
            ? DisplayServer.WindowMode.Windowed
            : DisplayServer.WindowMode.Fullscreen);
    }
}
