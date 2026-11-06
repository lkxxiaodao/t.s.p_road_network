namespace SimCore.Io;

/// <summary>
/// 关卡元数据（技术方案03 §3.2 <c>meta</c>：<c>{ name, author, chapter, index }</c>）。
/// </summary>
/// <remarks>
/// <para><see cref="Chapter"/> / <see cref="Index"/> 对应章节关卡制（游戏设计v2 §5）：如 1-1 即 chapter=1、index=1。
/// 供选关页展示与排序，不参与仿真与评分。</para>
/// <para>纯数据载体；<see cref="Name"/> / <see cref="Author"/> 缺省为空串，不写 <c>null</c>。</para>
/// </remarks>
public sealed class LevelMeta
{
    /// <summary>关卡名（§3.2 <c>meta.name</c>）。</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>作者（§3.2 <c>meta.author</c>；UGC 场景记为制图者）。</summary>
    public string Author { get; init; } = string.Empty;

    /// <summary>章节号，1 起（§3.2 <c>meta.chapter</c>）。</summary>
    public int Chapter { get; init; }

    /// <summary>关内序号，1 起（§3.2 <c>meta.index</c>）。</summary>
    public int Index { get; init; }
}
