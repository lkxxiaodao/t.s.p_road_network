using System.Reflection;
using System.Runtime.Versioning;

namespace SimCore;

/// <summary>
/// 工程骨架自检类型（T0.1 工具链任务）。
/// <para>
/// 用途：让 SimCore 程序集非空、并可作为 SimCore.Tests 的断言目标，从而用单元测试
/// 客观证明「SimCore 已成功编译、且目标框架为 net8.0」（对应验收标准 a）。
/// </para>
/// <para>
/// 重要：本类型 <b>不属于冻结接口</b>。冻结接口以 docs/SimCore-API.md 为准（由 T0.4 冻结）。
/// 它不携带任何业务语义，T0.2 起 Model / Geometry / Sim / Metrics / Scoring / Io 的实现与之无关。
/// </para>
/// </summary>
public static class SimCoreInfo
{
    /// <summary>程序集名称，期望为 <c>SimCore</c>。</summary>
    public const string AssemblyName = "SimCore";

    /// <summary>本工程声明的目标框架（TFM）。</summary>
    public const string ExpectedTargetFramework = "net8.0";

    /// <summary>
    /// 本程序集编译时写入的框架名（例如 <c>.NETCoreApp,Version=v8.0</c>）。
    /// </summary>
    public static string TargetFrameworkName =>
        typeof(SimCoreInfo).Assembly
            .GetCustomAttribute<TargetFrameworkAttribute>()?
            .FrameworkName
        ?? string.Empty;

    /// <summary>本程序集是否确实编译为 .NET 8（net8.0）。</summary>
    public static bool IsNet8 =>
        TargetFrameworkName.Contains("Version=v8.0", StringComparison.Ordinal);
}
