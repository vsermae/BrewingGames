// 背包装备：背包是道具 id 的数组，装备槽存的是「背包下标」。
// 下标方案让「装备中的道具」和背包是同一份数据，卸下不需要复制。
namespace AbyssEcho.Systems;

using AbyssEcho.Core;
using AbyssEcho.Data;

public static class Inventory
{
    public static readonly string[] EquipSlots = EntityRules.EquipSlots;

    public static bool AddItem(Player player, string itemId, int capacity)
    {
        if (player.Inventory.Count >= capacity) return false;
        player.Inventory.Add(itemId);
        return true;
    }

    /// <summary>背包满时用新道具替换指定下标；若该下标正被装备，装备保持不变（换成了新道具）</summary>
    public static string? ReplaceItem(Player player, int index, string itemId)
    {
        if (index < 0 || index >= player.Inventory.Count) return null;
        var old = player.Inventory[index];
        player.Inventory[index] = itemId;
        return old;
    }

    public static string? RemoveItem(Player player, int index)
    {
        if (index < 0 || index >= player.Inventory.Count) return null;
        var removed = player.Inventory[index];
        player.Inventory.RemoveAt(index);
        // 删除后修正在其之后的装备下标
        foreach (var slot in EquipSlots)
        {
            var idx = player.Equipped.Get(slot);
            if (idx is null) continue;
            if (idx.Value == index) player.Equipped.Set(slot, null);
            else if (idx.Value > index) player.Equipped.Set(slot, idx.Value - 1);
        }
        return removed;
    }

    /// <summary>
    /// 「用一件新道具换掉背包里的一件」的原子操作：丢弃 + 入手必须一起成功。
    /// 拆成两步写时，任一步的下标失效都会悄悄吞掉玩家的道具或金币。
    /// </summary>
    public static string? SwapInItem(Player player, int dropIndex, string itemId)
    {
        var dropped = ReplaceItem(player, dropIndex, itemId);
        if (dropped is null) return null;
        // 被替换的位置若正被装备，装备下标不用动（还是同一个位置），
        // 但「装备的已经不是原来那件」这件事要在调用方可见——
        // 返回被丢掉的那件，界面才能把「你失去了 X」写清楚。
        return dropped;
    }

    /// <summary>
    /// 换装。生命上限的变化要按「增量」结转：
    /// 面板上限是「基础 + 装备固定值」（再乘百分比词条），而当前生命是绝对值——
    /// 所以上限变动时当前生命要**一起平移**同样的点数（见 ClampHpToPanel）：
    /// 涨 20 就跟着涨 20（仍受新上限约束），降 20 就跟着降 20（不低于 1）。
    /// 换装于是变成一件「等你受伤之后才值钱」的事，而不是一瓶药。
    /// 以前两件都不做：换装既凭空回血 20（撞上 Min 就白送），也能让生命超上限。
    ///
    /// 这里的上限必须带词条那一份（<see cref="PanelMaxHp"/>），而不是只算固定值：
    /// 只算固定值时，一件带「脆弱躯壳（最大生命 +20%）」的装备换下来，
    /// 上限看着降了、实际只降了固定值那一小块，而当前生命会被压到那个偏高的数上——
    /// 玩家身上于是留下 70/60 这种「比上限多的血」，下一次治疗又被吃光。
    /// </summary>
    public static bool Equip(Player player, int index, IReadOnlyDictionary<string, ItemDef> itemsById)
    {
        if (index < 0 || index >= player.Inventory.Count) return false;
        var itemId = player.Inventory[index];
        if (!itemsById.TryGetValue(itemId, out var def) || def.Slot == Slot.Consumable) return false;
        var slot = SlotKey(def.Slot);
        var before = PanelMaxHp(player, itemsById);
        player.Equipped.Set(slot, index);
        ClampHpToPanel(player, before, PanelMaxHp(player, itemsById));
        return true;
    }

