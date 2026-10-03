using DiagnosticStudio.Core.Archives;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Ingestion;

namespace DiagnosticStudio.Ingestion;

/// <summary>
/// Opens a ZIP bundle or an extracted directory, discovers artifacts, recursively opens nested archives
/// through <see cref="IArchiveProvider"/>s and records provenance. Failures on individual items become
/// <see cref="IngestionIssue"/>s; they do not abort the bundle.
/// </summary>
public sealed class BundleIngestor : IBundleIngestor
{
    private const int ProgressInterval = 250;

    private readonly IReadOnlyList<IArchiveProvider> _providers;

    public BundleIngestor(IEnumerable<IArchiveProvider> providers)
    {
        _providers = providers.ToList();
    }

    public Task<InvestigationWorkspace> IngestAsync(
        string inputPath,
        IngestionOptions options,
        IProgress<IngestionProgress>? progress,
        CancellationToken cancellationToken) =>
        Task.Run(() => RunAsync(inputPath, options, progress, cancellationToken), cancellationToken);

    private async Task<InvestigationWorkspace> RunAsync(
        string inputPath,
        IngestionOptions options,
        IProgress<IngestionProgress>? progress,
        CancellationToken ct)
    {
        var run = new Run(options, progress);
        run.Report(IngestionStage.Validating, "Validating input");

        var isFile = File.Exists(inputPath);
        var isDirectory = Directory.Exists(inputPath);
        if (!isFile && !isDirectory)
        {
            throw new FileNotFoundException("The path does not exist.", inputPath);
        }

        var fullInput = Path.GetFullPath(inputPath);
        var workspaceId = Guid.NewGuid();
        var root = options.WorkspaceRoot ?? WorkspaceLocations.DefaultRoot;
        var workingDirectory = Path.Combine(root, WorkspaceLocations.NewDirectoryName(workspaceId));

        // The lease marks this directory as owned by a live process, so a later start can safely sweep it if we crash.
        var lease = WorkspaceLease.Create(workingDirectory);

        try
        {
            var pending = new Queue<DiagnosticArtifact>();

            run.Report(IngestionStage.Reading, "Reading " + Path.GetFileName(fullInput));
            if (isFile)
            {
                var rootArtifact = CreateRootFileArtifact(fullInput, run);
                run.Add(rootArtifact, pending);
            }
            else
            {
                DiscoverDirectory(fullInput, run, pending, ct);
            }

            var archiveIndex = 0;
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var archive = pending.Dequeue();
                await OpenArchiveAsync(archive, workingDirectory, ++archiveIndex, run, pending, ct)
                    .ConfigureAwait(false);
            }

            run.Report(IngestionStage.Completed, $"Discovered {run.Artifacts.Count:N0} artifacts");
            return new InvestigationWorkspace(
                workspaceId,
                fullInput,
                workingDirectory,
                run.Artifacts,
                run.Issues,
                lease.Dispose);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private DiagnosticArtifact CreateRootFileArtifact(string fullPath, Run run)
    {
        var name = Path.GetFileName(fullPath);
        var provenance = new[] { name };
        var classification = ArtifactClassifier.Classify(name, provenance, fullPath);
        return BuildArtifact(
            name, fullPath, fullPath, parent: null, provenance, nestingDepth: 0, classification, size: new FileInfo(fullPath).Length);
    }

    private void DiscoverDirectory(
        string directory,
        Run run,
        Queue<DiagnosticArtifact> pending,
        CancellationToken ct)
    {
        var rootName = new DirectoryInfo(directory).Name;
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            // Do not follow junctions/symlinks: they can point outside the bundle or loop.
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        run.Report(IngestionStage.Discovering, "Discovering artifacts");
        foreach (var path in Directory.EnumerateFiles(directory, "*", enumeration))
        {
            ct.ThrowIfCancellationRequested();

            if (run.Budget.TryReserveFile() is { } violation)
            {
                run.AddIssue(IngestionStage.Discovering, IngestionIssueSeverity.Error, rootName,
                    "Directory discovery stopped: " + violation.Message, component: null);
                break;
            }

            var relative = Path.GetRelativePath(directory, path);
            var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var provenance = new[] { rootName }.Concat(segments).ToArray();
            var name = segments[^1];

            long size;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (IOException)
            {
                size = 0;
            }

            var classification = ArtifactClassifier.Classify(name, provenance, path);
            var artifact = BuildArtifact(
                name, relative.Replace('\\', '/'), path, parent: null, provenance, nestingDepth: 0, classification, size);
            run.Add(artifact, pending);
        }
    }

    private async Task OpenArchiveAsync(
        DiagnosticArtifact archive,
        string workingDirectory,
        int index,
        Run run,
        Queue<DiagnosticArtifact> pending,
        CancellationToken ct)
    {
        var subject = archive.ProvenanceDisplay;

        if (archive.NestingDepth >= run.Options.Limits.MaxNestingDepth)
        {
            run.AddIssue(IngestionStage.Extracting, IngestionIssueSeverity.Warning, subject,
                $"Not opened: nesting depth limit of {run.Options.Limits.MaxNestingDepth} reached.", component: null);
            return;
        }

        var provider = _providers.FirstOrDefault(p => p.CanOpen(archive));
        if (provider is null)
        {
            run.AddIssue(IngestionStage.Extracting, IngestionIssueSeverity.Information, subject,
                $"No archive provider available for {archive.Subtype ?? "this"} archive; it is listed but its contents were not inspected.",
                component: null);
            return;
        }

        run.Report(IngestionStage.Extracting, "Extracting " + archive.Name);
        var destination = Path.Combine(workingDirectory, "a" + index.ToString("D4"));
        var context = new ArchiveExtractionContext(
            run.Budget,
            archive.NestingDepth,
            issue => run.Issues.Add(issue));

        IReadOnlyList<ExtractedArchiveEntry> entries;
        try
        {
            entries = await provider.ExtractAsync(archive, destination, context, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                       or NotSupportedException)
        {
            // Whole-archive failure (corrupt central directory etc.): keep the artifact, report, move on.
            run.AddIssue(IngestionStage.Extracting, IngestionIssueSeverity.Error, subject,
                "Archive could not be opened: " + ex.Message, provider.Name);
            run.ReplaceWithLeaf(archive);
            return;
        }

        run.MarkContainer(archive);
        run.ArchivesExtracted++;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            if (!SafeEntryPath.TrySplit(entry.EntryPath, out var segments, out _))
            {
                continue; // provider already vetted this; defensive only
            }

            var name = segments[^1];
            var provenance = archive.Provenance.Concat(segments).ToArray();
            var classification = ArtifactClassifier.Classify(name, provenance, entry.ExtractedPath);
            var artifact = BuildArtifact(
                name, entry.EntryPath, entry.ExtractedPath, archive, provenance, archive.NestingDepth + 1,
                classification, entry.Size);
            run.Add(artifact, pending);
        }
    }

    private static DiagnosticArtifact BuildArtifact(
        string name,
        string originalPath,
        string extractedPath,
        DiagnosticArtifact? parent,
        IReadOnlyList<string> provenance,
        int nestingDepth,
        ArtifactClassification classification,
        long size) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            OriginalPath = originalPath,
            ExtractedPath = extractedPath,
            ParentContainerId = parent?.Id,
            Provenance = provenance,
            NestingDepth = nestingDepth,
            ArtifactType = classification.Type,
            Category = classification.Category,
            Subtype = classification.Subtype,
            Size = size,
        };

