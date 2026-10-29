// 真实游玩探针：真的按玩家会按的顺序把一局玩下去，然后在每一段操作之后核对
// 「我说我拿到了，我是不是真的拿到了」。它不进断言，是给开发者看的现场。
//
// 与 --play 的分工：--play 报的是「血在哪丢的」，这一份报的是「手里有什么、能不能用」。
namespace AbyssEcho.Dev;

using Godot;
using AbyssEcho.Core;
using AbyssEcho.Data;
using AbyssEcho.Systems;

public static class PlayProbe
{
    /// <summary>
    /// 战术道具的消融开关：`all` / `nobomb` / `nocleanse`（由 `--items=` 传进来）。
    /// 关掉哪一件，横扫里就量出哪一件的价钱——「炸弹值不值 14 金币」这种东西，
    /// 读代码读不出来，只有同一批种子跑两遍、只差这一件才有答案。
    /// </summary>
    public static string ItemMode { get; set; } = "all";

    /// <summary>`--probe-dump`：把每一场战斗的完整日志打出来。死因表对不上账时用它看现场。</summary>
    public static bool DumpBattles { get; set; }

    /// <summary>
    /// `--optional=off`：把「自己去找的可选仗」整档关掉，只打挡在去 Boss 路上的那几场。
    ///
    /// 为什么要一个开关而不是直接改策略：「这一档该不该开」是一个能不能量出来的问题——
    /// 同一批种子跑两遍、只差这一档，通关率的差就是这一档的价钱。
    /// 默认 on（真人不会走到死胡同尽头只为看一眼），关掉它只用来做对照。
    /// </summary>
    public static bool FightOptional { get; set; } = true;

    /// <summary>
    /// `--mistake=N`：每一步有 N% 的概率「算错一次」，也就是这一回合本该扔炸弹 / 该吃草 /
    /// 该防御的那一手，被判成了「不必」。
    ///
    /// 为什么要一个会犯错的探针：「这件保险值不值 8 金币」是一个**保费**问题，
    /// 而一个每一步都算对的玩家永远不会出险——所以在它眼里，保险永远不值钱。
    /// </summary>
    public static int MistakePct { get; set; }

    /// <summary>`--probe-flee-dump`：把每一场战斗「为什么没撤」的输入打出来（诊断用）</summary>
    public static bool FleeDump { get; set; }

    /// <summary>`--mistake-kind=light|bad|ugly`：错成哪一种。</summary>
    public static string MistakeKind { get; set; } = "light";

    /// <summary>1=light · 2=bad · 3=ugly</summary>
    private static int Mistake => MistakeKind switch { "bad" => 2, "ugly" => 3, _ => 1 };

    /// <summary>这一次判据掷出了「算错」没有。同一个随机源走 <see cref="MistakeRng"/>，同 seed 可复现。</summary>
    private static bool Slips() => MistakePct > 0 && MistakeRng.Next(100) < MistakePct;

    /// <summary>只给「算错」用的随机源：不和战斗的 Rng 共用，否则打开误判会改掉整局的走向。</summary>
    private static readonly Random MistakeRng = new(20261028);

    public static int Run(uint seed)
    {
        GameDataStore.Get();
        var tally = new Tally { MaxHp = new Game(seed).Stats.MaxHp };
        var code = RunOne(seed, tally);
        tally.Report(seed);
        return code;
    }

    /// <summary>
    /// 横扫：同一套「按玩家顺序真玩」的走法跑 N 局，只报汇总。
    /// 它的意义是把单局的现场变成一条能对照的结论——「26/30 局死在 5~9 层」
    /// 这种话只有跑到足够多局才敢说。单局探针的那 20 项核对在每一局里照跑，
    /// 有一项不成立就把该局记进「坏局」。
    /// </summary>
    public static int Sweep(int runs)
    {
        GameDataStore.Get();
        var all = new Tally { MaxHp = 0 };
        var bad = 0;
        var floors = new int[20];
        var deathRooms = new Dictionary<RoomType, int>();
        for (var i = 1; i <= runs; i++)
        {
            var seed = (uint)i;
            var t = new Tally { MaxHp = new Game(seed).Stats.MaxHp };
            // 单局探针的逐条打印在横扫里太吵，先静音再放开汇总
            var code = RunOne(seed, t, quiet: true);
            if (code != 0) bad++;
            all.ShopVisits += t.ShopVisits;
            all.ShopsDoorWithoutPotion += t.ShopsDoorWithoutPotion;
            all.ShelfWithAntidote += t.ShelfWithAntidote;
            all.SupportShelfBlocked += t.SupportShelfBlocked;
            all.AntidoteOnfullBag += t.AntidoteOnfullBag;
            all.PotionBought += t.PotionBought;
            all.HealUsed += t.HealUsed;
            all.HealFromReward += t.HealFromReward;
            all.HealFromMap += t.HealFromMap;
            all.BlockedByFull += t.BlockedByFull;
            all.CouldNotAfford += t.CouldNotAfford;
            all.ShopRooms += t.ShopRooms;
            all.ShopLeftoverGold += t.ShopLeftoverGold;
            all.ShopLeftoverGoldWorst = Math.Max(all.ShopLeftoverGoldWorst, t.ShopLeftoverGoldWorst);
            all.ShopSurplusRatioWorst = Math.Max(all.ShopSurplusRatioWorst, t.ShopSurplusRatioWorst);
            all.BossPathBattlesTaken += t.BossPathBattlesTaken;
            all.OptionalBattlesTaken += t.OptionalBattlesTaken;
            all.SkippedOptionalBattles += t.SkippedOptionalBattles;
            all.FledBattles += t.FledBattles;
            all.ShardsEarned += t.ShardsEarned;
            all.FledWhileLowHp += t.FledWhileLowHp;
            all.OptionalSeen += t.OptionalSeen;
            all.FightOptionalSeen += t.FightOptionalSeen;
            all.OptionalRoomsSeen += t.OptionalRoomsSeen;
            all.BattleRoomsSeen += t.BattleRoomsSeen;
            all.FloorMaps += t.FloorMaps;
            all.FloorsWon += t.FloorsWon;
            all.FloorsWithOptionalBattle += t.FloorsWithOptionalBattle;
            all.FloorsWithOptionalSkipped += t.FloorsWithOptionalSkipped;
            all.FloorsOnlyForcedBattleLeft += t.FloorsOnlyForcedBattleLeft;
            all.FloorsBacktracked = Math.Max(all.FloorsBacktracked, t.FloorsBacktracked);
            all.ForcedBattlesAtDescend += t.ForcedBattlesAtDescend;
            all.FloorsEntered += t.FloorsEntered;
            all.FloorSteps += t.FloorSteps;
            foreach (var (k, v) in t.DeathsBy) all.DeathsBy[k] = (all.DeathsBy.TryGetValue(k, out var had2) ? had2 : 0) + v;
            foreach (var (k, v) in t.DeathsByRoom) deathRooms[k] = (deathRooms.TryGetValue(k, out var had3) ? had3 : 0) + v;
            all.DeathFloors.AddRange(t.DeathFloors);
            all.DeathCausesSeen += t.DeathCausesSeen;
            all.PurseProbe.AddRange(t.PurseProbe);
            foreach (var (k, v) in t.GoldByFloor) all.GoldByFloor[k] = v;
            all.MaxHpSeen = Math.Max(all.MaxHpSeen, t.MaxHp);
            all.OfferingSteps += t.OfferingSteps;
            all.ItemsSold += t.ItemsSold;
            all.GoldFromSales += t.GoldFromSales;
            all.ItemDamageUsed += t.ItemDamageUsed;
            all.CleanseUsed += t.CleanseUsed;
            all.CleanseBought += t.CleanseBought;
            all.BombBought += t.BombBought;
            all.PoisonTurnsWithAntidote += t.PoisonTurnsWithAntidote;
            all.CleanseWorthAndCarried += t.CleanseWorthAndCarried;
            all.CleanseWorthButLethal += t.CleanseWorthButLethal;
            all.SlippedNotTaken += t.SlippedNotTaken;
            all.SupportCarriedWorst = Math.Max(all.SupportCarriedWorst, t.SupportCarriedWorst);
            all.SupportBought += t.SupportBought;
            all.PoisonTurnsSeen += t.PoisonTurnsSeen;
            all.PoisonMateSeen += t.PoisonMateSeen;
            all.PoisonHeavySeen += t.PoisonHeavySeen;
            all.PoisonInDangerSamples += t.PoisonInDangerSamples;
            all.TurnInDangerSamples += t.TurnInDangerSamples;
            foreach (var (k, v) in t.PoisonTurnsByFloor)
                all.PoisonTurnsByFloor[k] = (all.PoisonTurnsByFloor.TryGetValue(k, out var hadAt) ? hadAt : 0) + v;
            all.BombExplodeRiskSamples += t.BombExplodeRiskSamples;
            all.CleanseWouldSave += t.CleanseWouldSave;
            all.CleanseSeenButNoneCarried += t.CleanseSeenButNoneCarried;
            all.CleanseNeededNotCarried += t.CleanseNeededNotCarried;
            all.SupportGoldSpent += t.SupportGoldSpent;
            all.HealGold += t.HealGold;
            all.PanelByFloor.AddRange(t.PanelByFloor);
            // 结局要一起记：通关那一局没有「死在第几层」，把它填成第 15 层会让
            // 「献过金的人平均死在 14.9 层」这种句子读起来像「献了金也救不了」。
            all.OfferingByRun.Add((t.OfferingSteps, t.DeathFloors.Count > 0 ? t.DeathFloors[^1] : 15,
                t.Won, t.TimesFull));

        }

        GD.Print($"\n===== 横扫 {runs} 局（按玩家顺序真玩） =====");
        foreach (var (floor, n) in all.DeathFloors.GroupBy(f => f).Select(g => (g.Key, g.Count())).OrderBy(x => x.Key))
            GD.Print($"死在第 {floor,2} 层：{new string('#', n)} {n}");
        // 平均死层只在「真的死了」的那几局上算。整批 30 局全通关时 `DeathFloors` 是空的，
        // `DefaultIfEmpty(0)` 会在报表上写「平均死亡层数 0.0」——一个不存在的数被报成了一个数。
        GD.Print($"{runs} 局里通关 {runs - all.DeathFloors.Count} 局"
                 + (all.DeathFloors.Count > 0
                     ? $" · 死了 {all.DeathFloors.Count} 局，平均死在第 {all.DeathFloors.Average():F1} 层"
                     : " · 一局没死"));
        // 覆盖率的账先算：死亡 N 局，死因表里必须正好 N 条。
        // 少于 N 就是有死法没认出来——以前那种情况会被静默跳过。
        GD.Print($"死因（最后一战里出现的招式 / 机制，按次数）· 覆盖 {all.DeathCausesSeen} / 死亡 {all.DeathFloors.Count} 局{(all.DeathCausesSeen == all.DeathFloors.Count ? "" : " · **对不上**")}:");
        foreach (var (k, v) in all.DeathsBy.OrderByDescending(kv => kv.Value).Take(12))
            GD.Print($"  {k,-24} {v}");
        GD.Print("死在哪类房:");
        foreach (var (k, v) in deathRooms.OrderByDescending(kv => kv.Value))
            GD.Print($"  {k,-10} {v}");
        // 「其中没有回血药」这张表从来没被填过（判据已经在 `VisitShop` 里直接 check 掉），
        // 报一个恒定 0 的计数器，和报一个空表是同一件事：读者会以为「一间都没有」。
        // 现在报的是**真的发生过什么**：几间店、空手出来几次。
        GD.Print($"商店：共 {all.ShopRooms} 间，逛了 {all.ShopVisits} 次；买药 {all.PotionBought} 瓶、喝掉 {all.HealUsed} 瓶"
                 + $"（其中地图上 {all.HealFromMap} 瓶）");
        // 货架保底只有一种失效方式：**开门那一刻**就没有药。
        // 用「进门时的货架」而不是「逛完之后的货架」判，是因为后者量的是自己刚买走的东西。
        GD.Print($"货架保底（每个货架至少一瓶回血药）：{all.ShopVisits - all.ShopsDoorWithoutPotion} / {all.ShopVisits} 次进门时摆着"
                 + (all.ShopsDoorWithoutPotion > 0 ? $" · **{all.ShopsDoorWithoutPotion} 次没有**" : " · 一次都没漏"));
        GD.Print($"想去买药却买不成：钱不够 {all.CouldNotAfford} 次 · 背包没有空格 {all.BlockedByFull} 次");
        GD.Print($"回复来源（{runs} 局合计）：战后奖励回血 {all.HealFromReward} 点 · 喝药 {all.HealUsed} 瓶");
        // 两件事分开报：进店时有多少钱（钱包峰值）、以及逛完之后还剩多少（货架的上限）。
        // 混成一句的时候，「钱多到花不掉 226 次」读不出是钱包涨得快、还是货架没有货。
        GD.Print($"进店时钱包堆到多高：最多一次 {all.ShopLeftoverGoldWorst}（是当次货架最贵那件的 {all.ShopSurplusRatioWorst:F1} 倍）");
        GD.Print($"逛完之后还剩大钱：{all.ShopLeftoverGold} 次店里能买的都买了、余额仍是货架最贵那件的 3 倍以上");
        // 钱的两个出口必须分开报：泉眼是**有意为之的长期出口**，
        // 而「卖了旧装备」是背包八格逼出来的。混成一个数，就说不清钱流向哪边了。
        GD.Print($"钱的两个出口：泉眼献金 {all.OfferingSteps} 次 · 卖旧装备 {all.ItemsSold} 件（到手 {all.GoldFromSales} 金币）");
        // 献金次数 vs 死层：上一轮留的待办 2 要的正是这张对照。分成「献过金」与「一次没献成」
        // 两拨，「泉眼值不值」不是看总数，是看「花了那笔钱的人走得更深吗」。
        if (all.OfferingByRun.Count > 0)
        {
            // 这一栏以前对每一局都填「死在第几层」，通关那一局填的是最后一层——
            // 于是「献过金平均死在 14.9 层」里混着「根本没死」的局。
            // 通关与死亡必须分开报：一个人的结局只有一种，混在一个平均数里两种都读不出来。
            var withOffering = all.OfferingByRun.Where(x => x.Times > 0).ToList();
            var without = all.OfferingByRun.Where(x => x.Times == 0).ToList();
            var wonWith = withOffering.Count(x => x.Won);
            var wonWithout = without.Count(x => x.Won);
            GD.Print($"泉眼对照（{all.OfferingByRun.Count} 局）：献过金 {withOffering.Count} 局（通关 {wonWith}）· 一次没献 {without.Count} 局（通关 {wonWithout}）");
            GD.Print($"  献过金的死层：{AvgFloor(withOffering)} · 一次没献的死层：{AvgFloor(without)}"
                     + $"（只在「真的死了」的局上算：{withOffering.Count(x => !x.Won)} / {without.Count(x => !x.Won)} 局）");
            foreach (var g in all.OfferingByRun.GroupBy(x => x.Times).OrderBy(g => g.Key))
                GD.Print($"  献 {g.Key} 次：{g.Count(),2} 局，通关 {g.Count(x => x.Won),2} 局，死的平均在 {AvgFloor(g.ToList())}");
        }
        // 「毒这一下就够要命」问的是**规则**：毒够不够重（毒 ≥ 血）、以及这一层的额度还没用完。
        // 这两个数以前会一起说话，现在必须分开读——不然「0 次」既可能是「毒从来没这么重」，
        // 也可能是「预算一直在兜」，读者分不出来。所以下面把两件事各报一行。
        GD.Print($"中毒回合里「毒够重」的：{all.PoisonHeavySeen} / {all.PoisonTurnsSeen} 次"
                 + (all.PoisonHeavySeen == 0 ? "（毒从来没咬到那个份量：这层的毒封在 1~6 点，几乎不可能一次按倒人）" : "")
                 + $"；「毒 + 下一刀」会要命的（含预算判定）：{all.PoisonMateSeen} 次"
                 + $"（解毒草真能救回来 {all.CleanseWouldSave} 次，手里一件都没有 {all.CleanseSeenButNoneCarried} 次）"
                 + $"；判据说「该吃」而背包里没有那一株：{all.CleanseNeededNotCarried} 次");
        GD.Print($"中毒回合里「毒真的会掉血」的：{all.PoisonInDangerSamples} 次 · 其中这一株草能省下的那一截，"
                 + $"判据说值得吃的 {all.CleanseWorthAndCarried} 次");
        GD.Print($"  这一回合的账（下一刀 + 毒 + 这一刀会打死的那只会炸的）够把我送走的：{all.TurnInDangerSamples} 次"
                 + "——这一手必须救命的场，判据读的是它，不是那个 +8 的余量");
        // 按层摊开：整个 1~15 层里只有**两层**会挂毒（瘴气 / 熔岩），
        // 混成一个总数之后，「毒到底有没有场」永远读不出来——分母里
        // 大部分回合来自那两层自己，而层号一混就没人看得见。
        if (all.PoisonTurnsByFloor.Count > 0)
        {
            var rows = all.PoisonTurnsByFloor.OrderBy(kv => kv.Key)
                .Select(kv => $"第 {kv.Key} 层 {kv.Value} 次"
                    + (FloorMechanics.FloorPoisons(MechanicIdAt(kv.Key), GameDataStore.Get().BossStages
                        .FirstOrDefault(b => b.Floor == kv.Key)?.Mechanic) ? "" : "（这一层不挂毒：玩家自己投的毒）"));
            GD.Print($"  中毒回合的分布：{string.Join(" · ", rows)}");
        }
        // 两个口径各报一行，不再挤在一句话里：「值得吃」（毒咬得动我）与
        // 「吃了能活」（不吃就死、吃了能站住）不是同一件事，混着写会得到
        // 「值得吃 6 次里当场会死的还有 6 次」这种读起来像「这株草没用」的句子。
        GD.Print($"中毒的回合里手上带着草：{all.PoisonTurnsWithAntidote} 次"
                 + $"（窄口径：不吃就死、吃了能站住 {all.CleanseWouldSave} 次 · 宽口径：毒咬得动我 {all.CleanseWorthAndCarried} 次）");
        GD.Print($"  宽口径里掐掉毒之后那一刀还要命的 {all.CleanseWorthButLethal} 次"
                 + "——草只解掉毒那一半，剩下的那一刀得靠防御或药。两个数一起读才看得出草有没有用："
                 + "窄口径非零、而吃下的株数是 0，那才是判据没按对键");
        if (MistakePct > 0)
            GD.Print($"算错：{MistakeKind} · 每一步按 {MistakePct}% 的概率误判，一共空过去 {all.SlippedNotTaken} 手（该喝的时候没喝、该吃的时候没吃）");
        GD.Print($"解毒草的货架：{all.ShelfWithAntidote} 次店里有它，其中 {all.AntidoteOnfullBag} 次进门时背包已经满了（八格）· 道具真的腾不出格而错过的：{all.SupportShelfBlocked} 次");
        // 「买进」与「用掉」必须并列：「用掉 0 颗」在一个买进 0 颗的报告里不是结论，
        // 而这句话以前把解毒草的买进数塞在「用掉」那一侧，两种读法都能读通。
        GD.Print($"倒下自爆的场：{all.BombExplodeRiskSamples} 个回合里这一刀会打死一只会炸的敌人（预报与结算读同一个入口 `Battle.ExplodeOnDeathForecast`）");
        GD.Print($"战术道具：炸弹 买进 {all.BombBought} 颗 / 用掉 {all.ItemDamageUsed} 颗 · 解毒草 买进 {all.CleanseBought} 株 / 吃下 {all.CleanseUsed} 株"
                 + $"（背包里最多同时躺过 {all.SupportCarriedWorst} 件，合计花了 {all.SupportGoldSpent} 金币）");
        // 药钱与道具钱分开报：两笔钱抢的是同一个钱包，而它们换来的东西不一样——
        // 药换血，道具换回合。混在一起就说不清「钱花在哪一件上更值」。
        GD.Print($"药钱 {all.HealGold} 金币（喝掉 {all.HealUsed} 瓶）· 道具钱 {all.SupportGoldSpent} 金币（用掉 {all.ItemDamageUsed + all.CleanseUsed} 件）");
        if (all.PanelByFloor.Count > 0)
        {
            GD.Print("进战斗时的面板（按层取中位）：");
            foreach (var g in all.PanelByFloor.GroupBy(x => Math.Min(10, (x.Floor - 1) / 3 * 3 + 1)).OrderBy(g => g.Key))
            {
                var atk = g.Select(x => x.Attack).OrderBy(v => v).ElementAt(g.Count() / 2);
                var best = g.Select(x => x.SkillPower).OrderBy(v => v).ElementAt(g.Count() / 2);
                GD.Print($"  第 {g.Key,2}~{g.Key + 2,2} 层：攻击 {atk,3} · 最强技能倍率 {best / 100.0:F1}");
            }
        }
        // 「打了多少场」与「必须打多少场」之间的差，就是玩家可以选择不打的量。
        // 以前这个数没有，于是「平均死在第 N 层」到底是难度还是不会取舍，说不清。
        var fought = all.BossPathBattlesTaken + all.OptionalBattlesTaken;
        if (fought > 0)
        {
            // 碎片是**跨局**的那条进项：局内那句「钱在深层没有下文」说的是一局之内，
        // 而「碎片 → 解锁」这条线上的账从来没人算过（见第五十九轮）。
        if (all.ShardsEarned >= 0 && runs > 0)
            GD.Print($"回声碎片：{runs} 局一共赚到 {all.ShardsEarned}（平均每局 {all.ShardsEarned / (double)runs:F1}）"
                     + "——它是跨局的那条进项，和局内的金币不是同一本账");
        // 撤退必须和「打了 / 放过」并排报：它以前恒为 0，而它是「放过」那条路
        // 在战斗里唯一真正的出口——不报它，「放过 0 场」既可能是不会取舍，
        // 也可能是**根本没地方退**。
        GD.Print($"战斗里的撤退：{all.FledBattles} 次（其中撤的时候血已低于四成 {all.FledWhileLowHp} 次）");
        GD.Print($"战斗取舍：必经之路 {all.BossPathBattlesTaken} 场 · 可选打了 {all.OptionalBattlesTaken} 场 · 血不够而放过 {all.SkippedOptionalBattles} 场"
                     + $"（路外的仗被走到过 {all.FightOptionalSeen} 次、轮到它们的那一间 {all.OptionalSeen} 次）"
                     + (FightOptional ? "" : " · 可选那一档这一轮整档关着（`--optional=off`）"));
            // 地图上「躲得过的仗」占全部战斗房的比例：这是「有没有退路」的分母。
            // 分母是**层数**，不是局数：一张地图对应一层，15 层 × 100 局 = 1500 张图。
            // 这一行回答的是上一轮那条待办里的一句推不掉的话——
            // 「投降式死亡 = 地图不给退路」。退路有没有，得先知道地图上本来有几条岔。
            var maps = Math.Max(1, all.FloorMaps);
            GD.Print($"地图构成（{maps} 张图）：每张图战斗房 {all.BattleRoomsSeen / (double)maps:F2} 场，"
                     + $"其中躲得过的 {all.OptionalRoomsSeen / (double)maps:F2} 场"
                     + $"（{100.0 * all.OptionalRoomsSeen / Math.Max(1, all.BattleRoomsSeen):F0}%）");
            // 「退路」这个词要先有证据再用：下楼那一刻地图上还剩几场躲得过的仗。
            // 必经那道门的下楼读数是 `BattlesToBoss()`，它是 0 表示这一层根本不用再打——
            // 这一行报的是「玩家实际是在什么状态下楼的」。
            var down = Math.Max(1, all.FloorsEntered);
            GD.Print($"下楼那一刻：还差 {all.ForcedBattlesAtDescend / (double)down:F2} 场必经的仗"
                     + $" · 图上还剩躲得过的仗 {all.FloorsWithOptionalBattle * 100.0 / down:F0}% 的层"
                     + $" · 躲得过的都清干净了 {all.FloorsOnlyForcedBattleLeft * 100.0 / down:F0}% 的层"
                     + $" · 其中有层「血不够放过」{all.FloorsWithOptionalSkipped}");
            GD.Print($"一张图的走路成本：最多走过 {all.FloorsBacktracked} 步（往返各算一步，"
                     + $"每张图平均 {all.FloorSteps / (double)maps:F1} 步）");
        }
        var full = all.PurseProbe.Where(p => p.Potions == 0 && p.CheapestPrice > 0).ToList();
        if (full.Count > 0)
        {
            var affordable = full.Count(p => p.Gold >= p.CheapestPrice);
            GD.Print($"本层有店、身上一瓶药都没有：{full.Count} 次采样，其中钱够买一瓶的 {affordable} 次（{affordable * 100.0 / full.Count:F0}%）");
            foreach (var g in full.GroupBy(p => Math.Min(10, (p.Floor - 1) / 3 * 3 + 1)).OrderBy(g => g.Key))
                GD.Print($"  第 {g.Key,2}~{g.Key + 2,2} 层：{g.Count(),4} 次，钱够买 {g.Count(p => p.Gold >= p.CheapestPrice),4} 次，均价 {g.Where(p => p.CheapestPrice > 0).Select(p => p.CheapestPrice).DefaultIfEmpty(0).Average():F0}，均钱 {g.Average(p => p.Gold):F0}");
        }
        if (all.GoldByFloor.Count > 0)
        {
            // 这一行以前写「结算时手上还剩多少钱」，而它读的是 `GoldByFloor`——
            // 那张表在**每一层收尾**都记一笔（三十局、每局十几层，一共几百个样本）。
            // 名字错得不算冤：结论恰好一样（钱在深层没有下文），但同一个数被两个名字
            // 指过一次之后，下一个人对不上账。现在叫它真名。
            var perFloor = all.GoldByFloor.ToList();
            GD.Print($"每层收尾时手上还剩多少钱：中位 {perFloor.Select(kv => kv.Value).OrderBy(v => v).ElementAt(perFloor.Count / 2)} · 最多 {perFloor.Max(kv => kv.Value)}");
            foreach (var g in perFloor.GroupBy(kv => Math.Min(10, (kv.Key - 1) / 3 * 3 + 1)).OrderBy(g => g.Key))
                GD.Print($"  第 {g.Key,2}~{g.Key + 2,2} 层：{g.Count(),4} 层样本，平均 {g.Average(kv => kv.Value):F0}");
        }
        GD.Print($"坏局 {bad} / {runs}");
        GD.Print("===============================");
        return bad == 0 ? 0 : 1;
    }

