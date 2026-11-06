using System.Text;
using Xunit;

namespace SimCore.Tests;

/// <summary>
/// T0.1「工程骨架与工具链」验收测试。
/// <para>
/// 每一条测试都标注它所服务的验收标准或铁律，便于"手和眼"逐条对证：
/// <list type="bullet">
///   <item>A 组 → 验收标准 a) dotnet build 成功（无警告）</item>
///   <item>B 组 → 验收标准 b) dotnet test 输出 0 失败</item>
///   <item>C 组 → 验收标准 c) 两个 Godot .NET 工程可正常打开</item>
///   <item>铁律 1 / 铁律 2 → §1.5 硬约束的静态守卫</item>
/// </list>
/// </para>
/// </summary>
public sealed class ScaffoldTests
{
    // ------------------------------------------------------------------
    // 验收标准 a) dotnet build 成功（且无警告）
    // ------------------------------------------------------------------

    /// <summary>a1：SimCore 程序集确实被编译出来，测试能加载并引用它。</summary>
    [Fact]
    public void A1_SimCore_Assembly_Is_Loadable()
    {
        Assert.Equal("SimCore", SimCoreInfo.AssemblyName);
        Assert.Equal("SimCore", SimCoreAssemblyName());
    }

    /// <summary>a2：SimCore 的目标框架确实是 net8.0（本任务追加约束）。</summary>
    [Fact]
    public void A2_SimCore_Targets_Net8()
    {
        Assert.True(
            SimCoreInfo.IsNet8,
            $"SimCore 目标框架应为 {SimCoreInfo.ExpectedTargetFramework}，实际编译框架名为 '{SimCoreInfo.TargetFrameworkName}'。");
    }

    /// <summary>a3（铁律 1）：SimCore 不含任何 PackageReference / ProjectReference，也不引用 Godot SDK。</summary>
    [Fact]
    public void A3_SimCore_Has_No_ThirdParty_Or_Godot_Dependencies()
    {
        var csproj = StripXmlComments(File.ReadAllText(PathOf("SimCore", "SimCore.csproj")));

        Assert.False(
            csproj.Contains("<PackageReference", StringComparison.OrdinalIgnoreCase),
            "SimCore.csproj 不得包含任何 <PackageReference>（铁律 1：不得新增第三方依赖）。");
        Assert.False(
            csproj.Contains("<ProjectReference", StringComparison.OrdinalIgnoreCase),
            "SimCore.csproj 不得包含任何 <ProjectReference>（SimCore 必须是叶子类库）。");
        Assert.False(
            csproj.Contains("Sdk=\"Godot", StringComparison.OrdinalIgnoreCase),
            "SimCore.csproj 不得使用 Godot SDK（铁律 1：SimCore 内不得出现 Godot）。");
    }

    // ------------------------------------------------------------------
    // 验收标准 b) dotnet test 输出 0 失败
    // ------------------------------------------------------------------

    /// <summary>b1：测试夹具本身可运行（"空测试能跑"的最小证据）。</summary>
    [Fact]
    public void B1_Test_Harness_Runs()
    {
        Assert.Equal(4, 2 + 2);
    }

    /// <summary>b2：本任务交付物清单逐项存在（§4.2 ⑤ 对照）。</summary>
    [Fact]
    public void B2_T01_Deliverables_Exist()
    {
        Assert.True(File.Exists(PathOf("SimCore", "SimCore.csproj")), "缺少 SimCore/SimCore.csproj");
        Assert.True(Directory.Exists(PathOf("SimCore.Tests")), "缺少 SimCore.Tests/ 目录");
        // Game（游戏本体）不在本仓库 —— 本仓库只含路网编辑器。存在时仍然校验，便于把两份并回一份。
        if (Directory.Exists(PathOf("Game")))
        {
            Assert.True(File.Exists(PathOf("Game", "project.godot")), "缺少 Game/project.godot");
        }
        Assert.True(File.Exists(PathOf("Editor", "project.godot")), "缺少 Editor/project.godot");
        Assert.True(File.Exists(PathOf("build.sh")), "缺少 build.sh");
    }

