using SimCore.Io;

namespace SimCore.Sim;

// ===========================================================================
// T1.7 · 需求生成器（恒定 / 时变 / 脉冲）—— 确定性时刻表
//
// 依据：
//   * docs/SimCore-API.md §4.8（冻结接口：DemandKind / DemandGenerator / RateAt / DrawArrivals / Reset）
//   * docs/SimCore-API.md §7.5（三类需求曲线：(time_points, flow_rates) 分段常值）
//   * docs/spmap-schema.md §4.6（demand[] / pedestrians[] / bicycles[] 同构）
//   * 技术方案01 §3.4（LightSim DemandProfile：idx = searchsorted(time_points, t) − 1 → 分段常值）
//   * 游戏设计v2 §9（固定时刻表生成：恒定、时变渐变高峰、脉冲）
//
// 本文件落地 §4.8 冻结的 **全部** public 面（其余成员一律 private）：
//     enum  DemandKind { Driving, Pedestrian, Bicycle }
//     class DemandGenerator(Level level, long seed)
//     double RateAt(DemandKind kind, int targetId, double time)
//     int    DrawArrivals(DemandKind kind, int targetId, double time, double dt)
//     void   Reset(long seed)
//
// ---------------------------------------------------------------------------
// 确定性设计（本任务追加约束："需求注入必须可复现（注入种子）"）
//
// DrawArrivals 是 (Seed, kind, targetId, time, dt) 的**纯函数**——内部不含任何可变累积状态。
// 由此获得四条工程性质（均已由单测逐条证明）：
//   (1) 与调用顺序无关：乱序调用、重复调用都返回同一值；
//   (2) 与加速档无关：同一仿真时刻恒返回同一到达数，故 1× 与 32× 注入序列逐位一致；
//   (3) 无浮点漂移：[t,t+dt) 与 [t+dt,t+2dt) 两次调用的和 == [t,t+2dt) 一次调用；
//   (4) 可复现：同 seed 跨实例、跨 Reset 往返，序列完全相同。
//
// 到达过程的数学构造（"确定性强度 λ 的点过程"）：
//   记 Λ(t) = ∫₀ᵗ λ(τ)dτ 为累计流率（λ 分段常值，故 Λ 为分段线性、单调不减）。
//   为第 k 次到达（k = 1,2,3,…）配一个**确定性扰动** s_k ∈ (0,1]，
//       第 k 次到达发生于 Λ(t) 越过 (k − 1 + s_k) 之时。
//   于是 [0,t) 内的到达数 N(t) = #{ k ≥ 1 : (k − 1 + s_k) ≤ Λ(t) }。
//   因 s_k ∈ (0,1]，有 (k−1+s_k) ∈ (k−1, k] 严格递增 ⇒ N(t) 单调不减（到达数恒 ≥ 0）。
//   且恒有 floor(Λ) ≤ N(t) ≤ ceil(Λ) ⇒ 长时相对误差 ≤ 1/Λ ⇒ 恒定流率误差 < 1%（当 Λ ≥ 100）。
//
//   s_k 由 (seed, kind, targetId, k) 经 FNV-1a64 混入 + splitmix64 终混器位运算派生——
//   **全程不使用任何系统随机数**（铁律 2：需要随机必须注入种子；本卡种子即 SimConfig.Seed）。
//   故流率"看起来随机"（到达间隔抖动），却完全可复现。
//
// 铁律：无 Godot 命名空间、无第三方依赖、无系统随机数。
// ===========================================================================

/// <summary>
/// 需求种类（<c>docs/SimCore-API.md</c> §4.8 冻结）。决定 <c>targetId</c> 的取值空间。
/// </summary>
public enum DemandKind
{
    /// <summary>机动车需求；<c>targetId</c> = 机动车道 id（<c>demand[].lane</c>）。</summary>
    Driving,

    /// <summary>行人过街需求；<c>targetId</c> = 人行横道转向 id（<c>pedestrians[].crosswalk_turn</c>）。</summary>
    Pedestrian,

    /// <summary>非机动车需求；<c>targetId</c> = 非机动车道 id（<c>bicycles[].lane</c>）。</summary>
    Bicycle,
}

