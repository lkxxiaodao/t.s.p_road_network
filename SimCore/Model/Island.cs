namespace SimCore.Model;

/// <summary>
/// 行人二次过街的**中央安全岛**（§3.2 <c>islands[]</c>，2026-10-24 新增，属 E92 G2 的契约扩展）。
/// </summary>
/// <remarks>
/// <para><b>为什么需要它</b>：宽路口的一次过街往往一个行人相位走不完，现实做法是在中央设安全岛，
/// 行人先过半幅、在岛上等待、再由第二个相位放行走完。要让"两段能被不同相位分别放行"成立，
/// 两段必须是<b>两条独立的 <see cref="Turn"/></b>（各自能被 <see cref="Stage"/> 引用）；
/// 而"岛在哪、能站多少人"需要独立承载 —— 就是这个类型。</para>
///
/// <para><b>与 turns 的关联</b>：分段过街的两条 Crosswalk 转向都带同一个 <see cref="Turn.IslandId"/>，
/// 用 <see cref="Turn.Leg"/> 区分前段（0）/ 后段（1）。它们共用同样的 <see cref="Turn.SrcLane"/> 与
/// <see cref="Turn.DstLane"/>（几何上都是"跨这条人行横道"），区别只在行人走哪一半：
/// 前段走 0 → 岛，后段走 岛 → 对岸。</para>
///
/// <para><b>坐标</b>：<see cref="X"/> / <see cref="Y"/> 与 <see cref="Node"/> 同一坐标系（世界米，Y 轴向上），
/// 只给渲染与"岛落在过街线上"的几何校验用，**不参与**仿真推进。</para>
///
/// <para>本类型为纯数据载体，不含任何仿真逻辑。</para>
/// </remarks>
public sealed class Island
{
    /// <summary>安全岛 id（§3.2 <c>islands[].id</c>；同一关卡内唯一）。</summary>
    public int Id { get; init; }

    /// <summary>中心点 X（§3.2 <c>islands[].x</c>，世界米）。</summary>
    public double X { get; init; }

    /// <summary>中心点 Y（§3.2 <c>islands[].y</c>，世界米）。</summary>
    public double Y { get; init; }

    /// <summary>可同时容纳的行人数（§3.2 <c>islands[].capacity</c>；≥ 1）。</summary>
    public int Capacity { get; init; }
}
