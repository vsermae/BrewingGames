// 矢量图标的绘制笔：把 Phaser Graphics 的 API 一一对应到 Godot 的 Draw*。
//
// 为什么需要这一层，而不是像上一版那样「每个图标手写一堆 V()/Poly()/Rect() 局部函数」：
// 原版 src/ui/icons.ts 是 1074 行 Graphics 手绘路径，用的是 fillStyle / lineStyle /
// save-scale 这套状态机 API。上一版把这些调用逐条手翻成显式缩放坐标，
// 结果就是**每一笔都在和坐标系搏斗**，谁也写不出「鳞片」「裂甲」这种需要几十笔的细节。
//
// 所以先补一层笔：坐标沿用原版的设计框（原点居中、基准 32），
// 缩放在这里一次性收口；颜色/描边宽度也像 Phaser 一样有「当前样式」。
// 有了它，原版的 case 分支就能逐行照抄——美术密度差的就是这一步。
namespace AbyssEcho.UI;

using Godot;

public sealed class Painter
{
    private readonly CanvasItem _ci;
    private readonly float _s;
    private Color _fill = Colors.White;
    private float _alpha = 1f;
    private Color _stroke = Colors.White;
    private float _strokeWidth;
    private float _strokeAlpha = 1f;

    private readonly ShapeProbe? _probe;

    public Painter(CanvasItem ci, float scale)
    {
        _ci = ci;
        _s = scale <= 0f ? 1f : scale;
    }

    /// <summary>记账模式：不落墨，只数笔画。参数是设计框下的缩放，仍按 size/32 走</summary>
    public Painter(ShapeProbe probe, float scale)
    {
        _ci = null!;
        _probe = probe;
        _s = scale <= 0f ? 1f : scale;
    }

    public float Scale => _s;

    /// <summary>记账板。null 表示这是真画布，正常落墨</summary>
    public ShapeProbe? Probe => _probe;

    private void Circle_(Vector2 c, float r, Color color)
    {
        if (_probe is not null)
        {
            _probe.Count(c + new Vector2(r, 0));
            _probe.Count(c - new Vector2(r, 0));
            return;
        }
        _ci.DrawCircle(c, r, color);
    }

    private void Poly_(Vector2[] pts, Color color)
    {
        if (_probe is not null)
        {
            foreach (var pt in pts) _probe.Count(pt);
            return;
        }
        _ci.DrawColoredPolygon(pts, color);
    }

    private void Arc_(Vector2 c, float r, float from, float to, int seg, Color color, float width)
    {
        if (_probe is not null)
        {
            _probe.Count(c + new Vector2(Mathf.Cos(from) * r, Mathf.Sin(from) * r));
            _probe.Count(c + new Vector2(Mathf.Cos(to) * r, Mathf.Sin(to) * r));
            return;
        }
        _ci.DrawArc(c, r, from, to, seg, color, width, true);
    }

    private void Line_(Vector2 a, Vector2 b, Color color, float width)
    {
        if (_probe is not null)
        {
            _probe.Count(a);
            _probe.Count(b);
            return;
        }
        _ci.DrawLine(a, b, color, width, true);
    }

    private void Polyline_(Vector2[] pts, Color color, float width)
    {
        if (_probe is not null)
        {
            foreach (var pt in pts) _probe.Count(pt);
            return;
        }
        _ci.DrawPolyline(pts, color, width, true);
    }

    // ---- 样式状态：和 Phaser Graphics 一样，「设定一次，之后每一笔都用它」----

    public void Fill(Color color, float alpha = 1f)
    {
        _fill = color;
        _alpha = alpha;
    }

    public void Line(float width, Color color, float alpha = 1f)
    {
        _strokeWidth = width;
        _stroke = color;
        _strokeAlpha = alpha;
    }

    private Vector2 P(float x, float y) => new(x * _s, y * _s);

    private Vector2[] Pts(params (float X, float Y)[] pts)
    {
        var arr = new Vector2[pts.Length];
        for (var i = 0; i < pts.Length; i++) arr[i] = P(pts[i].X, pts[i].Y);
        return arr;
    }

