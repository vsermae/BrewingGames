// 游戏数据加载与校验。
//
// TS 版里 loader.ts 负责 fetch + 缓存 + validate；Godot 里没有 fetch，
// 换成 FileAccess 读 res://Data/game_data.json（导出后打进 pck，路径一样）。
// 校验一条都不能少：配置写错时启动就报错，比玩到一半崩掉好得多。
using System.Text.Json;
using AbyssEcho.Data;

namespace AbyssEcho.Data;

public static class GameDataStore
{
    private const string ResPath = "res://Data/game_data.json";

    private static GameData? _cached;

    /// <summary>读一次并缓存。headless 自检与正常启动走的是同一条路径。</summary>
    public static GameData Load()
    {
        if (_cached is not null) return _cached;
        using var file = Godot.FileAccess.Open(ResPath, Godot.FileAccess.ModeFlags.Read)
            ?? throw new InvalidOperationException($"打不开 {ResPath}：{Godot.FileAccess.GetOpenError()}");
        return Set(file.GetAsText());
    }

    /// <summary>测试可直接注入已解析的 JSON 文本，跳过 FileAccess。</summary>
    public static GameData Set(string json)
    {
        var data = JsonSerializer.Deserialize<GameData>(json, JsonSetup.Options)
            ?? throw new InvalidOperationException("game_data.json 解析结果为空");
        Validate(data);
        _cached = data;
        return data;
    }

    public static GameData Get() => _cached ?? Load();

    /// <summary>测试用：清掉缓存，保证每个用例从干净状态开始。</summary>
    public static void Reset() => _cached = null;

    private static readonly HashSet<string> SkillTypes = new()
    {
        "damage", "heal", "buff", "poison", "drain", "debuff_defense", "debuff_attack", "evade", "stun", "dispel",
    };

