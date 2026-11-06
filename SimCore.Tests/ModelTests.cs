using SimCore.Model;
using Xunit;

namespace SimCore.Tests;

/// <summary>
/// T0.2「SimCore 数据模型 + 拓扑编译」验收测试。
/// <para>测试分组与验收标准逐条对应：
/// <list type="bullet">
///   <item>A 组 → 验收标准 a) 能构造单路口（4 进 4 出 + 各种转向）</item>
///   <item>B 组 → 验收标准 b) <c>LaneType / TurnType / TurnPriority</c> 枚举齐全（并含 NodeType）</item>
///   <item>C 组 → 验收标准 c) 拓扑编译后可枚举每条车道的上下游与每节点的转向集合；
///         另含确定性、交通流分组、优先级派生、异常输入等。<c>CompiledNetwork</c> 与 <see cref="Network.Compile"/></item>
/// </list>
/// </para>
/// </summary>
public sealed class ModelTests
{
    // ------------------------------------------------------------------
    // A 组：验收标准 a) 能构造单路口（4 进 4 出 + 各种转向）
    // ------------------------------------------------------------------

    /// <summary>a1：单路口可由模型构造出 4 条进口 + 4 条出口机动车道，且 5 种转向类型齐全。</summary>
    [Fact]
    public void A_Single_Intersection_Has_4_In_4_Out_And_All_Turn_Types()
    {
        CompiledNetwork compiled = BuildFourWayIntersection().Compile();

        int[] inboundDriving = InboundDrivingLanes(compiled, 0);
        int[] outboundDriving = OutboundDrivingLanes(compiled, 0);

        Assert.Equal<int>(new[] { 0, 1, 2, 3 }, inboundDriving);
        Assert.Equal<int>(new[] { 4, 5, 6, 7 }, outboundDriving);
        Assert.Equal(4, inboundDriving.Length);
        Assert.Equal(4, outboundDriving.Length);

        TurnType[] usedTypes = compiled.Turns.Select(t => t.Type).Distinct().OrderBy(t => t).ToArray();
        Assert.Equal<TurnType>(
            new[] { TurnType.Straight, TurnType.Left, TurnType.Right, TurnType.UTurn, TurnType.Crosswalk },
            usedTypes);
        Assert.Equal(20, compiled.TurnCount);
    }

    // ------------------------------------------------------------------
    // B 组：验收标准 b) 枚举齐全，命名与顺序与 §3.1 严格一致
    // ------------------------------------------------------------------

    /// <summary>b1：四个枚举的成员名称与出现顺序与 §3.1 完全一致。</summary>
    [Fact]
    public void B_Enums_Are_Complete_And_Exactly_Named()
    {
        Assert.Equal<string>(
            new[] { "Driving", "Bus", "Biking", "Sidewalk", "SharedLeftTurn", "Construction", "Parking" },
            Enum.GetNames<LaneType>());
        Assert.Equal<string>(
            new[] { "Straight", "Left", "Right", "UTurn", "Crosswalk" },
            Enum.GetNames<TurnType>());
        Assert.Equal<string>(
            new[] { "Banned", "Yield", "Protected" },
            Enum.GetNames<TurnPriority>());
        Assert.Equal<string>(
            new[] { "Signalized", "Unsignalized", "Origin", "Destination" },
            Enum.GetNames<NodeType>());
    }

    /// <summary>b2：枚举底层值自 0 起连续（保证序列化映射稳定）。</summary>
    [Fact]
    public void B_Enums_Have_Sequential_Values()
    {
        Assert.Equal(0, (int)LaneType.Driving);
        Assert.Equal(6, (int)LaneType.Parking);

        Assert.Equal(0, (int)TurnType.Straight);
        Assert.Equal(4, (int)TurnType.Crosswalk);

        Assert.Equal(0, (int)TurnPriority.Banned);
        Assert.Equal(2, (int)TurnPriority.Protected);

        Assert.Equal(0, (int)NodeType.Signalized);
        Assert.Equal(3, (int)NodeType.Destination);
    }

    // ------------------------------------------------------------------
    // C 组：验收标准 c) 拓扑编译后可枚举车道上下游与节点转向集合
    // ------------------------------------------------------------------

