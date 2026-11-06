using System.Text.Json.Serialization;

namespace SignalPlanner.MapExport;

/// <summary>导出坐标系。OSM 只支持 <see cref="Wgs84"/> / <see cref="Utm"/>；SHP 三者都支持。</summary>
public enum GeoProjectionKind
{
    Wgs84,
    Utm,
    Local,
}

/// <summary>
/// 一次导出的全部选项。由编辑器 GDScript 以 JSON 传入，字段名使用 camelCase。
/// </summary>
public sealed class MapExportOptions
{
    [JsonPropertyName("shp")]
    public bool ExportShapefile { get; init; }

    [JsonPropertyName("net")]
    public bool ExportNetXml { get; init; }

    /// <summary>
    /// 生成 <c>.rou.xml</c>（车型 + 车流 + 行人/自行车）。
    /// <para>★ 依赖 net.xml（要用它把车道映射回边），所以 <see cref="ExportNetXml"/> 必须同时为 true。</para>
    /// </summary>
    [JsonPropertyName("rou")]
    public bool ExportRou { get; init; }

    /// <summary>
    /// 导出检测设施（<c>&lt;base&gt;.add.xml</c>）：E1/E2 检测器、公交站、停车场。
    /// 依赖 net.xml（要用它把车道映射到边），所以必须与 <see cref="ExportNetXml"/> 同开。
    /// </summary>
    [JsonPropertyName("add")]
    public bool ExportAdditional { get; init; }

    /// <summary>
    /// 生成运行配置 <c>&lt;base&gt;.sumocfg</c>（S5）。
    /// <para>★ 依赖 <c>.net.xml</c> + <c>.rou.xml</c>：没有车流就没得试跑，
    /// 缺其中一个会单独报错（不影响其他格式）。</para>
    /// </summary>
    [JsonPropertyName("sumocfg")]
    public bool ExportSumoCfg { get; init; }

    /// <summary>输出 basename 的 OS 绝对路径（不含扩展名）。</summary>
    [JsonPropertyName("outputBase")]
    public string OutputBase { get; init; } = string.Empty;

    /// <summary>原点经度（WGS84，度）。null = 未配置。</summary>
    [JsonPropertyName("originLon")]
    public double? OriginLon { get; init; }

    /// <summary>原点纬度（WGS84，度）。null = 未配置。</summary>
    [JsonPropertyName("originLat")]
    public double? OriginLat { get; init; }

    [JsonPropertyName("projection")]
    public GeoProjectionKind Projection { get; init; } = GeoProjectionKind.Utm;

    [JsonPropertyName("laneWidthM")]
    public double LaneWidthM { get; init; } = 3.5;

    [JsonPropertyName("includeCrosswalks")]
    public bool IncludeCrosswalks { get; init; } = true;

    [JsonPropertyName("netconvertPath")]
    public string NetconvertPath { get; init; } = string.Empty;

    /// <summary>额外的 netconvert 搜索目录（编辑器传入 res://../Tools/sumo/bin 的 OS 路径）。</summary>
    [JsonPropertyName("netconvertSearchDir")]
    public string NetconvertSearchDir { get; init; } = string.Empty;

    [JsonPropertyName("yellowDuration")]
    public double YellowDuration { get; init; } = 3.0;

    [JsonPropertyName("allRedDuration")]
    public double AllRedDuration { get; init; } = 2.0;

    /// <summary>
    /// SUMO 专业档（`.sumo.json`）的 JSON 文本。空 = 基础模式，不生成 `.tll.xml`。
    /// ★ 由编辑器在导出时把专业档按**文件 id** 重映射后传进来（见 `SumoProfile.remapped`）；
    ///   这里不读磁盘，避免"读到的和导出用的不是同一份"。
    /// </summary>
    [JsonPropertyName("sumoProfile")]
    public string SumoProfileJson { get; init; } = string.Empty;

    [JsonIgnore]
    public bool HasGeoOrigin => OriginLon.HasValue && OriginLat.HasValue;
}
