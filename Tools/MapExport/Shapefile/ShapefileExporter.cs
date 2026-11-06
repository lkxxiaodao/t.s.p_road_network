using System.Text;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO.Esri;
using SignalPlanner.MapExport.Geo;
using EsriShapefile = NetTopologySuite.IO.Esri.Shapefile;

namespace SignalPlanner.MapExport.Shapefile;

/// <summary>
/// 把 <see cref="ExportGraph"/> 写成三个（可选四个）ESRI Shapefile 图层。
/// 每个图层都先写临时文件，再原子替换正式文件。
/// </summary>
public static class ShapefileExporter
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static void Write(ExportGraph graph, MapExportOptions options, GeoProjection geo, string outputBase)
    {
        WriteRoads(graph, geo, outputBase + "_roads");
        WriteLanes(graph, geo, outputBase + "_lanes");
        WriteNodes(graph, geo, outputBase + "_nodes");
        if (options.IncludeCrosswalks)
        {
            WriteCrosswalks(graph, geo, outputBase + "_crosswalks");
        }
    }

    private static void WriteRoads(ExportGraph graph, GeoProjection geo, string basePath)
    {
        var features = new List<Feature>();
        foreach (ExportRoad road in graph.Roads.Values.OrderBy(r => r.Id))
        {
            (double ax, double ay) = geo.ToShapefile(road.Ax, road.Ay);
            (double bx, double by) = geo.ToShapefile(road.Bx, road.By);
            var line = new LineString(new[] { new Coordinate(ax, ay), new Coordinate(bx, by) });

            double speed = 0.0;
            foreach (ExportLane lane in road.AllLanes.Where(IsMotor))
            {
                speed = Math.Max(speed, lane.SpeedLimit);
            }

            var attributes = new AttributesTable
            {
                { "road_id", road.Id },
                { "lanes_fwd", road.ForwardLanes.Count(IsMotor) },
                { "lanes_rev", road.BackwardLanes.Count(IsMotor) },
                { "oneway", road.ForwardLanes.Count(IsMotor) == 0 || road.BackwardLanes.Count(IsMotor) == 0 ? 1 : 0 },
                { "speed_kmh", Math.Round(speed * 3.6, 1) },
                { "name", string.Empty },
            };
            features.Add(new Feature(line, attributes));
        }

        WriteLayer(features, basePath, geo.PrjWkt);
    }

    private static void WriteLanes(ExportGraph graph, GeoProjection geo, string basePath)
    {
        var features = new List<Feature>();
        foreach (ExportLane lane in graph.Lanes.Values.OrderBy(l => l.Id))
        {
            (double sx, double sy) = graph.LanePointAtNode(lane, lane.FromNode);
            (double ex, double ey) = graph.LanePointAtNode(lane, lane.ToNode);
            (double ax, double ay) = geo.ToShapefile(sx, sy);
            (double bx, double by) = geo.ToShapefile(ex, ey);
            var line = new LineString(new[] { new Coordinate(ax, ay), new Coordinate(bx, by) });

            var attributes = new AttributesTable
            {
                { "lane_id", lane.Id },
                { "road_id", lane.RoadId },
                { "lane_type", lane.Type.ToString() },
                { "from_node", lane.FromNode },
                { "to_node", lane.ToNode },
                { "length_m", Math.Round(lane.Length, 2) },
                { "speed_kmh", Math.Round(lane.SpeedLimit * 3.6, 1) },
            };
            features.Add(new Feature(line, attributes));
        }

        WriteLayer(features, basePath, geo.PrjWkt);
    }

    private static void WriteNodes(ExportGraph graph, GeoProjection geo, string basePath)
    {
        var features = new List<Feature>();
        foreach (ExportNode node in graph.Nodes.Values.OrderBy(n => n.Id))
        {
            (double x, double y) = geo.ToShapefile(node.X, node.Y);
            var point = new Point(x, y);
            var attributes = new AttributesTable
            {
                { "node_id", node.Id },
                { "node_type", node.Type.ToString() },
                { "x_m", Math.Round(node.X, 2) },
                { "y_m", Math.Round(node.Y, 2) },
            };
            features.Add(new Feature(point, attributes));
        }

        WriteLayer(features, basePath, geo.PrjWkt);
    }

    private static void WriteCrosswalks(ExportGraph graph, GeoProjection geo, string basePath)
    {
        var features = new List<Feature>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ExportTurn turn in graph.Turns.Where(t => t.IsCrosswalk).OrderBy(t => t.Id))
        {
            string key = $"{turn.Src.Id}:{turn.Dst.Id}";
            if (!seen.Add(key))
            {
                continue;
            }

            (double sx, double sy) = graph.LanePointAtNode(turn.Src, turn.NodeId);
            (double dx, double dy) = graph.LanePointAtNode(turn.Dst, turn.NodeId);
            if (Math.Abs(sx - dx) < 1e-9 && Math.Abs(sy - dy) < 1e-9)
            {
                continue;
            }

            (double ax, double ay) = geo.ToShapefile(sx, sy);
            (double bx, double by) = geo.ToShapefile(dx, dy);
            var line = new LineString(new[] { new Coordinate(ax, ay), new Coordinate(bx, by) });
            var attributes = new AttributesTable
            {
                { "turn_id", turn.Id },
                { "node_id", turn.NodeId },
                { "leg", turn.Leg },
                { "island", turn.IslandId },
            };
            features.Add(new Feature(line, attributes));
        }

        WriteLayer(features, basePath, geo.PrjWkt);
    }

    private static void WriteLayer(List<Feature> features, string finalBase, string prjWkt)
    {
        if (features.Count == 0)
        {
            return;
        }

        string finalShp = finalBase + ".shp";
        string globalPrj = Path.ChangeExtension(finalShp, ".prj");
        string globalCpg = Path.ChangeExtension(finalShp, ".cpg");
        string tmpShp = finalBase + ".tmp.shp";
        try
        {
            // 先清掉旧输出，避免旧 .dbf/.prj 残留造成“看起来存在、实际没更新”。
            TryDelete(finalShp);
            TryDelete(Path.ChangeExtension(finalShp, ".shx"));
            TryDelete(Path.ChangeExtension(finalShp, ".dbf"));
            TryDelete(globalPrj);
            TryDelete(globalCpg);
            TryDelete(tmpShp);
            TryDelete(Path.ChangeExtension(tmpShp, ".shx"));
            TryDelete(Path.ChangeExtension(tmpShp, ".dbf"));
            TryDelete(Path.ChangeExtension(tmpShp, ".prj"));
            TryDelete(Path.ChangeExtension(tmpShp, ".cpg"));

            EsriShapefile.WriteAllFeatures(features, tmpShp, prjWkt, Utf8NoBom);
            MoveIfExists(Path.ChangeExtension(tmpShp, ".shp"), Path.ChangeExtension(finalShp, ".shp"));
            MoveIfExists(Path.ChangeExtension(tmpShp, ".shx"), Path.ChangeExtension(finalShp, ".shx"));
            MoveIfExists(Path.ChangeExtension(tmpShp, ".dbf"), Path.ChangeExtension(finalShp, ".dbf"));

            // .prj/.cpg 由我们自己写，确保和最终 .shp 同名同目录（不依赖 NTS 的临时文件名）。
            File.WriteAllText(globalPrj, prjWkt, Utf8NoBom);
            File.WriteAllText(globalCpg, "UTF-8", Utf8NoBom);

            // NTS 可能为临时路径额外写 .cpg/.prj，清掉，避免目录里出现 *.tmp.cpg。
            TryDelete(Path.ChangeExtension(tmpShp, ".cpg"));
            TryDelete(Path.ChangeExtension(tmpShp, ".prj"));

            if (!File.Exists(finalShp) ||
                !File.Exists(Path.ChangeExtension(finalShp, ".shx")) ||
                !File.Exists(Path.ChangeExtension(finalShp, ".dbf")) ||
                !File.Exists(globalPrj))
            {
                throw new InvalidOperationException(
                    "Shapefile 写出不完整（需要 .shp/.shx/.dbf/.prj 四件套）：" + finalBase);
            }
        }
        catch
        {
            TryDelete(tmpShp);
            TryDelete(Path.ChangeExtension(tmpShp, ".shx"));
            TryDelete(Path.ChangeExtension(tmpShp, ".dbf"));
            TryDelete(Path.ChangeExtension(tmpShp, ".prj"));
            TryDelete(Path.ChangeExtension(tmpShp, ".cpg"));
            throw;
        }
    }

    private static bool IsMotor(ExportLane lane)
        => lane.Type is SimCore.Model.LaneType.Driving
            or SimCore.Model.LaneType.Bus
            or SimCore.Model.LaneType.SharedLeftTurn;

    private static void MoveIfExists(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Move(source, destination, true);
        }
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
            // 清理失败不影响主错误。
        }
    }
}
