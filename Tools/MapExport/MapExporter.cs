using System.Text;
using SignalPlanner.MapExport.Geo;
using SignalPlanner.MapExport.OpenDrive;
using SignalPlanner.MapExport.Shapefile;
using SignalPlanner.MapExport.Sumo;
using SimCore.Io;

namespace SignalPlanner.MapExport;

/// <summary>
/// 多格式导出的总入口：读取一份已校验的 .spmap，按选项写 shp / net.xml。
/// 每个格式独立成败，互不影响。
/// OpenDRIVE 只作为 net.xml 的内部中间文件，不对外提供。
/// </summary>
public static class MapExporter
{
    public static ExportSummary Run(string spmapPath, MapExportOptions options)
    {
        if (string.IsNullOrWhiteSpace(spmapPath) || !File.Exists(spmapPath))
        {
            return new ExportSummary { Ok = false, Error = "找不到已校验的 .spmap 文件：" + spmapPath };
        }

        if (string.IsNullOrWhiteSpace(options.OutputBase))
        {
            return new ExportSummary { Ok = false, Error = "输出路径为空。" };
        }

        string outputBase = Path.GetFullPath(options.OutputBase);
        string? directory = Path.GetDirectoryName(outputBase);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return new ExportSummary { Ok = false, Error = "输出目录无效。" };
        }

        Directory.CreateDirectory(directory);

        Level level;
        try
        {
            level = Spmap.Load(spmapPath);
        }
        catch (Exception ex)
        {
            return new ExportSummary { Ok = false, Error = "读取 .spmap 失败：" + ex.Message };
        }

        ExportGraph graph;
        try
        {
            graph = ExportGraph.Build(level, options.LaneWidthM);
        }
        catch (Exception ex)
        {
            return new ExportSummary { Ok = false, Error = "构建导出路网失败：" + ex.Message };
        }

        GeoProjection? geo = null;
        if (options.HasGeoOrigin)
        {
            try
            {
                geo = GeoProjection.Create(options.OriginLon!.Value, options.OriginLat!.Value, options.Projection);
            }
            catch (Exception ex)
            {
                return new ExportSummary { Ok = false, Error = "地理原点无效：" + ex.Message };
            }
        }

        var results = new List<ExportResult>();
        IReadOnlyList<SumoCheck.Finding> sumoFindings = Array.Empty<SumoCheck.Finding>();
        string? tempXodr = null;

        // OpenDRIVE 只作为 net.xml 的内部中间文件，不再对外导出。
        if (options.ExportNetXml)
        {
            tempXodr = outputBase + ".net.tmp.xodr";
            try
            {
                OpenDriveExporter.Write(graph, options, geo, tempXodr);
            }
            catch
            {
                // 交给下面的 net 分支报错（会提示缺少中间文件）。
                tempXodr = null;
            }
        }

        if (options.ExportShapefile)
        {
            if (geo == null)
            {
                results.Add(ExportResult.Failure("shp", "SHP 需要经纬度原点。"));
            }
            else
            {
                try
                {
                    ShapefileExporter.Write(graph, options, geo, outputBase);
                    results.Add(ExportResult.Success("shp", outputBase + "_roads.shp"));
                }
                catch (Exception ex)
                {
                    results.Add(ExportResult.Failure("shp", ex.Message));
                }
            }
        }

