// 浮动提示（toast）的队列规则 + 落点。
//
// 这里全是纯 C#：进队、出队、「队首演完了该接谁」、提示弹在屏幕的哪个高度、
// 同一条重复消息在多久之内只弹一次。没有一条需要 Godot。
// 为什么值得单独拆：TS 版在这件事上翻过车——把「播完了」寄托在淡出回调上，
// 场景一重启回调就永远不来，队列卡死在「有一条正在演」，之后所有提示都被静默吞掉。
// 所以「谁在演」必须是可断言的状态，而不是某个动画的生命周期。
namespace AbyssEcho.UI;

using Godot;

public sealed class ToastItem
{
    public string Text { get; init; } = "";
    public Color? Tint { get; init; }
    public float Duration { get; init; } = ToastRules.DefaultDuration;
}

public static class ToastRules
{
    public const float DefaultDuration = 1.5f;

    /// <summary>同一条提示的节流窗口：按住方向键会以几十次/秒重发 keydown，不去重就攒一屏「那边没有路」</summary>
    public const float RepeatWindowSeconds = 0.45f;

    /// <summary>胶囊与底部提示条之间留的一道缝</summary>
    public const float GapAboveHint = 14f;

    /// <summary>
    /// 胶囊高度：17px 的字 + 上下各 7.5px 的气。
    ///
    /// 46 那一版是拍出来的：1280×720 的地图上，HUD 下沿（74）到网格上沿（119）
    /// 之间一共 45px，一个 46px 的胶囊**哪儿都塞不下**——于是它只能压在 HUD 上。
    /// 胶囊是「一句话的反馈」，不是一块面板：32px 才是它该有的体量。
    /// 再往上飘 34px（见 `RiseTotal`），整条飞行区间还要 66px 的空带。
    /// </summary>
    public const float PillHeight = 32f;

    /// <summary>
    /// 提示胶囊的**底边**该落在哪条线上（屏幕高度 - 提示条高 - 一道缝）。
    /// 上一版给的是中线，`_Draw` 又从中线往下 5px 铺 46px，于是整块胶囊
    /// 比算出来的位置低 29px，1280×720 下底边直接压进最后一排地砖。
    /// </summary>
    /// <param name="hintBandHeight">底部提示条的高度（场景各自知道），0 表示这条场景没有提示条</param>
    public static float PillBottomFor(float viewportHeight, float hintBandHeight = 0f)
    {
        var h = Math.Max(0f, viewportHeight);
        return h - Math.Max(0f, hintBandHeight) - GapAboveHint;
    }

    /// <summary>胶囊顶边（给断言 / 截图工具用：它要跟房间砖逐块比）</summary>
    public static float PillTopFor(float viewportHeight, float hintBandHeight = 0f) =>
        PillBottomFor(viewportHeight, hintBandHeight) - PillHeight;

    /// <summary>胶囊与它上下两条东西之间留的气。两侧都留，所以一条空带要 PillHeight + 2×Clearance 才够。</summary>
    public const float Clearance = 6f;

    /// <summary>淡入时往上升多少（飘起来的那一下）</summary>
    public const float RiseIn = 12f;

    /// <summary>淡出时再往上升多少（飘走的那一下）</summary>
    public const float RiseOut = 22f;

    /// <summary>
    /// 胶囊整个生命周期里**往上飘**多少。
    ///
    /// 落点必须把这一截算进去，否则「静态位置在 HUD 下面」不等于「飘起来还在 HUD 下面」——
    /// 截图只拍动画里的某一帧，飘到最高点那一帧压在 HUD 上的样子拍不到，也就没人修。
    /// ToastLayer 的 `_rise` 也由这两个常量拼出来，两边不许各写一遍。
    /// </summary>
    public const float RiseTotal = RiseIn + RiseOut;

    /// <summary>
    /// 一条「放得下提示」的空带要多少高：胶囊自己 + 它飘起来的那截 + 两侧的气。
    /// （窄带子里 `PillTopFor` 会自己把上飘量收小，见那里的注释。）
    /// 地图按它给提示让出一条带子（见 `MapMetrics.ToastBand`）——
    /// 1280×720 下没有这条带子时，HUD 下沿（74）到网格上沿（119）只有 45px，
    /// 一个会飘的胶囊哪儿都塞不下，于是它只能压在 HUD 上。
    /// </summary>
    public const float BandHeight = PillHeight + RiseTotal + Clearance * 2f;

