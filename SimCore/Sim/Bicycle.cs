using SimCore.Io;
using SimCore.Model;

namespace SimCore.Sim;

// ---------------------------------------------------------------------------
// T1.5 · 非机动车模型（独立非机动车道 / 机非混行）（技术方案03 §5 · T1.5）
//
// 本文件落地 docs/SimCore-API.md §4.4 冻结的 BicycleSnapshot，并交付非机动车的两个实现类型：
//   * Bicycle      —— 非机动车**独立单元类型**（本任务追加约束：不占用机动车逻辑通道——
//                     不是 Vehicle、不使用 Queue、不参与跟驰 / 饱和流率放行，只沿自己的车道
//                     做确定性匀直运动）。
//   * BicycleFlow  —— 单条非机动车车道（Independent 须 Biking；Mixed 可为 Biking / Driving）
//                     的"放行协调器"，按 BicycleMode 决定受**哪一相位**控制。
//
// 两种模式（游戏设计v2 §9「非机动车：两种模式——独立非机动车道（有独立相位可控）；
// 机非混行（随本向机动车相位放行，起步特性略慢）」）：
//   * Independent（独立非机动车道，关卡 1-8 骑行一族）——**有独立相位可控**：
//       ReferenceTurnId = **本车道自身转向**，放行判据 = 该转向在当前相位非 Banned。
//       于是只有"玩家把本车道自身转向排进某个相位"（自身相位）时才放行。
//   * Mixed（机非混行，关卡 2-9 混行的街道）——**随本向机动车相位放行**：
//       ReferenceTurnId = **同向（同 road / 同 to_node）的机动车转向**，放行判据 = 该机动车转向
//       在当前相位非 Banned。于是混行非机动车必然与同向机动车**同一相位**放行，
//       **不得单独占用相位**（本任务追加约束；由 ResolveReferenceTurn 只接受机动车转向、
//       且构造函数二次校验"参考转向须为同向机动车转向"双重保证）。
//
// 起步特性（验收 c：起步特性慢于机动车，量化断言）：
//   * 机动车起步损失时间 = SimConstants.StartUpLostTime（2.0 s，§4.1「起步损失时间」）；
//   * 混行非机动车 = SimConstants.StartUpLostTime + SimConstants.BicycleStartDelay
//     （2.0 + 1.0 = 3.0 s），即 §4.1 冻结的「混行非机动车起步附加延迟」（T1.5 c）。
//   量化关系：混行起步损失时间 − 机动车起步损失时间 == BicycleStartDelay == 1.0 s &gt; 0。
//
// 验收对应（逐条可测）：
//   a) 独立模式受自身相位控制    → Independent.ReferenceTurnId = 本车道自身转向；
//                                 放行判据 = 该转向在当前相位非 Banned（黄 / 全红相位不放行）。
//   b) 混行模式随本向机动车相位放行 → Mixed.ReferenceTurnId = 同向机动车转向；
//                                 即使自身非机动车转向被禁行，同向机动车相位一绿即放行。
//   c) 起步特性慢于机动车        → Bicycle.BicycleStartUpLostTimeFor(Mixed)
//                                 = StartUpLostTime + BicycleStartDelay &gt; StartUpLostTime（机动车）。
//   追加约束：混行不得单独占用相位 → Mixed 不接受以本车道自身（非机动车）转向为参考转向。
//
// 依赖方向：Sim → Io（BicycleMode / BicycleDemand）、Sim → Model（Lane / Turn / LaneType / Stage）、
//           Sim 内部复用 SignalStateMachine（T1.2）与 IntersectionGate（T1.3）——**仅调用，未改动**。
// 铁律：无 Godot 命名空间、无第三方依赖、无系统随机数（纯确定性匀直运动学）。
// ---------------------------------------------------------------------------

/// <summary>
/// 非机动车的微观状态（T1.5）。与 <see cref="VehicleState"/>、<see cref="PedestrianState"/> 相互独立。
/// </summary>
internal enum BicycleState
{
    /// <summary>等待中：停在停止线（等待区），等自身相位 / 本向机动车相位放行。</summary>
    Waiting,

    /// <summary>通行中：已放行，沿车道匀速前进。</summary>
    Riding,

    /// <summary>已通过：已抵达车道末端并驶离（<c>Position ≥ 车道长度</c>）。</summary>
    Exited,
}

