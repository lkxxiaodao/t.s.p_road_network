using System.Reflection;
using System.Runtime.CompilerServices;
using SimCore.Geometry;
using SimCore.Model;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

/// <summary>
/// T1.4「行人模型（等齐过街）」验收测试。
/// <para>分组与验收标准逐条对应：
/// <list type="bullet">
///   <item>A 组 → 验收 a) 行人仅在自身相位内通过（黄 / 全红 / 人行横道被禁行时不放行）</item>
///   <item>B 组 → 验收 b) 绿灯末尾新到行人等下一周期</item>
///   <item>C 组 → 验收 c) 过街最短绿灯 = 距离 / 1.4 s（阈值 + 物理过街时长）</item>
///   <item>D 组 → 验收 d) 与转向车流的冲突被门控拦住（复用 T1.3 ConflictMatrix + IntersectionGate）</item>
///   <item>E 组 → 与冻结接口的对应 / 行人独立类型 / 确定性 / 参数校验</item>
/// </list>
/// </para>
/// <para>测试路网见 <see cref="PedNetwork"/>：在 4 路口机动车路网基础上，追加两条 <see cref="LaneType.Sidewalk"/>
/// 车道（id 8/9）与一条 <see cref="TurnType.Crosswalk"/> 转向（id 12）。经手工推导，人行横道 12 的中心线弦为
/// 竖直线段 <c>x = 5.25, y ∈ [-6, 6]</c>，与机动车转向 <c>NL(8)</c>（北进口左转，弦 <c>(-1.75,6)→(6,-1.75)</c>）
/// 于 <c>(5.25, -1.0)</c> 真穿越 → 二者冲突；与 <c>NR(7)</c>（北进口右转）不相交 → 不冲突。</para>
/// </summary>
public sealed class PedestrianTests
{
    private readonly ITestOutputHelper _output;

    public PedestrianTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ------------------------------------------------------------------
    // 转向 id 约定（与 ConflictTests.FourWay 逐字一致）+ 人行横道转向
    //   S 进口(lane0)：0=SS / 1=SR / 2=SL    E 进口(lane1)：3=ES / 4=ER / 5=EL
    //   N 进口(lane2)：6=NS / 7=NR / 8=NL    W 进口(lane3)：9=WS / 10=WR / 11=WL
    //   人行横道(12)：src=lane8(S 侧人行道) → dst=lane9(N 侧人行道)
    // ------------------------------------------------------------------
    private const int SS = 0;
    private const int SR = 1;
    private const int SL = 2;
    private const int ES = 3;
    private const int ER = 4;
    private const int EL = 5;
    private const int NS = 6;
    private const int NR = 7;    // 北进口右转：与人行横道 12 **不冲突**
    private const int NL = 8;    // 北进口左转：与人行横道 12 **冲突**（几何真穿越）
    private const int WS = 9;
    private const int WR = 10;
    private const int WL = 11;
    private const int CW = 12;   // 人行横道转向

    private const double Dt = 0.5d;
    private const double Dist = 14.0d;   // 过街距离 → 最短绿灯 = 14 / 1.4 = 10.0 s

    // ==================================================================
    // A 组：验收 a) 行人仅在自身相位内通过
    // ==================================================================

    /// <summary>a1：人行横道转向在本相位**未被放行**（Banned）时，行人一律滞留等待区，不进入人行横道。</summary>
    [Fact]
    public void A1_NotOwnPhase_PedestriansWait()
    {
        (CompiledNetwork net, _, IntersectionGate gate) = Fixture();
        var signal = new SignalStateMachine(net.GetNode(0), SignalTiming.Default);
        signal.SyncToTime(0d);
        var crossing = new PedestrianCrossing(gate, net, CW, Dist);
        var p = new Pedestrian(0, CW, Dist);
        crossing.Arrive(p);

        // 主配时第 0 相位（[0,20) 绿）放行南北直行，人行横道 12 为 Banned。
        int released = Run(crossing, signal, Dt, 20.0);

        Assert.Equal(0, released);
        Assert.True(p.Waiting, "人行横道未被本相位放行时，行人必须等待（不得闯红灯）。");
        Assert.Equal(0.0, p.Position, 9);
        Assert.Equal(-1.0, p.StartTime);   // 从未开始过街
    }

