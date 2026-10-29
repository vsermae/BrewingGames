// Boss 出场演出：进 Boss 战之前先播一段专属入场。
//
// TS 版 BossIntroScene 是 333 行纯演出：三只 Boss 三种入场（熔岩从底部升起、
// 霜蚀四角冰晶汇聚、深渊中央漩涡展开），震动幅度各异，空格随时跳过。
// 它不承载任何规则——但也正因为如此，「玩家分不出是不是原版」这条验收标准
// 绕不开它：直接切进战斗，玩家会觉得「少了一段」。
//
// 这里用 Tween + 一个 _Draw 节点重做。立绘复用 MonsterSprite，
// 保证「出场的这位」和「挨打的这位」是同一个造型（TS 版也是这么做的）。
namespace AbyssEcho.UI;

using Godot;

public sealed record BossIntroDef(
    string Name,
    string Subtitle,
    float EntryMs,
    float Shake,
    string NameFx,
    string Entry,
    string ColorHex);

public static class BossIntroData
{
    public const float TotalMs = 2400f;
    public const float FadeMs = 500f;

    private static readonly Dictionary<string, BossIntroDef> Intros = new()
    {
        ["boss_molten"] = new("熔岩领主", "深渊之火的化身", 1100f, 3f, "fire", "rise", "ff7a2a"),
        ["boss_frost"] = new("霜蚀女王", "永冻之境的统治者", 1000f, 6f, "frost", "converge", "7fd4ff"),
        ["boss_abyss"] = new("深渊之主", "虚空深处的凝视", 1300f, 10f, "pulse", "vortex", "a97bff"),
        // 逆位：名字反过来写、配色转金——玩家在进场那一刻就知道这局不一样
        ["boss_abyss_reversed"] = new("之主宰·渊深", "回响被倒置，力量翻倍", 1500f, 14f, "pulse", "vortex", "ffd166"),
    };

    public static BossIntroDef Of(string bossId) =>
        Intros.TryGetValue(bossId, out var d) ? d : Intros["boss_molten"];

    public static IReadOnlyDictionary<string, BossIntroDef> All => Intros;
}

public partial class BossIntro : Control
{
    private string _bossId = "boss_molten";
    private BossIntroDef _def = BossIntroData.Of("boss_molten");
    private float _elapsed;
    private bool _finished;
    private bool _nameRevealed;

    private ColorRect _fade = null!;
    private Control _stage = null!;
    private MonsterSprite _sprite = null!;
    private Label _nameLabel = null!;
    private Label _subLabel = null!;
    private VBoxContainer _nameBox = null!;
    private FxLayer _fx = null!;

    /// <summary>演出结束（或被跳过）时回调，交给 MapScene 切战斗</summary>
    public event Action? Finished;

    public void Configure(string bossId) => _bossId = bossId;

    // ---- 截图自证用（--shot 读它们；正常运行路径不会碰）----

    /// <summary>名字栏上的字。空的 / 不是这一位的名字，就等于「这一屏不是他的出场」</summary>
    public string BossNameText => _nameLabel is null ? "" : _nameLabel.Text;

    /// <summary>名字的可见度：入场 1.3s 之后才淡到 1，早拍就是半透明的</summary>
    public float NameAlpha => _nameLabel is null ? 0f : _nameLabel.Modulate.A;

    /// <summary>这个 Boss 在名字栏上该显示的名字——截图工具拿它核对，不靠人记</summary>
    public static string NameOf(string bossId) => BossIntroData.Of(bossId).Name;

