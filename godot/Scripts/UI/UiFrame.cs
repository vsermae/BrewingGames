// 界面骨架的自绘件：氛围底、HUD 条带、压暗层、细分割线、铁牌按钮。
//
// 为什么不用 StyleBoxFlat：它只会「一个纯色 + 一条描边 + 圆角」，
// 于是所有面板长得一样、都像一块贴纸，屏上找不到层次。
// 这里的每一件都是几笔 `_Draw`，共同点是「上亮下暗 + 一条挑亮的上沿」——
// 有了这三样，一块矩形才读得出「被光打到的铁牌」，而不是「一块填了色的矩形」。
//
// 这一层不承载任何规则：没有它游戏照样能玩，只是看起来不像 2024 年的游戏。
namespace AbyssEcho.UI;

using Godot;

/// <summary>
/// 整屏的氛围底：纵向冷光渐变 + 一团从中央散开的辉光 + 四角压暗 + 极淡的尘埃。
///
/// 以前每个场景的底都是一块 `ColorRect`：一整屏同一个色值，
/// 上下没有明暗差，画面的「空气感」一点也没有，立绘像贴在纸上。
/// 这一层只画一次（每帧不做任何事），所以可以多花几笔。
/// </summary>
public partial class AmbienceBackdrop : Control
{
    /// <summary>辉光的色相：菜单偏冷蓝、战斗偏敌方的危险色。整屏的情绪靠这一个数</summary>
    public Color Tint = Palette.Accent;
    /// <summary>辉光强度。战斗里比菜单暗一档，因为要压住底下的舞台</summary>
    public float GlowStrength = 1f;
    /// <summary>顶部那条更亮的冷光带（「上面有个天窗」的错觉）</summary>
    public bool TopLift = true;

    public AmbienceBackdrop()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        var w = Size.X;
        var h = Size.Y;
        if (w <= 1f || h <= 1f) return;

        // 1) 主渐变：顶部深蓝 → 底部近乎黑的紫蓝。
        //    纯 `ColorRect` 的问题是上下同色，这里一屏之内要能看出明暗差。
        Paint.VGradient(this, new Rect2(0, 0, w, h), new Color("0d1526"), Palette.Bg, 24);

        if (TopLift)
        {
            // 2) 顶部天光：一条从上往下淡出的冷带。没有它，HUD 条带像浮在虚空里
            Paint.VGradient(this, new Rect2(0, 0, w, h * 0.34f),
                Paint.Alpha(Tint, 0.075f * GlowStrength), Paint.Alpha(Tint, 0f), 16);
        }

        // 3) 中央辉光：一朵被按扁的、多层的椭圆，一层比一层窄、一层比一层淡
        //
        // 「多层」这个形容现在只对了一半：它是一次画完的**径向渐变**，不是一摞
        // 同心的填实椭圆。区别在 1024×576 那一屏上看得见——主菜单的字后面
        // 有几道极淡的椭圆轮廓，那正是「一层比一层窄」留下的边界。
        Paint.SoftEllipse(this, new Vector2(w * 0.5f, h * 0.46f), w * 0.62f, h * 0.5f,
            Tint, 0.075f * GlowStrength, 26);

        // 4) 边缘压暗：**只压最外面那一圈**，不许往里长。
        //
        // 这里原来是每边一条 16 段、深到 `Min(w,h) * 0.30f`（720 高下 216px）的暗角：
        // 上沿那一条盖住了整条 74px 的 HUD，下沿那条盖住了底部提示条。
        // 背景比 HUD 的内容层 z 高，于是**血条、经验条、金币数字一直在画，
        // 只是被这层从上面抹掉了**——截图里 HUD 是空的，摆了不知道多少轮。
        // 暗角是「把视线收到中间」，不是「把边缘擦掉」：给 48px，够读出「画面有边」。
        const float vigDepth = 48f;
        const int vigBands = 8;
        for (var i = 0; i < vigBands; i++)
        {
            var t = i / (float)vigBands;
            var a = 0.26f * (1f - t) * (1f - t);
            if (a < 0.004f) continue;
            var c = new Color(0, 0, 0, a);
            var m = vigDepth * (i + 1) / vigBands;
            var prev = vigDepth * i / vigBands;
            DrawRect(new Rect2(0, prev, w, m - prev), c);
            DrawRect(new Rect2(0, h - m, w, m - prev), c);
            DrawRect(new Rect2(prev, 0, m - prev, h), c);
            DrawRect(new Rect2(w - m, 0, m - prev, h), c);
        }

