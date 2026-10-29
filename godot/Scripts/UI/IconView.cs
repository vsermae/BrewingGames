// 一个图标控件：把 IconArt 的造型画进任意容器里。
//
// 为什么需要它：地图上房间格已经是「图形不是字符」了，但面板里（奖励卡、
// 装备格、升级弹窗、Boss 出场）原来只有一个标题。TS 版这些地方都配了图标，
// 面板看起来才不像一串表单。造型本身在 IconArt 里，这里只负责摆尺寸与着色。
namespace AbyssEcho.UI;

using Godot;

public partial class IconView : Control
{
    public string Kind = "rune";
    public Color Tint = Colors.White;
    /// <summary>图案占控件的比例：0.7 左右留一圈呼吸空间，1.0 会顶满边</summary>
    public float Fill = 0.82f;

    public IconView()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public static IconView Of(string kind, float size, Color? tint = null)
    {
        var v = new IconView { Kind = kind, Tint = tint ?? Colors.White, CustomMinimumSize = new Vector2(size, size) };
        v.Size = new Vector2(size, size);
        return v;
    }

    public override void _Draw()
    {
        if (Size.X <= 1f || Size.Y <= 1f) return;
        var c = Size * 0.5f;
        var d = Mathf.Min(Size.X, Size.Y) * Fill;
        DrawSetTransform(c, 0, Vector2.One);
        IconArt.Draw(this, Kind, d, Tint);
        DrawSetTransform(Vector2.Zero, 0, Vector2.One);
    }
}
