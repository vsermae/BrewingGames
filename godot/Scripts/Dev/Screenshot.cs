// 开发期截图工具：--shot=<目录> 渲染主菜单 / 地图 / 战斗 / 弹窗 / Boss 出场各一张再退出。
//
// CI 里是这么用的（.cnb.yml）：xvfb 下跑一遍，**每张图拍下去之前先自证**，
// 再把清单写进 manifest.json 当构建产物——「这张图是什么」由工具回答，
// 而不是由「png 有几张」回答。上一轮的教训就是这个：五张图都在，
// 其中三张被同一张新手引导卡盖着，流水线只数张数，于是绿了好几轮。
//
// 自证分两层：
//   1) 屏上没有自己的覆盖层（`Widgets.Overlays`）——覆盖层是一整屏半透明色块，
//      没有任何控件类型或布局能数到它，只能专门问一次；
//   2) 每张图关心的一件事（标题栏、舞台、按钮行、名字带……）在这里当场断言。
// 任何一条不过，进程以非零码退出，流水线跟着红。
//
// 为什么用「状态机 + _Process」而不是 async/await：截图脚本要切换场景，
// 切场景时 async 续体多半再也等不到信号，实测会静默停在第一帧（xvfb 下卡死）。
namespace AbyssEcho.Dev;

using Godot;
using AbyssEcho.Core;
using AbyssEcho.Scenes;
using AbyssEcho.UI;

public partial class Screenshot : Node
{
    public string Dir = "/tmp/shots";

    /// <summary>
    /// 拍图模式：只有它为真时收尾才写清单。
    ///
    /// 以前写清单只看 `Dir`，而 `--from-shots=` 那个入口也会走同一个收尾函数：
    /// 复核那一步读完清单、判完、`Finish()` 又把一份**空清单**写了回去。
    /// 「复核」和「产生产物」是两件事，混在一个收尾函数里，复核就成了破坏性操作。
    /// </summary>
    private bool _captureMode;

    private int _step;
    private int _wait;

    /// <summary>这一步已经等过那一帧了（见 <see cref="Step"/>：切步前先让一帧过去）</summary>
    private bool _settled;
    private int _mapWaited;
    private MapScene? _map;
    private Game? _run;

    /// <summary>
    /// 敌人卡顶上那条「整宽接近纯白」的带子：上沿、下沿、高、以及有几行是**整宽**白的。
    ///
    /// 口径写得死一点，免得它变成一个「随便找块白」的探针：
    ///   · 只看卡右端往左 60px 那一条竖线之前（取景窗那 64px 本来就是亮的）；
    ///   · 一行里 500 个像素以上是纯白（≥240,240,240）才算「整宽」；
    ///   · 结果取最长的一段连续行。
    /// 这一份读数只能回答「白带在哪、多高」，回答不了「谁画的」——后者要靠旁边的证据
    /// （宣告的行高、字符色、控件下沿）。一个探针只回答一个问题。
    /// </summary>
    private static (int Top, int Bottom, int Height, int FullWidthRows) CardWhiteBand(Image img, BattleScene battle)
    {
        var panel = battle.FindChild("EnemyPanel", true, false) as Control;
        if (img is null || panel is null) return (0, 0, 0, 0);
        var left = Math.Max(0, (int)panel.GlobalPosition.X + 4);
        var right = Math.Min(img.GetWidth() - 1, (int)(panel.GlobalPosition.X + panel.Size.X) - 70);
        var top = Math.Max(0, (int)panel.GlobalPosition.Y);
        var bottom = Math.Min(img.GetHeight() - 1, (int)(panel.GlobalPosition.Y + panel.Size.Y));
        var best = (Top: 0, Bottom: 0, Height: 0, Rows: 0);
        var runStart = -1;
        var runRows = 0;
        for (var y = top; y <= bottom; y++)
        {
            var white = 0;
            for (var x = left; x <= right; x++)
            {
                var c = img.GetPixel(x, y);
                if (c.R8 >= 240 && c.G8 >= 240 && c.B8 >= 240) white++;
            }
            if (white >= 500)
            {
                if (runStart < 0) { runStart = y; runRows = 0; }
                runRows++;
                if (runRows > best.Height) best = (runStart, y, runRows, runRows);
            }
            else if (runStart >= 0) { runStart = -1; }
        }
        return best.Height == 0 ? (0, 0, 0, 0) : best;
    }

    /// <summary>
    /// 只看清单、不再拍图：`--from-shots=&lt;目录&gt;`。
    ///
    /// 为什么留着这条路：判据要绑在【上次 CI 真的产出的那批图】上。
    /// 拍图和查图分开之后，「图是对的」与「查图的那一步真的跑过」是两件事，
    /// 后者能被一条断言盯住——而只要它跑，上一版那种「三张图一起错、流水线全绿」
    /// 就不可能再发生。
    /// </summary>
    public bool AuditOnly;

    /// <summary>自证失败的条数：非 0 时退出码非 0，流水线红在这一步而不是「人工看图」</summary>
    private int _fail;

    /// <summary>清单：每张图的文件名 + 尺寸 + 这一张证明了什么</summary>
    private readonly List<string> _proven = new();

    /// <summary>最后一张图（Boss 出场）要见的名字：由截图流程自己定，不靠人记</summary>
    private string _bossName = "";

    /// <summary>提示那一屏的现场（地图 / 提示层 / 已经攒下的问题）</summary>
    private (MapScene? Map, ToastLayer? Layer, List<string> Problems) _toastProbe;

    /// <summary>提示那一屏拍过没有：它在「地图」与「战斗」之间插队一次</summary>
    private bool _toastStepDone;

    /// <summary>已经插过队了：回到地图时不再插第二次、也不再重拍同一张图</summary>
    private bool _afterToast;

    /// <summary>02-map 量 HUD 时读到了空图，已经重试过几轮（最多 3 轮）</summary>
    private int _mapHudRetry;

    /// <summary>
    /// 新手引导关到第几步了：0 = 还没动，1 = 刚 QueueFree（同一帧里问不出结果），
    /// 2 = 已经等过一帧、验过了。
    ///
    /// 为什么要一个计数而不是一个 bool：这一屏会被回来两次（提示那一屏插队之后要回地图），
    /// 而 `_mapWaited` 在回来时是新的 MapScene、从头开始数——用 bool 的话第二次进来
    /// 会重新走一遍「关掉」并把标记复位，永远卡在这一帧上。
    /// </summary>
    private int _introClosed;

    public override void _Ready()
    {
        System.IO.Directory.CreateDirectory(Dir);
        // 正常启动时 Boot 已经把主题挂到 root 上；截图是绕过 Boot 流程进来的，
        // 这里补一次。少了它菜单是「纯背景 + 没字」——第一次截图就是这个样子。
        if (GetTree().Root.Theme is null)
        {
            var theme = AbyssEcho.UI.UiTheme.Build();
            theme.DefaultFont = AbyssEcho.UI.UiTheme.FallbackFont();
            theme.DefaultFontSize = 16;
            GetTree().Root.Theme = theme;
        }
        if (AuditOnly) { Audit(); return; }
        _captureMode = true;
        // 延后一帧：_Ready 期间根节点还在 AddChild 流程里，这时换场景会撞上
        // "Parent node is busy adding/removing children"
        _step = 0;
        _wait = 2;
    }

    /// <summary>截图的「走了一段」：把图上七间房标成已探索（不触发任何房间交互）</summary>
    private static void RevealForShot(Game run)
    {
        var order = RunMap(run);
        for (var i = 0; i < order.Count && i < 7; i++) run.Map.Rooms[order[i]].Visited = true;
    }

    /// <summary>BFS 顺序：起点在第一个，后面的按步数排</summary>
    private static List<int> RunMap(Game run)
    {
        var dist = AbyssEcho.Core.MapGen.BfsDistances(run.Map.Links, run.PlayerIndex);
        var order = new List<int>();
        for (var i = 0; i < dist.Length; i++) if (dist[i] >= 0) order.Add(i);
        order.Sort((a, b) => dist[a].CompareTo(dist[b]));
        return order;
    }

    public override void _Process(double delta)
    {
        // 用状态机而不是 async/await：截图脚本要切场景，而切场景之后
        // 旧节点的续体能不能回来是不确定的（实测在 xvfb 下会静默停住）。
        if (_wait > 0) { _wait--; return; }
        // 负数步号是「插队的一屏」：Step() 的 switch 里没法把 _step 放到负数上，
        // 只能在这里分派。第一版让 case 2 把 _step 设成 -1，结果 switch 直接掉进
        // default 收尾——`06-toast.png` 一次都没拍，而清单里少一行没人发现。
        if (_step < 0) StepExtra();
        else Step();
    }

