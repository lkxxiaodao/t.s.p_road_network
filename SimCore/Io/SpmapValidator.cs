using SimCore.Model;

namespace SimCore.Io;

/// <summary>
/// <c>.spmap</c> 合法性校验器（技术方案03 §3.1：<c>SpmapValidator.Validate(level) -&gt; ValidationError[]</c>）。
/// </summary>
/// <remarks>
/// <para>规则借 LightSim <c>Network.validate()</c>（技术方案01 §3.6：端点引用不存在 / 孤儿节点 / 转向比例越界 /
/// 信号路口无相位 / 相位引用不存在的转向），并补齐本 schema 特有的引用完整性（车道→节点、需求→车道、
/// 行人→人行横道转向、非机动车→非机动车道）与物理量非负检查。</para>
/// <para>游戏与编辑器<b>共享同一份校验</b>（游戏设计v2 §13.2/§13.3）：运行时开局前校验、编辑器边画边校验、导出前拦截，
/// 均为同一实现、零重复。校验只读 <see cref="Level"/>，不修改任何对象。</para>
/// <para>返回的每条 <see cref="ValidationError"/> 都带稳定 <see cref="ValidationError.Code"/> 与 JSON 位置
/// <see cref="ValidationError.Path"/>（见 <c>docs/spmap-schema.md</c> 校验码表）。输出顺序固定（按表遍历），保证可复现。</para>
/// </remarks>
public static class SpmapValidator
{
    /// <summary>校验一张关卡，返回全部错误与警告（空数组 = 合法）。</summary>
    /// <param name="level">待校验关卡。</param>
    /// <returns>校验结论数组；顺序固定，可复现。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="level"/> 为 null。</exception>
    public static ValidationError[] Validate(Level level)
    {
        ArgumentNullException.ThrowIfNull(level);

        var errors = new List<ValidationError>();

        ValidateFormatVersion(level, errors);

        Network network = level.Network;
        ValidateIds(network.Nodes, static n => n.Id, "nodes", errors);
        ValidateIds(network.Lanes, static l => l.Id, "lanes", errors);
        ValidateIds(network.Turns, static t => t.Id, "turns", errors);
        ValidateIds(network.Islands, static i => i.Id, "islands", errors);

        var nodeIds = ToIdSet(network.Nodes, static n => n.Id);
        var laneIds = ToIdSet(network.Lanes, static l => l.Id);
        var turnIds = ToIdSet(network.Turns, static t => t.Id);

        ValidateLanes(network, nodeIds, errors);
        ValidateTurns(network, laneIds, nodeIds, errors);
        ValidateTurnRatios(level, turnIds, errors);
        ValidateOrphanNodes(network, errors);
        ValidateSignals(network, turnIds, errors);
        ValidateDemand(level, laneIds, errors);
        ValidateIslandsAndLegs(level, network, errors);
        ValidatePedestrians(level, network, turnIds, errors);
        ValidateBicycles(level, network, laneIds, errors);
        ValidateGameRules(level, errors);

        return errors.ToArray();
    }

    // ------------------------------------------------------------------
    // format_version
    // ------------------------------------------------------------------

    private static void ValidateFormatVersion(Level level, List<ValidationError> errors)
    {
        if (level.FormatVersion != Spmap.CurrentFormatVersion)
        {
            errors.Add(new ValidationError(
                "format_version_unsupported",
                $"format_version={level.FormatVersion} 不受支持，当前仅支持 {Spmap.CurrentFormatVersion}。",
                "format_version"));
        }
    }

    // ------------------------------------------------------------------
    // id 连续且唯一（0..N-1）
    // ------------------------------------------------------------------

    private static void ValidateIds<T>(IReadOnlyList<T> items, Func<T, int> idOf, string table, List<ValidationError> errors)
    {
        int count = items.Count;
        var seen = new bool[count];
        for (int i = 0; i < count; i++)
        {
            int id = idOf(items[i]);
            if ((uint)id >= (uint)count)
            {
                errors.Add(new ValidationError(
                    "id_not_contiguous",
                    $"{table}[{i}] 的 id={id} 越界：id 必须是 0..{count - 1} 的连续整数（不跳号）。",
                    $"{table}[{i}].id"));
                continue;
            }

            if (seen[id])
            {
                errors.Add(new ValidationError(
                    "duplicate_id",
                    $"{table}[{i}] 的 id={id} 与前面的条目重复。",
                    $"{table}[{i}].id"));
                continue;
            }

            seen[id] = true;
        }

        for (int id = 0; id < count; id++)
        {
            if (!seen[id])
            {
                errors.Add(new ValidationError(
                    "id_not_contiguous",
                    $"{table} 缺少 id={id}：id 必须是 0..{count - 1} 的连续整数（不跳号）。",
                    table));
            }
        }
    }