/// <summary>
/// 需求生成器（<c>docs/SimCore-API.md</c> §4.8 冻结）——按 <c>(time_points, flow_rates)</c> 分段常值曲线，
/// 给出任意时刻的到达流率与本步到达数。恒定 / 时变 / 脉冲三类曲线统一由该结构表达。
/// </summary>
/// <remarks>
/// <para>纯数据消费者：只读取 <see cref="Level.Demand"/> / <see cref="Level.Pedestrians"/> /
/// <see cref="Level.Bicycles"/> 三张曲线表，<b>不</b>校验其合法性（长度 / 升序 / 非负 / 目标类型等
/// 属 <c>SpmapValidator</c> 的职责，§4.6 校验码）。未配置曲线的目标一律视为零需求。</para>
/// <para>无网络依赖：本类型不编译路网、不做几何判断，故可用空 <see cref="Level.Network"/> 构造 / 单测。</para>
/// <para>线程安全：全部方法只读字段、无状态变更，<see cref="Reset"/> 除外（调用方自行串行化）。</para>
/// </remarks>
public sealed class DemandGenerator
{
    // 每类需求按 targetId 建"查表数组"（下标即 id）；空位 = null = 无需求（零流率）。
    // 用数组而非字典，既省去哈希开销，也从根上杜绝"字典遍历顺序"隐患（与引擎口径一致）。
    private readonly Profile?[] _driving;
    private readonly Profile?[] _pedestrian;
    private readonly Profile?[] _bicycle;

    private long _seed;
    private ulong _seedHash;

    /// <summary>以关卡的三张需求曲线表 + 注入种子构造生成器。</summary>
    /// <param name="level">关卡（提供 <see cref="Level.Demand"/> / <see cref="Level.Pedestrians"/> / <see cref="Level.Bicycles"/>）。</param>
    /// <param name="seed">需求注入种子（对应 <see cref="SimConfig.Seed"/>；禁止系统随机数）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="level"/> 为 <c>null</c>。</exception>
    public DemandGenerator(Level level, long seed)
    {
        if (level == null)
        {
            throw new ArgumentNullException(nameof(level));
        }

        _driving = BuildDriving(level.Demand);
        _pedestrian = BuildPedestrian(level.Pedestrians);
        _bicycle = BuildBicycle(level.Bicycles);
        SetSeed(seed);
    }

    /// <summary>
    /// 返回 <paramref name="time"/> 时刻该目标所处的分段流率（<see cref="DemandKind.Driving"/>/<see cref="DemandKind.Bicycle"/>
    /// 为辆/秒，<see cref="DemandKind.Pedestrian"/> 为人/秒）。
    /// </summary>
    /// <param name="kind">需求种类。</param>
    /// <param name="targetId">目标 id（车道 / 人行横道转向）；未配置曲线时返回 0。</param>
    /// <param name="time">仿真时刻（秒，须非负有限）。</param>
    /// <returns>该时段常值流率；<paramref name="time"/> 早于首个 <c>time_points</c> 或目标无需求时为 0。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="time"/> 为负、<c>NaN</c> 或无穷。</exception>
    public double RateAt(DemandKind kind, int targetId, double time)
    {
        RequireTime(time, nameof(time));

        Profile? profile = Lookup(kind, targetId);
        return profile is null ? 0d : profile.RateAt(time);
    }

    /// <summary>
    /// 返回区间 <c>[time, time+dt)</c> 内该目标的到达数（确定性纯函数）。
    /// </summary>
    /// <param name="kind">需求种类。</param>
    /// <param name="targetId">目标 id（车道 / 人行横道转向）；未配置曲线时返回 0。</param>
    /// <param name="time">区间起点（秒，须非负有限）。</param>
    /// <param name="dt">区间长度 / 仿真步长（秒，须非负有限；0 返回 0）。</param>
    /// <returns>本步到达数（≥ 0）。由 <c>(Seed, kind, targetId, time)</c> 确定性决定，不使用系统随机数。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="time"/> / <paramref name="dt"/> 为负、<c>NaN</c> 或无穷。</exception>
    /// <remarks>实现为 N(time+dt) − N(time)，其中 N 是累计到达数的纯函数（见文件头"确定性设计"）。</remarks>
    public int DrawArrivals(DemandKind kind, int targetId, double time, double dt)
    {
        RequireTime(time, nameof(time));
        RequireTime(dt, nameof(dt));

        if (dt <= 0d)
        {
            return 0;
        }

        Profile? profile = Lookup(kind, targetId);
        if (profile is null)
        {
            return 0;
        }

        long before = CountUpTo(profile, kind, targetId, profile.Integral(time));
        long after = CountUpTo(profile, kind, targetId, profile.Integral(time + dt));
        long delta = after - before;
        if (delta <= 0L)
        {
            return 0;
        }

        return delta >= int.MaxValue ? int.MaxValue : (int)delta;
    }

