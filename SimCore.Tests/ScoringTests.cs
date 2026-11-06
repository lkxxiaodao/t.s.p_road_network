using SimCore.Io;
using SimCore.Metrics;
using SimCore.Model;
using SimCore.Scoring;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

// 类型名与命名空间同名（SimCore.Metrics.Metrics）：显式别名，避免歧义。
using MetricsSnapshot = SimCore.Metrics.Metrics;

namespace SimCore.Tests;

/// <summary>
/// T2.2「线性插值计分 / 星级判定 / 与上局差值 + 归因文案」验收测试。
/// <para>验收标准逐条对应（任务卡 T2.2 a~d）：
/// <list type="bullet">
///   <item>A 组 → 验收 a) 单项分 clamp 到 [0,1]（R 越大越好、D/P/Q 越小越好两套公式都测）</item>
///   <item>B 组 → 验收 b) 阈值边界（=g / =b）得分正确</item>
///   <item>C 组 → 验收 c) 碰撞 → 0 星且标出冲突相位</item>
///   <item>D 组 → 验收 d) 归因能定位"最差进口"并生成一句文案</item>
///   <item>E 组 → 配置驱动（锚点 / 权重取自 game_rules，不硬编码）/ 前置 / 确定性 / 冻结面对应</item>
/// </list>
/// </para>
/// <para>口径来源：游戏设计v2 §6.3/§6.4/§8.5 + 技术方案02 §2.6 + <c>docs/SimCore-API.md</c> §6.1/§6.2。</para>
/// </summary>
public sealed class ScoringTests
{
    private readonly ITestOutputHelper _output;

    public ScoringTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ==================================================================
    // A 组：验收 a) 单项分 clamp 到 [0,1]（两套公式）
    // ==================================================================

    /// <summary>a1：R（越大越好）——超过优秀值钳到 1，低于底线钳到 0。</summary>
    [Fact]
    public void A1_R_BiggerBetter_Clamps_To_Unit_Interval()
    {
        Level level = MakeLevel(anchors: DefaultAnchors());   // R: b=0.8, g=1.0

        Score high = Scorer.Evaluate(level, MakeMetrics(r: 1.4d, d: 0d, p: 0d, q: 0d));
        Score neg = Scorer.Evaluate(level, MakeMetrics(r: -0.5d, d: 0d, p: 0d, q: 0d));

        _output.WriteLine($"[A1] R=1.4 → {high.Breakdown.R:F6}（期望 1.0）；R=-0.5 → {neg.Breakdown.R:F6}（期望 0.0）");

        Assert.Equal(1d, high.Breakdown.R, 9);   // 上限钳制
        Assert.Equal(0d, neg.Breakdown.R, 9);    // 下限钳制
    }

    /// <summary>a2：R 在锚点区间内线性插值——R=0.9 恰为中点 0.5。</summary>
    [Fact]
    public void A2_R_Interpolates_Linearly_Inside_Band()
    {
        Level level = MakeLevel(anchors: DefaultAnchors());   // R: b=0.8, g=1.0
        Score score = Scorer.Evaluate(level, MakeMetrics(r: 0.9d, d: 0d, p: 0d, q: 0d));

        _output.WriteLine($"[A2] R=0.9 → {score.Breakdown.R:F6}（期望 (0.9-0.8)/(1.0-0.8)=0.5）");

        Assert.Equal(0.5d, score.Breakdown.R, 9);
    }

    /// <summary>a3：D（越小越好）——超过底线钳到 0，优于优秀值钳到 1，区间内线性插值。</summary>
    [Fact]
    public void A3_D_SmallerBetter_Clamps_And_Interpolates()
    {
        Level level = MakeLevel(anchors: DefaultAnchors());   // D: b=60, g=20

        double zero = Scorer.Evaluate(level, MakeMetrics(r: 1d, d: 100d, p: 0d, q: 0d)).Breakdown.D;
        double full = Scorer.Evaluate(level, MakeMetrics(r: 1d, d: 5d, p: 0d, q: 0d)).Breakdown.D;
        double mid = Scorer.Evaluate(level, MakeMetrics(r: 1d, d: 40d, p: 0d, q: 0d)).Breakdown.D;

        _output.WriteLine($"[A3] D=100→{zero:F6}(0)；D=5→{full:F6}(1)；D=40→{mid:F6}(0.5)");

        Assert.Equal(0d, zero, 9);
        Assert.Equal(1d, full, 9);
        Assert.Equal(0.5d, mid, 9);
    }

