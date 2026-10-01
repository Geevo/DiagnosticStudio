using System.Diagnostics;
using System.Text;
using DiagnosticStudio.Core.Archives;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Ingestion;
using DiagnosticStudio.Ingestion;

namespace DiagnosticStudio.Tests.Ingestion;

public sealed class CabArchiveProviderTests : IDisposable
{
    private readonly TestWorkspace _ws = new();
    private readonly CabArchiveProvider _provider = new();

    public void Dispose() => _ws.Dispose();

    private sealed record Outcome(IReadOnlyList<ExtractedArchiveEntry> Entries, List<IngestionIssue> Issues, string Destination);

    private DiagnosticArtifact CabArtifact(byte[] bytes, string name = "mdmlogs.cab")
    {
        var path = _ws.PathFor(name);
        File.WriteAllBytes(path, bytes);
        return new DiagnosticArtifact
        {
            Id = Guid.NewGuid(),
            Name = name,
            OriginalPath = name,
            ExtractedPath = path,
            Provenance = new[] { "Bundle.zip", name },
            ArtifactType = ArtifactType.Archive,
            Subtype = ArchiveFormats.Cab,
        };
    }

    private async Task<Outcome> Extract(byte[] cab, ExtractionLimits? limits = null, CancellationToken ct = default)
    {
        var artifact = CabArtifact(cab);
        var issues = new List<IngestionIssue>();
        var destination = _ws.PathFor("out");
        var context = new ArchiveExtractionContext(new ExtractionBudget(limits ?? new ExtractionLimits()), 1, issues.Add);
        var entries = await _provider.ExtractAsync(artifact, destination, context, ct);
        return new Outcome(entries, issues, destination);
    }

    private static string Text(ExtractedArchiveEntry entry) => File.ReadAllText(entry.ExtractedPath);

    // ---- applicability ----

    [Fact]
    public void Only_cabinet_archives_with_a_working_copy_are_opened()
    {
        var cab = CabArtifact(new CabBuilder().Add("a.txt", "x").Build());

        Assert.True(_provider.CanOpen(cab));
        Assert.False(_provider.CanOpen(cab with { Subtype = ArchiveFormats.Zip }));
        Assert.False(_provider.CanOpen(cab with { ArtifactType = ArtifactType.TextLog }));
        Assert.False(_provider.CanOpen(cab with { ExtractedPath = null }));
    }

    // ---- extraction ----

    [Fact]
    public async Task Files_and_folders_are_extracted_with_their_contents()
    {
        var cab = new CabBuilder()
            .Add(@"Logs\agent.log", "2026-07-23 14:12:00 ERROR failed")
            .Add("Events/readme.txt", "hello")
            .Add("top.txt", "top")
            .Build();

        var outcome = await Extract(cab);

        Assert.Empty(outcome.Issues);
        Assert.Equal(new[] { "Logs/agent.log", "Events/readme.txt", "top.txt" }, outcome.Entries.Select(e => e.EntryPath));
        Assert.Equal("2026-07-23 14:12:00 ERROR failed", Text(outcome.Entries[0]));
        Assert.Equal("hello", Text(outcome.Entries[1]));
        Assert.All(outcome.Entries, e => Assert.StartsWith(outcome.Destination, e.ExtractedPath));
        Assert.Equal(new FileInfo(outcome.Entries[0].ExtractedPath).Length, outcome.Entries[0].Size);
    }

    [Fact]
    public async Task Content_larger_than_one_data_block_comes_out_intact()
    {
        var data = new byte[200_000];
        new Random(42).NextBytes(data);

        var outcome = await Extract(new CabBuilder().Add("big.bin", data).Add("after.txt", "tail").Build());

        Assert.Equal(data, await File.ReadAllBytesAsync(outcome.Entries[0].ExtractedPath));
        Assert.Equal("tail", Text(outcome.Entries[1]));
    }

    [Fact]
    public async Task Utf8_names_are_decoded()
    {
        var outcome = await Extract(new CabBuilder().Add("Logs/日本語.log", Encoding.UTF8.GetBytes("x"), utf8Name: true).Build());

        Assert.Equal("Logs/日本語.log", Assert.Single(outcome.Entries).EntryPath);
        Assert.True(File.Exists(outcome.Entries[0].ExtractedPath));
    }

    [Fact]
    public async Task Duplicate_names_are_kept_side_by_side()
    {
        var outcome = await Extract(new CabBuilder().Add("a.txt", "one").Add("A.txt", "two").Build());

        Assert.Equal(2, outcome.Entries.Count);
        Assert.NotEqual(outcome.Entries[0].ExtractedPath, outcome.Entries[1].ExtractedPath);
        Assert.Equal(new[] { "one", "two" }, outcome.Entries.Select(Text));
    }

    // ---- hostile content ----

