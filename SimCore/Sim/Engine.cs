using SimCore.Geometry;
using SimCore.Io;
using SimCore.Model;

namespace SimCore.Sim;

// ===========================================================================
// T1.6 · 确定性步进 + 加速一致性（技术方案03 §5 · T1.6；核心卡）
//
// 本文件落地 docs/SimCore-API.md 冻结的四个面：
//   * §4.2 SimConfig                     —— 仿真配置
//   * §4.3 SimEngine                     —— 仿真主循环（Step / Step(dt) / Advance / Reset /
//                                            Snapshot / StateHash / DrainEvents / ApplyPlan）
//   * §4.4 SimState + SignalSnapshot     —— 只读快照（Vehicle/Pedestrian/Bicycle/Lane 快照已在
//                                            T1.1/T1.4/T1.5 各自文件中冻结落地，本卡不复定义）
//   * §4.9 SimEvent + SimEventKind       —— 跨 Sim / Metrics 的事件契约
//
// 组装与复用（不重复造轮子、不改任何已冻结接口）：
//   * T1.1 Queue                —— 每车道一个队列，消费一个布尔 open 完成跟驰 + 饱和流率放行；
//   * T1.2 SignalStateMachine    —— 每个信号节点一个状态机，逐步 Tick；
//   * T1.3 ConflictMatrix / IntersectionGate —— 同源门控，提供相位放行掩码（IsTurnOpen）；
//   * T1.4/T1.5                 —— 行人 / 非机动车为独立单元，本卡不占用其逻辑通道（见报告"未解决项"）。
//
// 本任务追加约束的落地：
//   ① 不得依赖字典遍历顺序——引擎内部一切"逐项扫描"都按**车道 id / 节点 id 升序**进行，
//      未使用任何 Dictionary / HashSet 的遍历；Movement 分组在 CompiledNetwork 编译期已用
//      SortedDictionary 定序。
//   ② 步长用固定累加器避免浮点漂移——仿真时刻由**整数步数**派生（SimTime = 步数 · Dt），
//      绝不做 SimTime += Dt 的累加；Advance 用"步数余量累加器"（_pendingSteps）把不足一步的
//      实数时间留到下次，杜绝逐帧 round 造成的累计快进 / 漂移。
//
// 铁律：无 Godot 命名空间、无第三方依赖、无系统随机数（全部状态是"步数 + 输入"的纯函数）。
// ===========================================================================

/// <summary>
/// 仿真配置（<c>docs/SimCore-API.md</c> §4.2 冻结）。纯数据载体，属性为 <c>init</c>。
/// </summary>
/// <remarks>
/// <para>成员名 / 默认值与 §4.2 逐字一致，属冻结接口，只能构造、不得改名。</para>
/// <para>合法性校验（<see cref="Dt"/> 为正、<see cref="Duration"/> 非负等）在
/// <see cref="SimEngine"/> 构造时执行，本类型保持为纯载体（与 Model / Io 层口径一致）。</para>
/// </remarks>
public sealed class SimConfig
{
    /// <summary>固定仿真步长（秒），须 &gt; 0；默认 0.1（对应设计 §9 的 60Hz 逻辑帧率量级）。</summary>
    public double Dt { get; init; } = 0.1d;

    /// <summary>初始加速档，∈ {1,2,4,8,16,32}；默认 1。运行期可改（换挡 / 暂停）。</summary>
    public int SpeedMultiplier { get; init; } = 1;

    /// <summary>确定性模式（默认开）。SimCore 禁止系统随机数，故两种取值下步进结果一致（保留位）。</summary>
    public bool Deterministic { get; init; } = true;

    /// <summary>需求注入种子（禁止系统随机数；供 T1.7 需求生成器使用）。</summary>
    public long Seed { get; init; }

    /// <summary>单局仿真时长（秒），默认 600。</summary>
    public double Duration { get; init; } = 600d;

    /// <summary>黄 / 全红 / 绿闪时长（默认 <see cref="SignalTiming.Default"/>）。</summary>
    public SignalTiming Timing { get; init; } = SignalTiming.Default;

    /// <summary>每车道排队容量（辆），溢出判据；默认 20。</summary>
    public int QueueCapacityPerLane { get; init; } = 20;
}

/// <summary>
/// 仿真事件种类（<c>docs/SimCore-API.md</c> §4.9 冻结）。
/// </summary>
/// <remarks>成员名称与顺序为冻结契约（附 A），不得改名 / 增删 / 调序；顺序即数值。</remarks>
public enum SimEventKind
{
    /// <summary>需求到达（一辆车 / 一名行人 / 一辆非机动车进入网络）。</summary>
    DemandArrived,

    /// <summary>车辆通过（驶离一条车道，或驶出网络）。</summary>
    VehiclePassed,

    /// <summary>车辆停车（新发生"运动 → 停"跃迁）。</summary>
    VehicleStopped,

    /// <summary>排队采样（某车道的排队长度样本）。</summary>
    QueueSample,

    /// <summary>碰撞（由路口门控在检出碰撞时发出）。</summary>
    Collision,

    /// <summary>排队溢出（首车道占有率超阈值）。</summary>
    Spillback,
}

/// <summary>
/// 仿真事件（<c>docs/SimCore-API.md</c> §4.9 冻结；跨 <c>Sim</c> / <c>Metrics</c> 契约）。
/// </summary>
/// <param name="Kind">事件种类。</param>
/// <param name="Time">仿真时刻（秒）。</param>
/// <param name="LaneId">相关车道 id（无车道时为 -1）。</param>
/// <param name="VehicleId">相关车辆 id（无车辆时为 -1）。</param>
/// <param name="Value">事件的量（如排队长度 / 占有率）。</param>
public readonly record struct SimEvent(
    SimEventKind Kind,
    double Time,
    int LaneId,
    int VehicleId,
    double Value);

/// <summary>
/// 信号快照（<c>docs/SimCore-API.md</c> §4.4 冻结；表现层只读）。
/// </summary>
/// <param name="NodeId">路口节点 id。</param>
/// <param name="PlanIndex">当前生效方案下标。</param>
/// <param name="StageIndex">当前相位下标。</param>
/// <param name="Phase">当前相位状态（停止线颜色来源）。</param>
/// <param name="TimeInPhase">当前相位已持续时长（秒）。</param>
/// <param name="PhaseRemaining">当前相位剩余时长（秒）。</param>
public readonly record struct SignalSnapshot(
    int NodeId,
    int PlanIndex,
    int StageIndex,
    SignalPhaseState Phase,
    double TimeInPhase,
    double PhaseRemaining);

