using SimCore.Io;
using SimCore.Model;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

/// <summary>
/// T1.6「确定性步进 + 加速一致性」验收测试。
/// <para>分组与验收标准逐条对应，便于"手和眼"对证：
/// <list type="bullet">
///   <item>A 组 → 验收 a) 同 seed 跑两次最终状态哈希完全相同（含输入顺序无关、Reset 可复现）</item>
///   <item>B 组 → 验收 b) 1× 跑 600s 与 32× 跑到同一仿真时刻哈希相同（含逐帧驱动、运行期换挡）</item>
///   <item>C 组 → 验收 c) 全程无 NaN/Inf</item>
///   <item>D 组 → 验收 d) StateHash() 可用、随状态变化、与 Snapshot().Hash 一致</item>
///   <item>E 组 → 与冻结接口的对应（签名 / 默认值 / 校验 / 枚举 / 事件序 / Level 直通）</item>
/// </list>
/// </para>
/// <para>驱动场景：一条信号路口进口道（绿灯 30s / 红灯 20s + 清空时间，周期 60s）排队 + 恒定到达，
/// 车辆在绿灯放行后经转向汇入下游车道并驶出网络——既有排队、又有相位轮转、又有持续车流，
/// 足以让"状态哈希"覆盖到位置 / 速度 / 排队 / 相位等全部可影响结果的状态。</para>
/// </summary>
public sealed class DeterminismTests
{
    private const double Dt = 0.1d;
    private const double Duration = 600d;

    // ------------------------------------------------------------------
    // A 组：验收 a) 同 seed 两次运行最终状态哈希完全相同
    // ------------------------------------------------------------------

    /// <summary>a1：同 seed、同场景跑 3000 步，状态哈希与逐元素快照完全一致。</summary>
    [Fact]
    public void A1_Same_Seed_Two_Runs_Produce_Identical_State_And_Hash()
    {
        SimEngine first = MakeEngine();
        SimEngine second = MakeEngine();

        RunSteps(first, 3000);
        RunSteps(second, 3000);

        AssertSameState(first, second);
        Assert.True(first.Snapshot().Vehicles.Count > 0, "场景未产生任何车辆，确定性断言不具说服力。");
    }

    /// <summary>a2：同 seed 跑满整局（600s）两次，最终哈希完全相同、且都已结束。</summary>
    [Fact]
    public void A2_Same_Seed_Run_To_Duration_Is_Identical()
    {
        SimEngine first = MakeEngine();
        SimEngine second = MakeEngine();

        while (!first.IsFinished)
        {
            first.Step();
        }

        while (!second.IsFinished)
        {
            second.Step();
        }

        Assert.True(first.IsFinished);
        Assert.Equal(Duration, first.SimTime, 9);
        AssertSameState(first, second);
    }

    /// <summary>a3：运行后 Reset(seed) 复位到同一初始状态，并完成"换种子"。</summary>
    [Fact]
    public void A3_Reset_Restores_Initial_State_And_Swaps_Seed()
    {
        SimEngine engine = MakeEngine();

        engine.Reset(42L);
        ulong zero = engine.StateHash();

        RunSteps(engine, 1500);
        Assert.NotEqual(zero, engine.StateHash());

        engine.Reset(7L);
        Assert.Equal(0d, engine.SimTime);
        Assert.False(engine.IsFinished);
        Assert.Equal(7L, engine.Seed);
        Assert.Equal(zero, engine.StateHash());
    }

    /// <summary>a4：输入列表顺序（nodes / lanes / turns 的书写次序）不影响结果——不依赖集合遍历顺序。</summary>
    [Fact]
    public void A4_Input_List_Order_Does_Not_Affect_Result()
    {
        SimEngine forward = MakeEngine(networkReversed: false);
        SimEngine backward = MakeEngine(networkReversed: true);

        RunSteps(forward, 2000);
        RunSteps(backward, 2000);

        AssertSameState(forward, backward);
    }

    /// <summary>a5：状态未变时，重复调用 StateHash() 返回同一值。</summary>
    [Fact]
    public void A5_StateHash_Is_Stable_When_State_Unchanged()
    {
        SimEngine engine = MakeEngine();
        RunSteps(engine, 500);

        Assert.Equal(engine.StateHash(), engine.StateHash());
    }

