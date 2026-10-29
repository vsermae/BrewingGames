// 骑士立绘：纯矢量，地图与战斗共用同一份造型。
//
// TS 版 sprites.ts 的 paintHero 是 origin 为 (0,0)、约 34×58 的矢量拼装。
// 这里逐块照搬它的层次顺序：披风 → 腿靴 → 护甲 → 腰带 → 头与头盔 → 眼，
// 只把 Phaser 的 fillPoints/fillRoundedRect 换成 Godot 的 Draw*。
namespace AbyssEcho.UI;

using Godot;

public static class HeroArt
{
    /// <summary>设计尺寸：宽约 34、高约 58（头顶 -31 到靴底 27）</summary>
    public const float DesignHeight = 58f;

    /// <summary>
    /// 整套设计坐标的纵向跨度：头顶的羽毛 -37 到脚下投影 +22 = 59。
    /// 盒子高度换算时必须用它，不能用 DesignHeight——
    /// 少算那 22 像素的投影，比例尺会放大 65/58 ≈ 1.12 倍，
    /// 于是行动条里的骑士顶出格子、地图上的骑士压住上一块地砖（截图里就是这样）。
    /// </summary>
    public const float LayoutHeight = 65f;

    public static readonly Color Armor = new("4f7fd4");
    public static readonly Color Gold = new("f2c66d");

    /// <summary>把骑士画在 ci 的原点，scale 为缩放（1 ≈ 34×58 像素）。</summary>
    public static void DrawHero(CanvasItem ci, float scale = 1f, bool shadow = true)
    {
        Vector2 V(float x, float y) => new(x * scale, y * scale);
        void Poly(Color col, params (float, float)[] pts)
        {
            var arr = new Vector2[pts.Length];
            for (var i = 0; i < pts.Length; i++) arr[i] = V(pts[i].Item1, pts[i].Item2);
            Paint.Poly(ci, arr, col);
        }
        void Box(Color col, float x, float y, float w, float h, float r = 0)
        {
            var rect = new Rect2(V(x, y), new Vector2(w * scale, h * scale));
            if (r <= 0) Paint.Rect(ci, rect, col);
            else Paint.RoundRect(ci, rect, r * scale, col);
        }
        void Dot(Color col, float x, float y, float r) => Paint.Circle(ci, V(x, y), r * scale, col);
        void Segment(Color col, float x1, float y1, float x2, float y2, float width) =>
            Paint.Line(ci, V(x1, y1), V(x2, y2), col, width * scale);

        // 地面投影：没有它，立绘像贴纸一样浮在网格上
        if (shadow) Paint.Ellipse(ci, V(0, 22), 17f * scale, 5.5f * scale, new Color(0, 0, 0, 0.42f), 20);

        var outline = Paint.Darken(Armor, 0.42f);
        var lit = Paint.Lighten(Armor, 0.18f);
        var shade = Paint.Darken(Armor, 0.18f);
        var dark = new Color("161d30");

        // 画法：先压一层暗底，再按「亮面朝左上、暗面朝右下」叠色阶。
        // 上一版是平涂 + 零散高光，缩小到地图格里就糊成一块蓝；有了统一光向，
        // 同样尺寸才看得出体积，也不会因为细节多了变糊。

        // 披风：外轮廓 → 暗面 → 亮面 → 左缘高光。四层能把「布」和「板甲」分开
        Poly(outline, (-14, -8), (14, -8), (18, 19), (-18, 19));
        Poly(shade, (-11, -6), (11, -6), (14, 17), (-14, 17));
        Poly(Paint.Darken(Armor, 0.04f), (-11, -6), (4, -6), (5, 17), (-14, 17));
        Poly(new Color(1, 1, 1, 0.1f), (-9, -4), (-4, -4), (-5, 15), (-12, 15));
        // 披风下摆的褶皱：两条斜线，比一整块梯形更像布
        Segment(outline, -4, 9, -6, 17, 1.4f);
        Segment(outline, 6, 8, 9, 17, 1.4f);
        // 披风扣
        Dot(Gold, 0, -8, 3f);
        Dot(Paint.Darken(Gold, 0.24f), 0, -8, 1.4f);

        // 腿与靴：暗底 + 顶面亮一条，蹲在格子边沿也不发飘
        Box(dark, -9, 14, 7, 10, 3);
        Box(dark, 2, 14, 7, 10, 3);
        Box(Paint.Lighten(dark, 0.1f), -8.4f, 14.4f, 3, 7, 1.6f);
        Box(Paint.Lighten(dark, 0.1f), 2.6f, 14.4f, 3, 7, 1.6f);
        Box(new Color("2b3550"), -10, 21, 9, 5, 2.5f);
        Box(new Color("2b3550"), 1, 21, 9, 5, 2.5f);

        // 胸甲：暗底 → 主色 → 左上亮面 → 右下暗面 → 一条竖向高光
        Box(outline, -12, -10, 24, 27, 9);
        Box(Armor, -10, -8, 20, 23, 7);
        Poly(shade, (0, -8), (10, -8), (10, 12), (-2, 15));
        Poly(lit, (-10, -8), (1, -8), (0, 12), (-10, 13));
        Box(new Color(1, 1, 1, 0.3f), -7, -6, 4.5f, 16, 2.4f);
        // 胸甲的分片中缝：一条暗线把它劈成两片甲
        Segment(outline, 0, -6, 0, 12, 1.2f);

        // 肩甲：外缘暗、上缘亮，向两侧张开一点
        Box(outline, -17, -9, 9, 13, 4.5f);
        Box(outline, 8, -9, 9, 13, 4.5f);
        Box(lit, -16, -8, 7, 11, 4);
        Box(lit, 9, -8, 7, 11, 4);
        Box(new Color(1, 1, 1, 0.28f), -15.5f, -7.5f, 5, 3.4f, 2);
        Box(new Color(1, 1, 1, 0.28f), 9.5f, -7.5f, 5, 3.4f, 2);

        // 腰带与金扣
        Box(new Color("6b4a2a"), -11, 7, 22, 4);
        Box(new Color("8a6238"), -11, 7, 22, 1.4f);
        Box(Gold, -2.5f, 6, 5, 6);
        Box(Paint.Darken(Gold, 0.3f), -2.5f, 10, 5, 1.2f);

        // 头：暗底 → 肤色 → 右下暗面。脸只露一条，其余被头盔压住
        Dot(outline, 0, -17, 12f);
        Dot(new Color("f3d6b0"), 0, -16, 10f);
        Poly(new Color("d9b58e"), (2, -25), (9, -19), (8, -9), (1, -7));

        // 头盔：顶穹 + 护鼻 + 面罩缝，左上亮、右下暗
        Poly(new Color("9fc6ff"), (-12, -18), (0, -32), (12, -18), (12, -14), (-12, -14));
        Poly(new Color("6a94c8"), (2, -18), (12, -18), (12, -14), (2, -14));
        Poly(new Color("bcdcff", 0.9f), (-9, -18), (-1, -29), (2, -29), (-6, -18));
        Box(new Color("1a2740"), -11, -19, 22, 4.4f);
        // 护鼻
        Box(new Color("1a2740"), -1.4f, -15, 2.8f, 6);
        // 头盔顶的羽毛：一小撮金色，远看是个点睛
        Poly(Gold, (0, -32), (6, -37), (9, -33), (3, -30));
        // 眼睛：面罩缝里两点光
        Dot(new Color("dff0ff"), -4.4f, -17, 1.5f);
        Dot(new Color("dff0ff"), 4.4f, -17, 1.5f);
    }