    /// <summary>a2：进入人行横道**自身相位**（第 1 相位，prot:[CW]）后才集体通过；开始时刻 = 该相位绿灯起点。</summary>
    [Fact]
    public void A2_OwnPhase_PedestriansCrossCollectively()
    {
        (CompiledNetwork net, _, IntersectionGate gate) = Fixture();
        var signal = new SignalStateMachine(net.GetNode(0), SignalTiming.Default);
        signal.SyncToTime(0d);
        var crossing = new PedestrianCrossing(gate, net, CW, Dist);

        // 三人先到（等齐过街：攒批后集体通过）。
        var p0 = new Pedestrian(0, CW, Dist);
        var p1 = new Pedestrian(1, CW, Dist);
        var p2 = new Pedestrian(2, CW, Dist);
        crossing.Arrive(p0);
        crossing.Arrive(p1);
        crossing.Arrive(p2);

        // 第 0 相位结束、第 1 相位（行人相位）于 t=25 开始。
        double releasedAt = RunUntilReleased(crossing, signal, Dt, 40.0);

        Assert.Equal(25.0, releasedAt, 6);
        Assert.True(p0.IsCrossing && p1.IsCrossing && p2.IsCrossing, "等齐过街：等待者应同时被放行。");
        Assert.Equal(25.0, p0.StartTime, 6);
        Assert.Equal(25.0, p1.StartTime, 6);
        Assert.Equal(25.0, p2.StartTime, 6);
        Assert.Equal(0, crossing.WaitingCount);
        Assert.Equal(3, crossing.CrossingCount);
    }

    /// <summary>a3：黄灯 / 全红期间不放行（即使相位对象本身仍把人行横道标为 Protected）。</summary>
    [Fact]
    public void A3_YellowAndAllRed_DoNotRelease()
    {
        // 单相位：绿灯 30 s → 黄 3 s（[30,33)）→ 全红 2 s（[33,35)）→ 周期 35 s。
        SignalPlan plan = Plan(Stage(30d, prot: [CW]));
        (CompiledNetwork net, _, IntersectionGate gate) = Fixture(plan);
        var signal = new SignalStateMachine(net.GetNode(0), SignalTiming.Default);
        signal.SyncToTime(0d);
        var crossing = new PedestrianCrossing(gate, net, CW, Dist);

        // t=0.5 放行第一人；随后在绿灯尾（t=29.5，剩余 0.5 < 10）到第二人。
        var early = new Pedestrian(0, CW, Dist);
        crossing.Arrive(early);
        Run(crossing, signal, Dt, 0.5);
        Assert.True(early.IsCrossing, "绿灯起点（剩余绿灯充足）应放行。");

        var late = new Pedestrian(1, CW, Dist);
        Run(crossing, signal, Dt, 29.0);     // t = 29.5
        crossing.Arrive(late);

        // 绿灯尾 + 黄 + 全红（t=29.5 → 34.5）：一步都不放行。
        int released = Run(crossing, signal, Dt, 5.0);
        Assert.Equal(0, released);
        Assert.True(late.Waiting, "黄灯 / 全红期间不得放行行人。");

        // 下一周期绿灯（t=35.0）才放行。
        double releasedAt = RunUntilReleased(crossing, signal, Dt, 10.0);
        Assert.Equal(35.0, releasedAt, 6);
    }

    // ==================================================================
    // B 组：验收 b) 绿灯末尾新到行人等下一周期
    // ==================================================================

    /// <summary>b1：绿灯尾部到达（剩余绿灯 &lt; 最短绿灯）→ 本周期不放行，等下一周期（下一轮自身相位）。</summary>
    [Fact]
    public void B1_ArrivalAtGreenTail_WaitsForNextCycle()
    {
        (CompiledNetwork net, _, IntersectionGate gate) = Fixture();
        var signal = new SignalStateMachine(net.GetNode(0), SignalTiming.Default);
        signal.SyncToTime(0d);
        var crossing = new PedestrianCrossing(gate, net, CW, Dist);

        Run(crossing, signal, Dt, 40.0);                     // t = 40：仍在第 1 相位绿内（[25,45)），剩余 5 s
        var tail = new Pedestrian(0, CW, Dist);
        crossing.Arrive(tail);

        // 绿灯尾（剩余 5 < 最短绿灯 10）→ 本周期不放行。
        int released = Run(crossing, signal, Dt, 5.0);       // t = 45（绿灯结束）
        Assert.Equal(0, released);
        Assert.True(tail.Waiting, "绿灯尾到达的行人不得在本周期放行。");

        // 走完 第1相位黄/全红 + 第2/第3/第0相位，直到下一轮行人相位（t = 25 + 95 = 120）。
        Run(crossing, signal, Dt, 75.0);                     // t = 120
        Assert.False(tail.Waiting, "下一周期自身相位应放行。");
        Assert.Equal(120.0, tail.StartTime, 6);
    }

