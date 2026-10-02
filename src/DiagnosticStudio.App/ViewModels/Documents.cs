using DiagnosticStudio.Core.Parsing;
using System.IO;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.App.ViewModels.EventLogViewer;
using DiagnosticStudio.App.ViewModels.RegistryViewer;
using DiagnosticStudio.App.ViewModels.HtmlViewer;
using DiagnosticStudio.App.ViewModels.StructuredViewer;
using DiagnosticStudio.App.ViewModels.TableViewer;
using DiagnosticStudio.App.ViewModels.TextViewer;
using DiagnosticStudio.Core.Ingestion;
using DiagnosticStudio.Core.Navigation;
using DiagnosticStudio.Parsers;

namespace DiagnosticStudio.App.ViewModels;

public sealed record DetailRow(string Label, string Value);

public sealed record OverviewSection(string Title, IReadOnlyList<DetailRow> Rows);

/// <summary>Base for anything that can be hosted in a document tab.</summary>
public abstract partial class DocumentViewModel : ObservableObject
{
    public abstract string Title { get; }
    public virtual string? ToolTip => null;
    public virtual bool CanClose => true;

    /// <summary>Whether the document pane's zoom applies to what this document shows.</summary>
    public virtual bool SupportsContentZoom => true;

    /// <summary>Evidence location for documents tied to an artifact; <c>null</c> for the Overview.</summary>
    public virtual DiagnosticLocation? Location => null;

    /// <summary>
    /// A tab opened by a single click in the explorer. It is shown in italics and replaced by the next one; pinning it
    /// (double-click, or the pin on the tab) keeps it.
    /// </summary>
    [ObservableProperty]
    private bool _isPreview;

    /// <summary>Called when the tab is closed or replaced, so work done only for it can stop.</summary>
    public virtual void OnClosed()
    {
    }
}

public sealed class OverviewDocumentViewModel : DocumentViewModel
{
    public OverviewDocumentViewModel(InvestigationWorkspace? workspace)
    {
        if (workspace is null)
        {
            Sections = Array.Empty<OverviewSection>();
            EmptyMessage = "No bundle is open. Drop a ZIP or an extracted folder here, or use File > Open Archive (Ctrl+O).";
            return;
        }

        Sections = BuildSections(workspace);
    }

    public override string Title => "Overview";
    public override bool CanClose => false;

    public IReadOnlyList<OverviewSection> Sections { get; }
    public string? EmptyMessage { get; }
    public bool HasWorkspace => EmptyMessage is null;

    private static IReadOnlyList<OverviewSection> BuildSections(InvestigationWorkspace workspace)
    {
        var artifacts = workspace.Artifacts;
        var inv = CultureInfo.CurrentCulture;

        var bundle = new List<DetailRow>
        {
            new("Bundle", Path.GetFileName(workspace.InputPath.TrimEnd('\\', '/'))),
            new("Source path", workspace.InputPath),
        };

        var counts = new List<DetailRow> { new("Total", artifacts.Count.ToString("N0", inv)) };
        foreach (var group in artifacts.GroupBy(a => a.ArtifactType).OrderByDescending(g => g.Count()))
        {
            counts.Add(new(DescribeType(group.Key), group.Count().ToString("N0", inv)));
        }

        var containers = artifacts.Count(a => a.IsContainer);
        if (containers > 0)
        {
            counts.Add(new("Archives opened", containers.ToString("N0", inv)));
        }

        var unopened = artifacts.Count(a => a.ArtifactType == ArtifactType.Archive && !a.IsContainer);
        if (unopened > 0)
        {
            counts.Add(new("Archives not opened", unopened.ToString("N0", inv)));
        }

        var maxDepth = artifacts.Count == 0 ? 0 : artifacts.Max(a => a.NestingDepth);
        if (maxDepth > 0)
        {
            counts.Add(new("Deepest nesting", maxDepth.ToString(inv)));
        }

        var ingestion = new List<DetailRow>();
        foreach (var severity in new[] { IngestionIssueSeverity.Error, IngestionIssueSeverity.Warning, IngestionIssueSeverity.Information })
        {
            var n = workspace.Issues.Count(i => i.Severity == severity);
            if (n > 0)
            {
                ingestion.Add(new(severity + "s", n.ToString("N0", inv)));
            }
        }

        if (ingestion.Count == 0)
        {
            ingestion.Add(new("Issues", "None"));
        }

        return new[]
        {
            new OverviewSection("Bundle", bundle),
            new OverviewSection("Artifacts", counts),
            new OverviewSection("Ingestion", ingestion),
        };
    }

