// 主菜单的装饰层：字标、尘埃、两侧图标。
//
// 这三样原版都有（MenuScene.makeWordmark / spawnDust / 装饰剑与骷髅），
// 而 Godot 版一直是一行白标题 + 一行灰副标题——玩家一眼能看出「这不是原版那屏」。
// 全部仍走纯矢量：不引入任何贴图。
namespace AbyssEcho.UI;

using Godot;

/// <summary>
/// 字标：每个字一块带底光的小方牌，奇偶错位排开。
/// 布局是纯函数（WordmarkLayout），UiSmoke 直接断言「字牌不重叠、总宽与字号对得上」。
/// </summary>
public partial class Wordmark : Control
{
    public string Text = "深渊回响";
    public float CellSize = 53f;
    public float Gap = 3f;
    public Color Accent = Palette.Gold;

    /// <summary>字标下面那条金色短横线。画在同一个控件里，
    /// 是因为容器（CenterContainer）不给子控件宽度——单独放一个 Control 时
    /// 它拿到的 Size 一直是 0，线一次都没画出来过（试了三轮布局都不行）。</summary>
    public bool Underline;

    private Label[] _labels = Array.Empty<Label>();
    private float _t;

    /// <summary>牌子占用区的左右边界（相对控件左边）：装饰要摆在它外面</summary>
    public static (float Left, float Right) WordmarkBounds(int count, float cell, float gap, float controlWidth)
    {
        var (_, _, total) = WordmarkLayout(count, cell, gap);
        var half = controlWidth / 2f;
        return (half - total / 2f, half + total / 2f);
    }

    /// <summary>
    /// 排版：每块牌的中心 x（相对控件左边）与竖向错位。
    /// 单独暴露出来是为了能断言——上一版原版里装饰剑插进首字，就是因为宽度靠估。
    /// </summary>
    public static (float[] Centers, float[] Lifts, float TotalWidth) WordmarkLayout(int count, float cell, float gap)
    {
        var total = count * cell + Math.Max(0, count - 1) * gap;
        var centers = new float[count];
        var lifts = new float[count];
        for (var i = 0; i < count; i++)
        {
            centers[i] = -total / 2f + cell / 2f + i * (cell + gap);
            lifts[i] = i % 2 == 0 ? -4f : 4f;
        }
        return (centers, lifts, total);
    }

    public void Build()
    {
        var chars = Text.ToCharArray();
        var (centers, lifts, total) = WordmarkLayout(chars.Length, CellSize, Gap);
        // 高度留 1.35 倍：牌子本身 CELL、6px 投影、±6px 浮动，再加 12px 给下划线与间距
        CustomMinimumSize = new Vector2(total + CellSize, CellSize * 1.35f + (Underline ? 14f : 0f));
        Size = CustomMinimumSize;

        foreach (var old in _labels) old.QueueFree();
        _labels = new Label[chars.Length];
        var half = Size.X / 2f;
        var midY = Size.Y / 2f;
        for (var i = 0; i < chars.Length; i++)
        {
            // 字号取牌高的 0.66：上一版取 0.92，字比牌高，CJK 字形的上下留白
            // 直接把「深」的顶部顶出牌面——牌看着像被人从中间扎了一刀。
            var label = UiTheme.Text(chars[i].ToString(), (int)(CellSize * 0.66f), new Color("f6f8ff"));
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.65f));
            label.AddThemeConstantOverride("shadow_offset_x", 0);
            label.AddThemeConstantOverride("shadow_offset_y", 3);
            label.MouseFilter = MouseFilterEnum.Ignore;
            label.Size = new Vector2(CellSize, CellSize);
            // Label 自己的居中靠 HorizontalAlignment/VerticalAlignment，
            // 所以要给它正好等于牌子的 rect，位置就是牌子的左上角。
            label.Position = new Vector2(half + centers[i] - CellSize / 2f, midY + lifts[i] - CellSize / 2f);
            label.PivotOffset = label.Size * 0.5f;
            AddChild(label);
            _labels[i] = label;
        }
    }

    public override void _Process(double delta)
    {
        _t += (float)delta;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var chars = Text.ToCharArray();
        var (centers, lifts, _) = WordmarkLayout(chars.Length, CellSize, Gap);
        var half = Size.X / 2f;
        var midY = Size.Y / 2f;
        // 整体极慢的上下浮动：原版是 2.6s 往返 ±6px
        var bob = Mathf.Sin(_t / 2600f * Mathf.Tau) * 6f;
        for (var i = 0; i < chars.Length; i++)
        {
            var cx = half + centers[i];
            var cy = midY + lifts[i] + bob;
            var r = new Rect2(cx - CellSize / 2f, cy - CellSize / 2f, CellSize, CellSize);
            // 投影 → 牌面 → 描边 → 上缘底光，四层照抄原版 makeWordmark
            Paint.RoundRect(this, new Rect2(r.Position + new Vector2(0, 6), r.Size), 10f, new Color(0, 0, 0, 0.35f));
            Paint.RoundRect(this, r, 10f, new Color("1b2540"));
            Paint.RoundRectOutline(this, r, 10f, Paint.Alpha(Paint.Darken(Accent, 0.55f), 0.85f), 1.6f);
            Paint.RoundRect(this,
                new Rect2(r.Position + new Vector2(2, 2), new Vector2(r.Size.X - 4, r.Size.Y * 0.45f)),
                8f, Paint.Alpha(Accent, 0.16f));
        }

        if (!Underline) return;
        // 下划线：按字标实际总宽取 62%，居中在牌子下方。原版同一条算式。
        var (_, _, totalW) = WordmarkLayout(chars.Length, CellSize, Gap);
        var uw = Mathf.Max(120f, totalW * 0.62f);
        var uy = Size.Y - 6f;
        Paint.RoundRect(this, new Rect2(half - uw / 2f, uy - 1.5f, uw, 3f), 1.5f, Paint.Alpha(Accent, 0.85f));
        Paint.RoundRect(this, new Rect2(half - uw * 0.16f, uy - 1.5f, uw * 0.32f, 3f), 1.5f, new Color(1, 1, 1, 0.35f));
    }
}

