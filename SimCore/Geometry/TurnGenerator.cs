using SimCore.Model;

namespace SimCore.Geometry;

// ---------------------------------------------------------------------------
// T4.2(a) · 转向自动生成（技术方案03 §5 · E-C1；docs/SimCore-API.md §3.2 冻结）
//
// 本文件落地 docs/SimCore-API.md §3.2 冻结的 TurnGenerator.FromGeometry(Network)。
// 借鉴 A/B Street map_model/src/make/turns.rs（技术方案02 §2.4）：从车道几何自动生成
// 全部合法转向，并用"首段方向与末段方向的夹角"判定 Straight / Left / Right / UTurn；
// 人行道之间的连接另生成 Crosswalk（供行人需求引用）。
//
// 设计口径：
//   * 进口车道（Lane.ToNode == node）取机动车可通行类型 {Driving, Bus, Biking, SharedLeftTurn}；
//     出口车道（Lane.FromNode == node）取同一"可通行类型"集合（Construction=施工封道、
//     Parking=路边停车、Sidewalk=人行道 均不可供机动车通行）。
//   * 机动车转向：进口 × 出口，按两段行驶方向的有符号夹角分类（见 Classify）。
//   * 人行横道转向：Sidewalk 进口 × Sidewalk 出口，一律 Crosswalk（绝不并入 Straight/Left/Right）。
//   * id：返回数组依序编号 0..N-1 连续唯一；不负责与既有 turns 合并（基址与去重由调用方）。
//
// 铁律：只依赖 SimCore.Model、无 Godot 命名空间、无第三方依赖、无系统随机数；
//       同一 network 两次调用结果逐项相等（遍历顺序固定，字典仅用于查找不用于遍历）。
// ---------------------------------------------------------------------------

/// <summary>
/// 转向自动生成器（T4.2(a)；<c>docs/SimCore-API.md</c> §3.2 冻结）。
/// </summary>
/// <remarks>
/// <para>职责：给定路网几何（<see cref="Network"/> 的节点坐标 + 车道有向连接），自动推导该路网
/// <b>全部转向</b>并判定类型——对应技术方案02 §2.4「画完车道，转向与转向类型自动出现，玩家只需配相位」。</para>
///
/// <para><b>坐标系与左右判据</b>：节点坐标 <c>X</c> 向东、<c>Y</c> 向北（Y 轴向上），与
/// <c>ConflictMatrix</c> 的右法向 <c>Right(v) = (v.Y, −v.X)</c> 同一约定。设源车道行驶方向单位向量为
/// <c>u_src</c>、目标车道行驶方向为 <c>u_dst</c>，有符号转向角
/// <c>θ = atan2(u_src × u_dst, u_src · u_dst)</c>（逆时针为正）：
/// <list type="bullet">
///   <item><c>|θ| ≤ 45°</c> → <see cref="TurnType.Straight"/></item>
///   <item><c>θ &gt; 45°</c> 且 <c>&lt; 135°</c> → <see cref="TurnType.Left"/>（逆时针，驾驶员左手侧）</item>
///   <item><c>θ &lt; −45°</c> 且 <c>&gt; −135°</c> → <see cref="TurnType.Right"/></item>
///   <item><c>|θ| ≥ 135°</c> → <see cref="TurnType.UTurn"/></item>
/// </list>
/// 阈值取<b>固定常量</b> <see cref="StraightMaxTurnDegrees"/> / <see cref="UTurnMinTurnDegrees"/>，
/// 不读取环境、不引入随机（铁律 2）。</para>
///
/// <para><b>输出约定</b>（§3.2）：返回 <see cref="Turn"/> 数组，<see cref="Turn.Id"/> 依序 0..N-1；
/// 顺序由"节点 id 升序 → 进口车道 id 升序 → 出口车道 id 升序（机动车转向先于人行横道）"唯一确定，
/// 与输入列表顺序无关，故同一路网两次调用逐项相等。<b>本类型不与既有 turns 合并</b>——
/// id 基址与去重由调用方负责。</para>
/// </remarks>
public static class TurnGenerator
{
    /// <summary>
    /// 判为"直行"的转向角上界（度）：<c>|θ| ≤ 该值 → Straight</c>。
    /// 固定常量，不读环境、不随机（本任务追加约束）。
    /// </summary>
    internal const double StraightMaxTurnDegrees = 45.0d;

