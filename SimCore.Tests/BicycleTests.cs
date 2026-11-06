using System.Reflection;
using System.Runtime.CompilerServices;
using SimCore.Geometry;
using SimCore.Io;
using SimCore.Model;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

/// <summary>
/// T1.5「非机动车模型（独立非机动车道 / 机非混行）」验收测试。
/// <para>分组与验收标准逐条对应：
/// <list type="bullet">
///   <item>A 组 → 验收 a) 独立模式受自身相位控制（不受机动车相位控制）</item>
///   <item>B 组 → 验收 b) 混行模式随本向机动车相位放行；追加约束：混行不得单独占用相位</item>
///   <item>C 组 → 验收 c) 起步特性慢于机动车（量化断言 + 行为断言）</item>
///   <item>D 组 → 与冻结接口的对应 / 独立类型 / 确定性 / 参数校验 / 运动与快照</item>
/// </list>
/// </para>
/// <para>测试路网见 <see cref="BikeNetwork"/>：4 路口机动车路网（南/东/北/西进口 Driving 车道 + 出口车道）
/// 追加 2 条 <see cref="LaneType.Biking"/> 进口车道（id 8/9）与 2 条 Biking 出口车道（id 10/11），
/// 并新增 2 条非机动车转向：<c>BIKE_S(4) = 8→10</c>（南进口非机动车直行）、<c>BIKE_N(5) = 9→11</c>。</para>
/// </summary>
public sealed class BicycleTests
{
    // ------------------------------------------------------------------
    // 转向 id 约定
    //   机动车直行：SS(0)=0→6  ES(1)=1→7  NS(2)=2→4  WS(3)=3→5
    //   非机动车  ：BIKE_S(4)=8→10（南进口） BIKE_N(5)=9→11（北进口）
    // ------------------------------------------------------------------
    private const int SS = 0;       // 南进口直行（机动车）
    private const int ES = 1;       // 东进口直行（机动车）
    private const int NS = 2;       // 北进口直行（机动车）
    private const int WS = 3;       // 西进口直行（机动车）
    private const int BIKE_S = 4;   // 南进口非机动车直行（本车道自身转向）
    private const int BIKE_N = 5;   // 北进口非机动车直行

    // 车道 id 约定（见 BikeNetwork）
    private const int LaneBikeS = 8;   // 南进口非机动车道（Biking，road 1）
    private const int LaneDrivingS = 0; // 南进口机动车道（Driving，road 1）

    private const double Dt = 0.5d;

    // ==================================================================
    // A 组：验收 a) 独立模式受自身相位控制
    // ==================================================================

    /// <summary>a1：独立非机动车道进入**自身相位**（prot:[BIKE_S]）后放行；进入机动车相位后一律不放行。</summary>
    [Fact]
    public void A1_Independent_ReleasedByOwnPhaseOnly()
    {
        // 第 0 相位 = 非机动车自身相位；第 1 相位 = 南北机动车相位。
        SignalPlan plan = Plan(Stage(20d, prot: [BIKE_S]), Stage(20d, prot: [SS, NS]));
        (CompiledNetwork net, IntersectionGate gate) = Fixture(plan);
        var signal = new SignalStateMachine(net.GetNode(0), SignalTiming.Default);
        signal.SyncToTime(0d);
        var flow = new BicycleFlow(gate, net, LaneBikeS, BicycleMode.Independent);

        var b0 = new Bicycle(0, LaneBikeS, BicycleMode.Independent);
        flow.Arrive(b0);

        // 自身相位绿灯起点 t=0 → 起步损失时间（2.0 s）后放行。
        int first = Run(flow, signal, Dt, 5.0);
        Assert.Equal(1, first);
        Assert.True(b0.IsRiding, "自身相位（prot:[BIKE_S]）应放行独立非机动车。");
        Assert.Equal(2.0, b0.StartTime, 6);

        // 第 1 相位（机动车相位，t∈[25,45) 绿）内到达者：本车道转向被禁行 → 不放行。
        Run(flow, signal, Dt, 20.5);   // t = 25.5
        var b1 = new Bicycle(1, LaneBikeS, BicycleMode.Independent);
        flow.Arrive(b1);
        int second = Run(flow, signal, Dt, 15.0);   // t = 40.5
        Assert.Equal(0, second);
        Assert.True(b1.Waiting, "机动车相位不得放行独立非机动车（自身相位才可控）。");
    }