    /// <summary>a4：P / Q（越小越好）——两套公式中的"越小越好"用于 P、Q，同样 clamp + 插值。</summary>
    [Fact]
    public void A4_P_And_Q_SmallerBetter_Clamp_And_Interpolate()
    {
        Level level = MakeLevel(anchors: DefaultAnchors());   // P: b=2.5,g=1.2；Q: b=1.0,g=0.5

        double pZero = Scorer.Evaluate(level, MakeMetrics(1d, 0d, 3.0d, 0d)).Breakdown.P;
        double pFull = Scorer.Evaluate(level, MakeMetrics(1d, 0d, 1.0d, 0d)).Breakdown.P;
        double pMid = Scorer.Evaluate(level, MakeMetrics(1d, 0d, 1.85d, 0d)).Breakdown.P;

        double qZero = Scorer.Evaluate(level, MakeMetrics(1d, 0d, 0d, 1.2d)).Breakdown.Q;
        double qFull = Scorer.Evaluate(level, MakeMetrics(1d, 0d, 0d, 0.4d)).Breakdown.Q;
        double qMid = Scorer.Evaluate(level, MakeMetrics(1d, 0d, 0d, 0.75d)).Breakdown.Q;

        _output.WriteLine($"[A4] P: 3.0→{pZero:F4}(0) 1.0→{pFull:F4}(1) 1.85→{pMid:F4}(0.5)；" +
                          $"Q: 1.2→{qZero:F4}(0) 0.4→{qFull:F4}(1) 0.75→{qMid:F4}(0.5)");

        Assert.Equal(0d, pZero, 9);
        Assert.Equal(1d, pFull, 9);
        Assert.Equal(0.5d, pMid, 9);
        Assert.Equal(0d, qZero, 9);
        Assert.Equal(1d, qFull, 9);
        Assert.Equal(0.5d, qMid, 9);
    }

    /// <summary>a5：极端输入下四项单项分仍恒在 [0,1]（clamp 不遗漏任何一项）。</summary>
    [Fact]
    public void A5_All_Items_Stay_Within_Unit_Interval_On_Extremes()
    {
        Level level = MakeLevel(anchors: DefaultAnchors());

        (double r, double d, double p, double q)[] cases =
        [
            (0d, 0d, 0d, 0d),
            (1d, 0d, 0d, 0d),
            (0d, 1e9d, 1e9d, 1e9d),
            (1d, 1e9d, 1e9d, 1e9d),
            (2d, -5d, -5d, -5d),
        ];

        foreach ((double r, double d, double p, double q) in cases)
        {
            ScoreBreakdown b = Scorer.Evaluate(level, MakeMetrics(r, d, p, q)).Breakdown;
            _output.WriteLine($"[A5] (R={r},D={d},P={p},Q={q}) → [{b.R:F3},{b.D:F3},{b.P:F3},{b.Q:F3}] total={b.Total:F3}");

            Assert.InRange(b.R, 0d, 1d);
            Assert.InRange(b.D, 0d, 1d);
            Assert.InRange(b.P, 0d, 1d);
            Assert.InRange(b.Q, 0d, 1d);
            Assert.InRange(b.Total, 0d, 1d);
        }
    }

    /// <summary>a6：锚点退化（未标定 b=g）→ 取线性公式的极限（阶跃），不产生除零 / NaN。</summary>
    [Fact]
    public void A6_Degenerate_Anchor_Steps_Without_Division_By_Zero()
    {
        Level level = MakeLevel(anchors: new MetricAnchors());   // 四项锚点全未标定（b=g=0）
        ScoreBreakdown b = Scorer.Evaluate(level, MakeMetrics(1d, 30d, 1d, 0.5d)).Breakdown;

        _output.WriteLine($"[A6] 未标定锚点 → R={b.R} D={b.D} P={b.P} Q={b.Q} total={b.Total}");

        Assert.True(double.IsFinite(b.Total));
        Assert.Equal(1d, b.R, 9);   // R=1 ≥ g=0 → 阶跃取 1
        Assert.Equal(0d, b.D, 9);   // D=30 > g=0 → 阶跃取 0
        Assert.Equal(0d, b.P, 9);
        Assert.Equal(0d, b.Q, 9);
    }

