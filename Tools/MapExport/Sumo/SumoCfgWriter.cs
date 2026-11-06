using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace SignalPlanner.MapExport.Sumo;

/// <summary>
/// 生成 <c>&lt;base&gt;.sumocfg</c>（S5：运行配置 + 一键试跑）。
///
/// <para><b>为什么全部写相对路径</b>：验收口径是「把整个文件夹复制到别处仍能跑」。
/// 所以 net / rou / additional 一律只写**文件名**，靠 SUMO 以 cfg 所在目录为工作目录解析。</para>
///
/// <para><b>additional-files 只挂真实存在的文件</b>：`.tll.xml`（多套信号方案）与
/// `.add.xml`（检测器 / 公交站 / 停车场）都是"有才生成"的，写一个不存在的文件名会让
/// SUMO 直接报 `Could not access configuration file`。</para>
///
/// <para><b>★ 还会校验 .tll.xml 与当前网络是否配套</b>：目录里常有上一次导出留下的旧 tll，
/// 里面的灯 id 在新网络里根本不存在 → SUMO 报 `No initial signal plan loaded for tls 'X'`
/// 然后**不跑**。宁可不挂，也不能让"点一下就出统计"变成一条看不懂的报错。</para>
///
/// <para>参数来自专业档的 <c>config</c> 段（<c>begin / end / step_length / seed / teleport</c>），
/// 缺省值与 <c>SumoProfile.defaults()</c> 一致。</para>
/// </summary>
public static class SumoCfgWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public sealed class Result
    {
        public bool Ok { get; init; }
        public string Path { get; init; } = string.Empty;
        public string Error { get; init; } = string.Empty;
        public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

        internal static Result Failure(string error) => new() { Ok = false, Error = error };
    }

    /// <summary>一份运行配置（已归一化）。</summary>
    internal sealed class Config
    {
        internal double Begin { get; init; }
        internal double End { get; init; } = 3600.0;
        internal double StepLength { get; init; } = 1.0;
        internal int Seed { get; init; } = 42;
        internal double Teleport { get; init; } = 300.0;

        internal static Config FromProfile(string? profileJson)
        {
            if (string.IsNullOrWhiteSpace(profileJson))
            {
                return new Config();
            }

            try
            {
                ConfigDto? dto = JsonSerializer.Deserialize<ProfileDto>(profileJson, ReadOptions)?.Config;
                if (dto == null)
                {
                    return new Config();
                }

                double end = dto.End ?? 3600.0;
                if (end <= 0.0)
                {
                    end = 3600.0;
                }

                double begin = dto.Begin ?? 0.0;
                if (begin < 0.0 || begin >= end)
                {
                    begin = 0.0;
                }

                double step = dto.StepLength ?? 1.0;
                if (step <= 0.0 || step > 10.0)
                {
                    step = 1.0;
                }

                double teleport = dto.Teleport ?? 300.0;

                return new Config
                {
                    Begin = begin,
                    End = end,
                    StepLength = step,
                    // ★ Seed 在 DTO 里是 double?：编辑器那边是 SpinBox（float），
                    //   `JSON.stringify` 会写成 `7.0`，`int?` 接不住 7.0 会抛 JsonException
                    //   → 整个 config 被丢弃、sumocfg 回到默认值（S5 踩过）。
                    Seed = (int)Math.Round(dto.Seed ?? 42.0),
                    // SUMO 里 -1 = 永不传送；0 也是"立即传送"，两者都合法，原样传。
                    Teleport = teleport,
                };
            }
            catch (JsonException)
            {
                return new Config();
            }
        }
    }

    /// <summary>
    /// 写 <c>&lt;outputBase&gt;.sumocfg</c>。要求 <c>.net.xml</c> 与 <c>.rou.xml</c> 都已存在
    /// （没有车流就没得试跑）。
    /// </summary>
    public static Result Write(string outputBase, string? profileJson)
    {
        string netPath = outputBase + ".net.xml";
        string rouPath = outputBase + ".rou.xml";
        if (!File.Exists(netPath))
        {
            return Result.Failure("找不到 .net.xml —— 试跑需要网络，请同时勾上「SUMO .net.xml」。");
        }

        if (!File.Exists(rouPath))
        {
            return Result.Failure("找不到 .rou.xml —— 试跑需要车流，请同时勾上「SUMO .rou.xml」。");
        }

        var warnings = new List<string>();
        string name = Path.GetFileName(outputBase);
        var extras = new List<string>();

        string tllPath = outputBase + ".tll.xml";
        if (File.Exists(tllPath))
        {
            if (TllMatchesNet(tllPath, netPath, out string why))
            {
                extras.Add(name + ".tll.xml");
            }
            else
            {
                warnings.Add("跳过 " + name + ".tll.xml：" + why);
            }
        }

        string addPath = outputBase + ".add.xml";
        if (File.Exists(addPath))
        {
            extras.Add(name + ".add.xml");
        }

        Config cfg = Config.FromProfile(profileJson);
        var input = new XElement("input",
            new XElement("net-file", new XAttribute("value", name + ".net.xml")),
            new XElement("route-files", new XAttribute("value", name + ".rou.xml")));
        if (extras.Count > 0)
        {
            input.Add(new XElement("additional-files",
                new XAttribute("value", string.Join(",", extras))));
        }

        var root = new XElement("configuration",
            input,
            new XElement("time",
                new XElement("begin", new XAttribute("value", F(cfg.Begin))),
                new XElement("end", new XAttribute("value", F(cfg.End))),
                new XElement("step-length", new XAttribute("value", F(cfg.StepLength)))),
            new XElement("processing",
                new XElement("time-to-teleport", new XAttribute("value", F(cfg.Teleport)))),
            new XElement("report",
                new XElement("no-step-log", new XAttribute("value", "true")),
                new XElement("duration-log.statistics", new XAttribute("value", "true"))),
            new XElement("random_number",
                new XElement("seed", new XAttribute("value", cfg.Seed.ToString(CultureInfo.InvariantCulture)))));

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        string path = outputBase + ".sumocfg";
        try
        {
            using var writer = new System.Xml.XmlTextWriter(path, Utf8NoBom)
            {
                Formatting = System.Xml.Formatting.Indented,
                Indentation = 2,
            };
            doc.Save(writer);
        }
        catch (Exception ex)
        {
            return Result.Failure(".sumocfg 写入失败：" + ex.Message);
        }

        if (extras.Count == 0)
        {
            warnings.Add("没有附加文件（没有多套信号方案、也没配检测设施）—— 只跑 net + rou。");
        }

        return new Result { Ok = true, Path = path, Warnings = warnings };
    }

    /// <summary>
    /// `.tll.xml` 里的灯 id 必须都在 net.xml 里。否则 SUMO 会
    /// `No initial signal plan loaded for tls 'X'` 然后不跑。
    /// </summary>
    private static bool TllMatchesNet(string tllPath, string netPath, out string why)
    {
        why = string.Empty;
        try
        {
            var netIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (XElement tl in XDocument.Load(netPath).Root?.Elements("tlLogic")
                         ?? Enumerable.Empty<XElement>())
            {
                string? id = (string?)tl.Attribute("id");
                if (!string.IsNullOrEmpty(id))
                {
                    netIds.Add(id!);
                }
            }

            var tllIds = new List<string>();
            foreach (XElement tl in XDocument.Load(tllPath).Root?.Elements("tlLogic")
                         ?? Enumerable.Empty<XElement>())
            {
                string? id = (string?)tl.Attribute("id");
                if (!string.IsNullOrEmpty(id))
                {
                    tllIds.Add(id!);
                }
            }

            if (tllIds.Count == 0)
            {
                why = "里面没有任何 tlLogic";
                return false;
            }

            var missing = tllIds.Where(id => !netIds.Contains(id)).ToList();
            if (missing.Count > 0)
            {
                why = "这些灯在当前网络里不存在：" + string.Join("、", missing)
                      + " —— 多半是上一次导出留下的旧文件，重新导出一次再试";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            why = "解析失败（" + ex.Message + "）";
            return false;
        }
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class ProfileDto
    {
        [JsonPropertyName("config")]
        public ConfigDto? Config { get; set; }
    }

    /// <summary>★ 字段一律 snake_case（E10 §8-4：驼峰会被 JsonPropertyName 静默丢掉）。</summary>
    private sealed class ConfigDto
    {
        [JsonPropertyName("begin")] public double? Begin { get; set; }
        [JsonPropertyName("end")] public double? End { get; set; }
        [JsonPropertyName("step_length")] public double? StepLength { get; set; }
        [JsonPropertyName("seed")] public double? Seed { get; set; }
        [JsonPropertyName("teleport")] public double? Teleport { get; set; }
    }
}
