// 战斗单位的形状与状态管理。
//
// 注释里那几条（闪避分两份、CLEAR_STATUS 不展开 cooldowns）都是 TS 版踩过的坑，
// 移植时原样保留——它们不是历史包袱，是规则的一部分。
namespace AbyssEcho.Core;

using AbyssEcho.Data;

/// <summary>装备提供的属性加成之和</summary>
public struct Bonus
{
    public int Attack;
    public int Defense;
    public int MaxHp;

    public static readonly Bonus Empty = new();
}

public class Combatant
{
    public string Name { get; set; } = "";
    public int BaseMaxHp { get; set; }
    public int BaseAttack { get; set; }
    public int BaseDefense { get; set; }
    public int Hp { get; set; }
    public bool Defending { get; set; }

    /// <summary>下一回合的攻击力倍率（强化技能写入）</summary>
    public double AttackMultiplier { get; set; } = 1;

    /// <summary>剩余冷却回合，key 为技能 id</summary>
    public Dictionary<string, int> Cooldowns { get; set; } = new();

    public int PoisonTurns { get; set; }
    public int PoisonPower { get; set; }
    /// <summary>本局里中毒还允许毒死几次（只在玩家身上有意义，见 <see cref="PoisonLethalAllowed"/>）</summary>
    public int PoisonDyingLeft { get; set; }

    public int AtkMod { get; set; }
    public int AtkModTurns { get; set; }
    public int DefMod { get; set; }
    public int DefModTurns { get; set; }

    /// <summary>本回合闪避率（相位穿行、躁动/幽暗层读它）</summary>
    public double Evade { get; set; }

    /// <summary>
    /// 楼层机制给的常驻闪避（躁动 20% / 幽暗 30%）。
    /// 与技能给的闪避分开记：回合末清技能那一份时要把楼层这份还回去。
    /// </summary>
    public double BaseEvade { get; set; }

    /// <summary>
    /// 剩余「被震住」的回合数。1 表示「下一次轮到我时这一回合作废」。
    ///
    /// 这个数字必须在跨过一次自己的出手机会之后才递减，而不是在回合末一律 -1：
    /// 玩家先手，眩晕是玩家在**自己这一回合**挂上去的，而 EndRound 在同一回合末就把
    /// StunTurns 从 1 减到 0 —— 等敌人真的轮到出手时它已经醒着，于是
    /// 「震颤」的说明写着「眩晕 1 回合」，敌人一点没被影响。实测 2000/2000 次。
    /// 递减入口只有 EntityRules.ConsumeStun 一处，由 Battle.EnemyAct 在「轮到它出手」时调。
    /// </summary>
    public int StunTurns { get; set; }
    public bool? ImmunePoison { get; set; }
    public bool? UsedSurviveLethal { get; set; }
    public bool? RiskHitTaken { get; set; }
    public int? TurnStartHpLoss { get; set; }
    public bool? MoltenBurst { get; set; }
    public bool? FrostMirror { get; set; }
    /// <summary>深渊领域本回合张着手（周期型 Boss 标记，与熔岩爆裂/冰镜同一种用法）</summary>
    public bool? AbyssDomain { get; set; }

    public List<SkillDef> Skills { get; set; } = new();
    public bool IsBoss { get; set; }

    /// <summary>
    /// 这一侧是不是「玩家」。用它而不是 `is Player`：
    /// `EntityRules.CreateMonster` 造出来的敌人是普通 Combatant，判 `is Player` 永远为假，
    /// 但「谁会中毒 / 谁的状态会被驱散」这类判据在两边的逻辑里长得一模一样，
    /// 写反了不会报错，只会静默地少做一件事。见 Skills.UseSkill 里的中毒与驱散。
    /// </summary>
    public bool IsPlayer { get; set; }

    /// <summary>模板 id（怪物是 game_data 里的 id，玩家为空）。日志与调试靠它认人</summary>
    public string Kind { get; set; } = "";

    /// <summary>清掉战斗内的临时状态。新建单位、战斗结束都回到这一组。</summary>
    public void ClearStatus()
    {
        Defending = false;
        UsedSurviveLethal = false;
        RiskHitTaken = false;
        AttackMultiplier = 1;
        PoisonTurns = 0;
        PoisonPower = 0;
        AtkMod = 0;
        AtkModTurns = 0;
        DefMod = 0;
        DefModTurns = 0;
        Evade = 0;
        StunTurns = 0;
    }
}

