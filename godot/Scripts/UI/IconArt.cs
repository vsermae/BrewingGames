// 矢量图标：技能、道具、状态、房间符号、敌人剪影统一在这里画。
//
// 原版 src/ui/icons.ts 是 1074 行 Phaser Graphics 手绘路径。上一版移植把它压成了
// 「每个图标三五个基本形状」——因为当时是在跟坐标系搏斗（每笔都要手乘 scale）。
// 结果剪影全是「几何拼装」级，原版的鳞片、裂甲、发光缝全丢了。
//
// 这一版换了做法：先用 Painter（同目录）把 Phaser 的绘制状态机
// （fillStyle / lineStyle / 原点居中 + 按 size/32 缩放）补回来，
// 然后**逐条照抄原版的 case 分支**。一个 case 少一笔都不行——差距就是这些笔画。
//
// 没有独立 .svg 资源是有意的：这个仓库要断网离线可玩，加资源就多一条加载链。
namespace AbyssEcho.UI;

using Godot;

public static class IconArt
{
    /// <summary>设计基准边长：所有坐标都写在 32×32、原点居中的设计框里</summary>
    public const float DesignSize = 32f;

    /// <summary>把图标画在 ci 的 (0,0)，直径按 size，颜色默认白。</summary>
    public static void Draw(CanvasItem ci, string kind, float size = DesignSize, Color? color = null) =>
        Paint(ci, kind, size, color?.R ?? 1f, color?.G ?? 1f, color?.B ?? 1f);

    /// <summary>按 RGB 分量画：调用方常常只有「主色」这一个概念，不想包 Color</summary>
    public static void Paint(CanvasItem ci, string kind, float size, float r = 1f, float g = 1f, float b = 1f)
    {
        var p = new Painter(ci, size / DesignSize);
        Shape(p, kind, new Color(r, g, b));
    }

    /// <summary>记账模式：不落墨，只把笔画数与包围盒记进 probe（UiSmoke 用）</summary>
    public static void Shape(ShapeProbe probe, string kind, Color color, float size = DesignSize) =>
        Shape(new Painter(probe, size / DesignSize), kind, color);

    /// <summary>
    /// 这个图标在 32 设计框里实际占的横 / 竖向尺寸。
    /// 每只怪在框里站的位置和大小都不一样（巨鼠只占中间一条，Boss 占满整框），
    /// 所以「画多大」这件事不能只看框，得先量一量里面装了多少东西。
    /// 每次调用现量一遍：几十笔而已，换的是不会有第二份「每只怪多大」的手抄表。
    /// </summary>
    public static Vector2 Bounds(string kind)
    {
        var probe = new ShapeProbe();
        Shape(probe, kind, Colors.White);
        // 一笔都没落时退回整框：让它看起来「占满」，不至于算出 0 尺寸的除法
        if (probe.Ops == 0) return new Vector2(DesignSize, DesignSize);
        return new Vector2(probe.MaxX - probe.MinX, probe.MaxY - probe.MinY);
    }

    /// <summary>
    /// 只画外轮廓：变暗的剪影垫底用。
    /// 上一版这一层是把整幅细节图缩小重画一遍，笔画数翻倍但边缘糊成一圈毛；
    /// 只描轮廓之后，小尺寸下反而更清楚——这也正是 TS 版 createMonsterSprite 的做法。
    /// </summary>
    public static void Outline(CanvasItem ci, string kind, float size, Color color)
    {
        var p = new Painter(ci, size / DesignSize);
        p.Line(2.6f, color, 0.92f);
        var d = Darken(color, 26f);
        // 剪影用「主体色块 + 一圈描边」拼：细节全部省略，只留让人认出是谁的那几笔
        switch (kind)
        {
            case "rat":
                // 剪影只留「读得出是鼠」的那几笔：弓背、探出去的头与吻、尾巴、腿。
                // 耳朵不进剪影：小尺寸下它会和头糊成一团，反而看不出轮廓。
                p.Fill(d, 0.9f);
                p.FillPoints((-12f, 2f), (-8f, -6f), (0f, -8f), (7f, -5f), (10f, 1f),
                    (8f, 8f), (0f, 10f), (-8f, 9f));
                p.FillCircle(9f, 3f, 6.4f);
                p.FillTriangle(13f, 0f, 18f, 4f, 12f, 7f);
                p.FillRect(-9f, 7f, 3.4f, 5f);
                p.FillRect(9f, 7f, 3.4f, 5f);
                p.StrokePolyline(new[] { (-12f, 2f), (-17f, 0f), (-19f, -5f), (-17f, -9f) }, d, 2.4f);
                p.StrokePoints((-12f, 2f), (-8f, -6f), (0f, -8f), (7f, -5f), (10f, 1f),
                    (8f, 8f), (0f, 10f), (-8f, 9f));
                break;
            case "slime":
                p.Fill(d, 0.9f);
                p.FillPoints((-13f, 11f), (-9f, -3f), (-3f, -10f), (5f, -9f), (12f, -1f), (13f, 11f));
                p.FillCircle(13f, 6f, 6f);
                p.StrokePoints((-13f, 11f), (-9f, -3f), (-3f, -10f), (5f, -9f), (12f, -1f), (13f, 11f));
                break;
            case "skeleton":
                p.Fill(d, 0.9f);
                p.FillCircle(0f, -8f, 8f);
                p.FillRect(-5f, -3f, 10f, 5f);
                p.FillRect(-1.6f, 4f, 3.2f, 13f);
                p.StrokeCircle(0f, -8f, 8f);
                break;
            case "goblin":
                p.Fill(d, 0.9f);
                p.FillCircle(0f, -7f, 8f);
                p.FillTriangle(-8f, -11f, -17f, -6f, -7f, -3f);
                p.FillTriangle(8f, -11f, 17f, -6f, 7f, -3f);
                p.FillPoints((-8f, 2f), (8f, 2f), (7f, 13f), (-7f, 13f));
                p.StrokeCircle(0f, -7f, 8f);
                break;
            case "orc":
                p.Fill(d, 0.9f);
                p.FillCircle(0f, -9f, 8f);
                p.FillPoints((-11f, 5f), (11f, 5f), (9f, 14f), (-9f, 14f));
                p.StrokeCircle(0f, -9f, 8f);
                break;
            case "wraith":
                p.Fill(d, 0.92f);
                p.FillPoints((0f, -15f), (7f, -3f), (11f, 13f), (4f, 8f), (0f, 14f), (-4f, 8f), (-11f, 13f), (-7f, -3f));
                p.StrokePoints((0f, -15f), (7f, -3f), (11f, 13f), (0f, 14f), (-11f, 13f), (-7f, -3f));
                break;
            case "troll":
                p.Fill(d, 0.9f);
                p.FillPoints((-14f, 14f), (-12f, -2f), (-5f, -12f), (5f, -12f), (12f, -2f), (14f, 14f));
                p.FillCircle(0f, -10f, 7f);
                p.StrokeCircle(0f, -10f, 7f);
                break;
            case "lich":
                p.Fill(d, 0.9f);
                p.FillPoints((-12f, 14f), (-9f, -6f), (0f, -16f), (9f, -6f), (12f, 14f));
                p.FillCircle(0f, -7f, 7f);
                p.StrokePoints((-12f, 14f), (-9f, -6f), (0f, -16f), (9f, -6f), (12f, 14f));
                break;
            case "golem":
            case "stone_warden":
            case "rust_golem":
            case "cursed_statue":
                p.Fill(d, 0.9f);
                p.FillRoundRect(-11f, -9f, 22f, 23f, 6f);
                p.StrokeRect(-11f, -9f, 22f, 23f);
                break;
            case "blood_serpent":
                p.Fill(d, 0.9f);
                p.FillPoints((-12f, 9f), (-4f, 2f), (6f, 5f), (12f, 11f), (6f, 13f), (-6f, 13f));
                p.FillCircle(-2f, -8f, 5.4f);
                p.StrokeCircle(-2f, -8f, 5.4f);
                break;
            case "thorn_hound":
            case "abyss_hound":
            case "molten_hound":
                p.Fill(d, 0.9f);
                p.FillPoints((-13f, 4f), (-6f, -4f), (6f, -4f), (13f, 1f), (12f, 8f), (-10f, 8f));
                p.FillCircle(11f, -1f, 6f);
                p.StrokeCircle(11f, -1f, 6f);
                break;
            case "cave_bat":
            case "blood_bat":
                p.Fill(d, 0.9f);
                p.FillCircle(0f, -4f, 6.5f);
                p.FillPoints((-5f, -3f), (-16f, -8f), (-13f, 0f), (-15f, 6f), (-5f, 3f));
                p.FillPoints((5f, -3f), (16f, -8f), (13f, 0f), (15f, 6f), (5f, 3f));
                p.StrokeCircle(0f, -4f, 6.5f);
                break;
            case "frost_archer":
            case "frost_binder":
            case "crystal_priest":
            case "warden_priest":
            case "zealot":
            case "shadow_stalker":
            case "player":
            case "hero":
                p.Fill(d, 0.9f);
                p.FillCircle(0f, -8f, 6.5f);
                p.FillPoints((-9f, 14f), (-10f, -2f), (0f, -14f), (10f, -2f), (9f, 14f));
                p.StrokePoints((-9f, 14f), (-10f, -2f), (0f, -14f), (10f, -2f), (9f, 14f));
                break;
            case "mimic":
            case "chest":
                p.Fill(d, 0.9f);
                p.FillRoundRect(-12f, -12f, 24f, 22f, 3f);
                p.StrokeRect(-12f, -12f, 24f, 22f);
                break;
            case "boss_molten":
            case "boss_frost":
            case "boss_abyss":
            case "boss_abyss_reversed":
                p.Fill(d, 0.9f);
                p.FillPoints((-15f, 15f), (-13f, -2f), (-6f, -13f), (6f, -13f), (13f, -2f), (15f, 15f));
                p.FillCircle(0f, -10f, 8f);
                p.StrokePoints((-15f, 15f), (-13f, -2f), (-6f, -13f), (6f, -13f), (13f, -2f), (15f, 15f));
                break;
            case "boss":
                p.Fill(d, 0.9f);
                p.FillPoints((-15f, 14f), (-13f, 0f), (-6f, -12f), (6f, -12f), (13f, 0f), (15f, 14f));
                p.StrokePoints((-15f, 14f), (-13f, 0f), (-6f, -12f), (6f, -12f), (13f, 0f), (15f, 14f));
                break;
            default:
                p.Fill(d, 0.9f);
                p.FillCircle(0f, 0f, 11f);
                p.StrokeCircle(0f, 0f, 11f);
                break;
        }
    }

