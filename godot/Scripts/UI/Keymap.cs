// 键位表：帮助面板与输入处理共用同一份。
//
// TS 版在这里踩过一次：说明里写了「空格等待」「. 下楼」「M 静音」，
// 而输入处理只接了其中一半——玩家照着说明按键没反应，会以为是游戏卡了。
// 所以键位表必须是数据，帮助面板直接渲染它，加键位只改这一处。
namespace AbyssEcho.UI;

public static class Keymap
{
    public sealed record Binding(string Keys, string Action);

    /// <summary>地图与全局键位（顺序即帮助面板的显示顺序）</summary>
    public static readonly Binding[] Map =
    {
        new("WASD / 方向键", "在相邻房间之间移动"),
        new("E / 回车", "进入当前房间（敌人房自动开战）"),
        new("空格", "原地等待；Boss 出场演出中可跳过"),
        new("> 或 .", "本层清空后下楼 / 继续深潜"),
        new("I", "打开 / 关闭背包"),
        new("H", "这份操作说明"),
        new("M", "开关音效"),
        new("F11", "全屏 / 退出全屏"),
        new("Esc", "关闭面板 / 暂停菜单"),
    };

    /// <summary>战斗键位</summary>
    public static readonly Binding[] Battle =
    {
        new("1 ~ 9", "释放对应技能"),
        new("A", "普攻"),
        new("D", "防御"),
        new("Z", "使用消耗品（第一件可用的）"),
        new("V", "开关战斗特效"),
        new("H", "这份操作说明"),
        new("M", "开关音效"),
        new("F11", "全屏 / 退出全屏"),
        new("Esc", "逃跑（房间不会被清空）"),
    };

    /// <summary>
    /// 战斗场景里真的接了的单字符键。
    /// 它和上面那张表是两份数据，故意的：说明写了一个键、代码没接，就是「说了做不到」。
    /// UiSmoke 会核对「表里承诺的每个单字符键都在这份登记里」——
    /// 这条以前是靠读源码做的，但导出的二进制里没有 .cs，在产物上必然红。
    /// </summary>
    public static readonly char[] BattleWiredKeys = { 'A', 'D', 'Z', 'V', 'H', 'M' };

    public static IEnumerable<string> AllKeys() => Map.Concat(Battle).Select(b => b.Keys);
}
