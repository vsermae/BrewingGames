// 数据层类型：对应 game_data.json 的结构。
//
// 与 TS 版一一对应，只做两件事：把「字段可选」写成 C# 的 T?（值类型用 T?，
// 引用类型就用可空引用），并且保留所有注释里的「为什么」。
// 数据仍然只有一份 game_data.json，两边同源——改数值不该在两个引擎里各改一遍。
using System.Text.Json.Serialization;

namespace AbyssEcho.Data;

public enum SkillType
{
    Damage,
    Heal,
    Buff,
    Poison,
    Drain,
    DebuffDefense,
    DebuffAttack,
    Evade,
    Stun,
    Dispel,
}

public enum Slot
{
    Weapon,
    Armor,
    Trinket,
    Consumable,
}

/// <summary>装备品质：白 / 蓝 / 紫 / 橙。品质只决定「带几条词条」，数值强弱仍由道具自身决定。</summary>
public enum Quality
{
    White,
    Blue,
    Purple,
    Orange,
}

public sealed class SkillDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Desc { get; set; } = "";
    public SkillType Type { get; set; }
    public double Power { get; set; }
    public int Cooldown { get; set; }
    public string Icon { get; set; } = "";
    /// <summary>中毒持续回合数：缺省 3（PoisonTurns）。毒爆写 2，日志与描述才对得上。</summary>
    public int? PoisonTurns { get; set; }
    /// <summary>
    /// 毒系技能会不会自己就打出一次直接伤害：缺省 true。
    /// 毒雾写 false——它的 power（0.5）是「每回合受攻击力 50%」的持续伤害，
    /// 而不是「造成 0.5 倍攻击力伤害」。以前两者共用一条「攻击力 × power」，
    /// 于是毒雾每次都在日志里「造成 3 点伤害」，而描述里根本没这一句。
    /// </summary>
    public bool? DirectDamage { get; set; }
}

public sealed class ItemDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public Slot Slot { get; set; }
    public string Rarity { get; set; } = "common";
    public int Price { get; set; }
    /// <summary>
    /// 卖掉能拿回多少。缺省按 Inventory.SellValue 从 Price 推（见 Systems/Inventory.cs）。
    ///
    /// 为什么要有这个数：背包 6 格、装备槽只有 3 个（武器/护甲/饰品），
    /// 而「换一件更好的装备」必然剩下一件旧的——它占着格子、又不该留下来，
    /// 唯一的出口只能是把一件**有用的**东西丢给「放不下」的那条路。那不是取舍，是税。
    /// </summary>
    public int? Sell { get; set; }
    public int? Attack { get; set; }
    public int? Defense { get; set; }
    public int? MaxHp { get; set; }
    public int? Heal { get; set; }
    public int? Damage { get; set; }
    /// <summary>解除中毒：解毒草这类「用了不为造成伤害」的消耗品</summary>
    public bool? Cleanse { get; set; }
    /// <summary>装备品质，决定词条数量；缺省按 Rarity 推导</summary>
    public Quality? Quality { get; set; }
    public List<string>? Affixes { get; set; }
    /// <summary>套装名：橙色品质预留字段，暂不产生套装效果</summary>
    public string? Set { get; set; }
    public string Desc { get; set; } = "";

    public ItemDef Clone() => new()
    {
        Id = Id, Name = Name, Slot = Slot, Rarity = Rarity, Price = Price, Sell = Sell,
        Attack = Attack, Defense = Defense, MaxHp = MaxHp, Heal = Heal, Damage = Damage,
        Cleanse = Cleanse, Quality = Quality,
        Affixes = Affixes is null ? null : new List<string>(Affixes),
        Set = Set, Desc = Desc,
    };
}

/// <summary>词条：装备的第二层构筑维度。一段词条只做一件事。</summary>
public sealed class AffixDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Desc { get; set; } = "";
    public bool? Negative { get; set; }
    public double? AttackPct { get; set; }
    public double? CritChance { get; set; }
    public int? PoisonTurns { get; set; }
    public double? DrainHealPct { get; set; }
    public int? FrostSealHeal { get; set; }
    public BurnDef? CritBurn { get; set; }
    public double? KillHealPct { get; set; }
    public bool? SurviveLethal { get; set; }
    public double? RiskAttackPct { get; set; }
    public double? RiskIncomingPct { get; set; }
    public int? TurnStartHpLoss { get; set; }
    public double? HealTakenPct { get; set; }
    public double? MaxHpPct { get; set; }
    public double? DamageTakenPct { get; set; }
    public double? SpellFailChance { get; set; }
}