    private static HashSet<int> ToIdSet<T>(IReadOnlyList<T> items, Func<T, int> idOf)
    {
        var set = new HashSet<int>();
        for (int i = 0; i < items.Count; i++)
        {
            set.Add(idOf(items[i]));
        }

        return set;
    }

    // ------------------------------------------------------------------
    // lanes
    // ------------------------------------------------------------------

    private static void ValidateLanes(Network network, HashSet<int> nodeIds, List<ValidationError> errors)
    {
        IReadOnlyList<Lane> lanes = network.Lanes;
        for (int i = 0; i < lanes.Count; i++)
        {
            Lane lane = lanes[i];
            if (!nodeIds.Contains(lane.FromNode))
            {
                errors.Add(new ValidationError(
                    "dangling_reference",
                    $"车道 id={lane.Id} 的 from_node={lane.FromNode} 不存在。",
                    $"lanes[{i}].from_node"));
            }

            if (!nodeIds.Contains(lane.ToNode))
            {
                errors.Add(new ValidationError(
                    "dangling_reference",
                    $"车道 id={lane.Id} 的 to_node={lane.ToNode} 不存在。",
                    $"lanes[{i}].to_node"));
            }

            if (lane.Length <= 0d)
            {
                errors.Add(new ValidationError(
                    "invalid_lane_geometry",
                    $"车道 id={lane.Id} 的 length={lane.Length} 必须为正（单位：米）。",
                    $"lanes[{i}].length"));
            }

            if (lane.SpeedLimit <= 0d)
            {
                errors.Add(new ValidationError(
                    "invalid_lane_geometry",
                    $"车道 id={lane.Id} 的 speed_limit={lane.SpeedLimit} 必须为正（单位：米/秒）。",
                    $"lanes[{i}].speed_limit",
                    ValidationSeverity.Warning));
            }
        }
    }

    // ------------------------------------------------------------------
    // turns
    // ------------------------------------------------------------------

    private static void ValidateTurns(Network network, HashSet<int> laneIds, HashSet<int> nodeIds, List<ValidationError> errors)
    {
        // 按 id 取车道（列表顺序不保证等于 id 顺序，故不能按位置索引）。
        var laneById = new Dictionary<int, Lane>();
        foreach (Lane lane in network.Lanes)
        {
            laneById[lane.Id] = lane;
        }

        IReadOnlyList<Turn> turns = network.Turns;
        for (int i = 0; i < turns.Count; i++)
        {
            Turn turn = turns[i];

            bool srcOk = laneIds.Contains(turn.SrcLane);
            bool dstOk = laneIds.Contains(turn.DstLane);
            bool nodeOk = nodeIds.Contains(turn.Node);

            if (!srcOk)
            {
                errors.Add(new ValidationError(
                    "dangling_reference",
                    $"转向 id={turn.Id} 的 src_lane={turn.SrcLane} 不存在。",
                    $"turns[{i}].src_lane"));
            }

            if (!dstOk)
            {
                errors.Add(new ValidationError(
                    "dangling_reference",
                    $"转向 id={turn.Id} 的 dst_lane={turn.DstLane} 不存在。",
                    $"turns[{i}].dst_lane"));
            }

            if (!nodeOk)
            {
                errors.Add(new ValidationError(
                    "dangling_reference",
                    $"转向 id={turn.Id} 的 node={turn.Node} 不存在。",
                    $"turns[{i}].node"));
            }

            // 几何语义：进口车道须终止于此节点、出口车道须起始于此节点。
            if (srcOk && nodeOk && laneById[turn.SrcLane].ToNode != turn.Node)
            {
                errors.Add(new ValidationError(
                    "turn_node_mismatch",
                    $"转向 id={turn.Id} 的 src_lane={turn.SrcLane} 终止于节点 {laneById[turn.SrcLane].ToNode}，与 node={turn.Node} 不符。",
                    $"turns[{i}].src_lane"));
            }

            if (dstOk && nodeOk && laneById[turn.DstLane].FromNode != turn.Node)
            {
                errors.Add(new ValidationError(
                    "turn_node_mismatch",
                    $"转向 id={turn.Id} 的 dst_lane={turn.DstLane} 起始于节点 {laneById[turn.DstLane].FromNode}，与 node={turn.Node} 不符。",
                    $"turns[{i}].dst_lane"));
            }
        }
    }

