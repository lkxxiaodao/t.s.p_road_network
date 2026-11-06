using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SimCore.Model;

namespace SimCore.Io;

/// <summary>
/// <c>.spmap</c> 关卡文件的读写入口（技术方案03 §3.1：<c>Spmap.Load(path)</c> / <c>Spmap.Save(level, path)</c>）。
/// </summary>
/// <remarks>
/// <para><b>格式</b>：UTF-8 编码的 JSON，顶层结构见技术方案03 §3.2；字段名一律小写下划线、与 §3.2 逐字一致
/// （<c>format_version / meta / nodes / lanes / turns / signals / demand / pedestrians / bicycles / game_rules</c>）。</para>
/// <para><b>实现</b>：<see cref="System.Text.Json"/>（本任务追加约束）。枚举按名称（字符串）序列化，如 <c>"Signalized"</c>。</para>
/// <para><b>信号的落点</b>：§3.2 的 <c>signals[]</c> 以 node 为主键；内存模型（T0.2 冻结）把信号属性挂在
/// <see cref="Node.Offset"/> / <see cref="Node.SignalPlans"/> 上。读写时按下列<b>规范形</b>互相转换：
/// 只要某节点是信号路口、或 <c>Offset ≠ 0</c>、或已配相位，就为它写出一条 <c>signals[]</c> 记录；读取时回挂到同 id 的节点。
/// 非信号且无任何信号属性的节点不写 <c>signals[]</c> 记录。</para>
/// <para><b>转向比例</b>：<c>turns[].ratio</c> 为可选字段（默认 1.0）。仅当关卡显式给出比例时写入，值为 1.0 也照写，
/// 以保证"保存 → 加载"逐项等价。</para>
/// <para>本类只做格式转换，不做合法性校验——校验见 <see cref="SpmapValidator"/>；两者同源、共用同一份 <see cref="Level"/>。</para>
/// </remarks>
public static class Spmap
{
    /// <summary>当前支持的格式版本（§3.2 <c>format_version</c>）。</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>文件扩展名。</summary>
    public const string FileExtension = ".spmap";

    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            // 仅忽略 null：空的数组/对象照常写出，保证 §3.2 的十张表始终存在。
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>
    /// 读取一个 <c>.spmap</c> 文件为 <see cref="Level"/>。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <returns>关卡内存模型。</returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> 为空。</exception>
    /// <exception cref="JsonException">文件不是合法 JSON，或字段类型/枚举名不匹配。</exception>
    /// <exception cref="IOException">文件无法读取。</exception>
    public static Level Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("文件路径不能为空。", nameof(path));
        }

        string json = File.ReadAllText(path);
        LevelDto? dto = JsonSerializer.Deserialize<LevelDto>(json, JsonOptions)
            ?? throw new JsonException("关卡文件为空或不是有效 JSON 对象。");
        return ToLevel(dto);
    }

    /// <summary>
    /// 把 <see cref="Level"/> 写入一个 <c>.spmap</c> 文件（UTF-8，无 BOM，缩进 2 空格）。
    /// </summary>
    /// <param name="level">关卡内存模型。</param>
    /// <param name="path">文件路径。</param>
    /// <exception cref="ArgumentNullException"><paramref name="level"/> 为 null。</exception>
    /// <exception cref="ArgumentException"><paramref name="path"/> 为空。</exception>
    /// <exception cref="IOException">文件无法写入。</exception>
    public static void Save(Level level, string path)
    {
        ArgumentNullException.ThrowIfNull(level);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("文件路径不能为空。", nameof(path));
        }

        string json = JsonSerializer.Serialize(ToDto(level), JsonOptions);
        File.WriteAllText(path, json);
    }

    // ------------------------------------------------------------------
    // Level -> DTO
    // ------------------------------------------------------------------

    private static LevelDto ToDto(Level level)
    {
        Network network = level.Network;

        var nodes = new List<NodeDto>(network.Nodes.Count);
        var signals = new List<SignalDto>();
        foreach (Node node in network.Nodes)
        {
            nodes.Add(new NodeDto
            {
                Id = node.Id,
                Type = node.Type,
                X = node.X,
                Y = node.Y,
            });

            if (node.IsSignalized || node.Offset != 0d || node.SignalPlans.Count > 0)
            {
                signals.Add(ToSignalDto(node));
            }
        }

        var lanes = new List<LaneDto>(network.Lanes.Count);
        foreach (Lane lane in network.Lanes)
        {
            lanes.Add(new LaneDto
            {
                Id = lane.Id,
                Road = lane.Road,
                Type = lane.Type,
                FromNode = lane.FromNode,
                ToNode = lane.ToNode,
                Length = lane.Length,
                SpeedLimit = lane.SpeedLimit,
                LaneIndex = lane.LaneIndex,
                // 未标（-1）不写：保证旧关卡往返逐字节不变。
                LatIndex = lane.LatIndex >= 0 ? lane.LatIndex : (int?)null,
            });
        }

        var turns = new List<TurnDto>(network.Turns.Count);
        foreach (Turn turn in network.Turns)
        {
            turns.Add(new TurnDto
            {
                Id = turn.Id,
                SrcLane = turn.SrcLane,
                DstLane = turn.DstLane,
                Node = turn.Node,
                Type = turn.Type,
                Ratio = ExplicitRatio(level, turn.Id),
                Leg = turn.Leg >= 0 ? turn.Leg : (int?)null,
                Island = turn.IslandId >= 0 ? turn.IslandId : (int?)null,
            });
        }

        return new LevelDto
        {
            FormatVersion = level.FormatVersion,
            Meta = new MetaDto
            {
                Name = level.Meta.Name,
                Author = level.Meta.Author,
                Chapter = level.Meta.Chapter,
                Index = level.Meta.Index,
            },
            Nodes = nodes,
            Lanes = lanes,
            Turns = turns,
            Islands = ToIslandDtos(network.Islands),
            Signals = signals,
            Demand = ToDemandDtos(level.Demand),
            Pedestrians = ToPedestrianDtos(level.Pedestrians),
            Bicycles = ToBicycleDtos(level.Bicycles),
            GameRules = ToGameRulesDto(level.GameRules),
        };
    }

    /// <summary>关卡的显式转向比例：命中即返回其值（含 1.0），未命中返回 null（表示"未标注"）。</summary>
    private static double? ExplicitRatio(Level level, int turnId)
    {
        for (int i = 0; i < level.TurnRatios.Count; i++)
        {
            if (level.TurnRatios[i].Turn == turnId)
            {
                return level.TurnRatios[i].Ratio;
            }
        }

        return null;
    }

    private static SignalDto ToSignalDto(Node node)
    {
        var plans = new List<PlanDto>(node.SignalPlans.Count);
        foreach (SignalPlan plan in node.SignalPlans)
        {
            var stages = new List<StageDto>(plan.Stages.Count);
            foreach (Stage stage in plan.Stages)
            {
                stages.Add(new StageDto
                {
                    Type = stage.Type,
                    Duration = stage.Duration,
                    Protected = new List<int>(stage.ProtectedTurnIds),
                    Permitted = new List<int>(stage.PermittedTurnIds),
                });
            }

            plans.Add(new PlanDto { StartTime = plan.StartTime, Stages = stages });
        }

        return new SignalDto { Node = node.Id, Offset = node.Offset, Plans = plans };
    }

    private static List<DemandDto> ToDemandDtos(IReadOnlyList<DemandPlan> demand)
    {
        var list = new List<DemandDto>(demand.Count);
        foreach (DemandPlan plan in demand)
        {
            list.Add(new DemandDto
            {
                Lane = plan.Lane,
                TimePoints = new List<double>(plan.TimePoints),
                FlowRates = new List<double>(plan.FlowRates),
            });
        }

        return list;
    }

    private static List<PedestrianDto> ToPedestrianDtos(IReadOnlyList<PedestrianDemand> pedestrians)
    {
        var list = new List<PedestrianDto>(pedestrians.Count);
        foreach (PedestrianDemand ped in pedestrians)
        {
            list.Add(new PedestrianDto
            {
                CrosswalkTurn = ped.CrosswalkTurn,
                TimePoints = new List<double>(ped.TimePoints),
                FlowRates = new List<double>(ped.FlowRates),
            });
        }

        return list;
    }

    private static List<BicycleDto> ToBicycleDtos(IReadOnlyList<BicycleDemand> bicycles)
    {
        var list = new List<BicycleDto>(bicycles.Count);
        foreach (BicycleDemand bike in bicycles)
        {
            list.Add(new BicycleDto
            {
                Lane = bike.Lane,
                Mode = bike.Mode,
                TimePoints = new List<double>(bike.TimePoints),
                FlowRates = new List<double>(bike.FlowRates),
            });
        }

        return list;
    }

    private static GameRulesDto ToGameRulesDto(GameRules rules) => new()
    {
        Anchors = new AnchorsDto
        {
            R = Pack(rules.Anchors.R),
            D = Pack(rules.Anchors.D),
            P = Pack(rules.Anchors.P),
            Q = Pack(rules.Anchors.Q),
        },
        Weights = new WeightsDto
        {
            R = rules.Weights.R,
            D = rules.Weights.D,
            P = rules.Weights.P,
            Q = rules.Weights.Q,
        },
        Events = rules.Events?.DeepClone(),
        Tutorial = rules.Tutorial?.DeepClone(),
        VehicleMix = rules.VehicleMix,
    };

    /// <summary>把锚点打包成 §3.2 的二元数组 <c>[b, g]</c>。</summary>
    private static double[] Pack(MetricAnchorValue anchor) => new[] { anchor.Bottom, anchor.Good };

    // ------------------------------------------------------------------
    // DTO -> Level
    // ------------------------------------------------------------------

    private static Level ToLevel(LevelDto dto)
    {
        // 1) 先收拢 signals[]：node id -> (offset, plans)。重复记录后者覆盖（校验器会另报重复）。
        var signalByNode = new Dictionary<int, SignalDto>();
        if (dto.Signals is not null)
        {
            foreach (SignalDto signal in dto.Signals)
            {
                signalByNode[signal.Node] = signal;
            }
        }

        // 2) 节点（把信号属性回挂到同 id 的节点上）。
        var nodes = new List<Node>(dto.Nodes?.Count ?? 0);
        if (dto.Nodes is not null)
        {
            foreach (NodeDto node in dto.Nodes)
            {
                nodes.Add(ToNode(node, signalByNode.TryGetValue(node.Id, out SignalDto? signal) ? signal : null));
            }
        }

        // 3) 车道。
        var lanes = new List<Lane>(dto.Lanes?.Count ?? 0);
        if (dto.Lanes is not null)
        {
            foreach (LaneDto lane in dto.Lanes)
            {
                lanes.Add(new Lane
                {
                    Id = lane.Id,
                    Road = lane.Road,
                    Type = lane.Type,
                    FromNode = lane.FromNode,
                    ToNode = lane.ToNode,
                    Length = lane.Length,
                    SpeedLimit = lane.SpeedLimit,
                    LaneIndex = lane.LaneIndex,
                    LatIndex = lane.LatIndex ?? -1,
                });
            }
        }

        // 4) 转向 + 显式比例。
        var turns = new List<Turn>(dto.Turns?.Count ?? 0);
        var ratios = new List<TurnRatio>();
        if (dto.Turns is not null)
        {
            foreach (TurnDto turn in dto.Turns)
            {
                turns.Add(new Turn
                {
                    Id = turn.Id,
                    SrcLane = turn.SrcLane,
                    DstLane = turn.DstLane,
                    Node = turn.Node,
                    Type = turn.Type,
                    Leg = turn.Leg ?? -1,
                    IslandId = turn.Island ?? -1,
                });

                if (turn.Ratio is double ratio)
                {
                    ratios.Add(new TurnRatio(turn.Id, ratio));
                }
            }
        }

        return new Level
        {
            FormatVersion = dto.FormatVersion,
            Meta = ToMeta(dto.Meta),
            Network = new Network
            {
                Nodes = nodes,
                Lanes = lanes,
                Turns = turns,
                Islands = FromIslandDtos(dto.Islands),
            },
            TurnRatios = ratios,
            Demand = FromDemandDtos(dto.Demand),
            Pedestrians = FromPedestrianDtos(dto.Pedestrians),
            Bicycles = FromBicycleDtos(dto.Bicycles),
            GameRules = FromGameRulesDto(dto.GameRules),
        };
    }

    /// <summary>安全岛 → DTO；空集合返回 null（**整段键不写**，旧关卡往返逐字节不变）。</summary>
    private static List<IslandDto>? ToIslandDtos(IReadOnlyList<Island> islands)
    {
        if (islands.Count == 0)
        {
            return null;
        }

        var list = new List<IslandDto>(islands.Count);
        for (int i = 0; i < islands.Count; i++)
        {
            Island island = islands[i];
            list.Add(new IslandDto
            {
                Id = island.Id,
                X = island.X,
                Y = island.Y,
                Capacity = island.Capacity,
            });
        }

        return list;
    }

    private static IReadOnlyList<Island> FromIslandDtos(List<IslandDto>? dtos)
    {
        if (dtos is null || dtos.Count == 0)
        {
            return Array.Empty<Island>();
        }

        var list = new List<Island>(dtos.Count);
        foreach (IslandDto dto in dtos)
        {
            list.Add(new Island
            {
                Id = dto.Id,
                X = dto.X,
                Y = dto.Y,
                Capacity = dto.Capacity,
            });
        }

        return list;
    }

    private static Node ToNode(NodeDto dto, SignalDto? signal)
    {
        if (signal is null)
        {
            return new Node { Id = dto.Id, Type = dto.Type, X = dto.X, Y = dto.Y };
        }

        var plans = new List<SignalPlan>(signal.Plans?.Count ?? 0);
        if (signal.Plans is not null)
        {
            foreach (PlanDto plan in signal.Plans)
            {
                var stages = new List<Stage>(plan.Stages?.Count ?? 0);
                if (plan.Stages is not null)
                {
                    foreach (StageDto stage in plan.Stages)
                    {
                        stages.Add(new Stage
                        {
                            Type = stage.Type,
                            Duration = stage.Duration,
                            ProtectedTurnIds = stage.Protected ?? new List<int>(),
                            PermittedTurnIds = stage.Permitted ?? new List<int>(),
                        });
                    }
                }

                plans.Add(new SignalPlan { StartTime = plan.StartTime, Stages = stages });
            }
        }

        return new Node
        {
            Id = dto.Id,
            Type = dto.Type,
            X = dto.X,
            Y = dto.Y,
            Offset = signal.Offset,
            SignalPlans = plans,
        };
    }

    private static LevelMeta ToMeta(MetaDto? dto) => dto is null
        ? new LevelMeta()
        : new LevelMeta { Name = dto.Name ?? string.Empty, Author = dto.Author ?? string.Empty, Chapter = dto.Chapter, Index = dto.Index };

    private static List<DemandPlan> FromDemandDtos(List<DemandDto>? dtos)
    {
        var list = new List<DemandPlan>(dtos?.Count ?? 0);
        if (dtos is not null)
        {
            foreach (DemandDto dto in dtos)
            {
                list.Add(new DemandPlan
                {
                    Lane = dto.Lane,
                    TimePoints = dto.TimePoints ?? new List<double>(),
                    FlowRates = dto.FlowRates ?? new List<double>(),
                });
            }
        }

        return list;
    }

    private static List<PedestrianDemand> FromPedestrianDtos(List<PedestrianDto>? dtos)
    {
        var list = new List<PedestrianDemand>(dtos?.Count ?? 0);
        if (dtos is not null)
        {
            foreach (PedestrianDto dto in dtos)
            {
                list.Add(new PedestrianDemand
                {
                    CrosswalkTurn = dto.CrosswalkTurn,
                    TimePoints = dto.TimePoints ?? new List<double>(),
                    FlowRates = dto.FlowRates ?? new List<double>(),
                });
            }
        }

        return list;
    }

    private static List<BicycleDemand> FromBicycleDtos(List<BicycleDto>? dtos)
    {
        var list = new List<BicycleDemand>(dtos?.Count ?? 0);
        if (dtos is not null)
        {
            foreach (BicycleDto dto in dtos)
            {
                list.Add(new BicycleDemand
                {
                    Lane = dto.Lane,
                    Mode = dto.Mode,
                    TimePoints = dto.TimePoints ?? new List<double>(),
                    FlowRates = dto.FlowRates ?? new List<double>(),
                });
            }
        }

        return list;
    }

    private static GameRules FromGameRulesDto(GameRulesDto? dto)
    {
        if (dto is null)
        {
            return new GameRules();
        }

        return new GameRules
        {
            Anchors = new MetricAnchors
            {
                R = Unpack(dto.Anchors?.R),
                D = Unpack(dto.Anchors?.D),
                P = Unpack(dto.Anchors?.P),
                Q = Unpack(dto.Anchors?.Q),
            },
            Weights = dto.Weights is null
                ? MetricWeights.Default
                : new MetricWeights { R = dto.Weights.R, D = dto.Weights.D, P = dto.Weights.P, Q = dto.Weights.Q },
            Events = dto.Events ?? new JsonArray(),
            Tutorial = dto.Tutorial ?? new JsonArray(),
            VehicleMix = dto.VehicleMix,
        };
    }

    /// <summary>把 §3.2 的二元数组 <c>[b, g]</c> 还原为锚点；缺省或长度不足时按未标定处理。</summary>
    private static MetricAnchorValue Unpack(double[]? pair)
    {
        if (pair is null || pair.Length < 2)
        {
            return MetricAnchorValue.Unset;
        }

        return new MetricAnchorValue(pair[0], pair[1]);
    }

    // ------------------------------------------------------------------
    // 文件 DTO：字段名 = §3.2 逐字（小写下划线），顺序 = §3.2 出现顺序
    // ------------------------------------------------------------------

    private sealed class LevelDto
    {
        [JsonPropertyName("format_version")] public int FormatVersion { get; set; }

        [JsonPropertyName("meta")] public MetaDto? Meta { get; set; }

        [JsonPropertyName("nodes")] public List<NodeDto>? Nodes { get; set; }

        [JsonPropertyName("lanes")] public List<LaneDto>? Lanes { get; set; }

        [JsonPropertyName("turns")] public List<TurnDto>? Turns { get; set; }

        [JsonPropertyName("islands")] public List<IslandDto>? Islands { get; set; }

        [JsonPropertyName("signals")] public List<SignalDto>? Signals { get; set; }

        [JsonPropertyName("demand")] public List<DemandDto>? Demand { get; set; }

        [JsonPropertyName("pedestrians")] public List<PedestrianDto>? Pedestrians { get; set; }

        [JsonPropertyName("bicycles")] public List<BicycleDto>? Bicycles { get; set; }

        [JsonPropertyName("game_rules")] public GameRulesDto? GameRules { get; set; }
    }

    private sealed class MetaDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }

        [JsonPropertyName("author")] public string? Author { get; set; }

        [JsonPropertyName("chapter")] public int Chapter { get; set; }

        [JsonPropertyName("index")] public int Index { get; set; }
    }

    private sealed class NodeDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }

        [JsonPropertyName("type")] public NodeType Type { get; set; }

        [JsonPropertyName("x")] public double X { get; set; }

        [JsonPropertyName("y")] public double Y { get; set; }
    }

    private sealed class LaneDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }

        [JsonPropertyName("road")] public int Road { get; set; }

        [JsonPropertyName("type")] public LaneType Type { get; set; }

        [JsonPropertyName("from_node")] public int FromNode { get; set; }

        [JsonPropertyName("to_node")] public int ToNode { get; set; }

        [JsonPropertyName("length")] public double Length { get; set; }

        [JsonPropertyName("speed_limit")] public double SpeedLimit { get; set; }

        [JsonPropertyName("lane_index")] public int LaneIndex { get; set; }

        [JsonPropertyName("lat_index")] public int? LatIndex { get; set; }
    }

    private sealed class TurnDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }

        [JsonPropertyName("src_lane")] public int SrcLane { get; set; }

        [JsonPropertyName("dst_lane")] public int DstLane { get; set; }

        [JsonPropertyName("node")] public int Node { get; set; }

        [JsonPropertyName("type")] public TurnType Type { get; set; }

        [JsonPropertyName("ratio")] public double? Ratio { get; set; }

        // 分段过街（islands[] + 两段 Crosswalk，2026-10-24 新增）：缺省都不写，旧关卡往返不变。
        [JsonPropertyName("leg")] public int? Leg { get; set; }

        [JsonPropertyName("island")] public int? Island { get; set; }
    }

    private sealed class IslandDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }

        [JsonPropertyName("x")] public double X { get; set; }

        [JsonPropertyName("y")] public double Y { get; set; }

        [JsonPropertyName("capacity")] public int Capacity { get; set; }
    }

    private sealed class SignalDto
    {
        [JsonPropertyName("node")] public int Node { get; set; }

        [JsonPropertyName("offset")] public double Offset { get; set; }

        [JsonPropertyName("plans")] public List<PlanDto>? Plans { get; set; }
    }

    private sealed class PlanDto
    {
        [JsonPropertyName("start_time")] public double StartTime { get; set; }

        [JsonPropertyName("stages")] public List<StageDto>? Stages { get; set; }
    }

    private sealed class StageDto
    {
        [JsonPropertyName("protected")] public List<int>? Protected { get; set; }

        [JsonPropertyName("permitted")] public List<int>? Permitted { get; set; }

        [JsonPropertyName("type")] public StageType Type { get; set; }

        [JsonPropertyName("duration")] public double Duration { get; set; }
    }

    private sealed class DemandDto
    {
        [JsonPropertyName("lane")] public int Lane { get; set; }

        [JsonPropertyName("time_points")] public List<double>? TimePoints { get; set; }

        [JsonPropertyName("flow_rates")] public List<double>? FlowRates { get; set; }
    }

    private sealed class PedestrianDto
    {
        [JsonPropertyName("crosswalk_turn")] public int CrosswalkTurn { get; set; }

        [JsonPropertyName("time_points")] public List<double>? TimePoints { get; set; }

        [JsonPropertyName("flow_rates")] public List<double>? FlowRates { get; set; }
    }

    private sealed class BicycleDto
    {
        [JsonPropertyName("lane")] public int Lane { get; set; }

        [JsonPropertyName("mode")] public BicycleMode Mode { get; set; }

        [JsonPropertyName("time_points")] public List<double>? TimePoints { get; set; }

        [JsonPropertyName("flow_rates")] public List<double>? FlowRates { get; set; }
    }

    private sealed class GameRulesDto
    {
        [JsonPropertyName("anchors")] public AnchorsDto? Anchors { get; set; }

        [JsonPropertyName("weights")] public WeightsDto? Weights { get; set; }

        [JsonPropertyName("events")] public JsonNode? Events { get; set; }

        [JsonPropertyName("tutorial")] public JsonNode? Tutorial { get; set; }

        [JsonPropertyName("vehicle_mix")] public VehicleMix? VehicleMix { get; set; }
    }

    private sealed class AnchorsDto
    {
        [JsonPropertyName("R")] public double[]? R { get; set; }

        [JsonPropertyName("D")] public double[]? D { get; set; }

        [JsonPropertyName("P")] public double[]? P { get; set; }

        [JsonPropertyName("Q")] public double[]? Q { get; set; }
    }

    private sealed class WeightsDto
    {
        [JsonPropertyName("R")] public double R { get; set; }

        [JsonPropertyName("D")] public double D { get; set; }

        [JsonPropertyName("P")] public double P { get; set; }

        [JsonPropertyName("Q")] public double Q { get; set; }
    }
}