    /// <summary>
    /// 判为"掉头"的转向角下界（度）：<c>|θ| ≥ 该值 → UTurn</c>。
    /// 固定常量，不读环境、不随机（本任务追加约束）。
    /// </summary>
    internal const double UTurnMinTurnDegrees = 135.0d;

    /// <summary>几何退化判定容差（用于零长度车道：方向不可解析时该车道不参与转向生成）。</summary>
    private const double GeometryEpsilon = 1e-9d;

    private const double RadiansToDegrees = 180.0d / Math.PI;

    /// <summary>
    /// 由路网几何自动推导全部转向并判定类型（§3.2 字面）。
    /// </summary>
    /// <param name="network">路网（只读，不会被修改）。</param>
    /// <returns>
    /// 全部转向，<see cref="Turn.Id"/> 为 0..N-1 连续且唯一；顺序固定、可复现（确定性）。
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="network"/> 为 null。</exception>
    /// <exception cref="ArgumentException">某条车道的 <c>from_node</c> / <c>to_node</c> 引用了不存在的节点。</exception>
    public static Turn[] FromGeometry(Network network)
    {
        ArgumentNullException.ThrowIfNull(network);

        // 1) 节点索引：仅用于按 id 查坐标，绝不遍历（避免依赖字典遍历顺序，铁律 2）。
        var nodesById = new Dictionary<int, Node>(network.Nodes.Count);
        foreach (Node node in network.Nodes)
        {
            nodesById[node.Id] = node;   // 同 id 后者覆盖；重复 id 属非法输入，由 SpmapValidator 另行报错
        }

        // 2) 车道按 id 升序（与输入列表顺序解耦，保证确定性）。
        Lane[] lanes = SortedById(network.Lanes, static lane => lane.Id);

        // 3) 每条车道的行驶方向（FromNode → ToNode 的单位向量）；端点缺失 → 抛异常（§1.3）。
        var directionByLane = new Dictionary<int, (double X, double Y)>(lanes.Length);
        foreach (Lane lane in lanes)
        {
            Node from = RequireNode(nodesById, lane.FromNode, lane, "from_node");
            Node to = RequireNode(nodesById, lane.ToNode, lane, "to_node");
            directionByLane[lane.Id] = UnitDirection(from, to);
        }

        // 4) 把车道分桶到"所在节点"的进口 / 出口集合（车道已按 id 升序，故每个桶天然升序）。
        var vehicleIn = new Dictionary<int, List<Lane>>();     // ToNode == node  且 机动车可通行
        var vehicleOut = new Dictionary<int, List<Lane>>();    // FromNode == node 且 机动车可通行
        var sidewalkIn = new Dictionary<int, List<Lane>>();    // ToNode == node  且 Sidewalk
        var sidewalkOut = new Dictionary<int, List<Lane>>();   // FromNode == node 且 Sidewalk

        foreach (Lane lane in lanes)
        {
            (double ux, double uy) = directionByLane[lane.Id];
            if (ux == 0d && uy == 0d)
            {
                continue;   // 退化几何（零长度 / 自环）：方向不可解析，该车道不参与转向生成
            }

            if (IsVehicleLane(lane.Type))
            {
                AddToBucket(vehicleIn, lane.ToNode, lane);
                AddToBucket(vehicleOut, lane.FromNode, lane);
            }
            else if (lane.Type == LaneType.Sidewalk)
            {
                AddToBucket(sidewalkIn, lane.ToNode, lane);
                AddToBucket(sidewalkOut, lane.FromNode, lane);
            }
        }

        // 5) 逐节点生成转向：节点 id 升序 → 进口车道 id 升序 → 出口车道 id 升序。
        Node[] nodes = SortedById(network.Nodes, static node => node.Id);
        var turns = new List<Turn>();

        foreach (Node node in nodes)
        {
            int nodeId = node.Id;

            // 5a) 机动车转向：进口（可通行）× 出口（可通行），按夹角分型。
            if (vehicleIn.TryGetValue(nodeId, out List<Lane>? approaches) &&
                vehicleOut.TryGetValue(nodeId, out List<Lane>? exits))
            {
                foreach (Lane src in approaches)
                {
                    foreach (Lane dst in exits)
                    {
                        TurnType type = ClassifyVehicleTurn(
                            directionByLane[src.Id], directionByLane[dst.Id]);
                        turns.Add(new Turn
                        {
                            Id = turns.Count,
                            SrcLane = src.Id,
                            DstLane = dst.Id,
                            Node = nodeId,
                            Type = type,
                        });
                    }
                }
            }

            // 5b) 人行横道转向：人行道进口 × 人行道出口，一律 Crosswalk。
            if (sidewalkIn.TryGetValue(nodeId, out List<Lane>? walkIn) &&
                sidewalkOut.TryGetValue(nodeId, out List<Lane>? walkOut))
            {
                foreach (Lane src in walkIn)
                {
                    foreach (Lane dst in walkOut)
                    {
                        turns.Add(new Turn
                        {
                            Id = turns.Count,
                            SrcLane = src.Id,
                            DstLane = dst.Id,
                            Node = nodeId,
                            Type = TurnType.Crosswalk,
                        });
                    }
                }
            }
        }

        return turns.ToArray();
    }

