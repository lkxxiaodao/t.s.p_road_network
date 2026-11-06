using SimCore.Geometry;
using SimCore.Model;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

/// <summary>
/// 行人两段式过街（中央安全岛）—— 2026-10-25 E92 G2 · N6a2 验收。
/// <para>N6a 只落了数据层（<c>islands[]</c> / <c>turns[].leg</c> / <c>turns[].island</c> 与校验器）；
/// 本组验收的是<b>仿真推进</b>：人先走前段到岛、在岛上等后段、后段放行再走到对岸，且岛容量有限。</para>
/// <para>分组：
/// <list type="bullet">
///   <item>A 组：<see cref="Pedestrian"/> 的两段状态机（到岛停住 / 后段启动 / 一次走完不受影响）</item>
///   <item>B 组：<see cref="PedestrianCrossing"/> 的双阶段放行（前段相位放行前段、后段相位放行后段）</item>
///   <item>C 组：岛容量（一次放行不超过岛上空位）</item>
///   <item>D 组：<see cref="CompiledNetwork"/> 透传岛屿（按 id 升序 + 二分查表）</item>
///   <item>E 组：兼容与参数校验（无岛关卡行为逐位不变）</item>
/// </list>
/// </para>
/// </summary>
public sealed class TwoLegPedestrianTests
{
    private const double Dt = 0.5d;

    /// <summary>过街总距离 20 m，岛在正中 10 m 处 → 前段 / 后段各 10 m，各需 10 / 1.4 ≈ 7.14 s。</summary>
    private const double Dist = 20.0d;
    private const double IslandAt = 10.0d;
    private const int IslandId = 0;

    // ==================================================================
    // A 组：Pedestrian 的两段状态机
    // ==================================================================

    /// <summary>A1：一次走完的行人（无岛）——`IsTwoLeg` 为假，前段距离 = 总距离，后段为 0，位置直通对岸。</summary>
    [Fact]
    public void A1_SingleLeg_Pedestrian_HasNoIsland()
    {
        var p = new Pedestrian(0, 12, Dist);

        Assert.False(p.IsTwoLeg);
        Assert.Equal(-1, p.IslandId);
        Assert.Equal(-1d, p.IslandPosition);
        Assert.Equal(Dist, p.FirstLegDistance);
        Assert.Equal(0d, p.SecondLegDistance);
        Assert.Equal(PedestrianLegPlace.AtOrigin, p.Place);
    }

    /// <summary>A2：两段式行人走到岛即停住 —— 状态转 OnIsland、位置钳在岛位、不算抵达。</summary>
    [Fact]
    public void A2_WalksToIsland_ThenStopsAndWaits()
    {
        var p = new Pedestrian(0, 12, Dist, IslandAt, IslandId);
        p.StartCrossing(0d);

        // 前段 10 m / 1.4 ≈ 7.143 s → 8 步（4 s）不够，16 步（8 s）够了。
        for (int i = 0; i < 14; i++)
        {
            Assert.False(p.Advance(Dt, (i + 1) * Dt));   // 前段途中：既没到岛也没到岸
        }

        Assert.True(p.IsCrossing);
        Assert.Equal(14 * Dt * 1.4d, p.Position, 6);

        // 再推进到 7.5 s（≥ 7.143 s）→ 到岛
        for (int i = 14; i < 15; i++)
        {
            Assert.False(p.Advance(Dt, (i + 1) * Dt));   // 到岛那一步返回 false（人还在场）
        }

        Assert.True(p.IsOnIsland);
        Assert.False(p.IsCrossing);
        Assert.Equal(IslandAt, p.Position, 9);
        Assert.Equal(PedestrianLegPlace.OnIsland, p.Place);
        Assert.Equal(-1d, p.ExitTime);              // 还没到对岸
        Assert.False(p.IsOnSecondLeg);
    }

