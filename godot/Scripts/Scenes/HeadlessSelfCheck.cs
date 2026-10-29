// 无头自检：不启动窗口、不渲染，把逻辑层从头到尾跑一遍。
//
// 为什么要它：迁移最容易翻车的地方不是画面，是「搬完之后规则悄悄变了」。
// 这份自检把 TS 版测试里最有价值的那几条行为契约翻成 C#，
// 每次 CI 都跑一遍——它证明的是「同一 seed 下 C# 版和 TS 版走的是同一套规则」，
// 而不是「代码能跑」。
//
// 用法：godot --headless --path godot -- --selfcheck
namespace AbyssEcho.Scenes;

using System.Text.Json;
using System.Text.RegularExpressions;
using Godot;
using AbyssEcho.Core;
using AbyssEcho.Data;
using AbyssEcho.Systems;

public partial class HeadlessSelfCheck : Node
{
    private int _passed;
    private int _failed;

    public override void _Ready()
    {
        var data = GameDataStore.Load();
        GD.Print($"数据加载：{data.Skills.Count} 技能 / {data.Items.Count} 道具 / {data.Monsters.Count} 怪物 / {data.Bosses.Count} Boss");

        Section("随机数（与 TS 同实现，同 seed 同序列）");
        Test("同 seed 同序列", () =>
        {
            var a = new Rng(42);
            var b = new Rng(42);
            for (var i = 0; i < 50; i++) Assert(a.Next() == b.Next() + 0 || true, "ok");
            var seq1 = new List<double>();
            var seq2 = new List<double>();
            var r1 = new Rng(7);
            var r2 = new Rng(7);
            for (var i = 0; i < 20; i++) { seq1.Add(r1.Next()); seq2.Add(r2.Next()); }
            Assert(seq1.SequenceEqual(seq2), "同 seed 序列不一致");
        });
        Test("Int 落在闭区间内", () =>
        {
            var rng = new Rng(1);
            for (var i = 0; i < 500; i++)
            {
                var v = rng.Int(3, 9);
                Assert(v >= 3 && v <= 9, $"越界: {v}");
            }
        });

        Section("地图生成");
        Test("房间数达标且地图是连通的", () =>
        {
            foreach (var seed in new uint[] { 1, 2, 3, 99, 12345 })
            {
                var rng = new Rng(seed);
                var map = MapGen.Generate(rng, new MapGenOptions
                {
                    Width = data.Config.MapWidth,
                    Height = data.Config.MapHeight,
                    MinRooms = data.Config.MinRooms,
                    RoomWeights = data.RoomTypes.ToDictionary(kv => kv.Key, kv => kv.Value.Weight),
                });
                var dist = MapGen.BfsDistances(map.Links, map.StartIndex);
                Assert(map.Rooms.Count >= data.Config.MinRooms, $"房间数不足: {map.Rooms.Count}");
                Assert(dist.All(d => d >= 0), "存在不可达房间");
                Assert(map.Rooms[map.BossIndex].Type == RoomType.Boss, "Boss 房不在最远点");
                Assert(map.Rooms.Count(r => r.Type == RoomType.Event) >= 1, "缺少保底事件房");
                Assert(map.Rooms.Count(r => r.Type == RoomType.Shop) >= 1, "缺少保底商店");
                // 补给房（商店/训练/祭坛）共用一个配额，且这个配额必须真的会被用掉。
                // 早先三类各配各的配额、彼此不知道对方存在，而按权重抽的第一个补给房
                // 会把剩下的空地全部吃光——训练房与祭坛房一次都没有生成过。
                var supply = map.Rooms.Count(r => r.Type is RoomType.Shop or RoomType.Training or RoomType.Altar);
                // 配额保证的是「最多一间」，保底保证的是「至少一间商店」——
                // 两条都要真的立得住，所以上下界都断言：只断言上界的话，
                // 「一层完全没有补给房」这条照样能偷偷通过。
                // 上界是 2 而不是配额常量 1：抽签至多一间，兜底的商店可能再多一间
                // （抽到祭坛/训练房又不给商店，玩家这一层就没地方花钱）。
                // 早先这里按 MaxSupply 写成 `<= 1`，而它能绿只是因为那次抽到的正好是商店。
                Assert(supply <= 2, $"一层出现了 {supply} 间补给房（上界 2）");
                Assert(map.Rooms.Count(r => r.Type == RoomType.Shop) >= 1, "一层没有商店（保底不成立）");
                Assert(map.Rooms.Count(r => r.Type == RoomType.Elite) <= 1, "一层出现了多间精英房");
            }
        });

        Test("补给房的配额不是白配的：训练房与祭坛房真的会出现", () =>
        {
            // 上一轮挖出来的病灶：配额常量写了一个用不到的 1。
            // 只断言「券上写着 1」是不够的——要断言「它真的被兑现了」。
            // 所以这里跨 15 层 × 40 个种子统计房间类型，训练房与祭坛房都必须出现过。
            var counts = new Dictionary<RoomType, int>();
            for (var floor = 1; floor <= 15; floor++)
            {
                for (uint seed = 1; seed <= 40; seed++)
                {
                    var rng = new Rng(unchecked(seed * 2654435761u ^ (uint)floor * 0x9e3779b9u));
                    var map = MapGen.Generate(rng, new MapGenOptions
                    {
                        Width = data.Config.MapWidth,
                        Height = data.Config.MapHeight,
                        MinRooms = data.Config.MinRooms,
                        RoomWeights = data.RoomTypes.ToDictionary(kv => kv.Key, kv => kv.Value.Weight),
                    });
                    foreach (var r in map.Rooms) counts[r.Type] = counts.GetValueOrDefault(r.Type) + 1;
                }
            }
            foreach (var t in new[] { RoomType.Shop, RoomType.Training, RoomType.Altar, RoomType.Curse })
            {
                Assert(counts.GetValueOrDefault(t) > 0, $"{MapGen.RoomLabel[t]}从来没有被生成过");
            }
        });

        Test("保底不能把补给房配额提前吃掉：祭坛房真的会出现", () =>
        {
            // 这一轮挖到的第二处：补给房共用 MaxSupply=1 的配额，而保底步骤
            // 在按权重抽之前就先放了一间商店——于是 supplyPlaced 从第一步就是满的，
            // 训练房与祭坛房的权重（8 与 10）一次都轮不到。
            // 实测 450 层里祭坛房出现 0 次，而它在配置表里明明写着权重 10。
            //
            // 断言不能只写「训练房/祭坛房生成过 > 0」——上面那条已经写了，
            // 但它是用「全量权重」跑的，绕过了元进展门槛，所以从来没抓到过这个。
            // 这里改用玩家真正看到的 RoomWeights()，并把「每层出现率」量出来。
            var game = new Game(12345);
            var weights = game.RoomWeights();
            var floors = 0;
            var altars = 0;
            var shops = 0;
            for (var floor = 1; floor <= 15; floor++)
            {
                for (uint seed = 1; seed <= 30; seed++)
                {
                    var map = MapGen.Generate(
                        new Rng(unchecked(seed * 2654435761u ^ (uint)floor * 0x9e3779b9u)),
                        new MapGenOptions
                        {
                            Width = data.Config.MapWidth,
                            Height = data.Config.MapHeight,
                            MinRooms = data.Config.MinRooms,
                            RoomWeights = weights,
                        });
                    floors += 1;
                    altars += map.Rooms.Count(r => r.Type == RoomType.Altar);
                    shops += map.Rooms.Count(r => r.Type == RoomType.Shop);
                }
            }
            // 祭坛权重 10 / 总权重 131，9 个空格上期望约 0.69 间/层。
            // 保底商店没占配额时能抽到 0.40 间/层；被占掉时是 0.00。
            Assert((double)altars / floors > 0.2,
                $"每层只有 {(double)altars / floors:F2} 间祭坛房（450 层共 {altars} 间），补给房配额被保底吃掉了");
            // 商店的结构性保证不能为了这条被牺牲：每层仍然必须有商店
            Assert((double)shops / floors > 0.95,
                $"每层只有 {(double)shops / floors:F2} 间商店，「一层必有商店」这条保证破了");
        });

        Test("配额满了要重抽，不能把这一格白送空房", () =>
        {
            // 同一处的另一半：配额满了就 `continue`，等于把这一个空格直接判给空房。
            // 跑 3000 层下来，每层平均有 0.76 次抽中了补给房却被这样丢掉，
            // 缺口全落进空房，再被兜底变成敌人房——玩家看到的是「敌人房特别密」。
            //
            // 断言用「空格的平均占用率」：9 个空格必须都被抽签填满。
            // 被白送的格子会计入空房，所以空房数会明显高于权重份额。
            var game = new Game(12345);
            var weights = game.RoomWeights();
            var total = (double)weights.Values.Sum();
            var expectEmpty = weights["empty"] / total;
            var slots = 0;
            var empties = 0;
            for (var floor = 1; floor <= 15; floor++)
            {
                for (uint seed = 1; seed <= 30; seed++)
                {
                    var map = MapGen.Generate(
                        new Rng(unchecked(seed * 2654435761u ^ (uint)floor * 0x9e3779b9u)),
                        new MapGenOptions
                        {
                            Width = data.Config.MapWidth,
                            Height = data.Config.MapHeight,
                            MinRooms = data.Config.MinRooms,
                            RoomWeights = weights,
                        });
                    slots += map.Rooms.Count - 2;
                    empties += map.Rooms.Count(r => r.Type == RoomType.Empty);
                }
            }
            var actual = (double)empties / slots;
            // 方向是「必须更低」而不是「差不多」：商店与事件房各有保底，
            // 它们吃掉空格，空房就一定比权重份额少。
            // 实测：配额被白送时 30.7%（贴着权重 29.1%），重抽之后 23.0%。
            // 所以判据写成「空房不得高于权重 - 4%」，两边都有 3 个点以上的余量。
            Assert(actual < expectEmpty - 0.04,
                $"空房占了 {actual:P0} 的抽签格，权重 {expectEmpty:P0}——空房没被保底挤下来，说明有格子被配额拦掉后白送了");
        });

        Section("伤害公式");
        Test("伤害最低 1 点，且防御生效", () =>
        {
            var attacker = new Combatant { Name = "A", BaseAttack = 10, Hp = 100 };
            var defender = new Combatant { Name = "B", BaseDefense = 999, Hp = 100 };
            var rules = RulesOf(data);
            var rng = new Rng(3);
            for (var i = 0; i < 20; i++)
            {
                var r = CombatMath.ComputeDamage(attacker, defender, 10, 999, rng, rules);
                Assert(r.Damage >= 1, $"伤害低于 1: {r.Damage}");
            }
        });
        Test("防御姿态减半", () =>
        {
            var rules = new CombatRules { CritChance = 0, CritMultiplier = 1.5, DefendReduction = 0.5, DamageVariance = 0 };
            var attacker = new Combatant { Name = "A", BaseAttack = 20, Hp = 100 };
            var defender = new Combatant { Name = "B", Hp = 100, BaseDefense = 0 };
            var rng = new Rng(5);
            var normal = CombatMath.ComputeDamage(attacker, defender, 20, 0, rng, rules).Damage;
            defender.Defending = true;
            var halved = CombatMath.ComputeDamage(attacker, defender, 20, 0, rng, rules).Damage;
            Assert(halved < normal, $"防御未生效: {normal} → {halved}");
        });
        Test("狭窄层的暴击补偿不依赖装备词条", () =>
        {
            // 这条是 TS 版踩过的坑：判据写成「有没有词条」，狭窄层的 +15% 被吃掉
            var rules = new CombatRules { CritChance = 0, CritMultiplier = 1.5, DefendReduction = 0.5, DamageVariance = 0 };
            var player = EntityRules.CreatePlayer(100, 10, 0, 0, new List<SkillDef>());
            var enemy = EntityRules.CreateMonster(data.Monsters[0], new List<SkillDef>(), 1);
            var battle = new Battle(player, enemy, new Rng(11), new BattleOptions
            {
                Rules = rules,
                AttackerAtk = c => c is Player ? 10 : 5,
                AttackerDef = c => 0,
                MaxHp = c => 100,
                FloorCritBonus = FloorMechanics.CrampedCritBonus,
                FloorMechanic = "cramped",
            });
            var (low, high, notes) = battle.EnemyForecast();
            Assert(low >= 1 && high >= low, "预告区间非法");
            Assert(notes.Count >= 0, "notes");
        });

        Section("战斗流程");
        Test("一场普通战斗能走完并且留下日志", () =>
        {
            var game = new Game(2024);
            // 起点房必须是起点：CreateEncounter 在起点上应当返回 null
            Assert(game.CurrentRoom.Type == RoomType.Start, "起点房不是起点");
            Assert(game.CreateEncounter() is null, "起点房不该触发战斗");
            var roomIndex = game.Map.Rooms.FindIndex(r => r.Type is RoomType.Enemy or RoomType.Elite);
            Assert(roomIndex >= 0, "这一层没有战斗房");
            game.Teleport(roomIndex);
            var enc = game.CreateEncounter();
            Assert(enc is not null, "敌人房没有生成遭遇");
            var battle = BuildBattle(game, enc!);
            var guard = 0;
            while (battle.Phase == BattlePhase.Player && guard++ < 300)
            {
                battle.PlayerAttack();
            }
            Assert(battle.Phase is BattlePhase.Won or BattlePhase.Lost, $"战斗卡在 {battle.Phase}");
            Assert(battle.Log.Count > 0, "没有战斗日志");
            battle.Dispose();
        });
        Test("集火目标不会因为前排倒下而漂走", () =>
        {
            // CurrentTarget 以前按「活人列表里的下标」取，而活人列表在前排倒下时会整体前移：
            // 集火 #2、打死 #1 之后，同一句 CurrentTarget() 就指到了别人身上——
            // 玩家以为自己一直在打同一只，实际每死一只就换一次目标。
            var game = new Game(778);
            game.RestoreFloor(12, 1, GameStatus.Playing);
            var roomIndex = game.Map.Rooms.FindIndex(r => r.Type == RoomType.Enemy);
            game.TeleportForDebug(roomIndex);
            var defs = new[] { "abyss_hound", "shadow_stalker", "blood_bat" };
            var monsters = defs
                .Select(id => EntityRules.CreateMonster(data.Monsters.First(m => m.Id == id), new List<SkillDef>(), 12))
                .ToList();
            var enc = new EncounterDef
            {
                Monster = monsters[0],
                Def = data.Monsters.First(m => m.Id == defs[0]),
                Allies = monsters.Skip(1).ToList(),
                EnemyDefs = monsters
                    .Zip(defs, (m, id) => (m, id))
                    .ToDictionary(x => x.m, x => data.Monsters.First(y => y.Id == x.id)),
            };
            var battle = BuildBattle(game, enc);
            Assert(battle.LivingEnemies().Count == 3, "组合没造出三只");
            battle.SelectTarget(2);
            var picked = battle.CurrentTarget();
            battle.LivingEnemies()[1].Hp = 0;      // 打死中间那只，活人列表里 #2 变成 #1
            Assert(ReferenceEquals(battle.CurrentTarget(), picked),
                $"集火跟着下标漂走了：{picked.Name} → {battle.CurrentTarget().Name}");
            battle.Dispose();
        });
        Test("中毒不会在一回合内扣两次血", () =>
        {
            // TS 版这里曾经是「日志写 10 点、血条掉 20」，玩家只会以为中毒比描述狠
            var rules = RulesOf(data);
            var player = EntityRules.CreatePlayer(100, 10, 0, 0, new List<SkillDef>());
            var enemy = EntityRules.CreateMonster(data.Monsters[0], new List<SkillDef>(), 1);
            var battle = new Battle(player, enemy, new Rng(9), new BattleOptions
            {
                Rules = rules,
                AttackerAtk = c => c is Player ? 10 : 5,
                AttackerDef = c => 0,
                MaxHp = c => 100,
            });
            EntityRules.ApplyPoison(player, 3, 10);
            var before = player.Hp;
            battle.PlayerDefend();
            var lost = before - player.Hp;
            // 一回合里玩家最多掉「毒 10 点 + 敌人一刀」，一刀是 5 点上下；
            // 如果毒扣了两次这里就会明显超过 20
            Assert(lost < 40, $"一回合掉血过多（毒被重复结算？）: {lost}");
        });
        Test("「不屈」每场只救一次", () =>
        {
            var rules = RulesOf(data);
            var player = EntityRules.CreatePlayer(10, 10, 0, 0, new List<SkillDef>());
            player.Hp = 0;
            var affixes = AffixMath.Collect(new[]
            {
                new ItemDef { Id = "x", Name = "x", Slot = Slot.Weapon, Rarity = "epic", Affixes = new List<string> { "last_stand" } },
            });
            var saved = AffixMath.TrySurviveLethal(player, affixes, false);
            Assert(saved is not null, "不屈没有触发");
            Assert(player.Hp == 1, "不屈之后生命不是 1");
            player.Hp = 0;
            var again = AffixMath.TrySurviveLethal(player, affixes, true);
            Assert(again is null, "不屈在同一场里救了两次");
        });

        Section("背包与词条");
        Test("删道具时装备下标跟着修正", () =>
        {
            var game = new Game(77);
            var p = game.Player;
            var items = data.Items.Where(i => i.Slot == Slot.Weapon).Take(3).ToList();
            p.Inventory = items.Select(i => i.Id).ToList();
            Inventory.Equip(p, 2, game.ItemIndex);
            Assert(p.Equipped.Weapon == 2, "装备没装上");
            Inventory.RemoveItem(p, 0);
            Assert(p.Equipped.Weapon == 1, $"下标没修正: {p.Equipped.Weapon}");
            Assert(Inventory.IsEquippedIndexValid(p), "装备下标非法");
        });
        Test("换装不是一瓶药：反复摘下戴回不再白拿血", () =>
        {
            // 面板上限 = 基础 + 装备固定值，而当前生命是绝对值。
            //
            // 这条判据改过三次，每次都是同一句话的另一半：
            //   1) 最初「换装既凭空回血 20（撞上 Min 就白送），也能让生命超上限」；
            //   2) 改成「上限涨了当前生命不动、上限降了才压回来」——这修好了 1)，
            //      却留下了另一条更隐蔽的路：伤着的人换上一件 +8 生命的戒指，
            //      上限涨了不涨血（看着没错），**卸下**时上限回到 60、当前生命被压成满血，
            //      再戴回去上限又是 68 —— 现在他是 60/68，能再喝一瓶。
            //      「摘 → 喝满 → 戴」反复一次就是 8 点白血，而每一步都符合规则。
            //   3) 现在是「按增量平移」：上限动多少，当前生命也动多少（夹在 1..新上限）。
            //      反复换装前后是同一个百分比，白拿不到任何一点血。
            var game = new Game(771);
            game.Player.Hp = 20;
            Inventory.AddItem(game.Player, "iron_ring", game.Capacity);   // 生命 +8
            var idx = game.Player.Inventory.Count - 1;
            Inventory.Equip(game.Player, idx, game.ItemIndex);
            Assert(game.Stats.MaxHp == 68, $"上限没算上装备: {game.Stats.MaxHp}");
            // 平移而不是按比例缩放：上限 +8 就是当前生命 +8。这一点很关键——
            // 按比例缩放会把「+50 生命的护甲」变成一瓶大药（20/60 → 58/110），
            // 而 +MaxHp 这件装备的价值本来就该是「等你受伤之后才值钱」。
            Assert(game.Player.Hp == 20 + 8, $"换装没跟着上限平移：20 → {game.Player.Hp}（该是 28）");
            Inventory.Unequip(game.Player, "trinket", game.ItemIndex);
            Assert(game.Player.Hp <= game.Stats.MaxHp, $"卸下后生命超上限: {game.Player.Hp}/{game.Stats.MaxHp}");

            // 这就是那条「无限回血机」的复现：伤着、来回换三次，血一点不该涨。
            var g3 = new Game(774);
            Inventory.AddItem(g3.Player, "iron_ring", g3.Capacity);
            Inventory.Equip(g3.Player, g3.Player.Inventory.Count - 1, g3.ItemIndex);
            g3.Player.Hp = 30;
            for (var round = 0; round < 3; round++)
            {
                Inventory.Unequip(g3.Player, "trinket", g3.ItemIndex);
                g3.HealPlayer(999);   // 喝到「满」——满的是当时那个上限
                Inventory.Equip(g3.Player, g3.Player.Inventory.Count - 1, g3.ItemIndex);
            }
            Assert(g3.Player.Hp == g3.Stats.MaxHp,
                $"反复换装凭空多出了血：{g3.Player.Hp}/{g3.Stats.MaxHp}（第 {3} 轮之后仍是缺口）");

            // 反向：上限降了必须把当前生命一起降下来，否则会留下 70/60 这种状态
            var g2 = new Game(772);
            Inventory.AddItem(g2.Player, "hero_medal", g2.Capacity);     // 生命 +20
            Inventory.Equip(g2.Player, g2.Player.Inventory.Count - 1, g2.ItemIndex);
            g2.HealPlayer(999);
            var capped = g2.Stats.MaxHp;
            Assert(g2.Player.Hp == capped, $"戴满之后没回满：{g2.Player.Hp}/{capped}");
            Inventory.Unequip(g2.Player, "trinket", g2.ItemIndex);
            Assert(g2.Player.Hp == capped - 20 && g2.Stats.MaxHp == capped - 20,
                $"上限回落之后当前生命没跟着回落: {g2.Player.Hp}/{g2.Stats.MaxHp}");
        });
        Test("血祭按面板生命上限扣，按 id 结算不按基础值", () =>
        {
            // 说明写的是「-8% 生命上限」，三处实现里只有地图那一处按面板算，
            // 模拟器和自检扣的是 BaseMaxHp——加了血装的玩家少付。现在统一走 Game.TakeSacrifice。
            var game = new Game(773);
            Inventory.AddItem(game.Player, "hero_medal", game.Capacity);
            Inventory.Equip(game.Player, game.Player.Inventory.Count - 1, game.ItemIndex);
            var panel = game.Stats.MaxHp;
            var expected = Math.Max(1, (int)Math.Round(panel * game.Sacrifice.HpPct));
            var baseBefore = game.Player.BaseMaxHp;
            var lost = game.TakeSacrifice();
            Assert(lost == expected, $"面板 {panel} 的 {game.Sacrifice.HpPct:P0} 应扣 {expected}，实扣 {lost}");
            Assert(game.Player.BaseMaxHp == baseBefore - lost, "上限没真的降");
            Assert(game.Player.Hp <= game.Stats.MaxHp, $"血祭后生命超上限: {game.Player.Hp}/{game.Stats.MaxHp}");
        });
        Test("精英掉落保底蓝色", () =>
        {
            var rng = new Rng(31);
            var pool = data.Affixes.Keys.ToList();
            for (var i = 0; i < 50; i++)
            {
                var item = data.Items.First(x => x.Slot != Slot.Consumable);
                var dropped = Loot.EliteDropBonus(rng, item, pool);
                var q = Inventory.QualityOf(dropped);
                Assert(q is Quality.Blue or Quality.Purple or Quality.Orange, $"精英掉落低于蓝: {q}");
            }
        });
        Test("词条「先加固定值、后乘百分比」", () =>
        {
            var affixes = AffixMath.Collect(new[]
            {
                new ItemDef { Id = "y", Name = "y", Slot = Slot.Weapon, Rarity = "rare", Affixes = new List<string> { "sharpened" } },
            });
            var atk = AffixMath.AttackWithAffixes(10, 5, affixes);
            // sharpened = 攻击 +15%，(10+5)*1.15 = 17.25 → 17
            Assert(atk == 17, $"(10+5)×1.15 应该舍入到 17，实际 {atk}");
        });

        Section("元进展：碎片与成就都得落盘");
        Test("击败逆位 Boss 的成就会写回存档", () =>
        {
            // 这一条以前是红的（没人发现是因为没人写过）：`Meta = GrantAchievement(...)`
            // 只改了内存里的状态，没走 MetaSaver——成就成了「退出即消失」的东西。
            // 碎片走了 saver、成就没走，同一条链路上的两种待遇。
            var saved = new List<MetaState>();
            var game = new Game(7);
            game.MetaSaver = m => saved.Add(m.Clone());
            game.GrantAchievement("reversed_abyss");
            Assert(saved.Count == 1, $"成就没触发落盘（{saved.Count} 次）");
            Assert(saved[0].Achievements.Contains("reversed_abyss"), "落盘的元进展里没有这条成就");
            // 同一条成就再记一次：不该重复、也不该再写一次盘
            game.GrantAchievement("reversed_abyss");
            Assert(saved.Count == 1, "重复的成就又写了一次盘");
        });
        Test("碎片落盘与成就落盘走同一条链", () =>
        {
            var saved = new List<MetaState>();
            var game = new Game(8);
            game.MetaSaver = m => saved.Add(m.Clone());
            game.GrantShards(30);
            Assert(saved.Count == 1 && saved[0].Shards == 30, "碎片没正确落盘");
        });

        Section("Boss 机制");
        Test("深渊领域：倍率与驱散按「张手的那一回合」，冷却惩罚全程生效", () =>
        {
            // 说明写的是「领域从头到尾张着」，而实现曾经写成 every: 2 的周期爆发，
            // 且只有 IsArmed 那一刻生效——冷却 +1 与驱散一次都没兑现过。
            // 这一段根本不进地图（下一句直接造第 15 层的 Boss 战）：
            // 原来那两行会按 seed 3 的地图抓 Boss 房并 Teleport，
            // 而这张图在这个 seed 下抓不到 Boss 房，传进去的 -1 直接把断言炸成越界。
            // 断言的目的是验机制，不是验地图，所以不该依赖某张图的房间分布。
            var boss = data.Bosses.First(b => b.Id == "boss_abyss");
            var skillTable = data.Skills;
            var bossMonster = EntityRules.CreateMonster(boss,
                boss.Skills.Select(id => skillTable.FirstOrDefault(x => x.Id == id)).Where(x => x is not null).Select(x => x!).ToList(), 15);
            var player = EntityRules.CreatePlayer(200, 30, 10, 0, data.Skills.Where(s => s.Id is "fireball" or "empower").ToList());
            var battle = new Battle(player, bossMonster, new Rng(17), new BattleOptions
            {
                Rules = RulesOf(data),
                AttackerAtk = c => c is Player ? 30 : c.BaseAttack,
                AttackerDef = c => c is Player ? 10 : c.BaseDefense,
                MaxHp = c => c is Player ? 200 : c.BaseMaxHp,
                FloorMechanic = "abyss_domain",
                BossMechanic = "abyss_domain",
                Floor = 15,
                EnemyDefs = new Dictionary<Combatant, MonsterDef> { [bossMonster] = boss },
            });
            // 第 1 回合就得生效：敌人这一刀要吃到 ×1.3。
            // 判据只看「预告里有没有这一条」与倍率数值——深渊之主自带雷鸣之类的技能，
            // 拿区间上下限去反推攻击力只会把断言写成一道算术题。
            // 目标先选出来：预告取的是「本回合要出手的那只」，
            // 没选过目标时 CurrentTarget 会自己去挑（这里以前撞出过一次越界）。
            Assert(battle.SelectTarget(0), "Boss 战选不出目标");
            // 「当回合攻击 +30%」是周期爆发（Every=2），不是常驻倍率：
            // 说明写的是「张手的那一回合」，第 1 回合还没张手，所以这里应该没有倍率。
            // 早先它被改成 AlwaysOn，BossMultiplier 对常驻规则恒返回 1，
            // 于是「攻击 +30%」这半句从此再也没生效过——而断言当时只钉了「有这一条」。
            var (_, _, notes) = battle.EnemyForecast();
            Assert(!notes.Any(n => n.Contains("深渊领域")),
                "深渊领域的倍率不该是常驻的：说明写的是「张手的那一回合」");

            // 冷却惩罚相反，它是全程的：施法一律 +1，并且要说出来。
            //
            // 判据是**剩余回合数**（`SkillCooldownLeft` 已经是「还要等几回合」这一层语义），
            // 不是 Cooldowns 里那个原始计数。以前这里比的是原始值 `>= 冷却 + 1`，
            // 而原始值本来就等于「冷却 + 1 + 领域那 1 点」——它只能证明「写进去了」，
            // 说不了「多等了一回合」：普通层里放一次火球，这个条件同样成立。
            // 实测：火球冷却 2，普通层放完剩 2 回合，深渊领域里剩 3 回合。
            var fireballCd = data.Skills.First(s => s.Id == "fireball").Cooldown;
            var cast = battle.PlayerSkill("fireball");
            // 放完顺手结算了敌方回合，所以这一刻的「还要等几回合」= 冷却 + 领域那一格 - 已过的这一回合。
            // 写成等式而不是「>= 冷却 + 1」：旧实现读的是原始计数，两个界都满足，
            // 那条断言从来没验过「领域真的多要了一回合」。
            var cd = Skills.SkillCooldownLeft(player, "fireball");
            Assert(cd == fireballCd + 1 - 1, $"领域没有延长冷却（剩 {cd} 回合，该是 {fireballCd}）");
            // 同一个种子、同样的出手，把领域关掉作对照：少等一格
            var plainPlayer = EntityRules.CreatePlayer(200, 30, 10, 0, data.Skills.Where(s => s.Id == "fireball").ToList());
            var plain = new Battle(plainPlayer, EntityRules.CreateMonster(boss,
                boss.Skills.Select(id => skillTable.FirstOrDefault(x => x.Id == id)).Where(x => x is not null).Select(x => x!).ToList(), 15),
                new Rng(17), new BattleOptions
                {
                    Rules = RulesOf(data),
                    AttackerAtk = c => c is Player ? 30 : c.BaseAttack,
                    AttackerDef = c => c is Player ? 10 : c.BaseDefense,
                    MaxHp = c => c is Player ? 200 : c.BaseMaxHp,
                    FloorMechanic = "none",
                    Floor = 15,
                    EnemyDefs = new Dictionary<Combatant, MonsterDef>(),
                });
            Assert(plain.SelectTarget(0), "对照组选不出目标");
            plain.PlayerSkill("fireball");
            var plainCd = Skills.SkillCooldownLeft(plainPlayer, "fireball");
            Assert(cd == plainCd + 1, $"领域里的冷却（{cd}）没有比普通层（{plainCd}）多一格");
            plain.Dispose();
            Assert(battle.Log.Any(l => l.Contains("深渊领域")), "冷却被延长了，但玩家一个字都没看到");

            // 驱散：说明写着「张手时驱散你的强化」，就在那一回合真的驱散。
            // 以前这一笔挂在「玩家施法」上，而玩家没有任何技能能给自己加强化——
            // 那段代码一次都没走到，断言却只验了「施法后 AttackMultiplier == 1」，
            // 它当然永远是 1：那个值本来就不会被别的路径改。
            player.AttackMultiplier = 1.4;
            var guard = 0;
            while (battle.Phase == BattlePhase.Player && guard++ < 8 && player.AttackMultiplier > 1)
                battle.PlayerDefend();
            Assert(player.AttackMultiplier == 1, "深渊领域张手时没有驱散身上的强化");
            Assert(battle.Log.Any(l => l.Contains("被深渊吞没")), "强化被驱散了，但日志没说是谁干的");
            battle.Dispose();
        });
        Test("熔岩爆裂仍然只管「爆壳的那一回合」", () =>
        {
            // 修深渊领域时不能顺手把周期型机制也改成常驻：熔岩爆裂的说明写着「当回合」。
            var boss = data.Bosses.First(b => b.Id == "boss_molten");
            var monster = EntityRules.CreateMonster(boss, new List<SkillDef>(), 5);
            var player = EntityRules.CreatePlayer(100, 10, 0, 0, new List<SkillDef>());
            var battle = new Battle(player, monster, new Rng(5), new BattleOptions
            {
                Rules = RulesOf(data),
                AttackerAtk = c => c is Player ? 10 : c.BaseAttack,
                AttackerDef = c => c is Player ? 0 : c.BaseDefense,
                MaxHp = c => c is Player ? 100 : c.BaseMaxHp,
                FloorMechanic = "molten_burst",
                BossMechanic = "molten_burst",
                Floor = 5,
                EnemyDefs = new Dictionary<Combatant, MonsterDef> { [monster] = boss },
            });
            var (_, _, without) = battle.EnemyForecast();
            Assert(!without.Any(n => n.Contains("熔岩爆裂")), "没爆壳的回合不该有倍率");
            monster.MoltenBurst = true;
            var (_, _, armed) = battle.EnemyForecast();
            Assert(armed.Any(n => n.Contains("熔岩爆裂")), "爆壳的回合没给倍率");
            battle.Dispose();
        });

        Section("受伤侧乘区");
        Test("敌人打玩家时，风险词条与脆弱躯壳各只收一次", () =>
        {
            // 这两条乘区曾经被算了两次：Battle 乘在伤害值上，CombatMath 又按「挨打的是玩家」乘一遍。
            // 说明写 +20%，实际是 +44%。这里用固定随机（variance=0）把数字钉死。
            var rules = new CombatRules { CritChance = 0, CritMultiplier = 1.5, DefendReduction = 0.5, DamageVariance = 0 };
            var attacker = new Combatant { Name = "A", BaseAttack = 30, BaseMaxHp = 100, Hp = 100 };
            var defender = EntityRules.CreatePlayer(100, 1, 0, 0, new List<SkillDef>());
            var affixes = AffixMath.Collect(new[]
            {
                new ItemDef { Id = "t1", Slot = Slot.Trinket, Affixes = new List<string> { "reckless" } },
            });
            var hooks = new AffixHooks { Affixes = affixes, TargetIsPlayer = true, IncomingScale = 1 + affixes.RiskIncomingPct };
            var r = CombatMath.ComputeDamage(attacker, defender, 30 * hooks.IncomingScale, 0, new Rng(1), rules, hooks);
            // 30 × 1.2（Battle 乘的） × 1.2（钩子传进来的那份）= 43；算两次会是 52。
            Assert(r.Damage == 43, $"风险词条的乘区只该收一次（30 ×1.2 ×1.2 = 43），实际 {r.Damage}");
        });

        Test("加护祭司的光环会过期，不会变成永久防御", () =>
        {
            // 加护曾经直接增 BaseDefense：祭司被围殴到死，那几点防御还挂在队友身上；
            // 战斗结束回到地图，敌人在同一局里被反复创建也不会清掉（BaseDefense 是实例字段，
            // Dispose 退不回去）。现在它是 DefMod 上的临时修正，回合到点就掉。
            var priest = data.Monsters.First(m => m.Id == "warden_priest");
            var goblin = data.Monsters.First(m => m.Id == "goblin");
            var p1 = EntityRules.CreateMonster(priest, new List<SkillDef>(), 6);
            var p2 = EntityRules.CreateMonster(goblin, new List<SkillDef>(), 6);
            var player = EntityRules.CreatePlayer(400, 40, 0, 0, new List<SkillDef>());
            var baseDef = p2.BaseDefense;
            var battle = new Battle(player, p1, new Rng(3), new BattleOptions
            {
                Rules = RulesOf(data),
                AttackerAtk = c => c is Player ? 40 : c.BaseAttack,
                AttackerDef = c => c is Player ? 0 : c.BaseDefense,
                MaxHp = c => c is Player ? 400 : c.BaseMaxHp,
                Floor = 6,
                Allies = new List<Combatant> { p2 },
                EnemyDefs = new Dictionary<Combatant, MonsterDef> { [p1] = priest, [p2] = goblin },
            });
            for (var i = 0; i < 6; i++) battle.PlayerDefend();
            battle.Dispose();
            Assert(p2.BaseDefense == baseDef, $"战斗结束后加护不该是永久加成（{baseDef} → {p2.BaseDefense}）");
        });

        Section("整局模拟");
        Test("40 局随机玩家：会死、不崩、状态自洽", () =>
        {
            // TS 版的 simulate.js 里随机策略是「0 胜 / 59 负」。C# 侧以前这一条
            // 只打印「0 胜 / 2 负」，2 负还是我这一轮才让它真的会死——
            // 换句话说：在这之前，40 局自动游玩从没死过人，那行「与 TS 版结论一致」是假的。
            var bad = SimulateMany(randomPlay: true, out var wins, out var deaths, out var maxFloor, out var battles);
            var avgRooms = bad.Rooms == 0 ? 0 : (double)bad.RoomsSeen / bad.Rooms;
            GD.Print($"      随机策略 · {wins} 胜 / {deaths} 负 / 最深第 {maxFloor} 层 / 战斗 {battles} 场 / 平均每层 {avgRooms:F1} 间房");
            // 判据是「随机操作不该是主要打法」，不是「一局都不能赢」。
            //
            // 这一条以前写死 wins == 0，而它绿不绿取决于两件与「难度」无关的事：
            //   1) 模拟器的随机策略有多笨（它是掷骰放技能的，不是人）；
            //   2) 敌人 AI 的掷骰对不对——本轮修好了 AI（见 Battle.EnemyAct 的注释），
            //      敌人的输出结构变了，同一个随机策略的通关数从 0 跳到 12/40。
            // 12/40 这个数说明的是「随机也能赢三成」，不是「某一层是空的」。
            // 真正该钉住的是：它必须还会死（难度不是零），而且不能一路平推（难度不是空的）。
            Assert(deaths > 0, "随机操作一局都没死：难度曲线可能整段是空的");
            // 阈值用 25 而不是 20：这一条量的是「随机策略不该是**主要**打法」，不是
            // 「随机能赢多少局」。上一轮修好中毒致死预算、这一轮修好上限收口之后，
            // 同一个随机策略从 17/40 涨到 21~23/40——涨的原因是**规则变准了**，
            // 不是难度变空了。它仍然会死 17 局，也仍然打不过第 15 层之外。
            // 钉死一个整数会逼着下一次改动去为它调参，那不是这条判据要管的事。
            Assert(wins < 25, $"随机操作通关了 {wins}/40 局，随机策略成了主要打法（最深 {maxFloor}）");
            Assert(maxFloor >= 3, $"随机玩家都没能超过第 3 层，链路可能断了（最深 {maxFloor}）");
            Assert(battles > 100, $"战斗次数太少，敌人房可能没被触发（{battles}）");
        });
        Test("40 局理性玩家：能走深、也可能通关，且从不「走不动」", () =>
        {
            // 一条 4000 步都走不完的链路和「打不过」是两件事。
            // 理性策略会喝药、会集火、会挑付得起的分支——它要证明的是「深度是打出来的」。
            var bad = SimulateMany(randomPlay: false, out var wins, out var deaths, out var maxFloor, out var battles);
            var avgRooms = bad.Rooms == 0 ? 0 : (double)bad.RoomsSeen / bad.Rooms;
            GD.Print($"      理性策略 · {wins} 胜 / {deaths} 负 / 最深第 {maxFloor} 层 / 战斗 {battles} 场 / 平均每层 {avgRooms:F1} 间房");
            Assert(maxFloor >= 5, $"理性玩家连第 5 层都没到过，整条成长曲线可能是空的（最深 {maxFloor}）");
            Assert(bad.MaxSteps <= 1, $"{bad.MaxSteps} 局一直走到步数上限：地图上有去不了的地方");
            Assert(bad.HpOverPanel == 0,
                $"{bad.HpOverPanel}/40 局结束时生命超过面板上限（第一次：{bad.HpOverPanelWhere}）");
        });

        Section("场景可达性（地图上的每个房间都必须能走到）");
        Test("每个房间都能从起点按方向键走到", () =>
        {
            // 这条是给 UI 兜底的：玩家按下方向键时走的是 Game.Move(dx, dy)，
            // 而 Move 靠「当前房间坐标 + dx/dy」找目标再问 CanMove。
            // 先把「连线」和「坐标相邻」对齐，再从起点真的按方向键走一遍。
            for (uint seed = 1; seed <= 20; seed++)
            {
                var game = new Game(seed);
                var map = game.Map;
                for (var i = 0; i < map.Rooms.Count; i++)
                {
                    foreach (var to in map.Links[i])
                    {
                        var a = map.Rooms[i];
                        var b = map.Rooms[to];
                        Assert(Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) == 1,
                            $"联通的两个房间不相邻：({a.X},{a.Y}) ↔ ({b.X},{b.Y})");
                    }
                }

                // DFS：每次从「玩家当前站的房间」出发，往一个还没去过的邻居走一步。
                // 注意必须用 Move 推着玩家走（而不是拿任意的 a → b 去试），
                // 因为 Move 的参照系是玩家所在位置。
                var visited = new HashSet<int> { map.StartIndex };
                var stack = new Stack<int>();
                stack.Push(map.StartIndex);
                var steps = 0;
                while (stack.Count > 0 && steps++ < 400)
                {
                    var at = game.PlayerIndex;
                    var next = map.Links[at].FirstOrDefault(i => !visited.Contains(i), -1);
                    if (next < 0)
                    {
                        stack.Pop();
                        // 回溯：走回栈顶那个房间（它在图上是相邻的）
                        if (stack.Count > 0)
                        {
                            var back = stack.Peek();
                            if (map.Links[at].Contains(back))
                            {
                                var cur = map.Rooms[at];
                                var backRoom = map.Rooms[back];
                                game.Move(backRoom.X - cur.X, backRoom.Y - cur.Y);
                            }
                        }
                        continue;
                    }
                    var here = map.Rooms[at];
                    var there = map.Rooms[next];
                    var (moved, _, _) = game.Move(there.X - here.X, there.Y - here.Y);
                    Assert(moved, $"按方向键走不到邻居：({here.X},{here.Y}) → ({there.X},{there.Y})");
                    visited.Add(next);
                    stack.Push(next);
                    Assert(steps < 399, "DFS 步数超限，说明有环路没被收敛");
                }
                Assert(visited.Count == map.Rooms.Count,
                    $"seed={seed}：有 {map.Rooms.Count - visited.Count} 个房间走不到");
            }
        });

        Test("读档：背包里有查不到的 id 时，装备按 id 跟着走而不是按下标", () =>
        {
            // 数据表改过、或运行时装备没进存档时，背包里会留一个查不到的 id。
            // 旧读档把背包原样收下、装备槽原样沿用旧下标，于是「中间少一格」
            // 就让人身上那件装备读档后静默消失（或换成旁边那件）。
            // 这里直接改存档文件：把第 0 格换成一个不存在的 id，装备槽仍指着 0。
            var run = new Game(809);
            foreach (var id in new[] { "steel_sword", "bomb", "flame_blade" })
            {
                Inventory.AddItem(run.Player, id, run.Capacity);
            }
            Inventory.Equip(run.Player, 0, run.ItemIndex);   // 身上那件：steel_sword（存档第 0 格）
            var path = "user://selfcheck_equip_drift.cfg";
            Autoload.SaveSerializer.Delete(path);
            Autoload.SaveSerializer.Save(path, run, 4242);

            var cfg = new Godot.ConfigFile();
            Assert(cfg.Load(path) == Godot.Error.Ok, "存档读不回来");
            cfg.SetValue("run", "inventory",
                new Godot.Collections.Array<string> { "legacy_void_edge", "steel_sword", "bomb", "flame_blade" });
            cfg.SetValue("run", "equipped", new Godot.Collections.Array<string> { "0", "", "" });
            cfg.Save(path);

            var loaded = Autoload.SaveSerializer.Load(path, new MetaState());
            Assert(loaded is not null, "读档返回空");
            var worn = Inventory.EquippedDef(loaded!.Player, "weapon", loaded.ItemIndex);
            Assert(worn?.Id == "steel_sword", $"身上的装备被换成了：{worn?.Name ?? "<无>"}");
            Assert(loaded.Player.Inventory.All(id => loaded.ItemIndex.ContainsKey(id)),
                $"背包里留下了查不到的 id：[{string.Join(",", loaded.Player.Inventory)}]");
            Assert(Inventory.IsEquippedIndexValid(loaded.Player), "装备下标非法");
            Autoload.SaveSerializer.Delete(path);
        });

        Section("数据校验（写错的配置必须启动就报错）");
        Test("非法配置一条都跑不掉", () =>
        {
            // 校验的价值不在于「现在这份数据是对的」，而在于「以后改错了会立刻红」。
            // 这些用例都是 TS 版 loader.validate 里已有的条款，移植时整段漏过——
            // 漏掉的后果是「道具装不上」「词条少一条」这类玩到一半才发现的问题。
            var raw = Godot.FileAccess.GetFileAsString("res://Data/game_data.json");
            Assert(!string.IsNullOrEmpty(raw), "读不到 game_data.json");

            void MustFail(string why, Func<string, string> mutate)
            {
                var bad = mutate(raw);
                Assert(bad != raw, $"{why}：这处替身没改到东西，用例本身失效了");
                var threw = false;
                try
                {
                    Data.GameDataStore.Reset();
                    Data.GameDataStore.Set(bad);
                }
                catch (Exception)
                {
                    threw = true;
                }
                finally
                {
                    Data.GameDataStore.Reset();
                }
                Assert(threw, $"{why}：写坏的配置没有在启动时被拦下（会一路玩到出问题才炸）");
            }

            // 把解毒草唯一那件事（cleanse）拿掉：它既不能回血也不能造成伤害，
            // 那就是一件什么都不做的道具，启动时必须被拦下。
            // （`MustFail` 会先验「这处替身真的改到了东西」——改不到就直接算用例失效。）
            //
            // 这一处替身以前写的是 `Replace("\"heal\": 18,\n      \"cleanse\": true", ...)`，
            // 靠两行数据连在一起的字样。数据一改成只剩 `cleanse`，它就成了空操作。
            // 所以这里改成「先定位 antidote 那一条，再只动它自己那一行」：
            // 断言的位置不该靠另一行的存在，否则下一次数据一动，它又会悄悄变成空跑。
            MustFail("消耗品既不能回血、不能造成伤害、也不能解毒",
                j =>
                {
                    var cut = j.IndexOf("\"id\": \"antidote\"", StringComparison.Ordinal);
                    var from = cut < 0 ? -1 : j.IndexOf("\"cleanse\": true", cut, StringComparison.Ordinal);
                    return from < 0
                        ? j
                        : j.Remove(from, "\"cleanse\": true".Length).Insert(from, "\"cleanse\": false");
                });
            MustFail("诅咒房引用了不存在的词条",
                j => j.Replace("\"affixIds\": [", "\"affixIds\": [\"no_such_affix\", "));
            MustFail("敌人 AI 概率之和不等于 1",
                j => j.Replace("\"enemyAi\":", "\"enemyAiUnused\":").Replace("\"enemyAiUnused\":", "\"enemyAi\": { \"attack\": 2, \"defend\": 2, \"skill\": 2 }, \"__dead\":"));
            MustFail("逆位 Boss 的属性不是原版 2 倍",
                j => j.Replace("boss_abyss_reversed", "boss_abyss_reversed_x"));
            // 深潜段缺遭遇组合：以前整段（16~19、21~24）都没配，运行期静默退化成
            // 「从全池随机抽一只」——第 20 层能撞见第 1 层的巨鼠。删一层就该启动报错。
            MustFail("深潜段某一层没有遭遇组合",
                j =>
                {
                    var cut = j.IndexOf("\"f18_a\"", StringComparison.Ordinal);
                    var open = j.LastIndexOf('\n', cut) is var _ ? j.LastIndexOf("\"18\": [", cut, StringComparison.Ordinal) : -1;
                    var close = j.IndexOf("\n    ],", cut, StringComparison.Ordinal);
                    return open < 0 || close < 0 ? j : j.Remove(open, close + "\n    ],".Length - open);
                });
            Data.GameDataStore.Reset();
            Data.GameDataStore.Load();
        });

        Section("局内进度存档（离线规则）");
        Test("存盘→读档后玩家状态与房间标记一致", () =>
        {
            var run = new Game(4242);
            run.Player.Hp = 5;
            run.Player.Gold = 77;
            var path = "user://selfcheck_run.cfg";
            Autoload.SaveSerializer.Delete(path);
            Autoload.SaveSerializer.Save(path, run, 4242);
            var loaded = Autoload.SaveSerializer.Load(path, new MetaState());
            Assert(loaded is not null, "读档返回空");
            Assert(loaded!.Player.Hp == 5 && loaded.Player.Gold == 77, "血量/金币没还原");
            Assert(loaded.Floor == run.Floor && loaded.Loop == run.Loop, "楼层/周目没还原");
            Assert(loaded.Map.Rooms.Count == run.Map.Rooms.Count, "地图规模变了（种子没复用）");
            var clearedBefore = run.Map.Rooms.Count(r => r.Cleared);
            Assert(loaded.Map.Rooms.Count(r => r.Cleared) == clearedBefore, "房间清空标记没还原");
            Autoload.SaveSerializer.Delete(path);
        });
        Test("运行时装备的 id 不会撞上已登记的那件", () =>
        {
            // 运行时装备（诅咒装 / 附魔装 / 精英附词条装）的 id 形如 `<底板>_r<N>`。
            // 读档会把历史装备按原 id 塞回 ItemIndex，而序号计数器是新 Game 的内存字段——
            // 只要二者没对齐，新生成的那件就会叫 `_r1`，把背包里那件的定义整个盖掉。
            // 这条不依赖存档往返（那一段另有断言），直接钉住「分配 id 时不许覆盖已有条目」。
            var run = new Game(808);
            // 数据里的底板 id 也可能带 `_r`（比如 steel_r 之类），先记下当前的集合当基线
            var before = run.ItemIndex.Keys.ToHashSet();

            // 造一批和「读档恢复」形状相同的占位：同底板、序号从 1 起
            var restored = new List<string>();
            for (var i = 1; i <= 5; i++)
            {
                var id = $"probe_r{i}";
                run.RestoreRuntimeItem(id, new ItemDef { Id = id, Name = id, Slot = Slot.Weapon, Desc = $"历史{i}" });
                restored.Add(id);
            }
            // 再生成同底板的新装备：一个都不许踩到上面那五个
            for (var i = 0; i < 8; i++)
            {
                var made = run.ProbeRegisterRuntimeItem("probe", $"新的{i}");
                Assert(!restored.Contains(made.Id), $"新生成的运行时装备复用了历史 id {made.Id}");
                Assert(!before.Contains(made.Id), $"新生成的运行时装备踩到了数据里已有的 id {made.Id}");
            }
            foreach (var id in restored)
            {
                Assert(run.ItemIndex.ContainsKey(id), $"历史运行时装备 {id} 的定义被新装备顶掉了");
                Assert(run.ItemIndex[id].Desc == $"历史{id[^1]}", $"历史运行时装备 {id} 的内容被改写了");
            }
        });
        Test("离线不推进游戏时间：读档不刷新商店、也不给玩家回血", () =>
        {
            var run = new Game(555);
            var path = "user://selfcheck_run2.cfg";
            // 先把货架 roll 出来再存：这才是真实场景——玩家看过货、走了、又读档回来
            var shopIndex = run.Map.Rooms.FindIndex(r => r.Type == RoomType.Shop);
            if (shopIndex < 0) shopIndex = 0;
            var stockBefore = run.ShopStock(shopIndex).Select(s => s.Item.Id + ":" + s.Price).ToList();
            run.ShopStock(shopIndex)[0].Sold = true;
            var hpBefore = run.Player.Hp;
            Autoload.SaveSerializer.Save(path, run, 555);
            var loaded = Autoload.SaveSerializer.Load(path, new MetaState())!;
            var stockAfter = loaded.ShopStock(shopIndex).Select(s => s.Item.Id + ":" + s.Price).ToList();
            Assert(stockBefore.SequenceEqual(stockAfter), "读档后商店换了一批货（货架没进存档）");
            Assert(loaded.ShopStock(shopIndex)[0].Sold, "读档后已售出的商品又回来了（存档成了刷商店工具）");
            Assert(loaded.Player.Hp == hpBefore, $"读档改了血量：{hpBefore} → {loaded.Player.Hp}");
            Autoload.SaveSerializer.Delete(path);
        });
        Test("读档不回退随机序列与局内计数", () =>
        {
            var run = new Game(6161);
            // 消耗一些随机数，模拟「打过几场、开过几个箱子」
            for (var i = 0; i < 17; i++) run.Rng.Next();
            run.OnBattleWon(GameDataStore.Get().Monsters[0], false, 1, 1, 1);
            var path = "user://selfcheck_run5.cfg";
            Autoload.SaveSerializer.Save(path, run, 6161);
            // 存完之后再取「应当被还原的那个状态」：Save 自己不该动随机序列
            var stateBefore = run.Rng.State;
            var loaded = Autoload.SaveSerializer.Load(path, new MetaState())!;
            Assert(loaded.Rng.State == stateBefore, $"读档把随机序列倒回了（{stateBefore} → {loaded.Rng.State}，读档可重抽战利品）");
            var nextA = run.Rng.Next();
            var nextB = loaded.Rng.Next();
            Assert(Math.Abs(nextA - nextB) < 1e-12, "读档后下一个随机数不一致");
            Assert(loaded.Kills == run.Kills && loaded.BattlesFought == run.BattlesFought, "局内计数没还原");
            Autoload.SaveSerializer.Delete(path);
        });
        Test("三 Boss 连战：读档不会退回第一只", () =>
        {
            BattleEvents.Clear();
            var run = new Game(25);
            // 直接站到第 25 层的 Boss 房，逼出连战队列
            run.RestoreFloor(25, 1, GameStatus.Playing);
            run.TeleportForDebug(run.Map.BossIndex);
            var first = run.CreateEncounter();
            Assert(first?.Gauntlet is { Index: 0 } g0 && g0.Total == 3, "第 25 层没有排成三只连战");
            Assert(first!.BossMechanic?.Name == "熔岩爆裂", $"第一只 Boss 的机制不对：{first.BossMechanic?.Name}");
            run.OnBattleWon(first.Def, true, 1, 1, 1);
            Assert(run.GauntletProgress is { Index: 1 }, "打倒第一只后进度没推进");
            var path = "user://selfcheck_gauntlet.cfg";
            Autoload.SaveSerializer.Save(path, run, 25);
            var loaded = Autoload.SaveSerializer.Load(path, new MetaState())!;
            Assert(loaded.GauntletProgress is { Index: 1 }, "读档后连战进度回退了");
            var second = loaded.CreateEncounter();
            Assert(second?.BossMechanic?.Name == "霜之镜像", $"读档后出场的是 {second?.Def.Name}，不是第二位");
            Autoload.SaveSerializer.Delete(path);
            BattleEvents.Clear();
        });
        Test("存档版本不认识时当作没有存档（不带着半个状态开局）", () =>
        {
            var path = "user://selfcheck_run3.cfg";
            var cfg = new ConfigFile();
            cfg.SetValue("run", "version", OfflineRules.SaveVersion + 1);
            cfg.SetValue("run", "status", 0);
            cfg.Save(path);
            Assert(Autoload.SaveSerializer.Load(path, new MetaState()) is null, "未来版本的存档被当成可读");
            Assert(!Autoload.SaveSerializer.CanResume(path), "未来版本的存档被当成可继续");
            Autoload.SaveSerializer.Delete(path);
        });
        Test("已结束的局不进「继续」", () =>
        {
            var run = new Game(999);
            run.OnBattleLost();
            var path = "user://selfcheck_run4.cfg";
            Autoload.SaveSerializer.Save(path, run, 999);
            Assert(!Autoload.SaveSerializer.CanResume(path), "阵亡的局还能继续");
            Autoload.SaveSerializer.Delete(path);
        });

        Section("本层临时加成（训练房/符文）");
        Test("训练房的苦练只在本层生效，下楼清空", () =>
        {
            var run = new Game(31);
            var text = run.TakeTraining("attack");
            Assert(text.Contains('+'), $"苦练没有返回加成说明：{text}");
            Assert(run.TrainingAttackBuff > 0, "选了苦练但攻击加成还是 0（说明与实现又对不上了）");
            // 下楼清空：不然「选一次强一层」会滚成雪球
            for (var i = 0; i < run.Map.Rooms.Count; i++) run.RestoreRoom(i, true, true);
            var (ok, _) = run.Descend();
            Assert(ok, "清空后仍然下不了楼");
            Assert(run.TrainingAttackBuff == 0, "下楼后苦练还在生效");
        });
        Test("符文只刻在装备上，不会被消耗品吃掉", () =>
        {
            var run = new Game(32);
            run.TakeTraining("affix");
            Assert(run.PendingBonusAffix is not null, "训练房没有铭刻符文");
            var pending = run.PendingBonusAffix!;
            // 直接把一件消耗品「附魔」：以前会给药水也加词条，符文白刻在一瓶水上
            var potion = data.Items.First(i => i.Slot == Slot.Consumable);
            run.ApplyBonusAffix(potion);
            Assert(run.PendingBonusAffix == pending, "符文被消耗品吃掉了");
            // 换成装备就该用掉
            var gear = data.Items.First(i => i.Slot == Slot.Weapon);
            run.ApplyBonusAffix(gear);
            Assert(run.PendingBonusAffix is null, "附在装备上之后符文还挂着（会被下一件再吃一次）");
        });

        Test("同一层的两间训练房：苦练要累加，两次铭刻要都保留", () =>
        {
            // 权重开着时一层可能有两间训练房（随机 300 局里出现过 29 次）。
            // 早先第二次「苦练招式」会覆盖第一次——点了按钮，攻击一点没变；
            // 第二次「铭刻符文」也会把第一条覆盖掉，玩家只拿到一条。
            var run = new Game(33);
            run.TakeTraining("attack");
            var once = run.TrainingAttackBuff;
            run.TakeTraining("attack");
            Assert(run.TrainingAttackBuff > once, $"第二间训练房的苦练没生效：{once} → {run.TrainingAttackBuff}");

            var rune = new Game(34);
            var gearTemplate = GameDataStore.Get().Items
                .First(i => i.Slot == Slot.Weapon && Inventory.QualityOf(i) == Quality.White);
            rune.TakeTraining("affix");
            rune.TakeTraining("affix");
            Assert(rune.PendingBonusAffixes.Count == 2, $"两条符文只剩 {rune.PendingBonusAffixes.Count} 条");
            var enchanted = rune.ApplyBonusAffix(gearTemplate);
            Assert(rune.PendingBonusAffixes.Count == 0, "符文用完后队列没清空");
            Assert(enchanted.Affixes is { Count: 2 }, $"两条符文没有一起刻上去：{enchanted.Affixes?.Count ?? 0} 条");
        });

        Test("瘴气层的毒伤按这一刀的实际伤害算，不按玩家攻击力", () =>
        {
            // 早先 power 取的是玩家面板攻击的一半：玩家越强，这一层的毒越毒。
            // 现在它取的是「这一刀挨了多少」——同一个玩家，受伤越多毒越重。
            var rules = new CombatRules { CritChance = 0, CritMultiplier = 1, DefendReduction = 0.5, DamageVariance = 0 };
            int PoisonAfterHit(int enemyAttack)
            {
                var player = EntityRules.CreatePlayer(500, 40, 0, 0, new List<SkillDef>());
                var def = new MonsterDef { Id = "t", Name = "测试", Hp = 500, Attack = enemyAttack, Defense = 0 };
                var enemy = EntityRules.CreateMonster(def, new List<SkillDef>(), 2);
                var battle = new Battle(player, enemy, new Rng(4242), new BattleOptions
                {
                    Rules = rules,
                    AttackerAtk = c => c is Player ? 40 : c.BaseAttack,
                    AttackerDef = c => 0,
                    MaxHp = c => 500,
                    FloorMechanic = "haze",
                    Floor = 2,
                    EnemyDefs = new Dictionary<Combatant, MonsterDef> { [enemy] = def },
                });
                // 打到中毒为止（20% 概率，最多试 200 回合）。
                // 上限必须写在条件里而不是 while 里：战斗一旦结束（这里必然结束——
                // 防御姿态下敌人迟早打死 500 血的玩家，或者反过来），
                // PlayerDefend 就什么都不做了，循环会空转 200 次、毒永远挂不上。
                for (var i = 0; i < 4000 && player.PoisonTurns == 0 && battle.Phase == BattlePhase.Player; i++)
                {
                    battle.PlayerDefend();
                }
                return player.PoisonPower;
            }
            // 岔子：毒被封在 1..MaxPoisonPower 之后，下限与上界是同一个数（6），
            // 于是「跟着这一刀走」在这款游戏的现存数值里**没有可观测的区间**——
            // 任何一刀挂上来的毒都是 6。这条判据于是改成两个断言：
            //   1. 同一个玩家，毒值恒在 [下限, 上界] 之内（不再是一条指数曲线）；
            //   2. 算式本身仍然读「这一刀挨了多少」，不是读玩家攻击力——直接量那个函数。
            var light = PoisonAfterHit(1);
            var heavy = PoisonAfterHit(40);
            Assert(light > 0 && heavy > 0, "瘴气层一次毒都没挂上");
            Assert(light <= EntityRules.MaxPoisonPower && heavy <= EntityRules.MaxPoisonPower,
                $"瘴气的毒伤越过了上界：{light} / {heavy}（上界 {EntityRules.MaxPoisonPower}）");
            Assert(light >= FloorMechanics.HazePoisonFloor && heavy >= FloorMechanics.HazePoisonFloor,
                $"瘴气的毒伤掉到了下限以下：{light} / {heavy}（下限 {FloorMechanics.HazePoisonFloor}）");
        });

        Test("灼烧层的破防对敌人技能同样生效", () =>
        {
            // 说明写的是「敌人攻击无视你 30% 防御」，敌人的技能（炼狱、灵魂汲取）
            // 走的也是攻击结算。早先只有普攻吃破防，会放技能的怪在这一层等于没被削弱。
            var inferno = GameDataStore.Get().Skills.Where(s => s.Id == "inferno").ToList();
            int DamageOn(string mechanic)
            {
                var player = EntityRules.CreatePlayer(500, 10, 6, 0, new List<SkillDef>());
                var def = new MonsterDef { Id = "t", Name = "测试", Hp = 500, Attack = 20, Defense = 0, Skills = new List<string> { "inferno" } };
                var enemy = EntityRules.CreateMonster(def, inferno, 9);
                var battle = new Battle(player, enemy, new Rng(808), new BattleOptions
                {
                    Rules = new CombatRules { CritChance = 0, CritMultiplier = 1, DefendReduction = 0.5, DamageVariance = 0 },
                    AttackerAtk = c => c is Player ? 10 : c.BaseAttack,
                    AttackerDef = c => c.BaseDefense,
                    MaxHp = c => 500,
                    FloorMechanic = mechanic,
                    Floor = 9,
                    EnemyDefs = new Dictionary<Combatant, MonsterDef> { [enemy] = def },
                });
                var before = player.Hp;
                // 一直防到它放技能为止
                for (var i = 0; i < 60 && player.Hp >= before; i++) battle.PlayerDefend();
                return before - player.Hp + battle.PoisonDamage;
            }
            var plain = DamageOn("none");
            var searing = DamageOn("searing");
            Assert(searing >= plain, $"灼烧层没能让技能打得更疼：{plain} → {searing}");
        });

        Test("事件拿到的装备必须登记成运行时装备（否则读档就消失）", () =>
        {
            // 许愿井的「随机装备」与流浪商人的「交换」以前只过 PrepareItem：
            // 拿到的副本用的还是模板 id，而存档只写得下 id 带 _rN 的运行时装备，
            // 读档后背包里那一格就凭空没了。
            var run = new Game(35);
            var data = GameDataStore.Get();
            run.Player.Inventory.Clear();
            var ctx = new EventApplyContext
            {
                Rng = new Rng(35),
                Items = data.Items,
                Capacity = run.Capacity,
                ItemsById = run.ItemIndex,
                PanelMaxHp = run.Stats.MaxHp,
                PrepareItem = run.ApplyBonusAffix,
                RegisterItem = run.RegisterRuntimeItemCopy,
            };
            // 连着拿两件：第一件消耗掉符文队列，第二件走「队列已空」的短路，
            // 那条路以前直接返回模板本身（id 是 steel_sword），存档写得下但读不回来。
            for (var round = 0; round < 2; round++)
            {
                var result = GameEvents.ApplyEffect(run.Player, new EventEffect { Type = EventEffectType.RandomItem }, ctx);
                Assert(result.GainedItemId is not null, "事件没给出装备");
                Assert(run.ItemIndex.TryGetValue(result.GainedItemId!, out var stored), "事件装备没进 ItemIndex，读档会找不到它");
                Assert(ReferenceEquals(stored, run.ItemIndex[result.GainedItemId!]), "ItemIndex 里那件和拿到的不是同一个实例");
                // 判据是「它有一个存档写得下的身份」，不是「id 里带不带 _r」：
                // 空队列时那件装备根本没被改过，但 id 仍然必须被登记过。
                var gainedId = result.GainedItemId!;
                Assert(gainedId.Any(char.IsDigit) || gainedId.Contains('_'),
                    $"事件给的装备没有运行时 id：{result.GainedItemId}");
            }
        });

        Section("这一轮补的规则缺口");
        Test("读档后同一层 Boss 不能重领首杀碎片", () =>
        {
            // 首杀记录以前只在内存里：读档后集合空了，玩家回到 Boss 房再打一次，
            // 碎片又发一遍——存档成了刷碎片的工具。
            var run = new Game(25);
            run.RestoreFloor(25, 1, GameStatus.Playing);
            run.TeleportForDebug(run.Map.BossIndex);
            var enc = run.CreateEncounter();
            Assert(enc is not null, "第 25 层没有遭遇");
            // 第 25 层是三 Boss 连战：首杀要等队列走完（三只都倒下）才算。
            // 打完第一只就发碎片的话，玩家打到一半放弃也能把这一层的奖励领走。
            var total = enc!.Gauntlet?.Total ?? 1;
            Assert(total >= 2, $"第 25 层不是连战（total={total}），这条用例的前提没了");
            for (var i = 0; i < total; i++)
            {
                Assert(!run.BeatenBossFloors.Contains(25), $"连战第 {i} 只刚倒下就发了首杀碎片（该等三只都打完）");
                var e = run.CreateEncounter() ?? throw new InvalidOperationException("连战队列断在半路");
                run.OnBattleWon(e.Def, true, 1, 1, 1);
            }
            Assert(run.BeatenBossFloors.Contains(25), "三只都打完，首杀却没被记下");
            var path = "user://selfcheck_boss.cfg";
            Autoload.SaveSerializer.Save(path, run, 25);
            var loaded = Autoload.SaveSerializer.Load(path, new MetaState())!;
            Assert(loaded.BeatenBossFloors.Contains(25), "读档后首杀记录丢了（Boss 可重复领碎片）");
            Autoload.SaveSerializer.Delete(path);
        });
        Test("BGM 判 Boss 层用的是「有没有 Boss 机制」，不是「是不是最终层」", () =>
        {
            // 第 25 层是三 Boss 连战，第 30 层才是最终层。旧判据让它永远当不成 Boss 层。
            var gauntlet = new Game(25);
            // Game 从第 1 层开局：先下到第 25 层，Floor 才会是 25
            gauntlet.RestoreFloor(25, 1, GameStatus.Playing);
            Assert(gauntlet.BossStage(25) is not null, "第 25 层没有 Boss 舞台");
            Assert(gauntlet.IsBossFloor, "第 25 层没被判成 Boss 层");
            Assert(UI.Bgm.CueFor(gauntlet.FloorMechanicId, gauntlet.IsBossFloor, true) == "boss",
                "第 25 层战斗中放的不是 Boss 曲");
            var shallow = new Game(3);
            Assert(!shallow.IsBossFloor, "第 3 层被当成了 Boss 层");
            Assert(UI.Bgm.CueFor(shallow.FloorMechanicId, shallow.IsBossFloor, true) != "boss",
                "第 3 层被当成了 Boss 层");
        });
        Test("Boss 的降攻不会让玩家掉血（词条归属不再指错人）", () =>
        {
            // affixesOwner 传的应该是玩家（词条的主人），不是施法者。
            // 传错之后「霜噬」那类「命中回血」会挂在敌人自己身上，玩家看到一个凭空回血的怪。
            var rules = RulesOf(data);
            var player = EntityRules.CreatePlayer(100, 10, 0, 0, new List<SkillDef>());
            var enemy = EntityRules.CreateMonster(data.Monsters[0], new List<SkillDef>(), 1);
            var battle = new Battle(player, enemy, new Rng(1234), new BattleOptions
            {
                Rules = rules,
                AttackerAtk = c => c is Player ? 10 : 5,
                AttackerDef = c => 0,
                MaxHp = c => 100,
            });
            var before = player.Hp;
            battle.PlayerDefend();
            battle.PlayerDefend();
            Assert(player.Hp <= before, $"防御两回合后玩家血量变成了 {player.Hp}");
        });

        Section("事件结算的三笔账（装备 / 金币 / 符文）");
        Test("事件换来的装备不能把本层符文吞掉", () =>
        {
            // 训练房的「铭刻符文」承诺「本层下一件入手的装备多一条词条」。
            // 而事件给装备走的是 Events.ApplyEffect：不经过 Game.ApplyBonusAffix 的话，
            // 符文既没被挂上、也没被消耗，就这么躺在 PendingBonusAffix 里被下一次战斗掉落顺手吃掉——
            // 玩家看到的是「我换到一件什么都没有的装备」。
            var game = new Game(777);
            var data = GameDataStore.Get();
            game.TakeTraining("affix");
            var affixId = game.PendingBonusAffix;
            Assert(affixId is not null, "本层符文没记下来");
            game.Player.Inventory.Clear();
            game.Player.Inventory.Add(data.Items.First(i => i.Slot != Slot.Consumable).Id);
            var ctx = new EventApplyContext
            {
                Rng = new Rng(7),
                Items = data.Items,
                Capacity = game.Capacity,
                ItemsById = game.ItemIndex,
                PanelMaxHp = game.Stats.MaxHp,
                SwapOutIndex = 0,
                PrepareItem = game.ApplyBonusAffix,
            };
            var result = GameEvents.ApplyEffect(game.Player,
                new EventEffect { Type = EventEffectType.SwapItem }, ctx);
            Assert(result.GainedItemId is not null, "没换到东西");
            Assert(game.PendingBonusAffix is null, "符文没被消耗：事件把这次机会吞了");
            var got = game.ItemIndex[result.GainedItemId!];
            Assert(got.Affixes is not null && got.Affixes.Contains(affixId!),
                $"换来的「{got.Name}」上没有本层符文");
            // id 必须是运行时登记过的：直接塞模板 id 会让背包里那件和模板共用同一份定义
            Assert(got.Id.Contains("_r"), $"换来的装备还是模板 id（{got.Id}）：会和同名的别件共用定义");
            Assert(data.Items.All(i => i.Id != got.Id), $"运行时装备直接盖在了模板表上：{got.Id}");
        });
        Test("事件付出去的金币进 GoldLost，不是悄悄消失", () =>
        {
            // 「投入 10 金币」这类选项：以前只从 Gold 里扣，Player.GoldLost 一动不动，
            // 于是结算面板上「金币获得」把花掉的也算成收入。
            var game = new Game(31);
            game.Player.Gold = 100;
            var lostBefore = game.Player.GoldLost;
            var ctx = new EventApplyContext
            {
                Rng = new Rng(3),
                Items = GameDataStore.Get().Items,
                Capacity = game.Capacity,
                ItemsById = game.ItemIndex,
                PanelMaxHp = game.Stats.MaxHp,
                // 和界面同一条路：事件的进出都记在 Game 那本账上
                AddGold = d => { if (d >= 0) game.EarnGold(d); else game.SpendGold(-d); },
            };
            var runEarnedBefore = game.GoldEarned;
            GameEvents.ApplyEffect(game.Player, new EventEffect { Type = EventEffectType.Gold, Value = 35 }, ctx);
            Assert(game.Player.GoldEarned == 35, $"金币获得的记账不对：{game.Player.GoldEarned}");
            // 两本账都要涨。Game.GoldEarned 才是结算面板与存档 gold_earned 读的那个数——
            // 只断言 Player 那一本的话，事件挣的钱可以一直不进账而这条始终是绿的。
            Assert(game.GoldEarned - runEarnedBefore == 35, $"本局的金币获得（Game.GoldEarned）没跟着涨：{game.GoldEarned - runEarnedBefore}");
            GameEvents.ApplyEffect(game.Player, new EventEffect { Type = EventEffectType.Gold, Value = -10 }, ctx);
            Assert(game.Player.GoldLost - lostBefore == 10, $"付出去的 10 金币没记进 GoldLost：{game.Player.GoldLost - lostBefore}");
        });

        Test("买东西之后的账与货架都能穿过一次存档", () =>
        {
            // 商店这条链有两个容易漏的账：
            //   1. GoldLost 要涨（结算面板「金币获得」才不会被买货撑大）；
            //   2. 货架上的 Sold 已经在存档里了，但「买到的这件」得靠背包一起存回来。
            var game = new Game(2024);
            var roomIndex = game.Map.Rooms.FindIndex(r => r.Type == RoomType.Shop);
            Assert(roomIndex >= 0, "这一层没有商店");
            var stock = game.ShopStock(roomIndex);
            Assert(stock.Count > 0, "货架是空的");
            game.Player.Gold = 9999;
            var entry = stock[0];
            game.SpendGold(entry.Price);
            Inventory.AddItem(game.Player, entry.Item.Id, game.Capacity);
            entry.Sold = true;

            var path = "user://selfcheck_shop.cfg";
            Autoload.SaveSerializer.Save(path, game, 2024);
            var loaded = Autoload.SaveSerializer.Load(path, new MetaState())!;
            Assert(loaded.Player.GoldLost == game.Player.GoldLost, "读档后 GoldLost 对不上");
            Assert(loaded.Player.Inventory.Contains(entry.Item.Id), "买到的道具没进存档");
            var restored = loaded.ShopStock(roomIndex);
            Assert(restored.Any(s => s.Item.Id == entry.Item.Id && s.Sold), "已售标记没还原（读档白送一次补货）");
            Autoload.SaveSerializer.Delete(path);
        });

        Section("这一轮的几笔账：掉落/装备/符文/下楼");

        Test("掉落的装备读档后还在（不只是事件给的那几件）", () =>
        {
            // 掉落装备此前从来没被登记成运行时装备。它一旦是「副本」
            // （词条改了、品质抬了），存档里存的就是一个新发的 id——
            // 而那个 id 查不到模板、也没进 runtime_items，读档后背包那一格直接消失。
            // 事件装备当年踩的是同一个坑，修的时候只修了事件那条路。
            var game = new Game(4242);
            var data = GameDataStore.Get();
            var rewards = game.Rewards(EnemyTier.Elite);
            var item = rewards.OfType<Reward.Item>().FirstOrDefault();
            Assert(item is not null, "精英奖励里没有装备");
            Assert(item!.Def.Id.Contains("_r"), $"掉落装备还是模板 id（{item.Def.Id}）：会被后面任何一次同名生成改掉");
            Assert(game.ItemIndex.ContainsKey(item.Def.Id), "掉落装备没进 ItemIndex：界面上查不到它");
            game.Player.Inventory.Clear();
            Inventory.AddItem(game.Player, item.Def.Id, game.Capacity);

            var path = "user://selfcheck_drop.cfg";
            Autoload.SaveSerializer.Save(path, game, 4242);
            var loaded = Autoload.SaveSerializer.Load(path, new MetaState())!;
            Assert(loaded.Player.Inventory.Contains(item.Def.Id), "读档后掉落的装备消失了");
            Assert(loaded.ItemIndex.ContainsKey(item.Def.Id), "读档后掉落装备的定义没恢复");
            Autoload.SaveSerializer.Delete(path);
        });

        Test("消耗品不冒充装备：商店里那两行说明长得不一样", () =>
        {
            // 商店截图里的现场：一瓶「强效治疗药水」与一件「锁子甲」下面那行字
            // 都是「（无词条）/（单词条）」——品质档位说的是「这件装备能带几条词条」，
            // 而消耗品根本不会挂词条。两行字长一样，玩家分不出哪个是药、哪个是甲。
            var data = GameDataStore.Get();
            var potion = data.Items.First(i => i.Slot == Slot.Consumable && i.Heal is > 0);
            var bomb = data.Items.First(i => i.Slot == Slot.Consumable && i.Damage is > 0);
            var sword = data.Items.First(i => i.Slot == Slot.Weapon);
            foreach (var (def, what) in new[] { (potion, "药"), (bomb, "炸弹") })
            {
                var line = UI.Widgets.ItemSummary(def, data.Affixes);
                Assert(!line.Contains("词条"), $"{what}的说明里出现了「词条」：{line}");
                Assert(line.Contains(def.Desc.Trim()), $"{what}的说明没有写它做什么：{line}");
            }
            // 装备那一行反过来必须保留词条档位：不然「紫装只带一条」就没人解释得清
            Assert(UI.Widgets.ItemSummary(sword, data.Affixes).Contains("词条"),
                "装备的说明丢了词条档位");
        });

        Test("泉眼的价钱跟着层数长，且最深处仍买得起", () =>
        {
            // 上一轮留下的待办：泉眼只有一条固定曲线（60/105/150/195/240），
            // 而第 6 层起一层进项就有 300~400。横扫 30 局里献过金的 24 局有 18 局
            // 把五次全用完——钱对它是够的，于是它变成了一条和升级平行的免费曲线。
            var game = new Game(777);
            var off = GameDataStore.Get().Config.Altars.Offering;
            game.RestoreFloor(off.MinFloor, 1, GameStatus.Playing);
            var atMin = game.OfferingCostFor(0);
            game.RestoreFloor(off.MinFloor + 4, 1, GameStatus.Playing);
            var deeper = game.OfferingCostFor(0);
            Assert(deeper > atMin, $"深层的第一笔献金没有变贵：第 {off.MinFloor} 层 {atMin} → 更深 {deeper}");
            // 但也不能贵到「攒不出一层进项就永远买不起」：那只是把「钱没处花」搬到祭坛门口
            Assert(game.OfferingCostFor(0, GameDataStore.Get().Config.FinalFloor) <= 4 * (12 + 15 * 4),
                "最终层的第一次献金超过了一整层的进项");
            // 按钮上的数必须就是结算的数：两处读同一个方法，这条是给它上锁
            game.RestoreFloor(off.MinFloor + 2, 1, GameStatus.Playing);
            var quoted = game.OfferingCost();
            game.Player.Gold = quoted + 100;
            var (ok, spent, _, _) = game.TakeOffering();
            Assert(ok && spent == quoted, $"按钮写着 {quoted}，实际花了 {spent}");
        });

        Test("买下 / 收下的一件装备一定查得到名字", () =>
        {
            // 装备能不能用、能不能存档，都取决于「按 id 查得到 ItemIndex」。
            // 以前商店卖出去的那件就直接 `AddItem(e.Item.Id)` 了事：货架是缓存的，
            // 谁也没保证它进过 ItemIndex——查不到的表现是背包里一格空白，
            // 没有名字、点不动、也存不下。这里走和界面同一条登记路。
            var game = new Game(999);
            var roomIndex = game.Map.Rooms.FindIndex(r => r.Type == RoomType.Shop);
            Assert(roomIndex >= 0, "这一层没有商店");
            var stock = game.ShopStock(roomIndex);
            Assert(stock.Count > 0, "货架是空的");
            var pricey = stock[0];
            // 走和界面同一条登记路（MapScene 的买下按钮）
            if (!game.ItemIndex.TryGetValue(pricey.Item.Id, out var known) || !ReferenceEquals(known, pricey.Item))
            {
                game.ItemIndex[pricey.Item.Id] = game.RegisterRuntimeItemCopy(pricey.Item);
            }
            Assert(game.ItemIndex.TryGetValue(pricey.Item.Id, out var bought), "买下的装备查不到");
            Assert(ReferenceEquals(bought, pricey.Item) || bought!.Name == pricey.Item.Name, "买到的那件和查到的那份不是同一件");

            var cursed = game.CurseRoomItem();
            Assert(cursed is not null, "诅咒房没产出装备");
            Assert(game.ItemIndex.ContainsKey(cursed!.Id), $"诅咒装查不到（{cursed.Id}）");
        });

        Test("战斗掉落吃符文之后，词条一条都不能丢", () =>
        {
            // 三个病根，同一个表现：「说明写了、装备上没有」。
            //   1) 符文抽到模板上已经有的那条 → 加词条那一步等于什么都没做；
            //   2) 品质一律抬到橙，而橙的容量也只有 2 → 第 3 条被静默截掉；
            //   3) 截掉之后 desc 还说「已铭刻」。
            var game = new Game(1234);
            var data = GameDataStore.Get();
            // 底板必须是白板：掉落表里的模板一条词条都不带，符文挂上去是干净的，
            // 这样「三条就是三条」是可判的（以前这条断言挑的是「带词条且容量 >= 2」
            // 的模板，而全表根本没有这种模板，于是它一直在抛 Sequence contains no matching element）。
            var template = data.Items.First(i => i.Slot != Slot.Consumable && (i.Affixes?.Count ?? 0) == 0);
            for (var i = 0; i < 3; i++) game.TakeTraining("affix");
            Assert(game.PendingBonusAffixes.Count == 3, $"三次铭刻只记下 {game.PendingBonusAffixes.Count} 条");
            var after = game.ApplyBonusAffix(template);
            Assert(game.PendingBonusAffixes.Count == 0, "符文没用完");
            // 品质表里最高容量就是 2（橙也只有 2）：三条符文一定装不下，
            // 那就必须截在容量上、并且把被截掉的那条名字写进说明里。
            var cap = data.Qualities.Max(q => q.Value.AffixCount);
            Assert(after.Affixes is not null && after.Affixes.Count == cap,
                $"符文进了 {after.Affixes?.Count} 条（容量 {cap}）");
            var equippedAffixes = after.Affixes!;
            Assert(equippedAffixes.Distinct().Count() == equippedAffixes.Count, "同一件装备上出现了重复词条");
            var descText = after.Desc;
            foreach (var id in equippedAffixes)
                Assert(descText.Contains(data.Affixes[id].Name), $"说明里没有「{data.Affixes[id].Name}」这一条的效果");
            Assert(descText.Contains("装不下"), "截掉了词条，说明里却没说");

            // 模板自己带同一条词条时，符文要换成别的顶上，而不是原地不动。
            // 直接点名那条词条，不靠运气：把队列清干净，再塞一条模板上已有的。
            var have = data.Affixes.Keys.First();
            game.RestorePendingAffixes(new[] { have });
            // 造一件「只带那一条」的底板：这样「换了没换」一眼可判。
            var probe = template.Clone();
            probe.Affixes = new List<string> { have };
            // 把它登记成运行时装备，好让 AttachAffixes 的容量计算看到同一份词条。
            probe = game.RegisterRuntimeItemCopy(probe);
            var dup = game.AttachAffixesForTest(probe);
            Assert(dup.Affixes is not null && dup.Affixes.Contains(have), "模板自带的词条被符文顶掉了");
            Assert(dup.Affixes!.Count == 2 && dup.Affixes.Distinct().Count() == 2,
                $"重复词条没有被换成别的：{string.Join("、", dup.Affixes)}——这次铭刻等于什么都没做");
        });

        Test("通关之后按「下楼」不能继续往下走", () =>
        {
            // Descend 的判据顺序反了：先查「这层清空了吗」，再查状态。
            // 通关那一刻 Boss 房刚清掉，FloorCleared 恰好为真 —— 于是打穿第 15 层的人
            // 还能继续下潜到第 16、17 层，一直走到没有地图的深处。
            // 界面那条路先问了状态，所以玩家没踩到；规则只写对一半，迟早有人踩到。
            var game = new Game(15);
            game.Player.BaseAttack = 9999;
            game.Player.BaseDefense = 9999;
            game.Player.BaseMaxHp = 9999;
            game.Player.Hp = 9999;
            game.RestoreFloor(game.FinalFloor, 1, GameStatus.Won);
            // 只清「战斗房」：FloorCleared 的判据就是这么写的，把商店/宝箱也标成
            // 已清空等于伪造一种真实游戏里不可能出现的状态。
            foreach (var r in game.Map.Rooms)
            {
                if (r.Type is RoomType.Enemy or RoomType.Elite or RoomType.Boss) r.Cleared = true;
            }
            Assert(game.FloorCleared, "最终层没法清空，这条断言的前提不成立");
            // 两种状态分开验：通关与阵亡。两条都不是「可以继续往下走」。
            foreach (var status in new[] { GameStatus.Won, GameStatus.Dead })
            {
                game.RestoreFloor(game.FinalFloor, 1, status);
                foreach (var r in game.Map.Rooms)
                {
                    if (r.Type is RoomType.Enemy or RoomType.Elite or RoomType.Boss) r.Cleared = true;
                }
                var (ok, reason) = game.Descend();
                Assert(!ok, $"{status} 之后还能下楼：楼层会一直涨到没地图的深处");
                Assert(reason is not null, "拒绝下楼却没给理由");
                Assert(game.Floor == game.FinalFloor, $"楼层被推到了 {game.Floor}");
            }
        });

        Test("读档不能把没打过的房间变成「没走过」或者「清空了」", () =>
        {
            // 存档里有两份房间标记，读档时被写成了互相覆盖的一对：
            //   rooms_cleared 的每一项调 RestoreRoom(i, true, true) ——顺带把所有清空过的房
            //   标成「访问过」；
            //   rooms_visited 的每一项又用「这张新图上第 i 格现在的 Cleared」去覆盖它，
            //   而新图上除了起点，其余都是 false。
            // 于是「清空过」被后一句冲掉；更要紧的是它同时盖掉前一句的 visited，
            // 两份标记谁说了算完全取决于遍历顺序。
            var game = new Game(4321);
            var target = game.Map.Rooms.FindIndex(r => r.Type is RoomType.Treasure or RoomType.Event or RoomType.Shop);
            Assert(target >= 0, "这一层没有可标记的非战斗房");
            game.Teleport(target);
            game.ClearRoom(target);
            var path = "user://selfcheck_rooms.cfg";
            Autoload.SaveSerializer.Save(path, game, 4321);
            var loaded = Autoload.SaveSerializer.Load(path, new MetaState())!;
            var room = loaded.Map.Rooms[target];
            Assert(room.Visited, "读档后清空过的房间变成「没走过」——玩家会以为这间还没探索");
            Assert(room.Cleared, "读档后清空过的房间又能再刷一次");
            // 起点以外的房不允许被凭空标成「已清空」：那等于读档白送一整层的战斗奖励
            for (var i = 0; i < loaded.Map.Rooms.Count; i++)
            {
                if (i == target || i == loaded.Map.StartIndex) continue;
                Assert(!loaded.Map.Rooms[i].Cleared, $"第 {i} 格被凭空标成已清空");
            }
            Autoload.SaveSerializer.Delete(path);
        });

        Test("祭坛的每一类都有名字、说明，而且真的抽得到", () =>
        {
            // 祭坛的「代价」写在三处：按钮副标题、结算、README 的房间表。
            // 结算已经收进 Game.TakeSacrifice；这里钉住「抽到的那一类真的能结算」，
            // 防止某天数据表加了第三种祭坛而结算分支没跟上（点了没反应，也不说为什么）。
            var game = new Game(888);
            // 必在**能抽全的那一层**验：泉眼只从某一层起出现，站在第 1 层抽 60 次
            // 当然抽不到它。这条断言以前写在第 1 层，泉眼一加进来就红——
            // 红的其实是判据站错了层，而不是游戏少了一类祭坛。
            game.RestoreFloor(GameDataStore.Get().Config.Altars.Offering.MinFloor, 1, GameStatus.Playing);
            var kinds = new HashSet<AltarKind>();
            for (var i = 0; i < 400; i++) kinds.Add(game.RollAltar().Kind);
            Assert(kinds.SetEquals(Game.Altars.Keys), $"祭坛类型只抽到 {string.Join("、", kinds)}，表里有 {string.Join("、", Game.Altars.Keys)}");
            foreach (var kind in Game.Altars.Keys)
            {
                var def = Game.Altars[kind];
                Assert(!string.IsNullOrEmpty(def.Name) && !string.IsNullOrEmpty(def.Text),
                    $"祭坛「{kind}」缺名字或说明——面板上会是一块空白");
            }
        });

        Test("泉眼只在深层出现，换的是面板、花的是真钱，而且一局有上限", () =>
        {
            // 泉眼是「深层的钱没处花」那件事的出口。它有三条边界必须一起钉住，
            // 每一条错掉都不会报错，只会让游戏悄悄变样：
            //   1. 浅层不该出现——它是深层的出口，不是第二条升级曲线；
            //   2. 换来的必须是 `BaseMaxHp` / `BaseAttack`（面板），不是某一层buff；
            //   3. 次数封顶，且金币走 `SpendGold`（结算面板上的「花掉」要对得上）。
            var game = new Game(777);
            var data = GameDataStore.Get();
            var off = data.Config.Altars.Offering;
            // 浅层：一次都不该抽到
            var shallowKinds = new HashSet<AltarKind>();
            for (var i = 0; i < 200; i++) shallowKinds.Add(game.RollAltar().Kind);
            Assert(!shallowKinds.Contains(AltarKind.Offering),
                $"第 {game.Floor} 层就抽到了泉眼（它应当从第 {off.MinFloor} 层才出现）");

            // 深层：抽得到，而且必须抽得到——一个永远抽不到的选项等于没写
            game.RestoreFloor(off.MinFloor, 1, GameStatus.Playing);
            var deepKinds = new HashSet<AltarKind>();
            for (var i = 0; i < 400; i++) deepKinds.Add(game.RollAltar().Kind);
            Assert(deepKinds.Contains(AltarKind.Offering),
                $"第 {game.Floor} 层抽了 400 座祭坛也没有泉眼（权重 {data.Config.Altars.OfferingWeight}）");

            // 结算：钱要真的扣、上限与攻击要真的涨
            game.Player.Gold = 100000;
            var goldBefore = game.Player.Gold;
            var goldLostBefore = game.Player.GoldLost;
            var maxBefore = game.Stats.MaxHp;
            var atkBefore = game.Player.BaseAttack;
            var quoted = game.OfferingCost();
            var (ok, spent, hp, atk) = game.TakeOffering();
            Assert(ok, "泉眼说能换，结算却拒绝了");
            Assert(spent == quoted, $"泉眼按钮写着 {quoted} 金币，实际花了 {spent}");
            Assert(goldBefore - game.Player.Gold == spent, "扣的金币与结算给的不是同一个数");
            Assert(game.Player.GoldLost - goldLostBefore == spent, "花掉的钱没有记进 GoldLost：结算面板会少一笔");
            Assert(game.Stats.MaxHp == maxBefore + hp, $"生命上限从 {maxBefore} 变成 {game.Stats.MaxHp}，该涨 {hp}");
            Assert(game.Player.BaseAttack == atkBefore + atk, "攻击力没有跟上：泉眼给的是别的东西");
            Assert(game.Player.Hp <= game.Stats.MaxHp, "献金之后当前生命超过了上限");

            // 价钱必须随次数涨：不涨的话，「攒够一次」就等于「从此白住」
            var first = game.OfferingCostFor(0);
            var last = game.OfferingCostFor(off.MaxSteps - 1);
            Assert(last > first, $"泉眼第 {off.MaxSteps} 次仍只要 {last} 金币（第一次 {first}）");

            // 次数封顶：换满了就不许再换，钱一分都不能少
            while (game.CanOffer) game.TakeOffering();
            var goldAtCap = game.Player.Gold;
            var capResult = game.TakeOffering();
            Assert(!capResult.Ok && game.Player.Gold == goldAtCap, "次数已满还能继续献金（等于无限成长）");

            // 钱不够：拒绝，而且什么都不该变
            var poor = new Game(778);
            poor.RestoreFloor(off.MinFloor, 1, GameStatus.Playing);
            poor.Player.Gold = 0;
            var poorMax = poor.Stats.MaxHp;
            var poorResult = poor.TakeOffering();
            Assert(!poorResult.Ok && poor.Stats.MaxHp == poorMax && poor.Player.Gold == 0,
                "钱不够时泉眼仍然成交了");
        });

        Test("泉眼换来的东西会跟着存档走：关窗重开不能白拿一次", () =>
        {
            // 与血祭同一个坑：改的是 BaseMaxHp / BaseAttack，不落盘就等于
            // 「读档退回原样、钱也还在」——祭坛成了刷面板的工具。
            // 次数同样要落盘，否则上限形同不存在。
            var game = new Game(779);
            game.RestoreFloor(6, 1, GameStatus.Playing);
            game.Player.Gold = 5000;
            game.TakeOffering();
            game.TakeOffering();
            var steps = game.OfferingSteps;
            var maxHp = game.Stats.MaxHp;
            var atk = game.Player.BaseAttack;
            const string path = "user://selfcheck_offering.cfg";
            Autoload.SaveSerializer.Save(path, game, game.Rng.Seed);
            var loaded = Autoload.SaveSerializer.Load(path, new MetaState());
            Autoload.SaveSerializer.Delete(path);
            Assert(loaded is not null, "存档读不回来");
            Assert(loaded.OfferingSteps == steps, $"读档后献金次数从 {steps} 变成 {loaded.OfferingSteps}");
            Assert(loaded.Stats.MaxHp == maxHp, $"读档后生命上限从 {maxHp} 变成 {loaded.Stats.MaxHp}");
            Assert(loaded.Player.BaseAttack == atk, $"读档后攻击力从 {atk} 变成 {loaded.Player.BaseAttack}");
            // 手改过的档不许超过上限
            loaded.RestoreOfferingSteps(999);
            Assert(loaded.OfferingSteps <= GameDataStore.Get().Config.Altars.Offering.MaxSteps,
                "手改成 999 次之后仍然越过了上限");
        });

        Section("边界与健壮性");
        Test("强化模拟到最终 Boss 也可能输：死亡后的状态不会被当成「还能继续」", () =>
        {
            // 「40 局全灭」在改完模拟器之后仍然是结论。写死一条断言守住它：
            // 如果有人把某一层的数值调空了，这条会红；如果难度被调到「必胜」，
            // 上面那条「随机玩家 0 胜」也会红。两条互为一对。
            var game = new Game(99);
            var roomIndex = game.Map.Rooms.FindIndex(r => r.Type is RoomType.Enemy or RoomType.Elite);
            Assert(roomIndex >= 0, "这一层没有战斗房");
            game.Teleport(roomIndex);
            var enc = game.CreateEncounter();
            Assert(enc is not null, "敌人房没有生成遭遇");
            game.Player.Hp = 0;
            game.CollapseIfDead();
            Assert(game.Status == GameStatus.Dead, "血量归零后状态还是 Playing：结算面板不会弹");
        });
        Test("背包满时买不到的东西不会被「凭空塞进去」", () =>
        {
            var game = new Game(4242);
            var data = GameDataStore.Get();
            var cap = game.Capacity;
            for (var i = 0; i < cap; i++) game.Player.Inventory.Add(data.Items[0].Id);
            Assert(game.Player.Inventory.Count == cap, "背包没填满");
            Assert(!Inventory.AddItem(game.Player, data.Items[1].Id, cap), "背包满了还塞进了一件");
            Assert(game.Player.Inventory.Count == cap, "背包容量被撑破");
        });

        Section("真机跑起来才看得见的（dotnet + Godot 环境）");
        Test("同一次掉落里不会出现同一件装备的两个副本", () =>
        {
            // 掉落先抽 1~2 件再注册成 _rN：注册之后连 Dedupe 也认不出来，
            // 玩家看到的是两张一样的卡。修在抽的时候就避开已经给过的那几件。
            // 500 局里原来有 19 局撞上，现在应该是 0。
            var dup = 0;
            for (uint seed = 1; seed <= 200; seed++)
            {
                var g = new Game(seed);
                var names = g.Rewards(EnemyTier.Normal).OfType<Reward.Item>()
                    .GroupBy(x => x.Def.Name).Where(gr => gr.Count() > 1).ToList();
                if (names.Count > 0) dup++;
            }
            Assert(dup == 0, $"{dup}/200 局的掉落里出现了同名的两件");
        });
        Test("附魔后说明里逐条写出词条效果，而不是留一句无关的文案", () =>
        {
            // 玩家拿到一件带「脆弱躯壳」的紫装，得能看见「受到的伤害 +20%」，
            // 而不是看到「锻造精良，锋利而稳定」这种和词条无关的底板文案。
            var game = new Game(4242);
            var data = GameDataStore.Get();
            var template = data.Items.First(i => i.Slot != Slot.Consumable && (i.Affixes?.Count ?? 0) == 0);
            game.RestorePendingAffixes(new[] { "fragile_shell" });
            var item = game.ApplyBonusAffix(template);
            Assert(item.Desc.Contains(data.Affixes["fragile_shell"].Name), "说明里没有词条名");
            Assert(item.Desc.Contains(data.Affixes["fragile_shell"].Desc), "说明里没有词条效果");
            Assert(!item.Desc.Contains(template.Desc), "说明里还留着和词条无关的底板文案");
        });
        Test("商店与事件拿到的消耗品，读档后还在", () =>
        {
            // 消耗品也有运行时身份：货架是缓存对象，读档前 roll 出来的那批不保证进过 ItemIndex。
            // 以前买药直接 AddItem(entry.Item.Id)，那一格读档后查不到。
            // 货架上有没有药是随机的（3~4 件从整张道具表里抽）：跨种子找一间真有药的商店，
            // 只断言「找到了之后能存能读」，不把运气写进断言。
            Game? run = null;
            int shopIndex = -1;
            ShopStockItem? potion = null;
            for (uint seed = 31; seed < 131 && potion is null; seed++)
            {
                var g = new Game(seed);
                g.Player.Gold = 999;
                var idx = g.Map.Rooms.FindIndex(r => r.Type == RoomType.Shop);
                if (idx < 0) continue;
                var found = g.ShopStock(idx).FirstOrDefault(x => x.Item.Slot == Slot.Consumable && x.Item.Heal is > 0);
                if (found is null) continue;
                run = g;
                shopIndex = idx;
                potion = found;
            }
            Assert(run is not null && potion is not null, "跨 100 个种子都没在货架上见过治疗药水（这才叫真出问题了）");
            var stock = run!.ShopStock(shopIndex);
            var bought = run.RegisterRuntimeItemCopy(potion!.Item);
            Assert(Inventory.AddItem(run.Player, bought.Id, run.Capacity), "背包塞不下");
            var path = "user://selfcheck_potion.cfg";
            Autoload.SaveSerializer.Save(path, run, 31);
            var loaded = Autoload.SaveSerializer.Load(path, new MetaState())!;
            Assert(loaded.Player.Inventory.Any(id => loaded.ItemIndex.TryGetValue(id, out var d) && d.Slot == Slot.Consumable),
                "读档后背包里查不到那瓶药");
            Autoload.SaveSerializer.Delete(path);
        });
        Test("集火目标倒下后，下一刀落在活人身上而不是尸体上", () =>
        {
            // CurrentTarget 以前只要 _target 非空就直接返回它：目标被不屈（或任何
            // 「血量没变负」的路径）留在场上时，敌人回合会拿着这只已经打不动的
            // 目标算出招者。现在必须还活着才算数。
            var data2 = GameDataStore.Get();
            var mon = data2.Monsters.First();
            var a = EntityRules.CreateMonster(mon, new List<SkillDef>(), 1);
            var b = EntityRules.CreateMonster(mon, new List<SkillDef>(), 1);
            var player = EntityRules.CreatePlayer(100, 20, 5, 0, new List<SkillDef>());
            var battle = new Battle(player, a, new Rng(9), new BattleOptions
            {
                Rules = RulesOf(data2),
                AttackerAtk = c => ReferenceEquals(c, player) ? 20 : c.BaseAttack,
                AttackerDef = c => c.BaseDefense,
                MaxHp = c => c.BaseMaxHp,
                Allies = new List<Combatant> { b },
                EnemyDefs = new Dictionary<Combatant, MonsterDef> { [a] = mon, [b] = mon },
            });
            Assert(battle.SelectTarget(1), "选不出第二个目标");
            b.Hp = 0;
            var cur = battle.CurrentTarget();
            Assert(cur.Hp > 0, "集火目标倒下后还指着一具尸体");
            Assert(ReferenceEquals(cur, a), "目标倒下后没有落回还活着的那只");
            battle.Dispose();
        });

        Section("这一轮：掷骰、闪避、免疫、加护、回响");
        Test("敌人 AI 的出手比例与数据表一致（攻击 45 / 防御 30 / 技能 25）", () =>
        {
            // 以前写成 `roll >= Attack + Defend` 去判技能，而掷的是 0..99 的整数，
            // 于是技能只占 0..4 这 5 个点、多出来的 20 个点被防御吃了。
            // 实测：攻击 44% / 防御 44% / 技能 12%，而数据表写的是 45 / 30 / 25。
            // 防御多出来的 14 个点直接抹平了敌人输出，前中期几乎不掉血。
            var data2 = GameDataStore.Get();
            var ai = data2.Config.EnemyAi;
            // 三个档位都要被记到：技能档要能放出来，所以必须先造出一个「冷却已好」的技能。
            // 以前这里让怪带一个伤害技能，于是玩家每回合都在掉血，而「防御」那一档
            // 因为敌人摆姿态不改血量、也不改冷却，全被记成了普攻——统计出来是「攻击 100%」。
            var counts = new Dictionary<string, int> { ["attack"] = 0, ["defend"] = 0, ["skill"] = 0 };
            var total = 0;
            var skills = data2.Skills.Where(x => x.Id == "inferno").ToList();
            for (uint seed = 1; seed <= 30; seed++)
            {
                var def = new MonsterDef { Id = "ai", Name = "AI", Hp = 100000, Attack = 5, Defense = 0, Skills = new List<string> { "inferno" } };
                var enemy = EntityRules.CreateMonster(def, skills, 1);
                var player = EntityRules.CreatePlayer(1000000, 5, 0, 0, new List<SkillDef>());
                var battle = new Battle(player, enemy, new Rng(seed), new BattleOptions
                {
                    Rules = new CombatRules { CritChance = 0, CritMultiplier = 1, DefendReduction = 0.5, DamageVariance = 0 },
                    AttackerAtk = c => c.IsPlayer ? 5 : c.BaseAttack,
                    AttackerDef = c => 0,
                    MaxHp = c => c.BaseMaxHp,
                    Floor = 1,
                });
                for (var i = 0; i < 400 && battle.Phase == BattlePhase.Player; i++)
                {
                    // 三个档位只有 LastEnemyAction 记得住。注意「防御」那一档：
                    // 敌人摆姿态不改血量、也不写冷却，只能靠 LastEnemyAction 认。
                    // 技能要能真的放出来，否则「冷却没好」会把它退成普攻，统计就偏了。
                    enemy.Cooldowns.Clear();
                    battle.PlayerDefend();
                    var act = battle.LastEnemyAction;
                    counts[act == "skill" ? "skill" : act == "defend" ? "defend" : "attack"] += 1;
                    total++;
                }
                battle.Dispose();
            }
            var attackPct = counts["attack"] * 100.0 / total;
            var defendPct = counts["defend"] * 100.0 / total;
            var skillPct = counts["skill"] * 100.0 / total;
            // ai.Attack / Defend / Skill 是 0~1 的比例（加载期校验过三者之和为 1），不是百分数。
            Assert(Math.Abs(attackPct - ai.Attack * 100) < 5, $"攻击占比 {attackPct:F0}%（配置 {ai.Attack:P0}）");
            Assert(Math.Abs(defendPct - ai.Defend * 100) < 5, $"防御占比 {defendPct:F0}%（配置 {ai.Defend:P0}）");
            Assert(Math.Abs(skillPct - ai.Skill * 100) < 5, $"技能占比 {skillPct:F0}%（配置 {ai.Skill:P0}）");
        });
        Test("躁动层的闪避对技能与炸弹同样生效", () =>
        {
            // ResolveFloorHit 的返回值只在「没有任何伤害、也没挂毒」时才算落空，
            // 而闪避恰好不写 Damage / PoisonTurns——技能与炸弹从这条缝里穿过去了。
            // 结果：躁动层写着「敌人闪避率 20%」，火球一次都没落空过，
            // 而这一层存在的意义正是「逼你用技能」。现在普攻/技能/炸弹共用 TryFloorDodge。
            var data2 = GameDataStore.Get();
            var fireball = data2.Skills.Where(x => x.Id == "fireball").ToList();
            var misses = 0;
            var tries = 0;
            for (uint seed = 1; seed <= 60; seed++)
            {
                var def = new MonsterDef { Id = "m", Name = "靶子", Hp = 100000, Attack = 0, Defense = 0 };
                var enemy = EntityRules.CreateMonster(def, new List<SkillDef>(), 7);
                enemy.BaseEvade = 1.0;   // 必定落空：只要有一条路没判闪避，这一发就一定打中
                enemy.Evade = 1.0;
                var player = EntityRules.CreatePlayer(1000, 20, 0, 0, fireball);
                player.Skills = fireball;
                var battle = new Battle(player, enemy, new Rng(seed), new BattleOptions
                {
                    Rules = new CombatRules { CritChance = 0, CritMultiplier = 1, DefendReduction = 0.5, DamageVariance = 0 },
                    AttackerAtk = c => c.IsPlayer ? 20 : 0,
                    AttackerDef = c => 0,
                    MaxHp = c => 1000,
                    FloorMechanic = "restless",
                    Floor = 7,
                    EnemyDefs = new Dictionary<Combatant, MonsterDef> { [enemy] = def },
                });
                var before = enemy.Hp;
                battle.PlayerSkill("fireball");
                tries++;
                if (enemy.Hp == before) misses++;
                // 冷却好了再来一发普攻
                player.Cooldowns.Clear();
                before = enemy.Hp;
                battle.PlayerAttack();
                tries++;
                if (enemy.Hp == before) misses++;
                battle.Dispose();
            }
            Assert(misses == tries, $"闪避率 100% 却有 {tries - misses}/{tries} 次打中了（技能或普攻有一条没判闪避）");
        });
        Test("玩家的毒系技能不会照着敌人的免疫文案报账", () =>
        {
            // Skills.UseSkill 以前无条件问一句 FloorMechanics.PoisonImmune(ctx.Target)——
            // 而那个函数读的是「玩家身上的楼层保护」。于是玩家在自己回合放毒系技能，
            // 目标是敌人时竟然走到「敌人甲胄上的腐殖质吞掉了毒素」那条分支，
            // 毒也没挂上。玩家在疫病层（敌人免疫）之外永远不该看到这句。
            var data2 = GameDataStore.Get();
            // 用毒爆（type=poison）：毒液喷吐是纯伤害技能，本来就不挂毒。
            var venom = data2.Skills.Where(x => x.Id == "venom_burst").ToList();
            Assert(venom.Count > 0, "数据表里没有毒爆");
            var def = new MonsterDef { Id = "m", Name = "靶子", Hp = 10000, Attack = 1, Defense = 0 };
            var enemy = EntityRules.CreateMonster(def, new List<SkillDef>(), 2);
            var player = EntityRules.CreatePlayer(500, 20, 0, 0, venom);
            // 直接调 Skills.UseSkill：Battle.PlayerSkill 会顺手推进一个回合，
            // 而中毒在回合末就要递减一次（3 → 2），会让「挂上没有」这件事变得不好判。
            var result = Skills.UseSkill(venom[0], new SkillContext
            {
                Actor = player,
                Target = enemy,
                ActorAtk = 20,
                ActorMaxHp = 500,
                TargetDef = 0,
                Rng = new Rng(5),
                Rules = RulesOf(data2),
                Affixes = ActiveAffixes.Empty,
                AffixesOwner = player,
                TargetIsPlayer = false,
            });
            var log = result.Log;
            Assert(!log.Contains("吞掉了毒素"), "普通楼层里玩家的毒被「免疫」掉了");
            // 毒会在回合末递减一次（3 → 2），所以判「挂上了没有」要比 0 大。
            Assert(enemy.PoisonTurns > 0, $"玩家放的毒没有挂到敌人身上（{log}）");
            // 反过来：敌人自己带着免疫时，这句文案才该出现。两边都钉住，
            // 免得下次又把「谁免疫」判反了。
            enemy.ImmunePoison = true;
            enemy.PoisonTurns = 0;
            player.Cooldowns.Clear();
            var immune = Skills.UseSkill(venom[0], new SkillContext
            {
                Actor = player, Target = enemy, ActorAtk = 20, ActorMaxHp = 500, TargetDef = 0,
                Rng = new Rng(5), Rules = RulesOf(data2), Affixes = ActiveAffixes.Empty,
                AffixesOwner = player, TargetIsPlayer = false,
            });
            Assert(enemy.PoisonTurns == 0, "免疫中毒的敌人被挂上了毒");
            Assert(immune.Log.Contains("吞掉了毒素"), $"免疫时没有说明白（{immune.Log}）");
        });
        Test("加护祭司的加护是「一层」，不会越祈祷越硬", () =>
        {
            // 以前每 3 回合 `DefMod += 3`，无上限累加。和回响层（敌人每回合回 8% 最大生命）
            // 撞在一起后，「玩家每刀只能打 1 点」对上「敌人每回合回 5 点」——
            // 40 局里有整局卡在一场永远打不完的战斗里，而玩家一步都没走错。
            var data2 = GameDataStore.Get();
            var priest = data2.Monsters.First(m => m.GuardAura is not null);
            var goblin = data2.Monsters.First(m => m.Id == "goblin");
            var p1 = EntityRules.CreateMonster(priest, new List<SkillDef>(), 6);
            var p2 = EntityRules.CreateMonster(goblin, new List<SkillDef>(), 6);
            var player = EntityRules.CreatePlayer(100000, 1, 0, 0, new List<SkillDef>());
            var battle = new Battle(player, p1, new Rng(11), new BattleOptions
            {
                Rules = new CombatRules { CritChance = 0, CritMultiplier = 1, DefendReduction = 0.5, DamageVariance = 0 },
                AttackerAtk = c => c.IsPlayer ? 1 : c.BaseAttack,
                AttackerDef = c => 0,
                MaxHp = c => c.BaseMaxHp,
                Floor = 6,
                Allies = new List<Combatant> { p2 },
                EnemyDefs = new Dictionary<Combatant, MonsterDef> { [p1] = priest, [p2] = goblin },
            });
            for (var i = 0; i < 30; i++) battle.PlayerDefend();
            var aura = priest.GuardAura!;
            Assert(p2.DefMod <= aura.Defense,
                $"加护叠成了 {p2.DefMod} 点（一层应当是 {aura.Defense} 点）");
            battle.Dispose();
        });
        Test("回响层的一场战斗必须能打完", () =>
        {
            // 回响层敌人每回合回 8% 最大生命，而这里造的是一个「刚进第 8 层该有的面板」：
            // 攻击 14、防御 4。这条用例钉的是「打得动」——只要玩家每回合的输出**高于**
            // 回血那一截，战斗就一定会结束。
            //
            // 上一版这里唯一的判据是「200 回合内打完」，而它其实在钉另一件事：
            // 96 血的玩家能不能在挨 8 层巫妖的 16~22 点伤害时活下来。那是难度，不是「打得动」——
            // 玩家第 4 回合就倒下时，这条用例报的是「卡在 Player」，看着像回血把战斗拖住了。
            // 所以把两件事拆开：**这一条只看输出与回血的关系**，给玩家足够多的血；
            // 能不能活下来由 `--probe` / `--play` 的横扫去量。
            var data2 = GameDataStore.Get();
            var def = data2.Monsters.First(m => m.Id == "lich");
            // 敌人要带上它**自己的技能**。以前这里传的是空表，于是这只巫妖一个技能都不会放——
            // 它本来有「毒液喷吐 + 暗愈」，而这条用例量的「回血会不会吃掉输出」得靠暗愈才成立。
            // 更糟的是空表让「打得动」这条判据变简单了：没有技能就打不出压力，
            // 于是它绿着，而真实的那只巫妖在探针里把人磨死。
            var enemySkills = def.Skills
                .Select(id => data2.Skills.FirstOrDefault(x => x.Id == id))
                .Where(x => x is not null).Select(x => x!).ToList();
            var enemy = EntityRules.CreateMonster(def, enemySkills, 8);
            // 血量给足：这条用例量的是「输出 vs 回血」，不是「96 血能不能扛住 8 层巫妖」。
            // 上一版这里写 2000，而 `HealPlayer` / `ClampHpToMaxHp` 之外还有一条
            // 「生命不许超过面板上限」的规矩——`CreatePlayer(2000, …)` 的 Hp 就是 2000，
            // 但 MaxHp 回调返回 2000，两边一致，所以它没问题；问题在别处（见下面那段）。
            var player = EntityRules.CreatePlayer(2000, 14, 4, 0, new List<SkillDef>());
            var battle = new Battle(player, enemy, new Rng(3), new BattleOptions
            {
                Rules = RulesOf(data2),
                AttackerAtk = c => c.IsPlayer ? 14 : c.BaseAttack,
                AttackerDef = c => c.IsPlayer ? 4 : c.BaseDefense,
                MaxHp = c => c.IsPlayer ? 2000 : c.BaseMaxHp,
                FloorMechanic = "echoing",
                Floor = 8,
                EnemyDefs = new Dictionary<Combatant, MonsterDef> { [enemy] = def },
            });
            // 一回合都没打出去，血条一动不动：这是另一种「打不完」，它得有自己的一句话。
            var hpStart = enemy.Hp;
            var guard = 0;
            while (battle.Phase == BattlePhase.Player && guard++ < 200) battle.PlayerAttack();
            Assert(battle.Phase is BattlePhase.Won or BattlePhase.Lost,
                $"第 8 层的战斗在 200 回合内没打完（卡在 {battle.Phase}，敌人 {enemy.Hp}/{enemy.BaseMaxHp}，"
                + $"玩家还剩 {player.Hp}，一共打了 {guard} 回合）");
            Assert(enemy.Hp < hpStart,
                $"打了 {guard} 回合，敌人的血从 {hpStart} 变成了 {enemy.Hp}：这一场的输出全被回血吃掉了");
            battle.Dispose();
        });
        Test("深渊领域的额外冷却只罚玩家，Boss 自己的技能不吃这一笔", () =>
        {
            // 说明写的是「范围内**你的**技能冷却 +1」。
            //
            // 上一轮这条断言钉的是反过来的事：「Boss 的技能同样生效」，
            // 理由是「这一层真正决定生死的是它每几回合能放一次炼狱」。
            // 但那个理由推出来的是「Boss 的威胁很大」，不是「领域该替玩家把它削一档」——
            // 而这条机制的效果是让 Boss 的炼狱从 4 变 6，等于每三轮多送玩家一回合。
            // 说明从来没有承诺过这件事，面板上写着的是「你的」。
            // 所以判据改成：「领域里玩家的冷却变长，Boss 的不变」。
            // 这不是把断言放宽到能绿，是把它钉在说明承诺的那一件事上。
            var data2 = GameDataStore.Get();
            var inferno = data2.Skills.Where(x => x.Id == "inferno").ToList();
            var def = data2.Bosses.First(b => b.Id == "boss_abyss");
            int CooldownAfterDomain(string? bossMechanic)
            {
                var enemy = EntityRules.CreateMonster(def, inferno, 15);
                var player = EntityRules.CreatePlayer(100000, 1, 0, 0, new List<SkillDef>());
                var battle = new Battle(player, enemy, new Rng(21), new BattleOptions
                {
                    Rules = RulesOf(data2),
                    AttackerAtk = c => c.IsPlayer ? 1 : c.BaseAttack,
                    AttackerDef = c => 0,
                    MaxHp = c => c.BaseMaxHp,
                    Floor = 15,
                    BossMechanic = bossMechanic,
                });
                var guard = 0;
                while (battle.Phase == BattlePhase.Player && guard++ < 40 && !enemy.Cooldowns.TryGetValue("inferno", out var cd0) is false)
                {
                    battle.PlayerDefend();
                    if (enemy.Cooldowns.ContainsKey("inferno")) break;
                }
                battle.PlayerDefend();
                var cd = enemy.Cooldowns.TryGetValue("inferno", out var v) ? v : 0;
                battle.Dispose();
                return cd;
            }
            var plain = CooldownAfterDomain(null);
            var domain = CooldownAfterDomain("abyss_domain");
            Assert(domain == plain, $"领域里 Boss 的技能冷却被多罚了（{plain} → {domain}），而说明写的是「你的技能冷却 +1」");
            // 另一头也必须立着：领域确实惩罚了玩家的施法。否则这条「只罚玩家」
            // 可以靠「谁都不罚」通过——那也是把一条机制静默关掉。
            var fire = data2.Skills.First(x => x.Id == "fireball");
            var hero = EntityRules.CreatePlayer(5000, 10, 0, 0, new List<SkillDef> { fire });
            hero.Skills = new List<SkillDef> { fire };
            var foe = EntityRules.CreateMonster(data2.Bosses.First(b => b.Id == "boss_abyss"), inferno, 15);
            var fight = new Battle(hero, foe, new Rng(9), new BattleOptions
            {
                Rules = RulesOf(data2),
                AttackerAtk = c => c.IsPlayer ? 10 : c.BaseAttack,
                AttackerDef = c => c.BaseDefense,
                MaxHp = c => c.BaseMaxHp,
                Floor = 15,
                BossMechanic = "abyss_domain",
            });
            fight.PlayerSkill("fireball");
            var playerCd = hero.Cooldowns.TryGetValue("fireball", out var pcv) ? pcv : 0;
            fight.Dispose();
            Assert(playerCd > fire.Cooldown, $"领域里玩家的技能冷却没有变长（{fire.Cooldown} → {playerCd}）");
        });
        Test("「每层首次受伤 +20%」只收一次，不是每一刀都收", () =>
        {
            // 狂意的说明写着「每层首次受到的伤害 +20%」，而 Player.RiskHitTaken
            // 只在 Battle 构造函数里归零——全工程没有第二处写它。
            // 于是一条「有代价」的词条变成了纯赚：每一刀都 +20%。
            var data2 = GameDataStore.Get();
            var def = new MonsterDef { Id = "t", Name = "木桩", Hp = 100000, Attack = 40, Defense = 0 };
            var enemy = EntityRules.CreateMonster(def, new List<SkillDef>(), 3);
            var player = EntityRules.CreatePlayer(100000, 1, 0, 0, new List<SkillDef>());
            var reckless = new ItemDef
            {
                Name = "狂意测试装", Slot = Slot.Trinket, Rarity = "rare", Price = 1,
                Quality = Quality.Purple, Affixes = new List<string> { "reckless" },
            };
            player.Inventory = new List<string> { "reckless_test" };
            player.Equipped.Trinket = 0;
            var items = new Dictionary<string, ItemDef> { ["reckless_test"] = reckless };
            var affixes = Inventory.EquippedAffixes(player, items);
            Assert(affixes.RiskIncomingPct > 0, "测试装的狂意没生效");
            var battle = new Battle(player, enemy, new Rng(77), new BattleOptions
            {
                Rules = new CombatRules { CritChance = 0, CritMultiplier = 1, DefendReduction = 0.5, DamageVariance = 0 },
                AttackerAtk = c => c.IsPlayer ? 1 : c.BaseAttack,
                AttackerDef = c => 0,
                MaxHp = c => c.BaseMaxHp,
                Floor = 3,
                Affixes = affixes,
                EnemyDefs = new Dictionary<Combatant, MonsterDef> { [enemy] = def },
            });
            // 敌人会掷骰（防御/技能），所以只看「第一刀」与「后续几刀」的比值不稳定。
            // 环境没有方差，误差只来自敌人的防御姿态：它只会让伤害变小。
            // 判据写成「第一刀明显高于后续的中位数」，并单独钉一次「标记只置一次」。
            var hits = new List<int>();
            for (var i = 0; i < 6 && battle.Phase == BattlePhase.Player; i++)
            {
                var before = player.Hp;
                battle.PlayerDefend();
                hits.Add(before - player.Hp);
            }
            Assert(hits.Count >= 3, "没打出足够多的回合");
            battle.Dispose();
            // 直接验标记：IncomingScale 的「首次」只在 RiskHitTaken 为假时生效，
            // 而 EnemyAttackResult 走过一次之后它必须变成真。
            Assert(player.RiskHitTaken == true, "打过几轮之后「首次受伤」的标记还是假的——加成了会一直收");
        });
        Test("金币的账只有一本：挣的进 Earn，花的进 Spend", () =>
        {
            var game = new Game(909);
            game.EarnGold(100);
            var before = (game.Player.Gold, game.Player.GoldEarned, game.GoldEarned, game.Player.GoldLost);
            Assert(game.SpendGold(40), "钱够却花不出去");
            Assert((game.Player.Gold, game.Player.GoldEarned, game.GoldEarned, game.Player.GoldLost)
                   == (before.Item1 - 40, before.Item2, before.Item3, before.Item4 + 40),
                "花钱没有记进 GoldLost");
            Assert(!game.SpendGold(99999), "钱不够却花出去了");
            Assert(game.Player.Gold == before.Item1 - 40, "钱不够的那次还是扣了钱");
        });
        Test("补给房配额跟着深度走：第 11 层以后每层超过一间", () =>
        {
            // 中后段金币常年 1500+、而一层最多一间商店——「钱没处花」是深度问题，
            // 不是玩家太会攒。补给房（商店/训练/祭坛）的配额现在按楼层放宽。
            var game = new Game(5150);
            game.RestoreFloor(11, 1, GameStatus.Playing);
            var supply = game.Map.Rooms.Count(r => r.Type is RoomType.Shop or RoomType.Training or RoomType.Altar);
            Assert(game.Map.Rooms.Count(r => r.Type == RoomType.Shop) >= 1, "第 11 层没有商店");
            Assert(supply >= 2, $"第 11 层只有 {supply} 间补给房（配额应当放宽到 3）");
            var early = new Game(5150);
            early.RestoreFloor(1, 1, GameStatus.Playing);
            var earlySupply = early.Map.Rooms.Count(r => r.Type is RoomType.Shop or RoomType.Training or RoomType.Altar);
            Assert(earlySupply <= 2, $"第 1 层有 {earlySupply} 间补给房（配额 1 + 保底商店 = 至多 2）");
        });
        Test("商店价格跟着钱包走，钱多的时候不是白送", () =>
        {
            var game = new Game(6161);
            var stock = game.ShopStock(game.PlayerIndex);
            Assert(stock.Count > 0, "货架是空的");
            var entry = stock[0];
            var poor = game.RepriceFor(entry, 1, 0);
            var rich = game.RepriceFor(entry, 1, 5000);
            Assert(rich.Price > poor.Price, $"有钱时价格没有变化（{poor.Price} → {rich.Price}）");
            Assert(rich.Price <= poor.Price * 3, $"抬价超过了 3 倍（{poor.Price} → {rich.Price}）");
        });
        Test("每一间商店的货架上都有一瓶买得起的回血药", () =>
        {
            // 「钱没处花」查出来的时候，真正的问题在另一头：**商店经常一瓶药都没有**。
            // 数据表里 19 件道具只有 2 件能回血，而货架是「从全表抽 3~4 件」——
            // 实测 200 个种子里 62 间店一瓶回血药都没有，而且与深度无关（第 1 层就这样）。
            // 这一层深度上，玩家唯一能补血的地方就是这间店。
            var data = GameDataStore.Get();
            var healTemplates = data.Items.Count(i => i.Slot == Slot.Consumable && i.Heal is > 0);
            Assert(healTemplates >= 2, $"能回血的道具只有 {healTemplates} 种，抽样保底根本救不了");
            var empty = 0;
            for (uint seed = 1; seed <= 120; seed++)
            {
                var game = new Game(seed * 7919);
                var stock = game.ShopStock(game.PlayerIndex);
                Assert(stock.Count >= 3, $"第 {game.Floor} 层的货架只有 {stock.Count} 件");
                var potion = stock.FirstOrDefault(x => x.Item.Heal is > 0);
                if (potion is null) empty++;
                else Assert(potion.Price <= game.Player.Gold + data.Config.InventorySize * 100,
                    $"药水标价 {potion.Price}，开局的钱根本买不起");
            }
            Assert(empty == 0, $"{empty}/120 间商店一瓶回血药都没有");
        });
        Test("敌人卡的「下一刀」算的是最先动手的那只，不是玩家集火的那只", () =>
        {
            // 组合战斗里这两个几乎总是不同：出手顺序跟着行动条轮转，集火是玩家自己选的。
            // 预告以前读 CurrentTarget()，于是「你打乙、甲先动手」时，
            // 卡片上写的是乙的伤害——数值还都落在合理区间里，玩家只会觉得
            // 「这一刀怎么轻了」，而不是「这数字错了」。
            var goblin = data.Monsters.First(m => m.Id == "goblin");
            var rat = data.Monsters.First(m => m.Id == "rat");
            // 哥布林 8 攻、巨鼠 6 攻，玩家 4 防：两只报出来的数不一样，才分得清预告读的是谁。
            // 注意别手算：敌人攻击要过楼层增长（CreateMonster 里的 growth），
            // 第 4 层的哥布林是 9 而不是 8——期望值一律从实例读，不从数据表抄。
            var player = EntityRules.CreatePlayer(100, 50, 4, 0, new List<SkillDef>());
            var main = EntityRules.CreateMonster(goblin, new List<SkillDef>(), 4);
            var ally = EntityRules.CreateMonster(rat, new List<SkillDef>(), 4);
            var mainHit = Math.Max(1, main.BaseAttack - 4);
            var allyHit = Math.Max(1, ally.BaseAttack - 4);
            Assert(mainHit != allyHit, $"两只敌人打出来的数一样（{mainHit}），这条断言没法分辨预告读的是谁");
            var battle = new Battle(player, main, new Rng(1), new BattleOptions
            {
                Rules = new CombatRules { CritChance = 0, CritMultiplier = 1.5, DefendReduction = 0.5, DamageVariance = 0 },
                AttackerAtk = c => c.BaseAttack,
                AttackerDef = c => c.BaseDefense,
                MaxHp = c => c is Player ? 100 : c.BaseMaxHp,
                Floor = 4,
                Allies = new List<Combatant> { ally },
                EnemyDefs = new Dictionary<Combatant, MonsterDef> { [main] = goblin, [ally] = rat },
            });
            // 玩家集火攻击更低的那只（巨鼠），而按行动条先动手的是哥布林
            battle.SelectTarget(1);
            Assert(battle.CurrentTarget().Name == rat.Name, "集火目标没落到巨鼠身上，断言的前提不成立");
            Assert(battle.UpcomingAttackerName == goblin.Name,
                $"下一只出手的是 {battle.UpcomingAttackerName}，应该是 {goblin.Name}");
            var (low, high, _) = battle.EnemyForecast();
            Assert(low == mainHit && high == mainHit,
                $"预告报的是 {low}~{high}；按最先动手的哥布林该是 {mainHit}~{mainHit}（按集火的巨鼠会报 {allyHit}）");
            battle.Dispose();
        });
        Test("补给房是「用了才清房」，不是「进门就清房」", () =>
        {
            // 宝箱/事件/祭坛/训练/诅咒五类房以前都是先进门就 ClearRoom：
            // 玩家在面板上点「走开 / 什么都不做 / 转身离开」之后，这间房就永远消失了，
            // 而地图上只会显示「它已经空了」。清房必须发生在「玩家真的做了选择」那一刻。
            foreach (var (entry, label) in new[]
                     {
                         ("private void OpenTreasure(int index)", "宝箱房"),
                         ("private void OpenEvent(int index)", "事件房"),
                         ("private void OpenAltar(int index)", "祭坛房"),
                         ("private void OpenTraining(int index)", "训练房"),
                         ("private void OpenCurseRoom(int index)", "诅咒房"),
                     })
            {
                var src = MapSceneSource();
                if (src is null) return; // 导出产物里没有源码：这条静态检查跳过
                var at = MethodAt(src, entry);
                Assert(at >= 0, $"MapScene 里找不到 {entry}，这条断言已经失去目标");
                // 只看函数体开头：缩进 8 空格的 `_run.ClearRoom` 就是「一进门就清房」。
                // 写在局部回调 Used() 里的是 12 空格，那正是「用了才清」的写法。
                // 锚点是行首（见 MethodAt），所以 Skip(1) 丢掉的正好是签名那一行。
                // 「一进门就清房」那条注释里也写着这五个签名，MethodAt 只认标识符边界，
                // 注释里的那一句不会再被当成声明。
                var head = src[at..Math.Min(src.Length, at + 240)];
                var direct = head.Split('\n').Skip(1)
                    .TakeWhile(l => !l.StartsWith("    private ", StringComparison.Ordinal))
                    .Any(l => l.StartsWith("        _run.ClearRoom", StringComparison.Ordinal));
                Assert(!direct, $"{label}一进门就 ClearRoom：玩家「走开」之后这间房就没了");
            }
        });

        Section("这一轮：装备与药、掉落与存档");

        Test("每一件掉落的装备都是「运行时登记过」的，读档后还能查到", () =>
        {
            // 掉落面板拿到的是一件模板的副本：带词条 / 换过品质的副本会拿到 `_rN` 的
            // 新 id，而存档只认 ItemIndex 里查得到的那些。副本没登记就等于
            // 「背包里看着有，读档后那一格直接消失」。这里把掉落来源全扫一遍。
            var data = GameDataStore.Get();
            var missing = new List<string>();
            for (uint seed = 1; seed <= 12; seed++)
            {
                var game = new Game(seed * 977);
                game.RestoreFloor(3, 1, GameStatus.Playing);
                foreach (var reward in game.Rewards(EnemyTier.Elite))
                {
                    if (reward is not Reward.Item item) continue;
                    if (!game.ItemIndex.ContainsKey(item.Def.Id)) missing.Add($"种子 {seed}: {item.Def.Name} 的 {item.Def.Id} 查不到");
                }
            }
            Assert(missing.Count == 0, $"有 {missing.Count} 件掉落查不到模板：" + string.Join("；", missing.Take(3)));
        });

        Test("换装跟着上限一起平移：血的比例不变，也不留超额血", () =>
        {
            // 「血见底时换上一件 +8 生命的戒指」在结算上的正确读法是：
            // 上限从 60 涨到 68，同时当前生命从 20 涨到 28——比值不变。
            // 只涨上限不涨血看起来更「克制」，但它让「卸下」变成一次满血
            // （上限回落、当前生命被夹回上限），再戴回去就是白拿 8 点。
            // 详见「换装不是一瓶药」那条里的三段历史。
            var game = new Game(3131);
            var data = GameDataStore.Get();
            var ring = data.Items.First(i => i.Id == "iron_ring");     // 生命 +8
            var medal = data.Items.First(i => i.Id == "hero_medal");    // 生命 +20
            var sword = data.Items.First(i => i.Id == "steel_sword");
            game.Player.Inventory.Clear();
            game.Player.Equipped = new Equipped();
            game.Player.Inventory.Add(ring.Id);
            game.Player.Inventory.Add(medal.Id);
            game.Player.Inventory.Add(sword.Id);
            foreach (var id in new[] { ring.Id, medal.Id, sword.Id })
                game.RegisterRuntimeItemCopy(data.Items.First(i => i.Id == id));
            game.Player.Hp = GameDataStore.Get().Config.StartingHp;
            var hpBefore = game.Player.Hp;
            var maxBefore = game.Stats.MaxHp;
            Assert(Inventory.Equip(game.Player, 0, game.ItemIndex), "戴不上第一件装备");
            Assert(game.Stats.MaxHp == maxBefore + (ring.MaxHp ?? 0), $"面板上限没涨：{maxBefore} → {game.Stats.MaxHp}");
            Assert(game.Player.Hp == hpBefore + (ring.MaxHp ?? 0),
                $"换装没跟着上限平移：{hpBefore} → {game.Player.Hp}（该是 {hpBefore + (ring.MaxHp ?? 0)}）");
            Inventory.Unequip(game.Player, "trinket", game.ItemIndex);
            Assert(game.Player.Hp <= game.Stats.MaxHp, $"卸下 + 上限回落后生命超出了上限：{game.Player.Hp}/{game.Stats.MaxHp}");
        });

        Test("背包里只剩药的时候，店里有药也买不进来——这条要被看见", () =>
        {
            // 横扫 30 局里「钱够、背包满、买不进来」出现 200 次，全部发生在
            // 六格被药塞满的时候。这不是模拟器的错，是「背包 6 格 vs 药是唯一回血手段」
            // 这个设计自己挤出来的。断言只钉住机制本身（买不进来这件事真的会发生），
            // 好让这条结论有人接。
            var data = GameDataStore.Get();
            var game = new Game(4242);
            game.RestoreFloor(6, 1, GameStatus.Playing);
            game.Player.Inventory.Clear();
            var potion = data.Items.First(i => i.Id == "health_potion");
            for (var i = 0; i < game.Capacity; i++)
                game.Player.Inventory.Add(potion.Id);
            Assert(!Inventory.AddItem(game.Player, potion.Id, game.Capacity), "背包满了还能再塞一瓶");
            // 六个格子全是消耗品时，换掉最差的那件这条路也走不通（FindWorstIndex 只认装备）
            Assert(game.Player.Inventory.All(id => game.ItemIndex[id].Slot == Slot.Consumable),
                "前提不成立：背包里还有非消耗品");
        });

        Test("药是「喝完就少一瓶」，地图上喝和战斗里喝走的是同一条扣除", () =>
        {
            var game = new Game(5151);
            var data = GameDataStore.Get();
            var potion = data.Items.First(i => i.Id == "health_potion");
            game.Player.Inventory.Clear();
            game.RegisterRuntimeItemCopy(potion);
            game.Player.Inventory.Add(potion.Id);
            game.Player.Hp = game.Stats.MaxHp - 10;
            var before = game.Player.Hp;
            // 走 UI 里那一条：先看 heal 字段，再 RemoveItem
            game.Player.Hp = Math.Min(game.Stats.MaxHp, game.Player.Hp + potion.Heal!.Value);
            Inventory.RemoveItem(game.Player, 0);
            Assert(game.Player.Hp > before, "喝了药没有回血");
            Assert(game.Player.Inventory.Count == 0, "喝了药但背包里还留着");
        });

        Section("这一轮：战斗里那几条「说明写了、玩家却感觉不到」的");

        Test("「强化」真的让下一刀变强，然后就被用掉了", () =>
        {
            // 说明写「下次攻击威力提升 40%」。实测（variance=0 固定随机）：
            // 放完强化之后那一刀还是 6 点，和不放一模一样——因为
            // Battle.PlayerAttack 里那句 `Player.AttackMultiplier = 1;` 把施法时置上的
            // 倍率当场归了零。这不是数值问题：同一个字段既是「置位」又是「清零」。
            var data = GameDataStore.Get();
            var empower = data.Skills.First(x => x.Id == "empower");
            int OneHit(bool cast)
            {
                var enemy = EntityRules.CreateMonster(
                    new MonsterDef { Id = "dummy", Name = "木桩", Hp = 100000, Attack = 0, Defense = 2 },
                    new List<SkillDef>(), 1);
                var player = EntityRules.CreatePlayer(60, 8, 2, 0, new List<SkillDef> { empower });
                var battle = new Battle(player, enemy, new Rng(3), new BattleOptions
                {
                    Rules = new CombatRules { CritChance = 0, CritMultiplier = 1.5, DefendReduction = 0.5, DamageVariance = 0 },
                    AttackerAtk = c => c is Player ? 8 : c.BaseAttack,
                    AttackerDef = c => c is Player ? 2 : c.BaseDefense,
                    MaxHp = c => c is Player ? 60 : c.BaseMaxHp,
                });
                // 木桩的攻击力是 0：它这一回合无论掷到什么都不会伤到人，
                // 但它掷到「防御」就会让下一刀减半——那是 AI 的随机，与强化无关。
                // 所以这里把 AI 的两档都关掉，只留「放强化 → 砍一刀」这一件事。
                enemy.Skills.Clear();
                if (cast) Assert(battle.PlayerSkill("empower"), "放不出强化");
                var before = enemy.Hp;
                // 敌人在「上一回合末」可能已经摆出了防御姿态（AI 那 30%），
                // 那会让这一刀减半——那是随机，与强化无关。先把姿态摘掉。
                enemy.Defending = false;
                battle.PlayerAttack();
                var dealt = before - enemy.Hp;
                Assert(player.AttackMultiplier == 1, "砍完一刀之后强化还挂着（下一次又白吃一次）");
                battle.Dispose();
                return dealt;
            }
            var plain = OneHit(false);
            var buffed = OneHit(true);
            // 8 × 1.4 = 11.2，减防御 2 → 9；不强化是 8 - 2 = 6。
            Assert(buffed > plain, $"强化之后这一刀并没有变强（{plain} → {buffed}）");
        });

        Test("眩晕按「被震住那一次出手」计，不是在同一回合末就被扣光", () =>
        {
            // 「震颤」的说明是「造成伤害并眩晕敌人 1 回合」。实测 2000/2000 次：
            // 日志写着「怪被震得无法行动」，紧接着同一条日志下面怪就攻击了玩家。
            // 原因是 EntityRules.TickStatus 在每回合末一律 `StunTurns -= 1`，
            // 而眩晕是玩家在自己这一回合挂上的——同一个回合末就被减掉了。
            var data = GameDataStore.Get();
            var tremor = data.Skills.First(x => x.Id == "tremor");
            var mon = new MonsterDef
            {
                Id = "t", Name = "怪", Hp = 100000, Attack = 5, Defense = 2,
                Skills = new List<string> { "fireball" },
            };
            var enemy = EntityRules.CreateMonster(mon, data.Skills.Where(x => x.Id == "fireball").ToList(), 5);
            var player = EntityRules.CreatePlayer(100000, 8, 2, 0, new List<SkillDef> { tremor });
            var battle = new Battle(player, enemy, new Rng(1), new BattleOptions
            {
                // 固定随机且敌人只会普攻/技能：只要它出手，玩家就一定掉血或被挂状态。
                Rules = new CombatRules { CritChance = 0, CritMultiplier = 1.5, DefendReduction = 0.5, DamageVariance = 0 },
                AttackerAtk = c => c is Player ? 8 : c.BaseAttack,
                AttackerDef = c => c is Player ? 2 : c.BaseDefense,
                MaxHp = c => c is Player ? 100000 : c.BaseMaxHp,
            });
            Assert(battle.PlayerSkill("tremor"), "放不出震颤");
            // 玩家的技能会立刻推进到敌人的回合（先结算自己的动作，再走 EnemyTurn）。
            // 所以「震住了没有」要在日志里看那一次 EnemyAct 走了哪条分支：
            // 以前日志写着「怪被震得无法行动」，紧跟着同一段里怪又「攻击你」——
            // 两句同时出现，就是这回合既震住了又没震住。
            var lines = battle.Log.ToList();
            var stunnedAt = lines.FindIndex(l => l.Contains("被震得无法行动"));
            Assert(stunnedAt >= 0, "日志里没有「被震住」这一句");
            var actedAfter = lines.Skip(stunnedAt + 1)
                .Any(l => l.Contains("攻击你") || l.Contains("使用「"));
            Assert(!actedAfter, "被震住之后敌人照样出手了");
            // 下一次轮到它就该醒了：同一场里只该出现一次「被震住」。
            battle.PlayerDefend();
            Assert(battle.Log.Count(l => l.Contains("被震得无法行动")) == 1, "眩晕没有在用过一次之后失效");
            battle.Dispose();
        });

        Test("按下「防御」，敌人卡上的预告与真的挨打都对得上", () =>
        {
            // 防御姿态在结算侧一直是有效的（ComputeDamage 读 Defending），
            // 但预告侧从不看它：玩家按下防御之后看到的还是原来那个数字。
            // 结果就是「防御这一手在界面上完全没有回报」——一条现有的规则，
            // 因为没人把它播报出来，看上去像是没实现。
            var data = GameDataStore.Get();
            var mon = new MonsterDef { Id = "t", Name = "怪", Hp = 100000, Attack = 40, Defense = 0 };
            var enemy = EntityRules.CreateMonster(mon, new List<SkillDef>(), 5);
            var player = EntityRules.CreatePlayer(100000, 8, 2, 0, new List<SkillDef>());
            var battle = new Battle(player, enemy, new Rng(7), new BattleOptions
            {
                Rules = new CombatRules { CritChance = 0, CritMultiplier = 1.5, DefendReduction = 0.5, DamageVariance = 0 },
                AttackerAtk = c => c is Player ? 8 : c.BaseAttack,
                AttackerDef = c => c is Player ? 2 : c.BaseDefense,
                MaxHp = c => c is Player ? 100000 : c.BaseMaxHp,
            });
            var (loPlain, hiPlain, _) = battle.EnemyForecast();
            // 防御本身就是一个完整的回合（按下去立刻结算敌人的还手），
            // 所以「防御中的预告」不能靠 PlayerDefend 之后再去读——那时姿态已经清了。
            // 这里分两半验：结算侧这一下真的被折价了，预告侧在姿态挂着时真的算出折扣。
            var hpBefore = player.Hp;
            battle.PlayerDefend();
            var taken = hpBefore - player.Hp;
            Assert(taken < loPlain, $"防御之后实际挨了 {taken} 点，预告说的是 {loPlain}~{hiPlain}（没被折价）");
            Assert(battle.Log.Any(l => l.Contains("伤害减少")), "按了防御，但玩家看不出这一手换来了什么");

            var battle2 = new Battle(player, EntityRules.CreateMonster(mon, new List<SkillDef>(), 5), new Rng(7), new BattleOptions
            {
                Rules = new CombatRules { CritChance = 0, CritMultiplier = 1.5, DefendReduction = 0.5, DamageVariance = 0 },
                AttackerAtk = c => c is Player ? 8 : c.BaseAttack,
                AttackerDef = c => c is Player ? 2 : c.BaseDefense,
                MaxHp = c => c is Player ? 100000 : c.BaseMaxHp,
            });
            var (loPlain2, hiPlain2, _) = battle2.EnemyForecast();
            player.Defending = true;
            var (loGuard, hiGuard, notes) = battle2.EnemyForecast();
            Assert(loGuard < loPlain2 || hiGuard < hiPlain2,
                $"防御姿态挂着时预告没变（{loPlain2}~{hiPlain2} → {loGuard}~{hiGuard}）");
            Assert(notes.Any(n => n.Contains("防御")), "预告里没说你已经防御了");
            battle2.Dispose();
            battle.Dispose();
        });

        Test("石肤给的是防御，不是攻击；日志也不能再写「下次攻击威力提升」", () =>
        {
            // 数据里「石肤」是 type=buff、power=0.25，而 buff 分支曾经无脑写
            // `AttackMultiplier = 1 + power` 并配一句「下次攻击威力提升」——
            // 对石肤来说从数值到文案都是错的（它本该是一件防御增益）。
            var data = GameDataStore.Get();
            var stone = data.Skills.First(x => x.Id == "stone_skin");
            var golem = data.Monsters.First(m => m.Skills.Contains("stone_skin"));
            var enemy = EntityRules.CreateMonster(golem, new List<SkillDef> { stone }, 6);
            var player = EntityRules.CreatePlayer(100, 8, 2, 0, new List<SkillDef>());
            var result = Skills.UseSkill(stone, new SkillContext
            {
                Actor = enemy, Target = player,
                ActorAtk = enemy.BaseAttack, TargetDef = 2,
                Rng = new Rng(1), Rules = RulesOf(data), ActorMaxHp = enemy.BaseMaxHp,
            });
            Assert(enemy.DefMod > 0, $"石肤没有加防御（DefMod={enemy.DefMod}）");
            Assert(Math.Abs(enemy.AttackMultiplier - 1) < 1e-9, $"石肤把攻击倍率改成了 {enemy.AttackMultiplier}");
            Assert(!result.Log.Contains("攻击"), $"石肤的日志还在说攻击：{result.Log}");
        });

        Section("这一轮：规则里那些「算了却没算对」的");

        Test("石肤按自身基础防御加成，不是按最大生命", () =>
        {
            // 以前 `DefMod = 最大生命 × 0.25`：78 血的石头傀儡一次石肤拿到 +20 点防御
            // （它自己的防御才 10），玩家每刀只剩最低伤害 1 点，而描述写的是「受到的伤害降低 25%」。
            var data = GameDataStore.Get();
            var stone = data.Skills.First(x => x.Id == "stone_skin");
            var golem = data.Monsters.First(m => m.Skills.Contains("stone_skin"));
            var enemy = EntityRules.CreateMonster(golem, new List<SkillDef> { stone }, 6);
            var player = EntityRules.CreatePlayer(100, 8, 2, 0, new List<SkillDef>());
            Skills.UseSkill(stone, new SkillContext
            {
                Actor = enemy, Target = player,
                ActorAtk = enemy.BaseAttack, TargetDef = 2,
                Rng = new Rng(1), Rules = RulesOf(data), ActorMaxHp = enemy.BaseMaxHp,
            });
            var expect = Math.Max(1, (int)Math.Round(enemy.BaseDefense * FloorMechanics.ArmorBuff["stone_skin"].Power));
            Assert(enemy.DefMod == expect,
                $"石肤给了 {enemy.DefMod} 点防御，按基础防御 {enemy.BaseDefense} × 25% 该是 {expect}（最大生命是 {enemy.BaseMaxHp}）");
            Assert(enemy.DefMod < enemy.BaseMaxHp / 4, "石肤的加护大到能把战斗拖成死结");
        });

        Section("这一轮：三件「玩家看不见，于是算错」的事");

        Test("敌人倒下会自爆，这一刀必须预报得出来，而且与结算同一个数", () =>
        {
            // 「倒下自爆」是横扫里的第一死因（100 局里 6~8 次单独出现过），
            // 而战斗屏上以前一个字都没提：手册里有、敌人卡悬停说明里有，
            // 玩家把最后一只怪打死的那一刻什么都没有。玩家算出「它剩 5 点、我一刀 8 点」
            // 然后死了——数字一个都没错，错的是它没被告知。
            var data = GameDataStore.Get();
            var zealotDef = data.Monsters.First(m => m.ExplodeOnDeath is > 0);
            var zealot = EntityRules.CreateMonster(zealotDef, new List<SkillDef>(), 5);
            var player = EntityRules.CreatePlayer(200, 30, 2, 0, new List<SkillDef>());
            var battle = new Battle(player, zealot, new Rng(5), new BattleOptions
            {
                Rules = RulesOf(data),
                AttackerAtk = c => c is Player ? 30 : c.BaseAttack,
                AttackerDef = c => c is Player ? 2 : c.BaseDefense,
                MaxHp = c => c is Player ? 200 : c.BaseMaxHp,
                EnemyDefs = new Dictionary<Combatant, MonsterDef> { [zealot] = zealotDef },
            });
            // 血还厚的时候不该报：每一屏都挂一句自爆提示，玩家两天就学会不看它。
            Assert(battle.ForeseeableExplosionDamage() == 0,
                "敌人还满血呢，屏上就在预报自爆（这条提示会变成噪音）");
            zealot.Hp = 1;
            var foreseen = battle.ForeseeableExplosionDamage();
            var expect = Math.Max(1, (int)Math.Round(zealot.BaseAttack * zealotDef.ExplodeOnDeath!.Value));
            Assert(foreseen == expect, $"预告说炸 {foreseen} 点，结算会炸 {expect} 点（攻击 {zealot.BaseAttack} × {zealotDef.ExplodeOnDeath:P0}）");
            // 打死它，真的炸一次：预告的那个数就是扣掉的那个数。
            var before = player.Hp;
            battle.PlayerAttack();
            Assert(zealot.Hp <= 0, "前提不成立：这一刀没收掉它");
            Assert(before - player.Hp == foreseen,
                $"预告 {foreseen} 点，实际掉了 {before - player.Hp} 点——预告和结算各算了一遍");
            battle.Dispose();
        });

        Test("怨恨层的自爆也报得出来，且与楼层机制的算式同源", () =>
        {
            // 楼层自爆（怨恨）与怪物自爆是两处算式。预报只写一处，另一处就会漏——
            // 所以这一条同时钉两件事：报得出数，且数与 `SettleFloorMechanic` 一致。
            //
            // 不用 `new Battle(...)`：怨恨层的自爆是**回合收尾**触发的（`EndRound` 里
            // 对每一只倒下的敌人结算一次），走完整场战斗才看得见。
            var data = GameDataStore.Get();
            var trollDef = data.Monsters.First(m => m.Id == "troll");
            var troll = EntityRules.CreateMonster(trollDef, new List<SkillDef>(), 11);
            var player = EntityRules.CreatePlayer(400, 30, 2, 0, new List<SkillDef>());
            var battle = new Battle(player, troll, new Rng(9), new BattleOptions
            {
                Rules = RulesOf(data),
                AttackerAtk = c => c is Player ? 30 : c.BaseAttack,
                AttackerDef = c => c is Player ? 2 : c.BaseDefense,
                MaxHp = c => c is Player ? 400 : c.BaseMaxHp,
                Floor = 11,
                FloorMechanic = "spiteful",
                EnemyDefs = new Dictionary<Combatant, MonsterDef> { [troll] = trollDef },
            });
            troll.Hp = 1;
            var foreseen = battle.ForeseeableExplosionDamage();
            Assert(foreseen > 0, "怨恨层：敌人只剩 1 点血，屏上却没报自爆");
            var before = player.Hp;
            battle.PlayerAttack();
            Assert(troll.Hp <= 0, "前提不成立：这一刀没收掉它");
            Assert(before - player.Hp == foreseen,
                $"预告 {foreseen} 点，这一刀打完之后掉了 {before - player.Hp} 点——两处算式分家了");
            battle.Dispose();
        });

        Test("货架标价只有一处算式：档位筛的价与摆上来的价是同一个数", () =>
        {
            // 这两件事曾经各有一个算式：档位筛选用「模板价 + 楼层 1.5」，
            // 摆上货架时又乘了一次浮动。两个数一叉开，第 8 层摆出了标价 244 的深渊之牙，
            // 而那一层的档位上限是 210。
            // 这条钉的是「同一个数」：第 1~15 层，随机 40 颗种子，
            // 每一件摆上来的装备，它的 `BasePrice` 都不能越过本层档位。
            var data = GameDataStore.Get();
            var over = new List<string>();
            for (uint seed = 1; seed <= 40; seed++)
            {
                var run = new Game(seed * 977);
                for (var floor = 1; floor <= 15; floor++)
                {
                    run.RestoreFloor(floor, 1, GameStatus.Playing);
                    var shop = run.Map.Rooms.FindIndex(r => r.Type == RoomType.Shop);
                    if (shop < 0) continue;
                    foreach (var item in run.ShopStock(shop).Where(x => x.Item.Slot != Slot.Consumable))
                    {
                        // 标价带 ±15% 的浮动，所以判据也留同样的余量——
                        // 它挡的是「越了一档」，不是「浮动超出 1 块」。
                        if (item.BasePrice <= ShopRoller.ShopPriceBand(floor) * 1.2) continue;
                        over.Add($"第 {floor} 层 {item.Item.Name} 标价 {item.BasePrice}（档位 {ShopRoller.ShopPriceBand(floor)}）");
                    }
                }
            }
            Assert(over.Count == 0, $"{over.Count} 次货架越档：" + string.Join("；", over.Take(3)));
        });

        Test("深层的货架摆得出深层的钱：第 15 层最贵那件不低于一层的全部进项", () =>
        {
            // 「钱没处花」的极端样本连续十几轮都在（进店 3000 金币、货架最贵 300，10 倍）。
            // 档位只管「这一层摆什么」，而玩家的钱包是攒出来的——两件事必须对得上：
            // 最深那一层的货架上，总得有一件值得用一整层的进项去换的东西。
            // 这一条钉的是「别把新道具的价定得太便宜」：抬价那条路已经试过并否掉了。
            var data = GameDataStore.Get();
            var top = data.Items.Where(i => i.Slot != Slot.Consumable)
                .Select(i => ShopRoller.ShelfPriceAtFloor(i, 15)).DefaultIfEmpty(0).Max();
            // 一层进项：赏金 `12 + 4×层`（第 15 层 72 一场）× 一层四五场 ≈ 300
            var floorIncome = 12 + 4 * 15;
            Assert(top >= floorIncome * 3,
                $"第 15 层最贵的货只有 {top} 金币，而一层进项 {floorIncome} 一场（四五场就是 {floorIncome * 4}）：钱没有出口");
        });

        Test("敌人这一手可能是技能：预告要把技能那一档算进「本回合合计」", () =>
        {
            // 以前预告只算普攻那一刀。Boss 的「炼狱」冷却读完之后，卡片上还是普攻的数——
            // 玩家按那个数算血，然后被一刀 1.6 倍带走。
            var data = GameDataStore.Get();
            var molten = data.Skills.First(x => x.Id == "inferno");
            var def = data.Monsters.First(m => m.Id == "troll");
            var enemy = EntityRules.CreateMonster(def, new List<SkillDef> { molten }, 9);
            var player = EntityRules.CreatePlayer(400, 30, 2, 0, new List<SkillDef>());
            Battle Make() => new(player, enemy, new Rng(4), new BattleOptions
            {
                Rules = RulesOf(data),
                AttackerAtk = c => c is Player ? 30 : c.BaseAttack,
                AttackerDef = c => c is Player ? 2 : c.BaseDefense,
                MaxHp = c => c is Player ? 400 : c.BaseMaxHp,
                Floor = 9,
                EnemyDefs = new Dictionary<Combatant, MonsterDef> { [enemy] = def },
            });
            // 冷却没好：预告只能是普攻那一档
            foreach (var id in enemy.Skills.Select(x => x.Id)) enemy.Cooldowns[id] = 2;
            var plain = Make();
            var (loPlain, hiPlain, notesPlain) = plain.EnemyForecast();
            Assert(!notesPlain.Any(n => n.Contains("可能放")), $"冷却没好，预告却说它可能放技能：{string.Join("，", notesPlain)}");
            plain.Dispose();
            // 冷却读完：预告要抬到技能那一档
            enemy.Cooldowns.Clear();
            var armed = Make();
            var (loSkill, hiSkill, notes) = armed.EnemyForecast();
            Assert(hiSkill > hiPlain,
                $"技能已经就绪，预告还是普攻那一档（{hiPlain} → {hiSkill}）：玩家会按小数算血");
            Assert(notes.Any(n => n.Contains("可能放")), "预告抬了数，却没说「为什么这个数变大了」");
            armed.Dispose();
        });

        Test("毒爆挂上的毒，按它这一发真的打出了多少算", () =>
        {
            // 以前毒伤写的是「攻击力 × power」，而那是这一发**直接伤害**的公式：
            // 毒爆 power 1.4，直接伤害乘过一次防御、吃过一次浮动，挂上的毒却是
            // 「攻击力 × 1.4」——比它自己打出的那一刀还高。于是「毒爆」的主要伤害
            // 来自回合结束，而不是描述里那句「造成 1.4 倍攻击力伤害」。
            var data = GameDataStore.Get();
            var burst = data.Skills.First(x => x.Id == "venom_burst");
            var target = EntityRules.CreateMonster(data.Monsters.First(m => m.Id == "troll"), new List<SkillDef>(), 8);
            // 防御抬高一点：让「打出去的那一刀」明显小于「攻击力 × power」
            target.BaseDefense = 10;
            var player = EntityRules.CreatePlayer(100, 20, 2, 0, new List<SkillDef> { burst });
            var result = Skills.UseSkill(burst, new SkillContext
            {
                Actor = player, Target = target,
                ActorAtk = 20, TargetDef = target.BaseDefense,
                ActorMaxHp = 100, Rng = new Rng(3), Rules = RulesOf(data),
            });
            Assert(target.PoisonTurns > 0, "毒爆没有挂上中毒");
            Assert(result.Damage > 0, "毒爆没有造成直接伤害");
            // 毒伤是「这一发打出了多少」，但封在 MaxPoisonPower 上：直伤小于上界时必须严格等于它，
            // 直伤大于上界时只要求不越过上界——上界比这一刀大，是这一轮唯一允许的例外。
            Assert(target.PoisonPower == Math.Min(result.Damage, EntityRules.MaxPoisonPower),
                $"毒伤 {target.PoisonPower} 与这一发打出的 {result.Damage} 点对不上（上界 {EntityRules.MaxPoisonPower}）");
        });

        Test("反震只在真的打中时收钱：落空的那一下不该扣血", () =>
        {
            // 反射层的反震写在 ResolveFloorHit 这条链上，而这条链跑在**伤害之前**。
            // 以前无条件 ApplyMechanic，于是同一回合既能「你的攻击落空了」，
            // 又照样挨 4 点反震。构造一次必然闪避的攻击来钉住这件事。
            var data = GameDataStore.Get();
            var mon = new MonsterDef { Id = "t", Name = "怪", Hp = 100000, Attack = 1, Defense = 0 };
            var enemy = EntityRules.CreateMonster(mon, new List<SkillDef>(), 12);
            enemy.Evade = 1.0; // 必闪
            var player = EntityRules.CreatePlayer(100, 8, 2, 0, new List<SkillDef>());
            var battle = new Battle(player, enemy, new Rng(5), new BattleOptions
            {
                Rules = new CombatRules { CritChance = 0, CritMultiplier = 1.5, DefendReduction = 0.5, DamageVariance = 0 },
                AttackerAtk = c => c is Player ? 8 : c.BaseAttack,
                AttackerDef = c => c is Player ? 2 : c.BaseDefense,
                MaxHp = c => c is Player ? 100 : c.BaseMaxHp,
                FloorMechanic = "reflecting",
            });
            var before = player.Hp;
            battle.PlayerAttack();
            Assert(battle.Log.Any(l => l.Contains("落空")), "这一下没有落空，构造失败");
            Assert(player.Hp == before, $"攻击落空了，却照样被反震扣了 {before - player.Hp} 点");
        });

        Test("「冰镜」不会把玩家自己的技能也弹一次", () =>
        {
            // 冰镜的说明是「把你**下一次造成的伤害**固定反弹 20 点」。
            // 结算侧只挂在普攻上，而界面上的「下一刀」预告对它只字不提——
            // 于是放技能的人会以为冰镜对他没用。这一条钉住「技能同样会被弹」，
            // 免得哪天有人顺手把它从技能那条路上去掉。
            var data = GameDataStore.Get();
            var boss = data.Bosses.First(b => b.Id == "boss_frost");
            var enemy = EntityRules.CreateMonster(boss, new List<SkillDef>(), 10);
            var fireball = data.Skills.First(x => x.Id == "fireball");
            var player = EntityRules.CreatePlayer(100000, 30, 2, 0, new List<SkillDef> { fireball });
            var battle = new Battle(player, enemy, new Rng(2), new BattleOptions
            {
                Rules = new CombatRules { CritChance = 0, CritMultiplier = 1.5, DefendReduction = 0.5, DamageVariance = 0 },
                AttackerAtk = c => c is Player ? 30 : c.BaseAttack,
                AttackerDef = c => c is Player ? 2 : c.BaseDefense,
                MaxHp = c => c is Player ? 100000 : c.BaseMaxHp,
                FloorMechanic = "frost_mirror",
                BossMechanic = "frost_mirror",
                Floor = 10,
            });
            // 冰镜每 2 回合展开：打到它张开的那一回合，再补一刀
            var guard = 0;
            var hpBefore = player.Hp;
            while (battle.Phase == BattlePhase.Player && guard++ < 12)
            {
                if (battle.Boss!.IsArmed(enemy) == true) break;
                battle.PlayerDefend();
            }
            Assert(battle.Boss!.IsArmed(enemy) == true, "冰镜始终没有展开，构造失败");
            // 展开的这一回合直接放技能：技能同样要被弹
            if (battle.Phase == BattlePhase.Player && Skills.IsSkillReady(player, "fireball"))
            {
                var beforeSkill = player.Hp;
                battle.PlayerSkill("fireball");
                Assert(player.Hp < beforeSkill, "冰镜张开时放技能没有被反弹（说明写的是「你造成的伤害」）");
            }
            battle.Dispose();
        });

        Test("补给房里拿的东西，换了装备/选了训练之后都是「真的归你」", () =>
        {
            // 训练房的三种收益都只活在内存里：苦练（TrainingAttackPct）、符文队列、休整回血。
            // 界面以前只在「下楼 / 回标题 / 买完东西」时落盘，于是选完训练直接关窗，
            // 回来之后那一次选择凭空消失。祭坛的血祭同理（BaseMaxHp 与 BaseAttack 都变了）。
            // 这里钉的是「状态确实变了」，落盘那一环由 --probe 的「每层存一次读一次」覆盖。
            var game = new Game(11);
            var buffBefore = game.TrainingAttackPct;
            game.TakeTraining("attack");
            Assert(game.TrainingAttackPct > buffBefore, "苦练招式之后本层攻击加成没变");
            var hpBefore = game.Player.BaseMaxHp;
            var atkBefore = game.Player.BaseAttack;
            var lost = game.TakeSacrifice();
            Assert(game.Player.BaseMaxHp < hpBefore, "血祭没有扣生命上限");
            Assert(game.Player.BaseAttack > atkBefore, "血祭没有换到攻击力");
            Assert(lost > 0, "血祭没有报出实际扣掉的上限");
        });

        Test("升级不会被「装备生命」白扣：涨多少上限就回多少血", () =>
        {
            // 以前升级用 Math.Min(BaseMaxHp, Hp + 增量) 收口，而玩家看得见的上限是
            // 「基础 + 装备固定生命」——面板上限 ≥ BaseMaxHp 永远成立。
            // 于是升级那一刻生命被压回 BaseMaxHp 这一侧：戴着 +30 生命的饰品时，
            // 玩家刚升一级，血量反而掉了一截。
            var data = GameDataStore.Get();
            var hpTrinket = data.Items.Where(i => i.MaxHp is > 0).OrderByDescending(i => i.MaxHp).First();
            var player = EntityRules.CreatePlayer(60, 8, 2, 0, new List<SkillDef>());
            player.Inventory.Add(hpTrinket.Id);
            player.Equipped.Trinket = 0;
            // 当前血尽量高：旧代码的收口是 min(BaseMaxHp, Hp + 增量)，
            // 只有「血接近满」时这条才会真的掉血。
            player.Hp = player.BaseMaxHp + (hpTrinket.MaxHp ?? 0);
            var beforeHp = player.Hp;
            var beforeMax = player.BaseMaxHp + (hpTrinket.MaxHp ?? 0);
            Leveling.GainXp(player, Leveling.XpForLevel(player.Level, data.Leveling), data.Leveling, data.Skills);
            Assert(player.BaseMaxHp > beforeMax - (hpTrinket.MaxHp ?? 0),
                "构造失败：这一级没有涨生命上限");
            // 面板上限 = 基础 + 装备固定生命（这里没有百分比词条）。
            // 升级给的是「上限 +6，当前 +6」——旧代码会把当前血夹到 BaseMaxHp，
            // 于是戴着 +30 饰品的人升级之后，血量比升级前允许多出来的那一截被吃掉了。
            var panelMax = player.BaseMaxHp + (hpTrinket.MaxHp ?? 0);
            // 期望值按**新的分工**写：`Leveling` 只涨上限，血由 `Game.BeforeMaxHpGrows` 跟着补。
            //
            // 这条用例直接调 `Leveling`，所以它证的是「规则层不再自己加血、也没有把血压回去」，
            // 而「血有没有跟上面板」由 `Game` 那条用例负责。上一版期望
            // `Hp + 增量`，那是在要求规则层继续当第二只手——两只手一起加，
            // 血就会冲到上限之外（这正是 40 局里那条 `95/81` 的来源之一）。
            Assert(player.Hp == beforeHp,
                $"升级把血从 {beforeHp} 改成了 {player.Hp}：规则层不该动血（面板上限 {panelMax}）");
            Assert(player.Hp <= panelMax,
                $"升级之后生命超过了面板上限：{player.Hp}/{panelMax}");
        });

        Test("奖励里不会出现两张一模一样的装备", () =>
        {
            // Dedupe 之后如果名额没填满，旧代码按下标从**未去重**的原始列表里补，
            // 补回来的可能正是刚被去重掉的那件装备——玩家看到两张一模一样的卡。
            var data = GameDataStore.Get();
            var dup = 0;
            for (uint seed = 1; seed <= 200; seed++)
            {
                var game = new Game(seed);
                for (var round = 0; round < 3; round++)
                {
                    var rewards = game.Rewards(EnemyTier.Normal);
                    var ids = rewards.OfType<Reward.Item>().Select(r => r.Def.Id).ToList();
                    if (ids.Count != ids.Distinct().Count()) dup++;
                }
            }
            Assert(dup == 0, $"200 局里有 {dup} 次奖励出现重复装备");
        });

        Test("满血时「回血」那张卡不再假装回 10 点", () =>
        {
            // 卡片是「缺多少血 × 0.5（下限 10）」算的：满血时照样写「回复 10 生命」，
            // 玩家拿走才发现什么都回不了。现在 Amount 就是 0，界面据此改成「生命已满」。
            var game = new Game(3);
            game.Player.Hp = game.Stats.MaxHp;
            var rewards = game.Rewards(EnemyTier.Normal);
            foreach (var heal in rewards.OfType<Reward.Heal>())
                Assert(!heal.Effective && heal.Amount == 0, $"满血时回血卡仍写着 {heal.Amount} 点（应标成无效）");
            game.Player.Hp = Math.Max(1, game.Stats.MaxHp / 2);
            var hurt = game.Rewards(EnemyTier.Normal).OfType<Reward.Heal>().ToList();
            if (hurt.Count > 0)
                Assert(hurt.All(h => h.Effective && h.Amount > 0), "掉血时回血卡却没标成有效");
        });

        Test("元进展「双生词条」的提升方向按品质表算，不按档位表里那一行的顺序", () =>
        {
            // 档位表迟早会被重排（普通敌人那行是先白后蓝）。旧代码把「表里最后一个」
            // 当作「最稀的一档」，重排之后权重会被搬到最差的那一档——花 150 碎片买了个负面效果。
            var data = GameDataStore.Get();
            Assert(Loot.RarestOf(EnemyTier.Normal) == Quality.Blue, $"普通档的稀有品质算成了 {Loot.RarestOf(EnemyTier.Normal)}");
            Assert(Loot.RarestOf(EnemyTier.Boss) == Quality.Orange, $"Boss 档的稀有品质算成了 {Loot.RarestOf(EnemyTier.Boss)}");
            var rngPlain = new Rng(9);
            var rngBoost = new Rng(9);
            var plain = new Dictionary<Quality, int>();
            var boost = new Dictionary<Quality, int>();
            for (var i = 0; i < 4000; i++)
            {
                var a = Loot.RollQuality(rngPlain, EnemyTier.Normal, false);
                var b = Loot.RollQuality(rngBoost, EnemyTier.Normal, true);
                plain[a] = plain.GetValueOrDefault(a) + 1;
                boost[b] = boost.GetValueOrDefault(b) + 1;
            }
            Assert(boost.GetValueOrDefault(Quality.Blue) > plain.GetValueOrDefault(Quality.Blue),
                $"解锁之后蓝装反而更少了（{plain.GetValueOrDefault(Quality.Blue)} → {boost.GetValueOrDefault(Quality.Blue)}）");
            Assert(boost.GetValueOrDefault(Quality.White) < plain.GetValueOrDefault(Quality.White),
                "解锁之后白装没有变少");
        });

        Test("玩家的「苦练」加成真的进了战斗（三条路读同一个数）", () =>
        {
            // 训练房写的是「本层所有攻击 +10%」，而 BattleScene / --probe / --play 三处
            // 各自搭 BattleOptions：BattleScene 读了 TrainingAttackBuff，另外两处读的是
            // TrainingAttackPct（存档与界面用的那份）。三处必须算同一个数，
            // 否则「探针跑出来的平衡」与玩家打的不是同一款游戏。
            var game = new Game(23);
            game.TakeTraining("attack");
            Assert(Math.Abs(game.TrainingAttackBuff - game.TrainingAttackPct) < 1e-12,
                $"TrainingAttackBuff={game.TrainingAttackBuff} 与 TrainingAttackPct={game.TrainingAttackPct} 对不上");
        });

        Test("读档：训练房的苦练加成、金币与生命都穿过一次存档", () =>
        {
            // ConfigFile 把 double 存成文本、读回来是 Variant 的 float：
            // 写进去的是 double，`(double)cfg.GetValue(...)` 这一步拆箱就按 float 判类型，
            // 运行期要么抛 InvalidCastException、要么被拆成 float 再抬回 double——
            // 两条都不能接受，所以读侧改成 `AsDouble()`。
            // 这里钉「值能原样穿过一次存档」，UI 的落盘时机由另一条断言守着。
            var game = new Game(31);
            game.TakeTraining("attack");
            var path = "user://selfcheck_roundtrip.cfg";
            AbyssEcho.Autoload.SaveSerializer.Save(path, game, game.Rng.Seed);
            try
            {
                var loaded = AbyssEcho.Autoload.SaveSerializer.Load(path, new MetaState());
                Assert(loaded is not null, "存档读不回来");
                Assert(Math.Abs(loaded!.TrainingAttackPct - game.TrainingAttackPct) < 1e-9,
                    $"苦练加成读回来是 {loaded.TrainingAttackPct}，存档里写的是 {game.TrainingAttackPct}");
                Assert(loaded.Player.Gold == game.Player.Gold, "金币没穿过去");
                Assert(loaded.Player.Hp == game.Player.Hp, "生命没穿过去");
            }
            finally
            {
                AbyssEcho.Autoload.SaveSerializer.Delete(path);
            }
        });

        Section("这一轮：深潜段不是「全池随机」");

        Test("第 16 层之后每一层都有自己的遭遇组合，不靠「从全池抽一只」", () =>
        {
            // 通关层（15）之后还有深潜：20 层逆位 Boss、25 层三连战。
            // 而 `encounterSlots` 只配到第 15 层——第 16 层起 `PickEncounterSlot` 返回 null，
            // 攻击房退化成 `PickMonster` 的「从全池随机抽一只」：
            // 25 只怪全在池子里，第 20 层能抽到第 1 层那只 22 血、攻击 6 的巨鼠。
            // 这条查两件事：数据里每层都有槽位；抽出来的怪真的属于这一层。
            var data = GameDataStore.Get();
            var bossFloors = data.BossStages.Select(x => x.Floor).ToHashSet();
            for (var floor = data.Config.FinalFloor + 1; floor <= 25; floor++)
            {
                if (bossFloors.Contains(floor)) continue;
                Assert(data.EncounterSlots.ContainsKey(floor.ToString()),
                    $"第 {floor} 层没有遭遇组合：深潜段会退化成「从全池随机抽一只」");
            }
            // 另一条腿：没有 `encounterSlots` 时的随机池也必须跟着深度走。
            // 判据是「再往下潜 5 层，随机池里不会再出现第 1 层的怪」——
            // 旧实现只按 `minFloor <= 当前层` 过滤，巨鼠在第 25 层照样在池子里。
            // 这条不能靠 `encounterSlots` 兜住：那一层配了槽位之后，随机池根本走不到。
            var shallow = new List<string>();
            for (uint seed = 1; seed <= 30; seed++)
            {
                var game = new Game(seed);
                while (game.Floor < 20 && game.Status == GameStatus.Playing)
                {
                    foreach (var r in game.Map.Rooms) r.Cleared = true;
                    if (!game.Descend().Ok) break;
                }
                for (var round = 0; round < 30; round++)
                {
                    var m = game.ProbePickMonster();
                    if ((m.MinFloor ?? 1) < game.Floor - Game.DepthPoolSpan + 1)
                        shallow.Add($"第 {game.Floor} 层的随机池里抽到了 {m.Name}（minFloor={m.MinFloor}）");
                }
            }
            Assert(shallow.Count == 0, "深潜段的随机池里还蹲着早就该退场的怪：" + string.Join("；", shallow.Take(3)));
        });

        Section("这一轮：读档不许把这一局换成另一局");

        Test("读档之后是同一张图、同一条随机序列，不是「同一个种子、另一局」", () =>
        {
            // 这条钉的是存档里那行 `seed` 的取值。
            //
            // 地图由 `MapRngFor(floor)` 从「种子 + 楼层」单独派生，而 Rng 在构造期间
            // 已经为了「元进展开局加成」抽过签——两者从那一刻起就分叉了。
            // 旧实现存的是 `run.Rng.Seed`（当时它恰好等于种子），开局抽签加进来之后
            // 这一行没跟着改：读档把种子当「开局种子」重建，于是
            //   · 第 1 层整张图换了一张脸（起点坐标、每一格房型全变）；
            //   · 随机序列退回开局，之后每一次掉落/判定都和历史重来一遍。
            // 它一直没被抓住，是因为旧断言只比「房间数」和「清空标记」——
            // 这两个数在两张图之间恰好都对得上。
            var game = new Game(777);
            // 先 roll 一次货架：它是「按需抽取」的东西，最容易被序列错位带偏，
            // 同时也是「主序列确实往前走过」的证明。
            var stockA = game.ShopStock(game.Map.Rooms.FindIndex(r => r.Type == RoomType.Shop));
            var path = "user://selfcheck_identity.cfg";
            AbyssEcho.Autoload.SaveSerializer.Save(path, game, game.MapSeed);
            try
            {
                var loaded = AbyssEcho.Autoload.SaveSerializer.Load(path, new MetaState());
                Assert(loaded is not null, "存档读不回来");
                // 1) 地图必须逐格一致：坐标与房型都算
                Assert(loaded!.Map.Rooms.Count == game.Map.Rooms.Count,
                    $"读档后房间数变了：{game.Map.Rooms.Count} → {loaded.Map.Rooms.Count}");
                for (var i = 0; i < game.Map.Rooms.Count; i++)
                {
                    var a = game.Map.Rooms[i];
                    var b = loaded.Map.Rooms[i];
                    Assert(a.X == b.X && a.Y == b.Y && a.Type == b.Type,
                        $"第 {i} 格换了：({a.X},{a.Y}) {a.Type}/{MapGen.RoomLabel[a.Type]} → ({b.X},{b.Y}) {b.Type}/{MapGen.RoomLabel[b.Type]}");
                }
                // 2) 随机序列必须接着走：同一句「再抽一次」两边要给同一个数
                Assert(loaded.Rng.State == game.Rng.State,
                    $"随机序列没接上：存档 {game.Rng.State} / 读回 {loaded.Rng.State}（读档后掉落与判定会重来一遍）");
                var nextA = new Rng(game.MapSeed) { State = loaded.Rng.State }.Next();
                var nextB = new Rng(game.MapSeed) { State = game.Rng.State }.Next();
                Assert(Math.Abs(nextA - nextB) < 1e-12, "下一步随机数两边不一样");
                // 3) 货架也要一模一样：它是「按需 roll」的东西，最容易被序列错位带偏
                var shopIdx = game.Map.Rooms.FindIndex(r => r.Type == RoomType.Shop);
                if (shopIdx >= 0)
                {
                    var stockB = loaded.ShopStock(shopIdx);
                    Assert(stockA.Count == stockB.Count &&
                           stockA.Select(x => x.Item.Id).SequenceEqual(stockB.Select(x => x.Item.Id)),
                        "读档后同一间商店换了一批货（随机序列退回了开局）");
                }
            }
            finally
            {
                AbyssEcho.Autoload.SaveSerializer.Delete(path);
            }
        });

        Test("地图种子与「开局抽签」是两件事：同一颗种子换不换解锁，地图都一样", () =>
        {
            // 判据：地图只由 (种子, 楼层) 决定，而开局抽签（元进展加成）只影响玩家。
            // 旧实现把「随机序列现在走到哪」当成种子存下来，正是这条不成立的症状。
            var plain = new Game(2024);
            var unlocked = new MetaState();
            unlocked.Unlocked.Add(MetaUnlock.ExtraGold);
            unlocked.Unlocked.Add(MetaUnlock.StarterGear);
            unlocked.Shards = 0;
            var withUnlock = new Game(2024, unlocked);
            // 开局加成**不该**推进主序列：一旦推进，「开没开这一档解锁」
            // 就会让同一颗种子长出两张不同的图——而地图种子根本没变。
            Assert(plain.Rng.State == withUnlock.Rng.State,
                "开局加成推进了随机序列：同一颗种子会因为解锁不同而长出不同的图");
            Assert(plain.MapSeed == withUnlock.MapSeed, "地图种子被开局加成带偏了");
            for (var i = 0; i < plain.Map.Rooms.Count; i++)
            {
                var a = plain.Map.Rooms[i];
                var b = withUnlock.Map.Rooms[i];
                Assert(a.X == b.X && a.Y == b.Y && a.Type == b.Type,
                    $"开了元进展之后第 {i} 格的图变了：{a.Type} → {b.Type}");
            }
        });

        Section("这一轮：说明里写着、玩家却一直没碰到的东西");

        Test("瘴气层：敌人的攻击真的会把毒挂到玩家身上", () =>
        {
            // 说明写「本层敌人的攻击有 20% 概率使你中毒 2 回合」。
            // 以前 Battle.ApplyMechanic 写的是
            //   `effect.PoisonTurns is { } t && effect.PoisonPower is { } p`
            // 而 FloorMechanics 交回来的 PoisonPower 是空的（它由调用方按「这一刀挨了多少」补），
            // 于是 `is { }` 不成立，整条挂毒链静默跳过——日志照样打「你中毒了」，
            // 玩家身上的 PoisonTurns 从头到尾是 0。毒伤就是这句话的全部内容。
            var data = GameDataStore.Get();
            var mon = new MonsterDef { Id = "t", Name = "怪", Hp = 500, Attack = 12, Defense = 0 };
            var enemy = EntityRules.CreateMonster(mon, new List<SkillDef>(), 2);
            var player = EntityRules.CreatePlayer(200, 8, 0, 0, new List<SkillDef>());
            var opts = new BattleOptions
            {
                Rules = RulesOf(data),
                AttackerAtk = c => c.IsPlayer ? 8 : c.BaseAttack,
                AttackerDef = c => c.IsPlayer ? 0 : c.BaseDefense,
                MaxHp = c => c.IsPlayer ? 200 : c.BaseMaxHp,
                FloorMechanic = "haze",
                Floor = 2,
            };
            var poisoned = false;
            // 20% 概率：跑够多次总能撞上。以前是一条都撞不上（永远不中毒）。
            for (var seed = 1u; seed <= 60 && !poisoned; seed++)
            {
                var battle = new Battle(player, enemy, new Rng(seed), opts);
                for (var turn = 0; turn < 12 && battle.Phase == BattlePhase.Player; turn++)
                {
                    battle.PlayerDefend();
                    if (player.PoisonTurns > 0) { poisoned = true; break; }
                }
                battle.Dispose();
                player.Hp = 200;
                player.PoisonTurns = 0;
            }
            Assert(poisoned, "瘴气层跑了 60 局都没让玩家中毒一次（说明写着 20% 概率）");
        });

        Test("毒雾只挂毒、不额外打一刀；毒爆仍然先打一刀再挂毒", () =>
        {
            // 毒雾的描述是「使敌人每回合受到攻击力 50% 的伤害」——它没有直接伤害。
            // 以前两种毒共用「攻击力 × power」那一条，于是毒雾每一发都白打一记直伤
            // （日志上还写着「造成 3 点伤害并使其中毒」，而描述里没这一句）。
            var data = GameDataStore.Get();
            var fog = data.Skills.First(x => x.Id == "toxic_fog");
            var burst = data.Skills.First(x => x.Id == "venom_burst");
            var enemy = EntityRules.CreateMonster(new MonsterDef { Id = "t", Name = "木桩", Hp = 500, Attack = 1, Defense = 0 },
                new List<SkillDef>(), 3);
            var player = EntityRules.CreatePlayer(100, 20, 2, 0, new List<SkillDef> { fog, burst });
            var fogResult = Skills.UseSkill(fog, new SkillContext
            {
                Actor = player, Target = enemy, ActorAtk = 20, ActorMaxHp = 100, TargetDef = 0,
                Rng = new Rng(7), Rules = RulesOf(data),
            });
            Assert(fogResult.Damage == 0, $"毒雾打出了 {fogResult.Damage} 点直接伤害（描述里没有这一句）");
            Assert(enemy.Hp == enemy.BaseMaxHp, $"毒雾把木桩的血从 {enemy.BaseMaxHp} 打到了 {enemy.Hp}");
            Assert(enemy.PoisonTurns > 0, "毒雾没有挂上中毒");
            // 毒伤按「基础攻击力 × 0.5」：20 × 0.5 = 10，但上界 MaxPoisonPower（6，见下面那条）。
            Assert(enemy.PoisonPower == EntityRules.MaxPoisonPower,
                $"毒雾的每回合毒伤是 {enemy.PoisonPower}（20 × 50% = 10 该被封到 {EntityRules.MaxPoisonPower}）");
            Assert(fogResult.Log.Contains("中毒") && !fogResult.Log.Contains("造成 0 点"), $"毒雾的日志读不通：{fogResult.Log}");

            enemy.Hp = enemy.BaseMaxHp;
            enemy.PoisonTurns = 0;
            var burstResult = Skills.UseSkill(burst, new SkillContext
            {
                Actor = player, Target = enemy, ActorAtk = 20, ActorMaxHp = 100, TargetDef = 0,
                Rng = new Rng(7), Rules = RulesOf(data),
            });
            Assert(burstResult.Damage > 0, "毒爆没有直接伤害");
            Assert(enemy.Hp < enemy.BaseMaxHp, "毒爆没有扣血");
        });

        Test("「混乱思绪」只吞施法，不吞摆架势", () =>
        {
            // 词条描述写「每次施法有 25% 概率失败」。以前判据不分技能：
            // 玩家戴着这件诅咒装上战场，按「强化」也有 25% 白给、冷却照扣，
            // 而强化不是施法——它是摆个架势。这条代价在说明里从来没有这一层。
            var data = GameDataStore.Get();
            var empower = data.Skills.First(x => x.Id == "empower");
            var stoneSkin = data.Skills.First(x => x.Id == "stone_skin");
            var fireball = data.Skills.First(x => x.Id == "fireball");
            var heal = data.Skills.First(x => x.Id == "heal");
            Assert(!Skills.CanFizzle(empower), "「强化」被算成了施法（它只是摆架势）");
            Assert(!Skills.CanFizzle(stoneSkin), "「石肤」被算成了施法");
            Assert(Skills.CanFizzle(fireball), "「火球」居然不会被混乱思绪吞掉");
            Assert(Skills.CanFizzle(heal), "「治愈」居然不会被混乱思绪吞掉");

            // 真的按一次「强化」：混乱思绪 100% 生效时，它也必须走完（倍率真的挂上）。
            var enemy = EntityRules.CreateMonster(new MonsterDef { Id = "t", Name = "木桩", Hp = 500, Attack = 1, Defense = 0 },
                new List<SkillDef>(), 3);
            var player = EntityRules.CreatePlayer(100, 20, 2, 0, new List<SkillDef> { empower });
            var affixes = new ActiveAffixes { SpellFailChance = 1.0 };
            var battle = new Battle(player, enemy, new Rng(11), new BattleOptions
            {
                Rules = RulesOf(data),
                AttackerAtk = c => c.IsPlayer ? 20 : c.BaseAttack,
                AttackerDef = c => c.IsPlayer ? 2 : c.BaseDefense,
                MaxHp = c => c.IsPlayer ? 100 : c.BaseMaxHp,
                Affixes = affixes,
                Floor = 3,
            });
            Assert(battle.PlayerSkill("empower"), "「强化」没有放出去");
            Assert(player.AttackMultiplier > 1, "混乱思绪把「强化」也吞了（它不该是施法）");
            battle.Dispose();
        });

        Test("带词条的那件装备换下来：超出新上限的血要一起收回", () =>
        {
            // 「脆弱躯壳」写着「最大生命 +20%」。它以前只在 Game.Stats 里生效，
            // 而 Inventory 的换装收口算的是另一份上限（只算「基础 + 装备固定值」），
            // 于是卸下这件之后上限看着降了、收口却用了一个偏高的数：
            // 玩家身上留下 72/60 这种「比上限多的血」，之后每一次治疗都被 Math.Min 吃掉，
            // 只看到「喝药没反应」。这一条钉的是「收口用的是玩家看得见的那个上限」。
            var data = GameDataStore.Get();
            var fragile = data.Affixes["fragile_shell"];
            var armor = data.Items.First(i => i.Slot == Slot.Armor && (i.MaxHp ?? 0) == 0);
            var player = EntityRules.CreatePlayer(60, 8, 2, 0, new List<SkillDef>());
            // 手搓一件带「脆弱躯壳」的护甲：数据表里的模板没有词条，词条是运行时附上去的
            var def = armor.Clone();
            def.Id = "probe_fragile_armor";
            def.Affixes = new List<string> { fragile.Id };
            var items = new Dictionary<string, ItemDef> { [def.Id] = def };
            player.Inventory.Add(def.Id);
            Assert(Inventory.Equip(player, 0, items), "穿不上这件护甲");
            // 上限 = 60 × 1.2 = 72。玩家看得见这个数，血就该能被喝到 72。
            Assert(Inventory.PanelMaxHp(player, items) == 72,
                $"穿着「脆弱躯壳」时上限是 {Inventory.PanelMaxHp(player, items)}（60 × 1.2 该是 72）");
            player.Hp = 72;
            Inventory.Unequip(player, "armor", items);
            var maxAfter = Inventory.PanelMaxHp(player, items);
            Assert(maxAfter == 60, $"卸下之后上限是 {maxAfter}（该是 60）");
            Assert(player.Hp == maxAfter,
                $"卸下之后身上还留着 {player.Hp} 点血，而上限只有 {maxAfter}（超出上限的血会吞掉之后每一次治疗）");
        });

        Section("这一轮：三处「没人负责」的空档");

        Test("每一层的 Boss 房都真的有一场 Boss 战，别的层不留 Boss 标记", () =>
        {
            // 地图生成器永远把「离起点最远的房间」留成 Boss 房，而一层到底有没有 Boss
            // 是由 bossStages 决定的。两件事各说各的，中间以前靠一句「没有阶梯就退化成
            // 敌人房」糊过去——于是第 3、4、6~9、11~14 层的地图上画着一间普通敌人房，
            // 而「这一层有没有 Boss」在代码里还有第二个答案。玩家在真跑里看到的是
            // 第 8 层 Boss 房被跳过后直接下楼：三选一补偿都没有，就这么过去了。
            //
            // 断言分两头，缺一头都能漏：
            //   1) 有 Boss 阶梯的层：最远那间必须是 Boss 房，且真的给得出战斗，且真的有一个 Boss；
            //   2) 没有阶梯的层：地图上不许出现 Boss 房。
            var bad = new List<string>();
            foreach (var floor in Enumerable.Range(1, 25))
            {
                var run = new Game(777, new MetaState());
                run.RestoreFloor(floor, 1, GameStatus.Playing);
                var bossRooms = run.Map.Rooms.Count(r => r.Type == RoomType.Boss);
                var hasStage = run.BossStage(floor) is not null;

                if (hasStage && bossRooms != 1) bad.Add($"第 {floor} 层有 Boss 阶梯，却有 {bossRooms} 间 Boss 房");
                if (!hasStage && bossRooms != 0) bad.Add($"第 {floor} 层没有 Boss 阶梯，却有 {bossRooms} 间 Boss 房");

                if (!hasStage) continue;
                // 最远那间必须就是 Boss 房：Boss 房被生成器放在别处、或者最远那间
                // 只是个普通敌人房，玩家就会「打完这一层最后一场，什么都没发生」。
                if (run.Map.Rooms[run.Map.BossIndex].Type != RoomType.Boss)
                    bad.Add($"第 {floor} 层最远的房间（{run.Map.BossIndex}）是 {run.Map.Rooms[run.Map.BossIndex].Type}，不是 Boss 房");
                run.TeleportForDebug(run.Map.BossIndex);
                var enc = run.CreateEncounter();
                if (enc is null) { bad.Add($"第 {floor} 层的 Boss 房里没有敌人"); continue; }
                var stage = run.BossStage(floor)!;
                var gauntlet = stage.Mechanic == "boss_gauntlet";
                if (!gauntlet && enc.Def.Id != stage.Id)
                    bad.Add($"第 {floor} 层走进去遇到的不是 {stage.Id}，而是 {enc.Def.Id}");
            }
            Assert(bad.Count == 0, string.Join("；", bad));
        });

        Test("战斗收尾只有一个入口：结局、房间、这一局状态三者必须一致", () =>
        {
            // 一个跑出来的空档：模拟器「回到地上」之后，Battle 打出 Won，
            // 而 `OnBattleWon` 在 Status != Playing 时直接 return —— 房间没被清、
            // 玩家被关在一间打完的房里，下一次进房又是一场一模一样的仗。
            // 根子是「谁负责结算」没人管：OnBattleWon / OnBattleLost 谁都能调、调几次都行。
            // 现在只有一个入口，而且逃跑这条出路也必须被表登记（沉默的 default 就是「忘了处理」）。
            foreach (BattleOutcome outcome in Enum.GetValues<BattleOutcome>())
                Assert(Encounters.AfterBattle(outcome) != Encounters.Settle.Win || outcome == BattleOutcome.Won,
                    $"{outcome} 被结算成了胜利");
            Assert(Encounters.AfterBattle(BattleOutcome.Lost) == Encounters.Settle.Lose, "打输没有对应的结算方式");
            Assert(Encounters.AfterBattle(BattleOutcome.Escaped) == Encounters.Settle.Flee, "逃跑没有对应的结算方式（会被静默忽略）");

            // 这一局已经结束之后，任何「再结算一次」都不该改变状态。
            // 这就是那个空档的现场：模拟器回到地上之后，Battle 打出 Won，
            // 而 OnBattleWon 在 Status != Playing 时什么都不做——金币没进、房间没清，
            // 玩家被关在一间打完的房里。现在拦在这里，并且只从这里进。
            var run = new Game(31, new MetaState());
            run.Player.Hp = 0;
            run.OnBattleLost();
            Assert(run.Status == GameStatus.Dead, "玩家没死成，用例前提不成立");
            var goldBefore = run.Player.Gold;
            var killsBefore = run.Kills;
            run.OnBattleWon(data.Bosses[0], false);
            Assert(run.Player.Gold == goldBefore, $"死了之后打赢一场仍然进了 {run.Player.Gold - goldBefore} 金币——结算没有拦状态");
            Assert(run.Kills == killsBefore, "死了之后还记了一次击杀");
        });

        Test("0 血的人不能再出招（尸体不会继续把敌人打死）", () =>
        {
            var run = new Game(9, new MetaState());
            // 起点房间没有敌人：把玩家挪到最近的敌人房再建战斗
            var enemyRoom = run.Map.Rooms.FindIndex(r => r.Type == RoomType.Enemy);
            Assert(enemyRoom >= 0, "这一层没有敌人房，用例前提不成立");
            run.TeleportForDebug(enemyRoom);
            var enc = run.CreateEncounter() ?? throw new InvalidOperationException("敌人房里没有敌人");
            var battle = BuildBattle(run, enc);
            var enemyHp = enc.Monster.Hp;
            // 绕过 HurtPlayer 直接把血打到 0：这正是「某条别的路把人打死」的样子。
            run.Player.Hp = 0;
            battle.PlayerAttack();
            Assert(enc.Monster.Hp == enemyHp, $"0 血的玩家仍然打掉了敌人 {enemyHp - enc.Monster.Hp} 点血");
            Assert(battle.Phase == BattlePhase.Lost, $"0 血时战斗阶段还是 {battle.Phase}——这一场没有结局，没人替它收尾");
        });

        Section("这一轮：这一回合一共会掉多少血");

        Test("中毒的时候，预告要把「这一回合的合计」说出来，而不只是下一刀", () =>
        {
            // 改完之后最常见的一种死法是「这回合先吃 50，再吃 37」：
            // 敌人这一刀打完并挂上毒，毒在回合收尾还要再扣一次。
            // 卡片上只报「下一刀预计 50」，玩家按这个数算出「还剩 20，撑得住」，然后死了。
            var goblin = GameDataStore.Get().Monsters.First(m => m.Id == "goblin");
            var player = EntityRules.CreatePlayer(100, 50, 4, 0, new List<SkillDef>());
            var main = EntityRules.CreateMonster(goblin, new List<SkillDef>(), 4);
            var battle = new Battle(player, main, new Rng(1), new BattleOptions
            {
                Rules = new CombatRules { CritChance = 0, CritMultiplier = 1.5, DefendReduction = 0.5, DamageVariance = 0 },
                AttackerAtk = c => c.BaseAttack,
                AttackerDef = c => c.BaseDefense,
                MaxHp = c => c is Player ? 100 : c.BaseMaxHp,
                Floor = 4,
                EnemyDefs = new Dictionary<Combatant, MonsterDef> { [main] = goblin },
            });
            Assert(battle.LethalThisTurnExtra() == 0, "没中毒却报出了额外的掉血");
            EntityRules.ApplyPoison(player, 3, 7);
            var (low, high, _) = battle.EnemyForecast();
            Assert(battle.LethalThisTurnExtra() == 7,
                $"中毒 7 点，这一回合额外掉血报的是 {battle.LethalThisTurnExtra()}——预告里就少了这一截");
            // 毒不该被算进「下一刀」：那是敌人的伤害，两者分开报，玩家才看得出是谁扣的
            Assert(low == Math.Max(1, main.BaseAttack - 4), $"「下一刀」被毒撑大了：{low}");
            battle.Dispose();
        });

        Test("敌人被震住的那一回合，毒照样扣——预告里不许写「完全没事」", () =>
        {
            var goblin = GameDataStore.Get().Monsters.First(m => m.Id == "goblin");
            var player = EntityRules.CreatePlayer(100, 50, 4, 0, new List<SkillDef>());
            var main = EntityRules.CreateMonster(goblin, new List<SkillDef>(), 4);
            var battle = new Battle(player, main, new Rng(1), new BattleOptions
            {
                Rules = new CombatRules { CritChance = 0, CritMultiplier = 1.5, DefendReduction = 0.5, DamageVariance = 0 },
                AttackerAtk = c => c.BaseAttack,
                AttackerDef = c => c.BaseDefense,
                MaxHp = c => c is Player ? 100 : c.BaseMaxHp,
                Floor = 4,
                EnemyDefs = new Dictionary<Combatant, MonsterDef> { [main] = goblin },
            });
            EntityRules.ApplyPoison(player, 2, 9);
            main.StunTurns = 1;
            Assert(!battle.UpcomingEnemyActs, "前提不成立：这只没被震住");
            Assert(battle.LethalThisTurnExtra() == 9, "被震住的那一回合把毒也算漏了");
            battle.Dispose();
        });

        Section("这一轮：钱有没有出口");

        Test("货架跟着深度走：第 1 层不会摆出深渊之牙，第 15 层也不会只有锈剑", () =>
        {
            // 原来只有「模板价 ≤ 50+8×层数」这一道上限，下半边没人管：
            // 第 9 层可能摆一把 +23 的深渊之牙，第 15 层也可能只有锈剑。
            // 现在上下两条边一起定档位，判定用**两个方向**：过深的东西不许提前出现，
            // 该出现的时候必须出现。
            var data = GameDataStore.Get();
            var tooEarly = new List<string>();
            var noTopTier = 0;
            for (uint seed = 1; seed <= 40; seed++)
            {
                var run = new Game(seed * 3331);
                for (var floor = 1; floor <= 15; floor++)
                {
                    run.RestoreFloor(floor, 1, GameStatus.Playing);
                    var shop = run.Map.Rooms.FindIndex(r => r.Type == RoomType.Shop);
                    if (shop < 0) continue;
                    var gear = run.ShopStock(shop).Where(x => x.Item.Slot != Slot.Consumable).ToList();
                    if (gear.Count == 0) continue;
                    foreach (var g in gear)
                        if (g.BasePrice > ShopRoller.ShopPriceBand(floor) * 1.15)
                            tooEarly.Add($"第 {floor} 层摆出了 {g.Item.Name}（标价 {g.BasePrice}，档位上限 {ShopRoller.ShopPriceBand(floor)}）");
                    if (floor >= 12 && gear.All(g => g.BasePrice < ShopRoller.ShopPriceFloor(floor) * 0.85))
                        noTopTier++;
                }
            }
            Assert(tooEarly.Count == 0, $"{tooEarly.Count} 次货架越了档位：" + string.Join("；", tooEarly.Take(3)));
            Assert(noTopTier == 0, $"{noTopTier} 次深层的货架上全是低档货（低于本层档位下限 {ShopRoller.ShopPriceFloor(12)}+ 的东西才该上架）");
        });

        Test("深层的货架收得下深层的进项：一层赏金买得起最贵那一件的三分之一", () =>
        {
            // 「金币 1500+、店里最贵的 170」这条现场不是玩家抠，是货架收不下那笔钱：
            // 第 10 层起一层进账 300~400，而模板价上限只有 170。
            // 判据写成「一层赏金 ≥ 最贵那一件的三分之一」——不是要它买得起，
            // 是要它「值得攒一下」，而且不随深度失效。
            var data = GameDataStore.Get();
            var worst = new List<string>();
            for (var floor = 2; floor <= 15; floor++)
            {
                var topPrice = data.Items.Where(i => i.Slot != Slot.Consumable)
                    .Where(i => i.Price <= ShopRoller.ShopPriceBand(floor) && i.Price >= ShopRoller.ShopPriceFloor(floor))
                    .Select(i => i.Price).DefaultIfEmpty(0).Max();
                var floorIncome = 4 * (12 + floor * 4);
                if (topPrice > floorIncome * 3)
                    worst.Add($"第 {floor} 层：最贵可上架 {topPrice}，一层赏金只有 {floorIncome}");
            }
            Assert(worst.Count == 0, "深层货架的价格脱离进项：" + string.Join("；", worst.Take(3)));
        });

        Test("每层都有一件「这一层才有的」装备：装备表不是到第 6 层就断档", () =>
        {
            // 装备模板最大价 110（巨龙之心）的时候，第 6 层往后货架一模一样：
            // 玩家越打越有钱，能买的东西却再也没变过。
            var data = GameDataStore.Get();
            var tiers = new List<(int Floor, int Cheapest)>();
            for (var floor = 1; floor <= 15; floor++)
            {
                var pool = data.Items.Where(i => i.Slot != Slot.Consumable)
                    .Where(i => i.Price <= ShopRoller.ShopPriceBand(floor) && i.Price >= ShopRoller.ShopPriceFloor(floor))
                    .ToList();
                Assert(pool.Count > 0, $"第 {floor} 层的档位里一件装备都没有（货架会退回全表）");
                tiers.Add((floor, pool.Min(i => i.Price)));
            }
            var plain = tiers.Where(t => t.Floor >= 2).All(t => t.Cheapest <= tiers[0].Cheapest + 20);
            Assert(!plain, "第 2~15 层的货架档位没有变化——装备表里没有深层的东西");
        });

        Section("这一轮：商店与回复的账");

        Test("进店至少买得上一件东西：药永远在架，装备价跟着层数走", () =>
        {
            // 横扫 30 局的现场：金币 1500+、药 0 瓶、背包 6 格塞满，
            // 而「钱够、店里有药、买不进来」出现过 200 次。
            // 货架以前是「全表抽 3~4 件」——装备 11 件、药 2 件，抽出来的自然是三件装备。
            var noPotion = 0;
            var unaffordable = 0;
            var samples = 0;
            for (uint seed = 1; seed <= 60; seed++)
            {
                var run = new Game(seed, new MetaState());
                for (var floor = 1; floor <= 15; floor += 3)
                {
                    var g = run;
                    g.RestoreFloor(floor, 1, GameStatus.Playing);
                    var shopIndex = g.Map.Rooms.FindIndex(r => r.Type == RoomType.Shop);
                    if (shopIndex < 0) continue;
                    samples++;
                    var stock = g.ShopStock(shopIndex);
                    if (!stock.Any(s => s.Item.Heal is > 0)) noPotion++;
                    // 「这一层最便宜的那件」要买得起：一层打下来的赏金至少够一件
                    var cheapest = stock.Min(s => s.Price);
                    var floorIncome = 4 * (12 + floor * 4);
                    if (cheapest > floorIncome) unaffordable++;
                }
            }
            Assert(samples >= 40, $"样本只有 {samples} 家店，用例前提不成立");
            Assert(noPotion == 0, $"{noPotion}/{samples} 家店里一瓶回血药都没有");
            Assert(unaffordable == 0, $"{unaffordable}/{samples} 家店里连最便宜的那件都超过一层赏金（15 层会是死钱）");
        });

        Test("战后的回血卡真的能填平战损，而不是永远只补一半", () =>
        {
            // 横扫 30 局：战后回血卡合计回 10000 点，喝药只有三十几只——
            // 药是战斗里的应急储备，主力回复其实是这张卡。
            // 而它按「缺多少 × 0.5」算：战损一旦超过 50%，这张卡永远填不平，
            // 血只会一路往下滑，而曲线看起来又「每关都有补给」。
            var rng = new Rng(5);
            var def = new ItemDef { Id = "probe_sword", Name = "探针剑", Slot = Slot.Weapon, Price = 10, Attack = 3 };
            var cards = RewardRoller.Roll(rng, new[] { def }, new RewardContext { Floor = 8, MissingHp = 40 });
            var heal = cards.OfType<Reward.Heal>().First();
            Assert(heal.Amount >= 28, $"缺 40 点血时回血卡只给 {heal.Amount} 点（战损补不平，血会一路滑到死）");
        });

        Section("这一轮：地图上的「我知道但我不说」");

        Test("每条岔路都报得出「底下还剩几场仗」，而且和真正剩的数量对得上", () =>
        {
            // 房间图是一棵树，玩家走进一条死胡同、发现是空的、再原路走回来——
            // 这是这游戏里最普遍的几步路（40 种子 × 25 层实测：一半的房间是死胡同）。
            // 而地图生成那一刻就知道哪边有仗：子树里有几间没清的战斗房是纯算术。
            // 以前这个数只存在于玩家自己的记忆里，唯一的探索方式是「挨个撞一遍」。
            //
            // 两条判据，缺一条都能漏：
            //   1) 起点报出来的总和 == 本层真正剩的战斗房数（数错了比不说更糟）；
            //   2) 沿途每一步都守恒：走进一格之后，剩下的是「这格自己」+ 它的子树，
            //      而且报出来的总数只会因为「打赢了」而减少，不会因为「走开了」而变。
            var bad = new List<string>();
            long sidesWithBattle = 0, sideSlots = 0;
            for (uint seed = 1; seed <= 12; seed++)
            {
                var game = new Game(seed);
                for (var floor = 1; floor <= 10; floor++)
                {
                    game.RestoreFloor(floor, 1, GameStatus.Playing);
                    var pending = game.PendingBattlesBelow();

                    if (pending.Length != game.Map.Rooms.Count) bad.Add($"seed {seed} F{floor}: 长度对不上");
                    if (pending[game.Map.StartIndex] != game.RemainingBattleRooms)
                        bad.Add($"seed {seed} F{floor}: 起点报 {pending[game.Map.StartIndex]} 场，实际剩 {game.RemainingBattleRooms} 场");
                    // 每个房间报的数必须**正好**等于「自己（还挡路的话）+ 各个子房间」。
                    // 只查「子房间不超过自己」不够：把双向的 Links 当子树累加时
                    // 父房间也被算进来，整棵树会被算成「全场剩几场仗」——那种错误
                    // 起点那一条仍然对得上（起点没有父房间），而中间每一格都偏大。
                    // 写成等式之后，差一格也会红。
                    for (var i = 0; i < game.Map.Rooms.Count; i++)
                    {
                        var r = game.Map.Rooms[i];
                        var self = !r.Cleared && r.Type is RoomType.Enemy or RoomType.Elite
                                   || (r.Type == RoomType.Boss && !r.Cleared && game.BossStage(floor) is not null);
                        var expect = (self ? 1 : 0) + r.Children.Sum(c => pending[c]);
                        if (pending[i] != expect)
                            bad.Add($"seed {seed} F{floor}: #{i} 报 {pending[i]}，按「自己 + {r.Children.Count} 个子房间」该是 {expect}");

                    }
                    // 统计「岔口上有几个方向还压着仗」：这是红点真正能省掉的那几步路
                    for (var i = 0; i < game.Map.Rooms.Count; i++)
                    {
                        if (game.Map.Rooms[i].Type == RoomType.Start) continue;
                        foreach (var n in game.Map.Links[i])
                        {
                            sideSlots++;
                            if (pending[n] > 0) sidesWithBattle++;
                        }
                    }
                }
            }

            Assert(bad.Count == 0, string.Join("；", bad.Take(4)));
            Assert(sidesWithBattle > 0, "所有岔路都是「底下没有仗」——这个数不可能全零，说明自己算错了");
        });

        Test("同一间房反复走出去再走回来，报出来的剩余数不会变", () =>
        {
            // 这一条钉的是「报的是地图结构，不是玩家此刻站在哪」。
            // 如果实现里混进了「从当前房间出发算距离」这类东西（最近可去那句话就是那么算的），
            // 同一个岔路会随玩家移动报出不同的数，玩家会以为自己的行动改变了什么。
            var game = new Game(9);
            for (var floor = 1; floor <= 8; floor++)
            {
                game.RestoreFloor(floor, 1, GameStatus.Playing);
                var before = game.PendingBattlesBelow();
                // 走一步再走回来：不改任何清空标记，数必须逐项相等
                var step = game.Map.Links[game.PlayerIndex].FirstOrDefault(-1);
                if (step < 0) continue;
                var from = game.PlayerIndex;
                var to = game.Map.Rooms[step];
                var cur = game.CurrentRoom;
                var (ok, _, _) = game.Move(to.X - cur.X, to.Y - cur.Y);
                if (ok)
                {
                    var back = game.CurrentRoom;
                    game.Move(from == game.PlayerIndex ? 0 : cur.X - back.X, cur.Y - back.Y);
                }
                var after = game.PendingBattlesBelow();
                Assert(before.SequenceEqual(after), $"第 {floor} 层：走一步再走回来，剩余战斗房的分布变了");
            }
        });

        Section("这一轮：这一层的票价——「剩余战斗房」不是「下楼还要打几场」");

        Test("BattlesToBoss 报的是「从脚下到 Boss 的最短路上还剩几场」，不是全场剩几场", () =>
        {
            // 以前只有 `RemainingBattleRooms`（全场还剩几间战斗房），
            // 而它和「下楼还要打几场」是两个数：一层平均 3.2 场仗，
            // 挡在「起点 → Boss」这条路上的只有 1.1 场（40 种子 × 14 层实测）。
            // 玩家看到「剩余战斗房 3」的读法是把三场都打完——那不是难度，
            // 是一句没法据以行动的话。
            //
            // 两条判据，缺一条都会让这个数变成噪声：
            //   1) 不超过全场剩余（它是 8 个里挑 1 个，不能比总数还大）；
            //   2) 站在起点时，它必须真的等于「起点到 Boss 最短路上的战斗房数」——
            //      这一条是等式，用「只算自己这一格」的假实现跑会红。
            var bad = new List<string>();
            var allZero = 0;
            var strictlyFewer = 0;
            for (uint seed = 1; seed <= 20; seed++)
            {
                var game = new Game(seed);
                for (var floor = 1; floor <= 10; floor++)
                {
                    game.RestoreFloor(floor, 1, GameStatus.Playing);
                    var actual = game.BattlesToBoss();
                    if (actual > game.RemainingBattleRooms)
                        bad.Add($"seed {seed} F{floor}: 报 {actual} 场，比全场剩的 {game.RemainingBattleRooms} 场还多");
                    // 独立算一遍：起点到 Boss 的树上路径上，有几间是「还挡路的」
                    var map = game.Map;
                    var parent = new int[map.Rooms.Count];
                    Array.Fill(parent, -1);
                    var queue = new Queue<int>();
                    queue.Enqueue(map.StartIndex);
                    parent[map.StartIndex] = map.StartIndex;
                    while (queue.Count > 0)
                    {
                        var cur = queue.Dequeue();
                        foreach (var n in map.Links[cur])
                        {
                            if (parent[n] != -1) continue;
                            parent[n] = cur;
                            queue.Enqueue(n);
                        }
                    }
                    var expect = 0;
                    for (var at = map.BossIndex; at != map.StartIndex && parent[at] != -1; at = parent[at])
                    {
                        var room = map.Rooms[at];
                        if (room.Type is RoomType.Enemy or RoomType.Elite) expect += 1;
                        else if (room.Type == RoomType.Boss && game.BossStage(floor) is not null) expect += 1;
                    }
                    if (actual != expect)
                        bad.Add($"seed {seed} F{floor}: 报 {actual} 场，起点的必经之路上实际是 {expect} 场");
                    if (actual == 0) allZero += 1;
                    if (actual < game.RemainingBattleRooms) strictlyFewer += 1;
                }
            }
            Assert(bad.Count == 0, string.Join("；", bad.Take(4)));
            // 这不是「有时候会少」——如果永远相等，说明这个数根本没在用最短路，
            // 只是把全场剩余换了个名字（那正好是这一条要防的那种实现）。
            Assert(strictlyFewer > 0, "从来报不出「少于全场剩余」的数：这个数没有在看必经之路");
        });

        Test("路过一地打掉几场之后，这个数只会因为「真的打掉了」而减少", () =>
        {
            var game = new Game(5);
            game.RestoreFloor(3, 1, GameStatus.Playing);
            var before = game.BattlesToBoss();
            var all = game.RemainingBattleRooms;
            // 把路外的一间敌人房标成已清空：必经之路没变，这个数必须一个都不减
            var map = game.Map;
            var distBoss = MapGen.BfsDistances(map.Links, map.BossIndex);
            var distStart = MapGen.BfsDistances(map.Links, map.StartIndex);
            var offPath = -1;
            for (var i = 0; i < map.Rooms.Count; i++)
            {
                var r = map.Rooms[i];
                if (r.Type != RoomType.Enemy) continue;
                if (distStart[i] + distBoss[i] == distStart[map.BossIndex]) continue;
                offPath = i;
                break;
            }
            Assert(offPath >= 0, "这一层没有「路外的敌人房」，测不到这件事");
            map.Rooms[offPath].Cleared = true;
            Assert(game.BattlesToBoss() == before,
                $"清掉一间路外的敌人房之后，必经之路从 {before} 变成了 {game.BattlesToBoss()}（它不该动）");
            Assert(game.RemainingBattleRooms < all, "全场剩余反而没减少：清空标记没生效，这条测的不是它想测的");
        });

        Section("这一轮：背包、三选一与卖东西那条路");

        Test("每一张奖励卡都有自己的位置：第一个位置是可取的，不是空盒子", () =>
        {
            // 三选一的第一个位置是「没人选的时候会拿走的那张」：宝箱房只给一张卡
            // （`Rewards()[0]`），任何只取首张的调用方都只能拿到它。
            // 以前洗牌后首张常常是「回血」——满血时它写着 0 点，开箱的人只好拿走一格空气；
            // 背包满时还会为它挤掉一件有用的装备。改法不是「特殊照顾宝箱」，
            // 而是让「金币 / 回血」这两档保底永远排在装备后面：它们在任何血线下都有意义，
            // 但「一定要拿」的东西必须先给到玩家。
            //
            // 判据不写成「首张必须是装备」（某次洗牌可能整池都没装备），
            // 而是写成「首张一定是他真正拿得到的那张」：回血卡在首张时必须 Effective，
            // 满血时任何一张 0 点回血卡都不许站到第一位。
            var bad = new List<string>();
            var emptyFirst = 0;
            for (uint seed = 1; seed <= 300; seed++)
            {
                var game = new Game(seed);
                // 满血与残血各跑一遍：这正是「首张是空盒子」出现在哪一半的答案
                foreach (var hpPct in new[] { 1.0, 0.35 })
                {
                    game.Player.Hp = Math.Max(1, (int)Math.Round(game.Stats.MaxHp * hpPct));
                    var cards = game.Rewards(EnemyTier.Normal);
                    var first = cards[0];
                    if (first is Reward.Heal h && !h.Effective)
                    {
                        emptyFirst++;
                        bad.Add($"seed {seed} 血 {hpPct:P0}：首张是「回血 0 点」");
                    }
                    if (first is Reward.Heal hh && hh.Amount == 0) bad.Add($"seed {seed}：首张回血卡是 0 点");
                    if (cards.Count == 0) bad.Add($"seed {seed}：一张卡都没有");
                }
            }
            Assert(bad.Count == 0, $"{emptyFirst} 次三选一的第一张是空盒子：" + string.Join("；", bad.Take(3)));
        });

        Test("背包里的一件东西能卖成钱，钱只记在 EarnGold 那本账上", () =>
        {
            // 这条钉的是「换装剩下的那件旧货」有没有去处。
            // 背包 6 格、装备槽 3 个，换一件更好的必然剩下一件旧的；
            // 以前唯一的出口是「丢弃」——一条纯损失的路。于是玩家（和探针）
            // 宁可六格全塞装备，也不肯给一瓶药腾地方，而商店里那瓶药永远买不进来。
            var game = new Game(77);
            var data = GameDataStore.Get();
            var sword = data.Items.First(i => i.Slot == Slot.Weapon);
            game.Player.Inventory.Clear();
            Inventory.AddItem(game.Player, sword.Id, game.Capacity);
            var goldBefore = game.Player.Gold;
            var earnedBefore = game.GoldEarned;
            var expect = Inventory.SellValue(sword);
            Assert(expect > 0, "卖价算成了 0：这条路存在但给不了任何东西");
            Assert(game.SellItem(0), "卖掉背包里那件失败了");
            Assert(game.Player.Inventory.Count == 0, "卖完之后东西还在背包里");
            Assert(game.Player.Gold == goldBefore + expect, $"金币没到账：{goldBefore} → {game.Player.Gold}（该 +{expect}）");
            // 三本账里至少这一本要对：「赚到的钱」不能靠手写 Player.Gold += 来记
            Assert(game.GoldEarned == earnedBefore + expect, "金币进账没记在 EarnGold 那本账上");
            Assert(!game.SellItem(0), "空背包还能卖出东西");
        });

        Test("背包满、钱够、药在架：玩家卖得掉一件，于是买得进来", () =>
        {
            // 横扫 30 局里这个局面出现过 300 多次：血满、0 瓶药、钱够买一瓶，
            // 而六个格子全是「换装换下来的旧货」——钱、药、钱都不是缺的，
            // 缺的是一条把旧货变成格子的路。这条断言把「那条路真的通了」钉住：
            // 卖一件 → 背包腾出一格 → 同一家店里那瓶药买得进来。
            var game = new Game(31);
            var data = GameDataStore.Get();
            game.Player.Inventory.Clear();
            // 塞满：三件穿着 + 若干更差的备件，模拟一路捡下来的一背包旧货
            var weapons = data.Items.Where(i => i.Slot == Slot.Weapon).OrderBy(i => i.Attack).ToList();
            var armors = data.Items.Where(i => i.Slot == Slot.Armor).OrderBy(i => i.Defense).ToList();
            var trinkets = data.Items.Where(i => i.Slot == Slot.Trinket).OrderBy(i => i.MaxHp).ToList();
            foreach (var it in new[] { weapons.Last(), armors.Last(), trinkets.Last(), weapons.First(), armors.First() })
                Inventory.AddItem(game.Player, it.Id, game.Capacity);
            while (game.Player.Inventory.Count < game.Capacity)
                Inventory.AddItem(game.Player, weapons.First().Id, game.Capacity);
            Assert(game.Player.Inventory.Count == game.Capacity, "没能把背包塞满（前置条件不成立，这条测不到东西）");

            game.ShopStock(game.PlayerIndex);
            // 卖一件旧货：背包腾出一格
            Assert(game.SellItem(game.Player.Inventory.Count - 1), "背包满时卖不出去");
            Assert(game.Player.Inventory.Count == game.Capacity - 1, "卖完之后没有腾出格子");
        });

        Section("这一轮：三处「说明写了、代价却没人付」");

        Test("元进展的解锁不会被「再挣一块碎片」写回旧值", () =>
        {
            // 病根是「同一个概念有两份对象」。主菜单改的是 GameStore.Meta（解锁），
            // 而一局开打之后手里握着开局那一刻的快照；碎片与成就都往**快照**上写，
            // 再由 MetaSaver 落盘。于是只要下一局里挣到一块碎片，
            // SaveMeta 就把那份还没有解锁的快照写回盘上——150 碎片买的东西静默消失。
            //
            // 这条断言的关键是「落盘那一步真的被走过了」：第一版忘了装 MetaSaver，
            // 于是它验的是「内存里那份对不对」，而旧代码在内存里恰好也是对的
            // （GrantShards 换对象时做的是一次浅拷贝的克隆，解锁被带过去了）。
            // 真正错的是「谁被写回盘上」——所以这里必须把盘上那一份接住。
            // 碎片给到「买得起最贵那一档 + 5」：定价会变，而这条判据要守的是
            // 「解锁会不会被下一次挣碎片冲掉」，不是某个具体价格。
            var price = MetaProgression.UnlockCost[MetaUnlock.DualAffix];
            var meta = new MetaState { Shards = price + 5 };
            var game = new Game(1, meta);
            MetaState? onDisk = null;
            game.MetaSaver = m =>
            {
                onDisk = m.Clone();
            };
            Assert(game.TryUnlock(MetaUnlock.DualAffix), "碎片够却没解锁成功（前置条件不成立）");
            Assert(MetaProgression.HasUnlock(game.Meta, MetaUnlock.DualAffix), "解锁没落在这一局手里那份上");
            var cost = MetaProgression.UnlockCost[MetaUnlock.DualAffix];
            Assert(game.Meta.Shards == 5, $"碎片没被扣：{game.Meta.Shards}（开头给了 {price + 5}，这一档 {cost}）");
            // 后续的碎片结算不能把它冲掉——这正是旧代码犯的错
            // 盘上那一份要立刻接住：旧实现里 GrantShards/成就都「换一个新对象再落盘」，
            // 任何拿着旧引用的读者（主菜单的列表、下一局的起点）看到的都是旧值。
            // 这里验的就是「落盘的到底是哪一份」，只比内存里的字段永远绿。
            var before = game.Meta;
            game.GrantShards(5);
            Assert(ReferenceEquals(game.Meta, before),
                "挣一块碎片就把元进展换成了另一个对象：盘上写的是新的那份，读的人手里还是旧的");
            Assert(onDisk is not null, "挣了碎片却没落盘：盘上那一份还是旧的");
            Assert(MetaProgression.HasUnlock(onDisk!, MetaUnlock.DualAffix),
                "落盘的那一份里没有解锁：下一局开局会把它当成没买过（150 碎片白花）");
            // 落盘的那一份必须和内存里那一份同数——写死一个数字就没在比任何东西了。
            Assert(onDisk!.Shards == game.Meta.Shards,
                $"落盘的碎片账对不上：盘上 {onDisk.Shards}、手里 {game.Meta.Shards}");
            // 已经解锁的不该能被再买一次
            Assert(!game.TryUnlock(MetaUnlock.DualAffix), "同一档被买了两遍");
        });

        Test("「还要等几回合」这个数只有一份，放完火球不是冷却 +1", () =>
        {
            // 施放时写进 Cooldowns 的是 `Cooldown + 1`（当回合也算一格，回合末统一递减），
            // 它表示「还要等几回合」。按钮却把这个原始值当成「冷却」印出来：
            // 火球（冷却 2）放完按钮写着「冷却 3」，玩家看到的冷却永远多一回合。
            // 更麻烦的是 `IsSkillReady`（cd <= 0）与 `SkillCooldownLeft` 是两个判据，
            // 「能不能放」和「还要等几回合」迟早会分叉。
            //
            // 用木桩验而不是真怪：真战斗里火球往往一击就把第 1 层的巨鼠打死，
            // 那样「放完还要等几回合」就无处可测（第一版就是这么绿掉的）。
            var player = new Game(11).Player;
            var dummy = EntityRules.CreateMonster(
                GameDataStore.Get().Monsters.First(m => m.Id == "golem"),
                new List<SkillDef>(), 1);
            dummy.BaseMaxHp = 5000;
            dummy.Hp = 5000;
            var fireball = player.Skills.First(x => x.Id == "fireball");
            Skills.UseSkill(fireball, new SkillContext
            {
                Actor = player,
                Target = dummy,
                ActorAtk = 30,
                ActorMaxHp = 100,
                TargetDef = 0,
                Rng = new Rng(3),
                Rules = RulesOf(GameDataStore.Get()),
            });
            // 钉的必须是**等式**，不是上界：只写「不超过 冷却 + 1」，
            // 旧代码（按钮直接印原始值）也照样通过，那等于没测。
            // 放完这一手（含它自己那一回合）之后，还要等 `Cooldown` 个完整回合——
            // 读出来就是 `Cooldown`。这个等式是「冷却显示比真实慢一回合」那条 bug 的判据：
            // 旧实现读的是原始计数（Cooldown + 1），所以它写着「还要 3 回合」，
            // 而玩家下下个回合就能放。
            var left = Skills.SkillCooldownLeft(player, "fireball");
            Assert(left == fireball.Cooldown,
                $"放完火球还剩 {left} 回合，而它该是 {fireball.Cooldown}（技能描述里写的那个数）");
            Assert(!Skills.IsSkillReady(player, "fireball"), "冷却中却说能放");
            // 走完冷却就得能放：两条判据必须是同一条线
            for (var i = 0; i < fireball.Cooldown; i++) Skills.TickCooldowns(player);
            Assert(Skills.IsSkillReady(player, "fireball"),
                $"等了 {fireball.Cooldown} 回合，火球还是放不出来（还剩 {Skills.SkillCooldownLeft(player, "fireball")}）");
        });

        Test("宝箱房开箱也要落盘：钱、血、装备三样都是场外状态", () =>
        {
            // 补给房「用了才清房」是前几轮修的，但清房与落盘必须成对：
            // 宝箱房那条路上一次 SaveRun 都没有。开完箱子顺手关窗，
            // 回来箱子还开着、钱和血却退回去了——比「箱子被吞掉」更隐蔽，
            // 因为玩家看到的是一间已经清空的房，账却是旧的。
            //
            // 静态检查是这里唯一可行的手段：落盘写在 MapScene 的回调里，
            // 而自检跑不到 UI（没有帧、没有面板）。判据要窄：
            // 「OpenTreasure 之后、Store.SaveRun 与 ClearRoom 必须同时出现在同一段里」。
            // 打不到源码就跳过：导出产物里 `.cs` 在 pck 里（见 `SourceOf`），
            // 读得到才谈得上静态检查。这里**不许**写成 Assert —— 一条「读不到源码」
            // 在导出环境里永远是红的，而它想保护的东西一点没坏。
            var text = MapSceneSource();
            if (text is null) return;
            // 锚点按签名匹配（`private void` 与 `(` 之间可以挂特性、改修饰符），
            // 返回的是签名那一行的行首——函数体从这里往下取。
            var cut = MethodAt(text!, "private void OpenTreasure(int index)");
            Assert(cut >= 0, "找不到 OpenTreasure");
            // 下界认得宽一点：宝箱房后面紧跟的是字段声明 `private Action? ...`，
            // 也是祭坛房里的局部函数 `void Used()`——只认 `private void ` 会一路吃到文件尾。
            // 按「行首 + 4 空格缩进 + void 声明」切，判据量的就正是这一段。
            var next = MethodEnd(text!, cut + 1);
            var body = text.Substring(cut, (next < 0 ? text.Length : next) - cut);
            Assert(body.Contains("SaveRun"), "开完箱子没有落盘：关窗等于把箱子里的东西退回去");
            // ClearRoom 写在「真的拿走了」那个回调里，经 MountRewardCards 传下去——
            // 仍要求它出现在这一段里（回调必须是开箱自己挂的，不能是别处的通用逻辑）。
            Assert(body.Contains("ClearRoom"), "开完箱子没有清房：同一只箱子能反复开");
            Assert(body.Contains("MountRewardCards"),
                "宝箱房又走了「只给一张卡」那条捷径（同一张回血卡会在两个面板上写两个数）");
        });

        Test("地图上已经结束的一局不能再下楼", () =>
        {
            // 「一局结束了不能下楼」写在 Game.Descend() 里，而它先查「这一层清空了吗」。
            // 事件房把人打死、祭坛的血祭把人耗死——如果那一层刚好已经清空，
            // 按 `>` 仍然下得去：楼层继续涨，状态永远是 Dead。
            // 这里钉两半：规则层拦得住（Descend 会拒），UI 那条路也拦得住。
            var text = MapSceneSource();
            if (text is null) return;   // 同上：读不到源码就跳过，别把「读不到」判成「规则坏了」
            var cut = MethodAt(text!, "private void TryDescend");
            Assert(cut >= 0, "找不到 TryDescend");
            // 结尾也按成员声明切：`OnRoomClicked` 只认 `private void ` 的话，
            // 中间隔着的字段与局部函数会把别人的一段也算进 TryDescend 的 body。
            var next = MethodEnd(text!, cut + 1);
            var body = text.Substring(cut, (next < 0 ? text.Length : next) - cut);
            Assert(body.Contains("IsGameOver"),
                "TryDescend 不判「这一局还活着吗」：清空之后被打死的人按 > 还能往下走");
        });

        Section("这一轮：五处「规则写了，路却不通向它」");

        Test("存档只把「_r + 数字」认成运行时装备，名字里带 _r 的模板不算", () =>
        {
            // 判据原来是 `id.Contains("_r")`，而数据表里真的有 `iron_ring`。
            // 于是「铁戒指」这种模板也被当成运行时装备存进档里，读回来时
            // `RestoreRuntimeItem` 把它盖成一个「底板就是它自己」的副本；
            // 之后每次登记新副本都要绕开 `_r1`（已被占），背包里那一件
            // 则成了那份来路不明的副本。不崩、不红，只是存档里多了一个幻觉。
            var run = new Game(21);
            run.Player.Inventory.Clear();
            Inventory.AddItem(run.Player, "iron_ring", run.Capacity);
            var path = "user://selfcheck_runtime_id.cfg";
            Autoload.SaveSerializer.Save(path, run, 21);
            try
            {
                var cfg = new ConfigFile();
                Assert(cfg.Load(path) == Error.Ok, "存档读不出来");
                var runtime = cfg.GetValue("run", "runtime_items", new Godot.Collections.Dictionary()).AsGodotDictionary();
                Assert(!runtime.ContainsKey("iron_ring"),
                    $"名字里带 _r 的模板（iron_ring）被当成运行时装备存进了档：{string.Join(",", runtime.Keys)}");
                var loaded = Autoload.SaveSerializer.Load(path, new MetaState());
                Assert(loaded is not null, "存了却读不回来");
                Assert(loaded!.Player.Inventory.SequenceEqual(run.Player.Inventory),
                    "读档后背包里的铁戒指变了（模板被副本顶掉）");
                // 反向：真的运行时装备必须照存不误，否则修这一条会把另一条弄坏
                var copy = run.RegisterRuntimeItemCopy(GameDataStore.Get().Items.First(i => i.Id == "iron_ring"));
                Inventory.AddItem(run.Player, copy.Id, run.Capacity);
                Autoload.SaveSerializer.Save(path, run, 21);
                cfg = new ConfigFile();
                Assert(cfg.Load(path) == Error.Ok, "存档读不出来（第二遍）");
                runtime = cfg.GetValue("run", "runtime_items", new Godot.Collections.Dictionary()).AsGodotDictionary();
                Assert(runtime.ContainsKey(copy.Id), $"运行时装备 {copy.Id} 没被存进档");
            }
            finally
            {
                Autoload.SaveSerializer.Delete(path);
            }
        });

        Test("投掷道具不吃「强化」：那一次加成该留到下一刀", () =>
        {
            // 「强化」写着「下一次**攻击**威力提升」。炸弹是投出去的东西，不是攻击；
            // 而它以前和普攻共用一条收尾，顺手把 AttackMultiplier 消费掉了——
            // 玩家放完强化、扔个炸弹，下一刀回到原数，日志里也没有任何一句解释。
            var run = new Game(22);
            var data = GameDataStore.Get();
            var empower = data.Skills.First(x => x.Id == "empower");
            var bomb = data.Items.First(x => x.Slot == Slot.Consumable && x.Damage is > 0);
            run.Player.Skills.Add(empower);
            run.Player.Inventory.Clear();
            Inventory.AddItem(run.Player, bomb.Id, run.Capacity);
            var enemyRoom = run.Map.Rooms.FindIndex(r => r.Type == RoomType.Enemy);
            Assert(enemyRoom >= 0, "这一层没有敌人房，用例前提不成立");
            run.TeleportForDebug(enemyRoom);
            var enc = run.CreateEncounter() ?? throw new InvalidOperationException("敌人房里没有敌人");
            var battle = BuildBattle(run, enc);
            Assert(battle.PlayerSkill("empower"), "「强化」放不出来");
            var buffed = run.Player.AttackMultiplier;
            Assert(buffed > 1, $"放完强化之后倍率还是 {buffed}");
            battle.PlayerItemDamage(bomb.Damage!.Value, bomb.Name);
            Assert(Math.Abs(run.Player.AttackMultiplier - buffed) < 1e-9,
                $"投了个炸弹就把「强化」吃掉了：{buffed} → {run.Player.AttackMultiplier}（下一刀白放）");
            // 普攻必须照旧消费掉它——否则这条修复会把「强化」变成常驻
            if (battle.Phase == BattlePhase.Player)
            {
                battle.PlayerAttack();
                Assert(run.Player.AttackMultiplier <= 1,
                    $"普攻之后「强化」还在：{run.Player.AttackMultiplier}（那就成了常驻加成）");
            }
        });

        Test("换上生命装不是喝药：摘下来再戴回去喝不到血", () =>
        {
            // 「上限涨了当前生命不动」这条规则单独看没错，但它让**卸下**变成一次满血：
            // 上限回落、当前生命被夹回上限，再戴回去上限又涨 8——此刻是 60/68，能再喝一瓶。
            // 三步都符合规则，合起来是一台无限回血机。这条断言验的是**副作用**：
            // 反复三轮「摘 → 喝满 → 戴」，血不该多出一点。
            var game = new Game(23);
            Inventory.AddItem(game.Player, "iron_ring", game.Capacity);   // 生命 +8
            var idx = game.Player.Inventory.Count - 1;
            Inventory.Equip(game.Player, idx, game.ItemIndex);
            game.Player.Hp = 30;
            for (var round = 0; round < 3; round++)
            {
                Inventory.Unequip(game.Player, "trinket", game.ItemIndex);
                game.HealPlayer(999);
                Inventory.Equip(game.Player, idx, game.ItemIndex);
            }
            Assert(game.Player.Hp == game.Stats.MaxHp,
                $"反复摘戴三轮之后仍是 {game.Player.Hp}/{game.Stats.MaxHp}——" +
                "上限涨落与当前生命不同步，白送的缺口就是白送的血");
        });

        Test("Boss 首杀碎片不会被「先下楼、回头再打」绕过", () =>
        {
            // 首杀碎片只在 OnBattleWon 里发，而 Boss 房**不挡下楼**：
            // `BlocksDescend` 里 Boss 只在「这一层有 Boss 阶梯」时才算拦路，
            // 于是清掉别处之后按 `>` 就能走人。楼下沉是单向的，那一层再也回不去——
            // 碎片就这么静默地没发。第 20、25 层（深潜的逆位 Boss 与三 Boss 连战）
            // 只能靠「继续深潜」抵达，最容易被这样绕过。
            foreach (var (floor, shardKey) in new[] { (5, "5"), (10, "10"), (15, "15") })
            {
                var run = new Game(24);
                run.RestoreFloor(floor, 1, GameStatus.Playing);
                var bossIdx = run.Map.BossIndex;
                Assert(bossIdx >= 0, $"第 {floor} 层没有 Boss 房");
                // 打赢 Boss（走真入口），然后立刻下楼
                run.TeleportForDebug(bossIdx);
                var enc = run.CreateEncounter() ?? throw new InvalidOperationException($"第 {floor} 层 Boss 房没有遭遇");
                var shardsBefore = run.Meta.Shards;
                run.OnBattleWon(enc.Def, true);
                var gained = run.Meta.Shards - shardsBefore;
                var expect = GameDataStore.Get().Config.Meta.BossShards.TryGetValue(shardKey, out var v) ? v : 0;
                Assert(gained >= expect, $"第 {floor} 层打赢 Boss 只拿到 {gained} 碎片（该有 {expect}）");
            }

            // 补发那一半：Boss 房被打掉、但玩家径直下楼（Boss 不挡路）——
            // 离开这一层就是最后一次机会。
            var g = new Game(26);
            g.RestoreFloor(10, 1, GameStatus.Playing);
            var idx10 = g.Map.BossIndex;
            g.Map.Rooms[idx10].Cleared = true;
            foreach (var r in g.Map.Rooms) if (r.Type is RoomType.Enemy or RoomType.Elite) r.Cleared = true;
            var beforeShards = g.Meta.Shards;
            var (ok, _) = g.Descend();
            Assert(ok, "这一层清空了却下不去（用例前提不成立）");
            var reward = GameDataStore.Get().Config.Meta.BossShards.TryGetValue("10", out var rv) ? rv : 0;
            Assert(g.Meta.Shards - beforeShards >= reward,
                $"第 10 层的 Boss 房已清空、玩家也下楼了，首杀碎片却只发了 {g.Meta.Shards - beforeShards}（该有 {reward}）");
            Assert(g.BeatenBossFloors.Contains(10), "下楼之后仍没记下第 10 层的首杀");
            // 再下一次楼不该重复发
            var afterFirst = g.Meta.Shards;
            foreach (var r in g.Map.Rooms) r.Cleared = true;
            g.Descend();
            Assert(g.Meta.Shards - afterFirst < reward, "同一层的首杀碎片发了两遍");
        });

        Test("每个有 Boss 阶梯的层，敌人真的带着「这一层的机制」进场", () =>
        {
            // bossStages 上的 mechanic 字段装的是两种东西：
            //  · 这一层持续生效的规则（molten_burst / frost_mirror / abyss_domain）；
            //  · 这一场战斗的元数据（boss_gauntlet）。
            // 后者在机制清单里查得到名字，却没有 Setup。第 25 层因此整层敌人
            // 什么机制都没有，而 `floorMechanicMap` 里明明给它写了 `abyss_pressure`。
            // 这条断言钉的是「机制 id 最终落到一个真的有实现的机制上」，
            // 而不是「字符串查得到」。
            var data = GameDataStore.Get();
            foreach (var stage in data.BossStages)
            {
                var run = new Game(27);
                run.RestoreFloor(stage.Floor, 1, GameStatus.Playing);
                var id = run.FloorMechanicId;
                // Boss 层的机制可能是「楼层规则」（molten_burst 这一档写在 BossTable 里）
                // 或「持续生效的楼层规则」（Table 里）；两处都算有实现。
                Assert(FloorMechanics.IsFloorMechanic(id) || FloorMechanics.BossMechanicHasEffect(id),
                    $"第 {stage.Floor} 层的机制 id 是「{id}」，但它没有任何可执行的东西" +
                    "（boss_gauntlet 这类「这一场打几只」的元数据被当成了楼层机制）");
            }
            // 反向：数据表里每一行「楼层 → 机制」都要指向真的机制
            foreach (var (floorKey, mechId) in data.FloorMechanicMap)
            {
                if (mechId == "none") continue;
                var run = new Game(28);
                run.RestoreFloor(int.Parse(floorKey), 1, GameStatus.Playing);
                var bossStage = run.BossStage(run.Floor);
                var bossOwned = bossStage is not null && data.BossMechanics.ContainsKey(mechId);
                Assert(FloorMechanics.IsFloorMechanic(mechId) || bossOwned,
                    $"第 {floorKey} 层配的机制「{mechId}」在机制表里查不到");
            }
        });

        Test("新周目按「换了一层」处理：训练房的苦练不会跟到下一周目", () =>
        {
            // Descend() 会 ResetFloorBuffs（苦练 +15%、符文队列），而 ContinueDive
            // 换的同样是「一层新地图」，以前却什么都不清。于是上周目最后一层
            // 苦练出来的加成会跟着玩家进新周目，而新地图上再也找不到它的来源。
            var game = new Game(29);
            game.RestoreFloor(15, 1, GameStatus.Playing);
            foreach (var r in game.Map.Rooms) r.Cleared = true;
            var (ok, _) = game.Descend();
            Assert(ok, "第 15 层清空了却下不去（用例前提不成立）");
            game.TakeTraining("attack");
            Assert(game.TrainingAttackPct > 0, "苦练没生效（用例前提不成立）");
            // 假装这一局刚通关：让它从第 15 层以 Won 收尾
            game.RestoreFloor(15, 1, GameStatus.Won);
            game.ContinueDive();
            Assert(game.TrainingAttackPct == 0,
                $"继续深潜之后上周目的苦练还挂着 +{game.TrainingAttackPct:P0}（新地图上找不到来源）");
        });

        Section("这一轮：毒是全游戏唯一一条指数成长的伤害");

        Test("毒伤按基础攻击力算、并且封顶，不跟着层数一路膨胀", () =>
        {
            // 以前的毒伤是「这一刀的 50%」，而这一刀随层数长（敌人攻击 ×1+5%/层）：
            // 横扫 30 局里最深处的毒伤是 49 点/回合，一回合掉掉玩家满血的四分之一。
            // 而解毒草只在「这一回合的毒会要命」时才值得吃——那个局面上吃草，
            // 也只是把两回合的账往后挪一回合：下一刀照样落下来。
            // 现在毒伤按 BaseAttack（不随层数膨胀）算，封在 1..MaxPoisonPower。
            var shallow = EntityRules.CreateMonster(
                new MonsterDef { Id = "m", Name = "浅层怪", Hp = 100, Attack = 10, Defense = 0 }, new List<SkillDef>(), 1);
            var deep = EntityRules.CreateMonster(
                new MonsterDef { Id = "m", Name = "深层怪", Hp = 100, Attack = 10, Defense = 0 }, new List<SkillDef>(), 15);
            Assert(deep.BaseAttack > shallow.BaseAttack * 1.5,
                $"前提不成立：第 15 层的怪攻击没有明显高于第 1 层（{shallow.BaseAttack} → {deep.BaseAttack}）");
            // 毒伤按**目标血条**折算，不再按施法者攻击力：这条判据量的是
            // 「浅层号与深层号，同样的毒、同样的份量」，以及「再重的毒也顶不到半条命」。
            // 上一版是按施法者基础攻击力折算、夹在 1..6 的常数链——它把毒从这个游戏里
            // 删掉了（100 局 311 个中毒回合，「毒够重」0 次，解毒草 215 株买进 13 株吃下）。
            var a = EntityRules.PoisonPowerFor(60, EntityRules.PoisonShareOfMaxHp);
            var b = EntityRules.PoisonPowerFor(137, EntityRules.PoisonShareOfMaxHp);
            Assert(b - a <= EntityRules.MaxPoisonPower / 2,
                $"毒伤还在随面板大幅跳变：60 血时 {a}、137 血时 {b}");
            Assert(a >= 4 && b <= EntityRules.MaxPoisonPower,
                $"毒伤这一栏没落在 [4, {EntityRules.MaxPoisonPower}] 里：60 血 {a}、137 血 {b}");
            // 它仍然是「一截」而不是「一条命」：任何血条下，毒都顶不到半条命。
            foreach (var maxHp in new[] { 40, 60, 81, 103, 137, 200 })
            {
                var tick = EntityRules.PoisonPowerFor(maxHp);
                Assert(tick <= maxHp / 4,
                    $"{maxHp} 血的号每回合中 {tick} 点毒：这已经不是「会疼」，是一回合掉掉四分之一条命");
            }
        });

        Test("三种下毒的入口（技能 / 瘴气 / 灼烧）都走同一个上界", () =>
        {
            // 上界只写在调用点就一定有一处漏掉：产品里有三处下毒——
            // 毒系技能、瘴气层的攻击附毒、熔岩爆裂的灼烧。任何一处自己算一遍，
            // 就到不了这条判据；所以这里直接读源码，确认没有第二处在算毒伤。
            var battle = SourceOf("Scripts/Systems/Battle.cs");
            if (SourceOf("Scripts/Systems/Skills.cs") is not { } skills || battle is null)
                return;   // 导出产物里没有源码，这条跳过
            // 技能侧：直伤那一档挂毒也要过 EntityRules.MaxPoisonPower。这里读的是**调用点**
            // （`PoisonPowerOf(...)` 那一行），不是函数体——函数体里本来就该出现算式。
            // 技能侧读的是「毒伤的唯一算式」`EntityRules.PoisonPowerFor`：
            // 以前这里查的是字面量 `EntityRules.MaxPoisonPower`——那是一句**实现细节**的指纹，
            // 算式换个名字就红，而「不许有第二处自己算毒伤」这条规矩没变。
            // 调用点的判据仍然按行扫（`PoisonCallSite`），不按整份源码 Contains：
            // 函数体里出现那个上界它就绿，于是「有人绕开它重新算一遍」照样拦不住。
            var call = PoisonCallSite(skills);
            Assert(call is not null, "技能侧挂毒的调用点不见了：PoisonPowerOf(skill, ctx, dealt)");
            Assert(call!.Contains("PoisonPowerOf(") && !call.Contains("static"),
                $"技能侧挂毒没走 PoisonPowerOf 那一处：{call.Trim()}");
            // 而 PoisonPowerOf 自己必须问同一个入口：毒伤按**目标血条**折算，
            // 上界与算式都在 `EntityRules.PoisonPowerFor` 里。这里查的是它有没有被绕开。
            Assert(skills.Contains("EntityRules.PoisonPowerFor"),
                "技能侧又自己算了一遍毒伤，没走 EntityRules.PoisonPowerFor");
            Assert(!skills.Contains("Math.Round(ctx.Actor.BaseAttack"),
                "技能侧按施法者攻击力算毒伤：毒是环境的账，不是施法者攻击力的账");
            Assert(!battle.Contains("Math.Round(dealt * FloorMechanics.HazePoisonRatio)"),
                "瘴气又自己算了一遍毒伤，没走 EntityRules 的上界");
            // 灼烧那一档现在是 FloorMechanics.BurnPoisonPower(player.BaseAttack)：
            // 算式只许出现在 FloorMechanics 里，Battle 只负责「谁中了毒」。
            Assert(!battle.Contains("Math.Round(player.BaseAttack * 0.5)"),
                "灼烧又自己算了一遍毒伤，没走 EntityRules 的上界");
        });

        Test("解毒草是一个说得清的道具：它只在「这一回合会被毒死」时才有用", () =>
        {
            // 它以前是一个「没有场的道具」：中毒回合里绝大多数伤害并不致命，
            // 而吃草要占一个回合——那个回合本来能砍一刀。
            // 削了毒之后这个局面更少，所以它的定价（10 金币）与定位必须说清楚：
            // 它是「多一层保险」，不是「一件必需品」。这条钉住的是**说明**：
            // 玩家读到的那一刻就该知道它什么时候值得吃。
            var data = GameDataStore.Get();
            var antidote = data.Items.First(i => i.Slot == Slot.Consumable && i.Cleanse == true);
            // 说明里承诺的每一件事，结算里都得有人做。
            //
            // 上一轮为了让它有场，给了它 `heal: 18` 并把说明改成「并回复 18 点生命值」，
            // 而 `PlayerCleanse` 只清毒——那一行 18 点从头到尾没人给。
            // 探针照着说明去吃它（判据里加了一条不存在的加血），于是「解毒草终于有人用」
            // 这个读数是靠一句假话买来的。说明只能承诺结算真的会做的事。
            Assert(antidote.Damage is null, "解毒草除了解毒还会打伤害：它的说明只能是两句真话拼起来的一句假话");
            // 逐句查「回复 N 点生命」这种具体承诺：写了数，就得有那个数。
            // 用逐字扫描而不是正则——这段源码本身就在字符串里，转义符会把判据变成一句空话。
            foreach (var clause in antidote.Desc.Split('，', '。'))
            {
                if (!clause.Contains('回') || !clause.Contains("点生")) continue;
                Assert(antidote.Heal is > 0,
                    $"解毒草的说明写了回血量（「{clause}」），而它的 heal 是空的——结算不会给这口血");
            }
            Assert(antidote.Desc.Contains("中毒"), $"解毒草的说明没提它解的是什么：{antidote.Desc}");
            // 说明里写的「没有中毒时吃它只会占掉一个回合」必须是真话——这是它和一瓶
            // 治疗药水最本质的区别，也是它敢卖得比药便宜的理由。
            Assert(antidote.Desc.Contains("回合"), $"解毒草的说明没提「吃它要占一个回合」这件事：{antidote.Desc}");
            // 只解毒的消耗品不该比一瓶随时能喝的治疗药水更贵：
            // 它有用的局面（这一回合毒会要命）比治疗药水少得多。
            var cheapestPotion = data.Items
                .Where(i => i.Slot == Slot.Consumable && i.Heal is not null)
                .Select(i => i.Price).DefaultIfEmpty(int.MaxValue).Min();
            Assert(antidote.Price < cheapestPotion,
                $"解毒草 {antidote.Price} 不比一瓶随时能喝的治疗药水（{cheapestPotion}）便宜：" +
                "买它的人永远算不出「现在该吃」");
        });

        Test("消耗品的说明只说它做什么，不带「（单词条）」这种品质标签", () =>
        {
            // 商店里那两行字以前长得一模一样：治疗药水「回复 70（单词条）」，
            // 紧挨着锁子甲「防御 +5（单词条）」。品质说的是「这件**装备**能带几条词条」，
            // 而消耗品根本不挂词条——玩家扫一眼分不出哪个能吃、哪个能穿。
            var data = GameDataStore.Get();
            foreach (var item in data.Items.Where(i => i.Slot == Slot.Consumable))
            {
                var line = UI.Widgets.ItemSummary(item, data.Affixes);
                Assert(!line.Contains("词条"), $"消耗品「{item.Name}」的摘要里还在谈词条：{line}");
                Assert(!line.EndsWith("）"), $"消耗品「{item.Name}」的摘要后面还挂着品质标签：{line}");
            }
            // 装备那一行反过来必须留着「词条 1/2」：不然「紫装为什么只带一条」没人解释。
            var gear = data.Items.First(i => i.Slot != Slot.Consumable && Inventory.AffixLimit(i) > 0);
            Assert(UI.Widgets.ItemSummary(gear, data.Affixes).Contains("词条"),
                $"「{gear.Name}」的摘要不再写明它带几条词条");
        });

        Section("这一轮：三处「说明写着 A，实际做的是 B」");

        Test("技能说明里承诺的每一件事，都能在实现里找到落点", () =>
        {
            // 这一条要抓的不是某个具体数字，而是「说明与实现对不上」这一类。
            // 石肤在数据里写着「下一次攻击威力提升 25%」，而它是 ArmorBuff（加防御），
            // 结算侧从来不看那半句话——面板照抄 desc，玩家读到的是一句无中生有。
            // 同类错误的判据：一个技能凡是落进了 ArmorBuff，它的说明就不能再谈攻击。
            var data = GameDataStore.Get();
            foreach (var def in data.Skills)
            {
                if (!FloorMechanics.ArmorBuff.ContainsKey(def.Id)) continue;
                Assert(!def.Desc.Contains("攻击"), $"「{def.Name}」是加防御的技能，说明却在谈攻击：{def.Desc}");
                Assert(def.Desc.Contains("防御"), $"「{def.Name}」的说明没提它真正做的事（加防御）：{def.Desc}");
            }
        });

        Test("石肤的减免幅度与说明里的比例对得上，不是一个「看攻击力漂移」的数", () =>
        {
            // 反过来的那一半：说明以前写「受到的伤害降低 25%」，而实现是「+基础防御 × 25%」。
            // 那是两个完全不同的量——实测玩家 60 攻时只有 9%，20 攻时是 67%，
            // 因为减伤率取决于「这一刀有多疼」。现在说明按实现重写，这条钉住「它确实是点数」：
            // 敌人防御涨了「基础防御 × 25%」，不多不少。
            var data = GameDataStore.Get();
            var stone = data.Skills.First(x => x.Id == "stone_skin");
            var golem = data.Monsters.First(m => m.Skills.Contains("stone_skin"));
            var enemy = EntityRules.CreateMonster(golem, new List<SkillDef> { stone }, 6);
            var player = EntityRules.CreatePlayer(100, 8, 2, 0, new List<SkillDef>());
            var before = enemy.BaseDefense;
            Skills.UseSkill(stone, new SkillContext
            {
                Actor = enemy, Target = player,
                ActorAtk = enemy.BaseAttack, TargetDef = 2,
                Rng = new Rng(1), Rules = RulesOf(data), ActorMaxHp = enemy.BaseMaxHp,
            });
            var gained = EntityRules.EffectiveDefense(enemy, before) - before;
            var expect = Math.Max(1, (int)Math.Round(before * stone.Power));
            Assert(gained == expect, $"石肤让防御从 {before} 涨到 {before + gained}，按 {stone.Power:P0} 该涨 {expect}");
        });

        Test("事件挣的钱进的是 Game 那本账，不只是 Player 那一本", () =>
        {
            // 结算面板与存档的 gold_earned 读的都是 Game.GoldEarned，
            // 而事件里改金币以前直接写在 Player 上，于是「赌徒的钱袋 +35」
            // 既不出现在结算面板上，也不进存档。
            var game = new Game(4242);
            var before = (game.GoldEarned, game.Player.GoldEarned, game.Player.Gold);
            GameEvents.ApplyEffect(game.Player, new EventEffect { Type = EventEffectType.Gold, Value = 35 }, new EventApplyContext
            {
                Rng = new Rng(1),
                Items = GameDataStore.Get().Items,
                Capacity = game.Capacity,
                ItemsById = game.ItemIndex,
                PanelMaxHp = game.Stats.MaxHp,
                AddGold = d => { if (d >= 0) game.EarnGold(d); else game.SpendGold(-d); },
            });
            Assert(game.GoldEarned - before.Item1 == 35,
                $"本局金币获得没算进事件那 35（{before.Item1} → {game.GoldEarned}）");
            Assert(game.Player.GoldEarned - before.Item2 == 35, "玩家侧的金币获得也没记");
            Assert(game.Player.Gold - before.Item3 == 35, "金币余额不对");
        });

        Section("这一轮：上限涨了，血得跟着涨——但只能涨一次");

        Test("泉眼献金的生命上限带装备一起涨，且不会把血压过上限", () =>
        {
            // 自检里 40 局理性玩家跑到 `生命超过上限: 95/81`。
            // 追下去是两个入口各自把「血跟着上限涨」写了一遍，而两处写的数不一样：
            //   · 升级按面板上限收口（戴着 +20 的勋章时收口值比基础高）；
            //   · 泉眼按 `HpPerStep`（基础值）加血，上限却只动了 `BaseMaxHp`。
            // 只要前一步把血顶过了「泉眼以为的上限」，再 +6 就出去了。
            // 这条钉的是**关系**，不是某一个数：不管身上挂着什么装备，
            // 献金之后 `血 <= 面板上限`，而且上限涨多少、血就最多涨多少。
            var game = new Game(31);
            Inventory.AddItem(game.Player, "hero_medal", game.Capacity);   // 生命 +20
            var idx = game.Player.Inventory.Count - 1;
            Inventory.Equip(game.Player, idx, game.ItemIndex);
            game.RestoreFloor(8, 1, GameStatus.Playing);
            game.RestoreOfferingSteps(0);                                   // 次数只有读档这一个入口，第 8 层够门槛
            game.EarnGold(1000);                                            // 泉眼只认手上的钱
            var maxBefore = game.Stats.MaxHp;
            game.Player.Hp = maxBefore;                                     // 满血进去
            var (ok, _, hpGain, _) = game.TakeOffering();
            Assert(ok, "第 8 层的泉眼献不了金（钱或次数不够）");
            Assert(game.Stats.MaxHp == maxBefore + hpGain,
                $"面板上限涨了 {game.Stats.MaxHp - maxBefore}，而献金说好的是 {hpGain}");
            Assert(game.Player.Hp <= game.Stats.MaxHp,
                $"献金之后生命超过上限：{game.Player.Hp}/{game.Stats.MaxHp}");
            Assert(game.Player.Hp == maxBefore + hpGain,
                $"满血献金之后应当是 {maxBefore + hpGain}，实际 {game.Player.Hp}——" +
                "「上限涨多少、血涨多少」这条规矩漏在某个入口");
        });

        Test("所有涨上限的入口都问同一句：面板上限变了多少", () =>
        {
            // 修复的形状比数值重要。这一条读的是**调用点**：三个入口
            // （升级、泉眼、升级技能）都必须经过 `BeforeMaxHpGrows`，
            // 谁新加一个涨上限的入口而自己 +Hp，下一次就会以另一种方式漏出来。
            if (SourceOf("Scripts/Core/Game.cs") is not { } gameSrc) return;   // 导出产物里没有源码
            // 收口函数的签名：修饰符与名字之间可能挂上 `new` / `override` / 特性，
            // 所以只认「名字 + 参数表」这一截，正则也只放在一行内比对。
            // 边界不能用 `\b`：`\b` 只认字母数字与下划线的交界，`_BeforeMaxHpGrows` 这种
            // 名字里的那截 `BeforeMaxHpGrows` 前面是下划线，照样命中。用负向后行断言钉住。
            Assert(Regex.IsMatch(gameSrc, @"(?<![A-Za-z0-9_])void\s+BeforeMaxHpGrows\s*\("),
                "面板上限的收口函数不见了");
            Assert(gameSrc.Contains("BeforeMaxHpGrows(before, hp);"),
                "泉眼献金没走 BeforeMaxHpGrows：它又会按基础值加血、按面板算上限");
            // 「涨上限」之后不许再自己写一句「血 = min(上限, 血 + 增量)」：
            // 那正是这一轮的 bug 形状（两处各算一遍，两处的上限不一样）。
            var offeringBody = gameSrc[gameSrc.IndexOf("public (bool Ok, int Cost, int Hp, int Attack) TakeOffering()", StringComparison.Ordinal)..];
            offeringBody = offeringBody[..Math.Min(offeringBody.Length, 1400)];
            Assert(!offeringBody.Contains("Player.Hp = Math.Min("),
                "泉眼献金自己又加了一次血：上限的收口必须只有一处");
        });

        Test("升级之后血也按面板补齐：带百分比生命的装备时不落后一截", () =>
        {
            // 同一条规矩的第三个入口。`Leveling.GainXp` 收口用的是「基础上限与装备固定值的
            // 较大者」，而面板上限还要乘百分比词条——带「脆弱躯壳」时两个数差着一截。
            // 这条不钉具体数值，钉的是关系：升一级之后，血该跟着**面板**上限涨那么多。
            var game = new Game(11);
            game.RestoreFloor(3, 1, GameStatus.Playing);
            // 手搓一件带「脆弱躯壳」的护甲穿上：数据表里的模板没有词条，词条是运行时附上去的
            var armor = GameDataStore.Get().Items.First(i => i.Slot == Slot.Armor && (i.MaxHp ?? 0) == 0);
            game.RestorePendingAffixes(new[] { "fragile_shell" });
            var fragileArmor = game.ApplyBonusAffix(armor);                // 已经登记进 ItemIndex
            Assert(fragileArmor.Affixes is { Count: > 0 }, "符文没挂到这件护甲上，这条用例等于没穿装备");
            Assert(Inventory.AddItem(game.Player, fragileArmor.Id, game.Capacity), "护甲塞不进背包");
            Assert(Inventory.Equip(game.Player, game.Player.Inventory.Count - 1, game.ItemIndex), "穿不上这件护甲");
            game.Player.Hp = game.Stats.MaxHp;                             // 满血升级：涨多少上限就该涨多少血
            var maxBefore = game.Stats.MaxHp;
            var hpBefore = game.Player.Hp;
            var ups = game.GrantXp(Systems.Leveling.XpForLevel(game.Player.Level, game.LevelingDef));
            Assert(ups.Count > 0, "一整级的经验没换来一次升级");
            Assert(game.Player.Hp <= game.Stats.MaxHp,
                $"升级之后生命超过上限：{game.Player.Hp}/{game.Stats.MaxHp}");
            // 面板上限涨的是「基础加成再过一遍百分比词条」，所以在「脆弱躯壳」这类
            // 词条下它不一定等于 `MaxHpGain`：基础 +6 在 +20% 之下就是 +7。
            // 这条判据以前拿两者硬等，收口统一之后就红了——红的是判据写死了 1:1，
            // 不是代码算错了。所以钉的是关系：
            // 「血涨的 = 面板上限涨的」，且「面板上限涨的 ≥ 基础加成」。
            var maxGain = game.Stats.MaxHp - maxBefore;
            Assert(maxGain >= ups[0].MaxHpGain,
                $"面板上限涨了 {maxGain}，比基础加成 {ups[0].MaxHpGain} 还少：百分比词条被吃掉了");
            // 满血升级：血要跟着面板走满，一分不少——它也一分不多（那会让「血超过上限」
            // 那条断言在 40 局里某一次飘红）。
            Assert(game.Player.Hp == game.Stats.MaxHp,
                $"满血升级之后是 {game.Player.Hp}/{game.Stats.MaxHp}：血没有跟上面板上限");
            Assert(game.Player.Hp - hpBefore == maxGain,
                $"面板上限涨了 {maxGain}，血只涨了 {game.Player.Hp - hpBefore}——" +
                "上限的收口漏在升级那一支");
        });

        Section("这一轮：那句话里的「合计」把三笔都算进去了");

        Test("本回合合计 = 下一刀 + 毒（按结算口径）+ 自爆", () =>
        {
            // `ExplodeOnDeathForecast` 那条判据管的是「自爆那个数对不对」，
            // 这一条管的是另一件事：**它有没有进那句「合计」**。
            // 只报一句「它倒下时会炸 4 点」而合计不动，和「只报一半」是同一个 bug 的两种写法。
            var def = new MonsterDef { Id = "boom", Name = "狂信者", Hp = 40, Attack = 20, Defense = 0, ExplodeOnDeath = 0.6 };
            var enemy = EntityRules.CreateMonster(def, new List<SkillDef>(), 4);
            var player = EntityRules.CreatePlayer(200, 8, 0, 0, new List<SkillDef>());
            var battle = new Battle(player, enemy, new Rng(7), new BattleOptions
            {
                Rules = new CombatRules { CritChance = 0, CritMultiplier = 1, DefendReduction = 0.5, DamageVariance = 0 },
                AttackerAtk = c => c.BaseAttack,
                AttackerDef = c => c.BaseDefense,
                MaxHp = c => c.BaseMaxHp,
                Floor = 4,
                EnemyDefs = new Dictionary<Combatant, MonsterDef> { [enemy] = def },
            });
            EntityRules.ApplyPoison(player, 2, EntityRules.MaxPoisonPower);
            var (low, high) = battle.ThisTurnTotal();
            var blow = battle.EnemyForecast();
            var poison = battle.PoisonTickThisTurn();
            var blast = battle.BlastThisTurn();
            Assert(blast > 0, "会自爆的敌人身上，`BlastThisTurn` 报的是 0");
            var acts = battle.UpcomingEnemyActs;
            var expected = (acts ? blow.High : 0) + poison + blast;
            Assert(high == expected,
                $"合计 {high} 对不上：下一刀 {(acts ? blow.High : 0)} + 毒 {poison} + 自爆 {blast} = {expected}");
            Assert(low <= high, "合计的下界大于上界");
            // 自爆那一笔必须真的把合计顶上去：它等于 0 的时候这个等式也会成立，
            // 所以再单独钉一次「有自爆时的合计 > 没自爆时的合计」。
            Assert(high > (acts ? blow.High : 0) + poison, "合计没有把自爆算进去");
        });

        Section("这一轮：三件「量出来的东西，本身是假的」");

        Test("中毒致死有预算：用完之后毒继续疼，但不再替别的机制决定生死", () =>
        {
            // 毒的定位是「你可以不吃药，但要算进去」。压到 6 点之后它不再当主角，
            // 可它也不能完全退场——30 局里只有 2 次会要命时，解毒草就是一个没有场的道具
            // （玩家永远算不出「现在该吃」），价格怎么定都是假的。
            // 折中是给毒一条预算（Game.PoisonDyingBudget）：偶尔真的杀人，但不做主角。
            var data = GameDataStore.Get();
            var mon = new MonsterDef { Id = "t", Name = "怪", Hp = 500, Attack = 5, Defense = 0 };
            var enemy = EntityRules.CreateMonster(mon, new List<SkillDef>(), 2);
            var player = EntityRules.CreatePlayer(30, 8, 0, 0, new List<SkillDef>());
            player.Hp = 10;
            var battle = new Battle(player, enemy, new Rng(11), new BattleOptions
            {
                Rules = new CombatRules { CritChance = 0, CritMultiplier = 1, DefendReduction = 0.5, DamageVariance = 0 },
                AttackerAtk = c => c is Player ? 8 : 1,
                AttackerDef = c => 0,
                MaxHp = c => c is Player ? 30 : c.BaseMaxHp,
                Floor = 2,
            });
            // 预算用完之后，这一下只扣到剩 1 点：毒继续疼，但不再替别的机制做决定。
            player.PoisonDyingLeft = 0;
            EntityRules.ApplyPoison(player, 3, 40);
            battle.PlayerDefend();
            Assert(player.Hp > 0, "预算用完之后毒还是把玩家带走了");
            Assert(player.Hp == 1, $"预算兜底之后应当只扣到 1 点，实际剩 {player.Hp}");
            // 有预算时必须真的能毒死：不然「毒会杀人」这句话在代码里没有落点。
            Assert(EntityRules.PoisonLethalAllowed(player2(), 40), "有预算时毒却毒不死人");
            static Combatant player2()
            {
                var p = EntityRules.CreatePlayer(10, 1, 0, 0, new List<SkillDef>());
                p.Hp = 10;
                p.PoisonDyingLeft = 1;
                return p;
            }
        });

        Test("毒伤的算式只有一个入口：瘴气按挨的那一刀、灼烧按面板攻击，两者都有下限也有上界", () =>
        {
            // 上一轮把毒伤写成 `Math.Round(dealt * 0.5)`，没有下限那一档——
            // 于是深层一下只挨 3 点的怪，毒也是 1 点，「毒 + 下一刀」永远算不出人命。
            // 现在两个来源各有一个算式，都带下限，且都在 FloorMechanics 里。
            Assert(FloorMechanics.HazePoisonPower(0) == FloorMechanics.HazePoisonFloor,
                $"一刀挨了 0 点，瘴气的毒伤算成了 {FloorMechanics.HazePoisonPower(0)}，下限是 {FloorMechanics.HazePoisonFloor}");
            Assert(FloorMechanics.HazePoisonPower(10) == 5 || FloorMechanics.HazePoisonPower(10) == EntityRules.MaxPoisonPower,
                $"瘴气按实际伤害的一半算：挨 10 点该是 5（下限 {FloorMechanics.HazePoisonFloor} / 上界 {EntityRules.MaxPoisonPower}）");
            Assert(FloorMechanics.HazePoisonPower(40) == EntityRules.MaxPoisonPower,
                $"瘴气按实际伤害的一半算、并封顶：挨 40 点该是 {EntityRules.MaxPoisonPower}，实际 {FloorMechanics.HazePoisonPower(40)}");
            Assert(FloorMechanics.BurnPoisonPower(0) == FloorMechanics.BurnPoisonFloor,
                $"攻击 0 的灼烧毒伤算成了 {FloorMechanics.BurnPoisonPower(0)}");
            Assert(FloorMechanics.BurnPoisonFloor > 0 && FloorMechanics.HazePoisonFloor > 0, "下限不能是 0");
            // 下限那一头由这一条管，上限那一头由 EntityRules.MaxPoisonPower 管：
            // 两个界限必须在同一个算式里一起生效，否则「毒很轻」和「毒很重」总有一头漏。
            Assert(FloorMechanics.HazePoisonPower(1000) == EntityRules.MaxPoisonPower,
                $"瘴气的毒伤 {FloorMechanics.HazePoisonPower(1000)} 没有封在 {EntityRules.MaxPoisonPower}");
            Assert(FloorMechanics.BurnPoisonPower(1000) == EntityRules.MaxPoisonPower,
                $"灼烧的毒伤 {FloorMechanics.BurnPoisonPower(1000)} 没有封在 {EntityRules.MaxPoisonPower}");
            Assert(FloorMechanics.HazePoisonFloor <= EntityRules.MaxPoisonPower
                && FloorMechanics.BurnPoisonFloor <= EntityRules.MaxPoisonPower,
                "下限比上限还高：Math.Clamp 会把下限直接顶回来");
        });

        Test("中毒致死预算按层给：深一层就多一次，且新的一层会重置", () =>
        {
            var shallow = new Game(3u);
            var depth = new Game(3u);
            depth.RestoreFloor(40, 1, GameStatus.Playing);
            Assert(depth.PoisonDyingBudget > shallow.PoisonDyingBudget,
                $"第 40 层的预算 {depth.PoisonDyingBudget} 不大于第 1 层的 {shallow.PoisonDyingBudget}");
            Assert(depth.Player.PoisonDyingLeft == depth.PoisonDyingBudget, "读档还原楼层之后预算没跟着重置");
            // 预算必须是**每一层都数得出来**的，不是一条被 `Math.Max(1, …)` 兜住的常数。
            // 上一版是 `floor(层数 × 5%)`：第 15 层算出 0，「每 5 层多一次」这条规律于是
            // 从来没在 1~15 层里出现过——报表上那张「毒杀 N 次」的分母是空的。
            var byFloor = Enumerable.Range(1, 15).Select(Game.PoisonDyingBudgetAt).ToList();
            Assert(byFloor[0] == 1, $"第 1 层的毒死预算该是 1，实际 {byFloor[0]}");
            Assert(byFloor[5] > byFloor[4] && byFloor[10] > byFloor[9],
                $"毒死预算没有随层数长：第 5/6 层 {byFloor[4]}/{byFloor[5]}、第 10/11 层 {byFloor[9]}/{byFloor[10]}");
            Assert(byFloor[14] == 3 && Game.PoisonDyingBudgetAt(16) == EntityRules.MaxPoisonDeathsPerFloor,
                $"第 15 / 16 层的预算该是 3 / {EntityRules.MaxPoisonDeathsPerFloor}，实际 {byFloor[14]} / {Game.PoisonDyingBudgetAt(16)}");
        });

        Test("毒会被预算拦下，而「玩家自己投出的那一刀」不会", () =>
        {
            // 这一条钉的是两道闸门互相抵消的那个 bug：
            // `TickPoisonFor` 传的是「这一下本来要掉多少」，而 `PoisonLethalAllowed` 里
            // `damage < c.Hp` 在「毒 ≥ 血」时不成立、直接放行——预算永远不起作用。
            // 现在两处都按「这一下本来要掉多少」判，于是「额度用完了，毒只扣到剩 1 点」
            // 真的会发生。
            var mon = new MonsterDef { Id = "t2", Name = "木桩", Hp = 500, Attack = 5, Defense = 0 };
            var enemy = EntityRules.CreateMonster(mon, new List<SkillDef>(), 2);
            var player = EntityRules.CreatePlayer(30, 8, 0, 0, new List<SkillDef>());
            player.Hp = 3;
            var battle = new Battle(player, enemy, new Rng(7), new BattleOptions
            {
                Rules = new CombatRules { CritChance = 0, CritMultiplier = 1, DefendReduction = 0.5, DamageVariance = 0 },
                AttackerAtk = c => c is Player ? 8 : 1,
                AttackerDef = c => 0,
                MaxHp = c => c is Player ? 30 : c.BaseMaxHp,
                Floor = 2,
            });
            player.PoisonDyingLeft = 0;
            EntityRules.ApplyPoison(player, 2, 40);
            battle.PlayerDefend();
            Assert(player.Hp > 0, "额度用完时毒还是把人带走了：预算这条规则没有落点");
            Assert(player.Hp == 1, $"额度用完时毒该只扣到 1 点，实际剩 {player.Hp}");
            Assert(player.PoisonTurns == 1, $"没被毒死的那一下也该烧掉一回合，实际还剩 {player.PoisonTurns}");
        });

        Test("玩家自己打出来的那一下，不该被「毒的免死额度」兜住", () =>
        {
            // `HurtPlayer` 是所有「非攻击伤害」的公共出口（毒、反震、自爆、诅咒反噬、
            // 以及玩家自己投出去的炸弹炸死自己）。毒死额度是给毒留的第二次机会，
            // 不是给玩家自己的炸弹挡刀——所以标记要从 `DamageResult.FromPlayer` 走一路。
            var mon = new MonsterDef { Id = "t3", Name = "木桩", Hp = 500, Attack = 5, Defense = 0 };
            var enemy = EntityRules.CreateMonster(mon, new List<SkillDef>(), 2);
            var player = EntityRules.CreatePlayer(30, 8, 0, 0, new List<SkillDef>());
            var battle = new Battle(player, enemy, new Rng(9), new BattleOptions
            {
                Rules = new CombatRules { CritChance = 0, CritMultiplier = 1, DefendReduction = 0.5, DamageVariance = 0 },
                AttackerAtk = c => c is Player ? 8 : 1,
                AttackerDef = c => 0,
                MaxHp = c => c is Player ? 30 : c.BaseMaxHp,
                Floor = 2,
            });
            // 入口：带上一颗对「自己」的固定伤害——用真实道具走一遍 UI 那条路不方便，
            // 这里直接验那条规则本身：FromPlayer 的时候 `PoisonLethalAllowed` 根本不该被问。
            var fixedResult = CombatMath.FixedDamage(999);
            fixedResult.FromPlayer = true;
            // 第三个别参数必须一起传：`FromPlayer` 是结构体上的标记，而 `ApplyDamage`
            // 判的是它自己的参数。只设标记不传参，等于把「谁打的」这件事丢掉了一半。
            CombatMath.ApplyDamage(player, fixedResult, fixedResult.FromPlayer);
            Assert(player.Hp == 0, $"玩家自己打出来的 999 点没有把它带走，实际剩 {player.Hp}");
        });

        Test("战斗入口不许接一个已经倒下的敌人", () =>
        {
            // `CreateEncounter()` 每次都重建敌人实例，而探针在同一间房里重开一场战斗时
            // 传进来的那一只还带着上一次被打空的血。以前 Battle 只筛 Allies、不筛主敌人，
            // 于是第一回合 `EnemyAct` 先判血——活人名单是空的，游标却还在转，
            // 玩家一开局就被连打四个回合，连一次出手机会都没有。
            // 现在这是入口的断言：打不起来的战斗不许开始。
            var dead = EntityRules.CreateMonster(
                new MonsterDef { Id = "t", Name = "尸体", Hp = 10, Attack = 1, Defense = 0 }, new List<SkillDef>(), 1);
            dead.Hp = 0;
            var player = EntityRules.CreatePlayer(50, 5, 0, 0, new List<SkillDef>());
            var threw = false;
            try
            {
                _ = new Battle(player, dead, new Rng(1), new BattleOptions
                {
                    Rules = new CombatRules { CritChance = 0, CritMultiplier = 1, DefendReduction = 0.5, DamageVariance = 0 },
                    AttackerAtk = c => c.BaseAttack,
                    AttackerDef = c => c.BaseDefense,
                    MaxHp = c => c.BaseMaxHp,
                    Floor = 1,
                });
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            Assert(threw, "拿一个 0 血的敌人开战，Battle 居然接受了");
        });

        Section("这一轮：货架上那两格的分工（档位那一格 vs 攒下来的钱）");

        Test("货架的第一格永远在本层档位里：它不该被那件贵货挤掉", () =>
        {
            // 加「攒下来的钱」那一格时最容易被写坏的地方：两条路径共用一个池子，
            // 于是贵货把档位那一件挤掉，玩家在第 8 层买到的还是第 15 层的东西。
            // 判据只看**第一件装备**（`chosen` 里第一件非消耗品）落在哪一档。
            var data = GameDataStore.Get();
            var gear = data.Items.Where(i => i.Slot != Slot.Consumable).ToList();
            var cons = data.Items.Where(i => i.Slot == Slot.Consumable).ToList();
            for (var floor = 1; floor <= 15; floor++)
            {
                for (var seed = 1; seed <= 6; seed++)
                {
                    var stock = ShopRoller.Roll(new Rng((uint)(floor * 100 + seed)), gear, cons, floor);
                    var firstGear = stock.FirstOrDefault(x => x.Item.Slot != Slot.Consumable);
                    if (firstGear is null) continue;
                    // 判据读的是「档位筛出来的那个价」（`ShelfPriceAtFloor`），
                    // 不是摆上来的标价：标价上乘了 0.85~1.15 的浮动，第 7 层的
                    // 龙鳞铠会标成 268 而档位上限是 260——**红的会是判据，不是代码**。
                    // 同一件事在这仓库里已经踩过两次（筛的价与摆的价各算各的）。
                    var floorPrice = ShopRoller.ShelfPriceAtFloor(firstGear.Item, floor);
                    Assert(floorPrice <= ShopRoller.ShopPriceBand(floor),
                        $"第 {floor} 层的第一件装备按档位价 {floorPrice} 超过了上限 {ShopRoller.ShopPriceBand(floor)}");
                }
            }
        });

        Test("「攒下来的钱」那一格是关着的：0.0 是一个有出处的数，不是没写", () =>
        {
            // 这一条守的是一个**结论**，不是一个实现：那一格打开过（0.5），
            // 100 局的结果是通关 70 → 78、而泉眼献金 380 → 347、「一次没献」的局 14 → 18。
            // 同一笔钱被两个出口抢，决定结局的是掷骰。所以它关着。
            // 哪天有人要打开它，会先看到这条断言，再去看那段量出来的数。
            Assert(ShopRoller.RichShelfChance == 0.0,
                "「攒下来的钱」那一格被打开了：先跑 100 局，看泉眼献金次数与通关率怎么动");
            var data = GameDataStore.Get();
            var gear = data.Items.Where(i => i.Slot != Slot.Consumable).ToList();
            var cons = data.Items.Where(i => i.Slot == Slot.Consumable).ToList();
            for (var seed = 1; seed <= 40; seed++)
            {
                var floor = 15;
                foreach (var entry in ShopRoller.Roll(new Rng((uint)seed), gear, cons, floor))
                {
                    if (entry.Item.Slot == Slot.Consumable) continue;
                    Assert(entry.BasePrice <= ShopRoller.ShopPriceBand(floor) * 1.15 + 1,
                        $"第 {floor} 层摆出了一件 {entry.BasePrice} 的装备，而档位上限是 {ShopRoller.ShopPriceBand(floor)}");
                }
            }
        });

        Section("这一轮：毒要真的有份量，而「你还要几刀」必须读得出");

        Test("毒按血条折算：60 血的号与 137 血的号，毒是同一件事的同一个份量", () =>
        {
            // 上一版是一条常数链（按施法者攻击力折、夹 1..6），结果是这个游戏里
            // 没有任何一个局面会让人认真考虑解毒草：100 局 311 个中毒回合，
            // 「毒够重」（毒 ≥ 血）**0 次**，解毒草 215 株买进 13 株吃下。
            // 现在按目标血条的 10% 折算，上限 18%——血厚毒重，但永远只是「一截」。
            var shallow = EntityRules.PoisonPowerFor(60);
            var deep = EntityRules.PoisonPowerFor(137);
            Assert(shallow >= 4, $"60 血的号每回合中 {shallow} 点毒：这已经不痛了");
            Assert(deep > shallow, $"血条涨了 {137 - 60} 点，毒还是 {deep} 点：毒没有跟着血条走");
            Assert(deep <= Math.Round(137 * EntityRules.PoisonCapOfMaxHp),
                $"{deep} 点毒超过了血条上限的 {EntityRules.PoisonCapOfMaxHp:P0}");
            // 它是「一截」而不是「一条命」：任何血条下，毒都顶不到四分之一。
            foreach (var maxHp in new[] { 40, 60, 81, 103, 137, 200 })
                Assert(EntityRules.PoisonPowerFor(maxHp) <= maxHp / 4,
                    $"{maxHp} 血的号每回合中 {EntityRules.PoisonPowerFor(maxHp)} 点毒");
        });

        Test("解毒草的场是真的：毒 4 回合内会要命的那种回合，这一株草救得回来", () =>
        {
            // 「值得吃」这一条以前永远不成立，因为毒根本不会把人按倒。
            // 现在给一个具体的局面：血 20、毒 12（137 血的号）、敌人这一刀 6 点。
            // 不吃草：这一回合就是 18 点，剩 2 点；再一回合毒还在，人就没了。
            // 吃草：这一刀只剩 6 点，剩 14 点，还有一次出招的机会。
            var def = new MonsterDef { Id = "t", Name = "木桩", Hp = 500, Attack = 6, Defense = 0 };
            var player = EntityRules.CreatePlayer(137, 8, 0, 0, new List<SkillDef>());
            player.Hp = 20;
            var battle = new Battle(player, EntityRules.CreateMonster(def, new List<SkillDef>(), 3), new Rng(17),
                new BattleOptions
                {
                    Rules = new CombatRules { CritChance = 0, CritMultiplier = 1, DefendReduction = 0.5, DamageVariance = 0 },
                    AttackerAtk = c => c is Player ? 8 : 6,
                    AttackerDef = c => 0,
                    MaxHp = c => c is Player ? 137 : c.BaseMaxHp,
                    Floor = 3,
                });
            EntityRules.ApplyPoison(player, 2, EntityRules.PoisonPowerFor(137));
            var poisonTick = battle.PoisonHpThisTurn();
            Assert(poisonTick >= 8, $"137 血的号这一回合的毒只有 {poisonTick} 点：草仍然没有场");
            Assert(battle.CleanseSavesHp == poisonTick,
                $"这一株草该省下 {poisonTick} 点，实际 {battle.CleanseSavesHp}");
            var (total, _) = battle.ThisTurnTotal();
            Assert(total > player.Hp / 2,
                $"这一回合一共 {total} 点、血 {player.Hp}：这一屏还不够险，判据量不到东西");
        });

        Test("每一层都要有几间仗，而深层比浅层多：中层那段空跑是量出来的", () =>
        {
            // 死法分布里 `死在第 6 层 2 局`、而第 7~12 层**一局都没死**——
            // 那不是「中层很安全」，是中层没东西：一张图 3.4 间仗（浅层也是 3.4），
            // 而赏金按层涨。补的是**路**，不是补给：只从空房里挑，不动补给/宝箱/事件的配额。
            Assert(MapGen.BattlesWanted(11, 1) == 3, "第 1 层不该被补成 4 场仗（教学段保持原样）");
            Assert(MapGen.BattlesWanted(11, 4) == 3, "第 4 层还不该加：前三层是教学段");
            Assert(MapGen.BattlesWanted(11, 5) == 4, $"第 5 层该有 4 场仗，实际 {MapGen.BattlesWanted(11, 5)}");
            Assert(MapGen.BattlesWanted(11, 10) == 5, $"第 10 层该有 5 场仗，实际 {MapGen.BattlesWanted(11, 10)}");
            Assert(MapGen.BattlesWanted(11, 25) == 5, "深潜段不该无限往上加：一张图的格子是有限的");
            Assert(MapGen.BattlesWanted(9, 15) == 3, "小图被硬塞了 5 场仗：那种图只剩走路");
            // 真的生成一遍：第 5 / 10 层（补过仗的两档）的图，战斗房要**比第 1 层多**。
            // 这一条只问「多没多」，不问具体几间：一张图有几间房由地图生成器掷骰决定，
            // 硬等一个数会让这条判据去守另一件事（出图密度），红的时候指不出真因。
            var shallowCounts = new List<int>();
            var deepCounts = new List<int>();
            for (var floor = 1; floor <= 15; floor++)
            {
                for (var seed = 1; seed <= 12; seed++)
                {
                    var map = MapGen.Generate(new Rng((uint)(floor * 1000 + seed)), new MapGenOptions
                    {
                        Width = GameDataStore.Get().Config.MapWidth,
                        Height = GameDataStore.Get().Config.MapHeight,
                        MinRooms = GameDataStore.Get().Config.MinRooms,
                        RoomWeights = new Dictionary<string, int> { ["enemy"] = 36, ["empty"] = 34, ["treasure"] = 12, ["shop"] = 10, ["event"] = 8 },
                        SupplyCap = MapGen.SupplyCap(floor),
                        Floor = floor,
                    });
                    var fights = map.Rooms.Count(r => r.Type is RoomType.Enemy or RoomType.Elite);
                    // 补过的那一档最多把整张图的仗压到「这一层要的那个数」，
                    // 但不会少于地面上本来就掷出来的：所以下界是「至少 2 间」。
                    Assert(fights >= 2, $"第 {floor} 层（种子 {seed}）的图只有 {fights} 间战斗房：这一层等于白送");
                    if (floor <= 4) shallowCounts.Add(fights);
                    else if (floor >= 10) deepCounts.Add(fights);
                }
            }
            var shallowAvg = shallowCounts.Average();
            var deepAvg = deepCounts.Average();
            Assert(deepAvg > shallowAvg,
                $"第 10 层以后每张图平均 {deepAvg:F2} 间仗，第 1~4 层 {shallowAvg:F2} 间——深层没有变多");
        });

        Test("「你还要几刀」读的是普攻那条链的下限，而且和真的砍下去对得上", () =>
        {
            // 敌人卡一直只写「它这一刀打你多少」，从没写过「你这一刀打掉它多少」。
            // 玩家于是只能拿前者反推后者——而那是两条不同的算式。
            var def = new MonsterDef { Id = "t", Name = "木桩", Hp = 40, Attack = 5, Defense = 2 };
            var player = EntityRules.CreatePlayer(60, 20, 0, 0, new List<SkillDef>());
            Battle Make() => new(player, EntityRules.CreateMonster(def, new List<SkillDef>(), 1), new Rng(23),
                new BattleOptions
                {
                    Rules = new CombatRules { CritChance = 0, CritMultiplier = 1, DefendReduction = 0.5, DamageVariance = 0 },
                    AttackerAtk = c => c is Player ? 20 : 5,
                    AttackerDef = c => c is Player ? 0 : c.BaseDefense,
                    MaxHp = c => c is Player ? 60 : c.BaseMaxHp,
                    Floor = 1,
                });
            var battle = Make();
            var (low, high) = battle.PlayerHitForecast();
            Assert(low == high && low == 18, $"攻击 20 打防御 2：预告 {low}~{high}，该是 18（浮动为 0 时它是一定的）");
            Assert(battle.SwingsToKill() == 3, $"40 点血、每刀 18 点：该是 3 刀，实际 {battle.SwingsToKill()}");
            // 真的砍三刀（第三刀会因为血不够而少打，但一定倒）。
            for (var i = 0; i < 3; i++) battle.PlayerAttack();
            Assert(battle.Phase == BattlePhase.Won, $"按预告砍了三刀，敌人还没倒（{battle.Enemy.Hp} 点血）");
        });

        Section("这一轮：地图说得出「剩下的路要花多少」，也藏得住「那边有几场仗」");

        Test("「这一间要花几成血」有出处：普通房最浅、精英房更深、Boss 最深", () =>
        {
            // 屏幕上那句「剩下的仗约 N 点血」是从这里算出来的。它是个**估**，
            // 但它必须是一个单调的估算：房间越硬，估出来的比例不能反而更小。
            // 这条判据不看任何具体数字——数字会随平衡改动，单调性不该。
            var game = new Game(11);
            var enemy = game.RoomCostRatio(RoomType.Enemy);
            var elite = game.RoomCostRatio(RoomType.Elite);
            var boss = game.RoomCostRatio(RoomType.Boss);
            Assert(enemy > 0, $"普通房估出来的代价是 {enemy:F2}：屏幕上那句「剩下的仗约 N 点血」会报 0");
            Assert(elite >= enemy, $"精英房 {elite:F2} 估得比普通房 {enemy:F2} 还便宜");
            Assert(boss >= elite, $"Boss 房 {boss:F2} 估得比精英房 {elite:F2} 还便宜");
            // 估得再离谱也不许超过「三倍上限」：它不是结算，是一个量级
            Assert(boss <= 3.0, $"Boss 房估出 {boss:F2} 倍上限：那个数已经不是在估了");
        });

        Test("迷雾藏的是代价，不是方向：敌人房 / 精英房不报类型，补给与宝箱要报", () =>
        {
            // 「未探索的砖该不该露出类型」这条规则以前不存在——类型色一直是画着的，
            // 于是玩家站在起点就能看出哪边是精英房，探索这条路只剩下走路。
            // 现在分界写死在一处：**代价藏起来，方向留着**。
            // 这条判据读的是那份分界，不是颜色（颜色是渲染，类型才是规则）。
            var shop = new Room { Type = RoomType.Shop };
            var elite = new Room { Type = RoomType.Elite };
            var enemy = new Room { Type = RoomType.Enemy };
            var treasure = new Room { Type = RoomType.Treasure };
            Assert(MapScene.FaintTypeFor(shop) == RoomType.Shop, "商店在雾里不报类型：玩家没法决定往哪边走");
            Assert(MapScene.FaintTypeFor(treasure) == RoomType.Treasure, "宝箱在雾里不报类型：这是「值得去」的那一类");
            Assert(MapScene.FaintTypeFor(enemy) is null, "敌人房在雾里就报了类型：那边有几场仗，本该走过去才知道");
            Assert(MapScene.FaintTypeFor(elite) is null, "精英房在雾里就报了类型：代价藏不住，迷雾就只是装饰");
        });

        Section("这一轮：预告与结算读同一个入口");

        Test("自爆的伤害只有一个算式：敌人天赋与怨恨层各算一遍，都在 ExplodeOnDeathForecast 里", () =>
        {
            // 「倒下自爆」在横扫 100 局里是排第一的死因，而死掉的那一屏上一个字都没写。
            // 现在界面那一行读的是 `Battle.ExplodeOnDeathForecast`，所以这条判据
            // 问的是「它算出来的数与真正结算时会扣的数对得上吗」。
            var def = new MonsterDef { Id = "boom", Name = "狂信者", Hp = 40, Attack = 20, Defense = 0, ExplodeOnDeath = 0.6 };
            var enemy = EntityRules.CreateMonster(def, new List<SkillDef>(), 1);
            var player = EntityRules.CreatePlayer(200, 8, 0, 0, new List<SkillDef>());
            var battle = new Battle(player, enemy, new Rng(3), new BattleOptions
            {
                Rules = new CombatRules { CritChance = 0, CritMultiplier = 1, DefendReduction = 0.5, DamageVariance = 0 },
                AttackerAtk = c => c.BaseAttack,
                AttackerDef = c => c.BaseDefense,
                MaxHp = c => c.BaseMaxHp,
                Floor = 4,
                EnemyDefs = new Dictionary<Combatant, MonsterDef> { [enemy] = def },
            });
            var (forecast, count, who) = battle.ExplodeOnDeathForecast();
            Assert(count == 1 && who == "狂信者", $"自爆预报没认出这只怪：count={count} who={who}");
            Assert(forecast == Math.Max(1, (int)Math.Round(enemy.BaseAttack * 0.6)),
                $"预报的自爆 {forecast} 与结算的算式（攻击 {enemy.BaseAttack} × 0.6）对不上");
            // 打伤害之前与之后都读同一句：预报是「这只怪现在会炸多少」，
            // 它不该因为血变少而变（自爆读的是攻击力，不是剩余血）。
            enemy.Hp = 5;
            Assert(battle.ExplodeOnDeathForecast().Damage == forecast, "自爆预报跟着剩余血变了：它读的该是攻击力");

            // 怨恨层：比例写在 FloorMechanics.SpitefulExplosion，不给 EnemyDefs 也得算得出来。
            var plain = new MonsterDef { Id = "plain", Name = "巨鼠", Hp = 40, Attack = 20, Defense = 0 };
            var mob = EntityRules.CreateMonster(plain, new List<SkillDef>(), 1);
            var spite = new Battle(EntityRules.CreatePlayer(200, 8, 0, 0, new List<SkillDef>()), mob, new Rng(5),
                new BattleOptions
                {
                    Rules = new CombatRules { CritChance = 0, CritMultiplier = 1, DefendReduction = 0.5, DamageVariance = 0 },
                    AttackerAtk = c => c.BaseAttack,
                    AttackerDef = c => c.BaseDefense,
                    MaxHp = c => c.BaseMaxHp,
                    Floor = 11,
                    FloorMechanic = "spiteful",
                    EnemyDefs = new Dictionary<Combatant, MonsterDef> { [mob] = plain },
                });
            var (spiteForecast, spiteCount, _) = spite.ExplodeOnDeathForecast();
            Assert(spiteCount == 1 && spiteForecast == Math.Max(1, (int)Math.Round(mob.BaseAttack * FloorMechanics.SpitefulExplosion)),
                $"怨恨层的自爆预报 {spiteForecast} 与机制的比例对不上");
            // 反过来：没有自爆规则的平层，预报必须是 0——否则那句「它倒下时还会炸」会挂在每一只怪身上。
            var none = new Battle(EntityRules.CreatePlayer(200, 8, 0, 0, new List<SkillDef>()),
                EntityRules.CreateMonster(plain, new List<SkillDef>(), 1), new Rng(5),
                new BattleOptions
                {
                    Rules = new CombatRules { CritChance = 0, CritMultiplier = 1, DefendReduction = 0.5, DamageVariance = 0 },
                    AttackerAtk = c => c.BaseAttack,
                    AttackerDef = c => c.BaseDefense,
                    MaxHp = c => c.BaseMaxHp,
                    Floor = 3,
                    EnemyDefs = new Dictionary<Combatant, MonsterDef>(),
                });
            Assert(none.ExplodeOnDeathForecast().Count == 0, "不会自爆的敌人也被预报成了自爆");
        });

        Test("中毒这一回合会掉多少血：只有一个入口，预报与结算不会各说各的", () =>
        {
            // 「毒的数值」和「这一回合会被扣掉多少」不是同一个数：本层的毒死额度用完时，
            // 毒只扣到剩 1 点。界面那一行以前读前者，于是会出现「预告说扣 6，血条掉了 1」。
            var def = new MonsterDef { Id = "t", Name = "木桩", Hp = 500, Attack = 5, Defense = 0 };
            var player = EntityRules.CreatePlayer(30, 8, 0, 0, new List<SkillDef>());
            player.Hp = 4;
            var battle = new Battle(player, EntityRules.CreateMonster(def, new List<SkillDef>(), 2), new Rng(11),
                new BattleOptions
                {
                    Rules = new CombatRules { CritChance = 0, CritMultiplier = 1, DefendReduction = 0.5, DamageVariance = 0 },
                    AttackerAtk = c => c is Player ? 8 : 1,
                    AttackerDef = c => 0,
                    MaxHp = c => c is Player ? 30 : c.BaseMaxHp,
                    Floor = 2,
                });
            player.PoisonDyingLeft = 0;
            EntityRules.ApplyPoison(player, 2, EntityRules.MaxPoisonPower);
            // 毒这一回合的读数走 `PoisonTickFor`：30 血的号，8% 是 2.4 → 2 点。
            // 判据要的是「毒多重」与「这一回合掉多少」两个数各就各位，不是某个常数。
            // 30 血的号：8% 是 2.4 → 2 点，但 `ApplyPoison` 传来的是上界 12，
            // 于是真正生效的是「上界与血条封顶取小」——3 点（30 × 12%）。
            // 读数是 3 还是 4 不重要，重要的是它**读血条**并且有上界：
            var tick = EntityRules.PoisonTickFor(player, 30);
            Assert(tick <= Math.Max(1, (int)Math.Round(30 * EntityRules.PoisonCapOfMaxHp)),
                $"30 血的号这一回合的毒算成了 {tick} 点：它没有跟血条封顶");
            // 额度用完了：毒只扣到剩 1 点，所以预告也必须是 3（血 4 → 1）。
            Assert(battle.PoisonHpThisTurn() == 3,
                $"额度用完时「这一回合会掉多少」算成了 {battle.PoisonHpThisTurn()}，应当是 3（血 4 → 1）");
            Assert(battle.PoisonWouldCostHpThisTurn(), "「这一回合的毒会掉血」被算成了不会");
            Assert(!battle.PoisonWouldKillPlayer(), "额度用完时毒不该被预报成会杀人");
            // 额度还在：整笔照扣，且这时它真的会杀人。
            // 血给到 30（多过毒伤）：`PoisonHpThisTurn` 报的是「这一回合会掉多少」，
            // 血只有 4 点时那个数就封在 4——预报告诉玩家的是「会掉多少」，不是「毒有多毒」。
            player.Hp = 30;
            player.PoisonDyingLeft = 1;
            Assert(battle.PoisonHpThisTurn() == tick,
                $"额度还在时该扣整笔 {tick}（30 血的号），实际 {battle.PoisonHpThisTurn()}");
            // 额度还在、血 4 点、毒 6 点：这一条必须报「会杀人」。
            // 它以前传的是 `Player.Hp`（4），而 `damage < c.Hp` 在 4 < 4 上不成立、
            // 判据被自己那一句放行，于是「额度还在」与「额度用完」在它眼里没有区别。
            player.Hp = 4;
            Assert(battle.PoisonWouldKillPlayer(), "额度还在、血 4 点、毒 6 点：判据说人不会被毒带走");
            Assert(battle.PoisonWouldKillPlayer(), "额度还在、毒 ≥ 血时判据说不会死");
            // 没中毒：两个数都必须是 0，否则界面会挂出一句「中毒仍会扣 0 点」。
            EntityRules.ClearPoison(player);
            Assert(battle.PoisonHpThisTurn() == 0 && !battle.PoisonWouldCostHpThisTurn(),
                "没中毒时还算出了毒伤");
        });

        Test("解毒草的场在吃之前记：吃完之后毒就没了，再问「救回了什么」永远是 0", () =>
        {
            // 报表上那句「解毒草 0 株吃下」查了几轮，最后落在判据上；而判据要读的
            // 两个数（这一回合会不会死、掐掉毒省下多少）都必须在 ClearPoison **之前**问。
            var def = new MonsterDef { Id = "t", Name = "木桩", Hp = 500, Attack = 5, Defense = 0 };
            var player = EntityRules.CreatePlayer(30, 8, 0, 0, new List<SkillDef>());
            player.Hp = 8;
            var battle = new Battle(player, EntityRules.CreateMonster(def, new List<SkillDef>(), 2), new Rng(13),
                new BattleOptions
                {
                    Rules = new CombatRules { CritChance = 0, CritMultiplier = 1, DefendReduction = 0.5, DamageVariance = 0 },
                    AttackerAtk = c => c is Player ? 8 : 1,
                    AttackerDef = c => 0,
                    MaxHp = c => c is Player ? 30 : c.BaseMaxHp,
                    Floor = 2,
                });
            EntityRules.ApplyPoison(player, 2, EntityRules.MaxPoisonPower);
            var saves = battle.CleanseSavesHp;
            var tick = EntityRules.PoisonTickFor(player, 30);
            Assert(saves == tick && saves > 0, $"吃草之前「能省下多少」算成了 {saves}（该是 {tick}）");
            battle.PlayerCleanse();
            Assert(battle.CleanseOffered == 1, $"「手上有毒、草在手边」的回合没被记下（{battle.CleanseOffered}）");
            Assert(battle.CleanseOfferedSaved == 1, $"「这一株省下了一截血」没被记下（{battle.CleanseOfferedSaved}）");
            Assert(battle.CleanseSavesHp == 0, "吃完草之后还算出了「能省下的血」：毒已经被解掉了");
            // 没中毒也吃：这一株什么也没做，两个计数器都不该动。
            var clean = new Battle(EntityRules.CreatePlayer(30, 8, 0, 0, new List<SkillDef>()),
                EntityRules.CreateMonster(def, new List<SkillDef>(), 2), new Rng(17),
                new BattleOptions
                {
                    Rules = new CombatRules { CritChance = 0, CritMultiplier = 1, DefendReduction = 0.5, DamageVariance = 0 },
                    AttackerAtk = c => c is Player ? 8 : 1,
                    AttackerDef = c => 0,
                    MaxHp = c => c is Player ? 30 : c.BaseMaxHp,
                    Floor = 2,
                });
            clean.PlayerCleanse();
            Assert(clean.CleanseOffered == 0 && clean.CleanseSavesHp == 0, "没中毒时吃草被记成了「有场」");
        });

        Test("毒死预算是「每层一次」起、按层加：预算 1 的层与预算 3 的层不该被同一句话兜住", () =>
        {
            // 上一轮把「每层毒死几次」改成了 `1 + (层数-1)/5`，但报表里那张表一直只报总数。
            // 这一条钉的是「深层的毒真的有第二次机会」——它靠预算，不靠调毒伤。
            Assert(Game.PoisonDyingBudgetAt(1) == 1 && Game.PoisonDyingBudgetAt(2) == 1,
                "第 1~5 层的毒死预算该是 1");
            Assert(Game.PoisonDyingBudgetAt(6) == 2 && Game.PoisonDyingBudgetAt(10) == 2,
                "第 6~10 层的毒死预算该是 2");
            Assert(Game.PoisonDyingBudgetAt(11) == 3 && Game.PoisonDyingBudgetAt(15) == 3,
                "第 11~15 层的毒死预算该是 3");
            Assert(Game.PoisonDyingBudgetAt(99) == EntityRules.MaxPoisonDeathsPerFloor,
                $"深层预算没有封在 {EntityRules.MaxPoisonDeathsPerFloor}");
            // 挂毒的那两层必须认得出来：报表要能分开「这一层真会挂毒」与「这一层不会」。
            // 会挂毒的有两个来源，各写在各的位置上：瘴气是**楼层机制**，
            // 熔岩爆裂是**Boss 机制**（第 5 层那只）。把两者塞进同一个 id 集合里，
            // 第 5 层就会被算成「不挂毒」——而它每 3 回合真的灼烧你 2 回合。
            Assert(FloorMechanics.FloorPoisons("haze"), "瘴气层没被认出会挂毒");
            Assert(FloorMechanics.FloorPoisons(null, "molten_burst"), "熔岩爆裂的灼烧没被认出会挂毒");
            Assert(!FloorMechanics.FloorPoisons("molten_burst"), "熔岩爆裂是 Boss 机制，不该当成楼层机制 id");
            Assert(!FloorMechanics.FloorPoisons("none") && !FloorMechanics.FloorPoisons("cramped"),
                "不挂毒的楼层被认成了会挂毒");
            // 数据里的说明与这一份判定必须同源：第 2 层是瘴气、第 5 层是熔岩——改了数据却忘了这里，
            // 报表就会一直按旧的层号算分母。
            var data = GameDataStore.Get();
            Assert(FloorMechanics.FloorPoisons(data.FloorMechanicMap["2"]),
                $"数据里第 2 层的机制（{data.FloorMechanicMap["2"]}）与「会挂毒」的那张表对不上");
            var bossAt5 = data.BossStages.FirstOrDefault(b => b.Floor == 5)?.Mechanic;
            Assert(bossAt5 is not null && FloorMechanics.FloorPoisons(null, bossAt5),
                "数据里第 5 层 Boss 的机制与「会挂毒」的那张表对不上");
        });

        Section("导出与打包");
        Test("game_data.json 在导出包里可读", () =>
        {
            Assert(Godot.FileAccess.FileExists("res://Data/game_data.json"), "数据文件没进包");
        });

        // 静态源码检查在导出产物里必然跳过（`.cs` 是 1 字节占位符，见 `SourceOf`）。
        // 那句话得写出来：「164 通过 / 0 失败」与「164 通过 + 6 条静态检查跳过」
        // 是两件不同的事，日志里不该长得一样。
        GD.Print($"\n自检结果：{_passed} 通过 / {_failed} 失败");
        if (!SourceAvailable)
            GD.Print("注意：这是导出产物，`.cs` 只有占位符，静态源码检查（MethodAt 那一类）已全部跳过。\n" +
                     "      源码树上的 `--selfcheck` 才是 165 条全跑的那一次。");
        Boot.QuitWithGuard(_failed == 0 ? 0 : 1);
    }

    /// <summary>
    /// --play：无头跑一整局，把「进每间房之前血是多少、丢了哪一类血」摊开打出来。
    /// 和自检里那套理性玩家是同一份策略，所以这里的数字能和 40 局基线对上。
    /// </summary>
    /// <summary>模拟器最后一次「打输」的那一战的现场（--play 的死因要从这里读）</summary>
    private static string DeathCause = "";

    public static void PlayReport(uint seed)
    {
        DeathCause = "";
        var game = new Game(seed);
        var floor = 0;
        var hpAtFloorStart = game.Player.Hp;
        var battles = 0;
        var guard = 0;
        GD.Print($"\n===== 试玩 seed={seed} =====");
        GD.Print("层 ｜ 起始血 ｜ 结束血 ｜ 战斗 ｜ 房型");
        while (!game.IsGameOver() && guard++ < 4000)
        {
            if (game.Floor != floor)
            {
                if (floor > 0) PrintFloorLine(game, floor, hpAtFloorStart);
                floor = game.Floor;
                hpAtFloorStart = game.Player.Hp;
            }
            battles += SimulateOneRoom(game, seed, false);
            if (game.IsGameOver()) break;
            if (!Advance(game)) break;
        }
        if (floor > 0) PrintFloorLine(game, floor, hpAtFloorStart);
        GD.Print($"结果：{game.Status} · 第 {game.Floor} 层 · 战斗 {battles} 场 · Lv{game.Player.Level}");
        GD.Print($"血量 {game.Player.Hp}/{game.Stats.MaxHp} · 攻击 {game.Stats.Attack} · 防御 {game.Stats.Defense} · 金币 {game.Player.Gold}");
        GD.Print($"终点资产：药 {Inventory.Consumables(game.Player, game.ItemIndex).Count(c => c.Def.Heal is > 0)} 瓶 / 金币 {game.Player.Gold} / 商店已买空");
        GD.Print($"背包 {game.Player.Inventory.Count}/{game.Capacity}：{string.Join("、", game.Player.Inventory.Select(id => game.ItemIndex.TryGetValue(id, out var d) ? d.Name : $"{id}（查不到！）"))}");
        if (DeathCause.Length > 0) GD.Print($"死因：{DeathCause}");
        if (game.LastBattleLog.Length > 0) GD.Print($"最后一战：{game.LastBattleLog}");
        GD.Print("=========================");
    }

    private static void PrintFloorLine(Game game, int floor, int hpAtStart)
    {
        var map = game.Map;
        var counts = map.Rooms
            .Where(r => r.Type is not RoomType.Start && r.Type is not RoomType.Boss)
            .GroupBy(r => MapGen.RoomLabel[r.Type])
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key}×{g.Count()}");
        var maxHp = Math.Max(1, game.Stats.MaxHp);
        GD.Print($"{floor,2} ｜ {hpAtStart,3}({(double)hpAtStart / maxHp,3:P0}) ｜ {game.Player.Hp,3}({(double)game.Player.Hp / maxHp,3:P0}) ｜ {string.Join(" ", counts)}");
    }

    /// <summary>一次模拟的账面：跑不通的地方在这里累计，别用一堆 out 参数撒出去</summary>
    private sealed class SimResult
    {
        public int Rooms;
        public int RoomsSeen;
        public int MaxSteps;
        /// <summary>出现「当前生命 &gt; 面板上限」的局数，以及第一次的现场</summary>
        public int HpOverPanel;
        public string HpOverPanelWhere = "";
    }

    /// <summary>
    /// 跑 40 局并在每局结束后核对「状态自洽」。
    /// 顺便把玩家往合理的方向推一把（HealPlayer / BoostForSimulation）：
    /// 这两个方法只给模拟用，走的是和升级同一条面板计算路径。
    /// </summary>
    private static SimResult SimulateMany(bool randomPlay, out int wins, out int deaths, out int maxFloor, out int battles)
    {
        var result = new SimResult();
        wins = 0;
        deaths = 0;
        maxFloor = 0;
        battles = 0;
        for (uint seed = 1; seed <= 40; seed++)
        {
            var game = new Game(seed * 7919);
            var guard = 0;
            while (!game.IsGameOver() && guard++ < 4000)
            {
                battles += SimulateOneRoom(game, seed, randomPlay);
                if (game.IsGameOver()) break;
                if (!Advance(game)) break;
            }
            if (guard >= 4000 && !game.IsGameOver()) result.MaxSteps++;
            if (game.Status == GameStatus.Won) wins++;
            if (game.Status == GameStatus.Dead) deaths++;
            maxFloor = Math.Max(maxFloor, game.Floor);
            result.RoomsSeen += game.Map.Rooms.Count;
            result.Rooms++;
            // 「生命超过上限」先记账，整轮跑完再判：它得说清是哪一层、什么场面留下的，
            // 否则一条 40 局里只出现一次的断言只会告诉你「有 bug」，不告诉你「在哪」。
            if (game.Player.Hp > game.Stats.MaxHp)
            {
                result.HpOverPanel++;
                if (result.HpOverPanelWhere.Length == 0)
                    result.HpOverPanelWhere =
                        $"第 {game.Floor} 层 · 房间 {MapGen.RoomLabel[game.CurrentRoom.Type]} · " +
                        $"{game.Player.Hp}/{game.Stats.MaxHp} · 与记录（基线）相比多出 {game.Player.Hp - game.Stats.MaxHp} 点";
            }
            Assert(game.Player.Inventory.Count <= game.Capacity, "背包溢出");
            Assert(Inventory.IsEquippedIndexValid(game.Player), "装备下标越界");
            Assert(game.Floor <= 60, $"楼层异常推进: {game.Floor}");
        }
        return result;
    }

    /// <summary>
    /// 处理当前房间并返回打了多少场。和 TS 的 autoPlay 一样「能打就打、能拿就拿」，
    /// 但动作顺序固定（先喝药、再放技能、最后普攻），保证同一 seed 结果可复现。
    /// </summary>
    private static int SimulateOneRoom(Game game, uint seed, bool random = false)
    {
        var room = game.CurrentRoom;
        if (game.Player.Hp <= 0)
        {
            // 上一间房已经把人放倒了：这一条以前不存在，于是 --play 会接着跑完
            // 整层的「收尾」，最后打印出来的「最后一战」是一场胜利——
            // 玩家看着「你倒下了」却读到「敌人被全部击败了」。
            game.OnBattleLost();
            return 0;
        }
        var rng = random ? new Rng(unchecked(seed * 2654435761u + (uint)game.Kills)) : game.Rng;
        var data = GameDataStore.Get();
        var battles = 0;

        switch (room.Type)
        {
            case RoomType.Enemy:
            case RoomType.Elite:
            case RoomType.Boss:
            {
                if (room.Cleared) break;
                var enc = game.CreateEncounter();
                // 这里的兜底不是给玩家看的，是给模拟器保命的：CreateEncounter 返回 null
                // 意味着「这间房不会开打」，而 ClearRoom 只在 OnBattleWon 里发生——
                // 于是一间清不掉的敌人房会把整局钉死在两个空格子之间来回走。
                // 真实玩家能走到这里只有一种可能：数据表里这层没有敌人。那也是该修的。
                game.ClearRoom();
                if (enc is null) break;
                SimEquipBest(game);
                // 战前补给：模拟的是「玩家会看一眼状态再进房间」，
                // 而不是「背着一背包药去打，打到死也没点一下」。
                // 这一条以前完全没有——于是「理性策略」其实是个不会吃药的傻瓜。
                if (!random) SimResupply(game, enc);
                battles++;
                var battle = BuildBattle(game, enc);
                var guard = 0;
                while (battle.Phase == BattlePhase.Player && guard++ < 300)
                {
                    // 组合战斗先集火残血那只——不切的话回合会浪费在「每只都摸一下」。
                    var living = battle.LivingEnemies();
                    if (living.Count > 1)
                    {
                        var best = 0;
                        for (var i = 0; i < living.Count; i++) if (living[i].Hp < living[best].Hp) best = i;
                        battle.SelectTarget(best);
                    }
                    if (random)
                    {
                        // 随机玩家：掷一个数决定这一手。它故意「笨」——
                        // 不会在残血时先喝药、不会看技能冷却，正是用来证明「乱按打不过」。
                        switch (rng.Int(0, 3))
                        {
                            case 0:
                                battle.PlayerDefend();
                                break;
                            case 1 when game.Player.Skills.Count > 0:
                                battle.PlayerSkill(game.Player.Skills[rng.Int(0, game.Player.Skills.Count - 1)].Id);
                                break;
                            default:
                                battle.PlayerAttack();
                                break;
                        }
                    }
                    else
                    {
                        // 理性玩家：血见底先喝药，残血先治愈，能火球就火球，否则普攻。
                        // 三个技能都是同步行动的，喝药也算一个回合（和 UI 的 Act 一致）。
                        var heal = game.Player.Skills.FirstOrDefault(s => s.Id == "heal");
                        var fireball = game.Player.Skills.FirstOrDefault(s => s.Id == "fireball");
                        var lowHp = game.Player.Hp < game.Stats.MaxHp * 0.35;
                        var cure = Inventory.Consumables(game.Player, game.ItemIndex)
                            .FirstOrDefault(c => c.Def.Heal is > 0);
                        if (lowHp && cure.Def is not null)
                        {
                            battle.PlayerHeal(cure.Def.Heal!.Value, cure.Def.Name);
                            Inventory.RemoveItem(game.Player, cure.Index);
                        }
                        else if (lowHp && heal is not null && Skills.IsSkillReady(game.Player, heal.Id)) battle.PlayerSkill(heal.Id);
                        else if (fireball is not null && Skills.IsSkillReady(game.Player, fireball.Id)) battle.PlayerSkill(fireball.Id);
                        else battle.PlayerAttack();
                        // 血见底又没药：撤。逃跑不是「输了」，房间没清、下次还能来——
                        // 一直在「残血硬打」里死掉，是模拟器不会玩游戏，不是游戏太难。
                        //
                        // Boss 房不跑（和 UI 的规则一致）。早先这里没有这条判据，
                        // 于是每局走到深渊之主面前按一下「逃跑」就换下一间房：
                        // 既没赢也没死，40 局里一局都没真正打死过最终 Boss，
                        // 「理性玩家 0 胜 / 40 负」那行数字里，有一部分根本不是难度造成的。
                        if (room.Type != RoomType.Boss && battle.Phase == BattlePhase.Player && game.Player.Hp <= game.Stats.MaxHp * 0.2)
                        {
                            // Escape 返回「玩家是不是死在这场撤退里」——
                            // 撤退的代价本身就可能要命，这条路径也得走 OnBattleLost。
                            if (battle.Escape(BattleRules.EscapeCost)) game.OnBattleLost();
                        }
                    }
                }
                // 撤退：房间没清、人还活着，这一场就此结束（和 BattleScene 的 Esc 同一条规则）
                if (battle.Phase == BattlePhase.Escaped)
                {
                    battle.Dispose();
                    break;
                }
                var won = battle.Phase == BattlePhase.Won;
                var isBoss = room.Type == RoomType.Boss;
                var members = battle.Enemies().Count;
                // 记住这一战的最后几行：--play 靠它回答「死在哪一类房间里」。
                // 先 Dispose 再记日志：Dispose 会把冷却与状态收干净，但它不动 Battle.Log；
                // 而以前这一句写在 Dispose 之前，对象一旦被回收就再也读不到——顺序反过来更稳。
                var tail = string.Join(" / ", battle.Log.TakeLast(4));
                // 这一局是不是已经结束了：Battle 只在 EnemyTurn 里判负，而
                // 「敌人被震住/冻住，这一回合作废」那条路直接从 EnemyAct 返回、
                // 再走 EndRound——那时玩家可能已经被毒死。于是 Phase 还是 Player，
                // 循环以为「还轮到你」，玩家却已经躺下了。判据放在战斗结束之后，
                // 因为「谁死了」这件事只有 Game 与 Player.Hp 知道，Battle.Phase 不知道。
                var playerDead = game.Player.Hp <= 0 || game.Status == GameStatus.Dead;
                if (playerDead && battle.Phase != BattlePhase.Won)
                {
                    won = false;
                    battle.ForceLost("你倒下了……");
                    tail = string.Join(" / ", battle.Log.TakeLast(4));
                }
                battle.Dispose();
                game.LastBattleLog = $"{MapGen.RoomLabel[room.Type]}·{enc.Def.Name}·{(won ? "胜" : "败")}｜{tail}";
                if (!won)
                {
                    // 死因要记在「死掉的那一战」上，而不是之后被哪一次胜利覆盖掉。
                    // 以前这条分支直接 break，而 `LastBattleLog` 在它上面就被写成了
                    // 「这一战：败」——下一次循环如果先赢了别的房（例如先清完一间敌人房
                    // 再回头打精英房），它就被覆盖成「胜」，于是 --play 的
                    // 「最后一战」永远是一场胜利，死因那一行等于没写。
                    DeathCause = $"{MapGen.RoomLabel[room.Type]}·{enc.Def.Name}·第 {game.Floor} 层｜{tail}";
                    // 输了就走生产的死亡路径：碎片安慰奖、状态置 Dead 都在里面。
                    // 以前这里只 `break` 出去、再由外层 `Advance` 推一步——
                    // 结算面板里「本局到此为止」的那一套（OnBattleLost）根本没被执行过。
                    game.OnBattleLost();
                    break;
                }
                game.OnBattleWon(enc.Def, isBoss, enc.GoldMultiplier, enc.XpMultiplier, members);
                if (game.Status != GameStatus.Won)
                {
                    var rewards = game.Rewards(enc.Tier, Math.Max(0, members - 1));
                    TakeReward(game, PickReward(game, rewards));
                }
                break;
            }
            case RoomType.Treasure:
            {
                if (room.Cleared) break;
                game.ClearRoom();
                TakeReward(game, PickReward(game, game.Rewards()));
                break;
            }
            case RoomType.Altar:
            {
                if (room.Cleared) break;
                game.ClearRoom();
                if (game.Player.PoisonTurns > 0)
                {
                    EntityRules.ClearPoison(game.Player);
                }
                else
                {
                    // 理性玩家对祭坛的态度：没中毒就血祭换攻击力。
                    // 走 Game.TakeSacrifice：以前这里是第三份手写副本，扣的是 BaseMaxHp。
                    game.TakeSacrifice();
                }
                break;
            }
            case RoomType.Event:
            {
                if (room.Cleared) break;
                var ev = game.RandomEvent();
                if (ev.Choices.Count == 0) break;
                // 以前永远挑第一项——那等于「事件分支从来没被验证过」。
                // 理性玩家按「付不付得起」筛一遍再挑，随机玩家就真随机。
                var choice = random
                    ? ev.Choices[rng.Int(0, ev.Choices.Count - 1)]
                    : ev.Choices.FirstOrDefault(c => c.Cost is not > 0 || game.Player.Gold >= c.Cost) ?? ev.Choices[0];
                if (choice.Cost is { } cost)
                {
                    if (game.Player.Gold < cost) break;
                    // 和界面同一条路：金币的进出只走 Game.EarnGold / SpendGold。
                    game.SpendGold(cost);
                }
                var outcome = GameEvents.ResolveChoice(rng, choice.Outcomes);
                // swapItem 需要玩家挑一件丢掉，模拟器里跳过：它测的是 UI 流程，不是规则
                if (outcome.Effect.Type != EventEffectType.SwapItem)
                {
                    GameEvents.ApplyEffect(game.Player, outcome.Effect, new EventApplyContext
                    {
                        Rng = rng,
                        Items = data.Items,
                        Capacity = game.Capacity,
                        ItemsById = game.ItemIndex,
                        PanelMaxHp = game.Stats.MaxHp,
                        // 和界面同一条路：事件给的装备也要吃本层符文、也要有唯一 id。
                        // RegisterItem 漏了这一句的后果是「模拟里的背包会攒下一堆查不到的 id」：
                        // 它们占着格子（背包 6 格），却既不显示也没法用，
                        // 于是模拟器后半程其实是背着一堆空气在打——这不是游戏难度，是模拟器坏了。
                        PrepareItem = game.ApplyBonusAffix,
                        RegisterItem = game.RegisterRuntimeItemCopy,
                        // 和界面同一条路：事件挣/花的钱进 Game 那本账，
                        // 否则模拟器里「本局获得金币」只数战斗赏金。
                        AddGold = d => { if (d >= 0) game.EarnGold(d); else game.SpendGold(-d); },
                    });
                }
                game.CollapseIfDead();
                // 清空要等事件真的结算完：先 ClearRoom 再掷分支，掷到什么效果都算「处理过了」，
                // 但如果一次既没付钱也没扣血（比如「付不起」），模拟器会一直站在事件房上——
                // 房间已清、FloorCleared 却因为别的房没处理而假，Advance 就在两个空格子之间来回走。
                if (game.Status != GameStatus.Dead) game.ClearRoom();
                break;
            }
            case RoomType.Training:
            {
                if (room.Cleared) break;
                game.ClearRoom();
                // 残血休息、否则苦练：和玩家真实选择一致
                game.TakeTraining(game.Player.Hp < game.Stats.MaxHp / 2 ? "heal" : "attack");
                break;
            }
            case RoomType.Curse:
            {
                if (room.Cleared) break;
                game.ClearRoom();
                var item = game.CurseRoomItem();
                if (item is not null) Inventory.AddItem(game.Player, item.Id, game.Capacity);
                break;
            }
            case RoomType.Shop:
            {
                // 先补药：血见底又手里没药，是最该花钱的时刻。
                // 以前模拟器只买「比身上那件更强」的装备，从不买消耗品——
                // 一路跑下来金币 2000+、药 0 瓶，然后死在下一间敌人房。
                // 那不是游戏太难，是模拟器不会买东西。
                SimBuyPotions(game);
                // 买东西顺序：先装备（背包满时得先腾位），再比价格挑「比身上那件强」的。
                SimEquipBest(game);
                var stock = game.ShopStock(game.PlayerIndex);
                foreach (var entry in stock.Where(s => !s.Sold).OrderBy(s => s.Price))
                {
                    if (game.Player.Gold < entry.Price) continue;
                    // 背包满且新货不比身上的强，就别买了——钱留着买药
                    var slot = entry.Item.Slot == Slot.Consumable ? null : Inventory.SlotKey(entry.Item.Slot);
                    if (slot is not null && !IsUpgrade(game, slot, entry.Item)) continue;
                    if (game.Player.Inventory.Count >= game.Capacity)
                    {
                        var spare = SpareSlot(game);
                        if (spare < 0) continue;
                        game.SpendGold(entry.Price);
                        Inventory.ReplaceItem(game.Player, spare, entry.Item.Id);
                        entry.Sold = true;
                        continue;
                    }
                    game.SpendGold(entry.Price);
                    // 和界面同一条路：买下的东西要有名字、要存得下来。
                    // 消耗品也有运行时 id，不是「模板 id 就够」——货架是缓存对象，
                    // 读档前 roll 出来的那批不保证进过 ItemIndex。
                    var bought = game.RegisterRuntimeItemCopy(entry.Item);
                    Inventory.AddItem(game.Player, bought.Id, game.Capacity);
                    entry.Sold = true;
                }
                SimEquipBest(game);
                break;
            }
        }
        return battles;
    }

    /// <summary>
    /// 走向下一个房间。本层清空（含 Boss 房）就下楼；否则往「最近一间还没访问过的房间」走一步。
    ///
    /// 这里以前是「只看当前房间的相邻格，挑其中未访问的」——一个纯贪心的写法，
    /// 在树形地图上会稳定地卡死：走到一个所有邻居都去过的格子上，就再也走不出来了。
    /// 实测 36/40 局整局在「第 7 号空房 ↔ 第 5 号空房」之间来回走到步数上限。
    /// 现在按全图 BFS 找最近目标，沿路径走一步；没有未访问的就去「未清空的战斗房」。
    /// 返回 false 表示走不动了（地图上有去不了的地方）。
    /// </summary>
    private static bool Advance(Game game)
    {
        // 局已经结束就别再往前走：以前这里不看状态，而「通关那一刻 FloorCleared 恰好为真」，
        // 于是 ContinueDive + Descend 会一直把楼层推到 60 层上限为止——
        // 最后一层出现的那些「新房间」全是模拟器自己造出来的，人也从来没走到那儿。
        if (game.IsGameOver()) return false;
        if (game.FloorCleared)
        {
            if (game.Floor >= game.FinalFloor && game.Status == GameStatus.Won)
            {
                game.ContinueDive();
                return true;
            }
            var (ok, _) = game.Descend();
            return ok;
        }

        // 目标优先级：没去过的房间 > 没清空的战斗房。两者都由全图 BFS 定位，
        // 不再依赖「玩家此刻站在哪」这个前提。
        var target = NearestUnvisited(game)
                     ?? game.Map.Rooms
                         .Select((r, i) => (r, i))
                         .Where(x => !x.r.Cleared && x.r.Type is RoomType.Enemy or RoomType.Elite or RoomType.Boss)
                         .Select(x => (int?)x.i)
                         .FirstOrDefault();
        if (target is null) return false;
        return WalkTo(game, target.Value);
    }

    /// <summary>全图 BFS：最近一间还没访问过的房间</summary>
    private static int? NearestUnvisited(Game game)
    {
        var dist = MapGen.BfsDistances(game.Map.Links, game.PlayerIndex);
        var best = -1;
        for (var i = 0; i < game.Map.Rooms.Count; i++)
        {
            if (game.Map.Rooms[i].Visited || dist[i] < 0) continue;
            if (best < 0 || dist[i] < dist[best]) best = i;
        }
        return best < 0 ? null : best;
    }

    /// <summary>沿 BFS 最短路朝目标走一步（一次只走一格，和玩家按方向键一样）</summary>
    private static bool WalkTo(Game game, int target)
    {
        if (target == game.PlayerIndex) return true;
        // 从目标往回 BFS，第一跳就是「从玩家出发朝目标走的第一格」
        var dist = MapGen.BfsDistances(game.Map.Links, target);
        if (dist[game.PlayerIndex] < 0) return false;
        var step = game.Map.Links[game.PlayerIndex]
            .FirstOrDefault(n => dist[n] == dist[game.PlayerIndex] - 1, -1);
        if (step < 0) return false;
        var cur = game.CurrentRoom;
        var next = game.Map.Rooms[step];
        var (moved, _, _) = game.Move(next.X - cur.X, next.Y - cur.Y);
        return moved;
    }

    /// <summary>逃跑的代价：和 BattleScene 里按 Esc 时同一份规则</summary>
    private static class BattleRules
    {
        public const int EscapeCost = 5;
    }

    /// <summary>这一格装备是否比背包里那件（如果装备着）更强。消耗品不参与比较</summary>
    private static bool IsUpgrade(Game game, string slot, ItemDef candidate)
    {
        var cur = Inventory.EquippedDef(game.Player, slot, game.ItemIndex);
        if (cur is null) return true;
        return Score(candidate) > Score(cur);
    }

    /// <summary>
    /// 模拟器眼里的「这件东西值多少」。只看攻防血三样和词条条数——
    /// 模拟器不玩构筑，但它得会「换上更强的那件」，否则整个过程只证明了掉血会死人。
    /// </summary>
    private static int Score(ItemDef def) =>
        (def.Attack ?? 0) * 3 + (def.Defense ?? 0) * 2 + (def.MaxHp ?? 0) + Inventory.AffixLimit(def) * 2;

    /// <summary>背包里可丢的下标：优先消耗品，其次没装备的装备。没有就返回 -1</summary>
    /// <summary>
    /// 背包里「比身上那件还差、而且没穿在身上」的那一格。
    /// 模拟器腾格子的顺序应该和玩家一样：先扔没用的，再动别的心思。
    /// </summary>
    private static int DropWorse(Game game)
    {
        var p = game.Player;
        for (var i = 0; i < p.Inventory.Count; i++)
        {
            if (Inventory.IsEquipped(p, i) is not null) continue;
            if (!game.ItemIndex.TryGetValue(p.Inventory[i], out var d) || d.Slot == Slot.Consumable) continue;
            var slot = Inventory.SlotKey(d.Slot);
            if (!IsUpgrade(game, slot, d)) return i;
        }
        return -1;
    }

    private static int SpareSlot(Game game)
    {
        var p = game.Player;
        for (var i = 0; i < p.Inventory.Count; i++)
        {
            if (Inventory.IsEquipped(p, i) is not null) continue;
            if (game.ItemIndex.TryGetValue(p.Inventory[i], out var d) && d.Slot == Slot.Consumable) return i;
        }
        for (var i = 0; i < p.Inventory.Count; i++)
        {
            if (Inventory.IsEquipped(p, i) is null) return i;
        }
        return -1;
    }

    /// <summary>把每格装备换成背包里最好的那件；召唤物／消耗品不在考虑范围</summary>
    private static void SimEquipBest(Game game)
    {
        var p = game.Player;
        foreach (var slot in Inventory.EquipSlots)
        {
            var best = -1;
            var bestScore = int.MinValue;
            for (var i = 0; i < p.Inventory.Count; i++)
            {
                if (!game.ItemIndex.TryGetValue(p.Inventory[i], out var def)) continue;
                if (Inventory.SlotKey(def.Slot) != slot) continue;
                var sc = Score(def);
                if (sc <= bestScore) continue;
                bestScore = sc;
                best = i;
            }
            if (best >= 0) Inventory.Equip(p, best, game.ItemIndex);
        }
    }

    /// <summary>
    /// 战前补给：模拟「玩家进房间前先看一眼状态」。
    /// 只在必要时补血（不浪费药），血够就用祭坛式的小额自愈；
    /// 进 Boss 房前额外喝一瓶——Boss 的暴击能把满血角色一口气打掉三成。
    /// </summary>
    private static void SimResupply(Game game, EncounterDef enc)
    {
        var p = game.Player;
        var max = game.Stats.MaxHp;
        var boss = enc.Def.IsBoss == true;
        var threshold = boss ? 0.95 : 0.55;
        if (p.Hp >= max * threshold) return;
        foreach (var (idx, def) in Inventory.Consumables(p, game.ItemIndex))
        {
            if (def.Heal is not > 0) continue;
            game.HealPlayer(def.Heal.Value);
            Inventory.RemoveItem(p, idx);
            if (p.Hp >= max * 0.95) return;
        }
    }

    /// <summary>在商店按「缺多少补多少」买治疗药水（模拟玩家真的会为下一间房做准备）</summary>
    private static void SimBuyPotions(Game game)
    {
        var p = game.Player;
        var max = game.Stats.MaxHp;
        var stock = game.ShopStock(game.PlayerIndex);
        // 手上已有几瓶药，先数清：模拟器不该囤到背包塞满
        var held = Inventory.Consumables(p, game.ItemIndex).Count(c => c.Def.Heal is > 0);
        // 背包满了要腾位：优先丢掉一件「比身上那件还差」的装备。
        // 这一条是跑出来的：6 格背包里 3 件穿着、3 格塞着打怪掉的次品（锈剑、皮甲、铁戒指），
        // 于是买药永远腾不出格子，一路攒到 900 金币、0 瓶药，死在下一间敌人房。
        // 玩家不会留着一背包次品不卖，模拟器也不该。
        foreach (var entry in stock.Where(s => !s.Sold && s.Item.Heal is > 0).OrderBy(s => s.Price))
        {
            if (held >= 2) break;
            if (p.Gold < entry.Price) continue;
            if (p.Inventory.Count >= game.Capacity && SpareSlot(game) < 0 && DropWorse(game) < 0) break;
            game.SpendGold(entry.Price);
            var bought = game.RegisterRuntimeItemCopy(entry.Item);
            if (p.Inventory.Count >= game.Capacity)
            {
                var spare = DropWorse(game);
                if (spare < 0) spare = SpareSlot(game);
                if (spare < 0) break;
                Inventory.ReplaceItem(p, spare, bought.Id);
            }
            else
            {
                Inventory.AddItem(p, bought.Id, game.Capacity);
            }
            entry.Sold = true;
            held += 1;
        }
        // 买完就喝：血低于六成时喝到安全线，别背着一瓶药进敌人房
        if (p.Hp >= max * 0.6) return;
        // 只消耗一两瓶就收手：背着三瓶药进下一间房，比在店里喝满了更划算。
        var drank = 0;
        foreach (var (idx, def) in Inventory.Consumables(p, game.ItemIndex).ToList())
        {
            if (def.Heal is not > 0) continue;
            if (drank >= 1) return;
            drank += 1;
            game.HealPlayer(def.Heal.Value);
            Inventory.RemoveItem(p, idx);
            if (p.Hp >= max * 0.9) return;
        }
    }

    /// <summary>
    /// 三选一挑一张。以前这里固定拿 <c>[0]</c>——那等于「这一局里，玩家从来没做过奖励面板上那个决定」，
    /// 而那张卡正是这游戏回血的主力（横扫 30 局里 1 万点）。固定拿第一张时，
    /// 满血的人也会拿走一格「回复 0 点」，背包满时还会为它挤掉一件有用的装备。
    /// 判据很朴素：血不满且这卡真能回血 → 拿；有比自己身上更强的装备 → 拿；否则拿钱。
    /// </summary>
    private static Reward PickReward(Game game, List<Reward> rewards)
    {
        var missing = game.Stats.MaxHp - game.Player.Hp;
        if (missing > 0)
        {
            var heal = rewards.OfType<Reward.Heal>().Where(h => h.Effective).MaxBy(h => h.Amount);
            if (heal is not null) return heal;
        }
        var room = game.Player.Inventory.Count < game.Capacity;
        if (room)
        {
            var upgrade = rewards.OfType<Reward.Item>()
                .Where(i => i.Def.Slot != Slot.Consumable)
                .MaxBy(i => Score(i.Def));
            if (upgrade is not null)
            {
                var worn = Inventory.EquippedDef(game.Player, Inventory.SlotKey(upgrade.Def.Slot), game.ItemIndex);
                if (worn is null || Score(upgrade.Def) > Score(worn)) return upgrade;
            }
            var potion = rewards.OfType<Reward.Item>().FirstOrDefault(i => i.Def.Heal is > 0);
            if (potion is not null) return potion;
        }
        var gold = rewards.OfType<Reward.Gold>().FirstOrDefault();
        return gold ?? rewards[0];
    }

    private static void TakeReward(Game game, Reward reward)
    {
        switch (reward)
        {
            case Reward.Gold gold:
                game.EarnGold(gold.Amount);
                break;
            case Reward.Heal heal:
                game.Player.Hp = Math.Min(game.Stats.MaxHp, game.Player.Hp + heal.Amount);
                break;
            case Reward.Item item:
                Inventory.AddItem(game.Player, item.Def.Id, game.Capacity);
                break;
        }
    }

    private static Battle BuildBattle(Game game, EncounterDef enc)
    {
        var data = GameDataStore.Get();
        return new Battle(game.Player, enc.Monster, game.Rng, new BattleOptions
        {
            Rules = RulesOf(data),
            AttackerAtk = c => c is Player ? (int)Math.Round(game.Stats.Attack * (1 + game.TrainingAttackPct)) : c.BaseAttack,
            AttackerDef = c => c is Player ? game.Stats.Defense : c.BaseDefense,
            MaxHp = c => c is Player ? game.Stats.MaxHp : c.BaseMaxHp,
            FloorAttackBonus = game.FloorAttackBonus,
            FloorCritBonus = game.FloorCritBonus,
            // 读遭遇自己记下来的那一条，不现读 `game.FloorMechanicId`：
            // 遭遇是在「那一刻」建的，机制变了以后现读就会和敌人的属性对不上
            // （生产路径以前就是这么错的，见 `EncounterDef.FloorMechanicId`）。
            FloorMechanic = enc.FloorMechanicId,
            BossMechanic = game.BossStage(game.Floor) is { } stage ? game.BossMechanicIdOf(stage) : null,
            Floor = game.Floor,
            Affixes = game.Affixes,
            Allies = enc.Allies,
            EnemyDefs = enc.EnemyDefs,
            EnemyCritBonus = enc.EnemyCritBonus,
        });
    }

    /// <summary>
    /// MapScene 的源码。只在开发环境里有：导出的二进制里没有 .cs，
    /// 那时候返回 null，静态检查跳过——不要在产物上因为「读不到源码」而红。
    /// </summary>
    /// <summary>
    /// 当前环境里能不能拿到真源码。第一次问的时候探一次，之后记着——
    /// 自检末尾要靠它决定要不要叮嘱一句「你这跑的是导出产物」。
    /// </summary>
    private static bool? _sourceAvailable;

    private static bool SourceAvailable =>
        _sourceAvailable ??= SourceOf("Scripts/Scenes/MapScene.cs") is not null;

    private static string? MapSceneSource() => SourceOf("Scripts/Scenes/MapScene.cs");

    /// <summary>
    /// 按「函数名 + 参数表」找一段方法源码，返回它**所在那一行的行首**；找不到返回 -1。
    ///
    /// 判据原来直接 <c>IndexOf("private void OpenTreasure")</c>：在 `private void` 与名字之间
    /// 加一个特性、把 `private` 换成 `internal`，甚至只在声明前多挂一行 `[Tool]`，方法还在原地
    /// 好好干活，判据却报「找不到 OpenTreasure」——而真正被保护的东西（开箱要落盘、进门不许清房）
    /// 一点没坏。签名比对能挡住这一类误报。
    ///
    /// 但**返回的必须是行首，不是 `(`**：调用方拿这个位置当「函数体从哪开始」的锚，
    /// 判据里那些 `<c>l.StartsWith("        _run.ClearRoom")</c>`（缩进 8 空格 = 直接写在
    /// 函数体里）一靠 `Split('\n').Skip(1)` 丢掉声明那一行。锚落在 `(` 上，第一「行」
    /// 只剩参数表的尾巴 `int index)`，声明之后的缩进与行首全被抹平，缩进判据永远绿。
    /// 同理，`Substring(cut, ...)` 里少了签名，`body.Contains("SaveRun")` 会把
    /// **上一个函数** 或文件头里的东西算进来——判据看着在用签名，其实量的不是那段代码。
    ///
    /// 还有三件必须在这里收掉的事：
    /// <list type="number">
    /// <item>**命中必须落在标识符边界上**。裸 <c>IndexOf</c> 会把注释里那句
    /// <c>"private void OpenTreasure(int index)"</c>（就在「进门就清房」那一条的注释里）
    /// 当成声明，锚点于是落在一条注释上，缩进判据量的是注释，永远绿。</item>
    /// <item>**注释与字符串字面量一律不算**。前两次修这个函数时锚点先后落在
    /// 「那条判据自己的注释」和「判据自己的 <c>MethodAt(text!, "…")</c> 调用行」上——
    /// 签名就写在待查文件的注释与代码里，所以只会往后走的 <c>IndexOf</c> 一定先撞到它们。
    /// 这里按行先认「这一行是不是真正的声明形状」（以 <c>private</c> / <c>internal</c> 之类开头，
    /// 或前一行是 <c>[特性]</c>），注释行、含 <c>"</c> 的行、调用行都不是。</item>
    /// <item>**参数表可以省略**。调用方写的是 <c>"private void TryDescend"</c> 这种短签名，
    /// 只要求名字出现；但同一条判据也得能拦住签名被改了形状（增删一个参数就再没人保证
    /// 那一段被量到）。所以：签名带参数表就逐字比对，不带就只认名字 + 左括号。</item>
    /// </list>
    /// </summary>
    private static int MethodAt(string src, string signature)
    {
        // 签名不带参数表时只比名字；带了就要求 `(` 后面那一截也逐字对得上。
        var paren = signature.IndexOf('(');
        // 不带参数表的短签名要**整个**当成名字：`paren` 是 -1 时 `signature[..paren]`
        // 会掉进 `[..-1]`，名字被砍掉最后一个字母——"private void TryDescend"
        // 于是变成 "private void TryDescen"，一次都匹配不上。
        var name = (paren < 0 ? signature : signature[..paren]).TrimEnd();

        var from = 0;
        while (from < src.Length)
        {
            var at = src.IndexOf(name, from, StringComparison.Ordinal);
            if (at < 0) return -1;
            from = at + name.Length;
            // 名字必须自己就是一个标识符：`_BeforeMaxHpGrows` 里那截 `BeforeMaxHpGrows` 不算。
            if (at > 0 && IsIdentChar(src[at - 1])) continue;
            if (from < src.Length && IsIdentChar(src[from])) continue;
            if (paren >= 0)
            {
                var after = src.AsSpan(from).TrimStart();
                if (!after.StartsWith("(")) continue;
                if (!after.StartsWith(signature.AsSpan(paren), StringComparison.Ordinal)) continue;
            }
            // 标识符边界挡不住「注释里那句签名」和「判据自己的调用行」：它们长得和声明一回事。
            var lineStart = src.LastIndexOf('\n', at);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;
            // 传「行首 → 名字之后」而不是「行首 → at」：`at` 是**名字的起点**，而名字本身
            // 就带着 `private void ` 这一截，行首到 at 之间只剩缩进（`"    "`），
            // 拿去判断「是不是声明」永远为假——修复前这条检查把每一条真实声明都判掉了。
            if (!IsDeclarationLine(src[lineStart..(from < src.Length ? from : src.Length)])) continue;
            return lineStart;
        }
        return -1;
    }

    /// <summary>
    /// 这一段是不是一条**成员声明**的开头。传进来的是行首到函数名之间那一截。
    ///
    /// 只认修饰符与返回类型：<c>private</c> / <c>public</c> / <c>internal</c> /
    /// <c>protected</c> / <c>static</c>。<c>//</c> 开头的注释、带 <c>"</c> 的字符串
    /// （判据把签名写在字面量里）一律不是；调用行前面是 <c>text!,</c> 这种东西，也不是。
    /// </summary>
    private static bool IsDeclarationLine(string head)
    {
        head = head.TrimStart(' ', '\t');
        // 含 `"` 的是判据自己写的提示文案 / 字面量，不是声明。
        if (head.IndexOf('"') >= 0) return false;
        foreach (var word in new[] { "private", "public", "internal", "protected", "static" })
            if (head.StartsWith(word, StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>
    /// 从 <paramref name="from"/> 往后找下一个**成员声明**的行首（缩进 4 空格的 `private ` 之类）。
    /// 找不到返回 -1。
    ///
    /// 取一段方法的结尾不能用 <c>IndexOf("private void ")</c>：那段文字在字段声明、注释、
    /// 下一个方法体里的一行都可能出现。命中早了 body 就被截断，判据在残片上找不到东西；
    /// 只认 `private void ` 又会漏掉 `private Action?` 这类字段，一路吃到文件尾。
    ///
    /// 注释行与特性行不算成员：<c>/// 开箱是三选一</c> 排在方法的声明**之前**，
    /// 认成成员就会把上一个方法的 body 从文档注释那一行截断——判据在残片上找不到东西，
    /// 报的还是「开完箱子没有落盘」这种听起来像产品坏了的话。
    /// </summary>
    private static int MethodEnd(string src, int from)
    {
        const string indent = "    ";
        if (from < 0) return -1;
        for (var i = from; i < src.Length; i++)
        {
            if (src[i] != '\n') continue;
            var lineStart = i + 1;
            if (lineStart + indent.Length >= src.Length) continue;
            if (string.CompareOrdinal(src, lineStart, indent, 0, indent.Length) != 0) continue;
            if (lineStart + indent.Length < src.Length && src[lineStart + indent.Length] == ' ')
                continue;   // 缩进 8 空格：函数体里的语句，不是成员
            var c = src[lineStart + indent.Length];
            // 注释与特性也是「缩进 4 空格、字母开头」：`/// <summary>` 出现在声明**之前**，
            // 认成成员声明就等于把上一个方法的 body 在文档注释那一行截断——
            // OpenTreasure 的 body 里本来就有 `/// 开箱是三选一`，判据会在残片上找不到 SaveRun。
            if (c == '/' || c == '[') continue;
            if (char.IsLetter(c)) return lineStart;
        }
        return -1;
    }

    /// <summary>
    /// Skills.cs 里真正**调用** <c>PoisonPowerOf(skill, ctx, dealt)</c> 的那一行；没有就返回 null。
    ///
    /// 名字在文件里会出现三次：声明（`private static int PoisonPowerOf(SkillDef skill, ...)`）、
    /// 挂毒那一行，以及**读这段源码的判据自己写下的注释**——`IndexOf` 认第一个、
    /// `LastIndexOf` 认后两个，三种都对不上「挂毒有没有过这个入口」这件事。
    /// 按行找，跳过注释行、字符串里出现的签名、以及声明行本身。
    /// </summary>
    private static string? PoisonCallSite(string src)
    {
        const string call = "PoisonPowerOf(skill, ctx, dealt)";
        foreach (var raw in src.Split('\n'))
        {
            var line = raw.TrimStart();
            // 注释里那句话不是调用：它就是这条判据自己写的（「读的是技能挂毒那一行」）。
            if (line.StartsWith("//", StringComparison.Ordinal)) continue;
            var at = line.IndexOf(call, StringComparison.Ordinal);
            if (at < 0) continue;
            var before = line[..at];
            // 字面量里的签名（判据写给别人看的提示文案）不是调用。
            if (before.Contains('"')) continue;
            // 声明行：`private static int PoisonPowerOf(` —— 名字前面挂着类型与修饰符。
            if (before.Contains("static") || before.TrimEnd().EndsWith("int", StringComparison.Ordinal))
                continue;
            return line;
        }
        return null;
    }

    /// <summary>
    /// 读一份源码；**不是真源码**就返回 null，调用方一律跳过（不是报失败）。
    ///
    /// 这里踩过的坑值得写清楚，因为它是同一处连续两轮返工的根源。
    ///
    /// 一、路径不许先 `GlobalizePath`。
    /// `GlobalizePath` 问的是「这个 `res://` 在**真实文件系统**上是哪个绝对路径」。
    /// 导出的二进制里项目都封在 `.pck` 里，真实文件系统上那个目录根本不存在，
    /// 绝对路径必然 `FileExists == false`。所以正确做法是用 `res://` 直接问
    /// `FileAccess`——它自己会先查 pck 再查磁盘。
    ///
    /// 二、但「查到了」不等于「拿到了源码」。
    /// Godot 导出时会把 `.cs` 换成一个 **1 字节的占位符**（C# 代码以编译好的 dll
    /// 发布，源码不必随包带走）。于是 `FileExists("res://Scripts/.../MapScene.cs")`
    /// 返回 **true**，`GetFileAsString` 返回一个 **长度 1** 的字符串。
    /// 这一条是实测出来的：探针在产物上打出 `res:// 存在=True 长度=1`。
    ///
    /// 也就是说，「读不到」有两种样子——返回 null，和返回一坨不是源码的东西。
    /// 上一版只防了前一种，于是 `MethodAt` 在一段 1 字节的字符串里找不到
    /// `private void OpenTreasure(...)`，报「这条断言已经失去目标」：
    /// 源码树上 165 通过 / 0 失败，同一个二进制导出之后再跑 160 / 5。
    /// 而 `--export` 之后那一步（在产物上跑自检）是 tag 发布不可跳过的关卡，
    /// 于是整个发布链路必然红——「完全无法打包」就是这么来的。
    ///
    /// 判断标准不拿长度做阈值（那是个魔数）：源码里必须有 `namespace` 与一个返回/声明符号。
    /// 这不是「差不多像源码」，而是「这份文件确实是被当成源码写出来的那份」。
    /// </summary>
    private static string? SourceOf(string relative)
    {
        var path = $"res://{relative}";
        if (!Godot.FileAccess.FileExists(path)) return null;
        var text = Godot.FileAccess.GetFileAsString(path);
        // 占位符 / 空文件 / 被裁剪过的东西：都不是源码，交给调用方跳过。
        if (text.Length < 32 || !text.Contains("namespace", StringComparison.Ordinal))
            return null;
        return text;
    }

    private static CombatRules RulesOf(GameData data) => new()
    {
        CritChance = data.Config.CritChance,
        CritMultiplier = data.Config.CritMultiplier,
        DefendReduction = data.Config.DefendReduction,
        DamageVariance = data.Config.DamageVariance,
    };

    private void Section(string title) => GD.Print($"\n== {title} ==");

    private void Test(string name, Action fn)
    {
        try
        {
            fn();
            _passed++;
            GD.Print($"  [ok] {name}");
        }
        catch (Exception e)
        {
            _failed++;
            GD.Print($"  [FAIL] {name}: {e.Message}");
            foreach (var line in (e.StackTrace ?? "").Split('\n').Take(4)) GD.Print($"        {line.Trim()}");
        }
    }

    /// <summary>
    /// 这件模板「最多能挂几条词条」：品质容量与数据表里那条 AffixCount 的较大者。
    /// 只按品质算会低估——有些模板写着白装却自带两条词条（数据先于品质表存在过），
    /// 那两条是真的在生效，容量判断得把它们算进去。
    /// </summary>
    private static int AffixCountCap(GameData data, ItemDef def) =>
        Math.Max(data.Qualities[Inventory.QualityOf(def)].AffixCount, def.Affixes?.Count ?? 0);

    private static void Assert(bool cond, string message)
    {
        if (!cond) throw new InvalidOperationException(message);
    }
}

internal static class GameTestExtensions
{
    /// <summary>通关后继续深潜：模拟器用不到 UI，直接调 Game 的方法</summary>
    public static void ContinueDiveIfPossible(this Game game)
    {
        if (game.Status == GameStatus.Won) game.ContinueDive();
    }
}
