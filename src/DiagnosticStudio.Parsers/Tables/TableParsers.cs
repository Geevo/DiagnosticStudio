using System.Text;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Parsing;

namespace DiagnosticStudio.Parsers.Tables;

/// <summary>
/// Opens CMTrace-format logs as a table of records next to their raw text. Whether a file is one is decided by what is
/// in it (the record markers), not by its name, so logs from any agent that writes the format are recognised. Anything
/// else falls through to the plain text parser.
/// </summary>
public sealed class CmTraceParser : IDiagnosticParser
{
    private const int SniffBytes = 64 * 1024;

    // Message text can be long; a record is read whole, so the per-line cap of the raw view does not apply here.
    private const int ParseMaxLineChars = 16_000_000;

    public bool CanHandle(DiagnosticArtifact artifact) =>
        artifact.ArtifactType is ArtifactType.TextLog or ArtifactType.Unknown
        && artifact.ExtractedPath is { } path
        && LooksLikeCmTrace(path);

    public async Task<DiagnosticDocument> ParseAsync(DiagnosticArtifact artifact, CancellationToken cancellationToken)
    {
        var path = artifact.ExtractedPath!;
        var raw = await IndexedTextFile.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var source = await IndexedTextFile.OpenAsync(path, cancellationToken, maxLineChars: ParseMaxLineChars).ConfigureAwait(false);
        var table = await Task.Run(() => CmTraceTable.Build(source, cancellationToken), cancellationToken).ConfigureAwait(false);

        return new TableDocument
        {
            Artifact = artifact,
            Format = TableFormat.CmTrace,
            Table = table,
            RawSource = raw,
            UnreadLines = table.UnreadLines,
            FirstUnreadLine = table.FirstUnreadLine,
        };
    }

    internal static bool LooksLikeCmTrace(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[SniffBytes];
            var read = stream.Read(buffer, 0, buffer.Length);
            return read > 0 && CmTraceTable.LooksLikeCmTrace(Decode(buffer, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Decode(byte[] bytes, int length)
    {
        if (length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, length - 2);
        }

        if (length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, length - 2);
        }

        return Encoding.UTF8.GetString(bytes, 0, length);
    }
}

/// <summary>Opens delimited text (CSV, TSV and the like) as a table next to its raw text.</summary>
public sealed class CsvParser : IDiagnosticParser
{
    private const int ParseMaxLineChars = 16_000_000;

    public bool CanHandle(DiagnosticArtifact artifact) =>
        artifact.ArtifactType == ArtifactType.Csv && artifact.ExtractedPath is not null;

    public async Task<DiagnosticDocument> ParseAsync(DiagnosticArtifact artifact, CancellationToken cancellationToken)
    {
        var path = artifact.ExtractedPath!;
        var raw = await IndexedTextFile.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        var source = await IndexedTextFile.OpenAsync(path, cancellationToken, maxLineChars: ParseMaxLineChars).ConfigureAwait(false);
        var table = await Task.Run(() => CsvTable.Build(source, cancellationToken), cancellationToken).ConfigureAwait(false);

        var note = $"Delimiter: {table.DelimiterName}. " + (table.HasHeader ? "The first row is taken as the column names." : "No header row was found, so the columns are numbered.");
        return new TableDocument
        {
            Artifact = artifact,
            Format = TableFormat.Csv,
            Table = table,
            RawSource = raw,
            UnreadLines = table.UnreadLines,
            FirstUnreadLine = table.FirstUnreadLine,
            Note = note,
        };
    }
}