    /// <summary>Mutable state for a single ingestion run.</summary>
    private sealed class Run
    {
        private readonly IProgress<IngestionProgress>? _progress;
        private readonly Dictionary<Guid, int> _index = new();

        public Run(IngestionOptions options, IProgress<IngestionProgress>? progress)
        {
            Options = options;
            Budget = new ExtractionBudget(options.Limits);
            _progress = progress;
        }

        public IngestionOptions Options { get; }
        public ExtractionBudget Budget { get; }
        public List<DiagnosticArtifact> Artifacts { get; } = new();
        public List<IngestionIssue> Issues { get; } = new();
        public int ArchivesExtracted { get; set; }

        public void Add(DiagnosticArtifact artifact, Queue<DiagnosticArtifact> pending)
        {
            _index[artifact.Id] = Artifacts.Count;
            Artifacts.Add(artifact);
            if (artifact.ArtifactType == ArtifactType.Archive)
            {
                pending.Enqueue(artifact);
            }

            if (Artifacts.Count % ProgressInterval == 0)
            {
                Report(IngestionStage.Discovering, $"Discovering artifacts ({Artifacts.Count:N0})");
            }
        }

        public void MarkContainer(DiagnosticArtifact archive) =>
            Artifacts[_index[archive.Id]] = archive with { IsContainer = true };

        // An archive that failed to open stays in the catalogue as an ordinary leaf file.
        public void ReplaceWithLeaf(DiagnosticArtifact archive) =>
            Artifacts[_index[archive.Id]] = archive with { IsContainer = false };

        public void AddIssue(
            IngestionStage stage,
            IngestionIssueSeverity severity,
            string subject,
            string message,
            string? component) =>
            Issues.Add(new IngestionIssue
            {
                Stage = stage,
                Severity = severity,
                Subject = subject,
                Message = message,
                Component = component,
            });

        public void Report(IngestionStage stage, string message) =>
            _progress?.Report(new IngestionProgress
            {
                Stage = stage,
                Message = message,
                ArtifactsDiscovered = Artifacts.Count,
                ArchivesExtracted = ArchivesExtracted,
                BytesExtracted = Budget.BytesExtracted,
            });
    }
}