    private Color FillColor => new(_fill.R, _fill.G, _fill.B, _alpha);

    private Color StrokeColor => new(_stroke.R, _stroke.G, _stroke.B, _strokeAlpha);

    // ---- 填充 ----

    public void FillCircle(float x, float y, float r)
    {
        if (_probe is not null)
        {
            // 四个极点，不是一个圆心：记账笔要的是**包围盒**。
            // 早先只记圆心和 (x+r, y)，纵向范围全靠别人的笔画补，
            // 「这个图表实际占多大」就量不出来——而画多大正是要按包围盒反推的那件事。
            _probe.Count(P(x - r, y));
            _probe.Count(P(x + r, y));
            _probe.Count(P(x, y - r));
            _probe.Count(P(x, y + r));
            return;
        }
        Circle_(P(x, y), r * _s, FillColor);
    }

    public void FillEllipse(float x, float y, float rx, float ry)
    {
        var seg = 28;
        var pts = new Vector2[seg];
        for (var i = 0; i < seg; i++)
        {
            var a = Mathf.Tau * i / seg;
            pts[i] = P(x + Mathf.Cos(a) * rx, y + Mathf.Sin(a) * ry);
        }
        Poly_(pts, FillColor);
    }

    /// <summary>
    /// 记一个矩形。探针模式下记的是**四角**：Rect2 只有 Position / Size，
    /// 记 Position 与 Position+Size 两角也能框出同一个盒子，
    /// 但圆角矩形的四个圆角会落在外面——四角都记才不会把「盒子的边界」量小。
    /// </summary>
    private void Rect_(Rect2 rect, Color color, bool filled, float width)
    {
        if (_probe is not null)
        {
            _probe.Count(rect.Position);
            _probe.Count(rect.Position + new Vector2(rect.Size.X, 0));
            _probe.Count(rect.Position + new Vector2(0, rect.Size.Y));
            _probe.Count(rect.Position + rect.Size);
            return;
        }
        _ci.DrawRect(rect, color, filled, width);
    }

    public void FillRect(float x, float y, float w, float h) =>
        Rect_(new Rect2(P(x, y), new Vector2(w * _s, h * _s)), FillColor, true, -1f);

    public void FillRoundRect(float x, float y, float w, float h, float radius)
    {
        var k = Mathf.Min(radius, Mathf.Min(w, h) * 0.5f);
        var rect = new Rect2(P(x, y), new Vector2(w * _s, h * _s));
        if (k <= 0.4f)
        {
            Rect_(rect, FillColor, true, -1f);
            return;
        }
        var kr = k * _s;
        Rect_(new Rect2(rect.Position.X + kr, rect.Position.Y, rect.Size.X - 2 * kr, rect.Size.Y), FillColor, true, -1f);
        Rect_(new Rect2(rect.Position.X, rect.Position.Y + kr, kr, rect.Size.Y - 2 * kr), FillColor, true, -1f);
        Rect_(new Rect2(rect.Position.X + rect.Size.X - kr, rect.Position.Y + kr, kr, rect.Size.Y - 2 * kr), FillColor, true, -1f);
        Circle_(rect.Position + new Vector2(kr, kr), kr, FillColor);
        Circle_(rect.Position + new Vector2(rect.Size.X - kr, kr), kr, FillColor);
        Circle_(rect.Position + new Vector2(kr, rect.Size.Y - kr), kr, FillColor);
        Circle_(rect.Position + new Vector2(rect.Size.X - kr, rect.Size.Y - kr), kr, FillColor);
    }

    public void FillTriangle(float x1, float y1, float x2, float y2, float x3, float y3) =>
        Poly_(new[] { P(x1, y1), P(x2, y2), P(x3, y3) }, FillColor);

    public void FillPoints(params (float X, float Y)[] pts) => Poly_(Pts(pts), FillColor);

