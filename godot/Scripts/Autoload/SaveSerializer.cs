// 一局游戏的存档格式。
//
// 为什么是手写而不是序列化整个 Game：Game 里有 Rng、Map、ItemIndex 这些
// 「可由种子与楼层推导出来」的东西，存它们是给未来的迁移埋雷——
// 改一条生成规则，旧档里的地图就和规则对不上了。
// 存档只记「不可推导的玩家状态」，地图用同一颗种子重新生成，其余按标记还原。
//
// 这里只用 ConfigFile/FileAccess（Godot）与纯数据，不碰场景。
namespace AbyssEcho.Autoload;

using Godot;
using AbyssEcho.Core;
using AbyssEcho.Data;
using AbyssEcho.Systems;

public static class SaveSerializer
{
    private const string Section = "run";

    public static bool Exists(string path) => Godot.FileAccess.FileExists(path);

    public static void Save(string path, Game run, uint seed)
    {
        var cfg = new ConfigFile();
        cfg.SetValue(Section, "version", OfflineRules.SaveVersion);
        // 存的是**地图种子**，不是 Rng.Seed：两者之差就是「开局到现在抽了多少次」。
        // 地图由 `Rng.MapRngFor(floor)` 从 (seed, floor) 单独派生，而 Rng 在构造时
        // 已经为了「元进展开局加成」抽过至少一次——拿 Rng.Seed 去重建，读档后
        // 整个随机序列会退回开局（商店换货、掉落重掷），而且第 1 层的地图也会
        // 跟着变样（起点坐标、每一格的房型全不一样）。这条一直是隐形的：
        // 旧断言只比「房间数」和「清空标记」，两者在新图旧图上恰好都对得上。
        cfg.SetValue(Section, "seed", (long)run.MapSeed);
        // 随机状态必须存：只存种子的话读档会把整个随机序列倒回开局，
        // 商店货、掉落、战斗判定全都能被「读档」重抽一遍。
        cfg.SetValue(Section, "rng_state", (long)run.Rng.State);  // 按 64 位写，读的时候也按 64 位取
        cfg.SetValue(Section, "display_seed", seed);
        cfg.SetValue(Section, "floor", run.Floor);
        cfg.SetValue(Section, "loop", run.Loop);
        cfg.SetValue(Section, "status", (int)run.Status);
        cfg.SetValue(Section, "player_index", run.PlayerIndex);
        cfg.SetValue(Section, "kills", run.Kills);
        cfg.SetValue(Section, "gold_earned", run.GoldEarned);
        cfg.SetValue(Section, "shards_earned", run.ShardsEarned);
        cfg.SetValue(Section, "battles_fought", run.BattlesFought);
        // 泉眼献过几次：不落盘的话，关窗重开就能再换一次，一局的上限形同不存在
        cfg.SetValue(Section, "offering_steps", run.OfferingSteps);
        // 这一层「中毒已经毒死过我几次」：不落盘的话，读档就是一次免费的免死金牌——
        // 额度用完之后毒只扣到 1 点，而读一次档额度就满了。和泉眼那一条同一个坑：
        // **任何「一局 / 一层只能发生 N 次」的计数都得跟着状态一起走。**
        cfg.SetValue(Section, "poison_dying_used", run.PoisonDyingUsed);
        // 浮点必须写明类型：ConfigFile 把 double 存成文本、读回来是 Variant 的 float（单精度），
        // 而 `(double)cfg.GetValue(...)` 这种拆箱在 0.1 这类值上是按**写入时那个 double**
        // 装箱、按 float 拆箱——运行期直接 InvalidCastException，整局读不回来。
        // 楼下 reader 用 AsDouble() 接，两边都按 double 走。
        cfg.SetValue(Section, "training_attack_pct", run.TrainingAttackPct);
        // 符文是本层内的队列（一层可能有两间训练房）：只存一条的话，
        // 存档会把第二条静默丢掉，玩家读档后少拿一条词条。
        var affixQueue = new Godot.Collections.Array<string>();
        foreach (var a in run.PendingBonusAffixes) affixQueue.Add(a);
        cfg.SetValue(Section, "pending_affixes", affixQueue);
        // 兼容旧档的读法：v2 写的是单值
        cfg.SetValue(Section, "pending_affix", run.PendingBonusAffix ?? "");
        var usedEvents = new Godot.Collections.Array<string>();
        foreach (var e in run.UsedEvents) usedEvents.Add(e);
        cfg.SetValue(Section, "used_events", usedEvents);
        cfg.SetValue(Section, "gauntlet_index", run.GauntletProgress?.Index ?? 0);
        // 首杀记录：不发的话读档后同一层 Boss 能反复领碎片
        var beaten = new Godot.Collections.Array<int>();
        foreach (var f in run.BeatenBossFloors) beaten.Add(f);
        cfg.SetValue(Section, "beaten_boss_floors", beaten);

        var p = run.Player;
        cfg.SetValue(Section, "hp", p.Hp);
        cfg.SetValue(Section, "base_hp", p.BaseMaxHp);
        cfg.SetValue(Section, "base_attack", p.BaseAttack);
        cfg.SetValue(Section, "base_defense", p.BaseDefense);
        cfg.SetValue(Section, "gold", p.Gold);
        cfg.SetValue(Section, "gold_earned_player", p.GoldEarned);
        cfg.SetValue(Section, "gold_lost", p.GoldLost);
        cfg.SetValue(Section, "level", p.Level);
        cfg.SetValue(Section, "xp", p.Xp);

        cfg.SetValue(Section, "inventory", ToArray(p.Inventory));
        cfg.SetValue(Section, "skills", ToArray(p.Skills.Select(s => s.Id)));
        cfg.SetValue(Section, "equipped", ToArray(new[]
        {
            p.Equipped.Weapon?.ToString() ?? "",
            p.Equipped.Armor?.ToString() ?? "",
            p.Equipped.Trinket?.ToString() ?? "",
        }));
        // 下标之外再记一份「那件东西是什么」：下标只是背包位置，而背包内容会变
        // （数据表删过装备、运行时装备没落盘）。只按下标恢复的话，中间少一格
        // 就让人身上那件装备静默消失或换成旁边那件——存档里明明有它的名字。
        cfg.SetValue(Section, "equipped_ids", ToArray(
            new[] { "weapon", "armor", "trinket" }.Select(slot => Inventory.EquippedDef(p, slot, run.ItemIndex)?.Id ?? "")));

        // 房间标记：只存被清空/被访问过的，没标记的就是默认值——
        // 一张图几十个房间，全存会让存档大一倍且毫无信息量。
        var cleared = new Godot.Collections.Array<int>();
        var visited = new Godot.Collections.Array<int>();
        for (var i = 0; i < run.Map.Rooms.Count; i++)
        {
            if (run.Map.Rooms[i].Cleared) cleared.Add(i);
            if (run.Map.Rooms[i].Visited) visited.Add(i);
        }
        cfg.SetValue(Section, "rooms_cleared", cleared);
        cfg.SetValue(Section, "rooms_visited", visited);

        // 已经 roll 过的商店货架：不存的话读档会用「新的随机位置」重 roll，
        // 玩家读档就能无限换货——存档变成了刷商店的工具（这条是自检里挂了很久的那项）。
        // 只存已经打开过的店（RolledShops），没进过的店读档后照常 roll，不浪费存档体积。
        var shops = new Godot.Collections.Dictionary<string, Godot.Collections.Array>();
        foreach (var (index, stock) in run.RolledShops)
        {
            var arr = new Godot.Collections.Array();
            foreach (var s in stock)
            {
                arr.Add(new Godot.Collections.Dictionary
                {
                    ["id"] = s.Item.Id,
                    ["price"] = s.Price,
                    ["base_price"] = s.BasePrice,
                    ["sold"] = s.Sold,
                });
            }
            shops[index.ToString()] = arr;
        }
        cfg.SetValue(Section, "shops", shops);

        // 运行时生成的装备（诅咒装、附魔装、以及每一件掉落/购买/事件给的副本）：
        // 它们的 id 在数据表里查不到，必须整件存下来，否则读档后背包里会凭空少掉那一格。
        //
        // 只存「这一局真的用得着」的那些（装备槽 + 背包 + 货架），不是整个 ItemIndex：
        // 掉落里被弃掉的那两件、卖完下架的货都还挂在表上，全存进来会让存档
        // 随局内进度线性变胖，而读回来时它们一件也用不上。
        var runtime = new Godot.Collections.Dictionary<string, Godot.Collections.Dictionary>();
        foreach (var id in ReferencedItemIds(run))
        {
            if (!run.ItemIndex.TryGetValue(id, out var def) || !IsRuntimeId(id)) continue;
            runtime[id] = ToDict(def);
        }
        cfg.SetValue(Section, "runtime_items", runtime);

        cfg.Save(path);
    }

