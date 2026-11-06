using SimCore.Io;

namespace SimCore.Sim;

/// <summary>
/// 车型构成规则（T4.3 S1b）：预设 → 比例表、期望车头间距、逐车车型选择。
/// </summary>
/// <remarks>
/// <para><b>关卡级、只允许选预设</b>（<see cref="VehicleMix"/>），不允许自定义比例与逐车道配置
/// （老板 2026-10-19 裁定）。</para>
/// <para><b>影响面只有三处</b>：①车长 → 跟车间距（<see cref="CarFollowing.FollowingLimit"/> 按前车实际车长算，天然生效）；
/// ②期望车头间距 → 队列容量（<see cref="Queue.Capacity"/>）；③速度系数 → 目标速度
/// （<see cref="Queue"/> 的 <c>min(车道限速 × 系数, 刹车上限)</c>）。<b>不改变</b>饱和流率、放行掩码与转向路由。</para>
/// <para><b>确定性（铁律 2）</b>：<see cref="PickKind"/> 是纯函数——同一 <c>(mix, laneId, seq)</c> 恒返回同一车型，
/// 无系统随机数。比例用"千分位格子 + 互质步长打散"，1000 辆一循环内各车型数量精确等于比例，
/// 又不会出现"前 550 辆全是小汽车"这种整段同型的假象。</para>
/// <para>缺省（<c>null</c> / <see cref="VehicleMix.CarOnly"/>）时全部为小汽车、期望间距 <c>6.0m</c>，
/// 与 T4.3 之前的逐位行为一致。</para>
/// </remarks>
internal static class VehicleMixRules
{
    /// <summary>比例基数（千分位）。三个份额之和恒为 <see cref="Permille"/>。</summary>
    public const int Permille = 1000;

    /// <summary>打散步长：与 <see cref="Permille"/> 互质，保证 1000 次内遍历全部格子。</summary>
    private const int StepPerVehicle = 613;

    /// <summary>车道错相：让不同车道的车型序列不同（仍完全确定）。</summary>
    private const int StepPerLane = 149;

    /// <summary>取预设的三类车份额（千分位：小汽车 / 货车 / 公交，和 = 1000）。</summary>
    public static (int Car, int Truck, int Bus) Shares(VehicleMix? mix) => mix switch
    {
        VehicleMix.CityMix => (850, 120, 30),
        VehicleMix.FreightHeavy => (550, 450, 0),
        VehicleMix.BusHeavy => (700, 100, 200),
        VehicleMix.CarBusMix => (920, 0, 80),
        _ => (Permille, 0, 0),
    };

    /// <summary>
    /// 该预设下的**期望静止车头间距**（米）= Σ 份额 × 单车间距（车长 + 最小净距）。
    /// <para><see cref="VehicleMix.CarOnly"/> / <c>null</c> → 恰好 <c>4.5 + 1.5 = 6.0</c>，即历史容量口径。</para>
    /// </summary>
    public static double ExpectedJamSpacing(VehicleMix? mix)
    {
        (int car, int truck, int bus) = Shares(mix);
        double sum =
            (car * CarFollowing.JamSpacingOf(VehicleKind.Car)) +
            (truck * CarFollowing.JamSpacingOf(VehicleKind.Truck)) +
            (bus * CarFollowing.JamSpacingOf(VehicleKind.Bus));
        return sum / Permille;
    }

    /// <summary>
    /// 该车道第 <paramref name="seq"/> 辆（从 0 起）到达车辆的车型。
    /// <para>纯函数：同参数恒同结果；<c>CarOnly</c> / <c>null</c> 恒为 <see cref="VehicleKind.Car"/>。</para>
    /// </summary>
    /// <param name="mix">车型构成预设。</param>
    /// <param name="laneId">车道 id（用于错相，不影响总体比例）。</param>
    /// <param name="seq">该车道的到达序号（0 起，每次尝试注入递增；含因入口无位而溢出丢弃的那次）。</param>
    public static VehicleKind PickKind(VehicleMix? mix, int laneId, int seq)
    {
        (int car, int truck, int bus) = Shares(mix);
        if (truck == 0 && bus == 0)
        {
            return VehicleKind.Car;
        }

        long p = (((long)seq * StepPerVehicle) + ((long)laneId * StepPerLane)) % Permille;
        if (p < 0)
        {
            p += Permille;   // 防御：laneId 理论上非负，留一手
        }

        if (p < car)
        {
            return VehicleKind.Car;
        }

        return p < car + truck ? VehicleKind.Truck : VehicleKind.Bus;
    }
}
