// 界面上「动一下」的那一套：时长、曲线、以及几种同一个模子做出来的动作。
//
// 起因是散着写的时候，每个动效都自己拍一组数字：提示飘 12+22ms、
// Boss 名字渐隐 260ms、碎片飞散 Cubic.In、立绘入场 Back.Out……
// 单看每一处都不错，接在一起看就是「有的地方急、有的地方拖」。
// 顺滑不是「更多动画」，是**相邻的两个动作说同一种时间**。
//
// 所以这里只做三件事：
//   1) 把时长收成几档（快 / 中 / 慢），场景里不再出现裸的毫秒数；
//   2) 给出「同一种手感」的曲线（进：减速停稳；出：加速离场）；
//   3) 给出几个现成的动效——淡入、浮入、脉动、数值滚动——让调用方说的
//      是「浮进来」而不是「从 y+12 到 y 用 Cubic.Out 花 180ms」。
namespace AbyssEcho.UI;

using Godot;

public static class Motion
{
    /// <summary>一档时长：快。按钮按下、图标闪一下</summary>
    public const float Fast = 0.12f;

    /// <summary>一档时长：中。面板浮进来、提示飘走、数值滚动</summary>
    public const float Base = 0.22f;

    /// <summary>一档时长：慢。整屏的演出（出场、收场）</summary>
    public const float Slow = 0.42f;

    /// <summary>一条「进」手感的补间：起步快、落点稳。所有「东西出现」都用它</summary>
    public static Tween In(CanvasItem node)
    {
        var t = node.CreateTween();
        t.SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        return t;
    }

    /// <summary>一条「出」手感的补间：起步慢、后段加速。用在「它要走」</summary>
    public static Tween Out(CanvasItem node)
    {
        var t = node.CreateTween();
        t.SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
        return t;
    }

    /// <summary>
    /// 浮入：从下方 10px 处淡进来。
    ///
    /// 「淡入」单独用会让东西像是「原地显影」，少了「它落在哪儿」这个信息；
    /// 位移不用大，10px 就够读出方向。
    /// </summary>
    /// <param name="delay">序列里排队用的延迟（同一屏几块依次浮现时用）</param>
    public static void RiseIn(Control node, float seconds = Base, float delay = 0f, float from = 10f)
    {
        node.Modulate = new Color(1, 1, 1, 0);
        var home = node.Position;
        node.Position = home + new Vector2(0, from);
        var t = node.CreateTween().SetParallel(true)
            .SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        t.TweenProperty(node, "modulate:a", 1.0, seconds).SetDelay(delay);
        t.TweenProperty(node, "position", home, seconds).SetDelay(delay);
    }

    /// <summary>
    /// 纯淡入：只动透明度，不动位置。
    ///
    /// 有它是因为 `RiseIn` 不总是安全的：那个动作假设「这个控件的位置由它自己说了算」，
    /// 而容器（`CenterContainer` / `VBox` / `PanelContainer`）里每个子节点的位置都是
    /// 容器每次重排时算出来的——重排一次，`RiseIn` 的起点和终点一起作废。
    /// 容器里的东西只能用这个。
    /// </summary>
    public static void FadeIn(CanvasItem node, float seconds = Base, float delay = 0f)
    {
        if (!GodotObject.IsInstanceValid(node)) return;
        node.Modulate = new Color(node.Modulate, 0f);
        In(node).TweenProperty(node, "modulate:a", 1.0, seconds).SetDelay(delay);
    }

    /// <summary>淡出并收掉：先淡，再 QueueFree。**不在淡出中途释放**，那会「闪一下不见」</summary>
    public static void FadeOutAndFree(CanvasItem node, float seconds = Fast)
    {
        if (!GodotObject.IsInstanceValid(node)) return;
        var t = Out(node);
        t.TweenProperty(node, "modulate:a", 0.0, seconds);
        t.TweenCallback(Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(node)) node.QueueFree();
        }));
    }

    /// <summary>
    /// 呼吸：让一块静态的东西「活着」，但不抢注意力。
    /// 幅度默认只有 4%，周期 2.4s——比一次心跳慢，比一次眨眼快。
    /// </summary>
    public static LoopTween Breathe(CanvasItem node, float amplitude = 0.04f, float period = 2.4f, float delay = 0f)
        => new(node, amplitude, period, delay);
}

/// <summary>
/// 一条来回摆的补间（呼吸、脉动）。
///
/// 单独包一层而不是让每个调用方自己写 `SetLoops()`：循环补间最容易写错的两处是
/// 「一次之后要不要反着再来」和「相位错开」——错了以后不报错，只是节奏不齐。
/// </summary>
public sealed class LoopTween
{
    internal LoopTween(CanvasItem node, float amplitude, float period, float delay)
    {
        var t = node.CreateTween().SetLoops();
        t.SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
        if (delay > 0f) t.TweenInterval(delay);
        t.TweenProperty(node, "modulate:a", 1f - Mathf.Clamp(amplitude, 0f, 0.6f), period * 0.5f);
        t.TweenProperty(node, "modulate:a", 1f, period * 0.5f);
        Tween = t;
    }

    public Tween Tween { get; }

    public void Kill()
    {
        if (Tween.IsValid()) Tween.Kill();
    }
}