        if (options.ExportNetXml)
        {
            string netPath = outputBase + ".net.xml";
            string? exe = NetconvertRunner.Find(options.NetconvertPath, options.NetconvertSearchDir);
            if (exe == null)
            {
                results.Add(ExportResult.Failure("net", "未检测到 netconvert。请安装 SUMO 或在导出面板指定路径。"));
            }
            else if (tempXodr == null || !File.Exists(tempXodr))
            {
                results.Add(ExportResult.Failure("net", "缺少 OpenDRIVE 中间文件，无法生成 net.xml。"));
            }
            else
            {
                List<string> typeFiles = BuildTypeFiles(exe, outputBase, out string? extraTypeFile, out string? typeWarning);
                // ★ S4：netconvert 的 4 个常用开关来自专业档。没有专业档时全是默认值
                //   → 参数表与 2026-11 之前逐字节一致（见 SumoNetGenOptions）。
                SumoNetGenOptions gen = SumoNetGenOptions.FromProfile(options.SumoProfileJson);
                try
                {
                    NetconvertRunResult run = NetconvertRunner.Run(
                        exe, tempXodr, netPath, keepOriginalNames: true, typeFiles, gen);
                    if (!run.Ok)
                    {
                        string detail = run.TimedOut
                            ? run.StdErr
                            : FirstNonEmpty(run.StdErr, run.StdOut, "netconvert 退出码 " + run.ExitCode);
                        results.Add(ExportResult.Failure("net", detail));
                    }
                    else
                    {
                        var warnings = new List<string>();
                        if (!string.IsNullOrWhiteSpace(typeWarning))
                        {
                            warnings.Add(typeWarning);
                        }

                        // ★ S4：网络覆盖层（路口控制方式 / 车道属性 / 连接属性）。
                        //   **必须在配时回写之前**：TlLogicPatcher 要按"最终的 link 表"生成 state 串。
                        //   没有 overrides 时它一个字节都不改（基础模式产物逐字节不变）。
                        try
                        {
                            warnings.AddRange(SumoNetOverride.Apply(netPath, graph, options.SumoProfileJson));
                        }
                        catch (Exception ex)
                        {
                            warnings.Add("net.xml 已生成，但网络覆盖层应用失败：" + ex.Message);
                        }

                        try
                        {
                            warnings.AddRange(SumoTlLogicPatcher.Patch(netPath, graph, options));
                        }
                        catch (Exception ex)
                        {
                            warnings.Add("net.xml 已生成，但信号配时回写失败：" + ex.Message);
                        }
                        // ★ 专业档 → .tll.xml（additional file）。
                        //   2026-11-04 起**只有多套方案**才写这里；单套方案已经由
                        //   SumoTlLogicPatcher 直接写进 net.xml 的 programID="0"，
                        //   否则 netedit 打开只看得见网络自带那套，用户找不到自己的配时。
                        string tllPath = outputBase + ".tll.xml";
                        try
                        {
                            IReadOnlyList<string>? tllWarnings =
                                SumoTllWriter.Write(netPath, graph, options.SumoProfileJson, tllPath);
                            if (tllWarnings != null)
                            {
                                var tllNotes = new List<string>(tllWarnings);
                                if (File.Exists(tllPath))
                                {
                                    tllNotes.Add("专业配时已写入 " + Path.GetFileName(tllPath)
                                        + "：在 sumo 里用 --additional-files 加它才会生效。");
                                    results.Add(ExportResult.Success("tll", tllPath, tllNotes));
                                }
                                else
                                {
                                    results.Add(ExportResult.Failure("tll", string.Join("；", tllNotes)));
                                }
                            }
                            else if (File.Exists(tllPath))
                            {
                                // 上一次导出留下的旧文件：单套方案现在写进 net.xml，它不再需要。
                                // 不自动删（用户可能在 netedit 里改过），只提醒别引用。
                                warnings.Add("目录里还有旧文件 " + Path.GetFileName(tllPath)
                                    + "：专业配时已写进 net.xml，它不再需要，别在 sumocfg 里引用"
                                    + "（否则 netedit 里会看到两套程序）。");
                            }
                        }
                        catch (Exception ex)
                        {
                            results.Add(ExportResult.Failure("tll", ".tll.xml 写入失败：" + ex.Message));
                        }

                        // ★ 车型 / 车流 / 行人 → .rou.xml（S2）。
                        //   必须在 net 成功之后：路线要用 net.xml 的 origID 把车道映射回边。
                        if (options.ExportRou)
                        {
                            string rouPath = outputBase + ".rou.xml";
                            try
                            {
                                SumoRouWriter.Result? rou =
                                    SumoRouWriter.Write(netPath, graph, level, options.SumoProfileJson, rouPath);
                                if (rou == null)
                                {
                                    // 没有任何需求曲线 → 不产出空文件，也不占一行结果，只在 net 上提示一句
                                    warnings.Add("这张图没有任何需求曲线，未生成 .rou.xml（要车流请先画需求或配专业档）。");
                                }
                                else if (!rou.Ok)
                                {
                                    results.Add(ExportResult.Failure("rou", rou.Error));
                                }
                                else
                                {
                                    var rouNotes = new List<string>(rou.Warnings)
                                    {
                                        $"车流 {rou.FlowCount} 条 / 路线 {rou.RouteCount} 条 / 行人 {rou.PersonFlowCount} 条"
                                        + $"；理论生成车辆 {rou.ExpectedVehicles:0} 辆"
                                        + (rou.ExpectedPersons > 0 ? $"、行人 {rou.ExpectedPersons:0} 人次" : "")
                                        + "。",
                                    };
                                    results.Add(ExportResult.Success("rou", rouPath, rouNotes));
                                }
                            }
                            catch (Exception ex)
                            {
                                results.Add(ExportResult.Failure("rou", ".rou.xml 写入失败：" + ex.Message));
                            }
                        }

                        // ★ 检测设施 → .add.xml（S3）。同样必须在 net 之后：
                        //   站台/检测器的 lane 要用 net.xml 的 origID 映射，长度也要用 net 的。
                        if (options.ExportAdditional)
                        {
                            string addPath = outputBase + ".add.xml";
                            try
                            {
                                SumoAddWriter.Result? add = SumoAddWriter.Write(
                                    netPath, graph, options.SumoProfileJson, addPath);
                                if (add == null)
                                {
                                    // 没有任何设施 → 不产空文件，也不打扰用户
                                }
                                else if (!add.Ok)
                                {
                                    results.Add(ExportResult.Failure("add", add.Error));
                                }
                                else
                                {
                                    var parts = new List<string>();
                                    if (add.DetectorCount > 0)
                                    {
                                        parts.Add($"检测器 {add.DetectorCount} 个 → {add.DetectorOutput}");
                                    }

                                    if (add.BusStopCount > 0) parts.Add($"公交站 {add.BusStopCount} 个");
                                    if (add.ParkingCount > 0) parts.Add($"停车场 {add.ParkingCount} 个");

                                    var addNotes = new List<string>(add.Warnings)
                                    {
                                        string.Join(" / ", parts) + "。用 --additional-files 加载它。",
                                    };
                                    results.Add(ExportResult.Success("add", addPath, addNotes));
                                }
                            }
                            catch (Exception ex)
                            {
                                results.Add(ExportResult.Failure("add", ".add.xml 写入失败：" + ex.Message));
                            }
                        }

                        // ★ 运行配置 → .sumocfg（S5）。放在最后：要检查 net / tll / add
                        //   是否真的存在，只挂真实存在的附加文件，全部相对路径。
                        if (options.ExportSumoCfg)
                        {
                            try
                            {
                                SumoCfgWriter.Result cfg = SumoCfgWriter.Write(
                                    outputBase, options.SumoProfileJson);
                                if (!cfg.Ok)
                                {
                                    results.Add(ExportResult.Failure("sumocfg", cfg.Error));
                                }
                                else
                                {
                                    results.Add(ExportResult.Success("sumocfg", cfg.Path, cfg.Warnings));
                                }
                            }
                            catch (Exception ex)
                            {
                                results.Add(ExportResult.Failure("sumocfg", ".sumocfg 写入失败：" + ex.Message));
                            }
                        }

                        // ★ S6：专业档体检 —— 读**刚写出的产物**（link 表 / 车道长度 / 冲突矩阵
                        //   只有到这里才有）。导出前那批纯数据规则在 GDScript 侧（SumoCheckModel）。
                        if (!string.IsNullOrWhiteSpace(options.SumoProfileJson))
                        {
                            try
                            {
                                sumoFindings = SumoCheck.Run(
                                    outputBase, graph, level, options.SumoProfileJson);
                                int errCount = sumoFindings.Count(
                                    f => f.Severity == SumoCheck.SevError);
                                if (errCount > 0)
                                {
                                    results.Add(ExportResult.Failure("sumo",
                                        $"{errCount} 条专业档错误（文件已生成，请按提示修正后重新导出）"));
                                }
                                else if (sumoFindings.Count > 0)
                                {
                                    warnings.Add($"{sumoFindings.Count} 条专业档提示，见「校验问题」清单。");
                                }
                            }
                            catch (Exception ex)
                            {
                                warnings.Add("专业档体检失败（不影响产物）：" + ex.Message);
                            }
                        }

                        results.Add(ExportResult.Success("net", netPath, warnings));
                    }
                }
                finally
                {
                    if (extraTypeFile != null)
                    {
                        TryDelete(extraTypeFile);
                    }
                }
            }
        }

