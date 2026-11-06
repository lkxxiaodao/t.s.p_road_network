using SimCore.Io;
using SimCore.Model;

namespace SignalPlanner.MapExport;

/// <summary>导出用的节点（来自 <see cref="Node"/>）。</summary>
public sealed class ExportNode
{
    public int Id { get; init; }
    public NodeType Type { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Offset { get; init; }
    public IReadOnlyList<SignalPlan> Plans { get; init; } = Array.Empty<SignalPlan>();
}

/// <summary>导出用的车道（来自 <see cref="Lane"/>，并附带 OpenDRIVE 横向排位）。</summary>
public sealed class ExportLane
{
    public int Id { get; init; }
    public int RoadId { get; init; }
    public LaneType Type { get; init; }
    public int FromNode { get; init; }
    public int ToNode { get; init; }
    public double Length { get; init; }
    public double SpeedLimit { get; init; }
    public int LaneIndex { get; init; }
    public int LatIndex { get; init; }

    public bool IsForward { get; internal set; }
    public int Rank { get; internal set; }
    public int OdrLaneId { get; internal set; }
    /// <summary>该车道所属的“单向 OpenDRIVE road” id（forward / backward 各一条）。</summary>
    public int DirectionalRoadId { get; internal set; }
    public double LateralOffset { get; internal set; }
    public int? PredecessorLaneId { get; internal set; }
    public int? SuccessorLaneId { get; internal set; }
}

/// <summary>导出用的一条道路（共享 <c>lane.Road</c> 的车道集合）。</summary>
public sealed class ExportRoad
{
    public int Id { get; init; }
    public int NodeA { get; init; }
    public int NodeB { get; init; }
    public double Ax { get; init; }
    public double Ay { get; init; }
    public double Bx { get; init; }
    public double By { get; init; }
    public double Dx { get; init; }
    public double Dy { get; init; }
    public double Length { get; init; }
    public double Heading { get; init; }

    public List<ExportLane> ForwardLanes { get; } = new();
    public List<ExportLane> BackwardLanes { get; } = new();
    public List<ExportLane> AllLanes { get; } = new();

    /// <summary>正向（a→b）单向 OpenDRIVE road id。</summary>
    public int ForwardRoadId { get; init; }

    /// <summary>反向（b→a）单向 OpenDRIVE road id。</summary>
    public int BackwardRoadId { get; init; }

    public bool IsJunctionA { get; internal set; }
    public bool IsJunctionB { get; internal set; }
    public int LinkRoadA { get; internal set; } = -1;
    public int LinkRoadB { get; internal set; } = -1;
    public string ContactPointA { get; internal set; } = string.Empty;
    public string ContactPointB { get; internal set; } = string.Empty;

    public IEnumerable<ExportLane> LanesFromCenter => ForwardLanes.Concat(BackwardLanes);

    public int DirectionalRoadId(bool forward) => forward ? ForwardRoadId : BackwardRoadId;

    public int DirectionalStartNode(bool forward) => forward ? NodeA : NodeB;

    public int DirectionalEndNode(bool forward) => forward ? NodeB : NodeA;
}

/// <summary>导出用的一条转向。</summary>
public sealed class ExportTurn
{
    public int Id { get; init; }
    public int NodeId { get; init; }
    public TurnType Type { get; init; }
    public int Leg { get; init; } = -1;
    public int IslandId { get; init; } = -1;
    public ExportLane Src { get; init; } = null!;
    public ExportLane Dst { get; init; } = null!;

    /// <summary>
    /// 转率（`.spmap` 的 <c>turns[].ratio</c>，缺省 1.0）。
    /// <para>生成 SUMO 路线时当权重用：同一车道的出口转向若**全是 1.0**就按转向类型的
    /// 默认比例算，只要有一条不等于 1.0 就全部改按 ratio（见 <see cref="Sumo.SumoRouteEnumerator"/>）。</para>
    /// </summary>
    public double Ratio { get; init; } = 1.0;

    public bool IsCrosswalk => Type == TurnType.Crosswalk;

    /// <summary>机动车转向（Driving / Bus / SharedLeftTurn）。人行/非机动车转向不进入信号灯统计。</summary>
    public bool IsMotor => IsMotorLane(Src.Type) && IsMotorLane(Dst.Type);