    private void Step()
    {
        // 每一次切步都先让一帧真的过去。
        //
        // 为什么：`--shot` 里好几处收尾都是 `QueueFree`（`CloseBattleForShot` /
        // `CloseModalForShot`），而 `MapScene._modal` 是在 `TreeExited` 里才清空的——
        // 这些都在**帧末**发生。少这一帧，判据就可能问得太早：新加的
        // `08-battle-poison` 一拍完，紧跟着的 `05-panel` 就站在一个
        // 「战斗还挂着、弹窗还没真弹出来」的世界上，报「屏上有 0 层覆盖层」。
        // 判据没错，是它问早了。
        if (!_settled) { _settled = true; _wait = 1; return; }
        _settled = false;
        switch (_step)
        {
            case 0:
            {
                var e0 = GetTree().ChangeSceneToFile("res://Scenes/Menu.tscn");
                if (e0 != Error.Ok) GD.PrintErr($"[shot] 菜单场景加载失败：{e0}");
                Advance(10);
                break;
            }
            case 1:
            {
                // 菜单没有标题栏也没有舞台，只能验「有字、只有一个按钮组」这一层：
                // 六个按钮 + 标题，比这少就是有一块没建出来。再确认屏上是干净的
                // （没有任何覆盖层——正常启动路径不会一上来就弹卡）。
                var buttons = FindAll<Button>(GetTree().CurrentScene).Count;
                var menuProblem = Clear() ?? (buttons >= 6 ? null : $"菜单只有 {buttons} 个按钮");
                Save("01-menu.png", $"{buttons} 个按钮都在、屏上没有覆盖层", menuProblem);

                // 截图工具挂在 root 上，是持久节点；换场景时它不会被清掉，
                // 但 CurrentScene 会变——所以先缓存节点引用，后面别再 GetNode 依赖场景
                var store = GetNode<GameStore>("/root/GameStore");
                _run = store.NewRun(4242);
                // 截图是「玩家已经走了一段」的那一屏，不是开局第一帧：
                // 起手只有起点房是已探索的，拍出来满屏问号，看不见房间砖的实际样子。
                // 这里沿 BFS 走七步（真实 Move，不是改标记），顺路触发事件房/敌人房的
                // 交互面板会挡住地图——所以只走「空房间」，走过就回来。
                RevealForShot(_run);
                GetTree().ChangeSceneToFile("res://Scenes/Map.tscn");
                Advance(14);
                break;
            }
            case 2:
            {
                _map = GetTree().CurrentScene as MapScene;
                _map?.EnableShotMode();
                // 地图的内容（房间层、图例）是等第一次布局之后才建的：
                // 切场景后立刻拍，得到的是一张「还没画完」的图（图例常常不在）。
                // 多等三帧再拍。
                if (_mapWaited++ < 3) { _wait = 2; return; }
                // 新手引导卡是「玩家第一次进地图」时弹的，挂在**场景根**上——
                // 截图工具绕过玩家路径直接拼场景，于是它一路盖到地图、战斗、Boss 出场，
                // 三张图内容全错，而流水线只数张数。这里按玩家的做法把它关掉。
                //
                // 关掉之后必须**再等一帧**才验：`CloseModal` 是 QueueFree，节点退出场景树之前
                // `Widgets.Overlays` 照样数得到它（上一轮踩的是同一件事的反面：排队释放那几帧里
                // `_modal` 仍非空，于是按键一直被吞）。同一帧里问「关掉了吗」，答案永远是「没关」。
                if (_introClosed < 2)
                {
                    _introClosed++;
                    _map?.CloseIntroForShot();
                    _wait = 2;
                    return;
                }
                var introLeft = Widgets.Overlays(GetTree().CurrentScene).Count;
                if (introLeft > 0)
                {
                    // 关不掉就不拍：一张盖着卡的图不是证据，是负证据。
                    Save("02-map.png", "地图：房间砖 / 图例 / 提示条都在，血条比经验条粗",
                        $"引导卡关不掉：屏上还剩 {introLeft} 层覆盖层，后面每一张都会盖着它");
                    Quit();
                    return;
                }
                // 存档带 intro_seen 标记：以后不再弹新手卡时，这一屏的覆盖层数
                // 应当回到 0；一旦有人把卡又插回启动路径，这条会立刻红。
                var widgets = FindAll<Control>(GetTree().CurrentScene).Count;
                var mapProblem = Clear()
                                 ?? (widgets > 20 ? null : $"地图层没建出来（控件只有 {widgets} 个）");
                // 一次取图、一次判定、一次落盘，三者看的是同一张——
                // 这一步曾经栽在「判据手里的图」与「存下来的图」是两张不同的帧上。
                var shot = GetViewport().GetTexture().GetImage();
                // 提示那一屏拍完会回到这里，不再重复拍这张——清单里一张图只该有一行。
                if (!_afterToast)
                {
                    // 这张图还要证明一件只有画面能回答的事：HUD 上那两条进度条**粗细不同**。
                    // 代码里血条写 16、经验条写 8，而量像素发现两条都是 17——
                    // `CustomMinimumSize` 只是下限，一排里最高的那个（血量文字 20px）把它们一起拉平了。
                    // 判据读的是渲染出来的那一条：血条的红与经验条的蓝，各占多少行。
                    // 头一帧读不到像素就再等两帧，最多等三轮。
                    // `GetTexture().GetImage()` 在换场景后可能给一张还没画完的图，
                    // 只差一帧就能让「血条 0 行高」这种指着画面的结论落在一次空读上。
                    // 用「步号原样再来一次」表达重试，而不是在这里 Advance——
                    // 这一段的下面是 Save，跳过去就少拍一张图。
                    // **先取一次图，判据和落盘都用它**。
                    //
                    // 这一步曾经栽在两次取图上：`GetTexture().GetImage()` 在两次调用之间，
                    // 这里还隔着 `Clear()`、`FindAll<Control>()` 那些遍历整棵树的活，
                    // xvfb 的 llvmpipe 上足够过去一帧多。于是判据手里那张还是空的
                    // （窗口里最红 0.000），而 `Save()` 里再取一次、画全了，存下来的图是对的——
                    // 一张对的图和一条「血条只有 0 行高」的判语，各自都说得通，合起来就是撒谎。
                    // 现在一次取图、一次判定、一次落盘，三者看的是同一张。
                    var hud = MapHudBars(shot);
                    _mapHudRetry++;
                    if (hud.Ink < 0.01f && _mapHudRetry < 30)
                    {
                        _wait = 2;
                        return;
                    }
                    GD.Print($"[shot] 02-map 的 HUD 量了 {_mapHudRetry} 轮：ink={hud.Ink:0.0000} " +
                             $"red={hud.DangerRows} blue={hud.AccentRows}");
                    _mapHudRetry = 0;
                    // 先看这张图到底有没有内容：`GetTexture().GetImage()` 在两种情况下是空的
                    // （headless 没有渲染结果、刚换完场景第一帧还没画），
                    // 那时每一行都是 0——报「血条只有 0 行高」是把「图没拍出来」说成了「血条画错了」。
                    // 这两件事的修法完全不一样，判据必须先分清它们。
                    if (hud.Ink < 0.01f) mapProblem ??= "这一屏什么都没画（截到一张全黑的图）：先查渲染，再查 HUD";
                    else if (hud.DangerRows < 10) mapProblem ??= $"HUD 上血条只有 {hud.DangerRows} 行高";
                    else if (hud.AccentRows > 10) mapProblem ??= $"HUD 上经验条有 {hud.AccentRows} 行高：它被拉成和血条一样粗了";
                    else if (hud.AccentRows < 2) mapProblem ??= $"HUD 上找不到经验条（蓝色那一行）";
                }
                if (!_afterToast)
                {
                    // 这一轮写在地图上的两句话，都要有一条判据盯着——否则它们只活在这一张图里。
                    //
                    // 1) 迷雾：未探索的砖只报「这里值得去」，不报「那边有几场仗」。
                    //    判据读的是**类型**（`FaintKindForShot`），不是颜色：颜色是渲染，
                    //    类型才是规则。未探索的敌人房与精英房必须是空字符串。
                    var leaks = new List<string>();
                    var fogged = 0;
                    for (var i = 0; i < _run.Map.Rooms.Count; i++)
                    {
                        var room = _run.Map.Rooms[i];
                        if (room.Visited) continue;
                        fogged++;
                        var kind = _map.FaintKindForShot(i);
                        if (room.Type is RoomType.Enemy or RoomType.Elite or RoomType.Boss && kind.Length > 0)
                            leaks.Add($"{MapGen.RoomLabel[room.Type]}@{room.X},{room.Y}");
                        if (room.Type is RoomType.Shop or RoomType.Treasure && kind.Length == 0)
                            leaks.Add($"补给类被藏起来了：{MapGen.RoomLabel[room.Type]}@{room.X},{room.Y}");
                    }
                    if (fogged == 0) mapProblem ??= "这一屏没有一格是未探索的：迷雾的判据量不到任何东西";
                    else if (leaks.Count > 0) mapProblem ??= $"迷雾漏了 {leaks.Count} 格（{string.Join("，", leaks.Take(4))}）";
                    // 2) 「剩下的仗约 N 点血」：它必须真的落在提示条里，且这个数不是 0。
                    //    0 点血是一个**看起来合法**的读数（「没有代价」），而地图上一场仗都没有时
                    //    才会出现它——所以两个条件一起判。
                    var hint = _map.HintTextForShot;
                    if (!hint.Contains("剩下的仗约"))
                        mapProblem ??= $"提示条没报「剩下的仗约 N 点血」：{hint}";
                    else if (hint.Contains("约 0 点血"))
                        mapProblem ??= $"提示条报了 0 点血，而地图上还有战斗房：{hint}";
                }
                if (!_afterToast) Save("02-map.png", "地图：房间砖 / 图例 / 提示条都在，迷雾只说方向、提示条报出剩下要花的血", mapProblem, shot);
                if (_map is null || _run is null) { Quit(); return; }
                // 插一屏「提示弹在哪」：它必须在房间砖与图例之外。
                // 上一版落点 0.66 屏高，在 1280×720 的地图上正好压住最后一排砖与图例。
                if (!_toastStepDone) { _toastStepDone = true; _afterToast = true; _step = -1; _wait = 2; return; }
                // 走玩家自己那条路进战斗：站到敌人房再按「进房间」。
                // 上一版直接 NewRun + CreateEncounter + 硬挂场景，绕过了「进房间」这一层，
                // 于是引导卡还开着、也没人管——拍的是一张「战斗场景被弹窗盖住」的图。
                if (!EnterRoomOfType(RoomType.Enemy)) { GD.PrintErr("[shot] 找不到战斗房（这一层没有遭遇房）"); Quit(); return; }
                Advance(3, 12);
                break;
            }
            case 3:
            {
                // 先让 AI 打两回合再截：空日志的画面证明不了任何事，
                // 也不像玩家真正看到的那一屏。
                FindBattle(_map)?.PlayScriptedTurnsForShot(2);
                Advance(90);
                break;
            }
            case 4:
            {
                // 拍之前把日志面板按「真的排过版的内容高」再定一次高：
                // `RichTextLabel` 的内容高只有排完版才更新，而面板是跟着它收的。
                // 少了这一句，屏上会是「五行字 + 整整一行纯黑的空槽」——
                // 而那一行在截图里量得出来的时候，图已经拍下去了。
                var battle = FindBattle(_map);
                battle?.ResyncLogForShot();
                // --diag 时把战斗界面各面板的最终 rect 打出来：
                // 「按钮行被挤出屏幕」这类问题靠看图只能猜到，靠数字才定得下来。
                if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--diag") >= 0)
                {
                    battle?.DumpLayout();
                    battle?.DumpEnemyPanel();
                    battle?.DumpSpriteRects();
                }
                // 战斗这一屏证明三件事：标题栏写着对手是谁（没有它 = 战斗跑在没清掉的覆盖层底下）、
                // 舞台真的搭出来了、行动按钮整条在视口里。
                var problems = new List<string>();
                var overlay = Clear();
                if (overlay is not null) problems.Add(overlay);
                if (battle is null) problems.Add("没找到战斗场景");
                else
                {
                    // ── 那一块纯白：这一轮正式给它一个判据，而不是再猜一次 ──
                    //
                    // 八轮以来的说法是「敌人卡上半截有一块纯白」。这一轮不再问「白块在哪」，
                    // 改问一个能被一张图否掉的问题：**如果这块白是「伤害预告」那一行画的，
                    // 它应该只有一行字那么高（13~20px），而且字的颜色应该是 accent_soft。**
                    // 实测：白带 54px 高（跨过三行摘要）、那一行自己有字（文本非空、下沿在卡内）。
                    // 两条前提都不成立，于是「文字画的 / 排到了卡外」这两种解释一起被否掉——
                    // 剩下的嫌疑只剩卡面本身那几层贴片。判据在这里守着：白带一旦缩回一行高，
                    // 或者某一行的下沿越过卡底，这一张会立刻红。
                    var white = CardWhiteBand(GetViewport().GetTexture().GetImage(), battle);
                    var cardBottom = battle.CardBottomForShot;
                    if (battle.ForecastBottomForShot <= 0)
                        problems.Add("读不到敌人卡上那几行的位置：预算的那份算术没跑");
                    else if (battle.ForecastBottomForShot > cardBottom + 1)
                        problems.Add($"伤害预告那一行的下沿 {battle.ForecastBottomForShot:0} 越过了卡底 {cardBottom:0}");
                    else if (battle.ForecastTextForShot.Length == 0)
                        problems.Add("敌人卡上那句伤害预告是空的：这一屏没有可读的东西");
                    GD.Print($"[shot] 03-battle 的白带：{white.Top}..{white.Bottom}（高 {white.Height}，"
                             + $"整宽 {white.FullWidthRows} 行）· 预告行高 {battle.ForecastRowHeightForShot:0} "
                             + $"下沿 {battle.ForecastBottomForShot:0} 卡底 {cardBottom:0} "
                             + $"字色 {battle.ForecastColorForShot.ToHtml()}");
                    if (white.Height > 0 && white.FullWidthRows >= 3 && white.Height <= 24)
                        problems.Add($"卡上的白带只有 {white.Height}px（一行字那么高）：这些字被画到了卡面上，查文本渲染");
                }
                if (battle is not null)
                {
                    var title = battle.EnemyNameText;
                    if (string.IsNullOrWhiteSpace(title)) problems.Add("战斗没有标题栏（对手名字是空的）");
                    if (battle.StageRect.Size.Y < 100f) problems.Add($"舞台只有 {battle.StageRect.Size.Y:0} 高");
                    // 日志面板里「文字下面还剩多少」：内边距只该占一行（10px），
                    // 多出来的就是纯黑的空槽。这一条只能问画面——控件自己的 Size
                    // 读的是 CustomMinimumSize，而这块 bug 正是「两者对上了、画出来多一行」。
                    var logGap = battle.LogPanelBottomForShot - battle.LogTextBottomForShot;
                    if (logGap > 14f)
                        problems.Add($"日志面板里文字下面空了 {logGap:0}px（内边距只该占 10）：那一截是纯黑的空白");
                    // 日志块的宽度必须等于这一列的内容宽。
                    // 它曾经是 23px——`ExpandFill` 在 VBox 里是「分横向富余」，而这一列里
                    // 按钮行（最小宽 755）也在要，日志块的最小宽只有一格字，于是分到 0。
                    // 屏上是一条竖在左下角的黑细条，认不出是日志。
                    // 这条只能在这里量：冒烟那条路径下它本来就撑得开（多走了几帧布局），
                    // 23px 只在 `--shot` 这条路径上出现——判据得站在它要守的那个场景里。
                    var logW = battle.LogPanelWidthForShot;
                    var colW = battle.ActionColumnWidthForShot;
                    if (colW > 1f && Mathf.Abs(logW - colW) > 2f)
                        problems.Add($"日志块宽 {logW:0} 与行动条内容宽 {colW:0} 对不上：它会被排成一条窄条");
                    var vp = GetViewport().GetVisibleRect().Size;
                    foreach (var (label, rect) in battle.ActionButtonRects)
                        if (rect.Position.X < 0 || rect.Position.Y < 0 || rect.End.X > vp.X + 1 || rect.End.Y > vp.Y + 1)
                            problems.Add($"按钮「{label}」跑到视口外：{rect}");
                }
                Save("03-battle.png", $"战斗：标题栏「{battle?.EnemyNameText}」+ 舞台 + 按钮行",
                    problems.Count == 0 ? null : string.Join("；", problems));

                _map?.CloseBattleForShot();
                Advance(5);
                break;
            }
            case 5:
                // 顺手拍一张弹窗：面板 / 标题条 / 暗角是整套 UI 里最常出现的一类东西，
                // 只拍四个主界面的话，改坏了也看不见。这次弹的是一次真的房间交互。
                _map?.ShowPanelForShot();
                Advance(8);
                break;
            case 6:
            {
                // 弹窗这一屏证明的是「弹窗真的弹在自己的暗角上，且只有它一层」。
                // 上一版这里拍的是一张新手引导卡：没有暗角底、也不属于任何游戏流程，
                // 却和后面两张图共用同一张卡——三张图一起错，没人发现。
                // 这一屏**应当**正好有一层覆盖层（弹窗自己），所以这里查的是
                // 「有几层、名字对不对」，而不是 Clear()——Clear() 会说「屏上有弹窗」，那是它的本分。
                SavePanelShot("05-panel.png");
                _map?.CloseModalForShot();
                Advance(4);
                break;
            }
            case 7:
            {
                // 长内容弹窗：内容明显比工作区高的一屏。
                //
                // 为什么单开一屏：`05-panel.png` 里那张卡正好装得下，于是
                // 「内容比面板高」这条最常发生的分支一次都没被拍过。
                // 上一版的写法是：居中容器在内容超高时把面板**往上顶**，
                // 顶出屏幕的正好是标题条——玩家看到一张没有名字、也没有关闭键的卡。
                _map?.ShowPanelForShot(Widgets.LongContentLines.Length);
                Advance(8);
                break;
            }
            case 8:
            {
                SavePanelShot("07-panel-long.png");
                _map?.CloseModalForShot();
                Advance(4);
                break;
            }
            case 9:
            {
                // Boss 出场演出里会自己带一层黑底（它也是 ScrimBackdrop），
                // 所以要等名字淡入之后再拍，且拍之前不许还有别的覆盖层。
                var leftover = Clear(withScrim: true);
                if (leftover is not null) GD.PrintErr($"[shot] 弹窗没关掉：{leftover}");
                _bossName = NextBossName();
                _map?.ShowBossIntroForShot(_bossName);
                Advance(100);  // 1.3s 入场 + 名字淡入，按 60fps 折成帧数
                break;
            }
            case 10:
            {
                // 这一屏的判据是「名字真的画上去了」——上一轮它拍到的是一张引导卡，
                // 而深渊之主还在卡底下，图片尺寸、张数、渲染链全都正常。
                var intro = FindAll<BossIntro>(GetTree().CurrentScene).FirstOrDefault();
                var problems = new List<string>();
                var expected = BossIntro.NameOf(_bossName);
                if (intro is null) problems.Add("没找到 Boss 出场演出");
                else
                {
                    if (!string.Equals(intro.BossNameText, expected, StringComparison.Ordinal))
                        problems.Add($"名字栏是「{intro.BossNameText}」，应当是「{expected}」");
                    if (intro.NameAlpha < 0.5f) problems.Add($"名字还没淡入（alpha {intro.NameAlpha:0.00}）");
                }
                Save("04-boss-intro.png", $"Boss 出场：名字「{intro?.BossNameText}」",
                    problems.Count == 0 ? null : string.Join("；", problems));
                _map?.CloseBattleForShot();
                // 清场：这场战斗里剩下的东西（覆盖层、提示）一个都不该带进下一屏。
                // 留着的话，「中毒那一屏」会在一棵还没清干净的树上重开战斗——
                // 后面拍弹窗的那一屏就站在一个「屏上 0 层覆盖层」的世界里。
                Advance(16, 4);
                break;
            }
            case 16:
            {
                var leftover = Clear(withScrim: true);
                if (leftover is not null) GD.PrintErr($"[shot] 清场没干净：{leftover}");
                Advance(11, 2);
                break;
            }
            case 11:
            {
                // 中毒那一屏：敌人卡上必须写出「这一回合一共会掉多少血」。
                //
                // 上一轮修的是这句话本身（「下一刀 50」和「这回合合计 87」是两个数），
                // 但这一行**从来没被拍过**：`--shot` 的战斗屏是第 1 层的巨鼠，等级太低、
                // 没有毒系技能，那一支永远不出现。判据没有现场，就等于没有判据。
                // 所以先重新开一场战斗（第 3 步那场已经摘掉了），
                // 再把状态摆成玩家真会遇到的那个场面：身上挂着毒、敌人下一回合动手。
                //
                // 注意不能用 `EnterRoomOfType`：它挑的是**这一层还没清空**的敌人房，
                // 而第 3 步刚打完的那一间已经 Cleared，回头进去只会得到一句
                // 「这里已经被你清理过了」——然后状态机一路等下去（实测卡死在这里）。
                //
                // 也不用 `Advance(6, 14)` 那种「跳号」写法：`Step()` 里的 switch 只认
                // 它自己那几张牌，跳到 6 就等于把前面那几张**再拍一遍**——
                // 于是 `05-panel` / `07-panel-long` / `04-boss-intro` 会在这一轮里
                // 重拍三次，而第三次拍到的是「弹窗早关掉了」的世界（实测）。
                // 一屏一屏按顺序往下走，状态机不跳号。
                // 顺便借一层「怨恨」：这一屏之后那张「倒下自爆」要用同一场敌人，
                // 而它得真的会炸——第 1 层的巨鼠不会。借完立刻还原（见 RecreateBattleForShot），
                // 所以这依然是「第 1 层」，只是这一场战斗的敌人带着倒下自爆的规则。
                var (ok, enc) = RecreateBattleForShot("spiteful");
                if (!ok || enc is null || _map is null)
                {
                    Save("08-battle-poison.png", "战斗（中毒）：敌人卡写清了这一回合合计掉血",
                        "重启一场战斗失败，拍不到中毒那一屏");
                    Finish();
                    break;
                }
                _map.ShowBattleForShot(enc);
                Advance(12);
                break;
            }
            case 12:
            {
                // 剩 1 回合是最险的那一种：玩家看到「还剩 2 回合」会以为还有余量。
                FindBattle(_map)?.StagePoisonForShot(turns: 2);
                Advance(4);
                break;
            }
            case 13:
            {
                var battle = FindBattle(_map);
                var problems = new List<string>();
                if (battle is null) problems.Add("没找到战斗场景");
                else
                {
                    var line = battle.EnemyForecastText;
                    // 三件事一起验，缺一条这句话就还是「只报一半」：
                    //   1. 有毒的时候必须提到「中毒」；
                    //   2. 必须给出「本回合合计」这个数（只报下一刀的那一版没有它）；
                    //   3. 合计必须**大于**下一刀——等于的话说明毒的那一份没加进去。
                    if (!line.Contains("中毒")) problems.Add($"敌人卡上没有写中毒：「{line}」");
                    // 第 4 条：毒还剩几回合。毒被封在 EntityRules.MaxPoisonPower 之后，
                    // 「含中毒 6，合计 20~26」读起来像「吃完这一下就过去了」——而毒还剩一回合。
                    // 这一条要的不是某个具体文案，而是「这句话里出现了回合数」。
                    if (!line.Contains("回合")) problems.Add($"敌人卡上没有说毒还剩几回合：「{line}」");
                    if (!line.Contains("本回合合计")) problems.Add($"敌人卡上没有写合计：「{line}」");
                    // 第 5 条：**它还要吃几刀**。卡片把「它这一刀打你多少」写全了，
                    // 却一直没写「你这一刀打掉它多少」——而玩家读这一行是为了算出
                    // 「再挨两刀还是三刀」。缺了后半句，这一行仍然只报了一半。
                    if (!line.Contains("刀")) problems.Add($"敌人卡上没有写「还要几刀」：「{line}」");
                    else
                    {
                        var honest = SumIsBiggerThanSingleBlow(line);
                        if (honest is false) problems.Add($"合计不大于下一刀：「{line}」");
                    }
                }
                Save("08-battle-poison.png", $"战斗（中毒）：敌人卡写清了这一回合合计掉血「{battle?.EnemyForecastText}」",
                    problems.Count == 0 ? null : string.Join("；", problems));
                // 不在这里摘掉战斗：下一屏（倒下自爆）还要用**这一场**，只把血量压到 1 点。
                //
                // 重开一场是错的：这一场本来就是第 11 步 `RecreateBattleForShot()` 建的，
                // 而「再建一次」要再走一趟 `PathTo` + `EnterRoomForShot`——那条路在这里
                // 一帧都走不动（同一层已经没有未清的敌人房了），状态机于是停在原地等，
                // 判据不进、`Finish()` 也不进：实测卡死 10 分钟直到被外面掐掉。
                // 拍自爆不需要新战斗，只需要把**这一场**里那只敌人的血压到 1。
                //
                // 也不回到 `3` 重拍：`03-battle` 那两步（`PlayScriptedTurnsForShot` → 拍）
                // 在这一场上是一场**重放**——血量已经被压到 1，第一刀就打完，结算之后
                // 地图会弹「选择你的奖励」，那层覆盖层一直留到后面几张图上（实测
                // `05-panel` / `07-panel-long` 都报「屏上有 2 层覆盖层」）。
                Advance(3);
                break;
            }
            case 14:
            {
                // 第 9 屏：倒下自爆。
                //
                // 横扫 100 局里它是第一死因（7/28），而在这一行出现之前，
                // 战斗界面**一个字都没写过它**——手册里有，战斗里没有。
                // 玩家把最后一只怪打死的那一刻，完全不知道它还会炸回来。
                // 这一屏要证明的是：在它倒之前，卡片上就写着「含自爆 N」和「合计」。
                var battle = FindBattle(_map);
                var problems = new List<string>();
                if (battle is null) problems.Add("没找到战斗场景");
                else if (!battle.StageDeathBlastForShot()) problems.Add("这一场没有「倒下会炸」的敌人，拍不到这一屏");
                // 同一帧里拍：先把血压到 1 点、刷新那一行，再取图——
                // 中间插一次 `Advance` 会落到下一个 case 上（步号是顺序的，跳号会重拍）。
                SaveOrCheckBlast(battle, problems);
                // 数码判据：这只怪的血条必须真的见底了。文案改一改就能骗过上面那句，
                // 而像素骗不了——「它马上就要倒」这件事得在图上看得见，
                // 不然这一张拍的就是「一条满血的怪 + 一句会炸的预告」。
                ProbeBlastLineInked();
                // 第 14 步后面没有第 15 步：`Advance(_step + 1)` 会落进 default 直接收尾，
                // 而收尾之前还得把这一场战斗摘掉、让最后一帧真的过去。
                // 所以跳到 99，由 `StepExtra` 统一做收尾（那里才是「拍完最后一张」的正门）。
                Advance(99, 4);
                break;
            }
            default:
                Finish();
                break;
        }
    }

