// 技能结算：一次调用 = 扣冷却 + 按类型生效 + 返回一句战斗日志。
namespace AbyssEcho.Systems;

using AbyssEcho.Core;
using AbyssEcho.Data;

public sealed class SkillContext
{
    public Combatant Actor { get; init; } = null!;
    public Combatant Target { get; init; } = null!;
    public int ActorAtk { get; init; }
    public int ActorMaxHp { get; init; }
    public int TargetDef { get; init; }
    public Rng Rng { get; init; } = null!;
    public CombatRules Rules { get; init; } = null!;
    /// <summary>施放者生效中的装备词条：毒系持续、汲血回血、冰封回血在这里生效</summary>
    public ActiveAffixes Affixes { get; init; } = ActiveAffixes.Empty;
    /// <summary>施放者自身的汲取倍率（汲血蝙蝠低血时 2 倍）</summary>
    public double DrainMultiplier { get; init; } = 1;
    /// <summary>词条归属方：只有玩家侧的治疗会被贪婪之刃削弱</summary>
    public Combatant? AffixesOwner { get; init; }
    /// <summary>环境对治疗量的倍率（幽暗层减半）。和贪婪之刃是两笔账。</summary>
    public double HealFactor { get; init; } = 1;
    /// <summary>攻击方的暴击率加成（玩家侧词条 + 楼层补偿；敌人侧是周目诅咒）</summary>
    public double CritBonus { get; init; }
    /// <summary>受伤方是不是玩家。只有玩家挨的打才读「受到的伤害 +15%」这类词条。</summary>
    public bool TargetIsPlayer { get; init; }
    /// <summary>受伤方生效中的词条（通常是玩家那份）</summary>
    public ActiveAffixes TargetAffixes { get; init; } = ActiveAffixes.Empty;
    /// <summary>本场给「施法者」额外叠加的冷却回合数（深渊领域）。两个方向都要算</summary>
    public int ExtraCooldown { get; init; }
}

public sealed class SkillResult
{
    public string Log { get; init; } = "";
    public int Damage { get; init; }
    public int Healed { get; init; }
}

public static class Skills
{
    /// <summary>中毒持续回合数：与技能描述、UI 提示共用，改这里就够</summary>
    public const int PoisonTurns = 3;

    public static bool IsSkillReady(Combatant actor, string skillId) => SkillCooldownLeft(actor, skillId) == 0;

    /// <summary>
    /// 这个技能「还要等几回合」。0 表示能放。
    ///
    /// 判据只能有一份，而且必须是「还剩几回合」这一个数：
    /// 施放时写进去的是 `Cooldown + 1`（当回合也算一格，回合末统一递减），
    /// 所以放完立刻读出来是「冷却 +1」——它表示的是**还要等几回合**，
    /// 不是「还要等冷却加一回合」。
    ///
    /// 以前这里写成「做两件事」：`cd <= 0` 判能不能放，而按钮直接显示这个原始值。
    /// 于是火球（冷却 2）放完按钮写着「冷却 3」，而下一回合就能放的技能在
    /// 描述上也多出一格——玩家看到的冷却永远比真实的慢一回合。
    /// 现在两个读者读同一个数，且这个数就是「还要等几回合」。
    /// </summary>
    /// <remarks>
    /// 存进去的是 `Cooldown + 1`（当回合也算一格），所以「还要等几回合」= 存的值 - 1。
    /// 不减这一下的话，按钮上永远多报一格：火球（冷却 2）放完写着「还要 3 回合」，
    /// 而它再打两个回合就能放。判据仍然只有这一个数：`IsSkillReady` 也读它。
    /// </remarks>
    public static int SkillCooldownLeft(Combatant actor, string skillId)
    {
        var raw = actor.Cooldowns.TryGetValue(skillId, out var cd) ? cd : 0;
        var v = Math.Max(0, raw - 1);
        return v;
    }

