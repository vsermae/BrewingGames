// 商店：随机刷新 3~4 件商品，价格在基础价上下浮动。
namespace AbyssEcho.Systems;

using AbyssEcho.Core;
using AbyssEcho.Data;

public sealed class ShopStockItem
{
    public required ItemDef Item { get; init; }
    public int Price { get; init; }
    public bool Sold { get; set; }

    /// <summary>
    /// 未抬价（未按周目与钱包缩放）的标价，也就是货架上「这一件本来值多少」。
    /// 判「这一层的货架够不够深」只能看它：`Price` 会被钱包抬到 3 倍，
    /// 2000 金币的玩家会让第 1 层的锈剑标到 36，读 `Price` 就分不清档位了。
    /// </summary>
    public int BasePrice { get; init; }

    public ShopStockItem ClonePriced(int price) => new() { Item = Item, Price = price, BasePrice = BasePrice, Sold = Sold };
}

public static class ShopRoller
{
    /// <summary>
    /// 每个货架至少放一瓶能回血的药。
    ///
    /// 以前是「从全表里随便抽 3~4 件」：表里 19 件只有 2 件能回血，
    /// 于是 **62/200 的商店一瓶回血药都没有**（第 1 层就这样，与深度无关）。
    /// 而这一层深度上玩家唯一能补血的地方就是这间店——商店开成空的，
    /// 剩下的路就只能靠「不要挨打」走完。
    ///
    /// 保底是「每个货架一瓶」，不是「每次刷新一瓶」：货架是缓存对象，
    /// 玩家买走之后不该凭空再长出一瓶（那等于免费续药）。
    /// </summary>
    /// <param name="supportItems">
    /// 除药水以外的「战术道具」（炸弹这类）。它们不进装备档位池，
    /// 但要保证货架上偶尔有一件——全游戏只有 2 件能回血的消耗品，
    /// 其余消耗品（炸弹）在旧实现里根本没有进货通道，于是「花钱」这件事
    /// 只剩下买药与买装备两条路。
    /// </param>
    /// <summary>
    /// 货架 = 1 件药 + 1 件战术道具 + 1~2 件本层档位的装备。
    ///
    /// 分配由这里定，不交给「全表洗牌后取前 N 个」：
    /// 那样抽出来的永远是「装备占大头」，因为装备本来就多。
    /// </summary>
    public static List<ShopStockItem> Roll(Rng rng, IReadOnlyList<ItemDef> gear, IReadOnlyList<ItemDef> consumables, int floor)
    {
        var heal = consumables.Where(i => i.Heal is > 0).ToList();
        var support = consumables.Where(i => i.Heal is not > 0).ToList();
        var chosen = new List<ItemDef>();

        // 药：每个货架至少一瓶。它是玩家唯一能主动买到的回血东西。
        if (heal.Count > 0) chosen.Add(rng.Pick(heal));
        // 战术道具：一半概率，让「这家店」有区别
        if (support.Count > 0 && rng.Chance(0.5)) chosen.Add(rng.Pick(support));
        // 装备：按本层档位挑（模板价随层数上涨），不再从全表随便抽。
        // 档位有**上下两条边**：只有上限时，第 9 层会摆出 +23 的深渊之牙
        // （它一整局最强的一把），第 15 层却可能只摆锈剑——两家店看着都「合法」。
        // 比较的双方必须是**同一种价**：`ShopStockItem.BasePrice` 是「这一件在本层的标价」
        // （模板价 + 楼层 1.5），档位也是按标价定的。拿模板价去比档位，
        // 差的就是那层楼层加价——第 8 层的深渊之牙模板价 210 ≤ 210 进来了，标价却是 244。
        // 档位筛选与标价必须读**同一个算式**（`ShelfPriceAtFloor`）。
        // 以前它们是两个：筛的时候用「模板价 + 楼层 1.5」，摆上货架时又乘了一次浮动，
        // 差的正是那点浮动——第 8 层的深渊之牙就这样标成了 244，而那一层的档位上限是 210。
        // 货架的形状由价决定，筛的就得是那个价。
        // 两种货架各有各的读者，混成一个池子就会让一个饿死另一个：
        //   · **本层档位**（`ShopPriceFloor` ~ `ShopPriceBand`）：这是「给现在这个面板补短板」的那格；
        //   · **攒下来的钱**（高于档位上限）：这是「钱有出口」的那格，它只属于深层。
        // 上一版只有一个池子（档位内的），于是高于上限的货永远摆不出来——
        // 深渊之心模板价 560，而第 15 层的上限是 500，它从进数据表那天起就一次也没上过架。
        // 「出现率」这么问是问不出答案的：它压根不在候选里。
        var inBand = gear.Where(i => ShelfPriceAtFloor(i, floor) <= ShopPriceBand(floor) &&
                                     ShelfPriceAtFloor(i, floor) >= ShopPriceFloor(floor))
                         .OrderByDescending(i => i.Price).ToList();
        var pool = inBand.Count > 0
            ? inBand
            : gear.Where(i => ShelfPriceAtFloor(i, floor) <= ShopPriceBand(floor)).OrderByDescending(i => i.Price).ToList();
        if (pool.Count == 0) pool = gear.OrderBy(i => i.Price).ToList();
        // 攒下来的钱那一格：只有第 6 层起才有（那时候钱包才开始堆起来），
        // 而且条件是「这一层的档位已经装不下它」——同一件东西不该在货架上出现两次。
        var rich = floor >= RichShelfFloor
            ? gear.Where(i => ShelfPriceAtFloor(i, floor) > ShopPriceBand(floor)).OrderByDescending(i => i.Price).ToList()
            : new List<ItemDef>();
        // 装备**永远两件**：以前有战术道具时只摆 1 件，于是「本层档位」的那件
        // 一旦掷成消耗品就落空——第 15 层的货架上可能一件装备都没有。
        // 两件的另一个好处是它给了「这一件的属性和那一件不一样」一个比较的机会，
        // 而只有一件装备的时候，玩家能做的决定只有「买 / 不买」。
        var gearCount = 2;
        var seen = new HashSet<string>(chosen.Select(i => i.Id));
        for (var slot = 0; slot < gearCount; slot++)
        {
            // 第二格有一半机会换成「攒下来的钱」那一档：它必须与档位那一格**同时**存在，
            // 否则「深层有一样东西值得为它留钱」就永远只是一个说法——
            // 玩家在第 10 层看到的还是那件 300 出头的东西，而钱包里躺着 1300。
            var fromRich = slot == 1 && rich.Count > 0 && rng.Chance(RichShelfChance);
            var source = fromRich ? rng.Shuffle(rich) : rng.Shuffle(pool);
            foreach (var item in source)
            {
                if (!seen.Add(item.Id)) continue;
                chosen.Add(item);
                break;
            }
        }

        return chosen.Select(item =>
        {
            // 标价走 `ShelfPriceAtFloor`：它与上面筛档位时读的是同一个数。
            // 以前这里是「模板价 × 浮动 + 层数 × 1.5」，与筛档位那个略有出入——
            // 两个数各算各的，档位与标价迟早会叉开。
            var basePrice = Math.Max(1, (int)Math.Round(ShelfPriceAtFloor(item, floor) * rng.Range(0.85, 1.15)));
            return new ShopStockItem { Item = item, Price = basePrice, BasePrice = basePrice };
        }).ToList();
    }

