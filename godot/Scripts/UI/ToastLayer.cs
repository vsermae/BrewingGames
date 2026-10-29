// 浮动提示的显示层：队列规则在 ToastRules / ToastQueue，这里只管「把队首画出来」。
//
// TS 版是每条提示建一个 Phaser 容器 + 两条 tween；Godot 里那条路等于
// 「一个提示一个 Control + 一个 Tween」，长按方向键时会瞬间堆出几十个节点。
// 这里改成一个节点画全部：文字用 Label，底框与描边自己画，同一时刻只有一条在演。
//
// 一条提示的生命周期 = 淡入(0.12s) → 停留(duration) → 淡出(0.35s)。
// 演完必须走 Queue.Dequeue()：这是唯一能把「正在演」这只标记放下的地方。
namespace AbyssEcho.UI;

using Godot;

public partial class ToastLayer : Control
{
    private readonly ToastQueue _queue = new();

    private Label _label = null!;
    private float _phase;      // 0=淡入 1=停留 2=淡出
    private float _elapsed;
    private float _timer;
    private float _alpha;
    private float _rise;
    private Color _tint = Palette.Text;

    private string? _lastText;
    private float _lastAt;

    /// <summary>
    /// 底下那条提示带的高度。地图把它设成 HintBar 的高：
    /// 胶囊再怎么挪，也不许压到玩家正在读的那一行提示（见 `ToastRules.PillTopFor`）。
    /// </summary>
    public float HintBandHeight { get; set; }

    /// <summary>
    /// 提示不许压到的那条线：这一帧地图网格的下沿（由地图在每次重排后写进来）。
    ///
    /// 上一版按「提示条上沿 - 14」定位，而地图是垂直居中的——1280×720 下最后一排
    /// 房间砖压到 y≈628，胶囊顶边算出来 611，两条横着交叉，一格砖被吃掉一半。
    /// 0 表示没有网格（战斗、主菜单这类场景），退回「贴着提示条」。
    /// </summary>
    public float GridBottom { get; set; }

    /// <summary>网格上沿（同一份来源）：底下塞不下时胶囊挪到它下面去</summary>
    public float GridTop { get; set; }

    /// <summary>
    /// 顶部 HUD 的下沿。它是「网格上方那条空带」的上边界——
    ///
    /// 没有它的时候，胶囊只按「离网格上沿 Clearance」摆，1280×720 下算出来 59，
    /// 而 HUD 占到 74：胶囊的描边正好横穿 HUD 那条贴边发光线（截图里看得见，
    /// 但判据只查了「压没压到砖与图例」，HUD 不算）。
    /// </summary>
    public float HudBottom { get; set; }

    /// <summary>地图这一层画到哪为止（网格下沿与右下角图例里更靠下的那一条）</summary>
    public float BelowFloor { get; set; }

    /// <summary>胶囊顶边的屏幕高度（给断言用：它得在房间砖、图例与顶部 HUD 之外）</summary>
    public float PillTop => ToastRules.PillTopFor(Size.Y > 1f ? Size.Y : 720f, HintBandHeight,
        BelowFloor > 0f ? BelowFloor : GridBottom, GridTop, HudBottom);

    /// <summary>
    /// 这一颗胶囊**实际**能往上飘多少。
    ///
    /// 上飘是落点的后果，不是落点的前提：带子只给得出 54px 时，
    /// 胶囊顶边在 74、再飘 34 就穿进 HUD 了。所以「飘多少」必须和「摆在哪」同源——
    /// 分开算一次（比如这里再抄一遍 `RiseTotal`），就是又一个「两套账」。
    /// 截图工具判「含飘起来的」时读的是这个数，不是常量。
    /// </summary>
    public float RiseForShot => Math.Clamp(PillTop - (HudBottom + ToastRules.Clearance), 0f,
        ToastRules.RiseTotal);

    /// <summary>这一颗胶囊这一帧实际往上飘了多少（`_rise`）。判据读它，不读设计值</summary>
    public float CurrentRise => _rise;

