using Tau;

namespace Tau.Tests
{
    [Collection("env")]
    public class ToolExecutorTests : IDisposable
    {
        readonly TauRunner runner = new();

        public void Dispose() => runner.Dispose();

        static Dictionary<string, object> Shell(string command)
        {
            var cfg = new Config("") { ToolTimeoutSec = 5 };
            string result = ToolExecutor.Execute("shell",
                Json.ToString(new Dictionary<string, object> { ["cmd"] = command }), cfg);
            var parsed = Assert.IsType<Dictionary<string, object>>(Json.Parse(result));
            Assert.False(parsed.ContainsKey("error"), result);
            return parsed;
        }

        [Theory]
        [InlineData("echo HELLO WITH SPACES", "HELLO WITH SPACES")]
        [InlineData("echo FIRST && echo SECOND", "FIRST\nSECOND")]
        public void ShellReceivesTheEntireCommand(string command, string expected)
        {
            var result = Shell(command);
            Assert.Equal(0, Json.AsInt(result["exitCode"]));
            string actual = Json.AsString(result["stdout"]).Replace("\r\n", "\n");
            Assert.Equal(expected, string.Join("\n", actual.Trim().Split('\n').Select(s => s.TrimEnd())));
        }

        [Fact]
        public void ShellPreservesQuotedFilePaths()
        {
            string path = Path.Combine(runner.WorkDir, "file with spaces.txt");
            File.WriteAllText(path, "QUOTED_PATH_OK");
            string command = (OperatingSystem.IsWindows() ? "type" : "cat") + " \"" + path + "\"";
            var result = Shell(command);
            Assert.Equal(0, Json.AsInt(result["exitCode"]));
            Assert.Equal("QUOTED_PATH_OK", Json.AsString(result["stdout"]));
        }

        [Fact]
        public void ShellReturnsStderrAndNonZeroExitCode()
        {
            var result = Shell("echo ERROR_MARKER >&2 & exit 7".Replace(" & ",
                OperatingSystem.IsWindows() ? " & " : "; "));
            Assert.Equal(7, Json.AsInt(result["exitCode"]));
            Assert.Equal("ERROR_MARKER", Json.AsString(result["stderr"]).Trim());
            Assert.Equal("", Json.AsString(result["stdout"]));
        }
    }
}
