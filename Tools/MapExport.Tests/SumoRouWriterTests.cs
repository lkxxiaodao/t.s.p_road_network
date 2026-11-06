using System.Text.Json.Nodes;
using System.Xml.Linq;
using SignalPlanner.MapExport.Sumo;
using SimCore.Io;
using Xunit;

namespace SignalPlanner.MapExport.Tests;

/// <summary>
/// S2：转率 → 路线枚举 → .rou.xml。
///
/// <para>用示例关卡改出"一个进口两条转向"的情形（示例本身每个进口只有一条直行，
/// 覆盖不到转率）。全程不跑 netconvert：<see cref="SyntheticNet"/> 造一份同形状的 net。</para>
/// </summary>
public sealed class SumoRouWriterTests
{
    private static string ExamplePath =>
        Path.Combine(TestPaths.RepoRoot, "docs", "examples", "01-1-first-crossing.spmap");

    /// <summary>
    /// 造一份"lane 0 有两条出口（lane 6 直行 0.7 / lane 5 右转 0.3）"的关卡。
    /// </summary>
    private static string BuildTwoWayFixture(double straightRatio, double rightRatio)
    {
        string json = File.ReadAllText(ExamplePath);
        JsonNode root = JsonNode.Parse(json)!;

        var turns = new JsonArray
        {
            new JsonObject { ["id"] = 0, ["src_lane"] = 0, ["dst_lane"] = 6, ["node"] = 0, ["type"] = "Straight", ["ratio"] = straightRatio },
            new JsonObject { ["id"] = 1, ["src_lane"] = 0, ["dst_lane"] = 5, ["node"] = 0, ["type"] = "Right", ["ratio"] = rightRatio },
            // 第二个进口（id=3）：lane 1 → lane 7，供"按进口配车型"的测试用
            new JsonObject { ["id"] = 3, ["src_lane"] = 1, ["dst_lane"] = 7, ["node"] = 0, ["type"] = "Straight" },
            // 人行横道（id=2）：示例里 8→14 是人行道过街，行人需求指它
            new JsonObject { ["id"] = 2, ["src_lane"] = 8, ["dst_lane"] = 14, ["node"] = 0, ["type"] = "Crosswalk" },
        };
        root["turns"] = turns;

        // 只留一条需求，剩下的去掉，断言更干净
        root["demand"] = new JsonArray
        {
            new JsonObject
            {
                ["lane"] = 0,
                ["time_points"] = new JsonArray { 0.0 },
                ["flow_rates"] = new JsonArray { 0.1 },
            },
        };
        root["pedestrians"] = new JsonArray();
        root["bicycles"] = new JsonArray();

        string path = Path.Combine(TempDir.Create(), "two_way.spmap");
        File.WriteAllText(path, root.ToJsonString());
        return path;
    }

    private static (SumoRouWriter.Result? Result, XDocument? Rou, string NetPath) Run(
        string spmapPath, string? profileJson = null)
    {
        Level level = Spmap.Load(spmapPath);
        ExportGraph graph = ExportGraph.Build(level, 3.5);

        string dir = Path.GetDirectoryName(spmapPath)!;
        string netPath = Path.Combine(dir, "net.xml");
        SyntheticNet.Write(netPath, graph, nodeId: 0);

        string rouPath = Path.Combine(dir, "r.rou.xml");
        SumoRouWriter.Result? result = SumoRouWriter.Write(netPath, graph, level, profileJson, rouPath);
        XDocument? doc = File.Exists(rouPath) ? XDocument.Load(rouPath) : null;
        return (result, doc, netPath);
    }

    [Fact]
    public void RoutesAreSplitByTurnRatio()
    {
        string spmap = BuildTwoWayFixture(0.7, 0.3);
        (SumoRouWriter.Result? result, XDocument? rou, _) = Run(spmap);

        Assert.NotNull(result);
        Assert.True(result!.Ok, result.Error);
        Assert.NotNull(rou);

        // 两条转向 → 两条 route，概率 0.7 / 0.3
        var dist = rou!.Root!.Elements("routeDistribution").First();
        var refs = dist.Elements("route").ToList();
        Assert.Equal(2, refs.Count);

        var probs = refs
            .Select(r => double.Parse((string)r.Attribute("probability")!,
                System.Globalization.CultureInfo.InvariantCulture))
            .OrderByDescending(p => p)
            .ToList();
        Assert.Equal(0.7, probs[0], 3);
        Assert.Equal(0.3, probs[1], 3);
    }

