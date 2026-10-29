// 楼层机制：每层一条「这一层该怎么打」的规则。
//
// 设计原则只有一条：机制必须能改变打法，并且玩家能看见。
// 「每层敌人血量 +20%」这种只是数值放大，放进 CreateMonster 的楼层缩放就够了。
// 这里收的全是逼玩家换思路的东西：中毒（要解毒）、破防（换输出）、闪避（赌命中）、
// 自爆（不能拖）、镜像（要算反弹）。
//
// 数据在 game_data.json 的 floorMechanics / floorMechanicMap / bossMechanics 三段，
// 本文件只负责把 id 映射到效果与文案；数值一律从数据读，不在这里写死。
namespace AbyssEcho.Systems;

using AbyssEcho.Core;
using AbyssEcho.Data;

/// <summary>
/// 「这一下打中了才收钱」的机制句柄。反射层的反震是它唯一的使用者。
///
/// 为什么需要它：机制链（ResolveFloorHit）跑在玩家那一刀的**伤害结算之前**，
/// 而反震的前提是「打中了」。以前把反震的伤害直接写在返回值里，
/// 于是同一回合既可能「你的攻击落空了」，又照样挨 4 点反震。
/// 现在机制只负责「登记一笔待收的反震」，由 Battle 在伤害真的落地之后调 PayReflect。
/// </summary>
public interface IHitContext
{
    /// <summary>登记一笔反震：命中之后才扣。返回一句要写进日志的话</summary>
    MechanicEffect? ArmReflect(int damage, string log);
}

/// <summary>
/// 一条机制结算结果。用 record 而不是普通 class：Battle 需要在拿到结果后
/// 补一个「只有战斗上下文才知道」的数值（瘴气的毒伤取自这一刀的实际伤害），
/// with 表达式让这种局部改写不必把整个效果重抄一遍。
/// </summary>
public sealed record MechanicEffect
{
    public string? Log { get; init; }
    public int? Damage { get; init; }
    public int? PoisonTurns { get; init; }
    public int? PoisonPower { get; init; }
    public bool? Stun { get; init; }
    /// <summary>
    /// 这一下「倒下时炸开」的伤害（没有就是 null）。
    /// 它和 <see cref="Damage"/> 说的是同一笔钱，只是发生在**这只敌人倒下的那一刻**——
    /// 预告侧要能提前把它算出来（`Battle.DeathBlastForecast`），而结算侧照样走 Damage，
    /// 两边读同一个算式，不各写一遍。
    /// </summary>
    public int? DeathBlast { get; init; }
}

public sealed class FloorMechanic
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Desc { get; init; } = "";
    /// <summary>战斗开始时把本层规则作用到敌人身上（加攻击、加防御、免疫中毒等）</summary>
    public Action<Combatant>? Setup { get; init; }
    /// <summary>敌人回合结束后的收尾：自爆、回血这类</summary>
    public Func<Combatant, Combatant, Rng, MechanicEffect?>? EnemySettled { get; init; }
    /// <summary>
    /// 玩家命中前：闪避判定与反震。
    /// 第一个参数是战场本身（Battle.HitContext）：反震这类「知道打中了才收钱」的效果
    /// 需要一个能挂到伤害之后的句柄。写成 interface 而不是 Battle，是因为 FloorMechanics
    /// 要被自检与模拟器直接调用，不该反过来依赖战斗状态机。
    /// </summary>
    public Func<IHitContext, Combatant, Rng, MechanicEffect?>? BeforeHit { get; init; }
    /// <summary>玩家治疗量的修正（幽暗层减半）</summary>
    public Func<Combatant, double>? HealFactorFn { get; init; }
    /// <summary>敌人命中玩家之后的判定（瘴气层挂毒）：与 BeforeHit 方向相反</summary>
    public Func<Combatant, Rng, MechanicEffect?>? AfterHit { get; init; }
}

/// <summary>
/// Boss 机制：三只 Boss 各一套，按 every 回合周期触发。
/// 不写 Every 的那一套是「常驻规则」而不是「周期爆发」——深渊之主的领域从头到尾都张着，
/// 所以它没有 on/off 可切，也没有「当回合」可言（见 abyss_domain 那一项）。
/// </summary>
public sealed class BossMechanic
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Desc { get; init; } = "";
    public int? Every { get; init; }
    public double? Multiplier { get; init; }
    public int? Reflect { get; init; }
    public int? Cooldown { get; init; }
    /// <summary>周期到点时把标记挂到 Boss 身上，供本回合读取</summary>
    public Func<Combatant, bool>? Arm { get; init; }
    /// <summary>本回合是否已展开（Boss 身上的标记）</summary>
    public Func<Combatant, bool> IsArmed { get; init; } = _ => false;
    /// <summary>
    /// 常驻规则：没有周期，从第一回合起一直生效。
    /// 判据是「机制表里没写 Every」，而不是「IsArmed 没写」——后者默认就是 false，
    /// 用它来判等于把「没写」和「写了 false」混成同一件事。
    /// </summary>
    public bool AlwaysOn => Every is null;
    /// <summary>取走标记：用完就收，别让同一个效果连吃两回合</summary>
    public Func<Combatant, bool> Consume { get; init; } = _ => false;
}

