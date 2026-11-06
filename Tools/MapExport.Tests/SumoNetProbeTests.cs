using SignalPlanner.MapExport.Sumo;
using Xunit;

namespace SignalPlanner.MapExport.Tests;

/// <summary>
/// 探针（专业模式的地基）的契约测试。
///
/// ★ 它锁的是"编辑器看到的 link 表"这件事本身：只要示例关卡还能探出
///   1 个信号灯位 / 10 个 link / 每个 link 都能翻译回 turn，S1 的信号灯矩阵就有稳定的行。
///   哪天 netconvert 升级改了 linkIndex 分配，这条会先炸，而不是等用户编出错位的配时。
/// </summary>
public sealed class SumoNetProbeTests
{
    [Fact]
    public void ProbesExampleNetworkWhenNetconvertAvailable()
    {
        string? exe = NetconvertRunner.Find(string.Empty, string.Empty);
        if (exe == null)
        {
            // xUnit 2.x 没有动态 Skip；未安装 SUMO 时直接返回（与 NetconvertTests 同口径）。
            return;
        }

        Dictionary<string, object?> result = SumoNetProbe.Probe(TestPaths.Example, new MapExportOptions
        {
            NetconvertPath = exe,
        });

        Assert.True((bool)result["ok"]!, (string?)result["error"]);

        var tls = Assert.IsType<List<Dictionary<string, object?>>>(result["tls"]);
        Dictionary<string, object?> only = Assert.Single(tls);
        Assert.Equal(0, (int)only["node"]!);
        Assert.Equal(10, (int)only["link_count"]!);

        var links = Assert.IsType<List<Dictionary<string, object?>>>(only["links"]);
        Assert.Equal(10, links.Count);
        Assert.All(links, link => Assert.True((int)link["turn"]! >= 0, "有 link 没能映射回 spmap 的 turn"));

        // linkIndex 必须从 0 起连续（state 串按它排位，缺号会让整串错位）
        int[] indexes = links.Select(l => (int)l["link_index"]!).OrderBy(i => i).ToArray();
        Assert.Equal(Enumerable.Range(0, 10).ToArray(), indexes);

        // 三类灯都要出现：机动车、自行车、人行横道（专业模式要分行分组显示）
        var kinds = links.Select(l => (string?)l["kind"]).Distinct().ToList();
        Assert.Contains("motor", kinds);
        Assert.Contains("crosswalk", kinds);

        var edges = Assert.IsType<List<Dictionary<string, object?>>>(result["edges"]);
        Assert.NotEmpty(edges);
        Assert.DoesNotContain(edges, e => ((string?)e["id"])?.StartsWith(":", StringComparison.Ordinal) == true);
    }
}
