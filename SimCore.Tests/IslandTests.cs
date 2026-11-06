using System.Text.Json.Nodes;
using SimCore.Io;
using SimCore.Model;
using Xunit;

namespace SimCore.Tests;

/// <summary>
/// 行人二次过街（安全岛）契约验收 —— 2026-10-24 新增（E92 G2）。
/// <para>分三组：
/// <list type="bullet">
///   <item>A 组：读写 —— 旧关卡不写 <c>islands</c> / <c>leg</c>，新关卡往返逐字段一致</item>
///   <item>B 组：校验器 —— 每一条非法写法都报出对应校验码</item>
///   <item>C 组：兼容 —— 缺省值、编译</item>
/// </list>
/// </para>
/// </summary>
public sealed class IslandTests
{
    // ------------------------------------------------------------------
    // 关卡构造：两个节点 + 两条人行道车道 + 一条（或两条）Crosswalk 转向
    // ------------------------------------------------------------------

    private static Level MakeLevel(
        IReadOnlyList<Island>? islands = null,
        int turn0Leg = -1,
        int turn0Island = -1,
        int turn1Leg = -2,
        int turn1Island = -1,
        bool secondTurnSameLanes = true)
    {
        var nodes = new List<Node>
        {
            new() { Id = 0, Type = NodeType.Unsignalized, X = 0, Y = 0 },
            new() { Id = 1, Type = NodeType.Unsignalized, X = 0, Y = 20 },
            new() { Id = 2, Type = NodeType.Unsignalized, X = 0, Y = -20 },
        };

        var lanes = new List<Lane>
        {
            // 南侧人行道：2 → 0；北侧人行道：0 → 1
            new() { Id = 0, Road = 0, FromNode = 2, ToNode = 0, Type = LaneType.Sidewalk, Length = 20, SpeedLimit = 1.4, LaneIndex = 0 },
            new() { Id = 1, Road = 1, FromNode = 0, ToNode = 1, Type = LaneType.Sidewalk, Length = 20, SpeedLimit = 1.4, LaneIndex = 0 },
        };

        var turns = new List<Turn>
        {
            new()
            {
                Id = 0, SrcLane = 0, DstLane = 1, Node = 0, Type = TurnType.Crosswalk,
                Leg = turn0Leg, IslandId = turn0Island,
            },
        };

        // turn1Leg == -2 表示"不要第二条转向"
        if (turn1Leg != -2)
        {
            turns.Add(new Turn
            {
                Id = 1,
                SrcLane = secondTurnSameLanes ? 0 : 1,
                DstLane = secondTurnSameLanes ? 1 : 0,
                Node = 0,
                Type = TurnType.Crosswalk,
                Leg = turn1Leg,
                IslandId = turn1Island,
            });
        }

        return new Level
        {
            Meta = new LevelMeta { Name = "island-test", Author = "test", Chapter = 3, Index = 3 },
            Network = new Network
            {
                Nodes = nodes,
                Lanes = lanes,
                Turns = turns,
                Islands = islands ?? Array.Empty<Island>(),
            },
        };
    }

    private static Island[] OneIsland(int capacity = 6) =>
        [new Island { Id = 0, X = 0, Y = 10, Capacity = capacity }];

    private static string TempFile(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), "simcore-island-tests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    private static List<string> Codes(Level level) =>
        SpmapValidator.Validate(level).Select(e => e.Code).ToList();

    // ------------------------------------------------------------------
    // A 组：读写
    // ------------------------------------------------------------------

    /// <summary>A1：没有岛的旧关卡，写出的 JSON 里**不出现** islands 键，turns 里也不出现 leg / island。</summary>
    [Fact]
    public void A1_Legacy_Level_Writes_No_Island_Keys()
    {
        string path = TempFile("a1-legacy.spmap");
        Spmap.Save(MakeLevel(), path);
        string json = File.ReadAllText(path);

        Assert.DoesNotContain("\"islands\"", json);
        Assert.DoesNotContain("\"leg\"", json);
        Assert.DoesNotContain("\"island\"", json);
    }