    public void FillStar(float cx, float cy, int points, float outer, float inner, float rotation = 0f)
    {
        var pts = new Vector2[points * 2];
        for (var i = 0; i < pts.Length; i++)
        {
            var r = i % 2 == 0 ? outer : inner;
            var a = rotation + Mathf.Pi * i / points;
            pts[i] = P(cx + Mathf.Cos(a) * r, cy + Mathf.Sin(a) * r);
        }
        Poly_(pts, FillColor);
    }

    /// <summary>菱形：能量环、冰晶、符眼都靠它，单列一个省得每次写四个点</summary>
    public void FillDiamond(float cx, float cy, float rx, float ry) =>
        FillPoints((cx, cy - ry), (cx + rx, cy), (cx, cy + ry), (cx - rx, cy));

    // ---- 描边 ----

    public void StrokeCircle(float x, float y, float r) =>
        Arc_(P(x, y), r * _s, 0, Mathf.Tau, 48, StrokeColor, _strokeWidth * _s);

    public void StrokeArc(float x, float y, float r, float from, float to)
    {
        // 段数跟着角度走：小弧别浪费，大弧别露棱
        var sweep = Mathf.Abs(to - from);
        var seg = Mathf.Clamp((int)(sweep * 14f) + 4, 6, 64);
        Arc_(P(x, y), r * _s, from, to, seg, StrokeColor, _strokeWidth * _s);
    }

    public void StrokeRect(float x, float y, float w, float h) =>
        Rect_(new Rect2(P(x, y), new Vector2(w * _s, h * _s)), StrokeColor, false, _strokeWidth * _s);

    public void StrokePoints(params (float X, float Y)[] pts) => StrokePolyline(pts, StrokeColor, _strokeWidth);

    /// <summary>圆角矩形描边：Godot 没有现成接口，四段弧自己拼</summary>
    public void StrokeRoundRect(float x, float y, float w, float h, float radius)
    {
        var k = Mathf.Min(radius, Mathf.Min(w, h) * 0.5f);
        if (k <= 0.4f)
        {
            StrokeRect(x, y, w, h);
            return;
        }
        Line(x + k, y, x + w - k, y);
        Line(x + w, y + k, x + w, y + h - k);
        Line(x + w - k, y + h, x + k, y + h);
        Line(x, y + h - k, x, y + k);
        StrokeArc(x + k, y + k, k, Mathf.Pi, Mathf.Pi * 1.5f);
        StrokeArc(x + w - k, y + k, k, Mathf.Pi * 1.5f, Mathf.Tau);
        StrokeArc(x + w - k, y + h - k, k, 0f, Mathf.Pi * 0.5f);
        StrokeArc(x + k, y + h - k, k, Mathf.Pi * 0.5f, Mathf.Pi);
    }

    public void StrokePolyline((float X, float Y)[] pts, Color color, float width)
    {
        if (pts.Length < 2) return;
        Polyline_(Pts(pts), color, width * _s);
    }

    public void Line(float x1, float y1, float x2, float y2) =>
        Line_(P(x1, y1), P(x2, y2), StrokeColor, _strokeWidth * _s);

    /// <summary>径向光晕：几层同心半透明圆，纯几何就能画出「发光」</summary>
    public void Glow(float x, float y, float r, Color color, float alpha = 0.22f, int layers = 3)
    {
        for (var i = layers; i >= 1; i--)
        {
            var t = (float)i / layers;
            Circle_(P(x, y), r * t * _s, new Color(color.R, color.G, color.B, alpha * (1f - t + 0.4f) / layers));
        }
    }

    // ---- 颜色工具：原版 darkenC/lightenC 是「按 0-255 加减」，语义和 Darkened 不完全等价 ----

    /// <summary>按 0-255 通道减法变暗，与 TS 版 darkenC 同义</summary>
    public static Color Darken(Color c, float amount)
    {
        var k = amount / 255f;
        return new Color(Mathf.Max(0f, c.R - k), Mathf.Max(0f, c.G - k), Mathf.Max(0f, c.B - k), c.A);
    }

    public static Color Lighten(Color c, float amount)
    {
        var k = amount / 255f;
        return new Color(Mathf.Min(1f, c.R + k), Mathf.Min(1f, c.G + k), Mathf.Min(1f, c.B + k), c.A);
    }
}
