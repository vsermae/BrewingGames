// 战斗界面的布局计算：纯函数，不碰 Godot。
//
// 为什么单独拆出来：TS 版把「行动条高度」写死在样式里，窗口一变矮就压住敌人区——
// 这是纯算术问题，不该只有肉眼能验。搬进纯函数后，UiSmoke 能直接断言
// 「1280x720 下 262」「720 高以下按比例收」「不会低于最小可用高度」。
namespace AbyssEcho.UI;

public static class BattleLayout
{
    /// <summary>
    /// 算出来的一份布局。嵌套在类里而不是并列的 record：
    /// 两者同名时 C# 会报 CS0101（namespace 里已有同名定义），
    /// 而「BattleLayout.Compute 返回 BattleLayout」读起来最顺，所以让类型跟着工具类走。
    /// </summary>
    public sealed record Layout(float ActionBarHeight, float EnemyTop, float EnemyBottom, float StageFloor = 0f,
        float StageTopY = 0f, float StageBottomY = 0f)
    {
        /// <summary>日志区满高：够放下 7 行（96），矮窗口退一档到 6 行（80）</summary>
        public float LogHeight => ActionBarHeight >= DesignActionBar ? 96f : 80f;

        /// <summary>日志最多显示几行</summary>
        public int LogLines => ActionBarHeight >= DesignActionBar ? 7 : 6;

        /// <summary>
        /// 这一帧日志面板该占的高度：按实际行数算，最多到满高。
        /// 开战第一屏只有「遭遇 XX！」一行，面板以前照样占 96px —— 中间那块空白
        /// 看起来像「日志没画完」，还白白吃掉能放大立绘的那一百来像素。
        /// </summary>
        public float LogHeightFor(int lineCount) => Math.Clamp(lineCount, 1, LogLines) * (LogHeight / LogLines);

        /// <summary>
        /// 日志面板的总高度 = 文本行 + 上下内边距（10）。
        /// 单独给一个名字，是因为「面板比文字高 10」这件事以前散在界面代码里，
        /// 而行动条的配额是按文字行算的——差值攒起来正好把按钮行挤出屏幕。
        /// </summary>
        public float LogPanelHeight => LogHeight + LogPanelPadding;

        /// <summary>行动条里除日志之外那几行的高度：状态行 + 血条行 + 按钮行 + 行间距</summary>
        public float ActionBarChrome => StatusRowHeight + HpRowHeight + ActionRowHeight + ActionBarSeparation * 3;

        /// <summary>行动条内容的实际总高：配额必须大于等于它，否则底下的按钮会被裁掉</summary>
        public float ActionBarContentHeight => LogPanelHeight + ActionBarChrome;

        /// <summary>
        /// 顶部敌人卡的实际高度：卡里要塞「立绘 + 名字 + 血条 + 血量 + 下一刀预告 + 状态行」，
        /// 高度不能无脑用设计值——敌人区被压到不够时先收卡，再收不动就让 Compute 赔高度。
        /// </summary>
        public float EnemyPanelHeight =>
            Math.Min(BattleLayout.EnemyPanelHeight, Math.Max(MinEnemyPanel, EnemyBottom - EnemyTop - MinStage));

        /// <summary>
        /// 这张卡够不够装下六行内容（不含立绘宽度约束）。
        /// 界面按它决定「要不要把技能预告折成一行」，而不是让内容自己溢出边框。
        /// </summary>
        public bool EnemyPanelFitsContent => EnemyPanelHeight >= MinEnemyPanelFit;

        /// <summary>六行内容（名字/血条/数值/下一刀/技能/状态）压到极限需要的高度（含内边距）</summary>
        public const float MinEnemyPanelFit = 88f;

        /// <summary>
        /// 敌人卡上「伤害预告」那一行能折成几行。
        ///
        /// 这一行的长度是随机制长的：`下一刀预计 X~Y 点｜含中毒 N×M 回合，含自爆 K，
        /// 本回合合计 A~B 点｜别在你面前倒下` —— 1280 宽下刚好吃满一行，
        /// 而卡高是按六行内容算死的（`EnemyPanelHeight`）。多折一行，下面那句技能预告
        /// 就被挤出卡外，而它一个字都没报错，只是没人看得见。
        ///
        /// 所以先把它算出来：只有两种结局，**装得下**（1 行）与**这张卡放不了两行**（1 行，
        /// 但界面要把话收短）。永远返回 1 是「这一行不许折」的另一种写法——
        /// 与其让布局去兜一个装不下的东西，不如让写这句话的人先知道它装不下。
        /// </summary>
        public int ForecastLines => 1;

        /// <summary>
        /// 第 `row` 行文字（1 起）的基线相对于内容区顶边的位置，*不含*控件坐标。
        ///
        /// 这一份算术存在只有一个理由：截图里那一块纯白（见 `HeadlessSelfCheck` 的
        /// 白块诊断）一直被猜成「画错了」与「取到了上一帧」两种解释，
        /// 而**排错位置**这一种从来没被验过——因为没有人把它算出来过。
        /// 现在算了：名字 22、血条 18、其余各 13，行距 3（`topBox` 的 separation）。
        /// 判据拿它和像素里的白块上下沿比，一次就能分开「排到了卡外」与「渲染没跟上」。
        /// </summary>
        public float CardRowTop(int row)
        {
            const float nameSize = 22f, hpBar = 18f, smallSize = 13f, sep = 3f;
            var y = 0f;
            for (var i = 1; i < row; i++)
            {
                y += i switch { 1 => nameSize, 2 => hpBar, _ => smallSize } + sep;
            }
            return y;
        }

        /// <summary>这一行文字自己的高度（字号）：与 `CardRowTop` 用同一张尺寸表</summary>
        public static float CardRowHeight(int row) => row switch { 1 => 22f, 2 => 18f, _ => 13f };

        /// <summary>
        /// 舞台的中心线：敌人卡底边与行动条顶边之间的中点。
        /// 立绘、粒子、飘字都以它为准——以前敌人立绘贴着屏幕顶端、玩家立绘贴着行动条，
        /// 两者中间空出两百多像素，战斗画面像是两行贴纸。
        /// </summary>
        public float StageCenterY => (StageTop + StageFloor) / 2f;

        /// <summary>舞台顶边：敌人卡的底边（EnemyBottom 就是卡的底边）</summary>
        public float StageTop => StageTopY > 0 ? StageTopY : EnemyBottom;

        /// <summary>舞台底边：行动条的上沿</summary>
        public float StageBottom => StageBottomY > 0 ? StageBottomY : (StageFloor > 0 ? StageFloor : EnemyBottom + MinStage);

        /// <summary>舞台高度：立绘按它取尺寸，矮窗口下会收</summary>
        public float StageHeight => Math.Max(MinStage, StageBottom - StageTop);

        /// <summary>
        /// 血条行里立绘能用多高：行高减掉面板上下内边距（2×2）与一点余量。
        /// 这一行是固定高的，里面的控件超过它就会把整条行动条撑开，
        /// 而多出来的高度由下面的按钮行承担——按不了按钮比立绘小一点严重得多。
        /// </summary>
        public float HpRowBudget => HpRowHeight - 8f;

        /// <summary>
        /// 这一帧的视口高。以前界面里有几处要「按整屏高取档」（玩家立绘、行高），
        /// 拿不到它就退化成「按舞台高算」——舞台只占屏幕的一小半，
        /// 于是 720 高下玩家立绘只有 51px，屏幕上是个贴纸。
        /// 现在由 Compute 把原始高度带出来，界面不用再猜。
        /// </summary>
        public float ViewportHeight { get; init; } = 720f;

        /// <summary>行动条顶边（= 舞台底边）：玩家的站位与连线从这里往下算</summary>
        public float ActionBarContentTopY => StageBottom;
    }

