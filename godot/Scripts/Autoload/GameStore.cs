// 存档与会话状态的 Autoload。
//
// TS 版把元进展放在 localStorage 里，Godot 里换成 ConfigFile（落盘到 user://）。
// 另外它还负责「当前这一局」的引用：场景可以反复创建销毁，但一局只有一个。
// 场景之间只通过这个单例交换 Game，不互相持有引用——这正是 TS 版用 scene.get 干的事。
namespace AbyssEcho;

using Godot;
using AbyssEcho.Core;
using AbyssEcho.Systems;
using AbyssEcho.Autoload;

public partial class GameStore : Node
{
    private const string SavePath = "user://abyss_echo_meta.cfg";
    /// <summary>局内进度：和元进展分开存。元进展是跨局资产，局内进度是一次性的</summary>
    private const string RunPath = "user://abyss_echo_run.cfg";
    private const string Section = "meta";

    /// <summary>当前这一局；主菜单进游戏时创建，回标题时保留以便「继续」</summary>
    public Game? Run { get; private set; }

    /// <summary>元进展：跨局保存</summary>
    public MetaState Meta { get; private set; } = new();

    /// <summary>界面上一次的操作反馈（「那边没有路」「买下了锈剑」这类），场景读一次就清</summary>
    public string? Toast { get; set; }

    /// <summary>玩家上次用的种子，方便复现某一局</summary>
    public uint? LastSeed { get; private set; }

    /// <summary>
    /// 玩家看一眼就够的界面偏好：新手引导看没看过、战斗特效开不开。
    /// 单独一段、单独存——它既不是元进展（不影响解锁），也不是局内进度（跨局保留），
    /// 混进任何一边都会被「清档」「通关」连带清掉。TS 版这两条放在 localStorage 里。
    /// </summary>
    public bool IntroSeen { get; private set; }
    public bool FxEnabled { get; private set; } = true;

    public override void _Ready()
    {
        Meta = LoadMeta();
        // 关窗也要落盘：Godot 默认收到关闭请求就退出，而这一局的最后一个安全点
        // 可能是「刚喝完一瓶药」「刚在商店买完东西」——那些都没有写档。
        // 只存「可以被打断的安全点」这条规矩不变：战场不存，回到地图重开一场。
        GetTree().AutoAcceptQuit = false;
    }

    public override void _Notification(int what)
    {
        // 走 Node.NotificationWMCloseRequest（= MainLoop.NotificationWmCloseRequest）的数值，
        // 不写裸常量：窗口请求退出是 1006，进程请求退出是 1005，抄错一位就永远不触发。
        if (what != (int)Node.NotificationWMCloseRequest) return;
        SaveRun();
        GetTree().Quit();
    }

