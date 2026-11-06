using SimCore.Model;
using SimCore.Sim;
using Xunit;

namespace SimCore.Tests;

/// <summary>
/// T1.2「信号状态机与相位推进」验收测试。
/// <para>分组与验收标准逐条对应，便于"手和眼"对证：
/// <list type="bullet">
///   <item>A 组 → 验收 a) 一个周期内相位顺序与时长精确</item>
///   <item>B 组 → 验收 b) 黄 / 全红期间放行掩码正确（全红清空）</item>
///   <item>C 组 → 验收 c) 运行中改配时 → 下一周期生效（单独测）</item>
///   <item>D 组 → 验收 d) offset 正确偏移初始相位</item>
///   <item>E 组 → 与冻结接口的对应（周期公式 / 枚举 / 校验 / 时段方案 / 确定性）</item>
/// </list>
/// </para>
/// <para>采样步长取 <c>0.5s</c>（二进制可精确表示），使"按采样点计数得时长"落在精确整点上，
/// 因而可以逐点断言相位序列与时长（不用容差掩盖边界错误）。</para>
/// </summary>
public sealed class SignalTests
{
    private const double SampleDt = 0.5d;

    // ------------------------------------------------------------------
    // A 组：验收 a) 一个周期内相位顺序与时长精确
    // ------------------------------------------------------------------

    /// <summary>a1：默认清空时间（黄 3 / 全红 2 / 无绿闪）下，一整个周期的相位序列为
    /// <c>G(10) → Y(3) → R(2) → G(10) → Y(3) → R(2)</c>，顺序与每段时长逐点精确。</summary>
    [Fact]
    public void A1_Phase_Sequence_And_Durations_Are_Exact_Within_One_Cycle()
    {
        var plan = Plan(0d, Stage(10d, prot: [0]), Stage(10d, prot: [2]));
        var sm = new SignalStateMachine(Node(0d, plan), new SignalTiming(3.0d, 2.0d, 0d));
        sm.SyncToTime(0d);

        Assert.Equal(30d, sm.CycleLength);   // 2×(10 绿) + 2×(3 黄 + 2 全红) = 30

        (SignalPhaseState Phase, int Stage, int Count)[] seg = Segments(sm, SampleDt, steps: 60);

        Assert.Equal(6, seg.Length);
        AssertSegment(seg[0], SignalPhaseState.Green, stage: 0, seconds: 10d);
        AssertSegment(seg[1], SignalPhaseState.Yellow, stage: 0, seconds: 3d);
        AssertSegment(seg[2], SignalPhaseState.AllRed, stage: 0, seconds: 2d);
        AssertSegment(seg[3], SignalPhaseState.Green, stage: 1, seconds: 10d);
        AssertSegment(seg[4], SignalPhaseState.Yellow, stage: 1, seconds: 3d);
        AssertSegment(seg[5], SignalPhaseState.AllRed, stage: 1, seconds: 2d);

        // 明细核对：分段时长之和 = 周期长度。
        double total = 0d;
        foreach (var s in seg)
        {
            total += s.Count * SampleDt;
        }

        Assert.Equal(sm.CycleLength, total);
    }

    /// <summary>a2：周期倒计时（<c>TimeInPhase</c> / <c>PhaseRemaining</c>）在相位内逐点精确，
    /// 并在相位切换点归零 / 重置。</summary>
    [Fact]
    public void A2_Phase_Countdown_Is_Exact_And_Resets_At_Transitions()
    {
        var plan = Plan(0d, Stage(10d, prot: [0]), Stage(10d, prot: [2]));
        var sm = new SignalStateMachine(Node(0d, plan), new SignalTiming(3.0d, 2.0d, 0d));

        sm.SyncToTime(0d);
        Assert.Equal(SignalPhaseState.Green, sm.Phase);
        Assert.Equal(0d, sm.TimeInPhase);
        Assert.Equal(10d, sm.PhaseRemaining);

        sm.SyncToTime(4d);
        Assert.Equal(4d, sm.TimeInPhase);
        Assert.Equal(6d, sm.PhaseRemaining);

        sm.SyncToTime(9.5d);
        Assert.Equal(SignalPhaseState.Green, sm.Phase);
        Assert.Equal(9.5d, sm.TimeInPhase);
        Assert.Equal(0.5d, sm.PhaseRemaining);

        sm.SyncToTime(10d);                      // 绿 → 黄 边界
        Assert.Equal(SignalPhaseState.Yellow, sm.Phase);
        Assert.Equal(0d, sm.TimeInPhase);
        Assert.Equal(3d, sm.PhaseRemaining);

        sm.SyncToTime(13d);                      // 黄 → 全红 边界
        Assert.Equal(SignalPhaseState.AllRed, sm.Phase);
        Assert.Equal(0d, sm.TimeInPhase);
        Assert.Equal(2d, sm.PhaseRemaining);

        sm.SyncToTime(15d);                      // 全红 → 下一相位绿灯 边界
        Assert.Equal(SignalPhaseState.Green, sm.Phase);
        Assert.Equal(1, sm.StageIndex);
        Assert.Equal(0d, sm.TimeInPhase);
        Assert.Equal(10d, sm.PhaseRemaining);

        sm.SyncToTime(30d);                      // 一整周期后回到第 0 相位绿灯起点
        Assert.Equal(SignalPhaseState.Green, sm.Phase);
        Assert.Equal(0, sm.StageIndex);
        Assert.Equal(0d, sm.TimeInPhase);
    }

