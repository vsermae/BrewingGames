// 房间流程探针：玩家「想用这间房」和「还没决定就走开」之后，这间房分别还在不在。
//
// 结论应该是：用了才消失，没决定就还在。以前宝箱/事件/祭坛/训练/诅咒五类房
// 都是**先进门就清房**，于是玩家只是在面板上点了「走开 / 什么都不做 / 转身离开」，
// 这间房就永远消失了——而清房这件事没有任何提示，地图上只是「它已经空了」。
namespace AbyssEcho.Dev;

using Godot;
using AbyssEcho.Core;
using AbyssEcho.Data;

public static class RoomFlowProbe
{
    /// <summary>
    /// 「进门就清房」在代码里长这个样子：OpenXxx 的开头一句就 ClearRoom。
    /// 这条探针读源码做静态检查——它不依赖运气，也不依赖某个 seed 恰好抽到那间房。
    /// 导出的二进制里没有 .cs，所以它只在开发环境跑（自检在 CI 上也跑源码树）。
    /// </summary>
    public static int Run()
    {
        GameDataStore.Get();
        var path = ProjectSettings.GlobalizePath("res://Scripts/Scenes/MapScene.cs");
        if (!Godot.FileAccess.FileExists(path))
        {
            GD.Print("  [skip] 找不到 MapScene.cs（导出产物里没有源码），这条静态检查跳过");
            return 0;
        }
        var src = Godot.FileAccess.GetFileAsString(path);
        var bad = 0;
        foreach (var (entry, label) in new[]
                 {
                     ("private void OpenTreasure(int index)", "宝箱房"),
                     ("private void OpenEvent(int index)", "事件房"),
                     ("private void OpenAltar(int index)", "祭坛房"),
                     ("private void OpenTraining(int index)", "训练房"),
                     ("private void OpenCurseRoom(int index)", "诅咒房"),
                 })
        {
            var at = src.IndexOf(entry, StringComparison.Ordinal);
            if (at < 0)
            {
                bad++;
                GD.Print($"  [!!] 找不到 {entry}：这条检查已经失去目标了");
                continue;
            }
            // 判据只认「函数体开头这几十行里、缩进最浅的那一句 ClearRoom」：
            // 那就是「一进门就清房」的形状。写在局部回调 `Used() { ... }` 里的
            // ClearRoom 缩进更深一层，不该算进来——那正是「用了才清」的写法。
            var head = src.Substring(at, Math.Min(240, src.Length - at));
            var direct = head.Split('\n')
                .Skip(1)
                .TakeWhile(line => !line.StartsWith("    private ", StringComparison.Ordinal))
                .Any(line => line.StartsWith("        _run.ClearRoom", StringComparison.Ordinal));
            if (direct)
            {
                bad++;
                GD.Print($"  [!!] {label}：进门就 ClearRoom，玩家「走开」之后这间房就没了");
            }
        }
        // 反过来的那一半：这几类房**必须**真的落到「已清空」上。
        // 事件房以前两头都错：进门时清房那句被删掉了（于是永远不清空，
        // 同一间房可以反复掷同一口井），而界面上又写着「选了才算发生」。
        // 现在清房挪到 ResolveEvent 的开头；这里钉住「它确实清了」。
        // 只查「自己负责清房」的这一处：宝箱房把清房交给调用方（拿走才清、
        // 「算了」也要清），背包满的换装面板同理——它们调的是传进来的回调，
        // 静态文本里看不出来，写进来只会变成噪音。
        foreach (var (entry, label) in new[]
                 {
                     ("private void ResolveEvent(", "事件房"),
                 })
        {
            var at = src.IndexOf(entry, StringComparison.Ordinal);
            if (at < 0)
            {
                bad++;
                GD.Print($"  [!!] 找不到 {entry}：这条检查已经失去目标了");
                continue;
            }
            // 函数体（到下一条「4 空格缩进的 private」为止）里必须出现过 ClearRoom：
            // 不出现就等于「这间房永远可以被反复使用」。
            var bodyStart = at;
            var nextPrivate = src.IndexOf("\n    private ", bodyStart + 8, StringComparison.Ordinal);
            var body = nextPrivate < 0 ? src[bodyStart..] : src[bodyStart..nextPrivate];
            if (!body.Contains("_run.ClearRoom()"))
            {
                bad++;
                GD.Print($"  [!!] {label}：做完选择之后没有清房，同一间房可以反复进");
            }
        }
        GD.Print($"\n房间流程探针：{bad} 类房间的房间流程不对（应为 0）");
        return bad == 0 ? 0 : 1;
    }
}
