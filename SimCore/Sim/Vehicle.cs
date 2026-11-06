namespace SimCore.Sim;

/// <summary>
/// 车辆微观状态（借 A/B Street <c>CarState</c>，技术方案02 §2.1）。
/// </summary>
/// <remarks>
/// <para>本枚举是 T1.1 的**内部状态机**（<c>docs/SimCore-API.md</c> 未冻结其名称，仅冻结
/// <see cref="VehicleKind"/> 与 <see cref="VehicleSnapshot"/>），后续卡可引用但不得改写语义。</para>
/// <para>裁剪口径：A/B Street 的 <c>ChangingLanes / Unparking / Parking / IdlingAtStop</c>
/// 在本游戏（单点 / 小网络、短局）中不需要（技术方案02 §5.2 复杂度裁剪），故只保留三种基本态。</para>
/// </remarks>
internal enum VehicleState
{
    /// <summary>队列中：速度≈0 且前面还有车（被前车阻挡）。</summary>
    Queued,

    /// <summary>队首：已抵停止线并停下，等待信号放行（或被下游无空位阻挡）。</summary>
    WaitingToAdvance,

    /// <summary>行驶中：正常前进（自由流或跟驰）。</summary>
    Crossing,
}

/// <summary>
/// 车辆种类（<c>docs/SimCore-API.md</c> §4.4 冻结枚举；渲染区分，T3.1 b）。
/// </summary>
/// <remarks>成员名称与顺序为冻结契约，不得改名 / 增删 / 调序。</remarks>
public enum VehicleKind
{
    /// <summary>小汽车（默认；车长 <see cref="SimConstants.VehicleLength"/>）。</summary>
    Car,

    /// <summary>公交（加长；车长 <see cref="SimConstants.BusLength"/>）。</summary>
    Bus,

    /// <summary>货车（车长 <see cref="SimConstants.TruckLength"/>，速度系数 <see cref="SimConstants.TruckSpeedFactor"/>；T4.3 S1b 追加）。</summary>
    Truck,
}

/// <summary>
/// 车辆快照（<c>docs/SimCore-API.md</c> §4.4 冻结；表现层只读）。
/// </summary>
/// <param name="Id">车辆 id。</param>
/// <param name="LaneId">所在车道 id。</param>
/// <param name="Position">车头距车道起点的距离（米）。</param>
/// <param name="Speed">速度（米/秒）。</param>
/// <param name="Stopped">当前是否处于停车状态（速度 ≈ 0）。</param>
/// <param name="Kind">车型。</param>
public readonly record struct VehicleSnapshot(
    int Id,
    int LaneId,
    double Position,
    double Speed,
    bool Stopped,
    VehicleKind Kind);

/// <summary>
/// 单车微观状态机（T1.1；技术方案02 §2.1）。
/// </summary>
/// <remarks>
/// <para>职责：携带一辆车的几何（<see cref="Length"/>）、运动学（<see cref="Position"/> /
/// <see cref="Speed"/>）、状态（<see cref="State"/>）与统计量（<see cref="BlockedTime"/> /
/// <see cref="StopCount"/> / <see cref="ExitTime"/>）。设计 §9 要求「每单位记录通过时刻、停车次数、
/// 等待时长」——三者分别由 <see cref="ExitTime"/>、<see cref="StopCount"/>、<see cref="BlockedTime"/> 承载，
/// 直接供 M2 的 D（平均延误）与 P（平均停车次数）取用。</para>
/// <para>运动学由 <see cref="Queue"/> 统一推进（队列持有跟驰顺序）；本类型不自推进，
/// 以避免「同一车道内的车各自为政」破坏跟驰约束。</para>
/// <para>位置口径：<see cref="Position"/> = **车头（前保险杠）**距车道起点的距离（米）；
/// 车体占据 <c>[Position − Length, Position]</c>。</para>
/// <para>无系统随机数、无 Godot 依赖（铁律 1/2）。</para>
/// </remarks>
internal sealed class Vehicle
{
    /// <summary>构造一辆车。</summary>
    /// <param name="id">车辆 id（须 ≥ 0）。</param>
    /// <param name="kind">车型（默认 <see cref="VehicleKind.Car"/>）。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="id"/> 为负。</exception>
    public Vehicle(int id, VehicleKind kind = VehicleKind.Car)
    {
        if (id < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, "车辆 id 不得为负。");
        }

        Id = id;
        Kind = kind;
        Length = CarFollowing.VehicleLengthOf(kind);
    }

    /// <summary>车辆 id（全局唯一，由上层分配）。</summary>
    public int Id { get; }

    /// <summary>车型（渲染区分；影响车长）。</summary>
    public VehicleKind Kind { get; }

    /// <summary>车长（米）：小汽车 <see cref="SimConstants.VehicleLength"/>、货车 <see cref="SimConstants.TruckLength"/>、公交 <see cref="SimConstants.BusLength"/>。</summary>
    public double Length { get; }

    /// <summary>当前所在车道 id（未入场为 -1）。</summary>
    public int LaneId { get; internal set; } = -1;

    /// <summary>驶抵下游节点时拟走行的转向 id（-1 = 未指定，由上层路由决定；T1.3/T1.6 使用）。</summary>
    public int NextTurnId { get; set; } = -1;

    /// <summary>车头（前保险杠）距车道起点的距离（米）。</summary>
    public double Position { get; internal set; }

    /// <summary>速度（米/秒）。</summary>
    public double Speed { get; internal set; }

    /// <summary>当前微观状态。</summary>
    public VehicleState State { get; internal set; } = VehicleState.Crossing;

    /// <summary>累计阻塞时长（秒）：处于 <see cref="VehicleState.Queued"/> 或
    /// <see cref="VehicleState.WaitingToAdvance"/> 的时长之和（指标 D 的数据源）。</summary>
    public double BlockedTime { get; internal set; }

    /// <summary>累计停车次数（指标 P 的数据源）：由「运动 → 停」的状态跃迁计数，
    /// 持续停车不重复计数；入场即静止（未运动过）不计数。</summary>
    public int StopCount { get; internal set; }

    /// <summary>进入当前车道的时刻（秒；未入场为 -1）。</summary>
    public double EnterTime { get; internal set; } = -1d;

    /// <summary>驶离当前车道的时刻（秒；未驶离为 -1）——即设计 §9 的「通过时刻」。</summary>
    public double ExitTime { get; internal set; } = -1d;

    /// <summary>当前是否处于停车状态（速度 ≤ <see cref="CarFollowing.StopSpeedThreshold"/>）。</summary>
    public bool Stopped => Speed <= CarFollowing.StopSpeedThreshold;

    /// <summary>上一帧的停车标记（供停车次数计数；由 <see cref="Queue"/> 维护）。</summary>
    internal bool WasStopped { get; set; }

    /// <summary>产出只读快照（§4.4）。</summary>
    public VehicleSnapshot ToSnapshot()
        => new(Id, LaneId, Position, Speed, Stopped, Kind);

    /// <inheritdoc />
    public override string ToString()
        => $"Vehicle#{Id}({Kind}) lane={LaneId} pos={Position:F2} v={Speed:F2} {State}";
}