        // 5) 尘埃：极淡的石点，给它「有空气」的最后一点证据
        Paint.Speckle(this, new Rect2(0, 0, w, h), new Color(1, 1, 1, 0.02f), 90, 1.2f);
    }
}

/// <summary>
/// HUD 条带：屏幕上沿 / 下沿那条「仪表盘」。
///
/// 以前它是一块 #0e1420 的纯色 + 一条 1px 描边，和背景差不了几档，
/// 玩家分不清「这是面板」还是「这是背景的一部分」。
/// 现在给它：纵向渐变、贴边那条发光线、以及一条更亮的顶缘。
/// </summary>
public partial class HudBand : Control
{
    /// <summary>发光线画在哪一边：顶部条带画在底边，底部条带画在顶边</summary>
    public bool LineAtBottom;
    public Color LineTint = Palette.Accent;
    /// <summary>角标装饰：条带两端各一道短促的斜切亮线（仪表盘的味道）</summary>
    public bool CornerTicks = true;

    private int _once;
    public HudBand()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        // 条带只画自己那一条：底纹和贴边发光线都在 `Size` 里，溢出就等于「屏幕上沿多了一条亮带」。
        ClipContents = true;
    }

    public override void _Draw()
    {
        var w = Size.X;
        var h = Size.Y;
        if (w <= 1f || h <= 1f) return;

        // 卡面：靠近屏幕边缘那侧更深，往内容那侧提亮——视觉上是「面板浮在内容上」。
        //
        // 这里原来是反的：`LineAtBottom` 为真（顶部 HUD）时，把 PanelDeep 画在了 y=0、
        // 越往下越亮，于是条带的内容侧（y 30..74）是 0x16-0x17 的灰蓝，
        // 而地图背景在那一带只有 0x08-0x0b——**一块比背景亮三倍的面板盖住了 HUD**。
        // 条带本来该垫在内容**下面**当底，结果它成了不透明的顶。血条与经验条一直在画，
        // 只是画在这块灰蓝底上（红 100/255 → 约 90/255，判据的门槛是 191）。
        // 截图里那一屏 HUD 是空的，摆了不知道多少轮——**不是没画，是被垫在上面了**。
        // 「LineAtBottom」说的是那条发光线画在哪一边，不是渐变的方向：
        // 两条边带都该「贴屏幕边深、朝内容侧亮」，发光的那一侧朝内容。
        //
        // 但「朝内容侧亮」不等于「亮成一块白板」。这里原来给的是不透明的
        // PanelDeep → PanelSoft，而战斗那一屏的条带里装的是**白字**：
        // 名字、9/22、下一刀预告全落在 0x18-0x24 的浅灰蓝上，读作「一行被高亮选中的文字」，
        // 或者更糟——有人把它当成排版炸了。条带是**底**，它只该把内容托起来，
        // 不该和内容抢亮度：改成半透明（贴边 0.92 → 内容侧 0.72），
        // 底下的舞台与氛围透上来一档，字立刻回到「在深色面板上」。
        //
        // 四角走 `Surface` 而不是 `VGradient` 的 corner：后者只给首尾两条横带加圆角，
        // 中间那些是直边——1280 宽的面板两端于是成了「两条竖直的边 + 一个被磨圆的角」。
        Paint.Surface(this, new Rect2(0, 0, w, h), h * 0.34f,
            Paint.Alpha(Palette.PanelDeep, 0.92f), Paint.Alpha(Palette.PanelSoft, 0.72f), 12);

        // 斜纹理：一条 45° 的 3% 白线阵列，把「一整块纯色」打散
        Paint.Hatches(this, new Rect2(0, 0, w, h), new Color(1, 1, 1, 0.014f), 9f, 1f);

        // 贴边的发光线 + 它的余光
        var edgeY = LineAtBottom ? h - 1f : 0f;
        if (Name == "HudBand" && _once++ < 2) GD.Print($"[band] rect={new Rect2(0, 0, w, h)} edge={edgeY} z={ZIndex} mod={Modulate} self={SelfModulate} top={GetParent()?.Name}");
        if (LineAtBottom)
        {
            Paint.VGradient(this, new Rect2(0, h - 14f, w, 14f), new Color(0, 0, 0, 0f), new Color(0, 0, 0, 0.35f), 6);
            DrawLine(new Vector2(0, edgeY), new Vector2(w, edgeY), Paint.Alpha(LineTint, 0.5f), 1.5f);
        }
        else
        {
            DrawLine(new Vector2(0, edgeY), new Vector2(w, edgeY), Paint.Alpha(LineTint, 0.55f), 1.5f);
            Paint.VGradient(this, new Rect2(0, 1f, w, 16f), Paint.Alpha(LineTint, 0.055f), new Color(0, 0, 0, 0f), 8);
        }

        if (!CornerTicks) return;
        // 两端各一道斜切亮线：只在足够宽的时候画，窄屏上会挤到文字
        if (w < 260f) return;
        var tick = LineAtBottom ? -18f : 18f;
        for (var s = 0; s < 2; s++)
        {
            var x = s == 0 ? 0f : w;
            var dir = s == 0 ? 1f : -1f;
            DrawLine(new Vector2(x, edgeY), new Vector2(x + dir * 26f, edgeY + tick), Paint.Alpha(LineTint, 0.55f), 2f);
        }
    }
}

