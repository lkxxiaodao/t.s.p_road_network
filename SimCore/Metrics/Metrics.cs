namespace SimCore.Metrics;

// ===========================================================================
// T2.1 · Metrics —— 指标快照（docs/SimCore-API.md §5.2 冻结）
//
// 设计 gating（指标须分类，故快照需按类聚合）：
//   * ClassMetrics 记录每类的 R/D/P/QNormalized（§5.1）；
//   * R/D/P 与 Q 为**分类加权 / 木桶**后的顶层口径（§5.2 四项指标口径表）——
//     R/D/P 取三类中**有需求者**的算术均值（与设计 §6.2「三类加权合成」等价：等需求权重下，
//     占比加权合成 == 有需求类的算术均值；此口径在单测中固定并注明）；
//     Q 取三类的**最低**（木桶原则，最弱势群体代表本项得分）。
//
// 不可变：本类型为只读快照，构造后不再变化；每次 MetricsCollector.Snapshot() 返回新对象。
// ===========================================================================

/// <summary>
/// 四项指标快照（<c>docs/SimCore-API.md</c> §5.2 冻结；M2 的核心产物）。
/// </summary>
/// <remarks>
/// <para>口径见 §5.2「四项指标口径」与技术方案01 §3.5，<b>不得自创定义</b>：
/// <list type="bullet">
///   <item><see cref="R"/>：通行服务率 = 通过数 / 需求数（分类加权）。</item>
///   <item><see cref="D"/>：平均延误 = 实际通行时间 − 自由流通行时间的均值（秒）。</item>
///   <item><see cref="P"/>：平均停车次数；仅统计机动车与非机动车，行人不计。</item>
///   <item><see cref="Q"/>：各车道排队按容量百分比归一后，取**出现过排队的类**中三类的**最低**（木桶原则，
///     设计v2 §6.2；未出现的类不参与，三类全未出现 → 0）。</item>
/// </list>
/// </para>
/// <para><b>刻意与新契约的一处接缝</b>：<see cref="Q"/> 是「归一后的最长排队」（无单位、∈[0,1]），
/// 而非「最长排队的辆数」。契约 §5.2 的字面表述为「归一后最长排队 ∈ [0,1]，三类取最低（木桶）」，
/// 故此处只暴露归一值；HUD 若需原始辆数可经 <see cref="Classes"/> 的归一分母反推，
/// 或走后续变更流程（§11）新增字段。本卡只做**口径落地**，未改冻结签名。</para>
/// </remarks>
public sealed class Metrics
{
    internal Metrics(
        double r,
        double d,
        double p,
        double q,
        bool spillback,
        bool collision,
        IReadOnlyList<int> collisionTurns,
        IReadOnlyList<ClassMetrics> classes,
        IReadOnlyList<ApproachMetrics> approaches)
    {
        R = r;
        D = d;
        P = p;
        Q = q;
        Spillback = spillback;
        Collision = collision;
        CollisionTurns = collisionTurns;
        Classes = classes;
        Approaches = approaches;
    }

    /// <summary>通行服务率 ∈ [0,1]（越大越好）。</summary>
    public double R { get; }

    /// <summary>平均延误（秒，越小越好）。</summary>
    public double D { get; }

    /// <summary>平均停车次数（行人不计，越小越好）。</summary>
    public double P { get; }

    /// <summary>归一后最长排队 ∈ [0,1]，取**出现过排队的类**中三类最低（木桶，越小越好）。</summary>
    /// <remarks>无任何类出现过排队 → 0；详见 <see cref="MetricsCollector"/> 文首口径注。</remarks>
    public double Q { get; }

    /// <summary>是否触发排队溢出（首车道占有率 &gt; <c>SimConstants.SpillbackOccupancy</c>，T2.1 e）。</summary>
    public bool Spillback { get; }

    /// <summary>本局是否发生碰撞（T2.2 c）。</summary>
    public bool Collision { get; }

    /// <summary>碰撞涉及的转向 id（升序去重；标出冲突相位）。无碰撞时为空。</summary>
    public IReadOnlyList<int> CollisionTurns { get; }

    /// <summary>机动车 / 行人 / 非机动车三类的指标（固定顺序：Driving, Pedestrian, Bicycle）。</summary>
    public IReadOnlyList<ClassMetrics> Classes { get; }

    /// <summary>逐进口道统计（按 节点 id → 道路 id 升序，确定性）。</summary>
    public IReadOnlyList<ApproachMetrics> Approaches { get; }

    /// <summary>
    /// 空指标（零需求 / 未运行）——供 M2 的「首局无 prev」与零基线使用（契约 §6.2 附注）。
    /// </summary>
    /// <remarks>
    /// <para>口径：R=1（无需求视为全部通过）、D=0、P=0、Q=0、无溢出、无碰撞。
    /// 三类指标均为「无需求空类」形态（R=1、D/P/Q=0）。</para>
    /// </remarks>
    public static Metrics Empty { get; } = new(
        r: 1d,
        d: 0d,
        p: 0d,
        q: 0d,
        spillback: false,
        collision: false,
        collisionTurns: Array.Empty<int>(),
        classes:
        [
            new ClassMetrics(TrafficClass.Driving, 1d, 0d, 0d, 0d),
            new ClassMetrics(TrafficClass.Pedestrian, 1d, 0d, 0d, 0d),
            new ClassMetrics(TrafficClass.Bicycle, 1d, 0d, 0d, 0d),
        ],
        approaches: Array.Empty<ApproachMetrics>());
}
