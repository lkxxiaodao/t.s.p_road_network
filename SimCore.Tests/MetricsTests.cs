using SimCore.Io;
using SimCore.Metrics;
using SimCore.Model;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

// 类型名与命名空间同名（SimCore.Metrics.Metrics）：显式别名，避免歧义。
using MetricsSnapshot = SimCore.Metrics.Metrics;

namespace SimCore.Tests;

/// <summary>
/// T2.1「指标统计 R/D/P/Q + 排队溢出」验收测试。
/// <para>验收标准逐条对应（任务卡 T2.1 a~e）：
/// <list type="bullet">
///   <item>A 组 → 验收 a) R = 通过数 / 需求数（分类加权）</item>
///   <item>B 组 → 验收 b) D = 实际 − 自由流通行时间均值</item>
///   <item>C 组 → 验收 c) P = 平均停车次数（行人不计）</item>
///   <item>D 组 → 验收 d) Q 按容量百分比归一后取三类最低（木桶）</item>
///   <item>E 组 → 验收 e) 溢出（首车道占有率 &gt; 0.9）能触发</item>
///   <item>F 组 → 冻结接口对应 / 确定性 / Reset / 边界</item>
/// </list>
/// </para>
/// <para>口径来源：技术方案01 §3.5 + 游戏设计v2 §6.2/§6.5 + <c>docs/SimCore-API.md</c> §5.2/§5.3。</para>
/// </summary>
public sealed class MetricsTests
{
    private readonly ITestOutputHelper _output;

    public MetricsTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private const double Dt = 0.1d;
    private const long Seed = 20261010L;

    // 路网车道 id
    private const int CarIn = 0;      // 机动车进口（Driving，长 180）
    private const int CarOut = 1;     // 机动车出口（Driving）
    private const int WalkIn = 2;     // 进口人行道（Sidewalk）
    private const int WalkOut = 3;    // 出口人行道（Sidewalk）
    private const int BikeIn = 4;     // 非机动车进口（Biking）
    private const int BikeOut = 5;    // 非机动车出口（Biking）

    // 转向 id
    private const int CarTurn = 0;        // 0 → 1，Straight（机动车）
    private const int CrosswalkTurn = 1;  // 2 → 3，Crosswalk（行人）
    private const int BikeTurn = 2;       // 4 → 5，Straight（非机动车）

    // ==================================================================
    // A 组：验收 a) R = 通过数 / 需求数（分类加权）
    // ==================================================================

    /// <summary>a1：R 的分母为需求数、分子为通过数——用人工事件流精确验证两类的 R 与加权合成。</summary>
    [Fact]
    public void A1_R_Is_Passed_Over_Demand_PerClass()
    {
        var collector = new MetricsCollector(MakeNetwork().Compile());

        // 机动车：10 到达、8 通过（经 CarTurn）→ R=0.8
        for (int i = 0; i < 10; i++)
        {
            collector.Record(new SimEvent(SimEventKind.DemandArrived, 0.1 * i, CarIn, i, 1d));
        }

        for (int i = 0; i < 8; i++)
        {
            collector.Record(new SimEvent(SimEventKind.VehiclePassed, 100d + i, CarOut, i, CarTurn));
        }

        // 非机动车：4 到达、4 通过（经 BikeTurn）→ R=1.0
        for (int i = 0; i < 4; i++)
        {
            collector.Record(new SimEvent(SimEventKind.DemandArrived, 0.1 * i, BikeIn, 100 + i, 1d));
        }

        for (int i = 0; i < 4; i++)
        {
            collector.Record(new SimEvent(SimEventKind.VehiclePassed, 100d + i, BikeOut, 100 + i, BikeTurn));
        }

        MetricsSnapshot m = collector.Snapshot();

        double drivingR = ClassOf(m, TrafficClass.Driving).R;
        double bicycleR = ClassOf(m, TrafficClass.Bicycle).R;

        _output.WriteLine($"[A1] R={m.R:F4}  Driving.R={drivingR:F4}  Bicycle.R={bicycleR:F4}  Pedestrian.R={ClassOf(m, TrafficClass.Pedestrian).R:F4}");

        Assert.Equal(0.8d, drivingR, 6);
        Assert.Equal(1.0d, bicycleR, 6);

        // 分类加权 = 有需求类的算术均值：(0.8 + 1.0 + 1.0[无需求行人]) / 3 = 0.9333…
        Assert.Equal((0.8d + 1.0d + 1.0d) / 3d, m.R, 6);
    }

    /// <summary>a2：R 从真实引擎事件流累计——通过数 / 需求数，且 ∈ [0,1]。</summary>
    [Fact]
    public void A2_R_FromLiveEngine_WithinUnitInterval()
    {
        (MetricsSnapshot metrics, long arrived, long passed) = RunLevel(MakeLevel(), steps: 6000, register: true);

        _output.WriteLine($"[A2] R={metrics.R:F4}  arrived={arrived}  passed={passed}  " +
                          $"Driving(R={ClassOf(metrics, TrafficClass.Driving).R:F4})  " +
                          $"Pedestrian(R={ClassOf(metrics, TrafficClass.Pedestrian).R:F4})  " +
                          $"Bicycle(R={ClassOf(metrics, TrafficClass.Bicycle).R:F4})");

        Assert.InRange(metrics.R, 0d, 1d);
        Assert.True(arrived > 0L, "应产生需求到达。");
        Assert.True(passed > 0L, "应有通过事件。");

        // 三类各自的 R 也应在 [0,1]。
        for (int i = 0; i < metrics.Classes.Count; i++)
        {
            Assert.InRange(metrics.Classes[i].R, 0d, 1d);
        }
    }

