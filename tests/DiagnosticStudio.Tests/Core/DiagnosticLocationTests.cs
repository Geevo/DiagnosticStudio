using DiagnosticStudio.Core.Navigation;

namespace DiagnosticStudio.Tests.Core;

public class DiagnosticLocationTests
{
    private static readonly Guid Id = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Line_location_round_trips()
    {
        var text = DiagnosticLocation.ForLine(Id, 18442).ToString();

        Assert.Equal("artifact://11111111222233334444555555555555/line/18442", text);
        Assert.True(DiagnosticLocation.TryParse(text, out var parsed));
        Assert.Equal(DiagnosticLocationKind.Line, parsed!.Kind);
        Assert.Equal(18442, parsed.NumericPosition);
        Assert.Equal(Id, parsed.ArtifactId);
    }

    [Fact]
    public void Event_record_location_round_trips()
    {
        var text = DiagnosticLocation.ForEventRecord(Id, 4821).ToString();

        Assert.True(DiagnosticLocation.TryParse(text, out var parsed));
        Assert.Equal(DiagnosticLocationKind.EventRecord, parsed!.Kind);
        Assert.Equal(4821, parsed.NumericPosition);
    }

    [Fact]
    public void Registry_key_location_round_trips_without_a_member()
    {
        var original = DiagnosticLocation.ForRegistry(Id, @"HKEY_LOCAL_MACHINE\SOFTWARE\Vendor/Product");

        Assert.DoesNotContain("?value=", original.ToString());
        Assert.True(DiagnosticLocation.TryParse(original.ToString(), out var parsed));
        Assert.Equal(original, parsed);
        Assert.Null(parsed!.Member);
    }

    [Theory]
    [InlineData("Some Value")]
    [InlineData(@"Back\slash & ?=/ name")]
    [InlineData("")] // the default value is distinct from the key itself
    public void Registry_value_location_round_trips_the_value_name(string valueName)
    {
        var original = DiagnosticLocation.ForRegistry(Id, @"HKEY_CURRENT_USER\Key", valueName);

        Assert.True(DiagnosticLocation.TryParse(original.ToString(), out var parsed));
        Assert.Equal(original, parsed);
        Assert.Equal(@"HKEY_CURRENT_USER\Key", parsed!.Identifier);
        Assert.Equal(valueName, parsed.Member);
    }

    [Fact]
    public void Registry_location_with_an_unknown_query_is_rejected()
    {
        Assert.False(DiagnosticLocation.TryParse("artifact://11111111222233334444555555555555/registry/HKCU?other=1", out _));
    }

    [Fact]
    public void Artifact_only_location_round_trips()
    {
        Assert.True(DiagnosticLocation.TryParse(DiagnosticLocation.ForArtifact(Id).ToString(), out var parsed));
        Assert.Equal(DiagnosticLocationKind.Artifact, parsed!.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://example/line/3")]
    [InlineData("artifact://not-a-guid/line/3")]
    [InlineData("artifact://11111111222233334444555555555555/line/abc")]
    [InlineData("artifact://11111111222233334444555555555555/unknown/1")]
    public void Malformed_text_is_rejected(string? text)
    {
        Assert.False(DiagnosticLocation.TryParse(text, out var parsed));
        Assert.Null(parsed);
    }
}