    [Theory]
    [InlineData(@"..\..\evil.txt")]
    [InlineData("../evil.txt")]
    [InlineData(@"C:\Windows\evil.txt")]
    [InlineData(@"\\server\share\evil.txt")]
    [InlineData("/etc/evil.txt")]
    [InlineData(@"Logs\..\..\evil.txt")]
    public async Task Names_that_escape_the_destination_are_skipped_and_reported(string name)
    {
        var cab = new CabBuilder().Add(name, "pwned").Add("safe.txt", "fine").Build();

        var outcome = await Extract(cab);

        Assert.Equal("safe.txt", Assert.Single(outcome.Entries).EntryPath);
        var issue = Assert.Single(outcome.Issues);
        Assert.Equal(IngestionIssueSeverity.Warning, issue.Severity);
        Assert.Equal("CAB", issue.Component);
        Assert.Contains(name.Length > 0 ? "→" : string.Empty, issue.Subject);
        Assert.Empty(Directory.EnumerateFiles(_ws.Root, "evil.txt", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_file_declaring_more_than_the_single_file_limit_is_skipped_and_others_continue()
    {
        var cab = new CabBuilder().Add("big.bin", new byte[5000]).Add("small.txt", "ok").Build();

        var outcome = await Extract(cab, new ExtractionLimits { MaxSingleFileBytes = 1000 });

        Assert.Equal("small.txt", Assert.Single(outcome.Entries).EntryPath);
        Assert.Contains(outcome.Issues, i => i.Message.Contains("single-file limit"));
        Assert.False(File.Exists(Path.Combine(outcome.Destination, "big.bin")));
    }

    [Fact]
    public async Task The_total_size_limit_stops_extraction_and_leaves_no_partial_file()
    {
        var cab = new CabBuilder().Add("a.bin", new byte[3000]).Add("b.bin", new byte[3000]).Add("c.bin", new byte[3000]).Build();

        var outcome = await Extract(cab, new ExtractionLimits { MaxTotalExtractedBytes = 4000 });

        Assert.Equal("a.bin", Assert.Single(outcome.Entries).EntryPath);
        Assert.Contains(outcome.Issues, i => i.Severity == IngestionIssueSeverity.Error && i.Message.Contains("Extraction stopped"));
        Assert.Equal(new[] { "a.bin" }, Directory.GetFiles(outcome.Destination, "*", SearchOption.AllDirectories).Select(Path.GetFileName));
    }

    [Fact]
    public async Task The_file_count_limit_stops_extraction()
    {
        var builder = new CabBuilder();
        for (var i = 0; i < 6; i++)
        {
            builder.Add($"f{i}.txt", "x");
        }

        var outcome = await Extract(builder.Build(), new ExtractionLimits { MaxFileCount = 3 });

        Assert.Equal(3, outcome.Entries.Count);
        Assert.Contains(outcome.Issues, i => i.Severity == IngestionIssueSeverity.Error && i.Message.Contains("File count limit"));
    }

    [Fact]
    public async Task A_forged_size_cannot_make_the_extractor_write_more_than_the_cabinet_holds()
    {
        // Declares 50 bytes but stores 3. It may be rejected as damaged or extracted short; either way nothing
        // beyond what the cabinet really holds may appear, and no half-written file may remain.
        var cab = new CabBuilder().Add("liar.txt", Encoding.ASCII.GetBytes("abc"), declaredSize: 50).Build();

        try
        {
            var outcome = await Extract(cab);
            foreach (var entry in outcome.Entries)
            {
                Assert.True(new FileInfo(entry.ExtractedPath).Length <= 3);
            }
        }
        catch (InvalidDataException)
        {
        }

        var written = Directory.Exists(_ws.PathFor("out"))
            ? Directory.GetFiles(_ws.PathFor("out"), "*", SearchOption.AllDirectories)
            : Array.Empty<string>();
        Assert.All(written, f => Assert.True(new FileInfo(f).Length <= 3));
    }

    // ---- malformed input ----

    [Fact]
    public async Task A_file_that_is_not_a_cabinet_is_rejected_with_a_clear_error()
    {
        var artifact = CabArtifact(Encoding.ASCII.GetBytes("this is definitely not a cabinet file at all"));
        var context = new ArchiveExtractionContext(new ExtractionBudget(new ExtractionLimits()), 1, _ => { });

        await Assert.ThrowsAsync<InvalidDataException>(
            () => _provider.ExtractAsync(artifact, _ws.PathFor("out"), context, CancellationToken.None));
    }

    [Fact]
    public async Task A_truncated_cabinet_is_reported_and_keeps_whatever_was_complete()
    {
        var cab = new CabBuilder().Add("a.txt", "first file").Add("b.txt", new string('x', 100_000)).Build();
        var truncated = cab[..(cab.Length - 60_000)];

        try
        {
            var outcome = await Extract(truncated);
            Assert.Contains(outcome.Issues, i => i.Severity == IngestionIssueSeverity.Error);
        }
        catch (InvalidDataException)
        {
            // Also acceptable: nothing was recoverable, so the whole archive is reported as unreadable.
        }

        Assert.DoesNotContain(
            Directory.EnumerateFiles(_ws.Root, "*", SearchOption.AllDirectories),
            f => f.Contains(Path.Combine("out", "b.txt")));
    }

    [Fact]
    public async Task A_cabinet_that_continues_in_another_is_extracted_as_far_as_possible_with_a_warning()
    {
        var cab = new CabBuilder { HasNext = true }.Add("a.txt", "complete").Build();

        var outcome = await Extract(cab);

        Assert.Contains(outcome.Issues, i => i.Message.Contains("multi-cabinet"));
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Extract(new CabBuilder().Add("a.txt", "x").Build(), ct: cts.Token));
    }

    // ---- real compressed cabinets, built by the Windows makecab tool ----

    private static string? FindMakeCab()
    {
        var path = Path.Combine(Environment.SystemDirectory, "makecab.exe");
        return File.Exists(path) ? path : null;
    }

    private async Task<byte[]?> MakeCab(string compression, params (string Folder, string Name, byte[] Data)[] files)
    {
        if (FindMakeCab() is not { } makecab)
        {
            return null; // not a Windows machine with makecab; the store-only tests above still run
        }

        var source = _ws.PathFor("src-" + Guid.NewGuid().ToString("N"));
        var output = _ws.PathFor("made-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(output);
        var ddf = new StringBuilder()
            .AppendLine(".OPTION EXPLICIT")
            .AppendLine(".Set CabinetNameTemplate=test.cab")
            .AppendLine(".Set DiskDirectoryTemplate=" + output)
            .AppendLine(".Set CompressionType=" + compression)
            .AppendLine(".Set CompressionMemory=15")
            .AppendLine(".Set Cabinet=on")
            .AppendLine(".Set Compress=on")
            .AppendLine(".Set MaxDiskSize=0");
        foreach (var (folder, name, data) in files)
        {
            var path = Path.Combine(source, Guid.NewGuid().ToString("N") + "-" + name);
            await File.WriteAllBytesAsync(path, data);
            ddf.AppendLine(".Set DestinationDir=" + folder);
            ddf.AppendLine($"\"{path}\" \"{name}\"");
        }

        var ddfPath = Path.Combine(source, "make.ddf");
        await File.WriteAllTextAsync(ddfPath, ddf.ToString());

        using var process = Process.Start(new ProcessStartInfo(makecab, $"/F \"{ddfPath}\"")
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        // makecab writes a lot of progress text; an undrained pipe would block it forever.
        var drained = Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
        await process.WaitForExitAsync();
        await drained;
        Assert.Equal(0, process.ExitCode);
        return await File.ReadAllBytesAsync(Path.Combine(output, "test.cab"));
    }

    private static byte[] Compressible(int size, string seed)
    {
        var text = new StringBuilder();
        var i = 0;
        while (text.Length < size)
        {
            text.Append(seed).Append(' ').Append(i++ % 97).Append(" processed item status=ok\r\n");
        }

        return Encoding.ASCII.GetBytes(text.ToString(0, size));
    }

    [Theory]
    [InlineData("MSZIP")]
    [InlineData("LZX")]
    public async Task Real_compressed_cabinets_extract_byte_for_byte(string compression)
    {
        var big = Compressible(400_000, "agent");
        var small = Compressible(2_000, "cm");
        var cab = await MakeCab(compression, ("Logs", "agent.log", big), ("Logs", "cm.log", small), ("Events", "notes.txt", Encoding.ASCII.GetBytes("hello")));
        if (cab is null)
        {
            return;
        }

        Assert.True(cab.Length < big.Length / 2, "the cabinet should really be compressed");
        var outcome = await Extract(cab);

        Assert.Empty(outcome.Issues);
        Assert.Equal(3, outcome.Entries.Count);
        var agent = outcome.Entries.Single(e => e.EntryPath.EndsWith("agent.log", StringComparison.Ordinal));
        Assert.Equal("Logs/agent.log", agent.EntryPath);
        Assert.Equal(big, await File.ReadAllBytesAsync(agent.ExtractedPath));
        Assert.Equal(small, await File.ReadAllBytesAsync(outcome.Entries.Single(e => e.EntryPath == "Logs/cm.log").ExtractedPath));
        Assert.Equal("hello", Text(outcome.Entries.Single(e => e.EntryPath == "Events/notes.txt")));
    }

    [Fact]
    public async Task A_compressed_bomb_is_stopped_by_the_total_size_limit()
    {
        var zeros = new byte[20_000_000];
        var cab = await MakeCab("LZX", ("", "zeros.bin", zeros));
        if (cab is null)
        {
            return;
        }

        Assert.True(cab.Length < 100_000, "the cabinet should be tiny compared to its content");
        var outcome = await Extract(cab, new ExtractionLimits { MaxTotalExtractedBytes = 1_000_000, MaxSingleFileBytes = 100_000_000 });

        Assert.Empty(outcome.Entries);
        Assert.Contains(outcome.Issues, i => i.Severity == IngestionIssueSeverity.Error && i.Message.Contains("Extraction stopped"));
        Assert.Empty(Directory.GetFiles(outcome.Destination, "*", SearchOption.AllDirectories));
    }
}