    // ------------------------------------------------------------------
    // B 组：验收 b) 1× 跑 600s 与 32× 跑到同一仿真时刻哈希相同
    // ------------------------------------------------------------------

    /// <summary>b1：1× 推进 600 秒 与 32× 推进 18.75 秒，到达同一仿真时刻（600s），哈希相同。</summary>
    [Fact]
    public void B1_OneX_600s_Equals_32X_Same_SimTime()
    {
        SimEngine oneX = MakeEngine(multiplier: 1);
        SimEngine thirtyTwoX = MakeEngine(multiplier: 32);

        oneX.Advance(Duration);            // 1×：600 实时秒 → round(600/0.1) = 6000 步
        thirtyTwoX.Advance(Duration / 32d); // 32×：18.75 实时秒 → 6000 步

        Assert.True(oneX.IsFinished);
        Assert.True(thirtyTwoX.IsFinished);
        Assert.Equal(Duration, oneX.SimTime, 9);
        Assert.Equal(oneX.SimTime, thirtyTwoX.SimTime, 12);
        AssertSameState(oneX, thirtyTwoX);
    }

    /// <summary>b2：逐帧驱动（每帧 1/60 秒）下，1× 跑真实 600s 与 32× 跑真实 18.75s 得到同一状态（并证明无丢步 / 无漂移）。</summary>
    [Fact]
    public void B2_Frame_Driven_OneX_600s_Equals_32X_18_75s()
    {
        const double frame = 1d / 60d;

        SimEngine oneX = MakeEngine(multiplier: 1);
        SimEngine thirtyTwoX = MakeEngine(multiplier: 32);

        for (int frameIndex = 0; frameIndex < 36_000; frameIndex++)
        {
            oneX.Advance(frame);           // 1× 真实 600 秒（36000 帧）
        }

        for (int frameIndex = 0; frameIndex < 1_125; frameIndex++)
        {
            thirtyTwoX.Advance(frame);     // 32× 真实 18.75 秒（1125 帧）
        }

        Assert.True(oneX.IsFinished);
        Assert.True(thirtyTwoX.IsFinished);
        Assert.Equal(oneX.SimTime, thirtyTwoX.SimTime, 12);
        AssertSameState(oneX, thirtyTwoX);
    }

    /// <summary>b4：全部六档（1/2/4/8/16/32×）在同一仿真时刻（600s）状态与哈希完全一致。</summary>
    [Fact]
    public void B4_All_Speed_Gears_Reach_Identical_State()
    {
        SimEngine reference = MakeEngine(multiplier: 1);
        reference.Advance(Duration);

        foreach (int multiplier in new[] { 1, 2, 4, 8, 16, 32 })
        {
            SimEngine engine = MakeEngine(multiplier: multiplier);
            engine.Advance(Duration / multiplier);   // (600 / m) × m = 600 步预算 → 6000 步

            Assert.True(engine.IsFinished, $"×{multiplier} 未在 {Duration}s 结束。");
            AssertSameState(reference, engine);
        }
    }

    /// <summary>b3：运行期换挡（1× → 32× → 1×）不改变逻辑——与全程 1× 逐 Step 驱动到相同步数的结果一致。</summary>
    [Fact]
    public void B3_Runtime_Speed_Change_Does_Not_Alter_Logic()
    {
        SimEngine stepwise = MakeEngine(multiplier: 1);
        SimEngine shifting = MakeEngine(multiplier: 1);

        RunSteps(stepwise, 6000);          // 全程 1× 逐 Step

        shifting.SpeedMultiplier = 32;     // 中途换挡
        shifting.Advance(Duration / 32d);  // 批量推进到 6000 步
        shifting.SpeedMultiplier = 1;      // 换回

        Assert.Equal(stepwise.SimTime, shifting.SimTime, 12);
        Assert.Equal(stepwise.StateHash(), shifting.StateHash());
    }

    // ------------------------------------------------------------------
    // C 组：验收 c) 全程无 NaN / Inf
    // ------------------------------------------------------------------

