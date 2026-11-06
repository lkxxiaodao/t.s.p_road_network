namespace SimCore.Sim;

/// <summary>
/// 全局仿真常量（<c>docs/SimCore-API.md</c> §4.1 冻结；对应 T1.1 ④「饱和流率、起步损失时间等常量集中定义」）。
/// </summary>
/// <remarks>
/// <para>本类型是**全项目唯一真相**：任何卡不得就地硬编码同义数字（如 <c>1800</c>、<c>2.0</c>、<c>4.5</c>）。
/// 若需按关卡覆盖，走契约 §11 变更流程。</para>
/// <para>本类型成员名 / 值与 §4.1 **逐字一致**，为冻结接口，只能调用不得修改。</para>
/// </remarks>
public static class SimConstants
{
    /// <summary>饱和流率 ≈ 0.5 辆/秒/车道（= 1800 辆/小时/车道；T1.1 b，误差 ±5%）。</summary>
    public const double SaturationFlowPerLane = 1800d / 3600d;

    /// <summary>起步损失时间（秒，T1.1 d；技术方案01 §3.2b 的绿灯爬升时长）。</summary>
    public const double StartUpLostTime = 2.0d;

    /// <summary>标准车长（米）。</summary>
    public const double VehicleLength = 4.5d;

    /// <summary>公交加长（米，T3.1 b）。</summary>
    public const double BusLength = 12.0d;

    /// <summary>货车车长（米；T4.3 S1b，界于小汽车与公交之间）。</summary>
    public const double TruckLength = 7.0d;

    /// <summary>小汽车速度系数（× 车道限速；T4.3 S1b。1.0 = 不受车型限制）。</summary>
    public const double CarSpeedFactor = 1.0d;

    /// <summary>货车速度系数（× 车道限速；T4.3 S1b，略慢）。</summary>
    public const double TruckSpeedFactor = 0.85d;

    /// <summary>公交速度系数（× 车道限速；T4.3 S1b，接近小汽车）。</summary>
    public const double BusSpeedFactor = 0.95d;

    /// <summary>最小车头间距（米，即前后车保险杠之间的最小净距）。</summary>
    public const double MinHeadway = 1.5d;

    /// <summary>行人过街速度（米/秒，T1.4 c）。</summary>
    public const double PedestrianSpeed = 1.4d;

    /// <summary>混行非机动车起步附加延迟（秒，T1.5 c）。</summary>
    public const double BicycleStartDelay = 1.0d;

    /// <summary>溢出阈值：首车道占有率 &gt; 0.9（T2.1 e）。</summary>
    public const double SpillbackOccupancy = 0.9d;
}
