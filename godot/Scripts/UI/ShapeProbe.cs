// 记账笔：不是画布，是「数笔画」的画布。
//
// 为什么需要它：图标画得好不好看只能靠眼睛，但「这个图标到底有没有自己的造型」
// 是可以断言的——之前有 case 名字没对上，运行时静默落进 default 画了个空心菱形，
// 界面照样有东西，没人发现。数笔画就能发现。
//
// 做成 Painter 的外部记录器而不是 CanvasItem 子类：Godot 的 CanvasItem
// 派生类会被源生成器要求无参构造 + partial，那套约束不该污染一个纯测试辅助。
namespace AbyssEcho.UI;

using Godot;

/// <summary>一支「只数笔画」的绘图板：Painter 自己判断底层是不是它在记账</summary>
public sealed class ShapeProbe
{
    /// <summary>落了多少笔（填充 / 描边 / 直线）</summary>
    public int Ops { get; private set; }

    /// <summary>命中默认分支的次数——非 0 就说明这个图标没有专属造型</summary>
    public int DefaultHits { get; private set; }

    /// <summary>所有笔画覆盖到的包围盒，用来验证「原点居中」这条契约</summary>
    public float MinX { get; private set; } = float.MaxValue;
    public float MaxX { get; private set; } = float.MinValue;
    public float MinY { get; private set; } = float.MaxValue;
    public float MaxY { get; private set; } = float.MinValue;

    /// <summary>标记一次「掉进了 default」</summary>
    public void HitDefault() => DefaultHits++;

    /// <summary>记一笔，并把点算进包围盒</summary>
    public void Count(Vector2 p)
    {
        Ops++;
        MinX = Mathf.Min(MinX, p.X);
        MaxX = Mathf.Max(MaxX, p.X);
        MinY = Mathf.Min(MinY, p.Y);
        MaxY = Mathf.Max(MaxY, p.Y);
    }
}
