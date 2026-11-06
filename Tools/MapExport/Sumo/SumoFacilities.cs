using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace SignalPlanner.MapExport.Sumo;

/// <summary>
/// `.sumo.json` 的 <c>additional[]</c> → 可导出的设施列表。
///
/// <para><b>为什么单独一层</b>：<see cref="SumoAddWriter"/> 要写 add.xml，
/// <see cref="SumoRouWriter"/> 要按公交站给 flow 插 <c>&lt;stop&gt;</c>，
/// 两边必须用**同一套** id / 车道映射 / 数值夹取，否则"add 里的站在 A 车道、
/// rou 里的 stop 指向 B 车道"这种错会非常难查。</para>
///
/// <para><b>位置口径</b>：<c>pos</c> / <c>start</c> / <c>end</c> 都从<b>车道起点</b>算
/// （SUMO 的口径）。编辑器界面上给的是"离路口多远"，换算在 GDScript 侧完成。</para>
/// </summary>
internal static class SumoFacilities
{
    internal const string KindE1 = "e1";
    internal const string KindE2 = "e2";
    internal const string KindBusStop = "bus_stop";
    internal const string KindParking = "parking";

    /// <summary>一个已解析、车道已映射到 net 的设施。</summary>
    internal sealed class Facility
    {
        public string Id { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;

        /// <summary>.spmap 的车道 id（回显与报错用）。</summary>
        public int Lane { get; init; }

        /// <summary>net.xml 的车道 id（写 add.xml 用）；映射失败时为空。</summary>
        public string NetLane { get; init; } = string.Empty;

        /// <summary>net.xml 的边 id（公交站/停车场判断"路线是否经过"用）。</summary>
        public string NetEdge { get; init; } = string.Empty;

        /// <summary>net 里该车道的长度，用于把 pos / start / end 夹进合法区间。</summary>
        public double LaneLength { get; init; }

        public double Pos { get; init; }
        public double Length { get; init; }
        public double Freq { get; init; }
        public double Start { get; init; }
        public double End { get; init; }
        public string Lines { get; init; } = "bus";
        public double Duration { get; init; }
        public int Capacity { get; init; }

        /// <summary>停车区/公交站要让车停：<see cref="SumoRouWriter"/> 读它决定插不插 stop。</summary>
        public bool Stops { get; init; }
    }

    /// <summary>解析结果：设施列表 + 告警（映射失败、数值被夹取等）。</summary>
    internal sealed class Parsed
    {
        internal List<Facility> All { get; } = new();
        internal List<string> Warnings { get; } = new();

        internal IEnumerable<Facility> Of(string kind) =>
            All.Where(f => string.Equals(f.Kind, kind, StringComparison.Ordinal));
    }

    /// <summary>
    /// 解析专业档里的 <c>additional[]</c>。没有内容时返回空列表（不报错）。
    /// </summary>
    /// <param name="netPath">刚生成的 net.xml，用来拿 lane → edge 映射与车道长度。</param>
    internal static Parsed Parse(string netPath, ExportGraph graph, string? profileJson)
    {
        var result = new Parsed();
        if (string.IsNullOrWhiteSpace(profileJson))
        {
            return result;
        }

        ProfileDto? profile;
        try
        {
            profile = JsonSerializer.Deserialize<ProfileDto>(profileJson, ReadOptions);
        }
        catch (JsonException ex)
        {
            result.Warnings.Add("专业档解析失败，检测设施未导出：" + ex.Message);
            return result;
        }

        if (profile?.Additional is not { Count: > 0 } rows)
        {
            return result;
        }

        Dictionary<int, (string NetLane, string NetEdge, double Length)> byLane;
        try
        {
            byLane = BuildLaneIndex(netPath, graph);
        }
        catch (Exception ex)
        {
            result.Warnings.Add("读 net.xml 建立车道索引失败，检测设施未导出：" + ex.Message);
            return result;
        }

        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (AdditionalDto row in rows)
        {
            string kind = (row.Kind ?? string.Empty).Trim().ToLowerInvariant();
            if (kind is not (KindE1 or KindE2 or KindBusStop or KindParking))
            {
                result.Warnings.Add($"检测设施：未知类型「{row.Kind}」，已跳过。");
                continue;
            }

            int lane = row.Lane ?? -1;
            if (lane < 0)
            {
                result.Warnings.Add($"检测设施 {RowLabel(row)}：没有指定车道，已跳过。");
                continue;
            }

            if (!byLane.TryGetValue(lane, out (string NetLane, string NetEdge, double Length) info))
            {
                result.Warnings.Add($"检测设施 {RowLabel(row)}：车道 #{lane} 在 net.xml 里找不到映射，已跳过。");
                continue;
            }

            double len = Math.Max(info.Length, 0.0);
            string baseId = (row.Id ?? string.Empty).Trim();
            if (baseId.Length == 0)
            {
                baseId = $"{kind}_{lane}";
            }

            // id 唯一：重复时加后缀，不留悬空引用
            string id = baseId;
            int dup = 2;
            while (!usedIds.Add(id))
            {
                id = baseId + "_" + dup.ToString(CultureInfo.InvariantCulture);
                dup++;
            }

            double freq = row.Freq ?? 60.0;
            if (freq <= 0.0)
            {
                freq = 60.0;
            }

            var facility = new Facility
            {
                Id = id,
                Kind = kind,
                Name = (row.Name ?? string.Empty).Trim(),
                Lane = lane,
                NetLane = info.NetLane,
                NetEdge = info.NetEdge,
                LaneLength = len,
                Pos = Clamp(row.Pos ?? 0.0, 0.0, Math.Max(len - 0.1, 0.0), result, id, "位置"),
                Length = Math.Max(row.Length ?? 20.0, 1.0),
                Freq = freq,
                Start = Clamp(row.Start ?? 0.0, 0.0, Math.Max(len - 0.1, 0.0), result, id, "起点"),
                End = Clamp(row.End ?? Math.Min(15.0, len), 0.0, len, result, id, "终点"),
                Lines = string.IsNullOrWhiteSpace(row.Lines) ? "bus" : row.Lines!.Trim(),
                Duration = Math.Max(row.Duration ?? 20.0, 0.0),
                Capacity = Math.Max(row.Capacity ?? 5, 0),
                Stops = row.Stops ?? true,
            };

            // 站台/停车区：起止要有正长度，否则 SUMO 报 "Invalid stop position"
            if (kind is KindBusStop or KindParking && facility.End - facility.Start < 1.0)
            {
                result.Warnings.Add(
                    $"检测设施 {id}：起止只差 {facility.End - facility.Start:0.#} m（至少要 1 m），已跳过。");
                continue;
            }

            // ★ e2 的 pos 只要求 `0 <= pos < laneLength`（**与 length 无关**）。
            //   2026-11-05 实测：pos=85 / length=20 在 81.5 m 的车道上才报错，报的是 pos 越界，
            //   不是"检测区超出"。所以这里**只**信上面 Clamp 做过的 pos 检查 ——
            //   额外加 `pos + length <= len` 会把贴边的合法检测器误杀（S3 自测撞到过）。

            result.All.Add(facility);
        }

        return result;
    }

