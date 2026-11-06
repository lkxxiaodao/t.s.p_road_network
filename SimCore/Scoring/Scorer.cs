using System.Globalization;
using System.Text;
using SimCore.Io;
using SimCore.Metrics;

// 类型名与命名空间同名（SimCore.Metrics.Metrics）：显式别名，避免歧义（与 T2.1 测试同口径）。
using MetricsSnapshot = SimCore.Metrics.Metrics;

namespace SimCore.Scoring;

// ===========================================================================
// T2.2 · Scorer —— 评分 / 星级 / 与上局差值 + 归因文案（docs/SimCore-API.md §6.2）
//
//   public static Score  Evaluate(Level level, Metrics metrics)
//   public static string[] Attribution(Score previous, Score current)
//
// 口径来源（**不得自创**）：
//   * 游戏设计v2 §6.3 计分公式：越大越好 s=clamp((v-b)/(g-b),0,1)；越小越好 s=clamp((b-v)/(b-g),0,1)；
//   * 游戏设计v2 §6.4 / 契约 §6.2 星级阈值：≥0.95→3★、≥0.80→2★、≥0.60→1★、否则 0★；
//   * 游戏设计v2 §6.1 安全硬约束：任一次碰撞 → 本局判负 0 星，并标出冲突相位；
//   * 游戏设计v2 §8.5 归因：对丢分最多的指标，定位其最差进口，生成一句可解释文案；
//     技术方案02 §2.6：把 (Agent → DelayCause/Intersection) 对上"进口道"即可直接生成文案。
//
// 配置驱动（T2.2 ④）：**锚点与权重全部从 level.GameRules 读取**，本文件不含任何硬编码锚点 / 权重。
//
// 铁律：无 Godot 命名空间、无第三方依赖、无系统随机数 —— Evaluate / Attribution 均为纯函数（确定性）。
// ===========================================================================

/// <summary>
/// 评分器（<c>docs/SimCore-API.md</c> §6.2 冻结）——把一局的 <c>Metrics</c> 折算成星级成绩与归因文案。
/// </summary>
/// <remarks>
/// <para><b>纯函数</b>：输出只依赖入参；无系统时钟、无随机数 → 同输入同输出（铁律 2）。</para>
/// <para><b>表现层只读</b>：结算 UI 只展示 <see cref="Evaluate"/> 的数值与 <see cref="Attribution"/> 的文案，
/// 不得自行拼句（契约 §6.2 / §5.4）。</para>
/// </remarks>
public static class Scorer
{
    /// <summary>浮点比较容差（用于"提升 / 下降 / 持平"的判据，避免 1e-17 级噪声误报）。</summary>
    private const double Epsilon = 1e-9d;

    /// <summary>三星阈值（契约 §6.2；= 游戏设计v2 §6.4 的 95 分）。</summary>
    private const double ThreeStar = 0.95d;

    /// <summary>二星阈值（契约 §6.2；= 80 分）。</summary>
    private const double TwoStar = 0.80d;

    /// <summary>一星阈值（契约 §6.2；= 60 分）。</summary>
    private const double OneStar = 0.60d;

    // ------------------------------------------------------------------
    // Evaluate
    // ------------------------------------------------------------------

    /// <summary>
    /// 依据关卡配置（<c>game_rules</c> 锚点 / 权重）计算一局成绩（§6.2）。
    /// </summary>
    /// <param name="level">关卡装配层——提供 <see cref="Level.GameRules"/> 的锚点与权重（**唯一**配置来源）。</param>
    /// <param name="metrics">本局指标快照（<c>SimCore.Metrics.Metrics</c>）。</param>
    /// <returns>总分 / 星级 / 单项分解 / 碰撞判负 / 最差进口 / 最弱项。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="level"/> 或 <paramref name="metrics"/> 为 null。</exception>
    public static Score Evaluate(Level level, MetricsSnapshot metrics)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(metrics);

        MetricAnchors anchors = level.GameRules.Anchors;
        MetricWeights weights = level.GameRules.Weights;

        // 四项单项分：R 越大越好；D / P / Q 越小越好（游戏设计v2 §6.3）。
        double sr = ScoreBiggerBetter(metrics.R, anchors.R);
        double sd = ScoreSmallerBetter(metrics.D, anchors.D);
        double sp = ScoreSmallerBetter(metrics.P, anchors.P);
        double sq = ScoreSmallerBetter(metrics.Q, anchors.Q);

        // 加权总分（§6.2 字面 Σ w_i·s_i），clamp 到 [0,1]（§6.1）。
        double weighted =
            (weights.R * sr) + (weights.D * sd) + (weights.P * sp) + (weights.Q * sq);
        double total = Math.Clamp(weighted, 0d, 1d);