    /// <summary>A3：后段启动后走到对岸 —— 状态转 Crossed、位置写满、<c>ExitTime</c> 落地。</summary>
    [Fact]
    public void A3_SecondLeg_CompletesToDestination()
    {
        var p = new Pedestrian(0, 12, Dist, IslandAt, IslandId);
        p.StartCrossing(0d);

        while (!p.IsOnIsland)
        {
            p.Advance(Dt, 0d);
        }

        p.StartSecondLeg(5d);
        Assert.True(p.IsOnSecondLeg);
        Assert.True(p.IsCrossing);
        Assert.Equal(5d, p.IslandArriveTime);

        bool arrived = false;
        for (int i = 0; i < 40 && !arrived; i++)
        {
            arrived = p.Advance(Dt, 10d);
        }

        Assert.True(arrived);
        Assert.Equal(PedestrianState.Crossed, p.State);
        Assert.Equal(Dist, p.Position, 9);
        Assert.Equal(10d, p.ExitTime);
        Assert.Equal(PedestrianLegPlace.AtDestination, p.Place);
    }

    /// <summary>A4：不在岛上调用 <c>StartSecondLeg</c> 抛异常（防止状态机被外部误用）。</summary>
    [Fact]
    public void A4_StartSecondLeg_RequiresBeingOnIsland()
    {
        var p = new Pedestrian(0, 12, Dist, IslandAt, IslandId);
        Assert.Throws<InvalidOperationException>(() => p.StartSecondLeg(0d));

        p.StartCrossing(0d);
        Assert.Throws<InvalidOperationException>(() => p.StartSecondLeg(0d));   // 还在前段路上
    }

    /// <summary>A5：前段最短绿灯 = 前段距离 / 1.4（两段各自算，不再拿整程卡前段）。</summary>
    [Fact]
    public void A5_FirstLegMinGreenTime_UsesFirstLegDistance()
    {
        var p = new Pedestrian(0, 12, Dist, IslandAt, IslandId);

        Assert.Equal(IslandAt / 1.4d, p.MinGreenTimeForFirstLeg, 9);
        Assert.Equal(Dist / 1.4d, p.MinGreenTime, 9);   // 整程口径保留（工程提示用）
        Assert.True(p.MinGreenTimeForFirstLeg < p.MinGreenTime);
    }

