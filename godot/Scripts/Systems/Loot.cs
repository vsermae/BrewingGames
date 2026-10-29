// 装备掉落：决定「这一只敌人该掉什么品质的装备」。
//
// 规则只有一句：普通敌人掉白/蓝，精英掉蓝/紫，Boss 掉紫/橙。
// 品质在这里决定，词条由 game_data 里的模板决定——掉落的装备是从模板里挑一件，
// 而不是临时生成数值，这样「锈剑」永远是那把锈剑，玩家认得出装备。
namespace AbyssEcho.Systems;

using AbyssEcho.Core;
using AbyssEcho.Data;

public enum EnemyTier
{
    Normal,
    Elite,
    Boss,
}

public static class Loot
{
    private static readonly Dictionary<EnemyTier, (Quality Quality, int Weight)[]> TierQualities = new()
    {
        // 普通敌人：白装打底，偶尔一件带词条的蓝装让「词条」这个概念早点露面
        [EnemyTier.Normal] = new[] { (Quality.White, 6), (Quality.Blue, 4) },
        // 精英：蓝紫之间，玩家从这档开始见到双词条
        [EnemyTier.Elite] = new[] { (Quality.Blue, 5), (Quality.Purple, 5) },
        // Boss：紫橙，橙装同时是套装名的载体（套装效果预留，暂不生效）
        [EnemyTier.Boss] = new[] { (Quality.Purple, 6), (Quality.Orange, 4) },
    };

    /// <summary>
    /// 元进展「双生词条」给高档品质的权重加成：
    /// 蓝档的权重往紫/橙移，紫档的往橙移。它不改任何数值，只改「抽到哪一档」的概率——
    /// 和元进展那条红线（只解锁选择，不解锁数值）一致。
    /// </summary>
    public const int DualAffixQualityBoost = 3;

    /// <summary>
    /// 每个档位能出的品质，从低到高。
    /// 元进展「双生词条」要往「更稀的那一档」挪权重，而「哪一档更稀」不能靠
    /// 「表里写在最后那一条」来推——档位表迟早会被重排（普通敌人那行就先写白再写蓝），
    /// 那时候「最后一条」可能是最差的那一档，于是这个解锁会变成纯惩罚。
    /// </summary>
    private static readonly Quality[] QualityOrder = { Quality.White, Quality.Blue, Quality.Purple, Quality.Orange };

    /// <summary>某一档里最稀的品质。用它来定「权重往哪边挪」</summary>
    public static Quality RarestOf(EnemyTier tier) =>
        TierQualities[tier].Select(q => q.Quality).OrderBy(q => Array.IndexOf(QualityOrder, q)).Last();

    /// <summary>按档位抽一个品质</summary>
    public static Quality RollQuality(Rng rng, EnemyTier tier, bool dualAffixUnlocked = false)
    {
        var table = TierQualities[tier];
        if (!dualAffixUnlocked || table.Length < 2) return rng.Weighted(table, q => q.Weight).Quality;
        // 每一档的权重都往「这一档里最稀的那一件」挪一点，稀有档补上挪过来的总量。
        // 判「最稀」看 QualityOrder，不看它在表里排第几——表一旦重排，
        // 按位置算就会把权重搬到最差的那一档，等于花 150 碎片买了个负面效果。
        var rarest = RarestOf(tier);
        var weights = table
            .Select(q => q.Quality == rarest ? q.Weight : Math.Max(0, q.Weight - DualAffixQualityBoost))
            .ToArray();
        weights[Array.FindIndex(table, q => q.Quality == rarest)] +=
            DualAffixQualityBoost * (table.Count(q => q.Quality != rarest));
        return rng.Weighted(table.Select((q, i) => (q.Quality, Weight: weights[i])).ToArray(), q => q.Weight).Quality;
    }

