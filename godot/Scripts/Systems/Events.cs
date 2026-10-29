// 地图事件：抽签 + 按权重结算结果。
//
// 事件是「选项换代价」的地方，所以每个分支都要把文本写清楚——
// 玩家点了之后必须能从日志里看出自己刚才付了什么。
namespace AbyssEcho.Systems;

using AbyssEcho.Core;
using AbyssEcho.Data;

public sealed class EventApplyContext
{
    public required Rng Rng { get; init; }
    public required IReadOnlyList<ItemDef> Items { get; init; }
    public int Capacity { get; init; }
    public required IReadOnlyDictionary<string, ItemDef> ItemsById { get; init; }
    /// <summary>面板最大生命，用于诅咒/治疗按比例结算</summary>
    public int PanelMaxHp { get; init; }

    /// <summary>
    /// 交换类事件里「交出去哪一件」由玩家自己挑（界面传下标）。
    /// null 表示没有界面接管（自检 / 无头模拟），那就随机挑——规则不变，只是不打扰。
    /// </summary>
    public int? SwapOutIndex { get; init; }

    /// <summary>
    /// 换来的那件装备「登记成运行时装备」的回调。
    /// 必须走 Game 的那条路：换进来的装备若带着本层的符文（PendingBonusAffix），
    /// 只有 ApplyBonusAffix 会把它挂上并换一个唯一 id；直接塞模板 id 会让词条凭空消失，
    /// 而且背包里那件与模板共用同一个 id，身上的同类装备会被一起改掉。
    /// null 表示没人接管（自检 / 无头模拟），那就用模板本身。
    /// </summary>
    public Func<ItemDef, ItemDef>? PrepareItem { get; init; }

    /// <summary>
    /// 「这件装备要能被存档还原」的登记回调（Game.RegisterRuntimeItemCopy）。
    /// 它做两件事，别混：把副本放进 ItemIndex（否则背包里是一个查不到的 id，
    /// 界面上没有名字、点不动），以及给它一个唯一 id（否则存档写不下）。
    /// ApplyEffect 在「交给玩家」的每一处都会调它——GrantItem 也一样，
    /// 以前只有 Prepare 里调过一次，于是「背包满了换一件」那条路漏了。
    /// </summary>
    public Func<ItemDef, ItemDef>? RegisterItem { get; init; }

    /// <summary>
    /// 「钱进出了多少」的回调（Game.EarnGold / Game.SpendGold）。
    ///
    /// 事件里改金币以前直接在 Player 上加减，于是三本账对不上：
    /// `Game.GoldEarned` 只统计战斗赏金，而结算面板与存档的 `gold_earned`
    /// 读的正是它——「赌徒的钱袋 +35」既不出现在结算面板上，也不进存档；
    /// 付费选项那 10 金币同样是手写的 `SpendGold`，只是碰巧那条路走对了。
    /// 传负数表示花掉。null 表示没人接管（纯规则测试），那就退回直接改字段。
    /// </summary>
    public Action<int>? AddGold { get; init; }
}

public sealed class EventApplyResult
{
    public string Text { get; init; } = "";
    /// <summary>背包需要玩家手动丢弃时才提示</summary>
    public bool NeedDiscard { get; init; }
    public string? GainedItemId { get; init; }
}

public static class GameEvents
{
    /// <summary>
    /// 从事件池里抽一个事件；同一次游玩里尽量不重复。
    /// 事件池用完（深潜时必然发生）后要先把 used 清掉再抽，
    /// 否则就退化成「从全部事件里随机」——刚抽过的事件下一间房又来了。
    /// </summary>
    public static EventDef PickEvent(Rng rng, IReadOnlyList<EventDef> events, HashSet<string> used)
    {
        var available = events.Where(e => !used.Contains(e.Id)).ToList();
        if (available.Count == 0)
        {
            used.Clear();
            available = events.ToList();
        }
        var picked = rng.Pick(available);
        used.Add(picked.Id);
        return picked;
    }

