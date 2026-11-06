using System.Xml.Linq;
using SignalPlanner.MapExport.Sumo;
using SimCore.Io;
using Xunit;

namespace SignalPlanner.MapExport.Tests;

/// <summary>
/// `.tll.xml` 写入器的契约测试（专业模式 S1，2026-11-04 调整口径）。
///
/// 现在它**只负责多套方案**：单套方案由 <see cref="SumoTlLogicPatcher"/> 写进 net.xml
/// 的 programID="0"（否则 netedit 里会看到两套程序、只显示网络自带那套）。锁四件事：
///   ① 单套方案 → **不产文件**（返回 null）；
///   ② 多套方案 → programID 带 "pro" 前缀（同名会与网络自带的 "0" 撞号，SUMO 直接退出）；
///   ③ state 串长度 = 该 tlLogic 的 link 数，灯态按 turn id → linkIndex 落位；
///   ④ 有灯位从未放行 → 自动补绿 + 告警（宁可灯序不完美，也不交一个跑不起来的文件）。
/// </summary>
public sealed class SumoTllWriterTests
{
    [Fact]
    public void SingleProgramIsNotWrittenHere()
    {
        Level level = Spmap.Load(TestPaths.Example);
        ExportGraph graph = ExportGraph.Build(level, 3.5);
        string dir = TempDir.Create();
        try
        {
            string netPath = Path.Combine(dir, "n.net.xml");
            Dictionary<int, int> turnToLink = SyntheticNet.Write(netPath, graph, nodeId: 0);
            int turnId = turnToLink.Keys.OrderBy(k => k).First();

            // 只有一套方案 → 应当由 patcher 内嵌进 net.xml，这里不产文件
            string profile = "{\"format_version\":1,\"tls\":[{\"node\":0,\"programs\":[{" +
                             "\"id\":\"0\",\"type\":\"static\",\"phases\":[" +
                             "{\"duration\":30,\"states\":{\"" + turnId + "\":\"G\"}}]}]}]}";

            string outPath = Path.Combine(dir, "n.tll.xml");
            Assert.Null(SumoTllWriter.Write(netPath, graph, profile, outPath));
            Assert.False(File.Exists(outPath));
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void WritesMultiProgramWithWautAndPrefixedIds()
    {
        Level level = Spmap.Load(TestPaths.Example);
        ExportGraph graph = ExportGraph.Build(level, 3.5);
        string dir = TempDir.Create();
        try
        {
            string netPath = Path.Combine(dir, "n.net.xml");
            Dictionary<int, int> turnToLink = SyntheticNet.Write(netPath, graph, nodeId: 0);
            int linkCount = turnToLink.Count;
            int turnId = turnToLink.Keys.OrderBy(k => k).First();
            int linkIndex = turnToLink[turnId];

            string profile = "{\"format_version\":1,\"tls\":[{\"node\":0,\"programs\":[" +
                             "{\"id\":\"0\",\"type\":\"static\",\"activate_at\":0,\"phases\":[" +
                             "{\"duration\":30,\"states\":{\"" + turnId + "\":\"G\"}}]}," +
                             "{\"id\":\"1\",\"type\":\"static\",\"activate_at\":300,\"phases\":[" +
                             "{\"duration\":40,\"states\":{\"" + turnId + "\":\"G\"}}]}]}]}";

            string outPath = Path.Combine(dir, "n.tll.xml");
            IReadOnlyList<string>? warnings = SumoTllWriter.Write(netPath, graph, profile, outPath);
            Assert.NotNull(warnings);
            Assert.True(File.Exists(outPath));

            XDocument doc = XDocument.Load(outPath);
            List<XElement> programs = doc.Root!.Elements("tlLogic").ToList();
            Assert.Equal(2, programs.Count);
            Assert.Equal(new[] { "pro0", "pro1" },
                programs.Select(p => (string?)p.Attribute("programID")).ToArray());
            Assert.All(programs, p => Assert.Equal("0", (string?)p.Attribute("id")));

            string first = (string?)programs[0].Element("phase")!.Attribute("state") ?? string.Empty;
            Assert.Equal(linkCount, first.Length);
            Assert.Equal('G', first[linkIndex]);

            // 多套 → WAUT 按 activate_at 切换
            XElement waut = Assert.Single(doc.Root!.Elements("WAUT"));
            Assert.Equal("0", (string?)waut.Attribute("refId"));
            Assert.Equal(2, waut.Elements("wautSwitch").Count());
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void ForcesGreenOnNeverGreenLinkAndWarns()
    {
        Level level = Spmap.Load(TestPaths.Example);
        ExportGraph graph = ExportGraph.Build(level, 3.5);
        string dir = TempDir.Create();
        try
        {
            string netPath = Path.Combine(dir, "n.net.xml");
            Dictionary<int, int> turnToLink = SyntheticNet.Write(netPath, graph, nodeId: 0);

            // 一个灯位都不放行 → SUMO 会报 Missing green phase，写入器必须自己补
            string profile = "{\"format_version\":1,\"tls\":[{\"node\":0,\"programs\":[" +
                             "{\"id\":\"0\",\"type\":\"static\",\"phases\":[{\"duration\":30,\"states\":{}}]}," +
                             "{\"id\":\"1\",\"type\":\"static\",\"activate_at\":100,\"phases\":[{\"duration\":30,\"states\":{}}]}]}]}";

            string outPath = Path.Combine(dir, "n.tll.xml");
            IReadOnlyList<string>? warnings = SumoTllWriter.Write(netPath, graph, profile, outPath);
            Assert.NotNull(warnings);
            Assert.Contains(warnings!, w => w.Contains("补绿", StringComparison.Ordinal));

            XDocument doc = XDocument.Load(outPath);
            string state = (string?)doc.Root!.Elements("tlLogic").First().Element("phase")!.Attribute("state")
                           ?? string.Empty;
            Assert.Equal(turnToLink.Count, state.Length);
            Assert.DoesNotContain('r', state);          // 全被补成绿了
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void ReturnsNullWhenProfileHasNoTls()
    {
        Level level = Spmap.Load(TestPaths.Example);
        ExportGraph graph = ExportGraph.Build(level, 3.5);
        string dir = TempDir.Create();
        try
        {
            string netPath = Path.Combine(dir, "n.net.xml");
            SyntheticNet.Write(netPath, graph, nodeId: 0);
            string outPath = Path.Combine(dir, "n.tll.xml");

            Assert.Null(SumoTllWriter.Write(netPath, graph, string.Empty, outPath));
            Assert.Null(SumoTllWriter.Write(netPath, graph, "{\"format_version\":1}", outPath));
            Assert.False(File.Exists(outPath));         // 没有专业方案就不该产出文件
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }
}