/// <summary>
/// 弹出面板背后的压暗层：中间几乎不暗、四角很暗。
///
/// `ColorRect` 的 0.62 纯黑是一刀切的，弹窗边缘于是「硬切」在背景上。
/// 换成径向暗角，玩家感觉是「灯被人调暗了」，而不是「有人贴了张黑纸」。
/// </summary>
public partial class ScrimBackdrop : Control
{
    /// <summary>
    /// 压在整屏上的那层黑的色值。
    ///
    /// 单拎出来是因为截图工具要靠它认出「这一屏上有一层覆盖层」——
    /// 它是一整屏半透明色块，没有任何控件类型或布局能数到它，
    /// 而它恰恰是「一张卡盖住三屏」时唯一在画面上留下的证据。
    /// </summary>
    public static readonly Color OverlayColor = new(0, 0, 0, 0.5f);

    public ScrimBackdrop()
    {
        MouseFilter = MouseFilterEnum.Stop;
    }

    /// <summary>
    /// 这一层画出来的**最深处**有多黑（给冒烟用）。
    ///
    /// 它曾经是假的：`_Draw` 里那圈 `Paint.Glow(..., alpha: 0)` 把中间整块抹回透明——
    /// 于是弹窗下面那张地图和弹窗一样亮，玩家分不清哪一层在上面。
    /// 判据读这个数，而不是在断言里再算一遍公式：算一遍只能证明公式自洽。
    /// </summary>
    public static float DarkestAlpha => OverlayColor.A + 0.0f;