    /// <summary>c1：逐 Step 跑满整局，每 100 步采样一次，位置 / 速度 / 占有率 / 相位全为有限值。</summary>
    [Fact]
    public void C1_No_NonFinite_State_During_Long_Run()
    {
        SimEngine engine = MakeEngine();

        for (int stepIndex = 0; stepIndex < 6000; stepIndex++)
        {
            engine.Step();
            if (stepIndex % 100 == 0)
            {
                AssertAllFinite(engine);
            }
        }

        AssertAllFinite(engine);
    }

    /// <summary>c2：在 32× 逐帧驱动（含批量步进）下同样全程无 NaN / Inf。</summary>
    [Fact]
    public void C2_No_NonFinite_State_Under_Frame_Driven_Advance()
    {
        SimEngine engine = MakeEngine(multiplier: 32);

        for (int frameIndex = 0; frameIndex < 1_125; frameIndex++)
        {
            engine.Advance(1d / 60d);
            if (frameIndex % 25 == 0)
            {
                AssertAllFinite(engine);
            }
        }

        AssertAllFinite(engine);
    }

    // ------------------------------------------------------------------
    // D 组：验收 d) StateHash() 供测试调用
    // ------------------------------------------------------------------

    /// <summary>d1：Snapshot().Hash 与 StateHash() 一致（快照哈希即状态哈希）。</summary>
    [Fact]
    public void D1_Snapshot_Hash_Equals_StateHash()
    {
        SimEngine engine = MakeEngine();
        RunSteps(engine, 800);

        Assert.Equal(engine.StateHash(), engine.Snapshot().Hash);
    }

    /// <summary>d2：状态一变，哈希即变（哈希覆盖可影响结果的状态，而非恒定值）。</summary>
    [Fact]
    public void D2_StateHash_Changes_With_State()
    {
        SimEngine engine = MakeEngine();

        ulong before = engine.StateHash();
        engine.Step();
        ulong after = engine.StateHash();

        Assert.NotEqual(before, after);
    }

    /// <summary>d3：每次 Snapshot() 返回新的不可变对象，但内容哈希相同。</summary>
    [Fact]
    public void D3_Snapshot_Is_Fresh_Object_Each_Call()
    {
        SimEngine engine = MakeEngine();
        RunSteps(engine, 100);

        SimState first = engine.Snapshot();
        SimState second = engine.Snapshot();

        Assert.NotSame(first, second);
        Assert.Equal(first.Hash, second.Hash);
        Assert.Equal(first.SimTime, second.SimTime, 12);
    }

    // ------------------------------------------------------------------
    // E 组：与冻结接口的对应
    // ------------------------------------------------------------------

    /// <summary>e1：SimEngine 的 public 面与 §4.3 逐字一致（构造 / 属性类型 / 方法签名）。</summary>
    [Fact]
    public void E1_SimEngine_Surface_Matches_Frozen_Contract()
    {
        Type engine = typeof(SimEngine);

        Assert.NotNull(engine.GetConstructor([typeof(CompiledNetwork), typeof(SimConfig)]));
        Assert.NotNull(engine.GetConstructor([typeof(Level), typeof(SimConfig)]));

        Assert.Equal(typeof(SimConfig), engine.GetProperty(nameof(SimEngine.Config))!.PropertyType);
        Assert.Equal(typeof(CompiledNetwork), engine.GetProperty(nameof(SimEngine.Network))!.PropertyType);
        Assert.Equal(typeof(double), engine.GetProperty(nameof(SimEngine.SimTime))!.PropertyType);
        Assert.Equal(typeof(bool), engine.GetProperty(nameof(SimEngine.IsFinished))!.PropertyType);

        var speed = engine.GetProperty(nameof(SimEngine.SpeedMultiplier))!;
        Assert.Equal(typeof(int), speed.PropertyType);
        Assert.True(speed is { CanRead: true, CanWrite: true });

        Assert.Equal(typeof(void), engine.GetMethod(nameof(SimEngine.Step), Type.EmptyTypes)!.ReturnType);
        Assert.Equal(typeof(void), engine.GetMethod(nameof(SimEngine.Step), [typeof(double)])!.ReturnType);
        Assert.Equal(typeof(void), engine.GetMethod(nameof(SimEngine.Advance), [typeof(double)])!.ReturnType);
        Assert.Equal(typeof(void), engine.GetMethod(nameof(SimEngine.Reset), [typeof(long)])!.ReturnType);
        Assert.Equal(typeof(void), engine.GetMethod(nameof(SimEngine.ApplyPlan), [typeof(int), typeof(SignalPlan)])!.ReturnType);
        Assert.Equal(typeof(SimState), engine.GetMethod(nameof(SimEngine.Snapshot), Type.EmptyTypes)!.ReturnType);
        Assert.Equal(typeof(ulong), engine.GetMethod(nameof(SimEngine.StateHash), Type.EmptyTypes)!.ReturnType);
        Assert.Equal(
            typeof(IReadOnlyList<SimEvent>),
            engine.GetMethod(nameof(SimEngine.DrainEvents), Type.EmptyTypes)!.ReturnType);
    }