    /// <summary>
    /// 卡里那几行的**语义**顺序（自上而下）。界面按这个顺序建控件，
    /// 截图判据按它读「谁在谁上面」——文字的**基线**不去猜控件坐标，
    /// 而是问渲染器「这个字号下这一行该多高」，两者相加就是它该落的那条线。
    /// </summary>
    public static readonly string[] EnemyCardRows =
        { "name", "hpbar", "stats", "forecast", "skills", "status" };

    /// <summary>1280x720 下的设计值（保留旧名字：外部按它判断「用的是设计值」）</summary>
    public const float DesignActionBar = DesignActionBar720;

    /// <summary>
    /// 再挤也不能低于这个：低于它日志只剩一两行，玩家读不到「刚才发生了什么」。
    /// 196 → 214 是因为状态行、按钮行都是后来补的（分别对应「状态行 1px 宽」和
    /// 「按钮行被裁掉」两条截图问题），配额不跟着涨就会从别处抠。
    /// </summary>
    public const float MinActionBar = 214f;

    /// <summary>
    /// 顶部敌人卡的设计高度：内容（名字 33 + 血条 20 + 数值 20 + 下一刀 20 + 技能 18 + 状态行 22 + 五处 4px 间距）
    /// 加上上下内边距（10 + 8）。以前写 118 却没算内边距，卡的实际高度是 143——
    /// 多出来的 25px 从舞台里扣，立绘与横幅就压到彼此头上。
    /// 这个数现在是「内容 + 内边距」，界面只负责把内容按它排版。
    ///
    /// 132 → 100（卡高 150 → 118）是第四十八轮按截图定的：卡的左端那条立绘槽
    /// 从 132 收到 18 之后，六行内容从上到下只排到 y≈104，卡面剩下的 46px 是**空的**——
    /// 而屏幕上读到的是「卡的下半截刷地变白」，不是「卡比较高」。
    /// 卡里没有槽了，那 132 是 TS 版「卡里立绘 132 宽」留下的高度余量，不是一个内容尺寸。
    /// 收掉的 32px 全部给了舞台（这一轮的另一处：站在舞台上的人的脚踝本来在行动条后面）。
    /// </summary>
    public const float EnemyPanelHeight = 96f + EnemyPanelPadding;

