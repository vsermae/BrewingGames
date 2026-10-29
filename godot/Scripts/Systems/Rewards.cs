// 战后三选一：1~2 件道具 + 金币 + 回血，保证玩家总有得选。
namespace AbyssEcho.Systems;

using AbyssEcho.Core;
using AbyssEcho.Data;

public abstract class Reward
{
    public sealed class Item : Reward
    {
        public required ItemDef Def { get; init; }
    }

    public sealed class Gold : Reward
    {
        public int Amount { get; init; }
    }

    public sealed class Heal : Reward
    {
        public int Amount { get; init; }
        /// <summary>被面板上限截掉的那部分：界面要把它说出来，否则玩家会以为卡在骗人</summary>
        public int Clipped { get; init; }
        /// <summary>
        /// 这一格的数字是不是真的能拿到。
        ///
        /// 以前这张卡按「缺多少血」算：满血时 Amount 是下限 10，界面上写着
        /// 「回复 10 生命」，玩家拿走它，实际只回到上限——一格白给。
        /// 现在满血时 Amount 就是 0，界面据此写「你现在是满血」并把它压暗，
        /// 玩家至少知道自己挑的是什么。
        /// </summary>
        public bool Effective { get; init; } = true;
    }
}

public sealed class RewardContext
{
    /// <summary>
    /// 掉落装备「登记成运行时装备」的回调（Game.RegisterRuntimeItemCopy）。
    /// 奖励面板拿到的是一件模板的副本：带词条 / 换过品质的副本必须登记，
    /// 否则背包里只有一个查不到模板的 id —— 存档写不下、UI 也画不出来。
    /// null 表示没人接管（纯规则测试），那就用模板本身。
    /// </summary>
    public Func<ItemDef, ItemDef>? RegisterItem { get; init; }

    public int Floor { get; init; }
    public int MissingHp { get; init; }
    public EnemyTier Tier { get; init; } = EnemyTier.Normal;
    /// <summary>额外奖励条数：组合房间多打倒一只就多给一张卡</summary>
    public int Extra { get; init; }
    /// <summary>元进展「双生词条」：掉落品质权重往紫/橙挪，读档与开局都不变规则</summary>
    public bool DualAffixUnlocked { get; init; }
}