    /// <summary>b2：绿灯余量充足时到达的行人立即放行（"绿灯尾不放行"只针对余量不足者）。</summary>
    [Fact]
    public void B2_ArrivalWithEnoughGreenCrossesPromptly()
    {
        (CompiledNetwork net, _, IntersectionGate gate) = Fixture();
        var signal = new SignalStateMachine(net.GetNode(0), SignalTiming.Default);
        signal.SyncToTime(0d);
        var crossing = new PedestrianCrossing(gate, net, CW, Dist);

        Run(crossing, signal, Dt, 30.0);                     // t = 30：第 1 相位绿内，剩余 15 s ≥ 10
        var mid = new Pedestrian(0, CW, Dist);
        crossing.Arrive(mid);

        double releasedAt = RunUntilReleased(crossing, signal, Dt, 1.0);
        Assert.Equal(30.5, releasedAt, 6);
        Assert.Equal(30.5, mid.StartTime, 6);
    }

    // ==================================================================
    // C 组：验收 c) 过街最短绿灯 = 距离 / 1.4 s
    // ==================================================================

    /// <summary>c1：最短绿灯公式 = 距离 / <see cref="SimConstants.PedestrianSpeed"/>（1.4 m/s）。</summary>
    [Fact]
    public void C1_MinGreenTime_IsDistanceOverPace()
    {
        Assert.Equal(1.4d, SimConstants.PedestrianSpeed, 9);

        Assert.Equal(10.0d, Pedestrian.MinGreenTimeFor(14.0d), 9);   // 14 / 1.4
        Assert.Equal(15.0d, Pedestrian.MinGreenTimeFor(21.0d), 9);   // 21 / 1.4
        Assert.Equal(10.0d, Pedestrian.MinGreenTimeFor(Dist), 9);

        var p = new Pedestrian(0, CW, 21.0d);
        Assert.Equal(15.0d, p.MinGreenTime, 9);

        (CompiledNetwork net, _, IntersectionGate gate) = Fixture();
        var crossing = new PedestrianCrossing(gate, net, CW, Dist);
        Assert.Equal(10.0d, crossing.MinGreenTime, 9);
    }

    /// <summary>c2：放行阈值精确落在"最短绿灯"上——剩余绿灯 == 最短绿灯时放行，差一点（= 最短绿灯 − 0.1）则不放行。</summary>
    [Fact]
    public void C2_ReleaseThreshold_IsExactlyMinGreenTime()
    {
        // 相位绿灯时长 = 最短绿灯 10.0：Step 前不 Tick（剩余绿灯 = 整个绿灯 10.0）。
        SignalPlan planOk = Plan(Stage(10.0d, prot: [CW]));
        (CompiledNetwork netOk, _, IntersectionGate gateOk) = Fixture(planOk);
        var signalOk = new SignalStateMachine(netOk.GetNode(0), SignalTiming.Default);
        signalOk.SyncToTime(0d);
        var crossingOk = new PedestrianCrossing(gateOk, netOk, CW, Dist);
        crossingOk.Arrive(new Pedestrian(0, CW, Dist));
        Assert.Equal(1, crossingOk.Step(Dt, signalOk));   // 剩余 10.0 ≥ 10.0 → 放行

        // 相位绿灯时长 = 最短绿灯 − 0.1：剩余 9.9 < 10.0 → 不放行。
        SignalPlan planNo = Plan(Stage(9.9d, prot: [CW]));
        (CompiledNetwork netNo, _, IntersectionGate gateNo) = Fixture(planNo);
        var signalNo = new SignalStateMachine(netNo.GetNode(0), SignalTiming.Default);
        signalNo.SyncToTime(0d);
        var crossingNo = new PedestrianCrossing(gateNo, netNo, CW, Dist);
        var pHeld = new Pedestrian(0, CW, Dist);
        crossingNo.Arrive(pHeld);
        Assert.Equal(0, crossingNo.Step(Dt, signalNo));
        Assert.True(pHeld.Waiting);
    }

