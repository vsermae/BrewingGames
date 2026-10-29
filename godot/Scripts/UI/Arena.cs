// 战斗舞台的背景层：后墙、透视地砖、擂台弧线、两侧石柱、地雾；Boss 房再加岩浆裂缝。
//
// TS 版 drawArena / drawLava 是纯视觉的一块，却决定了「这是一场战斗」和
// 「这是一张表格」的区别。Godot 版原来只有一块纯色底 + 两行机制横幅，
// 立绘像是贴在表格上。这里把那一块补回来，数值逐条对照 TS 版：
// 地面高度 0.5h、砖缝 58 一行、透视地砖 6 行 21 列、石柱在 5% / 95% 两个位置。
//
// 它不承载任何规则——所以也不进断言，只保证「建得出来、画得下去」。
namespace AbyssEcho.UI;

using Godot;

public partial class Arena : Control
{
    /// <summary>双方的横向站位（占舞台宽的比例）。界面按这两个数摆立绘与飘字落点。</summary>
    public const float EnemyStanceX = 0.30f;
    public const float HeroStanceX = 0.70f;

    /// <summary>
    /// 落点光斑的中心在地板线下面多远。**立绘的脚不踩地板线，踩的是光斑的远沿**
    /// （见 `FootOnEllipse`）。
    ///
    /// 上一版光斑画在 `floorY + 62`、立绘的脚踩 `floorY`：屏幕上那两块椭圆
    /// 孤零零地飘在人物脚下 62px 处，像背景里两团莫名的色块——摆了三十多轮，
    /// 每轮都有人看见，但都被读成「装饰」。
    /// </summary>
    public const float StanceDrop = 62f;

    /// <summary>
    /// 脚比他那块光斑的中心高多少（占盒子高的比例）。
    ///
    /// 光斑是地面往观众这边延伸过来的那一块：脚踩在它的远沿、投影落进它的后半段，
    /// 才读得成「他站在那块地上」。踩到中心就等于站在一个圈的圆心，
    /// 再往下整个人就陷进地砖缝里了（这一版第一稿就是这么错的）。
    /// </summary>
    public const float FootOnEllipse = 0.17f;

    /// <summary>盒子多高的人，脚比光斑中心高多少</summary>
    public static float StanceLift(float boxH) => MathF.Max(0f, boxH * FootOnEllipse);

    /// <summary>
    /// 落点光斑的纵向半径。它和 `StanceDrop` 一起决定了「地板线下面要留多高的一条地」——
    /// 那一条地被行动条盖掉的话，屏上就是「怪没有腿」（见 `BattleLayout.StageFloorInset`）。
    /// </summary>
    public const float SpotRy = 22f;

    /// <summary>
    /// 立绘的脚该落在哪条线上（与舞台坐标系同一套）。界面摆立绘直接读它。
    ///
    /// 舞台（Arena）铺满视口，所以这里给的既是控件坐标也是视口坐标；换了父级就得加偏移。
    /// 三个数合在一起：地板线 + 光斑下沉 - 脚在光斑里的那一截。
    /// </summary>
    public static float FootLineY(float floorY, float footDrop, float boxH) =>
        floorY + footDrop - StanceLift(boxH);

    /// <summary>
    /// 地板的底色。Boss 房更暖更暗。
    ///
    /// 单拎出来是因为它不再是「一块填了色的矩形」：地板由底 + 两层浮光 + 一条
    /// 贴着踢脚线的压暗组成，四笔得用同一个色系往上叠，颜色散在四处就会叠成泥。
    /// </summary>
    private static readonly Color FloorCold = new("0f1422");
    private static readonly Color FloorWarm = new("28121a");

    /// <summary>后墙的底色。比背景亮一档，地板与它之间靠踢脚线分开</summary>
    private static readonly Color WallCold = new("111726");
    private static readonly Color WallWarm = new("1b0d13");

    /// <summary>
    /// 舞台的**光向**：左右两侧的墙比正中暗多少。
    ///
    /// 舞台是「中间一盏顶灯」的构图——两侧本来只有石柱和地雾，墙和地是均匀涂的，
    /// 于是舞台读起来像一块竖着的纸，立绘像贴纸。这一档压暗是纯氛围，
    /// 不动任何几何：它只回答「光从哪来」，不回答「谁站在哪」。
    /// </summary>
    private const float SideFalloff = 0.55f;

    private bool _isBoss;
    private float _t;
    private Color _warm = new("7cc0ff");
    private Color _floor = FloorCold;
    private Color _wall = WallCold;

