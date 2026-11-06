namespace SimCore.Model;

/// <summary>
/// 拓扑编译产物：把 <see cref="Network"/> 编译成仿真用的"扁平结构"。
/// </summary>
/// <remarks>
/// <para>所谓扁平：<b>一切按 id 直寻址</b>——节点 / 车道 / 转向各存为一个长度为 N 的数组，
/// 下标即 id；图邻居（每条车道的上下游车道、进出转向，每个节点的转向集合、进口 / 出口车道、
/// 交通流分组）在编译期一次性算好并排序去重，供仿真核（<c>SimCore.Sim</c>）与表现层直接只读枚举，
/// 无需再遍历原始列表、无需字典查找、且与输入顺序无关（确定性，见铁律 2）。</para>
/// <para>术语约定：
/// <list type="bullet">
///   <item>车道的"<b>下游</b>"= 本车道出发、经某条转向可达的目标车道集合；"<b>上游</b>"= 能经某条转向驶入本车道的源车道集合（基于 turns 的真实连接图）。</item>
///   <item>节点的"<b>进口车道</b>"= <c>Lane.ToNode == node</c> 的车道；"<b>出口车道</b>"= <c>Lane.FromNode == node</c> 的车道（基于几何）。</item>
///   <item>节点的"<b>转向集合</b>"= <c>Turn.Node == node</c> 的全部转向 id。</item>
/// </list>
/// </para>
/// <para>本类型只描述结构，不含任何仿真逻辑（不推进、不放行、不判冲突）。</para>
/// </remarks>
public sealed class CompiledNetwork
{
    private readonly Node[] _nodesById;
    private readonly Lane[] _lanesById;
    private readonly Turn[] _turnsById;

    // 以下均为"按 id 直寻址"的扁平邻接表（下标 = id，值 = 升序去重后的 id 列表）。
    private readonly int[][] _outgoingTurns;    // 车道 -> 以其为源的转向 id
    private readonly int[][] _incomingTurns;    // 车道 -> 以其为目标的转向 id
    private readonly int[][] _downstreamLanes;  // 车道 -> 下游车道 id
    private readonly int[][] _upstreamLanes;    // 车道 -> 上游车道 id
    private readonly int[][] _turnsAtNode;      // 节点 -> 转向 id
    private readonly int[][] _movementsAtNode;  // 节点 -> 交通流 id
    private readonly int[][] _incomingLanes;    // 节点 -> 进口车道 id（ToNode == node）
    private readonly int[][] _outgoingLanes;    // 节点 -> 出口车道 id（FromNode == node）

    private CompiledNetwork(
        Node[] nodesById,
        Lane[] lanesById,
        Turn[] turnsById,
        Movement[] movements,
        Island[] islandsById,
        int[][] outgoingTurns,
        int[][] incomingTurns,
        int[][] downstreamLanes,
        int[][] upstreamLanes,
        int[][] turnsAtNode,
        int[][] movementsAtNode,
        int[][] incomingLanes,
        int[][] outgoingLanes)
    {
        _nodesById = nodesById;
        _lanesById = lanesById;
        _turnsById = turnsById;
        _outgoingTurns = outgoingTurns;
        _incomingTurns = incomingTurns;
        _downstreamLanes = downstreamLanes;
        _upstreamLanes = upstreamLanes;
        _turnsAtNode = turnsAtNode;
        _movementsAtNode = movementsAtNode;
        _incomingLanes = incomingLanes;
        _outgoingLanes = outgoingLanes;

        Nodes = nodesById;
        Lanes = lanesById;
        Turns = turnsById;
        Movements = movements;
        Islands = islandsById;
    }

    /// <summary>节点列表（下标 = 节点 id）。</summary>
    public IReadOnlyList<Node> Nodes { get; }

    /// <summary>车道列表（下标 = 车道 id）。</summary>
    public IReadOnlyList<Lane> Lanes { get; }

    /// <summary>转向列表（下标 = 转向 id）。</summary>
    public IReadOnlyList<Turn> Turns { get; }

    /// <summary>交通流分组列表（下标 = 交通流 id）。</summary>
    public IReadOnlyList<Movement> Movements { get; }

