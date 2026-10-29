// 全局游戏状态：楼层、地图、玩家与房间内容。战斗过程由 Battle / BattleScene 负责。
//
// 和 TS 版一样，这里是「一局」的唯一真相来源：场景只读它、调它的方法，
// 不自己存一份可变状态——否则存档与 UI 迟早各说各话。
namespace AbyssEcho.Core;

using AbyssEcho.Data;
using AbyssEcho.Systems;

public enum AltarKind
{
    Cleanse,
    Curse,
    /// <summary>泉眼：金币换永久面板（深层才有），见 <see cref="OfferingDef"/></summary>
    Offering,
}

public sealed class AltarDef
{
    public AltarKind Kind { get; init; }
    public string Name { get; init; } = "";
    public string Text { get; init; } = "";
    /// <summary>净化的收益文案 / 诅咒的代价</summary>
    public string Detail { get; init; } = "";
}

public sealed class LoopCurse
{
    public string Name { get; init; } = "";
    public string Desc { get; init; } = "";
    /// <summary>敌人暴击率加成（暴戾）</summary>
    public double CritBonus { get; init; }
    /// <summary>敌人防御加成（坚韧）：比例，不是点数——说明写的是「+15%」</summary>
    public double DefenseBonus { get; init; }
    /// <summary>敌人攻击倍率（嗜血）</summary>
    public double AttackPct { get; init; } = 1;
}

public sealed class EncounterDef
{
    public required Combatant Monster { get; init; }
    public required MonsterDef Def { get; init; }
    public List<SkillDef> Skills { get; init; } = new();
    /// <summary>组合里的其余敌人实例（含各自的怪物定义），单人战斗为空</summary>
    public List<Combatant> Allies { get; init; } = new();
    public List<MonsterDef> AllyDefs { get; init; } = new();
    /// <summary>敌人实例 → 怪物定义：功能性敌人的效果来源</summary>
    public Dictionary<Combatant, MonsterDef> EnemyDefs { get; init; } = new();
    public EnemyTier Tier { get; init; }
    /// <summary>三 Boss 连战的进度；非连战为 null</summary>
    public (int Index, int Total)? Gauntlet { get; init; }
    public int GoldMultiplier { get; init; } = 1;
    public int XpMultiplier { get; init; } = 1;
    public FloorMechanicDef FloorMechanic { get; init; } = new();
    /// <summary>
    /// 这一场战斗**实际生效**的楼层机制 id。
    ///
    /// 以前战斗侧直接读 `_run.FloorMechanicId`：遭遇是在进房那一刻建的，
    /// 而战斗场景开起来可能更晚——中间这一段时间里楼层机制一旦变了
    /// （摆现场借一层、或将来别的东西改了它），界面与结算就会和敌人的属性对不上。
    /// 遭遇自己记一份，谁建的场谁负责，读的人不必去猜「现在」是哪一层。
    /// </summary>
    public string FloorMechanicId { get; init; } = "none";
    public BossMechanicDef? BossMechanic { get; init; }
    /// <summary>本周目负面效果给敌人的暴击率加成（暴戾 +10%）；第 1 周目为 0</summary>
    public double EnemyCritBonus { get; init; }
}

public enum GameStatus
{
    Playing,
    Dead,
    Won,
}

public sealed class Game
{
    /// <summary>
    /// 周目负面效果：数据里只写 name/desc，数值与效果的对应关系在这里。
    /// 为什么不把数值也放进 game_data：desc 是写给玩家的文案，
    /// 「暴戾」到底加多少暴击率属于结算细节，和机制常量放一处更好找。
    /// </summary>
    private static readonly Dictionary<string, (double CritBonus, double DefenseBonus, double AttackPct)> LoopCurseEffects = new()
    {
        ["暴戾"] = (0.1, 0, 1),
        ["坚韧"] = (0, 0.15, 1),
        ["嗜血"] = (0, 0, 1.1),
        ["贪婪"] = (0, 0, 1),
    };

    /// <summary>附魔符文能落到的最好品质：词条容量上限看品质表，取最高档</summary>
    private static readonly Quality QualityMax = Quality.Orange;

    public static readonly Dictionary<AltarKind, AltarDef> Altars = new()
    {
        [AltarKind.Cleanse] = new AltarDef
        {
            Kind = AltarKind.Cleanse,
            Name = "净化祭坛",
            Text = "一汪清澈的水泊浮在石台上，水面映出的你比现在健康。",
            Detail = "解除中毒",
        },
        [AltarKind.Curse] = new AltarDef
        {
            Kind = AltarKind.Curse,
            Name = "血祭祭坛",
            Text = "石台上的凹槽还温着，像是刚有人在这里付过代价。",
            // 具体数字由 Game.Sacrifice 从数据读，面板在打开时补上
            Detail = "以生命上限换取攻击力",
        },
        [AltarKind.Offering] = new AltarDef
        {
            Kind = AltarKind.Offering,
            Name = "泉眼",
            Text = "石缝里渗出的水是温的，落进池底的金币声很轻。",
            // 具体价钱由 Game.OfferingCost() 给，面板在打开时补上
            Detail = "投入金币，换取生命上限与攻击力",
        },
    };

    private readonly GameData _data;
    public Rng Rng { get; }

    /// <summary>
    /// 这一局的地图种子 = 构造时传进来的那个数。
    ///
    /// 它和 `Rng.Seed` 不是一回事：Rng 在构造期间就要为「元进展开局加成」抽签，
    /// 等第一层地图生成时，`Rng.Seed` 依然是它，但**随机序列已经往前走了**。
    /// 地图本身由 `MapRngFor(floor)` 从「这个种子 + 楼层」单独派生，所以只要拿对这个数，
    /// 任何时刻都能把同一层的图一模一样地重建出来——存档要的正是不动主序列的重建。
    /// 早先存档写的是 `Rng.Seed`（当时的实现里那个值确实等于种子），
    /// 后来开局抽签加进来，两个数就分叉了，而存档那一行没跟着改：
    /// 读档后第 1 层整张图换了一张脸，随机序列也退回开局。
    /// </summary>
    public uint MapSeed => Rng.Seed;
    public int Floor { get; private set; } = 1;
    public GridMap Map { get; private set; }
    public int PlayerIndex { get; private set; }
    public Player Player { get; }
    public int Kills { get; private set; }
    public int GoldEarned { get; private set; }
    public GameStatus Status { get; private set; } = GameStatus.Playing;
    /// <summary>已触发过的随机事件，避免同一局反复出现</summary>
    public HashSet<string> UsedEvents { get; } = new();
    /// <summary>
    /// 本局生效的元进展快照：开局时定一次。
    ///
    /// 它**不是**「只读副本」——这局里挣到的碎片、拿到的成就都写回这里，
    /// 再由 MetaSaver 落盘。曾经的实现是 `Meta = MetaProgression.AddShards(...)`：
    /// 每加一次碎片就换成一个新的模拟对象，而 SaveMeta 拿的是这个新对象，
    /// 主菜单那边 `store.Meta` 还指着开局那一份。
    ///
    /// 于是一条链上只要有一个「不信这个字段」的读者，元进展就会在下一局
    /// 或者读档时回到旧值。现在改成原地改字段：读的人永远读得到最新的那份。
    /// </summary>
    public MetaState Meta { get; private set; }
    /// <summary>周目数：通关后继续深潜 +1。第 1 周目是原版难度</summary>
    public int Loop { get; private set; } = 1;
    public int ShardsEarned { get; private set; }
    /// <summary>训练房选过的攻击加成：本层所有攻击 +N%，过层即失效</summary>
    public double TrainingAttackPct { get; private set; }
    /// <summary>本局一共打了多少场：结算与存档的计数，不允许读档回退</summary>
    public int BattlesFought { get; private set; }
    /// <summary>
    /// 连战（第 25 层三 Boss）打掉一只之后：这一场既没结束也没清房，
    /// 是「关窗就会丢进度」的时刻，需要立刻落盘。
    /// </summary>
    public bool GauntletMidway => _gauntletQueue.Count > 0 && _gauntletIndex > 0
        && _gauntletIndex < _gauntletQueue.Count;
    /// <summary>
    /// 训练房选过的附魔：本层下一件入手的装备会多一条随机词条。
    /// 一层里可能有两间训练房，所以内部按队列记——只留一个的话，
    /// 第二次铭刻会静默覆盖第一次，玩家看到日志说「已铭刻」但装备上只有一条。
    /// </summary>
    public string? PendingBonusAffix
    {
        get => _pendingBonusAffixes.Count > 0 ? _pendingBonusAffixes[^1] : null;
        private set
        {
            _pendingBonusAffixes.Clear();
            if (value is not null) _pendingBonusAffixes.Add(value);
        }
    }

    /// <summary>本层待生效的符文（可能不止一条）</summary>
    private List<string> _pendingBonusAffixes = new();

    /// <summary>存档与界面用：本层全部待生效的符文，按先后顺序</summary>
    public IReadOnlyList<string> PendingBonusAffixes => _pendingBonusAffixes;

    private readonly HashSet<int> _beatenBossFloors = new();
    private List<MonsterDef> _gauntletQueue = new();
    private int _gauntletIndex;
    private readonly List<LevelUpResult> _pendingLevelUps = new();
    public Dictionary<string, ItemDef> ItemIndex { get; } = new();
    private readonly Dictionary<int, List<ShopStockItem>> _shopCache = new();
    /// <summary>运行时生成的装备（诅咒房紫装、训练房附魔装）计数：给它们发唯一 id 用</summary>
    private int _runtimeItemSeq;
    /// <summary>写存档的钩子：碎片与成就变化时由 GameStore 落盘</summary>
    public Action<MetaState>? MetaSaver { get; set; }

    public Game(uint? seed = null, MetaState? meta = null)
    {
        _data = GameDataStore.Get();
        Rng = new Rng(seed);
        Meta = meta?.Clone() ?? new MetaState();
        foreach (var item in _data.Items) ItemIndex[item.Id] = item;

        // 元进展开局加成：只加「选择」，不加「数值」。
        var gold = _data.Config.StartingGold;
        if (MetaProgression.HasUnlock(Meta, MetaUnlock.ExtraGold)) gold += 20;
        // 解锁额外技能时，从技能池里挑一个非火球的攻击技给玩家——
        // 仍然是「换一种开局打法」，不是「变得更强」。
        var baseSkillIds = new[] { "fireball", "heal", "empower" };
        var startingSkills = _data.Skills.Where(s => baseSkillIds.Contains(s.Id)).ToList();
        if (MetaProgression.HasUnlock(Meta, MetaUnlock.ExtraSkill))
        {
            var extra = _data.Skills.FirstOrDefault(s => s.Id != "fireball" && s.Type == SkillType.Damage);
            if (extra is not null) startingSkills.Add(extra);
        }
        Player = EntityRules.CreatePlayer(
            _data.Config.StartingHp,
            _data.Config.StartingAttack,
            _data.Config.StartingDefense,
            gold,
            startingSkills);
        // 行装预备：开局直接给一件白装。白装无词条，
        // 它的价值是「第一层就有装备可换」，而不是「第一层就变强」。
        if (MetaProgression.HasUnlock(Meta, MetaUnlock.StarterGear))
        {
            // 不抽签：挑的是「数据表里第一件白武器」。这里原本写着 Rng 抽取，
            // 而地图要到下面 GenerateFloor 才生成——于是「开没开这一档解锁」
            // 会让同一颗种子长出两张不同的图（地图种子没变，但主序列已经往前走了一步）。
            // 落点应该是「开局多一件东西」，不是「换一张图」。
            var white = _data.Items.FirstOrDefault(i =>
                i.Slot == Slot.Weapon && Inventory.QualityOf(i) == Quality.White && (i.Affixes?.Count ?? 0) == 0);
            if (white is not null) Player.Inventory.Add(white.Id);
        }

        Player.PoisonDyingLeft = PoisonDyingBudget;
        Map = GenerateFloor(1);
        PlayerIndex = Map.StartIndex;
        Map.Rooms[PlayerIndex].Visited = true;
    }

    public Room CurrentRoom => Map.Rooms[PlayerIndex];