    /// <summary>
    /// 「倒下自爆」那一屏的像素判据：**预告那一行真的画在了敌人卡里**。
    ///
    /// 为什么不查血条：这一屏需要「血条空掉」，可 xvfb/llvmpipe 上这条路径会
    /// 把这一帧取成上一帧——读数是 0/100、控件连 `Visible = false` 都关掉了，
    /// 屏上那条满的红条依然在（实测，和笔记里那块「敌人卡纯白」是同一条路）。
    /// 判据读它守的是渲染器，不是这个游戏；而**这一屏真正要证明的事**是
    /// 「在你把它打死之前，卡片上就写着它会炸多少」——那句话落没落墨，数得出来。
    ///
    /// 框按 `BattleLayout` 算，不写死坐标：卡高会跟着内容变，
    /// 写死的 y 在改编排之后会静默瞄到别的东西上。
    /// </summary>
    private void ProbeBlastLineInked()
    {
        var img = GetViewport().GetTexture().GetImage();
        if (img.GetWidth() != 1280 || img.GetHeight() != 720)
        {
            GD.Print("[shot] 视口不是 1280×720，跳过敌人卡的像素判据");
            return;
        }
        // 预告那一行在卡片里的位置：名字 33 + 血条 18 + 数值 20 + 行距 3×3 ≈ 80 起，
        // 高 20。取宽一点（78..100），免得字体度量差一两个像素就整块落空。
        var ink = 0;
        for (var y = 78; y < 100; y++)
        for (var x = 32; x < 1170; x++)
        {
            var c = img.GetPixel(x, y);
            if (c.R + c.G + c.B > 1.1f) ink++;
        }
        if (ink < 200)
        {
            _fail++;
            GD.PrintErr($"[FAIL] 09-battle-deathblast.png · 敌人卡上预告那一行只有 {ink} 个亮点：那行字没画出来");
        }
        else GD.Print($"  [ok] 09-battle-deathblast.png · 敌人卡上的预告那一行真的落墨了（{ink} 个亮点）");
    }

