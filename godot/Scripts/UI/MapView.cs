// 地图的「该画多大」与「画成什么样」。
//
// 这两件事以前都散在 MapScene 里：格距写死 92、房间块写死 64、
// 走廊是一条 2px 直线、每个房间一个 Button 各带一套 StyleBox。
// 结果是一屏 400×400 的图贴在 1280×720 的正中间，四周两千多像素的空。
//
// 拆出来的理由和 BattleLayout 一样：**尺寸是算术，算术要能被断言**。
// MapMetrics 是纯函数，UiSmoke 直接算「画幅占可用区域的比例」；
// MapLines / RoomTile 只用 _Draw，它们的尺寸也来自同一个 MapMetrics。
namespace AbyssEcho.UI;

using Godot;
using AbyssEcho.Core;

/// <summary>
/// 地图这一帧的格距与原点。
/// `Tile` 是设计格距，`Span` 是实际用到的（视口不够就往下收，收到下限为止），
/// `Origin` 是「第一个房间块的左上角」，不是网格中心——
/// 房间块尺寸跟着 Span 走，居中的判据用整块包围盒，比用格心更稳。
/// </summary>
public readonly record struct MapMetrics(float Span, float Room, Vector2 Origin, int MinX, int MinY)
{
    /// <summary>设计值：格距 92、房间块占 86%（原版 tile − 10）</summary>
    public static readonly MapMetrics Design = new(92f, 92f * 0.86f, Vector2.Zero, 0, 0);

    /// <summary>
    /// 顶部给「提示飞行」留的那条带子（见 `ToastRules.BandHeight`）。
    ///
    /// 提示胶囊会往上飘 34px，而地图底下（网格 → 图例 → 提示条）在 1280×720 下只剩 12px，
    /// 提示只能挪到网格上方。不给它留一条带子，它就压在 HUD 上——
    /// 「提示不许压 HUD」和「地图不许压 HUD」是同一条线，地图先让开才谈得上摆正。
    /// </summary>
    public const float ToastBand = ToastRules.BandHeight;

    /// <summary>
    /// 实际给网格让出的那条带子的高度。
    ///
    /// 比 `ToastBand` 少一截：网格是**垂直居中**的，所以「带子」只决定画幅上限，
    /// 上下两边的余量会各自补上一半。留满 78 时网格反而用不到 78（它被高度约束住，
    /// 居中之后上方还空着 38），等于白扔一档格距。
    /// 「够放下一个胶囊」是 `PillHeight + Clearance`，上飘的那 34px 落在其余量里。
    /// </summary>
    public const float ResumeBand = ToastRules.PillHeight + ToastRules.Clearance;

    /// <summary>
    /// 网格四周留的那圈「地砖浮在背景上」的呼吸位（单边 14，上下一共 28）。
    ///
    /// 原来是左右 56 / 上下 36：1280×720 下 5×4 的图被吃掉的可用高是 74 + 46 + 36 + 54 = 210，
    /// 于是画幅只到 508 高，上下各空出一百多像素——而屏幕上读起来是「地图很小、字很大」，
    /// 后半句才是问题（见 `MapScene.TileFontScale`）。留白从 36 收到 28 不是为了让砖更大，
    /// 是为了让 HUD 里那几行字能按格距放大而不撞到边缘。
    /// </summary>
    public const float GridMargin = 28f;

    /// <summary>
    /// 按「可用区域」算一格能有多大。
    ///
    /// 上一版直接写死 92：1280 宽里画幅只有 450px，居中之后两边各空 400 多，
    /// 玩家看到的是一张小图漂在大屏中间。这里反过来算——
    /// 先看可用区域能把格距撑到多大，上限仍是设计值（别为了一张 4×4 的图放大到糊）。
    /// </summary>
    /// <param name="cap">格距上限。默认给 1280×720 下的设计值；
    /// 画幅撑不满时调用方可以传更大的值——「别浪费屏幕」比「守住设计值」重要。</param>
    public static MapMetrics Fit(Vector2 viewport, float top, float bottom, int minX, int minY, int cols, int rows,
        float reservedRight = 0f, float cap = 92f, float floor = 44f)
    {
        // 只留一圈「地砖浮在背景上」的呼吸位（左右 28 / 上下 18），
        // 并且要把右下的图例竖带减掉——图例是浮在地图上的，不该和地图抢同一块地。
        // 上一版这里减了 32 + 40 两重留白，5 列的图在 1280 宽下只算出 76 的格距，
        // 画幅 450px 漂在正中，两边的空白比地图本身还宽。
        var availW = MathF.Max(1f, viewport.X - reservedRight - GridMargin);
        // 顶上还要留出「提示飞行带」：底下只剩 12px 时提示只能挪到网格上方，
        // 而 HUD 下沿到网格上沿那点余量放不下一个会飘的胶囊——不给它留带子它就压在 HUD 上。
        //
        // 带子不该比胶囊需要的更多。上一版按「胶囊 + 上飘 34 + 两侧气」整条留死（78px），
        // 可网格上方的空地是**居中**的：留 78、网格只用到 40，剩下 38px 白扔。
        // 那 38px 直接变成格距的一档——1280×720 下地图画幅因此只有 488×486，
        // 四周全是空。现在按「胶囊本身 + 一侧气」留（54px），网格居中的余量自己补上另一半。
        var availH = MathF.Max(1f, viewport.Y - top - bottom - GridMargin - ResumeBand);
        // 网格宽度 = (cols - 1) × span + room，room = span × 0.86，
        // 所以「填满可用宽」对应的 span 要除掉 (cols - 0.14) 而不是 cols——
        // 差这 0.86 个格距就是一整列放不下、格距被迫收一档的原因。
        var usableW = availW;
        var usableH = availH;
        var spanForW = usableW / MathF.Max(1f, cols - 0.14f);
        var spanForH = usableH / MathF.Max(1f, rows - 0.14f);
        var span = MathF.Min(cap, MathF.Min(spanForW, spanForH));
        span = MathF.Max(floor, span);
        var room = span * 0.86f;
        var gridW = (cols - 1) * span + room;
        var gridH = (rows - 1) * span + room;
        // 右下图例占了一条竖带：地图在「减掉它之后的那块」里居中，
        // 否则 1024 宽下最后两列的房间会压在图例上（截图里就是这样）。
        // 水平居中在「除掉图例竖带」的区域里；垂直居中在 HUD 与提示条之间
        var x = (viewport.X - reservedRight - gridW) / 2f;
        var y = top + ToastBand + (viewport.Y - top - bottom - ToastBand - gridH) / 2f;
        return new MapMetrics(span, room, new Vector2(x, y), minX, minY) { GridRows = rows, GridCols = cols };
    }

    /// <summary>
    /// 网格底边的下沿（视口坐标）。提示胶囊要挂在它**下面**：
    /// 地图是垂直居中的，1280×720 下最后一排砖压到 y≈628，而提示胶囊按
    /// 「提示条上沿 - 14」算出来的顶边在 611——两条横着交叉，胶囊盖住一整块砖。
    /// 「提示弹在屏幕下方」这句需求是对的，错的是「下方」不等于「提示条上方那 14px」。
    /// </summary>
    public float GridBottom => Origin.Y + GrownRows * Span + Room;

    /// <summary>网格顶边的上沿</summary>
    public float GridTop => Origin.Y;

    /// <summary>这一帧网格占了几行 / 几列（`Fit` 的入参，记下来给「网格底边」和断言用）</summary>
    public int GridRows { get; init; } = 1;
    public int GridCols { get; init; } = 1;

    /// <summary>
    /// 网格一共占几条「格距」。
    /// `Origin` 是第 0 行的左上角，`rows` 是行数，所以从第 0 行顶到末行底是 `rows - 1` 个格距再加一块砖。
    /// 上一版写成了 `rows` 个格距——多算了一整格（92px），于是「网格下沿」比真砖低，
    /// 提示胶囊照着它摆，正好骑在最后一排砖上（截图里「宝箱房」被吃掉一半）。
    /// </summary>
    private int GrownRows => Math.Max(0, GridRows - 1);

    /// <summary>房间块左上角（该房间可能是包围盒之外的点，负偏移照常算）</summary>
    public Vector2 TopLeft(int x, int y) => Origin + new Vector2((x - MinX) * Span, (y - MinY) * Span);

    public Vector2 Center(int x, int y) => TopLeft(x, y) + new Vector2(Room / 2f, Room / 2f);

    /// <summary>画幅占「可用区域」的比例：1 表示正好铺满，越小越像漂在中间</summary>
    /// <summary>
    /// 画幅占可用区域的比例：1 = 正好铺满，越小越像「漂在中间」。
    /// 「占满」在宽高两个方向上是分别算的，所以取两边的 max：
    /// 一个方向顶格就说明这张图已经撑到极限，另一边的留白是长宽比造成的，不是浪费。
    /// </summary>
    public static float Coverage(Vector2 viewport, float top, float bottom, int cols, int rows, MapMetrics m)
    {
        var availW = MathF.Max(1f, viewport.X - 64f - 40f);
        var availH = MathF.Max(1f, viewport.Y - top - bottom - 32f - 40f);
        var gridW = (cols - 1) * m.Span + m.Room;
        var gridH = (rows - 1) * m.Span + m.Room;
        return MathF.Max(gridW / availW, gridH / availH);
    }
}

