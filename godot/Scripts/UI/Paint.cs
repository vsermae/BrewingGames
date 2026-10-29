// 矢量绘制的公共笔画：形状拼装、描边、光晕。
//
// TS 版全部图形都是 Phaser Graphics 现画（刻意不引入贴图，保证断网可玩、体积为零）。
// Godot 的正解也是 CanvasItem._Draw —— 同样是「现画」，而且是原生的，不用伪造贴图。
// 这里只放最底层的形状工具，具体造型在同目录的 EnemyArt / HeroArt / IconArt 里。
namespace AbyssEcho.UI;

using Godot;

public static class Paint
{
    public static Color Darken(Color c, float amount) => c.Darkened(amount);
    public static Color Lighten(Color c, float amount) => c.Lightened(amount);

    /// <summary>按 alpha 独立的半透明色：Godot 的 Color 带 a 通道，直接算出来给 Draw*</summary>
    public static Color Alpha(Color c, float a) => new(c.R, c.G, c.B, a);

    public static void Poly(CanvasItem ci, Vector2[] pts, Color color)
    {
        if (pts.Length < 3) return;
        ci.DrawColoredPolygon(pts, color);
    }

    public static void Outline(CanvasItem ci, Vector2[] pts, Color color, float width = 1.5f)
    {
        if (pts.Length < 2) return;
        var loop = new Vector2[pts.Length + 1];
        Array.Copy(pts, loop, pts.Length);
        loop[^1] = pts[0];
        ci.DrawPolyline(loop, color, width, true);
    }

    // `DrawRect(rect, color, filled, width)` 的第三个参数是 `filled`。全仓 17 处 `DrawRect`
    // 一律靠默认值吃 filled=true，**一处描边都没有**——`RectOutline` 那条是唯一的例外，
    // 显式传 false + width。所以这里不要自创第四个参数：Godot 没有「filled 放第三个」的版本。
    public static void Rect(CanvasItem ci, Rect2 r, Color color) => ci.DrawRect(r, color, true);
    public static void RectOutline(CanvasItem ci, Rect2 r, Color color, float width = 1.5f) =>
        ci.DrawRect(r, color, false, width);

    /// <summary>圆角矩形：Godot 没有现成接口，用两段矩形 + 四个扇形拼，够用且不依赖 StyleBox</summary>
    public static void RoundRect(CanvasItem ci, Rect2 r, float radius, Color color) =>
        RoundRect(ci, r, radius, radius, radius, radius, color);

    /// <summary>
    /// 四角可分别指定半径的圆角矩形。
    /// 卡片上的「纵向渐变」是切成若干条横带画的，首尾两条要跟着卡片的圆角切一刀，
    /// 否则四个角会露成直角。全零等于直边矩形。
    /// </summary>
    public static void RoundRect(CanvasItem ci, Rect2 r, float tl, float tr, float br, float bl, Color color)
    {
        var half = Mathf.Min(r.Size.X, r.Size.Y) * 0.5f;
        var a = Mathf.Min(tl, half);
        var b = Mathf.Min(tr, half);
        var c = Mathf.Min(br, half);
        var d = Mathf.Min(bl, half);
        if (Mathf.Max(Mathf.Max(a, b), Mathf.Max(c, d)) <= 0.5f)
        {
            ci.DrawRect(r, color, true);
            return;
        }

        // 三块矩形（上带 / 中带 / 下带）铺满，四个角靠扇形补上：
        // 上带只占「左右圆角内缩后」的宽度，中带铺到最左最右，下带同上级
        var p = r.Position;
        var w = r.Size.X;
        var h = r.Size.Y;
        var topK = Mathf.Max(a, b);
        var botK = Mathf.Max(c, d);
        var leftK = Mathf.Max(a, d);
        var rightK = Mathf.Max(b, c);

        if (h - topK - botK > 0f)
            ci.DrawRect(new Rect2(p.X + leftK, p.Y + topK, w - leftK - rightK, h - topK - botK), color, true);
        if (topK > 0f)
            ci.DrawRect(new Rect2(p.X + a, p.Y + a, w - a - b, topK - a), color, true);
        if (botK > 0f)
            ci.DrawRect(new Rect2(p.X + d, p.Y + h - botK, w - d - c, botK - c), color, true);

        // 四角补圆：以角的圆心画整圆，超出部分被卡片外的同色内容盖住看不到，
        // 但半圆会漏到卡片外，所以只画落在卡片内的那一段扇形。
        Sector(ci, new Vector2(p.X + a, p.Y + a), a, Mathf.Pi, Mathf.Pi * 1.5f, color);
        Sector(ci, new Vector2(p.X + w - b, p.Y + b), b, Mathf.Pi * 1.5f, Mathf.Tau, color);
        Sector(ci, new Vector2(p.X + w - c, p.Y + h - c), c, 0f, Mathf.Pi * 0.5f, color);
        Sector(ci, new Vector2(p.X + d, p.Y + h - d), d, Mathf.Pi * 0.5f, Mathf.Pi, color);
    }