    /// <summary>
    /// 截图与调试用：把玩家瞬移到某个房间。正常流程不要用它——
    /// 走动要走 Move（那条路径里有连通性校验与房间触发）。
    /// </summary>
    public void TeleportForDebug(int index)
    {
        if (index < 0 || index >= Map.Rooms.Count) return;
        PlayerIndex = index;
        Map.Rooms[index].Visited = true;
    }

    /// <summary>当前面板属性：基础 + 装备固定值，再乘百分比词条（先加后乘）</summary>
    public PlayerStats Stats
    {
        get
        {
            var affixes = Affixes;
            var baseStats = EntityRules.PlayerStatsOf(Player, ItemIndex, affixes.AttackPct);
            // 脆弱躯壳的 +20% 生命乘在「基础 + 装备固定值」之和上，和攻击侧的「先加后乘」一致
            baseStats.MaxHp = AffixMath.MaxHpWithAffixes(baseStats.MaxHp, affixes);
            return baseStats;
        }
    }

    /// <summary>当前装备的词条视图（每次读取都重算：装备会随时变化）</summary>
    public ActiveAffixes Affixes => Inventory.EquippedAffixes(Player, ItemIndex);

    public ItemDef? RollDrop(EnemyTier tier) =>
        Loot.PickItemOfQuality(Rng, ItemIndex.Values.ToList(), tier, MetaProgression.HasUnlock(Meta, MetaUnlock.DualAffix));

    public int Capacity => _data.Config.InventorySize;

    public LevelingDef LevelingDef => _data.Leveling;

    /// <summary>距离下一级还差多少经验，HUD 的经验条用</summary>
    public (int Current, int Needed, double Ratio) Xp => Systems.Leveling.XpProgress(Player, LevelingDef);

    /// <summary>
    /// 本局生效的房间权重：特殊房间要看元进展解锁。
    /// 模拟器与自检要用同一份判据——各抄一份的话，「没解锁的训练房权重为 0」
    /// 这种事就会在某一侧悄悄失效，测出来的分布和玩家看到的不是同一个游戏。
    /// </summary>
    public Dictionary<string, int> RoomWeights()
    {
        var special = MetaProgression.HasUnlock(Meta, MetaUnlock.SpecialRooms);
        var outMap = new Dictionary<string, int>();
        foreach (var (k, v) in _data.RoomTypes)
        {
            // 训练房与诅咒房属于第二档「深处异象」：没解锁就不进随机池。
            // 精英房不在这一档里——它是本层的房间类型，第一局就该见得到。
            var gated = k is "training" or "curse";
            outMap[k] = gated && !special ? 0 : v.Weight;
        }
        return outMap;
    }

    /// <summary>
    /// 每一层的地图随机源：种子 + 楼层。
    /// 地图必须只由「种子 + 楼层」决定，否则读档时重算出来的图会走样——
    /// 而且重算会推进主随机序列，读档就改写了接下来的掉落与战斗判定。
    /// 所以生成地图用一个独立的 Rng，主序列一概不碰。
    /// </summary>
    private Rng MapRngFor(int floor) =>
        new(unchecked(Rng.Seed * 2654435761u ^ (uint)Math.Max(1, floor) * 0x9e3779b9u));

    private GridMap GenerateFloor(int floor)
    {
        var map = MapGen.Generate(MapRngFor(floor), new MapGenOptions
        {
            Width = _data.Config.MapWidth,
            Height = _data.Config.MapHeight,
            MinRooms = _data.Config.MinRooms,
            RoomWeights = RoomWeights(),
            SupplyCap = MapGen.SupplyCap(floor),
            // 层号要传进去：「这一层该有几间仗」是深度的函数（见 `MapGen.BattlesWanted`）。
            Floor = floor,
        });
        // 地图生成器把「离起点最远的那一间」留给 Boss，但一层的剧本章节不由它决定。
        //
        // 这里曾经有这么一句：没有 Boss 阶梯的楼层，把那间房退化成普通敌人房。
        // 于是第 3、4、6~9、11~14 层出现了两个自相矛盾的东西：
        //   · 地图上那个最远的房间画着普通敌人，谁也没觉得不对；
        //   · 而这一层的「Boss」根本没有守卫——`EncounterDef` 仍会按 Boss 阶梯去建，
        //     建出来的是空壳，玩家走进去触发一场不该存在的仗，或者干脆什么都不发生。
        // 现在改成显式：这一层有没有 Boss 只由 BossStage 说了算，
        // 没有就**退成起点式的过路房**，而且 Starting 那一格不留 Boss 标记。
        // 「故事在第 15 层结束、第 16 层起是深潜」这件事才第一次在数据里说得清。
        if (BossStage(floor) is null && map.BossIndex >= 0 && map.BossIndex < map.Rooms.Count)
        {
            map.Rooms[map.BossIndex].Type = RoomType.Empty;
        }
        return map;
    }

    /// <summary>三 Boss 连战的出场顺序。按楼层重建而不是靠「第一次进房」的时机</summary>
    private static readonly string[] GauntletOrder = { "boss_molten", "boss_frost", "boss_abyss" };

    private void EnsureGauntlet()
    {
        if (_gauntletQueue.Count > 0) return;
        _gauntletQueue = GauntletOrder
            .Select(id => _data.Bosses.FirstOrDefault(b => b.Id == id))
            .Where(b => b is not null)
            .Select(b => b!)
            .ToList();
        _gauntletIndex = 0;
    }

    /// <summary>连战进度（第几只 / 共几只）。非连战为 null</summary>
    public (int Index, int Total)? GauntletProgress
    {
        get
        {
            if (BossStage(Floor)?.Mechanic != "boss_gauntlet") return null;
            EnsureGauntlet();
            return (_gauntletIndex, _gauntletQueue.Count);
        }
    }

    /// <summary>读档时把连战进度恢复回来，否则读档会让玩家重打已经打过的 Boss</summary>
    /// <summary>读档：把「泉眼换过几次」还原回来（手改过的档也要夹到上限以内）</summary>
    public void RestoreOfferingSteps(int steps) =>
        OfferingSteps = Math.Clamp(steps, 0, Math.Max(0, Offering.MaxSteps));

    /// <summary>
    /// 读档：把「这一层中毒已经毒死过我几次」还原回来。
    ///
    /// 不还原的后果很小，但方向是坏的：读一次档就白拿一次「毒只扣到 1 点」的额度。
    /// 和泉眼献金次数同一条规矩——凡是「一局 / 一层只能发生 N 次」的计数，
    /// 都得跟状态一起落盘，否则存档就是一台刷额度的机器。
    /// </summary>
    public void RestorePoisonDyingUsed(int used) =>
        Player.PoisonDyingLeft = Math.Clamp(PoisonDyingBudget - Math.Max(0, used), 0, PoisonDyingBudget);

    public void RestoreGauntletIndex(int index)
    {
        EnsureGauntlet();
        _gauntletIndex = Math.Clamp(index, 0, Math.Max(0, _gauntletQueue.Count - 1));
    }

    /// <summary>这一层对应的 Boss 阶梯；没有配置时该层没有 Boss 房</summary>
    public BossStageDef? BossStage(int floor) => _data.BossStages.FirstOrDefault(s => s.Floor == floor);

    /// <summary>
    /// 这一层的机制 id。
    ///
    /// Boss 层的机制由 bossStages 决定——但**三 Boss 连战那一层是例外**：
    /// 第 25 层的 stage 上写的是 `boss_gauntlet`，那是「这一场打三只」的元数据，
    /// 不是一个楼层机制；它没有 Setup、没有 EnemySettled，于是整层的敌人
    /// 什么机制都没有。而 `floorMechanicMap` 里给第 25 层写的是 `abyss_pressure`——
    /// 数据表明确表达了「这一层该是渊压」，只是被上面那一句提前截胡了。
    /// 现在只有在 stage 的机制**真的在机制表里**时才用它，否则退回楼层表。
    /// </summary>
    public string FloorMechanicId
    {
        get
        {
            // 摆现场用的覆盖（只给 `--shot` 那条路）：`null` 表示「照常按楼层算」。
            // 为什么要一个字段而不是让调用方各自改数据：楼层机制是从 `bossStages` /
            // `floorMechanicMap` 现算出来的，改数据等于改存档、也改掉后面每一层。
            // 这里只借一层、借完立刻还原，见 `Screenshot.RecreateBattleForShot`。
            if (FloorMechanicOverride is { } forced) return forced;
            var stage = BossStage(Floor);
            // Boss 层的机制由 bossStages 决定——但**三 Boss 连战那一层是例外**：
            // 第 25 层的 stage 上写的是 `boss_gauntlet`，那是「这一场打三只」的元数据，
            // 不是一个楼层机制；它没有 Setup、没有 EnemySettled，于是整层的敌人
            // 什么机制都没有。而 `floorMechanicMap` 里给第 25 层写的是 `abyss_pressure`——
            // 数据表明确表达了「这一层该是渊压」，只是被这一句提前截胡了。
            // 现在只有在 stage 的机制**真的会落地**时才用它，否则退回楼层表。
            // Boss 层的机制由 bossStages 决定——但**三 Boss 连战那一层是例外**：
            // 第 25 层的 stage 上写的是 `boss_gauntlet`，那是「这一场打三只」的元数据，
            // 不是一个楼层机制：它没有 Setup、没有 EnemySettled，也不带任何倍率。
            // 于是整层的敌人什么机制都没有，而 `floorMechanicMap` 里给第 25 层
            // 写的是 `abyss_pressure`——数据表明确表达了「这一层该是渊压」，
            // 只是被这一句提前截胡了。现在只有 stage 的机制**真的会落地**时才用它。
            if (!string.IsNullOrEmpty(stage?.Mechanic)
                && (FloorMechanics.IsFloorMechanic(stage!.Mechanic!) || FloorMechanics.BossMechanicHasEffect(stage.Mechanic!)))
                return stage.Mechanic!;
            // 深潜到 26 层以上时按 16~25 那一组循环：配置表只写到 25 层，
            // 再往下会静默退回「没有机制」，而「继续深潜」的卖点正是每周目都该有新规则可读。
            if (_data.FloorMechanicMap.TryGetValue(Floor.ToString(), out var direct)) return direct;
            // 循环要按「连续区间」算，不能按「有配置的那些行」算。
            // 配置表里 16~25 每层都有机制，而 1~5 是断的（第 1 层没有机制行）；
            // 以前这里取「所有 > finalFloor 的层号」当一张稠密表用，
            // 一旦哪天 26 层也补了配置，这张表就不再稠密，`deepFloors[idx]` 会在某个 idx 上
            // 直接越界。改成用区间的首尾去取模，缺行只是退回 none，不会抛。
            var deepFloors = _data.FloorMechanicMap.Keys.Select(int.Parse)
                .Where(f => f > FinalFloor).OrderBy(f => f).ToList();
            if (deepFloors.Count == 0) return "none";
            var first = deepFloors[0];
            var last = deepFloors[^1];
            var cycle = last - first + 1;
            var at = first + (Floor - first) % cycle;
            return _data.FloorMechanicMap.TryGetValue(at.ToString(), out var v) ? v : "none";
        }
    }

    /// <summary>
    /// 只给摆现场（`--shot`）用的楼层机制覆盖：设了就替掉按楼层算出来的那一条，
    /// 设回 null 就恢复。生产路径一行都不碰它——它存在的理由是
    /// 「第 1 层没有倒下自爆，而那张图要给倒下自爆留一个像素级的现场」。
    /// </summary>
    public string? FloorMechanicOverride { get; set; }

    /// <summary>这一层机制的展示信息：进本提示与战斗标题共用</summary>
    public FloorMechanicDef FloorMechanicInfo => FloorMechanics.MechanicInfo(FloorMechanicId);

    /// <summary>最终层：打完这一层的 Boss 即通关</summary>
    public int FinalFloor => _data.Config.FinalFloor;

    public bool IsFinalFloor => Floor == FinalFloor;

    /// <summary>
    /// 这一层是不是「Boss 层」：配置里有 Boss 舞台，或者往下总有更深的 Boss。
    /// 两处都要用它——战斗里的机制横幅、地图上的 BGM——以前各判各的，
    /// 于是第 25 层三 Boss 连战在音乐上永远是普通层（见 Bgm.CueFor 的注释）。
    /// </summary>
    public bool IsBossFloor => BossStage(Floor) is not null || Floor >= FinalFloor;

