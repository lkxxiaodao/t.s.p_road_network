using System.Reflection;
using SimCore.Geometry;
using SimCore.Model;
using Xunit;

namespace SimCore.Tests;

/// <summary>
/// T4.2(a)「转向自动生成」验收测试（<c>docs/SimCore-API.md</c> §3.2）。
/// <para>分组与验收标准逐条对应，便于"手和眼"对证：
/// <list type="bullet">
///   <item>A 组 → 验收 a) 十字路口 直行/左/右/掉头 各一条单测 + 全表核对</item>
///   <item>B 组 → 验收 b) 人行道之间→Crosswalk；机动车车道之间不得出现 Crosswalk</item>
///   <item>C 组 → 验收 c) id 0..N-1 连续唯一；两次调用逐项相等（+ 输入顺序无关）</item>
///   <item>D 组 → 验收 d) network == null → ArgumentNullException</item>
///   <item>E 组 → 验收 e) 每个 Turn 满足 src_lane.to_node == node 且 dst_lane.from_node == node</item>
///   <item>F 组 → 夹角阈值固定常量单测 / 边界分类 / 冻结签名对齐 / 空网络 / 悬空引用</item>
/// </list>
/// </para>
/// <para>测试路网（见 <see cref="FourWay"/>）：中心节点 0 为信号路口，南(S)/东(E)/北(N)/西(W)
/// 各一条进口 <c>Driving</c> 车道（id 0..3）与一条出口车道（id 4..7），另有南人行道进口 lane8、
/// 东人行道出口 lane9。节点坐标：S=(0,−180)、E=(180,0)、N=(0,180)、W=(−180,0)、中心=(0,0)（Y 轴向上）。</para>
/// </summary>
public sealed class TurnGeneratorTests
{
    // 车道 id 约定（FourWay 路网）
    private const int SouthIn = 0;    // 南进口（1→0，行驶方向 北）
    private const int EastIn = 1;     // 东进口（2→0，行驶方向 西）
    private const int NorthIn = 2;    // 北进口（3→0，行驶方向 南）
    private const int WestIn = 3;     // 西进口（4→0，行驶方向 东）
    private const int SouthOut = 4;   // 南出口（0→1，行驶方向 南）
    private const int EastOut = 5;    // 东出口（0→2，行驶方向 东）
    private const int NorthOut = 6;   // 北出口（0→3，行驶方向 北）
    private const int WestOut = 7;    // 西出口（0→4，行驶方向 西）
    private const int SouthWalkIn = 8;  // 南人行道进口（1→0，Sidewalk）
    private const int EastWalkOut = 9;  // 东人行道出口（0→2，Sidewalk）

    // ==================================================================
    // A 组：验收 a) 直行 / 左 / 右 / 掉头 各一条单测
    // ==================================================================

    /// <summary>a1：南进口直行至北出口（两段方向同向）→ Straight。</summary>
    [Fact]
    public void A1_Straight_On_South_Approach_To_North_Exit()
    {
        Turn[] turns = TurnGenerator.FromGeometry(FourWay());

        Turn straight = Find(turns, SouthIn, NorthOut);
        Assert.Equal(TurnType.Straight, straight.Type);
        Assert.Equal(0, straight.Node);
    }

    /// <summary>a2：南进口左拐至西出口（行驶方向由北逆时针转 90°）→ Left。</summary>
    [Fact]
    public void A2_Left_On_South_Approach_To_West_Exit()
    {
        Turn[] turns = TurnGenerator.FromGeometry(FourWay());

        Turn left = Find(turns, SouthIn, WestOut);
        Assert.Equal(TurnType.Left, left.Type);
        Assert.Equal(0, left.Node);
    }

    /// <summary>a3：南进口右拐至东出口（行驶方向由北顺时针转 90°）→ Right。</summary>
    [Fact]
    public void A3_Right_On_South_Approach_To_East_Exit()
    {
        Turn[] turns = TurnGenerator.FromGeometry(FourWay());

        Turn right = Find(turns, SouthIn, EastOut);
        Assert.Equal(TurnType.Right, right.Type);
        Assert.Equal(0, right.Node);
    }

    /// <summary>a4：南进口掉头至南出口（行驶方向反转 180°）→ UTurn。</summary>
    [Fact]
    public void A4_UTurn_On_South_Approach_To_South_Exit()
    {
        Turn[] turns = TurnGenerator.FromGeometry(FourWay());

        Turn uTurn = Find(turns, SouthIn, SouthOut);
        Assert.Equal(TurnType.UTurn, uTurn.Type);
        Assert.Equal(0, uTurn.Node);
    }

