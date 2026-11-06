using SimCore.Model;

namespace SimCore.Sim;

// ---------------------------------------------------------------------------
// T1.4 · 行人模型（等齐过街）（技术方案03 §5 · T1.4）
//
// 本文件落地 docs/SimCore-API.md §4.4 冻结的 PedestrianSnapshot，并交付行人的两个实现类型：
//   * Pedestrian          —— 行人**独立单元类型**（等齐过街的个体）。**不占用机动车逻辑通道**：
//                            不是 Vehicle、不使用 Queue、不参与跟驰 / 饱和流率放行，只按
//                            SimConstants.PedestrianSpeed（1.4 m/s）做匀直运动。
//   * PedestrianCrossing  —— 单条人行横道（一个 TurnType.Crosswalk 转向）的"等齐过街"协调器：
//                            相位开始时等待者**集体通过**；**绿灯尾不放行新人**；过街最短绿灯
//                            = 过街距离 / 1.4 s。
//
// 与 T1.3 的关系（"冲突纳入 T1.3"）：行人与**转向车流**的冲突判定**完全复用** T1.3 的
//   ConflictMatrix（几何冲突矩阵）+ IntersectionGate（路口门控），**不另起一套判定**：
//   * 人行横道转向与人行横道转向以外的机动车转向一样，都是 Turn，天然进入同一张冲突矩阵；
//   * 放行决策调用 IntersectionGate.CanEnter（相位掩码 ∧ 冲突仲裁 ∧ 目标容量，internal）；
//   * 行人过街中把自身人行横道转向 id 计入路口"占用转向集合"（OccupiedTurnIds），
//     供机动车侧用**同一套**门控判定"机动车须让行正在过街的行人"。
//
// 验收对应（逐条可测）：
//   a) 行人仅在自身相位内通过        → Step 仅在「相位为绿/绿闪」且「人行横道转向本相位被放行」时放行；
//                                      黄 / 全红 / 人行横道被 Banned 时一律不放行。
//   b) 绿灯末尾新到行人等下一周期    → 剩余绿灯 < 最短绿灯（距离 / 1.4）时一律不放行新人（绿灯尾不放行）；
//                                      该批行人保持等待，直到下一次自身相位给足绿灯。
//   c) 过街最短绿灯 = 距离 / 1.4 s   → Pedestrian.MinGreenTime / MinGreenTimeFor(distance) /
//                                      PedestrianCrossing.MinGreenTime；同时也是 b) 的放行阈值判据。
//   d) 与转向车流的冲突被门控拦住    → 复用 IntersectionGate.CanEnter（冲突仲裁：Yield 让行 Protected）；
//                                      CanEnter 拒绝时行人被拦在路缘（Waiting）。
//
// 铁律：无 Godot 命名空间、无第三方依赖、无系统随机数（纯确定性匀直运动学）。
// ---------------------------------------------------------------------------

/// <summary>
/// 行人的微观状态（T1.4）。与 <see cref="VehicleState"/> 相互独立（行人用独立类型，不占用机动车逻辑通道）。
/// </summary>
internal enum PedestrianState
{
    /// <summary>等待中：滞留路缘等待区，等待自身相位给足绿灯后集体过街。</summary>
    Waiting,

    /// <summary>
    /// 过街中：已进入人行横道，按 <see cref="SimConstants.PedestrianSpeed"/> 匀直前进。
    /// <para>注意：两段式过街里，前段（路缘 → 岛）也处于本状态；走到岛后转入 <see cref="OnIsland"/>。</para>
    /// </summary>
    Crossing,

    /// <summary>
    /// 在中央安全岛上等待（2026-10-25 E92 G2 · N6a2 新增）：前段已走完、后段尚未放行。
    /// 本状态<b>已经离开路缘</b>，故不等于 <see cref="Waiting"/>（路人看到的是"站在岛中间"）。
    /// </summary>
    OnIsland,

    /// <summary>已通过：已抵达对岸（<c>Position ≥ CrossingDistance</c>）。</summary>
    Crossed,
}

/// <summary>
/// 两段式过街中"行人当前站在哪一段/哪一端"的位置（2026-10-25 E92 G2 · N6a2）。
/// </summary>
/// <remarks>
/// <para>本枚举只在<b>分段过街</b>（<see cref="Turn.Leg"/> ≥ 0）时才有意义；一次走完的人行横道恒为
/// <see cref="AtOrigin"/> → <see cref="AtDestination"/>，中间不经过岛。</para>
/// </remarks>
public enum PedestrianLegPlace
{
    /// <summary>在起点路缘（尚未进入前段）。</summary>
    AtOrigin = 0,

    /// <summary>在中央安全岛上（前段已走完，等后段放行）。</summary>
    OnIsland = 1,

    /// <summary>已到对岸（后段走完）。</summary>
    AtDestination = 2,
}

/// <summary>
/// 行人快照（<c>docs/SimCore-API.md</c> §4.4 冻结；表现层只读）。
/// </summary>
/// <param name="Id">行人 id。</param>
/// <param name="CrosswalkTurnId">所走人行横道转向 id（<see cref="TurnType.Crosswalk"/>）。</param>
/// <param name="Position">沿人行横道已行进的距离（米，从出发点路缘算起）。</param>
/// <param name="Waiting">当前是否滞留路缘等待（未进入人行横道）。</param>
public readonly record struct PedestrianSnapshot(
    int Id,
    int CrosswalkTurnId,
    double Position,
    bool Waiting);

