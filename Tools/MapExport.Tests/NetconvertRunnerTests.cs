using SignalPlanner.MapExport.Sumo;
using Xunit;

namespace SignalPlanner.MapExport.Tests;

public sealed class NetconvertRunnerTests
{
    [Fact]
    public void ResolveSumoHomeFromPortableLayout()
    {
        string root = TempDir.Create();
        try
        {
            string home = Path.Combine(root, "sumo");
            Directory.CreateDirectory(Path.Combine(home, "bin"));
            Directory.CreateDirectory(Path.Combine(home, "data", "typemap"));
            string exe = Path.Combine(home, "bin", "netconvert.exe");
            File.WriteAllText(exe, string.Empty);

            Assert.Equal(Path.GetFullPath(home), NetconvertRunner.ResolveSumoHome(exe));
        }
        finally
        {
            TempDir.Delete(root);
        }
    }

    [Fact]
    public void ResolveSumoHomeFromFlatLayout()
    {
        string root = TempDir.Create();
        try
        {
            string home = Path.Combine(root, "sumo-flat");
            Directory.CreateDirectory(Path.Combine(home, "data", "typemap"));
            string exe = Path.Combine(home, "netconvert.exe");
            File.WriteAllText(exe, string.Empty);

            Assert.Equal(Path.GetFullPath(home), NetconvertRunner.ResolveSumoHome(exe));
        }
        finally
        {
            TempDir.Delete(root);
        }
    }
}