    // ------------------------------------------------------------------
    // 转向比例（借 LightSim turn_ratio ∈ (0,1]）
    // ------------------------------------------------------------------

    private static void ValidateTurnRatios(Level level, HashSet<int> turnIds, List<ValidationError> errors)
    {
        IReadOnlyList<TurnRatio> ratios = level.TurnRatios;
        for (int i = 0; i < ratios.Count; i++)
        {
            TurnRatio entry = ratios[i];

            if (!turnIds.Contains(entry.Turn))
            {
                errors.Add(new ValidationError(
                    "dangling_reference",
                    $"转向比例引用的转向 id={entry.Turn} 不存在。",
                    $"turns[ratio:{entry.Turn}]"));
            }

            if (entry.Ratio <= 0d || entry.Ratio > 1d)
            {
                errors.Add(new ValidationError(
                    "turn_ratio_out_of_range",
                    $"转向 id={entry.Turn} 的比例 ratio={entry.Ratio} 必须落在 (0, 1]。",
                    $"turns[ratio:{entry.Turn}]"));
            }
        }
    }

    // ------------------------------------------------------------------
    // 孤儿节点
    // ------------------------------------------------------------------

    private static void ValidateOrphanNodes(Network network, List<ValidationError> errors)
    {
        var connected = new HashSet<int>();
        foreach (Lane lane in network.Lanes)
        {
            connected.Add(lane.FromNode);
            connected.Add(lane.ToNode);
        }

        IReadOnlyList<Node> nodes = network.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (!connected.Contains(nodes[i].Id))
            {
                errors.Add(new ValidationError(
                    "orphan_node",
                    $"节点 id={nodes[i].Id} 是孤儿节点：未连接任何车道。",
                    $"nodes[{i}]"));
            }
        }
    }

    // ------------------------------------------------------------------
    // 信号
    // ------------------------------------------------------------------

    private static void ValidateSignals(Network network, HashSet<int> turnIds, List<ValidationError> errors)
    {
        IReadOnlyList<Node> nodes = network.Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            Node node = nodes[i];

            if (!node.IsSignalized)
            {
                if (node.Offset != 0d || node.SignalPlans.Count > 0)
                {
                    errors.Add(new ValidationError(
                        "signal_data_on_non_signal_node",
                        $"节点 id={node.Id} 非信号路口（type={node.Type}），却带有 offset/相位配置。",
                        $"nodes[{i}]",
                        ValidationSeverity.Warning));
                }

                continue;
            }

            if (node.SignalPlans.Count == 0)
            {
                errors.Add(new ValidationError(
                    "signalized_without_phases",
                    $"信号路口 id={node.Id} 没有任何信号方案（plans）。",
                    $"signals[node:{node.Id}]"));
                continue;
            }

            for (int p = 0; p < node.SignalPlans.Count; p++)
            {
                SignalPlan plan = node.SignalPlans[p];
                if (plan.Stages.Count == 0)
                {
                    errors.Add(new ValidationError(
                        "signalized_without_phases",
                        $"信号路口 id={node.Id} 的方案 plans[{p}] 没有任何相位（stages）。",
                        $"signals[node:{node.Id}].plans[{p}]"));
                    continue;
                }

                for (int s = 0; s < plan.Stages.Count; s++)
                {
                    ValidateStage(node, plan.Stages[s], p, s, turnIds, errors);
                }
            }
        }
    }

    private static void ValidateStage(Node node, Stage stage, int planIndex, int stageIndex, HashSet<int> turnIds, List<ValidationError> errors)
    {
        string path = $"signals[node:{node.Id}].plans[{planIndex}].stages[{stageIndex}]";

        if (stage.Duration <= 0d)
        {
            errors.Add(new ValidationError(
                "stage_duration_non_positive",
                $"节点 id={node.Id} 的相位 duration={stage.Duration} 必须为正（单位：秒，指绿灯时长）。",
                path + ".duration"));
        }

        ValidateStageTurnSet(node, stage.ProtectedTurnIds, turnIds, path + ".protected", "protected", errors);
        ValidateStageTurnSet(node, stage.PermittedTurnIds, turnIds, path + ".permitted", "permitted", errors);
    }

    private static void ValidateStageTurnSet(Node node, IReadOnlyList<int> ids, HashSet<int> turnIds, string path, string field, List<ValidationError> errors)
    {
        for (int i = 0; i < ids.Count; i++)
        {
            int turnId = ids[i];
            if (!turnIds.Contains(turnId))
            {
                errors.Add(new ValidationError(
                    "dangling_reference",
                    $"节点 id={node.Id} 的相位 {field} 引用了不存在的转向 id={turnId}。",
                    $"{path}[{i}]"));
            }
        }
    }

    // ------------------------------------------------------------------
    // demand
    // ------------------------------------------------------------------

    private static void ValidateDemand(Level level, HashSet<int> laneIds, List<ValidationError> errors)
    {
        IReadOnlyList<DemandPlan> demand = level.Demand;
        for (int i = 0; i < demand.Count; i++)
        {
            string path = $"demand[{i}]";
            if (!laneIds.Contains(demand[i].Lane))
            {
                errors.Add(new ValidationError(
                    "dangling_reference",
                    $"需求引用的车道 id={demand[i].Lane} 不存在。",
                    path + ".lane"));
            }

            ValidateProfile(demand[i].TimePoints, demand[i].FlowRates, path, "需求", errors);
        }
    }

    private static void ValidateProfile(IReadOnlyList<double> timePoints, IReadOnlyList<double> flowRates, string path, string what, List<ValidationError> errors)
    {
        if (timePoints.Count != flowRates.Count)
        {
            errors.Add(new ValidationError(
                "demand_profile_length_mismatch",
                $"{what}的 time_points（{timePoints.Count} 项）与 flow_rates（{flowRates.Count} 项）长度不一致。",
                path));
        }

        for (int i = 1; i < timePoints.Count; i++)
        {
            if (timePoints[i] <= timePoints[i - 1])
            {
                errors.Add(new ValidationError(
                    "time_points_not_ascending",
                    $"{what}的 time_points 必须严格递增：time_points[{i}]={timePoints[i]} 不大于前一项 {timePoints[i - 1]}。",
                    $"{path}.time_points[{i}]"));
                break;
            }
        }

        for (int i = 0; i < flowRates.Count; i++)
        {
            if (flowRates[i] < 0d)
            {
                errors.Add(new ValidationError(
                    "flow_rate_negative",
                    $"{what}的 flow_rates[{i}]={flowRates[i]} 不能为负。",
                    $"{path}.flow_rates[{i}]"));
            }
        }
    }

    // ------------------------------------------------------------------
    // pedestrians
    // ------------------------------------------------------------------

    // ------------------------------------------------------------------
    // islands[] + 分段过街（turns[].leg / turns[].island，2026-10-24 新增）
    // ------------------------------------------------------------------

    private static void ValidateIslandsAndLegs(Level level, Network network, List<ValidationError> errors)
    {
        // 岛本身：容量必须 ≥ 1（0 容量的岛等于没有岛，行人会卡死）
        for (int i = 0; i < network.Islands.Count; i++)
        {
            Island island = network.Islands[i];
            if (island.Capacity < 1)
            {
                errors.Add(new ValidationError(
                    "island_capacity_invalid",
                    $"安全岛 id={island.Id} 的 capacity={island.Capacity}，应 ≥ 1。",
                    $"islands[{i}].capacity"));
            }
        }

        var islandIds = ToIdSet(network.Islands, static x => x.Id);

        // 分段过街：leg 只能 0/1、只能出现在 Crosswalk 上、必须与 island 成对
        // 按 island id 收集两段，最后检查成对 + src/dst 一致
        var legsByIsland = new Dictionary<int, Dictionary<int, Turn>>();

        for (int i = 0; i < network.Turns.Count; i++)
        {
            Turn turn = network.Turns[i];
            bool hasLeg = turn.Leg >= 0;
            bool hasIsland = turn.IslandId >= 0;

            if (hasLeg != hasIsland)
            {
                errors.Add(new ValidationError(
                    "crosswalk_leg_incomplete",
                    $"转向 id={turn.Id} 的 leg/island 没有成对出现（leg={turn.Leg}, island={turn.IslandId}）。" +
                    "分段过街必须同时标「第几段」和「哪个安全岛」。",
                    $"turns[{i}]"));
                continue;
            }

            if (!hasLeg)
            {
                continue;
            }

            if (turn.Type != TurnType.Crosswalk)
            {
                errors.Add(new ValidationError(
                    "crosswalk_leg_not_crosswalk",
                    $"转向 id={turn.Id} 带了 leg={turn.Leg}，但类型是 {turn.Type}；只有 Crosswalk 允许分段。",
                    $"turns[{i}].leg"));
                continue;
            }

            if (turn.Leg != 0 && turn.Leg != 1)
            {
                errors.Add(new ValidationError(
                    "crosswalk_leg_invalid",
                    $"转向 id={turn.Id} 的 leg={turn.Leg}，只能是 0（路缘 → 岛）或 1（岛 → 对岸）。",
                    $"turns[{i}].leg"));
                continue;
            }

            if (!islandIds.Contains(turn.IslandId))
            {
                errors.Add(new ValidationError(
                    "crosswalk_island_missing",
                    $"转向 id={turn.Id} 引用的安全岛 id={turn.IslandId} 不存在。",
                    $"turns[{i}].island"));
                continue;
            }

            if (!legsByIsland.TryGetValue(turn.IslandId, out Dictionary<int, Turn>? legs))
            {
                legs = new Dictionary<int, Turn>(2);
                legsByIsland[turn.IslandId] = legs;
            }

            if (legs.ContainsKey(turn.Leg))
            {
                errors.Add(new ValidationError(
                    "crosswalk_leg_duplicated",
                    $"安全岛 id={turn.IslandId} 的第 {turn.Leg} 段出现了不止一条（转向 id={turn.Id} 与 {legs[turn.Leg].Id}）。",
                    $"turns[{i}]"));
                continue;
            }

            legs[turn.Leg] = turn;
        }

        // 每个岛必须正好两段，且两段跨越同一对人行道（src/dst 相同）
        foreach (KeyValuePair<int, Dictionary<int, Turn>> pair in legsByIsland)
        {
            int islandId = pair.Key;
            Dictionary<int, Turn> legs = pair.Value;
            if (!legs.TryGetValue(0, out Turn? leg0))
            {
                errors.Add(new ValidationError(
                    "crosswalk_leg_missing",
                    $"安全岛 id={islandId} 只有第 1 段，缺第 0 段（行人没有入口）。",
                    $"islands[id:{islandId}]"));
                continue;
            }

            if (!legs.TryGetValue(1, out Turn? leg1))
            {
                errors.Add(new ValidationError(
                    "crosswalk_leg_missing",
                    $"安全岛 id={islandId} 只有第 0 段，缺第 1 段（行人会卡在岛上走不完）。",
                    $"islands[id:{islandId}]"));
                continue;
            }

            if (leg0.SrcLane != leg1.SrcLane || leg0.DstLane != leg1.DstLane)
            {
                errors.Add(new ValidationError(
                    "crosswalk_leg_mismatch",
                    $"安全岛 id={islandId} 的两段不是同一对过街：第 0 段 {leg0.SrcLane}→{leg0.DstLane}，" +
                    $"第 1 段 {leg1.SrcLane}→{leg1.DstLane}。两段必须跨同一条人行横道。",
                    $"islands[id:{islandId}]"));
            }
        }

        _ = level;
    }

    private static void ValidatePedestrians(Level level, Network network, HashSet<int> turnIds, List<ValidationError> errors)
    {
        IReadOnlyList<PedestrianDemand> pedestrians = level.Pedestrians;
        for (int i = 0; i < pedestrians.Count; i++)
        {
            string path = $"pedestrians[{i}]";
            int turnId = pedestrians[i].CrosswalkTurn;

            if (!turnIds.Contains(turnId))
            {
                errors.Add(new ValidationError(
                    "dangling_reference",
                    $"行人需求引用的 crosswalk_turn={turnId} 不存在。",
                    path + ".crosswalk_turn"));
            }
            else if (TryGetTurn(network, turnId, out Turn? turn) && turn!.Type != TurnType.Crosswalk)
            {
                errors.Add(new ValidationError(
                    "pedestrian_turn_not_crosswalk",
                    $"行人需求引用的转向 id={turnId} 类型为 {turn.Type}，不是人行横道（Crosswalk）。",
                    path + ".crosswalk_turn"));
            }

            ValidateProfile(pedestrians[i].TimePoints, pedestrians[i].FlowRates, path, "行人需求", errors);
        }
    }

    // ------------------------------------------------------------------
    // bicycles
    // ------------------------------------------------------------------

    private static void ValidateBicycles(Level level, Network network, HashSet<int> laneIds, List<ValidationError> errors)
    {
        IReadOnlyList<BicycleDemand> bicycles = level.Bicycles;
        for (int i = 0; i < bicycles.Count; i++)
        {
            string path = $"bicycles[{i}]";
            BicycleDemand bike = bicycles[i];

            if (!laneIds.Contains(bike.Lane))
            {
                errors.Add(new ValidationError(
                    "dangling_reference",
                    $"非机动车需求引用的车道 id={bike.Lane} 不存在。",
                    path + ".lane"));
            }
            else if (TryGetLane(network, bike.Lane, out Lane? lane))
            {
                LaneType type = lane!.Type;
                bool ok = bike.Mode == BicycleMode.Independent
                    ? type == LaneType.Biking
                    : type is LaneType.Biking or LaneType.Driving;
                if (!ok)
                {
                    string expected = bike.Mode == BicycleMode.Independent ? "Biking" : "Biking 或 Driving";
                    errors.Add(new ValidationError(
                        "bicycle_lane_not_biking",
                        $"非机动车需求（mode={bike.Mode}）引用的车道 id={bike.Lane} 类型为 {type}，应为 {expected}。",
                        path + ".lane"));
                }
            }

            ValidateProfile(bike.TimePoints, bike.FlowRates, path, "非机动车需求", errors);
        }
    }

    // ------------------------------------------------------------------
    // game_rules
    // ------------------------------------------------------------------

    private static void ValidateGameRules(Level level, List<ValidationError> errors)
    {
        MetricAnchors anchors = level.GameRules.Anchors;
        if (anchors.AllUnset)
        {
            errors.Add(new ValidationError(
                "anchors_unset",
                "game_rules.anchors 四项锚点均未标定——评分将无法给出分数。",
                "game_rules.anchors",
                ValidationSeverity.Warning));
        }
        else
        {
            ValidateAnchor(anchors.R, "R", largerIsBetter: true, errors);
            ValidateAnchor(anchors.D, "D", largerIsBetter: false, errors);
            ValidateAnchor(anchors.P, "P", largerIsBetter: false, errors);
            ValidateAnchor(anchors.Q, "Q", largerIsBetter: false, errors);
        }

        MetricWeights weights = level.GameRules.Weights;
        if (weights.R < 0d || weights.D < 0d || weights.P < 0d || weights.Q < 0d)
        {
            errors.Add(new ValidationError(
                "weights_invalid",
                $"game_rules.weights 存在负权重（R={weights.R}, D={weights.D}, P={weights.P}, Q={weights.Q}）。",
                "game_rules.weights"));
        }
        else if (Math.Abs(weights.Sum - 1d) > 1e-6)
        {
            errors.Add(new ValidationError(
                "weights_not_normalized",
                $"game_rules.weights 之和为 {weights.Sum}，应为 1.0。",
                "game_rules.weights",
                ValidationSeverity.Warning));
        }
    }

    private static void ValidateAnchor(MetricAnchorValue anchor, string metric, bool largerIsBetter, List<ValidationError> errors)
    {
        if (anchor.IsUnset)
        {
            return;
        }

        bool inverted = largerIsBetter ? anchor.Good < anchor.Bottom : anchor.Good > anchor.Bottom;
        if (inverted)
        {
            string hint = largerIsBetter ? "优秀值 g 应 ≥ 底线值 b" : "优秀值 g 应 ≤ 底线值 b";
            errors.Add(new ValidationError(
                "anchor_ordering_inverted",
                $"game_rules.anchors.{metric}=[b={anchor.Bottom}, g={anchor.Good}] 的取值方向相反：{hint}。",
                $"game_rules.anchors.{metric}",
                ValidationSeverity.Warning));
        }
    }

    // ------------------------------------------------------------------
    // 工具
    // ------------------------------------------------------------------

    private static bool TryGetTurn(Network network, int turnId, out Turn? turn)
    {
        IReadOnlyList<Turn> turns = network.Turns;
        for (int i = 0; i < turns.Count; i++)
        {
            if (turns[i].Id == turnId)
            {
                turn = turns[i];
                return true;
            }
        }

        turn = null;
        return false;
    }

    private static bool TryGetLane(Network network, int laneId, out Lane? lane)
    {
        IReadOnlyList<Lane> lanes = network.Lanes;
        for (int i = 0; i < lanes.Count; i++)
        {
            if (lanes[i].Id == laneId)
            {
                lane = lanes[i];
                return true;
            }
        }

        lane = null;
        return false;
    }
}