    /// <summary>
    /// 用单色把人「盖」一遍，只留给受击白闪用。
    /// 不复用 DrawHero 是因为它逐块分层填色——想变白只能每块都换色，
    /// 那样细节会一起消失。这里走剪影路线：只画外轮廓的实心块。
    /// </summary>
    public static void DrawTint(CanvasItem ci, float scale, Color color)
    {
        Vector2 V(float x, float y) => new(x * scale, y * scale);
        void Poly(params (float, float)[] pts)
        {
            var arr = new Vector2[pts.Length];
            for (var i = 0; i < pts.Length; i++) arr[i] = V(pts[i].Item1, pts[i].Item2);
            Paint.Poly(ci, arr, color);
        }
        Paint.RoundRect(ci, new Rect2(V(-18, -8), new Vector2(36 * scale, 27 * scale)), 9 * scale, color);
        Paint.Circle(ci, V(0, -17), 12f * scale, color);
        Poly((-17, -9), (17, -9), (17, 4), (-17, 4));
        Poly((-10, 14), (10, 21), (10, 26), (-10, 26));
        Poly((-1, -32), (0, -37), (9, -33), (6, -30));
    }
}

/// <summary>
/// 地图上的玩家：骑士 + 脚下光晕 + 呼吸浮动 + 移动时压扁。
/// 对应 TS 版 createPlayerSprite —— 那三个动画是「看起来像活人」的全部秘密。
/// </summary>
public partial class HeroSprite : Control
{
    private float _scale = 1f;
    private float _bob;
    private float _glowPulse = 1f;
    private bool _moving;
    private float _squash = 1f;
    private float _lunge;
    private bool _lungeForward = true;
    private float _flash;

