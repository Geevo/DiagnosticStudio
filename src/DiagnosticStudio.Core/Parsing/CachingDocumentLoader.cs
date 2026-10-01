using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;

namespace DiagnosticStudio.Core.Parsing;

/// <param name="MaxEstimatedBytes">Estimated memory the cache may hold. A document larger than this is never cached.</param>
/// <param name="EstimateBytes">Approximate memory a loaded document keeps alive. Defaults to <see cref="DocumentSizeEstimator.Estimate"/>.</param>
public sealed record DocumentCacheOptions(
    long MaxEstimatedBytes = 768L * 1024 * 1024,
    Func<DiagnosticArtifact, DiagnosticDocument, long>? EstimateBytes = null);

public readonly record struct DocumentCacheStatistics(int Entries, long EstimatedBytes, long Hits, long Misses, long Evictions);

/// <summary>Control surface of the shared document cache, for the parts of the application that own its lifetime.</summary>
public interface IDocumentCache
{
    DocumentCacheStatistics Statistics { get; }

    /// <summary>Drops everything, e.g. when the investigation changes. In-flight loads are cancelled.</summary>
    void Clear();
}

/// <summary>Rough memory cost of the documents the parsers produce; deliberately simple and conservative.</summary>
public static class DocumentSizeEstimator
{
    // Measured on real data: a registry export holds roughly five to six times its file size as objects; an event
    // log index is a few dozen bytes per event; a text index is one checkpoint per 128 lines.
    public static long Estimate(DiagnosticArtifact artifact, DiagnosticDocument document) => document switch
    {
        RegistryDocument => Math.Max(artifact.Size, 1024) * 6,
        EventLogDocument events => (events.Source.Count * 48L) + (4L * 1024 * 1024),
        TextDocument text => (text.Lines.LineCount / 16L) + 65536,
        StructuredDocument structured => (structured.NodeCount * 160L) + (structured.RawSource.LineCount / 16L) + 65536,
        _ => 4096,
    };
}

/// <summary>
/// Shares parsed documents between everything that needs them (viewers, global search, rules) so an artifact is
/// parsed once rather than once per consumer.
/// </summary>
/// <remarks>
/// <para>Concurrent requests for the same artifact share one parse. That parse is cancelled only when every caller
/// waiting on it has gone away, so one abandoned search cannot cancel a viewer's load.</para>
/// <para>Documents are immutable once loaded and hold no file handles, so sharing them is safe and evicting one never
/// affects a consumer that still has it. Memory is bounded by estimated size with least-recently-used eviction.
/// An entry is dropped when the file behind it changes (size or modification time), which matters for directory
/// inputs that are read in place.</para>
/// </remarks>
public sealed class CachingDocumentLoader : IDocumentLoader, IDocumentCache
{
    private readonly IDocumentLoader _inner;
    private readonly long _maxBytes;
    private readonly Func<DiagnosticArtifact, DiagnosticDocument, long> _estimate;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = new();
    private long _clock;
    private long _bytes;
    private long _hits;
    private long _misses;
    private long _evictions;

    public CachingDocumentLoader(IDocumentLoader inner, DocumentCacheOptions? options = null)
    {
        options ??= new DocumentCacheOptions();
        _inner = inner;
        _maxBytes = options.MaxEstimatedBytes;
        _estimate = options.EstimateBytes ?? DocumentSizeEstimator.Estimate;
    }

    public DocumentCacheStatistics Statistics
    {
        get
        {
            lock (_gate)
            {
                return new DocumentCacheStatistics(_entries.Count, _bytes, _hits, _misses, _evictions);
            }
        }
    }

