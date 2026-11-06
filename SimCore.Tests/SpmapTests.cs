using System.Text.Json.Nodes;
using SimCore.Io;
using SimCore.Model;
using Xunit;

namespace SimCore.Tests;

/// <summary>
/// T0.3「<c>.spmap</c> 格式 + 读写 + 校验器」验收测试。
/// <para>分组与验收标准逐条对应：
/// <list type="bullet">
///   <item>A 组 → 验收标准 a) 往返读写（保存 → 加载）后对象等价</item>
///   <item>B 组 → 验收标准 b) 非法地图能报出具体错误条目（孤儿节点 / 转向引用不存在 / 转向比例越界 / 信号路口无相位，及补充项）</item>
///   <item>C 组 → 正例：合法地图（含随交付的示例 <c>.spmap</c>）零错误</item>
///   <item>D 组 → 契约与确定性守卫</item>
/// </list>
/// </para>
/// </summary>
public sealed class SpmapTests
{
    // ------------------------------------------------------------------
    // A 组：验收标准 a) 往返读写后对象等价
    // ------------------------------------------------------------------

    /// <summary>a1：把一个"全要素"关卡 保存 → 加载，往返后逐字段等价（含转向比例、锚点、原始 JSON 扩展）。</summary>
    [Fact]
    public void A1_RoundTrip_Preserves_Every_Field()
    {
        Level original = BuildFullLevel();

        string path = TempFile("roundtrip-full.spmap");
        Spmap.Save(original, path);
        Level reloaded = Spmap.Load(path);

        AssertLevelEquivalent(original, reloaded);
    }

    /// <summary>a2：随交付的示例文件 加载 → 保存 → 加载，两次加载结果完全等价（往返无损）。</summary>
    [Fact]
    public void A2_RoundTrip_Of_Shipped_Example_Is_Lossless()
    {
        string example = PathOf("docs", "examples", "01-1-first-crossing.spmap");

        Level first = Spmap.Load(example);
        string copy = TempFile("roundtrip-example.spmap");
        Spmap.Save(first, copy);
        Level second = Spmap.Load(copy);

        AssertLevelEquivalent(first, second);

        // 也验证"再存一次"字节级稳定（幂等）。
        string copy2 = TempFile("roundtrip-example-2.spmap");
        Spmap.Save(second, copy2);
        Assert.Equal(File.ReadAllText(copy), File.ReadAllText(copy2));
    }

