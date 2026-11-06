using SimCore.Io;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

/// <summary>
/// T1.7「需求生成器（恒定 / 时变 / 脉冲）」验收测试。
/// <para>分组与验收标准逐条对应：
/// <list type="bullet">
///   <item>A 组 → 验收 a) 恒定流率误差 &lt; 1%</item>
///   <item>B 组 → 验收 b) 分段时变曲线按 <c>time_points</c> 切换</item>
///   <item>C 组 → 验收 c) 脉冲只在中段生效（两侧恒为 0）</item>
///   <item>D 组 → 验收 d) 同 seed 注入序列完全一致；并覆盖与冻结接口 §4.8 的对应、纯函数性、参数校验、三类 kind</item>
/// </list>
/// </para>
/// <para>全部断言基于 <see cref="DemandGenerator"/> 的 public 面（§4.8），不触碰任何 internal 钩子。</para>
/// </summary>
public sealed class DemandTests
{
    private const double Dt = 0.5d;

    // ==================================================================
    // A 组：验收 a) 恒定流率误差 < 1%
    // ==================================================================

    /// <summary>a1：恒定 0.5 辆/秒 × 600 秒，到达总数应落在 300 的 1% 以内（且绝对误差 ≤ 1 辆）。</summary>
    [Fact]
    public void A1_ConstantRate_TotalErrorBelowOnePercent()
    {
        Level level = MakeLevel(demand: new[] { Drive(0, new[] { 0d }, new[] { 0.5d }) });
        var gen = new DemandGenerator(level, 20261010L);

        long total = SumArrivals(gen, DemandKind.Driving, 0, 0d, 600d, Dt);
        double expected = 0.5d * 600d;   // = 300

        Assert.True(Math.Abs(total - expected) <= 1d, $"恒定流率绝对误差应 ≤ 1 辆（观测 {total}，期望 {expected}）。");
        Assert.True(RelError(total, expected) < 0.01d, $"恒定流率误差 {RelError(total, expected):P3} 超过 1%（观测 {total}，期望 {expected}）。");
    }

    /// <summary>a2：恒定 0.2 辆/秒 × 600 秒（期望 120 辆），误差同样 &lt; 1%。</summary>
    [Fact]
    public void A2_ConstantRate_LowFlow_ErrorBelowOnePercent()
    {
        Level level = MakeLevel(demand: new[] { Drive(0, new[] { 0d }, new[] { 0.2d }) });
        var gen = new DemandGenerator(level, 424242L);

        long total = SumArrivals(gen, DemandKind.Driving, 0, 0d, 600d, Dt);
        double expected = 0.2d * 600d;   // = 120

        Assert.True(RelError(total, expected) < 0.01d, $"恒定流率误差 {RelError(total, expected):P3} 超过 1%（观测 {total}，期望 {expected}）。");
    }

    /// <summary>a3：到达节奏确实有抖动（不是等间隔"齐步走"），否则无随机可言——检查步内到达数并非处处相同。</summary>
    [Fact]
    public void A3_ArrivalsHaveJitter_NotPerfectlyPeriodic()
    {
        Level level = MakeLevel(demand: new[] { Drive(0, new[] { 0d }, new[] { 0.5d }) });
        var gen = new DemandGenerator(level, 20261010L);

        int[] seq = Sequence(gen, DemandKind.Driving, 0, 0d, 60d, Dt);
        int gaps = 0;
        for (int i = 1; i < seq.Length; i++)
        {
            if (seq[i] != seq[i - 1])
            {
                gaps++;
            }
        }

        Assert.True(gaps > 0, "到达序列不应是完全等间隔（无抖动）。");
    }

    // ==================================================================
    // B 组：验收 b) 分段时变曲线按 time_points 切换
    // ==================================================================

    /// <summary>b1：RateAt 精确命中各时段（含边界时刻归右侧时段、末段延伸到 +∞）。</summary>
    [Fact]
    public void B1_RateAt_SwitchesExactlyAtTimePoints()
    {
        Level level = MakeLevel(demand: new[]
        {
            Drive(0, new[] { 0d, 100d, 200d, 300d }, new[] { 0.1d, 0.3d, 0.2d, 0.05d }),
        });
        var gen = new DemandGenerator(level, 1L);

        Assert.Equal(0.1d, gen.RateAt(DemandKind.Driving, 0, 0d), 12);
        Assert.Equal(0.1d, gen.RateAt(DemandKind.Driving, 0, 99.999d), 12);
        Assert.Equal(0.3d, gen.RateAt(DemandKind.Driving, 0, 100d), 12);      // 边界归右侧时段
        Assert.Equal(0.3d, gen.RateAt(DemandKind.Driving, 0, 199.9d), 12);
        Assert.Equal(0.2d, gen.RateAt(DemandKind.Driving, 0, 200d), 12);
        Assert.Equal(0.05d, gen.RateAt(DemandKind.Driving, 0, 300d), 12);
        Assert.Equal(0.05d, gen.RateAt(DemandKind.Driving, 0, 10_000d), 12);  // 末段延伸到 +∞
    }