    /// <summary>造型本体。逐条对照 icons.ts 的 switch，笔画的顺序也要对上（后画的压前面的）</summary>
    public static void Shape(Painter p, string kind, Color c, bool outline = true)
    {
        switch (kind)
        {
            // ---------------- 道具与装备 ----------------
            case "sword":
                // 剑：刃、护手、柄、柄头四段。整把剑是绕着 (0,0) 立着的，
                // 刃尖在上、柄头在下——斜的刃面只占一条边，不能整块斜（整块斜就成了钩子）。
                // 上一版把刃画成一条从 (-1.5,-13) 到 (2.5,8) 的细三角，
                // 缩到 37px 时宽度不到 1px，屏幕上只剩一根白线，看着像问号。
                p.Fill(Hex("c9d6ee"));
                p.FillTriangle(0f, -15f, 4.6f, -9.5f, 4.6f, 3.5f);
                p.FillTriangle(0f, -15f, -4.6f, -9.5f, -4.6f, 3.5f);
                p.FillRect(-4.6f, 3.5f, 9.2f, 2f);
                // 刃面高光：只压左半边，右半边留给暗，剑脊就出来了
                p.Fill(Hex("f4f8ff"), 0.85f);
                p.FillTriangle(0f, -13.4f, 0.4f, -9.5f, 0.4f, 3f);
                p.Fill(Darken(c, 40f));
                p.FillTriangle(2.8f, -8.4f, 4.2f, -8.4f, 4.2f, 3f);
                // 护手：横向一条，两端各压一笔暗，显得是块金属
                p.Fill(c);
                p.FillRect(-8.2f, 5.5f, 16.4f, 3.2f);
                p.Fill(Darken(c, 70f));
                p.FillRect(-8.2f, 7.9f, 16.4f, 1.4f);
                // 柄 + 柄头
                p.Fill(Hex("8b5a2b"));
                p.FillRect(-1.8f, 8.7f, 3.6f, 6.6f);
                p.Fill(Darken(Hex("8b5a2b"), 40f));
                p.FillRect(-1.8f, 8.7f, 3.6f, 0.9f);
                p.Fill(Hex("c9d6ee"));
                p.FillCircle(0f, 17.2f, 2.8f);
                break;

            case "shield":
                // 盾面 + 描边 + 十字 + 中心铆钉；描边是原版的 lineStyle(2, c)
                p.Fill(Hex("2b3a5c"));
                p.FillPoints((0f, -14f), (11f, -9f), (9f, 4f), (0f, 14f), (-9f, 4f), (-11f, -9f));
                p.Fill(Darken(c, 170f), 0.55f);
                p.FillPoints((0f, -11f), (8f, -7f), (6.6f, 2.6f), (0f, 10.6f), (-6.6f, 2.6f), (-8f, -7f));
                p.Line(2f, c, 1f);
                p.StrokePoints((0f, -14f), (11f, -9f), (9f, 4f), (0f, 14f), (-9f, 4f), (-11f, -9f), (0f, -14f));
                p.Fill(c);
                p.FillRect(-1.6f, -6f, 3.2f, 12f);
                p.FillRect(-6f, -1.6f, 12f, 3.2f);
                p.Fill(Lighten(c, 110f), 0.8f);
                p.FillCircle(0f, -6f, 1.6f);
                break;

            case "flask":
                // 整瓶上下居中在 32 的设计框里：瓶口、瓶颈、瓶身、瓶底依次展开
                p.Fill(Hex("3d3358"));
                p.FillRect(-4f, -15f, 8f, 4f);
                p.Fill(Hex("2b3a5c"));
                p.FillTriangle(-7f, -11f, 7f, -11f, 0f, -6f);
                p.Fill(c);
                p.FillPoints((-9f, -9f), (9f, -9f), (11f, 6f), (-11f, 6f));
                p.FillCircle(0f, 6f, 11f);
                p.Fill(Lighten(c, 40f), 0.55f);
                p.FillCircle(0f, 6f, 8.4f);
                p.Fill(Colors.White, 0.35f);
                p.FillCircle(-4f, -2f, 2.4f);
                p.Fill(Colors.White, 0.18f);
                p.FillCircle(3.6f, 3f, 1.6f);
                break;

            case "herb":
                // 解毒草：一株带叶与浆果的草，不是任何瓶子。
                //
                // 原来解毒草与「中毒」状态共用毒药瓶：玩家在背包里看到一瓶毒药，
                // 而它写的是「立刻解除自身的中毒状态」——图标和用途正好相反，
                // 而且和「中毒」这个减益长得一模一样，读起来是「吃了会更毒」。
                p.Fill(Hex("3f5b33"));
                p.FillRect(-1.4f, -4f, 2.8f, 18f);
                // 叶片：左右各两片，向外斜上，剪影才不对称地像一株植物
                p.Fill(Hex("6fb04a"));
                p.FillPoints((0f, 2f), (-11f, -2f), (-13f, -9f), (-5f, -5f));
                p.FillPoints((0f, 6f), (11f, 2f), (13f, -5f), (5f, -1f));
                p.Fill(Hex("8fd45a"));
                p.FillPoints((0f, -3f), (-6f, -12f), (-9f, -17f), (-2f, -9f));
                p.FillPoints((0f, -3f), (6f, -12f), (9f, -17f), (2f, -9f));
                // 叶脉：一条中线，小尺寸下也读得出「这是片叶子」
                p.Line(1.1f, Hex("2f4426"), 0.8f);
                p.Line(-2f, -6f, -10f, -8f);
                p.Line(2f, -2f, 10f, -4f);
                // 两颗浆果：草药的「药」在这一笔上
                p.Fill(c);
                p.FillCircle(4.6f, 8.4f, 3.2f);
                p.FillCircle(-4.4f, 11f, 2.4f);
                p.Fill(Colors.White, 0.4f);
                p.FillCircle(3.6f, 7.4f, 1.1f);
                break;

            case "poison":
                // 毒药瓶：瓶身 + 三缕上飘的毒气，一眼看出「这和普通药水不是一回事」
                p.Fill(Hex("2b3a2c"));
                p.FillTriangle(-7f, -12f, 7f, -12f, 0f, -6f);
                p.Fill(c);
                p.FillPoints((-9f, -8f), (9f, -8f), (11f, 6f), (-11f, 6f));
                p.FillCircle(0f, 6f, 11f);
                p.Fill(Hex("0a0d18"), 0.5f);
                p.FillCircle(0f, 2f, 3.4f);
                p.Line(2.2f, c, 0.9f);
                foreach (var dx in new[] { -5f, 0f, 5f })
                {
                    p.Line(dx, -14f, dx + 2.5f, -20f);
                }
                p.Fill(Lighten(c, 50f), 0.6f);
                p.FillCircle(-3.4f, 8f, 1.8f);
                break;

            case "bomb":
                p.Fill(Hex("2a3550"));
                p.FillCircle(-1f, 3f, 11f);
                p.Fill(Hex("46536f"));
                p.FillCircle(-4f, 0f, 3.6f);
                p.Fill(Hex("1a1f30"), 0.7f);
                p.FillCircle(6f, 7f, 2.4f);
                p.Line(2.6f, Hex("8b5a2b"), 1f);
                p.Line(5f, -6f, 9f, -12f);
                p.Fill(c);
                p.FillStar(11f, -14f, 4, 4.4f, 1.6f);
                p.Fill(Lighten(c, 90f), 0.8f);
                p.FillCircle(11f, -14f, 1.8f);
                break;

            case "ring":
                p.Line(4.4f, c, 1f);
                p.StrokeCircle(0f, 4f, 8f);
                p.Fill(Hex("9fe8ff"));
                p.FillPoints((-5.5f, -4f), (5.5f, -4f), (0f, -14f));
                p.Fill(Colors.White, 0.55f);
                p.FillTriangle(-2.4f, -6f, 0.6f, -6f, 0f, -12f);
                break;

            case "armor":
                p.Fill(c);
                p.FillPoints((-10f, -10f), (-3f, -13f), (3f, -13f), (10f, -10f), (10f, 12f), (-10f, 12f));
                p.Fill(Hex("2b3a5c"));
                p.FillTriangle(-3f, -12f, 3f, -12f, 0f, -3f);
                p.FillRect(-2f, 4f, 4f, 8f);
                // 肩线 + 腹甲横纹：原版没有，但「裂甲」是这次补的细节主线
                p.Line(1.8f, Darken(c, 70f), 0.8f);
                p.Line(-9f, -2f, 9f, -2f);
                p.Line(-8f, 1f, 8f, 1f);
                p.Line(1.8f, Lighten(c, 90f), 0.6f);
                p.Line(-8.6f, -11f, -8.6f, 9f);
                break;

            case "amulet":
                p.Line(2.4f, Hex("d7c9a8"), 1f);
                p.StrokeArc(0f, 0f, 10f, Mathf.Pi * 0.15f, Mathf.Pi * 0.85f);
                p.Fill(c);
                p.FillPoints((0f, -5f), (6f, 3f), (0f, 12f), (-6f, 3f));
                p.Fill(Darken(c, 55f), 0.7f);
                p.FillPoints((0f, 2f), (3.4f, 4f), (0f, 10f), (-3.4f, 4f));
                p.Fill(Colors.White, 0.5f);
                p.FillTriangle(-2f, 0f, 2f, 0f, 0f, 5f);
                break;

            case "medal":
                p.Fill(Hex("7a4a4a"));
                p.FillTriangle(-8f, -14f, -2f, -4f, -9f, -3f);
                p.Fill(Hex("4a5a7a"));
                p.FillTriangle(8f, -14f, 2f, -4f, 9f, -3f);
                p.Fill(c);
                p.FillCircle(0f, 4f, 9f);
                p.Line(1.6f, Darken(c, 70f), 1f);
                p.StrokeCircle(0f, 4f, 9f);
                p.Fill(Hex("8a6a1e"));
                p.FillStar(0f, 4f, 5, 6f, 2.6f);
                p.Fill(Hex("ffe9a8"), 0.75f);
                p.FillStar(0f, 4f, 5, 3.6f, 1.6f);
                break;

            case "fire":
                p.Fill(Hex("ff7b3d"));
                p.FillPoints((0f, -15f), (7f, -2f), (4f, 10f), (-4f, 10f), (-7f, -2f));
                p.Fill(c);
                p.FillPoints((0f, -5f), (4f, 3f), (0f, 11f), (-4f, 3f));
                p.Fill(Colors.White, 0.45f);
                p.FillPoints((0f, 2f), (1.8f, 6f), (0f, 10f), (-1.8f, 6f));
                break;

            case "sparkle":
                p.FillStar(0f, 0f, 4, 14f, 4f);
                p.Fill(Colors.White, 0.8f);
                p.FillCircle(0f, 0f, 3f);
                p.Fill(c);
                p.FillStar(11f, -9f, 4, 5f, 1.6f);
                p.Fill(c, 0.75f);
                p.FillStar(-9f, 9f, 4, 3.4f, 1.1f);
                break;

            case "muscle":
                p.Fill(c);
                p.FillPoints((-12f, 2f), (-8f, -6f), (-2f, -9f), (5f, -10f), (10f, -6f), (12f, 0f), (12f, 8f), (-12f, 8f));
                p.Fill(Colors.Black, 0.18f);
                p.FillRect(-9f, 1f, 4f, 6f);
                p.FillRect(-1f, 1f, 4f, 6f);
                // 上缘高光：让「肌肉」不是一块平板
                p.Fill(Lighten(c, 80f), 0.5f);
                p.FillPoints((-6f, -7f), (4f, -8f), (8f, -5f), (-4f, -4f));
                break;

            case "swap":
                p.Line(3f, c, 1f);
                p.StrokeArc(0f, 0f, 9f, Mathf.Pi * 0.15f, Mathf.Pi * 0.85f);
                p.StrokeArc(0f, 0f, 9f, Mathf.Pi * 1.15f, Mathf.Pi * 1.85f);
                p.Fill(c);
                p.FillTriangle(9f, -4f, 3f, -4f, 9f, -11f);
                p.FillTriangle(-9f, 4f, -3f, 4f, -9f, 11f);
                break;

            case "coin":
                p.Fill(Hex("b8860b"));
                p.FillCircle(0f, 0f, 12f);
                p.Fill(c);
                p.FillCircle(0f, 0f, 10f);
                p.Fill(Hex("d9a520"));
                p.FillCircle(0f, 0f, 6.5f);
                p.Fill(Hex("fff2c0"), 0.75f);
                p.FillRect(-2.6f, -5f, 5.2f, 10f);
                p.Line(1.4f, Darken(c, 90f), 0.9f);
                p.StrokeCircle(0f, 0f, 10f);
                break;

            case "heart":
                // 心：两个圆 + 一个倒三角。
                //
                // 上一版靠 **裁剪** 抹掉两个圆在下面那一半——`DrawCircle` 是直接落墨的，
                // 不走 Painter 那套填充状态机，所以「后面那个三角形要盖住圆弧」
                // 这句话只在设计纸上成立：屏上是「一颗心 + 它下面多出来的一坨」，
                // 而这颗心在地图图例里只有 13px，「那一坨」是它面积的一半。
                // 现在按**外轮廓**拼：两片半圆只画上半圈，腰往下收成尖，
                // 一笔糊涂账变成一条闭合的边。
                p.Fill(c);
                p.FillCircle(-6f, -4f, 7f);
                p.FillCircle(6f, -4f, 7f);
                p.FillPoints((-12.6f, -5.4f), (12.6f, -5.4f), (0f, 14f));
                // 高光：左上那一片，位置比上一版更靠里——贴边会被剪影吃回去
                p.Fill(Colors.White, 0.3f);
                p.FillCircle(-5.6f, -6.4f, 2.6f);
                break;

            case "skull":
                p.Fill(Hex("e6e9f5"));
                p.FillCircle(0f, -3f, 11f);
                p.FillRect(-7f, -3f, 14f, 9f);
                p.Fill(Hex("1a1f30"));
                p.FillCircle(-4.2f, -4f, 3.2f);
                p.FillCircle(4.2f, -4f, 3.2f);
                p.FillTriangle(-1.4f, 2f, 1.4f, 2f, 0f, 5.5f);
                p.Fill(Hex("e6e9f5"));
                p.FillRect(-6f, 6f, 12f, 4f);
                p.Fill(Hex("1a1f30"));
                p.FillRect(-4f, 6f, 1.6f, 4f);
                p.FillRect(2.4f, 6f, 1.6f, 4f);
                // 骨缝：额头与颧骨的两道细线，小尺寸下也能看出「这是颅骨」
                p.Line(1.2f, Hex("b8c0d6"), 0.9f);
                p.Line(0f, -12f, 0f, -8f);
                p.Line(-9f, 2f, -5f, 3.4f);
                p.Line(9f, 2f, 5f, 3.4f);
                break;

            case "chest":
                p.Fill(Hex("7a5230"));
                p.FillRect(-12f, -3f, 24f, 12f);
                p.Fill(Hex("8f6339"));
                p.FillPoints((-12f, -3f), (12f, -3f), (11f, -12f), (-11f, -12f));
                p.Fill(Hex("4a3520"));
                p.FillRect(-12f, -4f, 24f, 3f);
                p.Fill(c);
                p.FillRect(-3f, -3f, 6f, 9f);
                p.FillRect(-13f, -12f, 4f, 21f);
                p.FillRect(9f, -12f, 4f, 21f);
                // 木纹与锁孔：原版这层没有，补上之后「箱子」不只是三块色板
                p.Line(1.2f, Hex("3a2716"), 0.8f);
                p.Line(-9f, -9f, -9f, 8f);
                p.Line(0f, -9f, 0f, 8f);
                p.Line(9f, -9f, 9f, 8f);
                p.Fill(Hex("2a1c10"));
                p.FillCircle(0f, 0.4f, 1.6f);
                break;

            case "cart":
                // 小车：货箱 + 把手 + 两个轮子，整体以原点为中心
                p.Fill(Hex("6b4a2a"));
                p.FillPoints((-13f, -10f), (13f, -10f), (11f, -3f), (-11f, -3f));
                p.Fill(c);
                p.FillRect(-13f, -3f, 26f, 9f);
                p.Fill(Hex("6b4a2a"));
                p.FillRect(-13f, -5f, 26f, 3f);
                p.Fill(Hex("1a1f30"));
                p.FillCircle(-6f, 9f, 3.4f);
                p.FillCircle(6f, 9f, 3.4f);
                p.Fill(Hex("46536f"));
                p.FillCircle(-6f, 9f, 1.4f);
                p.FillCircle(6f, 9f, 1.4f);
                p.Line(2.4f, c, 1f);
                p.Line(-13f, -8f, -17f, -13f);
                // 车板上的货：两包捆绳，让「商店」这件道具更好认
                p.Fill(Lighten(c, 70f), 0.9f);
                p.FillCircle(-4f, -6.4f, 2.6f);
                p.FillCircle(5f, -6.4f, 2.2f);
                break;

            case "circle":
            case "question":
                p.Fill(Hex("2b3a5c"));
                p.FillCircle(0f, 0f, 12f);
                p.Line(2f, c, 1f);
                p.StrokeCircle(0f, 0f, 12f);
                p.Fill(c);
                p.FillRect(-2.2f, -9f, 4.4f, 4f);
                p.FillCircle(0f, -6f, 2.2f);
                p.FillTriangle(-2f, -4f, 3f, -4f, 0f, 3f);
                p.FillCircle(0f, 8f, 2.4f);
                break;

            case "flag":
                p.Fill(Hex("cfd8ea"));
                p.FillRect(-8f, -14f, 2.6f, 28f);
                p.Fill(c);
                p.FillPoints((-5.4f, -14f), (12f, -8.5f), (-5.4f, -3f));
                p.Fill(Darken(c, 60f), 0.55f);
                p.FillPoints((-5.4f, -8.5f), (4f, -8.5f), (-5.4f, -3f));
                break;

            case "rune":
                p.Fill(Hex("2b3a5c"));
                p.FillCircle(0f, 0f, 12f);
                p.Line(2.4f, c, 1f);
                p.Line(-6f, -7f, 6f, 7f);
                p.Line(6f, -7f, -6f, 7f);
                p.Line(0f, -11f, 0f, -4f);
                p.Fill(c, 0.85f);
                p.FillCircle(0f, 9.6f, 1.4f);
                break;

            case "door":
                p.Fill(Hex("6b4a2a"));
                p.FillPoints((-9f, 12f), (-9f, -6f), (0f, -13f), (9f, -6f), (9f, 12f));
                p.Fill(Hex("4a3520"));
                p.FillPoints((-6f, 10f), (-6f, -4f), (0f, -9f), (6f, -4f), (6f, 10f));
                p.Fill(c);
                p.FillCircle(3f, 1f, 1.8f);
                // 门钉：一排五个，是「门」和「一块褐色梯形」的差别所在
                p.Fill(Lighten(c, 80f), 0.7f);
                for (var i = 0; i < 5; i++)
                {
                    p.FillCircle(-4.4f, -2f + i * 2.6f, 0.7f);
                }
                p.Line(1.4f, Hex("3a2716"), 0.9f);
                p.Line(-9f, -6f, 0f, -13f);
                p.Line(9f, -6f, 0f, -13f);
                break;

            case "player":
            case "hero":
                p.Fill(c);
                p.FillCircle(0f, -8f, 6f);
                p.FillPoints((-10f, 12f), (-10f, 0f), (-5f, -3f), (5f, -3f), (10f, 0f), (10f, 12f));
                p.Fill(Colors.White, 0.35f);
                p.FillCircle(-2f, -9f, 1.8f);
                break;

            case "trophy":
                // 杯耳
                p.Line(2.4f, Darken(c, 40f), 1f);
                p.StrokeArc(-9f, -6f, 5f, Mathf.Pi * 0.5f, Mathf.Pi * 1.5f);
                p.StrokeArc(9f, -6f, 5f, Mathf.Pi * 1.5f, Mathf.Pi * 0.5f);
                // 杯身
                p.Fill(c);
                p.FillPoints((-9f, -13f), (9f, -13f), (6f, 2f), (-6f, 2f));
                p.Fill(Lighten(c, 40f), 0.9f);
                p.FillPoints((-9f, -13f), (-4f, -13f), (-4f, 2f), (-6f, 2f));
                // 杯柄与底座
                p.Fill(Darken(c, 40f));
                p.FillRect(-2f, 2f, 4f, 6f);
                p.Fill(c);
                p.FillRoundRect(-9f, 9f, 18f, 4f, 1.6f);
                p.FillRoundRect(-11f, 13f, 22f, 4f, 1.6f);
                p.Fill(Colors.White, 0.4f);
                p.FillStar(0f, -6f, 4, 4.5f, 1.6f);
                break;

            // ---------------- 敌人剪影：逐只对照 icons.ts ----------------
            // 每只都按「底色块 → 主体 → 高光 → 描边/裂缝 → 眼」的顺序落笔，
            // 顺序错了细节就被盖住——原版靠的就是这个层次。
            case "rat":
                // 巨鼠：低头嗅地、弓着背、尾巴从左后拖出去。
                //
                // 上一版是「一条蜷着的身体 + 一个大头 + 一个圆耳朵」，放大到 280px
                // 之后只有三个枣核形的色块，读不出是只老鼠——「巨鼠像团块」这条
                // 在笔记里挂了三四轮。问题不在笔数，在**没有一条能读出鼠的线**：
                // 没有吻部、没有尾巴的方向、四只脚一团糊。
                // 现在按「头朝右前方」重排（它站在舞台左侧、面朝玩家）：
                // 尾根在左后 → 尾巴向左上翘出框外，四足各占一条独立的腿，
                // 头前方伸出一截尖吻 + 一颗门牙，耳朵是立着的两片。
                // 脚底落在 y=+12、吻部到 x=+18：包围盒因此变宽变矮，
                // 立绘的盒子按它反推（见 MonsterSprite），体量差才由数据说了算。
                p.Fill(c);
                // 躯干：背脊拱起、腹部收进去
                p.FillPoints((-12f, 2f), (-8f, -6f), (0f, -8f), (7f, -5f), (10f, 1f),
                    (8f, 8f), (0f, 10f), (-8f, 9f));
                // 头：接在躯干右前方，向前下方探（低头嗅）
                p.FillCircle(9f, 3f, 6.4f);
                // 吻部：一小截尖的，这是「鼠」而不是「仓鼠」的那一笔
                p.FillTriangle(13f, 0f, 18f, 4f, 12f, 7f);
                p.Fill(Hex("ffd0d0"));
                p.FillTriangle(14.5f, 4f, 18f, 4.5f, 14f, 6f);
                // 耳朵：两片立在头顶后侧。
                // 耳朵**不加深**（上一版加深了 18%）：剪影已经在后面垫了一层暗色，
                // 耳朵再压暗就和它糊在一起，屏上只剩「一个团块 + 一颗眼」。
                p.Fill(c);
                p.FillCircle(5f, -6f, 3.6f);
                p.FillCircle(12f, -4f, 3.2f);
                p.Fill(Hex("ffd0d0"), 0.55f);
                p.FillCircle(5f, -6f, 1.7f);
                p.FillCircle(12f, -4f, 1.5f);
                // 四条腿：前后各两条，脚掌落地（y=+12 是脚底线）
                p.Fill(Darken(c, 45f));
                p.FillRect(-9f, 7f, 3.4f, 5f);
                p.FillRect(-3f, 8f, 3.4f, 4f);
                p.FillRect(4f, 8f, 3.4f, 4f);
                p.FillRect(9f, 7f, 3.4f, 5f);
                // 背脊暗面：顺着背脊一条，把「弓」读出来
                p.Fill(Darken(c, 55f), 0.8f);
                p.FillPoints((-10f, 0f), (-6f, -5f), (2f, -7f), (7f, -4f), (2f, -3f), (-5f, 0f));
                // 尾巴：从尾根向左上甩出去，长、细、在框外留一截
                p.Line(2.6f, Darken(c, 30f), 0.95f);
                p.StrokePolyline(new[] { (-12f, 2f), (-17f, 0f), (-19f, -5f), (-17f, -9f) }, Darken(c, 30f), 2.4f);
                // 眼睛：一颗，朝前
                p.Fill(Hex("1a1f30"));
                p.FillCircle(11f, 1f, 1.8f);
                p.Fill(Hex("ffe9e9"), 0.9f);
                p.FillCircle(11.3f, 0.5f, 0.7f);
                break;

            case "slime":
                p.Fill(c);
                p.FillPoints((-13f, 11f), (-9f, -3f), (-3f, -10f), (5f, -9f), (12f, -1f), (13f, 11f));
                p.FillCircle(13f, 6f, 6f);
                p.Fill(Colors.White, 0.45f);
                p.FillCircle(-5f, -3f, 3f);
                p.Fill(Colors.White, 0.22f);
                p.FillCircle(-7f, -5f, 1.6f);
                p.Fill(Hex("1a1f30"));
                p.FillCircle(-4f, 2f, 2f);
                p.FillCircle(5f, 1f, 2f);
                p.Fill(Colors.White, 0.9f);
                p.FillCircle(-3.4f, 1.4f, 0.7f);
                p.FillCircle(5.6f, 0.4f, 0.7f);
                break;

            case "skeleton":
                p.Fill(c);
                p.FillCircle(0f, -8f, 8f);
                p.FillRect(-5f, -3f, 10f, 5f);
                p.FillRect(-8f, -13f, 3f, 3f);
                p.FillRect(5f, -13f, 3f, 3f);
                p.FillRect(-1.6f, 4f, 3.2f, 9f);
                p.Line(2.6f, c, 1f);
                p.Line(-9f, 2f, 9f, 6f);
                p.Line(-9f, 6f, 9f, 2f);
                p.Fill(Hex("1a1f30"));
                p.FillCircle(-3f, -9f, 2.6f);
                p.FillCircle(3f, -9f, 2.6f);
                // 牙与肋骨：骨感全靠这两笔
                p.Fill(Darken(c, 60f), 0.8f);
                p.FillRect(-3.4f, -1.6f, 1.4f, 2.6f);
                p.FillRect(-0.8f, -1.6f, 1.4f, 2.6f);
                p.FillRect(2f, -1.6f, 1.4f, 2.6f);
                p.Line(1.4f, Darken(c, 50f), 0.7f);
                p.Line(-3f, 6f, 3f, 6f);
                p.Line(-3.4f, 9f, 3.4f, 9f);
                break;

            case "goblin":
                p.Fill(c);
                p.FillCircle(0f, -7f, 8f);
                p.FillTriangle(-8f, -11f, -17f, -6f, -7f, -3f);
                p.FillTriangle(8f, -11f, 17f, -6f, 7f, -3f);
                p.FillPoints((-8f, 2f), (8f, 2f), (7f, 13f), (-7f, 13f));
                p.Fill(Hex("1a1f30"));
                p.FillCircle(-3f, -8f, 2f);
                p.FillCircle(3f, -8f, 2f);
                p.Fill(Colors.White);
                p.FillRect(-3f, -3f, 2f, 3f);
                p.FillRect(1f, -3f, 2f, 3f);
                // 耳朵内侧 + 腰布
                p.Fill(Darken(c, 60f), 0.7f);
                p.FillTriangle(-9f, -9f, -15f, -7f, -9f, -5f);
                p.FillTriangle(9f, -9f, 15f, -7f, 9f, -5f);
                p.Fill(Hex("4a3a26"));
                p.FillRect(-7f, 8f, 14f, 5f);
                break;

            case "orc":
                p.Fill(c);
                p.FillCircle(0f, -9f, 8f);
                p.FillPoints((-11f, 5f), (11f, 5f), (9f, 14f), (-9f, 14f));
                p.Fill(Colors.White, 0.85f);
                p.FillTriangle(-5f, -2f, -1f, -2f, -4f, 4f);
                p.FillTriangle(5f, -2f, 1f, -2f, 4f, 4f);
                p.Fill(Hex("1a1f30"));
                p.FillCircle(-3f, -10f, 2f);
                p.FillCircle(3f, -10f, 2f);
                p.Line(2.4f, c, 1f);
                p.Line(-13f, 2f, -16f, 12f);
                p.Line(13f, 2f, 16f, 12f);
                // 战纹 + 护肩
                p.Fill(Darken(c, 55f), 0.7f);
                p.FillRect(-2f, -14f, 1.4f, 4f);
                p.FillRect(1f, -14f, 1.4f, 4f);
                p.Fill(Hex("5a4a38"));
                p.FillRoundRect(-13f, 0f, 5f, 8f, 2f);
                p.FillRoundRect(8f, 0f, 5f, 8f, 2f);
                break;

            case "wraith":
                p.Fill(c, 0.92f);
                p.FillPoints((0f, -15f), (7f, -3f), (11f, 13f), (4f, 8f), (0f, 14f), (-4f, 8f), (-11f, 13f), (-7f, -3f));
                p.Fill(Hex("0a0d18"), 0.85f);
                p.FillCircle(-3.4f, -6f, 2.4f);
                p.FillCircle(3.4f, -6f, 2.4f);
                p.Fill(Hex("9fe8ff"));
                p.FillCircle(-3.4f, -6f, 1f);
                p.FillCircle(3.4f, -6f, 1f);
                // 兜帽内沿 + 下摆的破碎感
                p.Line(1.6f, Lighten(c, 60f), 0.6f);
                p.StrokePolyline(new[] { (0f, -14f), (6f, -3f), (9f, 10f) }, Lighten(c, 40f), 0.9f);
                p.StrokePolyline(new[] { (0f, -14f), (-6f, -3f), (-9f, 10f) }, Lighten(c, 40f), 0.9f);
                break;

            case "troll":
                p.Fill(c);
                p.FillPoints((-14f, 14f), (-12f, -2f), (-5f, -12f), (5f, -12f), (12f, -2f), (14f, 14f));
                p.FillCircle(0f, -10f, 7f);
                p.Fill(Hex("1a1f30"));
                p.FillCircle(-3f, -11f, 1.8f);
                p.FillCircle(3f, -11f, 1.8f);
                p.Fill(Colors.White, 0.9f);
                p.FillRect(-4f, -5f, 2.4f, 5f);
                p.FillRect(2f, -5f, 2.4f, 5f);
                p.Fill(Colors.Black, 0.2f);
                p.FillCircle(10f, 2f, 3f);
                p.FillCircle(-10f, 6f, 3f);
                // 皮褶：三道弧线，把「壮」和「一块梯形」区分开
                p.Line(1.6f, Darken(c, 60f), 0.7f);
                p.Line(-8f, 4f, -2f, 2f);
                p.Line(0f, 8f, 7f, 6f);
                p.Line(-6f, -14f, -4f, -20f);
                break;

            case "lich":
                p.Fill(Hex("3a2f5a"));
                p.FillPoints((-12f, 14f), (-9f, -6f), (0f, -16f), (9f, -6f), (12f, 14f));
                p.Fill(Hex("d8dced"));
                p.FillCircle(0f, -7f, 7f);
                p.FillRect(-4f, -3f, 8f, 5f);
                p.Fill(Hex("8f6bff"));
                p.FillCircle(-3f, -8f, 2.4f);
                p.FillCircle(3f, -8f, 2.4f);
                p.Fill(c);
                p.FillStar(0f, 8f, 5, 5f, 2f);
                // 法袍符文 + 肩骨
                p.Line(1.4f, Lighten(c, 70f), 0.85f);
                p.StrokePolyline(new[] { (-8f, -4f), (-7f, 6f), (-9f, 13f) }, Alpha(c, 0.7f), 1.2f);
                p.StrokePolyline(new[] { (8f, -4f), (7f, 6f), (9f, 13f) }, Alpha(c, 0.7f), 1.2f);
                p.Fill(Hex("d8dced"), 0.9f);
                p.FillRect(-3.4f, -12.6f, 6.8f, 1.4f);
                break;

            case "boss":
                p.Fill(c);
                p.FillPoints((-15f, 14f), (-13f, 0f), (-6f, -12f), (6f, -12f), (13f, 0f), (15f, 14f));
                p.Fill(Hex("ffb347"));
                p.FillTriangle(-7f, -12f, -13f, -20f, -2f, -14f);
                p.FillTriangle(7f, -12f, 13f, -20f, 2f, -14f);
                p.Fill(Hex("ffe066"));
                p.FillCircle(-4f, -5f, 2.6f);
                p.FillCircle(4f, -5f, 2.6f);
                p.Fill(Hex("2a0d0d"));
                p.FillRect(-6f, 3f, 12f, 3f);
                p.Fill(Hex("ffe066"));
                for (var i = -4; i <= 4; i += 4)
                {
                    p.FillTriangle(i - 1.6f, 3f, i + 1.6f, 3f, i, 8f);
                }
                break;

            case "mimic":
                // 宝箱怪：和「宝箱」同色系但露出一排牙与舌头，避免玩家把陷阱当奖励
                p.Fill(Hex("7a5230"));
                p.FillRect(-12f, -3f, 24f, 13f);
                p.Fill(Hex("8f6339"));
                p.FillPoints((-12f, -3f), (12f, -3f), (10f, -13f), (-10f, -13f));
                p.Fill(Hex("3a0f14"));
                p.FillRect(-10f, -6f, 20f, 6f);
                p.Fill(Hex("e8e2cf"));
                for (var i = -8; i <= 8; i += 4)
                {
                    p.FillTriangle(i - 1.6f, -6f, i + 1.6f, -6f, i, -0.4f);
                }
                p.Fill(Hex("c0392b"));
                p.FillRect(-6f, -1.6f, 12f, 2.4f);
                p.Fill(c);
                p.FillRect(-12.6f, -13f, 3.6f, 22f);
                p.FillRect(9f, -13f, 3.6f, 22f);
                p.Fill(Hex("ffdd44"));
                p.FillCircle(-5.4f, -9f, 2.2f);
                p.FillCircle(5.4f, -9f, 2.2f);
                break;

            case "boss_molten":
                // 熔岩领主：岩颅 + 流淌的岩浆裂纹 + 背后升起的火角
                p.Fill(Hex("4a1c10"));
                p.FillPoints((-14f, 14f), (-12f, -2f), (-6f, -13f), (6f, -13f), (12f, -2f), (14f, 14f));
                p.Fill(c);
                p.FillCircle(0f, -11f, 8f);
                // 火角：两簇向上收窄的三角形，像角又像火苗
                p.Fill(Hex("ff8c1a"));
                p.FillTriangle(-8f, -15f, -12f, -24f, -3f, -17f);
                p.FillTriangle(8f, -15f, 12f, -24f, 3f, -17f);
                p.Fill(Hex("ffd23f"));
                p.FillTriangle(-7.5f, -16f, -10f, -21.5f, -4.5f, -17.5f);
                p.FillTriangle(7.5f, -16f, 10f, -21.5f, 4.5f, -17.5f);
                // 裂纹：胸口几条亮橙折线，岩浆从缝里透出来
                p.Line(2.4f, Hex("ffe066"), 1f);
                p.StrokePolyline(new[] { (-5f, -4f), (-3f, 1f), (-6f, 6f) }, Hex("ffe066"), 2.4f);
                p.StrokePolyline(new[] { (5f, -4f), (3f, 1f), (6f, 6f) }, Hex("ffe066"), 2.4f);
                p.Fill(Hex("ffe066"));
                p.FillCircle(0f, 9f, 2.2f);
                // 眼睛：炽白的瞳，压一层暗底
                p.Fill(Hex("2a0d0d"));
                p.FillCircle(-3.2f, -11.5f, 2.8f);
                p.FillCircle(3.2f, -11.5f, 2.8f);
                p.Fill(Hex("fff3c4"));
                p.FillCircle(-3.2f, -11.5f, 1.5f);
                p.FillCircle(3.2f, -11.5f, 1.5f);
                // 熔渣肩甲：两块深色岩片，给剪影加「重」
                p.Fill(Hex("2a1008"), 0.9f);
                p.FillRoundRect(-15f, -1f, 5f, 10f, 2f);
                p.FillRoundRect(10f, -1f, 5f, 10f, 2f);
                break;

            case "boss_frost":
                // 霜蚀女王：尖锐的冰冠 + 拖地的披风，气质偏冷而不是壮
                p.Fill(Hex("123a52"));
                p.FillPoints((0f, -14f), (9f, -6f), (13f, 4f), (9f, 15f), (-9f, 15f), (-13f, 4f), (-9f, -6f));
                p.Fill(Hex("1d5c7a"));
                p.FillTriangle(-13f, 4f, -11f, 15f, -5f, 6f);
                p.FillTriangle(13f, 4f, 11f, 15f, 5f, 6f);
                p.Fill(c);
                p.FillCircle(0f, -10f, 7f);
                // 冰冠：五根长短不一的尖刺
                p.Fill(Hex("a8ecff"));
                foreach (var (dx, dy) in new[] { (-6f, -3f), (-3f, -5f), (0f, -6f), (3f, -5f), (6f, -3f) })
                {
                    p.FillTriangle(dx - 2f, dy, dx + 2f, dy, dx, dy - 8f);
                }
                p.Fill(Hex("9fe8ff"));
                p.FillCircle(-3f, -10f, 1.6f);
                p.FillCircle(3f, -10f, 1.6f);
                // 霜纹：胸前一道冷光斜线
                p.Line(2f, Hex("d6f4ff"), 0.9f);
                p.StrokePolyline(new[] { (-4f, 0f), (0f, 5f), (4f, 1f) }, Hex("d6f4ff"), 2f);
                // 冰晶肩饰：三颗菱形，比尖刺更好看也更好认
                p.Fill(Hex("d6f4ff"), 0.85f);
                p.FillDiamond(-11f, -2f, 2.2f, 3.4f);
                p.FillDiamond(11f, -2f, 2.2f, 3.4f);
                p.FillDiamond(0f, 11f, 1.6f, 2.6f);
                break;

            case "boss_abyss":
                // 深渊之主：虚空的王座轮廓 + 环绕的暗能量，最重的一只
                p.Fill(Hex("140f2e"));
                p.FillPoints((-15f, 15f), (-13f, -3f), (-7f, -14f), (7f, -14f), (13f, -3f), (15f, 15f));
                p.Fill(Hex("241a4d"));
                p.FillCircle(0f, -9f, 9f);
                // 头顶悬浮的暗能量环：三块错位的菱形
                p.Fill(Hex("8f6bff"), 0.95f);
                p.FillPoints((0f, -26f), (4f, -21f), (0f, -16f), (-4f, -21f));
                p.Fill(Hex("c39bff"), 0.8f);
                p.FillPoints((-8f, -22f), (-5f, -18.5f), (-8f, -15f), (-11f, -18.5f));
                p.FillPoints((8f, -22f), (11f, -18.5f), (8f, -15f), (5f, -18.5f));
                // 披风内衬的暗紫裂缝
                p.Line(2.2f, Hex("c39bff"), 0.85f);
                p.StrokePolyline(new[] { (-6f, -3f), (-3f, 4f), (-7f, 10f) }, Hex("c39bff"), 2.2f);
                p.StrokePolyline(new[] { (6f, -3f), (3f, 4f), (7f, 10f) }, Hex("c39bff"), 2.2f);
                // 眼睛：没有瞳孔的紫光
                p.Fill(Hex("0a0d18"));
                p.FillCircle(-3.6f, -9.5f, 3f);
                p.FillCircle(3.6f, -9.5f, 3f);
                p.Fill(Hex("d9c2ff"));
                p.FillCircle(-3.6f, -9.5f, 1.4f);
                p.FillCircle(3.6f, -9.5f, 1.4f);
                // 王座扶手：两条竖棱，深渊的「重」靠它
                p.Fill(Hex("1d1540"), 0.9f);
                p.FillRect(-15.4f, -2f, 4f, 17f);
                p.FillRect(11.4f, -2f, 4f, 17f);
                break;

            case "boss_abyss_reversed":
                // 深渊之主·逆位：同一副骨架，但披风翻面、王冠倒置、能量环下沉
                p.Fill(Hex("0f1a2e"));
                p.FillPoints((-15f, -15f), (-13f, 3f), (-7f, 14f), (7f, 14f), (13f, 3f), (15f, -15f));
                p.Fill(Hex("2b3a6b"));
                p.FillCircle(0f, 9f, 9f);
                p.Fill(Hex("8f6bff"), 0.95f);
                p.FillPoints((0f, 26f), (4f, 21f), (0f, 16f), (-4f, 21f));
                p.Fill(Hex("ffd166"), 0.85f);
                p.FillPoints((-8f, 22f), (-5f, 18.5f), (-8f, 15f), (-11f, 18.5f));
                p.FillPoints((8f, 22f), (11f, 18.5f), (8f, 15f), (5f, 18.5f));
                p.Line(2.2f, Hex("ffd166"), 0.85f);
                p.StrokePolyline(new[] { (-6f, 3f), (-3f, -4f), (-7f, -10f) }, Hex("ffd166"), 2.2f);
                p.StrokePolyline(new[] { (6f, 3f), (3f, -4f), (7f, -10f) }, Hex("ffd166"), 2.2f);
                p.Fill(Hex("0a0d18"));
                p.FillCircle(-3.6f, 9.5f, 3f);
                p.FillCircle(3.6f, 9.5f, 3f);
                p.Fill(Hex("ffe9a8"));
                p.FillCircle(-3.6f, 9.5f, 1.4f);
                p.FillCircle(3.6f, 9.5f, 1.4f);
                break;

            // ---- 新敌人建模：按数据里每只的定位单独画，不再共用骷髅剪影 ----
            case "thorn_hound":
                // 荆棘猎犬：低伏的四足兽，背脊一排尖刺——「近身会被扎」
                p.Fill(c);
                p.FillPoints((-13f, 4f), (-6f, -4f), (6f, -4f), (13f, 1f), (12f, 8f), (-10f, 8f));
                p.FillCircle(11f, -1f, 6f);
                p.Fill(Darken(c, 50f));
                p.FillRect(-9f, 7f, 3.4f, 7f);
                p.FillRect(-1f, 7f, 3.4f, 7f);
                p.FillRect(6f, 7f, 3.4f, 7f);
                p.Fill(Hex("ffe066"));
                foreach (var dx in new[] { -7f, -1f, 5f })
                {
                    p.FillTriangle(dx - 3f, -3f, dx + 3f, -3f, dx, -11f);
                }
                p.Fill(Hex("1a1f30"));
                p.FillCircle(13f, -2f, 1.5f);
                // 棘刺上的暗根 + 腹部阴影：一排发光三角太「糖」，加暗底才像荆棘
                p.Fill(Darken(c, 70f), 0.8f);
                p.FillTriangle(-9f, -2f, -5f, -2f, -7f, -7f);
                p.FillTriangle(3f, -2f, 7f, -2f, 5f, -7f);
                p.Line(1.6f, Darken(c, 60f), 0.8f);
                p.Line(-8f, 3f, 4f, 3f);
                break;

            case "cave_bat":
                // 洞穴蝠：两片张开的大翼 + 尖耳，靠轮廓认出来
                p.Fill(c);
                p.FillCircle(0f, -4f, 6.5f);
                p.FillTriangle(-5f, -9f, -8f, -16f, -1f, -10f);
                p.FillTriangle(5f, -9f, 8f, -16f, 1f, -10f);
                p.Fill(Darken(c, 40f));
                p.FillPoints((-5f, -3f), (-16f, -8f), (-13f, 0f), (-15f, 6f), (-5f, 3f));
                p.FillPoints((5f, -3f), (16f, -8f), (13f, 0f), (15f, 6f), (5f, 3f));
                p.Fill(Hex("1a1f30"));
                p.FillCircle(-2f, -5f, 1.4f);
                p.FillCircle(2f, -5f, 1.4f);
                p.Fill(Colors.White, 0.9f);
                p.FillTriangle(-1.6f, -2f, 1.6f, -2f, 0f, 1.4f);
                // 翼骨：三道从肩到翼尖的细线，展开的翼才不是两块布
                p.Line(1.2f, Lighten(c, 50f), 0.7f);
                p.Line(-5f, -3f, -14f, -5f);
                p.Line(-5f, -1f, -13f, 3f);
                p.Line(5f, -3f, 14f, -5f);
                p.Line(5f, -1f, 13f, 3f);
                break;

            case "blood_bat":
                // 汲血蝙蝠：同族但更凶——翼更窄、口器带血滴，别和洞穴蝠混
                p.Fill(c);
                p.FillCircle(0f, -4f, 6f);
                p.FillTriangle(-4f, -8f, -7f, -15f, 0f, -9f);
                p.FillTriangle(4f, -8f, 7f, -15f, 0f, -9f);
                p.Fill(Darken(c, 60f));
                p.FillPoints((-4f, -2f), (-17f, -5f), (-11f, 1f), (-14f, 7f), (-4f, 3f));
                p.FillPoints((4f, -2f), (17f, -5f), (11f, 1f), (14f, 7f), (4f, 3f));
                p.Fill(Hex("ff4d4d"));
                p.FillTriangle(-2f, 0f, -0.6f, 0f, -1.3f, 4f);
                p.FillTriangle(2f, 0f, 0.6f, 0f, 1.3f, 4f);
                p.Fill(Hex("1a1f30"));
                p.FillCircle(-2f, -5f, 1.4f);
                p.FillCircle(2f, -5f, 1.4f);
                // 血翼脉络：比洞穴蝠多一层「血管」
                p.Line(1f, Hex("ff6b6b"), 0.7f);
                p.Line(-4f, -1f, -15f, -2f);
                p.Line(4f, -1f, 15f, -2f);
                p.Fill(Hex("ff4d4d"), 0.9f);
                p.FillCircle(-6f, 5f, 1f);
                p.FillCircle(6f, 5f, 1f);
                break;

            case "stone_warden":
                // 石像守望：方正的岩块身躯 + 额头刻纹，「硬」是第一印象
                p.Fill(Hex("4a5570"));
                p.FillPoints((-13f, 14f), (-12f, -6f), (-6f, -13f), (6f, -13f), (12f, -6f), (13f, 14f));
                p.Fill(c);
                p.FillRoundRect(-9f, -8f, 18f, 16f, 5f);
                p.Line(1.6f, Darken(c, 70f), 0.9f);
                p.StrokePolyline(new[] { (-8f, -4f), (-2f, 2f), (-6f, 8f) }, Darken(c, 70f), 1.6f);
                p.StrokePolyline(new[] { (8f, -4f), (3f, 1f) }, Darken(c, 70f), 1.6f);
                p.Fill(Hex("8ce2ff"), 0.9f);
                p.FillPoints((0f, -11f), (3f, -8f), (0f, -5f), (-3f, -8f));
                p.Fill(Hex("1a1f30"));
                p.FillRect(-6f, -2f, 4f, 2.6f);
                p.FillRect(2f, -2f, 4f, 2.6f);
                // 石缝：三道短裂纹 + 底面暗影，岩块的「硬」全靠它
                p.Line(1.2f, Darken(c, 80f), 0.8f);
                p.Line(-10f, 6f, -4f, 9f);
                p.Line(5f, 5f, 10f, 8f);
                p.Fill(Darken(c, 60f), 0.45f);
                p.FillRect(-9f, 5f, 18f, 3f);
                break;

            case "cursed_statue":
                // 诅咒雕像：和石像守望同族，但碎裂一半 + 紫光符眼，一眼知「有诅咒」
                p.Fill(Hex("3b3352"));
                p.FillPoints((-13f, 14f), (-11f, -4f), (-5f, -12f), (4f, -13f), (12f, -5f), (11f, 14f));
                p.Fill(c);
                p.FillRoundRect(-8f, -8f, 16f, 15f, 4f);
                p.Fill(Hex("0a0d18"), 0.55f);
                p.FillPoints((-13f, 14f), (-13f, -2f), (-7f, 2f), (-8f, 14f));
                p.Line(2f, Hex("c39bff"), 0.85f);
                p.StrokePolyline(new[] { (-11f, -2f), (-6f, 3f), (-9f, 9f) }, Hex("c39bff"), 2f);
                p.Fill(Hex("c39bff"));
                p.FillCircle(-3.4f, -3f, 2.2f);
                p.FillCircle(3.4f, -3f, 2.2f);
                p.Fill(Colors.White, 0.85f);
                p.FillCircle(-3.4f, -3f, 0.8f);
                p.FillCircle(3.4f, -3f, 0.8f);
                // 崩口：右肩缺一块 + 三条掉渣，比「半块黑影」更清楚
                p.Fill(Hex("1a1428"), 0.9f);
                p.FillTriangle(11f, -6f, 13f, 2f, 8f, -2f);
                p.Fill(Hex("5a4b7a"), 0.8f);
                p.FillRect(-1f, 10f, 2.4f, 2.4f);
                p.FillRect(4f, 12f, 1.8f, 1.8f);
                break;

            case "rust_golem":
                // 锈蚀巨像：比石像更壮，胸口一块锈斑 + 垂着两条铁链
                p.Fill(Hex("5b5340"));
                p.FillPoints((-15f, 15f), (-14f, -8f), (-7f, -14f), (7f, -14f), (14f, -8f), (15f, 15f));
                p.Fill(c);
                p.FillRoundRect(-11f, -9f, 22f, 18f, 6f);
                p.Fill(Hex("9c5a24"), 0.85f);
                p.FillCircle(-4f, 0f, 4.4f);
                p.FillCircle(4f, 2f, 3.2f);
                p.Fill(Darken(c, 45f));
                p.FillCircle(-15f, 4f, 4f);
                p.FillCircle(15f, 4f, 4f);
                p.Line(2.4f, Hex("7a6a4a"), 1f);
                p.Line(-6f, -13f, -8f, -20f);
                p.Line(6f, -13f, 8f, -20f);
                p.Fill(Hex("ffb347"));
                p.FillRect(-6f, -3f, 4f, 3f);
                p.FillRect(2f, -3f, 4f, 3f);
                // 铁链：三节小环垂在右肩，锈蚀的「老」全在这
                p.Line(1.6f, Hex("8a7a5a"), 0.9f);
                for (var i = 0; i < 3; i++)
                {
                    p.StrokeCircle(11f, -17f + i * 3f, 1.4f);
                }
                // 锈斑上的碎屑 + 甲缝
                p.Fill(Hex("6b3c12"), 0.8f);
                p.FillCircle(-6f, 4f, 1.6f);
                p.FillCircle(2f, 6f, 1.2f);
                p.Line(1.4f, Darken(c, 80f), 0.8f);
                p.Line(-9f, -6f, 9f, -6f);
                break;

            case "golem":
                // 石头傀儡：四方形躯干 + 无颈直接连着的脑袋，「没有脖子的重物」
                p.Fill(Hex("515b70"));
                p.FillPoints((-14f, 14f), (-13f, -2f), (-7f, -9f), (7f, -9f), (13f, -2f), (14f, 14f));
                p.Fill(c);
                p.FillRoundRect(-11f, -8f, 22f, 15f, 5f);
                p.Fill(Lighten(c, 18f));
                p.FillRoundRect(-6f, -16f, 12f, 9f, 3f);
                p.Line(1.5f, Darken(c, 70f), 0.9f);
                p.StrokePolyline(new[] { (-7f, -4f), (-1f, 2f), (-5f, 7f) }, Darken(c, 70f), 1.5f);
                p.StrokePolyline(new[] { (7f, -4f), (2f, 0f) }, Darken(c, 70f), 1.5f);
                p.Fill(Hex("8ce2ff"), 0.9f);
                p.FillRect(-4f, -14f, 3f, 2.4f);
                p.FillRect(1f, -14f, 3f, 2.4f);
                // 拳与石纹：方拳头是「傀儡」的另一半身份
                p.Fill(Darken(c, 30f));
                p.FillRoundRect(-16f, 4f, 6f, 8f, 2f);
                p.FillRoundRect(10f, 4f, 6f, 8f, 2f);
                p.Line(1.2f, Darken(c, 80f), 0.7f);
                p.Line(-9f, 9f, -2f, 7f);
                p.Line(2f, 10f, 9f, 8f);
                break;

            case "blood_serpent":
                // 血蛇：盘起的蛇身 + 抬起的蛇头，和四足兽、人形完全不同的轮廓
                p.Fill(Darken(c, 30f));
                p.FillPoints((-12f, 9f), (-4f, 2f), (6f, 5f), (12f, 11f), (6f, 13f), (-6f, 13f));
                p.Fill(c);
                p.FillPoints((-11f, 8f), (-4f, 3f), (5f, 6f), (11f, 10f), (6f, 12f), (-6f, 12f));
                p.Line(1.2f, Darken(c, 45f), 0.9f);
                p.Line(-7f, 5f, -5f, 9f);
                p.Line(0f, 4f, 2f, 8f);
                p.Fill(c);
                p.FillCircle(-2f, -8f, 5.4f);
                p.Fill(Hex("ff4d4d"));
                p.FillTriangle(-1f, -4f, 1.5f, -4f, 0.2f, 0f);
                p.Fill(Hex("1a1f30"));
                p.FillCircle(-4f, -9f, 1.3f);
                p.FillCircle(0f, -9f, 1.3f);
                // 鳞片：三排小弧，蛇的「鳞」是这次补细节的样板
                p.Line(0.9f, Lighten(c, 60f), 0.55f);
                for (var i = 0; i < 3; i++)
                {
                    p.StrokeArc(-6f + i * 4f, 7f + (i % 2) * 2f, 1.6f, Mathf.Pi * 1.1f, Mathf.Pi * 1.9f);
                }
                p.Fill(Darken(c, 60f), 0.7f);
                p.FillCircle(-2f, -11f, 2.6f);
                break;

            case "frost_archer":
                // 霜弓手：兜帽 + 横持的长弓，弓弦朝向玩家
                p.Fill(Hex("1d3a4d"));
                p.FillPoints((-8f, 14f), (-9f, -6f), (0f, -14f), (9f, -6f), (8f, 14f));
                p.Fill(c);
                p.FillCircle(0f, -7f, 6.5f);
                p.Fill(Hex("a8ecff"), 0.85f);
                p.FillTriangle(-6f, -8f, 6f, -8f, 0f, -1f);
                p.Line(2.6f, Hex("9fe8ff"), 1f);
                p.StrokeArc(8f, 0f, 11f, Mathf.Pi * 1.25f, Mathf.Pi * 1.75f);
                p.Line(1.2f, Hex("d6f4ff"), 0.8f);
                p.Line(11.6f, -8f, 11.6f, 8f);
                p.Fill(Hex("1a1f30"));
                p.FillCircle(-2.6f, -8f, 1.4f);
                p.FillCircle(2.6f, -8f, 1.4f);
                // 兜帽边 + 肩甲霜纹 + 搭在弦上的箭
                p.Line(1.6f, Darken(c, 60f), 0.9f);
                p.StrokePolyline(new[] { (-7f, -6f), (0f, -12f), (7f, -6f) }, Darken(c, 60f), 1.6f);
                p.Line(1.4f, Hex("d6f4ff"), 0.7f);
                p.Line(11.6f, 0f, 3f, 0f);
                p.Fill(Hex("d6f4ff"));
                p.FillTriangle(1.6f, 0f, 4.4f, -1.4f, 4.4f, 1.4f);
                break;

            case "frost_binder":
                // 霜缚者：双手张开施法，身周两串冰晶锁链
                p.Fill(Hex("24506b"));
                p.FillPoints((-7f, 14f), (-8f, -4f), (0f, -13f), (8f, -4f), (7f, 14f));
                p.Fill(c);
                p.FillCircle(0f, -8f, 6f);
                p.Fill(Hex("2b3a5c"));
                p.FillRect(-2f, -3f, 4f, 12f);
                p.Line(3.2f, Hex("9fe8ff"), 1f);
                p.Line(-6f, -2f, -13f, -9f);
                p.Line(6f, -2f, 13f, -9f);
                p.Fill(Hex("d6f4ff"), 0.95f);
                foreach (var (dx, dy, r) in new (float, float, float)[] { (-14f, -12f, 3.4f), (14f, -12f, 3.4f), (-10f, 8f, 2.8f), (10f, 8f, 2.8f) })
                {
                    p.FillPoints((dx, dy - r), (dx + r, dy), (dx, dy + r), (dx - r, dy));
                }
                p.Fill(Hex("1a1f30"));
                p.FillCircle(-2.4f, -9f, 1.4f);
                p.FillCircle(2.4f, -9f, 1.4f);
                // 冰链：手到冰晶之间的三节链环
                p.Line(1.4f, Hex("a8ecff"), 0.8f);
                for (var i = 0; i < 3; i++)
                {
                    p.StrokeCircle(-8.4f - i * 2f, -4f - i * 2f, 1.1f);
                    p.StrokeCircle(8.4f + i * 2f, -4f - i * 2f, 1.1f);
                }
                break;

            case "shadow_stalker":
                // 暗影潜伏者：裹身的斗篷 + 双臂反持的短刃，轮廓最「扁」的一只
                p.Fill(Hex("171a2c"));
                p.FillPoints((0f, -15f), (8f, -6f), (12f, 12f), (-12f, 12f), (-8f, -6f));
                p.Fill(c, 0.9f);
                p.FillPoints((0f, -13f), (6f, -6f), (9f, 11f), (-9f, 11f), (-6f, -6f));
                p.Fill(Hex("05060d"), 0.9f);
                p.FillCircle(0f, -8f, 4.6f);
                p.Fill(Hex("c39bff"), 0.95f);
                p.FillCircle(-1.8f, -8.4f, 1.2f);
                p.FillCircle(1.8f, -8.4f, 1.2f);
                p.Line(3f, Hex("8ce2ff"), 1f);
                p.Line(-7f, 2f, -15f, -4f);
                p.Line(7f, 2f, 15f, -4f);
                // 斗篷下摆的碎角 + 刃口高光：出手感全在这
                p.Fill(Hex("0b0d18"), 0.85f);
                p.FillTriangle(-9f, 11f, -4f, 11f, -6.6f, 15f);
                p.FillTriangle(4f, 11f, 9f, 11f, 6.6f, 15f);
                p.Line(1.2f, Hex("e8f6ff"), 0.85f);
                p.Line(-13.4f, -3.2f, -16.4f, -4.6f);
                p.Line(13.4f, -3.2f, 16.4f, -4.6f);
                break;

            case "crystal_priest":
                // 晶石祭司：长袍 + 头顶悬浮的水晶冠，支援位的气场
                p.Fill(Hex("2a3a5c"));
                p.FillPoints((-8f, 14f), (-10f, -2f), (0f, -12f), (10f, -2f), (8f, 14f));
                p.Fill(c);
                p.FillCircle(0f, -8f, 6f);
                p.Fill(Hex("8ce2ff"), 0.95f);
                p.FillPoints((0f, -20f), (4f, -15f), (0f, -10f), (-4f, -15f));
                p.FillPoints((-7f, -18f), (-4.6f, -14.6f), (-7f, -11.2f), (-9.4f, -14.6f));
                p.FillPoints((7f, -18f), (9.4f, -14.6f), (7f, -11.2f), (4.6f, -14.6f));
                p.Fill(Colors.White, 0.5f);
                p.FillTriangle(-1.4f, -16f, 1f, -16f, 0f, -12f);
                p.Line(1.8f, Hex("9fe8ff"), 0.9f);
                p.StrokePolyline(new[] { (-3f, 0f), (0f, 5f), (3f, 0f) }, Hex("9fe8ff"), 1.8f);
                p.Fill(Hex("1a1f30"));
                p.FillCircle(-2.4f, -9f, 1.3f);
                p.FillCircle(2.4f, -9f, 1.3f);
                // 袍摆晶簇 + 胸口宝石：支援位的「施法者」身份
                p.Fill(Hex("a8ecff"), 0.8f);
                p.FillDiamond(-5.4f, 10f, 1.8f, 3f);
                p.FillDiamond(5.4f, 10f, 1.6f, 2.6f);
                p.FillDiamond(0f, -3f, 2f, 3.2f);
                break;

            case "warden_priest":
                // 加护祭司：和晶石祭司同为长袍支援位，换成「盾徽 + 双臂张开」的守护姿态
                p.Fill(Hex("2b3550"));
                p.FillPoints((-8f, 14f), (-10f, -2f), (0f, -12f), (10f, -2f), (8f, 14f));
                p.Fill(c);
                p.FillCircle(0f, -8f, 6f);
                p.Fill(Hex("2b3a5c"));
                p.FillRect(-2f, -3f, 4f, 12f);
                p.Line(3f, Hex("7cc0ff"), 0.9f);
                p.Line(-6f, -2f, -13f, -3f);
                p.Line(6f, -2f, 13f, -3f);
                p.Fill(Hex("5aa9ff"), 0.95f);
                p.FillPoints((0f, 1f), (5f, -1f), (4f, 6f), (0f, 10f), (-4f, 6f), (-5f, -1f));
                p.Fill(Hex("1a1f30"));
                p.FillCircle(-2.4f, -9f, 1.3f);
                p.FillCircle(2.4f, -9f, 1.3f);
                // 盾徽上的十字 + 头顶光环：加护的「护」要一眼看出来
                p.Line(1.2f, Hex("dff0ff"), 0.9f);
                p.Line(0f, 1.4f, 0f, 8.6f);
                p.Line(-3f, 4.4f, 3f, 4.4f);
                p.Line(1.4f, Hex("cfe6ff"), 0.7f);
                p.StrokeArc(0f, -12.4f, 3.2f, Mathf.Pi * 1.15f, Mathf.Pi * 1.85f);
                break;

            case "zealot":
                // 狂信者：瘦高 + 高举的匕首 + 头顶火纹，走的是「狂热」而不是「壮」
                p.Fill(Hex("3a2f5a"));
                p.FillPoints((-6f, 14f), (-7f, -3f), (0f, -11f), (7f, -3f), (6f, 14f));
                p.Fill(c);
                p.FillCircle(0f, -8f, 5.6f);
                p.Line(2.8f, Darken(c, 40f), 1f);
                p.Line(5f, -3f, 11f, -12f);
                p.Fill(Hex("cfd8ea"));
                p.FillTriangle(9f, -14f, 13f, -20f, 11f, -12f);
                p.Fill(Hex("ff8c1a"), 0.9f);
                p.FillPoints((0f, -17f), (3.4f, -13f), (0f, -11f), (-3.4f, -13f));
                p.Fill(Hex("1a1f30"));
                p.FillCircle(-2f, -9f, 1.3f);
                p.FillCircle(2f, -9f, 1.3f);
                // 苦行带 + 血誓：袍上两道绑带，狂热感来自「自缚」
                p.Line(1.6f, Hex("8a3a3a"), 0.9f);
                p.Line(-6f, 2f, 5f, 5f);
                p.Line(-5f, 7f, 4f, 10f);
                p.Fill(Hex("c0392b"), 0.85f);
                p.FillCircle(0f, 8f, 1.4f);
                break;

            case "abyss_hound":
                // 深渊猎犬：四足 + 头生双角 + 身绕暗能量，比荆棘猎犬更「深渊」
                p.Fill(Hex("1b1436"));
                p.FillPoints((-13f, 3f), (-6f, -4f), (6f, -4f), (13f, 0f), (12f, 7f), (-10f, 7f));
                p.Fill(c);
                p.FillCircle(11f, -1f, 6f);
                p.Fill(Darken(c, 55f));
                p.FillRect(-9f, 6f, 3.4f, 7f);
                p.FillRect(-1f, 6f, 3.4f, 7f);
                p.FillRect(6f, 6f, 3.4f, 7f);
                p.Fill(Hex("c39bff"));
                p.FillTriangle(8f, -6f, 5f, -14f, 12f, -8f);
                p.FillTriangle(14f, -6f, 17f, -14f, 11f, -8f);
                p.Line(2f, Hex("8f6bff"), 0.85f);
                p.StrokePolyline(new[] { (-9f, -6f), (-4f, -9f), (1f, -6f) }, Hex("8f6bff"), 2f);
                p.Fill(Hex("d9c2ff"));
                p.FillCircle(13f, -2f, 1.4f);
                // 暗能量缠绕：脊背上一串小菱形
                p.Fill(Hex("8f6bff"), 0.75f);
                p.FillDiamond(-7f, -3f, 1.4f, 2.2f);
                p.FillDiamond(-2f, -5f, 1.2f, 2f);
                p.FillDiamond(3f, -3.6f, 1.1f, 1.8f);
                break;

            case "molten_hound":
                // 熔火猎犬：同族第四只，换成「裂开的岩皮 + 透出的岩浆」
                p.Fill(Hex("3a1710"));
                p.FillPoints((-13f, 3f), (-6f, -4f), (6f, -4f), (13f, 0f), (12f, 7f), (-10f, 7f));
                p.Fill(c);
                p.FillCircle(11f, -1f, 6f);
                p.Fill(Darken(c, 60f));
                p.FillRect(-9f, 6f, 3.4f, 7f);
                p.FillRect(-1f, 6f, 3.4f, 7f);
                p.FillRect(6f, 6f, 3.4f, 7f);
                p.Line(2.2f, Hex("ffd08a"), 0.95f);
                p.StrokePolyline(new[] { (-8f, -2f), (-4f, 1f), (-7f, 5f) }, Hex("ffd08a"), 2.2f);
                p.StrokePolyline(new[] { (8f, -6f), (11f, -2f) }, Hex("ffd08a"), 2.2f);
                p.Fill(Hex("ffb347"));
                p.FillCircle(13f, -2f, 1.6f);
                // 岩皮碎块 + 背部热缝：熔岩从缝里透出来
                p.Fill(Hex("1a0a06"), 0.9f);
                p.FillTriangle(-5f, -3f, -1f, -3f, -3f, 1f);
                p.FillTriangle(2f, -2f, 6f, -3f, 4f, 1.6f);
                p.Line(1.4f, Hex("ffe066"), 0.8f);
                p.Line(-10f, 2f, -2f, 3f);
                break;

            case "mimic_chest":
                // 兼容 TS 版 roomIcon('treasure') 里可能出现的别名
                p.Fill(Hex("8f6339"));
                p.FillRoundRect(-12f, -8f, 24f, 18f, 3f);
                p.Fill(c);
                p.FillRect(-3f, -4f, 6f, 9f);
                break;

            default:
                // 未知图标不能画成空白：一个空心菱形至少能让人看出「这里缺东西」。
                // 同时向记账笔报一笔——UiSmoke 靠它抓「名字没对上、静默退化成菱形」的 case。
                p.Probe?.HitDefault();
                p.Fill(c, 0.5f);
                p.FillDiamond(0f, 0f, 9f, 9f);
                if (outline)
                {
                    p.Line(1.4f, c, 0.8f);
                    p.StrokePoints((0f, -9f), (9f, 0f), (0f, 9f), (-9f, 0f), (0f, -9f));
                }
                break;
        }
    }

