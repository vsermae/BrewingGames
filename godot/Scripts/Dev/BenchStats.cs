// 帧耗与绘制规模统计：给「更小、更快、更省资源」一个能对照的数，而不是一句形容词。
//
// 为什么在引擎里量而不是外面掐表：启动时间包含 .NET 运行时与资源加载，
// 外部只能量到一个总时长，量不出「哪一帧卡」。这里记录首帧时刻、
// 稳定后的帧间隔分位数，以及场景树与 CanvasItem 的数量——
// 后两项是「省资源」最直接的代理指标（Electron 版光一个 Chromium 就上千个 DOM 节点）。
namespace AbyssEcho.Dev;

using Godot;

public static class BenchStats
{
    /// <summary>从进程启动到第一帧渲染完成的毫秒数（不含 .NET 宿主启动，那部分看外部计时）</summary>
    public static double FirstFrameMs { get; private set; } = -1;

    private static readonly List<double> Frames = new();

    public static bool Enabled { get; private set; }
    public static string Report { get; set; } = "";

    public static void MarkFirstFrame()
    {
        if (FirstFrameMs >= 0) return;
        FirstFrameMs = Time.GetTicksMsec();
    }

    public static void Start() => Enabled = true;

    public static void RecordFrame(double delta)
    {
        if (!Enabled || Frames.Count > 6000) return;
        Frames.Add(delta * 1000.0);
    }

    /// <summary>统计场景树规模并生成报告（--bench 结束时调用）</summary>
    public static string Build(Node root)
    {
        var nodes = CountNodes(root, out var canvasItems);
        var sorted = Frames.OrderBy(x => x).ToList();
        double Pct(double p) => sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * p))];
        var avg = sorted.Count == 0 ? 0 : sorted.Average();
        Report = string.Join('\n', new[]
        {
            $"首帧耗时      {FirstFrameMs:F0} ms（进程启动→第一帧）",
            $"采样帧数      {sorted.Count}",
            $"帧间隔 平均   {avg:F2} ms（约 {1000.0 / Math.Max(0.01, avg):F0} fps）",
            $"帧间隔 p50    {Pct(0.50):F2} ms",
            $"帧间隔 p95    {Pct(0.95):F2} ms",
            $"帧间隔 p99    {Pct(0.99):F2} ms",
            $"节点数        {nodes}",
            $"CanvasItem    {canvasItems}（Electron 版的对照物是 DOM 节点数）",
            $"静态内存      {GC.GetTotalMemory(false) / 1024 / 1024} MB（托管堆）",
        });
        return Report;
    }

    private static int CountNodes(Node node, out int canvasItems)
    {
        var n = 1;
        canvasItems = node is CanvasItem ? 1 : 0;
        foreach (var child in node.GetChildren())
        {
            n += CountNodes(child, out var ci);
            canvasItems += ci;
        }
        return n;
    }
}