    // ==================================================================
    // B 组：验收 b) 阈值边界（=g / =b）
    // ==================================================================

    /// <summary>b1：R 的 =g → 1 分、=b → 0 分（游戏设计v2 §6.3）。</summary>
    [Fact]
    public void B1_R_Boundary_At_Good_And_Bottom()
    {
        Level level = MakeLevel(anchors: DefaultAnchors());   // R: b=0.8, g=1.0

        double atGood = Scorer.Evaluate(level, MakeMetrics(1.0d, 0d, 0d, 0d)).Breakdown.R;
        double atBottom = Scorer.Evaluate(level, MakeMetrics(0.8d, 0d, 0d, 0d)).Breakdown.R;

        _output.WriteLine($"[B1] R=1.0(=g)→{atGood}；R=0.8(=b)→{atBottom}");

        Assert.Equal(1d, atGood, 9);
        Assert.Equal(0d, atBottom, 9);
    }

    /// <summary>b2：D 的 =g → 1 分、=b → 0 分。</summary>
    [Fact]
    public void B2_D_Boundary_At_Good_And_Bottom()
    {
        Level level = MakeLevel(anchors: DefaultAnchors());   // D: b=60, g=20

        double atGood = Scorer.Evaluate(level, MakeMetrics(1d, 20d, 0d, 0d)).Breakdown.D;
        double atBottom = Scorer.Evaluate(level, MakeMetrics(1d, 60d, 0d, 0d)).Breakdown.D;

        _output.WriteLine($"[B2] D=20(=g)→{atGood}；D=60(=b)→{atBottom}");

        Assert.Equal(1d, atGood, 9);
        Assert.Equal(0d, atBottom, 9);
    }

    /// <summary>b3：P 的 =g → 1 分、=b → 0 分。</summary>
    [Fact]
    public void B3_P_Boundary_At_Good_And_Bottom()
    {
        Level level = MakeLevel(anchors: DefaultAnchors());   // P: b=2.5, g=1.2

        Assert.Equal(1d, Scorer.Evaluate(level, MakeMetrics(1d, 0d, 1.2d, 0d)).Breakdown.P, 9);
        Assert.Equal(0d, Scorer.Evaluate(level, MakeMetrics(1d, 0d, 2.5d, 0d)).Breakdown.P, 9);
    }

    /// <summary>b4：Q 的 =g → 1 分、=b → 0 分。</summary>
    [Fact]
    public void B4_Q_Boundary_At_Good_And_Bottom()
    {
        Level level = MakeLevel(anchors: DefaultAnchors());   // Q: b=1.0, g=0.5

        Assert.Equal(1d, Scorer.Evaluate(level, MakeMetrics(1d, 0d, 0d, 0.5d)).Breakdown.Q, 9);
        Assert.Equal(0d, Scorer.Evaluate(level, MakeMetrics(1d, 0d, 0d, 1.0d)).Breakdown.Q, 9);
    }

    /// <summary>b5：星级阈值边界——用 R 单项（权重 R=1）精确落在 0.95 / 0.80 / 0.60 与略低处。</summary>
    [Fact]
    public void B5_Star_Thresholds_Are_Exact_And_Not_Loosened()
    {
        // 权重 R=1、其余 0 → Total == s_R；锚点 R:[0,1] → s_R == R（构造精确落点）。
        var level = MakeLevel(
            anchors: new MetricAnchors { R = new MetricAnchorValue(0d, 1d) },
            weights: new MetricWeights { R = 1d, D = 0d, P = 0d, Q = 0d });

        Score s95 = Scorer.Evaluate(level, MakeMetrics(0.95d, 0d, 0d, 0d));
        Score s80 = Scorer.Evaluate(level, MakeMetrics(0.80d, 0d, 0d, 0d));
        Score s60 = Scorer.Evaluate(level, MakeMetrics(0.60d, 0d, 0d, 0d));
        Score sBelow = Scorer.Evaluate(level, MakeMetrics(0.599d, 0d, 0d, 0d));
        Score s9499 = Scorer.Evaluate(level, MakeMetrics(0.9499d, 0d, 0d, 0d));

        _output.WriteLine($"[B5] 0.95→{s95.Stars}★  0.80→{s80.Stars}★  0.60→{s60.Stars}★  " +
                          $"0.599→{sBelow.Stars}★  0.9499→{s9499.Stars}★");

        Assert.Equal(3, s95.Stars);      // = 0.95 恰好三星
        Assert.Equal(2, s80.Stars);      // = 0.80 恰好二星
        Assert.Equal(1, s60.Stars);      // = 0.60 恰好一星
        Assert.Equal(0, sBelow.Stars);   // 0.599 < 0.60 → 无星
        Assert.Equal(2, s9499.Stars);    // 0.9499 < 0.95 → 二星（不放宽）
    }