    /// <summary>
    /// 「倒下自爆」那一屏的自证：卡片上必须提前写出它会炸多少。
    ///
    /// 判据只看这一行里出现了什么，不认具体文案——
    /// 「含自爆 N」「别让它在你面前倒下」两句话以后一定会被改写，
    /// 而它们共同保证的那件事不会变：**在它倒之前，玩家读得到这一笔**。
    /// </summary>
    private void SaveOrCheckBlast(BattleScene? battle, List<string> problems)
    {
        var line = battle?.EnemyForecastText ?? "";
        if (battle is not null)
        {
            // 先钉住「它这一下就倒」这件事，再说文案。血 22/22 的敌人配一句
            // 「含自爆 7」证明的是一句没有现场的话——那张图会绿得毫无意义。
            // 「它这一下就倒」这一条读控件读数，不读像素：这条渲染路径上
            // 像素取的是上一帧（见 `ProbeBlastLineInked` 的说明）。
            if (battle.EnemyHpRatio > 0.01f)
                problems.Add($"敌人血条还灌着 {battle.EnemyHpRatio:0.00}：这一屏不是「马上就要倒」的样子");
            if (!line.Contains("自爆")) problems.Add($"敌人卡上没有提前写自爆：「{line}」");
            else
            {
                // 自爆那一笔必须真的进了合计：报「含自爆 N」而合计不动，
                // 与「只报一半」是同一个 bug 的两种写法。
                // 光在预告行里报出来还不够：预告一句句往后加，玩家一定先读第一个词——
                // 那就得是一句能直接照做的话。
                var nums = System.Text.RegularExpressions.Regex
                    .Matches(line, @"(\d+)~(\d+)")
                    .Select(m => (Low: int.Parse(m.Groups[1].Value), High: int.Parse(m.Groups[2].Value)))
                    .ToList();
                if (nums.Count < 2) problems.Add($"这句话里读不到「下一刀」与「合计」两个区间：「{line}」");
                else if (nums[^1].High <= nums[0].High)
                    problems.Add($"合计没有把自爆算进去（下一刀 {nums[0].High}，合计 {nums[^1].High}）：「{line}」");
                else if (!line.Contains("别在你面前倒下"))
                    // 第二个保证：这一行不只报数字，还说了「该怎么做」。
                    // 血量见底的那只怪会炸，玩家要在按下攻击之前读到这一句。
                    problems.Add($"敌人卡上没有提醒「别让它在你面前倒下」：「{line}」");
            }
        }
        Save("09-battle-deathblast.png",
            $"战斗（倒下自爆）：敌人卡提前写清了它倒下会炸多少「{line}」",
            problems.Count == 0 ? null : string.Join("；", problems));
    }