    /// <summary>
    /// 这件东西卖掉能拿回多少。
    ///
    /// 约三成：够让「换装剩下的那件旧货」不至于白扔，又不至于让人靠倒买倒卖赚钱
    /// （商店加价买、三成卖出，一来一回永远是亏的）。数据表里写了 `sell` 就照写的来。
    /// </summary>
    public static int SellValue(ItemDef def) => def.Sell ?? Math.Max(1, (int)Math.Round(def.Price * 0.3));

    /// <summary>
    /// 卖掉背包里的一格。返回 (卖掉的 id, 到手金币)；下标无效或那一格查不到模板时返回 null。
    ///
    /// 为什么要有这个动作：背包是 6 格，而「换装」必然留下一件旧的。
    /// 没有销路的时候，player 唯一的出路是「捡到东西 → 背包满 → 把一件有用的丢掉」，
    /// 于是活到第 6 层的玩家永远是「六格全是装备、一瓶药都没有」，
    /// 而商店里那瓶药永远买不进来。这不是难度，是缺了一条最基本的出口。
    /// </summary>
    public static (string Id, int Gold)? Sell(Player player, int index, IReadOnlyDictionary<string, ItemDef> itemsById)
    {
        if (index < 0 || index >= player.Inventory.Count) return null;
        var id = player.Inventory[index];
        if (!itemsById.TryGetValue(id, out var def)) return null;
        var gold = SellValue(def);
        // 卖之前先卸下：RemoveItem 会修装备下标，但「卖掉穿着的那件」应该先摘
        if (IsEquipped(player, index) is { } slot) player.Equipped.Set(slot, null);
        RemoveItem(player, index);
        return (id, gold);
    }

    /// <summary>卸下装备：同样要按上限降幅压一次当前生命</summary>
    public static void Unequip(Player player, string slot, IReadOnlyDictionary<string, ItemDef> itemsById)
    {
        var before = PanelMaxHp(player, itemsById);
        player.Equipped.Set(slot, null);
        ClampHpToPanel(player, before, PanelMaxHp(player, itemsById));
    }

    /// <summary>
    /// 玩家看得见的生命上限 = (基础 + 装备固定值) × 词条倍率。
    ///
    /// 词条那一份必须算进来，因为这就是玩家读血条时用的那个数。
    /// 以前这里只算固定值，于是卸下一件「脆弱躯壳（最大生命 +20%）」之后，
    /// 上限从 72 降到 60，收口却按一个偏高的数去压——玩家身上留下 72/60 这种
    /// 超额血，之后每一次治疗都被 Math.Min 吃掉。这一条和「换装等于喝了一瓶药」
    /// 是同一处接缝的两头。
    /// </summary>
    public static int PanelMaxHp(Player player, IReadOnlyDictionary<string, ItemDef> itemsById)
    {
        var flat = player.BaseMaxHp;
        foreach (var slot in EquipSlots)
        {
            var def = EquippedDef(player, slot, itemsById);
            if (def is not null) flat += def.MaxHp ?? 0;
        }
        // 词条按「当前装备」重算。调用方（Equip / Unequip）在动手之前先取一次、
        // 动手之后再取一次，两次的差就是「这次换装让上限动了多少」。
        var affixes = AffixMath.PlayerAffixes(player, itemsById);
        return Math.Max(1, AffixMath.MaxHpWithAffixes(flat, affixes));
    }

    /// <summary>
    /// 上限降了才压血；上限涨了不动当前生命（换装不该当一瓶药喝）。
    ///
    /// 「上限涨了不动当前生命」只对**满血**成立。伤着的人换上一件 +8 生命的戒指，
    /// 卸下时上限回到 60、当前生命被压到 60（满血），再戴回去上限又是 68 ——
    /// 现在他是 60/68，能再喝一瓶。反复「摘 → 喝满 → 戴」就是一台无限回血机，
    /// 而且它长得不像 bug：每一步都符合「上限降了要压血」那条规则。
    ///
    /// 修法是把「上限变动」当成一次**按比例平移**：上限降 8、当前生命也降 8
    /// （但不低于 1），上限升 8、当前生命也升 8（但不超过新上限）。
    /// 这样反复换装前后是同一个百分比，白拿不到任何一点血；
    /// 而 +MaxHp 那件装备仍然实打实地给你「多一些能装下的血量上限」。
    /// </summary>
    private static void ClampHpToPanel(Player player, int beforeMax, int afterMax)
    {
        var delta = afterMax - beforeMax;
        if (delta == 0) return;
        player.Hp = Math.Clamp(player.Hp + delta, 1, Math.Max(1, afterMax));
    }

