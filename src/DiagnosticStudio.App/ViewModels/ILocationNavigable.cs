using DiagnosticStudio.Core.Navigation;

namespace DiagnosticStudio.App.ViewModels;

/// <summary>A document viewer that can move to an exact <see cref="DiagnosticLocation"/> inside its artifact.</summary>
public interface ILocationNavigable
{
    /// <summary>Navigates to <paramref name="location"/>. Returns <c>false</c> when the viewer cannot resolve it.</summary>
    bool NavigateTo(DiagnosticLocation location);
}

/// <summary>A viewer that knows which place in its artifact the user is looking at (the selected line or event).</summary>
public interface ICurrentPosition
{
    /// <summary>The selected place as a location in <paramref name="artifactId"/>, or <c>null</c> when nothing is selected.</summary>
    DiagnosticLocation? CurrentPosition(Guid artifactId);
}

/// <summary>Query text to highlight in a viewer after navigating to a search hit.</summary>
public sealed record SearchHighlight(string Text, bool MatchCase);

/// <summary>A viewer that can mark occurrences of a search query in its content.</summary>
public interface ISearchHighlightable
{
    void Highlight(SearchHighlight highlight);
}
