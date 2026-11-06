using System.Reflection;
using SimCore.Geometry;
using SimCore.Model;
using SimCore.Sim;
using Xunit;
using Xunit.Abstractions;

namespace SimCore.Tests;

/// <summary>
/// T1.3「几何冲突矩阵 + 路口放行门控」验收测试。
/// <para>分组与验收标准逐条对应，便于"手和眼"对证：
/// <list type="bullet">
///   <item>A 组 → 验收 a) 冲突判定三规则（同起点不冲突 / 同终点冲突 / 几何相交冲突）逐条单测</item>
///   <item>B 组 → 验收 b) 同一相位内两个 Protected 且冲突的转向 → 报冲突</item>
///   <item>C 组 → 验收 c) 门控下跑满一局，任意两车不重合（碰撞数 = 0）+ 负向对照</item>
///   <item>D 组 → 验收 d) Yield 须让行 Protected</item>
///   <item>E 组 → 验收 e) 目标车道无空位时禁止驶入路口</item>
///   <item>F 组 → 与冻结接口的对应（签名 / 内部准入判定 / 确定性 / 越界与空参）</item>
/// </list>
/// </para>
/// <para>测试路网（见 <see cref="FourWay"/>）：中心节点 0 为信号路口，南(S)/东(E)/北(N)/西(W) 各一条进口
/// <c>Driving</c> 车道（id 0..3）与一条出口车道（id 4..7），含 12 条机动车转向（每进口 直/左/右）。
/// 节点坐标：S=(0,-180)、E=(180,0)、N=(0,180)、W=(-180,0)、中心=(0,0)。转向 id 约定见
/// <see cref="SS"/> 等常量，全部转向的中心线弦坐标经手工推导并对齐断言。</para>
/// </summary>
public sealed class ConflictTests
{
    private readonly ITestOutputHelper _output;

    public ConflictTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ------------------------------------------------------------------
    // 转向 id 约定（FourWay 路网；与下方建构逐字一致）
    //   S 进口(lane0)：0=SS 直行(→6) / 1=SR 右转(→5) / 2=SL 左转(→7)
    //   E 进口(lane1)：3=ES 直行(→7) / 4=ER 右转(→6) / 5=EL 左转(→4)
    //   N 进口(lane2)：6=NS 直行(→4) / 7=NR 右转(→7) / 8=NL 左转(→5)
    //   W 进口(lane3)：9=WS 直行(→5) / 10=WR 右转(→4) / 11=WL 左转(→6)
    // ------------------------------------------------------------------
    private const int SS = 0;    // 南→北 直行
    private const int SR = 1;    // 南→东 右转
    private const int SL = 2;    // 南→西 左转
    private const int ES = 3;    // 东→西 直行
    private const int ER = 4;    // 东→北 右转
    private const int EL = 5;    // 东→南 左转
    private const int NS = 6;    // 北→南 直行
    private const int NR = 7;    // 北→西 右转
    private const int NL = 8;    // 北→东 左转
    private const int WS = 9;    // 西→东 直行
    private const int WR = 10;   // 西→南 右转
    private const int WL = 11;   // 西→北 左转

    private const double Dt = 0.5d;

    // ==================================================================
    // A 组：验收 a) 冲突判定三规则逐条单测
    // ==================================================================

    /// <summary>a1（规则 2：同起点不冲突）：同源车道分流的两条转向即使中心线相接，也不冲突。</summary>
    [Fact]
    public void A1_SameSourceLane_NeverConflicts()
    {
        ConflictMatrix m = ConflictMatrix.From(FourWay(GoodPlan()));

        // SS 与 SL 同源 lane0，且二者中心线共用入口点 (1.75, -6)（几何相接）；
        // 但"同起点"优先级更高 → 必为不冲突（规则 2）。
        Assert.Equal(0, m.AreConflicting(SS, SL) ? 1 : 0);
        Assert.False(m.AreConflicting(SS, SL), "同起点（同一源车道）应不冲突。");

        // SS 与 SR 亦同源（lane0）。
        Assert.False(m.AreConflicting(SS, SR), "同起点（同一源车道）应不冲突。");

        // 对称性。
        Assert.False(m.AreConflicting(SL, SS));
    }

    /// <summary>a2（规则 3：同终点冲突）：不同源车道、汇入同一目标车道的两条转向 → 冲突。</summary>
    [Fact]
    public void A2_SameDestinationLane_AlwaysConflicts()
    {
        ConflictMatrix m = ConflictMatrix.From(FourWay(GoodPlan()));

        // SS(南→北, 目标 lane6) 与 ER(东→北, 目标 lane6)：源不同、目标相同 → 冲突（合流点）。
        Assert.True(m.AreConflicting(SS, ER), "汇入同一目标车道应冲突。");
        Assert.True(m.AreConflicting(ER, SS));

        // SL(南→西, 目标 lane7) 与 NR(北→西, 目标 lane7)：同一目标 → 冲突。
        Assert.True(m.AreConflicting(SL, NR), "汇入同一目标车道应冲突。");
    }

