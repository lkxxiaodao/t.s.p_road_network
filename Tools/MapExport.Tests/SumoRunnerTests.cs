using SignalPlanner.MapExport.Sumo;
using Xunit;

namespace SignalPlanner.MapExport.Tests;

/// <summary>
/// sumo 日志解析（S5 试跑的统计来源）。
/// ★ 最关键的一条：`Performance:` 段的 `Duration`（墙钟）不能覆盖
/// `Statistics:` 段的 `Duration`（平均行程时间）—— 否则"跑得飞快"时行程时间显示 0。
/// </summary>
public sealed class SumoRunnerTests
{
    /// <summary>真跑 sumo 1.28 抓下来的原文（示例关卡 0~300 s）。</summary>
    private const string RealLog = """
        Loading net-file from '01-1.net.xml' ... done (3ms).
        Loading route-files incrementally from '01-1.rou.xml'
        Loading done.
        Simulation version 1.28.0 started with time: 0.00.
        Simulation ended at time: 300.00.
        Reason: The final simulation step has been reached.
        Performance:
         Duration: 0.00s
         Real time factor: 75000
         UPS: 410000.000000
        Vehicles:
         Inserted: 60
         Running: 3
         Waiting: 0
        Statistics (avg of 57):
         RouteLength: 171.77
         Speed: 7.59
         Duration: 28.07
         WaitingTime: 9.63
         TimeLoss: 14.46
         DepartDelay: 0.00
        """;

    [Fact]
    public void ParsesRealSumoLog()
    {
        IReadOnlyDictionary<string, object?> s = SumoRunner.ParseOutput(RealLog);

        Assert.Equal(60, s["inserted"]);
        Assert.Equal(3, s["running"]);
        Assert.Equal(0, s["waiting"]);
        Assert.Equal(171.77, s["routelength"]);
        Assert.Equal(7.59, s["speed"]);
        Assert.Equal(9.63, s["waitingtime"]);
        Assert.Equal(14.46, s["timeloss"]);
        Assert.Equal(0.0, s["departdelay"]);
        Assert.Equal(300.0, s["ended_at"]);
        Assert.Equal(57, s["statistics_sample"]);
        Assert.Equal("The final simulation step has been reached", s["reason"]);
    }

    [Fact]
    public void PerformanceDurationDoesNotOverwriteStatisticsDuration()
    {
        IReadOnlyDictionary<string, object?> s = SumoRunner.ParseOutput(RealLog);
        Assert.Equal(28.07, s["duration"]);      // 不是 Performance 的 0.00
    }

    [Fact]
    public void ParsesTeleportsWithParenthesisedBreakdown()
    {
        const string log = """
            Vehicles:
             Inserted: 12
             Teleports: 3 (Collision: 1, Jam: 2, Yield: 0, Wrong Lane: 0)
            """;
        IReadOnlyDictionary<string, object?> s = SumoRunner.ParseOutput(log);
        Assert.Equal(12, s["inserted"]);
        Assert.Equal(3, s["teleports"]);
    }

    [Fact]
    public void CollectsErrorLines()
    {
        const string log = """
            Loading net-file from 'x.net.xml' ... done.
            Error: Vehicle 'f0_0.0' has no valid route.
            Quitting (on error).
            """;
        IReadOnlyDictionary<string, object?> s = SumoRunner.ParseOutput(log);
        var errors = Assert.IsAssignableFrom<IReadOnlyList<string>>(s["raw_errors"]);
        Assert.Single(errors);
        Assert.Contains("no valid route", errors[0]);
    }

    [Fact]
    public void GarbageInputDoesNotThrow()
    {
        foreach (string text in new[] { string.Empty, "   ", "hello world", ":::", "\n\n\n" })
        {
            IReadOnlyDictionary<string, object?> s = SumoRunner.ParseOutput(text);
            Assert.Equal(0, s["inserted"]);
            Assert.Equal(0.0, s["speed"]);
        }
    }

    [Fact]
    public void FindsExeInGivenDirectory()
    {
        string dir = TempDir.Create();
        try
        {
            string exe = Path.Combine(dir, OperatingSystem.IsWindows() ? "sumo.exe" : "sumo");
            File.WriteAllText(exe, string.Empty);

            Assert.Equal(Path.GetFullPath(exe),
                SumoRunner.Find(dir, string.Empty, string.Empty, "sumo"));
            Assert.Equal(Path.GetFullPath(exe),
                SumoRunner.Find(exe, string.Empty, string.Empty, "sumo"));
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }
}
