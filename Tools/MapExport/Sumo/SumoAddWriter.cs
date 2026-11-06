using System.Globalization;
using System.Xml.Linq;

namespace SignalPlanner.MapExport.Sumo;

/// <summary>
/// 把检测设施写成 SUMO 的 additional file（<c>&lt;base&gt;.add.xml</c>）。
///
/// <para><b>为什么是 additional 而不是 net.xml</b>：检测器 / 站台 / 停车区都是"附加设施"，
/// 写进 net.xml 会随 netconvert 重新生成而丢失，而且 net.xml 要保持"机器生成物"的纯净
/// （技术方案06 §4 D2）。</para>
///
/// <para><b>检测器统一输出到一个文件</b>（<c>&lt;base&gt;.det.xml</c>）：SUMO 的检测器必须带
/// <c>file</c> 属性才会写数据，让每个检测器各写一个文件会把目录炸开。相对路径 → 整个夹子可搬移。</para>
/// </summary>
public static class SumoAddWriter
{
    /// <summary>写文件的结果。</summary>
    public sealed class Result
    {
        public bool Ok { get; set; }
        public string Error { get; set; } = string.Empty;
        public List<string> Warnings { get; } = new();

        public int DetectorCount { get; set; }
        public int BusStopCount { get; set; }
        public int ParkingCount { get; set; }

        /// <summary>检测器输出文件名（相对 add.xml 所在目录），没有检测器时为 null。</summary>
        public string? DetectorOutput { get; set; }
    }

    /// <summary>
    /// 生成 <c>.add.xml</c>。<b>没有任何设施时返回 null</b>（不产出空文件）。
    /// </summary>
    /// <param name="netPath">刚生成的 net.xml（拿 lane → edge 映射）。</param>
    /// <param name="outPath">输出的 .add.xml 路径。</param>
    public static Result? Write(
        string netPath,
        ExportGraph graph,
        string? profileJson,
        string outPath)
    {
        SumoFacilities.Parsed parsed = SumoFacilities.Parse(netPath, graph, profileJson);
        var result = new Result();
        result.Warnings.AddRange(parsed.Warnings);

        if (parsed.All.Count == 0)
        {
            return result.Warnings.Count == 0 ? null : result;
        }

        // 检测器输出文件：与 add.xml 同目录、用输出基名，例如 01-1.det.xml
        string baseName = Path.GetFileNameWithoutExtension(outPath);
        if (baseName.EndsWith(".add", StringComparison.OrdinalIgnoreCase))
        {
            baseName = baseName[..^4];
        }

        string detFile = baseName + ".det.xml";
        bool anyDetector = false;

        var root = new XElement("additional");
        foreach (SumoFacilities.Facility f in parsed.All.OrderBy(x => x.Kind, StringComparer.Ordinal)
                     .ThenBy(x => x.Id, StringComparer.Ordinal))
        {
            switch (f.Kind)
            {
                case SumoFacilities.KindE1:
                    root.Add(new XElement("e1Detector",
                        new XAttribute("id", f.Id),
                        new XAttribute("lane", f.NetLane),
                        new XAttribute("pos", F(f.Pos)),
                        new XAttribute("freq", F(f.Freq)),
                        new XAttribute("file", detFile),
                        new XAttribute("friendlyPos", "true")));
                    result.DetectorCount++;
                    anyDetector = true;
                    break;

                case SumoFacilities.KindE2:
                    root.Add(new XElement("e2Detector",
                        new XAttribute("id", f.Id),
                        new XAttribute("lane", f.NetLane),
                        new XAttribute("pos", F(f.Pos)),
                        new XAttribute("length", F(f.Length)),
                        new XAttribute("freq", F(f.Freq)),
                        new XAttribute("file", detFile),
                        new XAttribute("friendlyPos", "true")));
                    result.DetectorCount++;
                    anyDetector = true;
                    break;

                case SumoFacilities.KindBusStop:
                    var stop = new XElement("busStop",
                        new XAttribute("id", f.Id),
                        new XAttribute("lane", f.NetLane),
                        new XAttribute("startPos", F(f.Start)),
                        new XAttribute("endPos", F(f.End)),
                        new XAttribute("lines", f.Lines),
                        new XAttribute("friendlyPos", "true"));
                    if (f.Name.Length > 0)
                    {
                        stop.SetAttributeValue("name", f.Name);
                    }

                    root.Add(stop);
                    result.BusStopCount++;
                    break;

                case SumoFacilities.KindParking:
                    var park = new XElement("parkingArea",
                        new XAttribute("id", f.Id),
                        new XAttribute("lane", f.NetLane),
                        new XAttribute("startPos", F(f.Start)),
                        new XAttribute("endPos", F(f.End)),
                        new XAttribute("roadsideCapacity", f.Capacity.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("friendlyPos", "true"));
                    if (f.Name.Length > 0)
                    {
                        park.SetAttributeValue("name", f.Name);
                    }

                    root.Add(park);
                    result.ParkingCount++;
                    break;
            }
        }

        if (anyDetector)
        {
            result.DetectorOutput = detFile;
        }

        try
        {
            var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
            doc.Save(outPath);
        }
        catch (Exception ex)
        {
            result.Error = ".add.xml 写入失败：" + ex.Message;
            return result;
        }

        result.Ok = true;
        return result;
    }

    private static string F(double value) =>
        Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);
}