    /// <summary>a3（规则 4：几何相交冲突）：源不同、目标不同，但中心线折线真穿越 → 冲突。</summary>
    [Fact]
    public void A3_CrossingCenterlines_Conflict()
    {
        ConflictMatrix m = ConflictMatrix.From(FourWay(GoodPlan()));

        // SS 弦 x=1.75（y∈[-6,6]）；NL 弦 从 (-1.75,6) 到 (6,-1.75)，二者于 (1.75,2.5) 内部真穿越。
        // 源不同（lane0 vs lane2）、目标不同（lane6 vs lane5）→ 冲突只能由"几何相交"给出（规则 4）。
        Assert.True(m.AreConflicting(SS, NL), "中心线几何相交应冲突。");
        Assert.True(m.AreConflicting(NL, SS));

        // 再一例：SL 弦 从 (1.75,-6) 到 (-6,1.75) 内部穿越 NS 弦（x=-1.75, y∈[-6,6]）于 (-1.75,-2.5)。
        Assert.True(m.AreConflicting(SL, NS), "南左转中心线穿越北向直行中心线，应冲突。");
    }

    /// <summary>a4（规则 5：其余不冲突）：源不同、目标不同、中心线不相交 → 不冲突。</summary>
    [Fact]
    public void A4_DisjointTurns_DoNotConflict()
    {
        ConflictMatrix m = ConflictMatrix.From(FourWay(GoodPlan()));

        // SS 弦 x=1.75；NR 弦 从 (-1.75,6) 到 (-6,1.75)（x∈[-6,-1.75]）→ 不相交，且源/目标皆不同。
        Assert.False(m.AreConflicting(SS, NR), "中心线不相交且源/目标均不同，应不冲突。");
        Assert.False(m.AreConflicting(NR, SS));
    }

    /// <summary>a5（规则 1）：一条转向与自身不冲突。</summary>
    [Fact]
    public void A5_Turn_Does_Not_Conflict_With_Itself()
    {
        ConflictMatrix m = ConflictMatrix.From(FourWay(GoodPlan()));

        for (int t = 0; t < 12; t++)
        {
            Assert.False(m.AreConflicting(t, t), $"转向 {t} 不应自冲突。");
        }

        Turn turn = new() { Id = SS, SrcLane = 0, DstLane = 6, Node = 0, Type = TurnType.Straight };
        Assert.False(m.AreConflicting(turn, turn));
    }

    /// <summary>a6（对称性）：全部转向两两判定均为对称关系。</summary>
    [Fact]
    public void A6_Conflict_Relation_Is_Symmetric()
    {
        ConflictMatrix m = ConflictMatrix.From(FourWay(GoodPlan()));

        for (int a = 0; a < 12; a++)
        {
            for (int b = 0; b < 12; b++)
            {
                Assert.Equal(m.AreConflicting(a, b), m.AreConflicting(b, a));
            }
        }
    }

    /// <summary>a7（<c>ConflictsOf</c> 与 <c>AreConflicting</c> 一致；升序、去重、不含自身）。</summary>
    [Fact]
    public void A7_ConflictsOf_Matches_AreConflicting_And_Is_SortedUnique()
    {
        ConflictMatrix m = ConflictMatrix.From(FourWay(GoodPlan()));

        for (int a = 0; a < 12; a++)
        {
            var expected = new List<int>();
            for (int b = 0; b < 12; b++)
            {
                if (m.AreConflicting(a, b))
                {
                    expected.Add(b);
                }
            }

            IReadOnlyList<int> actual = m.ConflictsOf(a);

            Assert.Equal(expected, actual);                                   // 与逐对判定一致
            Assert.DoesNotContain(a, actual);                                 // 不含自身
            Assert.Equal(actual.Distinct().Count(), actual.Count);            // 去重
            for (int i = 1; i < actual.Count; i++)
            {
                Assert.True(actual[i - 1] < actual[i], $"ConflictsOf({a}) 未升序。");   // 升序
            }
        }
    }

    /// <summary>a8（T1.3 验收 a 的典型三规则在真实十字路口的整体图景：南北直行互不冲突、东西直行互不冲突）。</summary>
    [Fact]
    public void A8_OpposingThrough_Movements_DoNotConflict()
    {
        ConflictMatrix m = ConflictMatrix.From(FourWay(GoodPlan()));

        Assert.False(m.AreConflicting(SS, NS), "对向直行（南北）应不冲突。");
        Assert.False(m.AreConflicting(ES, WS), "对向直行（东西）应不冲突。");

        // 交叉的直行两两冲突。
        Assert.True(m.AreConflicting(SS, ES), "南北直行 × 东西直行 应冲突。");
        Assert.True(m.AreConflicting(SS, WS));
        Assert.True(m.AreConflicting(NS, ES));
        Assert.True(m.AreConflicting(NS, WS));
    }

    // ==================================================================
    // B 组：验收 b) 相位内两个 Protected 且冲突的转向 → 报冲突
    // ==================================================================

    /// <summary>b1：同一相位内两条冲突的 Protected 转向 → <c>HasProtectedConflict</c> 报冲突。</summary>
    [Fact]
    public void B1_ProtectedConflict_Is_Reported()
    {
        IntersectionGate gate = Gate(FourWay(GoodPlan()));

        // NL(8) 与 NS(6) 冲突（NL 汇入 NS 的…… 实为交叉各半，见 A 组口径）：构造同时保护它们的相位。
        Stage bad = Stage(20d, prot: [SS, NS, SL]);   // SS×SL 同源不冲突；SL×NS 冲突（见 a3）
        Assert.True(gate.HasProtectedConflict(0, bad), "同一相位内两条冲突的 Protected 应被报出。");

        Stage bad2 = Stage(20d, prot: [SS, NL]);      // SS×NL 几何相交（见 a3）
        Assert.True(gate.HasProtectedConflict(0, bad2));
    }