/// <summary>
/// 行人个体（T1.4；等齐过街的**独立单元类型**，不对应机动车 <see cref="Vehicle"/>、不使用 <see cref="Queue"/>）。
/// </summary>
/// <remarks>
/// <para><b>为什么用独立类型</b>（本任务追加约束）：行人的运动学（匀速 1.4 m/s、无跟驰、无排队、无饱和流率）
/// 与机动车（跟驰 + 队列 + 饱和流率放行）差异极大；把行人硬塞进 <see cref="Queue"/> 会污染机动车逻辑通道。
/// 因此行人只携带"距离 / 位置 / 状态"三件事，由 <see cref="PedestrianCrossing"/> 统一推进。</para>
///
/// <para><b>运动学口径</b>：过街速度为 <see cref="SimConstants.PedestrianSpeed"/>（= 1.4 m/s，验收 c 的分母），
/// 沿人行横道**匀直**前进；位置写满即视为已通过（<see cref="PedestrianState.Crossed"/>），记录 <see cref="ExitTime"/>。
/// <b>无闯红灯行为</b>（游戏设计v2 §9）：只有在 <see cref="PedestrianCrossing"/> 判定放行后才会进入 <see cref="PedestrianState.Crossing"/>。</para>
///
/// <para><b>统计</b>：记录 <see cref="ArriveTime"/>（到达时刻）、<see cref="StartTime"/>（开始过街时刻）、
/// <see cref="ExitTime"/>（通过时刻）、<see cref="WaitTime"/>（累计等待时长）——供 M2 的行人延误 / 服务率取用。</para>
///
/// <para>无 Godot 依赖、无第三方依赖、无系统随机数（铁律 1/2）。</para>
/// </remarks>
internal sealed class Pedestrian
{
    /// <summary>浮点比较容差（米）。</summary>
    private const double Epsilon = 1e-9d;

    /// <summary>构造一个行人。</summary>
    /// <param name="id">行人 id（须 ≥ 0）。</param>
    /// <param name="crosswalkTurnId">所走人行横道转向 id（须 ≥ 0，且须为 <see cref="TurnType.Crosswalk"/> 转向）。</param>
    /// <param name="crossingDistance">过街距离（米，须 &gt; 0）；决定最短绿灯 <c>距离 / 1.4</c>。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> / <paramref name="crosswalkTurnId"/> 为负，或
    /// <paramref name="crossingDistance"/> 非有限正数。</exception>
    public Pedestrian(int id, int crosswalkTurnId, double crossingDistance)
    {
        if (id < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "行人 id 不得为负。");
        }

