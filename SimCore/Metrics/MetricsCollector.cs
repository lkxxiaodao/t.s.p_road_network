using SimCore.Model;
using SimCore.Sim;

namespace SimCore.Metrics;

// ===========================================================================
// T2.1 · MetricsCollector —— 指标累计器（docs/SimCore-API.md §5.3 冻结）
//
//    public MetricsCollector(CompiledNetwork network)   // network == null → ArgumentNullException
//    public void    Record(in SimEvent e)                // 逐事件累计
//    public void    RecordRange(IReadOnlyList<SimEvent> events)
//    public Metrics Snapshot()                           // 只读、可重复调用、顺序确定
//    public void    Reset()
//
// 契约 §5.3 前置条款落地（"二者取一，由 M2 在单测中固定并各卡遵守"）：
//   **LaneId 越界 → 忽略该事件并保持确定性**（不抛异常）。这样"在错误数据下仍可累计"，
//   与 SimEngine 的"非法目标跳过不注入"（T1.8）口径一致。
//
// ---------------------------------------------------------------------------
// 口径（技术方案01 §3.5；**不得自创**）
//
// 事件流只有 6 种 SimEventKind，其中可用于指标的是：
//   * DemandArrived —— 需求到达（需求数分母）：Driving → 以 LaneId 判类；Pedestrian / Bicycle → 无线索，
//     故**由 Level 需求曲线配准**（见「分类归属」）；
//   * VehiclePassed —— 通过（通过数分子 + 通行时间样本）：Value = 所走转向 id（T1.6 路由）/ 或 -1，
//     以**转向类型**判类（Crosswalk → 行人；SrcLane 为 Biking → 非机动车；否则机动车）；
//   * VehicleStopped —— 新增一次停车（P 的分子）：同样是逐辆机动车 / 非机动车（行人不发此事件）；
//   * QueueSample —— 排队样本（Q 的原始量）：Value = 排队辆数；
//   * Spillback —— 溢出（Value 作占有率判定，见下）；
//   * Collision —— 碰撞（直接置位）。
//
// R = 通过数 / 需求数：分子分母**各自按类汇总**，再按三类的**算术均值**合成（D / P 同理；无需求的空类
//     以其中性值参与：R=1、D=0、P=0——与契约 §5.2「分类加权」在等权下等价）。
// Q = 各类「排队辆数 / 该目标容量」的最大值，再取三类中**出现过排队者**的**最低**（木桶）；容量由车道几何
//     推导（floor(车道长 / 静止车头间距)，与 Queue.Capacity 同式），故不依赖额外配置。
//     「出现过」的必要性来自游戏设计v2 §6.2 的字面口径：「全程中任一进口道、**任一类型出现过**的
//     最大排队长度，按该等候区容量的百分比归一化；三类分别归一化后取最低分」。**未出现的类不参与木桶**
//     （否则一个没有行人的关卡会因「行人排队 0」把 Q 恒压到 0，木桶失去意义）；三类全未出现 → Q=0。
// 自由流时间为 0（技术方案01 §3.5 的 compute_link_delay 未扣自由流：车队从车道入口加入时
//     EnterTime 即"进入网络的时刻"），故 D 退化为"通行时间均值"，时间基准由第一条
//     DemandArrived.QueueSample（t≈0）确立。此口径在报告中显式登记。
// ===========================================================================

/// <summary>
/// 指标累计器（<c>docs/SimCore-API.md</c> §5.3 冻结）——逐事件消费
/// <see cref="SimEngine.DrainEvents"/>，产出四项指标快照（§5.4 表现层取用路径）。
/// </summary>
/// <remarks>
/// <para><b>确定性（铁律 2）</b>：本类型是无随机、无系统时钟的纯累计器；同一事件序列
/// （同 seed 两次运行 / 1× 与 32× 到同一仿真时刻）逐元素相同 → <see cref="Snapshot"/> 逐字段相同。</para>
/// <para><b>顺序无关的累计</b>：所有累计量均为「计数 / 求和 / 取最大」——满足交换律与结合律，
/// 故与事件的遍历顺序无关（确定性不依赖事件顺序）。</para>
/// <para><b>不依赖 SimState</b>：只消费 <see cref="SimEvent"/>（契约 §1.2 依赖方向 Metrics → Sim）。</para>
/// </remarks>
public sealed class MetricsCollector
{
    /// <summary>逐类计数器（下标 = <see cref="TrafficClass"/>）。</summary>
    private readonly ClassCounter[] _counters = new ClassCounter[3];