    /// <summary>扇形填充：圆角矩形的角。八个采样点在小半径下已经看不出棱角</summary>
    /// <summary>圆环：外半径与内半径之间那一圈。没有它就只能画实心圆再拿底色盖，
    /// 而底色在深色格子上盖不干净，会留下一枚浅色圆盘。</summary>
    public static void Ring(CanvasItem ci, Vector2 c, float outer, float inner, Color color, int seg = 32)
    {
        if (outer <= 1f) return;
        seg = Math.Max(8, seg);
        var pts = new Vector2[seg * 2];
        for (var i = 0; i < seg; i++)
        {
            var a = Mathf.Tau * i / seg;
            pts[i] = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * outer;
            pts[seg * 2 - 1 - i] = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * Mathf.Max(0f, inner);
        }
        ci.DrawColoredPolygon(pts, color);
    }

    /// <summary>
    /// 扇形填充：圆角矩形的角要用它（角是一个实心扇区，不是一条线）。
    /// 和 Arc 分开命名是这次踩坑的教训：一个函数名底下两种语义，
    /// 调用方永远猜不到自己会拿到实心饼还是一条弧。
    /// </summary>
    public static void Sector(CanvasItem ci, Vector2 center, float radius, float from, float to, Color color, int seg = 10)
    {
        if (radius <= 0.5f) return;
        var pts = new Vector2[seg + 2];
        pts[0] = center;
        for (var i = 0; i <= seg; i++)
        {
            var ang = Mathf.Lerp(from, to, (float)i / seg);
            pts[i + 1] = center + new Vector2(Mathf.Cos(ang) * radius, Mathf.Sin(ang) * radius);
        }
        ci.DrawColoredPolygon(pts, color);
    }

    /// <summary>
    /// 圆弧「描边」，不是扇形。
    /// 上一版把圆心塞进顶点表的第一个位置，画出来是一块实心饼——
    /// 房间徽章上那道「上缘挑亮」因此变成扣在图标头上的暗帽子，
    /// 因为它同时还用了半透明色，整块半圆直接把图形盖住了。
    /// 真正的弧用 DrawArc：只描一条线，不填内部。
    /// </summary>
    public static void Arc(CanvasItem ci, Vector2 center, float radius, float from, float to, Color color, int seg = 10)
    {
        if (radius <= 0.5f) return;
        ci.DrawArc(center, radius, from, to, Math.Max(2, seg), color, 1.5f, true);
    }

