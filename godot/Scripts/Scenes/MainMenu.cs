// 主菜单：开局、继续、用种子开局、看解锁进度。
//
// TS 版这里是一张画布 + 文字按钮；Godot 版用 Control 布局，
// 逻辑一样：「开始新的一局」永远最显眼，别让玩家在菜单里做选择题。
namespace AbyssEcho.Scenes;

using Godot;
using AbyssEcho.Core;
using AbyssEcho.Systems;
using AbyssEcho.UI;

public partial class MainMenu : Control
{
    private VBoxContainer _buttons = null!;
    private Label _progress = null!;

    // 菜单上的 M / F11：TS 版的 Electron 菜单里就有这两个，PC 版少了它们
    // 玩家会觉得「这不是个桌面程序」。
    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key) return;
        if (key.Keycode == Key.M)
        {
            Sfx.Toggle();
            RefreshButtons();
        }
        else if (key.Keycode == Key.F11)
        {
            var mode = DisplayServer.WindowGetMode();
            DisplayServer.WindowSetMode(mode == DisplayServer.WindowMode.Fullscreen
                ? DisplayServer.WindowMode.Windowed
                : DisplayServer.WindowMode.Fullscreen);
        }
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // 调试入口：godot --path godot -- --seed=123 直接开局，跳过菜单
        foreach (var arg in OS.GetCmdlineUserArgs())
        {
            if (!arg.StartsWith("--seed=")) continue;
            if (uint.TryParse(arg[7..], out var seed))
            {
                CallDeferred(nameof(StartSeed), seed);
                return;
            }
        }
        // 背景从「一块纯色 + 一张渐变贴图」换成自绘的氛围层：
        // 纵向冷光、中央辉光、四角压暗、极淡尘埃，全部在一个 _Draw 里，只画一次。
        // 这张图的层次就是玩家对「这游戏做没做完」的第一判断。
        var bg = new AmbienceBackdrop { Name = "Backdrop", Tint = new Color("6f9fe0") };
        bg.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(bg);

        // 漂浮尘埃：原版 spawnDust 那 26 粒。没有它，菜单是一块死板的渐变。
        var dust = new DustLayer { Name = "Dust", MouseFilter = MouseFilterEnum.Ignore };
        dust.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(dust);

        var center = new CenterContainer();
        center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(center);

        var col = new VBoxContainer();
        col.AddThemeConstantOverride("separation", 10);
        col.Alignment = BoxContainer.AlignmentMode.Center;
        col.CustomMinimumSize = new Vector2(420, 0);
        center.AddChild(col);

        // 字标 + 两侧装饰：原版 makeWordmark 是「每个字一块带底光的小方牌、奇偶错位」，
        // 两侧放剑与骷髅，各自往外斜开。上一版只有一行白字标题——
        // 这是玩家进游戏看到的第一屏，也是「像不像原版」最容易被判定的地方。
        var markRow = new HBoxContainer { Name = "WordmarkRow" };
        markRow.Alignment = BoxContainer.AlignmentMode.Center;
        markRow.AddThemeConstantOverride("separation", 34);
        col.AddChild(markRow);

        var mark = new Wordmark { Name = "Wordmark", CellSize = 62f, Gap = 5f, Accent = Palette.Gold, Underline = true };
        mark.Build();
        var sword = new MenuEmblem
        {
            Name = "EmblemSword", Kind = "sword", Tint = new Color("cfd8ea"),
            // 图标直径 47，旋转 0.5rad 后包围盒约 47*1.2=56，控件给 70 才不会被裁
            Diameter = 47f, Spin = 0.5f, CustomMinimumSize = new Vector2(70, 70),
        };
        var skull = new MenuEmblem
        {
            Name = "EmblemSkull", Kind = "skull", Tint = new Color("ff6b6b"),
            Diameter = 41f, Spin = 0f, CustomMinimumSize = new Vector2(56, 70),
        };
                // 顺序必须是「剑 → 字标 → 骷髅」：HBoxContainer 按加入顺序从左到右排，
        // 先加 mark 再加 sword 会让剑跑到字标右边，装饰就成了「压在字上」。
        markRow.AddChild(sword);
        markRow.AddChild(mark);
        markRow.AddChild(skull);

        var sub = UiTheme.Text("房间制 Roguelike · 一层一层往下走", 16, Palette.Muted);
        sub.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(sub);

        col.AddChild(new Control { CustomMinimumSize = new Vector2(0, 18) });

        _buttons = new VBoxContainer();
        _buttons.AddThemeConstantOverride("separation", 10);
        col.AddChild(_buttons);

        _progress = UiTheme.Text("", 13, Palette.Dim);
        _progress.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(_progress);

        var hint = UiTheme.Text("WASD / 方向键移动 · E 进入房间 · > 下楼 · I 背包", 13, Palette.Dim);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        col.AddChild(hint);

        // 菜单是场景树里的第一个界面：音频宿主挂这里，回标题时音乐不会断——
        // Attach 是幂等的，地图场景再调一次只是复用。
        Sfx.Attach(this);
        Sfx.Music(Bgm.DefaultKey);

        RefreshButtons();
    }

    /// <summary>
    /// 菜单按钮：自绘铁牌，而不是主题里的 StyleBox。
    /// 键位提示画在右侧——菜单里那句「音效：开（M）」把键位塞进正文，
    /// 于是每换一次状态整行文字就跳一下；键位本来就该固定在右边那一列。
    /// </summary>
    private static PlateButton MenuButton(string text, Action onPressed, int height, Color accent,
        bool primary = false, string keyHint = "")
    {
        var b = new PlateButton
        {
            Text = text,
            Accent = accent,
            Primary = primary,
            KeyHint = keyHint,
            CustomMinimumSize = new Vector2(0, height),
        };
        b.Pressed += onPressed;
        return b;
    }

    private void StartSeed(uint seed)
    {
        GetNode<GameStore>("/root/GameStore").StartRun(this, seed);
    }

    /// <summary>
    /// 菜单的背景辉光：一团从中心向外衰减的冷光。
    ///
    /// 一开始是手画的九层同心椭圆（TS 版那版也是），结果每一层的边都能数出来——
    /// 纯几何叠色挡不住「同心圆洋葱」。这里换成真正的径向渐变纹理：
    /// 渐变本身是连续的，缩放到全屏也不会有台阶，颜色表只写一次。
    /// </summary>
    private void DrawGlow(Control parent)
    {
        // FillTo 取画面外的远点：1.0 处已经基本透明，椭圆的长短轴比例由纹理拉伸决定
        var g = new Gradient();
        g.SetColor(0, new Color(0.58f, 0.76f, 1f, 0.16f));
        g.SetColor(1, new Color(0.58f, 0.76f, 1f, 0f));
        // 加一段中间色：只有首尾两段线性插值的话，中心那一块会显得「平」，
        // 衰减看着像贴了一张圆形的半透明纸
        g.AddPoint(0.55f, new Color(0.45f, 0.62f, 0.88f, 0.05f));

        var tex = new Godot.GradientTexture2D
        {
            Gradient = g,
            Width = 256,
            Height = 256,
            Fill = Godot.GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(1f, 0.5f),
        };
        var glow = new TextureRect
        {
            Texture = tex,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        parent.AddChild(glow);
        glow.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        glow.AnchorTop = 0f;
        glow.AnchorBottom = 0.86f;
        glow.OffsetTop = 0f;
        glow.OffsetBottom = 0f;
    }

    private void DrawVignette(Control parent)
    {
        // 背景一层渐变：纯黑太死，加一点自上而下的冷光就有「大厅」的空气感
        var gradient = new TextureRect
        {
            Texture = GradientTexture2D(),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        parent.AddChild(gradient);
        gradient.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    private static GradientTexture2D GradientTexture2D()
    {
        var g = new Gradient();
        g.SetColor(0, new Color("101a2e"));
        g.SetColor(1, new Color("070a12"));
        var tex = new GradientTexture2D { Gradient = g, Width = 8, Height = 256 };
        tex.FillFrom = new Vector2(0.5f, 0);
        tex.FillTo = new Vector2(0.5f, 1);
        return tex;
    }

    private void RefreshButtons()
    {
        // 先摘再删：QueueFree 是延迟到帧末的，同一帧里重建的话旧按钮还在容器里多留一帧
        foreach (var child in _buttons.GetChildren())
        {
            _buttons.RemoveChild(child);
            child.QueueFree();
        }

        var store = GetNode<GameStore>("/root/GameStore");
        // 主按钮：金色、比其余高一头、带余光。整屏只有它该是「最显眼的那个」——
        // 一排等高同色的按钮，玩家要在菜单里做选择题。
        _buttons.AddChild(MenuButton("开始新的一局", () => StartRun(null), 54, Palette.Gold, true));
        // 「继续」读的是盘上的存档，不是内存里的引用：
        // 玩家关掉游戏再打开，这一局依然在，否则存档这件事就没有意义。
        if (store.HasSavedRun())
        {
            _buttons.AddChild(MenuButton("继续上一局", ContinueRun, 46, Palette.Accent));
        }
        _buttons.AddChild(MenuButton("用种子开局…", AskSeed, 40, Palette.Accent, false, "S"));
        _buttons.AddChild(MenuButton("回声碎片", ShowMeta, 40, new Color("b98bff")));
        _buttons.AddChild(MenuButton("操作说明", ShowHelp, 40, Palette.AccentSoft, false, "H"));
        _buttons.AddChild(MenuButton(Sfx.Muted ? "音效：关" : "音效：开", () =>
        {
            Sfx.Toggle();
            RefreshButtons();
        }, 40, Palette.AccentSoft, false, "M"));
        _buttons.AddChild(MenuButton("退出", () => GetTree().Quit(), 40, Palette.Dim, false, "Esc"));

        var (done, total) = MetaProgression.UnlockProgress(store.Meta);
        _progress.Text = $"回声碎片 {store.Meta.Shards} · 解锁 {done}/{total}";
    }

    private void StartRun(uint? seed)
    {
        GetNode<GameStore>("/root/GameStore").StartRun(this, seed);
    }

    private void ContinueRun()
    {
        var store = GetNode<GameStore>("/root/GameStore");
        if (store.ContinueRun() is null)
        {
            RefreshButtons();
            return;
        }
        GetTree().ChangeSceneToFile("res://Scenes/Map.tscn");
    }

    private void AskSeed()
    {
        var (root, body) = Widgets.Overlay(this, "用种子开局", 420);
        var edit = new LineEdit { PlaceholderText = "整数种子，例如 20241019" };
        body.AddChild(edit);
        body.AddChild(UiTheme.Text("同一个种子会生成同一张地图、同一条随机序列，方便复现某一局。", 13, Palette.Muted));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        row.AddChild(UiTheme.Btn("开始", () =>
        {
            if (!uint.TryParse(edit.Text.Trim(), out var seed))
            {
                edit.PlaceholderText = "这不是一个非负整数";
                return;
            }
            root.QueueFree();
            StartRun(seed);
        }, 40));
        row.AddChild(UiTheme.Btn("取消", () => root.QueueFree(), 40));
        body.AddChild(row);
        edit.GrabFocus();
    }

    private void ShowMeta()
    {
        var store = GetNode<GameStore>("/root/GameStore");
        var (root, body) = Widgets.Overlay(this, "回声碎片与解锁", 620);
        body.AddChild(UiTheme.Text($"当前碎片：{store.Meta.Shards}", 16, Palette.Gold));
        foreach (var (id, info) in MetaProgression.UnlockInfo)
        {
            var cost = MetaProgression.UnlockCost[id];
            var owned = MetaProgression.HasUnlock(store.Meta, id);
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 10);
            var col = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            col.AddChild(UiTheme.Text($"{info.Name}（第 {info.Tier} 档）", 15, owned ? Palette.Good : Palette.Text));
            col.AddChild(UiTheme.Text(info.Desc, 12, Palette.Muted));
            row.AddChild(col);
            if (owned)
            {
                row.AddChild(UiTheme.Text("已解锁", 14, Palette.Good));
            }
            else
            {
                var btn = UiTheme.Btn($"{cost} 碎片", () =>
                {
                    // 走 GameStore 的那条路：解锁要同时改「界面读的那一份」和
                    // 「局里握着的那一份」，否则下一局挣到一块碎片就会把它写回旧值。
                    if (!store.TryUnlock(id)) return;
                    root.QueueFree();
                    ShowMeta();
                }, 36);
                btn.Disabled = store.Meta.Shards < cost;
                row.AddChild(btn);
            }
            body.AddChild(row);
        }
        body.AddChild(UiTheme.Text("碎片只买「多一种选择」和「内容更早出现」，不买数值——不然第一局就没意义了。", 12, Palette.Dim));
        body.AddChild(UiTheme.Btn("关闭", () => root.QueueFree(), 40));
    }

    /// <summary>说明直接渲染 Keymap：键位表是数据，说明与输入处理共用一份</summary>
    private void ShowHelp()
    {
        var (root, body) = Widgets.Overlay(this, "操作说明", 640);
        foreach (var (title, list) in new[] { ("地图", Keymap.Map), ("战斗", Keymap.Battle) })
        {
            body.AddChild(UiTheme.Text(title, 15, Palette.Gold));
            foreach (var b in list)
            {
                var row = new HBoxContainer();
                row.AddThemeConstantOverride("separation", 12);
                var key = UiTheme.Text(b.Keys, 13, Palette.AccentSoft);
                key.CustomMinimumSize = new Vector2(150, 0);
                row.AddChild(key);
                row.AddChild(UiTheme.Wrap(b.Action, 13, Palette.Text, 420));
                body.AddChild(row);
            }
        }
        body.AddChild(UiTheme.Wrap("每一层都有一条楼层机制写在左上角：中毒、破防、闪避、自爆——\n它们不是数值放大，是逼你换打法。", 13, Palette.Muted, 560));
        // 三条「玩过一局才懂、但第一局就该知道」的规矩。
        //
        // 它们以前只存在于实现里：玩家在祭坛上看见「-6 生命上限」不知道这笔账划不划算，
        // 在战后三选一里以为「全带走」，在商店里以为深层的贵价装备会自己变便宜。
        // 这张卡是唯一一处玩家会主动打开来读的地方，把三件事写清楚比加一段教程便宜。
        body.AddChild(UiTheme.Text("三条规矩", 15, Palette.Gold));
        foreach (var tip in new[]
                 {
                     "祭坛的血祭：付掉的是「生命上限」，换来的攻击力一局有效——上限在整局里不会自己长回来，除了升级。",
                     "战后三选一与宝箱都只能带走一件；金币与回血是另外两条独立的路。",
                     "背包只有 8 格：满仓时买进来的东西要先放下一样，先想好舍哪样。",
                 })
        {
            body.AddChild(UiTheme.Wrap("· " + tip, 13, Palette.Text, 560));
        }
        // 这一轮新写进地图的两句话，同样是「第一局就该知道」的：它们不解释的话，
        // 玩家会把「看不清那边是什么」当成 bug，也会把「剩下的仗约 N 点血」当成装饰。
        body.AddChild(UiTheme.Text("地图怎么读", 15, Palette.Gold));
        foreach (var tip in new[]
                 {
                     "地图上的问号是没去过的地方：商店、宝箱、事件会亮出图标，而敌人房与精英房在雾里看不出来——代价要走过去才知道。",
                     "底部那行「剩余战斗房 3（下楼还差 1 场）」里的后者才是这一层的票价；括号里那个数才是躲不过的。",
                     "「剩下的仗约 N 点血」是按你现在的面板估的：它算的是每条岔路底下还压着的仗，不是全图。",
                 })
        {
            body.AddChild(UiTheme.Wrap("· " + tip, 13, Palette.Text, 560));
        }
        body.AddChild(UiTheme.Btn("知道了", () => root.QueueFree(), 40));
    }
}