    /// <summary>A2：带一个岛 + 两段的关卡，保存 → 加载，岛与两段段号逐字段一致。</summary>
    [Fact]
    public void A2_RoundTrip_Preserves_Islands_And_Legs()
    {
        Level original = MakeLevel(OneIsland(8), turn0Leg: 0, turn0Island: 0, turn1Leg: 1, turn1Island: 0);

        string path = TempFile("a2-islands.spmap");
        Spmap.Save(original, path);
        Level reloaded = Spmap.Load(path);

        Assert.Single(reloaded.Network.Islands);
        Island isl = reloaded.Network.Islands[0];
        Assert.Equal(0, isl.Id);
        Assert.Equal(0d, isl.X);
        Assert.Equal(10d, isl.Y);
        Assert.Equal(8, isl.Capacity);

        Assert.Equal(2, reloaded.Network.Turns.Count);
        Assert.Equal(0, reloaded.Network.Turns[0].Leg);
        Assert.Equal(0, reloaded.Network.Turns[0].IslandId);
        Assert.Equal(1, reloaded.Network.Turns[1].Leg);
        Assert.Equal(0, reloaded.Network.Turns[1].IslandId);

        // 再存一次应当字节稳定（幂等）
        string path2 = TempFile("a2-islands-2.spmap");
        Spmap.Save(reloaded, path2);
        Assert.Equal(File.ReadAllText(path), File.ReadAllText(path2));
    }

    /// <summary>A3：字段名与 schema 逐字一致（<c>islands[].id/x/y/capacity</c>、<c>turns[].leg/island</c>）。</summary>
    [Fact]
    public void A3_Field_Names_Match_Schema()
    {
        string path = TempFile("a3-names.spmap");
        Spmap.Save(MakeLevel(OneIsland(), turn0Leg: 0, turn0Island: 0, turn1Leg: 1, turn1Island: 0), path);
        JsonNode root = JsonNode.Parse(File.ReadAllText(path))!;

        JsonNode island = root["islands"]![0]!;
        Assert.Equal(0, (int)island["id"]!);
        Assert.Equal(0d, (double)island["x"]!);
        Assert.Equal(10d, (double)island["y"]!);
        Assert.Equal(6, (int)island["capacity"]!);

        Assert.Equal(0, (int)root["turns"]![0]!["leg"]!);
        Assert.Equal(0, (int)root["turns"]![0]!["island"]!);
    }

    // ------------------------------------------------------------------
    // B 组：校验器
    // ------------------------------------------------------------------

    /// <summary>B1：合法的两段过街 → 没有 islands/leg 相关错误。</summary>
    [Fact]
    public void B1_Valid_TwoLeg_Crossing_Is_Clean()
    {
        Level level = MakeLevel(OneIsland(), turn0Leg: 0, turn0Island: 0, turn1Leg: 1, turn1Island: 0);
        List<string> codes = Codes(level);

        Assert.DoesNotContain("island_capacity_invalid", codes);
        Assert.DoesNotContain("crosswalk_leg_incomplete", codes);
        Assert.DoesNotContain("crosswalk_leg_not_crosswalk", codes);
        Assert.DoesNotContain("crosswalk_leg_invalid", codes);
        Assert.DoesNotContain("crosswalk_island_missing", codes);
        Assert.DoesNotContain("crosswalk_leg_duplicated", codes);
        Assert.DoesNotContain("crosswalk_leg_missing", codes);
        Assert.DoesNotContain("crosswalk_leg_mismatch", codes);
    }

    /// <summary>B2：岛容量 0 → island_capacity_invalid。</summary>
    [Fact]
    public void B2_Capacity_Zero_Is_Reported()
    {
        Level level = MakeLevel(OneIsland(0), turn0Leg: 0, turn0Island: 0, turn1Leg: 1, turn1Island: 0);
        Assert.Contains("island_capacity_invalid", Codes(level));
    }

    /// <summary>B3：只有 leg、没有 island（或反之）→ crosswalk_leg_incomplete。</summary>
    [Fact]
    public void B3_Leg_And_Island_Must_Pair()
    {
        Level onlyLeg = MakeLevel(OneIsland(), turn0Leg: 0, turn0Island: -1);
        Assert.Contains("crosswalk_leg_incomplete", Codes(onlyLeg));

        Level onlyIsland = MakeLevel(OneIsland(), turn0Leg: -1, turn0Island: 0);
        Assert.Contains("crosswalk_leg_incomplete", Codes(onlyIsland));
    }