    /// <summary>圆角描边：与 RoundRect 同一套半径算法，只画边</summary>
    public static void RoundRectOutline(CanvasItem ci, Rect2 r, float radius, Color color, float width = 1.5f)
    {
        var k = Mathf.Min(radius, Mathf.Min(r.Size.X, r.Size.Y) * 0.5f);
        var pts = new List<Vector2>();
        void Arc(Vector2 c, float from, float to)
        {
            const int seg = 8;
            for (var i = 0; i <= seg; i++)
            {
                var a = from + (to - from) * i / seg;
                pts.Add(c + new Vector2(Mathf.Cos(a) * k, Mathf.Sin(a) * k));
            }
        }
        var p = r.Position;
        var sz = r.Size;
        Arc(new Vector2(p.X + k, p.Y + k), Mathf.Pi, Mathf.Pi * 1.5f);
        Arc(new Vector2(p.X + sz.X - k, p.Y + k), Mathf.Pi * 1.5f, Mathf.Tau);
        Arc(new Vector2(p.X + sz.X - k, p.Y + sz.Y - k), 0f, Mathf.Pi * 0.5f);
        Arc(new Vector2(p.X + k, p.Y + sz.Y - k), Mathf.Pi * 0.5f, Mathf.Pi);
        pts.Add(pts[0]);
        ci.DrawPolyline(pts.ToArray(), color, width, true);
    }

    public static void Circle(CanvasItem ci, Vector2 c, float radius, Color color) =>
        ci.DrawCircle(c, radius, color);

    public static void Ring(CanvasItem ci, Vector2 c, float radius, Color color, float width = 1.5f) =>
        ci.DrawArc(c, radius, 0, Mathf.Tau, 48, color, width, true);

    /// <summary>椭圆：先按单位圆取点再缩放，避免 DrawCircle 只有正圆</summary>
    public static void Ellipse(CanvasItem ci, Vector2 c, float rx, float ry, Color color, int seg = 32)
    {
        var pts = new Vector2[seg];
        for (var i = 0; i < seg; i++)
        {
            var a = Mathf.Tau * i / seg;
            pts[i] = c + new Vector2(Mathf.Cos(a) * rx, Mathf.Sin(a) * ry);
        }
        ci.DrawColoredPolygon(pts, color);
    }

    public static void Line(CanvasItem ci, Vector2 a, Vector2 b, Color color, float width = 1.5f) =>
        ci.DrawLine(a, b, color, width, true);

    /// <summary>三角：箭头、闪电、尖角都用它</summary>
    public static void Tri(CanvasItem ci, Vector2 a, Vector2 b, Vector2 c, Color color) =>
        ci.DrawColoredPolygon(new[] { a, b, c }, color);

    /// <summary>星形（勋章、闪光）</summary>
    public static void Star(CanvasItem ci, Vector2 c, int points, float outer, float inner, Color color, float rot = 0)
    {
        var pts = new Vector2[points * 2];
        for (var i = 0; i < pts.Length; i++)
        {
            var r = i % 2 == 0 ? outer : inner;
            var a = rot + Mathf.Pi * i / points;
            pts[i] = c + new Vector2(Mathf.Cos(a) * r, Mathf.Sin(a) * r);
        }
        ci.DrawColoredPolygon(pts, color);
    }

    /// <summary>径向光晕：叠几层同心圆制造「发光」观感（无 shader，纯几何）</summary>
    public static void Glow(CanvasItem ci, Vector2 c, float radius, Color color, float alpha = 0.16f, int layers = 4) =>
        Glow(ci, c, radius, radius * 0.38f, color, alpha, layers);

    /// <summary>
    /// 可指定纵横比的同心光晕。
    /// 背景里那团「辉光」以前是横跨 90% 屏宽的大椭圆，硬边在纯色底上直接显形，
    /// 像贴了一张灰纸；改成多层、一层比一层淡、同时一层比一层窄，边才是软的。
    /// 这里用 `MathF.Pow` 把亮度曲线压成前几层暗、后几层几乎看不见的衰减，
    /// 简单的均匀分摊出来是「一圈圈能数出来」的年轮。
    /// </summary>
    public static void Glow(CanvasItem ci, Vector2 c, float rx, float ry, Color color, float alpha = 0.16f, int layers = 6)
    {
        for (var i = layers; i >= 1; i--)
        {
            var t = (float)i / layers;
            var a = alpha * Mathf.Pow(1f - t + 0.28f, 1.8f);
            Ellipse(ci, c, rx * t, ry * t, Alpha(color, a), 40);
        }
    }

