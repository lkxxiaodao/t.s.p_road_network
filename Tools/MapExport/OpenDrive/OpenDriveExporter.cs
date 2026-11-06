using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using SignalPlanner.MapExport.Geo;
using SimCore.Model;

namespace SignalPlanner.MapExport.OpenDrive;

/// <summary>
/// 把 <see cref="ExportGraph"/> 写成 OpenDRIVE 1.4（.xodr）。
/// ★ 仅供 SUMO net.xml 的内部中间格式使用：不在导出页面暴露，生成的临时 .xodr 会在 netconvert 后删除。
/// 每条 spmap road 拆成两条“单向 road”（a→b / b→a），与 netconvert 自身的 OpenDRIVE 输出结构一致，
/// 这样 SUMO netconvert 才能把信号灯正确归到路口。
/// </summary>
public static class OpenDriveExporter
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Write(ExportGraph graph, MapExportOptions options, GeoProjection? geo, string path)
    {
        string tmp = path + ".tmp";
        try
        {
            using (var writer = XmlWriter.Create(tmp, CreateSettings()))
            {
                WriteDocument(writer, graph, options, geo);
            }

            File.Move(tmp, path, true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private static XmlWriterSettings CreateSettings()
    {
        return new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Replace,
            Encoding = Utf8NoBom,
            OmitXmlDeclaration = false,
        };
    }

    private static void WriteDocument(XmlWriter writer, ExportGraph graph, MapExportOptions options, GeoProjection? geo)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement("OpenDRIVE");

        WriteHeader(writer, graph, geo);

        foreach (ExportRoad road in graph.Roads.Values.OrderBy(r => r.Id))
        {
            if (road.ForwardLanes.Count > 0)
            {
                WriteDirectionalRoad(writer, graph, road, forward: true);
            }

            if (road.BackwardLanes.Count > 0)
            {
                WriteDirectionalRoad(writer, graph, road, forward: false);
            }
        }

        // 连接道路也是 <road>，必须在 controller / junction 之前写完。
        foreach (ExportTurn turn in graph.Turns.Where(t => t.ShouldExport).OrderBy(t => t.Id))
        {
            WriteConnectingRoad(writer, graph, turn);
        }

        foreach (ExportNode node in graph.Nodes.Values
                     .Where(n => n.Type == NodeType.Signalized)
                     .OrderBy(n => n.Id))
        {
            WriteController(writer, graph, node);
        }

        foreach (int nodeId in graph.JunctionNodes.OrderBy(id => id))
        {
            WriteJunction(writer, graph, nodeId);
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static void WriteHeader(XmlWriter writer, ExportGraph graph, GeoProjection? geo)
    {
        writer.WriteStartElement("header");
        writer.WriteAttributeString("revMajor", "1");
        writer.WriteAttributeString("revMinor", "4");
        writer.WriteAttributeString("name", "SignalPlanner");
        writer.WriteAttributeString("version", "1");
        writer.WriteAttributeString("vendor", "SignalPlanner");

        if (geo != null && geo.Kind != GeoProjectionKind.Local && graph.Nodes.Count > 0)
        {
            double minLon = double.MaxValue;
            double maxLon = double.MinValue;
            double minLat = double.MaxValue;
            double maxLat = double.MinValue;
            foreach (ExportNode node in graph.Nodes.Values)
            {
                (double lon, double lat) = geo.ToWgs84(node.X, node.Y);
                minLon = Math.Min(minLon, lon);
                maxLon = Math.Max(maxLon, lon);
                minLat = Math.Min(minLat, lat);
                maxLat = Math.Max(maxLat, lat);
            }

            writer.WriteAttributeString("north", F(maxLat));
            writer.WriteAttributeString("south", F(minLat));
            writer.WriteAttributeString("east", F(maxLon));
            writer.WriteAttributeString("west", F(minLon));

            string proj = geo.Proj4;
            if (!string.IsNullOrEmpty(proj))
            {
                writer.WriteStartElement("geoReference");
                writer.WriteCData(proj);
                writer.WriteEndElement();
            }
        }

        WriteSignalPlanUserData(writer, graph);
        writer.WriteEndElement();
    }

    private static void WriteSignalPlanUserData(XmlWriter writer, ExportGraph graph)
    {
        var signals = graph.Nodes.Values
            .Where(n => n.Plans.Count > 0)
            .OrderBy(n => n.Id)
            .Select(n => new
            {
                node = n.Id,
                offset = n.Offset,
                plans = n.Plans.OrderBy(p => p.StartTime).Select(p => new
                {
                    start_time = p.StartTime,
                    stages = p.Stages.Select(s => new
                    {
                        type = s.Type.ToString(),
                        duration = s.Duration,
                        @protected = s.ProtectedTurnIds,
                        permitted = s.PermittedTurnIds,
                    }).ToArray(),
                }).ToArray(),
            })
            .ToArray();

        if (signals.Length == 0)
        {
            return;
        }

        string json = JsonSerializer.Serialize(new { signals }, JsonOptions);
        writer.WriteStartElement("userData");
        writer.WriteAttributeString("code", "sp.signalPlans");
        writer.WriteAttributeString("value", Convert.ToBase64String(Utf8NoBom.GetBytes(json)));
        writer.WriteEndElement();
    }

    private static void WriteDirectionalRoad(XmlWriter writer, ExportGraph graph, ExportRoad road, bool forward)
    {
        List<ExportLane> laneList = forward ? road.ForwardLanes : road.BackwardLanes;
        if (laneList.Count == 0)
        {
            return;
        }

        int roadId = road.DirectionalRoadId(forward);
        int startNode = road.DirectionalStartNode(forward);
        int endNode = road.DirectionalEndNode(forward);
        double x = forward ? road.Ax : road.Bx;
        double y = forward ? road.Ay : road.By;
        double dx = forward ? road.Dx : -road.Dx;
        double dy = forward ? road.Dy : -road.Dy;
        double heading = Math.Atan2(dy, dx);

        writer.WriteStartElement("road");
        writer.WriteAttributeString("id", roadId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("name", $"road_{road.Id}_{(forward ? "f" : "b")}");
        writer.WriteAttributeString("length", F(road.Length));
        writer.WriteAttributeString("junction", "-1");

        bool hasStartLink = graph.JunctionNodes.Contains(startNode) || LinkedRoadAt(road, startNode) >= 0;
        bool hasEndLink = graph.JunctionNodes.Contains(endNode) || LinkedRoadAt(road, endNode) >= 0;
        if (hasStartLink || hasEndLink)
        {
            writer.WriteStartElement("link");
            WriteDirectionalEndpoint(writer, "predecessor", graph, road, forward, isStart: true);
            WriteDirectionalEndpoint(writer, "successor", graph, road, forward, isStart: false);
            writer.WriteEndElement();
        }

        writer.WriteStartElement("planView");
        writer.WriteStartElement("geometry");
        writer.WriteAttributeString("s", "0");
        writer.WriteAttributeString("x", F(x));
        writer.WriteAttributeString("y", F(y));
        writer.WriteAttributeString("hdg", F(heading));
        writer.WriteAttributeString("length", F(road.Length));
        writer.WriteElementString("line", string.Empty);
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("elevationProfile");
        writer.WriteStartElement("elevation");
        writer.WriteAttributeString("s", "0");
        writer.WriteAttributeString("a", "0");
        writer.WriteAttributeString("b", "0");
        writer.WriteAttributeString("c", "0");
        writer.WriteAttributeString("d", "0");
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("lanes");
        writer.WriteStartElement("laneSection");
        writer.WriteAttributeString("s", "0");
        writer.WriteElementString("center", string.Empty);
        writer.WriteStartElement("right");
        foreach (ExportLane lane in laneList.OrderBy(l => l.Rank))
        {
            WriteLane(writer, graph, lane);
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();

        List<ExportSignalHead> heads = graph.SignalHeads
            .Where(h => h.DirectionalRoadId == roadId)
            .OrderBy(h => h.SignalId)
            .ToList();
        if (heads.Count > 0)
        {
            writer.WriteStartElement("signals");
            foreach (ExportSignalHead head in heads)
            {
                writer.WriteStartElement("signal");
                writer.WriteAttributeString("id", head.SignalId.ToString(CultureInfo.InvariantCulture));
                writer.WriteAttributeString("s", F(head.S));
                writer.WriteAttributeString("t", F(head.T));
                writer.WriteAttributeString("x", F(head.X));
                writer.WriteAttributeString("y", F(head.Y));
                writer.WriteAttributeString("z", "0");
                writer.WriteAttributeString("hOffset", "0");
                writer.WriteAttributeString("pitch", "0");
                writer.WriteAttributeString("roll", "0");
                writer.WriteAttributeString("zOffset", "5");
                writer.WriteAttributeString("orientation", "+");
                writer.WriteAttributeString("dynamic", "yes");
                writer.WriteAttributeString("country", "OpenDRIVE");
                writer.WriteAttributeString("type", "1000011");
                writer.WriteAttributeString("subtype", "10");
                writer.WriteAttributeString("value", "off");
                writer.WriteAttributeString("height", "0.78");
                writer.WriteAttributeString("width", "0.26");
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static int LinkedRoadAt(ExportRoad road, int nodeId)
    {
        if (road.NodeA == nodeId)
        {
            return road.LinkRoadA;
        }

        return road.NodeB == nodeId ? road.LinkRoadB : -1;
    }

    private static void WriteDirectionalEndpoint(
        XmlWriter writer,
        string elementName,
        ExportGraph graph,
        ExportRoad road,
        bool forward,
        bool isStart)
    {
        int nodeId = isStart ? road.DirectionalStartNode(forward) : road.DirectionalEndNode(forward);
        if (graph.JunctionNodes.Contains(nodeId))
        {
            writer.WriteStartElement(elementName);
            writer.WriteAttributeString("elementType", "junction");
            writer.WriteAttributeString("elementId", nodeId.ToString(CultureInfo.InvariantCulture));
            writer.WriteEndElement();
            return;
        }

        int linkRoadId = LinkedRoadAt(road, nodeId);
        if (linkRoadId < 0 || !graph.Roads.TryGetValue(linkRoadId, out ExportRoad? other))
        {
            return;
        }

        bool? otherForward = null;
        if (isStart)
        {
            // predecessor：上一条单向 road 必须在这里结束。
            if (other.DirectionalEndNode(true) == nodeId)
            {
                otherForward = true;
            }
            else if (other.DirectionalEndNode(false) == nodeId)
            {
                otherForward = false;
            }
        }
        else
        {
            // successor：下一条单向 road 必须从这里开始。
            if (other.DirectionalStartNode(true) == nodeId)
            {
                otherForward = true;
            }
            else if (other.DirectionalStartNode(false) == nodeId)
            {
                otherForward = false;
            }
        }

        if (otherForward == null)
        {
            return;
        }

        bool isOtherStart = other.DirectionalStartNode(otherForward.Value) == nodeId;
        writer.WriteStartElement(elementName);
        writer.WriteAttributeString("elementType", "road");
        writer.WriteAttributeString("elementId",
            other.DirectionalRoadId(otherForward.Value).ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("contactPoint", isOtherStart ? "start" : "end");
        writer.WriteEndElement();
    }

    private static void WriteLane(XmlWriter writer, ExportGraph graph, ExportLane lane)
    {
        writer.WriteStartElement("lane");
        writer.WriteAttributeString("id", lane.OdrLaneId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("type", MapLaneType(lane.Type));
        writer.WriteAttributeString("level", "false");

        if (lane.PredecessorLaneId.HasValue || lane.SuccessorLaneId.HasValue)
        {
            writer.WriteStartElement("link");
            if (lane.PredecessorLaneId.HasValue)
            {
                writer.WriteStartElement("predecessor");
                writer.WriteAttributeString("id", lane.PredecessorLaneId.Value.ToString(CultureInfo.InvariantCulture));
                writer.WriteEndElement();
            }

            if (lane.SuccessorLaneId.HasValue)
            {
                writer.WriteStartElement("successor");
                writer.WriteAttributeString("id", lane.SuccessorLaneId.Value.ToString(CultureInfo.InvariantCulture));
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteStartElement("width");
        writer.WriteAttributeString("sOffset", "0");
        writer.WriteAttributeString("a", F(graph.LaneWidthM));
        writer.WriteAttributeString("b", "0");
        writer.WriteAttributeString("c", "0");
        writer.WriteAttributeString("d", "0");
        writer.WriteEndElement();

        writer.WriteStartElement("speed");
        writer.WriteAttributeString("sOffset", "0");
        writer.WriteAttributeString("max", F(Math.Max(0.1, lane.SpeedLimit)));
        writer.WriteAttributeString("unit", "m/s");
        writer.WriteEndElement();

        writer.WriteEndElement();
    }

    private static void WriteController(XmlWriter writer, ExportGraph graph, ExportNode node)
    {
        List<ExportSignalHead> heads = graph.SignalHeads
            .Where(h => h.NodeId == node.Id)
            .OrderBy(h => h.SignalId)
            .ToList();
        if (heads.Count == 0)
        {
            return;
        }

        writer.WriteStartElement("controller");
        writer.WriteAttributeString("id", node.Id.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("name", "node_" + node.Id.ToString(CultureInfo.InvariantCulture));

        foreach (ExportSignalHead head in heads)
        {
            writer.WriteStartElement("control");
            writer.WriteAttributeString("signalId", head.SignalId.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("type", "0");
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteJunction(XmlWriter writer, ExportGraph graph, int nodeId)
    {
        writer.WriteStartElement("junction");
        writer.WriteAttributeString("id", nodeId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("name", "node_" + nodeId.ToString(CultureInfo.InvariantCulture));

        foreach (ExportTurn turn in graph.TurnsAt(nodeId)
                     .Where(t => t.ShouldExport)
                     .OrderBy(t => t.Id))
        {
            writer.WriteStartElement("connection");
            writer.WriteAttributeString("id", turn.Id.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("incomingRoad", turn.Src.DirectionalRoadId.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("connectingRoad", ConnectingRoadId(graph, turn).ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("contactPoint", "start");
            writer.WriteStartElement("laneLink");
            writer.WriteAttributeString("from", turn.Src.OdrLaneId.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("to", "-1");
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteConnectingRoad(XmlWriter writer, ExportGraph graph, ExportTurn turn)
    {
        (double inX, double inY) = graph.LanePointAtNode(turn.Src, turn.NodeId);
        (double outX, double outY) = graph.LanePointAtNode(turn.Dst, turn.NodeId);

        ExportRoad srcRoad = graph.Roads[turn.Src.RoadId];
        ExportRoad dstRoad = graph.Roads[turn.Dst.RoadId];
        double t0x = turn.Src.IsForward ? srcRoad.Dx : -srcRoad.Dx;
        double t0y = turn.Src.IsForward ? srcRoad.Dy : -srcRoad.Dy;
        double t1x = turn.Dst.IsForward ? dstRoad.Dx : -dstRoad.Dx;
        double t1y = turn.Dst.IsForward ? dstRoad.Dy : -dstRoad.Dy;

        double chordX = outX - inX;
        double chordY = outY - inY;
        double chord = Math.Sqrt(chordX * chordX + chordY * chordY);
        double length;
        double heading;
        bool curved = false;
        double aU = 0, bU = 0, cU = 0, dU = 0;
        double aV = 0, bV = 0, cV = 0, dV = 0;

        if (chord >= 1.0)
        {
            // 三次 Hermite：入口切向 = 进口车道方向，出口切向 = 出口车道方向。
            double p1x = inX + t0x * (chord / 3.0);
            double p1y = inY + t0y * (chord / 3.0);
            double p2x = outX - t1x * (chord / 3.0);
            double p2y = outY - t1y * (chord / 3.0);

            double n0x = -t0y;
            double n0y = t0x;
            double u1 = Dot(p1x - inX, p1y - inY, t0x, t0y);
            double v1 = Dot(p1x - inX, p1y - inY, n0x, n0y);
            double u2 = Dot(p2x - inX, p2y - inY, t0x, t0y);
            double v2 = Dot(p2x - inX, p2y - inY, n0x, n0y);
            double u3 = Dot(outX - inX, outY - inY, t0x, t0y);
            double v3 = Dot(outX - inX, outY - inY, n0x, n0y);

            aU = 0;
            bU = 3.0 * u1;
            cU = 3.0 * (u2 - 2.0 * u1);
            dU = u3 - 3.0 * u2 + 3.0 * u1;
            aV = 0;
            bV = 3.0 * v1;
            cV = 3.0 * (v2 - 2.0 * v1);
            dV = v3 - 3.0 * v2 + 3.0 * v1;

            double curveLength = BezierLength(inX, inY, p1x, p1y, p2x, p2y, outX, outY);
            if (curveLength >= 0.1)
            {
                curved = true;
                length = curveLength;
                heading = Math.Atan2(t0y, t0x);
            }
            else
            {
                length = Math.Max(chord, 1.0);
                heading = Math.Atan2(chordY, chordX);
            }
        }
        else
        {
            heading = chord > 1e-9 ? Math.Atan2(chordY, chordX) : Math.Atan2(t0y, t0x);
            length = Math.Max(chord, 1.0);
            if (chord < 1.0)
            {
                // 极短/退化连接：至少给 1 m，避免 0.1 m 路段在仿真里数值不稳。
                outX = inX + Math.Cos(heading) * length;
                outY = inY + Math.Sin(heading) * length;
            }
        }

        writer.WriteStartElement("road");
        writer.WriteAttributeString("id", ConnectingRoadId(graph, turn).ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("name", "turn_" + turn.Id.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("length", F(length));
        writer.WriteAttributeString("junction", turn.NodeId.ToString(CultureInfo.InvariantCulture));

        writer.WriteStartElement("link");
        writer.WriteStartElement("predecessor");
        writer.WriteAttributeString("elementType", "road");
        writer.WriteAttributeString("elementId", turn.Src.DirectionalRoadId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("contactPoint", "end");
        writer.WriteEndElement();
        writer.WriteStartElement("successor");
        writer.WriteAttributeString("elementType", "road");
        writer.WriteAttributeString("elementId", turn.Dst.DirectionalRoadId.ToString(CultureInfo.InvariantCulture));
        writer.WriteAttributeString("contactPoint", "start");
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("planView");
        writer.WriteStartElement("geometry");
        writer.WriteAttributeString("s", "0");
        writer.WriteAttributeString("x", F(inX));
        writer.WriteAttributeString("y", F(inY));
        writer.WriteAttributeString("hdg", F(heading));
        writer.WriteAttributeString("length", F(length));
        if (curved)
        {
            writer.WriteStartElement("paramPoly3");
            writer.WriteAttributeString("aU", F(aU));
            writer.WriteAttributeString("bU", F(bU));
            writer.WriteAttributeString("cU", F(cU));
            writer.WriteAttributeString("dU", F(dU));
            writer.WriteAttributeString("aV", F(aV));
            writer.WriteAttributeString("bV", F(bV));
            writer.WriteAttributeString("cV", F(cV));
            writer.WriteAttributeString("dV", F(dV));
            writer.WriteAttributeString("pRange", "normalized");
            writer.WriteEndElement();
        }
        else
        {
            writer.WriteElementString("line", string.Empty);
        }

        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("elevationProfile");
        writer.WriteStartElement("elevation");
        writer.WriteAttributeString("s", "0");
        writer.WriteAttributeString("a", "0");
        writer.WriteAttributeString("b", "0");
        writer.WriteAttributeString("c", "0");
        writer.WriteAttributeString("d", "0");
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteStartElement("lanes");
        writer.WriteStartElement("laneSection");
        writer.WriteAttributeString("s", "0");
        writer.WriteElementString("center", string.Empty);
        writer.WriteStartElement("right");
        writer.WriteStartElement("lane");
        writer.WriteAttributeString("id", "-1");
        writer.WriteAttributeString("type", MapLaneType(TurnLaneType(turn)));
        writer.WriteAttributeString("level", "false");
        writer.WriteStartElement("link");
        writer.WriteStartElement("predecessor");
        writer.WriteAttributeString("id", turn.Src.OdrLaneId.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
        writer.WriteStartElement("successor");
        writer.WriteAttributeString("id", turn.Dst.OdrLaneId.ToString(CultureInfo.InvariantCulture));
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteStartElement("width");
        writer.WriteAttributeString("sOffset", "0");
        writer.WriteAttributeString("a", F(graph.LaneWidthM));
        writer.WriteAttributeString("b", "0");
        writer.WriteAttributeString("c", "0");
        writer.WriteAttributeString("d", "0");
        writer.WriteEndElement();
        writer.WriteStartElement("speed");
        writer.WriteAttributeString("sOffset", "0");
        writer.WriteAttributeString("max", F(Math.Max(0.1, turn.Src.SpeedLimit)));
        writer.WriteAttributeString("unit", "m/s");
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();

        writer.WriteEndElement();
    }

    private static int ConnectingRoadId(ExportGraph graph, ExportTurn turn)
        => graph.ConnectingRoadBase + turn.Id;

    private static string MapLaneType(LaneType type)
    {
        return type switch
        {
            LaneType.Driving => "driving",
            LaneType.SharedLeftTurn => "driving",
            LaneType.Bus => "bus",
            LaneType.Biking => "biking",
            LaneType.Sidewalk => "sidewalk",
            LaneType.Parking => "parking",
            LaneType.Construction => "roadWorks",
            _ => "driving",
        };
    }

    private static LaneType TurnLaneType(ExportTurn turn)
    {
        if (turn.Src.Type == turn.Dst.Type)
        {
            return turn.Src.Type;
        }

        return turn.IsMotor ? LaneType.Driving : turn.Src.Type;
    }

    private static string F(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static double Dot(double ax, double ay, double bx, double by)
        => ax * bx + ay * by;

    private static double BezierLength(
        double x0, double y0,
        double x1, double y1,
        double x2, double y2,
        double x3, double y3)
    {
        const int samples = 24;
        double length = 0.0;
        double px = x0;
        double py = y0;
        for (int i = 1; i <= samples; i++)
        {
            double t = (double)i / samples;
            double mt = 1.0 - t;
            double a = mt * mt * mt;
            double b = 3.0 * mt * mt * t;
            double c = 3.0 * mt * t * t;
            double d = t * t * t;
            double x = a * x0 + b * x1 + c * x2 + d * x3;
            double y = a * y0 + b * y1 + c * y2 + d * y3;
            double dx = x - px;
            double dy = y - py;
            length += Math.Sqrt(dx * dx + dy * dy);
            px = x;
            py = y;
        }

        return length;
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