    /// <summary>颜色字面量简写，图标里出现频率极高</summary>
    private static Color Hex(string hex) => new(hex);

    private static Color Darken(Color c, float amount) => Painter.Darken(c, amount);

    private static Color Alpha(Color c, float a) => new(c.R, c.G, c.B, a);

    private static Color Lighten(Color c, float amount) => Painter.Lighten(c, amount);

    // ---- 映射表：和原版 icons.ts 的同名函数逐条对齐 ----

    /// <summary>物品 id → 图标。原版 itemIcon 的优先查表，再按槽位回落</summary>
    private static readonly Dictionary<string, string> ItemIcons = new()
    {
        ["rusty_sword"] = "sword",
        ["steel_sword"] = "sword",
        ["flame_blade"] = "sword",
        ["battle_axe"] = "sword",
        ["void_edge"] = "sword",
        ["leather_armor"] = "armor",
        ["chain_mail"] = "armor",
        ["dragon_plate"] = "armor",
        ["frost_mail"] = "armor",
        ["iron_ring"] = "ring",
        ["ruby_amulet"] = "amulet",
        ["hero_medal"] = "medal",
        ["health_potion"] = "flask",
        ["greater_potion"] = "flask",
        ["antidote"] = "herb",
        ["dragon_heart"] = "heart",
        ["bomb"] = "bomb",
    };

