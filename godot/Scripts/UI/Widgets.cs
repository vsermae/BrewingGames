// 共用小部件：覆盖层、技能按钮、房间格子。
//
// 这一层只做「摆位置 + 回调」，任何规则判断都不写在这里——
// 规则在 Game / Battle，界面错了只是难看，规则错了玩家会骂人。
namespace AbyssEcho.UI;

using Godot;
using AbyssEcho.Core;
using AbyssEcho.Data;
using AbyssEcho.Systems;

public static partial class Widgets
{
    /// <summary>
    /// 截图工具用来造「长内容」的几行说明文字。
    ///
    /// 弹窗的工作区一有滚动条，就出现「内容比面板高」这条分支——而它一直没被拍过：
    /// 商店那一屏刚好好装得下，于是「居中容器在内容超高时把面板往上顶出屏幕」
    /// 这件事只在玩家真的攒满词条时才出现，而那时光看截图也说不清是谁顶的。
    /// </summary>
    public static readonly string[] LongContentLines =
    {
        "这一行是故意写长的：弹窗的工作区一旦装不下内容，居中容器就会把面板整个往上顶，",
        "被顶掉的第一样东西是标题条——玩家看到一张没有名字、也没有关闭键的卡。",
        "判据不该问「面板有多高」（它可以很高），而该问「面板画出来的那一块在不在屏幕里」。",
        "内容越长，这个差别越明显：从「标题被吃掉一条边」到「整张卡只剩下半截」。",
        "所以这一屏要拍的正是这种状态：内容明显比工作区高，而标题条必须还在。",
        "如果某天这条分支又被写坏了，这一张会红，而不是等玩家来报「弹窗打不开」。",
        "下面几行只是把长度堆够，读起来像说明书就够了。",
        "第七行：面板本体（PanelPlate）的顶边不许跑到屏幕上方去。",
    };

    /// <summary>
    /// 覆盖层的「指纹」：一层压暗底色 + 每一块标题条上的字。
    ///
    /// 为什么需要它：覆盖层的底色是一整屏半透明黑，人一眼看得出，
    /// 但任何按控件类型 / 布局做的断言都数不到它——`05-panel.png` 里
    /// 那张卡盖住三屏、流水线照样绿，就是这么来的。
    /// 截图工具拿这个指纹问「这一屏上有几个覆盖层、各叫什么」，
    /// 数量与名字对不上就 red。
    /// </summary>
    public static List<(Color Scrim, List<string> Titles)> Overlays(Node root, Color? scrimColor = null)
    {
        var found = new List<(Color, List<string>)>();
        Walk(root);
        return found;

        void Walk(Node node)
        {
            if (node is ScrimBackdrop scrim)
            {
                var titles = new List<string>();
                CollectHeaders(node, titles);
                found.Add((ScrimBackdrop.OverlayColor, titles));
                return;  // 覆盖层里不会有第二个覆盖层，别往里钻
            }
            foreach (var child in node.GetChildren()) Walk(child);
        }
    }

    /// <summary>覆盖层里的标题条：`UiTheme.Header` 造的那一块（只摆一个 Label 子节点）</summary>
    private static void CollectHeaders(Node node, List<string> into)
    {
        foreach (var child in node.GetChildren())
        {
            if (child is PanelContainer panel && panel.GetChildCount() == 1 && panel.GetChild(0) is Label title)
            {
                into.Add(title.Text);
                continue;
            }
            CollectHeaders(child, into);
        }
    }