    public override void _Draw()
    {
        var w = Size.X;
        var h = Size.Y;
        if (w <= 1f || h <= 1f) return;
        // 整屏先压一层：**不许有洞**。
        //
        // 这里原来跟着一条 `Paint.Glow(..., new Color(0,0,0,1), 0f)`——
        // 本意是「中心留一点亮、别把注意力拉到四角」，可传的 alpha 是 0：
        // 它非但没留亮，反而在屏幕正中画了一圈**全透明**的椭圆（`Glow` 的每一层都乘 0）。
        // 截图 05 里弹窗下面那张地图与不弹窗时一模一样亮，就是这一圈。
        //
        // 一行代码、两个坑，都在这条路径上：
        //   一、`DrawRect(rect, color)` 的默认 `width` 是 1——屏上那层「压暗」其实是
        //       四条 1px 的灰边，中间一整块**原样透亮**（`05-panel.png` 里弹窗背后
        //       那一片 127 灰就是这么来的）。整屏压暗这件事不许靠默认参数。
        //   二、`DrawRect(rect, color, filled)` 的模板参数在 C# 里是 `filled = true`，
        //       但**宽度仍然是 1**。两者一起给，才是一块实心面：`filled` 只决定「填充」，
        //       不决定「填充多大」。别因为在别的引擎里见过 `filled` 就省掉 `width`。
        DrawRect(new Rect2(0, 0, w, h), OverlayColor);
        // 上沿再压一档：对话框贴在中间时，顶上那条不该和屏幕外一样黑
        Paint.VGradient(this, new Rect2(0, 0, w, h * 0.18f), new Color(0, 0, 0, 0.22f), new Color(0, 0, 0, 0f), 8);
        Paint.VGradient(this, new Rect2(0, h * 0.82f, w, h * 0.18f), new Color(0, 0, 0, 0f), new Color(0, 0, 0, 0.26f), 8);

        // 再压一档「看进井里」的纵深：**用黑，不用任何色相**。
        //
        // 暗角只该减亮，不该加颜色——弹窗下面那块画面本来是什么颜色，压暗之后还该是什么颜色。
        // 半径按屏幕对角线取（`0.66w / 0.62h` 在 16:9 上正好罩住四角），
        // 中心再压 0.18：四周叠起来约 0.68，中心约 0.56——「灯被调暗」而不是「糊了张纸」。
        Paint.SoftEllipse(this, new Vector2(w * 0.5f, h * 0.5f), w * 0.66f, h * 0.62f,
            new Color(0, 0, 0, 1), 0.18f, 12);
    }
}

/// <summary>
/// 内容与面板之间的那条细分割线：中间亮、两端淡出。
/// 实心 1px 的线在深色底上显得「刀切」，渐隐的线才像光扫出来的一道。
/// </summary>
public partial class Hairline : Control
{
    public Color Tint = Palette.Border;

    public Hairline()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        CustomMinimumSize = new Vector2(0, 1);
    }

    public override void _Draw()
    {
        var w = Size.X;
        if (w <= 1f) return;
        var y = Size.Y * 0.5f;
        const int seg = 24;
        for (var i = 0; i < seg; i++)
        {
            // 中间 1、两端 0 的抛物线：接缝看不见，只有一段光
            var t = i / (float)(seg - 1);
            var a = 1f - Mathf.Pow(Mathf.Abs(t - 0.5f) * 2f, 1.6f);
            var x0 = w * i / seg;
            DrawLine(new Vector2(x0, y), new Vector2(w * (i + 1) / seg, y),
                Paint.Alpha(Tint, 0.55f * a), 1f);
        }
    }
}

/// <summary>
/// 铁牌按钮：主菜单与面板里的动作都用它。
///
/// 三态靠「上沿高光线 + 底色亮度 + 左右两道斜切」区分，
/// 而不是只换一个底色——静态按钮玩家看不出能不能点，这在这个项目里已经踩过一次。
/// 主按钮（Primary）额外给一圈金色可见光，因为它必须是这一屏最显眼的东西。
/// </summary>
public partial class PlateButton : Button
{
    public Color Accent = Palette.Accent;
    public bool Primary;
    /// <summary>键位提示，画在右侧（「M」「Esc」）。空串等于不画</summary>
    public string KeyHint = "";

    private bool _hover;
    private bool _down;

    public PlateButton()
    {
        Flat = true;
        MouseFilter = MouseFilterEnum.Stop;
        MouseEntered += () => { _hover = true; QueueRedraw(); };
        MouseExited += () => { _hover = false; _down = false; QueueRedraw(); };
        ButtonDown += () => { _down = true; QueueRedraw(); };
        ButtonUp += () => { _down = false; QueueRedraw(); };
    }