public static class RewardRoller
{
    /// <summary>
    /// 这一场仗的赏金。
    ///
    /// **试过加、又退回来了**：把赏金改成「再乘 8%/层 的成长、Boss ×3、精英 ×1.8」之后，
    /// 通关从 26/30 跳到 27/30，钱反而剩得更多（中位 506 → 892）。
    /// 原因是清楚的：装备一到顶，多出来的钱没有出口，只会变成结算面板上一个更大的数。
    /// 所以「钱没处花」的答案不在进项，在货架——见 `ShopPriceBand` / `ShopPriceFloor`。
    /// 留下这段记录，是为了下一个想调赏金的人先读到它。
    /// </summary>
    public static int GoldAmount(in RewardContext ctx, Rng rng) => 12 + ctx.Floor * 4 + rng.Int(0, 10);
    public static List<Reward> Roll(Rng rng, IReadOnlyList<ItemDef> items, RewardContext ctx)
    {
        var equipItems = items.Where(i => i.Slot != Slot.Consumable).ToList();
        var pool = new List<Reward>();

        // 掉落品质由敌人档位决定：奖励面板里只出现符合档位的装备。
        // 同一次掉落里不许出现「同一件装备的两个副本」：跑 500 局下来有 19 局
        // 两张卡是同一件（注册成 _r1 / _r2 之后连去重都认不出来），
        // 玩家看到的是「打了一场，给你两个一样的东西」。
        var picked = new HashSet<string>();
        var itemCount = rng.Int(1, 2);
        for (var i = 0; i < itemCount; i++)
        {
            var item = Loot.PickItemOfQuality(rng, equipItems, ctx.Tier, ctx.DualAffixUnlocked,
                picked);
            if (item is null || !picked.Add(item.Id)) continue;
            pool.Add(new Reward.Item { Def = ctx.RegisterItem?.Invoke(item) ?? item });
        }

        pool.Add(new Reward.Gold { Amount = GoldAmount(ctx, rng) });
        // 「回血」这张卡是这局真正的主力回复：横扫 30 局合计回 1 万点，
        // 而喝药只有二十几瓶（药是战斗里的应急储备，不是主要补给）。
        // 所以它的数值要按「一次补齐多少缺口」定，而不是按「缺多少砍一半」定——
        // 砍一半意味着战损一旦超过 50%，这张卡就永远填不平，血只会一路往下滑。
        // 现在补 70%，并保留一条下限：前期血薄的时候 10 点不至于变成一整条命。
        pool.Add(new Reward.Heal { Amount = Math.Max(10, (int)Math.Round(ctx.MissingHp * 0.7)) });

        // 洗牌后取 3 + extra 个，不足时循环补充。
        // 组合房间多给一张卡是「打赢了有回报」最直接的形式。
        //
        // 排序有个前提：**第一个位置是「没人选的时候会拿走的那张」**。
        // 宝箱房只给一张卡（`Rewards()[0]`），而以前洗完牌的第一张可能是「回血」——
        // 于是满血的人开箱也只好拿走一格空气，或者背包满时被迫丢掉一件有用的东西
        // 换回一堆没用的血。洗牌对金币/回血本来没有意义（三选一里它们是「保底」那一档），
        // 所以让它们排在后面：装备在前，金币/回血殿后。
        // 三选一的候选集合与数量一个没动，变的只是「谁站在第一个」。
        var want = 3 + Math.Max(0, ctx.Extra);
        var shuffled = rng.Shuffle(pool)
            .OrderBy(r => r is Reward.Item ? 0 : r is Reward.Gold ? 1 : 2)
            .ToList();
        var result = new List<Reward>();
        for (var i = 0; i < want; i++) result.Add(shuffled[i % shuffled.Count]);
        result = Dedupe(result, want);
        // 「回血」这张卡在满血时是个空盒子：把它的数字改成真的能拿到的那一份
        // （0 点），而不是照「缺多少血」的下限写 10。界面据此压暗并写清原因。
        for (var i = 0; i < result.Count; i++)
        {
            if (result[i] is Reward.Heal heal)
            {
                var real = Math.Max(0, Math.Min(heal.Amount, ctx.MissingHp));
                // Clipped > 0 表示「这张卡给得比你现在缺的多」：卡片要照实写，
                // 不能让玩家以为自己亏了十几点血——那是面板上限，不是惩罚。
                result[i] = new Reward.Heal
                {
                    Amount = real,
                    Effective = real > 0,
                    Clipped = Math.Max(0, heal.Amount - real),
                };
            }
        }
        return result;
    }

    private static List<Reward> Dedupe(List<Reward> rewards, int want)
    {
        var seen = new HashSet<string>();
        var outList = new List<Reward>();
        foreach (var r in rewards)
        {
            var key = r switch
            {
                Reward.Item item => $"item:{item.Def.Id}",
                Reward.Gold => "gold",
                _ => "heal",
            };
            if (!seen.Add(key)) continue;
            outList.Add(r);
        }
        // 去重后可能不足，允许重复金币/回血把名额补满。
        // 但补充只能补「金币 / 回血」这两种：以前是从原始列表按下标取，
        // 取到的是还没经过上面那轮去重的 Item——于是三选一里会出现两张
        // 一模一样的装备（一个 `item:xxx` 刚刚才被去重掉）。
        var fillers = outList.Where(r => r is not Reward.Item).ToList();
        var cursor = 0;
        while (outList.Count < want && fillers.Count > 0)
            outList.Add(fillers[cursor++ % fillers.Count]);
        // 连金币/回血都没有时（理论上不会），退回原始列表，至少别给空面板
        while (outList.Count < want && rewards.Count > 0) outList.Add(rewards[outList.Count % rewards.Count]);
        return outList.Take(want).ToList();
    }
}