    /// <summary>a3：写出的字段名与 §3.2 逐字一致（小写下划线），且不出现 PascalCase 泄漏。</summary>
    [Fact]
    public void A3_Json_Field_Names_Match_Section_3_2()
    {
        string path = TempFile("field-names.spmap");
        Spmap.Save(BuildFullLevel(), path);
        string json = File.ReadAllText(path);

        string[] expected =
        [
            "\"format_version\"", "\"meta\"", "\"nodes\"", "\"lanes\"", "\"turns\"", "\"signals\"",
            "\"demand\"", "\"pedestrians\"", "\"bicycles\"", "\"game_rules\"",
            "\"name\"", "\"author\"", "\"chapter\"", "\"index\"",
            "\"from_node\"", "\"to_node\"", "\"speed_limit\"", "\"lane_index\"", "\"road\"",
            "\"src_lane\"", "\"dst_lane\"", "\"node\"", "\"ratio\"",
            "\"offset\"", "\"plans\"", "\"start_time\"", "\"stages\"",
            "\"protected\"", "\"permitted\"", "\"duration\"",
            "\"lane\"", "\"time_points\"", "\"flow_rates\"",
            "\"crosswalk_turn\"", "\"mode\"",
            "\"anchors\"", "\"weights\"", "\"events\"", "\"tutorial\"",
        ];

        foreach (string token in expected)
        {
            Assert.Contains(token, json, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("FormatVersion", json, StringComparison.Ordinal);
        Assert.DoesNotContain("FromNode", json, StringComparison.Ordinal);
        Assert.DoesNotContain("FlowRates", json, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // B 组：验收标准 b) 非法地图报出具体错误条目
    // ------------------------------------------------------------------

    /// <summary>b1：孤儿节点（未连接任何车道）被报出。</summary>
    [Fact]
    public void B1_Orphan_Node_Is_Reported()
    {
        Level level = new()
        {
            Network = new Network
            {
                Nodes = Nodes(3),
                Lanes = new[] { LaneOf(0, from: 0, to: 1) },
            },
        };

        ValidationError[] errors = SpmapValidator.Validate(level);

        AssertHasCode(errors, "orphan_node");
        Assert.Contains(errors, e => e.Path == "nodes[2]");
    }

    /// <summary>b2：转向引用了不存在的车道被报出（LightSim: from_link/to_link not found）。</summary>
    [Fact]
    public void B2_Dangling_Turn_Reference_Is_Reported()
    {
        Level level = new()
        {
            Network = new Network
            {
                Nodes = Nodes(2),
                Lanes = new[] { LaneOf(0, from: 1, to: 0) },
                Turns = new[] { TurnOf(0, src: 0, dst: 99, node: 0) },
            },
        };

        ValidationError[] errors = SpmapValidator.Validate(level);

        AssertHasCode(errors, "dangling_reference");
        Assert.Contains(errors, e => e.Path == "turns[0].dst_lane");
    }

    /// <summary>b2b：车道端点引用不存在的节点也被报出。</summary>
    [Fact]
    public void B2b_Dangling_Lane_Endpoint_Is_Reported()
    {
        Level level = new()
        {
            Network = new Network
            {
                Nodes = Nodes(2),
                Lanes = new[] { LaneOf(0, from: 0, to: 7) },
            },
        };

        ValidationError[] errors = SpmapValidator.Validate(level);

        AssertHasCode(errors, "dangling_reference");
        Assert.Contains(errors, e => e.Path == "lanes[0].to_node");
    }

    /// <summary>b3：转向比例越界（不在 (0,1]）被报出（LightSim: turn_ratio not in (0,1]）。</summary>
    [Theory]
    [InlineData(1.5)]
    [InlineData(0.0)]
    [InlineData(-0.2)]
    public void B3_Turn_Ratio_Out_Of_Range_Is_Reported(double ratio)
    {
        Level level = new()
        {
            Network = new Network
            {
                Nodes = Nodes(2),
                Lanes = new[] { LaneOf(0, from: 1, to: 0), LaneOf(1, from: 0, to: 1) },
                Turns = new[] { TurnOf(0, src: 0, dst: 1, node: 0) },
            },
            TurnRatios = new[] { new TurnRatio(0, ratio) },
        };

        ValidationError[] errors = SpmapValidator.Validate(level);

        AssertHasCode(errors, "turn_ratio_out_of_range");
    }

    /// <summary>b4：信号路口没有任何相位被报出（LightSim: signalized but has no phases）。</summary>
    [Fact]
    public void B4_Signalized_Without_Phases_Is_Reported()
    {
        Level level = new()
        {
            Network = new Network
            {
                Nodes = new[] { new Node { Id = 0, Type = NodeType.Signalized }, new Node { Id = 1, Type = NodeType.Unsignalized } },
                Lanes = new[] { LaneOf(0, from: 1, to: 0) },
            },
        };

        ValidationError[] errors = SpmapValidator.Validate(level);

        AssertHasCode(errors, "signalized_without_phases");
        Assert.Contains(errors, e => e.Path == "signals[node:0]");
    }

    /// <summary>b5：相位引用了不存在的转向被报出（LightSim: movement not found）。</summary>
    [Fact]
    public void B5_Stage_Referencing_Missing_Turn_Is_Reported()
    {
        Level level = new()
        {
            Network = new Network
            {
                Nodes = new[] { new Node { Id = 0, Type = NodeType.Signalized }, new Node { Id = 1, Type = NodeType.Unsignalized } },
                Lanes = new[] { LaneOf(0, from: 1, to: 0) },
                Turns = Array.Empty<Turn>(),
            },
            // 让节点 0 成为信号路口并带一个相位，但相位引用的转向不存在。
            GameRules = new GameRules(),
        };

        level = ReplaceNode(level, 0, new Node
        {
            Id = 0,
            Type = NodeType.Signalized,
            SignalPlans = new[]
            {
                new SignalPlan { StartTime = 0, Stages = new[] { new Stage { Duration = 30, ProtectedTurnIds = new[] { 42 } } } },
            },
        });

        ValidationError[] errors = SpmapValidator.Validate(level);

        AssertHasCode(errors, "dangling_reference");
        Assert.Contains(errors, e => e.Path == "signals[node:0].plans[0].stages[0].protected[0]");
    }

    /// <summary>b6：行人需求指向的不是人行横道转向、或非机动车需求指向了非非机动车道，被报出。</summary>
    [Fact]
    public void B6_Mode_And_Crosswalk_Reference_Checks()
    {
        Level level = new()
        {
            Network = new Network
            {
                Nodes = Nodes(2),
                Lanes = new[] { LaneOf(0, from: 1, to: 0), LaneOf(1, from: 0, to: 1) },
                Turns = new[] { TurnOf(0, src: 0, dst: 1, node: 0) }, // 直行，非 Crosswalk
            },
            Pedestrians = new[] { new PedestrianDemand { CrosswalkTurn = 0 } },
            Bicycles = new[] { new BicycleDemand { Lane = 0, Mode = BicycleMode.Independent } }, // 车道 0 是 Driving
        };

        ValidationError[] errors = SpmapValidator.Validate(level);

        AssertHasCode(errors, "pedestrian_turn_not_crosswalk");
        AssertHasCode(errors, "bicycle_lane_not_biking");
    }

    /// <summary>b7：需求曲线长度不一致 / 时间点非递增被报出；schema 版本不支持被报出。</summary>
    [Fact]
    public void B7_Demand_Profile_And_Format_Version_Checks()
    {
        Level level = new()
        {
            FormatVersion = 99,
            Network = new Network
            {
                Nodes = Nodes(2),
                Lanes = new[] { LaneOf(0, from: 1, to: 0) },
            },
            Demand = new[]
            {
                new DemandPlan { Lane = 0, TimePoints = new[] { 0.0, 300.0, 600.0 }, FlowRates = new[] { 0.1 } },
            },
        };

        ValidationError[] errors = SpmapValidator.Validate(level);

        AssertHasCode(errors, "format_version_unsupported");
        AssertHasCode(errors, "demand_profile_length_mismatch");
    }

    // ------------------------------------------------------------------
    // C 组：正例 —— 合法地图零错误
    // ------------------------------------------------------------------

    /// <summary>c1：随交付的示例 <c>.spmap</c> 通过校验（无任何错误）。</summary>
    [Fact]
    public void C1_Shipped_Example_Has_No_Errors()
    {
        Level level = Spmap.Load(PathOf("docs", "examples", "01-1-first-crossing.spmap"));

        ValidationError[] errors = SpmapValidator.Validate(level);

        ValidationError[] problems = errors.Where(e => e.IsError).ToArray();
        Assert.True(problems.Length == 0, "示例文件应零错误，实际：" + Join(errors));
    }

    /// <summary>c2：程序构造的"全要素"合法关卡零错误（仅允许警告）。</summary>
    [Fact]
    public void C2_Full_Level_Has_No_Errors()
    {
        ValidationError[] errors = SpmapValidator.Validate(BuildFullLevel());

        Assert.DoesNotContain(errors, e => e.IsError);
    }

    /// <summary>c3：校验器只读——校验前后关卡对象（含网络拓扑）不变。</summary>
    [Fact]
    public void C3_Validator_Does_Not_Mutate_Level()
    {
        Level build = BuildFullLevel();
        Level before = BuildFullLevel();

        SpmapValidator.Validate(build);

        AssertLevelEquivalent(before, build);
    }

    // ------------------------------------------------------------------
    // D 组：契约与确定性守卫
    // ------------------------------------------------------------------

    /// <summary>d1：校验结论顺序确定——同一关卡连续校验两次结果逐条一致。</summary>
    [Fact]
    public void D1_Validation_Is_Deterministic()
    {
        Level broken = new()
        {
            Network = new Network
            {
                Nodes = Nodes(4),
                Lanes = new[] { LaneOf(0, from: 0, to: 9), LaneOf(1, from: 1, to: 0) },
                Turns = new[] { TurnOf(0, src: 0, dst: 5, node: 0), TurnOf(1, src: 0, dst: 1, node: 1) },
            },
            TurnRatios = new[] { new TurnRatio(0, 2.0) },
            Demand = new[] { new DemandPlan { Lane = 3 } },
        };

        ValidationError[] first = SpmapValidator.Validate(broken);
        ValidationError[] second = SpmapValidator.Validate(broken);

        Assert.Equal(first.Select(Describe), second.Select(Describe));
    }

    /// <summary>d2：空关卡（无节点 / 无车道）是合法的——不误报孤儿节点、不崩溃。</summary>
    [Fact]
    public void D2_Empty_Level_Is_Valid()
    {
        ValidationError[] errors = SpmapValidator.Validate(new Level());

        Assert.DoesNotContain(errors, e => e.IsError);
    }

    /// <summary>d3：Load/Save 的入参守卫——null 关卡、空路径被明确拒绝。</summary>
    [Fact]
    public void D3_Load_Save_Reject_Bad_Arguments()
    {
        Assert.Throws<ArgumentNullException>(() => Spmap.Save(null!, TempFile("x.spmap")));
        Assert.Throws<ArgumentException>(() => Spmap.Save(new Level(), "  "));
        Assert.Throws<ArgumentException>(() => Spmap.Load(""));
        Assert.Throws<ArgumentNullException>(() => SpmapValidator.Validate(null!));
    }

    // ------------------------------------------------------------------
    // E 组：T4.3 S1 新增的两个可选字段（lat_index / vehicle_mix）
    // ------------------------------------------------------------------

    /// <summary>e1：两个字段都未设置时**不写进文件**——旧关卡往返逐字节不变。</summary>
    [Fact]
    public void E1_New_Fields_Are_Omitted_When_Unset()
    {
        string path = TempFile("new-fields-unset.spmap");
        Spmap.Save(BuildFullLevel(), path);
        string json = File.ReadAllText(path);

        Assert.DoesNotContain("\"lat_index\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"vehicle_mix\"", json, StringComparison.Ordinal);

        Level reloaded = Spmap.Load(path);
        Assert.Null(reloaded.GameRules.VehicleMix);
        Assert.All(reloaded.Network.Lanes, lane => Assert.Equal(-1, lane.LatIndex));
    }

    /// <summary>e2：两个字段带值时保存 → 加载逐字段等价，且字段名为小写下划线。</summary>
    [Fact]
    public void E2_New_Fields_RoundTrip_With_Values()
    {
        Level original = new()
        {
            FormatVersion = Spmap.CurrentFormatVersion,
            Meta = new LevelMeta { Name = "车型往返", Author = "test", Chapter = 4, Index = 2 },
            Network = new Network
            {
                Nodes = new[]
                {
                    new Node { Id = 0, Type = NodeType.Origin, X = 0, Y = 0 },
                    new Node { Id = 1, Type = NodeType.Destination, X = 180, Y = 0 },
                },
                Lanes = new[]
                {
                    new Lane
                    {
                        Id = 0, Road = 0, Type = LaneType.Driving, FromNode = 0, ToNode = 1,
                        Length = 180.0, SpeedLimit = 13.89, LaneIndex = 0, LatIndex = 0,
                    },
                    new Lane
                    {
                        Id = 1, Road = 0, Type = LaneType.Sidewalk, FromNode = 1, ToNode = 0,
                        Length = 180.0, SpeedLimit = 1.4, LaneIndex = 0, LatIndex = 1,
                    },
                },
                Turns = Array.Empty<Turn>(),
            },
            GameRules = new GameRules { VehicleMix = VehicleMix.FreightHeavy },
        };

        string path = TempFile("new-fields-roundtrip.spmap");
        Spmap.Save(original, path);
        string json = File.ReadAllText(path);

        Assert.Contains("\"lat_index\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"vehicle_mix\": \"FreightHeavy\"", json, StringComparison.Ordinal);

        Level reloaded = Spmap.Load(path);
        AssertLevelEquivalent(original, reloaded);
        Assert.Equal(VehicleMix.FreightHeavy, reloaded.GameRules.VehicleMix);
    }

    /// <summary>e3：未标注的 lane（LatIndex=-1）读回来仍是 -1，不会被写成 0。</summary>
    [Fact]
    public void E3_Unset_LatIndex_Stays_Minus_One()
    {
        Level level = new()
        {
            FormatVersion = Spmap.CurrentFormatVersion,
            Meta = new LevelMeta { Name = "既有关卡", Author = "test", Chapter = 1, Index = 1 },
            Network = new Network
            {
                Nodes = new[]
                {
                    new Node { Id = 0, Type = NodeType.Origin, X = 0, Y = 0 },
                    new Node { Id = 1, Type = NodeType.Destination, X = 100, Y = 0 },
                },
                Lanes = new[] { LaneOf(0, from: 0, to: 1) },
                Turns = Array.Empty<Turn>(),
            },
        };

        string path = TempFile("lat-unset.spmap");
        Spmap.Save(level, path);

        Assert.Equal(-1, Spmap.Load(path).Network.Lanes[0].LatIndex);
    }

    /// <summary>e4：`vehicle_mix` 写了非法名字 → 枚举解析失败（桥接层会转成 load_failed，不会静默放行）。</summary>
    [Fact]
    public void E4_Unknown_Vehicle_Mix_Throws()
    {
        string path = TempFile("bad-vehicle-mix.spmap");
        File.WriteAllText(path, """
        {
          "format_version": 1,
          "meta": { "name": "x", "author": "", "chapter": 1, "index": 1 },
          "nodes": [], "lanes": [], "turns": [], "signals": [],
          "demand": [], "pedestrians": [], "bicycles": [],
          "game_rules": {
            "anchors": { "R": [0, 0], "D": [0, 0], "P": [0, 0], "Q": [0, 0] },
            "weights": { "R": 0.4, "D": 0.2, "P": 0.2, "Q": 0.2 },
            "vehicle_mix": "FlyingCar",
            "events": [], "tutorial": []
          }
        }
        """);

        Assert.Throws<System.Text.Json.JsonException>(() => Spmap.Load(path));
    }

    /// <summary>e5：随交付的示例文件不受新字段影响（无 vehicle_mix、全部 lat_index = -1）。</summary>
    [Fact]
    public void E5_Shipped_Example_Has_No_New_Fields()
    {
        Level level = Spmap.Load(PathOf("docs", "examples", "01-1-first-crossing.spmap"));

        Assert.Null(level.GameRules.VehicleMix);
        Assert.All(level.Network.Lanes, lane => Assert.Equal(-1, lane.LatIndex));
    }

    // ------------------------------------------------------------------
    // 夹具
    // ------------------------------------------------------------------

    /// <summary>一个合法、全要素的四路口关卡（含信号两相位、四类需求、评分规则、转向比例与原始 JSON 扩展）。</summary>
    private static Level BuildFullLevel()
    {
        var nodes = new List<Node>
        {
            new()
            {
                Id = 0,
                Type = NodeType.Signalized,
                X = 0,
                Y = 0,
                Offset = 0,
                SignalPlans = new[]
                {
                    new SignalPlan
                    {
                        StartTime = 0,
                        Stages = new[]
                        {
                            new Stage { Type = StageType.Fixed, Duration = 30, ProtectedTurnIds = new[] { 0, 2, 4 }, PermittedTurnIds = new[] { 6, 8 } },
                            new Stage { Type = StageType.Fixed, Duration = 30, ProtectedTurnIds = new[] { 1, 3, 5 }, PermittedTurnIds = new[] { 7, 9 } },
                        },
                    },
                },
            },
            new() { Id = 1, Type = NodeType.Unsignalized, X = 0, Y = -180 },
            new() { Id = 2, Type = NodeType.Unsignalized, X = 180, Y = 0 },
            new() { Id = 3, Type = NodeType.Unsignalized, X = 0, Y = 180 },
            new() { Id = 4, Type = NodeType.Unsignalized, X = -180, Y = 0 },
        };

        var lanes = new List<Lane>();
        for (int i = 0; i < 4; i++)
        {
            lanes.Add(LaneOf(i, from: i + 1, to: 0, type: LaneType.Driving));
            lanes.Add(LaneOf(i + 4, from: 0, to: i + 1, type: LaneType.Driving));
            lanes.Add(LaneOf(i + 8, from: i + 1, to: 0, type: LaneType.Sidewalk, speed: 1.4));
            lanes.Add(LaneOf(i + 12, from: 0, to: i + 1, type: LaneType.Sidewalk, speed: 1.4));
        }

        lanes.Add(LaneOf(16, from: 1, to: 0, type: LaneType.Biking, speed: 5.0));
        lanes.Add(LaneOf(17, from: 2, to: 0, type: LaneType.Biking, speed: 5.0));
        lanes.Add(LaneOf(18, from: 0, to: 1, type: LaneType.Biking, speed: 5.0));
        lanes.Add(LaneOf(19, from: 0, to: 2, type: LaneType.Biking, speed: 5.0));

        var turns = new List<Turn>
        {
            TurnOf(0, src: 0, dst: 6, node: 0),
            TurnOf(1, src: 1, dst: 7, node: 0),
            TurnOf(2, src: 2, dst: 4, node: 0),
            TurnOf(3, src: 3, dst: 5, node: 0),
            TurnOf(4, src: 16, dst: 18, node: 0),
            TurnOf(5, src: 17, dst: 19, node: 0),
            TurnOf(6, src: 8, dst: 14, node: 0, type: TurnType.Crosswalk),
            TurnOf(7, src: 9, dst: 15, node: 0, type: TurnType.Crosswalk),
            TurnOf(8, src: 10, dst: 12, node: 0, type: TurnType.Crosswalk),
            TurnOf(9, src: 11, dst: 13, node: 0, type: TurnType.Crosswalk),
        };

        return new Level
        {
            FormatVersion = Spmap.CurrentFormatVersion,
            Meta = new LevelMeta { Name = "1-1 第一次通车", Author = "signal-planner", Chapter = 1, Index = 1 },
            Network = new Network { Nodes = nodes, Lanes = lanes, Turns = turns },
            TurnRatios = new[] { new TurnRatio(0, 0.35), new TurnRatio(1, 1.0) },
            Demand = new[]
            {
                new DemandPlan { Lane = 0, TimePoints = new[] { 0.0, 600.0 }, FlowRates = new[] { 0.1, 0.12 } },
                new DemandPlan { Lane = 1, TimePoints = new[] { 0.0, 600.0 }, FlowRates = new[] { 0.1, 0.12 } },
                new DemandPlan { Lane = 2, TimePoints = new[] { 0.0, 600.0 }, FlowRates = new[] { 0.08, 0.1 } },
                new DemandPlan { Lane = 3, TimePoints = new[] { 0.0, 600.0 }, FlowRates = new[] { 0.08, 0.1 } },
            },
            Pedestrians = new[]
            {
                new PedestrianDemand { CrosswalkTurn = 6, TimePoints = new[] { 0.0, 600.0 }, FlowRates = new[] { 0.05, 0.05 } },
                new PedestrianDemand { CrosswalkTurn = 8, TimePoints = new[] { 0.0, 600.0 }, FlowRates = new[] { 0.05, 0.06 } },
            },
            Bicycles = new[]
            {
                new BicycleDemand { Lane = 16, Mode = BicycleMode.Independent, TimePoints = new[] { 0.0, 600.0 }, FlowRates = new[] { 0.03, 0.04 } },
                new BicycleDemand { Lane = 17, Mode = BicycleMode.Mixed, TimePoints = new[] { 0.0, 600.0 }, FlowRates = new[] { 0.03, 0.04 } },
            },
            GameRules = new GameRules
            {
                Anchors = new MetricAnchors
                {
                    R = new MetricAnchorValue(0.8, 1.0),
                    D = new MetricAnchorValue(60, 20),
                    P = new MetricAnchorValue(2.5, 1.2),
                    Q = new MetricAnchorValue(1.0, 0.5),
                },
                Weights = MetricWeights.Default,
                Events = JsonNode.Parse("""[{"kind":"stalled_car","lane":2,"from":0,"to":60}]"""),
                Tutorial = JsonNode.Parse("""[{"text":"点击路口打开相位面板"}]"""),
            },
        };
    }

    private static Node[] Nodes(int count)
    {
        var nodes = new Node[count];
        for (int i = 0; i < count; i++)
        {
            nodes[i] = new Node { Id = i, Type = NodeType.Unsignalized };
        }

        return nodes;
    }

    private static Lane LaneOf(int id, int from, int to, LaneType type = LaneType.Driving, double speed = 13.89) => new()
    {
        Id = id,
        Road = id + 1,
        Type = type,
        FromNode = from,
        ToNode = to,
        Length = 180.0,
        SpeedLimit = speed,
        LaneIndex = 0,
    };

    private static Turn TurnOf(int id, int src, int dst, int node, TurnType type = TurnType.Straight) => new()
    {
        Id = id,
        SrcLane = src,
        DstLane = dst,
        Node = node,
        Type = type,
    };

    private static Level ReplaceNode(Level level, int nodeId, Node replacement)
    {
        var nodes = new List<Node>(level.Network.Nodes.Count);
        foreach (Node node in level.Network.Nodes)
        {
            nodes.Add(node.Id == nodeId ? replacement : node);
        }

        return new Level
        {
            FormatVersion = level.FormatVersion,
            Meta = level.Meta,
            Network = new Network { Nodes = nodes, Lanes = level.Network.Lanes, Turns = level.Network.Turns },
            TurnRatios = level.TurnRatios,
            Demand = level.Demand,
            Pedestrians = level.Pedestrians,
            Bicycles = level.Bicycles,
            GameRules = level.GameRules,
        };
    }

    // ------------------------------------------------------------------
    // 断言与工具
    // ------------------------------------------------------------------

    private static void AssertHasCode(ValidationError[] errors, string code)
        => Assert.True(errors.Any(e => e.Code == code), $"期望错误码 '{code}'，实际：{Join(errors)}");

    private static string Join(IEnumerable<ValidationError> errors)
        => string.Join(" | ", errors.Select(Describe));

    private static string Describe(ValidationError error) => error.ToString();

    private static void AssertLevelEquivalent(Level expected, Level actual)
    {
        Assert.Equal(expected.FormatVersion, actual.FormatVersion);

        Assert.Equal(expected.Meta.Name, actual.Meta.Name);
        Assert.Equal(expected.Meta.Author, actual.Meta.Author);
        Assert.Equal(expected.Meta.Chapter, actual.Meta.Chapter);
        Assert.Equal(expected.Meta.Index, actual.Meta.Index);

        AssertNodes(expected.Network.Nodes, actual.Network.Nodes);
        AssertLanes(expected.Network.Lanes, actual.Network.Lanes);
        AssertTurns(expected.Network.Turns, actual.Network.Turns);
        AssertRatios(expected.TurnRatios, actual.TurnRatios);

        AssertProfiles(
            expected.Demand.Select(d => (d.Lane, d.TimePoints, d.FlowRates)),
            actual.Demand.Select(d => (d.Lane, d.TimePoints, d.FlowRates)));
        AssertProfiles(
            expected.Pedestrians.Select(d => (d.CrosswalkTurn, d.TimePoints, d.FlowRates)),
            actual.Pedestrians.Select(d => (d.CrosswalkTurn, d.TimePoints, d.FlowRates)));
        AssertProfiles(
            expected.Bicycles.Select(d => (d.Lane, d.TimePoints, d.FlowRates)),
            actual.Bicycles.Select(d => (d.Lane, d.TimePoints, d.FlowRates)));
        Assert.Equal(
            expected.Bicycles.Select(b => b.Mode),
            actual.Bicycles.Select(b => b.Mode));

        Assert.Equal(expected.GameRules.Anchors.R, actual.GameRules.Anchors.R);
        Assert.Equal(expected.GameRules.Anchors.D, actual.GameRules.Anchors.D);
        Assert.Equal(expected.GameRules.Anchors.P, actual.GameRules.Anchors.P);
        Assert.Equal(expected.GameRules.Anchors.Q, actual.GameRules.Anchors.Q);
        Assert.Equal(expected.GameRules.Weights.R, actual.GameRules.Weights.R);
        Assert.Equal(expected.GameRules.Weights.D, actual.GameRules.Weights.D);
        Assert.Equal(expected.GameRules.Weights.P, actual.GameRules.Weights.P);
        Assert.Equal(expected.GameRules.Weights.Q, actual.GameRules.Weights.Q);
        Assert.True(
            JsonNode.DeepEquals(expected.GameRules.Events, actual.GameRules.Events),
            "events 原始 JSON 往返不等价。");
        Assert.True(
            JsonNode.DeepEquals(expected.GameRules.Tutorial, actual.GameRules.Tutorial),
            "tutorial 原始 JSON 往返不等价。");
        Assert.Equal(expected.GameRules.VehicleMix, actual.GameRules.VehicleMix);
    }

    private static void AssertNodes(IReadOnlyList<Node> expected, IReadOnlyList<Node> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Node e = expected[i];
            Node a = actual[i];
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.Type, a.Type);
            Assert.Equal(e.X, a.X);
            Assert.Equal(e.Y, a.Y);
            Assert.Equal(e.Offset, a.Offset);
            Assert.Equal(e.SignalPlans.Count, a.SignalPlans.Count);
            for (int p = 0; p < e.SignalPlans.Count; p++)
            {
                Assert.Equal(e.SignalPlans[p].StartTime, a.SignalPlans[p].StartTime);
                Assert.Equal(e.SignalPlans[p].Stages.Count, a.SignalPlans[p].Stages.Count);
                for (int s = 0; s < e.SignalPlans[p].Stages.Count; s++)
                {
                    Stage es = e.SignalPlans[p].Stages[s];
                    Stage as_ = a.SignalPlans[p].Stages[s];
                    Assert.Equal(es.Type, as_.Type);
                    Assert.Equal(es.Duration, as_.Duration);
                    Assert.Equal(es.ProtectedTurnIds, as_.ProtectedTurnIds);
                    Assert.Equal(es.PermittedTurnIds, as_.PermittedTurnIds);
                }
            }
        }
    }

    private static void AssertLanes(IReadOnlyList<Lane> expected, IReadOnlyList<Lane> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Lane e = expected[i];
            Lane a = actual[i];
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.Road, a.Road);
            Assert.Equal(e.Type, a.Type);
            Assert.Equal(e.FromNode, a.FromNode);
            Assert.Equal(e.ToNode, a.ToNode);
            Assert.Equal(e.Length, a.Length);
            Assert.Equal(e.SpeedLimit, a.SpeedLimit);
            Assert.Equal(e.LaneIndex, a.LaneIndex);
            Assert.Equal(e.LatIndex, a.LatIndex);
        }
    }

