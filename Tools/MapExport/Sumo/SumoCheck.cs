using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using SimCore.Geometry;
using SimCore.Io;
using SimCore.Model;

namespace SignalPlanner.MapExport.Sumo;

/// <summary>
/// 专业档体检（S6）：**导出后**读真实产物查 4 条。
///
/// <para>与 GDScript 侧 <c>SumoCheckModel</c> 分工：那边只看 <c>.spmap</c> + <c>.sumo.json</c>
/// 两份数据（导出前就能拦）；这边要看 net.xml 的 link 表、车道长度、冲突矩阵 ——
/// **只有导出之后才有**。</para>
///
/// <para><b>★ 为什么这些规则不能提前</b>：`linkIndex` 是 netconvert 分配的，几何一改就重排；
/// 检测器的合法 `pos` 上限是 **net 车道长度**（netconvert 会裁剪边），不是 `.spmap` 里那个数。</para>
///
/// ## 四条规则
/// | 码 | 级别 | 查什么 |
/// | --- | --- | --- |
/// | `sumo_tls_state_unmapped` | Error | 配了灯态的 turn 在 net 里没有对应 link（灯态不会生效） |
/// | `sumo_tls_never_green` | Error | 某条 link 在所有相位里都没放过绿灯（永远过不去） |
/// | `sumo_detector_out_of_range` | Error | 检测器 / 站台的 pos 超出 net 车道长度 |
/// | `sumo_phase_conflict` | Warning | 同一相位里放行的两条转向互相冲突（同时绿会撞） |
///
/// <para><b>Path 口径</b>：用**文件 id**（导出用的 .spmap 已经 renumber），
/// 形如 <c>nodes[3].sumo</c> / <c>turns[12].sumo</c> / <c>lanes[4].sumo</c> ——
/// 这样「校验问题」浮窗里能点一条跳到画布对象（`EditorApp._resolve_validation_target`）。</para>
/// </summary>
public static class SumoCheck
{
    public const string SevError = "Error";
    public const string SevWarning = "Warning";

    /// <summary>一条体检结论。★ 字段名用 PascalCase —— 与 SimCore 的校验条目同格式。</summary>
    public sealed class Finding
    {
        [JsonPropertyName("Code")] public string Code { get; init; } = string.Empty;
        [JsonPropertyName("Severity")] public string Severity { get; init; } = SevWarning;
        [JsonPropertyName("Message")] public string Message { get; init; } = string.Empty;
        [JsonPropertyName("Path")] public string Path { get; init; } = string.Empty;
    }

    /// <summary>
    /// 跑一遍。**不抛异常**（体检本身出错不该让导出失败），读不到文件就返回空。
    /// </summary>
    /// <param name="outputBase">导出 basename（没有扩展名）。</param>
    /// <param name="graph">同一个 graph（它用的 id 就是文件 id）。</param>
    /// <param name="level">加载好的关卡（算冲突矩阵用）。</param>
    public static IReadOnlyList<Finding> Run(
        string outputBase, ExportGraph graph, Level level, string? profileJson)
    {
        var findings = new List<Finding>();
        if (string.IsNullOrWhiteSpace(profileJson))
        {
            return findings;                 // 基础模式没有专业档，无需体检
        }

        string netPath = outputBase + ".net.xml";
        if (!File.Exists(netPath))
        {
            return findings;
        }

        XDocument doc;
        try
        {
            doc = XDocument.Load(netPath);
        }
        catch
        {
            return findings;
        }

        Dictionary<string, int> netToSpmap = SumoNetIndex.LaneMap(doc, graph);
        Dictionary<string, Dictionary<int, int>> linkMap =
            SumoNetIndex.LinkMap(doc, graph, netToSpmap, includeCrosswalk: true);
        Dictionary<int, List<SumoProfileTls.Program>>? profile = SumoProfileTls.Parse(profileJson);

        CheckTlsLinks(profile, linkMap, findings);
        CheckDetectors(netPath, netToSpmap, profileJson, findings);
        CheckPhaseConflicts(profile, level, findings);
        return findings;
    }

    // ============================================================
    // 1 + 2：灯态映射不上 / 某条 link 从未放行
    // ============================================================

