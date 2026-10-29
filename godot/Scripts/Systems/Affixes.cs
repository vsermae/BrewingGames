// 词条结算：装备词条的效果只在这里落地。
//
// 设计原则：词条是「数据里的一段描述」，不是「代码里的一类 if」。
// 新增词条不动这里的结构——字段写进 game_data.json，汇总与查询都在这一层。
namespace AbyssEcho.Systems;

using AbyssEcho.Core;
using AbyssEcho.Data;

/// <summary>生效中的词条集合：把玩家装备栏里所有装备的词条汇总成一份可查询的视图</summary>
public sealed class ActiveAffixes
{
    public List<AffixDef> List { get; init; } = new();
    public double AttackPct { get; init; }
    public double CritChance { get; init; }
    public int PoisonTurns { get; init; }
    public double DrainHealPct { get; init; }
    public int FrostSealHeal { get; init; }
    public BurnDef? CritBurn { get; init; }
    public double KillHealPct { get; init; }
    public bool SurviveLethal { get; init; }
    public double RiskIncomingPct { get; init; }
    public int TurnStartHpLoss { get; init; }
    public double HealTakenPct { get; init; }
    public double MaxHpPct { get; init; }
    public double DamageTakenPct { get; init; }
    public double SpellFailChance { get; init; }

    public static readonly ActiveAffixes Empty = new();
}

/// <summary>命中判定前的钩子返回值：需要战斗日志写出来的话</summary>
public sealed class AffixTrigger
{
    public string? Log { get; init; }
    /// <summary>对目标附加的灼烧（复用中毒的持续掉血）</summary>
    public BurnDef? Burn { get; init; }
}

public static class AffixMath
{
    public static AffixDef AffixDefOf(string id)
    {
        if (!GameDataStore.Get().Affixes.TryGetValue(id, out var def))
            throw new InvalidOperationException($"引用了不存在的词条: {id}");
        return def;
    }

    public static List<AffixDef> ItemAffixes(ItemDef def) =>
        (def.Affixes ?? new List<string>()).Select(AffixDefOf).ToList();

    private static bool HasNumeric(double? v) => v is { } d && !double.IsNaN(d) && !double.IsInfinity(d) && d > 0;

    /// <summary>汇总一组装备的词条效果</summary>
    public static ActiveAffixes Collect(IEnumerable<ItemDef> defs)
    {
        var list = new List<AffixDef>();
        double attackPct = 0, critChance = 0, drainHealPct = 0, killHealPct = 0,
            riskIncomingPct = 0, healTakenPct = 0, maxHpPct = 0, damageTakenPct = 0, spellFailChance = 0;
        int poisonTurns = 0, frostSealHeal = 0, turnStartHpLoss = 0;
        BurnDef? critBurn = null;
        var surviveLethal = false;

        foreach (var def in defs)
        {
            foreach (var affix in ItemAffixes(def))
            {
                list.Add(affix);
                if (HasNumeric(affix.AttackPct)) attackPct += affix.AttackPct!.Value;
                if (HasNumeric(affix.RiskAttackPct)) attackPct += affix.RiskAttackPct!.Value;
                if (HasNumeric(affix.CritChance)) critChance += affix.CritChance!.Value;
                if (HasNumeric(affix.PoisonTurns)) poisonTurns += affix.PoisonTurns!.Value;
                if (HasNumeric(affix.DrainHealPct)) drainHealPct += affix.DrainHealPct!.Value;
                if (HasNumeric(affix.FrostSealHeal)) frostSealHeal += affix.FrostSealHeal!.Value;
                if (HasNumeric(affix.KillHealPct)) killHealPct += affix.KillHealPct!.Value;
                if (HasNumeric(affix.RiskIncomingPct)) riskIncomingPct += affix.RiskIncomingPct!.Value;
                if (HasNumeric(affix.TurnStartHpLoss)) turnStartHpLoss += affix.TurnStartHpLoss!.Value;
                // 负面词条是「代价」，数值方向各不相同：减治疗、加受伤、加生命。
                // 这里用带符号累加，而不是只累加正数——否则贪婪之刃的 -50% 会被静默丢掉。
                if (affix.HealTakenPct is { } ht && !double.IsNaN(ht)) healTakenPct += ht;
                if (HasNumeric(affix.MaxHpPct)) maxHpPct += affix.MaxHpPct!.Value;
                if (HasNumeric(affix.DamageTakenPct)) damageTakenPct += affix.DamageTakenPct!.Value;
                if (HasNumeric(affix.SpellFailChance)) spellFailChance += affix.SpellFailChance!.Value;
                if (affix.CritBurn is not null) critBurn = affix.CritBurn;
                if (affix.SurviveLethal == true) surviveLethal = true;
            }
        }

        return new ActiveAffixes
        {
            List = list,
            AttackPct = attackPct,
            CritChance = critChance,
            PoisonTurns = poisonTurns,
            DrainHealPct = drainHealPct,
            FrostSealHeal = frostSealHeal,
            CritBurn = critBurn,
            KillHealPct = killHealPct,
            SurviveLethal = surviveLethal,
            RiskIncomingPct = riskIncomingPct,
            TurnStartHpLoss = turnStartHpLoss,
            HealTakenPct = healTakenPct,
            MaxHpPct = maxHpPct,
            DamageTakenPct = damageTakenPct,
            SpellFailChance = spellFailChance,
        };
    }

