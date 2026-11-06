using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SignalPlanner.MapExport.Sumo;

/// <summary>一次 <c>sumo -c</c> 的结果（含解析后的统计）。</summary>
public sealed class SumoRunResult
{
    public bool Ok { get; init; }
    public bool TimedOut { get; init; }
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;

    /// <summary>解析后的统计（snake_case）：见 <see cref="SumoRunner.ParseOutput"/>。</summary>
    public IReadOnlyDictionary<string, object?> Stats { get; init; } =
        new Dictionary<string, object?>();
}

/// <summary>
/// 跑一次 <c>sumo -c &lt;cfg&gt;</c> 并解析统计（S5：一键试跑）。
///
/// <para><b>为什么要解析文本</b>：SUMO 的 <c>--duration-log.statistics</c> 已经把
/// "Inserted / WaitingTime / TimeLoss / 平均速度"打在人能读的日志里，没有结构化输出口
/// （统计文件要靠 additional 里的 edgeData，那是另一套东西）。解析日志最省事、且与
/// 用户自己在命令行看到的一模一样。</para>
///
/// <para><b>★ 同名键必须按"段"分开</b>：`Performance:` 段里有 `Duration: 0.00s`（墙钟），
/// `Statistics:` 段里也有 `Duration: 28.07`（平均行程时间）。不分段就会互相覆盖，
/// 表现为"仿真跑得飞快时平均行程时间是 0"。</para>
///
/// <para><b>必须异步</b>：大网一次几秒到几十秒，调用方（<c>MapExportBridge</c>）放在后台线程，
/// 界面用 job + 轮询（与探针同一套手法）。</para>
/// </summary>
public static class SumoRunner
{
    private const int DefaultTimeoutMs = 600_000;

    /// <summary>
    /// 找 sumo / sumo-gui。
    /// </summary>
    /// <param name="hintPath">用户指定的 exe 路径或所在目录（可空）。</param>
    /// <param name="sumoHome">SUMO_HOME（可空，空则读环境变量）。</param>
    /// <param name="extraSearchDir">额外搜索目录（工具链自带的 bin，可空）。</param>
    /// <param name="exeName">不带扩展名：<c>sumo</c> / <c>sumo-gui</c> / <c>netconvert</c>。</param>
    public static string? Find(string hintPath, string sumoHome, string extraSearchDir, string exeName)
    {
        string fileName = OperatingSystem.IsWindows() ? exeName + ".exe" : exeName;
        var candidates = new List<string>();

        string hint = (hintPath ?? string.Empty).Trim().Trim('"');
        if (hint.Length > 0)
        {
            // 允许传目录（导出面板的「SUMO 安装目录」那种写法）
            candidates.Add(Directory.Exists(hint) ? Path.Combine(hint, fileName) : hint);
        }

        string home = string.IsNullOrWhiteSpace(sumoHome)
            ? (Environment.GetEnvironmentVariable("SUMO_HOME") ?? string.Empty).Trim().Trim('"')
            : sumoHome.Trim().Trim('"');
        if (home.Length > 0)
        {
            candidates.Add(Path.Combine(home, "bin", fileName));
            candidates.Add(Path.Combine(home, fileName));
        }

        if (!string.IsNullOrWhiteSpace(extraSearchDir))
        {
            candidates.Add(Path.Combine(extraSearchDir, fileName));
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                candidates.Add(Path.Combine(dir.Trim(), fileName));
            }
        }

        // 最后兜底：Windows 上的默认安装位置（用户不一定设了 SUMO_HOME）
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(@"C:\Program Files (x86)\SUMO\bin\" + fileName);
            candidates.Add(@"C:\Program Files\SUMO\bin\" + fileName);
        }

        foreach (string candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch
            {
                // 非法路径直接跳过
            }
        }

        return null;
    }