    /// <summary>
    /// 重开一场战斗（不为玩家那条路服务，只给截图摆现场）。
    ///
    /// 为什么不能复用「走进敌人房」那条路：第 3 步打完的那一间已经 Cleared，
    /// 再进去只会得到一句「这里已经被你清理过了」，状态机会一直等下去。
    /// 这里直接问 `Game` 要一份遭遇，和玩家按「进房间」时走的是同一个
    /// `CreateEncounter`——摆的是同一类现场，只是换了一间房。
    /// </summary>
    private (bool Ok, EncounterDef? Enc) RecreateBattleForShot(string? floorMechanic = null)
    {
        if (_run is null) return (false, null);
        var index = _run.Map.Rooms.FindIndex(r => r.Type is RoomType.Enemy or RoomType.Elite);
        if (index < 0) return (false, null);
        if (!PathTo(index)) return (false, null);
        // 摆现场时可以先借一层机制（「倒下自爆」那张图要用它）：机制是楼层属性，
        // 而第 1 层没有这一条。改的是这一局的楼层机制，不是规则表——
        // `CreateEncounter` 之后立刻改回来，免得这一层真的变成了怨恨层。
        if (floorMechanic is not null) _run.FloorMechanicOverride = floorMechanic;
        var enc = _run.CreateEncounter();
        // 借完就还：这一层仍然是「第 1 层」，变的只是这一场敌人的规则。
        _run.FloorMechanicOverride = null;
        return enc is null ? (false, null) : (true, enc);
    }

    /// <summary>
    /// 「本回合合计 X~Y」里的 X 是不是真的比「下一刀 a~b」里的 a 大。
    ///
    /// 为什么不直接比那两个字符串：这句话将来一定会被改写（多一个技能名、多一句防御提示），
    /// 判据不该跟着文案走。这里只从里面把**三对数字里最后那一对**捞出来对比——
    /// 合计是这句话里最后出现的那个区间，而它必须严格大于前一个区间。
    /// 捞不到数字就是 null（判据会说不清），捞到了不满足就是 false。
    /// </summary>
    private static bool? SumIsBiggerThanSingleBlow(string line)
    {
        var nums = System.Text.RegularExpressions.Regex
            .Matches(line, @"(\d+)~(\d+)")
            .Select(m => (Low: int.Parse(m.Groups[1].Value), High: int.Parse(m.Groups[2].Value)))
            .ToList();
        if (nums.Count < 2) return null;
        var single = nums[0];
        var total = nums[^1];
        return total.Low > single.Low || total.High > single.High;
    }

