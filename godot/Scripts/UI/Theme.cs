// 一套最小主题：颜色、字号、按钮与面板样式。
//
// TS 版的 ui/theme.ts 里那些色值是「深渊回响」的视觉基调所在，
// 这里原样搬过来——玩家认的不是某个引擎，是这套颜色。
namespace AbyssEcho.UI;

using Godot;

public static class Palette
{
    // 底色往更深的冷蓝压了两档：原来的 #0b0e16 在 OLED 上是一块灰，
    // 深渊该是「黑里透蓝」而不是「浅灰里偏冷」。所有色相都在这个基色上做加减，
    // 一整屏看下去才像同一盏灯打的。
    public static readonly Color Bg = new("070a12");
    public static readonly Color Panel = new("121a2a");
    public static readonly Color PanelSoft = new("182437");
    public static readonly Color PanelDeep = new("0c1220");
    public static readonly Color Border = new("2b3a57");
    public static readonly Color BorderLit = new("4a628c");
    public static readonly Color Text = new("e9effb");
    public static readonly Color Muted = new("93a2bf");
    public static readonly Color Dim = new("63738f");
    public static readonly Color Gold = new("f5c15e");
    public static readonly Color Accent = new("74c8ff");
    public static readonly Color AccentSoft = new("a8cdf5");

    /// <summary>
    /// 界面文字的「底色替身」：同一个色值，不带透明度。
    ///
    /// 为什么要有一个替身：`Lerp(白, 文字色, cover)` 的反函数是
    /// 「`Lerp(白, 底色, 1 - cover)` 还原出的文字色」——而只要文字色里**带 alpha**，
    /// 还原出来的就不是它自己（`accent_soft` 原来写着 `(0.66,0.80,0.96,0.55)`，
    /// 还原出来是 210 而不是 168，于是这条判据把正确的界面判成「字糊在底上」）。
    /// 判据要的是「字有没有落墨」，不是「字用了哪个色号」：把 alpha 抹掉，两边就一致了。
    /// </summary>
    public static Color Opaque(Color c) => new(c.R, c.G, c.B, 1f);
    public static readonly Color Good = new("74d894");
    public static readonly Color Danger = new("ff6a6a");
    // 危险色另配一档「更深的血」：血条的底与内芯是同一个色相的两端，
    // 只用 #ff6a6a 一条色带看着像贴纸，加一层深红才有「灌进来」的体积。
    public static readonly Color DangerDeep = new("8a1f2e");
    public static readonly Color GoodDeep = new("1f6b42");
    public static readonly Color Crit = new("ffb257");
    public static readonly Color Boss = new("ff8fa3");
    public static readonly Color Shadow = new(0f, 0f, 0f, 0.45f);
}

public static class UiTheme
{
    /// <summary>整套主题：一个 StyleBox 复用到所有面板，避免每个场景各画一遍</summary>
    public static Theme Build()
    {
        var theme = new Theme();

        theme.SetColor("font_color", "Label", Palette.Text);
        theme.SetColor("default_color", "RichTextLabel", Palette.Text);
        theme.SetFontSize("font_size", "Label", 16);

        var panelStyle = new StyleBoxFlat
        {
            BgColor = Palette.Panel,
            // 淡投影 + 圆角收小一档：原来的 8px 圆角配纯色底，整块像贴纸
            ShadowColor = new Color(0, 0, 0, 0.35f),
            ShadowSize = 6,
            ShadowOffset = new Vector2(0, 2),
            BorderColor = Palette.Border,
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
            CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
            ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 10, ContentMarginBottom = 10,
        };
        theme.SetStylebox("panel", "PanelContainer", panelStyle);
        theme.SetStylebox("panel", "Panel", panelStyle);

        // 按钮：三态各一份，hover 亮一档、pressed 暗一档——静态按钮玩家看不出能不能点
        foreach (var (state, color) in new (string, Color)[]
                 {
                     ("normal", Palette.PanelSoft),
                     ("hover", new Color("22314c")),
                     ("pressed", new Color("0f1624")),
                     ("disabled", new Color("161c29")),
                 })
        {
            var sb = new StyleBoxFlat
            {
                BgColor = color,
                BorderColor = state == "hover" ? Palette.Accent : Palette.Border,
                BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 2,
                CornerRadiusTopLeft = 5, CornerRadiusTopRight = 5,
                CornerRadiusBottomLeft = 5, CornerRadiusBottomRight = 5,
                ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 8, ContentMarginBottom = 8,
            };
            theme.SetStylebox(state, "Button", sb);
        }
        // 焦点：键盘走到的那一格要看得见。
        //
        // 全键盘可玩的游戏里，看不到「焦点现在在哪」等于玩不了——而这个项目
        // 一直只有鼠标悬停的高亮，`focus` 走的是主题默认（一圈几乎看不见的描边）。
        // 现在它和 hover 同色、但**更粗**：鼠标与键盘走的是同一套光，强弱不同。
        var focus = new StyleBoxFlat
        {
            BgColor = new Color("22314c"),
            BorderColor = Palette.AccentSoft,
            BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 5, CornerRadiusTopRight = 5,
            CornerRadiusBottomLeft = 5, CornerRadiusBottomRight = 5,
            ContentMarginLeft = 14, ContentMarginRight = 14, ContentMarginTop = 8, ContentMarginBottom = 8,
        };
        theme.SetStylebox("focus", "Button", focus);
        theme.SetColor("font_color", "Button", Palette.Text);
        theme.SetColor("font_hover_color", "Button", Colors.White);
        theme.SetColor("font_disabled_color", "Button", Palette.Dim);
        theme.SetFontSize("font_size", "Button", 16);

        var lineEdit = new StyleBoxFlat
        {
            BgColor = Palette.Bg,
            BorderColor = Palette.Border,
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 8, ContentMarginRight = 8,
        };
        theme.SetStylebox("normal", "LineEdit", lineEdit);
        return theme;
    }