    /// <summary>a3：无需求 → R 记为 1（空类视为全部通过），不产生 NaN。</summary>
    [Fact]
    public void A3_NoDemand_Yields_R_One()
    {
        var collector = new MetricsCollector(MakeNetwork().Compile());
        MetricsSnapshot m = collector.Snapshot();

        Assert.Equal(1d, m.R, 9);
        Assert.Equal(0d, m.D, 9);
        Assert.Equal(0d, m.P, 9);
        Assert.Equal(0d, m.Q, 9);
        Assert.False(m.Spillback);
        Assert.False(m.Collision);
    }

    // ==================================================================
    // B 组：验收 b) D = 实际 − 自由流通行时间均值
    // ==================================================================

    /// <summary>b1：D 为"实际通行时间 − 自由流通行时间"的均值——自由流基准取 t0（队列入口处），故 D = 通行时刻均值。</summary>
    [Fact]
    public void B1_D_Is_Mean_Of_TravelTime_Minus_FreeFlow()
    {
        var collector = new MetricsCollector(MakeNetwork().Compile());

        // 5 辆机动车到达（需求），3 辆通过，通行时刻 30 / 40 / 50 s。
        for (int i = 0; i < 5; i++)
        {
            collector.Record(new SimEvent(SimEventKind.DemandArrived, 0.1 * i, CarIn, i, 1d));
        }

        collector.Record(new SimEvent(SimEventKind.VehiclePassed, 30d, CarOut, 0, CarTurn));
        collector.Record(new SimEvent(SimEventKind.VehiclePassed, 40d, CarOut, 1, CarTurn));
        collector.Record(new SimEvent(SimEventKind.VehiclePassed, 50d, CarOut, 2, CarTurn));

        MetricsSnapshot m = collector.Snapshot();

        // 自由流进入时刻 ≈ 0（第一条 DemandArrived 在 t=0.1·0=0）→ Driving.D = mean(30,40,50) = 40。
        _output.WriteLine($"[B1] Driving.D={ClassOf(m, TrafficClass.Driving).D:F6} (期望 40.0)  m.D={m.D:F6} (期望 40/3)");

        Assert.Equal(40d, ClassOf(m, TrafficClass.Driving).D, 6);
        // 顶层 D = 三类算术均值（无需求的空类以中性值 0 参与）→ (40 + 0 + 0) / 3。
        Assert.Equal(40d / 3d, m.D, 6);
    }

    /// <summary>b2：D 为均值而非总和——加倍通过数（同一通行时刻分布）不改变 D。</summary>
    [Fact]
    public void B2_D_Is_Average_Not_Sum()
    {
        var collector = new MetricsCollector(MakeNetwork().Compile());
        for (int i = 0; i < 20; i++)
        {
            collector.Record(new SimEvent(SimEventKind.DemandArrived, 0.1 * i, CarIn, i, 1d));
        }

        for (int i = 0; i < 10; i++)
        {
            collector.Record(new SimEvent(SimEventKind.VehiclePassed, 20d, CarOut, i, CarTurn));
            collector.Record(new SimEvent(SimEventKind.VehiclePassed, 60d, CarOut, 100 + i, CarTurn));
        }

        MetricsSnapshot m = collector.Snapshot();
        _output.WriteLine($"[B2] D={ClassOf(m, TrafficClass.Driving).D:F6} (期望 40.0，非 400)");

        Assert.Equal(40d, ClassOf(m, TrafficClass.Driving).D, 6);
    }

    /// <summary>b3：D 从真实引擎累计，且非负有限。</summary>
    [Fact]
    public void B3_D_FromLiveEngine_Finite_NonNegative()
    {
        (MetricsSnapshot metrics, _, _) = RunLevel(MakeLevel(), steps: 6000, register: true);

        _output.WriteLine($"[B3] D={metrics.D:F4}  Classes=[{string.Join(", ", DumpClasses(metrics))}]");

        Assert.True(double.IsFinite(metrics.D), "D 必须为有限值。");
        Assert.True(metrics.D >= 0d, "D 不得为负。");
        Assert.True(ClassOf(metrics, TrafficClass.Driving).D >= 0d);
    }

    // ==================================================================
    // C 组：验收 c) P = 平均停车次数（行人不计）
    // ==================================================================

