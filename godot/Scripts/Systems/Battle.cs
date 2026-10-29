// 回合制战斗状态机。玩家与敌人轮流行动：玩家的行动由 UI 调用，
// 玩家行动结束后自动推进敌人行动，直到轮回玩家或分出胜负。
//
// 这是纯逻辑类：没有一行 Godot 代码。演出（飘字、粒子、日志着色）
// 走 BattleEvents，UI 订阅后自己表演——所以 headless 自检也能跑完整场战斗。
namespace AbyssEcho.Systems;

using AbyssEcho.Core;
using AbyssEcho.Data;

/// <summary>
/// 战斗阶段。Escaped 是玩家主动撤离：它不是胜负，而是「这场结束了、房间没清」，
/// 单独一个值好过让 UI 靠外部标记去记——漏了标记就会出现「逃跑了还弹胜利面板」。
/// </summary>
public enum BattlePhase
{
    Player,
    Enemy,
    Won,
    Lost,
    Escaped,
}

public sealed class BattleOptions
{
    public CombatRules Rules { get; init; } = null!;
    /// <summary>取当前面板攻击力/防御/最大生命的函数（玩家会随装备变化）</summary>
    public Func<Combatant, int> AttackerAtk { get; init; } = _ => 0;
    public Func<Combatant, int> AttackerDef { get; init; } = _ => 0;
    public Func<Combatant, int> MaxHp { get; init; } = _ => 0;
    /// <summary>本层机制给玩家的攻击补偿（疫病层 +4），缺省 0</summary>
    public int FloorAttackBonus { get; init; }
    /// <summary>本层机制给玩家的暴击补偿（狭窄层 +15%），缺省 0</summary>
    public double FloorCritBonus { get; init; }
    public string FloorMechanic { get; init; } = "none";
    public string? BossMechanic { get; init; }
    public int Floor { get; init; } = 1;
    public ActiveAffixes Affixes { get; init; } = ActiveAffixes.Empty;
    /// <summary>敌人一侧的暴击率加成（周目负面效果「暴戾」+10%）。只加在敌人身上。</summary>
    public double EnemyCritBonus { get; init; }
    /// <summary>组合里的其余敌人：与主敌人共享一个行动条、轮流出手</summary>
    public List<Combatant> Allies { get; init; } = new();
    public Dictionary<Combatant, MonsterDef> EnemyDefs { get; init; } = new();
}

public sealed class Battle : IHitContext
{
    public const int MaxLogLines = 40;

    public BattlePhase Phase { get; private set; } = BattlePhase.Player;
    public List<string> Log { get; } = new();
    public int PlayerHpLossSinceSync { get; set; }
    public string LastEnemyAction { get; private set; } = "attack";
    public Combatant Player { get; }
    public Combatant Enemy { get; }
    public List<Combatant> Allies { get; }
    public int PoisonDamage { get; private set; }
    public string FloorMechanic { get; }
    public BossMechanic? Boss { get; }
    public int Round { get; private set; } = 1;
    public int RoundFloor { get; }
    public string LastMechanicNote { get; private set; } = "";
    public int TargetIndex { get; set; }

    /// <summary>当前集火的目标实例。判「死没死」看它自己，而不是看它在活人列表里的下标</summary>
    private Combatant? _target;
    public int ActiveEnemyIndex { get; private set; }

    private readonly Dictionary<Combatant, MonsterDef> _enemyDefs;
    private readonly Rng _rng;
    private readonly BattleOptions _opts;
    private readonly HashSet<Combatant> _exploded = new();
    /// <summary>
    /// 本回合摆出防御姿态的敌人。
    /// 玩家先手、敌人后手，而防御姿态在 EndRound 一律清零的话，
    /// 敌人这一回合摆出的姿态在玩家下一刀之前就被清掉了——AI 里那 30% 的防御概率成了纯浪费。
    /// </summary>
    private readonly HashSet<Combatant> _guarding = new();
    private int _groupCursor;
    /// <summary>
    /// 这一刀「登记下来、命中之后才收」的反震。0 表示没有。
    /// 为什么不在机制链里直接扣：那条链跑在伤害结算之前，而反震的前提是打中了。
    /// </summary>
    private int _armedReflect;
    private string? _armedReflectLog;
    private readonly ActiveAffixes _affixes;
    private readonly AffixHooks? _affixHooks;
    private readonly double _critBonus;
    private readonly int _floorAttackBonus;
    private readonly double _enemyCritBonus;

    public Battle(Combatant player, Combatant enemy, Rng rng, BattleOptions opts)
    {
        Player = player;
        Enemy = enemy;
        Allies = (opts.Allies ?? new List<Combatant>()).Where(c => c != enemy && c.Hp > 0).ToList();
        // 主敌人必须站在活人名单里。这里以前只筛 `Allies`，主敌人是谁就给谁——
        // 而 `CreateEncounter()` 每次调用都会**重建**一份敌人实例，主敌人的 Hp 又是按
        // 「这一层有没有楼层机制」改了又改的那种对象。探针在同一间房里重开一场战斗时，
        // 拿到的 `Enemy` 还带着上一次被打空的血（实测 `enemy=深渊猎犬/173`、
        // 而创建出来的那一只 Hp 早已是 0）：第一回合 `EnemyAct` 先判血，
        // `LivingEnemies()` 空着，`_groupCursor` 却还在转——玩家一开局就被
        // 「毒爆 + 灵魂汲取」连打四个回合，连一次出手机会都没有。
        // 战斗开局的第一件事是「敌人还活着」，这是入口的责任，不是调用方的。
        if (!LivingEnemies().Contains(Enemy))
            throw new InvalidOperationException($"{Enemy.Name} 进入战斗时生命是 {Enemy.Hp}：这一场打不起来");
        _enemyDefs = opts.EnemyDefs ?? new Dictionary<Combatant, MonsterDef>();
        _rng = rng;
        _opts = opts;
        _affixes = opts.Affixes ?? ActiveAffixes.Empty;
        // 暴击率 = 基础 + 楼层补偿 + 装备词条。楼层补偿（狭窄 +15%）写在机制说明里，
        // 以前只有说明没有调用方，玩家白挨了敌人的 +4 防御。
        _critBonus = opts.FloorCritBonus + _affixes.CritChance;
        _floorAttackBonus = opts.FloorAttackBonus;
        _enemyCritBonus = opts.EnemyCritBonus;
        // 有暴击加成或有词条时才建钩子。判据不能是「有没有词条」——
        // 狭窄层没有任何装备词条也照样该 +15% 暴击。
        _affixHooks = _critBonus > 0 || _affixes.List.Count > 0
            ? new AffixHooks { Affixes = _affixes, CritBonus = _critBonus, TargetIsPlayer = true }
            : null;
        FloorMechanic = opts.FloorMechanic;
        Boss = FloorMechanics.BossMechanicOf(opts.BossMechanic);
        RoundFloor = opts.Floor;
        // 两个「每场战斗一次」的标记要在开场归零：玩家是跨战斗长存的对象。
        Player.RiskHitTaken = false;
        Player.UsedSurviveLethal = false;
        PushLog($"遭遇 {enemy.Name}！");
    }

    /// <summary>
    /// 敌人实例 → 它的模板。界面用它把「自爆 / 加护 / 反伤 / 附霜」这些规则
    /// 摊给玩家看：这些字段一直躺在 game_data 里，只是以前没人读。
    /// </summary>
    public IReadOnlyDictionary<Combatant, MonsterDef> Defs => _enemyDefs;

    private void PushLog(string line)
    {
        Log.Add(line);
        if (Log.Count > MaxLogLines) Log.RemoveRange(0, Log.Count - MaxLogLines);
        BattleEvents.EmitLog(line);
    }

    /// <summary>组合里全部敌人（含倒下的）：自爆、光环找队友都从这里筛</summary>
    public List<Combatant> Enemies() => new[] { Enemy }.Concat(Allies).ToList();

    /// <summary>还活着的敌人：玩家只能集火活人，行动轮转也只看活人</summary>
    public List<Combatant> LivingEnemies() => Enemies().Where(c => c.Hp > 0).ToList();

    /// <summary>
    /// 玩家当前选中的攻击目标，目标倒下时自动落到第一个活人身上。
    /// 目标用「实例」记，不用「活人列表里的下标」：下标会在前排倒下时整体前移，
    /// 集火 #2 打死 #1 之后同一句 CurrentTarget() 就指到别人身上了——
    /// 玩家以为自己一直在打同一只，实际每死一只就换一次目标。
    /// </summary>
    public Combatant CurrentTarget()
    {
        var living = LivingEnemies();
        if (living.Count == 0) return Enemy;
        // 目标得是「还活着的那个」：不死身（不屈）救回来的同一个人不算换了目标，
        // 但已经倒下的那只不能再被当成本回合的出招者——否则敌人回合从这里取
        // actor 就会拿着尸体算一刀（预告与结算都会对着一个空位报错）。
        if (_target is not null && _target.Hp > 0) return _target;
        var idx = Math.Min(Math.Max(0, TargetIndex), living.Count - 1);
        _target = living[idx];
        return _target;
    }

    /// <summary>切换集火目标：只接受活着的敌人</summary>
    public bool SelectTarget(int index)
    {
        var living = LivingEnemies();
        if (index < 0 || index >= living.Count) return false;
        TargetIndex = index;
        _target = living[index];
        return true;
    }

    /// <summary>目标真正倒下时换人：只在「它已经不在了」这一刻重挑，挨打不会丢目标</summary>
    private void RetargetIfDead()
    {
        if (_target is not null && _target.Hp > 0) return;
        var living = LivingEnemies();
        _target = living.Count > 0 ? living[0] : null;
        TargetIndex = 0;
    }

    /// <summary>本场是不是组合战斗（多于一只敌人）</summary>
    public bool IsGroup => Allies.Count > 0;

