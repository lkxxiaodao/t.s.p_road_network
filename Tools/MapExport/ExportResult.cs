using System.Text.Json.Serialization;

namespace SignalPlanner.MapExport;

/// <summary>单个格式的导出结果。</summary>
public sealed class ExportResult
{
    [JsonPropertyName("format")]
    public string Format { get; init; } = string.Empty;

    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("error")]
    public string Error { get; init; } = string.Empty;

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public static ExportResult Success(string format, string path, IReadOnlyList<string>? warnings = null)
        => new()
        {
            Format = format,
            Ok = true,
            Path = path,
            Warnings = warnings ?? Array.Empty<string>(),
        };

    public static ExportResult Failure(string format, string error, string path = "")
        => new()
        {
            Format = format,
            Ok = false,
            Path = path,
            Error = error,
        };
}

/// <summary>一次多格式导出的汇总。</summary>
public sealed class ExportSummary
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("results")]
    public IReadOnlyList<ExportResult> Results { get; init; } = Array.Empty<ExportResult>();

    [JsonPropertyName("error")]
    public string Error { get; init; } = string.Empty;

    /// <summary>
    /// 专业档体检结论（S6，导出后读产物得出的）。没有专业档 / 体检没跑时是空数组。
    /// ★ 字段名保持 PascalCase（与 SimCore 的校验条目同格式），前端直接并进校验清单。
    /// </summary>
    [JsonPropertyName("sumoFindings")]
    public IReadOnlyList<Sumo.SumoCheck.Finding> SumoFindings { get; init; }
        = Array.Empty<Sumo.SumoCheck.Finding>();
}