    /// <summary>启动即校验数据：非法配置在这里炸，而不是玩到一半。</summary>
    private static void Validate(GameData d)
    {
        var skillIds = d.Skills.Select(s => s.Id).ToHashSet();
        foreach (var m in d.Monsters.Concat(d.Bosses))
        {
            foreach (var s in m.Skills)
            {
                if (!skillIds.Contains(s)) throw new InvalidOperationException($"怪物 {m.Id} 引用了不存在的技能: {s}");
            }
            if (m.MinFloor is { } minFloor && (minFloor < 1)) throw new InvalidOperationException($"怪物 {m.Id} 的 minFloor 无效: {m.MinFloor}");
        }
        foreach (var e in d.Events)
        {
            foreach (var c in e.Choices)
            {
                if (c.Outcomes.Count == 0) throw new InvalidOperationException($"事件 {e.Id} 的选择「{c.Label}」没有结果");
            }
        }

        // 技能：类型必须认识、数值必须能用。漏了 power 时火球会变成 ×0，
        // 一路算到 0 伤害——玩家看到的是一串 0，比启动报错难查得多。
        foreach (var s in d.Skills)
        {
            if (!SkillTypes.Contains(ToSnake(s.Type))) throw new InvalidOperationException($"技能 {s.Id} 的类型非法: {s.Type}");
            if (double.IsNaN(s.Power) || double.IsInfinity(s.Power) || s.Power <= 0) throw new InvalidOperationException($"技能 {s.Id} 的 power 无效: {s.Power}");
            if (s.Cooldown <= 0) throw new InvalidOperationException($"技能 {s.Id} 的冷却无效: {s.Cooldown}");
        }

        // Boss 阶梯：写错楼层会让某些层永远刷不出 Boss，或者多个 Boss 抢同一层
        if (d.BossStages.Count == 0) throw new InvalidOperationException("缺少 bossStages（Boss 阶梯）");
        var stageFloors = new HashSet<int>();
        foreach (var stage in d.BossStages)
        {
            if (stage.Floor <= 0) throw new InvalidOperationException($"Boss 阶梯楼层非法: {stage.Floor}");
            if (!stageFloors.Add(stage.Floor)) throw new InvalidOperationException($"Boss 阶梯楼层重复: {stage.Floor}");
            if (d.Bosses.All(b => b.Id != stage.Id)) throw new InvalidOperationException($"Boss 阶梯引用了不存在的 Boss: {stage.Id}");
            if (stage.Floor % 5 != 0) throw new InvalidOperationException($"Boss 只能落在 5 的倍数层，当前 {stage.Floor}");
        }

        // 楼层机制：每一层都要有机制 id，且这个 id 必须在机制表里。
        // 少一层不会崩，但那一层就会静默退回「没有机制」。
        var bossMechKeys = d.BossMechanics.Keys.Select(k => k).ToHashSet();
        var mechKeys = d.FloorMechanics.Keys.Concat(bossMechKeys).ToHashSet();
        for (var f = 1; f <= d.Config.FinalFloor; f++)
        {
            if (!d.FloorMechanicMap.TryGetValue(f.ToString(), out var id) || string.IsNullOrEmpty(id))
                throw new InvalidOperationException($"第 {f} 层没有配置楼层机制（floorMechanicMap）");
            if (!mechKeys.Contains(id)) throw new InvalidOperationException($"第 {f} 层引用了不存在的楼层机制: {id}");
        }
        foreach (var (id, def) in d.FloorMechanics)
        {
            if (string.IsNullOrEmpty(def.Name)) throw new InvalidOperationException($"楼层机制 {id} 缺少名称");
            if (string.IsNullOrEmpty(def.Desc)) throw new InvalidOperationException($"楼层机制 {id} 缺少说明");
        }

        // Boss 机制：Boss 阶梯引用的机制必须在表里，周期与倍率也要能用
        foreach (var stage in d.BossStages)
        {
            if (string.IsNullOrEmpty(stage.Mechanic)) throw new InvalidOperationException($"Boss 阶梯第 {stage.Floor} 层没有配置 mechanic");
            if (!d.BossMechanics.TryGetValue(stage.Mechanic, out var def))
                throw new InvalidOperationException($"Boss 阶梯引用了不存在的 Boss 机制: {stage.Mechanic}");
            if (def.Every is { } every && every <= 0) throw new InvalidOperationException($"Boss 机制 {stage.Mechanic} 的 every 无效: {def.Every}");
            foreach (var (key, v) in new (string, double?)[] { ("multiplier", def.Multiplier), ("reflect", def.Reflect), ("cooldown", def.Cooldown) })
            {
                if (v is { } value && (double.IsNaN(value) || double.IsInfinity(value) || value <= 0))
                    throw new InvalidOperationException($"Boss 机制 {stage.Mechanic} 的 {key} 无效: {v}");
            }
        }

        // 楼层机制表：每一行都必须指向一个真的在机制表里的 id。
        //
        // 这一条以前没有，而它已经咬过两次：
        //   · 第 25 层写的是 `abyss_pressure`，而 `bossStages[25].mechanic` 是
        //     `boss_gauntlet`（连战的元数据，没有 Setup）——那一层的敌人因此
        //     一个楼层机制都没有，面板上却写着「渊压」；
        //   · 写错一个字母只会静默退回「没有机制」，玩家以为自己走错了层。
        // 这两种都不会崩、也不会红，只会让「这一层有新规则」这句话变成空话。
        foreach (var (floorKey, mechanicId) in d.FloorMechanicMap)
        {
            if (!int.TryParse(floorKey, out var mechFloor) || mechFloor <= 0)
                throw new InvalidOperationException($"楼层机制表的楼层键非法: {floorKey}");
            if (mechanicId == "none") continue;
            if (d.FloorMechanics.ContainsKey(mechanicId)) continue;
            // Boss 层的机制写在 bossStages 上：那里允许出现 Boss 机制的 id
            // （molten_burst / frost_mirror / abyss_domain / boss_gauntlet）
            var stage = d.BossStages.FirstOrDefault(x => x.Floor == mechFloor);
            if (stage is not null && d.BossMechanics.ContainsKey(mechanicId)) continue;
            throw new InvalidOperationException($"楼层机制表第 {mechFloor} 层引用了不存在的机制: {mechanicId}");
        }

        // 遭遇组合：每一层配的组合里，怪物都必须在这一层之前（含）就能出场。
        // 写错了不会崩，只会静默把这一层少掉一组组合（f7_a 就是这样整个被跳过的）——
        // 玩家看到的是「第 7 层只有两种敌人」，而数据里明明写着三种。
        var floorOfMonster = d.Monsters.Concat(d.Bosses)
            .ToDictionary(x => x.Id, x => x.MinFloor ?? 1);
        foreach (var (floorKey, slots) in d.EncounterSlots)
        {
            if (!int.TryParse(floorKey, out var slotFloor) || slotFloor <= 0)
                throw new InvalidOperationException($"遭遇组合的楼层键非法: {floorKey}");
            foreach (var slot in slots)
            {
                if (slot.Monsters.Count == 0) throw new InvalidOperationException($"遭遇组合 {slot.Id} 没有怪物");
                foreach (var id in slot.Monsters)
                {
                    if (!floorOfMonster.TryGetValue(id, out var minFloor))
                        throw new InvalidOperationException($"遭遇组合 {slot.Id} 引用了不存在的怪物: {id}");
                    if (minFloor > slotFloor)
                        throw new InvalidOperationException(
                            $"遭遇组合 {slot.Id} 在第 {slotFloor} 层就放了 {id}（它 minFloor={minFloor}），这组会被整组跳过");
                }
            }
        }

        // 深潜段（finalFloor 之后）也必须有遭遇组合。
        // 顺便把「深潜到第几层」这个数定下来：Boss 阶梯最深的那个楼层。
        // 这一段以前整段没有配 encounterSlots，`PickEncounterSlot` 返回 null 就退化成
        // 「从全池随机抽一只」——第 20 层的仗里能撞见第 1 层的巨鼠。
        // 光靠运行期兜底（Game.PickMonster 的深度窗口）不够：那只是「别抽到太弱的」，
        // 而「这一层该有什么」是数据的事。缺就启动就红，别等玩家潜到 20 层才发现。
        const int gauntletFloor = 25;
        var deepestFloor = d.BossStages.Count > 0 ? d.BossStages.Max(x => x.Floor) : gauntletFloor;
        for (var floor = d.Config.FinalFloor + 1; floor <= deepestFloor; floor++)
        {
            if (!d.EncounterSlots.ContainsKey(floor.ToString()) && d.BossStages.All(x => x.Floor != floor))
                throw new InvalidOperationException(
                    $"第 {floor} 层没有遭遇组合（encounterSlots）：深潜段会退化成「从全池随机抽一只」");
        }

        // 深潜阶梯：第 20 层逆位、第 25 层三连战。
        foreach (var floor in new[] { 20, gauntletFloor })
        {
            if (d.BossStages.All(s => s.Floor != floor)) throw new InvalidOperationException($"缺少第 {floor} 层的深潜 Boss 阶梯");
        }
        if (d.Bosses.All(b => b.Id != "boss_abyss_reversed")) throw new InvalidOperationException("缺少逆位 Boss（boss_abyss_reversed）");
        var gauntletStage = d.BossStages.First(s => s.Floor == gauntletFloor);
        if (gauntletStage.Mechanic != "boss_gauntlet")
            throw new InvalidOperationException($"第 {gauntletFloor} 层应配置 boss_gauntlet 机制，当前 {gauntletStage.Mechanic}");

        if (d.Config.FinalFloor <= 0) throw new InvalidOperationException($"通关层 finalFloor 无效: {d.Config.FinalFloor}");
        if (!stageFloors.Contains(d.Config.FinalFloor)) throw new InvalidOperationException($"finalFloor {d.Config.FinalFloor} 没有对应的 Boss 阶梯");

        foreach (var key in new[] { "cleanse", "curse" })
        {
            // 祭坛两种权重都必须为正：其中一个写成 0，玩家就永远遇不到那一种
            var weight = key == "cleanse" ? d.Config.Altars.Cleanse : d.Config.Altars.Curse;
            if (weight <= 0) throw new InvalidOperationException($"祭坛房 {key} 权重无效: {weight}");
        }
        ValidateItemsAndAffixes(d);
        ValidateRoomsAndMeta(d);
        ValidateCombatConfig(d);
        ValidateLeveling(d);

        if (d.Leveling.Pool.Count == 0) throw new InvalidOperationException("缺少升级技能池 leveling.pool");
    }

