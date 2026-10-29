// 角色状态图标行：立绘下方一排小图标，右上角标剩余回合。
//
// 状态从哪来？**不在界面里猜**——Combatant 上的 PoisonTurns / AtkMod / StunTurns
// 这些字段就是唯一真相。TS 版也是这么做的（statusIcons 只负责画，不负责判断）。
//
// 没有专属造型的状态退化成「圆圈 + 首字母」，和 TS 版一致：
// 宁可显示一个看不懂的字母，也不要静默吞掉一个正在生效的状态。
namespace AbyssEcho.UI;

using Godot;
using AbyssEcho.Core;

/// <summary>一个「身上带着的状态」：id 决定图标，turns 决定右上角数字</summary>
public sealed record ActiveStatus(string Id, int Label, string Name);

public static class StatusIcons
{
    public const int IconSize = 22;
    public const int Gap = 4;

    public sealed record Def(string Name, string Icon, Color Color);

    /// <summary>对照 TS 版 statusIconDefs.ts。说明文字每次由 Combatant 现场带数值进来。</summary>
    public static readonly Dictionary<string, Def> Defs = new()
    {
        // 中毒用骷髅而不是毒药瓶：毒药瓶是「解毒草」这一类道具的造型，
        // 两者都画成瓶子的话，状态栏里的减益和背包里的解药长得一样。
        ["poison"] = new("中毒", "skull", new Color("8ff0bd")),
        ["burn"] = new("灼烧", "fire", new Color("ff9f45")),
        ["frost"] = new("冰封", "sparkle", new Color("7fd4ff")),
        ["stoneShield"] = new("石肤", "shield", new Color("8b7355")),
        ["defend"] = new("防御", "shield", new Color("5aa9ff")),
        ["empowered"] = new("强化", "muscle", new Color("f2c14e")),
        ["stunned"] = new("眩晕", "rune", new Color("c39bff")),
        ["evade"] = new("闪避", "swap", new Color("8ce2ff")),
        ["atkDown"] = new("攻击下降", "sword", new Color("ff8f8f")),
        ["defDown"] = new("防御下降", "armor", new Color("ff8f8f")),
    };

    /// <summary>
    /// 从单位身上读状态。顺序固定（中毒、灼烧、冰封、防御…），
    /// 否则同一组状态每帧顺序不同，玩家的眼睛跟不上。
    /// </summary>
    public static List<ActiveStatus> Read(Combatant c)
    {
        var list = new List<ActiveStatus>();
        if (c.PoisonTurns > 0) list.Add(new ActiveStatus("poison", c.PoisonTurns, $"中毒 {c.PoisonPower}/回合"));
        if (c.AtkMod < 0 && c.AtkModTurns > 0) list.Add(new ActiveStatus("atkDown", c.AtkModTurns, $"攻击 {c.AtkMod}"));
        if (c.DefMod < 0 && c.DefModTurns > 0) list.Add(new ActiveStatus("defDown", c.DefModTurns, $"防御 {c.DefMod}"));
        if (c.AtkMod > 0 && c.AtkModTurns > 0) list.Add(new ActiveStatus("empowered", c.AtkModTurns, $"攻击 +{c.AtkMod}"));
        // 石肤：增益给的是「防御 +N」，和防御姿态是两回事，图标也分开。
        if (c.DefMod > 0 && c.DefModTurns > 0) list.Add(new ActiveStatus("stoneShield", c.DefModTurns, $"石肤：防御 +{c.DefMod}"));
        if (c.Defending) list.Add(new ActiveStatus("defend", 1, "本回合伤害减半"));
        if (c.Evade > 0 && c.Evade > c.BaseEvade + 0.001) list.Add(new ActiveStatus("evade", 1, $"闪避 {(int)Math.Round(c.Evade * 100)}%"));
        if (c.StunTurns > 0) list.Add(new ActiveStatus("stunned", c.StunTurns, "下一回合无法行动"));
        if (c.AttackMultiplier > 1.001) list.Add(new ActiveStatus("empowered", 1, $"下一击 ×{c.AttackMultiplier:0.0}"));
        return list;
    }

    public static float RowWidth(int count) => count <= 0 ? 0f : count * IconSize + (count - 1) * Gap;
}

/// <summary>一排状态图标的绘制体。状态变了就 Refresh，内部重排宽度。</summary>
public partial class StatusIconRow : Control
{
    private List<ActiveStatus> _statuses = new();
    private List<TooltipArea> _tips = new();

    public StatusIconRow()
    {
        MouseFilter = MouseFilterEnum.Pass;
    }

    public IReadOnlyList<ActiveStatus> Statuses => _statuses;

    public void Refresh(List<ActiveStatus> statuses)
    {
        _statuses = statuses;
        foreach (var t in _tips)
        {
            RemoveChild(t);
            t.QueueFree();
        }
        _tips.Clear();
        var w = StatusIcons.RowWidth(statuses.Count);
        // 空行必须把最小尺寸归零：一旦写过一次非零值，容器就永远给它留那一行的位置，
        // 行动条因此凭空高出一截，把日志与按钮往下挤（截图里底部那片空白就是它）。
        Size = new Vector2(Mathf.Max(0f, w), statuses.Count > 0 ? StatusIcons.IconSize : 0f);
        CustomMinimumSize = Size;
        Visible = statuses.Count > 0;
        // 悬停提示：中心点对齐这一行，图标多时自动变宽
        for (var i = 0; i < statuses.Count; i++)
        {
            var st = statuses[i];
            var def = StatusIcons.Defs.TryGetValue(st.Id, out var d) ? d : null;
            var tipText = $"{def?.Name ?? st.Id}：{st.Name}";
            var hit = new TooltipArea
            {
                Position = new Vector2(i * (StatusIcons.IconSize + StatusIcons.Gap), 0),
                Size = new Vector2(StatusIcons.IconSize, StatusIcons.IconSize),
                TooltipText = tipText,
                MouseFilter = MouseFilterEnum.Pass,
            };
            AddChild(hit);
            _tips.Add(hit);
        }
        QueueRedraw();
    }