    /// <summary>c1：编译后可按 id 直寻址，并能枚举每节点的转向集合。</summary>
    [Fact]
    public void C_Compiled_Network_Enumerates_Node_Turn_Sets()
    {
        CompiledNetwork compiled = BuildFourWayIntersection().Compile();

        Assert.Equal(5, compiled.NodeCount);
        Assert.Equal(16, compiled.LaneCount);
        Assert.Equal(20, compiled.TurnCount);

        Assert.Equal(20, compiled.TurnsAtNode(0).Count);
        Assert.Empty(compiled.TurnsAtNode(1));
        Assert.Empty(compiled.TurnsAtNode(2));
        Assert.Empty(compiled.TurnsAtNode(3));
        Assert.Empty(compiled.TurnsAtNode(4));

        Assert.Equal(1, compiled.GetLane(0).FromNode);
        Assert.Equal(TurnType.UTurn, compiled.GetTurn(3).Type);
        Assert.Equal(NodeType.Signalized, compiled.GetNode(0).Type);
    }

    /// <summary>c2：编译后可枚举每条车道的上游 / 下游车道与进出转向。</summary>
    [Fact]
    public void C_Compiled_Network_Enumerates_Lane_Upstream_And_Downstream()
    {
        CompiledNetwork compiled = BuildFourWayIntersection().Compile();

        // 北进口车道 0：下游 = 直/左/右/掉头分别指向的出口车道 {4,5,6,7}；无上游。
        Assert.Equal<int>(new[] { 4, 5, 6, 7 }, compiled.DownstreamLanes(0));
        Assert.Empty(compiled.UpstreamLanes(0));

        // 北出口车道 4：上游 = 能驶入它的进口车道 {0,1,2,3}；出口车道不再发起转向。
        Assert.Equal<int>(new[] { 0, 1, 2, 3 }, compiled.UpstreamLanes(4));
        Assert.Empty(compiled.DownstreamLanes(4));

        // 进出转向（按 id 升序）
        Assert.Equal<int>(new[] { 0, 1, 2, 3 }, compiled.OutgoingTurns(0));
        Assert.Equal<int>(new[] { 3, 6, 8, 13 }, compiled.IncomingTurns(4));

        // 节点进口 / 出口车道（含人行道）
        Assert.Equal<int>(new[] { 0, 1, 2, 3, 8, 9, 10, 11 }, compiled.IncomingLanes(0));
        Assert.Equal<int>(new[] { 4, 5, 6, 7, 12, 13, 14, 15 }, compiled.OutgoingLanes(0));
    }

    /// <summary>c3：编译结果确定、与输入顺序无关；交通流按 (节点, 进口道路, 转向类型) 分组。</summary>
    [Fact]
    public void C_Compile_Is_Deterministic_And_Groups_Movements()
    {
        CompiledNetwork first = BuildFourWayIntersection().Compile();
        CompiledNetwork second = BuildFourWayIntersection().Compile();
        AssertSameTopology(first, second);

        Network source = BuildFourWayIntersection();
        Network reversed = new()
        {
            Nodes = source.Nodes.Reverse().ToArray(),
            Lanes = source.Lanes.Reverse().ToArray(),
            Turns = source.Turns.Reverse().ToArray(),
        };
        AssertSameTopology(first, reversed.Compile());

        // 单车道进口：一路一型一转向，20 条转向 -> 20 个交通流，且全部挂在节点 0。
        Assert.Equal(20, first.MovementCount);
        Assert.Equal(20, first.MovementsAtNode(0).Count);
        Assert.Empty(first.MovementsAtNode(1));
    }

    /// <summary>c4：同一进口道路、同一转向类型的多条转向被聚为一个 Movement。</summary>
    [Fact]
    public void C_Movement_Groups_Turns_By_Node_Road_And_Type()
    {
        Network network = new()
        {
            Nodes = new List<Node>
            {
                new() { Id = 0, Type = NodeType.Signalized },
                new() { Id = 1, Type = NodeType.Unsignalized },
            },
            Lanes = new List<Lane>
            {
                new() { Id = 0, Road = 1, Type = LaneType.Driving, FromNode = 1, ToNode = 0 },
                new() { Id = 1, Road = 1, Type = LaneType.Driving, FromNode = 1, ToNode = 0 },
                new() { Id = 2, Road = 1, Type = LaneType.Driving, FromNode = 0, ToNode = 1 },
            },
            Turns = new List<Turn>
            {
                new() { Id = 0, SrcLane = 0, DstLane = 2, Node = 0, Type = TurnType.Straight },
                new() { Id = 1, SrcLane = 1, DstLane = 2, Node = 0, Type = TurnType.Straight },
                new() { Id = 2, SrcLane = 0, DstLane = 2, Node = 0, Type = TurnType.Left },
            },
        };

        CompiledNetwork compiled = network.Compile();

        Assert.Equal(2, compiled.MovementCount);
        Assert.Equal<int>(new[] { 0, 1 }, compiled.MovementsAtNode(0));

        // SortedDictionary 按 (Node, Road, Type) 字典序分配 id：Straight(0) 在 Left(1) 之前。
        Movement straight = compiled.Movements[0];
        Assert.Equal(0, straight.Node);
        Assert.Equal(1, straight.Road);
        Assert.Equal(TurnType.Straight, straight.Type);
        Assert.Equal<int>(new[] { 0, 1 }, straight.TurnIds);

        Movement left = compiled.Movements[1];
        Assert.Equal(TurnType.Left, left.Type);
        Assert.Equal<int>(new[] { 2 }, left.TurnIds);
    }