    /// <summary>B4：非 Crosswalk 转向带了 leg → crosswalk_leg_not_crosswalk。</summary>
    [Fact]
    public void B4_Leg_On_Non_Crosswalk_Is_Reported()
    {
        Level level = MakeLevel(OneIsland(), turn0Leg: 0, turn0Island: 0);
        // 把那条转向改成直行
        Turn patched = level.Network.Turns[0];
        Level broken = new()
        {
            Meta = level.Meta,
            Network = new Network
            {
                Nodes = level.Network.Nodes,
                Lanes = level.Network.Lanes,
                Islands = level.Network.Islands,
                Turns =
                [
                    new Turn
                    {
                        Id = patched.Id, SrcLane = patched.SrcLane, DstLane = patched.DstLane,
                        Node = patched.Node, Type = TurnType.Straight,
                        Leg = 0, IslandId = 0,
                    },
                ],
            },
        };

        Assert.Contains("crosswalk_leg_not_crosswalk", Codes(broken));
    }

    /// <summary>B5：leg 只能 0/1 → crosswalk_leg_invalid。</summary>
    [Fact]
    public void B5_Leg_Out_Of_Range_Is_Reported()
    {
        Level level = MakeLevel(OneIsland(), turn0Leg: 2, turn0Island: 0);
        Assert.Contains("crosswalk_leg_invalid", Codes(level));
    }

    /// <summary>B6：引用的岛不存在 → crosswalk_island_missing。</summary>
    [Fact]
    public void B6_Missing_Island_Is_Reported()
    {
        Level level = MakeLevel(OneIsland(), turn0Leg: 0, turn0Island: 9);
        Assert.Contains("crosswalk_island_missing", Codes(level));
    }

    /// <summary>B7：只有第 0 段（缺第 1 段）→ crosswalk_leg_missing。</summary>
    [Fact]
    public void B7_Missing_Second_Leg_Is_Reported()
    {
        Level level = MakeLevel(OneIsland(), turn0Leg: 0, turn0Island: 0);
        Assert.Contains("crosswalk_leg_missing", Codes(level));
    }

    /// <summary>B8：只有第 1 段（缺第 0 段）→ crosswalk_leg_missing。</summary>
    [Fact]
    public void B8_Missing_First_Leg_Is_Reported()
    {
        Level level = MakeLevel(OneIsland(), turn0Leg: 1, turn0Island: 0);
        Assert.Contains("crosswalk_leg_missing", Codes(level));
    }

    /// <summary>B9：两段的 src/dst 不是同一对 → crosswalk_leg_mismatch。</summary>
    [Fact]
    public void B9_Leg_Lane_Mismatch_Is_Reported()
    {
        Level level = MakeLevel(OneIsland(), turn0Leg: 0, turn0Island: 0,
            turn1Leg: 1, turn1Island: 0, secondTurnSameLanes: false);
        Assert.Contains("crosswalk_leg_mismatch", Codes(level));
    }

    /// <summary>B10：同一个岛的第 0 段出现两条 → crosswalk_leg_duplicated。</summary>
    [Fact]
    public void B10_Duplicated_Leg_Is_Reported()
    {
        Level level = MakeLevel(OneIsland(), turn0Leg: 0, turn0Island: 0,
            turn1Leg: 0, turn1Island: 0);
        Assert.Contains("crosswalk_leg_duplicated", Codes(level));
    }

    // ------------------------------------------------------------------
    // C 组：兼容性
    // ------------------------------------------------------------------

    /// <summary>C1：<see cref="Turn.Leg"/> / <see cref="Turn.IslandId"/> 缺省为 -1（旧代码构造的 Turn 不受影响）。</summary>
    [Fact]
    public void C1_Turn_Defaults_Are_Unset()
    {
        var turn = new Turn { Id = 0, SrcLane = 0, DstLane = 1, Node = 0, Type = TurnType.Crosswalk };
        Assert.Equal(-1, turn.Leg);
        Assert.Equal(-1, turn.IslandId);
    }

    /// <summary>C2：<see cref="Network.Islands"/> 缺省为空，<c>Compile()</c> 照常工作（岛不参与拓扑编译）。</summary>
    [Fact]
    public void C2_Compile_Works_With_And_Without_Islands()
    {
        Level withIsland = MakeLevel(OneIsland(), turn0Leg: 0, turn0Island: 0, turn1Leg: 1, turn1Island: 0);
        CompiledNetwork compiled = withIsland.Network.Compile();
        Assert.Equal(2, compiled.Turns.Count);
        Assert.Equal(0, compiled.Turns[0].Leg);
        Assert.Equal(1, compiled.Turns[1].Leg);

        Level legacy = MakeLevel();
        Assert.Empty(legacy.Network.Islands);
        Assert.NotNull(legacy.Network.Compile());
    }
}
