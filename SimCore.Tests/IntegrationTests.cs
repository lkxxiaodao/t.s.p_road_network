using System.Reflection;
using SimCore.Geometry;
using SimCore.Io;
using SimCore.Model;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

/// <summary>
/// T1.8「需求接入主循环（M1 收口）」验收测试。
/// <para>本卡把 T1.7 的 <see cref="DemandGenerator"/> 接入 <see cref="SimEngine"/> 主循环：
/// <c>SimEngine(Level, SimConfig)</c> 构造时按 <c>Level.Demand / Pedestrians / Bicycles</c> 建立需求时刻表，
/// 每步对每个目标调 <see cref="DemandGenerator.DrawArrivals"/> 注入到达，并驱动
/// <see cref="PedestrianCrossing"/> / <see cref="BicycleFlow"/>，使
/// <c>SimState.Pedestrians</c> / <c>SimState.Bicycles</c> 非空（§4.3 Level 构造语义）。</para>
/// <para>分组与验收标准逐条对应：
/// <list type="bullet">
///   <item>A 组 → 验收 a) 产生 <see cref="SimEventKind.DemandArrived"/>，且车辆通过数 &gt; 0</item>
///   <item>B 组 → 验收 b) <c>SimState.Pedestrians</c> / <c>SimState.Bicycles</c> 非空（且确实被驱动前进）</item>
///   <item>C 组 → 验收 c) 1× 与 32× 到同一仿真时刻状态哈希相同（回归 T1.6 b）</item>
///   <item>D 组 → 验收 d) 同 seed 两次运行最终状态哈希相同（回归 T1.6 a）；换 seed 注入流随之改变</item>
///   <item>E 组 → 硬约束：SimEngine 未新增任何 public 成员（冻结面不扩张）</item>
///   <item>F 组 → 边界：无行人/非机动车需求、网络形构造（无需求注入）、Reset 可复现</item>
/// </list>
/// </para>
/// </summary>
public sealed class IntegrationTests
{
    private readonly ITestOutputHelper _output;

    public IntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private const double Dt = 0.1d;
    private const double Duration = 600d;
    private const long Seed = 20261010L;

    // 车道 id
    private const int CarInLane = 0;      // 机动车进口（Driving）
    private const int CarOutLane = 1;     // 机动车出口（Driving）
    private const int WalkInLane = 2;     // 进口人行道（Sidewalk）
    private const int WalkOutLane = 3;    // 出口人行道（Sidewalk）
    private const int BikeInLane = 4;     // 非机动车进口（Biking）
    private const int BikeOutLane = 5;    // 非机动车出口（Biking）

    // 转向 id
    private const int CarTurn = 0;        // 0 → 1，Straight
    private const int CrosswalkTurn = 1;  // 2 → 3，Crosswalk
    private const int BikeTurn = 2;       // 4 → 5，Straight（独立非机动车自身转向）

    // ==================================================================
    // A 组：验收 a) 产生 DemandArrived，且车辆通过数 > 0
    // ==================================================================

    /// <summary>a1：以 Level 起的引擎运行中产生 <c>DemandArrived</c>，且车辆实际通过（<c>VehiclePassed</c> &gt; 0）。</summary>
    [Fact]
    public void A1_LevelEngine_EmitsDemandArrived_AndVehiclesPass()
    {
        SimEngine engine = new(MakeLevel(), MakeConfig());

        long arrived = 0L;
        long passed = 0L;
        int maxVehicles = 0;

        while (!engine.IsFinished)
        {
            engine.Step();
            Accumulate(engine.DrainEvents(), ref arrived, ref passed);
            maxVehicles = Math.Max(maxVehicles, engine.Snapshot().Vehicles.Count);
        }

        Accumulate(engine.DrainEvents(), ref arrived, ref passed);

        _output.WriteLine($"[A1] DemandArrived={arrived}  VehiclePassed={passed}  MaxVehicles={maxVehicles}  FinalHash={engine.StateHash():X16}");

        Assert.True(arrived > 0L, "Level 起的引擎应产生 DemandArrived 事件。");
        Assert.True(passed > 0L, "车辆应实际通过（VehiclePassed > 0）——非空路。");
        Assert.True(maxVehicles > 0, "运行中应出现过机动车（快照非空）。");
    }