    /// <summary>
    /// 舞台的**边**：横向压暗 / 纵向收口的余量。
    ///
    /// 舞台本身是满屏铺的（它得和 Arena 一样大，立绘的脚才能直接读控件坐标），
    /// 但满屏铺的色块会一直画到视口边上——1024×576 那一屏看得出「舞台的墙
    /// 在屏幕左边贴成一条硬直线」。这里让色块自己往内收一圈淡出去，
    /// 舞台就有了边，而坐标一点没动。
    /// </summary>
    private const float EdgeFadeX = 0.16f;
    private const float EdgeFadeY = 0.10f;

    /// <summary>地雾呼吸的相位：整块背景只有这一处动，别的都是静态线条</summary>
    public override void _Process(double delta)
    {
        _t += (float)delta;
        QueueRedraw();
    }

    public void Setup(bool isBoss)
    {
        _isBoss = isBoss;
        _warm = isBoss ? new Color("ff6b6b") : new Color("7cc0ff");
        _floor = isBoss ? FloorWarm : FloorCold;
        _wall = isBoss ? WallWarm : WallCold;
        MouseFilter = MouseFilterEnum.Ignore;
        QueueRedraw();
    }

    /// <summary>
    /// 立绘脚底踩的那条线（视口坐标）。
    ///
    /// 舞台的后墙/地板是按「控件尺寸的一半」切的，但立绘的脚踩的是 BattleLayout 算出来的
    /// 地板线（StageBottom = 行动条上沿 - 18），两条数是各算各的：1280×720 下地板线在
    /// y=452，程序切的地板在 y=360——差 92px。于是立绘的脚落在地板下面，而舞台画的
    /// 落点光斑留在地板上，屏幕上是「人站在光斑外面」，那块光成了背景里两团莫名的椭圆。
    ///
    /// 由 BattleScene 在每次重排时写进来；读不到（0）就退回旧行为，至少不会崩。
    /// </summary>
    public float FloorY { get; set; } = 0f;

    /// <summary>
    /// 舞台的左右边界（视口坐标）。卡与行动条是左右通栏的，立绘站位在它们里面那一截，
    /// 落点光斑就得画在同一截里——以前光斑按满宽算，站位按内缩算，
    /// 光斑圆心和脚就永远差着那一段内缩。
    /// </summary>
    public float StageLeft { get; set; } = 0f;
    /// <summary>舞台右边界。0 = 没写进来，退回满宽。</summary>
    public float StageRight { get; set; } = 0f;

    /// <summary>舞台横向的比例位置 → 视口坐标（和 BattleScene.StageX 同一把尺子）</summary>
    public float StanceX(float ratio)
    {
        var l = StageLeft;
        var r = StageRight > l + 1f ? StageRight : Size.X;
        return l + (r - l) * ratio;
    }

    /// `_Draw` 真的会用的那条地板线（FloorY 过一遍夹取）。界面靠它对齐立绘的脚：
    /// 读原始字段的话，「写进去了但 _Draw 不用」的写法照样能对上答案。
    /// </summary>
    public static float ClampFloor(float floorY, float controlH) =>
        floorY > 1f ? Mathf.Clamp(floorY, controlH * 0.28f, controlH * 0.86f) : controlH * 0.5f;

    /// <summary>这一帧渲染会用的地板线（控件坐标）</summary>
    public float FloorLine => ClampFloor(FloorY, Size.Y);

    /// <summary>
    /// 光斑中心相对地板线往下掉多少（不含「脚比光斑中心高」那一截）。
    /// `_Draw` 用它画，BattleScene 用它摆立绘的脚——这个函数存在的唯一理由
    /// 就是「两边不许各算一次」。
    /// </summary>
    public float FootDrop => Mathf.Min(StanceDrop, Mathf.Max(0f, Size.Y - 4f - FloorLine));