    /// <summary>
    /// 道具、品质、词条三张表互相引用，一起校验才有意义。
    /// 这几条在 TS 版 loader.validate 里都有，移植时整段漏了——
    /// 结果是「槽位写错的道具怎么点都装不上」「词条 id 打错装备静默少一条」，
    /// 都是玩到一半才发现的类型。
    /// </summary>
    private static void ValidateItemsAndAffixes(GameData d)
    {
        var qualities = new[] { Quality.White, Quality.Blue, Quality.Purple, Quality.Orange };
        if (d.Qualities.Count == 0) throw new InvalidOperationException("缺少品质表 qualities");
        var prevCount = 0;
        foreach (var q in qualities)
        {
            if (!d.Qualities.TryGetValue(q, out var def)) throw new InvalidOperationException($"品质表缺少 {q}");
            if (def.AffixCount < 0) throw new InvalidOperationException($"品质 {q} 的词条数无效: {def.AffixCount}");
            if (string.IsNullOrEmpty(def.Color)) throw new InvalidOperationException($"品质 {q} 缺少描边色");
            if (string.IsNullOrEmpty(def.Name)) throw new InvalidOperationException($"品质 {q} 缺少名称");
            if (def.AffixCount < prevCount) throw new InvalidOperationException("品质词条数必须按 白<=蓝<=紫<=橙 递增");
            prevCount = def.AffixCount;
        }
        if (d.Qualities[Quality.White].AffixCount != 0)
            throw new InvalidOperationException($"白色品质不该带词条，当前 {d.Qualities[Quality.White].AffixCount}");

        foreach (var (id, a) in d.Affixes)
        {
            if (string.IsNullOrEmpty(a.Name)) throw new InvalidOperationException($"词条 {id} 缺少名称");
            if (string.IsNullOrEmpty(a.Desc)) throw new InvalidOperationException($"词条 {id} 缺少说明");
            if (a.Id != id) throw new InvalidOperationException($"词条 {id} 的 id 字段与键不一致: {a.Id}");
        }

        foreach (var i in d.Items)
        {
            if (string.IsNullOrEmpty(i.Id)) throw new InvalidOperationException("道具缺少 id");
            if (i.Price < 0) throw new InvalidOperationException($"道具 {i.Id} 的价格无效: {i.Price}");
            if (i.Slot == Slot.Consumable)
            {
                // 消耗品要有「用掉之后会发生什么」，否则它只是占背包的一格
                if (i.Heal is null && i.Damage is null && i.Cleanse != true)
                    throw new InvalidOperationException($"消耗品 {i.Id} 既没有回血也没有伤害也不能解毒，用掉等于白扔");
                // 「解毒草」是一次**保险**，不是一瓶小药：价格得对得上它「只在会被毒死的
                // 那一回合有用」这个定位。8 金币与治疗药水同价，而治疗药水在任何回合都有用。
                if (i.Cleanse == true && i.Heal is null && i.Damage is null && i.Price > 6)
                    throw new InvalidOperationException(
                        $"消耗品 {i.Id} 只解毒，价格 {i.Price} 却比一瓶随时能喝的治疗药水还贵：买它的人永远算不出「现在该吃」");
                // 带解毒的消耗品只做一件事：解毒。
                // 它以前还回 18 点血，于是说明写成「立刻解除中毒，并回复 18 点生命值」——
                // 两句真话，拼成一句假话：玩家读到的是「随时可以当小药喝」，
                // 而它真正的场是「这一回合毒会要命，吃它比砍一刀划算」。
                // 自检里那条判据（`antidote.Heal is null`）在数据一侧漏了 40 轮，
                // 因为校验只管「消耗品至少得做一件事」，没有管「解毒草不许顺手做第二件事」。
                if (i.Cleanse == true && (i.Heal is not null || i.Damage is not null))
                    throw new InvalidOperationException(
                        $"消耗品 {i.Id} 既解毒又回血 / 打伤害：它的说明会变成两句真话拼起来的一句假话");
                if (i.Quality is not null) throw new InvalidOperationException($"消耗品 {i.Id} 不该有品质（品质只属于装备）");
                if (i.Affixes is { Count: > 0 }) throw new InvalidOperationException($"消耗品 {i.Id} 不该带词条");
                continue;
            }
            var affixes = i.Affixes ?? new List<string>();
            foreach (var a in affixes)
            {
                if (!d.Affixes.ContainsKey(a)) throw new InvalidOperationException($"装备 {i.Id} 引用了不存在的词条: {a}");
            }
            if (affixes.Distinct().Count() != affixes.Count) throw new InvalidOperationException($"装备 {i.Id} 的词条重复");
            if (i.Quality is { } q && d.Qualities.TryGetValue(q, out var qd) && affixes.Count > qd.AffixCount)
                throw new InvalidOperationException($"装备 {i.Id} 的词条数 {affixes.Count} 超过品质 {q} 的上限 {qd.AffixCount}");
        }
    }

