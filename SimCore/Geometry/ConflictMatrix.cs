using SimCore.Model;

namespace SimCore.Geometry;

// ---------------------------------------------------------------------------
// T1.3 · 几何冲突矩阵（技术方案03 §5 · T1.3；docs/SimCore-API.md §3.1 冻结）
//
// 本文件落地 docs/SimCore-API.md §3.1 冻结的 ConflictMatrix。类型名、成员签名、
// 判定语义与技术方案02 §2.2(a) 逐条对齐；只依赖 SimCore.Model，不含仿真状态。
//
// 冲突判定（有序规则，§3.1「AreConflicting(a,b) 判定」）：
//   1) a.Id == b.Id               → false  （同一条转向不自冲突）
//   2) a.SrcLane == b.SrcLane     → false  （同起点不冲突 / 分流点）
//   3) a.DstLane == b.DstLane     → true   （同终点冲突 / 合流点）
//   4) 两条转向中心线折线几何相交  → true   （几何相交冲突 / 交叉点）
//   5) 其余                        → false
// 该关系对称：AreConflicting(a,b) == AreConflicting(b,a)。
//
// 铁律：无 Godot 命名空间、无第三方依赖、无系统随机数（From 两次编译逐项相等）。
// ---------------------------------------------------------------------------

/// <summary>
/// 转向冲突矩阵（T1.3；<c>docs/SimCore-API.md</c> §3.1 冻结）。
/// </summary>
/// <remarks>
/// <para>语义：把路网里任意两条 <see cref="Turn"/> 之间的"是否冲突"表达成一张可查询的矩阵，
/// 对应交通工程标准的三类冲突点（技术方案02 §2.2(a)）：分流点（同起点，不冲突）、
/// 合流点（同终点）、交叉点（几何相交）。编辑器"画完几何即自动得到冲突矩阵"
/// （技术方案02 §2.3/§2.4）与运行期路口门控（<c>IntersectionGate</c>）共用本判定，
/// <b>不得重复实现</b>（验收 T4.2 d）。</para>
///
/// <para><b>转向中心线模型</b>：§3.1 要求判定"两条转向中心线折线几何相交"。本实现按
/// 技术方案02 §2.2(a) 附注「冲突矩阵用『车道端点 + 方向』判定即可（不需要高精度几何求交）」
/// 的裁定，把每条转向的中心线建模为一条<b>两点折线（弦）</b>：
/// <c>[入口点, 出口点]</c>。设转向所在节点坐标为 <c>N</c>、源车道行驶方向为 <c>u_src</c>、
/// 目标车道行驶方向为 <c>u_dst</c>（均为单位向量），则
/// <c>入口点 = N − u_src·D + Right(u_src)·offset(srcLane)</c>、
/// <c>出口点 = N + u_dst·D + Right(u_dst)·offset(dstLane)</c>；
/// 其中 <c>D</c> 为弦在节点两侧的伸出深度、<c>Right(v) = (v.Y, −v.X)</c> 为右法向、
/// <c>offset(lane) = (lane.LaneIndex + 0.5)·LaneWidth</c> 为按 <see cref="Lane.LaneIndex"/>
/// 推出的车道中心横向偏移。<b>横向偏移</b>保证对向车道（如进口道与其对向出口道）在几何上分离，
/// 从而"车道端点 + 方向"即可稳定区分分流 / 合流 / 交叉三类关系。</para>
///
/// <para><b>确定性</b>（铁律 2）：几何与冲突集在 <see cref="From(CompiledNetwork)"/> 时一次性算好，
/// 结果只依赖输入路网；同一路网两次编译，<see cref="ConflictsOf(int)"/> 逐项相等。</para>
/// </remarks>
public sealed class ConflictMatrix
{
    /// <summary>几何求交的坐标容差（米）／叉积容差。</summary>
    private const double Epsilon = 1e-9d;

    /// <summary>车道横向宽度（米）：由 <see cref="Lane.LaneIndex"/> 推出的车道中心相对道路中心线的横向偏移单位。</summary>
    private const double LaneWidth = 3.5d;

    /// <summary>转向中心线弦在节点两侧的伸出深度（米）。</summary>
    private const double ChordDepth = 6.0d;

    private readonly CompiledNetwork _network;
    private readonly double[] _entryX;
    private readonly double[] _entryY;
    private readonly double[] _exitX;
    private readonly double[] _exitY;
    private readonly int[][] _conflicts;

    private ConflictMatrix(
        CompiledNetwork network,
        double[] entryX,
        double[] entryY,
        double[] exitX,
        double[] exitY,
        int[][] conflicts)
    {
        _network = network;
        _entryX = entryX;
        _entryY = entryY;
        _exitX = exitX;
        _exitY = exitY;
        _conflicts = conflicts;
    }

