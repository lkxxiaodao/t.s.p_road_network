using SimCore.Model;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

/// <summary>
/// T1.1「车辆状态机 + 车道队列 + 跟驰约束」验收测试。
/// <para>分组与验收标准逐条对应，便于"手和眼"对证：
/// <list type="bullet">
///   <item>A 组 → 验收 a) 红灯时车辆在停止线排队且不重叠</item>
///   <item>B 组 → 验收 b) 绿灯按饱和流率放行（约 1800 veh/h/lane，误差 ±5%）</item>
///   <item>C 组 → 验收 c) 每辆车累计 <c>BlockedTime</c> 与 <c>StopCount</c></item>
///   <item>D 组 → 验收 d) 含起步损失时间爬升（技术方案01 §3.2b）</item>
///   <item>E 组 → 与冻结接口的对应（快照 / 枚举 / 校验 / 确定性 / 网络驱动）</item>
/// </list>
/// </para>
/// </summary>
public sealed class VehicleTests
{
    private const double Dt = 0.1d;

    // ------------------------------------------------------------------
    // A 组：验收 a) 红灯时车辆在停止线排队且不重叠
    // ------------------------------------------------------------------

    /// <summary>a1：红灯下车辆依次在停止线排队——队首压线、每辆都停、且彼此不重叠（保留最小净距）。</summary>
    [Fact]
    public void A1_Red_Light_Queues_At_Stop_Line_Without_Overlap()
    {
        Lane lane = MakeLane(length: 180d);
        var queue = new Queue(lane);
        List<Vehicle> queued = EnqueueWhile(queue, 6, open: false);
        Settle(queue, open: false, seconds: 60d);

        Assert.Equal(6, queue.Count);
        Assert.Equal(6, queued.Count);

        // 全部停住；队首处于 WaitingToAdvance，其余处于 Queued。
        Assert.Equal(VehicleState.WaitingToAdvance, queue.Head!.State);
        for (int i = 0; i < queue.Vehicles.Count; i++)
        {
            Assert.True(queue.Vehicles[i].Stopped, $"第 {i} 辆未停住。");
            if (i > 0)
            {
                Assert.Equal(VehicleState.Queued, queue.Vehicles[i].State);
            }
        }

        // 队首车头正好压在停止线（= 车道终点）。
        Assert.True(
            Math.Abs(queue.Head!.Position - lane.Length) < 1e-6,
            $"队首应在停止线，实际 pos={queue.Head.Position:F6}，停止线={lane.Length}。");

        // 严格降序 + 不重叠 + 保留最小车头间距。
        for (int i = 1; i < queue.Vehicles.Count; i++)
        {
            Vehicle ahead = queue.Vehicles[i - 1];
            Vehicle behind = queue.Vehicles[i];
            double minLegalFront = ahead.Position - ahead.Length - SimConstants.MinHeadway;

            Assert.True(behind.Position < ahead.Position, "队列未按位置降序。");
            Assert.True(
                behind.Position <= minLegalFront + 1e-9,
                $"第 {i} 辆与前车重叠/净距不足：behind={behind.Position:F6} 允许上限={minLegalFront:F6}。");
        }
    }

    /// <summary>a2：车道排满后入口被堵住（无空位）——<c>TryEnqueue</c> 返回 false（溢出判据）。</summary>
    [Fact]
    public void A2_Entrance_Rejects_When_No_Room()
    {
        Lane lane = MakeLane(length: 30d);   // 仅容 ~5 辆（车长 4.5 + 净距 1.5 = 6m/辆）
        var queue = new Queue(lane);

        int accepted = 0;
        for (int id = 0; id < 50; id++)
        {
            for (int s = 0; s < 4000; s++)
            {
                queue.Step(Dt, false);
            }

            if (!queue.TryEnqueue(new Vehicle(id)))
            {
                break;
            }

            accepted++;
        }

        Assert.True(accepted is > 0 and < 50, $"短车道应被排满，实际接受 {accepted} 辆。");
        Assert.False(queue.TryEnqueue(new Vehicle(999)), "车道已满时入口应拒绝驶入。");
    }

    // ------------------------------------------------------------------
    // B 组：验收 b) 绿灯按饱和流率放行（约 1800 veh/h/lane，误差 ±5%）
    // ------------------------------------------------------------------

