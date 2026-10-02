using System.Globalization;
using System.Text;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Parsing;

namespace DiagnosticStudio.Parsers;

/// <summary>
/// Opens HTML files as a page to render and as source text. Nothing in the file is executed or fetched here; this only
/// reads it. Files too large to render safely open as source with a note.
/// </summary>
public sealed class HtmlFileParser : IDiagnosticParser
{
    public bool CanHandle(DiagnosticArtifact artifact) =>
        artifact.ArtifactType == ArtifactType.Html && artifact.ExtractedPath is not null;

    public async Task<DiagnosticDocument> ParseAsync(DiagnosticArtifact artifact, CancellationToken cancellationToken)
    {
        var path = artifact.ExtractedPath!;
        var raw = await IndexedTextFile.OpenAsync(path, cancellationToken).ConfigureAwait(false);

        if (raw.ByteLength > HtmlDocument.MaxRenderBytes)
        {
            return new HtmlDocument
            {
                Artifact = artifact,
                RawSource = raw,
                RenderNote = string.Create(
                    CultureInfo.CurrentCulture,
                    $"This file is {raw.ByteLength / 1_000_000.0:0.0} MB, larger than the {HtmlDocument.MaxRenderBytes / 1_000_000.0:0.0} MB that is rendered. Its source is shown."),
            };
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return new HtmlDocument { Artifact = artifact, RawSource = raw, Markup = Decode(bytes) };
    }

    /// <summary>Byte-order mark first; otherwise UTF-8 if the bytes are valid UTF-8; otherwise Latin-1, which never fails.</summary>
    internal static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }
}
