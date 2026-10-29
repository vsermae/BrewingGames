// 战斗日志的着色与分类。纯文本判断，headless 可断言。
namespace AbyssEcho.UI;

using Godot;

public static class BattleLogRules
{
    /// <summary>玩家白、敌人浅红、状态黄、暴击金、治疗绿、Boss 紫红</summary>
    public static Color ColorOf(string kind) => kind switch
    {
        "enemy" => new Color("ff6666"),
        "status" => new Color("ffdd44"),
        "crit" => new Color("ffd700"),
        "heal" => new Color("66ff66"),
        "boss" => new Color("cc66ff"),
        _ => new Color("ffffff"),
    };

    /// <summary>一行日志按内容分类：纯文本战斗里最省事也最可靠的做法</summary>
    public static string Classify(string line)
    {
        if (HasAny(line, "领域", "岩浆壳", "冰镜", "BOSS", "Boss")) return "boss";
        if (HasAny(line, "暴击", "大招", "全力以赴")) return "crit";
        if (HasAny(line, "中毒", "灼烧", "冰封", "毒素", "冰冻", "麻痹", "强化", "防御姿态", "闪避")) return "status";
        if (HasAny(line, "回复", "治疗", "治愈", "汲取") && !line.Contains("攻击你")) return "heal";
        if (HasAny(line, "攻击你", "朝你", "击中你", "对你")) return "enemy";
        return "player";
    }

    private static bool HasAny(string s, params string[] keys) => keys.Any(s.Contains);

    /// <summary>Boss 大招要加金色边框，玩家濒死的行要闪</summary>
    public static bool WantsBorder(string kind) => kind == "boss";
}