    private static string DescribeType(ArtifactType type) => type switch
    {
        ArtifactType.TextLog => "Text logs",
        ArtifactType.CommandOutput => "Command outputs",
        ArtifactType.EventLog => "Event logs",
        ArtifactType.RegistryExport => "Registry exports",
        ArtifactType.Archive => "Archives",
        ArtifactType.Xml => "XML files",
        ArtifactType.Json => "JSON files",
        ArtifactType.Html => "HTML files",
        ArtifactType.Csv => "CSV files",
        ArtifactType.Trace => "Traces (ETL)",
        ArtifactType.Binary => "Binary files",
        _ => "Unclassified",
    };
}

/// <summary>Tab for a single artifact: provenance, classification and (once parsers exist) its viewer content.</summary>
public sealed partial class ArtifactDocumentViewModel : DocumentViewModel
{
    public ArtifactDocumentViewModel(DiagnosticArtifact artifact)
    {
        Artifact = artifact;
        Location = DiagnosticLocation.ForArtifact(artifact.Id);
        Details = BuildDetails(artifact);
    }

    public DiagnosticArtifact Artifact { get; }

    public override string Title => Artifact.Name;
    public override string? ToolTip => Artifact.ProvenanceDisplay;
    public override DiagnosticLocation Location { get; }

    public IReadOnlyList<DetailRow> Details { get; }

    [ObservableProperty]
    private DiagnosticDocument? _document;

    /// <summary>Content viewer for the parsed document; <c>null</c> while loading or when only metadata can be shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SupportsContentZoom))]
    private object? _viewer;

    // A web page is a browser surface with its own zoom (Ctrl+wheel over it).
    public override bool SupportsContentZoom => Viewer is not HtmlViewerViewModel;

    private DiagnosticLocation? _pendingLocation;
    private SearchHighlight? _pendingHighlight;

    /// <summary>Raised when the user deliberately navigates within the document (e.g. go to line), for navigation history.</summary>
    public event EventHandler<DiagnosticLocation>? LocationNavigated;

    /// <summary>Moves the viewer to <paramref name="location"/>, or remembers it until the document has loaded.</summary>
    public void NavigateTo(DiagnosticLocation location, SearchHighlight? highlight = null)
    {
        if (Viewer is ILocationNavigable navigable)
        {
            navigable.NavigateTo(location);
            if (highlight is not null && Viewer is ISearchHighlightable highlightable)
            {
                highlightable.Highlight(highlight);
            }

            return;
        }

        if (IsLoading)
        {
            _pendingLocation = location;
            _pendingHighlight = highlight;
        }
    }

    [ObservableProperty]
    private bool _isLoading = true;

    /// <summary>The file has no content at all (0 bytes), so a blank viewer is not a rendering fault.</summary>
    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string? _statusMessage;

    private readonly CancellationTokenSource _closed = new();
    private bool _reloaded;

    /// <summary>Closing the tab stops loading the file, unless something else (search, the rules) still needs it.</summary>
    public override void OnClosed() => _closed.Cancel();

    /// <summary>
    /// Reads the file again and shows the new content, returning to where the engineer was (the same line or event) when
    /// the new content still has it. The old content stays on screen until the new is ready.
    /// </summary>
    public Task ReloadAsync(IDocumentLoader loader, IOutputLog output, CancellationToken cancellationToken)
    {
        if (Viewer is ICurrentPosition here && here.CurrentPosition(Artifact.Id) is { } location)
        {
            _pendingLocation = location;
            _pendingHighlight = null;
        }

        IsLoading = true;
        _reloaded = true;
        return LoadAsync(loader, output, cancellationToken);
    }