    /// <summary>a3：启用绿闪（<c>GreenFlashTime &gt; 0</c>）时，子相位顺序为
    /// <c>绿 → 绿闪 → 黄 → 全红 → 下一相位</c>，且绿闪计入周期长度。</summary>
    [Fact]
    public void A3_GreenFlash_Inserts_Between_Green_And_Yellow()
    {
        var plan = Plan(0d, Stage(10d, prot: [0]), Stage(10d, prot: [2]));
        var sm = new SignalStateMachine(Node(0d, plan), new SignalTiming(3.0d, 2.0d, 1.0d));
        sm.SyncToTime(0d);

        Assert.Equal(32d, sm.CycleLength);   // 2×(10 绿 + 1 绿闪 + 3 黄 + 2 全红) = 32

        (SignalPhaseState Phase, int Stage, int Count)[] seg = Segments(sm, SampleDt, steps: 64);

        Assert.Equal(8, seg.Length);
        AssertSegment(seg[0], SignalPhaseState.Green, stage: 0, seconds: 10d);
        AssertSegment(seg[1], SignalPhaseState.GreenFlash, stage: 0, seconds: 1d);
        AssertSegment(seg[2], SignalPhaseState.Yellow, stage: 0, seconds: 3d);
        AssertSegment(seg[3], SignalPhaseState.AllRed, stage: 0, seconds: 2d);
        AssertSegment(seg[4], SignalPhaseState.Green, stage: 1, seconds: 10d);
        AssertSegment(seg[5], SignalPhaseState.GreenFlash, stage: 1, seconds: 1d);
        AssertSegment(seg[6], SignalPhaseState.Yellow, stage: 1, seconds: 3d);
        AssertSegment(seg[7], SignalPhaseState.AllRed, stage: 1, seconds: 2d);
    }

    // ------------------------------------------------------------------
    // B 组：验收 b) 黄 / 全红期间放行掩码正确（全红清空）
    // ------------------------------------------------------------------