    /// <summary>技能 id → 图标，覆盖原版 skillIcon 的 13 条专属映射</summary>
    private static readonly Dictionary<string, string> SkillIcons = new()
    {
        ["fireball"] = "fire",
        ["heal"] = "sparkle",
        ["empower"] = "muscle",
        ["venom_spit"] = "flask",
        ["bone_throw"] = "skull",
        ["toxic_fog"] = "poison",
        ["soul_drain"] = "heart",
        ["frost_nova"] = "sparkle",
        ["thunderclap"] = "fire",
        ["stone_skin"] = "shield",
        ["dark_bolt"] = "rune",
        ["lava_burst"] = "fire",
        ["smash"] = "muscle",
    };

    /// <summary>道具 → 图标：先按 id 查专属，再按槽位回落（和原版 itemIcon 一致）</summary>
    public static string ForItem(string slot, bool cleanse = false, bool offensive = false) =>
        ForItem("", slot, cleanse, offensive);

    public static string ForItem(string id, string slot, bool cleanse = false, bool offensive = false)
    {
        if (id.Length > 0 && ItemIcons.TryGetValue(id, out var known)) return known;
        if (cleanse) return "flask";
        if (offensive) return "bomb";
        return slot switch
        {
            "weapon" => "sword",
            "armor" => "armor",
            "consumable" => "flask",
            _ => "ring",
        };
    }