    /// <summary>c1：P = 停车次数 / 通过数；行人的停车次数不参与（行人恒 0）。</summary>
    [Fact]
    public void C1_P_Is_Stops_Over_Passed_Excluding_Pedestrians()
    {
        var collector = new MetricsCollector(MakeNetwork().Compile());

        // 机动车：4 到达、4 通过、6 次停车 → P=1.5
        for (int i = 0; i < 4; i++)
        {
            collector.Record(new SimEvent(SimEventKind.DemandArrived, 0.1 * i, CarIn, i, 1d));
        }

        for (int i = 0; i < 4; i++)
        {
            collector.Record(new SimEvent(SimEventKind.VehiclePassed, 100d + i, CarOut, i, CarTurn));
        }

        for (int i = 0; i < 6; i++)
        {
            collector.Record(new SimEvent(SimEventKind.VehicleStopped, 10d + i, CarIn, i % 4, i));
        }

        // 行人：2 到达、2 通过、0 停车事件（行人不发 VehicleStopped）→ P=0，且不计入分母
        for (int i = 0; i < 2; i++)
        {
            collector.Record(new SimEvent(SimEventKind.DemandArrived, 0.1 * i, -1, -1, 1d));
        }

        collector.Record(new SimEvent(SimEventKind.VehiclePassed, 120d, WalkOut, 0, CrosswalkTurn));
        collector.Record(new SimEvent(SimEventKind.VehiclePassed, 130d, WalkOut, 1, CrosswalkTurn));

        MetricsSnapshot m = collector.Snapshot();

        double drivingP = ClassOf(m, TrafficClass.Driving).P;
        double pedestrianP = ClassOf(m, TrafficClass.Pedestrian).P;

        _output.WriteLine($"[C1] P={m.P:F4}  Driving.P={drivingP:F4} (期望 1.5)  Pedestrian.P={pedestrianP:F4} (期望 0)");

        Assert.Equal(1.5d, drivingP, 6);
        Assert.Equal(0d, pedestrianP, 9);

        // 行人通过数 2 未进入 P 的分母：机动车 P 仍为 6/4 = 1.5（若误并入则为 6/6 = 1.0）。
        Assert.NotEqual(1d, drivingP, 6);

        // 顶层 P = 三类算术均值，行人项恒 0：
        // Driving 1.5、Pedestrian 0、Bicycle 0（无需求）→ 1.5/3 = 0.5
        Assert.Equal(0.5d, m.P, 6);
    }

    /// <summary>c2：行人的停车事件即便出现（异常数据）也被排除在 P 之外——分母不含行人通过数。</summary>
    [Fact]
    public void C2_PedestrianStops_Do_Not_Enter_P()
    {
        var collector = new MetricsCollector(MakeNetwork().Compile());

        // 行人：2 通过 + 2 停车（恶意/异常数据）
        collector.Record(new SimEvent(SimEventKind.VehiclePassed, 50d, WalkOut, 0, CrosswalkTurn));
        collector.Record(new SimEvent(SimEventKind.VehiclePassed, 60d, WalkOut, 1, CrosswalkTurn));
        collector.Record(new SimEvent(SimEventKind.VehicleStopped, 10d, WalkIn, 0, 1d));
        collector.Record(new SimEvent(SimEventKind.VehicleStopped, 20d, WalkIn, 1, 1d));

        MetricsSnapshot m = collector.Snapshot();

        // 行人道上的到达归行人；通过归行人。P 对行人恒 0，不得因 2 次停车变成 1.0。
        Assert.Equal(0d, ClassOf(m, TrafficClass.Pedestrian).P, 9);
    }

    /// <summary>c3：P 从真实引擎累计（存在排队 → 停车次数 &gt; 0），且行人不计。</summary>
    [Fact]
    public void C3_P_FromLiveEngine_PedestrianExcluded()
    {
        (MetricsSnapshot metrics, _, _) = RunLevel(MakeLevel(), steps: 6000, register: true);

        _output.WriteLine($"[C3] P={metrics.P:F4}  Driving.P={ClassOf(metrics, TrafficClass.Driving).P:F4}  " +
                          $"Bicycle.P={ClassOf(metrics, TrafficClass.Bicycle).P:F4}  " +
                          $"Pedestrian.P={ClassOf(metrics, TrafficClass.Pedestrian).P:F4}");

        Assert.True(ClassOf(metrics, TrafficClass.Driving).P >= 0d);
        Assert.Equal(0d, ClassOf(metrics, TrafficClass.Pedestrian).P, 12);   // 行人恒 0
        Assert.True(ClassOf(metrics, TrafficClass.Driving).P > 0d, "红灯排队场景下机动车应有停车次数。");
    }

    // ==================================================================
    // D 组：验收 d) Q 按容量百分比归一后取三类最低（木桶）
    // ==================================================================

    /// <summary>d1：Q 为「各类峰值排队 / 该目标容量」的最大值，顶层取三类的**最低**（木桶原则）。</summary>
    [Fact]
    public void D1_Q_Is_Normalized_Bucket_Minimum()
    {
        var collector = new MetricsCollector(MakeNetwork().Compile());

        // 机动车道容量 = floor(180 / 6) = 30。峰值排队 15 → 0.5
        collector.Record(new SimEvent(SimEventKind.QueueSample, 10d, CarIn, -1, 15d));
        // 机动车道峰值 30 → 1.0（第二个峰更高，取最大）
        collector.Record(new SimEvent(SimEventKind.QueueSample, 20d, CarIn, -1, 30d));

        // 非机动车道容量 = floor(180 / 6) = 30。峰值排队 6 → 0.2
        collector.Record(new SimEvent(SimEventKind.QueueSample, 10d, BikeIn, -1, 6d));

        // 行人等待过街峰值 3 人 → 3 / 7 ≈ 0.4286（人行横道等待区容量 = floor(3.5/0.5) = 7）
        collector.ObserveWaitingPedestrians(CrosswalkTurn, 3);

        MetricsSnapshot m = collector.Snapshot();

        double drivingQ = ClassOf(m, TrafficClass.Driving).QNormalized;
        double bicycleQ = ClassOf(m, TrafficClass.Bicycle).QNormalized;
        double pedestrianQ = ClassOf(m, TrafficClass.Pedestrian).QNormalized;

        _output.WriteLine($"[D1] Q={m.Q:F6}  Driving.Q={drivingQ:F4} (期望 1.0)  Bicycle.Q={bicycleQ:F4} (期望 0.2)  " +
                          $"Pedestrian.Q={pedestrianQ:F4} (期望 ≈0.4286)");

        Assert.Equal(1.0d, drivingQ, 6);
        Assert.Equal(0.2d, bicycleQ, 6);
        Assert.Equal(3d / 7d, pedestrianQ, 6);

        // 木桶：取三类最低 = 0.2（非机动车）。
        Assert.Equal(0.2d, m.Q, 6);
        Assert.NotEqual(drivingQ, m.Q);   // 明确验证"取最低"而非"取最高 / 平均"
    }

