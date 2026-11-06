using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using SimCore.Io;
using SimCore.Model;

namespace SignalPlanner.MapExport.Sumo;

/// <summary>
/// 把需求写成 SUMO 的 <c>.rou.xml</c>。
///
/// <para><b>三件套的分工</b>（实测得出，写错 SUMO 会直接退出而不是警告）：</para>
/// <list type="bullet">
///   <item><c>vType</c> / <c>route</c> / <c>flow</c> <b>只能</b>写这里 —— 写进 additional 会报
///         <c>route not known</c>；</item>
///   <item>路线由 <see cref="SumoRouteEnumerator"/> 按转率枚举，用户不写路线；</item>
///   <item>行人走 <c>personFlow</c> + <c>walk</c>（跨路口连通性已于 2026-11-05 冒烟通过）。</item>
/// </list>
///
/// <para><b>数据从哪来</b>：专业档有 <c>flows[]</c> / <c>person_flows[]</c> / <c>bike_flows[]</c> 就用它；
/// 没有就按 <c>.spmap</c> 的 <c>demand</c> / <c>bicycles</c> / <c>pedestrians</c> 曲线自动派生
/// （"能推导的绝不让人填"，技术方案06 §7.1）。</para>
/// </summary>
public static class SumoRouWriter
{
    /// <summary>写文件的结果（也用于编辑器回显与自检）。</summary>
    public sealed class Result
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = string.Empty;
        public List<string> Warnings { get; } = new();
        public int RouteCount { get; set; }
        public int FlowCount { get; set; }
        public int PersonFlowCount { get; set; }

        /// <summary>生成公交线路班次的数量（S3b）。</summary>
        public int BusLineCount { get; set; }

        /// <summary>理论生成车辆数（Σ 段时长 × 流率），自检拿它和 SUMO 的 Inserted 比。</summary>
        public double ExpectedVehicles { get; set; }