    /// <summary>
    /// 训练房的三选一。三个选项分属三种节奏：
    /// 回血救急、攻击加成提效、附魔则是「下一件装备的彩票」——按当前处境挑。
    /// </summary>
    public List<(string Id, string Label, string Detail)> TrainingOptions()
    {
        var cfg = _data.Config.Rooms.Training;
        return new List<(string, string, string)>
        {
            ("heal", "就地休整", $"回复 {(int)Math.Round(cfg.HealPct * 100)}% 最大生命"),
            ("attack", "苦练招式", $"本层所有攻击 +{(int)Math.Round(cfg.AttackPct * 100)}%"),
            ("affix", "铭刻符文", "本层下一件入手的装备额外获得一条随机词条"),
        };
    }

    /// <summary>结算训练房的选择</summary>
    public string TakeTraining(string choice)
    {
        var cfg = _data.Config.Rooms.Training;
        var maxHp = Stats.MaxHp;
        switch (choice)
        {
            case "heal":
            {
                var amount = (int)Math.Round(maxHp * cfg.HealPct);
                var before = Player.Hp;
                Player.Hp = Math.Min(maxHp, Player.Hp + amount);
                return $"回复 {Player.Hp - before} 点生命";
            }
            case "attack":
            {
                // 累加而不是覆盖：权重开着的时候一层可能有两间训练房
                // （300 局随机里出现过 29 次）。覆盖的话第二间的「苦练招式」
                // 等于什么都没选——玩家点了按钮，攻击一点没变。
                var before = TrainingAttackPct;
                TrainingAttackPct += cfg.AttackPct;
                var gained = $"{(int)Math.Round(cfg.AttackPct * 100)}%";
                return before > 0
                    ? $"本层所有攻击再 +{gained}（累计 +{(int)Math.Round(TrainingAttackPct * 100)}%）"
                    : $"本层所有攻击 +{gained}";
            }
            default:
            {
                // 同理：两次铭刻就是两条符文，而不是后一条把前一条顶掉。
                // 由 ApplyBonusAffix 一次全部挂上并清空队列。
                var affixId = PickAffix();
                _pendingBonusAffixes.Add(affixId);
                var def = _data.Affixes.TryGetValue(affixId, out var a) ? a : null;
                var count = _pendingBonusAffixes.Count > 1 ? $"（本层已累积 {_pendingBonusAffixes.Count} 条符文）" : "";
                return $"已铭刻符文「{def?.Name ?? affixId}」：本层下一件入手的装备将带上它{count}";
            }
        }
    }

    /// <summary>
    /// 抽一条词条，并且跳过这件装备已经有的那条。
    /// 「铭刻符文」与「苦练」是两条不同的路，但都可以落在一件装备上；
    /// 抽到重复的那条时，加词条这一步等于什么都没做（橙装容量只有 2，
    /// 一条重复词条就能把新符文静默挤出去）。
    /// </summary>
    private string PickAffix(IReadOnlyCollection<string>? avoid = null)
    {
        var pool = AffixPool().Where(a => avoid is null || !avoid.Contains(a)).ToList();
        if (pool.Count == 0) pool = AffixPool();
        return Rng.Pick(pool);
    }

    /// <summary>本层生效的攻击加成（战斗侧读取，0 表示没选苦练）</summary>
    public double TrainingAttackBuff => TrainingAttackPct;

    /// <summary>
    /// 本层机制给玩家的固定攻击补偿（疫病层 +4）。
    /// 与训练房的百分比加成是两回事：一个是「换了个环境多给点力」，
    /// 一个是「本层临时苦练」，叠在同一个位置上但不互相覆盖。
    /// </summary>
    public int FloorAttackBonus => FloorMechanics.PlayerMechanicBonus(FloorMechanicId).Attack;

    /// <summary>本层机制给玩家的暴击补偿（狭窄层 +15%），战斗侧直接加在暴击率上</summary>
    public double FloorCritBonus => FloorMechanics.PlayerMechanicBonus(FloorMechanicId).Crit;

    /// <summary>
    /// 这一层「中毒还能毒死玩家几次」。
    ///
    /// 它不是一个难度旋钮，是一条防止某个机制消失的规则：毒伤压到 30 局里只有 2 次会要命
    /// 时，解毒草就变成零使用的道具（买的人永远算不出「现在该吃」），
    /// 而抬回高毒伤又会让深层变成「毒死的」。所以让毒**偶尔**真的杀人。
    ///
    /// 额度**只有一个定义**，就在这里（`FloorMechanics.PoisonDyingBudgetFor`）。
    ///
    /// 上一版按比例给（`floor(层数 × 5%)`），第 15 层也算出 0，被 `Math.Max(1, …)` 兜成 1——
    /// 那条公式于是从来没起过作用，1~15 层全是同一个数。更糟的是它**让一条规则说了假话**：
    /// `EntityRules.PoisonLethalAllowed` 把「这一层已经毒死过一次」变成「这一层剩下的毒都不许杀人」，
    /// 于是毒**第一次不会杀人**、第二次也永远不会，报表里那张「毒杀 N 次」的分母是空的
    /// （300 局、5 次毒杀全被这一条挡掉了）。现在改成「每 5 层多一次」，
    /// 上界由 `EntityRules.MaxPoisonDeathsPerFloor` 定，深层的毒真的有第二次机会。
    /// </summary>
    public int PoisonDyingBudget => FloorMechanics.PoisonDyingBudgetFor(Floor);

    /// <summary>某一层「中毒还能毒死玩家几次」——给自检与探针用，规则只有一份</summary>
    public static int PoisonDyingBudgetAt(int floor) => FloorMechanics.PoisonDyingBudgetFor(floor);

    /// <summary>这一层已经用掉几次（和面板一起落盘，读档不会白拿一次「毒不死」的免死金牌）</summary>
    public int PoisonDyingUsed => Math.Max(0, PoisonDyingBudget - Player.PoisonDyingLeft);

    /// <summary>
    /// 附魔可抽的词条池：全部词条。负面词条也照抽——
    /// 「随机」才有彩票感，只给好词条会让这个选项变成稳赚不赔的必选。
    /// </summary>
    private List<string> AffixPool() => _data.Affixes.Keys.ToList();

    /// <summary>
    /// 界面上「继续深潜」也要清本层临时加成。
    /// 换层这件事有三个入口（Descend / ContinueDive / 读档），三处都得清——
    /// 训练房的苦练是按层算的，不清的话上周目最后一层的 +15% 会跟着玩家进新周目，
    /// 而地图上再也找不到来源。ContinueDive 现在自己清了，这个入口留给
    /// 「界面自己在换层之后还想再清一次」的场合（幂等，重复调用无副作用）。
    /// </summary>
    public void ResetFloorBuffsFromUi() => ResetFloorBuffs();

    /// <summary>下楼时清掉本层的临时加成：训练房收益不该跨层，否则「选一次强一层」会滚成雪球</summary>
    private void ResetFloorBuffs()
    {
        TrainingAttackPct = 0;
        _pendingBonusAffixes.Clear();
    }

    /// <summary>
    /// 把训练房的附魔作用到一件装备上，并消耗掉这次机会。
    /// 附魔后的装备是运行时生成的：登记进道具表、换个唯一 id，
    /// 否则玩家收下它之后按 id 只会查到原始模板，符文白刻。
    /// </summary>
    public ItemDef ApplyBonusAffix(ItemDef item)
    {
        // 消耗品没有词条槽：给它们加词条等于把符文扔进一瓶药水里，玩家什么也得不到。
        // 注意这里不消耗符文——否则玩家拿到一瓶药的同一刻，这一层的符文就白刻了。
        if (item.Slot == Slot.Consumable) return item;
        return AttachAffixes(item);
    }

    /// <summary>
    /// 把本层待生效的符文一次全部挂到这件装备上，并消耗掉队列。
    ///
    /// 两件事必须同时成立，缺一条玩家就会看到「说明写了、实际没有」：
    /// 1) 符文一条都不能丢。一条重复词条（模板上已经有那条）也要换成一条不同的——
    ///    以前直接跳过重复项，橙装容量只有 2，于是铭刻完的装备上还是原来那一条。
    /// 2) 品质要跟着词条条数走。以前一律抬到橙装，而橙装容量也是 2：
    ///    三间训练房 = 三条符文，第三条被静默截掉，而品质还写着「橙」。
    /// 容量真的不够（词条条数超过全表最高容量）时才截，并在 desc 里说清楚截掉了什么。
    /// </summary>
    private ItemDef AttachAffixes(ItemDef item)
    {
        if (_pendingBonusAffixes.Count == 0) return item;
        var pending = _pendingBonusAffixes.ToList();
        _pendingBonusAffixes.Clear();

        // 词条是「一条一条算」的，同名两条不叠加（ActiveAffixes 也只认一次）。
        // 所以符文抽到「这件上已经有的那条」时不能直接丢——那等于这次铭刻什么都没做，
        // 换一条别的顶上。
        var list = (item.Affixes ?? new List<string>()).ToList();
        var replaced = new List<string>();
        foreach (var id in pending)
        {
            if (list.Contains(id))
            {
                var alt = PickAffix(list);
                if (alt == id) continue;
                replaced.Add(_data.Affixes.TryGetValue(id, out var da) ? da.Name : id);
                list.Add(alt);
                continue;
            }
            list.Add(id);
        }

        // 品质跟着条数走：0 条白、1 条蓝、其余橙。抬到「刚好装得下」的那一档，
        // 而不是无脑拉满——面板上写着橙、词条却只有一条，比降档更难解释。
        //
        // 顺序不能反：先把品质定下来，再按「那个品质的容量」截。
        // 早先是先按橙装容量（2）截、再把品质设成橙——于是三条符文掉一条，
        // 玩家看到的是「铭刻了三次，装备上两条」。
        var quality = list.Count switch
        {
            0 => Quality.White,
            1 => Quality.Blue,
            _ => QualityMax,
        };
        var capacity = _data.Qualities[quality].AffixCount;
        var dropped = list.Skip(capacity).ToList();
        list = list.Take(capacity).ToList();
        var notes = new List<string>();
        if (replaced.Count > 0) notes.Add($"重复的「{string.Join("、", replaced)}」换成了别的");
        if (dropped.Count > 0) notes.Add($"装不下「{string.Join("、", dropped.Select(d => _data.Affixes.TryGetValue(d, out var x) ? x.Name : d))}」");
        var note = notes.Count > 0 ? $"（{string.Join("；", notes)}）" : "";

        var next = item.Clone();
        next.Affixes = list;
        next.Quality = quality;
        // 说明里逐条铺出词条名与效果：附魔前那半句（「锻造精良」之类的文案）
        // 与「这件到底变强还是变脆」没关系，留着只会把真正生效的那几条挤到看不见。
        // 奖励/商店/背包三处共用的就是这一段文本。
        var summary = AffixSummary(list);
        next.Desc = summary.Length > 0 ? $"{summary}{note}" : $"{item.Desc}{note}";
        return RegisterRuntimeItem(next);
    }

    /// <summary>把一串词条摊成「词条名：效果」的说明文本。没有词条时退回原说明。</summary>
    private string AffixSummary(IReadOnlyCollection<string> affixes)
    {
        if (affixes.Count == 0) return "";
        return string.Join("；", affixes.Select(id =>
            _data.Affixes.TryGetValue(id, out var def) ? $"{def.Name}：{def.Desc}" : id));
    }

    /// <summary>
    /// 自检用：把「挂符文」单独露出来，好在没有掉落/事件的场合里精确验一条规则。
    /// 生产路径只有 ApplyBonusAffix 与 Rewards 两处会走到 AttachAffixes。
    /// </summary>
    public ItemDef AttachAffixesForTest(ItemDef item) => AttachAffixes(item);