    public override void _Ready()
    {
        _def = BossIntroData.Of(_bossId);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = Layers.Of(StageOrder.Overlay);

        // 底不是一块纯黑：径向深渊 + 三圈同心环 + 地平雾 + 四角框。
        // 一屏戏的分量几乎全靠背景给——同样的剪影放在有纵深的光里是「它从深处升起来」，
        // 放在纯黑上只是「一块贴纸」。
        var backdrop = new AbyssStage { Tint = new Color(_def.ColorHex) };
        AddChild(backdrop);
        backdrop.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _stage = new Control { MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_stage);
        _stage.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        _fx = new FxLayer();
        _stage.AddChild(_fx);

        var vp = GetViewportRect().Size;
        var size = Mathf.Min(240f, vp.Y * 0.4f);
        _sprite = new MonsterSprite();
        _stage.AddChild(_sprite);
        _sprite.Setup(_bossId, size, true);
        _sprite.PivotOffset = _sprite.Size * 0.5f;
        _sprite.Size = new Vector2(size * 1.4f, size * 1.5f);
        _sprite.PivotOffset = _sprite.Size * 0.5f;

        var target = new Vector2(vp.X * 0.5f - _sprite.Size.X * 0.5f, vp.Y * 0.44f - _sprite.Size.Y * 0.5f);
        PlayEntry(target, size, vp);
        PlayParticles(vp, new Vector2(vp.X * 0.5f, vp.Y * 0.44f));

        // 名字区用 CenterContainer 定位：以前是「给 Label 写 Position + Size」，
        // 但 Label 的尺寸要等字体排版完才算得出来，写死的坐标会把标题和副标题叠在一起。
        var nameBox = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        _stage.AddChild(nameBox);
        nameBox.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
        nameBox.OffsetTop = -200f;
        nameBox.OffsetBottom = -40f;
        nameBox.AddThemeConstantOverride("separation", 6);

        _nameLabel = UiTheme.Text(_def.Name, 46, new Color(_def.ColorHex));
        _nameLabel.HorizontalAlignment = HorizontalAlignment.Center;
        nameBox.AddChild(_nameLabel);
        // 名字下面压一条渐隐的暗带：立绘的光晕会飘到字上，直接把字盖成灰。
        // 这条带子从透明渐到近黑，字就永远落在「暗」的那一侧。
        var nameScrim = new NameScrim { Tint = new Color(_def.ColorHex) };
        _stage.AddChild(nameScrim);
        nameScrim.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
        nameScrim.OffsetTop = -230f;
        nameScrim.OffsetBottom = 0f;
        _stage.MoveChild(nameScrim, 1);

        _subLabel = UiTheme.Text(_def.Subtitle, 16, Palette.Muted);
        _subLabel.HorizontalAlignment = HorizontalAlignment.Center;
        nameBox.AddChild(_subLabel);
        // 名字一开始是「没出现」的：入场到位后才淡入
        nameBox.Modulate = new Color(1, 1, 1, 0);
        _nameBox = nameBox;

        // 渐入：从全黑慢慢显出内容
        _fade = new ColorRect { Color = new Color(0, 0, 0, 1), MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_fade);
        _fade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var fadeTween = CreateTween();
        fadeTween.TweenProperty(_fade, "color:a", 0.0, BossIntroData.FadeMs / 1000f);
        fadeTween.TweenCallback(Callable.From(() => _fade.Visible = false));

        Sfx.Attach(this);
        Sfx.Play("boss");

        // 立绘到位 → 出名字 → 震一下
        var nameTimer = GetTree().CreateTimer(_def.EntryMs / 1000f);
        nameTimer.Timeout += RevealName;

        // 整体过渡到战斗
        var endTimer = GetTree().CreateTimer(BossIntroData.TotalMs / 1000f);
        endTimer.Timeout += Finish;
    }

    public override void _Process(double delta)
    {
        _elapsed += (float)delta * 1000f;
        // 兜底：计时器万一被跳过，别把玩家卡在黑屏
        if (!_finished && _elapsed > BossIntroData.TotalMs + 800f) Finish();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        // 空格随时跳过：刷第 15 层时不会想每把都看三秒动画
        if (@event is InputEventKey { Pressed: true, Echo: false } k &&
            (k.Keycode == Key.Space || k.IsAction("interact")))
            Finish();
    }