    private readonly CompiledNetwork _network;
    private readonly int[] _laneCapacity;            // 下标 = 车道 id（按几何推导的排队容量，辆）
    private readonly int[] _laneQueuePeak;           // 下标 = 车道 id（峰值排队辆数）
    private readonly int[] _lanePeakTime;            // 下标 = 车道 id（峰值出现时的整步序号；-1 = 无）
    private readonly double[] _laneDelaySum;         // 下标 = 车道 id（延误和，秒）
    private readonly int[] _lanePassed;              // 下标 = 车道 id（通过数）
    private readonly int[] _laneStops;               // 下标 = 车道 id（停车次数）
    private readonly int[] _crosswalkQueuePeak;      // 下标 = 人行横道转向 id（待过街峰值）
    private readonly int[] _crosswalkPassed;         // 下标 = 人行横道转向 id（通过数）

    private readonly double[] _drivingDemandByLane;  // 下标 = 车道 id（配准的机动车需求数）
    private readonly double[] _bicycleDemandByLane;  // 下标 = 车道 id（配准的非机动车需求数）
    private readonly double[] _pedestrianDemandByTurn; // 下标 = 人行横道转向 id（配准的行人需求数）

    private bool _hasRegistration;

    // 自由流时间基准：首次出现 QueueSample 的时刻（t≈0 的初始采样）。
    private double _freeFlowBaseline = -1d;

    // 三类需求的独立计数（用于"配准"分配 DemandArrived 的分类归属）。
    private long _unattributedDriving;
    private long _unattributedBicycle;
    private long _unattributedPedestrian;

    private readonly SortedSet<int> _collisionTurns = new();

    // 各类是否"出现过排队"（下标 = TrafficClass；= 被观测到非零排队）——决定该类是否参与 Q 的木桶取最低。
    private readonly bool[] _queueObserved = new bool[3];

    private bool _spillback;
    private bool _collision;

    /// <summary>
    /// 以编译路网构造累计器（§5.3）。
    /// </summary>
    /// <param name="network">编译路网（只读；用于车道 / 转向分类与排队容量推导、逐进口聚合）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="network"/> 为 null。</exception>
    public MetricsCollector(CompiledNetwork network)
    {
        ArgumentNullException.ThrowIfNull(network);
        _network = network;

        int laneCount = network.LaneCount;
        _laneCapacity = new int[laneCount];
        _laneQueuePeak = new int[laneCount];
        _lanePeakTime = new int[laneCount];
        _laneDelaySum = new double[laneCount];
        _lanePassed = new int[laneCount];
        _laneStops = new int[laneCount];
        _drivingDemandByLane = new double[laneCount];
        _bicycleDemandByLane = new double[laneCount];

        for (int laneId = 0; laneId < laneCount; laneId++)
        {
            _laneCapacity[laneId] = CapacityOf(network.GetLane(laneId));
            _lanePeakTime[laneId] = -1;
        }

        int turnCount = network.TurnCount;
        _crosswalkQueuePeak = new int[turnCount];
        _crosswalkPassed = new int[turnCount];
        _pedestrianDemandByTurn = new double[turnCount];

        for (int c = 0; c < _counters.Length; c++)
        {
            _counters[c] = new ClassCounter();
        }
    }