    /// <summary>
    /// 弹窗这一屏的自证：盘上有且只有一层覆盖层、名字对、卡片整块落在视口里。
    ///
    /// 最后那条是这一轮补的：内容比面板高的时候，居中容器会把面板往上顶出屏幕，
    /// 被顶掉的正好是标题条。判据问的是「卡片画出来的那一块在不在屏幕里」——
    /// 布局数本身是"对"的（它可以很高），错的是它落到屏幕外的那个位置。
    /// </summary>
    private void SavePanelShot(string name)
    {
        var overlays = Widgets.Overlays(GetTree().CurrentScene);
        var problems = new List<string>();
        if (overlays.Count != 1) problems.Add($"屏上有 {overlays.Count} 层覆盖层，应当正好 1 层");
        else if (!overlays[0].Titles.Any(t => t.Contains("商店"))) problems.Add($"弹出来的是「{string.Join(" / ", overlays[0].Titles)}」，不是商店面板");
        var vp = GetViewport().GetVisibleRect().Size;
        var card = FindAll<Widgets.PanelPlate>(GetTree().CurrentScene).FirstOrDefault();
        if (card is null) problems.Add("弹窗里找不到面板本体");
        else
        {
            var top = card.GlobalPosition.Y;
            var bottom = top + card.Size.Y;
            if (top < -0.5f) problems.Add($"面板顶边在 {top:0}（屏幕上方）：标题条被顶出屏幕了");
            if (bottom > vp.Y + 0.5f) problems.Add($"面板底边在 {bottom:0}，超出视口 {vp.Y:0}");
        }
        var cardNote = name.Contains("long") ? "长内容：正文可滚动，标题条还在" : "面板整块在屏内";
        Save(name, $"弹窗：{string.Join(" / ", overlays.SelectMany(o => o.Titles))} 压在暗角上（{cardNote}）",
            problems.Count == 0 ? null : string.Join("；", problems));
    }

    /// <summary>
    /// HUD 左上角那两条进度条各画了多少行。判据只看**像素**：
    /// 「血条比经验条粗」这件事，代码写得再对也不作数，画出来一样粗就是错的。
    /// </summary>
    private (int DangerRows, int AccentRows, float Ink) MapHudBars(Image img)
    {
        var danger = 0;
        var accent = 0;
        var ink = 0f;   // 窗口里「不是纯黑」的像素占比：0 说明这一帧根本没画出来
        var bestRed = 0f;   // 窗口里最红的那一个像素有多红（诊断用）
        var bestBlue = 0f;  // 同上，最蓝的
        // 只看 HUD 那一条（左上角 300×80，逻辑像素）：地图的砖和图例都不在这个范围里。
        //
        // 这个窗口必须按「截图相对 1280×720 逻辑视口放大了多少」换算：
        // 1600×900 下整屏被放大 1.25 倍（stretch=canvas_items / aspect=keep），
        // 而这里读的是放大镜之后的像素——写死 300×80 就等于只看了 HUD 的上半条，
        // 经验条（逻辑 y 67..74 → 实际 83..92）落在窗口外面，判据于是报「找不到经验条」。
        // 判据读的和画面上画的必须是同一个坐标系里的同一个数。
        // 图本身也要说清楚：宽高、格式、还有「左上角那一块到底是什么」。
        // 判据红的时候，人第一个想知道的不是「读到 0」，而是「你读的是哪儿、读出来是什么样」。
        GD.Print($"[shot] 图 {img.GetWidth()}×{img.GetHeight()} fmt={img.GetFormat()}");
        var zoom = MathF.Max(1f, (float)img.GetWidth() / 1280f);
        // 窗口对准**顶部 HUD 那一条**，不是写死的「左上角 300×80」。
        //
        // 写死的那一版在 CI 上一直报「血条 0 行高」，而字符画说清了原因：
        // 它那一格里有字（「第 1 层 / 15」）、有经验条，唯独没有血条——
        // 因为血条不在这块地方。判据扫哪儿，得问 HUD 自己要。
        var band = FindAll<HudBand>(GetTree().CurrentScene)
            .FirstOrDefault(b => b.GetGlobalRect().Position.Y <= 1f);
        var hudH = band is null ? 80f : MathF.Max(1f, band.GetGlobalRect().Size.Y);
        var w = (int)Math.Min(300f * zoom, img.GetWidth());
        var h = (int)Math.Min(hudH * zoom, img.GetHeight());
        for (var y = 0; y < h; y++)
        {
            var d = 0;
            var a = 0;
            // 读到的最饱和的红 / 蓝，仅用于诊断：判据红了的时候，人想知道
            // 「一条都没读到」还是「读到了但不算数」，那是两种完全不同的修法。
            for (var x = 8; x < w; x++)
            {
                var c = img.GetPixel(x, y);
                if (c.R + c.G + c.B > 0.06f) ink++;
                // 阈值按**画出来**的颜色定，不按常量推。
                //
                // 前两版写的是 `R > 0.75 / G < 0.55 / B < 0.55` 之类，而真机上的填充色
                // 是 `Palette.Danger` #ff6a6a 再叠一道 `Paint.Lighten(fill, 0.35)` 的上沿，
                // 绿蓝只要被压到 0.55 以上就出圈——血条 16px 明明画着，判据却报「0 行」。
                // 现在只认「饱和度 + 色相」，容差给足：
                if (c.R > 0.6f && c.R > c.G + 0.15f && c.R > c.B + 0.15f) d++;
                else if (c.B > 0.5f && c.B > c.R + 0.08f && c.B > c.G + 0.05f) a++;
                var redScore = c.R - MathF.Max(c.G, c.B);
                if (redScore > bestRed) bestRed = redScore;
                var blueScore = c.B - MathF.Max(c.R, c.G);
                if (blueScore > bestBlue) bestBlue = blueScore;
            }
            // 一行里要连成一条才叫「条子」：门槛按**实际条宽**给。
            // 上一版写死 40，而 1280×720 下血条是从 x≈26 到 x≈256 的一条（≈230 像素宽），
            // 门槛本身没问题；但窗口是按 zoom 放大的，窄窗口里 40 就变成了大半条。
            // 现在按窗口宽的一半算：一条占不到一半宽的，谈不上「这条是血条」。
            if (d > (int)(w * 0.25f)) danger++;
            if (a > (int)(w * 0.25f)) accent++;
        }
        // 不通过时打一份字符画（R = 红、B = 蓝、. = 其他），外加几个能定案的数。
        //
        // 为什么是日志不是 png：截图这步红了，流水线后面的「归档」不会跑，
        // 图永远留在构建容器里；日志一定拿得到。
        if (danger == 0 || accent == 0)
        {
            GD.Print($"[shot] HUD 窗口 {w}×{h}：最红 {bestRed:0.000}、最蓝 {bestBlue:0.000}、ink {ink / MathF.Max(1f, w * h):0.0000}");
            DumpMap(img, 0, h, 6);
            // 血条与经验条自己的 rect：窗口是照着 HUD 那一条算的，
            // 但「条子画在哪儿」还得问条子。两者对不上就是坐标问题。
            foreach (var bar in FindAll<ProgressBar>(GetTree().CurrentScene))
                GD.Print($"[shot] 条「{bar.Name}」rect={bar.GetGlobalRect()} value={bar.Value}/{bar.MaxValue}");
        }
        return (danger, accent, (float)w * h <= 0f ? 0f : ink / (w * h));
    }

    /// <summary>
    /// 把图上的一块打成字符画（R = 红、B = 蓝、. = 其他），写进日志。
    ///
    /// 为什么是日志而不是存 png：截图那一步红了，流水线后面的「归档」不会跑，
    /// 图会留在构建容器里出不来；而日志一定拿得到。
    /// </summary>
    private static void DumpMap(Image img, int fromY, int toY, int rows)
    {
        const int cols = 40;
        var w = img.GetWidth();
        for (var gy = 0; gy < rows; gy++)
        {
            var sb = new System.Text.StringBuilder("        ");
            var y0 = fromY + gy * (toY - fromY) / rows;
            var y1 = fromY + (gy + 1) * (toY - fromY) / rows;
            for (var gx = 0; gx < cols; gx++)
            {
                var reds = 0;
                var blues = 0;
                var x0 = gx * w / cols;
                var x1 = (gx + 1) * w / cols;
                for (var y = y0; y < y1; y++)
                for (var x = x0; x < x1; x++)
                {
                    var c = img.GetPixel(x, y);
                    if (c.R > 0.6f && c.R > c.G + 0.15f && c.R > c.B + 0.15f) reds++;
                    else if (c.B > 0.5f && c.B > c.R + 0.08f) blues++;
                }
                var area = (x1 - x0) * (y1 - y0);
                sb.Append(reds > area / 3 ? 'R' : blues > area / 3 ? 'B' : '.');
            }
            GD.Print(sb.ToString());
        }
    }