    /// <summary>
    /// 房间配置与元进展：数值写错不会崩，只会「静默失效」——
    /// 训练房回血变 0、精英房保底品质写成白、碎片表漏一层 Boss，
    /// 玩家看到的都是「这个房间/这个 Boss 没有回报」。
    /// </summary>
    private static void ValidateRoomsAndMeta(GameData d)
    {
        var meta = d.Config.Meta;
        if (meta is null) throw new InvalidOperationException("缺少 config.meta（元进展配置）");
        foreach (var stage in d.BossStages)
        {
            // 没配 = 0 是合法的（第 20/25 层本来就不发碎片），配了负数才是错。
            if (!meta.BossShards.TryGetValue(stage.Floor.ToString(), out var shards)) continue;
            if (shards < 0) throw new InvalidOperationException($"第 {stage.Floor} 层 Boss 的碎片奖励无效: {shards}");
        }
        if (meta.ClearShards < 0) throw new InvalidOperationException($"元进展 clearShards 无效: {meta.ClearShards}");
        if (meta.DeathShards < 0) throw new InvalidOperationException($"元进展 deathShards 无效: {meta.DeathShards}");
        if (meta.LoopEnemyScale < 0) throw new InvalidOperationException($"周目敌人数值递增无效: {meta.LoopEnemyScale}");
        if (meta.LoopShopScale < 0) throw new InvalidOperationException($"周目商店价格递增无效: {meta.LoopShopScale}");
        if (meta.LoopCurses.Count == 0) throw new InvalidOperationException("周目负面效果不能为空");
        foreach (var c in meta.LoopCurses)
        {
            if (string.IsNullOrEmpty(c.Name)) throw new InvalidOperationException("周目负面效果缺少名称");
            if (string.IsNullOrEmpty(c.Desc)) throw new InvalidOperationException("周目负面效果缺少说明");
        }

        var rooms = d.Config.Rooms;
        if (rooms is null) throw new InvalidOperationException("缺少 config.rooms（房间配置）");
        var t = rooms.Training;
        if (t.HealPct <= 0 || t.HealPct > 1) throw new InvalidOperationException($"训练房回血比例无效: {t.HealPct}");
        if (t.AttackPct <= 0 || t.AttackPct > 2) throw new InvalidOperationException($"训练房攻击加成无效: {t.AttackPct}");
        if (t.AffixCount < 1) throw new InvalidOperationException($"训练房附魔词条数无效: {t.AffixCount}");

        var curse = rooms.Curse.AffixIds;
        if (curse.Count == 0) throw new InvalidOperationException("诅咒房词条池不能为空");
        if (curse.Distinct().Count() < 2)
            throw new InvalidOperationException($"诅咒房词条池只有 {curse.Distinct().Count()} 种，玩家会每次都看到同一件");
        foreach (var id in curse)
        {
            if (!d.Affixes.TryGetValue(id, out var def)) throw new InvalidOperationException($"诅咒房引用了不存在的词条: {id}");
            if (def.Negative != true) throw new InvalidOperationException($"诅咒房词条 {id} 不是负面词条");
        }

        var elite = rooms.Elite;
        if (elite.ScaleBase <= 0) throw new InvalidOperationException($"精英房属性倍率基础值无效: {elite.ScaleBase}");
        if (elite.ScalePerFloor < 0) throw new InvalidOperationException($"精英房属性倍率的每层增量无效: {elite.ScalePerFloor}");
        // 第 1 层就该约等于普通怪：倍率曲线太陡会让玩家在第二间房就被拍死
        if (elite.ScaleBase + elite.ScalePerFloor > 1.2)
            throw new InvalidOperationException($"精英房第 1 层倍率 {(elite.ScaleBase + elite.ScalePerFloor):F2} 过高，前期劝退");
        if (elite.AffixChance < 0 || elite.AffixChance > 1) throw new InvalidOperationException($"精英房附词条概率无效: {elite.AffixChance}");

        // 房间权重：全 0 会让随机抽取退化成「永远取最后一个」，整层只剩一种房间
        if (!d.RoomTypes.Values.Any(r => r.Weight > 0)) throw new InvalidOperationException("房间权重不能全为 0");
        foreach (var (key, def) in d.RoomTypes)
        {
            if (string.IsNullOrEmpty(def.Symbol)) throw new InvalidOperationException($"房间类型 {key} 缺少符号");
            if (string.IsNullOrEmpty(def.Name)) throw new InvalidOperationException($"房间类型 {key} 缺少名称");
        }
        // 每种房型都必须有可能出现。写成 0 不会崩，只会静默消失——
        // 这一轮的祭坛房就是这么「消失」的：权重写着 10，但保底商店先把补给配额吃光，
        // 抽签一次都轮不到它，450 层里出现 0 次。权重为 0 是显式的「我不要这种房间」，
        // 而那属于门槛（元进展解锁），不该写在数据表里冒充权重。
        foreach (var key in new[] { "empty", "enemy", "treasure", "shop", "event", "altar", "elite" })
        {
            if (!d.RoomTypes.TryGetValue(key, out var def)) throw new InvalidOperationException($"缺少房间类型 {key}");
            if (def.Weight <= 0) throw new InvalidOperationException($"房间类型 {key} 的权重为 {def.Weight}，它永远不会出现");
        }

        // 逆位 Boss：属性必须是原版的 2 倍。写成一个普通数值的话，
        // 「属性为原版 2 倍」这条需求就只存在于文档里，数据里看不出来。
        foreach (var rev in d.Bosses.Where(b => b.Id.EndsWith("_reversed", StringComparison.Ordinal)))
        {
            var originId = rev.Id[..^"_reversed".Length];
            var origin = d.Bosses.FirstOrDefault(b => b.Id == originId);
            if (origin is null) throw new InvalidOperationException($"逆位 Boss {rev.Id} 找不到原版 {originId}");
            if (rev.Hp != origin.Hp * 2) throw new InvalidOperationException($"逆位 Boss {rev.Id} 的 hp 应为原版 2 倍，当前 {rev.Hp} / {origin.Hp}");
            if (rev.Attack != origin.Attack * 2) throw new InvalidOperationException($"逆位 Boss {rev.Id} 的 attack 应为原版 2 倍");
            if (rev.Defense != origin.Defense * 2) throw new InvalidOperationException($"逆位 Boss {rev.Id} 的 defense 应为原版 2 倍");
        }
    }