public static class FloorMechanics
{
    /// <summary>疫病层的攻击补偿</summary>
    public const int BlightedAttackBonus = 4;
    /// <summary>灼烧层的破防比例</summary>
    public const double SearingPierce = 0.3;
    /// <summary>狭窄层的暴击补偿</summary>
    public const double CrampedCritBonus = 0.15;
    /// <summary>幽暗层的治疗系数</summary>
    public const double GloomHealFactor = 0.5;
    /// <summary>反射层的反震伤害与概率</summary>
    public const int ReflectingDamage = 4;
    public const double ReflectingChance = 0.25;
    /// <summary>
    /// 回合回复的上限：**不超过目标最大生命的这个比例**。
    ///
    /// 它以前只写在注释里（「回血封顶见 HealCapRatio」），代码里根本没有这个常量——
    /// 于是一条被写成「回血封顶」的说明，四十轮里没有人真的实现过。实测：
    /// 第 8 层巫妖每回合回 8%(81)≈6 点，而一个刚够第 8 层面板的玩家（攻击 14、
    /// 敌人防御 8）每刀只打 4~5 点——**回血吃掉了整份输出**，200 回合打不完一场。
    /// 封顶之后回血仍然有意义（小怪照样回得动），但它不再是「无敌」。
    /// </summary>
    public const double HealCapRatio = 0.05;

    /// <summary>瘴气层的中毒概率：说明里的「20%」在这里，改文案就得改这里</summary>
    public const double HazePoisonChance = 0.2;
    /// <summary>瘴气层的中毒参数：说明写「20% 概率中毒 2 回合」，数值与 desc 同源</summary>
    public const int HazePoisonTurns = 2;
    /// <summary>瘴气的毒伤比例：按挨打时的实际伤害算，不按玩家攻击力。
    /// 早先它取的是玩家面板攻击的一半（8 点攻击 → 每回合 4 点），
    /// 玩家的攻击越高，这一层的毒就越毒——「环境」变成了「构筑惩罚」。</summary>
    public const double HazePoisonRatio = 0.5;
    /// <summary>瘴气的毒伤下限：一刀挨得再轻，毒也得有个分量。
    /// 死因表里「中毒」不是致死原因时，解毒草就没有场——见 <see cref="HazePoisonPower"/>。</summary>
    public const int HazePoisonFloor = 4;
    /// <summary>灼烧的毒伤比例与下限：按玩家面板攻击的一半算。</summary>
    public const double BurnPoisonRatio = 0.4;
    public const int BurnPoisonFloor = 4;
    /// <summary>
    /// 这一层中毒最多毒死玩家几次：`1 + floor(层数 / 层距)`，封在 `EntityRules.MaxPoisonDeathsPerFloor`。
    ///
    /// 上限靠 `EntityRules.PoisonLethalAllowed` 里的预算实现，不是靠调小毒伤。
    /// 原因是穷举过的：毒伤压到 4 点，30 局里能要命的回合掉到 1/30，
    /// 解毒草跟着变成「零使用」——**毒不能太毒，但也不能没有存在感**。
    ///
    /// 这里以前是 `floor(层数 × 5%)`：第 15 层算出 0，被 `Math.Max(1, …)` 兜成 1。
    /// 于是 1~15 层**全是 1**，那条公式从没起过作用——而更糟的是它让规则说了假话：
    /// 毒第一次不杀人、第二次也永远不会（见 `Game.PoisonDyingBudget`）。
    /// 现在「每 5 层多一次」，第 1~5 层 1 次、第 6~10 层 2 次、第 11~15 层 3 次，合计封在 4。
    /// </summary>
    public const int PoisonDyingFloorStep = 5;

    public static int PoisonDyingBudgetFor(int floor)
    {
        // 除法在前面单列一次：`1 + (floor - 1) / Step` 在这套工具链上算出的是 1——
        // 这条算式能打成什么，不该由「我读代码时以为的优先级」决定（实测见 DEVELOPMENT_NOTES）。
        var steps = Math.Max(0, floor - 1) / PoisonDyingFloorStep;
        var budget = 1 + steps;
        return Math.Min(EntityRules.MaxPoisonDeathsPerFloor, budget);
    }
    /// <summary>自爆伤害比例</summary>
    public const double SpitefulExplosion = 0.6;

    /// <summary>怨恨层的 id：预告侧（`PreviewFatalBurst`）问「这一层会不会炸」时读它</summary>
    public const string SpitefulId = "spiteful";

    /// <summary>
    /// 这一层的敌人倒下时会不会炸。**集合只有一个**：结算侧的 `EnemySettled`
    /// 与预告侧的 `PreviewFatalBurst` 都读它，所以「会炸」与「炸多少」
    /// 不会一边有一边没有。
    ///
    /// 现在有两层：怨恨（第 11 / 22 层）与魂腐（第 25 层，三 Boss 连战那一层）。
    /// 魂腐以前只有「回血 + 免疫毒」，拖得动但拖不出代价——那一层是整局的最后一战，
    /// 敌人会自爆这件事，本该是它最像最后一战的地方。
    /// </summary>
    private static readonly HashSet<string> DeathBurstFloors = new() { SpitefulId, "soul_rot" };