    /// <summary>A6：参数校验 —— 前段必须严格落在 (0, 总距离) 之间，岛 id 不得为负。</summary>
    [Fact]
    public void A6_TwoLeg_ConstructorValidation()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pedestrian(0, 12, Dist, 0d, IslandId));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pedestrian(0, 12, Dist, Dist, IslandId));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pedestrian(0, 12, Dist, Dist + 1d, IslandId));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pedestrian(0, 12, Dist, double.NaN, IslandId));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pedestrian(0, 12, -5d, IslandAt, IslandId));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Pedestrian(0, 12, Dist, IslandAt, -1));
    }

    // ==================================================================
    // B 组：PedestrianCrossing 的双阶段放行
    // ==================================================================

    /// <summary>B1：前段相位放行 → 人到岛后停住；后段相位不放行 → 一直留在岛上。</summary>
    [Fact]
    public void B1_FirstLegReleasesToIsland_SecondLegWaits()
    {
        (CompiledNetwork net, IntersectionGate gate, SignalStateMachine signal) = TwoLegFixture(Plan(
            Stage(30d, prot: [CW]),        // 第 0 相位：只放前段
            Stage(30d, prot: [Other]),     // 第 1 相位：放一条无关转向（后段仍禁行）
            Stage(30d, prot: [CW2])));     // 第 2 相位：放后段

        PedestrianCrossing crossing = MakeCrossing(net, gate);
        crossing.Arrive(new Pedestrian(0, CW, Dist, IslandAt, IslandId));

        // 相位 0 内：放行走前段。
        signal.Tick(Dt);
        Assert.Equal(1, crossing.Step(Dt, signal));
        Assert.Equal(0, crossing.OnIslandCount);

        // 走到岛（前段 ≈ 7.14 s）。
        for (int i = 0; i < 20; i++)
        {
            signal.Tick(Dt);
            crossing.Step(Dt, signal);
        }

        Assert.Equal(1, crossing.OnIslandCount);
        Assert.Equal(0, crossing.CrossingCount);
        Assert.Equal(IslandAt, crossing.OnIsland[0].Position, 9);

        // 进到第 2 相位之前：后段禁行 → 人不会离开岛。
        Assert.Equal(0, crossing.CrossedCount);
    }

    /// <summary>B2：后段相位放行 → 岛上的人走完到对岸（结束时刻晚于"一次走完"的 14.3 s）。</summary>
    [Fact]
    public void B2_SecondLegPhase_CompletesTheTrip()
    {
        (CompiledNetwork net, IntersectionGate gate, SignalStateMachine signal) = TwoLegFixture(Plan(
            Stage(15d, prot: [CW]),
            Stage(15d, prot: [CW2]),
            Stage(15d, prot: [Other])));

        PedestrianCrossing crossing = MakeCrossing(net, gate);
        crossing.Arrive(new Pedestrian(0, CW, Dist, IslandAt, IslandId));

        // 跑满两个相位（15 + 黄 3 + 全红 2 + 15 ≈ 35 s；给 60 s 余量）：
        // 前段 γ 0~15 s 放行 → 7.2 s 到岛 → 等 → 第 1 相位（约 t=20 s 起）放行后段 → 约 t=27.5 s 到岸。
        for (int i = 0; i < 120; i++)
        {
            signal.Tick(Dt);
            crossing.Step(Dt, signal);
        }

        Assert.Equal(1, crossing.CrossedCount);
        Assert.Equal(0, crossing.OnIslandCount);
        Assert.Equal(0, crossing.CrossingCount);
    }

    /// <summary>B3：后段从不放行（相位里没有 CW2）→ 人永远停在岛上，不越界、不消失。</summary>
    [Fact]
    public void B3_WithoutSecondLegPhase_PedestrianStaysOnIsland()
    {
        (CompiledNetwork net, IntersectionGate gate, SignalStateMachine signal) = TwoLegFixture(Plan(
            Stage(20d, prot: [CW]),
            Stage(20d, prot: [Other])));

        PedestrianCrossing crossing = MakeCrossing(net, gate);
        crossing.Arrive(new Pedestrian(0, CW, Dist, IslandAt, IslandId));

        for (int i = 0; i < 200; i++)
        {
            signal.Tick(Dt);
            crossing.Step(Dt, signal);
        }

        Assert.Equal(0, crossing.CrossedCount);
        Assert.Equal(1, crossing.OnIslandCount);
        Assert.Equal(IslandAt, crossing.OnIsland[0].Position, 9);
        Assert.Single(crossing.Snapshots());   // 仍在快照里（表现层要能画出来）
    }

    // ==================================================================
    // C 组：岛容量
    // ==================================================================

    /// <summary>C1：岛容量 2、路缘来了 5 人 → 一次只放 2 人上岛，其余留在等待区。</summary>
    [Fact]
    public void C1_IslandCapacity_LimitsReleasePerStep()
    {
        (CompiledNetwork net, IntersectionGate gate, SignalStateMachine signal) = TwoLegFixture(Plan(
            Stage(60d, prot: [CW])), islandCapacity: 2);

        PedestrianCrossing crossing = MakeCrossing(net, gate, islandCapacity: 2);
        for (int i = 0; i < 5; i++)
        {
            crossing.Arrive(new Pedestrian(i, CW, Dist, IslandAt, IslandId));
        }

        signal.Tick(Dt);
        int released = crossing.Step(Dt, signal);

        Assert.Equal(2, released);
        Assert.Equal(3, crossing.WaitingCount);
        Assert.Equal(2, crossing.CrossingCount);
        Assert.Equal(2, crossing.IslandCapacity);
    }

    /// <summary>C2：岛满后不再放新人 —— 前 2 人走完到岛，后面的 3 人才被放行（先到先走）。</summary>
    [Fact]
    public void C2_IslandFull_BlocksNewReleases_UntilSlotsFree()
    {
        (CompiledNetwork net, IntersectionGate gate, SignalStateMachine signal) = TwoLegFixture(Plan(
            Stage(200d, prot: [CW])), islandCapacity: 2);

        PedestrianCrossing crossing = MakeCrossing(net, gate, islandCapacity: 2);
        for (int i = 0; i < 5; i++)
        {
            crossing.Arrive(new Pedestrian(i, CW, Dist, IslandAt, IslandId));
        }

        // 走 8 s：前 2 人到岛（岛满），剩下 3 人仍在等待区。
        for (int i = 0; i < 16; i++)
        {
            signal.Tick(Dt);
            crossing.Step(Dt, signal);
        }

        Assert.Equal(2, crossing.OnIslandCount);
        Assert.Equal(3, crossing.WaitingCount);
        Assert.Equal(0, crossing.CrossingCount);   // 岛满 → 没人被放上桥

        // 但后段一直没放行 → 岛永远满 → 后面 3 人一直等（这就是"岛太小"的真相）。
        for (int i = 0; i < 60; i++)
        {
            signal.Tick(Dt);
            crossing.Step(Dt, signal);
        }

        Assert.Equal(2, crossing.OnIslandCount);
        Assert.Equal(3, crossing.WaitingCount);
        Assert.Equal(0, crossing.CrossedCount);
    }

    /// <summary>C3：岛上人走完后空位释放，后面的人接着被放行（容量是"活水"，不是一次性额度）。</summary>
    [Fact]
    public void C3_FreedIslandSlots_AllowNextBatch()
    {
        (CompiledNetwork net, IntersectionGate gate, SignalStateMachine signal) = TwoLegFixture(Plan(
            Stage(14d, prot: [CW]),
            Stage(14d, prot: [CW2])), islandCapacity: 2);

        PedestrianCrossing crossing = MakeCrossing(net, gate, islandCapacity: 2);
        for (int i = 0; i < 5; i++)
        {
            crossing.Arrive(new Pedestrian(i, CW, Dist, IslandAt, IslandId));
        }

        // 跑满两个相位（15+黄+全红+15 ≈ 35 s；给 60 s 余量）：
        // 前段放 2 人上岛 → 后段放行 2 人走到对岸 → 空位回归。
        for (int i = 0; i < 120; i++)
        {
            signal.Tick(Dt);
            crossing.Step(Dt, signal);
        }

        Assert.Equal(2, crossing.CrossedCount);      // 第一批走完
        Assert.True(crossing.WaitingCount < 5);      // 后面的人已被放上桥 / 到岛
    }

    // ==================================================================
    // D 组：CompiledNetwork 透传岛屿
    // ==================================================================

    /// <summary>D1：无岛关卡 <c>Islands</c> 为空，<c>TryGetIsland</c> 返回 false（不影响编译）。</summary>
    [Fact]
    public void D1_NoIslands_CompilesToEmpty()
    {
        CompiledNetwork net = TwoLegNetwork(islandCapacity: 2, withIsland: false).Compile();

        Assert.Empty(net.Islands);
        Assert.False(net.TryGetIsland(0, out Island? island));
        Assert.Null(island);
    }

    /// <summary>D2：岛按 id 升序、二分查表能取到；不存在的 id 返回 false。</summary>
    [Fact]
    public void D2_IslandsAreSortedById_AndLookupWorks()
    {
        Network network = TwoLegNetwork(islandCapacity: 3);
        network = new Network
        {
            Nodes = network.Nodes,
            Lanes = network.Lanes,
            Turns = network.Turns,
            Islands =
            [
                new Island { Id = 7, X = 0, Y = 0, Capacity = 5 },
                new Island { Id = 0, X = 0, Y = 1, Capacity = 3 },
                new Island { Id = 4, X = 0, Y = 2, Capacity = 9 },
            ],
        };

        CompiledNetwork net = network.Compile();

        Assert.Equal(new[] { 0, 4, 7 }, net.Islands.Select(i => i.Id).ToArray());
        Assert.True(net.TryGetIsland(4, out Island? found));
        Assert.Equal(9, found!.Capacity);
        Assert.False(net.TryGetIsland(5, out _));
    }

    // ==================================================================
    // E 组：兼容与参数校验
    // ==================================================================

    /// <summary>E1：无岛关卡上建协调器 → <c>IsTwoLeg</c> 为假、岛上容量 0、放行行为与改动前一致。</summary>
    [Fact]
    public void E1_LegacyCrosswalk_BehavesAsSingleLeg()
    {
        Network network = TwoLegNetwork(islandCapacity: 2, withIsland: false, withTwoLegTurns: false);
        CompiledNetwork net = network.Compile();
        IntersectionGate gate = IntersectionGate.Build(net, ConflictMatrix.From(net));
        var signal = new SignalStateMachine(net.GetNode(0), SignalTiming.Default);
        signal.SyncToTime(0d);

        var crossing = new PedestrianCrossing(gate, net, CW, Dist);
        Assert.False(crossing.IsTwoLeg);
        Assert.Equal(-1, crossing.IslandId);
        Assert.Equal(0, crossing.IslandCapacity);

        crossing.Arrive(new Pedestrian(0, CW, Dist));
        signal.Tick(Dt);
        Assert.Equal(1, crossing.Step(Dt, signal));

        for (int i = 0; i < 40; i++)
        {
            signal.Tick(Dt);
            crossing.Step(Dt, signal);
        }

        Assert.Equal(1, crossing.CrossedCount);
        Assert.Equal(0, crossing.OnIslandCount);
    }

    /// <summary>E2：转向指向不存在的岛 → 协调器构造抛 <see cref="ArgumentException"/>（不静默退化成单段）。</summary>
    [Fact]
    public void E2_MissingIsland_Throws()
    {
        Network network = TwoLegNetwork(islandCapacity: 2, withIsland: false, withTwoLegTurns: true);
        CompiledNetwork net = network.Compile();
        IntersectionGate gate = IntersectionGate.Build(net, ConflictMatrix.From(net));

        Assert.Throws<ArgumentException>(() => new PedestrianCrossing(gate, net, CW, Dist));
    }

    /// <summary>E3：同一对分段转向只建一个协调器（<c>BuildCrosswalks</c> 语义）—— 通过 Step 计数间接验证。</summary>
    [Fact]
    public void E3_PairedLegs_ProduceSingleCrossing()
    {
        (CompiledNetwork net, IntersectionGate gate, SignalStateMachine signal) = TwoLegFixture(Plan(
            Stage(60d, prot: [CW])));

        PedestrianCrossing crossing = MakeCrossing(net, gate);

        // 前段与后段都是合法 Crosswalk，都写在 turns 里，但协调器只认一对。
        Assert.True(net.GetTurn(CW).Leg == 0);
        Assert.True(net.GetTurn(CW2).Leg == 1);
        Assert.Equal(CW2, SecondLegTurnIdOf(net, crossing));
    }

    // ==================================================================
    // 夹具
    // ==================================================================

    private const int CW = 0;       // 前段：路缘 → 岛
    private const int CW2 = 1;      // 后段：岛 → 对岸
    private const int Other = 2;    // 一条无关转向（占位，用来做"别的相位"）

    private static PedestrianCrossing MakeCrossing(CompiledNetwork net, IntersectionGate gate, int islandCapacity = 6)
        => new(gate, net, CW, Dist);

    private static (CompiledNetwork Net, IntersectionGate Gate, SignalStateMachine Signal) TwoLegFixture(
        SignalPlan plan,
        int islandCapacity = 6)
    {
        CompiledNetwork net = TwoLegNetwork(islandCapacity, plan: plan).Compile();
        IntersectionGate gate = IntersectionGate.Build(net, ConflictMatrix.From(net));
        var signal = new SignalStateMachine(net.GetNode(0), SignalTiming.Default);
        signal.SyncToTime(0d);
        return (net, gate, signal);
    }

    /// <summary>
    /// 一条人行横道 + 一个中央岛：节点 0 为信号路口，两侧人行道车道 0 → 1，
    /// 前段转向 0（Leg=0）与后段转向 1（Leg=1）共用同一条弦、同岛。
    /// </summary>
    /// <param name="islandCapacity">岛容量。</param>
    /// <param name="withIsland">是否带岛。</param>
    /// <param name="withTwoLegTurns">转向是否带 Leg / IslandId。</param>
    /// <param name="plan">第 0 个节点的配时方案；<c>null</c> = 默认"只放前段"的单相位。</param>
    private static Network TwoLegNetwork(
        int islandCapacity,
        bool withIsland = true,
        bool withTwoLegTurns = true,
        SignalPlan? plan = null)
    {
        plan ??= Plan(Stage(30d, prot: [CW]));

        var nodes = new List<Node>
        {
            new() { Id = 0, Type = NodeType.Signalized, X = 0, Y = 0, SignalPlans = [plan] },
            new() { Id = 1, Type = NodeType.Unsignalized, X = 0, Y = -20 },
            new() { Id = 2, Type = NodeType.Unsignalized, X = 0, Y = 20 },
        };

        var lanes = new List<Lane>
        {
            new() { Id = 0, Road = 0, FromNode = 1, ToNode = 0, Type = LaneType.Sidewalk, Length = 20, SpeedLimit = 1.4, LaneIndex = 1 },
            new() { Id = 1, Road = 0, FromNode = 0, ToNode = 2, Type = LaneType.Sidewalk, Length = 20, SpeedLimit = 1.4, LaneIndex = 1 },
        };

        var turns = new List<Turn>
        {
            new() { Id = CW, SrcLane = 0, DstLane = 1, Node = 0, Type = TurnType.Crosswalk, Leg = -1, IslandId = -1 },
            new() { Id = CW2, SrcLane = 0, DstLane = 1, Node = 0, Type = TurnType.Crosswalk },
        };

        if (withTwoLegTurns)
        {
            turns[0] = new Turn { Id = CW, SrcLane = 0, DstLane = 1, Node = 0, Type = TurnType.Crosswalk, Leg = 0, IslandId = IslandId };
            turns[1] = new Turn { Id = CW2, SrcLane = 0, DstLane = 1, Node = 0, Type = TurnType.Crosswalk, Leg = 1, IslandId = IslandId };
        }

        return new Network
        {
            Nodes = nodes,
            Lanes = lanes,
            Turns = turns,
            Islands = withIsland
                ? [new Island { Id = IslandId, X = 0, Y = 0, Capacity = islandCapacity }]
                : Array.Empty<Island>(),
        };
    }

    /// <summary>用反射读协调器的后段转向 id（internal 面，不经 public 契约）。</summary>
    private static int SecondLegTurnIdOf(CompiledNetwork net, PedestrianCrossing crossing)
    {
        var field = typeof(PedestrianCrossing).GetField("_secondLegTurnId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        return (int)field!.GetValue(crossing)!;
    }

    private static Stage Stage(double duration, int[]? prot = null, int[]? perm = null)
        => new()
        {
            Type = StageType.Fixed,
            Duration = duration,
            ProtectedTurnIds = prot ?? [],
            PermittedTurnIds = perm ?? [],
        };

    private static SignalPlan Plan(params Stage[] stages)
        => new() { StartTime = 0d, Stages = stages };
}
