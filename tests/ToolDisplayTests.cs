using Tau;

namespace Tau.Tests
{
    public class ToolDisplayTests
    {
        [Theory]
        [InlineData("shell", "{\"cmd\":\"echo hello\"}", "echo hello")]
        [InlineData("read_file", "{\"path\":\" a.txt \"}", "a.txt")]
        [InlineData("write_file", "{\"path\":\"a.txt\",\"content\":\"private body\"}", "a.txt")]
        [InlineData("list_dir", "{\"path\":\"src\",\"pattern\":\"*.cs\"}", "src  [*.cs]")]
        [InlineData("curl", "{\"url\":\"https://example.com\",\"method\":\"POST\",\"headers\":{\"Authorization\":\"secret\"}}", "POST https://example.com")]
        [InlineData("curl", "{\"url\":\"https://example.com\"}", "https://example.com")]
        [InlineData("memory_write", "{\"text\":\"private note\"}", "")]
        [InlineData("shell", "{", "")]
        [InlineData("shell", "[]", "")]
        [InlineData("shell", "null", "")]
        [InlineData("shell", null, "")]
        [InlineData("shell", "{\"cmd\":null}", "")]
        public void FormatsDetailsWithoutIncludingBodies(string name, string args, string expected)
        {
            Assert.Equal(expected, ToolDisplay.Detail(name, args));
        }

        [Theory]
        [InlineData("shell", "echo hello", "Tool: shell  echo hello")]
        [InlineData("memory_read", "", "Tool: memory_read")]
        [InlineData(null, null, "Tool: ?")]
        public void FormatsStoredSteps(string name, string detail, string expected)
        {
            Assert.Equal(expected, ToolDisplay.Step(name, detail));
        }
    }
}
