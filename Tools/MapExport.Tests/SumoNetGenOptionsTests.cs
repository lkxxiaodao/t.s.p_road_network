using SignalPlanner.MapExport.Sumo;
using Xunit;

namespace SignalPlanner.MapExport.Tests;

/// <summary>
/// netconvert 开关（S4）。★ 核心承诺：**没有专业档时参数表与 2026-11 之前完全一致**
/// （`--no-turnarounds true` 一直是硬编码），基础模式产物因此逐字节不变。
/// </summary>
public sealed class SumoNetGenOptionsTests
{
    [Fact]
    public void DefaultsMatchLegacyBehaviour()
    {
        SumoNetGenOptions gen = SumoNetGenOptions.FromProfile(null);

        Assert.True(gen.NoTurnarounds);          // 既有硬编码就是 true
        Assert.False(gen.NoInternalLinks);
        Assert.False(gen.LeftHand);
        Assert.False(gen.JunctionsJoin);
        Assert.True(gen.IsDefault);
        Assert.Empty(gen.ToArgs());              // 默认值一个参数都不追加
        Assert.Equal("禁掉头", gen.Describe());
    }

    [Fact]
    public void ParsesSnakeCaseProfile()
    {
        const string json = """
            {"format_version":1,"netconvert":{
              "no_turnarounds":false,
              "no_internal_links":true,
              "lefthand":true,
              "junctions_join":true
            }}
            """;
        SumoNetGenOptions gen = SumoNetGenOptions.FromProfile(json);

        Assert.False(gen.NoTurnarounds);
        Assert.True(gen.NoInternalLinks);
        Assert.True(gen.LeftHand);
        Assert.True(gen.JunctionsJoin);
        Assert.False(gen.IsDefault);

        IReadOnlyList<string> args = gen.ToArgs();
        Assert.Equal(
            new[] { "--no-internal-links", "true", "--lefthand", "true", "--junctions.join", "true" },
            args);
    }

    [Fact]
    public void MissingKeysFallBackToDefaults()
    {
        // 只写了 lefthand：其余三个必须是默认值（no_turnarounds 不能因为缺键就变 false）
        SumoNetGenOptions gen = SumoNetGenOptions.FromProfile("""{"netconvert":{"lefthand":true}}""");

        Assert.True(gen.NoTurnarounds);
        Assert.True(gen.LeftHand);
        Assert.False(gen.NoInternalLinks);
        Assert.Equal(new[] { "--lefthand", "true" }, gen.ToArgs());
    }

    [Fact]
    public void BrokenJsonFallsBackToDefaults()
    {
        Assert.True(SumoNetGenOptions.FromProfile("{ not json").IsDefault);
        Assert.True(SumoNetGenOptions.FromProfile("""{"netconvert":"nonsense"}""").IsDefault);
        Assert.True(SumoNetGenOptions.FromProfile(string.Empty).IsDefault);
    }

    [Fact]
    public void CamelCaseKeysAreIgnored()
    {
        // ★ E10 §8-4 的回归：驼峰键匹配不上必须"静默当没写"，**不能**当成 false 生效
        SumoNetGenOptions gen = SumoNetGenOptions.FromProfile("""{"netconvert":{"noTurnarounds":false}}""");
        Assert.True(gen.NoTurnarounds);
    }
}
