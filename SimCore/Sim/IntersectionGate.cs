using System.Runtime.CompilerServices;
using SimCore.Geometry;
using SimCore.Model;

// T1.3：把本卡新增的"路口准入判定"（IntersectionGate.CanEnter）以 internal 形式交付——
// 它不进入 §4.6 冻结的 public 符号面（Build / IsTurnOpen / HasProtectedConflict），
// 仅供 SimCore 内部（T1.6 SimEngine）与单元测试使用；此处授权测试程序集访问 internal。
[assembly: InternalsVisibleTo("SimCore.Tests")]

namespace SimCore.Sim;

// ---------------------------------------------------------------------------
// T1.3 · 路口放行门控（技术方案03 §5 · T1.3；docs/SimCore-API.md §4.6 冻结）
//
// 本文件落地 docs/SimCore-API.md §4.6 冻结的 IntersectionGate，并实现其"门控 =
// 相位掩码 ∧ 冲突仲裁 ∧ 目标车道容量"的完整语义：
//   * IsTurnOpen         —— 相位掩码（Protected / Yield → true；Banned → false）
//   * HasProtectedConflict —— 同一相位内 protected 集合存在冲突对 → true（告警，T1.3 b）
//   * CanEnter（internal）—— 进入决策：IsTurnOpen ∧ 冲突仲裁（Yield 让行 Protected，T1.3 d）
//                            ∧ 目标车道空位（T1.3 e）
//
// 与 docs/SimCore-API.md §4.6 的对应：原契约只列出前两个 public 符号，并规定"SimEngine 内部
// 对每辆车做进入决策"；本卡把该"进入决策"具化为 CanEnter（internal，非冻结面），
// 供 T1.6 组装 SimEngine 时调用，public 冻结面保持不变。
//
// 铁律：无 Godot 命名空间、无第三方依赖、无系统随机数（纯查表 / 纯判定，无副作用）。
// ---------------------------------------------------------------------------

/// <summary>
/// 路口门控（T1.3；<c>docs/SimCore-API.md</c> §4.6 冻结）。
/// </summary>
/// <remarks>
/// <para>职责：把某路口的"相位掩码 ∧ 冲突仲裁 ∧ 目标车道容量"三者合成进入判定，
/// 是"碰撞判负"的准入闸门（技术方案02 §2.2b「<c>maybe_start_turn()</c> + Protected/Yield/Banned 门控」）。</para>
///
/// <para><b>三个判定面</b>：
/// <list type="number">
///   <item><b>相位掩码</b>（<see cref="IsTurnOpen(int,int,Stage)"/>）：由 <see cref="Stage.PriorityOf(int)"/>
///   派生——<see cref="TurnPriority.Protected"/> / <see cref="TurnPriority.Yield"/> 放行、
///   <see cref="TurnPriority.Banned"/> 禁行（红灯）。</item>
///   <item><b>冲突仲裁</b>（<see cref="CanEnter(int,int,Stage,System.Collections.Generic.IReadOnlyList{int},bool)"/>）：
///   <see cref="TurnPriority.Yield"/> 转向不得与已放行的 <see cref="TurnPriority.Protected"/> 转向冲突
///   （Yield 须让行 Protected，T1.3 d）。<see cref="TurnPriority.Protected"/> 具绝对优先，不因冲突被阻断——
///   同一相位内两条冲突的 Protected 属"相位作者过失"，由 <see cref="HasProtectedConflict"/> 告警、
///   由运行期碰撞判负承担后果（游戏设计v2 §8.1「红色警示但不阻止运行」）。</item>
///   <item><b>目标车道容量</b>：目标车道无空位时禁止驶入路口（T1.3 e），由调用方以
///   <c>targetLaneHasSpace</c> 传入（门控不持有队列状态）。</item>
/// </list>
/// </para>
///
/// <para><b>同源约束</b>：<see cref="Build(CompiledNetwork,ConflictMatrix)"/> 要求冲突矩阵与路网同源
/// （同一 <see cref="CompiledNetwork"/> 实例），保证门控与编辑器"自动推导"共用同一套冲突判定
/// （验收 T4.2 d，不得重复实现）。</para>
/// </remarks>
public sealed class IntersectionGate
{
    private readonly CompiledNetwork _network;
    private readonly ConflictMatrix _conflicts;

    private IntersectionGate(CompiledNetwork network, ConflictMatrix conflicts)
    {
        _network = network;
        _conflicts = conflicts;
    }

    /// <summary>
    /// 由编译路网与冲突矩阵构建门控（§4.6 字面）。
    /// </summary>
    /// <param name="network">编译路网（只读）。</param>
    /// <param name="conflicts">与之同源的冲突矩阵（<c>ConflictMatrix.From(network)</c>）。</param>
    /// <returns>不可变的门控。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="network"/> 或 <paramref name="conflicts"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="conflicts"/> 与本路网不同源（非同一 <see cref="CompiledNetwork"/> 实例）。</exception>
    public static IntersectionGate Build(CompiledNetwork network, ConflictMatrix conflicts)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(conflicts);

        if (!ReferenceEquals(conflicts.SourceNetwork, network))
        {
            throw new ArgumentException(
                "冲突矩阵与本路网不同源：IntersectionGate.Build 要求 conflicts == ConflictMatrix.From(network)（同一 CompiledNetwork 实例）。",
                nameof(conflicts));
        }

