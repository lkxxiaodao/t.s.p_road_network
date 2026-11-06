namespace SimCore.Io;

/// <summary>
/// 机动车需求曲线（技术方案03 §3.2 <c>demand[]</c>：<c>{ lane, time_points, flow_rates }</c>）。
/// </summary>
/// <remarks>
/// <para>借 LightSim <c>DemandProfile(link_id, time_points, flow_rates)</c>（技术方案01 §3.4）：分段常值时变曲线。
/// <see cref="TimePoints"/> 为各时段起始时刻（秒，升序），<see cref="FlowRates"/> 为对应时段的到达流率（辆/秒），
/// 两者等长。三类需求曲线（恒定 / 时变 / 脉冲）统一用该结构表达——脉冲即"中段高、两侧 0"（Gate 0 口径）。</para>
/// <para>纯数据载体；实际按时刻取值的确定性时刻表由 T1.7 需求生成器消费。</para>
/// </remarks>
public sealed class DemandPlan
{
    private static readonly double[] Empty = Array.Empty<double>();

    /// <summary>目标车道 id（§3.2 <c>demand[].lane</c>）。</summary>
    public int Lane { get; init; }

    /// <summary>分段起始时刻（秒，升序；§3.2 <c>time_points</c>）。</summary>
    public IReadOnlyList<double> TimePoints { get; init; } = Empty;

    /// <summary>各时段流率（辆/秒；§3.2 <c>flow_rates</c>）。</summary>
    public IReadOnlyList<double> FlowRates { get; init; } = Empty;
}

/// <summary>
/// 行人过街需求（技术方案03 §3.2 <c>pedestrians[]</c>：<c>{ crosswalk_turn, time_points, flow_rates }</c>）。
/// </summary>
/// <remarks>
/// 与 <see cref="DemandPlan"/> 同构的分段常值曲线，但作用对象是人行横道转向（<see cref="TurnType.Crosswalk"/>，见游戏设计v2 §9 等齐过街）。
/// </remarks>
public sealed class PedestrianDemand
{
    private static readonly double[] Empty = Array.Empty<double>();

    /// <summary>人行横道转向 id（§3.2 <c>crosswalk_turn</c>；须指向一条 <see cref="SimCore.Model.TurnType.Crosswalk"/> 转向）。</summary>
    public int CrosswalkTurn { get; init; }

    /// <summary>分段起始时刻（秒，升序；§3.2 <c>time_points</c>）。</summary>
    public IReadOnlyList<double> TimePoints { get; init; } = Empty;

    /// <summary>各时段行人到达率（人/秒；§3.2 <c>flow_rates</c>）。</summary>
    public IReadOnlyList<double> FlowRates { get; init; } = Empty;
}

/// <summary>
/// 非机动车运行模式（技术方案03 §3.2 <c>bicycles[].mode</c>；游戏设计v2 §9 的两种模式）。
/// </summary>
public enum BicycleMode
{
    /// <summary>独立非机动车道：有独立相位可控。</summary>
    Independent,

    /// <summary>机非混行：随本向机动车相位放行，起步略慢。</summary>
    Mixed,
}

/// <summary>
/// 非机动车需求（技术方案03 §3.2 <c>bicycles[]</c>：<c>{ lane, mode, time_points, flow_rates }</c>）。
/// </summary>
public sealed class BicycleDemand
{
    private static readonly double[] Empty = Array.Empty<double>();

    /// <summary>目标非机动车道 id（§3.2 <c>bicycles[].lane</c>）。</summary>
    public int Lane { get; init; }

    /// <summary>运行模式（§3.2 <c>bicycles[].mode</c>；缺省 <see cref="BicycleMode.Independent"/>）。</summary>
    public BicycleMode Mode { get; init; } = BicycleMode.Independent;

    /// <summary>分段起始时刻（秒，升序；§3.2 <c>time_points</c>）。</summary>
    public IReadOnlyList<double> TimePoints { get; init; } = Empty;

    /// <summary>各时段到达率（辆/秒；§3.2 <c>flow_rates</c>）。</summary>
    public IReadOnlyList<double> FlowRates { get; init; } = Empty;
}