    /// <summary>b1：绿灯放行量按饱和流率（1800 veh/h/lane）——长时间放行的均值落在 ±5% 内。</summary>
    [Fact]
    public void B1_Green_Discharges_At_Saturation_Flow()
    {
        const double greenSeconds = 120d;
        List<double> exits = DischargeSeededQueue(count: 100, greenSeconds);

        double vehiclesPerHour = exits.Count / greenSeconds * 3600d;
        double errorPercent = Math.Abs(vehiclesPerHour - 1800d) / 1800d * 100d;

        Assert.True(
            errorPercent <= 5d,
            $"实测饱和流率 {vehiclesPerHour:F1} veh/h（{exits.Count} 辆 / {greenSeconds}s），误差 {errorPercent:F2}% 超出 ±5%。");
    }

    /// <summary>b2：稳态放行的车头时距 ≈ 1/饱和流率 = 2.0 秒。</summary>
    [Fact]
    public void B2_Steady_State_Headway_Is_Saturation_Headway()
    {
        List<double> exits = DischargeSeededQueue(count: 100, greenSeconds: 120d);
        Assert.True(exits.Count >= 20, $"放行数不足以评估车头时距（仅 {exits.Count} 辆）。");

        // 取中段（避开起步爬升与队列尾部）车头时距。
        var intervals = new List<double>();
        for (int i = Math.Max(1, exits.Count / 4); i < exits.Count * 3 / 4; i++)
        {
            intervals.Add(exits[i] - exits[i - 1]);
        }

        Assert.True(intervals.Count > 0, "无可用车头时距样本。");
        intervals.Sort();
        double median = intervals[intervals.Count / 2];

        Assert.True(
            Math.Abs(median - CarFollowing.SaturationHeadway) < 0.05,
            $"稳态车头时距 {median:F3}s 偏离饱和车头时距 {CarFollowing.SaturationHeadway:F3}s。");
    }

    // ------------------------------------------------------------------
    // C 组：验收 c) 每辆车累计 BlockedTime 与 StopCount
    // ------------------------------------------------------------------

    /// <summary>c1：红灯排队期间，每辆车都累计了阻塞时长与至少一次停车。</summary>
    [Fact]
    public void C1_BlockedTime_And_StopCount_Accumulate_On_Red()
    {
        Lane lane = MakeLane(length: 400d);
        var queue = new Queue(lane);
        List<Vehicle> queued = EnqueueWhile(queue, 4, open: false);
        Settle(queue, open: false, seconds: 60d);

        Assert.Equal(4, queued.Count);
        foreach (Vehicle v in queued)
        {
            Assert.True(v.StopCount >= 1, $"车辆 #{v.Id} 的 StopCount 未累计（={v.StopCount}）。");
            Assert.True(v.BlockedTime > 0d, $"车辆 #{v.Id} 的 BlockedTime 未累计（={v.BlockedTime}）。");
        }

        // 阻塞时长 ≈ 停车后的等待时长：队首应显著长于队尾。
        Vehicle head = queued[0];
        Assert.True(head.BlockedTime > SimConstants.StartUpLostTime, "队首排队时长过短。");
    }

    /// <summary>c2：全程自由流的车辆不产生停车、也无阻塞时长。</summary>
    [Fact]
    public void C2_Free_Flow_Vehicle_Does_Not_Stop()
    {
        Lane lane = MakeLane(length: 4000d);
        var queue = new Queue(lane);
        Settle(queue, open: true, seconds: 5d);   // 让起步爬升结束

        var v = new Vehicle(0);
        Assert.True(queue.TryEnqueue(v));
        Settle(queue, open: true, seconds: 2d);   // 2s 前进 < 28m，远未到 4000m 停止线

        Assert.Equal(0, v.StopCount);
        Assert.Equal(0d, v.BlockedTime, 9);
        Assert.False(v.Stopped);
        Assert.Equal(0, queue.QueueLength);
    }