    /// <summary>a5（合并证据）：中心路口 4 进口 × 4 出口 = 16 条机动车转向，类型逐条对齐期望表。</summary>
    [Fact]
    public void A5_All_Sixteen_Vehicle_Turns_Match_Expected_Table()
    {
        Turn[] turns = TurnGenerator.FromGeometry(FourWay());

        // (进口, 出口, 期望类型)
        (int Src, int Dst, TurnType Type)[] expected =
        [
            (SouthIn, SouthOut, TurnType.UTurn), (SouthIn, EastOut, TurnType.Right),
            (SouthIn, NorthOut, TurnType.Straight), (SouthIn, WestOut, TurnType.Left),

            (EastIn, SouthOut, TurnType.Left), (EastIn, EastOut, TurnType.UTurn),
            (EastIn, NorthOut, TurnType.Right), (EastIn, WestOut, TurnType.Straight),

            (NorthIn, SouthOut, TurnType.Straight), (NorthIn, EastOut, TurnType.Left),
            (NorthIn, NorthOut, TurnType.UTurn), (NorthIn, WestOut, TurnType.Right),

            (WestIn, SouthOut, TurnType.Right), (WestIn, EastOut, TurnType.Straight),
            (WestIn, NorthOut, TurnType.Left), (WestIn, WestOut, TurnType.UTurn),
        ];

        foreach ((int src, int dst, TurnType type) in expected)
        {
            Assert.Equal(type, Find(turns, src, dst).Type);
        }
    }

    /// <summary>a6（口径留痕）：转向对<b>每个节点</b>都生成（§3.2 字面），故端节点（南端 node1）
    /// 的"进口 lane4 → 出口 lane0"也会得到一条 UTurn。</summary>
    [Fact]
    public void A6_Terminal_Node_Produces_UTurn()
    {
        Turn[] turns = TurnGenerator.FromGeometry(FourWay());

        Turn uTurnAtSouthEnd = Find(turns, SouthOut, SouthIn);   // lane4 → lane0，位于节点 1
        Assert.Equal(TurnType.UTurn, uTurnAtSouthEnd.Type);
        Assert.Equal(1, uTurnAtSouthEnd.Node);
    }

    // ==================================================================
    // B 组：验收 b) 人行道之间 → Crosswalk；机动车之间不得出现 Crosswalk
    // ==================================================================
    /// <summary>b1：Sidewalk（南人行道进口 lane8）→ Sidewalk（东人行道出口 lane9）→ Crosswalk。
    /// 该几何为 90° 夹角（若被当成机动车转向会误判为 Left），故本条同时证明人行道不被
    /// 归入 Straight/Left/Right。</summary>
    [Fact]
    public void B1_Sidewalk_To_Sidewalk_Is_Crosswalk()
    {
        Turn[] turns = TurnGenerator.FromGeometry(FourWay());

        Turn crosswalk = Find(turns, SouthWalkIn, EastWalkOut);
        Assert.Equal(TurnType.Crosswalk, crosswalk.Type);
        Assert.NotEqual(TurnType.Left, crosswalk.Type);
        Assert.Equal(0, crosswalk.Node);
    }

    /// <summary>b2：机动车车道之间绝不出现 Crosswalk；且所有 Crosswalk 的两端车道均为 Sidewalk。</summary>
    [Fact]
    public void B2_No_Crosswalk_Between_Vehicle_Lanes()
    {
        Network net = FourWay();
        Turn[] turns = TurnGenerator.FromGeometry(net);

        var lanesById = net.Lanes.ToDictionary(lane => lane.Id);
        int vehiclePairs = 0;
        int crosswalks = 0;

        foreach (Turn turn in turns)
        {
            Lane src = lanesById[turn.SrcLane];
            Lane dst = lanesById[turn.DstLane];
            bool srcVehicle = src.Type != LaneType.Sidewalk;
            bool dstVehicle = dst.Type != LaneType.Sidewalk;

            if (srcVehicle && dstVehicle)
            {
                vehiclePairs++;
                Assert.NotEqual(TurnType.Crosswalk, turn.Type);
            }

            if (turn.Type == TurnType.Crosswalk)
            {
                crosswalks++;
                Assert.Equal(LaneType.Sidewalk, src.Type);
                Assert.Equal(LaneType.Sidewalk, dst.Type);
            }
        }

        Assert.Equal(20, vehiclePairs);   // 中心路口 16 条 + 四个端节点各 1 条掉头（turns 在每个节点都生成）
        Assert.Equal(1, crosswalks);      // 唯一人行道对：lane8 → lane9
    }

    // ==================================================================
    // C 组：验收 c) id 连续唯一；两次调用逐项相等
    // ==================================================================