    // ==================================================================
    // C 组：验收 c) 碰撞 → 0 星且标出冲突相位
    // ==================================================================

    /// <summary>c1：碰撞 → Total=0、Stars=0、HasCollision=true，且标出冲突转向。</summary>
    [Fact]
    public void C1_Collision_Forces_Zero_Stars_And_Flags_Conflict_Turns()
    {
        Level level = MakeLevel(anchors: DefaultAnchors());   // 本可拿三星的完美指标
        Score score = Scorer.Evaluate(
            level,
            MakeMetrics(1d, 0d, 0d, 0d, collision: true, collisionTurns: new[] { 3, 1 }));

        _output.WriteLine($"[C1] 完美指标但碰撞 → total={score.Total} stars={score.Stars} " +
                          $"turns=[{string.Join(",", score.CollisionTurns)}]");

        Assert.True(score.HasCollision);
        Assert.Equal(0d, score.Total, 9);
        Assert.Equal(0, score.Stars);
        Assert.Contains(1, score.CollisionTurns);
        Assert.Contains(3, score.CollisionTurns);
    }

    /// <summary>c2：碰撞归零总分，但单项分保留（便于 UI 解释"为何丢分"，游戏设计v2 §6.1）。</summary>
    [Fact]
    public void C2_Collision_Keeps_Single_Item_Scores()
    {
        Level level = MakeLevel(anchors: DefaultAnchors());

        Score noCrash = Scorer.Evaluate(level, MakeMetrics(1d, 0d, 0d, 0d));
        Score crash = Scorer.Evaluate(level, MakeMetrics(1d, 0d, 0d, 0d, collision: true, collisionTurns: new[] { 0 }));

        _output.WriteLine($"[C2] 无碰撞 R'={noCrash.Breakdown.R}；碰撞 R'={crash.Breakdown.R}（期望相同）");

        Assert.Equal(noCrash.Breakdown.R, crash.Breakdown.R, 9);
        Assert.Equal(noCrash.Breakdown.D, crash.Breakdown.D, 9);
        Assert.Equal(0d, crash.Breakdown.Total, 9);   // 分解总分与 Score.Total 一致（碰撞同为 0）
        Assert.Equal(0d, crash.Total, 9);
    }

    /// <summary>c3：无碰撞时星级由总分决定（0 星仅在低于 0.60 时出现，与碰撞无关）。</summary>
    [Fact]
    public void C3_No_Collision_Stars_Follow_Total()
    {
        var level = MakeLevel(
            anchors: new MetricAnchors { R = new MetricAnchorValue(0d, 1d) },
            weights: new MetricWeights { R = 1d, D = 0d, P = 0d, Q = 0d });

        Score score = Scorer.Evaluate(level, MakeMetrics(0.97d, 0d, 0d, 0d));

        Assert.False(score.HasCollision);
        Assert.Equal(3, score.Stars);
        Assert.Equal(0.97d, score.Total, 9);
    }

    // ==================================================================
    // D 组：验收 d) 归因定位"最差进口" + 文案
    // ==================================================================

