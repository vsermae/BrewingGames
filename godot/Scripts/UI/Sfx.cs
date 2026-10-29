// 程序化音效：振荡器 + 噪声 + 包络，实时合成，不引入任何音频文件。
//
// 为什么不用 .wav/.ogg：这个仓库承诺断网离线可玩、体积为零资源，
// TS 版用 WebAudio 合成的理由在这里一样成立。Godot 的对应物是
// AudioStreamGenerator —— 它给一段 PCM 缓冲，我们自己往里写波形。
//
// 分工和 TS 版一致：**Sfx.Names 是唯一清单**，配方表决定每个音效怎么合成。
// 合成过程是纯数学，UiSmoke 能在 headless 里跑一遍（无音频设备也不炸）。
namespace AbyssEcho.UI;

using Godot;

public static class Sfx
{
    /// <summary>TS 版 audio.ts 的 SfxName 全表，一个不多一个不少</summary>
    public static readonly string[] Names =
    {
        "click", "step", "hit", "crit", "hurt", "block", "coin", "chest",
        "gain", "heal", "reveal", "descend", "victory", "defeat", "deny", "boss",
    };

    private const int SampleRate = 22050;
    private const float MasterVolume = 0.32f;

    private static AudioStreamPlayer? _player;
    private static AudioStreamGeneratorPlayback? _playback;
    /// <summary>BGM 单独一个播放器：音效会被打断（一次一记），垫乐必须一直在</summary>
    private static AudioStreamPlayer? _music;
    private static string _musicKey = "";
    private static bool _muted;
    private static readonly Dictionary<string, double> LastAt = new();