    /// <summary>c3：物理过街时长 = 最短绿灯（以 1.4 m/s 恰好走完过街距离）。</summary>
    [Fact]
    public void C3_PhysicalCrossingDuration_EqualsMinGreenTime()
    {
        SignalPlan plan = Plan(Stage(30d, prot: [CW]));   // 绿灯 30 s，足够
        (CompiledNetwork net, _, IntersectionGate gate) = Fixture(plan);
        var signal = new SignalStateMachine(net.GetNode(0), SignalTiming.Default);
        signal.SyncToTime(0d);
        var crossing = new PedestrianCrossing(gate, net, CW, Dist);

        var p = new Pedestrian(0, CW, Dist);
        crossing.Arrive(p);
        Run(crossing, signal, Dt, 30.0);                  // 一直跑到稳（30 s 内必然到达对岸）

        Assert.Equal(PedestrianState.Crossed, p.State);
        Assert.Equal(Dist, p.Position, 6);
        Assert.Equal(p.MinGreenTime, p.ExitTime - p.StartTime, 6);   // 10.0 s
        Assert.Equal(1, crossing.CrossedCount);
        Assert.Equal(0, crossing.CrossingCount);
    }

    // ==================================================================
    // D 组：验收 d) 与转向车流的冲突被门控拦住（复用 T1.3）
    // ==================================================================

    /// <summary>d0：先钉死几何——人行横道 12 与机动车转向 NL(8) 冲突、与 NR(7) 不冲突（T1.3 冲突矩阵给出）。</summary>
    [Fact]
    public void D0_CrosswalkConflicts_WithTurningFlow()
    {
        (CompiledNetwork net, ConflictMatrix conflicts, _) = Fixture();

        Assert.Equal(TurnType.Crosswalk, net.GetTurn(CW).Type);
        Assert.True(conflicts.AreConflicting(CW, NL), "人行横道 12 与北进口左转 8 几何相交 → 应冲突。");
        Assert.True(conflicts.AreConflicting(NL, CW), "冲突关系对称。");
        Assert.False(conflicts.AreConflicting(CW, NR), "人行横道 12 与北进口右转 7 不相交 → 不冲突。");
        Assert.Contains(NL, conflicts.ConflictsOf(CW));   // 冲突集合包含左转
        Assert.DoesNotContain(NR, conflicts.ConflictsOf(CW));
    }

    /// <summary>d1（车辆让行人）：人行横道被 Protected 保护、冲突右/左转仅为 Yield → 冲突转向被门控拦住，不冲突者放行。</summary>
    [Fact]
    public void D1_ConflictingVehicleTurn_IsBlockedWhilePedestriansOccupied()
    {
        (CompiledNetwork net, _, IntersectionGate gate) = Fixture();

        // 相位：行人（人行横道 12）Protected；机动车 NL（冲突）与 NR（不冲突）均 Yield。
        Stage stage = Stage(30d, prot: [CW], perm: [NL, NR]);
        int[] occupied = [CW];

        Assert.False(
            gate.CanEnter(0, NL, stage, occupied, targetLaneHasSpace: true),
            "与正在过街的行人冲突的转向（Yield）必须被门控拦住（冲突纳入 T1.3）。");

        Assert.True(
            gate.CanEnter(0, NR, stage, occupied, targetLaneHasSpace: true),
            "与人行横道不冲突的转向不应被误拦（对照）。");
    }

    /// <summary>d2（行人让受保护车流）：受保护左转 NL 占用路口时，冲突的人行横道（本相位 Yield）被门控拦住 → 行人不放行。</summary>
    [Fact]
    public void D2_PedestriansBlocked_ByProtectedTurningFlow()
    {
        // 相位：机动车 NL Protected；人行横道 12 Yield（与人行横道冲突的车流具更高优先级）。
        SignalPlan plan = Plan(Stage(30d, prot: [NL], perm: [CW]));
        (CompiledNetwork net, _, IntersectionGate gate) = Fixture(plan);
        var signal = new SignalStateMachine(net.GetNode(0), SignalTiming.Default);
        signal.SyncToTime(0d);
        var crossing = new PedestrianCrossing(gate, net, CW, Dist);

        var p = new Pedestrian(0, CW, Dist);
        crossing.Arrive(p);

        // 对照组：无冲突车流占用 → 放行。
        int ok = crossing.Step(Dt, signal, occupiedTurnIds: null);
        Assert.Equal(1, ok);
        crossing.Reset();

        // 实验组：受保护左转 NL 正占用路口 → 冲突仲裁拒绝，行人不放行。
        var blockedPed = new Pedestrian(1, CW, Dist);
        crossing.Arrive(blockedPed);
        int blocked = crossing.Step(Dt, signal, occupiedTurnIds: [NL]);
        Assert.Equal(0, blocked);
        Assert.True(blockedPed.Waiting, "冲突仲裁（Yield 让行 Protected）应把行人拦在路缘。");
    }