    /// <summary>
    /// 由原始 <see cref="Network"/> 编译并构建冲突矩阵（§3.1 字面）。
    /// </summary>
    /// <param name="network">路网（只读，不被修改）。</param>
    /// <returns>不可变的冲突矩阵。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="network"/> 为 null。</exception>
    /// <exception cref="ArgumentException">节点 / 车道 / 转向的 id 非 0..N-1 连续或存在重复（<see cref="Network.Compile"/>）。</exception>
    public static ConflictMatrix From(Network network)
    {
        ArgumentNullException.ThrowIfNull(network);
        return From(network.Compile());
    }

    /// <summary>
    /// 由已编译路网构建冲突矩阵（§3.1 便利重载）。
    /// </summary>
    /// <param name="network">编译路网（只读，不被修改）。</param>
    /// <returns>不可变的冲突矩阵。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="network"/> 为 null。</exception>
    public static ConflictMatrix From(CompiledNetwork network)
    {
        ArgumentNullException.ThrowIfNull(network);

        int turnCount = network.TurnCount;
        var entryX = new double[turnCount];
        var entryY = new double[turnCount];
        var exitX = new double[turnCount];
        var exitY = new double[turnCount];

        for (int id = 0; id < turnCount; id++)
        {
            Turn turn = network.GetTurn(id);
            Lane src = network.GetLane(turn.SrcLane);
            Lane dst = network.GetLane(turn.DstLane);
            Node node = network.GetNode(turn.Node);

            (double sux, double suy) = Direction(network, src);
            (double dux, double duy) = Direction(network, dst);

            double srcOffset = OffsetOf(src);
            double dstOffset = OffsetOf(dst);

            // 入口 = 节点 − u_src·D + Right(u_src)·offset；Right(v) = (v.Y, −v.X)。
            entryX[id] = node.X - (sux * ChordDepth) + (suy * srcOffset);
            entryY[id] = node.Y - (suy * ChordDepth) - (sux * srcOffset);

            // 出口 = 节点 + u_dst·D + Right(u_dst)·offset。
            exitX[id] = node.X + (dux * ChordDepth) + (duy * dstOffset);
            exitY[id] = node.Y + (duy * ChordDepth) - (dux * dstOffset);
        }

        var lists = new List<int>[turnCount];
        for (int id = 0; id < turnCount; id++)
        {
            lists[id] = new List<int>();
        }

        for (int a = 0; a < turnCount; a++)
        {
            for (int b = a + 1; b < turnCount; b++)
            {
                if (!ConflictsCore(network, a, b, entryX, entryY, exitX, exitY))
                {
                    continue;
                }

                lists[a].Add(b);   // a < b：升序
                lists[b].Add(a);   // a 递增：升序
            }
        }

        var conflicts = new int[turnCount][];
        for (int id = 0; id < turnCount; id++)
        {
            conflicts[id] = lists[id].ToArray();
        }

        return new ConflictMatrix(network, entryX, entryY, exitX, exitY, conflicts);
    }

    /// <summary>
    /// 判定两条转向是否冲突（§3.1 字面，实例成员）。
    /// </summary>
    /// <param name="a">转向 a。</param>
    /// <param name="b">转向 b。</param>
    /// <returns>冲突（对称）为 <c>true</c>，否则 <c>false</c>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="a"/> 或 <paramref name="b"/> 为 null。</exception>
    /// <exception cref="ArgumentOutOfRangeException">其 id 越界。</exception>
    public bool AreConflicting(Turn a, Turn b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return AreConflicting(a.Id, b.Id);
    }

    /// <summary>
    /// 判定两条转向（按 id）是否冲突（§3.1 原始类型友好重载）。
    /// </summary>
    /// <param name="turnIdA">转向 id a。</param>
    /// <param name="turnIdB">转向 id b。</param>
    /// <returns>冲突（对称）为 <c>true</c>，否则 <c>false</c>。</returns>
    /// <exception cref="ArgumentOutOfRangeException">任一 id 越界。</exception>
    public bool AreConflicting(int turnIdA, int turnIdB)
    {
        CheckTurn(turnIdA);
        CheckTurn(turnIdB);

        if (turnIdA == turnIdB)
        {
            return false;                                    // 规则 1：同一条转向不自冲突
        }

        Turn a = _network.GetTurn(turnIdA);
        Turn b = _network.GetTurn(turnIdB);

        if (a.SrcLane == b.SrcLane)
        {
            return false;                                    // 规则 2：同起点不冲突（分流点）
        }

        if (a.DstLane == b.DstLane)
        {
            return true;                                     // 规则 3：同终点冲突（合流点）
        }

        // 规则 4：转向中心线折线几何相交（交叉点）。
        return SegmentsIntersect(
            _entryX[turnIdA], _entryY[turnIdA], _exitX[turnIdA], _exitY[turnIdA],
            _entryX[turnIdB], _entryY[turnIdB], _exitX[turnIdB], _exitY[turnIdB]);
    }