    public static ItemDef? EquippedDef(Player player, string slot, IReadOnlyDictionary<string, ItemDef> itemsById)
    {
        var idx = player.Equipped.Get(slot);
        if (idx is null) return null;
        if (idx.Value < 0 || idx.Value >= player.Inventory.Count) return null;
        return itemsById.TryGetValue(player.Inventory[idx.Value], out var def) ? def : null;
    }

    public static string? IsEquipped(Player player, int index)
    {
        foreach (var slot in EquipSlots)
        {
            if (player.Equipped.Get(slot) == index) return slot;
        }
        return null;
    }

    public static List<(int Index, ItemDef Def)> Consumables(Player player, IReadOnlyDictionary<string, ItemDef> itemsById)
    {
        var outList = new List<(int, ItemDef)>();
        for (var i = 0; i < player.Inventory.Count; i++)
        {
            if (itemsById.TryGetValue(player.Inventory[i], out var def) && def.Slot == Slot.Consumable)
                outList.Add((i, def));
        }
        return outList;
    }

    /// <summary>装备下标是否仍指向合法位置（读档/换装后的自检）</summary>
    public static bool IsEquippedIndexValid(Player player)
    {
        foreach (var slot in EquipSlots)
        {
            var idx = player.Equipped.Get(slot);
            if (idx is not null && (idx.Value < 0 || idx.Value >= player.Inventory.Count)) return false;
        }
        return true;
    }

    public static string SlotKey(Slot slot) => slot switch
    {
        Slot.Weapon => "weapon",
        Slot.Armor => "armor",
        Slot.Trinket => "trinket",
        _ => "consumable",
    };

    // ---- 品质与词条 ----

    /// <summary>装备的有效品质：没写则按 Rarity 推导，旧数据不用迁移</summary>
    public static Quality QualityOf(ItemDef def) => def.Quality ?? def.Rarity switch
    {
        "common" => Quality.White,
        "rare" => Quality.Blue,
        "epic" => Quality.Orange,
        _ => Quality.White,
    };

    public static QualityDef QualityDefOf(Quality quality)
    {
        if (!GameDataStore.Get().Qualities.TryGetValue(quality, out var def))
            throw new InvalidOperationException($"未定义的装备品质: {quality}");
        return def;
    }

    /// <summary>该品质允许的词条数量上限</summary>
    public static int AffixLimit(ItemDef def) => QualityDefOf(QualityOf(def)).AffixCount;

    /// <summary>装备自带的词条 id 列表（不会超过品质上限）</summary>
    public static List<string> AffixesOf(ItemDef def) =>
        (def.Affixes ?? new List<string>()).Take(AffixLimit(def)).ToList();

    /// <summary>当前装备的词条视图，面板与战斗共用</summary>
    public static ActiveAffixes EquippedAffixes(Player player, IReadOnlyDictionary<string, ItemDef> itemsById) =>
        AffixMath.PlayerAffixes(player, itemsById);

    /// <summary>装备固定属性之和（不含百分比词条）</summary>
    public static (int Attack, int Defense, int MaxHp) FlatBonus(Player player, IReadOnlyDictionary<string, ItemDef> itemsById)
    {
        int attack = 0, defense = 0, maxHp = 0;
        foreach (var slot in EquipSlots)
        {
            var idx = player.Equipped.Get(slot);
            if (idx is null || idx.Value < 0 || idx.Value >= player.Inventory.Count) continue;
            if (!itemsById.TryGetValue(player.Inventory[idx.Value], out var def)) continue;
            attack += def.Attack ?? 0;
            defense += def.Defense ?? 0;
            maxHp += def.MaxHp ?? 0;
        }
        return (attack, defense, maxHp);
    }
}
