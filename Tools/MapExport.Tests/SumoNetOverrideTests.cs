using System.Globalization;
using System.Xml.Linq;
using SignalPlanner.MapExport.Sumo;
using SimCore.Io;
using Xunit;

namespace SignalPlanner.MapExport.Tests;

/// <summary>
/// 网络覆盖层（S4）的单元测试。
/// ★ 这里守着三条"实测铁律"（见 <see cref="SumoNetOverride"/> 类注释）：
///   ① 禁连接**不能删元素**（SUMO 段错误）；② 写 allow 必须删 disallow；
///   ③ 没有 overrides 时文件一个字节都不能变。
/// </summary>
public sealed class SumoNetOverrideTests
{
    [Fact]
    public void NoOverrides_LeavesFileByteIdentical()
    {
        string dir = TempDir.Create();
        try
        {
            string path = Path.Combine(dir, "net.xml");
            ExportGraph graph = BuildExampleNet(path);
            byte[] before = File.ReadAllBytes(path);

            // 空专业档 / 只有 config / overrides 三张表都空 —— 全部不许动文件
            Assert.Empty(SumoNetOverride.Apply(path, graph, null));
            Assert.Empty(SumoNetOverride.Apply(path, graph, string.Empty));
            Assert.Empty(SumoNetOverride.Apply(path, graph, """{"format_version":1,"config":{"begin":0}}"""));
            Assert.Empty(SumoNetOverride.Apply(path, graph, """{"format_version":1,"overrides":{"lanes":[]}}"""));

            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void LaneAccess_ReplacesDisallow()
    {
        string dir = TempDir.Create();
        try
        {
            string path = Path.Combine(dir, "net.xml");
            ExportGraph graph = BuildExampleNet(path);
            int laneId = graph.Lanes.Keys.Min();
            string netLane = "e" + laneId.ToString(CultureInfo.InvariantCulture) + "_0";

            XElement lane = LoadLane(path, netLane);
            Assert.NotNull(lane.Attribute("disallow"));        // 造网时就带着 disallow

            string json = $$$"""{"overrides":{"lanes":[{"lane":{{{laneId}}},"access":"bus"}]}}""";
            Assert.Empty(SumoNetOverride.Apply(path, graph, json));

            lane = LoadLane(path, netLane);
            Assert.Equal("bus", (string?)lane.Attribute("allow"));
            // ★ 铁律 2：两个并存时 SUMO 会忽略 disallow 并告警 → 必须删掉
            Assert.Null(lane.Attribute("disallow"));
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void LaneSpeedAndWidth_AreWrittenAsIs()
    {
        string dir = TempDir.Create();
        try
        {
            string path = Path.Combine(dir, "net.xml");
            ExportGraph graph = BuildExampleNet(path);
            int laneId = graph.Lanes.Keys.Min();
            string netLane = "e" + laneId.ToString(CultureInfo.InvariantCulture) + "_0";

            string json = $$$"""
                {"overrides":{"lanes":[{"lane":{{{laneId}}},"speed":8.33,"width":3.25}]}}
                """;
            Assert.Empty(SumoNetOverride.Apply(path, graph, json));

            XElement lane = LoadLane(path, netLane);
            Assert.Equal("8.33", (string?)lane.Attribute("speed"));
            Assert.Equal("3.25", (string?)lane.Attribute("width"));
            // 只改限速/宽度不许碰 allow/disallow
            Assert.NotNull(lane.Attribute("disallow"));
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void ConnectionNone_UsesPedestrianAllow_AndKeepsElement()
    {
        string dir = TempDir.Create();
        try
        {
            string path = Path.Combine(dir, "net.xml");
            ExportGraph graph = BuildExampleNet(path);
            ExportTurn turn = graph.Turns.First(t => !t.IsCrosswalk);
            int connectionsBefore = XDocument.Load(path).Root!.Elements("connection").Count();

            string json = $$$"""{"overrides":{"connections":[{"turn":{{{turn.Id}}},"access":"none"}]}}""";
            Assert.Empty(SumoNetOverride.Apply(path, graph, json));

            XDocument doc = XDocument.Load(path);
            // ★ 铁律 1：元素数量不变（删了会让 SUMO 段错误退出）
            Assert.Equal(connectionsBefore, doc.Root!.Elements("connection").Count());

            XElement connection = FindConnection(doc, turn);
            Assert.Equal("pedestrian", (string?)connection.Attribute("allow"));
            Assert.Null(connection.Attribute("disallow"));
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void JunctionType_IsApplied()
    {
        string dir = TempDir.Create();
        try
        {
            string path = Path.Combine(dir, "net.xml");
            ExportGraph graph = BuildExampleNet(path);

            // 造网时加了一个非信号 junction（id=99）专门给这条测试用
            string json = """{"overrides":{"junctions":[{"node":99,"type":"right_before_left"}]}}""";
            Assert.Empty(SumoNetOverride.Apply(path, graph, json));

            XElement junction = XDocument.Load(path).Root!.Elements("junction")
                .Single(j => (string?)j.Attribute("id") == "99");
            Assert.Equal("right_before_left", (string?)junction.Attribute("type"));
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void SignalizedJunction_IsSkippedWithWarning()
    {
        string dir = TempDir.Create();
        try
        {
            string path = Path.Combine(dir, "net.xml");
            ExportGraph graph = BuildExampleNet(path);
            int signalNode = graph.Nodes.Values.First(n => n.Type == SimCore.Model.NodeType.Signalized).Id;

            string json = $$$"""{"overrides":{"junctions":[{"node":{{{signalNode}}},"type":"unregulated"}]}}""";
            IReadOnlyList<string> warnings = SumoNetOverride.Apply(path, graph, json);

            Assert.Single(warnings);
            Assert.Contains("信号路口", warnings[0]);
            XElement junction = XDocument.Load(path).Root!.Elements("junction")
                .Single(j => (string?)j.Attribute("id") == signalNode.ToString(CultureInfo.InvariantCulture));
            Assert.Equal("traffic_light", (string?)junction.Attribute("type"));
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void UnknownIdsAndValues_WarnButDoNotCrash()
    {
        string dir = TempDir.Create();
        try
        {
            string path = Path.Combine(dir, "net.xml");
            ExportGraph graph = BuildExampleNet(path);
            byte[] before = File.ReadAllBytes(path);
            int knownLane = graph.Lanes.Keys.Min();

            string json = $$$"""
                {"overrides":{
                  "lanes":[{"lane":99999,"access":"bus"},{"lane":{{{knownLane}}},"access":"flying"}],
                  "connections":[{"turn":99999,"access":"none"}],
                  "junctions":[{"node":88888,"type":"priority"},{"node":99,"type":"nonsense"}]
                }}
                """;
            IReadOnlyList<string> warnings = SumoNetOverride.Apply(path, graph, json);

            // 每条坏数据各一条告警，且一条都不许生效
            Assert.Contains(warnings, w => w.Contains("找不到对应车道"));
            Assert.Contains(warnings, w => w.Contains("不认识的允许车型"));
            Assert.Contains(warnings, w => w.Contains("找不到对应的转向"));
            Assert.Contains(warnings, w => w.Contains("没有同名路口"));
            Assert.Contains(warnings, w => w.Contains("不认识的控制方式"));
            Assert.Equal(before, File.ReadAllBytes(path));   // 一条都没生效 → 文件不动
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void BrokenJson_WarnsAndLeavesFile()
    {
        string dir = TempDir.Create();
        try
        {
            string path = Path.Combine(dir, "net.xml");
            ExportGraph graph = BuildExampleNet(path);
            byte[] before = File.ReadAllBytes(path);

            IReadOnlyList<string> warnings = SumoNetOverride.Apply(path, graph, "{ not json");
            Assert.Single(warnings);
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    // ============================================================
    // 造网：比 SyntheticNet 多两样东西 —— lane 上的 disallow、一个非信号 junction
    // ============================================================

    private static ExportGraph BuildExampleNet(string path)
    {
        Level level = Spmap.Load(TestPaths.Example);
        ExportGraph graph = ExportGraph.Build(level, 3.5);

        var root = new XElement("net");
        foreach (ExportLane lane in graph.Lanes.Values.OrderBy(l => l.Id))
        {
            string edgeId = "e" + lane.Id.ToString(CultureInfo.InvariantCulture);
            root.Add(new XElement("edge",
                new XAttribute("id", edgeId),
                new XElement("lane",
                    new XAttribute("id", edgeId + "_0"),
                    new XAttribute("index", "0"),
                    new XAttribute("speed", lane.SpeedLimit.ToString("0.##", CultureInfo.InvariantCulture)),
                    new XAttribute("length", lane.Length.ToString("0.##", CultureInfo.InvariantCulture)),
                    // netconvert 给机动车道的典型值：一长串 disallow
                    new XAttribute("disallow", "pedestrian bicycle rail"),
                    new XElement("param",
                        new XAttribute("key", "origID"),
                        new XAttribute("value",
                            lane.DirectionalRoadId.ToString(CultureInfo.InvariantCulture) + " " +
                            lane.OdrLaneId.ToString(CultureInfo.InvariantCulture))))));
        }

        foreach (ExportTurn turn in graph.Turns.Where(t => !t.IsCrosswalk).OrderBy(t => t.Id))
        {
            root.Add(new XElement("connection",
                new XAttribute("from", "e" + turn.Src.Id.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("fromLane", "0"),
                new XAttribute("to", "e" + turn.Dst.Id.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("toLane", "0"),
                new XAttribute("tl", turn.NodeId.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("linkIndex", turn.Id.ToString(CultureInfo.InvariantCulture))));
        }

        foreach (ExportNode node in graph.Nodes.Values.OrderBy(n => n.Id))
        {
            root.Add(new XElement("junction",
                new XAttribute("id", node.Id.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("type", node.Type == SimCore.Model.NodeType.Signalized
                    ? "traffic_light"
                    : "priority")));
        }

        // 专供 junction 覆盖测试的"额外路口"：度数够、但不在 spmap 里
        root.Add(new XElement("junction",
            new XAttribute("id", "99"),
            new XAttribute("type", "priority")));

        foreach (ExportNode node in graph.Nodes.Values.Where(n => n.Type == SimCore.Model.NodeType.Signalized))
        {
            root.Add(new XElement("tlLogic",
                new XAttribute("id", node.Id.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("type", "static"),
                new XAttribute("programID", "0"),
                new XElement("phase", new XAttribute("duration", "60"), new XAttribute("state", "rrrrrr"))));
        }

        new XDocument(root).Save(path);
        return graph;
    }

    private static XElement LoadLane(string path, string netLaneId)
        => XDocument.Load(path).Descendants("lane")
            .Single(l => (string?)l.Attribute("id") == netLaneId);

    private static XElement FindConnection(XDocument doc, ExportTurn turn)
        => doc.Root!.Elements("connection").Single(c =>
            (string?)c.Attribute("from") == "e" + turn.Src.Id.ToString(CultureInfo.InvariantCulture) &&
            (string?)c.Attribute("to") == "e" + turn.Dst.Id.ToString(CultureInfo.InvariantCulture));
}