    private static void AssertTurns(IReadOnlyList<Turn> expected, IReadOnlyList<Turn> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Turn e = expected[i];
            Turn a = actual[i];
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.SrcLane, a.SrcLane);
            Assert.Equal(e.DstLane, a.DstLane);
            Assert.Equal(e.Node, a.Node);
            Assert.Equal(e.Type, a.Type);
        }
    }

    private static void AssertRatios(IReadOnlyList<TurnRatio> expected, IReadOnlyList<TurnRatio> actual)
    {
        Dictionary<int, double> e = expected.ToDictionary(r => r.Turn, r => r.Ratio);
        Dictionary<int, double> a = actual.ToDictionary(r => r.Turn, r => r.Ratio);
        Assert.Equal(e.Count, a.Count);
        foreach (KeyValuePair<int, double> pair in e)
        {
            Assert.True(a.TryGetValue(pair.Key, out double value), $"往返后缺少转向 {pair.Key} 的比例。");
            Assert.Equal(pair.Value, value);
        }
    }

    private static void AssertProfiles(
        IEnumerable<(int Key, IReadOnlyList<double> Times, IReadOnlyList<double> Rates)> expected,
        IEnumerable<(int Key, IReadOnlyList<double> Times, IReadOnlyList<double> Rates)> actual)
    {
        var e = expected.ToList();
        var a = actual.ToList();
        Assert.Equal(e.Count, a.Count);
        for (int i = 0; i < e.Count; i++)
        {
            Assert.Equal(e[i].Key, a[i].Key);
            Assert.Equal(e[i].Times, a[i].Times);
            Assert.Equal(e[i].Rates, a[i].Rates);
        }
    }

    private static string TempFile(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "signalplanner-spmap-tests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    /// <summary>定位仓库根目录（含 SignalPlanner.sln 的目录），使测试与运行目录无关。</summary>
    private static string PathOf(params string[] parts)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SignalPlanner.sln")))
            {
                var all = new string[parts.Length + 1];
                all[0] = dir.FullName;
                Array.Copy(parts, 0, all, 1, parts.Length);
                return Path.Combine(all);
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("未能在测试输出目录之上找到 SignalPlanner.sln，无法定位仓库根目录。");
    }
}