    /// <summary>c1：返回的 Turn[] 的 id 恰为 0..N-1 连续且唯一。</summary>
    [Fact]
    public void C1_Turn_Ids_Are_Contiguous_And_Unique()
    {
        Turn[] turns = TurnGenerator.FromGeometry(FourWay());

        var seen = new HashSet<int>();
        for (int i = 0; i < turns.Length; i++)
        {
            Assert.Equal(i, turns[i].Id);          // 下标即 id（连续）
            Assert.True(seen.Add(turns[i].Id));    // 唯一
        }

        Assert.NotEmpty(turns);
    }

    /// <summary>c2：同一 network 两次调用，结果逐项（Id/SrcLane/DstLane/Node/Type）相等。</summary>
    [Fact]
    public void C2_Two_Calls_Are_Item_By_Item_Equal()
    {
        Network net = FourWay();

        Turn[] first = TurnGenerator.FromGeometry(net);
        Turn[] second = TurnGenerator.FromGeometry(net);

        AssertSameTurns(first, second);
    }

    /// <summary>c3（强化确定性）：打乱输入列表顺序（反转 Nodes/Lanes）后调用，结果逐项相等。</summary>
    [Fact]
    public void C3_Result_Is_Independent_Of_Input_List_Order()
    {
        Network net = FourWay();
        Turn[] original = TurnGenerator.FromGeometry(net);

        var reversed = new Network { Nodes = Reverse(net.Nodes), Lanes = Reverse(net.Lanes) };
        Turn[] reordered = TurnGenerator.FromGeometry(reversed);

        AssertSameTurns(original, reordered);
    }

    // ==================================================================
    // D 组：验收 d) network == null → ArgumentNullException
    // ==================================================================