    /// <summary>
    /// 椭圆形的软衰减：**从中心往外淡到 0**，最外一圈的边就是 0 透明度。
    ///
    /// 为什么必须有它：`Glow` 是一摞同心椭圆，最外那一层的边缘就是一条色阶可见的
    /// 硬边（Boss 出场那屏整屏一圈圈的环、敌人卡的底光、舞台灯，全是同一件事）。
    ///
    /// **这个函数改过三稿，前两稿都被截图否掉了，值得记下来：**
    ///   一、顶点色插值（中心亮、顶点全透明）：顶点色是分片线性的，一圈十几个顶点时
    ///       亮的那颗心显成一块多边形——放大是「饼图上切出来的几片」。
    ///   二、同心环、每环不超过 3px：环内一档颜色、环间按 `1-t²` 衰减。理论上够，
    ///       实测中心 100px 内仍是三四道能数出来的台阶——因为**每环都要覆盖一次**
    ///       （环 r 盖住 r-1），而 sRGB 下的低亮度区里 1/255 就是一道可见的边。
    ///       「多画几层」只是把台阶磨细，不是消掉它。
    /// 现在走引擎自己的渐变采样：`GradientTexture2D` 的 FillRadial，**一次把它画完**。
    /// 这不是贴图资源——它在这个函数里按半径现建、画完就丢，包里依然什么都没有。
    ///
    /// `rings` 只影响坡的采样精度（沿半径分几档），不影响外边界。
    /// </summary>
    /// <param name="tint">光的颜色（alpha 取 `tint.A × alpha`）</param>
    public static void SoftEllipse(CanvasItem ci, Vector2 c, float rx, float ry, Color tint, float alpha, int rings = 0)
    {
        if (alpha <= 0.002f || rx <= 0.5f || ry <= 0.5f) return;
        var n = rings > 0 ? rings : 24;
        var grad = new Gradient();
        var colors = new Color[Math.Max(3, n)];
        var offsets = new float[colors.Length];
        for (var i = 0; i < colors.Length; i++)
        {
            var t = (float)i / (colors.Length - 1);
            // `1 - t²`：中心附近平、边缘陡，读起来像「一盏灯」而不是「两个套着的圆」
            offsets[i] = t;
            colors[i] = Alpha(tint, tint.A * alpha * (1f - t * t));
        }
        grad.Colors = colors;
        grad.Offsets = offsets;

        // 纹理按**目标尺寸**建，不是写死 128×128。
        //
        // 写死 128 的那一版有两个问题，都是同一件事的两面：
        //   一、这次真正在地图上看到的：软衰减一铺就是几百像素宽，128 采样再放大 4 倍，
        //       屏幕上出现的是**一个个菱形色块**（线性过滤放大的马赛克）。
        //       `05-panel.png` 里弹窗背后那一屏「127 灰的棋盘」就是舞台的两道侧光被放大后的样子。
        //   二、缩小时反向：18px 宽的取景框把 128 纹理压到十几个像素，
        //       采到的是最外圈之外——屏上是一条纯白竖线，而代码里没有任何一处是白的。
        //
        // 所以宽度跟目标走，封顶 512（再大的渐变人眼看不出差别，白白占显存）。
        var px = Mathf.Clamp((int)(rx * 2f), 32, 512);
        var py = Mathf.Clamp((int)(ry * 2f), 32, 512);
        // FillRadial + fill_from/fill_to：0 在圆心、1 在半径处。半径取 1，
        // 纹理按椭圆的**包围盒**铺，于是同一个圆在横竖两边各自拉成 rx / ry。
        var tex = new GradientTexture2D
        {
            Gradient = grad,
            Width = px,
            Height = py,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(0.5f, 0.5f) + new Vector2(0.5f, 0f),
        };
        ci.DrawTextureRect(tex, new Rect2(c - new Vector2(rx, ry), new Vector2(rx * 2f, ry * 2f)),
            false, new Color(1, 1, 1, 1));
    }

