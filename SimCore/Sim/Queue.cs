using SimCore.Model;

namespace SimCore.Sim;

/// <summary>
/// 车道快照（<c>docs/SimCore-API.md</c> §4.4 冻结；表现层只读）。
/// </summary>
/// <param name="LaneId">车道 id。</param>
/// <param name="QueueLength">该车道排队车辆数（速度 ≈ 0）。</param>
/// <param name="Occupancy">该车道占有率 ∈ [0,1]；&gt; <see cref="SimConstants.SpillbackOccupancy"/> 即溢出。</param>
public readonly record struct LaneSnapshot(int LaneId, int QueueLength, double Occupancy);

/// <summary>
/// 单车道队列（T1.1；借 A/B Street <c>mechanics/queue.rs</c>「每车道一个队列，按位置从前到后」）。
/// </summary>
/// <remarks>
/// <para>职责：持有一条车道上全部车辆的**有序队列**（按车头位置降序），每步推进时
/// （1）按 <see cref="CarFollowing"/> 做跟驰约束与外层硬钳制，（2）在停止线处按**饱和流率 + 起步损失爬升**
/// 放行队首车辆（技术方案01 §3.2b）。</para>
/// <para>信号口径：本类型**不实现**信号状态机（属 T1.2），只消费一个布尔 <c>open</c> =
/// 「当前步停止线是否允许放行」。绿灯起点由 <c>false → true</c> 的跃迁自动识别，用于重置起步爬升。</para>
/// <para>确定性（铁律 2）：队列顺序、放行顺序、事件顺序全部固定；不使用任何系统随机数；
/// 浮点放行额度带 1e-9 容差以避免抖动。</para>
/// <para>与冻结接口的对应：产出 <see cref="LaneSnapshot"/>（§4.4），并为 T1.6 组装
/// <c>SimEventKind.VehicleStopped</c>（<see cref="DrainStopped"/>）与
/// <c>SimEventKind.QueueSample</c>（<see cref="QueueLength"/>）提供数据源。</para>
/// </remarks>
internal sealed class Queue
{
    /// <summary>放行额度判定容差（避免浮点抖动导致「差一步」）。</summary>
    private const double CreditEpsilon = 1e-9d;

    /// <summary>「已抵停止线」的判定容差（米）。</summary>
    private const double StopLineTolerance = 1e-6d;

    /// <summary>间距判定容差（米）。</summary>
    private const double SpacingEpsilon = 1e-9d;

    private readonly List<Vehicle> _vehicles = new();   // 前 → 后（车头位置降序）
    private readonly List<Vehicle> _departed = new();    // 自上次 Drain 以来驶离车道者
    private readonly List<Vehicle> _stopped = new();     // 自上次 Drain 以来新发生的停车事件

    /// <summary>构造一条车道的队列。</summary>
    /// <param name="lane">车道几何（长度 / 限速 / id）；只读，不被修改。</param>
    /// <param name="expectedJamSpacing">
    /// 该车道车型构成的**期望静止车头间距**（米，见 <see cref="VehicleMixRules.ExpectedJamSpacing"/>）。
    /// ≤ 0 时退回纯小汽车口径（<c>4.5 + 1.5 = 6.0</c>），与 T4.3 之前逐位一致。
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="lane"/> 为 null。</exception>
    public Queue(Lane lane, double expectedJamSpacing = 0d)
    {
        ArgumentNullException.ThrowIfNull(lane);
        Lane = lane;
        double spacing = expectedJamSpacing > 0d
            ? expectedJamSpacing
            : CarFollowing.JamSpacingOf(VehicleKind.Car);
        Capacity = (int)Math.Floor(lane.Length / spacing);
    }

    /// <summary>本队列对应的车道（只读引用）。</summary>
    public Lane Lane { get; }

    /// <summary>车道 id（= <see cref="Lane"/>.<see cref="Model.Lane.Id"/>）。</summary>
    public int LaneId => Lane.Id;

    /// <summary>按静止车头间距估算的车道容量（辆），供参考与溢出判据。</summary>
    public int Capacity { get; }

    /// <summary>队列内车辆数。</summary>
    public int Count => _vehicles.Count;

    /// <summary>队列内车辆（前 → 后；只读视图）。</summary>
    public IReadOnlyList<Vehicle> Vehicles => _vehicles;

    /// <summary>队首（最靠近停止线者）；空队列为 null。</summary>
    public Vehicle? Head => _vehicles.Count > 0 ? _vehicles[0] : null;

    /// <summary>队尾（最靠近入口者）；空队列为 null。</summary>
    public Vehicle? Tail => _vehicles.Count > 0 ? _vehicles[^1] : null;

    /// <summary>排队车辆数（速度 ≈ 0 的车数）＝ <see cref="LaneSnapshot.QueueLength"/>。</summary>
    public int QueueLength { get; private set; }

    /// <summary>车道占有率 ∈ [0,1] ＝ <see cref="LaneSnapshot.Occupancy"/>。</summary>
    public double Occupancy { get; private set; }

