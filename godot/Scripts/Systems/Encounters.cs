// 「这间房打完该结什么」的唯一一张表。
//
// 上一轮记下的结论是：一连六条 bug 全长在「调用方与实现之间的那个空档」上——
// 注释写着「这里该清房」，代码里没有那一句；敌人卡上写着「下一刀 X」，报的是别人。
// 但它们其实不是一回事，得分开治：
//
//   · 守卫（守方/攻方/谁负责）能靠一张表钉住的，就钉住 —— 本文件。
//   · 「返回值说这一步做完了没有」那种，会把每个调用点都改成解构赋值，
//     而这游戏里真正需要它的只有一处（房间流程），摊到全工程反而是过度设计。
//
// 所以这里只做一件事：把「房间 → 结算方式」写成数据，让收尾只有一个入口，
// 对不上就当场抛。省掉的不是几行代码，是「某条路上忘了结」这种静默失败。
namespace AbyssEcho.Systems;

using AbyssEcho.Core;

/// <summary>一场战斗的结局。Escaped 单独一档：它既不是赢也不是输，但同样要有去处。</summary>
public enum BattleOutcome
{
    Won,
    Lost,
    Escaped,
}

public static class Encounters
{
    /// <summary>打完一间房该做什么</summary>
    public enum Settle
    {
        /// <summary>打赢结算赏金与掉落，房间清空</summary>
        Win,
        /// <summary>打输就是这一局结束</summary>
        Lose,
        /// <summary>逃跑：房间还不算清，玩家回地图，代价在 Escape() 里已经付过</summary>
        Flee,
    }

    /// <summary>
    /// 战斗房型的结算方式。写成表而不是散在各处 if 里：
    /// 以前「精英房打赢了要不要多给一次赏金」「Boss 房打输了算不算通关」这类判断
    /// 在 BattleScene、模拟器、探针三处各写一遍，改一处漏两处。
    /// </summary>
    private static readonly Dictionary<string, Settle> Table = new()
    {
        ["enemy"] = Settle.Win,
        ["elite"] = Settle.Win,
        ["boss"] = Settle.Win,
    };

    public static Settle SettleFor(string roomKey) =>
        Table.TryGetValue(roomKey, out var s)
            ? s
            : throw new InvalidOperationException($"没有登记的房间类型：{roomKey}");

    /// <summary>
    /// 一场战斗结束之后，这一局该怎么收尾。
    ///
    /// 这就是那个「谁负责」的显式化：返回的是「必须有人处理」的东西，
    /// 调用方拿不到合法值就只能抛/报错，不会像以前那样「什么都不做」也算正常结束。
    /// </summary>
    public static Settle AfterBattle(BattleOutcome outcome) => outcome switch
    {
        BattleOutcome.Won => Settle.Win,
        BattleOutcome.Lost => Settle.Lose,
        BattleOutcome.Escaped => Settle.Flee,
        _ => throw new InvalidOperationException($"没有登记的结局：{outcome}"),
    };

    /// <summary>这一局是不是已经结束（赢到底 / 死了）。逃跑不算结束。</summary>
    public static bool EndsRun(BattleOutcome outcome) => outcome == BattleOutcome.Lost;
}