    /// <summary>更换注入种子（<c>docs/SimCore-API.md</c> §4.8 <c>Reset</c>）；曲线表不变，仅重派生到达扰动序列。</summary>
    /// <param name="seed">新种子（对应 <see cref="SimConfig.Seed"/>）。</param>
    public void Reset(long seed) => SetSeed(seed);

    // ------------------------------------------------------------------
    // 内部：查表 / 计数 / 扰动
    // ------------------------------------------------------------------

    private void SetSeed(long seed)
    {
        _seed = seed;
        _seedHash = SeedHash(seed);
    }

    private Profile? Lookup(DemandKind kind, int targetId)
    {
        if (targetId < 0)
        {
            return null;
        }

        Profile?[] table = kind switch
        {
            DemandKind.Driving => _driving,
            DemandKind.Pedestrian => _pedestrian,
            DemandKind.Bicycle => _bicycle,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的需求种类。"),
        };

        return (uint)targetId < (uint)table.Length ? table[targetId] : null;
    }

    /// <summary>累计到达数 N = #{ k ≥ 1 : (k−1+s_k) ≤ x }，其中 x = Λ(t)。</summary>
    private long CountUpTo(Profile profile, DemandKind kind, int targetId, double x)
    {
        if (x <= 0d)
        {
            return 0L;
        }

        if (x >= 9.0e18d)
        {
            return long.MaxValue;   // 防御：累计数超出 long 表示范围（实际时长下不可达）
        }

        long n = (long)Math.Floor(x);
        double frac = x - n;
        if (frac <= 0d)
        {
            return n;
        }

        // 第 n+1 次到达位于 (n, n+1]；当且仅当其扰动目标 ≤ x 时计入。
        double offset = UnitOffset(kind, targetId, n + 1L);
        return offset <= frac ? n + 1L : n;
    }

    /// <summary>确定性扰动 s_k ∈ (0,1]：由 (seed, kind, targetId, k) 位运算派生，无系统随机数。</summary>
    private double UnitOffset(DemandKind kind, int targetId, long k)
    {
        ulong h = _seedHash;
        h = Mix64(h, (ulong)(uint)kind);
        h = Mix64(h, (ulong)(uint)targetId);
        h = Mix64(h, (ulong)k);

        // splitmix64 终混器（充分雪崩，消除低位弱相关）。
        h ^= h >> 30;
        h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 27;
        h *= 0x94D049BB133111EBUL;
        h ^= h >> 31;

        // 高 53 位 → [0,1)，取 1−u 得 (0,1]（严格 >0 保证 (k−1+s_k) 严格递增）。
        double u = (h >> 11) * (1.0d / 9007199254740992.0d);
        return 1.0d - u;
    }

    /// <summary>FNV-1a64：把种子折叠为初始哈希。</summary>
    private static ulong SeedHash(long seed)
    {
        ulong h = 14695981039346656037UL;
        return Mix64(h, (ulong)seed);
    }

    /// <summary>FNV-1a64 逐字节混入（与引擎 StateHasher 同族，确定性、与进程无关）。</summary>
    private static ulong Mix64(ulong hash, ulong value)
    {
        ulong h = hash;
        for (int i = 0; i < 8; i++)
        {
            h ^= value & 0xFFUL;
            h *= 1099511628211UL;
            value >>= 8;
        }

        return h;
    }