    /// <summary>
    /// 安全岛列表（下标 = 数组位置；<see cref="Island.Id"/> 索引见 <see cref="GetIsland"/>，2026-10-24 E92 G2）。
    /// </summary>
    /// <remarks>
    /// 岛 id 在 <c>islands[]</c> 里只要求唯一、不要求连续（校验器另有限制），因此这里<b>按 id 升序</b>
    /// 排成扁平数组，另用 <see cref="TryGetIsland"/> 做二分查表 —— 保证"不依赖输入顺序"的确定性口径。
    /// 旧关卡没有岛 → 空数组，行为与改动前逐位一致。
    /// </remarks>
    public IReadOnlyList<Island> Islands { get; }

    /// <summary>节点数量。</summary>
    public int NodeCount => _nodesById.Length;

    /// <summary>车道数量。</summary>
    public int LaneCount => _lanesById.Length;

    /// <summary>转向数量。</summary>
    public int TurnCount => _turnsById.Length;

    /// <summary>交通流数量。</summary>
    public int MovementCount => Movements.Count;

    /// <summary>
    /// 把路网编译为扁平结构。
    /// </summary>
    /// <param name="network">待编译路网。</param>
    /// <returns>不可变的编译产物。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="network"/> 为 null。</exception>
    /// <exception cref="ArgumentException">节点 / 车道 / 转向的 id 非 0..N-1 连续或存在重复。</exception>
    /// <exception cref="ArgumentOutOfRangeException">转向 / 车道引用了越界的节点或车道 id。</exception>
    public static CompiledNetwork Compile(Network network)
    {
        ArgumentNullException.ThrowIfNull(network);

        IReadOnlyList<Node> nodes = network.Nodes;
        IReadOnlyList<Lane> lanes = network.Lanes;
        IReadOnlyList<Turn> turns = network.Turns;

        Node[] nodesById = IndexById(nodes, static n => n.Id, "节点");
        Lane[] lanesById = IndexById(lanes, static l => l.Id, "车道");
        Turn[] turnsById = IndexById(turns, static t => t.Id, "转向");

        int nodeCount = nodesById.Length;
        int laneCount = lanesById.Length;

        // 1) 节点 <-> 车道 的几何邻接（进口 / 出口车道）。
        List<int>[] incomingLanes = NewListArray(nodeCount);
        List<int>[] outgoingLanes = NewListArray(nodeCount);
        for (int i = 0; i < lanes.Count; i++)
        {
            Lane lane = lanes[i];
            EnsureNodeRef(lane.FromNode, nodeCount, "车道", lane.Id, "from_node");
            EnsureNodeRef(lane.ToNode, nodeCount, "车道", lane.Id, "to_node");
            outgoingLanes[lane.FromNode].Add(lane.Id);
            incomingLanes[lane.ToNode].Add(lane.Id);
        }

        // 2) 转向图：车道上游 / 下游、节点转向集合。
        List<int>[] outgoingTurns = NewListArray(laneCount);
        List<int>[] incomingTurns = NewListArray(laneCount);
        List<int>[] downstreamLanes = NewListArray(laneCount);
        List<int>[] upstreamLanes = NewListArray(laneCount);
        List<int>[] turnsAtNode = NewListArray(nodeCount);
        for (int i = 0; i < turns.Count; i++)
        {
            Turn turn = turns[i];
            EnsureLaneRef(turn.SrcLane, laneCount, turn.Id, "src_lane");
            EnsureLaneRef(turn.DstLane, laneCount, turn.Id, "dst_lane");
            EnsureNodeRef(turn.Node, nodeCount, "转向", turn.Id, "node");

            outgoingTurns[turn.SrcLane].Add(turn.Id);
            incomingTurns[turn.DstLane].Add(turn.Id);
            downstreamLanes[turn.SrcLane].Add(turn.DstLane);
            upstreamLanes[turn.DstLane].Add(turn.SrcLane);
            turnsAtNode[turn.Node].Add(turn.Id);
        }

        // 3) 交通流分组：(节点, 进口道路, 转向类型)。SortedDictionary 保证 id 分配与输入顺序无关。
        var grouped = new SortedDictionary<(int Node, int Road, TurnType Type), List<int>>();
        for (int i = 0; i < turns.Count; i++)
        {
            Turn turn = turns[i];
            int road = lanesById[turn.SrcLane].Road;
            (int Node, int Road, TurnType Type) key = (turn.Node, road, turn.Type);
            if (!grouped.TryGetValue(key, out List<int>? bucket))
            {
                bucket = new List<int>();
                grouped.Add(key, bucket);
            }

            bucket.Add(turn.Id);
        }

        Movement[] movements = new Movement[grouped.Count];
        List<int>[] movementsAtNode = NewListArray(nodeCount);
        int movementIndex = 0;
        foreach (KeyValuePair<(int Node, int Road, TurnType Type), List<int>> entry in grouped)
        {
            (int node, int road, TurnType type) = entry.Key;
            movements[movementIndex] = new Movement(movementIndex, node, road, type, DedupSort(entry.Value));
            movementsAtNode[node].Add(movementIndex);
            movementIndex++;
        }

        // 4) 安全岛：按 id 升序排成扁平数组（岛不参与拓扑邻接，只被行人二次过街查容量）。
        Island[] islandsById = SortIslandsById(network.Islands);

        return new CompiledNetwork(
            nodesById,
            lanesById,
            turnsById,
            movements,
            islandsById,
            ToFlat(outgoingTurns),
            ToFlat(incomingTurns),
            ToFlat(downstreamLanes),
            ToFlat(upstreamLanes),
            ToFlat(turnsAtNode),
            ToFlat(movementsAtNode),
            ToFlat(incomingLanes),
            ToFlat(outgoingLanes));
    }