    /// <summary>c5：相位由 protected/permitted 派生转向优先级；节点承载信号配时方案。</summary>
    [Fact]
    public void C_Stage_Derives_Priority_And_Node_Carries_Signal_Plan()
    {
        Stage stage = new()
        {
            Type = StageType.Fixed,
            Duration = 30.0,
            ProtectedTurnIds = new[] { 0, 1 },
            PermittedTurnIds = new[] { 7 },
        };

        Assert.Equal(TurnPriority.Protected, stage.PriorityOf(0));
        Assert.Equal(TurnPriority.Protected, stage.PriorityOf(1));
        Assert.Equal(TurnPriority.Yield, stage.PriorityOf(7));
        Assert.Equal(TurnPriority.Banned, stage.PriorityOf(2));
        Assert.Equal(TurnPriority.Banned, stage.PriorityOf(99));

        Node node = new()
        {
            Id = 0,
            Type = NodeType.Signalized,
            Offset = 5.0,
            SignalPlans = new[]
            {
                new SignalPlan { StartTime = 0.0, Stages = new[] { stage } },
            },
        };

        Node edge = new() { Id = 1, Type = NodeType.Unsignalized };

        Assert.True(node.IsSignalized);
        Assert.False(edge.IsSignalized);
        Assert.Equal(5.0, node.Offset);
        Assert.Single(node.SignalPlans);
        Assert.Single(node.SignalPlans[0].Stages);
        Assert.Equal(30.0, node.SignalPlans[0].Stages[0].Duration);
    }

    /// <summary>c6：非法拓扑（引用越界 / id 重复 / id 跳号 / 空引用）被明确拒绝。</summary>
    [Fact]
    public void C_Compile_Rejects_Broken_Topology()
    {
        Assert.Throws<ArgumentNullException>(() => CompiledNetwork.Compile(null!));

        Network badTurnRef = new()
        {
            Nodes = new List<Node> { new() { Id = 0, Type = NodeType.Unsignalized } },
            Lanes = new List<Lane> { new() { Id = 0, Road = 1, Type = LaneType.Driving, FromNode = 0, ToNode = 0 } },
            Turns = new List<Turn> { new() { Id = 0, SrcLane = 0, DstLane = 99, Node = 0, Type = TurnType.Straight } },
        };
        Assert.Throws<ArgumentOutOfRangeException>(() => badTurnRef.Compile());

        Network duplicateId = new()
        {
            Nodes = new List<Node>
            {
                new() { Id = 0, Type = NodeType.Unsignalized },
                new() { Id = 0, Type = NodeType.Unsignalized },
            },
        };
        Assert.Throws<ArgumentException>(() => duplicateId.Compile());

        Network gapId = new()
        {
            Nodes = new List<Node>
            {
                new() { Id = 0, Type = NodeType.Unsignalized },
                new() { Id = 2, Type = NodeType.Unsignalized },
            },
        };
        Assert.Throws<ArgumentException>(() => gapId.Compile());
    }

    // ------------------------------------------------------------------
    // 工具
    // ------------------------------------------------------------------