    public override void _Draw()
    {
        var w = Size.X;
        var h = Size.Y;
        if (w <= 1f || h <= 1f) return;
        // 后墙 / 地板的交界：立绘的脚踩在这条线上。
        // 夹在画面内是必要的——矮窗口下 StageBottom 可能贴近底边，让墙占满全屏就看不见地板了。
        var floorY = FloorLine;

        // 光斑不许掉到控件外面：它比地板线低 62px，矮窗口下地板线贴着底边时，
        // 画在控件外面就是「什么都没画」——而它正是立绘脚踩的那个点。
        var footDrop = FootDrop;

        // 地板：更暗更暖，铺满下半屏
        DrawRect(new Rect2(0, floorY, w, h - floorY), _floor);

        // 后墙：比背景亮一档，和地板之间用一条踢脚线分开
        DrawRect(new Rect2(0, 0, w, floorY), _wall);

        // 后墙的上下收光：墙与地板的交界不该是一条「一刀切」的亮暗线。
        //
        // 舞台后墙原来是**一整块平色 + 一张均匀的方格网**（见下面砖缝那一段），
        // 而后墙占了整屏的三分之二——在深色场景里，一整块平色是最显眼的一块
        // 「没画完」。现在给它两层：顶上压暗（顶灯照不到那么高），
        // 墙脚暖一点（离地砖近，地板的光该反上来一点）。
        Paint.VGradient(this, new Rect2(0, 0, w, floorY * 0.62f),
            new Color(0, 0, 0, 0.34f), new Color(0, 0, 0, 0f), 14);
        Paint.VGradient(this, new Rect2(0, floorY - floorY * 0.30f, w, floorY * 0.30f),
            new Color(0, 0, 0, 0f), new Color(_warm.R, _warm.G, _warm.B, 0.055f), 10);

        // 光向：左右两侧各压一道软衰减，舞台才有一盏「顶灯」。
        //
        // 这几笔是这一轮唯一新增的氛围画法，而它的前提是 `Paint.SoftEllipse`：
        // 原来那条路是 `Paint.Glow`（一摞同心椭圆），层数少时边是一条能数出来的线。
        // 舞台背景上那几圈环在 Boss 出场那一屏最刺眼——整屏一大片同心椭圆，
        // 玩家读到的是「一圈圈的环」而不是「一个深渊」。
        EdgeFalloff(w, h, floorY);
        // 墙面砖缝：横向拉长一点，才有地下大厅的进深
        //
        // 原来横竖都是 0.03 的白线、横 58px 一道、竖 96px 一道：竖线一路拉到地板线，
        // 整个后墙就是一张**均匀的方格网**——那不是砖，那是坐标纸。
        // 现在横缝保留（它读作「一层层的石砌」），竖缝改成**错缝**且只在下半截
        // （上半截本来就该被顶灯压掉），并且越往上越淡：砖墙的缝是有方向的。
        var seam = new Color(1, 1, 1, 0.03f);
        for (var y = 40f; y < floorY; y += 58f)
        {
            var deep = (floorY - y) / MathF.Max(1f, floorY);
            DrawLine(new Vector2(0, y), new Vector2(w, y), new Color(seam.R, seam.G, seam.B, seam.A * (0.35f + 0.65f * deep)), 1f);
        }
        var brickRows = 0;
        for (var y = 40f; y < floorY - 8f; y += 58f, brickRows++)
        {
            var off = brickRows % 2 == 0 ? 0f : 48f;
            var fade = Mathf.Clamp((y - floorY * 0.45f) / MathF.Max(1f, floorY * 0.55f), 0f, 1f);
            if (fade <= 0.01f) continue;
            for (var x = -20f + off; x < w + 20f; x += 96f)
                DrawLine(new Vector2(x, y), new Vector2(x, y + 58f), new Color(seam.R, seam.G, seam.B, seam.A * 0.55f * fade), 1f);
        }

        // 透视地砖：近疏远密的横向色带
        const int rows = 6;
        for (var r = 0; r < rows; r++)
        {
            var t0 = (float)r / rows;
            var t1 = (float)(r + 1) / rows;
            var y0 = floorY + (h - floorY) * t0 * t0;
            var y1 = floorY + (h - floorY) * t1 * t1;
            DrawRect(new Rect2(0, y0, w, y1 - y0), new Color(1, 1, 1, r % 2 == 0 ? 0.028f : 0.012f));
        }
        // 纵向透视线：由中心向两侧发散，撑出地面纵深
        const int cols = 10;
        var cx = w * 0.5f;
        for (var c = -cols; c <= cols; c++)
        {
            var xFar = cx + c * (w / cols) * 0.5f;
            var xNear = cx + c * (w / cols) * 1.5f;
            DrawLine(new Vector2(xFar, floorY), new Vector2(xNear, h), new Color(1, 1, 1, 0.035f), 1f);
        }
        // 踢脚线：一道暖光 + 一道压暗，地面才「站得住」
        DrawRect(new Rect2(0, floorY - 3, w, 3), new Color(_warm.R, _warm.G, _warm.B, 0.14f));
        DrawRect(new Rect2(0, floorY, w, 6), new Color(0, 0, 0, 0.25f));

        // 擂台：两圈同心的透视弧，把两个立绘框进同一舞台
        EllipseRing(new Vector2(w * 0.5f, floorY + 46), w * 0.35f, 60f, new Color(_warm.R, _warm.G, _warm.B, 0.2f), 2f);
        EllipseRing(new Vector2(w * 0.5f, floorY + 46), w * 0.45f, 84f, new Color(_warm.R, _warm.G, _warm.B, 0.1f), 1.5f);

        if (_isBoss) DrawLava(w, h, floorY);

        // 两侧远景石柱：只站边缘，不压中间的立绘
        foreach (var x in new[] { w * 0.05f, w * 0.95f })
        {
            var top = h * 0.26f;
            DrawRect(new Rect2(x - 20, top, 40, floorY - top), new Color("161d2f"));
            DrawRect(new Rect2(x + 8, top, 12, floorY - top), new Color(0, 0, 0, 0.32f));
            DrawRect(new Rect2(x - 28, top - 10, 56, 14), new Color("232c44"));
            DrawRect(new Rect2(x - 28, floorY - 14, 56, 14), new Color("232c44"));
            DrawRect(new Rect2(x - 20, top, 12, floorY - top), new Color(_warm.R, _warm.G, _warm.B, 0.06f));
        }

        // 地雾：一块会呼吸的光池，让地板不是一块死色。
        //
        // 呼吸的相位整块舞台共用一条（`breathe`），所以它在这里先算好——
        // 以前它写在下面，而新加的顶灯在它前面，读到未赋值就是 0：
        // 一屏「灯不呼吸」在静帧上看不出来，只有连看几帧才发现只有一处是死的。
        var breathe = 0.55f + 0.45f * (0.5f + 0.5f * Mathf.Sin(_t / 2.6f * Mathf.Tau));

        // 对峙双方的落点：两块「压在地上」的暗影 + 两圈族系色的余光。
        // 立绘的脚踩在这里，站位就不是由界面临时拍出来的坐标决定，
        // 而是「舞台上有两个位置」——这也是玩家读得出「谁在跟谁打」的前提。
        // 光斑的横向半径按「两个站位之间的距离」算，不按满屏宽：
        // 按满宽取 0.11 时，1024 宽下半径 113 大于「舞台内宽 × 0.15」（约 96），
        // 两块光斑在中间连成一条，读起来是一块底色而不是两个落点。
        var stanceSpan = MathF.Abs(StanceX(HeroStanceX) - StanceX(EnemyStanceX));
        var spotRx = MathF.Min(w * 0.11f, stanceSpan * 0.30f);
        // 横向问 StanceX（左右各内缩 12px），纵向问 floorY + footDrop
        // （立绘的脚踩在这块光斑的远沿，见 Arena.FootLineY）。
        foreach (var (sx, tint) in new[] { (StanceX(EnemyStanceX), _warm), (StanceX(HeroStanceX), new Color("7cc0ff")) })
        {
            var foot = new Vector2(sx, floorY + footDrop);
            // 落点的「压在地上」那一片：**软边**的暗，不是一个填实的椭圆。
            // 填实的椭圆在深色地上还好，一旦舞台亮了（Boss 房），它的边就成了
            // 一条能数出来的等高线——而那正是「一圈圈的环」的来源之一。
            // 三道软衰减**叠成一份**：中心总强度 ≈ 0.44 + 0.20 + 0.10。
            // 以前用的是 `Paint.Glow`（同心），边缘是一条能数出来的等高线；现在换成
            // 一次画完的径向渐变，但「三份叠起来」这件事没变——光斑是舞台地面上最亮的一块，
            // 亮到能看清「人站在它上面」，就够读了。三道各自满强度会糊成一团白斑。
            Paint.SoftEllipse(this, foot, spotRx, SpotRy, new Color(0, 0, 0, 1),
                0.28f * breathe + 0.08f, 7);
            // 族系色的余光：一内一外两条，外圈更淡更大，光才「散」得出去
            Paint.SoftEllipse(this, foot, spotRx * 0.95f, SpotRy - 2f, tint, 0.12f * breathe, 6);
            Paint.SoftEllipse(this, foot, spotRx * 0.55f, SpotRy * 0.55f, tint, 0.07f * breathe, 5);
        }

        // 地雾：一块会呼吸的光池，让地板不是一块死色
        //
        // 它原来是 `EllipseFill` 一次填实 + 单独一条 `EllipseRing` 描边，
        // 于是地板上有一个**填实的椭圆**——在 Boss 那屏（地面被岩浆染亮）上，
        // 它的边缘和上面那几圈环叠在一起，读起来是「同心圆」而不是「雾」。
        Paint.SoftEllipse(this, new Vector2(w * 0.5f, floorY + 60), w * 0.6f, 95f,
            _warm, 0.10f * breathe, 9);

        // 顶部一道「舞台灯」：从上方打下来的一片冷光，把两个人罩在同一个光斑里
        Paint.SoftEllipse(this, new Vector2(w * 0.5f, floorY * 0.55f), w * 0.34f, floorY * 0.75f,
            _warm, 0.055f * breathe, 9);
    }

