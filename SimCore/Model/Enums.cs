namespace SimCore.Model;

// ---------------------------------------------------------------------------
// 技术方案03 §3.1 · SimCore.Model 的枚举定义。
// 【冻结对齐】成员名称与出现顺序与 §3.1 严格一致，不得改名、增删或调序。
//              .spmap 序列化（T0.3）按字符串名映射，顺序即数值，务必保持稳定。
// 【本任务范围】仅定义枚举；模型层不含任何仿真逻辑（本文件纯声明）。
// ---------------------------------------------------------------------------

/// <summary>
/// 车道类型（技术方案03 §3.1）。
/// <para>映射游戏要素（技术方案02 §2.5、游戏设计v2 §5/§9）：
/// <see cref="Driving"/>=机动车道、<see cref="Bus"/>=公交专用道、<see cref="Biking"/>=非机动车道、
/// <see cref="Sidewalk"/>=人行道、<see cref="SharedLeftTurn"/>=左转专用/共享左转车道、
/// <see cref="Construction"/>=施工封道/抛锚占道、<see cref="Parking"/>=路边停车。</para>
/// </summary>
public enum LaneType
{
    Driving,
    Bus,
    Biking,
    Sidewalk,
    SharedLeftTurn,
    Construction,
    Parking,
}

/// <summary>
/// 转向类型（技术方案03 §3.1）。借 A/B Street <c>Turn::angle()</c> 的几何分类
/// （技术方案02 §2.4）：直行 / 左转 / 右转 / 掉头，外加人行横道（Crosswalk）。
/// </summary>
public enum TurnType
{
    Straight,
    Left,
    Right,
    UTurn,
    Crosswalk,
}

/// <summary>
/// 转向优先级（技术方案03 §3.1，借 A/B Street <c>TurnPriority</c>）。
/// <para>语义（技术方案02 §2.2b）：<see cref="Protected"/>=保护放行（本相位内不得与其他保护流向冲突）；
/// <see cref="Yield"/>=允许通行但须让行；<see cref="Banned"/>=禁行（红灯）。</para>
/// <para>本模型中该枚举由 <see cref="Stage.PriorityOf(int)"/> 从相位（Stage）的
/// protected/permitted 集合派生，与 §3.2 的 <c>stages[].protected/permitted</c> 字段对应。</para>
/// </summary>
public enum TurnPriority
{
    Banned,
    Yield,
    Protected,
}

/// <summary>
/// 节点类型（技术方案03 §3.1；对齐 LightSim <c>ORIGIN/DESTINATION/SIGNALIZED/UNSIGNALIZED</c>，
/// 见技术方案01 §3.1）：信号路口 / 无信号路口 / 需求源 / 需求汇。
/// </summary>
public enum NodeType
{
    Signalized,
    Unsignalized,
    Origin,
    Destination,
}

/// <summary>
/// 相位（Stage）配时类型（支撑技术方案03 §3.2 的 <c>stages[].type</c> 字段）。
/// <para><see cref="Fixed"/>=固定配时（基础模式）；<see cref="Variable"/>=感应控制（高级/智能交通，
/// 见技术方案02 §2.2c）。§3.1 未显式列出本枚举，此处仅为让 §3.2 的 <c>type</c> 字段有强类型载体，
/// 可变相位所需的具体参数（最小/延迟/可延长）留待 T1.2 与 T0.4 补全。</para>
/// </summary>
public enum StageType
{
    Fixed,
    Variable,
}