    /// <summary>
    /// 面向：+1 朝右、-1 朝左。
    ///
    /// 骑士的设计坐标是「朝右」摆的，站在舞台右侧时他的攻击动作、头盔的斜面
    /// 全都在背离敌人——两个人背对背打架。地图上要朝右（也就是不用翻），
    /// 战斗里站在右边的那个要朝左。翻的是整个绘制变换，造价只有一次 scale。
    /// </summary>
    public float Facing = 1f;

    public HeroSprite()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(48, 64);
    }

    public void SetHeroScale(float scale) => _scale = scale;

    /// <summary>是否正在做动作，以及方向（同 MonsterSprite 的语义）</summary>
    public bool IsActing => _lunge > 0f;

    /// <summary>true = 前冲（攻击），false = 后仰（受击）</summary>
    public bool LungesForward => _lungeForward;

    /// <summary>挨打时闪一下白，和敌人立绘同一套反馈</summary>
    public void Flash() => _flash = 1f;

    /// <summary>出手前冲：和敌人立绘同一套动作语汇（朝着对手），受击则后仰</summary>
    public void Lunge() { _lunge = 1f; _lungeForward = true; }

    /// <summary>挨打后仰</summary>
    public void Recoil() { _lunge = 1f; _lungeForward = false; }

    /// <summary>移动时压扁一下：110ms 的横向拉伸，回到 1</summary>
    public void PlayStep()
    {
        _squash = 1.12f;
        _moving = true;
    }

    public override void _Process(double delta)
    {
        var dt = (float)delta;
        // 呼吸浮动：±5px / 1.5s；光晕在 0.16 与 0.30 之间来回
        _bob = Mathf.Sin((float)Time.GetTicksMsec() / 1500f * Mathf.Pi) * 3.5f;
        _glowPulse = 0.16f + 0.14f * (0.5f + 0.5f * Mathf.Sin((float)Time.GetTicksMsec() / 1700f * Mathf.Pi));
        if (_moving)
        {
            _squash = Mathf.MoveToward(_squash, 1f, dt * 1.6f);
            if (Mathf.IsEqualApprox(_squash, 1f)) _moving = false;
        }
        if (_lunge > 0f) _lunge = Mathf.Max(0f, _lunge - dt * 3.2f);
        if (_flash > 0f) _flash = Mathf.Max(0f, _flash - dt * 4f);
        QueueRedraw();
    }

    public override void _Draw()
    {
        // 出手位移：玩家站舞台右侧，所以「向前」是朝左（朝着敌人）。
        // 后仰时朝右、幅度小一点——受击是被动动作，别抢镜。
        var punch = Mathf.Sin(_lunge * Mathf.Pi) * 22f;
        var lean = _lungeForward ? -punch : punch * 0.7f;
        // 竖向基准取框底：DesignHeight(58) 里的人从头顶 -37 画到靴底 26，
        // 所以「脚底」在原点下方 26*_scale 处。把这段减掉，人就真的踩在框底，
        // 而不是一半身子挂在框外面（以前用 Size*0.5f + 6，人整体悬空约 10px）。
        // 投影最低点在 y=+22（见 HeroArt.LayoutHeight）。把这一截从框高里减掉，
        // 靴底（+26）才会真的踩在框底上、投影才落在框内。
        var foot = 22f * _scale + 2f;
        var c = new Vector2(Size.X * 0.5f + lean, Size.Y - foot - 2f + _bob);
        // 横向压扁与面向合在一个变换里：先按 Facing 取 x 的符号，再乘上压扁系数
        var sx = _squash * (Facing < 0f ? -1f : 1f);
        DrawSetTransform(c, 0, new Vector2(sx, 2f - _squash));
        // 脚下蓝色光晕：让立绘不像贴纸
        Paint.Ellipse(this, new Vector2(0, 20 * _scale), 27 * _scale, 10 * _scale,
            Paint.Alpha(new Color("7cc0ff"), _glowPulse), 24);
        HeroArt.DrawHero(this, _scale);
        if (_flash > 0f)
        {
            // 白闪只叠一层半透明本体：直接把人画成纯白会把披风/护甲的层次全糊掉
            DrawSetTransform(c, 0, Vector2.One);
            HeroArt.DrawTint(this, _scale, new Color(1f, 1f, 1f, _flash * 0.5f));
        }
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }
}