    private void PlayEntry(Vector2 target, float size, Vector2 vp)
    {
        switch (_def.Entry)
        {
            case "rise":
            {
                // 从屏幕底部缓缓升起
                _sprite.Position = new Vector2(target.X, vp.Y + size * 0.5f);
                _sprite.Modulate = new Color(1, 1, 1, 0);
                var t = CreateTween().SetParallel(true);
                t.TweenProperty(_sprite, "position:y", target.Y, _def.EntryMs / 1000f)
                    .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
                t.TweenProperty(_sprite, "modulate:a", 1.0, _def.EntryMs / 1000f);
                break;
            }
            case "converge":
            {
                // 四角冰晶向中心汇聚，到位后立绘显形
                var corners = new[] { Vector2.Zero, new Vector2(vp.X, 0), new Vector2(0, vp.Y), new Vector2(vp.X, vp.Y) };
                foreach (var c in corners)
                {
                    var shard = new ShardNode { Position = c, Color = new Color("7fd4ff") };
                    _stage.AddChild(shard);
                    var t = CreateTween().SetParallel(true);
                    t.TweenProperty(shard, "position", target + _sprite.Size * 0.5f, _def.EntryMs / 1000f)
                        .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
                    t.TweenProperty(shard, "modulate:a", 0.0, _def.EntryMs / 1000f);
                    t.Chain().TweenCallback(Callable.From(() => shard.QueueFree()));
                }
                _sprite.Position = target;
                _sprite.Modulate = new Color(1, 1, 1, 0);
                _sprite.Scale = new Vector2(0.4f, 0.4f);
                var st = CreateTween().SetParallel(true);
                st.TweenProperty(_sprite, "modulate:a", 1.0, _def.EntryMs / 1000f);
                st.TweenProperty(_sprite, "scale", Vector2.One, _def.EntryMs / 1000f)
                    .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);
                break;
            }
            default:
            {
                // 中央漩涡：先扩散一圈能量环，立绘从中心旋转展开
                var ring = new RingNode { Position = target + _sprite.Size * 0.5f, Color = new Color("8f4dff") };
                _stage.AddChild(ring);
                var rt = CreateTween().SetParallel(true);
                rt.TweenProperty(ring, "scale", new Vector2(9, 9), _def.EntryMs / 1000f)
                    .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
                rt.TweenProperty(ring, "modulate:a", 0.0, _def.EntryMs / 1000f);
                rt.Chain().TweenCallback(Callable.From(() => ring.QueueFree()));

                _sprite.Position = target;
                _sprite.Modulate = new Color(1, 1, 1, 0);
                _sprite.Scale = new Vector2(0.2f, 0.2f);
                _sprite.Rotation = -Mathf.Pi;
                var st = CreateTween().SetParallel(true);
                st.TweenProperty(_sprite, "modulate:a", 1.0, _def.EntryMs / 1000f);
                st.TweenProperty(_sprite, "scale", Vector2.One, _def.EntryMs / 1000f)
                    .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
                st.TweenProperty(_sprite, "rotation", 0.0, _def.EntryMs / 1000f)
                    .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
                break;
            }
        }
    }

    /// <summary>伴随粒子：火焰上升 / 冰晶飘落 / 暗紫能量环</summary>
    private void PlayParticles(Vector2 vp, Vector2 center)
    {
        var rng = new Random(4649);
        var list = new List<FxParticle>();
        FxParticle P() => new() { Alive = true, Gravity = 0f, BaseAlpha = 1f, Shape = FxShape.Circle };
        switch (_def.Entry)
        {
            case "rise":
                for (var i = 0; i < 20; i++)
                {
                    var p = P();
                    p.Pos = new Vector2((float)rng.NextDouble() * vp.X, vp.Y + 10);
                    p.Vel = new Vector2((float)(rng.NextDouble() - 0.5) * 0.06f, -0.24f);
                    p.Life = 900 + (float)rng.NextDouble() * 800;
                    p.Size = 2 + (float)rng.NextDouble() * 3;
                    p.Color = new Color("ff7a2a");
                    p.BaseAlpha = 0.5f + (float)rng.NextDouble() * 0.45f;
                    list.Add(p);
                }
                break;
            case "converge":
                for (var i = 0; i < 24; i++)
                {
                    var p = P();
                    p.Pos = new Vector2((float)rng.NextDouble() * vp.X, -10);
                    p.Vel = new Vector2((float)(rng.NextDouble() - 0.5) * 0.05f, 0.30f);
                    p.Life = 1200 + (float)rng.NextDouble() * 1000;
                    p.Size = 1.5f + (float)rng.NextDouble() * 2;
                    p.Color = new Color("7fd4ff");
                    p.BaseAlpha = 0.4f + (float)rng.NextDouble() * 0.5f;
                    list.Add(p);
                }
                break;
            default:
                // 暗紫能量环：从立绘周围一圈圈扩散
                for (var i = 0; i < 5; i++)
                {
                    var ring = new RingNode { Position = center, Color = new Color("8f4dff") };
                    ring.Modulate = new Color(1, 1, 1, 0.7f);
                    _stage.AddChild(ring);
                    var t = CreateTween().SetParallel(true);
                    t.TweenProperty(ring, "scale", new Vector2(2.4f, 2.4f), 0.9)
                        .SetDelay(i * 0.18).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
                    t.TweenProperty(ring, "modulate:a", 0.0, 0.9).SetDelay(i * 0.18);
                    t.Chain().TweenCallback(Callable.From(() => ring.QueueFree()));
                }
                break;
        }
        if (list.Count > 0) _fx.Emit(list);
        _fx.SetMeta("intro_particles", list.Count);
    }

    /// <summary>名称特效：火焰燃烧 / 结霜 / 脉动光晕</summary>
    private void RevealName()
    {
        if (_finished || _nameRevealed) return;
        _nameRevealed = true;

        var t = CreateTween().SetParallel(true);
        t.TweenProperty(_nameBox, "modulate:a", 1.0, 0.38);
        // 上浮 8px：字「顶上来」比直接淡入更有分量
        // 位移放在内层 VBox 上：_nameBox 是 BottomWide 锚点，它的 position 由锚点每帧重算，
        // 直接 tween 它的 position 会被布局系统覆盖掉（看起来就像动画没生效）。
        _nameLabel.Position = new Vector2(_nameLabel.Position.X, 8f);
        t.TweenProperty(_nameLabel, "position:y", 0f, 0.38)
            .SetTrans(Tween.TransitionType.Back).SetEase(Tween.EaseType.Out);

        switch (_def.NameFx)
        {
            case "fire":
            {
                // 火焰燃烧：颜色在橙→黄之间跳，字号也抖，像火舌在舔字
                var colors = new[] { new Color("ff7a2a"), new Color("ff9f45"), new Color("ffd08a"), new Color("ff5500") };
                var i = 0;
                // 用 Tween 的间隔回调推进闪烁：CreateTimer 的一次性语义在递归里
                // 很容易漏掉「下一次」的注册，播到一半就不闪了。
                var flicker = CreateTween();
                for (var k = 0; k < 12; k++)
                {
                    flicker.TweenInterval(0.11);
                    flicker.TweenCallback(Callable.From(() =>
                    {
                        if (_finished || !GodotObject.IsInstanceValid(_nameLabel)) return;
                        _nameLabel.AddThemeColorOverride("font_color", colors[i % colors.Length]);
                        _nameLabel.AddThemeFontSizeOverride("font_size", 44 + i % 3 * 3);
                        i++;
                    }));
                }
                break;
            }
            case "frost":
                // 结霜：整体反复「糊」一下
                var ft = CreateTween();
                ft.SetLoops(3);
                ft.TweenProperty(_nameLabel, "modulate:a", 0.82, 0.62);
                ft.TweenProperty(_nameLabel, "modulate:a", 1.0, 0.62);
                break;
            default:
                // 脉动光晕：整块字呼吸
                _nameLabel.Resized += () => _nameLabel.PivotOffset = _nameLabel.Size * 0.5f;
                _nameLabel.PivotOffset = _nameLabel.Size * 0.5f;
                var pt = CreateTween();
                pt.SetLoops(3);
                pt.TweenProperty(_nameLabel, "scale", new Vector2(1.08f, 1.08f), 0.62)
                    .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
                pt.TweenProperty(_nameLabel, "scale", Vector2.One, 0.62)
                    .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
                break;
        }

        // 震屏：靠摄影机偏移实现，比挪所有对象省事
        var cam = GetViewport().GetCamera2D();
        if (cam is not null)
        {
            var amp = _def.Shake;
            var shake = CreateTween();
            for (var k = 0; k < 4; k++)
            {
                shake.TweenProperty(cam, "offset",
                    new Vector2((float)GD.RandRange(-amp, amp), (float)GD.RandRange(-amp, amp)), 0.05);
            }
            shake.TweenProperty(cam, "offset", Vector2.Zero, 0.06);
        }
    }

    /// <summary>结束：黑幕淡出后切战斗。重复调用只生效一次</summary>
    public void Finish()
    {
        if (_finished) return;
        _finished = true;
        var t = CreateTween();
        t.TweenProperty(_fade, "color:a", 1.0, 0.26);
        t.TweenCallback(Callable.From(() => Finished?.Invoke()));
    }
}

