using SimCore.Io;
using SimCore.Model;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

/// <summary>
/// T4.3 · S1b「车型构成轻微影响仿真」验收测试。
/// <para>口径（<c>docs/T4.3-编辑器补强执行计划.md</c> §3.2）：
/// <list type="bullet">
///   <item>车长：小汽车 4.5 / 货车 7.0 / 公交 12.0（米）</item>
///   <item>速度系数：1.00 / 0.85 / 0.95（× 车道限速）</item>
///   <item>队列容量：<c>floor(车道长 / 期望车头间距)</c>；纯小汽车时恰为 6.0 → 与 T4.3 之前逐位一致</item>
///   <item>车型选择：纯函数 <see cref="VehicleMixRules.PickKind"/>，同参数恒同结果，无系统随机</item>
/// </list>
/// </para>
/// </summary>
public sealed class VehicleMixTests
{
    private const double Dt = 0.1d;

    // ------------------------------------------------------------------
    // A 组：比例与期望间距
    // ------------------------------------------------------------------

    /// <summary>a1：五个预设的期望车头间距（纯小汽车必须恰为 6.0，这是"零影响"的根基）。</summary>
    [Theory]
    [InlineData(null, 6.0)]
    [InlineData(VehicleMix.CarOnly, 6.0)]
    [InlineData(VehicleMix.CityMix, 6.525)]
    [InlineData(VehicleMix.FreightHeavy, 7.125)]
    [InlineData(VehicleMix.BusHeavy, 7.75)]
    [InlineData(VehicleMix.CarBusMix, 6.6)]
    public void A1_ExpectedJamSpacing_Matches_Presets(VehicleMix? mix, double expected)
    {
        Assert.Equal(expected, VehicleMixRules.ExpectedJamSpacing(mix), 6);
    }

    /// <summary>a2：未设置 / <c>CarOnly</c> 时永远不会出现货车或公交。</summary>
    [Fact]
    public void A2_CarOnly_Picks_Only_Cars()
    {
        foreach (VehicleMix? mix in new VehicleMix?[] { null, VehicleMix.CarOnly })
        {
            for (int seq = 0; seq < 200; seq++)
            {
                Assert.Equal(VehicleKind.Car, VehicleMixRules.PickKind(mix, laneId: 0, seq));
                Assert.Equal(VehicleKind.Car, VehicleMixRules.PickKind(mix, laneId: 7, seq));
            }
        }
    }

    /// <summary>a3：每个预设跑 1000 辆，各车型数量**精确**等于千分位比例。</summary>
    [Theory]
    [InlineData(VehicleMix.CityMix, 850, 120, 30)]
    [InlineData(VehicleMix.FreightHeavy, 550, 450, 0)]
    [InlineData(VehicleMix.BusHeavy, 700, 100, 200)]
    [InlineData(VehicleMix.CarBusMix, 920, 0, 80)]
    public void A3_Composition_Matches_Preset_Exactly(VehicleMix mix, int cars, int trucks, int buses)
    {
        var counts = new Dictionary<VehicleKind, int>();
        for (int seq = 0; seq < VehicleMixRules.Permille; seq++)
        {
            VehicleKind kind = VehicleMixRules.PickKind(mix, laneId: 0, seq);
            counts[kind] = counts.GetValueOrDefault(kind) + 1;
        }

        Assert.Equal(cars, counts.GetValueOrDefault(VehicleKind.Car));
        Assert.Equal(trucks, counts.GetValueOrDefault(VehicleKind.Truck));
        Assert.Equal(buses, counts.GetValueOrDefault(VehicleKind.Bus));
    }

    /// <summary>a4：纯函数——同参数恒同结果；不同车道错相（序列不完全相同）。</summary>
    [Fact]
    public void A4_PickKind_Is_Deterministic_And_Lane_Phased()
    {
        for (int seq = 0; seq < 50; seq++)
        {
            Assert.Equal(
                VehicleMixRules.PickKind(VehicleMix.CityMix, 3, seq),
                VehicleMixRules.PickKind(VehicleMix.CityMix, 3, seq));
        }

        var lane0 = Enumerable.Range(0, 20).Select(s => VehicleMixRules.PickKind(VehicleMix.CityMix, 0, s)).ToList();
        var lane1 = Enumerable.Range(0, 20).Select(s => VehicleMixRules.PickKind(VehicleMix.CityMix, 1, s)).ToList();
        Assert.NotEqual(lane0, lane1);
    }

    // ------------------------------------------------------------------
    // B 组：车型真的进入仿真（车长 / 速度 / 容量）
    // ------------------------------------------------------------------

    /// <summary>b1：货车有独立车长；<c>VehicleKind</c> 只末尾追加，<c>Bus</c> 序数仍为 1。</summary>
    [Fact]
    public void B1_Truck_Has_Own_Length_And_Appends_At_End()
    {
        Assert.Equal(0, (int)VehicleKind.Car);
        Assert.Equal(1, (int)VehicleKind.Bus);      // 冻结序数，不得动
        Assert.Equal(2, (int)VehicleKind.Truck);

        Assert.Equal(SimConstants.VehicleLength, new Vehicle(0, VehicleKind.Car).Length);
        Assert.Equal(SimConstants.TruckLength, new Vehicle(1, VehicleKind.Truck).Length);
        Assert.Equal(SimConstants.BusLength, new Vehicle(2, VehicleKind.Bus).Length);
        Assert.Equal(7.0, SimConstants.TruckLength, 6);
    }

