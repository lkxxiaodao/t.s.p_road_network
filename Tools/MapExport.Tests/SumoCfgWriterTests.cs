using System.Xml.Linq;
using SignalPlanner.MapExport.Sumo;
using Xunit;

namespace SignalPlanner.MapExport.Tests;

/// <summary>
/// <c>.sumocfg</c> 生成（S5）。核心承诺：
/// ① 全部相对路径（整夹可搬移）；② 只挂**真实存在**的附加文件；③ 旧 `.tll.xml` 不许拖累试跑。
/// </summary>
public sealed class SumoCfgWriterTests
{
    [Fact]
    public void WritesRelativePathsAndDefaults()
    {
        string dir = TempDir.Create();
        try
        {
            string b = Path.Combine(dir, "01-1");
            File.WriteAllText(b + ".net.xml", "<net/>");
            File.WriteAllText(b + ".rou.xml", "<routes/>");

            SumoCfgWriter.Result r = SumoCfgWriter.Write(b, null);
            Assert.True(r.Ok, r.Error);

            XElement root = XDocument.Load(b + ".sumocfg").Root!;
            Assert.Equal("01-1.net.xml", (string?)root.Element("input")!.Element("net-file")!.Attribute("value"));
            Assert.Equal("01-1.rou.xml", (string?)root.Element("input")!.Element("route-files")!.Attribute("value"));
            // 没有附加文件时整段不写
            Assert.Null(root.Element("input")!.Element("additional-files"));
            // 缺省值（与 SumoProfile.defaults 一致）
            Assert.Equal("0", (string?)root.Element("time")!.Element("begin")!.Attribute("value"));
            Assert.Equal("3600", (string?)root.Element("time")!.Element("end")!.Attribute("value"));
            Assert.Equal("1", (string?)root.Element("time")!.Element("step-length")!.Attribute("value"));
            Assert.Equal("42", (string?)root.Element("random_number")!.Element("seed")!.Attribute("value"));
            Assert.Equal("300", (string?)root.Element("processing")!.Element("time-to-teleport")!.Attribute("value"));
            // 统计要打开，否则试跑没有数字可看
            Assert.Equal("true",
                (string?)root.Element("report")!.Element("duration-log.statistics")!.Attribute("value"));
            // 只有文件名，不含任何绝对路径
            Assert.DoesNotContain(dir, File.ReadAllText(b + ".sumocfg"));
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void AppliesProfileConfig()
    {
        string dir = TempDir.Create();
        try
        {
            string b = Path.Combine(dir, "lv");
            File.WriteAllText(b + ".net.xml", "<net/>");
            File.WriteAllText(b + ".rou.xml", "<routes/>");

            const string profile = """
                {"config":{"begin":60,"end":900,"step_length":0.5,"seed":7,"teleport":120}}
                """;
            Assert.True(SumoCfgWriter.Write(b, profile).Ok);

            XElement root = XDocument.Load(b + ".sumocfg").Root!;
            Assert.Equal("60", (string?)root.Element("time")!.Element("begin")!.Attribute("value"));
            Assert.Equal("900", (string?)root.Element("time")!.Element("end")!.Attribute("value"));
            Assert.Equal("0.5", (string?)root.Element("time")!.Element("step-length")!.Attribute("value"));
            Assert.Equal("7", (string?)root.Element("random_number")!.Element("seed")!.Attribute("value"));
            Assert.Equal("120", (string?)root.Element("processing")!.Element("time-to-teleport")!.Attribute("value"));
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void InvalidConfigFallsBackToDefaults()
    {
        string dir = TempDir.Create();
        try
        {
            string b = Path.Combine(dir, "lv");
            File.WriteAllText(b + ".net.xml", "<net/>");
            File.WriteAllText(b + ".rou.xml", "<routes/>");

            // end 落在 begin 之前、步长 0、JSON 坏掉 —— 全部回落成**合法**的值，不许写出非法 cfg。
            // 口径：end 只判 <=0 回默认；begin 夹到 [0, end)（用户的 end 是有意设的，别替他丢）。
            Assert.True(SumoCfgWriter.Write(b, """{"config":{"begin":500,"end":100,"step_length":0}}""").Ok);
            XElement root = XDocument.Load(b + ".sumocfg").Root!;
            Assert.Equal("0", (string?)root.Element("time")!.Element("begin")!.Attribute("value"));
            Assert.Equal("100", (string?)root.Element("time")!.Element("end")!.Attribute("value"));
            Assert.Equal("1", (string?)root.Element("time")!.Element("step-length")!.Attribute("value"));

            // end <= 0 才回默认 3600
            Assert.True(SumoCfgWriter.Write(b, """{"config":{"begin":0,"end":0}}""").Ok);
            root = XDocument.Load(b + ".sumocfg").Root!;
            Assert.Equal("3600", (string?)root.Element("time")!.Element("end")!.Attribute("value"));

            Assert.True(SumoCfgWriter.Write(b, "{ not json").Ok);
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void AcceptsSeedWrittenAsFloat()
    {
        // ★ S5 踩过的坑：编辑器那边 seed 来自 SpinBox（float），JSON 里是 `7.0`。
        //   DTO 用 `int?` 时 System.Text.Json 会抛 JsonException（严格模式），
        //   整个 config 被丢弃 → sumocfg 悄悄回到默认值 3600/42。
        string dir = TempDir.Create();
        try
        {
            string b = Path.Combine(dir, "lv");
            File.WriteAllText(b + ".net.xml", "<net/>");
            File.WriteAllText(b + ".rou.xml", "<routes/>");

            Assert.True(SumoCfgWriter.Write(b, """{"config":{"end":240.0,"seed":7.0}}""").Ok);
            XElement root = XDocument.Load(b + ".sumocfg").Root!;
            Assert.Equal("240", (string?)root.Element("time")!.Element("end")!.Attribute("value"));
            Assert.Equal("7", (string?)root.Element("random_number")!.Element("seed")!.Attribute("value"));

            // 整数写法当然也要认
            Assert.True(SumoCfgWriter.Write(b, """{"config":{"seed":11}}""").Ok);
            root = XDocument.Load(b + ".sumocfg").Root!;
            Assert.Equal("11", (string?)root.Element("random_number")!.Element("seed")!.Attribute("value"));
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void AttachesExistingAdditionalFiles()
    {
        string dir = TempDir.Create();
        try
        {
            string b = Path.Combine(dir, "01-1");
            File.WriteAllText(b + ".net.xml", TlsNet("0"));
            File.WriteAllText(b + ".rou.xml", "<routes/>");
            File.WriteAllText(b + ".tll.xml", TlsFile("0"));
            File.WriteAllText(b + ".add.xml", "<additional/>");

            SumoCfgWriter.Result r = SumoCfgWriter.Write(b, null);
            Assert.True(r.Ok, r.Error);

            XElement input = XDocument.Load(b + ".sumocfg").Root!.Element("input")!;
            Assert.Equal("01-1.tll.xml,01-1.add.xml",
                (string?)input.Element("additional-files")!.Attribute("value"));
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void StaleTllIsSkippedWithWarning()
    {
        string dir = TempDir.Create();
        try
        {
            string b = Path.Combine(dir, "01-1");
            File.WriteAllText(b + ".net.xml", TlsNet("0"));
            File.WriteAllText(b + ".rou.xml", "<routes/>");
            // 旧文件：灯 99 在新网络里根本不存在 → SUMO 会报
            // "No initial signal plan loaded for tls '99'" 然后不跑
            File.WriteAllText(b + ".tll.xml", TlsFile("99"));

            SumoCfgWriter.Result r = SumoCfgWriter.Write(b, null);
            Assert.True(r.Ok, r.Error);
            Assert.Contains(r.Warnings, w => w.Contains("跳过") && w.Contains("99"));

            XElement input = XDocument.Load(b + ".sumocfg").Root!.Element("input")!;
            Assert.Null(input.Element("additional-files"));   // 一个都不挂
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void MissingRouFails()
    {
        string dir = TempDir.Create();
        try
        {
            string b = Path.Combine(dir, "01-1");
            File.WriteAllText(b + ".net.xml", "<net/>");

            SumoCfgWriter.Result r = SumoCfgWriter.Write(b, null);
            Assert.False(r.Ok);
            Assert.Contains("rou.xml", r.Error);
            Assert.False(File.Exists(b + ".sumocfg"));
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    [Fact]
    public void MissingNetFails()
    {
        string dir = TempDir.Create();
        try
        {
            string b = Path.Combine(dir, "01-1");
            SumoCfgWriter.Result r = SumoCfgWriter.Write(b, null);
            Assert.False(r.Ok);
            Assert.Contains("net.xml", r.Error);
        }
        finally
        {
            TempDir.Delete(dir);
        }
    }

    private static string TlsNet(string tlId) =>
        $"<net><tlLogic id=\"{tlId}\" type=\"static\" programID=\"0\"/></net>";

    private static string TlsFile(string tlId) =>
        $"<additional><tlLogic id=\"{tlId}\" type=\"static\" programID=\"pro0\"/></additional>";
}
