using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Parsers.Evtx;
using Xunit.Abstractions;

namespace DiagnosticStudio.Tests.Evtx;

/// <summary>
/// Cross-checks the EVTX parser against the operating system's own reader on real logs. The logs are exported
/// from this machine with <c>wevtutil</c> at test time (nothing is checked in). When a log cannot be exported
/// here the test is reported as skipped, so the suite still passes on machines without it and a skip is visible.
/// </summary>
[Trait("Category", "Oracle")]
public sealed class EvtxOracleTests : IDisposable
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ds-evtx-oracle-" + Guid.NewGuid().ToString("N"));

    public EvtxOracleTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    public static IEnumerable<object[]> Logs() => new[]
    {
        new object[] { "Application" },
        new object[] { "System" },
        new object[] { "Setup" },
        new object[] { "Microsoft-Windows-PowerShell/Operational" },
        new object[] { "Microsoft-Windows-Kernel-PnP/Configuration" },
        new object[] { "Microsoft-Windows-Windows Defender/Operational" },
    };

    private string? Export(string logName)
    {
        var file = Path.Combine(_dir, logName.Replace('/', '_').Replace(' ', '_') + ".evtx");
        try
        {
            var psi = new ProcessStartInfo("wevtutil", $"epl \"{logName}\" \"{file}\" /ow:true")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            using var process = Process.Start(psi)!;
            process.WaitForExit(60_000);
            return process.ExitCode == 0 && File.Exists(file) ? file : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    [SkippableTheory]
    [MemberData(nameof(Logs))]
    public async Task Parsed_events_match_the_operating_systems_rendering(string logName)
    {
        var path = Export(logName);
        Skip.If(path is null, $"could not export '{logName}' on this machine.");

        var file = await EvtxFileTestAccess.Open(path);
        var sw = Stopwatch.StartNew();
        var mismatches = new List<string>();
        var compared = 0;
        var mismatchCount = 0;
        var osCount = 0;

        using (var reader = new EventLogReader(new EventLogQuery(path, PathType.FilePath)))
        {
            for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
            {
                using (record)
                {
                    osCount++;
                    var index = file.FindByRecordId(record.RecordId ?? -1);
                    if (index < 0)
                    {
                        mismatchCount++;
                        if (mismatches.Count < 6)
                        {
                            mismatches.Add($"record {record.RecordId} missing from parsed index");
                        }

                        continue;
                    }

                    var mine = XDocument.Parse(file.ReadDetail(index).Xml);
                    var theirs = XDocument.Parse(record.ToXml());
                    var diff = Diff(Canonical(theirs.Root!), Canonical(mine.Root!), "Event");
                    compared++;
                    if (diff is not null)
                    {
                        mismatchCount++;
                        if (mismatches.Count < 6)
                        {
                            mismatches.Add($"record {record.RecordId} ({record.ProviderName}/{record.Id}): {diff}");
                        }
                    }
                }
            }
        }

        var summary = $"{logName}: OS read {osCount:N0} events, parser indexed {file.Count:N0}, compared {compared:N0} in {sw.ElapsedMilliseconds:N0} ms; {file.TotalIssueCount} parser issues; {mismatchCount:N0} mismatches.";
        _output.WriteLine(summary);
        foreach (var m in mismatches)
        {
            _output.WriteLine("  MISMATCH " + m);
        }

        Assert.Equal(osCount, file.Count);
        Assert.True(mismatchCount == 0, summary + Environment.NewLine + string.Join(Environment.NewLine, mismatches));
    }

    [SkippableTheory]
    [InlineData("System")]
    [InlineData("Application")]
    public async Task Messages_agree_with_the_operating_systems_formatting_where_a_message_is_produced(string logName)
    {
        var path = Export(logName);
        Skip.If(path is null, $"could not export '{logName}' on this machine.");

        using var formatter = new DiagnosticStudio.Parsers.ProviderMessageFormatter();
        var file = await DiagnosticStudio.Parsers.Evtx.EvtxFile.OpenAsync(path, formatter, CancellationToken.None);
        int produced = 0, same = 0, noneButOs = 0, total = 0;
        var differences = new List<string>();

        using var reader = new EventLogReader(new EventLogQuery(path, PathType.FilePath));
        for (var record = reader.ReadEvent(); record is not null && total < 4000; record = reader.ReadEvent())
        {
            using (record)
            {
                total++;
                var index = file.FindByRecordId(record.RecordId ?? -1);
                var mine = file.ReadDetail(index).Message;
                string? theirs;
                try
                {
                    theirs = record.FormatDescription()?.TrimEnd();
                }
                catch (EventLogException)
                {
                    theirs = null;
                }

                if (mine is null)
                {
                    if (!string.IsNullOrEmpty(theirs))
                    {
                        noneButOs++;
                    }

                    continue;
                }

                produced++;
                if (Normalize(mine) == Normalize(theirs))
                {
                    same++;
                }
                else if (differences.Count < 6)
                {
                    differences.Add($"{record.ProviderName}/{record.Id}: OS=[{theirs}] mine=[{mine}]");
                }
            }
        }

        _output.WriteLine($"{logName}: of {total} events, mine produced {produced}, identical to OS {same}, OS-only {noneButOs}.");
        foreach (var d in differences)
        {
            _output.WriteLine("  DIFF " + d);
        }

        // Coverage is asserted: anywhere the OS can produce a message, so can we. How closely the text matches is only
        // reported, because it depends on which providers this machine's log holds. Where a manifest maps numbers to
        // names (STATUS_SUCCESS, flag names) the formatter shows the raw number, and logs heavy in such events differ.
        Assert.True(noneButOs <= total / 100, $"{noneButOs} of {total} events had an OS message but none from the formatter");
    }

    [SkippableFact]
    public async Task Message_only_text_is_found_when_messages_are_included_on_a_real_log()
    {
        var path = Export("System");
        Skip.If(path is null, "could not export 'System' on this machine.");

        using var formatter = new DiagnosticStudio.Parsers.ProviderMessageFormatter();
        var file = await DiagnosticStudio.Parsers.Evtx.EvtxFile.OpenAsync(path, formatter, CancellationToken.None);

        // Find an event whose rendered message has a phrase that is not in its provider name or data values.
        string? phrase = null;
        for (var i = 0; i < file.Count && phrase is null; i++)
        {
            var detail = file.ReadDetail(i);
            var message = detail.Message;
            if (message is null)
            {
                continue;
            }

            var dataText = file.ReadSearchText(i, includeMessage: false);
            foreach (var candidate in message.Split(new[] { (char)46, (char)44, (char)58, (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()))
            {
                if (candidate.Length >= 18 && !dataText.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    phrase = candidate;
                    break;
                }
            }
        }

        Skip.If(phrase is null, "no event with message-only text found.");

        var sw = Stopwatch.StartNew();
        var without = DiagnosticStudio.Search.EventLogFilter.Apply(file, new DiagnosticStudio.Search.EventFilterCriteria { Text = phrase }, CancellationToken.None)!;
        var tWithout = sw.ElapsedMilliseconds;
        sw.Restart();
        var with = DiagnosticStudio.Search.EventLogFilter.Apply(file, new DiagnosticStudio.Search.EventFilterCriteria { Text = phrase, IncludeMessage = true }, CancellationToken.None)!;
        var tWith = sw.ElapsedMilliseconds;

        _output.WriteLine($"phrase [{phrase}] over {file.Count:N0} events: data only {without.Length} matches in {tWithout} ms; with messages {with.Length} matches in {tWith} ms.");
        Assert.Empty(without);
        Assert.NotEmpty(with);
    }

    private static string Normalize(string? text)
    {
        // The OS formats typed timestamps in its own style with direction marks; compare everything else.
        var cleaned = (text ?? string.Empty).Replace("‎", string.Empty).Replace("‏", string.Empty);
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\d{4}.\d{2}.\d{2}T[\d:.]+Z", "<time>");

        // Typed numbers are rendered by the OS using the manifest's out-types (hex, error text); compare structure only.
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"0x[0-9a-fA-F]+|\d+", "#");
        return System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    // ---- canonical comparison of two renderings ----

    private static string Canonical(XElement e)
    {
        var attrs = e.Attributes()
            .Where(a => !a.IsNamespaceDeclaration)
            .OrderBy(a => a.Name.LocalName, StringComparer.Ordinal)
            .Select(a => $" {a.Name.LocalName}='{a.Value}'");
        var children = e.Nodes().Select(n => n switch
        {
            XElement c => Canonical(c),
            XText t => t.Value.Trim().Length == 0 ? string.Empty : "T:" + t.Value,
            _ => string.Empty,
        }).Where(s => s.Length > 0);
        return $"<{e.Name.LocalName}{string.Concat(attrs)}>{string.Concat(children)}</{e.Name.LocalName}>";
    }

    private static string? Diff(string expected, string actual, string where)
    {
        if (expected == actual)
        {
            return null;
        }

        var i = 0;
        while (i < expected.Length && i < actual.Length && expected[i] == actual[i])
        {
            i++;
        }

        var from = Math.Max(0, i - 60);
        return $"{where}: OS …{expected.Substring(from, Math.Min(140, expected.Length - from))}…  vs  parser …{actual.Substring(from, Math.Min(140, actual.Length - from))}…";
    }
}

/// <summary>EvtxFile.Open is internal-friendly via its public async entry; this keeps the test free of formatter plumbing.</summary>
internal static class EvtxFileTestAccess
{
    public static Task<DiagnosticStudio.Parsers.Evtx.EvtxFile> Open(string path) =>
        DiagnosticStudio.Parsers.Evtx.EvtxFile.OpenAsync(path, null, CancellationToken.None);
}