    /// <summary>d1：非 Q 最弱项时，按平均延误最大者定位最差进口。</summary>
    [Fact]
    public void D1_Worst_Approach_By_Delay_When_Worst_Metric_Is_Not_Q()
    {
        // 让 D 单项分明显最低：用近底线锚点，D=58 → 0.05。
        var anchors = new MetricAnchors
        {
            R = new MetricAnchorValue(0.5d, 1.0d),
            D = new MetricAnchorValue(60d, 20d),
            P = new MetricAnchorValue(2.5d, 1.2d),
            Q = new MetricAnchorValue(1.0d, 0.5d),
        };
        Level level = MakeLevel(anchors: anchors);

        ApproachMetrics[] approaches =
        [
            new(0, 1, Delay: 10d, QueueLength: 5, R: 1d, P: 0d),
            new(0, 3, Delay: 50d, QueueLength: 2, R: 1d, P: 0d),
        ];

        // D=58 → 单项分 (60-58)/40 = 0.05（最弱）；其余取好人值。
        Score score = Scorer.Evaluate(level, MakeMetrics(0.98d, 58d, 1.2d, 0.5d, approaches: approaches));

        _output.WriteLine($"[D1] worstMetric={score.WorstMetric} worst=({score.WorstApproachNode},{score.WorstApproachRoad})（期望 D 与 (0,3)）");

        Assert.Equal(MetricKind.D, score.WorstMetric);
        Assert.Equal(0, score.WorstApproachNode);
        Assert.Equal(3, score.WorstApproachRoad);
    }

    /// <summary>d2：最弱项为 Q 时，按峰值排队最长者定位最差进口。</summary>
    [Fact]
    public void D2_Worst_Approach_By_Queue_When_Worst_Metric_Is_Q()
    {
        var anchors = new MetricAnchors
        {
            R = new MetricAnchorValue(0.5d, 1.0d),
            D = new MetricAnchorValue(60d, 20d),
            P = new MetricAnchorValue(2.5d, 1.2d),
            Q = new MetricAnchorValue(1.0d, 0.5d),
        };
        Level level = MakeLevel(anchors: anchors);

        ApproachMetrics[] approaches =
        [
            new(1, 2, Delay: 100d, QueueLength: 1, R: 1d, P: 0d),
            new(2, 4, Delay: 1d, QueueLength: 9, R: 1d, P: 0d),
        ];

        // Q=0.95 → 单项分 (1-0.95)/0.5 = 0.1（最弱）→ 按排队最长者定位。
        Score score = Scorer.Evaluate(level, MakeMetrics(0.98d, 0.5d, 1.2d, 0.95d, approaches: approaches));

        _output.WriteLine($"[D2] worstMetric={score.WorstMetric} worst=({score.WorstApproachNode},{score.WorstApproachRoad})（期望 Q 与 (2,4)）");

        Assert.Equal(MetricKind.Q, score.WorstMetric);
        Assert.Equal(2, score.WorstApproachNode);
        Assert.Equal(4, score.WorstApproachRoad);
    }

    /// <summary>d3：归因至少含「①最弱项 + 最差进口定位句」与「②与上局差值句」；无碰撞时不追加碰撞句。</summary>
    [Fact]
    public void D3_Attribution_Contains_Location_And_Delta_Lines()
    {
        Level level = MakeLevel(anchors: DefaultAnchors());

        ApproachMetrics[] approaches = [new(0, 2, Delay: 30d, QueueLength: 7, R: 0.8d, P: 1d)];
        Score prev = Scorer.Evaluate(level, MakeMetrics(0.80d, 50d, 2.0d, 0.9d, approaches: approaches));
        Score cur = Scorer.Evaluate(level, MakeMetrics(0.95d, 25d, 1.3d, 0.55d, approaches: approaches));

        string[] lines = Scorer.Attribution(prev, cur);
        foreach (string line in lines)
        {
            _output.WriteLine("[D3] " + line);
        }

        Assert.True(lines.Length >= 2, "归因文案至少两句（定位 + 差值）。");
        Assert.Contains("最差进口", lines[0]);
        Assert.Contains("节点 0·道路 2", lines[0]);
        Assert.Contains("与上局相比", lines[1]);
        Assert.DoesNotContain(lines, static s => s.Contains("碰撞"));
    }

    /// <summary>d4：碰撞时追加碰撞说明句（标出冲突转向）。</summary>
    [Fact]
    public void D4_Attribution_Adds_Collision_Line()
    {
        Level level = MakeLevel(anchors: DefaultAnchors());
        Score prev = Scorer.Evaluate(level, MakeMetrics(0.7d, 40d, 1.5d, 0.7d));
        Score cur = Scorer.Evaluate(
            level,
            MakeMetrics(0.9d, 30d, 1.3d, 0.6d, collision: true, collisionTurns: new[] { 2, 5 }));

        string[] lines = Scorer.Attribution(prev, cur);
        foreach (string line in lines)
        {
            _output.WriteLine("[D4] " + line);
        }

        Assert.Contains(lines, static s => s.Contains("碰撞"));
        Assert.Contains(lines, static s => s.Contains("2、5"));
    }