        bool collision = metrics.Collision;
        IReadOnlyList<int> collisionTurns = metrics.CollisionTurns ?? Array.Empty<int>();

        MetricKind worstMetric = WeakestMetric(sr, sd, sp, sq);
        (int worstNode, int worstRoad) = WorstApproach(metrics, worstMetric);

        // 安全是硬约束：碰撞 → 总分 0、0 星（单项分保留，供 UI 解释"为何丢分"）。
        double finalTotal = collision ? 0d : total;
        int stars = collision ? 0 : StarsOf(total);

        var breakdown = new ScoreBreakdown(sr, sd, sp, sq, finalTotal);
        return new Score(
            finalTotal,
            stars,
            breakdown,
            collision,
            collisionTurns,
            worstNode,
            worstRoad,
            worstMetric);
    }

    // ------------------------------------------------------------------
    // Attribution
    // ------------------------------------------------------------------

    /// <summary>
    /// 生成"与上局的差值 + 归因"文案（§6.2；游戏设计v2 §8.5）。
    /// </summary>
    /// <param name="previous">上一局的成绩（首局可用 <c>Evaluate(level, Metrics.Empty)</c> 作为零基线）。</param>
    /// <param name="current">本局成绩。</param>
    /// <returns>
    /// 每项一句中文，至少包含：①最弱项 + 最差进口的定位句；②与上一局的差值句；
    /// ③碰撞说明句（仅当 <see cref="Score.HasCollision"/> 为真）。
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="previous"/> 或 <paramref name="current"/> 为 null。</exception>
    public static string[] Attribution(Score previous, Score current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var lines = new List<string>(3);

        // ① 定位句：最弱项 + 最差进口（游戏设计v2 §8.5「定位其最差进口」）。
        string location = current.WorstApproachNode < 0
            ? "无进口道数据"
            : $"节点 {current.WorstApproachNode}·道路 {current.WorstApproachRoad}";
        lines.Add(
            $"最弱项：{MetricName(current.WorstMetric)}（单项得分 {Format(WeakestScore(current), 2)}）；" +
            $"最差进口：{location}");

        // ② 差值句：总分 + 四项单项分相对上一局的变化（cur − prev）。
        double totalDelta = current.Total - previous.Total;
        string verdict = totalDelta > Epsilon ? "提升"
            : totalDelta < -Epsilon ? "下降"
            : "持平";
        var sb = new StringBuilder();
        sb.Append("与上局相比：总分 ")
          .Append(Format(previous.Total, 3)).Append(" → ").Append(Format(current.Total, 3))
          .Append("（").Append(Signed(totalDelta)).Append(' ').Append(verdict).Append("）；")
          .Append(MetricDelta("R", previous.Breakdown.R, current.Breakdown.R, biggerBetter: true)).Append('、')
          .Append(MetricDelta("D", previous.Breakdown.D, current.Breakdown.D, biggerBetter: false)).Append('、')
          .Append(MetricDelta("P", previous.Breakdown.P, current.Breakdown.P, biggerBetter: false)).Append('、')
          .Append(MetricDelta("Q", previous.Breakdown.Q, current.Breakdown.Q, biggerBetter: false));
        lines.Add(sb.ToString());

        // ③ 碰撞说明句（仅碰撞时；标出冲突转向 = 冲突相位，游戏设计v2 §8.5 / §6.1）。
        if (current.HasCollision)
        {
            string turns = current.CollisionTurns.Count > 0
                ? string.Join("、", current.CollisionTurns)
                : "未记录到转向";
            lines.Add($"本局发生碰撞（冲突转向 {turns}）→ 按安全底线直接判负 0 星；请检查该相位内是否存在冲突流向。");
        }

        return lines.ToArray();
    }

    // ------------------------------------------------------------------
    // 单项计分（游戏设计v2 §6.3）
    // ------------------------------------------------------------------

    /// <summary>越大越好（R）：<c>s = clamp((v − b) / (g − b), 0, 1)</c>。</summary>
    private static double ScoreBiggerBetter(double value, MetricAnchorValue anchor)
    {
        if (!double.IsFinite(value))
        {
            return 0d;   // 非有限值（NaN / ±∞）不给分，保持确定性。
        }

        double b = anchor.Bottom;
        double g = anchor.Good;

        if (g == b)
        {
            // 锚点band退化（含未标定 b=g=0）：取线性公式的**极限**（阶跃函数），不产生 0 除。
            return value >= g ? 1d : 0d;
        }

        return Math.Clamp((value - b) / (g - b), 0d, 1d);
    }

    /// <summary>越小越好（D / P / Q）：<c>s = clamp((b − v) / (b − g), 0, 1)</c>。</summary>
    private static double ScoreSmallerBetter(double value, MetricAnchorValue anchor)
    {
        if (!double.IsFinite(value))
        {
            return 0d;
        }

        double b = anchor.Bottom;
        double g = anchor.Good;

        if (g == b)
        {
            return value <= g ? 1d : 0d;
        }

        return Math.Clamp((b - value) / (b - g), 0d, 1d);
    }

    /// <summary>星级判定（契约 §6.2；阈值与游戏设计v2 §6.4 严格等价，不得放宽）。</summary>
    private static int StarsOf(double total)
    {
        if (total >= ThreeStar && total > Epsilon)
        {
            return 3;
        }

        if (total >= TwoStar)
        {
            return 2;
        }

        if (total >= OneStar)
        {
            return 1;
        }

        return 0;
    }

    // ------------------------------------------------------------------
    // 最弱项 / 最差进口
    // ------------------------------------------------------------------

    /// <summary>四项单项分中最低者即"最弱项"；同分取枚举顺序 R→D→P→Q（确定性）。</summary>
    private static MetricKind WeakestMetric(double r, double d, double p, double q)
    {
        MetricKind kind = MetricKind.R;
        double min = r;

        if (d < min)
        {
            min = d;
            kind = MetricKind.D;
        }

        if (p < min)
        {
            min = p;
            kind = MetricKind.P;
        }

        if (q < min)
        {
            min = q;
            kind = MetricKind.Q;
        }

        return kind;
    }

    /// <summary>
    /// 定位"最差进口"（§6.2 / 游戏设计v2 §8.5）：
    /// 最弱项为 Q 时按峰值排队最长者；否则按平均延误最大者。次级按另一维度取最劣，最终按 (节点, 道路) id 升序——保证确定性。
    /// </summary>
    private static (int Node, int Road) WorstApproach(MetricsSnapshot metrics, MetricKind worstMetric)
    {
        IReadOnlyList<ApproachMetrics> approaches = metrics.Approaches;
        if (approaches is null || approaches.Count == 0)
        {
            return (-1, -1);
        }

        bool byQueue = worstMetric == MetricKind.Q;
        ApproachMetrics best = approaches[0];

        for (int i = 1; i < approaches.Count; i++)
        {
            if (IsWorse(approaches[i], best, byQueue))
            {
                best = approaches[i];
            }
        }

        return (best.NodeId, best.RoadId);
    }

    private static bool IsWorse(ApproachMetrics candidate, ApproachMetrics incumbent, bool byQueue)
    {
        double cPrimary = byQueue ? candidate.QueueLength : candidate.Delay;
        double iPrimary = byQueue ? incumbent.QueueLength : incumbent.Delay;
        if (cPrimary != iPrimary)
        {
            return cPrimary > iPrimary;
        }

        double cSecondary = byQueue ? candidate.Delay : candidate.QueueLength;
        double iSecondary = byQueue ? incumbent.Delay : incumbent.QueueLength;
        if (cSecondary != iSecondary)
        {
            return cSecondary > iSecondary;
        }

        if (candidate.NodeId != incumbent.NodeId)
        {
            return candidate.NodeId < incumbent.NodeId;
        }

        return candidate.RoadId < incumbent.RoadId;
    }

    // ------------------------------------------------------------------
    // 文案工具
    // ------------------------------------------------------------------

    private static string MetricName(MetricKind kind) => kind switch
    {
        MetricKind.R => "R 通行服务率",
        MetricKind.D => "D 平均延误",
        MetricKind.P => "P 平均停车次数",
        MetricKind.Q => "Q 最长排队",
        _ => kind.ToString(),
    };

    /// <summary>取"最弱项"对应的单项分（用于归因文案展示丢分幅度）。</summary>
    private static double WeakestScore(Score score) => score.WorstMetric switch
    {
        MetricKind.R => score.Breakdown.R,
        MetricKind.D => score.Breakdown.D,
        MetricKind.P => score.Breakdown.P,
        MetricKind.Q => score.Breakdown.Q,
        _ => 1d,
    };

    private static string MetricDelta(string name, double before, double after, bool biggerBetter)
    {
        double delta = after - before;
        bool improved = biggerBetter ? delta > Epsilon : delta < -Epsilon;
        bool worsened = biggerBetter ? delta < -Epsilon : delta > Epsilon;
        string tag = improved ? "改善" : worsened ? "恶化" : "持平";

        return $"{name} {Format(before, 2)}→{Format(after, 2)}（{Signed(delta)} {tag}）";
    }

    private static string Format(double value, int digits)
        => value.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private static string Signed(double value)
        => value.ToString("+0.000;-0.000;0.000", CultureInfo.InvariantCulture);
}
