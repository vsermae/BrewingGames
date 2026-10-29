// 敌人立绘：剪影 + 地面投影 + 危险光晕 + 浮动。
//
// TS 版 createMonsterSprite 的做法是「暗色描边剪影垫底 + 亮色剪影叠上来」，
// 小尺寸下也读得出造型；Boss 描边更厚、浮动更慢（视觉上「更沉」）。
// 这里照搬这套层次：影子 → 光晕 → 暗描边 → 本体，全部由 _Draw 现画。
namespace AbyssEcho.UI;

using Godot;

public partial class MonsterSprite : Control
{
    private string _monsterId = "rat";
    private float _size = 96f;
    private float _visual = 1f;
    private bool _isBoss;
    private float _t;
    private float _flash;

    /// <summary>出手动作：正数向前压（攻击），负数向后缩（受击）</summary>
    private float _lunge;
    private bool _lungeForward = true;

    public MonsterSprite()
    {
        MouseFilter = MouseFilterEnum.Ignore;
    }

    /// <summary>
    /// 尺寸交给父级决定：勾上之后 Setup 不再改写自己的框。
    /// 战斗界面按「本体该多大」反推盒子（含浮动与投影余量），
    /// 两处各算一套账的结果就是屏上的怪总比预期小一圈。
    /// </summary>
    public bool SizeFromParent { get; set; }

    /// <summary>
    /// 盒子高 / 本体高。盒子底下那 22% 是浮动（±9）与投影的余量，
    /// 所以按「盒子高」算出来的尺寸永远比画出来的本体大一圈。
    ///
    /// 这个数原来写在 `_Draw` 里，调用方（`BattleScene`）自己又手抄了一份
    /// （`box.Y / 1.22f`）——两处只要有一次改了一个没改另一个，
    /// 「立绘的脚」就会按错误的盒子高去抬，构图当场垮半格（冒烟里差 2px 就是这么来的）。
    /// </summary>
    public const float BoxHRatioConst = 1.22f;

    public void Setup(string monsterId, float size = 96f, bool isBoss = false)
    {
        _monsterId = monsterId;
        _size = size;
        _isBoss = isBoss;
        if (!SizeFromParent)
        {
            CustomMinimumSize = new Vector2(size * 1.4f, size * 1.5f);
            Size = CustomMinimumSize;
        }
        MouseFilter = MouseFilterEnum.Ignore;
        QueueRedraw();
    }

    /// <summary>
    /// 「图形在盒子里占多大」的系数（默认 1）。
    /// 盒子由父级槽位收窄时，图形不该跟着一起缩成芝麻——立绘在卡里只剩 40px 高，
    /// 玩家就认不出造型了。所以盒子照缩，图形按系数自己撑出盒子，压到边框也不管：
    /// 这一条是截图里「卡左上角一只老鼠，下面一大片空白」的修法。
    /// </summary>
    public void SetVisualScale(float k) => _visual = Mathf.Max(0.5f, k);

    /// <summary>是否正在做动作（出手/受击），以及方向：测试用它核对「出手」与「挨打」没写反</summary>
    public bool IsActing => _lunge > 0f;

    /// <summary>true = 前冲（攻击），false = 后仰（受击）</summary>
    public bool LungesForward => _lungeForward;

    /// <summary>
    /// 脚下投影的中心（控件坐标）。它不在造型里，所以按包围盒反推盒子时不会有它：
    /// 没跟着那只怪一起走的话，光斑缩在框中间，而怪站在它下面——「离地」就成了「飘着」。
    /// 冒烟拿它和脚的位置对一遍。
    /// </summary>
    public Vector2 ShadowAnchor { get; private set; }

    /// <summary>
    /// 画出来的脚在控件坐标里的 y（不含浮动那一口呼吸）。
    ///
    /// 它是「渲染真的会用的那个数」：`BattleScene` 把盒子摆在哪、光斑画在哪，
    /// 都只能拿它去对——拿盒子底边去对等于比余白，而余白随各自盒子高变；
    /// 拿设计框去对更糟：矮胖造型（巨鼠 37×21.6）横向先到顶、竖着剩一大截，
    /// 光按设计框算出来的位置和画出来的能差二十几像素。
    /// </summary>
    public float FootLocalY { get; private set; }

    /// <summary>被打中时闪一下白：纯视觉反馈，不影响任何数值</summary>
    public void Flash() => _flash = 1f;

    /// <summary>向前冲一下：出手的时候立绘得动，否则「打中了」只有数字在变</summary>
    public void Lunge() { _lunge = 1f; _lungeForward = true; }

    /// <summary>向后缩一下：被击中/格挡时用，方向与攻击相反，一眼能看出谁在挨打</summary>
    public void Recoil() { _lunge = 1f; _lungeForward = false; }

    public override void _Process(double delta)
    {
        _t += (float)delta;
        if (_flash > 0f) _flash = Mathf.Max(0f, _flash - (float)delta * 4f);
        if (_lunge > 0f) _lunge = Mathf.Max(0f, _lunge - (float)delta * 3.2f);
        QueueRedraw();
    }

