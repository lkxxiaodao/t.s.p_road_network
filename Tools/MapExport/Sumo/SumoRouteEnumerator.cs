using SimCore.Model;

namespace SignalPlanner.MapExport.Sumo;

/// <summary>一条完整路线（从某条车道出发，一路走到底）。</summary>
internal sealed class SumoRouteCandidate
{
    /// <summary>途经的 .spmap 车道 id（含起点与终点）。</summary>
    internal IReadOnlyList<int> LanePath { get; init; } = Array.Empty<int>();

    /// <summary>net.xml 的 edge 序列（相邻重复已合并）。</summary>
    internal IReadOnlyList<string> Edges { get; init; } = Array.Empty<string>();

    /// <summary>相对权重 = 沿途各转向权重的乘积（未归一化）。</summary>
    internal double Weight { get; init; }

    /// <summary>终点车道 id（没有后继转向的那条）。</summary>
    internal int EndLane { get; init; }
}

/// <summary>
/// 按转率把"某个进口"展开成若干条完整路线。
///
/// <para><b>为什么必须有它</b>：SUMO 的 <c>route</c> 是 edge 序列，写错一条 SUMO 就直接退出
/// （不是警告）。而本编辑器的用户面对的是车道 / 转向 / 转率，不该手写路线。</para>
///
/// <para><b>转率从哪来</b>：<c>.spmap</c> 的 <c>turns[].ratio</c>，<b>值就是权重</b>，导出时按进口归一化。</para>
/// <para>★ 2026-11-05 去掉了"全 1.0 = 未配置 → 走类型默认"的哨兵语义：
/// 1:1:1 也是合法需求，不能被当成"没配"。默认值由编辑器在建 turn / 导入旧文件时
/// <b>实体写进 ratio</b>（见 <c>Doc.DEFAULT_TURN_WEIGHT</c> / <c>Doc.fill_default_ratios</c>），
/// 这里只负责按权重归一化。</para>
/// <para>权重为 0 的转向不生成路线（容错：编辑器不允许填 0，但手写文件可能有）。</para>
///
/// <para><b>限制</b>：行驶深度上限 <see cref="MaxDepth"/> 跳（防环路无限展开）；
/// 无路可走（起点没有出口、或路线断在中间）由调用方报可读错误。</para>
/// </summary>
internal static class SumoRouteEnumerator
{
    /// <summary>单条路线最多经过多少个路口（防环路）。</summary>
    internal const int MaxDepth = 64;

    /// <summary>
    /// 从 <paramref name="startLane"/> 出发枚举所有路线。
    /// </summary>
    /// <param name="graph">导出路网。</param>
    /// <param name="startLane">起点车道 id。</param>
    /// <param name="laneToEdge">.spmap 车道 id → net.xml 的 edge id。</param>
    /// <param name="shortestOnly">true = 忽略转率，只保留"经过路口最少"的那一批（强制最短路）。</param>
    /// <param name="onLoopTrim">因深度上限被截断的路线数（诊断用）。</param>
    internal static List<SumoRouteCandidate> Enumerate(
        ExportGraph graph,
        int startLane,
        IReadOnlyDictionary<int, string> laneToEdge,
        bool shortestOnly,
        out int onLoopTrim)
    {
        onLoopTrim = 0;

        // 出发车道 → 它的出口转向（只认能写进 OpenDRIVE 的：机动车 / 同类自行车、人行道）
        var outgoing = new Dictionary<int, List<ExportTurn>>();
        foreach (ExportTurn turn in graph.Turns)
        {
            if (!turn.ShouldExport)
            {
                continue;
            }

            int src = turn.Src.Id;
            if (!outgoing.TryGetValue(src, out List<ExportTurn>? list))
            {
                list = new List<ExportTurn>();
                outgoing[src] = list;
            }

            list.Add(turn);
        }

        var results = new List<SumoRouteCandidate>();
        var lanes = new List<int>();
        var edges = new List<string>();
        int trimmed = 0;

        void Walk(int lane, double weight, int depth)
        {
            lanes.Add(lane);
            if (laneToEdge.TryGetValue(lane, out string? edge) &&
                (edges.Count == 0 || !string.Equals(edges[^1], edge, StringComparison.Ordinal)))
            {
                edges.Add(edge);
            }

            List<ExportTurn>? outs = outgoing.TryGetValue(lane, out List<ExportTurn>? found)
                ? found
                : null;

            if (outs == null || outs.Count == 0)
            {
                // 走到头了：这条就是一条完整路线
                results.Add(new SumoRouteCandidate
                {
                    LanePath = lanes.ToArray(),
                    Edges = edges.ToArray(),
                    Weight = weight,
                    EndLane = lane,
                });
            }
            else if (depth >= MaxDepth)
            {
                trimmed++;
            }
            else
            {
                foreach (ExportTurn t in outs)
                {
                    double w = Math.Max(0.0, t.Ratio);
                    if (w <= 0.0)
                    {
                        continue;      // 权重 0 = 不生成这条路线（容错，编辑器不允许填 0）
                    }

                    Walk(t.Dst.Id, weight * w, depth + 1);
                }
            }

            lanes.RemoveAt(lanes.Count - 1);
            if (edges.Count > 0 && laneToEdge.TryGetValue(lane, out string? own) &&
                string.Equals(edges[^1], own, StringComparison.Ordinal) &&
                !ContainsEdgeOf(laneToEdge, lanes, own))
            {
                edges.RemoveAt(edges.Count - 1);
            }
        }

        Walk(startLane, 1.0, 0);
        onLoopTrim = trimmed;

        if (shortestOnly && results.Count > 1)
        {
            int best = results.Min(r => r.Edges.Count);
            results = results.Where(r => r.Edges.Count == best).ToList();
        }

        return results;
    }

    /// <summary>回退时判断"这个 edge 是否还被路径上其它车道占用"（避免把共用的 edge 提前弹掉）。</summary>
    private static bool ContainsEdgeOf(IReadOnlyDictionary<int, string> laneToEdge, List<int> lanes, string edge)
    {
        foreach (int lane in lanes)
        {
            if (laneToEdge.TryGetValue(lane, out string? e) && string.Equals(e, edge, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
