// 战斗视觉事件总线。
//
// Battle 是纯逻辑层（headless 也能跑），而伤害飘字、日志着色长在场景上。
// 两边不能直接引用，否则逻辑自检就跑不起来了。折中办法：Battle 结算时往这里发事件，
// 场景订阅后转成演出。没有订阅者时发事件就是空转，逻辑层完全无感。
namespace AbyssEcho.Systems;

public sealed class DamageNumberEvent
{
    public string Side { get; init; } = "player";
    public int Amount { get; init; }
    public string Kind { get; init; } = "hit";
}

public sealed class SkillFxEvent
{
    public string SkillId { get; init; } = "";
    public string From { get; init; } = "player";
    public string To { get; init; } = "enemy";
}

public static class BattleEvents
{
    public static event Action<DamageNumberEvent>? Damage;
    public static event Action<SkillFxEvent>? SkillFired;
    public static event Action<string>? LogLine;

    /// <summary>订阅者数量：自检用来确认「没有订阅时发事件也不会炸」</summary>
    public static int HandlerCount =>
        (Damage?.GetInvocationList().Length ?? 0)
        + (SkillFired?.GetInvocationList().Length ?? 0)
        + (LogLine?.GetInvocationList().Length ?? 0);

    public static void EmitDamageNumber(string side, int amount, string kind) =>
        Damage?.Invoke(new DamageNumberEvent { Side = side, Amount = amount, Kind = kind });

    public static void EmitSkillFx(string skillId, string from, string to) =>
        SkillFired?.Invoke(new SkillFxEvent { SkillId = skillId, From = from, To = to });

    public static void EmitLog(string line) => LogLine?.Invoke(line);

    /// <summary>场景切换时退订，避免旧场景收到下一场战斗的事件</summary>
    public static void Clear()
    {
        Damage = null;
        SkillFired = null;
        LogLine = null;
    }
}