    public static bool BurstsOnDeath(string id) => DeathBurstFloors.Contains(id);
    /// <summary>闪避率：躁动 / 幽暗</summary>
    public const double RestlessEvade = 0.2;
    public const double GloomEvade = 0.3;
    /// <summary>敌人每回合回血比例：回响 / 魂腐 / 凝固</summary>
    public const double EchoingHeal = 0.08;
    public const double SoulRotHeal = 0.06;
    public const double CongealingHeal = 0.05;
    /// <summary>敌人属性加成：狭窄 / 尖刺 / 凝固</summary>
    public const int CrampedDefense = 4;
    public const double ThornyAttack = 1.25;
    public const int CongealingDefense = 2;
    public const double CongealingAttack = 1.1;

    /// <summary>
    /// 楼层机制表。每个机制都回答一个问题：这一层要玩家「多想一层」什么。
    /// 1 层 none 是教学层，别一上来就上强度。
    /// </summary>
    public static readonly Dictionary<string, FloorMechanic> Table = new()
    {
        ["none"] = new FloorMechanic { Id = "none", Name = "常规", Desc = "这一层没有特殊规则。" },

        // 瘴气：把已经教过的中毒变成环境压力，逼玩家带解毒草。
        // 挂毒放在 AfterHit（敌人打中你之后）而不是 BeforeHit：
        // 说明写的是「敌人的攻击有 20% 概率使你中毒」，方向是敌人 → 玩家。
        ["haze"] = new FloorMechanic
        {
            Id = "haze",
            Name = "瘴气",
            Desc = "毒雾贴着地面爬：本层敌人的攻击有 20% 概率使你中毒 2 回合。",
            // 毒伤按**玩家血条**折算（`Battle` 在命中后把数值填进来）：毒是环境的账。
            // 每次命中 20% 概率挂毒，持续 2 回合。
            AfterHit = (_, rng) => rng.Chance(HazePoisonChance)
                ? new MechanicEffect { PoisonTurns = HazePoisonTurns, Log = "瘴气顺着伤口钻进你的身体，你中毒了！" }
                : null,
        },

        // 狭窄：给玩家补偿，让「换思路」有甜头而不是纯吃亏
        ["cramped"] = new FloorMechanic
        {
            Id = "cramped",
            Name = "狭窄",
            Desc = "岩壁挤占了战场：本层敌人防御 +4，作为补偿你的暴击率 +15%。",
            Setup = enemy => enemy.BaseDefense += CrampedDefense,
        },

        // 尖刺：纯输出压力，逼玩家把防御当正经资源用
        ["thorny"] = new FloorMechanic
        {
            Id = "thorny",
            Name = "尖刺",
            Desc = "满地碎岩与倒刺：本层敌人攻击 +25%。",
            Setup = enemy => enemy.BaseAttack = (int)Math.Round(enemy.BaseAttack * ThornyAttack),
        },

        // 疫病：毒系在这里吃瘪，但给 +4 攻击当补偿，避免「这条路白走了」
        ["blighted"] = new FloorMechanic
        {
            Id = "blighted",
            Name = "疫病",
            Desc = "腐殖质糊住了敌人的甲胄：本层敌人免疫中毒，但你的攻击力 +4。",
            Setup = enemy => enemy.ImmunePoison = true,
        },

        // 躁动：命中变赌，逼玩家用高倍率技能而不是普攻磨
        ["restless"] = new FloorMechanic
        {
            Id = "restless",
            Name = "躁动",
            Desc = "这些敌人不肯正面挨打：本层敌人闪避率 20%。",
            Setup = enemy =>
            {
                // 常驻闪避记在 BaseEvade：它是这一层的空气，不该被技能的那一份带过界。
                enemy.BaseEvade = Math.Max(enemy.BaseEvade, RestlessEvade);
                enemy.Evade = Math.Max(enemy.Evade, enemy.BaseEvade);
            },
        },

        // 回响：不能拖，拖下去它越打越满
        ["echoing"] = new FloorMechanic
        {
            Id = "echoing",
            Name = "回响",
            Desc = "深处的低语在愈合它们的伤口：本层敌人每回合回复 8% 最大生命。",
            // 「愈合伤口」= 回血，不是「回血 + 全身无敌」。
            //
            // 这一层和加护祭司（每 3 回合给同伴 +3 防御，无上限累加）撞在一起时，
            // 敌人每小时回的血比玩家每回合能造成的伤害还多：实测玩家攻击 14、
            // 敌人防御 8（加护后 11），每刀只打 3~4 点，而敌人一回合回 5~6 点。
            // 战斗永远打不完——40 局里整局卡在第 8 层，一次都没往下走。
            // 修法有两处：加护只算一层（Battle.TickGuardAura），回血封顶见 HealCapRatio。
            EnemySettled = (enemy, _, _) =>
            {
                // 倒下的敌人不回血：这一句是给「毒死/打死后的收尾」兜底的
                if (enemy.Hp <= 0) return null;
                var raw = (int)Math.Round(enemy.BaseMaxHp * EchoingHeal);
                // 封顶：一回合回的血不超过最大生命的 `HealCapRatio`，
                // 而且必须**明显低于**一个刚好够面板的玩家每回合能打出的伤害。
                var amount = Math.Min(raw, Math.Max(1, (int)Math.Round(enemy.BaseMaxHp * HealCapRatio)));
                // 持续伤害回不上来（`DoesNotHealDot`）。
                //
                // 「回合回复」和「持续伤害」撞在一起时，两边的顺序决定谁赢：这一层先回血、
                // 再结算毒（Battle.EndRound 就是这个顺序），于是毒那一笔被回血吃掉了——
                // 一只被毒缠住的巫妖可以 200 回合不掉血，战斗永远打不完。
                // 规则本身也没说「回血能抵掉中毒」，它说的是「回复 8% 最大生命」；
                // 而毒是每秒都在流的账，不该靠「回得比掉得快」抹平。
                var dot = EntityRules.PoisonTickDamage(enemy);
                if (dot > 0)
                {
                    return new MechanicEffect
                    {
                        Log = $"{enemy.Name}在低语中试图愈合，但毒素把这一口气按了回去",
                    };
                }
                var healed = EntityRules.Heal(enemy, amount, enemy.BaseMaxHp);
                return healed > 0 ? new MechanicEffect { Log = $"{enemy.Name}在低语中愈合了 {healed} 点伤口" } : null;
            },
        },

        // 灼烧：防御不再是万能解，逼玩家拼输出。破防在 Battle 里按 id 判
        ["searing"] = new FloorMechanic
        {
            Id = "searing",
            Name = "灼烧",
            Desc = "空气烫得刺喉：本层敌人攻击无视你 30% 的防御。",
            Setup = _ => { },
        },

        // 怨恨：收割时也要小心，换血流打不过
        [SpitefulId] = new FloorMechanic
        {
            Id = SpitefulId,
            Name = "怨恨",
            Desc = "敌人带着怨恨而来：它们倒下时会自爆，对你造成其攻击力 60% 的伤害。",
            EnemySettled = (enemy, _, _) =>
            {
                if (enemy.Hp > 0) return null;
                var damage = SpitefulBlast(enemy.BaseAttack);
                return new MechanicEffect
                {
                    Damage = damage,
                    DeathBlast = damage,
                    Log = $"{enemy.Name}倒下的瞬间炸开一团怨气，对你造成 {damage} 点伤害！",
                };
            },
        },

        // 反射：普攻越多越疼，逼玩家改用技能
        //
        // 反震带一句 Note、不带 Damage：伤害由 Battle.HurtPlayer 在**命中之后**扣。
        // 以前它把 Damage 写在这个返回值里，而这条链跑在「这一刀有没有打中」之前——
        // 于是同一回合里可能出现「你的攻击落空了」+「你损失 4 点生命」。
        // 反震是「武器被弹回来」，没打中就没有被弹回来的东西。
        ["reflecting"] = new FloorMechanic
        {
            Id = "reflecting",
            Name = "反射",
            Desc = "这里的骨头泛着金属光：你攻击时有 25% 概率被反震，损失 4 点生命。",
            BeforeHit = (battle, _, rng) => rng.Chance(ReflectingChance)
                ? battle.ArmReflect(ReflectingDamage, "你的武器被反震回来，损失 4 点生命")
                : null,
        },

        // 幽暗：闪避 + 治疗减半，双压逼玩家带药水而不是只靠治愈
        ["gloom"] = new FloorMechanic
        {
            Id = "gloom",
            Name = "幽暗",
            Desc = "光源照不到这里：本层敌人闪避率 30%，你的治疗效果减半。",
            Setup = enemy =>
            {
                enemy.BaseEvade = Math.Max(enemy.BaseEvade, GloomEvade);
                enemy.Evade = Math.Max(enemy.Evade, enemy.BaseEvade);
            },
            HealFactorFn = _ => GloomHealFactor,
        },

        // 渊压：深潜层的纯数值压力。第 15 层之后没有新机制可教，
        // 用一层「什么都涨一点」把周目递增的压力显式摆出来。
        ["abyss_pressure"] = new FloorMechanic
        {
            Id = "abyss_pressure",
            Name = "渊压",
            Desc = "深处的重量压在骨头上：本层敌人攻击 +15%、防御 +5。",
            Setup = enemy =>
            {
                enemy.BaseAttack = (int)Math.Round(enemy.BaseAttack * 1.15);
                enemy.BaseDefense += 5;
            },
        },

        // 魂腐：免疫中毒 + 每回合回复。毒系在这里彻底吃瘪。
        ["soul_rot"] = new FloorMechanic
        {
            Id = "soul_rot",
            Name = "魂腐",
            Desc = "倒下的敌人仍在低语：本层敌人每回合回复 6% 最大生命，并免疫中毒；它们倒下时会自爆（攻击力 60%）。",
            Setup = enemy => enemy.ImmunePoison = true,
            // 自爆这一笔是从怨恨层（`spiteful`）借过来的：两层用的是同一条纯规则算式
            // （`SpitefulBlast`），所以预告侧 `PreviewFatalBurst` 只要认机制 id
            // 就能把「它会炸」提前写在敌人卡上。魂腐层在深潜段才出现，
            // 而那一层以前是「只有回血 + 免疫中毒」——它能拖，但拖不出代价。
            EnemySettled = (enemy, _, _) =>
            {
                if (enemy.Hp <= 0)
                {
                    var boom = SpitefulBlast(enemy.BaseAttack);
                    return new MechanicEffect
                    {
                        Damage = boom,
                        DeathBlast = boom,
                        Log = $"{enemy.Name}腐烂的躯壳炸开，对你造成 {boom} 点伤害！",
                    };
                }
                var healed = EntityRules.Heal(enemy, (int)Math.Round(enemy.BaseMaxHp * SoulRotHeal), enemy.BaseMaxHp);
                return healed > 0 ? new MechanicEffect { Log = $"{enemy.Name}在低语中愈合了 {healed} 点伤口" } : null;
            },
        },

        // 凝固：Boss 前一层，三种压力叠着来，当成终局演练。
        // 描述里的三个数字（+2 / +10% / 5%）与下面的常量一一对应。
        ["congealing"] = new FloorMechanic
        {
            Id = "congealing",
            Name = "凝固",
            Desc = "脚下的地面正在凝固：本层敌人防御 +2、攻击 +10%，且每回合回复 5% 最大生命。",
            Setup = enemy =>
            {
                enemy.BaseDefense += CongealingDefense;
                enemy.BaseAttack = (int)Math.Round(enemy.BaseAttack * CongealingAttack);
            },
            EnemySettled = (enemy, _, _) =>
            {
                if (enemy.Hp <= 0) return null;
                var healed = EntityRules.Heal(enemy, (int)Math.Round(enemy.BaseMaxHp * CongealingHeal), enemy.BaseMaxHp);
                return healed > 0 ? new MechanicEffect { Log = $"{enemy.Name}在凝固的地面上重新凝固，回复了 {healed} 点生命" } : null;
            },
        },
    };