/// <summary>
/// 非机动车快照（<c>docs/SimCore-API.md</c> §4.4 冻结；表现层只读）。
/// </summary>
/// <param name="Id">非机动车 id。</param>
/// <param name="LaneId">所在非机动车道 id。</param>
/// <param name="Position">沿 <paramref name="LaneId"/> 从起点算起的距离（米）。</param>
/// <param name="Mode">通行模式（独立 / 混行）。</param>
/// <param name="Stopped">当前是否处于停车状态（速度 ≈ 0）。</param>
public readonly record struct BicycleSnapshot(
    int Id,
    int LaneId,
    double Position,
    BicycleMode Mode,
    bool Stopped);

/// <summary>
/// 非机动车个体（T1.5；**独立单元类型**，不对应机动车 <see cref="Vehicle"/>、不使用 <see cref="Queue"/>）。
/// </summary>
/// <remarks>
/// <para><b>为什么用独立类型</b>（本任务追加约束）：非机动车的运动学（匀直、不参与跟驰 / 饱和流率放行）
/// 与机动车（跟驰 + 队列 + 饱和流率放行）差异极大；把非机动车硬塞进 <see cref="Queue"/> 会污染机动车
/// 逻辑通道。因此非机动车只携带"车道 / 位置 / 状态"与统计量，由 <see cref="BicycleFlow"/> 统一推进。</para>
///
/// <para><b>运动学口径</b>：放行后沿车道以 <c>车道限速</c>（<see cref="Model.Lane.SpeedLimit"/>）**匀直**前进；
/// 位置写满车道长度即视为已通过（<see cref="BicycleState.Exited"/>），记录 <see cref="ExitTime"/>。
/// 非机动车**只在被放行后才进入 <see cref="BicycleState.Riding"/>**（无闯红灯行为，与行人模型口径一致）。</para>
///
/// <para><b>统计</b>：记录 <see cref="ArriveTime"/>（到达时刻）、<see cref="StartTime"/>（放行 / 起步时刻）、
/// <see cref="ExitTime"/>（通过时刻）、<see cref="WaitTime"/>（累计等待时长）、<see cref="StopCount"/>
/// （通行前停车次数）——供 M2 的 R（服务率）/ D（延误）/ P（停车次数）取用。</para>
///
/// <para>无 Godot 依赖、无第三方依赖、无系统随机数（铁律 1/2）。</para>
/// </remarks>
internal sealed class Bicycle
{
    /// <summary>浮点比较容差（米）。</summary>
    private const double Epsilon = 1e-9d;

    /// <summary>构造一辆非机动车。</summary>
    /// <param name="id">非机动车 id（须 ≥ 0）。</param>
    /// <param name="laneId">所在非机动车道 id（须 ≥ 0）。</param>
    /// <param name="mode">通行模式（独立 / 混行）。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> 或 <paramref name="laneId"/> 为负。</exception>
    public Bicycle(int id, int laneId, BicycleMode mode)
    {
        if (id < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "非机动车 id 不得为负。");
        }

