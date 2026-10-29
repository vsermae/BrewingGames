// 战斗场景：把 Battle（纯逻辑）接上按钮、血条与飘字。
//
// 这一层不做任何规则判断：能打的技能问 Battle，伤害问 Battle，
// 连「这一下该不该飘红字」都由 BattleEvents 决定。
// 好处是同一份逻辑既能被玩家点按钮驱动，也能被 headless 自检批量驱动。
namespace AbyssEcho.Scenes;

using Godot;
using System.Threading.Tasks;
using AbyssEcho.Core;
using AbyssEcho.Data;
using AbyssEcho.Systems;
using AbyssEcho.UI;

// BattleOutcome 在 Systems/Encounters.cs：结算方式与结局是同一件事的两面，
// 分成两份只会让「这边赢了、那边没结」这种情况没人负责。
public partial class BattleScene : Control
{
    private Game _run = null!;
    private EncounterDef _enc = null!;
    private Battle _battle = null!;
    private bool _busy = true;
    /// <summary>布局快照：立绘与世界坐标的落点用它算，截图/自检也读它</summary>
    private BattleLayout.Layout _layout = BattleLayout.Compute(720);

    public event Action<BattleOutcome>? Finished;

    private Label _enemyName = null!;
    private ProgressBar _enemyHp = null!;
    private Label _enemyHpText = null!;
    private Label _enemyForecast = null!;
    private Label _enemySkills = null!;
    /// <summary>
    /// 卡上那一行「你还要几刀」。它**不是**第七行：它并进「下一刀」那一行的尾巴，
    /// 用一件所有玩家都在算的事（`再挨两刀还是三刀`）把两个方向补成一对，
    /// 而卡的六行配额（`BattleLayout.EnemyPanelHeight`）一行都没多。
    /// </summary>
    private Label _youHit = null!;
    private Arena? _arena;
    private Label _playerBuffs = null!;
    private Label _playerHpText = null!;
    private ProgressBar _playerHp = null!;
    private HBoxContainer _actions = null!;
    /// <summary>行动条里那一列（日志块 / 状态行 / 血条行 / 按钮行）。日志块的落位由它决定</summary>
    private VBoxContainer _actionCol = null!;
    private Control _fxLayer = null!;
    private VBoxContainer _enemyGroup = null!;

    // ---- 表现层（对应 TS 版 sprites / particles / damageNumbers / statusIcons / audio）----
    private MonsterSprite? _enemySprite;
    private StatusIconRow? _enemyStatus;
    private StatusIconRow? _playerStatus;
    private FxLayer? _particles;
    private DamageNumberLayer? _damageNumbers;
    private RichTextLabel _logRich = null!;
    private PanelContainer _logPanel = null!;
    /// <summary>当前日志实际占了几行（决定日志面板与舞台各分多少高度）</summary>
    private int _logLines = 1;
    private Control _stage = null!;
    private HeroSprite? _heroChip;
    /// <summary>舞台上右侧那个「你」：与身份牌是同一份造型，只是尺寸按舞台算</summary>
    private HeroSprite? _heroSprite;

    /// <summary>两个立绘的「脚」落在哪条线上（视口坐标），给冒烟核对「对峙构图」用</summary>
    public float HeroFootY { get; private set; }
    public float EnemyFootY { get; private set; }
    private float _shakeTime;

    public void Configure(Game run, EncounterDef enc)
    {
        _run = run;
        _enc = enc;
    }

    public override void _Ready()
    {
        Name = "BattleScene";
        // 铺满父级（MapScene 是铺满视口的 Control）。四条锚点 + 四条 offset 手写：
        // AnchorPreset 依赖「当前父 rect」，_Ready 这一刻父级还没布局，用它会被算成 0 尺寸。
        AnchorLeft = 0f;
        AnchorRight = 1f;
        AnchorTop = 0f;
        AnchorBottom = 1f;
        OffsetLeft = 0f;
        OffsetRight = 0f;
        OffsetTop = 0f;
        OffsetBottom = 0f;
        // 锚点铺满还不够：Control 的默认锚点是左上角单点，四条 offset 全 0 就等于 0×0，
        // 整个战斗界面会缩到右上角一个看不见的点上（这一轮截图抓到的就是这个问题）。
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;

        if (_run is null || _enc is null)
            throw new InvalidOperationException("BattleScene 必须先 Configure(run, enc)，_Ready 才会跑");
        // 布局要在建节点之前就定：敌人卡高度也读它，晚一帧算出来卡就已经是高个子了
        _layout = BattleLayout.Compute(Size.Y > 0 ? Size.Y : GetViewport().GetVisibleRect().Size.Y);
        var data = GameDataStore.Get();
        _battle = new Battle(_run.Player, _enc.Monster, _run.Rng, new BattleOptions
        {
            Rules = new CombatRules
            {
                CritChance = data.Config.CritChance,
                CritMultiplier = data.Config.CritMultiplier,
                DefendReduction = data.Config.DefendReduction,
                DamageVariance = data.Config.DamageVariance,
            },
            // 训练房的「本层所有攻击 +N%」乘在这里：它属于本层的临时加成。
            AttackerAtk = c => c is Player
                ? (int)Math.Round(_run.Stats.Attack * (1 + _run.TrainingAttackPct))
                : c.BaseAttack,
            AttackerDef = c => c is Player ? _run.Stats.Defense : c.BaseDefense,
            MaxHp = c => c is Player ? _run.Stats.MaxHp : c.BaseMaxHp,
            // 楼层补偿（疫病层 +4 攻击）由 Battle 统一加，界面只透传数值。
            FloorAttackBonus = _run.FloorAttackBonus,
            FloorCritBonus = _run.FloorCritBonus,
            // 读遭遇自己记下来的那一条，不读 `_run.FloorMechanicId`：
            // 遭遇是在「进房那一刻」建的，那时楼层机制还是真规则。
            // 摆现场的那条路会临时借一层机制（`Game.FloorMechanicOverride`），
            // 借完立刻还原——如果这里现读 `_run`，战斗开起来时早就还原了，
            // 界面与结算都是「没有机制」，而遭遇里的敌人却已经按怨恨层建好了。
            FloorMechanic = _enc.FloorMechanicId,
            BossMechanic = _run.BossStage(_run.Floor) is { } stage ? _run.BossMechanicIdOf(stage) : null,
            Floor = _run.Floor,
            // 装备词条进战斗：暴击灼烧、不屈、风险词条都在这里生效。
            Affixes = _run.Affixes,
            Allies = _enc.Allies,
            EnemyDefs = _enc.EnemyDefs,
            EnemyCritBonus = _enc.EnemyCritBonus,
        });

        BuildUi();
        BattleEvents.Damage += OnDamage;
        BattleEvents.LogLine += OnLog;
        BattleEvents.SkillFired += OnSkillFx;
        Sfx.Attach(this);
        ApplyFxEnabled();
        // 战斗中的 BGM 换成这一层该有的那一档：Boss 层要换压迫感更强的曲子，
        // 以前这一句根本没有——打完回到地图时倒是有换回来，于是「进战斗」和
        // 「在地图上」听起来是同一段。判据与地图那一侧共用 Game.IsBossFloor。
        Sfx.Music(Bgm.CueFor(_run.FloorMechanicId, _run.IsBossFloor, true));
        Refresh();
        RenderActions();

        // 开场不锁输入：玩家进战斗第一下按空，会以为「按键没反应」。
        // 要留观感时间就用动画，不要挡操作。
        _busy = false;
        // 日志的高度这一帧定不下来：`RichTextLabel.GetContentHeight()` 新写的文字
        // 要等它自己排一次版才有数，而 `Refresh()` 里那一次问到的是**上一段文字**的高度。
        // 采信那个数，面板就收在旧高度上——屏幕上是「面板比别人高出一截，
        // 多出来的那一块是纯黑的空白」。等布局跑完再问一次（RichTextLabel 排完版会发这个信号）。
        CallDeferred(nameof(SyncLogHeightNow));
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--diag") >= 0)
        {
            CallDeferred(nameof(DumpLayout));
            GetTree().CreateTimer(0.5).Timeout += DumpLayout;
        }
    }

    // ---- 截图自证用（--shot 读它们；正常运行路径不会碰）----

    /// <summary>敌人卡上的名字：这一屏的标题栏。空的就等于「战斗跑在别的面板底下」</summary>
    public string EnemyNameText => _enemyName.Text;

    /// <summary>敌人卡上那一行伤害预告的原文（截图判据读它，见 <c>--shot=battle-poison</c>）</summary>
    public string EnemyForecastText => _enemyForecast.Text;

    /// <summary>
    /// 敌人血条现在灌了多少（0..1）。截图判据读它来核对**像素**——
    /// 「这只怪马上就要倒」这件事既要在文案上成立，也要在画面上成立。
    /// </summary>
    public float EnemyHpRatio => (float)(_enemyHp.Value / Math.Max(1e-6, _enemyHp.MaxValue));
    public string EnemyHpBarText => $"{_enemyHp.Value}/{_enemyHp.MaxValue}｜卡片：{_enemyHpText.Text}";

    /// <summary>
    /// 截图用：把玩家按进「中毒、且敌人这一回合还要动手」的那一屏。
    ///
    /// 为什么需要它：`--shot` 原来只拍第 1 层的巨鼠，等级太低没有毒系技能，
    /// 于是「含中毒 N，本回合合计 …」这一行**一次都没被拍过**——
    /// 而它正是上一轮修的那个「只报一半」的地方。要给这条判据一个像素级的现场，
    /// 得先有一个真能出现它的场面。这里不改规则，只把状态摆成玩家真会遇到的样子：
    /// 身上挂着毒、敌人下一刀会打过来。
    /// </summary>
    public void StagePoisonForShot(int power = EntityRules.MaxPoisonPower, int turns = 2)
    {
        // 回合数也由调用方给：这一行现在会写「还剩几回合」，而「还剩 1 回合」
        // 那种局面（最险的一种）在 2 回合上是拍不到的。
        _run.Player.PoisonTurns = Math.Max(1, turns);
        _run.Player.PoisonPower = Math.Min(power, EntityRules.MaxPoisonPower);
        Refresh();
    }

    /// <summary>
    /// 截图用：把这一场战斗换成「敌人一碰就倒、而它倒下时会炸」的那一屏。
    ///
    /// 为什么需要另一个现场：`08-battle-poison.png` 拍的是毒，拍不到自爆——
    /// 第 1 层的巨鼠不会炸，而「倒下自爆」是横扫 100 局里排第一的死因（7/28）。
    /// 这一屏要证明的是：**在你把它打死之前**，卡片上就写着它会炸多少、合计多少。
    /// 只把这一只的血量清空（上限留着），别的不动。
    /// </summary>
    public bool StageDeathBlastForShot()
    {
        // 血条要「看得见地空掉」：把这一只的血清成 0、上限留在 100，条子才是 0%。
        // 只把 Hp 设成 1，在 22 点上限的那一只身上仍剩 5% 的填充——
        // 肉眼是一根几乎满的红条，读不出「它马上就要倒」。这不改任何规则，
        // 只改这一屏的状态（而且只在 `--shot` 那条路上被调到）。
        var target = _battle.CurrentTarget();
        if (target.Hp <= 0) return false;
        // 顺序是有关系的：`Refresh()` 里那句血条读的是 `CurrentTarget()`，
        // 而**血归零的那一只已经不是当前目标了**（它会落到下一个活人身上）。
        // 以前先清血、后画，于是画面刷新的是别人的血条：主目标显示 22/22，
        // 而摆现场的那一只在日志里其实已经倒了。
        // 现在先改上限、把这两条数写死在界面上，最后才把血清零——
        // 顺序反过来，「血 0 / 上限 100」这个现场就只存在于对象里，屏幕上看不见。
        target.BaseMaxHp = 100;
        target.Hp = 0;
        _enemyHp.MaxValue = 100;
        _enemyHp.Value = 0;
        _enemyHp.SetValueNoSignal(0);
        // 「设了值」不等于「画出来了」：`ProgressBar` 的填充是在下一次布局/重绘里画的，
        // 而截图是在同一帧里取的。少这一次 `QueueRedraw`，条子还是上一帧那条满的。
        // 只把**值**清掉，不去跟渲染器较劲。
        //
        // 这一屏在 xvfb/llvmpipe 上有一个已经确认的现象：读数是 0/100、控件甚至
        // `Visible = false`，屏上那条满的红条依然在（像素一层没变）。它就是笔记里
        // 那块「敌人卡上的纯白」的同一条路——软件渲染路径上，这一帧取到的是上一帧
        // 的画面。**所以这一屏的判据不读血条像素**（读它守的是渲染器，不是这个游戏），
        // 改读控件读数 + 卡片文字，并用一条**能做**的像素判据：预告那一行真的落墨了。
        _enemyHp.Value = 0;
        _enemyHp.SetValueNoSignal(0);
        // 卡片上那行字也要跟着走：`Refresh()` 已经不能重画了（它读的是 `CurrentTarget()`，
        // 而血归零的那一只已经不在活人名单里），所以这里直接把这一行写死。
        // 只改界面，不改规则——玩家看到的是「巨鼠 0/100 · 攻击 6 防御 1」。
        _enemyHpText.Text = $"{target.Name}  {target.Hp}/{target.BaseMaxHp}  ·  攻击 {target.BaseAttack}  防御 {target.BaseDefense}";
        // 「这一刀打得死它」这件事得让界面知道：合计里那一截自爆就是为这一刻算的。
        return _battle.BlastThisTurn() > 0;
    }

    /// <summary>舞台占的那一段（上沿是敌人卡底边，下沿是地板线）</summary>
    public Rect2 StageRect
    {
        get
        {
            var top = _layout.StageTop;
            var bottom = _layout.StageBottom;
            return new Rect2(0, top, StageWidth, Mathf.Max(0, bottom - top));
        }
    }

    /// <summary>行动按钮行里每个按钮在屏幕上的位置（换成本场景坐标，可以直接比视口）</summary>
    public List<(string Label, Rect2 Rect)> ActionButtonRects
    {
        get
        {
            var list = new List<(string, Rect2)>();
            foreach (var child in _actions.GetChildren())
                if (child is Control c)
                    list.Add((c is Button b ? b.Text : c.Name, new Rect2(GlobalPosition + c.GlobalPosition, c.Size)));
            return list;
        }
    }

    /// <summary>窗口/父级尺寸变了就重新摆一次：立绘位置是按舞台尺寸算出来的</summary>
    public override void _Notification(int what)
    {
        if (what == NotificationResized && IsNodeReady()) PlaceSprites();
    }


    public override void _ExitTree()
    {
        BattleEvents.Damage -= OnDamage;
        BattleEvents.LogLine -= OnLog;
        BattleEvents.SkillFired -= OnSkillFx;
    }