    public MetaState LoadMeta()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(SavePath) != Error.Ok) return new MetaState();
        // 界面偏好顺手读回来：第一次开新档时它们保持默认（引导会讲一次、特效开着）
        IntroSeen = (bool)cfg.GetValue(Section, "intro_seen", false);
        FxEnabled = (bool)cfg.GetValue(Section, "fx_enabled", true);
        var state = new MetaState
        {
            Shards = (int)cfg.GetValue(Section, "shards", 0),
            // 开过几局：第一局是教学局（见 MainMenu 里那条新手引导）
            Runs = (int)cfg.GetValue(Section, "runs", 0),
        };
        var unlocked = cfg.GetValue(Section, "unlocked", new Godot.Collections.Array<string>()).AsGodotArray<string>();
        foreach (var u in unlocked)
        {
            if (Enum.TryParse<MetaUnlock>(u, out var id)) state.Unlocked.Add(id);
        }
        var achievements = cfg.GetValue(Section, "achievements", new Godot.Collections.Array<string>()).AsGodotArray<string>();
        foreach (var a in achievements) state.Achievements.Add(a);
        return state;
    }

    public void SaveMeta(MetaState state)
    {
        Meta = state;
        var cfg = new ConfigFile();
        cfg.SetValue(Section, "shards", state.Shards);
        cfg.SetValue(Section, "runs", state.Runs);
        var unlocked = new Godot.Collections.Array<string>();
        foreach (var u in state.Unlocked) unlocked.Add(u.ToString());
        cfg.SetValue(Section, "unlocked", unlocked);
        var achievements = new Godot.Collections.Array<string>();
        foreach (var a in state.Achievements) achievements.Add(a);
        cfg.SetValue(Section, "achievements", achievements);
        cfg.SetValue(Section, "intro_seen", IntroSeen);
        cfg.SetValue(Section, "fx_enabled", FxEnabled);
        // 写档失败静默：玩家不会因为存档写不进而丢掉这一局
        cfg.Save(SavePath);
    }

    /// <summary>开新局。seed 为空则随机，但会记下来供 UI 展示。</summary>
    /// <summary>直接开一局并进地图：菜单与调试入口共用同一条路，免得两处各写一份开场</summary>
    public void StartRun(Node from, uint? seed = null)
    {
        NewRun(seed);
        from.GetTree().ChangeSceneToFile("res://Scenes/Map.tscn");
    }

    public Game NewRun(uint? seed = null)
    {
        var actual = seed ?? (uint)(Time.GetTicksUsec() & 0xFFFFFFFF);
        LastSeed = actual;
        // 开局的计数在**开新局**这一刻加一：它决定「要不要讲那一遍新手引导」。
        // 落在开局而不是结算，是因为玩家第一局就关窗的那一次也算开过一局。
        Meta.Runs += 1;
        SaveMeta(Meta);
        Run = new Game(actual, Meta);
        Run.MetaSaver = SaveMeta;
        return Run;
    }

    /// <summary>
    /// 继续上一次的局。内存里有就直接用；没有就读盘——
    /// 「继续」按钮不能在重启游戏之后消失，否则玩家关窗就等于丢了一整局。
    /// </summary>
    public Game? ContinueRun()
    {
        if (Run is { Status: GameStatus.Playing }) return Run;
        var loaded = SaveSerializer.Load(RunPath, Meta);
        if (loaded is null) return null;
        loaded.MetaSaver = SaveMeta;
        Run = loaded;
        return loaded;
    }

    /// <summary>盘上有没有能继续的局（主菜单据此决定「继续」按钮亮不亮）</summary>
    /// <summary>盘上有没有能继续的局。只读文件头，不真把整局读出来（主菜单每帧都可能问）</summary>
    public bool HasSavedRun() => SaveSerializer.CanResume(RunPath);

    /// <summary>
    /// 把当前这一局写到盘上。
    /// 调用时机有三处：下楼、回地图、返回标题——都是「一个可以被打断的安全点」。
    /// 战斗中途不写：见 OfflineRules 里关于「无损暂停等于作弊器」的理由。
    /// </summary>
    public void SaveRun()
    {
        if (Run is null) return;
        if (Run.Status != GameStatus.Playing)
        {
            // 局已经结束：留着存档只会让「继续」点进去看到一屏结算
            SaveSerializer.Delete(RunPath);
            return;
        }
        // 显示用的种子跟着这一局走：读档之后 LastSeed 可能是另一局留下的
        SaveSerializer.Save(RunPath, Run, Run.Rng.Seed);
        // 元进展也可能在这一局里变了（碎片、成就），顺手一起落盘
        SaveMeta(Run.Meta);
    }

    /// <summary>
    /// 清档：连内存里的那一局一起丢掉。
    /// 调用方注意顺序——它会把 Run 置空，所以「清档 + 开新局」必须写成 ClearRun() 再 NewRun()，
    /// 反着写等于拿着已经被丢掉的旧局继续改（界面冒烟的喝药断言踩过）。
    /// </summary>
    public void ClearRun()
    {
        Run = null;
        SaveSerializer.Delete(RunPath);
    }

    /// <summary>
    /// 买下一档元进展。
    ///
    /// 关键是「改哪一份」。界面上读到的是 <see cref="Meta"/>，而一局开打之后
    /// 手里握的是开局那一刻的快照；两份若各改各的，下一次 `SaveRun`（它会顺手
    /// `SaveMeta(Run.Meta)`）就把解锁写回旧值——玩家买的东西在挣到下一块碎片时消失。
    /// 所以：局在跑的时候改局里那份，并让它带着最新的碎片余额；
    /// 没有局在跑时改 <see cref="Meta"/>。
    /// </summary>
    public bool TryUnlock(MetaUnlock id)
    {
        if (Run is not null)
        {
            if (!Run.TryUnlock(id)) return false;
            Meta = Run.Meta;
            return true;
        }
        if (!MetaProgression.HasUnlock(Meta, id) && Meta.Shards < MetaProgression.UnlockCost[id]) return false;
        var next = MetaProgression.TryUnlock(Meta, id);
        if (next is null) return false;
        Meta = next;
        SaveMeta(Meta);
        return true;
    }

    /// <summary>记下「新手引导看过了」：同一个从库里只讲一次，不占元进展的存档位</summary>
    public void MarkIntroSeen()
    {
        if (IntroSeen) return;
        IntroSeen = true;
        SaveMeta(Meta);
    }

    /// <summary>战斗特效开关。关掉只影响粒子与飘字，结算一条不变</summary>
    public bool ToggleFx()
    {
        FxEnabled = !FxEnabled;
        SaveMeta(Meta);
        return FxEnabled;
    }
}
