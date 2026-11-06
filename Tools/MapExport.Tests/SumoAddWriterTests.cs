using System.Text.Json.Nodes;
using System.Xml.Linq;
using SignalPlanner.MapExport.Sumo;
using SimCore.Io;
using Xunit;

namespace SignalPlanner.MapExport.Tests;

/// <summary>
/// S3：检测设施 → <c>.add.xml</c>，以及"公交站必须让车真停"这条配套规则。
///
/// <para>两个最容易写错的点，这里各锁一条：</para>
/// <list type="bullet">
///   <item><c>&lt;busStop&gt;</c> 本身**不会**让车停 —— 要在 rou 的 flow 里插
///         <c>&lt;stop busStop=.../&gt;</c>（2026-11-05 实测：不停时 depart→arrival 18 s，
///         插了之后 82 s）；</item>
///   <item>插 stop 会让**整条 flow** 的车都停 —— 混型（含私家车）时不能插。</item>
/// </list>
/// </summary>
public sealed class SumoAddWriterTests
{
    private static string ExamplePath =>
        Path.Combine(TestPaths.RepoRoot, "docs", "examples", "01-1-first-crossing.spmap");

    /// <summary>lane 0 有两条出口（lane 6 / lane 5），供"站台只在部分路线上"的用例。</summary>
    private static string BuildTwoWayFixture()
    {
        JsonNode root = JsonNode.Parse(File.ReadAllText(ExamplePath))!;
        root["turns"] = new JsonArray
        {
            new JsonObject { ["id"] = 0, ["src_lane"] = 0, ["dst_lane"] = 6, ["node"] = 0, ["type"] = "Straight", ["ratio"] = 0.7 },
            new JsonObject { ["id"] = 1, ["src_lane"] = 0, ["dst_lane"] = 5, ["node"] = 0, ["type"] = "Right", ["ratio"] = 0.3 },
        };
        root["demand"] = new JsonArray
        {
            new JsonObject { ["lane"] = 0, ["time_points"] = new JsonArray { 0.0 }, ["flow_rates"] = new JsonArray { 0.1 } },
        };
        root["pedestrians"] = new JsonArray();
        root["bicycles"] = new JsonArray();

        string path = Path.Combine(TempDir.Create(), "two_way.spmap");
        File.WriteAllText(path, root.ToJsonString());
        return path;
    }

    /// <summary>跑一次 net + rou + add（不经 netconvert，用合成 net）。</summary>
    private static (SumoAddWriter.Result? Add, SumoRouWriter.Result? Rou, XDocument? AddDoc, XDocument? RouDoc)
        Run(string spmapPath, string? profileJson)
    {
        Level level = Spmap.Load(spmapPath);
        ExportGraph graph = ExportGraph.Build(level, 3.5);

        string dir = Path.GetDirectoryName(spmapPath)!;
        string netPath = Path.Combine(dir, "net.xml");
        SyntheticNet.Write(netPath, graph, nodeId: 0);

        string rouPath = Path.Combine(dir, "01-1.rou.xml");
        SumoRouWriter.Result? rou = SumoRouWriter.Write(netPath, graph, level, profileJson, rouPath);

        string addPath = Path.Combine(dir, "01-1.add.xml");
        SumoAddWriter.Result? add = SumoAddWriter.Write(netPath, graph, profileJson, addPath);

        return (add, rou,
            File.Exists(addPath) ? XDocument.Load(addPath) : null,
            File.Exists(rouPath) ? XDocument.Load(rouPath) : null);
    }

    [Fact]
    public void WritesAllFourKinds()
    {
        // 车道 0 长度 180 → 各位置都放得下
        const string profile = """
        {
          "additional": [
            {"id":"e1_a","kind":"e1","lane":0,"pos":40,"freq":30},
            {"id":"e2_a","kind":"e2","lane":0,"pos":100,"length":20,"freq":60},
            {"id":"stop_a","kind":"bus_stop","lane":0,"start":40,"end":55,"lines":"bus","duration":20},
            {"id":"pa_a","kind":"parking","lane":0,"start":10,"end":30,"capacity":8}
          ]
        }
        """;

        (SumoAddWriter.Result? add, _, XDocument? doc, _) = Run(BuildTwoWayFixture(), profile);

        Assert.True(add!.Ok, add.Error);
        Assert.Equal(2, add.DetectorCount);
        Assert.Equal(1, add.BusStopCount);
        Assert.Equal(1, add.ParkingCount);
        Assert.Equal("01-1.det.xml", add.DetectorOutput);

        Assert.NotNull(doc);
        Assert.Single(doc!.Root!.Elements("e1Detector"));
        Assert.Single(doc.Root.Elements("e2Detector"));
        Assert.Single(doc.Root.Elements("busStop"));
        Assert.Single(doc.Root.Elements("parkingArea"));

        // 检测器必须带 file（不带就完全不输出数据），且统一到同一个文件
        var det = doc.Root.Elements("e1Detector").Single();
        Assert.Equal("01-1.det.xml", (string?)det.Attribute("file"));
        Assert.Equal("30", (string?)det.Attribute("freq"));

        // 车道映射：lane 0 → net 的 e0_0
        Assert.Equal("e0_0", (string?)det.Attribute("lane"));
        Assert.Equal("e0_0", (string?)doc.Root.Elements("busStop").Single().Attribute("lane"));

        Assert.Equal("8", (string?)doc.Root.Elements("parkingArea").Single().Attribute("roadsideCapacity"));
    }

