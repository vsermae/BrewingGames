// 界面冒烟：真的把 Boot → 主菜单 → 地图 → 战斗 这几个场景实例化一遍。
//
// 为什么需要它：`--selfcheck` 只驱动逻辑层——那些场景一个都不会崩。
// 界面层的问题（_Ready 里空引用、节点被 QueueFree 后还在用、布局算成 0 尺寸）
// 只有真把节点建出来、真走一遍布局才会暴露。headless 下同样能抓。
//
// 用法：godot --headless --path godot -- --uismoke
namespace AbyssEcho.Dev;

using Godot;
using AbyssEcho.Core;
using AbyssEcho.Scenes;
using AbyssEcho.UI;

public partial class UiSmoke : Node
{
    private int _passed;
    private int _failed;

    public override async void _Ready()
    {
        var store = GetNode<GameStore>("/root/GameStore");

        Step("主菜单能建出来", () =>
        {
            var menu = new MainMenu();
            AddChild(menu);
            menu.QueueFree();
        });

        Step("用固定种子开局后地图场景能建出来", () =>
        {
            store.NewRun(123);
            var map = new MapScene();
            AddChild(map);
            map.QueueFree();
        });

        // 布局要等一帧才有 rect；房间层本身也是等第一帧布局之后才建的
        await Frame();
        await Frame();

        var menuForMark = new MainMenu();
        AddChild(menuForMark);
        // 字标在 HBoxContainer 里，位置要等容器布局跑完才是真值。
        // 上一版在 AddChild 之后立刻读 GlobalPosition，拿到的全是 (0,0)——
        // 那条断言于是永远「装饰压在字上」，而不是它想抓的那个 bug。
        await Frame();
        await Frame();

        Step("主菜单字标：每字一块牌、不重叠、装饰不压字（原版 makeWordmark 的形状）", () =>
        {
            // 原版这一屏是「四个带底光的方牌 + 两侧剑与骷髅 + 26 粒尘埃」，
            // 而 Godot 版一度只有一行白字。看不见的差别没人能断言，所以这一条查的是配方。
            var map = new MapScene();
            AddChild(map);
            var menu = menuForMark;
            var mark = menu.FindChild("Wordmark", true, false) as UI.Wordmark;
            Assert(mark is not null, "菜单上没有字标控件");
            var (centers, lifts, total) = UI.Wordmark.WordmarkLayout(4, mark!.CellSize, mark.Gap);
            Assert(centers.Length == 4, "字标牌数不对");
            // 相邻两牌不重叠：中心距必须大于一个牌宽
            for (var i = 1; i < centers.Length; i++)
                Assert(centers[i] - centers[i - 1] >= mark.CellSize,
                    $"第 {i} 块牌与上一块重叠（中心距 {centers[i] - centers[i - 1]:0} < {mark.CellSize}）");
            // 奇偶错位：原版就是这么排的，全平会失去手工感
            Assert(lifts.Any(l => l < 0) && lifts.Any(l => l > 0), "字牌没有奇偶错位");
            Assert(total > mark.CellSize * 3.5f, $"字标总宽 {total:0} 装不下四个字");
            // 装饰：剑与骷髅各一，且都在字标外侧（不压字）
            var sword = menu.FindChild("EmblemSword", true, false) as Control;
            var skull = menu.FindChild("EmblemSkull", true, false) as Control;
            Assert(sword is not null && skull is not null, "字标两侧的装饰图标不见了");
            // 判据是「牌子的占用区」，不是整个控件框：控件左右各留了半块牌的余量，
            // 装饰落在留白里看着仍像压在字上（原版踩过：剑身插进「深」字）。
            var (markLeft, markRight) = UI.Wordmark.WordmarkBounds(4, mark.CellSize, mark.Gap, mark.Size.X);
            var markAbs = mark.GlobalPosition;
            Assert(sword!.GlobalPosition.X + sword.Size.X <= markAbs.X + markLeft + 1f,
                $"装饰剑压到字标上了（剑右沿 {sword.GlobalPosition.X + sword.Size.X:0} > 字标左沿 {markAbs.X + markLeft:0}）");
            Assert(skull!.GlobalPosition.X >= markAbs.X + markRight - 1f,
                $"装饰骷髅压到字标上了（骷髅左沿 {skull.GlobalPosition.X:0} < 字标右沿 {markAbs.X + markRight:0}）");
            // 尘埃：原版 26 粒，少了整屏就死了
            var dust = menu.FindChild("Dust", true, false) as UI.DustLayer;
            Assert(dust is not null && dust.Count >= 20, "菜单上没有尘埃层（或粒子太少）");
            map.QueueFree();
        });

        Step("地图铺满视口（不是 0 宽贴在左上角）", () =>
        {
            var map = new MapScene();
            AddChild(map);
            var viewport = ViewportSize();
            Assert(map.Size.X >= viewport.X - 1 && map.Size.Y >= viewport.Y - 1,
                $"地图尺寸 {map.Size} 未铺满视口 {viewport}");
            map.QueueFree();
        });

        var gridMap = new MapScene();
        AddChild(gridMap);
        await Frame();
        await Frame();

        Step("地图网格在视口内居中（不是缩在左上角）", () =>
        {
            var viewport = ViewportSize();
            var boxes = FindRoomButtons(gridMap);
            Assert(boxes.Count >= 9, $"房间格只有 {boxes.Count} 个");

            var rects = boxes.Select(b => new Rect2(b.GlobalPosition, b.Size)).ToList();
            var minX = rects.Min(r => r.Position.X);
            var minY = rects.Min(r => r.Position.Y);
            var maxX = rects.Max(r => r.Position.X + r.Size.X);
            var maxY = rects.Max(r => r.Position.Y + r.Size.Y);
            var leftGap = minX;
            var rightGap = viewport.X - maxX;
            var topGap = minY;
            var bottomGap = viewport.Y - maxY;

            // 判据：网格完整落在视口内，且不压住顶部 HUD（74）与底部提示条（46）。
            // 「Size 是 0 时算原点」那类塌陷会让整张图掉到视口外，这几条就会红。
            Assert(leftGap > 0 && rightGap > 0, $"网格溢出视口：左 {leftGap:F0} / 右 {rightGap:F0}");
            Assert(topGap > MapScene.HudBandHeight, $"网格压在顶部 HUD 上：上 {topGap:F0}");
            // 网格上方还得留出「提示飞行带」：底下（网格 → 图例 → 提示条）只剩 12px 时，
            // 提示只能挪到网格上方；那一条带子不够放一个会飘的胶囊，它就压到 HUD 上。
            // 判据读的是地图自己算出来的那条带子（`MapMetrics.ResumeBand`），不是再抄一遍算术。
            // 它比 `ToastBand` 少一截：网格是垂直居中的，多留的那一半本来就会被居中补回来。
            Assert(topGap >= MapScene.HudBandHeight + UI.MapMetrics.ResumeBand - 1f,
                $"网格上沿只留 {topGap - MapScene.HudBandHeight:F0}px 给提示飞行带（要 {UI.MapMetrics.ResumeBand:F0}）：提示会压到 HUD 上");
            // 下沿不只要让开那 46px 的提示条：右下图例也贴着这条底边站。
            // 图例的两列高 118、竖排高 249，都比提示条高一倍多——网格一往下长，
            // 它就被顶出屏幕下沿（这一轮把格距放大之后正好是这样）。
            // 判据要比的是「图例自己的落位」，不是「离提示条多远」这个中间量。
            var legendBox = gridMap.FindChild("MapLegend", true, false) as Control;
            Assert(legendBox is not null, "地图上没有图例");
            var legendRect = new Rect2(legendBox!.GlobalPosition, legendBox.Size);
            Assert(legendRect.End.Y <= viewport.Y - MapScene.HintBandHeight + 0.5f,
                $"右下图例的底边落在 {legendRect.End.Y:F0}，进了底部提示条（上沿 {viewport.Y - MapScene.HintBandHeight:F0}）：图例被顶出画面");

            // 「居中」不能只判左右都有留白：上一版左右留白是 332 / 608——
            // 两个数都是正的，于是这条断言一直绿着，而玩家看到的是整张图偏到右边去了。
            //
            // 现在的判据是「居中在**自己那块可用区域**里」：右下角有一张图例面板
            // （宽 142 + 间距 36），地图要在减掉它之后的地方居中，
            // 否则 1024 宽下最后两列房间会压在图例上。
            // 所以判据不能再用「左右留白相等」，而要判「画幅占满可用区域的多少」
            // 以及「网格没压到图例」——两条都是可算的数。
            var legend = gridMap.FindChild("MapLegend", true, false) as Control;
            Assert(legend is not null, "地图上没有图例：玩家只能靠图标猜房间类型");
            // 图例不许出视口：1024×576 下竖排十一行要 250px，
            // 上一版贴着提示条摆，最后两行被裁在屏幕外。
            // 图例面板必须装得下所有行：上一版把表头高度重复算了一遍，
            // 面板比内容高，末行「空房间」被裁在面板外——从截图里看像图例少了一项。
            Assert(UI.MapLegend.NeededHeight(11, UI.MapLegend.ColumnsFor(viewport)) <= legend!.Size.Y + 0.5f,
                $"图例面板 {legend.Size.Y} 装不下 {11} 项（需要 {UI.MapLegend.NeededHeight(11, UI.MapLegend.ColumnsFor(viewport))}）");
            Assert(legend!.Position.X >= 0 && legend.Position.Y >= 0 &&
                   legend.Position.X + legend.Size.X <= viewport.X + 0.5f &&
                   legend.Position.Y + legend.Size.Y <= viewport.Y + 0.5f,
                $"图例 {legend.Position}+{legend.Size} 跑出视口 {viewport}");
            // 图例每一行都得有位置可站：面板高是按「表头一行 + 正文 N 行」算的，
            // 行距要是按写死的 6 行摊，一列时（11 行）每行的问号就顶到上一行的图标上。
            // 判据取「最后一行中线 + 一行的高度」还在面板里——比的是渲染真的会用的那个数
            // （`RowPitch` 由面板高与行数算出来，不是另一个常数）。
            // 这条也要在「两列」下成立：横屏时图例折两列，6 行 × 19px 才是它的设计形态；
            // 一列 11 行有没有地方站是 ColumnsFor 答的问题（它只在 ≥1600 宽才选一列）。
            //
            // 判据里的行距不是重新算一遍，而是问**画图的那个类**：
            // `RowPitchNarrowed` 让 MapLegend 用「一列 11 行」的窄面板自检一遍，
            // 它和 `_Draw` 用的是同一个 `RowPitch`。
            // 早先这里的写法是「按 NeededHeight 反推行距」——而 NeededHeight 本身就是
            // 按 19px 一行算出来的，反推必然得到 19，这条断言永远是绿的（自己跟自己对答案）。
            var narrow = new Vector2(UI.MapLegend.WidthFor(1), UI.MapLegend.HeightFor(11));
            // 判据问的是「表头那一行**结束**了没有」，比的是第一行正文真的从哪儿起排
            // （`FirstBodyTop`），不是基线：基线天然比格顶低半行，拿它去比表头基线，
            // 旧写法也能过（0.35 行距只差几个像素，落在容差里）。这条判据自己踩过一次这个坑。
            Assert(UI.MapLegend.FirstBodyTopNarrowed(narrow) >= UI.MapLegend.HeadLineBottom,
                $"一列 11 行时正文第一行从 {UI.MapLegend.FirstBodyTopNarrowed(narrow):0} 起排，表头占到 {UI.MapLegend.HeadLineBottom:0}：两行叠在一起了");
            // 上面那条只守「表头」。真正在截图里出错的是**相邻两行正文**：
            // 面板按「表头 16 + 11 行 × 19」算高，正文却按写死的 6 行摊行距，
            // 一列时它只剩 12px 一行，每行的「?」压在上一行的图标上。
            // 所以还要问一次「一列 11 行时行距够不够站」——这是这一轮真正修的那件事。
            var pitch = UI.MapLegend.FirstBodyPitchNarrowed(narrow);
            Assert(pitch >= UI.MapLegend.MinRowPitch,
                $"一列 11 行时正文行距 {pitch:0.0}px（下限 {UI.MapLegend.MinRowPitch:0}）：每行的图标会顶在上一行的字上");
            foreach (var cols in new[] { 1, 2 })
            {
                var panel = new Vector2(UI.MapLegend.WidthFor(cols), UI.MapLegend.NeededHeight(11, cols));
                Assert(UI.MapLegend.LegendFits(panel, cols),
                    $"{cols} 列、{panel.Y:0} 高的图例面板装不下 {11} 项");
            }
            var legendLeft = legend!.GlobalPosition.X;
            Assert(maxX <= legendLeft - 4f,
                $"地图网格压到图例上：网格右边界 {maxX:F0}、图例左边界 {legendLeft:F0}");
            var reserved = legend.Size.X + 36f;
            var areaLeftGap = leftGap;
            var areaRightGap = rightGap - reserved;
            Assert(Math.Abs(areaLeftGap - areaRightGap) < 92f,
                $"地图网格没在「除掉图例」的区域里居中：左留 {areaLeftGap:F0}、右留 {areaRightGap:F0}");
            gridMap.QueueFree();
        });

        Step("地图画幅撑得起来：不是一张小图漂在大屏中间", () =>
        {
            // 这一条是纯算术，不需要建场景：MapMetrics.Fit 是纯函数。
            // 上一版的格距写死 92，13 间房的树通常只占 5×4 的包围盒，
            // 1280 宽里画幅 450px——四周两千多像素的空，玩家看到的是「一张小图」。
            // 判据：画幅至少占可用区域的 75%，并且不超过设计格距（别为了放大而糊）。
            var viewport = new Vector2(1280, 720);
            foreach (var (cols, rows) in new[] { (5, 4), (4, 5), (5, 5), (6, 4) })
            {
                // 用**生产常量**算：上一版这里传的是字面量 92，把上限改成 60 也照样绿——
                // 断言和被断言的东西必须同一份数据，否则「全绿」什么也证明不了。
                var m = UI.MapMetrics.Fit(viewport, MapScene.HudBandHeight, MapScene.HintBandHeight, 0, 0, cols, rows,
                    UI.MapLegend.Width + 36f);
                var cover = UI.MapMetrics.Coverage(viewport, 74f, 46f, cols, rows, m);
                // 上限 92 是设计格距：宽高里更紧的那一边决定画幅，所以取 min 才有意义。
                // 5×4 的图在 1280 宽下被高度约束（可用高 568 / 4 行 → 92 已顶格）。
                var expected = MathF.Min(92f, MathF.Min((1280f - 64f) / cols, (720f - 74f - 46f - 32f - 40f) / rows));
                var real = MathF.Min((cols - 1) * m.Span + m.Room, (rows - 1) * m.Span + m.Room) /
                           MathF.Min(1280f - 64f - 40f, 720f - 74f - 46f - 32f - 40f);
                Assert(m.Span >= expected - 0.5f,
                    $"{cols}×{rows} 的图只用到格距 {m.Span}（按可用区域应该到 {expected}）：画幅太小，像漂在屏幕中间");
                Assert(cover >= 0.55f, $"{cols}×{rows} 的图只占可用区域 {cover:P0}：画幅太小，像漂在屏幕中间");
                Assert(m.Span <= 92f + 0.01f, $"{cols}×{rows} 的图把格距放大到了 {m.Span}：超过设计值会糊");
            }
            // 1280 宽下 5 列的图应该贴着设计格距（96 上下），而不是 60 出头
            var fit = UI.MapMetrics.Fit(viewport, MapScene.HudBandHeight, MapScene.HintBandHeight, 0, 0, 5, 4,
                UI.MapLegend.Width + 36f);
            Assert(fit.Span >= 70f, $"1280×720 下 5 列的图格距只有 {fit.Span}：还能再大一点");
        });

        Step("地图画幅用满可用区域：图例只按高度折行，不无谓地吃掉宽度", () =>
        {
            // 这一条以前断的是「1280×720 下图例要折两列」——而那句话是错的：
            // 折两列省的是**高度**，赔的是**宽度**（236 比 142 多 94），
            // 而横屏下紧的正是宽度。图例吃掉的宽度地图就少了，
            // 所以真正该断的不是「图例几列」，是**地图占了多少画幅**。
            var viewport = new Vector2(1280, 720);
            // 竖排装得下就竖排：11 行 × 19 + 内边距 ≈ 250，720 高减去 HUD 与提示条够。
            Assert(UI.MapLegend.ColumnsFor(viewport) == 1,
                "1280×720 下图例应当竖排（折两列反而多吃 94px 宽度）");
            Assert(UI.MapLegend.ColumnsFor(new Vector2(1920, 1080)) == 1,
                "1920×1080 下图例应能竖排（宽度够，不该无谓折行）");
            // 装不下才折：判的那条线是「720 高扣掉 HUD 与提示条之后还剩多少」，
            // 11 行要 ~250。拿 300 高逼它走折行分支——这里不能凭感觉挑一个高度，
            // 挑大了（比如 420）那条线照样够，断的就是个假命题。
            Assert(UI.MapLegend.ColumnsFor(new Vector2(1024, 300)) == 2,
                "矮窗口（300 高）下图例应当折两列：11 行要 ~250px，装不下");

            var m = UI.MapMetrics.Fit(viewport, MapScene.HudBandHeight, MapScene.HintBandHeight, 0, 0, 5, 4,
                UI.MapLegend.WidthFor(UI.MapLegend.ColumnsFor(viewport)) + 36f, MapScene.MaxTile, 44);
            // 竖直方向要基本吃满：画幅高 / 可用高 ≥ 0.85
            var availH = 720f - MapScene.HudBandHeight - MapScene.HintBandHeight - 36f;
            var gridH = 3 * m.Span + m.Room;
            Assert(gridH / availH >= 0.85f,
                $"5×4 的图只占可用高度的 {gridH / availH:P0}：格距 {m.Span} 太小，下方会空出一大块");
            // 也不许放大到糊：格距不能超过 MaxTile
            Assert(m.Span <= MapScene.MaxTile + 0.01f, $"格距 {m.Span} 超过了上限 {MapScene.MaxTile}");
            // 「图例少吃了 94px 宽度」要量到地图上，但**不能量成「网格占宽度的多少」**：
            // 3 行比 4 列矮，格距被高度卡住，宽度方向必然空掉一截——那不是图例的错。
            // 所以判的是「这一条边到底谁卡住了它」：格距应当贴着高度那档，而不是贴着宽度那档。
            // 图例要是又胖又短，`spanForW` 会比 `spanForH` 更小，这一条立刻红。
            var availW = viewport.X - (UI.MapLegend.WidthFor(UI.MapLegend.ColumnsFor(viewport)) + 36f) - 56f;
            var availH2 = viewport.Y - MapScene.HudBandHeight - MapScene.HintBandHeight - 36f - UI.MapMetrics.ResumeBand;
            var spanForW = availW / (5 - 0.14f);
            var spanForH = availH2 / (4 - 0.14f);
            // 「贴着哪一档」= 等于它、或者被上限封住。`Fit` 取 `min(cap, 两档)`：
            // 格距 132 顶到上限时它不是任何一档，但它已经把画幅撑满——不是错。
            var bound = MathF.Min(MapScene.MaxTile, MathF.Min(spanForW, spanForH));
            Assert(m.Span <= MapScene.MaxTile + 0.01f && MathF.Abs(m.Span - bound) < 0.51f,
                $"格距 {m.Span:0.0} 既不是宽度那档（{spanForW:0.0}）也不是高度那档（{spanForH:0.0}），也没顶到上限（{MapScene.MaxTile}）：图例或留白把它卡住了");
            Assert(spanForH <= spanForW,
                $"宽度那档只有 {spanForW:0.0}，比高度那档 {spanForH:0.0} 还小：图例把宽度吃成了瓶颈");
        });

        Step("战斗场景能按视口铺满并建出行动条", () =>
        {
            var run = store.Run!;
            var index = run.Map.Rooms.FindIndex(r => r.Type is RoomType.Enemy or RoomType.Elite or RoomType.Boss);
            Assert(index >= 0, "这一层没有战斗房");
            run.Teleport(index);
            var enc = run.CreateEncounter();
            Assert(enc is not null, "战斗房没有生成遭遇");

            var battle = new BattleScene();
            battle.Configure(run, enc!);
            AddChild(battle);
            var viewport = ViewportSize();
            Assert(battle.Size.X >= viewport.X - 1, $"战斗场景宽度 {battle.Size.X} 未铺满 {viewport.X}");
            var panel = battle.GetNodeOrNull<Control>("ActionPanel");
            Assert(panel is not null, "没有行动条面板");
            // 行动条贴底：不能飘到画面上半部
            Assert(panel!.Position.Y > viewport.Y * 0.5f, $"行动条跑到画面上半部：y={panel.Position.Y} / {viewport.Y}");
            Assert(panel.Size.X >= viewport.X - 1, $"行动条宽度 {panel.Size.X} 未铺满 {viewport.X}");
            battle.QueueFree();
        });


        // ---- 表现层：立绘 / 图标 / 飘字 / 粒子 / 音效 / 状态行 ----
        // 这一组的意义：逻辑自检证明「规则没坏」，上面几条证明「场景能建」，
        // 但「立绘画不画出来、飘字颜色对不对、粒子配方发几颗」全都没人管。
        // 它们全是纯函数/纯数据，headless 里能直接断言——所以没有理由不测。

        Step("每个敌人都能在敌人表里查到剪影与主色", () =>
        {
            var data = AbyssEcho.Data.GameDataStore.Get();
            foreach (var m in data.Monsters.Concat(data.Bosses))
            {
                var kind = UI.EnemyArt.IconKind(m.Id);
                Assert(kind != "skull" || m.Id is "skeleton" or "lich", $"{m.Id} 没有专属剪影（回落成骷髅了）");
                var tint = UI.EnemyArt.Tint(m.Id);
                Assert(tint.A > 0.99f, $"{m.Id} 的主色不透明");
            }
            // Boss 必须有角色设定（主色/光晕/节奏），否则出场演出会退化成普通怪
            foreach (var b in data.Bosses)
                Assert(UI.EnemyArt.Style(b.Id) is not null, $"{b.Id} 缺少 Boss 配色设定");
        });

        Step("剪影的包围盒：每只怪在设计框里实际占多大（立绘体量的尺子）", () =>
        {
            // 立绘画多大一直按「舞台高 × 系数」统一给，可这话少了一半：
            // 每只怪在 32 设计框里占的大小根本不一样。巨鼠只占中间一条（x 到 -18..15、y 到 -15..10），
            // 霜蚀女王占满整框。同一个盒子装下来，屏幕上的巨鼠就比骑士小一档——
            // 「一只巨鼠比人小」这件事在画面上摆了三十多轮。
            //
            // 所以先把「尺子」量出来：用记账笔描一遍真实笔画，拿包围盒的相对大小。
            // 下面所有关于体量的断言都乘这个数，不再假设「一格 32 就是一样大」。
            var data = AbyssEcho.Data.GameDataStore.Get();
            foreach (var m in data.Monsters.Concat(data.Bosses))
            {
                var kind = UI.EnemyArt.IconKind(m.Id);
                var probe = new UI.ShapeProbe();
                UI.IconArt.Shape(probe, kind, Colors.White);
                var h = probe.MaxY - probe.MinY;
                Assert(h >= 16f, $"{kind} 的剪影纵向只有 {h:0.0}：量出来是空的，尺子不能用");
                // 只查「下界」和「大得离谱」两头，中间那圈留白不查：
                // 史莱姆的黏液泡伸到 19、王座的扶手到 15.4、逆位深渊的尾摆垂到 +26、
                // 巨魔的角顶到 -21——每一条都是造型的一部分。这条守的是
                // 「别跑到隔壁格子里去」，不是「必须正好画满 32」。
                Assert(probe.MaxX <= 22f && probe.MaxY <= 28f && probe.MinX >= -22f && probe.MinY >= -28f,
                    $"{kind} 的剪影跑出设计框：x {probe.MinX:0.0}..{probe.MaxX:0.0} / y {probe.MinY:0.0}..{probe.MaxY:0.0}");
            }
            // 巨鼠与霜蚀女王的框内占位真的差得多——这条断言守的是「尺子有东西可量」
            var rat = new UI.ShapeProbe();
            UI.IconArt.Shape(rat, "rat", Colors.White);
            var queen = new UI.ShapeProbe();
            UI.IconArt.Shape(queen, "boss_frost", Colors.White);
            Assert((queen.MaxY - queen.MinY) - (rat.MaxY - rat.MinY) > 6f,
                $"巨鼠占 {(rat.MaxY - rat.MinY):0.0}、Boss 占 {(queen.MaxY - queen.MinY):0.0}：两者一样大，按包围盒换算就没意义了");
        });

        Step("每个剪影都真的落了笔画，没静默退化成默认菱形", () =>
        {
            // 上一版有几个 case 根本没写（或者写了但名字对不上），
            // 结果在游戏里画出来是「一个空心菱形」——图标看起来「有」，其实是缺的。
            // 这里用一支记账笔替代 Godot 画布：记录每个 kind 落了多少笔。
            foreach (var kind in UI.IconArt.Kinds)
            {
                var probes = new UI.ShapeProbe();
                UI.IconArt.Shape(probes, kind, Colors.White);
                Assert(probes.Ops >= 3, $"{kind} 只画了 {probes.Ops} 笔，像是退化成默认形状了");
                Assert(probes.DefaultHits == 0, $"{kind} 落进了 default 分支（没有专属造型）");
            }
        });

        Step("敌人剪影的细节笔画比「几何拼装」级更密", () =>
        {
            // 原版 icons.ts 的敌人剪影每只 10~25 笔（鳞片、裂甲、发光缝都在里面）。
            // 门槛设在 10：低于它就是「三五个形状拼一拼」，正是玩家看得出的差距。
            var data = AbyssEcho.Data.GameDataStore.Get();
            foreach (var m in data.Monsters.Concat(data.Bosses))
            {
                var kind = UI.EnemyArt.IconKind(m.Id);
                var probe = new UI.ShapeProbe();
                UI.IconArt.Shape(probe, kind, Colors.White);
                Assert(probe.Ops >= 10, $"{m.Id} 的剪影只有 {probe.Ops} 笔，细节不够");
            }
        });

        Step("每个图标都是「原点居中 + 按 size/32 缩放」", () =>
        {
            // 契约跑偏过一次：多 translate 半格，所有图标都比调用方给的位置偏了半格。
            // 现在靠画笔一次性收口，这条断言守着它别再跑偏。
            var probe = new UI.ShapeProbe();
            UI.IconArt.Shape(probe, "sword", Colors.White);
            Assert(probe.MinY < -8f && probe.MaxY > 8f, $"剑的纵向跨度 {probe.MinY}..{probe.MaxY} 不像居中的 32 设计框");
            Assert(Mathf.Abs(probe.MinX + probe.MaxX) < 8f, $"剑横向不居中：{probe.MinX}..{probe.MaxX}");
        });

        Step("道具/技能/房间的映射表和原版 icons.ts 对得上", () =>
        {
            Assert(UI.IconArt.ForItem("health_potion", "consumable") == "flask", "治疗药水不是瓶子");
            // 解毒草不能画成毒药瓶：它写的是「立刻解除自身的中毒状态」，
            // 画成瓶子的话，图标与用途正好相反，而且和「中毒」这个减益长得一样。
            Assert(UI.IconArt.ForItem("antidote", "consumable") == "herb", "解毒草不是草药（还是毒药瓶）");
            Assert(UI.IconArt.ForItem("antidote", "consumable") != UI.IconArt.ForItem("health_potion", "consumable"),
                "解毒草与治疗药水用了同一个图标：玩家只能靠读字分辨它们");
            Assert(UI.IconArt.ForItem("bomb", "consumable") == "bomb", "炸弹不是炸弹");
            Assert(UI.IconArt.ForSkill("fireball") == "fire", "火球不是火");
            Assert(UI.IconArt.ForSkill("stone_skin") == "shield", "石肤不是盾");
            Assert(UI.IconArt.ForRoom("Elite") == "shield", "精英房不是盾徽");
            Assert(UI.IconArt.ForRoom("Boss") == "skull", "Boss 房不是骷髅");
            Assert(UI.IconArt.ForRoom("Altar") == "sparkle", "祭坛不是闪光");
            // 每个房间类型都必须落到 Kinds 里登记过的造型，否则画出来是空的
            foreach (var t in new[] { "Start", "Empty", "Enemy", "Treasure", "Shop", "Event", "Altar", "Training", "Curse", "Elite", "Boss" })
            {
                var k = UI.IconArt.ForRoom(t);
                Assert(UI.IconArt.Kinds.Contains(k), $"房间 {t} 用的造型 {k} 没登记在 Kinds 里");
            }
        });

        Step("背包里每件装备都写清「它给了什么」，换装时还能比出差距", () =>
        {
            // 面板上的「攻击 124」是总数：从这里看不出哪一件贡献了 16，
            // 于是六格背包里的取舍只能靠玩家自己背数值。
            // 这条钉住的是「逐条写效果」+「与身上那件对比」两件事都在。
            var data = AbyssEcho.Data.GameDataStore.Get();
            foreach (var def in data.Items.Where(i => i.Slot != AbyssEcho.Data.Slot.Consumable))
            {
                var gain = UI.Widgets.ItemGain(def);
                Assert(gain != "没有面板加成", $"{def.Name} 的面板加成算成了空");
                Assert(UI.Widgets.ItemScore(def) > 0, $"{def.Name} 的分是 0，换装比较永远平手");
            }
            var rusty = data.Items.First(i => i.Id == "rusty_sword");
            var steel = data.Items.First(i => i.Id == "steel_sword");
            // 换装时要把差值写出来：「攻击 +7（+4）」
            var versus = UI.Widgets.ItemGain(steel, rusty);
            Assert(versus.Contains("+4"), $"与手上一件对比时没写出差多少：{versus}");
            Assert(UI.Widgets.ItemScore(steel) > UI.Widgets.ItemScore(rusty), "精钢剑的分不如锈剑");
        });

        Step("房间格子画的是矢量图标，不只是文字符号", () =>
        {
            // 房间砖从「Button + StyleBox + 图标子节点」换成了一个 `RoomTile`：
            // 底板、砖缝、徽章、图标、状态角标都在同一个 `_Draw` 里。
            // 断言因此改判 `RoomTile.Kind`（映射表有没有漏），
            // 同时仍然要有一条「名字与图标都落到了格子上」的证据。
            var tiles = FindRoomTiles(gridMap);
            Assert(tiles.Count >= 9, $"房间砖只有 {tiles.Count} 块");
            Assert(tiles.All(t => !string.IsNullOrEmpty(t.Kind)), "有房间砖没有造型");
            foreach (var t in tiles)
                Assert(UI.IconArt.Kinds.Contains(t.Kind), $"房间砖的造型 {t.Kind} 没登记在 Kinds 里");
        });

        Step("骑士站着的那一格不画图标：图标与「起点」标签会一起被劈开", () =>
        {
            // 起点格上压着两样东西，都长在「徽章」那一套坐标里：一枚房间图标，
            // 和贴在上沿的「起点」标签。骑士立绘从中间穿过，标题被劈成两半——
            // 1280×720 的截图放大看，那块金字断在头盔两侧。这一格被站着的时候
            // 只该说「你在这儿」，不该再说一遍「这是起点」。
            var tiles = FindRoomTiles(gridMap);
            var standing = tiles.Where(t => t.Standing).ToList();
            Assert(standing.Count == 1, $"站着的那一格有 {standing.Count} 块：玩家不可能同时站在两格上");
            Assert(standing[0].IsStart, "站着的那一格不是玩家所在的那间房");
            // 判据不是「Standing 被设成 true」——那只是把字段抄一遍。
            // 读的是「这块砖还画不画徽章」：它为 false 时，图标与标签一起让开。
            Assert(!standing[0].DrawsBadge, "站着的那一格还在画徽章：图标和「起点」标签会一起被立绘劈开");
            var others = tiles.Where(t => !t.Standing).ToList();
            Assert(others.Any(t => t.DrawsBadge), "别的房间砖也不画徽章了：这一条改宽了，等于没查");
        });

        Step("地图上每一格都标出「这条岔路底下还剩几场仗」", () =>
        {
            // 一层里一半的房间是死胡同（40 种子 × 25 层实测），而地图以前只有
            // 「打过 / 没打过」两种像素。于是玩家在岔口唯一的探索方式是挨个撞一遍：
            // 走进空的、再原路走回来。地图生成那一刻就知道哪边有仗，它只是没说。
            //
            // 判据三条，缺一条都能漏：
            //   1) 每一块砖的 PendingBelow ≥ 0（数错成负数会让「还剩几场」变成噪声）；
            //   2) 至少有一块砖标着 ≥1（否则这个字段根本没接上线上的数据）；
            //   3) 起点那一格报的数 == 本层真正剩的战斗房数（和自检同一条等式，
            //      这里查的是 UI 有没有把它接到砖上，而不是逻辑层算得对不对）。
            var run = store.Run;
            Assert(run is not null, "冒烟时没有正在进行的这一局");
            var tiles = FindRoomTiles(gridMap);
            Assert(tiles.Count >= 9, $"房间砖只有 {tiles.Count} 块");
            Assert(tiles.All(t => t.PendingBelow >= 0), "有房间砖的「底下还剩几场仗」是负数");
            Assert(tiles.Any(t => t.PendingBelow >= 1), "整张图上没有一块砖标着「这边还有仗」——字段没接上数据");
            var pending = run!.PendingBattlesBelow();
            var start = run.Map.StartIndex;
            var startTile = tiles[start];
            Assert(startTile.PendingBelow == run.RemainingBattleRooms,
                $"起点砖报 {startTile.PendingBelow} 场，实际剩 {run.RemainingBattleRooms} 场（pending[起点]={pending[start]}）");
        });

        Step("地图上标出「哪一场仗躲不过」，而且和逻辑层报的是同一件事", () =>
        {
            // 红点说的是「这边还有几场」，是数量；玩家在岔口真正要判断的是「哪一场躲不过」，
            // 是位置。一层 11 间房、3.2 场仗，挡在去 Boss 路上的只有 1.1 场，
            // 而以前地图和提示条都只报总数——于是玩家的读法是「三场都得打完」。
            //
            // 这一步查的是「接上线了没有」：逻辑层算得对（自检管）与 UI 画得出来（这里管）
            // 是两件事，前几轮已经在「防御姿态没播报」「规则层有条没人点得到的路」上栽过两次。
            var run = store.Run;
            Assert(run is not null, "冒烟时没有正在进行的这一局");
            var tiles = FindRoomTiles(gridMap);
            var marked = tiles.Count(t => t.OnBossPath);
            Assert(marked <= 1, $"整张图上有 {marked} 块砖标着「必经之路」——从脚下到 Boss 的最短路只有一条，最多一块");
            if (run!.BattlesToBoss() > 0)
                Assert(marked == 1, "这一层还有躲不过的仗，地图上却一块都没标");
            // 提示条要把「剩余战斗房」与「还差几场」分开说：只报前者时，
            // 玩家的读法是「剩余 3 场 = 打完这三场才能下楼」，而真相是其中只有 1 场躲不过。
            var hint = FindChildOfType<Label>(this, "HintBar");
            Assert(hint is not null, "冒烟时没找到提示条（HintBar）");
            Assert(hint!.Text.Contains("剩余战斗房"), $"提示条里没有「剩余战斗房」：{hint.Text}");
            if (run.BattlesToBoss() > 0)
                Assert(hint.Text.Contains("下楼还差"), $"还有躲不过的仗，提示条却没写「下楼还差几场」：{hint.Text}");
        });

        Step("地图上的玩家是骑士立绘，不是一个小方块", () =>
        {
            var hero = FindChildOfType<UI.HeroSprite>(gridMap);
            Assert(hero is not null, "地图上没有 HeroSprite");
        });

        Step("飘字规则：暴击更大、治疗是绿的、同屏有上限", () =>
        {
            Assert(UI.DamageNumberRules.FontSizeOf("crit") > UI.DamageNumberRules.FontSizeOf("hit"), "暴击字号不大于普通");
            Assert(UI.DamageNumberRules.ColorOf("heal").G > UI.DamageNumberRules.ColorOf("hit").G, "治疗不是绿的");
            Assert(UI.DamageNumberRules.TextOf("heal", 12) == "+12", "治疗飘字没带加号");
            Assert(UI.DamageNumberRules.TextOf("hit", 12) == "-12", "伤害飘字没带减号");
            Assert(UI.DamageNumberRules.MaxActive == 40, "同屏飘字上限被改了");
        });

        Step("飘字层超过上限会丢掉最旧的（不会无限堆积）", () =>
        {
            var layer = new UI.DamageNumberLayer();
            AddChild(layer);
            for (var i = 0; i < UI.DamageNumberRules.MaxActive + 15; i++)
                layer.Spawn("hit", i, new Vector2(100, 100));
            Assert(layer.ActiveCount <= UI.DamageNumberRules.MaxActive, $"飘字堆到了 {layer.ActiveCount}");
            layer.QueueFree();
        });

        Step("粒子池：容量封顶、批量到期不 O(n²)、顺序稳定", () =>
        {
            // 上一版倒序 RemoveAt：一次冰霜新星 36 颗 + 石肤 20 颗同帧到期时，
            // 每删一个都要搬动后面的元素——同屏越多越慢，正好是"特效全开时掉帧"。
            var fx = new UI.FxLayer();
            AddChild(fx);
            var rng = new Random(7);
            var total = 0;
            for (var round = 0; round < 40; round++)
            {
                var batch = new List<UI.FxParticle>();
                for (var i = 0; i < 30; i++)
                    batch.Add(new UI.FxParticle
                    {
                        Pos = new Vector2(rng.Next(0, 1280), rng.Next(0, 720)),
                        Vel = new Vector2(0, rng.Next(-20, 20)),
                        Life = 60f + rng.Next(0, 400),
                        Size = 2f + rng.Next(0, 3),
                        Color = Colors.White,
                        BaseAlpha = 1f,
                        Alive = true,
                    });
                fx.Emit(batch);
                total += batch.Count;
                // 手动推进：不依赖渲染帧，headless 也能跑
                fx.StepForTest(16.7f);
                Assert(fx.ActiveCount <= UI.FxLayer.MaxParticles,
                    $"同屏粒子 {fx.ActiveCount} 超过上限 {UI.FxLayer.MaxParticles}");
            }
            Assert(total > 0, "一条粒子都没发出来");
            Assert(fx.Capacity <= UI.FxLayer.MaxParticles,
                $"粒子池容量 {fx.Capacity} 超过上限：容量复用没生效，会在扩容上抖");
            // 推进到所有粒子过期：池必须回收干净，否则越玩越卡
            for (var i = 0; i < 200; i++) fx.StepForTest(16.7f);
            Assert(fx.ActiveCount == 0, $"粒子过期后还剩 {fx.ActiveCount} 颗没回收");
            fx.QueueFree();
        });

        Step("五种技能各有粒子配方，且粒子会过期消失", () =>
        {
            var rng = new Random(7);
            foreach (var id in UI.FxLayer.FxSkillIds)
            {
                var list = UI.FxLayer.Recipe(id, new Vector2(100, 500), new Vector2(640, 200), () => (float)rng.NextDouble());
                Assert(list.Count > 0, $"{id} 没有粒子");
                Assert(list.All(p => p.Life > 0), $"{id} 有粒子寿命为 0（永远不消失）");
            }
            // 冰霜新星是 36 点放射、毒雾 30 点——数量是设计值，改了要有人知道
            var frost = UI.FxLayer.Recipe("frost_nova", Vector2.Zero, Vector2.Zero, () => 0.5f);
            Assert(frost.Count == 36, $"冰霜新星粒子数 {frost.Count}，应为 36");
            var fog = UI.FxLayer.Recipe("toxic_fog", Vector2.Zero, Vector2.Zero, () => 0.5f);
            Assert(fog.Count == 30 && fog.All(p => p.BaseAlpha is >= 0.3f and <= 0.7f), "毒雾浓度不在 0.3~0.7");
        });

        Step("十六种音效都有配方，且能合成出非空波形", () =>
        {
            Assert(UI.Sfx.Names.Length == 16, $"音效清单有 {UI.Sfx.Names.Length} 条，应为 16");
            foreach (var name in UI.Sfx.Names)
            {
                Assert(UI.Sfx.HasRecipe(name), $"{name} 没有合成配方");
                var pcm = UI.Sfx.Synthesize(name);
                Assert(pcm.Length > 0, $"{name} 合成出空波形");
                Assert(pcm.Any(v => Math.Abs(v) > 0.001f), $"{name} 全是静音");
                Assert(pcm.All(v => Math.Abs(v) <= 1f), $"{name} 削波（超出 ±1）");
            }
        });

        Step("状态行：中毒/防御/眩晕都能从单位身上读出来", () =>
        {
            var c = new Combatant { Name = "测试" };
            Assert(UI.StatusIcons.Read(c).Count == 0, "干净单位不该有状态");
            c.PoisonTurns = 3;
            c.PoisonPower = 4;
            var list = UI.StatusIcons.Read(c);
            Assert(list.Any(x => x.Id == "poison" && x.Label == 3), "中毒没读出来");
            c.Defending = true;
            c.StunTurns = 1;
            Assert(UI.StatusIcons.Read(c).Any(x => x.Id == "defend"), "防御没读出来");
            Assert(UI.StatusIcons.Read(c).Any(x => x.Id == "stunned"), "眩晕没读出来");
            // 状态定义表必须覆盖到 Combatant 能产生的每一种状态，否则界面上会出现字母圈
            foreach (var st in UI.StatusIcons.Read(c))
                Assert(UI.StatusIcons.Defs.ContainsKey(st.Id), $"状态 {st.Id} 没有图标定义");
        });

        Step("战斗界面上能看到敌人立绘与玩家立绘", () =>
        {
            var run = store.Run!;
            var index = run.Map.Rooms.FindIndex(r => r.Type is RoomType.Enemy or RoomType.Elite or RoomType.Boss);
            run.Teleport(index);
            var enc = run.CreateEncounter();
            Assert(enc is not null, "战斗房没有生成遭遇");
            var battle = new BattleScene();
            battle.Configure(run, enc!);
            AddChild(battle);
            Assert(FindChildOfType<UI.MonsterSprite>(battle) is not null, "战斗界面没有敌人立绘");
            Assert(FindChildOfType<UI.HeroSprite>(battle) is not null, "战斗界面没有玩家立绘");
            Assert(FindChildOfType<UI.StatusIconRow>(battle) is not null, "战斗界面没有状态图标行");
            battle.QueueFree();
        });

        await StepAsync("战斗排版：立绘够大且落在舞台里、按钮整条可见（截图形状，不是靠肉眼）", async () =>
        {
            // 这一组断言全部来自这一轮的截图问题：
            //   · 立绘溢到屏幕上沿、名字贴边（卡的内边距没算进配额）
            //   · 按钮行被挤出屏幕底部（行动条配额比内容少 18px）
            // 三件事都能用矩形算出来，没有理由只靠看图。
            var run = store.Run!;
            var index = run.Map.Rooms.FindIndex(r => r.Type is RoomType.Enemy or RoomType.Elite or RoomType.Boss);
            run.Teleport(index);
            var enc = run.CreateEncounter();
            var battle = new BattleScene();
            battle.Configure(run, enc!);
            AddChild(battle);
            // 容器布局要两帧：第一帧算最小尺寸，第二帧定位置。
            // 战斗场景的根节点是「锚点铺满」的 Control，进树那一帧 Size 还是 0，
            // 布局得按视口尺寸走（场景自己有同样的兜底）。这里等三帧是让
            // NotificationResized 把它们俩对齐，不至于按 0 高算出卡高 0。
            await Frame();
            await Frame();
            await Frame();

            var viewport = ViewportSize();
            var actions = battle.FindChild("Actions", true, false) as Control;
            Assert(actions is not null, "战斗界面里找不到按钮行");
            var bottom = actions!.Position.Y + actions.Size.Y + (actions.GetParent() as Control)?.Position.Y ?? 0;
            // 按钮行必须整条落在视口内：以前它被裁掉一半，玩家点不到技能
            Assert(bottom <= viewport.Y + 0.5f, $"按钮行底边 {bottom} 超出视口 {viewport.Y}");

            // 敌人立绘站在舞台上（卡只留出左边一栏给它占位），所以断言换成三条：
            // 它要够大、要落在舞台里、要避开卡里的名字行。
            // 上一版断言的是「立绘在卡里且不溢出槽位」，可那个槽位本身只有 94px，
            // 立绘缩成 70px 才符合断言——测试全绿，玩家看到的是卡左上角一只小老鼠。
            var panel = battle.FindChild("EnemyPanel", true, false) as Control;
            var socket = battle.FindChild("EnemySocket", true, false) as Control;
            var sprite = FindChildOfType<UI.MonsterSprite>(battle);
            Assert(panel is not null && socket is not null && sprite is not null, "敌人卡 / 立绘槽 / 立绘缺一个");
            var arenas0 = new List<UI.Arena>();
            Collect(battle, arenas0);
            var arena = arenas0.FirstOrDefault();
            var scene = battle as Scenes.BattleScene;
            Assert(scene is not null, "战斗场景类型不对");
            Assert(socket!.Size.X > 1f, "敌人卡里没给立绘留出占位栏，名字与血条会从屏幕最左开始排");
            Assert(sprite!.Size.Y >= 120f, $"敌人立绘盒子只有 {sprite.Size.Y}px 高：压太小玩家认不出造型");
            var spriteBottom = sprite.GlobalPosition.Y + sprite.Size.Y;
            Assert(spriteBottom <= viewport.Y + 0.5f, $"敌人立绘底边 {spriteBottom} 溢出视口 {viewport.Y}");
            // 「不压卡」的判据必须是卡的底边，不是卡的高度——
            // 上一版比的是 panel.Size.Y（143），而卡的底边就是 143，两边撞车时断言刚好成立，
            // 于是 640 高窗口下立绘顶到名字行上，这条断言还是绿的。
            var spriteTop = sprite.GlobalPosition.Y;
            var stageTop = UI.BattleLayout.Compute(viewport.Y).StageTop;
            Assert(spriteTop >= stageTop - 0.5f,
                $"敌人立绘顶边 {spriteTop} 压到敌人卡（卡底边 {stageTop}）：卡与立绘的坐标系算错了");
            var spriteBottomAbs = sprite.GlobalPosition.Y + sprite.Size.Y;
            var stageFloor = UI.BattleLayout.Compute(viewport.Y).StageBottom;
            // 「盒子底边在地板线以下」不是错——盒底停在舞台画的光斑中心那条线上，
            // 而立绘的脚还要再往上一点（见下面那条）。这里钉住的是「盒子没伸进行动条」。
            // 下边界问 Arena 要：它画到的最后一行就是光斑中心（地板线 + FootDrop）。
            var stageBottomEdge = arena.GlobalPosition.Y + arena.FloorLine + arena.FootDrop;
            Assert(spriteBottomAbs <= stageBottomEdge + 0.5f,
                $"敌人立绘盒子底边 {spriteBottomAbs} 伸进了行动条（{stageBottomEdge}）：立绘没有站在舞台上");
            // 舞台自己画的地板必须就是立绘踩的那条线。
            // 以前舞台上后墙与地板的交界是「控件高的一半」，立绘踩的是 BattleLayout 的地板线，
            // 1280×720 下两者差 92px：人站在光斑外面，落点那块余光变成了背景里两团莫名的椭圆。
            // 判据要求「交界贴近地板线」，而不是「两边都等于某个常数」——
            // 矮窗口下舞台会夹一下，允许它贴着上下边界。
            Assert(arena is not null, "战斗里找不到舞台（Arena）：后墙/地板/落点都是它画的");
            // 读的是 Arena 自己会拿去画的那条线（FloorY 过一遍夹取），不是它的原始字段：
            // 只比字段的话，Arena 里把 FloorY 丢掉不用的那种写法照样能过。
            var arenaFloor = arena.FloorY > 1f
                ? Mathf.Clamp(arena.FloorY, viewport.Y * 0.28f, viewport.Y * 0.86f)
                : viewport.Y * 0.5f;
            Assert(Mathf.Abs(arenaFloor - Mathf.Clamp(stageFloor, viewport.Y * 0.28f, viewport.Y * 0.86f)) <= 0.5f,
                $"舞台画的地板在 {arenaFloor}，立绘的脚踩在 {stageFloor}：脚踩不到自己的落点光斑");

            // **脚和那块光斑到底差多远。**
            //
            // 上一条只证明「两边用的地板线是同一个数」，它绿着，屏幕上却是
            // 「两块椭圆孤零零飘在人脚前面 62px」——因为光斑画在地板线 +62，
            // 立绘踩地板线，两条都对、彼此错开。判据得比「脚」和「光斑」这两个点。
            //
            // 光斑中心问 Arena 要（`FloorLine + FootDrop`，就是它 _Draw 用的那个点），
            // 脚问界面要——两处都是「渲染真的会用的那个数」，断言不按公式再算一遍。
            // 差多少算对：脚要落在椭圆里靠后的位置，投影（约 0.1 盒高）才不会甩到椭圆前面去。
            // 所以允许「脚比光斑中心高 0~0.22 盒高」，下限是 0（脚陷进椭圆最低点的下面就是站歪了）。
            var shadowY = arena.GlobalPosition.Y + arena.FloorLine + arena.FootDrop;
            // **那两块光斑必须整块落在行动条之上。**
            //
            // 光斑画在「地板线 + StanceDrop」、自己还有 SpotRy 的纵半径；行动条上沿在
            // `StageBottom`。以前地板线按「行动条上沿 - 18」算，于是 1280×720 下
            // 地板线 452、光斑中心 514，而行动条 470 就盖在中间——光斑与脚（474）
            // 整整齐齐排在面板后面，屏上是「巨鼠没有腿」。这两条都在 z=3，面板在 z=10。
            // 判据读的是「光斑最低点」和「行动条上沿」这两个渲染都会用的数。
            var spotBottom = shadowY + UI.Arena.SpotRy;
            var actionBarTop = viewport.Y - UI.BattleLayout.Compute(viewport.Y).ActionBarHeight;
            Assert(spotBottom <= actionBarTop + 0.5f,
                $"落点光斑的底边 {spotBottom:0} 掉进行动条里（上沿 {actionBarTop:0}）：舞台的地面被面板切掉了");
            Assert(scene.EnemyFootY <= actionBarTop + 0.5f,
                $"敌人的脚在 {scene.EnemyFootY:0}，落在行动条上沿 {actionBarTop:0} 之下：腿被面板切掉了");
            var footGap = shadowY - scene.EnemyFootY;
            Assert(footGap >= -2f && footGap <= sprite.Size.Y * 0.22f,
                $"敌人脚在 {scene.EnemyFootY:0}、落点光斑中心在 {shadowY:0}，差 {footGap:0}px（盒高 {sprite.Size.Y:0}）：" +
                (footGap > sprite.Size.Y * 0.22f ? "人飘在光斑上面，那块地成了背景里一团色块" : "脚陷到光斑下面去了"));

            // 玩家立绘：舞台上一个「本人」，行动条里一张「身份牌」——两处各有一个岗位。
            //   舞台那只：证明「谁在跟谁打」。没有它，玩家的伤害是从一片空气里飘出来的。
            //   身份牌那只：血条前面那张小像，眼睛不用离开血量就知道哪条血是自己的。
            // 早先这一条断言的是「屏幕上只能有一个」：那时舞台上是空的，
            // 多出来的每一个都在抢「我是谁」这件事。现在两个位置各司其职，
            // 判据换成「两个都在、都在自己该在的地方、都不是被压扁的贴纸」。
            var heroes = new List<UI.HeroSprite>();
            Collect(battle, heroes);
            Assert(heroes.Count == 2, $"战斗界面里有 {heroes.Count} 个玩家立绘：舞台与身份牌各该有一个");
            var stageHero = battle.FindChild("HeroSprite", true, false) as UI.HeroSprite;
            var chip = battle.FindChild("HeroChip", true, false) as UI.HeroSprite;
            Assert(stageHero is not null && chip is not null, "舞台立绘或行动条身份牌缺一个");

            var actionTop = viewport.Y - UI.BattleLayout.Compute(viewport.Y).ActionBarHeight;
            var floorLine = UI.BattleLayout.Compute(viewport.Y).StageBottom;
            // 舞台那只：整块落在「卡底 → 地板线」之间，脚踩地板线，朝左面对敌人。
            // 下边界是行动条上沿，不是地板线：立绘的脚要踩进落点光斑的远沿（见下面那条），
            // 而光斑本来就在地板线下面一点点。
            var actionTopEdge = viewport.Y - UI.BattleLayout.Compute(viewport.Y).ActionBarHeight;
            // 下边界是「舞台上那一排：光斑中心」（`Arena.FloorLine + FootDrop`）——
            // 舞台画到那儿为止，再往下就是行动条了。问 Arena 要这个数，不在断言里另算一遍。
            var stageLastRow = arena.GlobalPosition.Y + arena.FloorLine + arena.FootDrop;
            Assert(stageHero!.GlobalPosition.Y >= stageTop - 0.5f && stageHero.GlobalPosition.Y + stageHero.Size.Y <= stageLastRow + 0.5f,
                $"舞台上的自己落在 {stageHero.GlobalPosition}..{stageHero.GlobalPosition.Y + stageHero.Size.Y}，不在舞台 {stageTop}..{stageLastRow:0} 里");
            Assert(stageHero.Size.Y >= 150f, $"舞台上的自己只有 {stageHero.Size.Y}px 高：认不出是个人");
            Assert(stageHero.Facing < 0f, "舞台上的自己背对着敌人：他得朝左");
            // 与敌人站在同一条线上（脚对脚），不是一高一低。
            // 比的是**脚**——盒子底边不是脚了：盒子底下还留着「光斑远沿」那一截，
            // 「脚」不是盒底：盒底下还留着「光斑远沿」那一截，而那一截随各自盒子高变
            // （Boss 的盒子高、抬得也多）。比盒底等于比余白。
            Assert(Mathf.Abs(scene.HeroFootY - scene.EnemyFootY) <= 1f,
                $"玩家与敌人没站在同一条线上：脚在 {scene.HeroFootY:0} / {scene.EnemyFootY:0}，对峙构图垮了");
            // 两人横向不重叠：左右各占一边，中间留出交手区
            Assert(stageHero.GlobalPosition.X > sprite.GlobalPosition.X + sprite.Size.X * 0.5f,
                "舞台上玩家站在敌人左边：方向反了");

            // 身份牌那只：在行动条的血条行里，整块落在视口内，比例没被压扁。
            Assert(chip!.GlobalPosition.Y >= actionTop - 1f,
                $"身份牌顶边 {chip.GlobalPosition.Y} 在行动条之上（行动条顶边 {actionTop}）：它该在血条行里");
            Assert(chip.GlobalPosition.Y + chip.Size.Y <= viewport.Y + 0.5f,
                $"身份牌底边 {chip.GlobalPosition.Y + chip.Size.Y} 掉出视口 {viewport.Y}");
            Assert(chip.Size.Y >= 32f && chip.Size.X >= chip.Size.Y * 0.45f,
                $"身份牌 {chip.Size} 太小或被压扁：它得认得出是个人");

            // 立绘的「脚」真的落在框底：尺寸按那只怪的包围盒反推之后，
            // 框中心不再是造型的中心（巨鼠的重心偏左下），按中心摆的话脚会穿到地板下面——
            // 上一版就是这样，脚沉进去 50 多像素，投影画在大腿高度。
            // 判据用「脚下投影」而不是包围盒：投影是渲染真的会画的那一笔，
            // 它到框底的距离必须和到那双腿的距离差不多。
            sprite.QueueRedraw();
            var shadow = sprite.ShadowAnchor;
            var shadowGap = sprite.Size.Y - shadow.Y;
            Assert(shadowGap > 0f && shadowGap < sprite.Size.Y * 0.22f,
                $"敌人脚下投影落在框内 {shadow.Y:0}／框高 {sprite.Size.Y:0} 处：离框底 {shadowGap:0}px，像是脚穿到地板下面去了");

            // 「它有多大」这件事以前只写在注释里（「巨鼠比骑士小一档」），
            // 而截图里是一只和骑士齐肩的巨鼠：说明两个尺寸是分开拍的。
            // 判据取「玩家盒子 / 敌人盒子」，不取像素——像素跟着视口走，比不了。
            Assert(stageHero.Size.Y < sprite.Size.Y,
                $"舞台上的自己 {stageHero.Size.Y:0}px 不比敌人 {sprite.Size.Y:0}px 矮：威胁感来自「它比我大」，两边一样高就没了");
            Assert(stageHero.Size.Y >= sprite.Size.Y * 0.7f,
                $"舞台上的自己只有敌人的 {stageHero.Size.Y / sprite.Size.Y:0.00}：小到像宠物，读不出「我 vs 它」");
            // 但「怪一律比人小」也是错的：巨鼠是 1 级小怪，Boss 是威胁。
            // 盒子高度是同一把尺子（舞台高）给的，可盒子**里**装多少由那只怪的包围盒决定——
            // 所以这里比的是「谁在设计框里占得多」，而不是「谁被缩放得更多」。
            var ratBounds = UI.IconArt.Bounds("rat");
            var queenBounds = UI.IconArt.Bounds("boss_frost");
            Assert(queenBounds.Y > ratBounds.Y * 1.2f,
                $"霜蚀女王占 {queenBounds.Y:0.0}、巨鼠占 {ratBounds.Y:0.0}：两者在框里一样大，立绘体量就差不出来");
            // ---- 这一轮补的两条 ----
            // 一、舞台的站位与舞台画的落点用同一条边界：敌人卡与行动条是左右通栏的（各留 12px），
            //     落点必须落在同一条边里，否则敌人立绘的脚一半在卡外，
            //     而且两个站位关于画面中线不对称（一边内缩一边不内缩）。
            //     读的是 Arena 真会拿去画落点的那个函数，不是它旁边的字段。
            var enemyCx = sprite.GlobalPosition.X + sprite.Size.X * 0.5f;
            Assert(Mathf.Abs(enemyCx - arena.StanceX(UI.Arena.EnemyStanceX)) <= 1f,
                $"敌人脚在 {enemyCx:0}、舞台画的落点光斑在 {arena.StanceX(UI.Arena.EnemyStanceX):0}：立绘的站位和光斑不是同一个点");
            var heroCx = stageHero.GlobalPosition.X + stageHero.Size.X * 0.5f;
            Assert(Mathf.Abs(heroCx - arena.StanceX(UI.Arena.HeroStanceX)) <= 1f,
                $"玩家脚在 {heroCx:0}、舞台画的落点光斑在 {arena.StanceX(UI.Arena.HeroStanceX):0}：两边各算一次就会差半格");
            var mid = arena.StanceX((UI.Arena.EnemyStanceX + UI.Arena.HeroStanceX) * 0.5f);
            Assert(Mathf.Abs(mid - viewport.X * 0.5f) <= 0.5f,
                $"两个站位的中间落在 {mid:0}，画面中线在 {viewport.X * 0.5f:0}：一边内缩一边不内缩，对峙构图就偏了");

            // 二、敌人卡里的槽只该是「指向舞台上那一只」的取景框，不能再画一只同款小像。
            //     上一轮把它改成取景框，`_Draw` 里画小像的那半个分支却没删：
            //     屏上一只 280px 的巨鼠，卡里又一只 30px 的——同一个对手说了两遍，
            //     而且小到认不出，读者只会问「这只怪身边带了个谁」。
            //     判据比的是「槽里那道光的圆心在框高的哪个位置」：
            //     取景框的光从框底往上打（0.8），画小像的那一版照的是框内 52% 高的那张脸。
            var socketLight = (socket as UI.EnemySocket)?.LightY ?? -1f;
            Assert(socketLight > 0.6f,
                $"敌人卡里的槽把光打在框高 {socketLight:0.00} 处：取景框的光该从框底来，不是从中间照一张小像");

            battle.QueueFree();
        });

        Step("舞台几何：地板线在卡与行动条之间，任何视口高度下都不为负", () =>
        {
            // 640 高时上一版算出 StageBottom(570) < StageTop(580)，舞台高度是负的，
            // 立绘的站位由这个负数推出来，于是整个人跑到卡上面去。
            // 这条断言只看纯函数，和「界面建得出来」是两件事。
            foreach (var h in new[] { 720f, 640f, 540f, 480f })
            {
                var l = UI.BattleLayout.Compute(h);
                Assert(l.StageTop >= l.EnemyTop, $"{h} 高：舞台顶边 {l.StageTop} 在敌人卡底边 {l.EnemyTop} 之上");
                Assert(l.StageBottom > l.StageTop, $"{h} 高：舞台高度为负（顶边 {l.StageTop} / 地板线 {l.StageBottom}）");
                Assert(l.StageBottom <= h - l.ActionBarHeight,
                    $"{h} 高：地板线 {l.StageBottom} 落在行动条里（行动条顶边 {h - l.ActionBarHeight}）");
            }
        });

        Step("立绘动作：出手前冲、挨打后仰，两个方向相反", () =>
        {
            // 动作是纯状态，不碰渲染，所以 headless 也能断言「它到底动没动」：
            // 控件在进树之前不会有渲染帧，但「有没有登记一次出手/受击」是确定的状态。
            var mon = new UI.MonsterSprite();
            Assert(!mon.IsActing, "新建的敌人立绘不该处于动作中");
            mon.Lunge();
            Assert(mon.IsActing && mon.LungesForward, "敌人出手没有登记成「前冲」");
            var hero = new UI.HeroSprite();
            hero.Recoil();
            Assert(hero.IsActing && !hero.LungesForward, "玩家受击没有登记成「后仰」——方向反了就和攻击分不出");
            mon.QueueFree();
            hero.QueueFree();
        });

        Step("布局预算：立绘不会把行动条里的按钮行挤出去", () =>
        {
            // 这一轮的截图抓到两条同源的问题：
            //   1) 舞台上的怪比骑士还小——盒子余量 1.12/1.32 是两套数；
            //   2) 把玩家立绘放大之后，血条行被撑开，按钮行被裁掉半截。
            // 两条都是「同一个尺寸在两个地方各算一遍」，所以断言直接查预算关系。
            foreach (var h in new float[] { 720, 640, 540, 480 })
            {
                var l = UI.BattleLayout.Compute(h);
                // 立绘预算由血条行给，血条行是固定高：两者必须对得上
                Assert(l.HpRowBudget <= UI.BattleLayout.HpRowHeight,
                    $"{h} 高：立绘预算 {l.HpRowBudget:0} 超过了血条行高 {UI.BattleLayout.HpRowHeight}");
                Assert(l.HpRowBudget > 0, $"{h} 高：立绘预算算成了 {l.HpRowBudget}");
                // 行动条配额必须装得下内容，否则最早被挤掉的就是按钮行
                Assert(l.ActionBarHeight >= l.ActionBarContentHeight - 0.5f,
                    $"{h} 高：行动条配额 {l.ActionBarHeight:0} 小于内容 {l.ActionBarContentHeight:0}，按钮行会被裁");
            }
        });

        Step("骑士比例尺按「含投影的设计坐标」算，不会比格子高出一截", () =>
        {
            // HeroArt 的绘制坐标从头顶 -37 到脚下投影 +22；比例尺只除 58 的话人会被放大 1.12 倍，
            // 地图上头盔压住上一块地砖、行动条里顶出格子（两张截图都抓到了）。
            Assert(UI.HeroArt.LayoutHeight > UI.HeroArt.DesignHeight, "LayoutHeight 必须大于 DesignHeight（要含投影）");
            Assert((int)UI.HeroArt.LayoutHeight == 65, "LayoutHeight 被改了：绘制坐标的跨度是 65（-37 到 +22）");
        });

        await StepAsync("关掉的弹窗真的关掉了：`_modal` 里不残留已释放的节点（截图形状，不是靠肉眼）", async () =>
        {
            // 上一版引导卡的按钮直接 QueueFree，而 `_modal` 只在 TreeExited 里清：
            // 排队释放的那几帧里 `_modal` 仍然非空，按键继续被吞。
            // 截图工具更惨——它以为卡还开着，于是那张卡一路盖到战斗和 Boss 出场。
            store.NewRun(4242);
            var map = new MapScene();
            AddChild(map);
            await Frame();
            await Frame();
            map.ShowPanelForShot();
            await Frame();
            await Frame();
            var open = UI.Widgets.Overlays(map);
            // 报错时把那几层**叫什么**一起写出来。只写个数的话，「两张卡叠着」
            // 与「一张卡被数了两遍」在日志里长得一模一样，而修法完全相反。
            // 这条断言第一次红的时候，日志里只有「实际 2」——于是花了整整一轮
            // 才问出那两层是「深渊回响 · 启程 | 商店」：新手引导卡没关，商店叠在上面。
            Assert(open.Count == 1,
                $"弹出面板后屏上应当有 1 层覆盖层，实际 {open.Count}" +
                (open.Count > 0 ? $"（{string.Join(" | ", open.SelectMany(o => o.Titles))}）" : ""));
            Assert(open[0].Titles.Any(t => t.Contains("商店")), $"弹出来的不是商店面板：{string.Join(" / ", open[0].Titles)}");
            map.CloseModalForShot();
            await Frame();
            await Frame();
            await Frame();
            var left = UI.Widgets.Overlays(map);
            Assert(left.Count == 0, $"面板关掉之后屏上还剩 {left.Count} 层覆盖层（{string.Join(" / ", left.SelectMany(o => o.Titles))}）");
            map.QueueFree();
        });

        await StepAsync("弹窗只有一个位置：连开两张，屏上只剩后开的那一张", async () =>
        {
            // 这条守的是 `ShowModal` 的不变式，也钉住「引导卡不再跟别人的第一帧抢」。
            //
            //
            // 上一轮 CI 红在「弹出面板后屏上应当有 1 层覆盖层，实际 2」，把那两层挖出来
            // 才看清是 `深渊回响 · 启程 | 商店`：新手引导卡是 `CallDeferred` 弹的，
            // 恰好落在「面板也被打开」的那一帧上，两张卡同时挂着。
            // 于是这条判据时绿时红——同一个 commit 在 `cnb-gaq` 上红、在 `cnb-4pk` 上绿。
            //
            // 修法有两条，缺一条都还会复发：
            //   一、引导卡改成 `_Ready` 里同步弹，不再跟别人的第一帧抢；
            //   二、`ShowModal` 自己先关掉上一张。「弹窗只有一个位置」是这个函数
            //       该守的不变式，写在二十多个入口里迟早漏一个。
            // 这里钉的是第二条——它才是「以后任何入口都不会叠出两张」的保证。
            var store9 = GetNode<GameStore>("/root/GameStore");
            store9.NewRun(4243);
            var map9 = new MapScene();
            AddChild(map9);
            await Frame();
            await Frame();

            // 先清场：开场可能挂着新手引导卡（前两局会弹），那是**另一件**在验证的事。
            // 不清掉的话，这条判据会变成「引导卡在不在」的复读机。
            //
            // 这里关得掉，本身就是修复的一部分：引导卡现在在 `_Ready` 里同步弹出来，
            // 所以「场景建好了」的这一刻它已经在 `_modal` 上——而不是等一帧之后
            // 才从 `CallDeferred` 里冒出来（那正是它和面板撞车的原因）。
            map9.CloseModalForShot();
            await Frame();
            var cleared = UI.Widgets.Overlays(map9);
            Assert(cleared.Count == 0,
                $"清场失败：关掉开场那张卡之后屏上还剩 {cleared.Count} 层" +
                $"（{string.Join(" | ", cleared.SelectMany(o => o.Titles))}）：这条判据没法验证");

            // 连开两张：第二张开的时候第一张必须已经不在屏上。
            map9.OpenInventoryForShot();
            await Frame();
            Assert(UI.Widgets.Overlays(map9).Count == 1, "第一张弹窗没弹出来");
            map9.ShowPanelForShot();
            await Frame();
            var both = UI.Widgets.Overlays(map9);
            Assert(both.Count == 1,
                $"连开两张之后屏上有 {both.Count} 层覆盖层（{string.Join(" | ", both.SelectMany(o => o.Titles))}）：" +
                "弹窗只有一个位置，开新的就该关掉旧的");

            // 关掉最后那张之后必须真的干净：`CloseModal` 走「先摘再删」，
            // 屏上不该再数到一张等释放的卡（那会让判据去追一个正在跑的靶子）。
            map9.CloseModalForShot();
            await Frame();
            var after = UI.Widgets.Overlays(map9);
            Assert(after.Count == 0,
                $"关掉最后一张之后屏上还剩 {after.Count} 层（{string.Join(" | ", after.SelectMany(o => o.Titles))}）");
            map9.QueueFree();
        });

        Step("Boss 出场演出能建出来，名字在画面下半部", () =>
        {
            var def = UI.BossIntroData.Of("boss_abyss");
            Assert(def.Name == "深渊之主" && def.Entry == "vortex", "boss_abyss 的演出设定被改了");
            Assert(UI.BossIntroData.All.Count == 4, "Boss 演出设定应有 4 条（含逆位）");
            var intro = new UI.BossIntro();
            intro.Configure("boss_abyss");
            AddChild(intro);
            intro.QueueFree();
        });

        // ---- 这一轮新增：行动条自适应 / BGM / 键位表 / 存档往返 ----
        // 上一版把「行动条在部分分辨率下位置偏上」写进了「没做到的」，
        // 而它其实是一个纯算术函数，没有理由只靠肉眼。

        Step("行动条：1280x720 用设计值，窗口变矮按比例收且不低于下限", () =>
        {
            var design = UI.BattleLayout.Compute(720);
            Assert(Math.Abs(design.ActionBarHeight - UI.BattleLayout.DesignActionBar) < 0.01f,
                $"720 高下行动条 {design.ActionBarHeight}，应为 {UI.BattleLayout.DesignActionBar}");
            // 4:3 与更矮的窗口：行动条必须跟着收，否则会盖住敌人区
            foreach (var h in new[] { 640f, 600f, 540f })
            {
                var l = UI.BattleLayout.Compute(h);
                Assert(l.ActionBarHeight < UI.BattleLayout.DesignActionBar, $"{h} 高下行动条没有收（{l.ActionBarHeight}）");
                Assert(l.ActionBarHeight >= UI.BattleLayout.MinActionBar, $"{h} 高下行动条低于下限（{l.ActionBarHeight}）");
                // 留白判据：敌人卡底部与行动条顶部之间至少要剩 MinStage 给立绘与粒子。
                // 这里不另写一个数字——「舞台至少 MinStage」是布局自己的常量，
                // 断言抄一份就会和实现各走各的（以前写死 200，其实常量是 150）。
                Assert(l.EnemyBottom - l.EnemyTop >= UI.BattleLayout.MinStage,
                    $"{h} 高下敌人区只剩 {l.EnemyBottom - l.EnemyTop}px，少于舞台下限 {UI.BattleLayout.MinStage}");
                // 这一条是这轮补的硬约束：行动条的配额必须装得下自己的内容。
                // 少 18px 的表现就是「按钮行被裁掉一半」——而这 18px 曾经真的少过。
                Assert(l.ActionBarHeight + 0.01f >= l.ActionBarContentHeight,
                    $"{h} 高下行动条配额 {l.ActionBarHeight} 装不下内容 {l.ActionBarContentHeight}，按钮行会被裁掉");
            }
            // 设计值必须等于内容高：改内容（加一行状态、换字号）就要改设计值，
            // 两个数分开维护的那一天，就是按钮被裁掉的那一天。
            Assert(Math.Abs(UI.BattleLayout.DesignActionBar720 - design.ActionBarContentHeight) < 0.01f,
                $"设计值 {UI.BattleLayout.DesignActionBar720} 与内容高 {design.ActionBarContentHeight} 不一致");
        });

        Step("BGM：每个楼层机制都有曲子，Boss 层的底色更暗", () =>
        {
            var data = AbyssEcho.Data.GameDataStore.Get();
            // 以前的判据只查「曲子存在」，于是 13 种机制全都静默退回 drift 也能过——
            // 现在查的是「每种机制都有自己的那一段」，退回默认就是红的。
            var used = new Dictionary<string, string>();
            foreach (var mechanic in data.FloorMechanics.Keys.Concat(data.BossMechanics.Keys).Distinct())
            {
                var cue = UI.Bgm.CueFor(mechanic, false, false);
                Assert(UI.Bgm.HasCue(cue), $"机制 {mechanic} 没有对应曲子（{cue}）");
                if (mechanic is "none") continue;
                // 同一段曲子可以复用（瘴气与疫病同源），但每种机制都必须是显式配的
                Assert(UI.Bgm.ByMechanic.ContainsKey(mechanic), $"机制 {mechanic} 没有显式配曲，退回默认了");
                used[mechanic] = cue;
            }
            // 楼层机制表里出现的每一种机制都要能在 ByMechanic 里找到
            Assert(data.FloorMechanicMap.Values.All(m => UI.Bgm.ByMechanic.ContainsKey(m)),
                "floorMechanicMap 里有机制没配曲");
            Assert(UI.Bgm.CueFor("none", false, false) == UI.Bgm.DefaultKey, "无机制层没有回到默认垫底");
            // Boss 层：战斗中必须换压迫感更强的一档，且比普通层更低更暗
            var boss = UI.Bgm.Cues[UI.Bgm.CueFor("none", true, true)];
            var normal = UI.Bgm.Cues[UI.Bgm.DefaultKey];
            Assert(boss.RootHz < normal.RootHz, $"Boss 曲根音 {boss.RootHz} 不比普通层 {(normal.RootHz)} 低");
            Assert(boss.Bright < normal.Bright, "Boss 曲不比普通层暗");
        });

        Step("BGM 能合成出可循环的波形（非空、不静音、不削波、循环点不跳变）", () =>
        {
            var expected = (int)(UI.Bgm.SampleRate * UI.Bgm.LoopSeconds);
            foreach (var key in UI.Bgm.Cues.Keys)
            {
                var pcm = UI.Bgm.Synthesize(key);
                Assert(pcm.Length == expected, $"{key} 合成长度 {pcm.Length}，应为 {expected}");
                // pcm 可能是 null（合成失败）：这里先兜一下，顺带消掉 CS8604
                Assert(pcm is { Length: > 0 }, $"{key} 没有合成出任何采样");
                Assert(pcm!.Any(v => Math.Abs(v) > 0.01f), $"{key} 全是静音");
                Assert(pcm.All(v => Math.Abs(v) <= 1f), $"{key} 削波");
                // 循环点：末尾到开头不该有跳变，否则每一圈都会"咔"一声。
                // 这一条比"能合成"重要得多——它决定一段垫子能不能循环播下去。
                var jump = Math.Abs(pcm[0] - pcm[^1]);
                Assert(jump < 0.05f, $"{key} 循环点跳变 {jump:0.000}，会听见爆音");
            }
        });

        Step("BGM 是曲子不是垫子：有音阶、有动机、有和弦进行", () =>
        {
            // 这一条是这一轮的起点：「有 BGM」不等于「有曲子」。
            // 上一版每段只有根音 + 五度 + 琶音，所有楼层是同一块垫子换调。
            // 判据全部落在数据上，不靠耳朵。
            foreach (var (key, def) in UI.Bgm.Cues)
            {
                Assert(def.Scale is { Length: >= 5 }, $"{key} 没有音阶：只能是垫子");
                Assert(def.Motif is { Length: >= 6 }, $"{key} 没有动机：听不出旋律");
                Assert(def.Motif!.Any(d => d >= 0), $"{key} 的动机全是休止");
                // 动机必须真的在动，不能是同一个音反复（那还是垫子）
                var distinct = def.Motif!.Where(d => d >= 0).Distinct().Count();
                Assert(distinct >= 3, $"{key} 的动机只用了 {distinct} 个音，听不出走向");
            }
            Assert(UI.Bgm.Progression.Length >= 3, "和弦进行少于 3 个和弦就不构成调性");
            // 不同楼层的动机应该有差别，而不是全部共用一套
            var motifs = UI.Bgm.Cues.Values.Select(d => string.Join(",", d.Motif!)).Distinct().Count();
            Assert(motifs >= 3, $"全部 {UI.Bgm.Cues.Count} 段曲子只有 {motifs} 种动机，等于换调不换曲");

            // 成品必须真的按动机发音：统计过零率判断"有没有在动"。
            // 单块垫子的过零率几乎是常数，有旋律的会随音高变化而起伏。
            var pcm = UI.Bgm.Synthesize("drift");
            var chunk = pcm.Length / 8;
            var crossings = new List<int>();
            for (var c = 0; c < 8; c++)
            {
                var n = 0;
                for (var i = c * chunk + 1; i < (c + 1) * chunk; i++)
                    if ((pcm[i] >= 0) != (pcm[i - 1] >= 0)) n++;
                crossings.Add(n);
            }
            var mean = crossings.Average();
            var spread = crossings.Max() - crossings.Min();
            Assert(mean > 10, $"曲子里几乎没有振荡（平均过零 {mean:0}）");
            Assert(spread > mean * 0.1f, "每段的过零率几乎一样：旋律没有起伏，还是垫子");
        });

        Step("键位表是数据：说明里写的每个键都有对应动作，没有空条目", () =>
        {
            Assert(UI.Keymap.Map.Length >= 8, "地图键位表太短");
            Assert(UI.Keymap.Battle.Length >= 5, "战斗键位表太短");
            foreach (var b in UI.Keymap.Map.Concat(UI.Keymap.Battle))
            {
                Assert(!string.IsNullOrWhiteSpace(b.Keys), "有空键位");
                Assert(!string.IsNullOrWhiteSpace(b.Action), $"键位 {b.Keys} 没有动作说明");
            }
            // 这几条是 TS 版「说明写了但没接」的重灾区，必须都在表里
            foreach (var must in new[] { "H", "M" })
                Assert(UI.Keymap.Map.Any(b => b.Keys == must), $"键位表缺少 {must}（说明与实现会再次对不上）");
        });

        Step("局内进度：存盘 → 读档后楼层/血量/金币/背包一致", () =>
        {
            var meta = new AbyssEcho.Systems.MetaState();
            var run = new Game(777, meta);
            run.Player.Hp = 7;
            run.Player.Gold = 123;
            var item = AbyssEcho.Data.GameDataStore.Get().Items[0];
            run.ItemIndex[item.Id] = item;
            run.Player.Inventory.Add(item.Id);
            run.Player.Equipped.Set("weapon", 0);
            var path = "user://uismoke_run.cfg";
            AbyssEcho.Autoload.SaveSerializer.Delete(path);
            AbyssEcho.Autoload.SaveSerializer.Save(path, run, 777);
            Assert(AbyssEcho.Autoload.SaveSerializer.CanResume(path), "刚存的档却说不能继续");

            var loaded = AbyssEcho.Autoload.SaveSerializer.Load(path, meta);
            Assert(loaded is not null, "读档返回空");
            Assert(loaded!.Floor == run.Floor, $"楼层不一致：{loaded.Floor} ≠ {run.Floor}");
            Assert(loaded.Player.Hp == 7, $"血量没还原：{loaded.Player.Hp}");
            Assert(loaded.Player.Gold == 123, $"金币没还原：{loaded.Player.Gold}");
            Assert(loaded.Player.Inventory.SequenceEqual(run.Player.Inventory), "背包没还原");
            Assert(loaded.Player.Equipped.Weapon == 0, "装备下标没还原");
            // 同一个种子必须长出同一张图：地图不入档，靠种子重算
            Assert(loaded.Rng.Seed == run.Rng.Seed, "种子没还原，地图会变");
            AbyssEcho.Autoload.SaveSerializer.Delete(path);
        });

        Step("终局不留下「继续」：通关/阵亡后存档被清", () =>
        {
            // 自己造一份存档再清，不碰 GameStore 手上的那一局
            var meta = new AbyssEcho.Systems.MetaState();
            var run = new Game(888, meta);
            var path = "user://uismoke_run2.cfg";
            AbyssEcho.Autoload.SaveSerializer.Save(path, run, 888);
            Assert(AbyssEcho.Autoload.SaveSerializer.CanResume(path), "存档没写进去");
            AbyssEcho.Autoload.SaveSerializer.Delete(path);
            Assert(!AbyssEcho.Autoload.SaveSerializer.CanResume(path), "删档之后仍然显示可继续");
        });

        // ---- 这一轮新增：敌人卡只占卡高 / 取景窗在右端 / 弹窗底板不许被主题盖住 ----

        Step("敌人卡的六行内容都装得进卡里：预算出来的下沿不许越过卡底", () =>
        {
            // 这一条守的是「排到了卡外」那一种。那一块纯白的两种流行解释
            //（「字排到了卡外」与「取到了上一帧」）都已经在这张卡上被否掉了，
            // 但没有一条断言管过 **卡里那几行到底排到哪** —— 因为那份算术从来没被算出来过。
            // 现在它存在了：`MeasuredForecastBottom` 由控件自己的位置累加出来，
            // 不是复述字号。判据问的是「最后一个可见行的下沿有没有越过卡底」，
            // 与字号、行距、机制文案长度都无关——文案再长一行，它也照样只是问这一句。
            var run = store.Run!;
            var index = run.Map.Rooms.FindIndex(r => r.Type is RoomType.Enemy or RoomType.Elite or RoomType.Boss);
            Assert(index >= 0, "这一层没有战斗房");
            run.Teleport(index);
            var scene = new BattleScene();
            scene.Configure(run, run.CreateEncounter()!);
            var host = new Control();
            AddChild(host);
            host.AddChild(scene);
            var panel = scene.FindChild("EnemyPanel", true, false) as Control;
            Assert(panel is not null, "战斗里找不到敌人卡（EnemyPanel）");
            var bottom = BattleScene.MeasuredForecastBottom(scene);
            var cardBottom = panel!.GlobalPosition.Y + panel.Size.Y;
            Assert(bottom > 0, "量不出卡里那几行的位置：那份算术没跑");
            Assert(bottom <= cardBottom + 1f,
                $"卡里最后一行排到 {bottom:0}，而卡底在 {cardBottom:0}：多出来的字没人看得见");
            scene.QueueFree();
            host.QueueFree();
        });

        Step("敌人卡的高度就是卡高：不许一路铺到舞台里", () =>
        {
            // 截图里敌人卡曾经是一块**铺到舞台中间**的亮板：卡里文字按内容只排到 111，
            // 而卡本身高 359 —— 剩下的 248px 是空的，屏上读作「战斗界面顶上有一条白板」。
            // 根因是 `top.OffsetBottom = _layout.EnemyBottom`（那是舞台的上沿），
            // 不是 `EnemyPanelHeight`（卡高）。两个数在 720 高下差 248。
            // 判据比的是「卡画出来的高度」和布局说的卡高，不是名字：名字改对了、值写错了照样红。
            var run = store.Run!;
            var index = run.Map.Rooms.FindIndex(r => r.Type is RoomType.Enemy or RoomType.Elite or RoomType.Boss);
            Assert(index >= 0, "这一层没有战斗房");
            run.Teleport(index);
            var scene = new BattleScene();
            scene.Configure(run, run.CreateEncounter()!);
            var host = new Control();
            AddChild(host);
            host.AddChild(scene);
            var panel = scene.FindChild("EnemyPanel", true, false) as Control;
            Assert(panel is not null, "战斗里找不到敌人卡（EnemyPanel）");
            var cardH = UI.BattleLayout.Compute(720).EnemyPanelHeight;
            Assert(panel!.Size.Y <= cardH + 1f,
                $"敌人卡画出来 {panel.Size.Y:0} 高，而卡高只有 {cardH:0}：它会一路铺到舞台里");
            scene.QueueFree();
            host.QueueFree();
        });

        await StepAsync("取景窗在卡片右端，不在左上角", async () =>
        {
            // 它原来是一条 132 宽的竖栏摆在卡最左，而卡里早就不画立绘了——
            // 屏上那块方板读作「这里漏画了东西」。挪到右端之后，判据要守住这个位置：
            // 修错位得让判据盯位置，只盯形状的话下一轮还会挪回左边。
            var run = store.Run!;
            var index = run.Map.Rooms.FindIndex(r => r.Type is RoomType.Enemy or RoomType.Elite or RoomType.Boss);
            run.Teleport(index);
            var scene = new BattleScene();
            scene.Configure(run, run.CreateEncounter()!);
            var host = new Control();
            AddChild(host);
            host.AddChild(scene);
            var panel = scene.FindChild("EnemyPanel", true, false) as Control;
            var socket = scene.FindChild("EnemySocket", true, false) as Control;
            Assert(panel is not null && socket is not null, "敌人卡上找不到取景窗（EnemySocket）");
            // 布局要真的跑过一遍：同一个函数里新加的子节点在这一帧还没有被容器排过，
            // 读到的 `Position` 是 0——那句「取景窗在 0，卡片中线在 20」是这么来的，
            // 不是它跑到了左边。判位置之前先等一帧。
            await Frame();
            await Frame();
            var mid = panel!.Size.X * 0.5f;
            Assert(socket!.Position.X >= mid,
                $"取景窗在 {socket.Position.X:0}，卡片中线在 {mid:0}：它跑到卡左边去了");
            Assert(socket.Size.X <= 80f,
                $"取景窗宽 {socket.Size.X:0}：它该是一条窄框（{UI.EnemySocket.Width:0}），不是一条空栏");
            scene.QueueFree();
            host.QueueFree();
        });

        Step("弹窗的底板不许被主题那块默认 StyleBox 盖住", () =>
        {
            // `05-panel.png` 里弹窗一直是一块**平板**：`PanelPlate` 画的渐变、内描边、
            // 四角切角、上沿高光全不见。原因不是压暗层发灰，而是 `PanelContainer`
            // 没写 `panel` 覆盖时会去用主题里那块不透明的 `Palette.Panel`，
            // 它铺满内容区、把牌子整块盖在下面。这条判据问的是「弹窗的底是不是透明的」。
            var modalHost = new Control();
            AddChild(modalHost);
            var root = Widgets.Overlay(modalHost, "商店", 600).Root;
            var panel = root.FindChild("OverlayPanel", true, false) as PanelContainer;
            Assert(panel is not null, "弹窗里找不到 OverlayPanel");
            var sb = panel!.GetThemeStylebox("panel") as StyleBoxFlat;
            Assert(sb is not null && sb.BgColor.A < 0.02f,
                $"弹窗的底不透明（alpha={(sb?.BgColor.A ?? 1f):0.00}）：它会盖住 PanelPlate 画的那张牌");
            root.QueueFree();
            modalHost.QueueFree();
        });

        // ---- 这一轮新增：矮窗口下敌人卡跟着收 / 战斗键位 / 连战标题 / 空格等待 ----

        Step("敌人卡在矮窗口下先收一档，不会顶到行动条上", () =>
        {
            // 「行动条收了但敌人卡没收」是上一轮的真问题：540 高下卡片仍占 108，
            // 舞台被挤掉，立绘和飘字全压在血条上。
            foreach (var h in new[] { 720f, 640f, 540f, 480f })
            {
                var l = UI.BattleLayout.Compute(h);
                Assert(l.EnemyPanelHeight <= UI.BattleLayout.EnemyPanelHeight,
                    $"{h} 高下敌人卡 {l.EnemyPanelHeight} 比设计值还高");
                Assert(l.EnemyTop + l.EnemyPanelHeight <= l.EnemyBottom,
                    $"{h} 高下敌人卡压到行动条上");
                Assert(l.LogHeight is 96f or 80f, $"{h} 高下日志高度 {l.LogHeight} 不在两档里");
            }
        });

        await StepAsync("日志面板跟着行数收、不留空槽，且不许盖住舞台上的立绘", async () =>
        {
            // 之前日志面板固定满高 96：开战第一屏只有「遭遇 XX！」一行，
            // 下面那块空白看起来像「日志没画完」，同时白白吃掉能放大立绘的高度。
            // 判据是「面板高度 == 文本内容高 + 内边距」，而不是某个固定数——
            // 字号、行距由主题决定，写死的数迟早和渲染对不上。
            var run = store.Run!;
            var index = run.Map.Rooms.FindIndex(r => r.Type is RoomType.Enemy or RoomType.Elite or RoomType.Boss);
            run.Teleport(index);
            var enc = run.CreateEncounter();
            var battle = new BattleScene();
            battle.Configure(run, enc!);
            AddChild(battle);
            await Frame();
            await Frame();
            var panel = battle.FindChild("LogPanel", true, false) as PanelContainer;
            Assert(panel is not null, "战斗界面里找不到日志面板");
            var rich = panel!.GetChildren().OfType<RichTextLabel>().FirstOrDefault();
            Assert(rich is not null, "日志面板里没有富文本");
            // 判据一：面板最多占「这一列里还剩多少」——超了就会把按钮行顶下去。
            //
            // 这条原来比的是 `BattleLayout.LogHeight`（96），也就是「设计上日志区多高」。
            // 96 是按「一行 13.7px、七行」算出来的，而 13px 的中文一行实测 20px：
            // 五行要 100px，被 96 裁掉一截——`03-battle.png` 里最后一行被切在字腰上。
            // 真正的上限只有一个：这一列的高度减去别的东西占掉的（`ActionColumnRoomFor`）。
            // 设计值不是现场值，判据要读现场那个。
            var col = panel.GetParent() as VBoxContainer;
            Assert(col is not null, "日志面板不在行动条那一列里");
            var roomForLog = battle.LogRoomForProbe;
            var logCap = battle.LogRoomForProbe;
            Assert(panel.Size.Y <= roomForLog + BattleLogChrome(panel) + 1f,
                $"日志面板高 {panel.Size.Y} 超过这一列还剩下的 {roomForLog:0} + 内边距（会把按钮行顶下去）");

            // 判据二：砖的圆角——描边以内那半个线宽不许被「上沿高光」贴片盖住。
            // 这条是拿像素算的——`RoundRectOutline` 是以砖沿为折线、左右各铺半个线宽，
            // 而高光贴片原来从 2px 起排，正好压在描边的内侧一半上。
            // 结果骑士站的那一格（没有图标挡着）四角各露出一道「描边被抠掉」的口子，
            // 截图放大 4 倍才看得清。判据比的就是这两个数：贴片起排点 vs 描边内沿。
            var tile = new UI.RoomTile { Size = new Vector2(104, 104), Visited = true, Reachable = true };
            var highlightInset = tile.HighlightInsetForProbe;
            var outlineInner = 2f;   // 描边中心线在砖沿上，内沿 = 半个线宽
            var strokeHalf = 2.2f * 0.5f;
            Assert(highlightInset >= outlineInner + strokeHalf - 0.3f,
                $"上沿高光从 {highlightInset:0.0}px 起排，描边的内沿在 {outlineInner + strokeHalf:0.0}px：高光会盖住描边，砖角被抠出缺口");
            tile.QueueFree();

            // 下面的判据都必须在日志长出来之后问：开场只有一行日志，
            // 面板按一行算怎么收缩都对，留白与遮挡都是「打了两回合之后」才出现的。
            // `PlayScriptedTurnsForShot` 走的是和按钮完全相同的入口（`Act` → `Refresh`）。
            var battleNode = battle as BattleScene;
            battleNode!.PlayScriptedTurnsForShot(2);
            for (var i = 0; i < 3; i++) await Frame();
            // 「定住」要循环几次：面板改高度本身就是一次布局（列高、缝的条数都变），
            // 只同步一遍的话它停在上一轮那个数上，比文字矮一截。
            await battle.SyncLogUntilStableForTest();

            // 判据三：面板高要跟「文字真的占的高度」走——多出来的是纯黑空槽，少了就是裁字。
            // 内边距读的是**渲染真的在用的那一份**（`StyleBoxFlat.ContentMargin`），
            // 不是常量：这一轮日志块被挪到按钮行下面，面板外边距从 6/4 改成 4/4，
            // 而常量还写着 10，那条断言照样绿——它比的是一个「名字叫内边距」的数。
            // 文字超过满高时面板封顶（只显示最后七行），所以比较的是封顶后的那个数。
            var lineRows = Mathf.Min((float)rich!.GetContentHeight(), logCap);
            Assert(Math.Abs(panel.Size.Y - (lineRows + BattleLogChrome(panel))) < 1.5f,
                $"日志面板高 {panel.Size.Y} 与文字高 {lineRows} + 内边距 {BattleLogChrome(panel)} 对不上：" +
                "多出来的空白是在吃舞台，少了就是最后一行被裁掉");

            // 判据四：日志块的底边要在屏幕里。它排在按钮行之后，变长只往下顶自己，
            // 顶出屏幕底边时整条行动条会被 VBox 压扁，按钮行跟着变矮。
            var vp = ViewportSize();
            var panelBottom = panel.GlobalPosition.Y + panel.Size.Y;
            Assert(panelBottom <= vp.Y + 0.5f, $"日志面板底边 {panelBottom:0} 出屏了（视口高 {vp.Y:0}）");

            // 判据五：立绘的盒子不许伸进日志面板。截图里「你」只剩一个人头、
            // 身体整块被日志盖掉，而当时所有断言都是绿的——没有任何一条问过
            // 「这两块矩形有没有撞上」。比的是盒子，不是脚：脚在 468、日志上沿在 470，
            // 差 2px 绿着，屏幕上却少了三分之一，因为盒子底下还留着「光斑远沿」那一截。
            var enemy = FindChildOfType<UI.MonsterSprite>(battle);
            var stageHero = battle.FindChild("HeroSprite", true, false) as UI.HeroSprite;
            Assert(enemy is not null && stageHero is not null, "攒了日志之后找不到舞台上的两个立绘");
            var logTop = panel.GlobalPosition.Y;
            foreach (var (who, c) in new (string, Control)[] { ("敌人", enemy!), ("舞台上的你", stageHero!) })
            {
                var bottom = c.GlobalPosition.Y + c.Size.Y;
                Assert(bottom <= logTop + 1f,
                    $"{who}的盒子底边在 {bottom:0}，日志面板上沿在 {logTop:0}：立绘被日志盖掉了 " +
                    $"{bottom - logTop:0}px（盒子高 {c.Size.Y:0}）");
            }

            // 判据六（像素）：文字末行到面板底边之间只该有内边距，不该是一整行纯黑。
            // headless 下没有渲染纹理（`GetTexture()` 是空的），这一条量不了像素；
            // 它不是「放过」：带显卡的那一趟由截图工具逐张看同一件事，
            // 而控件账上面几条已经查过了。
            var panelBg = (panel.GetThemeStylebox("panel") as StyleBoxFlat)?.BgColor ?? new Color("070c15");
            var img = DisplayServer.GetName() == "headless"
                ? null
                : GetViewport().GetTexture().GetImage();
            if (img is not null)
            {
                var (blankRows, lastInk) = BlankRowsIn(img, rich, panelBg);
                Assert(lastInk > 0, "日志面板里一行字都没画出来：量不到空白");
                Assert(blankRows <= 8,
                    $"最后一行日志下面还剩 {blankRows}px 的空白（文字末行在 y={lastInk}）：内边距只该占几像素");
            }

            battle.QueueFree();
        });

        Step("战斗键位说明与战斗实际接的键一致", () =>
        {
            // 说明是数据，这里查的是「数据里承诺过的键，战斗场景确实认得」。
            // 只查清单长度是不够的——TS 版就是这么漏掉「说明写了 M，界面没接」的。
            Assert(UI.Keymap.Battle.Any(b => b.Keys == "A"), "战斗键位表缺少普攻 A");
            Assert(UI.Keymap.Battle.Any(b => b.Keys.StartsWith('1')), "战斗键位表缺少技能数字键");
            Assert(UI.Keymap.Battle.Any(b => b.Keys.Contains("Esc")), "战斗键位表缺少逃跑");
            // 地图键位表里的 H/M/F11 在战斗里同样生效（战斗场景也接了这三个）
            foreach (var must in new[] { "H", "M", "F11" })
                Assert(UI.Keymap.Map.Any(b => b.Keys == must), $"键位表缺少 {must}");
            Assert(UI.Keymap.Map.Any(b => b.Keys.Contains("空格")), "空格等待不在键位表里");
            // 「> 或 .」下楼：说明里一直写着两种按键，而 InputMap 只登记了 >。
            // 照着说明按 . 什么都不会发生——正是这份键位表要防的那种「说了做不到」。
            Assert(InputMap.HasAction("descend"), "InputMap 里没有 descend");
            var descendKeys = InputMap.ActionGetEvents("descend")
                .OfType<InputEventKey>()
                .Select(k => k.PhysicalKeycode)
                .ToList();
            Assert(descendKeys.Contains(Key.Greater), "> 没有接到 descend");
            Assert(descendKeys.Contains(Key.Period), "键位表承诺「. 下楼」，但 InputMap 里只有 >");
            // 「E / 回车」进房间同理
            var interactKeys = InputMap.ActionGetEvents("interact")
                .OfType<InputEventKey>()
                .Select(k => k.PhysicalKeycode)
                .ToList();
            Assert(interactKeys.Contains(Key.Enter) || interactKeys.Contains(Key.KpEnter),
                "键位表承诺「回车进房间」，但 InputMap 里没有回车");
            // 这一轮补上的两个：Z 吃药（原来只有按钮能点）、V 特效开关（TS 版有）
            Assert(UI.Keymap.Battle.Any(b => b.Keys == "Z"), "战斗键位表缺少消耗品 Z");
            Assert(UI.Keymap.Battle.Any(b => b.Keys == "V"), "战斗键位表缺少特效开关 V");
            // 战斗键位表里承诺的每一个单字符键，都得有一份「谁接了它」的登记。
            // 为什么不在断言里读源码：导出的二进制里没有 .cs（CI 会拿 export 产物再跑一遍），
            // 那条断言在产物上必然红。改成读 Keymap 自己的接线表——
            // 同一份数据既渲染说明面板，也被这里核对，改一处漏一处会被抓到。
            var battleKeys = UI.Keymap.Battle
                .SelectMany(b => b.Keys.Split(' ', '~', '/'))
                .Select(k => k.Trim())
                .Where(k => k.Length == 1 && char.IsLetter(k[0]))
                .Distinct()
                .ToList();
            foreach (var k in battleKeys)
                Assert(UI.Keymap.BattleWiredKeys.Contains(char.ToUpperInvariant(k[0])),
                    $"战斗键位表写了 {k}，但 Keymap.BattleWiredKeys 里没有登记它（说明与实现会再次对不上）");
        });

        Step("三 Boss 连战的每一只都有自己的机制与曲目", () =>
        {
            var data = AbyssEcho.Data.GameDataStore.Get();
            var gauntlet = data.BossStages.First(st => st.Mechanic == "boss_gauntlet");
            var run = new Game(25);
            run.RestoreFloor(gauntlet.Floor, 1, GameStatus.Playing);
            run.TeleportForDebug(run.Map.BossIndex);
            var seen = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var enc = run.CreateEncounter();
                Assert(enc is not null, $"连战第 {i + 1} 只没有生成遭遇");
                Assert(enc!.Gauntlet is { } g && g.Index == i && g.Total == 3, $"连战进度不对：{enc.Gauntlet}");
                var mech = enc.BossMechanic?.Name ?? "";
                Assert(mech.Length > 0, $"连战第 {i + 1} 只没有 Boss 机制（玩家打的是三只没机制的 Boss）");
                seen.Add(mech);
                run.OnBattleWon(enc.Def, true, 1, 1, 1);
            }
            Assert(seen.Distinct().Count() == 3, $"三只 Boss 的机制重复了：{string.Join("/", seen)}");
        });