    /// <summary>本队列的仿真时刻（秒），随 <see cref="Step"/> 累加（与 SimEngine 同步步长即可对齐）。</summary>
    public double Now { get; private set; }

    /// <summary>停止线当前是否放行（上一步的 <c>open</c>）。</summary>
    public bool IsOpen { get; private set; }

    /// <summary>本绿灯周期已持续时长（秒）；红灯期间为 0。</summary>
    public double OpenElapsed { get; private set; }

    /// <summary>当前累积的放行额度（辆，∈ [0,1]）；达到 1 且队首抵停止线即放行一辆。</summary>
    public double DischargeCredit { get; private set; }

    /// <summary>
    /// 起步爬升因子 ∈ [0,1]：绿灯开始后 <see cref="SimConstants.StartUpLostTime"/> 秒内由 0 线性升至 1
    /// （技术方案01 §3.2b：<c>min(1, 绿灯已持续时长 / lost_time)</c>）。
    /// </summary>
    public static double StartUpCapacityFactor(double openElapsed)
    {
        if (openElapsed <= 0d)
        {
            return 0d;
        }

        double lost = SimConstants.StartUpLostTime;
        return openElapsed >= lost ? 1d : openElapsed / lost;
    }

    /// <summary>
    /// 在车道**入口**（车头位置 = 0）放入一辆车。若入口无足够净距（上一辆尚未让出）则返回 false（溢出）。
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="vehicle"/> 为 null。</exception>
    public bool TryEnqueue(Vehicle vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        double limit = Lane.Length;
        if (_vehicles.Count > 0)
        {
            Vehicle tail = _vehicles[^1];
            if (tail.Position - tail.Length < SimConstants.MinHeadway - SpacingEpsilon)
            {
                return false;   // 入口被占满：排队溢出，禁止驶入
            }

            limit = CarFollowing.FollowingLimit(tail.Position, tail.Length);
        }

        Place(vehicle, 0d,
            Math.Min(Lane.SpeedLimit * CarFollowing.SpeedFactorOf(vehicle.Kind), CarFollowing.BrakingSpeed(limit)),
            _vehicles.Count);
        return true;
    }