    /// <summary>b1：放行掩码——绿 / 绿闪期间按 <c>Stage.PriorityOf</c> 放行（保护 / 允许），
    /// 黄 / 全红期间全部转向一律禁行（全红清空）。</summary>
    [Fact]
    public void B1_Yellow_And_AllRed_Ban_Every_Turn()
    {
        var plan = Plan(
            0d,
            Stage(10d, prot: [0, 1], perm: [7]),   // 相位 0：保护 {0,1} + 允许 {7}
            Stage(10d, prot: [2]));                // 相位 1：保护 {2}
        var sm = new SignalStateMachine(Node(0d, plan), new SignalTiming(3.0d, 2.0d, 1.0d));

        // 相位 0 绿灯：保护 / 允许 / 禁行三态齐全。
        sm.SyncToTime(0d);
        Assert.Equal(SignalPhaseState.Green, sm.Phase);
        Assert.Equal(TurnPriority.Protected, sm.PriorityOf(0));
        Assert.Equal(TurnPriority.Protected, sm.PriorityOf(1));
        Assert.Equal(TurnPriority.Yield, sm.PriorityOf(7));
        Assert.Equal(TurnPriority.Banned, sm.PriorityOf(2));

        // 相位 0 绿闪：放行口径与绿灯一致。
        sm.SyncToTime(10d);
        Assert.Equal(SignalPhaseState.GreenFlash, sm.Phase);
        Assert.Equal(TurnPriority.Protected, sm.PriorityOf(0));
        Assert.Equal(TurnPriority.Yield, sm.PriorityOf(7));

        // 相位 0 黄灯：全部禁行。
        sm.SyncToTime(11d);
        Assert.Equal(SignalPhaseState.Yellow, sm.Phase);
        Assert.Equal(TurnPriority.Banned, sm.PriorityOf(0));
        Assert.Equal(TurnPriority.Banned, sm.PriorityOf(1));
        Assert.Equal(TurnPriority.Banned, sm.PriorityOf(7));

        // 相位 0 全红：全部禁行（"全红清空"——上一相位放行的转向也必须清空）。
        sm.SyncToTime(14d);
        Assert.Equal(SignalPhaseState.AllRed, sm.Phase);
        Assert.Equal(TurnPriority.Banned, sm.PriorityOf(0));
        Assert.Equal(TurnPriority.Banned, sm.PriorityOf(1));
        Assert.Equal(TurnPriority.Banned, sm.PriorityOf(7));

        // 相位 1 绿灯：换相位后放行集合随之切换。
        sm.SyncToTime(16d);
        Assert.Equal(SignalPhaseState.Green, sm.Phase);
        Assert.Equal(1, sm.StageIndex);
        Assert.Equal(TurnPriority.Protected, sm.PriorityOf(2));
        Assert.Equal(TurnPriority.Banned, sm.PriorityOf(0));

        // 相位 1 全红：再次清空。
        sm.SyncToTime(30d);
        Assert.Equal(SignalPhaseState.AllRed, sm.Phase);
        Assert.Equal(TurnPriority.Banned, sm.PriorityOf(2));
    }

    /// <summary>b2：全红的"清空"是硬性质——在整个全红窗口内采样，任一转向都不得放行。</summary>
    [Fact]
    public void B2_AllRed_Window_Releases_Nothing()
    {
        var plan = Plan(0d, Stage(10d, prot: [0, 1], perm: [2, 3]), Stage(10d, prot: [4]));
        var sm = new SignalStateMachine(Node(0d, plan), new SignalTiming(3.0d, 2.0d, 0d));

        int[] allTurns = [0, 1, 2, 3, 4, 99];
        int sampled = 0;
        for (double t = 13d; t < 15d; t += 0.25d)        // 相位 0 全红窗口 [13,15)
        {
            sm.SyncToTime(t);
            Assert.Equal(SignalPhaseState.AllRed, sm.Phase);
            foreach (int turn in allTurns)
            {
                Assert.Equal(TurnPriority.Banned, sm.PriorityOf(turn));
            }

            sampled++;
        }

        Assert.Equal(8, sampled);
    }

    // ------------------------------------------------------------------
    // C 组：验收 c) 运行中改配时 → 下一周期生效（单独测）
    // ------------------------------------------------------------------

