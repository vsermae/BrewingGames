// 程序化生成房间网格：在网格里放点 -> 连成树 -> 挑最远的点当 Boss。
// 树形结构保证任何房间都可达，玩家不会被困在孤岛里。
namespace AbyssEcho.Core;

public enum RoomType
{
    Start,
    Empty,
    Enemy,
    Treasure,
    Shop,
    Event,
    Altar,
    Training,
    Curse,
    Elite,
    Boss,
}

public sealed class Room
{
    public int X { get; set; }
    public int Y { get; set; }
    public RoomType Type { get; set; } = RoomType.Empty;
    public bool Cleared { get; set; }
    public bool Visited { get; set; }
    public List<int> Children { get; set; } = new();
}

public sealed class GridMap
{
    public int Width { get; init; }
    public int Height { get; init; }
    public List<Room> Rooms { get; init; } = new();
    public List<List<int>> Links { get; init; } = new();
    public int StartIndex { get; set; }
    public int BossIndex { get; set; }
}

public sealed class MapGenOptions
{
    public int Width { get; set; }
    public int Height { get; set; }
    public int MinRooms { get; set; }
    public Dictionary<string, int> RoomWeights { get; set; } = new();
    /// <summary>这一层补给的配额上限（商店 / 训练 / 祭坛合计）。见 MapGen.SupplyCap</summary>
    public int SupplyCap { get; set; } = 1;

    /// <summary>
    /// 这是第几层。`BattlesWanted` 要它——地图生成本身不知道层数，而
    /// 「这一层该有几间仗」是深度的函数。默认 1，与 `Floor` 未设时的旧行为一致。
    /// </summary>
    public int Floor { get; set; } = 1;
}

public static class MapGen
{
    public static readonly Dictionary<RoomType, string> RoomSymbol = new()
    {
        [RoomType.Start] = "起",
        [RoomType.Empty] = "·",
        [RoomType.Enemy] = "!",
        [RoomType.Treasure] = "$",
        [RoomType.Shop] = "商",
        [RoomType.Event] = "?",
        [RoomType.Altar] = "†",
        [RoomType.Training] = "☥",
        [RoomType.Curse] = "♁",
        [RoomType.Elite] = "⚑",
        [RoomType.Boss] = "☠",
    };

    public static readonly Dictionary<RoomType, string> RoomLabel = new()
    {
        [RoomType.Start] = "起点",
        [RoomType.Empty] = "空房间",
        [RoomType.Enemy] = "敌人房",
        [RoomType.Treasure] = "宝箱房",
        [RoomType.Shop] = "商店",
        [RoomType.Event] = "事件房",
        [RoomType.Altar] = "祭坛房",
        [RoomType.Training] = "训练房",
        [RoomType.Curse] = "诅咒房",
        [RoomType.Elite] = "精英房",
        [RoomType.Boss] = "Boss 房",
    };

    /// <summary>
    /// 每层的房间配额。写死在这里而不是数据里：这是「一层至少该有什么」的
    /// 结构性保证，不是可调参数——让玩家配权重配掉「这层没有商店」，
    /// 等于让他配出一个没法买东西的层。
    /// </summary>
    private const int GuaranteedEvents = 1;
    /// <summary>
    /// 补给房（商店 / 训练 / 祭坛）的总数上限，按「层」算而不是按「类」算。
    /// 早先三类各配各的配额、彼此不知道对方存在：加权抽取走到第一个商店就把剩下的空地全占了，
    /// 于是一层里永远只有一间补给房，训练房与祭坛房一次都没生成过。
    /// 商店的保底不在这里做——它挪到按权重抽完之后（见 AssignRoomTypes 第 3 步），
    /// 否则保底会先把这个配额占满，训练房与祭坛房照样一次都轮不到。
    /// </summary>
    private const int MaxSupply = 1;
    private const int MaxElites = 1;

    /// <summary>
    /// 这一层要有几间战斗房（普通 + 精英）。**跟着深度涨**，但只从第 6 层起涨。
    ///
    /// 定它的是一张死法分布：100 局横扫里死在第 6 层 2 局，而第 7~12 层**一局都没死**。
    /// 中层那段既没有浅层的教学压力，也没有深层的数值压力——它只是「走过去」。
    /// 赏金是 12 + 4×层、泉眼从第 6 层开，所以中层的钱在涨，仗却没有跟着涨。
    ///
    /// 三段：第 1~4 层 3 场（一场都不补，教学段保持原样）；
    /// 第 5~9 层 4 场；第 10 层起 5 场。补出来的只从空房里挑。
    /// 分界刻意压在 Boss 层（5 / 10）上：一层的仗多一场，是「这一层有 Boss」之外的另一种分量。
    /// </summary>
    public static int BattlesWanted(int roomCount, int floor)
    {
        var want = 3 + Math.Clamp(floor / 5, 0, 2);
        // 小图不硬凑：格子不够时上限也跟着降（一张 9 间房的图塞 5 场仗就只剩走路了）。
        // 第 6 层的「多一场」在 11 间房的图上还能容下（11 = 起点 + Boss + 4 场仗 + 5 间别的），
        // 再小就真的只剩走路了。
        if (roomCount < 11) want = Math.Min(want, 3);
        return Math.Min(want, Math.Max(1, roomCount - 4));   // 补给、宝箱、起点、Boss 各留一格
    }