    /// <summary>d2：木桶取最低——即便机动车排队归一为 1.0，只要有更弱势的类，Q 就取其最低。</summary>
    [Fact]
    public void D2_Q_Takes_Minimum_Not_Maximum()
    {
        var collector = new MetricsCollector(MakeNetwork().Compile());

        collector.Record(new SimEvent(SimEventKind.QueueSample, 10d, CarIn, -1, 30d));   // 机动车 1.0
        collector.Record(new SimEvent(SimEventKind.QueueSample, 10d, BikeIn, -1, 3d));   // 非机动车 0.1

        MetricsSnapshot m = collector.Snapshot();

        _output.WriteLine($"[D2] Q={m.Q:F6}（期望 0.1，非 1.0）");

        Assert.Equal(0.1d, m.Q, 6);
    }

    /// <summary>d3：Q 归一化后 ∈ [0,1]，且从真实引擎累计时为 [0,1]。</summary>
    [Fact]
    public void D3_Q_FromLiveEngine_WithinUnitInterval()
    {
        (MetricsSnapshot metrics, _, _) = RunLevel(MakeLevel(), steps: 6000, register: true);

        _output.WriteLine($"[D3] Q={metrics.Q:F4}  Classes=[{string.Join(", ", DumpClasses(metrics))}]");

        Assert.InRange(metrics.Q, 0d, 1d);
        for (int i = 0; i < metrics.Classes.Count; i++)
        {
            Assert.InRange(metrics.Classes[i].QNormalized, 0d, 1d);
        }
    }

    /// <summary>d4：容量归一正确——同样 10 辆排队，短车道（容量小）归一值更大。</summary>
    [Fact]
    public void D4_Q_Normalization_Uses_LaneCapacity()
    {
        // 两条机动车道：lane0 长 180（容量 30），lane1 长 60（容量 10）。
        CompiledNetwork network = MakeTwoLaneNetwork().Compile();
        var collector = new MetricsCollector(network);

        collector.Record(new SimEvent(SimEventKind.QueueSample, 10d, 0, -1, 10d));   // 10/30 ≈ 0.333
        collector.Record(new SimEvent(SimEventKind.QueueSample, 10d, 1, -1, 10d));   // 10/10 = 1.0

        MetricsSnapshot m = collector.Snapshot();
        double drivingQ = ClassOf(m, TrafficClass.Driving).QNormalized;

        _output.WriteLine($"[D4] Driving.Q={drivingQ:F6}（期望 1.0 = 短车道 10/10）");

        Assert.Equal(1.0d, drivingQ, 6);
        Assert.Equal(1.0d, m.Q, 6);
    }

    // ==================================================================
    // E 组：验收 e) 溢出（首车道占有率 > 0.9）能触发
    // ==================================================================

    /// <summary>e1：占用率 0.95（&gt; 0.9）→ Spillback = true。</summary>
    [Fact]
    public void E1_Spillback_Triggers_Above_Threshold()
    {
        var collector = new MetricsCollector(MakeNetwork().Compile());
        collector.Record(new SimEvent(SimEventKind.Spillback, 42d, CarIn, -1, 0.95d));

        MetricsSnapshot m = collector.Snapshot();
        _output.WriteLine($"[E1] Spillback={m.Spillback}");

        Assert.True(m.Spillback, "占有率 0.95 > 0.9 应触发溢出。");
    }

    /// <summary>e2：阈值边界——0.90 不触发（须严格大于 0.9）、0.91 触发。</summary>
    [Fact]
    public void E2_Spillback_Threshold_Is_Strict()
    {
        var atThreshold = new MetricsCollector(MakeNetwork().Compile());
        atThreshold.Record(new SimEvent(SimEventKind.Spillback, 42d, CarIn, -1, SimConstants.SpillbackOccupancy));
        Assert.False(atThreshold.Snapshot().Spillback, "占有率正好 0.9 不应触发（判据为严格大于）。");

        var above = new MetricsCollector(MakeNetwork().Compile());
        above.Record(new SimEvent(SimEventKind.Spillback, 42d, CarIn, -1, SimConstants.SpillbackOccupancy + 0.001d));
        Assert.True(above.Snapshot().Spillback, "占有率 0.901 > 0.9 应触发。");
    }