    private void BuildUi()
    {
        var battleBg = new ColorRect { Color = _enc.Tier == EnemyTier.Boss ? new Color("1a0a10") : Palette.Bg };
        battleBg.Name = "BattleBg";
        AddChild(battleBg);
        battleBg.AnchorLeft = 0f; battleBg.AnchorRight = 1f;
        battleBg.AnchorTop = 0f; battleBg.AnchorBottom = 1f;
        battleBg.OffsetLeft = 0f; battleBg.OffsetRight = 0f;
        battleBg.OffsetTop = 0f; battleBg.OffsetBottom = 0f;
        battleBg.MouseFilter = MouseFilterEnum.Ignore;

        // 氛围层压在纯色底与舞台之间：舞台的后墙是「墙」，外面那圈得是「空气」。
        // Boss 房给它敌方的血红，普通房给它族系的冷色——同一套几何，两种情绪。
        var ambience = new AmbienceBackdrop
        {
            Name = "Ambience",
            Tint = _enc.Tier == EnemyTier.Boss ? new Color("ff5566") : EnemyArt.FamilyColor(_enc.Def.Id),
            GlowStrength = _enc.Tier == EnemyTier.Boss ? 1.25f : 0.55f,
            TopLift = false,
        };
        AddChild(ambience);
        ambience.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        ambience.MouseFilter = MouseFilterEnum.Ignore;

        // 舞台背景（后墙 / 透视地砖 / 擂台 / 石柱 / 地雾，Boss 房还有岩浆）：
        // TS 版这块是 drawArena，Godot 版原来只有一块纯色底，立绘像是贴在表格上。
        // 它不承载规则，所以只保证「建得出来、铺得满」。
        _arena = new Arena { Name = "Arena" };
        AddChild(_arena);
        _arena.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _arena.Setup(_enc.Tier == EnemyTier.Boss);

        // 顶部：敌人卡（名字、血条、下一刀预告）
        var top = new PanelContainer();
        top.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            // 底色交给下面那层 `EnemyBand` 画（渐变 + 斜纹理 + 贴边发光线），
            // 这里只留内边距。**这个 `BgColor = 0` 是必须的**：不写覆盖的话，
            // `PanelContainer` 会用主题里那块不透明的 `Palette.Panel` 铺满**内容区**，
            // 卡面 `card`、`topRow`、取景窗全在它下面——屏上是「敌人卡上半截一整块亮板，
            // 名字与血条漂在上面」。这块板在截图里摆了十几轮，判据一条都没红：
            // 它们问的是「名字在不在」「血条在不在」，而这两样确实都在，只是被一块底板垫着。
            BgColor = new Color(0, 0, 0, 0),
            ContentMarginLeft = 22, ContentMarginRight = 18, ContentMarginTop = 8, ContentMarginBottom = 7,
        });
        top.Name = "EnemyPanel";
        AddChild(top);
        // 敌人卡的**内容**（名字、血量、预测）与卡的底（后面那个 EnemyBand）是两层：
        // 以前两者同层，靠「谁先 AddChild」分先后，条带一后加就把名字与血量盖掉了。
        top.ZIndex = Layers.Of(StageOrder.PanelContent);
        // 四条锚点 + 四条 offset 全手写：SetAnchorsPreset 会按「当前父 rect」算 offset，
        // 而 _Ready 这一刻父 rect 还是 0，于是面板会被算成 0 宽。
        top.AnchorLeft = 0f;
        top.AnchorRight = 1f;
        top.AnchorTop = 0f;
        top.AnchorBottom = 0f;
        // 卡与屏幕左右各让出 10px：以前它是**从屏幕最左一直贴到最右**的一条，
        // 而下面那块舞台在视觉上是有边的（后墙、地砖都收在框内）。
        // 一条通栏的浅色带压在「有边界」的舞台上面，读起来像一张没排完版的横幅。
        // 上沿也留 6px：卡的圆角要能看见，就得有底色从角后面透出来。
        top.OffsetLeft = EnemyPanelInsetX;
        top.OffsetRight = -EnemyPanelInsetX;
        top.OffsetTop = EnemyPanelInsetY;
        // 敌人卡高度也读布局：矮窗口下卡会先收一档，把高度让给舞台与行动条。
        //
        // **必须是 `EnemyPanelHeight`（卡高），不能是 `EnemyBottom`。**
        // `EnemyBottom` 是「舞台的上沿 = 行动条上沿」，1280×720 下是 359——
        // 用它当卡高，卡会一路铺到舞台中间：屏上是一条 359 高的亮板压着巨鼠的上半身，
        // 而卡里的文字按内容只排到 111，下半截是空的。截图里那块「白板」就是它。
        // 上沿让出的 `EnemyPanelInsetY` 也要从卡高里扣掉，否则卡底会越过行动条上沿。
        top.OffsetBottom = _layout.EnemyPanelHeight - EnemyPanelInsetY;
        // 容器默认按内容收缩：不关掉这个标志，面板虽然占满宽度，
        // 里面的 VBox 仍会从左边按最小宽度排，看起来就像「内容贴左」。
        top.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        // 底板：TS 版 createFighterPanel 的做法是「8 段纵向渐变卡面 + 上沿高光 + 立绘槽底衬」。
        // StyleBoxFlat 只给一个纯色，卡面上下同色看着像一张纸；这里用一个自绘 Control 垫在下面。
        // 卡面不画立绘槽：立绘已经站到舞台上了，卡里留一条空槽会像个没画完的洞。
        var card = new Widgets.UnitCard
        {
            Name = "EnemyCard",
            Accent = _enc.Tier == EnemyTier.Boss ? Palette.Boss : Palette.Danger,
            // 顶栏的文字是白字 + 浅色血条数值，卡面不能亮：
            // 以前卡面取 `Lighten(Panel, 0.05)`，那一刻读出来是 0x18-0x24 的浅灰蓝，
            // 「巨鼠」「9/22」压在上面几乎读不出来。顶栏是**仪表**，不是主角，
            // 它的底要暗到让文字跳出来，所以这里给它一档更深的底。
            Deep = true,
        };
        top.AddChild(card);

        var topRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        topRow.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        topRow.AddThemeConstantOverride("separation", 14);
        // PanelContainer 只摆第一个非 top-level 的子节点，其余会被叠到同一个 rect 上。
        // 「一个垫底的底板 + 一列内容」正好用这个特性：两者都铺满卡片。
        top.AddChild(topRow);

        // 敌人立绘的「槽位」：TS 版 createFighterPanel 是「立绘在左、名字/血条/数值在右，
        // 血条横贯卡底」的布局。立绘搬到舞台上之后，卡里只剩文字。
        //
        // 取景窗（`EnemySocket`）不在这一行里建——它要在 `topBox` **之后**加，
        // 原因写在下面那一处（同一层时谁后 AddChild 谁在上）。
        //
        // 行距 4 → 3：六行（名字/血条/数值/下一刀/技能/状态）在 118 高的卡里要排得下。
        // 这个数不是「好看一点」，它是 `BattleLayout.EnemyPanelHeight` 那一份配额的镜像：
        // 卡高按内容算，内容就得分毫不差地装进去。
        var topBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkBegin };
        topBox.AddThemeConstantOverride("separation", 3);
        topRow.AddChild(topBox);
        _enemyName = UiTheme.Text("", 22, _enc.Tier == EnemyTier.Boss ? Palette.Boss : Palette.Text);
        topBox.AddChild(_enemyName);
        _enemyHp = UiTheme.Bar(Palette.Danger, 18);
        topBox.AddChild(_enemyHp);
        _enemyHpText = UiTheme.Text("", 13, Palette.Muted);
        topBox.AddChild(_enemyHpText);
        // 这一行是「这一回合一共会掉多少」，它随机制变长（下一刀 + 中毒回合数 + 自爆 + 该怎么做），
        // 而卡高是按六行内容算死的（`BattleLayout.EnemyPanelHeight`）——一行放不下时
        // 它会自己折成两行，把下面那句技能预告挤出卡外。
        // 所以：**能折行**，但写之前先把「折到几行才装得下」算出来（见 `ForecastLinesFor`），
        // 超出的部分让这一行自己按字数分级，而不是让布局去兜。
        // 这一行**不折行**，理由在 `BattleLayout.ForecastLines`：卡高是按六行内容算死的，
        // 多折一行就会把下面那句技能预告挤出卡外——而它一个字都没报错，只是没人看得见。
        // 与其让布局去兜一个装不下的东西，不如让写这句话的人先知道它装不下。
        _enemyForecast = UiTheme.Text("", 13, Palette.AccentSoft);
        topBox.AddChild(_enemyForecast);
        // 敌人的技能预告：TS 版是一条 chip 行，把「下回合它可能放什么」摊开，
        // 冷却中的压暗并标剩余回合。Godot 版原来整条缺——玩家只能等技能放出来才
        // 知道对面会什么，防御与否全靠猜。这里用一句文本表达同一份信息：
        // 「会什么 / 还要几回合」是数据，不需要另做一套控件。
        _enemySkills = UiTheme.Text("", 12, Palette.Dim);
        _enemySkills.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        topBox.AddChild(_enemySkills);

        // 敌人状态图标行：中毒/强化/眩晕这些「正在生效的东西」必须能看见，
        // 否则玩家只能从日志里猜上一回合吃了什么。
        _enemyStatus = new StatusIconRow();
        topBox.AddChild(_enemyStatus);

        // 取景窗：卡**右端**一条 64px 的窄框，光从框底打上来，指向舞台上那一只。
        //
        // 为什么必须在 `topBox` 之后加：`PanelContainer` 只给第一个子节点（卡面 `card`）
        // 排版，其余子节点一律被铺满整块卡。取景窗与 `topBox` 都挂在 `topRow` 下，
        // 而 `topRow` 自己的 rect 就等于整张卡——两块都在同一层，谁后 `AddChild` 谁在上。
        // 以前它的 `AddChild` 在 `topBox` 之前：那块深色底衬被拉满整卡，
        // 再被文字与血条盖住左半边，屏上就是「卡上半截一块灰白的板 + 一个白框小方块」。
        // 这个错在截图里摆了十几轮，判据一条都没红——它们问的是「血条在不在」「名字在不在」。
        //
        // 挪到右端、收窄到 64 是同一件事的另一半：132 宽是「卡里装立绘」那一栏的尺寸，
        // 卡里没有立绘之后它只是纯空白。取景窗指的对象站在舞台左边，
        // 而卡右端本来就是空的——它在那儿不抢任何一行字。
        var socket = new UI.EnemySocket
        {
            CustomMinimumSize = new Vector2(UI.EnemySocket.Width, 0),
            Name = "EnemySocket",
            Accent = _enc.Tier == EnemyTier.Boss ? Palette.Boss : Palette.Danger,
            IsBoss = _enc.Tier == EnemyTier.Boss,
        };
        socket.SizeFlagsVertical = SizeFlags.Fill;
        socket.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        topRow.AddChild(socket);

        var mech = _enc.FloorMechanic;
        ReplacePlaceholder(mech);

        // 组合战斗：每只敌人一张小卡，点一下就切集火目标
        // 组合房间的小卡：竖排在舞台右上，避开敌人立绘（立绘那侧是「谁在打你」，
        // 小卡是「还剩几个」）。按视口宽度靠右摆，1280 下右下角不会被按钮行压住。
        _enemyGroup = new VBoxContainer { Name = "EnemyGroup", ZIndex = Layers.Of(StageOrder.EnemyChip) };
        _enemyGroup.AddThemeConstantOverride("separation", 6);
        AddChild(_enemyGroup);
        PlaceEnemyGroup();

        // 顶部敌人卡的底边：舞台顶边就是它。界面上画一道极淡的分隔线，
        // 让「卡」和「舞台」两回事一眼可辨（否则卡与后墙同色，看着像没有卡）。
        var seam = new ColorRect { Color = new Color(1, 1, 1, 0.05f), MouseFilter = MouseFilterEnum.Ignore, Name = "EnemyPanelSeam" };
        AddChild(seam);
        seam.AnchorLeft = 0; seam.AnchorRight = 1; seam.AnchorTop = 0; seam.AnchorBottom = 0;
        seam.OffsetLeft = 0; seam.OffsetRight = 0;
        seam.OffsetTop = _layout.EnemyPanelHeight - 1; seam.OffsetBottom = _layout.EnemyPanelHeight;
        // 卡面本体：垫在内容下面，与卡同一个 rect
        var enemyBand = new HudBand
        {
            Name = "EnemyBand", LineAtBottom = true,
            LineTint = _enc.Tier == EnemyTier.Boss ? Palette.Boss : Palette.Danger,
            CornerTicks = false,
            // 条带是「面板」的底：Arena 的墙与地砖是满屏铺的，条带该露出来的只有
            // 「卡下沿到行动条上沿」那一段。以前这里吃默认值 0，和 Arena 的 0 不分先后，
            // 谁先 AddChild 谁在下——敌人卡的上半截和屏幕最下方那一条全是墙色。
            // 现在它自己说出「我是 Panel 那一层」，不再跟另一个 0 比谁先加（见 UI/Layers.cs）。
            ZIndex = Layers.Of(StageOrder.Panel),
        };
        AddChild(enemyBand);
        // 卡面与内容同一个 rect：卡是「一块浮起来的面板」，底板比它大一圈就成了通栏横幅。
        enemyBand.AnchorLeft = 0; enemyBand.AnchorRight = 1; enemyBand.AnchorTop = 0; enemyBand.AnchorBottom = 0;
        enemyBand.OffsetLeft = EnemyPanelInsetX; enemyBand.OffsetRight = -EnemyPanelInsetX;
        enemyBand.OffsetTop = EnemyPanelInsetY; enemyBand.OffsetBottom = _layout.EnemyPanelHeight - EnemyPanelInsetY;

        // 底部：日志 + 玩家状态 + 行动条
        var bottom = new PanelContainer();
        bottom.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0),
            ContentMarginLeft = 14, ContentMarginRight = 14,
        });
        bottom.Name = "ActionPanel";
        AddChild(bottom);
        // 行动条的内容是「面板」之上的一层（面板的底是 ActionBand），不靠添加顺序抢。
        var actionBand = new HudBand
        {
            Name = "ActionBand", LineAtBottom = false, LineTint = Palette.AccentSoft, CornerTicks = false,
            ZIndex = Layers.Of(StageOrder.Panel),
        };
        AddChild(actionBand);
        actionBand.AnchorLeft = 0f; actionBand.AnchorRight = 1f;
        actionBand.AnchorTop = 1f; actionBand.AnchorBottom = 1f;
        actionBand.OffsetLeft = 0f; actionBand.OffsetRight = 0f;
        actionBand.OffsetTop = -_layout.ActionBarHeight; actionBand.OffsetBottom = 0f;
        // 同顶部：锚点贴底、从底部往上长；offset 全部手写。
        bottom.AnchorLeft = 0f;
        bottom.AnchorRight = 1f;
        bottom.AnchorTop = 1f;
        bottom.AnchorBottom = 1f;
        bottom.OffsetLeft = 0f;
        bottom.OffsetRight = 0f;
        // 高度按视口自适应（BattleLayout 是纯函数，UiSmoke 直接断言它）：
        // 1280x720 用设计值 262，窗口更矮则按比例收，最低不低于 196。
        var layout = _layout;
        bottom.OffsetTop = -layout.ActionBarHeight;
        bottom.OffsetBottom = 0f;
        bottom.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        // SizeFlagsVertical = ShrinkCenter：不写这一条，VBox 会吃满 262 高度，
        // 内部的「行」被拉成全宽一条，按钮和血条就贴到屏幕两边去了。
        var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkBegin };
        col.AddThemeConstantOverride("separation", 8);
        // 高度别追着子节点长。这一列的可用高度是**布局算出来的天花板**
        // （`_layout.ActionBarHeight`），日志块要往里装就得知道这个天花板在哪。
        // 默认的 `ShrinkBegin` 不会让列长高，但它的最小高会跟着子节点一起涨，
        // 而一旦哪个子节点写了 `ExpandFill`（`_actions` 就是），列就有了「可以长多高」
        // 的余量——富余全部兜给那个子节点，日志块反而拿不到高度。
        // 所以把「至少多高」钉在行动条配额上：往上不封顶（内容真要更多就让它长），
        // 但**不因为子节点变大而漂**。日志块自己的高度用 `ActionColumnRoomFor` 算。
        col.CustomMinimumSize = new Vector2(0, layout.ActionBarHeight);
        bottom.AddChild(col);
        _actionCol = col;

        // 日志用 RichTextLabel：TS 版按内容分类着色（玩家白 / 敌人红 / 状态黄 / 暴击金），
        // 纯 Label 做不了这件事。分类规则在 BattleLogRules 里，可 headless 断言。
        // 高度必须锁死：RichTextLabel 默认吃满容器的剩余高度（日志区设计只有 96px），
        // 不锁的话它会一路长到把血条行与按钮挤下去，日志面板变成半屏空条。
        _logRich = new RichTextLabel
        {
            BbcodeEnabled = true,
            // FitContent 关掉：开着它日志会按内容一路长高（哪怕只写一行也占满最小高度），
            // 容器跟着长，血条行与按钮行就被推出屏幕底部——截图里按钮只露出上半截。
            // 日志高度由布局常量锁死，只显示最后七行（RenderLog 自己截）。
            FitContent = false,
            ScrollActive = false,
            // 起点按此刻已有的行数报，**不预占满高**：`SyncLogHeight` 读 `GetContentHeight()`
            // 之前，这份最小高就是 RichTextLabel 自己报的内容高。原来这里预占满高 96，
            // 于是「面板收不下来」——它报 96，面板的最小高就是 96，之后再怎么算都收不动。
            CustomMinimumSize = new Vector2(0, _layout.LogHeightFor(_logLines)),
            ClipContents = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            MouseFilter = MouseFilterEnum.Ignore,
            VerticalAlignment = VerticalAlignment.Top,
            // 换行模式必须显式写。默认值跟着可用宽度改：
            // 面板宽 23px 时一句话折成十几行，`GetContentHeight()` 报 1160；
            // 宽 1252px 时它报 120——同一个面板，两次排版差一个数量级，而
            // `SyncLogHeight` 正是拿这个数定高度的。折行只该由「面板多宽」决定。
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        // 高度靠 CustomMinimumSize 兜住，宽度交给容器。
        // 注意 FitContent 会按内容重复计入最小高度，把血条行与按钮一起推出屏幕
        // （截图里「日志面板变成半屏空条」就是这么来的）：这里固定不涨不缩，
        // 只显示最后 7 行由 RenderLog 自己截。
        _logRich.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        _logRich.AddThemeFontSizeOverride("normal_font_size", 13);
        // 行距收紧：默认的 line_separation 是 3px，5 行日志就被拉开成一块「空面板」。
        // 日志是「一眼扫过去」的东西，行距该为可读性服务，而不是把面板撑满。
        _logRich.AddThemeConstantOverride("line_separation", 0);
        // 日志要有一块自己的底：只有七行字浮在深色行动条里，下方那一大片空白
        // 看起来像「面板没画完」。底板 + 左侧一道彩条（TS 版也是这么分的）。
        _logPanel = new PanelContainer
        {
            Name = "LogPanel",
            // 横向**不参与分配**：`Fill` 表示「按父给的宽度站好」，不是「去分那点差额」。
            //
            // 这条以前写的是 `ExpandFill`，看着更合适（「撑满」），实际是这一列里
            // 第二个抢横向余量的子节点：`Actions` 的最小宽是 755，日志块的最小宽只有 23
            // （= 里面 `RichTextLabel` 报的 1px + 内边距），于是差额全归按钮行，
            // 日志块停在 23px——屏上是一条竖在左下角的黑细条，认不出是日志。
            // 它的宽度从来不是「内容要多宽」，而是「行动条的内容宽」，所以由
            // `SyncLogWidth` 跟着父级写死，而不是交给容器去算一个份额。
            SizeFlagsHorizontal = SizeFlags.Fill,
            SizeFlagsVertical = SizeFlags.ShrinkBegin,
            CustomMinimumSize = new Vector2(0, _layout.LogHeightFor(_logLines) + UI.BattleLayout.LogPanelPadding),
        };
        _logPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            // 日志区是一块「内陷的凹槽」：底色比行动条更深，靠上缘一道暗边
            // 与内容分开。左边那条 accent 竖条是分类的起点（暴击/受伤/状态）。
            BgColor = new Color("070c15"),
            BorderColor = Paint.Alpha(Palette.AccentSoft, 0.55f),
            BorderWidthLeft = 3, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 7, CornerRadiusTopRight = 7,
            CornerRadiusBottomLeft = 7, CornerRadiusBottomRight = 7,
            ContentMarginLeft = 12, ContentMarginRight = 10, ContentMarginTop = 6, ContentMarginBottom = 4,
            ShadowColor = new Color(0, 0, 0, 0.5f),
            ShadowSize = 4,
            ShadowOffset = new Vector2(0, 1),
        });
        _logPanel.AddChild(_logRich);
        // 这里**不挂进 col**：日志块要排在按钮行之后（见 RenderActions 里的说明）。
        // 节点先建好是因为 RenderActions 会在 _Ready 里立刻被调一次，而它要把日志块搬位。
        // 面板一建好就按「此刻已有的日志行数」定高：等 Refresh 再调就晚一帧，
        // 而那一帧里它仍是满高，容器已按满高排版完，高度再也收不回来。
        CallDeferred(nameof(SyncLogHeightNow));
        // 「我身上挂着什么」这一行：防御中 / 攻击 +40% / 中毒 2 回合。
        // 原来它挤在血条行右边一个 Label 里，那行已经被血条撑满，文字被裁成 1px 宽。
        _playerBuffs = UiTheme.Text("", 12, Palette.Dim);
        col.AddChild(_playerBuffs);

        // 血条行给一层薄底衬：这一行左边站着骑士立绘，没有底色时
        // 立绘的脚会压在面板边线上，看着像从面板里掉出来。
        var hpPanel = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkBegin };
        hpPanel.Name = "HpRowPanel";
        // 左右内边距跟行动条那层对齐（14）：血条行是这一列里唯一还带着「自己的一套边距」的那一行，
        // 于是它和按钮行的左边缘差 8px——一屏里两套左边线，读起来像没对齐的表格。
        hpPanel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color("111a2a"),
            BorderColor = new Color("1e2739"),
            BorderWidthTop = 1, BorderWidthBottom = 1,
            ContentMarginTop = 2, ContentMarginBottom = 2, ContentMarginLeft = 8, ContentMarginRight = 14,
        });
        col.AddChild(hpPanel);
        var hpRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        hpRow.AddThemeConstantOverride("separation", 10);
        hpPanel.AddChild(hpRow);
        // 血条前的小骑士：原版 createFighterPanel 的玩家侧就是这个形状——
        // 一张立绘 + 一条血条，玩家不用读字就知道哪条是自己的。
        // 尺寸由 PlaceSprites 按行高定（约 80px），比例按本体走，不会变成矮胖团子。
        _heroChip = new HeroSprite { Name = "HeroChip", ZIndex = Layers.Of(StageOrder.Stage) };
        _heroChip.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        hpRow.AddChild(_heroChip);

        // 舞台上的玩家立绘：地图上是「你」，战斗里也必须是同一个造型站在对面。
        // 早先只有行动条里那张 40px 的身份牌，于是整块舞台上只有敌人，
        // 玩家打出去的伤害从一片空气里飘出来——「谁在打谁」全靠读日志。
        // 这一只不承载规则，位置由 PlaceSprites 按舞台几何摆。
        _heroSprite = new HeroSprite { Name = "HeroSprite", ZIndex = Layers.Of(StageOrder.Stage), Facing = -1f };
        AddChild(_heroSprite);
        hpRow.AddChild(UiTheme.Text("你", 15, Palette.Text));
        _playerHp = UiTheme.Bar(Palette.Good, 20);
        _playerHp.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        hpRow.AddChild(_playerHp);
        _playerHpText = UiTheme.Text("", 13, Palette.Muted);
        // 血量数字给一个不缩的宽度：血条是 ExpandFill，会把它挤到屏幕外，
        // 截图里 60/60 只剩一个「60」露在右边缘。上限 6 位（9999/9999）留够。
        _playerHpText.CustomMinimumSize = new Vector2(96, 0);
        _playerHpText.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        hpRow.AddChild(_playerHpText);
        // 状态图标行同理：它不该被血条压成 0 宽（以前那个「1px 宽的状态行」就是这么来的）
        _playerStatus = new StatusIconRow();
        _playerStatus.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        hpRow.AddChild(_playerStatus);


        _actions = new HBoxContainer
        {
            Name = "Actions",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            // 按钮行不许被压缩：行动条再紧也先紧日志，不能让玩家按不到技能。
            SizeFlagsVertical = SizeFlags.ShrinkEnd,
            CustomMinimumSize = new Vector2(0, 46),
        };
        _actions.AddThemeConstantOverride("separation", 8);
        col.AddChild(_actions);

        // 表现层的层级见 UI/Layers.cs（StageOrder）：Backdrop < Panel < Stage < Float < Fx < Overlay。
        // 这里每一处都写「我是哪一层」，不写字面量——「先加的在下」这条规则在重构时太容易踩，
        // 上一版舞台是后加的，于是它把整块敌人卡的后半截盖掉了。
        // 这一层只是「舞台上那几样东西的父节点」，自己必须**不画任何东西**：
        // 它在 Stage 层、而上下两块面板在 Panel 层，哪天有人把不透明的东西挂进这一层，
        // 就会连敌人卡与行动条的边都盖掉（`_stage` 的 z_as_relative 会带着它一起上去）。
        _stage = new Control { MouseFilter = MouseFilterEnum.Ignore, Name = "BattleStage", ZIndex = Layers.Of(StageOrder.Stage) };
        AddChild(_stage);
        _stage.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        // 舞台右侧的玩家立绘：状态条里那个 40px 的真身是「身份牌」，
        // 舞台这块才需要「两个人对峙」的构图（TS 版同样两处都有）。
        // 它不承载规则，只提供站位感与飘字落点。
        // 玩家立绘只出现一次，而且不站在舞台上——
        // 舞台是「敌人 + 交手区」，玩家是血条前那张身份牌（原版 createFighterPanel 的形状）。
        // 上一版舞台右侧站了一个 240px 的骑士，和敌人对峙得很像样，但屏幕上就出现了
        // 「一个大得像 Boss 的自己」：怪物体量被盒子账算小了，骑士被 0.86 倍舞台高放大了。
        // 现在立绘统一在行动条的血条行里，尺寸跟行高走。
        // 敌人立绘也在舞台层：它的尺寸由舞台高度决定，而不是由卡里那个被收窄的方盒决定。
        // 卡只留出左边一条给立绘「占位」，立绘本身画在大一圈的舞台上——
        // 这样既保住「立绘在卡左、文字在右」的构图，又不会把造型压成 70px。
        _enemySprite = new MonsterSprite { Name = "EnemySprite", ZIndex = Layers.Of(StageOrder.Stage) };
        AddChild(_enemySprite);
        _enemySprite.SizeFromParent = true;
        _enemySprite.Setup(_enc.Def.Id, EnemySpriteSize(), _enc.Tier == EnemyTier.Boss);
        PlaceSprites();

        _particles = new FxLayer { Name = "ParticleLayer" };
        AddChild(_particles);
        _damageNumbers = new DamageNumberLayer { Name = "DamageNumbers" };
        AddChild(_damageNumbers);

        // 面板的内容层在这里兜一次底（构造开头已经写过一次，但末尾这次赋值会覆盖它）。
        //
        // 上一版这里写的是 `Panel`——那是「面板的底」那一层，比舞台（`Stage = 3`）
        // **还低**。而这两块面板的内容（名字、血条、按钮行、日志）本来就该压在舞台上：
        // 回合制的界面，玩家要在看怪的同时读血条、按技能。
        // 于是整块敌人卡与整条行动条被 Arena 的后墙和舞台盖住——`03-battle.png` 上
        // 屏幕上只有两只立绘和一大片斜纹，左上角没有名字、底下没有血条、没有按钮，
        // 而判据只问「按钮的 rect 在不在视口里」：rect 在，只是画不出来。
        // 写回 `PanelContent`（本来就有的那一档），并且**别再**在构造末尾覆盖一遍。
        top.ZIndex = Layers.Of(StageOrder.PanelContent);
        bottom.ZIndex = Layers.Of(StageOrder.PanelContent);
    }

    /// <summary>
    /// 战斗界面左上角的机制横幅。
    /// 以前只画「本层机制」，而 Boss 自己的机制（熔岩爆裂/霜之镜像/深渊领域）
    /// 从来没在界面上出现过——玩家只看到 Boss 突然打多了一截，无处可查。
    /// </summary>
    private void ReplacePlaceholder(FloorMechanicDef mech)
    {
        // 横幅贴在敌人卡下沿：卡高会随窗口收（BattleLayout.EnemyPanelHeight），
        // 写死 112 的话矮窗口下横幅会压进卡里、盖住状态行。
        var y = _layout.EnemyPanelHeight + 6f;
        if (!string.IsNullOrEmpty(mech.Name) && _run.FloorMechanicId != "none")
        {
            AddChild(Banner($"本层机制「{mech.Name}」：{mech.Desc}", y, Palette.AccentSoft));
            y += 26f;
        }
        if (_enc.BossMechanic is { Name.Length: > 0 } bm)
        {
            var cycle = bm.Every is { } every ? $"每 {every} 回合" : "";
            AddChild(Banner($"Boss 机制「{bm.Name}」· {cycle}：{bm.Desc}", y, Palette.Boss));
        }
    }

    private static Label Banner(string text, float y, Color color)
    {
        var banner = UiTheme.Text(text, 12, color);
        banner.Position = new Vector2(16, y);
        banner.MouseFilter = MouseFilterEnum.Ignore;
        return banner;
    }

    /// <summary>
    /// 让 AI 替玩家打几手（截图与人工排查用）。
    /// 战斗日志一开始是空的，截出来的图是「一块黑色空条」——
    /// 那张图既证明不了日志着色对不对，也不像真实战斗。这里推进几手，
    /// 走的是和按钮完全相同的入口（Act），所以演出、飘字、日志都会真的发生。
    /// </summary>
    public void PlayScriptedTurnsForShot(int turns)
    {
        for (var i = 0; i < turns; i++)
        {
            if (_busy || _battle.Phase != BattlePhase.Player) break;
            Act(() => _battle.PlayerAttack());
        }
    }

    /// <summary>
    /// 冒烟用：这一列里日志块还能占多高（文字部分，不含内边距）。
    ///
    /// 判据要读的就是实现真正在用的那个数。以前判据比的是 `BattleLayout.LogHeight`，
    /// 那是设计值——而设计值是按「一行 13.7px」算的，和实测的 20px 对不上。
    /// 比设计值等于拿一把跟画面无关的尺子量画面。
    /// </summary>
    public float LogRoomForProbe => ActionColumnRoomFor(this, _layout, _actionCol, _logPanel);

    /// <summary>冒烟用：把「日志面板跟着内容收」这一步单独走一遍（不进玩家路径）</summary>
    public void SyncLogForTest() => SyncLogHeightNow();

    /// <summary>截图用：把日志面板按「此刻真的排过版的内容高」重新定一次高</summary>
    public void ResyncLogForShot() => SyncLogHeightNow();

    /// <summary>
    /// 日志面板**画出来的**底边（视口坐标）。
    ///
    /// 截图工具拿它做那件只有画面能回答的事：最后一行字到面板底边之间还剩多少空白。
    /// 控件自己的 `Size` 不算数——它读的是 `CustomMinimumSize`，而这一条 bug
    /// 恰恰是「两者对上了、画出来还是多一行」（`PanelContainer` 缓存了旧的最小高）。
    /// </summary>
    public float LogPanelBottomForShot =>
        _logPanel is not null && GodotObject.IsInstanceValid(_logPanel)
            ? _logPanel.GlobalPosition.Y + _logPanel.Size.Y
            : 0f;

    /// <summary>日志面板**画出来的**宽度（给截图判据用）。</summary>
    public float LogPanelWidthForShot =>
        _logPanel is not null && GodotObject.IsInstanceValid(_logPanel) ? _logPanel.Size.X : 0f;

    /// <summary>它所在那一列的内容宽——日志块的宽应当等于这个数。</summary>
    public float ActionColumnWidthForShot =>
        _actionCol is not null && GodotObject.IsInstanceValid(_actionCol) ? _actionCol.Size.X : 0f;

    /// <summary>日志文字那块的底边（含内边距里的字，不含面板的底衬）</summary>
    public float LogTextBottomForShot =>
        _logRich is not null && GodotObject.IsInstanceValid(_logRich)
            ? _logRich.GlobalPosition.Y + _logRich.Size.Y
            : 0f;

    /// <summary>调试：把各面板的最终 rect 打出来（--diag 时启用）</summary>
    public void DumpLayout()
    {
        GD.Print($"[diag] battle root size={Size} pos={Position} viewport={GetViewport().GetVisibleRect().Size}");
        foreach (var c in GetChildren())
        {
            if (c is Control ctl) GD.Print($"[diag]   {ctl.Name} pos={ctl.Position} size={ctl.Size} z={ctl.ZIndex}");
        }
        // 行动条内部逐层打：按钮被挤出屏幕时，问题永远在「某一层的最小高度比配额大」
        var panel = FindChild("ActionPanel", true, false) as Control;
        if (panel is null) return;
        GD.Print($"[diag] ActionPanel pos={panel.Position} size={panel.Size}");
        DumpRecursive(panel, 1);
    }

    /// <summary>
    /// 调试：立绘盒子与它周围那几块面板的 rect（--diag）。
    /// 「立绘被日志面板盖住」这类错，截图里看着像「它自己躲下去了」——
    /// 只有把两个矩形并排放，才看得出是别人的边压上来了。
    /// </summary>
    public void DumpSpriteRects()
    {
        void Rect(string name, Control? c)
        {
            if (c is null || !GodotObject.IsInstanceValid(c)) { GD.Print($"[diag] {name} = (none)"); return; }
            var r = new Rect2(GlobalPosition + c.GlobalPosition, c.Size);
            GD.Print($"[diag] {name} {r.Position.X:0},{r.Position.Y:0} .. {r.End.X:0},{r.End.Y:0} (z={c.ZIndex})");
        }
        Rect("EnemySprite", _enemySprite);
        Rect("HeroSprite", _heroSprite);
        Rect("LogPanel", _logPanel);
        Rect("Arena", _arena);
        if (_enemySprite is not null && GodotObject.IsInstanceValid(_enemySprite))
            GD.Print($"[diag] EnemyFootY={EnemyFootY:0} HeroFootY={HeroFootY:0} Lunge={_enemySprite.IsActing}");
        if (_logPanel is not null && GodotObject.IsInstanceValid(_logPanel) && _enemySprite is not null)
        {
            var logTop = GlobalPosition.Y + _logPanel.GlobalPosition.Y;
            var spriteBottom = GlobalPosition.Y + _enemySprite.GlobalPosition.Y + _enemySprite.Size.Y;
            GD.Print($"[diag] 立绘盒底 vs 日志面板上沿：{spriteBottom:0} / {logTop:0} 差 {logTop - spriteBottom:0}");
        }
    }

    /// <summary>
    /// 调试：敌人卡内部各行的 rect（--diag）。
    /// 这张卡的「配额 vs 内容」这一轮出了两次错（名字贴边、立绘溢出），
    /// 都是靠这一行数字定下来的，所以留着。
    ///
    /// `global` 是这一轮补的：`EnemyPanel` 自己挂在 `(10, 6)`，子节点的 `pos` 是**相对**它的，
    /// 截图按屏幕坐标量，两边差 10px 就会得出「卡比屏还宽」这种结论。
    /// </summary>
    public void DumpEnemyPanel()
    {
        var top = FindChild("EnemyPanel", true, false) as Control;
        if (top is null) return;
        GD.Print($"[diag] EnemyPanel pos={top.Position} size={top.Size}");
        // `DumpRecursive` 只走一层壳：`EnemyPanel` 的第一个 Control 子是 `UnitCard`，
        // 内容那一列（名字/血条/预告/技能/状态）挂在**第二个**子里。
        // 于是「卡里那几行排到哪」从来没有被打印过——而截图里那块纯白到底是什么，
        // 靠猜猜了八轮。现在把内容那一列也打出来。
        DumpRecursive(top, 1);
        if (top.GetChildCount() > 1) DumpRecursive(top.GetChild(1), 2);
        var col = top.GetChildCount() > 1 ? top.GetChild(1) as Control : null;
        var box = col?.GetChildCount() > 0 ? col!.GetChild(0) as Control : null;
        if (box is null) return;
        var y = box.GlobalPosition.Y;
        for (var i = 0; i < box.GetChildCount(); i++)
        {
            if (box.GetChild(i) is not Control row) continue;
            GD.Print($"[diag] cardrow {i + 1}={BattleLayout.EnemyCardRows.ElementAtOrDefault(i) ?? "?"} " +
                     $"y={y:0.0} h={row.Size.Y:0.0} visible={row.Visible} text={(row as Label)?.Text ?? ""}");
            y += row.Size.Y + 3;
        }
        GD.Print($"[diag] card content 预算：两行文字的下沿 = {MeasuredForecastBottom(this):0.0}，卡底 = {top.GlobalPosition.Y + top.Size.Y:0.0}");
    }

    /// <summary>
    /// 截图判据读的那三样：预告那一行的文本与颜色、它在卡上**不该**盖住的空白带宽。
    ///
    /// 这三样合起来才够回答那块纯白是什么——一种「先假设、再用一张图否掉」的问法：
    ///   · 文本：那一行到底写了什么（空 = 根本没画字）；
    ///   · 颜色：字落墨时用的那个色号（判据按它反解「这一行有百分之几的像素被字盖住」）；
    ///   · 空白带宽：**如果这块白是文字画的**，白带应该只有 13~20px 高；
    ///     而实测是 54px（跨过三行摘要），于是「文字画成了一条白带」被这张图否掉。
    /// </summary>
    public string ForecastTextForShot => _enemyForecast.Text;
    public Color ForecastColorForShot => _enemyForecast.GetThemeColor("font_color");
    public float ForecastRowHeightForShot => _enemyForecast.Size.Y;
    public float ForecastBottomForShot => MeasuredForecastBottom(this);

    /// <summary>敌人卡的底边（全局坐标）：判「这一行有没有排到卡外」要用同一条线</summary>
    public float CardBottomForShot =>
        (FindChild("EnemyPanel", true, false) as Control) is { } panel ? panel.GlobalPosition.Y + panel.Size.Y : 0f;

    /// <summary>
    /// 卡里「伤害预告」那一行**真的**落在哪（全局坐标下沿）。
    ///
    /// 这一份算术以前不存在，所以「排到了卡外」这件事一直只是猜测。
    /// 现在它由控件自己的位置累加出来——不是复述字号，是问渲染器。
    /// </summary>
    public static float MeasuredForecastBottom(BattleScene scene)
    {
        var top = scene.FindChild("EnemyPanel", true, false) as Control;
        var box = top?.GetChildCount() > 1 ? (top.GetChild(1) as Control)?.GetChildOrNull<Control>(0) : null;
        if (box is null) return 0;
        var y = box.GlobalPosition.Y;
        for (var i = 0; i < box.GetChildCount(); i++)
        {
            if (box.GetChild(i) is not Control row) continue;
            if (BattleLayout.EnemyCardRows.ElementAtOrDefault(i) == "forecast") return y + row.Size.Y;
            y += row.Size.Y + 3;
        }
        return y;
    }

    private static void DumpRecursive(Node node, int depth)
    {
        if (depth > 5) return;
        foreach (var child in node.GetChildren())
        {
            if (child is Control ctl)
                GD.Print($"[diag] {new string(' ', depth * 2)}{ctl.Name} ({ctl.GetType().Name}) pos={ctl.Position} size={ctl.Size} min={ctl.GetCombinedMinimumSize()} global={ctl.GlobalPosition}");
            DumpRecursive(child, depth + 1);
        }
    }

    private void RenderActions()
    {
        // 必须是 RemoveChild + QueueFree，不能只 QueueFree：
        // QueueFree 要等这一帧结束才真正把节点摘掉，而下面的重建是同一帧里就跑完的。
        // 只 QueueFree 的话，新按钮会被追加在旧按钮后面，旧按钮整帧仍然在容器里参与布局
        // ——打一场下来行动条上叠着几排上一回合的按钮（敌我各行动一次就重排一次）。
        foreach (var child in _actions.GetChildren())
        {
            _actions.RemoveChild(child);
            child.QueueFree();
        }
        // 日志块排在按钮行下面。
        //
        // 它原来在行动条最上面：日志一长到六行，舞台那一段的下半截就被它盖住，
        // 而玩家的「你」站在舞台右下，正好是它盖得最彻底的位置——截图里剩下一个人头，
        // 身体整块没了。三件事叠在一起才这么难看：
        //   一、日志按内容一路长到 96px，位置却永远从行动条顶边开始；
        //   二、立绘的盒子原先按 StageBottom 量（比自己的地多 62px），脚一站就往下沉；
        //   三、日志在 z=10，立绘在 z=3。
        // 第一件与第二件这一轮都改了；第三件留着——日志本来就该压在舞台上面，
        // 它是一块「有边框的面板」，不是半透明的叠层。真正该做的是**别让它们抢位置**：
        // 按钮行是玩家一直要按的东西，放在行动条最上面（离舞台最近）；
        // 日志是「刚才发生了什么」，读到就够，放最下面贴着屏幕边。
        // 顺序反过来之后，日志变长只会往下顶自己，不会再把舞台盖掉。
        // 按钮行是列里最后一块、日志块在它之后：日志块**先**挂进去不行——
        // VBox 的 `ShrinkBegin` 会把富余高度兜在最后一个子节点身上，日志块要是不在最后，
        // 富余就摊到按钮行上，按钮行被拉高，日志反而收不到高度。
        // 日志块在最后，但**不接 VBox 的竖向余量**：它的高度只有一个答案——
        // 「文字真的占多高 + 内边距」，由 `SyncLogHeight` 每次按实测写 `CustomMinimumSize`。
        // 上一版这里写的是 `ExpandFill`，本意是「把行动条底部那片空白吃掉」，
        // 结果面板被拉高 6px：`Size` 比 `CustomMinimumSize` 大，屏上是一块**画出来的
        // 空槽**（判据当场抓到）。留白该由「这一列打算多高」解决，不是让某一块虚胖。
        // 行动条的配额本来就按「日志满高 + 其余几行」算好了，所以它不需要谁来吃余量。
        _logPanel.SizeFlagsVertical = SizeFlags.ShrinkBegin;
        if (!_logPanel.IsInsideTree()) _actionCol.AddChild(_logPanel);

        _actions.AddChild(UiTheme.Btn("攻击 (A)", () => Act(() => _battle.PlayerAttack()), 46));
        _actions.AddChild(UiTheme.Btn("防御 (D)", () => Act(() => { Sfx.Play("block"); _battle.PlayerDefend(); }), 46));

        foreach (var skill in _run.Player.Skills)
        {
            var id = skill.Id;
            var btn = Widgets.SkillButton(skill, _run.Player, () => Act(() => _battle.PlayerSkill(id)));
            // 技能按钮上补一句「这一手大约打掉多少」：敌人卡已经把「它这一刀打你多少」
            // 写得很清楚，而**你能打掉它多少**一直只有它自己知道。玩家于是只能拿
            // 「下一刀 12~14」去反推「我还剩几刀」，而那两件事不是同一条算式。
            var (dlo, dhi) = _battle.PlayerHitForecast(id);
            if (dhi > 0 && Skills.IsSkillReady(_run.Player, id))
                btn.TooltipText = $"{skill.Desc}\n预计造成 {dlo}~{dhi} 点伤害";
            // 混乱思绪：施法有概率被吞掉。这条代价必须写在按钮上——
            // 玩家在战斗里没空翻背包看自己戴着什么，冷却照扣的失败会像是「游戏吞了我的操作」。
            //
            // 只写在真的会被吞的那几个技能上：摆架势（强化 / 石肤 / 闪避）不算施法，
            // 而以前提示一律挂上去，玩家会以为「按强化也可能白给」——
            // 于是这条代价被读成了「整条技能栏都有 25% 概率作废」。
            if (_run.Affixes.SpellFailChance > 0 && Skills.CanFizzle(skill))
                btn.TooltipText = $"{skill.Desc}\n混乱思绪：施放时 {(int)Math.Round(_run.Affixes.SpellFailChance * 100)}% 概率失败（冷却照扣）";
            _actions.AddChild(btn);
        }

        // 消耗品：战斗中能吃的只有回血/解毒/伤害三类。
        // 按钮与 Z 键走同一个入口（UseConsumableAt）：键位表写着「Z 使用消耗品」，
        // 而上一版只有鼠标能点——照着说明按 Z 什么都不会发生。
        var consumables = Inventory.Consumables(_run.Player, _run.ItemIndex);
        for (var i = 0; i < consumables.Count; i++)
        {
            var idx = consumables[i].Index;
            var d = consumables[i].Def;
            _actions.AddChild(UiTheme.Btn($"{d.Name} (Z)", () => UseConsumableAt(idx), 46));
        }

        _actions.AddChild(UiTheme.Btn("逃跑 (Esc)", Escape, 46));
    }

    /// <summary>
    /// 战斗中用掉一件消耗品。Z 键取第一件可用的，按钮点哪件用哪件。
    /// 走 Act 是为了保住「一次行动 = 一个回合」这条规则：吃药也算回合。
    /// </summary>
    private void UseConsumableAt(int index)
    {
        if (index < 0 || index >= _run.Player.Inventory.Count) return;
        if (!_run.ItemIndex.TryGetValue(_run.Player.Inventory[index], out var def)) return;
        Act(() =>
        {
            if (def.Cleanse == true) _battle.PlayerCleanse();
            else if (def.Heal is { } heal) _battle.PlayerHeal(heal, def.Name);
            else if (def.Damage is { } dmg) _battle.PlayerItemDamage(dmg, def.Name);
            else return;
            Inventory.RemoveItem(_run.Player, index);
            Refresh();
            RenderActions();
        });
    }

    private void Act(Action action)
    {
        if (_busy || _battle.Phase != BattlePhase.Player) return;
        _busy = true;
        action();
        Refresh();
        RenderActions();
        if (_battle.Phase is BattlePhase.Won or BattlePhase.Lost)
        {
            Settle();
            return;
        }
        _busy = false;
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--diag") >= 0)
        {
            CallDeferred(nameof(DumpLayout));
            GetTree().CreateTimer(0.5).Timeout += DumpLayout;
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (_busy || _battle.Phase != BattlePhase.Player) return;
        // M / F11 在战斗里同样有效（说明面板是把「地图」「战斗」两份表一起列出来的，
        // 玩家在战斗中按 M 没反应就会以为静音坏了）
        if (key.Keycode == Key.M) { Sfx.Toggle(); return; }
        if (key.Keycode == Key.F11)
        {
            var mode = DisplayServer.WindowGetMode();
            DisplayServer.WindowSetMode(mode == DisplayServer.WindowMode.Fullscreen
                ? DisplayServer.WindowMode.Windowed
                : DisplayServer.WindowMode.Fullscreen);
            return;
        }
        if (key.Keycode == Key.H) { ToggleHelp(); return; }
        // V：战斗特效开关（TS 版有，键位表里也写了）。关掉只影响粒子与飘字，结算一条不变
        if (key.Keycode == Key.V) { ToggleFx(); return; }
        if (key.Keycode == Key.A) Act(() => _battle.PlayerAttack());
        else if (key.Keycode == Key.D) Act(() => _battle.PlayerDefend());
        else if (key.Keycode == Key.Escape) Escape();
        else if (key.Keycode == Key.Z)
        {
            var first = Inventory.Consumables(_run.Player, _run.ItemIndex);
            if (first.Count > 0) UseConsumableAt(first[0].Index);
        }
        else if (key.Keycode >= Key.Key1 && key.Keycode <= Key.Key9)
        {
            var idx = (int)(key.Keycode - Key.Key1);
            if (idx < _run.Player.Skills.Count)
            {
                var id = _run.Player.Skills[idx].Id;
                Act(() => _battle.PlayerSkill(id));
            }
        }
    }

    /// <summary>
    /// 战斗中的键位说明。内容直接读 UI.Keymap.Battle，不在这里另抄一份——
    /// 抄一份的下场就是「说明写了、实现没接」，TS 版在这个坑里躺过一次。
    /// </summary>
    private void ToggleHelp()
    {
        if (_help is not null && GodotObject.IsInstanceValid(_help))
        {
            _help.QueueFree();
            _help = null;
            return;
        }
        var (root, body) = Widgets.Overlay(this, "战斗键位", 520);
        _help = root;
        foreach (var b in Keymap.Battle)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 12);
            var k = UiTheme.Text(b.Keys, 13, Palette.AccentSoft);
            k.CustomMinimumSize = new Vector2(120, 0);
            row.AddChild(k);
            row.AddChild(UiTheme.Text(b.Action, 13, Palette.Text));
            body.AddChild(row);
        }
        body.AddChild(UiTheme.Btn("知道了 (H)", () =>
        {
            root.QueueFree();
            _help = null;
        }, 40));
    }

    private Control? _help;

    /// <summary>
    /// 战斗特效开关：粒子、飘字、屏震一起关。
    /// 关掉是为了「打久了眼睛累」和「低配机」两种人——不开的人应该仍然能算清伤害，
    /// 所以关的只是表现，日志与数值一条不动。
    /// </summary>
    private void ToggleFx()
    {
        var on = GetNode<GameStore>("/root/GameStore").ToggleFx();
        ApplyFxEnabled();
        // 反馈本身不参与开关：玩家按了键，总得知道按到了没有。
        // 走 _battle.Log 而不是 BattleEvents.EmitLog：后者只是给界面听的事件，
        // 战斗日志是玩家真的能读到的那一份。
        _battle.Log.Add(on ? "战斗特效已开启" : "战斗特效已关闭（数值与日志照常结算）");
        Refresh();
        Sfx.Play(on ? "gain" : "click");
    }

    /// <summary>把「特效开不开」落到节点上：粒子层与飘字层整体隐藏，屏震归零</summary>
    private void ApplyFxEnabled()
    {
        _fxEnabled = GetNode<GameStore>("/root/GameStore").FxEnabled;
        if (_particles is not null && GodotObject.IsInstanceValid(_particles)) _particles.Visible = _fxEnabled;
        if (_damageNumbers is not null && GodotObject.IsInstanceValid(_damageNumbers)) _damageNumbers.Visible = _fxEnabled;
        if (!_fxEnabled)
        {
            _shakeTime = 0f;
            _stage.Position = Vector2.Zero;
        }
    }

    private bool _fxEnabled = true;

    private void Escape()
    {
        if (_busy || _battle.Phase != BattlePhase.Player) return;
        _busy = true;
        // 逃跑不标记房间已清理：房间一旦被标成 cleared 就再也不会触发战斗与奖励。
        Sfx.Play("deny");
        var dead = _battle.Escape(5);
        if (dead)
        {
            // 逃跑代价把玩家放倒了：这已经是一次失败，走同一条结算入口
            _run.SettleBattle(BattleOutcome.Lost, _enc);
            _battle.Dispose();
            Finish(BattleOutcome.Lost);
            return;
        }
        _battle.Dispose();
        Finish(BattleOutcome.Escaped);
    }

    private void Settle()
    {
        var victory = _battle.Phase == BattlePhase.Won;
        Sfx.Play(victory ? "victory" : "defeat");
        // 结算只有一个入口：打赢该给的东西、打输该记的东西，都在 Game.SettleBattle 里对表。
        // 以前这里写 if (victory) OnBattleWon else OnBattleLost，而「哪一类房打赢要给什么」
        // 这条判据在模拟器与探针里又各写了一遍——三份都对，错的是有人漏调。
        _run.SettleBattle(victory ? BattleOutcome.Won : BattleOutcome.Lost, _enc);
        _battle.Dispose();
        // 收尾演出留半秒：让玩家看清最后一下，而不是一黑就换界面
        var timer = new Timer { WaitTime = victory ? 0.7 : 0.9, OneShot = true };
        AddChild(timer);
        timer.Timeout += () => Finish(victory ? BattleOutcome.Won : BattleOutcome.Lost);
        timer.Start();
    }

    private void Finish(BattleOutcome outcome)
    {
        Finished?.Invoke(outcome);
        QueueFree();
    }

    private void Refresh()
    {
        var target = _battle.CurrentTarget();
        var gauntlet = _enc.Gauntlet is { } g ? $"连战 {g.Index + 1}/{g.Total} · " : "";
        // 集火目标也要说清楚：玩家点了「打乙」之后，如果标题还只写主敌人的名字，
        // 他会以为自己的点击没生效（TS 版同样把目标写进来）。
        var focus = target != _battle.Enemy ? $"（集火：{target.Name}）" : "";
        _enemyName.Text = gauntlet + (_battle.IsGroup
            ? $"{_enc.Def.Name} 等 {_battle.Enemies().Count} 只{focus}"
            : _enc.Def.Name + focus);
        // 血条的上限取「这一只进场时的满血」，不取 `BaseMaxHp`。
        // 精英房会把实例的血量按层数放大（`EliteScale`），而模板上的 `BaseMaxHp`
        // 还是原值——第 6 层以后血条会一直卡在满格，玩家看不到自己打了多少。
        // 摆现场（把血压到 1 点）时同一个数也要跟着走，否则「马上就要倒」
        // 在数字上成立、在血条上仍然是满的。
        // `MaxValue` 是「进场的满血」：`Math.Max(Hp, BaseMaxHp)` 给的是同一件事，
        // 只是把「还没扣过血」这一档也覆盖了。
        // 唯一的例外是血真的空了——那时 `Math.Max(0, 100) = 100` 会让条子算成 0%，
        // 这是对的；写死一个更大的下限反而会把「空血」画成满格。
        _enemyHp.MaxValue = Math.Max(1, Math.Max(target.Hp, target.BaseMaxHp));
        // `Value` 必须在 `MaxValue` 之前夹一次：`ProgressBar` 夹的是「赋值那一刻」的上限，
        // 而摆现场时上限是从 1 提到 100 的——先写 Value=0 再写 MaxValue=100 没问题，
        // 反过来（先设上限、再写一个比旧上限大的值）就会在旧上限上被夹掉。
        _enemyHp.Value = Math.Clamp(target.Hp, _enemyHp.MinValue, _enemyHp.MaxValue);
        _enemyHpText.Text = $"{target.Name}  {target.Hp}/{target.BaseMaxHp}  ·  攻击 {target.BaseAttack}  防御 {target.BaseDefense}";

        // 组合战斗里「下一刀」来自哪一只要写清楚：预告按出手的那只算（不是集火的那只），
        // 不写名字的话玩家会拿它去对集火目标的血条，然后觉得游戏在乱报数。
        var (low, high, notes) = _battle.EnemyForecast();
        var noteText = notes.Count > 0 ? $"（{string.Join("，", notes)}）" : "";
        var from = _battle.IsGroup && _battle.UpcomingAttackerName is { } who ? $"{who} 的下一刀" : "下一刀";
        // 这一行报的是**这一回合一共会掉多少**，不是「下一刀多少」。
        // 三条都要算进来，缺一条就有一类死法看不见：
        //   · 下一刀（敌人先手）；
        //   · 回合收尾的毒（且按结算口径：额度用完时只扣到剩 1 点）；
        //   · 倒下自爆（把最后一只打死的那一刻炸回来的那一笔）。
        // 横扫 100 局里「倒下自爆」是第一死因（7/28），而在这一行出现之前，
        // 玩家把怪打死的那一刻完全不知道它还会炸——手册里写了，战斗里没有。
        var poison = _battle.PoisonTickThisTurn();
        var poisonRaw = _battle.LethalThisTurnExtra();
        // 毒还剩几回合要说：只报「合计 12」的时候玩家会以为吃完这一下就过去了，
        // 而毒在下一回合还会再扣一次——真正会要命的是「毒 + 下一刀」，缺的从来是「还有多久」。
        var poisonTurns = _battle.PlayerPoisonTurnsLeft;
        var poisonText = poisonTurns > 1 ? $"{poisonRaw}×{poisonTurns} 回合" : $"{poisonRaw}";
        var blast = _battle.BlastThisTurn();
        var (totalLow, totalHigh) = _battle.ThisTurnTotal();
        var parts = new List<string>();
        if (poison > 0) parts.Add($"含中毒 {poisonText}");
        if (blast > 0) parts.Add($"含自爆 {blast}");
        var partsText = parts.Count > 0 ? $"｜{string.Join("，", parts)}" : "";
        // 报出来还不够：玩家读这一行是为了决定「这一刀敢不敢打下去」，
        // 所以自爆那一笔后面得跟一句能直接照做的话。
        var surge = blast > 0 ? "｜别在你面前倒下" : "";
        // 「你还要几刀」接在合计后面：这一行的用途是让玩家算出「这一场还有多久」，
        // 而「它这一刀打你多少」只是那笔账的一半。缺了另一半，玩家只能靠猜。
        var youHit = YouHitText();
        _enemyForecast.Text = _battle.UpcomingEnemyActs
            ? $"{from}预计 {low}~{high} 点{noteText}{partsText}，本回合合计 {totalLow}~{totalHigh} 点{surge}{youHit}"
            // 这只不动，但毒与自爆照算：只写「这回合不动」的话，玩家以为这一回合完全安全。
            : parts.Count > 0
                ? $"{_battle.UpcomingAttackerName} 被震住了，这回合不动｜仍会掉 {totalLow}~{totalHigh} 点（{string.Join("，", parts).TrimStart('｜')}）{youHit}"
                : $"{_battle.UpcomingAttackerName} 被震住了，这回合不动{youHit}";

        _enemySkills.Text = SkillForecastText(target);
        _playerBuffs.Text = PlayerBuffText();

        var maxHp = _run.Stats.MaxHp;
        _playerHp.MaxValue = Math.Max(1, maxHp);
        _playerHp.Value = _run.Player.Hp;
        _playerHpText.Text = $"{_run.Player.Hp}/{maxHp}";

        // 日志只显示最后 7 行：战斗里玩家没空读长文本
        RenderLog();
        RenderEnemyGroup();

        // 状态图标从单位身上读：不给界面另开一份「状态副本」，
        // 副本迟早和真实状态不一致，那时玩家看到的和结算的就是两回事。
        _enemyStatus?.Refresh(StatusIcons.Read(target));
        _playerStatus?.Refresh(StatusIcons.Read(_run.Player));
    }

    /// <summary>
    /// 「它还要吃几刀」——和卡片上半行那个「你还要挨多少」配成一对。
    ///
    /// 这只是**读**：`Battle.SwingsToKill` 走的是普攻那条链的下限，
    /// 不掷骰、不改任何状态。刀数在一刀之内就收起尾巴（`这一刀能收`），
    /// 因为玩家真正要问的就是那一句：「我这回合按下去，它会不会倒」。
    /// </summary>
    private string YouHitText()
    {
        if (_battle.Phase is BattlePhase.Won or BattlePhase.Lost) return "";
        var swings = _battle.SwingsToKill();
        if (swings <= 0) return "";
        var (low, high) = _battle.PlayerHitForecast();
        return swings == 1
            ? $"｜你这一刀 {low}~{high}，能收掉它"
            : $"｜你还要 {swings} 刀（每刀 {low}~{high}）";
    }

    /// <summary>
    /// 敌人的技能预告：名字 + 剩余冷却，冷却中压暗。对 TS 版 chip 行的文本化。
    /// 规则是「会什么」，不是「这次一定会放什么」——写死了反而骗人。
    /// </summary>
    private string SkillForecastText(Combatant enemy)
    {
        if (enemy.Skills.Count == 0) return "";
        var parts = new List<string>();
        foreach (var skill in enemy.Skills)
        {
            if (Skills.IsSkillReady(enemy, skill.Id)) parts.Add(skill.Name);
            else parts.Add($"{skill.Name}（还要 {Skills.SkillCooldownLeft(enemy, skill.Id)} 回合）");
        }
        var hint = enemy.Skills.All(s => !Skills.IsSkillReady(enemy, s.Id)) ? "（冷却中）" : "";
        return $"可能出招：{string.Join(" / ", parts)}{hint}";
    }

    /// <summary>玩家这一侧正在生效的临时状态，按「先防御、后强化、再中毒」的固定顺序</summary>
    private string PlayerBuffText()
    {
        var p = _run.Player;
        var parts = new List<string>();
        if (p.Defending) parts.Add("防御中 伤害减半");
        if (p.AttackMultiplier > 1) parts.Add($"攻击 +{(int)Math.Round((p.AttackMultiplier - 1) * 100)}%");
        if (p.PoisonTurns > 0) parts.Add($"中毒 {p.PoisonTurns} 回合");
        // 敌人中毒也要写在玩家这一行：TS 版就是这么排的（refreshBuffRow 同时报双方），
        // 因为毒雾这类技能的收益只有玩家自己知道——不写出来，玩家就分不清
        // 「我放了毒」和「我什么都没做」。
        var target = _battle.CurrentTarget();
        if (target.PoisonTurns > 0) parts.Add($"敌人中毒 {target.PoisonTurns} 回合");
        if (p.AtkModTurns > 0 && p.AtkMod < 0) parts.Add($"攻击下降 {p.AtkModTurns} 回合");
        if (p.DefModTurns > 0 && p.DefMod < 0) parts.Add($"防御下降 {p.DefModTurns} 回合");
        // 本层的临时加成（训练房苦练 / 疫病层 +4）也写在这里：
        // 地图 HUD 上看得到，进战斗反而消失，玩家会以为加成没了。
        if (_run.TrainingAttackBuff > 0) parts.Add($"苦练 +{(int)Math.Round(_run.TrainingAttackBuff * 100)}%");
        if (_run.FloorAttackBonus > 0) parts.Add($"本层攻击 +{_run.FloorAttackBonus}");
        // 诅咒装的两条常驻代价：每回合掉血、施法可能失败。
        // 它们不进状态图标（不是回合状态），但玩家必须能在战斗界面看到自己付了什么。
        if (_run.Affixes.TurnStartHpLoss > 0) parts.Add($"每回合 -{_run.Affixes.TurnStartHpLoss} 生命");
        if (_run.Affixes.SpellFailChance > 0) parts.Add($"施法失败 {(int)Math.Round(_run.Affixes.SpellFailChance * 100)}%");
        return string.Join("　", parts);
    }

    private void RenderEnemyGroup()
    {
        foreach (var child in _enemyGroup.GetChildren())
        {
            _enemyGroup.RemoveChild(child);
            child.QueueFree();
        }
        if (!_battle.IsGroup) return;
        var living = _battle.LivingEnemies();
        for (var i = 0; i < living.Count; i++)
        {
            var idx = i;
            var e = living[i];
            var btn = new Button
            {
                Text = $"{e.Name}  {e.Hp}/{e.BaseMaxHp}",
                CustomMinimumSize = new Vector2(180, 34),
                Disabled = _battle.CurrentTarget() == e,
                // 悬停说明：功能性敌人的规则（自爆、加护、反伤、附霜）必须能在
                // 点它之前读到。TS 版是一张自绘浮卡，Godot 用 TooltipText 表达同一份数据。
                TooltipText = EnemyTip(e),
            };
            btn.Pressed += () =>
            {
                _battle.SelectTarget(idx);
                Refresh();
            };
            _enemyGroup.AddChild(btn);
        }
    }

    /// <summary>
    /// 一只敌人的悬停说明：攻防血 + 它身上的特殊规则。
    /// 这些字段就写在 game_data 里，界面以前一个都没露出来——
    /// 玩家只能靠「我打了它一下，血掉得怪怪的」去猜。
    /// </summary>
    private string EnemyTip(Combatant c)
    {
        var lines = new List<string>
        {
            $"{c.Name}  攻击 {c.BaseAttack}  防御 {c.BaseDefense}",
            $"生命 {Math.Max(0, c.Hp)} / {c.BaseMaxHp}",
        };
        if (_battle.Defs.TryGetValue(c, out var def))
        {
            if (def.GuardAura is { } aura) lines.Add($"每 {aura.Every} 回合给同伴防御 +{aura.Defense}");
            if (def.ExplodeOnDeath is { } boom) lines.Add($"倒下时自爆，伤害为攻击力的 {(int)Math.Round(boom * 100)}%");
            if (def.LowHpDrain is { } drain) lines.Add($"血量低于 {(int)Math.Round(drain.Ratio * 100)}% 时汲取回血 ×{drain.Multiplier}");
            if (def.SpellReflect is { } reflect) lines.Add($"你每次施法受到 {reflect} 点反伤");
            if (def.FrostSealOnHit == true) lines.Add("攻击附带冰封印记（降攻 25% / 3 回合）");
            if (!string.IsNullOrEmpty(def.Desc)) lines.Add(def.Desc!);
        }
        lines.Add("点一下集火它");
        return string.Join('\n', lines);
    }

    /// <summary>
    /// 日志这一屏能显示几行：按**实测行高**从「这块地有多高」反推，不再按设计常量截。
    ///
    /// 设计值 `LogLines`（7）是在「一行 13.7px」的假设下算出来的，而实测一行 20px——
    /// 于是七行的假设下其实放得下五行。以前两个数各按各的走：`RenderLog` 按 7 行截，
    /// 面板按 96 定高，中间那两行既被截又被裁。
    /// 行高先问渲染（`GetContentHeight / 行数`），问不到才退回设计值。
    /// </summary>
    private int VisibleLogLines()
    {
        var total = _battle.Log.Count;
        if (total <= 0) return 1;
        var rows = Math.Max(1, Math.Min(total, _layout.LogLines));
        var room = ActionColumnRoomFor(this, _layout, _actionCol, _logPanel);
        var rowHeight = LogRowHeight();
        if (rowHeight <= 1f) return rows;
        return Math.Max(1, Math.Min(rows, (int)MathF.Floor(room / rowHeight)));
    }

    /// <summary>
    /// 日志一行有多高（像素）。
    ///
    /// 从**上一次排版**的结果反推，而不是从设计常量算：
    /// `_layout.LogHeight / LogLines` ≈ 13.7px，而 13px 的中文一行实测 20px。
    /// 也不是每次现问 `GetContentHeight() / 行数`——那个数会跟着「这次排了几行」变，
    /// 而「这次排几行」正是要算的东西，自指的方程解不出来（实测会在 4 行与 5 行之间来回跳）。
    /// 所以取「上一次真的排过版的那份内容高 ÷ 那次排进去的行数」，拿不到才退回设计值。
    /// 它是一个观测量，不是一次计算。
    /// </summary>
    private float LogRowHeight()
    {
        var rows = _battle.Log.Count > 0 ? Math.Min(_battle.Log.Count, _layout.LogLines) : 1;
        var contentHeight = (float)_logRich.GetContentHeight();
        if (contentHeight > 1f && rows > 0)
        {
            var measured = contentHeight / rows;
            // 一行至少得有一个字号那么高（9pt 以下不可能），否则是还没排过版的噪声
            if (measured >= 8f) return measured;
        }
        return _layout.LogHeight / Math.Max(1, _layout.LogLines);
    }

    /// <summary>日志着色：分类规则在 BattleLogRules，这里只管把颜色拼成 BBCode</summary>
    private void RenderLog()
    {
        var lines = _battle.Log.Skip(Math.Max(0, _battle.Log.Count - VisibleLogLines())).ToList();
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var kind = BattleLogRules.Classify(line);
            var hex = BattleLogRules.ColorOf(kind).ToHtml(false)[..6];
            // 最后一行最亮、往上逐级变暗：玩家的眼睛自然落在「刚发生了什么」上
            var alpha = 0.55f + 0.45f * (float)(i + 1) / lines.Count;
            if (BattleLogRules.WantsBorder(kind)) sb.Append("[color=#1b2334]▌[/color]");
            sb.Append($"[color=#{hex}{(int)(alpha * 255):x2}]{line}[/color]");
            if (i < lines.Count - 1) sb.Append('\n');
        }
        _logRich.Text = sb.ToString();
        SyncLogHeight(lines.Count);
    }

    /// <summary>
    /// 日志面板跟着实际行数收放，多出来的高度还给舞台。
    /// 上限是 LogLines：日志再长也不该把按钮行顶下去——那是玩家最常按的东西。
    /// </summary>
    private void SyncLogHeightNow() => SyncLogHeight(_battle?.Log.Count ?? 0);

    private void SyncLogHeight(int lineCount)
    {
        if (_logPanel is null || !GodotObject.IsInstanceValid(_logPanel)) return;
        // 高度按 RichTextLabel 实测的内容高度定，不按「行数 × 假设行高」推算：
        // 字号、行距、CJK 行高都由主题决定，自己算出来的数迟早和渲染对不上
        // （上一版就是按 13px 估，实测渲染出来一行 19px，面板底部永远留一条空）。
        //
        // 但 `GetContentHeight()` 读的是**上一次排版**的结果：`Text` 刚写进去时它还报旧文本的高度
        // （实测五行日志写到四行之后仍报 20）。上一版就是拿着这个旧数把面板收在旧高度上——
        // 屏上「面板比别人高出一截，多出来的那块是纯黑的空白」，摆了三十多轮。
        // 两件事一起做：① 内容变了就立刻排一次版（`NotificationReady` 是
        // `RichTextLabel` 自己那条「重新算内容高」的入口，不用等下一帧 process）；
        // ② 要的是「面板装得下文字」，所以取实测与按行数算的两个值里**大的**那个——
        //    宁可多留一行，也不许裁掉玩家刚打出来的那一行。
        var textHeight = (float)_logRich.GetContentHeight();
        // 文本还没排过版（内容高 ≤ 1）时按行数估一个；行数也没有就按一行算。
        if (textHeight <= 1f) textHeight = _layout.LogHeightFor(Math.Max(1, lineCount));
        textHeight = Math.Max(textHeight, _layout.LogHeightFor(Math.Max(1, lineCount)));
        // 现场上限 `room` 是「这一列里除日志块之外还剩多少」。日志块排在按钮行**之后**，
        // 它变长只往下顶自己，可再长就会顶出屏幕底边——那时整块行动条会被 VBox 压扁
        // （Godot 先收富余再排），按钮行跟着变矮，比「日志少两行」坏得多。
        // 所以「日志该多高」必须问现场，而不是查一个设计常量。
        var room = ActionColumnRoomFor(this, _layout, _actionCol, _logPanel);
        // 上限只有**一个**：这一列里还剩多少（`room`）。
        //
        // 以前这里还有一层 `_layout.LogHeight`（96）——那是「设计上日志区多高」。
        // 它和实测行高对不上：96 / 7 行 ≈ 13.7px，而 13px 的中文字排出来一行是 20px。
        // 于是五行日志要 100px、被 96 裁掉，屏上最后一行只剩上半截（`03-battle.png` 里
        // 那句「巨鼠攻击你，造成 3 点伤害」被切在字腰上）。
        // 「最多显示几行」这件事本来就该由「这块地能放几行」决定，而地有多少是 `room` 说的。
        // `LogHeight` 留在布局里当配额（行动条高度由它算出来），但不再是文本的上限。
        var wantText = Mathf.Min(textHeight, room);
        var want = wantText + UI.BattleLayout.LogPanelPadding;
        _logLines = lineCount;
        if (Mathf.Abs(_logPanel.CustomMinimumSize.Y - want) > 0.5f)
        {
            // RichTextLabel 的最小高由它自己按「视口宽 ÷ 行长」报，与这里的 wantText 无关：
            // 它报了 96，面板就再也收不到 46（面板的最小高 = max(自己的, 子的)）。
            _logRich.CustomMinimumSize = new Vector2(0, wantText);
            _logPanel.CustomMinimumSize = new Vector2(0, want);
            // 两步都要：`CustomMinimumSize` 改了不等于它认了——
            // `PanelContainer` 把自己算出来的最小高缓存着，`ResetSize()` 只让它重新排一次，
            // 缓存不冲掉面板就停在旧高度上。屏上量得出来：面板画到 580、文字到 575，
            // 正好差一整行（19px）的纯黑空槽。
            //
            // `UpdateMinimumSize()` 只该往上叫（子节点变了，父级重算一次最小尺寸），
            // **不能在这里顺手把面板自己那一层的缓存也冲掉**：`PanelContainer` 会按
            // 「此刻的 content 宽」把最小宽记下来，而此刻 content 宽正是那次 23px 排版的结果。
            // 一旦记成 23，之后 VBox 就有了一根「最小宽 23」的柱子，`ExpandFill` 也只
            // 把它撑到 23——日志块于是在「窄条」与「整条」之间来回跳，跳完停在窄条上。
            // 面板自己的宽从来不是由内容决定的（它是「行动条的内容宽」），所以这里不碰它。
            _logPanel.ResetSize();
            _logRich.UpdateMinimumSize();
        }
        // 宽度跟着父级：见 `LogPanel` 那段注释——它是「行动条的内容宽」，
        // 不是「内容要多宽」，所以不交给 VBox 去分配份额。
        SyncLogWidth();
        // 日志收下去的高度立刻还给舞台：立绘按它取尺寸，站位按它算
        PlaceSprites();
    }

    /// <summary>
    /// 日志块的宽度 = 这一列的内容宽。
    ///
    /// 为什么不用布局标志位：这一列里 `Actions`（按钮行）也在要横向余量，
    /// 两个 `ExpandFill` 只能分那点差额，而差额按最小宽加权——日志块的最小宽
    /// 只有一格字（1px + 内边距 = 23），于是它永远分到 0。
    /// 宽度在这里是一个常量式的意图（等于父宽），写成赋值比写成一个份额可靠。
    /// </summary>
    private void SyncLogWidth()
    {
        if (_logPanel is null || !GodotObject.IsInstanceValid(_logPanel)) return;
        var col = _actionCol;
        if (col is null || !GodotObject.IsInstanceValid(col) || col.Size.X <= 1f) return;
        if (Mathf.Abs(_logPanel.CustomMinimumSize.X - col.Size.X) > 0.5f)
            _logPanel.CustomMinimumSize = new Vector2(col.Size.X, _logPanel.CustomMinimumSize.Y);
    }

    /// <summary>
    /// 冒烟用：把日志面板按「此刻真的排过版的内容高」再定一次高，直到定住为止。
    ///
    /// 一次不够，因为这里量的是**布局的结果**，而改高度本身就是一次布局：
    /// 面板报 38 → 列跟着长 8px（缝多了一条）→ 现场上限 `room` 变了 → 下一次又报 46。
    /// 只跑一遍的话，面板停在「上一轮那个高度」上，比文字矮一截——屏幕上读作
    /// 「最后一行被裁掉了」，而控件自己的账（`CustomMinimumSize`）是对的。
    /// 循环等两帧再问，是让容器真的排完：同一帧里问，容器报的还是旧尺寸。
    /// </summary>
    public async Task SyncLogUntilStableForTest(int rounds = 4)
    {
        for (var i = 0; i < rounds; i++)
        {
            SyncLogHeightNow();
            if (!IsInsideTree()) return;
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        SyncLogHeightNow();
    }

    /// <summary>
    /// 行动条这一列里，日志块（文字部分，不含内边距）最多能占多高。
    ///
    /// 不问「配额还剩下多少」，而是问容器自己：这个场景身上那块行动条实际有多高，
    /// 减去这一列里**别的**子节点占掉的高度（含它们之间的缝）。
    /// 第一版按配额算（`ActionBarHeight - chrome`），拿配额减内容，等于把同一个数
    /// 从两头各减一遍——结果是「眼睛给 46、名字给 44、血条行给 40」，日志只拿到 46px，
    /// 五行字里有两行被裁掉。而那一版跑起来一切正常，因为富余的高度由 VBox 悄悄垫了回来。
    /// **布局是最终决定权，所以判据和实现都得问它要，不能各自按公式再算一遍。**
    /// </summary>
    private static float ActionColumnRoomFor(Control root, UI.BattleLayout.Layout layout,
        VBoxContainer? col, Control? logPanel)
    {
        // 一、没有容器（还没建出来）就用配额兜底
        if (col is null || !GodotObject.IsInstanceValid(col) || col.Size.Y <= 1f)
            return Mathf.Max(20f, layout.LogHeight);
        // 二、这一列里除了日志块之外，别的东西**至少要占**多少，以及它们之间有几条缝。
        //
        // 这里量的是各自的最小高，不是各自当前的 `Size`——这是这段代码唯一容易算错的地方：
        // 日志块的高度由 `room` 决定，而列会把富余（被日志块顶出来的那一截）兜在最后一个
        // 子节点身上，那个子节点的 `Size` 于是比自己的最小高大出整整一截。按 `Size` 加起来，
        // `room` 每算一次就多 8px，反复调永远定不住：面板 40 → 48 → 56 → 64 → 80……
        // 屏上是一条越吃越深、把按钮行顶出屏幕的日志，而每一轮的账都对得上。
        // 用最小高，这个方程就没有自指了：列高的变化不改变「别的东西要多少」。
        var used = 0f;
        var count = 0;
        foreach (var child in col.GetChildren())
        {
            if (child == logPanel || child is not Control c) continue;
            used += c.GetCombinedMinimumSize().Y;
            count++;
        }
        // 缝：除了日志块，这一列里有 `count` 块东西，它们之间 `count - 1` 条缝
        // （日志块自己上下的那两条不算）。
        var gaps = UI.BattleLayout.ActionBarSeparation * Mathf.Max(0, count - 1);
        var room = col.Size.Y - used - gaps - UI.BattleLayout.LogPanelPadding;
        // 这里**只按现场**封顶，不再夹一层 `layout.LogHeight`。
        //
        // 上一版是 `Min(LogHeight, room)`——`LogHeight`（96）是「设计上日志区多高」，
        // 而它是按「一行 13.7px、七行」算出来的。实测一行 20px，于是这块地其实放得下五行、
        // 却只给了 96（四行多），屏上最后一行被切在字腰上。
        // 「还剩多少」只有一个答案：这一列的高度减去别的东西占掉的。设计值不是现场值。
        return Mathf.Max(20f, room);
    }

    /// <summary>
    /// 舞台的实际高度：布局给的高度 + 日志面板让出来的那部分。
    /// 以前舞台高度是写死的，日志省下来的空间没有人接手——立绘照旧那么小，中间照旧空着。
    /// </summary>
    private float LiveStageHeight =>
        _layout.StageHeight + Mathf.Max(0f, _layout.LogHeight - _layout.LogHeightFor(_logLines));

    /// <summary>
    /// 舞台这一段真正的高度：上沿是敌人卡底边，下沿是**舞台上最后画到的那一行**
    /// （`Arena.FloorLine + FootDrop`，也就是落点光斑中心），而不是 `StageBottom`。
    ///
    /// 这两个数差着一截：StageBottom 是行动条上沿（452），光斑中心在 514。
    /// 立绘的盒子是按上下沿之差算的（283 高），却从光斑中心往上摆——
    /// 于是它的底边落到 514，比行动条上沿还低 44px，整个人被日志面板吃掉一截。
    /// 截图里读起来像「立绘自己躲下去了」，其实是它的盒子长过了自己站的那块地。
    ///
    /// 用 Arena 交出来的那两条线，不在界面里再按公式算一遍。
    /// </summary>
    private float StageFloorLine =>
        _arena is not null && GodotObject.IsInstanceValid(_arena) && _arena.Size.Y > 1f
            ? _arena.Position.Y + _arena.FloorLine + _arena.FootDrop
            : _layout.StageBottom + Arena.StanceDrop;

    /// <summary>技能特效：配方决定发多少粒子，界面只决定从哪发到哪</summary>
    private void OnSkillFx(SkillFxEvent ev)
    {
        if (!_fxEnabled) return;
        if (_particles is null) return;
        var from = ev.From == "player" ? PlayerAnchor() : EnemyAnchor();
        var to = ev.To == "enemy" ? EnemyAnchor() : PlayerAnchor();
        var rng = new Random((int)(Time.GetTicksMsec() & 0x7fffffff));
        var list = FxLayer.Recipe(ev.SkillId, from, to, () => (float)rng.NextDouble());
        if (list.Count > 0) _particles.Emit(list);
        // 出手的那一方前冲一下：光有粒子在飞，立绘站着不动，看着像「技能自己飘过去了」。
        // 挨打那一侧的后仰在 OnDamage 里，两个动作方向相反，一来一回就有了交锋感。
        if (ev.From == "player") { _heroSprite?.Lunge(); _heroChip?.Lunge(); }
        else _enemySprite?.Lunge();
        Sfx.Play(ev.SkillId is "soul_drain" or "thirst" ? "hurt" : "gain");
        Shake(3f, 0.18f);
    }

    /// <summary>
    /// 舞台宽度：优先用自身的最终尺寸，还没布局时用可见视口——
    /// 立绘位置如果按 0 宽度算，会全部堆在左上角（截图抓到的就是这一条）。
    /// </summary>
    private float StageWidth => Size.X > 1f ? Size.X : GetViewport().GetVisibleRect().Size.X;

    /// <summary>
    /// 舞台的左右边界：敌人卡与行动条是**左右通栏**的（两边各留 12px），
    /// 舞台不能也通栏——那样敌人立绘的脚就在卡外那 12px 里，被卡沿切掉一半。
    ///
    /// 更值的是「两个站位」这件事：左右各内缩 12 之后，
    /// `EnemyStanceX(0.30) * StageLeft` 与 `HeroStanceX(0.70) * StageRight` 才是一对
    /// 关于画面中线对称的数。以前两边都按满宽算，两个站位各自被边缘吃掉了不对称的一截，
    /// 屏幕上是「敌人在 372、你在 902」，中间那条中线落在 576 而不是 640。
    /// </summary>
    private const float StageInset = 12f;
    private float StageLeft => StageInset;
    private float StageInnerWidth => MathF.Max(1f, StageWidth - StageInset * 2f);

    /// <summary>舞台横向的比例位置 → 屏幕坐标</summary>
    private float StageX(float ratio) => StageLeft + StageInnerWidth * ratio;

    /// <summary>敌人卡与屏幕左右两边各让出的距离（卡是一块浮起来的面板，不是通栏横幅）</summary>
    private const float EnemyPanelInsetX = 10f;

    /// <summary>敌人卡上沿与屏幕上沿的距离：让圆角后面露出一线底色</summary>
    private const float EnemyPanelInsetY = 6f;

    /// <summary>
    /// 立绘直径：由「槽位能放多高」决定，而不是由舞台宽度拍脑袋定。
    /// 槽位高 = 卡高 - 上下内边距，再留一点给浮动（±6/9px）与脚下投影。
    /// 上限 132 是 Boss 的尺寸；下限 56 保证矮窗口下还认得出造型。
    /// </summary>
    private float EnemySpriteSize()
    {
        // 图形的直径：槽高的一半偏多一点。MonsterSprite 的盒子是 size×1.4 × size×1.5，
        // 图形只占其中 1/1.5 那一份，剩下的留白是浮动、投影与光晕。
        // 盒子本身由 PlaceSprites 统一按槽高设定，这里只管图形多大。
        var socket = _layout.EnemyPanelHeight - BattleLayout.EnemyPanelPadding;
        return Math.Clamp(socket / 1.5f, 48f, 132f);
    }

    /// <summary>
    /// 立绘的脚该落在哪条线上（视口坐标）。**和舞台画的光斑是同一个数**。
    ///
    /// 光斑不在 `BattleLayout.StageBottom` 那条线上：它画在 `floorY + 62`。
    /// 以前立绘踩 StageBottom、光斑画在 StageBottom + 62——脚在光斑上方 62px，
    /// 屏幕上两块椭圆孤零零地飘在人脚前面（截图上一直看得见，摆了三十多轮没人当成 bug）。
    ///
    /// 但「踩到光斑中心」也是错的（这一版第一稿就是）：脚落进地砖缝里，
    /// 人像从地板下面长出来的。光斑是地面往观众延伸的那一块，脚踩在它的**远沿**，
    /// 投影落进它的后半段——那一截叫 `Arena.StanceLift(盒子高)`。
    /// 盒子越高（Boss），脚要抬得越多，影子才不会甩到光斑前面去。
    /// </summary>
    private float FootY(float boxH) => Arena.FootLineY(FootFloor(), FootDropOfStage(), boxH);

    /// <summary>舞台这一帧真的会用的地板线（视口坐标）</summary>
    private float FootFloor()
    {
        var laid = _arena is not null && GodotObject.IsInstanceValid(_arena) && _arena.Size.Y > 1f;
        return laid ? _arena!.FloorLine + _arena.Position.Y : _layout.StageBottom;
    }

    /// <summary>光斑中心相对地板线往下掉多少（舞台上会按控件高夹一次）</summary>
    private float FootDropOfStage()
    {
        var laid = _arena is not null && GodotObject.IsInstanceValid(_arena) && _arena.Size.Y > 1f;
        return laid ? _arena!.FootDrop : Arena.StanceDrop;
    }

    /// <summary>
    /// 把两个立绘摆回各自的槽位。
    /// 敌人卡与玩家状态条都是容器布局，这里只负责「在槽里居中」——
    /// 上一版是手算舞台坐标，窗口一变矮就溢到日志上。
    /// </summary>
    private void PlaceSprites()
    {
        // 舞台的地板线：立绘的脚与舞台画的落点光斑必须踩同一条线。
        // 不写进来时 Arena 自己按控件高度的一半切地板（720 下是 360），
        // 而立绘按 StageBottom（452）站——两边差 92px，光斑留在天花板那边。
        //
        // 给的是控件坐标：Arena 是铺满视口的一层，它的原点就是视口原点，
        // 所以这里要减掉它自己的 Position——减去不是凑数，是这个类唯一还
        // 「可能不铺满」的那一天不写死在别处。
        // 光斑与站位用同一把尺子（见 Arena.StanceX）：舞台左右各内缩 12px，
        // 和敌人卡、行动条的内容边界对齐。
        if (_arena is not null && GodotObject.IsInstanceValid(_arena))
        {
            _arena.FloorY = _layout.StageBottom - _arena.Position.Y;
            _arena.StageLeft = StageLeft - _arena.Position.X;
            _arena.StageRight = StageLeft + StageInnerWidth - _arena.Position.X;
        }

        if (_enemySprite is not null && GodotObject.IsInstanceValid(_enemySprite))
        {
            var size = EnemySpriteSize();
            _enemySprite.Setup(_enc.Def.Id, size, _enc.Tier == EnemyTier.Boss);
            // 关掉 Setup 的自动改尺寸：它会把框重新写成 size×1.4 × size×1.5，
            // 覆盖掉下面这套按本体反推的盒子——上一版屏上的怪总比预期小一圈就是这个。
            _enemySprite.SizeFromParent = true;
            // 体量按舞台高定：普通怪的盒子占舞台高 78%，Boss 占满。
            // 上一版是「设计直径 × 1.12/1.32」——那组数看着像比例，其实是盒子余量，
            // 结果 720 高下普通怪的盒子只有 234px，屏幕上就是一只比骑士还小的耗子。
            var ratio = _enc.Tier == EnemyTier.Boss ? 1.06f : 1.0f;
            // 舞台这一段有多高：从卡底到「舞台上最后画到的那一行」。
            // 立绘的盒子必须装进这段里——用 StageBottom 量的话，盒子会比自己的地多长一截
            // （光斑中心在 StageBottom 下面 62px），脚一站上去，身体下半截就掉进行动条，
            // 被日志面板盖住。截图里看着像「立绘躲到面板后面」，其实是盒子长过了地板。
            var stageRoom = MathF.Max(0f, StageFloorLine - _layout.StageTop);
            // 用 LiveStageHeight 而不是写死的舞台高：日志只剩一行时省下的高度
            // 要让怪变大，而不是留在原地当空白。
            // 上限还要受「地板线到卡底」这段实际空间约束——立绘的脚踩地板线、身体往上长，
            // 长得超过这段就会被顶到敌人卡上，顶边压过卡底边就是压字。
            // 「卡底到地板线」这段实际空间能装多高的盒子。
            // 注意这里不能再除 HeroArt.LayoutHeight：room 已经是像素，
            // 再除 65 得到的是「人高的倍数」，把它当上限去夹像素值，
            // 结果上限恒等于 6，`Clamp(v, 140, 6)` 永远返回 140——立绘被死死钉在下限上，
            // 上调任何比例都不生效。这正是「改了半天比例，屏幕上的怪一点没变大」的原因。
            var room = _layout.StageBottom - _layout.StageTop;
            var maxByStage = MathF.Max(140f, stageRoom * 0.94f);
            // 上面那两个系数（Boss 1.06 / 普通 1.0）是「盒子占舞台高的比例」：
            // 早先写 0.98/0.78，屏幕上一只巨鼠只有骑士的七成高，
            // 而玩家要读的是「它有多大」——怪物的体量在这个游戏里就是威胁等级。
            // 盒底停在光斑中心，造型的脚在它上面 0.17 盒高；而精灵是「按盒子高」画的，
            // 所以盒子要多留这一截——否则造型会被画小一圈，脚也伸出框外。
            var boxH = Math.Clamp(LiveStageHeight * ratio, 140f, MathF.Min(430f, MathF.Max(140f, maxByStage)));
            // 盒子按本体反推：本体 34 : 65（含投影），横向不再额外加宽——
            // 加宽会让怪横向摊开，看着比实际矮一档
            var box = new Vector2(boxH * 34f / HeroArt.LayoutHeight, boxH);
            _enemySprite.CustomMinimumSize = box;
            _enemySprite.Size = box;
            // 站位：舞台左侧，和右侧的玩家立绘成对角。
            // 站位与舞台画的落点共用同一个常数（Arena.EnemyStanceX）：
            // 界面自己拍一个 0.32、舞台画在 0.30 的话，脚就永远差半格踩在光斑外面。
            //
            // 纵向不再「盒底 = 光斑中心」——那样做的前提是「造型的脚正好在盒底」，
            // 而矮胖的造型（巨鼠 37×21.6）比例尺被横向卡住，竖着剩一大截：
            // 盒子底边对上了，画出来的脚却在盒底上方二十几像素，屏上就是一只浮着的耗子。
            // 现在反过来：先定**脚**该踩哪（光斑远沿），再由 `MonsterSprite.FootLocalY`
            // 反推盒子该摆在哪。脚往上长，盒子往上挪同样的距离，脚就落在同一条线上。
            // 抬多少按**造型**高算，不按盒子高算：盒子底下那 22%（BoxHRatio）是浮动与投影的余量，
            // 拿盒子高去算等于给每只怪多发一份不属于它的「脚高」。
            var footLine = FootFloor() + FootDropOfStage() -
                           Arena.StanceLift(box.Y / MonsterSprite.BoxHRatioConst);
            EnemyFootY = footLine;
            _enemySprite.Position = new Vector2(
                StageX(Arena.EnemyStanceX) - box.X * 0.5f,
                footLine - _enemySprite.FootLocalY);

        }
        // 行动条里的身份牌：高度按血条行高走，宽度按本体宽高比（34 : 58）。
        // 比例必须对——上一版用「0.9 倍高的窄盒子」，人就被横向压扁成蓝团子。
        if (_heroChip is not null && GodotObject.IsInstanceValid(_heroChip))
        {
            // 立绘高 = 行高的九成：再高就会把血条行撑开，玩家看到的是「血条前面挂了个挂件」。
            // 上限必须卡在血条行能承载的高度上：HpRowHeight(56) 减掉面板内边距，
            // 立绘再高就会把这一行撑开，多出来的高度由容器从下面的按钮行里扣——
            // 截图里那排「攻击(A) / 防御(D)」被切成半截，就是这么来的。
            // 所以这里不是「想画多大画多大」，而是「血条行装得下多大」。
            var chipH = Math.Clamp(_layout.HpRowBudget, 40f, _layout.HpRowBudget);
            var box = new Vector2(chipH * 38f / 65f, chipH);
            _heroChip.CustomMinimumSize = box;
            _heroChip.Size = box;
            // 比例尺按「整套设计坐标（含投影到 y=+22 的那一截）」算：65 = 头顶 -43 到投影 +22。
            // 只除 58 会让人的脚踩到框下面去。
            _heroChip.SetHeroScale(chipH / 65f);
        }
        // 舞台右侧的玩家：尺寸取敌人盒子的 0.82（HeroToEnemyBox），脚踩同一条线。
        // 比敌人稍小是刻意的——玩家读作「我 vs 它」，视觉重量该略偏向对手；
        // 但也不能小到像宠物，0.82 是两个人放进同一个光斑里还不挤的那一档。
        if (_heroSprite is not null && GodotObject.IsInstanceValid(_heroSprite))
        {
            // 上限受「舞台能装多高」约束：盒子不能比卡底到地板线这段还高，
            // 否则人的头会穿出敌人卡（冒烟里那条「不在舞台里」抓到的就是这个）。
            // 尺寸和敌人同一把尺子（见 HeroToEnemyBox 的说明）：玩家盒子高 = 敌人盒子高 × 0.82。
            // 上限也是：敌人已经按「舞台画到的那一行」收过了，这里再按 `StageBottom` 收一次
            // 就白收——那一条比光斑中心还高 62px，收出来的上限比真实的地多一截。
            var stageRoomHero = MathF.Max(0f, FootFloor() + FootDropOfStage() - _layout.StageTop);
            var enemyH = Math.Clamp(LiveStageHeight * 1.0f, 140f, MathF.Min(430f, MathF.Max(140f, stageRoomHero * 0.94f)));
            // 盒子底边停在「光斑中心」那条线：从那儿往上量得出上限 ——
            // `(光斑中心 - 卡底) / (1 - 0.17)`。
            //
            // 这一段地以前被三个不同量纲的数混着算：`_arena.FloorLine` 是控件坐标、
            // `_layout.StageTop` 是屏幕坐标、再减一个 `Size.Y` 当上限——上限比真实的地
            // 多出整整一张卡。现在统一走舞台上那一行，只减一次卡底。
            var shadowY = FootFloor() + FootDropOfStage() - _layout.StageTop;
            var h = Math.Clamp(enemyH * HeroToEnemyBox, 120f,
                MathF.Max(120f, shadowY / (1f - Arena.FootOnEllipse)));
            // 盒子比造型高 0.17 倍：脚站在光斑的远沿，盒子只装到那儿为止。
            // 那一截不留出来，脚会伸出框外、位置跟着往下滑。
            var box = new Vector2(h * 38f / HeroArt.LayoutHeight, h + Arena.StanceLift(h));
            _heroSprite.CustomMinimumSize = box;
            _heroSprite.Size = box;
            _heroSprite.SetHeroScale(h / HeroArt.LayoutHeight);
            HeroFootY = FootFloor() + FootDropOfStage() - Arena.StanceLift(h);
            // 盒子底边 = 脚 + 「光斑远沿」那一截（脚踩的是光斑中心那条线）。
            // 以前写的是 `HeroFootY - box.Y`——等于把那一截**又减了一遍**：
            // 盒子比它该在的位置高了 0.17 盒（48px），人的头从敌人卡上沿穿出去。
            // 还是第三十四轮那个坑：**盒子装到哪、脚站在哪、光斑画在哪是三件事**。
            _heroSprite.Position = new Vector2(
                StageX(Arena.HeroStanceX) - box.X * 0.5f,
                HeroFootY + Arena.StanceLift(h) - box.Y);

        }
        PlaceEnemyGroup();
        _placedFor = (LiveStageHeight, Size.Y);
    }

    /// <summary>
    /// 战斗里「你」和「它」是同一个人物、同一个动作。
    ///
    /// 上一版玩家取 `LiveStageHeight * 0.62`、敌人取 `* 1.0`，两个数互不相干：
    /// 720 高下敌人的盒子被下限顶到 283、玩家只到 176——屏幕上是只到膝盖的骑士
    /// 对着一只巨鼠，而敌人卡上写着「巨鼠 9/22」：数值说它是小怪，画面说它比你大得多。
    /// 现在先定敌人盒子（它才是「谁在打你」那一侧），玩家按同一个盒子缩 0.82。
    /// </summary>
    private const float HeroToEnemyBox = 0.82f;

    /// <summary>组合房间的小卡：竖排在舞台右上一列，避开立绘与按钮行</summary>
    private void PlaceEnemyGroup()
    {
        if (_enemyGroup is null || !GodotObject.IsInstanceValid(_enemyGroup)) return;
        var w = StageWidth;
        _enemyGroup.Position = new Vector2(w - 24f - 190f, _layout.StageBottom - 24f - Math.Max(1, _battle?.Enemies().Count ?? 1) * 40f);
        _enemyGroup.Position = new Vector2(_enemyGroup.Position.X, Math.Max(_layout.StageTop + 8f, _enemyGroup.Position.Y));
    }

    /// <summary>
    /// 飘字与特效的落点：敌人站在舞台左侧，玩家站在舞台右侧（对手位）。
    ///
    /// 玩家没有立绘站在舞台上，但「他打过来的伤害飘在哪」必须有一个位置——
    /// 那就是他在舞台上的**站位**，不是血条。所以这里用舞台几何算，
    /// 不再跟着立绘控件走（立绘进了行动条之后，跟着算就会飘到屏幕下沿）。
    /// </summary>
    private Vector2 PlayerAnchor() =>
        _heroSprite is not null && GodotObject.IsInstanceValid(_heroSprite)
            ? _heroSprite.Position + new Vector2(_heroSprite.Size.X * 0.5f, _heroSprite.Size.Y * 0.36f)
            : new(StageX(Arena.HeroStanceX), FootY(_layout.StageBottom - _layout.StageTop) - Mathf.Max(40f, LiveStageHeight * 0.34f));


    /// <summary>
    /// 「打在敌人身上」的那一下从哪冒出来。
    ///
    /// 以前取的是**敌人卡里那个槽**的中心（`StageTop + 卡高×0.52`）：立绘站在舞台上、
    /// 卡里的槽只是「指向舞台那一只」的取景框（见 Paint.EnemySocket），于是伤害数字
    /// 从屏幕上方的空框里飘出来，而被打中的那只在旁边一动。让位给槽之后这一处更要紧：
    /// 现在取立绘自己的胸口，没立绘时退回舞台上那个站位。
    /// </summary>
    private Vector2 EnemyAnchor() =>
        _enemySprite is not null && GodotObject.IsInstanceValid(_enemySprite) && _enemySprite.Size.Y > 1f
            ? _enemySprite.Position + new Vector2(_enemySprite.Size.X * 0.5f, _enemySprite.Size.Y * 0.34f)
            : new(StageX(Arena.EnemyStanceX), FootY(_layout.StageBottom - _layout.StageTop) - Mathf.Max(40f, LiveStageHeight * 0.34f));


    /// <summary>屏震：整层偏移几帧，比挪每个对象省事，也不会打乱布局</summary>
    private void Shake(float amp, float duration)
    {
        _shakeAmp = Mathf.Max(_shakeAmp, amp);
        _shakeTime = Mathf.Max(_shakeTime, duration);
        _shakeDur = Mathf.Max(_shakeDur, duration);
    }

    private float _shakeAmp;
    private float _shakeDur = 1f;

    /// <summary>
    /// 上一次摆立绘时的「现场签名」：舞台可用高度 + 自己拿到的高度。
    ///
    /// 以前这是一个一次性开关（`_placedOnce`）：第一帧摆过就再也不摆。
    /// 可容器的第一趟布局会按「还没量过的父级矩形」再走一次 `PlaceSprites`，
    /// 那一趟里 Arena 还没铺开（`Size.Y <= 1`），算出来的脚是 0——
    /// 于是人站在屏幕最上面，头穿出敌人卡，而屏幕上看着只像「偏了一点」。
    /// 一次性开关管不到「后来又变了」，签名可以。
    /// </summary>
    private (float Stage, float Self) _placedFor = (-1f, -1f);

    public override void _Process(double delta)
    {
        // 舞台可用高度变了就重摆一次（日志收放、窗口缩放、布局落定都算）。
        var sig = (LiveStageHeight, Size.Y);
        if (Size.Y > 1f && !(_placedFor.Stage > 0f && Mathf.Abs(_placedFor.Stage - sig.LiveStageHeight) < 0.5f
                             && Mathf.Abs(_placedFor.Self - sig.Y) < 0.5f))
        {
            _placedFor = sig;
            PlaceSprites();
        }
        if (_shakeTime <= 0f) return;
        _shakeTime -= (float)delta;
        var k = Mathf.Max(0f, _shakeTime / _shakeDur);
        var amp = _shakeAmp * k * k;
        _stage.Position = new Vector2((float)GD.RandRange(-amp, amp), (float)GD.RandRange(-amp, amp));
        if (_shakeTime <= 0f)
        {
            _stage.Position = Vector2.Zero;
            _shakeAmp = 0f;
        }
    }

    private void OnDamage(DamageNumberEvent ev)
    {
        // 飘字：配色与字号由 DamageNumberRules 决定（暴击大 50%、治疗绿、持续伤害浅绿），
        // 界面只负责「从哪冒出来」——敌人从敌卡附近，玩家从底部血条附近。
        var pos = ev.Side == "enemy" ? EnemyAnchor() : PlayerAnchor();
        // 同一侧连着蹦出两个数时，第二个往上错开一行。
        //
        // 一次攻击常常同时结算「伤害 + 中毒」两条：两行字从同一个点、同一帧起飘，
        // 屏幕上重叠成一个认不出的字团。错开一行的代价是零，而玩家读得到两个数。
        // 判「还有几条活着」问飘字层自己（它每帧都在丢到期的），不在界面里另记一个数。
        var live = _damageNumbers?.ActiveCount ?? 0;
        pos += new Vector2((float)GD.RandRange(-60, 60), -DamageNumberRules.RowStep * Math.Min(live, 3));
        if (_fxEnabled) _damageNumbers?.Spawn(ev.Kind, -ev.Amount, pos);
        // 挨打的那一方抖一下（立绘闪白），这是「打中了」最直接的反馈
        if (ev.Side == "enemy") { _enemySprite?.Flash(); _enemySprite?.Recoil(); }
        else { _heroSprite?.Flash(); _heroSprite?.Recoil(); _heroChip?.Flash(); }
        if (ev.Amount > 0)
        {
            Sfx.Play(ev.Kind switch
            {
                "crit" => "crit",
                "heal" => "heal",
                "dot" => "hurt",
                "boss" => "boss",
                _ => "hit",
            });
            Shake(ev.Kind == "crit" ? 6f : 3f, 0.16f);
        }
    }

    private void OnLog(string line)
    {
        // 日志已经由 Battle.Log 承载，这里只用来做「新行闪一下」的提示。
        // 不额外存一份：两份日志迟早不一致，那时玩家看到的和结算的就是两回事。
    }
}