    /// <summary>c3：停车次数按"运动→停"跃迁计数——持续停着不会重复累加。</summary>
    [Fact]
    public void C3_Stop_Count_Counts_Transitions_Not_Continuity()
    {
        Lane lane = MakeLane(length: 180d);
        var queue = new Queue(lane);
        List<Vehicle> queued = EnqueueWhile(queue, 1, open: false);
        Settle(queue, open: false, seconds: 30d);

        Vehicle v = queued[0];
        Assert.Equal(1, v.StopCount);
        double blockedAfterFirstStop = v.BlockedTime;

        Settle(queue, open: false, seconds: 30d);   // 继续红灯 30s

        Assert.Equal(1, v.StopCount);                                // 不重复计数
        Assert.True(v.BlockedTime > blockedAfterFirstStop);          // 阻塞时长继续累加
    }

    // ------------------------------------------------------------------
    // D 组：验收 d) 含起步损失时间爬升（技术方案01 §3.2b）
    // ------------------------------------------------------------------

    /// <summary>d1：起步爬升因子 <c>min(1, t / StartUpLostTime)</c>——绿灯起点为 0，随绿灯时长线性升至 1。</summary>
    [Fact]
    public void D1_StartUp_Capacity_Factor_Ramps_Linearly()
    {
        Assert.Equal(0d, Queue.StartUpCapacityFactor(0d));
        Assert.Equal(0.25d, Queue.StartUpCapacityFactor(0.5d), 9);
        Assert.Equal(0.5d, Queue.StartUpCapacityFactor(1.0d), 9);
        Assert.Equal(1d, Queue.StartUpCapacityFactor(SimConstants.StartUpLostTime), 9);
        Assert.Equal(1d, Queue.StartUpCapacityFactor(100d));
    }

    /// <summary>d2：饱和流率与起步损失时间等常量集中定义且取值正确（T1.1 ④）。</summary>
    [Fact]
    public void D2_Constants_Are_Centralized_And_Correct()
    {
        Assert.Equal(0.5d, SimConstants.SaturationFlowPerLane, 9);
        Assert.Equal(1800d, SimConstants.SaturationFlowPerLane * 3600d, 6);
        Assert.Equal(2.0d, SimConstants.StartUpLostTime);
        Assert.Equal(4.5d, SimConstants.VehicleLength);
        Assert.Equal(12.0d, SimConstants.BusLength);
        Assert.Equal(1.5d, SimConstants.MinHeadway);
        Assert.Equal(2.0d, CarFollowing.SaturationHeadway, 9);
    }

    /// <summary>d3：绿灯刚亮时放行被起步损失时间推迟（首车晚于饱和车头时距才放行），随后进入稳态。</summary>
    [Fact]
    public void D3_First_Discharge_Is_Delayed_By_StartUp_Lost_Time()
    {
        List<double> exits = DischargeSeededQueue(count: 40, greenSeconds: 40d);
        Assert.True(exits.Count >= 5, $"放行样本不足（{exits.Count}）。");

        double first = exits[0];

        // 爬升使首车放行晚于"无爬升"的饱和车头时距（2.0s），体现起步损失。
        Assert.True(
            first >= SimConstants.StartUpLostTime,
            $"首车放行时刻 {first:F3}s 未体现起步损失（应 ≥ {SimConstants.StartUpLostTime}s）。");

        // 但也不会晚太多：爬升结束后应尽快放行。
        Assert.True(
            first <= SimConstants.StartUpLostTime + CarFollowing.SaturationHeadway + 0.2,
            $"首车放行时刻 {first:F3}s 过晚。");

        // 爬升窗口内不放行任何车辆（额度尚未攒满一辆）。
        Assert.DoesNotContain(exits, t => t < SimConstants.StartUpLostTime * 0.5d);
    }

    // ------------------------------------------------------------------
    // E 组：与冻结接口的对应
    // ------------------------------------------------------------------

    /// <summary>e1：<c>VehicleSnapshot</c> / <c>LaneSnapshot</c>（§4.4）字段语义正确。</summary>
    [Fact]
    public void E1_Snapshots_Match_Frozen_Contract()
    {
        Lane lane = MakeLane(length: 180d);
        var queue = new Queue(lane);
        EnqueueWhile(queue, 3, open: false);
        Settle(queue, open: false, seconds: 30d);

        Vehicle head = queue.Head!;
        VehicleSnapshot vs = head.ToSnapshot();

        Assert.Equal(head.Id, vs.Id);
        Assert.Equal(lane.Id, vs.LaneId);
        Assert.Equal(head.Position, vs.Position);
        Assert.Equal(head.Speed, vs.Speed);
        Assert.Equal(head.Stopped, vs.Stopped);
        Assert.Equal(VehicleKind.Car, vs.Kind);

        LaneSnapshot ls = queue.Snapshot();
        Assert.Equal(lane.Id, ls.LaneId);
        Assert.Equal(3, ls.QueueLength);
        Assert.InRange(ls.Occupancy, 0d, 1d);
        Assert.True(ls.Occupancy > 0d);
    }