    /// <summary>
    /// 三 Boss 连战不是「一个机制」，而是「一场战斗打三只」的元数据。
    /// 它仍然要能在 MechanicInfo 里查到名字与说明。
    /// </summary>
    private static readonly BossMechanic GauntletMeta = new()
    {
        Id = "boss_gauntlet",
        Name = "三 Boss 连战",
        Desc = "依次对战熔岩领主、霜蚀女王、深渊之主，中途没有补给。",
        // 连战本身不按回合触发任何东西：每一只 Boss 用的是它自己的机制
        // （molten_burst / frost_mirror / abyss_domain），由 Game.BossMechanicIdOf
        // 换成当前那只。这里留着 multiplier 只是为了让「连战」这条元数据
        // 在 BossMechanicInfo 里仍能报出一个说明用的倍率；Every 不再写，
        // 否则会有人误以为连战自己会在第 3 回合做点什么。
        Multiplier = 1.2,
    };

    public static readonly Dictionary<string, BossMechanic> BossTable = new()
    {
        ["boss_gauntlet"] = GauntletMeta,
        ["molten_burst"] = new BossMechanic
        {
            Id = "molten_burst",
            Name = "熔岩爆裂",
            Desc = "每 3 回合爆开一层岩浆壳：当回合攻击 +40%，并灼烧你 2 回合。",
            Every = 3,
            Multiplier = 1.4,
            // 展开只置标记并写一句日志；攻击加成在敌人的下一次行动里读，
            // 灼烧交给战斗收尾时统一挂，避免在 arm 里直接改玩家状态。
            Arm = b => { b.MoltenBurst = true; return true; },
            IsArmed = b => b.MoltenBurst == true,
            Consume = b => { var on = b.MoltenBurst == true; b.MoltenBurst = false; return on; },
        },
        ["frost_mirror"] = new BossMechanic
        {
            Id = "frost_mirror",
            Name = "霜之镜像",
            Desc = "每 2 回合展开一面冰镜，把你下一次造成的伤害固定反弹 20 点。",
            Every = 2,
            Reflect = 20,
            Arm = b => { b.FrostMirror = true; return true; },
            IsArmed = b => b.FrostMirror == true,
            Consume = b => { var on = b.FrostMirror == true; b.FrostMirror = false; return on; },
        },
        // 深渊领域：常驻规则，不是「每 2 回合」。
        //
        // 早先它写着 every: 2，于是 Arm / IsArmed / Consume 一整套周期标记，
        // 而 Effect 侧（冷却惩罚 / 驱散）只认 IsArmed。标记只在 Arm 之后到 EndRound 的 Consume
        // 之间是 true——也就是敌人出手那一刻——而冷却惩罚挂在「玩家施法」上，两者的时机永远错开：
        // 结果是：技能冷却 +1 与「驱散你的强化」**一次都没生效过**，攻击 +30% 只在
        // 「张开的那个回合」成立——说明写的是常驻的领域，实际只有一半的时间在。
        //
        // 「每回合冷却 +1」如果真按周期结算，玩家的技能会直接被锁死；所以冷却这一条
        // 本身就只能是常驻的（它不读 Every，见 Battle.Ctx 的 ExtraCooldown）。
        //
        // 说明里还有两件事，以前一件都没兑现：
        //  · 「当回合攻击 +30%」——Multiplier 写在表里，可那时 Arena 把 Every 去掉写成
        //    AlwaysOn，BossMultiplier 对常驻规则恒返回 1，于是这个倍率再也没生效；
        //  · 「驱散你的强化」——副作用挂在「玩家施法」上，而玩家手上没有任何技能能给
        //    自己加强化，那段代码一次都没走到。
        // 现在倍率与驱散都回到「张手的那个回合」这一条线上（Every=2），
        // 冷却惩罚仍然常驻，说明也按真实时机重写。
        ["abyss_domain"] = new BossMechanic
        {
            Id = "abyss_domain",
            Name = "深渊领域",
            Desc = "每 2 回合领域张手：当回合攻击 +30%、驱散你的强化；范围内你的技能冷却 +1 回合。",
            Every = 2,
            Multiplier = 1.3,
            Cooldown = 1,
            // 周期型机制必须有一整套标记，否则那条线整段是死的：
            // 这一条当初从常驻改回「每 2 回合」时只补了 Every，Arm/IsArmed/Consume
            // 都还是默认的空实现——Battle.StartRound 里 `Boss.Arm?.Invoke(...) == true`
            // 永远是 false，于是「攻击 +30%」「驱散强化」连日志都不打，一次没生效过。
            Arm = b => { b.AbyssDomain = true; return true; },
            IsArmed = b => b.AbyssDomain == true,
            Consume = b => { var on = b.AbyssDomain == true; b.AbyssDomain = false; return on; },
        },
    };