        if (tempXodr != null)
        {
            TryDelete(tempXodr);
        }

        return new ExportSummary
        {
            Ok = results.All(r => r.Ok),
            Results = results,
            SumoFindings = sumoFindings,
        };
    }

    private const string ExtraTypemapXml = """
        <types xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xsi:noNamespaceSchemaLocation="http://sumo.dlr.de/xsd/types_file.xsd">
          <type id="biking"   priority="-1" speed="5.56"  width="1.50" allow="bicycle"/>
          <type id="sidewalk" priority="-2" speed="2.78"  width="2.50" allow="pedestrian"/>
          <type id="bus"      priority="1"  speed="13.89" width="3.65" allow="bus"/>
        </types>
        """;

    /// <summary>netconvert 的 typemap 列表。★ internal：<see cref="Sumo.SumoNetProbe"/> 也要用同一套，
    /// 否则"探针看到的车道类型"和"真正导出的车道类型"会不一致。</summary>
    internal static List<string> BuildTypeFiles(
        string exe,
        string outputBase,
        out string? extraTypeFile,
        out string? warning)
    {
        extraTypeFile = null;
        warning = null;

        string home = NetconvertRunner.ResolveSumoHome(exe);
        string defaultTypemap = Path.Combine(home, "data", "typemap", "opendriveNetconvert.typ.xml");
        if (!File.Exists(defaultTypemap))
        {
            warning = "未找到 SUMO typemap；自行车道 / 人行道 / 公交专用道不会写入 net.xml。";
            return new List<string>();
        }

        string extra = outputBase + ".extra.typ.xml";
        try
        {
            File.WriteAllText(extra, ExtraTypemapXml, new UTF8Encoding(false));
            extraTypeFile = extra;
            return new List<string> { defaultTypemap, extra };
        }
        catch (Exception ex)
        {
            warning = "无法写入自行车/人行道/公交 typemap：" + ex.Message;
            return new List<string> { defaultTypemap };
        }
    }

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
            // 清理失败不影响主结果。
        }
    }
}