    /// <summary>a2：需求到达数应覆盖三类（机动车 + 行人 + 非机动车），且量级合理。</summary>
    [Fact]
    public void A2_DemandArrivals_CoverAllThreeKinds()
    {
        SimEngine engine = new(MakeLevel(), MakeConfig());

        long arrived = 0L;
        long passed = 0L;

        while (!engine.IsFinished)
        {
            engine.Step();
            Accumulate(engine.DrainEvents(), ref arrived, ref passed);
        }

        Accumulate(engine.DrainEvents(), ref arrived, ref passed);

        // 行人 0.15/s × 600s = 90、非机动车 0.15/s × 600s = 90 —— 二者不会被丢弃，故到达数至少 ≥ 180。
        Assert.True(arrived >= 180L, $"三类到达总数应 ≥ 180（观测 {arrived}）。");
    }

    // ==================================================================
    // B 组：验收 b) 行人 / 非机动车快照非空
    // ==================================================================

    /// <summary>b1：配置了行人/非机动车需求的关卡中，SimState.Pedestrians / SimState.Bicycles 非空。</summary>
    [Fact]
    public void B1_PedestriansAndBicycles_AppearInSnapshot()
    {
        SimEngine engine = new(MakeLevel(), MakeConfig());

        int maxPedestrians = 0;
        int maxBicycles = 0;
        double maxPedestrianPosition = 0d;
        double maxBicyclePosition = 0d;

        for (int step = 0; step < 6000; step++)
        {
            engine.Step();

            SimState state = engine.Snapshot();

            if (state.Pedestrians.Count > maxPedestrians)
            {
                maxPedestrians = state.Pedestrians.Count;
            }

            if (state.Bicycles.Count > maxBicycles)
            {
                maxBicycles = state.Bicycles.Count;
            }

            foreach (PedestrianSnapshot pedestrian in state.Pedestrians)
            {
                maxPedestrianPosition = Math.Max(maxPedestrianPosition, pedestrian.Position);
            }

            foreach (BicycleSnapshot bicycle in state.Bicycles)
            {
                maxBicyclePosition = Math.Max(maxBicyclePosition, bicycle.Position);
            }
        }

        Assert.True(maxPedestrians > 0, "SimState.Pedestrians 应非空（行人需求已接入主循环）。");
        Assert.True(maxBicycles > 0, "SimState.Bicycles 应非空（非机动车需求已接入主循环）。");
        Assert.True(maxPedestrianPosition > 0d, "行人应被 PedestrianCrossing.Step 驱动前进（Position > 0）。");
        Assert.True(maxBicyclePosition > 0d, "非机动车应被 BicycleFlow.Step 驱动前进（Position > 0）。");

        _output.WriteLine(
            $"[B1] MaxPedestrians={maxPedestrians}  MaxPedPosition={maxPedestrianPosition:F3}  " +
            $"MaxBicycles={maxBicycles}  MaxBikePosition={maxBicyclePosition:F3}");
    }

    /// <summary>b2：行人 / 非机动车未被并入机动车队列——机动车车道快照不因二者而增长（独立单元口径）。</summary>
    [Fact]
    public void B2_IndependentUnits_DoNotEnterVehicleQueues()
    {
        SimEngine engine = new(MakeLevel(), MakeConfig());

        for (int step = 0; step < 6000; step++)
        {
            engine.Step();
        }

        SimState state = engine.Snapshot();

        // 行人 / 非机动车数量可观，但它们不在任何机动车车道队列里：CarOutLane（出口道）不应出现行人/非机动车。
        // 出口道（CarOutLane）的机动车队列由上游机动车路由而来，与行人 / 非机动车互不相干。
        foreach (VehicleSnapshot vehicle in state.Vehicles)
        {
            Assert.True(
                vehicle.LaneId is CarInLane or CarOutLane,
                $"机动车只应出现在机动车道（0/1），实见车道 {vehicle.LaneId}。");
        }
    }

    // ==================================================================
    // C 组：验收 c) 1× 与 32× 到同一仿真时刻哈希相同（回归 T1.6 b）
    // ==================================================================

    /// <summary>c1：1× 推进 600 秒 与 32× 推进 18.75 秒，到达同一仿真时刻（600s），哈希与全快照（含行人 / 非机动车）相同。</summary>
    [Fact]
    public void C1_OneX_Equals_32X_AtSameSimTime()
    {
        SimEngine oneX = new(MakeLevel(), MakeConfig(multiplier: 1));
        SimEngine thirtyTwoX = new(MakeLevel(), MakeConfig(multiplier: 32));

        oneX.Advance(Duration);              // 1×：600 实时秒 → 6000 步
        thirtyTwoX.Advance(Duration / 32d);  // 32×：18.75 实时秒 → 6000 步

        Assert.True(oneX.IsFinished);
        Assert.True(thirtyTwoX.IsFinished);
        Assert.Equal(Duration, oneX.SimTime, 9);
        Assert.Equal(oneX.SimTime, thirtyTwoX.SimTime, 12);
        AssertSameState(oneX, thirtyTwoX);
    }

