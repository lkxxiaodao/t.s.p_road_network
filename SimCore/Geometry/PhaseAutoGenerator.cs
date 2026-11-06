using SimCore.Model;

namespace SimCore.Geometry;

// ---------------------------------------------------------------------------
// T4.2(b) · 相位方案自动生成（技术方案03 §5 · E-C2；docs/SimCore-API.md §3.3 冻结）
//
// 本文件落地 docs/SimCore-API.md §3.3 冻结的 PhaseAutoGenerator.Suggest(Network, int)。
// 借鉴 A/B Street map_model/src/make/traffic_signals/mod.rs 的 get_possible_policies()
// （技术方案02 §2.3）：
//   * four_way_two_stage  → 十字、无保护左转  → 本类"2 阶段方案"
//   * four_way_four_stage → 十字、保护左转    → 本类"4 阶段方案"
//   * greedy_assignment   → 任意异形兜底      → 本类"把互不冲突的交通流贪心并入同一相位"
//
// 同源要求（§3.3 末条 / 验收 T4.2-d）：编辑器"自动推导"与运行期门控必须使用同一套冲突判定，
// 因此本类**一切冲突判定一律经 ConflictMatrix.AreConflicting**，绝不另写几何。
//
// 铁律：只依赖 SimCore.Model（+ 同 namespace 的 ConflictMatrix）；无 Godot 命名空间；
//       无第三方依赖；无系统随机数；同一 network 两次调用结果逐项相等（遍历顺序全部由 id 唯一确定）。
// ---------------------------------------------------------------------------

/// <summary>
/// 相位方案自动生成器（T4.2(b)；<c>docs/SimCore-API.md</c> §3.3 冻结）。
/// </summary>
/// <remarks>
/// <para>职责：给定路网与路口，产出该路口的<b>相位方案候选集</b>，至少包含"2 阶段"与"4 阶段"各一套。
/// 对应技术方案02 §2.3「画完几何即自动得到灯组与冲突矩阵」，与运行期路口门控共享同一套
/// <see cref="ConflictMatrix"/> 判定（验收 T4.2-d）。</para>
///
/// <para><b>分组口径</b></para>
/// <list type="number">
///   <item>取该路口全部<b>机动车交通流</b>（<see cref="Movement"/>，来自
///   <see cref="CompiledNetwork.MovementsAtNode"/>；<see cref="TurnType.Crosswalk"/> 为人行横道，
///   不参与机动车相位，排除）。</item>
///   <item>按<b>进口方向</b>把交通流聚成若干"轴"（<see cref="ApproachAxis"/>）：进口方向
///   ∥ 关系（±<see cref="AxisParallelSinTolerance"/>）视为同轴——十字路口的"南进口 + 北进口"
///   同轴、"东进口 + 西进口"同轴，与 A/B Street "对向进口同相位"一致。</item>
///   <item>把互不冲突的交通流贪心并入同一 <see cref="Stage"/> 的 <c>protected</c>；冲突者
///   退到 <c>permitted</c>（让行）或另一相位。<b>贪心选择保证同相位 protected 内两两不冲突</b>
///   （验收 b）。</item>
/// </list>
///
/// <para><b>两套方案</b></para>
/// <list type="bullet">
///   <item><b>2 阶段</b>（<c>Suggest[0]</c>）：每轴一个相位；<c>protected</c> = 直行 + 右转，
///   <c>permitted</c> = 左转 + 掉头（无保护左转，须让行对向直行）——即
///   <c>four_way_two_stage</c>。十字路口恰为 2 个相位。</item>
///   <item><b>4 阶段</b>（<c>Suggest[1]</c>）：每轴拆成"直行 + 右转"与"左转 + 掉头"两个相位，
///   得到"保护左转"相序（NS 直行 → NS 左转 → EW 直行 → EW 左转）——即 <c>four_way_four_stage</c>。
///   十字路口恰为 4 个相位。</item>
/// </list>
///
/// <para><b>时长口径</b>（<c>docs/spmap-schema.md</c> §4.5）：<see cref="Stage.Duration"/> = <b>绿灯时长</b>，
/// 一律 <c>&gt; 0</c>；黄灯 / 全红 / 绿闪<b>不写入</b>（由 <c>SignalTiming</c> 依清空时间估算）。
/// <c>stages[].type</c> 一律写 <see cref="StageType.Fixed"/>。时长取固定常量建议值
/// （<see cref="TwoStageGreenSeconds"/> / <see cref="FourStageThroughGreenSeconds"/> /
/// <see cref="FourStageTurningGreenSeconds"/>），可直接被玩家在基础模式微调。</para>
///
/// <para><b>前置</b>（§3.3）：<c>network != null</c>；<c>nodeId</c> 指向存在的节点；该节点有进口车道。
/// 不满足者抛异常（<see cref="Suggest"/> 的异常表）。此外本实现要求该节点<b>已有 turns</b>
/// （否则 <c>protected/permitted</c> 无 id 可指）——见 §1.3「无 turns 时的行为」。</para>
/// </remarks>
public static class PhaseAutoGenerator
{
    // ------------------------------------------------------------------
    // 固定配时建议值（本任务追加约束：Stage.Duration = 绿灯时长，必须 > 0；不得读环境 / 随机）
    // 取 internal 以便单测直接断言其固定取值，同时不扩展冻结 public 面（§3.3 只列 Suggest）。
    // ------------------------------------------------------------------

