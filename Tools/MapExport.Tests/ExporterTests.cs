using System.Xml.Linq;
using SignalPlanner.MapExport;
using SignalPlanner.MapExport.Geo;
using SignalPlanner.MapExport.OpenDrive;
using SignalPlanner.MapExport.Shapefile;
using SimCore.Io;
using Xunit;

namespace SignalPlanner.MapExport.Tests;

internal static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string Example => Path.Combine(RepoRoot, "docs", "examples", "01-1-first-crossing.spmap");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docs", "examples", "01-1-first-crossing.spmap")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录（docs/examples/01-1-first-crossing.spmap）。");
    }
}

internal static class TempDir
{
    public static string Create()
    {
        string path = Path.Combine(Path.GetTempPath(), "spmap-export-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // 测试清理失败不影响结果。
        }
    }
}

public sealed class OpenDriveExporterTests
{
    [Fact]
    public void WritesExpectedRoadJunctionAndConnectionCounts()
    {
        Level level = Spmap.Load(TestPaths.Example);
        ExportGraph graph = ExportGraph.Build(level, 3.5);
        string dir = TempDir.Create();
        try
        {
            string path = Path.Combine(dir, "map.xodr");
            MapExportOptions options = new() { OutputBase = Path.Combine(dir, "map") };
            GeoProjection geo = GeoProjection.Create(116.397, 39.909, GeoProjectionKind.Utm);
            OpenDriveExporter.Write(graph, options, geo, path);

            string text = File.ReadAllText(path);
            Assert.DoesNotContain("NaN", text);
            Assert.DoesNotContain("Infinity", text);

            XDocument doc = XDocument.Load(path);
            XElement root = Assert.IsType<XElement>(doc.Root);
            Assert.Equal("OpenDRIVE", root.Name.LocalName);

            int normalRoads = root.Elements("road").Count(r => (string?)r.Attribute("junction") == "-1");
            int connectingRoads = root.Elements("road").Count(r => (string?)r.Attribute("junction") != "-1");
            int exportedTurns = graph.Turns.Count(t => t.ShouldExport);
            int expectedNormalRoads = graph.Roads.Values.Sum(r =>
                (r.ForwardLanes.Count > 0 ? 1 : 0) + (r.BackwardLanes.Count > 0 ? 1 : 0));
            Assert.Equal(expectedNormalRoads, normalRoads);
            Assert.Equal(exportedTurns, connectingRoads);

            int connections = root.Elements("junction").Sum(j => j.Elements("connection").Count());
            Assert.Equal(exportedTurns, connections);

            var laneTypes = root.Descendants("lane")
                .Select(l => (string?)l.Attribute("type"))
                .Where(t => t != null)
                .ToHashSet(StringComparer.Ordinal);
            Assert.Contains("biking", laneTypes);
            Assert.Contains("sidewalk", laneTypes);

            foreach (XElement width in root.Descendants("width"))
            {
                Assert.Equal(3.5, double.Parse((string?)width.Attribute("a") ?? "0",
                    System.Globalization.CultureInfo.InvariantCulture), 3);
            }

            Assert.Contains(root.Elements("controller"), c => c.Elements("control").Any());

            // OpenDRIVE 1.4 要求 <signal> 带 orientation；netconvert 缺这个属性会直接报错退出。
            foreach (XElement signal in root.Descendants("signal"))
            {
                string? orientation = (string?)signal.Attribute("orientation");
                Assert.True(orientation is "+" or "-", "signal 缺少合法 orientation：" + signal);
                Assert.NotNull(signal.Attribute("pitch"));
                Assert.NotNull(signal.Attribute("roll"));
            }

            foreach (XElement control in root.Descendants("control"))
            {
                Assert.NotNull(control.Attribute("type"));
            }
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void ExportIsDeterministic()
    {
        Level level = Spmap.Load(TestPaths.Example);
        ExportGraph graph = ExportGraph.Build(level, 3.5);
        string dir = TempDir.Create();
        try
        {
            MapExportOptions options = new() { OutputBase = Path.Combine(dir, "map") };
            GeoProjection geo = GeoProjection.Create(116.397, 39.909, GeoProjectionKind.Utm);
            string a = Path.Combine(dir, "a.xodr");
            string b = Path.Combine(dir, "b.xodr");
            OpenDriveExporter.Write(graph, options, geo, a);
            OpenDriveExporter.Write(graph, options, geo, b);
            Assert.Equal(File.ReadAllBytes(a), File.ReadAllBytes(b));
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }
}

public sealed class ShapefileExporterTests
{
    [Fact]
    public void WritesReadableLayers()
    {
        Level level = Spmap.Load(TestPaths.Example);
        ExportGraph graph = ExportGraph.Build(level, 3.5);
        string dir = TempDir.Create();
        try
        {
            string basePath = Path.Combine(dir, "map");
            MapExportOptions options = new() { OutputBase = basePath, IncludeCrosswalks = true };
            GeoProjection geo = GeoProjection.Create(116.397, 39.909, GeoProjectionKind.Utm);
            ShapefileExporter.Write(graph, options, geo, basePath);

            string roads = basePath + "_roads.shp";
            string lanes = basePath + "_lanes.shp";
            string nodes = basePath + "_nodes.shp";
            Assert.True(File.Exists(roads));
            Assert.True(File.Exists(lanes));
            Assert.True(File.Exists(nodes));
            Assert.True(File.Exists(Path.ChangeExtension(roads, ".prj")));
            Assert.True(File.Exists(Path.ChangeExtension(roads, ".cpg")));

            Assert.Equal(graph.Roads.Count, NetTopologySuite.IO.Esri.Shapefile.ReadAllFeatures(roads).Count());
            Assert.Equal(graph.Lanes.Count, NetTopologySuite.IO.Esri.Shapefile.ReadAllFeatures(lanes).Count());
            Assert.Equal(graph.Nodes.Count, NetTopologySuite.IO.Esri.Shapefile.ReadAllFeatures(nodes).Count());
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }
}

public sealed class GeoProjectionTests
{
    [Fact]
    public void OriginRoundTripsThroughUtm()
    {
        GeoProjection geo = GeoProjection.Create(116.397, 39.909, GeoProjectionKind.Utm);
        (double lon, double lat) = geo.ToWgs84(0.0, 0.0);
        Assert.Equal(116.397, lon, 5);
        Assert.Equal(39.909, lat, 5);
        Assert.Equal(50, geo.UtmZone);
    }

    [Fact]
    public void OffsetPointStaysNearOrigin()
    {
        GeoProjection geo = GeoProjection.Create(116.397, 39.909, GeoProjectionKind.Utm);
        (double lon, double lat) = geo.ToWgs84(100.0, 0.0);
        Assert.True(lon > 116.397);
        Assert.InRange(lat, 39.90, 39.92);
    }
}

public sealed class MapExporterTests
{
    [Fact]
    public void ExportsShapefile()
    {
        string dir = TempDir.Create();
        try
        {
            string basePath = Path.Combine(dir, "map");
            var options = new MapExportOptions
            {
                ExportShapefile = true,
                OutputBase = basePath,
                OriginLon = 116.397,
                OriginLat = 39.909,
                Projection = GeoProjectionKind.Utm,
            };

            ExportSummary summary = MapExporter.Run(TestPaths.Example, options);
            Assert.True(summary.Ok);
            Assert.All(summary.Results, r => Assert.True(r.Ok, r.Error));
            Assert.True(File.Exists(basePath + "_roads.shp"));
            Assert.False(File.Exists(basePath + ".xodr"));
            Assert.False(File.Exists(basePath + ".osm"));
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void NetconvertMissingLeavesNoTempFiles()
    {
        if (SignalPlanner.MapExport.Sumo.NetconvertRunner.Find(string.Empty, string.Empty) != null)
        {
            return;
        }

        string dir = TempDir.Create();
        try
        {
            string basePath = Path.Combine(dir, "map");
            var options = new MapExportOptions
            {
                ExportNetXml = true,
                OutputBase = basePath,
                OriginLon = 116.397,
                OriginLat = 39.909,
                Projection = GeoProjectionKind.Utm,
            };

            ExportSummary summary = MapExporter.Run(TestPaths.Example, options);
            Assert.False(summary.Ok);
            ExportResult net = Assert.Single(summary.Results, r => r.Format == "net");
            Assert.False(net.Ok);
            Assert.Contains("未检测到 netconvert", net.Error);
            Assert.False(File.Exists(basePath + ".net.tmp.xodr"));
            Assert.False(File.Exists(basePath + ".xodr"));
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }
}

public sealed class NetconvertTests
{
    [Fact]
    public void ConvertsOpenDriveToNetXmlWhenAvailable()
    {
        string? exe = SignalPlanner.MapExport.Sumo.NetconvertRunner.Find(string.Empty, string.Empty);
        if (exe == null)
        {
            // xUnit 2.x 没有动态 Skip；未安装 SUMO 时直接返回，手工验证见方案 §9.2。
            return;
        }

        string dir = TempDir.Create();
        try
        {
            Level level = Spmap.Load(TestPaths.Example);
            ExportGraph graph = ExportGraph.Build(level, 3.5);
            var options = new MapExportOptions
            {
                ExportNetXml = true,
                OutputBase = Path.Combine(dir, "map"),
                NetconvertPath = exe,
            };
            ExportSummary summary = MapExporter.Run(TestPaths.Example, options);
            Assert.True(File.Exists(Path.Combine(dir, "map.net.xml")));
            Assert.All(summary.Results, r => Assert.True(r.Ok, r.Error));

            XDocument net = XDocument.Load(Path.Combine(dir, "map.net.xml"));
            bool hasBicycleLane = net.Descendants("lane")
                .Any(l => ((string?)l.Attribute("allow"))?.Split(' ').Contains("bicycle") == true);
            bool hasPedestrianLane = net.Descendants("lane")
                .Any(l => ((string?)l.Attribute("allow"))?.Split(' ').Contains("pedestrian") == true);
            Assert.True(hasBicycleLane, "net.xml 缺少 allow=bicycle 车道");
            Assert.True(hasPedestrianLane, "net.xml 缺少 allow=pedestrian 车道");
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }
}