    /// <summary>
    /// 一块真正的「面板底」：圆角 + 纵向渐变 + 四角是真弧。
    ///
    /// 为什么不用 `VGradient` 的 corner 参数：它把矩形切成 n 条**横带**，圆角只加在
    /// 首尾两条上，中间那 n-2 条是直边——n 越大，四角越像「被磨了一下」，而不是一段弧。
    /// 面板这种大面积底一放大就露馅。
    ///
    /// 这里对每一条横带单独算它与四段圆弧的交点，画成一块「两端是弧的梯形」。
    /// 参数 `corner` 是圆角半径，超过最短边的一半就没有直边可收，按 0.9 倍封顶。
    /// </summary>
    public static void Surface(CanvasItem ci, Rect2 r, float corner, Color top, Color bottom, int bands = 14)
    {
        if (r.Size.Y <= 1f || r.Size.X <= 1f) return;
        bands = Math.Max(2, bands);
        var rad = Mathf.Clamp(corner, 0f, Mathf.Min(r.Size.X, r.Size.Y) * 0.5f * 0.9f);
        var bandH = r.Size.Y / bands;
        var x0 = r.Position.X;
        var x1 = r.Position.X + r.Size.X;
        for (var i = 0; i < bands; i++)
        {
            var t = i / (float)(bands - 1);
            var col = top.Lerp(bottom, t);
            var y = r.Position.Y + i * bandH;
            var h = bandH + 1f;
            if (rad <= 0.01f)
            {
                ci.DrawRect(new Rect2(x0, y, x1 - x0, h), col);
                continue;
            }
            var topInset = CornerInset(y - r.Position.Y, r.Size.Y, rad);
            var botInset = CornerInset(y + h - r.Position.Y, r.Size.Y, rad);
            var pts = new[]
            {
                new Vector2(x0 + topInset, y),
                new Vector2(x1 - topInset, y),
                new Vector2(x1 - botInset, y + h),
                new Vector2(x0 + botInset, y + h),
            };
            ci.DrawColoredPolygon(pts, col);
        }
    }

    /// <summary>距上沿 `d` 处，半径 `rad` 的圆角让这条边往里收多少</summary>
    private static float CornerInset(float d, float total, float rad)
    {
        var fromTop = Mathf.Max(0f, rad - d);
        var fromBottom = Mathf.Max(0f, d - (total - rad));
        var over = Mathf.Max(fromTop, fromBottom);
        if (over <= 0f) return 0f;
        var dy = rad - over;
        return rad - Mathf.Sqrt(Mathf.Max(0f, rad * rad - dy * dy));
    }

    /// <summary>
    /// 圆形软衰减（`SoftEllipse` 的正圆版）：地面投影、暗角、落点光斑都用它。
    /// </summary>
    public static void SoftDot(CanvasItem ci, Vector2 c, float r, Color tint, float alpha, int rings = 8) =>
        SoftEllipse(ci, c, r, r, tint, alpha, rings);

    // ---- 材质：让「一片纯色」变成「一块石头」的几笔 ----

