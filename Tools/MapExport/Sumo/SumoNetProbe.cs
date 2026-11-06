using System.Globalization;
using System.Xml.Linq;
using SignalPlanner.MapExport.OpenDrive;
using SimCore.Io;
using SimCore.Model;

namespace SignalPlanner.MapExport.Sumo;

/// <summary>
/// 探针：真跑一遍 netconvert，把 <b>SUMO 实际分配的 link 表</b>交回编辑器。
///
/// 为什么必须有它：`tlLogic` 的 `state` 串是按 `linkIndex` 排的，而 linkIndex 由 netconvert
/// 自己决定（含人行横道、自行车连接、内部车道）。编辑器如果凭空假设 link 顺序，
/// 用户编出来的配时就会和实际生效的灯错位。所以专业模式的信号灯编辑器必须先"看见"这张表。
///
/// 输出刻意用 snake_case，与 .spmap 的字段风格一致；由 <c>MapExportBridge</c> 原样序列化。
/// </summary>
public static class SumoNetProbe
{
    /// <summary>
    /// 跑一次探针。<b>会启动 netconvert 子进程（示例关卡约 0.3 s，大网更久）</b>，
    /// 调用方必须放在后台线程里，别卡住界面。
    /// </summary>
    public static Dictionary<string, object?> Probe(string spmapPath, MapExportOptions options)
    {
        if (string.IsNullOrWhiteSpace(spmapPath) || !File.Exists(spmapPath))
        {
            return Fail("找不到 .spmap 文件：" + spmapPath);
        }

        string exe = NetconvertRunner.Find(options.NetconvertPath, options.NetconvertSearchDir) ?? string.Empty;
        if (exe.Length == 0)
        {
            return Fail("未检测到 netconvert。请安装 SUMO 或在导出面板指定路径。");
        }

        string workDir = Path.Combine(Path.GetTempPath(), "spmap-probe-" + Guid.NewGuid().ToString("N"));
        string xodr = Path.Combine(workDir, "probe.xodr");
        string netPath = Path.Combine(workDir, "probe.net.xml");
        string? extraTypeFile = null;

        try
        {
            Directory.CreateDirectory(workDir);

            Level level = Spmap.Load(spmapPath);
            ExportGraph graph = ExportGraph.Build(level, options.LaneWidthM);
            OpenDriveExporter.Write(graph, options, null, xodr);

            List<string> typeFiles = MapExporter.BuildTypeFiles(exe, Path.Combine(workDir, "probe"),
                out extraTypeFile, out _);

            // ★ 探针必须用**与导出完全相同**的 netconvert 开关，否则"编辑器里看到的灯位"
            //   和"导出后真正生效的灯位"会分叉（与 SumoNetIndex 共用同一套口径是一个道理）。
            SumoNetGenOptions gen = SumoNetGenOptions.FromProfile(options.SumoProfileJson);
            NetconvertRunResult run = NetconvertRunner.Run(
                exe, xodr, netPath, keepOriginalNames: true, typeFiles, gen);
            if (!run.Ok)
            {
                string detail = run.TimedOut
                    ? run.StdErr
                    : FirstNonEmpty(run.StdErr, run.StdOut, "netconvert 退出码 " + run.ExitCode);
                return Fail("netconvert 生成探针网络失败：" + detail);
            }

            return Parse(netPath, graph);
        }
        catch (Exception ex)
        {
            return Fail("探针失败：" + ex.Message);
        }
        finally
        {
            if (extraTypeFile != null)
            {
                TryDelete(extraTypeFile);
            }

            TryDeleteDirectory(workDir);
        }
    }

    private static Dictionary<string, object?> Parse(string netPath, ExportGraph graph)
    {
        XDocument doc = XDocument.Load(netPath);

        var turnsById = graph.Turns.ToDictionary(t => t.Id);
        Dictionary<string, int> laneMap = SumoNetIndex.LaneMap(doc, graph);
        // ★ 专业模式要连人行横道一起看：人也是单独一跳灯。
        Dictionary<string, Dictionary<int, int>> linkTurns =
            SumoNetIndex.LinkMap(doc, graph, laneMap, includeCrosswalk: true);

        // --- 连接表：linkIndex → 起讫边/车道 ---
        var connections = new Dictionary<string, Dictionary<int, Dictionary<string, object?>>>(StringComparer.Ordinal);
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

            if (!connections.TryGetValue(tl, out Dictionary<int, Dictionary<string, object?>>? map))
            {
                map = new Dictionary<int, Dictionary<string, object?>>();
                connections[tl] = map;
            }

            string fromEdge = (string?)connection.Attribute("from") ?? string.Empty;
            string toEdge = (string?)connection.Attribute("to") ?? string.Empty;
            int fromLane = IntAttr(connection, "fromLane");
            int toLane = IntAttr(connection, "toLane");

            int turnId = -1;
            if (linkTurns.TryGetValue(tl, out Dictionary<int, int>? turnMap))
            {
                turnMap.TryGetValue(linkIndex, out turnId);
            }

            map[linkIndex] = new Dictionary<string, object?>
            {
                ["link_index"] = linkIndex,
                ["from"] = fromEdge,
                ["from_lane"] = fromLane,
                ["to"] = toEdge,
                ["to_lane"] = toLane,
                ["dir"] = (string?)connection.Attribute("dir") ?? string.Empty,
                ["turn"] = turnId,
                ["kind"] = KindOf(turnId, turnsById),
            };
        }