        if (crosswalkTurnId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(crosswalkTurnId), crosswalkTurnId, "人行横道转向 id 不得为负。");
        }

        if (double.IsNaN(crossingDistance) || double.IsInfinity(crossingDistance) || crossingDistance <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(crossingDistance), crossingDistance, "过街距离必须为有限正数。");
        }

        Id = id;
        CrosswalkTurnId = crosswalkTurnId;
        CrossingDistance = crossingDistance;
        IslandId = -1;
        IslandPosition = -1d;
    }

    /// <summary>
    /// 构造一个<b>两段式过街</b>的行人（2026-10-25 E92 G2 · N6a2）：先走 <paramref name="firstLegDistance"/> 米到中央岛，
    /// 在岛上等后段放行，再走剩下的 <c>totalDistance − firstLegDistance</c> 米到对岸。
    /// </summary>
    /// <param name="id">行人 id（须 ≥ 0）。</param>
    /// <param name="crosswalkTurnId">所走人行横道转向 id（须 ≥ 0）。</param>
    /// <param name="totalDistance">总过街距离（路缘 → 对岸，米，须 &gt; 0）。</param>
    /// <param name="firstLegDistance">前段距离（路缘 → 岛，米，须 ∈ (0, totalDistance)）。</param>
    /// <param name="islandId">中央安全岛 id（须 ≥ 0）。</param>
    /// <exception cref="ArgumentOutOfRangeException">任一参数越界。</exception>
    public Pedestrian(int id, int crosswalkTurnId, double totalDistance, double firstLegDistance, int islandId)
        : this(id, crosswalkTurnId, totalDistance)
    {
        if (islandId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(islandId), islandId, "安全岛 id 不得为负。");
        }

        if (double.IsNaN(firstLegDistance) || double.IsInfinity(firstLegDistance)
            || firstLegDistance <= 0d || firstLegDistance >= totalDistance)
        {
            throw new ArgumentOutOfRangeException(
                nameof(firstLegDistance), firstLegDistance, "前段距离必须为有限正数，且严格小于总过街距离。");
        }

        IslandId = islandId;
        IslandPosition = firstLegDistance;
    }

    /// <summary>行人 id（由上层分配，全局唯一）。</summary>
    public int Id { get; }

    /// <summary>所走人行横道转向 id（<see cref="TurnType.Crosswalk"/>）。</summary>
    public int CrosswalkTurnId { get; }

    /// <summary>过街距离（米）。</summary>
    public double CrossingDistance { get; }

    /// <summary>
    /// 分段过街所属的安全岛 id（2026-10-25 E92 G2 · N6a2）；<c>-1</c> = 一次走完（不经过岛）。
    /// </summary>
    public int IslandId { get; }

    /// <summary>
    /// 中央岛在过街线上的位置（米，从起点路缘算起）；<c>-1</c> = 无岛（一次走完）。
    /// </summary>
    public double IslandPosition { get; }

    /// <summary>是否为两段式过街（经过中央安全岛）。</summary>
    public bool IsTwoLeg => IslandId >= 0;

    /// <summary>前段距离（路缘 → 岛）；无岛时等于 <see cref="CrossingDistance"/>。</summary>
    public double FirstLegDistance => IsTwoLeg ? IslandPosition : CrossingDistance;

    /// <summary>后段距离（岛 → 对岸）；无岛时为 0。</summary>
    public double SecondLegDistance => IsTwoLeg ? CrossingDistance - IslandPosition : 0d;

    /// <summary>
    /// 当前在过街线上的哪个位置（起点路缘 / 中央岛 / 对岸）。供表现层决定"人画在哪"。
    /// </summary>
    public PedestrianLegPlace Place
    {
        get
        {
            if (State == PedestrianState.Crossed)
            {
                return PedestrianLegPlace.AtDestination;
            }

            return State == PedestrianState.OnIsland ? PedestrianLegPlace.OnIsland : PedestrianLegPlace.AtOrigin;
        }
    }


    /// <summary>过街速度（米/秒）= <see cref="SimConstants.PedestrianSpeed"/>（1.4）。</summary>
    public double Speed => SimConstants.PedestrianSpeed;

    /// <summary>
    /// 过街最短绿灯（秒）= <see cref="CrossingDistance"/> / <see cref="Speed"/>（验收 c）。
    /// 同时也是"绿灯尾不放行新人"的放行阈值：剩余绿灯不足此值时不得放行新人。
    /// </summary>
    /// <remarks>
    /// <b>两段式过街的口径</b>：最短绿灯指<b>单段</b>要走完的那段时间。前段最短绿灯 =
    /// <c>FirstLegDistance / 1.4</c>（见 <see cref="MinGreenTimeForFirstLeg"/>），
    /// 后段 = <c>SecondLegDistance / 1.4</c>；本属性（整程）只用于"一次走完"与工程提示。
    /// </remarks>
    public double MinGreenTime => MinGreenTimeFor(CrossingDistance);

    /// <summary>前段（路缘 → 岛）最短绿灯（秒）；无岛时等于整程最短绿灯。</summary>
    public double MinGreenTimeForFirstLeg => MinGreenTimeFor(FirstLegDistance);

    /// <summary>沿人行横道已行进的距离（米，从出发点路缘算起）。</summary>
    public double Position { get; private set; }

    /// <summary>当前微观状态。</summary>
    public PedestrianState State { get; private set; } = PedestrianState.Waiting;

    /// <summary>当前是否滞留路缘等待（未进入人行横道）。</summary>
    public bool Waiting => State == PedestrianState.Waiting;

    /// <summary>当前是否正在过街。</summary>
    public bool IsCrossing => State == PedestrianState.Crossing;

    /// <summary>当前是否在中央安全岛上等待后段。</summary>
    public bool IsOnIsland => State == PedestrianState.OnIsland;


    /// <summary>到达等待区的时刻（秒；未登记为 -1）。</summary>
    public double ArriveTime { get; internal set; } = -1d;

    /// <summary>开始过街的时刻（秒；未开始为 -1）。</summary>
    public double StartTime { get; private set; } = -1d;

    /// <summary>抵达对岸的时刻（秒；未通过为 -1）。</summary>
    public double ExitTime { get; private set; } = -1d;

    /// <summary>抵达中央安全岛的时刻（秒；未到岛 / 无岛为 -1）。</summary>
    public double IslandArriveTime { get; private set; } = -1d;

    /// <summary>是否已进入后段（岛 → 对岸）；无岛时恒为 <c>false</c>。</summary>
    public bool IsOnSecondLeg { get; private set; }

    /// <summary>累计等待时长（秒；指标"行人延误"的数据源）。</summary>
    public double WaitTime { get; internal set; }

    /// <summary>
    /// 过街最短绿灯的计算式（验收 c）：<c>距离 / 1.4</c>（借 A/B Street <c>CROSSWALK_PACE = 1.4 m/s</c>，
    /// 技术方案02 §2.2c）。供 T3.3 相位面板"工程提示"与 <see cref="PedestrianCrossing"/> 复用，不得另行硬编码。
    /// </summary>
    /// <param name="crossingDistance">过街距离（米）。</param>
    /// <returns>最短绿灯时长（秒）。</returns>
    public static double MinGreenTimeFor(double crossingDistance)
        => crossingDistance / SimConstants.PedestrianSpeed;

    /// <summary>开始过街（由 <see cref="PedestrianCrossing"/> 在放行时调用）。</summary>
    internal void StartCrossing(double now)
    {
        State = PedestrianState.Crossing;
        StartTime = now;
    }

    /// <summary>
    /// 从岛上出发走后段（由 <see cref="PedestrianCrossing"/> 在后段放行时调用）。
    /// </summary>
    /// <param name="now">当前仿真时刻（秒）。</param>
    /// <exception cref="InvalidOperationException">行人当前不在岛上。</exception>
    internal void StartSecondLeg(double now)
    {
        if (State != PedestrianState.OnIsland)
        {
            throw new InvalidOperationException($"行人 #{Id} 不在安全岛上（当前 {State}），无法开始后段。");
        }

        State = PedestrianState.Crossing;
        IsOnSecondLeg = true;
        IslandArriveTime = now;
    }

    /// <summary>
    /// 推进一个步长（仅在过街中有效）。返回本步是否 <b>恰好抵达终点</b>（对岸 / 无岛时即对岸；用于上层移出）。
    /// </summary>
    /// <param name="dt">步长（秒，&gt; 0）。</param>
    /// <param name="now">当前仿真时刻（秒）。</param>
    /// <remarks>
    /// <para><b>两段式过街</b>：走完前段时位置被钳到 <see cref="IslandPosition"/>，状态转入
    /// <see cref="PedestrianState.OnIsland"/>，并返回 <c>false</c>（人还在场，不能移出）；
    /// 后段由 <see cref="StartSecondLeg"/> 启动后继续推进到对岸。</para>
    /// <para><b>无岛（一次走完）</b>：与 T1.4 原实现逐位一致。</para>
    /// </remarks>
    internal bool Advance(double dt, double now)
    {
        if (State != PedestrianState.Crossing)
        {
            return false;
        }

        Position += Speed * dt;

        // 两段式：前段走到岛即停，转入岛上等待（本步不算"抵达终点"）。
        if (IsTwoLeg && !IsOnSecondLeg && Position >= IslandPosition - Epsilon)
        {
            Position = IslandPosition;
            State = PedestrianState.OnIsland;
            return false;
        }

        if (Position >= CrossingDistance - Epsilon)
        {
            Position = CrossingDistance;
            State = PedestrianState.Crossed;
            ExitTime = now;
            return true;
        }

        return false;
    }

    /// <summary>产出只读快照（§4.4）。</summary>
    public PedestrianSnapshot ToSnapshot() => new(Id, CrosswalkTurnId, Position, Waiting);

    /// <inheritdoc />
    public override string ToString()
        => IsTwoLeg
            ? $"Pedestrian#{Id} cw={CrosswalkTurnId} leg={Position:F2}/{IslandPosition:F2}/{CrossingDistance:F2} {State}"
            : $"Pedestrian#{Id} cw={CrosswalkTurnId} pos={Position:F2}/{CrossingDistance:F2} {State}";
}

