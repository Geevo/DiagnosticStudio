namespace DiagnosticStudio.Core.Navigation;

public interface INavigationHistory
{
    DiagnosticLocation? Current { get; }
    bool CanGoBack { get; }
    bool CanGoForward { get; }

    event EventHandler? Changed;

    /// <summary>Records a navigation. Discards any forward entries; repeating the current location is ignored.</summary>
    void Record(DiagnosticLocation location);

    /// <summary>Moves back and returns the location to show, or <c>null</c> when there is nothing earlier.</summary>
    DiagnosticLocation? GoBack();

    DiagnosticLocation? GoForward();

    void Clear();
}

/// <summary>Linear back/forward history of <see cref="DiagnosticLocation"/>s, bounded in size.</summary>
public sealed class NavigationHistory : INavigationHistory
{
    private readonly List<DiagnosticLocation> _entries = new();
    private readonly int _capacity;
    private int _index = -1;

    public NavigationHistory(int capacity = 200)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    public event EventHandler? Changed;

    public DiagnosticLocation? Current => _index >= 0 ? _entries[_index] : null;
    public bool CanGoBack => _index > 0;
    public bool CanGoForward => _index >= 0 && _index < _entries.Count - 1;

    public void Record(DiagnosticLocation location)
    {
        if (Current == location)
        {
            return;
        }

        if (_index < _entries.Count - 1)
        {
            _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        }

        _entries.Add(location);
        if (_entries.Count > _capacity)
        {
            _entries.RemoveAt(0);
        }

        _index = _entries.Count - 1;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public DiagnosticLocation? GoBack()
    {
        if (!CanGoBack)
        {
            return null;
        }

        _index--;
        Changed?.Invoke(this, EventArgs.Empty);
        return Current;
    }

    public DiagnosticLocation? GoForward()
    {
        if (!CanGoForward)
        {
            return null;
        }

        _index++;
        Changed?.Invoke(this, EventArgs.Empty);
        return Current;
    }

    public void Clear()
    {
        _entries.Clear();
        _index = -1;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