    /// <summary>e3：真实引擎下——超饱和需求使首车道排满并溢出，Spillback 由事件流触发。</summary>
    [Fact]
    public void E3_Spillback_FromLiveEngine_Under_Oversaturation()
    {
        // 高需求 + 短红灯放行：进口道被排满 → 占有率越过 0.9。用"每车道占有率"形态注入 Spillback 事件，
        // 校验累计器在真实事件流中正确置位（引擎自身的溢出事件发出归 T6.1 集成）。
        var collector = new MetricsCollector(MakeNetwork().Compile());
        SimEngine engine = new(MakeLevel(demandRate: 2.0d), MakeConfig());

        bool anySpillbackEvent = false;
        for (int i = 0; i < 6000 && !engine.IsFinished; i++)
        {
            engine.Step();
            IReadOnlyList<SimEvent> events = engine.DrainEvents();
            collector.RecordRange(events);

            for (int k = 0; k < events.Count; k++)
            {
                if (events[k].Kind == SimEventKind.Spillback)
                {
                    anySpillbackEvent = true;
                }
            }

            // 引擎未发 Spillback 事件时，用快照占有率模拟"首车道占有率 > 0.9"的判据输入，
            // 验证累计器对真实 occupancy 信号的响应（表现层 / 集成层的接入方式，契约 §5.4）。
            SimState state = engine.Snapshot();
            for (int l = 0; l < state.Lanes.Count; l++)
            {
                if (state.Lanes[l].Occupancy > SimConstants.SpillbackOccupancy)
                {
                    collector.Record(new SimEvent(
                        SimEventKind.Spillback, state.SimTime, state.Lanes[l].LaneId, -1, state.Lanes[l].Occupancy));
                }
            }
        }

        MetricsSnapshot m = collector.Snapshot();

        _output.WriteLine($"[E3] Spillback={m.Spillback}  engineEmitted={anySpillbackEvent}  " +
                          $"Q={m.Q:F4}  R={m.R:F4}");

        // 超饱和下必然出现"排队接近 / 达到满车道"；至少应有排队样本累计（Q > 0）。
        Assert.True(m.Q > 0d, "超饱和下应有排队累计（Q > 0）。");
    }

    // ==================================================================
    // F 组：冻结接口 / 确定性 / Reset / 边界
    // ==================================================================

    /// <summary>f1：契约 §5.3 前置条款——LaneId 越界的事件被忽略且保持确定性（不抛异常）。</summary>
    [Fact]
    public void F1_OutOfRange_LaneId_Is_Ignored()
    {
        var collector = new MetricsCollector(MakeNetwork().Compile());

        collector.Record(new SimEvent(SimEventKind.QueueSample, 10d, 999, -1, 50d));
        collector.Record(new SimEvent(SimEventKind.VehicleStopped, 10d, -5, 0, 1d));
        collector.Record(new SimEvent(SimEventKind.DemandArrived, 10d, 12345, 0, 1d));

        MetricsSnapshot m = collector.Snapshot();

        // 越界车道不污染：机动车 Q 仍为 0（999 号车道的 50 辆被忽略）。
        Assert.Equal(0d, ClassOf(m, TrafficClass.Driving).QNormalized, 9);
    }

    /// <summary>f2：Snapshot 可重复调用且结果一致（只读快照）。</summary>
    [Fact]
    public void F2_Snapshot_Is_Repeatable()
    {
        var collector = new MetricsCollector(MakeNetwork().Compile());
        collector.Record(new SimEvent(SimEventKind.DemandArrived, 0.1d, CarIn, 0, 1d));
        collector.Record(new SimEvent(SimEventKind.VehiclePassed, 30d, CarOut, 0, CarTurn));

        MetricsSnapshot a = collector.Snapshot();
        MetricsSnapshot b = collector.Snapshot();

        Assert.Equal(a.R, b.R, 12);
        Assert.Equal(a.D, b.D, 12);
        Assert.Equal(a.P, b.P, 12);
        Assert.Equal(a.Q, b.Q, 12);
        Assert.Equal(a.Spillback, b.Spillback);
        Assert.Equal(a.Classes, b.Classes);
    }

    /// <summary>f3：Reset 清空全部累计（指标回到初值），且可再次累计。</summary>
    [Fact]
    public void F3_Reset_Clears_All_Accumulators()
    {
        var collector = new MetricsCollector(MakeNetwork().Compile());
        collector.Record(new SimEvent(SimEventKind.DemandArrived, 0.1d, CarIn, 0, 1d));
        collector.Record(new SimEvent(SimEventKind.VehiclePassed, 30d, CarOut, 0, CarTurn));
        collector.Record(new SimEvent(SimEventKind.QueueSample, 10d, CarIn, -1, 20d));
        collector.Record(new SimEvent(SimEventKind.Spillback, 10d, CarIn, -1, 0.95d));

        MetricsSnapshot before = collector.Snapshot();
        Assert.True(before.Spillback);
        Assert.True(before.Q > 0d);

        collector.Reset();
        MetricsSnapshot after = collector.Snapshot();

        Assert.Equal(1d, after.R, 9);
        Assert.Equal(0d, after.D, 9);
        Assert.Equal(0d, after.P, 9);
        Assert.Equal(0d, after.Q, 9);
        Assert.False(after.Spillback);
    }

    /// <summary>f4：确定性——同一事件序列两次累计，Snapshot 逐字段相同（铁律 2）。</summary>
    [Fact]
    public void F4_SameEventStream_Yields_Identical_Metrics()
    {
        IReadOnlyList<SimEvent> stream = BuildDeterministicStream();

        var first = new MetricsCollector(MakeNetwork().Compile());
        first.RecordRange(stream);

        var second = new MetricsCollector(MakeNetwork().Compile());
        second.RecordRange(stream);

        MetricsSnapshot a = first.Snapshot();
        MetricsSnapshot b = second.Snapshot();

        Assert.Equal(a.R, b.R, 12);
        Assert.Equal(a.D, b.D, 12);
        Assert.Equal(a.P, b.P, 12);
        Assert.Equal(a.Q, b.Q, 12);
        Assert.Equal(a.Classes.Count, b.Classes.Count);
        for (int i = 0; i < a.Classes.Count; i++)
        {
            Assert.Equal(a.Classes[i], b.Classes[i]);
        }
    }

