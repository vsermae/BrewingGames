// 启动入口：加载数据 → 决定去自检还是去主菜单。
//
// 自检走命令行的 `-- --selfcheck`：CI 里用 `--headless` 跑，
// 不需要显卡也不需要窗口，失败直接以非零码退出，流水线就红了。
namespace AbyssEcho.Scenes;

using Godot;
using AbyssEcho.Data;

public partial class Boot : Node
{
    public override void _Ready()
    {
        var args = OS.GetCmdlineUserArgs();
        var selfCheck = Array.IndexOf(args, "--selfcheck") >= 0;
        var uiSmoke = Array.IndexOf(args, "--uismoke") >= 0;

        // 首帧时刻：从进程启动到第一帧。--bench 的报告里要用它，
        // 平时也记着——它是最便宜的一个「快不快」的原始数据点。
        Dev.BenchStats.MarkFirstFrame();
        // 试玩：拿「理性玩家」策略真的跑完整局，把过程按层摊开看。
        // 它不是测试（不进断言），是这一轮「把裁判做细」剩下的那一半——
        // 光看 40 局的结局数字没法知道血是在哪类房间里丢的。
        var playArg = args.FirstOrDefault(a => a.StartsWith("--play"));
        if (playArg is not null)
        {
            var fields = playArg.Split('=', 2);
            var seed = uint.TryParse(fields.Length > 1 ? fields[1] : "", out var sd) ? sd : 1u;
            RunPlayProbe(seed);
            return;
        }

        var probeArg = args.FirstOrDefault(a => a.StartsWith("--probe"));
        if (probeArg is not null)
        {
            try { GameDataStore.Load(); }
            catch (Exception e) { GD.PushError($"数据加载失败：{e.Message}"); QuitWithGuard(2); return; }
            var pf = probeArg.Split('=', 2);
            var ps = uint.TryParse(pf.Length > 1 ? pf[1] : "", out var s1) ? s1 : 1u;
            if (probeArg.Contains("rooms")) { QuitWithGuard(Dev.RoomFlowProbe.Run()); return; }
            // --items=nobomb / --items=nocleanse：消融开关。关掉哪一件就量出哪一件的价钱，
            // 做法是同一批种子跑两组、只差这一件。「探针会不会用」这种问题不能靠读代码回答，
            // 只能靠「关掉它之后差多少」——而它必须是命令行参数，不是环境变量：
            // 命令行参数会出现在别人的复现命令里，环境变量只会留在写它那个人的记忆里。
            Dev.PlayProbe.ItemMode = args.FirstOrDefault(a => a.StartsWith("--items=")) is { } itemsArg
                ? itemsArg[(itemsArg.IndexOf('=') + 1)..]
                : "all";
            // --mistake=N：让探针偶尔算错（0~100，每一步误判的概率）。默认 0 = 每一步都算对。
            //
            // 「解毒草这件保险值多少钱」这个问题，一个每一步都算对的人永远回答不了——
            // 他从来不需要保险。误判的概率必须出现在复现命令里，理由和上面那条一样。
            Dev.PlayProbe.MistakeKind = args.FirstOrDefault(a => a.StartsWith("--mistake-kind=")) is { } kindArg
                ? kindArg[(kindArg.IndexOf('=') + 1)..]
                : "light";
            Dev.PlayProbe.MistakePct = args.FirstOrDefault(a => a.StartsWith("--mistake=")) is { } misArg
                && int.TryParse(misArg[(misArg.IndexOf('=') + 1)..], out var misPct) && misPct > 0
                ? Math.Min(100, misPct)
                : 0;
            // --probe-dump：把每一场战斗的完整日志打出来。死因表对不上账时，先看现场再改判据。
            Dev.PlayProbe.DumpBattles = args.Contains("--probe-dump");
            // --probe-flee-dump：同上，但打的是「这一场为什么没撤」的那几个输入。
            Dev.PlayProbe.FleeDump = args.Contains("--probe-flee-dump");
            // --optional=off：关掉「自己去找的可选仗」，只打必经的那几场（对照用）。
            Dev.PlayProbe.FightOptional = !args.Contains("--optional=off");
            // --probe=sweep[=N]：同一套走法连跑 N 局，把「死在几层、被什么打死」摊成一张表。
            // 单局是现场，横扫才是结论。
            if (probeArg.Contains("sweep"))
            {
                var n = 30;
                foreach (var f in pf.Length > 1 ? pf[1].Split(',') : Array.Empty<string>())
                {
                    // `--probe=sweep=100` 拆一次之后是 `sweep=100`，不是 `100`。
                    // 以前这里直接 TryParse 整个字段，于是整条链静默退回默认值 30——
                    // 而报表头写着「横扫 30 局」，看上去就是「我让你跑 30 局」。
                    // 一个永远读不到参数的入口，和一条读空的统计量是同一类东西。
                    var count = f[(f.IndexOf('=') + 1)..];
                    if (int.TryParse(count, out var parsed) && parsed > 0) n = parsed;
                }
                QuitWithGuard(Dev.PlayProbe.Sweep(n));
                return;
            }
            QuitWithGuard(Dev.PlayProbe.Run(ps));
            return;
        }

        var bench = Array.IndexOf(args, "--bench") >= 0;
        if (bench)
        {
            Dev.BenchStats.Start();
            Dev.BenchStats.Report = "";
            // --bench 量的是渲染帧：没有显示设备时它量到的是一串 0，
            // 而且等帧的 await 永远不会返回（进程挂到被外面杀掉）。
            // 与其静默挂住，不如明说「这里量不了」，让调用方去看 xvfb-run。
            if (DisplayServer.GetName() == "headless")
            {
                GD.Print("\n===== 性能基准 =====");
                GD.Print("当前是 headless 显示驱动，量不到渲染帧。请在有显示设备的环境下跑：");
                GD.Print("  xvfb-run -a godot --path . --rendering-driver opengl3 -- --bench");
                GD.Print("====================");
                QuitWithGuard(2);
                return;
            }
            // 不在这里 return：字体、主题、数据都得先就绪，否则量的是三个空场景。
            // 以前这里直接 return，于是菜单与地图都没有主题和配置，
            // 基准永远等不到那两段 await —— 表现出来就是「挂了二十分钟」。
        }

        // 字体：默认字体没有中文字形，整个界面会变成方块。
        var font = new SystemFont
        {
            FontNames = new[] { "Noto Sans CJK SC", "Source Han Sans SC", "Microsoft YaHei", "PingFang SC", "WenQuanYi Micro Hei", "Noto Sans SC" },
        };
        var theme = UI.UiTheme.Build();
        theme.DefaultFont = font;
        theme.DefaultFontSize = 16;

        try
        {
            var data = GameDataStore.Load();
            GD.Print($"深渊回响 · Godot 4.7 + C# —— 数据就绪（{data.Skills.Count} 技能 / {data.Items.Count} 道具 / {data.Monsters.Count + data.Bosses.Count} 敌人）");
        }
        catch (Exception e)
        {
            // 数据坏了就别装作能玩：直接把原因打出来并退出
            GD.PushError($"数据加载失败：{e.Message}");
            GetTree().Quit(2);
            return;
        }

        // 主题必须在进场景之前挂好：它挂在 root 上，之后建的每个 Control 才会用它。
        // 挂在某个场景自己的 _Ready 里就晚了（那个场景的根节点已经初始化完）。
        GetTree().Root.Theme = theme;

        if (selfCheck)
        {
            var node = new HeadlessSelfCheck();
            AddChild(node);
            return;
        }

        // 界面冒烟：真的把场景建出来跑一遍布局。逻辑自检管不到「_Ready 里空引用」这类问题。
        if (uiSmoke)
        {
            var node = new Dev.UiSmoke();
            AddChild(node);
            return;
        }

        // 直接换场景而不是 AddChild：菜单是 Control，挂在 Node 下拿不到视口尺寸，
        // 所有「居中」都会塌到左上角（第一次截图就是这么发现的）。
        // 必须延后一帧：Boot 自己还在 SceneTree 的 AddChild 流程里，
        // 这时候换场景会撞上 "Parent node is busy adding/removing children"。
        // 截图用：--shot=<dir> 把五个界面各拍一张、每张先自证、再写一份清单；
        // 另有一条 --from-shots=<dir> 只读那份清单重放判据（流水线里给「产物留痕」那一步用）。
        // 这不是测试，是「我说能看」之前先让我自己看一眼——看的那一眼现在也留下了证据。
        var auditArg = args.FirstOrDefault(a => a.StartsWith("--from-shots="));
        if (auditArg is not null)
        {
            CallDeferred(nameof(GoAudit), auditArg["--from-shots=".Length..]);
            return;
        }
        var shotArg = args.FirstOrDefault(a => a.StartsWith("--shot="));
        if (shotArg is not null)
        {
            CallDeferred(nameof(GoShot), shotArg["--shot=".Length..]);
            return;
        }

        if (bench)
        {
            CallDeferred(nameof(RunBench));
            return;
        }

        CallDeferred(nameof(GoMenu));
    }