        // --- tlLogic ---
        var tlsList = new List<Dictionary<string, object?>>();
        foreach (XElement tlLogic in doc.Root?.Elements("tlLogic") ?? Enumerable.Empty<XElement>())
        {
            string tlId = (string?)tlLogic.Attribute("id") ?? string.Empty;
            if (tlId.Length == 0)
            {
                continue;
            }

            var phases = new List<Dictionary<string, object?>>();
            int linkCount = 0;
            foreach (XElement phase in tlLogic.Elements("phase"))
            {
                string state = (string?)phase.Attribute("state") ?? string.Empty;
                linkCount = Math.Max(linkCount, state.Length);
                phases.Add(new Dictionary<string, object?>
                {
                    ["duration"] = DoubleAttr(phase, "duration"),
                    ["state"] = state,
                });
            }

            var links = new List<Dictionary<string, object?>>();
            if (connections.TryGetValue(tlId, out Dictionary<int, Dictionary<string, object?>>? linkMap))
            {
                foreach (int index in linkMap.Keys.OrderBy(k => k))
                {
                    links.Add(linkMap[index]);
                }
            }

            tlsList.Add(new Dictionary<string, object?>
            {
                ["id"] = tlId,
                ["node"] = int.TryParse(tlId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int nodeId)
                    ? nodeId
                    : -1,
                ["program_id"] = (string?)tlLogic.Attribute("programID") ?? string.Empty,
                ["type"] = (string?)tlLogic.Attribute("type") ?? string.Empty,
                ["offset"] = DoubleAttr(tlLogic, "offset"),
                ["link_count"] = linkCount,
                ["links"] = links,
                ["phases"] = phases,
            });
        }

        // --- 边表（跳过内部边：那是路口内的车道，用户不直接编辑）---
        var edges = new List<Dictionary<string, object?>>();
        foreach (XElement edge in doc.Root?.Elements("edge") ?? Enumerable.Empty<XElement>())
        {
            if (string.Equals((string?)edge.Attribute("function"), "internal", StringComparison.Ordinal))
            {
                continue;
            }

            var lanes = edge.Elements("lane").ToList();
            edges.Add(new Dictionary<string, object?>
            {
                ["id"] = (string?)edge.Attribute("id") ?? string.Empty,
                ["from"] = (string?)edge.Attribute("from") ?? string.Empty,
                ["to"] = (string?)edge.Attribute("to") ?? string.Empty,
                ["type"] = (string?)edge.Attribute("type") ?? string.Empty,
                ["lanes"] = lanes.Count,
                ["speed"] = lanes.Count > 0 ? DoubleAttr(lanes[0], "speed") : 0.0,
            });
        }

        return new Dictionary<string, object?>
        {
            ["ok"] = true,
            ["error"] = string.Empty,
            ["tls"] = tlsList,
            ["edges"] = edges,
        };
    }

    /// <summary>这一跳灯是什么：机动车 / 自行车 / 人行横道 / 认不出来。</summary>
    private static string KindOf(int turnId, Dictionary<int, ExportTurn> turnsById)
    {
        if (turnId < 0 || !turnsById.TryGetValue(turnId, out ExportTurn? turn))
        {
            return "unknown";
        }

        if (turn.IsCrosswalk)
        {
            return "crosswalk";
        }

        return turn.Src.Type switch
        {
            LaneType.Biking => "bike",
            LaneType.Sidewalk => "crosswalk",
            LaneType.Driving or LaneType.Bus or LaneType.SharedLeftTurn => "motor",
            _ => "other",
        };
    }

    private static int IntAttr(XElement element, string name)
        => int.TryParse((string?)element.Attribute(name), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int value) ? value : 0;

    private static double DoubleAttr(XElement element, string name)
        => double.TryParse((string?)element.Attribute(name), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double value) ? value : 0.0;

    private static Dictionary<string, object?> Fail(string message) => new()
    {
        ["ok"] = false,
        ["error"] = message,
        ["tls"] = new List<Dictionary<string, object?>>(),
        ["edges"] = new List<Dictionary<string, object?>>(),
    };

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (string value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响结果。
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // 可能还有句柄没释放，留着也无害（系统临时目录）。
        }
    }
}