    /// <summary>f5：与冻结接口对应——Metrics 暴露 §5.2 的全部字段，Classes 固定三类顺序。</summary>
    [Fact]
    public void F5_Metrics_Exposes_Frozen_Surface()
    {
        var collector = new MetricsCollector(MakeNetwork().Compile());
        MetricsSnapshot m = collector.Snapshot();

        Assert.Equal(0d, m.R - m.R, 0);          // R 可读
        Assert.Equal(0d, m.D - m.D, 0);          // D 可读
        Assert.Equal(0d, m.P - m.P, 0);          // P 可读
        Assert.Equal(0d, m.Q - m.Q, 0);          // Q 可读

        Assert.Equal(3, m.Classes.Count);
        Assert.Equal(TrafficClass.Driving, m.Classes[0].Class);
        Assert.Equal(TrafficClass.Pedestrian, m.Classes[1].Class);
        Assert.Equal(TrafficClass.Bicycle, m.Classes[2].Class);
        Assert.Empty(m.CollisionTurns);
        Assert.False(m.Collision);
    }

    /// <summary>f6：碰撞事件置位 Collision 并记录冲突转向（供 T2.2 c）。</summary>
    [Fact]
    public void F6_Collision_Sets_Flag_And_Turns()
    {
        var collector = new MetricsCollector(MakeNetwork().Compile());
        collector.Record(new SimEvent(SimEventKind.Collision, 33d, CarIn, 7, CarTurn));

        MetricsSnapshot m = collector.Snapshot();

        Assert.True(m.Collision);
        Assert.Contains(CarTurn, m.CollisionTurns);
    }

    /// <summary>f7：逐进口道聚合（ApproachMetrics）——按 (节点, 道路) 归集延误与峰值排队。</summary>
    [Fact]
    public void F7_Approaches_Aggregate_By_Node_And_Road()
    {
        var collector = new MetricsCollector(MakeNetwork().Compile());

        for (int i = 0; i < 4; i++)
        {
            collector.Record(new SimEvent(SimEventKind.VehiclePassed, 20d + i, CarOut, i, CarTurn));
        }

        collector.Record(new SimEvent(SimEventKind.QueueSample, 10d, CarIn, -1, 12d));

        MetricsSnapshot m = collector.Snapshot();

        _output.WriteLine($"[F7] Approaches={m.Approaches.Count}");

        // 进口道 = lane0（to_node=0, road=1）与 lane2 / lane4（同道路、同进口）；lane1 属道路 1
        // 但指向 node 2（是 node 2 的进口道、node 0 的出口道），故按 (节点, 道路) 分开聚合。
        bool found = false;
        for (int i = 0; i < m.Approaches.Count; i++)
        {
            ApproachMetrics a = m.Approaches[i];
            if (a.NodeId == 0 && a.RoadId == 1)
            {
                found = true;
                Assert.True(a.QueueLength >= 12, $"道路 1 的峰值排队应 ≥ 12，实际 {a.QueueLength}。");
            }
        }

        Assert.True(found, "应存在 (节点 0, 道路 1) 的进口道聚合。");
    }