    /// <summary>b2：各时段的到达总数等于该时段"流率 × 时长"（±1 辆），即积分随 time_points 正确切换。</summary>
    [Fact]
    public void B2_PiecewiseTotalsFollowTimePoints()
    {
        Level level = MakeLevel(demand: new[]
        {
            Drive(0, new[] { 0d, 100d, 200d, 300d }, new[] { 0.1d, 0.3d, 0.2d, 0.05d }),
        });
        var gen = new DemandGenerator(level, 88L);

        long seg1 = SumArrivals(gen, DemandKind.Driving, 0, 0d, 100d, Dt);      // 0.1 × 100 = 10
        long seg2 = SumArrivals(gen, DemandKind.Driving, 0, 100d, 200d, Dt);    // 0.3 × 100 = 30
        long seg3 = SumArrivals(gen, DemandKind.Driving, 0, 200d, 300d, Dt);    // 0.2 × 100 = 20
        long seg4 = SumArrivals(gen, DemandKind.Driving, 0, 300d, 400d, Dt);    // 0.05 × 100 = 5

        Assert.True(Math.Abs(seg1 - 10d) <= 1d, $"第 1 段到达 {seg1}，期望 ≈ 10。");
        Assert.True(Math.Abs(seg2 - 30d) <= 1d, $"第 2 段到达 {seg2}，期望 ≈ 30。");
        Assert.True(Math.Abs(seg3 - 20d) <= 1d, $"第 3 段到达 {seg3}，期望 ≈ 20。");
        Assert.True(Math.Abs(seg4 - 5d) <= 1d, $"第 4 段到达 {seg4}，期望 ≈ 5。");
    }

    /// <summary>b3：`time` 早于首个 time_points → 流率与到达数均为 0（§4.8 明文）。</summary>
    [Fact]
    public void B3_BeforeFirstTimePoint_IsZero()
    {
        Level level = MakeLevel(demand: new[] { Drive(0, new[] { 10d, 20d }, new[] { 0.5d, 0.2d }) });
        var gen = new DemandGenerator(level, 3L);

        Assert.Equal(0d, gen.RateAt(DemandKind.Driving, 0, 0d), 12);
        Assert.Equal(0d, gen.RateAt(DemandKind.Driving, 0, 9.999d), 12);
        Assert.Equal(0L, SumArrivals(gen, DemandKind.Driving, 0, 0d, 10d, Dt));
        Assert.Equal(0.5d, gen.RateAt(DemandKind.Driving, 0, 10d), 12);   // 首个时段起点起生效
    }

    // ==================================================================
    // C 组：验收 c) 脉冲只在中段生效
    // ==================================================================

    /// <summary>c1：脉冲曲线 [0,200)=0 → [200,400)=0.5 → [400,600]=0；只有中段产生到达。</summary>
    [Fact]
    public void C1_Pulse_OnlyActiveInMiddleSegment()
    {
        Level level = MakeLevel(demand: new[]
        {
            Drive(0, new[] { 0d, 200d, 400d, 600d }, new[] { 0d, 0.5d, 0d, 0d }),
        });
        var gen = new DemandGenerator(level, 7L);

        long before = SumArrivals(gen, DemandKind.Driving, 0, 0d, 200d, Dt);
        long middle = SumArrivals(gen, DemandKind.Driving, 0, 200d, 400d, Dt);
        long after = SumArrivals(gen, DemandKind.Driving, 0, 400d, 600d, Dt);

        Assert.Equal(0L, before);   // 脉冲前：恒 0
        Assert.Equal(0L, after);    // 脉冲后：恒 0
        Assert.True(middle > 0, "脉冲中段应产生到达。");
        Assert.True(RelError(middle, 0.5d * 200d) < 0.01d, $"脉冲中段到达 {middle}，期望 ≈ 100（误差 <1%）。");

        // 时刻查表：两侧恒 0，中段恒 0.5
        Assert.Equal(0d, gen.RateAt(DemandKind.Driving, 0, 0d), 12);
        Assert.Equal(0d, gen.RateAt(DemandKind.Driving, 0, 199.999d), 12);
        Assert.Equal(0.5d, gen.RateAt(DemandKind.Driving, 0, 200d), 12);
        Assert.Equal(0.5d, gen.RateAt(DemandKind.Driving, 0, 399.999d), 12);
        Assert.Equal(0d, gen.RateAt(DemandKind.Driving, 0, 400d), 12);
        Assert.Equal(0d, gen.RateAt(DemandKind.Driving, 0, 5_000d), 12);
    }

