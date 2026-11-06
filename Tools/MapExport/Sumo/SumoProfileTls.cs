using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace SignalPlanner.MapExport.Sumo;

/// <summary>
/// 专业档里 <c>tls</c> 那一段的读取，以及"专业方案 → &lt;tlLogic&gt;"的构造。
///
/// ★ 两处共用（2026-11-04 抽出）：
///   - <see cref="SumoTlLogicPatcher"/>：**单套方案**写进 net.xml 的 <c>programID="0"</c>；
///   - <see cref="SumoTllWriter"/>：**多套方案**才走 .tll.xml（additional），programID 带 pro 前缀。
///
/// ★ 灯态按 **turn id** 存（专业档里也是 turn id），到这里才翻译成 <c>linkIndex</c>：
///   linkIndex 由 netconvert 分配，几何一改就重排；turn id 在 Doc 里稳定。
/// </summary>
internal static class SumoProfileTls
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>解析专业档；没有 tls / 解析失败时返回 null（专业档是旁挂件，坏了不该挡住导出）。</summary>
    public static Dictionary<int, List<Program>>? Parse(string? profileJson)
    {
        if (string.IsNullOrWhiteSpace(profileJson))
        {
            return null;
        }

        ProfileDto? profile;
        try
        {
            profile = JsonSerializer.Deserialize<ProfileDto>(profileJson, ReadOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        if (profile?.Tls == null || profile.Tls.Count == 0)
        {
            return null;
        }

        var result = new Dictionary<int, List<Program>>();
        foreach (TlsDto tls in profile.Tls)
        {
            if (tls.Programs == null || tls.Programs.Count == 0)
            {
                continue;
            }

            result[tls.Node] = tls.Programs
                .OrderBy(p => p.ActivateAt)
                .ThenBy(p => p.Id, StringComparer.Ordinal)
                .ToList();
        }

        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// 该节点是否只有**一套**方案。只有一套时可以直接内嵌进 net.xml
    /// （多套要用 WAUT 分时段切换，只能走 additional，见 <see cref="SumoTllWriter"/>）。
    /// </summary>
    public static bool TryGetSingle(
        Dictionary<int, List<Program>>? profile,
        int nodeId,
        out Program program)
    {
        program = null!;
        if (profile == null || !profile.TryGetValue(nodeId, out List<Program>? programs) || programs.Count != 1)
        {
            return false;
        }

        program = programs[0];
        return true;
    }

    /// <summary>
    /// 构造一条 &lt;tlLogic&gt;。没有相位时返回 null（不产出空程序）。
    /// </summary>
    public static XElement? BuildTlLogic(
        string tlId,
        int linkCount,
        Program program,
        string programId,
        Dictionary<int, int>? linkTurns,
        int nodeId,
        List<string> warnings)
    {
        var element = new XElement("tlLogic",
            new XAttribute("id", tlId),
            new XAttribute("type", string.IsNullOrWhiteSpace(program.Type) ? "static" : program.Type!),
            new XAttribute("programID", programId),
            new XAttribute("offset", F(program.Offset)));

        if (program.Params != null)
        {
            foreach (KeyValuePair<string, string> kv in program.Params)
            {
                element.Add(new XElement("param", new XAttribute("key", kv.Key), new XAttribute("value", kv.Value)));
            }
        }

        var greens = new bool[linkCount];
        var built = new List<XElement>();

        foreach (PhaseDto phase in program.Phases ?? new List<PhaseDto>())
        {
            var state = new char[linkCount];
            for (int i = 0; i < linkCount; i++)
            {
                state[i] = 'r';
            }

            if (phase.States != null && linkTurns != null)
            {
                foreach (KeyValuePair<string, string> kv in phase.States)
                {
                    if (!int.TryParse(kv.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int turnId))
                    {
                        continue;
                    }

                    if (!TryFindLink(linkTurns, turnId, out int linkIndex) || linkIndex < 0 || linkIndex >= linkCount)
                    {
                        continue;
                    }

                    char code = NormalizeState(kv.Value);
                    state[linkIndex] = code;
                    if (code is 'G' or 'g')
                    {
                        greens[linkIndex] = true;
                    }
                }
            }

            var phaseElement = new XElement("phase",
                new XAttribute("duration", F(Math.Max(0.1, phase.Duration))),
                new XAttribute("state", new string(state)));
            if (!string.IsNullOrWhiteSpace(phase.Name))
            {
                phaseElement.SetAttributeValue("name", phase.Name);
            }

            if (phase.MinDur.HasValue)
            {
                phaseElement.SetAttributeValue("minDur", F(Math.Max(0.1, phase.MinDur.Value)));
            }

            if (phase.MaxDur.HasValue)
            {
                phaseElement.SetAttributeValue("maxDur", F(Math.Max(0.1, phase.MaxDur.Value)));
            }

            built.Add(phaseElement);
        }

        if (built.Count == 0)
        {
            warnings.Add($"节点 {nodeId}：方案 {program.Id} 里一个相位都没有，已跳过。");
            return null;
        }

        // ★ 从未放行的 link → SUMO 会在加载时直接报 "Missing green phase" 并拒绝运行。
        //   这里在**第一个相位**补一个绿灯，并如实告警：宁可灯序不完美，也不要交一个跑不起来的文件。
        int fixedCount = 0;
        for (int i = 0; i < linkCount; i++)
        {
            if (greens[i])
            {
                continue;
            }

            char[] chars = ((string)built[0].Attribute("state")!).ToCharArray();
            chars[i] = 'G';
            built[0].SetAttributeValue("state", new string(chars));
            fixedCount++;
        }

        if (fixedCount > 0)
        {
            warnings.Add($"节点 {nodeId}：有 {fixedCount} 个灯位从未放行，已在第 1 相位补绿灯（否则 SUMO 拒绝加载）。");
        }

        foreach (XElement phaseElement in built)
        {
            element.Add(phaseElement);
        }

        return element;
    }

    /// <summary>turn id → linkIndex。一个 turn 可能对应多跳灯，取最小的那个（正常情况一对一）。</summary>
    private static bool TryFindLink(Dictionary<int, int> linkTurns, int turnId, out int linkIndex)
    {
        linkIndex = -1;
        foreach (KeyValuePair<int, int> kv in linkTurns)
        {
            if (kv.Value != turnId)
            {
                continue;
            }

            if (linkIndex < 0 || kv.Key < linkIndex)
            {
                linkIndex = kv.Key;
            }
        }

        return linkIndex >= 0;
    }

    /// <summary>SUMO 灯态只收 G/g/y/r/o/u；认不出来的按红灯处理（保守=安全）。</summary>
    private static char NormalizeState(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return 'r';
        }

        return raw[0] switch
        {
            'G' or 'g' or 'y' or 'r' or 'o' or 'u' or 'O' => raw[0],
            'Y' => 'y',
            'R' => 'r',
            _ => 'r',
        };
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    // ============================================================
    // 专业档里只取 tls 这一块（其余字段不关心）
    // ============================================================

    private sealed class ProfileDto
    {
        [JsonPropertyName("tls")]
        public List<TlsDto>? Tls { get; set; }
    }

    private sealed class TlsDto
    {
        [JsonPropertyName("node")]
        public int Node { get; set; }

        [JsonPropertyName("programs")]
        public List<Program>? Programs { get; set; }
    }

    internal sealed class Program
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("offset")]
        public double Offset { get; set; }

        [JsonPropertyName("activate_at")]
        public double ActivateAt { get; set; }

        [JsonPropertyName("params")]
        public Dictionary<string, string>? Params { get; set; }

        [JsonPropertyName("phases")]
        public List<PhaseDto>? Phases { get; set; }
    }

    internal sealed class PhaseDto
    {
        [JsonPropertyName("duration")]
        public double Duration { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("min_dur")]
        public double? MinDur { get; set; }

        [JsonPropertyName("max_dur")]
        public double? MaxDur { get; set; }

        [JsonPropertyName("states")]
        public Dictionary<string, string>? States { get; set; }
    }
}