    /// <summary>a2：若玩家的任何相位都未放行本车道自身转向，独立非机动车**永不放行**（受自身相位控制，不受机动车相位牵连）。</summary>
    [Fact]
    public void A2_Independent_NotReleasedByMotorPhases()
    {
        // 单相位：只放行南北机动车直行（SS/NS），非机动车转向 BIKE_S 不在任何相位。
        SignalPlan plan = Plan(Stage(20d, prot: [SS, NS]));
        (CompiledNetwork net, IntersectionGate gate) = Fixture(plan);
        var signal = new SignalStateMachine(net.GetNode(0), SignalTiming.Default);
        signal.SyncToTime(0d);
        var flow = new BicycleFlow(gate, net, LaneBikeS, BicycleMode.Independent);

        var b0 = new Bicycle(0, LaneBikeS, BicycleMode.Independent);
        flow.Arrive(b0);

        int released = Run(flow, signal, Dt, 50.0);   // 2 个周期
        Assert.Equal(0, released);
        Assert.True(b0.Waiting);
        Assert.Equal(-1.0, b0.StartTime);   // 从未起步
    }

    /// <summary>a3：黄灯 / 全红期间一律不放行（即使相位对象本身仍把本车道转向标为 Protected）。</summary>
    [Fact]
    public void A3_YellowAndAllRed_DoNotRelease()
    {
        // 单相位：绿灯 10 s（[0,10)）→ 黄（[10,13)）→ 全红（[13,15)）→ 周期 15 s。
        SignalPlan plan = Plan(Stage(10d, prot: [BIKE_S]));
        (CompiledNetwork net, IntersectionGate gate) = Fixture(plan);
        var signal = new SignalStateMachine(net.GetNode(0), SignalTiming.Default);
        signal.SyncToTime(0d);
        var flow = new BicycleFlow(gate, net, LaneBikeS, BicycleMode.Independent);

        Run(flow, signal, Dt, 10.5);   // t = 10.5（黄灯）
        var b0 = new Bicycle(0, LaneBikeS, BicycleMode.Independent);
        flow.Arrive(b0);

        int released = Run(flow, signal, Dt, 4.0);   // t = 14.5（黄 + 全红）
        Assert.Equal(0, released);
        Assert.True(b0.Waiting, "黄灯 / 全红期间不得放行非机动车。");
        Assert.Equal(4.0, b0.WaitTime, 6);
    }

    // ==================================================================
    // B 组：验收 b) 混行模式随本向机动车相位放行 + 追加约束（不得单独占用相位）
    // ==================================================================

    /// <summary>b1：混行的参考转向 = 同向机动车转向；跟着**本向机动车相位**放行，不被别的非机动车相位放行。</summary>
    [Fact]
    public void B1_Mixed_FollowsOwnDirectionMotorPhase()
    {
        // 第 0 相位：南进口机动车直行 SS；第 1 相位：北进口非机动车 BIKE_N（与南进口无关）。
        SignalPlan plan = Plan(Stage(20d, prot: [SS]), Stage(20d, prot: [BIKE_N]));
        (CompiledNetwork net, IntersectionGate gate) = Fixture(plan);
        var signal = new SignalStateMachine(net.GetNode(0), SignalTiming.Default);
        signal.SyncToTime(0d);
        var flow = new BicycleFlow(gate, net, LaneBikeS, BicycleMode.Mixed);

        Assert.Equal(BicycleMode.Mixed, flow.Mode);
        Assert.Equal(SS, flow.ReferenceTurnId);            // 参考转向 = 同向机动车转向（本向）
        Assert.NotEqual(BIKE_S, flow.ReferenceTurnId);     // 不是本车道自身转向

        var b0 = new Bicycle(0, LaneBikeS, BicycleMode.Mixed);
        flow.Arrive(b0);

        // 本向机动车相位（SS）绿灯起点 t=0 → 起步损失（2.0 + 1.0 = 3.0 s）后放行。
        int first = Run(flow, signal, Dt, 5.0);
        Assert.Equal(1, first);
        Assert.Equal(3.0, b0.StartTime, 6);
        Assert.True(b0.IsRiding, "混行非机动车应随本向机动车相位放行。");

        // 第 1 相位（北进口非机动车相位，与本向机动车无关）内到达者：本向机动车转向被禁行 → 不放行。
        Run(flow, signal, Dt, 20.5);   // t = 25.5
        var b1 = new Bicycle(1, LaneBikeS, BicycleMode.Mixed);
        flow.Arrive(b1);
        int second = Run(flow, signal, Dt, 15.0);   // t = 40.5
        Assert.Equal(0, second);
        Assert.True(b1.Waiting, "非本向相位不得放行混行非机动车。");
    }