    // ------------------------------------------------------------------
    // 内部：分类与几何
    // ------------------------------------------------------------------

    /// <summary>
    /// 依据源 / 目标车道行驶方向的有符号夹角判定机动车转向类型（见类型注释的左右判据）。
    /// </summary>
    private static TurnType ClassifyVehicleTurn(
        (double X, double Y) source, (double X, double Y) destination)
    {
        double cross = (source.X * destination.Y) - (source.Y * destination.X);
        double dot = (source.X * destination.X) + (source.Y * destination.Y);
        double turnDegrees = Math.Atan2(cross, dot) * RadiansToDegrees;   // (−180, 180]

        double magnitude = Math.Abs(turnDegrees);
        if (magnitude <= StraightMaxTurnDegrees)
        {
            return TurnType.Straight;
        }

        if (magnitude >= UTurnMinTurnDegrees)
        {
            return TurnType.UTurn;
        }

        // 逆时针为正 → 左手侧（左转）；否则右转。
        return turnDegrees > 0d ? TurnType.Left : TurnType.Right;
    }

    /// <summary>
    /// 机动车可通行的车道类型集合（进口 / 出口共用）：
    /// <see cref="LaneType.Driving"/> / <see cref="LaneType.Bus"/> / <see cref="LaneType.Biking"/> /
    /// <see cref="LaneType.SharedLeftTurn"/>。施工封道（<see cref="LaneType.Construction"/>）、
    /// 路边停车（<see cref="LaneType.Parking"/>）、人行道（<see cref="LaneType.Sidewalk"/>）不可通行。
    /// </summary>
    private static bool IsVehicleLane(LaneType type) => type switch
    {
        LaneType.Driving or LaneType.Bus or LaneType.Biking or LaneType.SharedLeftTurn => true,
        _ => false,
    };

    /// <summary>两节点间的单位方向向量；两端重合（长度≈0）时返回零向量。</summary>
    private static (double X, double Y) UnitDirection(Node from, Node to)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));

        return length < GeometryEpsilon ? (0d, 0d) : (dx / length, dy / length);
    }

    /// <summary>按 id 查节点；不存在则抛 <see cref="ArgumentException"/>（不静默吞掉越界引用，§1.3）。</summary>
    private static Node RequireNode(
        Dictionary<int, Node> nodesById, int nodeId, Lane lane, string field)
    {
        if (!nodesById.TryGetValue(nodeId, out Node? node))
        {
            throw new ArgumentException(
                $"车道 id={lane.Id} 的 {field}={nodeId} 引用了不存在的节点。", nameof(lane));
        }

        return node;
    }

    /// <summary>把车道加入以 <paramref name="key"/>（节点 id）为键的桶；桶不存在则新建。</summary>
    private static void AddToBucket(Dictionary<int, List<Lane>> buckets, int key, Lane lane)
    {
        if (!buckets.TryGetValue(key, out List<Lane>? list))
        {
            list = new List<Lane>();
            buckets.Add(key, list);
        }

        list.Add(lane);
    }

    /// <summary>把只读列表拷贝为数组并按 id 升序排序（稳定确定：仅依赖元素 id 值）。</summary>
    private static T[] SortedById<T>(IReadOnlyList<T> items, Func<T, int> idOf)
    {
        var array = new T[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            array[i] = items[i];
        }

        Array.Sort(array, (a, b) => idOf(a).CompareTo(idOf(b)));
        return array;
    }
}