    /// <summary>c1：运行中 <c>ApplyPlan</c> 不打断当前相位；新配时在当前周期走完（相位环回到
    /// 第 0 相位绿灯起点）的那个边界才生效，届时 <c>CycleLength</c> 随之切换到新值。</summary>
    [Fact]
    public void C1_ApplyPlan_Takes_Effect_At_Next_Cycle_Only()
    {
        var oldPlan = Plan(0d, Stage(10d, prot: [0]), Stage(10d, prot: [2]));   // C = 30
        var newPlan = Plan(0d, Stage(20d, prot: [0]), Stage(20d, prot: [2]));   // C = 50
        var sm = new SignalStateMachine(Node(0d, oldPlan), new SignalTiming(3.0d, 2.0d, 0d));
        sm.SyncToTime(0d);

        // 运行 5s 进入第 0 相位绿灯中段。
        for (int i = 0; i < 10; i++)
        {
            sm.Tick(SampleDt);
        }

        Assert.Equal(5d, sm.TimeInPhase);
        Assert.Equal(30d, sm.CycleLength);

        // 运行中改配时。
        sm.ApplyPlan(newPlan);

        // 立即：当前相位 / 阶段 / 周期长度都不许变（不得即时改当前相位）。
        Assert.Equal(30d, sm.CycleLength);
        Assert.Equal(0, sm.StageIndex);
        Assert.Equal(SignalPhaseState.Green, sm.Phase);
        Assert.Equal(5d, sm.TimeInPhase);
        Assert.Equal(5d, sm.PhaseRemaining);

        // 继续推进 20s（t=25s）：仍由旧方案主导——此刻是旧方案第 1 相位黄灯。
        // 若误把新方案即时生效（C=50），t=25 会落在第 1 相位绿灯，从而被本断言抓住。
        for (int i = 0; i < 40; i++)
        {
            sm.Tick(SampleDt);
        }

        Assert.Equal(30d, sm.CycleLength);
        Assert.Equal(1, sm.StageIndex);
        Assert.Equal(SignalPhaseState.Yellow, sm.Phase);
        Assert.Equal(25d, sm.TimeInPhase + 25d);   // 位置 = 25s（self-documenting）

        // 再推进 5s（t=30s，旧周期恰好走完）：边界处新方案生效，周期长度变为 50。
        for (int i = 0; i < 10; i++)
        {
            sm.Tick(SampleDt);
        }

        Assert.Equal(50d, sm.CycleLength);
        Assert.Equal(0, sm.StageIndex);
        Assert.Equal(SignalPhaseState.Green, sm.Phase);
        Assert.Equal(0d, sm.TimeInPhase);
        Assert.Equal(20d, sm.PhaseRemaining);      // 新方案第 0 相位绿灯 = 20s
    }

    /// <summary>c2：改配时的效果在边界处"整段"替换——新周期全部相位均来自新方案。</summary>
    [Fact]
    public void C2_New_Cycle_Uses_New_Plan_Throughout()
    {
        var oldPlan = Plan(0d, Stage(10d, prot: [0]), Stage(10d, prot: [2]));
        var newPlan = Plan(0d, Stage(4d, prot: [0]), Stage(20d, prot: [2]), Stage(6d, prot: [5]));
        var sm = new SignalStateMachine(Node(0d, oldPlan), new SignalTiming(3.0d, 2.0d, 0d));
        sm.SyncToTime(0d);

        sm.ApplyPlan(newPlan);
        for (int i = 0; i < 60; i++)   // 推进整整一个旧周期（30s）
        {
            sm.Tick(SampleDt);
        }

        // 新周期 = 3 相位：(4+3+2) + (20+3+2) + (6+3+2) = 45
        Assert.Equal(45d, sm.CycleLength);

        (SignalPhaseState Phase, int Stage, int Count)[] seg = Segments(sm, SampleDt, steps: 90);
        Assert.Equal(9, seg.Length);
        AssertSegment(seg[0], SignalPhaseState.Green, stage: 0, seconds: 4d);
        AssertSegment(seg[1], SignalPhaseState.Yellow, stage: 0, seconds: 3d);
        AssertSegment(seg[2], SignalPhaseState.AllRed, stage: 0, seconds: 2d);
        AssertSegment(seg[3], SignalPhaseState.Green, stage: 1, seconds: 20d);
        AssertSegment(seg[4], SignalPhaseState.Yellow, stage: 1, seconds: 3d);
        AssertSegment(seg[5], SignalPhaseState.AllRed, stage: 1, seconds: 2d);
        AssertSegment(seg[6], SignalPhaseState.Green, stage: 2, seconds: 6d);
        AssertSegment(seg[7], SignalPhaseState.Yellow, stage: 2, seconds: 3d);
        AssertSegment(seg[8], SignalPhaseState.AllRed, stage: 2, seconds: 2d);
    }

    // ------------------------------------------------------------------
    // D 组：验收 d) offset 正确偏移初始相位
    // ------------------------------------------------------------------