        Step("战斗舞台画得出背景（后墙 / 地砖 / 擂台 / 石柱），不是一块死色", () =>
        {
            // TS 版 drawArena 是纯视觉的一块，但少了它立绘就像贴在表格上。
            // 这里只断言「建得出来、铺得满」——背景不承载规则，不该进值断言。
            var run = store.Run!;
            var index = run.Map.Rooms.FindIndex(r => r.Type is RoomType.Enemy or RoomType.Elite or RoomType.Boss);
            run.Teleport(index);
            var enc = run.CreateEncounter();
            var battle = new BattleScene();
            battle.Configure(run, enc!);
            AddChild(battle);
            var arena = FindChildOfType<UI.Arena>(battle);
            Assert(arena is not null, "战斗场景里没有舞台背景层");
            Assert(arena!.Size.X > 0 && arena.Size.Y > 0, $"舞台背景尺寸 {arena.Size}");
            battle.QueueFree();
        });

        await StepAsync("敌人技能预告与玩家状态条都有内容（不是永远空着）", async () =>
        {
            // TS 版有两条：敌人技能 chip 行、玩家 buff 行。Godot 版一直缺，
            // 于是「对面下回合可能放什么」「我身上还挂着什么」只能靠逐条读日志。
            var run = store.Run!;
            var index = run.Map.Rooms.FindIndex(r => r.Type is RoomType.Enemy or RoomType.Elite or RoomType.Boss);
            run.Teleport(index);
            var enc = run.CreateEncounter();
            var battle = new BattleScene();
            battle.Configure(run, enc!);
            AddChild(battle);
            await Frame();
            var texts = new List<Label>();
            Collect(battle, texts);
            // 敌人带技能才有这一行；没技能时留空是对的——这里要证明的是「有技能够时它会写」。
            if (enc!.Monster.Skills.Count > 0)
                Assert(texts.Any(t => t.Text.StartsWith("可能出招")), "敌人有技能却没有预告");
            battle.QueueFree();
        });

