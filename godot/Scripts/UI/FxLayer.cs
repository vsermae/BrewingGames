// 技能粒子特效：配方（纯数据）+ 每帧推进 + 一个 _Draw 批量画完。
//
// TS 版的配方刻意不 import Phaser，好让「冰霜新星放几个粒子」能在 node 里断言；
// 这里继续守住这条：**配方只在 FxLayer 里做数学，不下判断**，
// 所以 UiSmoke 能在 headless 里跑配方、数粒子、查寿命，一张图都不用渲染。
//
// 一个节点画所有粒子（而不是每个粒子一个节点）是关键：
// 一场战斗十几次技能，每粒子一个 Node2D 会让 GC 抖成幻灯片。
namespace AbyssEcho.UI;

using Godot;

public enum FxShape
{
    Circle,
    Line,
    Triangle,
}

public struct FxParticle
{
    public Vector2 Pos;
    public Vector2 Vel;
    /// <summary>线段的终点方向（Line 用）</summary>
    public Vector2 Dir;
    public float Age;
    public float Life;
    public float Size;
    public Color Color;
    public FxShape Shape;
    public float BaseAlpha;
    public float Gravity;
    public float Rotation;
    public float Spin;
    /// <summary>绕锚点公转（石肤）</summary>
    public Vector2 Anchor;
    public float Orbit;
    public float OrbitAngle;
    public float OrbitSpeed;
    public bool Alive;
}

public partial class FxLayer : Control
{
    /// <summary>同屏粒子上限：多个技能同帧炸开时别把帧率打穿</summary>
    public const int MaxParticles = 420;

    /// <summary>
    /// 粒子池：List 里的 struct 只是"对象"，没有堆分配——
    /// 真正的开销是每次 Emit 时 List 扩容与 Clear，而不是"每个粒子一个 Node"。
    /// 所以这里的池化方式很朴素：容量复用（EnsureCapacity）+ 死粒子原地压缩（紧致化）。
    /// 上一版用 RemoveAt(i) 在倒序循环里删，单个 O(n)，一次冰霜新星 36 颗 + 石肤 20 颗
    /// 同帧到期时就是 O(n²)；紧致化一遍扫完，且不会移动存活粒子的相对顺序（画序稳定）。
    /// </summary>
    private readonly List<FxParticle> _active = new();
    private readonly List<FxParticle> _scratch = new();

    public FxLayer()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        // 「表现层压在最上面」写在自己身上，而不是由调用方每次记得传：
        // 上一版这个数是散在场景里的字面量，谁新加一块特效谁就要再猜一次。
        ZIndex = Layers.Of(StageOrder.Fx);
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    public int ActiveCount => _active.Count;

    public void Emit(IEnumerable<FxParticle> particles)
    {
        // 预留容量：一场战斗最多同屏 420 颗，一次扩到够用，之后不再扩容
        if (_active.Capacity < MaxParticles) _active.Capacity = MaxParticles;
        foreach (var p in particles)
        {
            if (_active.Count >= MaxParticles) break;
            _active.Add(p);
        }
    }

    /// <summary>池化统计：UiSmoke 用它核对"粒子不再无限增长"</summary>
    public int Capacity => _active.Capacity;

    /// <summary>紧致化次数（每次推进只要没有死粒子就不做），用于排查 O(n²) 回潮</summary>
    public int CompactCount { get; private set; }

    public void Clear() => _active.Clear();

    /// <summary>重力：需求写「每帧 +0.1」，按 16.67ms 一帧折算成每秒</summary>
    private const float GravityPerSec = 0.1f * 60f;

    public override void _Process(double delta)
    {
        if (_active.Count == 0) return;
        StepCore((float)delta * 1000f);
        QueueRedraw();
    }

    /// <summary>推进一帧（单位：毫秒）。拆出来是为了让测试能直接驱动，不必等渲染帧。</summary>
    private void StepCore(float dt)
    {
        if (_active.Count == 0) return;
        var expired = false;
        for (var i = _active.Count - 1; i >= 0; i--)
        {
            var p = _active[i];
            p.Age += dt;
            if (p.Age >= p.Life)
            {
                // 标记为死，统一紧致化：倒序 RemoveAt 在批量到期时是 O(n²)，
                // 而一帧里到期几十颗是常态（同帧炸开的技能也会同帧一起死）。
                p.Alive = false;
                _active[i] = p;
                expired = true;
                continue;
            }
            if (p.Orbit > 0f)
            {
                p.OrbitAngle += p.OrbitSpeed * dt;
                p.Pos = p.Anchor + new Vector2(Mathf.Cos(p.OrbitAngle), Mathf.Sin(p.OrbitAngle)) * p.Orbit;
            }
            else
            {
                p.Pos += p.Vel * dt;
                p.Vel += new Vector2(0, GravityPerSec * dt / 1000f * p.Gravity);
            }
            p.Rotation += p.Spin * dt;
            p.Alive = true;
            _active[i] = p;
        }
        if (expired) Compact();
    }

    /// <summary>测试用的一步推进：与 _Process 同一份逻辑，但不依赖渲染帧</summary>
    public void StepForTest(float deltaMs) => StepCore(deltaMs);

    /// <summary>一遍扫完，把存活的粒子按原顺序搬回前面</summary>
    private void Compact()
    {
        _scratch.Clear();
        if (_scratch.Capacity < MaxParticles) _scratch.Capacity = MaxParticles;
        foreach (var p in _active)
            if (p.Alive) _scratch.Add(p);
        _active.Clear();
        _active.AddRange(_scratch);
        _scratch.Clear();
        CompactCount++;
    }

