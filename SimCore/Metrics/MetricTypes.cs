namespace SimCore.Metrics;

// ===========================================================================
// T2.1 · 指标统计 R/D/P/Q + 排队溢出（技术方案03 §5 · T2.1；角色：评分系统工程师）
//
// 本文件落地 docs/SimCore-API.md §5.1 冻结的三个类型（其余成员一律不新增 public 面）：
//   * enum TrafficClass      —— 机动车 / 行人 / 非机动车
//   * record struct ClassMetrics    —— 三类交通各自的指标（T2.1 d「木桶」与归因）
//   * record struct ApproachMetrics —— 逐进口道统计（T2.2 d「定位最差进口」/ T3.4 c 归因句）
//
// 口径来源（**不得自创定义**，技术方案01 §3.5）：
//   * R 通行服务率   = 通过数 / 需求数（分类加权：机动车 / 行人 / 非机动车）
//   * D 平均延误     = 实际通行时间 − 自由流通行时间的均值（秒）
//   * P 平均停车次数 = 仅统计机动车与非机动车，行人不计（T2.1 c）
//   * Q 排队归一     = 各车道排队按容量百分比归一后，取三类的最低（木桶）
//   * 溢出          = 首车道占有率 > SimConstants.SpillbackOccupancy(0.9)（T2.1 e）
//
// 铁律：无 Godot 命名空间、无第三方依赖、无系统随机数（纯累计，确定性）。
// ===========================================================================

/// <summary>
/// 交通类别（<c>docs/SimCore-API.md</c> §5.1 冻结）。用于 R/D/P/Q 的分类加权与 T2.1 d 的「木桶」。
/// </summary>
/// <remarks>成员名称与顺序为冻结契约（附 A），不得改名 / 增删 / 调序；顺序即数值。</remarks>
public enum TrafficClass
{
    /// <summary>机动车（<c>SimEventKind.DemandArrived</c> 落在机动车道 / 出行需求）。</summary>
    Driving,

    /// <summary>行人（人行横道转向；P 指标不计行人，T2.1 c）。</summary>
    Pedestrian,

    /// <summary>非机动车（非机动车道）。</summary>
    Bicycle,
}

/// <summary>
/// 单类交通的三项指标（<c>docs/SimCore-API.md</c> §5.1 冻结）。
/// </summary>
/// <param name="Class">交通类别。</param>
/// <param name="R">该类通行服务率 = 通过数 / 需求数 ∈ [0,1]（越大越好）。</param>
/// <param name="D">该类平均延误（秒；实际 − 自由流，越小越好）。</param>
/// <param name="P">该类平均停车次数（行人不计 → 行人恒为 0，越小越好）。</param>
/// <param name="QNormalized">该类排队按容量百分比归一后的最大值 ∈ [0,1]（越小越好）。</param>
public readonly record struct ClassMetrics(
    TrafficClass Class,
    double R,
    double D,
    double P,
    double QNormalized);

/// <summary>
/// 逐进口道的统计（<c>docs/SimCore-API.md</c> §5.1 冻结）——供 T2.2 d「定位最差进口」与 T3.4 c 归因句。
/// </summary>
/// <param name="NodeId">进口道所属路口节点 id。</param>
/// <param name="RoadId">进口道路 id（取自 <c>Lane.Road</c>）。</param>
/// <param name="Delay">该进口道的平均延误（秒）。</param>
/// <param name="QueueLength">该进口道的峰值排队长度（辆）。</param>
/// <param name="R">该进口道的通行服务率 ∈ [0,1]。</param>
/// <param name="P">该进口道的平均停车次数。</param>
public readonly record struct ApproachMetrics(
    int NodeId,
    int RoadId,
    double Delay,
    int QueueLength,
    double R,
    double P);