    public override void _Draw()
    {
        for (var i = 0; i < _statuses.Count; i++)
        {
            var st = _statuses[i];
            var x = i * (StatusIcons.IconSize + StatusIcons.Gap) + StatusIcons.IconSize * 0.5f;
            var c = new Vector2(x, StatusIcons.IconSize * 0.5f);
            if (StatusIcons.Defs.TryGetValue(st.Id, out var def))
            {
                DrawSetTransform(c, 0, Vector2.One);
                IconArt.Draw(this, def.Icon, StatusIcons.IconSize - 4, def.Color);
                DrawSetTransform(Vector2.Zero, 0, Vector2.One);
                // 剩余回合标在右上角
                var font = UiTheme.FallbackFont();
                var label = st.Label.ToString();
                DrawString(font, c + new Vector2(StatusIcons.IconSize * 0.5f - 1, -StatusIcons.IconSize * 0.5f + 9),
                    label, HorizontalAlignment.Right, -1, 10, Colors.White);
            }
            else
            {
                // 兜底：圆圈 + 首字母，绝不让一个正在生效的状态静默消失
                Paint.Circle(this, c, StatusIcons.IconSize * 0.5f, new Color(0.05f, 0.07f, 0.13f, 0.95f));
                Paint.Ring(this, c, StatusIcons.IconSize * 0.5f, new Color("93a0be"), 1.2f);
                DrawString(UiTheme.FallbackFont(), c + new Vector2(-4, 4),
                    st.Id.Substring(0, 1).ToUpperInvariant(), HorizontalAlignment.Left, -1, 11, new Color("93a0be"));
            }
        }
    }
}

/// <summary>只有 tooltip 的透明命中区：状态图标本身不该吃掉点击，但悬停要能出说明</summary>
public partial class TooltipArea : Control
{
    public override void _Ready() => MouseFilter = MouseFilterEnum.Pass;
}

/// <summary>飘字层：一个节点画出所有飘字，数量超上限时最旧的先走。</summary>
public partial class DamageNumberLayer : Control
{
    private sealed class Entry
    {
        public string Text = "";
        public Color Color;
        public float Age;
        public float Duration;
        public Vector2 Origin;
        public int FontSize;
        public bool Emphasis;
    }

    private readonly List<Entry> _entries = new();

    public DamageNumberLayer()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        // 飘字要压在人物上、但不许盖过面板（见 UI/Layers.cs）
        ZIndex = Layers.Of(StageOrder.Float);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    public int ActiveCount => _entries.Count;

    /// <summary>弹一个飘字。amount 是伤害值，kind 决定颜色与字号。</summary>
    public void Spawn(string kind, int amount, Vector2 origin)
    {
        if (_entries.Count >= DamageNumberRules.MaxActive) _entries.RemoveAt(0);
        _entries.Add(new Entry
        {
            Text = DamageNumberRules.TextOf(kind, amount),
            Color = DamageNumberRules.ColorOf(kind),
            Duration = DamageNumberRules.DefaultDuration / 1000f,
            Origin = origin,
            FontSize = DamageNumberRules.FontSizeOf(kind),
            Emphasis = DamageNumberRules.IsEmphasis(kind),
        });
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (_entries.Count == 0) return;
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            _entries[i].Age += (float)delta;
            if (_entries[i].Age >= _entries[i].Duration) _entries.RemoveAt(i);
        }
        QueueRedraw();
    }

    public override void _Draw()
    {
        var font = UiTheme.FallbackFont();
        foreach (var e in _entries)
        {
            var t = Mathf.Clamp(e.Age / e.Duration, 0f, 1f);
            // 上升 30px + 后三分之一淡出：观感上「冒出来、飘走、消失」。
            // 起步用 `t²`：第一帧几乎不动、随后加速，读起来是「被打出去」而不是
            // 「匀速上浮」——匀速是最像 PPT 的那种动。
            var rise = t * t;
            var y = e.Origin.Y - DamageNumberRules.DefaultRise * rise;
            var alpha = t < 0.66f ? 1f : 1f - (t - 0.66f) / 0.34f;
            var col = Paint.Alpha(e.Color, Mathf.Clamp(alpha, 0f, 1f));
            // 起步略小、中段顶到满号：`1 + 0.22t - 0.22t²` 在 t=0.5 附近高于 1，
            // 读起来是「一下蹦出来」；比全程固定字号多出半帧的力。
            var pop = 1f + 0.22f * t - 0.22f * t * t;
            var size = Mathf.RoundToInt(e.FontSize * pop);
            var pos = new Vector2(e.Origin.X - e.Text.Length * e.FontSize * 0.28f, y);
            // 描边一圈深色，浅色背景上也读得清
            DrawStringOutline(font, pos, e.Text, HorizontalAlignment.Left, -1, size, 4, new Color(0, 0, 0, alpha * 0.8f));
            DrawString(font, pos, e.Text, HorizontalAlignment.Left, -1, size, col);
        }
    }
}
