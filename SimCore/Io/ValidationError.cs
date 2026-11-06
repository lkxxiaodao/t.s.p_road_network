namespace SimCore.Io;

/// <summary>
/// 校验结论的严重级别（技术方案01 §3.6：编辑器边画边校验、红字提示并禁止导出）。
/// </summary>
public enum ValidationSeverity
{
    /// <summary>警告：不阻断导出，但提示可疑配置。</summary>
    Warning,

    /// <summary>错误：地图非法，禁止导出 / 禁止开局。</summary>
    Error,
}

/// <summary>
/// 一条校验结论（技术方案03 §3.1：<c>SpmapValidator.Validate(level) -&gt; ValidationError[]</c>）。
/// </summary>
/// <remarks>
/// <para><see cref="Code"/> 是稳定的机器可读代码（供编辑器 / 测试精确断言，也便于国际化）；
/// <see cref="Message"/> 是给人看的中文说明；<see cref="Path"/> 是出问题的 JSON 位置
/// （如 <c>turns[7].src_lane</c>），编辑器据此高亮。</para>
/// </remarks>
public sealed class ValidationError
{
    /// <summary>构造一条校验结论。</summary>
    /// <param name="code">稳定错误码（小写下划线）。</param>
    /// <param name="message">人类可读的中文说明。</param>
    /// <param name="path">出问题的 JSON 位置。</param>
    /// <param name="severity">严重级别，默认 <see cref="ValidationSeverity.Error"/>。</param>
    public ValidationError(string code, string message, string path, ValidationSeverity severity = ValidationSeverity.Error)
    {
        Code = code;
        Message = message;
        Path = path;
        Severity = severity;
    }

    /// <summary>稳定错误码（小写下划线，见 <c>docs/spmap-schema.md</c> 校验码表）。</summary>
    public string Code { get; }

    /// <summary>人类可读的中文说明。</summary>
    public string Message { get; }

    /// <summary>出问题的 JSON 位置（如 <c>turns[7].src_lane</c>）。</summary>
    public string Path { get; }

    /// <summary>严重级别。</summary>
    public ValidationSeverity Severity { get; }

    /// <summary>是否为错误级。</summary>
    public bool IsError => Severity == ValidationSeverity.Error;

    /// <inheritdoc />
    public override string ToString() =>
        $"[{Severity}] {Code} @ {Path}: {Message}";
}