    /// <summary>
    /// 纵向渐变带：切成 n 条横带，每条多画 1px 防接缝。
    ///
    /// 为什么不用 GradientTexture2D：面板尺寸随布局变，纹理得跟着重建，
    /// 而这条路径只吃两个颜色和一个 rect，调用方不用管资源生命周期。
    /// corner 只作用在首尾两条带上，四角要与面板一致时传面板的圆角半径。
    /// </summary>
    public static void VGradient(CanvasItem ci, Rect2 r, Color top, Color bottom, int bands = 12, float corner = 0f)
    {
        if (r.Size.Y <= 0.5f || r.Size.X <= 0.5f) return;
        bands = Math.Max(1, bands);
        var bandH = r.Size.Y / bands;
        for (var i = 0; i < bands; i++)
        {
            var t = bands == 1 ? 0f : i / (float)(bands - 1);
            var col = top.Lerp(bottom, t);
            var topR = i == 0 ? corner : 0f;
            var botR = i == bands - 1 ? corner : 0f;
            var br = new Rect2(r.Position.X, r.Position.Y + i * bandH, r.Size.X, bandH + 1f);
            if (topR > 0f || botR > 0f) RoundRect(ci, br, topR, topR, botR, botR, col);
            else ci.DrawRect(br, col, true);
        }
    }

    /// <summary>
    /// 斜向细纹：一层 45° 的极淡条纹，用来打散大块纯色。
    ///
    /// 深色面板最怕「一整块同色」——玩家看不到边界，只看到一片灰。
    /// gap 给到 7~9、alpha 给到 0.02~0.04 就够：能感觉到有东西，
    /// 但盯着看数不出条纹，这才是材质而不是图案。
    /// </summary>
    public static void Hatches(CanvasItem ci, Rect2 r, Color color, float gap = 7f, float width = 1f)
    {
        if (r.Size.Y <= 1f || gap <= 1f) return;
        // 从矩形左上角外沿起笔、到右下角外沿收笔：45° 线要盖满整个矩形
        var start = r.Position.X - r.Size.Y;
        for (var x = start; x < r.Position.X + r.Size.X; x += gap)
            ci.DrawLine(new Vector2(x, r.Position.Y + r.Size.Y), new Vector2(x + r.Size.Y, r.Position.Y), color, width, true);
    }

    /// <summary>石点：位置由固定公式推出来，不吃主随机序列（背景不该影响玩法）</summary>
    public static void Speckle(CanvasItem ci, Rect2 r, Color color, int count = 60, float maxR = 1.4f)
    {
        for (var i = 0; i < count; i++)
        {
            var fx = (i * 0.6180339887f) % 1f;
            var fy = (i * 0.7548776662f + 0.31f) % 1f;
            var rad = maxR * (0.35f + ((i * 0.37f) % 1f) * 0.65f);
            ci.DrawCircle(new Vector2(r.Position.X + fx * r.Size.X, r.Position.Y + fy * r.Size.Y), rad, color);
        }
    }

    /// <summary>
    /// 切角矩形：既不是圆角也不是直角的那个形状。
    /// 卡面用切角而不是大圆角——圆角偏糖果，切角偏铁牌，这是「深渊」该有的硬度。
    /// k 是被切掉的直角边长。
    /// </summary>
    public static void BeveledRect(CanvasItem ci, Rect2 r, float k, Color color)
    {
        var p = r.Position;
        var w = r.Size.X;
        var h = r.Size.Y;
        k = Mathf.Clamp(k, 0f, Mathf.Min(w, h) * 0.5f);
        var pts = new[]
        {
            p + new Vector2(k, 0), p + new Vector2(w - k, 0), p + new Vector2(w, k),
            p + new Vector2(w, h - k), p + new Vector2(w - k, h), p + new Vector2(k, h),
            p + new Vector2(0, h - k), p + new Vector2(0, k),
        };
        ci.DrawColoredPolygon(pts, color);
    }

    /// <summary>切角描边：与 BeveledRect 同一套顶点，只走线</summary>
    public static void BeveledOutline(CanvasItem ci, Rect2 r, float k, Color color, float width = 1.5f)
    {
        var p = r.Position;
        var w = r.Size.X;
        var h = r.Size.Y;
        k = Mathf.Clamp(k, 0f, Mathf.Min(w, h) * 0.5f);
        var pts = new[]
        {
            p + new Vector2(k, 0), p + new Vector2(w - k, 0), p + new Vector2(w, k),
            p + new Vector2(w, h - k), p + new Vector2(w - k, h), p + new Vector2(k, h),
            p + new Vector2(0, h - k), p + new Vector2(0, k),
            p + new Vector2(k, 0),
        };
        ci.DrawPolyline(pts, color, width, true);
    }
}