    public override void _Draw()
    {
        foreach (var p in _active)
        {
            var t = 1f - p.Age / p.Life;
            var a = Mathf.Clamp(p.BaseAlpha * t, 0f, 1f);
            if (a <= 0.01f) continue;
            var col = Paint.Alpha(p.Color, a);
            switch (p.Shape)
            {
                case FxShape.Line:
                    Paint.Line(this, p.Pos, p.Pos + p.Dir, col, Mathf.Max(1f, p.Size * 0.5f));
                    break;
                case FxShape.Triangle:
                {
                    var pts = new Vector2[3];
                    for (var i = 0; i < 3; i++)
                    {
                        var ang = p.Rotation + Mathf.Tau * i / 3f;
                        pts[i] = p.Pos + new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * p.Size;
                    }
                    Paint.Poly(this, pts, col);
                    break;
                }
                default:
                    Paint.Circle(this, p.Pos, p.Size, col);
                    break;
            }
        }
    }

    // ---------------- 配方 ----------------
    // 五个技能各一套。数值照搬 TS 版 particleRecipes.ts：
    // 冰霜 36 点 × 0.16~0.22 px/ms，雷鸣 5 条 × 8 段折线，石肤 20 个三角绕圈，
    // 汲取 15 点沿连线，毒雾 30 点散布 60×60。改数字前先想清楚为什么。

    public static List<FxParticle> Recipe(string skillId, Vector2 from, Vector2 to, Func<float> rand)
    {
        var outLi = new List<FxParticle>();
        FxParticle P() => new() { Alive = true, Gravity = 1f, BaseAlpha = 1f };
        switch (skillId)
        {
            case "frost_nova":
            {
                const int count = 36;
                for (var i = 0; i < count; i++)
                {
                    var a = (float)i / count * Mathf.Tau + rand() * 0.06f;
                    var speed = 0.16f + rand() * 0.06f;
                    var p = P();
                    p.Pos = from;
                    p.Vel = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * speed;
                    p.Life = 700;
                    p.Size = 3f + rand() * 3f;
                    p.Color = new Color("7fd4ff");
                    p.Shape = FxShape.Circle;
                    outLi.Add(p);
                }
                break;
            }
            case "thunderclap":
            {
                var flash = P();
                flash.Pos = to;
                flash.Life = 150;
                flash.Size = 74f;
                flash.Color = Colors.White;
                flash.Shape = FxShape.Circle;
                outLi.Add(flash);
                const int bolts = 5;
                const int segs = 8;
                for (var b = 0; b < bolts; b++)
                {
                    var targetX = to.X + (rand() - 0.5f) * 46f;
                    const float startY = -20f;
                    var px = targetX + (rand() - 0.5f) * 40f;
                    var py = startY;
                    for (var s = 0; s < segs; s++)
                    {
                        var nx = targetX + (rand() - 0.5f) * 26f;
                        var ny = startY + (to.Y - startY) * (s + 1) / segs;
                        var p = P();
                        p.Pos = new Vector2(px, py);
                        p.Dir = new Vector2(nx - px, ny - py);
                        p.Life = 400;
                        p.Size = 3f;
                        p.Color = new Color("ffff44");
                        p.Shape = FxShape.Line;
                        outLi.Add(p);
                        px = nx;
                        py = ny;
                    }
                }
                break;
            }
            case "stone_skin":
            {
                const int count = 20;
                var baseAng = rand() * Mathf.Tau;
                for (var i = 0; i < count; i++)
                {
                    var p = P();
                    p.Pos = from;
                    p.Anchor = from;
                    p.Orbit = 34f + rand() * 22f;
                    p.OrbitAngle = baseAng + (float)i / count * Mathf.Tau;
                    p.OrbitSpeed = 0.0018f + rand() * 0.0008f;
                    p.Life = 1200;
                    p.Size = 3f + rand() * 4f;
                    p.Color = new Color("8b7355");
                    p.Shape = FxShape.Triangle;
                    p.Rotation = rand() * Mathf.Pi;
                    p.Spin = (rand() - 0.5f) * 0.004f;
                    outLi.Add(p);
                }
                break;
            }
            case "soul_drain":
            {
                const int count = 15;
                const float speed = 0.09f;
                for (var i = 0; i < count; i++)
                {
                    var t = (float)i / (count - 1);
                    var p = P();
                    p.Pos = to + (from - to) * t;
                    p.Vel = (from - to) * speed * 0.12f;
                    p.Life = 650;
                    p.Size = 3.2f + (1f - t) * 1.6f;
                    p.Color = new Color("cc3333");
                    p.Shape = FxShape.Circle;
                    outLi.Add(p);
                }
                break;
            }
            case "toxic_fog":
            {
                const int count = 30;
                for (var i = 0; i < count; i++)
                {
                    var drift = rand() < 0.5f ? -1f : 1f;
                    var p = P();
                    p.Pos = to + new Vector2((rand() - 0.5f) * 60f, (rand() - 0.5f) * 60f);
                    p.Vel = new Vector2(drift * (0.01f + rand() * 0.02f),
                        drift > 0 ? -(0.02f + rand() * 0.03f) : (rand() - 0.5f) * 0.02f);
                    p.Life = 1300;
                    p.Size = 5f + rand() * 8f;
                    p.Color = new Color("64c864");
                    p.Shape = FxShape.Circle;
                    // 需求要求每团透明度在 0.3~0.7：最终透明度 = 剩余寿命 × 基准浓度
                    p.BaseAlpha = 0.3f + rand() * 0.4f;
                    outLi.Add(p);
                }
                break;
            }
        }
        return outLi;
    }

    /// <summary>有专属特效的技能：其余技能走飘字 + 抖动，不硬凑粒子</summary>
    public static readonly string[] FxSkillIds = { "frost_nova", "thunderclap", "stone_skin", "soul_drain", "toxic_fog" };
}