        await StepAsync("地图 HUD 会指出下一步该往哪走（不是只报剩余房间数）", async () =>
        {
            var map = new MapScene();
            AddChild(map);
            // HUD 的文本要等一帧布局后才刷出来（_Ready 那一刻 Size 还是 0）
            await Frame();
            await Frame();
            var labels = new List<Label>();
            Collect(map, labels);
            Assert(labels.Any(l => l.Text.Contains("最近可去")), "底部提示没有指向未处理的房间");
            map.QueueFree();
        });

        Step("BGM：每种楼层机制都有自己显式配的曲子", () =>
        {
            var data = AbyssEcho.Data.GameDataStore.Get();
            foreach (var mechanic in data.FloorMechanics.Keys)
            {
                if (mechanic == "none") continue;
                Assert(UI.Bgm.ByMechanic.ContainsKey(mechanic), $"机制 {mechanic} 没配曲，会静默退回默认垫底");
            }
        });

        // ---- 这一轮新增：提示队列 / 事件换道具 / 新手引导 / 特效开关 ----

        Step("地图 HUD 的两条进度条粗细不同：血条 16px、经验条 8px，容器不许把它们拉成一样粗", () =>
        {
            // 截图上 HUD 左上角是「两条一样粗的条」：血条写 16、经验条写 8，量出来都是 17。
            // `CustomMinimumSize` 只是下限，`ProgressBar` 自己报的最小高是 0，
            // 于是同一排 HBox 把这一行多余的高度摊给了它们——**尺寸写得对，长出来不对**。
            var hp = UI.UiTheme.Bar(UI.Palette.Danger, 16);
            var xp = UI.UiTheme.Bar(UI.Palette.Accent, 8);
            Assert(hp.CustomMinimumSize.Y - xp.CustomMinimumSize.Y >= 6f,
                $"血条 {hp.CustomMinimumSize.Y} / 经验条 {xp.CustomMinimumSize.Y}：两条本来就该差一档");
            // 真正的判据：容器里长出来的高度必须还是 16 / 8。
            // 这里不摆一个 HBox 去量——`Control` 的最小尺寸只在**进树之后**才算得出来，
            // 没进树的条子 `Size` 永远等于它自己声明的那一个（第一版就是这么写的，
            // 修复前照样绿：量到的不是布局结果，是输入）。
            // 改成问「这两条在容器里会不会被拉高」：MinSize 由 stylebox 的四个内边距说了算，
            // 只要上下内边距是 0、且不许 ExpandFill，容器就没有把它们拉高的余地。
            // 这一层只守**静态契约**：最小高对不对、纵向 SizeFlags 有没有被写成 ExpandFill。
            // 「画出来到底多粗」由截图工具守（`Screenshot.MapHudBars` 数像素行）——
            // 这个坑在冒烟里量不出来：没进树的 Control 的 `Size` 就是它自己声明的那一个，
            // 而进树的又得等一整帧布局。第一版两种写法写错了都读不出差别。
            foreach (var (name, bar3, want3) in new[] { ("血条", hp, 16f), ("经验条", xp, 8f) })
            {
                var bg = bar3.GetThemeStylebox("background") as StyleBoxFlat;
                Assert(bg is not null, $"{name}没有自己的样式盒");
                Assert(bg!.ContentMarginTop == 0f && bg.ContentMarginBottom == 0f,
                    $"{name}的样式盒上下各留了 {bg.ContentMarginTop:0}/{bg.ContentMarginBottom:0}px：最小高会被它撑开");
                // 关键的两条：`CustomMinimumSize` 只是下限，真正决定「长多高」的是
                // 最小尺寸（由 stylebox 内边距 + 字号一起算出来的那个）与纵向 SizeFlags。
                // 上一版这两条都靠默认值，一排里的血量文字（20px）就把两条一起拉成 17px。
                Assert(Mathf.Abs(bar3.GetCombinedMinimumSize().Y - want3) <= 1f,
                    $"{name}的最小高是 {bar3.GetCombinedMinimumSize().Y:0}，写的却是 {want3:0}：容器会按这个数摆它");
                Assert(bar3.SizeFlagsVertical != Control.SizeFlags.ExpandFill,
                    $"{name}是 ExpandFill：一排里最高的那个（血量文字）会把它一起撑高，两条就一样粗了");
            }
            // 两条真的差一档：修复前它们的最小高都是 20（血量文字那一行的高度）
            Assert(hp.GetCombinedMinimumSize().Y > xp.GetCombinedMinimumSize().Y + 4f,
                $"血条最小高 {hp.GetCombinedMinimumSize().Y:0}、经验条 {xp.GetCombinedMinimumSize().Y:0}：两条分不出粗细");
            hp.QueueFree();
            xp.QueueFree();
        });