    /// <summary>d1：null 入参抛 <see cref="ArgumentNullException"/>。</summary>
    [Fact]
    public void D1_Null_Network_Throws_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => TurnGenerator.FromGeometry(null!));
    }

    // ==================================================================
    // E 组：验收 e) 每个 Turn 满足 src.to_node == node 且 dst.from_node == node
    // ==================================================================

    /// <summary>e1：逐条校验几何一致性（保证不会触发 <c>turn_node_mismatch</c>）。</summary>
    [Fact]
    public void E1_Every_Turn_Respects_Node_Geometry()
    {
        Network net = FourWay();
        Turn[] turns = TurnGenerator.FromGeometry(net);

        var lanesById = net.Lanes.ToDictionary(lane => lane.Id);
        foreach (Turn turn in turns)
        {
            Assert.Equal(turn.Node, lanesById[turn.SrcLane].ToNode);
            Assert.Equal(turn.Node, lanesById[turn.DstLane].FromNode);
        }

        Assert.NotEmpty(turns);
    }

    // ==================================================================
    // F 组：阈值常量 / 边界分类 / 冻结签名 / 空网络 / 悬空引用
    // ==================================================================

    /// <summary>f1：夹角阈值为固定常量（45° 直行上界、135° 掉头下界），不读环境、不随机。</summary>
    [Fact]
    public void F1_Angle_Threshold_Constants_Are_Fixed()
    {
        Assert.Equal(45.0d, TurnGenerator.StraightMaxTurnDegrees);
        Assert.Equal(135.0d, TurnGenerator.UTurnMinTurnDegrees);
    }

    /// <summary>f2：阈值边界分类——44.9°/45.1°/134.9°/135.1° 及各自镜像角，逐条对齐期望类型。</summary>
    [Fact]
    public void F2_Angle_Boundary_Classification()
    {
        // 进口行驶方向固定为"东"（approach 由 (−100,0) 驶向中心 (0,0)）；
        // 出口方向按角度 θ（自东轴逆时针）放置，故 θ 即转向角。
        Network net = AngleNetwork(44.9, 45.1, 134.9, 135.1, -45.1, -134.9, -135.1, 0.0);
        Turn[] turns = TurnGenerator.FromGeometry(net);

        Assert.Equal(TurnType.Straight, Find(turns, 0, 1).Type);   // 44.9°  （≤45）
        Assert.Equal(TurnType.Left, Find(turns, 0, 2).Type);       // 45.1°  （>45，左）
        Assert.Equal(TurnType.Left, Find(turns, 0, 3).Type);       // 134.9° （<135，左）
        Assert.Equal(TurnType.UTurn, Find(turns, 0, 4).Type);      // 135.1° （≥135）
        Assert.Equal(TurnType.Right, Find(turns, 0, 5).Type);      // −45.1° （右）
        Assert.Equal(TurnType.Right, Find(turns, 0, 6).Type);      // −134.9°（右）
        Assert.Equal(TurnType.UTurn, Find(turns, 0, 7).Type);      // −135.1°（≥135）
        Assert.Equal(TurnType.Straight, Find(turns, 0, 8).Type);   // 0°
    }

    /// <summary>f3：冻结签名对齐——public 面恰为 <c>static Turn[] FromGeometry(Network)</c>（§3.2）。</summary>
    [Fact]
    public void F3_Public_Surface_Is_Frozen()
    {
        Type type = typeof(TurnGenerator);

        MethodInfo? method = type.GetMethod(
            "FromGeometry", BindingFlags.Public | BindingFlags.Static, null, [typeof(Network)], null);
        Assert.NotNull(method);
        Assert.Equal(typeof(Turn[]), method!.ReturnType);

        MethodInfo[] declared = type.GetMethods(
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        MethodInfo only = Assert.Single(declared);
        Assert.Equal("FromGeometry", only.Name);
    }

    /// <summary>f4：空路网 / 有节点无车道 → 返回空数组（无异常）。</summary>
    [Fact]
    public void F4_Empty_Network_Returns_Empty_Array()
    {
        Assert.Empty(TurnGenerator.FromGeometry(new Network()));

        var nodesOnly = new Network
        {
            Nodes = [N(0, 0, 0), N(1, 0, -100)],
            Lanes = [],
        };
        Assert.Empty(TurnGenerator.FromGeometry(nodesOnly));
    }

    /// <summary>f5：车道引用了不存在的节点 → <see cref="ArgumentException"/>（不静默吞掉越界引用，§1.3）。</summary>
    [Fact]
    public void F5_Dangling_Node_Reference_Throws()
    {
        var net = new Network
        {
            Nodes = [N(0, 0, 0)],
            Lanes = [L(0, road: 1, from: 99, to: 0)],   // from_node=99 不存在
        };

        Assert.Throws<ArgumentException>(() => TurnGenerator.FromGeometry(net));
    }

    // ==================================================================
    // 夹具与工具
    // ==================================================================

    /// <summary>构建四路口测试路网（含人行道对）。</summary>
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
            L(SouthIn, road: 1, from: 1, to: 0),                        // 南进口
            L(EastIn, road: 2, from: 2, to: 0),                         // 东进口
            L(NorthIn, road: 3, from: 3, to: 0),                        // 北进口
            L(WestIn, road: 4, from: 4, to: 0),                         // 西进口
            L(SouthOut, road: 1, from: 0, to: 1),                       // 南出口
            L(EastOut, road: 2, from: 0, to: 2),                        // 东出口
            L(NorthOut, road: 3, from: 0, to: 3),                       // 北出口
            L(WestOut, road: 4, from: 0, to: 4),                        // 西出口
            L(SouthWalkIn, road: 1, from: 1, to: 0, LaneType.Sidewalk), // 南人行道进口
            L(EastWalkOut, road: 2, from: 0, to: 2, LaneType.Sidewalk), // 东人行道出口
        ];

        return new Network { Nodes = nodes, Lanes = lanes };
    }

    /// <summary>
    /// 构建"角度夹具"：进口 lane0 由 (−100,0) 驶向中心 (0,0)（方向东）；每条出口 lane(1+i)
    /// 由中心驶向与东轴成 <paramref name="exitAngles"/>[i] 度（逆时针）的点 → 该角度即转向角。
    /// </summary>
    private static Network AngleNetwork(params double[] exitAngles)
    {
        var nodes = new List<Node>
        {
            N(0, 0, 0, NodeType.Signalized),
            N(1, -100, 0),
        };

        var lanes = new List<Lane>
        {
            L(0, road: 1, from: 1, to: 0),
        };

        for (int i = 0; i < exitAngles.Length; i++)
        {
            double radians = exitAngles[i] * Math.PI / 180.0d;
            int nodeId = 2 + i;
            nodes.Add(N(nodeId, Math.Cos(radians) * 100.0d, Math.Sin(radians) * 100.0d));
            lanes.Add(L(1 + i, road: 1, from: 0, to: nodeId));
        }

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

    /// <summary>按 (src, dst) 唯一查找一条转向；缺失或重复即断言失败。</summary>
    private static Turn Find(Turn[] turns, int srcLane, int dstLane)
    {
        Turn? found = null;
        foreach (Turn turn in turns)
        {
            if (turn.SrcLane == srcLane && turn.DstLane == dstLane)
            {
                Assert.Null(found);   // 同一 (src, dst) 不得重复出现
                found = turn;
            }
        }

        Assert.NotNull(found);
        return found!;
    }

    /// <summary>逐项比较两次调用的结果（Id/SrcLane/DstLane/Node/Type 全等）。</summary>
    private static void AssertSameTurns(Turn[] expected, Turn[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Id, actual[i].Id);
            Assert.Equal(expected[i].SrcLane, actual[i].SrcLane);
            Assert.Equal(expected[i].DstLane, actual[i].DstLane);
            Assert.Equal(expected[i].Node, actual[i].Node);
            Assert.Equal(expected[i].Type, actual[i].Type);
        }
    }
}