    private static void RequireTime(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value < 0d)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "时间参数必须为非负有限数。");
        }
    }

    // ------------------------------------------------------------------
    // 曲线表构造（按 targetId 落位；同 id 多条记录"后者覆盖"，与 signals[] 口径一致）
    // ------------------------------------------------------------------

    private static Profile?[] BuildDriving(IReadOnlyList<DemandPlan> list)
    {
        int max = -1;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Lane > max)
            {
                max = list[i].Lane;
            }
        }

        if (max < 0)
        {
            return Array.Empty<Profile?>();
        }

        var table = new Profile?[max + 1];
        for (int i = 0; i < list.Count; i++)
        {
            DemandPlan d = list[i];
            if (d.Lane < 0)
            {
                continue;   // 非法 id：忽略（由 SpmapValidator 报错）
            }

            table[d.Lane] = new Profile(d.TimePoints, d.FlowRates);
        }

        return table;
    }

    private static Profile?[] BuildPedestrian(IReadOnlyList<PedestrianDemand> list)
    {
        int max = -1;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].CrosswalkTurn > max)
            {
                max = list[i].CrosswalkTurn;
            }
        }

        if (max < 0)
        {
            return Array.Empty<Profile?>();
        }

        var table = new Profile?[max + 1];
        for (int i = 0; i < list.Count; i++)
        {
            PedestrianDemand d = list[i];
            if (d.CrosswalkTurn < 0)
            {
                continue;
            }

            table[d.CrosswalkTurn] = new Profile(d.TimePoints, d.FlowRates);
        }

        return table;
    }

    private static Profile?[] BuildBicycle(IReadOnlyList<BicycleDemand> list)
    {
        int max = -1;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Lane > max)
            {
                max = list[i].Lane;
            }
        }

        if (max < 0)
        {
            return Array.Empty<Profile?>();
        }

        var table = new Profile?[max + 1];
        for (int i = 0; i < list.Count; i++)
        {
            BicycleDemand d = list[i];
            if (d.Lane < 0)
            {
                continue;
            }

            table[d.Lane] = new Profile(d.TimePoints, d.FlowRates);
        }

        return table;
    }

    /// <summary>
    /// 单条分段常值需求曲线（<c>time_points</c> + <c>flow_rates</c>）的只读派生：
    /// 预计算分段累计流率 <c>prefix[]</c>，使 <c>Λ(t)</c> 与 <c>RateAt(t)</c> 均为 O(log n) 查表。
    /// </summary>
    private sealed class Profile
    {
        private readonly double[] _timePoints;
        private readonly double[] _flowRates;
        private readonly double[] _prefix;   // _prefix[j] = ∫₀^{timePoints[j]} λ dτ

        public Profile(IReadOnlyList<double> timePoints, IReadOnlyList<double> flowRates)
        {
            int n = timePoints.Count;
            _timePoints = new double[n];
            _flowRates = new double[n];
            _prefix = new double[n];

            for (int i = 0; i < n; i++)
            {
                _timePoints[i] = timePoints[i];
                double rate = i < flowRates.Count ? flowRates[i] : 0d;
                _flowRates[i] = double.IsFinite(rate) && rate > 0d ? rate : 0d;   // 防御性夹紧（非法值由校验器报错）
            }

            double acc = 0d;
            for (int i = 0; i < n; i++)
            {
                _prefix[i] = acc;
                if (i + 1 < n)
                {
                    double span = _timePoints[i + 1] - _timePoints[i];
                    acc += span > 0d ? _flowRates[i] * span : 0d;   // 非升序输入按 0 段处理，保持 Λ 单调
                }
            }
        }

        /// <summary>分段常值查表：<c>time</c> 所在时段的流率；早于首个 <c>time_points</c> 时为 0。</summary>
        public double RateAt(double time)
        {
            int n = _timePoints.Length;
            if (n == 0 || time < _timePoints[0])
            {
                return 0d;
            }

            return _flowRates[LastIndexAtOrBefore(time)];
        }

        /// <summary>累计流率 <c>Λ(t) = ∫₀ᵗ λ(τ)dτ</c>（分段线性、单调不减）。</summary>
        public double Integral(double time)
        {
            int n = _timePoints.Length;
            if (n == 0 || time <= _timePoints[0])
            {
                return 0d;
            }

            int j = LastIndexAtOrBefore(time);
            return _prefix[j] + (_flowRates[j] * (time - _timePoints[j]));
        }

        /// <summary>最后一个满足 <c>timePoints[j] ≤ time</c> 的下标（二分，确定性；调用前须保证 time ≥ timePoints[0]）。</summary>
        private int LastIndexAtOrBefore(double time)
        {
            int lo = 0;
            int hi = _timePoints.Length;
            while (lo < hi)
            {
                int mid = (int)(((uint)lo + (uint)hi) >> 1);
                if (_timePoints[mid] <= time)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo - 1;
        }
    }
}