    /// <summary>施放技能：扣冷却、按类型结算，返回用于战斗日志的描述</summary>
    public static SkillResult UseSkill(SkillDef skill, SkillContext ctx)
    {
        if (!IsSkillReady(ctx.Actor, skill.Id))
        {
            return new SkillResult { Log = $"{skill.Name} 仍在冷却中" };
        }
        // 施放当回合也要计入，回合结束时统一递减
        ctx.Actor.Cooldowns[skill.Id] = skill.Cooldown + 1;
        // 额外冷却（深渊领域 +1）：走这里，不走调用方。
        // 以前只有 Battle.PlayerSkill 补这一笔，敌人的技能（炼狱、雷鸣、暗愈……）
        // 在深渊领域里冷却是原样的——说明写「你的技能冷却 +1」，而这一层真正
        // 决定生死的是「Boss 每几回合能放一次 1.6 倍炼狱」。
        var extra = ctx.ExtraCooldown;
        if (extra > 0) ctx.Actor.Cooldowns[skill.Id] += extra;

        switch (skill.Type)
        {
            case SkillType.Damage:
            {
                var result = CombatMath.ComputeDamage(
                    ctx.Actor, ctx.Target, ctx.ActorAtk * skill.Power, ctx.TargetDef, ctx.Rng, ctx.Rules, SkillHooks(ctx));
                var dealt = CombatMath.ApplyDamage(ctx.Target, result);
                return new SkillResult
                {
                    Log = $"{Label(ctx.Actor)}使用「{skill.Name}」，造成 {dealt} 点伤害{(result.Crit ? "（暴击！）" : "")}",
                    Damage = dealt,
                };
            }

            case SkillType.Heal:
            {
                // 两个折扣分开算：环境（幽暗层）× 词条（贪婪之刃）。
                var env = ctx.HealFactor;
                // 贪婪之刃只对玩家侧生效（词条是「我的构筑」），敌人的暗愈不该被玩家的诅咒削弱。
                var curse = ctx.Actor == ctx.AffixesOwner ? AffixMath.HealTakenScale(ctx.Affixes) : 1;
                var amount = Math.Max(1, (int)Math.Round(ctx.ActorMaxHp * skill.Power * env * curse));
                var before = ctx.Actor.Hp;
                ctx.Actor.Hp = Math.Min(ctx.ActorMaxHp, ctx.Actor.Hp + amount);
                var healed = ctx.Actor.Hp - before;
                var why = env < 1 ? "（幽暗中治疗效果减半）" : curse < 1 ? "（贪婪之刃啃掉了大半）" : "";
                return new SkillResult
                {
                    Log = $"{Label(ctx.Actor)}使用「{skill.Name}」，回复 {healed} 点生命{why}",
                    Healed = healed,
                };
            }

            // 中毒：立刻造成一次伤害，并给目标挂上持续掉血。
            // 没有它之前战斗永远是「你一刀我一刀」，毒让「先手」有了后续收益。
            case SkillType.Poison:
            {
                // 纯 DoT（毒雾）没有直接伤害：它的 power 是「每回合受攻击力 × power」。
                // 以前两种毒共用这一句，于是毒雾那一发会按「攻击力 × 0.5」再削一刀，
                // 而描述里只写了持续伤害——玩家白挨了一记没写出来的直伤。
                var dealt = 0;
                var crit = false;
                if (HasDirectHit(skill))
                {
                    var result = CombatMath.ComputeDamage(
                        ctx.Actor, ctx.Target, ctx.ActorAtk * skill.Power, ctx.TargetDef, ctx.Rng, ctx.Rules, SkillHooks(ctx));
                    dealt = CombatMath.ApplyDamage(ctx.Target, result);
                    crit = result.Crit;
                }
                // 免疫中毒只有一种来源：敌人模板上的 ImmunePoison（疫病/魂腐层由 Setup 写进去）。
                // 玩家永远不会免疫——以前这里问的是 FloorMechanics.PoisonImmune，
                // 而它读的正是 `enemy.ImmunePoison == true`，玩家身上是 null，
                // 所以玩家放毒时这一句不会误伤。真正的判据只该看「目标自己免不免疫」。
                if (ctx.Target.ImmunePoison == true)
                {
                    var hitPart = dealt > 0 ? $"造成 {dealt} 点伤害，" : "";
                    return new SkillResult
                    {
                        Log = $"{Label(ctx.Actor)}使用「{skill.Name}」，{hitPart}敌人甲胄上的腐殖质吞掉了毒素",
                        Damage = dealt,
                    };
                }
                // 回合数以技能数据为准，缺省才用全局的 3：毒爆的描述写「中毒 2 回合」。
                var baseTurns = skill.PoisonTurns ?? PoisonTurns;
                var turns = AffixMath.PoisonTurnsWithAffixes(baseTurns, ctx.Affixes);
                EntityRules.ApplyPoison(ctx.Target, turns, PoisonPowerOf(skill, ctx, dealt));
                // 日志要说清这一发到底是「打了一刀再挂毒」还是「只是挂毒」。
                // 毒雾那一档的伤害是 0（它的 power 全给了持续伤害），
                // 而以前两种都按「造成 X 点伤害并使其中毒」写，于是毒雾在日志里
                // 变成「造成 0 点伤害」——玩家看到的是一个什么都没做的技能。
                var hit = dealt > 0 ? $"造成 {dealt} 点伤害{(crit ? "（暴击！）" : "")}并" : "";
                return new SkillResult
                {
                    Log = $"{Label(ctx.Actor)}使用「{skill.Name}」，{hit}使其中毒 {turns} 回合",
                    Damage = dealt,
                };
            }

            // 汲血：造成伤害并按同样数值回血。
            case SkillType.Drain:
            {
                var result = CombatMath.ComputeDamage(
                    ctx.Actor, ctx.Target, ctx.ActorAtk * skill.Power, ctx.TargetDef, ctx.Rng, ctx.Rules, SkillHooks(ctx));
                var dealt = CombatMath.ApplyDamage(ctx.Target, result);
                // 「汲髓」：灵魂汲取类回血量 +N%。加成按「本次汲取的量」算，满血时不攒到下一击。
                var pct = ctx.Affixes.DrainHealPct;
                var drained = (int)Math.Round(dealt * (1 + pct) * ctx.DrainMultiplier);
                var healed = EntityRules.Heal(ctx.Actor, drained, ctx.ActorMaxHp);
                var log = $"{Label(ctx.Actor)}使用「{skill.Name}」，汲取 {drained} 点生命"
                          + (healed > 0 ? $"并回复 {healed} 点" : "")
                          + (pct > 0 ? "（汲髓）" : "")
                          + (ctx.DrainMultiplier > 1 ? "（嗜血）" : "");
                return new SkillResult { Log = log, Damage = dealt, Healed = healed };
            }

            // 破防：压低目标防御若干回合。数值是绝对值（3 点），不是倍率。
            case SkillType.DebuffDefense:
            {
                ctx.Target.DefMod = -(int)Math.Round(skill.Power);
                ctx.Target.DefModTurns = 3;
                var after = EntityRules.EffectiveDefense(ctx.Target, ctx.Target.BaseDefense);
                return new SkillResult
                {
                    Log = $"{Label(ctx.Actor)}使用「{skill.Name}」，{ctx.Target.Name}的防御降到 {Math.Max(0, after)} 点，持续 3 回合",
                };
            }

            // 降攻：把目标的攻击按比例压低，让「下一刀」的预告一起变温柔
            case SkillType.DebuffAttack:
            {
                ctx.Target.AtkMod = -(int)Math.Round(ctx.Target.BaseAttack * skill.Power);
                ctx.Target.AtkModTurns = 3;
                // 「霜噬」：冰封印记命中时自身回血。只认 DebuffAttack 这一种。
                var sealHeal = ctx.Affixes.FrostSealHeal;
                // 归属方判断：敌人放冰封印记不该凭空回血。
                if (sealHeal > 0 && ctx.Actor == ctx.AffixesOwner)
                {
                    var before = ctx.Actor.Hp;
                    ctx.Actor.Hp = Math.Min(ctx.ActorMaxHp, ctx.Actor.Hp + sealHeal);
                    if (ctx.Actor.Hp > before)
                    {
                        return new SkillResult
                        {
                            Log = $"{Label(ctx.Actor)}使用「{skill.Name}」，{ctx.Target.Name}的攻击下降了 {(int)Math.Round(skill.Power * 100)}%，持续 3 回合；霜噬回复 {ctx.Actor.Hp - before} 点生命",
                            Healed = ctx.Actor.Hp - before,
                        };
                    }
                }
                return new SkillResult
                {
                    Log = $"{Label(ctx.Actor)}使用「{skill.Name}」，{ctx.Target.Name}的攻击下降了 {(int)Math.Round(skill.Power * 100)}%，持续 3 回合",
                };
            }

            // 闪避：本回合闪避率提升，敌人攻击落空。
            // 与楼层机制给的常驻闪避分开：这里只加「这一份」，楼层那份还在 BaseEvade 里。
            case SkillType.Evade:
            {
                ctx.Actor.Evade = Math.Max(ctx.Actor.Evade, ctx.Actor.BaseEvade + skill.Power);
                return new SkillResult { Log = $"{Label(ctx.Actor)}使用「{skill.Name}」，身影散作黑雾" };
            }

            // 眩晕：先造成伤害，再让目标下一回合无法行动。
            case SkillType.Stun:
            {
                var result = CombatMath.ComputeDamage(
                    ctx.Actor, ctx.Target, ctx.ActorAtk * skill.Power, ctx.TargetDef, ctx.Rng, ctx.Rules, SkillHooks(ctx));
                var dealt = CombatMath.ApplyDamage(ctx.Target, result);
                ctx.Target.StunTurns = Math.Max(ctx.Target.StunTurns, 1);
                return new SkillResult
                {
                    Log = $"{Label(ctx.Actor)}使用「{skill.Name}」，造成 {dealt} 点伤害{(result.Crit ? "（暴击！）" : "")}，{ctx.Target.Name}被震得一时无法行动",
                    Damage = dealt,
                };
            }

            // 驱散：先造成一次伤害，再清掉目标身上的强化与正面状态。
            case SkillType.Dispel:
            {
                var result = CombatMath.ComputeDamage(
                    ctx.Actor, ctx.Target, ctx.ActorAtk * skill.Power, ctx.TargetDef, ctx.Rng, ctx.Rules, SkillHooks(ctx));
                var dealt = CombatMath.ApplyDamage(ctx.Target, result);
                // 「你的正面状态」是写给玩家看的：判据是目标是不是玩家，不是「是不是词条主人」。
                // 用 IsPlayer 而不是 `is Player`：敌人是普通 Combatant 实例，
                // `is Player` 对它们永远为假——而这里要判的恰恰是「目标是不是玩家那一侧」。
                var targetIsPlayer = ctx.Target.IsPlayer;
                var had = ctx.Target.AttackMultiplier > 1;
                ctx.Target.AttackMultiplier = 1;
                var dispelled = FloorMechanics.DispelBuffs(ctx.Target);
                var note = targetIsPlayer && (had || dispelled) ? "，你的正面状态被黑暗吞没" : "";
                return new SkillResult
                {
                    Log = $"{Label(ctx.Actor)}使用「{skill.Name}」，造成 {dealt} 点伤害{(result.Crit ? "（暴击！）" : "")}{note}",
                    Damage = dealt,
                };
            }

            // 增益：只有两种，按数据里的 power 区分，日志得说清是哪一种。
            // 早先这里无脑写「下次攻击威力提升」，而「石肤」的 power 是 0.25 ——
            // 那句文案对它是假的（石肤实际生效在防御侧，走 ArmorBuff）。
            case SkillType.Buff:
            {
                if (ArmorBuff(skill) is { } armor)
                {
                    ctx.Actor.DefMod = Math.Max(1, (int)Math.Round(ctx.Actor.BaseDefense * armor.Power));
                    ctx.Actor.DefModTurns = armor.Turns + 1;
                    return new SkillResult
                    {
                        Log = $"{Label(ctx.Actor)}使用「{skill.Name}」，防御提升 {ctx.Actor.DefMod} 点（{armor.Turns} 回合）",
                    };
                }
                // 攻击类增益：倍率挂在「下一次出手」上。这里只置位，真正消费在
                // Battle.PlayerAttack 与敌人的 EnemyAttackResult —— 两边各一处，缺一边就白放。
                ctx.Actor.AttackMultiplier = 1 + skill.Power;
                return new SkillResult { Log = $"{Label(ctx.Actor)}使用「{skill.Name}」，下次攻击威力提升 {Percent(skill.Power)}" };
            }

            default:
                // 其余类型（减益/闪避/眩晕/驱散）都已经在上面各自结算。
                // 走到这里说明数据里新加了一个枚举值却没写结算——宁可报错也不要静默什么都不做。
                throw new NotSupportedException($"技能类型 {skill.Type} 没有结算分支：{skill.Id}");
        }
    }

