using DiagnosticStudio.App.Services;
using DiagnosticStudio.App.ViewModels;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Ingestion;
using DiagnosticStudio.Core.Navigation;
using static DiagnosticStudio.Tests.Rules.RuleFixtures;

namespace DiagnosticStudio.Tests.App;

public sealed class ExplorerActionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-act-" + Guid.NewGuid().ToString("N"));
    private readonly OutputViewModel _output = new();
    private readonly Ingestor _ingestor = new();
    private readonly FakeTools _tools = new();
    private readonly FakeClipboard _clipboard = new();

    public ExplorerActionsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class Ingestor : IBundleIngestor
    {
        public InvestigationWorkspace? Next { get; set; }

        public Task<InvestigationWorkspace> IngestAsync(
            string inputPath, IngestionOptions options, IProgress<IngestionProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(Next!);
    }

    private sealed class FakeTools : IExternalToolService
    {
        public string? NotepadPlusPlusPath { get; set; }
        public List<string> Calls { get; } = new();
        public ExternalToolException? Fail { get; set; }

        public void OpenInNotepad(string path) => Record("notepad", path);
        public void OpenInNotepadPlusPlus(string path) => Record("npp", path);
        public void ShowInExplorer(string path) => Record("explorer", path);

        private void Record(string tool, string path)
        {
            if (Fail is not null)
            {
                throw Fail;
            }

            Calls.Add(tool + ": " + path);
        }
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public List<string> Texts { get; } = new();
        public bool Succeeds { get; set; } = true;

        public bool SetText(string text)
        {
            if (Succeeds)
            {
                Texts.Add(text);
            }

            return Succeeds;
        }
    }

    private static DiagnosticArtifact File(string path, params string[] provenance) => new()
    {
        Id = Guid.NewGuid(),
        Name = provenance[^1],
        OriginalPath = provenance[^1],
        ExtractedPath = path,
        Provenance = provenance,
        ArtifactType = ArtifactType.TextLog,
    };

    private async Task<(ExplorerViewModel Explorer, ExplorerNodeViewModel File, ExplorerNodeViewModel Folder, DiagnosticArtifact Artifact)> Open()
    {
        var artifact = File(@"C:\work\ds\agent.log", "Bundle.zip", "Logs", "agent.log");
        var workspace = new WorkspaceViewModel(_ingestor, _output);
        var host = new DocumentHostViewModel(workspace, new FakeLoader(), new NavigationHistory(), _output);
        var explorer = new ExplorerViewModel(workspace, host, _tools, _clipboard, _output);
        _ingestor.Next = new InvestigationWorkspace(Guid.NewGuid(), _dir, _dir, new[] { artifact }, Array.Empty<IngestionIssue>());
        await workspace.OpenAsync(_dir);

        var folder = explorer.Nodes[0].Children[0]; // Logs
        return (explorer, folder.Children[0], folder, artifact);
    }

    [Fact]
    public async Task Open_in_notepad_and_explorer_use_the_files_working_copy()
    {
        var (explorer, file, _, _) = await Open();

        explorer.OpenInNotepadCommand.Execute(file);
        explorer.ShowInFileExplorerCommand.Execute(file);

        Assert.Equal(new[] { @"notepad: C:\work\ds\agent.log", @"explorer: C:\work\ds\agent.log" }, _tools.Calls);
    }

    [Fact]
    public async Task Notepad_plus_plus_is_offered_only_when_it_is_installed()
    {
        var (explorer, file, _, _) = await Open();
        Assert.False(explorer.HasNotepadPlusPlus);

        _tools.NotepadPlusPlusPath = @"C:\Program Files\Notepad++\notepad++.exe";
        Assert.True(explorer.HasNotepadPlusPlus);

        explorer.OpenInNotepadPlusPlusCommand.Execute(file);
        Assert.Equal(new[] { @"npp: C:\work\ds\agent.log" }, _tools.Calls);
    }

    [Fact]
    public async Task File_actions_are_not_available_on_folders()
    {
        var (explorer, file, folder, _) = await Open();

        Assert.True(explorer.OpenInNotepadCommand.CanExecute(file));
        Assert.False(explorer.OpenInNotepadCommand.CanExecute(folder));
        Assert.False(explorer.ShowInFileExplorerCommand.CanExecute(folder));
        Assert.False(explorer.CopyPathCommand.CanExecute(folder));
        Assert.False(explorer.CopyNameCommand.CanExecute(folder));
        Assert.False(explorer.CopyLinkCommand.CanExecute(folder));
        Assert.False(explorer.CopyBundlePathCommand.CanExecute(folder));

        explorer.OpenInNotepadCommand.Execute(folder);
        Assert.Empty(_tools.Calls);
    }

    [Fact]
    public async Task An_artifact_with_no_file_on_disk_cannot_be_opened_externally_but_can_be_named()
    {
        var workspace = new WorkspaceViewModel(_ingestor, _output);
        var host = new DocumentHostViewModel(workspace, new FakeLoader(), new NavigationHistory(), _output);
        var explorer = new ExplorerViewModel(workspace, host, _tools, _clipboard, _output);
        var virtualFile = File(@"C:\x", "Bundle.zip", "a.log") with { ExtractedPath = null };
        _ingestor.Next = new InvestigationWorkspace(Guid.NewGuid(), _dir, _dir, new[] { virtualFile }, Array.Empty<IngestionIssue>());
        await workspace.OpenAsync(_dir);
        var node = explorer.Nodes[0].Children[0];

        Assert.False(explorer.OpenInNotepadCommand.CanExecute(node));
        Assert.False(explorer.CopyPathCommand.CanExecute(node));
        Assert.True(explorer.CopyNameCommand.CanExecute(node));
        Assert.True(explorer.CopyBundlePathCommand.CanExecute(node));
    }

    [Fact]
    public async Task A_tool_that_cannot_start_is_reported_in_output_instead_of_crashing()
    {
        var (explorer, file, _, _) = await Open();
        _tools.Fail = new ExternalToolException("The file is no longer there: x");

        explorer.OpenInNotepadCommand.Execute(file);

        Assert.Contains(_output.Entries, e => e.Source == "Explorer" && e.Severity == OutputSeverity.Warning && e.Message.Contains("no longer there"));
    }

    [Fact]
    public async Task The_copy_commands_put_the_right_text_on_the_clipboard()
    {
        var (explorer, file, _, artifact) = await Open();

        explorer.CopyPathCommand.Execute(file);
        explorer.CopyBundlePathCommand.Execute(file);
        explorer.CopyNameCommand.Execute(file);
        explorer.CopyLinkCommand.Execute(file);

        Assert.Equal(
            new[]
            {
                @"C:\work\ds\agent.log",
                "Bundle.zip \u2192 Logs \u2192 agent.log",
                "agent.log",
                DiagnosticLocation.ForArtifact(artifact.Id).ToString(),
            },
            _clipboard.Texts);
        Assert.True(DiagnosticLocation.TryParse(_clipboard.Texts[3], out var parsed));
        Assert.Equal(artifact.Id, parsed!.ArtifactId);
    }

    [Fact]
    public async Task A_busy_clipboard_is_reported_not_ignored()
    {
        var (explorer, file, _, _) = await Open();
        _clipboard.Succeeds = false;

        explorer.CopyPathCommand.Execute(file);

        Assert.Contains(_output.Entries, e => e.Source == "Explorer" && e.Message.Contains("clipboard"));
    }

    // ---- finding Notepad++ ----

    private static Func<string, string?> Env(Dictionary<string, string> values) => name => values.GetValueOrDefault(name);

    [Fact]
    public void Notepad_plus_plus_is_found_in_the_program_files_folder()
    {
        var env = Env(new() { ["ProgramFiles"] = @"C:\Program Files" });

        var found = NotepadPlusPlusLocator.Find(p => p == @"C:\Program Files\Notepad++\notepad++.exe", env, () => Array.Empty<string>());

        Assert.Equal(@"C:\Program Files\Notepad++\notepad++.exe", found);
    }

    [Fact]
    public void Notepad_plus_plus_is_found_in_a_per_user_install()
    {
        var env = Env(new() { ["LocalAppData"] = @"C:\Users\me\AppData\Local" });

        var found = NotepadPlusPlusLocator.Find(p => p.Contains("Programs"), env, () => Array.Empty<string>());

        Assert.Equal(@"C:\Users\me\AppData\Local\Programs\Notepad++\notepad++.exe", found);
    }

    [Fact]
    public void Notepad_plus_plus_is_found_from_its_registry_entry_when_it_is_somewhere_unusual()
    {
        var found = NotepadPlusPlusLocator.Find(p => p == @"D:\Tools\npp\notepad++.exe", Env(new()), () => new[] { @"D:\Tools\npp" });

        Assert.Equal(@"D:\Tools\npp\notepad++.exe", found);
    }

    [Fact]
    public void The_standard_folder_wins_over_the_registry()
    {
        var env = Env(new() { ["ProgramFiles"] = @"C:\Program Files" });

        var found = NotepadPlusPlusLocator.Find(_ => true, env, () => new[] { @"D:\other" });

        Assert.Equal(@"C:\Program Files\Notepad++\notepad++.exe", found);
    }

    [Fact]
    public void No_notepad_plus_plus_means_null()
    {
        Assert.Null(NotepadPlusPlusLocator.Find(_ => false, Env(new() { ["ProgramFiles"] = @"C:\Program Files" }), () => new[] { @"D:\x" }));
    }
}