    /// <summary>
    /// 跑一次仿真。<b>会阻塞到 SUMO 退出或超时</b>，调用方必须在后台线程。
    /// </summary>
    public static SumoRunResult Run(string exePath, string cfgPath, int timeoutMs = DefaultTimeoutMs)
    {
        if (!File.Exists(exePath))
        {
            return Fail("找不到 sumo：" + exePath);
        }

        if (!File.Exists(cfgPath))
        {
            return Fail("找不到 .sumocfg：" + cfgPath);
        }

        string workDir = Path.GetDirectoryName(Path.GetFullPath(cfgPath)) ?? Directory.GetCurrentDirectory();
        var args = new List<string> { "-c", Path.GetFileName(cfgPath) };

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = workDir,     // ★ cfg 里是相对路径，工作目录必须是它所在的文件夹
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string a in args)
            {
                info.ArgumentList.Add(a);
            }

            string? home = Environment.GetEnvironmentVariable("SUMO_HOME");
            if (!string.IsNullOrWhiteSpace(home))
            {
                info.Environment["SUMO_HOME"] = home.Trim().Trim('"');
            }

            using var process = Process.Start(info);
            if (process == null)
            {
                return Fail("无法启动 sumo。");
            }

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    stdout.AppendLine(e.Data);
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                {
                    stderr.AppendLine(e.Data);
                }
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            if (!process.WaitForExit(timeoutMs))
            {
                TryKill(process);
                return new SumoRunResult
                {
                    Ok = false,
                    TimedOut = true,
                    Error = $"仿真超过 {timeoutMs / 1000} 秒未结束，已中止。",
                };
            }

            process.WaitForExit();               // 等异步输出排空，别截断统计