    /// <summary>d5：差值句数值正确——总分 0.70 → 0.90 记为 +0.200 且判为"提升"。</summary>
    [Fact]
    public void D5_Attribution_Reports_Correct_Delta()
    {
        var level = MakeLevel(
            anchors: new MetricAnchors { R = new MetricAnchorValue(0d, 1d) },
            weights: new MetricWeights { R = 1d, D = 0d, P = 0d, Q = 0d });

        Score prev = Scorer.Evaluate(level, MakeMetrics(0.70d, 0d, 0d, 0d));
        Score cur = Scorer.Evaluate(level, MakeMetrics(0.90d, 0d, 0d, 0d));

        string delta = Scorer.Attribution(prev, cur)[1];
        _output.WriteLine("[D5] " + delta);

        Assert.Contains("+0.200", delta);
        Assert.Contains("提升", delta);
    }

    /// <summary>d6：Attribution 前置——prev / cur 为 null 抛 ArgumentNullException。</summary>
    [Fact]
    public void D6_Attribution_Null_Arguments_Throw()
    {
        Level level = MakeLevel(anchors: DefaultAnchors());
        Score score = Scorer.Evaluate(level, MakeMetrics(1d, 0d, 0d, 0d));

        Assert.Throws<ArgumentNullException>(() => Scorer.Attribution(null!, score));
        Assert.Throws<ArgumentNullException>(() => Scorer.Attribution(score, null!));
    }

    // ==================================================================
    // E 组：配置驱动 / 前置 / 确定性 / 冻结面对应
    // ==================================================================

    /// <summary>e1：默认权重 0.4/0.2/0.2/0.2 生效——构造已知单项分，验证加权总分。</summary>
    [Fact]
    public void E1_Default_Weights_Are_Applied()
    {
        // 1-1 锚点：R[0.8,1.0] D[60,20] P[2.5,1.2] Q[1,0.5]
        // 取 R=0.9→0.5、D=30→0.75、P=1.85→0.5、Q=0.75→0.5
        Level level = MakeLevel(anchors: DefaultAnchors(), weights: MetricWeights.Default);
        Score score = Scorer.Evaluate(level, MakeMetrics(0.9d, 30d, 1.85d, 0.75d));

        double expected = (0.4d * 0.5d) + (0.2d * 0.75d) + (0.2d * 0.5d) + (0.2d * 0.5d);   // 0.55
        _output.WriteLine($"[E1] total={score.Total:F6}（期望 {expected}）");

        Assert.Equal(expected, score.Total, 9);
        Assert.Equal(0, score.Stars);   // 0.55 < 0.60
    }

    /// <summary>e2：锚点取自 game_rules（改锚点即改分，证明未硬编码）。</summary>
    [Fact]
    public void E2_Anchors_Come_From_GameRules_Not_Hardcoded()
    {
        MetricsSnapshot metrics = MakeMetrics(0.9d, 30d, 1.85d, 0.75d);

        Score withDefault = Scorer.Evaluate(MakeLevel(anchors: DefaultAnchors()), metrics);

        var tighter = new MetricAnchors
        {
            R = new MetricAnchorValue(0.5d, 0.6d),   // R=0.9 远超 g → 1 分
            D = new MetricAnchorValue(60d, 20d),
            P = new MetricAnchorValue(2.5d, 1.2d),
            Q = new MetricAnchorValue(1.0d, 0.5d),
        };
        Score withTighter = Scorer.Evaluate(MakeLevel(anchors: tighter), metrics);

        double expected = (0.4d * 1.0d) + (0.2d * 0.75d) + (0.2d * 0.5d) + (0.2d * 0.5d);   // 0.75
        _output.WriteLine($"[E2] 默认锚点 total={withDefault.Total:F6}；收紧锚点 total={withTighter.Total:F6}（期望 {expected}）");

        Assert.Equal(0.55d, withDefault.Total, 9);
        Assert.Equal(expected, withTighter.Total, 9);
        Assert.NotEqual(withDefault.Total, withTighter.Total);
    }