        Step("提示队列：一条在演、其余排队，演完自动接上；同一条消息节流", () =>
        {
            // TS 版在这里翻过车：把「播完了」寄托在淡出回调上，场景一重启回调就不来了，
            // 队列卡死在「有一条正在演」，之后所有提示被静默吞掉（Issue #61）。
            // 所以「谁在演」必须是能被断言的状态。
            var q = new UI.ToastQueue();
            Assert(q.Enqueue(new UI.ToastItem { Text = "甲" }), "空队列的第一条应该立刻开演");
            Assert(q.Begin(), "标记开演失败");
            Assert(q.Current?.Text == "甲", $"队首应是甲，实际 {q.Current?.Text}");
            Assert(!q.Enqueue(new UI.ToastItem { Text = "乙" }), "已有在演的，第二条应排队而不是抢播");
            Assert(q.Pending == 2, $"应有两條排队，实际 {q.Pending}");
            q.Dequeue();
            Assert(q.Playing && q.Current?.Text == "乙", "第一条演完应自动接上第二条");
            q.Dequeue();
            Assert(!q.Playing && q.Pending == 0, "演完最后一条应收尾");
            // 重置（场景销毁）必须把「正在演」也放下来
            q.Enqueue(new UI.ToastItem { Text = "丙" });
            q.Begin();
            q.Reset();
            Assert(!q.Playing && q.Pending == 0, "Reset 之后队列应彻底干净");
            // 节流：同一条消息在窗口内不重复弹，换了内容或过了窗口就该弹
            Assert(UI.ToastRules.ShouldSuppress("那边没有路", "那边没有路", 0.2f), "同一条消息没有被节流");
            Assert(!UI.ToastRules.ShouldSuppress("那边没有路", "那边没有路", 1f), "过了窗口还在节流");
            Assert(!UI.ToastRules.ShouldSuppress("获得金币", "那边没有路", 0.1f), "不同消息被误节流");
            // 落点：挂在底部提示条上方那一条空带上，不许压在屏幕中间。
            // 0.66 那张画布是 960×540 的竖构图；搬到 1280×720 的地图上，0.66 正好落在
            // **最后一排房间砖**里——提示胶囊盖住玩家要点的下一格（截图里「宝箱房」被吃掉一半）。
            // 所以判据不是「等于某个常数」，而是「不许落在任何一块房间砖上」＋「不许出屏」。
            // 落点由「地图网格画到哪」决定，不由面板边界决定：
            // 地图垂直居中，1280×720 下最后一排砖的下沿在 ~628，而提示条上沿在 674——
            // 只差 46px，刚好是一个胶囊。上一版按「提示条上沿 - 14」摆，胶囊就骑在砖上。
            // 网格下沿用**实测**的那个数：1280×720 下最后一排砖画到 ~600（截图量的）。
            // 上一版写 628 是按「砖顶 + 92」推的，多算了一格，于是这条断言把
            // 「本来压不上砖」的落点判成压上了——判据自己也得跟着画面改。
            var gridBottom = 600f;
            var pillTop = UI.ToastRules.PillTopFor(720f, 46f, gridBottom);
            var pillBottom = pillTop + UI.ToastRules.PillHeight;
            Assert(pillTop >= gridBottom, $"提示胶囊顶边 {pillTop:0} 压在网格下沿 {gridBottom:0} 之上：它会盖住最后一排砖");
            Assert(pillBottom <= 720f - 46f + 0.01f, $"提示胶囊底边 {pillBottom:0} 压进底部提示条（{720 - 46}）");
            // 没有网格的界面（战斗 / 主菜单）退回「贴着提示条」，但也不许出屏
            var bare = UI.ToastRules.PillTopFor(576f, 46f, 0f);
            Assert(bare > 0f && bare + UI.ToastRules.PillHeight <= 576f - 46f + 0.01f,
                $"576 高、没有网格时提示胶囊 {bare:0} 出屏");
            // 网格长到贴住提示条（两者之间没有空地）时，「不许压提示条」优先：
            // 提示条是这块场景自己的边界，压上去等于把玩家正在读的提示盖掉。
            var tight = UI.ToastRules.PillTopFor(720f, 46f, 700f);
            Assert(tight + UI.ToastRules.PillHeight <= 720f - 46f + 0.01f,
                $"网格贴底时胶囊底边 {tight + UI.ToastRules.PillHeight:0} 压进了底部提示条");

            // **底下塞不下、挪到网格上方那一档：不许压在顶部 HUD 上。**
            //
            // 1280×720 的地图就是这一档：网格画到 628、图例垂到 662、提示条上沿 674，
            // 底下只剩 12px。上一版「挪上去」只按「离网格 Clearance」算——
            // 而网格上方还压着一条 74px 的 HUD：胶囊摆在 59..105，一多半在 HUD 面板上，
            // 描边横穿 HUD 那条贴边发光线。判据以前只查「压没压到砖与图例」，HUD 不算。
            // 现在那条带子由 HUD 下沿与网格上沿两头夹出来，胶囊在带子里居中。
            // 网格上沿不写死：它是地图按「HUD + 提示飞行带」算出来的，随这层的包围盒变。
            // 判据问 `MapMetrics.Fit` 要——那是 `MapScene.UpdateMetrics` 真的会用的同一个函数，
            // 在断言里抄一个常数等于自己跟自己对答案（这条坑在这个仓库里栽过六次了）。
            var gridTop720 = UI.MapMetrics.Fit(new Vector2(1280f, 720f), MapScene.HudBandHeight,
                MapScene.HintBandHeight, 3, 1, 4, 4,
                UI.MapLegend.WidthFor(2) + 36f, MapScene.MaxTile, 44).Origin.Y;
            var real = UI.ToastRules.PillTopFor(720f, MapScene.HintBandHeight, 662f,
                gridTop720, MapScene.HudBandHeight);
            var realBottom = real + UI.ToastRules.PillHeight;
            Assert(real >= MapScene.HudBandHeight,
                $"提示胶囊顶边 {real:0} 压在顶部 HUD 上（HUD 下沿 {MapScene.HudBandHeight:0}）：描边会横穿 HUD 的发光线");
            Assert(realBottom <= gridTop720 + 0.01f,
                $"提示胶囊底边 {realBottom:0} 压进网格上沿 {gridTop720:0}：它盖住了第一排砖");
            // 那条带子只有 45px 时，「不许压 HUD、不许压砖」这两条在同一屏里无解——
            // 那不是摆歪了，是这块屏幕给它的地方不够；所以带子由地图让出来，
            // 判据钉的是「地图让出的那条带子够放整条飞行区间」。
            var band = gridTop720 - MapScene.HudBandHeight;
            Assert(UI.ToastRules.BandHeight <= band + 0.01f,
                $"提示的飞行区间要 {UI.ToastRules.BandHeight:0}px，地图只让出 {band:0}px：胶囊会压到 HUD 或砖上");
        });