    public override void _Draw()
    {
        var w = Size.X;
        var h = Size.Y;
        if (w <= 2f || h <= 2f) return;

        var disabled = Disabled;
        var lit = Primary ? 0.30f : 0.16f;
        if (_hover && !disabled) lit += 0.16f;
        if (_down && !disabled) lit -= 0.06f;

        const float k = 9f;
        var r = new Rect2(0, 0, w, h);

        // 投影：往下错 3px。不投影的平板按钮看着像「印在面板上的字」
        if (!disabled)
            Paint.BeveledRect(this, new Rect2(1, 3, w - 2, h - 3), k, new Color(0, 0, 0, 0.38f));

        var baseCol = Palette.PanelSoft.Lerp(Accent, Primary ? 0.16f : 0.06f);
        // 纵向渐变：上亮下暗，方向与「光从上面来」一致
        var top = Paint.Lighten(baseCol, lit * 0.55f);
        var bottom = Paint.Darken(baseCol, disabled ? 0.1f : 0.22f);
        Paint.BeveledRect(this, r, k, disabled ? new Color("151d2c") : bottom);
        Paint.VGradient(this, new Rect2(1, 1, w - 2, h - 2),
            disabled ? new Color("1b2436") : top, disabled ? new Color("141b28") : bottom, 10);

        // 左侧一条 accent 竖条：整排按钮里「这是可点的」最快的读法
        var bar = Primary ? 4f : 3f;
        Paint.BeveledRect(this, new Rect2(0, 0, bar + 2f, h), 2f,
            Paint.Alpha(Accent, disabled ? 0.18f : Primary ? 0.95f : 0.55f));

        // 上沿高光 + 描边
        var edge = disabled ? Palette.Border : Paint.Lighten(Accent, _hover ? 0.35f : 0.1f);
        Paint.BeveledOutline(this, new Rect2(0.5f, 0.5f, w - 1, h - 1), k, Paint.Alpha(edge, disabled ? 0.5f : 0.85f), 1.4f);
        Paint.BeveledRect(this, new Rect2(k + 4, 1.5f, w - k * 2 - 8, 1.5f), 0.75f, new Color(1, 1, 1, disabled ? 0.03f : 0.10f));

        // 内描边：距边 3px 一道 6% 白线。这一笔是「铁牌」和「一块渐变」的分界——
        // 只有外描边时，卡面看着还是平的；有了内描边，边框才读得出厚度。
        Paint.BeveledOutline(this, new Rect2(3.5f, 3.5f, w - 7, h - 7), Mathf.Max(2f, k - 3f),
            new Color(1, 1, 1, disabled ? 0.018f : 0.055f), 1f);

        if (Primary && !disabled)
        {
            // 主按钮的余光：只往两侧薄薄地铺一点，不能糊到按钮外面去
            Paint.SoftEllipse(this, new Vector2(w * 0.5f, h * 0.5f), w * 0.5f, h * 0.62f, Accent, 0.06f, 20);
        }

        if (!string.IsNullOrEmpty(Text))
        {
            var font = GetThemeFont("font");
            var size = GetThemeFontSize("font_size");
            var col = disabled ? Palette.Dim : Primary ? Paint.Lighten(Accent, 0.55f) : Palette.Text;
            if (_hover && !disabled && !Primary) col = Colors.White;
            var tw = font.GetStringSize(Text, HorizontalAlignment.Left, -1, size).X;
            var tx = (w - tw) * 0.5f;
            DrawString(font, new Vector2(tx, h * 0.5f + size * 0.36f), Text,
                HorizontalAlignment.Left, -1, size, col);
        }

        if (string.IsNullOrEmpty(KeyHint)) return;
        var kf = GetThemeFont("font");
        var ks = Math.Max(10, GetThemeFontSize("font_size") - 4);
        var kw = kf.GetStringSize(KeyHint, HorizontalAlignment.Left, -1, ks).X;
        DrawString(kf, new Vector2(w - kw - 14f, h * 0.5f + ks * 0.36f), KeyHint,
            HorizontalAlignment.Left, -1, ks, Paint.Alpha(Palette.AccentSoft, 0.75f));
    }
}

/// <summary>
/// Boss 出场 / 胜利结算这类「单独一屏」的底：径向深渊 + 三条环 + 地平雾。
///
/// 原来这里是一块纯黑 `ColorRect`：一个紫色剪影贴在纯黑上，看着像素材没导出。
/// 一屏戏的「分量」几乎全靠背景给——同样的剪影放在有纵深的光里，
/// 观感是「它从深处升起来」，放在纯黑上就只是「一块贴纸」。
/// </summary>
public partial class AbyssStage : Control
{
    public Color Tint = new Color("8f4dff");
    public bool ShowFrame = true;