    /// <summary>战斗与地图的基础数值：写错的后果是「难度静默翻转」，比崩溃更难查</summary>
    private static void ValidateCombatConfig(GameData d)
    {
        var c = d.Config;
        if (c.InventorySize <= 0) throw new InvalidOperationException("背包容量必须为正数");
        if (c.MapWidth < 3 || c.MapHeight < 3) throw new InvalidOperationException("地图至少 3x3");
        if (c.MinRooms > c.MapWidth * c.MapHeight)
            throw new InvalidOperationException($"最小房间数 {c.MinRooms} 超过格子总数 {c.MapWidth * c.MapHeight}");
        foreach (var (key, v) in new (string, double)[]
                 { ("critChance", c.CritChance), ("defendReduction", c.DefendReduction), ("damageVariance", c.DamageVariance) })
        {
            if (double.IsNaN(v) || v < 0 || v > 1) throw new InvalidOperationException($"配置 {key} 必须在 0~1 之间，当前 {v}");
        }
        if (c.CritMultiplier < 1) throw new InvalidOperationException($"暴击倍率必须 ≥ 1，当前 {c.CritMultiplier}");
        // 负数会让深层敌人比浅层还弱；写错时是静默把难度翻转过来
        if (double.IsNaN(c.EnemyGrowth) || c.EnemyGrowth < 0 || c.EnemyGrowth > 0.5)
            throw new InvalidOperationException($"敌人成长率 enemyGrowth 无效: {c.EnemyGrowth}");
        var aiSum = c.EnemyAi.Attack + c.EnemyAi.Defend + c.EnemyAi.Skill;
        if (Math.Abs(aiSum - 1) > 1e-6) throw new InvalidOperationException($"敌人 AI 概率之和必须为 1，当前 {aiSum}");

        var sac = c.Altars.Sacrifice;
        if (sac is null) throw new InvalidOperationException("缺少祭坛血祭的代价配置（config.altars.sacrifice）");
        if (sac.HpPct <= 0 || sac.HpPct > 0.5) throw new InvalidOperationException($"血祭扣除的生命比例无效: {sac.HpPct}");
        // 固定代价的两个数：写错会让血祭要么白送攻击、要么一刀砍死玩家
        if (sac.HpCostMin < 1) throw new InvalidOperationException($"血祭的最小代价必须 ≥ 1，当前 {sac.HpCostMin}");
        if (sac.HpCostMaxPct <= 0 || sac.HpCostMaxPct > 0.2)
            throw new InvalidOperationException($"血祭的代价上限比例必须在 (0, 0.2] 内，当前 {sac.HpCostMaxPct}");
        if (sac.Attack <= 0) throw new InvalidOperationException($"血祭换来的攻击力无效: {sac.Attack}");

        // 泉眼：写错的后果是「攒的钱买不到东西」或者「一次把面板顶穿」，
        // 两种都不会报错，只会让深层忽然变简单或变白忙。
        var off = c.Altars.Offering;
        if (off is null) throw new InvalidOperationException("缺少泉眼的配置（config.altars.offering）");
        if (off.BaseCost <= 0) throw new InvalidOperationException($"泉眼的首价必须 > 0，当前 {off.BaseCost}");
        if (off.CostPerStep < 0) throw new InvalidOperationException($"泉眼的涨价不能为负，当前 {off.CostPerStep}");
        if (off.CostPerFloor < 0) throw new InvalidOperationException($"泉眼的每层加价不能为负，当前 {off.CostPerFloor}");
        // 深层的第一次献金不能贵过一整层的进项：赏金是 12+4×层、一层四五场，
        // 而第 15 层那一笔如果超过它，这个「出口」就变成了永远买不起的摆设——
        // 上一轮量到的「钱没处花」会原样回来，只是这回钱是攒在祭坛门口的。
        var deepestFirst = off.BaseCost + off.CostPerFloor * Math.Max(0, d.Config.FinalFloor - off.MinFloor);
        var deepestIncome = 4 * (12 + d.Config.FinalFloor * 4);
        if (deepestFirst > deepestIncome)
            throw new InvalidOperationException(
                $"泉眼在最终层的第一次献金要 {deepestFirst} 金币，超过该层一整层的进项 {deepestIncome}（又一个买不起的出口）");
        if (off.HpPerStep <= 0 || off.AttackPerStep <= 0)
            throw new InvalidOperationException($"泉眼每次的成长必须为正：hp {off.HpPerStep} / attack {off.AttackPerStep}");
        if (off.MaxSteps < 0 || off.MaxSteps > 12)
            throw new InvalidOperationException($"泉眼的次数上限必须在 0~12，当前 {off.MaxSteps}");
        // 一局换来的上限不能超过升级给的总额：它是「深层的一个出口」，不是第二条升级曲线
        var levelCap = off.MaxSteps * off.HpPerStep;
        if (levelCap > 80) throw new InvalidOperationException($"泉眼一局最多给 {levelCap} 点上限，比升级还给得多");
        if (off.MinFloor < 1 || off.MinFloor > d.Config.FinalFloor)
            throw new InvalidOperationException($"泉眼的起始层 {off.MinFloor} 不在 1~{d.Config.FinalFloor} 之间");
    }