    /// <summary>诅咒房掉的那件负面紫装</summary>
    public ItemDef? CurseRoomItem()
    {
        var cfg = _data.Config.Rooms.Curse;
        var ids = cfg.AffixIds.Where(id => _data.Affixes.ContainsKey(id)).ToList();
        if (ids.Count == 0) return null;
        var affixId = Rng.Pick(ids);
        var equipItems = ItemIndex.Values.Where(i => i.Slot != Slot.Consumable).ToList();
        // 从白/蓝品质的模板里挑一件套上负面词条：底子普通，风险全在词条上
        var nonOrange = equipItems.Where(i => Inventory.QualityOf(i) != Quality.Orange).ToList();
        var baseItem = Rng.Pick(nonOrange.Count > 0 ? nonOrange : equipItems);
        // 同样是运行时生成：给唯一 id 并登记，诅咒装才能在背包里活下来
        var next = baseItem.Clone();
        next.Quality = Quality.Purple;
        next.Affixes = new List<string> { affixId };
        next.Desc = $"{baseItem.Desc}（被诅咒缠绕）";
        return RegisterRuntimeItem(next);
    }

    /// <summary>
    /// 血祭祭坛的代价：以一定比例的最大生命上限换攻击力。
    /// 数值从数据读（config.altars.sacrifice），面板上的按钮副标题、结算、
    /// README 的房间表三处共用——以前写死在界面里，改一处就对不上另外两处。
    /// </summary>
    public SacrificeDef Sacrifice => _data.Config.Altars.Sacrifice;

    /// <summary>
    /// 血祭祭坛的结算：扣**固定**的生命上限，换固定攻击力。
    /// 收在这里而不是各写一遍，是因为三处（地图面板、模拟器、自检）都做过同一件事，
    /// 而只有地图那一处是按面板算的——另外两处扣的是 BaseMaxHp，
    /// 于是「说明写着 8% 上限，实际按更小的基础值扣」。
    ///
    /// 代价从「当前上限的 8%」改成「基础上限的 4%，且不低于 `hpCostMin`」：
    /// 比例制让这笔账随进度变负——上限涨上去之后，同样的 6 点攻击要付越来越多的血。
    /// 现在一局里一刀的价格是固定的几档，玩家可以放心把它当成一次真选择。
    /// 返回值是实际扣掉的上限点数（可能被下限 10 夹住）。
    /// </summary>
    public int TakeSacrifice()
    {
        var lost = SacrificeCost();
        Player.BaseMaxHp -= lost;
        Player.BaseAttack += Sacrifice.Attack;
        // 按结算后的面板上限收口（装备的固定生命仍在），且不能低于 1
        Player.Hp = Math.Min(Player.Hp, Math.Max(1, Stats.MaxHp));
        return lost;
    }

    /// <summary>
    /// 这一次血祭要付多少生命上限。**界面与结算读同一个数**：
    /// 按钮上写「-7 生命上限」而实际扣 5，是这个仓库里反复出现的那一类错。
    /// 返回值已经夹在「不能把基础上限扣到 10 以下」的范围内。
    /// </summary>
    public int SacrificeCost()
    {
        var sac = Sacrifice;
        // 代价不跟当前上限走，跟**基础上限**走：它在一局里只被升级与血祭自己改动，
        // 于是「一刀换 N 点攻击」这笔账在同一个地方结算。
        //
        // 以前按面板上限的 8% 算：上限越高，同一笔攻击力越贵。第 9 级身上 90 点上限时
        // 一刀要 7 点血换 6 点攻击，而 6 点攻击只是一个零头——**这个交换条件会随进度变负**。
        // 玩家（和模拟器里的理性策略）照说明做，结果是一路把血换成攻击，越换越脆。
        var cost = Math.Max(sac.HpCostMin, (int)Math.Round(Player.BaseMaxHp * sac.HpCostMaxPct));
        // 扣完不能低于 10：血祭是代价，不是自杀按钮
        return Math.Max(0, Math.Min(cost, Player.BaseMaxHp - 10));
    }

    /// <summary>祭坛房里会出现哪一种祭坛（权重来自配置）</summary>
    public AltarDef RollAltar()
    {
        var weights = _data.Config.Altars;
        var table = new List<(AltarKind Type, int Weight)>
        {
            (AltarKind.Cleanse, weights.Cleanse),
            (AltarKind.Curse, weights.Curse),
            // 泉眼只在深层出现：它的初衷是「深层的钱没处花」，不是让前期更容易。
        };
        if (OfferingUnlocked) table.Add((AltarKind.Offering, weights.OfferingWeight));
        var kind = Rng.Weighted(table, x => x.Weight).Type;
        return Altars[kind];
    }

    /// <summary>泉眼这一层的规矩：深层才有，且一局里换来的成长有上限</summary>
    public OfferingDef Offering => _data.Config.Altars.Offering;

    /// <summary>这一局现在能不能见到泉眼</summary>
    public bool OfferingUnlocked => Offering.MaxSteps > 0 && Floor >= Offering.MinFloor;

    /// <summary>这一局已经换过几次（存档里带着，关窗重开也不会白拿）</summary>
    public int OfferingSteps { get; private set; }

    /// <summary>下一次要多少金币。价钱**不跟钱包走**——
    /// 跟钱包走的那种价（商店的抬价）在这里会变成「钱越多越贵」，而那正是玩家攒钱的动机。
    /// 它跟的是**层数**：越深的进项越大，出口也得跟着长大，否则这条曲线等于白送
    /// （横扫 30 局里献过金的 24 局有 18 局把五次全用完，没人需要为此做任何决定）。</summary>
    public int OfferingCost() => OfferingCostFor(OfferingSteps);

    /// <summary>给自检用：在第 <paramref name="floor"/> 层、第 n 次献金要多少钱</summary>
    public int OfferingCostFor(int steps, int? floor = null) =>
        Offering.BaseCost
        + Offering.CostPerStep * Math.Clamp(steps, 0, Offering.MaxSteps)
        + Offering.CostPerFloor * Math.Max(0, (floor ?? Floor) - Offering.MinFloor);

    /// <summary>还能不能献：次数没满、且这座祭坛只在这一层出现一次</summary>
    public bool CanOffer => OfferingUnlocked && OfferingSteps < Offering.MaxSteps;

    /// <summary>
    /// 献金的结算。返回 (是否成功, 花掉的金币, 换到的上限, 换到的攻击)。
    ///
    /// 金币只走 `SpendGold`：这里以前最容易写成 `Player.Gold -= cost`，
    /// 那样结算面板上的「本局花掉」就会少一笔，而玩家手上的钱确实少了。
    /// </summary>
    public (bool Ok, int Cost, int Hp, int Attack) TakeOffering()
    {
        if (!CanOffer) return (false, 0, 0, 0);
        var cost = OfferingCost();
        if (!SpendGold(cost)) return (false, 0, 0, 0);
        var hp = Offering.HpPerStep;
        var atk = Offering.AttackPerStep;
        var before = Stats.MaxHp;
        Player.BaseMaxHp += hp;
        Player.BaseAttack += atk;
        // 上限涨了，血跟着涨同样的量（和升级一致）：不然「多出来的上限」要等下一条命才看得见。
        //
        // 顺手收一次口：`BeforeMaxHpGrows` 那条规矩以前只在升级与升级技能里各写一遍，
        // 谁新加一个「涨上限」的入口就漏一次——泉眼这里漏了：
        // 血 95 / 上限 81，而那一局的等级是涨过的。收口之后新入口只要走这支函数就不会再漏。
        BeforeMaxHpGrows(before, hp);
        OfferingSteps += 1;
        return (true, cost, hp, atk);
    }

    public (bool Moved, Room? Room, int Index) Move(int dx, int dy)
    {
        if (Status != GameStatus.Playing) return (false, null, PlayerIndex);
        var cur = CurrentRoom;
        var target = Map.Rooms.FindIndex(r => r.X == cur.X + dx && r.Y == cur.Y + dy);
        if (target < 0 || !MapGen.CanMove(Map, PlayerIndex, target)) return (false, null, PlayerIndex);
        PlayerIndex = target;
        var room = Map.Rooms[target];
        room.Visited = true;
        return (true, room, target);
    }

    public Room Teleport(int index)
    {
        PlayerIndex = index;
        Map.Rooms[index].Visited = true;
        return Map.Rooms[index];
    }

    /// <summary>
    /// 运行时生成的装备（诅咒装 / 附魔装 / 精英附词条装）发唯一 id 并登记。
    ///
    /// 序号必须从「已经登记过的 id」里推，而不是每次从 1 重新数：
    /// 读档会把这些装备按原 id 塞回 ItemIndex，而计数器是字段、读档后归零。
    /// 于是读档后开的第一件运行时装备又叫 `..._r1`——直接覆盖掉背包里那一件的定义，
    /// 玩家身上那件紫装的数值与词条被静默换成了新掉落的，背包里两件同名不同物。
    /// </summary>
    private ItemDef RegisterRuntimeItem(ItemDef def)
    {
        var id = NextRuntimeItemId(def.Id);
        var runtime = def.Clone();
        runtime.Id = id;
        ItemIndex[id] = runtime;
        return runtime;
    }

    /// <summary>
    /// 把一件装备副本登记成运行时装备（发唯一 id + 进 ItemIndex），供事件等外部系统调用。
    /// 生产代码走 CurseRoomItem / ApplyBonusAffix / Rewards，它们都落到 RegisterRuntimeItem。
    /// </summary>
    public ItemDef RegisterRuntimeItemCopy(ItemDef def) => RegisterRuntimeItem(def);

    /// <summary>
    /// 自检用：按指定底板登记一件运行时装备并返回它。
    /// 这条入口只是把「底板」露出来，好让断言能精确地验证「同底板不会撞号」。
    /// </summary>
    public ItemDef ProbeRegisterRuntimeItem(string baseId, string desc) =>
        RegisterRuntimeItem(new ItemDef { Id = baseId, Name = baseId, Slot = Slot.Weapon, Desc = desc });

    /// <summary>
    /// 运行时装备的 id：底板 id + 一个当前没被占用的序号。
    ///
    /// 两处都要防撞：读档恢复的运行时装备占着历史 id（`_r1`、`_r2`…），
    /// 而 `_runtimeItemSeq` 只认本进程发过的号。两者一交错，新生成的那件就会叫
    /// `_r1`，把背包里那件的定义整个盖掉——玩家身上的紫装被静默换成了刚掉落的那件。
    /// </summary>
    private string NextRuntimeItemId(string baseId)
    {
        string id;
        do
        {
            id = $"{baseId}_r{++_runtimeItemSeq}";
        } while (ItemIndex.ContainsKey(id));
        return id;
    }

    /// <summary>
    /// 读档时登记一件运行时装备，并保留它原来的 id。
    /// 与 RegisterRuntimeItem 的区别：那个是「新生成一件」，这个是「认得旧的那件」。
    /// 它同时把序号计数器推到已用过的最大值，这是 NextRuntimeItemId 的第一道防线。
    /// </summary>
    public void RestoreRuntimeItem(string id, ItemDef def)
    {
        def.Id = id;
        ItemIndex[id] = def;
        var at = id.LastIndexOf("_r", StringComparison.Ordinal);
        if (at >= 0 && int.TryParse(id[(at + 2)..], out var seq) && seq > _runtimeItemSeq) _runtimeItemSeq = seq;
    }

    /// <summary>
    /// 刚进入的战斗日志（只给 --play 用，生产路径不读它）。
    /// 「这一局是在哪一类房间里丢的血」这个问题需要一个能留住战斗现场的东西，
    /// 而 Battle 是短命的——它一 Dispose，答案就没了。
    /// </summary>
    public string LastBattleLog { get; set; } = "";

