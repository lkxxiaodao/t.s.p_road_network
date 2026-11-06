using System.Xml.Linq;
using SignalPlanner.MapExport.Sumo;
using SimCore.Io;
using Xunit;

namespace SignalPlanner.MapExport.Tests;

/// <summary>
/// 「专业档单套方案 → 内嵌进 net.xml」的契约测试（2026-11-04）。
///
/// 为什么要有这个行为：netedit 打开 net.xml 时只显示网络自带的那套程序；
/// 我们的专业配时如果作为 additional 的第二套（pro0）存在，用户在 netedit 的信号灯编辑器里
/// 根本看不到它（老板实测）。所以单套方案必须落进 net.xml 的 <c>programID="0"</c>。
/// 多套方案需要 WAUT 分时段切换，仍旧走 .tll.xml（见 <see cref="SumoTllWriterTests"/>）。
/// </summary>
public sealed class SumoTlsBakeTests
{
    private const int SignalNode = 0;      // 示例关卡里 node 0 是信号路口（10 条转向）

    [Fact]
    public void SingleProgramReplacesNetProgram()
    {
        Level level = Spmap.Load(TestPaths.Example);
        ExportGraph graph = ExportGraph.Build(level, 3.5);
        string dir = TempDir.Create();
        try
        {
            string netPath = Path.Combine(dir, "n.net.xml");
            Dictionary<int, int> turnToLink = SyntheticNet.Write(netPath, graph, SignalNode);
            int turnId = turnToLink.Keys.OrderBy(k => k).First();
            int linkIndex = turnToLink[turnId];

            var options = new MapExportOptions
            {
                SumoProfileJson = "{\"format_version\":1,\"tls\":[{\"node\":" + SignalNode + ",\"programs\":[{" +
                                  "\"id\":\"0\",\"type\":\"static\",\"offset\":5,\"phases\":[" +
                                  "{\"duration\":20,\"name\":\"阶段 1 绿\",\"states\":{\"" + turnId + "\":\"G\"}}," +
                                  "{\"duration\":3,\"states\":{}}]}]}]}",
            };

            IReadOnlyList<string> warnings = SumoTlLogicPatcher.Patch(netPath, graph, options);
            Assert.NotNull(warnings);

            XDocument doc = XDocument.Load(netPath);
            XElement tl = Assert.Single(doc.Root!.Elements("tlLogic"));
            Assert.Equal("0", (string?)tl.Attribute("programID"));     // ★ 不能是 pro0
            Assert.Equal("static", (string?)tl.Attribute("type"));
            Assert.Equal("5", (string?)tl.Attribute("offset"));

            List<XElement> phases = tl.Elements("phase").ToList();
            Assert.Equal(2, phases.Count);
            Assert.Equal("20", (string?)phases[0].Attribute("duration"));
            Assert.Equal("阶段 1 绿", (string?)phases[0].Attribute("name"));

            string state = (string?)phases[0].Attribute("state") ?? string.Empty;
            Assert.Equal(turnToLink.Count, state.Length);              // 长度 = link 数
            Assert.Equal('G', state[linkIndex]);                       // 落位正确
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void ActuatedProgramKeepsTypeParamsAndMinMaxDur()
    {
        Level level = Spmap.Load(TestPaths.Example);
        ExportGraph graph = ExportGraph.Build(level, 3.5);
        string dir = TempDir.Create();
        try
        {
            string netPath = Path.Combine(dir, "n.net.xml");
            Dictionary<int, int> turnToLink = SyntheticNet.Write(netPath, graph, SignalNode);
            int turnId = turnToLink.Keys.OrderBy(k => k).First();

            var options = new MapExportOptions
            {
                SumoProfileJson = "{\"format_version\":1,\"tls\":[{\"node\":" + SignalNode + ",\"programs\":[{" +
                                  "\"id\":\"0\",\"type\":\"actuated\",\"params\":{\"max-gap\":\"3.0\"},\"phases\":[" +
                                  "{\"duration\":20,\"min_dur\":8,\"max_dur\":60,\"states\":{\"" + turnId + "\":\"G\"}}]}]}]}",
            };

            SumoTlLogicPatcher.Patch(netPath, graph, options);

            XDocument doc = XDocument.Load(netPath);
            XElement tl = Assert.Single(doc.Root!.Elements("tlLogic"));
            Assert.Equal("actuated", (string?)tl.Attribute("type"));
            Assert.Equal("max-gap",
                (string?)tl.Elements("param").Single().Attribute("key"));

            XElement phase = Assert.Single(tl.Elements("phase"));
            Assert.Equal("8", (string?)phase.Attribute("minDur"));
            Assert.Equal("60", (string?)phase.Attribute("maxDur"));
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void MultiProgramIsLeftToTllWriter()
    {
        Level level = Spmap.Load(TestPaths.Example);
        ExportGraph graph = ExportGraph.Build(level, 3.5);
        string dir = TempDir.Create();
        try
        {
            string netPath = Path.Combine(dir, "n.net.xml");
            Dictionary<int, int> turnToLink = SyntheticNet.Write(netPath, graph, SignalNode);
            int turnId = turnToLink.Keys.OrderBy(k => k).First();

            var options = new MapExportOptions
            {
                SumoProfileJson = "{\"format_version\":1,\"tls\":[{\"node\":" + SignalNode + ",\"programs\":[" +
                                  "{\"id\":\"0\",\"type\":\"static\",\"phases\":[{\"duration\":20,\"states\":{\"" + turnId + "\":\"G\"}}]}," +
                                  "{\"id\":\"1\",\"type\":\"static\",\"activate_at\":300,\"phases\":[{\"duration\":40,\"states\":{\"" + turnId + "\":\"G\"}}]}]}]}",
            };

            SumoTlLogicPatcher.Patch(netPath, graph, options);

            XDocument doc = XDocument.Load(netPath);
            XElement tl = Assert.Single(doc.Root!.Elements("tlLogic"));
            Assert.Equal("0", (string?)tl.Attribute("programID"));
            // 不是专业档那套（20/40）→ 说明没内嵌，留给 tll
            Assert.DoesNotContain(tl.Elements("phase"),
                p => (string?)p.Attribute("duration") == "20");
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }
}