    /// <summary>b2：相位内 Protected 互不冲突 / 仅一条 / 空 → 不报冲突。</summary>
    [Fact]
    public void B2_ConflictFree_Protected_Set_Is_Clean()
    {
        IntersectionGate gate = Gate(FourWay(GoodPlan()));

        Assert.False(gate.HasProtectedConflict(0, Stage(20d, prot: [SS, NS])), "南北对向直行互不冲突。");
        Assert.False(gate.HasProtectedConflict(0, Stage(20d, prot: [SS, SR, NS, NR])));
        Assert.False(gate.HasProtectedConflict(0, Stage(20d, prot: [SS])), "单条 Protected 不构成冲突对。");
        Assert.False(gate.HasProtectedConflict(0, Stage(20d)), "空 Protected 集合不报冲突。");
    }

    /// <summary>b3：<c>HasProtectedConflict</c> 只依据相位（Protected 集合）而非 Permitted 集合。</summary>
    [Fact]
    public void B3_HasProtectedConflict_Ignores_Permitted_Set()
    {
        IntersectionGate gate = Gate(FourWay(GoodPlan()));

        // SL 与 NS 冲突，但 SL 放在 permitted 集合 → 不属"保护流向冲突"，不报。
        Stage s = Stage(20d, prot: [NS], perm: [SL]);
        Assert.False(gate.HasProtectedConflict(0, s));
    }

    // ==================================================================
    // C 组：验收 c) 门控下跑满一局，任意两车不重合（碰撞数 = 0）
    // ==================================================================
    //
    // 碰撞口径：两车"同时占用几何冲突的转向" = 轨迹相交且时刻重叠 = 碰撞
    // （对齐 技术方案02 §2.2(a) 的三类冲突点：分流点不冲突，合流点 / 交叉点冲突）。
    // 同一转向上的多车视为同一车流的跟驰（由 T1.1 队列的车头间距保证不重叠），不计碰撞。

    /// <summary>c1：正确配时的四相位方案下，门控跑满三种整局（直行+右转 / 左转 / 混合）
    /// → 碰撞数均为 0，且确实有车通行（避免"空跑即通过"的假阳性）。
    /// 同时校核任意同车道两车不重叠（车头净距 ≥ 最小车头间距）。</summary>
    [Fact]
    public void C1_Full_Session_With_Gate_Has_Zero_Collisions()
    {
        Network net = FourWay(GoodPlan());

        // (1) 直行 + 右转：均在同相位放行，车队可连续通过（高流量）。
        SessionStats through = RunSession(
            net,
            approachLanes: [0, 1, 2, 3],
            rotations: [[SS, SR], [ES, ER], [NS, NR], [WS, WR]],
            spawnInterval: 4d,
            duration: 300d,
            traverseTime: 2.5d,
            drainExitLanes: true);

        // (2) 左转：由独立相位保护。
        SessionStats left = RunSession(
            net,
            approachLanes: [0, 1, 2, 3],
            rotations: [[SL], [EL], [NL], [WL]],
            spawnInterval: 8d,
            duration: 300d,
            traverseTime: 2.5d,
            drainExitLanes: true);

        // (3) 混合：直/左/右混排（含队首阻塞）——更难的门控场景。
        SessionStats mixed = RunSession(
            net,
            approachLanes: [0, 1, 2, 3],
            rotations: [[SS, SL, SR], [ES, EL, ER], [NS, NL, NR], [WS, WL, WR]],
            spawnInterval: 6d,
            duration: 300d,
            traverseTime: 2.5d,
            drainExitLanes: true);

        foreach (SessionStats s in new[] { through, left, mixed })
        {
            Assert.Equal(0, s.Collisions);
            Assert.False(s.InLaneOverlap, "同一车道内出现两车重叠（净距 < 最小车头间距）。");
            Assert.True(s.Entries > 0, "整局应有车辆通过路口（避免'空跑即通过'的假阳性）。");
        }
    }

    /// <summary>c2（负向对照）：相位作者把两条冲突的 Protected 放在同一相位（游戏设计v2 §8.1 允许试错），
    /// 门控<b>不阻止</b>二者同时放行 → 碰撞检测器必须如实报出碰撞（证明 c1 的 0 非"检测器失灵"）。</summary>
    [Fact]
    public void C2_Conflicting_Protected_Phase_Produces_Collisions_NegativeControl()
    {
        Network net = FourWay(Plan(Stage(30d, prot: [SS, NS, SL])));   // SL 与 NS 冲突且同相位保护

        // 自检：该相位确实被判定为冲突（b 组同源）。
        Assert.True(Gate(net).HasProtectedConflict(0, net.Nodes[0].SignalPlans[0].Stages[0]));

        SessionStats stats = RunSession(
            net,
            approachLanes: [0, 2],              // 南进口派 SS/SL，北进口派 NS
            rotations: [[SS, SL], [NS]],
            spawnInterval: 3d,
            duration: 240d,
            traverseTime: 2.5d,
            drainExitLanes: true);

        Assert.True(stats.Entries > 0, "负向对照局应有车通行。");
        Assert.True(stats.Collisions > 0, "冲突的 Protected 同相位放行时，碰撞检测器应报出碰撞。");
    }

    // ==================================================================
    // D 组：验收 d) Yield 须让行 Protected
    // ==================================================================