/// <summary>玩家在战斗者之上多一层装备、背包与成长状态</summary>
public sealed class Player : Combatant
{
    public int Gold { get; set; }
    public List<string> Inventory { get; set; } = new();
    public Equipped Equipped { get; set; } = new();
    public int Kills { get; set; }
    public int GoldEarned { get; set; }
    public int GoldLost { get; set; }
    /// <summary>角色等级，从 1 开始</summary>
    public int Level { get; set; } = 1;
    public int Xp { get; set; }
}

public sealed class Equipped
{
    public int? Weapon { get; set; }
    public int? Armor { get; set; }
    public int? Trinket { get; set; }

    public int? Get(string slot) => slot switch
    {
        "weapon" => Weapon,
        "armor" => Armor,
        "trinket" => Trinket,
        _ => null,
    };

    public void Set(string slot, int? index)
    {
        switch (slot)
        {
            case "weapon": Weapon = index; break;
            case "armor": Armor = index; break;
            case "trinket": Trinket = index; break;
        }
    }

    public Equipped Clone() => new() { Weapon = Weapon, Armor = Armor, Trinket = Trinket };
}

public struct PlayerStats
{
    public int MaxHp;
    public int Attack;
    public int Defense;
}

public static class EntityRules
{
    public static readonly string[] EquipSlots = { "weapon", "armor", "trinket" };

    /// <summary>一份全新的冷却表。每个单位都要有自己的，不能共用同一个对象。</summary>
    public static Dictionary<string, int> NewCooldowns() => new();

    public static Player CreatePlayer(int hp, int attack, int defense, int gold, List<SkillDef> skills)
    {
        var p = new Player
        {
            Name = "你",
            BaseMaxHp = hp,
            BaseAttack = attack,
            BaseDefense = defense,
            Hp = hp,
            Skills = skills,
            IsBoss = false,
            IsPlayer = true,
            Kind = "player",
            Gold = gold,
            Level = 1,
            Xp = 0,
        };
        // 中毒的致死预算在 Game 里按层数与比例写入（FloorMechanics.PoisonLethalShare），
        // 这里只给一个 0：没走 Game 的场合（单步自检）不该悄悄多出几次免死。
        p.ClearStatus();
        p.Cooldowns = NewCooldowns();
        return p;
    }

    public static Combatant CreateMonster(MonsterDef def, List<SkillDef> skills, int floor)
    {
        // 楼层加成：每下一层敌人属性略增，Boss 不再额外放大。
        // 比率走数据（config.enemyGrowth）。0.10 是「敌人技能被共用冷却表压制」时的假基线，
        // 技能恢复出手后重新标定成 0.05。
        var growth = GameDataStore.Get().Config.EnemyGrowth;
        var scale = (def.IsBoss ?? false) ? 1.0 : 1 + (floor - 1) * growth;
        var maxHp = (int)Math.Round(def.Hp * scale);
        var c = new Combatant
        {
            Name = def.Name,
            IsPlayer = false,
            Kind = def.Id,
            BaseMaxHp = maxHp,
            BaseAttack = (int)Math.Round(def.Attack * scale),
            BaseDefense = (int)Math.Round(def.Defense * scale),
            Hp = maxHp,
            Skills = skills,
            IsBoss = def.IsBoss ?? false,
        };
        c.ClearStatus();
        c.Cooldowns = NewCooldowns();
        return c;
    }

    /// <summary>
    /// 面板属性 = 基础属性 + 已装备道具的固定加成，再乘百分比词条。
    /// 顺序固定为「先加固定值、后乘百分比」：换一把数值更好的剑会让百分比词条一起涨。
    /// </summary>
    public static PlayerStats PlayerStatsOf(Player player, IReadOnlyDictionary<string, ItemDef> itemsById, double attackPct = 0)
    {
        double attack = player.BaseAttack;
        double defense = player.BaseDefense;
        var maxHp = player.BaseMaxHp;
        foreach (var slot in EquipSlots)
        {
            var invIndex = player.Equipped.Get(slot);
            if (invIndex is null) continue;
            var idx = invIndex.Value;
            if (idx < 0 || idx >= player.Inventory.Count) continue;
            var itemId = player.Inventory[idx];
            if (!itemsById.TryGetValue(itemId, out var def)) continue;
            attack += def.Attack ?? 0;
            defense += def.Defense ?? 0;
            maxHp += def.MaxHp ?? 0;
        }
        var scale = 1 + attackPct;
        return new PlayerStats
        {
            MaxHp = maxHp,
            Attack = Math.Max(1, (int)Math.Round(attack * scale)),
            Defense = (int)Math.Round(defense),
        };
    }

