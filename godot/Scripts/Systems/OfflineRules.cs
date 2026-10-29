// 离线的代价：纯规则，不碰 Godot。
//
// 局内进度落盘会带来一个 TS 版没有的问题——玩家可以随时退出再回来。
// 如果存档等于「无损暂停」，那它就成了作弊器：打成残血就退出，回来还是残血，
// 但怪已经被打掉了；买之前先看价格，退出再进换一套商品。
// 所以这里定三条规矩，全部可断言：
//
// 1. 离线不推进游戏时间：不刷新商店、不重置冷却、不掉血也不回血。
// 2. 回来时敌人房的进度保留（房间已清空就还是清空），但战场本身不保留——
//    重开一场新的战斗。理由：一场战斗的中间状态（冷却、闪避、连击）比整局状态复杂一个量级，
//    保存它等于把 Battle 的内部结构写进存档格式，以后改一个结算细节就要迁移存档。
// 3. 存档带版本号：旧版本的存档读不出来就当作没有，绝不带着半个状态开局。
namespace AbyssEcho.Systems;

using AbyssEcho.Core;

public static class OfflineRules
{
    /// <summary>
    /// 存档格式版本。改结构就 +1，旧档按「没有」处理。
    /// v2：加上 beaten_boss_floors（首杀记录）——v1 的档没有这一项，
    /// 读回来会退化成「这一层 Boss 没打过」，正好是它要修的那个 bug。
    /// </summary>
    public const int SaveVersion = 2;

    /// <summary>
    /// 从存档恢复出来的一局是否还值得继续。
    /// 已经结束的局（通关/阵亡）不进「继续」按钮——那会让玩家点进去看到一屏结算。
    /// </summary>
    public static bool IsResumable(int status) => status == (int)GameStatus.Playing;

    /// <summary>版本号是否认得</summary>
    public static bool IsVersionSupported(int version) => version == SaveVersion;

    /// <summary>
    /// 恢复后需要修正的东西：只做「不可能成立的组合」的兜底，
    /// 不做补偿（不给回过血的玩家扣血，也不给残血的玩家加血）。
    /// </summary>
    public static void Sanitize(Game game)
    {
        var p = game.Player;
        var maxHp = game.Stats.MaxHp;
        if (p.Hp > maxHp) p.Hp = maxHp;
        if (p.Hp < 0) p.Hp = 0;

        // 背包与装备下标：存档是文本，手改过也不该让游戏崩
        if (p.Inventory.Count > game.Capacity)
        {
            p.Inventory.RemoveRange(game.Capacity, p.Inventory.Count - game.Capacity);
        }
        foreach (var slot in EntityRules.EquipSlots)
        {
            var idx = p.Equipped.Get(slot);
            if (idx is null || idx < 0 || idx >= p.Inventory.Count) p.Equipped.Set(slot, null);
        }
        // 玩家站的位置必须在图里
        if (game.PlayerIndex < 0 || game.PlayerIndex >= game.Map.Rooms.Count) game.TeleportForDebug(game.Map.StartIndex);
    }
}
