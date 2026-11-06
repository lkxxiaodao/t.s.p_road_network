using SimCore.Model;

namespace SimCore.Sim;

// ---------------------------------------------------------------------------
// T1.2 · 信号状态机与相位推进（技术方案03 §5 · T1.2）
//
// 本文件落地 docs/SimCore-API.md §4.5 冻结的 SignalStateMachine，并连带实现它必须依赖的
// 两个同属 T1.2 的冻结类型（覆盖矩阵 §9.1 T1.2 行）：
//   * §4.4 SignalPhaseState（Green / Yellow / AllRed / GreenFlash）
//   * §4.7 SignalTiming（黄灯 / 全红 / 绿闪时长；Default = new(3.0, 2.0, 0.0)）
// 类型命名、成员签名、语义与契约逐字一致；只调用 Model 层，未修改任何已冻结接口。
//
// 铁律：无 Godot 命名空间、无第三方依赖、无系统随机数（步进完全确定性）。
// ---------------------------------------------------------------------------

/// <summary>
/// 信号相位状态（<c>docs/SimCore-API.md</c> §4.4 冻结枚举；运行期状态，不写入 <c>.spmap</c>）。
/// </summary>
/// <remarks>
/// <para>成员名称与顺序为冻结契约（附 A），不得改名 / 增删 / 调序；顺序即数值。</para>
/// <para>语义：一个相位（<see cref="Stage"/>）的**放行窗口**在其绿灯与绿闪期间，
/// 黄灯 / 全红期间全部转向禁行（§4.5「Phase 掩码」）。</para>
/// </remarks>
public enum SignalPhaseState
{
    /// <summary>绿灯：按 <see cref="Stage.PriorityOf(int)"/> 放行本相位保护 / 允许转向。</summary>
    Green,

    /// <summary>黄灯：警示清空，本相位全部转向禁行（Banned）。</summary>
    Yellow,

    /// <summary>全红：路口清空，本相位全部转向禁行（Banned）。</summary>
    AllRed,

    /// <summary>绿闪：绿灯结束前的闪烁预警，放行口径与绿灯一致。</summary>
    GreenFlash,
}

/// <summary>
/// 清空时间与配时常量（<c>docs/SimCore-API.md</c> §4.7 冻结；黄灯 / 全红 / 绿闪时长）。
/// </summary>
/// <remarks>
/// <para>这三个时长**不写入** <c>.spmap</c>（<c>stages[].duration</c> 仅承载绿灯时长，§2.6 / 附 D8）：
/// 由本类型为全部相位统一提供。黄 / 全红 / 绿闪出现在**每个相位**的绿灯之后。</para>
/// <para>本类型为不可变配置；构造时校验三个时长均 ≥ 0（§1.3 参数校验：越界抛
/// <see cref="ArgumentOutOfRangeException"/>）。</para>
/// <para>说明：§4.7 另声明了三个 <c>[表现层]</c> 静态方法（<c>ClearanceTime</c> / <c>SuggestedYellow</c> /
/// <c>SuggestedAllRed</c>）。按覆盖矩阵它们属 **T3.3 相位控制面板**（"建议清空时间"），
/// 且需要路口几何（<c>CompiledNetwork</c>）——不在 T1.2 范围，故本卡不实现，避免越界。</para>
/// </remarks>
public sealed class SignalTiming
{
    /// <summary>默认清空时间：黄灯 3.0s、全红 2.0s、绿闪 0.0s（§4.7 冻结字面）。</summary>
    public static SignalTiming Default { get; } = new(3.0d, 2.0d, 0.0d);

    /// <summary>构造一套清空时间配置。</summary>
    /// <param name="yellowTime">黄灯时长（秒，≥ 0）。</param>
    /// <param name="allRedTime">全红时长（秒，≥ 0）。</param>
    /// <param name="greenFlashTime">绿闪时长（秒，≥ 0；默认 0，即不启用绿闪）。</param>
    /// <exception cref="ArgumentOutOfRangeException">任一参数为负 / 非有限。</exception>
    public SignalTiming(double yellowTime, double allRedTime, double greenFlashTime = 0d)
    {
        YellowTime = RequireNonNegative(yellowTime, nameof(yellowTime));
        AllRedTime = RequireNonNegative(allRedTime, nameof(allRedTime));
        GreenFlashTime = RequireNonNegative(greenFlashTime, nameof(greenFlashTime));
    }

    /// <summary>黄灯时长（秒）。</summary>
    public double YellowTime { get; }

    /// <summary>全红时长（秒）。</summary>
    public double AllRedTime { get; }

    /// <summary>绿闪时长（秒）；0 表示不启用绿闪。</summary>
    public double GreenFlashTime { get; }

