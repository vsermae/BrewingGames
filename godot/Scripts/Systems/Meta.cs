// 元进展：局外永久成长。
//
// 设计红线只有一条：不解锁任何数值强化。
// 回声碎片买的是「开局多一种选择」和「内容更早出现」，不是「初始攻击 +5」——
// 后者会让第一局就失去意义，玩家会在「刷碎片」和「玩游戏」之间选前者。
namespace AbyssEcho.Systems;

public enum MetaUnlock
{
    StarterGear,
    ExtraGold,
    ExtraSkill,
    SpecialRooms,
    DualAffix,
}

public sealed class MetaState
{
    public int Shards { get; set; }
    public List<MetaUnlock> Unlocked { get; set; } = new();
    public List<string> Achievements { get; set; } = new();

    /// <summary>
    /// 已经用这个存档开过几局。它只有一个用途：**第一局是教学局**——
    /// 那一局的五处解锁（三十块碎片全部买得起的量）摊在「用碎片买一个新按钮」后面，
    /// 等于让玩家在学会走路之前先逛商店。
    /// </summary>
    public int Runs { get; set; }

    public MetaState Clone() => new()
    {
        Shards = Shards,
        Unlocked = new List<MetaUnlock>(Unlocked),
        Achievements = new List<string>(Achievements),
        Runs = Runs,
    };
}

/// <summary>存档后端：Godot 里是 ConfigFile（GameStore autoload），测试里是内存。</summary>
public interface IMetaStorage
{
    MetaState Load();
    void Save(MetaState state);
}

public sealed class InMemoryMetaStorage : IMetaStorage
{
    private MetaState _state = new();
    public MetaState Load() => _state.Clone();
    public void Save(MetaState state) => _state = state.Clone();
}

public static class MetaProgression
{
    /// <summary>
    /// 三档解锁的成本。**这一版全部重定价**，原因是量出来的一本账：
    ///
    /// 100 局横扫一共赚到 31850 块碎片（平均每局 318）。而第一档三件加起来 90、
    /// 全部五件 320——也就是说，**打完一局就能把商店清空**。那五件标签买的
    /// 不是成长，是「第一局就全给」。
    ///
    /// 碎片按「一局 300 上下」定价，第一档要三局、第三档要十局：
    /// 60 / 60 / 60 是三件不同的「开局多一种选择」，凑齐要六局；
    /// 深处异象 400 一局半；双生词条 900 三局。总价 1480 ≈ 五局。
    /// </summary>
    public static readonly Dictionary<MetaUnlock, int> UnlockCost = new()
    {
        [MetaUnlock.StarterGear] = 60,
        [MetaUnlock.ExtraGold] = 60,
        [MetaUnlock.ExtraSkill] = 60,
        [MetaUnlock.SpecialRooms] = 400,
        [MetaUnlock.DualAffix] = 900,
    };

    public static readonly Dictionary<MetaUnlock, (string Name, string Desc, int Tier)> UnlockInfo = new()
    {
        [MetaUnlock.StarterGear] = ("行装预备", "开局可选携带 1 件白装", 1),
        [MetaUnlock.ExtraGold] = ("沉甸甸的钱袋", "开局多 20 金币", 1),
        [MetaUnlock.ExtraSkill] = ("旁门左道", "解锁火球以外的初始技能", 1),
        // 文案跟着实现走：宝箱怪是敌人不是房间，训练房与诅咒房才是这张解锁真正放出来的东西。
        [MetaUnlock.SpecialRooms] = ("深处异象", "训练房与诅咒房进入随机池", 2),
        // 双词条从来没有「品质解锁」这回事（词条数一直只由品质表决定），
        // 所以它改成了一件代码里真的会读到的事：深层掉落更容易出紫/橙。
        [MetaUnlock.DualAffix] = ("双生词条", "战斗掉落更容易出紫装与橙装", 3),
    };

    public static MetaState AddShards(MetaState state, int amount)
    {
        var next = state.Clone();
        next.Shards = Math.Max(0, state.Shards + Math.Max(0, amount));
        return next;
    }

    /// <summary>解锁。碎片不够或已解锁时返回 null，不改状态</summary>
    public static MetaState? TryUnlock(MetaState state, MetaUnlock id)
    {
        if (state.Unlocked.Contains(id)) return null;
        var cost = UnlockCost[id];
        if (state.Shards < cost) return null;
        var next = state.Clone();
        next.Unlocked.Add(id);
        next.Shards -= cost;
        return next;
    }

    public static MetaState GrantAchievement(MetaState state, string id)
    {
        if (state.Achievements.Contains(id)) return state;
        var next = state.Clone();
        next.Achievements.Add(id);
        return next;
    }

    public static bool HasUnlock(MetaState state, MetaUnlock id) => state.Unlocked.Contains(id);

    public static (int Done, int Total) UnlockProgress(MetaState state) =>
        (state.Unlocked.Count, UnlockCost.Count);
}