/// <summary>
/// 走廊、门框、可达光点。
///
/// 原版是一条「先横后竖」的折线，三层描边做厚度，接缝处补一个门框方块，
/// 并且只连接**已探索**的房间（迷雾感靠这个，而不是靠把线画淡）。
/// Godot 版此前是一条 2px 直线，13 个房间 12 条线，看着像电路图。
/// </summary>
public partial class MapLines : Control
{
    private readonly List<(Vector2 A, Vector2 B, bool Known, Color Tint)> _lines = new();
    private readonly List<(Vector2 At, Color Tint)> _steps = new();
    private float _span = 92f;

    public MapLines()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public void Set(float span)
    {
        _span = span;
        QueueRedraw();
    }

    public void Clear()
    {
        _lines.Clear();
        _steps.Clear();
    }

    /// <summary>一条走廊：A 是房间块中心，B 是邻居中心；Known = 至少一端已探索</summary>
    public void AddLine(Vector2 a, Vector2 b, bool known, Color tint) => _lines.Add((a, b, known, tint));

    /// <summary>可走方向上的跳动光点（原版放在走廊中点）</summary>
    public void AddStep(Vector2 at, Color tint) => _steps.Add((at, tint));

    public override void _Draw()
    {
        var t = _span;
        foreach (var (a, b, known, tint) in _lines)
        {
            // 先横后竖：原版 drawCorridor 的走法。斜线会让「两个房间是不是相邻」变得不可读。
            var mid = new Vector2(b.X, a.Y);
            var pts = new[] { a, mid, b };
            // 三层描边 = 厚度。最外层最暗，最内层最亮，读起来像地砖下压着的石板路。
            // 最外层：贴地的一圈暗光。走廊落在背景上如果没有这一层，
            // 它就像「画在地图上的线」，而不是「挖在石头里的路」。
            DrawPolyline(pts, new Color(0, 0, 0, 0.28f), MathF.Max(12f, t * 0.30f));
            DrawPolyline(pts, new Color(0.08f, 0.11f, 0.17f), MathF.Max(8f, t * 0.22f));
            // 已探索的走廊更亮：未探索的那段几乎只剩一道暗影，这是「迷雾」的表达。
            var body = known ? new Color(0.165f, 0.208f, 0.314f) : new Color(0.105f, 0.132f, 0.19f);
            DrawPolyline(pts, body, MathF.Max(6f, t * 0.15f));
            var core = known ? new Color(0.28f, 0.35f, 0.51f) : new Color(0.155f, 0.19f, 0.27f);
            DrawPolyline(pts, core, MathF.Max(2f, t * 0.055f));

            // 门框：两格中点上一个小方块（外深内更深），原版同款
            var door = (a + b) * 0.5f;
            var d = MathF.Max(5f, t * 0.13f);
            Paint.RoundRect(this, new Rect2(door - new Vector2(d, d), new Vector2(d * 2, d * 2)), d * 0.35f,
                known ? new Color(0.231f, 0.29f, 0.427f) : new Color(0.15f, 0.19f, 0.27f));
            Paint.RoundRect(this, new Rect2(door - new Vector2(d * 0.6f, d * 0.6f), new Vector2(d * 1.2f, d * 1.2f)),
                d * 0.2f, new Color(0.106f, 0.137f, 0.216f));
        }

        foreach (var (at, tint) in _steps)
        {
            // 可走方向：一个亮芯 + 一圈淡光。原版是 900ms 呼吸的 tween，
            // 这里交给 _Process 的相位（有帧才动，headless 下就是静态的一点，不影响断言）。
            var pulse = 0.55f + 0.45f * MathF.Sin(_phase * 3.2f + at.X * 0.05f);
            Paint.Circle(this, at, MathF.Max(3.5f, t * 0.055f), Paint.Alpha(tint, 0.95f));
            Paint.Circle(this, at, MathF.Max(7f, t * 0.11f), Paint.Alpha(tint, 0.16f * pulse));
        }
    }