    /// <summary>这一局各类房间的净血量开销：总开销才是预算表</summary>
    private sealed class Tally
    {
        public readonly Dictionary<string, (int Rooms, int NetHp)> ByType = new();
        /// <summary>这一层买过几件装备：装备是「攒两三层换一件」的东西，不是一层一件</summary>
        public int GearBuysThisFloor;

        /// <summary>
        /// 同类房「净掉血占当时上限的比例」：探针用它回答「这一间值得不值得进」。
        /// 记比例而不是绝对值——面板在涨，第 3 层那 40 点血到第 13 层不是同一件事。
        /// </summary>
        public readonly Dictionary<RoomType, List<double>> HpCostRatioByRoom = new();
        public readonly List<int> StartHpByFloor = new();
        public readonly List<int> EndHpByFloor = new();
        /// <summary>进店次数：药是按「一次最多两瓶」搬的，所以「搬走几瓶」只有配上店数才读得出意思</summary>
        public int ShopVisits;
        /// <summary>进门那一刻货架上就没有回血药的次数：保底失效只可能发生在这里</summary>
        public int ShopsDoorWithoutPotion;
        /// <summary>这一局里有没有一家店开门时就摆着回血药（保底是「每个货架一瓶」）</summary>
        public bool ShopSawPotionOnShelf;
        public int PotionBought;
        public int HealUsed;
        public int HealFromReward;
        public int HealFromMap;
        public int BlockedByFull;
        public int CouldNotAfford;
        public int ShopRooms;
        /// <summary>泉眼换来的次数：这一局的钱有没有出口，就看这个数和「卖掉的旧装备」</summary>
        public int OfferingSteps;
        public int ItemsSold;
        public int GoldFromSales;
        /// <summary>中毒的回合里手上正好有解毒草的次数：分母够大才谈得上「用掉几次」</summary>
        public int PoisonTurnsWithAntidote;
        /// <summary>手上既有草、判据又说值得吃的次数</summary>
        public int CleanseWorthAndCarried;
        /// <summary>判据说「值得吃」、但掐掉毒之后这一回合照样会被那一刀打死：草没有用武之地</summary>
        public int CleanseWorthButLethal;
        /// <summary>`--mistake` 打开时：掷出「算错」、于是该用的那一手没用上的次数</summary>
        public int SlippedNotTaken;
        /// <summary>买进来的解毒草株数（和「用掉几株」分开记：买而不用的那一半才是问题）</summary>
        public int CleanseBought;
        /// <summary>买进来的炸弹颗数：和「扔出去几颗」并排才有意义</summary>
        public int BombBought;
        /// <summary>战术道具真的被打出去/吃下去的次数：炸弹与解毒草</summary>
        public int ItemDamageUsed;
        public int CleanseUsed;
        /// <summary>背包里最多同时躺着几件战术道具（炸弹 / 解毒草）：一件都没有就谈不上「用过」</summary>
        public int SupportCarriedWorst;
        /// <summary>店里买下的战术道具，以及为它们花掉的钱</summary>
        public int SupportBought;
        public int PoisonTurnsSeen;
        public int PoisonMateSeen;
        /// <summary>中毒回合里「毒自己就够把人按倒」的次数（不含预算那一道）</summary>
        public int PoisonHeavySeen;
        /// <summary>「这一回合的毒真的会掉血」的回合数：解毒草能省下的那一截，分母在这里</summary>
        public int PoisonInDangerSamples;
        /// <summary>「这一回合一共会掉多少 ≥ 现在的血」的回合数：这一手必须救命的场</summary>
        public int TurnInDangerSamples;
        /// <summary>按层数的中毒回合：只在「这一层真的会挂毒」的那两层上有数（瘴气 / 熔岩）</summary>
        public readonly Dictionary<int, int> PoisonTurnsByFloor = new();
        /// <summary>「这一刀打死的怪会炸」的回合数：自爆预告的分母</summary>
        public int BombExplodeRiskSamples;
        /// <summary>「毒 + 下一刀」会要命、且解毒草真的能救回来的次数——这才是解毒草的场</summary>
        public int CleanseWouldSave;
        /// <summary>上面那种局面出现时，背包里一件解毒草都没有的次数</summary>
        public int CleanseSeenButNoneCarried;
        /// <summary>判据说「现在该吃解毒草」、而背包里一株都没有的次数</summary>
        public int CleanseNeededNotCarried;
        public int SupportGoldSpent;
        /// <summary>为回血药花掉的钱（店里买下的那一份）</summary>
        public int HealGold;
        /// <summary>各层进战斗时的面板：攻击力与最强技能倍率，面板涨不涨看这几行比看结论快</summary>
        public readonly List<(int Floor, int Attack, int SkillPower)> PanelByFloor = new();
        /// <summary>每局（献金次数，死在第几层，通关了没，五次用满没）：泉眼值不值，只能靠这几列对照</summary>
        public readonly List<(int Times, int Floor, bool Won, bool Full)> OfferingByRun = new();
        /// <summary>这一局是不是通关了：死层只在「没通关」的局上有意义</summary>
        public bool Won;
        /// <summary>泉眼的五次是否全用完了（「这是一个决定」还是「顺手点到底」）</summary>
        public bool TimesFull;
        /// <summary>店里卖解毒草的店数（用来分开「判据没到」与「店里从来没有」）</summary>
        public int ShelfWithAntidote;
        /// <summary>道具在架上、背包满且腾不出一格：这一件真的错过了</summary>
        public int SupportShelfBlocked;
        /// <summary>店里有解毒草、而背包已经满了的次数——那一株从开局起就没有地方放</summary>
        public int AntidoteOnfullBag;

        /// <summary>进店时钱比货架上最贵的那件还多三倍、出来却没花掉多少的次数</summary>
        public int ShopLeftoverGold;
        public int ShopLeftoverGoldWorst;
        /// <summary>进店时钱包比「货架最贵那件」多几倍（取各次进店的最大值）</summary>
        public double ShopSurplusRatioWorst = 1;
        /// <summary>挡在去 Boss 路上的仗、可选的仗、以及被跳过的可选的仗</summary>
        public int BossPathBattlesTaken;
        public int OptionalBattlesTaken;
        /// <summary>「路外的仗」被考虑过几次：为 0 表示这一档关着，报表必须说出来</summary>
        public int OptionalSeen;
        /// <summary>
        /// 第 4 档（路外的仗）真的被走到过几次。它是 `OptionalSeen` 的**上层**分母：
        /// 前者只在「这一间是路外战斗房」时加一，后者在每一间房都会加一。
        /// 两个数都在，才分得清「没走到那一档」与「走到了、但一间都没敢进」。
        /// </summary>
        public int FightOptionalSeen;
        /// <summary>这一局里「路外的仗」一共有几场：它回答「有没有取舍可做」</summary>
        public int OptionalRoomsSeen;
        /// <summary>这一局赚到多少回声碎片（通关 5 / 阵亡 1，见 `config.meta`）</summary>
        public int ShardsEarned;
        /// <summary>
        /// 撤离过几次。它以前恒为 0，而且不是「不需要撤」——是**探针从来不按那个键**：
        /// 一进战斗只有「打到赢或者死」两条路。于是「63% 的仗是可选的」这件事
        /// 在探针的世界里等于不存在：它会走进去，然后把它打完。
        /// </summary>
        public int FledBattles;
        /// <summary>撤离时血已经低到四成以下的次数：这一走是「救了自己」还是「走晚了」</summary>
        public int FledWhileLowHp;
        /// <summary>最近一场仗**开打那一刻**的血量比例：判断「这一间本来就不该进」要看它</summary>
        public double LastFightEntryHpPct = 1;

        /// <summary>这一局一共几张地图（层数）：报「每张图几场仗」时的分母</summary>
        public int FloorMaps;
        /// <summary>这一局地图上战斗房一共几场（含必经的）：分母</summary>
        public int BattleRoomsSeen;
        public int SkippedOptionalBattles;
        /// <summary>打完的层数（不含最后一层）：不是每一层都能被单局读数数到</summary>
        public int FloorsWon;
        /// <summary>下楼那一刻还差几场的样本数（= 层数）</summary>
        public int FloorsEntered;
        /// <summary>下楼那一刻，挡在去 Boss 路上的战斗房还有几场</summary>
        public int ForcedBattlesAtDescend;
        /// <summary>这张图上还有躲得过的仗没打</summary>
        public int FloorsWithOptionalBattle;
        /// <summary>这张图上有躲得过的仗被「血不够」放过过</summary>
        public int FloorsWithOptionalSkipped;
        /// <summary>下楼那一刻，地图上只剩必经的那几场（躲得过的都清干净了）</summary>
        public int FloorsOnlyForcedBattleLeft;
        /// <summary>这一局里「只剩必经那道门」时往返的最多步数（绕路成本）</summary>
        public int FloorsBacktracked;
        /// <summary>这一局一共走了多少步：它是「一张图的走路成本」的分母</summary>
        public int FloorSteps;
        /// <summary>这一张图上「躲得过」的仗还剩几场（最近一次采样）</summary>
        public int LastOptionalLeft;
        /// <summary>这一张图上「躲不过」的仗还剩几场（最近一次采样）</summary>
        public int LastForcedLeft;
        /// <summary>这一张图上「躲得过」的仗被「血不够」放过过</summary>
        public bool FloorSawOptionalSkip;
        /// <summary>这一张图上出现过「躲得过」的仗（哪怕已经打完）</summary>
        public bool FloorSawOptional;
        /// <summary>这一层已经走了几步（不是房间数：同一间房可能被路过两次）</summary>
        public int WalkStepsOnFloor;

        /// <summary>死因：最后一战里敌人用的招式 / 机制，按名字累计</summary>
        public readonly Dictionary<string, int> DeathsBy = new();
        public readonly List<int> DeathFloors = new();
        public readonly Dictionary<RoomType, int> DeathsByRoom = new();
        public RoomType LastDeathRoom;
        /// <summary>死因表里的条目总数：它必须等于死亡局数，否则这张表是漏的</summary>
        public int DeathCausesSeen;
        public string LastDeathLog = "";
        /// <summary>
        /// 最严重的**单笔**掉血（伤害值, 造成它的那一行）。
        ///
        /// 「谁打死的你」在血很低的时候往往不是最后一刀：一条日志尾巴里躺着
        /// 「攻击你造成 13」和「敌人倒下时炸你 22」，而最后一击可能只是那 1 点普攻。
        /// 以前死因表把这种局面落成「未定 + 一整段日志尾巴」，读者得自己从五行里挑。
        /// 现在它被归成「最后一击 X 点（最重的一下是 Y）」——一个数，加上一个来源。
        /// </summary>
        public (int Damage, string Line) DeathBlow;
        public int MaxHpSeen;

        public void Add(string type, int netHp)
        {
            var cur = ByType.TryGetValue(type, out var v) ? v : (0, 0);
            ByType[type] = (cur.Item1 + 1, cur.Item2 + netHp);
        }

        public int MaxHp;
        /// <summary>只给横扫用：满血时攒下的钱够买几瓶药（衡量「没药是买不起还是买不着」）</summary>
        public readonly List<(int Floor, int Gold, int Potions, int CheapestPrice, bool Shop)> PurseProbe = new();
        /// <summary>每层进店前的余额：装备到顶之后它只会一路涨，是「钱没处花」的体温计</summary>
        public readonly Dictionary<int, int> GoldByFloor = new();

        public void Report(uint seed)
        {
            GD.Print($"\n===== 血量预算 seed={seed} =====");
            foreach (var (type, (rooms, net)) in ByType.OrderByDescending(kv => kv.Value.NetHp))
            {
                var avg = rooms == 0 ? 0 : net / (double)rooms;
                GD.Print($"{type,-8} {rooms,3} 间 · 净掉血 {net,5} · 单间占上限 {avg / Math.Max(1, MaxHp),6:P1}");
            }
            GD.Print($"商店：{ShopRooms} 间（逛了 {ShopVisits} 次）；这局买到的药 {PotionBought} 瓶，用掉 {HealUsed} 瓶（其中地图上 {HealFromMap} 瓶）");
            GD.Print(ShopSawPotionOnShelf
                ? "货架保底：这一局进过的店里，开门时就摆着回血药"
                : "货架保底：这一局一间有药的店都没进过（或进的时候已经被买空了）");
            GD.Print($"回复来源：战后奖励回血 {HealFromReward} 点 · 喝药 {HealUsed} 瓶（地图上 {HealFromMap} 瓶）");
            if (StartHpByFloor.Count > 0)
            {
                // 行数按**两边的最大值**走：读数少一行的时候，这里以前是静默少打一行，
                // 看上去像「本来就只有这么多层」——而真相是「最后一层没有收尾读数」。
                // 一张表最不该做的事，就是用「没有这一行」表示「这一行有问题」。
                var rows = Math.Max(StartHpByFloor.Count, EndHpByFloor.Count);
                GD.Print("层 │ 进房血% │ 出房血%");
                for (var i = 0; i < rows; i++)
                {
                    var start = i < StartHpByFloor.Count ? $"{StartHpByFloor[i],6}" : "     -";
                    var end = i < EndHpByFloor.Count ? $"{EndHpByFloor[i],6}" : "     - ← 没有收尾读数";
                    GD.Print($"{i + 1,2} │ {start} │ {end}");
                }
            }
            GD.Print("=============================");
        }
    }

    /// <summary>某一层的楼层机制 id（数据里那张表：1 层 none、2 层瘴气…）</summary>
    private static string? MechanicIdAt(int floor) =>
        GameDataStore.Get().FloorMechanicMap.TryGetValue(floor.ToString(), out var id) ? id : null;

    /// <summary>死层均值：只算真的死了的那几局（通关那一局没有死层）</summary>
    private static string AvgFloor(IReadOnlyList<(int Times, int Floor, bool Won, bool Full)> rows)
    {
        var died = rows.Where(r => !r.Won).ToList();
        return died.Count == 0 ? "—（一局没死）" : $"{died.Average(r => r.Floor):F1} 层（{died.Count} 局）";
    }