    // 帧统计必须挂在「常驻节点」上，不能挂 Boot 自己：
    // Boot 是主场景的根节点，一旦 ChangeSceneToFile，它就被释放，_Process 再也不跑。
    // 以前 --bench 采不到任何帧间隔就是这一条——不是没帧，是采样器跟着场景一起没了。
    public override void _Process(double delta) => Dev.BenchStats.RecordFrame(delta);

    /// <summary>
    /// 自检/冒烟的退出：先按正常流程 Quit，再挂一个墙钟兜底。
    /// 为什么需要兜底：`GetTree().Quit()` 只是请求，场景树如果不继续推进（没有渲染帧时
    /// 待释放节点一直等不到下一帧），进程就一直不退。CI 里就撞上过这一条——
    /// 界面冒烟 38/38 全绿之后，进程又挂了十分钟，最后被 SIGKILL 判成失败。
    /// 兜底必须用 Environment.Exit 而不是再调一次 Quit：它走的是「立刻结束进程」。
    /// </summary>
    public static void QuitWithGuard(int code)
    {
        var tree = ((SceneTree)Engine.GetMainLoop());
        tree.Quit(code);
        // 2 秒还没退就硬退：正常路径下根本走不到这里。
        // 用 OS.Kill（Godot 自己的进程号）而不是 System.Environment.Exit——
        // Godot 的 .NET 宿主裁剪掉了后者的某些重载，编译期就报 CS0117。
        // 进程号必须在主线程上取：Godot 的托管宿主是线程绑定的，
        // 在后台线程碰任何 Node/OS 单例都会以「Object name: 'Godot.OSInstance'」为由直接 abort。
        // 以前这里在兜底线程里调 OS.GetProcessId()，于是「正常退出」也会在退出路径上炸一次
        // （exit code 134），日志里看不出问题、CI 却把一次成功的自检判成失败。
        var pid = OS.GetProcessId();
        new System.Threading.Thread(() =>
        {
            System.Threading.Thread.Sleep(2000);
            GD.Print($"[退出兜底] 正常 Quit 之后进程仍未结束，强制退出（code={code}）");
            OS.Kill(pid);
        }) { IsBackground = true }.Start();
    }