    private float _phase;

    public override void _Process(double delta)
    {
        _phase += (float)delta;
        if (_steps.Count > 0) QueueRedraw();
    }
}

/// <summary>
/// 一块房间格：地砖 + 类型色 + 砖缝 + 徽章底 + 矢量图标 + 状态角标。
///
/// 以前每格是一个 `Button` 加一套 `StyleBoxFlat`：底板在 StyleBox 上，
/// `Button` 的圆角与图标层的圆角对不齐，两层叠起来整块发灰；
/// 而「已清空的敌人房打勾」「起点标签」这些细节一个都没有。
/// 现在底板、砖缝、徽章、角标全在同一个 `_Draw` 里，悬停只改一个透明度。
/// </summary>
public partial class RoomTile : Control
{
    public string Kind = "rune";

    /// <summary>
    /// 这一格**没去过**时露出来的那个类型（`null` = 雾里什么都不说）。
    ///
    /// 地图上最该说的两件事，一件是「值不值得走一趟」，一件是「这一趟有多贵」。
    /// 以前两件都说：未探索的砖在类型色底下压着徽章的余温，颜色一露，玩家不用走
    /// 就知道那边是精英房；于是「探索」这个动作在地图上只是一个过场——
    /// 走过去的路上已经把答案读完了。
    ///
    /// 现在只留前一件：补给、宝箱、事件照常亮着（它们是玩家决定走哪边的依据），
    /// 而「那边有几场仗、有几场躲得过」要走近了才看得见。迷雾要藏的是**代价**，
    /// 不该藏**方向**——把方向也藏起来，那就不叫迷雾，叫随机。
    /// </summary>
    public RoomType? FaintType {
        get => _faintType;
        set { _faintType = value; QueueRedraw(); }
    }
    private RoomType? _faintType;

    /// <summary>未探索时露出来的类型色；`FaintType` 为 null 时这一格在雾里是中性的</summary>
    public Color FaintAccent {
        get => _faintAccent;
        set { _faintAccent = value; QueueRedraw(); }
    }
    private Color _faintAccent = Colors.White;

    public Color Tint = Colors.White;
    /// <summary>类型色（地砖底色）：比图标色淡，两者不是一回事</summary>
    public Color Accent = Colors.White;
    public bool Visited;
    public bool Cleared;
    public bool Reachable;
    public bool IsStart;
    public bool IsEnemy;
    public bool Hovered;

    /// <summary>
    /// 上沿「被光打到」那一片亮带的起排点（像素）。
    /// `_Draw` 真的用它起排，冒烟也读它——判据比的是「亮带会不会盖住描边的内侧一半」。
    /// 上一版是一个写死的 2，而描边以砖沿为折线、左右各铺半个线宽，
    /// 于是亮带压掉描边内侧一半：圆角处混出一圈比砖面暗的颜色，
    /// 读作「砖角的描边被抠了个口子」。
    /// </summary>
    private float HighlightInset => 2f + (Reachable ? 2.2f : 1.8f) * 0.5f;

    /// <summary>给冒烟用：亮带起排点（`_Draw` 同一份算法）</summary>
    public float HighlightInsetForProbe => HighlightInset;

    /// <summary>
    /// 玩家此刻正站在这一格上。
    ///
    /// 起点格上压着两样东西，而且它们都长在「徽章」那一套坐标里：
    /// 底下的房间图标（`IconArt` 现画的一枚房子/匕首）和上沿的「起点」标签。
    /// 两者叠在一起，中间那一竖正好被骑士的头劈成两半——截图里那块金字读起来
    /// 像一个被掰开的字，既不像「起点」，也不像任何图标。
    ///
    /// 这一格被骑士站着的时候，砖只需要说「你在这儿」：图标和标签一起让开，
    /// 砖留成一块干净的、带金色描边的地。让开的不是「起点」这个信息——
    /// 玩家正站上去的那一格，用不着再写一遍它是起点。
    /// </summary>
    public bool Standing {
        get => _standing;
        set { _standing = value; QueueRedraw(); }
    }
    private bool _standing;

    /// <summary>这块砖会画出整套徽章（色环 + 图标 + 起点标签）</summary>
    public bool DrawsBadge => !_standing;

    /// <summary>
    /// 这一格所在的岔路底下还压着几场没打完的仗（见 `Game.PendingBattlesBelow`）。
    /// 0 = 这条岔路已经走干净了，玩家不必再往里走。
    ///
    /// 地图上原本只有「打过 / 没打过」两种像素：走进一条死胡同、发现是空的、
    /// 再原路走回来，是这游戏里最普遍的几步路——而地图生成器在生成那一刻
    /// 就知道哪边有仗。知道自己知道却不说，就是「玩家在猜、程序在瞒」。
    /// </summary>
    public int PendingBelow {
        get => _pendingBelow;
        set { _pendingBelow = value; QueueRedraw(); }
    }
    private int _pendingBelow;