    /// <summary>
    /// 胶囊顶边：挂在地图网格**下面**——除非下面根本没有地方。
    ///
    /// `gridBottom` 是这一帧「最后一行房间砖 / 右下角那块图例」的下沿。
    /// 1280×720 下网格画到 629、图例画到 662，而底部提示条的上沿在 674：
    /// 中间只剩 12px，一个胶囊哪儿都塞不下。
    ///
    /// 所以这条规则是：底下塞得下就塞底下；塞不下就**挪到网格上面**那块空地里
    /// （地图是垂直居中的，上方一直是空的）；连上方也没有（超大网格）时，
    /// 才退回贴着提示条——那是有遮挡的一档，但没有办法再好了。
    /// </summary>
    /// <param name="belowFloor">地图自己画的东西里，最靠下的那条边（网格下沿 / 右下图例）
    /// 之上就已经没有空地了——这一层再往下只有提示条，所以它是「底下」的净空起点。</param>
    /// <param name="hudBottom">顶部 HUD 的下沿。它是「上方那条空带」真正的上边界——
    /// 缺了它，胶囊就会按「离网格多远」算，算出来的位置落在 HUD 里面。</param>
    public static float PillTopFor(float viewportHeight, float hintBandHeight, float belowFloor,
        float gridTop = 0f, float hudBottom = 0f)
    {
        var h = Math.Max(0f, viewportHeight);
        var aboveBand = PillBottomFor(h, hintBandHeight) - PillHeight;
        var below = belowFloor + Clearance;
        // 一、网格与提示条之间塞得下就塞底下——提示本来该在玩家视线落点附近
        if (below + PillHeight <= aboveBand) return below;
        // 二、塞不下就挪到网格上方那条空带里，**把整条飞行区间都装进带子**。
        //
        // 这条带子的**上边界是顶部 HUD 的下沿，不是网格的上沿**。
        // 上一版只拿 `gridTop` 当边界（「离网格 Clearance」），于是 1280×720 下
        // 算出来 59，而 HUD 占到 74——胶囊 47..93 有一大半压在 HUD 面板上，
        // 它的描边正好横穿 HUD 那条贴边发光线。判据只查了「压没压到砖与图例」，
        // HUD 不算，于是这一屏一直绿着。
        //
        // 胶囊还会**往上飘**（`ToastRules.RiseTotal`）：淡入升 12px、淡出再升 22px。
        // 只按静态矩形留位置的话，飘起来的那一部分照样穿进 HUD——
        // HUD 下沿 74、静态胶囊 69 已经贴着了，飘 12px 就是 57。
        var bandTop = hudBottom + Clearance;
        var bandBottom = gridTop - Clearance;
        var span = PillHeight + RiseTotal;
        // 顶边取「贴着带子上沿往下放」：带子上沿就是 HUD 下沿 + 气，
        // 于是飞行区间的顶边（`PillTop - RiseTotal`）正好落在带上沿——
        // 离 HUD 最远、离网格也还有 `bandBottom - bandTop - span` 的余量。
        // 早先这里是「在带子里居中」：带子比胶囊高时居中会把整颗往下推，
        // 胶囊就骑在网格第一行砖上（158 的带子里算出来顶边 117，砖在 146）。
        if (bandBottom - bandTop >= span)
            return bandTop + RiseTotal;
        // 带子不够「胶囊 + 上飘」那么多：动效自己收，不准许它去压 HUD。
        //
        // 上一版这里是「贴着带子底部摆」——带宽 54 时胶囊顶边 74、飘起来到 40，
        // 正好穿进 HUD。截图 06 当场红了。
        // 「带子放不下」是**布局给的额度**，不是「动效可以越界」：
        // 额度小就少飘一点，这比让一段装饰动画去盖住玩家的血量诚实。
        // 额度 = 带子够装胶囊之后还剩多少，但不能超过设计的上飘量。
        var rise = Math.Clamp(bandBottom - bandTop - PillHeight - Clearance, 0f, RiseTotal);
        if (bandBottom - bandTop > 0f) return bandBottom - PillHeight - rise;
        // 三、两头都没地方：贴着提示条，宁可压住一点画面，也不许出屏
        return Math.Clamp(aboveBand, 0f, MathF.Max(0f, h - PillHeight));
    }

    /// <summary>同一条消息是否还在节流窗口内（窗口内不再弹第二条）</summary>
    public static bool ShouldSuppress(string text, string? lastText, float sinceSeconds) =>
        lastText is not null && lastText == text && sinceSeconds < RepeatWindowSeconds;
}

/// <summary>
/// 队列本身。界面层只管「把队首画出来」，这里管「谁是队首、什么时候换人」。
/// 场景被销毁时必须调 Reset()：不重置的话，下一次进同一场景会以为还有一条在演。
/// </summary>
public sealed class ToastQueue
{
    private readonly List<ToastItem> _items = new();
    private bool _playing;

    public int Pending => _items.Count;
    public bool Playing => _playing;
    public ToastItem? Current => _playing && _items.Count > 0 ? _items[0] : null;

    /// <summary>入队。返回 true 表示这条应该立刻开始演（队列本来是空的）</summary>
    public bool Enqueue(ToastItem toast)
    {
        _items.Add(toast);
        return !_playing;
    }

    /// <summary>队首演完了：出队；还有剩的继续演，没有就停下</summary>
    public void Dequeue()
    {
        if (_items.Count > 0) _items.RemoveAt(0);
        _playing = _items.Count > 0;
    }

    /// <summary>标记「开始演队首」。队列空时返回 false，调用方不该去画</summary>
    public bool Begin()
    {
        if (_items.Count == 0)
        {
            _playing = false;
            return false;
        }
        _playing = true;
        return true;
    }

    public void Reset()
    {
        _items.Clear();
        _playing = false;
    }
}