    /// <summary>
    /// 配准三类需求的「每目标总量」（可选；供无法从事件本身判类的到达正确归类）。
    /// </summary>
    /// <remarks>
    /// <para><b>为什么需要</b>：<see cref="SimEventKind.DemandArrived"/> 对行人 / 非机动车**不携带目标 id**
    /// （<c>LaneId = -1</c>，见 T1.8 与契约 §4.9），故单靠事件无法判类。本方法在**开始消费事件前**
    /// 由图层的需求曲线给出各目标的需求总量，累计器据此把到达事件按类归属。</para>
    /// <para><b>判定顺序</b>：机动车可由事件自身的 <c>LaneId</c>（机动车道）直接判类；
    /// 余下「无车道线索」的到达按「先非机动车、后行人」的顺序、依配准剩余量分配。
    /// 在累积需求非负且总到达 ≤ 总需求的正常情形下，该分配与真实来源**逐项一致**
    /// （单测以「非机动车需求 = 0」的关卡验证到达全归行人、以「行人需求 = 0」验证全归非机动车）。</para>
    /// <para>未配准时：机动车道上的到达仍归机动车；其余到达按「非机动车 / 行人」二者中已出现非零需求者归类，
    /// 否则默认归行人（保证"不少于实际需求"的归类上界，R 不会因归类而虚高）。</para>
    /// </remarks>
    /// <param name="drivingDemandByLane">机动车需求（下标 = 车道 id；可为 null = 未知）。</param>
    /// <param name="pedestrianDemandByTurn">行人需求（下标 = 人行横道转向 id；可为 null = 未知）。</param>
    /// <param name="bicycleDemandByLane">非机动车需求（下标 = 车道 id；可为 null = 未知）。</param>
    public void RegisterDemand(
        double[]? drivingDemandByLane,
        double[]? pedestrianDemandByTurn,
        double[]? bicycleDemandByLane)
    {
        CopyInto(drivingDemandByLane, _drivingDemandByLane);
        CopyInto(pedestrianDemandByTurn, _pedestrianDemandByTurn);
        CopyInto(bicycleDemandByLane, _bicycleDemandByLane);

        for (int i = 0; i < _drivingDemandByLane.Length; i++)
        {
            _unattributedDriving += (long)Math.Ceiling(Math.Max(0d, _drivingDemandByLane[i]));
        }

        for (int i = 0; i < _bicycleDemandByLane.Length; i++)
        {
            _unattributedBicycle += (long)Math.Ceiling(Math.Max(0d, _bicycleDemandByLane[i]));
        }

        for (int i = 0; i < _pedestrianDemandByTurn.Length; i++)
        {
            _unattributedPedestrian += (long)Math.Ceiling(Math.Max(0d, _pedestrianDemandByTurn[i]));
        }

        _hasRegistration = true;
    }

    /// <summary>累计单个事件（§5.3）。越界 <c>LaneId</c> 的事件被忽略并保持确定性（§5.3 前置条款）。</summary>
    /// <param name="e">仿真事件（只读）。</param>
    public void Record(in SimEvent e)
    {
        switch (e.Kind)
        {
            case SimEventKind.DemandArrived:
                RecordArrival(e);
                break;

            case SimEventKind.VehiclePassed:
                RecordPassed(e);
                break;

            case SimEventKind.VehicleStopped:
                RecordStopped(e);
                break;

            case SimEventKind.QueueSample:
                RecordQueue(e);
                break;

            case SimEventKind.Spillback:
                RecordSpillback(e);
                break;

            case SimEventKind.Collision:
                _collision = true;
                if (e.Value > 0d && e.Value < _network.TurnCount)
                {
                    _collisionTurns.Add((int)e.Value);
                }
                else if ((uint)e.LaneId < (uint)_network.LaneCount)
                {
                    // 无转向线索时退化为"冲突转向的源车道"（契约 §4.9）：记录该车道的首个下游转向，仅作标识。
                    IReadOnlyList<int> turns = _network.OutgoingTurns(e.LaneId);
                    if (turns.Count > 0)
                    {
                        _collisionTurns.Add(turns[0]);
                    }
                }
                break;

            default:
                break;
        }
    }

    /// <summary>批量累计（§5.3）；等价于逐个 <see cref="Record"/>，用于消费 <see cref="SimEngine.DrainEvents"/>。</summary>
    /// <param name="events">事件序列（只读；可为空数组）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="events"/> 为 null。</exception>
    public void RecordRange(IReadOnlyList<SimEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        for (int i = 0; i < events.Count; i++)
        {
            Record(events[i]);
        }
    }