    /// <summary>d1：<c>offset</c> 把初始相位平移 <c>offset</c> 秒——带 offset 的机器在 t 时刻的相位，
    /// 等于无 offset 的机器在 <c>t − offset</c> 时刻的相位（相位 / 阶段 / 相位内进度全部一致）。</summary>
    [Fact]
    public void D1_Offset_Shifts_Initial_Phase_By_Offset_Seconds()
    {
        const double offset = 7d;
        var plan = Plan(0d, Stage(10d, prot: [0]), Stage(10d, prot: [2]));
        var timing = new SignalTiming(3.0d, 2.0d, 0d);

        var shifted = new SignalStateMachine(Node(offset, plan), timing);
        var baseline = new SignalStateMachine(Node(0d, plan), timing);

        foreach (double t in new[] { 7d, 12d, 18d, 25d, 29d })
        {
            shifted.SyncToTime(t);
            baseline.SyncToTime(t - offset);   // t − offset ∈ {0,5,11,18,22}，均 ≥ 0

            Assert.Equal(baseline.Phase, shifted.Phase);
            Assert.Equal(baseline.StageIndex, shifted.StageIndex);
            Assert.Equal(baseline.PlanIndex, shifted.PlanIndex);
            Assert.Equal(baseline.CycleLength, shifted.CycleLength);
            Assert.Equal(baseline.TimeInPhase, shifted.TimeInPhase, 9);
            Assert.Equal(baseline.PhaseRemaining, shifted.PhaseRemaining, 9);
        }
    }

    /// <summary>d2：offset 的显式初始相位——<c>offset=7, C=30</c> 时 t=0 位于周期内位置 23，
    /// 即第 1 相位绿灯的第 8 秒（剩余 2s）。</summary>
    [Fact]
    public void D2_Offset_Determines_Phase_At_Time_Zero()
    {
        var plan = Plan(0d, Stage(10d, prot: [0]), Stage(10d, prot: [2]));
        var sm = new SignalStateMachine(Node(7d, plan), new SignalTiming(3.0d, 2.0d, 0d));

        sm.SyncToTime(0d);

        Assert.Equal(SignalPhaseState.Green, sm.Phase);
        Assert.Equal(1, sm.StageIndex);          // 位置 23 ∈ [15,25) = 第 1 相位绿灯
        Assert.Equal(8d, sm.TimeInPhase);        // 23 − 15
        Assert.Equal(2d, sm.PhaseRemaining);     // 25 − 23
    }

    /// <summary>d3：offset 超过一个周期时按周期取模，不改变相位。</summary>
    [Fact]
    public void D3_Offset_Wraps_Modulo_Cycle_Length()
    {
        var plan = Plan(0d, Stage(10d, prot: [0]), Stage(10d, prot: [2]));
        var timing = new SignalTiming(3.0d, 2.0d, 0d);

        var small = new SignalStateMachine(Node(7d, plan), timing);
        var large = new SignalStateMachine(Node(7d + 30d * 3d, plan), timing);   // 37 = 7 + 3×C

        foreach (double t in new[] { 0d, 4d, 11d, 19d, 26d })
        {
            small.SyncToTime(t);
            large.SyncToTime(t);

            Assert.Equal(small.Phase, large.Phase);
            Assert.Equal(small.StageIndex, large.StageIndex);
            Assert.Equal(small.TimeInPhase, large.TimeInPhase, 9);
            Assert.Equal(small.PhaseRemaining, large.PhaseRemaining, 9);
        }
    }

    // ------------------------------------------------------------------
    // E 组：与冻结接口的对应
    // ------------------------------------------------------------------

    /// <summary>e1（§4.5）：<c>CycleLength = Σ(绿灯) + Σ(黄 + 全红 + 绿闪)</c>。</summary>
    [Fact]
    public void E1_Cycle_Length_Matches_Frozen_Formula()
    {
        var plan = Plan(
            0d,
            Stage(7d, prot: [0]),
            Stage(13d, prot: [1]),
            Stage(5d, prot: [2]));
        var sm = new SignalStateMachine(Node(0d, plan), new SignalTiming(3.5d, 2.25d, 0.5d));

        // Σ绿灯 = 25；Σ(黄+全红+绿闪) = 3×(3.5+2.25+0.5) = 18.75；合计 43.75
        Assert.Equal(43.75d, sm.CycleLength);
    }

    /// <summary>e2（§4.4 附 A）：<c>SignalPhaseState</c> 成员名与顺序逐字冻结。</summary>
    [Fact]
    public void E2_SignalPhaseState_Members_Are_Frozen()
    {
        Assert.Equal(
            new[] { "Green", "Yellow", "AllRed", "GreenFlash" },
            Enum.GetNames<SignalPhaseState>());
    }

