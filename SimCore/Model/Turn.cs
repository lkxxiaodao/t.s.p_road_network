namespace SimCore.Model;

/// <summary>
/// 一条转向（原子连接）：车流从 <see cref="SrcLane"/> 经 <see cref="Node"/> 驶入 <see cref="DstLane"/>。
/// 字段对齐技术方案03 §3.2 的 <c>turns[]</c>。
/// </summary>
/// <remarks>
/// <para>这是路网里最小的可放行单元：信号相位（<see cref="Stage"/>）按转向 id 集合保护/允许放行，
/// 冲突矩阵（T1.3 <c>ConflictMatrix</c>）也两两作用于转向（技术方案01 §4.2）。</para>
/// <para>转向的优先级（<see cref="TurnPriority"/>）不静态存储——§3.2 的 turns 表无该字段——
/// 而是由当前生效相位通过 <see cref="Stage.PriorityOf(int)"/> 派生。</para>
/// <para>本类型为纯数据载体，不含任何仿真逻辑。</para>
/// </remarks>
public sealed class Turn
{
    /// <summary>转向 id（§3.2 <c>turns[].id</c>；编译期要求 0..N-1 连续且唯一）。</summary>
    public int Id { get; init; }

    /// <summary>源车道 id（§3.2 <c>turns[].src_lane</c>；其 <see cref="Lane.ToNode"/> 应为 <see cref="Node"/>）。</summary>
    public int SrcLane { get; init; }

    /// <summary>目标车道 id（§3.2 <c>turns[].dst_lane</c>；其 <see cref="Lane.FromNode"/> 应为 <see cref="Node"/>）。</summary>
    public int DstLane { get; init; }

    /// <summary>所属路口节点 id（§3.2 <c>turns[].node</c>）。</summary>
    public int Node { get; init; }

    /// <summary>转向类型（§3.2 <c>turns[].type</c>）。</summary>
    public TurnType Type { get; init; }

    /// <summary>
    /// 分段过街的"第几段"（§3.2 <c>turns[].leg</c>，可选，缺省 <c>-1</c> = 不是分段过街）。
    /// </summary>
    /// <remarks>
    /// <para>只有 <see cref="TurnType.Crosswalk"/> 可能带这个值：<c>0</c> = 前段（路缘 → 安全岛）、
    /// <c>1</c> = 后段（安全岛 → 对岸）。两段是<b>两条独立转向</b>，各自能被相位放行，
    /// 因此"两段式行人相位"可以真的做出来（2026-10-24 E92 G2）。</para>
    /// <para>缺省 <c>-1</c> 表示一次走完（旧关卡全部是这种），行为与改动前逐位一致。</para>
    /// </remarks>
    public int Leg { get; init; } = -1;

    /// <summary>
    /// 分段过街所属的安全岛 id（§3.2 <c>turns[].island</c>，可选，缺省 <c>-1</c> = 无岛）。
    /// </summary>
    /// <remarks>与 <see cref="Leg"/> 成对出现：有岛必须有段号，有段号必须指向存在的 <see cref="Island"/>。</remarks>
    public int IslandId { get; init; } = -1;
}