/// <summary>
/// 房间格里的符号。房间类型决定造型（IconArt.ForRoom），颜色由地图给。
/// 单独一个节点是为了让地图不必「一个房间一个 Label + 一张字形表」。
/// </summary>
public partial class RoomIcon : Control
{
    public string Kind = "flag";
    public Color Tint = Colors.White;

    public RoomIcon()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        var c = Size * 0.5f;
        var box = Mathf.Min(Size.X, Size.Y);
        var d = box * 0.5f;

        // 地砖：这里不画格子底板——底板在 Button 的 StyleBox 上（它还要负责悬停态与点击区），
        // 再叠一层就会把格子糊灰。这一层只管「地砖上的光」。
        //
        // 徽章底：外圈类型色晕 → 内圈压暗 → 上缘挑亮。
        // 有它之后，图标像「刻在牌子上的标记」，而不是悬在格子中间的一笔画。
        //
        // 「内圈压暗」这一层以前是实心圆，于是每个房间格上半截都扣着一块暗色饼，
        // 看着像给图标戴了帽子。压暗应该落在图标背后一小片，不该盖住格子——
        // 半径收到 0.42 且几乎透明，只起「把图形从底色里托出来」的作用。
        // 色晕只留一圈「环」，不留实心圆：格子本身是方的、有圆角，
        // 里面再垫一个实心圆就会在深色底上显出一枚浅灰圆盘，
        // 看着像格子中间被人放了个盘子，而不是「地砖上的光」。
        Paint.Ring(this, c, d * 0.96f, d * 0.86f, Paint.Alpha(Tint, 0.16f), 36);
        Paint.Arc(this, c, d * 0.9f, Mathf.Pi * 1.1f, Mathf.Pi * 1.9f,
            Paint.Alpha(Colors.White, 0.14f), 14);

        DrawSetTransform(c, 0, Vector2.One);
        IconArt.Draw(this, Kind, box * 0.58f, Tint);
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }
}

/// <summary>
/// 敌人卡右端的那一格：**取景窗**，不是立绘位。
///
/// 立绘本身不装在这里（它的尺寸由舞台高度决定，装进一条空框里会被压成图钉），
/// 所以这一格只负责把「位」画出来：凹进去的底衬 + 一圈 accent 描边 + 从框底打上来的光。
///
/// 它曾经是卡**左边**一条 132px 宽的竖栏，而 132 是 TS 版「卡里装立绘」那一栏的宽度。
/// 卡里没有立绘之后，那一条就成了纯空白：截图上是「卡左边一块灰白的方板」，
/// 而屏幕上方读作「信封卡的上半截是白的」。现在它挪到卡右端、收窄到 64px，
/// 因为它指的是**舞台左边那一只**——取景窗摆在与被拍对象同一侧。
/// </summary>
public partial class EnemySocket : Control
{
    public Color Accent = Colors.White;

    /// <summary>Boss 房给这一框更集中的光</summary>
    public bool IsBoss;

    /// <summary>
    /// 槽里画谁。**永远是 null**，而且不该再被填上：这块框在第四十八轮从
    /// 「卡里画一只小怪」改成「指向舞台上那一只的取景框」。同一屏里再画一遍同一只怪，
    /// 只是把「它是谁」说了第二遍，而且小到认不出——读者只会问「这只怪身边带了个谁」。
    /// 留着这个字段是为了让「谁把它填回来」这件事在代码里看得见。
    /// </summary>
    public string? EnemyKind;

