using System.Reflection;
using SimCore.Geometry;
using SimCore.Model;
using Xunit;

namespace SimCore.Tests;

/// <summary>
/// T4.2(b)「相位方案自动生成」验收测试（<c>docs/SimCore-API.md</c> §3.3）。
/// <para>分组与验收标准逐条对应：
/// <list type="bullet">
///   <item>A 组 → 验收 a) 十字路口 ≥2 套候选，顺序稳定（先 2 阶段、后 4 阶段）</item>
///   <item>B 组 → 验收 b) 每套内每个 Stage：protected 集合两两不冲突（逐对断言）</item>
///   <item>C 组 → 验收 c) 4 阶段方案覆盖该路口全部机动车进口，无进口被漏</item>
///   <item>D 组 → 验收 d) 每个 Stage.Duration &gt; 0</item>
///   <item>E 组 → 验收 e) network==null / nodeId 不存在 / 无进口车道 → 抛异常（+ 无 turns 行为留痕）</item>
///   <item>F 组 → 验收 f) 确定性：两次调用逐项相等（+ 输入顺序无关）</item>
///   <item>G 组 → 追加约束：冻结 public 面 / 固定配时常量 / <c>type = Fixed</c> / 逐 id 精确序 / 左转让行口径</item>
/// </list>
/// </para>
/// <para>测试路网（见 <see cref="FourWay"/>）：中心节点 0 为信号路口，南(S)/东(E)/北(N)/西(W)
/// 各一条进口 <c>Driving</c> 车道（id 0..3）与一条出口车道（id 4..7），另有南人行道进口 lane8、
/// 东人行道出口 lane9；turns 由 <see cref="TurnGenerator.FromGeometry"/> 生成后并入（E-C2 的输入前提）。
/// 节点坐标：S=(0,−180)、E=(180,0)、N=(0,180)、W=(−180,0)、中心=(0,0)（Y 轴向上）。</para>
/// <para><b>turn id 速查（中心节点 0，共 16 条机动车转向 + 1 条人行横道）</b>：
/// 南 S={直2, 右1, 左3, 掉0}、东 E={直7, 右6, 左4, 掉5}、
/// 北 N={直8, 右11, 左9, 掉10}、西 W={直13, 右12, 左14, 掉15}；
/// 人行横道 = lane8→lane9 = id16。</para>
/// </summary>
public sealed class PhaseAutoGeneratorTests
{
    // 车道 id 约定（FourWay 路网）
    private const int SouthIn = 0;      // 南进口（1→0，行驶方向 北）
    private const int EastIn = 1;       // 东进口（2→0，行驶方向 西）
    private const int NorthIn = 2;      // 北进口（3→0，行驶方向 南）
    private const int WestIn = 3;       // 西进口（4→0，行驶方向 东）
    private const int SouthOut = 4;
    private const int EastOut = 5;
    private const int NorthOut = 6;
    private const int WestOut = 7;
    private const int SouthWalkIn = 8;
    private const int EastWalkOut = 9;

    // 十字路口"期望的 2 阶段方案"（南北轴 / 东西轴）：protected = 直行+右转，permitted = 左转+掉头。
    private static readonly int[] TwoStageNsProtected = [1, 2, 8, 11];
    private static readonly int[] TwoStageNsPermitted = [0, 3, 9, 10];
    private static readonly int[] TwoStageEwProtected = [6, 7, 12, 13];
    private static readonly int[] TwoStageEwPermitted = [4, 5, 14, 15];

    // ==================================================================
    // A 组：验收 a) ≥2 套候选，顺序稳定（先 2 阶段、后 4 阶段）
    // ==================================================================

    /// <summary>a1：返回恰 2 套候选；[0] 为 2 阶段（2 个相位），[1] 为 4 阶段（4 个相位）。</summary>
    [Fact]
    public void A1_Cross_Returns_Two_Candidates_Two_Stage_First()
    {
        SignalPlan[] plans = PhaseAutoGenerator.Suggest(FourWayWithTurns(), 0);

        Assert.Equal(2, plans.Length);
        Assert.Equal(2, plans[0].Stages.Count);   // 先 2 阶段
        Assert.Equal(4, plans[1].Stages.Count);   // 后 4 阶段
    }