    /// <summary>
    /// 某条转向的冲突转向 id 集合（升序、去重、不含自身，§3.1）。
    /// </summary>
    /// <param name="turnId">转向 id。</param>
    /// <returns>与该转向冲突的全部转向 id。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="turnId"/> 越界。</exception>
    public IReadOnlyList<int> ConflictsOf(int turnId)
    {
        CheckTurn(turnId);
        return _conflicts[turnId];
    }

    /// <summary>
    /// 本矩阵编译所用的编译路网（<b>内部</b>，非冻结 public 面）。
    /// 供 <c>IntersectionGate.Build</c> 校验"冲突矩阵与路网同源"。
    /// </summary>
    internal CompiledNetwork SourceNetwork => _network;

    // ------------------------------------------------------------------
    // 内部：判定与几何
    // ------------------------------------------------------------------

    /// <summary>无校验的判定核心（用于 <see cref="From(CompiledNetwork)"/> 预计算）。</summary>
    private static bool ConflictsCore(
        CompiledNetwork network,
        int a,
        int b,
        double[] entryX,
        double[] entryY,
        double[] exitX,
        double[] exitY)
    {
        Turn ta = network.GetTurn(a);
        Turn tb = network.GetTurn(b);

        if (ta.SrcLane == tb.SrcLane)
        {
            return false;   // 规则 2
        }

        if (ta.DstLane == tb.DstLane)
        {
            return true;    // 规则 3
        }

        return SegmentsIntersect(
            entryX[a], entryY[a], exitX[a], exitY[a],
            entryX[b], entryY[b], exitX[b], exitY[b]);
    }

    /// <summary>取车道行驶方向单位向量（<c>FromNode → ToNode</c>）；零长度车道退化为零向量。</summary>
    private static (double X, double Y) Direction(CompiledNetwork network, Lane lane)
    {
        Node from = network.GetNode(lane.FromNode);
        Node to = network.GetNode(lane.ToNode);

        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        double length = Math.Sqrt((dx * dx) + (dy * dy));

        return length < Epsilon ? (0d, 0d) : (dx / length, dy / length);
    }

    /// <summary>车道中心相对道路中心线的横向偏移量（米）：<c>(LaneIndex + 0.5)·LaneWidth</c>。</summary>
    private static double OffsetOf(Lane lane) => (lane.LaneIndex + 0.5d) * LaneWidth;

    /// <summary>两线段是否相交（含共线重叠与端点相接；闭线段判定）。</summary>
    private static bool SegmentsIntersect(
        double a1x, double a1y, double a2x, double a2y,
        double b1x, double b1y, double b2x, double b2y)
    {
        double d1 = Cross(b1x, b1y, b2x, b2y, a1x, a1y);
        double d2 = Cross(b1x, b1y, b2x, b2y, a2x, a2y);
        double d3 = Cross(a1x, a1y, a2x, a2y, b1x, b1y);
        double d4 = Cross(a1x, a1y, a2x, a2y, b2x, b2y);

        bool straddleA = (d1 > Epsilon && d2 < -Epsilon) || (d1 < -Epsilon && d2 > Epsilon);
        bool straddleB = (d3 > Epsilon && d4 < -Epsilon) || (d3 < -Epsilon && d4 > Epsilon);
        if (straddleA && straddleB)
        {
            return true;   // 真穿越
        }

        if (Math.Abs(d1) <= Epsilon && OnSegment(b1x, b1y, b2x, b2y, a1x, a1y))
        {
            return true;
        }

        if (Math.Abs(d2) <= Epsilon && OnSegment(b1x, b1y, b2x, b2y, a2x, a2y))
        {
            return true;
        }

        if (Math.Abs(d3) <= Epsilon && OnSegment(a1x, a1y, a2x, a2y, b1x, b1y))
        {
            return true;
        }

        if (Math.Abs(d4) <= Epsilon && OnSegment(a1x, a1y, a2x, a2y, b2x, b2y))
        {
            return true;
        }

        return false;
    }

    /// <summary>叉积 <c>(q − p) × (r − p)</c>。</summary>
    private static double Cross(double px, double py, double qx, double qy, double rx, double ry)
        => ((qx - px) * (ry - py)) - ((qy - py) * (rx - px));

    /// <summary>点 r 是否落在线段 pq 的包围盒内（配合共线叉积使用）。</summary>
    private static bool OnSegment(double px, double py, double qx, double qy, double rx, double ry)
        => rx >= Math.Min(px, qx) - Epsilon
        && rx <= Math.Max(px, qx) + Epsilon
        && ry >= Math.Min(py, qy) - Epsilon
        && ry <= Math.Max(py, qy) + Epsilon;

    private void CheckTurn(int turnId)
    {
        if ((uint)turnId >= (uint)_network.TurnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(turnId), turnId, "转向 id 越界。");
        }
    }
}