    /// <summary>2 阶段方案中每个相位的绿灯时长建议值（秒）。</summary>
    internal const double TwoStageGreenSeconds = 30.0d;

    /// <summary>4 阶段方案中"直行 + 右转"相位的绿灯时长建议值（秒）。</summary>
    internal const double FourStageThroughGreenSeconds = 25.0d;

    /// <summary>4 阶段方案中"左转 + 掉头"相位的绿灯时长建议值（秒）。</summary>
    internal const double FourStageTurningGreenSeconds = 15.0d;

    /// <summary>
    /// "同轴"判定的平行容差（进口方向单位向量叉积的绝对值上界 = sin 10°）。
    /// 小于该值视为对向/同向进口（可并入同一相位）。
    /// </summary>
    internal const double AxisParallelSinTolerance = 0.17364817766693033d;

    /// <summary>几何退化判定容差（零长度车道：方向不可解析）。</summary>
    private const double DirectionEpsilon = 1e-9d;

    /// <summary>
    /// 为指定路口自动生成相位方案候选集（§3.3 字面）。
    /// </summary>
    /// <param name="network">路网（只读，不会被修改）。</param>
    /// <param name="nodeId">目标路口节点 id。</param>
    /// <returns>
    /// 候选集，<b>顺序稳定</b>：<c>[0]</c> = 2 阶段方案，<c>[1]</c> = 4 阶段方案。
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="network"/> 为 <c>null</c>。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="nodeId"/> 不存在（越界）。</exception>
    /// <exception cref="ArgumentException">
    /// 该节点无进口车道；或该节点无 turns（见 <see cref="Suggest"/> 备注）；或该节点无机动车转向组
    /// （仅有人行横道）。
    /// </exception>
    public static SignalPlan[] Suggest(Network network, int nodeId)
    {
        ArgumentNullException.ThrowIfNull(network);

        // 拓扑编译：按 id 直寻址 + 预计算交通流分组（§2.8）。id 非 0..N-1 连续 → ArgumentException。
        CompiledNetwork compiled = network.Compile();

        // 前置：nodeId 指向存在的节点（§3.3）。合法路网节点 id 为 0..N-1，越界即"不存在"。
        if ((uint)nodeId >= (uint)compiled.NodeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeId), nodeId, "节点 id 不存在（越界）。");
        }

        // 前置：该节点有进口车道（§3.3）。
        if (compiled.IncomingLanes(nodeId).Count == 0)
        {
            throw new ArgumentException(
                $"节点 {nodeId} 无进口车道，无法生成相位方案。", nameof(nodeId));
        }

        // 前置（本任务追加约束）：该节点已有 turns。无 turns 时 protected/permitted 无 id 可指。
        // 【行为选择】此处选"抛异常"（而非返回空数组）——与 §1.3「前置违反一律抛异常、不静默吞掉」一致。
        if (compiled.TurnsAtNode(nodeId).Count == 0)
        {
            throw new ArgumentException(
                $"节点 {nodeId} 无 turns（protected/permitted 无 id 可指）；"
                + "请先运行 TurnGenerator.FromGeometry 并把结果并入路网后再调用本类。",
                nameof(nodeId));
        }

        // 冲突判定全部走 ConflictMatrix（§3.3 末条同源要求），与运行期门控共用同一套几何。
        ConflictMatrix conflicts = ConflictMatrix.From(compiled);

        // 机动车交通流（排除人行横道）；MovementsAtNode 已按交通流 id 升序返回。
        var movements = new List<Movement>();
        foreach (int movementId in compiled.MovementsAtNode(nodeId))
        {
            Movement movement = compiled.Movements[movementId];
            if (movement.Type != TurnType.Crosswalk)
            {
                movements.Add(movement);
            }
        }

        if (movements.Count == 0)
        {
            throw new ArgumentException(
                $"节点 {nodeId} 无机动车转向组（仅有人行横道），无法生成相位方案。", nameof(nodeId));
        }

        List<ApproachAxis> axes = BuildAxes(compiled, movements);

        SignalPlan twoStage = BuildTwoStagePlan(axes, conflicts);
        SignalPlan fourStage = BuildFourStagePlan(axes, conflicts);

        return [twoStage, fourStage];
    }

    // ------------------------------------------------------------------
    // 进口方向聚类（"轴"）
    // ------------------------------------------------------------------

    /// <summary>
    /// 按进口方向把机动车交通流聚成若干"轴"（对向进口同轴）；轴按组内最小交通流 id 升序排列。
    /// </summary>
    private static List<ApproachAxis> BuildAxes(CompiledNetwork compiled, List<Movement> movements)
    {
        // 道路 id 升序（显式排序以保证与输入顺序无关的确定性）。
        var roads = new SortedSet<int>();
        var directionByRoad = new Dictionary<int, (double X, double Y)>();
        foreach (Movement movement in movements)
        {
            if (roads.Add(movement.Road))
            {
                directionByRoad[movement.Road] = ApproachDirection(compiled, movement);
            }
        }

        var axes = new List<ApproachAxis>();
        var axisIndexOfRoad = new Dictionary<int, int>();
        foreach (int road in roads)
        {
            (double X, double Y) direction = directionByRoad[road];

            int found = -1;
            for (int i = 0; i < axes.Count; i++)
            {
                if (IsParallel(axes[i].Reference, direction))
                {
                    found = i;
                    break;
                }
            }

            int axisIndex;
            if (found < 0)
            {
                axes.Add(new ApproachAxis(direction));
                axisIndex = axes.Count - 1;
            }
            else
            {
                axisIndex = found;
            }

            axisIndexOfRoad[road] = axisIndex;
        }

        // 按道路 id 升序把交通流装入所属轴（movements 本身已按 id 升序，故每组内也升序）。
        foreach (Movement movement in movements)
        {
            axes[axisIndexOfRoad[movement.Road]].Movements.Add(movement);
        }

        // 轴顺序：组内最小交通流 id 升序（十字即"南北轴(NS) 先、东西轴(EW) 后"）。
        axes.Sort(static (a, b) => a.Movements[0].Id.CompareTo(b.Movements[0].Id));
        return axes;
    }

    /// <summary>取交通流的进口方向：其首条转向的源车道行驶方向（指向节点）单位向量。</summary>
    private static (double X, double Y) ApproachDirection(CompiledNetwork compiled, Movement movement)
    {
        Turn turn = compiled.GetTurn(movement.TurnIds[0]);
        Lane src = compiled.GetLane(turn.SrcLane);
        return UnitDirection(compiled.GetNode(src.FromNode), compiled.GetNode(src.ToNode));
    }

    /// <summary>两进口方向是否"同轴"（近似平行，含同向与反向）；零向量不参与聚类。</summary>
    private static bool IsParallel((double X, double Y) a, (double X, double Y) b)
    {
        bool degenerateA = a.X == 0d && a.Y == 0d;
        bool degenerateB = b.X == 0d && b.Y == 0d;
        if (degenerateA || degenerateB)
        {
            return false;
        }

        double cross = (a.X * b.Y) - (a.Y * b.X);
        return Math.Abs(cross) <= AxisParallelSinTolerance;
    }

    /// <summary>两节点间的单位方向向量；两端重合（长度≈0）时返回零向量。</summary>
    private static (double X, double Y) UnitDirection(Node from, Node to)
    {
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));

        return length < DirectionEpsilon ? (0d, 0d) : (dx / length, dy / length);
    }

    // ------------------------------------------------------------------
    // 方案构造
    // ------------------------------------------------------------------

    /// <summary>
    /// 2 阶段方案：每轴一个相位；<c>protected</c> = 直行 + 右转（贪心取两两不冲突者），
    /// <c>permitted</c> = 左转 + 掉头（+ 未入选 protected 的直行/右转）。
    /// </summary>
    private static SignalPlan BuildTwoStagePlan(List<ApproachAxis> axes, ConflictMatrix conflicts)
    {
        var stages = new List<Stage>(axes.Count);
        foreach (ApproachAxis axis in axes)
        {
            List<Movement> throughCandidates = Candidates(axis, through: true);
            List<Movement> turningCandidates = Candidates(axis, through: false);

            List<Movement> protectedMovements = GreedySelect(throughCandidates, conflicts);
            var permittedMovements = new List<Movement>(turningCandidates);
            foreach (Movement movement in throughCandidates)
            {
                if (!protectedMovements.Contains(movement))
                {
                    permittedMovements.Add(movement);
                }
            }

            stages.Add(BuildStage(protectedMovements, permittedMovements, TwoStageGreenSeconds));
        }

        return new SignalPlan { StartTime = 0d, Stages = stages };
    }

    /// <summary>
    /// 4 阶段方案：每轴拆"直行 + 右转"与"左转 + 掉头"两个相位（保护左转相序）。
    /// 空组不生成相位（不产出空相位）。
    /// </summary>
    private static SignalPlan BuildFourStagePlan(List<ApproachAxis> axes, ConflictMatrix conflicts)
    {
        var stages = new List<Stage>(axes.Count * 2);
        foreach (ApproachAxis axis in axes)
        {
            AppendStage(stages, Candidates(axis, through: true), FourStageThroughGreenSeconds, conflicts);
            AppendStage(stages, Candidates(axis, through: false), FourStageTurningGreenSeconds, conflicts);
        }

        return new SignalPlan { StartTime = 0d, Stages = stages };
    }

    /// <summary>把一组候选交通流贪心并入一个新相位（空组跳过）；未入选者退到 <c>permitted</c>。</summary>
    private static void AppendStage(
        List<Stage> stages,
        List<Movement> candidates,
        double duration,
        ConflictMatrix conflicts)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        List<Movement> protectedMovements = GreedySelect(candidates, conflicts);
        var permittedMovements = new List<Movement>();
        foreach (Movement movement in candidates)
        {
            if (!protectedMovements.Contains(movement))
            {
                permittedMovements.Add(movement);
            }
        }

        stages.Add(BuildStage(protectedMovements, permittedMovements, duration));
    }

    /// <summary>组装一个相位：<c>type = Fixed</c>，时长 = 绿灯时长（&gt; 0），转向集合升序去重。</summary>
    private static Stage BuildStage(
        IReadOnlyList<Movement> protectedMovements,
        IReadOnlyList<Movement> permittedMovements,
        double duration)
        => new()
        {
            Type = StageType.Fixed,
            Duration = duration,
            ProtectedTurnIds = CollectTurnIds(protectedMovements),
            PermittedTurnIds = CollectTurnIds(permittedMovements),
        };

    /// <summary>
    /// 取某轴的候选交通流：<paramref name="through"/> 为真取"直行 + 右转"，否则取"左转 + 掉头"；
    /// 按 (转向类型优先级, 交通流 id) 升序（直行优先入选 protected）。
    /// </summary>
    private static List<Movement> Candidates(ApproachAxis axis, bool through)
    {
        var list = new List<Movement>();
        foreach (Movement movement in axis.Movements)
        {
            bool isThrough = movement.Type is TurnType.Straight or TurnType.Right;
            if (isThrough == through)
            {
                list.Add(movement);
            }
        }

        list.Sort(static (a, b) =>
        {
            int byRank = TypeRank(a.Type).CompareTo(TypeRank(b.Type));
            return byRank != 0 ? byRank : a.Id.CompareTo(b.Id);
        });

        return list;
    }

    /// <summary>
    /// 贪心挑选"与已选交通流两两不冲突"的最大前缀子集（A/B Street <c>greedy_assignment</c> 的核心）。
    /// 因每次入选前都逐对校验，返回集合<b>两两不冲突</b>（构造即满足验收 b）。
    /// </summary>
    private static List<Movement> GreedySelect(
        IReadOnlyList<Movement> candidates, ConflictMatrix conflicts)
    {
        var picked = new List<Movement>();
        foreach (Movement candidate in candidates)
        {
            bool compatible = true;
            foreach (Movement chosen in picked)
            {
                if (MovementsConflict(candidate, chosen, conflicts))
                {
                    compatible = false;
                    break;
                }
            }

            if (compatible)
            {
                picked.Add(candidate);
            }
        }

        return picked;
    }

    /// <summary>两个交通流是否冲突：任一对转向冲突即冲突（判定全部走 <see cref="ConflictMatrix"/>）。</summary>
    private static bool MovementsConflict(Movement a, Movement b, ConflictMatrix conflicts)
    {
        foreach (int turnA in a.TurnIds)
        {
            foreach (int turnB in b.TurnIds)
            {
                if (conflicts.AreConflicting(turnA, turnB))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>把若干交通流包含的转向 id 合并为升序去重数组（确定性）。</summary>
    private static int[] CollectTurnIds(IReadOnlyList<Movement> movements)
    {
        var ids = new SortedSet<int>();
        foreach (Movement movement in movements)
        {
            foreach (int turnId in movement.TurnIds)
            {
                ids.Add(turnId);
            }
        }

        return ids.Count == 0 ? Array.Empty<int>() : ids.ToArray();
    }

    /// <summary>转向类型的入选优先级（越小越优先入选 protected）：直行 &lt; 右转 &lt; 左转 &lt; 掉头。</summary>
    private static int TypeRank(TurnType type) => type switch
    {
        TurnType.Straight => 0,
        TurnType.Right => 1,
        TurnType.Left => 2,
        TurnType.UTurn => 3,
        _ => 4,
    };

    /// <summary>一个"轴"：一组同轴（对向/同向）进口的机动车交通流。</summary>
    private sealed class ApproachAxis
    {
        public ApproachAxis((double X, double Y) reference) => Reference = reference;

        /// <summary>本轴的参考进口方向（首条进口道路的方向），供平行聚类比较。</summary>
        public (double X, double Y) Reference { get; }

        /// <summary>本轴的交通流（按交通流 id 升序）。</summary>
        public List<Movement> Movements { get; } = new();
    }
}