    private static int RunOne(uint seed, Tally tally, bool quiet = false)
    {
        var game = new Game(seed);
        var checks = 0;
        var bad = 0;

        if (!quiet) GD.Print($"\n===== 真实游玩探针 seed={seed} =====");
        var guard = 0;
        var check = (Action<bool, string>)((cond, what) =>
        {
            checks++;
            if (cond) return;
            bad++;
            GD.Print($"  [!!] 第 {game.Floor} 层：{what}");
        });
        // 先走一步、再判「结束了没」。
        //
        // 以前写成 `while (!game.IsGameOver())`：最后那场 Boss 打完，状态在这一圈
        // **末尾**才变成 Won，下一圈开头条件就不成立了——`FloorCleared` 那一支
        // （唯一会记「这一层收尾」的地方）永远轮不到最后一层。
        // 后果不是少一行日志：横扫里 `平均死亡层数` 读到空表，报「死在第 0 层」，
        // 而实际 30 局全部通关。**判据站错了位置，数是错的，结论就反了。**
        CountBattlesOnFloor(game, tally);   // 第 1 层没有 Descend，得单独数一次
        do
        {
            // 一间一间走过去：用 Move 而不是 Teleport，走的是玩家那条路
            if (game.FloorCleared)
            {
                // 这一层打完了：**它是这一局最后一层**（通关）或者还有下一层。
                // 两件事只差一次 ClearRoom，不分开判就会把最后一层当成「还没打完」。
                var lastFloor = game.IsGameOver();
                tally.EndHpByFloor.Add(Pct(game));
                // 「这一层花完了还剩多少」：以前只记结算时的那一笔，
                // 而钱是不是「没处花」要看每一层结束时它还堆着多少——
                // 结算那笔混着最后一笔赏金，也看不见中间是怎么攒起来的。
                tally.GoldByFloor[game.Floor] = game.Player.Gold;
                if (lastFloor) break;
                // 下楼那一刻这张图的状态：躲得过的仗还剩几场、躲不过的还剩几场。
                // 这一笔就是「退路有没有用上」的那一笔——`BattlesToBoss()` 是必经数，
                // 与它同一份判据的那个 0 表示「这一层真的只剩一道门要过」。
                tally.FloorsEntered += 1;
                tally.ForcedBattlesAtDescend += game.BattlesToBoss();
                if (tally.LastOptionalLeft > 0) tally.FloorsWithOptionalBattle += 1;
                if (tally.FloorSawOptionalSkip) tally.FloorsWithOptionalSkipped += 1;
                if (tally.LastOptionalLeft == 0) tally.FloorsOnlyForcedBattleLeft += 1;
                tally.FloorsBacktracked = Math.Max(tally.FloorsBacktracked, tally.WalkStepsOnFloor);
                tally.FloorSteps += tally.WalkStepsOnFloor;
                var (ok, _) = game.Descend();
                if (!ok) break;
                tally.StartHpByFloor.Add(Pct(game));
                tally.WalkStepsOnFloor = 0;
                tally.FloorSawOptional = false;
                // 新的一层：本层的「装备已经买过一件」重新计数。
                tally.GearBuysThisFloor = 0;
                CountBattlesOnFloor(game, tally);
                tally.FloorSawOptionalSkip = false;
                continue;
            }
            // 这一张图上还剩几场「躲得过」与「躲不过」的仗：一楼一采样。
            // 它回答的是上一轮那条待办里推不掉的一句话——「本层唯一的精英房、
            // 血只剩三成」到底是**地图不给退路**，还是**玩家没去找那条退路**。
            // 两个数在同一张图上各自记账，才分得开这两件事。
            CountLeftOnFloor(game, tally);
            // 进每一间房之前记一笔钱包与药：横扫靠这张表回答「药到底缺在哪」。
            // 只在「这层有商店且一瓶药都没买过」时采样——否则数出来的是
            // 「我满血路过了一百次」，与「买不到」无关。
            // 采样的时机是「这一步之前」，而一步可能是「走进一间房、被砍掉半条命、
            // 又回到地图上」——于是 `Hp >= MaxHp` 把「刚被打过、正要去买药」的那些步
            // 全滤掉了，剩下的 196/203 采样落在第 1~3 层（那里还没挨过打）。
            // 一个只在开局成立的判据，量的是「开局时药缺不缺」，而缺药是深层的事。
            // 现在按「药不足（一瓶都没有）」采——血满不满与买不买药无关。
            if (HealCount(game) == 0)
            {
                var probe = PurseProbeOf(game);
                if (probe.Shop) tally.PurseProbe.Add(probe);
            }
            // 货架采样：这家店卖不卖解毒草、我进门时还有没有格子放它。
            // 「解毒草 0 次使用」必须能分开是「判据没到」还是「店里从来没有」。
            for (var si = 0; si < game.Map.Rooms.Count; si++)
            {
                if (game.Map.Rooms[si].Type != RoomType.Shop || game.Map.Rooms[si].Cleared) continue;
                var st = game.ShopStock(si);
                var hasClean = st.Any(x => !x.Sold && x.Item.Cleanse == true);
                if (!hasClean) continue;
                tally.ShelfWithAntidote++;
                if (game.Player.Inventory.Count >= game.Capacity) tally.AntidoteOnfullBag++;
            }
            // 剩下的钱也按「进店之前」采一次：写在 VisitShop 里会把「买之前」的余额
            // 记成「买之后」，读数直接反了（第 10~12 层平均剩 1243，而那些局里
            // 一次店都没进过——钱从来没被花过，就不该出现在「逛完还剩多少」里）。
            tally.GoldByFloor[game.Floor] = game.Player.Gold;
            var walked = WalkToNextRoom(game, tally);
            // 走不动了：这一层没清完（走不到任何目标），或者这一局已经结束。
            // 收尾读数统一在循环出口那一笔记，这里只管退出。
            if (!walked) break;
            var hpBefore = game.Player.Hp;
            var maxHpBefore = game.Stats.MaxHp;
            var floorBefore = game.Floor;
            var floorBeforePlay = game.Floor;
            var roomType = game.CurrentRoom.Type;
            PlayRoom(game, check, tally);
            // 这一间吃掉了几成上限：喂给 `ExpectedHpCost`，让「值不值得进」有一个实测的分母。
            // 只记战斗房，祭坛/商店那一类不进这个表——它们的「代价」不是血。
            if (roomType is RoomType.Enemy or RoomType.Elite or RoomType.Boss && maxHpBefore > 0)
            {
                if (!tally.HpCostRatioByRoom.TryGetValue(roomType, out var costs))
                    tally.HpCostRatioByRoom[roomType] = costs = new List<double>();
                costs.Add(Math.Clamp((double)Math.Max(0, hpBefore - game.Player.Hp) / maxHpBefore, 0, 1));
            }
            // 每层打完就存一次、读回来一次：玩家最常做的事就是「打一层、关窗、明天再开」。
            if (game.Floor != floorBeforePlay && !game.IsGameOver())
            {
                var reloaded = RoundTrip(game, out var why);
                if (reloaded is null) check(false, $"存档/读档失败：{why}");
                else
                {
                    check(reloaded.Player.Inventory.Count == game.Player.Inventory.Count,
                        $"读档后背包少了 {game.Player.Inventory.Count - reloaded.Player.Inventory.Count} 格");
                    check(Math.Abs(reloaded.TrainingAttackBuff - game.TrainingAttackPct) < 1e-9,
                        "读档后本层训练加成对不上");
                    check(reloaded.Player.Gold == game.Player.Gold, "读档后金币对不上");
                    game = reloaded;
                }
            }
            // 每一间房之后都像人一样看一眼装备：捡到更好的就换上，槽里是垃圾就换下来。
            // 没有这一步，「按玩家顺序真玩」玩的是一个从不换装备的人——
            // 那不是难度，那是没人这么玩。
            DressUp(game, check);
            if (game.Floor == floorBefore) tally.Add(roomType.ToString(), Math.Max(0, hpBefore - game.Player.Hp));
        } while (!game.IsGameOver() && guard++ < 400);
        // 循环出口那一笔：**最后一层的收尾读数**
        //
        // 只在循环开头判「这一层完了没」是不够的：最后一场 Boss 打完，`Won` 是在
        // 这一圈末尾才落定的，下一圈开头条件就不成立——最后那一层于是没有任何
        // 收尾读数（`EndHpByFloor` 少一行）。横扫里 `平均死亡层数` 读的正是这张表，
        // 读空了就报「死在第 0 层」，而实际 30 局全部通关。**判据站错一格，结论就反了。**
        if (tally.EndHpByFloor.Count < tally.StartHpByFloor.Count + 1)
        {
            tally.EndHpByFloor.Add(Pct(game));
            tally.GoldByFloor[game.Floor] = game.Player.Gold;
        }

        tally.Won = game.Status == GameStatus.Won;
        // 这一局赚到的碎片：它是「通关之外的另一条进项」，而探针从没记过它。
        // 横扫里那句「钱在深层没有下文」说的是一局之内；跨局的那条线（碎片 → 解锁）
        // 有没有下文，得先有这个数。
        tally.ShardsEarned += game.ShardsEarned;
        tally.TimesFull = tally.OfferingSteps >= game.Offering.MaxSteps;
        if (game.Status != GameStatus.Won)
        {
            // 死因不是「难度太高」这个形容词，是「哪一招把你打死的」。
            // 日志里最后那几行就是现场——按招式名归类比再猜十次有用。
            tally.DeathFloors.Add(game.Floor);
            tally.DeathsByRoom[tally.LastDeathRoom] = (tally.DeathsByRoom.TryGetValue(tally.LastDeathRoom, out var n) ? n : 0) + 1;
            var causes = CausesOfDeath(tally.LastDeathLog, game.Player.Skills.Select(x => x.Id).ToHashSet()).ToList();
            check(causes.Count <= 1, $"一局的死因认出了 {causes.Count} 条：{string.Join(" / ", causes)}");
            // 一张表绿了不等于它对：死因必须先**对得上总数**。
            // 以前这里是「抽到几条记几条」，于是某一类死法认不出来时，
            // 死因表加起来会比死亡局数少，而报表看起来一切正常。
            // 现在认不出来的那一局会明确落成一个「未归类」的口径，并且带着日志尾巴。
            //
            // 「自己放的技能」这一类已经由 `CausesOfDeath` 自己剔掉了（它拿着玩家的技能表），
            // 这里不再在**外面**复述一遍同一套过滤——两处都过滤的时候，第二处永远是空操作，
            // 而它看着像"还在一层保险"，下一个人就会以为第一处不做这件事。
            // 认不出来的那一局：与其把五行日志尾巴塞进表里，不如说清「最重的一下是谁」。
            // 这一档仍然带着现场（日志尾巴留在 `--probe-dump` 里），只是不再把它当名字。
            if (causes.Count == 0)
            {
                // 死因表里剩下的那几档，其实都能认出「这是谁打的」，只是认不出招式名
                // （普攻、倒下自爆、楼层机制）。把它们归进一个**按来源分桶**的口径，
                // 比一条一条「未定 + 一段日志尾巴」有用得多：读者要的是「死在哪类伤害上」。
                causes.Add(SourceOf(tally.DeathBlow, tally.LastDeathLog));
            }
            tally.DeathCausesSeen += causes.Count;
            foreach (var cause in causes)
                tally.DeathsBy[cause] = (tally.DeathsBy.TryGetValue(cause, out var m) ? m : 0) + 1;
        }
        if (!quiet)
        {
            GD.Print($"结果：{game.Status} · 第 {game.Floor} 层 · 金币 {game.Player.Gold} · 药 {HealCount(game)} 瓶");
            GD.Print($"背包：{string.Join("、", game.Player.Inventory.Select(id => game.ItemIndex.TryGetValue(id, out var d) ? d.Name : $"<{id} 查不到>"))}");
            GD.Print($"核对 {checks} 项，其中 {bad} 项不成立");
            GD.Print("=========================");
        }
        return bad == 0 ? 0 : 1;
    }

    /// <summary>
    /// 存一次、读一次。走的是玩家真的会走的那条路（SaveSerializer），
    /// 不是「new Game 然后复制字段」——后者永远绿，因为它绕过了整条序列化。
    /// </summary>
    private static Game? RoundTrip(Game game, out string why)
    {
        why = "";
        var path = "user://probe_run.cfg";
        try
        {
            AbyssEcho.Autoload.SaveSerializer.Save(path, game, game.Rng.Seed);
            if (!AbyssEcho.Autoload.SaveSerializer.CanResume(path)) { why = "存了但读不回来"; return null; }
            var loaded = AbyssEcho.Autoload.SaveSerializer.Load(path, new MetaState());
            if (loaded is null) { why = "Load 返回空"; return null; }
            // 逐字段对一遍：Load 只写「读回来是一局」，不保证「读回来是那一局」。
            // 以前这条探针只比背包、金币、苦练三项，而 ConfigFile 把 double 存成
            // Variant 的 float —— training_attack_pct 就是在这一层整局读不回来的
            // （拆箱类型不符，直接 InvalidCastException）。多比几项，下一条同类问题
            // 才不用再靠「玩家关窗才发现」。
            var diffs = new List<string>();
            void Same<T>(T a, T b, string what)
            {
                if (!EqualityComparer<T>.Default.Equals(a, b)) diffs.Add($"{what}: {a} ≠ {b}");
            }
            Same(loaded.Floor, game.Floor, "楼层");
            Same(loaded.Loop, game.Loop, "周目");
            Same(loaded.PlayerIndex, game.PlayerIndex, "位置");
            Same(loaded.Player.Hp, game.Player.Hp, "生命");
            Same(loaded.Player.BaseMaxHp, game.Player.BaseMaxHp, "生命上限");
            Same(loaded.Player.BaseAttack, game.Player.BaseAttack, "攻击");
            Same(loaded.Player.BaseDefense, game.Player.BaseDefense, "防御");
            Same(loaded.Player.Level, game.Player.Level, "等级");
            Same(loaded.Player.Xp, game.Player.Xp, "经验");
            Same(loaded.Player.Gold, game.Player.Gold, "金币");
            Same(loaded.Kills, game.Kills, "击杀");
            Same(loaded.BattlesFought, game.BattlesFought, "战斗场次");
            Same(Math.Round(loaded.TrainingAttackPct, 6), Math.Round(game.TrainingAttackPct, 6), "苦练加成");
            Same(loaded.Player.Skills.Count, game.Player.Skills.Count, "技能数");
            // 装备槽：按下标比会误报（背包会被重排），按「那件是什么」比才是玩家看到的东西
            foreach (var slot in EntityRules.EquipSlots)
                Same(Inventory.EquippedDef(loaded.Player, slot, loaded.ItemIndex)?.Id,
                     Inventory.EquippedDef(game.Player, slot, game.ItemIndex)?.Id, $"装备槽 {slot}");
            // 本层待生效的符文队列：只存一条的话，存档会把第二条静默丢掉
            Same(string.Join(",", loaded.PendingBonusAffixes), string.Join(",", game.PendingBonusAffixes), "符文队列");
            // 房间标记：读档把「已清空」冲掉的话，打过的宝箱能再开一次
            var clearedL = loaded.Map.Rooms.Count(r => r.Cleared);
            var clearedG = game.Map.Rooms.Count(r => r.Cleared);
            Same(clearedL, clearedG, "已清空的房间数");
            // 毒死额度是「一层只能发生 N 次」的计数：不落盘的话，读一次档就白拿一次免死。
            Same(loaded.Player.PoisonDyingLeft, game.Player.PoisonDyingLeft, "本层剩余的毒死额度");
            if (diffs.Count > 0)
            {
                why = string.Join("；", diffs);
                return null;
            }
            return loaded;
        }
        catch (Exception e)
        {
            why = e.Message;
            return null;
        }
        finally
        {
            AbyssEcho.Autoload.SaveSerializer.Delete(path);
        }
    }

    /// <summary>
    /// 从最后一战的日志里摘死因：敌人招式名（「XX使用「炼狱」」）、楼层机制名、
    /// 以及「普攻」——最后一个要单独数，因为「被平砍砍死」和「被大招砍死」
    /// 是两条不同的结论。
    /// </summary>
    /// <summary>
    /// 认不出来招式名时，至少说清「是哪一类伤害」。三档，都从现场那两句里读：
    ///   · 倒下自爆：「XX倒下的瞬间炸开一团怨气」；
    ///   · 环境：「灼热的风掠过」「毒雾」这类不带主语的句子；
    ///   · 普攻：句子是完整的「XX攻击你，造成 N 点伤害」。
    /// </summary>
    private static string SourceOf((int Damage, string Line) blow, string log)
    {
        var line = blow.Line.Length > 0 ? blow.Line : log;
        if (line.Contains("炸开一团狂热")) return "普攻之外:倒下自爆";
        if (line.Contains("炸开一团怨气")) return "机制:自爆";
        if (line.Contains("倒下的瞬间")) return "普攻之外:倒下自爆";
        if (System.Text.RegularExpressions.Regex.IsMatch(line, "^[^，]{0,12}(风|毒雾|地面|低语|空气)")) return "机制:环境";
        var hits = System.Text.RegularExpressions.Regex.Match(line, "^(.{1,10}?)攻击你");
        if (hits.Success) return $"普攻:{hits.Groups[1].Value}";
        return line.Length == 0 ? "未定:（没有日志）" : "未定:" + Shorten(line);
    }

    /// <summary>
    /// 这一行是不是**玩家自己**写下的。玩家与敌人的句子只差一个主语：
    /// 「你发起攻击，造成 7 点伤害」和「巨鼠攻击你，造成 7 点伤害」。
    /// 判据落在主语上，不落在「有没有造成」上——后半句两边都有。
    /// </summary>
    /// 玩家的句子还有个特点：敌人那几句里一定先出现**敌人的名字**，而玩家的句子里
    /// 主语就是「你」——所以只要「你」出现在句首，或者句子里没有「XX攻击你」这种结构，
    /// 它就是玩家自己写的。（`你举起盾牌` / `你发起攻击` / `你使用…` 都落在这里。）
    private static bool OwnLine(string line) =>
        line.StartsWith("你")
        || (line.Contains("使用「") && !line.Contains("攻击你") && !line.Contains("对你"))
        || line.Contains("你投出") || line.Contains("你吃下");

    /// <summary>把一行日志压成「谁做了什么」：表格里只放得下这么长</summary>
    private static string Shorten(string line)
    {
        var text = line.Trim();
        var cut = text.IndexOfAny(new[] { '，', '。', '！', '｜', '/' });
        if (cut > 0) text = text[..cut];
        return text.Length <= 18 ? text : text[..18] + "…";
    }

    private static string FirstLine(string log) =>
        log.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "（没有日志）";