        public double ExpectedPersons { get; set; }
    }

    /// <summary>
    /// 生成 <c>.rou.xml</c>。<b>没有任何需求时返回 null</b>（不产出空文件）。
    /// </summary>
    /// <param name="netPath">刚生成的 net.xml（用它拿 lane → edge 映射）。</param>
    public static Result? Write(
        string netPath,
        ExportGraph graph,
        Level level,
        string? profileJson,
        string outPath)
    {
        var result = new Result();
        Dictionary<int, string> laneToEdge;
        try
        {
            laneToEdge = BuildLaneEdges(netPath, graph);
        }
        catch (Exception ex)
        {
            result.Error = "读取 net.xml 建立车道→边映射失败：" + ex.Message;
            return result;
        }

        if (laneToEdge.Count == 0)
        {
            result.Error = "net.xml 里没有任何车道能映射回 .spmap（origID 缺失？），无法生成路线。";
            return result;
        }

        ProfileFlows? configured = ParseProfile(profileJson);
        List<Request> requests = configured != null && configured.HasAny
            ? FromProfile(configured, graph, result)
            : FromSpmap(level, graph, profileJson, result);

        // 公交站：只有"整条车流都是公交"时才插 <stop>（见下方 BusStopsFor 的注释）
        SumoFacilities.Parsed facilities = SumoFacilities.Parse(netPath, graph, profileJson);

        // ★ 公交线路（S3b）：有它就以它为准 —— 每条线生成自己的 route + flow + 停靠表。
        //   这时**不再**给普通需求车流自动插站（否则同一条路线上会冒出两拨公交）。
        List<BusLineDto> busLines = ParseBusLines(profileJson);

        if (requests.Count == 0)
        {
            return null;
        }

        if (result.Error.Length > 0 && !result.Ok)
        {
            return result;
        }

        // ★ 校核（2026-11-05）：车流是**从需求曲线派生**的，不是从车型派生。
        //   "配了 4 个进口的车型，却只有 1 个进口出车" 的根本原因就是这个 ——
        //   另外 3 个进口没有 demand 曲线，自然没有 flow。这里把它说清楚。
        var demandLanes = new HashSet<int>();
        foreach (DemandPlan d in level.Demand)
        {
            demandLanes.Add(d.Lane);
        }

        var idle = new List<int>();
        foreach (int lane in SumoVehicleTypes.LanesWithOwnMix(profileJson).OrderBy(x => x))
        {
            if (!demandLanes.Contains(lane))
            {
                idle.Add(lane);
            }
        }

        if (idle.Count > 0)
        {
            result.Warnings.Add($"进口 {string.Join("、", idle.Select(l => "#" + l))} 配了车型构成，"
                + "但**没有需求曲线** → 不会出车。请选中这些车道，在「需求曲线」里加一段（填流量）。");
        }

        // 需求挂在非机动车道上：车会从同一条边的机动车道出发，容易让人以为"车是凭空来的"
        foreach (DemandPlan d in level.Demand)
        {
            if (graph.Lanes.TryGetValue(d.Lane, out ExportLane? lane) &&
                lane.Type is not (LaneType.Driving or LaneType.Bus or LaneType.SharedLeftTurn))
            {
                result.Warnings.Add($"车道 #{d.Lane}（{lane.Type}）不是机动车道，但挂着机动车需求 —— "
                    + "车会从同一条边的机动车道出发。建议把需求改挂到进口的机动车道上。");
            }
        }

        // 行人要用转向表找横道；统一在这里补，免得每个分支都传一遍
        foreach (Request req in requests)
        {
            req.Graph = graph;
        }

        // --- 车型库 ---
        List<SumoVehicleType> types = SumoVehicleTypes.Resolve(profileJson, level.GameRules.VehicleMix?.ToString());
        var typeById = new Dictionary<string, SumoVehicleType>(StringComparer.Ordinal);
        foreach (SumoVehicleType t in types)
        {
            typeById[t.Id] = t;
        }

        // 车流里引用了不存在的车型 → 补一条（否则 SUMO 报 unknown type）
        foreach (Request req in requests)
        {
            foreach ((string typeId, _) in req.Mix)
            {
                if (typeById.ContainsKey(typeId))
                {
                    continue;
                }

                SumoVehicleType created = SumoVehicleTypes.Create(typeId, "normal", typeId);
                typeById[typeId] = created;
                types.Add(created);
                result.Warnings.Add($"车型「{typeId}」没有定义，已按预设自动建一条。");
            }
        }

        // --- 路线 ---
        var root = new XElement("routes");
        foreach (SumoVehicleType t in types)
        {
            root.Add(BuildVType(t));
        }

        var emittedRoutes = new HashSet<string>(StringComparer.Ordinal);
        var flowRoot = new List<XElement>();

        foreach (Request req in requests)
        {
            if (req.Kind == RequestKind.Person)
            {
                XElement? person = BuildPersonFlow(req, laneToEdge, result);
                if (person != null)
                {
                    flowRoot.Add(person);
                    result.PersonFlowCount++;
                }

                continue;
            }

            if (!laneToEdge.TryGetValue(req.Lane, out string? startEdge))
            {
                result.Error = $"车流 {req.Id}：车道 {req.Lane} 在 net.xml 里找不到对应的边。";
                return result;
            }

            List<SumoRouteCandidate> routes = SumoRouteEnumerator.Enumerate(
                graph, req.Lane, laneToEdge, req.RouteMode == "shortest", out int trimmed);
            if (trimmed > 0)
            {
                result.Warnings.Add($"车流 {req.Id}：有 {trimmed} 条路线超过 {SumoRouteEnumerator.MaxDepth} 跳，已截断（可能有环路）。");
            }

            if (routes.Count == 0)
            {
                result.Error = $"车流 {req.Id}：车道 {req.Lane} 没有任何可走的出口转向，生成不出路线。"
                    + "请检查该车道在路口处是否连了转向（转向表为空或全部被删）。";
                return result;
            }

            // ★ "断头"的判据（2026-11-05）：需求挂在**出口车道**上——它已经被别的转向指向（有 incoming），
            //   却没有任何驶出它的转向。这时车一上台就消失，SUMO 不报错但结果毫无意义。
            //   纯单路段练习图（没有任何转向指向它）不算错，只给警告。
            bool hasIncoming = false;
            foreach (ExportTurn t in graph.Turns)
            {
                if (t.ShouldExport && !t.IsCrosswalk && t.Dst.Id == req.Lane)
                {
                    hasIncoming = true;
                    break;
                }
            }

            if (hasIncoming && routes.TrueForAll(r => r.Edges.Count <= 1))
            {
                result.Error = $"车流 {req.Id}：需求挂在车道 {req.Lane} 上，但这是一条**出口车道**"
                    + "（已有别的转向驶入它），没有驶出它的转向 —— 车一进路网就会消失。"
                    + "请把这条需求改挂到对应的进口车道上。";
                return result;
            }

            // 归一化概率 + 去重（同一条 edge 序列只留一条 route）
            var byEdges = new Dictionary<string, (SumoRouteCandidate Route, double Weight)>(StringComparer.Ordinal);
            foreach (SumoRouteCandidate r in routes)
            {
                string key = string.Join(" ", r.Edges);
                if (byEdges.TryGetValue(key, out (SumoRouteCandidate Route, double Weight) found))
                {
                    byEdges[key] = (found.Route, found.Weight + r.Weight);
                }
                else
                {
                    byEdges[key] = (r, r.Weight);
                }
            }

            double total = byEdges.Values.Sum(v => v.Weight);
            if (total <= 0.0)
            {
                result.Error = $"车流 {req.Id}：所有出口转向的权重都是 0，生成不出路线。";
                return result;
            }

            string distId = "rd_" + req.Id;
            var dist = new XElement("routeDistribution", new XAttribute("id", distId));
            int routeIndex = 0;
            foreach ((SumoRouteCandidate route, double weight) in byEdges.Values)
            {
                string routeId = "r_" + req.Id + "_" + routeIndex.ToString(CultureInfo.InvariantCulture);
                routeIndex++;

                string edgeKey = string.Join(" ", route.Edges);
                if (emittedRoutes.Add(edgeKey))
                {
                    root.Add(new XElement("route",
                        new XAttribute("id", routeId),
                        new XAttribute("edges", edgeKey)));
                }
                else
                {
                    // 复用已有的 route（同一串边不重复定义）
                    routeId = "r_" + req.Id + "_" + routeIndex.ToString(CultureInfo.InvariantCulture) + "_dup";
                    root.Add(new XElement("route",
                        new XAttribute("id", routeId),
                        new XAttribute("edges", edgeKey)));
                }

                dist.Add(new XElement("route",
                    new XAttribute("refId", routeId),
                    new XAttribute("probability", F(weight / total))));

                if (route.Edges.Count <= 1)
                {
                    result.Warnings.Add($"车流 {req.Id}：路线只有 1 条边（{edgeKey}），车会进得去出不来，仿真里会直接消失。");
                }
            }

            root.Add(dist);
            result.RouteCount += routeIndex;

            string typeRef = req.Mix.Count == 1 ? req.Mix[0].TypeId : "mix_" + req.Id;
            if (req.Mix.Count > 1)
            {
                double sum = req.Mix.Sum(m => m.Share);
                var dist2 = new XElement("vTypeDistribution", new XAttribute("id", typeRef));
                foreach ((string typeId, double share) in req.Mix)
                {
                    dist2.Add(new XElement("vType",
                        new XAttribute("refId", typeId),
                        new XAttribute("probability", F(sum > 0 ? share / sum : 1.0 / req.Mix.Count))));
                }

                root.Add(dist2);
            }

            // ★ 公交停靠（S3，2026-11-05 实测）：<busStop> 本身**不会**让车停 ——
            //   必须在车辆的 route 里写 <stop busStop="..."/ >。所以停靠要由这里补。
            //
            //   只在「该车流的候选车型**全是** vClass=bus」时才插：flow 共享同一套 route，
            //   一旦插入 stop，跑在这条 flow 上的**所有**车都会停 —— 混了私家车就变成"小汽车也停公交站"。
            //   宁可不停 + 警告，也不要行为错。
            List<(SumoFacilities.Facility Stop, int Order)> busStops = busLines.Count > 0
                ? new List<(SumoFacilities.Facility, int)>()
                : BusStopsFor(req, byEdges.Values.Select(v => v.Route.Edges).ToList(),
                    typeById, facilities, result);
            string lineName = string.Empty;
            if (busStops.Count > 0)
            {
                // 站台可能标记不同线路名；一条车流只能挂一个 line，取排序第一个，
                // 其余线路的站跳过（警告一次，免得用户以为全停了）
                lineName = busStops.Select(s => s.Stop.Lines).Distinct(StringComparer.Ordinal)
                    .OrderBy(x => x, StringComparer.Ordinal).First();
                foreach (string other in busStops.Select(s => s.Stop.Lines).Distinct(StringComparer.Ordinal)
                             .Where(x => !string.Equals(x, lineName, StringComparison.Ordinal)))
                {
                    result.Warnings.Add($"车流 {req.Id}：公交站有多个线路名（{lineName} / {other}），"
                        + $"这条车流只停靠「{lineName}」，其余站请另建公交车型的车流。");
                }

                busStops = busStops
                    .Where(s => string.Equals(s.Stop.Lines, lineName, StringComparison.Ordinal))
                    .ToList();
            }

            foreach ((double begin, double end, double vph) in req.Segments)
            {
                if (vph <= 0.0 || end - begin < 1.0)
                {
                    continue;
                }

                var flow = new XElement("flow",
                    new XAttribute("id", req.Id + "_" + F(begin).Replace(".", "_", StringComparison.Ordinal)),
                    new XAttribute("type", typeRef),
                    new XAttribute("begin", F(begin)),
                    new XAttribute("end", F(end)),
                    new XAttribute("vehsPerHour", F(vph)),
                    new XAttribute("route", distId),
                    new XAttribute("departLane", string.IsNullOrWhiteSpace(req.DepartLane) ? "best" : req.DepartLane),
                    new XAttribute("departSpeed", string.IsNullOrWhiteSpace(req.DepartSpeed) ? "max" : req.DepartSpeed));

                // 公交站：line 必须与 busStop 的 lines 对上，否则车经过也不停
                if (busStops.Count > 0)
                {
                    flow.SetAttributeValue("line", lineName);
                    foreach ((SumoFacilities.Facility stop, int _) in busStops
                                 .OrderBy(s => s.Order))
                    {
                        flow.Add(new XElement("stop",
                            new XAttribute("busStop", stop.Id),
                            new XAttribute("duration", F(stop.Duration))));
                    }
                }

                flowRoot.Add(flow);
                result.FlowCount++;
                result.ExpectedVehicles += (end - begin) / 3600.0 * vph;
            }
        }

        // ★ 公交线路的班次也塞进 flowRoot（下面统一按 begin 排序）
        if (busLines.Count > 0)
        {
            AppendBusLines(root, flowRoot, busLines, laneToEdge, graph, facilities,
                types, typeById, result);
        }

        // ★★ 必须按 begin 升序输出 flow（2026-11-05 实测踩坑）：
        //    SUMO 解析 route 文件时是顺序消费的，一旦遇到 begin 较大的 flow，
        //    后面 begin 更小的 flow 会被**静默丢弃**（不报错、不告警，车辆数莫名其妙变少）。
        //    示例：f0(0-600) → f0(600-3600) → f1(0-600) 时，f1 完全不生效。
        foreach (XElement element in flowRoot.OrderBy(e => (double?)e.Attribute("begin") ?? 0.0))
        {
            root.Add(element);
        }

        try
        {
            var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
            doc.Save(outPath);
        }
        catch (Exception ex)
        {
            result.Error = ".rou.xml 写入失败：" + ex.Message;
            return result;
        }

        result.Ok = true;
        return result;
    }

    // ============================================================
    // 公交线路：一趟车跑一条固定路线、按间隔发车、按顺序停站
    // ============================================================

    /// <summary>
    /// 为每条公交线路生成 <c>&lt;route&gt;</c> + <c>&lt;flow period=...&gt;</c> + <c>&lt;stop&gt;</c>。
    ///
    /// <para><b>为什么用 flow 而不是一辆辆 vehicle</b>：用户要的是"发车间隔"，
    /// <c>flow</c> 的 <c>period</c> 就是它，且车辆 id 由 SUMO 自动编号，不会撞。
    /// 代价是 <c>&lt;stop until="绝对时刻"&gt;</c> 用不了（那要求每辆车不同的时间），
    /// 所以停靠一律用 <c>duration</c>。</para>
    ///
    /// <para><b>路线从哪来</b>：不让用户画 —— 按起点进口的转率枚举，取**权重最大**的那条
    /// （"用户永不写路线"，技术方案06 §7.1）。站台不在该路线上的会被过滤掉并告警。</para>
    /// </summary>
    private static void AppendBusLines(
        XElement root,
        List<XElement> flowRoot,
        List<BusLineDto> lines,
        Dictionary<int, string> laneToEdge,
        ExportGraph graph,
        SumoFacilities.Parsed facilities,
        List<SumoVehicleType> types,
        Dictionary<string, SumoVehicleType> typeById,
        Result result)
    {
        // 公交车型：优先用车型库里 vClass=bus 的；没有就自动补一条
        string busType = string.Empty;
        foreach (SumoVehicleType t in types)
        {
            if (string.Equals(t.VClass, "bus", StringComparison.Ordinal))
            {
                busType = t.Id;
                break;
            }
        }

        if (busType.Length == 0)
        {
            SumoVehicleType created = SumoVehicleTypes.Create("bus", "normal", "bus");
            types.Add(created);
            typeById[created.Id] = created;
            root.Add(BuildVType(created));
            busType = created.Id;
            result.Warnings.Add("公交线路需要公交车型，已按预设自动建一条 vType「bus」。");
        }

        List<SumoFacilities.Facility> allStops = facilities.Of(SumoFacilities.KindBusStop).ToList();
        var usedRouteIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (BusLineDto line in lines)
        {
            string lineId = string.IsNullOrWhiteSpace(line.Id) ? "bus" : line.Id!.Trim();
            string lineName = string.IsNullOrWhiteSpace(line.Line) ? "1" : line.Line!.Trim();

            if (!laneToEdge.TryGetValue(line.Lane, out string? startEdge))
            {
                result.Warnings.Add($"公交线路 {lineId}：起点车道 #{line.Lane} 在 net.xml 里找不到对应边，已跳过。");
                continue;
            }

            List<SumoRouteCandidate> candidates = SumoRouteEnumerator.Enumerate(
                graph, line.Lane, laneToEdge, false, out int trimmed);
            if (trimmed > 0)
            {
                result.Warnings.Add($"公交线路 {lineId}：有 {trimmed} 条路线超过深度上限，已截断。");
            }

            if (candidates.Count == 0)
            {
                result.Warnings.Add($"公交线路 {lineId}：起点车道 #{line.Lane} 没有可走的出口转向，"
                    + "生成不出路线，已跳过。");
                continue;
            }

            // 取权重最大的那条（同权重时取边数最多的那条，更像"主线"）
            SumoRouteCandidate main = candidates
                .OrderByDescending(c => c.Weight)
                .ThenByDescending(c => c.Edges.Count)
                .First();

            IReadOnlyList<string> edges = main.Edges;
            if (edges.Count <= 1)
            {
                result.Warnings.Add($"公交线路 {lineId}：路线只有 1 条边（{string.Join(" ", edges)}），"
                    + "车进去就消失，已跳过。");
                continue;
            }

            // 站台：只留在这条路线上的，按路线顺序排（SUMO 按 <stop> 出现顺序停）
            var ordered = new List<SumoFacilities.Facility>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string edge in edges)
            {
                foreach (SumoFacilities.Facility stop in allStops)
                {
                    if (string.Equals(stop.NetEdge, edge, StringComparison.Ordinal) && seen.Add(stop.Id))
                    {
                        ordered.Add(stop);
                    }
                }
            }

            var configStops = new HashSet<string>(
                (line.Stops ?? new List<string>()).Select(x => x ?? string.Empty), StringComparer.Ordinal);
            if (configStops.Count > 0)
            {
                var wanted = ordered.Where(s => configStops.Contains(s.Id)).ToList();
                var missing = configStops
                    .Where(id => ordered.All(s => !string.Equals(s.Id, id, StringComparison.Ordinal)))
                    .ToList();
                if (missing.Count > 0)
                {
                    result.Warnings.Add($"公交线路 {lineId}：站 {string.Join("、", missing)} "
                        + "不在这条路线上（或已被删除），已跳过。");
                }

                ordered = wanted;
            }
            else
            {
                // 没勾任何站 → 空驶公交。仍然生成（用户可能只想看车流），但提醒一句
                result.Warnings.Add($"公交线路 {lineId}：没有勾选任何停靠站，这条线会空驶。");
            }

            string routeId = "r_" + lineId;
            int dup = 2;
            while (!usedRouteIds.Add(routeId))
            {
                routeId = "r_" + lineId + "_" + dup.ToString(CultureInfo.InvariantCulture);
                dup++;
            }

            root.Add(new XElement("route",
                new XAttribute("id", routeId),
                new XAttribute("edges", string.Join(" ", edges))));

            double begin = line.Begin ?? 0.0;
            double end = line.End ?? 3600.0;
            double headway = line.Headway ?? 300.0;
            if (headway < 1.0)
            {
                headway = 60.0;
                result.Warnings.Add($"公交线路 {lineId}：发车间隔 {line.Headway:0.#} s 不合法，按 60 s 处理。");
            }

            if (end <= begin)
            {
                result.Warnings.Add($"公交线路 {lineId}：运营时段 {begin:0}-{end:0} 秒为空，已跳过。");
                continue;
            }

            var flow = new XElement("flow",
                new XAttribute("id", lineId),
                new XAttribute("type", busType),
                new XAttribute("line", lineName),
                new XAttribute("begin", F(begin)),
                new XAttribute("end", F(end)),
                new XAttribute("period", F(headway)),
                new XAttribute("route", routeId),
                new XAttribute("departLane", "best"));

            foreach (SumoFacilities.Facility stop in ordered)
            {
                flow.Add(new XElement("stop",
                    new XAttribute("busStop", stop.Id),
                    new XAttribute("duration", F(stop.Duration))));
            }

            flowRoot.Add(flow);
            result.FlowCount++;
            result.BusLineCount++;
            result.ExpectedVehicles += Math.Max(0.0, Math.Floor((end - begin) / headway));
        }
    }

    private static List<BusLineDto> ParseBusLines(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<BusLineDto>();
        }

        try
        {
            BusProfileDto? profile = JsonSerializer.Deserialize<BusProfileDto>(json, ReadOptions);
            return profile?.BusLines ?? new List<BusLineDto>();
        }
        catch (JsonException)
        {
            return new List<BusLineDto>();
        }
    }

    private sealed class BusProfileDto
    {
        [JsonPropertyName("bus_lines")] public List<BusLineDto>? BusLines { get; set; }
    }

    private sealed class BusLineDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("line")] public string? Line { get; set; }
        [JsonPropertyName("lane")] public int Lane { get; set; } = -1;
        [JsonPropertyName("headway")] public double? Headway { get; set; }
        [JsonPropertyName("begin")] public double? Begin { get; set; }
        [JsonPropertyName("end")] public double? End { get; set; }
        [JsonPropertyName("stops")] public List<string>? Stops { get; set; }
    }

    // ============================================================
    // 公交停靠：从"候选路线 + 车型"推到该挂哪些站
    // ============================================================

    /// <summary>
    /// 这条车流该在哪些公交站停靠。返回 (站, 在路线上的序号)。
    ///
    /// <para><b>判据</b>：车流的所有候选路线**都经过**该站所在的边。
    /// 只经过一部分候选路线时不能挂 —— flow 只有一套 stop 表，
    /// 挂上去会让"不走这条边"的那部分车报 <c>busStop not on route</c> 直接退出。</para>
    /// </summary>
    private static List<(SumoFacilities.Facility Stop, int Order)> BusStopsFor(
        Request req,
        List<IReadOnlyList<string>> candidateEdges,
        Dictionary<string, SumoVehicleType> typeById,
        SumoFacilities.Parsed facilities,
        Result result)
    {
        var found = new List<(SumoFacilities.Facility, int)>();
        List<SumoFacilities.Facility> stops = facilities.Of(SumoFacilities.KindBusStop)
            .Where(s => s.NetEdge.Length > 0)
            .ToList();
        if (stops.Count == 0)
        {
            return found;
        }

        // 车型必须全是公交：混型时插 stop 会让私家车也停（见调用处的注释）
        bool allBus = req.Mix.Count > 0;
        foreach ((string typeId, double _) in req.Mix)
        {
            if (!typeById.TryGetValue(typeId, out SumoVehicleType? t) ||
                !string.Equals(t.VClass, "bus", StringComparison.Ordinal))
            {
                allBus = false;
                break;
            }
        }

        if (!allBus)
        {
            return found;
        }

        if (candidateEdges.Count == 0)
        {
            return found;
        }

        // 所有候选路线的**公共边**
        var common = new HashSet<string>(candidateEdges[0], StringComparer.Ordinal);
        foreach (IReadOnlyList<string> edges in candidateEdges.Skip(1))
        {
            common.IntersectWith(edges);
        }

        foreach (SumoFacilities.Facility stop in stops)
        {
            if (!common.Contains(stop.NetEdge))
            {
                continue;
            }

            // 在路线上的位置：决定停靠先后（SUMO 按 <stop> 出现顺序停）
            int order = -1;
            for (int i = 0; i < candidateEdges[0].Count; i++)
            {
                if (string.Equals(candidateEdges[0][i], stop.NetEdge, StringComparison.Ordinal))
                {
                    order = i;
                    break;
                }
            }
            found.Add((stop, order));
        }

        if (found.Count == 0)
        {
            // 让用户知道"站建了但这条车流不停"，而不是默默不生效
            var blocked = stops.Where(s => candidateEdges.Any(e => e.Contains(s.NetEdge))).ToList();
            if (blocked.Count > 0)
            {
                result.Warnings.Add($"车流 {req.Id}：公交站 {string.Join("、", blocked.Select(b => b.Id))} "
                    + "只经过部分候选路线，无法挂停靠（flow 只有一套 stop 表）。"
                    + "把该进口的转率配成唯一路线，或另建一条只走那条路线的车流。");
            }
        }

        return found;
    }

    // ============================================================
    // 派生：.spmap 曲线 → 车流请求
    // ============================================================

    private static List<Request> FromSpmap(Level level, ExportGraph graph, string? profileJson, Result result)
    {
        var list = new List<Request>();
        double horizon = 3600.0;

        // ★ 传 profileJson：专业档里自定义了车型库就以它为准，
        //   否则按 game_rules.vehicle_mix 预设展开（2026-11-05 修：原来传 null，自定义被忽略）
        string? mixPreset = level.GameRules.VehicleMix?.ToString();
        string bikeType = "bicycle";
        string personType = "pedestrian";

        foreach (DemandPlan demand in level.Demand)
        {
            var segments = new List<(double Begin, double End, double Vph)>();
            for (int i = 0; i < demand.TimePoints.Count && i < demand.FlowRates.Count; i++)
            {
                double begin = demand.TimePoints[i];
                double end = i + 1 < demand.TimePoints.Count ? demand.TimePoints[i + 1] : horizon;
                segments.Add((begin, end, demand.FlowRates[i] * 3600.0));
            }

            if (segments.Count == 0)
            {
                continue;
            }

            list.Add(new Request
            {
                Id = "f" + demand.Lane.ToString(CultureInfo.InvariantCulture),
                Kind = RequestKind.Motor,
                Lane = demand.Lane,
                // ★ 按**这个进口**配的构成（lane_mix → 库份额 → 关卡预设，三级回落）
                Mix = SumoVehicleTypes.MixForLane(profileJson, demand.Lane, mixPreset),
                Segments = segments,
                RouteMode = "ratio",
            });
        }

        foreach (BicycleDemand bike in level.Bicycles)
        {
            var segments = new List<(double Begin, double End, double Vph)>();
            for (int i = 0; i < bike.TimePoints.Count && i < bike.FlowRates.Count; i++)
            {
                double begin = bike.TimePoints[i];
                double end = i + 1 < bike.TimePoints.Count ? bike.TimePoints[i + 1] : horizon;
                segments.Add((begin, end, bike.FlowRates[i] * 3600.0));
            }

            if (segments.Count == 0)
            {
                continue;
            }

            list.Add(new Request
            {
                Id = "b" + bike.Lane.ToString(CultureInfo.InvariantCulture),
                Kind = RequestKind.Bike,
                Lane = bike.Lane,
                Mix = new List<(string, double)> { (bikeType, 1.0) },
                Segments = segments,
                RouteMode = "ratio",
            });
        }

        foreach (PedestrianDemand ped in level.Pedestrians)
        {
            var segments = new List<(double Begin, double End, double Vph)>();
            for (int i = 0; i < ped.TimePoints.Count && i < ped.FlowRates.Count; i++)
            {
                double begin = ped.TimePoints[i];
                double end = i + 1 < ped.TimePoints.Count ? ped.TimePoints[i + 1] : horizon;
                segments.Add((begin, end, ped.FlowRates[i] * 3600.0));
            }

            if (segments.Count == 0)
            {
                continue;
            }

            list.Add(new Request
            {
                Id = "p" + ped.CrosswalkTurn.ToString(CultureInfo.InvariantCulture),
                Kind = RequestKind.Person,
                Lane = -1,
                CrosswalkTurn = ped.CrosswalkTurn,
                Mix = new List<(string, double)> { (personType, 1.0) },
                Segments = segments,
            });
        }

        if (list.Count > 0 && level.Demand.Count == 0)
        {
            result.Warnings.Add("这张图没有机动车需求曲线，.rou.xml 里只有行人/自行车。");
        }

        return list;
    }

    // ============================================================
    // 专业档的 flows / person_flows / bike_flows
    // ============================================================

    private static List<Request> FromProfile(ProfileFlows profile, ExportGraph graph, Result result)
    {
        var list = new List<Request>();

        foreach (FlowDto dto in profile.Flows ?? new List<FlowDto>())
        {
            var segments = dto.Segments?.Select(s => (s.Begin, s.End, s.VehPerHour)).ToList()
                           ?? new List<(double, double, double)>();
            if (segments.Count == 0)
            {
                result.Warnings.Add($"车流「{dto.Id}」没有时段，已跳过。");
                continue;
            }

            list.Add(new Request
            {
                Id = string.IsNullOrWhiteSpace(dto.Id) ? "f" + dto.Lane : dto.Id!,
                Kind = RequestKind.Motor,
                Lane = dto.Lane,
                Mix = new List<(string, double)> { (dto.Vtype ?? "car", 1.0) },
                Segments = segments,
                RouteMode = dto.RouteMode ?? "ratio",
                DepartLane = dto.DepartLane ?? "best",
                DepartSpeed = dto.DepartSpeed ?? "max",
            });
        }

        foreach (FlowDto dto in profile.BikeFlows ?? new List<FlowDto>())
        {
            var segments = dto.Segments?.Select(s => (s.Begin, s.End, s.VehPerHour)).ToList()
                           ?? new List<(double, double, double)>();
            if (segments.Count == 0)
            {
                continue;
            }

            list.Add(new Request
            {
                Id = string.IsNullOrWhiteSpace(dto.Id) ? "b" + dto.Lane : dto.Id!,
                Kind = RequestKind.Bike,
                Lane = dto.Lane,
                Mix = new List<(string, double)> { (dto.Vtype ?? "bicycle", 1.0) },
                Segments = segments,
                RouteMode = dto.RouteMode ?? "ratio",
            });
        }

        foreach (FlowDto dto in profile.PersonFlows ?? new List<FlowDto>())
        {
            var segments = dto.Segments?.Select(s => (s.Begin, s.End, s.VehPerHour)).ToList()
                           ?? new List<(double, double, double)>();
            if (segments.Count == 0)
            {
                continue;
            }

            list.Add(new Request
            {
                Id = string.IsNullOrWhiteSpace(dto.Id) ? "p" + dto.CrosswalkTurn : dto.Id!,
                Kind = RequestKind.Person,
                Lane = -1,
                CrosswalkTurn = dto.CrosswalkTurn,
                Mix = new List<(string, double)> { (dto.Vtype ?? "pedestrian", 1.0) },
                Segments = segments,
            });
        }

        return list;
    }

    // ============================================================
    // 元件构造
    // ============================================================

    private static XElement BuildVType(SumoVehicleType t)
    {
        var e = new XElement("vType",
            new XAttribute("id", t.Id),
            new XAttribute("vClass", t.VClass),
            new XAttribute("length", F(t.Length)),
            new XAttribute("maxSpeed", F(t.MaxSpeed)),
            new XAttribute("accel", F(t.Accel)),
            new XAttribute("decel", F(t.Decel)),
            new XAttribute("sigma", F(t.Sigma)),
            new XAttribute("minGap", F(t.MinGap)),
            new XAttribute("tau", F(t.Tau)),
            new XAttribute("speedFactor", F(t.SpeedFactor)),
            new XAttribute("color", t.Color));

        if (t.IsPerson)
        {
            // 行人用 personFlow，不走 vType 的车辆属性；这里只在被误引为车辆时兜底
            e.SetAttributeValue("guiShape", "pedestrian");
        }

        return e;
    }

    /// <summary>
    /// 行人：<c>personFlow</c> + <c>walk</c>。
    /// <para>walk 的 edge 序列 = 出发人行道所在的边 → 目标人行道所在的边（中间由 SUMO 自己走横道）。
    /// 2026-11-05 冒烟：示例路口 30 人 / 20 段 walk 全走通，不需要降级。</para>
    /// </summary>
    private static XElement? BuildPersonFlow(Request req, Dictionary<int, string> laneToEdge, Result result)
    {
        ExportTurn? turn = null;
        // 只认 leg=0 那条（分段过街的两条 turn 共用同一对车道，leg=0 才是 pedestrians 指向的）
        foreach (ExportTurn t in req.Graph!.Turns)
        {
            if (t.Id == req.CrosswalkTurn && t.IsCrosswalk && t.Leg <= 0)
            {
                turn = t;
                break;
            }
        }

        if (turn == null)
        {
            result.Warnings.Add($"行人需求 {req.Id}：转向 {req.CrosswalkTurn} 不是 leg=0 的人行横道，已跳过。");
            return null;
        }

        if (!laneToEdge.TryGetValue(turn.Src.Id, out string? fromEdge) ||
            !laneToEdge.TryGetValue(turn.Dst.Id, out string? toEdge))
        {
            result.Warnings.Add($"行人需求 {req.Id}：横道两端的车道在 net.xml 里找不到边，已跳过。");
            return null;
        }

        var walk = new XElement("walk",
            new XAttribute("edges", string.Equals(fromEdge, toEdge, StringComparison.Ordinal)
                ? fromEdge
                : fromEdge + " " + toEdge));

        var element = new XElement("personFlow",
            new XAttribute("id", req.Id),
            new XAttribute("type", req.Mix[0].TypeId),
            new XAttribute("departPos", "0"));

        double first = double.MaxValue;
        double last = double.MinValue;
        double persons = 0.0;
        foreach ((double begin, double end, double vph) in req.Segments)
        {
            first = Math.Min(first, begin);
            last = Math.Max(last, end);
            persons += (end - begin) / 3600.0 * vph;
        }

        if (first == double.MaxValue)
        {
            return null;
        }

        result.ExpectedPersons += persons;

        element.SetAttributeValue("begin", F(first));
        element.SetAttributeValue("end", F(last));
        element.SetAttributeValue("number", ((int)Math.Round(persons, MidpointRounding.AwayFromZero))
            .ToString(CultureInfo.InvariantCulture));
        element.Add(walk);
        return element;
    }

    // ============================================================
    // 车道 → edge
    // ============================================================

    /// <summary>
    /// 用 net.xml 的 <c>origID</c> 反查：.spmap 车道 id → net 的 edge id。
    /// 不猜 edge 命名规则（`-3` / `:0_0` 都见过），一律走 <see cref="SumoNetIndex.LaneMap"/>。
    /// </summary>
    private static Dictionary<int, string> BuildLaneEdges(string netPath, ExportGraph graph)
    {
        XDocument doc = XDocument.Load(netPath);
        Dictionary<string, int> netLaneToSpmap = SumoNetIndex.LaneMap(doc, graph);

        var result = new Dictionary<int, string>();
        foreach (XElement edge in doc.Root?.Elements("edge") ?? Enumerable.Empty<XElement>())
        {
            string? edgeId = (string?)edge.Attribute("id");
            if (string.IsNullOrEmpty(edgeId) || edgeId.StartsWith(":", StringComparison.Ordinal))
            {
                continue;   // 内部边（路口内部车道）不参与路线
            }

            foreach (XElement lane in edge.Elements("lane"))
            {
                string? laneId = (string?)lane.Attribute("id");
                if (string.IsNullOrEmpty(laneId))
                {
                    continue;
                }

                if (netLaneToSpmap.TryGetValue(laneId, out int spmapLane))
                {
                    result.TryAdd(spmapLane, edgeId);
                }
            }
        }

        return result;
    }

    // ============================================================
    // 内部类型
    // ============================================================

    private enum RequestKind
    {
        Motor,
        Bike,
        Person,
    }

    private sealed class Request
    {
        internal string Id { get; init; } = string.Empty;
        internal RequestKind Kind { get; init; }
        internal int Lane { get; init; }
        internal int CrosswalkTurn { get; init; } = -1;
        internal List<(string TypeId, double Share)> Mix { get; init; } = new();
        internal List<(double Begin, double End, double Vph)> Segments { get; init; } = new();
        internal string RouteMode { get; init; } = "ratio";
        internal string DepartLane { get; init; } = "best";
        internal string DepartSpeed { get; init; } = "max";

        /// <summary>行人用：从哪张转向表里找横道。<b>不入 XML</b>，构造前由调用方补。</summary>
        internal ExportGraph? Graph { get; set; }
    }

    private sealed class ProfileFlows
    {
        // ★ 属性必须是 **public** —— System.Text.Json 默认**不读 internal 属性**，
        //   而 private 嵌套类里的 public 属性是能读的（"public"指可访问性，不是可见性）。
        //   2026-11-05 踩过：这里原来写 internal，导致专业档的 flows[] 一直读不到
        //   （静默回落到 .spmap 曲线），而且不报错，只能靠"配了却不生效"发现。

        internal bool HasAny =>
            (Flows?.Count ?? 0) + (PersonFlows?.Count ?? 0) + (BikeFlows?.Count ?? 0) > 0;

        [JsonPropertyName("flows")] public List<FlowDto>? Flows { get; set; }
        [JsonPropertyName("person_flows")] public List<FlowDto>? PersonFlows { get; set; }
        [JsonPropertyName("bike_flows")] public List<FlowDto>? BikeFlows { get; set; }
    }

    private sealed class FlowDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("lane")] public int Lane { get; set; }
        [JsonPropertyName("crosswalk_turn")] public int CrosswalkTurn { get; set; } = -1;
        [JsonPropertyName("vtype")] public string? Vtype { get; set; }
        [JsonPropertyName("segments")] public List<SegmentDto>? Segments { get; set; }
        [JsonPropertyName("depart_lane")] public string? DepartLane { get; set; }
        [JsonPropertyName("depart_speed")] public string? DepartSpeed { get; set; }
        [JsonPropertyName("route_mode")] public string? RouteMode { get; set; }
    }

    private sealed class SegmentDto
    {
        [JsonPropertyName("begin")] public double Begin { get; set; }
        [JsonPropertyName("end")] public double End { get; set; } = 3600.0;
        [JsonPropertyName("veh_per_hour")] public double VehPerHour { get; set; }
    }

    private static ProfileFlows? ParseProfile(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            ProfileFlows? profile = JsonSerializer.Deserialize<ProfileFlows>(json, ReadOptions);
            return profile;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
