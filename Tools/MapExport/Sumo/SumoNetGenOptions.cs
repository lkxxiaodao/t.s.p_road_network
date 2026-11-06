using System.Text.Json;
using System.Text.Json.Serialization;

namespace SignalPlanner.MapExport.Sumo;

/// <summary>
/// netconvert 的 4 个常用开关（技术方案06 §6.2：200+ 选项只露这几个）。
///
/// <para><b>默认值必须让基础模式产物逐字节不变</b>：`--no-turnarounds true` 从 2026-11 起
/// 就是硬编码的，所以 <see cref="NoTurnarounds"/> 默认 true，其余三个默认 false（= 不传参数）。
/// 换句话说：没有专业档 / 专业档里没配 `netconvert` 段时，拼出来的参数表与以前**完全一致**。</para>
///
/// <para><b>★ 探针与导出必须用同一份</b>：<see cref="SumoNetProbe"/> 也调这个类。两边一旦分叉，
/// 就会出现"信号灯编辑器里看到 16 个灯位、导出的 net.xml 里只有 15 个"这种对不上的问题
/// （与 `SumoNetIndex` 被两处共用是同一个道理）。</para>
/// </summary>
public sealed class SumoNetGenOptions
{
    public bool NoTurnarounds { get; init; } = true;

    public bool NoInternalLinks { get; init; }

    public bool LeftHand { get; init; }

    public bool JunctionsJoin { get; init; }

    /// <summary>是不是"一个都没改"（= 与基础模式完全一样）。</summary>
    [JsonIgnore]
    public bool IsDefault =>
        NoTurnarounds && !NoInternalLinks && !LeftHand && !JunctionsJoin;

    /// <summary>
    /// 从专业档 JSON 的 <c>netconvert</c> 段解析。**任何异常都回落默认值**——
    /// 开关读不出来最多是"少改一项"，不该让整次导出失败。
    /// </summary>
    public static SumoNetGenOptions FromProfile(string? profileJson)
    {
        if (string.IsNullOrWhiteSpace(profileJson))
        {
            return new SumoNetGenOptions();
        }

        try
        {
            NetconvertDto? dto = JsonSerializer.Deserialize<ProfileDto>(profileJson, ReadOptions)?.Netconvert;
            if (dto == null)
            {
                return new SumoNetGenOptions();
            }

            return new SumoNetGenOptions
            {
                NoTurnarounds = dto.NoTurnarounds ?? true,
                NoInternalLinks = dto.NoInternalLinks ?? false,
                LeftHand = dto.LeftHand ?? false,
                JunctionsJoin = dto.JunctionsJoin ?? false,
            };
        }
        catch (JsonException)
        {
            return new SumoNetGenOptions();
        }
    }

    /// <summary>
    /// 要追加到 netconvert 命令行末尾的参数（不含 exe 与固定参数）。
    /// 顺序固定，保证同一份配置每次导出逐字节一致。
    /// </summary>
    public IReadOnlyList<string> ToArgs()
    {
        var args = new List<string>();
        if (NoInternalLinks)
        {
            args.Add("--no-internal-links");
            args.Add("true");
        }

        if (LeftHand)
        {
            args.Add("--lefthand");
            args.Add("true");
        }

        if (JunctionsJoin)
        {
            args.Add("--junctions.join");
            args.Add("true");
        }

        return args;
    }

    /// <summary>给导出结果 / 试跑摘要看的一句话。</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (NoTurnarounds) parts.Add("禁掉头");
        if (NoInternalLinks) parts.Add("无内部车道");
        if (LeftHand) parts.Add("左侧通行");
        if (JunctionsJoin) parts.Add("合并路口");
        return parts.Count == 0 ? "默认" : string.Join(" / ", parts);
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class ProfileDto
    {
        [JsonPropertyName("netconvert")]
        public NetconvertDto? Netconvert { get; set; }
    }

    /// <summary>
    /// ★ 字段一律 snake_case（E10 §8-4：驼峰会被 <c>JsonPropertyName</c> 静默丢掉）。
    /// 全部可空：缺键 = 用默认值。
    /// </summary>
    private sealed class NetconvertDto
    {
        [JsonPropertyName("no_turnarounds")] public bool? NoTurnarounds { get; set; }
        [JsonPropertyName("no_internal_links")] public bool? NoInternalLinks { get; set; }
        [JsonPropertyName("lefthand")] public bool? LeftHand { get; set; }
        [JsonPropertyName("junctions_join")] public bool? JunctionsJoin { get; set; }
    }
}