    /// <summary>敌人卡上下内边距之和</summary>
    public const float EnemyPanelPadding = 15f;

    /// <summary>日志面板上下内边距之和（与 StyleBoxFlat 里的 ContentMargin 一致）</summary>
    public const float LogPanelPadding = 10f;

    /// <summary>玩家状态行高度（防御中 / 攻击 +40% / 中毒 N 回合）</summary>
    public const float StatusRowHeight = 18f;

    /// <summary>血条行高度：玩家立绘 56 + 字号 20，取大者</summary>
    public const float HpRowHeight = 56f;

    /// <summary>按钮行高度</summary>
    public const float ActionRowHeight = 46f;

    /// <summary>行动条内各行之间的间距</summary>
    public const float ActionBarSeparation = 8f;

    /// <summary>
    /// 1280x720 下的行动条设计高度：日志面板 106（7 行 96 + 内边距 10）+ 状态行 18
    /// + 血条行 56 + 按钮行 46 + 三处 8px 间距 = 250。
    /// 这个数不是拍脑袋定的，是 ActionBarChrome + LogPanelHeight 加出来的：
    /// 以前写 232、内容要 250，差的那 18px 正好把按钮行推出屏幕底部。
    /// </summary>
    public const float DesignActionBar720 = 250f;

    /// <summary>敌人卡再怎么收，也得放得下名字与血条</summary>
    public const float MinEnemyPanel = 84f;

    /// <summary>舞台（立绘、粒子、飘字）至少要有这么高，否则看不见「打中了」</summary>
    public const float MinStage = 150f;

    /// <summary>舞台上沿与敌人卡之间留的空隙</summary>
    public const float StageTopSpacing = 12f;