    /// <summary>
    /// b2：**追加约束「混行不得单独占用相位」**——即使玩家把本车道自身转向 BIKE_S 单列一个相位（protected），
    /// 只要同向机动车转向 SS 未被任何相位放行，混行非机动车就**永不放行**（不存在"混行专属相位"）。
    /// </summary>
    [Fact]
    public void B2_Mixed_CannotOccupyItsOwnPhase()
    {
        // 第 1 相位把本车道自身非机动车转向 BIKE_S 保护放行；但同向机动车 SS 从未被放行。
        SignalPlan plan = Plan(Stage(20d, prot: [NS]), Stage(20d, prot: [BIKE_S]));
        (CompiledNetwork net, IntersectionGate gate) = Fixture(plan);
        var signal = new SignalStateMachine(net.GetNode(0), SignalTiming.Default);
        signal.SyncToTime(0d);
        var flow = new BicycleFlow(gate, net, LaneBikeS, BicycleMode.Mixed);

        var b0 = new Bicycle(0, LaneBikeS, BicycleMode.Mixed);
        flow.Arrive(b0);

        int released = Run(flow, signal, Dt, 100.0);   // 2 个完整周期
        Assert.Equal(0, released);
        Assert.True(b0.Waiting, "混行非机动车只能随本向机动车相位放行，不得单独占用相位。");
    }

    /// <summary>b3：参考转向解析与构造校验——混行须为同向机动车转向；独立须为本车道自身转向。</summary>
    [Fact]
    public void B3_ReferenceTurnResolution_AndConstructionGuards()
    {
        (CompiledNetwork net, IntersectionGate gate) = Fixture();

        Assert.Equal(SS, BicycleFlow.ResolveReferenceTurn(net, LaneBikeS, BicycleMode.Mixed));
        Assert.Equal(BIKE_S, BicycleFlow.ResolveReferenceTurn(net, LaneBikeS, BicycleMode.Independent));

        // 混行以本车道自身非机动车转向为参考转向 → 拒绝（不得单独占用相位）。
        Assert.Throws<ArgumentException>(() => new BicycleFlow(gate, net, LaneBikeS, BicycleMode.Mixed, BIKE_S));
        // 混行以跨向（东进口机动车 ES，road 2）转向为参考 → 拒绝（非同向）。
        Assert.Throws<ArgumentException>(() => new BicycleFlow(gate, net, LaneBikeS, BicycleMode.Mixed, ES));
        // 独立以机动车转向为参考转向 → 拒绝（独立须以本车道为源车道）。
        Assert.Throws<ArgumentException>(() => new BicycleFlow(gate, net, LaneBikeS, BicycleMode.Independent, SS));

        Assert.Throws<ArgumentException>(() => new BicycleFlow(gate, net, LaneDrivingS, BicycleMode.Independent)); // 车道 0 是 Driving，非 Biking
        Assert.Throws<ArgumentOutOfRangeException>(() => new BicycleFlow(gate, net, 99, BicycleMode.Independent));
        Assert.Throws<ArgumentNullException>(() => new BicycleFlow(null!, net, LaneBikeS, BicycleMode.Independent));
        Assert.Throws<ArgumentNullException>(() => new BicycleFlow(gate, null!, LaneBikeS, BicycleMode.Independent));
    }

    // ==================================================================
    // C 组：验收 c) 起步特性慢于机动车（量化断言）
    // ==================================================================

