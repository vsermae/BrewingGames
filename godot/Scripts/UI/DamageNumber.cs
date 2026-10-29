// 伤害飘字的配色、字号与生命周期 —— 这些是「设计决策」，不是排版细节。
//
// 单独一层是因为它们要能被 headless 断言：暴击是不是比普通大 50%、
// 治疗是不是绿的、同屏是不是有上限。TS 版把这些抽成 damageNumberRules.ts 正是这个理由。
namespace AbyssEcho.UI;

using Godot;

public static class DamageNumberRules
{
    public const float DefaultDuration = 800f;
    public const float DefaultRise = 30f;

    /// <summary>
    /// 同一侧连着蹦出第二条时的错开量。
    ///
    /// 一次攻击常常同时结算「伤害 + 中毒」两条；两行字从同一个点、同一帧起飘，
    /// 屏上就读成一个认不出的字团。错开一行的代价是零。
    /// </summary>
    public const float RowStep = 26f;
    /// <summary>同屏上限：一片毒刷起来时最旧的先走，别把帧率拖死</summary>
    public const int MaxActive = 40;

    /// <summary>普通伤害红、暴击橙、治疗绿、持续伤害浅绿、Boss 大招深橙</summary>
    public static Color ColorOf(string kind) => kind switch
    {
        "crit" => new Color("ff8800"),
        "heal" => new Color("44ff44"),
        "dot" => new Color("88ff88"),
        "boss" => new Color("ff5500"),
        _ => new Color("ff4444"),
    };

    /// <summary>字号倍率：暴击大 50%，Boss 大招更大，持续伤害略小</summary>
    public static float ScaleOf(string kind) => kind switch
    {
        "crit" => 1.5f,
        "boss" => 1.6f,
        "dot" => 0.85f,
        _ => 1f,
    };

    /// <summary>基准字号：TS 版是 24px 起步，套上倍率后再往上加</summary>
    public const int BaseFontSize = 24;

    public static int FontSizeOf(string kind) => Mathf.RoundToInt(BaseFontSize * ScaleOf(kind));

    public static string TextOf(string kind, int amount) =>
        kind == "heal" ? $"+{Mathf.Abs(amount)}" : (amount > 0 ? $"-{amount}" : "0");

    /// <summary>暴击与 Boss 大招额外加粗：色盲玩家靠粗细也能分辨</summary>
    public static bool IsEmphasis(string kind) => kind is "crit" or "boss";
}