    /// <summary>
    /// 从模板池里挑一件符合目标品质的装备。
    /// 池子里没有该品质时退化为全部装备里随机一件，保底让玩家总能拿到东西。
    /// </summary>
    public static ItemDef? PickItemOfQuality(Rng rng, IReadOnlyList<ItemDef> items, EnemyTier tier,
        bool dualAffixUnlocked = false, IReadOnlyCollection<string>? avoid = null)
    {
        var equip = items.Where(i => i.Slot != Slot.Consumable).ToList();
        if (equip.Count == 0) return null;
        var want = RollQuality(rng, tier, dualAffixUnlocked);
        // avoid 是「这一次掉落已经给过的那几件」：同一次掉落里给两件一样的东西，
        // 玩家只会觉得奖励少了，不会觉得自己赚了。整张表都被避开时退回全表。
        var fresh = avoid is null ? equip : equip.Where(i => !avoid.Contains(i.Id)).ToList();
        if (fresh.Count == 0) fresh = equip;
        var same = fresh.Where(i => Inventory.QualityOf(i) == want).ToList();
        return rng.Pick(same.Count > 0 ? same : fresh);
    }

    /// <summary>
    /// 精英房的保底掉落：品质不低于蓝色，并按配置概率额外附一条随机词条。
    /// 掉落表里的模板本来大多不带词条，所以精英房的词条是「附」上去的——
    /// 这才让精英房成为早期词条装备的主要来源。
    /// </summary>
    public static ItemDef EliteDropBonus(Rng rng, ItemDef item, IReadOnlyList<string> affixPool,
        IReadOnlyCollection<string>? avoid = null)
    {
        if (item.Slot == Slot.Consumable) return item;
        var cfg = GameDataStore.Get().Config.Rooms.Elite;
        var order = new[] { Quality.White, Quality.Blue, Quality.Purple, Quality.Orange };
        // 保底品质：白/橙模板都可能被抽到，低于蓝色时提到蓝，并补一条词条
        var def = item.Clone();
        if (Array.IndexOf(order, Inventory.QualityOf(def)) < Array.IndexOf(order, cfg.MinQuality))
        {
            def.Quality = cfg.MinQuality;
            def.Affixes = new List<string>();
        }
        if (affixPool.Count == 0 || !rng.Chance(cfg.AffixChance)) return def;
        // 不抽这件装备已经有的那条：词条条数是有上限的，抽到重复项等于
        // 「精英保底给了词条」这件事被静默吞掉——玩家看到的是白装换了个名字。
        var pool = affixPool.Where(a => avoid is null || !avoid.Contains(a)).ToList();
        var affixId = rng.Pick(pool.Count > 0 ? pool : affixPool);
        var kept = (def.Affixes ?? new List<string>()).Where(a => a != affixId).ToList();
        var capacity = GameDataStore.Get().Qualities[Inventory.QualityOf(def)].AffixCount;
        // 附上的词条可能超出该品质容量：往上升一档，而不是把新词条悄悄截掉
        var list = kept.Append(affixId).ToList();
        if (list.Count > capacity)
        {
            var cur = Array.IndexOf(order, Inventory.QualityOf(def));
            var up = order[Math.Min(cur + 1, order.Length - 1)];
            def.Quality = up;
            var upCap = GameDataStore.Get().Qualities[up].AffixCount;
            def.Affixes = list.Skip(Math.Max(0, list.Count - upCap)).ToList();
            return def;
        }
        def.Affixes = list;
        return def;
    }

    /// <summary>该装备最终会带几条词条（品质上限与模板自身取小）</summary>
    public static int EffectiveAffixCount(ItemDef def) => Math.Min(Inventory.AffixLimit(def), (def.Affixes ?? new List<string>()).Count);

    /// <summary>掉落说明：宝箱/奖励面板上显示「紫装 · 双词条」</summary>
    public static string QualityTag(Quality quality) => quality switch
    {
        Quality.White => "无词条",
        Quality.Blue => "单词条",
        Quality.Purple => "双词条",
        Quality.Orange => "双词条",
        _ => "",
    };
}