    /// <summary>e3：权重取自 game_rules（改权重即改分）。</summary>
    [Fact]
    public void E3_Weights_Come_From_GameRules()
    {
        MetricsSnapshot metrics = MakeMetrics(0.9d, 30d, 1.85d, 0.75d);

        // 只留 R：Total == s_R == 0.5
        var onlyR = MakeLevel(
            anchors: DefaultAnchors(),
            weights: new MetricWeights { R = 1d, D = 0d, P = 0d, Q = 0d });
        Score score = Scorer.Evaluate(onlyR, metrics);

        _output.WriteLine($"[E3] 仅 R 权重 → total={score.Total:F6}（期望 0.5 = s_R）");

        Assert.Equal(score.Breakdown.R, score.Total, 9);
        Assert.Equal(0.5d, score.Total, 9);
    }

    /// <summary>e4：Evaluate 前置——level / metrics 为 null 抛 ArgumentNullException。</summary>
    [Fact]
    public void E4_Evaluate_Null_Arguments_Throw()
    {
        Level level = MakeLevel(anchors: DefaultAnchors());
        MetricsSnapshot metrics = MakeMetrics(1d, 0d, 0d, 0d);

        Assert.Throws<ArgumentNullException>(() => Scorer.Evaluate(null!, metrics));
        Assert.Throws<ArgumentNullException>(() => Scorer.Evaluate(level, (MetricsSnapshot)null!));
    }

    /// <summary>e5：确定性——同输入两次求值，Score 全字段一致；归因文案逐句一致（铁律 2）。</summary>
    [Fact]
    public void E5_Evaluate_And_Attribution_Are_Deterministic()
    {
        Level level = MakeLevel(anchors: DefaultAnchors());
        ApproachMetrics[] approaches =
        [
            new(0, 1, Delay: 20d, QueueLength: 4, R: 0.9d, P: 0.5d),
            new(1, 2, Delay: 55d, QueueLength: 6, R: 0.7d, P: 1.2d),
        ];
        MetricsSnapshot metrics = MakeMetrics(0.9d, 45d, 1.6d, 0.8d, approaches: approaches);

        Score a = Scorer.Evaluate(level, metrics);
        Score b = Scorer.Evaluate(level, metrics);

        Assert.Equal(a.Total, b.Total, 12);
        Assert.Equal(a.Stars, b.Stars);
        Assert.Equal(a.WorstApproachNode, b.WorstApproachNode);
        Assert.Equal(a.WorstApproachRoad, b.WorstApproachRoad);
        Assert.Equal(a.WorstMetric, b.WorstMetric);
        Assert.Equal(a.Breakdown.R, b.Breakdown.R, 12);
        Assert.Equal(a.Breakdown.D, b.Breakdown.D, 12);
        Assert.Equal(a.Breakdown.P, b.Breakdown.P, 12);
        Assert.Equal(a.Breakdown.Q, b.Breakdown.Q, 12);
        Assert.Equal(Scorer.Attribution(a, b), Scorer.Attribution(a, b));
    }

    /// <summary>e6：空指标（零需求）作为首局零基线——无进口道数据时最差进口记 (-1, -1)。</summary>
    [Fact]
    public void E6_Empty_Metrics_Zero_Baseline()
    {
        Level level = MakeLevel(anchors: DefaultAnchors());
        Score score = Scorer.Evaluate(level, MetricsSnapshot.Empty);

        _output.WriteLine($"[E6] Empty → total={score.Total:F4} stars={score.Stars} " +
                          $"worst=({score.WorstApproachNode},{score.WorstApproachRoad})");

        Assert.Equal(-1, score.WorstApproachNode);
        Assert.Equal(-1, score.WorstApproachRoad);
        Assert.InRange(score.Total, 0d, 1d);
    }

    /// <summary>e7：冻结面对应——MetricKind 枚举顺序即契约（R,D,P,Q = 0,1,2,3）。</summary>
    [Fact]
    public void E7_Frozen_Surface_MetricKind_Order()
    {
        Assert.Equal(0, (int)MetricKind.R);
        Assert.Equal(1, (int)MetricKind.D);
        Assert.Equal(2, (int)MetricKind.P);
        Assert.Equal(3, (int)MetricKind.Q);
    }

