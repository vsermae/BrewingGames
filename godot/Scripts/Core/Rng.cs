// 可复现的随机数：同一 seed 产生同一序列，方便测试与重开同一种子。
//
// 逐位照搬 TS 版的 xorshift32 + splitmix32 种子打散：
// 这样同一 seed 在 TS 与 C# 里生成的地图、伤害序列完全一致，
// 迁移是不是「功能等价」才有一个能对照的标尺（见 Tests/SelfCheck.cs）。
namespace AbyssEcho.Core;

public sealed class Rng
{
    private uint _state;

    public Rng(uint? seed = null)
    {
        var s = seed ?? (uint)(Environment.TickCount64 & 0xFFFFFFFF);
        // Seed 以前根本没被赋值，永远是 0：存档里写的一直是 0，读档靠它重建地图，
        // 于是「同一个种子长出同一张图」只在内存里成立，落盘之后就断了。
        Seed = s;
        _state = HashSeed(s);
        if (_state == 0) _state = 1;
    }

    public uint Seed { get; }

    /// <summary>
    /// 当前内部状态。存档要存它而不是只存 Seed：
    /// 只存种子的话，读档后随机序列会从头重来——商店换货、掉落重复、
    /// 战斗里的每一次判定都和上次一模一样，存档就成了读心器。
    /// </summary>
    public uint State
    {
        get => _state;
        set => _state = value == 0 ? 1 : value;
    }

    /// <summary>[0, 1)</summary>
    public double Next()
    {
        // xorshift32
        var x = _state;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _state = x;
        return _state / 4294967296.0;
    }

    /// <summary>[min, max] 整数</summary>
    public int Int(int min, int max) => min + (int)Math.Floor(Next() * (max - min + 1));

    /// <summary>0~99 的整数：敌人 AI 的三选一按百分制掷骰</summary>
    public int Percent() => (int)Math.Floor(Next() * 100);

    /// <summary>[min, max) 浮点</summary>
    public double Range(double min, double max) => min + Next() * (max - min);

    public T Pick<T>(IReadOnlyList<T> arr) => arr[Int(0, arr.Count - 1)];

    public bool Chance(double p) => Next() < p;

    /// <summary>按 weight 字段加权抽取</summary>
    public T Weighted<T>(IReadOnlyList<T> arr, Func<T, double> weight)
    {
        var total = arr.Sum(weight);
        var r = Next() * total;
        foreach (var item in arr)
        {
            r -= weight(item);
            if (r <= 0) return item;
        }
        return arr[^1];
    }

    /// <summary>洗牌（返回新数组，不改入参）</summary>
    public List<T> Shuffle<T>(IReadOnlyList<T> arr)
    {
        var outList = new List<T>(arr);
        for (var i = outList.Count - 1; i > 0; i--)
        {
            var j = Int(0, i);
            (outList[i], outList[j]) = (outList[j], outList[i]);
        }
        return outList;
    }

    /// <summary>splitmix32：把任意种子（含小整数）打散成分布良好的初始状态</summary>
    private static uint HashSeed(uint seed)
    {
        var x = seed + 0x9e3779b9u;
        x = (uint)((x ^ (x >> 16)) * 0x21f0aaad);
        x = (uint)((x ^ (x >> 15)) * 0x735a2d97);
        return x ^ (x >> 15);
    }

    public static Rng FromSeed(uint seed) => new(seed);
}