    [Fact]
    public void NoFacilitiesMeansNoFile()
    {
        string dir = TempDir.Create();
        string spmap = Path.Combine(dir, "x.spmap");
        File.Copy(ExamplePath, spmap);

        Level level = Spmap.Load(spmap);
        ExportGraph graph = ExportGraph.Build(level, 3.5);
        string netPath = Path.Combine(dir, "net.xml");
        SyntheticNet.Write(netPath, graph, nodeId: 0);

        string addPath = Path.Combine(dir, "01-1.add.xml");
        SumoAddWriter.Result? add = SumoAddWriter.Write(netPath, graph, "{\"additional\":[]}", addPath);

        Assert.Null(add);                       // 空 → 返回 null
        Assert.False(File.Exists(addPath));     // 且不产出空文件
    }

    [Fact]
    public void BusStopInsertsStopOnlyForAllBusFlows()
    {
        const string profile = """
        {
          "vehicle_types": [
            {"id":"bus","preset":"bus","style":"normal"},
            {"id":"car","preset":"car","style":"normal"}
          ],
          "lane_mix": { "0": [{"id":"bus","share":100}] },
          "additional": [
            {"id":"stop_a","kind":"bus_stop","lane":0,"start":40,"end":55,"lines":"bus","duration":20}
          ]
        }
        """;

        (_, SumoRouWriter.Result? rou, _, XDocument? doc) = Run(BuildTwoWayFixture(), profile);

        Assert.True(rou!.Ok, rou.Error);
        Assert.NotNull(doc);

        var flow = doc!.Root!.Elements("flow").First();
        Assert.Equal("bus", (string?)flow.Attribute("line"));

        var stop = flow.Elements("stop").Single();
        Assert.Equal("stop_a", (string?)stop.Attribute("busStop"));
        Assert.Equal("20", (string?)stop.Attribute("duration"));
    }

    [Fact]
    public void MixedTypesDoNotGetStop()
    {
        // 该进口 50% 公交 + 50% 小汽车 → flow 共享 route，插 stop 会让私家车也停 → 必须不插
        const string profile = """
        {
          "vehicle_types": [
            {"id":"bus","preset":"bus","style":"normal"},
            {"id":"car","preset":"car","style":"normal"}
          ],
          "lane_mix": { "0": [{"id":"bus","share":50},{"id":"car","share":50}] },
          "additional": [
            {"id":"stop_a","kind":"bus_stop","lane":0,"start":40,"end":55,"lines":"bus"}
          ]
        }
        """;

        (_, SumoRouWriter.Result? rou, _, XDocument? doc) = Run(BuildTwoWayFixture(), profile);

        Assert.True(rou!.Ok, rou.Error);
        foreach (XElement flow in doc!.Root!.Elements("flow"))
        {
            Assert.Null(flow.Attribute("line"));
            Assert.Empty(flow.Elements("stop"));
        }
    }

    [Fact]
    public void StopOnPartialRouteIsSkippedWithWarning()
    {
        // 站台在 lane 5 上：lane 0 的两条候选路线里只有一条经过 e5 → 不能挂
        const string profile = """
        {
          "vehicle_types": [ {"id":"bus","preset":"bus","style":"normal"} ],
          "lane_mix": { "0": [{"id":"bus","share":100}] },
          "additional": [
            {"id":"stop_far","kind":"bus_stop","lane":5,"start":10,"end":25,"lines":"bus"}
          ]
        }
        """;

        (_, SumoRouWriter.Result? rou, _, XDocument? doc) = Run(BuildTwoWayFixture(), profile);

        Assert.True(rou!.Ok, rou.Error);
        foreach (XElement flow in doc!.Root!.Elements("flow"))
        {
            Assert.Empty(flow.Elements("stop"));
        }

        Assert.Contains(rou.Warnings, w => w.Contains("只经过部分候选路线"));
    }

    [Fact]
    public void OutOfRangeValuesAreClamped()
    {
        // pos 超出车道长度 → 夹取并告警，而不是让 SUMO 直接退出
        const string profile = """
        {
          "additional": [
            {"id":"e1_far","kind":"e1","lane":0,"pos":9999},
            {"id":"bad_span","kind":"bus_stop","lane":0,"start":100,"end":100}
          ]
        }
        """;

        (SumoAddWriter.Result? add, _, XDocument? doc, _) = Run(BuildTwoWayFixture(), profile);

        Assert.True(add!.Ok, add.Error);
        Assert.Contains(add.Warnings, w => w.Contains("超出车道长度"));
        // 零长度站台被跳过（起止差 < 1 m）
        Assert.Contains(add.Warnings, w => w.Contains("至少要 1 m"));
        Assert.Empty(doc!.Root!.Elements("busStop"));
    }
}