    /// <summary>取玩家当前装备的词条视图；背包里的未装备道具不参与</summary>
    public static ActiveAffixes PlayerAffixes(Player player, IReadOnlyDictionary<string, ItemDef> itemsById)
    {
        var defs = new List<ItemDef>();
        foreach (var slot in EntityRules.EquipSlots)
        {
            var idx = player.Equipped.Get(slot);
            if (idx is null) continue;
            if (idx.Value < 0 || idx.Value >= player.Inventory.Count) continue;
            if (itemsById.TryGetValue(player.Inventory[idx.Value], out var def)) defs.Add(def);
        }
        return Collect(defs);
    }

    /// <summary>攻击力结算：固定值先加，百分比后乘</summary>
    public static int AttackWithAffixes(int baseAttack, int flatBonus, ActiveAffixes affixes) =>
        Math.Max(1, (int)Math.Round((baseAttack + flatBonus) * (1 + affixes.AttackPct)));

    /// <summary>暴击率：基础 + 词条，上限 100%</summary>
    public static double CritChanceWithAffixes(double baseChance, ActiveAffixes affixes) =>
        Math.Min(1, baseChance + affixes.CritChance);

    /// <summary>中毒类技能的持续回合 +N</summary>
    public static int PoisonTurnsWithAffixes(int baseTurns, ActiveAffixes affixes) => baseTurns + affixes.PoisonTurns;

    /// <summary>暴击命中时的触发型词条</summary>
    public static AffixTrigger? OnCrit(bool crit, ActiveAffixes affixes)
    {
        if (!crit || affixes.CritBurn is null) return null;
        var burn = affixes.CritBurn;
        return new AffixTrigger
        {
            Burn = burn,
            Log = $"暴击点燃了「炽热暴击」，目标被灼烧 {burn.Turns} 回合",
        };
    }

    /// <summary>玩家击杀敌人时的触发型词条</summary>
    public static AffixTrigger? OnKill(Combatant actor, int maxHp, ActiveAffixes affixes)
    {
        if (affixes.KillHealPct <= 0) return null;
        var amount = Math.Max(1, (int)Math.Round(maxHp * affixes.KillHealPct));
        var before = actor.Hp;
        actor.Hp = Math.Min(maxHp, actor.Hp + amount);
        var healed = actor.Hp - before;
        if (healed <= 0) return null;
        return new AffixTrigger { Log = $"「噬魂」从倒下的敌人身上汲回 {healed} 点生命" };
    }

    /// <summary>致命伤拦截：受伤后 hp ≤ 0 且有「不屈」时保留 1 点生命，每场战斗一次</summary>
    public static AffixTrigger? TrySurviveLethal(Combatant player, ActiveAffixes affixes, bool used)
    {
        if (!affixes.SurviveLethal || used) return null;
        if (player.Hp > 0) return null;
        player.Hp = 1;
        return new AffixTrigger { Log = "「不屈」替你扛下了致命一击，你以 1 点生命站了起来" };
    }

    /// <summary>每回合开始的负面词条（诅咒房紫装）</summary>
    public static int TurnStartLoss(ActiveAffixes affixes) => affixes.TurnStartHpLoss;

    /// <summary>治疗效果修正（贪婪之刃 -50%）：返回倍率而不是比例差</summary>
    public static double HealTakenScale(ActiveAffixes affixes) => affixes.HealTakenPct >= 0 ? 1 : 1 + affixes.HealTakenPct;

    /// <summary>最大生命百分比修正（脆弱躯壳 +20%）：乘在基础与装备固定值之和上</summary>
    public static int MaxHpWithAffixes(int baseMaxHp, ActiveAffixes affixes) =>
        (int)Math.Round(baseMaxHp * (1 + affixes.MaxHpPct));

    /// <summary>受伤加成（脆弱躯壳 +15%）。放在伤害结算的最后一乘。</summary>
    public static double IncomingScale(ActiveAffixes affixes) => 1 + affixes.DamageTakenPct;

    /// <summary>施法失败判定（混乱思绪）：法术被吞掉，冷却照扣，回合照结束</summary>
    public static bool SpellFizzled(ActiveAffixes affixes, Rng rng) =>
        affixes.SpellFailChance > 0 && rng.Chance(affixes.SpellFailChance);
}