    /// <summary>d3：相位内人行横道与冲突车流同为 Protected → HasProtectedConflict 报冲突（供红色警示，相位作者过失）。</summary>
    [Fact]
    public void D3_ProtectedConflict_IsReported()
    {
        (CompiledNetwork net, _, IntersectionGate gate) = Fixture();

        Assert.True(gate.HasProtectedConflict(0, Stage(30d, prot: [CW, NL])), "人行横道与冲突左转同相位保护 → 报冲突。");
        Assert.False(gate.HasProtectedConflict(0, Stage(30d, prot: [CW, NR])), "人行横道与不冲突右转同相位保护 → 无冲突。");
        Assert.False(gate.HasProtectedConflict(0, Stage(30d, prot: [CW])), "单条保护 → 无冲突。");
    }

    // ==================================================================
    // E 组：冻结接口对应 / 行人独立类型 / 确定性 / 参数校验
    // ==================================================================

    /// <summary>e1：<see cref="PedestrianSnapshot"/> 与 §4.4 冻结记录签名逐字段一致（4 个 get-only 属性，名称 / 类型 / 顺序）。</summary>
    [Fact]
    public void E1_PedestrianSnapshot_MatchesFrozenSignature()
    {
        Type t = typeof(PedestrianSnapshot);
        Assert.True(t.IsValueType, "§4.4 的 PedestrianSnapshot 是 readonly record struct。");

        PropertyInfo[] props = t.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        Assert.Equal(4, props.Length);

        AssertProperty(props, "Id", typeof(int));
        AssertProperty(props, "CrosswalkTurnId", typeof(int));
        AssertProperty(props, "Position", typeof(double));
        AssertProperty(props, "Waiting", typeof(bool));

        // 位置参数顺序 = 冻结签名 (Id, CrosswalkTurnId, Position, Waiting)。
        Assert.Equal(CW, new PedestrianSnapshot(7, CW, 3.5d, true).CrosswalkTurnId);
        Assert.Equal(3.5d, new PedestrianSnapshot(7, CW, 3.5d, true).Position, 9);
        Assert.True(new PedestrianSnapshot(7, CW, 3.5d, true).Waiting);
    }

    /// <summary>e2：行人用**独立类型**——<see cref="Pedestrian"/> 是 sealed 且与 <see cref="Vehicle"/> 无继承关系（不占用机动车逻辑通道）。</summary>
    [Fact]
    public void E2_Pedestrian_IsIndependentType()
    {
        Assert.True(typeof(Pedestrian).IsSealed);
        Assert.False(typeof(Vehicle).IsAssignableFrom(typeof(Pedestrian)));
        Assert.False(typeof(Pedestrian).IsAssignableFrom(typeof(Vehicle)));
        Assert.Equal(typeof(object), typeof(Pedestrian).BaseType);

        // 行人状态机与车辆状态机是两套互不相干的枚举。
        Assert.NotEqual(typeof(VehicleState), typeof(PedestrianState));
    }

    /// <summary>e3：行人只能走在 <see cref="TurnType.Crosswalk"/> 转向——非人行横道转向构造协调器应抛异常。</summary>
    [Fact]
    public void E3_Crossing_RequiresCrosswalkTurn()
    {
        (CompiledNetwork net, _, IntersectionGate gate) = Fixture();

        Assert.Throws<ArgumentException>(() => new PedestrianCrossing(gate, net, SS, Dist));   // SS 是 Straight
        Assert.Throws<ArgumentOutOfRangeException>(() => new PedestrianCrossing(gate, net, 99, Dist));
        Assert.Throws<ArgumentNullException>(() => new PedestrianCrossing(null!, net, CW, Dist));
        Assert.Throws<ArgumentNullException>(() => new PedestrianCrossing(gate, null!, CW, Dist));
    }