    /// <summary>
    /// 中毒每回合掉多少血：按「这一发毒实际打出了多少」算。
    ///
    /// 以前写的是 `ActorAtk × skill.Power`——那是「这一发直接伤害」的公式，
    /// 拿来当持续伤害就变成了另一回事：毒雾 power 0.5、描述写「每回合受攻击力 50%」，
    /// 而毒爆 power 1.4 的直接伤害本来就乘过一次防御、吃了一次暴击与浮动，
    /// 挂上的毒却是「攻击力 × 1.4」——远高于它自己打出的那一刀，
    /// 于是「毒爆」的主要伤害来自回合结束，而不是描述里那句「造成 1.4 倍攻击力伤害」。
    ///
    /// 毒雾是纯 DoT，没有直接伤害可比，退回它自己那句「攻击力 × power」。
    /// 两种情况都不再看目标防御：毒是「无视护甲」的那种持续伤害，这一点和描述一致。
    /// </summary>
    /// <summary>
    /// 纯 DoT 的毒（毒雾）按「每回合受攻击力 × power」算，而且**不能按防御后的一刀算**。
    ///
    /// 毒雾的描述写的是「使敌人每回合受到攻击力 50% 的伤害」——它没有直接伤害，
    /// 所以这一发打出去的 dealt 永远是 0，这里退回「攻击力 × power」是对的。
    /// 但退回时必须看**这一发本身有没有直接伤害**，不能看 dealt 是不是 0：
    /// 毒爆打在一只防御极高的怪身上，dealt 可能被压成 0（最低伤害那一档），
    /// 那时候照 dealt 回退会把「1.4 倍攻击力」的毒缩成 0.1 倍。
    /// 判据收在 Skill.HasDirectHit 上：它由技能类型与数据决定，与这一次掷骰无关。
    /// </summary>
    private static int PoisonPowerOf(SkillDef skill, SkillContext ctx, int dealt)
    {
        // 有直伤的那一档（毒液喷吐 / 毒爆）：毒伤就是这一刀实际打出的数，但封顶。
        // 「按这一刀」与「按基础攻击」在浅层差不多，到了深层差十倍——
        // 毒是唯一一条会随着层数指数膨胀的伤害，而玩家身上唯一能解它的东西
        // 只有一个吃回合的解毒草。封顶写在 EntityRules 里，这一处只负责问它。
        // 挂毒的数值走 `EntityRules.PoisonPowerFor`：它按**目标血条**折算
        // （8%，夹在 1..MaxPoisonPower）。这一处以前问的是「施法者的基础攻击力」，
        // 于是毒的份量随施法者膨胀、而玩家的血条不动——第 15 层的毒固定 6 点，
        // 六分之一条命也没有。现在施法者只提供比例（`skill.Power`），量血条的是目标。
        var targetMax = Math.Max(1, ctx.Target.BaseMaxHp);
        if (HasDirectHit(skill) && dealt > 0)
            return Math.Min(dealt, EntityRules.PoisonPowerFor(targetMax, skill.Power));
        // 纯 DoT（毒雾）：它没有直伤，「这一刀打掉多少」无从谈起，
        // 于是按目标血条与技能倍率折算——和上面那一档读同一个入口、同一把尺子。
        return EntityRules.PoisonPowerFor(targetMax, skill.Power);
    }