        return new IntersectionGate(network, conflicts);
    }

    /// <summary>
    /// 相位掩码：某转向在当前相位下是否被放行（§4.6）。Protected / Yield → <c>true</c>；Banned → <c>false</c>。
    /// </summary>
    /// <param name="nodeId">路口节点 id（用于校验；本判定不依赖节点自身状态）。</param>
    /// <param name="turnId">转向 id。</param>
    /// <param name="stage">当前相位（其 <see cref="Stage.PriorityOf(int)"/> 承载放行集合）。</param>
    /// <returns>放行为 <c>true</c>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stage"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="nodeId"/> 或 <paramref name="turnId"/> 越界。</exception>
    public bool IsTurnOpen(int nodeId, int turnId, Stage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        CheckNode(nodeId);
        CheckTurn(turnId);

        return stage.PriorityOf(turnId) != TurnPriority.Banned;
    }

    /// <summary>
    /// 相位内 protected 集合是否存在冲突对（§4.6，T1.3 b）——供 T3.3 c 的红色警示，
    /// 编辑器 / UI <b>不得自行判定</b>。
    /// </summary>
    /// <param name="nodeId">路口节点 id（用于校验）。</param>
    /// <param name="stage">待检查的相位。</param>
    /// <returns>存在冲突对为 <c>true</c>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stage"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="nodeId"/> 越界，
    /// 或 <paramref name="stage"/> 的保护转向 id 越界。</exception>
    public bool HasProtectedConflict(int nodeId, Stage stage)
    {
        ArgumentNullException.ThrowIfNull(stage);
        CheckNode(nodeId);

        IReadOnlyList<int> protectedIds = stage.ProtectedTurnIds;
        for (int i = 0; i < protectedIds.Count; i++)
        {
            for (int j = i + 1; j < protectedIds.Count; j++)
            {
                if (_conflicts.AreConflicting(protectedIds[i], protectedIds[j]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 进入决策：<b>相位掩码 ∧ 冲突仲裁 ∧ 目标车道容量</b>（T1.3 的完整门控语义，
    /// 落地 <c>docs/SimCore-API.md</c> §4.6「SimEngine 内部对每辆车做进入决策」）。
    /// </summary>
    /// <remarks>
    /// <para>本成员为 <b>internal（非冻结 public 面）</b>：§4.6 只冻结了
    /// <see cref="Build(CompiledNetwork,ConflictMatrix)"/> / <see cref="IsTurnOpen"/> /
    /// <see cref="HasProtectedConflict"/> 三个符号，故"进入决策"以 internal 形式交付，
    /// 供 T1.6 <c>SimEngine</c>（同程序集）与单测调用，不进入对外接口面。</para>
    /// <para><b>语义</b>：依次判定
    /// ①<see cref="IsTurnOpen(int,int,Stage)"/>；
    /// ②<b>冲突仲裁</b>（T1.3 d）——若 <paramref name="occupiedTurnIds"/> 中存在某占用转向
    /// <c>o</c> 与 <paramref name="turnId"/> 冲突，且 <c>o</c> 的优先级<b>更高</b>
    /// （即 <c>stage.PriorityOf(o) &gt; stage.PriorityOf(turnId)</c>，等价于"Yield 须让行 Protected"），则拒绝；
    /// <see cref="TurnPriority.Protected"/> 转向因具最高优先级而不被冲突阻断；
    /// ③<b>目标车道容量</b>（T1.3 e）——<paramref name="targetLaneHasSpace"/> 为 <c>false</c> 时拒绝。</para>
    /// <para><b>occupiedTurnIds 的含义</b>：调用方传入"当前已放行 / 正占用路口的流向"集合；
    /// 只要其中包含已放行的 <see cref="TurnPriority.Protected"/> 流向，即落实了
    /// §4.6「与已放行的 Protected 流向不冲突（Yield 须让行 Protected）」。</para>
    /// </remarks>
    /// <param name="nodeId">路口节点 id。</param>
    /// <param name="turnId">待进入的转向 id。</param>
    /// <param name="stage">当前相位。</param>
    /// <param name="occupiedTurnIds">当前占用路口的流向 id 集合（可为 null = 视为空）。</param>
    /// <param name="targetLaneHasSpace">目标车道是否还有空位（由调用方按下游队列判定）。</param>
    /// <returns>允许驶入为 <c>true</c>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stage"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">任一转向 id 或 <paramref name="nodeId"/> 越界。</exception>
    internal bool CanEnter(
        int nodeId,
        int turnId,
        Stage stage,
        IReadOnlyList<int>? occupiedTurnIds,
        bool targetLaneHasSpace)
    {
        ArgumentNullException.ThrowIfNull(stage);
        CheckNode(nodeId);
        CheckTurn(turnId);

        // ① 相位掩码。
        if (stage.PriorityOf(turnId) == TurnPriority.Banned)
        {
            return false;
        }

        // ③ 目标车道容量（T1.3 e）。
        if (!targetLaneHasSpace)
        {
            return false;
        }

        // ② 冲突仲裁（T1.3 d）：仅当占用方向优先级更高（Yield 让行 Protected）才阻断。
        if (occupiedTurnIds is not null)
        {
            TurnPriority mine = stage.PriorityOf(turnId);
            for (int i = 0; i < occupiedTurnIds.Count; i++)
            {
                int other = occupiedTurnIds[i];
                CheckTurn(other);

                if (!_conflicts.AreConflicting(turnId, other))
                {
                    continue;
                }

                if (stage.PriorityOf(other) > mine)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private void CheckNode(int nodeId)
    {
        if ((uint)nodeId >= (uint)_network.NodeCount)
        {
            throw new ArgumentOutOfRangeException(nameof(nodeId), nodeId, "节点 id 越界。");
        }
    }

    private void CheckTurn(int turnId)
    {
        if ((uint)turnId >= (uint)_network.TurnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(turnId), turnId, "转向 id 越界。");
        }
    }
}