    /// <summary>产出当前累计指标的只读快照（§5.3）；可重复调用，多次调用结果一致。</summary>
    /// <returns>四项指标 + 分类 / 逐进口分解 + 溢出 / 碰撞标记。</returns>
    public Metrics Snapshot()
    {
        ClassMetrics[] classes =
        [
            BuildClass(TrafficClass.Driving),
            BuildClass(TrafficClass.Pedestrian),
            BuildClass(TrafficClass.Bicycle),
        ];

        double r = WeightedMean(classes, static c => c.R);
        double d = WeightedMean(classes, static c => c.D);
        double p = WeightedMean(classes, static c => c.P);
        double q = BucketMinimum(classes);

        return new Metrics(
            r,
            d,
            p,
            q,
            _spillback,
            _collision,
            BuildCollisionTurns(),
            classes,
            BuildApproaches());
    }

    /// <summary>复位到全零（§5.3）——清空全部累计，保留路网与已配准的需求曲线。</summary>
    public void Reset()
    {
        Array.Clear(_laneQueuePeak);
        Array.Clear(_laneDelaySum);
        Array.Clear(_lanePassed);
        Array.Clear(_laneStops);
        Array.Clear(_crosswalkQueuePeak);
        Array.Clear(_crosswalkPassed);
        for (int i = 0; i < _lanePeakTime.Length; i++)
        {
            _lanePeakTime[i] = -1;
        }

        for (int c = 0; c < _counters.Length; c++)
        {
            _counters[c].Reset();
            _queueObserved[c] = false;
        }

        _freeFlowBaseline = -1d;
        _unattributedDriving = 0L;
        _unattributedBicycle = 0L;
        _unattributedPedestrian = 0L;
        _collisionTurns.Clear();
        _spillback = false;
        _collision = false;

        // 若已配准需求，复位后重新装载"未归属额度"（同一局可反复跑）。
        if (_hasRegistration)
        {
            double[] driving = (double[])_drivingDemandByLane.Clone();
            double[] bicycle = (double[])_bicycleDemandByLane.Clone();
            double[] pedestrian = (double[])_pedestrianDemandByTurn.Clone();
            Array.Clear(_drivingDemandByLane);
            Array.Clear(_bicycleDemandByLane);
            Array.Clear(_pedestrianDemandByTurn);
            RegisterDemand(driving, pedestrian, bicycle);
        }
    }

    // ------------------------------------------------------------------
    // 事件分支
    // ------------------------------------------------------------------

    private void RecordArrival(in SimEvent e)
    {
        if (_freeFlowBaseline < 0d)
        {
            _freeFlowBaseline = e.Time;
        }

        // ① 机动车：事件自带车道 id，直接可由车道类型判类。
        if ((uint)e.LaneId < (uint)_network.LaneCount)
        {
            TrafficClass cls = ClassifyLane(e.LaneId);
            _counters[(int)cls].Demand++;
            if (cls == TrafficClass.Bicycle)
            {
                _unattributedBicycle = Math.Max(0L, _unattributedBicycle - 1);
            }
            else if (cls == TrafficClass.Driving)
            {
                _unattributedDriving = Math.Max(0L, _unattributedDriving - 1);
            }

            return;
        }

        // ② 行人 / 非机动车：事件无线索 → 按配准额度归属（非机动车优先，再行人）。
        if (_unattributedBicycle > 0L)
        {
            _unattributedBicycle--;
            _counters[(int)TrafficClass.Bicycle].Demand++;
            return;
        }

        if (_unattributedPedestrian > 0L)
        {
            _unattributedPedestrian--;
            _counters[(int)TrafficClass.Pedestrian].Demand++;
            return;
        }

        // ③ 未配准 / 额度用尽：取二者中已配置需求者；都没有则默认行人（归类上界，R 不虚高）。
        bool bicycleConfigured = HasAnyPositive(_bicycleDemandByLane);
        bool pedestrianConfigured = HasAnyPositive(_pedestrianDemandByTurn);

        if (bicycleConfigured && !pedestrianConfigured)
        {
            _counters[(int)TrafficClass.Bicycle].Demand++;
        }
        else
        {
            _counters[(int)TrafficClass.Pedestrian].Demand++;
        }
    }