/// <summary>
/// 人行横道协调器（T1.4；"等齐过街" + 冲突纳入 T1.3）。
/// </summary>
/// <remarks>
/// <para><b>等齐过街（游戏设计v2 §9）</b>：行人在路缘等待区**攒批**，相位开始（本相位把人行横道转向放行、
/// 且剩余绿灯足够）时**集体通过**；<b>绿灯尾不放行新人</b>——剩余绿灯不足以走完全程（&lt; 最短绿灯
/// <c>距离 / 1.4</c>）时，新到者一律留在等待区，等下一周期。</para>
///
/// <para><b>放行判据（<see cref="Step"/>）</b>，四条全部满足才放行（缺一即等待）：
/// <list type="number">
///   <item><b>相位为绿</b>：<c>signal.Phase ∈ {Green, GreenFlash}</c>（黄 / 全红一律不放行，验收 a）；</item>
///   <item><b>自身相位放行</b>：<c>signal.PriorityOf(CrosswalkTurnId) ≠ Banned</c>（人行横道转向不在本相位放行集合 → 不放行，验收 a）；</item>
///   <item><b>绿灯尾余量</b>：<c>signal.PhaseRemaining ≥ MinGreenTime</c>（剩余绿灯不足 → 不放行新人，验收 b/c）；</item>
///   <item><b>路口门控</b>：<c>IntersectionGate.CanEnter(...)</c>——相位掩码 ∧ <b>冲突仲裁</b>（与已占用的
///   <see cref="TurnPriority.Protected"/> 转向冲突时，本转向若为 <see cref="TurnPriority.Yield"/> 则被拦）∧ 目标容量（验收 d，复用 T1.3）。</item>
/// </list>
/// </para>
///
/// <para><b>冲突纳入 T1.3</b>：本类型不自行判定冲突；过街中把自身人行横道转向 id 通过
/// <see cref="OccupiedTurnIds"/> 暴露给机动车侧，机动车侧用**同一张** <c>ConflictMatrix</c> /
/// <c>IntersectionGate</c> 判定"须让行正在过街的行人"（如受保护左转 vs 冲突人行横道）。</para>
///
/// <para><b>行人不占用机动车逻辑通道</b>：本类型只持有 <see cref="Pedestrian"/>，不使用 <see cref="Queue"/>，
/// 不参与跟驰与饱和流率放行；机动车侧与行人侧仅通过 T1.3 的门控在路口处交汇。</para>
///
/// <para><b>确定性（铁律 2）</b>：状态是「仿真时刻 + 到达序列 + 相位」的纯函数；无随机、无系统时钟。</para>
/// </remarks>
internal sealed class PedestrianCrossing
{
    /// <summary>浮点比较容差（秒）。</summary>
    private const double Epsilon = 1e-9d;