        if (laneId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(laneId), laneId, "非机动车道 id 不得为负。");
        }

        Id = id;
        LaneId = laneId;
        Mode = mode;
    }

    /// <summary>非机动车 id（由上层分配，全局唯一）。</summary>
    public int Id { get; }

    /// <summary>所在非机动车道 id。</summary>
    public int LaneId { get; }

    /// <summary>通行模式（<see cref="BicycleMode.Independent"/> 独立 / <see cref="BicycleMode.Mixed"/> 混行）。</summary>
    public BicycleMode Mode { get; }

    /// <summary>沿所在车道从起点算起的距离（米）；车头位置。</summary>
    public double Position { get; internal set; }

    /// <summary>速度（米/秒）。</summary>
    public double Speed { get; internal set; }

    /// <summary>当前微观状态。</summary>
    public BicycleState State { get; internal set; } = BicycleState.Waiting;

    /// <summary>当前是否处于停车状态（速度 ≤ <see cref="CarFollowing.StopSpeedThreshold"/>）。</summary>
    public bool Stopped => Speed <= CarFollowing.StopSpeedThreshold;

    /// <summary>当前是否滞留停止线等待（未放行）。</summary>
    public bool Waiting => State == BicycleState.Waiting;

    /// <summary>当前是否正在通行。</summary>
    public bool IsRiding => State == BicycleState.Riding;

    /// <summary>到达停止线（等待区）的时刻（秒；未登记为 -1）。</summary>
    public double ArriveTime { get; internal set; } = -1d;

    /// <summary>放行（起步）时刻（秒；未起步为 -1）——用于"起步特性"与延误核算。</summary>
    public double StartTime { get; private set; } = -1d;

    /// <summary>驶离车道（通过）的时刻（秒；未通过为 -1）。</summary>
    public double ExitTime { get; private set; } = -1d;

    /// <summary>累计等待时长（秒；指标 D 的数据源）。</summary>
    public double WaitTime { get; internal set; }

    /// <summary>通行前停车次数（指标 P 的数据源）：被扣在停止线一步（及以上）即计 1 次，未停直接通过计 0。</summary>
    public int StopCount { get; internal set; }

    /// <summary>本车起步损失时间（秒）= <see cref="BicycleStartUpLostTimeFor"/>（验收 c）。</summary>
    public double StartUpLostTime => BicycleStartUpLostTimeFor(Mode);

    /// <summary>
    /// 按模式给出**起步损失时间**（秒，验收 c：起步特性慢于机动车）：
    /// <list type="bullet">
    ///   <item><see cref="BicycleMode.Mixed"/>（机非混行）= <see cref="SimConstants.StartUpLostTime"/> + <see cref="SimConstants.BicycleStartDelay"/>
    ///   （机动车起步损失时间 2.0 s + 混行附加延迟 1.0 s = 3.0 s）；</item>
    ///   <item><see cref="BicycleMode.Independent"/>（独立非机动车道，自身相位）= <see cref="SimConstants.StartUpLostTime"/>
    ///   （与机动车一致，无附加延迟）。</item>
    /// </list>
    /// <b>不得就地硬编码</b>：两个常量均来自 <see cref="SimConstants"/>（§4.1 冻结，全项目唯一真相）。
    /// </summary>
    /// <param name="mode">通行模式。</param>
    /// <returns>起步损失时间（秒）。</returns>
    public static double BicycleStartUpLostTimeFor(BicycleMode mode)
        => mode == BicycleMode.Mixed
            ? SimConstants.StartUpLostTime + SimConstants.BicycleStartDelay
            : SimConstants.StartUpLostTime;

    /// <summary>放行（起步：进入通行状态，由 <see cref="BicycleFlow"/> 在放行时调用）。</summary>
    internal void StartRiding(double now, double speed)
    {
        State = BicycleState.Riding;
        StartTime = now;
        Speed = speed;
    }

    /// <summary>
    /// 推进一个步长（仅在通行中有效）。返回本步是否**恰好抵达车道末端**（用于上层移出）。
    /// </summary>
    /// <param name="dt">步长（秒，&gt; 0）。</param>
    /// <param name="laneLength">车道长度（米）。</param>
    /// <param name="speed">通行速度（米/秒）。</param>
    /// <param name="now">当前仿真时刻（秒）。</param>
    internal bool Advance(double dt, double laneLength, double speed, double now)
    {
        if (State != BicycleState.Riding)
        {
            return false;
        }

        Position += speed * dt;
        if (Position >= laneLength - Epsilon)
        {
            Position = laneLength;
            Speed = 0d;
            State = BicycleState.Exited;
            ExitTime = now;
            return true;
        }

        return false;
    }

    /// <summary>产出只读快照（§4.4）。</summary>
    public BicycleSnapshot ToSnapshot() => new(Id, LaneId, Position, Mode, Stopped);

    /// <inheritdoc />
    public override string ToString()
        => $"Bicycle#{Id}({Mode}) lane={LaneId} pos={Position:F2} v={Speed:F2} {State}";
}

