namespace SimCore.Model;

/// <summary>
/// 相位（一个放行阶段）。字段对齐技术方案03 §3.2 的 <c>signals[].plans[].stages[]</c>。
/// </summary>
/// <remarks>
/// <para>借 A/B Street 的 <c>Stage { protected_turns, permitted_turns, stage_type }</c>（技术方案02 §2.9）：
/// <see cref="ProtectedTurnIds"/> = 保护放行的转向集合（§3.2 <c>protected</c>）；
/// <see cref="PermittedTurnIds"/> = 允许（须让行）的转向集合（§3.2 <c>permitted</c>）。</para>
/// <para><see cref="PriorityOf(int)"/> 是纯查表分类，把某条转向在本相位下的
/// <see cref="TurnPriority"/> 派生出来（Protected / Yield / Banned），不涉及任何相位推进逻辑
/// （相位推进属 T1.2）。</para>
/// </remarks>
public sealed class Stage
{
    /// <summary>配时类型（§3.2 <c>stages[].type</c>）。</summary>
    public StageType Type { get; init; } = StageType.Fixed;

    /// <summary>本相位时长，秒（§3.2 <c>stages[].duration</c>）。</summary>
    public double Duration { get; init; }

    /// <summary>保护放行的转向 id 集合（§3.2 <c>stages[].protected</c>）。</summary>
    public IReadOnlyList<int> ProtectedTurnIds { get; init; } = Array.Empty<int>();

    /// <summary>允许（须让行）的转向 id 集合（§3.2 <c>stages[].permitted</c>）。</summary>
    public IReadOnlyList<int> PermittedTurnIds { get; init; } = Array.Empty<int>();

    /// <summary>
    /// 派生某条转向在本相位下的优先级：保护集合内→<see cref="TurnPriority.Protected"/>；
    /// 允许集合内→<see cref="TurnPriority.Yield"/>；否则→<see cref="TurnPriority.Banned"/>。
    /// </summary>
    /// <param name="turnId">转向 id。</param>
    /// <returns>该转向的 <see cref="TurnPriority"/>。</returns>
    public TurnPriority PriorityOf(int turnId)
    {
        if (Contains(ProtectedTurnIds, turnId))
        {
            return TurnPriority.Protected;
        }

        if (Contains(PermittedTurnIds, turnId))
        {
            return TurnPriority.Yield;
        }

        return TurnPriority.Banned;
    }

    private static bool Contains(IReadOnlyList<int> ids, int value)
    {
        for (int i = 0; i < ids.Count; i++)
        {
            if (ids[i] == value)
            {
                return true;
            }
        }

        return false;
    }
}