    /// <summary>按 id 取节点。</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="nodeId"/> 越界。</exception>
    public Node GetNode(int nodeId)
    {
        if ((uint)nodeId >= (uint)_nodesById.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeId), nodeId, "节点 id 越界。");
        }

        return _nodesById[nodeId];
    }

    /// <summary>按 id 取车道。</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="laneId"/> 越界。</exception>
    public Lane GetLane(int laneId)
    {
        if ((uint)laneId >= (uint)_lanesById.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(laneId), laneId, "车道 id 越界。");
        }

        return _lanesById[laneId];
    }

    /// <summary>按 id 取转向。</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="turnId"/> 越界。</exception>
    public Turn GetTurn(int turnId)
    {
        if ((uint)turnId >= (uint)_turnsById.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(turnId), turnId, "转向 id 越界。");
        }

        return _turnsById[turnId];
    }

    /// <summary>以 <paramref name="laneId"/> 为源车道的转向 id 集合（升序）。</summary>
    public IReadOnlyList<int> OutgoingTurns(int laneId) => _outgoingTurns[CheckLane(laneId)];

    /// <summary>以 <paramref name="laneId"/> 为目标车道的转向 id 集合（升序）。</summary>
    public IReadOnlyList<int> IncomingTurns(int laneId) => _incomingTurns[CheckLane(laneId)];

    /// <summary><paramref name="laneId"/> 的下游车道 id 集合（经转向可达，升序去重）。</summary>
    public IReadOnlyList<int> DownstreamLanes(int laneId) => _downstreamLanes[CheckLane(laneId)];

    /// <summary><paramref name="laneId"/> 的上游车道 id 集合（可经转向驶入本车道，升序去重）。</summary>
    public IReadOnlyList<int> UpstreamLanes(int laneId) => _upstreamLanes[CheckLane(laneId)];

    /// <summary>节点 <paramref name="nodeId"/> 的转向 id 集合（升序）。</summary>
    public IReadOnlyList<int> TurnsAtNode(int nodeId) => _turnsAtNode[CheckNode(nodeId)];

    /// <summary>节点 <paramref name="nodeId"/> 的交通流 id 集合（升序）。</summary>
    public IReadOnlyList<int> MovementsAtNode(int nodeId) => _movementsAtNode[CheckNode(nodeId)];

    /// <summary>节点 <paramref name="nodeId"/> 的进口车道 id 集合（<c>Lane.ToNode == node</c>，升序）。</summary>
    public IReadOnlyList<int> IncomingLanes(int nodeId) => _incomingLanes[CheckNode(nodeId)];

    /// <summary>节点 <paramref name="nodeId"/> 的出口车道 id 集合（<c>Lane.FromNode == node</c>，升序）。</summary>
    public IReadOnlyList<int> OutgoingLanes(int nodeId) => _outgoingLanes[CheckNode(nodeId)];

    /// <summary>按 id 取安全岛（岛 id 唯一但不一定连续，故走二分查表）。</summary>
    /// <returns>找到为 <c>true</c>。</returns>
    public bool TryGetIsland(int islandId, out Island? island)
    {
        int lo = 0;
        int hi = Islands.Count - 1;
        while (lo <= hi)
        {
            int mid = lo + ((hi - lo) / 2);
            int id = Islands[mid].Id;
            if (id == islandId)
            {
                island = Islands[mid];
                return true;
            }

            if (id < islandId)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        island = null;
        return false;
    }

    private int CheckLane(int laneId)
    {
        if ((uint)laneId >= (uint)_lanesById.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(laneId), laneId, "车道 id 越界。");
        }

        return laneId;
    }

    private int CheckNode(int nodeId)
    {
        if ((uint)nodeId >= (uint)_nodesById.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeId), nodeId, "节点 id 越界。");
        }

        return nodeId;
    }

    private static T[] IndexById<T>(IReadOnlyList<T> items, Func<T, int> idOf, string what)
        where T : class
    {
        var byId = new T[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            T item = items[i];
            int id = idOf(item);
            if ((uint)id >= (uint)byId.Length)
            {
                throw new ArgumentException(
                    $"{what} id={id} 非法：id 必须是 0..{byId.Length - 1} 的连续整数（不重叠、不跳号）。");
            }

            if (byId[id] is not null)
            {
                throw new ArgumentException($"{what} id={id} 重复。");
            }

            byId[id] = item;
        }

        return byId;
    }

    private static List<int>[] NewListArray(int length)
    {
        var lists = new List<int>[length];
        for (int i = 0; i < length; i++)
        {
            lists[i] = new List<int>();
        }

        return lists;
    }

    private static int[][] ToFlat(List<int>[] lists)
    {
        var flat = new int[lists.Length][];
        for (int i = 0; i < lists.Length; i++)
        {
            flat[i] = DedupSort(lists[i]);
        }

        return flat;
    }

    /// <summary>就地升序排序并去重，返回新数组（空集合返回共享的空数组）。</summary>
    private static int[] DedupSort(List<int> values)
    {
        if (values.Count == 0)
        {
            return Array.Empty<int>();
        }

        values.Sort();
        int write = 0;
        for (int read = 1; read < values.Count; read++)
        {
            if (values[read] != values[write])
            {
                write++;
                values[write] = values[read];
            }
        }

        int length = write + 1;
        var result = new int[length];
        values.CopyTo(0, result, 0, length);
        return result;
    }

    /// <summary>
    /// 把 <c>islands[]</c> 按 <see cref="Island.Id"/> 升序排成扁平数组（供二分查表）。
    /// </summary>
    /// <remarks>
    /// 岛不参与任何邻接，故不做"id 必须连续"的强校验（连续性 / 唯一性属 <c>SpmapValidator</c> 职责）；
    /// 这里只把顺序固定下来，保证编译产物与输入顺序无关（确定性，铁律 2）。
    /// </remarks>
    private static Island[] SortIslandsById(IReadOnlyList<Island> islands)
    {
        if (islands.Count == 0)
        {
            return Array.Empty<Island>();
        }

        var sorted = new Island[islands.Count];
        for (int i = 0; i < islands.Count; i++)
        {
            sorted[i] = islands[i];
        }

        Array.Sort(sorted, static (a, b) => a.Id.CompareTo(b.Id));
        return sorted;
    }

    private static void EnsureNodeRef(int id, int nodeCount, string owner, int ownerId, string field)
    {
        if ((uint)id >= (uint)nodeCount)
        {
            throw new ArgumentOutOfRangeException(
                field, id, $"{owner} id={ownerId} 的 {field}={id} 越界（节点数={nodeCount}）。");
        }
    }

    private static void EnsureLaneRef(int id, int laneCount, int turnId, string field)
    {
        if ((uint)id >= (uint)laneCount)
        {
            throw new ArgumentOutOfRangeException(
                field, id, $"转向 id={turnId} 的 {field}={id} 越界（车道数={laneCount}）。");
        }
    }
}