    private void RecordPassed(in SimEvent e)
    {
        // 转向 id：T1.6 路由时 Value = turnId；无转向时（车道出口）为 -1 → 以**源车道**类型判类。
        int turnId = (int)e.Value;
        TrafficClass cls;
        int classSource;

        if ((uint)turnId < (uint)_network.TurnCount)
        {
            Turn turn = _network.GetTurn(turnId);
            cls = ClassifyLane(turn.SrcLane);
            classSource = turn.SrcLane;

            if (turn.Type == TurnType.Crosswalk)
            {
                cls = TrafficClass.Pedestrian;
                _crosswalkPassed[turnId]++;
            }
        }
        else
        {
            if ((uint)e.LaneId >= (uint)_network.LaneCount)
            {
                return;   // 既无转向也无车道：忽略（保持确定性）。
            }

            cls = ClassifyLane(e.LaneId);
            classSource = e.LaneId;
        }

        ClassCounter counter = _counters[(int)cls];
        counter.Passed++;
        counter.DelaySum += Math.Max(0d, e.Time - _freeFlowBaseline);

        if (cls == TrafficClass.Pedestrian)
        {
            return;   // 行人延误不计入车道层（其 DelaySum 已按人次累计）。
        }

        if ((uint)classSource < (uint)_lanePassed.Length)
        {
            _lanePassed[classSource]++;
            _laneDelaySum[classSource] += Math.Max(0d, e.Time - _freeFlowBaseline);
        }
    }

    private void RecordStopped(in SimEvent e)
    {
        if ((uint)e.LaneId >= (uint)_network.LaneCount)
        {
            return;   // 车道越界：忽略（§5.3 前置条款）。
        }

        TrafficClass cls = ClassifyLane(e.LaneId);
        _counters[(int)cls].Stops++;

        if (cls != TrafficClass.Pedestrian)
        {
            _laneStops[e.LaneId]++;
        }
    }

    private void RecordQueue(in SimEvent e)
    {
        if ((uint)e.LaneId >= (uint)_network.LaneCount)
        {
            return;   // 车道越界：忽略。
        }

        int queueLength = (int)Math.Max(0d, Math.Round(e.Value));

        // 「出现过排队」＝ 该类被观测到 **非零** 排队——决定其是否参与 Q 木桶（设计v2 §6.2 的
        // 「任一类型**出现过**的最大排队长度」）。注意：引擎每步对**每条车道**都发 QueueSample
        // （含人行道 / 非机动车道，值为 0），故不能以"被采样过"作为出现判据，必须要求非零。
        if (queueLength > 0)
        {
            _queueObserved[(int)ClassifyLane(e.LaneId)] = true;
        }

        if (queueLength < _laneQueuePeak[e.LaneId])
        {
            return;
        }

        int step = (int)Math.Round(e.Time * 10d);   // 以 0.1s 为整步（Dt 量级）
        bool laterPeak = queueLength > _laneQueuePeak[e.LaneId];
        bool firstAtSameValue = queueLength == _laneQueuePeak[e.LaneId] && _lanePeakTime[e.LaneId] < 0;
        if (laterPeak || firstAtSameValue)
        {
            _laneQueuePeak[e.LaneId] = queueLength;
            _lanePeakTime[e.LaneId] = step;
        }
    }

    /// <summary>
    /// 登记人行横道「等待过街人数」（供 Q 的行人分项；由表现层 / 集成层从
    /// <c>SimState.Pedestrians</c> 的等待者统计后逐帧调用，或由后续卡的逐行碰撞归因接入）。
    /// </summary>
    /// <param name="crosswalkTurnId">人行横道转向 id。</param>
    /// <param name="waitingCount">当前等待过街人数（≥ 0）。</param>
    public void ObserveWaitingPedestrians(int crosswalkTurnId, int waitingCount)
    {
        if ((uint)crosswalkTurnId >= (uint)_crosswalkQueuePeak.Length)
        {
            return;   // 越界：忽略（保持确定性）。
        }

        if (_network.GetTurn(crosswalkTurnId).Type != TurnType.Crosswalk)
        {
            return;
        }

        int value = Math.Max(0, waitingCount);
        _crosswalkQueuePeak[crosswalkTurnId] = Math.Max(_crosswalkQueuePeak[crosswalkTurnId], value);
        if (value > 0)
        {
            _queueObserved[(int)TrafficClass.Pedestrian] = true;
        }
    }