    public async Task<DocumentLoadResult> LoadAsync(DiagnosticArtifact artifact, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stamp = FileStamp.Of(artifact);
        var entry = Acquire(artifact, stamp);

        try
        {
            return await entry.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AbandonIfUnwatched(artifact.Id, entry);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                entry.Waiters--;
            }
        }
    }

    public void Clear()
    {
        List<Entry> dropped;
        lock (_gate)
        {
            dropped = _entries.Values.ToList();
            _entries.Clear();
            _bytes = 0;
        }

        foreach (var entry in dropped)
        {
            if (!entry.Task.IsCompleted)
            {
                entry.Cts.Cancel();
            }
        }
    }

    private Entry Acquire(DiagnosticArtifact artifact, FileStamp stamp)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(artifact.Id, out var existing))
            {
                if (existing.Stamp.Equals(stamp))
                {
                    existing.Waiters++;
                    existing.LastUsed = ++_clock;
                    _hits++;
                    return existing;
                }

                // The file changed underneath the cached copy: forget it and parse afresh. A load still running is
                // left to finish for the callers already waiting on it; it is simply no longer the cached one.
                Remove(existing);
            }

            _misses++;
            var entry = new Entry(artifact.Id, stamp) { Waiters = 1, LastUsed = ++_clock };
            _entries[artifact.Id] = entry;
            entry.Task = Task.Run(() => _inner.LoadAsync(artifact, entry.Cts.Token));
            _ = entry.Task.ContinueWith(
                finished => OnLoaded(artifact, entry, finished),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return entry;
        }
    }

    // The last caller to give up cancels the shared parse, and the entry is dropped so the next caller starts clean.
    private void AbandonIfUnwatched(Guid id, Entry entry)
    {
        var cancel = false;
        lock (_gate)
        {
            // This caller is still counted (its finally block has not run), so "last" means exactly one remains.
            if (entry.Waiters == 1 && !entry.Task.IsCompleted)
            {
                if (_entries.TryGetValue(id, out var current) && ReferenceEquals(current, entry))
                {
                    _entries.Remove(id);
                }

                cancel = true;
            }
        }

        if (cancel)
        {
            entry.Cts.Cancel();
        }
    }

    private void OnLoaded(DiagnosticArtifact artifact, Entry entry, Task<DocumentLoadResult> finished)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(CurrentOrNull(entry.Id), entry))
            {
                return; // already replaced, cleared or abandoned
            }

            if (!finished.IsCompletedSuccessfully)
            {
                _entries.Remove(entry.Id); // failures and cancellations are never cached
                return;
            }

            var weight = Math.Max(1, _estimate(artifact, finished.Result.Document));
            if (weight > _maxBytes)
            {
                _entries.Remove(entry.Id); // too big to keep; the callers waiting still receive it
                return;
            }

            entry.Weight = weight;
            entry.Counted = true;
            _bytes += weight;
            EvictToBudget(keep: entry);
        }
    }

    private Entry? CurrentOrNull(Guid id) => _entries.TryGetValue(id, out var current) ? current : null;

    // Called with the gate held.
    private void EvictToBudget(Entry keep)
    {
        while (_bytes > _maxBytes)
        {
            Entry? oldest = null;
            foreach (var candidate in _entries.Values)
            {
                if (candidate.Counted && !ReferenceEquals(candidate, keep) && (oldest is null || candidate.LastUsed < oldest.LastUsed))
                {
                    oldest = candidate;
                }
            }

            if (oldest is null)
            {
                return;
            }

            Remove(oldest);
            _evictions++;
        }
    }

    // Called with the gate held.
    private void Remove(Entry entry)
    {
        if (_entries.TryGetValue(entry.Id, out var current) && ReferenceEquals(current, entry))
        {
            _entries.Remove(entry.Id);
        }

        if (entry.Counted)
        {
            _bytes -= entry.Weight;
            entry.Counted = false;
        }
    }

    private sealed class Entry
    {
        public Entry(Guid id, FileStamp stamp)
        {
            Id = id;
            Stamp = stamp;
        }

        public Guid Id { get; }
        public FileStamp Stamp { get; }
        public CancellationTokenSource Cts { get; } = new();
        public Task<DocumentLoadResult> Task { get; set; } = null!;
        public int Waiters { get; set; }
        public long LastUsed { get; set; }
        public long Weight { get; set; }
        public bool Counted { get; set; }
    }

    private readonly record struct FileStamp(long Length, long LastWriteTicks)
    {
        public static FileStamp Of(DiagnosticArtifact artifact)
        {
            try
            {
                if (artifact.ExtractedPath is { } path && new FileInfo(path) is { Exists: true } info)
                {
                    return new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
            {
                // An unreadable stamp just means "unknown"; the parser will report the real problem.
            }

            return new FileStamp(-1, -1);
        }
    }
}
