// 成长系统：击败敌人累积经验，够数就升级。
//
// 没有它之前，玩家的属性只来自初始值 + 装备，第 5 层 Boss 之后就是数值天花板。
// 升级把「打得越深 → 越强」接回了同一条线上。
namespace AbyssEcho.Systems;

using AbyssEcho.Core;
using AbyssEcho.Data;

public sealed class LevelUpResult
{
    public int Level { get; init; }
    /// <summary>面板属性的增量，UI 用来飘字（生命按增益值涨，而不是直接拉满）</summary>
    public int MaxHpGain { get; init; }
    public int AttackGain { get; init; }
    public int DefenseGain { get; init; }
    /// <summary>本次升级习得的新技能；没有新技能时为 null</summary>
    public SkillDef? Learned { get; init; }
}

public static class Leveling
{
    /// <summary>从 1 级升到 2 级所需经验</summary>
    public static int XpForLevel(int level, LevelingDef def) =>
        (int)Math.Round(def.BaseXp * Math.Pow(def.Growth, Math.Max(0, level - 1)));

    /// <summary>
    /// 结算经验并尽可能多地升级（一次拿到大量经验时不会卡在半级）。
    /// 属性直接加在 BaseMaxHp / BaseAttack / BaseDefense 上：升级的收益必须和装备加成
    /// 走同一条面板计算路径，否则「诅咒」这类百分比改动会和它打架。
    /// 生命按增量上涨而不是回满——升级不该顺带当一瓶大药水。
    /// </summary>
    public static List<LevelUpResult> GainXp(Player player, int amount, LevelingDef def, IReadOnlyList<SkillDef> skills)
    {
        var results = new List<LevelUpResult>();
        if (amount <= 0) return results;
        player.Xp += amount;

        // 防御每 DefenseEveryLevels 级加一次，间隔由数据决定，别在这里写死 2
        var defenseStep = Math.Max(1, def.DefenseEveryLevels);
        var guard = 0;
        while (player.Xp >= XpForLevel(player.Level, def) && guard++ < 200)
        {
            player.Xp -= XpForLevel(player.Level, def);
            player.Level += 1;

            var defenseGain = player.Level % defenseStep == 0 ? def.DefensePerLevel : 0;
            // 涨多少上限就回多少血，但收口要用**面板上限**而不是 BaseMaxHp。
            //
            // 面板上限 = (基础 + 装备固定生命) × 词条倍率：戴着 +8 生命的戒指、
            // 或者身上有「脆弱躯壳」（生命 +20%）时，面板上限比 BaseMaxHp 高得多。
            // 以前用 Math.Min(BaseMaxHp, Hp + 增量)，于是升级那一刻生命被**压回**
            // 一个比面板低的值——玩家刚升一级，血量反而掉了一截。
            // 极端一点的情形：基础 60、当前 50 血、身上没有装备时升一级，
            // 旧代码给的是 min(66, 56) = 56；一旦身上有 +30 生命的饰品，
            // 玩家在面板上看到的上限是 96，而代码按 66 收口——升级那一刻血被白扣了一截。
            player.BaseMaxHp += def.HpPerLevel;
            player.BaseAttack += def.AttackPerLevel;
            player.BaseDefense += defenseGain;
            // 升级只涨上限，**不涨血**。
            //
            // 以前这里写的是 `Hp = Math.Min(levelUpMaxHp, Hp + HpPerLevel)`：它自己想当
            // 「血跟着涨」的那只手，而 `Game.GrantXp` 与 `BeforeMaxHpGrows` 也当一次——
            // 三次加血里有两次是重复的，具体表现是「升一级，血比上限涨得还多」。
            // 现在只留一处：规则层改数值，`Game` 那句「血涨的 == 面板涨的」负责补齐。

            results.Add(new LevelUpResult
            {
                Level = player.Level,
                MaxHpGain = def.HpPerLevel,
                AttackGain = def.AttackPerLevel,
                DefenseGain = defenseGain,
                Learned = LearnSkill(player, def, skills),
            });
        }
        return results;
    }

    /// <summary>
    /// 按升级次数从技能池里依次解锁：第 2 级给池子里的第一个，第 3 级给第二个……
    /// 池子用完就不再给新技能，玩家仍然拿属性成长。
    /// </summary>
    private static SkillDef? LearnSkill(Player player, LevelingDef def, IReadOnlyList<SkillDef> skills)
    {
        var known = player.Skills.Select(s => s.Id).ToHashSet();
        var next = def.Pool
            .Select(id => skills.FirstOrDefault(s => s.Id == id))
            .FirstOrDefault(s => s is not null && !known.Contains(s.Id));
        if (next is null) return null;
        player.Skills = player.Skills.Append(next).ToList();
        return next;
    }

    /// <summary>当前等级的经验进度，UI 的经验条直接用它</summary>
    public static (int Current, int Needed, double Ratio) XpProgress(Player player, LevelingDef def)
    {
        var needed = XpForLevel(player.Level, def);
        return (player.Xp, needed, Math.Min(1, (double)player.Xp / needed));
    }
}