    private static void CheckTlsLinks(
        Dictionary<int, List<SumoProfileTls.Program>>? profile,
        Dictionary<string, Dictionary<int, int>> linkMap,
        List<Finding> findings)
    {
        if (profile == null)
        {
            return;
        }

        foreach ((int nodeId, List<SumoProfileTls.Program> programs) in profile)
        {
            string tlId = nodeId.ToString(CultureInfo.InvariantCulture);
            linkMap.TryGetValue(tlId, out Dictionary<int, int>? linkToTurn);
            var mappedTurns = new HashSet<int>();
            if (linkToTurn != null)
            {
                foreach (int turn in linkToTurn.Values)
                {
                    mappedTurns.Add(turn);
                }
            }

            // --- 1. 配了灯态的 turn 有没有对应 link ---
            var unmapped = new List<int>();
            var stateByTurn = new Dictionary<int, bool>();        // turn → 是否至少一次绿灯
            foreach (SumoProfileTls.Program program in programs)
            {
                foreach (SumoProfileTls.PhaseDto phase in program.Phases ?? new List<SumoProfileTls.PhaseDto>())
                {
                    if (phase.States == null)
                    {
                        continue;
                    }

                    foreach ((string key, string value) in phase.States)
                    {
                        if (!int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int turnId))
                        {
                            continue;
                        }

                        if (!mappedTurns.Contains(turnId))
                        {
                            if (!unmapped.Contains(turnId))
                            {
                                unmapped.Add(turnId);
                            }

                            continue;
                        }

                        bool green = IsGreen(value);
                        stateByTurn[turnId] = stateByTurn.TryGetValue(turnId, out bool was) ? was || green : green;
                    }
                }
            }

            if (unmapped.Count > 0)
            {
                unmapped.Sort();
                findings.Add(new Finding
                {
                    Code = "sumo_tls_state_unmapped",
                    Severity = SevError,
                    Message = $"路口 #{nodeId}：连接 #{Join(unmapped)} 配了灯态，但 net.xml 里没有对应的 link"
                              + "（这些转向可能没被导出）—— 它们的灯态不会生效。",
                    Path = $"nodes[{nodeId}].sumo",
                });
            }

            // --- 2. 某条 link 从未放行 ---
            // ★ 严格版：`states` 在专业档里是**稀疏**的（只存非红，缺省 = 红），
            //   导出时缺的位补 'r' —— 所以"没配过"就等于"从未放行"，也该报。
            var never = new List<int>();
            foreach (int turnId in mappedTurns.OrderBy(t => t))
            {
                if (!stateByTurn.TryGetValue(turnId, out bool hasGreen) || !hasGreen)
                {
                    never.Add(turnId);
                }
            }

            if (never.Count > 0)
            {
                findings.Add(new Finding
                {
                    Code = "sumo_tls_never_green",
                    Severity = SevError,
                    Message = $"路口 #{nodeId}：连接 #{Join(never)} 在所有相位里都没有绿灯（只有红/黄/让行）"
                              + " —— 这些转向的车永远过不去。",
                    Path = never.Count > 0 ? $"turns[{never[0]}].sumo" : $"nodes[{nodeId}].sumo",
                });
            }
        }
    }

    /// <summary>
    /// SUMO 灯态：`G` 保护放行、`g` 让行放行、`y` 黄、`r` 红、`o` 红+黄。
    /// **只有 G / g 算"放行"**。
    /// </summary>
    private static bool IsGreen(string state)
    {
        foreach (char c in state ?? string.Empty)
        {
            if (c is 'G' or 'g')
            {
                return true;
            }
        }

        return false;
    }

    // ============================================================
    // 3. 检测器 / 站台超出 net 车道长度
    // ============================================================

    private static void CheckDetectors(
        string netPath, Dictionary<string, int> netToSpmap,
        string? profileJson, List<Finding> findings)
    {
        // ★ 必须读**专业档原值**：`SumoFacilities.Parse` 会先把 pos 夹进合法区间
        //   （并记一条"已夹取"警告），所以拿它的结果永远查不出越界。
        Dictionary<int, double> laneLength = NetLaneLengths(netPath, netToSpmap);
        if (laneLength.Count == 0)
        {
            return;
        }

        JsonDocument json;
        try
        {
            json = JsonDocument.Parse(profileJson!);
        }
        catch (JsonException)
        {
            return;
        }

        using (json)
        {
            if (!json.RootElement.TryGetProperty("additional", out JsonElement rows) ||
                rows.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (JsonElement row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                int lane = IntOf(row, "lane");
                if (lane < 0 || !laneLength.TryGetValue(lane, out double len) || len <= 0.0)
                {
                    continue;
                }

                string id = StringOf(row, "id");
                if (id.Length == 0)
                {
                    id = StringOf(row, "kind") + "_" + lane;
                }

                double worst = Math.Max(DoubleOf(row, "pos"),
                    Math.Max(DoubleOf(row, "end"), DoubleOf(row, "start")));
                if (worst <= len + 0.05)
                {
                    continue;
                }

                findings.Add(new Finding
                {
                    Code = "sumo_detector_out_of_range",
                    Severity = SevError,
                    Message = $"设施「{id}」的位置 {worst:0.#} m 超出车道 #{lane} 的长度 {len:0.#} m"
                              + " —— SUMO 会报 Invalid position 并拒绝启动（导出时会被夹回边界，但位置不是你要的）。",
                    Path = $"lanes[{lane}].sumo",
                });
            }
        }
    }

    /// <summary>spmap 车道 id → net 车道长度（SUMO 会对边做裁剪，长度与 .spmap 里的不一样）。</summary>
    private static Dictionary<int, double> NetLaneLengths(
        string netPath, Dictionary<string, int> netToSpmap)
    {
        var result = new Dictionary<int, double>();
        try
        {
            foreach (XElement lane in XDocument.Load(netPath).Descendants("lane"))
            {
                string? id = (string?)lane.Attribute("id");
                if (id == null || !netToSpmap.TryGetValue(id, out int spmapLane))
                {
                    continue;
                }

                if (double.TryParse((string?)lane.Attribute("length"), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double len))
                {
                    result[spmapLane] = len;
                }
            }
        }
        catch
        {
            // 读不到就返回空 → 这条规则跳过（体检不该让导出失败）
        }

        return result;
    }

    private static int IntOf(JsonElement row, string name)
        => row.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number
            ? (int)v.GetDouble()
            : -1;

    private static double DoubleOf(JsonElement row, string name)
        => row.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : 0.0;

    private static string StringOf(JsonElement row, string name)
        => row.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? string.Empty)
            : string.Empty;

    // ============================================================
    // 4. 同一相位里放行的转向互相冲突
    // ============================================================

    private static void CheckPhaseConflicts(
        Dictionary<int, List<SumoProfileTls.Program>>? profile, Level level, List<Finding> findings)
    {
        if (profile == null)
        {
            return;
        }

        ConflictMatrix conflicts;
        try
        {
            conflicts = ConflictMatrix.From(level.Network);
        }
        catch
        {
            return;
        }

        foreach ((int nodeId, List<SumoProfileTls.Program> programs) in profile)
        {
            for (int pi = 0; pi < programs.Count; pi++)
            {
                List<SumoProfileTls.PhaseDto> phases =
                    programs[pi].Phases ?? new List<SumoProfileTls.PhaseDto>();
                for (int si = 0; si < phases.Count; si++)
                {
                    var green = new List<int>();
                    Dictionary<string, string>? states = phases[si].States;
                    if (states != null)
                    {
                        foreach ((string key, string value) in states)
                        {
                            if (IsGreen(value) &&
                                int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                    out int turnId))
                            {
                                green.Add(turnId);
                            }
                        }
                    }

                    if (green.Count < 2)
                    {
                        continue;
                    }

                    var pairs = new List<string>();
                    for (int a = 0; a < green.Count; a++)
                    {
                        for (int b = a + 1; b < green.Count; b++)
                        {
                            if (!conflicts.AreConflicting(green[a], green[b]))
                            {
                                continue;
                            }

                            pairs.Add($"#{green[a]}×#{green[b]}");
                            if (pairs.Count >= 3)
                            {
                                break;
                            }
                        }

                        if (pairs.Count >= 3)
                        {
                            break;
                        }
                    }

                    if (pairs.Count == 0)
                    {
                        continue;
                    }

                    findings.Add(new Finding
                    {
                        Code = "sumo_phase_conflict",
                        Severity = SevWarning,
                        Message = $"路口 #{nodeId} 第 {pi + 1} 套方案第 {si + 1} 相位："
                                  + $"{string.Join("、", pairs)} 同时放行但互相冲突 —— 会撞或互相堵死。",
                        Path = $"nodes[{nodeId}].sumo",
                    });
                }
            }
        }
    }

    // ============================================================

    private static string Join(IReadOnlyList<int> ids)
        => string.Join("、", ids.Take(8)) + (ids.Count > 8 ? $" 等 {ids.Count} 条" : string.Empty);
}