    private SkillContext Ctx(Combatant actor, Combatant target) => new()
    {
        Actor = actor,
        Target = target,
        // 技能的攻防也要吃 EffectiveXxx：玩家被降攻后放火球，伤害该一起变少。
        ActorAtk = IsPlayerSide(actor) ? PlayerAtk() : EntityRules.EffectiveAttack(actor, _opts.AttackerAtk(actor)),
        ActorMaxHp = _opts.MaxHp(actor),
        // 灼烧层的破防对「技能」同样生效：说明写的是「敌人攻击无视你 30% 防御」，
        // 而敌人的技能（炼狱、灵魂汲取）走的也是攻击结算。
        // 以前只在 EnemyAttackResult 里减，于是这一层对会放技能的怪形同虚设——
        // 玩家按预告（EnemyForecast 已经算了破防）防了个寂寞，实际技能伤害比预告高一截。
        TargetDef = IsPlayerSide(target)
            ? Math.Max(0, (int)Math.Round(EntityRules.EffectiveDefense(target, _opts.AttackerDef(target)) * (1 - PiercedDefense())))
            : EntityRules.EffectiveDefense(target, _opts.AttackerDef(target)),
        Rng = _rng,
        Rules = _opts.Rules,
        // 只有玩家侧带词条：敌人的技能数值来自怪物模板与楼层机制。
        Affixes = IsPlayerSide(actor) ? _affixes : ActiveAffixes.Empty,
        // 词条归属方：只有玩家的治疗会被贪婪之刃削弱，敌人的暗愈照常全额
        AffixesOwner = Player,
        // 汲血蝙蝠：低血量时汲取回血翻倍
        DrainMultiplier = DrainMultiplierOf(actor),
        // 暴击加成只给玩家侧；但「暴戾」是周目给敌人的账。
        CritBonus = IsPlayerSide(actor) ? _critBonus : _enemyCritBonus,
        // 受伤方的词条只看「挨打的是不是玩家」
        TargetIsPlayer = IsPlayerSide(target),
        TargetAffixes = IsPlayerSide(target) ? _affixes : ActiveAffixes.Empty,
        // 幽暗层的治疗减半：只对玩家侧生效
        HealFactor = IsPlayerSide(actor) ? FloorMechanics.HealFactor(FloorMechanic, Player) : 1,
        // 深渊领域的冷却惩罚只落在**玩家**身上：说明写的是「范围内你的技能冷却 +1」。
        //
        // 以前的判据只问「机制里写了 Cooldown」，两边都加——于是那段注释说的
        // 「领域只惩罚了一半的施法者」被修成了「领域把 Boss 自己也一起罚」：
        // 实测 Boss 的炼狱冷却从 4 变成 5，领域里变成 6。而这一层真正决定生死的
        // 恰恰是「Boss 每几回合能放一次 1.6 倍炼狱」——多罚它一格，等于替玩家减难度，
        // 还和面板上那句「你的技能冷却 +1」对不上。
        //
        // 周期不参与判断：冷却惩罚是领域本身的性质，与「张手的那一回合」无关。
        ExtraCooldown = IsPlayerSide(actor) && Boss is { Cooldown: { } cd } && cd > 0 ? cd : 0,
    };

    /// <summary>
    /// 这个单位是不是玩家那一侧。Battle 里所有「玩家的账只算一次」的分支都走它，
    /// 而不是 `ReferenceEquals(x, Player)`：判据散在二十处，换一个「玩家代理单位」就会漏。
    /// </summary>
    private bool IsPlayerSide(Combatant c) => ReferenceEquals(c, Player) || c.IsPlayer;

    /// <summary>玩家当前的攻击力 = 面板 + 楼层补偿 + 临时修正（降攻）</summary>
    private int PlayerAtk() => EntityRules.EffectiveAttack(Player, _opts.AttackerAtk(Player) + _floorAttackBonus);

    /// <summary>该敌人当前的汲取倍率：低血蝙蝠 2 倍，其余 1 倍</summary>
    private double DrainMultiplierOf(Combatant actor)
    {
        if (!_enemyDefs.TryGetValue(actor, out var def) || def.LowHpDrain is null) return 1;
        var cfg = def.LowHpDrain;
        var maxHp = _opts.MaxHp(actor);
        return actor.Hp > 0 && (double)actor.Hp / maxHp <= cfg.Ratio ? cfg.Multiplier : 1;
    }

    /// <summary>
    /// 登记一笔反震（反射层）。真正扣血在 <see cref="PayReflect"/>：
    /// 「武器被弹回来」的前提是这一下真的砸上去了。
    /// </summary>
    public MechanicEffect? ArmReflect(int damage, string log)
    {
        if (damage <= 0) return null;
        _armedReflect = damage;
        _armedReflectLog = log;
        // 登记本身不写日志：这一刻还没打中，写出去就变成「预告里先说损失 4 点」。
        return new MechanicEffect();
    }

    /// <summary>这一下打中了：把登记的反震收掉。返回是否真的扣了血</summary>
    private bool PayReflect()
    {
        if (_armedReflect <= 0) return false;
        var amount = _armedReflect;
        var log = _armedReflectLog;
        _armedReflect = 0;
        _armedReflectLog = null;
        if (log is not null) PushLog(log);
        BattleEvents.EmitDamageNumber("player", amount, "dot");
        HurtPlayer(amount);
        return true;
    }

    /// <summary>
    /// 消耗掉一次「下一次攻击威力提升」的增益。
    ///
    /// 它以前写作 `X.AttackMultiplier = 1` —— 直接归零、什么都不说。
    /// 同一个字段既被「强化」的施法置成 1.4，也被这里顺手设成 1，
    /// 于是玩家放完「强化」在自己那一栏看到「下一击 ×1.4」，砍出去却是个原数
    /// （固定随机下 6 → 9 变成了 6 → 6），增益等于白放。
    /// 现在改成「1 + 一句日志」——玩家得看得见它被用掉了。
    /// </summary>
    private void ConsumeAttackBuff(Combatant actor)
    {
        PushLog(IsPlayerSide(actor)
            ? "强化的力量灌注进这一击"
            : $"{actor.Name}的强化随这一击散尽");
        actor.AttackMultiplier = 1;
    }

    /// <summary>普通攻击：结算后消耗掉「强化」倍率</summary>
    public void PlayerAttack()
    {
        if (!CanAct()) return;
        var target = CurrentTarget();
        if (TryFloorDodge(target)) return;
        var mech = FloorMechanics.ResolveFloorHit(FloorMechanic, target, _rng, this);
        if (mech is not null)
        {
            // 先判「这一下到底有没有打中」，再决定要不要扣玩家的血。
            //
            // 反射层的反震写在这一条链上，而这条链跑在**伤害之前**：
            // 以前无条件 ApplyMechanic，于是「你的武器被反震回来，损失 4 点生命」
            // 在下面那次 dodge / 失手之后照样扣。同一回合里既落空又挨反震，
            // 玩家看到的是「攻击落空了」+「你损失 4 点生命」——反震不该在没打中时收钱。
            var whiffed = mech.Log is not null && mech.Damage is null && mech.PoisonTurns is null;
            if (!whiffed) ApplyMechanic(mech);
            else
            {
                PushLog(mech.Log!);
                LastMechanicNote = mech.Log!;
                // 纯落空：不造成伤害，但仍然消耗一个回合
                FinishPlayerTurn();
                return;
            }
        }
        var res = CombatMath.ComputeDamage(
            Player,
            target,
            // 攻防都要过一层 EffectiveXxx：冰封印记的降攻写在玩家身上，锈蚀的破防写在敌人身上。
            PlayerAtk(),
            EntityRules.EffectiveDefense(target, _opts.AttackerDef(target)),
            _rng,
            _opts.Rules,
            _affixHooks);
        if (Player.AttackMultiplier > 1) ConsumeAttackBuff(Player);
        var dealt = CombatMath.ApplyDamage(target, res);
        // 挨了这一刀，对方的防御姿态就消费掉了：它护的就是「下一次受击」
        target.Defending = false;
        _guarding.Remove(target);
        PushLog($"你发起攻击，造成 {dealt} 点伤害{(res.Crit ? "（暴击！）" : "")}{(IsGroup ? $"（{target.Name}）" : "")}");
        BattleEvents.EmitDamageNumber("enemy", dealt, res.Crit ? "crit" : "hit");
        // 反射层：这一下打中了，反震才收。落空的那条路走不到这里。
        if (PayReflect() && Player.Hp <= 0)
        {
            Phase = BattlePhase.Lost;
            PushLog("你被自己的武器带走了……");
            return;
        }
        // 暴击附加灼烧：词条挂在玩家装备上，命中后给敌人挂持续掉血
        var burn = AffixMath.OnCrit(res.Crit, _affixes);
        if (burn?.Burn is not null)
        {
            if (target.ImmunePoison != true)
            {
                EntityRules.ApplyPoison(target, burn.Burn.Turns, burn.Burn.Power);
                if (burn.Log is not null) PushLog(burn.Log);
            }
            else if (burn.Log is not null)
            {
                PushLog("敌人免疫灼烧，「炽热暴击」没有生效");
            }
        }
        // 霜之镜像：把这一下固定反弹，数值来自数据而不是敌人攻击力
        if (Boss is { Id: "frost_mirror" } && Boss.Consume(target))
        {
            var reflect = GameDataStore.Get().BossMechanics["frost_mirror"].Reflect ?? 20;
            PushLog($"冰镜把你的伤害反弹回来，你受到 {reflect} 点伤害！");
            HurtPlayer(reflect);
        }
        FinishPlayerTurn();
    }

    /// <summary>
    /// 防御。这一手必须在「接下来挨的那一下」上真的折价，否则它只是个拖延按钮。
    ///
    /// 两条都要成立，缺一条玩家按下去就等于放弃一回合：
    ///  1) 结算侧要折 —— ComputeDamage 里读 Defending，这条一直在；
    ///  2) 预告侧要同步 —— 敌人卡上那个「预计 X~Y 点」得把折价算进去。
    /// 以前 EnemyForecast 完全不看 Defending（也没看敌人的防御姿态），
    /// 于是玩家按防御之后预告还是原数，看上去就像「防了个寂寞」。
    /// </summary>
    public void PlayerDefend()
    {
        if (!CanAct()) return;
        Defend(Player);
        var cut = (int)Math.Round(_opts.Rules.DefendReduction * 100);
        PushLog($"你举起盾牌，这一击伤害减少 {cut}%");
        FinishPlayerTurn();
    }

    /// <summary>
    /// 楼层机制给的闪避（躁动 20% / 幽暗 30%）：命中前先判，落空就吃掉一个回合。
    /// 返回 true 表示这一下已经落空、回合也结算完了。
    ///
    /// 单独立一个方法是因为它必须被「每一条造成伤害的玩家行动」调用，而不只是普攻：
    /// 技能那条路上，ResolveFloorHit 的返回值只在前半段（「没有任何伤害、也没挂毒」）
    /// 时才算落空，而闪避恰好不写 Damage / PoisonTurns —— 于是技能从这条缝里穿过去了。
    /// 结果就是：躁动层写着「敌人闪避率 20%」，玩家的火球一次都没落空过，
    /// 而这一层存在的意义正是「逼你用高倍率技能」——它反过来只惩罚了普攻。
    /// </summary>
    private bool TryFloorDodge(Combatant target)
    {
        if (target is null || target.Evade <= 0 || !_rng.Chance(target.Evade)) return false;
        PushLog($"{target.Name}身形一晃，你的攻击落空了");
        FinishPlayerTurn();
        return true;
    }