    /// <summary>d1：Yield 转向与其冲突的 Protected 转向同时被占用 → 不得驶入。</summary>
    [Fact]
    public void D1_Yield_GivesWay_To_Conflicting_Protected()
    {
        IntersectionGate gate = Gate(FourWay(GoodPlan()));
        Stage s = Stage(20d, prot: [NS], perm: [SL]);   // SL 与 NS 冲突（a3）

        Assert.True(gate.IsTurnOpen(0, SL, s), "SL 在 permitted 集合内 → 相位掩码放行。");
        Assert.Equal(TurnPriority.Yield, s.PriorityOf(SL));
        Assert.Equal(TurnPriority.Protected, s.PriorityOf(NS));

        // NS 已占用 → SL 须让行（T1.3 d）。
        Assert.False(gate.CanEnter(0, SL, s, occupiedTurnIds: [NS], targetLaneHasSpace: true),
            "Yield 转向冲突于已占用的 Protected 转向时应让行。");
    }

    /// <summary>d2：若冲突的 Protected 未占用，Yield 可驶入（让行是"条件"而非"禁行"）。</summary>
    [Fact]
    public void D2_Yield_May_Enter_When_Protected_Not_Occupying()
    {
        IntersectionGate gate = Gate(FourWay(GoodPlan()));
        Stage s = Stage(20d, prot: [NS], perm: [SL]);

        Assert.True(gate.CanEnter(0, SL, s, occupiedTurnIds: [], targetLaneHasSpace: true));
        Assert.True(gate.CanEnter(0, SL, s, occupiedTurnIds: [NR], targetLaneHasSpace: true),
            "与占用流向不冲突的 Yield 不应被阻断。");
    }

    /// <summary>d3：Protected 具绝对优先——不因冲突的 Yield 正在占用而被阻断
    /// （冲突的 Protected 属相位作者过失，由 HasProtectedConflict 告警 + 碰撞判负承担，§8.1）。</summary>
    [Fact]
    public void D3_Protected_Is_Not_Blocked_By_Yield()
    {
        IntersectionGate gate = Gate(FourWay(GoodPlan()));
        Stage s = Stage(20d, prot: [NS], perm: [SL]);

        Assert.True(gate.CanEnter(0, NS, s, occupiedTurnIds: [SL], targetLaneHasSpace: true),
            "Protected 转向具绝对优先，不因冲突的 Yield 占用而让行。");
    }

    /// <summary>d4：Yield 转向若与已占用的 Protected 不冲突，则放行（让行只针对冲突者）。</summary>
    [Fact]
    public void D4_NonConflicting_Yield_Is_Admitted()
    {
        IntersectionGate gate = Gate(FourWay(GoodPlan()));
        Stage s = Stage(20d, prot: [SS], perm: [SR, NR]);   // SR/NR 与 SS 均不冲突

        Assert.True(gate.CanEnter(0, SR, s, occupiedTurnIds: [SS], targetLaneHasSpace: true));
        Assert.True(gate.CanEnter(0, NR, s, occupiedTurnIds: [SS], targetLaneHasSpace: true));
    }

    /// <summary>d5：Banned 转向无论占用与否都不得驶入。</summary>
    [Fact]
    public void D5_Banned_Turn_Is_Never_Admitted()
    {
        IntersectionGate gate = Gate(FourWay(GoodPlan()));
        Stage s = Stage(20d, prot: [SS], perm: [SR]);

        Assert.False(gate.IsTurnOpen(0, SL, s), "SL 不在任何放行集合内 → Banned。");
        Assert.False(gate.CanEnter(0, SL, s, occupiedTurnIds: [], targetLaneHasSpace: true));
    }

    /// <summary>d6（同优先级不互相阻断的显式口径）：两条同属 Yield 且互相冲突的转向之间，
    /// 门控不按优先级阻断（间隙通过的进一步约束由路口净空 / 调用方承担）——此行为在报告中显式声明。</summary>
    [Fact]
    public void D6_SamePriority_Does_Not_Block_By_Priority_Rule()
    {
        IntersectionGate gate = Gate(FourWay(GoodPlan()));
        Stage s = Stage(20d, perm: [SL, EL]);   // SL(2) 与 EL(5) 冲突，二者同为 Yield

        Assert.Equal(TurnPriority.Yield, s.PriorityOf(SL));
        Assert.Equal(TurnPriority.Yield, s.PriorityOf(EL));
        Assert.True(gate.CanEnter(0, EL, s, occupiedTurnIds: [SL], targetLaneHasSpace: true),
            "同优先级（Yield×Yield）不按优先级规则阻断。");
    }

    // ==================================================================
    // E 组：验收 e) 目标车道无空位时禁止驶入路口
    // ==================================================================

    /// <summary>e1：目标车道无空位 → 即便相位放行且无冲突，也不得驶入。</summary>
    [Fact]
    public void E1_No_Target_Lane_Space_Blocks_Entry()
    {
        IntersectionGate gate = Gate(FourWay(GoodPlan()));
        Stage s = Stage(20d, prot: [SS, NS]);

        Assert.True(gate.CanEnter(0, SS, s, occupiedTurnIds: [], targetLaneHasSpace: true));
        Assert.False(gate.CanEnter(0, SS, s, occupiedTurnIds: [], targetLaneHasSpace: false),
            "目标车道无空位时应禁止驶入路口（T1.3 e）。");
    }