    /// <summary>
    /// 构造一个单路口网络：中心节点 0（信号路口），四个方向各 1 条进口 + 1 条出口机动车道，
    /// 另有 4 条进口 + 4 条出口人行道；机动车每个进口含 直/左/右/掉头 四种转向，另有 4 条人行横道转向。
    /// </summary>
    private static Network BuildFourWayIntersection()
    {
        var nodes = new List<Node>
        {
            new() { Id = 0, Type = NodeType.Signalized, X = 0, Y = 0 },
            new() { Id = 1, Type = NodeType.Unsignalized, X = 0, Y = -180 },
            new() { Id = 2, Type = NodeType.Unsignalized, X = 180, Y = 0 },
            new() { Id = 3, Type = NodeType.Unsignalized, X = 0, Y = 180 },
            new() { Id = 4, Type = NodeType.Unsignalized, X = -180, Y = 0 },
        };

        var lanes = new List<Lane>();
        for (int i = 0; i < 4; i++)
        {
            AddLane(lanes, i, i + 1, LaneType.Driving, i + 1, 0);       // 4 条进口机动车道
            AddLane(lanes, i + 4, i + 1, LaneType.Driving, 0, i + 1);   // 4 条出口机动车道
            AddLane(lanes, i + 8, i + 1, LaneType.Sidewalk, i + 1, 0);  // 4 条进口人行道
            AddLane(lanes, i + 12, i + 1, LaneType.Sidewalk, 0, i + 1); // 4 条出口人行道
        }

        var turns = new List<Turn>();
        // 每个进口机动车道 L(i)：直→L((i+2)%4+4)、左→L((i+1)%4+4)、右→L((i+3)%4+4)、掉头→L(i+4)
        for (int i = 0; i < 4; i++)
        {
            int straight = ((i + 2) % 4) + 4;
            int left = ((i + 1) % 4) + 4;
            int right = ((i + 3) % 4) + 4;
            int uTurn = i + 4;
            int baseId = i * 4;
            AddTurn(turns, baseId, i, straight, 0, TurnType.Straight);
            AddTurn(turns, baseId + 1, i, left, 0, TurnType.Left);
            AddTurn(turns, baseId + 2, i, right, 0, TurnType.Right);
            AddTurn(turns, baseId + 3, i, uTurn, 0, TurnType.UTurn);
        }

        // 4 条人行横道：进口人行道 L(8+i) → 对面出口人行道 L(14 - i)
        AddTurn(turns, 16, 8, 14, 0, TurnType.Crosswalk);
        AddTurn(turns, 17, 9, 15, 0, TurnType.Crosswalk);
        AddTurn(turns, 18, 10, 12, 0, TurnType.Crosswalk);
        AddTurn(turns, 19, 11, 13, 0, TurnType.Crosswalk);

        return new Network { Nodes = nodes, Lanes = lanes, Turns = turns };
    }

    private static void AddLane(List<Lane> lanes, int id, int road, LaneType type, int fromNode, int toNode)
    {
        lanes.Add(new Lane
        {
            Id = id,
            Road = road,
            Type = type,
            FromNode = fromNode,
            ToNode = toNode,
            Length = 180.0,
            SpeedLimit = 13.89,
            LaneIndex = 0,
        });
    }

    private static void AddTurn(List<Turn> turns, int id, int srcLane, int dstLane, int node, TurnType type)
    {
        turns.Add(new Turn { Id = id, SrcLane = srcLane, DstLane = dstLane, Node = node, Type = type });
    }

    private static int[] InboundDrivingLanes(CompiledNetwork compiled, int nodeId)
        => compiled.IncomingLanes(nodeId)
            .Where(id => compiled.GetLane(id).Type == LaneType.Driving)
            .ToArray();

    private static int[] OutboundDrivingLanes(CompiledNetwork compiled, int nodeId)
        => compiled.OutgoingLanes(nodeId)
            .Where(id => compiled.GetLane(id).Type == LaneType.Driving)
            .ToArray();

    private static void AssertSameTopology(CompiledNetwork expected, CompiledNetwork actual)
    {
        Assert.Equal(expected.NodeCount, actual.NodeCount);
        Assert.Equal(expected.LaneCount, actual.LaneCount);
        Assert.Equal(expected.TurnCount, actual.TurnCount);
        Assert.Equal(expected.MovementCount, actual.MovementCount);

        for (int lane = 0; lane < expected.LaneCount; lane++)
        {
            Assert.Equal<int>(expected.OutgoingTurns(lane), actual.OutgoingTurns(lane));
            Assert.Equal<int>(expected.IncomingTurns(lane), actual.IncomingTurns(lane));
            Assert.Equal<int>(expected.DownstreamLanes(lane), actual.DownstreamLanes(lane));
            Assert.Equal<int>(expected.UpstreamLanes(lane), actual.UpstreamLanes(lane));
        }

        for (int node = 0; node < expected.NodeCount; node++)
        {
            Assert.Equal<int>(expected.TurnsAtNode(node), actual.TurnsAtNode(node));
            Assert.Equal<int>(expected.MovementsAtNode(node), actual.MovementsAtNode(node));
            Assert.Equal<int>(expected.IncomingLanes(node), actual.IncomingLanes(node));
            Assert.Equal<int>(expected.OutgoingLanes(node), actual.OutgoingLanes(node));
        }

        for (int m = 0; m < expected.MovementCount; m++)
        {
            Movement left = expected.Movements[m];
            Movement right = actual.Movements[m];
            Assert.Equal(left.Node, right.Node);
            Assert.Equal(left.Road, right.Road);
            Assert.Equal(left.Type, right.Type);
            Assert.Equal<int>(left.TurnIds, right.TurnIds);
        }
    }
}