    /// <summary>当前房间是否需要触发战斗；需要时返回敌人数据</summary>
    public EncounterDef? CreateEncounter()
    {
        var room = CurrentRoom;
        // 这一场实际生效的楼层机制，只在开头取一次。
        //
        // 它以前是在 `return` 那一刻现读的：敌人已经按 A 建好了，而遭遇上写的是那一刻的 B。
        // 摆现场借一层机制时立刻就会撞上——敌人会炸，界面与结算都说「这一层没有机制」。
        var mechanicId = FloorMechanicId;
        var isBoss = room.Type == RoomType.Boss;
        var isElite = room.Type == RoomType.Elite;
        // 第 25 层是三 Boss 连战：队列按楼层重建，不靠「第一次进房」这个隐式时机——
        // 读档后内存里没有队列，靠时机的话玩家会在打到第二只时被重排回第一只。
        if (isBoss && BossStage(Floor)?.Mechanic == "boss_gauntlet") EnsureGauntlet();
        if (room.Type != RoomType.Enemy && !isBoss && !isElite) return null;
        if (!isBoss && !isElite && room.Cleared) return null;
        // Boss 房必须真的有一场 Boss 战。这一句以前不存在：地图上留着一个 Boss 房、
        // 而这一层没有 Boss 阶梯时，EncounterDef 会退到 `_data.Bosses[0]`——
        // 玩家在第 3 层撞见第 5 层的 Boss，或者更常见的是「走进 Boss 房什么都没发生」，
        // 因为 UI 那侧拿到 null 只弹一句「这里没有敌人」，而房间永远不清空。
        // 现在这属于数据自相矛盾，直接抛，别让它在运行时变成一句含糊的提示。
        if (isBoss && BossStage(Floor) is null && _gauntletQueue.Count == 0)
            throw new InvalidOperationException($"第 {Floor} 层有 Boss 房，但 bossStages 里没有这一层的 Boss");

        // Boss 房永远是单只：三 Boss 连战由 bossStages 的多档实现，不走组合槽。
        // 精英房同样单只：它是一只「被强化过的普通怪」，不是一群。
        var slot = isBoss || isElite ? null : PickEncounterSlot();
        var defs = slot is not null
            ? slot.Select(MonsterById).Where(d => d is not null).Select(d => d!).ToList()
            : new List<MonsterDef> { PickMonster(isBoss) };
        if (defs.Count == 0) defs.Add(PickMonster(isBoss));

        var main = defs[0];
        var skills = MonsterSkills(main);
        var monster = EntityRules.CreateMonster(main, skills, Floor);
        // 精英房：属性按层数递增（1 + 层数 × 0.03）。前期几乎无感、第 10 层 1.3 倍，
        // 避免了「第 2 层撞见一只 1.5 倍血量的兽人直接劝退」。
        if (isElite)
        {
            var scale = EliteScale();
            monster.BaseMaxHp = (int)Math.Round(monster.BaseMaxHp * scale);
            monster.Hp = monster.BaseMaxHp;
            monster.BaseAttack = (int)Math.Round(monster.BaseAttack * scale);
            monster.BaseDefense = (int)Math.Round(monster.BaseDefense * scale);
        }
        // 多周目：敌人属性按周目递增。递增作用在实例上而不是模板上，
        // 否则第 2 周目的怪物定义会被永久改写。
        ApplyLoopScale(monster);
        ApplyLoopCurse(monster);
        FloorMechanics.ApplyFloorMechanic(mechanicId, monster);

        // 组合里的其余成员：各自建实例、各自过楼层机制，互不影响
        var allies = new List<Combatant>();
        var allyDefs = new List<MonsterDef>();
        var enemyDefs = new Dictionary<Combatant, MonsterDef> { [monster] = main };
        foreach (var d in defs.Skip(1))
        {
            var s = MonsterSkills(d);
            var c = EntityRules.CreateMonster(d, s, Floor);
            // 组合成员按人数打折：2 只时每只 0.85，3 只时每只 0.8。
            // 不打折的话「一间房两只怪」等于两场战斗的血量总和，玩家纯粹被数值墙拍死。
            var scale = defs.Count >= 3 ? 0.8 : 0.85;
            c.BaseMaxHp = Math.Max(1, (int)Math.Round(c.BaseMaxHp * scale));
            c.Hp = c.BaseMaxHp;
            c.BaseAttack = Math.Max(1, (int)Math.Round(c.BaseAttack * scale));
            c.BaseDefense = Math.Max(0, (int)Math.Round(c.BaseDefense * scale));
            ApplyLoopScale(c);
            ApplyLoopCurse(c);
            FloorMechanics.ApplyFloorMechanic(mechanicId, c);
            allies.Add(c);
            allyDefs.Add(d);
            enemyDefs[c] = d;
        }

        // 档位：组合房按精英算——多打一只就该多拿一份，
        // 否则玩家会学会「绕着组合房走」，组合就成了纯惩罚。
        var tier = isBoss ? EnemyTier.Boss : isElite || allies.Count > 0 ? EnemyTier.Elite : EnemyTier.Normal;

        var stage = isBoss ? BossStage(Floor) : null;
        return new EncounterDef
        {
            Monster = monster,
            Def = main,
            Skills = skills,
            Allies = allies,
            AllyDefs = allyDefs,
            EnemyDefs = enemyDefs,
            Tier = tier,
            // 精英房金币 ×2：掉装备之外的另一条保底，玩家残血时也能选
            GoldMultiplier = isElite ? 2 : stage?.GoldMultiplier ?? 1,
            XpMultiplier = stage?.XpMultiplier ?? 1,
            FloorMechanic = FloorMechanics.MechanicInfo(mechanicId),
            FloorMechanicId = mechanicId,
            // 三 Boss 连战：每一只用它自己的机制，而不是整场套一个「连战」元数据。
            // 以前这里统一给 boss_gauntlet，而它没有 arm——
            // 于是熔岩领主不爆壳、霜蚀女王不展镜，玩家打的是三只「没有机制的 Boss」。
            BossMechanic = isBoss && stage is not null && !string.IsNullOrEmpty(stage.Mechanic)
                ? FloorMechanics.BossMechanicInfo(BossMechanicIdOf(stage))
                : null,
            // 暴戾（敌人暴击率 +10%）由战斗侧结算
            EnemyCritBonus = LoopCurse?.CritBonus ?? 0,
            Gauntlet = _gauntletQueue.Count > 0 ? (_gauntletIndex, _gauntletQueue.Count) : null,
        };
    }

    /// <summary>
    /// 这场战斗实际生效的 Boss 机制 id。
    /// 连战时按队列里当前那只 Boss 查它自己的机制，其它情况直接用 bossStages 上配的那个。
    /// </summary>
    public string BossMechanicIdOf(BossStageDef stage)
    {
        if (stage.Mechanic != "boss_gauntlet") return stage.Mechanic ?? "";
        EnsureGauntlet();
        var current = _gauntletQueue[Math.Min(_gauntletIndex, _gauntletQueue.Count - 1)];
        // 队列里每只 Boss 在 bossStages 里都有自己的机制档，按 id 反查
        var own = _data.BossStages.FirstOrDefault(s => s.Id == current.Id);
        return own?.Mechanic ?? stage.Mechanic ?? "";
    }

    /// <summary>
    /// 「这一间战斗房大概要花掉玩家几成血」：一个**估**，用来回答「剩下的路值不值得走」。
    ///
    /// 它不是结算，也不是第二份规则。它只做一件事：把这一间房的敌人面板与当前面板
    /// 摆在一起，算「我砍它要几刀、它砍我要几刀」。真实伤害还要过防御、浮动、暴击、
    /// 技能冷却、楼层机制五六道修正——所以这里的口径刻意粗，并且**只用于文案与探针**，
    /// 任何结算路径都不读它。多写一份精确规则，就多一处会分叉的地方（这仓库里最贵的一类 bug）。
    ///
    /// 三种房各自的口径：普通房按「一只」、精英房乘 `EliteScale`、
    /// Boss 房按 bossStages 里配的那只。房间类型本来就决定敌人是谁，这里不另立一张表。
    /// </summary>
    public double RoomCostRatio(RoomType type)
    {
        var maxHp = Math.Max(1, Stats.MaxHp);
        var atk = Math.Max(1, Stats.Attack);
        var def = Stats.Defense;
        var cfg = _data.Config.Rooms.Elite;
        // 普通房的「一只」取本层档位里最弱的那只：玩家在一层里遇到的多半不是最狠的那只，
        // 拿最狠的估，会把「剩下的仗」报成一个人不敢走的路。
        var pool = _data.Monsters.Where(m => (m.MinFloor ?? 1) <= Floor).ToList();
        if (pool.Count == 0) pool = _data.Monsters.ToList();
        if (pool.Count == 0) return 0;
        var baseDef = pool.OrderBy(m => m.Hp).First();
        var scale = type switch
        {
            RoomType.Elite => cfg.ScaleBase + Floor * cfg.ScalePerFloor,
            _ => 1.0,
        };
        if (type == RoomType.Boss && BossStage(Floor) is { } stage)
        {
            var boss = _data.Bosses.FirstOrDefault(b => b.Id == stage.Id) ?? _data.Bosses.FirstOrDefault();
            if (boss is not null) baseDef = boss;
        }
        var hp = baseDef.Hp * scale;
        var eatk = Math.Max(1, (int)Math.Round(baseDef.Attack * scale));
        var edef = Math.Max(0, (int)Math.Round(baseDef.Defense * scale));
        // 我要几刀：每刀至少 1 点（防御再高也砍得动，不然估出来是无穷大）
        var myHit = Math.Max(1, atk - edef);
        var swings = Math.Ceiling(hp / myHit);
        // 它要几刀：按玩家的防御折一次
        var itsHit = Math.Max(1, eatk - def);
        var loss = swings * itsHit;
        // 普通房按 85% 折（浮动与落空），Boss 房不打折：那是必打的一场，别把票价报低
        if (type != RoomType.Boss) loss *= 0.85;
        return Math.Clamp(loss / maxHp, 0, 3.0);
    }

    /// <summary>精英房当前的属性倍率：基础值 + 层数 × 每层增量，随深潜缓增</summary>
    public double EliteScale(int? floor = null)
    {
        var cfg = _data.Config.Rooms.Elite;
        return cfg.ScaleBase + (floor ?? Floor) * cfg.ScalePerFloor;
    }

    /// <summary>周目递增：每多一周目，敌人攻防血 +15%</summary>
    private void ApplyLoopScale(Combatant c)
    {
        if (Loop <= 1) return;
        var scale = 1 + (Loop - 1) * _data.Config.Meta.LoopEnemyScale;
        c.BaseMaxHp = (int)Math.Round(c.BaseMaxHp * scale);
        c.Hp = c.BaseMaxHp;
        c.BaseAttack = (int)Math.Round(c.BaseAttack * scale);
        c.BaseDefense = (int)Math.Round(c.BaseDefense * scale);
    }

    /// <summary>
    /// 本周目的全局负面效果（第 15 层之后每周目一个）。
    /// 数值挂在同一条 getter 上，界面与结算读同一份。
    /// </summary>
    public LoopCurse? LoopCurse
    {
        get
        {
            var list = _data.Config.Meta.LoopCurses;
            if (Loop <= 1 || list.Count == 0) return null;
            var cur = list[(Loop - 2) % list.Count];
            if (LoopCurseEffects.TryGetValue(cur.Name, out var spec))
            {
                return new LoopCurse { Name = cur.Name, Desc = cur.Desc, CritBonus = spec.CritBonus, DefenseBonus = spec.DefenseBonus, AttackPct = spec.AttackPct };
            }
            return new LoopCurse { Name = cur.Name, Desc = cur.Desc };
        }
    }

    /// <summary>本周目负面效果给敌人的属性加成：由 CreateEncounter 在建实例时套上</summary>
    private void ApplyLoopCurse(Combatant c)
    {
        var cur = LoopCurse;
        if (cur is null) return;
        if (Math.Abs(cur.AttackPct - 1) > 1e-9) c.BaseAttack = (int)Math.Round(c.BaseAttack * cur.AttackPct);
        // 比例不是点数：说明写「防御 +15%」，早先这里写成 += 0.15，
        // 等于给敌人加了 0 点防御——玩家看到提示，实际什么都没发生。
        if (cur.DefenseBonus > 0) c.BaseDefense = (int)Math.Round(c.BaseDefense * (1 + cur.DefenseBonus));
    }

    /// <summary>本周目商店价格倍率</summary>
    public double ShopPriceScale => Loop <= 1 ? 1 : 1 + (Loop - 1) * _data.Config.Meta.LoopShopScale;

    /// <summary>
    /// 随机池的深度窗口：只留「最近这么多层之内出场过的怪」。
    /// 8 是数据表里 minFloor 的跨度（1~15）；取更大的值等于没淘汰，取更小会在
    /// 某一层把池子抽空（那时退回「全部 minFloor 已到」的池子）。
    /// </summary>
    public const int DepthPoolSpan = 8;

