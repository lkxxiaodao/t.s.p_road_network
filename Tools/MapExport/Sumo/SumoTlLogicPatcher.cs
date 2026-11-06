using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SignalPlanner.MapExport.Sumo;

/// <summary>
/// netconvert 生成 net.xml 后，把配时回写成 SUMO 的 &lt;tlLogic&gt;。
/// 用 lane 的 origID 把 SUMO 的 connection 映射回 spmap 的 lane / turn。
///
/// ★ 写哪一套（2026-11-04 调整）：
///   - 专业档里该节点**只有一套**方案 → 用专业方案（含 minDur/maxDur/params/相位名）；
///   - 否则 → 用 Doc 的第一套 SignalPlan（基础口径：绿 / 黄 / 全红）。
///   两套都不走 additional，保证 net.xml 单独就能跑，netedit 打开也看得见。
/// </summary>
public static class SumoTlLogicPatcher
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static IReadOnlyList<string> Patch(string netXmlPath, ExportGraph graph, MapExportOptions options)
    {
        var warnings = new List<string>();
        XDocument doc = XDocument.Load(netXmlPath, LoadOptions.PreserveWhitespace);

        Dictionary<string, int> netLaneToSpmapLane = SumoNetIndex.LaneMap(doc, graph);
        // ★ includeCrosswalk 保持默认 false：人行横道连接不参与**基础**回写的映射，
        //   于是它们落进下面的 fallbackLinks，第一相位统一给绿灯（2026-11 之前的既有行为）。
        Dictionary<string, Dictionary<int, int>> linkIndexToTurn =
            SumoNetIndex.LinkMap(doc, graph, netLaneToSpmapLane);
        // ★ 专业档的矩阵里"人"是独立一行 → 内嵌专业方案时人行横道必须算进映射。
        Dictionary<string, Dictionary<int, int>> linkIndexToTurnAll =
            SumoNetIndex.LinkMap(doc, graph, netLaneToSpmapLane, includeCrosswalk: true);
        Dictionary<int, ExportTurn> turnsById = graph.Turns.ToDictionary(t => t.Id);
        Dictionary<int, List<SumoProfileTls.Program>>? profileTls = SumoProfileTls.Parse(options.SumoProfileJson);

        foreach (XElement tlLogic in doc.Root?.Elements("tlLogic") ?? Enumerable.Empty<XElement>())
        {
            string tlId = (string?)tlLogic.Attribute("id") ?? string.Empty;
            linkIndexToTurn.TryGetValue(tlId, out Dictionary<int, int>? linkTurns);
            linkIndexToTurnAll.TryGetValue(tlId, out Dictionary<int, int>? linkTurnsAll);
            bool hasLinkMap = linkTurns != null && linkTurns.Count > 0;

            if (!hasLinkMap && (linkTurnsAll == null || linkTurnsAll.Count == 0))
            {
                warnings.Add($"tlLogic {tlId}: 没有任何 connection 能映射到 turn，保留 netconvert 默认程序。");
                continue;
            }

            ExportNode? node = null;
            if (int.TryParse(tlId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedNodeId) &&
                graph.Nodes.TryGetValue(parsedNodeId, out ExportNode? parsedNode) &&
                parsedNode.Type == SimCore.Model.NodeType.Signalized)
            {
                node = parsedNode;
            }

            if (node == null)
            {
                // netconvert 可能给 tlLogic 加了前缀或用了 junction id；用已映射的 turn 反推节点。
                IEnumerable<int> candidates = (linkTurns?.Values ?? Enumerable.Empty<int>())
                    .Concat(linkTurnsAll?.Values ?? Enumerable.Empty<int>());
                foreach (int turnId in candidates)
                {
                    if (turnsById.TryGetValue(turnId, out ExportTurn? turn) &&
                        graph.Nodes.TryGetValue(turn.NodeId, out ExportNode? byTurn) &&
                        byTurn.Type == SimCore.Model.NodeType.Signalized)
                    {
                        node = byTurn;
                        break;
                    }
                }
            }

            if (node == null)
            {
                warnings.Add($"tlLogic {tlId}: 找不到对应信号节点，保留 netconvert 默认程序。");
                continue;
            }

            // ★ 专业档只有**一套**方案时，直接替换 net.xml 里的程序（programID 仍为 "0"）。
            //   这样 netedit 打开看到的就是用户自己的配时，不存在"pro0 藏在方案下拉里"的问题
            //   （2026-11-04 老板实测：两套程序并存 → netedit 只显示网络自带那套）。
            //   多套方案要 WAUT 分时段切换，只能走 additional，见 SumoTllWriter。
            if (SumoProfileTls.TryGetSingle(profileTls, node.Id, out SumoProfileTls.Program? proProgram))
            {
                Dictionary<int, int>? mapForPro =
                    linkTurnsAll != null && linkTurnsAll.Count > 0 ? linkTurnsAll : linkTurns;
                int professionalLinks = mapForPro != null && mapForPro.Count > 0 ? mapForPro.Keys.Max() + 1 : 0;
                int existingLinks = tlLogic.Elements("phase")
                    .Select(p => ((string?)p.Attribute("state"))?.Length ?? 0)
                    .DefaultIfEmpty(0)
                    .Max();
                int bakedLinkCount = Math.Max(professionalLinks, existingLinks);

                if (bakedLinkCount > 0)
                {
                    XElement? baked = SumoProfileTls.BuildTlLogic(
                        tlId, bakedLinkCount, proProgram, "0", mapForPro, node.Id, warnings);
                    if (baked != null)
                    {
                        ApplyBaked(tlLogic, baked);
                        continue;
                    }
                }
            }

            if (!hasLinkMap)
            {
                warnings.Add($"tlLogic {tlId}: 没有任何 connection 能映射到 turn，保留 netconvert 默认程序。");
                continue;
            }

            if (node.Plans.Count == 0)
            {
                continue;
            }

            if (node.Plans.Count > 1)
            {
                warnings.Add($"tlLogic {tlId}: 存在 {node.Plans.Count} 套方案，本期只应用第一套。");
            }

            SimCore.Model.SignalPlan plan = node.Plans.OrderBy(p => p.StartTime).First();
            // netconvert 生成的默认 tlLogic 已经覆盖了全部 linkIndex（包括我们没映射到的转向）；
            // 状态串长度必须与 linkIndex 总数一致，否则 SUMO 会报 state 长度不匹配。
            int existingLinkCount = tlLogic.Elements("phase")
                .Select(p => ((string?)p.Attribute("state"))?.Length ?? 0)
                .DefaultIfEmpty(0)
                .Max();
            int linkCount = Math.Max(linkTurns!.Keys.Max() + 1, existingLinkCount);

            // 找出“没有被任何相位引用”的 link（典型：自行车/人行道连接）。
            // SUMO 要求每个 tl-index 至少有一次绿灯，否则会说 Missing green phase；
            // 这些 link 统一在第一相位给一个绿灯，避免生成非法 tlLogic。
            var controlledTurns = new HashSet<int>();
            foreach (SimCore.Model.Stage s in plan.Stages)
            {
                controlledTurns.UnionWith(s.ProtectedTurnIds);
                controlledTurns.UnionWith(s.PermittedTurnIds);
            }

            var fallbackLinks = new HashSet<int>();
            for (int linkIndex = 0; linkIndex < linkCount; linkIndex++)
            {
                if (!linkTurns.TryGetValue(linkIndex, out int turnId) || !controlledTurns.Contains(turnId))
                {
                    fallbackLinks.Add(linkIndex);
                }
            }

            var phases = new List<(double Duration, string State)>();
            int stageIndex = 0;

            foreach (SimCore.Model.Stage stage in plan.Stages)
            {
                char[] state = Enumerable.Repeat('r', linkCount).ToArray();
                foreach ((int linkIndex, int turnId) in linkTurns)
                {
                    if (linkIndex < 0 || linkIndex >= state.Length)
                    {
                        continue;
                    }

                    if (Contains(stage.ProtectedTurnIds, turnId))
                    {
                        state[linkIndex] = 'G';
                    }
                    else if (Contains(stage.PermittedTurnIds, turnId))
                    {
                        state[linkIndex] = 'g';
                    }
                }

                if (stageIndex == 0)
                {
                    foreach (int linkIndex in fallbackLinks)
                    {
                        if (linkIndex >= 0 && linkIndex < state.Length)
                        {
                            state[linkIndex] = 'G';
                        }
                    }
                }

                stageIndex++;

                phases.Add((Math.Max(0.1, stage.Duration), new string(state)));

                char[] yellow = new char[linkCount];
                for (int i = 0; i < linkCount; i++)
                {
                    yellow[i] = state[i] is 'G' or 'g' ? 'y' : 'r';
                }

                phases.Add((Math.Max(0.1, options.YellowDuration), new string(yellow)));
                phases.Add((Math.Max(0.0, options.AllRedDuration), new string(Enumerable.Repeat('r', linkCount).ToArray())));
            }

            tlLogic.RemoveNodes();
            tlLogic.SetAttributeValue("type", "static");
            tlLogic.SetAttributeValue("programID", "0");
            tlLogic.SetAttributeValue("offset", F(node.Offset));

            foreach ((double duration, string state) in phases)
            {
                tlLogic.Add(new XElement("phase",
                    new XAttribute("duration", F(duration)),
                    new XAttribute("state", state)));
            }
        }

        using (var writer = XmlWriter.Create(netXmlPath, new XmlWriterSettings
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

    // ★ lane / link 的映射已抽到 SumoNetIndex（探针共用同一套口径），此处不再保留副本。

    /// <summary>
    /// 就地替换 tlLogic 的内容。
    /// ★ 不能用 <c>ReplaceWith</c>：正在枚举父节点的子元素，改子节点列表会抛
    ///   "Collection was modified"。改属性 + 换子节点则安全。
    /// </summary>
    private static void ApplyBaked(XElement target, XElement source)
    {
        target.RemoveNodes();
        foreach (XAttribute attribute in source.Attributes())
        {
            target.SetAttributeValue(attribute.Name, attribute.Value);
        }

        foreach (XElement child in source.Elements().ToList())
        {
            target.Add(child);
        }
    }

    private static bool Contains(IReadOnlyList<int> ids, int value)
    {
        for (int i = 0; i < ids.Count; i++)
        {
            if (ids[i] == value)
            {
                return true;
            }
        }

        return false;
    }

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