    /// <summary>
    /// 这一颗胶囊的**飞行区间**：它在这个生命周期里最靠上的那条边。
    ///
    /// 不能拿「当前帧的 `_rise`」和「落点额度」里小的那个糊过去：淡入那一帧
    /// `_rise` 才 4px（`RiseIn × alpha`），而 0.12s 之后就飘到 12——判据挑到的是
    /// 最安静的那一帧。上一版就是这么绿的：量了一个「此刻」去回答「这一整段」。
    /// </summary>
    public float FlightTop => PillTop - _riseCap;

    /// <summary>演完之后把额度复位：下一颗的落点可能不一样</summary>
    private void ResetRiseCap() => _riseCap = ToastRules.RiseTotal;

    /// <summary>这一颗的飘升额度（落点给的），按落点与设计值取小</summary>
    private float _riseCap = ToastRules.RiseTotal;


    /// <summary>
    /// 胶囊底框的实际矩形（控件坐标）。截图工具拿它和房间砖逐块比：
    /// 上一版落点 0.66 屏高，1280×720 下正好盖住最后一排地砖，而没有任何断言问过这件事。
    /// 没在演时退回一个空矩形。
    /// </summary>
    public Rect2 PillRect
    {
        get
        {
            if (!_queue.Playing || _alpha <= 0.01f) return new Rect2();
            var w = Size.X > 1f ? Size.X : 1280f;
            var textW = _label.GetThemeFont("font")
                .GetStringSize(_label.Text, HorizontalAlignment.Left, -1, _label.GetThemeFontSize("font_size")).X;
            var pillW = Mathf.Min(w - 32f, textW + 44f);
            return new Rect2(w * 0.5f - pillW * 0.5f, PillTop - _rise, pillW, ToastRules.PillHeight);
        }
    }

    /// <summary>
    /// `Layout()` 刚刚写给文字的那个框（控件坐标）。
    ///
    /// 判据要问「字跟着胶囊走」，而这件事取决于 `Layout()` 写了什么——
    /// 不是取决于层里有没有一个 Label（提示层里有不止一个）。所以由它自己报。
    /// </summary>
    public Rect2 LabelBox => new(_label.Position, _label.Size);