    /// <summary>
    /// 这一格是「从玩家脚下到 Boss 的必经之路」上的下一间战斗房（见 `Game.BattlesToBoss`）。
    ///
    /// 红点是数量，这个标记是位置。剩下的仗里哪一场躲不过，才是玩家站在岔口要判断的事：
    /// 一层平均 3.2 场仗，挡在去 Boss 路上的只有 1.1 场，而以前这两件事在地图上看不出区别。
    /// </summary>
    public bool OnBossPath {
        get => _onBossPath;
        set { _onBossPath = value; QueueRedraw(); }
    }
    private bool _onBossPath;

    public RoomTile()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        var s = MathF.Min(Size.X, Size.Y);
        if (s < 12f) return;
        var r = new Rect2(Vector2.Zero, new Vector2(s, s));
        var pad = s * 0.5f;
        var corner = s * 0.16f;

        if (!Visited)
        {
            // 未探索：一块比背景亮一档的剪影 + 一点类型色的余温 + 一个呼吸的问号。
            // 原版同一条思路——迷雾不该是死灰块，也不该抢戏。
            // 但「不抢戏」不等于「什么都没有」：这里给它极淡的斜纹与内描边，
            // 于是它读起来是「被雾盖住的砖」，而不是「屏幕上的一块灰矩形」。
            Paint.RoundRect(this, r, corner, new Color(0.072f, 0.094f, 0.152f));
            // 雾里只说方向：补给 / 宝箱 / 事件这几类照旧留一点自己的色温，
            // 而「那边有几场仗」的砖在雾里是一块中性的暗砖——要走过去才知道。
            // 判据只有一处（`FaintType`），颜色与图标都读它：
            // 一条规则写两遍，改一处另一处就还在说旧话。
            var faint = FaintType is null ? Accent : FaintAccent;
            if (FaintType is not null) Paint.RoundRect(this, r, corner, Paint.Alpha(faint, 0.075f));
            Paint.Hatches(this, r, new Color(1, 1, 1, 0.012f), s * 0.16f, 1f);
            Paint.RoundRect(this, new Rect2(2, 2, s - 4, s * 0.38f), corner * 0.9f, corner * 0.9f, 0, 0,
                new Color(1, 1, 1, 0.03f));
            Paint.RoundRectOutline(this, r, corner, new Color(0.16f, 0.20f, 0.31f), 1.6f);
            // 内描边：未探索的砖也要有厚度，否则一半的房间看起来是「没画」
            Paint.RoundRectOutline(this, new Rect2(3.5f, 3.5f, s - 7, s - 7), corner * 0.85f,
                new Color(1, 1, 1, 0.022f), 1f);
            DrawSetTransform(new Vector2(pad, pad), 0, Vector2.One);
            Paint.Ring(this, Vector2.Zero, s * 0.17f, s * 0.155f, Paint.Alpha(faint, 0.06f), 28);
            // 问号只画在「雾里什么都没说」的砖上。已经报了类型的砖再顶一个问号，
            // 玩家读到的是两句互相打岔的话：「这是商店」和「这是未知」。
            IconArt.Draw(this,
                FaintType is null ? "question" : IconArt.ForRoom(FaintType.Value.ToString()),
                s * 0.30f,
                FaintType is null ? new Color(0.34f, 0.40f, 0.53f) : new Color(0.50f, 0.57f, 0.70f));
            DrawSetTransform(Vector2.Zero, 0, Vector2.One);
            return;
        }

        // 地砖厚度：先在最下面垫一层暗色偏下 6% 的方块，读作「砖有侧边」。
        // 侧边用 accent 压深而不是纯黑：砖从底下的走廊里「立」起来，颜色得是一路的。
        Paint.RoundRect(this, new Rect2(0, s * 0.07f, s, s * 0.95f), corner, Paint.Darken(Accent, 0.55f));
        Paint.VGradient(this, r, new Color(0.098f, 0.129f, 0.203f), new Color(0.062f, 0.082f, 0.137f),
            Math.Max(4, (int)(s / 9f)), corner);
        var body = Cleared ? 0.13f : 0.26f;
        Paint.RoundRect(this, r, corner, Paint.Alpha(Accent, body));
        Paint.RoundRect(this, new Rect2(6f, 6f, s - 12f, s - 12f), corner * 0.75f, Paint.Alpha(Accent, body * 0.4f));
        // 石面纹理：斜纹 + 几粒石点。这一层几乎看不见，但少了它
        // 整块砖就是一张纯色圆角矩形——放大看尤其明显。
        Paint.Hatches(this, new Rect2(2, 2, s - 4, s - 4), new Color(1, 1, 1, 0.014f), s * 0.14f, 1f);
        Paint.Speckle(this, new Rect2(4, 4, s - 8, s - 8), new Color(1, 1, 1, 0.022f),
            Math.Max(6, (int)(s * 0.22f)), MathF.Max(0.6f, s * 0.012f));

        // 砖缝：错开的短缝，比一律三条横线更像砌出来的
        var seam = new Color(1, 1, 1, 0.055f);
        var third = s / 3f;
        Paint.Line(this, new Vector2(2, third), new Vector2(s - 2, third), seam, 1f);
        Paint.Line(this, new Vector2(2, third * 2), new Vector2(s - 2, third * 2), seam, 1f);
        Paint.Line(this, new Vector2(s / 2, 2), new Vector2(s / 2, third), seam, 1f);
        Paint.Line(this, new Vector2(s / 4, third), new Vector2(s / 4, third * 2), seam, 1f);
        Paint.Line(this, new Vector2(s * 0.75f, third * 2), new Vector2(s * 0.75f, s - 2), seam, 1f);

