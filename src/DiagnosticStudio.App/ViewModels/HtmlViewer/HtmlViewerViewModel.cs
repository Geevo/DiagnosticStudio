using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DiagnosticStudio.App.ViewModels.TextViewer;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Navigation;

namespace DiagnosticStudio.App.ViewModels.HtmlViewer;

/// <summary>
/// An HTML file drawn as a page, locked down so nothing in it can run or reach out, next to its source text. If the page
/// cannot be drawn (too large, or the browser component is not installed) the source is shown and the reason is given.
/// </summary>
public sealed partial class HtmlViewerViewModel : ObservableObject, ILocationNavigable, ISearchHighlightable
{
    public const int PageTab = 0;
    public const int SourceTab = 1;

    public const string SandboxNote =
        "Shown with scripts, links, forms and network access blocked. Nothing in the file runs or is fetched.";

    public const string ScriptsNote =
        "The page's own scripts are running. Links, forms, frames and network access are still blocked: a script can change the page but cannot send or fetch anything.";

    public HtmlViewerViewModel(HtmlDocument document)
    {
        Document = document;
        Raw = new TextViewerViewModel(document.RawSource);
        Markup = document.Markup;
        _renderError = document.RenderNote;
        _selectedTabIndex = CanRender ? PageTab : SourceTab;
        InfoText = string.Create(
            CultureInfo.CurrentCulture,
            $"HTML · {document.RawSource.LineCount:N0} lines · {document.RawSource.ByteLength:N0} bytes · {document.RawSource.EncodingName}");
    }

    public HtmlDocument Document { get; }

    /// <summary>The complete file as text; always available.</summary>
    public TextViewerViewModel Raw { get; }

    /// <summary>The page to draw, or <c>null</c> when there is none to draw.</summary>
    public string? Markup { get; }

    public string InfoText { get; }

    /// <summary>The page can be drawn: there is markup and nothing has gone wrong.</summary>
    public bool CanRender => Markup is not null && RenderError is null;

    /// <summary>Why the page is not drawn, when it is not (too large, the component is missing, it failed).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRender), nameof(HasRenderError))]
    private string? _renderError;

    public bool HasRenderError => RenderError is not null;

    [ObservableProperty]
    private int _selectedTabIndex;

    /// <summary>
    /// Let the page run its own scripts (some pages draw nothing without them). Off for every page when it opens, and not
    /// remembered: it is a choice about this page. The network stays blocked either way.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Note))]
    private bool _allowScripts;

    /// <summary>What is blocked for the page as it is shown now.</summary>
    public string Note => AllowScripts ? ScriptsNote : SandboxNote;

    /// <summary>Called by the view when the browser component cannot draw the page; the source is shown instead.</summary>
    public void ReportRenderFailure(string message)
    {
        RenderError = message;
        SelectedTabIndex = SourceTab;
    }

    public bool NavigateTo(DiagnosticLocation location)
    {
        if (location.Kind != DiagnosticLocationKind.Line || location.NumericPosition is not { } line)
        {
            return false;
        }

        // A line is a place in the source text.
        SelectedTabIndex = SourceTab;
        Raw.NavigateToLine((int)Math.Min(line, int.MaxValue));
        return true;
    }

    public void Highlight(SearchHighlight highlight) => Raw.Highlight(highlight);
}
