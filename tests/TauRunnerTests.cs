using System;
using System.IO;
using Tau;
using Xunit;

namespace Tau.Tests
{
    // テストから tau を起動するための DLL の選び方を検証する。
    [Collection("env")]
    public class TauRunnerTests
    {
        [Fact]
        public void TauDllIsFrameworkDependent()
        {
            string dll = TauRunner.TauDll();
            Assert.True(File.Exists(dll), "tau.dll should exist: " + dll);

            // テスト出力先の tau.dll は自己完結ビルドなので使ってはいけない。
            // `dotnet tau.dll` では hostpolicy.dll が見つからず起動できない。
            Assert.NotEqual(
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "tau.dll")),
                Path.GetFullPath(dll));

            string runtimeConfig = Path.ChangeExtension(dll, ".runtimeconfig.json");
            Assert.True(File.Exists(runtimeConfig), runtimeConfig + " should exist");

            string json = File.ReadAllText(runtimeConfig);
            Dictionary<string, object> d = Json.Parse(json) as Dictionary<string, object>;
            Assert.True(d.ContainsKey("runtimeOptions"), "runtimeconfig should have runtimeOptions");
            Dictionary<string, object> opts = d["runtimeOptions"] as Dictionary<string, object>;
            Assert.True(opts != null && opts.ContainsKey("framework"),
                "a framework-dependent build must name its framework, otherwise "
                + "dotnet refuses to run it as a self-contained app");
        }
    }
}