    /// <summary>
    /// e2：<c>VehicleKind</c>（§4.4）成员名称与顺序与契约逐字一致；公交按加长车长建模。
    /// <para>T4.3 S1b 起 <c>Truck</c> 追加在末尾（§11 变更）：<c>Car=0</c> / <c>Bus=1</c> 序数不得变。</para>
    /// </summary>
    [Fact]
    public void E2_VehicleKind_Matches_Frozen_Contract()
    {
        Assert.Equal(["Car", "Bus", "Truck"], Enum.GetNames<VehicleKind>());
        Assert.Equal(0, (int)VehicleKind.Car);
        Assert.Equal(1, (int)VehicleKind.Bus);
        Assert.Equal(2, (int)VehicleKind.Truck);

        Assert.Equal(SimConstants.VehicleLength, new Vehicle(0, VehicleKind.Car).Length);
        Assert.Equal(SimConstants.BusLength, new Vehicle(1, VehicleKind.Bus).Length);
    }

    /// <summary>e3：非法输入一律抛异常（契约 §1.3：不静默吞掉）。</summary>
    [Fact]
    public void E3_Input_Validation_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new Queue(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Vehicle(-1));

        var queue = new Queue(MakeLane());
        Assert.Throws<ArgumentNullException>(() => queue.TryEnqueue(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Step(0d, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.Step(-0.1d, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.TryPlace(new Vehicle(0), -1d));
        Assert.Throws<ArgumentOutOfRangeException>(() => queue.TryPlace(new Vehicle(0), 1d, -1d));
    }

    /// <summary>e4：队列由**编译后的路网车道**驱动（复用 <c>Network / Turn / Lane / CompiledNetwork</c>，§2.3/2.4/2.8）。</summary>
    [Fact]
    public void E4_Queue_Is_Driven_By_Compiled_Network_Lane()
    {
        Network network = new()
        {
            Nodes =
            [
                new Node { Id = 0, Type = NodeType.Signalized, X = 0, Y = 0 },
                new Node { Id = 1, Type = NodeType.Origin, X = 0, Y = -180 },
            ],
            Lanes =
            [
                new Lane { Id = 0, Road = 0, Type = LaneType.Driving, FromNode = 1, ToNode = 0, Length = 180d, SpeedLimit = 13.89d, LaneIndex = 0 },
                new Lane { Id = 1, Road = 0, Type = LaneType.Driving, FromNode = 0, ToNode = 1, Length = 180d, SpeedLimit = 13.89d, LaneIndex = 0 },
            ],
            Turns =
            [
                new Turn { Id = 0, SrcLane = 0, DstLane = 1, Node = 0, Type = TurnType.Straight },
            ],
        };

        CompiledNetwork compiled = network.Compile();
        Lane inbound = compiled.GetLane(0);
        var queue = new Queue(inbound);
        Assert.Equal(0, queue.LaneId);
        Assert.Equal(0, compiled.OutgoingTurns(0).Single());

        List<Vehicle> queued = EnqueueWhile(queue, 5, open: false);
        Settle(queue, open: false, seconds: 60d);

        Assert.Equal(5, queue.Count);
        Assert.All(queued, v => Assert.True(v.Stopped));
        Assert.Equal(inbound.Length, queue.Head!.Position, 6);

        // 放行前后对比：绿灯放行数 > 0，且车道开始腾空。
        int before = queue.Count;
        int released = 0;
        for (int s = 0; s < (int)(20d / Dt); s++)
        {
            queue.Step(Dt, true);
            released += queue.DrainDeparted().Count;
        }

        Assert.True(released > 0, "绿灯未放行任何车辆。");
        Assert.True(queue.Count < before, "绿灯放行后车道车辆数未减少。");
    }

    /// <summary>e5：同一场景两次运行结果逐项一致（铁律 2 确定性）。</summary>
    [Fact]
    public void E5_Replay_Is_Deterministic()
    {
        (List<double> firstExits, List<double> firstPositions) = RunReplay(seedCount: 20, seconds: 40d);
        (List<double> secondExits, List<double> secondPositions) = RunReplay(seedCount: 20, seconds: 40d);

        Assert.Equal(firstExits.Count, secondExits.Count);
        for (int i = 0; i < firstExits.Count; i++)
        {
            Assert.Equal(firstExits[i], secondExits[i], 9);
        }

        Assert.Equal(firstPositions.Count, secondPositions.Count);
        for (int i = 0; i < firstPositions.Count; i++)
        {
            Assert.Equal(firstPositions[i], secondPositions[i], 9);
        }
    }

    // ------------------------------------------------------------------
    // 工具
    // ------------------------------------------------------------------

    private static Lane MakeLane(int id = 0, double length = 180d, double speedLimit = 13.89d) => new()
    {
        Id = id,
        Road = 0,
        Type = LaneType.Driving,
        FromNode = 1,
        ToNode = 0,
        Length = length,
        SpeedLimit = speedLimit,
        LaneIndex = 0,
    };

    /// <summary>逐步推进并注入车辆，直到成功放入 <paramref name="count"/> 辆（返回被注入的车辆，按 id 升序）。</summary>
    private static List<Vehicle> EnqueueWhile(Queue queue, int count, bool open, int maxSteps = 200_000)
    {
        var accepted = new List<Vehicle>();
        int nextId = 0;
        int steps = 0;
        while (accepted.Count < count && steps < maxSteps)
        {
            if (nextId < count)
            {
                var v = new Vehicle(nextId);
                if (queue.TryEnqueue(v))
                {
                    accepted.Add(v);
                    nextId++;
                }
            }

            queue.Step(Dt, open);
            steps++;
        }

        return accepted;
    }

    /// <summary>在队列尾部铺一条**静止的满排队**（车头依次落在 停止线, 停止线−6, 停止线−12, …）。</summary>
    private static List<Vehicle> SeedPackedQueue(Queue queue, int count)
    {
        var seeded = new List<Vehicle>();
        double spacing = CarFollowing.JamSpacingOf(VehicleKind.Car);
        for (int i = 0; i < count; i++)
        {
            var v = new Vehicle(i);
            bool placed = queue.TryPlace(v, queue.Lane.Length - (i * spacing));
            Assert.True(placed, $"铺第 {i} 辆车失败（位置 {queue.Lane.Length - (i * spacing):F2}）。");
            seeded.Add(v);
        }

        return seeded;
    }

    private static void Settle(Queue queue, bool open, double seconds)
    {
        int steps = (int)Math.Round(seconds / Dt);
        for (int i = 0; i < steps; i++)
        {
            queue.Step(Dt, open);
        }
    }

    /// <summary>铺一条满排队后放行 <paramref name="greenSeconds"/> 秒，返回各车驶离时刻（升序）。</summary>
    private static List<double> DischargeSeededQueue(int count, double greenSeconds)
    {
        Lane lane = MakeLane(length: 4000d);
        var queue = new Queue(lane);
        SeedPackedQueue(queue, count);

        var exitTimes = new List<double>();
        int steps = (int)Math.Round(greenSeconds / Dt);
        for (int s = 0; s < steps; s++)
        {
            queue.Step(Dt, true);
            foreach (Vehicle departed in queue.DrainDeparted())
            {
                exitTimes.Add(departed.ExitTime);
            }
        }

        exitTimes.Sort();
        return exitTimes;
    }

    private static (List<double> Exits, List<double> Positions) RunReplay(int seedCount, double seconds)
    {
        Lane lane = MakeLane(length: 4000d);
        var queue = new Queue(lane);
        SeedPackedQueue(queue, seedCount);

        var exits = new List<double>();
        int steps = (int)Math.Round(seconds / Dt);
        for (int s = 0; s < steps; s++)
        {
            queue.Step(Dt, true);
            foreach (Vehicle departed in queue.DrainDeparted())
            {
                exits.Add(departed.ExitTime);
            }
        }

        var positions = new List<double>();
        foreach (Vehicle v in queue.Vehicles)
        {
            positions.Add(v.Position);
        }

        return (exits, positions);
    }
}