    /// <summary>e4：确定性——同一场景两次运行，放行时刻与全部快照逐项相等（铁律 2）。</summary>
    [Fact]
    public void E4_Deterministic_AcrossRuns()
    {
        (double firstTime, IReadOnlyList<PedestrianSnapshot> firstSnaps) = DeterministicRun();
        (double secondTime, IReadOnlyList<PedestrianSnapshot> secondSnaps) = DeterministicRun();

        Assert.Equal(firstTime, secondTime, 9);
        Assert.Equal(firstSnaps, secondSnaps);
    }

    /// <summary>e5：参数校验——构造 / 步进 / 登记 对非法入参一律抛异常（§1.3）。</summary>
    [Fact]
    public void E5_ArgumentValidation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pedestrian(-1, CW, Dist));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pedestrian(0, -1, Dist));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pedestrian(0, CW, 0d));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pedestrian(0, CW, double.NaN));

        (CompiledNetwork net, _, IntersectionGate gate) = Fixture();
        var signal = new SignalStateMachine(net.GetNode(0), SignalTiming.Default);
        signal.SyncToTime(0d);
        var crossing = new PedestrianCrossing(gate, net, CW, Dist);

        Assert.Throws<ArgumentOutOfRangeException>(() => crossing.Step(0d, signal));
        Assert.Throws<ArgumentNullException>(() => crossing.Step(Dt, null!));
        Assert.Throws<ArgumentNullException>(() => crossing.Arrive(null!));
        Assert.Throws<ArgumentException>(() => crossing.Arrive(new Pedestrian(0, SS, Dist)));   // 属于另一条人行横道

        var p = new Pedestrian(1, CW, Dist);
        crossing.Arrive(p);
        Assert.Throws<ArgumentException>(() => crossing.Arrive(p));   // 重复登记
    }

    /// <summary>e6：占用转向集合——过街中把人行横道转向计入路口占用；无人过街时为空；全部通过后再次为空。</summary>
    [Fact]
    public void E6_OccupiedTurnIds_ReflectCrossingState()
    {
        (CompiledNetwork net, _, IntersectionGate gate) = Fixture();
        var signal = new SignalStateMachine(net.GetNode(0), SignalTiming.Default);
        signal.SyncToTime(0d);
        var crossing = new PedestrianCrossing(gate, net, CW, Dist);
        var p = new Pedestrian(0, CW, Dist);
        crossing.Arrive(p);

        Assert.False(crossing.IsOccupying);
        Assert.Empty(crossing.OccupiedTurnIds);

        Run(crossing, signal, Dt, 25.0);   // t = 25：放行
        Assert.True(crossing.IsOccupying);
        Assert.Equal(new[] { CW }, crossing.OccupiedTurnIds);
        Assert.Single(crossing.Snapshots());   // 未通过者才出现在快照里

        Run(crossing, signal, Dt, 10.0);   // 10 s 后到岸
        Assert.False(crossing.IsOccupying);
        Assert.Empty(crossing.OccupiedTurnIds);
        Assert.Empty(crossing.Snapshots());    // 已通过者不再出现在快照里
        Assert.Equal(1, crossing.CrossedCount);
    }

    // ==================================================================
    // 夹具
    // ==================================================================

    /// <summary>主配时（4 相位；人行横道在第 1 相位保护放行）。周期 = 95 s。</summary>
    private static SignalPlan PedPlan()
        => Plan(
            Stage(20d, prot: [SS, NS], perm: [SR, NR]),   // 第 0 相位：南北直行
            Stage(20d, prot: [CW]),                       // 第 1 相位：行人相位
            Stage(15d, prot: [SL, NL]),                   // 第 2 相位：左转
            Stage(20d, prot: [ES, WS], perm: [ER, WR]));  // 第 3 相位：东西直行

    private static (CompiledNetwork Net, ConflictMatrix Conflicts, IntersectionGate Gate) Fixture()
        => Fixture(PedPlan());

    private static (CompiledNetwork Net, ConflictMatrix Conflicts, IntersectionGate Gate) Fixture(SignalPlan plan)
    {
        CompiledNetwork net = PedNetwork(plan).Compile();
        ConflictMatrix conflicts = ConflictMatrix.From(net);
        return (net, conflicts, IntersectionGate.Build(net, conflicts));
    }

    /// <summary>
    /// 4 路口机动车路网（与 <c>ConflictTests.FourWay</c> 同几何）+ 两条人行道车道 + 一条人行横道转向。
    /// 全部机动车车道 <c>LaneIndex = 0</c>；人行道车道 <c>LaneIndex = 1</c> → 人行横道弦 <c>x = 5.25</c>。
    /// </summary>
    private static Network PedNetwork(SignalPlan plan)
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
            Lane(0, road: 1, from: 1, to: 0),                                       // 南进口
            Lane(1, road: 2, from: 2, to: 0),                                       // 东进口
            Lane(2, road: 3, from: 3, to: 0),                                       // 北进口
            Lane(3, road: 4, from: 4, to: 0),                                       // 西进口
            Lane(4, road: 1, from: 0, to: 1),                                       // 南出口
            Lane(5, road: 2, from: 0, to: 2),                                       // 东出口
            Lane(6, road: 3, from: 0, to: 3),                                       // 北出口
            Lane(7, road: 4, from: 0, to: 4),                                       // 西出口
            Lane(8, road: 1, from: 1, to: 0, laneIndex: 1, type: LaneType.Sidewalk), // 南侧人行道
            Lane(9, road: 3, from: 0, to: 3, laneIndex: 1, type: LaneType.Sidewalk), // 北侧人行道
        ];

        Turn[] turns =
        [
            Turn(SS, 0, 6, 0, TurnType.Straight),
            Turn(SR, 0, 5, 0, TurnType.Right),
            Turn(SL, 0, 7, 0, TurnType.Left),
            Turn(ES, 1, 7, 0, TurnType.Straight),
            Turn(ER, 1, 6, 0, TurnType.Right),
            Turn(EL, 1, 4, 0, TurnType.Left),
            Turn(NS, 2, 4, 0, TurnType.Straight),
            Turn(NR, 2, 7, 0, TurnType.Right),
            Turn(NL, 2, 5, 0, TurnType.Left),
            Turn(WS, 3, 5, 0, TurnType.Straight),
            Turn(WR, 3, 4, 0, TurnType.Right),
            Turn(WL, 3, 6, 0, TurnType.Left),
            Turn(CW, 8, 9, 0, TurnType.Crosswalk),   // 人行横道
        ];

        return new Network { Nodes = nodes, Lanes = lanes, Turns = turns };
    }

    /// <summary>推进 <paramref name="seconds"/> 秒（每步先 <c>Signal.Tick</c> 再 <c>Crossing.Step</c>），返回累计放行人数。</summary>
    private static int Run(PedestrianCrossing crossing, SignalStateMachine signal, double dt, double seconds)
    {
        int steps = (int)Math.Round(seconds / dt);
        int released = 0;
        for (int i = 0; i < steps; i++)
        {
            signal.Tick(dt);
            released += crossing.Step(dt, signal);
        }

        return released;
    }

    /// <summary>推进直到首次放行，返回放行时刻（仿真时刻）；<paramref name="maxSeconds"/> 内未放行返回 -1。</summary>
    private static double RunUntilReleased(PedestrianCrossing crossing, SignalStateMachine signal, double dt, double maxSeconds)
    {
        int steps = (int)Math.Round(maxSeconds / dt);
        for (int i = 0; i < steps; i++)
        {
            signal.Tick(dt);
            if (crossing.Step(dt, signal) > 0)
            {
                return crossing.Now;
            }
        }

        return -1d;
    }

    /// <summary>确定性复现：固定场景跑一遍，返回（首次放行时刻, 全程快照序列）。</summary>
    private static (double ReleasedAt, IReadOnlyList<PedestrianSnapshot> Snaps) DeterministicRun()
    {
        (CompiledNetwork net, _, IntersectionGate gate) = Fixture();
        var signal = new SignalStateMachine(net.GetNode(0), SignalTiming.Default);
        signal.SyncToTime(0d);
        var crossing = new PedestrianCrossing(gate, net, CW, Dist);

        crossing.Arrive(new Pedestrian(0, CW, Dist));
        crossing.Arrive(new Pedestrian(1, CW, Dist));
        double releasedAt = RunUntilReleased(crossing, signal, Dt, 40.0);
        Run(crossing, signal, Dt, 5.0);
        return (releasedAt, crossing.Snapshots());
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
        Assert.True(initOnly, $"PedestrianSnapshot.{name} 应为 init-only（只读快照，§4.4）。");
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