    /// <summary>Boss 房的地面裂缝：几道发光折线 + 缓慢上飘的火星</summary>
    private void DrawLava(float w, float h, float floorY)
    {
        var hot = new Color("ff7a3d");
        var deep = new Color("ff3b1f");
        var cracks = new[]
        {
            new[] { new Vector2(w * 0.18f, h), new Vector2(w * 0.26f, floorY + 70), new Vector2(w * 0.33f, h * 0.82f) },
            new[] { new Vector2(w * 0.78f, h), new Vector2(w * 0.7f, floorY + 64), new Vector2(w * 0.62f, h * 0.8f) },
            new[] { new Vector2(w * 0.46f, h), new Vector2(w * 0.5f, floorY + 90), new Vector2(w * 0.55f, floorY + 40) },
        };
        foreach (var crack in cracks)
        {
            // 先粗后细两层：粗的那层是「岩壁还烫着」的余晖
            for (var i = 0; i < crack.Length - 1; i++)
            {
                DrawLine(crack[i], crack[i + 1], new Color(deep.R, deep.G, deep.B, 0.22f), 7f);
                DrawLine(crack[i], crack[i + 1], new Color(hot.R, hot.G, hot.B, 0.75f), 2f);
            }
        }
        // 火星：位置由固定公式推出来，不用随机数——背景不该吃掉主随机序列
        for (var i = 0; i < 14; i++)
        {
            var seed = i * 2.399963f;
            var x = w * (0.12f + 0.76f * ((seed * 0.618f) % 1f));
            var rise = ((_t * (0.12f + 0.03f * (i % 4)) + i * 0.137f) % 1f);
            var y = h - rise * (h - floorY - 30f);
            var a = (1f - rise) * 0.7f;
            DrawCircle(new Vector2(x, y), 1.6f + (i % 3) * 0.5f, new Color(hot.R, hot.G, hot.B, a));
        }
    }