    /// <summary>
    /// 这一层「货架上的装备值多少钱」的上限。它和 `ShopPriceFloor` 一起构成一条带子，
    /// 跟着玩家的进项走。
    ///
    /// 原来写 50 + 8×层数，第 15 层是 170——而这个数一算就知道不对：
    /// 进项是「赏金 12+4×层 一场、一层四五场」，第 10 层起一层就进 300~400，
    /// 上限 170 意味着**每打一层，能买的东西就比进来的钱便宜一倍**。
    /// 钱攒起来不是玩家抠，是货架根本收不下那笔钱。
    /// 现在改成 50 + 20×层数：第 10 层 250、第 15 层 350。
    ///
    /// 再改一次（这一轮）：20 → 30，第 10 层 350、第 15 层 500。
    ///
    /// 上一版的数刚好卡在进项下面一点点：第 15 层档位 350、而一层进项是 288，
    /// 看起来「够了」。但玩家没在每层把钱花光——横扫 30 局里进店时钱包最多一次 2642 金币，
    /// 而那次货架上最贵的只有 284（**9.3 倍**）。也就是说档位管住了「货架摆什么」，
    /// 却管不住「攒下来的钱」。20 → 30 之后第 15 层摆得出 440~500 的那一档
    /// （深渊面纱 300、龙鳞铠 260 都在里面），攒下来的钱才有东西可以换。
    /// </summary>
    public static int ShopPriceBand(int floor) => 50 + 30 * Math.Max(1, floor);