    /// <summary>
    /// 是否写入 OpenDRIVE 路口连接：
    /// - 机动车转向；
    /// - 同类型的自行车/人行道转向（含 Crosswalk，用于把这两类车道连起来）。
    /// 混合类型（如 biking → driving）不写，避免类型不匹配。
    /// </summary>
    public bool ShouldExport
    {
        get
        {
            if (IsCrosswalk)
            {
                return Src.Type == LaneType.Sidewalk && Dst.Type == LaneType.Sidewalk;
            }

            if (IsMotor)
            {
                return true;
            }

            return Src.Type == Dst.Type &&
                   Src.Type is LaneType.Biking or LaneType.Sidewalk;
        }
    }

    private static bool IsMotorLane(LaneType type)
        => type is LaneType.Driving or LaneType.Bus or LaneType.SharedLeftTurn;
}

/// <summary>一个信号灯位（每条进入信号路口的车道一个）。</summary>
public sealed class ExportSignalHead
{
    public int NodeId { get; init; }
    public int LaneId { get; init; }
    public int RoadId { get; init; }
    public int OdrLaneId { get; init; }
    public int DirectionalRoadId { get; init; }
    public bool IsForward { get; init; }
    public int SignalId { get; init; }
    public double S { get; init; }
    public double T { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
}

/// <summary>
/// 把 <see cref="Level"/> 预处理成导出所需的 road / 方向 / 车道序 / 路口分类。
/// 所有排序都按 id / rank 固定，保证同一输入导出两次逐字节一致。
/// </summary>
public sealed class ExportGraph
{
    private readonly Dictionary<int, ExportNode> _nodes;
    private readonly Dictionary<int, ExportRoad> _roads;
    private readonly Dictionary<int, ExportLane> _lanes;
    private readonly Dictionary<int, List<ExportTurn>> _turnsAtNode;
    private readonly HashSet<int> _junctionNodes;

    private ExportGraph(
        Dictionary<int, ExportNode> nodes,
        Dictionary<int, ExportRoad> roads,
        Dictionary<int, ExportLane> lanes,
        List<ExportTurn> turns,
        Dictionary<int, List<ExportTurn>> turnsAtNode,
        HashSet<int> junctionNodes,
        List<ExportSignalHead> signalHeads,
        int maxDirectionalRoadId,
        double laneWidthM)
    {
        _nodes = nodes;
        _roads = roads;
        _lanes = lanes;
        Turns = turns;
        _turnsAtNode = turnsAtNode;
        _junctionNodes = junctionNodes;
        SignalHeads = signalHeads;
        MaxDirectionalRoadId = maxDirectionalRoadId;
        LaneWidthM = laneWidthM;
        ConnectingRoadBase = maxDirectionalRoadId + 1;
    }

    public IReadOnlyDictionary<int, ExportNode> Nodes => _nodes;

    public IReadOnlyDictionary<int, ExportRoad> Roads => _roads;

    public IReadOnlyDictionary<int, ExportLane> Lanes => _lanes;

    public IReadOnlyList<ExportTurn> Turns { get; }

    public IReadOnlyList<ExportSignalHead> SignalHeads { get; }

    public IReadOnlySet<int> JunctionNodes => _junctionNodes;

    public int MaxDirectionalRoadId { get; }

    public int ConnectingRoadBase { get; }

    public double LaneWidthM { get; }

    public IReadOnlyList<ExportTurn> TurnsAt(int nodeId)
        => _turnsAtNode.TryGetValue(nodeId, out List<ExportTurn>? list) ? list : Array.Empty<ExportTurn>();

    /// <summary>某条车道在指定节点的端点坐标（起点或终点，含横向偏移）。</summary>
    public (double X, double Y) LanePointAtNode(ExportLane lane, int nodeId)
    {
        ExportRoad road = _roads[lane.RoadId];
        double nx = road.Dy;
        double ny = -road.Dx;
        double offsetX = nx * lane.LateralOffset;
        double offsetY = ny * lane.LateralOffset;

        if (lane.IsForward)
        {
            return lane.ToNode == nodeId
                ? (road.Bx + offsetX, road.By + offsetY)
                : (road.Ax + offsetX, road.Ay + offsetY);
        }

        // 反向车道：行驶方向是 B → A；它的横向偏移在参考线左侧。
        offsetX = -nx * lane.LateralOffset;
        offsetY = -ny * lane.LateralOffset;
        return lane.ToNode == nodeId
            ? (road.Ax + offsetX, road.Ay + offsetY)
            : (road.Bx + offsetX, road.By + offsetY);
    }