    private static readonly int[] EmptyTurns = Array.Empty<int>();

    private readonly IntersectionGate _gate;
    private readonly int _nodeId;
    private readonly List<Pedestrian> _all = new();       // 到达顺序（确定性）
    private readonly List<Pedestrian> _waiting = new();    // 等待区（在起点路缘）
    private readonly List<Pedestrian> _onIsland = new();   // 中央安全岛上等待后段（N6a2）
    private readonly List<Pedestrian> _crossing = new();   // 过街中
    private readonly List<Pedestrian> _crossed = new();    // 已通过
    private readonly int[] _occupying;                     // 过街中占用的人行横道转向 id（单元素）
    private readonly int _islandId;                        // 中央安全岛 id（-1 = 无岛）
    private readonly int _islandCapacity;                  // 岛上可站人数（无岛时为 0）
    private readonly double _islandPosition;               // 岛在过街线上的位置（米；-1 = 无岛）
    private readonly int _secondLegTurnId;                 // 后段转向 id（岛 → 对岸；-1 = 无后段）

    /// <summary>
    /// 为一条人行横道转向构造协调器。
    /// </summary>
    /// <param name="gate">同源路口的门控（<c>IntersectionGate.Build(network, ConflictMatrix.From(network))</c>）。</param>
    /// <param name="network">该门控所用的编译路网（用于校验转向与取节点）。</param>
    /// <param name="crosswalkTurnId">人行横道转向 id（其 <see cref="Turn.Type"/> 须为 <see cref="TurnType.Crosswalk"/>）。</param>
    /// <param name="crossingDistance">过街距离（米，&gt; 0）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="gate"/> 或 <paramref name="network"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="crosswalkTurnId"/> 越界，或 <paramref name="crossingDistance"/> 非有限正数。</exception>
    /// <exception cref="ArgumentException"><paramref name="crosswalkTurnId"/> 指向的转向不是 <see cref="TurnType.Crosswalk"/>。</exception>
    public PedestrianCrossing(
        IntersectionGate gate,
        CompiledNetwork network,
        int crosswalkTurnId,
        double crossingDistance)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(network);