    /// <summary>给断言用：当前有没有一条在演、排队里还有几条</summary>
    public bool Playing => _queue.Playing;
    public int Pending => _queue.Pending;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _label = UiTheme.Text("", 17, Palette.Gold);
        _label.HorizontalAlignment = HorizontalAlignment.Center;
        _label.VerticalAlignment = VerticalAlignment.Center;
        _label.MouseFilter = MouseFilterEnum.Ignore;
        _label.Modulate = new Color(1, 1, 1, 0);
        AddChild(_label);
    }

    /// <summary>
    /// 弹一条提示。同一个场景里同一条消息在节流窗口内只弹一次——
    /// 键盘长按撞墙时会以几十次/秒的速率重发 keydown，不去重就是满屏重复。
    /// </summary>
    public void Show(string text, Color? tint = null, float duration = ToastRules.DefaultDuration)
    {
        if (string.IsNullOrEmpty(text)) return;
        var now = Time.GetTicksMsec() / 1000f;
        if (ToastRules.ShouldSuppress(text, _lastText, now - _lastAt)) return;
        _lastText = text;
        _lastAt = now;
        if (_queue.Enqueue(new ToastItem { Text = text, Tint = tint, Duration = duration })) Pump();
    }

    /// <summary>
    /// 手动推一帧（给断言用）。`_Process` 要等场景树真的开始跑才会有 delta，
    /// 而冒烟里造出来的层只挂了一两帧，有时连淡入的门槛都过不去。
    /// </summary>
    public void StepForTest(float delta) => _Process(delta);

    /// <summary>场景要销毁 / 重开时收干净：残留的「正在演」会把之后所有提示吞掉</summary>
    public void Clear()
    {
        _queue.Reset();
        _label.Modulate = new Color(1, 1, 1, 0);
        _alpha = 0f;
    }

    private void Pump()
    {
        if (!_queue.Begin()) return;
        var item = _queue.Current!;
        // 每条用自己的颜色与时长，不借队首的：红条的「那边没有路」后面
        // 排着金条的「获得金币」时，金币那条必须是金的。
        _tint = item.Tint ?? Palette.Gold;
        _label.Text = item.Text;
        _label.AddThemeColorOverride("font_color", _tint);
        _phase = 0;
        _elapsed = 0;
        _timer = item.Duration;
        _alpha = 0f;
        _rise = 0f;
        // 飘升额度：设计值的两段，但被落点压住时按落点收（`RiseForShot`）。
        // 这一颗演完之后 `_riseCap` 会被复位成设计值——否则下一条提示会沿着
        // 上一条被压小的额度飘，屏幕上看起来像「第二条提示不敢动」。
        _riseCap = MathF.Max(ToastRules.RiseIn, MathF.Min(ToastRules.RiseTotal, RiseForShot));
        Layout();
        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (!_queue.Playing) return;
        var dt = (float)delta;
        _elapsed += dt;
        switch (_phase)
        {
            case 0:
                _alpha = Mathf.Min(1f, _elapsed / 0.12f);
                // 上飘量按**这一颗的额度** `_riseCap` 走，不写死 `RiseIn`。
                // 写死的话，第二条提示会把第一条收下来的额度扔掉，直接飘满设计值——
                // 也就是「带子只给得出 4px 的那一屏，第二句话照样穿进 HUD」。
                _rise = Mathf.Min(_riseCap, _riseCap * _alpha);
                if (_elapsed >= 0.12f) { _phase = 1; _elapsed = 0; _alpha = 1f; _rise = _riseCap; }
                break;
            case 1:
                _timer -= dt;
                if (_timer <= 0f) { _phase = 2; _elapsed = 0; }
                break;
            default:
                _alpha = Mathf.Max(0f, 1f - _elapsed / 0.35f);
                // 淡出的那一段只走「额度里剩下的那一截」：额度是落点给的，
                // 它已经保证「飘完还在带子里」，这里不许再加一段设计值。
                _rise = _riseCap + MathF.Max(0f, _riseCap - ToastRules.RiseIn) * (1f - _alpha);
                if (_elapsed >= 0.35f)
                {
                    _queue.Dequeue();
                    ResetRiseCap();
                    if (_queue.Playing) Pump();
                    else { _label.Modulate = new Color(1, 1, 1, 0); _alpha = 0f; QueueRedraw(); }
                }
                break;
        }
        if (_queue.Playing)
        {
            _label.Modulate = new Color(1, 1, 1, _alpha);
            Layout();
            QueueRedraw();
        }
    }

    /// <summary>
    /// 文字居中在胶囊里。上一版是「放在中线上方 16px 处」——胶囊按中线往下铺，
    /// 字按中线往上摆，两边各算各的；现在字的位置从 `PillRect` 里读，
    /// 胶囊挪到哪，字跟着到哪。
    /// </summary>
    private void Layout()
    {
        var rect = PillRect;
        // 字的框**就是胶囊的内区**，不是「中线上方 16px，高 32」。
        //
        // 那个 16/32 是胶囊高 32 那版留下的：字高 32、往上抬 16，正好居中。
        // 竖排居中本来就该由 `VerticalAlignment` 说，而不是靠「框高恰好等于字高」——
        // 换了字号、换了胶囊高，字就贴着上沿；胶囊往上飘的时候，
        // 字按落点算、框按 `_rise` 算，两边各算一次，飘到一半会脱开半格。
        // 现在字框从胶囊里读：胶囊挪到哪、飘到哪，字跟着到哪，一个像素都不另算。
        var w = Size.X > 1f ? Size.X : 1280f;
        _label.Position = new Vector2(0, rect.Position.Y);
        _label.Size = new Vector2(w, rect.Size.Y);
    }

    /// <summary>胶囊底框：跟着文字宽度走，而不是一条通栏横条</summary>
    public override void _Draw()
    {
        if (!_queue.Playing || _alpha <= 0.01f) return;
        var rect = PillRect;
        if (rect.Size.X <= 0f) return;
        Paint.RoundRect(this, rect, rect.Size.Y * 0.5f, new Color(0.05f, 0.07f, 0.13f, 0.94f * _alpha));
        Paint.RoundRectOutline(this, rect, rect.Size.Y * 0.5f, Paint.Alpha(_tint, 0.55f * _alpha), 1.5f);
    }
}