/// <summary>
/// 非机动车放行协调器（T1.5；单条非机动车车道的"独立相位 / 混行随本向相位"）。
/// </summary>
/// <remarks>
/// <para><b>放行判据（<see cref="Step"/>）</b>，四条全部满足才放行（缺一即等待）：
/// <list type="number">
///   <item><b>相位为绿</b>：<c>signal.Phase ∈ {Green, GreenFlash}</c>（黄 / 全红一律不放行）；</item>
///   <item><b>参考转向被放行</b>：<c>signal.PriorityOf(<see cref="ReferenceTurnId"/>) ≠ Banned</c>
///   ——独立模式取**本车道自身转向**（验收 a，自身相位）；混行模式取**同向机动车转向**（验收 b）；</item>
///   <item><b>起步损失时间已过</b>：本相位放行持续时长 ≥ <see cref="StartUpLostTime"/>
///   ——混行比机动车多 <see cref="SimConstants.BicycleStartDelay"/>（验收 c）；</item>
///   <item><b>路口门控</b>：<c>IntersectionGate.CanEnter(...)</c>（相位掩码 ∧ 冲突仲裁 ∧ 目标容量，
///   与 T1.4 行人模型同源复用 T1.3）。</item>
/// </list>
/// </para>
///
/// <para><b>混行不得单独占用相位（追加约束）</b>：混行模式的 <see cref="ReferenceTurnId"/> 必须是
/// "同向（同 <see cref="Lane.Road"/> / 同 <see cref="Lane.ToNode"/>）的机动车转向"；任何试图把
/// 非机动车道自身转向作为混行参考转向的构造都会抛 <see cref="ArgumentException"/>。因此混行非机动车
/// 在相位层面**只可能随同向机动车放行**，不存在"专属相位"。</para>
///
/// <para><b>起步（验收 c）</b>：<see cref="StartUpLostTime"/> 从**本相位放行起点**起算；只有在该时长之内
/// 到达的等待者会经历起步延迟，之后到达者直接通过（相位已运转）。</para>
///
/// <para><b>不占用机动车逻辑通道</b>：本类型只持有 <see cref="Bicycle"/>，不使用 <see cref="Queue"/>，
/// 不参与跟驰与饱和流率放行；与机动车侧仅通过相位与 T1.3 门控在路口处交汇。</para>
///
/// <para><b>确定性（铁律 2）</b>：状态是「仿真时刻 + 到达序列 + 相位」的纯函数；无随机、无系统时钟。</para>
/// </remarks>
internal sealed class BicycleFlow
{
    /// <summary>浮点比较容差（秒）。</summary>
    private const double Epsilon = 1e-9d;

    private readonly IntersectionGate _gate;
    private readonly Lane _lane;
    private readonly List<Bicycle> _all = new();      // 到达顺序（确定性）
    private readonly List<Bicycle> _waiting = new();  // 等待区
    private readonly List<Bicycle> _riding = new();   // 通行中
    private readonly List<Bicycle> _exited = new();   // 已通过

    private double _openElapsed;   // 本相位放行已持续时长（秒）；不放行时归零
    private bool _phaseOpen;       // 本相位是否放行（绿 / 绿闪 ∧ 参考转向非 Banned）

    /// <summary>
    /// 为一条非机动车道构造协调器，自动解析参考转向（<see cref="ResolveReferenceTurn"/>）。
    /// </summary>
    /// <param name="gate">同源路口的门控（<c>IntersectionGate.Build(network, ConflictMatrix.From(network))</c>）。</param>
    /// <param name="network">该门控所用的编译路网。</param>
    /// <param name="laneId">非机动车道 id（Independent 须 <see cref="LaneType.Biking"/>；Mixed 须 <see cref="LaneType.Biking"/> 或 <see cref="LaneType.Driving"/>）。</param>
    /// <param name="mode">通行模式。</param>
    /// <exception cref="ArgumentNullException"><paramref name="gate"/> 或 <paramref name="network"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="laneId"/> 越界。</exception>
    /// <exception cref="ArgumentException">车道类型与模式不符，或找不到合适的参考转向。</exception>
    public BicycleFlow(IntersectionGate gate, CompiledNetwork network, int laneId, BicycleMode mode)
        : this(gate, network, laneId, mode, ResolveReferenceTurn(network, laneId, mode))
    {
    }

    /// <summary>
    /// 为一条非机动车道构造协调器，显式指定参考转向。
    /// </summary>
    /// <param name="gate">同源路口的门控。</param>
    /// <param name="network">该门控所用的编译路网。</param>
    /// <param name="laneId">非机动车道 id。</param>
    /// <param name="mode">通行模式。</param>
    /// <param name="referenceTurnId">参考转向 id：Independent 须以本车道为源车道；Mixed 须为同向机动车转向。</param>
    /// <exception cref="ArgumentNullException"><paramref name="gate"/> 或 <paramref name="network"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="laneId"/> 或 <paramref name="referenceTurnId"/> 越界。</exception>
    /// <exception cref="ArgumentException">车道类型 / 参考转向与模式不符（含"混行不得单独占用相位"约束）。</exception>
    public BicycleFlow(
        IntersectionGate gate,
        CompiledNetwork network,
        int laneId,
        BicycleMode mode,
        int referenceTurnId)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(network);

