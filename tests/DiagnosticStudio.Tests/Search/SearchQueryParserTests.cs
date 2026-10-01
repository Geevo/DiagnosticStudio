using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Search;

namespace DiagnosticStudio.Tests.Search;

public class SearchQueryParserTests
{
    private static SearchQuery Ok(string input, bool matchCase = false)
    {
        var result = SearchQueryParser.Parse(input, matchCase);
        Assert.Empty(result.Errors);
        return result.Query;
    }

    [Theory]
    [InlineData("0x80072F8F", "0x80072F8F")]
    [InlineData("print spooler", "print spooler")]
    [InlineData("  lots   of   space  ", "lots of space")]
    [InlineData("\"quoted phrase\"", "quoted phrase")]
    [InlineData(@"C:\Windows\System32", @"C:\Windows\System32")]
    [InlineData("http://host:8530/path", "http://host:8530/path")]
    [InlineData("unknown:thing here", "unknown:thing here")]
    [InlineData("trailing:", "trailing:")]
    [InlineData(":leading", ":leading")]
    public void Plain_text_is_kept_as_text(string input, string expected)
    {
        var query = Ok(input);

        Assert.Equal(expected, query.Text);
        Assert.False(query.HasEventOperators);
        Assert.Null(query.Types);
        Assert.False(query.IsEmpty);
    }

    [Fact]
    public void Empty_input_is_an_empty_query()
    {
        Assert.True(Ok("").IsEmpty);
        Assert.True(Ok("   ").IsEmpty);
        Assert.True(SearchQueryParser.Parse(null).Query.IsEmpty);
    }

    [Fact]
    public void Match_case_is_carried_through()
    {
        Assert.True(Ok("x", matchCase: true).MatchCase);
        Assert.False(Ok("x").MatchCase);
    }

    [Fact]
    public void Event_id_operator_accepts_lists_and_ranges_alongside_text()
    {
        var query = Ok("eventid:7031,100-200 crashed");

        Assert.Equal("crashed", query.Text);
        Assert.True(query.EventIds!.Contains(7031));
        Assert.True(query.EventIds.Contains(150));
        Assert.False(query.EventIds.Contains(7036));
        Assert.True(query.HasEventOperators);
    }

    [Fact]
    public void Provider_operator_takes_the_rest_of_a_quoted_value()
    {
        var query = Ok("provider:\"Service Control\" failed");

        Assert.Equal("Service Control", query.ProviderContains);
        Assert.Equal("failed", query.Text);
    }

    [Theory]
    [InlineData("level:error", new byte[] { 2 })]
    [InlineData("level:ERROR,warning", new byte[] { 2, 3 })]
    [InlineData("level:info", new byte[] { 4 })]
    [InlineData("level:critical,verbose", new byte[] { 1, 5 })]
    [InlineData("level:3", new byte[] { 3 })]
    public void Level_operator_maps_names_and_numbers(string input, byte[] expected)
    {
        var query = Ok(input);

        Assert.Equal(expected.OrderBy(b => b), query.Levels!.OrderBy(b => b));
        Assert.Equal(string.Empty, query.Text);
        Assert.False(query.IsEmpty);
    }

    [Theory]
    [InlineData("type:registry", ArtifactType.RegistryExport)]
    [InlineData("type:reg", ArtifactType.RegistryExport)]
    [InlineData("type:evtx", ArtifactType.EventLog)]
    [InlineData("type:log", ArtifactType.TextLog)]
    [InlineData("type:cmd", ArtifactType.CommandOutput)]
    [InlineData("type:ETL", ArtifactType.Trace)]
    public void Type_operator_maps_names(string input, ArtifactType expected)
    {
        Assert.Equal(new[] { expected }, Ok(input).Types);
    }

    [Fact]
    public void Several_types_can_be_listed()
    {
        Assert.Equal(
            new HashSet<ArtifactType> { ArtifactType.RegistryExport, ArtifactType.EventLog },
            Ok("type:reg,evtx foo").Types!.ToHashSet());
    }

    [Fact]
    public void Quoted_text_that_looks_like_an_operator_is_text()
    {
        var query = Ok("\"eventid:5\"");

        Assert.Equal("eventid:5", query.Text);
        Assert.Null(query.EventIds);
    }

    [Theory]
    [InlineData("eventid:abc")]
    [InlineData("eventid:20-10")]
    [InlineData("level:loud")]
    [InlineData("level:9")]
    [InlineData("type:spreadsheet")]
    public void Bad_operator_values_are_reported(string input)
    {
        var result = SearchQueryParser.Parse(input);

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Operators_are_case_insensitive_and_combine()
    {
        var query = Ok("EventID:7031 PROVIDER:schannel Level:Error type:EVTX tls failure");

        Assert.Equal("tls failure", query.Text);
        Assert.Equal("schannel", query.ProviderContains);
        Assert.Contains((byte)2, query.Levels!);
        Assert.Contains(ArtifactType.EventLog, query.Types!);
        Assert.True(query.EventIds!.Contains(7031));
    }
}
