using DiagnosticStudio.App.ViewModels;
using DiagnosticStudio.App.ViewModels.EventLogViewer;
using DiagnosticStudio.App.ViewModels.RegistryViewer;
using DiagnosticStudio.App.ViewModels.TextViewer;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Ingestion;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Core.Parsing;
using DiagnosticStudio.Parsers;
using DiagnosticStudio.Tests.Evtx;

namespace DiagnosticStudio.Tests.App;

public sealed class DocumentHostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-host-" + Guid.NewGuid().ToString("N"));
    private readonly List<DiagnosticArtifact> _artifacts = new();

    public DocumentHostTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private DiagnosticArtifact AddLog(string name, int lines, ArtifactType type = ArtifactType.TextLog)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllLines(path, Enumerable.Range(1, lines).Select(i => $"{name} line {i}"));
        var artifact = new DiagnosticArtifact
        {
            Id = Guid.NewGuid(),
            Name = name,
            OriginalPath = name,
            ExtractedPath = path,
            Provenance = new[] { "Bundle", name },
            ArtifactType = type,
        };
        _artifacts.Add(artifact);
        return artifact;
    }

    private DiagnosticArtifact AddReg(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        var artifact = new DiagnosticArtifact
        {
            Id = Guid.NewGuid(),
            Name = name,
            OriginalPath = name,
            ExtractedPath = path,
            Provenance = new[] { "Bundle", name },
            ArtifactType = ArtifactType.RegistryExport,
        };
        _artifacts.Add(artifact);
        return artifact;
    }

    private DiagnosticArtifact AddEvtx(string name, int count)
    {
        var path = Path.Combine(_dir, name);
        var events = Enumerable.Range(0, count)
            .Select(i => new TestEvent(500 + i, "Provider" + (i % 3), (uint)(100 + i % 7), EventLevels.Error,
                new DateTime(2026, 7, 23, 14, 0, 0, DateTimeKind.Utc).AddSeconds(i), "PC", "a" + i, "b"))
            .ToList();
        File.WriteAllBytes(path, new EvtxBuilder().Build(events));
        var artifact = new DiagnosticArtifact
        {
            Id = Guid.NewGuid(),
            Name = name,
            OriginalPath = name,
            ExtractedPath = path,
            Provenance = new[] { "Bundle", name },
            ArtifactType = ArtifactType.EventLog,
        };
        _artifacts.Add(artifact);
        return artifact;
    }

    private sealed class FixedIngestor : IBundleIngestor
    {
        private readonly InvestigationWorkspace _workspace;

        public FixedIngestor(InvestigationWorkspace workspace) => _workspace = workspace;

        public Task<InvestigationWorkspace> IngestAsync(
            string inputPath, IngestionOptions options, IProgress<IngestionProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(_workspace);
    }

    private async Task<(DocumentHostViewModel Host, INavigationHistory History, OutputViewModel Output)> CreateHost()
    {
        var workspace = new InvestigationWorkspace(
            Guid.NewGuid(), _dir, _dir, _artifacts, Array.Empty<IngestionIssue>());
        var output = new OutputViewModel();
        var workspaceVm = new WorkspaceViewModel(new FixedIngestor(workspace), output);
        var history = new NavigationHistory();
        var loader = new DocumentLoader(new IDiagnosticParser[] { new RegFileParser(), new EvtxParser(), new TextLogParser(), new UnsupportedArtifactParser() });
        var host = new DocumentHostViewModel(workspaceVm, loader, history, output);

        await workspaceVm.OpenAsync(_dir);
        return (host, history, output);
    }

    private static async Task<ArtifactDocumentViewModel> Loaded(DocumentHostViewModel host, DiagnosticArtifact artifact)
    {
        var document = host.Documents.OfType<ArtifactDocumentViewModel>().Single(d => d.Artifact.Id == artifact.Id);
        for (var i = 0; i < 200 && document.IsLoading; i++)
        {
            await Task.Delay(25);
        }

        Assert.False(document.IsLoading, "document did not finish loading");
        return document;
    }

    [Fact]
    public async Task Opening_a_text_artifact_produces_a_text_viewer()
    {
        var log = AddLog("agent.log", 300);
        var (host, _, _) = await CreateHost();

        host.OpenArtifact(log);
        var document = await Loaded(host, log);

        var viewer = Assert.IsType<TextViewerViewModel>(document.Viewer);
        Assert.Equal(300, viewer.Lines.Count);
        Assert.IsType<TextDocument>(document.Document);
    }

    [Fact]
    public async Task Artifacts_without_a_viewer_fall_back_to_metadata()
    {
        var bin = AddLog("blob.bin", 3, ArtifactType.Binary);
        var (host, _, _) = await CreateHost();

        host.OpenArtifact(bin);
        var document = await Loaded(host, bin);

        Assert.Null(document.Viewer);
        Assert.IsType<UnsupportedDocument>(document.Document);
    }

    [Fact]
    public async Task Opening_a_line_location_scrolls_the_viewer_once_it_has_loaded()
    {
        var log = AddLog("agent.log", 500);
        var (host, _, _) = await CreateHost();

        Assert.True(host.OpenLocation(DiagnosticLocation.ForLine(log.Id, 321)));
        var document = await Loaded(host, log);

        Assert.Equal(321, ((TextViewerViewModel)document.Viewer!).CurrentLine);
    }

    [Fact]
    public async Task Opening_a_line_location_in_an_already_open_document_moves_the_viewer()
    {
        var log = AddLog("agent.log", 500);
        var (host, _, _) = await CreateHost();
        host.OpenArtifact(log);
        var document = await Loaded(host, log);

        host.OpenLocation(DiagnosticLocation.ForLine(log.Id, 40));

        Assert.Equal(40, ((TextViewerViewModel)document.Viewer!).CurrentLine);
        Assert.Single(host.Documents.OfType<ArtifactDocumentViewModel>());
    }

    [Fact]
    public async Task Unknown_locations_are_reported_not_thrown()
    {
        var (host, _, output) = await CreateHost();

        var opened = host.OpenLocation(DiagnosticLocation.ForLine(Guid.NewGuid(), 1));

        Assert.False(opened);
        Assert.Contains(output.Entries, e => e.Severity == OutputSeverity.Warning && e.Source == "Navigation");
    }

    [Fact]
    public async Task Go_to_line_is_recorded_in_history_and_back_returns_to_the_previous_location()
    {
        var log = AddLog("agent.log", 500);
        var (host, history, _) = await CreateHost();
        host.OpenArtifact(log);
        var document = await Loaded(host, log);
        var viewer = (TextViewerViewModel)document.Viewer!;

        viewer.GoToLineText = "250";
        viewer.GoToLineCommand.Execute(null);

        Assert.Equal(DiagnosticLocation.ForLine(log.Id, 250), history.Current);
        Assert.True(history.CanGoBack);

        host.GoBackCommand.Execute(null);

        Assert.Equal(DiagnosticLocation.ForArtifact(log.Id), history.Current);
        Assert.True(history.CanGoForward);
    }

    [Fact]
    public async Task Going_back_to_another_document_does_not_discard_forward_history()
    {
        var a = AddLog("a.log", 50);
        var b = AddLog("b.log", 50);
        var (host, history, _) = await CreateHost();

        host.OpenArtifact(a);
        host.OpenArtifact(b);
        host.GoBackCommand.Execute(null);

        Assert.Same(a, ((ArtifactDocumentViewModel)host.ActiveDocument!).Artifact);
        Assert.True(history.CanGoForward);

        host.GoForwardCommand.Execute(null);

        Assert.Same(b, ((ArtifactDocumentViewModel)host.ActiveDocument!).Artifact);
    }

    private const string Reg = "Windows Registry Editor Version 5.00\n\n[HKEY_CURRENT_USER\\A\\B]\n\"V\"=\"x\"\n";

    [Fact]
    public async Task Registry_artifacts_open_in_the_registry_viewer_with_a_raw_tab()
    {
        var reg = AddReg("t.reg", Reg);
        var (host, _, _) = await CreateHost();

        host.OpenArtifact(reg);
        var document = await Loaded(host, reg);

        var viewer = Assert.IsType<RegistryViewerViewModel>(document.Viewer);
        Assert.IsType<RegistryDocument>(document.Document);
        Assert.Equal(4, viewer.Raw.Lines.Count);
    }

    [Fact]
    public async Task A_registry_location_opens_the_artifact_and_selects_the_key_and_value_once_loaded()
    {
        var reg = AddReg("t.reg", Reg);
        var (host, _, _) = await CreateHost();

        Assert.True(host.OpenLocation(DiagnosticLocation.ForRegistry(reg.Id, @"HKEY_CURRENT_USER\A\B", "V")));
        var document = await Loaded(host, reg);

        var viewer = (RegistryViewerViewModel)document.Viewer!;
        Assert.Equal(@"HKEY_CURRENT_USER\A\B", viewer.SelectedKeyPath);
        Assert.Equal("V", viewer.SelectedValue!.Name);
    }

    [Fact]
    public async Task Go_to_line_in_the_raw_tab_of_a_registry_file_is_recorded_in_history()
    {
        var reg = AddReg("t.reg", Reg);
        var (host, history, _) = await CreateHost();
        host.OpenArtifact(reg);
        var viewer = (RegistryViewerViewModel)(await Loaded(host, reg)).Viewer!;

        viewer.Raw.GoToLineText = "4";
        viewer.Raw.GoToLineCommand.Execute(null);

        Assert.Equal(DiagnosticLocation.ForLine(reg.Id, 4), history.Current);
    }

    [Fact]
    public async Task Event_log_artifacts_open_in_the_event_log_viewer()
    {
        var evtx = AddEvtx("System.evtx", 50);
        var (host, _, _) = await CreateHost();

        host.OpenArtifact(evtx);
        var document = await Loaded(host, evtx);

        var viewer = Assert.IsType<EventLogViewerViewModel>(document.Viewer);
        Assert.IsType<EventLogDocument>(document.Document);
        Assert.Equal(50, viewer.Events.Count);
    }

    [Fact]
    public async Task An_event_record_location_opens_the_artifact_and_selects_the_event_once_loaded()
    {
        var evtx = AddEvtx("System.evtx", 50);
        var (host, _, _) = await CreateHost();

        Assert.True(host.OpenLocation(DiagnosticLocation.ForEventRecord(evtx.Id, 520)));
        var document = await Loaded(host, evtx);

        var viewer = (EventLogViewerViewModel)document.Viewer!;
        Assert.Equal(520, viewer.SelectedEvent!.RecordId);
    }

    [Fact]
    public async Task A_corrupt_event_log_degrades_to_an_unsupported_document_with_the_reason_in_Output()
    {
        var evtx = AddEvtx("bad.evtx", 5);
        File.WriteAllBytes(evtx.ExtractedPath!, new byte[] { 1, 2, 3, 4 });
        var (host, _, output) = await CreateHost();

        host.OpenArtifact(evtx);
        var document = await Loaded(host, evtx);

        Assert.Null(document.Viewer);
        Assert.IsType<UnsupportedDocument>(document.Document);
        Assert.Contains(output.Entries, e => e.Severity == OutputSeverity.Error && e.Message.Contains("EvtxParser"));
    }

    [Fact]
    public async Task Overview_is_always_present_and_cannot_be_closed()
    {
        var (host, _, _) = await CreateHost();

        Assert.IsType<OverviewDocumentViewModel>(host.ActiveDocument);
        host.CloseCommand.Execute(host.ActiveDocument);

        Assert.Single(host.Documents);
    }
}