    /// <summary>
    /// 以**显式位置**放入一辆车（初始条件 / 状态恢复；用于铺设初始排队或重放）。
    /// 位置须落在 <c>[0, Lane.Length]</c>，且不破坏前后车的跟驰净距，否则返回 false。
    /// </summary>
    /// <param name="vehicle">车辆。</param>
    /// <param name="position">车头距车道起点的距离（米）。</param>
    /// <param name="speed">初始速度（米/秒，默认 0）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="vehicle"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="position"/> 越界或 <paramref name="speed"/> 为负。</exception>
    public bool TryPlace(Vehicle vehicle, double position, double speed = 0d)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        if (double.IsNaN(position) || position < 0d || position > Lane.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(position), position, "车头位置须落在 [0, 车道长度] 内。");
        }

        if (double.IsNaN(speed) || speed < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(speed), speed, "速度不得为负。");
        }

        int index = 0;
        while (index < _vehicles.Count && _vehicles[index].Position > position)
        {
            index++;
        }

        if (index > 0)
        {
            Vehicle ahead = _vehicles[index - 1];
            if (position > CarFollowing.FollowingLimit(ahead.Position, ahead.Length) + SpacingEpsilon)
            {
                return false;   // 与前车过近
            }
        }

        if (index < _vehicles.Count)
        {
            Vehicle behind = _vehicles[index];
            if (behind.Position > CarFollowing.FollowingLimit(position, vehicle.Length) + SpacingEpsilon)
            {
                return false;   // 与后车过近
            }
        }

        Place(vehicle, position, speed, index);
        return true;
    }

    /// <summary>
    /// 推进一个固定步长。
    /// </summary>
    /// <param name="dt">步长（秒，须 &gt; 0）。</param>
    /// <param name="open">本步停止线是否放行（由 T1.2/T1.3 提供；本类型只消费，不判定信号）。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="dt"/> 非正或非有限。</exception>
    public void Step(double dt, bool open)
    {
        if (dt <= 0d || double.IsNaN(dt) || double.IsInfinity(dt))
        {
            throw new ArgumentOutOfRangeException(nameof(dt), dt, "步长必须为正值。");
        }

        Now += dt;

        // 1) 绿灯起点识别 + 放行额度累计（含起步爬升；红灯期间清零）。
        bool greenStarted = open && !IsOpen;
        IsOpen = open;

        if (open)
        {
            if (greenStarted)
            {
                OpenElapsed = 0d;
                DischargeCredit = 0d;
            }

            OpenElapsed += dt;
            DischargeCredit += SimConstants.SaturationFlowPerLane * StartUpCapacityFactor(OpenElapsed) * dt;
            if (DischargeCredit > 1d)
            {
                DischargeCredit = 1d;   // 绿灯不"储蓄"放行能力：至多攒满一辆
            }
        }
        else
        {
            OpenElapsed = 0d;
            DischargeCredit = 0d;
        }

        bool canDischarge = open && DischargeCredit >= 1d - CreditEpsilon;

        // 2) 逐车推进（前 → 后；顺序固定，保证确定性）。
        for (int i = 0; i < _vehicles.Count; i++)
        {
            Vehicle v = _vehicles[i];

            // 队首：可放行时不受停止线约束（本步末会被放行驶离）；否则被停止线挡住。
            double limit;
            if (i == 0)
            {
                limit = canDischarge ? double.PositiveInfinity : Lane.Length;
            }
            else
            {
                Vehicle leader = _vehicles[i - 1];
                limit = CarFollowing.FollowingLimit(leader.Position, leader.Length);
            }

            double gap = limit - v.Position;
            double target = Math.Min(
                Lane.SpeedLimit * CarFollowing.SpeedFactorOf(v.Kind),
                CarFollowing.BrakingSpeed(gap));
            if (target < 0d)
            {
                target = 0d;
            }

            v.Speed = CarFollowing.StepSpeed(v.Speed, target, dt);
            v.Position += v.Speed * dt;

            // 硬钳制：绝不允许越过约束线（保证"任意两车不重叠"，T1.1 a）。
            if (v.Position > limit)
            {
                v.Position = limit;
                v.Speed = 0d;
            }
        }

        // 3) 队首放行：唯一"离开车道"的出口。
        if (canDischarge && Head is { } head && head.Position >= Lane.Length - StopLineTolerance)
        {
            DischargeCredit = Math.Max(0d, DischargeCredit - 1d);
            _vehicles.RemoveAt(0);
            head.ExitTime = Now;
            head.State = VehicleState.Crossing;
            _departed.Add(head);
        }

        // 4) 状态与统计（停车次数由"运动→停"跃迁计数；阻塞时长按状态累计）。
        QueueLength = 0;
        for (int i = 0; i < _vehicles.Count; i++)
        {
            Vehicle v = _vehicles[i];
            bool stopped = v.Stopped;
            if (stopped)
            {
                QueueLength++;
            }

            if (stopped && !v.WasStopped)
            {
                v.StopCount++;
                _stopped.Add(v);
            }

            v.WasStopped = stopped;

            v.State = stopped
                ? (i == 0 ? VehicleState.WaitingToAdvance : VehicleState.Queued)
                : VehicleState.Crossing;

            if (v.State != VehicleState.Crossing)
            {
                v.BlockedTime += dt;
            }
        }

        UpdateOccupancy();
    }

    /// <summary>取出并清空「自上次调用以来驶离车道」的车辆（顺序 = 放行顺序）。</summary>
    public IReadOnlyList<Vehicle> DrainDeparted()
    {
        if (_departed.Count == 0)
        {
            return Array.Empty<Vehicle>();
        }

        Vehicle[] result = _departed.ToArray();
        _departed.Clear();
        return result;
    }

    /// <summary>取出并清空「自上次调用以来新发生停车」的车辆（供 T1.6 发出
    /// <c>SimEventKind.VehicleStopped</c>）。</summary>
    public IReadOnlyList<Vehicle> DrainStopped()
    {
        if (_stopped.Count == 0)
        {
            return Array.Empty<Vehicle>();
        }

        Vehicle[] result = _stopped.ToArray();
        _stopped.Clear();
        return result;
    }

    /// <summary>产出只读快照（§4.4）。</summary>
    public LaneSnapshot Snapshot() => new(LaneId, QueueLength, Occupancy);

    /// <summary>复位到 t=0 的空队列（车辆清空、时刻归零、爬升与额度清零）。</summary>
    public void Reset()
    {
        _vehicles.Clear();
        _departed.Clear();
        _stopped.Clear();
        Now = 0d;
        IsOpen = false;
        OpenElapsed = 0d;
        DischargeCredit = 0d;
        QueueLength = 0;
        Occupancy = 0d;
    }

    private void Place(Vehicle vehicle, double position, double speed, int index)
    {
        vehicle.LaneId = LaneId;
        vehicle.Position = position;
        vehicle.Speed = speed;
        vehicle.State = VehicleState.Crossing;
        vehicle.EnterTime = Now;
        vehicle.ExitTime = -1d;
        vehicle.WasStopped = vehicle.Stopped;   // 入场即静止不算"停"；入场带速也不算
        _vehicles.Insert(index, vehicle);
        UpdateOccupancy();
    }

    private void UpdateOccupancy()
    {
        if (_vehicles.Count == 0)
        {
            Occupancy = 0d;
            return;
        }

        double front = _vehicles[0].Position;
        Vehicle tail = _vehicles[^1];
        double rear = Math.Max(0d, tail.Position - tail.Length);
        double occupied = front - rear;
        Occupancy = Lane.Length <= 0d ? 0d : Math.Clamp(occupied / Lane.Length, 0d, 1d);
    }
}