    /// <summary>c2：脉冲窗口外的每一步到达数都严格为 0（逐点检查，而非只查总数）。</summary>
    [Fact]
    public void C2_Pulse_EveryStepOutsideWindowIsZero()
    {
        Level level = MakeLevel(demand: new[]
        {
            Drive(0, new[] { 0d, 100d, 140d, 600d }, new[] { 0d, 2d, 0d, 0d }),
        });
        var gen = new DemandGenerator(level, 55L);

        for (int i = 0; i < 200; i++)               // t ∈ [0,100)
        {
            double t = i * Dt;
            Assert.Equal(0, gen.DrawArrivals(DemandKind.Driving, 0, t, Dt));
        }

        for (int i = 280; i < 1200; i++)            // t ∈ [140,600)
        {
            double t = i * Dt;
            Assert.Equal(0, gen.DrawArrivals(DemandKind.Driving, 0, t, Dt));
        }
    }

    // ==================================================================
    // D 组：验收 d) 同 seed 完全一致；纯函数性 / 参数校验 / 三类 kind
    // ==================================================================

    /// <summary>d1：同 seed 的两个独立实例，逐步到达序列逐位相同。</summary>
    [Fact]
    public void D1_SameSeed_TwoInstancesIdenticalSequence()
    {
        Level level = MakeLevel(demand: new[]
        {
            Drive(0, new[] { 0d, 120d, 240d }, new[] { 0.4d, 0.7d, 0.2d }),
        });

        var a = new DemandGenerator(level, 12345L);
        var b = new DemandGenerator(level, 12345L);

        int[] sa = Sequence(a, DemandKind.Driving, 0, 0d, 400d, Dt);
        int[] sb = Sequence(b, DemandKind.Driving, 0, 0d, 400d, Dt);

        Assert.Equal(sa, sb);
        Assert.True(sa.Length > 0, "序列不应为空。");
    }

    /// <summary>d2：同实例重复调用（含乱序 / 重复）结果一致——DrawArrivals 是无状态纯函数。</summary>
    [Fact]
    public void D2_DrawArrivals_IsStatelessPureFunction()
    {
        Level level = MakeLevel(demand: new[] { Drive(0, new[] { 0d }, new[] { 0.5d }) });
        var gen = new DemandGenerator(level, 999L);

        int first = gen.DrawArrivals(DemandKind.Driving, 0, 50d, Dt);
        int again = gen.DrawArrivals(DemandKind.Driving, 0, 50d, Dt);
        int later = gen.DrawArrivals(DemandKind.Driving, 0, 250d, Dt);
        int firstRepeat = gen.DrawArrivals(DemandKind.Driving, 0, 50d, Dt);

        Assert.Equal(first, again);
        Assert.Equal(first, firstRepeat);   // 中间夹了别的调用也不影响
        Assert.Equal(gen.DrawArrivals(DemandKind.Driving, 0, 250d, Dt), later);
    }

    /// <summary>d3：可加性——[t,t+2dt) 一次调用 == [t,t+dt) 与 [t+dt,t+2dt) 两次调用之和（无浮点漂移）。</summary>
    [Fact]
    public void D3_AdditiveOverAdjacentSteps()
    {
        Level level = MakeLevel(demand: new[] { Drive(0, new[] { 0d, 60d }, new[] { 0.6d, 0.9d }) });
        var gen = new DemandGenerator(level, 31415L);

        const double t = 37.5d;
        int single = gen.DrawArrivals(DemandKind.Driving, 0, t, 2d * Dt);
        int halfA = gen.DrawArrivals(DemandKind.Driving, 0, t, Dt);
        int halfB = gen.DrawArrivals(DemandKind.Driving, 0, t + Dt, Dt);

        Assert.Equal(single, halfA + halfB);
    }