    /// <summary>生效中的攻击力 = 面板攻击 + 临时修正（降攻技能、楼层加成）</summary>
    public static int EffectiveAttack(Combatant c, int baseAtk) => c.AtkModTurns > 0 ? baseAtk + c.AtkMod : baseAtk;

    /// <summary>生效中的防御力 = 面板防御 + 临时修正（锈蚀的破防）</summary>
    public static int EffectiveDefense(Combatant c, int baseDef) => c.DefModTurns > 0 ? baseDef + c.DefMod : baseDef;

    /// <summary>
    /// 回合结束时把临时修正的剩余回合数减掉，避免状态跨回合赖着不走。
    /// 眩晕不在这里递减——它按「自己的出手机会」计，见 ConsumeStun。
    /// </summary>
    public static void TickStatus(Combatant c)
    {
        if (c.AtkModTurns > 0 && --c.AtkModTurns == 0) c.AtkMod = 0;
        if (c.DefModTurns > 0 && --c.DefModTurns == 0) c.DefMod = 0;
    }

    /// <summary>
    /// 消耗一层眩晕。只在「这个单位真的轮到出手」时调：
    /// 被震住就作废这一次出手，然后扣一层。
    /// 早期版本在每回合末一律 -1，而眩晕是玩家在自己回合挂上的，
    /// 同一个回合末就被扣光了——说明写着「眩晕 1 回合」，敌人一次都没被震住。
    /// </summary>
    public static void ConsumeStun(Combatant c)
    {
        if (c.StunTurns > 0) c.StunTurns -= 1;
    }

    /// <summary>
    /// 这个单位「这一回合能不能行动」。
    /// 目前只有一个来源（眩晕），但它被提成了一条独立的判据：
    /// 这句话在战斗里有三处要问（敌人出手、预告、自检），散着写迟早有一处漏。
    /// </summary>
    public static bool CannotAct(Combatant c) => c.StunTurns > 0;

    /// <summary>回合末收闪避：技能给的那一份只保一回合，楼层机制给的常驻闪避要还回去。</summary>
    public static void ExpireEvade(Combatant c) => c.Evade = c.BaseEvade;

    public static bool IsAlive(Combatant c) => c.Hp > 0;

    public static int Heal(Combatant c, int amount, int maxHp)
    {
        var before = c.Hp;
        c.Hp = Math.Min(maxHp, c.Hp + amount);
        return c.Hp - before;
    }

    public static int TakeDamage(Combatant c, int amount)
    {
        var dealt = Math.Min(c.Hp, amount);
        c.Hp -= dealt;
        return dealt;
    }

    /// <summary>
    /// 一层里中毒最多毒死玩家几次（上界；具体几次由 `FloorMechanics.PoisonDyingBudgetFor` 按层给）。
    /// </summary>
    public const int MaxPoisonDeathsPerFloor = 4;

    /// <summary>
    /// 中毒这一回合扣不扣血。**规则**，不是技巧：一层里中毒最多毒死玩家
    /// `Game.PoisonDyingBudget` 次，用完就停手。
    ///
    /// 为什么要一条规则来管这件事：毒伤压得太低，「毒 + 下一刀」会要命的回合在 30 局里
    /// 只剩 2/30，解毒草就永远是零使用——一个没有场的道具，价格怎么定都是假的。
    /// 毒伤抬回去又会让深层变成「毒死的」，与「毒不是主角」冲突。
    /// 折中是让毒**偶尔**真的杀人：够疼就会被计算，但不能变成死因表的主项。
    ///
    /// 预算只拦「这一下会不会致死」，不拦伤害本身：撑得住的那一下照扣。
    /// 敌人身上的毒不设预算——那条规则说的是玩家怎么死。
    ///
    /// **`damage` 是「这一下本来要掉多少」，不是「还差多少掉到 0」**：
    /// 探针以前传的是当前血量，判据 `damage < c.Hp` 于是永远不成立、永远返回 true。
    /// 同理，玩家亲手投出的那一刀（炸弹）也不是毒，它走 `IsFixedDamage`，不该被这条预算拦下。
    /// </summary>
    public static bool PoisonLethalAllowed(Combatant c, int damage)
    {
        if (damage <= 0 || damage < c.Hp) return true;
        // 预算用完 ≠ 这一层的毒从此不许杀任何人：`PoisonDyingLeft` 是**余额**，
        // 归零就是「额度用完了」，只拦这一次。
        return c.PoisonDyingLeft > 0;
    }

    /// <summary>毒真的把人带走了：用掉一次预算</summary>
    public static void NotePoisonDying(Combatant c)
    {
        if (c.PoisonDyingLeft > 0) c.PoisonDyingLeft -= 1;
    }