    /// <summary>按权重决定事件结果</summary>
    public static EventOutcome ResolveChoice(Rng rng, IReadOnlyList<EventOutcome> outcomes) =>
        rng.Weighted(outcomes, o => o.Weight);

    /// <summary>把事件结果实际作用到玩家身上。返回日志文本与是否需要玩家处理背包溢出。</summary>
    public static EventApplyResult ApplyEffect(Player player, EventEffect effect, EventApplyContext ctx)
    {
        switch (effect.Type)
        {
            case EventEffectType.None:
                // 文案缺了也照样要有一条：玩家点了按钮，结果面板不能是一条空白
                return new EventApplyResult { Text = "什么也没有发生" };

            case EventEffectType.Gold:
            {
                var value = (int)Math.Round(effect.Value ?? 0);
                // 走 Game 那本账（它同时记 Player.GoldEarned / Game.GoldEarned / GoldLost）。
                // 以前这里直接在 Player 上加减：金币余额是对的，可「本局获得」只统计战斗赏金，
                // 结算面板与存档读的都是它——事件挣的钱等于没挣。
                if (ctx.AddGold is not null) ctx.AddGold(value);
                else
                {
                    player.Gold += value;
                    if (value >= 0) player.GoldEarned += value;
                    else player.GoldLost += -value;
                }
                return new EventApplyResult { Text = value >= 0 ? $"获得 {value} 金币" : $"失去 {-value} 金币" };
            }

            case EventEffectType.Hp:
            {
                var value = (int)Math.Round(effect.Value ?? 0);
                if (value < 0)
                {
                    // 不再保底留 1 点：残血时点「拿走钱袋」被炸死是玩家的账，
                    // 硬保 1 点等于给所有负面事件加了一条看不见的免死。
                    // 归零后由 UI 调 Game.CollapseIfDead() 结束这一局。
                    var lost = Math.Min(player.Hp, -value);
                    player.Hp -= lost;
                    return new EventApplyResult { Text = $"失去 {lost} 点生命" };
                }
                player.Hp = Math.Min(ctx.PanelMaxHp, player.Hp + value);
                return new EventApplyResult { Text = $"回复 {value} 点生命" };
            }

            case EventEffectType.HealPct:
            {
                var amount = (int)Math.Round(ctx.PanelMaxHp * (effect.Value ?? 0));
                player.Hp = Math.Min(ctx.PanelMaxHp, player.Hp + amount);
                return new EventApplyResult { Text = $"回复 {amount} 点生命" };
            }

            case EventEffectType.RandomItem:
            {
                var equipItems = ctx.Items.Where(i => i.Slot != Slot.Consumable).ToList();
                if (equipItems.Count == 0) return new EventApplyResult();
                // 只 Prepare、不在这里 Register：交给玩家的那道关在 GrantItem 里，
                // 那里才同时管到「塞进背包」和「背包满了要走换装」两条路。
                var template = ctx.Rng.Pick(equipItems);
                return GrantItem(player, Prepare(ctx, template), ctx);
            }

            case EventEffectType.SwapItem:
            {
                if (player.Inventory.Count == 0) return new EventApplyResult { Text = "你身上没有任何道具可交换" };
                // 玩家挑过就换他挑的那件；没人挑（自检、随机模拟）才 random 一件。
                // 注意这里不带 NeedDiscard：交换是原地替换，背包大小不变，
                // 带上它会让界面在交换完再问一次「背包满了要放下什么」。
                var lostIndex = ctx.SwapOutIndex is { } picked && picked >= 0 && picked < player.Inventory.Count
                    ? picked
                    : ctx.Rng.Int(0, player.Inventory.Count - 1);
                var equipItems = ctx.Items.Where(i => i.Slot != Slot.Consumable).ToList();
                var template = ctx.Rng.Pick(equipItems);
                // 换进来的那件要过一遍「运行时登记」：本层训练房的符文、以及唯一 id，
                // 都在 Game.ApplyBonusAffix 里。不做这一步，符文就这么被商人吞了。
                var gained = Prepare(ctx, template);
                // 换进来的这件必须进道具表：Prepare 只负责「生成副本」，
                // 而副本的 id 是运行时新发的（`_rN`）——没人登记 ItemIndex 的话，
                // 玩家背包里就多了一个查不到的 id：界面上没有名字、不能装备，
                // 存档也写不进去，看着就像「商人把你那件吞了」。
                ctx.RegisterItem?.Invoke(gained);
                var lostId = player.Inventory[lostIndex];
                var lostDef = ctx.ItemsById.TryGetValue(lostId, out var d) ? d : null;
                // 交换：原地替换，避免下标漂移影响装备
                player.Inventory[lostIndex] = gained.Id;
                var lostName = lostDef?.Name ?? lostId;
                return new EventApplyResult { Text = $"失去了「{lostName}」，换来了「{gained.Name}」", GainedItemId = gained.Id };
            }

            case EventEffectType.Cleanse:
                // 战斗内的中毒会被 ResetCombatState 收掉，这里只处理地图上的情况：
                // 没有中毒时给一句「白白摘了草」的反馈，别让玩家以为点了没反应。
                EntityRules.ClearPoison(player);
                return new EventApplyResult { Text = "毒素被涤荡一空" };

            case EventEffectType.Curse:
            {
                // 代价按「玩家看得见的上限」算：面板上限 = (基础 + 装备固定值) × 词条倍率。
                var hpPct = effect.HpPct ?? 0;
                var lost = (int)Math.Round(ctx.PanelMaxHp * (double)-hpPct);
                player.BaseMaxHp = Math.Max(10, player.BaseMaxHp - lost);
                // 收口也要按面板上限，不是 BaseMaxHp：装备的固定生命还在。
                player.Hp = Math.Min(player.Hp, Math.Max(1, ctx.PanelMaxHp - lost));
                player.BaseAttack += effect.Attack ?? 0;
                return new EventApplyResult { Text = $"最大生命 -{lost}，攻击力 +{effect.Attack ?? 0}" };
            }

            default:
                return new EventApplyResult();
        }
    }