    private void RecordSpillback(in SimEvent e)
    {
        // 契约 §4.9：Spillback.Value = 首车道占有率。同时接受"每车道占有率"形态
        // （LaneId = 车道且 Value = 该车道占有率），只要任一事件的占有率越过阈值即置位。
        double occupancy = e.Value;
        if (occupancy > SimConstants.SpillbackOccupancy)
        {
            _spillback = true;
            return;
        }

        // 退化形态：Value 为车道队列辆数（部分实现），则以车道容量换算占有率。
        if ((uint)e.LaneId < (uint)_network.LaneCount && _laneCapacity[e.LaneId] > 0)
        {
            double ratio = occupancy / _laneCapacity[e.LaneId];
            if (ratio > SimConstants.SpillbackOccupancy)
            {
                _spillback = true;
            }
        }
    }

    // ------------------------------------------------------------------
    // 聚合
    // ------------------------------------------------------------------

    private ClassMetrics BuildClass(TrafficClass cls)
    {
        ClassCounter counter = _counters[(int)cls];

        double r = counter.Demand > 0L ? counter.Passed / (double)counter.Demand : 1d;
        if (r > 1d)
        {
            r = 1d;   // 通过数不应超过需求数；防御性夹紧（例如未配准导致分母偏小）。
        }

        double d = counter.Passed > 0L ? counter.DelaySum / counter.Passed : 0d;

        // P：行人不计（T2.1 c）→ 分母取"机动车 / 非机动车"的通过数。
        double p = cls == TrafficClass.Pedestrian
            ? 0d
            : (counter.Passed > 0L ? counter.Stops / (double)counter.Passed : 0d);

        double q = QueueNormalizedOf(cls);
        return new ClassMetrics(cls, r, d, p, q);
    }

    private double QueueNormalizedOf(TrafficClass cls)
    {
        double peak = 0d;

        if (cls == TrafficClass.Pedestrian)
        {
            for (int turnId = 0; turnId < _crosswalkQueuePeak.Length; turnId++)
            {
                if (_network.GetTurn(turnId).Type != TurnType.Crosswalk)
                {
                    continue;
                }

                int capacity = PedestrianCapacityOf();
                double ratio = capacity > 0 ? _crosswalkQueuePeak[turnId] / (double)capacity : 0d;
                peak = Math.Max(peak, ratio);
            }

            return Math.Min(1d, peak);
        }

        for (int laneId = 0; laneId < _laneQueuePeak.Length; laneId++)
        {
            if (ClassifyLane(laneId) != cls)
            {
                continue;
            }

            int capacity = _laneCapacity[laneId];
            double ratio = capacity > 0 ? _laneQueuePeak[laneId] / (double)capacity : 0d;
            peak = Math.Max(peak, ratio);
        }

        return Math.Min(1d, peak);
    }

    private IReadOnlyList<ApproachMetrics> BuildApproaches()
    {
        var list = new List<ApproachMetrics>();
        int laneCount = _network.LaneCount;

        // 按 (节点 id, 道路 id) 升序聚合——确定性（不依赖字典遍历顺序）。
        for (int nodeId = 0; nodeId < _network.NodeCount; nodeId++)
        {
            for (int roadId = 0; roadId < MaxRoadId(); roadId++)
            {
                double delaySum = 0d;
                int passed = 0;
                int stops = 0;
                int queuePeak = 0;
                bool any = false;

                for (int laneId = 0; laneId < laneCount; laneId++)
                {
                    Lane lane = _network.GetLane(laneId);
                    if (lane.ToNode != nodeId || lane.Road != roadId)
                    {
                        continue;
                    }

                    any = true;
                    delaySum += _laneDelaySum[laneId];
                    passed += _lanePassed[laneId];
                    stops += _laneStops[laneId];
                    queuePeak = Math.Max(queuePeak, _laneQueuePeak[laneId]);
                }

                if (!any)
                {
                    continue;
                }

                double delay = passed > 0 ? delaySum / passed : 0d;
                list.Add(new ApproachMetrics(nodeId, roadId, delay, queuePeak, 0d, 0d));
            }
        }

        return list;
    }