    /// <summary>
    /// 从最后一战的日志里摘死因。**最后一刀才算死因**。
    ///
    /// 以前这里把日志尾巴里出现过的每一招都算一遍，于是一局的死因能有好几行
    /// （「覆盖」那一栏于是常年对不上：死亡 8 局、死因表 9~10 条）。而「谁打死的你」
    /// 只有一个答案：把血打到 0 的那一下。现在倒着读，认出来的第一条就是死因；
    /// 一条都认不出来时才落到「未定」（带日志尾巴，供下一个人查）。
    /// </summary>
    private static IEnumerable<string> CausesOfDeath(string log, IReadOnlySet<string> playerSkillIds)
    {
        if (string.IsNullOrEmpty(log)) yield break;
        // 玩家自己的技能名：「敌人使用「X」」与「你使用「X」」两句话只差一个字，
        // 不查一遍就会把玩家自己放的治愈记成死因。
        var own = GameDataStore.Get().Skills.Where(k => playerSkillIds.Contains(k.Id)).Select(k => k.Name).ToHashSet();
        var lines = log.Split('\n').Where(l => l.Trim().Length > 0).ToList();
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            // 毒伤要单独归一类。它以前落不进任何一格：那一行是「你受毒素侵蚀，失去 N 点生命」，
            // 既没有「造成」也没有招式名，而下面那条过滤只认「造成 / 失去 / 损失」——
            // 于是「被毒放倒」的那一局在表里要么消失、要么被算到别的招式头上。
            if (line.Contains("受毒素侵蚀") || line.Contains("被毒素放倒"))
            {
                yield return "机制:中毒";
                yield break;
            }
            // 自爆 / 反震 / 楼层机制打回来的：它们写的是「造成 N 点伤害」但没有招式名，
            // 以前一律漏进「未定」。这些句子自己就写清了来源，读它比猜它强。
            //
            // 「炸开一团怨气」是怨恨层（机制自爆），「炸开一团狂热」是狂信者自己的
            // `ExplodeOnDeath`（敌人天赋）。两句都写「自爆」，但来源不是一回事：
            // 同一张表里混成一格，就说不清「该死的是这一层的规则，还是这只怪」。
            if (line.Contains("炸开一团狂热")) { yield return "普攻之外:倒下自爆"; yield break; }
            if (line.Contains("炸开一团怨气")) { yield return "机制:自爆"; yield break; }
            if (line.Contains("反震") && line.Contains("造成")) { yield return "机制:反震"; yield break; }
            if (!line.Contains("造成") && !line.Contains("失去") && !line.Contains("损失")) continue;
            var skill = System.Text.RegularExpressions.Regex.Match(line, "使用「(.+?)」");
            if (skill.Success)
            {
                var name = skill.Groups[1].Value;
                if (own.Contains(name)) continue;          // 自己放的，不是死因
                yield return "技能:" + name;
                yield break;
            }
            // 普攻只认完整的那一句「XX攻击你，造成 N 点伤害」：
            // 楼层机制的句子（「灼热的风掠过，造成 5 点伤害」）里也有「造成」，
            // 它以前被归成「普攻」——最粗的那一类把最细的来源吞掉了。
            if (line.Contains("攻击你") && line.Contains("造成"))
            {
                // 谁打的也要说：同一张表里「普攻 2」和「普攻:巨魔 1」是两句话——
                // 前者说明这一类还没细分，后者说明份量落在哪只怪身上。
                var who = System.Text.RegularExpressions.Regex.Match(line, "^(.{1,10}?)攻击你");
                yield return who.Success ? "普攻:" + who.Groups[1].Value : "普攻";
                yield break;
            }
            var mech = System.Text.RegularExpressions.Regex.Match(line, "^(.+?)[，,。]");
            yield return "机制:" + (mech.Success ? mech.Groups[1].Value : line.Trim());
            yield break;
        }
    }

    private static int Pct(Game game) => (int)Math.Round(100.0 * game.Player.Hp / Math.Max(1, game.Stats.MaxHp));

    private static int HealCount(Game game) =>
        Inventory.Consumables(game.Player, game.ItemIndex).Count(c => c.Def.Heal is > 0);

    /// <summary>
    /// 这一层地图上有几场仗，其中几场是「路外的」（躲得过的）。
    ///
    /// 为什么要两个数：上一轮留的待办说「39 局死亡里有 20 局是投降式死亡——本层唯一的
    /// 精英房，血只剩三成」。那句结论要成立，前提是**玩家本来可以绕过它**。
    /// 如果一层里地图上只有必经的那几场，那「投降式」说的就只是难度，不是没有退路。
    /// 这两个数放在一起，才分得开「地图不给退路」和「这一层就是要打这么多」。
    /// </summary>
    private static void CountBattlesOnFloor(Game game, Tally tally)
    {
        var total = 0;
        var optional = 0;
        foreach (var room in game.Map.Rooms)
        {
            if (room.Type is not (RoomType.Enemy or RoomType.Elite or RoomType.Boss)) continue;
            total += 1;
            // 「路外」的判据是「不挡在去 Boss 的路上」，和 `WalkToNextRoom` 第 4 档
            // 用的是同一条：站在当下这个位置、走最短路能不能绕过去。
            // 判据只写一遍，两处读同一个——不然「绕过几场」与「打过几场」会各说各的。
            if (!OnBossPath(game, game.Map.Rooms.IndexOf(room))) optional += 1;
        }
        tally.BattleRoomsSeen += total;
        tally.OptionalRoomsSeen += optional;
        tally.FloorMaps += 1;
    }

    /// <summary>
    /// 每走一步之前，给这一张图记一笔「还剩几场躲得过的仗、几场躲不过的」。
    ///
    /// 记的是**同一张图的最终状态**（下楼那一刻），所以同一层的多次采样只留最后一份：
    /// 前面几步看到的是「还没打完」，那不是一个决定，那是一个过程。
    /// 只在地图**真的还剩战斗房**时刷新，免得「本层已清空」也被算成一档。
    /// </summary>
    private static void CountLeftOnFloor(Game game, Tally tally)
    {
        var optionalLeft = 0;
        var forcedLeft = 0;
        for (var i = 0; i < game.Map.Rooms.Count; i++)
        {
            var room = game.Map.Rooms[i];
            if (room.Type is not (RoomType.Enemy or RoomType.Elite or RoomType.Boss)) continue;
            if (room.Cleared) continue;
            if (OnBossPath(game, i)) forcedLeft += 1;
            else optionalLeft += 1;
        }
        tally.LastForcedLeft = forcedLeft;
        tally.LastOptionalLeft = optionalLeft;
        tally.FloorSawOptional = tally.FloorSawOptional || optionalLeft > 0;
    }

    /// <summary>这一间房是不是挡在「我 → Boss」的某条最短路上</summary>
    private static bool OnBossPath(Game game, int index)
    {
        if (index < 0) return false;
        var links = game.Map.Links;
        var boss = game.Map.BossIndex;
        if (boss < 0 || boss >= links.Count) return false;
        var dist = MapGen.BfsDistances(links, game.PlayerIndex);
        if (dist[boss] < 0 || dist[index] < 0) return false;
        // index 在最短路上 ⇔ 从它出发仍能走到 Boss，且步数正好少一段
        var fromIndex = MapGen.BfsDistances(links, index);
        return dist[index] + fromIndex[boss] == dist[boss];
    }

    /// <summary>「满血时我手上多少钱、几瓶药、最近的店卖多贵」——一次采样</summary>
    private static (int Floor, int Gold, int Potions, int CheapestPrice, bool Shop) PurseProbeOf(Game game)
    {
        var potions = HealCount(game);
        var price = int.MaxValue;
        var shop = false;
        for (var i = 0; i < game.Map.Rooms.Count; i++)
        {
            if (game.Map.Rooms[i].Type != RoomType.Shop) continue;
            shop = true;
            foreach (var s in game.ShopStock(i).Where(s => !s.Sold && s.Item.Heal is > 0))
                price = Math.Min(price, s.Price);
        }
        return (game.Floor, game.Player.Gold, potions, price == int.MaxValue ? -1 : price, shop);
    }

    /// <summary>
    /// 血掉到七成以下就在地图上喝一瓶——真人就是这么玩的。
    /// 喝之前先看一眼下一间房是不是战斗房：走廊上补满，进战斗只带满血。
    /// </summary>
    private static void TopUpOnMap(Game game)
    {
        TopUpOnMap(game, null);
    }

    /// <summary>
    /// 同上，但把「在地图上喝掉的那一瓶」记进账。
    /// 报表里「喝掉 N 瓶」以前只数了战斗里与店里那两处，地图上这一处一直是空白——
    /// 于是「药钱 / 喝掉瓶数」算出来的单价，分母比真实值小，单价偏高。
    /// </summary>
    private static void TopUpOnMap(Game game, Tally? tally)
    {
        if (game.Player.Hp >= game.Stats.MaxHp * 0.7) return;
        var potion = Inventory.Consumables(game.Player, game.ItemIndex)
            .Where(c => c.Def.Heal is > 0)
            // 小药先喝，大药留到真要命的时候
            .OrderBy(c => c.Def.Heal)
            .FirstOrDefault();
        // 只在「下几间房里还有仗要打」时补血：本层已经清空还喝一瓶，
        // 是纯浪费——而一个人不会这么玩，统计出来的数也就不该这么算。
        if (game.FloorCleared) return;
        // 没药就买。这一句以前不存在：地图上血掉到七成以下、背包里没药、而下一间
        // 就是商店——探针照样走进战斗，然后被毒一刀带走。横扫里「判据说该吃解毒草」
        // 的那两次，脚下的地图上都还有一间没逛的店。
        if (potion.Def is null) { BuyPotionHere(game, tally); return; }
        var index = potion.Index;
        if (index < 0 || index >= game.Player.Inventory.Count) return;
        var before = game.Player.Hp;
        game.Player.Hp = Math.Min(game.Stats.MaxHp, game.Player.Hp + potion.Def.Heal!.Value);
        // 与 UI 走同一组入口：道具从背包里摘掉这件事只有 Inventory.RemoveItem 会顺带修装备下标
        Inventory.RemoveItem(game.Player, index);
        if (game.Player.Hp <= before) return;
        if (tally is not null)
        {
            tally.HealUsed++;
            tally.HealFromMap++;
        }
    }

    /// <summary>
    /// 在地图上买一瓶药带走：血掉到七成以下、背包里没药时，站在店里不买才是不会玩。
    /// 只买一瓶、只买得起的、只在本层商店真的还在时买。
    /// </summary>
    private static void BuyPotionHere(Game game, Tally? tally = null)
    {
        for (var i = 0; i < game.Map.Rooms.Count; i++)
        {
            if (game.Map.Rooms[i].Type != RoomType.Shop) continue;
            // 用 `Where(...).ToList()` 再取首项，不用 `FirstOrDefault`：
            // `ShopStockItem.Item` 是 required，默认值那一份不是 null 而是一个空壳，
            // 判 `entry.Item is null` 在它身上永远不成立——货架上没有回血药的那家店
            // 会走进下面那一段，然后空引用崩掉整局探针（实测）。
            var onSale = game.ShopStock(i)
                .Where(s => !s.Sold && s.Item.Heal is > 0 && s.Price <= game.Player.Gold).ToList();
            if (onSale.Count == 0) continue;
            var entry = onSale[0];
            // 腾格用的是**同一个账本**：`new Tally()` 会把为这一瓶药卖掉的货记进一个
            // 马上就丢掉的账里，而报表上的「卖旧装备 N 件、到手多少钱」永远少这一笔。
            if (game.Player.Inventory.Count >= game.Capacity && !SellSomething(game, tally ?? new Tally())) continue;
            game.SpendGold(entry.Price);
            var bought = game.RegisterRuntimeItemCopy(entry.Item);
            Inventory.AddItem(game.Player, bought.Id, game.Capacity);
            entry.Sold = true;
            if (tally is not null)
            {
                tally.PotionBought++;
                tally.HealGold += entry.Price;
            }
            return;
        }
    }

    /// <summary>
    /// 「这一局还要进哪一间房」。以前是「最近的没清空的房」——那不是一个决定，是地毯式扫图：
    /// 一层平均 3.2 场仗，挡在「起点 → Boss」这条路上的只有 1.1 场，
    /// 而探针把 3.2 场全打了，于是「平均死在第 N 层」一直是「把所有可选的仗都硬啃一遍」的数。
    ///
    /// 现在分档，顺序就是玩家的读法：
    ///   1. 补给房（商店 / 训练 / 祭坛）——本层唯一能把血与装备换回来的地方。
    ///      它排在战斗前面不是为了「先逛再打」，是因为这些房**不花血量**，
    ///      先走完它们，接下来每一场仗的血线才是玩家真正面对的血线；
    ///   2. 挡在去 Boss 路上的那一间：躲不过，必须打；
    ///   3. 宝箱 / 事件 / 诅咒这类「不花血量、只有好处」的房；
    ///   4. 路外的敌人房与精英房：血够才去。血低于四成还去啃可选的仗，
    ///      那不是勇敢，那是不会玩——一个人不会这么打。
    /// </summary>
    private static bool WalkToNextRoom(Game game, Tally tally)
    {
        var toBoss = game.BattlesToBoss();
        var hpPct = game.Stats.MaxHp <= 0 ? 1.0 : (double)game.Player.Hp / game.Stats.MaxHp;
        // 「这一间在不在去 Boss 的最短路上」：从起点与从 Boss 各算一次距离，
        // 两者相加正好等于「起点 → Boss」的距离，它才压在那条线上。
        //
        // 踩过的两个坑（两种写法，同一个错）：先是 `BattlesToBoss() > 0 && 可达`，
        // 后是只剩「可达」——一张连通图里每个点都走得到 Boss，于是**整张图都是必经之路**。
        // 两种错都让「必经」这两个字失去信息量，而报表上只留一个 0。
        var distToBoss = OnBossPathDistances(game);
        var bossIdxSafe = Math.Clamp(game.Map.BossIndex, 0, Math.Max(0, game.Map.Links.Count - 1));
        var distFromStart = MapGen.BfsDistances(game.Map.Links, game.Map.StartIndex);
        var bossDepth = distFromStart.Length > bossIdxSafe ? distFromStart[bossIdxSafe] : -1;
        bool IsTheOnlyPath(int index) => distToBoss is not null && index >= 0 && index < distToBoss.Length
                                         && distToBoss[index] >= 0 && index < distFromStart.Length
                                         && distFromStart[index] >= 0 && distFromStart[index] + distToBoss[index] == bossDepth;
        var onBossPath = NextBattleOnBossPath(game);
        var steps = new List<(Func<Room, bool> Wants, bool CountOptional)>
        {
            (r => !r.Cleared && r.Type is RoomType.Shop or RoomType.Training or RoomType.Altar, false),
            // 第 2 档只收「挡在去 Boss 那条最短路上」的战斗房。
            // 这一条判据以前**不存在**：`Wants` 里写的是「任何一间没打完的战斗房」，
            // 于是第 2 档把整张图的仗全吃了——第 3、4 档永远轮不到，
            // 而这正是横扫里「可选打了 0 场 · 血不够而放过 0 场」的来源。
            // 探针不是在取舍，它是把地图从近到远扫了一遍，每一场都算必经的。
            (r => !r.Cleared && (r.Type is RoomType.Enemy or RoomType.Elite or RoomType.Boss) && IsTheOnlyPath(game.Map.Rooms.IndexOf(r)), false),
            (r => !r.Cleared && r.Type is RoomType.Treasure or RoomType.Event or RoomType.Curse, false),
            (r => !r.Cleared && r.Type is RoomType.Enemy or RoomType.Elite, true),
        };
        // 可选那一档可以被 `--optional=off` 关掉（对照用）。关掉之后剩下的三档
        // 全都不含「路外的仗」，于是这一层只打必经的那几场。
        if (!FightOptional) steps.RemoveAt(steps.Count - 1);
        for (var stepIdx = 0; stepIdx < steps.Count; stepIdx++)
        {
            var (wants, countOptional) = steps[stepIdx];
            for (var i = 0; i < game.Map.Rooms.Count; i++)
            {
                var room = game.Map.Rooms[i];
                if (i == game.PlayerIndex || room.Type == RoomType.Start) continue;
                if (!wants(room)) continue;
                // 「这一间挡不挡在去 Boss 的最短路上」是**按房间**判的，不是
                // 「它是不是那条路上下一间该打的仗」。
                //
                // 这两个问题以前被写成了同一个（`i == onBossPath`），而它是错的：
                // `NextBattleOnBossPath` 返回的是**最近的那一间**，于是第 2 档把
                // 「路外」的战斗房当成必经的排到了前面——第 3 档的宝箱、商店
                // 全排在它们后面，而第 4 档永远轮不到（它们在前一档就被打了）。
                // 横扫里的现场：`可选打了 0 场`、`血不够而放过 0 场`，
                // 而 100 局里地图上有两千多间路外的仗。**探针把每一场都当成必经的。**
                var isBossPath = IsTheOnlyPath(i);
                // 第 4 档只收「路外」的仗：挡在路上的那几场已在第 2 档处理。
                if (countOptional && isBossPath) continue;
                // 门槛是「**这一间的净收益**」，不是「我还剩多少血」。
                //
                // 以前只写 `hpPct < 0.4`：一个 399/400 血的玩家会去打一间净掉 200 血的精英房，
                // 而一个 160/400 血的玩家连一间净掉 10 血的普通房都不敢进——判据读的是
                // 「血的比例」，而决定这件事的是「这一间要付多少」。
                // 横扫里 seed 7 就是这么死的：第 13 层带着满血进一间精英房，
                // 那一间净掉 49.6% 上限，出来就没了。
                //
                // 现在问两件事：这一间大概要掉多少血（按本层同类房的实测中位），
                // 以及打完还剩不剩得下三成。两个数任一不够，就放过这一间。
                var expected = ExpectedHpCost(game, room.Type, tally, out var fromSamples);
                // 「绕过」与「血不够而放过」是两件事，必须分开记：
                // 以前第 4 档被 `--optional=off` 整档拿掉之后，「放过 0 场」读起来像
                // 「一场都没敢打」，而真相是「根本没走到那一步」——一个恒为 0 的计数器
                // 和一条读空的统计量是同一类东西。绕过的那一档记在 `OptionalBypassed`。
                // 位置在这里而不是门口：**第 4 档真的被走到的**那一次才算。
                // 以前记在门口，于是第 2 档就会先撞上所有路外的战斗房，
                // `--optional=off` 前后两种口径的差被算进了同一个数里。
                if (countOptional) tally.OptionalSeen += 1;
                if (countOptional) tally.FightOptionalSeen += 1;
                if (countOptional && (hpPct < 0.4 || game.Player.Hp - expected < game.Stats.MaxHp * 0.3))
                {
                    tally.SkippedOptionalBattles += 1;
                    continue;
                }
                // 「必经之路」那一档没有取舍余地，但它仍然能被这一条挡下来：
                // 一个手上只剩一成血的人，下楼只会死在下一间里——
                // 而**这一层已经没有别的路可走了**（挡在 Boss 前面的那一间必须打）。
                // 所以这里不跳过，只把「这一步很贵」记进报表，让「死在哪一层」
                // 背后有一句话解释。跳过它等于让探针站在地图上发呆到 200 回合。
                if (!WalkTo(game, i, tally)) continue;
                if (countOptional) tally.OptionalBattlesTaken += 1;
                else if (isBossPath && toBoss > 0) tally.BossPathBattlesTaken += 1;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 这一间战斗房「大概要吃多少血」：按已经打过的同类房「掉血占当时上限的比例」取中位，
    /// 再乘回当前上限。没有样本时按上限的三成估。
    ///
    /// 为什么不读敌人面板：伤害要经过防御、浮动、暴击、楼层机制、技能倍率五六道修正，
    /// 在探针里再抄一遍就是第二份规则——同一份规则抄两遍，改一处就一定漏一处。
    /// 实测样本是免费的：这个探针本来就在记每一间的净掉血。
    /// 按**比例**而不是绝对值：面板在涨，第 3 层那 40 点血到第 13 层不再是同一件事。
    /// </summary>
    private static int ExpectedHpCost(Game game, RoomType type, Tally tally, out bool fromSamples)
    {
        var sample = tally.HpCostRatioByRoom.TryGetValue(type, out var list) && list.Count > 0 ? list : null;
        fromSamples = sample is not null;
        if (sample is null) return (int)Math.Round(game.Stats.MaxHp * 0.3);
        var sorted = sample.OrderBy(v => v).ToList();
        return (int)Math.Round(game.Stats.MaxHp * Math.Clamp(sorted[sorted.Count / 2], 0.02, 0.8));
    }

    /// <summary>
    /// 这一间在不在「脚下 → Boss」的最短路上（不要求它还没打）。
    /// 与 `Game.BattlesToBoss()` 读同一张 BFS 距离：那个数数的是这条线上还剩几场没打的，
    /// 这里回答的是「某间房属不属于这条线」。
    /// </summary>
    private static bool OnBossPathToBoss(Game game, int index)
    {
        var links = game.Map.Links;
        var boss = game.Map.BossIndex;
        if (boss < 0 || boss >= links.Count) return false;
        var dist = MapGen.BfsDistances(links, index);
        return dist[boss] >= 0;
    }

    /// <summary>
    /// 「每间房离 Boss 有多远」，-1 = 走不到。判「这一间在不在必经之路上」读它。
    /// 只算一次：`WalkToNextRoom` 里每一间房都算一遍 BFS，一层十几间房就是十几趟图搜索。
    /// </summary>
    private static int[]? OnBossPathDistances(Game game)
    {
        var links = game.Map.Links;
        var boss = game.Map.BossIndex;
        if (boss < 0 || boss >= links.Count) return null;
        return MapGen.BfsDistances(links, boss);
    }


    /// <summary>从脚下到 Boss，下一间要找它打的战斗房（不在路上则为 -1）</summary>
    private static int NextBattleOnBossPath(Game game)
    {
        var links = game.Map.Links;
        var boss = game.Map.BossIndex;
        if (boss < 0 || boss >= links.Count) return -1;
        var dist = MapGen.BfsDistances(links, game.PlayerIndex);
        if (dist[boss] < 0) return -1;
        for (var i = 0; i < links.Count; i++)
        {
            if (i == boss || dist[i] != dist[boss] - 1) continue;
            var room = game.Map.Rooms[i];
            if (room.Cleared) continue;
            if (room.Type is RoomType.Enemy or RoomType.Elite or RoomType.Boss) return i;
        }
        return -1;
    }

    /// <summary>按 Move 走到指定的房间。走一步算一步，不瞬移。</summary>
    private static bool WalkTo(Game game, int goal, Tally? tally = null)
    {
        var map = game.Map;
        var start = game.PlayerIndex;
        var prev = new Dictionary<int, int>();
        var queue = new Queue<int>();
        queue.Enqueue(start);
        prev[start] = -1;
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (cur == goal) break;
            foreach (var next in map.Links[cur])
            {
                if (prev.ContainsKey(next)) continue;
                prev[next] = cur;
                queue.Enqueue(next);
            }
        }
        if (!prev.ContainsKey(goal)) return false;
        var path = new List<int>();
        for (var at = goal; at != start; at = prev[at]) path.Add(at);
        path.Reverse();
        foreach (var step in path)
        {
            var from = map.Rooms[game.PlayerIndex];
            var to = map.Rooms[step];
            if (tally is not null) tally.WalkStepsOnFloor += 1;
            TopUpOnMap(game, tally);
            var (moved, _, _) = game.Move(to.X - from.X, to.Y - from.Y);
            if (!moved) return false;
        }
        return true;
    }

    private static void PlayRoom(Game game, Action<bool, string> check, Tally tally)
    {
        var room = game.CurrentRoom;
        switch (room.Type)
        {
            case RoomType.Enemy:
            case RoomType.Elite:
            case RoomType.Boss:
            {
                var enc = game.CreateEncounter();
                game.ClearRoom();
                if (enc is null) return;
                tally.LastFightEntryHpPct = game.Stats.MaxHp <= 0 ? 1 : (double)game.Player.Hp / game.Stats.MaxHp;
                var battle = new Battle(game.Player, enc.Monster, game.Rng, Options(game, enc));
                // 进战斗时的面板与「手上有没有战术道具」：这两行是这一轮量难度的前提。
                tally.PanelByFloor.Add((game.Floor, game.Stats.Attack,
                    game.Player.Skills.Where(x => x.Type is SkillType.Damage or SkillType.Drain or SkillType.Poison)
                                      .Select(x => (int)Math.Round(x.Power * 100)).DefaultIfEmpty(0).Max()));
                tally.SupportCarriedWorst = Math.Max(tally.SupportCarriedWorst,
                    Inventory.Consumables(game.Player, game.ItemIndex).Count(c => c.Def.Heal is not > 0 || c.Def.Cleanse == true));
                var rounds = 0;
                while (battle.Phase == BattlePhase.Player && rounds++ < 200)
                {
                    // 出手顺序是一个玩家真的会做的决定，而这里以前只做三件事：
                    // 喝药、放治愈、普攻。于是零冷却的「火球」与升级学到的
                    // 掷骨 / 毒液喷吐 / 毒爆**一次都没被放过**——模拟出来的平衡
                    // 是「一个只会平砍的人」的平衡，不是这款游戏的平衡。
                    //
                    // 现在的顺序：
                    //  1) 血低于四成：先喝药（带着药进棺材是纯浪费）；
                    //  2) 血低于一半且治愈好了：放治愈；
                    //  3) 能放伤害技能就放（倍率 > 攻击力那一刀），否则普攻。
                    var gap = game.Stats.MaxHp - game.Player.Hp;
                    var cure = Inventory.Consumables(game.Player, game.ItemIndex)
                        .Where(c => c.Def.Heal is > 0)
                        .OrderBy(c => c.Def.Heal >= gap ? 0 : 1)
                        // 补得上的两瓶之间取**更大的那一瓶**。
                        //
                        // 以前这里按 Heal 升序：两瓶都补得上时先喝小药（30），
                        // 把强效（70）留到「以后」。而「以后」在一局里只有十三层，
                        // 结果是玩家死在满背包的强效药上（seed 7 第 13 层：0 瓶药、
                        // 三个装备槽全顶配）。补得上就喝大的——大药的价值是「这一回合活下来」，
                        // 不是「以后省着用」。
                        .ThenByDescending(c => c.Def.Heal)
                        .FirstOrDefault();
                    // 药是不是「该现在喝」：以前固定在四成血，那是按上一版的伤害量定的。
                    // 泉眼把钱换成面板之后，怪物的血量成长（每层 +5%）会被一路甩开——
                    // 横扫 30 局全部通关（死在第 0 层），死因表一片空白。
                    // 所以这里改成**按这一刀的实际威胁**算：敌人这一回合能打掉的最多血量
                    // 已经接近剩下的血，就该喝，而不是等那条固定的四成。
                    var forecast = battle.EnemyForecast();
                    var threat = forecast.High;
                    // 中毒的回合要算上毒那一截，而且要比别的伤害**更保守**：
                    // 毒是回合收尾才扣、已经挂在身上、下一回合还在——它不是「预测」，
                    // 是账上已经欠着的钱。只按「下一刀」判的话，血 39 / 毒 39 / 下一刀 34
                    // 这一屏会被判成「不危险」，而药就在背包里没喝（上一轮死在毒手里的三局之一）。
                    // 这一回合**一共**会掉多少血：下一刀 + 已经挂在身上的毒（回合收尾再扣一次）。
                    // 以前这里是 `threat + extra * 2`：毒那一截确实是已经欠着的钱，
                    // 但它一回合只扣一次，乘 2 是拿三个回合的账压一个回合。
                    // 副作用比数字难看——血 56 / 毒 17 / 下一刀 34 被算成「要 85 点，危险」，
                    // 而这一屏最多掉 51 点，人还站着。于是探针在**不会死**的回合里狂喝药，
                    // 真正会死的那一回合手上倒是一瓶都没有（横扫 100 局：喝掉 413 瓶，
                    // 而 5 次「毒+下一刀」要命的回合里，5 次背包里没有药）。
                    // 毒这一截要算，但**算真的会掉的那部分**：预算是规则的一部分
                    // （`Battle.PoisonWouldKillPlayer` 问的就是它）。不过这一回合毒会把血
                    // 压到 1 点、下一刀再收走，和「毒直接带走」只差一回合——
                    // 所以撑得住的那一下也算进来，本层额度用完的那一下只算到「剩 1 点」。
                    // 「这一回合一共会掉多少」现在问产品自己的入口（`PoisonHpThisTurn`），
                    // 探针不再复述那条预算规则。自爆那一笔也在这里：它同样是
                    // 「已经决定要发生」的账——这一刀打死的那只怪，回合收尾就会炸。
                    var poisonReal = battle.PoisonHpThisTurn();
                    var (boomNow, boomCount, _) = battle.ExplodeOnDeathForecast();
                    tally.PoisonInDangerSamples += poisonReal > 0 ? 1 : 0;
                    if (poisonReal > 0)
                        tally.PoisonTurnsByFloor[game.Floor] = (tally.PoisonTurnsByFloor.TryGetValue(game.Floor, out var seenAt) ? seenAt : 0) + 1;
                    if (boomCount > 0) tally.BombExplodeRiskSamples++;
                    var threatThisTurn = threat + poisonReal + boomNow;
                    // 「这一回合会不会把我送走」与「这一回合疼不疼」是两个问题，
                    // 以前挤在同一个 `+8` 里。现在分开：
                    //   · **会死**（这一回合的账 ≥ 血）：一定亏不得这一手，先解药再防御；
                    //   · **疼**（掉完不足一成半）：喝药是划算的，但不必花掉解药那一手。
                    // 只按后者算的时候，「疼」那一档会把「会死」的回合挤到后面去——
                    // 横扫里 6 次「毒就是这一回合的最后一根稻草」全都落在这一格里。
                    var hpNow = game.Player.Hp;
                    var willDie = hpNow <= threatThisTurn;
                    tally.TurnInDangerSamples += willDie ? 1 : 0;
                    var inDanger = hpNow <= threatThisTurn + 8 || hpNow < game.Stats.MaxHp * 0.4;
                    if (willDie)
                    {
                        // 会死的回合：解药排在喝药前面（它便宜、而且毒那一截药补不回来）。
                        if (TryUseConsumable(game, battle, tally)) continue;
                    }
                    // 「算错」就落在这里：判据说血够、于是这一回合该喝的那瓶药没喝。
                    // 只是**这一回合**没喝——下一回合重新掷，所以误判不会让一个人从第 3 层
                    // 一路错到第 15 层，它错的是「某几个回合」。这正好是一个人会犯的那类错。
                    //
                    // 错法分三档（`--mistake-kind`）：
                    //   · light：漏判「这一刀疼」，血其实还够——最像真人；
                    //   · bad  ：漏判「这一回合就要死」——那一次没有替补；
                    //   · ugly ：bad 之外还连道具那一档也空过去（见 TryUseConsumable 里的 slipped）。
                    // 三档一起跑才看得出「保险」这个说法站不站得住：只有 bad 才需要兜底。
                    //
                    // 这一段以前在合流里整块丢了：参数读得进来、报表也照样打一行「空过去 N 手」，
                    // 而 N 永远是 0——因为 `Slips()` 只在道具那一支被调用过。
                    //
                    if (inDanger && Slips())
                    {
                        inDanger = false;
                        tally.SlippedNotTaken++;
                    }
                    // 手上没有药、这一回合也买不到：能救命的只剩「道具 / 防御 / 技能」这三手。
                    // 以前这三手排在「有药就喝」**之后**，于是「没药且危险」那一屏直接落到
                    // 「防御」或「平砍」——解毒草躺在背包里，判据点名说该吃，而这一回合
                    // 根本走不到那一步（横扫 100 局：2 次「该吃」的局面，2 次都没吃）。
                    // 解毒草和「喝药」抢的是同一个回合，但它便宜得多（8 对 8~16），
                    // 而且它救的是**这一回合**的毒。所以它排在喝药前面：
                    // 只有「带在身上的草能吃」时它才会抢先，否则这一支什么也不做。
                    //
                    // 「手上有没有那一株」才是这一支的门槛：上一版这里写的是
                    // `if (PoisonTurns > 0 && TryUseConsumable(...)) continue;` ——
                    // 于是**只要中了毒**就把这个回合交给道具那一段，哪怕背包里
                    // 只有一颗炸弹、根本没人打算吃解药。横扫里那 83 株草、2 次吃下，
                    // 差的就是这一句：判据不是「没到」，是这一步先把回合花掉了。
                    if (game.Player.PoisonTurns > 0 && CarriesCleanse(game) && TryUseConsumable(game, battle, tally)) continue;
                    // 3″) 先看「这一回合能不能把它按死」——**排在喝药之前**。
                    //
                    // 这一条是这一轮量出来的：探针以前遇到危险（血 ≤ 这一刀 + 8）就喝药，
                    // 而「危险」这一档里有一大半是「我这一下就能打死它，它再也打不到我」。
                    // 于是它把药喝在一个根本不会挨刀的回合上——100 局喝掉 652 瓶，
                    // 而同一批种子的通关率从 72/100 掉到 60/100。
                    //
                    // 药补得回来，但补得不多：一瓶 30~70 点，而这一刀常常是 40 以上；
                    // 「先打断它」比「先补血」便宜——技能不花钱，炸弹 14 金币。
                    // 只有真的收不掉这一只时，喝药才是那一手。
                    var finisher = TryFinishNow(game, battle, tally);
                    if (finisher) continue;
                    var noOut = inDanger && cure.Def is null;
                    if (noOut && TryUseConsumable(game, battle, tally)) continue;
                    if (inDanger && cure.Def is not null)
                    {
                        var invBefore = game.Player.Inventory.Count;
                        battle.PlayerHeal(cure.Def.Heal!.Value, cure.Def.Name);
                        Inventory.RemoveItem(game.Player, cure.Index);
                        tally.HealUsed++;
                        check(game.Player.Inventory.Count == invBefore - 1, "战斗里喝掉的药没有从背包里减少");
                        continue;
                    }
                    // 3′) 战术道具：背包里常年躺着的炸弹与解毒草，以前一次都没被用过——
                    // 于是一个「手里有 25 点无视防御」的玩家被当成只会平砍的玩家来衡量，
                    // 难度表量出来的也不是这款游戏的难度。判据刻意朴素，和一个会看背包的人一致：
                    //   · 中毒、且这一回合的毒正好要命（血 ≤ 毒 + 下一刀的上限）→ 解毒草；
                    //   · 敌人这一刀能带走一大块血、而炸弹能把它打断 → 炸弹。
                    if (TryUseConsumable(game, battle, tally)) continue;
                    // 4) 血撑得住、但这一刀要打很久：防御一次。
                    //    以前探针只会平砍，于是「防御减半」这条规则在 30 局里一次都没用过——
                    //    一个从不防御的人打出来的平衡，不是这游戏的平衡。
                    //    判据是「硬吃着把这一刀吃下去也死不了，而且下一刀会很贵」：
                    //    敌人这一刀的上限超过当前血量的三分之一，而防御能把它砍到一半以下。
                    if (PlayerShouldDefend(game, battle)) { battle.PlayerDefend(); continue; }
                    // 5) 能放伤害技能就放，否则平砍。
                    if (TryAttackSkill(game, battle)) continue;
                    battle.PlayerAttack();
                    // 上面那一整段是「这一回合怎么打」。它以前是这个循环的全部——
                    // 打得过就打、打不过就死，中间没有「走」这个动作。
                    // 于是横扫里「血不够而放过可选的仗」永远是 0 次，不是玩家不敢走，
                    // 是**走进来之后再也没有出口**。退路一直有（`Battle.Escape`），
                    // 只是没人按过它：地图上 63% 的仗是可选的，而一个不会撤退的人
                    // 只能把每一场都硬啃完。
                    if (ShouldFlee(game, battle, tally)) break;
                }
                DumpFleeReason(game, battle, tally);
                var fled = battle.Phase == BattlePhase.Escaped;
                var gaveUp = !fled && battle.Phase != BattlePhase.Won;
                if (fled)
                {
                    // 撤离：房间不会因此清空（这是产品的规则），所以这里也不 `ClearRoom`——
                    // 探针调的就是玩家按 Esc 时走的那一条路，代价（5 点生命）由它自己收。
                    //
                    // 「这一次撤退值不值」要有分母才有意义：只报「撤了几次」，
                    // 读不出它救过人还是白送过命。两个数分开记：
                    //   · 撤完之后这一局还活着 → 这一走救了一次；
                    //   · 撤完就走了 → 那是一次「走晚了」，它比死在里面更值得记。
                    tally.FledBattles++;
                    if (game.Player.Hp <= game.Stats.MaxHp * 0.4) tally.FledWhileLowHp++;
                }
                var won = battle.Phase == BattlePhase.Won;
                // 留住现场：死因与「死在哪类房」都从这几行里读
                tally.LastDeathRoom = room.Type;
                // 留**尾巴**而不是头部：死因在最后一刀上，头部那几行是「你遭遇了谁」。
                // 合成一行之后 `CausesOfDeath` 要按行倒着读，所以行与行之间仍然得是换行。
                tally.LastDeathLog = string.Join("\n", battle.Log.TakeLast(24));
                // 从日志里挑「最重的那一笔」：死因表要的不是一段现场，是一个答案。
                //
                // **只认敌人打玩家的那几句**。玩家自己的输出写在同一个日志里
                //（「使用「火球」，造成 128 点伤害」），不筛的话「最重的一下」
                // 永远是玩家自己打的，死因表会写着「你被自己的火球打死了」。
                foreach (var line in battle.Log.TakeLast(10))
                {
                    if (OwnLine(line)) continue;
                    var m = System.Text.RegularExpressions.Regex.Match(line, "造成 (\\d+) 点伤害|失去 (\\d+) 点生命");
                    if (!m.Success) continue;
                    var dmg = int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
                    if (dmg > tally.DeathBlow.Damage) tally.DeathBlow = (dmg, line.Trim());
                }
                // 完整一份留档：只要 `--probe-dump` 打开就逐行打出来。
                // 上一轮的死因表少记了一局，`TakeLast(6)` 那六行里不带招式名，谁也不知道
                // 死者是被什么带走的——判别据之前得先能看见现场。
                if (DumpBattles) foreach (var line in battle.Log) GD.Print($"[battle] {line}");
                battle.Dispose();
                // 输掉的判据读 `gaveUp`，不读 `!won`：撤离也不是胜利，但它不是输。
                // 以前这里写的是 `if (!won)`——一旦真有人按了 Esc，这一局会以
                // 「打输了」收尾（`OnBattleLost`），也就是**撤退等于阵亡**。
                if (gaveUp)
                {
                    game.OnBattleLost();
                    return;
                }
                if (fled) break;
                game.OnBattleWon(enc.Def, room.Type == RoomType.Boss, enc.GoldMultiplier, enc.XpMultiplier, 1);
                var rewards = game.Rewards(enc.Tier);
                var reward = ChooseReward(game, rewards);
                switch (reward)
                {
                    case Reward.Item item:
                    {
                        var added = Inventory.AddItem(game.Player, item.Def.Id, game.Capacity);
                        if (!added)
                        {
                            // 背包满：走的是界面上的「换掉它」
                            var drop = FindWorstIndex(game);
                            if (drop >= 0) Inventory.SwapInItem(game.Player, drop, item.Def.Id);
                        }
                        check(game.ItemIndex.ContainsKey(item.Def.Id), $"收下的「{item.Def.Name}」查不到模板");
                        break;
                    }
                    case Reward.Gold gold:
                        game.EarnGold(gold.Amount);
                        break;
                            case Reward.Heal h:
                    {
                        var before = game.Player.Hp;
                        game.HealPlayer(h.Amount);
                        tally.HealFromReward += game.Player.Hp - before;
                        break;
                    }
                }
                break;
            }
            case RoomType.Treasure:
            {
                // 宝箱：开箱 → 拿走唯一那张卡
                game.ClearRoom();
                // 开箱也是三选一：卡是 game.Rewards() 滚出来的三张，玩家站在箱子前一样要挑。
                // 以前探针永远拿 [0]，而 [0] 有可能正好是「回血 0 点」或一瓶血满时没用的药——
                // 于是「背包满 + 开出一瓶药」这种局面被记成了「箱子里的东西到手了」。
                // 挑法复用 ChooseReward：和战后三选一是同一个人做的同一类决定。
                var treasureChoices = game.Rewards();
                var reward = ChooseReward(game, treasureChoices);
                if (reward is Reward.Item item)
                {
                    var added = Inventory.AddItem(game.Player, item.Def.Id, game.Capacity);
                    if (!added)
                    {
                        // 背包满：界面上会弹「放下什么换它」。真人这会儿会跟自己比一下：
                        // 新的更好就换，不如就放手——以前探针无条件换，于是它把一件
                        // 有用的药换成了「精钢剑」，还记成「箱子里的东西到手了」。
                        var drop = FindWorstIndex(game);
                        var better = drop >= 0 && Score(item.Def) > Score(game.ItemIndex[game.Player.Inventory[drop]]);
                        if (better) Inventory.SwapInItem(game.Player, drop, item.Def.Id);
                        else if (drop < 0) check(false, "背包满了却找不到可以换掉的那件，箱子里的东西只能丢掉");
                    }
                    check(game.ItemIndex.ContainsKey(item.Def.Id), "宝箱开出来的东西查不到");
                }
                else if (reward is Reward.Gold g) game.EarnGold(g.Amount);
                else if (reward is Reward.Heal h)
                {
                    var before = game.Player.Hp;
                    game.HealPlayer(h.Amount);
                    tally.HealFromReward += game.Player.Hp - before;
                }
                break;
            }
            case RoomType.Altar:
            {
                game.ClearRoom();
                // 泉眼：身上另有 300 金币余量就投，先算清楚「这一笔花完还剩多少」——
                // 把最后一枚铜板扔进池子里、然后在一场精英房里渴死，那不是理性玩家。
                // 上限与攻击的**规则**不在这里复述：探针只调 Game 的方法，读它给的数。
                // 只掷一次：`RollAltar` 每次调用都会从 Rng 抽一次，
                // 掷两遍等于「面板上写的是泉眼，结算走的却是另一座」——
                // 而探针要复述的正是「玩家看到的那座祭坛」。
                var altar = game.RollAltar();
                if (altar.Kind == AltarKind.Offering)
                {
                    var steps = 0;
                    while (game.CanOffer && game.Player.Gold - game.OfferingCost() >= OfferingReserve(game) && steps++ < 8)
                    {
                        var maxBefore = game.Stats.MaxHp;
                        var atkBefore = game.Player.BaseAttack;
                        var cost = game.OfferingCost();
                        var (ok, spent, hp, atk) = game.TakeOffering();
                        check(ok, $"泉眼说能换（第 {game.OfferingSteps} 次），结算却拒绝了");
                        if (!ok) break;
                        check(spent == cost, $"泉眼按钮写着 {cost} 金币，实际花了 {spent}");
                        check(game.Stats.MaxHp >= maxBefore + hp, "献金之后生命上限没有涨");
                        check(game.Player.BaseAttack >= atkBefore + atk, "献金之后攻击力没有涨");
                        check(game.Player.Gold < 0 == false, "献金把钱包算成负数了");
                        tally.OfferingSteps++;
                    }
                    break;
                }
                // 祭坛：没中毒就血祭（和理性玩家一致），代价按面板上限算
                if (game.Player.PoisonTurns > 0) EntityRules.ClearPoison(game.Player);
                else
                {
                    // 探针读的是**按钮上那个数**：`SacrificeCost()` 就是界面与结算共用的那一份。
                    // 以前这里自己复述了一遍旧公式（当前上限的 8%），血祭改成固定代价之后
                    // 它立刻开始误报「扣的上限超过了说明写的 8%」——把一条正确的结算报成了坏局。
                    var quoted = game.SacrificeCost();
                    var lost = game.TakeSacrifice();
                    check(lost == quoted, $"血祭按钮写着 -{quoted} 生命上限，实际扣了 {lost}");
                    check(game.Player.Hp <= game.Stats.MaxHp, "血祭之后生命超过了上限");
                }
                break;
            }
            case RoomType.Training:
            {
                game.ClearRoom();
                // 训练房：血少就地休整，否则苦练
                // 训练房先看选项：血不满时回血才是真收益，满血时苦练
                var options = game.TrainingOptions().Select(o => o.Id).ToList();
                var pick = game.Player.Hp < game.Stats.MaxHp * 0.6 && options.Contains("heal") ? "heal"
                    : options.Contains("attack") ? "attack"
                    : options.FirstOrDefault() ?? "heal";
                var hpBefore = game.Player.Hp;
                var buffBefore = game.TrainingAttackBuff;
                var text = game.TakeTraining(pick);
                if (pick == "heal") check(game.Player.Hp > hpBefore || hpBefore == game.Stats.MaxHp, $"就地休整没有回血（{text}）");
                else check(game.TrainingAttackBuff > buffBefore, $"苦练招式之后攻击加成没变（{text}）");
                break;
            }
            case RoomType.Event:
            {
                game.ClearRoom();
                var ev = game.RandomEvent();
                if (ev.Choices.Count == 0) { check(false, "事件一个选项都没有"); break; }
                // 选项要真的挑得动：付得起的钱、交得出的道具。
                // 以前只判金币，于是「需要一件道具」的选项在空手时也被选中——
                // 结果那条路什么都没发生，玩家还以为是运气问题。
                bool Pickable(EventChoice c) =>
                    (c.Cost is not > 0 || game.Player.Gold >= c.Cost)
                    && (c.RequiresItem != true || FindWorstIndex(game) >= 0);
                var choice = ev.Choices.FirstOrDefault(Pickable) ?? ev.Choices[0];
                if (choice.Cost is { } cost)
                {
                    if (game.Player.Gold < cost) break;
                    game.SpendGold(cost);
                }
                var outcome = GameEvents.ResolveChoice(game.Rng, choice.Outcomes);
                // 交换类事件在界面上是「先挑一件交出去，再换回一件」。
                // 探针以前直接 break —— 金币付了、结果文本也弹了，而东西没换上：
                // 那一局里玩家白花了一笔钱，还以为是「随机到手气不好」。
                var swapOut = -1;
                if (outcome.Effect.Type == EventEffectType.SwapItem)
                {
                    swapOut = FindWorstIndex(game);
                    check(swapOut >= 0, "交换事件要交出一件，而背包里没有可交的");
                    if (swapOut < 0) break;
                    if (Inventory.IsEquipped(game.Player, swapOut) is { } slot)
                        Inventory.Unequip(game.Player, slot, game.ItemIndex);
                }
                var result = GameEvents.ApplyEffect(game.Player, outcome.Effect, new EventApplyContext
                {
                    Rng = game.Rng,
                    Items = GameDataStore.Get().Items,
                    Capacity = game.Capacity,
                    ItemsById = game.ItemIndex,
                    PanelMaxHp = game.Stats.MaxHp,
                    SwapOutIndex = swapOut >= 0 ? swapOut : null,
                    PrepareItem = game.ApplyBonusAffix,
                    RegisterItem = game.RegisterRuntimeItemCopy,
                    // 钱只走 Game 那本账：事件里在 Player 上直接加减的那一版，
                    // 「本局金币获得」永远只数战斗赏金，探针量到的收入就是偏的。
                    AddGold = d => { if (d >= 0) game.EarnGold(d); else game.SpendGold(-d); },
                });
                if (result.GainedItemId is { } gainedId)
                    check(game.ItemIndex.ContainsKey(gainedId), $"事件给的「{result.Text}」查不到");
                check(game.Player.Inventory.Count <= game.Capacity, "事件把背包塞爆了");
                game.CollapseIfDead();
                break;
            }
            case RoomType.Shop:
            {
                game.ClearRoom();
                tally.ShopRooms++;
                VisitShop(game, check, tally);
                break;
            }
            default:
                game.ClearRoom();
                break;
        }
    }

    /// <summary>
    /// 战斗中用掉一件战术道具（炸弹 / 解毒草）。返回 true 表示这一回合已经用掉了。
    ///
    /// 这里以前完全没有：横扫 30 局里「炸弹 0 次、解毒草 0 次」，而背包里常年躺着它们。
    /// 「钱花得掉」这件事的可信度上限就被卡在这里——一局里最便宜的 25 点伤害
    /// （14 金币，无视防御）从来没被打出去过。
    ///
    /// 判据是「这一件能不能改变这一回合的结局」，不是「有没有」：
    ///   · 解毒草：这一回合的毒（含下一刀）能把人带走 → 解毒是活命，不是浪费一个回合；
    ///   · 炸弹：这 25 点能把敌人打死，或者这一刀的血量缺口大于一瓶药能补的 →
    ///     早点结束战斗，比多吃一刀划算。
    /// 两件事都读面板上的数：`EnemyForecast` / `LethalThisTurnExtra` / `PoisonPower`，
    /// 探针不复述任何一条规则。
    /// </summary>
    private static bool TryUseConsumable(Game game, Battle battle, Tally tally)
    {
        var items = Inventory.Consumables(game.Player, game.ItemIndex);
        var hp = game.Player.Hp;
        // 这一回合的毒**真的**会掉多少血：以前读的是「毒伤」那个数，
        // 而额度用完时毒只扣到剩 1 点——预报与判据各读一个数，就会在
        // 「预报说 6 点、实际掉 1 点」那种局面里给出一句自相矛盾的话。
        // 现在两边问同一个入口（`Battle.PoisonHpThisTurn`）。
        var poison = battle.PoisonHpThisTurn();
        // 「这一回合一共会掉多少」读产品自己的入口：下一刀 + 毒（结算口径）+ 倒下自爆。
        //
        // 这里以前只读 `EnemyForecast().High`（下一刀的上限），于是「毒 + 自爆」
        // 这种真正的死法它一次都看不见——而那是横扫里排第一的死因。
        // 三条收在 `ThisTurnTotal()` 一处，界面那一行、吃草的判据、喝药的判据读的都是它。
        var incoming = battle.ThisTurnTotal().High;
        // 分母按「中毒的回合」数。别用「剩余回合数变了就记一次」当判据——那个数在
        // 同一回合里会被改两次（毒爆先挂毒、回合末再递减），于是第二回合完全数不到，
        // 30 局的 17 个中毒回合被数成了 1 次「会要命」。判据得是**真的一回合**：
        // 这个探针每回合进来一次，所以每进一次就记一次（毒在就记）。
        // 报表的分母是「毒真的会带走我」，不是「毒的数字加下一刀够大」。
        // 那个数得问规则本身：扣得死要同时满足「血量 ≤ 毒伤」和「这一层的毒死预算还没用完」
        // （Game.PoisonDyingBudget，每 5 层多一次）。探针以前只比大小，
        // 于是分母里混进一堆「毒看着重、其实这一层已经免死」的回合。
        // 「毒这一下就够要命」= 毒够重 **且** 这一层的毒死额度还没用完。
        // 后半句以前读的是一个永远为真的数（预算那条判据在 `damage < Hp` 上就放行了），
        // 于是玩家身上挂着「毒 6 点 / 血 5 点」时，报表会判「毒不会要命」——
        // 而解毒草该吃的那一刻，正是这一屏。
        // 「毒够重」与「毒会不会杀人」是两个问题：
        //   · 够重 = 这一回合的毒 ≥ 当前血量（毒自己就能把人按倒，不用等下一刀）；
        //   · 会杀人 = 够重 **且** 这一层的额度还没用完（`PoisonWouldKillPlayer`）。
        // 报告里两个都报：只报后者时，「0 次」读不出到底是毒太轻，还是预算一直在兜。
        var poisonHeavy = poison > 0 && hp <= poison;
        var poisonWouldKill = battle.PoisonWouldKillPlayer();
        if (poison > 0)
        {
            tally.PoisonTurnsSeen++;
            // 分母问的是规则（毒够重 **且** 这一层的毒死预算还没用完），
            // 分子（「解毒草真能救回来」）在下面和「真的吃」读同一个表达式
            // （`cleanseRing`），不然「判据说该吃」和「实际吃了」会各自成立，
            // 而两条路一条都没走通时谁也看不出来。
            if (poisonHeavy) tally.PoisonHeavySeen++;
            if (poisonWouldKill)
            {
                tally.PoisonMateSeen++;
                if (Inventory.Consumables(game.Player, game.ItemIndex).All(c => c.Def.Cleanse != true))
                    tally.CleanseSeenButNoneCarried++;
            }
        }
        var probe = ItemMode;
        // 解毒草：这回合中了毒，而一株草（8 金币）能把这一回合的毒从账上抹掉。
        //
        // 这里以前和「喝药」共用同一个判据，而喝药那一段先跑——背包里有药时
        // **永远轮不到这一支**，解毒草只活在「手上刚好没药」的那几个局面里，
        // 100 局 0 次使用。判据本身也定得太死：要「毒 + 下一刀 ≥ 血」才肯吃，
        // 而那时往往已经没救了。查下来它的价与它救的命本来就不成比例
        // —— 一株 8 金币换回一整条命，一瓶 16 金币的药只补 70 点。
        //
        // 现在是两档：
        //   `cleanseRing`：这一回合就会死，草把它救回来（报表的分子读这一档，
        //      和下面「真的吃」读同一个表达式，两句判据不会各自成立）；
        //   `cleanseWorth`：这一回合的毒**咬得动我**、而血量会掉到不足一半
        //      ——草掐掉这一截，等于给这一回合留出一次挨刀的余地。
        // 两档都只读 `EnemyForecast` / `LethalThisTurnExtra` 与毒自己的读数，
        // 探针不复述任何一条规则。
        //
        // 合成之后这一档只当**分母**用（`CleanseWorthAndCarried` / `CleanseWorthButLethal`
        // 两个计数器）：真正吃不吃只看 `cleanseRing`。`main` 上这两档是合一的，
        // 合流时并回来——「该吃」与「真的吃」本来就该是两个数。
        //
        // 上一版这里读的是 `antidote.Def.Heal`：那株草当时还回 18 点血，
        // 判据顺手就把「补血 18」算进了「能不能站住」。而 40 轮之后它只剩「解毒」
        // ——判据要是还留着那个数，探针就会为一株什么都不回血的草算错一整个回合。
        var antidoteNow = items.FirstOrDefault(c => c.Def.Cleanse == true);
        // 这一回合毒**真的**会掉多少，已经由 `PoisonHpThisTurn` 算好了（额度、封顶都在里面）。
        // 两档判据都读这一个数，不是读「毒的数值」——规则改了、判据不该跟着改。
        var poisonReal = poison;
        // 吃草要花掉这一回合的出手，所以它救的必须是**这一回合的命**：
        // 判据只问一件事：**不吃这株草，这一回合就会被毒压垮吗**。
        //
        // 以前它多带一个 `hp > incoming`（「掐掉毒之后这一刀留得住人」），
        // 于是「毒 + 下一刀 = 要命、掐掉毒之后这一刀也还要命」的局面被判成「不必吃」——
        // 而那一手给草并不是错：它把两段死改成一段死，剩下的那一段还能用防御 / 药去挡
        // （防御减的是下一刀，毒不吃姿态）。横扫 100 局里这一档出现了 6 次，
        // 判据说「不值得」，草于是 0 株吃下——报表上写着「解毒草 0 株/ 买进 255 株」，
        // 前几轮那句「它是一个没有场的道具」被这一句判据又写了一遍。
        //
        // 合流之后 `incoming` 是**这一回合一共会掉多少**（下一刀 + 毒 + 自爆），
        // 而 `poisonReal` 已经含在里面了。所以这里要减掉毒那一截再比——
        // 不减的话 `hp <= 毒 + (下一刀 + 毒 + 自爆)` 会把每一回合都算成「要命」，
        // 又变回那个「80 次救回来、2 次会死」的虚数。
        var incomingNoPoison = Math.Max(0, incoming - poisonReal);
        //
        // 另一半也对齐了：`CleanseWouldSave` 以前缩进在 `if (poison > 0)` 里，
        // 看起来像在数「有毒的回合」，实际数的是 `cleanseRing` 自己（毒在不在
        // 早就写进 `cleanseRing` 了）。于是报表上出现过一句自相矛盾的话：
        // 「会要命 2 次（解毒草真能救回来 80 次）」——80 > 2。
        // 现在它只有一处入口，和「真的吃」读同一个表达式。
        var cleanseRing = poisonReal > 0 && hp <= poisonReal + incomingNoPoison;
        // 宽口径（只当分母）：「毒咬得动我」——不吃草，这一回合会被压掉一截。
        var cleanseWorth = cleanseRing || (poisonReal > 0 && hp - poisonReal < hp * 3 / 4);
        if (cleanseRing) tally.CleanseWouldSave++;
        if (poison > 0)
        {
            tally.PoisonTurnsWithAntidote += items.Any(c => c.Def.Cleanse == true) ? 1 : 0;
            if (items.Any(c => c.Def.Cleanse == true) && cleanseWorth) tally.CleanseWorthAndCarried++;
            // 「值得吃」与「吃了能活」以前是两个**不同口径**的表达式：
            // 前者读 `cleanseWorth`（毒咬得动我），后者读 `hp <= incoming`（下一刀要命）——
            // 于是报表上那句「值得吃的 6 次里，掐掉毒之后当场就会死的还有 6 次」
            // 读起来像「这株草一次都没用」，而它其实是「两句话在说同一件事：
            // 这 6 次都是『毒 + 下一刀』要命，草只解掉毒那一半」。
            // 现在第二句话改读真的会发生的事：掐掉毒之后**这一回合还会不会死**——
            // 那要看下一刀加上自爆，而不是只看下一刀。
            var (boomAfter, _, _) = battle.ExplodeOnDeathForecast();
            if (items.Any(c => c.Def.Cleanse == true) && cleanseWorth && hp <= incoming + boomAfter)
                tally.CleanseWorthButLethal++;
        }
        if (cleanseRing && antidoteNow.Def is null) tally.CleanseNeededNotCarried++;
        // 最后一道门槛：真的走到吃这一步时再问一次「这一回合的毒要不要命」。
        // `cleanseWorth` 的第二档（毒咬得动、但不会当场死）是**故意宽**的：
        // 它让「该吃的局面」有足够的分母。「吃了有没有用」则要单独量——
        // 探针只在这一回合的毒真的会把人带走时才吃：
        // 不带草时这一下会死（`cleanseRing`），带上草之后不会（`hp + poison > incoming`）。
        //
        // 少这一道，30 局里 83 株草买到手，只吃下 2 株——不是判据没到，
        // 是第二档把回合花在了不会死的毒上，而那个回合本来该砍一刀。
        // 「算错」：这一回合其实该吃，判据掷出误判就当成「不必吃」。
        // 这一手于是空过去——草还在背包里，损失在血条上。
        var slipped = Slips() && (Mistake >= 3 || cleanseWorth && hp > incoming);
        if (slipped) tally.SlippedNotTaken++;
        if (probe != "nocleanse" && cleanseRing && antidoteNow.Def is not null && !slipped)
        {
            battle.PlayerCleanse();
            Inventory.RemoveItem(game.Player, antidoteNow.Index);
            tally.CleanseUsed++;
            return true;
        }
        // 炸弹：优先「这一下就能收掉」，其次「这一刀很疼、我想早点结束」。
        var bomb = probe == "nobomb" ? default : items.FirstOrDefault(c => c.Def.Damage is > 0);
        if (bomb.Def?.Damage is { } dmg)
        {
            var target = battle.CurrentTarget();
            var finishes = dmg >= target.Hp;                       // 无视防御：按敌人当前血判
            var worth = incoming * 3 >= game.Stats.MaxHp && dmg * 3 >= target.Hp;
            if (finishes || worth)
            {
                battle.PlayerItemDamage(dmg, bomb.Def.Name);
                Inventory.RemoveItem(game.Player, bomb.Index);
                tally.ItemDamageUsed++;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 「这一回合能不能把它收掉」——收得掉就别喝药，先打断它。
    ///
    /// 判据只有一条：这一下打出去，**场上就再也没有活着的敌人**。组合战斗里
    /// 收掉一只不等于收掉这一场，所以问的是 `LivingEnemies().Count`，不是血量。
    /// 顺序刻意是「技能 → 炸弹 → 平砍」：技能不花钱，炸弹 14 金币，
    /// 平砍不要钱却是最轻的那一手。三条都只读面板与规则，探针不复述算式。
    ///
    /// 这一条以前不存在，代价在数字里：探针遇到「危险」就喝药，
    /// 而危险档里有一大半是「我这一下就能打死它」。药被喝在不会挨刀的回合上
    /// （100 局 652 瓶），通关率从 72 掉到 60。
    /// </summary>
    private static bool TryFinishNow(Game game, Battle battle, Tally tally)
    {
        var living = battle.LivingEnemies();
        if (living.Count != 1) return false;
        var target = battle.CurrentTarget();
        var strike = game.Stats.Attack * (1 + game.TrainingAttackPct);
        // 1) 平砍就够：这一刀的最低伤害（不算暴击、按最大浮动扣防御）已经超过它的血
        var variance = GameDataStore.Get().Config.DamageVariance;
        var minBlow = (int)Math.Round(strike * (1 - variance)) - target.BaseDefense - target.DefMod;
        if (minBlow >= target.Hp) { battle.PlayerAttack(); return true; }
        // 2) 技能：倍率高的那些，按同一套下限算
        foreach (var skill in game.Player.Skills.OrderByDescending(x => x.Power))
        {
            if (skill.Type is not (SkillType.Damage or SkillType.Drain or SkillType.Poison)) continue;
            if (!Skills.IsSkillReady(game.Player, skill.Id)) continue;
            // 技能那一档不减防御（Skills 里那一支就是这么写的），所以只算浮动下限
            var minSkill = (int)Math.Round(strike * skill.Power * (1 - variance));
            if (minSkill < target.Hp) continue;
            if (battle.PlayerSkill(skill.Id)) return true;
        }
        // 3) 炸弹：无视防御，按它的面板伤害判
        if (ItemMode != "nobomb")
        {
            var bomb = Inventory.Consumables(game.Player, game.ItemIndex).FirstOrDefault(c => c.Def.Damage is > 0);
            if (bomb.Def?.Damage is { } dmg && dmg >= target.Hp)
            {
                battle.PlayerItemDamage(dmg, bomb.Def.Name);
                Inventory.RemoveItem(game.Player, bomb.Index);
                tally.ItemDamageUsed++;
                return true;
            }
        }
        return false;
    }

    /// <summary>背包里有没有解毒草。`TryUseConsumable` 会顺手试炸弹，所以门槛得单独问一次。</summary>
    private static bool CarriesCleanse(Game game) =>
        Inventory.Consumables(game.Player, game.ItemIndex).Any(c => c.Def.Cleanse == true);

    /// <summary>
    /// 这一回合该不该防御：敌人这一刀的上限吃掉当前血量的三分之一以上，
    /// 而减半之后剩下的血还能撑住下一刀。写成一个函数是为了两句判据待在一起——
    /// 「该防」和「防了不会白防」是同一件事的两面。
    /// </summary>
    private static bool PlayerShouldDefend(Game game, Battle battle)
    {
        var threat = battle.EnemyForecast().High;
        var hp = game.Player.Hp;
        if (hp <= 0 || threat <= 0) return false;
        // 门槛先写在最前面：**这一刀值不值得占一个回合**。
        // 它守的是「这一刀本身疼不疼」，不该被别的伤害源改写——
        // 加自爆与毒那两笔的时候它被挤掉过一次，代价是 100 局里 58 局死在
        // **第 1 层**：3~5 点的巨鼠也被判成「值得防」，回合数从 5 涨到 36，
        // 一刀都没砍出去。
        if (threat * 3 < hp) return false;              // 这一刀不疼，不如还手
        // 过门槛之后才算这一回合的**总账**：下一刀（防御能减半）、
        // 已经挂在身上的毒、以及这一刀打死的那只怪倒下的自爆。
        // 后两笔**全额**加回去——`CombatMath.FixedDamage` 明写着它们无视防御、
        // 也不吃姿态减半。混着打折会让探针以为防御能救回一半自爆，
        // 那不是任何一条规则，那是把三条算式揉成了第四条。
        var (boom, _, _) = battle.ExplodeOnDeathForecast();
        var afterDefend = hp - (int)Math.Ceiling(threat * 0.5) - boom - battle.PoisonHpThisTurn();
        return afterDefend > Math.Max(4, hp / 5);        // 防了还得站得住
    }

    /// <summary>
    /// 这一场该不该走。判据只有一条：**再打下去赢不了**。
    ///
    /// 为什么需要它：地图上 63% 的仗是可选的（`OptionalRoomsSeen`），而横扫里的
    /// 「血不够而放过」从第 58 轮到这一轮一直是 **0 次**。原因不是门槛太松，
    /// 是**探针走进去之后就再也没有出口**：一进战斗只有「打到赢或者死」两条路，
    /// `Battle.Escape` 与界面上的 Esc 一次都没被按过。
    /// 一个从不撤退的玩家量出来的「可选战斗」不是取舍，是必打。
    ///
    /// 两条判据，缺一条就会把「能打赢的仗」误判成「该走」：
    ///   · **这一场打不完**：敌人剩下的血 ÷ 我每刀能打出的伤害 = 还要几刀，
    ///     这几刀里我挨的打（每刀按预告的下限算，已经扣掉防御）会把我送走；
    ///   · **走进来的时候就已经不够**：血低于四成——那种局面下就算勉强赢了，
    ///     下一间房也走不过去，而撤退只收 5 点。
    ///
    /// 只对**路外的仗**生效：Boss 与「挡在去它路上的那一间」没有撤退这个选项
    /// （撤了还得回来打，而代价是白白多付 5 点血 + 一整个回合数）。
    /// 所以这里返回 true 的场合，`WalkToNextRoom` 下一轮会把它判成「血不够而放过」。
    /// </summary>
    private static bool ShouldFlee(Game game, Battle battle, Tally tally)
    {
        if (battle.Phase != BattlePhase.Player) return false;
        if (game.CurrentRoom.Type is RoomType.Boss) return false;
        // 必经的那一间不撤：撤了还得回来，中间那几步与 5 点血是纯亏。
        if (IsOnBossPath(game, game.PlayerIndex)) return false;
        var hp = game.Player.Hp;
        var max = Math.Max(1, game.Stats.MaxHp);
        if (hp <= 0) return false;
        // 走进来就已经不够：这一间本来就不该进。撤退是止损，不是战术。
        // 门槛读的是「进来的时候有多少血」（`FightEntryHpPct`），不是现在有多少——
        // 打到一半才掉到四成以下，那是「这一场真的打不过」，由下面那条算账判。
        // 这一间本来就不该进：血已经低到「赢了也走不到下一间」，而这一场又可以不打。
        // 两件事都要：**打不过**（下面那条算账），以及**不打也不要紧**——
        // 挡在去 Boss 路上的那一间，撤了还得回来，白白多付 5 点血。
        // 「这一场打不完」：剩下的血要几刀，这几刀里我挨多少。
        //
        // 刀的算法与 `TryFinishNow` 用的其实是同一件事（我这一手能打掉多少），
        // 这里刻意用更粗的「攻击 − 敌防」：它是一个**下界**，
        // 用它算出来的刀数偏多，宁可早撤一步，也别让一个还有救的局面被当成绝望。
        var living = battle.LivingEnemies();
        // 已经不剩敌人了：这一场其实已经赢了，走的判断不成立。
        // 这个 `return false` 不是防御性代码——它曾经是一条**假判据**的遮羞布：
        // 上面那个条件写成了「还有没打完的战斗房」而不是「这一间在不在必经之路上」，
        // 于是它恒为真，`ShouldFlee` 一次都没真的执行过，而报表上一片 0 看着像
        // 「玩家不想撤」。一个从不执行的函数与一个不存在的函数，读数上一模一样。
        if (living.Count == 0) return false;
        var perSwing = Math.Max(1, game.Stats.Attack - living.Max(e => e.BaseDefense));

        if ((int)Math.Ceiling(living.Sum(e => Math.Max(0, e.Hp)) / (double)perSwing) <= 2) return false;  // 两刀之内收得掉，不撤
        // 每回合挨的那一刀按预告的**下限**算：上限会把所有仗都判成打不完。
        var perRound = Math.Max(1, battle.EnemyForecast().Low);
        var swingsLeft = (int)Math.Ceiling(living.Sum(e => Math.Max(0, e.Hp)) / (double)perSwing);
        var wouldLose = (long)perRound * swingsLeft >= hp;
        if (!wouldLose) return false;
        // 撤了也得站得住：撤离自己收 5 点，而探针从不用「我可能死在撤退上」这种方式赌。
        // 扛不住那 5 点就只能硬打——两条路都不好，但一条是确定的死，一条不是。
        return hp - 5 > max * 0.25;
    }

    /// <summary>
    /// 只给 `--probe-flee-dump` 用的诊断：把「为什么没撤」的那几个数打出来。
    /// 判据不成立的时候，先看是哪一个输入把它挡住的——不然只会在阈值上来回调。
    /// </summary>
    private static void DumpFleeReason(Game game, Battle battle, Tally tally)
    {
        if (!FleeDump) return;
        var living = battle.LivingEnemies();
        var perSwing = Math.Max(1, game.Stats.Attack - (living.Count == 0 ? 0 : living.Max(e => e.BaseDefense)));
        var swingsLeft = living.Count == 0 ? 0 : (int)Math.Ceiling(living.Sum(e => Math.Max(0, e.Hp)) / (double)perSwing);
        GD.Print($"[flee] f{game.Floor} {game.CurrentRoom.Type} hp={game.Player.Hp}/{game.Stats.MaxHp} entry={tally.LastFightEntryHpPct:P0} "
                 + $"living={living.Count} hpLeft={living.Sum(e => e.Hp)} perSwing={perSwing} swings={swingsLeft} "
                 + $"low={battle.EnemyForecast().Low} bossPath={IsOnBossPath(game, game.PlayerIndex)}");
    }

    /// <summary>
    /// 这一间是不是「从脚下到 Boss 的**唯一**那条路」上的一间。
    ///
    /// 判据是「起点 → 这一间 → Boss」的距离加起来正好等于「起点 → Boss」的距离
    /// —— 也就是这一间确实压在那条最短路上，而不是只是在同一张连通图里。
    /// 这张图是一棵树，所以它同时等价于 `distToBoss > 0`（Boss 自己不算）。
    ///
    /// 写的时候踩过两次，都是同一句话的两种错读：
    ///   1. `BattlesToBoss() > 0 && 走得到 Boss`：那张图上前面还有两场没打，
    ///      于是一个**路外的**房间也被判成了必经；
    ///   2. 只写「走得到 Boss」：连通图里每个点都走得到，于是整张图都是必经之路。
    /// 两种错都让「必经」这两个字失去了信息量。距离才是信息。
    /// </summary>
    private static bool IsOnBossPath(Game game, int index)
    {
        var links = game.Map.Links;
        var boss = game.Map.BossIndex;
        if (boss < 0 || boss >= links.Count || index < 0 || index >= links.Count) return false;
        var distToBoss = MapGen.BfsDistances(links, boss);
        var distFromStart = MapGen.BfsDistances(links, game.Map.StartIndex);
        if (distToBoss[index] < 0 || distFromStart[index] < 0 || distFromStart[boss] < 0) return false;
        return distFromStart[index] + distToBoss[index] == distFromStart[boss];
    }

    /// <summary>
    /// 放一个「这一回合值得放」的伤害技能。返回 false 表示没有可放的，交给普攻。
    ///
    /// 判据是「倍率乘出来的伤害比平砍高」：技能 power ≥ 1.3 才值得占一个回合，
    /// 而 1.1 的冰霜新星在有火球时不值得。这条判据刻意写得朴素——
    /// 探针要回答的是「一个会看技能栏的人能走多远」，不是「最优解能走多远」。
    /// </summary>
    private static bool TryAttackSkill(Game game, Battle battle)
    {
        var strike = game.Stats.Attack * (1 + game.TrainingAttackPct);
        foreach (var skill in game.Player.Skills.OrderByDescending(s => s.Power))
        {
            if (skill.Type is not (SkillType.Damage or SkillType.Drain or SkillType.Poison)) continue;
            if (!Skills.IsSkillReady(game.Player, skill.Id)) continue;
            if (skill.Power < 1.3) continue;
            if (skill.Power * strike <= strike * 1.2) continue;
            if (battle.PlayerSkill(skill.Id)) return true;
        }
        return false;
    }

    /// <summary>
    /// 三选一挑一张：真人在血不满的时候会拿回血，缺装备的时候会拿装备，
    /// 其余时候拿钱。以前探针永远拿第一张，于是「战后回血」这张卡基本没被用过——
    /// 而它恰好是这款游戏里最大的一根回血来源。
    /// </summary>
    private static Reward ChooseReward(Game game, List<Reward> rewards)
    {
        var missing = game.Stats.MaxHp - game.Player.Hp;
        var potions = Inventory.Consumables(game.Player, game.ItemIndex).Count(c => c.Def.Heal is > 0);
        var room = game.Player.Inventory.Count < game.Capacity;

        // 血不满就先回血：这张卡是这一局最大的一根回血来源（横扫 30 局里 8202 点，
        // 而喝药只回了几十点）。真人不会为了「一件更好的剑」把半条命带进下一间房。
        if (missing > game.Stats.MaxHp * 0.25)
        {
            var heal = rewards.OfType<Reward.Heal>().Where(h => h.Effective).OrderByDescending(h => h.Amount).FirstOrDefault();
            if (heal is not null) return heal;
        }
        // 空槽或者有件明显更好的：拿它
        if (room)
        {
            var upgrade = rewards.OfType<Reward.Item>()
                .Where(i => i.Def.Slot != Slot.Consumable)
                .OrderByDescending(i => Score(i.Def))
                .FirstOrDefault(i =>
                {
                    var cur = Inventory.EquippedDef(game.Player, Inventory.SlotKey(i.Def.Slot), game.ItemIndex);
                    return cur is null || Score(i.Def) > Score(cur);
                });
            if (upgrade is not null) return upgrade;
            // 没装备可拿、身上又没药：拿一瓶（掉落里偶尔会有药）
            if (potions == 0)
            {
                var potion = rewards.OfType<Reward.Item>().FirstOrDefault(i => i.Def.Heal is > 0);
                if (potion is not null) return potion;
            }
            // 背包有空格时，拿一件不亏的（换钱换不到东西）
            var any = rewards.OfType<Reward.Item>().FirstOrDefault();
            if (any is not null) return any;
        }
        var gold = rewards.OfType<Reward.Gold>().FirstOrDefault();
        if (gold is not null) return gold;
        return rewards[0];
    }

    /// <summary>
    /// 逛店：真人的动作顺序不是「买一瓶就走」，而是
    /// 「先把身上的药喝到够用 → 再买一瓶带走 → 剩的钱换一件更好的装备」。
    /// 这里以前只做第二步，于是药在背包里攒着、从不进胃里——
    /// 横扫统计出来的「买药 62 瓶、喝掉 3 瓶」就是它不会玩药的证据。
    /// </summary>
    private static void VisitShop(Game game, Action<bool, string> check, Tally tally)
    {
        var stock = game.ShopStock(game.PlayerIndex);
        // 「钱没处花」的现场就在这一行：进店时钱比货架上最贵的那件还多三倍，
        // 出来时还是没花掉。以前这个数根本没有，于是「金币 1500+」只能靠人回忆。
        var priciest = stock.Where(x => !x.Sold).Select(x => x.Price).DefaultIfEmpty(0).Max();
        var goldBefore = game.Player.Gold;
        // 只在**进店那一刻**记余额：以前这一行留在函数末尾，喂进去的是「买完还剩多少」，
        // 于是「最多一次剩 2642」量的是钱包的历史峰值，而不是「进店时它堆到了多高」。
        tally.ShopLeftoverGoldWorst = Math.Max(tally.ShopLeftoverGoldWorst, goldBefore);
        if (priciest > 0)
            tally.ShopSurplusRatioWorst = Math.Max(tally.ShopSurplusRatioWorst, (double)goldBefore / priciest);
        var spentOnPotion = 0;
        // 1) 先把血补起来：身上有药、血不满，就地喝。喝到 85% 就停手（留一瓶给接下来那一仗）。
        while (game.Player.Hp < game.Stats.MaxHp * 0.85)
        {
            var now = Inventory.Consumables(game.Player, game.ItemIndex)
                .Where(c => c.Def.Heal is > 0)
                // 小药先喝，大药留着——补得上的那点缺口不值得用一瓶强效
                .OrderBy(c => c.Def.Heal >= game.Stats.MaxHp - game.Player.Hp ? 0 : 1)
                .ThenBy(c => c.Def.Heal)
                .FirstOrDefault();
            if (now.Def is null) break;
            var before = game.Player.Hp;
            game.Player.Hp = Math.Min(game.Stats.MaxHp, game.Player.Hp + now.Def.Heal!.Value);
            Inventory.RemoveItem(game.Player, now.Index);
            tally.HealUsed++;
            if (game.Player.Hp == before) break;
        }

        // 2″) 战术道具：以前这一整段不存在，于是货架上那件炸弹从来没有被买过。
        // 「探针不会用道具」这句待办，两轮之后才查清它的真因不在战斗里——**手里根本没有道具**：
        // 横扫 30 局「背包里最多同时躺过 0 件」。装备在前、金币殿后的掉落排序下，
        // 消耗品根本进不了三选一；唯一的上货通道就是这间店。
        // 判据按「这一颗能替我吃掉多少血」：
        //   · 炸弹：25 点无视防御，只要它比一瓶药补得多、或者一局里敌人都挨得住一刀，
        //     就带一颗——它的价格是 14，而一层赏金 100 上下；
        //   · 解毒草：只在身上没解药、且深层（毒是深层的主要死因之一）时带一株。
        // 花钱的顺序排在买药**之后**：药是保命，道具是省血。
        // 判据是「消耗品且不会回血」——只写 `Heal is not > 0` 会把整排装备都当成
        // 战术道具（第一版实测：它一路买进了战斧和英雄勋章）；但只写这一条又会把
        // 解毒草一起筛掉，因为它现在 8 金币换 18 点血、**也回血**。
        // 消耗品的能力有三样（回血 / 造成伤害 / 解毒），一条 `Heal` 只认得出第一样。
        //
        // 顺序是「先买会救命的，再买顺手的」：解毒草排在炸弹**前面**（它的 Damage
        // 是 null，按旧排序永远垫底）。实测这一条不是全部原因——只调顺序仍然是 0 株，
        // 因为它根本没进名单。两条都得改，缺一条都会看着像「探针不肯买」。
        var support = stock
            .Where(s => !s.Sold && s.Item.Slot == Slot.Consumable && (s.Item.Heal is not > 0 || s.Item.Cleanse == true))
            // 先买会救命的，再买顺手的：解毒草的 Damage 是 null，按旧排序它永远垫底。
            .OrderByDescending(s => s.Item.Cleanse == true ? 1 : 0)
            .ThenByDescending(s => s.Item.Damage ?? 0)
            .ThenBy(s => s.Price).ToList();
        // 带上限：一局最多两颗炸弹、一株解毒草。第一版没有上限，横扫的结果是
        // 「买到 163 件、花掉 11718 金币，只扔出去 32 颗」——买进来的东西没被用掉，
        // 钱只是从钱包挪到了背包里，而背包是有限的八格。那不是会玩，那是搬运。
        // 真人不会囤到用不完：炸弹是「这一刀疼的时候的那一手」，不是收藏品。
        var heldSupport = Inventory.Consumables(game.Player, game.ItemIndex)
            .Where(c => c.Def.Heal is not > 0).ToList();
        var budgetBombs = 2 - heldSupport.Count(c => c.Def.Damage is > 0);
        var budgetCleanse = 1 - Inventory.Consumables(game.Player, game.ItemIndex).Count(c => c.Def.Cleanse == true);
        // 战术道具排在买药**之前**买。上一版它在买药之后，于是永远轮不到：
        // 货架上只有一瓶药，买完药背包就满了（八格）；`break` 一响，整段道具一个都进不来。
        // 横扫里「判据说该吃解毒草、而背包里没有那一株」正是这么来的——
        // 从来就没有哪一格是留给它的。现在反过来：道具先占位，药那一格由「卖旧装备」腾。
        //
        // `reserved` 是「这一趟已经挑定、还没落袋」的那几件：买药那一段按 `!Sold` 挑药，
        // 而 `Sold` 要到这一轮末尾才标——中间不记一笔，就会把炸弹当药重复算一遍。
        var reserved = new HashSet<ShopStockItem>();
        foreach (var entry in support)
        {
            if (entry.Price > game.Player.Gold) continue;
            // 预算只在**真的买下**时才扣。上一版写成 `budgetBombs-- <= 0` 那一瞬间就扣，
            // 于是「背包满了、没买成」也会吃掉一次额度——后面同样的东西再也买不了。
            if (entry.Item.Damage is > 0 && budgetBombs <= 0) continue;
            if (entry.Item.Cleanse == true && budgetCleanse <= 0) continue;
            // 背包满就腾一格。**不腾的话这一件直接放不下**：横扫里的
            // 「买到 163 件、背包里最多躺着 3 件」就是从这里来的——钱花掉了，东西没进包。
            // 卖完还是满的（八格全是穿着的，或者身上还留着两瓶药）：这一件真的放不下，跳过。
            //
            // 以前这里是 `continue` —— 「卖不掉」与「不腾」被写成了同一件事。
            // 这条路上最缺的恰恰是那件**会救命**的道具：八格满了之后，解毒草
            // 在 636 次上架里 524 次根本没被放进背包，而售价只有一层进项的十分之一。
            if (game.Player.Inventory.Count >= game.Capacity && !SellSomething(game, tally))
            {
                tally.SupportShelfBlocked++;
                continue;
            }
            // 卖出去了就把这一格腾出来——`SellSomething` 只负责「卖掉一件」，
            // 而这一件道具是**现在就要进包**的（它挡在买药与买装备两条路前面）。
            // 不腾的话下一次进店又是满的：100 局里 540 次「货架上有解毒草，背包是满的」。

            if (entry.Item.Damage is > 0) budgetBombs--;
            if (entry.Item.Cleanse == true) budgetCleanse--;
            reserved.Add(entry);
            tally.SupportBought++;
            game.SpendGold(entry.Price);
            var boughtSupport = game.RegisterRuntimeItemCopy(entry.Item);
            Inventory.AddItem(game.Player, boughtSupport.Id, game.Capacity);
            entry.Sold = true;
            tally.SupportGoldSpent += entry.Price;
            // 「战术道具」这一格是炸弹 + 解毒草两样：只数「不回血的」会把刚买下的
            // 那一株草漏掉（它现在也回 18 点血），于是报表上的「最多同时躺过 N 件」
            // 永远比真实值少一株。
            tally.SupportCarriedWorst = Math.Max(tally.SupportCarriedWorst,
                Inventory.Consumables(game.Player, game.ItemIndex).Count(c => c.Def.Heal is not > 0 || c.Def.Cleanse == true));
            if (entry.Item.Cleanse == true) tally.CleanseBought++;
            if (entry.Item.Damage is > 0) tally.BombBought++;
        }

        // 3′) 装备买完之后，**用剩下的钱把药补满**。
        //
        // 药的上限是两瓶，而上面那一段买药排在买装备**之前**：买的时候身上往往已经有一瓶
        // （或者钱还不够买第二瓶），于是离开柜台时手上只有一瓶——接下来那间精英房打完，
        // 一瓶都不剩。横扫里 100 局喝掉 598 瓶，而死因表上「死在 12/13 层」有 41 局：
        // 那两层恰好是钱最多、货最贵的两层，钱全换成了装备，换血的那一笔留到了最后。
        //
        // 真人不会这么排队：装备是「以后每一仗都省一点」，药是「这一仗活着走出去」。
        // 所以药必须**在离开店之前补到两瓶**，而不是只在进门时买一瓶。
        var potions = Inventory.Consumables(game.Player, game.ItemIndex).Count(c => c.Def.Heal is > 0);
        if (potions < 2)
        {
            foreach (var entry in stock.Where(x => !x.Sold && x.Item.Heal is > 0).OrderBy(x => x.Price).ToList())
            {
                if (potions >= 2) break;
                if (entry.Price > game.Player.Gold) break;
                if (game.Player.Inventory.Count >= game.Capacity && !SellSomething(game, tally)) break;
                game.SpendGold(entry.Price);
                var restock = game.RegisterRuntimeItemCopy(entry.Item);
                Inventory.AddItem(game.Player, restock.Id, game.Capacity);
                entry.Sold = true;
                potions++;
                tally.PotionBought++;
                tally.HealGold += entry.Price;
                spentOnPotion += entry.Price;
            }
        }
        // 这条核对必须在**我们买走药之前**判：战术道具那一段已经挪到了前面，
        // 它会把货架上唯一那瓶药买走——于是上面那句「货架上没有回血药」在对的货架上成立，
        // 而三十局里冒出一片「[!!] 商店里一瓶回血药都没有（货架：治疗药水、…）」。
        // 判据站错了位置，看见的不是游戏，是自己刚才干的事。
        // 保底是**每个货架**一瓶（`ShopRoller.Roll` 的 `rng.Pick(heal)`），所以「一瓶都买不到」
        // 只有在「买过之后」才成立：同一个人反复进同一家店，货架不会凭空再长出一瓶。
        // 以前这里对每一次进门都 `check(healOnShelf > 0)`，把「这一家我买空了」判成了
        // 「这家店开门时就没有药」——判据站错了位置，看见的不是游戏，是自己刚干的事。
        var healOnShelf = stock.Count(s => s.Item.Heal is > 0);
        tally.ShopVisits++;
        // 保底是「每个货架一瓶」，所以判它必须在**进门那一刻**：买空之后再进门，
        // 货架当然是空的。以前这里对每一次进门都 check 一次，冒出一片
        // 「商店里一瓶回血药都没有（货架：治疗药水、…）」——判据看见的不是游戏，
        // 是自己刚才干的事。
        if (healOnShelf <= 0) tally.ShopsDoorWithoutPotion++;
        if (!tally.ShopSawPotionOnShelf)
            tally.ShopSawPotionOnShelf = healOnShelf > 0;
        var anyHeal = stock.FirstOrDefault(s => !s.Sold && s.Item.Heal is > 0);
        if (anyHeal is null) return;

        // 2) 买药带走 —— 身上已经有两瓶就不用再买了。
        // 上限是两瓶**回血**药；战术道具不算，但它们已经占了格子，所以要看还能不能放下。
        if (potions < 2 && game.Player.Inventory.Count >= game.Capacity) SellSomething(game, tally);
        if (potions < 2)
        {
            // 2′) 背包满：先在店里卖一件。
            // 真人站在柜台前不会「因为背包满就转身走人」——他会卖掉换下来的旧武器。
            // 以前探针在这里直接 return 并记一次 BlockedByFull，于是横扫里
            // 「钱够、药在架、买不进来」出现了 300 多次，看起来像游戏缺一条出口；
            // 缺的其实是「卖」这个动作本身（现在界面与规则都有了，这里只是把它用起来）。
            if (game.Player.Inventory.Count >= game.Capacity)
            {
                var sold = SellSomething(game, tally);
                if (!sold) tally.BlockedByFull++;
            }
            var potion = game.Player.Inventory.Count < game.Capacity
                ? stock.FirstOrDefault(s => !s.Sold && !reserved.Contains(s) && s.Item.Heal is > 0 && s.Price <= game.Player.Gold)
                : null;
            if (potion is null && game.Player.Inventory.Count < game.Capacity) tally.CouldNotAfford++;
            if (potion is not null)
            {
                tally.PotionBought++;
                tally.HealGold += potion.Price;
                spentOnPotion += potion.Price;
                game.SpendGold(potion.Price);
                var bought = game.RegisterRuntimeItemCopy(potion.Item);
                Inventory.AddItem(game.Player, bought.Id, game.Capacity);
                potion.Sold = true;
                check(game.ItemIndex.ContainsKey(bought.Id), "买下的药查不到");
            }
        }
        // 以前这里在「药够两瓶」时直接 return，于是整段买装备的代码**从来没跑过**：
        // 玩家（和探针）永远只买药，剩下的钱一路攒到结算（那局 2833 金币，装备栏里
        // 还挂着锈剑）。药是补血，装备是变强；后者才是「钱没处花」的那一头。

        // 这一笔判据站在**买完装备之后**：以前它写在买装备那一段的前面，
        // 于是量的永远是「进店时钱多不多」，而那件事已经在 `ShopSurplusRatioWorst`
        // 里报过了。这里要问的是另一件事：**这家店里能花掉的钱，花完之后还剩多少**。
        // 剩下的钱如果还是货架最贵那件的三倍以上，那说明钱不是被攒下来的，
        // 是这家店根本没有对应的货——「钱没处花」的现场。
        var leftAfterShopping = game.Player.Gold;
        if (priciest > 0 && leftAfterShopping >= priciest * 3) tally.ShopLeftoverGold++;
        // 3) 剩的钱换装备：**每一件**买得起的、比身上好的都买（同价先看缺口：空槽优先）。
        //
        // 这里以前只买「比槽里那件好」的：买了更强的武器之后，同一家店里那把稍弱的
        // 就再也进不了条件，而更常见的情况是——身上没有任何一件值得换，于是
        // 全表的装备都买不进来。钱就这么一直攒着：横扫 30 局的末段采样是
        // 「第 10~12 层平均 1100 金币、身上一瓶药都不缺」，钱在结算时还剩上千。
        // 现在按「带得走的都带走」买：买进来先穿上，穿不上的（同槽已有更好的）
        // 再按「换上/卖店」处理，会计不会因为多买一件而变负。
        var affordable = stock.Where(s => !s.Sold && s.Item.Slot != Slot.Consumable && s.Price <= game.Player.Gold)
                              .OrderByDescending(s => Score(s.Item) + AffixCount(s.Item))
                              .ToList();
        foreach (var entry in affordable)
        {
            if (entry.Sold || entry.Price > game.Player.Gold) continue;
            // 手里的钱够不够「现在就把这个槽填满」：够，这件不如身上那件也值得买。
            //
            // 上一轮这里只问「这一件比槽里那件好吗」，于是到第 10 层左右三个槽都穿满了，
            // 剩下的钱在探针手里**一件东西都买不进来**——横扫里 30 局只死了 4 局，
            // 剩下的 26 局是「钱多到花不掉」被记了下来，可那些局身上一瓶药都没有。
            // 这不是「货架收不下」，是探针不舍得花钱：另一个槽还是 +2 攻击的皮甲，
            // 而它正盯着一把 +9 的战斧想「我身上这把剑已经够好了」。
            // 真人会补短板，所以这里按整套装备的缺口来算账。
            var slotKey0 = Inventory.SlotKey(entry.Item.Slot);
            // 只买**真的更好**的那一件，不买「同一件再买一遍」。
            //
            // 横扫里那个症状：一个 34/100 通关率的采样里，玩家背包里躺着两把
            // 深渊之牙、两件霜纹锁甲——钱换成了**重复品**，而同一个槽里穿着的还是那件。
            // `SlotHasGap` 判的是「这个槽有没有缺口」，缺口存在期间同一家店的每件装备
            // 都算「值得买」，于是同一件被买两遍。真人不会这么买：第二件一文不值。
            if (DuplicatesOwned(game, entry.Item)) continue;
            // 装备预算：**每一层最多三笔**（一个槽一件，三个槽）。
            //
            // 不加这一条时，深层那个 3000 金币的钱包会被「补短板」一路买到只剩几十——
            // 下一间精英房、一瓶药都买不起。横扫里那条最刺眼的对比就在这儿：
            // 补短板放开的那一版，通关率 70/100 → 33/100，死亡集中在 12~13 层，
            // 那正是钱最多、货最贵的两层。钱不是没有出口，是**被装备抢先花光了**。
            // 真人不会把整层进项一次性换成三件装备，他攒两三层。
            if (tally.GearBuysThisFloor >= 3) continue;
            var budget = PlayerBudget(game, stock);
            // 短板按**槽**看，不按总分看：三个槽一起算总分时，「武器已经顶配、
            // 护甲还是第 3 层那件」会被总分掩盖——而店里的护甲正等着那一笔钱。
            // 总分那笔账会一直说「够了」，于是深层那家店里一件装备都买不进来，
            // 背包八格里塞满八件按分数来说「都很好」的重复品，而一条能救命的解毒草
            // 连一格都分不到（100 局：675 次上架，540 次背包是满的）。
            // 「补短板」按**槽**判，不再额外要求「总分还有缺口」。
            //
            // 以前写成 `PlayerScore(game) < budget && SlotHasGap(...)`：两个条件一起要，
            // 于是只要有一个槽是空的、而另一个槽已经顶配，中间那个 `sum < budget`
            // 就永远成立——判据看上去很严，实际上只等价于 `SlotHasGap`。
            // 更糟的是它读起来像「总分还有空间才买」，而总分从来没有空间：
            // `budget` 是按货架现价算的，玩家一买就把预算撑满了。
            // 现在两个真相都摆出来：哪个槽有缺口就补哪个槽；总分只在报表里看。
            var fillGap = SlotHasGap(game, stock, slotKey0);
            if (fillGap && game.Player.Inventory.Count >= game.Capacity && !WorthASlot(entry, game)) continue;
            var slotKey = Inventory.SlotKey(entry.Item.Slot);
            var cur = Inventory.EquippedDef(game.Player, slotKey, game.ItemIndex);
            // 比的是「这个槽里最好的那件」（穿着的或背包里的），不是「身上穿着一的那件」：
            // 包里已经有一把更好的时候，店里这把不该再买第二遍。
            var curIdx = BestInSlot(game.Player, game, slotKey);
            if (curIdx >= 0 && game.ItemIndex.TryGetValue(game.Player.Inventory[curIdx], out var inBag) && Better(inBag, cur ?? entry.Item))
                cur = inBag;
            var worth = cur is null || Better(entry.Item, cur) || (fillGap && !WorseThanSlot(entry.Item, cur));
            if (worth)
            {
                if (game.Player.Inventory.Count >= game.Capacity)
                {
                    // 背包满：换掉一件不如它的。药和装备同样能换，只看分数。
                    var worst = FindWorstIndex(game);
                    if (worst < 0 || !Better(entry.Item, game.ItemIndex[game.Player.Inventory[worst]])) continue;
                    game.SpendGold(entry.Price);
                    var copy = game.RegisterRuntimeItemCopy(entry.Item);
                    if (Inventory.IsEquipped(game.Player, worst) is { } slot) Inventory.Unequip(game.Player, slot, game.ItemIndex);
                    Inventory.SwapInItem(game.Player, worst, copy.Id);
                    entry.Sold = true;
                    tally.GearBuysThisFloor++;
                }
                else
                {
                    game.SpendGold(entry.Price);
                    var boughtItem = game.RegisterRuntimeItemCopy(entry.Item);
                    Inventory.AddItem(game.Player, boughtItem.Id, game.Capacity);
                    entry.Sold = true;
                    tally.GearBuysThisFloor++;
                    DressUp(game, check);
                }
                continue;
            }
            // 同槽已经有更好的：这一件只值钱，买进来等于把钱换成三成，
            // 真人不会干——跳过。但**这空格不白留**：一个槽位只有一件装备，
            // 而每层大约进账 100~200 金币，装备在 10 层左右就到顶了，
            // 之后那笔进项没有对应的开销。谁也没错，只是钱没有出口。
        }

    }

    /// <summary>
    /// 除了泉眼这笔钱，身上还该留住多少：一瓶药 + 一次血祭的余量。
    /// 写成「按层数长」而不是常数：深层的药与事件都更贵，
    /// 而常数会让第 1 层显得抠、第 15 层显得莽。
    /// </summary>
    private static int OfferingReserve(Game game) => 80 + game.Floor * 20;

    /// <summary>
    /// 身上（穿着或背包里）是不是已经有一件**同模板**的装备了。
    ///
    /// 判「同一件」看 Id：词条是实例属性，模板是同一份，所以再买一遍就是重复品。
    /// 消耗品不算——药是可以囤的，装备不行。
    /// </summary>
    private static bool DuplicatesOwned(Game game, ItemDef def)
    {
        if (def.Slot == Slot.Consumable) return false;
        foreach (var id in game.Player.Inventory)
            if (id == def.Id) return true;
        return false;
    }

    /// <summary>
    /// 这个人的「预算」：把他身上的三个槽，按货架上能买到的**每槽最好那件**重算一遍。
    ///
    /// 为什么不能拿「身上这套的分数」当目标：那样第 10 层之后目标必然达得到
    /// （装备在 12 层左右就到顶），于是「钱花不掉」永远成立，而且它证明不了任何事——
    /// 一个人的装备到顶了，剩下的钱还能干什么，才是这一轮要回答的问题。
    /// 预算按货架算，问的才是「这家店里有没有值得我补的短板」。
    ///
    /// 药不算进预算：它是消耗品，本来就没有「到顶」这一说。
    /// </summary>
    private static int PlayerBudget(Game game, IEnumerable<ShopStockItem> stock)
    {
        var best = new Dictionary<string, int>();
        foreach (var slot in new[] { Slot.Weapon, Slot.Armor, Slot.Trinket })
            best[Inventory.SlotKey(slot)] = 0;
        // 现价 × 1.6 是「这一件折成面板分」的估值：装备的价随层数涨，
        // 面板却不涨，直接用价当初值会让第 15 层的锈剑显得比第 1 层值钱。
        foreach (var slot in new[] { Slot.Weapon, Slot.Armor, Slot.Trinket })
        {
            var key = Inventory.SlotKey(slot);
            var worn = Inventory.EquippedDef(game.Player, key, game.ItemIndex);
            var floorValue = worn is null ? 0 : Score(worn) + AffixCount(worn);
            var shelf = stock.Where(s => !s.Sold && s.Item.Slot == slot)
                             .Select(s => Score(s.Item) + AffixCount(s.Item))
                             .DefaultIfEmpty(0).Max();
            best[key] = Math.Max(floorValue, shelf);
        }
        return best.Values.Sum();
    }

    /// <summary>这个槽还有没有「值得补」的缺口：货架上最好的那件比身上（或包里）最好的更强</summary>
    private static bool SlotHasGap(Game game, IEnumerable<ShopStockItem> stock, string slotKey)
    {
        var cur = Inventory.EquippedDef(game.Player, slotKey, game.ItemIndex);
        var bestInBag = BestInSlot(game.Player, game, slotKey);
        if (bestInBag >= 0 && game.ItemIndex.TryGetValue(game.Player.Inventory[bestInBag], out var bagged)
            && (cur is null || Better(bagged, cur)))
            cur = bagged;
        var shelf = stock.Where(s => !s.Sold && Inventory.SlotKey(s.Item.Slot) == slotKey)
                         .Select(s => s.Item)
                         .OrderByDescending(x => Score(x) + AffixCount(x))
                         .FirstOrDefault();
        return shelf is not null && (cur is null || Better(shelf, cur));
    }

    /// <summary>身上三个槽加起来的面板分（与 PlayerBudget 同一把尺子）</summary>
    private static int PlayerScore(Game game)
    {
        var total = 0;
        foreach (var slot in new[] { Slot.Weapon, Slot.Armor, Slot.Trinket })
        {
            var worn = Inventory.EquippedDef(game.Player, Inventory.SlotKey(slot), game.ItemIndex);
            if (worn is null) continue;
            total += Score(worn) + AffixCount(worn);
        }
        return total;
    }

    /// <summary>x 是不是不至于「比 y 还差」：差一点点（一件装备的分差以内）算平手</summary>
    private static bool WorseThanSlot(ItemDef? x, ItemDef? y)
    {
        if (x is null) return true;
        if (y is null) return false;
        return Score(x) + AffixCount(x) < Score(y) + AffixCount(y);
    }

    /// <summary>
    /// 背包满的时候，这一格值不值得腾给它：至少要能在三件里排上号。
    /// 「不如身上那件」和「不如身上那件且塞不进背包」是两件事，别混成一个判断。
    /// </summary>
    private static bool WorthASlot(ShopStockItem entry, Game game) =>
        Score(entry.Item) + AffixCount(entry.Item) >= Score(Inventory.EquippedDef(game.Player,
            Inventory.SlotKey(entry.Item.Slot), game.ItemIndex) ?? entry.Item);

    /// <summary>
    /// 「像人一样穿装备」：空槽就穿上，槽里那件不如新的就换掉。
    ///
    /// 打平（分数相同）时优先**双词条**的那件：这一版的装备强度大半在词条上，
    /// 而 Score 只看面板，于是探针会把「虚空之刃（攻 16）」一直穿着，
    /// 把深渊之牙（攻 23）当二手货——不是它不想，是它看不见。
    /// </summary>
    private static void DressUp(Game game, Action<bool, string> check)
    {
        var player = game.Player;
        // 先摘掉「穿在身上、但还不如背包里某件没穿的」——以前只做「从背包里挑」，于是
        // 一旦穿上就再也不会被替换（背包里那件更强的永远进不了比较，因为槽里已经满了）。
        for (var round = 0; round < 8; round++)
        {
            var changed = false;
            for (var i = 0; i < player.Inventory.Count && !changed; i++)
            {
                if (!game.ItemIndex.TryGetValue(player.Inventory[i], out var fresh)) continue;
                if (fresh.Slot == Slot.Consumable) continue;
                var key = Inventory.SlotKey(fresh.Slot);
                var worn = Inventory.EquippedDef(player, key, game.ItemIndex);
                if (worn is null) continue;
                // 只比同一个背包格：这一格正穿着（`EquippedDef` 读的就是它），
                // 拿「另一把+更差的锈剑」跟它比自己当然更差，于是多余的装备
                // 被摘下来又装上去，来回八轮。要问的是「这个槽里有没有更好的选择」，
                // 那得先看槽里这件是不是全场最好的。
                if (BestInSlot(player, game, key) != i) continue;
                if (Better(fresh, worn)) continue;
                if (ReferenceEquals(fresh, worn)) continue;   // 同一件，不比
                Inventory.Unequip(player, key, game.ItemIndex);
                changed = true;
                break;
            }
            if (!changed) break;
        }
        for (var i = 0; i < player.Inventory.Count; i++)
        {
            if (Inventory.IsEquipped(player, i) is not null) continue;
            if (!game.ItemIndex.TryGetValue(player.Inventory[i], out var cand)) continue;
            if (cand.Slot == Slot.Consumable) continue;
            var slotKey = cand.Slot switch
            {
                Slot.Weapon => "weapon",
                Slot.Armor => "armor",
                _ => "trinket",
            };
            var cur = Inventory.EquippedDef(player, slotKey, game.ItemIndex);
            if (cur is not null && !Better(cand, cur)) continue;
            var before = player.Hp;
            var beforeMax = game.Stats.MaxHp;
            if (!Inventory.Equip(player, i, game.ItemIndex)) continue;
            check(game.Stats.MaxHp >= 1, "换装之后面板上限算成了 0");
            // 换装不该当一瓶药喝：当前生命要跟着**上限的增量**平移。
            // 判据写成等式，而不是 `Hp == min(before, MaxHp)`——后者只覆盖「上限降了」那一半，
            // 于是「上限涨了、血跟着涨了」会被当成 bug 报出来（而它现在是设计）。
            // 上限没动的时候这条等式退化成 `before == before`，它什么也没证明；
            // 而 `before` 本身可能已经高于当前上限（读档之后、或者上一个门把血补到了
            // 旧上限上），于是 `Clamp(before)` 会把一个**换装根本不负责**的差额
            // 算成「该是 106」。上限没动就没有「平移」这回事：这一次换装只换属性、不碰血。
            var expected = game.Stats.MaxHp == beforeMax
                ? player.Hp
                : Math.Clamp(before + (game.Stats.MaxHp - beforeMax), 1, game.Stats.MaxHp);
            check(player.Hp == expected,
                $"换装把生命从 {before} 改成了 {player.Hp}（上限 {beforeMax} → {game.Stats.MaxHp}，该是 {expected}）");
        }
    }

    /// <summary>
    /// 一件装备「值多少血」。生命按 1 算，是因为毒、自爆、反震这些**无视防御**的伤害
    /// 已经占到这个游戏伤害来源的一半——它们只认血条。以前生命权重是 1、
    /// 攻击 ×3、防御 ×2，等于把「血条」排在最后；而毒按血条百分比掉血之后，
    /// 同一个面板分里，「+6 生命」比「+1 防御」值钱得多（后者挡不住任何一笔毒）。
    /// 这只是探针的尺子，产品里没有这条比较——它决定的是「它会不会换上那件」。
    /// </summary>
    private static int Score(ItemDef def) => (def.Attack ?? 0) * 3 + (def.Defense ?? 0) * 2 + (def.MaxHp ?? 0) * 2;

    /// <summary>a 是不是比 b 更值得穿：先看面板分，打平看谁带词条（词条数量）</summary>
    private static bool Better(ItemDef a, ItemDef b)
    {
        var sa = Score(a);
        var sb = Score(b);
        if (sa != sb) return sa > sb;
        return AffixCount(a) > AffixCount(b);
    }

    private static int AffixCount(ItemDef def) => def.Affixes?.Count ?? 0;

    /// <summary>某个槽里、背包中最好的那一件的下标（空槽返回 -1）</summary>
    private static int BestInSlot(Player player, Game game, string slotKey)
    {
        var best = -1;
        ItemDef? bestDef = null;
        for (var i = 0; i < player.Inventory.Count; i++)
        {
            if (!game.ItemIndex.TryGetValue(player.Inventory[i], out var def)) continue;
            if (def.Slot == Slot.Consumable || Inventory.SlotKey(def.Slot) != slotKey) continue;
            if (bestDef is null || Better(def, bestDef)) { best = i; bestDef = def; }
        }
        return best;
    }

    /// <summary>
    /// 在店里卖掉一件「最没用」的东西，换一格空间。
    /// 顺序和真人一致：先卖比身上那件更差的装备，再卖多余的消耗品（留两瓶药），
    /// 最后才动身上穿着的。卖不出好价钱的（价格 0）不卖。
    /// </summary>
    private static bool SellSomething(Game game, Tally tally)
    {
        var p = game.Player;
        var potions = Inventory.Consumables(p, game.ItemIndex).Count(c => c.Def.Heal is > 0);
        // 三个 pass 都会跳过「穿着的那件」：卖东西是为了腾一格放一瓶药，
        // 不是拆自己身上的护甲。`pass 2` 以前能把穿着的也卖掉——那是三档判据里唯一
        // 没有 `!equipped` 的一档，随手补的一个 `_ =>`，代价是玩家少了 6 点防御。
        for (var pass = 0; pass < 3; pass++)
        {
            for (var i = 0; i < p.Inventory.Count; i++)
            {
                if (!game.ItemIndex.TryGetValue(p.Inventory[i], out var def)) continue;
                var equipped = Inventory.IsEquipped(p, i) is not null;
                var keep = pass switch
                {
                    // pass 0：比身上那件还差的、没穿着的装备
                    0 => !equipped && def.Slot != Slot.Consumable
                         && Inventory.EquippedDef(p, Inventory.SlotKey(def.Slot), game.ItemIndex) is { } worn
                         && Score(worn) >= Score(def),
                    // pass 1：多余的那瓶药（两瓶以上才卖）
                    1 => def.Slot == Slot.Consumable && def.Heal is > 0 && potions >= 2 && !equipped,
                    // pass 2：实在腾不出来，才动背包里没穿着的任何一件（穿着的永远不动）
                    _ => !equipped,
                };
                if (!keep) continue;
                if (Inventory.SellValue(def) <= 0) continue;
                var saleValue = Inventory.SellValue(def);
                if (!game.SellItem(i)) continue;
                if (def.Slot == Slot.Consumable && def.Heal is > 0) potions--;
                tally.ItemsSold++;
                tally.GoldFromSales += saleValue;
                return true;
            }
        }
        return false;
    }

    private static int FindWorstIndex(Game game)
    {
        var worst = -1;
        var worstScore = int.MaxValue;
        for (var i = 0; i < game.Player.Inventory.Count; i++)
        {
            if (Inventory.IsEquipped(game.Player, i) is not null) continue;
            if (!game.ItemIndex.TryGetValue(game.Player.Inventory[i], out var def)) continue;
            // 消耗品也算「最没用的一件」：背包八格里常常有五六件是药，
            // 而这里以前跳过消耗品，于是「背包满」时腾不出任何一格——
            // 那局死在第 13 层、包里躺着 2152 金币和四瓶药，药却一瓶也买不进来。
            // 卖掉一件药换一件装备是玩家真的会做的决定（而且卖出价三成，
            // 亏的那七成正是「背包只有八格」的价钱）。
            if (def.Slot == Slot.Consumable && Inventory.IsEquipped(game.Player, i) is null)
            {
                // 药按「一颗顶几分」记账。
                //
                // 这里以前是 `(Heal ?? 0) / 40`：小药 30 点算 0 分、强效 70 点算 1 分，
                // 而**炸弹与解毒草是 0 分**（它们不回血）——于是「换掉最没用的一件」
                // 永远先扔掉炸弹和解毒草。横扫里那条最刺眼的数据就是从这里来的：
                // 玩家打到第 13 层，背包里一件药都没有，两把锈剑和一颗炸弹躺着。
                // 分数要能表达「这一件在战斗里值多少血」：炸弹 25 点无视防御，按 30 记。
                var potionScore = def.Cleanse == true ? 20 : def.Damage is { } bd ? bd + 5 : (def.Heal ?? 0);
                if (potionScore < worstScore) { worstScore = potionScore; worst = i; }
                continue;
            }
            if (def.Slot == Slot.Consumable) continue;
            var score = (def.Attack ?? 0) * 3 + (def.Defense ?? 0) * 2 + (def.MaxHp ?? 0);
            if (score < worstScore)
            {
                worstScore = score;
                worst = i;
            }
        }
        return worst;
    }

    private static BattleOptions Options(Game game, EncounterDef enc) => new()
    {
        Rules = new CombatRules
        {
            CritChance = GameDataStore.Get().Config.CritChance,
            CritMultiplier = GameDataStore.Get().Config.CritMultiplier,
            DefendReduction = GameDataStore.Get().Config.DefendReduction,
            DamageVariance = GameDataStore.Get().Config.DamageVariance,
        },
        AttackerAtk = c => c is Player ? (int)Math.Round(game.Stats.Attack * (1 + game.TrainingAttackPct)) : c.BaseAttack,
        AttackerDef = c => c is Player ? game.Stats.Defense : c.BaseDefense,
        MaxHp = c => c is Player ? game.Stats.MaxHp : c.BaseMaxHp,
        FloorMechanic = game.FloorMechanicId,
        Floor = game.Floor,
        Affixes = game.Affixes,
        Allies = enc.Allies,
        EnemyDefs = enc.EnemyDefs,
        EnemyCritBonus = enc.EnemyCritBonus,
    };
}
