using DiagnosticStudio.Ingestion;

namespace DiagnosticStudio.Tests.Ingestion;

public class SafeEntryPathTests
{
    private static readonly string Destination = Path.Combine(Path.GetTempPath(), "ds-safe-path-tests", "dest");

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData(@"..\evil.txt")]
    [InlineData(@"a\..\..\evil.txt")]
    [InlineData("/etc/passwd")]
    [InlineData(@"\Windows\System32\x.dll")]
    [InlineData(@"C:\Windows\x.dll")]
    [InlineData("c:evil.txt")]
    [InlineData(".. /evil.txt")]
    [InlineData("")]
    [InlineData("   ")]
    public void Unsafe_names_are_rejected(string name)
    {
        Assert.False(SafeEntryPath.TryResolve(Destination, name, out _, out var reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Theory]
    [InlineData("logs/agent.log", "logs", "agent.log")]
    [InlineData(@"logs\agent.log", "logs", "agent.log")]
    [InlineData("./logs//agent.log", "logs", "agent.log")]
    public void Safe_names_resolve_inside_destination(string name, string folder, string file)
    {
        Assert.True(SafeEntryPath.TryResolve(Destination, name, out var path, out _));
        Assert.Equal(Path.Combine(Path.GetFullPath(Destination), folder, file), path);
    }

    [Fact]
    public void Invalid_characters_are_replaced_and_stay_inside_destination()
    {
        Assert.True(SafeEntryPath.TryResolve(Destination, "dir/file:stream.txt", out var path, out _));

        Assert.StartsWith(Path.GetFullPath(Destination), path);
        Assert.DoesNotContain(':', path[2..]);
    }

    [Fact]
    public void Reserved_device_names_are_prefixed()
    {
        Assert.True(SafeEntryPath.TryResolve(Destination, "NUL.txt", out var path, out _));

        Assert.Equal("_NUL.txt", Path.GetFileName(path));
    }

    [Fact]
    public void Split_keeps_original_segments_for_provenance()
    {
        Assert.True(SafeEntryPath.TrySplit(@"Logs\agent.log", out var segments, out _));

        Assert.Equal(new[] { "Logs", "agent.log" }, segments);
    }
}
