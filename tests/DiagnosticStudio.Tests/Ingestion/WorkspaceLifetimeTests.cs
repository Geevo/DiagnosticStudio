using DiagnosticStudio.Core.Ingestion;
using DiagnosticStudio.Ingestion;

namespace DiagnosticStudio.Tests.Ingestion;

public sealed class WorkspaceLifetimeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ds-life-" + Guid.NewGuid().ToString("N"));

    public WorkspaceLifetimeTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string NewName() => WorkspaceLocations.NewDirectoryName(Guid.NewGuid());

    /// <summary>A directory shaped like one left by a dead process: a lease file nobody holds, plus content.</summary>
    private string Orphan(bool withLease = true)
    {
        var dir = Path.Combine(_root, NewName());
        Directory.CreateDirectory(Path.Combine(dir, "a0001", "Logs"));
        File.WriteAllText(Path.Combine(dir, "a0001", "Logs", "agent.log"), "x");
        if (withLease)
        {
            File.WriteAllText(Path.Combine(dir, WorkspaceLocations.LeaseFileName), string.Empty);
        }

        return dir;
    }

    // ---- names ----

    [Theory]
    [InlineData("ws-0123456789abcdef0123456789abcdef", true)]
    [InlineData("ws-0123456789ABCDEF0123456789abcdef", false)]
    [InlineData("ws-123", false)]
    [InlineData("Documents", false)]
    [InlineData("ws-0123456789abcdef0123456789abcdef-extra", false)]
    public void Only_generated_workspace_names_are_recognised(string name, bool expected)
    {
        Assert.Equal(expected, WorkspaceLocations.IsWorkspaceDirectoryName(name));
    }

    [Fact]
    public void Generated_names_are_recognised()
    {
        Assert.True(WorkspaceLocations.IsWorkspaceDirectoryName(NewName()));
    }

    // ---- lease ----

    [Fact]
    public void A_lease_holds_an_exclusive_marker_and_dispose_removes_the_directory()
    {
        var dir = Path.Combine(_root, NewName());
        var lease = WorkspaceLease.Create(dir);
        File.WriteAllText(Path.Combine(dir, "extracted.txt"), "x");

        var leasePath = Path.Combine(dir, WorkspaceLocations.LeaseFileName);
        Assert.True(File.Exists(leasePath));
        Assert.Throws<IOException>(() =>
        {
            using var _ = new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });

        lease.Dispose();

        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void Disposing_a_lease_twice_is_harmless()
    {
        var lease = WorkspaceLease.Create(Path.Combine(_root, NewName()));

        lease.Dispose();
        lease.Dispose();
    }

    [Fact]
    public void Dispose_that_cannot_delete_leaves_the_directory_for_the_sweeper_without_throwing()
    {
        var dir = Path.Combine(_root, NewName());
        var lease = WorkspaceLease.Create(dir);
        var held = Path.Combine(dir, "open.log");
        File.WriteAllText(held, "x");
        using var open = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None);

        lease.Dispose(); // must not throw

        Assert.True(Directory.Exists(dir));
        open.Dispose();

        // Delete removed what it could, including the lease file, so the leftover is reclaimed by the age rule
        // on a later start rather than immediately.
        Assert.Equal(0, WorkspaceJanitor.SweepStale(_root).Removed);
        var result = WorkspaceJanitor.SweepStale(
            _root, utcNow: DateTime.UtcNow + WorkspaceJanitor.LeaselessGrace + TimeSpan.FromMinutes(1));
        Assert.Equal(1, result.Removed);
        Assert.False(Directory.Exists(dir));
    }

    // ---- sweep ----

    [Fact]
    public void Missing_root_is_not_an_error()
    {
        var result = WorkspaceJanitor.SweepStale(Path.Combine(_root, "does-not-exist"));

        Assert.Equal(WorkspaceSweepResult.None, result);
    }

    [Fact]
    public void Orphaned_workspaces_with_an_unheld_lease_are_removed()
    {
        var a = Orphan();
        var b = Orphan();

        var result = WorkspaceJanitor.SweepStale(_root);

        Assert.Equal(2, result.Removed);
        Assert.Equal(0, result.InUse);
        Assert.Equal(0, result.Failed);
        Assert.False(Directory.Exists(a));
        Assert.False(Directory.Exists(b));
    }

    [Fact]
    public void A_workspace_whose_lease_is_held_is_never_removed()
    {
        using var live = WorkspaceLease.Create(Path.Combine(_root, NewName()));
        File.WriteAllText(Path.Combine(live.WorkingDirectory, "keep.txt"), "x");
        var orphan = Orphan();

        var result = WorkspaceJanitor.SweepStale(_root);

        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.InUse);
        Assert.True(File.Exists(Path.Combine(live.WorkingDirectory, "keep.txt")));
        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public void Young_directories_without_a_lease_are_left_alone_because_another_instance_may_be_starting()
    {
        var young = Orphan(withLease: false);

        var result = WorkspaceJanitor.SweepStale(_root);

        Assert.Equal(0, result.Removed);
        Assert.Equal(1, result.InUse);
        Assert.True(Directory.Exists(young));
    }

    [Fact]
    public void Old_directories_without_a_lease_are_removed()
    {
        var old = Orphan(withLease: false);

        var result = WorkspaceJanitor.SweepStale(
            _root, utcNow: DateTime.UtcNow + WorkspaceJanitor.LeaselessGrace + TimeSpan.FromMinutes(1));

        Assert.Equal(1, result.Removed);
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void Anything_that_is_not_an_application_workspace_is_ignored()
    {
        var stranger = Path.Combine(_root, "MyDocuments");
        Directory.CreateDirectory(stranger);
        File.WriteAllText(Path.Combine(stranger, "important.txt"), "x");
        var lookalike = Path.Combine(_root, "ws-notahexid");
        Directory.CreateDirectory(lookalike);
        File.WriteAllText(Path.Combine(_root, "loose-file.txt"), "x");

        var result = WorkspaceJanitor.SweepStale(
            _root, utcNow: DateTime.UtcNow + TimeSpan.FromDays(30));

        Assert.Equal(0, result.Removed);
        Assert.True(File.Exists(Path.Combine(stranger, "important.txt")));
        Assert.True(Directory.Exists(lookalike));
        Assert.True(File.Exists(Path.Combine(_root, "loose-file.txt")));
    }

    [Fact]
    public void A_directory_that_cannot_be_removed_is_reported_and_the_sweep_continues()
    {
        var stuck = Orphan();
        var held = Path.Combine(stuck, "a0001", "Logs", "agent.log");
        using var open = new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None);
        var fine = Orphan();

        var result = WorkspaceJanitor.SweepStale(_root);

        Assert.Equal(1, result.Removed);
        Assert.Equal(1, result.Failed);
        Assert.Single(result.Errors);
        Assert.Contains(stuck, result.Errors[0]);
        Assert.False(Directory.Exists(fine));
    }

    [Fact]
    public async Task Sweep_honours_cancellation()
    {
        Orphan();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkspaceJanitor.SweepStaleAsync(_root, cts.Token));
    }

    // ---- integration with ingestion ----

    [Fact]
    public async Task An_ingested_workspace_is_protected_while_open_and_removed_on_dispose()
    {
        var zip = Path.Combine(_root, "bundle.zip");
        File.WriteAllBytes(zip, TestWorkspace.BuildZip(z => TestWorkspace.AddText(z, "Logs/a.log", "x")));
        var options = new IngestionOptions { WorkspaceRoot = Path.Combine(_root, "work") };

        var workspace = await TestWorkspace.CreateIngestor().IngestAsync(zip, options, null, CancellationToken.None);

        var sweep = WorkspaceJanitor.SweepStale(options.WorkspaceRoot, utcNow: DateTime.UtcNow.AddDays(30));
        Assert.Equal(0, sweep.Removed);
        Assert.Equal(1, sweep.InUse);
        Assert.True(Directory.Exists(workspace.WorkingDirectory));

        workspace.Dispose();

        Assert.False(Directory.Exists(workspace.WorkingDirectory));
        Assert.True(File.Exists(zip), "the original bundle must never be touched");
    }

    [Fact]
    public async Task A_failed_ingest_leaves_nothing_behind()
    {
        var work = Path.Combine(_root, "work");
        var options = new IngestionOptions { WorkspaceRoot = work };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var zip = Path.Combine(_root, "bundle.zip");
        File.WriteAllBytes(zip, TestWorkspace.BuildZip(z => TestWorkspace.AddText(z, "a.log", "x")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TestWorkspace.CreateIngestor().IngestAsync(zip, options, null, cts.Token));

        Assert.Empty(Directory.Exists(work) ? Directory.GetDirectories(work) : Array.Empty<string>());
    }
}