    /// <summary>b2：速度系数只缩放目标速度——同一条 10 m/s 车道，小汽车跑满 10，货车封顶 8.5。</summary>
    [Fact]
    public void B2_Truck_Speed_Is_Scaled_By_Factor()
    {
        var carQueue = new Queue(MakeLane(length: 4000d, speedLimit: 10d));
        var truckQueue = new Queue(MakeLane(length: 4000d, speedLimit: 10d));

        var car = new Vehicle(0, VehicleKind.Car);
        var truck = new Vehicle(1, VehicleKind.Truck);
        Assert.True(carQueue.TryPlace(car, 0d, speed: 0d));       // 从静止起步，走 Step 里的目标速度分支
        Assert.True(truckQueue.TryPlace(truck, 0d, speed: 0d));

        for (int i = 0; i < 100; i++)   // 10 秒，足够加速到位
        {
            carQueue.Step(Dt, open: true);
            truckQueue.Step(Dt, open: true);
        }

        Assert.Equal(10.0, car.Speed, 6);
        Assert.Equal(8.5, truck.Speed, 6);
    }

    /// <summary>b3：队列容量按期望车头间距算——纯小汽车 30 辆，货运为主 25 辆（180m 车道）。</summary>
    [Fact]
    public void B3_Queue_Capacity_Follows_Mix_Spacing()
    {
        Assert.Equal(30, new Queue(MakeLane(length: 180d)).Capacity);
        Assert.Equal(30, new Queue(MakeLane(length: 180d), 0d).Capacity);
        Assert.Equal(30, new Queue(MakeLane(length: 180d), 6.0d).Capacity);
        Assert.Equal(25, new Queue(MakeLane(length: 180d), VehicleMixRules.ExpectedJamSpacing(VehicleMix.FreightHeavy)).Capacity);
        Assert.Equal(27, new Queue(MakeLane(length: 180d), VehicleMixRules.ExpectedJamSpacing(VehicleMix.CityMix)).Capacity);
        Assert.Equal(23, new Queue(MakeLane(length: 180d), VehicleMixRules.ExpectedJamSpacing(VehicleMix.BusHeavy)).Capacity);
        Assert.Equal(27, new Queue(MakeLane(length: 180d), VehicleMixRules.ExpectedJamSpacing(VehicleMix.CarBusMix)).Capacity);
    }

    /// <summary>b4：货车排队时占用更长车头间距（8.5m vs 6.0m）——同一条 180m 车道少停 5 辆。</summary>
    [Fact]
    public void B4_Trucks_Take_Longer_Jam_Spacing()
    {
        var lane = MakeLane(length: 180d, speedLimit: 10d);
        var queue = new Queue(lane, VehicleMixRules.ExpectedJamSpacing(VehicleMix.FreightHeavy));

        int accepted = 0;
        for (int id = 0; id < 40; id++)
        {
            for (int s = 0; s < 2000; s++)
            {
                queue.Step(Dt, open: false);
            }

            if (queue.TryEnqueue(new Vehicle(id, VehicleKind.Truck)))
            {
                accepted++;
            }
        }

        Assert.True(accepted is >= 18 and <= 23, $"货车应停 20 辆上下，实际 {accepted} 辆。");

        // 任意相邻两辆仍不重叠，且净距按**前车实际车长**（货车 7m）保留。
        for (int i = 1; i < queue.Vehicles.Count; i++)
        {
            Vehicle ahead = queue.Vehicles[i - 1];
            Vehicle behind = queue.Vehicles[i];
            Assert.True(behind.Position <= ahead.Position - ahead.Length - SimConstants.MinHeadway + 1e-6);
        }
    }

    /// <summary>b5：装配链打通——<c>Level.GameRules.VehicleMix</c> 真的被 <c>SimEngine</c> 吃进队列容量。</summary>
    [Fact]
    public void B5_Engine_Reads_Level_VehicleMix()
    {
        SimEngine plain = new(MakeLevel(null), MakeConfig());
        SimEngine freight = new(MakeLevel(VehicleMix.FreightHeavy), MakeConfig());
        SimEngine city = new(MakeLevel(VehicleMix.CityMix), MakeConfig());

        Assert.Equal(30, plain.Queues[0].Capacity);      // 180 / 6.0
        Assert.Equal(25, freight.Queues[0].Capacity);   // 180 / 7.125
        Assert.Equal(27, city.Queues[0].Capacity);      // 180 / 6.525
    }

    // ------------------------------------------------------------------
    // 夹具
    // ------------------------------------------------------------------
    private static Level MakeLevel(VehicleMix? mix) => new()
    {
        FormatVersion = Spmap.CurrentFormatVersion,
        Meta = new LevelMeta { Name = "mix", Author = "test", Chapter = 4, Index = 7 },
        Network = new Network
        {
            Nodes = new[]
            {
                new Node { Id = 0, Type = NodeType.Destination, X = 0, Y = 0 },
                new Node { Id = 1, Type = NodeType.Origin, X = 0, Y = -180 },
            },
            Lanes = new[] { MakeLane(length: 180d) },
            Turns = Array.Empty<Turn>(),
        },
        GameRules = new GameRules { VehicleMix = mix },
    };

    private static SimConfig MakeConfig() => new()
    {
        Dt = Dt,
        SpeedMultiplier = 1,
        Deterministic = true,
        Seed = 20261020,
        Duration = 60d,
        Timing = SignalTiming.Default,
        QueueCapacityPerLane = 20,
    };

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
}
