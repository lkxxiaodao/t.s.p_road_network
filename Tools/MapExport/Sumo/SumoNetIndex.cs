using System.Globalization;
using System.Xml.Linq;

namespace SignalPlanner.MapExport.Sumo;

/// <summary>
/// net.xml ↔ .spmap 的索引：SUMO 的 lane id / connection 反查回 spmap 的 lane / turn。
///
/// ★ 本类被两处共用，口径必须完全一致：
///   ① <see cref="SumoTlLogicPatcher"/> —— 基础模式把 .spmap 的相位回写成 &lt;tlLogic&gt;；
///   ② <see cref="SumoNetProbe"/> —— 专业模式的探针，把真实 link 表交给编辑器。
///   两边一旦分叉，就会出现"编辑器里看到的灯"和"实际生效的灯"不是同一套。
/// </summary>
internal static class SumoNetIndex
{
    /// <summary>net.xml 的 lane id → spmap 的 lane id（靠 lane 上的 origID 参数）。</summary>
    internal static Dictionary<string, int> LaneMap(XDocument doc, ExportGraph graph)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var byOdr = new Dictionary<(int Road, int Lane), int>();

        foreach (ExportLane lane in graph.Lanes.Values)
        {
            byOdr[(lane.DirectionalRoadId, lane.OdrLaneId)] = lane.Id;
        }

        foreach (XElement lane in doc.Descendants("lane"))
        {
            string? netLaneId = (string?)lane.Attribute("id");
            if (string.IsNullOrEmpty(netLaneId))
            {
                continue;
            }

            string? origId = null;
            foreach (XElement param in lane.Elements("param"))
            {
                if (string.Equals((string?)param.Attribute("key"), "origID", StringComparison.OrdinalIgnoreCase))
                {
                    origId = (string?)param.Attribute("value");
                    break;
                }
            }

            origId ??= (string?)lane.Attribute("origID");
            origId ??= (string?)lane.Attribute("origId");
            if (string.IsNullOrWhiteSpace(origId))
            {
                continue;
            }

            if (!TryParseOrigId(origId, out int roadId, out int odrLaneId))
            {
                continue;
            }

            if (byOdr.TryGetValue((roadId, odrLaneId), out int spmapLaneId))
            {
                result[netLaneId] = spmapLaneId;
            }
        }

        return result;
    }

    /// <summary>tl id → (linkIndex → spmap 的 turn id)。</summary>
    /// <param name="includeCrosswalk">
    /// false = 只收机动车/自行车转向（基础模式回写配时的既有口径，人行横道连接不进映射）；
    /// true  = 连人行横道也算进来（专业模式探针要显示"人是单独一跳灯"）。
    /// 默认 false 是为了让 <see cref="SumoTlLogicPatcher"/> 的行为与 2026-11 之前逐字节一致。
    /// </param>
    internal static Dictionary<string, Dictionary<int, int>> LinkMap(
        XDocument doc,
        ExportGraph graph,
        Dictionary<string, int> netLaneToSpmapLane,
        bool includeCrosswalk = false)
    {
        var turnByLanePair = new Dictionary<(int Src, int Dst), int>();
        foreach (ExportTurn turn in graph.Turns.Where(t => !t.IsCrosswalk))
        {
            turnByLanePair[(turn.Src.Id, turn.Dst.Id)] = turn.Id;
        }

        // ★ 分段过街的两条 turn 共用同一对 src/dst（几何上是同一条横道），
        //   所以这里只认 leg=0 那条 —— 它就是 pedestrians[].crosswalk_turn 指向的那条。
        if (includeCrosswalk)
        {
            foreach (ExportTurn turn in graph.Turns.Where(t => t.IsCrosswalk && t.Leg <= 0))
            {
                turnByLanePair[(turn.Src.Id, turn.Dst.Id)] = turn.Id;
            }
        }

        var result = new Dictionary<string, Dictionary<int, int>>(StringComparer.Ordinal);
        foreach (XElement connection in doc.Root?.Elements("connection") ?? Enumerable.Empty<XElement>())
        {
            string? tl = (string?)connection.Attribute("tl");
            if (string.IsNullOrEmpty(tl))
            {
                continue;
            }

            if (!int.TryParse((string?)connection.Attribute("linkIndex"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int linkIndex))
            {
                continue;
            }

            string fromNet = (string?)connection.Attribute("from") + "_" + (string?)connection.Attribute("fromLane");
            string toNet = (string?)connection.Attribute("to") + "_" + (string?)connection.Attribute("toLane");
            if (!netLaneToSpmapLane.TryGetValue(fromNet, out int fromLane) ||
                !netLaneToSpmapLane.TryGetValue(toNet, out int toLane))
            {
                continue;
            }

            if (!turnByLanePair.TryGetValue((fromLane, toLane), out int turnId))
            {
                continue;
            }

            if (!result.TryGetValue(tl, out Dictionary<int, int>? map))
            {
                map = new Dictionary<int, int>();
                result[tl] = map;
            }

            map[linkIndex] = turnId;
        }

        return result;
    }

    /// <summary>
    /// netconvert 的 origId 有两种写法：
    ///   - 空格分隔：<c>"2 -1"</c>
    ///   - 下划线分隔：<c>"2_-1"</c>（SUMO 1.20+ 的 --output.original-names）
    /// 还有可能把 edge id 写成 <c>"2.0"</c>（lane section 后缀）。
    /// </summary>
    private static bool TryParseOrigId(string raw, out int roadId, out int odrLaneId)
    {
        roadId = 0;
        odrLaneId = 0;
        string text = raw.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        string[] parts = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            return TryParseRoadAndLane(parts[0], parts[1], out roadId, out odrLaneId);
        }

        int separator = text.LastIndexOf('_');
        if (separator <= 0 || separator >= text.Length - 1)
        {
            return false;
        }

        return TryParseRoadAndLane(text[..separator], text[(separator + 1)..], out roadId, out odrLaneId);
    }

    private static bool TryParseRoadAndLane(string roadText, string laneText, out int roadId, out int odrLaneId)
    {
        roadId = 0;
        odrLaneId = 0;
        int dot = roadText.IndexOf('.');
        if (dot >= 0)
        {
            roadText = roadText[..dot];
        }

        return int.TryParse(roadText, NumberStyles.Integer, CultureInfo.InvariantCulture, out roadId) &&
               int.TryParse(laneText, NumberStyles.Integer, CultureInfo.InvariantCulture, out odrLaneId);
    }
}