    /// <summary>
    /// 这一层允许多少间补给房（商店 / 训练 / 祭坛合计）。
    /// 基础 1 间，往下每深 5 层多给 1 间——
    /// 因为「钱没处花」是一个跑出来的结论：中后段金币常年 1500+、
    /// 而一层最多一间商店，货架上四件加起来不到 200；精英 / 敌人房却一层四五间。
    /// 补给房密度不跟着深度走，深层就只剩「打」没有「选」。
    /// </summary>
    public static int SupplyCap(int floor) => 1 + Math.Max(0, (floor - 1) / 5);

    public static string RoomTypeKey(RoomType t) => t switch
    {
        RoomType.Start => "start",
        RoomType.Empty => "empty",
        RoomType.Enemy => "enemy",
        RoomType.Treasure => "treasure",
        RoomType.Shop => "shop",
        RoomType.Event => "event",
        RoomType.Altar => "altar",
        RoomType.Training => "training",
        RoomType.Curse => "curse",
        RoomType.Elite => "elite",
        RoomType.Boss => "boss",
        _ => "empty",
    };

    public static RoomType? RoomTypeFromKey(string key) => key switch
    {
        "start" => RoomType.Start,
        "empty" => RoomType.Empty,
        "enemy" => RoomType.Enemy,
        "treasure" => RoomType.Treasure,
        "shop" => RoomType.Shop,
        "event" => RoomType.Event,
        "altar" => RoomType.Altar,
        "training" => RoomType.Training,
        "curse" => RoomType.Curse,
        "elite" => RoomType.Elite,
        "boss" => RoomType.Boss,
        _ => null,
    };

    public static GridMap Generate(Rng rng, MapGenOptions opts)
    {
        var target = Math.Max(opts.MinRooms, (int)Math.Floor((double)opts.Width * opts.Height / 3));
        var occupied = new HashSet<string>();
        var rooms = new List<Room>();
        var links = new List<List<int>>();

        string Key(int x, int y) => $"{x},{y}";

        int AddRoom(int x, int y)
        {
            // 同一个格子只能有一个房间：重复登记会让「连接」指向一个看不见的幽灵房间，
            // 玩家看到的是「画着一条路却走不过去」（离屏自检就是这么抓到的）。
            if (occupied.Contains(Key(x, y)))
            {
                throw new InvalidOperationException($"地图重复登记了同一个格子 ({x},{y})");
            }
            var idx = rooms.Count;
            rooms.Add(new Room { X = x, Y = y, Type = RoomType.Empty });
            links.Add(new List<int>());
            occupied.Add(Key(x, y));
            return idx;
        }

        void Link(int a, int b)
        {
            if (!links[a].Contains(b)) links[a].Add(b);
            if (!links[b].Contains(a)) links[b].Add(a);
            rooms[a].Children.Add(b);
        }

        // 起点必须在内部格（1..w-2 / 1..h-2）：贴着边框的话「向外生长」会立刻撞墙，
        // 整张图就只剩起点一间房。地图小到没有内部格时退回中心。
        var startX = opts.Width >= 3 ? rng.Int(1, opts.Width - 2) : opts.Width / 2;
        var startY = opts.Height >= 3 ? rng.Int(1, opts.Height - 2) : opts.Height / 2;
        var startIdx = AddRoom(startX, startY);

        // 从已有房间出发，随机向空位生长，直到房间数达标。
        // 「这一步没长出来」不算失败：换个起点再试。原来的实现是掷 60 次就收工，
        // 结果是地图经常只长到一半——房间少、树也更短，玩家一晚上看到的都是小图。
        var attempts = 0;
        var stall = 0;
        while (rooms.Count < target && attempts++ < target * 400)
        {
            var from = rng.Int(0, rooms.Count - 1);
            var baseRoom = rooms[from];
            var dirs = rng.Shuffle(new[] { (1, 0), (-1, 0), (0, 1), (0, -1) });
            var grew = false;
            foreach (var (dx, dy) in dirs)
            {
                var nx = baseRoom.X + dx;
                var ny = baseRoom.Y + dy;
                if (nx < 0 || ny < 0 || nx >= opts.Width || ny >= opts.Height) continue;
                if (occupied.Contains(Key(nx, ny))) continue;
                var idx = AddRoom(nx, ny);
                Link(from, idx);
                grew = true;
                break;
            }
            // 连续很多次都长不出来，说明这张图已经满了（宽高给的格子用完了）
            if (!grew && ++stall > target * 40) break;
            if (grew) stall = 0;
        }

        // Boss 放在离起点最远的房间（BFS 距离最大），保证有一段探索路程
        var dist = BfsDistances(links, startIdx);
        var bossIndex = startIdx;
        for (var i = 0; i < rooms.Count; i++)
        {
            if (dist[i] > dist[bossIndex]) bossIndex = i;
        }

        rooms[startIdx].Type = RoomType.Start;
        rooms[startIdx].Visited = true;
        rooms[bossIndex].Type = RoomType.Boss;

        AssignRoomTypes(rng, rooms, opts.RoomWeights, startIdx, bossIndex, opts.SupplyCap, opts.Floor);

        return new GridMap
        {
            Width = opts.Width,
            Height = opts.Height,
            Rooms = rooms,
            Links = links,
            StartIndex = startIdx,
            BossIndex = bossIndex,
        };
    }