    /// <summary>d4：Reset(seed) 回到同 seed → 序列复原；换 seed → 序列改变（含 Reset 后再次换回）。</summary>
    [Fact]
    public void D4_Reset_ReproducesBySeed()
    {
        Level level = MakeLevel(demand: new[]
        {
            Drive(0, new[] { 0d, 150d, 300d }, new[] { 0.5d, 0.8d, 0.3d }),
        });
        var gen = new DemandGenerator(level, 7L);

        int[] s7 = Sequence(gen, DemandKind.Driving, 0, 0d, 400d, Dt);

        gen.Reset(42L);
        int[] s42 = Sequence(gen, DemandKind.Driving, 0, 0d, 400d, Dt);

        gen.Reset(7L);
        int[] s7Again = Sequence(gen, DemandKind.Driving, 0, 0d, 400d, Dt);

        Assert.Equal(s7, s7Again);      // 同 seed 完全一致
        Assert.NotEqual(s7, s42);       // 换 seed 后注入序列不再相同
    }

    /// <summary>d5：不同 seed 至少改变一个目标的注入序列（用多目标放大差异，避免单点巧合）。</summary>
    [Fact]
    public void D5_DifferentSeeds_ChangeAtLeastOneTarget()
    {
        var plans = new DemandPlan[16];
        for (int lane = 0; lane < plans.Length; lane++)
        {
            plans[lane] = Drive(lane, new[] { 0d }, new[] { 0.3d });
        }

        Level level = MakeLevel(demand: plans);
        var a = new DemandGenerator(level, 11L);
        var b = new DemandGenerator(level, 22L);

        // 注意：种子改变的是到达的**时间落点**（落在哪一步），而非常数流率下的累计总数
        // —— 当 Λ(to) 恰为整数时累计数必等，故此处比较逐步序列而非总数。
        bool anyDiff = false;
        for (int lane = 0; lane < plans.Length && !anyDiff; lane++)
        {
            int[] sa = Sequence(a, DemandKind.Driving, lane, 0d, 300d, Dt);
            int[] sb = Sequence(b, DemandKind.Driving, lane, 0d, 300d, Dt);
            Assert.Equal(sa.Length, sb.Length);
            if (!sa.AsSpan().SequenceEqual(sb))
            {
                anyDiff = true;
            }
        }

        Assert.True(anyDiff, "不同种子应改变到达的时间落点（逐步注入序列）。");
    }

    /// <summary>d6：三类 kind 各自独立、按 §4.8 的 id 空间取值（Driving→车道 / Pedestrian→人行横道 / Bicycle→非机动车道）。</summary>
    [Fact]
    public void D6_ThreeKindsUseIndependentIdSpaces()
    {
        Level level = MakeLevel(
            demand: new[] { Drive(0, new[] { 0d }, new[] { 0.5d }) },
            ped: new[] { Pedestrian(12, new[] { 0d }, new[] { 0.2d }) },
            bike: new[] { Bicycle(3, new[] { 0d }, new[] { 0.1d }) });

        var gen = new DemandGenerator(level, 5L);

        Assert.Equal(0.5d, gen.RateAt(DemandKind.Driving, 0, 10d), 12);
        Assert.Equal(0.2d, gen.RateAt(DemandKind.Pedestrian, 12, 10d), 12);
        Assert.Equal(0.1d, gen.RateAt(DemandKind.Bicycle, 3, 10d), 12);

        // id 空间互不串味
        Assert.Equal(0d, gen.RateAt(DemandKind.Driving, 12, 10d), 12);
        Assert.Equal(0d, gen.RateAt(DemandKind.Pedestrian, 0, 10d), 12);
        Assert.Equal(0d, gen.RateAt(DemandKind.Bicycle, 0, 10d), 12);

        // [0,100) 到达数分别 ≈ 50 / 20 / 10（±1）
        Assert.True(Math.Abs(SumArrivals(gen, DemandKind.Driving, 0, 0d, 100d, Dt) - 50d) <= 1d);
        Assert.True(Math.Abs(SumArrivals(gen, DemandKind.Pedestrian, 12, 0d, 100d, Dt) - 20d) <= 1d);
        Assert.True(Math.Abs(SumArrivals(gen, DemandKind.Bicycle, 3, 0d, 100d, Dt) - 10d) <= 1d);
    }