    public override void _Draw()
    {
        var kind = EnemyArt.IconKind(_monsterId);
        var style = EnemyArt.Style(_monsterId);
        // 越靠后的 Boss 浮动越慢；普通敌人轻快
        var floatDur = (float)(style?.Float ?? (_isBoss ? 1.8 : 1.3));
        var breathDur = (float)(style?.Breath ?? (_isBoss ? 1.6 : 2.1));
        var lift = _isBoss ? 9f : 6f;
        var bob = Mathf.Sin(_t / floatDur * Mathf.Tau) * lift;
        var breath = 0.5f + 0.5f * Mathf.Sin(_t / breathDur * Mathf.Tau);

        // 画多大由「控件框」决定，不是由 Setup 时的 _size 决定：
        // 父级（敌人卡槽位）会把框按卡高收窄，这时候还按 _size 画，
        // 图形就会从框里溢出去——上一版截图里立绘压住名字行就是这么来的。
        //
        // 框的分配（和 BattleScene.PlaceSprites 里的盒子尺寸是同一套账）：
        //   横向：本体宽 = 框宽 / BOX_W_RATIO（1.12）
        //   竖向：本体高 = 框高 / BOX_H_RATIO（1.22）——多出来的那一份是浮动（±9）
        //         与光晕/投影的余量，所以中心要往上提一点，脚才落得准。
        // 比例写在这里而不是调用方：上一版一处按 1/1.5 算、另一处按 size×1.4 定盒子，
        // 两套账对不上，屏上的怪只有骑士的七成高，「一只巨鼠比人小一圈」。
        const float BoxWRatio = 1.12f;
        // 剪影放大倍数。它在**横竖两边**都要占地方（放大以中心为原点，四周各撑出去一份），
        // 不写进盒子的账里，头顶和尾巴就会从盒子里溢出去，压到名字行上或垂进地板。
        const float OutlineK = 1.12f;
        var graphicW = Size.X / BoxWRatio;
        var graphicH = Size.Y / BoxHRatioConst;
        // 盒子要装的是**这只怪在框里占的那一块**，不是整个 32 设计框。
        // 巨鼠只占中间 24×24、霜蚀女王 27×30：同一把 32 去换算，
        // 屏幕上的巨鼠就比骑士小一档（这条在画面上摆了三十多轮，一直写「留给下一个人」）。
        // 按包围盒反推之后，「巨鼠比骑士小」与「女王比骑士大」才都由数据决定，
        // 而不是由「谁的设计图占得满」决定。
        var bounds = IconArt.Bounds(kind);
        // 造型在框里多半不居中（巨鼠的尾巴伸到 -18、头在 -15..-3，重心偏左下），
        // 所以不能「按中心对齐」：比例尺要按包围盒的真实跨度算，
        // 再把这一个盒子的**脚**钉在框底——否则同一套尺寸下，有的怪悬空、有的怪踩进地板。
        var spanX = Mathf.Max(4f, bounds.X * OutlineK);
        var spanY = Mathf.Max(4f, bounds.Y * OutlineK);
        // 比例尺：盒子容得下多大就画多大，横竖两边取小的那个（不拉伸）。
        //
        // 「取小的那个」本身没错——不拉伸是必须的。错的是**同时**把它当成脚的位置：
        // 横竖两个比值不相等时（矮胖的造型永远是这样：巨鼠 37×21.6，横向先到顶），
        // 竖着还剩一大截没用掉，而脚钉在「竖着用了多少」的那个位置上——
        // 于是屏上一只巨鼠浮在半空，影子落在它脚下面 24px 处，读作「它飘着」。
        // 上一轮量像素的判据（比包围盒、比脚与光斑）都绿着，因为它们量的都是「设计上该在哪」，
        // 而这个偏移是「画的时候真的在哪」——只有看图、或者问渲染要脚的位置才看得见。
        var s = Mathf.Min(graphicW / spanX, graphicH / spanY) * _visual;
        // 剪影放大 12% 之后，它的「脚底」在缩放后的设计坐标里落在 MaxY*OutlineK 处。
        // 把这个点对到几何框的底边（留 6% 给地面投影），脚才真的踩在框底：
        // 比例尺取小的那个之后，`bottomInDesign * s` 就是**画出来**的那一截，
        // 不是「框里剩下的那一截」。
        var bottomInDesign = bounds.Y * 0.5f * OutlineK;
        var c = new Vector2(Size.X * 0.5f, Size.Y - graphicH * 0.06f - bottomInDesign * s);
        // 投影、光晕、头顶高光的半径都跟着图形一起放大，否则小盒子里会出现
        // 「一只大老鼠配一小圈光环」这种比例失配。
        // size 是「本体在设计坐标下的直径」，从框反推，而不是用 Setup 时的旧 _size：
        // 容器布局跑完之前 _size 和 Size 不是一回事，拿旧值算出来的光晕会大小失配。
        var size = Mathf.Min(graphicW, graphicH) * 0.62f;
        var tint = style?.Tint ?? (_isBoss ? new Color("ff6b6b") : EnemyArt.Tint(_monsterId));
        var auraColor = style?.Aura ?? (_isBoss ? new Color("ff3b3b") : EnemyArt.FamilyColor(_monsterId));

        // 地面投影：位置不跟着浮动走（bob 只加在 center 上），所以立绘才有「离地」的错觉。
        // 它锚在 c 上而不是框中心：尺寸按包围盒反推之后，c 会跟着怪的高矮上下走，
        // 投影留在原地的话，矮的怪就踩不到自己的影子了。
        ShadowAnchor = c + new Vector2(0, size * 0.36f);
        // 脚就是**影子**那一条线，不是「设计框的底边」。
        //
        // 造型的底边在设计框里是四只脚的 y=12，而地面投影画在 size*0.36 处——
        // 那是另一条线：立绘站在地上时，脚底该落在影子的中心，不是影子下面 24px。
        // 第一版按设计底边算，屏上的巨鼠浮在它自己的影子上方一大截（改对了比例尺才发现）。
        FootLocalY = ShadowAnchor.Y;
        Paint.Ellipse(this, ShadowAnchor, size * 0.4f, size * 0.11f, new Color(0, 0, 0, 0.42f), 24);

        // 危险光晕：Boss 更大更艳，普通敌人用自己的族系色
        var auraR = size * (_isBoss ? 0.775f : 0.59f);
        Paint.Ellipse(this, c + new Vector2(0, size * 0.40f), auraR, auraR * 0.22f,
            Paint.Alpha(auraColor, (_isBoss ? 0.20f : 0.11f) * (0.7f + 0.3f * breath)), 28);

        // 四层，从后往前压：
        //   1) 暗色描边剪影（放大 10%，只画外轮廓）——小尺寸下的可读性靠这一步
        //   2) 亮面剪影（沿左上错开 2px、提亮一档、半透明）——「光从左上来」的体积感
        //   3) 本体（带全部细节：鳞片、裂甲、发光缝）
        //   4) 受击白闪叠一层
        // 上一版第 1 层其实是把整幅细节图缩着画了一遍，等于同一堆笔画叠两次，
        // 边缘糊成一圈毛。改成只描外轮廓后，剪影干净了，细节也腾出了位置。
        // 出手位移：一次 0.31 秒的往返，攻击朝画面右侧（朝着玩家），受击朝左。
        // 用 sin 而不是线性插值，起手和收回都不生硬。
        var punch = Mathf.Sin(_lunge * Mathf.Pi) * (_isBoss ? 26f : 18f);
        var lean = (_lungeForward ? punch : -punch * 0.7f);
        var center = c + new Vector2(lean, bob);
        DrawSetTransform(center, 0, new Vector2(s * OutlineK, s * OutlineK));
        IconArt.Outline(this, kind, 32f, Paint.Darken(tint, _isBoss ? 0.5f : 0.58f));
        // 亮面：只是把同一张剪影整体提亮再往左上挪一点，所以它不是「描边」而是
        // 「被照亮的那一侧」。右下方留给上面的暗描边露出来，方向就一致了。
        var edgeOffset = new Vector2(-size * 0.022f, -size * 0.028f);
        // 亮面这一层用本体的比例（不跟 OutlineK）：它和本体一样大，只整体挪一点位置，
        // 挪出去的那点才是「被照亮的一侧」；跟着放大就成了第二层剪影。
        DrawSetTransform(center + edgeOffset, 0, new Vector2(s, s));
        IconArt.Draw(this, kind, 32f, Paint.Alpha(Paint.Lighten(tint, 0.32f), 0.55f));
        DrawSetTransform(center, 0, new Vector2(s, s));
        IconArt.Draw(this, kind, 32f, _flash > 0f ? tint.Lerp(Colors.White, _flash * 0.8f) : tint);
        if (_flash > 0f)
        {
            // 白闪时只再叠一层半透明本体，而不是把整幅重画成纯白——纯白会把细节全糊掉
            DrawSetTransform(center, 0, new Vector2(s, s));
            IconArt.Draw(this, kind, 32f, new Color(1f, 1f, 1f, _flash * 0.45f));
        }
        // 头顶一抹冷高光：像灯打在头顶，Boss 更亮一点。它是「看一眼就知道谁显眼」的
        // 那一层，也是让剪影从背景里跳出来的最后一笔。
        Paint.Ellipse(this, center + new Vector2(-size * 0.06f, -size * 0.3f),
            size * 0.21f, size * 0.1f, new Color(1, 1, 1, _isBoss ? 0.16f : 0.1f), 24);

        DrawSetTransform(Vector2.Zero, 0, Vector2.One);

        // 头顶一圈「被集火」的提示不在这里画：那是选择态，属于战斗界面。
    }
}