    /// <summary>技能 id → 图标：先查专属表，再按技能类型 / 名字回落</summary>
    public static string ForSkill(string skillId, string type = "")
    {
        if (SkillIcons.TryGetValue(skillId, out var known)) return known;
        return type switch
        {
            "damage" => "sword",
            "heal" => "heart",
            "buff" => "muscle",
            "poison" => "poison",
            "drain" => "flask",
            "debuff_defense" => "armor",
            "debuff_attack" => "sword",
            "evade" => "swap",
            "stun" => "rune",
            "dispel" => "sparkle",
            _ => skillId.Contains("fire") || skillId.Contains("molten") ? "fire" : "rune",
        };
    }

    /// <summary>房间类型 → 地图符号。原版 roomIcon 的 11 条映射，逐条照抄</summary>
    public static string ForRoom(string type) => type switch
    {
        "Start" => "flag",
        "Empty" => "door",
        "Enemy" => "sword",
        "Treasure" => "chest",
        "Shop" => "cart",
        "Event" => "question",
        "Altar" => "sparkle",
        "Training" => "muscle",
        "Curse" => "rune",
        "Elite" => "shield",
        "Boss" => "skull",
        _ => "rune",
    };

    /// <summary>
    /// 有专属造型的图标清单。新增一个 case 就必须在这里登记：
    /// UiSmoke 会遍历它，验证「登记过的都能画出来、且不落进 default」。
    /// 上一版漏了这些断言，几个 case 因为没写而静默退化成默认菱形，没人发现。
    /// </summary>
    public static readonly string[] Kinds =
    {
        "sword", "shield", "flask", "poison", "bomb", "ring", "armor", "amulet", "medal",
        "fire", "sparkle", "muscle", "swap", "coin", "heart", "skull", "chest", "cart", "herb",
        "question", "flag", "rune", "door", "player", "trophy",
        "rat", "slime", "skeleton", "goblin", "orc", "wraith", "troll", "lich", "golem",
        "blood_serpent", "thorn_hound", "cave_bat", "stone_warden", "frost_archer",
        "rust_golem", "shadow_stalker", "abyss_hound", "crystal_priest", "molten_hound",
        "warden_priest", "zealot", "blood_bat", "cursed_statue", "frost_binder",
        "mimic", "boss", "boss_molten", "boss_frost", "boss_abyss", "boss_abyss_reversed",
    };
}