    /// <summary>c1：起步损失时间公式——混行 = 机动车起步损失时间 + BicycleStartDelay；量化差恰为 1.0 s。</summary>
    [Fact]
    public void C1_StartUpLostTime_MixedIsSlowerThanMotorByBicycleStartDelay()
    {
        Assert.Equal(2.0d, SimConstants.StartUpLostTime, 9);       // 机动车起步损失时间（§4.1）
        Assert.Equal(1.0d, SimConstants.BicycleStartDelay, 9);     // 混行非机动车起步附加延迟（§4.1，T1.5 c）

        Assert.Equal(2.0d, Bicycle.BicycleStartUpLostTimeFor(BicycleMode.Independent), 9);
        Assert.Equal(3.0d, Bicycle.BicycleStartUpLostTimeFor(BicycleMode.Mixed), 9);

        double motor = SimConstants.StartUpLostTime;
        double mixed = Bicycle.BicycleStartUpLostTimeFor(BicycleMode.Mixed);

        // 量化断言：混行非机动车起步损失时间 = 机动车起步损失时间 + BicycleStartDelay（严格大于）。
        Assert.Equal(motor + SimConstants.BicycleStartDelay, mixed, 9);
        Assert.Equal(SimConstants.BicycleStartDelay, mixed - motor, 9);
        Assert.True(mixed > motor, "混行非机动车起步必须慢于机动车。");
    }

    /// <summary>c2：混行行为断言——本向机动车绿灯起点后，前进 2.5 s 仍不动，到 3.0 s（= 2.0 + 1.0）才起步。</summary>
    [Fact]
    public void C2_Mixed_StartsAtMotorLossPlusBicycleStartDelay()
    {
        SignalPlan plan = Plan(Stage(30d, prot: [SS]));
        (CompiledNetwork net, IntersectionGate gate) = Fixture(plan);
        var signal = new SignalStateMachine(net.GetNode(0), SignalTiming.Default);
        signal.SyncToTime(0d);
        var flow = new BicycleFlow(gate, net, LaneBikeS, BicycleMode.Mixed);

        var b0 = new Bicycle(0, LaneBikeS, BicycleMode.Mixed);
        flow.Arrive(b0);

        int early = Run(flow, signal, Dt, 2.5);   // 绿灯已 2.5 s：仍不足 3.0 s
        Assert.Equal(0, early);
        Assert.True(b0.Waiting, "混行非机动车在 2.5 s（< 3.0 s）不得起步。");

        int start = Run(flow, signal, Dt, 0.5);   // t = 3.0
        Assert.Equal(1, start);
        Assert.Equal(3.0, b0.StartTime, 6);
        Assert.Equal(Bicycle.BicycleStartUpLostTimeFor(BicycleMode.Mixed), b0.StartTime, 6);
    }

    /// <summary>c3：对照断言——独立模式起步（= 机动车起步损失时间 2.0 s），混行比它多一个 BicycleStartDelay。</summary>
    [Fact]
    public void C3_Independent_StartsAtMotorLoss_MixedIsOneDelaySlower()
    {
        SignalPlan plan = Plan(Stage(30d, prot: [BIKE_S]));
        (CompiledNetwork net, IntersectionGate gate) = Fixture(plan);
        var signal = new SignalStateMachine(net.GetNode(0), SignalTiming.Default);
        signal.SyncToTime(0d);
        var flow = new BicycleFlow(gate, net, LaneBikeS, BicycleMode.Independent);

        var b0 = new Bicycle(0, LaneBikeS, BicycleMode.Independent);
        flow.Arrive(b0);

        int early = Run(flow, signal, Dt, 1.5);
        Assert.Equal(0, early);

        int start = Run(flow, signal, Dt, 0.5);   // t = 2.0
        Assert.Equal(1, start);
        Assert.Equal(SimConstants.StartUpLostTime, b0.StartTime, 6);

        double independent = Bicycle.BicycleStartUpLostTimeFor(BicycleMode.Independent);
        double mixed = Bicycle.BicycleStartUpLostTimeFor(BicycleMode.Mixed);

        Assert.Equal(SimConstants.BicycleStartDelay, mixed - independent, 9);   // 混行比（机动车基线）慢一个附加延迟
        Assert.True(mixed > SimConstants.StartUpLostTime);                      // 混行慢于机动车
        Assert.Equal(3.0d - 2.0d, mixed - independent, 9);
    }

    // ==================================================================
    // D 组：冻结接口对应 / 独立类型 / 确定性 / 参数校验 / 运动与快照
    // ==================================================================