    /// <summary>项目里没有字体资源，用系统 CJK 字体兜底：中文在默认字体里会变成方块。</summary>
    public static Font FallbackFont()
    {
        var sys = new SystemFont
        {
            FontNames = new[] { "Noto Sans CJK SC", "Source Han Sans SC", "Microsoft YaHei", "PingFang SC", "WenQuanYi Micro Hei", "Noto Sans SC", "sans-serif" },
        };
        return sys;
    }

    /// <summary>
    /// 一段文字。
    ///
    /// `MouseFilter = Ignore` 是**必须的**，不是保险：`Label` 的默认值是 `Stop`，
    /// 于是一个铺满宽度的标签会吃掉它整块矩形上的鼠标事件，连带盖住底下那层。
    /// 这些标签全是「读的数字」，没有一个是可点的——它们不该参与命中测试。
    ///
    /// 更要紧的是另一件事：`Label` 默认是一个**不透明的面板**吗？不是。
    /// 那片白不是它画的（`--diag` 把每个大面积不透明的 CanvasItem 都打出来过，
    /// 里面没有一块是白的）。所以把它按 `Ignore` 摆平，是为了别让叠层问题更难查。
    /// </summary>
    public static Label Text(string text, int size = 16, Color? color = null)
    {
        var l = new Label { Text = text, MouseFilter = Control.MouseFilterEnum.Ignore };
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color ?? Palette.Text);
        return l;
    }

    public static Label Wrap(string text, int size = 15, Color? color = null, float width = 0)
    {
        var l = Text(text, size, color);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        if (width > 0) l.CustomMinimumSize = new Vector2(width, 0);
        return l;
    }

    /// <summary>
    /// 界面上所有按钮的唯一入口。
    ///
    /// 「按下要有反应」这件事以前散在每处调用点上：战斗里 4 个按钮上写 `Sfx.Play`，
    /// 弹窗里的按钮一个都没写——同一屏上有的键有声、有的没声，玩家读成「这个键没生效」。
    /// 所以反馈收到这里：**按下去先压一档再弹回**，并且响一声。
    /// 调用方只管「按了之后要做什么」。
    /// </summary>
    public static Button Btn(string text, Action onPressed, int height = 40, bool disabled = false)
    {
        var b = new Button { Text = text, CustomMinimumSize = new Vector2(0, height) };
        b.Pressed += () =>
        {
            Sfx.Play("click");
            Press(b);
            onPressed();
        };
        b.Disabled = disabled;
        return b;
    }

    /// <summary>
    /// 按下的那一档：缩到 0.97，再弹回。
    ///
    /// 幅度只有 3%——它是「手感」不是「动画」，再大就是「按钮跳了一下」。
    /// 曲线两段都用 `Motion.In`（起步快落点稳）：按下的反馈必须在第一帧就看得见。
    /// </summary>
    public static void Press(Control b)
    {
        if (!GodotObject.IsInstanceValid(b)) return;
        // 轴心放中心：默认轴心在左上角，缩放会把按钮往右下拉，读起来像「位移」
        b.PivotOffset = b.Size * 0.5f;
        var down = Motion.In(b);
        down.TweenProperty(b, "scale", new Vector2(0.97f, 0.97f), Motion.Fast * 0.5f);
        var up = Motion.In(b);
        up.TweenProperty(b, "scale", Vector2.One, Motion.Fast);
    }

    /// <summary>带底色的标题条：面板顶部的「这是干什么的」</summary>
    public static PanelContainer Header(string title, Color? accent = null)
    {
        var panel = new PanelContainer();
        var tint = accent ?? Palette.AccentSoft;
        var style = new StyleBoxFlat
        {
            // 标题条：底色比面板更沉一档，下沿一道 accent 亮线，
            // 让「这是这块面板的名字」一眼可辨——纯色条会跟内容黏在一起。
            BgColor = new Color("0e1523"),
            BorderColor = Paint.Alpha(tint, 0.75f),
            BorderWidthTop = 1, BorderWidthBottom = 2,
            ContentMarginLeft = 26, ContentMarginRight = 20, ContentMarginTop = 12, ContentMarginBottom = 10,
        };
        panel.AddThemeStyleboxOverride("panel", style);
        // 标题条本身是 PanelContainer：它只摆第一个子节点，第二个会被叠到同一个 rect 上。
        // 所以左侧那道金色竖条不能在容器里再塞一个 ColorRect，得画在面板自己身上
        // （不然那个 ColorRect 会被当成唯一的内容，把标题顶掉——截图里标题条被压成一条就是它）。
        // 金条改由 stylebox 的左边框画：PanelContainer 会把每个非 top-level 子节点
        // 都摆到同一个 rect 上，一个不占最小尺寸的自绘件在那里只会被压成 0 高
        // （截图里标题条上根本看不见那根线）。
        style.BorderWidthLeft = 4;
        style.BorderColor = Paint.Alpha(Palette.Gold, 0.9f);
        style.ContentMarginLeft = 22;
        panel.AddChild(Text(title, 20, tint));
        return panel;
    }

    public static PanelContainer Card(Action<Container> fill, Color? border = null)
    {
        var panel = new PanelContainer();
        var style = (StyleBoxFlat)new StyleBoxFlat
        {
            BgColor = Palette.PanelSoft,
            BorderColor = border ?? Palette.Border,
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 10, ContentMarginBottom = 10,
        };
        panel.AddThemeStyleboxOverride("panel", style);
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        panel.AddChild(box);
        fill(box);
        return panel;
    }

    /// <summary>
    /// 进度条（血条 / 经验条 / 敌人血条）。
    ///
    /// 必须按比例取量程：早先这里把 MaxValue 写死成 100，而玩家血量是 60/60、
    /// 敌人是 9/22——于是满血也只画 60% 和 40%，屏上那条血永远是「掉了半管」。
    /// 量程交给调用方的最大值，值就是当前值。
    /// </summary>
    public static ProgressBar Bar(Color fill, int height = 16, float value = 100f, float max = 100f)
    {
        var bar = new ProgressBar
        {
            CustomMinimumSize = new Vector2(0, height),
            ShowPercentage = false,
            MinValue = 0,
            MaxValue = System.Math.Max(1f, max),
            Value = Mathf.Clamp(value, 0f, System.Math.Max(1f, max)),
        };
        var bg = new StyleBoxFlat
        {
            // 底槽要有内陷感：深一档 + 一圈内描边，条子才像「嵌进面板」
            BgColor = Palette.PanelDeep,
            BorderColor = new Color(0, 0, 0, 0.55f),
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
        };
        // 填充：上亮下暗两段，读作「灌进来的液体」而不是一块色带
        var fg = new StyleBoxFlat
        {
            BgColor = fill,
            BorderColor = Paint.Lighten(fill, 0.35f),
            BorderWidthTop = 1,
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3,
        };
        fg.ShadowColor = Paint.Alpha(fill, 0.45f);
        fg.ShadowSize = 2;
        // 条子的**最小高**必须由这里说了算。
        //
        // `CustomMinimumSize` 只是「别小于这个」，而 `ProgressBar` 自己按主题字号报的
        // 最小高是 0：两条血条一起塞进一排 HBox 时，HBox 把富余的高度摊给它们，
        // 于是 16px 的血条长成 17px、8px 的经验条也长成 17px——地图 HUD 上
        // 两条一模一样粗的条子，读不出「哪条是命、哪条是进度」。截图里那对
        // 「半条血 + 一整条经验」就是这么来的，而代码里两条的尺寸本来写得对。
        // 这里把两条 stylebox 的最小高一起钉死，容器再怎么摊也摊不开。
        bg.ContentMarginTop = 0;
        bg.ContentMarginBottom = 0;
        bg.ContentMarginLeft = 0;
        bg.ContentMarginRight = 0;
        fg.ContentMarginTop = 0;
        fg.ContentMarginBottom = 0;
        fg.ContentMarginLeft = 0;
        fg.ContentMarginRight = 0;
        // 最小高由样式盒说了算：`CustomMinimumSize` 只是下限，撑不撑开还得看这里
        bar.CustomMinimumSize = new Vector2(0, height);   // 只是下限；真正的粗细见 SizeFlagsVertical
        bar.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        bar.AddThemeStyleboxOverride("background", bg);
        bar.AddThemeStyleboxOverride("fill", fg);
        return bar;
    }
}
