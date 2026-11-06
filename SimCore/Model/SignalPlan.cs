namespace SimCore.Model;

/// <summary>
/// 一套信号配时方案（某时段的相位序列）。字段对齐技术方案03 §3.2 的
/// <c>signals[].plans[]</c>：<c>{ start_time, stages[] }</c>。
/// </summary>
/// <remarks>
/// <para>一个节点可有多个方案，按 <see cref="StartTime"/> 时段切换——即支持第四章的时变配时
/// （早 / 晚高峰各一套，技术方案02 §2.9）。"当前生效方案"的选择属仿真期逻辑（T1.2），不在此模型层。</para>
/// <para>本类型为纯数据载体，不含任何仿真逻辑。</para>
/// </remarks>
public sealed class SignalPlan
{
    /// <summary>方案生效起始时刻，秒（§3.2 <c>plans[].start_time</c>）。</summary>
    public double StartTime { get; init; }

    /// <summary>本方案的相位序列（§3.2 <c>plans[].stages[]</c>）。</summary>
    public IReadOnlyList<Stage> Stages { get; init; } = Array.Empty<Stage>();
}
