using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using SignalPlanner.MapExport;
using SignalPlanner.MapExport.Sumo;

/// <summary>
/// GDScript 调用外部格式导出的桥。挂在 Main.tscn 的 MapExportBridge 节点上。
/// 所有方法都返回 JSON 字符串，异常不抛给 GDScript。
/// </summary>
public partial class MapExportBridge : Node
{
    private static readonly JsonSerializerOptions InputOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>把一份已校验的 .spmap 导出成选中的外部格式。</summary>
    public string ExportFormats(string spmapPath, string outputBase, string optionsJson)
    {
        try
        {
            MapExportOptions? options = JsonSerializer.Deserialize<MapExportOptions>(optionsJson, InputOptions);
            if (options == null)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = "导出选项不是有效 JSON 对象。",
                    results = Array.Empty<object>(),
                }, OutputOptions);
            }

            ExportSummary summary = MapExporter.Run(spmapPath, options);
            return JsonSerializer.Serialize(summary, OutputOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = ex.Message,
                results = Array.Empty<object>(),
            }, OutputOptions);
        }
    }

    /// <summary>探测 netconvert：返回 {ok, path, version, error}。</summary>
    public string DetectNetconvert(string hintPath, string searchDir)
    {
        try
        {
            string? exe = NetconvertRunner.Find(hintPath, searchDir);
            if (exe == null)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = "未检测到 netconvert。",
                    path = string.Empty,
                    version = string.Empty,
                }, OutputOptions);
            }

            string version = NetconvertRunner.ReadVersion(exe);
            return JsonSerializer.Serialize(new
            {
                ok = true,
                error = string.Empty,
                path = exe,
                version,
            }, OutputOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = ex.Message,
                path = string.Empty,
                version = string.Empty,
            }, OutputOptions);
        }
    }

    // ============================================================
    // SUMO 探针（专业模式的地基）
    // ============================================================

    // 探针要跑一次 netconvert（示例关卡约 0.3 s，大网更久），绝不能占住主线程。
    // 所以做成"启动 → GDScript 每帧轮询"，与面板的"占位 → 算完就地填"配合（README 铁律 25）。
    private readonly object _probeLock = new();
    private string _probeJob = string.Empty;
    private volatile string _probeState = "idle";     // idle / running / done
    private string _probeResult = string.Empty;

    /// <summary>
    /// 启动探针（立即返回，不阻塞）。
    /// 返回 {ok, error, job}；job 交给 <see cref="ProbeSumoPoll"/> 轮询。
    /// </summary>
    public string ProbeSumo(string spmapPath, string optionsJson)
    {
        MapExportOptions options;
        try
        {
            options = JsonSerializer.Deserialize<MapExportOptions>(optionsJson, InputOptions)
                      ?? new MapExportOptions();
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "探针选项不是有效 JSON：" + ex.Message,
                job = string.Empty,
            }, OutputOptions);
        }

        string job = Guid.NewGuid().ToString("N");
        lock (_probeLock)
        {
            _probeJob = job;
            _probeState = "running";
            _probeResult = string.Empty;
        }

        string path = spmapPath;
        Task.Run(() =>
        {
            string json;
            try
            {
                json = JsonSerializer.Serialize(SumoNetProbe.Probe(path, options), OutputOptions);
            }
            catch (Exception ex)
            {
                json = JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = ex.Message,
                }, OutputOptions);
            }

            lock (_probeLock)
            {
                _probeResult = json;
                _probeState = "done";
            }
        });

        return JsonSerializer.Serialize(new { ok = true, error = string.Empty, job }, OutputOptions);
    }

    /// <summary>
    /// 轮询探针。返回 {state: running|done|error, error, json}。
    /// ★ json 只在 state=done 时才有意义；job 对不上（比如已经发起了新的一次）一律按 error 处理，
    ///   避免把上一次的结果当成这一次的。
    /// </summary>
    public string ProbeSumoPoll(string jobId)
    {
        lock (_probeLock)
        {
            if (!string.Equals(jobId, _probeJob, StringComparison.Ordinal))
            {
                return JsonSerializer.Serialize(new
                {
                    state = "error",
                    error = "探针任务已失效（可能发起了新的一次）。",
                    json = string.Empty,
                }, OutputOptions);
            }

            return JsonSerializer.Serialize(new
            {
                state = _probeState,
                error = string.Empty,
                json = _probeState == "done" ? _probeResult : string.Empty,
            }, OutputOptions);
        }
    }

    // ============================================================
    // 试跑（S5）：跑一次 sumo -c 出统计 / 开 sumo-gui
    // ============================================================

    // 大网一次几秒到几十秒 → 与探针同一套"启动 → 每帧轮询"，绝不占主线程。
    private readonly object _sumoLock = new();
    private string _sumoJob = string.Empty;
    private volatile string _sumoState = "idle";
    private string _sumoResult = string.Empty;

    /// <summary>
    /// 启动一次试跑（立即返回）。返回 {ok, error, job}。
    /// </summary>
    /// <param name="cfgPath">.sumocfg 的绝对路径（相对路径文件名写在它里面）。</param>
    /// <param name="exePath">sumo.exe 路径；空则自动查找。</param>
    /// <param name="sumoHome">SUMO_HOME；空则读环境变量。</param>
    /// <param name="searchDir">额外搜索目录（Tools/sumo/bin）。</param>
    public string RunSumo(string cfgPath, string exePath, string sumoHome, string searchDir)
    {
        string exe = exePath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            exe = SumoRunner.Find(string.Empty, sumoHome ?? string.Empty, searchDir ?? string.Empty, "sumo")
                  ?? string.Empty;
        }

        if (exe.Length == 0)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = "未检测到 sumo。请安装 SUMO 或在导出面板指定路径。",
                job = string.Empty,
            }, OutputOptions);
        }

        string job = Guid.NewGuid().ToString("N");
        lock (_sumoLock)
        {
            _sumoJob = job;
            _sumoState = "running";
            _sumoResult = string.Empty;
        }

        Task.Run(() =>
        {
            string json;
            try
            {
                SumoRunResult run = SumoRunner.Run(exe, cfgPath);
                json = JsonSerializer.Serialize(new
                {
                    ok = run.Ok,
                    error = run.Error,
                    timed_out = run.TimedOut,
                    exit_code = run.ExitCode,
                    exe = exe,
                    cfg = cfgPath,
                    stats = run.Stats,
                    // 只回最后 40 行：完整日志可能上千行，面板显示不了，排查也够用
                    tail = Tail(run.StdOut + run.StdErr, 40),
                }, OutputOptions);
            }
            catch (Exception ex)
            {
                json = JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = ex.Message,
                }, OutputOptions);
            }

            lock (_sumoLock)
            {
                _sumoResult = json;
                _sumoState = "done";
            }
        });

        return JsonSerializer.Serialize(new { ok = true, error = string.Empty, job }, OutputOptions);
    }

    /// <summary>轮询试跑。返回 {state: running|done|error, error, json}。</summary>
    public string PollSumo(string jobId)
    {
        lock (_sumoLock)
        {
            if (!string.Equals(jobId, _sumoJob, StringComparison.Ordinal))
            {
                return JsonSerializer.Serialize(new
                {
                    state = "error",
                    error = "试跑任务已失效（可能发起了新的一次）。",
                    json = string.Empty,
                }, OutputOptions);
            }

            return JsonSerializer.Serialize(new
            {
                state = _sumoState,
                error = string.Empty,
                json = _sumoState == "done" ? _sumoResult : string.Empty,
            }, OutputOptions);
        }
    }

    /// <summary>开 sumo-gui（立即返回，不等待窗口关闭）。</summary>
    public string OpenSumoGui(string cfgPath, string exePath, string sumoHome, string searchDir)
    {
        try
        {
            string exe = exePath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                exe = SumoRunner.Find(string.Empty, sumoHome ?? string.Empty,
                          searchDir ?? string.Empty, "sumo-gui")
                      ?? string.Empty;
            }

            if (exe.Length == 0)
            {
                return JsonSerializer.Serialize(new
                {
                    ok = false,
                    error = "未检测到 sumo-gui。请安装 SUMO 或在导出面板指定路径。",
                    exe = string.Empty,
                }, OutputOptions);
            }

            (bool ok, string error) = SumoRunner.OpenGui(exe, cfgPath);
            return JsonSerializer.Serialize(new { ok, error, exe }, OutputOptions);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                ok = false,
                error = ex.Message,
                exe = string.Empty,
            }, OutputOptions);
        }
    }

    /// <summary>取文本最后 <paramref name="lines"/> 行。</summary>
    private static string Tail(string text, int lines)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string[] all = text.Replace("\r\n", "\n").Split('\n');
        int start = Math.Max(0, all.Length - lines);
        return string.Join("\n", all, start, all.Length - start).Trim();
    }
}