    public static ExportGraph Build(Level level, double laneWidthM)
    {
        if (laneWidthM <= 0.1)
        {
            throw new ArgumentOutOfRangeException(nameof(laneWidthM), "车道宽必须大于 0.1 米。");
        }

        var nodes = new Dictionary<int, ExportNode>();
        foreach (Node node in level.Network.Nodes)
        {
            nodes[node.Id] = new ExportNode
            {
                Id = node.Id,
                Type = node.Type,
                X = node.X,
                Y = node.Y,
                Offset = node.Offset,
                Plans = node.SignalPlans,
            };
        }

        var roads = new Dictionary<int, ExportRoad>();
        var lanes = new Dictionary<int, ExportLane>();

        foreach (IGrouping<int, Lane> group in level.Network.Lanes
                     .GroupBy(l => l.Road)
                     .OrderBy(g => g.Key))
        {
            var groupLanes = group.ToList();
            if (groupLanes.Count == 0)
            {
                continue;
            }

            int a;
            int b;
            if (groupLanes.All(l => l.FromNode == groupLanes[0].FromNode))
            {
                a = groupLanes[0].FromNode;
                b = groupLanes[0].ToNode;
            }
            else if (groupLanes.All(l => l.ToNode == groupLanes[0].ToNode))
            {
                a = groupLanes[0].ToNode;
                b = groupLanes[0].FromNode;
            }
            else
            {
                var endpoints = groupLanes
                    .SelectMany(l => new[] { l.FromNode, l.ToNode })
                    .Distinct()
                    .OrderBy(id => id)
                    .ToList();
                if (endpoints.Count != 2)
                {
                    continue;
                }

                a = endpoints[0];
                b = endpoints[1];
            }

            if (a == b)
            {
                continue;
            }

            if (!nodes.TryGetValue(a, out ExportNode? nodeA) || !nodes.TryGetValue(b, out ExportNode? nodeB))
            {
                continue;
            }

            double dx = nodeB.X - nodeA.X;
            double dy = nodeB.Y - nodeA.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= 1e-9)
            {
                length = groupLanes.Max(l => l.Length);
                if (length <= 1e-9)
                {
                    length = 0.1;
                    dx = length;
                    dy = 0.0;
                }
                else
                {
                    dx = length;
                    dy = 0.0;
                }
            }

            double ux = dx / length;
            double uy = dy / length;

            var road = new ExportRoad
            {
                Id = group.Key,
                ForwardRoadId = group.Key * 2,
                BackwardRoadId = group.Key * 2 + 1,
                NodeA = a,
                NodeB = b,
                Ax = nodeA.X,
                Ay = nodeA.Y,
                Bx = nodeB.X,
                By = nodeB.Y,
                Dx = ux,
                Dy = uy,
                Length = length,
                Heading = Math.Atan2(uy, ux),
            };

            var forward = groupLanes
                .Where(l => l.FromNode == a && l.ToNode == b)
                .OrderBy(EffectiveLat)
                .ThenBy(l => l.LaneIndex)
                .ThenBy(l => l.Id)
                .ToList();
            var backward = groupLanes
                .Where(l => l.FromNode == b && l.ToNode == a)
                .OrderBy(EffectiveLat)
                .ThenBy(l => l.LaneIndex)
                .ThenBy(l => l.Id)
                .ToList();

            // 兜底：方向识别失败的异常车道（坏引用）也放进 forward，避免整条 road 消失。
            foreach (Lane lane in groupLanes)
            {
                if (!forward.Any(x => x.Id == lane.Id) && !backward.Any(x => x.Id == lane.Id))
                {
                    forward.Add(lane);
                }
            }

            int rank = 0;
            foreach (Lane lane in forward)
            {
                ExportLane export = CreateLane(lane);
                export.IsForward = true;
                export.Rank = rank;
                export.OdrLaneId = -(rank + 1);
                export.DirectionalRoadId = road.ForwardRoadId;
                export.LateralOffset = (rank + 0.5) * laneWidthM;
                road.ForwardLanes.Add(export);
                road.AllLanes.Add(export);
                lanes[export.Id] = export;
                rank++;
            }

            rank = 0;
            foreach (Lane lane in backward)
            {
                ExportLane export = CreateLane(lane);
                export.IsForward = false;
                export.Rank = rank;
                // 反向车道在“反向单向 road”里也是右侧车道 → 同样用负 id。
                export.OdrLaneId = -(rank + 1);
                export.DirectionalRoadId = road.BackwardRoadId;
                export.LateralOffset = (rank + 0.5) * laneWidthM;
                road.BackwardLanes.Add(export);
                road.AllLanes.Add(export);
                lanes[export.Id] = export;
                rank++;
            }

            roads[road.Id] = road;
        }