    /// <summary>a2：顺序稳定——[0] 的相位数少于 [1]（"先 2 阶段、后 4 阶段"），且两套都非空。</summary>
    [Fact]
    public void A2_Candidate_Order_Is_Two_Stage_Then_Four_Stage()
    {
        SignalPlan[] plans = PhaseAutoGenerator.Suggest(FourWayWithTurns(), 0);

        Assert.True(plans[0].Stages.Count < plans[1].Stages.Count);
        Assert.NotEmpty(plans[0].Stages);
        Assert.NotEmpty(plans[1].Stages);
        Assert.Equal(2, plans[0].Stages.Count);
        Assert.Equal(4, plans[1].Stages.Count);
    }

    // ==================================================================
    // B 组：验收 b) 每套内每个 Stage：protected 集合两两不冲突
    // ==================================================================

    /// <summary>b1：2 阶段方案内，逐个 Stage 的 protected 集合两两不冲突（用 ConflictMatrix.AreConflicting 逐对断言）。</summary>
    [Fact]
    public void B1_Two_Stage_Protected_Is_Pairwise_Non_Conflicting()
    {
        Network net = FourWayWithTurns();
        ConflictMatrix conflicts = ConflictMatrix.From(net);

        SignalPlan[] plans = PhaseAutoGenerator.Suggest(net, 0);
        AssertNoProtectedConflict(plans[0], conflicts);
    }

    /// <summary>b2：4 阶段方案内，逐个 Stage 的 protected 集合两两不冲突。</summary>
    [Fact]
    public void B2_Four_Stage_Protected_Is_Pairwise_Non_Conflicting()
    {
        Network net = FourWayWithTurns();
        ConflictMatrix conflicts = ConflictMatrix.From(net);

        SignalPlan[] plans = PhaseAutoGenerator.Suggest(net, 0);
        AssertNoProtectedConflict(plans[1], conflicts);
    }

    // ==================================================================
    // C 组：验收 c) 4 阶段方案覆盖该路口全部机动车进口，无进口被漏
    // ==================================================================

    /// <summary>c1：4 阶段方案（protected ∪ permitted）覆盖全部 4 条机动车进口车道（0/1/2/3），无进口被漏。</summary>
    [Fact]
    public void C1_Four_Stage_Covers_All_Motor_Approaches()
    {
        Network net = FourWayWithTurns();
        SignalPlan[] plans = PhaseAutoGenerator.Suggest(net, 0);

        HashSet<int> covered = CollectTurnIds(plans[1]);
        int[] motorApproaches = [SouthIn, EastIn, NorthIn, WestIn];

        foreach (int approach in motorApproaches)
        {
            // 该进口车道为源、且落在某个相位（protected ∪ permitted）里的转向。
            bool coveredThisApproach = net.Turns.Any(
                turn => turn.SrcLane == approach && covered.Contains(turn.Id));
            Assert.True(coveredThisApproach, $"进口车道 {approach} 未被 4 阶段方案覆盖。");
        }
    }

    /// <summary>c2（强化）：4 阶段方案的并集恰为全部 16 条机动车转向 id（0..15），一条不漏、无多余。</summary>
    [Fact]
    public void C2_Four_Stage_Covers_All_Sixteen_Motor_Turns()
    {
        Network net = FourWayWithTurns();
        SignalPlan[] plans = PhaseAutoGenerator.Suggest(net, 0);

        HashSet<int> covered = CollectTurnIds(plans[1]);
        HashSet<int> expectedMotorTurns = net.Turns
            .Where(turn => turn.Node == 0 && turn.Type != TurnType.Crosswalk)
            .Select(turn => turn.Id)
            .ToHashSet();

        Assert.Equal(16, expectedMotorTurns.Count);
        Assert.True(covered.SetEquals(expectedMotorTurns),
            "4 阶段方案并集应恰为全部机动车转向 id 集合。");
    }

    // ==================================================================
    // D 组：验收 d) 每个 Stage.Duration > 0
    // ==================================================================

    /// <summary>d1：两套方案的每个 Stage.Duration 均 &gt; 0（= 绿灯时长口径）。</summary>
    [Fact]
    public void D1_All_Stage_Durations_Are_Positive()
    {
        SignalPlan[] plans = PhaseAutoGenerator.Suggest(FourWayWithTurns(), 0);

        foreach (SignalPlan plan in plans)
        {
            Assert.NotEmpty(plan.Stages);
            foreach (Stage stage in plan.Stages)
            {
                Assert.True(stage.Duration > 0d, $"相位时长必须 > 0，实际 {stage.Duration}。");
            }
        }
    }

    // ==================================================================
    // E 组：验收 e) 前置违反 → 抛异常
    // ==================================================================