    /// <summary>
    /// 这个技能会不会自己就打出一次直接伤害。
    /// 判据是数据里的 <see cref="SkillDef.DirectDamage"/>：毒雾（toxic_fog）的 power
    /// 全给了持续伤害，描述里也没有「造成 X 点伤害」那一句，它在数据里显式写着 false。
    /// 不按 dealt 判——毒爆打在高防御的怪身上也可能打出 0（最低伤害那一档）。
    /// </summary>
    public static bool HasDirectHit(SkillDef skill) => skill.DirectDamage != false;

    /// <summary>
    /// 这个技能会不会被「混乱思绪」吞掉。
    ///
    /// 判据是「要不要念」：只有需要凝聚力量的施法（伤害 / 汲血 / 中毒 / 治疗）会散掉。
    /// 摆架势的那几类不是法术——强化自己的下一击、石肤加防、相位穿行的闪避、
    /// 防守反击的破防与降攻，它们手上没有可以「念错」的东西。
    ///
    /// 以前这条判据不分技能：玩家戴着「混乱思绪」上战场，按防御之外**任何**一手
    /// （包括「强化」）都有 25% 概率白给，而词条描述只写「每次施法有 25% 概率失败」——
    /// 「摆个架势也算施法」这件事没有任何一处说明写着。
    ///
    /// 敌人放的技能不参与：那个词条是玩家的构筑代价（Battle.PlayerSkill 才调它）。
    /// </summary>
    public static bool CanFizzle(SkillDef skill) =>
        skill.Type is SkillType.Damage or SkillType.Drain or SkillType.Poison or SkillType.Heal;