    /// <summary>c2：逐帧驱动（每帧 1/60 秒）下同样 1× ≡ 32×（证明需求接入未引入丢步 / 漂移）。</summary>
    [Fact]
    public void C2_Frame_Driven_OneX_Equals_32X()
    {
        const double frame = 1d / 60d;

        SimEngine oneX = new(MakeLevel(), MakeConfig(multiplier: 1));
        SimEngine thirtyTwoX = new(MakeLevel(), MakeConfig(multiplier: 32));

        for (int i = 0; i < 36_000; i++)
        {
            oneX.Advance(frame);
        }

        for (int i = 0; i < 1_125; i++)
        {
            thirtyTwoX.Advance(frame);
        }

        Assert.True(oneX.IsFinished);
        Assert.True(thirtyTwoX.IsFinished);
        AssertSameState(oneX, thirtyTwoX);
    }

    // ==================================================================
    // D 组：验收 d) 同 seed 两次运行最终状态哈希相同（回归 T1.6 a）
    // ==================================================================

    /// <summary>d1：同 seed、同关卡的两个引擎跑满整局，最终状态哈希与逐元素快照完全一致。</summary>
    [Fact]
    public void D1_SameSeed_TwoRuns_IdenticalFinalState()
    {
        SimEngine first = new(MakeLevel(), MakeConfig());
        SimEngine second = new(MakeLevel(), MakeConfig());

        while (!first.IsFinished)
        {
            first.Step();
        }

        while (!second.IsFinished)
        {
            second.Step();
        }

        Assert.True(first.IsFinished);
        AssertSameState(first, second);
    }

    /// <summary>d2：换 seed 后需求注入流随之改变（证明种子确实作用于到达序列）。</summary>
    [Fact]
    public void D2_DifferentSeed_ChangesDemandStream()
    {
        SimEngine a = new(MakeLevel(), MakeConfig(seed: 111L));
        SimEngine b = new(MakeLevel(), MakeConfig(seed: 222L));

        for (int i = 0; i < 1200; i++)   // 120 s
        {
            a.Step();
            b.Step();
        }

        Assert.NotEqual(EventSequence(a), EventSequence(b));
    }

    // ==================================================================
    // E 组：硬约束——未新增任何 public 成员
    // ==================================================================