/// <summary>
/// 仿真状态只读快照（<c>docs/SimCore-API.md</c> §4.4 冻结；表现层只读）。
/// </summary>
/// <remarks>
/// <para><b>刻意不含指标（R/D/P/Q）</b>：指标由 <c>Metrics</c> 层从事件流累计（契约 §1.2 依赖方向），
/// 表现层 HUD 取指标的路径见 §5.4（<c>DrainEvents → MetricsCollector → Snapshot</c>）。</para>
/// <para>不可变：每次 <see cref="SimEngine.Snapshot"/> 返回一个全新对象。</para>
/// </remarks>
public sealed class SimState
{
    internal SimState(
        double simTime,
        IReadOnlyList<VehicleSnapshot> vehicles,
        IReadOnlyList<PedestrianSnapshot> pedestrians,
        IReadOnlyList<BicycleSnapshot> bicycles,
        IReadOnlyList<SignalSnapshot> signals,
        IReadOnlyList<LaneSnapshot> lanes,
        bool collision,
        ulong hash)
    {
        SimTime = simTime;
        Vehicles = vehicles;
        Pedestrians = pedestrians;
        Bicycles = bicycles;
        Signals = signals;
        Lanes = lanes;
        Collision = collision;
        Hash = hash;
    }

    /// <summary>已仿真时长（秒）。</summary>
    public double SimTime { get; }

    /// <summary>全部在场机动车快照（按车道 id 升序、车道内按队首 → 队尾）。</summary>
    public IReadOnlyList<VehicleSnapshot> Vehicles { get; }

    /// <summary>全部未通过行人快照（本卡为空，见报告"未解决项"）。</summary>
    public IReadOnlyList<PedestrianSnapshot> Pedestrians { get; }

    /// <summary>全部未通过非机动车快照（本卡为空，见报告"未解决项"）。</summary>
    public IReadOnlyList<BicycleSnapshot> Bicycles { get; }

    /// <summary>信号快照（按节点 id 升序）。</summary>
    public IReadOnlyList<SignalSnapshot> Signals { get; }

    /// <summary>车道快照（按车道 id 升序）。</summary>
    public IReadOnlyList<LaneSnapshot> Lanes { get; }

    /// <summary>本局是否已发生碰撞。</summary>
    public bool Collision { get; }

    /// <summary>状态哈希（== <see cref="SimEngine.StateHash"/>）。</summary>
    public ulong Hash { get; }
}

/// <summary>
/// 仿真主循环（<c>docs/SimCore-API.md</c> §4.3 冻结；T1.6 核心）。
/// </summary>
/// <remarks>
/// <para><b>职责</b>：按固定步长 <see cref="SimConfig.Dt"/> 推进仿真——每步依序
/// （1）注入需求到达、（2）推进各信号状态机、（3）逐车道按相位放行掩码推进队列、
/// （4）把驶离车道的车辆路由到下游车道、（5）产生事件。全过程顺序固定、无字典遍历依赖、无系统随机。</para>
///
/// <para><b>确定性保证（T1.6）</b>：
/// <list type="bullet">
///   <item>仿真时刻由整数步数派生（<c>SimTime = 步数 · Dt</c>），不做浮点累加——无漂移；</item>
///   <item>加速档 <see cref="SpeedMultiplier"/> <b>只改变每帧推进的步数</b>，不改变任何一步的逻辑；
///   因此 1×/2×/…/32× 在同一仿真时刻的状态逐位相同；</item>
///   <item>一切逐项扫描按车道 / 节点 id 升序，不依赖任何集合的遍历顺序；</item>
///   <item><see cref="StateHash"/> 覆盖位置 / 速度 / 状态 / 排队 / 相位等全部可影响结果的状态。</item>
/// </list>
/// </para>
///
/// <para><b>步长累加器（本任务追加约束）</b>：<see cref="Advance"/> 把 <c>realSeconds·SpeedMultiplier</c>
/// 换算成"步数余量"累加到 <c>_pendingSteps</c>，取整数步推进、余量留待下次调用。这样逐帧调用
/// （如每帧 1/60 秒）不会因单帧不足一步而被丢弃，也不会因四舍五入而累计快进或漂移。</para>
/// </remarks>
public sealed class SimEngine
{
    /// <summary>步数整除判定容差（把 6000.0000000001 之类的浮点尾差归入整步）。</summary>
    private const double StepEpsilon = 1e-9d;

    /// <summary>到达额度判定容差（避免浮点抖动导致"差一点不放行"）。</summary>
    private const double ArrivalEpsilon = 1e-9d;

    // 人行横道过街距离的"两点弦"几何常量（与 ConflictMatrix 的中心线弦模型逐字一致）：
    // 车道横向宽度、弦在节点两侧的伸出深度。见 CrosswalkDistanceOf。
    private const double CrosswalkLaneWidth = 3.5d;
    private const double CrosswalkChordDepth = 6.0d;

    /// <summary>退化几何（节点重合 / 零长车道）时的兜底过街距离（米）。</summary>
    private const double FallbackCrosswalkDistance = 12.0d;

    /// <summary>把过街距离下限钳到正数，满足 <c>PedestrianCrossing</c> 的前置（&gt; 0）。</summary>
    private const double MinCrosswalkDistance = 1e-6d;

    private readonly SimConfig _config;
    private readonly CompiledNetwork _network;
    private readonly IntersectionGate _gate;
    private readonly Queue[] _queues;                 // 下标 = 车道 id
    private readonly SignalStateMachine?[] _signals;  // 下标 = 节点 id（非信号节点为 null）
    private readonly int[] _signalNodeIds;            // 信号节点 id，升序
    private readonly double[] _arrivalRate;           // 下标 = 车道 id（辆/秒，0 = 无注入）
    private readonly VehicleKind[] _arrivalKind;      // 下标 = 车道 id
    private readonly int[] _arrivalSeq;               // 下标 = 车道 id（该车道已入场车辆数，供车型轮转）
    private readonly VehicleMix? _vehicleMix;         // 车型构成（Level 形才有值；网络形为 null = 纯小汽车）
    private readonly double[] _arrivalCredit;         // 下标 = 车道 id（到达额度余量）
    private readonly List<SimEvent> _events = new();
    private readonly long _finishedSteps;             // 达到该步数即结束

    // --- T1.8 需求接入（仅 Level 直通形非空；网络形保持默认空值，行为与 T1.6 逐位一致）---
    private readonly DemandGenerator? _demand = null;                 // 需求时刻表（§4.8）
    private readonly int[] _drivingTargets = Array.Empty<int>();      // 机动车需求目标车道（升序）
    private readonly PedestrianCrossing[] _crosswalks = Array.Empty<PedestrianCrossing>(); // 行人（按人行横道转向 id 升序）
    private readonly BicycleFlow[] _bicycles = Array.Empty<BicycleFlow>();                  // 非机动车（按车道 id 升序）

    private long _seed;
    private long _steps;                    // 已完成固定步数（唯一时间累加器）
    private int _nextVehicleId;
    private int _nextPedestrianId;          // 行人 id 分配器（独立于机动车 id 空间）
    private int _nextBicycleId;             // 非机动车 id 分配器（独立于机动车 id 空间）
    private long _exitedVehicles;
    private bool _collision;
    private double _pendingSteps;           // Advance 的步数余量累加器
    private int _speedMultiplier;