    /// <summary>
    /// 运行时装备的判据：id 形如 `<模板>_r<序号>`。
    ///
    /// 以前这里写的是 `id.Contains("_r")` —— 而数据表里真的有 `iron_ring`。
    /// 于是「铁戒指」这种名字里带 `_r` 的**模板**也被当成运行时装备，
    /// 存进档里、读回来时被盖成一个「底板就是它自己」的副本：
    /// 之后每次登记新副本都绕开 `_r1`（因为 id 已被占），而背包里那一件
    /// 已经是 `RestoreRuntimeItem` 塞进去的那份。判据要判的是**后缀**，
    /// 所以按「`_r` + 纯数字」认，序号为空的那种不算。
    /// </summary>
    private static bool IsRuntimeId(string id)
    {
        var at = id.LastIndexOf("_r", StringComparison.Ordinal);
        if (at < 0 || at + 2 >= id.Length) return false;
        for (var i = at + 2; i < id.Length; i++)
        {
            if (!char.IsAsciiDigit(id[i])) return false;
        }
        return true;
    }

    /// <summary>
    /// 这一局真正引用到的道具 id：装备槽、背包、以及已经 roll 过的货架。
    /// 存档只需要这些——运行时装备表里剩下的都是「曾经出现过、现在没人拿着」的。
    /// </summary>
    private static HashSet<string> ReferencedItemIds(Game run)
    {
        var ids = new HashSet<string>(run.Player.Inventory);
        foreach (var slot in Inventory.EquipSlots)
        {
            if (Inventory.EquippedDef(run.Player, slot, run.ItemIndex) is { } def) ids.Add(def.Id);
        }
        foreach (var stock in run.RolledShops.Values)
        {
            foreach (var entry in stock) ids.Add(entry.Item.Id);
        }
        return ids;
    }