    /// <summary>
    /// 机制展示信息（进本提示、战斗界面标题用）。
    /// Boss 层的机制 id 只存在于 bossMechanics 里，所以两表都要查——
    /// 否则第 5/10/15 层会静默显示成「没有机制」，正是要消灭的情况。
    /// </summary>
    public static FloorMechanicDef MechanicInfo(string id)
    {
        if (Table.TryGetValue(id, out var m)) return new FloorMechanicDef { Name = m.Name, Desc = m.Desc };
        if (BossTable.TryGetValue(id, out var b)) return new FloorMechanicDef { Name = b.Name, Desc = b.Desc };
        return new FloorMechanicDef { Name = "", Desc = "" };
    }

    /// <summary>战斗开始时把楼层机制作用到敌人身上</summary>
    public static void ApplyFloorMechanic(string id, Combatant enemy)
    {
        if (Table.TryGetValue(id, out var m)) m.Setup?.Invoke(enemy);
    }

    /// <summary>
    /// 这个 id 是不是一个**楼层**机制（有 Setup / EnemySettled 的那种）。
    ///
    /// 判它是因为 `bossStages` 上那个 mechanic 字段装的是两种东西：
    /// 「这一层持续生效的规则」（molten_burst / frost_mirror / abyss_domain）
    /// 和「这一场战斗的元数据」（boss_gauntlet）。后者在清单里查得到名字，
    /// 但它没有 Setup、没有 EnemySettled——把它当成楼层机制用，整层的敌人
    /// 就什么机制都没有，而且一句提示也不会错。
    /// </summary>
    public static bool IsFloorMechanic(string id) => Table.ContainsKey(id);