    // ------------------------------------------------------------------
    // 验收标准 c) 两个 Godot .NET 工程可正常打开
    // ------------------------------------------------------------------

    /// <summary>c1（§1.3 目录规范）：§1.3 要求的目录全部就位。</summary>
    [Fact]
    public void C1_Section13_Directories_Exist()
    {
        // 只列本仓库（路网编辑器）自己的目录。Game / Tools/Calibrator 属于完整项目的其余部分，
        // 单独发布时不在这里，所以不做硬性要求。
        string[] required =
        [
            "SimCore", "SimCore/Model", "SimCore/Geometry", "SimCore/Sim",
            "SimCore/Metrics", "SimCore/Scoring", "SimCore/Io",
            "SimCore.Tests", "Editor", "Tools/MapExport", "docs",
        ];

        var missing = new List<string>();
        foreach (var relative in required)
        {
            if (!Directory.Exists(PathOf(relative.Split('/'))))
            {
                missing.Add(relative);
            }
        }

        Assert.True(missing.Count == 0, "缺少 §1.3 要求的目录：" + string.Join(", ", missing));
    }

    /// <summary>c2：Game 是带 C# 的 Godot 4.7 工程，且主场景文件真实存在。</summary>
    [Fact]
    public void C2_Game_Godot_Project_Is_DotNet_And_Runnable()
    {
        if (!Directory.Exists(PathOf("Game")))
        {
            return;   // 本仓库不含 Game（游戏本体），跳过
        }

        AssertGodotDotNetProject("Game", "Game");
    }

    /// <summary>c3：Editor 是带 C# 的 Godot 4.7 工程，且主场景文件真实存在。</summary>
    [Fact]
    public void C3_Editor_Godot_Project_Is_DotNet_And_Runnable()
    {
        AssertGodotDotNetProject("Editor", "Editor");
    }

    /// <summary>c4：两个 Godot 工程都用 Godot.NET.Sdk/4.7.2 且引用了 SimCore（同源，§1.3）。</summary>
    [Theory]
    [InlineData("Game")]
    [InlineData("Editor")]
    public void C4_Godot_Projects_Use_DotNetSdk_And_Reference_SimCore(string project)
    {
        if (!Directory.Exists(PathOf(project)))
        {
            return;   // 该 Godot 工程不在本仓库
        }

        var csproj = File.ReadAllText(PathOf(project, project + ".csproj"));

        Assert.True(
            csproj.Contains("Godot.NET.Sdk/4.7.2", StringComparison.Ordinal),
            $"{project}.csproj 应使用 Godot.NET.Sdk/4.7.2（与已安装的 Godot 4.7.2 mono 对应）。");
        Assert.True(
            csproj.Contains("<TargetFramework>net8.0</TargetFramework>", StringComparison.Ordinal),
            $"{project}.csproj 应以 net8.0 为目标框架。");
        Assert.True(
            csproj.Contains("SimCore/SimCore.csproj", StringComparison.Ordinal),
            $"{project}.csproj 应引用 SimCore（游戏与编辑器共享同一份仿真核）。");
    }

    // ------------------------------------------------------------------
    // 铁律守卫（§1.5 硬约束）
    // ------------------------------------------------------------------

    /// <summary>铁律 1：SimCore 源码内不得出现 Godot 痕迹（已剔除注释，避免注释误报）。</summary>
    [Fact]
    public void Guard_SimCore_Source_Has_No_Godot()
    {
        string[] forbidden = ["using Godot", "GodotSharp", "Godot."];
        var violations = ScanSimCoreSources(forbidden);

        Assert.True(violations.Count == 0, "SimCore 源码中出现 Godot 痕迹（铁律 1）：" + string.Join("; ", violations));
    }

    /// <summary>铁律 2：SimCore 内禁止使用系统随机数 / 非确定性时间源。</summary>
    [Fact]
    public void Guard_SimCore_Source_Has_No_System_Randomness()
    {
        string[] forbidden =
        [
            "new Random", "Random.Shared", "System.Random",
            "Guid.NewGuid", "DateTime.Now", "DateTime.UtcNow", "DateTimeOffset.Now",
            "Environment.TickCount",
        ];
        var violations = ScanSimCoreSources(forbidden);

        Assert.True(violations.Count == 0, "SimCore 源码中出现非确定性调用（铁律 2，需要时须注入种子）：" + string.Join("; ", violations));
    }