    /// <summary>
    /// 只看一眼「能不能继续」：不构造 Game，也不碰数据表。
    /// 主菜单要用它决定按钮状态，每帧读一整局太重了。
    /// </summary>
    public static bool CanResume(string path)
    {
        if (!Exists(path)) return false;
        var cfg = new ConfigFile();
        if (cfg.Load(path) != Error.Ok) return false;
        return OfflineRules.IsVersionSupported((int)cfg.GetValue(Section, "version", 0))
               && OfflineRules.IsResumable((int)cfg.GetValue(Section, "status", 0));
    }

    /// <summary>读档。返回 null 表示「没有可用的存档」——版本不符、坏档、局已结束都算。</summary>
    public static Game? Load(string path, MetaState meta)
    {
        if (!Exists(path)) return null;
        var cfg = new ConfigFile();
        if (cfg.Load(path) != Error.Ok) return null;
        if (!OfflineRules.IsVersionSupported((int)cfg.GetValue(Section, "version", 0))) return null;
        var status = (int)cfg.GetValue(Section, "status", 0);
        if (!OfflineRules.IsResumable(status)) return null;

        // 种子与随机状态都按 64 位存读：ConfigFile 文本里 uint32 的高位会被当成符号位
        var seed = unchecked((uint)cfg.GetValue(Section, "seed", 1L).AsInt64());
        var game = new Game(seed, meta);
        // 恢复随机序列位置：先恢复状态，后面所有「按需 roll」的东西（商店、掉落、战斗判定）
        // 才会接着上次的位置走，而不是从开局重来。
        // 注意：ConfigFile 从文本读回来的是 32 位 Int，而随机状态是 uint32，
        // 高位为 1 的值会变成负数——直接强转会把状态读坏（读档后序列完全变样）。
        // 用 AsInt64 取，再截回 uint。
        if (cfg.HasSectionKey(Section, "rng_state"))
        {
            var rv = cfg.GetValue(Section, "rng_state", 0L);
            game.Rng.State = unchecked((uint)rv.AsInt64());
        }

        // 运行时装备先登记：背包与装备下标接下来要按 id 查它们
        var runtime = cfg.GetValue(Section, "runtime_items", new Godot.Collections.Dictionary()).AsGodotDictionary();
        foreach (var keyVariant in runtime.Keys)
        {
            var key = keyVariant.AsString();
            var def = FromDict(runtime[Variant.From(key)].AsGodotDictionary());
            if (def is null) continue;
            // 走 Restore 而不是直接写字典：它顺手把「运行时装备序号」推到已用过的最大值，
            // 否则读档后生成的第一件运行时装备会重复使用 `_r1`，把背包里那一件的定义覆盖掉。
            game.RestoreRuntimeItem(key, def);
        }


        var p = game.Player;
        p.Hp = (int)cfg.GetValue(Section, "hp", p.Hp);
        p.BaseMaxHp = (int)cfg.GetValue(Section, "base_hp", p.BaseMaxHp);
        p.BaseAttack = (int)cfg.GetValue(Section, "base_attack", p.BaseAttack);
        p.BaseDefense = (int)cfg.GetValue(Section, "base_defense", p.BaseDefense);
        p.Gold = (int)cfg.GetValue(Section, "gold", p.Gold);
        p.GoldEarned = (int)cfg.GetValue(Section, "gold_earned_player", 0);
        p.GoldLost = (int)cfg.GetValue(Section, "gold_lost", 0);
        p.Level = (int)cfg.GetValue(Section, "level", 1);
        p.Xp = (int)cfg.GetValue(Section, "xp", 0);

        // 背包只收「查得到的 id」：数据表改过、或者运行时装备没进存档时，
        // 留一个空壳 id 在背包里会让后面所有的下标都对不上——而装备槽存的正是下标。
        // 以前这里原样收下，Sanitize 只查「下标越界」，于是「中间少一格」这件事
        // 没人管：玩家身上那件装备读档后静默消失（或换成旁边那件）。
        // 这里按顺序重建，并把装备槽指向的「道具 id」一起搬过去，下标重新算。
        var inventory = cfg.GetValue(Section, "inventory", new Godot.Collections.Array<string>()).AsGodotArray<string>();
        var kept = new List<string>();
        var indexMap = new Dictionary<int, int>();
        for (var i = 0; i < inventory.Count; i++)
        {
            var id = inventory[i];
            if (string.IsNullOrEmpty(id) || !game.ItemIndex.ContainsKey(id)) continue;
            indexMap[i] = kept.Count;
            kept.Add(id);
        }
        p.Inventory = kept;

        var skillIds = cfg.GetValue(Section, "skills", new Godot.Collections.Array<string>()).AsGodotArray<string>();
        var data = GameDataStore.Get();
        var skills = skillIds.Select(id => data.Skills.FirstOrDefault(s => s.Id == id)).Where(s => s is not null).Select(s => s!).ToList();
        // 技能表为空（存档被手改坏了）时退回默认三件套，别让玩家读档后一场空手仗
        if (skills.Count == 0) skills = data.Skills.Where(s => s.Id is "fireball" or "heal" or "empower").ToList();
        p.Skills = skills;

        var equipped = cfg.GetValue(Section, "equipped", new Godot.Collections.Array<string>()).AsGodotArray<string>();
        // 装备槽按「存档里的旧下标 → 重建后的新下标」翻译，翻译不过去再用记下的 id 找。
        // 直接沿用旧下标的话，只要前面有任何一格被丢掉，玩家身上那件就换人了；
        // 而旧档（没有 equipped_ids）只能靠下标，所以两条路都要有。
        var equippedIds = cfg.GetValue(Section, "equipped_ids", new Godot.Collections.Array<string>()).AsGodotArray<string>();
        foreach (var (slot, i) in new[] { ("weapon", 0), ("armor", 1), ("trinket", 2) })
        {
            var old = ParseIndex(equipped, i);
            int? index = old is { } o && indexMap.TryGetValue(o, out var now) ? now : null;
            if (index is null && i < equippedIds.Count && equippedIds[i] is { Length: > 0 } want)
            {
                var found = p.Inventory.IndexOf(want);
                if (found >= 0) index = found;
            }
            p.Equipped.Set(slot, index);
        }

        // 存档里的当前生命要按**重建之后**的面板上限收口一次。
        // 手改过的档、或者表里删过一件 +生命的装备，都可能让 hp 高过上限——
        // 那个状态会一路吞掉之后每一次治疗（Math.Min 把它全部吃掉），
        // 玩家只看到「喝药没反应」。
        p.Hp = Math.Min(p.Hp, Inventory.PanelMaxHp(p, game.ItemIndex));

        game.RestoreFloor((int)cfg.GetValue(Section, "floor", 1), (int)cfg.GetValue(Section, "loop", 1), (GameStatus)status);
        // 本局计数与临时加成：不恢复的话读档后击杀数清零、训练房的苦练白练
        game.RestoreRunStats(
            kills: (int)cfg.GetValue(Section, "kills", 0),
            goldEarned: (int)cfg.GetValue(Section, "gold_earned", 0),
            shardsEarned: (int)cfg.GetValue(Section, "shards_earned", 0),
            battles: (int)cfg.GetValue(Section, "battles_fought", 0),
            // AsDouble()：ConfigFile 读回来的是 Variant，直接 `(double)` 拆箱会按 float 判错类型
            // （写进去的是 double，读出来的是 float，运行期抛 InvalidCastException）。
            trainingAttackPct: cfg.GetValue(Section, "training_attack_pct", 0.0).AsDouble(),
            pendingAffix: cfg.GetValue(Section, "pending_affix", "").AsString() is { Length: > 0 } a ? a : null);
        // 队列版优先：老档没有这一项，读出来是空数组，会退回上面那条单值
        var affixList = cfg.GetValue(Section, "pending_affixes", new Godot.Collections.Array<string>())
            .AsGodotArray<string>();
        if (affixList.Count > 0) game.RestorePendingAffixes(affixList);
        foreach (var e in cfg.GetValue(Section, "used_events", new Godot.Collections.Array<string>()).AsGodotArray<string>())
            game.UsedEvents.Add(e);
        // 三 Boss 连战：不恢复进度的读档会让玩家退回第一只 Boss 重打
        game.RestoreGauntletIndex((int)cfg.GetValue(Section, "gauntlet_index", 0));
        game.RestoreOfferingSteps((int)cfg.GetValue(Section, "offering_steps", 0));
        game.RestorePoisonDyingUsed((int)cfg.GetValue(Section, "poison_dying_used", 0));
        game.RestoreBeatenBossFloors(cfg.GetValue(Section, "beaten_boss_floors", new Godot.Collections.Array<int>()).AsGodotArray<int>());
        // 两份标记是「或」不是「谁后写谁算」：以前第二句拿「这张新图上第 i 格现在是不是清空过」
        // 去回填 cleared，而新图上除了起点谁也清空过——于是「清空」被后一句冲掉，
        // 玩家读档后发现打过的宝箱房又能再开一次。
        var clearedRooms = cfg.GetValue(Section, "rooms_cleared", new Godot.Collections.Array<int>()).AsGodotArray<int>().ToHashSet();
        var visitedRooms = cfg.GetValue(Section, "rooms_visited", new Godot.Collections.Array<int>()).AsGodotArray<int>().ToHashSet();
        foreach (var i in visitedRooms.Union(clearedRooms))
            game.RestoreRoom(i, visited: true, cleared: clearedRooms.Contains(i));

        // 货架必须在 RestoreFloor 之后恢复：那一句会新开一层、顺手清空 _shopCache，
        // 先恢复就会被清掉（这个顺序坑踩过一次：存档写了货、读档却还是换了一批）。
        // 同时它也要在运行时装备登记之后——货里的运行时装备按 id 查 ItemIndex。
        var shops = cfg.GetValue(Section, "shops", new Godot.Collections.Dictionary()).AsGodotDictionary();
        foreach (var keyVariant in shops.Keys)
        {
            if (!int.TryParse(keyVariant.AsString(), out var shopIndex)) continue;
            var arr = shops[keyVariant].AsGodotArray();
            var stock = new List<ShopStockItem>();
            foreach (var entryVariant in arr)
            {
                var d = entryVariant.AsGodotDictionary();
                var id = d["id"].AsString();
                // 货架上的东西不一定在模板表里（运行时生成的装备），查不到就跳过；
                // 这是「宁可少一件货也不能崩」的兜底，而不是常态。
                // 货架上的东西不一定在模板表里（运行时生成的装备），查不到就跳过：
                // 「宁可少一件货也不能崩」是兜底，而不是常态。
                if (!game.ItemIndex.TryGetValue(id, out var def)) continue;
                stock.Add(new ShopStockItem
                {
                    Item = def,
                    Price = (int)d["price"],
                    // 老存档没有这一项：退回模板价 + 楼层那 1.5，
                    // 与 ShopRoller.Roll 算出来的量级一致（它只差一次 0.85~1.15 的浮动）。
                    BasePrice = d.ContainsKey("base_price") ? (int)d["base_price"] : Math.Max(1, def.Price + game.Floor * 2),
                    Sold = (bool)d["sold"],
                });
            }
            if (stock.Count > 0) game.RestoreShop(shopIndex, stock);
        }

        game.TeleportForDebug((int)cfg.GetValue(Section, "player_index", game.Map.StartIndex));

        OfflineRules.Sanitize(game);
        return game;
    }