    /// <summary>
    /// 事件给 / 换来的装备要过两道关，顺序不能反：
    /// 1) PrepareItem：吃掉本层的符文（Game.ApplyBonusAffix），返回一件副本；
    /// 2) RegisterItem：把副本登记进 ItemIndex 并换唯一 id。
    /// 只做第一步的话，副本的 id 与模板相同——背包里那件和模板共用 id，
    /// 而存档只写得下「运行时装备」（id 带 _rN），读档后这一格就凭空消失了。
    /// 第二步刻意不在 Prepare 里做，而是放在「真的要交给玩家」的那一刻
    /// （GrantItem / SwapItem 的原地替换）：在这里登记等于给一件可能没人收下的装备先占一个 id。
    /// </summary>
    private static ItemDef Prepare(EventApplyContext ctx, ItemDef template) =>
        ctx.PrepareItem?.Invoke(template) ?? template;

    private static EventApplyResult GrantItem(Player player, ItemDef item, EventApplyContext ctx)
    {
        // 同 SwapItem：拿到的这件得能按 id 查到，否则背包里那格是块空白。
        ctx.RegisterItem?.Invoke(item);
        if (player.Inventory.Count < ctx.Capacity)
        {
            player.Inventory.Add(item.Id);
            return new EventApplyResult { Text = $"获得「{item.Name}」", GainedItemId = item.Id };
        }
        // 背包满：替换下标 0 会让玩家无感，改为标记需要玩家手动处理
        return new EventApplyResult { Text = $"获得「{item.Name}」，但背包已满", NeedDiscard = true, GainedItemId = item.Id };
    }
}
