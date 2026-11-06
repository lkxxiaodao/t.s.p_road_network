namespace SimCore.Sim;

/// <summary>
/// 车辆跟驰约束（技术方案02 §2.1「车辆微观模型」、游戏设计v2 §9「恒定间距模型」）。
/// </summary>
/// <remarks>
/// <para>本类型只做**纵向跟驰**：给出「后车车头可达到的最大位置」与「由剩余距离反推的最大速度」，
/// 由 <see cref="Queue"/> 在每步推进时调用。<b>不含</b>信号逻辑（属 T1.2）、路口门控（属 T1.3）。</para>
/// <para>模型选择：以「固定安全车头间距」为准的**恒定间距模型**（游戏设计v2 §9）——后车与
/// 前车之间至少保留 <see cref="SimConstants.MinHeadway"/> 净距；接近约束线时按最大减速度减速，
/// 一旦单步越界即被硬钳制（保证「任意两车不重叠」，T1.1 a）。</para>
/// <para>为命中 T1.1 b（绿灯按约 1800 辆/小时/车道放行）所需的物理跟车时间，
/// 本类型的加 / 减速度由 <see cref="SimConstants.SaturationFlowPerLane"/> 与
/// <see cref="SimConstants.MinHeadway"/> 推导出的车头时距反标定（见 <see cref="SaturationHeadway"/>），
/// 取足够大的值使**放行节拍由饱和流率主导、而非被车辆动力学拖慢**。</para>
/// <para>本类型是 T1.1 内部的**非冻结**实现细节：后续卡可引用其常量，但不得依赖其数值语义
/// （唯有 <see cref="SimConstants"/> 是冻结真相）。</para>
/// </remarks>
internal static class CarFollowing
{
    /// <summary>期望加速度（米/秒²）。</summary>
    public const double Acceleration = 4.5d;

    /// <summary>期望减速度（米/秒²，非紧急制动）。</summary>
    public const double Deceleration = 4.5d;

    /// <summary>判定「停车」的速度阈值（米/秒）。</summary>
    public const double StopSpeedThreshold = 0.1d;

    /// <summary>饱和车头时距（秒）= 1 / <see cref="SimConstants.SaturationFlowPerLane"/> ≈ 2.0 秒。</summary>
    public static double SaturationHeadway => 1.0d / SimConstants.SaturationFlowPerLane;

    /// <summary>按车型取车长（米）。</summary>
    public static double VehicleLengthOf(VehicleKind kind) => kind switch
    {
        VehicleKind.Bus => SimConstants.BusLength,
        VehicleKind.Truck => SimConstants.TruckLength,
        _ => SimConstants.VehicleLength,
    };

    /// <summary>
    /// 按车型取**速度系数**（× 车道限速；T4.3 S1b）。
    /// <para>只做"目标速度上限"的缩放，不改变跟驰与放行节拍——保证车型影响「轻微」。</para>
    /// </summary>
    public static double SpeedFactorOf(VehicleKind kind) => kind switch
    {
        VehicleKind.Bus => SimConstants.BusSpeedFactor,
        VehicleKind.Truck => SimConstants.TruckSpeedFactor,
        _ => SimConstants.CarSpeedFactor,
    };

    /// <summary>按车型取**静止队列的最小车头间距**（车长 + 最小净距，米）。</summary>
    public static double JamSpacingOf(VehicleKind kind)
        => VehicleLengthOf(kind) + SimConstants.MinHeadway;

    /// <summary>
    /// 后车车头可达到的最大位置：<c>前车车头 − 前车车长 − 最小车头间距</c>。
    /// 即「不重叠」+「保留最小净距」的联合约束。
    /// </summary>
    /// <param name="leaderFront">前车车头距车道起点的距离（米）。</param>
    /// <param name="leaderLength">前车车长（米）。</param>
    public static double FollowingLimit(double leaderFront, double leaderLength)
        => leaderFront - leaderLength - SimConstants.MinHeadway;

    /// <summary>
    /// 由「剩余可行驶距离」反推的当前最大安全速度：<c>v = sqrt(2·a·d)</c>，使车辆恰能在约束线前停住。
    /// </summary>
    /// <param name="distanceToLimit">车头到约束线（前车尾部或停止线）的剩余距离（米）。</param>
    public static double BrakingSpeed(double distanceToLimit)
        => distanceToLimit <= 0d ? 0d : Math.Sqrt(2.0d * Deceleration * distanceToLimit);

    /// <summary>求「以当前速度在约束线前停住所需的最短距离」（米），用于进入判定。</summary>
    public static double StoppingDistance(double speed)
        => speed <= 0d ? 0d : (speed * speed) / (2.0d * Deceleration);

    /// <summary>
    /// 单步速度更新：把 <paramref name="speed"/> 向 <paramref name="targetSpeed"/> 逼近，
    /// 加速受 <see cref="Acceleration"/> 约束、减速受 <see cref="Deceleration"/> 约束。
    /// </summary>
    public static double StepSpeed(double speed, double targetSpeed, double dt)
    {
        if (targetSpeed > speed)
        {
            return Math.Min(targetSpeed, speed + (Acceleration * dt));
        }

        if (targetSpeed < speed)
        {
            return Math.Max(targetSpeed, speed - (Deceleration * dt));
        }

        return speed;
    }
}