    /// <summary>给目标挂上中毒；重复命中取更长的持续与更高的伤害，不会叠成无限掉血</summary>
    /// <summary>
    /// 中毒每回合的掉血量。
    ///
    /// 以前的写法是「按这一刀的 50%」，而这一刀本身随层数长（敌人攻击 ×1+5%/层），
    /// 于是**毒是全游戏里唯一一条指数级成长的伤害**：横扫 30 局里最深处的毒伤
    /// 是 49 点，一回合掉掉玩家满血的四分之一，还剩两回合。
    /// 而解毒草只在「这一回合的毒会要命」时才值得吃——那个局面上，吃草也只是
    /// 把两回合的账往后挪一回合：下一刀照样落下来。
    ///
    /// 所以毒伤跟着**基础攻击力**走（那一项不随层数膨胀），封在 1..MaxPoisonPower。
    /// 深层仍然更疼，但那是因为怪本身更狠，不是因为一条持续伤害偷偷跑到了前面。
    /// 上界写在这里而不是调用点：毒有三种挂法（技能、瘴气、熔岩灼烧），
    /// 任何一处自己算一遍，就一定会有一处漏掉。
    /// </summary>
    public const int MaxPoisonPower = 20;

    /// <summary>
    /// 毒伤按**目标血条**的一小截算：这是「毒有多重」的唯一定义。
    ///
    /// 上一版是按敌人基础攻击力折算、夹在 1..6 的一条常数链。它解决了一个真问题
    /// （毒不能随层数指数增长，不然深层的毒一回合掉四分之一条命），但也顺手
    /// 把毒从这个游戏里删掉了：60 血的面板和 137 血的面板，每回合中的毒都是 6 点，
    /// 100 局横扫 311 个中毒回合里「毒够重」（毒 ≥ 血）**0 次**，
    /// 解毒草 215 株买进、13 株吃下，而其中判据说「这一回合该吃」的是 0 次。
    /// 一个永远咬不动人的毒，配一个解它的道具，两边都是摆设。
    ///
    /// 现在的两条边都是**比例**：每回合掉血条的 10%，最多 18%。
    /// 于是第 1 层 60 血的毒是 5 点、第 15 层 137 血是 11 点——同一件事的同一个份量。
    /// 它仍然不会「随层数指数增长」：涨的是玩家自己的血，毒跟着走。
    /// </summary>
    public const double PoisonShareOfMaxHp = 0.10;
    public const double PoisonCapOfMaxHp = 0.18;

    /// <summary>
    /// 毒这一回合的读数（**血条口径**）：每回合 8% 最大生命，夹在 1..12 之间。
    /// 血条越大、毒越重，但永远不超过血条的 12%——「毒会要命，但不会一直要命」。
    /// </summary>
    public static int PoisonTickFor(Combatant c, int maxHp)
    {
        if (!IsPoisoned(c) || c.PoisonTurns <= 0) return 0;
        var cap = Math.Clamp((int)Math.Round(Math.Max(1, maxHp) * PoisonCapOfMaxHp), 1, MaxPoisonPower);
        return Math.Clamp(Math.Max(1, c.PoisonPower), 1, cap);
    }

    /// <summary>
    /// 挂毒时用多大的数值：按目标血条的 <see cref="PoisonShareOfMaxHp"/>，
    /// 夹在 1..<see cref="MaxPoisonPower"/>。上界仍然只有一个定义——挂毒的三条路
    /// （毒系技能 / 瘴气 / 熔岩灼烧）全部问这里。
    /// </summary>
    public static int PoisonPowerFor(int targetMaxHp, double ratio = PoisonShareOfMaxHp) =>
        Math.Clamp((int)Math.Round(Math.Max(1, targetMaxHp) * ratio), 1, MaxPoisonPower);

    public static void ApplyPoison(Combatant c, int turns, int power)
    {
        c.PoisonTurns = Math.Max(c.PoisonTurns, turns);
        c.PoisonPower = Math.Max(c.PoisonPower, power);
    }

    /// <summary>中毒这一回合的毒伤（不扣血）：规则只此一处，结算与预告都读它</summary>
    public static int PoisonTickDamage(Combatant c) =>
        IsPoisoned(c) && c.PoisonTurns > 0 ? Math.Max(1, c.PoisonPower) : 0;

    public static bool ClearPoison(Combatant c)
    {
        var had = c.PoisonTurns > 0;
        c.PoisonTurns = 0;
        c.PoisonPower = 0;
        return had;
    }

    public static bool IsPoisoned(Combatant c) => c.PoisonTurns > 0;
}