    /// <inheritdoc />
    public override string ToString()
        => $"SignalTiming(yellow={YellowTime:0.###}, allRed={AllRedTime:0.###}, greenFlash={GreenFlashTime:0.###})";

    private static double RequireNonNegative(double value, string name)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0d)
        {
            throw new ArgumentOutOfRangeException(name, value, "清空时间必须为有限的非负数。");
        }

        return value;
    }
}

/// <summary>
/// 信号状态机（T1.2；<c>docs/SimCore-API.md</c> §4.5 冻结）。
/// </summary>
/// <remarks>
/// <para>职责：把某一路口的配时方案（<see cref="Node.SignalPlans"/>）在运行期展开成
/// <b>可查询的当前相位</b>，并负责相位推进（<b>绿 → 绿闪 → 黄 → 全红 → 下一相位</b>）、
/// 周期与相位倒计时、相位差 <see cref="Node.Offset"/> 对齐，以及运行期改配时的「下一周期生效」。</para>
///
/// <para><b>相位推进口径（§4.5）</b>：每个相位依次经历
/// <c>Green(Duration)</c> → <c>GreenFlash(GreenFlashTime)</c> → <c>Yellow(YellowTime)</c> →
/// <c>AllRed(AllRedTime)</c>，之后进入下一相位的 <c>Green</c>；最后一个相位走完后回到第 0 相位，
/// 即进入下一周期。时长为 0 的子相位被<b>跳过</b>（默认绿闪 0 → 不出现 <see cref="SignalPhaseState.GreenFlash"/>）。</para>
///
/// <para><b>周期长度</b>：<c>Σ(绿灯) + Σ(黄 + 全红 + 绿闪)</c>（§4.5）——每个相位各计一次黄 / 全红 / 绿闪。</para>
///
/// <para><b>放行掩码</b>：<see cref="SignalPhaseState.Green"/> 与 <see cref="SignalPhaseState.GreenFlash"/>
/// 期间由 <see cref="Stage.PriorityOf(int)"/> 放行；<see cref="SignalPhaseState.Yellow"/> 与
/// <see cref="SignalPhaseState.AllRed"/> 期间一律 <see cref="TurnPriority.Banned"/>（全红清空，T1.2 b）。</para>
///
/// <para><b>相位差 offset（T1.2 d）</b>：节点处于周期内位置
/// <c>p = ((t − offset) mod C + C) mod C</c>。即 <c>offset</c> = 本路口绿波参考点（第 0 相位绿灯起点）
/// 相对主时钟的时刻；<c>offset = 0</c> 即 <c>p = t mod C</c>。
/// <see cref="SyncToTime(double)"/> 按此式对齐初始相位。</para>
///
/// <para><b>下一周期生效（T1.2 c）</b>：<see cref="ApplyPlan(SignalPlan)"/> 只登记待生效方案，
/// <b>不打断当前相位</b>；在当前周期走完（相位环回到第 0 相位绿灯起点）时，待生效方案才替换当前方案。</para>
///
/// <para><b>时段方案选择</b>：若节点配有多套 <see cref="SignalPlan"/>（时变配时），按
/// <see cref="SignalPlan.StartTime"/> 选择当前生效方案（取 <c>StartTime ≤ simTime</c> 中最大者，
/// 均晚于 <c>simTime</c> 时取最早者）；选择在 <see cref="SyncToTime(double)"/> 与每个周期边界处评估。</para>
///
/// <para><b>确定性（铁律 2）</b>：状态是「仿真时刻 + 方案 + offset」的纯函数，无随机、无系统时钟；
/// 同一输入序列两次运行结果逐项一致。</para>
/// </remarks>
public sealed class SignalStateMachine
{
    private readonly Node _node;
    private readonly SignalTiming _timing;
    private readonly List<SignalPlan> _plans;

    private SignalPlan? _pendingPlan;   // ApplyPlan 登记、下一周期边界生效
    private int _planIndex;             // 当前生效方案
    private double _simTime;            // 最近一次 Tick / SyncToTime 的仿真时刻
    private double _cyclePosition;      // 周期内位置 ∈ [0, CycleLength)

    /// <summary>构造一个信号状态机。</summary>
    /// <param name="node">信号路口节点（提供 <see cref="Node.Offset"/> 与 <see cref="Node.SignalPlans"/>）；只读，不被修改。</param>
    /// <param name="timing">黄 / 全红 / 绿闪时长（通常取 <see cref="SignalTiming.Default"/>）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> 或 <paramref name="timing"/> 为 null。</exception>
    /// <exception cref="ArgumentException">
    /// 节点未配置任何方案（<c>signals[].plans</c> 为空），或某方案不含相位（<c>stages</c> 为空）——
    /// 二者均属非法输入（校验器报 <c>signalized_without_phases</c> / 不可运行的信号）。
    /// </exception>
    public SignalStateMachine(Node node, SignalTiming timing)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(timing);