/// <summary>漂浮尘埃：26 粒慢慢往上飘、慢慢淡出，飘到底就从下面重新来。</summary>
public partial class DustLayer : Control
{
    public int Count = 26;
    public int Seed = 4242;

    private struct Mote
    {
        public Vector2 Pos;
        public float Radius;
        public float Speed;
        public float Alpha;
        public float Phase;
    }

    private Mote[] _motes = Array.Empty<Mote>();
    private bool _init;

    public override void _Process(double delta)
    {
        if (Size.X <= 1f || Size.Y <= 1f) return;
        if (!_init)
        {
            _init = true;
            Init();
        }
        for (var i = 0; i < _motes.Length; i++)
        {
            var m = _motes[i];
            m.Pos.Y -= m.Speed * (float)delta;
            if (m.Pos.Y < -10f)
            {
                m.Pos.Y = Size.Y + 10f;
                m.Pos.X = (float)GD.RandRange(0, Size.X);
            }
            _motes[i] = m;
        }
        QueueRedraw();
    }

    private void Init()
    {
        var rng = new Random(Seed);
        _motes = new Mote[Count];
        for (var i = 0; i < Count; i++)
            _motes[i] = new Mote
            {
                Pos = new Vector2((float)rng.NextDouble() * Size.X, (float)rng.NextDouble() * Size.Y),
                Radius = 0.8f + (float)rng.NextDouble() * 1.6f,
                Speed = 10f + (float)rng.NextDouble() * 24f,
                Alpha = 0.10f + (float)rng.NextDouble() * 0.25f,
                Phase = (float)rng.NextDouble() * Mathf.Tau,
            };
    }

    public override void _Draw()
    {
        var t = (float)Time.GetTicksMsec() / 1000f;
        foreach (var m in _motes)
        {
            // 呼吸：每粒有自己的相位，否则 26 粒会同时亮同时暗
            var a = m.Alpha * (0.65f + 0.35f * Mathf.Sin(t * 0.8f + m.Phase));
            Paint.Circle(this, m.Pos, m.Radius, new Color(0.62f, 0.78f, 1f, a));
        }
    }
}

/// <summary>字标两侧的装饰图标（剑 / 骷髅），随字标一起轻微浮动。</summary>
public partial class MenuEmblem : Control
{
    public string Kind = "sword";
    public Color Tint = Colors.White;
    public float Diameter = 47f;
    public float Spin;

    private float _t;

    public override void _Process(double delta)
    {
        _t += (float)delta;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var bob = Mathf.Sin(_t / 2000f * Mathf.Tau) * 8f;
        DrawSetTransform(Size * 0.5f + new Vector2(0, bob), Spin, Vector2.One);
        IconArt.Draw(this, Kind, Diameter, Tint);
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }
}

/// <summary>标题下的金色短横线：中间一段更亮，两端淡下去（原版是两段圆角矩形叠的）。</summary>
public partial class Underline : Control
{
    public float Width = 200f;

    public Underline()
    {
        // 容器只给尺寸，不落墨；自绘控件必须自己声明「我要画」，
        // 上一版没写，_Draw 一次都没被调过——线上根本没这根线。
        MouseFilter = MouseFilterEnum.Ignore;
    }

    private bool _shown;

    public override void _Process(double delta)
    {
        // 容器布局要几帧才定尺寸，首帧就画会画出 0 宽的线。
        // 这里只在「有尺寸了」的时候补一次重绘，之后不再每帧跑。
        if (_shown || Size.X < 2f) return;
        _shown = true;
        QueueRedraw();
    }

    /// <summary>
    /// 线的几何：纯函数，UiSmoke 直接断言「宽度就是 Wordmark 的 62%，且不出界」。
    /// 上一版这条线一次都没画出来，而没有任何断言能发现——因为「画了条线」这件事
    /// 只有看得见的人知道。把几何拆出来之后，它至少能被算一遍。
    /// </summary>
    public static (float Left, float Y, float Width) Geometry(float availableWidth, float rowHeight, float width) =>
        (Mathf.Max(0f, (availableWidth - width) / 2f), rowHeight * 0.5f, width);

    public override void _Draw()
    {
        var (a, y, w) = Geometry(Size.X <= 1f ? Width : Size.X, Mathf.Max(Size.Y, CustomMinimumSize.Y), Width);
        Paint.RoundRect(this, new Rect2(a, y - 1.5f, w, 3f), 1.5f, Paint.Alpha(Palette.Gold, 0.85f));
        Paint.RoundRect(this, new Rect2(a + w * 0.34f, y - 1.5f, w * 0.32f, 3f), 1.5f, new Color(1, 1, 1, 0.35f));
    }
}