/// <summary>一片向中心飞的冰晶碎片</summary>
public partial class ShardNode : Control
{
    public Color Color = Colors.White;

    public ShardNode()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        Paint.Tri(this, Vector2.Zero, new Vector2(26, 8), new Vector2(10, 30), Paint.Alpha(Color, 0.5f));
    }
}

/// <summary>一圈扩散的能量环（漩涡与深渊主子的粒子都用它）</summary>
public partial class RingNode : Control
{
    public Color Color = Colors.White;

    public RingNode()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        Paint.Ring(this, Vector2.Zero, 20, Paint.Alpha(Color, 0.8f), 4f);
    }
}

/// <summary>
/// 名字区背后那条渐隐暗带：上透明、下近黑。
/// 立绘的光晕会飘到标题上，直接把字洗成灰色——有了它，字永远落在暗的一侧。
/// </summary>
public partial class NameScrim : Control
{
    public Color Tint = Palette.Boss;

    public NameScrim()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        var w = Size.X;
        var h = Size.Y;
        if (w <= 1f || h <= 1f) return;
        Paint.VGradient(this, new Rect2(0, 0, w, h), new Color(0, 0, 0, 0f), new Color(0.02f, 0.02f, 0.04f, 0.92f), 16);
        // 名字两侧各一道 accent 短横：标题不是「一行字」，是「一个牌子」
        var y = h * 0.62f;
        for (var i = 0; i < 2; i++)
        {
            var dir = i == 0 ? -1f : 1f;
            var x = w * 0.5f + dir * w * 0.20f;
            DrawLine(new Vector2(x, y), new Vector2(x + dir * 52f, y), Paint.Alpha(Tint, 0.55f), 1.6f);
        }
    }
}