        if ((uint)laneId >= (uint)network.LaneCount)
        {
            throw new ArgumentOutOfRangeException(nameof(laneId), laneId, "非机动车道 id 越界。");
        }

        if ((uint)referenceTurnId >= (uint)network.TurnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(referenceTurnId), referenceTurnId, "参考转向 id 越界。");
        }

        _gate = gate;
        _lane = network.GetLane(laneId);
        Mode = mode;
        ReferenceTurnId = referenceTurnId;

        Turn referenceTurn = network.GetTurn(referenceTurnId);
        Lane referenceSource = network.GetLane(referenceTurn.SrcLane);

        if (mode == BicycleMode.Independent)
        {
            if (_lane.Type != LaneType.Biking)
            {
                throw new ArgumentException(
                    $"独立模式（Independent）的车道须为 {LaneType.Biking}，实为 {_lane.Type}。",
                    nameof(laneId));
            }

            if (referenceTurn.SrcLane != laneId)
            {
                throw new ArgumentException(
                    $"独立模式的参考转向须以本非机动车道（id={laneId}）为源车道（自身相位），实为源车道 {referenceTurn.SrcLane}。",
                    nameof(referenceTurnId));
            }
        }
        else
        {
            if (_lane.Type is not (LaneType.Biking or LaneType.Driving))
            {
                throw new ArgumentException(
                    $"混行模式（Mixed）的车道须为 {LaneType.Biking} 或 {LaneType.Driving}，实为 {_lane.Type}。",
                    nameof(laneId));
            }

            bool sameApproach = referenceSource.Road == _lane.Road && referenceSource.ToNode == _lane.ToNode;
            if (!sameApproach || !IsMotorLane(referenceSource.Type))
            {
                throw new ArgumentException(
                    "混行模式的参考转向须为同向（同 road / 同 to_node）的机动车转向——" +
                    "混行非机动车随本向机动车相位放行，不得单独占用相位。",
                    nameof(referenceTurnId));
            }
        }

        NodeId = referenceTurn.Node;
    }

    /// <summary>本协调器对应的非机动车道 id。</summary>
    public int LaneId => _lane.Id;

    /// <summary>通行模式。</summary>
    public BicycleMode Mode { get; }

    /// <summary>参考转向所属路口节点 id（= 参考转向的 <see cref="Turn.Node"/>）。</summary>
    public int NodeId { get; }

    /// <summary>
    /// 参考转向 id：独立模式 = 本车道自身转向（自身相位）；混行模式 = 同向机动车转向（本向机动车相位）。
    /// </summary>
    public int ReferenceTurnId { get; }

    /// <summary>本车道长度（米）。</summary>
    public double LaneLength => _lane.Length;

    /// <summary>本协调器的起步损失时间（秒）= <see cref="Bicycle.BicycleStartUpLostTimeFor"/>（验收 c）。</summary>
    public double StartUpLostTime => Bicycle.BicycleStartUpLostTimeFor(Mode);

    /// <summary>本协调器的仿真时刻（秒），随 <see cref="Step"/> 累加。</summary>
    public double Now { get; private set; }

    /// <summary>本相位是否放行（绿 / 绿闪 ∧ 参考转向非 Banned）。</summary>
    public bool IsPhaseOpen => _phaseOpen;

    /// <summary>本相位的起步损失时间是否已过（<see cref="IsPhaseOpen"/> ∧ <c>放行已持续 ≥ <see cref="StartUpLostTime"/></c>）。</summary>
    public bool IsOpen => _phaseOpen && _openElapsed >= StartUpLostTime - Epsilon;

    /// <summary>全部已到达非机动车（按到达顺序，含等待 / 通行中 / 已通过）。</summary>
    public IReadOnlyList<Bicycle> Bicycles => _all;

    /// <summary>等待区非机动车（按到达顺序）。</summary>
    public IReadOnlyList<Bicycle> Waiting => _waiting;

    /// <summary>通行中非机动车（按到达顺序）。</summary>
    public IReadOnlyList<Bicycle> Riding => _riding;

    /// <summary>已通过非机动车（按到达顺序）。</summary>
    public IReadOnlyList<Bicycle> Exited => _exited;

    /// <summary>等待区车辆数。</summary>
    public int WaitingCount => _waiting.Count;

    /// <summary>通行中车辆数。</summary>
    public int RidingCount => _riding.Count;

    /// <summary>已通过车辆数。</summary>
    public int ExitedCount => _exited.Count;

    /// <summary>
    /// 解析某条非机动车道在当前模式下应由哪条转向决定放行（确定性：取满足条件的最小转向 id）。
    /// </summary>
    /// <param name="network">编译路网。</param>
    /// <param name="laneId">非机动车道 id。</param>
    /// <param name="mode">通行模式。</param>
    /// <returns>参考转向 id。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="network"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="laneId"/> 越界。</exception>
    /// <exception cref="ArgumentException">车道类型与模式不符，或无可用参考转向。</exception>
    public static int ResolveReferenceTurn(CompiledNetwork network, int laneId, BicycleMode mode)
    {
        ArgumentNullException.ThrowIfNull(network);

        if ((uint)laneId >= (uint)network.LaneCount)
        {
            throw new ArgumentOutOfRangeException(nameof(laneId), laneId, "非机动车道 id 越界。");
        }

        Lane bicycleLane = network.GetLane(laneId);

        if (mode == BicycleMode.Independent)
        {
            if (bicycleLane.Type != LaneType.Biking)
            {
                throw new ArgumentException(
                    $"独立模式（Independent）的车道须为 {LaneType.Biking}，实为 {bicycleLane.Type}。",
                    nameof(laneId));
            }

            for (int t = 0; t < network.TurnCount; t++)
            {
                if (network.GetTurn(t).SrcLane == laneId)
                {
                    return t;   // 本车道自身转向（最小 id，确定性）
                }
            }

            throw new ArgumentException(
                $"独立非机动车道 {laneId} 无任何转向（turns[] 中无 src_lane = {laneId}），无法受自身相位控制。",
                nameof(laneId));
        }

        if (bicycleLane.Type is not (LaneType.Biking or LaneType.Driving))
        {
            throw new ArgumentException(
                $"混行模式（Mixed）的车道须为 {LaneType.Biking} 或 {LaneType.Driving}，实为 {bicycleLane.Type}。",
                nameof(laneId));
        }

        for (int t = 0; t < network.TurnCount; t++)
        {
            Turn turn = network.GetTurn(t);
            Lane source = network.GetLane(turn.SrcLane);
            if (source.Road == bicycleLane.Road && source.ToNode == bicycleLane.ToNode && IsMotorLane(source.Type))
            {
                return t;   // 同向机动车转向（最小 id，确定性）
            }
        }

        throw new ArgumentException(
            $"混行车道 {laneId} 找不到同向机动车转向（turns[] 中无 road = {bicycleLane.Road}、to_node = {bicycleLane.ToNode} 的机动车转向）。",
            nameof(laneId));
    }

    /// <summary>
    /// 登记一辆非机动车到达停止线（等待区）。
    /// </summary>
    /// <param name="bicycle">非机动车（<see cref="Bicycle.LaneId"/> 须与本协调器一致，且尚未登记）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="bicycle"/> 为 null。</exception>
    /// <exception cref="ArgumentException">非机动车属于另一条车道，或已被登记过。</exception>
    public void Arrive(Bicycle bicycle)
    {
        ArgumentNullException.ThrowIfNull(bicycle);

        if (bicycle.LaneId != LaneId)
        {
            throw new ArgumentException(
                $"非机动车 #{bicycle.Id} 的车道为 {bicycle.LaneId}，与本协调器 {LaneId} 不一致。",
                nameof(bicycle));
        }

        if (bicycle.ArriveTime >= 0d)
        {
            throw new ArgumentException(
                $"非机动车 #{bicycle.Id} 已被登记（ArriveTime={bicycle.ArriveTime}），不得重复到达。",
                nameof(bicycle));
        }

        bicycle.ArriveTime = Now;
        _all.Add(bicycle);
        _waiting.Add(bicycle);
    }

    /// <summary>
    /// 推进一个步长：先让通行中非机动车前进，再按放行判据决定是否放行等待区。
    /// </summary>
    /// <param name="dt">步长（秒，&gt; 0）。</param>
    /// <param name="signal">本车道所属路口的信号状态机（提供相位 / 相位掩码）。</param>
    /// <param name="occupiedTurnIds">当前路口**已占用**的转向 id 集合（可为 null = 视为空）。</param>
    /// <param name="destinationHasSpace">目标车道是否还有空位；<c>false</c> 时不放行。</param>
    /// <returns>本步被放行的车辆数；0 表示无放行。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="signal"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dt"/> 非正 / 非有限。</exception>
    public int Step(
        double dt,
        SignalStateMachine signal,
        IReadOnlyList<int>? occupiedTurnIds = null,
        bool destinationHasSpace = true)
    {
        ArgumentNullException.ThrowIfNull(signal);

        if (double.IsNaN(dt) || double.IsInfinity(dt) || dt <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(dt), dt, "步长必须为有限正值。");
        }

        Now += dt;

        // 1) 通行中非机动车前进（确定性匀直；到车道末端即通过）。
        for (int i = _riding.Count - 1; i >= 0; i--)
        {
            Bicycle b = _riding[i];
            if (b.Advance(dt, _lane.Length, _lane.SpeedLimit, Now))
            {
                _riding.RemoveAt(i);
                _exited.Add(b);
            }
        }

        // 2) 等待者累计等待时长（无论本步是否放行）。
        for (int i = 0; i < _waiting.Count; i++)
        {
            _waiting[i].WaitTime += dt;
        }

        // 3) 相位判据：绿 / 绿闪 ∧ 参考转向（独立=自身 / 混行=本向机动车）非 Banned。
        _phaseOpen = (signal.Phase is SignalPhaseState.Green or SignalPhaseState.GreenFlash)
            && signal.PriorityOf(ReferenceTurnId) != TurnPriority.Banned;

        _openElapsed = _phaseOpen ? _openElapsed + dt : 0d;

        // 4) 起步损失时间未过 → 等待（含验收 c 的起步延迟）。
        if (!_phaseOpen || _openElapsed < StartUpLostTime - Epsilon)
        {
            CountStops();
            return 0;
        }

        if (_waiting.Count == 0)
        {
            return 0;
        }

        // 5) 路口门控（复用 T1.3；与行人模型同源：相位掩码 ∧ 冲突仲裁 ∧ 目标容量）。
        if (!_gate.CanEnter(NodeId, ReferenceTurnId, signal.CurrentStage, occupiedTurnIds, destinationHasSpace))
        {
            CountStops();
            return 0;
        }

        // 6) 放行整队（起步损失时间已过）：批量进入通行状态。
        int released = _waiting.Count;
        for (int i = 0; i < _waiting.Count; i++)
        {
            Bicycle b = _waiting[i];
            b.StartRiding(Now, _lane.SpeedLimit);
            _riding.Add(b);
        }

        _waiting.Clear();
        return released;
    }

    /// <summary>
    /// 未通过非机动车（等待 + 通行中）的只读快照，按到达顺序排列（供 <c>SimState.Bicycles</c>，§4.4）。
    /// </summary>
    public IReadOnlyList<BicycleSnapshot> Snapshots()
    {
        var list = new List<BicycleSnapshot>(_all.Count);
        for (int i = 0; i < _all.Count; i++)
        {
            Bicycle b = _all[i];
            if (b.State != BicycleState.Exited)
            {
                list.Add(b.ToSnapshot());
            }
        }

        return list;
    }

    /// <summary>复位到 t=0 的空状态（清空全部非机动车、时刻与相位计时归零）。</summary>
    public void Reset()
    {
        _all.Clear();
        _waiting.Clear();
        _riding.Clear();
        _exited.Clear();
        Now = 0d;
        _openElapsed = 0d;
        _phaseOpen = false;
    }

    /// <inheritdoc />
    public override string ToString()
        => $"BicycleFlow(lane={LaneId}, {Mode}, refTurn={ReferenceTurnId}, wait={WaitingCount}, riding={RidingCount}, exited={ExitedCount})";

    /// <summary>机动车车道类型（本向机动车相位的来源）。</summary>
    private static bool IsMotorLane(LaneType type)
        => type is LaneType.Driving or LaneType.Bus or LaneType.SharedLeftTurn;

    /// <summary>等待者计一次停车（被扣在停止线一步及以上，未停直接通过者不计）。</summary>
    private void CountStops()
    {
        for (int i = 0; i < _waiting.Count; i++)
        {
            Bicycle b = _waiting[i];
            if (b.StopCount == 0)
            {
                b.StopCount = 1;
            }
        }
    }
}