    /// <summary>e2（端到端）：出口车道容量不足时，门控在整局中真实地"卡住"入口
    /// （capacityBlocks &gt; 0），且不产生碰撞。</summary>
    [Fact]
    public void E2_Short_Exit_Lane_Triggers_Capacity_Blocking()
    {
        Network net = SingleApproach(exitLength: 15d);
        SessionStats stats = RunSession(
            net,
            approachLanes: [0],
            rotations: [[0]],                 // 唯一转向：0（南→北 直行）
            spawnInterval: 2d,
            duration: 120d,
            traverseTime: 2d,
            drainExitLanes: false);           // 下游不移出 → 出口车道填满

        Assert.Equal(0, stats.Collisions);
        Assert.True(stats.CapacityBlocks > 0,
            "出口车道填满后，门控应因目标车道无空位而拒绝后续车辆驶入（T1.3 e）。");
    }

    // ==================================================================
    // F 组：与冻结接口的对应（签名 / 内部准入判定 / 确定性 / 越界与空参）
    // ==================================================================

    /// <summary>f1：<c>ConflictMatrix</c> 的冻结 public 符号齐备（§3.1）。</summary>
    [Fact]
    public void F1_ConflictMatrix_Public_Surface_Is_Frozen()
    {
        Type t = typeof(ConflictMatrix);

        Assert.NotNull(t.GetMethod("From", BindingFlags.Public | BindingFlags.Static, null, [typeof(Network)], null));
        Assert.NotNull(t.GetMethod("From", BindingFlags.Public | BindingFlags.Static, null, [typeof(CompiledNetwork)], null));
        Assert.NotNull(t.GetMethod("AreConflicting", BindingFlags.Public | BindingFlags.Instance, null, [typeof(Turn), typeof(Turn)], null));
        Assert.NotNull(t.GetMethod("AreConflicting", BindingFlags.Public | BindingFlags.Instance, null, [typeof(int), typeof(int)], null));
        Assert.NotNull(t.GetMethod("ConflictsOf", BindingFlags.Public | BindingFlags.Instance, null, [typeof(int)], null));
    }

    /// <summary>f2：<c>IntersectionGate</c> 冻结 public 面恰为 <c>Build / IsTurnOpen / HasProtectedConflict</c>；
    /// 本卡新增的准入判定 <c>CanEnter</c> 为 <b>internal</b>（不进入冻结 public 面，§4.6）。</summary>
    [Fact]
    public void F2_IntersectionGate_Public_Surface_Is_Frozen_And_CanEnter_Is_Internal()
    {
        Type t = typeof(IntersectionGate);

        Assert.NotNull(t.GetMethod("Build", BindingFlags.Public | BindingFlags.Static, null, [typeof(CompiledNetwork), typeof(ConflictMatrix)], null));
        Assert.NotNull(t.GetMethod("IsTurnOpen", BindingFlags.Public | BindingFlags.Instance, null, [typeof(int), typeof(int), typeof(Stage)], null));
        Assert.NotNull(t.GetMethod("HasProtectedConflict", BindingFlags.Public | BindingFlags.Instance, null, [typeof(int), typeof(Stage)], null));

        MethodInfo[] publicMethods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.DoesNotContain(publicMethods, m => m.Name == "CanEnter");

        bool hasInternal = t
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Any(m => m.Name == "CanEnter");
        Assert.True(hasInternal, "准入判定 CanEnter 应为 internal（供 T1.6 与单测）。");
    }

    /// <summary>f3（T1.3 d/e 的 <c>IntersectionGate</c> 内部准入判定可被测试程序集访问）：
    /// 直接经内部 API 完成"相位掩码 ∧ 冲突仲裁 ∧ 容量"三合一判定。</summary>
    [Fact]
    public void F3_CanEnter_Combines_Mask_Conflict_And_Capacity()
    {
        IntersectionGate gate = Gate(FourWay(GoodPlan()));
        Stage s = Stage(20d, prot: [NS], perm: [SL]);

        Assert.True(gate.CanEnter(0, NS, s, [], true));                       // 掩码 ✓ 冲突 ✓ 容量 ✓
        Assert.False(gate.CanEnter(0, NS, s, [], false));                     // 容量 ✗
        Assert.False(gate.CanEnter(0, SL, s, [NS], true));                    // 让行 ✗
        Assert.False(gate.CanEnter(0, WS, s, [], true));                      // 相位掩码 ✗（Banned）
        Assert.True(gate.CanEnter(0, SL, s, [], true));                       // 全通过
    }

    /// <summary>f4：<c>Build</c> 要求冲突矩阵与路网同源；null 与不同源一律抛异常（§1.3 / §4.6）。</summary>
    [Fact]
    public void F4_Build_Rejects_Null_And_Mismatched_Source()
    {
        CompiledNetwork a = FourWay(GoodPlan()).Compile();
        CompiledNetwork b = FourWay(GoodPlan()).Compile();

        Assert.Throws<ArgumentNullException>(() => IntersectionGate.Build(null!, ConflictMatrix.From(a)));
        Assert.Throws<ArgumentNullException>(() => IntersectionGate.Build(a, null!));
        Assert.Throws<ArgumentException>(() => IntersectionGate.Build(a, ConflictMatrix.From(b)));

        // 同源（同一实例）→ 正常构建。
        Assert.NotNull(IntersectionGate.Build(a, ConflictMatrix.From(a)));
    }