public sealed class BurnDef
{
    public int Turns { get; set; }
    public int Power { get; set; }
}

public sealed class MonsterDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Hp { get; set; }
    public int Attack { get; set; }
    public int Defense { get; set; }
    public int Gold { get; set; }
    public int Xp { get; set; }
    public List<string> Skills { get; set; } = new();
    public string Desc { get; set; } = "";
    public bool? IsBoss { get; set; }
    public int? MinFloor { get; set; }
    /// <summary>倒下时自爆：对玩家造成其攻击力 × ratio 的伤害</summary>
    public double? ExplodeOnDeath { get; set; }
    public LowHpDrainDef? LowHpDrain { get; set; }
    public int? SpellReflect { get; set; }
    public GuardAuraDef? GuardAura { get; set; }
    public bool? FrostSealOnHit { get; set; }
}

/// <summary>
/// 防御类增益（石肤）：按「自身基础防御」的比例折算成防御点数，持续 N 回合。
/// 比例而不是绝对值：石头傀儡在第 5 层和第 15 层不该给出同一个固定点数。
/// </summary>
public sealed class ArmorBuffDef
{
    public string Id { get; set; } = "";
    public int Turns { get; set; } = 2;
    public double Power { get; set; }
}

public sealed class LowHpDrainDef
{
    public double Ratio { get; set; }
    public double Multiplier { get; set; }
}

public sealed class GuardAuraDef
{
    public int Every { get; set; }
    public int Defense { get; set; }
}

public sealed class EncounterSlotDef
{
    public string Id { get; set; } = "";
    public List<string> Monsters { get; set; } = new();
    public string? Desc { get; set; }
}

public enum EventEffectType
{
    None,
    Gold,
    Hp,
    HealPct,
    RandomItem,
    SwapItem,
    Curse,
    Cleanse,
}

public sealed class EventEffect
{
    public EventEffectType Type { get; set; }
    /// <summary>数值：gold/hp 是整数，healPct 是比例（0.4 = 40%）——所以是 double</summary>
    public double? Value { get; set; }
    /// <summary>Curse 专用：按面板上限扣的比例</summary>
    public double? HpPct { get; set; }
    /// <summary>Curse 专用：换来的攻击力</summary>
    public int? Attack { get; set; }
}

public sealed class EventOutcome
{
    public int Weight { get; set; }
    public string Text { get; set; } = "";
    public EventEffect Effect { get; set; } = new();
}

public sealed class EventChoice
{
    public string Label { get; set; } = "";
    public int? Cost { get; set; }
    public bool? RequiresItem { get; set; }
    public List<EventOutcome> Outcomes { get; set; } = new();
}

public sealed class EventDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Text { get; set; } = "";
    public List<EventChoice> Choices { get; set; } = new();
}

public sealed class LevelingDef
{
    public int BaseXp { get; set; }
    public double Growth { get; set; }
    public int HpPerLevel { get; set; }
    public int AttackPerLevel { get; set; }
    public int DefenseEveryLevels { get; set; }
    public int DefensePerLevel { get; set; }
    public List<string> Pool { get; set; } = new();
    public string? Desc { get; set; }
}

public sealed class BossStageDef
{
    public int Floor { get; set; }
    public string Id { get; set; } = "";
    public int? GoldMultiplier { get; set; }
    public int? XpMultiplier { get; set; }
    public string? Mechanic { get; set; }
}

public class FloorMechanicDef
{
    public string Name { get; set; } = "";
    public string Desc { get; set; } = "";
}

public sealed class BossMechanicDef : FloorMechanicDef
{
    public int? Every { get; set; }
    public double? Multiplier { get; set; }
    public int? Reflect { get; set; }
    public int? Cooldown { get; set; }
}

public sealed class EnemyAiConfig
{
    public double Attack { get; set; }
    public double Defend { get; set; }
    public double Skill { get; set; }
}

public sealed class SacrificeDef
{
    /// <summary>
    /// 说明里写的那个比例（「-8% 生命上限」）。**它不是结算用的数**——
    /// 结算是按 `HpCostCap` 算的固定代价，见 `Game.TakeSacrifice`。
    /// 留着它是给校验与老存档里的文案比对用。
    /// </summary>
    public double HpPct { get; set; }

