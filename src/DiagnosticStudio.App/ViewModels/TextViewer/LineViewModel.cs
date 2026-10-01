using CommunityToolkit.Mvvm.ComponentModel;
using DiagnosticStudio.Core.Documents;
using DiagnosticStudio.Core.Parsing;

namespace DiagnosticStudio.App.ViewModels.TextViewer;

/// <summary>One displayed line. Created only for pages that have actually been scrolled into view.</summary>
public sealed partial class LineViewModel : ObservableObject
{
    public LineViewModel(int lineNumber, string text)
    {
        LineNumber = lineNumber;
        Text = text;
        Info = LogLineAnalyzer.Analyze(text);
    }

    /// <summary>One-based, matching <see cref="DiagnosticStudio.Core.Navigation.DiagnosticLocation"/> line numbers.</summary>
    public int LineNumber { get; }

    public string Text { get; }

    public LogLineInfo Info { get; }

    public LogSeverity Severity => Info.Severity;

    [ObservableProperty]
    private bool _isMatch;
}