    /// <summary>回合结束时递减所有冷却</summary>
    public static void TickCooldowns(Combatant actor)
    {
        // 前提：`Cooldowns[id]` 记的是「还要等几回合」，施放当回合就已经是「冷却 + 额外」
        // （放完立刻读出来该是 冷却 2 / 领域里 3）。所以这里每回合末减一格，
        // 减到 0 就说明「这个回合开始就能放了」。
        foreach (var id in actor.Cooldowns.Keys.ToList())
        {
            if (actor.Cooldowns[id] > 0) actor.Cooldowns[id] -= 1;
        }
    }

    /// <summary>
    /// 战斗结束后清空临时状态。
    /// 中毒、冷却、强化倍率这些就不说了，降攻/破防的剩余回合、被眩晕的回合、
    /// 相位穿行的闪避率也必须是「只属于这一场」的。
    /// </summary>
    public static void ResetCombatState(Combatant actor)
    {
        actor.Defending = false;
        actor.AttackMultiplier = 1;
        actor.Cooldowns = new Dictionary<string, int>();
        EntityRules.ClearPoison(actor);
        actor.AtkMod = 0;
        actor.AtkModTurns = 0;
        actor.DefMod = 0;
        actor.DefModTurns = 0;
        actor.StunTurns = 0;
        actor.Evade = 0;
        actor.BaseEvade = 0;
    }