    /// <summary>
    /// 舞台四周的收口：横向两侧压暗、上下沿各自淡出一层。
    ///
    /// 全部是软衰减，不许出现「一条边界」——舞台得读成「一块被灯照到的地」，
    /// 而不是「一块贴上去的矩形」。所以这里没有一条 `DrawRect` 是对齐视口边的，
    /// 四笔都以**屏幕外**为圆心往内散（见 `SoftEllipse` 的调用）。
    /// </summary>
    private void EdgeFalloff(float w, float h, float floorY)
    {
        var shade = new Color(0, 0, 0, 1);
        var fx = w * EdgeFadeX;
        // 两侧：圆心放在视口外面，只让靠内的半边落在画面上
        Paint.SoftEllipse(this, new Vector2(-fx * 0.15f, h * 0.45f), fx * 1.4f, h * 1.1f, shade, SideFalloff, 7);
        Paint.SoftEllipse(this, new Vector2(w + fx * 0.15f, h * 0.45f), fx * 1.4f, h * 1.1f, shade, SideFalloff, 7);
        // 上下沿：一道很浅的收口，让地面画到屏幕底之前先暗下去
        var fy = h * EdgeFadeY;
        Paint.SoftEllipse(this, new Vector2(w * 0.5f, -fy * 0.4f), w * 0.85f, fy * 1.6f, shade, 0.34f, 6);
        Paint.SoftEllipse(this, new Vector2(w * 0.5f, h + fy * 0.4f), w * 0.85f, fy * 1.6f, shade, 0.40f, 6);
    }

    private void EllipseFill(Vector2 c, float rx, float ry, Color color, int seg = 40)
    {
        var pts = new Vector2[seg];
        for (var i = 0; i < seg; i++)
        {
            var a = Mathf.Tau * i / seg;
            pts[i] = c + new Vector2(Mathf.Cos(a) * rx, Mathf.Sin(a) * ry);
        }
        DrawColoredPolygon(pts, color);
    }

    private void EllipseRing(Vector2 c, float rx, float ry, Color color, float width)
    {
        const int seg = 48;
        var prev = c + new Vector2(rx, 0);
        for (var i = 1; i <= seg; i++)
        {
            var a = Mathf.Tau * i / seg;
            var next = c + new Vector2(Mathf.Cos(a) * rx, Mathf.Sin(a) * ry);
            DrawLine(prev, next, color, width);
            prev = next;
        }
    }
}