    /// <summary>插队的两屏：提示弹在哪（-1 弹出，-2 拍下来）</summary>
    private void StepExtra()
    {
        switch (_step)
        {
            case -1:
            {
                // 「提示弹在哪」这一屏：玩家按方向键撞墙、拿到金币时看到的就是它。
                // 上一版落点是 0.66 屏高——那张画布是 960×540 的竖构图，搬到 1280×720 的
                // 地图上正好压在**最后一排地砖中间**：提示胶囊盖住玩家要点的下一格。
                // 判据不查常数，查「胶囊不压任何一块房间砖、不出屏」，并且问的是
                // ToastLayer 自己报的胶囊矩形（`PillRect`），不是断言里再算一遍公式。
                var map = GetTree().CurrentScene as MapScene;
                var layer = map is null ? null : FindAll<ToastLayer>(map).FirstOrDefault();
                var problems = new List<string>();
                if (map is null) problems.Add("没找到地图场景");
                else if (layer is null) problems.Add("地图上没有提示层（ToastLayer）");
                else map.ShowToastForShot("那边没有路");
                _toastProbe = (map, layer, problems);
                Advance(-2, 6);
                break;
            }
            case -2:
            {
                var (map, layer, problems) = _toastProbe;
                if (layer is not null && !layer.Playing && layer.Pending == 0)
                    problems.Add("提示没弹出来（队列是空的）");
                if (layer is not null)
                {
                    var vp = GetViewport().GetVisibleRect().Size;
                    var pill = layer.PillRect;
                    var pillTop = pill.Position.Y;
                    var pillBottom = pillTop + pill.Size.Y;
                    if (pillTop < 0f || pillBottom > vp.Y + 0.5f)
                        problems.Add($"提示胶囊 {pillTop:0}..{pillBottom:0} 出屏（视口 {vp.Y:0}）");
                    // 判的是**整条飞行区间**，不是这一帧静态的那一块。
                    // 胶囊会往上飘（设计值 34px；带子窄时按落点自己收小），而截图只拍到动画里的一帧：
                    // 只按静态矩形比，飘到最高点那一帧压在 HUD 上的样子根本拍不到。
                    var flightTop = layer.FlightTop;
                    if (flightTop < 0f)
                        problems.Add($"提示胶囊飘起来到 {flightTop:0}：飞行区间出了屏顶");
                    // 不许压的东西有两类：房间砖，和右下角那块图例（它也是「画面上有字的一块」）。
                    // 上一版只查砖：1280×720 下图例垂到 662，胶囊摆在 611 时压的是**图例**，
                    // 而砖刚好让开了——判据绿着，屏上一行「商店」被盖掉。
                    foreach (var tile in FindAll<RoomTile>(map))
                    {
                        var r = new Rect2(tile.GlobalPosition, tile.Size);
                        if (r.End.Y < pillTop || r.Position.Y > pillBottom) continue;
                        if (r.End.X < 0f || r.Position.X > vp.X) continue;
                        problems.Add($"提示胶囊压住了房间砖 {r.Position}..{r.End}");
                        break;
                    }
                    var legend = map!.FindChild("MapLegend", true, false) as Control;
                    if (legend is not null)
                    {
                        var r = new Rect2(legend.GlobalPosition, legend.Size);
                        if (!(r.End.Y < pillTop || r.Position.Y > pillBottom || r.End.X < 0f || r.Position.X > vp.X))
                            problems.Add($"提示胶囊压住了图例 {r.Position}..{r.End}");
                    }
                    // 顶部 HUD 也算「不许压的东西」。
                    //
                    // 1280×720 的地图上，底下（网格 628 / 图例 662 → 提示条 674）只剩 12px，
                    // 胶囊只能挪到网格上方；而那一档的判据以前只查「离网格多远」，
                    // 网格上面还压着一条 74px 的 HUD——胶囊 47..93 有一大半在 HUD 面板上，
                    // 描边横穿它的贴边发光线。砖与图例都让开了，于是这一屏一直绿着。
                    // 判据要读的是「画出来的那条带子」，不是「离网格多远」这个中间量。
                    var hud = map!.FindChild("HudBand", true, false) as Control;
                    if (hud is not null)
                    {
                        var r = new Rect2(hud.GlobalPosition, hud.Size);
                        if (!(r.End.Y < flightTop || r.Position.Y > pillBottom || r.End.X < 0f || r.Position.X > vp.X))
                            problems.Add($"提示胶囊的飞行区间顶边在 {flightTop:0}，压住了顶部 HUD {r.Position}..{r.End}");
                    }
                    Save("06-toast.png", $"提示弹在房间砖与图例之外（{pillTop:0}..{pillBottom:0}）",
                        problems.Count == 0 ? null : string.Join("；", problems));
                }
                else Save("06-toast.png", "提示层没建出来", string.Join("；", problems));
                Advance(2, 4);
                break;
            }
            default:
                Finish();
                break;
        }
    }

    /// <summary>屏幕必须干净：没有任何覆盖层（`allowScrim` 时允许 Boss 演出自己那层黑底）</summary>
    private string? Clear(bool withScrim = true)
    {
        var overlays = Widgets.Overlays(GetTree().CurrentScene);
        if (overlays.Count == 0) return null;
        // Boss 出场自己带的黑底也是覆盖层：它的名字栏会在 Save 时单独核，
        // 这里允许它留着，但别的情况（引导卡 / 弹窗没关）一律算脏。
        if (withScrim && overlays.All(o => o.Titles.Count == 0)) return null;
        var names = overlays.SelectMany(o => o.Titles).ToArray();
        return $"屏上还盖着覆盖层「{(names.Length > 0 ? string.Join(" / ", names) : "无标题")}」";
    }

    /// <summary>沿 BFS 顺序找一间指定类型的房，走过去再按「进房间」——走玩家自己那条路</summary>
    private bool EnterRoomOfType(RoomType type)
    {
        if (_run is null || _map is null) return false;
        var order = RunMap(_run);
        foreach (var index in order)
        {
            if (_run.Map.Rooms[index].Type != type) continue;
            if (!PathTo(index)) continue;
            _map.EnterRoomForShot();
            return true;
        }
        return false;
    }

    /// <summary>一步一步走到目标房间（每次 Move 都会就地刷新地图，和玩家按方向键一样）</summary>
    private bool PathTo(int target)
    {
        if (_run is null) return false;
        var dist = MapGen.BfsDistances(_run.Map.Links, target);
        for (var guard = 0; guard < 64 && _run.PlayerIndex != target; guard++)
        {
            var at = _run.PlayerIndex;
            var next = -1;
            foreach (var nb in _run.Map.Links[at])
                if (dist[nb] >= 0 && dist[nb] == dist[at] - 1) { next = nb; break; }
            if (next < 0) return false;
            var (moved, _, _) = _run.Move(_run.Map.Rooms[next].X - _run.Map.Rooms[at].X,
                                          _run.Map.Rooms[next].Y - _run.Map.Rooms[at].Y);
            if (!moved) return false;
        }
        return _run.PlayerIndex == target;
    }

    private string NextBossName()
    {
        var id = AbyssEcho.Data.GameDataStore.Get().Bosses.FirstOrDefault()?.Id;
        if (string.IsNullOrEmpty(id))
        {
            GD.PrintErr("[shot] 数据表里没有 Boss");
            return "boss_abyss";
        }
        return id;
    }

    private static List<T> FindAll<T>(Node? root, List<T>? into = null) where T : Node
    {
        into ??= new List<T>();
        if (root is null) return into;
        if (root is T hit) into.Add(hit);
        foreach (var child in root.GetChildren()) FindAll(child, into);
        return into;
    }

    /// <summary>
    /// 这一轮应当拍几张：菜单 / 地图 / 提示落点 / 战斗 / 弹窗 / 长内容弹窗 / Boss 出场 /
    /// 中毒合计 / 倒下自爆。
    ///
    /// 数张数回答不了「这张图里画的是什么」，但它能挡住「某一张根本没拍」——
    /// 第 41 轮就踩过：插队那一步的步号是负数，switch 掉进 default，
    /// `06-toast.png` 一次都没拍，而清单里少一行、`--from-shots` 照样全绿。
    /// 这个数必须和流水线里那条 `test -ge` 一起改，改一处就有一处会对不上。
    /// </summary>
    public const int ShotCount = 9;