    /// <summary>
    /// 以编译路网建引擎（§4.3 字面形）；<b>无需求注入</b>——供 T1.1~T1.6 单测直接放车。
    /// </summary>
    /// <param name="network">编译路网（只读）。</param>
    /// <param name="config">仿真配置。</param>
    /// <exception cref="ArgumentNullException"><paramref name="network"/> 或 <paramref name="config"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="config"/> 中 <c>Dt</c> / <c>Duration</c> / <c>SpeedMultiplier</c> / <c>QueueCapacityPerLane</c> 非法。</exception>
    /// <exception cref="ArgumentException">某信号节点未配置相位（无法运行信号状态机）。</exception>
    public SimEngine(CompiledNetwork network, SimConfig config)
        : this(network, config, null)
    {
    }

    /// <summary>
    /// 网络形 + 车型构成（内部装配形）：<paramref name="vehicleMix"/> 只影响队列容量口径与到达车型，
    /// <c>null</c> = 纯小汽车（与 T4.3 之前逐位一致）。
    /// </summary>
    private SimEngine(CompiledNetwork network, SimConfig config, VehicleMix? vehicleMix)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(config);

        if (double.IsNaN(config.Dt) || double.IsInfinity(config.Dt) || config.Dt <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(config), config.Dt, "SimConfig.Dt 必须为有限正数。");
        }

        if (double.IsNaN(config.Duration) || double.IsInfinity(config.Duration) || config.Duration < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(config), config.Duration, "SimConfig.Duration 必须为有限非负数。");
        }

        if (config.SpeedMultiplier < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(config), config.SpeedMultiplier, "SimConfig.SpeedMultiplier 不得为负（0 = 暂停）。");
        }

        if (config.QueueCapacityPerLane <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(config), config.QueueCapacityPerLane, "SimConfig.QueueCapacityPerLane 必须为正整数。");
        }

        if (config.Timing is null)
        {
            throw new ArgumentNullException(nameof(config), "SimConfig.Timing 不得为 null。");
        }

        _config = config;
        _network = network;
        _speedMultiplier = config.SpeedMultiplier;
        _seed = config.Seed;
        _vehicleMix = vehicleMix;

        // 结束步数：以整数步数判定是否结束，避免 SimTime 的浮点尾差导致"永远差一步"。
        _finishedSteps = (long)Math.Ceiling((config.Duration / config.Dt) - StepEpsilon);

        // 每车道一个队列（下标 = 车道 id）。
        int laneCount = network.LaneCount;
        _queues = new Queue[laneCount];
        _arrivalRate = new double[laneCount];
        _arrivalKind = new VehicleKind[laneCount];
        _arrivalSeq = new int[laneCount];
        _arrivalCredit = new double[laneCount];
        double expectedSpacing = VehicleMixRules.ExpectedJamSpacing(vehicleMix);
        for (int laneId = 0; laneId < laneCount; laneId++)
        {
            _queues[laneId] = new Queue(network.GetLane(laneId), expectedSpacing);
        }

        // 每个信号节点一个状态机（仅 IsSignalized；其余节点放行恒为自由流）。
        int nodeCount = network.NodeCount;
        _signals = new SignalStateMachine?[nodeCount];
        var signalNodeIds = new List<int>();
        for (int nodeId = 0; nodeId < nodeCount; nodeId++)
        {
            Node node = network.GetNode(nodeId);
            if (!node.IsSignalized)
            {
                continue;
            }

            var machine = new SignalStateMachine(node, config.Timing);
            machine.SyncToTime(0d);   // 按 node.Offset 对齐到 t=0
            _signals[nodeId] = machine;
            signalNodeIds.Add(nodeId);   // 按 nodeId 递增遍历 → 天然升序
        }

        _signalNodeIds = signalNodeIds.ToArray();

        // 冲突矩阵 + 门控（同源，供相位放行掩码使用）。
        ConflictMatrix conflicts = ConflictMatrix.From(network);
        _gate = IntersectionGate.Build(network, conflicts);
    }

    /// <summary>
    /// 关卡直通形（§4.3；装配层，契约 §10 D9）：内部执行 <c>level.Network.Compile()</c>，
    /// 并按 <c>Level.Demand / Pedestrians / Bicycles</c> <b>建立需求时刻表</b>（§4.3 语义，T1.8）。
    /// </summary>
    /// <param name="level">关卡（提供路网与三类需求曲线）。</param>
    /// <param name="config">仿真配置。</param>
    /// <exception cref="ArgumentNullException"><paramref name="level"/> 或 <paramref name="config"/> 为 null。</exception>
    /// <remarks>
    /// <para>本构造在完成路网编译后，进一步消费 T1.7 的 <see cref="DemandGenerator"/>：
    /// 每步按 <see cref="DemandGenerator.DrawArrivals"/> 注入三类到达
    /// （机动车 → 车道队列；行人 → <see cref="PedestrianCrossing"/>；非机动车 → <see cref="BicycleFlow"/>），
    /// 并驱动行人与非机动车推进，使 <see cref="SimState.Pedestrians"/> / <see cref="SimState.Bicycles"/> 非空。</para>
    /// <para>行人与非机动车仍为<b>独立单元</b>：不并入机动车 <c>Queue</c> / 跟驰通道（T1.4 / T1.5 口径）。</para>
    /// </remarks>
    public SimEngine(Level level, SimConfig config)
        : this(CompileLevel(level), config, level.GameRules.VehicleMix)
    {
        _demand = new DemandGenerator(level, config.Seed);
        _drivingTargets = CollectDrivingTargets(level);
        _crosswalks = BuildCrosswalks(level);
        _bicycles = BuildBicycleFlows(level);
    }

    /// <summary>仿真配置（只读，§4.3）。</summary>
    public SimConfig Config => _config;

    /// <summary>编译路网（只读，§4.3）。</summary>
    public CompiledNetwork Network => _network;

    /// <summary>
    /// 加速档（§4.3），运行时可改（换挡 / 暂停）；<b>只改步数不改逻辑</b>。0 = 暂停（不推进）。
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">赋值为负。</exception>
    public int SpeedMultiplier
    {
        get => _speedMultiplier;
        set
        {
            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "加速档不得为负（0 = 暂停）。");
            }

            _speedMultiplier = value;
        }
    }

    /// <summary>已仿真时长（秒）：由整数步数派生，无浮点累加漂移（§4.3）。</summary>
    public double SimTime => _steps * _config.Dt;

    /// <summary>是否已到达单局时长（§4.3）。</summary>
    public bool IsFinished => _steps >= _finishedSteps;

    /// <summary>
    /// 供单测核对 <c>Reset(seed)</c> 的"换种子"是否生效（<b>非冻结 public 面</b>）。
    /// </summary>
    internal long Seed => _seed;

    /// <summary>
    /// 车道队列（下标 = 车道 id；<b>非冻结 public 面</b>）——供单测直接检查跟驰 / 排队状态。
    /// </summary>
    internal IReadOnlyList<Queue> Queues => _queues;

    /// <summary>推进<b>恰好</b>一个固定步长 <see cref="SimConfig.Dt"/>（§4.3）；已结束时空操作。</summary>
    public void Step()
    {
        if (IsFinished)
        {
            return;
        }

        StepOnce();
    }

    /// <summary>§4.3 兼容形：<c>Step(dt)</c>，前置 <c>dt == Config.Dt</c>。</summary>
    /// <param name="dt">步长（秒），必须等于 <see cref="SimConfig.Dt"/>。</param>
    /// <exception cref="ArgumentException"><paramref name="dt"/> ≠ <see cref="SimConfig.Dt"/>（固定步长不变式）。</exception>
    public void Step(double dt)
    {
        if (dt != _config.Dt)
        {
            throw new ArgumentException($"Step(dt) 的前置为 dt == Config.Dt（{_config.Dt}），实际为 {dt}。", nameof(dt));
        }

        Step();
    }

    /// <summary>
    /// 按加速档推进整数步（§4.3，<c>[表现层]</c> 每帧调用）。
    /// </summary>
    /// <param name="realSeconds">真实流逝时间（秒，≥ 0）。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="realSeconds"/> 为负 / 非有限。</exception>
    /// <remarks>
    /// 采用<b>步数余量累加器</b>：<c>_pendingSteps += realSeconds·SpeedMultiplier / Dt</c>，
    /// 取整数步推进、余量留待下次。因此逐帧（如 1/60s）调用不会丢步，也不会累计快进 / 漂移。
    /// 加速档 0（暂停）时推进 0 步。
    /// </remarks>
    public void Advance(double realSeconds)
    {
        if (double.IsNaN(realSeconds) || double.IsInfinity(realSeconds) || realSeconds < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(realSeconds), realSeconds, "realSeconds 必须为有限非负数。");
        }

        if (realSeconds == 0d || IsFinished)
        {
            return;
        }

        _pendingSteps += (realSeconds * _speedMultiplier) / _config.Dt;

        long whole = (long)Math.Floor(_pendingSteps + StepEpsilon);
        if (whole <= 0)
        {
            return;
        }

        _pendingSteps -= whole;

        for (long i = 0; i < whole && !IsFinished; i++)
        {
            StepOnce();
        }
    }

    /// <summary>复位到 <c>t=0</c> 并换种子（§4.3）：清空状态与事件、相位回到初始（未结束、未清空）。</summary>
    /// <param name="seed">新种子（同时换给需求时刻表 <see cref="DemandGenerator"/>）。</param>
    public void Reset(long seed)
    {
        _seed = seed;
        _steps = 0L;
        _nextVehicleId = 0;
        _nextPedestrianId = 0;
        _nextBicycleId = 0;
        _exitedVehicles = 0L;
        _collision = false;
        _pendingSteps = 0d;
        _events.Clear();

        for (int laneId = 0; laneId < _queues.Length; laneId++)
        {
            _queues[laneId].Reset();
            _arrivalCredit[laneId] = 0d;
            _arrivalSeq[laneId] = 0;      // ★ 车型轮转也归零，保证 Reset 后可复现（铁律 2）
        }

        for (int i = 0; i < _signalNodeIds.Length; i++)
        {
            _signals[_signalNodeIds[i]]!.SyncToTime(0d);
        }

        // 需求时刻表换种子（§4.8 Reset）；行人 / 非机动车协调器清空到 t=0（确定性复位）。
        _demand?.Reset(seed);
        for (int i = 0; i < _crosswalks.Length; i++)
        {
            _crosswalks[i].Reset();
        }

        for (int i = 0; i < _bicycles.Length; i++)
        {
            _bicycles[i].Reset();
        }
    }

    /// <summary>返回当前只读快照（§4.3，<c>[表现层]</c>）；每次调用返回<b>新的不可变</b>对象。</summary>
    public SimState Snapshot()
    {
        var vehicles = new List<VehicleSnapshot>();
        var lanes = new List<LaneSnapshot>(_queues.Length);
        for (int laneId = 0; laneId < _queues.Length; laneId++)
        {
            Queue queue = _queues[laneId];
            IReadOnlyList<Vehicle> queued = queue.Vehicles;
            for (int i = 0; i < queued.Count; i++)
            {
                vehicles.Add(queued[i].ToSnapshot());
            }

            lanes.Add(queue.Snapshot());
        }

        var signals = new List<SignalSnapshot>(_signalNodeIds.Length);
        for (int i = 0; i < _signalNodeIds.Length; i++)
        {
            int nodeId = _signalNodeIds[i];
            SignalStateMachine machine = _signals[nodeId]!;
            signals.Add(new SignalSnapshot(
                nodeId,
                machine.PlanIndex,
                machine.StageIndex,
                machine.Phase,
                machine.TimeInPhase,
                machine.PhaseRemaining));
        }

        // 行人与非机动车（独立单元）：按人行横道转向 / 非机动车道 id 升序收集"未通过者"快照。
        var pedestrians = new List<PedestrianSnapshot>();
        for (int i = 0; i < _crosswalks.Length; i++)
        {
            IReadOnlyList<PedestrianSnapshot> snaps = _crosswalks[i].Snapshots();
            for (int j = 0; j < snaps.Count; j++)
            {
                pedestrians.Add(snaps[j]);
            }
        }

        var bicycles = new List<BicycleSnapshot>();
        for (int i = 0; i < _bicycles.Length; i++)
        {
            IReadOnlyList<BicycleSnapshot> snaps = _bicycles[i].Snapshots();
            for (int j = 0; j < snaps.Count; j++)
            {
                bicycles.Add(snaps[j]);
            }
        }

        return new SimState(
            SimTime,
            vehicles,
            pedestrians,
            bicycles,
            signals,
            lanes,
            _collision,
            StateHash());
    }

    /// <summary>
    /// 当前状态哈希（§4.3，T1.6 d）——覆盖位置 / 速度 / 状态 / 排队 / 相位等全部可影响结果的状态。
    /// </summary>
    /// <remarks>
    /// <para>哈希只覆盖<b>物理仿真状态</b>，不含 <c>Seed</c> 与 <c>_pendingSteps</c>（调度余量）：
    /// 因此"同 seed 两次运行"与"1× / 32× 到同一仿真时刻"都得到相同哈希。</para>
    /// <para>实现为 FNV-1a 64 位，逐项按车道 / 节点 id 升序混入，不依赖任何集合的遍历顺序。</para>
    /// </remarks>
    public ulong StateHash()
    {
        var hasher = new StateHasher();

        hasher.Add(_steps);
        hasher.Add(_exitedVehicles);
        hasher.Add(_collision ? 1L : 0L);

        for (int laneId = 0; laneId < _queues.Length; laneId++)
        {
            Queue queue = _queues[laneId];
            hasher.Add(laneId);
            hasher.Add(queue.Count);

            IReadOnlyList<Vehicle> queued = queue.Vehicles;
            for (int i = 0; i < queued.Count; i++)
            {
                Vehicle v = queued[i];
                hasher.Add(v.Id);
                hasher.Add(v.LaneId);
                hasher.Add((int)v.Kind);
                hasher.Add((int)v.State);
                hasher.Add(v.StopCount);
                hasher.Add(v.Position);
                hasher.Add(v.Speed);
                hasher.Add(v.BlockedTime);
                hasher.Add(v.EnterTime);
                hasher.Add(v.ExitTime);
            }

            hasher.Add(queue.QueueLength);
            hasher.Add(queue.Occupancy);
            hasher.Add(queue.IsOpen ? 1L : 0L);
            hasher.Add(queue.OpenElapsed);
            hasher.Add(queue.DischargeCredit);
        }

        // 行人与非机动车（独立单元）：按人行横道转向 / 非机动车道 id 升序、到达顺序混入。
        for (int i = 0; i < _crosswalks.Length; i++)
        {
            PedestrianCrossing crossing = _crosswalks[i];
            hasher.Add(crossing.CrosswalkTurnId);

            IReadOnlyList<Pedestrian> people = crossing.Pedestrians;
            hasher.Add(people.Count);
            for (int j = 0; j < people.Count; j++)
            {
                Pedestrian p = people[j];
                hasher.Add(p.Id);
                hasher.Add(p.CrosswalkTurnId);
                hasher.Add((int)p.State);
                hasher.Add(p.Position);
                hasher.Add(p.ArriveTime);
                hasher.Add(p.StartTime);
                hasher.Add(p.ExitTime);
                hasher.Add(p.WaitTime);
            }
        }

        for (int i = 0; i < _bicycles.Length; i++)
        {
            BicycleFlow flow = _bicycles[i];
            hasher.Add(flow.LaneId);

            IReadOnlyList<Bicycle> bikes = flow.Bicycles;
            hasher.Add(bikes.Count);
            for (int j = 0; j < bikes.Count; j++)
            {
                Bicycle b = bikes[j];
                hasher.Add(b.Id);
                hasher.Add(b.LaneId);
                hasher.Add((int)b.Mode);
                hasher.Add((int)b.State);
                hasher.Add(b.Position);
                hasher.Add(b.Speed);
                hasher.Add(b.StopCount);
                hasher.Add(b.ArriveTime);
                hasher.Add(b.ExitTime);
                hasher.Add(b.WaitTime);
            }
        }

        for (int i = 0; i < _signalNodeIds.Length; i++)
        {
            int nodeId = _signalNodeIds[i];
            SignalStateMachine machine = _signals[nodeId]!;
            hasher.Add(nodeId);
            hasher.Add(machine.PlanIndex);
            hasher.Add(machine.StageIndex);
            hasher.Add((int)machine.Phase);
            hasher.Add(machine.TimeInPhase);
            hasher.Add(machine.PhaseRemaining);
            hasher.Add(machine.CycleLength);
        }

        return hasher.Value;
    }

    /// <summary>取出并清空自上次调用以来的事件（§4.3）；顺序确定、可复现。</summary>
    public IReadOnlyList<SimEvent> DrainEvents()
    {
        if (_events.Count == 0)
        {
            return Array.Empty<SimEvent>();
        }

        SimEvent[] drained = _events.ToArray();
        _events.Clear();
        return drained;
    }

    /// <summary>提交新配时方案，<b>下一周期生效</b>（§4.3 / T1.2 c）。</summary>
    /// <param name="nodeId">信号路口节点 id。</param>
    /// <param name="plan">新配时方案（至少含一个相位）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="plan"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="nodeId"/> 越界或非信号路口。</exception>
    public void ApplyPlan(int nodeId, SignalPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        if ((uint)nodeId >= (uint)_network.NodeCount)
        {
            throw new ArgumentException($"节点 id {nodeId} 越界（节点数 {_network.NodeCount}）。", nameof(nodeId));
        }

        SignalStateMachine? machine = _signals[nodeId];
        if (machine is null)
        {
            throw new ArgumentException($"节点 {nodeId} 不是信号路口，无法应用配时方案。", nameof(nodeId));
        }

        machine.ApplyPlan(plan);
    }

    // ------------------------------------------------------------------
    // 单测注入钩子（internal，非冻结 public 面；经 InternalsVisibleTo 授权测试程序集）
    // ------------------------------------------------------------------

    /// <summary>
    /// 在车道<b>停止线一侧</b>铺一条静止的满队列（车头依次落在 车道长, 车道长−车头间距, …）。
    /// </summary>
    /// <param name="laneId">目标车道 id。</param>
    /// <param name="count">期望铺设的车辆数（超过车道容量时铺到放不下为止）。</param>
    /// <param name="kind">车型。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="laneId"/> 越界或 <paramref name="count"/> 为负。</exception>
    internal void SeedQueue(int laneId, int count, VehicleKind kind = VehicleKind.Car)
    {
        if ((uint)laneId >= (uint)_queues.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(laneId), laneId, "车道 id 越界。");
        }

        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "车辆数不得为负。");
        }

        Queue queue = _queues[laneId];
        double spacing = CarFollowing.JamSpacingOf(kind);
        for (int i = 0; i < count; i++)
        {
            double position = queue.Lane.Length - (i * spacing);
            if (position < 0d)
            {
                break;   // 车道放不下更多
            }

            var vehicle = new Vehicle(_nextVehicleId, kind);
            if (!queue.TryPlace(vehicle, position))
            {
                break;
            }

            _nextVehicleId++;
        }
    }

    /// <summary>
    /// 设置某车道的<b>恒定到达流率</b>（辆/秒）——确定性的步进驱动源，供 T1.6 单测制造持续演化的场景。
    /// </summary>
    /// <param name="laneId">目标车道 id。</param>
    /// <param name="vehiclesPerSecond">流率（辆/秒，≥ 0；0 关闭）。</param>
    /// <param name="kind">到达车型。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="laneId"/> 越界或 <paramref name="vehiclesPerSecond"/> 非法。</exception>
    /// <remarks>
    /// 本钩子只提供"恒定流率"这一最小驱动面，<b>不是</b>契约 §4.8 的 <c>DemandGenerator</c>（T1.7）——
    /// 时变 / 脉冲曲线与种子抽样由 T1.7 实现。
    /// </remarks>
    internal void SetArrivalRate(int laneId, double vehiclesPerSecond, VehicleKind kind = VehicleKind.Car)
    {
        if ((uint)laneId >= (uint)_arrivalRate.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(laneId), laneId, "车道 id 越界。");
        }

        if (double.IsNaN(vehiclesPerSecond) || double.IsInfinity(vehiclesPerSecond) || vehiclesPerSecond < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(vehiclesPerSecond), vehiclesPerSecond, "流率必须为有限非负数。");
        }

        _arrivalRate[laneId] = vehiclesPerSecond;
        _arrivalKind[laneId] = kind;
    }

    // ------------------------------------------------------------------
    // 内部：单步推进 / 放行掩码 / 路由 / 哈希
    // ------------------------------------------------------------------

    /// <summary>推进一个固定步长：注入 → 信号 → 队列 → 路由 → 独立单元（行人 / 非机动车），顺序固定（确定性）。</summary>
    private void StepOnce()
    {
        double t0 = _steps * _config.Dt;              // 本步区间起点 [t0, t0+Dt)
        double now = (_steps + 1) * _config.Dt;       // 本步区间终点 / 事件时刻

        // 1) 需求到达注入（机动车 / 行人 / 非机动车各按目标 id 升序；确定性）。
        InjectArrivals(t0, now);

        // 2) 信号状态机推进（按节点 id 升序）。
        for (int i = 0; i < _signalNodeIds.Length; i++)
        {
            _signals[_signalNodeIds[i]]!.Tick(_config.Dt);
        }

        // 3) 逐车道推进队列（按车道 id 升序；open = 该车道当前是否被放行）。
        for (int laneId = 0; laneId < _queues.Length; laneId++)
        {
            Queue queue = _queues[laneId];
            queue.Step(_config.Dt, LaneIsOpen(laneId));

            IReadOnlyList<Vehicle> stopped = queue.DrainStopped();
            for (int s = 0; s < stopped.Count; s++)
            {
                _events.Add(new SimEvent(SimEventKind.VehicleStopped, now, laneId, stopped[s].Id, stopped[s].StopCount));
            }

            _events.Add(new SimEvent(SimEventKind.QueueSample, now, laneId, -1, queue.QueueLength));
        }

        // 4) 路由驶离车道的车辆（按车道 id 升序）。
        for (int laneId = 0; laneId < _queues.Length; laneId++)
        {
            IReadOnlyList<Vehicle> departed = _queues[laneId].DrainDeparted();
            for (int d = 0; d < departed.Count; d++)
            {
                Route(departed[d], laneId, now);
            }
        }

        // 5) 行人 / 非机动车推进（独立单元；不并入 Queue / 跟驰，T1.4 / T1.5 口径）。
        StepIndependentUnits();

        _steps++;
    }

    /// <summary>
    /// 本步到达注入：三类需求各按目标 id 升序取 <see cref="DemandGenerator.DrawArrivals"/> 并注入。
    /// </summary>
    /// <param name="t0">本步区间起点（秒）。</param>
    /// <param name="now">本步区间终点 / 事件时刻（秒）。</param>
    /// <remarks>
    /// <para>机动车 → 车道队列入口（<see cref="Queue.TryEnqueue"/>）；行人 → <see cref="PedestrianCrossing.Arrive"/>；
    /// 非机动车 → <see cref="BicycleFlow.Arrive"/>。三者均为"独立单元"通道。</para>
    /// <para>无系统随机：到达数完全由 <c>(Seed, kind, targetId, time)</c> 决定（§4.8）；与加速档无关，
    /// 故 1× 与 32× 到同一仿真时刻的注入序列逐位一致。</para>
    /// </remarks>
    private void InjectArrivals(double t0, double now)
    {
        if (_demand is not null)
        {
            // (a) 机动车需求（车道 id 升序）。
            for (int i = 0; i < _drivingTargets.Length; i++)
            {
                int laneId = _drivingTargets[i];
                int count = _demand.DrawArrivals(DemandKind.Driving, laneId, t0, _config.Dt);
                for (int k = 0; k < count; k++)
                {
                    // ★ 车型按「该车道的到达序号」确定性轮转（T4.3 S1b）；CarOnly / 未设置 → 恒为小汽车。
                    VehicleKind kind = VehicleMixRules.PickKind(_vehicleMix, laneId, _arrivalSeq[laneId]);
                    _arrivalSeq[laneId]++;
                    var vehicle = new Vehicle(_nextVehicleId, kind);
                    if (_queues[laneId].TryEnqueue(vehicle))
                    {
                        _nextVehicleId++;
                        _events.Add(new SimEvent(SimEventKind.DemandArrived, now, laneId, vehicle.Id, 1d));
                    }
                }
            }

            // (b) 行人需求（人行横道转向 id 升序）。
            for (int i = 0; i < _crosswalks.Length; i++)
            {
                PedestrianCrossing crossing = _crosswalks[i];
                int count = _demand.DrawArrivals(DemandKind.Pedestrian, crossing.CrosswalkTurnId, t0, _config.Dt);
                for (int k = 0; k < count; k++)
                {
                    // 两段式（N6a2）：岛的容量与位置由协调器统一持有，行人只需带上"前段多长"。
                    Pedestrian pedestrian = crossing.IsTwoLeg
                        ? new Pedestrian(_nextPedestrianId, crossing.CrosswalkTurnId, crossing.CrossingDistance, crossing.IslandPosition, crossing.IslandId)
                        : new Pedestrian(_nextPedestrianId, crossing.CrosswalkTurnId, crossing.CrossingDistance);

                    crossing.Arrive(pedestrian);
                    _nextPedestrianId++;
                    _events.Add(new SimEvent(SimEventKind.DemandArrived, now, -1, -1, 1d));
                }
            }

            // (c) 非机动车需求（车道 id 升序）。
            for (int i = 0; i < _bicycles.Length; i++)
            {
                BicycleFlow flow = _bicycles[i];
                int count = _demand.DrawArrivals(DemandKind.Bicycle, flow.LaneId, t0, _config.Dt);
                for (int k = 0; k < count; k++)
                {
                    var bicycle = new Bicycle(_nextBicycleId, flow.LaneId, flow.Mode);
                    flow.Arrive(bicycle);
                    _nextBicycleId++;
                    _events.Add(new SimEvent(SimEventKind.DemandArrived, now, flow.LaneId, -1, 1d));
                }
            }
        }

        // (d) 恒定流率注入钩子（T1.6 单测驱动面；Level 形下 _arrivalRate 恒为 0，不影响生产路径）。
        for (int laneId = 0; laneId < _arrivalRate.Length; laneId++)
        {
            double rate = _arrivalRate[laneId];
            if (rate <= 0d)
            {
                continue;
            }

            double credit = _arrivalCredit[laneId] + (rate * _config.Dt);
            while (credit >= 1d - ArrivalEpsilon)
            {
                credit -= 1d;

                var vehicle = new Vehicle(_nextVehicleId, _arrivalKind[laneId]);
                if (_queues[laneId].TryEnqueue(vehicle))
                {
                    _nextVehicleId++;
                    _events.Add(new SimEvent(SimEventKind.DemandArrived, now, laneId, vehicle.Id, 1d));
                }
            }

            _arrivalCredit[laneId] = credit;
        }
    }

    /// <summary>
    /// 推进行人与非机动车（独立单元）：各按其路口信号状态机驱动"等齐过街 / 放行协调"。
    /// </summary>
    /// <remarks>
    /// 行人与非机动车不并入机动车 <c>Queue</c> / 跟驰；二者与机动车侧仅在路口门控处交汇
    /// （<c>IntersectionGate.CanEnter</c>，相位掩码 ∧ 冲突仲裁 ∧ 目标容量）。本卡以"空占用集合"调用
    /// （跨模式的精细冲突仲裁留待后续集成卡，见自检报告"未解决项"）。
    /// </remarks>
    private void StepIndependentUnits()
    {
        for (int i = 0; i < _crosswalks.Length; i++)
        {
            PedestrianCrossing crossing = _crosswalks[i];
            SignalStateMachine? signal = _signals[crossing.NodeId];
            if (signal is null)
            {
                continue;
            }

            // 两段式（N6a2）：岛上空位由协调器自己按岛容量算，这里传 null 即"按岛容量"。
            _ = crossing.Step(_config.Dt, signal, occupiedTurnIds: null, destinationHasSpace: true, islandFreeSlots: null);
        }

        for (int i = 0; i < _bicycles.Length; i++)
        {
            BicycleFlow flow = _bicycles[i];
            SignalStateMachine? signal = _signals[flow.NodeId];
            if (signal is null)
            {
                continue;
            }

            _ = flow.Step(_config.Dt, signal, occupiedTurnIds: null, destinationHasSpace: true);
        }
    }

    /// <summary>
    /// 某车道停止线当前是否放行：无信号路口恒放行；信号路口要求相位为绿 / 绿闪，
    /// 且该车道至少有一条转向在当前相位被放行（复用 T1.3 门控的相位掩码 <c>IsTurnOpen</c>）。
    /// </summary>
    private bool LaneIsOpen(int laneId)
    {
        int nodeId = _network.GetLane(laneId).ToNode;
        SignalStateMachine? machine = _signals[nodeId];
        if (machine is null)
        {
            return true;   // 无信号路口：自由流放行
        }

        if (machine.Phase is not (SignalPhaseState.Green or SignalPhaseState.GreenFlash))
        {
            return false;   // 黄 / 全红：一律禁行
        }

        Stage stage = machine.CurrentStage;
        IReadOnlyList<int> turns = _network.OutgoingTurns(laneId);
        for (int i = 0; i < turns.Count; i++)
        {
            if (_gate.IsTurnOpen(nodeId, turns[i], stage))
            {
                return true;
            }
        }

        return false;   // 本相位无任何放行转向 → 红灯
    }

    /// <summary>把驶离 <paramref name="fromLane"/> 的车辆路由到下游车道入口（无下游则记为驶出网络）。</summary>
    private void Route(Vehicle vehicle, int fromLane, double now)
    {
        IReadOnlyList<int> turns = _network.OutgoingTurns(fromLane);
        if (turns.Count == 0)
        {
            _exitedVehicles++;
            return;
        }

        int turnId = vehicle.NextTurnId;
        if (turnId < 0 || turnId >= _network.TurnCount || _network.GetTurn(turnId).SrcLane != fromLane)
        {
            turnId = turns[0];   // 最小转向 id（升序），确定性兜底
        }

        Turn turn = _network.GetTurn(turnId);
        if (!_queues[turn.DstLane].TryEnqueue(vehicle))
        {
            _exitedVehicles++;   // 下游入口无空位：本卡视为驶出网络（简化，见自检报告）
        }

        _events.Add(new SimEvent(SimEventKind.VehiclePassed, now, fromLane, vehicle.Id, turnId));
    }

    /// <summary>由 <see cref="SimEngine(Level,SimConfig)"/> 使用的路网编译（含 null 前置校验）。</summary>
    /// <exception cref="ArgumentNullException"><paramref name="level"/> 为 null。</exception>
    private static CompiledNetwork CompileLevel(Level level)
    {
        ArgumentNullException.ThrowIfNull(level);
        return level.Network.Compile();
    }

    // ------------------------------------------------------------------
    // 内部：需求目标落位（T1.8）
    //
    // 落位仅做"id 空间整理与去重"，不重复校验器职责（曲线长度 / 升序 / 目标类型等合法性
    // 属 SpmapValidator）。非法目标一律跳过（不注入），与 DemandGenerator 的"越界 id → 0"口径一致。
    // 全部结果按 id 升序排列（确定性；不依赖任何无序集合的遍历顺序）。
    // ------------------------------------------------------------------

    /// <summary>机动车需求目标车道（去重、升序；越界 / 负值忽略）。</summary>
    private int[] CollectDrivingTargets(Level level)
    {
        var lanes = new List<int>();
        for (int i = 0; i < level.Demand.Count; i++)
        {
            int lane = level.Demand[i].Lane;
            if (lane < 0 || lane >= _network.LaneCount || lanes.Contains(lane))
            {
                continue;
            }

            lanes.Add(lane);
        }

        lanes.Sort();
        return lanes.ToArray();
    }

    /// <summary>行人需求的人行横道协调器（按人行横道转向 id 升序；仅"信号路口 + Crosswalk 转向"可驱动）。</summary>
    /// <summary>
    /// 行人需求的人行横道协调器（按人行横道转向 id 升序；仅"信号路口 + Crosswalk 转向"可驱动）。
    /// </summary>
    /// <remarks>
    /// <para><b>两段式（N6a2）</b>：若某条人行横道转向属于"前段 / 后段"这一对（<see cref="Turn.Leg"/> ≥ 0），
    /// 则<b>只建一个协调器</b>（挂在转向 id 较小的那条上），后段由协调器内部按同岛同车道反查得到；
    /// 否则同一对人行横道会各建一个协调器、各放各的人，行人会在岛上被算两次。</para>
    /// <para>需求里只写后段转向（Leg = 1）也允许：协调器以前段为入口，两个 id 都进 <c>targets</c> 时只建一次。</para>
    /// </remarks>
    private PedestrianCrossing[] BuildCrosswalks(Level level)
    {
        var targets = new List<int>();
        for (int i = 0; i < level.Pedestrians.Count; i++)
        {
            int turn = level.Pedestrians[i].CrosswalkTurn;
            if (turn < 0 || turn >= _network.TurnCount || targets.Contains(turn))
            {
                continue;
            }

            targets.Add(turn);
        }

        targets.Sort();

        var crossings = new List<PedestrianCrossing>(targets.Count);
        var handled = new List<int>();   // 已经由某个协调器代表过的分段转向 id
        for (int i = 0; i < targets.Count; i++)
        {
            int turnId = targets[i];
            Turn turn = _network.GetTurn(turnId);

            if (turn.Type != TurnType.Crosswalk)
            {
                continue;   // 非法目标（非人行横道转向）：跳过（SpmapValidator 职责）
            }

            // 等齐过街由信号状态机驱动；非信号路口无状态机可 Tick → 本卡不支持（见自检报告"未解决项"）。
            if (_signals[turn.Node] is null)
            {
                continue;
            }

            // 两段式：一对待一次；若需求写的是后段，仍需先确认前段存在（否则数据不完整，校验器先报错）。
            if (turn.Leg >= 0)
            {
                if (handled.Contains(turnId))
                {
                    continue;
                }

                int firstId = FirstLegTurnIdOf(turn);
                if (firstId < 0)
                {
                    firstId = turn.Id;   // 孤立的单段（无配对）：按前段处理，行为等同一次走完
                }

                crossings.Add(new PedestrianCrossing(_gate, _network, firstId, CrosswalkDistanceOf(turn)));
                handled.Add(turn.Id);
                if (firstId != turn.Id)
                {
                    handled.Add(firstId);
                }

                continue;
            }

            crossings.Add(new PedestrianCrossing(_gate, _network, turnId, CrosswalkDistanceOf(turn)));
        }

        return crossings.ToArray();
    }

    /// <summary>在 <paramref name="turn"/> 的同路口里找它的分段配对（同岛、同起终车道、Leg 互补），返回该转向 id。</summary>
    /// <returns>找到返回配对转向 id；找不到返回 <c>-1</c>。</returns>
    private int FirstLegTurnIdOf(Turn turn)
    {
        int wantLeg = turn.Leg == 1 ? 0 : 1;
        IReadOnlyList<int> atNode = _network.TurnsAtNode(turn.Node);
        for (int i = 0; i < atNode.Count; i++)
        {
            Turn other = _network.GetTurn(atNode[i]);
            if (other.Id == turn.Id || other.Type != TurnType.Crosswalk)
            {
                continue;
            }

            if (other.IslandId == turn.IslandId && other.Leg == wantLeg
                && other.SrcLane == turn.SrcLane && other.DstLane == turn.DstLane)
            {
                return other.Id;
            }
        }

        return -1;
    }

    /// <summary>非机动车需求的放行协调器（按非机动车道 id 升序；仅可解析出参考转向且其路口为信号时才可驱动）。</summary>
    private BicycleFlow[] BuildBicycleFlows(Level level)
    {
        // 车道 id → 模式；同 id 后者覆盖（与 DemandGenerator / signals[] 口径一致）。
        // 用 SortedDictionary：迭代即按车道 id 升序（显式有序，非"字典遍历顺序"）。
        var modes = new SortedDictionary<int, BicycleMode>();
        for (int i = 0; i < level.Bicycles.Count; i++)
        {
            BicycleDemand demand = level.Bicycles[i];
            if (demand.Lane >= 0 && demand.Lane < _network.LaneCount)
            {
                modes[demand.Lane] = demand.Mode;
            }
        }

        var flows = new List<BicycleFlow>(modes.Count);
        foreach (KeyValuePair<int, BicycleMode> entry in modes)
        {
            BicycleFlow flow;
            try
            {
                flow = new BicycleFlow(_gate, _network, entry.Key, entry.Value);
            }
            catch (ArgumentException)
            {
                continue;   // 车道类型 / 参考转向与模式不符：非法目标，跳过（SpmapValidator 职责）
            }

            if (_signals[flow.NodeId] is null)
            {
                continue;   // 参考转向所在路口非信号：本卡无法驱动其相位
            }

            flows.Add(flow);
        }

        return flows.ToArray();
    }

    /// <summary>
    /// 人行横道过街距离（米）：按与冲突矩阵同一套"两点弦"几何 —— 源人行道进口点 → 目标人行道出口点。
    /// </summary>
    /// <remarks>
    /// 与 <c>ConflictMatrix</c>（T1.3）的中心线弦模型逐字一致：
    /// 入口 = <c>N − u_src·D + Right(u_src)·offset</c>、出口 = <c>N + u_dst·D + Right(u_dst)·offset</c>，
    /// 其中 <c>D = CrosswalkChordDepth</c>、<c>Right(v) = (v.Y, −v.X)</c>、
    /// <c>offset = (LaneIndex + 0.5)·CrosswalkLaneWidth</c>。畸变几何（节点重合 / 零长车道）取兜底距离。
    /// </remarks>
    private double CrosswalkDistanceOf(Turn crosswalk)
    {
        Lane src = _network.GetLane(crosswalk.SrcLane);
        Lane dst = _network.GetLane(crosswalk.DstLane);
        Node node = _network.GetNode(crosswalk.Node);

        (double sux, double suy) = LaneDirection(src);
        (double dux, double duy) = LaneDirection(dst);

        double srcOffset = (src.LaneIndex + 0.5d) * CrosswalkLaneWidth;
        double dstOffset = (dst.LaneIndex + 0.5d) * CrosswalkLaneWidth;

        double entryX = node.X - (sux * CrosswalkChordDepth) + (suy * srcOffset);
        double entryY = node.Y - (suy * CrosswalkChordDepth) - (sux * srcOffset);
        double exitX = node.X + (dux * CrosswalkChordDepth) + (duy * dstOffset);
        double exitY = node.Y + (duy * CrosswalkChordDepth) - (dux * dstOffset);

        double dx = exitX - entryX;
        double dy = exitY - entryY;
        double distance = Math.Sqrt((dx * dx) + (dy * dy));

        return distance > MinCrosswalkDistance ? distance : FallbackCrosswalkDistance;
    }

    /// <summary>取车道行驶方向单位向量（<c>FromNode → ToNode</c>）；零长度车道退化为零向量。</summary>
    private (double X, double Y) LaneDirection(Lane lane)
    {
        Node from = _network.GetNode(lane.FromNode);
        Node to = _network.GetNode(lane.ToNode);

        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));

        return length < MinCrosswalkDistance ? (0d, 0d) : (dx / length, dy / length);
    }

    /// <summary>FNV-1a 64 位状态哈希器（逐字节混入，确定性、与进程无关）。</summary>
    private sealed class StateHasher
    {
        private const ulong OffsetBasis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        private ulong _hash = OffsetBasis;

        /// <summary>哈希值。</summary>
        public ulong Value => _hash;

        /// <summary>混入一个 64 位整数。</summary>
        public void Add(long value) => Mix((ulong)value);

        /// <summary>混入一个 32 位整数（零扩展）。</summary>
        public void Add(int value) => Mix((ulong)(uint)value);

        /// <summary>混入一个双精度浮点数（按 IEEE-754 位模式，符号 / 指数 / 尾数全参与）。</summary>
        public void Add(double value) => Mix((ulong)BitConverter.DoubleToInt64Bits(value));

        private void Mix(ulong value)
        {
            for (int i = 0; i < 8; i++)
            {
                _hash ^= value & 0xFFUL;
                _hash *= Prime;
                value >>= 8;
            }
        }
    }
}