    /// <summary>自检用：直接问「这一层的随机池会给出什么怪」（不走战斗，也不推进主序列以外的状态）</summary>
    public MonsterDef ProbePickMonster() => PickMonster(false);

    /// <summary>按 id 找怪物定义：组合槽里写错 id 时跳过它，不让整间房空掉</summary>
    private MonsterDef? MonsterById(string id) => _data.Monsters.FirstOrDefault(m => m.Id == id);

    /// <summary>
    /// 从当前层的组合槽里随机抽一组。
    /// 没有配置这一层时返回 null，退化成单人战斗——浅层本来就该一只一只打。
    /// </summary>
    private List<string>? PickEncounterSlot()
    {
        if (!_data.EncounterSlots.TryGetValue(Floor.ToString(), out var slots) || slots.Count == 0) return null;
        // 过一遍 minFloor：深层怪不会在第 2 层登场，哪怕组合里写了它
        var available = slots.Where(s => s.Monsters.All(id =>
        {
            var m = MonsterById(id);
            return m is not null && (m.MinFloor ?? 1) <= Floor;
        })).ToList();
        var pool = available.Count > 0 ? available : slots;
        return Rng.Pick(pool).Monsters;
    }

    public MonsterDef PickMonster(bool isBoss)
    {
        if (isBoss)
        {
            // 三 Boss 连战：按队列依次出场，中途不回复、不清状态
            if (_gauntletQueue.Count > 0) return _gauntletQueue[Math.Min(_gauntletIndex, _gauntletQueue.Count - 1)];
            var stage = BossStage(Floor);
            return _data.Bosses.FirstOrDefault(b => b.Id == stage?.Id) ?? _data.Bosses[0];
        }
        // 越深的层抽越强的怪。
        //
        // 旧判据只有「minFloor <= 当前层」——它只淘汰「还没到出场的怪」，
        // 从不淘汰「早就该退场的怪」。于是第 20 层（通关后的深潜）的随机池里
        // 仍然蹲着第 1 层的巨鼠：25 只怪全在里面，玩家有一半概率碰上
        // 「防御 1、攻击 6」的东西，而这一层本该是深渊。
        // 深潜段（第 16 层起）没有 `encounterSlots`，走的就是这条路，
        // 所以它不只是「偶尔抽到弱怪」，而是整段都退化成全池随机。
        //
        // 现在的判据是「往上数 DEPTH_POOL 层之内出场过的怪」：深层只留深层的怪，
        // 而 minFloor 的语义不变（还是「最早第几层能见到」）。
        var maxMinFloor = Math.Max(1, Floor - DepthPoolSpan + 1);
        var pool = _data.Monsters
            .Where(m => (m.MinFloor ?? 1) <= Floor && (m.MinFloor ?? 1) >= maxMinFloor)
            .ToList();
        if (pool.Count == 0) pool = _data.Monsters.Where(m => (m.MinFloor ?? 1) <= Floor).ToList();
        return Rng.Pick(pool.Count > 0 ? pool : _data.Monsters);
    }

    public List<SkillDef> MonsterSkills(MonsterDef def) =>
        def.Skills.Select(id => _data.Skills.FirstOrDefault(s => s.Id == id)).Where(s => s is not null).Select(s => s!).ToList();

    public void ClearRoom(int? index = null) => Map.Rooms[index ?? PlayerIndex].Cleared = true;

    /// <summary>
    /// 本层是否已经清空。宝箱/商店/事件房/祭坛房都不拦路（玩家可以放着不管），
    /// 只有敌人房与精英房必须打完；Boss 层同理。
    /// </summary>
    public bool FloorCleared =>
        Map.Rooms.All(r => !BlocksDescend(r));

    /// <summary>
    /// 这一间房还挡不挡路（没打完的敌人房 / 精英房 / 有 Boss 阶梯的 Boss 房）。
    /// 单独列出来是因为它有两个读者：`FloorCleared`（这一层过没过）
    /// 和 `Game.PendingBattlesBelow`（这一格底下还压着几场仗）。
    /// 以前这条判据只写在 `FloorCleared` 里，于是「地图上还有几场仗」只能靠
    /// 数量（RemainingBattleRooms）说，说不出「在哪一边」。
    /// </summary>
    private bool BlocksDescend(Room r) => r.Type switch
    {
        RoomType.Enemy => !r.Cleared,
        RoomType.Elite => !r.Cleared,
        RoomType.Boss => !r.Cleared && BossStage(Floor) is not null,
        _ => false,
    };

    /// <summary>
    /// 从玩家现在站的地方继续打完一层，**必须**再打几场仗（含已经挡在路上的那一间）。
    ///
    /// 为什么单独有一个数：`FloorCleared` 只回答「这一层能不能下楼」，
    /// 它要求的是「全图所有战斗房都打掉」；而一层平均有 11 间房、3.2 场仗，
    /// 真正挡在「我 → Boss」这条必经之路上的只有 1.1 场（40 种子 × 14 层实测）。
    /// 也就是说 67% 的战斗房是可选的，而游戏里没有任何地方说过这件事——
    /// 玩家看到「剩余战斗房 3」，会以为要打三场才能下楼，于是照着打完，
    /// 然后在中段被消耗掉。
    ///
    /// 判据和 `BlocksDescend` 共用一份：如果这里另写一遍「哪些房算仗」，
    /// 迟早会和「能不能下楼」分叉（那正是前几轮反复踩的接缝）。
    /// 走的是 BFS 最短路，不是一个方向数——Boss 可能就在隔壁那条岔上。
    /// </summary>
    public int BattlesToBoss()
    {
        var links = Map.Links;
        var target = Map.BossIndex;
        if (target < 0 || target >= links.Count) return 0;
        var dist = MapGen.BfsDistances(links, PlayerIndex);
        if (dist[target] < 0) return 0;
        // 沿「离 Boss 更近一格」的方向回溯，把这条最短路上的战斗房数出来。
        var count = 0;
        var cur = target;
        while (cur != PlayerIndex)
        {
            if (BlocksDescend(Map.Rooms[cur])) count += 1;
            var next = -1;
            foreach (var n in links[cur])
            {
                if (dist[n] != dist[cur] - 1) continue;
                next = n;
                break;
            }
            // 图是树，dist 一定连续递减到 0；走到这里说明起点到 Boss 不连通（不该发生）
            if (next < 0) return count;
            cur = next;
        }
        return count;
    }