    /// <summary>
    /// 全屏覆盖层：半透明黑底 + 居中面板 + 竖排内容。
    /// 弹出时会吃掉输入（鼠标 + 键盘），保证玩家不会在写字的时候还在走路。
    /// </summary>
    public static (Control Root, VBoxContainer Body) Overlay(Control parent, string title, float width = 640)
    {
        // 压暗层用自绘暗角而不是一块 62% 纯黑：纯黑是一刀切的，
        // 弹窗边缘会在背景上「硬切」；暗角让玩家感觉是「灯被调暗了」。
        // 压暗层取 `Overlay` 的**下一档**，不跟弹窗自己在同一号上抢「谁后加」。
        //
        // 起因是一次截图：`05-panel.png` 里弹窗背后那一大片不是「灯被调暗了」，
        // 而是一整块 **127 的灰板**——0.5 的黑叠了两遍（0.5 + 0.5×0.5 = 0.75）。
        // 两片同号同 z 的层，谁在上只取决于 `AddChild` 的先后。
        //
        // 实测过：把弹窗和压暗层分到 100 / 99 两档，灰板**照样在**——所以真正坐在
        // 那一片上的不是「第二片压暗层」，而是这张卡自己（或它上面的某一块不透明底）。
        // 层号这一步仍然是必须的（压暗层不该跟卡片抢同一号），但它不是那个 bug 的解；
        // 那个还开着，见 `DEVELOPER_NOTES.md` 第 43 轮。
        var root = new ScrimBackdrop { ZIndex = Layers.Of(StageOrder.Overlay) - 1 };
        parent.AddChild(root);
        // 进树之后再设锚点：在构造函数里写 AnchorsPreset 不会重算偏移，
        // 面板就会缩在左上角（这个坑在截图里出现过两次）。
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        // 居中容器要把面板**夹在屏幕里**：照旧铺满全屏的话，
        // 内容一旦比面板高，CenterContainer 会按「整块大小」居中——超出来的那一半往上顶，
        // 顶出屏幕的正好是标题条（玩家看到一张没有名字、也没有关闭键的卡）。
        // 所以居中框自己先留出上下各 24px，再由它居中：面板永远有边可以退。
        const float ScreenInset = 24f;
        var center = new CenterContainer();
        root.AddChild(center);
        center.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        center.OffsetTop = ScreenInset;
        center.OffsetBottom = -ScreenInset;

        // 背景与边框必须写进 stylebox：不写的话 StyleBoxFlat 的 BgColor 默认是全透明黑，
        // 面板背后是 ColorRect 的 0.62 黑幕，看起来就像「弹窗根本没弹出来」。
        var panel = new PanelContainer { CustomMinimumSize = new Vector2(width, 0), Name = "OverlayPanel" };
        // 面板底交给 PanelPlate 画（渐变 + 内描边 + 角标 + 投影）：
        // StyleBoxFlat 只会给一个纯色 + 一圈描边，弹窗看着像一块纸。
        var plate = new PanelPlate { MouseFilter = Control.MouseFilterEnum.Ignore };
        panel.AddChild(plate);
        // PanelContainer 只摆第一个非 top-level 子节点，后面的会被叠到同一个 rect 上：
        // 「一块底 + 一列内容」正好用这个特性。
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0, 0, 0, 0),
            ContentMarginLeft = 20, ContentMarginRight = 20, ContentMarginTop = 14, ContentMarginBottom = 16,
        });
        center.AddChild(panel);

        // 内容区可滚动：长内容（攒了十几条词条的商店、堆满说明的确认框）不该把面板撑出屏幕。
        //
        // 这一块改了三稿才立住，三稿各错在哪都值得记：
        //   一、不给滚动区任何高度约束 → 面板被内容撑到 765，底边出屏；
        //   二、只给它 `CustomMaximumSize` → 面板按标题条定高，正文排进 0 高的缝里；
        //   三、竖排「占位 + 滚动区」两个兄弟 → 滚动区一进树就把自己的最小高改成内容高，
        //       两个兄弟抢同一份高度，滚动区被分到 0，屏上还是一张只有标题的卡。
        // 现在：一块**隐形占位**报高度（跟着内容长、封顶 520），滚动区与它**叠在同一格里**
        // 并吃满这一格。报高度的只负责撑开父级，装内容的只负责充满。
        const float ScrollMaxHeight = 520f;

        var body = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 12);

        var bodyStack = new Control { Name = "OverlayBody" };
        bodyStack.CustomMinimumSize = new Vector2(0, 40);

        // 占位：只报高度，不画、不吃鼠标。它和滚动区铺满同一格，
        // 于是「面板多高」由它说、「正文占多高」由滚动区自己分。
        var contentHeight = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
        contentHeight.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        bodyStack.AddChild(contentHeight);

        var scroll = new ScrollContainer
        {
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto,
            CustomMinimumSize = Vector2.Zero,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        scroll.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        bodyStack.AddChild(scroll);

        // 内容一变就把高度同步到占位：+34 = 滚动区内边距（上下各 16/18 取整）。
        // 上限就是 `ScrollMaxHeight`：再多也不许比屏幕高。
        void SyncHeight() => bodyStack.CustomMinimumSize = new Vector2(0,
            Mathf.Min(ScrollMaxHeight, body.GetCombinedMinimumSize().Y + 34f));
        body.MinimumSizeChanged += SyncHeight;
        body.Ready += SyncHeight;

        var margin = new MarginContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        margin.AddThemeConstantOverride("margin_left", 18);
        margin.AddThemeConstantOverride("margin_right", 18);
        margin.AddThemeConstantOverride("margin_top", 16);
        margin.AddThemeConstantOverride("margin_bottom", 18);
        margin.AddChild(body);
        scroll.AddChild(margin);

        // 标题条不跟着滚：它是「这张卡是什么」的唯一标识，滚走了就等于没有。
        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 0);
        col.AddChild(UiTheme.Header(title));
        col.AddChild(bodyStack);
        panel.AddChild(col);

        // 入场：整张卡淡入（`Motion.Base + Fast*0.5` ≈ 0.28s）。
        //
        // 这里**不做上浮**。上一版是 `RiseIn(panel, ..., from: 14)`，它的做法是
        // 「记下此刻的 Position → 挪开 14px → 补间回原位」，前提是「这个控件的位置
        // 由它自己说了算」。而 `panel` 是 `CenterContainer` 的直接子节点：
        // 位置是容器每次重排算出来的（居中）。容器一重排就把 `Position` 写回居中值，
        // 补间的起点与终点同时作废。同一棵树里 `col`、`bodyStack` 也都是容器摆的，
        // 内层一样不安全；真要保住位移，得把整张卡从布局流里摘出来（`TopLevel`），
        // 那会连带失去居中与自适应高度——为 14px 换掉整套布局，不值。
        //
        // 淡入本身就够读出「它出现了」。一张要玩家做决定的卡，位置稳定比入场花哨重要：
        // 它出现那一刻，玩家的手已经在往选项上走了。位移留给飘字与提示——
        // 那些是「流过去的信息」，它们值得那一下。
        Motion.FadeIn(panel, Motion.Base + Motion.Fast * 0.5f);

        return (root, body);
    }

    /// <summary>
    /// 弹出面板的底板：纵向渐变 + 斜纹理 + 内描边 + 四角切角 + 投影。
    ///
    /// 它是一块「牌」而不是一个「框」：上沿有一道被光照到的亮线、四角切掉一点、
    /// 底下压一层投影。这三样凑齐了，它才浮在暗角之上，而不是嵌在里面。
    /// </summary>
    public sealed partial class PanelPlate : Control
    {
        public Color Accent = Palette.Accent;
        /// <summary>四角切角边长</summary>
        public float Bevel = 14f;

        public PanelPlate()
        {
            MouseFilter = MouseFilterEnum.Ignore;
        }

        public override void _Draw()
        {
            var w = Size.X;
            var h = Size.Y;
            if (w < 12f || h < 12f) return;

            // 投影：往右下错 6px，牌子才浮得起来
            Paint.BeveledRect(this, new Rect2(3, 7, w - 6, h - 8), Bevel, new Color(0, 0, 0, 0.5f));
            Paint.BeveledRect(this, new Rect2(1.5f, 3.5f, w - 3, h - 4), Bevel, new Color(0, 0, 0, 0.3f));

            var body = new Rect2(0, 0, w, h);
            // 底：上亮下暗的一档渐变，和整屏的光向一致
            Paint.BeveledRect(this, body, Bevel, Palette.Panel);
            // 面板要**挡住**它背后的东西，不是和它叠两遍：
            // 以前底色是 `PanelSoft` 提亮 0.02（≈0x1a 一档），底下那块地图/舞台
            // 透过描边之外的地方还在，卡面读起来像「一层薄纱」。
            // 弹窗是玩家要读字的地方，它的底该是这块屏幕上最实的一块。
            Paint.Surface(this, new Rect2(1, 1, w - 2, h - 2), Bevel,
                Palette.Panel, Paint.Darken(Palette.PanelDeep, 0.15f), 16);
            // accent 只染顶部一小片：整块染会让面板读成「带颜色的纸」。
            //
            // 这里原来每条半径 0.10、一共 6 层，**一摞叠起来是 0.6**——
            // 弹窗背后那层 0.5 的暗角已经很重，再加 0.6 的红，屏上就是一大片粉。
            // 截图里「商店」那张卡背后那块发白的光斑就是它；半透明条带之后更明显。
            // 现在是**一道**软衰减，总强度回到 0.10 左右（面板底色本身只有 0x12 上下）。
            Paint.SoftEllipse(this, new Vector2(w * 0.5f, h * 0.02f), w * 0.40f, h * 0.42f, Accent, 0.085f, 10);

            Paint.Hatches(this, new Rect2(2, 2, w - 4, h - 4), new Color(1, 1, 1, 0.012f), 10f, 1f);

            // 边框：外圈 accent 半透明、内圈一道 4% 白线（倒角）
            Paint.BeveledOutline(this, new Rect2(0.5f, 0.5f, w - 1, h - 1), Bevel, Paint.Alpha(Accent, 0.45f), 1.6f);
            Paint.BeveledOutline(this, new Rect2(4.5f, 4.5f, w - 9, h - 9), MathF.Max(2f, Bevel - 4f),
                new Color(1, 1, 1, 0.035f), 1f);

            // 上沿高光：牌子被顶光照到的那一条
            var hl = w * 0.62f;
            DrawLine(new Vector2((w - hl) * 0.5f, 2f), new Vector2((w + hl) * 0.5f, 2f),
                Paint.Alpha(Palette.Text, 0.10f), 1.6f);
        }
    }

    /// <summary>
    /// 单位卡（战斗里敌人那张）的底板。
    ///
    /// 为什么不用 StyleBoxFlat：它的 `BgColor` 只能给一个纯色，卡片上下同色看着像一张纸。
    /// TS 版这块是 8 段纵向渐变 + 上沿一条 1px 高光——一块金属铭牌的样子。
    /// StyleBox 支持多个叠加，但每一条缝隙的接缝都得自己对圆角，比直接画还麻烦；
    /// 所以这里整块交给 `_Draw`：形状、渐变、描边都在一处，改一个数就整体跟着变。
    /// </summary>
    public sealed partial class UnitCard : Control
    {
        public Color Accent = Palette.Accent;

        /// <summary>
        /// 用「深一档」的卡面。给顶栏那种**文字压在卡上**的场合：
        /// 默认的卡面是按「卡里有自己的深色底衬」设计的，直接在上面排白字会读成
        /// 「一行被高亮选中的文字」。舞台上的立绘卡不走这条路。
        /// </summary>
        public bool Deep;

        public UnitCard()
        {
            MouseFilter = MouseFilterEnum.Ignore;
            // 卡面铺满**整块卡**，而它被 `PanelContainer` 摆在自己的**内容区**里——
            // 一旦内容区比卡片小一圈，底纹就会溢出卡外（截图里表现为屏幕上沿一条不透明亮带）。
            // 卡面上的一切都该在卡的矩形里，多一点都不该露出去。
            ClipContents = true;
        }

        public override void _Draw()
        {
            var w = Size.X;
            var h = Size.Y;
            if (w < 8f || h < 8f) return;

            const float corner = 14f;
            var body = new Rect2(0, 0, w, h);

            // 投影：卡片浮在舞台上方，不是被钉在墙上
            Paint.RoundRect(this, new Rect2(body.Position + new Vector2(0, 5), body.Size), corner,
                new Color(0, 0, 0, 0.45f));

            // 底色：accent 淡染的一条纵向渐变。上亮下暗，方向与「光从上面来」一致
            // 深色档：顶栏那一条。0.05 的提亮在 720p 上就是 0x1c-0x24，
            // 而白字在它上面几乎读不出来；这里改成不提亮、压深一档。
            var top = Deep ? Paint.Darken(Palette.Panel, 0.30f) : Paint.Lighten(Palette.Panel, 0.05f);
            var bottom = Deep ? Paint.Darken(Palette.Panel, 0.55f) : Paint.Darken(Palette.Panel, 0.22f);
            // 底色与渐变合成一遍（`Surface`）+ 一条**极淡**的底衬：以前是先铺一层不透明的
            // `Panel` 再叠 14 条渐变横带，两层都近乎不透明，卡面等于被涂了两遍；
            // 而且横带的圆角只加在首尾两条上，卡的上下两端是「直边 + 磨过的角」。
            Paint.Surface(this, body, corner, Paint.Alpha(top, 0.99f), Paint.Alpha(bottom, 0.99f), 16);
            // accent 的染色只留在左上角：整块染的话卡片会变成「一张红纸」，
            // 玩家读不出血条与卡片哪个是「危险」的那一层。
            // 这道光**以传进来的点为中心**往外散，不是「从左上角往外散」：
            // 锚点留在 (0,0) 时那一团光有一半落在卡外、糊在整条屏幕上方，
            // 卡面自己反倒没染上色。现在中心落在卡内左上四分之一处，半径按卡宽逐步衰减。
            //
            // 注意别把这件事和另一个 bug 混起来：截图里敌人卡「上半截是一团红雾」
            // 有两层原因，光锚点错是其中一层，另一层是整块卡被 Arena 的墙色盖掉
            // （两者都在 ZIndex 0，谁先 AddChild 谁在下）。层序现在写在 UI/Layers.cs。
            //
            // 第三层原因是「同心」本身：这条路原来是 `Glow`（一摞同心椭圆），
            // 卡面是一整块平涂，层与层之间那道 1/255 就成了一圈能数出来的边。
            // 现在走 `SoftEllipse`（一次画完的径向渐变），卡面上没有环。
            Paint.SoftEllipse(this, new Vector2(w * 0.22f, h * 0.30f), w * 0.52f, h * 0.62f, Accent, 0.16f, 9);

            // 斜纹理 + 石点：一块真正「有材质」的卡面
            Paint.Hatches(this, new Rect2(1, 1, w - 2, h - 2), new Color(1, 1, 1, 0.013f), 9f, 1f);
            Paint.Speckle(this, new Rect2(2, 2, w - 4, h - 4), new Color(1, 1, 1, 0.018f), 40, 1.1f);

            // 上沿一条细高光：卡片边缘被光扫到的感觉，1px 就够
            Paint.RoundRect(this, new Rect2(4, 2, w - 8, 2.5f), 1.2f, new Color(1, 1, 1, 0.10f));

            // 外描边 + 内描边：外圈 85% accent、内圈 4% 白，切出倒角
            Paint.RoundRectOutline(this, body, corner, Paint.Alpha(Accent, 0.7f), 1.6f);
            Paint.RoundRectOutline(this, new Rect2(3, 3, w - 6, h - 6), corner - 3f,
                new Color(1, 1, 1, 0.035f), 1f);
        }
    }

    public static PanelContainer Card(Action<VBoxContainer> fill, Color? border = null)
    {
        var panel = new PanelContainer();
        panel.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = Palette.PanelSoft,
            BorderColor = border ?? Palette.Border,
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6,
            CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            ContentMarginLeft = 12, ContentMarginRight = 12, ContentMarginTop = 10, ContentMarginBottom = 10,
        });
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 6);
        panel.AddChild(box);
        fill(box);
        return panel;
    }

    public static Button CloseButton(VBoxContainer body, string text = "继续")
    {
        var btn = UiTheme.Btn(text, () => { }, 40);
        body.AddChild(btn);
        return btn;
    }

    /// <summary>技能按钮：名字 + 冷却/威力副标题，冷却中禁用但不隐藏</summary>
    public static Button SkillButton(SkillDef skill, Combatant actor, Action onPressed)
    {
        var cd = Skills.SkillCooldownLeft(actor, skill.Id);
        var ready = cd == 0;
        // 写的是「还要等几回合」，所以冷却 2 的技能放完显示「冷却 2」：
        // 再打两个回合就能放。以前这里直接显示施放时写进去的原始值（冷却 + 1），
        // 于是每个技能的按钮在整个冷却期间都多报一格。
        var label = ready ? skill.Name : $"{skill.Name}（还要 {cd} 回合）";
        var b = new Button
        {
            Text = label,
            TooltipText = skill.Desc,
            Disabled = !ready,
            CustomMinimumSize = new Vector2(148, 44),
        };
        b.Pressed += onPressed;
        return b;
    }

    /// <summary>词条说明行：装备卡上「锋锐：攻击 +15%」这种</summary>
    public static string AffixLine(AffixDef a) => $"{a.Name}：{a.Desc}";

    /// <summary>
    /// 装备卡上的一行摘要：固定属性 · 词条名（词条效果另行铺开）· 品质标签。
    /// 词条只列名字会被玩家读成「这装备没效果」——列表里塞不下效果，
    /// 所以这里给出名字，卡片下方用 AffixLines 把效果逐条写出来。
    /// </summary>
    public static string ItemSummary(ItemDef def, IReadOnlyDictionary<string, AffixDef> affixes)
    {
        var parts = new List<string>();
        if (def.Attack is { } atk) parts.Add($"攻击 +{atk}");
        if (def.Defense is { } def2) parts.Add($"防御 +{def2}");
        if (def.MaxHp is { } hp) parts.Add($"生命 +{hp}");
        if (def.Heal is { } heal) parts.Add($"回复 {heal}");
        if (def.Damage is { } dmg) parts.Add($"伤害 {dmg}");
        // 消耗品不在这里报品质档位：品质档位说的是「这件装备能带几条词条」，
        // 而一瓶药、一颗炸弹根本不会挂词条。以前这一句不带条件，于是商店里
        // 摆出来的每一件消耗品都写着「（无词条）」——玩家读到的是「这东西什么都没有」，
        // 而它旁边那件锁子甲同样写着「单词条」，两行字长得一模一样，谁也分不出哪个是装备。
        // 消耗品只说它做什么，然后把描述接着写出来（不然「解毒草」三个字什么都不解释）。
        if (def.Slot == Slot.Consumable)
        {
            if (def.Cleanse == true) parts.Add("解毒");
            var body = parts.Count > 0 ? string.Join(" · ", parts) : "消耗品";
            return string.IsNullOrWhiteSpace(def.Desc) ? body : $"{body}｜{def.Desc}";
        }
        // 品质决定词条上限，把词条数写出来（「词条 1/2」）能让玩家看懂
        // 「为什么这件紫装只带一条」——以前只列名字，串位与溢出都看不出来。
        var affixIds = def.Affixes ?? new List<string>();
        var names = affixIds.Select(id => affixes.TryGetValue(id, out var a) ? a.Name : id).ToList();
        // 品质说的是「这件**装备**能带几条词条」，消耗品根本不挂词条。
        // 以前这一行对消耗品也追加品质标签（「回复 70（单词条）」），
        // 和它下面那件锁子甲的「（单词条）」长得一模一样——
        // 商店里两行字一样，玩家扫一眼分不出哪个能吃、哪个能穿。
        // 消耗品只说它做什么；装备那边「词条 1/2」留着（不然「紫装为什么只带一条」没人解释）。
        if (def.Slot == Slot.Consumable)
            return parts.Count == 0 ? def.Desc : string.Join(" · ", parts);
        var limit = Inventory.AffixLimit(def);
        if (names.Count > 0) parts.Add($"词条 {names.Count}/{limit}：" + string.Join("、", names));
        var tag = Loot.QualityTag(Inventory.QualityOf(def));
        return $"{parts.Count switch { 0 => def.Desc, _ => string.Join(" · ", parts) }}（{tag}）";
    }

    /// <summary>装备上每条词条的效果，一行一条（词条是构筑的核心，名字不够，要看到数值）</summary>
    public static List<string> AffixLines(ItemDef def, IReadOnlyDictionary<string, AffixDef> affixes) =>
        (def.Affixes ?? new List<string>())
            .Take(Inventory.AffixLimit(def))
            .Select(id => affixes.TryGetValue(id, out var a) ? $"{a.Name}：{a.Desc}" : id)
            .ToList();

    /// <summary>
    /// 带图标的标题行：左边一个 24~40px 的矢量符号，右边标题。
    /// TS 版的面板几乎每一处都配了图标（升级、奖励、祭坛、训练、诅咒、事件结果），
    /// Godot 版原来只有一行行文字，面板看起来像一串表单。
    /// 图标本身还是 IconArt 现画，不引入任何贴图。
    /// </summary>
    public static HBoxContainer IconRow(string kind, string text, int size = 28, int fontSize = 16, Color? tint = null, Color? textColor = null)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        var icon = IconView.Of(kind, size, tint ?? Palette.Gold);
        icon.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(icon);
        var label = UiTheme.Text(text, fontSize, textColor ?? Palette.Text);
        label.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(label);
        return row;
    }

    /// <summary>图标 + 标题 + 说明的三行小块：奖励卡、装备格都用它</summary>
    public static HBoxContainer IconWithBody(string kind, Color tint, string title, string desc, float descWidth = 190, int titleSize = 16, int iconSize = 26) =>
        IconWithBody(kind, tint, title, desc, descWidth, titleSize, iconSize, null);

    /// <summary>
    /// 同上，另外允许在描述下面追加若干行（装备的词条效果）。
    /// 单独重载而不是让调用方自己再拼一个 HBox：图标那一列的对齐要跟标题一致，
    /// 在调用点重拼必然对不齐。
    /// </summary>
    public static HBoxContainer IconWithBody(string kind, Color tint, string title, string desc, float descWidth,
        int titleSize, int iconSize, IEnumerable<string>? extraLines)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        var icon = IconView.Of(kind, iconSize, tint);
        icon.SizeFlagsVertical = Control.SizeFlags.ShrinkBegin;
        row.AddChild(icon);
        var col = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        col.AddThemeConstantOverride("separation", 2);
        col.AddChild(UiTheme.Text(title, titleSize, tint));
        if (!string.IsNullOrEmpty(desc)) col.AddChild(UiTheme.Wrap(desc, 12, Palette.Muted, descWidth));
        foreach (var line in extraLines ?? Enumerable.Empty<string>())
            col.AddChild(UiTheme.Wrap(line, 11, Palette.Dim, descWidth));
        row.AddChild(col);
        return row;
    }

    /// <summary>
    /// 一件装备的「面板分」：攻击 3 / 防御 2 / 生命 1。
    /// 权重不是精算，是为了让「攻击 +3」和「生命 +9」能摆在一起比——
    /// 没有这把尺子，六格背包里的取舍只能靠玩家自己背数值。
    /// </summary>
    public static int ItemScore(ItemDef def) => (def.Attack ?? 0) * 3 + (def.Defense ?? 0) * 2 + (def.MaxHp ?? 0);

    /// <summary>这件装备提供什么（逐条写出来），换装面板与背包都用它</summary>
    public static string ItemGain(ItemDef def, ItemDef? versus = null)
    {
        var parts = new List<string>();
        void Add(string label, int value)
        {
            if (value == 0) return;
            var diff = versus is null ? 0 : value - FieldOf(versus, label);
            parts.Add(diff == 0 ? $"{label} +{value}" : $"{label} +{value}（{(diff > 0 ? "+" : "")}{diff}）");
        }
        Add("攻击", def.Attack ?? 0);
        Add("防御", def.Defense ?? 0);
        Add("生命", def.MaxHp ?? 0);
        if (def.Heal is { } heal) parts.Add($"回复 {heal}");
        if (def.Damage is { } dmg) parts.Add($"伤害 {dmg}");
        if (def.Cleanse == true) parts.Add("解毒");
        return parts.Count == 0 ? "没有面板加成" : string.Join(" · ", parts);
    }

    private static int FieldOf(ItemDef def, string label) => label switch
    {
        "攻击" => def.Attack ?? 0,
        "防御" => def.Defense ?? 0,
        "生命" => def.MaxHp ?? 0,
        _ => 0,
    };

    public static Color QualityColor(Quality q) => q switch
    {
        Quality.White => new Color("cfd8e6"),
        Quality.Blue => new Color("7cc0ff"),
        Quality.Purple => new Color("c39bff"),
        Quality.Orange => new Color("f2c66d"),
        _ => Palette.Text,
    };
}