    /// <summary>换来的攻击力。固定的，不随层数或已有攻击漂移</summary>
    public int Attack { get; set; }

    /// <summary>
    /// 一次血祭**最多**扣掉多少生命上限（绝对值）。
    /// 以前扣的是「当前上限的 8%」：角色越练越强，代价就跟着一起涨，
    /// 而换来的还是 6 点攻击——第 9 级身上 90 上限时，一刀要付 7 点上限换 6 点攻击，
    /// 这笔账本身就是亏的，玩家照做只会越换越弱（见 DEVELOPER_NOTES 这一轮）。
    /// </summary>
    public int HpCostMin { get; set; } = 6;

    /// <summary>扣费上限还占「基础生命上限」的多少：保证后期不至于一刀砍掉半条命</summary>
    public double HpCostMaxPct { get; set; } = 0.04;
}

public sealed class AltarsConfig
{
    public int Cleanse { get; set; }
    public int Curse { get; set; }
    /// <summary>泉眼在深层祭坛里的权重。它只加在「已经解锁泉眼」的那些层上</summary>
    public int OfferingWeight { get; set; } = 6;
    public SacrificeDef Sacrifice { get; set; } = new();

    /// <summary>泉眼：金币换成长。深层才有，见 <see cref="OfferingDef.MinFloor"/> 的说明</summary>
    public OfferingDef Offering { get; set; } = new();
}

/// <summary>
/// 泉眼祭坛：把金币换成永久的生命上限与攻击力。
///
/// 为什么要有它：装备在 12 层左右就到顶（三个槽各一件，每件的价与强度都有头），
/// 而进项是每层 300~400。装备到顶之后，「钱」这个资源在本作里就没有下文了——
/// 横扫 30 局里第 10~12 层每层花完还剩 1200 左右，结算时中位 380、最多 1868。
/// 这不是玩家不会花，是货架上再没有「值得买的东西」：一件更好的装备并不存在。
///
/// 它和一局到顶的装备不冲突：这里买到的是**面板**（BaseMaxHp / BaseAttack），
/// 装备加的是另一层。所以钱在深层仍然有下文，而它买不到「跳过一层的强度」——
/// 每级 +6 上限、+1 攻击，一局最多升十几级，泉眼最多再给 5 级的分量。
/// </summary>
public sealed class OfferingDef
{
    /// <summary>第一次的价钱</summary>
    public int BaseCost { get; set; } = 60;

    /// <summary>往后每一次贵多少：一起涨价，避免「攒够一次就白住」</summary>
    public int CostPerStep { get; set; } = 45;

    /// <summary>
    /// 每深一层，第一笔贵多少。
    ///
    /// 这一条是量出来的：旧版价钱是一条固定的 60/105/150/195/240，而第 6 层起
    /// 进项就有 300~400 一层。横扫 30 局里献过金的 24 局、其中 18 局把五次全用了——
    /// 钱对它是够的，于是「泉眼」变成了一条和升级平行的免费曲线：
    /// 谁都用、谁都不需要为它做决定。那不像一个「出口」，像一个按钮。
    ///
    /// 加上这一项之后，第一笔在第 6 层是 180（半层进项）、第 10 层 260、
    /// 第 15 层 360（约一层进项）。它仍然是出口，但每一次都要问一句：
    /// 这笔钱买面板，还是留着买那把剑？
    /// </summary>
    public int CostPerFloor { get; set; } = 20;

    /// <summary>一次给多少生命上限（与升级的 hpPerLevel 同一量级）</summary>
    public int HpPerStep { get; set; } = 6;

    /// <summary>一次给多少攻击力</summary>
    public int AttackPerStep { get; set; } = 1;

    /// <summary>一局最多献几次：不封顶的话，深层的钱会直接把面板顶穿</summary>
    public int MaxSteps { get; set; } = 5;

    /// <summary>
    /// 从第几层开始出现。写死在浅层会让「钱」变成一种越早攒越划算的资源，
    /// 而这条机制的初衷是**给深层一个出口**，不是让前期变得更简单。
    /// </summary>
    public int MinFloor { get; set; } = 6;
}