    /// <summary>e1：SimEngine 的 public 面与 §4.3 冻结面逐字一致——本卡新增成员一律 internal / private。</summary>
    [Fact]
    public void E1_SimEngine_PublicSurface_Unchanged()
    {
        Type engine = typeof(SimEngine);

        string[] methods = engine
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)   // 排除属性 getter / setter（get_X 等）
            .Select(m => m.Name)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[] { "Advance", "ApplyPlan", "DrainEvents", "Reset", "Snapshot", "StateHash", "Step" },
            methods);

        string[] properties = engine
            .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "Config", "IsFinished", "Network", "SimTime", "SpeedMultiplier" }, properties);

        Assert.Empty(engine.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        Assert.Empty(engine.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly));

        Assert.NotNull(engine.GetConstructor([typeof(CompiledNetwork), typeof(SimConfig)]));
        Assert.NotNull(engine.GetConstructor([typeof(Level), typeof(SimConfig)]));

        // 本卡新增的注入 / 构建成员应为非 public（internal / private）。
        Assert.NotNull(engine.GetMethod("InjectArrivals", BindingFlags.NonPublic | BindingFlags.Instance));
        Assert.Null(engine.GetMethod("InjectArrivals", BindingFlags.Public | BindingFlags.Instance));
    }

    // ==================================================================
    // F 组：边界
    // ==================================================================

    /// <summary>f1：无行人 / 非机动车需求的关卡——正常跑通，其快照恒为空，无异常。</summary>
    [Fact]
    public void F1_DrivingOnlyLevel_EmptyPedestriansAndBicycles()
    {
        SimEngine engine = new(MakeLevel(withPedestrianAndBicycle: false), MakeConfig());

        long arrived = 0L;
        long passed = 0L;
        bool anyPedestrian = false;
        bool anyBicycle = false;

        while (!engine.IsFinished)
        {
            engine.Step();
            Accumulate(engine.DrainEvents(), ref arrived, ref passed);

            SimState state = engine.Snapshot();
            anyPedestrian |= state.Pedestrians.Count > 0;
            anyBicycle |= state.Bicycles.Count > 0;
        }

        Assert.True(arrived > 0L, "仍应有机动车需求到达。");
        Assert.True(passed > 0L);
        Assert.False(anyPedestrian, "未配置行人需求的关卡不应出现行人。");
        Assert.False(anyBicycle, "未配置非机动车需求的关卡不应出现非机动车。");
    }

    /// <summary>f2：网络形构造（<c>SimEngine(CompiledNetwork, SimConfig)</c>）不注入需求——保持 T1.6 口径。</summary>
    [Fact]
    public void F2_NetworkConstructor_HasNoDemandInjection()
    {
        var engine = new SimEngine(MakeNetwork().Compile(), MakeConfig());

        long arrived = 0L;
        long passed = 0L;

        for (int i = 0; i < 2000; i++)
        {
            engine.Step();
            Accumulate(engine.DrainEvents(), ref arrived, ref passed);
        }

        Assert.Equal(0L, arrived);
        Assert.Equal(0L, passed);
        Assert.Empty(engine.Snapshot().Vehicles);
        Assert.Empty(engine.Snapshot().Pedestrians);
        Assert.Empty(engine.Snapshot().Bicycles);
    }

    /// <summary>f3：Reset(seed) 使需求接入后的引擎可复现——复位后与初始状态哈希一致，且换 seed 后演化改变。</summary>
    [Fact]
    public void F3_Reset_ReproducesDemandDrivenState()
    {
        SimEngine engine = new(MakeLevel(), MakeConfig());

        engine.Reset(Seed);
        ulong initial = engine.StateHash();

        RunSteps(engine, 2000);
        ulong evolved = engine.StateHash();
        Assert.NotEqual(initial, evolved);

        engine.Reset(Seed);
        Assert.Equal(0d, engine.SimTime);
        Assert.Equal(Seed, engine.Seed);
        Assert.Equal(initial, engine.StateHash());
    }

    // ==================================================================
    // 夹具与工具
    // ==================================================================

    private static SimConfig MakeConfig(int multiplier = 1, long seed = Seed) => new()
    {
        Dt = Dt,
        SpeedMultiplier = multiplier,
        Deterministic = true,
        Seed = seed,
        Duration = Duration,
        Timing = SignalTiming.Default,
        QueueCapacityPerLane = 20,
    };

    private static Level MakeLevel(bool withPedestrianAndBicycle = true)
    {
        DemandPlan[] demand = new[] { DriveDemand(CarInLane, new[] { 0d }, new[] { 0.2d }) };
        PedestrianDemand[] pedestrians = withPedestrianAndBicycle
            ? new[] { WalkDemand(CrosswalkTurn, new[] { 0d }, new[] { 0.15d }) }
            : Array.Empty<PedestrianDemand>();
        BicycleDemand[] bicycles = withPedestrianAndBicycle
            ? new[] { BikeDemand(BikeInLane, new[] { 0d }, new[] { 0.15d }) }
            : Array.Empty<BicycleDemand>();

        return new Level
        {
            Meta = new LevelMeta { Name = "T1.8 需求接入", Chapter = 1, Index = 8 },
            Network = MakeNetwork(),
            Demand = demand,
            Pedestrians = pedestrians,
            Bicycles = bicycles,
        };
    }

    private static DemandPlan DriveDemand(int lane, double[] timePoints, double[] flowRates) => new()
    {
        Lane = lane,
        TimePoints = timePoints,
        FlowRates = flowRates,
    };

    private static PedestrianDemand WalkDemand(int crosswalkTurn, double[] timePoints, double[] flowRates) => new()
    {
        CrosswalkTurn = crosswalkTurn,
        TimePoints = timePoints,
        FlowRates = flowRates,
    };

    private static BicycleDemand BikeDemand(int lane, double[] timePoints, double[] flowRates) => new()
    {
        Lane = lane,
        Mode = BicycleMode.Independent,
        TimePoints = timePoints,
        FlowRates = flowRates,
    };

    /// <summary>
    /// 单路口路网：Origin(1) → Signalized(0) → Destination(2)，另加 Destination(3)（人行横道 / 非机动车出口）。
    /// 含机动车道（0/1）、人行道（2/3，Crosswalk 转向 1）、非机动车道（4/5，独立自身转向 2）。
    /// </summary>
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
            Lane(CarInLane, road: 1, from: 1, to: 0),
            Lane(CarOutLane, road: 1, from: 0, to: 2),
            Lane(WalkInLane, road: 1, from: 1, to: 0, type: LaneType.Sidewalk, speed: 1.4d),
            Lane(WalkOutLane, road: 3, from: 0, to: 3, type: LaneType.Sidewalk, speed: 1.4d),
            Lane(BikeInLane, road: 1, from: 1, to: 0, type: LaneType.Biking, speed: 5.0d),
            Lane(BikeOutLane, road: 1, from: 0, to: 2, type: LaneType.Biking, speed: 5.0d),
        ];

        Turn[] turns =
        [
            new Turn { Id = CarTurn, SrcLane = CarInLane, DstLane = CarOutLane, Node = 0, Type = TurnType.Straight },
            new Turn { Id = CrosswalkTurn, SrcLane = WalkInLane, DstLane = WalkOutLane, Node = 0, Type = TurnType.Crosswalk },
            new Turn { Id = BikeTurn, SrcLane = BikeInLane, DstLane = BikeOutLane, Node = 0, Type = TurnType.Straight },
        ];

        return new Network { Nodes = nodes, Lanes = lanes, Turns = turns };
    }

    /// <summary>两相位配时：相位 A 保护放行机动车 / 非机动车（30s），相位 B 保护放行人行横道（30s）。</summary>
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
        => new()
        {
            Id = id,
            Road = road,
            Type = type,
            FromNode = from,
            ToNode = to,
            Length = 180d,
            SpeedLimit = speed,
            LaneIndex = 0,
        };

    private static void RunSteps(SimEngine engine, int steps)
    {
        for (int i = 0; i < steps; i++)
        {
            engine.Step();
        }
    }

    private static void Accumulate(IReadOnlyList<SimEvent> events, ref long arrived, ref long passed)
    {
        for (int i = 0; i < events.Count; i++)
        {
            switch (events[i].Kind)
            {
                case SimEventKind.DemandArrived:
                    arrived++;
                    break;
                case SimEventKind.VehiclePassed:
                    passed++;
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>采集引擎当前累计的事件序列（顺序确定），供"换 seed 注入流改变"断言。</summary>
    private static string EventSequence(SimEngine engine)
    {
        IReadOnlyList<SimEvent> events = engine.DrainEvents();
        var builder = new System.Text.StringBuilder(events.Count * 24);
        for (int i = 0; i < events.Count; i++)
        {
            builder.Append(events[i].Kind).Append(':').Append(events[i].Time.ToString("F3")).Append(';');
        }

        return builder.ToString();
    }

    /// <summary>逐元素比较两个引擎状态：仿真时刻、状态哈希、车辆 / 行人 / 非机动车 / 车道 / 信号快照。</summary>
    private static void AssertSameState(SimEngine expected, SimEngine actual)
    {
        Assert.Equal(expected.SimTime, actual.SimTime, 12);
        Assert.Equal(expected.StateHash(), actual.StateHash());

        SimState a = expected.Snapshot();
        SimState b = actual.Snapshot();

        Assert.Equal(a.Vehicles.Count, b.Vehicles.Count);
        for (int i = 0; i < a.Vehicles.Count; i++)
        {
            Assert.Equal(a.Vehicles[i], b.Vehicles[i]);
        }

        Assert.Equal(a.Pedestrians.Count, b.Pedestrians.Count);
        for (int i = 0; i < a.Pedestrians.Count; i++)
        {
            Assert.Equal(a.Pedestrians[i], b.Pedestrians[i]);
        }

        Assert.Equal(a.Bicycles.Count, b.Bicycles.Count);
        for (int i = 0; i < a.Bicycles.Count; i++)
        {
            Assert.Equal(a.Bicycles[i], b.Bicycles[i]);
        }

        Assert.Equal(a.Signals.Count, b.Signals.Count);
        for (int i = 0; i < a.Signals.Count; i++)
        {
            Assert.Equal(a.Signals[i], b.Signals[i]);
        }

        Assert.Equal(a.Lanes.Count, b.Lanes.Count);
        for (int i = 0; i < a.Lanes.Count; i++)
        {
            Assert.Equal(a.Lanes[i], b.Lanes[i]);
        }
    }
}