    /// <summary>e2：SimConfig 默认值与 §4.2 逐字一致。</summary>
    [Fact]
    public void E2_SimConfig_Defaults_Match_Contract()
    {
        var config = new SimConfig();

        Assert.Equal(0.1d, config.Dt);
        Assert.Equal(1, config.SpeedMultiplier);
        Assert.True(config.Deterministic);
        Assert.Equal(0L, config.Seed);
        Assert.Equal(600d, config.Duration);
        Assert.Same(SignalTiming.Default, config.Timing);
        Assert.Equal(20, config.QueueCapacityPerLane);
    }

    /// <summary>e3：构造与运行期参数校验一律抛异常（契约 §1.3：不静默吞掉）。</summary>
    [Fact]
    public void E3_Argument_Validation_Throws()
    {
        CompiledNetwork network = MakeNetwork().Compile();
        SimConfig config = MakeConfig();

        Assert.Throws<ArgumentNullException>(() => new SimEngine((CompiledNetwork)null!, config));
        Assert.Throws<ArgumentNullException>(() => new SimEngine(network, null!));
        Assert.Throws<ArgumentNullException>(() => new SimEngine((Level)null!, config));

        Assert.Throws<ArgumentOutOfRangeException>(() => new SimEngine(network, new SimConfig { Dt = 0d }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimEngine(network, new SimConfig { Duration = -1d }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimEngine(network, new SimConfig { SpeedMultiplier = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimEngine(network, new SimConfig { QueueCapacityPerLane = 0 }));

        SimEngine engine = new(network, config);
        Assert.Throws<ArgumentException>(() => engine.Step(Dt * 2d));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.Advance(-1d));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.SpeedMultiplier = -1);
    }

    /// <summary>e4：SimEventKind 顺序 / SimEvent / SignalSnapshot 形状与冻结契约一致。</summary>
    [Fact]
    public void E4_Event_And_Snapshot_Types_Match_Contract()
    {
        Assert.Equal(
            ["DemandArrived", "VehiclePassed", "VehicleStopped", "QueueSample", "Collision", "Spillback"],
            Enum.GetNames<SimEventKind>());
        Assert.Equal(0, (int)SimEventKind.DemandArrived);
        Assert.Equal(5, (int)SimEventKind.Spillback);

        Assert.True(typeof(SimEvent).IsValueType);
        var e = new SimEvent(SimEventKind.Collision, 12.5d, 3, 7, 0.9d);
        Assert.Equal(SimEventKind.Collision, e.Kind);
        Assert.Equal(12.5d, e.Time);
        Assert.Equal(3, e.LaneId);
        Assert.Equal(7, e.VehicleId);
        Assert.Equal(0.9d, e.Value);

        Assert.True(typeof(SignalSnapshot).IsValueType);
        var signal = new SignalSnapshot(1, 0, 1, SignalPhaseState.Yellow, 1.5d, 1.5d);
        Assert.Equal(1, signal.NodeId);
        Assert.Equal(1, signal.StageIndex);
        Assert.Equal(SignalPhaseState.Yellow, signal.Phase);
        Assert.Equal(1.5d, signal.PhaseRemaining);

        Assert.Equal(typeof(ulong), typeof(SimState).GetProperty(nameof(SimState.Hash))!.PropertyType);
        Assert.Equal(typeof(bool), typeof(SimState).GetProperty(nameof(SimState.Collision))!.PropertyType);
    }

    /// <summary>e5：Level 直通构造（§10 D9）可用——编译路网、正常推进、状态有限。</summary>
    [Fact]
    public void E5_Level_Constructor_Compiles_And_Runs()
    {
        var level = new Level { Network = MakeNetwork(), Meta = new LevelMeta { Name = "T1.6", Chapter = 1, Index = 6 } };
        SimEngine engine = new(level, MakeConfig());

        Assert.Equal(level.Network.Lanes.Count, engine.Network.LaneCount);

        engine.SeedQueue(0, 5);
        RunSteps(engine, 500);

        AssertAllFinite(engine);
    }

    /// <summary>e6：ApplyPlan 委托信号状态机并校验节点（非信号 / 越界 / null 方案均抛异常）。</summary>
    [Fact]
    public void E6_ApplyPlan_Delegates_And_Validates()
    {
        SimEngine engine = MakeEngine();

        Assert.Throws<ArgumentException>(() => engine.ApplyPlan(1, MakePlan()));       // 非信号节点（Origin）
        Assert.Throws<ArgumentException>(() => engine.ApplyPlan(99, MakePlan()));      // 越界
        Assert.Throws<ArgumentNullException>(() => engine.ApplyPlan(0, null!));        // 空方案

        engine.ApplyPlan(0, MakePlan());   // 合法：下一周期生效
        RunSteps(engine, 1200);            // 跨过周期边界（60s = 600 步）
        AssertAllFinite(engine);
    }

    /// <summary>e7：DrainEvents 顺序确定、同 seed 两次运行事件序列逐项相同，且调用后清空。</summary>
    [Fact]
    public void E7_DrainEvents_Is_Deterministic_And_Clears()
    {
        SimEngine first = MakeEngine();
        SimEngine second = MakeEngine();

        RunSteps(first, 400);
        RunSteps(second, 400);

        IReadOnlyList<SimEvent> firstEvents = first.DrainEvents();
        IReadOnlyList<SimEvent> secondEvents = second.DrainEvents();

        Assert.True(firstEvents.Count > 0, "本场景应产生事件。");
        Assert.Equal(firstEvents.Count, secondEvents.Count);
        for (int i = 0; i < firstEvents.Count; i++)
        {
            Assert.Equal(firstEvents[i], secondEvents[i]);
        }

        Assert.Empty(first.DrainEvents());   // 已清空
    }

    // ------------------------------------------------------------------
    // 工具
    // ------------------------------------------------------------------

    /// <summary>构造一辆固定时钟 + 信号路口进口道的测试引擎（可指定加速档 / 种子 / 输入顺序）。</summary>
    private static SimEngine MakeEngine(int multiplier = 1, long seed = 42L, bool networkReversed = false)
    {
        var engine = new SimEngine(MakeNetwork(networkReversed).Compile(), MakeConfig(seed, multiplier));
        engine.SeedQueue(0, 12);                 // 停止线满排
        engine.SetArrivalRate(0, 0.25d);         // 恒定到达（辆/秒）
        return engine;
    }

    private static SimConfig MakeConfig(long seed = 42L, int multiplier = 1) => new()
    {
        Dt = Dt,
        SpeedMultiplier = multiplier,
        Deterministic = true,
        Seed = seed,
        Duration = Duration,
        Timing = SignalTiming.Default,
        QueueCapacityPerLane = 20,
    };

    /// <summary>
    /// 三节点两点路网：Origin(1) → Signalized(0) → Destination(2)。
    /// 进口道 lane0（长 200m）在节点 0 受两相位配时控制，经转向 0 汇入下游 lane1。
    /// </summary>
    private static Network MakeNetwork(bool reversed = false)
    {
        Node[] nodes =
        [
            new Node { Id = 0, Type = NodeType.Signalized, X = 0d, Y = 0d, Offset = 0d, SignalPlans = [MakePlan()] },
            new Node { Id = 1, Type = NodeType.Origin, X = 0d, Y = -200d },
            new Node { Id = 2, Type = NodeType.Destination, X = 0d, Y = 200d },
        ];

        Lane[] lanes =
        [
            new Lane { Id = 0, Road = 0, Type = LaneType.Driving, FromNode = 1, ToNode = 0, Length = 200d, SpeedLimit = 13.89d, LaneIndex = 0 },
            new Lane { Id = 1, Road = 0, Type = LaneType.Driving, FromNode = 0, ToNode = 2, Length = 200d, SpeedLimit = 13.89d, LaneIndex = 0 },
        ];

        Turn[] turns =
        [
            new Turn { Id = 0, SrcLane = 0, DstLane = 1, Node = 0, Type = TurnType.Straight },
        ];

        if (reversed)
        {
            Array.Reverse(nodes);
            Array.Reverse(lanes);
            Array.Reverse(turns);
        }

        return new Network { Nodes = nodes, Lanes = lanes, Turns = turns };
    }

    /// <summary>两相位配时：相位 A 保护放行 turn0（绿灯 30s），相位 B 空（绿灯 20s）；清空时间取默认（黄 3 + 全红 2）。</summary>
    private static SignalPlan MakePlan() => new()
    {
        StartTime = 0d,
        Stages =
        [
            new Stage { Type = StageType.Fixed, Duration = 30d, ProtectedTurnIds = [0] },
            new Stage { Type = StageType.Fixed, Duration = 20d },
        ],
    };

    private static void RunSteps(SimEngine engine, int steps)
    {
        for (int i = 0; i < steps; i++)
        {
            engine.Step();
        }
    }

    /// <summary>逐元素比较两个引擎的状态：仿真时刻、状态哈希、车辆 / 车道 / 信号快照。</summary>
    private static void AssertSameState(SimEngine expected, SimEngine actual)
    {
        Assert.Equal(expected.SimTime, actual.SimTime, 12);
        Assert.Equal(expected.StateHash(), actual.StateHash());

        SimState a = expected.Snapshot();
        SimState b = actual.Snapshot();

        Assert.Equal(a.Vehicles.Count, b.Vehicles.Count);
        for (int i = 0; i < a.Vehicles.Count; i++)
        {
            Assert.Equal(a.Vehicles[i].Id, b.Vehicles[i].Id);
            Assert.Equal(a.Vehicles[i].LaneId, b.Vehicles[i].LaneId);
            Assert.Equal(a.Vehicles[i].Kind, b.Vehicles[i].Kind);
            Assert.Equal(a.Vehicles[i].Position, b.Vehicles[i].Position, 12);
            Assert.Equal(a.Vehicles[i].Speed, b.Vehicles[i].Speed, 12);
        }

        Assert.Equal(a.Lanes.Count, b.Lanes.Count);
        for (int i = 0; i < a.Lanes.Count; i++)
        {
            Assert.Equal(a.Lanes[i], b.Lanes[i]);
        }

        Assert.Equal(a.Signals.Count, b.Signals.Count);
        for (int i = 0; i < a.Signals.Count; i++)
        {
            Assert.Equal(a.Signals[i], b.Signals[i]);
        }
    }

    /// <summary>断言当前快照中的全部数值均为有限值（无 NaN / Inf）。</summary>
    private static void AssertAllFinite(SimEngine engine)
    {
        AssertFinite(engine.SimTime, "SimTime");

        SimState state = engine.Snapshot();

        foreach (VehicleSnapshot vehicle in state.Vehicles)
        {
            AssertFinite(vehicle.Position, $"Vehicle#{vehicle.Id}.Position");
            AssertFinite(vehicle.Speed, $"Vehicle#{vehicle.Id}.Speed");
        }

        foreach (LaneSnapshot lane in state.Lanes)
        {
            AssertFinite(lane.Occupancy, $"Lane#{lane.LaneId}.Occupancy");
        }

        foreach (SignalSnapshot signal in state.Signals)
        {
            AssertFinite(signal.TimeInPhase, $"Signal#{signal.NodeId}.TimeInPhase");
            AssertFinite(signal.PhaseRemaining, $"Signal#{signal.NodeId}.PhaseRemaining");
        }
    }

    private static void AssertFinite(double value, string what)
    {
        Assert.False(double.IsNaN(value) || double.IsInfinity(value), $"{what} 非有限值：{value}。");
    }
}
