using System.Diagnostics;
using System.Text;

namespace SignalPlanner.MapExport.Sumo;

/// <summary>netconvert 调用结果。</summary>
public sealed class NetconvertRunResult
{
    public int ExitCode { get; init; }
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;
    public bool TimedOut { get; init; }

    public bool Ok => !TimedOut && ExitCode == 0 && File.Exists(OutputPath);

    public string OutputPath { get; init; } = string.Empty;
}

/// <summary>查找并调用 SUMO 的 netconvert。</summary>
public static class NetconvertRunner
{
    private const int DefaultTimeoutMs = 120_000;

    public static string? Find(string hintPath, string extraSearchDir)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(hintPath))
        {
            candidates.Add(hintPath);
        }

        string? sumoHome = Environment.GetEnvironmentVariable("SUMO_HOME");
        if (!string.IsNullOrWhiteSpace(sumoHome))
        {
            candidates.Add(Path.Combine(sumoHome, "bin", ExecutableName));
        }

        if (!string.IsNullOrWhiteSpace(extraSearchDir))
        {
            candidates.Add(Path.Combine(extraSearchDir, ExecutableName));
        }

        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                candidates.Add(Path.Combine(dir.Trim(), ExecutableName));
            }
        }

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static string ReadVersion(string exePath)
    {
        try
        {
            using var process = StartProcess(exePath, new[] { "--version" });
            if (process == null || !process.WaitForExit(10_000))
            {
                TryKill(process);
                return string.Empty;
            }

            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            string text = string.IsNullOrWhiteSpace(output) ? error : output;
            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static NetconvertRunResult Run(
        string exePath,
        string xodrPath,
        string outputPath,
        bool keepOriginalNames,
        IReadOnlyList<string>? typeFiles = null,
        SumoNetGenOptions? gen = null)
    {
        string tmp = outputPath + ".tmp";
        var args = new List<string>
        {
            "--opendrive-files", xodrPath,
            "--output-file", tmp,
            // ★ 这是**默认**值。用户关了「禁止掉头」（SumoNetGenOptions.NoTurnarounds=false）
            //   时这里不传，交给 netconvert 自己生成掉头连接。
            "--no-turnarounds", gen is null || gen.NoTurnarounds ? "true" : "false",
            "--offset.disable-normalization", "true",
            // 我们自己的 xodr 已通过 XML 结构测试；显式关闭 schema 校验，
            // 避免 netconvert 因为找不到 SUMO_HOME 里的 XSD 而给出噪音警告/误判。
            "--xml-validation", "never",
        };

        if (keepOriginalNames)
        {
            args.Add("--output.original-names");
            args.Add("true");
        }

        if (typeFiles is { Count: > 0 })
        {
            args.Add("--type-files");
            args.Add(string.Join(",", typeFiles));
        }

        // ★ 专业模式的其余开关。**追加在末尾**，保证没有专业档时参数表与 2026-11 之前逐字节一致。
        //   探针与导出共用这一份（见 SumoNetGenOptions 类注释）。
        if (gen != null)
        {
            args.AddRange(gen.ToArgs());
        }

        try
        {
            using var process = StartProcess(exePath, args);
            if (process == null)
            {
                return new NetconvertRunResult { ExitCode = -1, StdErr = "无法启动 netconvert。" };
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

            bool exited = process.WaitForExit(DefaultTimeoutMs);
            if (!exited)
            {
                TryKill(process);
                TryDelete(tmp);
                return new NetconvertRunResult
                {
                    ExitCode = -1,
                    TimedOut = true,
                    StdErr = "netconvert 超过 120 秒未结束。",
                };
            }

            // 等异步输出读完，避免截断日志。
            process.WaitForExit();

            string outText = stdout.ToString();
            string errText = stderr.ToString();
            if (process.ExitCode == 0 && File.Exists(tmp))
            {
                File.Move(tmp, outputPath, true);
                return new NetconvertRunResult
                {
                    ExitCode = process.ExitCode,
                    StdOut = outText,
                    StdErr = errText,
                    OutputPath = outputPath,
                };
            }

            TryDelete(tmp);
            return new NetconvertRunResult
            {
                ExitCode = process.ExitCode,
                StdOut = outText,
                StdErr = errText,
                OutputPath = string.Empty,
            };
        }
        catch (Exception ex)
        {
            TryDelete(tmp);
            return new NetconvertRunResult
            {
                ExitCode = -1,
                StdErr = ex.Message,
            };
        }
    }

    private static Process? StartProcess(string exePath, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        ConfigureSumoEnvironment(info, exePath);
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        return Process.Start(info);
    }

    /// <summary>
    /// 从 netconvert.exe 的位置推断 SUMO_HOME，并写入子进程环境。
    /// 典型结构：&lt;SUMO_HOME&gt;/bin/netconvert.exe + &lt;SUMO_HOME&gt;/data/typemap。
    /// 若用户已设置有效的 SUMO_HOME，则优先保留用户值。
    /// </summary>
    public static string ResolveSumoHome(string exePath)
    {
        string exeDir = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? string.Empty;
        string candidate = exeDir;

        if (string.Equals(Path.GetFileName(exeDir), "bin", StringComparison.OrdinalIgnoreCase))
        {
            candidate = Path.GetFullPath(Path.Combine(exeDir, ".."));
        }

        if (HasTypeMaps(candidate))
        {
            return candidate;
        }

        if (!string.Equals(candidate, exeDir, StringComparison.OrdinalIgnoreCase) && HasTypeMaps(exeDir))
        {
            return exeDir;
        }

        string? env = Environment.GetEnvironmentVariable("SUMO_HOME");
        if (!string.IsNullOrWhiteSpace(env))
        {
            string cleaned = env.Trim().Trim('"');
            if (HasTypeMaps(cleaned))
            {
                return Path.GetFullPath(cleaned);
            }
        }

        return candidate;
    }

    private static void ConfigureSumoEnvironment(ProcessStartInfo info, string exePath)
    {
        string? env = Environment.GetEnvironmentVariable("SUMO_HOME");
        if (!string.IsNullOrWhiteSpace(env))
        {
            string cleaned = env.Trim().Trim('"');
            if (HasTypeMaps(cleaned))
            {
                info.Environment["SUMO_HOME"] = Path.GetFullPath(cleaned);
                return;
            }
        }

        string inferred = ResolveSumoHome(exePath);
        if (HasTypeMaps(inferred))
        {
            info.Environment["SUMO_HOME"] = inferred;
        }
        else if (!string.IsNullOrWhiteSpace(env))
        {
            // 用户设过但目录无效：保留原值，至少不改变 netconvert 的既有行为。
            info.Environment["SUMO_HOME"] = env;
        }
    }

    private static bool HasTypeMaps(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        try
        {
            return Directory.Exists(Path.Combine(directory, "data", "typemap"));
        }
        catch
        {
            return false;
        }
    }

    private static string ExecutableName
        => OperatingSystem.IsWindows() ? "netconvert.exe" : "netconvert";

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
            // 进程可能已退出。
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