    /// <summary>d1：<see cref="BicycleSnapshot"/> 与 §4.4 冻结记录签名逐字段一致（5 个 get-only 属性，名称 / 类型 / 顺序）。</summary>
    [Fact]
    public void D1_BicycleSnapshot_MatchesFrozenSignature()
    {
        Type t = typeof(BicycleSnapshot);
        Assert.True(t.IsValueType, "§4.4 的 BicycleSnapshot 是 readonly record struct。");

        PropertyInfo[] props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(5, props.Length);

        AssertProperty(props, "Id", typeof(int));
        AssertProperty(props, "LaneId", typeof(int));
        AssertProperty(props, "Position", typeof(double));
        AssertProperty(props, "Mode", typeof(BicycleMode));
        AssertProperty(props, "Stopped", typeof(bool));

        // 位置参数顺序 = 冻结签名 (Id, LaneId, Position, Mode, Stopped)。
        BicycleSnapshot s = new(7, LaneBikeS, 3.5d, BicycleMode.Mixed, true);
        Assert.Equal(7, s.Id);
        Assert.Equal(LaneBikeS, s.LaneId);
        Assert.Equal(3.5d, s.Position, 9);
        Assert.Equal(BicycleMode.Mixed, s.Mode);
        Assert.True(s.Stopped);
    }

    /// <summary>d2：非机动车用**独立类型**——<see cref="Bicycle"/> 是 sealed 且与 Vehicle / Pedestrian 无继承关系。</summary>
    [Fact]
    public void D2_Bicycle_IsIndependentType()
    {
        Assert.True(typeof(Bicycle).IsSealed);
        Assert.Equal(typeof(object), typeof(Bicycle).BaseType);
        Assert.False(typeof(Vehicle).IsAssignableFrom(typeof(Bicycle)));
        Assert.False(typeof(Bicycle).IsAssignableFrom(typeof(Vehicle)));
        Assert.False(typeof(Pedestrian).IsAssignableFrom(typeof(Bicycle)));
        Assert.False(typeof(Bicycle).IsAssignableFrom(typeof(Pedestrian)));

        // 非机动车状态机与车辆 / 行人状态机是互不相干的枚举。
        Assert.NotEqual(typeof(VehicleState), typeof(BicycleState));
        Assert.NotEqual(typeof(PedestrianState), typeof(BicycleState));
    }

    /// <summary>d3：确定性——同一场景两次运行，首次起步时刻与全部快照逐项相等（铁律 2）。</summary>
    [Fact]
    public void D3_Deterministic_AcrossRuns()
    {
        (double firstTime, IReadOnlyList<BicycleSnapshot> firstSnaps) = DeterministicRun();
        (double secondTime, IReadOnlyList<BicycleSnapshot> secondSnaps) = DeterministicRun();

        Assert.Equal(firstTime, secondTime, 9);
        Assert.Equal(firstSnaps, secondSnaps);
    }