    /// <summary>升级曲线：写错会让升级要么永不发生、要么一次跳满</summary>
    private static void ValidateLeveling(GameData d)
    {
        var lv = d.Leveling;
        if (lv is null) throw new InvalidOperationException("缺少 leveling 段（升级曲线）");
        if (lv.BaseXp <= 0) throw new InvalidOperationException($"升级经验 baseXp 无效: {lv.BaseXp}");
        if (lv.Growth < 1) throw new InvalidOperationException($"升级经验增长倍率 growth 必须 ≥ 1，当前 {lv.Growth}");
        if (lv.HpPerLevel < 0 || lv.AttackPerLevel < 0 || lv.DefensePerLevel < 0)
            throw new InvalidOperationException("升级属性增量不能为负");
        if (lv.DefenseEveryLevels <= 0) throw new InvalidOperationException($"升级防御间隔必须为正整数，当前 {lv.DefenseEveryLevels}");
        var skillIds = d.Skills.Select(s => s.Id).ToHashSet();
        foreach (var id in lv.Pool)
        {
            if (!skillIds.Contains(id)) throw new InvalidOperationException($"升级技能池引用了不存在的技能: {id}");
        }
    }

    private static string ToSnake(SkillType t) => t switch
    {
        SkillType.Damage => "damage",
        SkillType.Heal => "heal",
        SkillType.Buff => "buff",
        SkillType.Poison => "poison",
        SkillType.Drain => "drain",
        SkillType.DebuffDefense => "debuff_defense",
        SkillType.DebuffAttack => "debuff_attack",
        SkillType.Evade => "evade",
        SkillType.Stun => "stun",
        SkillType.Dispel => "dispel",
        _ => "unknown",
    };
}
