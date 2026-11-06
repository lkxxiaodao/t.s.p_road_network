using System.Text.Json.Nodes;

namespace SimCore.Io;

/// <summary>
/// 游戏扩展规则（技术方案03 §3.2 <c>game_rules</c>）：
/// 评分锚点、权重覆盖、变体意外元素、教学步骤。游戏专属，仿真核不消费、评分与 UI 消费。
/// </summary>
/// <remarks>
/// <para><see cref="Events"/>（意外元素）与 <see cref="Tutorial"/>（教学步骤）在 §3.2 中只给出空数组占位、
/// 未定义字段——本任务<b>不臆造字段</b>，以原始 JSON 节点原样承载，保证任何未来字段都能无损往返。</para>
/// <para>纯数据载体。</para>
/// </remarks>
public sealed class GameRules
{
    /// <summary>四项指标的评分锚点（§3.2 <c>game_rules.anchors</c>）。</summary>
    public MetricAnchors Anchors { get; init; } = new();

    /// <summary>四项指标的权重（§3.2 <c>game_rules.weights</c>）。</summary>
    public MetricWeights Weights { get; init; } = MetricWeights.Default;

    /// <summary>
    /// 车型构成预设（<c>game_rules.vehicle_mix</c>；T4.3 · S1）。
    /// <para><c>null</c> = 未设置，语义等同 <see cref="VehicleMix.CarOnly"/>（此时不写文件，保证旧关卡往返不变）。</para>
    /// </summary>
    public VehicleMix? VehicleMix { get; init; }

    /// <summary>变体关"小意外"元素（§3.2 <c>game_rules.events</c>；游戏设计v2 §5.0）。原始 JSON，缺省空数组。</summary>
    public JsonNode? Events { get; init; } = new JsonArray();

    /// <summary>教学步骤（§3.2 <c>game_rules.tutorial</c>；游戏设计v2 §8.6）。原始 JSON，缺省空数组。</summary>
    public JsonNode? Tutorial { get; init; } = new JsonArray();
}

/// <summary>
/// 单项指标的评分锚点（游戏设计v2 §6.3）。
/// <para>JSON 形态为二元数组 <c>[b, g]</c>（§3.2 示例 <c>"R":[0.8,1.0]</c> 即 b=0.8、g=1.0）。</para>
/// </summary>
/// <param name="Bottom">底线值 b：达到即 0 分，劣于底线同样 0 分。</param>
/// <param name="Good">优秀值 g：达到即满分。</param>
public readonly record struct MetricAnchorValue(double Bottom, double Good)
{
    /// <summary>未标定的锚点（b=g=0）。</summary>
    public static MetricAnchorValue Unset => default;

    /// <summary>是否尚未标定。</summary>
    public bool IsUnset => Bottom == 0d && Good == 0d;
}

/// <summary>
/// 四项指标的评分锚点集合（§3.2 <c>game_rules.anchors</c>）。
/// <para>指标口径见游戏设计v2 §6.2：R 通行服务率（越大越好）、D 平均延误 / P 平均停车次数 / Q 最长排队（越小越好）。</para>
/// </summary>
public sealed class MetricAnchors
{
    /// <summary>通行服务率 R 的锚点（越大越好）。</summary>
    public MetricAnchorValue R { get; init; }

    /// <summary>平均延误 D 的锚点（越小越好）。</summary>
    public MetricAnchorValue D { get; init; }

    /// <summary>平均停车次数 P 的锚点（越小越好）。</summary>
    public MetricAnchorValue P { get; init; }

    /// <summary>最长排队 Q 的锚点（越小越好）。</summary>
    public MetricAnchorValue Q { get; init; }

    /// <summary>四项锚点是否都未标定。</summary>
    public bool AllUnset => R.IsUnset && D.IsUnset && P.IsUnset && Q.IsUnset;
}

/// <summary>
/// 四项指标的权重（§3.2 <c>game_rules.weights</c>；游戏设计v2 §6.2 默认 0.4 / 0.2 / 0.2 / 0.2）。
/// </summary>
public sealed class MetricWeights
{
    /// <summary>通行服务率 R 权重。</summary>
    public double R { get; init; }

    /// <summary>平均延误 D 权重。</summary>
    public double D { get; init; }

    /// <summary>平均停车次数 P 权重。</summary>
    public double P { get; init; }

    /// <summary>最长排队 Q 权重。</summary>
    public double Q { get; init; }

    /// <summary>全关卡统一默认权重（游戏设计v2 §6.2）。</summary>
    public static MetricWeights Default => new() { R = 0.4, D = 0.2, P = 0.2, Q = 0.2 };

    /// <summary>四项权重之和。</summary>
    public double Sum => R + D + P + Q;
}

/// <summary>
/// 车型构成预设（<c>game_rules.vehicle_mix</c>；T4.3 · S1）。
/// </summary>
/// <remarks>
/// <para><b>关卡级、只允许选预设</b>，不允许自定义比例与逐车道配置（老板 2026-10-19 裁定）。</para>
/// <para>比例表与仿真影响见 <c>SimCore.Sim.VehicleMixRules</c>（S1b）：
/// 预设只影响「车长 → 队列容量」与「速度系数 → 行程时间」，不改变放行规则。</para>
/// <para><b>兼容性</b>：文件里存的是成员名，故只许<b>末尾追加</b>，不得改名 / 调序 / 复用已废弃的名字。</para>
/// </remarks>
public enum VehicleMix
{
    /// <summary>纯小汽车：小汽车 100%（= 未设置时的语义，也是历史关卡的默认）。</summary>
    CarOnly,

    /// <summary>城市混合：小汽车 85% / 货车 12% / 公交 3%。</summary>
    CityMix,

    /// <summary>货运为主：小汽车 55% / 货车 45% / 公交 0。</summary>
    FreightHeavy,

    /// <summary>公交偏多：小汽车 70% / 货车 10% / 公交 20%。</summary>
    BusHeavy,

    /// <summary>小汽车+少量公交：小汽车 92% / 货车 0 / 公交 8%（2026-10-20 追加）。</summary>
    CarBusMix,
}
