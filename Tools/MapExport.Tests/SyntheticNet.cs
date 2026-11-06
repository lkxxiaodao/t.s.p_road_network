using System.Globalization;
using System.Xml.Linq;

namespace SignalPlanner.MapExport.Tests;

/// <summary>
/// 造一份"linkIndex 从 0 连续分配"的合成 net（真 netconvert 的输出就是这个形状），
/// 返回 turn id → linkIndex 的对照表。不跑 netconvert，测试才够快、够稳。
/// </summary>
internal static class SyntheticNet
{
    public static Dictionary<int, int> Write(string path, ExportGraph graph, int nodeId)
    {
        var root = new XElement("net");
        foreach (ExportLane lane in graph.Lanes.Values.OrderBy(l => l.Id))
        {
            string edgeId = "e" + lane.Id.ToString(CultureInfo.InvariantCulture);
            root.Add(new XElement("edge",
                new XAttribute("id", edgeId),
                new XElement("lane",
                    new XAttribute("id", edgeId + "_0"),
                    // ★ 带 length：S3 的检测设施要用它把 pos / startPos 夹进合法区间，
                    //   没 length 时所有设施都会被判"放不下"而跳过。
                    new XAttribute("length", lane.Length.ToString("0.##", CultureInfo.InvariantCulture)),
                    new XAttribute("speed", lane.SpeedLimit.ToString("0.##", CultureInfo.InvariantCulture)),
                    new XElement("param",
                        new XAttribute("key", "origID"),
                        new XAttribute("value",
                            lane.DirectionalRoadId.ToString(CultureInfo.InvariantCulture) + " " +
                            lane.OdrLaneId.ToString(CultureInfo.InvariantCulture))))));
        }

        var turnToLink = new Dictionary<int, int>();
        int linkIndex = 0;
        foreach (ExportTurn turn in graph.Turns.Where(t => t.NodeId == nodeId).OrderBy(t => t.Id))
        {
            root.Add(new XElement("connection",
                new XAttribute("from", "e" + turn.Src.Id.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("fromLane", "0"),
                new XAttribute("to", "e" + turn.Dst.Id.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("toLane", "0"),
                new XAttribute("tl", nodeId.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("linkIndex", linkIndex.ToString(CultureInfo.InvariantCulture))));
            turnToLink[turn.Id] = linkIndex;
            linkIndex++;
        }

        root.Add(new XElement("tlLogic",
            new XAttribute("id", nodeId.ToString(CultureInfo.InvariantCulture)),
            new XAttribute("type", "static"),
            new XAttribute("programID", "0"),
            new XElement("phase",
                new XAttribute("duration", "60"),
                new XAttribute("state", new string('r', turnToLink.Count)))));

        new XDocument(root).Save(path);
        return turnToLink;
    }
}