    private void GoMenu() => GetTree().ChangeSceneToFile("res://Scenes/Menu.tscn");

    /// <summary>
    /// 启动性能基准。它只做两件事：把驱动节点挂到 root 上、留一道墙钟兜底。
    /// 真正的流程在 Dev/BenchProbe 里——因为它要跨场景，而 Boot 自己会被换掉。
    /// </summary>
    private void RunBench()
    {
        GetTree().Root.AddChild(new Dev.BenchProbe());
        // 兜底：万一 BenchProbe 那条链断了（比如场景建不起来），也别挂住。
        // 直接结束进程而不是等下一次帧——没有帧的时候等不到下一次。
        new System.Threading.Timer(_ => HardExit("25 秒内没跑完"), null, 25000, System.Threading.Timeout.Infinite);
    }

    /// <summary>
    /// 等等待永远不返回时的最后一道闸：直接结束进程。
    /// 这里只碰 OS 与 GD.Print（两者都能在任意线程调用），
    /// 不去读场景树——`BenchStats.Build(Root)` 要遍历节点，在定时器线程上是非法的。
    /// 少打一份数字，换「进程一定能退出」，值得。
    /// </summary>
    private static void HardExit(string why)
    {
        GD.Print($"\n===== 性能基准（{why}，数据不全）=====");
        GD.Print($"首帧耗时      {(Dev.BenchStats.FirstFrameMs is { } ms ? $"{ms:F1} ms" : "量不到（无渲染帧）")}");
        GD.Print("====================");
        GD.Print("[退出兜底] --bench 等不到帧，强制退出");
        OS.Kill(OS.GetProcessId());
    }

    /// <summary>
    /// --play[=seed]：无头跑一整局，逐层打印血量、房型分布与死亡原因。
    /// 走的是自检里那套「理性玩家」，所以数字能和 40 局基线对上。
    /// </summary>
    private void RunPlayProbe(uint seed)
    {
        try
        {
            GameDataStore.Load();
        }
        catch (Exception e)
        {
            GD.PushError($"数据加载失败：{e.Message}");
            GetTree().Quit(2);
            return;
        }
        HeadlessSelfCheck.PlayReport(seed);
        QuitWithGuard(0);
    }

    private void GoShot(string dir)
    {
        var shooter = new Dev.Screenshot { Dir = dir };
        GetTree().Root.AddChild(shooter);
    }

    /// <summary>只读清单不拍图。和 GoShot 分开是因为 CallDeferred 只认单参数 + Variant[] 的老写法</summary>
    private void GoAudit(string dir)
    {
        var auditor = new Dev.Screenshot { Dir = dir, AuditOnly = true };
        GetTree().Root.AddChild(auditor);
    }
}
