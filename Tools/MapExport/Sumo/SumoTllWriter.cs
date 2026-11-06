using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SignalPlanner.MapExport.Sumo;

/// <summary>
/// 把专业档里的信号方案写成 <c>&lt;base&gt;.tll.xml</c>（additional file）。
///
/// ★ 只处理**多套方案**（2026-11-04 调整）：
///   - 单套方案 → 由 <see cref="SumoTlLogicPatcher"/> 直接写进 net.xml 的 <c>programID="0"</c>，
///     这里**跳过**。理由：两套程序并存时 netedit 只显示网络自带那套，用户在 netedit 里
///     看不到自己的配时（老板实测反馈）；单套没必要留两份。
///   - 多套方案 → 只能用 additional + WAUT 按 activate_at 分时段切换，programID 加 "pro" 前缀
///     （与 net.xml 自带的 "0" 撞号会让 SUMO 直接退出，2026-11-04 实测）。
/// </summary>
public static class SumoTllWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>
    /// 写 tll 文件。返回警告列表；**没写文件**（没有多套方案 / 专业档为空）时返回 null。
    /// </summary>
    public static IReadOnlyList<string>? Write(
        string netXmlPath,
        ExportGraph graph,
        string profileJson,
        string outputPath)
    {
        if (string.IsNullOrWhiteSpace(profileJson))
        {
            return null;
        }

        Dictionary<int, List<SumoProfileTls.Program>>? profile = SumoProfileTls.Parse(profileJson);
        if (profile == null)
        {
            return null;
        }

        var warnings = new List<string>();
        XDocument net = XDocument.Load(netXmlPath, LoadOptions.PreserveWhitespace);
        Dictionary<string, int> laneMap = SumoNetIndex.LaneMap(net, graph);
        Dictionary<string, Dictionary<int, int>> linkTurns =
            SumoNetIndex.LinkMap(net, graph, laneMap, includeCrosswalk: true);

        // net.xml 里每个 tlLogic 的 link 数（= state 串长度）
        var linkCountOf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (XElement tlLogic in net.Root?.Elements("tlLogic") ?? Enumerable.Empty<XElement>())
        {
            string id = (string?)tlLogic.Attribute("id") ?? string.Empty;
            if (id.Length == 0)
            {
                continue;
            }

            int count = tlLogic.Elements("phase")
                .Select(p => ((string?)p.Attribute("state"))?.Length ?? 0)
                .DefaultIfEmpty(0)
                .Max();
            linkCountOf[id] = count;
        }

        var root = new XElement("additional");
        int multiCount = 0;

        foreach (KeyValuePair<int, List<SumoProfileTls.Program>> entry in profile)
        {
            // ★ 单套方案已在 net.xml 里（programID="0"），不能再产一份，否则撞号。
            if (entry.Value.Count < 2)
            {
                continue;
            }

            string tlId = entry.Key.ToString(CultureInfo.InvariantCulture);
            if (!linkCountOf.TryGetValue(tlId, out int linkCount) || linkCount <= 0)
            {
                warnings.Add($"节点 {entry.Key}：网络里没有对应的信号灯位（可能不是信号路口），专业配时已跳过。");
                continue;
            }

            multiCount++;

            // ★ programID 必须与 net.xml 里的错开：SUMO 见到同名 (id, programID) 会直接报
            //   "Another logic with id '0' and programID '0' exists" 并退出（2026-11-04 实测）。
            //   统一加 "pro" 前缀：既保证不撞，又能在 netedit 里一眼认出这是编辑器生成的那套。
            var usedProgramIds = new HashSet<string>(StringComparer.Ordinal);
            var emitted = new List<(SumoProfileTls.Program Program, string ProgramId)>();
            foreach (SumoProfileTls.Program program in entry.Value)
            {
                string wanted = "pro" + (program.Id ?? "0").Trim();
                string programId = wanted;
                int n = 2;
                while (!usedProgramIds.Add(programId))
                {
                    programId = wanted + "_" + n.ToString(CultureInfo.InvariantCulture);
                    n++;
                }

                XElement? element = SumoProfileTls.BuildTlLogic(tlId, linkCount, program, programId,
                    linkTurns.TryGetValue(tlId, out var map) ? map : null, entry.Key, warnings);
                if (element == null)
                {
                    continue;
                }

                emitted.Add((program, programId));
                root.Add(element);
            }

            // 多套方案：用 WAUT 按 activate_at 依次切换（SUMO 的 additional 支持）
            if (emitted.Count > 1)
            {
                var waut = new XElement("WAUT",
                    new XAttribute("id", "waut_" + tlId),
                    new XAttribute("refId", tlId),
                    new XAttribute("startProg", emitted[0].ProgramId));
                foreach ((SumoProfileTls.Program program, string programId) in emitted)
                {
                    waut.Add(new XElement("wautSwitch",
                        new XAttribute("time", F(program.ActivateAt)),
                        new XAttribute("to", programId)));
                }

                root.Add(waut);
            }
        }

        if (multiCount == 0)
        {
            // 专业档全是一套方案 → net.xml 里已经写了，本文件不需要。
            return null;
        }

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
        using (var writer = XmlWriter.Create(outputPath, new XmlWriterSettings
               {
                   Indent = true,
                   IndentChars = "    ",
                   NewLineChars = "\n",
                   NewLineHandling = NewLineHandling.Replace,
                   Encoding = Utf8NoBom,
                   OmitXmlDeclaration = false,
               }))
        {
            doc.Save(writer);
        }

        return warnings;
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
