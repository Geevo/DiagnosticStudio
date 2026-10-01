using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Parsers;
using DiagnosticStudio.Search;

namespace DiagnosticStudio.Tests.Search;

public class RegistrySearchTests
{
    private sealed class NoSource : ITextLineSource
    {
        public int LineCount => 0;
        public long ByteLength => 0;
        public string EncodingName => "test";
        public IReadOnlyList<string> ReadLines(int startLine, int count) => Array.Empty<string>();
        public IEnumerable<string> EnumerateLines(int startLine = 0) => Array.Empty<string>();
    }

    private static RegistryDocument Doc(params string[] lines) => RegFileParser.Parse(
        new DiagnosticArtifact
        {
            Id = Guid.NewGuid(),
            Name = "t.reg",
            OriginalPath = "t.reg",
            ArtifactType = ArtifactType.RegistryExport,
        },
        lines,
        new NoSource());

    private static readonly string[] Sample =
    {
        "Windows Registry Editor Version 5.00",
        @"[HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate]",
        "\"WUServer\"=\"http://wsus.contoso.test:8530\"",
        "\"UseWUServer\"=dword:00000001",
        @"[HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU]",
        "@=\"default au\"",
        "\"NoAutoUpdate\"=dword:00000000",
    };

    [Fact]
    public void Matches_value_data_value_names_and_key_names_in_file_order()
    {
        var result = RegistrySearch.Find(Doc(Sample), "wsus", false, CancellationToken.None);

        var data = Assert.Single(result.Matches);
        Assert.Equal(RegistryMatchField.ValueData, data.Field);
        Assert.Equal("WUServer", data.Value!.Name);

        var names = RegistrySearch.Find(Doc(Sample), "UseWU", false, CancellationToken.None);
        Assert.Equal(RegistryMatchField.ValueName, Assert.Single(names.Matches).Field);

        var keys = RegistrySearch.Find(Doc(Sample), "WindowsUpdate", false, CancellationToken.None);
        Assert.Equal(new[] { "WindowsUpdate" }, keys.Matches.Select(m => m.Key.Name)); // not the AU child
        Assert.Equal(RegistryMatchField.KeyPath, keys.Matches[0].Field);
        Assert.Null(keys.Matches[0].Value);
    }

    [Fact]
    public void Query_with_a_backslash_matches_against_the_full_key_path()
    {
        var result = RegistrySearch.Find(Doc(Sample), @"WindowsUpdate\AU", false, CancellationToken.None);

        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", Assert.Single(result.Matches).Key.FullPath);
    }

    [Fact]
    public void Results_follow_file_order()
    {
        var result = RegistrySearch.Find(Doc(Sample), "u", false, CancellationToken.None);

        var lines = result.Matches.Select(m => m.Value?.SourceLine ?? m.Key.SourceLine).ToArray();
        Assert.Equal(lines.OrderBy(l => l), lines);
        Assert.True(result.Matches.Count >= 3);
    }

    [Fact]
    public void Case_sensitivity_is_respected()
    {
        Assert.NotEmpty(RegistrySearch.Find(Doc(Sample), "wuserver", false, CancellationToken.None).Matches);
        Assert.Empty(RegistrySearch.Find(Doc(Sample), "wuserver", true, CancellationToken.None).Matches);
    }

    [Fact]
    public void Default_value_is_found_by_its_displayed_name_and_data()
    {
        Assert.Contains(RegistrySearch.Find(Doc(Sample), "(default)", false, CancellationToken.None).Matches, m => m.Value?.IsDefault == true);
        Assert.Contains(RegistrySearch.Find(Doc(Sample), "default au", false, CancellationToken.None).Matches, m => m.Value?.IsDefault == true);
    }

    [Fact]
    public void A_value_matching_both_name_and_data_is_reported_once()
    {
        var doc = Doc("Windows Registry Editor Version 5.00", @"[HKEY_CURRENT_USER\K]", "\"abc\"=\"abc\"");

        var matches = RegistrySearch.Find(doc, "abc", false, CancellationToken.None).Matches;

        Assert.Single(matches);
        Assert.Equal(RegistryMatchField.ValueName, matches[0].Field);
    }

    [Fact]
    public void Deleted_values_do_not_match_on_their_placeholder_text()
    {
        var doc = Doc("Windows Registry Editor Version 5.00", @"[HKEY_CURRENT_USER\K]", "\"X\"=-");

        Assert.Empty(RegistrySearch.Find(doc, "deleted", false, CancellationToken.None).Matches);
    }

    [Fact]
    public void Empty_query_and_cap_and_cancellation()
    {
        Assert.Empty(RegistrySearch.Find(Doc(Sample), "", false, CancellationToken.None).Matches);

        var capped = RegistrySearch.Find(Doc(Sample), "e", false, CancellationToken.None, maxMatches: 2);
        Assert.Equal(2, capped.Matches.Count);
        Assert.True(capped.Truncated);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => RegistrySearch.Find(Doc(Sample), "zzz", false, cts.Token));
    }
}