            string outText = stdout.ToString();
            string errText = stderr.ToString();
            bool ok = process.ExitCode == 0;
            return new SumoRunResult
            {
                Ok = ok,
                ExitCode = process.ExitCode,
                StdOut = outText,
                StdErr = errText,
                Error = ok ? string.Empty : FirstUsefulLine(outText, errText, process.ExitCode),
                Stats = ParseOutput(outText),
            };
        }
        catch (Exception ex)
        {
            return Fail("启动 sumo 失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 开 sumo-gui（**不等它退出**）。返回 (是否成功, 错误说明)。
    /// </summary>
    public static (bool Ok, string Error) OpenGui(string exePath, string cfgPath)
    {
        if (!File.Exists(exePath))
        {
            return (false, "找不到 sumo-gui：" + exePath);
        }

        if (!File.Exists(cfgPath))
        {
            return (false, "找不到 .sumocfg：" + cfgPath);
        }

        try
        {
            var info = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(cfgPath))
                                   ?? Directory.GetCurrentDirectory(),
                UseShellExecute = true,          // GUI 用 shell 起，别把我们的控制台挂上去
            };
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add(Path.GetFileName(cfgPath));
            Process.Start(info);
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, "启动 sumo-gui 失败：" + ex.Message);
        }
    }

    // ============================================================
    // 统计解析
    // ============================================================

    private static readonly Regex KeyValue = new(
        @"^(?<key>[A-Za-z][A-Za-z0-9 ._\-]*?)\s*[:=]\s*(?<val>.*)$", RegexOptions.Compiled);

    private static readonly Regex FirstNumber = new(
        @"-?\d+(?:\.\d+)?", RegexOptions.Compiled);

    /// <summary>
    /// 把 sumo 的 stdout 解析成统计字典。**认不出来的行一律忽略**，不报错。
    ///
    /// <para>返回的键（snake_case）：<c>inserted / running / waiting / teleports /
    /// routelength / speed / duration / waitingtime / timeloss / departdelay /
    /// ended_at / reason / statistics_sample / raw_errors</c>。</para>
    /// </summary>
    public static IReadOnlyDictionary<string, object?> ParseOutput(string text)
    {
        var stats = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["inserted"] = 0,
            ["running"] = 0,
            ["waiting"] = 0,
            ["teleports"] = 0,
            ["routelength"] = 0.0,
            ["speed"] = 0.0,
            ["duration"] = 0.0,
            ["waitingtime"] = 0.0,
            ["timeloss"] = 0.0,
            ["departdelay"] = 0.0,
            ["ended_at"] = 0.0,
            ["reason"] = string.Empty,
            ["statistics_sample"] = 0,
        };

        var errors = new List<string>();
        string section = string.Empty;

        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            string trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            // 段标题："Vehicles:" / "Statistics (avg of 57):" / "Performance:"
            if (trimmed.EndsWith(':'))
            {
                string head = trimmed[..^1];
                int paren = head.IndexOf('(');
                if (paren > 0)
                {
                    string inside = head[paren..].Trim('(', ')', ' ');
                    // (avg of 57) → 57，用来判断"样本太少，平均值没意义"
                    Match m = FirstNumber.Match(inside);
                    if (m.Success && int.TryParse(m.Value, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int sample))
                    {
                        stats["statistics_sample"] = sample;
                    }

                    head = head[..paren];
                }

                section = head.Trim().ToLowerInvariant().Replace(" ", "");
                continue;
            }

            // 明显的错误行（兼容 "Error:" / "Warning:" 前缀）
            if (trimmed.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(trimmed);
                continue;
            }

            Match kv = KeyValue.Match(trimmed);
            if (!kv.Success)
            {
                continue;
            }

            string key = kv.Groups["key"].Value.Trim().ToLowerInvariant().Replace(" ", string.Empty);
            string val = kv.Groups["val"].Value.Trim();
            double? num = ParseFirstNumber(val);

            switch (key)
            {
                case "inserted" when section == "vehicles":
                    stats["inserted"] = (int)(num ?? 0);
                    break;
                case "running" when section == "vehicles":
                    stats["running"] = (int)(num ?? 0);
                    break;
                case "waiting" when section == "vehicles":
                    stats["waiting"] = (int)(num ?? 0);
                    break;
                case "routelength":
                    stats["routelength"] = Round(num);
                    break;
                case "speed":
                    stats["speed"] = Round(num);
                    break;
                case "waitingtime":
                    stats["waitingtime"] = Round(num);
                    break;
                case "timeloss":
                    stats["timeloss"] = Round(num);
                    break;
                case "departdelay":
                    stats["departdelay"] = Round(num);
                    break;
                case "duration":
                    // ★ 只认 Statistics 段的 Duration（平均行程时间）；
                    //   Performance 段那个是墙钟，丢掉。
                    if (section.StartsWith("statistics", StringComparison.Ordinal))
                    {
                        stats["duration"] = Round(num);
                    }

                    break;
                case "teleports":
                case "teleporting":
                    stats["teleports"] = (int)(num ?? 0);
                    break;
                case "simulationendedattime":
                    stats["ended_at"] = Round(num);
                    break;
                case "reason":
                    stats["reason"] = val.TrimEnd('.');
                    break;
            }
        }

        if (errors.Count > 0)
        {
            stats["raw_errors"] = errors;
        }

        return stats;
    }

    /// <summary>取字符串里第一个数（`0.00s` → 0；`3 (Collision: 1, ...)` → 3）。</summary>
    internal static double? ParseFirstNumber(string text)
    {
        Match m = FirstNumber.Match(text ?? string.Empty);
        if (!m.Success)
        {
            return null;
        }

        return double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v
            : null;
    }

    private static double Round(double? value)
        => value.HasValue ? Math.Round(value.Value, 2) : 0.0;

    /// <summary>失败时给一句人话：优先 Error 行，其次 stderr，最后退出码。</summary>
    private static string FirstUsefulLine(string stdout, string stderr, int exitCode)
    {
        foreach (string line in (stderr + "\n" + stdout).Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
            {
                return t;
            }
        }

        string trimmedErr = (stderr ?? string.Empty).Trim();
        if (trimmedErr.Length > 0)
        {
            return trimmedErr.Split('\n')[0].Trim();
        }

        return "sumo 退出码 " + exitCode;
    }

    private static SumoRunResult Fail(string message) => new() { Ok = false, Error = message };

    private static void TryKill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 已退出
        }
    }
}