    /// <summary>
    /// 从起点开始走一遍这棵树：每个房间记下「它所在的这条岔路底下还压着几场仗」。
    ///
    /// 为什么需要这个数：房间图是一棵树，玩家走一条分支到尽头再折返，
    /// 而地图上只有「打过 / 没打过」两种像素，没有「这一边我根本没去」。
    /// 于是一层里 50% 的房间是死胡同（跑 40 个种子 × 25 层实测），
    /// 每个死胡同都要走进去、发现是空的、再原路走回来——
    /// 而玩家在岔口没有任何依据判断哪一边有仗。这不是地图生成了孤岛
    /// （树保证了连通），是**地图不肯说它知道的事**：它本来就知道。
    /// </summary>
    public int[] PendingBattlesBelow()
    {
        var rooms = Map.Rooms;
        var pending = new int[rooms.Count];
        var order = new List<int>();
        var stack = new Stack<int>();
        stack.Push(Map.StartIndex);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            order.Add(cur);
            foreach (var child in rooms[cur].Children) stack.Push(child);
        }
        // 后序累加：子房间先算完，父房间再加起来。
        // 用 Children 而不是 Links：Links 是双向的（含父房间），按它累加会把整棵树算成「全场剩几场仗」。
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var cur = order[i];
            var sum = BlocksDescend(rooms[cur]) ? 1 : 0;
            foreach (var child in rooms[cur].Children) sum += pending[child];
            pending[cur] = sum;
        }
        return pending;
    }

    public List<Reward> Rewards(EnemyTier tier = EnemyTier.Normal, int extra = 0)
    {
        var missingHp = Math.Max(0, Stats.MaxHp - Player.Hp);
        var list = RewardRoller.Roll(Rng, _data.Items, new RewardContext
        {
            Floor = Floor,
            MissingHp = missingHp,
            Tier = tier,
            Extra = extra,
            DualAffixUnlocked = MetaProgression.HasUnlock(Meta, MetaUnlock.DualAffix),
            // 掉落的那件也是「副本」：带词条、换过品质的必须登记。
            // 不登记的话背包里只有一个模板 id，而存档只写得下 _rN 的运行时装备，
            // 玩家拿着这件读档，背包那一格就凭空消失（和事件装备当年同一个坑）。
            RegisterItem = RegisterRuntimeItemCopy,
        });
        // 精英房：掉的那件装备保底蓝色、并按概率附一条随机词条。
        // 训练房的附魔优先作用在同一件装备上——两个效果都落在「这一件」上，
        // 玩家才看得见自己刚才那个选择换来了什么。
        var usedBonus = false;
        var outList = new List<Reward>();
        foreach (var r in list)
        {
            if (r is not Reward.Item itemReward || usedBonus)
            {
                outList.Add(r);
                continue;
            }
            usedBonus = true;
            var item = tier == EnemyTier.Elite
                ? Loot.EliteDropBonus(Rng, itemReward.Def, AffixPool(), itemReward.Def.Affixes)
                : itemReward.Def;
            // 符文只给装备槽：以前消耗品也会被「附魔」，符文白刻在一个药水上。
            // 走 AttachAffixes：它一次挂上全部待生效符文，并保证「数量与品质匹配」。
            if (_pendingBonusAffixes.Count > 0 && item.Slot != Slot.Consumable) item = AttachAffixes(item);
            // 精英房的词条是「附」上去的运行时对象：和诅咒房、附魔装一样，
            // 必须登记进 ItemIndex，否则背包里按 id 只会查到原始模板。
            if (!ReferenceEquals(item, itemReward.Def) && item.Slot != Slot.Consumable) item = RegisterRuntimeItem(item);
            outList.Add(ReferenceEquals(item, itemReward.Def) ? r : new Reward.Item { Def = item });
        }
        return outList;
    }

    /// <summary>已经 roll 过的货架（存档要原样带走，读档才不换货）</summary>
    public IReadOnlyDictionary<int, List<ShopStockItem>> RolledShops => _shopCache;

    /// <summary>读档恢复货架：连 Sold 一起搬回来，免得读档白送一次补货</summary>
    public void RestoreShop(int index, List<ShopStockItem> stock) => _shopCache[index] = stock;

    /// <summary>
    /// 货架。进的是「这一层该卖什么」，不是「全表抽 3~4 件」。
    ///
    /// 以前装备、药、炸弹混在一个池子里按名额抢，而装备有 11 件、药只有 2 件：
    /// 横扫 30 局里 268 家店一共只卖掉 53 瓶药，而「钱够、店里有药、买不进来」
    /// 出现过 200 次——深层一家店里通常是「三件你买得起但也用不上的装备」。
    /// 现在按三种东西分开摆：
    ///  · 一件**本层档位**的装备（模板价跟层数走，越深越贵、也越值得买）；
    ///  · 一件**回血药**（唯一的主动补血手段，永远在架，只是价格跟着钱包抬）；
    ///  · 一件**战术道具**（炸弹这类，商店以前完全没有进货通道）；
    ///  · 剩下 0~1 格随机（再来一瓶药、或一件别的装备）。
    /// 这样「进店要有取舍」才有内容可取舍，而不是三件都买不起。
    /// </summary>
    public List<ShopStockItem> ShopStock(int index)
    {
        if (!_shopCache.TryGetValue(index, out var stock))
        {
            var consumables = _data.Items.Where(i => i.Slot == Slot.Consumable).ToList();
            var gear = _data.Items.Where(i => i.Slot != Slot.Consumable).ToList();
            // 清单按**这一层该有的东西**给：`ShopStock` 只负责摆货与标价，
            // 「第 1 层不该有 +23 的剑」这条规则活在 `ShopRoller.TierFor` 里。
            // 以前只有「模板价 ≤ 50+8×层数」这一道上限，下半边没人管：
            // 第 9 层可能摆一把 +23 的深渊之牙，而第 15 层也可能只有锈剑。
            var rolled = ShopRoller.Roll(Rng, gear, consumables, Floor);
            var scale = ShopPriceScale;
            stock = rolled.Select(s => Reprice(s, scale)).ToList();
            _shopCache[index] = stock;
        }
        return stock;
    }

    /// <summary>
    /// 标价。价格 = 模板价 × 周目系数，再按「玩家手上的钱」抬一档。
    ///
    /// 后一半是这一轮加的，起因是一个跑出来的现象：金币常年 1500~1900，
    /// 而一层最多一间商店、货架上四件加起来不到 200。打完第 8 层之后，
    /// 「钱」这个资源就彻底没有下文了——不是玩家太会攒，是价格曲线根本不涨：
    /// 模板价写死在 8~110，楼层只加 1.5/层，第 15 层也只贵 22 点。
    /// 于是深层的商店等于白送，而真正的稀缺（药、材料）又只有这一间店能买到。
    ///
    /// 抬价是「跟着你的钱包走」而不是「跟着楼层走」：
    ///  - 只抬到能买得起：钱多的时候不至于让人站着看四件都买不起；
    ///  - 上限 3 倍，且只对「兜里钱明显多于货值」的情况生效；
    ///  - 一只药的成本跟着抬，所以「深层没药」这件事不能靠攒钱解决，
    ///    只能靠每层的取舍——这正是这游戏想让人做的决定。
    /// </summary>
    private ShopStockItem Reprice(ShopStockItem s, double loopScale) => RepriceFor(s, loopScale, Player.Gold);

    /// <summary>按指定的钱包余额算价（自检要问「如果我有 N 金币，这件多少钱」）</summary>
    public ShopStockItem RepriceFor(ShopStockItem s, double loopScale, int gold)
    {
        var price = s.Price * loopScale;
        var reference = Math.Max(1, _data.Items.Max(i => i.Price));
        var surplus = (double)gold / reference;
        if (surplus > 2.0)
        {
            // 1 倍起步，每超出一个「最贵模板价」加 0.25，封顶 3 倍
            price *= Math.Min(3.0, 1 + (surplus - 2.0) * 0.25);
        }
        return s.ClonePriced(Math.Max(1, (int)Math.Round(price)));
    }

    public EventDef RandomEvent() => GameEvents.PickEvent(Rng, _data.Events, UsedEvents);

    /// <summary>
    /// 下楼。本层还有没打的敌人房时拒绝，否则等于跳过内容去刷更深的层。
    /// 返回失败原因，让 UI 能说清楚为什么按了没反应。
    /// </summary>
    public (bool Ok, string? Reason) Descend()
    {
        // 状态必须先判，而且判在「清空」之前：通关那一刻 Boss 房刚被清掉，
        // `FloorCleared` 恰好为真，只查清空就会放行——于是打穿第 15 层的人还能
        // 按 > 继续往下走，楼层一直涨到没地图的深处。UI 那条路先问了状态，
        // 所以玩家从没踩到；但「规则只写对了一半」迟早会被下一个人踩到。
        if (Status != GameStatus.Playing) return (false, "冒险已经结束了");
        if (!FloorCleared) return (false, "这一层还有房间没有处理");
        SettlePendingFirstClears();
        Floor += 1;
        Map = GenerateFloor(Floor);
        PlayerIndex = Map.StartIndex;
        Map.Rooms[PlayerIndex].Visited = true;
        _shopCache.Clear();
        ResetFloorBuffs();
        // 中毒的致死预算按层给：新的一层，毒重新有「能毒死几次」的额度。
        Player.PoisonDyingLeft = PoisonDyingBudget;
        return (true, null);
    }

    /// <summary>
    /// 金币进出只走这里。以前「挣」与「花」各有各的写法：
    /// 战斗赏金记在 Game.GoldEarned，事件赏金记在 Player.GoldEarned，
    /// 而买下东西、血祭、付门票则只在 UI 里 `Gold -= price` 一笔——
    /// 于是同一次存档里出现了三份对不上的账（结算面板、存档字段、实际余额）。
    /// 现在统一：任何改 Gold 的地方都调 EarnGold / SpendGold。
    /// </summary>
    public void EarnGold(int amount)
    {
        if (amount <= 0) return;
        Player.Gold += amount;
        Player.GoldEarned += amount;
        GoldEarned += amount;
    }

    /// <summary>花掉金币。返回 false 表示钱不够，什么都没变</summary>
    public bool SpendGold(int amount)
    {
        if (amount <= 0) return true;
        if (Player.Gold < amount) return false;
        Player.Gold -= amount;
        Player.GoldLost += amount;
        return true;
    }

    /// <summary>把背包里的一格卖掉。钱只走 EarnGold，界面只需说结果。</summary>
    public bool SellItem(int index)
    {
        var sold = Inventory.Sell(Player, index, ItemIndex);
        if (sold is not { } s) return false;
        EarnGold(s.Gold);
        return true;
    }

    /// <summary>战斗胜利后的结算：金币、经验、可能的升级；只有最终层的 Boss 战胜利才通关</summary>
    public void OnBattleWon(MonsterDef def, bool isBoss, int goldMultiplier = 1, int xpMultiplier = 1, int members = 1)
    {
        // 结果只由这里宣告。战斗打出 Won 而没人调这一句，房间永远不清空、
        // 玩家会被关在一间打完的房里——所以 BattleScene 收尾时走 SettleBattle，
        // 那边会断言「每一场都已经有了结局」。
        if (Status != GameStatus.Playing) return;
        Kills += 1;
        BattlesFought += 1;
        // 组合房间按「实际打倒了几只」结算：多打一只多拿一份金币与经验。
        var count = Math.Max(1, members);
        var gold = (int)Math.Round(def.Gold * (double)goldMultiplier * count);
        var xp = (int)Math.Round(def.Xp * (double)xpMultiplier * count);
        EarnGold(gold);
        Player.Kills = Kills;
        GrantXp(xp);
        // 连战中途不清房间：清了就等于「打完熔岩领主这层就过了」
        if (_gauntletQueue.Count == 0 || _gauntletIndex >= _gauntletQueue.Count - 1) ClearRoom();
        // 首次击败 Boss 发回声碎片。同一层只发一次——
        // 否则玩家可以靠「反复刷同一层」把碎片刷爆，元进展就变成了肝度。
        // 判据与补发那条路共用 SettleFirstClear：连战的第 25 层有三只 Boss，
        // 只有队列走完（房间真的清空）之后才轮得到它，否则打到第二只就先发了一次。
        if (isBoss && (_gauntletQueue.Count == 0 || _gauntletIndex >= _gauntletQueue.Count - 1))
            SettleFirstClear(Floor, def.Id);
        // 三 Boss 连战：打倒一只就换下一只，房间不算清空，玩家不回血、不重置状态。
        if (isBoss && _gauntletQueue.Count > 0 && _gauntletIndex < _gauntletQueue.Count - 1)
        {
            _gauntletIndex += 1;
            return;
        }
        // 只有通关层的 Boss 才算打完：更深的层只是又一个里程碑，玩家可以继续下潜
        if (isBoss && IsFinalFloor)
        {
            Status = GameStatus.Won;
            GrantShards(_data.Config.Meta.ClearShards);
        }
    }

    /// <summary>
    /// 记一条成就并写回存档。
    /// 以前这里是 `Meta = GrantAchievement(...)` 一行——状态改了，但没走到 SaveMeta，
    /// 于是「击败逆位 Boss」这条成就只在内存里活着：回标题、关游戏就没了。
    /// 碎片走了 MetaSaver，成就漏了同一步，这是同一条链路上的两种待遇。
    /// </summary>
    public void GrantAchievement(string id)
    {
        // 和碎片同理：这里也只能原地改。以前写的是 `Meta = next`——
        // 换对象之后，任何拿着旧引用的读者（主菜单的解锁列表、下一局的起点）
        // 看到的都是「还没拿到这条成就」的那一份。
        if (Meta.Achievements.Contains(id)) return;
        Meta.Achievements.Add(id);
        MetaSaver?.Invoke(Meta);
    }

    /// <summary>发放回声碎片并写回存档</summary>
    public void GrantShards(int amount)
    {
        if (amount <= 0) return;
        ShardsEarned += amount;
        // 原地改，不换对象：`Meta` 的读者不止一个（主菜单、SaveMeta、下一局的起点），
        // 换对象只在「所有人都从同一个入口重新取」时才等价，而这里并不是。
        Meta.Shards = Math.Max(0, Meta.Shards + amount);
        MetaSaver?.Invoke(Meta);
    }

    /// <summary>
    /// 买下一档元进展。碎片不够返回 false，什么都不变。
    ///
    /// 以前这段逻辑只写在主菜单里，改的是 `store.Meta`——而一个正在进行的局
    /// 手里握着开局那一刻的快照。玩家结束这局、回标题再开一局时，
    /// 新局反而能读到解锁（走的是 GameStore.Meta），但只要这一局里再挣到一块碎片，
    /// `SaveMeta(Run.Meta)` 就会把那份没有解锁的旧快照写回盘上，**解锁被静默撤销**。
    /// 现在解锁与结算同源：都改这一局手里那一份，也就只有一份。
    /// </summary>
    public bool TryUnlock(MetaUnlock id)
    {
        var cost = MetaProgression.UnlockCost[id];
        if (Meta.Unlocked.Contains(id) || Meta.Shards < cost) return false;
        Meta.Unlocked.Add(id);
        Meta.Shards -= cost;
        MetaSaver?.Invoke(Meta);
        return true;
    }

    /// <summary>通关后继续深潜：周目 +1，从第 16 层开始</summary>
    public void ContinueDive()
    {
        if (Status != GameStatus.Won) return;
        Loop += 1;
        Status = GameStatus.Playing;
        // 新周目接着往下走一层。这里以前是 `Floor += 1`，而它和 `Descend()` 有一个
        // 不一致的地方：下楼会清掉本层临时加成（重置训练房的苦练、清空符文队列），
        // 「继续深潜」却不清——上周目最后一层的 +15% 会跟着玩家进新周目，
        // 而新地图上再也找不到来源。两处都是「换了一层」，就都该按换层处理。
        Floor += 1;
        Map = GenerateFloor(Floor);
        PlayerIndex = Map.StartIndex;
        Map.Rooms[PlayerIndex].Visited = true;
        _shopCache.Clear();
        ResetFloorBuffs();
        Player.PoisonDyingLeft = PoisonDyingBudget;
    }

    /// <summary>结算经验并记录升级结果，供 UI 演出</summary>
    public List<LevelUpResult> GrantXp(int amount)
    {
        // 升级也要走同一句收口。
        //
        // 血涨的必须正好等于**面板**上限涨的。差额由这里补，而且只由这里补：
        // `Leveling.GainXp` 只改数值（基础生命 +6），不再自己给血加一截。
        //
        // 两边都加的时候，带「脆弱躯壳」（生命 +20%）的玩家升一级：
        // 面板 +7、`Leveling` 加血 +6，收口再补一个「面板差 7」——血直接冲到上限之外。
        // 只留 `Leveling` 那只手时又反过来：面板涨 7、血只涨 6，差的那 1 点永远补不上。
        // 所以口径写成一句等式：**血涨的 == 面板涨的**，差值按「面板涨了多少」与
        // 「它实际给血涨了多少」算，不去猜它内部用了哪个上限。
        var before = Stats.MaxHp;
        var hpBefore = Player.Hp;
        var ups = Systems.Leveling.GainXp(Player, amount, LevelingDef, _data.Skills);
        if (ups.Count > 0)
        {
            var shortfall = (Stats.MaxHp - before) - (Player.Hp - hpBefore);
            if (shortfall > 0) Player.Hp = Math.Min(Stats.MaxHp, Player.Hp + shortfall);
            else if (shortfall < 0) ClampHpToMaxHp();
            BeforeMaxHpGrows(before, 0);
            _pendingLevelUps.AddRange(ups);
        }
        return ups;
    }

    /// <summary>取走并清空最近的升级事件（UI 负责展示，取一次就够）</summary>
    public List<LevelUpResult> TakeLevelUps()
    {
        var ups = _pendingLevelUps.ToList();
        _pendingLevelUps.Clear();
        return ups;
    }

    /// <summary>
    /// 地图上的掉血（事件、祭坛）也可能把玩家放倒。
    /// 以前只有战斗里的掉血会结束这一局：事件扣 12 点血把玩家打到 0 之后，
    /// 他仍然站在地图上继续走，下一次进战斗才「突然死了」。
    /// 返回是否已结束，UI 据此弹结算面板。
    /// </summary>
    public bool CollapseIfDead()
    {
        if (Status != GameStatus.Playing || Player.Hp > 0) return false;
        OnBattleLost();
        return true;
    }

    public void OnBattleLost()
    {
        if (Status != GameStatus.Playing) return;
        Status = GameStatus.Dead;
        // 死亡安慰奖：少量碎片，让「这局白打了」变成「至少攒了点东西」
        GrantShards(_data.Config.Meta.DeathShards);
    }

    /// <summary>
    /// 一场战斗的收尾。**所有**战斗都必须走这里，而且只走一次。
    ///
    /// 起因是一个跑出来的空档：战斗打出 Won 而调用方没接（某一处忘了判）、
    /// 或者打赢之后又 `OnBattleLost`，两种都不会崩、都不会红——
    /// 表现只有两句话可以形容：「这间房打完了但地图上还是敌人」和
    /// 「我明明赢了却弹出结算」。以前 `OnBattleWon` / `OnBattleLost` 谁都能调、
    /// 调几次都行，于是「结算」这件事没有主人。
    ///
    /// 现在它有一个主人：`Encounters` 里那本「哪一类房打完该结什么」的表，
    /// 收尾时按表对一遍。对不上就抛——把一个静默的状态错误变成一次可见的失败。
    /// </summary>
    public void SettleBattle(BattleOutcome outcome, EncounterDef enc)
    {
        var room = CurrentRoom;
        var kind = room.Type switch
        {
            RoomType.Boss => "boss",
            RoomType.Elite => "elite",
            RoomType.Enemy => "enemy",
            _ => throw new InvalidOperationException($"{MapGen.RoomLabel[room.Type]}里不该有战斗，却结算了一场"),
        };
        if (Encounters.SettleFor(kind) != Encounters.Settle.Win)
            throw new InvalidOperationException($"{MapGen.RoomLabel[room.Type]}里没有可结算的胜利");
        switch (Encounters.AfterBattle(outcome))
        {
            case Encounters.Settle.Win:
                OnBattleWon(enc.Def, room.Type == RoomType.Boss, enc.GoldMultiplier, enc.XpMultiplier, enc.Allies.Count + 1);
                return;
            case Encounters.Settle.Lose:
                OnBattleLost();
                return;
            case Encounters.Settle.Flee:
                // 逃跑什么都不结：房间还在，代价在 Escape() 里已经付过。
                // 但这一句必须存在——沉默的 default 分支就是「忘了处理」的同义词。
                return;
        }
    }

    public bool IsGameOver() => Status != GameStatus.Playing;

    // ---- 只给模拟 / 测试用的入口 ----
    //
    // 这两个方法存在的理由只有一个：自检里的「40 局自动游玩」必须能真的死、也能真的通关。
    // 之前那两个结论都是假的——遇到战斗房直接 OnBattleWon（等于假定每场都赢），
    // 所以 40 局一局都没死过；而「理性 23% 通关」在 C# 侧根本没有对应的模拟，
    // 是照着 TS 版的数字抄上来的。一个不会失败的模拟器只能证明「不崩」，证明不了「规则对」。

    /// <summary>治伤：模拟器与测试用。不超过当前面板上限</summary>
    public void HealPlayer(int amount)
    {
        // 先按面板上限收口再回血。
        //
        // 「当前生命不许超过上限」这条规矩以前只写在「减上限」那条路径上
        // （血祭、卸装备都记得夹回来），而「加上限」那一路全靠修完的 **副作用**：
        // 自检里 40 局理性玩家跑到 `生命超过上限: 95/81` —— 全游戏只有两个入口
        // 能让他超过上限，而两个入口的顺序都是「先记账、后夹血」。
        // 夹在这里不解决问题（夹一次就是一次白送的治疗），真正的修法在上限那头，
        // 这里只是不让任何路径把超上限的状态带出去。
        ClampHpToMaxHp();
        if (amount <= 0) return;
        Player.Hp = Math.Min(Stats.MaxHp, Player.Hp + amount);
    }

    /// <summary>
    /// 面板上限变动的唯一收口：生命跟着上限一起走。
    ///
    /// 三条老规矩说的是同一件事，以前分散在升级、升级技能、血祭、卸装备四处：
    ///   · 上限**涨**了，血跟着涨同样的量（不然升一级只看到上限、看不到血）；
    ///   · 上限**降**了，血夹回上限（不然成了无限回血机）；
    ///   · 血跟着涨的那一截是按**面板**算的，不是按基础值算的——英雄勋章（+20）、
    ///     巨龙之心（+44）挂上去之后，泉眼那一档不能按基础值 +6 结算。
    /// 第 40 轮的 bug 就在第三条：泉眼涨的是 `BaseMaxHp`，血加的是 `HpPerStep`，
    /// 而「加了多少面板上限」只有面板知道。所以这里只问一件事：面板上限变了多少。
    /// <para>
    /// 自检读的是这段注释里的那一行字（`Game.cs` 是静态检查的锚点），别改成别的写法。
    /// </para>
    /// </summary>
    public void BeforeMaxHpGrows(int maxHpBefore, int grownByBase)
    {
        var grownByPanel = Stats.MaxHp - maxHpBefore;
        if (grownByPanel > 0) Player.Hp = Math.Min(Stats.MaxHp, Player.Hp + grownByPanel);
        else if (grownByPanel < 0) ClampHpToMaxHp();
        else if (grownByBase > 0) Player.Hp = Math.Min(Stats.MaxHp, Player.Hp + grownByBase);
    }

    /// <summary>
    /// 血不许超过面板上限。所有改上限的路径末尾都调它。
    ///
    /// `Player.Hp` 是裸字段，没有 setter 帮忙收口——这一点很关键：
    /// 要是它自己会把超出的部分吃掉，那「谁把血顶过了头」在赋值之后**就看不见了**，
    /// 判据会被自己的补丁骗过去（这个仓库里已经出现过四次同一形状的坑）。
    /// </summary>
    public void ClampHpToMaxHp()
    {
        var max = Stats.MaxHp;
        if (Player.Hp <= max) return;
        Player.Hp = Math.Max(1, max);
    }

    // ---- 存档恢复：只给 GameStore 的读档用 ----

    /// <summary>
    /// 把存档里的楼层还原出来。
    /// 不走 Descend()：那条路径会重置层内加成、清商店缓存，
    /// 而存档要保留的正是「下楼之后、离开之前」那一刻的状态。
    /// </summary>
    public void RestoreFloor(int floor, int loop, GameStatus status)
    {
        Floor = Math.Max(1, floor);
        Loop = Math.Max(1, loop);
        Status = status;
        Player.PoisonDyingLeft = PoisonDyingBudget;
        Map = GenerateFloor(Floor);
        PlayerIndex = Map.StartIndex;
        Map.Rooms[PlayerIndex].Visited = true;
        _shopCache.Clear();
    }

    /// <summary>
    /// 读档还原本局计数与「本层临时加成」。
    /// 这些字段都是 private set，构造时定死——所以只能在这里成对地补一个入口，
    /// 而不是把它们一个个改成 public。
    /// </summary>
    public void RestoreRunStats(int kills, int goldEarned, int shardsEarned, int battles,
        double trainingAttackPct, string? pendingAffix)
    {
        Kills = Math.Max(0, kills);
        GoldEarned = Math.Max(0, goldEarned);
        ShardsEarned = Math.Max(0, shardsEarned);
        BattlesFought = Math.Max(0, battles);
        TrainingAttackPct = Math.Max(0, trainingAttackPct);
        PendingBonusAffix = pendingAffix;
        Player.Kills = Kills;
    }

    /// <summary>读档还原本层待生效的符文队列（一层可能有两间训练房，不止一条）</summary>
    public void RestorePendingAffixes(IEnumerable<string> affixIds)
    {
        _pendingBonusAffixes = affixIds.Where(id => _data.Affixes.ContainsKey(id)).ToList();
    }

    /// <summary>
    /// 已经领过「首次击败 Boss」奖励的楼层。
    /// 它原本只在内存里：读档后集合是空的，玩家回到同一层的 Boss 房再打一次，
    /// 碎片又发一遍——存档成了刷碎片的工具，元进展当场破功。
    /// </summary>
    public IReadOnlyCollection<int> BeatenBossFloors => _beatenBossFloors;

    /// <summary>读档时还原「哪些层的 Boss 首杀奖励已经发过」</summary>
    public void RestoreBeatenBossFloors(IEnumerable<int> floors)
    {
        _beatenBossFloors.Clear();
        foreach (var f in floors) _beatenBossFloors.Add(f);
    }

    /// <summary>
    /// 下楼前把「这一层 Boss 的首杀奖励」结掉。
    ///
    /// 起因是一个能被顺手绕过的漏发：首杀碎片只在 `OnBattleWon` 里发，
    /// 而 Boss 房**不挡下楼**——`BlocksDescend` 里 Boss 只在「这一层有 Boss 阶梯」时
    /// 才算拦路，而一局只能打完第 15 层那一次才算通关。于是通关之后「继续深潜」、
    /// 往下潜到第 25 层三 Boss 连战的人，如果先下楼再去打别的层，
    /// 第 20 层的逆位 Boss 与第 25 层的三只就永远没人来发那笔碎片。
    ///
    /// 楼下沉是单向的：一旦离开，那一层就再也回不去。所以结算点只能在这里——
    /// 「离开这一层」是这件事唯一还算及时的时刻。
    /// </summary>
    private void SettlePendingFirstClears()
    {
        if (BossStage(Floor) is null) return;
        var index = Map.BossIndex;
        if (index < 0 || index >= Map.Rooms.Count) return;
        if (Map.Rooms[index].Type != RoomType.Boss || !Map.Rooms[index].Cleared) return;
        SettleFirstClear(Floor, BossStage(Floor)!.Id);
    }

    /// <summary>
    /// 首次击败某一层的 Boss：碎片与成就。同一层只发一次。
    /// 收在一处，因为有两个调用方：当场结算的 `OnBattleWon`，以及
    /// 「打下去了但没进 Boss 房」的补发（见 SettlePendingFirstClears）。
    /// 两处各写一遍的话，「只发一次」这条判据会分叉。
    /// </summary>
    private void SettleFirstClear(int floor, string bossId)
    {
        // 先核对这一层配的是不是这只 Boss：数据表里 stage 的 id 与真打的那只对不上时，
        // 这一笔不该发——否则「打了别的怪也领到 Boss 首杀」。
        if (BossStage(floor) is not { } stage) return;
        // 核对「这一层配的是不是这只 Boss」时，连战的 stage.id 要换成队列里实际那几只：
        // 第 25 层写的是一条 `boss_gauntlet` 元数据，真打的那三只各自的 id
        // 在 `bossStages` 里是另外三行。只比 stage.id 的话，连战的首杀永远发不出来。
        var ids = new HashSet<string> { stage.Id };
        if (stage.Mechanic == "boss_gauntlet")
            foreach (var s in _data.BossStages) ids.Add(s.Id);
        if (!ids.Contains(bossId)) return;
        if (!_beatenBossFloors.Add(floor)) return;
        if (bossId == "boss_abyss_reversed") GrantAchievement("reversed_abyss");
        var table = _data.Config.Meta.BossShards;
        var reward = table.TryGetValue(floor.ToString(), out var v) ? v : 0;
        if (reward > 0) GrantShards(reward);
    }

    /// <summary>把房间的探索/清空标记还原（不进 _shopCache 的那部分状态）</summary>
    public void RestoreRoom(int index, bool visited, bool cleared)
    {
        if (index < 0 || index >= Map.Rooms.Count) return;
        Map.Rooms[index].Visited = visited;
        Map.Rooms[index].Cleared = cleared;
    }


    /// <summary>训练房没被清空时也能跳过：UI 需要它报进度</summary>
    public int RemainingBattleRooms => Map.Rooms.Count(r =>
        (r.Type == RoomType.Enemy || r.Type == RoomType.Elite || r.Type == RoomType.Boss) && !r.Cleared);
}
