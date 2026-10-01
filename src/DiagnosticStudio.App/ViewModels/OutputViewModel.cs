using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DiagnosticStudio.App.ViewModels;

public enum OutputSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record OutputEntry(DateTimeOffset Time, OutputSeverity Severity, string Source, string Message);

/// <summary>Sink for ingestion, parser and application diagnostics.</summary>
public interface IOutputLog
{
    void Write(OutputSeverity severity, string source, string message);
}

public sealed partial class OutputViewModel : ObservableObject, IOutputLog
{
    public ObservableCollection<OutputEntry> Entries { get; } = new();

    public void Write(OutputSeverity severity, string source, string message) =>
        Entries.Add(new OutputEntry(DateTimeOffset.Now, severity, source, message));

    [RelayCommand]
    private void Clear() => Entries.Clear();
}