        // 描边：可达的那一格更亮，告诉玩家「这一步能走」
        var edge = Reachable ? Paint.Lighten(Accent, 0.28f) : Accent;
        var edgeW = Reachable ? 2.2f : 1.8f;
        Paint.RoundRectOutline(this, r, corner, Paint.Alpha(edge, Reachable ? 1f : 0.75f), edgeW);
        // 内描边：一圈 5% 白线，砖面与边框之间就有了「倒角」
        Paint.RoundRectOutline(this, new Rect2(3.5f, 3.5f, s - 7, s - 7), corner * 0.88f,
            new Color(1, 1, 1, Cleared ? 0.02f : 0.05f), 1f);
        // 上沿「被光打到」的那一片：铺在描边**以内**。
        //
        // 它原来从 (2,2) 起、高 0.4s，而描边（RoundRectOutline）是以砖沿为折线画的，
        // 于是这张贴片会盖在描边的**内侧一半**上，混出一圈比砖面暗一点的颜色。
        // 可达的砖上这一圈跟描边叠在一起还看不清；骑士站的那一格没有图标挡着，
        // 于是四角露出来的是「砖角的描边被抠掉一个口子」（截图放大 4 倍才看得清）。
        // 现在起排点让开半个线宽：亮带只在砖面里，碰不到描边。
        var edgeInset = HighlightInset;
        if (Reachable)
        {
            // 可达：上沿一片高光 + 一圈外扩的余光（「这块在发亮」而不只是「框更粗」）
            Paint.RoundRect(this, new Rect2(edgeInset, edgeInset, s - edgeInset * 2f, s * 0.4f),
                corner * 0.9f, corner * 0.9f, 0, 0, new Color(1, 1, 1, 0.07f));
            Paint.RoundRectOutline(this, new Rect2(-1.5f, -1.5f, s + 3, s + 3), corner * 1.1f,
                Paint.Alpha(edge, 0.16f), 2.5f);
        }

        // 徽章底 + 图标：外圈色环 + 一圈上缘挑亮，图标本身用 IconArt 现画。
        // 骑士站着的那一格不画图标——见 Standing 的说明：图标和标签会一起被劈开。
        if (!Standing)
        {
            DrawSetTransform(new Vector2(pad, pad - s * 0.02f), 0, Vector2.One);
            // 色环收紧到 0.26 / 0.225：上一版 0.30 的环几乎贴到砖缝上，
            // 每块砖看着都像镶了一圈彩色边框，图标反而被挤小了。
            Paint.Ring(this, Vector2.Zero, s * 0.26f, s * 0.225f, Paint.Alpha(Accent, Cleared ? 0.18f : 0.32f), 32);
            Paint.Arc(this, Vector2.Zero, s * 0.245f, MathF.PI * 1.1f, MathF.PI * 1.9f, new Color(1, 1, 1, 0.13f), 14);
            IconArt.Draw(this, Kind, s * 0.40f, Cleared ? Paint.Darken(Accent, 0.3f) : Tint);
            DrawSetTransform(Vector2.Zero, 0, Vector2.One);

            if (IsStart)
            {
                // 起点标签压在地砖上沿：立绘站在格子里也碰不到它
                var w = s * 0.62f;
                var tr = new Rect2(s / 2f - w / 2f, -s * 0.06f, w, s * 0.20f);
                Paint.RoundRect(this, tr, s * 0.06f, Paint.Alpha(Palette.Gold, 0.22f));
                DrawString(ThemeDB.FallbackFont, new Vector2(tr.Position.X, tr.Position.Y + s * 0.155f), "起点",
                    HorizontalAlignment.Center, w, (int)Math.Clamp(s * 0.15f, 8f, 13f), Palette.Gold);
            }
        }
        else
        {
            // 站着的那一格：地砖四角各一道内缩的亮线，读作「脚下这块亮着」。
            // 不画文字也不画图标——这一格上唯一需要被认出来的东西是骑士本人。
            var k = Paint.Alpha(Palette.Gold, 0.5f);
            const float t = 1.2f;
            foreach (var (dx, dy, sx, sy) in new[] { (1f, 1f, 1f, 1f), (-1f, 1f, -1f, 1f), (1f, -1f, 1f, -1f), (-1f, -1f, -1f, -1f) })
            {
                var x0 = s / 2f + dx * s * 0.34f;
                var y0 = s / 2f + dy * s * 0.30f;
                Paint.Line(this, new Vector2(x0, y0), new Vector2(x0 + sx * s * 0.10f, y0), k, t);
                Paint.Line(this, new Vector2(x0, y0), new Vector2(x0, y0 + sy * s * 0.10f), k, t);
            }
        }

        if (Cleared && IsEnemy)
        {
            // 已清空的敌人房打勾：一个圆底 + 一道折线
            var c = new Vector2(s - s * 0.2f, s - s * 0.21f);
            var rad = s * 0.16f;
            Paint.Circle(this, c, rad, new Color(0.043f, 0.071f, 0.125f, 0.85f));
            Paint.Ring(this, c, rad, new Color(0.39f, 0.9f, 0.66f), 1.4f);
            DrawPolyline(new[]
            {
                c + new Vector2(-rad * 0.45f, 0.5f),
                c + new Vector2(-rad * 0.1f, rad * 0.45f),
                c + new Vector2(rad * 0.55f, -rad * 0.55f),
            }, new Color(0.39f, 0.9f, 0.66f), MathF.Max(2f, s * 0.045f));
        }

        if (OnBossPath && Visited)
        {
            // 「这一场躲不过」：地砖外框描一圈暖橙。
            // 放在最外圈而不是内圈：内圈被图标和角标占满，而这条信息的读法是
            // 「这块砖整体不一样」，不是「这块砖上多了个数字」。
            Paint.RoundRectOutline(this, r, corner, new Color(0.95f, 0.66f, 0.32f, 0.85f), MathF.Max(1.6f, s * 0.05f));
        }