    public static void Delete(string path)
    {
        if (Exists(path)) DirAccess.RemoveAbsolute(ProjectSettings.GlobalizePath(path));
    }

    private static int? ParseIndex(Godot.Collections.Array<string> arr, int i) =>
        i < arr.Count && int.TryParse(arr[i], out var v) && v >= 0 ? v : null;

    private static Godot.Collections.Array<string> ToArray(IEnumerable<string> values)
    {
        var arr = new Godot.Collections.Array<string>();
        foreach (var v in values) arr.Add(v);
        return arr;
    }

    private static Godot.Collections.Dictionary ToDict(ItemDef def)
    {
        var d = new Godot.Collections.Dictionary
        {
            ["name"] = def.Name,
            ["slot"] = (int)def.Slot,
            ["rarity"] = def.Rarity,
            ["price"] = def.Price,
            ["attack"] = def.Attack ?? 0,
            ["defense"] = def.Defense ?? 0,
            ["max_hp"] = def.MaxHp ?? 0,
            ["heal"] = def.Heal ?? 0,
            ["damage"] = def.Damage ?? 0,
            ["cleanse"] = def.Cleanse ?? false,
            ["quality"] = def.Quality is null ? -1 : (int)def.Quality.Value,
            ["desc"] = def.Desc,
            ["affixes"] = ToArray(def.Affixes ?? new List<string>()),
            ["set"] = def.Set ?? "",
        };
        return d;
    }

    private static ItemDef? FromDict(Godot.Collections.Dictionary d)
    {
        if (d is null || d.Count == 0) return null;
        var quality = (int)d["quality"];
        return new ItemDef
        {
            Name = d["name"].AsString(),
            Slot = (Slot)(int)d["slot"],
            Rarity = d["rarity"].AsString(),
            Price = (int)d["price"],
            Attack = NullIfZero((int)d["attack"]),
            Defense = NullIfZero((int)d["defense"]),
            MaxHp = NullIfZero((int)d["max_hp"]),
            Heal = NullIfZero((int)d["heal"]),
            Damage = NullIfZero((int)d["damage"]),
            Cleanse = (bool)d["cleanse"] ? true : null,
            Quality = quality < 0 ? null : (Quality)quality,
            Desc = d["desc"].AsString(),
            Affixes = d["affixes"].AsGodotArray<string>().ToList(),
            Set = string.IsNullOrEmpty(d["set"].AsString()) ? null : d["set"].AsString(),
        };
    }

    private static int? NullIfZero(int v) => v == 0 ? null : v;
}