        var degree = new Dictionary<int, HashSet<int>>();
        foreach (ExportRoad road in roads.Values)
        {
            AddDegree(degree, road.NodeA, road.Id);
            AddDegree(degree, road.NodeB, road.Id);
        }

        var turnsAtNode = new Dictionary<int, List<ExportTurn>>();
        var turns = new List<ExportTurn>();
        foreach (Turn turn in level.Network.Turns.OrderBy(t => t.Id))
        {
            if (!lanes.TryGetValue(turn.SrcLane, out ExportLane? src) ||
                !lanes.TryGetValue(turn.DstLane, out ExportLane? dst) ||
                !nodes.ContainsKey(turn.Node))
            {
                continue;
            }

            var exportTurn = new ExportTurn
            {
                Id = turn.Id,
                NodeId = turn.Node,
                Type = turn.Type,
                Leg = turn.Leg,
                IslandId = turn.IslandId,
                Src = src,
                Dst = dst,
                Ratio = level.TurnRatioOf(turn.Id),
            };
            turns.Add(exportTurn);
            if (!turnsAtNode.TryGetValue(turn.Node, out List<ExportTurn>? list))
            {
                list = new List<ExportTurn>();
                turnsAtNode[turn.Node] = list;
            }

            list.Add(exportTurn);
        }

        var junctionNodes = new HashSet<int>();
        foreach (ExportNode node in nodes.Values.OrderBy(n => n.Id))
        {
            int roadCount = degree.TryGetValue(node.Id, out HashSet<int>? set) ? set.Count : 0;
            if (roadCount == 0)
            {
                continue;
            }

            bool isJunction = node.Type == NodeType.Signalized || roadCount >= 3;
            if (!isJunction && roadCount == 2)
            {
                IReadOnlyList<ExportTurn> atNode = turnsAtNode.TryGetValue(node.Id, out List<ExportTurn>? list)
                    ? list
                    : (IReadOnlyList<ExportTurn>)Array.Empty<ExportTurn>();

                foreach (ExportTurn turn in atNode)
                {
                    if (turn.IsCrosswalk)
                    {
                        continue;
                    }

                    if (turn.Src.RoadId == turn.Dst.RoadId || turn.Type != TurnType.Straight)
                    {
                        isJunction = true;
                        break;
                    }
                }
            }

            if (isJunction)
            {
                junctionNodes.Add(node.Id);
            }
        }

        foreach (ExportRoad road in roads.Values)
        {
            road.IsJunctionA = junctionNodes.Contains(road.NodeA);
            road.IsJunctionB = junctionNodes.Contains(road.NodeB);
        }

        // 度 2 节点：把两条 road 直接接起来，并用 turn 建立车道前后继。
        foreach (int nodeId in nodes.Keys.OrderBy(id => id))
        {
            if (junctionNodes.Contains(nodeId))
            {
                continue;
            }

            if (!degree.TryGetValue(nodeId, out HashSet<int>? incident) || incident.Count != 2)
            {
                continue;
            }

            int[] roadIds = incident.OrderBy(id => id).ToArray();
            int first = roadIds[0];
            int second = roadIds[1];

            LinkRoad(roads[first], nodeId, second, roads[second]);
            LinkRoad(roads[second], nodeId, first, roads[first]);

            if (!turnsAtNode.TryGetValue(nodeId, out List<ExportTurn>? atNode))
            {
                continue;
            }

            foreach (ExportTurn turn in atNode)
            {
                if (turn.IsCrosswalk)
                {
                    continue;
                }

                turn.Src.SuccessorLaneId = turn.Dst.OdrLaneId;
                turn.Dst.PredecessorLaneId = turn.Src.OdrLaneId;
            }
        }