    /// <summary>f5：越界与空参校验（§1.3）。</summary>
    [Fact]
    public void F5_Out_Of_Range_And_Null_Arguments_Throw()
    {
        ConflictMatrix m = ConflictMatrix.From(FourWay(GoodPlan()));
        IntersectionGate gate = Gate(FourWay(GoodPlan()));

        Assert.Throws<ArgumentNullException>(() => ConflictMatrix.From((Network)null!));
        Assert.Throws<ArgumentNullException>(() => ConflictMatrix.From((CompiledNetwork)null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.AreConflicting(0, 99));
        Assert.Throws<ArgumentOutOfRangeException>(() => m.ConflictsOf(-1));
        Assert.Throws<ArgumentNullException>(() => m.AreConflicting(null!, Turn(0, 0, 6, 0, TurnType.Straight)));

        Assert.Throws<ArgumentNullException>(() => gate.IsTurnOpen(0, 0, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => gate.IsTurnOpen(99, 0, Stage(1d, prot: [0])));
        Assert.Throws<ArgumentOutOfRangeException>(() => gate.IsTurnOpen(0, 99, Stage(1d, prot: [0])));
        Assert.Throws<ArgumentNullException>(() => gate.HasProtectedConflict(0, null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => gate.HasProtectedConflict(99, Stage(1d, prot: [0])));
    }

    /// <summary>f6（铁律 2 确定性）：同一路网两次构建 <c>ConflictMatrix</c>，逐项结果一致。</summary>
    [Fact]
    public void F6_From_Is_Deterministic()
    {
        Network net = FourWay(GoodPlan());
        ConflictMatrix a = ConflictMatrix.From(net);
        ConflictMatrix b = ConflictMatrix.From(net);

        for (int i = 0; i < 12; i++)
        {
            Assert.Equal(a.ConflictsOf(i), b.ConflictsOf(i));
            for (int j = 0; j < 12; j++)
            {
                Assert.Equal(a.AreConflicting(i, j), b.AreConflicting(i, j));
            }
        }
    }

    /// <summary>f7：<c>IsTurnOpen</c> 直接反映 <c>Stage.PriorityOf</c>（Protected/Yield→true；Banned→false）。</summary>
    [Fact]
    public void F7_IsTurnOpen_Follows_PriorityOf()
    {
        IntersectionGate gate = Gate(FourWay(GoodPlan()));
        Stage s = Stage(20d, prot: [SS, NS], perm: [SR, NR]);

        Assert.True(gate.IsTurnOpen(0, SS, s));
        Assert.True(gate.IsTurnOpen(0, SR, s));
        Assert.False(gate.IsTurnOpen(0, SL, s));
    }

    // ==================================================================
    // G 组：整局统计证据（供自检报告留痕；同时复校验收 c / e）
    // ==================================================================

    /// <summary>g1：打印整局统计（直行+右转 / 左转 / 混合 / 冲突相位负向对照 / 短出口车道），
    /// 作为验收 c、e 的量化证据。</summary>
    [Fact]
    public void G1_Session_Statistics_Evidence()
    {
        Network net = FourWay(GoodPlan());

        SessionStats through = RunSession(
            net, approachLanes: [0, 1, 2, 3],
            rotations: [[SS, SR], [ES, ER], [NS, NR], [WS, WR]],
            spawnInterval: 4d, duration: 300d, traverseTime: 2.5d, drainExitLanes: true);

        SessionStats left = RunSession(
            net, approachLanes: [0, 1, 2, 3],
            rotations: [[SL], [EL], [NL], [WL]],
            spawnInterval: 8d, duration: 300d, traverseTime: 2.5d, drainExitLanes: true);

        SessionStats mixed = RunSession(
            net, approachLanes: [0, 1, 2, 3],
            rotations: [[SS, SL, SR], [ES, EL, ER], [NS, NL, NR], [WS, WL, WR]],
            spawnInterval: 6d, duration: 300d, traverseTime: 2.5d, drainExitLanes: true);

        SessionStats bad = RunSession(
            FourWay(Plan(Stage(30d, prot: [SS, NS, SL]))),
            approachLanes: [0, 2],
            rotations: [[SS, SL], [NS]],
            spawnInterval: 3d, duration: 240d, traverseTime: 2.5d, drainExitLanes: true);

        SessionStats cap = RunSession(
            SingleApproach(exitLength: 15d), approachLanes: [0], rotations: [[0]],
            spawnInterval: 2d, duration: 120d, traverseTime: 2d, drainExitLanes: false);

        _output.WriteLine($"直行+右转整局：通过={through.Entries} 碰撞={through.Collisions} 车道内重叠={through.InLaneOverlap}");
        _output.WriteLine($"左转整局　　：通过={left.Entries} 碰撞={left.Collisions} 车道内重叠={left.InLaneOverlap}");
        _output.WriteLine($"混合整局　　：通过={mixed.Entries} 碰撞={mixed.Collisions} 容量阻塞={mixed.CapacityBlocks} 汇入停滞={mixed.TransferStalls} 车道内重叠={mixed.InLaneOverlap}");
        _output.WriteLine($"冲突相位整局：通过={bad.Entries} 碰撞={bad.Collisions}（负向对照，应 > 0）");
        _output.WriteLine($"短出口车道局：通过={cap.Entries} 碰撞={cap.Collisions} 容量阻塞={cap.CapacityBlocks}（应 > 0）");

        Assert.Equal(0, through.Collisions);
        Assert.Equal(0, left.Collisions);
        Assert.Equal(0, mixed.Collisions);
        Assert.True(mixed.Entries > 0);
        Assert.False(mixed.InLaneOverlap);
        Assert.True(bad.Collisions > 0, "负向对照：冲突相位应产生碰撞。");
        Assert.True(cap.CapacityBlocks > 0, "短出口车道应触发容量阻塞。");
    }

    // ==================================================================
    // 夹具：网络与配时
    // ==================================================================

    /// <summary>正确配时的四相位方案（每相位的放行集合两两不冲突；左转单独相位）。</summary>
    private static SignalPlan GoodPlan()
        => Plan(
            Stage(20d, prot: [SS, NS], perm: [SR, NR]),
            Stage(15d, prot: [SL, NL]),
            Stage(20d, prot: [ES, WS], perm: [ER, WR]),
            Stage(15d, prot: [EL, WL]));

    /// <summary>构建四路口测试路网（含配时）。</summary>
    private static Network FourWay(SignalPlan plan)
    {
        Node[] nodes =
        [
            new Node { Id = 0, Type = NodeType.Signalized, X = 0d, Y = 0d, SignalPlans = [plan] },
            new Node { Id = 1, Type = NodeType.Unsignalized, X = 0d, Y = -180d },
            new Node { Id = 2, Type = NodeType.Unsignalized, X = 180d, Y = 0d },
            new Node { Id = 3, Type = NodeType.Unsignalized, X = 0d, Y = 180d },
            new Node { Id = 4, Type = NodeType.Unsignalized, X = -180d, Y = 0d },
        ];

        Lane[] lanes =
        [
            Lane(0, road: 1, from: 1, to: 0),   // 南进口
            Lane(1, road: 2, from: 2, to: 0),   // 东进口
            Lane(2, road: 3, from: 3, to: 0),   // 北进口
            Lane(3, road: 4, from: 4, to: 0),   // 西进口
            Lane(4, road: 1, from: 0, to: 1),   // 南出口
            Lane(5, road: 2, from: 0, to: 2),   // 东出口
            Lane(6, road: 3, from: 0, to: 3),   // 北出口
            Lane(7, road: 4, from: 0, to: 4),   // 西出口
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
        ];

        return new Network { Nodes = nodes, Lanes = lanes, Turns = turns };
    }

    /// <summary>构建单进口路网：南进口 lane0（长 60）→ 唯一转向 0 → 北出口 lane1（长 exitLength）。</summary>
    private static Network SingleApproach(double exitLength)
    {
        Node[] nodes =
        [
            new Node { Id = 0, Type = NodeType.Signalized, X = 0d, Y = 0d, SignalPlans = [Plan(Stage(30d, prot: [0]))] },
            new Node { Id = 1, Type = NodeType.Unsignalized, X = 0d, Y = -60d },
            new Node { Id = 2, Type = NodeType.Unsignalized, X = 0d, Y = exitLength },
        ];

        Lane[] lanes =
        [
            Lane(0, road: 1, from: 1, to: 0, length: 60d),
            Lane(1, road: 1, from: 0, to: 2, length: exitLength),
        ];

        Turn[] turns = [Turn(0, 0, 1, 0, TurnType.Straight)];

        return new Network { Nodes = nodes, Lanes = lanes, Turns = turns };
    }

    private static IntersectionGate Gate(Network net)
    {
        CompiledNetwork compiled = net.Compile();
        return IntersectionGate.Build(compiled, ConflictMatrix.From(compiled));
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

    // ==================================================================
    // 微型"整局"仿真夹具（仅测门控：把路口准入交给门控，其余用 T1.1 队列）
    // ==================================================================

    /// <summary>一局的统计结果。</summary>
    private sealed class SessionStats
    {
        public int Collisions { get; init; }

        public int Entries { get; init; }

        public int CapacityBlocks { get; init; }

        public int TransferStalls { get; init; }

        public int SpawnDropped { get; init; }

        public bool InLaneOverlap { get; init; }
    }

    /// <summary>
    /// 跑一局固定步长仿真：需求按确定序列注入；每条进口车道的队首在停止线处向门控申请准入，
    /// 门控放行才驶入路口（占用其转向 <paramref name="traverseTime"/> 秒后汇入目标车道）。
    /// 每步校核：占用中转向两两不冲突（碰撞数）、同车道两车不重叠。
    /// </summary>
    private static SessionStats RunSession(
        Network network,
        int[] approachLanes,
        int[][] rotations,
        double spawnInterval,
        double duration,
        double traverseTime,
        bool drainExitLanes)
    {
        CompiledNetwork net = network.Compile();
        ConflictMatrix conflicts = ConflictMatrix.From(net);
        IntersectionGate gate = IntersectionGate.Build(net, conflicts);

        var signal = new SignalStateMachine(net.GetNode(0), SignalTiming.Default);
        signal.SyncToTime(0d);

        int laneCount = net.LaneCount;
        var queues = new Queue[laneCount];
        for (int i = 0; i < laneCount; i++)
        {
            queues[i] = new Queue(net.GetLane(i));
        }

        var isApproach = new bool[laneCount];
        foreach (int lane in approachLanes)
        {
            isApproach[lane] = true;
        }

        var occupied = new List<(int Turn, double Remaining, Vehicle V)>();
        var arrivalIndex = new int[approachLanes.Length];
        var nextSpawn = new double[approachLanes.Length];
        for (int k = 0; k < approachLanes.Length; k++)
        {
            nextSpawn[k] = k * (spawnInterval / approachLanes.Length);
        }

        int nextVehicleId = 0;
        int collisions = 0;
        int entries = 0;
        int capacityBlocks = 0;
        int transferStalls = 0;
        int spawnDropped = 0;
        bool inLaneOverlap = false;

        int steps = (int)Math.Round(duration / Dt);
        for (int step = 0; step < steps; step++)
        {
            double t = step * Dt;

            // 1) 需求到达（确定序列：每条进口按各自轮转表循环发车）。
            for (int k = 0; k < approachLanes.Length; k++)
            {
                if (t + 1e-9 < nextSpawn[k])
                {
                    continue;
                }

                int lane = approachLanes[k];
                int[] rotation = rotations[k];
                if (rotation.Length > 0)
                {
                    int turn = rotation[arrivalIndex[k] % rotation.Length];
                    arrivalIndex[k]++;
                    var v = new Vehicle(nextVehicleId++, VehicleKind.Car) { NextTurnId = turn };
                    if (!queues[lane].TryEnqueue(v))
                    {
                        spawnDropped++;
                    }
                }

                nextSpawn[k] += spawnInterval;
            }

            // 2) 推进路口内车辆：走完转向耗时后汇入目标车道（容量不足则继续占用，下步重试）。
            for (int i = occupied.Count - 1; i >= 0; i--)
            {
                (int turn, double remaining, Vehicle v) = occupied[i];
                remaining -= Dt;
                if (remaining > 1e-9)
                {
                    occupied[i] = (turn, remaining, v);
                    continue;
                }

                int dstLane = net.GetTurn(turn).DstLane;
                if (queues[dstLane].TryEnqueue(v))
                {
                    occupied.RemoveAt(i);
                }
                else
                {
                    transferStalls++;                  // 目标车道满（spillback）：保持占用，下步重试
                    occupied[i] = (turn, 0d, v);
                }
            }

            // 3) 信号推进。
            signal.Tick(Dt);
            bool green = signal.Phase is SignalPhaseState.Green or SignalPhaseState.GreenFlash;
            Stage stage = signal.CurrentStage;

            // 4) 准入决策（顺序化；provisional 累加本步已放行转向，避免同一步内冲突放行）。
            var provisional = new List<int>(occupied.Count);
            for (int i = 0; i < occupied.Count; i++)
            {
                provisional.Add(occupied[i].Turn);
            }

            var open = new bool[laneCount];
            if (green)
            {
                for (int k = 0; k < approachLanes.Length; k++)
                {
                    int lane = approachLanes[k];
                    Vehicle? head = queues[lane].Head;
                    if (head is null || head.NextTurnId < 0)
                    {
                        continue;
                    }

                    int turn = head.NextTurnId;
                    int dst = net.GetTurn(turn).DstLane;
                    bool space = CanAccept(queues[dst]);
                    bool atStopLine = head.Position >= net.GetLane(lane).Length - 1e-6;
                    if (!space && atStopLine)
                    {
                        capacityBlocks++;
                    }

                    if (gate.CanEnter(0, turn, stage, provisional, space))
                    {
                        open[lane] = true;
                        provisional.Add(turn);
                    }
                }
            }

            // 5) 推进各车道（进口按门控；出口按 drainExitLanes 排空）。
            for (int i = 0; i < laneCount; i++)
            {
                queues[i].Step(Dt, isApproach[i] ? open[i] : drainExitLanes);
            }

            // 6) 收集本步进入路口的车辆 + 排空出口车道（离开网络）。
            for (int k = 0; k < approachLanes.Length; k++)
            {
                int lane = approachLanes[k];
                foreach (Vehicle v in queues[lane].DrainDeparted())
                {
                    occupied.Add((v.NextTurnId, traverseTime, v));
                    entries++;
                }
            }

            for (int i = 0; i < laneCount; i++)
            {
                if (!isApproach[i])
                {
                    queues[i].DrainDeparted();
                }
            }

            // 7) 碰撞检测：占用中转向两两冲突 => 碰撞。
            for (int i = 0; i < occupied.Count; i++)
            {
                for (int j = i + 1; j < occupied.Count; j++)
                {
                    if (occupied[i].Turn != occupied[j].Turn &&
                        conflicts.AreConflicting(occupied[i].Turn, occupied[j].Turn))
                    {
                        collisions++;
                    }
                }
            }

            // 8) 同车道不重叠校核（车头净距 ≥ 最小车头间距）。
            for (int i = 0; i < laneCount; i++)
            {
                IReadOnlyList<Vehicle> vs = queues[i].Vehicles;
                for (int j = 0; j + 1 < vs.Count; j++)
                {
                    double gap = vs[j].Position - vs[j].Length - vs[j + 1].Position;
                    if (gap < SimConstants.MinHeadway - 1e-6)
                    {
                        inLaneOverlap = true;
                    }
                }
            }
        }

        return new SessionStats
        {
            Collisions = collisions,
            Entries = entries,
            CapacityBlocks = capacityBlocks,
            TransferStalls = transferStalls,
            SpawnDropped = spawnDropped,
            InLaneOverlap = inLaneOverlap,
        };
    }

    /// <summary>目标车道是否还有空位（与 <see cref="Queue.TryEnqueue(Vehicle)"/> 的入口判定一致）。</summary>
    private static bool CanAccept(Queue queue)
    {
        if (queue.Count == 0)
        {
            return true;
        }

        Vehicle tail = queue.Tail!;
        return tail.Position - tail.Length >= SimConstants.MinHeadway - 1e-9;
    }
}