    /// <summary>d7：前置校验——level 为 null / time 或 dt 为负、NaN、无穷 → 抛异常；无效目标 / dt=0 → 0。</summary>
    [Fact]
    public void D7_PreconditionsAndEdgeCases()
    {
        Level level = MakeLevel(demand: new[] { Drive(0, new[] { 0d }, new[] { 0.5d }) });
        var gen = new DemandGenerator(level, 1L);

        Assert.Throws<ArgumentNullException>(() => new DemandGenerator(null!, 1L));

        Assert.Throws<ArgumentOutOfRangeException>(() => gen.RateAt(DemandKind.Driving, 0, -1d));
        Assert.Throws<ArgumentOutOfRangeException>(() => gen.RateAt(DemandKind.Driving, 0, double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => gen.RateAt(DemandKind.Driving, 0, double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => gen.DrawArrivals(DemandKind.Driving, 0, -1d, Dt));
        Assert.Throws<ArgumentOutOfRangeException>(() => gen.DrawArrivals(DemandKind.Driving, 0, 0d, -Dt));
        Assert.Throws<ArgumentOutOfRangeException>(() => gen.DrawArrivals(DemandKind.Driving, 0, double.NaN, Dt));
        Assert.Throws<ArgumentOutOfRangeException>(() => gen.DrawArrivals(DemandKind.Driving, 0, 0d, double.PositiveInfinity));

        Assert.Equal(0, gen.DrawArrivals(DemandKind.Driving, 0, 10d, 0d));      // dt = 0 → 0
        Assert.Equal(0d, gen.RateAt(DemandKind.Driving, 99, 10d), 12);          // 无该目标的需求 → 0
        Assert.Equal(0, gen.DrawArrivals(DemandKind.Driving, 99, 10d, Dt));     // 无该目标的需求 → 0
    }

    /// <summary>d8：空关卡（无任何需求曲线）不报错，一切流率 / 到达数恒为 0。</summary>
    [Fact]
    public void D8_EmptyLevel_YieldsZeroDemand()
    {
        var gen = new DemandGenerator(new Level(), 1L);

        Assert.Equal(0d, gen.RateAt(DemandKind.Driving, 0, 100d), 12);
        Assert.Equal(0d, gen.RateAt(DemandKind.Pedestrian, 0, 100d), 12);
        Assert.Equal(0d, gen.RateAt(DemandKind.Bicycle, 0, 100d), 12);
        Assert.Equal(0, gen.DrawArrivals(DemandKind.Driving, 0, 100d, Dt));
        Assert.Equal(0L, SumArrivals(gen, DemandKind.Driving, 0, 0d, 600d, Dt));
    }

    // ==================================================================
    // 夹具与工具
    // ==================================================================

    private static Level MakeLevel(
        DemandPlan[]? demand = null,
        PedestrianDemand[]? ped = null,
        BicycleDemand[]? bike = null) => new()
        {
            Demand = demand ?? Array.Empty<DemandPlan>(),
            Pedestrians = ped ?? Array.Empty<PedestrianDemand>(),
            Bicycles = bike ?? Array.Empty<BicycleDemand>(),
        };

    private static DemandPlan Drive(int lane, double[] timePoints, double[] flowRates) => new()
    {
        Lane = lane,
        TimePoints = timePoints,
        FlowRates = flowRates,
    };

    private static PedestrianDemand Pedestrian(int crosswalkTurn, double[] timePoints, double[] flowRates) => new()
    {
        CrosswalkTurn = crosswalkTurn,
        TimePoints = timePoints,
        FlowRates = flowRates,
    };

    private static BicycleDemand Bicycle(int lane, double[] timePoints, double[] flowRates) => new()
    {
        Lane = lane,
        Mode = BicycleMode.Independent,
        TimePoints = timePoints,
        FlowRates = flowRates,
    };

    /// <summary>以固定步长 <paramref name="dt"/> 在 <c>[from, to)</c> 内逐步累加到达数。</summary>
    private static long SumArrivals(DemandGenerator gen, DemandKind kind, int targetId, double from, double to, double dt)
    {
        long total = 0L;
        int steps = (int)Math.Round((to - from) / dt);
        for (int i = 0; i < steps; i++)
        {
            total += gen.DrawArrivals(kind, targetId, from + (i * dt), dt);
        }

        return total;
    }

    /// <summary>逐步采集 <c>[from, to)</c> 的到达数序列（供"逐位相同"断言）。</summary>
    private static int[] Sequence(DemandGenerator gen, DemandKind kind, int targetId, double from, double to, double dt)
    {
        int steps = (int)Math.Round((to - from) / dt);
        var result = new int[steps];
        for (int i = 0; i < steps; i++)
        {
            result[i] = gen.DrawArrivals(kind, targetId, from + (i * dt), dt);
        }

        return result;
    }

    private static double RelError(long observed, double expected) => Math.Abs(observed - expected) / expected;
}
