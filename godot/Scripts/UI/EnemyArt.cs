// 敌人的「身份表」：id → 剪影种类 → 立绘主色 → 族系。
//
// 逐条照搬 TS 版 src/ui/enemyDefs.ts。为什么单独一层：立绘、战斗卡描边、
// 地图上的房间符号都要问「这只敌人长什么样」，答案必须只有一份。
namespace AbyssEcho.UI;

using Godot;

public static class EnemyArt
{
    /// <summary>剪影种类：决定造型（角、翼、骨、甲），与 TS 版 IconKind 同名</summary>
    private static readonly Dictionary<string, string> IconTable = new()
    {
        ["rat"] = "rat",
        ["slime"] = "slime",
        ["skeleton"] = "skeleton",
        ["goblin"] = "goblin",
        ["orc"] = "orc",
        ["wraith"] = "wraith",
        ["troll"] = "troll",
        ["lich"] = "lich",
        ["mimic"] = "chest",
        ["golem"] = "golem",
        ["blood_serpent"] = "blood_serpent",
        ["thorn_hound"] = "thorn_hound",
        ["cave_bat"] = "cave_bat",
        ["stone_warden"] = "stone_warden",
        ["frost_archer"] = "frost_archer",
        ["rust_golem"] = "rust_golem",
        ["shadow_stalker"] = "shadow_stalker",
        ["abyss_hound"] = "abyss_hound",
        ["crystal_priest"] = "crystal_priest",
        ["molten_hound"] = "molten_hound",
        ["warden_priest"] = "warden_priest",
        ["zealot"] = "zealot",
        ["blood_bat"] = "blood_bat",
        ["cursed_statue"] = "cursed_statue",
        ["frost_binder"] = "frost_binder",
        ["boss_molten"] = "boss_molten",
        ["boss_frost"] = "boss_frost",
        ["boss_abyss"] = "boss_abyss",
        ["boss_abyss_reversed"] = "boss_abyss_reversed",
    };

    public static string IconKind(string id) => IconTable.TryGetValue(id, out var k) ? k : "skull";

    /// <summary>
    /// 主色跟着元素走：火=橙、冰=蓝、岩=褐灰、暗影=紫、毒=绿。
    /// TS 版注释里记过一个教训：以前所有小怪共用同一个橙，
    /// 组合房间里两只不同元素的敌人并排时，光看颜色分不出谁是谁。
    /// </summary>
    private static readonly Dictionary<string, Color> TintTable = new()
    {
        ["rat"] = new("b08a72"),
        ["slime"] = new("7fd4a0"),
        ["skeleton"] = new("dfe4f2"),
        ["goblin"] = new("8fd45a"),
        ["orc"] = new("a8763f"),
        ["wraith"] = new("8fa8d8"),
        ["troll"] = new("7fa05c"),
        ["lich"] = new("9d8cff"),
        ["mimic"] = new("d8a24e"),
        ["golem"] = new("9aa3b8"),
        ["blood_serpent"] = new("e0655f"),
        ["thorn_hound"] = new("a88a4a"),
        ["cave_bat"] = new("8b7ab8"),
        ["stone_warden"] = new("8494b4"),
        ["frost_archer"] = new("7fd4ff"),
        ["rust_golem"] = new("a8703a"),
        ["shadow_stalker"] = new("8f6bff"),
        ["abyss_hound"] = new("9d5cff"),
        ["crystal_priest"] = new("6ec2e8"),
        ["molten_hound"] = new("ff8a3c"),
        ["warden_priest"] = new("5aa9ff"),
        ["zealot"] = new("e07a5f"),
        ["blood_bat"] = new("d4556b"),
        ["cursed_statue"] = new("8a7ab8"),
        ["frost_binder"] = new("a8e4ff"),
    };

    public static Color Tint(string id) => TintTable.TryGetValue(id, out var c) ? c : new Color("ffa06b");

    /// <summary>Boss 的角色设定：主色、光晕、浮动节奏。颜色跟着属性走，玩家看色就认人。</summary>
    public sealed record BossStyle(Color Tint, Color Aura, double Float, double Breath);

    private static readonly Dictionary<string, BossStyle> BossStyles = new()
    {
        ["boss_molten"] = new(new Color("ff7a2a"), new Color("ff5a1e"), 1.5, 1.4),
        ["boss_frost"] = new(new Color("7fd4ff"), new Color("49b6ff"), 2.1, 1.9),
        ["boss_abyss"] = new(new Color("a97bff"), new Color("8f4dff"), 2.6, 2.3),
        // 逆位：同一只深渊之主，配色反过来——金转暗紫，玩家一眼知道「这不是原来那只」
        ["boss_abyss_reversed"] = new(new Color("ffd166"), new Color("8f4dff"), 2.0, 1.7),
    };

    public static BossStyle? Style(string id) => BossStyles.TryGetValue(id, out var s) ? s : null;

    /// <summary>族系：同一元素的一窝敌人共用一个色相</summary>
    public static readonly Dictionary<string, (string Name, Color Color)> Families = new()
    {
        ["fire"] = ("火焰", new Color("ff9f45")),
        ["frost"] = ("寒霜", new Color("8ce2ff")),
        ["earth"] = ("大地", new Color("b08a5a")),
        ["shadow"] = ("暗影", new Color("a97bff")),
        ["beast"] = ("野兽", new Color("c9a06a")),
        ["undead"] = ("亡者", new Color("9fe0c4")),
    };

    private static readonly Dictionary<string, string> FamilyTable = new()
    {
        ["rat"] = "beast",
        // 史莱姆主色是绿的，冷暖上和野兽的褐色对不上，归到亡者（冷绿）一侧
        ["slime"] = "undead",
        ["goblin"] = "beast",
        ["orc"] = "beast",
        ["thorn_hound"] = "beast",
        ["cave_bat"] = "beast",
        ["blood_bat"] = "undead",
        ["mimic"] = "beast",
        ["skeleton"] = "undead",
        ["wraith"] = "undead",
        ["zealot"] = "undead",
        ["lich"] = "undead",
        ["troll"] = "beast",
        ["golem"] = "earth",
        ["stone_warden"] = "earth",
        ["rust_golem"] = "earth",
        ["cursed_statue"] = "earth",
        ["blood_serpent"] = "beast",
        ["frost_archer"] = "frost",
        ["frost_binder"] = "frost",
        ["crystal_priest"] = "frost",
        ["molten_hound"] = "fire",
        ["shadow_stalker"] = "shadow",
        ["abyss_hound"] = "shadow",
        ["warden_priest"] = "frost",
        ["boss_molten"] = "fire",
        ["boss_frost"] = "frost",
        ["boss_abyss"] = "shadow",
        ["boss_abyss_reversed"] = "shadow",
    };

    public static string Family(string id) => FamilyTable.TryGetValue(id, out var f) ? f : "beast";

    public static Color FamilyColor(string id) =>
        Families.TryGetValue(Family(id), out var e) ? e.Color : new Color("c9a06a");
}
