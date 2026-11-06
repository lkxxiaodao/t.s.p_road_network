using SimCore.Model;

namespace SimCore.Io;

/// <summary>
/// 一张关卡的内存模型 = <c>.spmap</c> 文件（技术方案03 §3.2 顶层结构）的完整载体。
/// </summary>
/// <remarks>
/// <para>结构与 §3.2 逐键对应：
/// <list type="bullet">
///   <item><see cref="FormatVersion"/> ← <c>format_version</c></item>
///   <item><see cref="Meta"/> ← <c>meta</c></item>
///   <item><see cref="Network"/> ← <c>nodes / lanes / turns</c> 三表（信号 <c>signals[]</c> 挂在节点上，见 <see cref="Node.Offset"/> / <see cref="Node.SignalPlans"/>）</item>
///   <item><see cref="TurnRatios"/> ← <c>turns[].ratio</c>（可选字段，默认 1.0）</item>
///   <item><see cref="Demand"/> ← <c>demand[]</c></item>
///   <item><see cref="Pedestrians"/> ← <c>pedestrians[]</c></item>
///   <item><see cref="Bicycles"/> ← <c>bicycles[]</c></item>
///   <item><see cref="GameRules"/> ← <c>game_rules</c></item>
/// </list>
/// </para>
/// <para>本类型是 <see cref="SimCore.Model"/> 之上的<b>关卡装配层</b>：把"地图是什么"（<see cref="Network"/>）
/// 与"这一关的需求 / 评分规则"（其余字段）打包成仿真核的输入。纯数据载体，不含仿真逻辑，也不做合法性校验
/// （校验见 <see cref="SpmapValidator"/>）。</para>
/// <para>依赖方向：<c>Model ← Io</c>（见 Gate 0 契约）。</para>
/// </remarks>
public sealed class Level
{
    private static readonly TurnRatio[] NoTurnRatios = Array.Empty<TurnRatio>();
    private static readonly DemandPlan[] NoDemand = Array.Empty<DemandPlan>();
    private static readonly PedestrianDemand[] NoPedestrians = Array.Empty<PedestrianDemand>();
    private static readonly BicycleDemand[] NoBicycles = Array.Empty<BicycleDemand>();

    /// <summary>格式版本（§3.2 <c>format_version</c>）。当前仅支持 <see cref="Spmap.CurrentFormatVersion"/>。</summary>
    public int FormatVersion { get; init; } = Spmap.CurrentFormatVersion;

    /// <summary>关卡元数据（§3.2 <c>meta</c>）。</summary>
    public LevelMeta Meta { get; init; } = new();

    /// <summary>路网（§3.2 <c>nodes / lanes / turns</c> + 节点的信号属性）。</summary>
    public Network Network { get; init; } = new();

    /// <summary>
    /// 每条转向的流量分配比例（§3.2 <c>turns[].ratio</c>，可选，缺省 1.0）——即 LightSim
    /// <c>Movement.turn_ratio</c>（技术方案01 §3.1/§3.6）。<see cref="SimCore.Model.Turn"/> 属冻结模型、不含该字段，
    /// 故在关卡层以"转向 id → 比例"旁挂承载。仅当比例 ≠ 1.0 时才会写入文件。
    /// </summary>
    public IReadOnlyList<TurnRatio> TurnRatios { get; init; } = NoTurnRatios;

    /// <summary>机动车需求曲线（§3.2 <c>demand[]</c>）。</summary>
    public IReadOnlyList<DemandPlan> Demand { get; init; } = NoDemand;

    /// <summary>行人过街需求（§3.2 <c>pedestrians[]</c>）。</summary>
    public IReadOnlyList<PedestrianDemand> Pedestrians { get; init; } = NoPedestrians;

    /// <summary>非机动车需求（§3.2 <c>bicycles[]</c>）。</summary>
    public IReadOnlyList<BicycleDemand> Bicycles { get; init; } = NoBicycles;

    /// <summary>游戏扩展规则（§3.2 <c>game_rules</c>：锚点 / 权重 / 意外元素 / 教学）。</summary>
    public GameRules GameRules { get; init; } = new();

    /// <summary>取某条转向的流量比例（无双向匹配则返回 1.0）。</summary>
    /// <param name="turnId">转向 id。</param>
    /// <returns>该转向的比例；未显式给出时为 1.0。</returns>
    public double TurnRatioOf(int turnId)
    {
        for (int i = 0; i < TurnRatios.Count; i++)
        {
            if (TurnRatios[i].Turn == turnId)
            {
                return TurnRatios[i].Ratio;
            }
        }

        return 1.0;
    }
}

/// <summary>一条转向的流量分配比例（§3.2 <c>turns[].ratio</c>）。</summary>
/// <param name="Turn">转向 id。</param>
/// <param name="Ratio">占上游流量的比例，要求落在 (0, 1]。</param>
public readonly record struct TurnRatio(int Turn, double Ratio);
