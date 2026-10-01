using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Parsers;

namespace DiagnosticStudio.Tests.Parsers;

public class DocumentLoaderTests
{
    private static readonly DiagnosticArtifact Artifact = new()
    {
        Id = Guid.NewGuid(),
        Name = "x.log",
        OriginalPath = "x.log",
        Provenance = new[] { "Bundle.zip", "x.log" },
        ArtifactType = ArtifactType.TextLog,
    };

    [Fact]
    public async Task First_matching_parser_wins()
    {
        var loader = new DocumentLoader(new IDiagnosticParser[]
        {
            new StubParser(canHandle: false, "first"),
            new StubParser(canHandle: true, "second"),
            new StubParser(canHandle: true, "third"),
        });

        var result = await loader.LoadAsync(Artifact, CancellationToken.None);

        Assert.Equal("second", Assert.IsType<UnsupportedDocument>(result.Document).Reason);
        Assert.Null(result.FailureMessage);
    }

    [Fact]
    public async Task Parser_exception_becomes_an_unsupported_document_not_a_crash()
    {
        var loader = new DocumentLoader(new IDiagnosticParser[] { new ThrowingParser() });

        var result = await loader.LoadAsync(Artifact, CancellationToken.None);

        var document = Assert.IsType<UnsupportedDocument>(result.Document);
        Assert.Contains("boom", document.Reason);
        Assert.Contains("ThrowingParser", result.FailureMessage);
        Assert.Contains("Bundle.zip", result.FailureMessage);
    }

    [Fact]
    public async Task A_failing_parser_falls_through_to_the_next_capable_one_and_still_reports_the_failure()
    {
        var loader = new DocumentLoader(new IDiagnosticParser[]
        {
            new ThrowingParser(),
            new StubParser(canHandle: false, "skipped"),
            new StubParser(canHandle: true, "raw fallback"),
        });

        var result = await loader.LoadAsync(Artifact, CancellationToken.None);

        Assert.Equal("raw fallback", Assert.IsType<UnsupportedDocument>(result.Document).Reason);
        Assert.Contains("ThrowingParser", result.FailureMessage);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var loader = new DocumentLoader(new IDiagnosticParser[] { new CancellingParser() });

        await Assert.ThrowsAsync<OperationCanceledException>(() => loader.LoadAsync(Artifact, CancellationToken.None));
    }

    [Fact]
    public async Task No_matching_parser_still_yields_a_document()
    {
        var result = await new DocumentLoader(Array.Empty<IDiagnosticParser>()).LoadAsync(Artifact, CancellationToken.None);

        Assert.IsType<UnsupportedDocument>(result.Document);
    }

    [Fact]
    public async Task Fallback_parser_explains_why_etl_is_unsupported()
    {
        var etl = Artifact with { ArtifactType = ArtifactType.Trace };

        var document = await new UnsupportedArtifactParser().ParseAsync(etl, CancellationToken.None);

        Assert.Contains("ETL", Assert.IsType<UnsupportedDocument>(document).Reason);
    }

    private sealed class StubParser : IDiagnosticParser
    {
        private readonly bool _canHandle;
        private readonly string _tag;

        public StubParser(bool canHandle, string tag)
        {
            _canHandle = canHandle;
            _tag = tag;
        }

        public bool CanHandle(DiagnosticArtifact artifact) => _canHandle;

        public Task<DiagnosticDocument> ParseAsync(DiagnosticArtifact artifact, CancellationToken cancellationToken) =>
            Task.FromResult<DiagnosticDocument>(new UnsupportedDocument { Artifact = artifact, Reason = _tag });
    }

    private sealed class ThrowingParser : IDiagnosticParser
    {
        public bool CanHandle(DiagnosticArtifact artifact) => true;

        public Task<DiagnosticDocument> ParseAsync(DiagnosticArtifact artifact, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class CancellingParser : IDiagnosticParser
    {
        public bool CanHandle(DiagnosticArtifact artifact) => true;

        public Task<DiagnosticDocument> ParseAsync(DiagnosticArtifact artifact, CancellationToken cancellationToken) =>
            throw new OperationCanceledException();
    }
}