    /// <summary>这个 id 是不是一个**有实现**的 Boss 机制（熔岩爆裂 / 冰镜 / 深渊领域…）</summary>
    public static bool IsBossMechanic(string id) => BossTable.ContainsKey(id);

    /// <summary>
    /// 这个 id 拿它当「一层持续生效的机制」用，会不会真的落地？
    ///
    /// `bossStages` 上的 mechanic 字段装两种东西：楼层规则，和「这一场打几只」的元数据。
    /// `BossTable` 里两种都查得到名字，但元数据那一条（boss_gauntlet）没有周期、
    /// 没有 Arm——它不会在任何一个回合做任何事。
    ///
    /// 判据刻意**不看 Multiplier**：`boss_gauntlet` 上留着一个倍率，那只是为了让
    /// 「连战」这条元数据在 BossMechanicInfo 里报得出一个说明用数字，没有调用方读它。
    /// 拿它当「有实现」的证据，等于把「写着 1.2、实际没人用」当成功能。
    /// 「会做事」的判据只有两条：有周期（Every），或者有展开动作（Arm）。
    /// </summary>
    public static bool BossMechanicHasEffect(string id) =>
        BossTable.TryGetValue(id, out var m) && (m.Every is not null || m.Arm is not null);

    /// <summary>
    /// 玩家命中敌人之后的机制判定：反震（反射层）依旧在 BeforeHit，
    /// 敌人打中玩家之后的挂毒（瘴气层）走这里——两者方向相反。
    /// </summary>
    public static MechanicEffect? ResolveEnemyHit(string id, Combatant enemy, Rng rng) =>
        Table.TryGetValue(id, out var m) ? m.AfterHit?.Invoke(enemy, rng) : null;

    /// <summary>命中前的机制判定：闪避与反震由 Battle 调，返回 null 表示正常命中</summary>
    public static MechanicEffect? ResolveFloorHit(string id, Combatant enemy, Rng rng, IHitContext? battle = null)
    {
        if (!Table.TryGetValue(id, out var mech)) return null;
        // 闪避是所有机制共用的：敌人身上挂着 evade 就判一次
        if (enemy.Evade > 0 && rng.Chance(enemy.Evade))
        {
            return new MechanicEffect { Log = $"{enemy.Name}身形一晃，你的攻击落空了" };
        }
        // 没有战场句柄时不判「需要命中之后才收钱」的那类（纯规则测试走这条路）
        if (battle is null) return null;
        return mech.BeforeHit?.Invoke(battle, enemy, rng);
    }

