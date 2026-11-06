namespace SimCore.Model;

/// <summary>
/// 路网节点（路口 / 需求源汇）。字段对齐技术方案03 §3.2 的 <c>nodes[]</c> 与 <c>signals[]</c>。
/// </summary>
/// <remarks>
/// <para>几何：<see cref="X"/>/<see cref="Y"/> 为网格坐标（单位与 <see cref="Lane.Length"/> 一致，米）。</para>
/// <para>信号：§3.2 的 <c>signals[] = { node, offset, plans[] }</c> 以 node 为主键。
/// 为使 §3.1 的类型集合保持为 {Network, Node, Lane, Turn, Movement, Stage, SignalPlan}（不额外发明
/// "TrafficSignal" 类型），此处把节点的信号属性直接挂在 <see cref="Node"/> 上：
/// <c>signals[].offset</c> → <see cref="Offset"/>，<c>signals[].plans[]</c> → <see cref="SignalPlans"/>。
/// 非信号节点保留默认值（<see cref="Offset"/>=0、<see cref="SignalPlans"/> 为空）。</para>
/// <para>本类型为纯数据载体，不含任何仿真逻辑；地图合法性校验属 T0.3 的 <c>SpmapValidator</c>，不在此处。</para>
/// </remarks>
public sealed class Node
{
    /// <summary>节点 id（§3.2 <c>nodes[].id</c>；编译期要求同集合内 0..N-1 连续且唯一）。</summary>
    public int Id { get; init; }

    /// <summary>节点类型（§3.2 <c>nodes[].type</c>）。</summary>
    public NodeType Type { get; init; }

    /// <summary>X 坐标（§3.2 <c>nodes[].x</c>）。</summary>
    public double X { get; init; }

    /// <summary>Y 坐标（§3.2 <c>nodes[].y</c>）。</summary>
    public double Y { get; init; }

    /// <summary>信号相位差，秒（§3.2 <c>signals[].offset</c>）。非信号节点为 0。</summary>
    public double Offset { get; init; }

    /// <summary>该节点的信号配时方案集，按时段切换（§3.2 <c>signals[].plans[]</c>）。</summary>
    public IReadOnlyList<SignalPlan> SignalPlans { get; init; } = Array.Empty<SignalPlan>();

    /// <summary>是否为信号路口（<see cref="NodeType.Signalized"/>）。</summary>
    public bool IsSignalized => Type == NodeType.Signalized;
}