    /// <summary>d4：参数校验——构造 / 步进 / 登记 对非法入参一律抛异常（§1.3）。</summary>
    [Fact]
    public void D4_ArgumentValidation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bicycle(-1, LaneBikeS, BicycleMode.Independent));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bicycle(0, -1, BicycleMode.Independent));

        (CompiledNetwork net, IntersectionGate gate) = Fixture();
        var signal = new SignalStateMachine(net.GetNode(0), SignalTiming.Default);
        signal.SyncToTime(0d);
        var flow = new BicycleFlow(gate, net, LaneBikeS, BicycleMode.Independent);

        Assert.Throws<ArgumentOutOfRangeException>(() => flow.Step(0d, signal));
        Assert.Throws<ArgumentOutOfRangeException>(() => flow.Step(double.NaN, signal));
        Assert.Throws<ArgumentNullException>(() => flow.Step(Dt, null!));
        Assert.Throws<ArgumentNullException>(() => flow.Arrive(null!));
        Assert.Throws<ArgumentException>(() => flow.Arrive(new Bicycle(0, LaneDrivingS, BicycleMode.Independent)));   // 属于另一条车道

        var b = new Bicycle(1, LaneBikeS, BicycleMode.Independent);
        flow.Arrive(b);
        Assert.Throws<ArgumentException>(() => flow.Arrive(b));   // 重复登记
    }

    /// <summary>d5：运动与快照——放行后在车道内匀直前进、到末端驶离；快照不含已通过者。</summary>
    [Fact]
    public void D5_MovementAndSnapshots()
    {
        SignalPlan plan = Plan(Stage(30d, prot: [SS]));
        (CompiledNetwork net, IntersectionGate gate) = Fixture(plan);
        var signal = new SignalStateMachine(net.GetNode(0), SignalTiming.Default);
        signal.SyncToTime(0d);
        var flow = new BicycleFlow(gate, net, LaneBikeS, BicycleMode.Mixed);
        Assert.Equal(0, flow.WaitingCount + flow.RidingCount + flow.ExitedCount);

        var b0 = new Bicycle(0, LaneBikeS, BicycleMode.Mixed);
        flow.Arrive(b0);

        flow.Arrive(new Bicycle(1, LaneBikeS, BicycleMode.Mixed));
        Assert.Equal(2, flow.WaitingCount);

        double releasedAt = RunUntilReleased(flow, signal, Dt, 10.0);
        Assert.Equal(3.0, releasedAt, 6);                 // 混行起步 = 2.0 + 1.0
        Assert.Equal(2, flow.RidingCount);
        Assert.Equal(2, flow.Snapshots().Count);          // 等待 + 通行中都在快照里

        double p0 = b0.Position;
        Run(flow, signal, Dt, 5.0);
        Assert.True(b0.Position > p0, "放行后应在车道内前进。");

        Run(flow, signal, Dt, 15.0);                      // 足够跑到车道末端
        Assert.Equal(0, flow.RidingCount);
        Assert.Equal(2, flow.ExitedCount);
        Assert.Equal(BicycleState.Exited, b0.State);
        Assert.Equal(net.GetLane(LaneBikeS).Length, b0.Position, 6);
        Assert.Empty(flow.Snapshots());                   // 已通过者不再出现在快照里
    }

    // ==================================================================
    // 夹具
    // ==================================================================

    /// <summary>主配时（含南进口非机动车自身相位）。</summary>
    private static SignalPlan BikePlan()
        => Plan(
            Stage(20d, prot: [SS, NS], perm: [ES, WS]),   // 第 0 相位：南北机动车直行
            Stage(20d, prot: [BIKE_S]),                    // 第 1 相位：南进口非机动车自身相位
            Stage(20d, prot: [BIKE_N]));                   // 第 2 相位：北进口非机动车自身相位

    private static (CompiledNetwork Net, IntersectionGate Gate) Fixture()
        => Fixture(BikePlan());

    private static (CompiledNetwork Net, IntersectionGate Gate) Fixture(SignalPlan plan)
    {
        CompiledNetwork net = BikeNetwork(plan).Compile();
        ConflictMatrix conflicts = ConflictMatrix.From(net);
        return (net, IntersectionGate.Build(net, conflicts));
    }

    /// <summary>
    /// 4 路口机动车路网（nodes 0..4 / lanes 0..7）+ 非机动车车道（lanes 8..11）+ 非机动车转向（turns 4/5）。
    /// </summary>
    private static Network BikeNetwork(SignalPlan plan)
    {
        Node[] nodes =
        [
            new Node { Id = 0, Type = NodeType.Signalized, X = 0d, Y = 0d, SignalPlans = [plan] },
            new Node { Id = 1, Type = NodeType.Unsignalized, X = 0d, Y = -180d },   // S
            new Node { Id = 2, Type = NodeType.Unsignalized, X = 180d, Y = 0d },     // E
            new Node { Id = 3, Type = NodeType.Unsignalized, X = 0d, Y = 180d },     // N
            new Node { Id = 4, Type = NodeType.Unsignalized, X = -180d, Y = 0d },    // W
        ];

        Lane[] lanes =
        [
            Lane(0, road: 1, from: 1, to: 0),                                        // 南进口（机动车）
            Lane(1, road: 2, from: 2, to: 0),                                        // 东进口（机动车）
            Lane(2, road: 3, from: 3, to: 0),                                        // 北进口（机动车）
            Lane(3, road: 4, from: 4, to: 0),                                        // 西进口（机动车）
            Lane(4, road: 1, from: 0, to: 1),                                        // 南出口
            Lane(5, road: 2, from: 0, to: 2),                                        // 东出口
            Lane(6, road: 3, from: 0, to: 3),                                        // 北出口
            Lane(7, road: 4, from: 0, to: 4),                                        // 西出口
            Lane(8, road: 1, from: 1, to: 0, laneIndex: 1, type: LaneType.Biking),    // 南进口非机动车道
            Lane(9, road: 3, from: 3, to: 0, laneIndex: 1, type: LaneType.Biking),    // 北进口非机动车道
            Lane(10, road: 3, from: 0, to: 3, laneIndex: 1, type: LaneType.Biking),   // 北出口非机动车道
            Lane(11, road: 1, from: 0, to: 1, laneIndex: 1, type: LaneType.Biking),   // 南出口非机动车道
        ];

        Turn[] turns =
        [
            Turn(SS, 0, 6, 0, TurnType.Straight),        // 南进口直行（机动车）
            Turn(ES, 1, 7, 0, TurnType.Straight),        // 东进口直行（机动车）
            Turn(NS, 2, 4, 0, TurnType.Straight),        // 北进口直行（机动车）
            Turn(WS, 3, 5, 0, TurnType.Straight),        // 西进口直行（机动车）
            Turn(BIKE_S, 8, 10, 0, TurnType.Straight),   // 南进口非机动车直行
            Turn(BIKE_N, 9, 11, 0, TurnType.Straight),   // 北进口非机动车直行
        ];

        return new Network { Nodes = nodes, Lanes = lanes, Turns = turns };
    }

    /// <summary>推进 <paramref name="seconds"/> 秒（每步先 <c>Signal.Tick</c> 再 <c>Flow.Step</c>），返回累计放行车辆数。</summary>
    private static int Run(BicycleFlow flow, SignalStateMachine signal, double dt, double seconds)
    {
        int steps = (int)Math.Round(seconds / dt);
        int released = 0;
        for (int i = 0; i < steps; i++)
        {
            signal.Tick(dt);
            released += flow.Step(dt, signal);
        }

        return released;
    }

    /// <summary>推进直到首次放行，返回放行时刻（仿真时刻）；<paramref name="maxSeconds"/> 内未放行返回 -1。</summary>
    private static double RunUntilReleased(BicycleFlow flow, SignalStateMachine signal, double dt, double maxSeconds)
    {
        int steps = (int)Math.Round(maxSeconds / dt);
        for (int i = 0; i < steps; i++)
        {
            signal.Tick(dt);
            if (flow.Step(dt, signal) > 0)
            {
                return flow.Now;
            }
        }

        return -1d;
    }

    /// <summary>确定性复现：固定场景跑一遍，返回（首次放行时刻, 全程快照序列）。</summary>
    private static (double ReleasedAt, IReadOnlyList<BicycleSnapshot> Snaps) DeterministicRun()
    {
        SignalPlan plan = Plan(Stage(30d, prot: [SS]));
        (CompiledNetwork net, IntersectionGate gate) = Fixture(plan);
        var signal = new SignalStateMachine(net.GetNode(0), SignalTiming.Default);
        signal.SyncToTime(0d);
        var flow = new BicycleFlow(gate, net, LaneBikeS, BicycleMode.Mixed);

        flow.Arrive(new Bicycle(0, LaneBikeS, BicycleMode.Mixed));
        flow.Arrive(new Bicycle(1, LaneBikeS, BicycleMode.Mixed));
        double releasedAt = RunUntilReleased(flow, signal, Dt, 10.0);
        Run(flow, signal, Dt, 2.0);
        return (releasedAt, flow.Snapshots());
    }

    private static void AssertProperty(PropertyInfo[] props, string name, Type type)
    {
        PropertyInfo? prop = Array.Find(props, p => p.Name == name);
        Assert.NotNull(prop);
        Assert.Equal(type, prop!.PropertyType);

        // record 位置参数生成的是 init-only 属性：有 get，且 setter 带 IsExternalInit 修饰符。
        MethodInfo? setter = prop.SetMethod;
        bool initOnly = setter is not null
            && setter.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));
        Assert.True(initOnly, $"BicycleSnapshot.{name} 应为 init-only（只读快照，§4.4）。");
    }

    private static Lane Lane(int id, int road, int from, int to, double length = 180d, int laneIndex = 0, LaneType type = LaneType.Driving)
        => new()
        {
            Id = id,
            Road = road,
            Type = type,
            FromNode = from,
            ToNode = to,
            Length = length,
            SpeedLimit = 13.89d,
            LaneIndex = laneIndex,
        };

    private static Turn Turn(int id, int src, int dst, int node, TurnType type)
        => new() { Id = id, SrcLane = src, DstLane = dst, Node = node, Type = type };

    private static Stage Stage(double duration, int[]? prot = null, int[]? perm = null)
        => new()
        {
            Type = StageType.Fixed,
            Duration = duration,
            ProtectedTurnIds = prot ?? [],
            PermittedTurnIds = perm ?? [],
        };

    private static SignalPlan Plan(params Stage[] stages)
        => new() { StartTime = 0d, Stages = stages };
}
