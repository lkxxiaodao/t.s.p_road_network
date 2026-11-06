namespace SimCore.Model;

/// <summary>
/// 有向路段 = 一条车道。字段对齐技术方案03 §3.2 的 <c>lanes[]</c>。
/// </summary>
/// <remarks>
/// <para>拓扑方向：车辆沿 <see cref="FromNode"/> → <see cref="ToNode"/> 行驶；
/// 在 <see cref="ToNode"/> 处可通过某条 <see cref="Turn"/> 转入下一条车道。</para>
/// <para><see cref="Road"/> 为所属道路 id：同一道路的多个车道共享该值，供相位/转向按"进口道"聚合
/// （见 <see cref="Movement"/>）。</para>
/// <para>本类型为纯数据载体，不含任何仿真逻辑。</para>
/// </remarks>
public sealed class Lane
{
    /// <summary>车道 id（§3.2 <c>lanes[].id</c>；编译期要求 0..N-1 连续且唯一）。</summary>
    public int Id { get; init; }

    /// <summary>所属道路 id（§3.2 <c>lanes[].road</c>）。</summary>
    public int Road { get; init; }

    /// <summary>车道类型（§3.2 <c>lanes[].type</c>）。</summary>
    public LaneType Type { get; init; }

    /// <summary>起点节点 id（§3.2 <c>lanes[].from_node</c>）。</summary>
    public int FromNode { get; init; }

    /// <summary>终点节点 id（§3.2 <c>lanes[].to_node</c>）。</summary>
    public int ToNode { get; init; }

    /// <summary>车道长度，米（§3.2 <c>lanes[].length</c>）。</summary>
    public double Length { get; init; }

    /// <summary>限速，米/秒（§3.2 <c>lanes[].speed_limit</c>；默认 13.89 ≈ 50 km/h）。</summary>
    public double SpeedLimit { get; init; }

    /// <summary>同向车道序号（§3.2 <c>lanes[].lane_index</c>）。</summary>
    /// <remarks>
    /// <b>不可改语义</b>：<c>ConflictMatrix</c> 与 <c>Engine</c> 用 <c>(LaneIndex + 0.5) · 车宽</c>
    /// 计算横向偏移与行人过街距离。横向排位请用 <see cref="LatIndex"/>。
    /// </remarks>
    public int LaneIndex { get; init; }

    /// <summary>
    /// 横向排位序号（<c>lanes[].lat_index</c>；T4.3 · S1）：同向从道路中心线向外 0,1,2…，
    /// 覆盖 <c>Driving / Bus / Biking / Sidewalk</c> 全部类型，只给渲染排布用。
    /// <para><c>-1</c> = 未标（旧文件），读入后由编辑器回填；<b>不参与任何仿真计算</b>。</para>
    /// </summary>
    public int LatIndex { get; init; } = -1;
}