    /// <summary>e8：公开路径集成——MetricsCollector 产出的快照可直接被 Scorer 消费（不依赖 internal 构造）。</summary>
    [Fact]
    public void E8_Scorer_Consumes_Public_Metrics_Snapshot()
    {
        var collector = new MetricsCollector(MakeNetwork().Compile());

        for (int i = 0; i < 10; i++)
        {
            collector.Record(new SimEvent(SimEventKind.DemandArrived, 0.1 * i, CarIn, i, 1d));
        }

        for (int i = 0; i < 6; i++)
        {
            collector.Record(new SimEvent(SimEventKind.VehiclePassed, 100d + i, CarOut, i, CarTurn));
        }

        MetricsSnapshot metrics = collector.Snapshot();
        Score score = Scorer.Evaluate(MakeLevel(anchors: DefaultAnchors()), metrics);

        _output.WriteLine($"[E8] 通过路径 total={score.Total:F4} stars={score.Stars} R={score.Breakdown.R:F4}");

        Assert.InRange(score.Total, 0d, 1d);
        Assert.InRange(score.Stars, 0, 3);
        Assert.Equal(3, metrics.Classes.Count);
    }

    // ==================================================================
    // 夹具与工具
    // ==================================================================

    private const int CarIn = 0;
    private const int CarOut = 1;
    private const int CarTurn = 0;

    /// <summary>1-1 教学关锚点（游戏设计v2 §6.3 示例；spmap-schema §4.7）。</summary>
    private static MetricAnchors DefaultAnchors() => new()
    {
        R = new MetricAnchorValue(0.8d, 1.0d),
        D = new MetricAnchorValue(60d, 20d),
        P = new MetricAnchorValue(2.5d, 1.2d),
        Q = new MetricAnchorValue(1.0d, 0.5d),
    };

    private static Level MakeLevel(MetricAnchors anchors, MetricWeights? weights = null)
        => new()
        {
            Meta = new LevelMeta { Name = "T2.2 评分", Chapter = 1, Index = 1 },
            GameRules = new GameRules
            {
                Anchors = anchors,
                Weights = weights ?? MetricWeights.Default,
            },
        };

    /// <summary>构造指标快照（经 internal 构造，测试经 InternalsVisibleTo 授权；与 T2.1 同口径）。</summary>
    private static MetricsSnapshot MakeMetrics(
        double r,
        double d,
        double p,
        double q,
        bool collision = false,
        IReadOnlyList<int>? collisionTurns = null,
        IReadOnlyList<ApproachMetrics>? approaches = null)
        => new(
            r,
            d,
            p,
            q,
            spillback: false,
            collision: collision,
            collisionTurns: collisionTurns ?? Array.Empty<int>(),
            classes:
            [
                new ClassMetrics(TrafficClass.Driving, r, d, p, q),
                new ClassMetrics(TrafficClass.Pedestrian, 1d, 0d, 0d, 0d),
                new ClassMetrics(TrafficClass.Bicycle, 1d, 0d, 0d, 0d),
            ],
            approaches: approaches ?? Array.Empty<ApproachMetrics>());

    /// <summary>单信号路口：机动车道 0/1 + 一条直行转向（E8 的公开路径夹具）。</summary>
    private static Network MakeNetwork()
    {
        Node[] nodes =
        [
            new Node { Id = 0, Type = NodeType.Signalized, X = 0d, Y = 0d, Offset = 0d, SignalPlans = [MakePlan()] },
            new Node { Id = 1, Type = NodeType.Origin, X = 0d, Y = -180d },
            new Node { Id = 2, Type = NodeType.Destination, X = 0d, Y = 180d },
        ];

        Lane[] lanes =
        [
            new Lane { Id = CarIn, Road = 1, Type = LaneType.Driving, FromNode = 1, ToNode = 0, Length = 180d, SpeedLimit = 13.89d, LaneIndex = 0 },
            new Lane { Id = CarOut, Road = 1, Type = LaneType.Driving, FromNode = 0, ToNode = 2, Length = 180d, SpeedLimit = 13.89d, LaneIndex = 0 },
        ];

        Turn[] turns =
        [
            new Turn { Id = CarTurn, SrcLane = CarIn, DstLane = CarOut, Node = 0, Type = TurnType.Straight },
        ];

        return new Network { Nodes = nodes, Lanes = lanes, Turns = turns };
    }

    private static SignalPlan MakePlan() => new()
    {
        StartTime = 0d,
        Stages =
        [
            new Stage { Type = StageType.Fixed, Duration = 60d, ProtectedTurnIds = [CarTurn] },
        ],
    };
}
