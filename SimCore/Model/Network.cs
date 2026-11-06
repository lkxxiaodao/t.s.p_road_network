namespace SimCore.Model;

/// <summary>
/// 路网（纯拓扑 + 几何 + 信号）。§3.2 的 <c>nodes / lanes / turns</c> 三表 + 挂在节点上的
/// <c>signals</c>，构成仿真核的输入。
/// </summary>
/// <remarks>
/// <para>本类型只承载"地图是什么"（结构）；"地图怎么跑"由 <see cref="SimCore.Sim"/> 承担。
/// §3.2 中面向文件的 <c>format_version</c> / <c>meta</c> 及 <c>demand / pedestrians / bicycles /
/// game_rules</c> 不属本类型——它们由 Io 层（T0.3）组织为"关卡"对象。</para>
/// <para>调用 <see cref="Compile"/> 可把本结构编译成仿真用的扁平结构
/// <see cref="CompiledNetwork"/>（按 id 直寻址 + 预计算图邻居）。</para>
/// <para>本类型为纯数据载体，不含任何仿真逻辑。</para>
/// </remarks>
public sealed class Network
{
    /// <summary>节点集合（§3.2 <c>nodes[]</c>）。</summary>
    public IReadOnlyList<Node> Nodes { get; init; } = Array.Empty<Node>();

    /// <summary>车道集合（§3.2 <c>lanes[]</c>）。</summary>
    public IReadOnlyList<Lane> Lanes { get; init; } = Array.Empty<Lane>();

    /// <summary>转向集合（§3.2 <c>turns[]</c>）。</summary>
    public IReadOnlyList<Turn> Turns { get; init; } = Array.Empty<Turn>();

    /// <summary>
    /// 行人二次过街的中央安全岛（§3.2 <c>islands[]</c>，可选，2026-10-24 新增）。
    /// 旧关卡没有这一项 → 空数组，行为与改动前一致。
    /// </summary>
    public IReadOnlyList<Island> Islands { get; init; } = Array.Empty<Island>();

    /// <summary>
    /// 把本路网编译为仿真用的扁平结构（按 id 直寻址 + 预计算的上下游车道 / 进出转向 /
    /// 每节点转向集合 / 交通流分组）。
    /// </summary>
    /// <returns>不可变的 <see cref="CompiledNetwork"/>。</returns>
    /// <exception cref="ArgumentException">节点 / 车道 / 转向的 id 非 0..N-1 连续或存在重复。</exception>
    /// <exception cref="ArgumentOutOfRangeException">转向 / 车道引用了越界的节点或车道 id。</exception>
    public CompiledNetwork Compile() => CompiledNetwork.Compile(this);
}
