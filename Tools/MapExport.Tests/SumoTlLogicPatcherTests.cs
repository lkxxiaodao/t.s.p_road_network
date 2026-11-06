using System.Globalization;
using System.Xml.Linq;
using SignalPlanner.MapExport.Sumo;
using SimCore.Io;
using Xunit;

namespace SignalPlanner.MapExport.Tests;

public sealed class SumoTlLogicPatcherTests
{
    [Fact]
    public void RewritesTlLogicFromSignalPlan()
    {
        Level level = Spmap.Load(TestPaths.Example);
        ExportGraph graph = ExportGraph.Build(level, 3.5);
        string dir = TempDir.Create();
        try
        {
            string path = Path.Combine(dir, "synthetic.net.xml");
            WriteSyntheticNet(path, graph);

            IReadOnlyList<string> warnings = SumoTlLogicPatcher.Patch(
                path,
                graph,
                new MapExportOptions { YellowDuration = 3.0, AllRedDuration = 2.0 });
            Assert.Empty(warnings);

            XDocument doc = XDocument.Load(path);
            XElement tlLogic = Assert.Single(doc.Root!.Elements("tlLogic"));
            List<XElement> phases = tlLogic.Elements("phase").ToList();
            Assert.Equal(6, phases.Count);

            string first = (string?)phases[0].Attribute("state") ?? string.Empty;
            Assert.Equal(6, first.Length);
            Assert.Equal('G', first[0]);
            Assert.Equal('r', first[1]);
            Assert.Equal('G', first[2]);
            Assert.Equal('G', first[4]);
            Assert.DoesNotContain('g', first);
            Assert.Equal("30", (string?)phases[0].Attribute("duration"));

            string yellow = (string?)phases[1].Attribute("state") ?? string.Empty;
            Assert.Equal('y', yellow[0]);
            Assert.Equal('y', yellow[2]);
            Assert.Equal('r', yellow[1]);

            string allRed = (string?)phases[2].Attribute("state") ?? string.Empty;
            Assert.DoesNotContain('G', allRed);
            Assert.DoesNotContain('g', allRed);
            Assert.DoesNotContain('y', allRed);
            Assert.Equal(6, allRed.Length);
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    private static void WriteSyntheticNet(string path, ExportGraph graph)
    {
        var root = new XElement("net");
        foreach (ExportLane lane in graph.Lanes.Values.OrderBy(l => l.Id))
        {
            string edgeId = "e" + lane.Id.ToString(CultureInfo.InvariantCulture);
            root.Add(new XElement("edge",
                new XAttribute("id", edgeId),
                new XElement("lane",
                    new XAttribute("id", edgeId + "_0"),
                    new XElement("param",
                        new XAttribute("key", "origID"),
                        new XAttribute("value",
                            lane.DirectionalRoadId.ToString(CultureInfo.InvariantCulture) + " " +
                            lane.OdrLaneId.ToString(CultureInfo.InvariantCulture))))));
        }

        foreach (ExportTurn turn in graph.Turns.Where(t => !t.IsCrosswalk).OrderBy(t => t.Id))
        {
            root.Add(new XElement("connection",
                new XAttribute("from", "e" + turn.Src.Id.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("fromLane", "0"),
                new XAttribute("to", "e" + turn.Dst.Id.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("toLane", "0"),
                new XAttribute("tl", turn.NodeId.ToString(CultureInfo.InvariantCulture)),
                new XAttribute("linkIndex", turn.Id.ToString(CultureInfo.InvariantCulture))));
        }

        root.Add(new XElement("tlLogic",
            new XAttribute("id", "0"),
            new XAttribute("type", "static"),
            new XAttribute("programID", "0"),
            new XElement("phase", new XAttribute("duration", "60"), new XAttribute("state", "GGGGGG"))));

        new XDocument(root).Save(path);
    }
}