    public async Task LoadAsync(IDocumentLoader loader, IOutputLog output, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closed.Token);
        cancellationToken = linked.Token;
        try
        {
            var result = await loader.LoadAsync(Artifact, cancellationToken).ConfigureAwait(true);
            Document = result.Document;
            IsEmpty = IsEmptyFile(Artifact, result.Document);
            StatusMessage = result.Document is UnsupportedDocument unsupported ? unsupported.Reason : null;
            if (result.FailureMessage is { } failure)
            {
                output.Write(OutputSeverity.Error, "Parser", failure);
            }

            Viewer = CreateViewer(result.Document, output);
            if (Viewer is not null && _pendingLocation is { } pending)
            {
                _pendingLocation = null;
                var pendingHighlight = _pendingHighlight;
                _pendingHighlight = null;
                NavigateTo(pending, pendingHighlight);
            }
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Loading was cancelled.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static bool IsEmptyFile(DiagnosticArtifact artifact, DiagnosticDocument document)
    {
        if (DocumentText.LinesOf(document) is { ByteLength: 0 })
        {
            return true;
        }

        try
        {
            return artifact.ExtractedPath is { } path && new FileInfo(path) is { Exists: true, Length: 0 };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private object? CreateViewer(DiagnosticDocument document, IOutputLog output)
    {
        switch (document)
        {
            case TextDocument text:
                var textViewer = new TextViewerViewModel(text.Lines);
                textViewer.LineNavigated += RaiseLineNavigated;
                return textViewer;

            case HtmlDocument html:
                var htmlViewer = new HtmlViewerViewModel(html);
                htmlViewer.Raw.LineNavigated += RaiseLineNavigated;
                return htmlViewer;

            case TableDocument table:
                var tableViewer = new TableViewerViewModel(table);
                tableViewer.Raw.LineNavigated += RaiseLineNavigated;
                return tableViewer;

            case StructuredDocument structured:
                var structuredViewer = new StructuredViewerViewModel(structured);
                structuredViewer.Raw.LineNavigated += RaiseLineNavigated;
                return structuredViewer;

            case RegistryDocument registry:
                var registryViewer = new RegistryViewerViewModel(registry);
                registryViewer.Raw.LineNavigated += RaiseLineNavigated;
                if (registry.TotalIssueCount > 0 || registry.IsTruncated)
                {
                    output.Write(
                        OutputSeverity.Warning,
                        "Registry",
                        $"{Artifact.ProvenanceDisplay}: {registryViewer.WarningText}");
                }

                return registryViewer;

            case EventLogDocument eventLog:
                var eventViewer = new EventLogViewerViewModel(eventLog, _reloaded);
                if (eventViewer.WarningText is { } eventWarning)
                {
                    output.Write(OutputSeverity.Warning, "EventLog", $"{Artifact.ProvenanceDisplay}: {eventWarning}");
                }

                foreach (var issue in eventLog.Issues.Take(20))
                {
                    output.Write(OutputSeverity.Warning, "EventLog", $"{Artifact.ProvenanceDisplay}: {issue.Message}");
                }

                if (eventLog.TotalIssueCount > 20)
                {
                    output.Write(OutputSeverity.Warning, "EventLog", $"{Artifact.ProvenanceDisplay}: {eventLog.TotalIssueCount - 20:N0} more problems not listed.");
                }

                return eventViewer;

            default:
                return null;
        }
    }

    private void RaiseLineNavigated(object? sender, int line) =>
        LocationNavigated?.Invoke(this, DiagnosticLocation.ForLine(Artifact.Id, line));

    private static IReadOnlyList<DetailRow> BuildDetails(DiagnosticArtifact artifact)
    {
        var rows = new List<DetailRow>
        {
            new("Name", artifact.Name),
            new("Type", artifact.ArtifactType.ToString()),
        };

        if (artifact.Category is not null)
        {
            rows.Add(new("Category", artifact.Category));
        }

        if (artifact.Subtype is not null)
        {
            rows.Add(new("Subtype", artifact.Subtype));
        }

        rows.Add(new("Size", FormatSize(artifact.Size)));
        rows.Add(new("Provenance", artifact.ProvenanceDisplay));
        if (artifact.NestingDepth > 0)
        {
            rows.Add(new("Nesting depth", artifact.NestingDepth.ToString(CultureInfo.CurrentCulture)));
        }

        if (artifact.ExtractedPath is not null)
        {
            rows.Add(new("Working copy", artifact.ExtractedPath));
        }

        rows.Add(new("Location", DiagnosticLocation.ForArtifact(artifact.Id).ToString()));
        return rows;
    }

    internal static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes:N0} B"
            : string.Create(CultureInfo.CurrentCulture, $"{value:0.##} {units[unit]} ({bytes:N0} bytes)");
    }
}