    /// <summary>敌人回合结束后的机制收尾</summary>
    public static MechanicEffect? SettleFloorMechanic(string id, Combatant enemy, Combatant player, Rng rng) =>
        Table.TryGetValue(id, out var mech) ? mech.EnemySettled?.Invoke(enemy, player, rng) : null;

    /// <summary>
    /// 这一层「敌人倒下时会炸多少」的**纯规则版**：没有随机、没有战斗上下文。
    /// 预告（`Battle.DeathBlastForecast`）与结算（`ResolveFloorHit`）都从这里取，
    /// 自检也读它——三处各写一遍的话，迟早有一处会跟着调参走掉。
    /// </summary>
    public static int SpitefulBlast(int attack) => Math.Max(1, (int)Math.Round(attack * SpitefulExplosion));

    /// <summary>
    /// 只问「这一层有没有倒下自爆」：有，就返回那句写在手册里的说明。
    /// 界面不该硬编码机制 id——它读的是同一张表。
    /// </summary>
    public static FloorMechanic? Mechanic(string id) => Table.TryGetValue(id, out var mech) ? mech : null;

    /// <summary>
    /// 「这一只倒下时会炸我多少」——只回答**楼层机制**那一半（怨恨层的自爆）。
    ///
    /// 为什么不直接用 `SettleFloorMechanic`：那支会真的掷骰、真的回血、真的写日志，
    /// 而战斗屏每一帧都在问这一句。算式仍然只有一处（<see cref="SpitefulBlast"/>）：
    /// 结算侧的 `EnemySettled` 与这里读的是同一个函数，自检里钉了「两边同数」。
    ///
    /// 判据只看这一层有没有「倒下即伤害」这条规则，而且**只认会炸的那一层**：
    /// 熔岩爆裂 / 冰镜是 Boss 的周期爆发（打的是活人的那一刀），不是倒下自爆。
    /// 回血那几层的 `EnemySettled` 也一样——它们不造成伤害。
    /// </summary>
    public static int PreviewFatalBurst(string id, Combatant enemy, Combatant player)
    {
        if (!Table.TryGetValue(id, out var mech) || mech.EnemySettled is null) return 0;
        return BurstsOnDeath(id) ? SpitefulBlast(enemy.BaseAttack) : 0;
    }

    /// <summary>
    /// 瘴气层挂给玩家的毒伤：按这一刀**实际打出了多少**折算，下限 <see cref="HazePoisonFloor"/>，
    /// 上界 <see cref="EntityRules.MaxPoisonPower"/>。
    ///
    /// 这里是唯一算式：Battle 挂毒时读它，自检也读它。
    /// 两头各有一条理由：没有下限，「毒 + 下一刀」会要命的回合在 30 局里只有 2/30，
    /// 解毒草一次都用不上；没有上界，这一刀随层数长，第 14 层的瘴气一挂就是 49 点/回合
    /// ——毒会变成全游戏唯一一条指数成长的伤害。
    /// </summary>
    public static int HazePoisonPower(int dealt) => HazePoisonPower(dealt, 0);

    /// <summary>
    /// 瘴气挂毒：**按玩家血条**折算（`EntityRules.PoisonPowerFor`），再保一个下限。
    ///
    /// 上一版按「这一刀实际打出多少」折算——于是一个血厚的号挨得越疼、毒就越重，
    /// 两个面板不同的玩家身上挂着同一个数字。毒是环境的账，不是那一刀的账。
    /// `dealt` 仍然收着：它只用来判「这一刀有没有打中过」，不参与分量。
    /// </summary>
    public static int HazePoisonPower(int dealt, int playerMaxHp) =>
        playerMaxHp <= 0
            ? Math.Clamp((int)Math.Round(dealt * HazePoisonRatio), HazePoisonFloor, EntityRules.MaxPoisonPower)
            : Math.Max(HazePoisonFloor, EntityRules.PoisonPowerFor(playerMaxHp));

    /// <summary>
    /// 毒伤的唯一算式在 `EntityRules.PoisonPowerFor`（按**目标血条**折算，夹 1..MaxPoisonPower）。
    /// 这里只多一条「下限」：一刀挨得再轻，毒也得有个分量，否则解毒草连出现的必要都没有。
    /// </summary>
    public static int PoisonWithFloor(int targetMaxHp, int floor) =>
        Math.Max(floor, EntityRules.PoisonPowerFor(targetMaxHp));

    /// <summary>灼烧挂给玩家的毒伤：按面板攻击折算，下限 <see cref="BurnPoisonFloor"/>、上界同上</summary>
    public static int BurnPoisonPower(int attack) => BurnPoisonPower(attack, 0);

    /// <summary>灼烧挂毒：和瘴气读同一个入口（按玩家血条），`attack` 只保留旧签名</summary>
    public static int BurnPoisonPower(int attack, int playerMaxHp) =>
        playerMaxHp <= 0
            ? Math.Clamp((int)Math.Round(attack * BurnPoisonRatio), BurnPoisonFloor, EntityRules.MaxPoisonPower)
            : Math.Max(BurnPoisonFloor, EntityRules.PoisonPowerFor(playerMaxHp));