    private static void AssignRoomTypes(Rng rng, List<Room> rooms, Dictionary<string, int> weights, int startIdx, int bossIdx, int supplyCap, int floor)
    {
        var pool = weights
            .Where(kv => kv.Value > 0)
            .Select(kv => (Type: RoomTypeFromKey(kv.Key), Weight: kv.Value))
            .Where(p => p.Type is not null)
            .Select(p => (Type: p.Type!.Value, p.Weight))
            .ToList();
        if (pool.Count == 0) throw new InvalidOperationException("房间权重全为 0，无法生成楼层");

        var candidates = new List<int>();
        for (var i = 0; i < rooms.Count; i++)
        {
            if (i == startIdx || i == bossIdx) continue;
            candidates.Add(i);
        }
        // 洗牌候选格：保证类房间时按随机顺序挑位置，
        // 否则「商店永远在最左格」会变成玩家能背下来的规律。
        candidates = rng.Shuffle(candidates);

        int Count(RoomType t) => rooms.Count(r => r.Type == t);

        // 1) 先补保底：事件房与商店房各至少一个。
        //    放在加权抽取之前，权重再低也至少有得逛。
        //
        //    这里刻意用 while 而不是 for：早先写成「遍历候选格，遇到空的就放下，
        //    放够就 break」，于是函数体一次只会放一间房。商店的 max 恰好是 1，
        //    循环条件第一轮就成立，第二、三间房永远补不上；事件房同理，
        //    只在候选格奇少时才凑巧能放下第二间。
        void PlaceAtLeast(RoomType type, int want, int? max = null)
        {
            var limit = Math.Min(want, max ?? int.MaxValue);
            var have = Count(type);
            var i = 0;
            while (have < limit && i < candidates.Count)
            {
                var idx = candidates[i++];
                if (rooms[idx].Type != RoomType.Empty) continue;
                rooms[idx].Type = type;
                have += 1;
            }
        }
        PlaceAtLeast(RoomType.Event, GuaranteedEvents);

        bool IsSupply(RoomType t) => t is RoomType.Shop or RoomType.Training or RoomType.Altar;
        // 保底商店不能提前放：补给房共用一个配额，先放一间商店就等于把配额用光，
        // 后面按权重抽时 supplyPlaced 恒等于 MaxSupply —— 训练房与祭坛房的权重
        // （8 与 10）一次都轮不到，跑 450 层下来祭坛房出现 0 次。
        // 现在改成「先按权重抽，抽完发现这层没有商店再补一间」，配额留给抽签用。
        var supplyPlaced = rooms.Count(r => IsSupply(r.Type));

        // 2) 剩下空格按权重抽。精英房有上限（超过一个会把玩家按在地上摩擦）。
        //    补给房共用 MaxSupply 的配额：有了它，「一层里必有一间商店」这件事
        //    才真正成立；否则权重高的商店会把配额吃光，训练房与祭坛房的权重
        //    （8 与 10）永远轮不到空地。
        //
        //    配额满了要「重抽」，不能「跳过这一格」。以前是 `continue`——那等于把这
        //    一格白送给空房：跑 3000 层下来，每层平均有 0.76 次抽中了补给房却被丢掉，
        //    于是一层 9 个空格的抽签只兑现了 8.2 次，剩下的 0.8 格全落进空房，
        //    再被第 3 步兜底变成敌人房。玩家感受到的是「补给房稀、敌人房密」，
        //    而权重表上写的不是这样；更糟的是连空房都不够时（格子本来就少），
        //    同一层会出现「3 间敌人房」这种权重表里根本不该有的密度。
        //    重抽也顺手让上限不再扭曲其余房型的比例：被拦下的名额退还给同一次抽签
        //    的其它类型，而不是退给「空房间」。
        var elitePlaced = Count(RoomType.Elite);
        var maxRedraws = pool.Count * 4;
        foreach (var idx in candidates)
        {
            if (rooms[idx].Type != RoomType.Empty) continue;
            for (var attempt = 0; attempt < maxRedraws; attempt++)
            {
                var chosen = rng.Weighted(pool, p => p.Weight).Type;
                if (chosen == RoomType.Elite)
                {
                    if (elitePlaced >= MaxElites) continue;
                    elitePlaced += 1;
                }
                if (IsSupply(chosen))
                {
                    if (supplyPlaced >= supplyCap) continue;
                    supplyPlaced += 1;
                }
                rooms[idx].Type = chosen;
                break;
            }
        }

        // 3) 兜底一：商店是唯一「钱能花掉的地方」，一层没有商店等于金币变废纸。
        //    按权重抽完之后再看，抽到了就保留抽到的那间（商店/训练/祭坛都不缺），
        //    抽不到才补一间商店——这样配额不会被保底提前吃掉，
        //    同时「每层必有商店」这个结构性保证还在。
        //
        //    这一间是「配额之外」的：抽签抽到的是祭坛房时，配额已经用掉了，
        //    但玩家一层没商店就没法花钱——「一层必有商店」是硬保证，不能被配额让路。
        //    （试过让兜底也守配额，结果每层只剩 0.57 间商店，
        //    也就是「一层有商店」这条保证直接破了。配额不是目的，买东西的地方才是。）
        //    所以补给房配额的上界是「抽签至多一间，加兜底的一间商店」，
        //    自检里那条恒等式要按这个下界写，不能按 MaxSupply 写死。
        if (Count(RoomType.Shop) == 0)
        {
            foreach (var idx in candidates)
            {
                if (rooms[idx].Type != RoomType.Empty) continue;
                rooms[idx].Type = RoomType.Shop;
                supplyPlaced += 1;
                break;
            }
        }

        // 4) 兜底二：深层补一场仗。
        //
        //    横扫 100 局的现场（第 59 轮）：`死在第 6 层 2 局`，而第 7~12 层**一局都没死**。
        //    那不是「中层很安全」，是中层没东西：一张图 3.4 间仗（浅层也是 3.4），
        //    而赏金是按层涨的（12 + 4×层），于是中层变成一段「走两步就下楼」的空跑。
        //    深层的钱有出口（泉眼、货架），中层的仗却一直没跟着深度长。
        //
        //    配额按 `BattlesWanted(floor)` 给：第 1~5 层不补（教学段保持 3 场上下），
        //    第 6 层起每 5 层多一场，封在 2。补出来的这两间从**空房**里挑，
        //    不动补给、宝箱、事件的配额——那些是「深一层该多出来的东西」，
        //    而这里要补的恰是「深一层该多出来的路」。
        var want = BattlesWanted(rooms.Count, floor);
        var have = Count(RoomType.Enemy) + Count(RoomType.Elite);
        if (have < want)
        {
            foreach (var idx in candidates)
            {
                if (have >= want) break;
                if (rooms[idx].Type != RoomType.Empty) continue;
                rooms[idx].Type = RoomType.Enemy;
                have += 1;
            }
        }

        // 5) 兜底三：权重全落到空房上时（极少数分布），补几个敌人房让楼层可玩
        var emptyLeft = Count(RoomType.Empty);
        if (emptyLeft > 0 && Count(RoomType.Enemy) == 0)
        {
            foreach (var idx in candidates)
            {
                if (emptyLeft <= 0) break;
                if (rooms[idx].Type != RoomType.Empty) continue;
                rooms[idx].Type = RoomType.Enemy;
                emptyLeft -= 1;
            }
        }
    }

    /// <summary>BFS 求到每个房间的最短步数，不可达为 -1</summary>
    public static int[] BfsDistances(List<List<int>> links, int from)
    {
        var dist = new int[links.Count];
        Array.Fill(dist, -1);
        dist[from] = 0;
        var queue = new Queue<int>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var next in links[cur])
            {
                if (dist[next] != -1) continue;
                dist[next] = dist[cur] + 1;
                queue.Enqueue(next);
            }
        }
        return dist;
    }

    public static bool IsAdjacent(Room a, Room b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) == 1;

    public static bool CanMove(GridMap map, int from, int to) => map.Links[from].Contains(to);

    public static int RoomAt(GridMap map, int x, int y) => map.Rooms.FindIndex(r => r.X == x && r.Y == y);
}