    // ------------------------------------------------------------------
    // 工具
    // ------------------------------------------------------------------

    private static string SimCoreAssemblyName()
        => typeof(SimCoreInfo).Assembly.GetName().Name ?? string.Empty;

    private static void AssertGodotDotNetProject(string folder, string assemblyName)
    {
        var text = File.ReadAllText(PathOf(folder, "project.godot"));

        Assert.True(text.Contains("config_version=5", StringComparison.Ordinal), $"{folder}/project.godot 缺少 config_version=5。");
        Assert.True(text.Contains("\"4.7\"", StringComparison.Ordinal), $"{folder}/project.godot 的 config/features 未声明引擎版本 4.7。");
        Assert.True(text.Contains("\"C#\"", StringComparison.Ordinal), $"{folder}/project.godot 的 config/features 未声明 C#（.NET 工程必需）。");
        Assert.True(text.Contains("[dotnet]", StringComparison.Ordinal), $"{folder}/project.godot 缺少 [dotnet] 段。");
        Assert.True(
            text.Contains($"project/assembly_name=\"{assemblyName}\"", StringComparison.Ordinal),
            $"{folder}/project.godot 的 project/assembly_name 应为 \"{assemblyName}\"。");

        var mainScene = ReadMainScene(text);
        Assert.False(string.IsNullOrEmpty(mainScene), $"{folder}/project.godot 未设置 run/main_scene。");

        var scenePath = Path.Combine(
            PathOf(folder),
            mainScene.Replace("res://", string.Empty, StringComparison.Ordinal)
                .Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(scenePath), $"{folder}/project.godot 指向的主场景 {mainScene} 不存在。");
    }

    private static string ReadMainScene(string projectGodot)
    {
        const string key = "run/main_scene=";
        foreach (var raw in projectGodot.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(key, StringComparison.Ordinal))
            {
                return line[key.Length..].Trim().Trim('"');
            }
        }

        return string.Empty;
    }

    private static List<string> ScanSimCoreSources(IEnumerable<string> forbiddenTokens)
    {
        var violations = new List<string>();
        var root = PathOf("SimCore");

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (relative.StartsWith("bin", StringComparison.OrdinalIgnoreCase) ||
                relative.StartsWith("obj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var code = StripComments(File.ReadAllText(file));
            foreach (var token in forbiddenTokens)
            {
                if (code.Contains(token, StringComparison.Ordinal))
                {
                    violations.Add($"{relative} -> '{token}'");
                }
            }
        }

        return violations;
    }

    /// <summary>剔除行注释与块注释，避免"注释里提到禁用词"造成误报。简单状态机，不依赖正则。</summary>
    private static string StripComments(string source)
    {
        var sb = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    i++;
                }

                i = Math.Min(i + 2, source.Length);
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>剔除 XML 注释，避免"注释里提到禁用标记"造成误报。</summary>
    private static string StripXmlComments(string text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (i + 3 < text.Length && text[i] == '<' && text[i + 1] == '!' && text[i + 2] == '-' && text[i + 3] == '-')
            {
                i += 4;
                while (i + 2 < text.Length && !(text[i] == '-' && text[i + 1] == '-' && text[i + 2] == '>'))
                {
                    i++;
                }

                i = Math.Min(i + 3, text.Length);
                continue;
            }

            sb.Append(text[i]);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>定位仓库根目录（含 SignalPlanner.sln 的目录），使测试与运行目录无关。</summary>
    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SignalPlanner.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("未能在测试输出目录之上找到 SignalPlanner.sln，无法定位仓库根目录。");
    }

    private static string PathOf(params string[] parts)
    {
        var all = new string[parts.Length + 1];
        all[0] = RepoRoot();
        Array.Copy(parts, 0, all, 1, parts.Length);
        return Path.Combine(all);
    }
}