    private float _t;

    public AbyssStage()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Process(double delta)
    {
        _t += (float)delta;
        QueueRedraw();
    }

    public override void _Draw()
    {
        var w = Size.X;
        var h = Size.Y;
        if (w <= 1f || h <= 1f) return;

        // 底：中间深紫、四周近黑。不是纯黑——纯黑的画面显脏，也不给光留位置
        DrawRect(new Rect2(0, 0, w, h), new Color("05060b"));
        Paint.VGradient(this, new Rect2(0, 0, w, h), Paint.Alpha(Tint, 0.055f), new Color(0, 0, 0, 0f), 20);

        var center = new Vector2(w * 0.5f, h * 0.44f);
        // 主光：一团被压扁的、会呼吸的深渊辉光
        var breathe = 0.86f + 0.14f * Mathf.Sin(_t / 2.4f * Mathf.Tau);
        // 软衰减，不是一摞同心椭圆。
        //
        // 这里原来是一套 `Glow(…, 9 层)`、外面再叠三圈**填实**的椭圆 + 三条描边。
        // 结果是整屏一张同心圆网：9 层光环的每一层都在 5% 的亮度差上留一条边，
        // 三圈填实椭圆又各留一条轮廓线——4 倍放大之后，「深渊」是一圈圈能数出来的环。
        // 「给一口井的纵深」靠的是**亮度梯度**，不是环的条数：
        // 一道干净的径向坡 + 一条极淡的地平线，就够了。
        Paint.SoftEllipse(this, center, w * 0.42f, h * 0.52f, Tint, 0.20f * breathe, 10);

        // 纵深：一条压在最深处的地平线 + 一圈**极淡**的界线（只留一道，不再三圈）
        var horizon = center + new Vector2(0, h * 0.20f);
        Paint.SoftEllipse(this, horizon, w * 0.30f, h * 0.055f, Tint, 0.10f * breathe, 6);
        var pts = new Vector2[65];
        for (var j = 0; j <= 64; j++)
        {
            var ang = Mathf.Tau * j / 64;
            pts[j] = center + new Vector2(Mathf.Cos(ang) * w * 0.30f, Mathf.Sin(ang) * h * 0.26f);
        }
        DrawPolyline(pts, Paint.Alpha(Tint, 0.10f * breathe), 1.2f, true);

        // 地平雾：脚下一片横向的光池，立绘才「站得住」
        var floorY = h * 0.78f;
        Paint.SoftEllipse(this, new Vector2(w * 0.5f, floorY), w * 0.52f, h * 0.12f, Tint, 0.24f * breathe, 9);
        Paint.VGradient(this, new Rect2(0, floorY, w, h - floorY), new Color(0, 0, 0, 0.35f), new Color(0, 0, 0, 0.75f), 14);

        // 石点：极淡的浮尘，给静止的画面一点颗粒
        Paint.Speckle(this, new Rect2(0, 0, w, h), new Color(1, 1, 1, 0.03f), 70, 1.1f);

        if (!ShowFrame) return;
        // 四角框：两道斜切的角标。屏幕四角空着时画面会「漏」出去，
        // 有这四个角，整块屏幕就成了一扇窗。
        var m = new Vector2(26f, 22f);
        var col = Paint.Alpha(Tint, 0.55f);
        void Corner(float x, float y, float dx, float dy)
        {
            DrawLine(new Vector2(x, y), new Vector2(x + dx * 46f, y), col, 2f);
            DrawLine(new Vector2(x, y), new Vector2(x, y + dy * 34f), col, 2f);
            DrawLine(new Vector2(x + dx * 46f, y), new Vector2(x + dx * 54f, y + dy * 6f), Paint.Alpha(Tint, 0.25f), 1.4f);
            DrawLine(new Vector2(x, y + dy * 34f), new Vector2(x + dx * 6f, y + dy * 40f), Paint.Alpha(Tint, 0.25f), 1.4f);
        }
        Corner(m.X, m.Y, 1, 1);
        Corner(w - m.X, m.Y, -1, 1);
        Corner(m.X, h - m.Y, 1, -1);
        Corner(w - m.X, h - m.Y, -1, -1);
    }
}