        if (PendingBelow > 0)
        {
            // 「这边还有仗没打完」：右下角一枚红点，里面写还剩几场。
            // 三个位置各让一步，才轮到右下：
            //   · 右上被「已清空」的打勾占着，而这两个角标说的是相反的两件事；
            //   · 左下被骑士占着——玩家开场就站在起点格上，而起点报的正是
            //     「这一层还剩几场」，所以那个红点永远压在人的腿上（截图抓到的）。
            // 数字超过 9 就不写数字了——那不是信息量，那是像素噪声。
            // 再往外收一档：0.8 那一版虽然挪开了图标，但人的肩膀还在那儿，
            // 数字被压在肩线下面读不出来。徽标要落在「地砖的角上」，不是「地砖里」。
            var c = new Vector2(s * 0.86f, s - s * 0.15f);
            var rad = s * 0.15f;
            Paint.Circle(this, c, rad, new Color(0.30f, 0.09f, 0.11f, 0.95f));
            Paint.Ring(this, c, rad, new Color(0.95f, 0.42f, 0.36f), 1.4f);
            if (PendingBelow <= 9)
                DrawString(ThemeDB.FallbackFont, new Vector2(c.X - rad, c.Y + s * 0.055f), PendingBelow.ToString(),
                    HorizontalAlignment.Center, rad * 2f, (int)Math.Clamp(s * 0.17f, 8f, 13f),
                    new Color(1f, 0.78f, 0.72f));
            else
                Paint.Circle(this, c, rad * 0.4f, new Color(1f, 0.78f, 0.72f));
        }
    }
}

/// <summary>
/// 地图右下角的房间图例。
///
/// 原版 `createLegend` 是一块 136×212 的面板，十行「图标 + 名称」，顶上一句「? = 未探索」。
/// Godot 版一直没有这张表：玩家只能靠图标猜「那个货摊是不是商店」，
/// 而图标本身画得再细，第一次见也读不出来。
/// </summary>
public partial class MapLegend : Control
{
    public const float Width = 142f;

    /// <summary>
    /// 图例正文的字号（12）。
    ///
    /// 11 是一屏上最小的字，而图例恰好是「不认就玩不下去」的那一块——
    /// 它现在和地图提示条（14）差两档，读起来是「附注」而不是「说明书」。
    /// 12 是这一档里还能把 11 行塞进 142 宽的一档（再大一档「诅咒房」就要折行）。
    /// </summary>
    public const int LegendFontSize = 12;

    /// <summary>两列时的宽度：两栏图标 + 名称</summary>
    public const float WidthTwoColumn = 236f;

    public static float WidthFor(int columns) => columns <= 1 ? Width : WidthTwoColumn;

    /// <summary>
    /// 一行的设计高度：14px 的图标 + 上下各 3.5px 的气。
    ///
    /// 19 → 21：图例是**一屏上唯一需要逐行去认的东西**，而它原来用的是 11px 的字
    /// （一屏上最小的字）。19 这个数不是从「11px 的字该多高」算出来的，
    /// 是「13px 的图标 + 上下各 3」——图标比字大，于是行被图标撑高、字在行里悬着。
    /// 现在图标和字一起放大一档，行距跟着涨，行数不变（11 行）。
    /// </summary>
    private const float RowH = 21f;

    /// <summary>
    /// 正文行距。
    ///
    /// 以前这里算的是 `Min(RowH, (面板高 - 内边距 - 表头) / 行数)`：面板高本来就是按
    /// 「行数 × 19」给的，除回去必然得 19，等于自己跟自己对答案；而 `_Draw` 又不用这个数，
    /// 它自己拿 `HeadH * 0.35` 当起排余量。两套账的结果是每行的「?」顶在上一行图标上。
    /// 现在两边共用 `BodyTop` / `RowPitch`，而行距就是设计行高——
    /// 面板高度不够时由 `LegendFits` 报错，不靠压行距硬塞。
    /// </summary>
    private float RowPitch => ComputePitch(Size.X > Width + 8f ? 2 : 1);

    /// <summary>
    /// 正文第一行的**格顶**：表头整行（`HeadH`）之后。
    /// 文字基线在格里再往下 `ascent` 一点（见 `_Draw`），所以这里给的是格子而不是基线。
    /// </summary>
    private float BodyTop => PadY + HeadH;

    /// <summary>正文行数 = 面板高能装下的行数（一列时 11、两列时 6）</summary>
    private int BodyRows =>
        (int)MathF.Round((Size.Y - PadY * 2 - HeadH) / RowH);

    private const float PadY = 12f;

    /// <summary>
    /// 表头占的那一行：18。
    ///
    /// 16 是「11px 的字 + 5px 的气」——字号和行距一起放大之后，表头这一行也得跟着长，
    /// 否则「? = 未探索」的基线会压到正文第一行的图标上（那一格的图标正好在 y=P2..P4）。
    /// `HeadH` 是**面板算高**与**绘制**共用的同一个数（`HeightFor` / `BodyTop` 都读它），
    /// 所以改一处，面板与内容一起动。
    /// </summary>
    private const float HeadH = 18f;

    public const float HeadHeight = HeadH;

    /// <summary>
    /// 面板总高：表头单独一行 + 正文若干行 + 上下内边距。
    /// 以前表头挤在 PadY 那 12px 里（`HeadH * 0.4` 只当余量用），于是它和正文第一行
    /// 抢同一块位置——「起点」被「? = 未探索」盖掉一半。表头既然占了一行，就得给它一行的高度。
    /// </summary>
    public static float HeightFor(int rows) => rows * RowH + PadY * 2 + HeadH;

    /// <summary>
    /// 竖排（142 宽）与折两列（236 宽）各要占屏多少：判 `ColumnsFor` 用。
    ///
    /// 只回答一件事：折成两列省下的是**高度**（11 行 → 6 行），代价是**宽度**。
    /// 1280×720 上高度够、宽度紧，所以应当竖排；1920×1080 两样都够，也竖排。
    /// 上一版的判据恰好反着写（要求屏宽 ≥ 1418 才竖排），于是 1280 上折成两列，
    /// 而两列比一列多吃 94px 宽——为了省宽度反而吃了宽度。
    /// </summary>
    public static bool VerticalFits(Vector2 viewport) =>
        viewport.Y - 74f - 46f >= HeightFor(11) + 40f;



    /// <summary>面板高度要能装下的最小高（与 HeightFor 同源，断言直接引用它）</summary>
    public static float NeededHeight(int items, int columns) => HeightFor((items + columns - 1) / columns);

    /// <summary>
    /// 让一块指定尺寸的图例自报它要用的两个数。给冒烟用。
    ///
    /// 为什么不在断言里按同一套公式再算一遍：那样等于自己跟自己对答案，永远绿。
    /// 这两个值来自 `_Draw` 真的会读的 `RowPitch` / `BodyTop`。
    /// </summary>
    public static float RowPitchNarrowed(Vector2 size) => Measure(size, l => l.RowPitch);

