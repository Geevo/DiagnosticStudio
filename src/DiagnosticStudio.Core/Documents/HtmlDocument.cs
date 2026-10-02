namespace DiagnosticStudio.Core.Documents;

/// <summary>
/// An HTML file. The text is always available; the markup is kept for rendering only when it is small enough to render
/// safely, and says why not when it is not.
/// </summary>
public sealed record HtmlDocument : DiagnosticDocument
{
    /// <summary>Largest file that is rendered. A browser control takes its page as one string with a size limit of its own.</summary>
    public const long MaxRenderBytes = 1_800_000;

    /// <summary>The complete file as text; always available.</summary>
    public required ITextLineSource RawSource { get; init; }

    /// <summary>The page, or <c>null</c> when it is too large to render (see <see cref="RenderNote"/>).</summary>
    public string? Markup { get; init; }

    /// <summary>Why the page is not rendered, when it is not.</summary>
    public string? RenderNote { get; init; }
}