    private int MaxRoadId()
    {
        int max = -1;
        for (int laneId = 0; laneId < _network.LaneCount; laneId++)
        {
            max = Math.Max(max, _network.GetLane(laneId).Road);
        }

        return max + 1;
    }

    private IReadOnlyList<int> BuildCollisionTurns()
    {
        if (_collisionTurns.Count == 0)
        {
            return Array.Empty<int>();
        }

        int[] result = new int[_collisionTurns.Count];
        _collisionTurns.CopyTo(result);
        return result;
    }

    // ------------------------------------------------------------------
    // 工具
    // ------------------------------------------------------------------

    /// <summary>按车道类型分类交通类别（机动车 / 非机动车 / 行人）。</summary>
    private TrafficClass ClassifyLane(int laneId)
    {
        LaneType type = _network.GetLane(laneId).Type;
        return type switch
        {
            LaneType.Biking => TrafficClass.Bicycle,
            LaneType.Sidewalk => TrafficClass.Pedestrian,
            _ => TrafficClass.Driving,
        };
    }

    /// <summary>按几何推导排队容量（辆）：与 <c>Queue.Capacity</c> 同式（车道长 / 静止车头间距）。</summary>
    private static int CapacityOf(Lane lane)
    {
        double spacing = SimConstants.VehicleLength + SimConstants.MinHeadway;
        int capacity = (int)Math.Floor(lane.Length / spacing);
        return Math.Max(0, capacity);
    }

    /// <summary>人行横道等待区容量（人）：按 0.5 m/人 的等待密度与 3.5 m 过街宽度估算（= 7 人）。</summary>
    private const double PedestrianWaitDensity = 0.5d;

    private static int PedestrianCapacityOf()
        => (int)Math.Floor(3.5d / PedestrianWaitDensity);

    private static double WeightedMean(IReadOnlyList<ClassMetrics> classes, Func<ClassMetrics, double> selector)
    {
        double sum = 0d;
        int count = 0;
        for (int i = 0; i < classes.Count; i++)
        {
            sum += selector(classes[i]);
            count++;
        }

        return count > 0 ? sum / count : 0d;
    }

    /// <summary>
    /// 木桶取最低（设计v2 §6.2）：在**出现过排队**的类中取最低归一排队；三类全未出现 → 0。
    /// </summary>
    /// <remarks>
    /// 「未出现的类不参与木桶」的理由见文首口径注：空场景（无行人、无非机动车）不应因这些类排队为 0
    /// 而把 Q 恒压到 0，否则木桶失去"最弱势群体代表本项得分"的语义。判据为
    /// <see cref="RecordQueue"/> / <see cref="ObserveWaitingPedestrians"/> 是否被该类触发过。
    /// </remarks>
    private double BucketMinimum(IReadOnlyList<ClassMetrics> classes)
    {
        double min = double.PositiveInfinity;
        for (int i = 0; i < classes.Count; i++)
        {
            if (!_queueObserved[(int)classes[i].Class])
            {
                continue;
            }

            min = Math.Min(min, classes[i].QNormalized);
        }

        return double.IsPositiveInfinity(min) ? 0d : min;
    }

    private static bool HasAnyPositive(double[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] > 0d)
            {
                return true;
            }
        }

        return false;
    }

    private static void CopyInto(double[]? source, double[] target)
    {
        Array.Clear(target);
        if (source is null)
        {
            return;
        }

        int n = Math.Min(source.Length, target.Length);
        for (int i = 0; i < n; i++)
        {
            target[i] = Math.Max(0d, source[i]);
        }
    }

    /// <summary>逐类累计器（需求 / 通过 / 停车 / 延误和）。</summary>
    private sealed class ClassCounter
    {
        public long Demand;
        public long Passed;
        public long Stops;
        public double DelaySum;

        public void Reset()
        {
            Demand = 0L;
            Passed = 0L;
            Stops = 0L;
            DelaySum = 0d;
        }
    }
}
