using System.Text.RegularExpressions;

namespace DiagnosticStudio.Ingestion;

public static partial class WorkspaceLocations
{
    public const string DirectoryPrefix = "ws-";
    public const string LeaseFileName = ".lease";

    /// <summary>Parent of every per-investigation working directory the application creates.</summary>
    public static string DefaultRoot => Path.Combine(Path.GetTempPath(), "DiagnosticStudio");

    public static string NewDirectoryName(Guid workspaceId) => DirectoryPrefix + workspaceId.ToString("N");

    /// <summary>True only for names the application itself generates; anything else under the root is left alone.</summary>
    public static bool IsWorkspaceDirectoryName(string name) => WorkspaceName().IsMatch(name);

    [GeneratedRegex("^ws-[0-9a-f]{32}$")]
    private static partial Regex WorkspaceName();
}

/// <summary>
/// Ownership marker for a working directory. The owning process keeps an exclusive handle on a lease file
/// inside it for as long as the workspace lives. The operating system releases that handle when the process
/// ends, even by crash or kill, which is how <see cref="WorkspaceJanitor"/> tells a live workspace from an orphan.
/// </summary>
public sealed class WorkspaceLease : IDisposable
{
    private FileStream? _handle;

    private WorkspaceLease(string workingDirectory, FileStream handle)
    {
        WorkingDirectory = workingDirectory;
        _handle = handle;
    }

    public string WorkingDirectory { get; }

    /// <summary>Creates the working directory and takes the lease on it.</summary>
    public static WorkspaceLease Create(string workingDirectory)
    {
        Directory.CreateDirectory(workingDirectory);
        var handle = new FileStream(
            Path.Combine(workingDirectory, WorkspaceLocations.LeaseFileName),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        return new WorkspaceLease(workingDirectory, handle);
    }

    /// <summary>Releases the lease and removes the working directory. Best effort: failures leave it for the next sweep.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _handle, null) is not { } handle)
        {
            return;
        }

        handle.Dispose();

        // A viewer that was just closed, or a scanner looking at a new file, can still have something open for a
        // moment, so a failed removal is tried again shortly. This runs off the interface thread. What still cannot
        // be removed is left for the janitor on a later start.
        for (var attempt = 0; attempt < RemovalAttempts; attempt++)
        {
            try
            {
                if (Directory.Exists(WorkingDirectory))
                {
                    Directory.Delete(WorkingDirectory, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < RemovalAttempts - 1)
                {
                    Thread.Sleep(RetryDelayMilliseconds);
                }
            }
        }
    }

    private const int RemovalAttempts = 3;
    private const int RetryDelayMilliseconds = 150;
}

public sealed record WorkspaceSweepResult(int Removed, int InUse, int Failed, IReadOnlyList<string> Errors)
{
    public static WorkspaceSweepResult None { get; } = new(0, 0, 0, Array.Empty<string>());
}

/// <summary>What <see cref="WorkspaceJanitor.Survey"/> found: abandoned working directories and the disk they take.</summary>
public sealed record WorkspaceSurvey(int Stale, long Bytes)
{
    public static WorkspaceSurvey None { get; } = new(0, 0);
}

/// <summary>
/// Removes working directories orphaned by a crashed or killed instance. A directory is only removed when
/// its lease can be taken (nobody owns it) or, for directories that have no lease at all, when it is old enough
/// that a live instance cannot still be setting it up. Directories belonging to running instances are never touched.
/// </summary>
public static class WorkspaceJanitor
{
    /// <summary>How old a lease-less directory must be before it is considered abandoned.</summary>
    public static readonly TimeSpan LeaselessGrace = TimeSpan.FromMinutes(10);

    public static Task<WorkspaceSweepResult> SweepStaleAsync(
        string? root = null,
        CancellationToken cancellationToken = default,
        DateTime? utcNow = null) =>
        Task.Run(() => SweepStale(root, cancellationToken, utcNow), cancellationToken);