    /// <summary>
    /// 进入防御姿态。刻意不清 AttackMultiplier：「强化」的说明是「下一次攻击威力提升」，
    /// 防御并没有发起攻击，不该把它吃掉。
    /// </summary>
    private void Defend(Combatant c)
    {
        c.Defending = true;
        // 敌人后手：它摆出的姿态要活到玩家的下一刀才有意义，记下来，回合末就不清它。
        if (!ReferenceEquals(c, Player)) _guarding.Add(c);
    }

    /// <summary>使用技能，成功返回 true</summary>
    public bool PlayerSkill(string skillId)
    {
        if (!CanAct()) return false;
        var skill = Player.Skills.FirstOrDefault(s => s.Id == skillId);
        if (skill is null || !Skills.IsSkillReady(Player, skillId)) return false;
        // 混乱思绪：法术被吞掉，冷却照扣、回合照结束。判在最前面，失败时连目标都不该被摸到。
        // 只有「要念的」那几类才会散（Skills.CanFizzle）：摆架势不是施法。
        if (Skills.CanFizzle(skill) && AffixMath.SpellFizzled(_affixes, _rng))
        {
            Player.Cooldowns[skillId] = skill.Cooldown + 1;
            PushLog($"「{skill.Name}」念到一半散了——混乱思绪吞掉了它，冷却照扣");
            BattleEvents.EmitDamageNumber("player", 0, "dot");
            FinishPlayerTurn();
            return true;
        }
        // 技能也要过闪避判定：否则「躁动层敌人闪避率 20%」只对普攻生效。
        // 增益/减益/控制类技能不参与命中判定，它们本来就不该被闪掉。
        var dodgeable = skill.Type is SkillType.Damage or SkillType.Poison or SkillType.Drain;
        if (dodgeable)
        {
            // 判定对象是玩家真正打的那只，不是主敌人。
            var tgt = CurrentTarget();
            // 闪避单独判一次：它不写 Damage / PoisonTurns，从下面的「纯落空」判据里漏得过去。
            if (TryFloorDodge(tgt)) return true;
            var mech = FloorMechanics.ResolveFloorHit(FloorMechanic, tgt, _rng, this);
            if (mech?.Log is not null && mech.Damage is null && mech.PoisonTurns is null)
            {
                PushLog(mech.Log);
                FinishPlayerTurn();
                return true;
            }
        }
        BattleEvents.EmitSkillFx(skill.Id, "player", "enemy");
        var target = CurrentTarget();
        // 注意顺序：先结算、后消费。姿态减半写在 ComputeDamage 里读 Defending。
        var result = Skills.UseSkill(skill, Ctx(Player, target));
        target.Defending = false;
        _guarding.Remove(target);
        PushLog(result.Log);
        // 反射层：技能这一下也算「打中」（描述写的是「你攻击时」，不只是普攻）。
        if (PayReflect() && Player.Hp <= 0)
        {
            Phase = BattlePhase.Lost;
            PushLog("你被自己的武器带走了……");
            return true;
        }
        // 诅咒雕像：玩家每次施法被反伤。反伤按「施法次数」而不是伤害值算。
        var reflect = SpellReflectDamage();
        if (reflect > 0 && Player.Hp > 0)
        {
            PushLog($"诅咒雕像的反噬咬中你，失去 {reflect} 点生命");
            HurtPlayer(reflect);
            if (Player.Hp <= 0)
            {
                Phase = BattlePhase.Lost;
                PushLog("你被雕像的诅咒压垮了……");
                return true;
            }
        }
        // 治疗飘绿字、伤害飘红/橙字
        if (result.Healed > 0) BattleEvents.EmitDamageNumber("player", -result.Healed, "heal");
        if (result.Damage > 0) BattleEvents.EmitDamageNumber("enemy", result.Damage, "hit");
        // 深渊领域的「技能冷却 +1」：这一笔已经在 Skills.UseSkill 里按 ctx.ExtraCooldown 记过，
        // 这里只把它说出来（不说的话玩家只会觉得「技能怎么老是不好」）。
        // 判据是 Cooldown 写在机制里，而不是 Id 写死——再加一条冷却惩罚也该自动生效。
        if (Boss is { Cooldown: { } extraCd } && extraCd > 0)
        {
            PushLog($"深渊领域压住了技能的余韵，「{skill.Name}」的冷却延长了 {extraCd} 回合");
        }
        FinishPlayerTurn();
        return true;
    }

    /// <summary>消耗品造成的固定伤害。描述写「无视防御」，防御力与防御姿态都不该削减它</summary>
    public void PlayerItemDamage(int amount, string name)
    {
        if (!CanAct()) return;
        // 刻意不清 AttackMultiplier：炸弹是「投出去的东西」，不是你的攻击。
        // 「强化」写着「下一次**攻击**威力提升」，投掷不是攻击——这个增益得留到
        // 下一次真的挥刀 / 施法。归零写在「用掉它的那一句」里（ConsumeAttackBuff），
        // 所以这里唯一的责任就是**不要**顺手把它清掉。
        var target = CurrentTarget();
        // 「无视防御」不等于「必中」：闪避与防御是两回事，躁动/幽暗层的闪避对它一样有效。
        if (TryFloorDodge(target)) return;
        var dealt = CombatMath.ApplyDamage(target, CombatMath.FixedDamage(amount));
        // 但这一下依然算「挨了一下」：姿态该被消耗。
        target.Defending = false;
        _guarding.Remove(target);
        PushLog($"你投出{name}，造成 {dealt} 点伤害{(IsGroup ? $"（{target.Name}）" : "")}");
        BattleEvents.EmitDamageNumber("enemy", dealt, "hit");
        FinishPlayerTurn();
    }

    public void PlayerHeal(int amount, string name)
    {
        if (!CanAct()) return;
        var max = _opts.MaxHp(Player);
        // 两个治疗折扣叠乘：幽暗层是「环境」，贪婪之刃是「诅咒」。分开报原因。
        var envFactor = FloorMechanics.HealFactor(FloorMechanic, Player);
        var curseFactor = AffixMath.HealTakenScale(_affixes);
        var real = Math.Max(1, (int)Math.Round(amount * envFactor * curseFactor));
        var before = Player.Hp;
        Player.Hp = Math.Min(max, Player.Hp + real);
        var healed = Player.Hp - before;
        var why = envFactor < 1 ? "（幽暗中治疗效果减半）" : curseFactor < 1 ? "（贪婪之刃啃掉了大半）" : "";
        PushLog($"你使用{name}，回复 {healed} 点生命{why}");
        if (healed > 0) BattleEvents.EmitDamageNumber("player", -healed, "heal");
        FinishPlayerTurn();
    }

    /// <summary>把机制的结算结果写进日志与血量：闪避/反震/自爆/冰镜都走这里</summary>
    private void ApplyMechanic(MechanicEffect effect)
    {
        if (effect.Log is not null)
        {
            PushLog(effect.Log);
            LastMechanicNote = effect.Log;
        }
        if (effect.Damage is { } dmg) HurtPlayer(dmg);
        // 挂毒只看「有没有回合数与伤害」，而且**伤害必须补一个兜底**。
        //
        // 这里是瘴气层唯一的下毒入口，而 FloorMechanics 交回来的 PoisonPower 是
        // 空的（power 由调用方按「这一刀挨了多少」填）。以前写成
        // `effect.PoisonPower is { } power`——它在空值上不成立，于是整条链静默跳过：
        // 说明写着「敌人的攻击有 20% 概率使你中毒 2 回合」，日志也照样打
        // 「瘴气顺着伤口钻进你的身体，你中毒了！」，而玩家身上的 PoisonTurns 一次都没变过。
        // 毒伤是这份说明的**主体**，缺了它这句话等于没实现。
        if (effect.PoisonTurns is { } turns && turns > 0 && !FloorMechanics.PoisonImmune(Player))
        {
            EntityRules.ApplyPoison(Player, turns, Math.Max(1, effect.PoisonPower ?? 0));
        }
        if (effect.Stun == true) Enemy.StunTurns = Math.Max(Enemy.StunTurns, 1);
    }

    /// <summary>`HurtPlayer` 的来源标记：玩家自己打出来的那一下（炸弹炸死自己）。</summary>
    public const string ReasonFromPlayer = "@player";

    /// <summary>
    /// 玩家受到的非攻击伤害：反震、自爆、冰镜反弹、诅咒雕像反噬、毒伤、词条反噬。
    /// 全部走这一处，为的是两件事：脆弱躯壳对它们也生效；「不屈」在每一条致命路径上都有效。
    /// `reason` 不为 null 时才会写进日志，所以它同时也是「这一下从哪来」的唯一标记。
    /// </summary>
    private void HurtPlayer(int amount, string? reason = null)
    {
        var scaled = Math.Max(1, (int)Math.Round(amount * AffixMath.IncomingScale(_affixes)));
        // 反震 / 自爆 / 毒伤也算「挨了一下」：都走这条链，就都该消费掉首次加成。
        if (_affixes.RiskIncomingPct > 0) MarkRiskHit();
        var before = Player.Hp;
        // 来源要标清楚：`FixedDamage` 默认是「环境打回来的」，而玩家亲手投出的那一刀
        // （炸弹、自伤技能）不该被「中毒每层最多毒死几次」那条预算兜住——
        // 预算是给毒留的第二次机会，不是替玩家自己的炸弹挡刀。
        var fromPlayer = reason == ReasonFromPlayer;
        var result = CombatMath.FixedDamage(scaled);
        result.FromPlayer = fromPlayer;
        var real = CombatMath.ApplyDamage(Player, result, fromPlayer);
        BattleEvents.EmitDamageNumber("player", real, "dot");
        PlayerHpLossSinceSync += before - Player.Hp;
        // 预算把这一下兜住了：日志里要说出来。预告说「合计多少」，结算就得是那个数——
        // 对不上的时候必须有一句话解释，而不是让玩家自己发现血条没掉那么多。
        if (real < scaled) PushLog($"（这一层的毒素已经用完了额度，这一下只扣到剩 1 点）");
        if (scaled != amount && reason is not null) PushLog($"（脆弱躯壳让这一下变成了 {scaled} 点）");
        if (Player.Hp <= 0) CheckSurvive();
    }