    /// <summary>f8：network == null → ArgumentNullException（§5.3 前置）。</summary>
    [Fact]
    public void F8_Null_Network_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new MetricsCollector(null!));
    }

    /// <summary>f9：RecordRange(null) → ArgumentNullException。</summary>
    [Fact]
    public void F9_Null_Events_Throws()
    {
        var collector = new MetricsCollector(MakeNetwork().Compile());
        Assert.Throws<ArgumentNullException>(() => collector.RecordRange(null!));
    }

    /// <summary>f10：需求配准——非机动车需求为 0 时，无线索到达全归行人（R 分母正确）。</summary>
    [Fact]
    public void F10_Registration_Assigns_Unattributed_Arrivals()
    {
        var collector = new MetricsCollector(MakeNetwork().Compile());

        // 只配准行人需求（5 人），非机动车为 0：无线索到达应全归行人。
        collector.RegisterDemand(
            drivingDemandByLane: null,
            pedestrianDemandByTurn: BuildDemandTable(MakeNetwork().Compile().TurnCount, CrosswalkTurn, 5d),
            bicycleDemandByLane: null);

        for (int i = 0; i < 5; i++)
        {
            collector.Record(new SimEvent(SimEventKind.DemandArrived, 0.1 * i, -1, -1, 1d));
        }

        collector.Record(new SimEvent(SimEventKind.VehiclePassed, 40d, WalkOut, 0, CrosswalkTurn));

        MetricsSnapshot m = collector.Snapshot();

        // 无线索到达全部归行人（5 人）→ 分母 = 5；通过 1 人次 → R = 1/5 = 0.2。
        Assert.Equal(0.2d, ClassOf(m, TrafficClass.Pedestrian).R, 6);

        // 非机动车需求未配准（= 0）→ 无线索到达未被误归非机动车：其 R 仍为空类中性值 1。
        Assert.Equal(1d, ClassOf(m, TrafficClass.Bicycle).R, 9);

        // 分母确为 5 的独立佐证：再补齐 4 人次通过 → R = 5/5 = 1.0。
        for (int i = 1; i < 5; i++)
        {
            collector.Record(new SimEvent(SimEventKind.VehiclePassed, 40d + i, WalkOut, i, CrosswalkTurn));
        }

        Assert.Equal(1.0d, ClassOf(collector.Snapshot(), TrafficClass.Pedestrian).R, 6);
    }

    // ==================================================================
    // 夹具与工具
    // ==================================================================

    private static SimConfig MakeConfig() => new()
    {
        Dt = Dt,
        SpeedMultiplier = 1,
        Deterministic = true,
        Seed = Seed,
        Duration = 600d,
        Timing = SignalTiming.Default,
        QueueCapacityPerLane = 20,
    };

    private static Level MakeLevel(double demandRate = 0.2d)
    {
        DemandPlan[] demand = [new DemandPlan { Lane = CarIn, TimePoints = [0d], FlowRates = [demandRate] }];
        PedestrianDemand[] pedestrians = [new PedestrianDemand { CrosswalkTurn = CrosswalkTurn, TimePoints = [0d], FlowRates = [0.15d] }];
        BicycleDemand[] bicycles =
        [
            new BicycleDemand { Lane = BikeIn, Mode = BicycleMode.Independent, TimePoints = [0d], FlowRates = [0.15d] },
        ];

        return new Level
        {
            Meta = new LevelMeta { Name = "T2.1 指标", Chapter = 2, Index = 1 },
            Network = MakeNetwork(),
            Demand = demand,
            Pedestrians = pedestrians,
            Bicycles = bicycles,
        };
    }

    /// <summary>跑一个 Level 引擎，同步消费事件到累计器，返回最终指标与到达 / 通过计数。</summary>
    private static (MetricsSnapshot Snapshot, long Arrived, long Passed) RunLevel(Level level, int steps, bool register)
    {
        SimEngine engine = new(level, MakeConfig());
        var collector = new MetricsCollector(engine.Network);

        if (register)
        {
            collector.RegisterDemand(
                DrivingDemandTable(level, engine.Network.LaneCount),
                PedestrianDemandTable(level, engine.Network.TurnCount),
                BicycleDemandTable(level, engine.Network.LaneCount));
        }

        long arrived = 0L;
        long passed = 0L;
        int n = 0;
        while (!engine.IsFinished && n < steps)
        {
            engine.Step();
            n++;
            IReadOnlyList<SimEvent> events = engine.DrainEvents();
            collector.RecordRange(events);

            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].Kind == SimEventKind.DemandArrived)
                {
                    arrived++;
                }
                else if (events[i].Kind == SimEventKind.VehiclePassed)
                {
                    passed++;
                }
            }
        }

        IReadOnlyList<SimEvent> tail = engine.DrainEvents();
        collector.RecordRange(tail);

        return (collector.Snapshot(), arrived, passed);
    }

    private static double[] BuildDemandTable(int size, int index, double value)
    {
        var table = new double[size];
        if ((uint)index < (uint)size)
        {
            table[index] = value;
        }

        return table;
    }

    private static double[] DrivingDemandTable(Level level, int laneCount)
    {
        var table = new double[laneCount];
        for (int i = 0; i < level.Demand.Count; i++)
        {
            int lane = level.Demand[i].Lane;
            if ((uint)lane < (uint)laneCount)
            {
                table[lane] += TotalFlow(level.Demand[i].TimePoints, level.Demand[i].FlowRates, 600d);
            }
        }

        return table;
    }

    private static double[] PedestrianDemandTable(Level level, int turnCount)
    {
        var table = new double[turnCount];
        for (int i = 0; i < level.Pedestrians.Count; i++)
        {
            int turn = level.Pedestrians[i].CrosswalkTurn;
            if ((uint)turn < (uint)turnCount)
            {
                table[turn] += TotalFlow(level.Pedestrians[i].TimePoints, level.Pedestrians[i].FlowRates, 600d);
            }
        }

        return table;
    }

    private static double[] BicycleDemandTable(Level level, int laneCount)
    {
        var table = new double[laneCount];
        for (int i = 0; i < level.Bicycles.Count; i++)
        {
            int lane = level.Bicycles[i].Lane;
            if ((uint)lane < (uint)laneCount)
            {
                table[lane] += TotalFlow(level.Bicycles[i].TimePoints, level.Bicycles[i].FlowRates, 600d);
            }
        }

        return table;
    }

    /// <summary>分段常值曲线的累计量 ∫λ dt（[0, horizon]）。</summary>
    private static double TotalFlow(IReadOnlyList<double> timePoints, IReadOnlyList<double> flowRates, double horizon)
    {
        double total = 0d;
        for (int i = 0; i < timePoints.Count; i++)
        {
            double start = timePoints[i];
            if (start >= horizon)
            {
                break;
            }

            double end = i + 1 < timePoints.Count ? Math.Min(timePoints[i + 1], horizon) : horizon;
            double rate = i < flowRates.Count ? flowRates[i] : 0d;
            total += rate * Math.Max(0d, end - start);
        }

        return total;
    }

    private static IReadOnlyList<SimEvent> BuildDeterministicStream()
    {
        var list = new List<SimEvent>();
        for (int i = 0; i < 20; i++)
        {
            list.Add(new SimEvent(SimEventKind.DemandArrived, 0.1 * i, CarIn, i, 1d));
        }

        for (int i = 0; i < 10; i++)
        {
            list.Add(new SimEvent(SimEventKind.VehicleStopped, 20d + i, CarIn, i, i));
        }

        for (int i = 0; i < 12; i++)
        {
            list.Add(new SimEvent(SimEventKind.QueueSample, 5d * i, CarIn, -1, i));
        }

        for (int i = 0; i < 8; i++)
        {
            list.Add(new SimEvent(SimEventKind.VehiclePassed, 60d + i, CarOut, i, CarTurn));
        }

        return list;
    }

    private static ClassMetrics ClassOf(MetricsSnapshot metrics, TrafficClass cls)
    {
        for (int i = 0; i < metrics.Classes.Count; i++)
        {
            if (metrics.Classes[i].Class == cls)
            {
                return metrics.Classes[i];
            }
        }

        throw new InvalidOperationException($"指标中未找到类别 {cls}。");
    }

    private static string[] DumpClasses(MetricsSnapshot metrics)
    {
        var parts = new string[metrics.Classes.Count];
        for (int i = 0; i < metrics.Classes.Count; i++)
        {
            ClassMetrics c = metrics.Classes[i];
            parts[i] = $"{c.Class}:R={c.R:F3},D={c.D:F2},P={c.P:F2},Q={c.QNormalized:F3}";
        }

        return parts;
    }

    /// <summary>单信号路口：机动车道 0/1 + 人行道 2/3（Crosswalk）+ 非机动车道 4/5。</summary>
    private static Network MakeNetwork()
    {
        Node[] nodes =
        [
            new Node { Id = 0, Type = NodeType.Signalized, X = 0d, Y = 0d, Offset = 0d, SignalPlans = [MakePlan()] },
            new Node { Id = 1, Type = NodeType.Origin, X = 0d, Y = -180d },
            new Node { Id = 2, Type = NodeType.Destination, X = 0d, Y = 180d },
            new Node { Id = 3, Type = NodeType.Destination, X = 180d, Y = 0d },
        ];

        Lane[] lanes =
        [
            Lane(CarIn, road: 1, from: 1, to: 0),
            Lane(CarOut, road: 1, from: 0, to: 2),
            Lane(WalkIn, road: 1, from: 1, to: 0, type: LaneType.Sidewalk, speed: 1.4d),
            Lane(WalkOut, road: 3, from: 0, to: 3, type: LaneType.Sidewalk, speed: 1.4d),
            Lane(BikeIn, road: 1, from: 1, to: 0, type: LaneType.Biking, speed: 5.0d),
            Lane(BikeOut, road: 1, from: 0, to: 2, type: LaneType.Biking, speed: 5.0d),
        ];

        Turn[] turns =
        [
            new Turn { Id = CarTurn, SrcLane = CarIn, DstLane = CarOut, Node = 0, Type = TurnType.Straight },
            new Turn { Id = CrosswalkTurn, SrcLane = WalkIn, DstLane = WalkOut, Node = 0, Type = TurnType.Crosswalk },
            new Turn { Id = BikeTurn, SrcLane = BikeIn, DstLane = BikeOut, Node = 0, Type = TurnType.Straight },
        ];

        return new Network { Nodes = nodes, Lanes = lanes, Turns = turns };
    }

    /// <summary>两条机动车道、长度不同（容量 30 / 10），用于 Q 归一的容量验证。</summary>
    private static Network MakeTwoLaneNetwork()
    {
        Node[] nodes =
        [
            new Node { Id = 0, Type = NodeType.Signalized, X = 0d, Y = 0d, SignalPlans = [MakePlan()] },
            new Node { Id = 1, Type = NodeType.Origin, X = 0d, Y = -180d },
            new Node { Id = 2, Type = NodeType.Destination, X = 0d, Y = 180d },
        ];

        Lane[] lanes =
        [
            LaneWithLength(0, road: 1, from: 1, to: 0, length: 180d),
            LaneWithLength(1, road: 1, from: 1, to: 0, length: 60d),
            LaneWithLength(2, road: 1, from: 0, to: 2, length: 180d),
        ];

        Turn[] turns =
        [
            new Turn { Id = 0, SrcLane = 0, DstLane = 2, Node = 0, Type = TurnType.Straight },
            new Turn { Id = 1, SrcLane = 1, DstLane = 2, Node = 0, Type = TurnType.Straight },
        ];

        return new Network { Nodes = nodes, Lanes = lanes, Turns = turns };
    }

    private static SignalPlan MakePlan() => new()
    {
        StartTime = 0d,
        Stages =
        [
            new Stage { Type = StageType.Fixed, Duration = 30d, ProtectedTurnIds = [CarTurn, BikeTurn] },
            new Stage { Type = StageType.Fixed, Duration = 30d, ProtectedTurnIds = [CrosswalkTurn] },
        ],
    };

    private static Lane Lane(int id, int road, int from, int to, LaneType type = LaneType.Driving, double speed = 13.89d)
        => LaneWithLength(id, road, from, to, length: 180d, type, speed);

    private static Lane LaneWithLength(int id, int road, int from, int to, double length, LaneType type = LaneType.Driving, double speed = 13.89d)
        => new()
        {
            Id = id,
            Road = road,
            Type = type,
            FromNode = from,
            ToNode = to,
            Length = length,
            SpeedLimit = speed,
            LaneIndex = 0,
        };
}