        if (node.SignalPlans.Count == 0)
        {
            throw new ArgumentException(
                $"信号节点 #{node.Id} 未配置任何配时方案（signals[].plans 为空），无法运行信号状态机。",
                nameof(node));
        }

        _node = node;
        _timing = timing;
        _plans = new List<SignalPlan>(node.SignalPlans);

        for (int i = 0; i < _plans.Count; i++)
        {
            if (_plans[i].Stages.Count == 0)
            {
                throw new ArgumentException(
                    $"信号节点 #{node.Id} 的第 {i} 个配时方案不含任何相位（stages 为空），无法运行信号状态机。",
                    nameof(node));
            }
        }

        _planIndex = SelectPlanIndex(0d);
        _simTime = 0d;
        _cyclePosition = Normalize(-_node.Offset, CycleLength);
    }

    /// <summary>节点 id（= §4.5 <c>NodeId</c>）。</summary>
    public int NodeId => _node.Id;

    /// <summary>当前生效方案的下标（按 <see cref="SignalPlan.StartTime"/> 选时段，§4.5 <c>PlanIndex</c>）。</summary>
    public int PlanIndex => _planIndex;

    /// <summary>当前相位所属的相位下标（0 起）。</summary>
    public int StageIndex => Locate(_cyclePosition).StageIndex;

    /// <summary>当前相位状态（绿 / 黄 / 全红 / 绿闪，§4.5 <c>Phase</c>）。</summary>
    public SignalPhaseState Phase => Locate(_cyclePosition).Phase;

    /// <summary>当前相位已持续时长（秒，§4.5 <c>TimeInPhase</c>）。</summary>
    public double TimeInPhase
    {
        get
        {
            PhaseSlice slice = Locate(_cyclePosition);
            return _cyclePosition - slice.Start;
        }
    }

    /// <summary>当前相位剩余时长（秒，§4.5 <c>PhaseRemaining</c>）；该相位倒计时。</summary>
    public double PhaseRemaining
    {
        get
        {
            PhaseSlice slice = Locate(_cyclePosition);
            return slice.End - _cyclePosition;
        }
    }

    /// <summary>
    /// 周期长度（秒，§4.5 <c>CycleLength</c>）= <c>Σ(绿灯) + Σ(黄 + 全红 + 绿闪)</c>。
    /// </summary>
    public double CycleLength
    {
        get
        {
            IReadOnlyList<Stage> stages = _plans[_planIndex].Stages;
            double green = 0d;
            for (int i = 0; i < stages.Count; i++)
            {
                green += stages[i].Duration;
            }

            double clearance = _timing.GreenFlashTime + _timing.YellowTime + _timing.AllRedTime;
            return green + (stages.Count * clearance);
        }
    }

    /// <summary>当前相位对象（§4.5 <c>CurrentStage</c>），供门控 / 渲染读取放行集合。</summary>
    public Stage CurrentStage => _plans[_planIndex].Stages[Locate(_cyclePosition).StageIndex];

    /// <summary>
    /// 依 <see cref="Node.Offset"/> 把状态机对齐到给定仿真时刻（§4.5 <c>SyncToTime</c>，T1.2 d）。
    /// </summary>
    /// <param name="simTime">仿真时刻（秒）。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="simTime"/> 非有限。</exception>
    public void SyncToTime(double simTime)
    {
        if (double.IsNaN(simTime) || double.IsInfinity(simTime))
        {
            throw new ArgumentOutOfRangeException(nameof(simTime), simTime, "仿真时刻必须为有限值。");
        }

        _simTime = simTime;
        _planIndex = SelectPlanIndex(simTime);
        _cyclePosition = Normalize(simTime - _node.Offset, CycleLength);
    }

    /// <summary>
    /// 提交新配时方案，<b>下一周期生效</b>，不打断当前相位（§4.5 <c>ApplyPlan</c>，T1.2 c）。
    /// </summary>
    /// <param name="plan">新配时方案（须至少含一个相位）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="plan"/> 不含任何相位。</exception>
    public void ApplyPlan(SignalPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Stages.Count == 0)
        {
            throw new ArgumentException("新配时方案必须至少包含一个相位（stages 非空）。", nameof(plan));
        }

        _pendingPlan = plan;
    }

    /// <summary>推进一个固定步长（§4.5 <c>Tick</c>）。</summary>
    /// <param name="dt">步长（秒，须 &gt; 0 且有限）。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dt"/> 非正 / 非有限。</exception>
    public void Tick(double dt)
    {
        if (double.IsNaN(dt) || double.IsInfinity(dt) || dt <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(dt), dt, "步长必须为有限正值。");
        }

        _simTime += dt;
        _cyclePosition += dt;

        double cycle = CycleLength;
        while (_cyclePosition >= cycle)
        {
            _cyclePosition -= cycle;
            OnCycleBoundary();
            cycle = CycleLength;
        }
    }

    /// <summary>
    /// 当前相位下某条转向的优先级（§4.5 <c>PriorityOf</c>）：绿 / 绿闪期间透传
    /// <see cref="Stage.PriorityOf(int)"/>；黄 / 全红期间一律 <see cref="TurnPriority.Banned"/>。
    /// </summary>
    /// <param name="turnId">转向 id。</param>
    public TurnPriority PriorityOf(int turnId)
    {
        PhaseSlice slice = Locate(_cyclePosition);
        return slice.Phase is SignalPhaseState.Green or SignalPhaseState.GreenFlash
            ? _plans[_planIndex].Stages[slice.StageIndex].PriorityOf(turnId)
            : TurnPriority.Banned;
    }

    /// <inheritdoc />
    public override string ToString()
        => $"SignalStateMachine(node={NodeId}, plan={PlanIndex}, stage={StageIndex}, phase={Phase}, " +
           $"tIn={TimeInPhase:0.###}, remaining={PhaseRemaining:0.###}, cycle={CycleLength:0.###})";

    // ------------------------------------------------------------------
    // 内部
    // ------------------------------------------------------------------

    /// <summary>周期边界：优先安装待生效方案（下一周期生效），否则按时段重选方案。</summary>
    private void OnCycleBoundary()
    {
        if (_pendingPlan is { } pending)
        {
            _plans[_planIndex] = pending;
            _pendingPlan = null;
            return;
        }

        _planIndex = SelectPlanIndex(_simTime);
    }

    /// <summary>按 <see cref="SignalPlan.StartTime"/> 选当前时段方案；均未生效时取最早者。</summary>
    private int SelectPlanIndex(double simTime)
    {
        int active = -1;
        double activeStart = double.NegativeInfinity;

        int earliest = 0;
        double earliestStart = double.PositiveInfinity;

        for (int i = 0; i < _plans.Count; i++)
        {
            double start = _plans[i].StartTime;

            if (start < earliestStart)
            {
                earliestStart = start;
                earliest = i;
            }

            if (start <= simTime && start >= activeStart)
            {
                activeStart = start;
                active = i;
            }
        }

        return active >= 0 ? active : earliest;
    }

    /// <summary>把弧度意义上的周期内位置映射到 <c>[0, cycleLength)</c>。</summary>
    private static double Normalize(double time, double cycleLength)
    {
        double position = time % cycleLength;
        if (position < 0d)
        {
            position += cycleLength;
        }

        return position;
    }

    /// <summary>定位 <paramref name="position"/> 落在哪个相位 / 子相位；跳过零时长子相位。</summary>
    private PhaseSlice Locate(double position)
    {
        IReadOnlyList<Stage> stages = _plans[_planIndex].Stages;
        double acc = 0d;
        PhaseSlice fallback = default;

        for (int i = 0; i < stages.Count; i++)
        {
            Stage stage = stages[i];

            if (TrySlice(ref acc, stage.Duration, position, i, SignalPhaseState.Green, ref fallback, out PhaseSlice green))
            {
                return green;
            }

            if (TrySlice(ref acc, _timing.GreenFlashTime, position, i, SignalPhaseState.GreenFlash, ref fallback, out PhaseSlice flash))
            {
                return flash;
            }

            if (TrySlice(ref acc, _timing.YellowTime, position, i, SignalPhaseState.Yellow, ref fallback, out PhaseSlice yellow))
            {
                return yellow;
            }

            if (TrySlice(ref acc, _timing.AllRedTime, position, i, SignalPhaseState.AllRed, ref fallback, out PhaseSlice allRed))
            {
                return allRed;
            }
        }

        // 仅当浮点把 position 顶到周期末尾时才会走到这里；返回最后一段非空子相位。
        return fallback;
    }

    private static bool TrySlice(
        ref double acc,
        double length,
        double position,
        int stageIndex,
        SignalPhaseState phase,
        ref PhaseSlice fallback,
        out PhaseSlice hit)
    {
        hit = default;
        if (length <= 0d)
        {
            return false;   // 零时长子相位：不占用周期，不产生相位
        }

        var slice = new PhaseSlice(stageIndex, phase, acc, acc + length);
        acc = slice.End;

        if (position < slice.End)
        {
            hit = slice;
            return true;
        }

        fallback = slice;
        return false;
    }

    /// <summary>一个子相位在周期内的区间 <c>[Start, End)</c>。</summary>
    private readonly record struct PhaseSlice(int StageIndex, SignalPhaseState Phase, double Start, double End);
}