    /// <summary>
    /// 敌人倒下时的所有自爆：功能性敌人的 ExplodeOnDeath 加楼层机制的怨恨层自爆。
    /// 每只只炸一次：不记账的话，一间三只怪的怨恨房会把同一具尸体每回合都炸一遍。
    /// 返回 true 表示玩家被自爆带走。
    /// </summary>
    private bool ExplodeDeadEnemies()
    {
        var killed = false;
        foreach (var dead in Enemies())
        {
            if (dead.Hp > 0) continue;
            if (_exploded.Contains(dead)) continue;
            var ratio = _enemyDefs.TryGetValue(dead, out var def) ? def.ExplodeOnDeath ?? 0 : 0;
            if (ratio > 0)
            {
                var damage = Math.Max(1, (int)Math.Round(_opts.AttackerAtk(dead) * ratio));
                PushLog($"{dead.Name}倒下的瞬间炸开一团狂热，对你造成 {damage} 点伤害！");
                HurtPlayer(damage);
                if (Player.Hp <= 0) killed = true;
            }
            // 怨恨层：倒下时按自己攻击力的比例炸。放在这里而不是回合末，
            // 否则玩家先看到「胜利」，再莫名其妙掉一截血。
            var boom = FloorMechanics.SettleFloorMechanic(FloorMechanic, dead, Player, _rng);
            if (boom is not null)
            {
                ApplyMechanic(boom);
                if (Player.Hp <= 0) killed = true;
            }
            _exploded.Add(dead);
        }
        return killed;
    }

    private void FinishPlayerTurn()
    {
        // 这一回合没打成（落空 / 被闪避 / 属性无效）时，登记的反震一起作废：
        // 留着它就会在下一刀才收，而下一刀可能根本没被弹回来。
        _armedReflect = 0;
        _armedReflectLog = null;
        // 目标倒下了才换人：同一回合里挨打、被毒、被反震都不该让集火跑掉。
        RetargetIfDead();
        // 组合战斗：只有全灭才算赢。死一个只是「少了一个集火选项」。
        if (LivingEnemies().Count == 0)
        {
            Phase = BattlePhase.Won;
            PushLog("敌人被全部击败了！");
            if (ExplodeDeadEnemies() || Player.Hp <= 0)
            {
                Phase = BattlePhase.Lost;
                PushLog("你被倒下的敌人带走了……");
                return;
            }
            var killHeal = AffixMath.OnKill(Player, _opts.MaxHp(Player), _affixes);
            if (killHeal?.Log is not null)
            {
                PushLog(killHeal.Log);
                BattleEvents.EmitDamageNumber("player", -1, "heal");
            }
            return;
        }
        EnemyTurn();
    }

    /// <summary>
    /// 战斗之外的裁判宣布这一场输了。
    ///
    /// 什么时候需要它：Battle 只在 EnemyTurn 里判负，而「敌人被震住，这一回合作废」
    /// 那条路直接从 EnemyAct 返回、再走 EndRound——玩家可能已经在那之前被毒死或
    /// 被反震打死，Phase 却还是 Player。没有这条入口时，模拟器只能在尸体上继续出招：
    /// `PlayerAttack` 照常扣敌人的血，日志最后写「敌人被全部击败了」。
    /// 生产路径（BattleScene）由 UI 判玩家血量，这里补的是无头那一侧。
    /// </summary>
    public void ForceLost(string log)
    {
        if (Phase is BattlePhase.Won or BattlePhase.Lost) return;
        Phase = BattlePhase.Lost;
        PushLog(log);
    }

    /// <summary>
    /// 战斗彻底结束时收尾：清掉只属于这一场的临时状态。
    /// 必须在三种出路上都调——胜利、失败、以及「逃跑」。
    /// 不清 RiskHitTaken / UsedSurviveLethal：那两个由构造函数负责归零。
    /// </summary>
    public void Dispose()
    {
        Skills.ResetCombatState(Player);
        foreach (var c in Enemies()) Skills.ResetCombatState(c);
        // 中毒是跨战斗的：战斗结束时毒不该跟着玩家走到下一场去。
        // 以前毒只挂在「每回合掉一次」上，没人管它在这一场结束之后还挂着，
        // 玩家靠「一场战斗差不多刚好烧完」蒙对了一次——连胜的那几场就会连着中毒。
        EntityRules.ClearPoison(Player);
        foreach (var c in Enemies()) EntityRules.ClearPoison(c);
    }

    /// <summary>
    /// 玩家现在能不能出手。唯一的判据，而且是防御性的：
    /// 以前每个入口各写一句 `Phase != BattlePhase.Player`，但「玩家已经倒下」这件事
    /// 只在 EnemyTurn / 毒伤 / 反震那几条路上写进 Phase——掉血走到别的路上（机制、
    /// 词条、模拟器的外部治疗）时就漏了。于是一具尸体可以继续 `PlayerAttack`：
    /// 血条是 0，敌人还在掉血，最后日志写「敌人被全部击败了」。
    /// 现在血量本身也是判据：0 血的人不出招，这一场就地判负。
    /// </summary>
    private bool CanAct()
    {
        if (Phase != BattlePhase.Player) return false;
        if (Player.Hp > 0) return true;
        ForceLost("你倒下了……");
        return false;
    }

