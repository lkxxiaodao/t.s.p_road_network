namespace SimCore.Model;

/// <summary>
/// 交通流（进口道级转向组）：同一路口、同一进口道路（<see cref="Road"/>）、同一转向类型
/// <see cref="Type"/> 的全部 <see cref="Turn"/> 的集合。
/// </summary>
/// <remarks>
/// <para>分组口径借 A/B Street 的 <c>MovementID(from_road, turn_type)</c>（技术方案02 §2.2b/§2.3）：
/// 一条多车道进口道上的"左转流"是一个 Movement，其下含各条车道的具体 Turn。
/// 信号相位面板按 Movement 勾选灯组，比逐条 Turn 更贴近工程语义。</para>
/// <para>§3.2 的 <c>turns[]</c> 表不含 movements 表，因此 <see cref="Movement"/> 是
/// <see cref="CompiledNetwork.Compile(Network)"/> 在拓扑编译时从 turns 派生的产物，不由地图直接编写。</para>
/// <para>本类型为纯数据载体，不含任何仿真逻辑。</para>
/// </remarks>
public sealed class Movement
{
    /// <summary>按 (Node, Road, Type) 字典序稳定分配的分组 id（0..M-1）。</summary>
    public int Id { get; }

    /// <summary>所属路口节点 id。</summary>
    public int Node { get; }

    /// <summary>进口道路 id（由各 Turn 源车道的 <see cref="Lane.Road"/> 取值）。</summary>
    public int Road { get; }

    /// <summary>该流统一的转向类型。</summary>
    public TurnType Type { get; }

    /// <summary>该流包含的转向 id 集合（升序、去重）。</summary>
    public IReadOnlyList<int> TurnIds { get; }

    /// <summary>构造一条交通流分组。</summary>
    public Movement(int id, int node, int road, TurnType type, IReadOnlyList<int> turnIds)
    {
        Id = id;
        Node = node;
        Road = road;
        Type = type;
        TurnIds = turnIds ?? Array.Empty<int>();
    }
}