    /// <summary>e3（§4.7）：<c>SignalTiming.Default = new(3.0, 2.0, 0.0)</c>；构造校验非负。</summary>
    [Fact]
    public void E3_SignalTiming_Default_And_Validation()
    {
        Assert.Equal(3.0d, SignalTiming.Default.YellowTime);
        Assert.Equal(2.0d, SignalTiming.Default.AllRedTime);
        Assert.Equal(0.0d, SignalTiming.Default.GreenFlashTime);

        Assert.Throws<ArgumentOutOfRangeException>(() => new SignalTiming(-0.1d, 2.0d));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SignalTiming(3.0d, -1.0d));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SignalTiming(3.0d, 2.0d, -1.0d));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SignalTiming(double.NaN, 2.0d));
    }

    /// <summary>e4：构造前置——null / 空方案 / 空相位一律抛异常（§1.3）。</summary>
    [Fact]
    public void E4_Constructor_Rejects_Invalid_Input()
    {
        var plan = Plan(0d, Stage(10d));

        Assert.Throws<ArgumentNullException>(() => new SignalStateMachine(null!, SignalTiming.Default));
        Assert.Throws<ArgumentNullException>(() => new SignalStateMachine(Node(0d, plan), null!));

        var noPlanNode = new Node { Id = 0, Type = NodeType.Signalized };
        Assert.Throws<ArgumentException>(() => new SignalStateMachine(noPlanNode, SignalTiming.Default));

        var emptyStagePlan = Plan(0d);   // 无相位
        Assert.Throws<ArgumentException>(() => new SignalStateMachine(Node(0d, emptyStagePlan), SignalTiming.Default));
    }

    /// <summary>e5：<c>ApplyPlan</c> 前置——null / 空相位抛异常（§1.3）。</summary>
    [Fact]
    public void E5_ApplyPlan_Rejects_Invalid_Input()
    {
        var sm = new SignalStateMachine(Node(0d, Plan(0d, Stage(10d))), SignalTiming.Default);

        Assert.Throws<ArgumentNullException>(() => sm.ApplyPlan(null!));
        Assert.Throws<ArgumentException>(() => sm.ApplyPlan(Plan(0d)));
    }

    /// <summary>e6：<c>Tick</c> 前置——步长必须为有限正值（§1.3）。</summary>
    [Fact]
    public void E6_Tick_Rejects_Non_Positive_Or_Non_Finite_Dt()
    {
        var sm = new SignalStateMachine(Node(0d, Plan(0d, Stage(10d))), SignalTiming.Default);

        Assert.Throws<ArgumentOutOfRangeException>(() => sm.Tick(0d));
        Assert.Throws<ArgumentOutOfRangeException>(() => sm.Tick(-1d));
        Assert.Throws<ArgumentOutOfRangeException>(() => sm.Tick(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => sm.Tick(double.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => sm.SyncToTime(double.NaN));
    }

    /// <summary>e7（§4.5 <c>PlanIndex</c>）：多套方案按时段（<c>StartTime</c>）选择生效方案。</summary>
    [Fact]
    public void E7_PlanIndex_Selects_Plan_By_StartTime()
    {
        var morning = Plan(0d, Stage(10d));      // C = 10+3+2 = 15
        var evening = Plan(100d, Stage(20d));    // C = 20+3+2 = 25
        var sm = new SignalStateMachine(
            new Node { Id = 0, Type = NodeType.Signalized, SignalPlans = [morning, evening] },
            new SignalTiming(3.0d, 2.0d, 0d));

        sm.SyncToTime(0d);
        Assert.Equal(0, sm.PlanIndex);
        Assert.Equal(15d, sm.CycleLength);

        sm.SyncToTime(150d);
        Assert.Equal(1, sm.PlanIndex);
        Assert.Equal(25d, sm.CycleLength);

        sm.SyncToTime(-10d);                     // 早于所有 StartTime → 取最早者
        Assert.Equal(0, sm.PlanIndex);
    }

    /// <summary>e8：<c>CurrentStage</c> / <c>NodeId</c> 与输入一致（放行集合可被门控读取）。</summary>
    [Fact]
    public void E8_CurrentStage_And_NodeId_Expose_Input()
    {
        Stage s0 = Stage(10d, prot: [0], perm: [1]);
        Stage s1 = Stage(10d, prot: [2]);
        var plan = new SignalPlan { StartTime = 0d, Stages = [s0, s1] };
        var node = new Node { Id = 42, Type = NodeType.Signalized, SignalPlans = [plan] };
        var sm = new SignalStateMachine(node, SignalTiming.Default);

        Assert.Equal(42, sm.NodeId);

        sm.SyncToTime(3d);
        Assert.Same(s0, sm.CurrentStage);

        sm.SyncToTime(18d);
        Assert.Same(s1, sm.CurrentStage);
    }

    /// <summary>e9（铁律 2）：同输入序列两次运行，相位序列逐点一致（确定性）。</summary>
    [Fact]
    public void E9_Identical_Inputs_Produce_Identical_Phase_Sequences()
    {
        var plan = Plan(0d, Stage(10d, prot: [0]), Stage(10d, prot: [2]));
        var timing = new SignalTiming(3.0d, 2.0d, 1.0d);

        var a = new SignalStateMachine(Node(3d, plan), timing);
        var b = new SignalStateMachine(Node(3d, plan), timing);
        a.SyncToTime(0d);
        b.SyncToTime(0d);

        for (int i = 0; i < 400; i++)
        {
            Assert.Equal(a.Phase, b.Phase);
            Assert.Equal(a.StageIndex, b.StageIndex);
            Assert.Equal(a.TimeInPhase, b.TimeInPhase, 12);
            a.Tick(0.25d);
            b.Tick(0.25d);
        }
    }

    /// <summary>e10：<c>TimeInPhase + PhaseRemaining</c> 恒等于当前子相位时长（自洽性）。</summary>
    [Fact]
    public void E10_TimeInPhase_Plus_PhaseRemaining_Equals_Slice_Length()
    {
        var plan = Plan(0d, Stage(10d, prot: [0]), Stage(10d, prot: [2]));
        var timing = new SignalTiming(3.0d, 2.0d, 1.0d);
        var sm = new SignalStateMachine(Node(0d, plan), timing);

        foreach (double t in new[] { 0d, 2.5d, 10d, 10.5d, 11d, 13.5d, 15d, 26d, 27d, 30d })
        {
            sm.SyncToTime(t);
            double expected = sm.Phase switch
            {
                SignalPhaseState.Green => sm.CurrentStage.Duration,
                SignalPhaseState.GreenFlash => timing.GreenFlashTime,
                SignalPhaseState.Yellow => timing.YellowTime,
                _ => timing.AllRedTime,
            };

            Assert.Equal(expected, sm.TimeInPhase + sm.PhaseRemaining, 9);
            Assert.True(sm.TimeInPhase >= 0d && sm.PhaseRemaining > 0d, $"t={t} 相位区间不自洽。");
        }
    }

    // ------------------------------------------------------------------
    // 夹具
    // ------------------------------------------------------------------

    private static Node Node(double offset, params SignalPlan[] plans)
        => new()
        {
            Id = 0,
            Type = NodeType.Signalized,
            Offset = offset,
            SignalPlans = plans,
        };

    private static SignalPlan Plan(double startTime, params Stage[] stages)
        => new() { StartTime = startTime, Stages = stages };

    private static Stage Stage(double duration, int[]? prot = null, int[]? perm = null)
        => new()
        {
            Type = StageType.Fixed,
            Duration = duration,
            ProtectedTurnIds = prot ?? [],
            PermittedTurnIds = perm ?? [],
        };

    /// <summary>按固定步长采样相位，压缩成「相位 / 阶段 / 采样数」的连续段（采样点在 tick 之前）。</summary>
    private static (SignalPhaseState Phase, int Stage, int Count)[] Segments(SignalStateMachine sm, double dt, int steps)
    {
        var segments = new List<(SignalPhaseState Phase, int Stage, int Count)>();
        for (int i = 0; i < steps; i++)
        {
            SignalPhaseState phase = sm.Phase;
            int stage = sm.StageIndex;

            if (segments.Count == 0 || segments[^1].Phase != phase || segments[^1].Stage != stage)
            {
                segments.Add((phase, stage, 1));
            }
            else
            {
                segments[^1] = (phase, stage, segments[^1].Count + 1);
            }

            sm.Tick(dt);
        }

        return [.. segments];
    }

    private static void AssertSegment(
        (SignalPhaseState Phase, int Stage, int Count) segment,
        SignalPhaseState phase,
        int stage,
        double seconds)
    {
        Assert.Equal(phase, segment.Phase);
        Assert.Equal(stage, segment.Stage);
        Assert.Equal(seconds, segment.Count * SampleDt, 9);
    }
}