    public static bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            if (_music is null || !GodotObject.IsInstanceValid(_music)) return;
            // 静音只关声音，不销毁节点：再打开时从当前循环继续，而不是从头再来
            _music.StreamPaused = value;
        }
    }

    public static bool Toggle()
    {
        Muted = !Muted;
        return _muted;
    }

    /// <summary>挂到一个场景上。重复调用安全；节点没了会重新建。</summary>
    public static void Attach(Node host)
    {
        if (_player is not null && GodotObject.IsInstanceValid(_player)) return;
        var stream = new AudioStreamGenerator
        {
            MixRate = SampleRate,
            BufferLength = 0.5f,
        };
        _player = new AudioStreamPlayer { Stream = stream, VolumeDb = Mathf.LinearToDb(MasterVolume) };
        host.AddChild(_player);
        _player.Play();
        _playback = _player.GetStreamPlayback() as AudioStreamGeneratorPlayback;

        // BGM：用 AudioStreamWav 循环播放一段现场合成的 PCM，
        // 而不是再开一个 generator——generator 的缓冲要每帧喂，
        // 垫乐断了就断一拍；Wav 的 LoopMode 交给引擎循环最稳。
        _music = new AudioStreamPlayer { VolumeDb = Mathf.LinearToDb(Bgm.Volume), Autoplay = false };
        host.AddChild(_music);
        if (!string.IsNullOrEmpty(_musicKey)) ApplyMusic(_musicKey);
    }

    public static void Detach()
    {
        if (_player is not null && GodotObject.IsInstanceValid(_player)) _player.QueueFree();
        _player = null;
        _playback = null;
        if (_music is not null && GodotObject.IsInstanceValid(_music)) _music.QueueFree();
        _music = null;
    }

    /// <summary>
    /// 播放一个音效。静音、没有播放设备、headless 时全部静默失败——
    /// 音频失败绝不能把游戏带下去（TS 版也是这么兜的）。
    /// </summary>
    public static void Play(string name)
    {
        if (_muted) return;
        // 同一音效 40ms 内不重复：走路和点击容易一帧触发好几次
        var now = Time.GetTicksMsec() / 1000.0;
        if (LastAt.TryGetValue(name, out var last) && last + 0.04 > now) return;
        LastAt[name] = now;

        var samples = Synthesize(name);
        Push(samples);
    }

    /// <summary>当前 BGM 的曲子 key（空表示还没切过）</summary>
    public static string MusicKey => _musicKey;

    /// <summary>
    /// 切 BGM。同一首重复调用不会重来——换层时每帧都调它也只会是一次赋值。
    /// 曲子由 Bgm.Synthesize 现场合成，缓存成 AudioStreamWav 后交给引擎循环。
    /// </summary>
    public static void Music(string key)
    {
        if (key == _musicKey) return;
        _musicKey = key;
        SampleCache.Remove(key);
        ApplyMusic(key);
    }

    /// <summary>测试用：合成结果没别的副作用，但会占用几 MB，验完就丢</summary>
    public static void ForgetMusicCache() => SampleCache.Clear();

    private static void ApplyMusic(string key)
    {
        if (_music is null || !GodotObject.IsInstanceValid(_music)) return;
        var wav = MusicSamples(key);
        if (wav is null) return;
        _music.Stream = wav;
        if (!_muted) _music.Play();
    }

    /// <summary>整段 PCM → AudioStreamWav（16 位单声道，循环整段）。长度由 Bgm.LoopSeconds 决定</summary>
    private static AudioStreamWav? MusicSamples(string key)
    {
        if (SampleCache.TryGetValue(key, out var cached)) return cached;
        var pcm = Bgm.Synthesize(key);
        if (pcm.Length == 0) return null;
        var bytes = new byte[pcm.Length * 2];
        for (var i = 0; i < pcm.Length; i++)
        {
            var v = (short)(Mathf.Clamp(pcm[i], -1f, 1f) * short.MaxValue);
            bytes[i * 2] = (byte)(v & 0xff);
            bytes[i * 2 + 1] = (byte)((v >> 8) & 0xff);
        }
        var wav = new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = Bgm.SampleRate,
            Stereo = false,
            Data = bytes,
            LoopMode = AudioStreamWav.LoopModeEnum.Forward,
            LoopBegin = 0,
            LoopEnd = pcm.Length,
        };
        SampleCache[key] = wav;
        return wav;
    }

    private static readonly Dictionary<string, AudioStreamWav> SampleCache = new();

    /// <summary>把 PCM 推进生成器缓冲。缓冲满了就丢掉这一帧的余量，不阻塞。</summary>
    private static void Push(float[] samples)
    {
        if (_playback is null) return;
        try
        {
            var frames = Math.Min(samples.Length, _playback.GetFramesAvailable());
            var arr = new Vector2[frames];
            for (var i = 0; i < frames; i++) arr[i] = new Vector2(samples[i], samples[i]);
            _playback.PushBuffer(arr);
        }
        catch
        {
            // 设备没起来 / 被 headless 剥掉：静默失败
        }
    }

    // ---------------- 配方 ----------------

    private sealed record Tone(string Wave, float From, float To, float Dur, float Vol, float Delay = 0f);
    private sealed record Noise(float Dur, float Vol, float Freq, float Delay = 0f);

    /// <summary>
    /// 每个音效的配方。数值照搬 TS 版 audio.ts 的 playSfx：
    /// 那里是 WebAudio 的 osc/noise 调用，这里是同等语义的数据表。
    /// </summary>
    private static readonly Dictionary<string, (Tone[] Tones, Noise[] Noises)> Recipes = new()
    {
        ["click"] = (new[] { new Tone("square", 620, 520, 0.07f, 0.16f) }, Array.Empty<Noise>()),
        ["step"] = (Array.Empty<Tone>(), new[] { new Noise(0.07f, 0.10f, 620) }),
        ["hit"] = (new[] { new Tone("triangle", 220, 70, 0.14f, 0.22f) }, new[] { new Noise(0.13f, 0.30f, 1400) }),
        ["crit"] = (new[]
            {
                new Tone("sawtooth", 320, 60, 0.22f, 0.26f),
                new Tone("square", 900, 400, 0.12f, 0.12f, 0.02f),
            },
            new[] { new Noise(0.16f, 0.34f, 2600) }),
        ["hurt"] = (new[] { new Tone("sawtooth", 180, 90, 0.20f, 0.24f) }, new[] { new Noise(0.10f, 0.16f, 700) }),
        ["block"] = (new[] { new Tone("square", 340, 300, 0.08f, 0.14f) }, new[] { new Noise(0.09f, 0.12f, 1800) }),
        ["coin"] = (new[]
        {
            new Tone("triangle", 900, 900, 0.06f, 0.20f),
            new Tone("triangle", 1350, 1350, 0.10f, 0.18f, 0.06f),
        }, Array.Empty<Noise>()),
        ["chest"] = (new[]
        {
            new Tone("triangle", 500, 700, 0.09f, 0.18f),
            new Tone("triangle", 700, 950, 0.10f, 0.16f, 0.09f),
            new Tone("triangle", 950, 1200, 0.14f, 0.14f, 0.19f),
        }, Array.Empty<Noise>()),
        ["gain"] = (new[]
        {
            new Tone("sine", 520, 780, 0.12f, 0.18f),
            new Tone("sine", 780, 1040, 0.16f, 0.14f, 0.10f),
        }, Array.Empty<Noise>()),
        ["heal"] = (new[]
        {
            new Tone("sine", 600, 900, 0.22f, 0.16f),
            new Tone("sine", 900, 1200, 0.24f, 0.10f, 0.12f),
        }, Array.Empty<Noise>()),
        ["reveal"] = (new[] { new Tone("sine", 380, 760, 0.30f, 0.12f) }, Array.Empty<Noise>()),
        ["descend"] = (new[] { new Tone("sine", 300, 140, 0.50f, 0.20f) }, new[] { new Noise(0.40f, 0.10f, 400, 0.05f) }),
        ["victory"] = (new[]
        {
            new Tone("triangle", 523, 523, 0.32f, 0.18f, 0.00f),
            new Tone("triangle", 659, 659, 0.32f, 0.18f, 0.11f),
            new Tone("triangle", 784, 784, 0.32f, 0.18f, 0.22f),
            new Tone("triangle", 1046, 1046, 0.32f, 0.18f, 0.33f),
        }, Array.Empty<Noise>()),
        ["defeat"] = (new[]
        {
            new Tone("sawtooth", 392, 384, 0.40f, 0.16f, 0.00f),
            new Tone("sawtooth", 330, 323, 0.40f, 0.16f, 0.14f),
            new Tone("sawtooth", 262, 257, 0.40f, 0.16f, 0.28f),
            new Tone("sawtooth", 196, 192, 0.40f, 0.16f, 0.42f),
        }, Array.Empty<Noise>()),
        ["deny"] = (new[] { new Tone("square", 200, 150, 0.12f, 0.14f) }, Array.Empty<Noise>()),
        // Boss 出场：一记闷响的低音 + 上行的金属泛音，和普通遇敌的「叮」彻底分开
        ["boss"] = (new[]
        {
            new Tone("sine", 90, 42, 0.90f, 0.30f),
            new Tone("sawtooth", 180, 70, 0.50f, 0.14f, 0.05f),
            new Tone("triangle", 392, 392, 0.50f, 0.10f, 0.35f),
            new Tone("triangle", 523, 523, 0.50f, 0.10f, 0.47f),
            new Tone("triangle", 659, 659, 0.50f, 0.10f, 0.59f),
        }, new[] { new Noise(0.60f, 0.12f, 300, 0.02f) }),
    };

    public static bool HasRecipe(string name) => Recipes.ContainsKey(name);

    /// <summary>
    /// 合成一段 PCM。纯数学、无副作用，headless 也能跑——这就是它能进冒烟的前提。
    /// 振荡器是先「从→到」滑音、再指数衰减包络；噪声是低通白噪 + 线性衰减。
    /// </summary>
    public static float[] Synthesize(string name, int seed = 12345)
    {
        if (!Recipes.TryGetValue(name, out var recipe)) return Array.Empty<float>();
        var total = 0f;
        foreach (var t in recipe.Tones) total = Math.Max(total, t.Delay + t.Dur);
        foreach (var n in recipe.Noises) total = Math.Max(total, n.Delay + n.Dur);
        var length = Math.Max(1, (int)(Samples(total) + 1));
        var buf = new float[length];
        var rng = new Random(seed);

        foreach (var t in recipe.Tones)
        {
            var start = (int)Samples(t.Delay);
            var count = (int)Samples(t.Dur);
            var phase = 0.0;
            for (var i = 0; i < count && start + i < length; i++)
            {
                var p = (float)i / Math.Max(1, count);
                var freq = t.To == t.From ? t.From : Mathf.Lerp(t.From, Math.Max(1f, t.To), p);
                phase += Mathf.Tau * freq / SampleRate;
                var s = t.Wave switch
                {
                    "square" => Math.Sin(phase) >= 0 ? 1f : -1f,
                    "sawtooth" => (float)(2.0 * (phase / Mathf.Tau - Math.Floor(phase / Mathf.Tau + 0.5))),
                    "triangle" => (float)(2.0 * Math.Abs(2.0 * (phase / Mathf.Tau - Math.Floor(phase / Mathf.Tau + 0.5))) - 1.0),
                    _ => (float)Math.Sin(phase),
                };
                // 包络：20ms 起振 + 剩下的指数衰减到 0.0001
                var attack = Math.Min(0.02f, t.Dur * 0.3f);
                var tt = (float)i / SampleRate;
                var env = tt < attack
                    ? Mathf.Lerp(0.0001f, t.Vol, tt / attack)
                    : t.Vol * Mathf.Pow(0.0001f / t.Vol, (tt - attack) / Math.Max(0.001f, t.Dur - attack));
                buf[start + i] += s * env;
            }
        }

        foreach (var n in recipe.Noises)
        {
            var start = (int)Samples(n.Delay);
            var count = (int)Samples(n.Dur);
            var prev = 0f;
            // 一阶低通：截止频率越高，「沙沙」越尖
            var k = Mathf.Clamp(n.Freq / SampleRate, 0.001f, 1f);
            for (var i = 0; i < count && start + i < length; i++)
            {
                var white = (float)(rng.NextDouble() * 2 - 1);
                prev += k * (white - prev);
                var p = (float)i / Math.Max(1, count);
                buf[start + i] += prev * n.Vol * (1f - p);
            }
        }

        for (var i = 0; i < length; i++) buf[i] = Mathf.Clamp(buf[i], -1f, 1f);
        return buf;
    }

    private static float Samples(float seconds) => seconds * SampleRate;
}