    [Fact]
    public void EqualRatiosMeanEqualSplit()
    {
        // ★ 2026-11-05 语义变更：ratio **就是权重**。
        //   1:1:1（或这里的两条 1:1）是合法需求，不能再被当成"没配过"。
        string spmap = BuildTwoWayFixture(1.0, 1.0);
        (SumoRouWriter.Result? result, XDocument? rou, _) = Run(spmap);

        Assert.NotNull(result);
        Assert.True(result!.Ok, result.Error);

        var refs = rou!.Root!.Elements("routeDistribution").First().Elements("route").ToList();
        Assert.Equal(2, refs.Count);

        foreach (XElement r in refs)
        {
            double p = double.Parse((string)r.Attribute("probability")!,
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(0.5, p, 3);
        }
    }

    [Fact]
    public void ThreeWayEqualSplitIsRepresentable()
    {
        // 老板的原始诉求：1:1:1 要能被表达（三条各 33.3%）
        string spmap = BuildTwoWayFixture(1.0, 1.0);
        JsonNode root = JsonNode.Parse(File.ReadAllText(spmap))!;
        ((JsonArray)root["turns"]!).Add(new JsonObject
        {
            ["id"] = 9, ["src_lane"] = 0, ["dst_lane"] = 4, ["node"] = 0,
            ["type"] = "Left", ["ratio"] = 1.0,
        });
        File.WriteAllText(spmap, root.ToJsonString());

        (SumoRouWriter.Result? result, XDocument? rou, _) = Run(spmap);
        Assert.True(result!.Ok, result.Error);

        var refs = rou!.Root!.Elements("routeDistribution").First().Elements("route").ToList();
        Assert.Equal(3, refs.Count);
        foreach (XElement r in refs)
        {
            double p = double.Parse((string)r.Attribute("probability")!,
                System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(1.0 / 3.0, p, 3);
        }
    }

    [Fact]
    public void ZeroRatioIsSkipped()
    {
        // 右转填 0 = 禁掉这条转向（掉头的默认行为就是 0）
        string spmap = BuildTwoWayFixture(0.7, 0.0);
        (SumoRouWriter.Result? result, XDocument? rou, _) = Run(spmap);

        Assert.True(result!.Ok, result.Error);
        var refs = rou!.Root!.Elements("routeDistribution").First().Elements("route").ToList();
        Assert.Single(refs);
    }

    [Fact]
    public void FlowMustBeSortedByBegin()
    {
        // ★ 2026-11-05 实测踩坑：begin 乱序时 SUMO 会静默丢弃后面 begin 更小的 flow。
        //   示例关卡有 [0,600) 与 [600,∞) 两段 → 必须按 begin 升序写在文件里。
        string spmap = BuildTwoWayFixture(0.7, 0.3);
        // 改成两段：0~600 是 360 辆/时，600 之后 720 辆/时
        JsonNode root = JsonNode.Parse(File.ReadAllText(spmap))!;
        root["demand"] = new JsonArray
        {
            new JsonObject
            {
                ["lane"] = 0,
                ["time_points"] = new JsonArray { 0.0, 600.0 },
                ["flow_rates"] = new JsonArray { 0.1, 0.2 },
            },
        };
        File.WriteAllText(spmap, root.ToJsonString());

        (SumoRouWriter.Result? result, XDocument? rou, _) = Run(spmap);
        Assert.True(result!.Ok, result.Error);

        var begins = rou!.Root!.Elements("flow")
            .Select(f => double.Parse((string)f.Attribute("begin")!,
                System.Globalization.CultureInfo.InvariantCulture))
            .ToList();

        Assert.Equal(2, begins.Count);
        for (int i = 1; i < begins.Count; i++)
        {
            Assert.True(begins[i] >= begins[i - 1], "flow 必须按 begin 升序");
        }
    }

    [Fact]
    public void PersonFlowIsWrittenWithWalkEdges()
    {
        string spmap = BuildTwoWayFixture(0.7, 0.3);
        string json = File.ReadAllText(spmap);
        JsonNode root = JsonNode.Parse(json)!;
        root["pedestrians"] = new JsonArray
        {
            new JsonObject
            {
                ["crosswalk_turn"] = 2,
                ["time_points"] = new JsonArray { 0.0 },
                ["flow_rates"] = new JsonArray { 0.05 },
            },
        };
        File.WriteAllText(spmap, root.ToJsonString());

        (SumoRouWriter.Result? result, XDocument? rou, _) = Run(spmap);
        Assert.True(result!.Ok, result.Error);

        var person = rou!.Root!.Elements("personFlow").FirstOrDefault();
        Assert.NotNull(person);
        var walk = person!.Element("walk");
        Assert.NotNull(walk);
        Assert.False(string.IsNullOrWhiteSpace((string?)walk!.Attribute("edges")));
    }

    [Fact]
    public void NoDemandProducesNoFile()
    {
        string spmap = BuildTwoWayFixture(0.7, 0.3);
        JsonNode root = JsonNode.Parse(File.ReadAllText(spmap))!;
        root["demand"] = new JsonArray();
        root["pedestrians"] = new JsonArray();
        root["bicycles"] = new JsonArray();
        File.WriteAllText(spmap, root.ToJsonString());

        (SumoRouWriter.Result? result, XDocument? rou, _) = Run(spmap);
        Assert.Null(result);
        Assert.Null(rou);
    }

    [Fact]
    public void LaneMixIsResolvedPerEntry()
    {
        // 车道 0 只跑小汽车；车道 1 是 30% 小汽车 + 70% 货车 → 只有车道 1 生成 vTypeDistribution
        string spmap = BuildTwoWayFixture(0.7, 0.3);
        JsonNode root = JsonNode.Parse(File.ReadAllText(spmap))!;
        root["demand"] = new JsonArray
        {
            new JsonObject { ["lane"] = 0, ["time_points"] = new JsonArray { 0.0 }, ["flow_rates"] = new JsonArray { 0.1 } },
            new JsonObject { ["lane"] = 1, ["time_points"] = new JsonArray { 0.0 }, ["flow_rates"] = new JsonArray { 0.1 } },
        };
        File.WriteAllText(spmap, root.ToJsonString());

        const string profile = """
        {
          "vehicle_types": [
            {"id":"car","preset":"car","style":"normal"},
            {"id":"truck","preset":"truck","style":"normal"}
          ],
          "lane_mix": {
            "0": [{"id":"car","share":100}],
            "1": [{"id":"car","share":30},{"id":"truck","share":70}]
          }
        }
        """;

        (SumoRouWriter.Result? result, XDocument? rou, _) = Run(spmap, profile);
        Assert.True(result!.Ok, result.Error);

        var flows = rou!.Root!.Elements("flow")
            .ToDictionary(f => (string)f.Attribute("id")!, f => (string)f.Attribute("type")!);

        Assert.Equal("car", flows["f0_0"]);          // 单一车型 → 直接引用
        Assert.Equal("mix_f1", flows["f1_0"]);       // 两种车型 → vTypeDistribution

        var mix = rou.Root!.Elements("vTypeDistribution").Single();
        Assert.Equal("mix_f1", (string)mix.Attribute("id")!);
        var refs = mix.Elements("vType").ToDictionary(
            e => (string)e.Attribute("refId")!,
            e => double.Parse((string)e.Attribute("probability")!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0.3, refs["car"], 3);
        Assert.Equal(0.7, refs["truck"], 3);
    }

    [Fact]
    public void LaneMixFallsBackToPresetWhenTypesAreMissing()
    {
        // lane_mix 引用了库里不存在的车型 → 视为空，回落到关卡预设（city 混合 = 3 种）
        string spmap = BuildTwoWayFixture(0.7, 0.3);
        JsonNode root = JsonNode.Parse(File.ReadAllText(spmap))!;
        root["game_rules"]!["vehicle_mix"] = "CityMix";
        File.WriteAllText(spmap, root.ToJsonString());

        const string profile = """
        {
          "vehicle_types": [{"id":"car","preset":"car","style":"normal"}],
          "lane_mix": { "0": [{"id":"ghost","share":100}] }
        }
        """;

        (SumoRouWriter.Result? result, XDocument? rou, _) = Run(spmap, profile);
        Assert.True(result!.Ok, result.Error);

        // 库里只有 car，没有 share → 再回落到 CityMix 预设（car/truck/bus 三种）
        var dist = rou!.Root!.Elements("vTypeDistribution").FirstOrDefault();
        Assert.NotNull(dist);
        Assert.Equal(3, dist!.Elements("vType").Count());
    }

    [Fact]
    public void ProfileFlowsOverrideSpmapDemand()
    {
        // ★ 回归用：`flows[]` 的 DTO 属性一度写成 internal，System.Text.Json 默认**不读**，
        //   于是专业档的车流配置被静默忽略、回落到 .spmap 曲线 —— 而且不报错。
        //   S2 起就一直是这样，一直到 S3b 做公交线路时才撞出来。
        string spmap = BuildTwoWayFixture(0.7, 0.3);

        const string profile = """
        {
          "flows": [
            {
              "id": "custom",
              "lane": 0,
              "segments": [ {"begin": 0, "end": 600, "veh_per_hour": 900} ]
            }
          ]
        }
        """;

        (SumoRouWriter.Result? result, XDocument? rou, _) = Run(spmap, profile);
        Assert.True(result!.Ok, result.Error);

        var flows = rou!.Root!.Elements("flow").ToList();
        var sole = Assert.Single(flows);
        // flow id = 请求 id + 起始时间（同一请求可能分多段）
        Assert.StartsWith("custom", (string?)sole.Attribute("id"));
        Assert.Equal("900", (string?)sole.Attribute("vehsPerHour"));
    }

    [Fact]
    public void BusLineProducesRouteAndStops()
    {
        // 公交线路：一条固定 route + 按 period 发车 + 按顺序停站
        string spmap = BuildTwoWayFixture(0.7, 0.3);

        const string profile = """
        {
          "additional": [
            {"id":"stop_a","kind":"bus_stop","lane":0,"start":40,"end":55,"lines":"1","duration":20}
          ],
          "bus_lines": [
            {"id":"bus_1","line":"1","lane":0,"headway":120,"begin":0,"end":600,"stops":["stop_a"]}
          ]
        }
        """;

        (SumoRouWriter.Result? result, XDocument? rou, _) = Run(spmap, profile);
        Assert.True(result!.Ok, result.Error);
        Assert.Equal(1, result.BusLineCount);

        // 自己的 route（不是 routeDistribution —— 公交走固定一条线）
        var route = rou!.Root!.Elements("route").Single(r => (string?)r.Attribute("id") == "r_bus_1");
        Assert.False(string.IsNullOrWhiteSpace((string?)route.Attribute("edges")));

        var flow = rou.Root.Elements("flow").Single(f => (string?)f.Attribute("id") == "bus_1");
        Assert.Equal("1", (string?)flow.Attribute("line"));
        Assert.Equal("120", (string?)flow.Attribute("period"));
        Assert.Equal("r_bus_1", (string?)flow.Attribute("route"));

        var stop = flow.Elements("stop").Single();
        Assert.Equal("stop_a", (string?)stop.Attribute("busStop"));
        Assert.Equal("20", (string?)stop.Attribute("duration"));

        // 公交车型不存在时自动补一条
        Assert.Contains(rou.Root.Elements("vType"), v => (string?)v.Attribute("vClass") == "bus");
    }

    [Fact]
    public void BusLineSkipsStopNotOnRoute()
    {
        // 站不在主路线上 → 过滤掉 + 告警（不能让 SUMO 报 busStop not on route 直接退出）
        string spmap = BuildTwoWayFixture(0.7, 0.3);

        const string profile = """
        {
          "additional": [
            {"id":"on_route","kind":"bus_stop","lane":0,"start":40,"end":55,"lines":"1"},
            {"id":"off_route","kind":"bus_stop","lane":12,"start":10,"end":25,"lines":"1"}
          ],
          "bus_lines": [
            {"id":"bus_1","line":"1","lane":0,"headway":300,"stops":["on_route","off_route"]}
          ]
        }
        """;

        (SumoRouWriter.Result? result, XDocument? rou, _) = Run(spmap, profile);
        Assert.True(result!.Ok, result.Error);

        var flow = rou!.Root!.Elements("flow").Single(f => (string?)f.Attribute("id") == "bus_1");
        var ids = flow.Elements("stop").Select(e => (string?)e.Attribute("busStop")).ToList();
        Assert.Contains("on_route", ids);
        Assert.DoesNotContain("off_route", ids);

        Assert.Contains(result.Warnings, w => w.Contains("off_route") && w.Contains("不在这条路线上"));
    }
}
