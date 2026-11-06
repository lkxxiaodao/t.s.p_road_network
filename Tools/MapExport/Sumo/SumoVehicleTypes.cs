using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SignalPlanner.MapExport.Sumo;

/// <summary>
/// 一种车型的完整参数（对应 `.rou.xml` 的一条 &lt;vType&gt;）。
/// </summary>
internal sealed class SumoVehicleType
{
    internal string Id { get; set; } = "car";
    internal string VClass { get; set; } = "passenger";
    internal double Length { get; set; } = 4.5;
    internal double MaxSpeed { get; set; } = 13.89;
    internal double Accel { get; set; } = 2.6;
    internal double Decel { get; set; } = 4.5;
    internal double Sigma { get; set; } = 0.5;
    internal double MinGap { get; set; } = 2.5;
    internal double Tau { get; set; } = 1.0;
    internal double SpeedFactor { get; set; } = 1.0;
    internal string Color { get; set; } = "0.85,0.85,0.90";

    /// <summary>构成比例（同一车流里按它抽车型；0 = 不参与）。</summary>
    internal double Share { get; set; }

    internal bool IsPerson => string.Equals(VClass, "pedestrian", StringComparison.Ordinal);
}

/// <summary>
/// 车型库：6 个预设 × 3 种驾驶风格。
///
/// <para><b>为什么只有 6 个</b>：SUMO 的 <c>vClass</c> 有 30 多种，界面全列出来没人看得懂
/// （技术方案06 §7.2）。这里给 6 个覆盖日常的预设，专业档里仍可写原始 <c>vclass</c> 兜底。</para>
///
/// <para><b>物理参数谁来定</b>：预设给"车本体"（车长 / 极速 / 加减速 / 最小净距），
/// 风格给"司机"（<c>sigma</c> 不完美度 / <c>tau</c> 车头时距 / <c>speedFactor</c> 速度系数）。
/// 两者叠起来就是一条完整的 &lt;vType&gt;，用户不需要知道这些参数叫什么。</para>
/// </summary>
internal static class SumoVehicleTypes
{
    /// <summary>预设名 → 中文（界面用；C# 侧只在提示里用）。</summary>
    internal static readonly (string Key, string Label)[] Presets =
    {
        ("car", "小汽车"),
        ("bus", "公交"),
        ("truck", "货车"),
        ("bicycle", "自行车"),
        ("pedestrian", "行人"),
        ("emergency", "应急"),
    };

    internal static readonly (string Key, string Label)[] Styles =
    {
        ("careful", "保守"),
        ("normal", "标准"),
        ("aggressive", "激进"),
    };

    /// <summary>把 preset 名（或 vclass 名）规范成 6 个预设之一；认不出来当小汽车。</summary>
    internal static string NormalizePreset(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "car";
        }

