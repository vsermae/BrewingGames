// 性能基准的驱动节点：挂在 root 上，不随场景切换被释放。
//
// 为什么要单独一个节点：--bench 要先后跑「菜单 1 秒 → 地图 5 秒」，
// 而这段等待原本写在 Boot 里。Boot 是主场景的根节点，ChangeSceneToFile 会把它释放，
// await 之后的续体跑在已释放节点上（GetTree() 为 null），于是基准一次都没跑完，
// CI 里表现为「挂了二十分钟后被 SIGKILL」。采样器同理——以前也挂在 Boot 上，
// 换场景之后就再也收不到帧。
//
// 这里用「时间片状态机 + 帧推进」而不是 async/await：它不依赖场景生命周期的任何保证。
namespace AbyssEcho.Dev;

using Godot;

public partial class BenchProbe : Node
{
    private enum Stage { Menu, Map, Done }

    private Stage _stage = Stage.Menu;
    private float _elapsed;

    public override void _Ready()
    {
        Name = "BenchProbe";
    }

    public override void _Process(double delta)
    {
        // 每一帧都喂给统计器：帧间隔分位数就是从这里来的
        BenchStats.RecordFrame(delta);
        _elapsed += (float)delta;
        switch (_stage)
        {
            case Stage.Menu when _elapsed >= 1.0f:
                // 菜单只是一屏文字，量不出什么；稳定后进地图
                GetNodeOrNull<GameStore>("/root/GameStore")?.NewRun(20241019);
                GetTree().ChangeSceneToFile("res://Scenes/Map.tscn");
                _stage = Stage.Map;
                _elapsed = 0f;
                break;
            case Stage.Map when _elapsed >= 5.0f:
                GD.Print("\n===== 性能基准 =====");
                GD.Print(BenchStats.Build(GetTree().Root));
                GD.Print("====================");
                _stage = Stage.Done;
                Scenes.Boot.QuitWithGuard(0);
                break;
        }
    }
}