    /// <summary>
    /// 舞台地板线距行动条上沿的余量：地板线**下面**还得画得下「地面」。
    ///
    /// 落点光斑的中心比地板线低 `Arena.StanceDrop`（62），它自己还有 `Arena.SpotRy`（22）
    /// 的纵半径——所以地面要 62 + 22 + 4 = 88px 才装得下那两块光斑。
    /// 以前这里写死 18、Arena 那边写死 62，两个数互不相干：
    /// 1280×720 下地板线在 452、光斑中心落在 514，而行动条上沿在 470——
    /// 光斑和立绘的脚（474）整整齐齐排在行动条**后面**，被 z=10 的面板切掉。
    /// 截图上就是「巨鼠的腿没了，人飘在半空」，摆了好几轮没人当成 bug。
    /// </summary>
    public const float StageFloorInset = Arena.StanceDrop + Arena.SpotRy + 4f;

    /// <summary>
    /// 行动条高度按视口高度自适应。
    /// 设计视口（720）及以上用设计值；更矮的窗口按比例收，收到 MinActionBar 为止。
    /// 这样 1280x720 与 1024x640 两种窗口下，敌人区都不会被行动条盖住。
    /// </summary>
    /// <summary>某个行动条高度对应的日志行高度（96 / 80 两档）</summary>
    private static float LogPanelHeightFor(float bar) => (bar >= DesignActionBar720 ? 96f : 80f) + LogPanelPadding;

    public static Layout Compute(float viewportHeight)
    {
        var h = viewportHeight <= 0 ? 720f : viewportHeight;
        // 顶部敌人卡也随窗口收：720 用设计值，更矮按比例收，下限 MinEnemyPanel。
        // 以前它是常量，矮窗口下行动条、舞台各自让位，唯独卡不让——
        // 结果 540 高时敌人区只剩 156px，立绘与飘字全挤成一条。
        var top = h >= 720f
            ? EnemyPanelHeight
            : Math.Max(MinEnemyPanel, EnemyPanelHeight * h / 720f);
        // 下限不只是「日志够不够」：还要保证「敌人卡 + 舞台」放得下。
        // 540 高时 262*540/720=196，而卡 108 + 舞台 150 + 196 = 454 < 540，够；
        // 但窗口再矮就要靠这一条兜住，否则敌人卡会顶到行动条上。
        var floorByStage = top + MinStage;
        var bar = h >= 720f
            ? DesignActionBar720
            : Math.Max(MinActionBar, DesignActionBar720 * h / 720f);
        bar = Math.Min(bar, Math.Max(MinActionBar, h - floorByStage));
        // 配额绝不能小于内容：小一点点就是以「按钮行被裁掉一半」的形式表现出来，
        // 而裁掉的是玩家最常按的东西。内容和配额是同一个算式的两头，这里兜住它。
        // 内容要按 LogPanelHeightFor(bar) 算——bar 一收，日志就矮一档。
        var chrome = StatusRowHeight + HpRowHeight + ActionRowHeight + ActionBarSeparation * 3;
        var content = LogPanelHeightFor(bar) + chrome;
        bar = Math.Max(bar, content);
        // 敌人区占顶部卡与行动条之间；中间的「舞台」就是立绘、粒子、飘字的活动范围
        var bottom = Math.Max(top, h - bar);
        // 舞台底边：立绘要站得住的「地板线」。
        // 640 高时 570，但敌人卡的底边在 570 以下——上一版直接用底边当舞台底边，
        // 算出来的舞台高度是负的，敌人立绘的脚踩进卡里、头压在名字行上。
        // 地板线取「舞台底边」与「卡底边 + Spacing」的较大值，再夹一次。
        var stageTop = top + StageTopSpacing;
        var floor = Math.Min(Math.Max(bottom - StageFloorInset, stageTop), bottom);
        // 参数顺序：StageTopY = 卡的底边（舞台从卡下面开始），StageBottomY = 地板线（脚踩的那条）。
        // 上一版把 stageTop（卡顶 + 12）传给了 StageTopY，于是「舞台高」被算成
        // 「地板线 − (卡顶+12)」，比真实高度少了整张卡——720 高下只剩 290，
        // 立绘按 78% 取尺寸，屏幕上那只老鼠只有骑士的七成高。
        // 结果就是「越改比例，怪越小」：改的是个已经被算错的基数。
        return new Layout(bar, top, bottom, floor, top, floor) { ViewportHeight = h };
    }
}
