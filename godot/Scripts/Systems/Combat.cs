// 伤害公式的唯一实现。技能、普攻、敌人反击都走这里，
// 这样「敌人卡上的预告」与「实际挨的打」才有机会一致。
namespace AbyssEcho.Systems;

using AbyssEcho.Core;

public struct DamageResult
{
    public int Damage;
    public bool Crit;
    public bool AvoidedDefense;
    /// <summary>
    /// 「这一下是玩家亲手打出去的」，与「环境 / 毒 / 反震打回来的」分开。
    ///
    /// 它会经过 `HurtPlayer` 这条对所有非攻击伤害都开放的私路，而那条路上挂着
    /// 「中毒每层最多毒死玩家 N 次」的预算——预算是拿来让毒偶尔真的杀人的，
    /// 不是拿来替一颗玩家自己投出的炸弹挡刀的。以前没有这个标记，
    /// 于是预算用完之后，玩家扔炸弹炸死自己的那一下会被兜成「只剩 1 点」。
    /// </summary>
    public bool FromPlayer;
}

public sealed class CombatRules
{
    public double CritChance { get; init; }
    public double CritMultiplier { get; init; }
    public double DefendReduction { get; init; }
    public double DamageVariance { get; init; }
}

/// <summary>
/// 词条钩子的可选注入：暴击率、风险词条受伤加成。
/// Battle 构造时把玩家当前词条视图传进来，这里只负责「按词条改结算」。
/// </summary>
public sealed class AffixHooks
{
    public ActiveAffixes Affixes { get; init; } = ActiveAffixes.Empty;
    public double CritBonus { get; init; }
    public bool TargetIsPlayer { get; init; }
    /// <summary>
    /// 受伤侧乘区（风险词条的每层首次、脆弱躯壳）。由 Battle 传进来而不是在这里判：
    /// 敌人攻击的那条链在伤害值进 ComputeDamage 之前就已经乘过一次了，
    /// 这里再判一遍等于同一句说明收两次钱。
    /// </summary>
    public double IncomingScale { get; init; } = 1;
    /// <summary>攻击方是敌人时的额外暴击率（周目负面效果「暴戾」），与 CritBonus 分开</summary>
    public double EnemyCritBonus { get; init; }
}

public static class CombatMath
{
    /// <summary>
    /// damage = attacker.atk * random(1-v, 1+v) - defender.def
    /// 防御状态使最终伤害减半，暴击乘 1.5，最低 1 点。
    /// </summary>
    public static DamageResult ComputeDamage(
        Combatant attacker,
        Combatant defender,
        double attackerAtk,
        int defenderDef,
        Rng rng,
        CombatRules rules,
        AffixHooks? hooks = null)
    {
        var variance = rng.Range(1 - rules.DamageVariance, 1 + rules.DamageVariance);
        // 暴击率：基础值 + 攻击方加成，上限 100%。
        // 玩家的加成走 CritBonus，敌人的加成走 EnemyCritBonus，两者互不串台。
        var critChance = hooks is null
            ? rules.CritChance
            : Math.Min(1, rules.CritChance + hooks.CritBonus + hooks.EnemyCritBonus);
        var damage = attackerAtk * attacker.AttackMultiplier * variance - defenderDef;
        var crit = rng.Chance(critChance);
        if (crit) damage *= rules.CritMultiplier;
        if (defender.Defending) damage *= 1 - rules.DefendReduction;
        // 受伤侧词条只有一条路：Battle.IncomingScale()。
        // 这里早先自己判了一遍风险词条与脆弱躯壳，而 Battle 在传伤害值进来之前
        // 已经乘过一次同样的乘区——于是「每层首次 +20% → 24%」「脆弱躯壳 +15% → 32%」，
        // 伤害翻了倍，而说明里只写了一次。
        // 传进来的 attackerAtk 已经是「算完所有修正的攻击力」，这里只负责浮动/暴击/防御/姿态。
        if (hooks is { TargetIsPlayer: true }) damage *= hooks.IncomingScale;
        return new DamageResult
        {
            Damage = Math.Max(1, (int)Math.Round(damage)),
            Crit = crit,
            AvoidedDefense = false,
        };
    }

    /// <summary>
    /// 无视防御的固定伤害（炸弹、部分技能）。
    /// 「无视防御」= 不吃防御力、也不吃防御姿态的减半，否则道具描述就是骗人的。
    /// 这里和 ComputeDamage 一样先四舍五入再夹下限：TS 版两处都是 Math.round，
    /// 移植时这一句漏了 Round，调用方传进来一个小数（楼层机制的 5.5 反震）
    /// 就会被 (int) 截断成 5——两边算出不同的数，还查不出是谁改的。
    /// </summary>
    public static DamageResult FixedDamage(int amount) => new()
    {
        Damage = Math.Max(1, (int)Math.Round((double)amount)),
        Crit = false,
        AvoidedDefense = true,
    };

    /// <summary>
    /// 真正扣血。`fromPlayer` 为 true 时这一下是玩家亲手打出来的（炸弹炸死自己），
    /// 它不该被「中毒每层最多毒死 N 次」那条预算兜住——预算拦的是毒，不是玩家自己。
    /// </summary>
    public static int ApplyDamage(Combatant target, DamageResult result, bool fromPlayer = false)
    {
        var hp = target.Hp;
        var dealt = Math.Min(hp, result.Damage);
        // 预算只拦「毒会把**玩家**带走」这一件事：它既只拦环境那一侧
        // （玩家自己投出的刀不算），也只认玩家——敌人身上没有这条规则，
        // 把它们一起拦下来的代价是敌人剩 1 点血却怎么也打不死（实测：
        // 第 8 层的回响巫妖卡在 1 点，200 回合打不完；一层的巨鼠也一样）。
        //
        // 全用局部变量比较：同一个对象上的成员读两遍，读到的不是同一件事时，
        // 这条判据会静默失效。
        var lethal = dealt >= hp;
        if (!fromPlayer && target.IsPlayer && lethal && !EntityRules.PoisonLethalAllowed(target, hp))
            dealt = Math.Max(0, hp - 1);
        target.Hp = hp - dealt;
        return dealt;
    }
}
