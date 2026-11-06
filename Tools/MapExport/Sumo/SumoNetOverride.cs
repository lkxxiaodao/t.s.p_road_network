using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace SignalPlanner.MapExport.Sumo;

/// <summary>
/// 网络覆盖层（S4）：在 netconvert 生成 net.xml **之后**、回写配时**之前**，
/// 按专业档的 <c>overrides</c> 就地改三类属性：
///
/// <list type="bullet">
///   <item><c>overrides.junctions</c> → <c>&lt;junction type&gt;</c>（优先让行 / 全向停车 / 无控制）</item>
///   <item><c>overrides.lanes</c> → <c>&lt;lane allow / speed / width&gt;</c></item>
///   <item><c>overrides.connections</c> → <c>&lt;connection allow / speed&gt;</c></item>
/// </list>
///
/// <para><b>为什么必须在配时回写之前</b>：<see cref="SumoTlLogicPatcher"/> 要按最终的 link 表
/// 生成 state 串。覆盖层不改变 linkIndex（见下面铁律），但顺序固定下来以后就不用再讨论"谁先谁后"。</para>
///
/// <para><b>★ 三条实测铁律（2026-11-06 真跑 SUMO 撞出来的，别改回去）：</b></para>
/// <list type="number">
///   <item><b>绝不能删 &lt;connection&gt; 来表示"禁止通行"</b> —— junction 的
///     <c>&lt;request&gt;</c> 表与连接强耦合，删完 SUMO 直接**段错误**（exit 139）。
///     禁用改用 <c>allow="pedestrian"</c>：任何机动车/自行车都不再走这条连接，结构不动。</item>
///   <item><b>写 <c>allow</c> 必须同时删掉 <c>disallow</c></b> —— 两个并存时 SUMO 报
///     "SVCPermissions must be specified either via 'allow' or 'disallow'"，然后**忽略** disallow，
///     行为变得不可预期（实测：driving lane 原带一长串 disallow，追加 allow 后语义不明）。</item>
///   <item><b>把一条 edge 的所有车道都限成某类车型，会让别的车型"无处发车"</b> ——
///     SUMO 直接 `Error: Vehicle 'x' is not allowed to depart on any lane of edge ...` 退出。
///     这是用户配置问题，导出端只能警告，不能替他改。</item>
/// </list>
///
/// <para><b>没有 overrides 时一个字节都不改</b>（提前 return，不重写文件），
/// 基础模式产物因此逐字节不变。</para>
/// </summary>
public static class SumoNetOverride
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private const string AccessBus = "bus";
    private const string AccessBicycle = "bicycle";
    private const string AccessPedestrian = "pedestrian";
    private const string AccessNone = "none";

    private static readonly HashSet<string> LaneAccess = new(StringComparer.Ordinal)
    {
        AccessBus, AccessBicycle, AccessPedestrian,
    };

    private static readonly HashSet<string> TurnAccess = new(StringComparer.Ordinal)
    {
        AccessBus, AccessNone,
    };

    private static readonly HashSet<string> JunctionTypes = new(StringComparer.Ordinal)
    {
        "priority", "right_before_left", "unregulated",
    };

    /// <summary>
    /// 应用覆盖层。返回给人看的告警（不抛异常；文件写失败才算失败，由调用方兜）。
    /// </summary>
    public static IReadOnlyList<string> Apply(string netXmlPath, ExportGraph graph, string? profileJson)
    {
        var warnings = new List<string>();
        OverridesDto? overrides = ParseOverrides(profileJson, warnings);
        if (overrides == null)
        {
            return warnings;
        }

        bool hasJunctions = overrides.Junctions is { Count: > 0 };
        bool hasLanes = overrides.Lanes is { Count: > 0 };
        bool hasConnections = overrides.Connections is { Count: > 0 };
        if (!hasJunctions && !hasLanes && !hasConnections)
        {
            return warnings;                 // ★ 没有覆盖 → 不动文件（基础模式逐字节不变）
        }

        XDocument doc = XDocument.Load(netXmlPath, LoadOptions.PreserveWhitespace);
        Dictionary<string, int> netLaneToSpmap = SumoNetIndex.LaneMap(doc, graph);
        var spmapLaneToNet = Reverse(netLaneToSpmap);
        var turnByLanePair = TurnByLanePair(graph);
        bool changed = false;

        if (hasJunctions)
        {
            changed |= ApplyJunctions(doc, overrides.Junctions!, warnings);
        }

        if (hasLanes)
        {
            changed |= ApplyLanes(doc, overrides.Lanes!, spmapLaneToNet, warnings);
        }

        if (hasConnections)
        {
            changed |= ApplyConnections(doc, overrides.Connections!, spmapLaneToNet, turnByLanePair, warnings);
        }

        if (!changed)
        {
            return warnings;
        }

        using var writer = XmlWriter.Create(netXmlPath, new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "    ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
            Encoding = Utf8NoBom,
            OmitXmlDeclaration = false,
        });
        doc.Save(writer);
        return warnings;
    }

    // ============================================================
    // 三类应用
    // ============================================================

    private static bool ApplyJunctions(XDocument doc, List<JunctionDto> rows, List<string> warnings)
    {
        var byId = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (XElement junction in doc.Root?.Elements("junction") ?? Enumerable.Empty<XElement>())
        {
            string? id = (string?)junction.Attribute("id");
            if (!string.IsNullOrEmpty(id))
            {
                byId[id!] = junction;
            }
        }

        // net.xml 里哪些 junction 被 tlLogic 控制（= 信号路口）
        var signalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (XElement tlLogic in doc.Root?.Elements("tlLogic") ?? Enumerable.Empty<XElement>())
        {
            string? id = (string?)tlLogic.Attribute("id");
            if (!string.IsNullOrEmpty(id))
            {
                signalized.Add(id!);
            }
        }

        bool changed = false;
        foreach (JunctionDto row in rows)
        {
            int nodeId = row.Node ?? -1;
            string type = (row.Type ?? string.Empty).Trim();
            if (nodeId < 0)
            {
                warnings.Add("路口覆盖：缺少节点 id，已跳过。");
                continue;
            }

            if (!JunctionTypes.Contains(type))
            {
                warnings.Add($"路口 #{nodeId}：不认识的控制方式「{row.Type}」，已跳过。");
                continue;
            }

            string key = nodeId.ToString(CultureInfo.InvariantCulture);
            if (!byId.TryGetValue(key, out XElement? junction))
            {
                warnings.Add($"路口 #{nodeId}：net.xml 里没有同名路口（多半是度数不足、没生成路口），已跳过。");
                continue;
            }

            // ★ 信号路口不许改：改了 tlLogic 还在，但 junction 不再受它控制 →
            //   配时**静默失效**，用户只会看到"车不按灯走"。路径引导：先在节点类型里改成普通路口。
            if (signalized.Contains(key))
            {
                warnings.Add($"路口 #{nodeId}：是信号路口，控制方式不由这里决定 —— "
                             + $"要关掉信号请先把节点类型改成「普通路口」，已跳过。");
                continue;
            }

            if (string.Equals((string?)junction.Attribute("type"), type, StringComparison.Ordinal))
            {
                continue;
            }

            junction.SetAttributeValue("type", type);
            changed = true;
        }

        return changed;
    }

    private static bool ApplyLanes(
        XDocument doc,
        List<LaneDto> rows,
        Dictionary<int, string> spmapToNet,
        List<string> warnings)
    {
        var byNetId = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (XElement lane in doc.Descendants("lane"))
        {
            string? id = (string?)lane.Attribute("id");
            if (!string.IsNullOrEmpty(id))
            {
                byNetId[id!] = lane;
            }
        }

        bool changed = false;
        foreach (LaneDto row in rows)
        {
            int laneId = row.Lane ?? -1;
            if (laneId < 0)
            {
                warnings.Add("车道覆盖：缺少车道 id，已跳过。");
                continue;
            }

            if (!spmapToNet.TryGetValue(laneId, out string? netLaneId) ||
                !byNetId.TryGetValue(netLaneId, out XElement? lane))
            {
                warnings.Add($"车道 #{laneId}：net.xml 里找不到对应车道，已跳过。");
                continue;
            }

            string access = (row.Access ?? string.Empty).Trim();
            if (access.Length > 0)
            {
                if (!LaneAccess.Contains(access))
                {
                    warnings.Add($"车道 #{laneId}：不认识的允许车型「{row.Access}」，已跳过这一项。");
                }
                else
                {
                    changed |= SetAllow(lane, access);
                }
            }

            if (row.Speed is > 0.0)
            {
                changed |= SetDouble(lane, "speed", row.Speed.Value);
            }

            if (row.Width is > 0.0)
            {
                changed |= SetDouble(lane, "width", row.Width.Value);
            }
        }

        return changed;
    }

    private static bool ApplyConnections(
        XDocument doc,
        List<ConnectionDto> rows,
        Dictionary<int, string> spmapToNet,
        Dictionary<(int Src, int Dst), int> turnByLanePair,
        List<string> warnings)
    {
        // (fromNetLane, toNetLane) → connection 元素
        var byLanePair = new Dictionary<(string From, string To), XElement>();
        foreach (XElement connection in doc.Root?.Elements("connection") ?? Enumerable.Empty<XElement>())
        {
            string? fromEdge = (string?)connection.Attribute("from");
            string? toEdge = (string?)connection.Attribute("to");
            if (fromEdge == null || toEdge == null)
            {
                continue;                            // 内部边补全用的连接，跳过
            }

            string from = fromEdge + "_" + (string?)connection.Attribute("fromLane");
            string to = toEdge + "_" + (string?)connection.Attribute("toLane");
            byLanePair[(from, to)] = connection;
        }

        var pairByTurn = new Dictionary<int, (int Src, int Dst)>();
        foreach (((int Src, int Dst) pair, int id) in turnByLanePair)
        {
            pairByTurn[id] = pair;
        }

        bool changed = false;
        foreach (ConnectionDto row in rows)
        {
            int turnId = row.Turn ?? -1;
            if (turnId < 0)
            {
                warnings.Add("连接覆盖：缺少连接 id，已跳过。");
                continue;
            }

            if (!pairByTurn.TryGetValue(turnId, out (int Src, int Dst) pair))
            {
                warnings.Add($"连接 #{turnId}：找不到对应的转向（已被删除？），已跳过。");
                continue;
            }

            if (!spmapToNet.TryGetValue(pair.Src, out string? fromNet) ||
                !spmapToNet.TryGetValue(pair.Dst, out string? toNet))
            {
                warnings.Add($"连接 #{turnId}：进口/出口车道在 net.xml 里找不到映射，已跳过。");
                continue;
            }

            if (!byLanePair.TryGetValue((fromNet, toNet), out XElement? connection))
            {
                warnings.Add($"连接 #{turnId}：net.xml 里没有这条连接（可能没被导出），已跳过。");
                continue;
            }

            string access = (row.Access ?? string.Empty).Trim();
            if (access.Length > 0)
            {
                if (!TurnAccess.Contains(access))
                {
                    warnings.Add($"连接 #{turnId}：不认识的通行限制「{row.Access}」，已跳过这一项。");
                }
                else
                {
                    // ★ 铁律 1：不能删 connection（SUMO 段错误）——
                    //   "禁止机动车" = allow="pedestrian"，结构不动、车不进来。
                    string allow = access == AccessNone ? AccessPedestrian : AccessBus;
                    changed |= SetAllow(connection, allow);
                }
            }

            if (row.Speed is > 0.0)
            {
                changed |= SetDouble(connection, "speed", row.Speed.Value);
            }
        }

        return changed;
    }

    // ============================================================
    // 属性写入
    // ============================================================

    /// <summary>
    /// 写 allow 并**删掉 disallow**（铁律 2）。值没变就不算改动。
    /// </summary>
    private static bool SetAllow(XElement element, string access)
    {
        bool changed = false;
        if (!string.Equals((string?)element.Attribute("allow"), access, StringComparison.Ordinal))
        {
            element.SetAttributeValue("allow", access);
            changed = true;
        }

        if (element.Attribute("disallow") != null)
        {
            element.Attribute("disallow")!.Remove();
            changed = true;
        }

        return changed;
    }

    private static bool SetDouble(XElement element, string name, double value)
    {
        string text = value.ToString("0.##", CultureInfo.InvariantCulture);
        if (string.Equals((string?)element.Attribute(name), text, StringComparison.Ordinal))
        {
            return false;
        }

        element.SetAttributeValue(name, text);
        return true;
    }

    // ============================================================

    /// <summary>(src 车道, dst 车道) → turn id。与 <see cref="SumoNetIndex.LinkMap"/> 同口径：
    /// 分段过街只认 leg=0 那条（它才是 <c>pedestrians[].crosswalk_turn</c> 指向的）。</summary>
    private static Dictionary<(int Src, int Dst), int> TurnByLanePair(ExportGraph graph)
    {
        var map = new Dictionary<(int Src, int Dst), int>();
        foreach (ExportTurn turn in graph.Turns.Where(t => !t.IsCrosswalk))
        {
            map[(turn.Src.Id, turn.Dst.Id)] = turn.Id;
        }

        foreach (ExportTurn turn in graph.Turns.Where(t => t.IsCrosswalk && t.Leg <= 0))
        {
            map[(turn.Src.Id, turn.Dst.Id)] = turn.Id;
        }

        return map;
    }

    private static Dictionary<int, string> Reverse(Dictionary<string, int> netToSpmap)
    {
        var result = new Dictionary<int, string>();
        foreach ((string netLane, int spmapLane) in netToSpmap)
        {
            // 一条 spmap 车道理论上只对应一条 net 车道；多个时取 id 最小的，保证确定性。
            if (!result.TryGetValue(spmapLane, out string? existing) ||
                string.CompareOrdinal(netLane, existing) < 0)
            {
                result[spmapLane] = netLane;
            }
        }

        return result;
    }

    private static OverridesDto? ParseOverrides(string? profileJson, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(profileJson))
        {
            return null;
        }

        try
        {
            OverridesDto? overrides = JsonSerializer.Deserialize<ProfileDto>(profileJson, ReadOptions)?.Overrides;
            return overrides;
        }
        catch (JsonException ex)
        {
            warnings.Add("专业档解析失败，网络覆盖层未应用：" + ex.Message);
            return null;
        }
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class ProfileDto
    {
        [JsonPropertyName("overrides")]
        public OverridesDto? Overrides { get; set; }
    }

    private sealed class OverridesDto
    {
        [JsonPropertyName("junctions")] public List<JunctionDto>? Junctions { get; set; }
        [JsonPropertyName("lanes")] public List<LaneDto>? Lanes { get; set; }
        [JsonPropertyName("connections")] public List<ConnectionDto>? Connections { get; set; }
    }

    private sealed class JunctionDto
    {
        [JsonPropertyName("node")] public int? Node { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
    }

    private sealed class LaneDto
    {
        [JsonPropertyName("lane")] public int? Lane { get; set; }
        [JsonPropertyName("access")] public string? Access { get; set; }
        [JsonPropertyName("speed")] public double? Speed { get; set; }
        [JsonPropertyName("width")] public double? Width { get; set; }
    }

    private sealed class ConnectionDto
    {
        [JsonPropertyName("turn")] public int? Turn { get; set; }
        [JsonPropertyName("access")] public string? Access { get; set; }
        [JsonPropertyName("speed")] public double? Speed { get; set; }
    }
}