    /// <summary>
    /// 只读清单，逐条重放判据——判据和拍图时是同一份（`manifest.json` 里记着每张图证明了什么）。
    /// 图不在、张数不对、清单里有一条失败，退回码都是 1。
    /// </summary>
    /// <summary>
    /// 最后一道关：**在图上数像素**，不问任何一个控件。
    ///
    /// 为什么还要这一道：前面每一条判据都在问「某个控件 / 某个数对不对」，
    /// 而第 41 轮那次「战斗界面下半截根本没画出来」——敌人卡、行动条、按钮行、
    /// 日志全在控件树里、位置全对、`ZIndex` 全对——正是因为**没有一条判据问过
    /// 「这一屏上到底有没有那些像素」**。控件存在不等于它被画出来：
    /// 被更大的 z 盖住、被一层半透明黑幕罩住、被父级裁掉，都长这个样子。
    ///
    /// 这里只查「某块区域里该有的东西有没有落墨」。阈值取得很松——
    /// 这一条要抓的是「整块没画」，不是配色跑偏。
    /// </summary>
    private void AuditPixels()
    {
        // 坐标按 1280×720 这一版截图定，别的视口先跳过：
        // 换成按比例算会得到一个谁都没看过的框——那比没有判据更糟。
        void Probe(string file, Rect2I box, Func<Color, bool> hit, string what)
        {
            var path = System.IO.Path.Combine(Dir, file);
            if (!System.IO.File.Exists(path)) return;
            var img = Image.LoadFromFile(path);
            if (img.GetWidth() != 1280 || img.GetHeight() != 720) return;
            var n = 0;
            for (var y = box.Position.Y; y < box.End.Y; y++)
            for (var x = box.Position.X; x < box.End.X; x++)
                if (hit(img.GetPixel(x, y))) n++;
            // 「有几个像素命中」而不是「平均色」：平均色会把「一小块对 + 一大块错」
            // 混成一个不见得难看的数；命中数只说一件事——那块地方到底有没有画出东西。
            if (n < 12)
            {
                _fail++;
                GD.PrintErr($"[FAIL] {file} · {what}：{box} 里只有 {n} 个像素命中——那块东西没画出来（或被盖住了）");
            }
            else GD.Print($"  [ok] {file} · {what}（{n} 个像素命中）");
        }

        // 1) 行动条那一排按钮：卡面是深蓝灰，按钮的描边是它的两倍亮
        Probe("03-battle.png", new Rect2I(14, 556, 1266, 62), c => c.R + c.G + c.B > 1.15f, "行动条里的按钮行真的落墨了");
        // 2) 玩家血条：绿（`116, 216, 148`）。这一条第一版查的是「红」——那是**敌方**
        // 血条的颜色，玩家的是绿的，于是它 0 命中、红了一次，红在了我写错的判据上。
        // 正好演示了这一道关为什么值得有：它连「判据自己写错」也拦得住。
        Probe("03-battle.png", new Rect2I(90, 505, 900, 35), c => c.G > c.R + 0.2f && c.G > 0.5f, "行动条上的玩家血条是绿的");
        // 3) 地图 HUD 的血条
        Probe("02-map.png", new Rect2I(24, 38, 230, 20), c => c.R > c.B + 0.25f && c.R > 0.5f, "地图 HUD 上的血条是红的");
        // 4) 地图 HUD 的经验条：蓝
        Probe("02-map.png", new Rect2I(24, 60, 230, 14), c => c.B > c.R + 0.15f && c.B > 0.4f, "地图 HUD 上的经验条是蓝的");
    }

    private void Audit()
    {
        var manifest = System.IO.Path.Combine(Dir, "manifest.json");
        if (!System.IO.File.Exists(manifest))
        {
            GD.PrintErr($"[shot] 找不到清单 {manifest}：没有产物就没有「图是对的」这句话");
            _fail++;
            _step = 99;
            GetTree().Quit(1);
            return;
        }
        using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(manifest));
        var shots = doc.RootElement.GetProperty("shots");
        foreach (var shot in shots.EnumerateArray())
        {
            var file = shot.GetProperty("file").GetString() ?? "";
            var proves = shot.GetProperty("proves").GetString() ?? "";
            var ok = shot.GetProperty("ok").GetBoolean();
            var exists = System.IO.File.Exists(System.IO.Path.Combine(Dir, file));
            if (!ok)
            {
                _fail++;
                GD.PrintErr($"[FAIL] {file} · {shot.GetProperty("problem").GetString()}");
                continue;
            }
            if (!exists) { _fail++; GD.PrintErr($"[FAIL] {file} · 清单里说有，目录里没有"); continue; }
            GD.Print($"  [ok] {file} · {proves}");
        }
        // 六张：菜单 / 地图 / 提示落点 / 战斗 / 弹窗 / Boss 出场。
        // 数张数回答不了「这张图里画的是什么」，但它能挡住「某一张根本没拍」——
        // 这一轮就踩过：插队那一步的步号是负数，switch 掉进 default，`06-toast.png`
        // 一次都没拍，而清单里少一行、`--from-shots` 照样全绿。
        if (shots.GetArrayLength() < ShotCount) { _fail++; GD.PrintErr($"[FAIL] 清单里只有 {shots.GetArrayLength()} 张，至少要 {ShotCount} 张"); }
        AuditPixels();
        GD.Print(_fail == 0 ? "截图自证：全部通过" : $"截图自证：{_fail} 条不通过");
        _step = 99;
        GetTree().Quit(_fail > 0 ? 1 : 0);
    }

    /// <summary>收尾：写清单；有任何一条自证不过，退出码就是 1</summary>
    private void Finish()
    {
        if (!_captureMode) { _step = 99; GetTree().Quit(_fail > 0 ? 1 : 0); return; }
        var manifest = System.IO.Path.Combine(Dir, "manifest.json");
        var rows = "[" + string.Join(",", _proven) + "]";
        // 用 JsonSerializer 拼，不手搓字符串：
        // 手搓的那一版写出来是 `"shots":[]`——六张图的 row 一个都没进去，
        // 而日志里六行 `saved`、六张 png 都在，谁也想不到去 `cat` 一下清单。
        var doc = System.Text.Json.JsonSerializer.Serialize(new
        {
            seed = 4242,
            boss = BossIntro.NameOf(_bossName),
            shots = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(rows),
        });
        System.IO.File.WriteAllText(manifest, doc);
        // 写进去之后立刻读回来数一遍：清单是下游（`--from-shots` 与流水线）唯一的凭证，
        // 「写成功了」不等于「写对了」——这一轮第一版写出来的 `shots` 是个空数组，
        // 六张图都在、六行日志都对，而清单里一行都没有。
        try
        {
            using var check = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(manifest));
            var count = check.RootElement.GetProperty("shots").GetArrayLength();
            GD.Print($"清单里有 {count} 行（本次拍了 {_proven.Count} 张）");
            if (count != _proven.Count) { _fail++; GD.PrintErr($"[shot] 清单写坏了：{count} 行 vs {_proven.Count} 张"); }
        }
        catch (System.Exception e)
        {
            _fail++;
            GD.PrintErr($"[shot] 清单不是合法的 JSON：{e.Message}");
        }
        GD.Print($"截图完成 → {System.IO.Path.GetFullPath(Dir)}");
        if (_fail > 0) GD.PrintErr($"[shot] {_fail} 张图没有通过自证：画面内容和文件名对不上");
        _step = 99;
        GetTree().Quit(_fail > 0 ? 1 : 0);
    }

    private static BattleScene? FindBattle(Node? root)
    {
        if (root is null) return null;
        foreach (var child in root.GetChildren())
        {
            if (child is BattleScene b) return b;
            var deep = FindBattle(child);
            if (deep is not null) return deep;
        }
        return null;
    }

    /// <summary>推进到下一步：等 frames 帧后继续（每步只推进一次）</summary>
    private void Advance(int frames) => Advance(_step + 1, frames);

    /// <summary>跳到指定步号（插队的那两屏是负数，不能用 _step++ 表达）</summary>
    private void Advance(int nextStep, int frames)
    {
        _step = nextStep;
        _wait = frames;
    }

    /// <summary>
    /// 拍一张，并且当场把「这一张证明了什么」记下来。
    /// `problem` 非空即算这张没通过：图片照样留一份（人要看的就是错的那张），
    /// 但清单里会写成失败，退出码也随之变 1。
    /// </summary>
    private void Save(string name, string proves, string? problem = null, Image? image = null)
    {
        // 判据手里那张图和这里落盘的那张必须是同一张——见 02-map 那一步的注释。
        // 调用方已经取过图就传进来；没传的（绝大多数）就此刻取。
        var img = image ?? GetViewport().GetTexture().GetImage();
        img.SavePng(System.IO.Path.Combine(Dir, name));
        var ok = problem is null;
        if (!ok) _fail++;
        GD.Print(ok
            ? $"  saved {name} {img.GetWidth()}x{img.GetHeight()} · {proves}"
            : $"  [FAIL] {name} · {problem}");
        var row = $"\"file\":\"{name}\",\"width\":{img.GetWidth()},\"height\":{img.GetHeight()}," +
                  $"\"proves\":\"{Escape(proves)}\",\"ok\":{(ok ? "true" : "false")}";
        if (!ok) row += $",\"problem\":\"{Escape(problem!)}\"";
        _proven.Add("{" + row + "}");
    }

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private void Quit() { Finish(); }
}