public sealed class TrainingConfig
{
    public double HealPct { get; set; }
    public double AttackPct { get; set; }
    public int AffixCount { get; set; }
}

public sealed class CurseRoomConfig
{
    public List<string> AffixIds { get; set; } = new();
}

public sealed class EliteConfig
{
    public double ScaleBase { get; set; }
    public double ScalePerFloor { get; set; }
    public Quality MinQuality { get; set; }
    public double AffixChance { get; set; }
}

public sealed class RoomsConfig
{
    public TrainingConfig Training { get; set; } = new();
    public CurseRoomConfig Curse { get; set; } = new();
    public EliteConfig Elite { get; set; } = new();
}

public sealed class LoopCurseDef
{
    public string Name { get; set; } = "";
    public string Desc { get; set; } = "";
}

public sealed class MetaConfig
{
    public Dictionary<string, int> BossShards { get; set; } = new();
    public int ClearShards { get; set; }
    public int DeathShards { get; set; }
    public double LoopEnemyScale { get; set; }
    public double LoopShopScale { get; set; }
    public List<LoopCurseDef> LoopCurses { get; set; } = new();
}

public sealed class GameConfig
{
    public int MapWidth { get; set; }
    public int MapHeight { get; set; }
    public int MinRooms { get; set; }
    public int InventorySize { get; set; }
    public int StartingHp { get; set; }
    public int StartingAttack { get; set; }
    public int StartingDefense { get; set; }
    public int StartingGold { get; set; }
    public double CritChance { get; set; }
    public double CritMultiplier { get; set; }
    public double DefendReduction { get; set; }
    public double DamageVariance { get; set; }
    public EnemyAiConfig EnemyAi { get; set; } = new();
    public double EnemyGrowth { get; set; } = 0.05;
    public int FinalFloor { get; set; }
    public AltarsConfig Altars { get; set; } = new();
    public MetaConfig Meta { get; set; } = new();
    public RoomsConfig Rooms { get; set; } = new();
}

public sealed class QualityDef
{
    public string Name { get; set; } = "";
    public int AffixCount { get; set; }
    public string Color { get; set; } = "";
}

public sealed class RoomTypeDef
{
    public int Weight { get; set; }
    public string Name { get; set; } = "";
    public string Symbol { get; set; } = "";
}

public sealed class GameData
{
    public List<SkillDef> Skills { get; set; } = new();
    public List<ItemDef> Items { get; set; } = new();
    public List<MonsterDef> Monsters { get; set; } = new();
    public List<MonsterDef> Bosses { get; set; } = new();
    public List<BossStageDef> BossStages { get; set; } = new();
    public Dictionary<string, FloorMechanicDef> FloorMechanics { get; set; } = new();
    public Dictionary<string, string> FloorMechanicMap { get; set; } = new();
    public Dictionary<string, BossMechanicDef> BossMechanics { get; set; } = new();
    public List<EventDef> Events { get; set; } = new();
    public Dictionary<string, List<EncounterSlotDef>> EncounterSlots { get; set; } = new();
    public Dictionary<string, AffixDef> Affixes { get; set; } = new();
    public Dictionary<Quality, QualityDef> Qualities { get; set; } = new();
    public Dictionary<string, RoomTypeDef> RoomTypes { get; set; } = new();
    public GameConfig Config { get; set; } = new();
    public LevelingDef Leveling { get; set; } = new();
}

/// <summary>
/// JSON 序列化配置：枚举按字符串读（数据里写的是 "damage" 而不是 0）。
/// 只在一处定义，加载与校验都走它。
/// </summary>
public static class JsonSetup
{
    public static readonly System.Text.Json.JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // 数据里的枚举是 snake_case（debuff_defense / randomItem 这类），
        // .NET 8 的 JsonStringEnumConverter 只认 CamelCase，所以自带一个命名策略。
        Converters = { new JsonStringEnumConverter(SnakeCaseNamingPolicy.Instance) },
    };
}

/// <summary>
/// 枚举 ↔ snake_case。.NET 8 的 JsonStringEnumConverter 只支持 CamelCase，
/// 而 game_data.json 里写的是 debuff_defense —— 数据优先，转换器迁就它。
/// </summary>
public sealed class SnakeCaseNamingPolicy : System.Text.Json.JsonNamingPolicy
{
    public static readonly SnakeCaseNamingPolicy Instance = new();

    public override string ConvertName(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