    /// <summary>正文第一行的顶边：`_Draw` 真的从这儿开始排字</summary>
    public static float BodyTopNarrowed(Vector2 size) => Measure(size, l => l.BodyTop);

    /// <summary>`_Draw` 里正文第一行的**格顶**。与 `BodyTop` 分开是有意的：
    /// 两套账的那一版 `_Draw` 自己起排、`BodyTop` 只给面板算高，
    /// 于是「格顶」和「面板以为的格顶」不是一个数。判据比的是这个。</summary>
    private float FirstBodyTop => _drawnBodyTop;

    /// <summary>
    /// `_Draw` 这一趟真的从哪一行开始排。以前它是个局部变量（`var bodyTop = ...`），
    /// 面板报给外面的 `BodyTop` 是另算的一个数——两套账，而只有一套会画到屏上。
    /// 「判据读的值」和「渲染用的值」必须是同一个，否则判据只能证明那句注释没写错。
    /// </summary>
    private float _drawnBodyTop = float.NaN;

    /// <summary>表头那一行占到哪：`PadY` + 表头行高（正文一格都不许压进这条线）</summary>
    public const float HeadLineBottom = PadY + HeadH;

    /// <summary>正文第一行真的从哪儿起排（给冒烟用：它要和表头的下线比）</summary>
    public static float FirstBodyTopNarrowed(Vector2 size) => Measure(size, l =>
    {
        // 让这块面板真的走一遍 `_Draw` 的排版分支：不画到屏上（没有父节点，也没有 canvas item），
        // 只让它把 `_drawnBodyTop` 写出来。上一版这两个值是两个局部变量，
        // 断言读到的那个从来没被渲染用过——「自己跟自己对答案」的第五次。
        l.DrawForMeasure();
        return l.FirstBodyTop;
    });

    /// <summary>只跑排版那一段（不落墨）：Measure 用</summary>
    private void DrawForMeasure() => _ = DrawLayout();

    /// <summary>
    /// 正文行距：面板高按「表头一行 + 正文 N 行 × RowH」给的，所以 N 行时行距就是 RowH。
    /// 上一版这里写的是 `min(RowH, (高 - 内边距 - 表头) / 6)`——**除的是写死的 6 行**，
    /// 而一列时正文有 11 行：面板高够 11 行，行距却被摊成 12px，每行的「?」顶在上一行图标上。
    /// </summary>
    private float ComputePitch(int columns) => RowH;

    /// <summary>`_Draw` 这一趟真的用的行距（给冒烟用）</summary>
    private float _drawnPitch = float.NaN;

    /// <summary>
    /// `_Draw` 的**排版那一段**：算出这一趟真的会用的起排与行距（不落墨）。
    /// `_Draw` 与 `Measure` 都走这里——上一版 `DrawForMeasure` 自己把 `RowPitch` 抄了一遍，
    /// 于是不管 `_Draw` 怎么写，判据读到的都是「正确」的那个值（第五次自己跟自己对答案）。
    /// </summary>
    private (float BodyTop, float Pitch) DrawLayout()
    {
        var columns = Size.X > Width + 8f ? 2 : 1;
        _drawnPitch = ComputePitch(columns);
        _drawnBodyTop = ComputeBodyTop(columns);
        return (_drawnBodyTop, _drawnPitch);
    }

    /// <summary>行距的硬下限：13px 的图标 + 字，一行至少站得下 14px</summary>
    public const float MinRowPitch = 14f;

    /// <summary>图例一共几项（给冒烟用：别在断言里再抄一遍 11）</summary>
    public static int RowsForProbe => Rows.Length;

    /// <summary>一列 11 行时 `_Draw` 真的会用的行距（给冒烟用）</summary>
    public static float FirstBodyPitchNarrowed(Vector2 size) => Measure(size, l =>
    {
        l.DrawForMeasure();
        return l._drawnPitch;
    });

    /// <summary>正文第一行的格顶：按列数算，`_Draw` 与 Measure 共用这一处</summary>
    private float ComputeBodyTop(int columns)
    {
        // 两列时正文从表头基线往下排（`PadY + 11`），只留 0.35 行距的余量——
        // 那是「表头挤在 PadY 那 12px 里」的时代留下的写法，表头现在占满一整行 16px。
        return columns > 1 ? PadY + 11f + RowPitch * 0.35f : BodyTop;
    }

    /// <summary>
    /// 这块面板装不装得下这些行。
    /// 只比「NeededHeight ≤ Size.Y」不够——上一版那个数是绿的，
    /// 正文行距却按写死的 6 行摊，一列 11 行时每行的问号压在上一行图标上。
    /// </summary>
    private static float Measure(Vector2 size, Func<MapLegend, float> pick)
    {
        var probe = new MapLegend { Size = size };
        var v = pick(probe);
        probe.QueueFree();
        return v;
    }

    public static bool LegendFits(Vector2 size, int columns)
    {
        var rows = (int)MathF.Round((size.Y - PadY * 2 - HeadH) / RowH);
        var perCol = (Rows.Length + Math.Max(1, columns) - 1) / Math.Max(1, columns);
        if (rows < perCol) return false;
        var pitch = MathF.Min(RowH, (size.Y - PadY * 2 - HeadH) / rows);
        // 行距不许压成「图标和字挤在一起」：13px 的图标加字，一行至少要 14px。
        // 上一版按写死的 6 行摊，一列 11 行时行距只剩 12.3；
        // 而第一版判据只查了「行数够不够」，于是它是绿的——**查行数不等于查行距**。
        // 末行中线再往下半行，不许越过面板下内边距
        var lastRowBottom = PadY + HeadH + (perCol - 1) * pitch + pitch * 0.5f + RowH * 0.5f;
        return lastRowBottom <= size.Y - PadY + 0.5f;
    }