        var signalHeads = new List<ExportSignalHead>();
        foreach (ExportNode node in nodes.Values.OrderBy(n => n.Id))
        {
            if (node.Type != NodeType.Signalized)
            {
                continue;
            }

            var incoming = lanes.Values
                .Where(l => l.ToNode == node.Id && IsMotor(l))
                .OrderBy(l => l.RoadId)
                .ThenBy(l => l.OdrLaneId)
                .ThenBy(l => l.Id)
                .ToList();

            int ordinal = 0;
            foreach (ExportLane lane in incoming)
            {
                if (!roads.TryGetValue(lane.RoadId, out ExportRoad? road))
                {
                    continue;
                }

                // 单向 road 的进口车道总在 road 末端（s≈length），
                // 反向车道所在的 road 方向是 b→a，末端就是路口节点。
                double s = Math.Max(0.0, road.Length - 0.5);
                double t = lane.OdrLaneId * (Math.Abs(lane.OdrLaneId) - 0.5) * laneWidthM;
                double ox = lane.IsForward ? road.Ax : road.Bx;
                double oy = lane.IsForward ? road.Ay : road.By;
                double dx = lane.IsForward ? road.Dx : -road.Dx;
                double dy = lane.IsForward ? road.Dy : -road.Dy;
                double leftX = -dy;
                double leftY = dx;
                double x = ox + dx * s + leftX * t;
                double y = oy + dy * s + leftY * t;

                signalHeads.Add(new ExportSignalHead
                {
                    NodeId = node.Id,
                    LaneId = lane.Id,
                    RoadId = lane.RoadId,
                    OdrLaneId = lane.OdrLaneId,
                    DirectionalRoadId = lane.DirectionalRoadId,
                    IsForward = lane.IsForward,
                    SignalId = node.Id * 1000 + ordinal,
                    S = s,
                    T = t,
                    X = x,
                    Y = y,
                });
                ordinal++;
            }
        }

        int maxDirectionalRoadId = roads.Count == 0
            ? 0
            : roads.Values.Max(r => Math.Max(r.ForwardRoadId, r.BackwardRoadId));
        return new ExportGraph(
            nodes,
            roads,
            lanes,
            turns,
            turnsAtNode,
            junctionNodes,
            signalHeads,
            maxDirectionalRoadId,
            laneWidthM);
    }

    private static ExportLane CreateLane(Lane lane)
    {
        return new ExportLane
        {
            Id = lane.Id,
            RoadId = lane.Road,
            Type = lane.Type,
            FromNode = lane.FromNode,
            ToNode = lane.ToNode,
            Length = lane.Length,
            SpeedLimit = lane.SpeedLimit,
            LaneIndex = lane.LaneIndex,
            LatIndex = lane.LatIndex,
        };
    }

    private static int EffectiveLat(Lane lane)
        => lane.LatIndex >= 0 ? lane.LatIndex : Math.Max(0, lane.LaneIndex);

    private static bool IsMotor(ExportLane lane)
        => lane.Type is LaneType.Driving or LaneType.Bus or LaneType.SharedLeftTurn;

    private static void AddDegree(Dictionary<int, HashSet<int>> degree, int nodeId, int roadId)
    {
        if (!degree.TryGetValue(nodeId, out HashSet<int>? set))
        {
            set = new HashSet<int>();
            degree[nodeId] = set;
        }

        set.Add(roadId);
    }

    private static void LinkRoad(ExportRoad road, int nodeId, int otherRoadId, ExportRoad other)
    {
        string contactPoint = other.NodeA == nodeId ? "start" : "end";
        if (road.NodeA == nodeId)
        {
            road.LinkRoadA = otherRoadId;
            road.ContactPointA = contactPoint;
        }
        else
        {
            road.LinkRoadB = otherRoadId;
            road.ContactPointB = contactPoint;
        }
    }
}