    /// <summary>e1：<c>network == null</c> → <see cref="ArgumentNullException"/>。</summary>
    [Fact]
    public void E1_Null_Network_Throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => PhaseAutoGenerator.Suggest(null!, 0));
    }

    /// <summary>e2：<c>nodeId</c> 不存在（越界）→ 抛异常。</summary>
    [Fact]
    public void E2_Unknown_NodeId_Throws()
    {
        Network net = FourWayWithTurns();
        Assert.Throws<ArgumentOutOfRangeException>(() => PhaseAutoGenerator.Suggest(net, 99));
        Assert.Throws<ArgumentOutOfRangeException>(() => PhaseAutoGenerator.Suggest(net, -1));
    }

    /// <summary>e3：该节点无进口车道 → 抛 <see cref="ArgumentException"/>。</summary>
    [Fact]
    public void E3_Node_Without_Incoming_Lanes_Throws()
    {
        // 车道 0→1：node0 只有出口、无进口车道。
        var net = new Network
        {
            Nodes = [N(0, 0, 0), N(1, 0, -180)],
            Lanes = [L(0, road: 1, from: 0, to: 1)],
        };

        Assert.Throws<ArgumentException>(() => PhaseAutoGenerator.Suggest(net, 0));
    }

    /// <summary>e4（口径留痕）：该节点有进口车道但<b>无 turns</b> → 抛 <see cref="ArgumentException"/>。
    /// 本实现选择"抛异常"而非"返回空数组"（与 §1.3 前置违反一律抛异常一致）。</summary>
    [Fact]
    public void E4_Node_Without_Turns_Throws()
    {
        // 车道 1→0：node0 有进口车道，但未并入任何 turns。
        var net = new Network
        {
            Nodes = [N(0, 0, 0), N(1, 0, -180)],
            Lanes = [L(0, road: 1, from: 1, to: 0)],
        };

        Assert.Throws<ArgumentException>(() => PhaseAutoGenerator.Suggest(net, 0));
    }

    // ==================================================================
    // F 组：验收 f) 确定性
    // ==================================================================

    /// <summary>f1：同一 network 两次调用，结果逐项（相位数 / 时长 / type / protected / permitted）相等。</summary>
    [Fact]
    public void F1_Two_Calls_Are_Item_By_Item_Equal()
    {
        Network net = FourWayWithTurns();

        SignalPlan[] first = PhaseAutoGenerator.Suggest(net, 0);
        SignalPlan[] second = PhaseAutoGenerator.Suggest(net, 0);

        AssertSamePlans(first, second);
    }

    /// <summary>f2（强化确定性）：反转输入列表顺序（Nodes/Lanes/Turns）后调用，结果逐项相等。</summary>
    [Fact]
    public void F2_Result_Is_Independent_Of_Input_List_Order()
    {
        Network net = FourWayWithTurns();
        SignalPlan[] original = PhaseAutoGenerator.Suggest(net, 0);

        var reversed = new Network
        {
            Nodes = Reverse(net.Nodes),
            Lanes = Reverse(net.Lanes),
            Turns = Reverse(net.Turns),
        };
        SignalPlan[] reordered = PhaseAutoGenerator.Suggest(reversed, 0);

        AssertSamePlans(original, reordered);
    }

    // ==================================================================
    // G 组：追加约束（冻结 public 面 / 常量 / type / 精确序 / 让行口径）
    // ==================================================================

    /// <summary>g1：冻结签名对齐——public 面恰为 <c>static SignalPlan[] Suggest(Network, int)</c>（§3.3）。</summary>
    [Fact]
    public void G1_Public_Surface_Is_Frozen()
    {
        Type type = typeof(PhaseAutoGenerator);

        MethodInfo? method = type.GetMethod(
            "Suggest", BindingFlags.Public | BindingFlags.Static, null, [typeof(Network), typeof(int)], null);
        Assert.NotNull(method);
        Assert.Equal(typeof(SignalPlan[]), method!.ReturnType);

        MethodInfo[] declared = type.GetMethods(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        MethodInfo only = Assert.Single(declared);
        Assert.Equal("Suggest", only.Name);
    }

    /// <summary>g2：固定配时建议值为固定常量（绿灯时长，必须 &gt; 0；不读环境、不随机）。</summary>
    [Fact]
    public void G2_Green_Time_Constants_Are_Fixed()
    {
        Assert.Equal(30.0d, PhaseAutoGenerator.TwoStageGreenSeconds);
        Assert.Equal(25.0d, PhaseAutoGenerator.FourStageThroughGreenSeconds);
        Assert.Equal(15.0d, PhaseAutoGenerator.FourStageTurningGreenSeconds);
        Assert.True(PhaseAutoGenerator.TwoStageGreenSeconds > 0d);
        Assert.True(PhaseAutoGenerator.FourStageThroughGreenSeconds > 0d);
        Assert.True(PhaseAutoGenerator.FourStageTurningGreenSeconds > 0d);
    }

    /// <summary>g3：<c>stages[].type</c> 一律写 <see cref="StageType.Fixed"/>（本任务追加约束）。</summary>
    [Fact]
    public void G3_Stage_Type_Is_Always_Fixed()
    {
        SignalPlan[] plans = PhaseAutoGenerator.Suggest(FourWayWithTurns(), 0);

        foreach (SignalPlan plan in plans)
        {
            foreach (Stage stage in plan.Stages)
            {
                Assert.Equal(StageType.Fixed, stage.Type);
            }
        }
    }

    /// <summary>g4：逐 id 精确断言（顺序 / 内容稳定性证据）——十字路口两套方案的 protected / permitted 全表。</summary>
    [Fact]
    public void G4_Exact_Protected_And_Permitted_Sets()
    {
        SignalPlan[] plans = PhaseAutoGenerator.Suggest(FourWayWithTurns(), 0);

        // 2 阶段：南北轴、东西轴
        AssertIds(TwoStageNsProtected, plans[0].Stages[0].ProtectedTurnIds);
        AssertIds(TwoStageNsPermitted, plans[0].Stages[0].PermittedTurnIds);
        AssertIds(TwoStageEwProtected, plans[0].Stages[1].ProtectedTurnIds);
        AssertIds(TwoStageEwPermitted, plans[0].Stages[1].PermittedTurnIds);

        // 4 阶段：NS 直行 → NS 左转 → EW 直行 → EW 左转（保护左转相序）
        AssertIds(TwoStageNsProtected, plans[1].Stages[0].ProtectedTurnIds);
        AssertIds(TwoStageNsPermitted, plans[1].Stages[1].ProtectedTurnIds);   // 左转+掉头在本相序里受保护
        AssertIds(TwoStageEwProtected, plans[1].Stages[2].ProtectedTurnIds);
        AssertIds(TwoStageEwPermitted, plans[1].Stages[3].ProtectedTurnIds);
    }

    /// <summary>g5：2 阶段方案中左转"让行不保护"（无保护左转口径）——南进口左转（turn3）不在任何 protected，
    /// 且落在某个 permitted 内。</summary>
    [Fact]
    public void G5_Two_Stage_Left_Turns_Are_Permitted_Not_Protected()
    {
        Network net = FourWayWithTurns();
        int southLeft = FindTurnId(net, SouthIn, WestOut);   // 南进口左转 → 西出口

        SignalPlan[] plans = PhaseAutoGenerator.Suggest(net, 0);
        SignalPlan twoStage = plans[0];

        Assert.DoesNotContain(twoStage.Stages, stage => stage.ProtectedTurnIds.Contains(southLeft));
        Assert.Contains(twoStage.Stages, stage => stage.PermittedTurnIds.Contains(southLeft));
    }

    /// <summary>g6：protected / permitted 的转向 id 均为升序去重（确定性输出约定）。</summary>
    [Fact]
    public void G6_Turn_Id_Lists_Are_Ascending_And_Unique()
    {
        SignalPlan[] plans = PhaseAutoGenerator.Suggest(FourWayWithTurns(), 0);

        foreach (SignalPlan plan in plans)
        {
            foreach (Stage stage in plan.Stages)
            {
                AssertAscendingUnique(stage.ProtectedTurnIds);
                AssertAscendingUnique(stage.PermittedTurnIds);
            }
        }
    }

    // ==================================================================
    // 夹具与工具
    // ==================================================================

    /// <summary>构建四路口测试路网（含人行道对），并把 TurnGenerator 的结果并入 turns（E-C2 的输入前提）。</summary>
    private static Network FourWayWithTurns()
    {
        Network baseNet = FourWay();
        Turn[] turns = TurnGenerator.FromGeometry(baseNet);
        return new Network { Nodes = baseNet.Nodes, Lanes = baseNet.Lanes, Turns = turns };
    }

    /// <summary>构建四路口测试路网（无 turns）。几何与 TurnGeneratorTests 的 FourWay 一致。</summary>
    private static Network FourWay()
    {
        Node[] nodes =
        [
            N(0, 0, 0, NodeType.Signalized),   // 中心路口
            N(1, 0, -180),                     // 南
            N(2, 180, 0),                      // 东
            N(3, 0, 180),                      // 北
            N(4, -180, 0),                     // 西
        ];

        Lane[] lanes =
        [
            L(SouthIn, road: 1, from: 1, to: 0),
            L(EastIn, road: 2, from: 2, to: 0),
            L(NorthIn, road: 3, from: 3, to: 0),
            L(WestIn, road: 4, from: 4, to: 0),
            L(SouthOut, road: 1, from: 0, to: 1),
            L(EastOut, road: 2, from: 0, to: 2),
            L(NorthOut, road: 3, from: 0, to: 3),
            L(WestOut, road: 4, from: 0, to: 4),
            L(SouthWalkIn, road: 1, from: 1, to: 0, LaneType.Sidewalk),
            L(EastWalkOut, road: 2, from: 0, to: 2, LaneType.Sidewalk),
        ];

        return new Network { Nodes = nodes, Lanes = lanes };
    }

    private static Node N(int id, double x, double y, NodeType type = NodeType.Unsignalized)
        => new() { Id = id, Type = type, X = x, Y = y };

    private static Lane L(int id, int road, int from, int to, LaneType type = LaneType.Driving)
        => new()
        {
            Id = id,
            Road = road,
            Type = type,
            FromNode = from,
            ToNode = to,
            Length = 180d,
            SpeedLimit = 13.89d,
            LaneIndex = 0,
        };

    private static T[] Reverse<T>(IReadOnlyList<T> items)
    {
        var array = new T[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            array[i] = items[items.Count - 1 - i];
        }

        return array;
    }

    /// <summary>按 (src, dst) 查找唯一一条转向的 id。</summary>
    private static int FindTurnId(Network net, int srcLane, int dstLane)
    {
        Turn? found = null;
        foreach (Turn turn in net.Turns)
        {
            if (turn.SrcLane == srcLane && turn.DstLane == dstLane)
            {
                Assert.Null(found);
                found = turn;
            }
        }

        Assert.NotNull(found);
        return found!.Id;
    }

    /// <summary>逐 Stage 断言 protected 集合两两不冲突（逐对调 ConflictMatrix.AreConflicting）。</summary>
    private static void AssertNoProtectedConflict(SignalPlan plan, ConflictMatrix conflicts)
    {
        foreach (Stage stage in plan.Stages)
        {
            IReadOnlyList<int> ids = stage.ProtectedTurnIds;
            for (int i = 0; i < ids.Count; i++)
            {
                for (int j = i + 1; j < ids.Count; j++)
                {
                    Assert.False(
                        conflicts.AreConflicting(ids[i], ids[j]),
                        $"相位内 protected 出现冲突对：{ids[i]} 与 {ids[j]}。");
                }
            }
        }
    }

    private static HashSet<int> CollectTurnIds(SignalPlan plan)
    {
        var set = new HashSet<int>();
        foreach (Stage stage in plan.Stages)
        {
            foreach (int id in stage.ProtectedTurnIds)
            {
                set.Add(id);
            }

            foreach (int id in stage.PermittedTurnIds)
            {
                set.Add(id);
            }
        }

        return set;
    }

    private static void AssertIds(IReadOnlyList<int> expected, IReadOnlyList<int> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i], actual[i]);
        }
    }

    private static void AssertAscendingUnique(IReadOnlyList<int> ids)
    {
        for (int i = 1; i < ids.Count; i++)
        {
            Assert.True(ids[i] > ids[i - 1], "转向 id 列表必须严格升序（升序去重）。");
        }
    }

    /// <summary>逐项比较两套候选集（相位数 / StartTime / type / duration / protected / permitted）。</summary>
    private static void AssertSamePlans(SignalPlan[] expected, SignalPlan[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int p = 0; p < expected.Length; p++)
        {
            Assert.Equal(expected[p].StartTime, actual[p].StartTime);
            Assert.Equal(expected[p].Stages.Count, actual[p].Stages.Count);
            for (int s = 0; s < expected[p].Stages.Count; s++)
            {
                Stage e = expected[p].Stages[s];
                Stage a = actual[p].Stages[s];
                Assert.Equal(e.Type, a.Type);
                Assert.Equal(e.Duration, a.Duration);
                AssertIds(e.ProtectedTurnIds, a.ProtectedTurnIds);
                AssertIds(e.PermittedTurnIds, a.PermittedTurnIds);
            }
        }
    }
}