    public static Task<WorkspaceSurvey> SurveyAsync(
        string? root = null,
        CancellationToken cancellationToken = default,
        DateTime? utcNow = null) =>
        Task.Run(() => Survey(root, cancellationToken, utcNow), cancellationToken);

    /// <summary>
    /// Counts what <see cref="SweepStale(string?, CancellationToken, DateTime?)"/> would remove, without removing it.
    /// Uses the same test for "abandoned", so a directory a running instance owns is never counted.
    /// </summary>
    public static WorkspaceSurvey Survey(string? root = null, CancellationToken cancellationToken = default, DateTime? utcNow = null)
    {
        root ??= WorkspaceLocations.DefaultRoot;
        if (!Directory.Exists(root))
        {
            return WorkspaceSurvey.None;
        }

        var now = utcNow ?? DateTime.UtcNow;
        int stale = 0;
        long bytes = 0;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var info = new DirectoryInfo(directory);
                if (!WorkspaceLocations.IsWorkspaceDirectoryName(info.Name)
                    || info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                    || !IsAbandoned(info, now))
                {
                    continue;
                }

                stale++;
                bytes += SizeOf(info, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Report what was counted so far; a sweep says what it could not remove.
        }

        return new WorkspaceSurvey(stale, bytes);
    }

    private static bool IsAbandoned(DirectoryInfo directory, DateTime utcNow)
    {
        var leasePath = Path.Combine(directory.FullName, WorkspaceLocations.LeaseFileName);
        if (!File.Exists(leasePath))
        {
            return utcNow - directory.LastWriteTimeUtc >= LeaselessGrace;
        }

        try
        {
            using var lease = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static long SizeOf(DirectoryInfo directory, CancellationToken cancellationToken)
    {
        long total = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in directory.EnumerateFiles("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                total += file.Length;
            }
            catch (IOException)
            {
                // Gone since it was listed.
            }
        }

        return total;
    }

    public static WorkspaceSweepResult SweepStale(
        string? root = null,
        CancellationToken cancellationToken = default,
        DateTime? utcNow = null)
    {
        root ??= WorkspaceLocations.DefaultRoot;
        if (!Directory.Exists(root))
        {
            return WorkspaceSweepResult.None;
        }

        var now = utcNow ?? DateTime.UtcNow;
        int removed = 0, inUse = 0, failed = 0;
        var errors = new List<string>();

        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateDirectories(root).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WorkspaceSweepResult(0, 0, 1, new[] { $"Could not list {root}: {ex.Message}" });
        }

        foreach (var directory in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var info = new DirectoryInfo(directory);
            if (!WorkspaceLocations.IsWorkspaceDirectoryName(info.Name)
                || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            switch (TryReclaim(info, now, out var error))
            {
                case Outcome.Removed:
                    removed++;
                    break;
                case Outcome.InUse:
                    inUse++;
                    break;
                case Outcome.Failed:
                    failed++;
                    errors.Add(error!);
                    break;
            }
        }

        return new WorkspaceSweepResult(removed, inUse, failed, errors);
    }

    private enum Outcome
    {
        Removed,
        InUse,
        Failed,
    }

    private static Outcome TryReclaim(DirectoryInfo directory, DateTime utcNow, out string? error)
    {
        error = null;
        var leasePath = Path.Combine(directory.FullName, WorkspaceLocations.LeaseFileName);

        FileStream? lease = null;
        try
        {
            if (File.Exists(leasePath))
            {
                try
                {
                    // Succeeds only if no live process holds the lease.
                    lease = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    return Outcome.InUse;
                }
            }
            else if (utcNow - directory.LastWriteTimeUtc < LeaselessGrace)
            {
                // Possibly another instance between creating the directory and taking its lease.
                return Outcome.InUse;
            }

            lease?.Dispose();
            lease = null;
            Directory.Delete(directory.FullName, recursive: true);
            return Outcome.Removed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Could not remove {directory.FullName}: {ex.Message}";
            return Outcome.Failed;
        }
        finally
        {
            lease?.Dispose();
        }
    }
}