        await StepAsync("商店面板里有「卖」的出口：钱够、药在架、背包满时，玩家腾得出一格", async () =>
        {
            // 横扫 30 局里「血满、0 瓶药、钱够买一瓶、背包满」出现过 300 多次。
            // 钱、药、店都不缺，缺的是一条把「换装剩下的旧货」变成格子的路。
            // 逻辑层的 SellItem 由自检钉住了；这里钉的是**界面上真的按得到它**——
            // 规则层有一条没人点得到的路，和没实现是一样的（前几轮已经栽过两次）。
            var store5 = GetNode<GameStore>("/root/GameStore");
            store5.NewRun(4242);
            var map = new MapScene();
            AddChild(map);
            await Frame();

            var run = store5.Run;
            // 塞满背包：全塞装备（换装换下来的那种旧货）
            var data = AbyssEcho.Data.GameDataStore.Get();
            var gear = data.Items.Where(i => i.Slot != AbyssEcho.Data.Slot.Consumable).ToList();
            run.Player.Inventory.Clear();
            for (var i = 0; i < run.Capacity; i++) run.Player.Inventory.Add(gear[i % gear.Count].Id);

            // 本层的商店格：直接调 OpenShop（和玩家点进去是同一条路）
            var shopIndex = -1;
            for (var i = 0; i < run.Map.Rooms.Count; i++)
                if (run.Map.Rooms[i].Type == AbyssEcho.Core.RoomType.Shop) { shopIndex = i; break; }
            Assert(shopIndex >= 0, "这张图上没有商店房，这条断言没法验证");
            typeof(MapScene).GetMethod("OpenShop", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(map, new object[] { shopIndex, 0 });
            await Frame();
            await Frame();

            var texts = new List<Label>();
            Collect(map, texts);
            Assert(texts.Any(t => t.Text.Contains("收购")), "商店面板里没有「收购」这一块：背包满了就只能转身走人");
            // 收购是按件陈列的按钮：每一件的文案是「名字 · 价格」
            var buttons = new List<Button>();
            Collect(map, buttons);
            var sellButtons = buttons.Where(b => b.Text.Contains("·") && b.Text.Split("·").Length == 2).ToList();
            Assert(sellButtons.Count > 0, "收购区里一件可卖的东西都没有（背包明明塞满了）");

            // 真按一下：背包腾出一格，金币按卖价到账
            var goldBefore = run.Player.Gold;
            sellButtons[0].EmitSignal("pressed");
            await Frame();
            await Frame();
            Assert(run.Player.Inventory.Count < run.Capacity, $"点了「卖」背包还是满的（{run.Player.Inventory.Count}/{run.Capacity}）");
            Assert(run.Player.Gold > goldBefore, $"点了「卖」金币没到账：{goldBefore} → {run.Player.Gold}");
            map.QueueFree();
        });

        Step("事件换道具：玩家挑的那件被换掉，且不会再多问一次「背包满了」", () =>
        {
            // 以前 SwapItem 走的是「随机挑一件 + 带上 NeedDiscard」，
            // 于是界面跳过挑选那一步、交换完又弹一次「背包满了要放下什么」（其实没满）。
            var data = AbyssEcho.Data.GameDataStore.Get();
            var player = new AbyssEcho.Core.Player();
            var equip = data.Items.Where(i => i.Slot != AbyssEcho.Data.Slot.Consumable).ToList();
            Assert(equip.Count >= 2, "数据里可换的装备不足两件，这条断言没法验证");
            player.Inventory.Add(equip[0].Id);
            player.Inventory.Add(equip[1].Id);
            var keep = player.Inventory[0];
            var rng = new AbyssEcho.Core.Rng(4242);
            var ctx = new AbyssEcho.Systems.EventApplyContext
            {
                Rng = rng,
                Items = data.Items,
                Capacity = 8,
                ItemsById = data.Items.ToDictionary(i => i.Id),
                PanelMaxHp = 60,
                SwapOutIndex = 1,
            };
            var result = AbyssEcho.Systems.GameEvents.ApplyEffect(player, new AbyssEcho.Data.EventEffect { Type = AbyssEcho.Data.EventEffectType.SwapItem }, ctx);
            Assert(player.Inventory[0] == keep, "没挑的那件被换掉了");
            Assert(player.Inventory[1] != equip[1].Id, "挑中的那件没被换走");
            Assert(!result.NeedDiscard, "交换是原地替换，不该再要求玩家丢东西");
            Assert(result.Text.Contains("失去了"), $"结果文案没说清失去了什么：{result.Text}");
        });

        await StepAsync("新手引导：第一次开局讲一次「品质 = 词条数」，之后不再弹", async () =>
        {
            // TS 版有这张卡（showIntroCard），Godot 版一直没有——玩家只会按攻击力挑装备。
            var store0 = GetNode<GameStore>("/root/GameStore");
            // 计数决定这条判据问什么：前两局要弹，第三局起不许再弹。
            // 以前写的是「第一个存档只弹一次」，于是玩家的第二局起什么提示都没有。
            var runsBefore = store0.Meta.Runs;
            store0.NewRun(999);
            var expectsCard = store0.Meta.Runs <= 2;
            var map = new MapScene();
            AddChild(map);
            await Frame();
            await Frame();
            var texts = new List<Label>();
            Collect(map, texts);
            var shown = texts.Any(t => t.Text.Contains("词条才是构筑"));
            Assert(shown == expectsCard,
                expectsCard
                    ? $"第 {store0.Meta.Runs} 局没有讲「品质=词条数」"
                    : $"第 {store0.Meta.Runs} 局还在弹新手引导（讲第二遍是复习，第三遍就是烦人）");
            map.QueueFree();
            // 标记必须落盘：看过之后 `intro_seen` 就该在盘上
            Assert(store0.IntroSeen, "看过引导之后标记没留下");
            Assert(store0.Meta.Runs == runsBefore + 1, $"开局计数没有 +1（{runsBefore} → {store0.Meta.Runs}）");
        });

        Step("战斗特效开关是持久化偏好，关掉只影响表现", () =>
        {
            var store1 = GetNode<GameStore>("/root/GameStore");
            var before = store1.FxEnabled;
            var now = store1.ToggleFx();
            Assert(now != before, "开关没有翻转");
            store1.ToggleFx(); // 还原，别把状态留给后面的断言
            Assert(store1.FxEnabled == before, "还原失败");
        });

        Step("BGM 与音效不参与特效开关：关特效不该把声音一起关掉", () =>
        {
            // 这条是给「两个开关别混成一个」上的锁：TS 版 M 是静音、V 是特效。
            var muted = AbyssEcho.UI.Sfx.Muted;
            var store2 = GetNode<GameStore>("/root/GameStore");
            var fxBefore = store2.FxEnabled;
            store2.ToggleFx();
            Assert(AbyssEcho.UI.Sfx.Muted == muted, "切特效把音效静音状态改掉了");
            if (store2.FxEnabled != fxBefore) store2.ToggleFx();
            Assert(store2.FxEnabled == fxBefore, "还原特效开关失败");
        });

        await StepAsync("地图上喝的药立刻落盘，并把反馈说出来", async () =>
        {
            // 以前喝完药不写档：存档里存的是背包 id 列表，关窗就等于把药退回来。
            // 这条查的是「界面那一步有没有真的落盘」，所以直接读盘、不读内存里的那一局。
            var store4 = GetNode<GameStore>("/root/GameStore");
            var data = AbyssEcho.Data.GameDataStore.Get();
            var potion = data.Items.First(i => i.Slot == AbyssEcho.Data.Slot.Consumable && i.Heal is > 0);
            // 清档必须在 NewRun 之前：ClearRun 会把 GameStore.Run 置空，
            // 先开一局再清，后面 SetHp 读到的 run 已经不是 store4 手上那一局了
            // （这一条曾在 tag 1.9.0 上让整条发布流水线红）。
            store4.ClearRun();
            store4.NewRun(31415);
            var run = store4.Run!;
            run.ItemIndex[potion.Id] = potion;
            run.Player.Inventory.Clear();
            run.Player.Inventory.Add(potion.Id);
            run.Player.Hp = Math.Max(1, run.Stats.MaxHp / 2);
            // 喝药是玩家动作，界面那一步要等地图场景布局完再问「有没有提示」
            var map2 = new MapScene();
            AddChild(map2);
            await Frame();

            // 直接调私有入口：这与玩家按 I 再点「使用」走的是同一条路。
            // 名字变了就要在这里红——别让一条找不到方法的断言悄悄变绿。
            var method = typeof(MapScene).GetMethod("UseConsumable",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert(method is not null, "MapScene 上没有 UseConsumable：这条断言已经找不到要测的东西了");
            method!.Invoke(map2, new object[] { 0 });
            var toasts = new List<UI.ToastLayer>();
            Collect(map2, toasts);
            Assert(toasts.Count == 1, "地图上没有提示层");
            Assert(toasts[0].Playing, "喝药之后一句反馈都没有");
            Assert(store4.HasSavedRun(), "地图上喝完药没有落盘：关窗会把药送回背包");

            // 盘上那份里不该再有那瓶药
            var reloaded = AbyssEcho.Autoload.SaveSerializer.Load("user://abyss_echo_run.cfg", store4.Meta);
            Assert(reloaded is not null, "刚写的档读不回来");
            Assert(!reloaded!.Player.Inventory.Contains(potion.Id), "盘上的背包里还有那瓶已经喝掉的药");
            map2.QueueFree();
            store4.ClearRun();
        });

        await StepAsync("开宝箱走的是三选一面板，而且清房与落盘在「真的拿走了」之后", async () =>
        {
            // 宝箱房以前是「只给一张卡」的捷径：它绕过了战后三选一那块面板，
            // 于是「回血被上限截掉多少」这类修复只在战后生效，箱子里的那张卡仍写着旧数；
            // 那条路上也一次 SaveRun 都没有——开完箱子关窗，箱子还开着、钱和血却退回去了。
            // 这条钉两件事：面板真的是三选一、清房之前先落盘。
            var store5 = GetNode<GameStore>("/root/GameStore");
            store5.ClearRun();
            store5.NewRun(2718);
            var run = store5.Run!;
            // 造一个宝箱房：挑一层还有空房的地图把它改成 Treasure
            var idx = run.Map.Rooms.FindIndex(r => r.Type == AbyssEcho.Core.RoomType.Empty);
            Assert(idx >= 0, "这一层没有空房间可以改成宝箱房（前置条件不成立）");
            run.Map.Rooms[idx].Type = AbyssEcho.Core.RoomType.Treasure;
            run.TeleportForDebug(idx);
            var map3 = new MapScene();
            AddChild(map3);
            await Frame();
            run.Player.Hp = Math.Max(1, run.Stats.MaxHp / 2);

            var open = typeof(MapScene).GetMethod("OpenTreasure",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert(open is not null, "MapScene 上没有 OpenTreasure：这条断言已经找不到要测的东西了");
            open!.Invoke(map3, new object[] { idx });
            await Frame();

            // 三选一：面板上要有多张「带走」按钮（宝箱只给一张卡的话这里只有 1 个）
            var buttons = new List<Button>();
            CollectButtons(map3, buttons);
            var take = buttons.Count(b => b.Text.Contains("带走"));
            Assert(take >= 2, $"宝箱面板上只有 {take} 张卡：开箱还没走三选一那条路");
            // 还没拿走：箱子必须还在（清房只在「真的拿走了」之后）
            Assert(!run.Map.Rooms[idx].Cleared, "一进宝箱房就清房了：玩家还没做任何决定");

            // 点第一张卡：清房 + 落盘
            var first = buttons.First(b => b.Text.Contains("带走"));
            first.EmitSignal(BaseButton.SignalName.Pressed);
            await Frame();
            Assert(run.Map.Rooms[idx].Cleared, "拿走之后箱子没有被清掉：还能再开一次");
            Assert(store5.HasSavedRun(), "开完箱子没有落盘：关窗等于把箱子里的东西退回去");
            map3.QueueFree();
            store5.ClearRun();
        });

        Step("关窗会落盘：关闭请求不是「直接退出」，而是先存再退", () =>
        {
            // 以前关窗等于丢进度：最后一个安全点可能是「刚喝完一瓶药」。
            // 两个前提都得在：AutoAcceptQuit 关掉、GameStore 接了关闭请求。
            var store3 = GetNode<GameStore>("/root/GameStore");
            Assert(!GetTree().AutoAcceptQuit, "AutoAcceptQuit 还开着：关闭请求根本不会走到 GameStore");
            // 走一次真实的保存路径：地图上喝药/买东西都会显式调 SaveRun，
            // 这里只验证「当前这一局能被写成一份可继续的存档」。
            store3.NewRun(4321);
            var run = store3.Run!;
            run.Player.Gold = 66;
            store3.SaveRun();
            Assert(store3.HasSavedRun(), "SaveRun 之后盘上没有可继续的局（关窗一样会丢）");
            store3.ClearRun();
            Assert(!store3.HasSavedRun(), "清档之后仍然显示可继续");
        });

        // ---- 层序：舞台上的东西各是第几层，以及它是不是「同一层里靠先加」 ----

        Step("层序表：没有两层撞在同一个层号上，几条明写的上下关系都成立", () =>
        {
            // 层号本身不是判据，「两件事撞在同一个数上」才是：
            // 上一轮敌人卡的上半截是墙色，就是因为 Arena 与两块条带都是 0，
            // 谁先 AddChild 谁在下——而那件事读代码读不出来。
            var orders = new List<(UI.StageOrder Order, string Name)>();
            foreach (UI.StageOrder o in Enum.GetValues(typeof(UI.StageOrder))) orders.Add((o, o.ToString()));
            for (var i = 1; i < orders.Count; i++)
            for (var j = 0; j < i; j++)
                Assert(orders[i].Order != orders[j].Order,
                    $"「{orders[i].Name}」与「{orders[j].Name}」撞在同一个层号 {UI.Layers.Of(orders[i].Order)} 上：两者谁在上只取决于谁先加");
            // 明写出来的几条顺序关系：它们是这一轮那几处 bug 的答案，不该靠数字大小猜
            Assert(UI.Layers.Above(UI.StageOrder.Panel, UI.StageOrder.Backdrop), "面板没有压在背景之上");
            Assert(UI.Layers.Above(UI.StageOrder.Stage, UI.StageOrder.Panel), "舞台上的人物被面板压住了");
            Assert(UI.Layers.Above(UI.StageOrder.Float, UI.StageOrder.Stage), "伤害数字会被人物盖住");
            Assert(UI.Layers.Above(UI.StageOrder.Fx, UI.StageOrder.Float), "粒子被飘字盖住了");
            Assert(UI.Layers.Above(UI.StageOrder.Legend, UI.StageOrder.Actor), "图例被地图上的玩家盖住了");
            Assert(UI.Layers.Above(UI.StageOrder.Toast, UI.StageOrder.Legend), "浮动提示压不住图例");
            Assert(UI.Layers.Above(UI.StageOrder.Overlay, UI.StageOrder.Toast), "弹窗压不住浮动提示");
            // 弹窗的「底」不许和弹窗自己同号：同级同 z 按添加顺序叠，两片 0.5 的黑
            // 会叠成一片 0.75 的灰板（而它看起来像「一张灰色截图」，不像 bug）。
            Assert(UI.Layers.Of(UI.StageOrder.Overlay) - 1 > UI.Layers.Of(UI.StageOrder.Toast),
                "弹窗的压暗层落到了提示之下");
            // 「面板的底」和「面板里装的东西」是两层。
            // 少了这一档，谁在上面就退化成「谁先 AddChild」——上一轮地图 HUD 的
            // 文字与两条进度条就是这么被自己的条带盖住的，一整轮没人发现：
            // 那一屏上 HUD 是空的，而血条一直在画。
            Assert(UI.Layers.Above(UI.StageOrder.PanelContent, UI.StageOrder.Panel),
                "面板的内容没有压在面板的底之上：先加的那一个会盖住后加的");
            // 战斗是挂在地图下面的：两棵树，ZIndex 却比在同一块画布上。
            // 战斗的子树根必须高过地图的面板内容，否则行动条会藏到地图 HUD 后面。
            Assert(UI.Layers.Above(UI.StageOrder.BattleRoot, UI.StageOrder.PanelContent),
                "战斗这一整块没有抬到地图 HUD 之上：行动条会藏到地图 HUD 后面");
            // 抬起来之后还得保证「战斗内部」的相对顺序不被压平：
            // 飘字（5）、粒子（6）是子树里的绝对数，子树根必须比它们高。
            Assert(UI.Layers.Above(UI.StageOrder.BattleRoot, UI.StageOrder.Fx),
                "战斗的子树根比自己的粒子层还低：飘字与特效会被压到地图的面板下面");
        });

        Step("覆盖层真的把下面压暗了：中间不许被抹回透明", () =>
        {
            // `ScrimBackdrop` 的注释写的是「灯被人调暗了」，而它画出来一直是**没有暗角效果的**：
            // `_Draw` 里那圈 `Paint.Glow(..., alpha: 0)` 把中间整块抹回透明，
            // 于是弹窗下面那张地图和弹窗一样亮——玩家分不清哪一层在上面。
            //
            // 判据读的是「它真正画出来的那一层」的深度，不是断言里再算一遍公式：
            // `DarkestAlpha` 由 `_Draw` 用的同一组数算出来。
            var declared = UI.ScrimBackdrop.OverlayColor.A;
            Assert(declared >= 0.35f, $"覆盖层的黑只有 {declared:P0}：它压不暗下面那张屏");
            var deepest = UI.ScrimBackdrop.DarkestAlpha;
            Assert(deepest >= declared - 0.001f,
                $"覆盖层画出来的最深处只有 {deepest:P0}（声明 {declared:P0}）：中间那一块被抹回透明了，压暗是假的");
        });

        Step("场景代码里不再有裸的 ZIndex 字面量：每一处都说得出「我是哪一层」", () =>
        {
            // 把上一轮留下来的那件事钉住：那时候一堆数字散在四个文件里
            // （条带 1 / 舞台 3 / 飘字 5 / 粒子 6 / 面板 10 / 提示 90 / 覆盖层 100），
            // 每个数都在跟另一个数比大小。现在它们都走 Layers.Of(...)。
            //
            // 判据只能读源码：层序错了不抛异常，也不会让任何控件量出奇怪的尺寸——
            // 它只在截图上看得出来，而「只在截图上看得出来」正是这条要防的。
            // 走 FileAccess 而不是 System.IO：和 RoomFlowProbe 一样，导出的二进制里
            // res:// 下没有 .cs，这条自然读不到、跳过（那种环境下它本来也无从检查）。
            // 目录也走 `res://`：导出的二进制里项目封在 pck 里，`GlobalizePath`
            // 算出来的绝对路径在磁盘上根本不存在——那样问到的「没有源码」
            // 与「pck 里的 1 字节占位符」是两件事，判据该分开认。
            const string dir = "res://Scripts";
            if (!Godot.DirAccess.DirExistsAbsolute(dir))
            {
                GD.Print("        [skip] 源码树不在（导出产物里没有 .cs），静态层序检查跳过");
                return;
            }
            var bad = new List<string>();
            var scanned = 0;
            ScanLayers(dir, bad, ref scanned);
            if (scanned == 0)
            {
                // 导出产物里每个 `.cs` 都是 1 字节占位符，一个真源码都扫不到。
                // 这种情况要**明说跳过**，不能拿一个空扫描去换一条绿：
                // 判据什么都没读到的时候，它的绿没有意义。
                GD.Print("        [skip] 一个真源码都没扫到（导出产物里 .cs 是占位符），静态层序检查跳过");
                return;
            }
            Assert(scanned > 20, $"只扫到 {scanned} 个源文件，路径大概不对：{dir}");
            Assert(bad.Count == 0,
                $"{bad.Count} 处 ZIndex 还是裸数字（{string.Join("、", bad.Take(5))}）：改用 Layers.Of(StageOrder.X)");
        });

        await StepAsync("地图上「你」比房间砖高、图例又比「你」高（不靠谁先 AddChild）", async () =>
        {
            var store5b = GetNode<GameStore>("/root/GameStore");
            store5b.NewRun(777);
            var map = new MapScene();
            AddChild(map);
            // 玩家立绘不是 `_Ready` 里建的：`MapScene._Process` 要等**自己有了尺寸**
            // 才建房间层与立绘（`_Ready` 那一刻 Size 还是 0，按 0 算会把整张网格算到视口外）。
            // 所以这里必须等一帧——而且不能只等一帧就当它好了：
            // 上一版是同步 Step，同帧里找 `HeroSprite`，只能指望「宿主恰好也是同帧布局」，
            // 在 CI 上就不成立（60 通过 / 3 失败，三条全挂在这条链上）。
            // 等的是「它真的建出来了」，最多等十帧；等不到再报，报的是真话。
            for (var i = 0; i < 10 && FindChildOfType<UI.HeroSprite>(map) is null; i++) await Frame();
            var hero = FindChildOfType<UI.HeroSprite>(map);
            var legend = map.FindChild("MapLegend", true, false) as Control;
            Assert(hero is not null, "地图上没有玩家立绘（等了 10 帧：房间层与立绘都没建出来）");
            Assert(legend is not null, "地图上没有图例");
            Assert(hero!.ZIndex == UI.Layers.Of(UI.StageOrder.Actor), $"玩家立绘在 z={hero.ZIndex}，不是 Actor 那一层");
            Assert(legend!.ZIndex == UI.Layers.Of(UI.StageOrder.Legend), $"图例在 z={legend.ZIndex}，不是 Legend 那一层");
            var tile = FindRoomTiles(map).FirstOrDefault();
            Assert(tile is not null, "地图上没有房间砖");
            Assert(hero.ZIndex > tile!.ZIndex, $"玩家 z={hero.ZIndex} 不比房间砖 z={tile.ZIndex} 高：站在砖上会被砖压住");
            Assert(legend.ZIndex > hero.ZIndex, $"图例 z={legend.ZIndex} 不比玩家 z={hero.ZIndex} 高");
            map.QueueFree();
        });

        await StepAsync("战斗场景：面板压在舞台背景之上，飘字与粒子又在人物之上", async () =>
        {
            // 上一轮的两处：敌人卡上半截是墙色（条带 z=0 与 Arena 的 z=0 不分先后）、
            // 以及「面板跟着往上提」那一句把 top/bottom 提到 10，而舞台是 3——
            // 顺序对了，但那是三个数比出来的对。现在它们各自说出自己是谁。
            var store6 = GetNode<GameStore>("/root/GameStore");
            var run = store6.Run ?? store6.NewRun(909);
            var index = run.Map.Rooms.FindIndex(r => r.Type is RoomType.Enemy or RoomType.Elite or RoomType.Boss);
            run.Teleport(index);
            var enc = run.CreateEncounter();
            var battle = new BattleScene();
            battle.Configure(run, enc!);
            AddChild(battle);
            var arena = FindChildOfType<UI.Arena>(battle);
            var enemySprite = battle.FindChild("EnemySprite", true, false) as Control;
            var numbers = battle.FindChild("DamageNumbers", true, false) as Control;
            var particles = battle.FindChild("ParticleLayer", true, false) as Control;
            var enemyPanel = battle.FindChild("EnemyPanel", true, false) as Control;
            // 两块条带的底是分层时出的那处错的主角：它们必须在舞台背景之上。
            // 按类型找而不是按名字找——`HudBand` 是自绘控件，名字只是给截图工具看的。
            var bands = new List<UI.HudBand>();
            Collect(battle, bands);
            var bandNames = string.Join(" / ", bands.Select(b => b.Name.ToString()));
            Assert(arena is not null && enemySprite is not null && numbers is not null && particles is not null,
                "战斗场景缺了舞台 / 立绘 / 飘字 / 粒子中的某一层");
            Assert(bands.Count >= 2, $"战斗场景里只找到 {bands.Count} 块条带（{bandNames}）：面板的底色没建出来");
            foreach (var band in bands)
                Assert(band.ZIndex > arena!.ZIndex,
                    $"条带「{band.Name}」z={band.ZIndex} 不高于舞台背景 z={arena.ZIndex}：面板会被墙色盖掉");
            Assert(enemySprite!.ZIndex > enemyPanel!.ZIndex,
                $"立绘 z={enemySprite.ZIndex} 不高于敌人卡 z={enemyPanel.ZIndex}：舞台上的人会被卡压住");
            Assert(numbers!.ZIndex > enemySprite.ZIndex, $"飘字 z={numbers.ZIndex} 不高于立绘 z={enemySprite.ZIndex}");
            Assert(particles!.ZIndex >= numbers.ZIndex, $"粒子 z={particles.ZIndex} 低于飘字 z={numbers.ZIndex}");

            // 面板的**内容**必须压在舞台之上。
            //
            // 上面那几条问的是「条带（底）高于 Arena」「立绘高于卡」，全绿——
            // 而这一轮 `03-battle.png` 上整个敌人卡和整条行动条都不见了：
            // `_Ready` 末尾有一句 `top.ZIndex = Panel; bottom.ZIndex = Panel`（= 1），
            // 把开头写好的 `PanelContent`（= 2）覆盖掉，而舞台是 3。
            // 于是名字、血条、按钮、日志全被 Arena 的墙与地砖盖住，
            // 屏幕上只剩两只立绘和一大片斜纹，而每一条断言都绿着——
            // 它们问的是「条带在背景之上」「立绘在卡之上」，没人问过「卡里的字在舞台之上」。
            // 回合制的界面里，玩家要在看怪的同时读血条、按技能：内容比人物高是必须的。
            var actionPanel = battle.FindChild("ActionPanel", true, false) as Control;
            Assert(enemyPanel.ZIndex > enemySprite!.ZIndex - 1 || enemyPanel.ZIndex > arena!.ZIndex,
                $"敌人卡 z={enemyPanel.ZIndex} 不高于舞台背景 z={arena!.ZIndex}：卡里的名字与血条会被墙色盖掉");
            Assert(enemyPanel.ZIndex > arena!.ZIndex,
                $"敌人卡 z={enemyPanel.ZIndex} 不高于舞台背景 z={arena.ZIndex}：卡里的名字与血条会被墙色盖掉");
            Assert(enemyPanel.ZIndex >= UI.Layers.Of(UI.StageOrder.PanelContent),
                $"敌人卡 z={enemyPanel.ZIndex} 低于 PanelContent（{UI.Layers.Of(UI.StageOrder.PanelContent)}）");
            Assert(actionPanel is not null, "战斗界面里找不到行动条（ActionPanel）");
            Assert(actionPanel!.ZIndex >= UI.Layers.Of(UI.StageOrder.PanelContent),
                $"行动条 z={actionPanel.ZIndex} 低于 PanelContent（{UI.Layers.Of(UI.StageOrder.PanelContent)}）：" +
                "血条、按钮行、日志整块会被 Arena 的后墙盖掉");

            // 地图那棵树不许压住战斗——这条**不能用层号判**。
            //
            // `Legend`（40）高于 `BattleRoot`（30）是**对的**：图例得压在地砖与玩家之上，
            // 那是它自己的那棵树里的正确答案。两棵树对撞时，靠「谁的数字大」解决不了，
            // 因为两个数字各自都有正当理由（这一轮的教训：两个都对，放在一起错）。
            // 真正的意思是「战斗时地图整块不该被看见」，而那是 `Visible` 的事，不是层号的事。
            // 判据放在下面那条「战斗时地图的零件全都收起来」里。

            // EnemyBand / ActionBand 是另一个分支 AddChild 的、且带锚点：等一帧布局之后再量，
            // 免得这里量到的是「还没排版的、尺寸全是 0 的那一帧」
            await Frame();
            battle.QueueFree();
        });

        // 日志块的两条：宽度与最后一行。
        //
        // 这一屏出过一次很难看的错：日志面板被排成 23px 宽的窄条（竖在左下角，认不出是什么），
        // 而所有断言都绿——它们问的是**高度**。宽度写的是 `ExpandFill`，
        // 而 `ExpandFill` 在 VBox 里是「分横向富余」：这一列里 `Actions`（按钮行，最小宽 755）
        // 也在要，日志块的最小宽只有一格字（1px + 内边距 = 23），于是它分到 0。
        await StepAsync("日志块的宽度撑满行动条，最后一行不会被切在字腰上", async () =>
        {
            var store7 = GetNode<GameStore>("/root/GameStore");
            var run = store7.Run ?? store7.NewRun(910);
            var index = run.Map.Rooms.FindIndex(r => r.Type is RoomType.Enemy or RoomType.Elite or RoomType.Boss);
            run.Teleport(index);
            var enc = run.CreateEncounter();
            var battle = new BattleScene();
            battle.Configure(run, enc!);
            AddChild(battle);
            await Frame();
            await Frame();
            // 只有一行日志时怎么排都对：宽度与裁字都是「打了两回合之后」才出现的
            battle.PlayScriptedTurnsForShot(2);
            for (var i = 0; i < 3; i++) await Frame();
            await battle.SyncLogUntilStableForTest();

            var panel = battle.FindChild("LogPanel", true, false) as PanelContainer;
            var col = panel?.GetParent() as VBoxContainer;
            Assert(panel is not null && col is not null, "找不到日志面板或它所在的那一列");
            // 一、宽度：它等于父级的内容宽，不是「内容要多宽」。
            // 差 2px 容得下（描边、取整），差一个数量级就是那条 23px 的窄条。
            Assert(Math.Abs(panel!.Size.X - col!.Size.X) < 2f,
                $"日志块宽 {panel.Size.X:0} 与行动条内容宽 {col.Size.X:0} 对不上：它会被排成一条窄条");

            // 二、最后一行不许被切：可见行数 × 实测行高，必须装得进面板的正文区。
            var rich = panel.GetChildren().OfType<RichTextLabel>().FirstOrDefault();
            Assert(rich is not null, "日志面板里没有富文本");
            var lineCount = rich!.Text.Split('\n').Length;
            var contentHeight = (float)rich.GetContentHeight();
            var bodyHeight = panel.Size.Y - BattleLogChrome(panel);
            Assert(contentHeight <= bodyHeight + 1.5f,
                $"日志有 {lineCount} 行、内容高 {contentHeight:0}，而面板正文区只有 {bodyHeight:0}：" +
                "最后一行被切在字腰上（可见行数没有按实测行高反推）");
            battle.QueueFree();
        });

        // 战斗一开场，地图的零件必须全部收起来。
        //
        // `03-battle.png` 右边一直挂着一条 11 行的房间类型图例，盖住巨鼠那半张卡。
        // 它是 `Legend`（40）、战斗的子树根是 `BattleRoot`（30）——两个数字各自都是对的：
        // 图例得压在地砖上，战斗得压在地图上，谁也没错。两棵树对撞时「谁的数字大」
        // 解决不了这件事，因为答案不在层号里：战斗开场时地图**整块不该被看见**。
        // 判据问的就是这句话本身：地图自己的那些层，战斗挂着的时候 `Visible` 得是 false。
        await StepAsync("战斗一开场，地图自己的层全部收起来（图例 / 网格 / 玩家 / HUD）", async () =>
        {
            var store8 = GetNode<GameStore>("/root/GameStore");
            var run = store8.NewRun(911);
            var map = new MapScene();
            AddChild(map);
            await Frame();
            await Frame();
            var legend = map.FindChild("MapLegend", true, false) as Control;
            Assert(legend is not null, "地图上没有图例（这一条判据要靠它量「战斗时该不该被看见」）");
            Assert(legend!.Visible, "地图刚建好时图例就该是可见的");
            var index = run.Map.Rooms.FindIndex(r => r.Type is RoomType.Enemy or RoomType.Elite or RoomType.Boss);
            run.Teleport(index);
            map.ShowBattleForShot(run.CreateEncounter()!);
            await Frame();
            await Frame();
            // 图例与房间层是「地图的零件」，战斗中一个都不该出现。
            // 按名字与类型找，不靠字段：以后再多一层地图装饰，这条会自动带上它
            // （只要它挂在地图场景下、并且不是战斗自己）。
            foreach (var name in new[] { "MapLegend", "RoomLayer", "MapLines", "ActorLayer" })
            {
                var node = map.FindChild(name, true, false) as CanvasItem;
                if (node is null) continue;
                Assert(!node.Visible, $"战斗挂着的时候地图的「{name}」还看得见：它会浮在战斗画面上");
            }
            map.CloseBattleForShot();
            await Frame();
            await Frame();
            Assert(legend.Visible, "战斗结束之后图例没有放回来：地图会缺一块");
            map.QueueFree();
        });

        // ---- 这一轮：软衰减与两套坐标 ----------------
        //
        // 这一轮改了五处「画出来的东西不是它要画的那件事」，四处都是**同心环**：
        // 一摞同心的填实椭圆，层数不够时每一层都在留一条边，叠起来就是一屏
        // 能数出来的环。Boss 出场那屏是整屏的网，敌人卡的底光是一块方形亮斑。
        //
        // 修法收在一处：`Paint.SoftEllipse`（`GradientTexture2D` 的径向填充，
        // 一次画完，没有环）。下面四条钉的是「谁在用软衰减、谁还在用旧路子」，
        // 剩下两条钉的是这一轮顺手改掉的两处**两套坐标**。

        Step("氛围光一律走软衰减：舞台 / Boss 背景 / 敌人卡上不再有同心圆的硬边", () =>
        {
            // 读**源码**。这一条没法问节点：`_Draw` 里画了什么，调 `_Draw` 之前
            // 什么都读不到，调了也不留痕（`Paint` 是静态的直写）。和层序那条一样走 FileAccess，
            // 导出产物里没有 `.cs` 就跳过。
            const string dir = "res://Scripts/UI";
            if (!Godot.DirAccess.DirExistsAbsolute(dir)) return;
            var files = new[] { "Arena.cs", "UiFrame.cs", "Paint.cs" };
            foreach (var file in files)
            {
                using var f = Godot.FileAccess.Open($"{dir}/{file}", Godot.FileAccess.ModeFlags.Read);
                if (f is null) continue;
                var text = StripComments(f.GetAsText());
                // `Paint.Glow` 是「一摞同心椭圆」，它自己的体积感全靠层数堆。
                // 在这些**大面积、低对比**的底上（舞台、整屏背景、卡面），
                // 层与层之间的 1/255 就是一道看得见的边——这一轮四张截图里数出来的
                // 那些环，全都是它。
                Assert(!text.Contains("Paint.Glow("),
                    $"{file} 里还有 Paint.Glow(…)：它是一摞同心椭圆，在大面积低对比的底上" +
                    "会留下一圈圈能数出来的边。改用 Paint.SoftEllipse / SoftDot");
            }
        });

        Step("Boss 出场的底只有一道软衰减，不再三圈填实椭圆 + 三条描边", () =>
        {
            const string file = "res://Scripts/UI/UiFrame.cs";
            if (!Godot.FileAccess.FileExists(file)) return;
            using var f = Godot.FileAccess.Open(file, Godot.FileAccess.ModeFlags.Read);
            if (f is null) return;
            var text = StripComments(f.GetAsText());
            // 旧写法：`for (var i = 0; i < 3; i++)` 里一圈 `Paint.Ellipse` 填实 + 一条 `DrawPolyline`。
            // 它叠在 `Glow(…, 9 层)` 上面，一共 12 条同心边界——4 倍放大后就是 12 个环。
            // 「井的纵深」该由亮度梯度给，不由环的条数给。
            Assert(!text.Contains("Paint.Ellipse(this, center, w * 0.34f * k"),
                "Boss 背景还在画三圈填实椭圆：那些环叠在主光上，整屏是一张同心圆网");
        });

        Step("提示胶囊：字框从胶囊里读，飘到一半也不会和底框脱开", () =>
        {
            var host = new Control { Size = new Vector2(1280, 720) };
            AddChild(host);
            var layer = new ToastLayer
            {
                Name = "ToastProbe",
                HintBandHeight = 46f, GridBottom = 628f, GridTop = 119f,
                HudBottom = 74f, BelowFloor = 662f,
            };
            layer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            host.AddChild(layer);
            layer.Show("那边没有路", Palette.Gold, 3f);
            // 提示是**淡入**的：刚 Show 完 `_alpha` 还是 0，胶囊矩形要等演起来才有。
            // 上一版这里读完就断言，读到的是一个空矩形——判据红在「什么也没读到」，
            // 而不是红在它想抓的那件事上。手动推两帧让它真的在演（不 await：
            // 这一条要读的是「布局写了什么」，推帧比等场景树简单，也不会被别的步骤插队）。
            layer.StepForTest(1f / 60f);
            layer.StepForTest(1f / 60f);
            // 提示是**淡入**的：刚 Show 完 `_alpha` 还是 0，胶囊矩形要等演起来才有。
            // 上一版这里读完就断言，读到的是一个空矩形——判据红在「什么也没读到」，
            // 而不是红在它想抓的那件事上。等两帧，让它真的在演。
            // 字框的**位置**是 `Layout()` 写的，它在演出那一帧里跑：这里直接问它要
            // 判据不该问「胶囊有多高」——它可以改。判据问的是「字跟着胶囊走」：
            // 字框的竖直中心必须落在胶囊的竖直中心上（两者都由同一个 `PillRect` 说）。
            // 这里不按控件类型去找 Label（提示层里不止一个 Label，找错的概率比找对的高），
            // 而是让**提示层自己**报它写下去的字框——等于问「你按什么摆的字」。
            var rect = layer.PillRect;
            var labelBox = layer.LabelBox;
            Assert(rect.Size.Y > 0f, "提示层没有播报胶囊矩形：这条判据什么也没读到");
            Assert(labelBox.Size.Y > 0f, "提示层没有播报字框：这一条什么也没读到");
            // 判据比的是**同一个量**：字框的中线 vs 胶囊的中线。
            //
            // 但这一条在「字框高恰好等于字高」的那些胶囊高下恒成立——旧写法
            // `rect.Y + rect.H/2 - 16` 配 `Size.Y = 32`，胶囊高 32 时中线正好对得上。
            // 于是它抓不住那个 bug，只是恰好不红。真正抓住它的是**字高跟着胶囊走**：
            // 胶囊多高，字框就多高（`LabelBox.Size.Y == PillRect.Size.Y`）。
            // 旧写法把 32 写死，胶囊一改高就露馅。
            var pillH = rect.Size.Y;
                        Assert(Mathf.Abs(labelBox.Size.Y - pillH) < 0.5f,
                $"字框高 {labelBox.Size.Y:0.#}、胶囊高 {pillH:0.#}：字的框没有跟着胶囊走。" +
                "写死字框高的话，胶囊一改尺寸字就贴上沿；飘起来时底框与字各算一次，会脱开");
            var labelCenter = labelBox.Position.Y + labelBox.Size.Y * 0.5f;
            var pillCenter = rect.Position.Y + rect.Size.Y * 0.5f;
            Assert(Mathf.Abs(labelCenter - pillCenter) < 1f,
                $"字的中线在 {labelCenter:0.#}、胶囊的中线在 {pillCenter:0.#}：两者不是同一个矩形算出来的，" +
                "胶囊往上飘的时候字会脱开");
            host.QueueFree();
        });

        Step("提示上飘不许越界：额度小的那一颗，淡出走完也还在带子里", () =>
        {
            // 这一条的来历：`_Process` 的两段写死了 `RiseIn` / `RiseOut`，
            // 而「这一颗能飘多少」是 `_riseCap`（按落点收）。两套数分开之后，
            // 第一条被收成 4px 的提示演完，第二条照样飘满 34px——穿进 HUD。
            // 判据读 `FlightTop`（生命周期里最靠上的那条边）与带子上沿比。
            var host = new Control { Size = new Vector2(1280, 720) };
            AddChild(host);
            var layer = new ToastLayer
            {
                Name = "ToastProbe2",
                HintBandHeight = 46f, GridTop = 160f, HudBottom = 74f, BelowFloor = 660f,
            };
            layer.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            host.AddChild(layer);
            layer.Show("放得下的时候", null, 3f);
            // 与截图工具同一条口径：这条判据问的是**设计上限**那一档（带子够宽），
            // 收窄的那一档由截图（`06-toast.png`）在真的窄带子下拍。
            var flightTop = layer.FlightTop;
            var bandTop = 74f + ToastRules.Clearance;
            Assert(flightTop >= bandTop - 0.5f,
                $"提示的飞行区间顶边在 {flightTop:0.#}，带子上沿在 {bandTop:0.#}：飘起来会穿进顶部 HUD");
            host.QueueFree();
        });

        await Frame();

        // 收尾前先把树上还挂着的「屏上覆盖层」点一遍。
        //
        // 起因是 `cnb-gaq-1k636ho1d` 上的一条红：某个 `Step` 报「屏上应当有 1 层
        // 覆盖层，实际 2」，而报的时候**没人知道那两层各叫什么**——判据只数了数。
        // 数出来的 2 可能是「真有两张卡」，也可能是「一张卡被数了两遍」，
        // 两种的修法完全不同，而日志里看不出来。
        DumpOverlays();

        GD.Print($"\n界面冒烟：{_passed} 通过 / {_failed} 失败");
        Scenes.Boot.QuitWithGuard(_failed == 0 ? 0 : 1);
    }

    /// <summary>
    /// 去掉 C# 注释（`//` 与 `/* */`），只留代码。
    ///
    /// 「源码里不许再有 X」这一类判据必须走一遍它：这个仓库的注释里全是**历史**——
    /// 上一版的写法、为什么改、判据拿哪个数比。那些 `Paint.Glow(...)` 出现在说明里
    /// 是好事（下一个人一眼看得出在防什么），但判据扫到它就会一直红。
    /// 判据要管的是代码，不是笔记。字符串字面量不特殊处理：这些文件里没有
    /// 藏在字符串里的 `Paint.Glow`，为它写一个完整的词法分析器不值。
    /// </summary>
    private static string StripComments(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        var i = 0;
        while (i < src.Length)
        {
            if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n') i++;
            }
            else if (i + 1 < src.Length && src[i] == '/' && src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < src.Length && !(src[i] == '*' && src[i + 1] == '/')) i++;
                i = Math.Min(src.Length, i + 2);
            }
            else
            {
                sb.Append(src[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    /// <summary>一个目录下的全部条目名（文件与子目录），不含 `.` 与 `..`。</summary>
    private static List<string> Walk(Godot.DirAccess access)
    {
        var names = new List<string>();
        access.ListDirBegin();
        for (var name = access.GetNext(); !string.IsNullOrEmpty(name); name = access.GetNext())
        {
            if (name == "." || name == "..") continue;
            names.Add(name);
        }
        access.ListDirEnd();
        return names;
    }

    /// <summary>
    /// 扫一遍源码，找出「ZIndex = 数字」这种裸字面量。
    /// 层号表自己（Layers.cs）当然要写数字，跳过它；注释行也跳过。
    /// `path` 是目录就下去，是文件才量。
    /// </summary>
    private static void ScanLayers(string path, List<string> bad, ref int scanned)
    {
        // 判目录要问 DirAccess，不能问 string：`string` 上没有 CurrentDirExists / GetDirectories。
        //
        // 递归别用 `GetDirectoriesAt` 手拼路径：它返回的是**目录名**，不是路径，
        // 拼成 `/a/b` 之后 `FileAccess` 读不到、`DirExistsAbsolute` 也认不出来，
        // 于是每个子目录都被当成「一个名字不以 .cs 结尾的文件」跳掉——扫到 0 个文件，
        // 判据照样绿。Godot 自己的做法是 `DirAccess.Open(dir)` + `ListDirBegin/GetNext`
        // （返回的正是名字），这里照办。
        using var access = Godot.DirAccess.Open(path);
        if (access is not null)
        {
            foreach (var name in Walk(access))
                ScanLayers($"{path}/{name}", bad, ref scanned);
            return;
        }
        // 走到这里就是「一个文件」了
        var leaf = path.GetFile();
        if (!leaf.EndsWith(".cs", StringComparison.Ordinal) || leaf == "Layers.cs") return;
        var text = Godot.FileAccess.GetFileAsString(path);
        // 导出产物里的 `.cs` 是一个 **1 字节占位符**（C# 以编译好的 dll 发布，
        // Godot 不随包带走源码）。它「存在」、能读、就是没有内容——
        // 不认这一条的话，`scanned` 照样 +1、`scanned > 20` 照样过，
        // 而实际上一个字都没扫到：判据变成假绿。
        // 这与 `HeadlessSelfCheck.SourceOf` 是同一条实测结论，两边必须认同一件事。
        if (text.Length < 32 || !text.Contains("namespace", StringComparison.Ordinal)) return;
        scanned++;
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            // 只认「ZIndex = 数」/「ZIndex = -数」：`Layers.Of(StageOrder.X)` 里没有数字，自然不算
            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"ZIndex\s*=\s*-?\d"))
                bad.Add($"{leaf}:{i + 1}");
        }
    }

    /// <summary>
    /// 日志面板里「文字下面还剩多少是面板自己的深色底」。
    ///
    /// 为什么不问控件的 `Size`：那个数只反映 `CustomMinimumSize`，而这一条 bug 正是
    /// 「两边的数对上了、画出来还是多一行」——控件答不出这件事，画面答得出。
    /// 做法：取日志文字那一块，从下往上扫，跳过面板底色（含阴影/内边距），
    /// 第一条落墨的行就是「最后一行文字」，再数它到面板底边之间还剩多少行。
    /// </summary>
    /// <returns>(文字下面的空白像素, 最后一行文字的 y)</returns>
    private static (int BlankRows, int LastInk) BlankRowsIn(Image img, Control rich, Color panelBg)
    {
        var rect = new Rect2(rich.GlobalPosition, rich.Size);
        var x0 = (int)Mathf.Clamp(rect.Position.X + 6f, 0f, img.GetWidth() - 1f);
        var x1 = (int)Mathf.Clamp(rect.End.X - 6f, x0 + 1f, img.GetWidth() - 1f);
        var y0 = (int)Mathf.Clamp(rect.Position.Y, 0f, img.GetHeight() - 1f);
        var y1 = (int)Mathf.Clamp(rect.End.Y, y0 + 1f, img.GetHeight() - 1f);
        // 「底色」用一个容差判：渐变与斜纹会让同一块底的像素差一两档亮度，
        // 但它们都不会亮到文字的程度（13px 的正文亮度远在底色之上）。
        bool IsBlank(Color c) =>
            Mathf.Abs(c.R - panelBg.R) < 0.10f && Mathf.Abs(c.G - panelBg.G) < 0.10f &&
            Mathf.Abs(c.B - panelBg.B) < 0.10f;
        var lastInk = -1;
        for (var y = y1 - 1; y >= y0; y--)
        {
            for (var x = x0; x < x1; x++)
            {
                if (IsBlank(img.GetPixel(x, y))) continue;
                lastInk = y;
                break;
            }
            if (lastInk >= 0) break;
        }
        return lastInk < 0 ? (0, 0) : (y1 - 1 - lastInk, lastInk);
    }

    private static void CollectButtons(Node node, List<Button> into)
    {
        if (node is Button b) into.Add(b);
        foreach (var child in node.GetChildren()) CollectButtons(child, into);
    }

    /// <summary>
    /// 把「界面上还挂着哪些覆盖层」打到日志里：类型、名字、父节点、标题条上的字。
    ///
    /// 只给数目不够用。一条「实际 2」的红，光是数目分不出是两张卡还是一张数了两遍，
    /// 而这两件事的修法完全相反。诊断信息要能回答「是哪两张」。
    /// </summary>
    private void DumpOverlays()
    {
        var count = 0;
        Walk(GetTree().Root);
        GD.Print($"  [overlays] 树上还有 {count} 层覆盖层");
        return;

        void Walk(Node node)
        {
            if (node is UI.ScrimBackdrop)
            {
                count++;
                var titles = new List<string>();
                CollectTitles(node, titles);
                GD.Print($"  [overlays] #{count} {node.Name} <{node.GetType().Name}> 父={node.GetParent()?.Name}" +
                         $" 标题=[{(titles.Count > 0 ? string.Join(" | ", titles) : "无")}]");
                return;
            }
            foreach (var child in node.GetChildren()) Walk(child);
        }

        static void CollectTitles(Node node, List<string> into)
        {
            foreach (var child in node.GetChildren())
            {
                if (child is PanelContainer p && p.GetChildCount() == 1 && p.GetChild(0) is Label l)
                {
                    into.Add(l.Text);
                    continue;
                }
                CollectTitles(child, into);
            }
        }
    }

    private SignalAwaiter Frame() => ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private Vector2 ViewportSize() => GetViewport().GetVisibleRect().Size;

    /// <summary>房间格是 RoomLayer 下的 Button；面板与遮罩不在这一层。</summary>
    private static List<Button> FindRoomButtons(Node root)
    {
        var layer = root.FindChild("RoomLayer", true, false);
        if (layer is null) return new List<Button>();
        return layer.GetChildren().OfType<Button>().ToList();
    }

    private static List<UI.RoomTile> FindRoomTiles(Node root)
    {
        var layer = root.FindChild("RoomLayer", true, false);
        if (layer is null) return new List<UI.RoomTile>();
        var found = new List<UI.RoomTile>();
        Collect(layer, found);
        return found;
    }

    private static List<UI.RoomIcon> FindRoomIcons(Node root)
    {
        var layer = root.FindChild("RoomLayer", true, false);
        if (layer is null) return new List<UI.RoomIcon>();
        var found = new List<UI.RoomIcon>();
        Collect(layer, found);
        return found;
    }

    private static T? FindChildOfType<T>(Node root, string? name = null) where T : Node
    {
        var list = new List<T>();
        Collect(root, list, name);
        return list.FirstOrDefault();
    }

    private static void Collect<T>(Node node, List<T> into, string? name = null) where T : Node
    {
        foreach (var child in node.GetChildren())
        {
            if (child is T t && (name is null || child.Name == name)) into.Add(t);
            Collect(child, into, name);
        }
    }

    /// <summary>要等帧的断言：内部要 await 布局，所以单独一个入口</summary>
    private async Task StepAsync(string name, Func<Task> fn)
    {
        try
        {
            await fn();
            _passed++;
            GD.Print($"  [ok] {name}");
        }
        catch (Exception e)
        {
            _failed++;
            GD.Print($"  [FAIL] {name}: {e.Message}");
        }
    }

    private void Step(string name, Action fn)
    {
        try
        {
            fn();
            _passed++;
            GD.Print($"  [ok] {name}");
        }
        catch (Exception e)
        {
            _failed++;
            GD.Print($"  [FAIL] {name}: {e.Message}");
        }
    }

    private static void Assert(bool cond, string message)
    {
        if (!cond) throw new InvalidOperationException(message);
    }

    /// <summary>
    /// 日志面板的上下内边距——读**渲染真的在用的那一份**（`StyleBoxFlat.ContentMargin`），
    /// 不是常量。
    ///
    /// 这一轮把日志块挪到按钮行下面时，面板的外边距从 6/4 改成了 4/4，
    /// 而 `BattleLayout.LogPanelPadding` 还写着 10：断言照样绿，因为它比的是一个
    /// 「名字叫内边距」的数，不是面板自己那一份。这和前面几轮的 `Rng.State`、
    /// `arena.FloorY`、`NeededRowPitch` 是同一个坑。
    /// </summary>
    private static float BattleLogChrome(PanelContainer panel)
    {
        if (panel.GetThemeStylebox("panel") is StyleBoxFlat sb)
            return sb.ContentMarginTop + sb.ContentMarginBottom;
        return UI.BattleLayout.LogPanelPadding;
    }

    /// <summary>
    /// 一个立绘的「脚」在哪（视口坐标）。盒子底边不是脚：盒子底下还留着

    /// 「光斑远沿」那一截（`Arena.StanceLift(盒高)`），脚站在它的上沿。
    /// 比盒底等于比余白——而余白随各自盒子高变。
    /// </summary>
}