        string key = raw.Trim().ToLowerInvariant();
        return key switch
        {
            "car" or "passenger" or "private" => "car",
            "bus" => "bus",
            "truck" or "trailer" or "delivery" => "truck",
            "bicycle" or "bike" => "bicycle",
            "pedestrian" or "person" or "foot" => "pedestrian",
            "emergency" or "ambulance" or "firebrigade" or "police" => "emergency",
            _ => "car",
        };
    }

    /// <summary>按预设 + 风格造一条车型。</summary>
    internal static SumoVehicleType Create(string preset, string style, string? id = null, double share = 0.0)
    {
        string p = NormalizePreset(preset);
        var t = new SumoVehicleType { Id = id ?? p };

        switch (p)
        {
            case "bus":
                t.VClass = "bus";
                t.Length = 12.0;
                t.MaxSpeed = 13.89;
                t.Accel = 1.2;
                t.Decel = 4.0;
                t.MinGap = 3.0;
                t.Color = "0.20,0.50,0.90";
                break;
            case "truck":
                t.VClass = "truck";
                t.Length = 8.0;
                t.MaxSpeed = 11.11;
                t.Accel = 1.3;
                t.Decel = 4.0;
                t.MinGap = 3.0;
                t.Color = "0.60,0.60,0.60";
                break;
            case "bicycle":
                t.VClass = "bicycle";
                t.Length = 1.8;
                t.MaxSpeed = 5.0;
                t.Accel = 1.2;
                t.Decel = 3.0;
                t.MinGap = 1.0;
                t.Color = "0.20,0.80,0.40";
                break;
            case "pedestrian":
                t.VClass = "pedestrian";
                t.Length = 0.5;
                t.MaxSpeed = 1.4;
                t.Accel = 1.5;
                t.Decel = 3.0;
                t.MinGap = 0.5;
                t.Color = "0.90,0.70,0.30";
                break;
            case "emergency":
                t.VClass = "emergency";
                t.Length = 5.0;
                t.MaxSpeed = 22.22;
                t.Accel = 3.5;
                t.Decel = 6.0;
                t.MinGap = 2.5;
                t.Color = "0.90,0.20,0.20";
                break;
            default:
                t.VClass = "passenger";
                t.Length = 4.5;
                t.MaxSpeed = 13.89;
                t.Accel = 2.6;
                t.Decel = 4.5;
                t.MinGap = 2.5;
                t.Color = "0.85,0.85,0.90";
                break;
        }

        ApplyStyle(t, style);
        t.Share = share;
        return t;
    }

    /// <summary>风格只改三样：<c>sigma</c>（不完美度）、<c>tau</c>（车头时距）、<c>speedFactor</c>（速度系数）。</summary>
    internal static void ApplyStyle(SumoVehicleType t, string? style)
    {
        switch ((style ?? "normal").Trim().ToLowerInvariant())
        {
            case "careful" or "conservative":
                t.Sigma = 0.2;
                t.Tau = 1.4;
                t.SpeedFactor = 0.85;
                break;
            case "aggressive":
                t.Sigma = 0.9;
                t.Tau = 0.7;
                t.SpeedFactor = 1.15;
                break;
            default:
                t.Sigma = 0.5;
                t.Tau = 1.0;
                t.SpeedFactor = 1.0;
                break;
        }
    }

    /// <summary>
    /// `.spmap` 的 <c>game_rules.vehicle_mix</c> → 三类车的份额（千分位）。
    /// ★ 与 <c>SimCore.Sim.VehicleMixRules.Shares</c> **同一口径**（那边是 internal，这里不引它，
    ///   免得 MapExport 依赖 SimCore 的内部实现；两边改动必须同步）。
    /// </summary>
    internal static (int Car, int Truck, int Bus) MixShares(string? mix)
    {
        return (mix ?? string.Empty).Trim() switch
        {
            "CityMix" => (850, 120, 30),
            "FreightHeavy" => (550, 450, 0),
            "BusHeavy" => (700, 100, 200),
            "CarBusMix" => (920, 0, 80),
            _ => (1000, 0, 0),
        };
    }

    /// <summary>
    /// 专业档的 <c>vehicle_types[]</c>；没配就按 <c>vehicle_mix</c> 预设展开成 1~3 条。
    /// </summary>
    internal static List<SumoVehicleType> Resolve(string? profileJson, string? vehicleMix)
    {
        List<TypeDto>? configured = ParseProfile(profileJson)?.VehicleTypes;

        if (configured is { Count: > 0 })
        {
            var list = new List<SumoVehicleType>();
            foreach (TypeDto dto in configured)
            {
                SumoVehicleType t = Create(dto.Preset ?? dto.Vclass ?? dto.Id ?? "car", dto.Style ?? "normal",
                    dto.Id ?? dto.Preset ?? "car", dto.Share ?? 0.0);

                // 显式写了的字段覆盖预设（"高级"里能改原始值）
                if (!string.IsNullOrWhiteSpace(dto.Vclass))
                {
                    t.VClass = dto.Vclass!;
                }

                if (dto.Length.HasValue) t.Length = dto.Length.Value;
                if (dto.MaxSpeed.HasValue) t.MaxSpeed = dto.MaxSpeed.Value;
                if (dto.Accel.HasValue) t.Accel = dto.Accel.Value;
                if (dto.Decel.HasValue) t.Decel = dto.Decel.Value;
                if (dto.Sigma.HasValue) t.Sigma = dto.Sigma.Value;
                if (dto.MinGap.HasValue) t.MinGap = dto.MinGap.Value;
                if (!string.IsNullOrWhiteSpace(dto.Color)) t.Color = dto.Color!;
                list.Add(t);
            }

            return list;
        }

        // --- 没配 → 按 vehicle_mix 预设展开 ---
        (int car, int truck, int bus) = MixShares(vehicleMix);
        var fallback = new List<SumoVehicleType>();
        if (car > 0) fallback.Add(Create("car", "normal", "car", car / 10.0));
        if (truck > 0) fallback.Add(Create("truck", "normal", "truck", truck / 10.0));
        if (bus > 0) fallback.Add(Create("bus", "normal", "bus", bus / 10.0));
        return fallback;
    }

    /// <summary>
    /// **某个进口**最终生效的车型构成。解析顺序必须与 GDScript 的
    /// <c>SumoFleetModel.effective_mix</c> 逐条一致，否则"编辑器里看到的"和"导出的"会不一样：
    /// <list type="number">
    ///   <item><c>lane_mix[lane]</c>（并过滤掉库里已不存在的 id）；</item>
    ///   <item>库里 <c>shared &gt; 0</c> 的车型（全局默认比例）；</item>
    ///   <item><c>game_rules.vehicle_mix</c> 预设展开。</item>
    /// </list>
    /// </summary>
    internal static List<(string Id, double Share)> MixForLane(string? profileJson, int laneId, string? vehicleMix)
    {
        ProfileDto? profile = ParseProfile(profileJson);

        if (profile?.LaneMix != null &&
            profile.LaneMix.TryGetValue(laneId.ToString(CultureInfo.InvariantCulture), out List<MixRowDto>? rows) &&
            rows is { Count: > 0 })
        {
            var valid = new HashSet<string>(StringComparer.Ordinal);
            if (profile.VehicleTypes != null)
            {
                foreach (TypeDto t in profile.VehicleTypes)
                {
                    if (!string.IsNullOrEmpty(t.Id))
                    {
                        valid.Add(t.Id!);
                    }
                }
            }

            var owned = new List<(string, double)>();
            foreach (MixRowDto row in rows)
            {
                if (string.IsNullOrEmpty(row.Id) || (valid.Count > 0 && !valid.Contains(row.Id!)))
                {
                    continue;
                }

                double share = row.Share ?? 0.0;
                if (share > 0.0)
                {
                    owned.Add((row.Id!, share));
                }
            }

            if (owned.Count > 0)
            {
                return owned;
            }
        }

        if (profile?.VehicleTypes is { Count: > 0 })
        {
            var fromLibrary = new List<(string, double)>();
            foreach (TypeDto t in profile.VehicleTypes)
            {
                double share = t.Share ?? 0.0;
                if (share > 0.0 && !string.IsNullOrEmpty(t.Id))
                {
                    fromLibrary.Add((t.Id!, share));
                }
            }

            if (fromLibrary.Count > 0)
            {
                return fromLibrary;
            }
        }

        (int car, int truck, int bus) = MixShares(vehicleMix);
        var fallback = new List<(string, double)>();
        if (car > 0) fallback.Add(("car", car / 10.0));
        if (truck > 0) fallback.Add(("truck", truck / 10.0));
        if (bus > 0) fallback.Add(("bus", bus / 10.0));
        return fallback;
    }

    /// <summary>`.sumo.json` 里 `lane_mix` 出现的车道 id（用于"配了车型但没有需求"的校核）。</summary>
    internal static HashSet<int> LanesWithOwnMix(string? profileJson)
    {
        var result = new HashSet<int>();
        Dictionary<string, List<MixRowDto>>? laneMix = ParseProfile(profileJson)?.LaneMix;
        if (laneMix == null)
        {
            return result;
        }

        foreach (string key in laneMix.Keys)
        {
            if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int lane))
            {
                result.Add(lane);
            }
        }

        return result;
    }

    /// <summary>解析专业档（只取车型相关两段；失败返回 null）。</summary>
    private static ProfileDto? ParseProfile(string? profileJson)
    {
        if (string.IsNullOrWhiteSpace(profileJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProfileDto>(profileJson, ReadOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    internal static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private sealed class ProfileDto
    {
        [JsonPropertyName("vehicle_types")]
        public List<TypeDto>? VehicleTypes { get; set; }

        /// <summary>按进口配的构成：键是**字符串化的车道 id**（JSON 对象的键只能是字符串）。</summary>
        [JsonPropertyName("lane_mix")]
        public Dictionary<string, List<MixRowDto>>? LaneMix { get; set; }
    }

    private sealed class MixRowDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("share")] public double? Share { get; set; }
    }

    private sealed class TypeDto
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("preset")] public string? Preset { get; set; }
        [JsonPropertyName("vclass")] public string? Vclass { get; set; }
        [JsonPropertyName("style")] public string? Style { get; set; }
        [JsonPropertyName("length")] public double? Length { get; set; }
        [JsonPropertyName("max_speed")] public double? MaxSpeed { get; set; }
        [JsonPropertyName("accel")] public double? Accel { get; set; }
        [JsonPropertyName("decel")] public double? Decel { get; set; }
        [JsonPropertyName("sigma")] public double? Sigma { get; set; }
        [JsonPropertyName("min_gap")] public double? MinGap { get; set; }
        [JsonPropertyName("color")] public string? Color { get; set; }
        [JsonPropertyName("share")] public double? Share { get; set; }
    }
}