    /// <summary>
    /// 取景框那道底光的圆心在框高的哪个比例处（框底 = 0.8）。
    /// 单拎出来是给判据用的：「这一框是从底部打上来的光」和「框中间照着一张脸」
    /// 在画面上都只是一团亮色，只有问过的人才知道是哪一种。
    /// </summary>
    public float LightY => LightRatio;

    private const float LightRatio = 0.80f;

    /// <summary>
    /// 这一格画多宽：64。
    ///
    /// **不是 132**——那是 TS 版「卡片里装立绘」那一栏的宽度。卡里没有立绘之后，
    /// 132 宽的空框叠在一条 118 高的卡里，屏上就是「卡左边一块灰白的方板」，
    /// 读者读作「这里漏画了东西」。宽度由这一处说了算，卡片与冒烟读同一个数。
    /// </summary>
    public const float Width = 64f;

    public EnemySocket()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        if (Size.X < 8f || Size.Y < 8f) return;
        var r = new Rect2(Vector2.Zero, Size);
        var boss = IsBoss;
        // 这一格是**一条 64px 宽的竖条**（`Width`），不是卡左边那条 132 宽的空栏：
        // 底衬与描边的圆角都跟着窄下来（10 → 6），否则 64 宽里吃两只 10px 的角，
        // 剩下的直边不足 40px，框读起来像一个圆药丸而不是取景窗。
        // 底衬：比卡面**深**一层，读作「凹进去的一格」，不是一块亮板。
        Paint.RoundRect(this, r, 6f, new Color(0, 0, 0, 0.26f));
        Paint.RoundRectOutline(this, r, 6f, Paint.Alpha(Accent, boss ? 0.42f : 0.26f), 1.2f);
        // 内描边：给这一格一点厚度
        Paint.RoundRectOutline(this, new Rect2(3, 3, Size.X - 6, Size.Y - 6), 4.5f,
            new Color(1, 1, 1, 0.035f), 1f);
        // 四角：这一格的角色是「瞄准 / 取景」，不是「装立绘」
        var aim = Paint.Alpha(Accent, boss ? 0.6f : 0.4f);
        const float corner = 10f;
        foreach (var (cx, cy, dx, dy) in new[]
                 {
                     (7f, 7f, 1f, 1f), (Size.X - 7f, 7f, -1f, 1f),
                     (7f, Size.Y - 7f, 1f, -1f), (Size.X - 7f, Size.Y - 7f, -1f, -1f),
                 })
        {
            Paint.Line(this, new Vector2(cx, cy), new Vector2(cx + dx * corner, cy), aim, 1.4f);
            Paint.Line(this, new Vector2(cx, cy), new Vector2(cx, cy + dy * corner), aim, 1.4f);
        }
        // 光从框底打上来：它站在屏幕下方，于是这一格里越往下越亮。
        // 一次画完的软衰减（不是一摞同心椭圆），外边界透明度 0——没有能数出来的环。
        var floorY = Size.Y * LightRatio;
        Paint.SoftEllipse(this, new Vector2(Size.X * 0.5f, floorY + Size.Y * 0.04f),
            Size.X * 0.46f, Size.Y * 0.34f, Accent, boss ? 0.30f : 0.18f, 8);
        // 框底的一道光：读作「它站在框下面那块地上」。
        //
        // 这里原来还跟着一块 0.34 的纯黑椭圆当「贴地暗影」。卡面改成深色之后，
        // 一块纯黑把框底的渐变吃穿，在条带下半截留下一块比周围更黑、读起来像**白**的空洞。
        // 暗影要的是「地面暗下去」，不是「涂一块黑」：一块 64 宽的框里没有地面可暗，
        // 于是这一版只剩下面那道光——它在窄框里就是「指向」这件事本身。
        Paint.Rect(this, new Rect2(5, floorY, Size.X - 10, 1.6f), Paint.Alpha(Accent, boss ? 0.75f : 0.5f));
    }
}

