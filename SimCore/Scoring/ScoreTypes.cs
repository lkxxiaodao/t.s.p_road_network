namespace SimCore.Scoring;

// ===========================================================================
// T2.2 · 评分类型（docs/SimCore-API.md §6.1 冻结）
//
//   * enum MetricKind        —— R / D / P / Q
//   * sealed class ScoreBreakdown —— 四项单项分（∈[0,1]）+ 加权总分
//   * sealed class Score          —— 总分 / 星级 / 碰撞判负 / 最差进口 / 最弱项
//
// 铁律：无 Godot 命名空间、无第三方依赖、无系统随机数（纯函数，确定性）。
// 冻结面：本文件只落地 §6.1 列出的 **public 属性**；构造函数一律 internal（不新增 public 面），
//        与 T2.1 的 Metrics（internal 构造）同口径。测试经 InternalsVisibleTo 授权访问 internal 构造。
// ===========================================================================

/// <summary>
/// 四项被计分的指标种类（<c>docs/SimCore-API.md</c> §6.1 冻结；游戏设计v2 §6.2）。
/// </summary>
/// <remarks>成员名称与顺序为冻结契约（附 A），不得改名 / 增删 / 调序；顺序即数值，并用作「最弱项」同分时的确定性顺序。</remarks>
public enum MetricKind
{
    /// <summary>通行服务率 R（越大越好）。</summary>
    R = 0,

    /// <summary>平均延误 D（越小越好）。</summary>
    D = 1,

    /// <summary>平均停车次数 P（越小越好）。</summary>
    P = 2,

    /// <summary>归一后最长排队 Q（越小越好）。</summary>
    Q = 3,
}

/// <summary>
/// 单项得分分解（<c>docs/SimCore-API.md</c> §6.1 冻结）。
/// </summary>
/// <remarks>
/// <para>四项单项分 s_i 均由「锚点线性插值 + clamp 到 [0,1]」得到（§6.2 / 游戏设计v2 §6.3）：
/// R 越大越好、D/P/Q 越小越好。</para>
/// <para><see cref="Total"/> 严格按 §6.2 字面 <c>Σ w_i · s_i</c> 计算（权重取自 <c>game_rules.weights</c>），
/// 再 clamp 到 [0,1] 以兑现 §6.1「<c>Total ∈ [0,1]</c>」；默认权重和为 1 时与未 clamp 结果一致。</para>
/// </remarks>
public sealed class ScoreBreakdown
{
    internal ScoreBreakdown(double r, double d, double p, double q, double total)
    {
        R = r;
        D = d;
        P = p;
        Q = q;
        Total = total;
    }

    /// <summary>通行服务率 R 的单项分 ∈ [0,1]（R 越大越好：b→0 分、g→1 分）。</summary>
    public double R { get; }

    /// <summary>平均延误 D 的单项分 ∈ [0,1]（D 越小越好：b→0 分、g→1 分）。</summary>
    public double D { get; }

    /// <summary>平均停车次数 P 的单项分 ∈ [0,1]（P 越小越好）。</summary>
    public double P { get; }

    /// <summary>归一后最长排队 Q 的单项分 ∈ [0,1]（Q 越小越好）。</summary>
    public double Q { get; }

    /// <summary>加权总分 <c>= clamp(Σ w_i · s_i, 0, 1)</c>，权重取自 <c>game_rules.weights</c>（§6.1）。</summary>
    /// <remarks>与 <see cref="Score.Total"/> 恒等：发生碰撞时二者同为 0（安全硬约束，游戏设计v2 §6.1）。</remarks>
    public double Total { get; }
}

/// <summary>
/// 一局的评分结果（<c>docs/SimCore-API.md</c> §6.1 冻结；M2 的结算产物）。
/// </summary>
/// <remarks>
/// <para>星级阈值见 §6.2（Gate 0 冻结默认，老板裁定 D11）：<c>≥0.95→3★、≥0.80→2★、≥0.60→1★、否则 0★</c>；
/// 与游戏设计v2 §6.4 的 95 / 80 / 60（0–100 制）严格等价，<b>不得放宽</b>。</para>
/// <para>安全是硬约束：<see cref="HasCollision"/> 为真时 <see cref="Total"/>=0、<see cref="Stars"/>=0
/// （游戏设计v2 §6.1「发生任意一次碰撞 → 本局直接判负」）。</para>
/// </remarks>
public sealed class Score
{
    internal Score(
        double total,
        int stars,
        ScoreBreakdown breakdown,
        bool hasCollision,
        IReadOnlyList<int> collisionTurns,
        int worstApproachNode,
        int worstApproachRoad,
        MetricKind worstMetric)
    {
        Total = total;
        Stars = stars;
        Breakdown = breakdown;
        HasCollision = hasCollision;
        CollisionTurns = collisionTurns;
        WorstApproachNode = worstApproachNode;
        WorstApproachRoad = worstApproachRoad;
        WorstMetric = worstMetric;
    }

    /// <summary>加权总分 ∈ [0,1]（碰撞时强制为 0）。</summary>
    public double Total { get; }

    /// <summary>星级 0..3（碰撞时强制为 0）。</summary>
    public int Stars { get; }

    /// <summary>单项得分分解（碰撞时单项分仍保留，仅总分归零——便于玩家看清"为何丢分"）。</summary>
    public ScoreBreakdown Breakdown { get; }

    /// <summary>本局是否发生碰撞（碰撞 → 0 星，§6.2）。</summary>
    public bool HasCollision { get; }

    /// <summary>碰撞涉及的转向 id（升序去重；即"冲突相位"的定位，无碰撞时为空）。</summary>
    public IReadOnlyList<int> CollisionTurns { get; }

    /// <summary>最差进口所在节点 id（-1 表示无进口道数据）。</summary>
    public int WorstApproachNode { get; }

    /// <summary>最差进口道路 id（-1 表示无进口道数据）。</summary>
    public int WorstApproachRoad { get; }

    /// <summary>最弱项（四项单项分中最低者；同分取枚举顺序 R→D→P→Q）。</summary>
    public MetricKind WorstMetric { get; }
}