    /// <summary>治疗量修正（幽暗层减半）</summary>
    public static double HealFactor(string id, Combatant player) =>
        Table.TryGetValue(id, out var m) ? m.HealFactorFn?.Invoke(player) ?? 1 : 1;

    /// <summary>
    /// 楼层给玩家的补偿：狭窄 +15% 暴击、疫病 +4 攻击。
    /// 补偿不走 Setup()（那只收敌人），由 Game 在这里按层算出来，
    /// Battle 拿它当「本层额外加成」，和训练房的 +10% 同一个位置。
    /// </summary>
    public static (int Attack, double Crit) PlayerMechanicBonus(string id) => id switch
    {
        "cramped" => (0, CrampedCritBonus),
        "blighted" => (BlightedAttackBonus, 0),
        _ => (0, 0),
    };

    /// <summary>
    /// 「加护」类增益（数据里 type=buff 且落在防御侧的那几个）。
    /// 怪物手册、战斗结算、状态图标三处读同一份，免得再出现
    /// 「手册写减伤 25%、结算却给攻击 +25%」这种事。
    ///
    /// Power 的基数是**自身基础防御**，不是最大生命：以前用它乘最大生命，
    /// 78 血的石头傀儡一次石肤拿到 +20 点防御（它自己的防御才 10），
    /// 玩家的每一刀都只剩最低伤害 1 点——而描述写的是「受到的伤害降低 25%」。
    /// 比例而不是绝对值仍然成立：同一只傀儡在第 15 层会跟着成长一起变厚。
    /// </summary>
    public static readonly Dictionary<string, ArmorBuffDef> ArmorBuff = new()
    {
        ["stone_skin"] = new ArmorBuffDef { Id = "stone_skin", Turns = 2, Power = 0.25 },
    };

    /// <summary>中毒免疫：疫病层。ApplyPoison 前先问它</summary>
    public static bool PoisonImmune(Combatant enemy) => enemy.ImmunePoison == true;

    /// <summary>
    /// 清掉一个单位的正面修正（强化倍率、降攻/破防这类临时状态）。
    /// 返回是否真的清掉了东西：腐蚀新星与深渊领域都要靠它决定日志写不写。
    /// 注意不清中毒：中毒是负面状态，「驱散正面」不该顺手把敌人的毒也解了。
    /// </summary>
    public static bool DispelBuffs(Combatant c)
    {
        var had = c.AttackMultiplier > 1 || c.AtkModTurns > 0 || c.DefModTurns > 0 || c.Evade > c.BaseEvade;
        c.AttackMultiplier = 1;
        // 只驱散技能给的那份闪避：楼层机制给的常驻闪避是「这一层的空气」
        c.Evade = c.BaseEvade;
        if (c.AtkModTurns > 0)
        {
            c.AtkMod = 0;
            c.AtkModTurns = 0;
        }
        if (c.DefModTurns > 0)
        {
            c.DefMod = 0;
            c.DefModTurns = 0;
        }
        return had;
    }

    public static BossMechanic? BossMechanicOf(string? id) =>
        id is not null && BossTable.TryGetValue(id, out var m) ? m : null;

    /// <summary>
    /// 这一层会不会给玩家挂毒 —— 也就是「中毒」这条链在本层的两个来源：
    ///   · 楼层机制：瘴气（`haze`，敌人攻击 20% 概率挂毒）；
    ///   · Boss 机制：熔岩爆裂（`molten_burst`，第 5 层那只 Boss 每 3 回合灼烧你 2 回合）。
    ///
    /// 为什么要有它：中毒致死预算与解毒草的场都只在这一层真的会挂毒时才有意义。
    /// 报表上那句「中毒回合 N 次」的分母里混进了所有层——第 13~15 层那 49 次
    /// 从来没人分开算过，而它是「玩家自己投的毒」与「环境挂的毒」的两回事。
    /// 上界只有一个：这个集合。加一种新毒源时改这里，报表与判据一起跟上。
    /// </summary>
    private static readonly HashSet<string> PoisonFloors = new() { "haze" };

    /// <summary>Boss 机制里会挂毒的那几种（熔岩爆裂的灼烧复用中毒那条链）</summary>
    private static readonly HashSet<string> PoisonBossMechanics = new() { "molten_burst" };

    /// <param name="id">楼层机制 id（`FloorMechanicMap` 那一列）</param>
    /// <param name="bossMechanic">Boss 机制 id（连战时传当前那一只的）</param>
    public static bool FloorPoisons(string? id, string? bossMechanic = null) =>
        (id is not null && PoisonFloors.Contains(id))
        || (bossMechanic is not null && PoisonBossMechanics.Contains(bossMechanic));

    /// <summary>Boss 机制的展示信息：战斗界面用它渲染标题与说明</summary>
    public static BossMechanicDef BossMechanicInfo(string? id)
    {
        if (id is not null && BossTable.TryGetValue(id, out var m))
        {
            return new BossMechanicDef
            {
                Name = m.Name,
                Desc = m.Desc,
                Every = m.Every,
                Multiplier = m.Multiplier,
                Reflect = m.Reflect,
                Cooldown = m.Cooldown,
            };
        }
        return new BossMechanicDef { Name = "", Desc = "" };
    }
}