    /// <summary>日志里怎么称呼施法者：玩家用「你」，敌人用名字。判 IsPlayer 而不是 `is Player`</summary>
    private static string Label(Combatant c) => c.IsPlayer ? "" : c.Name;

    private static string Percent(double v) => $"{(int)Math.Round(v * 100)}%";

    /// <summary>
    /// 这个增益是不是「加防御」的那一类。
    ///
    /// 判据来自类型本身，不去猜数据：怪物手册里「石肤」的正文是
    /// 「本回合受到的伤害降低 25%」（ArmorBuff 里就是这一条），而它落成的
    /// SkillType 与「强化」一样是 Buff、power 也一样落在 0.25 这个数量级，
    /// 光看 power 分不开。所以定义写在 <see cref="FloorMechanics.ArmorBuff"/>，
    /// 数据表与战斗结算共用一份——以前只有怪物手册那一侧有，结算侧无脑加攻击。
    /// </summary>
    private static ArmorBuffDef? ArmorBuff(SkillDef skill) =>
        FloorMechanics.ArmorBuff.TryGetValue(skill.Id, out var cfg) ? cfg : null;

    /// <summary>
    /// 技能伤害的词条钩子。两个方向分开算：
    /// 攻击方加成（CritBonus）归谁的招谁受益；受伤方词条（脆弱躯壳）只看挨打的是不是玩家。
    /// </summary>
    private static AffixHooks? SkillHooks(SkillContext ctx)
    {
        var critBonus = ctx.CritBonus;
        var targetIsPlayer = ctx.TargetIsPlayer;
        var affixes = targetIsPlayer ? ctx.TargetAffixes : ActiveAffixes.Empty;
        if (critBonus <= 0 && affixes.List.Count == 0) return null;
        return new AffixHooks { Affixes = affixes, CritBonus = critBonus, TargetIsPlayer = targetIsPlayer };
    }
}