    /// <summary>
    /// 这一层「最便宜的那件装备」的门槛：越深，摆出来的越是好东西。
    ///
    /// 定它的不是美观，是「钱有没有出口」：玩家的进项按层线性涨（赏金 12+4×层），
    /// 而货架的值有上限。于是第 10 层以后每打一场，钱包就多出一截没有对应的开销——
    /// 横扫里进店时身上 1000+、店里最贵的 174，中间那 800 一局都花不出去。
    ///
    /// 它**只由上界定义**，不另写一条公式：先写过 `40 + 24×(层-1)`，
    /// 到第 8 层算出 208、而上限 210——差 2 点，「本层档位」一件都挑不出来，
    /// 货架退回全表，+23 的深渊之牙被摆进了第 8 层。两条各涨各的线一定会叉开，
    /// 而「本层的档位」只该有一个定义。
    /// </summary>
    public static int ShopPriceFloor(int floor) => ShopPriceBand(floor) / 3;

    /// <summary>
    /// 货架上「这一件在这一层标多少」的**唯一算式**。
    ///
    /// 以前有两个：档位筛选用「模板价 + 楼层 1.5」，真正摆上货架时又乘了一次浮动。
    /// 两个数一叉开，档位就失守：第 8 层的深渊之牙筛过的时候是 210（正好等于档位），
    /// 摆上来标成 244。**货架的形状由价决定，那价就只能有一个定义。**
    ///
    /// 这里刻意**不做**「越深越贵」的乘区。做过一次（第 15 层 ×1.84），
    /// 100 局的通关率从 70 掉到 33：抬价的另一面是「同样一笔钱买到的血更少」，
    /// 而这款游戏的深层考的是血（精英房一间的净掉血是上限的四到五成）。
    /// 那不是「钱有了出口」，那是把玩家的血换成了攻击力。
    /// 货架的上半边空着，比把它填满更接近这款游戏的平衡——这条是量出来的，不是拍的。
    /// </summary>
    public static int ShelfPriceAtFloor(ItemDef item, int floor) =>
        Math.Max(1, (int)Math.Round(item.Price + floor * 1.5));

    /// <summary>从第几层起，货架上会出现「比本层档位还贵」的那一格</summary>
    public const int RichShelfFloor = 6;

    /// <summary>
    /// 那一格出现的概率。**0 是量出来的**，不是没做。
    ///
    /// 第一版给了 0.5，100 局的结果是：通关 70 → **78**，平均死层 12.7 → 13.0，
    /// 「逛完还剩大钱」370 → 209 次。看着全是好消息，问题在钱从哪来——
    /// 泉眼献金从 380 次掉到 347，而「一次没献」的局从 14 涨到 18（通关 4 → 10）。
    /// 也就是说：买到那件贵货的人不再需要泉眼，没买到的人连泉眼的钱都花掉了。
    /// **同一笔钱被两个出口抢，最后决定结局的是掷骰**，不是玩家怎么打。
    ///
    /// 这一条和上一轮「货架抬价 ×1.84」是同一种错误的反面：那次是把血换成了攻击力，
    /// 这次是把「要不要攒钱」换成了「这家店摆没摆那一件」。所以留成 0：
    /// 货架的上半边空着，比把它填满更接近这款游戏的平衡——这一句现在有两个方向的数据撑着。
    ///
    /// 那件 `深渊之心`（模板价 560）因此仍然是「数据层里有、货架上没有」。
    /// 这不是待办，是结论：它进不了货架，是因为**这一档的价钱买不到它想换的东西**。
    /// 要么重定价（进档位池），要么等一个真正的出口（不是掷骰）。
    /// </summary>
    public const double RichShelfChance = 0.0;
}
