using System.Collections.Generic;
using System.Linq;
using Godot;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;

public partial class EditorBridge : Node
{
	private static readonly JsonSerializerOptions Opts = new JsonSerializerOptions
	{
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		// ★ 必须加：否则枚举（Severity / TurnType / StageType）会序列化成数字
		Converters = { new JsonStringEnumConverter() },
	};

	// 读 .spmap 并校验，返回错误数组 JSON；解析失败也返回一条错误（不抛）。
	public string ValidateFile(string osPath)
	{
		try
		{
			var level = SimCore.Io.Spmap.Load(osPath);
			var errs  = SimCore.Io.SpmapValidator.Validate(level);
			return JsonSerializer.Serialize(errs, Opts);
		}
		catch (System.Exception ex)
		{
			var one = new[] { new {
				Code = "load_failed", Message = ex.Message, Path = "", Severity = "Error"
			}};
			return JsonSerializer.Serialize(one, Opts);
		}
	}
	// 某路口内【互相冲突】的转向 id 对（ConflictMatrix —— 与运行期门控同源）
	// 返回 {"ok":true,"pairs":[[a,b],...]}；失败 {"ok":false,"error":"..."}
	public string ConflictsAt(string osPath, int nodeId)
	{
		try
		{
			var level = SimCore.Io.Spmap.Load(osPath);
			var net = level.Network.Compile();
			var conflicts = SimCore.Geometry.ConflictMatrix.From(net);
			var atNode = new HashSet<int>(net.TurnsAtNode(nodeId));
			var seen = new HashSet<long>();
			var pairs = new List<int[]>();
			foreach (var a in atNode)
			{
				foreach (var b in conflicts.ConflictsOf(a))
				{
					if (a == b || !atNode.Contains(b)) continue;   // 只留本路口内部的一对
					long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
					if (!seen.Add(key)) continue;                  // 去重（冲突是对称关系）
					pairs.Add(new[] { a, b });
				}
			}
			return JsonSerializer.Serialize(new { ok = true, pairs = pairs }, Opts);
		}
		catch (System.Exception ex)
		{
			return JsonSerializer.Serialize(new { ok = false, error = ex.Message }, Opts);
		}
	}
	// 自动推导【全网络】转向（TurnGenerator），返回 {"ok":true,"turns":[...]}
	// 失败返回 {"ok":false,"error":"..."}。
	// ★ TurnGenerator 不负责与既有 turns 合并、也不去重：过滤 / 去重 / 分配 id 由 GDScript 侧做。
	public string DeriveTurns(string osPath)
	{
		try
		{
			var level = SimCore.Io.Spmap.Load(osPath);
			var turns = SimCore.Geometry.TurnGenerator.FromGeometry(level.Network);
			var dtos = turns.Select(t => new Dictionary<string, object?>
			{
				["id"]       = t.Id,
				["src_lane"] = t.SrcLane,
				["dst_lane"] = t.DstLane,
				["node"]     = t.Node,
				["type"]     = t.Type,          // 枚举 → 名字字符串（Opts 里的转换器）
			}).ToArray();
			return JsonSerializer.Serialize(new { ok = true, turns = dtos }, Opts);
		}
		catch (System.Exception ex)
		{
			return JsonSerializer.Serialize(new { ok = false, error = ex.Message }, Opts);
		}
	}

	// 自动生成某路口的相位候选集（PhaseAutoGenerator），返回 {"ok":true,"plans":[...]}
	// ★ 该节点必须【已有 turns】，否则 Suggest 抛 ArgumentException —— 所以必须先 DeriveTurns 并入路网。
	public string SuggestPhases(string osPath, int nodeId)
	{
		try
		{
			var level = SimCore.Io.Spmap.Load(osPath);
			var plans = SimCore.Geometry.PhaseAutoGenerator.Suggest(level.Network, nodeId);
			var dtos = plans.Select(p => new Dictionary<string, object?>
			{
				["start_time"] = p.StartTime,
				["stages"] = p.Stages.Select(s => new Dictionary<string, object?>
				{
					["protected"] = s.ProtectedTurnIds,
					["permitted"] = s.PermittedTurnIds,
					["type"]      = s.Type,
					["duration"]  = s.Duration,
				}).ToArray(),
			}).ToArray();
			return JsonSerializer.Serialize(new { ok = true, plans = dtos }, Opts);
		}
		catch (System.Exception ex)
		{
			return JsonSerializer.Serialize(new { ok = false, error = ex.Message }, Opts);
		}
	}
}