        if ((uint)crosswalkTurnId >= (uint)network.TurnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(crosswalkTurnId), crosswalkTurnId, "人行横道转向 id 越界。");
        }

        Turn turn = network.GetTurn(crosswalkTurnId);
        if (turn.Type != TurnType.Crosswalk)
        {
            throw new ArgumentException(
                $"转向 {crosswalkTurnId} 的类型为 {turn.Type}，不是 Crosswalk —— 行人不占用机动车逻辑通道，只能走在人行横道转向。",
                nameof(crosswalkTurnId));
        }

        if (double.IsNaN(crossingDistance) || double.IsInfinity(crossingDistance) || crossingDistance <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(crossingDistance), crossingDistance, "过街距离必须为有限正数。");
        }

        _gate = gate;
        _nodeId = turn.Node;
        CrosswalkTurnId = crosswalkTurnId;
        CrossingDistance = crossingDistance;
        _occupying = [crosswalkTurnId];

        // 两段式（N6a2）：转向带 IslandId 时，按岛的容量与"岛在过街线上的位置"驱动二次过街。
        if (turn.IslandId >= 0)
        {
            if (!network.TryGetIsland(turn.IslandId, out Island? island) || island is null)
            {
                throw new ArgumentException(
                    $"转向 {crosswalkTurnId} 指向安全岛 {turn.IslandId}，但编译路网里没有这个岛。",
                    nameof(crosswalkTurnId));
            }

            if (island.Capacity < 1)
            {
                throw new ArgumentException(
                    $"安全岛 {island.Id} 的容量为 {island.Capacity}，必须 ≥ 1。",
                    nameof(crosswalkTurnId));
            }

            _islandId = island.Id;
            _islandCapacity = island.Capacity;
            _islandPosition = IslandPositionOf(network, turn, crossingDistance);
            _secondLegTurnId = ResolveSecondLeg(network, turn);
        }
        else
        {
            _islandId = -1;
            _islandCapacity = 0;
            _islandPosition = -1d;
            _secondLegTurnId = -1;
        }
    }

    /// <summary>
    /// 找同一条人行横道的"后段"转向（<see cref="Turn.Leg"/> = 1，且同岛、同起点 / 终点车道）。
    /// </summary>
    /// <returns>后段转向 id；找不到返回 <c>-1</c>（不抛出 —— 数据完整性由校验器先报 <c>crosswalk_leg_incomplete</c>）。</returns>
    /// <remarks>
    /// 前段（调用方传入的 <paramref name="firstLeg"/>）按 <see cref="Turn.Leg"/> 判断方向：
    /// 若调用方给的其实是后段（Leg = 1），则反过来找 Leg = 0 的那条，由 <see cref="Step"/> 统一按"先前段、后后段"处理。
    /// </remarks>
    private static int ResolveSecondLeg(CompiledNetwork network, Turn firstLeg)
    {
        int wantLeg = firstLeg.Leg == 1 ? 0 : 1;
        IReadOnlyList<int> atNode = network.TurnsAtNode(firstLeg.Node);
        for (int i = 0; i < atNode.Count; i++)
        {
            Turn other = network.GetTurn(atNode[i]);
            if (other.Id == firstLeg.Id || other.Type != TurnType.Crosswalk)
            {
                continue;
            }

            if (other.IslandId == firstLeg.IslandId && other.Leg == wantLeg
                && other.SrcLane == firstLeg.SrcLane && other.DstLane == firstLeg.DstLane)
            {
                return other.Id;
            }
        }

        return -1;
    }

    /// <summary>
    /// 安全岛在这条人行横道上的位置（米，从起点路缘算起）：优先取岛心投影，退化时取整程中点。
    /// </summary>
    /// <remarks>
    /// 与 <c>Engine.CrosswalkDistanceOf</c> 同一套"两点弦"几何：入口点 = <c>N − u_src·D + Right(u_src)·offset</c>。
    /// 岛心投影落在该弦上若得到 <c>(0, 全长)</c> 之外的退化值（岛放歪了 / 节点重合），退回<b>中点</b>，
    /// 保证前段 / 后段都有正长度（<see cref="Pedestrian"/> 的前置要求）。
    /// </remarks>
    private static double IslandPositionOf(CompiledNetwork network, Turn turn, double crossingDistance)
    {
        double half = crossingDistance * 0.5d;
        if (!network.TryGetIsland(turn.IslandId, out Island? island) || island is null)
        {
            return half;
        }

        Lane src = network.GetLane(turn.SrcLane);
        Lane dst = network.GetLane(turn.DstLane);
        Node node = network.GetNode(turn.Node);

        (double sux, double suy) = Direction(network, src);
        (double dux, double duy) = Direction(network, dst);

        const double chordDepth = 6.0d;
        const double laneWidth = 3.5d;

        double srcOffset = (src.LaneIndex + 0.5d) * laneWidth;
        double dstOffset = (dst.LaneIndex + 0.5d) * laneWidth;

        double entryX = node.X - (sux * chordDepth) + (suy * srcOffset);
        double entryY = node.Y - (suy * chordDepth) - (sux * srcOffset);
        double exitX = node.X + (dux * chordDepth) + (duy * dstOffset);
        double exitY = node.Y + (duy * chordDepth) - (dux * dstOffset);

        double dx = exitX - entryX;
        double dy = exitY - entryY;
        double len2 = (dx * dx) + (dy * dy);
        if (len2 <= 1e-12d)
        {
            return half;
        }

        // 岛心在弦上的投影参数 t ∈ [0,1]。
        double t = (((island.X - entryX) * dx) + ((island.Y - entryY) * dy)) / len2;
        if (double.IsNaN(t) || t <= 0.05d || t >= 0.95d)
        {
            return half;
        }

        return crossingDistance * t;
    }

    /// <summary>车道行驶方向单位向量（零长度车道退化为零向量）。</summary>
    private static (double X, double Y) Direction(CompiledNetwork network, Lane lane)
    {
        Node from = network.GetNode(lane.FromNode);
        Node to = network.GetNode(lane.ToNode);
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));
        return length <= 1e-12d ? (0d, 0d) : (dx / length, dy / length);
    }

    /// <summary>人行横道所属路口节点 id。</summary>
    public int NodeId => _nodeId;

    /// <summary>人行横道转向 id（<see cref="TurnType.Crosswalk"/>）。</summary>
    public int CrosswalkTurnId { get; }

    /// <summary>过街距离（米）。</summary>
    public double CrossingDistance { get; }

    /// <summary>过街最短绿灯（秒）= 过街距离 / 1.4（验收 c）。</summary>
    public double MinGreenTime => Pedestrian.MinGreenTimeFor(CrossingDistance);

    /// <summary>中央安全岛 id（<c>-1</c> = 一次走完，无岛；N6a2）。</summary>
    public int IslandId => _islandId;

    /// <summary>是否为本协调器配置了中央安全岛（两段式过街）。</summary>
    public bool IsTwoLeg => _islandId >= 0;

    /// <summary>岛上可同时容纳的人数（无岛时为 0）。</summary>
    public int IslandCapacity => _islandCapacity;

    /// <summary>岛在过街线上的位置（米，从起点路缘算起；无岛时为 -1）。</summary>
    public double IslandPosition => _islandPosition;

    /// <summary>岛上等待后段的人数。</summary>
    public int OnIslandCount => _onIsland.Count;

    /// <summary>岛上等待者（按到达顺序；N6a2）。</summary>
    public IReadOnlyList<Pedestrian> OnIsland => _onIsland;

    /// <summary>本协调器的仿真时刻（秒），随 <see cref="Step"/> 累加。</summary>
    public double Now { get; private set; }

    /// <summary>全部已到达行人（按到达顺序，含等待 / 过街中 / 已通过）。</summary>
    public IReadOnlyList<Pedestrian> Pedestrians => _all;

    /// <summary>等待区行人（按到达顺序）。</summary>
    public IReadOnlyList<Pedestrian> Waiting => _waiting;

    /// <summary>过街中行人（按到达顺序）。</summary>
    public IReadOnlyList<Pedestrian> Crossing => _crossing;

    /// <summary>已通过行人（按到达顺序）。</summary>
    public IReadOnlyList<Pedestrian> Crossed => _crossed;

    /// <summary>等待区人数。</summary>
    public int WaitingCount => _waiting.Count;

    /// <summary>过街中人数。</summary>
    public int CrossingCount => _crossing.Count;

    /// <summary>已通过人数。</summary>
    public int CrossedCount => _crossed.Count;

    /// <summary>当前是否有人正在过街（= 人行横道转向正被行人占用）。</summary>
    public bool IsOccupying => _crossing.Count > 0;

    /// <summary>
    /// 当前被行人占用的人行横道转向 id 集合（过街中非空，否则空）。
    /// 供机动车侧把它并入路口"占用转向集合"，用**同一套** T1.3 门控判定"机动车须让行过街行人"。
    /// </summary>
    public IReadOnlyList<int> OccupiedTurnIds => IsOccupying ? _occupying : EmptyTurns;

    /// <summary>
    /// 登记一名行人到达路缘等待区（等齐过街：先攒批，相位给足绿灯后集体通过）。
    /// </summary>
    /// <param name="pedestrian">行人（<see cref="Pedestrian.CrosswalkTurnId"/> 须与本协调器一致，且尚未登记）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="pedestrian"/> 为 null。</exception>
    /// <exception cref="ArgumentException">行人属于另一条人行横道，或已被登记过。</exception>
    public void Arrive(Pedestrian pedestrian)
    {
        ArgumentNullException.ThrowIfNull(pedestrian);

        if (pedestrian.CrosswalkTurnId != CrosswalkTurnId)
        {
            throw new ArgumentException(
                $"行人 #{pedestrian.Id} 的人行横道转向为 {pedestrian.CrosswalkTurnId}，与本协调器 {CrosswalkTurnId} 不一致。",
                nameof(pedestrian));
        }

        if (pedestrian.ArriveTime >= 0d)
        {
            throw new ArgumentException($"行人 #{pedestrian.Id} 已被登记（ArriveTime={pedestrian.ArriveTime}），不得重复到达。", nameof(pedestrian));
        }

        pedestrian.ArriveTime = Now;
        _all.Add(pedestrian);
        _waiting.Add(pedestrian);
    }

    /// <summary>
    /// 推进一个步长：先让过街中行人前进，再按四条判据决定是否放行等待区（集体通过）。
    /// </summary>
    /// <param name="dt">步长（秒，&gt; 0）。</param>
    /// <param name="signal">人行横道所属路口的信号状态机（提供相位 / 剩余绿灯 / 相位掩码）。</param>
    /// <param name="occupiedTurnIds">当前路口**已占用**的转向 id 集合（机动车 + 其他冲突流；可为 null = 视为空）。</param>
    /// <param name="destinationHasSpace">对岸（目标人行道）是否还有空位；<c>false</c> 时不放行新人。</param>
    /// <returns>本步被放行（开始过街）的人数；0 表示无放行。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="signal"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dt"/> 非正 / 非有限。</exception>
    public int Step(
        double dt,
        SignalStateMachine signal,
        IReadOnlyList<int>? occupiedTurnIds = null,
        bool destinationHasSpace = true,
        int? islandFreeSlots = null)
    {
        ArgumentNullException.ThrowIfNull(signal);

        if (double.IsNaN(dt) || double.IsInfinity(dt) || dt <= 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(dt), dt, "步长必须为有限正值。");
        }

        Now += dt;

        // 1) 过街中行人前进；到岛者移入岛上等待区（两段式），到岸者移入已通过集合。
        for (int i = _crossing.Count - 1; i >= 0; i--)
        {
            Pedestrian p = _crossing[i];
            if (p.Advance(dt, Now))
            {
                _crossing.RemoveAt(i);
                _crossed.Add(p);
            }
            else if (p.State == PedestrianState.OnIsland)
            {
                _crossing.RemoveAt(i);
                _onIsland.Add(p);
            }
        }

        int released = 0;

        // 2) 两段式：先把岛上的人放行走后段（后段是**另一条**转向，由调用方的信号决定）。
        if (IsTwoLeg)
        {
            released += ReleaseSecondLeg(dt, signal, occupiedTurnIds, destinationHasSpace);
        }

        if (_waiting.Count == 0)
        {
            return released;
        }

        // 3) 等待者累计等待时长（无论本步是否放行）。
        for (int i = 0; i < _waiting.Count; i++)
        {
            _waiting[i].WaitTime += dt;
        }

        // 4) 放行判据：绿 / 自身相位放行 / 绿灯尾余量 / 路口门控（冲突纳入 T1.3）。
        //    两段式时"绿灯尾余量"只要求走完**前段**（路缘 → 岛），后段另有自己的相位。
        bool green = signal.Phase is SignalPhaseState.Green or SignalPhaseState.GreenFlash;
        bool ownPhase = signal.PriorityOf(CrosswalkTurnId) != TurnPriority.Banned;
        double neededGreen = IsTwoLeg ? Pedestrian.MinGreenTimeFor(_islandPosition) : MinGreenTime;
        bool hasGreenTail = signal.PhaseRemaining >= neededGreen - Epsilon;

        if (!green || !ownPhase || !hasGreenTail)
        {
            return released;
        }

        // 岛容量：两段式的放行数受"岛上还有几个空位"限制（一次放行人数 = min(等待者, 空位)）。
        int canEnter = _waiting.Count;
        if (IsTwoLeg)
        {
            int free = islandFreeSlots ?? (_islandCapacity - _onIsland.Count - CountApproachingIsland());
            if (free < canEnter)
            {
                canEnter = free;
            }
        }

        if (canEnter <= 0)
        {
            return released;
        }

        if (!_gate.CanEnter(_nodeId, CrosswalkTurnId, signal.CurrentStage, occupiedTurnIds, destinationHasSpace))
        {
            return released;
        }

        // 5) 等齐过街：等待区**集体**通过（受岛容量限制时为"按到达顺序先到先走"）。
        for (int i = 0; i < canEnter; i++)
        {
            Pedestrian p = _waiting[i];
            p.StartCrossing(Now);
            _crossing.Add(p);
        }

        _waiting.RemoveRange(0, canEnter);
        return released + canEnter;
    }

    /// <summary>
    /// 放行岛上等待者走后段（岛 → 对岸）。
    /// </summary>
    /// <remarks>
    /// 后段与后段是<b>同一对人行横道转向里的第二个</b>（<see cref="Turn.Leg"/> = 1）。本协调器由整对
    /// 转向共同构造，因此后段判据与前段同构，只是换用后段转向 id 与后段最短绿灯。
    /// </remarks>
    private int ReleaseSecondLeg(
        double dt,
        SignalStateMachine signal,
        IReadOnlyList<int>? occupiedTurnIds,
        bool destinationHasSpace)
    {
        if (_onIsland.Count == 0)
        {
            return 0;
        }

        // 在岛上也是"等待"，计入等待时长（行人延误口径统一）。
        for (int i = 0; i < _onIsland.Count; i++)
        {
            _onIsland[i].WaitTime += dt;
        }

        if (_secondLegTurnId < 0)
        {
            return 0;   // 没有配置后段转向：人只能留在岛上（数据不完整，校验器会先报错）
        }

        bool green = signal.Phase is SignalPhaseState.Green or SignalPhaseState.GreenFlash;
        if (!green)
        {
            return 0;
        }

        if (signal.PriorityOf(_secondLegTurnId) == TurnPriority.Banned)
        {
            return 0;
        }

        double neededGreen = Pedestrian.MinGreenTimeFor(CrossingDistance - _islandPosition);
        if (signal.PhaseRemaining < neededGreen - Epsilon)
        {
            return 0;
        }

        if (!_gate.CanEnter(_nodeId, _secondLegTurnId, signal.CurrentStage, occupiedTurnIds, destinationHasSpace))
        {
            return 0;
        }

        // 同一时刻不允许"前段后段一起放"：若还有人在前段（路缘 → 岛）途中，后段等这一步再放。
        // 否则前段 / 后段两条 Crosswalk 相位相同（常见于"前后段同时亮"的错误配时）会让行人在岛上来回穿。
        if (CountApproachingIsland() > 0)
        {
            return 0;
        }

        int released = _onIsland.Count;
        for (int i = 0; i < _onIsland.Count; i++)
        {
            Pedestrian p = _onIsland[i];
            p.StartSecondLeg(Now);
            _crossing.Add(p);
        }

        _onIsland.Clear();
        return released;
    }

    /// <summary>已放行、正走在前段（路缘 → 岛）路上的人数 —— 他们还没到岛，但岛位要给他们留着。</summary>
    private int CountApproachingIsland()
    {
        int n = 0;
        for (int i = 0; i < _crossing.Count; i++)
        {
            if (!_crossing[i].IsOnSecondLeg)
            {
                n++;
            }
        }

        return n;
    }

    /// <summary>
    /// 未通过行人（等待 + 岛上 + 过街中）的只读快照，按到达顺序排列（供 <c>SimState.Pedestrians</c>，§4.4）。
    /// </summary>
    public IReadOnlyList<PedestrianSnapshot> Snapshots()
    {
        var list = new List<PedestrianSnapshot>(_waiting.Count + _onIsland.Count + _crossing.Count);
        for (int i = 0; i < _all.Count; i++)
        {
            Pedestrian p = _all[i];
            if (p.State != PedestrianState.Crossed)
            {
                list.Add(p.ToSnapshot());
            }
        }

        return list;
    }

    /// <summary>复位到 t=0 的空状态（清空全部行人、时刻归零）。</summary>
    public void Reset()
    {
        _all.Clear();
        _waiting.Clear();
        _onIsland.Clear();
        _crossing.Clear();
        _crossed.Clear();
        Now = 0d;
    }

    /// <inheritdoc />
    public override string ToString()
        => $"PedestrianCrossing(node={NodeId}, cw={CrosswalkTurnId}, wait={WaitingCount}, crossing={CrossingCount}, crossed={CrossedCount})";
}