    private static string RowLabel(AdditionalDto row) =>
        string.IsNullOrWhiteSpace(row.Id) ? $"（{row.Kind}）" : row.Id!;

    private static double Clamp(
        double value, double min, double max, Parsed result, string id, string what)
    {
        if (value < min)
        {
            result.Warnings.Add($"检测设施 {id}：{what} {value:0.#} m 小于下限 {min:0.#}，已夹取。");
            return min;
        }

        if (value > max)
        {
            result.Warnings.Add($"检测设施 {id}：{what} {value:0.#} m 超出车道长度（上限 {max:0.#}），已夹取。");
            return max;
        }

        return value;
    }

    /// <summary>
    /// spmap 车道 id → (net 车道 id, net 边 id, 车道长度)。
    /// 长度取 net.xml 里的 <c>length</c>（不是 spmap 的）——SUMO 会对边做延长/裁剪，
    /// pos 是相对 net 车道算的。
    /// </summary>
    private static Dictionary<int, (string NetLane, string NetEdge, double Length)> BuildLaneIndex(
        string netPath, ExportGraph graph)
    {
        XDocument doc = XDocument.Load(netPath);
        Dictionary<string, int> netToSpmap = SumoNetIndex.LaneMap(doc, graph);

        var netLength = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (XElement lane in doc.Descendants("lane"))
        {
            string? id = (string?)lane.Attribute("id");
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            double length = 0.0;
            string? raw = (string?)lane.Attribute("length");
            if (raw != null)
            {
                double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out length);
            }

            netLength[id] = length;
        }

        var result = new Dictionary<int, (string, string, double)>();
        foreach ((string netLaneId, int spmapLaneId) in netToSpmap)
        {
            string edge = netLaneId;
            int cut = edge.LastIndexOf('_');
            if (cut > 0)
            {
                edge = edge[..cut];
            }

            netLength.TryGetValue(netLaneId, out double length);
            result[spmapLaneId] = (netLaneId, edge, length);
        }

        return result;
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class ProfileDto
    {
        [JsonPropertyName("additional")]
        public List<AdditionalDto>? Additional { get; set; }
    }

    private sealed class AdditionalDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("lane")] public int? Lane { get; set; }
        [JsonPropertyName("pos")] public double? Pos { get; set; }
        [JsonPropertyName("length")] public double? Length { get; set; }
        [JsonPropertyName("freq")] public double? Freq { get; set; }
        [JsonPropertyName("start")] public double? Start { get; set; }
        [JsonPropertyName("end")] public double? End { get; set; }
        [JsonPropertyName("lines")] public string? Lines { get; set; }
        [JsonPropertyName("duration")] public double? Duration { get; set; }
        [JsonPropertyName("capacity")] public int? Capacity { get; set; }
        [JsonPropertyName("stops")] public bool? Stops { get; set; }
    }
}