    /// <summary>
    /// 撤离战斗。血不够代价就死在「逃跑」这个本该保命的操作上——
    /// 它照样算一次死亡，玩家不该带着 0 血回到地图继续走。
    /// 返回玩家是否因此倒下。
    /// </summary>
    public bool Escape(int cost)
    {
        if (!CanAct()) return false;
        Phase = BattlePhase.Escaped;
        Player.Hp -= Math.Max(0, cost);
        if (Player.Hp <= 0)
        {
            Player.Hp = 0;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 玩家在战斗中使用消耗品（解毒草）：解掉自己的中毒，占一个回合。
    ///
    /// 「没中毒也吃掉」是一条**真的会输**的路：这一回合不产生任何伤害，
    /// 只换来一个本来就不存在的毒。所以日志必须把这件事说出来——
    /// 以前这一支也写「毒被涤荡一空」，玩家手里什么都没发生，读到的却是解掉了什么。
    /// </summary>
    public void PlayerCleanse()
    {
        if (!CanAct()) return;
        var wasPoisoned = Player.PoisonTurns > 0 || Player.PoisonPower > 0;
        // 解毒草的场要**在吃之前**记：吃完之后毒就没了，再问「这一株救回了什么」永远是 0。
        // 两个数分开记——「这一回合毒会不会要命」是规则（含本层的毒死预算），
        // 「掐掉毒之后站不站得住」是它值不值那一回合。报表的分子读前者。
        if (wasPoisoned)
        {
            CleanseOffered++;
            if (PoisonWouldKillPlayer()) CleanseOfferedLethal++;
            if (PoisonWouldCostHpThisTurn()) CleanseOfferedSaved++;
        }
        EntityRules.ClearPoison(Player);
        PushLog(wasPoisoned
            ? "你吃下解毒草，体内的毒被涤荡一空"
            : "你吃下解毒草——身上并没有毒，这一回合白过了");
        FinishPlayerTurn();
    }

    /// <summary>
    /// 下一个出手的敌人。和 EnemyTurn 用的是同一个游标，所以预告与结算指向同一只。
    /// 唯一的分歧是「眩晕跳过」：那只被震住的敌人这一回合什么也不做，
    /// 这时候告知「它不动」比硬算一个数字更准确。
    /// </summary>
    private Combatant UpcomingAttackerCore()
    {
        var living = LivingEnemies();
        if (living.Count == 0) return CurrentTarget();
        return living[_groupCursor % living.Count];
    }

    /// <summary>下一个出手的敌人这回合会不会真的动手（被震住 / 被冻住的那一只不会）</summary>
    public bool UpcomingEnemyActs => !EntityRules.CannotAct(UpcomingAttackerCore());

    /// <summary>自检用：下一个出手的是哪一只</summary>
    public Combatant UpcomingAttacker() => UpcomingAttackerCore();

    /// <summary>下一个出手的敌人的名字（组合战斗的预告要写清「这是谁的一刀」）</summary>
    public string? UpcomingAttackerName => UpcomingAttackerCore().Name;

    /// <summary>
    /// 组合的敌人回合：活着的敌人依次出手，共享一个行动条。
    /// 不是「每只都打一下」——那样一间两只怪的房等于两倍伤害频率。
    /// </summary>
    private void EnemyTurn()
    {
        var hpBefore = Player.Hp;
        var living = LivingEnemies();
        if (living.Count > 0)
        {
            var actor = living[_groupCursor % living.Count];
            ActiveEnemyIndex = Math.Max(0, Enemies().IndexOf(actor));
            EnemyAct(actor);
            _groupCursor = (_groupCursor + 1) % living.Count;
        }
        // 覆盖而不是相加会让反射层的 4 点反震凭空消失。快照点卡在 EndRound 之前。
        PlayerHpLossSinceSync += Math.Max(0, hpBefore - Player.Hp);
        if (Player.Hp <= 0)
        {
            Phase = BattlePhase.Lost;
            PushLog("你倒下了……");
            return;
        }
        EndRound();
    }

    /// <summary>单个敌人的一次行动：AI 掷骰，按结果攻击 / 防御 / 技能</summary>
    private void EnemyAct(Combatant actor)
    {
        // 被震住的敌人跳过整回合：眩晕必须真的「吃掉」一回合。
        // 判据走 EntityRules.CannotAct 而不是直接读 StunTurns——「这一回合不能动」
        // 以后可能还有第二种来源（冰冻之类），分成几处判迟早漏一边。
        if (EntityRules.CannotAct(actor))
        {
            PushLog($"{actor.Name}被震得无法行动");
            LastEnemyAction = "defend";
            // 眩晕按「自己的出手机会」扣：这一次作废，下一次就轮到了。
            EntityRules.ConsumeStun(actor);
            return;
        }
        var ai = GameDataStore.Get().Config.EnemyAi;
        var usable = actor.Skills.Where(s => Skills.IsSkillReady(actor, s.Id)).ToList();
        // 三选一：掷一次 [0,1)，按「技能 → 攻击 → 防御」的顺序判。
        //
        // 这里以前写成 `roll >= ai.Attack + ai.Defend`，而 roll 是 [0,1) 的浮点数：
        // 「技能」这一档因此要求 roll ≥ 0.75，只有 25% 的概率能进——
        // 这是对的。但下面还剩两档：`roll < ai.Attack` 是攻击（45%），
        // 剩下的 30% 全落进防御——也是对的。真正错的是「技能抽到了却没技能可用」
        // 那一条：它当时直接掉进 `else`（防御），于是不会放技能的怪
        // 会把它那 25% 全变成防御，实测攻击 44% / 防御 44% / 技能 12%。
        // 现在「技能没准备好」退还给普攻。
        var roll = _rng.Next();
        var wantsSkill = roll >= 1 - ai.Skill;
        var wantsAttack = roll < ai.Attack;

        if (wantsSkill && usable.Count > 0)
        {
            var skill = _rng.Pick(usable);
            // 玩家的闪避也该挡得住敌人的技能。只挡造成伤害的那几类。
            var enemySkillDodgeable = skill.Type is SkillType.Damage or SkillType.Poison or SkillType.Drain or SkillType.Stun or SkillType.Dispel;
            if (enemySkillDodgeable && Player.Evade > 0 && _rng.Chance(Player.Evade))
            {
                PushLog($"你的身影散作黑雾，{actor.Name}的「{skill.Name}」落空了");
                LastEnemyAction = "skill";
                return;
            }
            BattleEvents.EmitSkillFx(skill.Id, "enemy", "player");
            var result = Skills.UseSkill(skill, Ctx(actor, Player));
            // 技能也是「打中了你」的一种：狂意的首次加成对技能同样该收（而且只收一次）。
            if (result.Damage > 0 && _affixes.RiskIncomingPct > 0) MarkRiskHit();
            PushLog(result.Log);
            LastEnemyAction = "skill";
            if (result.Damage > 0 || result.Healed > 0)
            {
                BattleEvents.EmitDamageNumber("player", result.Damage, "hit");
                if (result.Healed > 0) BattleEvents.EmitDamageNumber("enemy", -result.Healed, "heal");
            }
            // 敌人的技能伤害是 UseSkill 内部直接扣的（不走 HurtPlayer），
            // 所以致命伤要在这里单独判一次「不屈」。
            if (result.Damage > 0 && Player.Hp <= 0) CheckSurvive();
        }
        // 技能这一档抽到了但没技能可用时，退成普攻：不退还的话，
        // 「不会放技能的怪」会比配置里写的更爱防御。
        else if (wantsAttack || wantsSkill)
        {
            // 玩家闪避（相位穿行、Boss 机制）：命中前先判，落空则不结算伤害
            if (Player.Evade > 0 && _rng.Chance(Player.Evade))
            {
                PushLog($"你的身影散作黑雾，{actor.Name}的攻击落空了");
                LastEnemyAction = "attack";
                return;
            }
            var res = EnemyAttackResult(actor);
            if (actor.AttackMultiplier > 1) ConsumeAttackBuff(actor);
            var dealt = CombatMath.ApplyDamage(Player, res);
            // 「不屈」要在写日志之前判：触发时日志说的是「以 1 点生命站了起来」。
            var saved = CheckSurvive();
            PushLog($"{actor.Name}攻击你，造成 {dealt} 点伤害{(res.Crit ? "（暴击！）" : "")}{(saved ? "——但不屈替你扛下了" : "")}");
            LastEnemyAction = "attack";
            BattleEvents.EmitDamageNumber("player", dealt, Boss?.IsArmed(actor) == true ? "boss" : "hit");
            // 瘴气层：说明写「敌人的攻击有 20% 概率使你中毒」，方向是敌人 → 玩家。
            // 毒伤按这一刀的实际伤害算（FloorMechanics.HazePoisonRatio）：
            // 以前它落在玩家的面板攻击力上，于是玩家越强、毒越毒。
            if (dealt > 0)
            {
                var haze = FloorMechanics.ResolveEnemyHit(FloorMechanic, actor, _rng);
                if (haze is not null)
                {
                    // 两条前提一起成立的算式只留一个入口：FloorMechanics.HazePoisonPower。
                    // 它按「这一刀实际打出多少」折算（毒是环境的账，不是玩家攻击力的账），
                    // 再保一个下限（伤得再轻毒也有分量），并封在 EntityRules.MaxPoisonPower 上界
                    // ——上界是这一支的来意：这一刀随层数长，不封的话第 14 层的瘴气一挂就是 49 点/回合。
                    ApplyMechanic(haze with { PoisonPower = FloorMechanics.HazePoisonPower(dealt, _opts.MaxHp(Player)) });
                }
            }
            // 霜缚者：攻击附带冰封印记（复用霜弓手的降攻 25% / 3 回合）
            if (Enemies().Contains(actor))
            {
                var seal = EnemyFrostSeal(actor);
                if (seal is not null)
                {
                    Player.AtkMod = -Math.Max(1, (int)Math.Round(_opts.AttackerAtk(Player) * 0.25));
                    Player.AtkModTurns = 3;
                    PushLog(seal);
                }
            }
        }
        else
        {
            Defend(actor);
            PushLog($"{actor.Name}摆出防御姿态");
            LastEnemyAction = "defend";
        }
    }

    /// <summary>
    /// 加护祭司：周期性给同房间队友加防御。
    /// 每回合只结算一次，且只给「还活着、不是自己」的队友加。
    /// </summary>
    private void TickGuardAura()
    {
        foreach (var priest in LivingEnemies())
        {
            if (!_enemyDefs.TryGetValue(priest, out var def) || def.GuardAura is null) continue;
            var aura = def.GuardAura;
            if (Round % aura.Every != 0) continue;
            // 加在 DefMod 上而不是 BaseDefense：加护是一层会被时间收走的东西。
            // 以前直接加 BaseDefense，而那是实例字段——祭司死了、战斗结束回到地图，
            // 那几点防御还挂在敌人身上，谁也退不回去。
            var granted = 0;
            foreach (var mate in LivingEnemies())
            {
                if (ReferenceEquals(mate, priest)) continue;
                // 每只队友只吃「一层」加护，值由祭司刷新，不按回合累加。
                //
                // 以前是 `mate.DefMod += aura.Defense`，每 every 回合叠 3 点、无上限。
                // 两条链一起把它变成了死结：
                //   1) 回响层（8 层）敌人每回合回 8% 最大生命，回血量按 BaseMaxHp 算；
                //   2) 加护把队友防御叠到几百点，玩家每刀只能打出 1 点（最低伤害）。
                // 于是「玩家打不动、敌人回得满」——战斗永远不会结束。
                // 实测 40 局里有整局卡在第 8 层的战斗里（第 301 回合 break 掉那一层），
                // 而玩家连一步都没走错。加护是「一层护盾」，不是「越祈祷越硬」。
                mate.DefMod = Math.Max(mate.DefMod, aura.Defense);
                mate.DefModTurns = Math.Max(mate.DefModTurns, aura.Every + 1);
                granted += 1;
            }
            if (granted == 0) continue;
            PushLog($"{priest.Name}低声祈祷，同伴的防御提升了 {aura.Defense} 点");
            LastMechanicNote = $"{priest.Name}为同伴施加了加护";
        }
    }

    /// <summary>场上活着的敌人里，诅咒雕像的施法反伤总和（多座雕像各算各的）</summary>
    private int SpellReflectDamage()
    {
        var total = 0;
        foreach (var c in LivingEnemies())
        {
            if (_enemyDefs.TryGetValue(c, out var def) && def.SpellReflect is { } v && v > 0) total += v;
        }
        return total;
    }

    /// <summary>敌人是否带冰封印记（数据字段 FrostSealOnHit），命中时给玩家降攻</summary>
    private string? EnemyFrostSeal(Combatant actor)
    {
        if (!_enemyDefs.TryGetValue(actor, out var def) || def.FrostSealOnHit != true) return null;
        return $"{actor.Name}的攻击覆上一层寒霜，你的攻击下降了 25%，持续 3 回合";
    }

    /// <summary>
    /// 敌人攻击的伤害结算。攻防都要过一层 EffectiveXxx：
    /// 冰封印记的降攻、锈蚀的破防都在这里生效。改动这里时要同步 EnemyForecast。
    /// </summary>
    private DamageResult EnemyAttackResult(Combatant actor)
    {
        var atk = EntityRules.EffectiveAttack(actor, _opts.AttackerAtk(actor));
        var def = EntityRules.EffectiveDefense(Player, _opts.AttackerDef(Player));
        // 灼烧层：敌人攻击无视玩家一部分防御。按本场机制 id 判而不是反查楼层表。
        var pierce = PiercedDefense();
        // Boss 机制的攻击倍率：周期爆发（熔岩爆裂 / 冰镜）看「本回合标记」，
        // 常驻规则（深渊领域）一直生效。
        var multiplier = BossMultiplier(actor);
        // 受伤侧乘区乘在伤害值上，只有这一处算：EnemyForecast 复述的是同一个数。
        var taken = IncomingScale();
        // 算过就算「挨过」：下一次调用 IncomingScale 不该再给一次首次加成。
        if (_affixes.RiskIncomingPct > 0) MarkRiskHit();
        // 敌人攻击不带玩家的暴击词条：鹰眼是「我的构筑」。受伤侧词条仍要走。
        return CombatMath.ComputeDamage(
            actor,
            Player,
            atk * multiplier * taken,
            Math.Max(0, (int)Math.Round(def * (1 - pierce))),
            _rng,
            _opts.Rules,
            IncomingHooks());
    }

    /// <summary>
    /// 敌人打玩家时的钩子：只带「受伤侧」词条，不带玩家的 CritBonus。
    /// 反过来，「暴戾」是敌人自己的加成，走 EnemyCritBonus——必须在没有玩家词条时也能生效。
    /// </summary>
    private AffixHooks? IncomingHooks()
    {
        var affixes = _affixHooks?.Affixes ?? _affixes;
        var need = affixes.List.Count > 0 || _enemyCritBonus > 0;
        if (!need) return null;
        return new AffixHooks { Affixes = affixes, TargetIsPlayer = true, EnemyCritBonus = _enemyCritBonus };
    }

    /// <summary>
    /// 敌人下一刀的伤害预告：区间 + 修正来源。
    /// 把 EnemyAttackResult 的整条修正链复述一遍（不含随机），
    /// 让界面报出来的区间就是结算时会走到的区间。
    /// </summary>
    public (int Low, int High, List<string> Notes) EnemyForecast()
    {
        // 预告算的是「下一手会打你的那一只」，不是「你正集火的那一只」。
        //
        // 这两只在组合战斗里几乎总是不同的：出手顺序跟着 _groupCursor 轮转，
        // 而集火是玩家自己选的。以前预告读 CurrentTarget()，于是「你打乙、
        // 甲先动手」时，卡片上写的是乙的伤害——实测 3356 次普攻里 156 次对不上，
        // 而且错得很像真的（数字都在合理区间里，玩家只会觉得「这一刀怎么轻了」）。
        var actor = UpcomingAttackerCore();
        var notes = new List<string>();
        var rawAtk = _opts.AttackerAtk(actor);
        var atk = EntityRules.EffectiveAttack(actor, rawAtk);
        if (atk != rawAtk) notes.Add($"敌人攻击{(atk - rawAtk > 0 ? "+" : "")}{atk - rawAtk}");
        var buffed = actor.AttackMultiplier > 1 ? atk * actor.AttackMultiplier : (double)atk;
        if (actor.AttackMultiplier > 1) notes.Add($"强化 ×{actor.AttackMultiplier}");
        var multiplier = BossMultiplier(actor);
        if (multiplier > 1) notes.Add($"{Boss!.Name} ×{multiplier}");
        var pierce = PiercedDefense();
        if (pierce > 0) notes.Add($"无视你 {(int)Math.Round(pierce * 100)}% 防御");
        var rawDef = _opts.AttackerDef(Player);
        var def = EntityRules.EffectiveDefense(Player, rawDef);
        if (def != rawDef) notes.Add($"你的防御{(def - rawDef > 0 ? "+" : "")}{def - rawDef}");
        if (_affixes.DamageTakenPct > 0) notes.Add($"受到的伤害 +{(int)Math.Round(_affixes.DamageTakenPct * 100)}%");
        if (_affixes.RiskIncomingPct > 0 && Player.RiskHitTaken != true)
        {
            notes.Add($"首次受伤 +{(int)Math.Round(_affixes.RiskIncomingPct * 100)}%");
        }
        var taken = IncomingScale();
        var variance = _opts.Rules.DamageVariance;
        var cut = Math.Max(0, (int)Math.Round(def * (1 - pierce)));
        var lo = Math.Max(1, (int)Math.Round((buffed * multiplier * (1 - variance) - cut) * taken));
        var hi = Math.Max(1, (int)Math.Round((buffed * multiplier * (1 + variance) - cut) * taken));
        // 技能那一手：这一只下一回合可能放的是技能，而技能的伤害是**另一条算式**
        // （`Skills` 里按倍率算，不走 ComputeDamage 的浮动与防御折价）。
        // 以前预告只算「普攻那一刀」，于是 Boss 亮着「炼狱」的冷却已经读完，
        // 卡片上还是按普攻报的数——玩家按那个数算血，然后被一刀 1.6 倍带走。
        // 报的是「两种可能里更高的那一档」：预告的用途是让人决定要不要防御 / 喝药，
        // 少报一次就是多死一次；多报的那一次，玩家按下防御之后自己会看见数变了。
        if (UpcomingEnemyActs && SkillLikely(actor) is { } skill)
        {
            var (slo, shi) = SkillDamageRange(actor, skill);
            if (shi > hi) hi = shi;
            if (slo > lo) lo = slo;
            notes.Add($"可能放「{skill.Name}」");
        }
        // 防御姿态的折价同样要播报：结算侧读 Defending，预告侧不读的话，
        // 玩家按下防御之后看到的还是一个没变过的数字——这正是「防御没用」的来源。
        if (Player.Defending && _opts.Rules.DefendReduction > 0)
        {
            var keep = 1 - _opts.Rules.DefendReduction;
            lo = Math.Max(1, (int)Math.Round(lo * keep));
            hi = Math.Max(1, (int)Math.Round(hi * keep));
            notes.Add($"你已防御 −{(int)Math.Round(_opts.Rules.DefendReduction * 100)}%");
        }
        return (lo, hi, notes);
    }

    /// <summary>
    /// 这一只下一回合**最可能**放出来、而且会直接伤人的那个技能。
    ///
    /// 判据只有两条：冷却读完了、而且是造成伤害的那几类。它**不掷骰**——
    /// 掷骰是结算那一侧的事，预告只说「它手上有这个，可能会用」。
    /// 取倍率最高的那一个：预告要给的是上界。
    /// </summary>
    private SkillDef? SkillLikely(Combatant actor)
    {
        SkillDef? best = null;
        foreach (var skill in actor.Skills)
        {
            if (skill.Type is not (SkillType.Damage or SkillType.Drain or SkillType.Poison)) continue;
            if (!Skills.IsSkillReady(actor, skill.Id)) continue;
            if (best is null || skill.Power > best.Power) best = skill;
        }
        return best;
    }

    /// <summary>
    /// 技能这一下的伤害区间。和 `Skills.UseSkill` 那一档读同一份数据：
    /// 伤害 = 攻击力 × 倍率（再乘强化与 Boss 机制倍率、乘受伤侧乘区），不走防御折价。
    ///
    /// 这里刻意不用 `ComputeDamage`：带上它会把防御也算进去，而技能那一档
    /// 本来就不减防御——两边算出来的数会不一样，然后「预告」这两个字又变成假话。
    /// 变异系数按普攻那条路的一半算：技能是「必中的一手」，报满一个 ±20% 的区间
    /// 只会让玩家觉得预告没用。这一点偏保守（宁可多报），不偏乐观。
    /// </summary>
    private (int Low, int High) SkillDamageRange(Combatant actor, SkillDef skill)
    {
        var atk = EntityRules.EffectiveAttack(actor, _opts.AttackerAtk(actor));
        var buffed = actor.AttackMultiplier > 1 ? atk * actor.AttackMultiplier : (double)atk;
        var raw = buffed * skill.Power * BossMultiplier(actor) * IncomingScale();
        var variance = _opts.Rules.DamageVariance / 2;
        var lo = Math.Max(1, (int)Math.Round(raw * (1 - variance)));
        var hi = Math.Max(1, (int)Math.Round(raw * (1 + variance)));
        return (lo, hi);
    }

    /// <summary>
    /// 不吃这株解毒草，这一回合的毒**真的会掉多少血**（含本层「还剩几次毒死额度」那条规则）。
    ///
    /// 为什么不能直接用 <see cref="LethalThisTurnExtra"/>：那个数是「毒伤」，而这个数是
    /// 「账上会走掉多少」。额度用完时毒只扣到剩 1 点——预报与判据都得读同一个数，
    /// 否则「预报说 6 点、实际掉 1 点」这种矛盾会在玩家那一屏上摆一整轮。
    /// </summary>
    public int PoisonHpThisTurn()
    {
        var power = LethalThisTurnExtra();
        if (power <= 0 || Player.Hp <= 0) return 0;
        return EntityRules.PoisonLethalAllowed(Player, power) ? Math.Min(power, Player.Hp) : Math.Max(0, Player.Hp - 1);
    }

    /// <summary>这一回合的毒会不会真的让玩家掉血（吃了草就等于白掉那一截）</summary>
    public bool PoisonWouldCostHpThisTurn() => PoisonHpThisTurn() > 0;

    /// <summary>
    /// 这一株解毒草在**这一回合**救回了什么：0 = 白吃，否则是「不吃就会掉的血」。
    /// 只给报表与界面读，规则不依赖它。
    /// </summary>
    public int CleanseSavesHp => PoisonHpThisTurn();

    /// <summary>本场战斗里「手上带着毒、草在手边」的回合数（解毒草的场，分母）</summary>
    public int CleanseOffered { get; private set; }

    /// <summary>其中「这一回合的毒会把人带走」的回合数（分子）</summary>
    public int CleanseOfferedLethal { get; private set; }

    /// <summary>其中「吃了草就等于不掉这一截血」的回合数（分子，宽口径）</summary>
    public int CleanseOfferedSaved { get; private set; }

    /// <summary>
    /// 这一回合里**有几只、一共会炸出多少**（0 = 本场没人会炸）。
    ///
    /// 为什么要预报这件事：横扫 100 局里「倒下自爆」排第一死因（7 / 28 局），
    /// 而死掉的那一屏上**一个字都没写**。手册里有，战斗里没有——那就等于没有。
    ///
    /// 口径是「这一回合可能炸到你身上的」：还活着的都在名单里（你这回合可能把它打死），
    /// 已经倒下但还没炸过的也算（毒死的那只会在回合收尾炸）。
    /// 自爆的两个来源都在这里收口：敌人模板的 `ExplodeOnDeath` 与怨恨层给全体挂的那一份，
    /// 两处都走 `DeathBlastOf` —— 界面、探针、自检读的也是它，不是各写一遍的算式。
    /// </summary>
    public (int Damage, int Count, string? Name) ExplodeOnDeathForecast()
    {
        var total = 0;
        var count = 0;
        string? who = null;
        foreach (var foe in Enemies())
        {
            if (_exploded.Contains(foe)) continue;          // 已经炸过的不再报
            var damage = DeathBlastOf(foe);
            if (damage <= 0) continue;
            count++;
            total += damage;
            if (who is null || damage > 0) who ??= foe.Name;
        }
        return (total, count, who);
    }

    /// <summary>
    /// 一只敌人倒下时会炸多少（0 = 它不会炸）。预告与结算读同一处：
    /// 模板那一份走 `TemplateBlast`，机制那一份走 `FloorMechanics.SpitefulBlast` 那条
    /// 纯规则算式（`SettleFloorMechanic` 的 `DeathBlast` 读的也是它）。
    /// </summary>
    public int DeathBlastOf(Combatant enemy)
    {
        var raw = TemplateBlast(enemy.BaseAttack, enemy);
        var mech = FloorMechanics.SettleFloorMechanic(FloorMechanic, Dead(enemy), Player, _rng);
        if (mech?.DeathBlast is { } boom) raw += boom;
        return raw;
    }

    /// <summary>
    /// 界面上那句「它倒下会炸 N 点」读它：只在「那只已经低到可能被一刀带走」时才非 0。
    ///
    /// 为什么不直接报 `BlastThisTurn()`：那个数是「这一回合可能炸到的全部」，
    /// 每一屏都挂一句「它倒下会炸」，玩家两天就学会不看这一行。
    /// 而 `EnemyForecast` 那一行**本来就只报下一刀**——「它剩 5 点、我一刀 8 点」
    /// 是最容易算错的一次，也是自爆真正要命的那一次。
    /// </summary>
    public int ForeseeableExplosionDamage()
    {
        // 「一刀的常见上限」：玩家在当前面板下的平砍上限。技能与暴击会更高，
        // 这里刻意保守——宁可多提醒一次，也不要漏掉一次会死人的自爆。
        var blade = Math.Max(1, (int)Math.Round(PlayerAtk() * (1 + _opts.Rules.DamageVariance)));
        var worst = 0;
        foreach (var e in LivingEnemies())
        {
            if (e.Hp > blade) continue;
            var boom = DeathBlastOf(e);
            if (boom > worst) worst = boom;
        }
        return worst;
    }

    /// <summary>模板上的「倒下自爆」占多少（没有就是 0）</summary>
    private int TemplateBlast(int attack, Combatant enemy)
    {
        if (!_enemyDefs.TryGetValue(enemy, out var def)) return 0;
        var ratio = def.ExplodeOnDeath ?? 0;
        return ratio > 0 ? Math.Max(1, (int)Math.Round(attack * ratio)) : 0;
    }

    /// <summary>
    /// 一个「已经倒下」的副本：机制的 `EnemySettled` 只在 Hp ≤ 0 时才给自爆，
    /// 而预告要问的对象是还活着的那一只。复制一份把血打成 0，问完就丢——
    /// 这样机制表里那条判据只有一份，预告侧不必复述一遍。
    /// </summary>
    private static Combatant Dead(Combatant enemy) => new()
    {
        Name = enemy.Name,
        Kind = enemy.Kind,
        Hp = 0,
        BaseMaxHp = enemy.BaseMaxHp,
        BaseAttack = enemy.BaseAttack,
        BaseDefense = enemy.BaseDefense,
    };

    /// <summary>
    /// 这一回合的自爆会打掉多少：合计过一次受伤侧缩放（脆弱躯壳这类词条对它也生效）。
    /// 「预告」与「结算」在这一层是同一条链——`HurtPlayer` 也乘同一个 `IncomingScale`。
    /// </summary>
    public int BlastThisTurn()
    {
        var (total, _, _) = ExplodeOnDeathForecast();
        if (total <= 0) return 0;
        return Math.Max(1, (int)Math.Round(total * AffixMath.IncomingScale(_affixes)));
    }

    /// <summary>
    /// 这一回合玩家**真的**会掉多少血：下一刀 + 这一回合收尾的毒 + 倒下自爆。
    ///
    /// 与 `LethalThisTurnExtra` 的区别是它把三条都算进去，而且都按**结算口径**：
    /// 毒被这一层的免死额度兜住时只扣到剩 1 点，自爆要过脆弱躯壳那一层。
    /// 界面上那句「本回合合计 X~Y」读的就是它——**看得见的数必须就是结算用的数**。
    /// </summary>
    public (int Low, int High) ThisTurnTotal()
    {
        var (low, high, _) = EnemyForecast();
        if (!UpcomingEnemyActs) { low = 0; high = 0; }
        var poison = PoisonTickThisTurn();
        var blast = BlastThisTurn();
        return (low + poison + blast, high + poison + blast);
    }

    /// <summary>
    /// 回合收尾那一记毒**实际**会扣多少（含本层「还剩几次毒死额度」那条规则）。
    ///
    /// 与 `PoisonHpThisTurn()` 是同一个数，只多一层「没中毒就是 0」的短路：
    /// 那两个名字指同一个地方，是为了让读代码的人从「合计」和「毒」两头都能找到它。
    /// </summary>
    public int PoisonTickThisTurn() => PoisonHpThisTurn();

    /// <summary>
    /// 毒这一回合** realmente ** 想扣多少（按血条口径算出来的那一笔，封在 1..`MaxPoisonPower`）。
    ///
    /// 它以前是一条常数链：`PoisonPowerFor(基础攻击 × 比例)` 被夹在 1..6，
    /// 于是第 1~15 层、60 血的面板与 137 血的面板，每一回合的毒都是 6 点。
    /// 结果是这个游戏里没有任何一个局面会让人认真地考虑解毒草：
    /// 100 局横扫 311 个中毒回合，「毒够重」（毒 ≥ 血）**0 次**。
    ///
    /// 现在毒按**玩家血条**的一小截算（`PoisonShareOfMaxHp`），再夹在
    /// 1..`PoisonCapOfMaxHp` 之间。两条边都是比例的，所以第 1 层和第 15 层
    /// 的毒是同一件事的同一个份量——不是一条常数，也不是一条指数。
    /// </summary>
    public int PoisonHpPerTurn()
    {
        if (!EntityRules.IsPoisoned(Player) || Player.PoisonTurns <= 0) return 0;
        return EntityRules.PoisonTickFor(Player, Math.Max(1, _opts.MaxHp(Player)));
    }

    /// <summary>
    /// 这一回合玩家**一共**会掉多少血，而不只是「下一刀」多少。
    ///
    /// 起因是改完之后最常见的一种死法（横扫 30 局里排第一）：
    /// 「这回合先吃 50，再吃 37」——敌人先毒爆打一刀并挂上毒，
    /// 而毒在回合收尾还要再扣一次。卡片上只报了「下一刀预计 50~50」，
    /// 玩家按那个数算出「还剩 20 点，撑得住」，然后就死了。
    /// 数字一个都没错，错的是它只报了一半。
    ///
    /// 这里加的是「已经挂在你身上、这一回合收尾会扣掉的那部分」。
    ///
    /// 毒被封在 `EntityRules.MaxPoisonPower` 之后，最险的一次变成「毒 6 点 / 当时 28 点血」——
    /// 也就是说真正会要命的是毒**加上这一刀**，而这一刀是可见的。这时候玩家需要的
    /// 不是「还有 6 点会扣」，而是「一共还有多久、哪一下会把你按死」。
    /// 所以这一回合**会掉多少**与**还剩几回合**一起报：只报一个数，
    /// 玩家会以为吃完这一下就安全了，而毒还剩一回合。
    /// </summary>
    public int LethalThisTurnExtra() => PoisonHpPerTurn();

    /// <summary>
    /// 回合收尾那一记毒，**真的会把玩家带走吗**。
    ///
    /// 「这一回合掉多少血」和「这一回合会不会死」不是同一个问题：毒伤算得清，
    /// 而毒到底许不许杀人，得看这一层还剩几次额度（`Game.PoisonDyingBudget`，
    /// 每层 floor(层数×5%) 次——额度用完毒继续疼，但只扣到剩 1 点）。
    /// 探针的「解毒草该不该吃」读的就是这一句：它问的是「吃不吃得成活下去」，
    /// 不是「毒的数字大不大」。规则只有一个入口，判据也只有一个入口。
    /// </summary>
    public bool PoisonWouldKillPlayer()
    {
        // 判据两半：毒够重（整笔 ≥ 血）、而且这一层还剩额度。
        //
        // 传进去的是**整笔毒伤**，不是当前血量：`PoisonLethalAllowed` 里那句
        // `damage < c.Hp` 是全游戏唯一一处判「这一下会不会致死」的地方，
        // 把一个被血封顶的数传给它，等于让它自己和自己比——
        // 血 4 点、毒 6 点时它拿到的是 4，`4 < 4` 不成立、直接放行，
        // 于是「额度还在」与「额度用完」在它眼里没有区别（`PoisonHpThisTurn`
        // 那条封顶也正是为了让预告显示「会掉 4 点」而不是「6 点」）。
        var power = LethalThisTurnExtra();
        return power > 0 && Player.Hp > 0 && Player.Hp <= power &&
               EntityRules.PoisonLethalAllowed(Player, power);
    }

    /// <summary>
    /// 玩家这一手会打掉多少（区间）——**和结算同一条修正链**，只是不算随机那一层。
    ///
    /// 为什么需要它：敌人卡把「它这一刀打你多少」写得很清楚（`EnemyForecast`），
    /// 而它自己还剩多少刀才倒、你这一刀能打掉它多少，一样都看不见。玩家于是只能
    /// 用「下一刀 12~14 点」去反推「我这一下够不够」，而那两件事不是同一条算式。
    /// 第 59 轮的死因表里排第一的就是这一类：`普攻:深渊猎犬 7 局`——
    /// 它们不是打不过，是玩家算不出「再挨两刀还是三刀」。
    ///
    /// 口径：普攻 / 技能 / 炸弹各一条，读的就是 `PlayerAttack`、`Skills.UseSkill`、
    /// `PlayerItemDamage` 会走的那几个入口。它不改任何状态，只有界面与探针读。
    /// </summary>
    public (int Low, int High) PlayerHitForecast(string? skillId = null, int? bombDamage = null)
    {
        var target = CurrentTarget();
        var variance = _opts.Rules.DamageVariance;
        if (bombDamage is { } flat)
        {
            // 炸弹无视防御、也不吃浮动：它写多少就是多少（`CombatMath.FixedDamage`）。
            var d = Math.Max(1, flat);
            return (d, d);
        }
        if (skillId is not null && Player.Skills.FirstOrDefault(x => x.Id == skillId) is { } skill)
        {
            // 只有会伤人的那三类才报数：治疗、加防、闪避、降攻、驱散的手上
            // 根本没有「这一下打掉多少」可言——报 0 比报一个假的数好。
            if (skill.Type is not (SkillType.Damage or SkillType.Drain or SkillType.Poison)) return (0, 0);
            // 技能不吃防御折价，但吃「技能自己那条算式」：攻击力 × 倍率 × 强化。
            var raw = PlayerAtk() * skill.Power * (Player.AttackMultiplier > 1 ? Player.AttackMultiplier : 1);
            var slo = Math.Max(1, (int)Math.Round(raw * (1 - variance / 2)));
            var shi = Math.Max(1, (int)Math.Round(raw * (1 + variance / 2)));
            return (slo, shi);
        }
        var atk = PlayerAtk();
        if (Player.AttackMultiplier > 1) atk = (int)Math.Round(atk * Player.AttackMultiplier);
        var def = EntityRules.EffectiveDefense(target, _opts.AttackerDef(target));
        var lo = Math.Max(1, (int)Math.Round(atk * (1 - variance) - def));
        var hi = Math.Max(1, (int)Math.Round(atk * (1 + variance) - def));
        return (lo, hi);
    }

    /// <summary>
    /// 按「还要几刀」说人话：这一只还要吃几刀才倒。
    /// 读的是 `PlayerHitForecast` 的下限——宁可报多一刀，也不要让玩家以为下一刀就结束。
    /// </summary>
    public int SwingsToKill()
    {
        var (low, _) = PlayerHitForecast();
        var hp = LivingEnemies().Sum(e => Math.Max(0, e.Hp));
        if (hp <= 0) return 0;
        return (int)Math.Ceiling(hp / (double)Math.Max(1, low));
    }

    /// <summary>身上还挂着几回合毒（0 = 没中毒）：预告里要写「含中毒 6×2 回合」，玩家才知道下一回合还会再扣一次</summary>
    public int PlayerPoisonTurnsLeft => EntityRules.IsPoisoned(Player) ? Math.Max(0, Player.PoisonTurns) : 0;

    /// <summary>
    /// Boss 这一下的攻击倍率。两个来源合流到一处，因为它们读的不是同一个前提：
    /// 周期爆发（熔岩爆裂 / 冰镜）看「本回合标记」，常驻规则（深渊领域）一直生效。
    /// 早先 EnemyAttackResult 与 EnemyForecast 各抄了一遍，两处都只判 IsArmed，
    /// 于是常驻的那一套乘区永远读不到。
    /// </summary>
    private double BossMultiplier(Combatant actor)
    {
        if (Boss?.Id is null) return 1;
        if (!Boss.AlwaysOn && Boss.IsArmed(actor) != true) return 1;
        return GameDataStore.Get().BossMechanics.TryGetValue(Boss.Id, out var def) && def.Multiplier is { } m ? m : 1;
    }

    /// <summary>本场机制要穿掉玩家多少比例的防御（灼烧层 30%，其余 0）</summary>
    private double PiercedDefense() => FloorMechanic == "searing" ? FloorMechanics.SearingPierce : 0;

    /// <summary>
    /// 玩家挨打的词条乘区：常驻加成（脆弱躯壳）+ 首次加成（狂意）。
    /// 「首次」的标记必须在真正收钱的那一刻置位——见 MarkRiskHit。
    /// </summary>
    private double IncomingScale()
    {
        var first = _affixes.RiskIncomingPct > 0 && Player.RiskHitTaken != true ? 1 + _affixes.RiskIncomingPct : 1;
        return AffixMath.IncomingScale(_affixes) * first;
    }

    /// <summary>
    /// 记下「本层我第一次挨打了」。
    ///
    /// 以前 Player.RiskHitTaken 只在构造函数里归零，**全工程没有第二处写它**：
    /// 于是「狂意」写着的「每层首次受到的伤害 +20%」变成了「本层每一刀都 +20%」，
    /// 而且越往后越值——一条标明「有代价」的词条成了纯赚。
    /// 三处入口（敌人普攻、敌人技能、各种非攻击伤害）都要记，
    /// 所以判据放在这里，而不是散在调用点。
    /// </summary>
    private void MarkRiskHit() => Player.RiskHitTaken = true;

    /// <summary>
    /// 「不屈」：致命伤时保留 1 点生命，每场战斗一次。
    /// 所有让玩家掉血的路径都经它判一次，漏一条就是「买了保险但没保到」。
    /// </summary>
    private bool CheckSurvive()
    {
        if (Player.Hp > 0) return false;
        var saved = AffixMath.TrySurviveLethal(Player, _affixes, Player.UsedSurviveLethal == true);
        if (saved is null) return false;
        Player.UsedSurviveLethal = true;
        if (saved.Log is not null) PushLog(saved.Log);
        BattleEvents.EmitDamageNumber("player", -1, "heal");
        Phase = BattlePhase.Player;
        return true;
    }

    /// <summary>回合收尾：清防御、结算中毒、递减冷却</summary>
    private void EndRound()
    {
        // 玩家的姿态当回合末清：它摆在回合开头，敌人紧接着就会还以颜色。
        // 敌人的姿态反过来——它摆在回合末尾，要一直护到玩家的下一刀。
        Player.Defending = false;
        foreach (var c in Enemies())
        {
            if (!_guarding.Contains(c)) c.Defending = false;
        }
        _guarding.Clear();
        // 加护祭司的光环：每 3 回合给同房间活着的队友 +3 防御。
        TickGuardAura();
        var enemyPoison = 0;
        foreach (var c in Enemies()) enemyPoison += TickPoisonFor(c);
        var playerPoison = TickPoisonFor(Player);
        PoisonDamage = enemyPoison + playerPoison;
        // 临时状态按回合衰减：闪避只保一回合，降攻/破防按各自的剩余回合走
        EntityRules.TickStatus(Player);
        foreach (var c in Enemies()) EntityRules.TickStatus(c);
        // 闪避分两份：楼层机制给的常驻 + 技能给的一回合。
        EntityRules.ExpireEvade(Player);
        foreach (var c in Enemies()) EntityRules.ExpireEvade(c);
        // 毒伤不进这里的统计：TickPoisonFor → HurtPlayer 已经记过一笔了。
        Skills.TickCooldowns(Player);
        foreach (var c in Enemies()) Skills.TickCooldowns(c);
        // 楼层机制的回合收尾：回响/魂腐的每回合回血、怨恨层的自爆判定都走这里。
        foreach (var c in LivingEnemies())
        {
            var settled = FloorMechanics.SettleFloorMechanic(FloorMechanic, c, Player, _rng);
            if (settled is not null) ApplyMechanic(settled);
        }
        // 只有「周期爆发」型机制需要收标记：熔岩爆裂与深渊领域都写着「当回合 / 张手的那一回合」。
        // 常驻规则没有标记可收——它从第一回合就张着。
        if (Boss is not null && Boss.Id != "frost_mirror" && !Boss.AlwaysOn) Boss.Consume(Enemy);
        // 中毒/反噬放倒的敌人也要炸：狂信者被毒死和你打死一样会自爆。
        var boomKilled = ExplodeDeadEnemies();
        if (LivingEnemies().Count == 0 || boomKilled)
        {
            Phase = BattlePhase.Won;
            PushLog("敌人被全部击败了！");
            // 组合战斗的收尾补给：每多打倒一只，回复 3% 最大生命。
            if (Allies.Count > 0)
            {
                var max = _opts.MaxHp(Player);
                var amount = (int)Math.Round(max * 0.03 * Enemies().Count);
                var healed = Math.Min(amount, max - Player.Hp);
                if (healed > 0)
                {
                    Player.Hp += healed;
                    PushLog($"你从倒下的敌人身上缓过一口气，回复 {healed} 点生命");
                    BattleEvents.EmitDamageNumber("player", -healed, "heal");
                }
            }
            return;
        }
        if (Player.Hp <= 0)
        {
            Phase = BattlePhase.Lost;
            PushLog("你被毒素放倒了……");
            return;
        }
        Phase = BattlePhase.Player;
        StartRound();
    }

    /// <summary>
    /// 新回合开始：推进计数，到点就展开 Boss 机制。
    /// 展开只置一个标记，真正生效在敌人的下一次攻击与玩家的下一次技能——
    /// 这样玩家有整整一个回合的时间看到「冰镜展开了」，来得及按防御。
    /// </summary>
    private void StartRound()
    {
        Round += 1;
        // 负面词条（诅咒房紫装）：每回合开始扣血。放在回合开头，玩家在行动前就能看到血掉了。
        var loss = AffixMath.TurnStartLoss(_affixes);
        if (loss > 0 && Player.Hp > 0)
        {
            PushLog($"血蚀锋刃反噬，你失去 {loss} 点生命");
            HurtPlayer(loss);
            if (Player.Hp <= 0)
            {
                Phase = BattlePhase.Lost;
                PushLog("你被血蚀锋刃拖垮了……");
                return;
            }
        }
        if (Boss is null) return;
        var def = Boss.Id is not null && GameDataStore.Get().BossMechanics.TryGetValue(Boss.Id, out var bd) ? bd : null;
        if (def?.Every is not { } every || Round % every != 0) return;
        if (Boss.Arm?.Invoke(Enemy) == true)
        {
            // 每种机制一句专属预告：玩家要在「挨打之前」知道该防什么。
            var warn = Boss.Id switch
            {
                "frost_mirror" => $"{Enemy.Name}展开一面冰镜——下一次攻击会被反弹，先防住！",
                "abyss_domain" => $"{Enemy.Name}张开领域——你的技能冷却将被打断，强化也会被驱散！",
                _ => $"{Enemy.Name}爆开一层岩浆壳，攻势骤然暴涨！",
            };
            PushLog(warn);
            LastMechanicNote = warn;
            // 深渊领域：说明写着「驱散你的强化」，那就在张手的这一回合真的驱散。
            // 以前这一笔挂在「玩家施法」上，而玩家施法时身上的强化永远是空的
            // （他没有任何技能能给自己加强化），于是这句话一次都没生效过。
            if (Boss.Id == "abyss_domain")
            {
                var had = Player.AttackMultiplier > 1 || Player.AtkModTurns > 0 || Player.DefModTurns > 0;
                if (had && FloorMechanics.DispelBuffs(Player))
                {
                    PushLog("你的强化被深渊吞没了");
                    LastMechanicNote = "你的强化被深渊吞没了";
                }
            }
            // 熔岩爆裂：展开的同一秒就把灼烧挂上，玩家当回合就能在状态行看到。
            if (Boss.Id == "molten_burst")
            {
                ApplyMechanicBurn(Player, 2);
                PushLog("岩浆溅到你身上，你被灼烧了 2 回合");
            }
        }
    }

    /// <summary>
    /// 熔岩爆裂的灼烧：复用中毒的持续掉血，但文案说「灼烧」。
    /// 不为它单开一套状态——玩家要的只是「每回合掉血 + 看得见」。
    /// </summary>
    private void ApplyMechanicBurn(Combatant player, int turns)
    {
        if (FloorMechanics.PoisonImmune(player)) return;
        // 灼烧与瘴气同一个口径：按**玩家血条**折算、有下限、封在 MaxPoisonPower。
        EntityRules.ApplyPoison(player, turns, FloorMechanics.BurnPoisonPower(player.BaseAttack, _opts.MaxHp(player)));
    }

    /// <summary>单方中毒结算；把日志写清楚，否则玩家只会看到血莫名少了一截</summary>
    private int TickPoisonFor(Combatant c)
    {
        if (!EntityRules.IsPoisoned(c)) return 0;
        // 只取「该掉多少」，不让 TickPoison 扣血：扣血交给 HurtPlayer 一处，
        // 否则玩家中毒时每回合掉的是说明写的两倍。
        var dealt = EntityRules.PoisonTickDamage(c);
        // 玩家身上的毒有一条「本层还能毒死几次」的预算（见 Game.PoisonDyingBudget）。
        // 额度用完之后，这一下只扣到剩 1 点为止——毒继续疼，但不再替别的机制做决定。
        //
        // 判据问的是「这一下会不会把一个还活着的人带走」，而 `PoisonLethalAllowed`
        // 拿它和自己的 `c.Hp` 比——两边必须是同一个血量。
        //
        // 这里以前踩过一个坑：`ApplyDamage` 在**扣完血之后**才问这条判据，
        // 于是 `damage < c.Hp` 里的 `Hp` 已经是扣过的值（毒 40、血 3 → `Hp` 变 0），
        // 判据永远放行，预算是死代码。现在扣血那一处自带同一个判断（见 `CombatMath.ApplyDamage`），
        // 这里只负责「兜住之后日志要说出来」。
        var lethalBlocked = c.IsPlayer && dealt > 0 && !EntityRules.PoisonLethalAllowed(c, dealt);
        if (lethalBlocked)
        {
            // 只扣到剩 1 点：毒继续疼，但这一下不再替「这一层还能毒死几次」做决定。
            var capped = Math.Max(0, c.Hp - 1);
            if (capped <= 0) PushLog("毒素涌上来，但你硬撑着没有倒");
            else
            {
                PushLog($"你受毒素侵蚀，失去 {capped} 点生命（撑住了，毒素没能带走你）");
                HurtPlayer(capped);
            }
            c.PoisonTurns -= 1;
            if (c.PoisonTurns <= 0) c.PoisonPower = 0;
            return capped;
        }
        if (dealt > 0)
        {
            if (c.IsPlayer)
            {
                PushLog($"你受毒素侵蚀，失去 {dealt} 点生命");
                HurtPlayer(dealt);
                // 记账要在扣血**之后**：`HurtPlayer` 里还挂着受伤侧乘区与「不屈」，
                // 真正的死因得看那之后的血，不是这一行日志里的数。
                if (c.Hp <= 0) EntityRules.NotePoisonDying(c);
            }
            else
            {
                PushLog($"{c.Name}受毒素侵蚀，失去 {dealt} 点生命");
                BattleEvents.EmitDamageNumber("enemy", dealt, "dot");
                CombatMath.ApplyDamage(c, CombatMath.FixedDamage(dealt));
            }
        }
        // 回合数照烧，但不再扣血。
        c.PoisonTurns -= 1;
        if (c.PoisonTurns <= 0) c.PoisonPower = 0;
        return dealt;
    }
}