    /// <summary>
    /// 图例要不要折成两列。
    ///
    /// 旧判据是「1100×700 以上才竖排」，1280×720 正好落在竖排那一侧——于是图例独占
    /// 右下一条 142px 的竖带，地图只能在剩下的宽度里排，5 列的图只占到 61% 的画幅，
    /// 两边是三百多像素的空。
    /// 改成按「竖排装不装得下」判：11 行 × 19 + 内边距要 ~250px，
    /// 而横屏（常见 720 高）下方要给提示条留位置，装不下就折成两列——
    /// 两列只有 6 行、118px 高、236 宽，贴底一条，地图反而能撑满整个宽度。
    /// </summary>
    public static int ColumnsFor(Vector2 viewport)
    {
        // 判据就一条：图例能不能「不占地方」。
        // 竖排是 142×250 的一条，横排是 236×118 的一条——
        // 横屏（宽 > 高）时竖直空间本来就紧，再竖一条会把地图的宽度切掉一大块，
        // 所以只要不是「又宽又高到能白摆一条竖带」（≥1600×900），就折成两列贴底。
        // 判的是「竖排装不装得下」，只看高度。
        //
        // 上一版还加了 `viewport.X - Width - 56f >= 1200f` 这一项——它判的其实是
        // 「屏幕宽 ≥ 1418」，和地图一点关系都没有：1280×720 上于是折成两列，
        // 而两列（236）比一列（142）**多吃 94px 宽度**。为了不让图例吃宽度，
        // 结果多吃了宽度：5 列的图被挤到 61% 的画幅，两边三百多像素的空。
        return VerticalFits(viewport) ? 1 : 2;
    }

    // 按「玩家最常在找的那几种」排序：商店与宝箱放前面，
    // 空房间（门）在最后——它是最没有信息量的一行。
    private static readonly (RoomType Type, string Kind)[] Rows =
    {
        (RoomType.Start, "flag"),
        (RoomType.Enemy, "sword"),
        (RoomType.Elite, "shield"),
        (RoomType.Treasure, "chest"),
        (RoomType.Shop, "cart"),
        (RoomType.Event, "question"),
        (RoomType.Altar, "sparkle"),
        (RoomType.Training, "muscle"),
        (RoomType.Curse, "rune"),
        (RoomType.Boss, "skull"),
        (RoomType.Empty, "door"),
    };

    public MapLegend()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        var r = new Rect2(Vector2.Zero, Size);
        // 图例是一块浮在地图上的「说明牌」：投影 + 纵向渐变 + 内描边 + 斜纹。
        // 以前是 85% 不透明的一片纯色，与地图上的砖抢同一个亮度，读起来糊在一起。
        Paint.RoundRect(this, new Rect2(2, 3, Size.X, Size.Y), 10f, new Color(0, 0, 0, 0.35f));
        Paint.VGradient(this, r, Paint.Alpha(Palette.PanelSoft, 0.94f), Paint.Alpha(Palette.PanelDeep, 0.96f), 12, 10f);
        Paint.Hatches(this, new Rect2(1, 1, Size.X - 2, Size.Y - 2), new Color(1, 1, 1, 0.014f), 8f, 1f);
        Paint.RoundRectOutline(this, r, 10f, Paint.Alpha(Palette.BorderLit, 0.55f), 1.2f);
        Paint.RoundRectOutline(this, new Rect2(2.5f, 2.5f, Size.X - 5, Size.Y - 5), 8.5f,
            new Color(1, 1, 1, 0.04f), 1f);

        // 表头：字号 11 的字基线不能压在 PadY 上方（那样会被面板圆角切掉一截）。
        // 以前这里写的是 `PadY + HeadH*0.4 - 3` ≈ 12.4，字的下缘正好落在描边线上，
        // 截图里那几个字看起来是「贴着上边框被削掉一半」。
        DrawString(ThemeDB.FallbackFont, new Vector2(10f, PadY + 13f), "? = 未探索", HorizontalAlignment.Left,
            Size.X - 20f, LegendFontSize - 1, Palette.Dim);

        // 正文从表头**整行**下面起排：以前第一行直接叠在表头基线上，
        // 于是「起点」那一行被「? = 未探索」压了一半；后来改了 `HeadH * 0.35`
        // 当余量，看着好像够了，实际十行里每一行的「**?**」都顶在上面那行的图标上——
        // 面板算高时给表头留了 16px 的一行，正文却按 5.75px 起排，两处又是两套账。
        // 现在两边用同一个数（`DrawLayout`），表头就是一行，正文从它底下开始。
        var (bodyTop, pitch) = DrawLayout();
        var columns = Size.X > Width + 8f ? 2 : 1;
        var colW = (Size.X - 16f) / columns;
        var perCol = (Rows.Length + columns - 1) / columns;
        for (var i = 0; i < Rows.Length; i++)
        {
            var (type, kind) = Rows[i];
            var col = columns == 1 ? 0 : i / perCol;
            var row = columns == 1 ? i : i % perCol;
            var x = 8f + col * colW;
            var y = bodyTop + row * pitch;
            DrawSetTransform(new Vector2(x + 11f, y + pitch * 0.5f - 2f), 0, Vector2.One);
            IconArt.Draw(this, kind, 14f, AccentFor(type));
            DrawSetTransform(Vector2.Zero, 0, Vector2.One);
            // 图例文字缩进必须让开图标：图标画在 x+10、直径 13，于是它的右缘在 x+16.5。
            // 原来写的是 x+22，两列只剩 118px 一列时，11px 的字正好压上图标半边
            // （截图里「未探索」那一行就是被旗子盖住的）。给到 x+28，
            // 可用宽度同步收窄，长名字会自动折，而不是冲进第二列。
            DrawString(ThemeDB.FallbackFont, new Vector2(x + 30f, y + pitch * 0.5f + 3.5f), MapGen.RoomLabel[type],
                HorizontalAlignment.Left, colW - 32f, LegendFontSize, Palette.Muted);
        }
    }

    /// <summary>
    /// 图例里的颜色必须和地图上的地砖类型色一致，否则图例只是在讲另一套语言。
    /// 地图侧（MapScene.RoomAccent）与这里共用同一张表。
    /// </summary>
    public static Color AccentFor(RoomType type) => type switch
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
        RoomType.Start => Palette.Gold,
        _ => Palette.Muted,
    };
}
