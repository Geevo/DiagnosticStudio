namespace DiagnosticStudio.Core.Documents;

public enum RegistryValueKind
{
    None = 0,
    String = 1,
    ExpandString = 2,
    Binary = 3,
    DWord = 4,
    DWordBigEndian = 5,
    Link = 6,
    MultiString = 7,
    ResourceList = 8,
    FullResourceDescriptor = 9,
    ResourceRequirementsList = 10,
    QWord = 11,

    /// <summary>A <c>hex(N)</c> type this parser does not know; <see cref="RegistryValue.TypeName"/> carries the number.</summary>
    Unknown = 255,

    /// <summary>The line deleted the value (<c>"name"=-</c>).</summary>
    Deleted = 256,
}

/// <summary>One value line from a .reg file, decoded for display.</summary>
public sealed class RegistryValue
{
    public const int MaxStoredDataBytes = 4096;

    public required string Name { get; init; }
    public bool IsDefault { get; init; }
    public required RegistryValueKind Kind { get; init; }

    /// <summary>Registry Editor style type name, e.g. <c>REG_SZ</c>.</summary>
    public required string TypeName { get; init; }

    /// <summary>Single-line rendering of the data. Long binary data is abbreviated.</summary>
    public required string DisplayValue { get; init; }

    /// <summary>Size of the decoded data in bytes (0 for strings kept as text and deletions).</summary>
    public long DataLength { get; init; }

    /// <summary>Raw bytes for binary-like values, kept only up to <see cref="MaxStoredDataBytes"/> (see <see cref="IsDataTruncated"/>).</summary>
    public byte[]? Data { get; init; }

    public bool IsDataTruncated { get; init; }

    /// <summary>Multi-line rendering for the detail pane when <see cref="DisplayValue"/> would lose structure (REG_MULTI_SZ).</summary>
    public string? DetailText { get; init; }

    /// <summary>One-based line in the source file where the value starts.</summary>
    public int SourceLine { get; init; }

    public bool IsDeleted => Kind == RegistryValueKind.Deleted;

    public string DisplayName => IsDefault ? "(Default)" : Name;
}

public sealed class RegistryKey
{
    private readonly Dictionary<string, RegistryKey> _childrenByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RegistryKey> _children = new();
    private readonly Dictionary<string, int> _valueIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RegistryValue> _values = new();

    public RegistryKey(string name, RegistryKey? parent, int sourceLine)
    {
        Name = name;
        Parent = parent;
        SourceLine = sourceLine;
        FullPath = parent is null || parent.IsRoot ? name : parent.FullPath + "\\" + name;
    }

    /// <summary>Key name (single path segment). Empty for the virtual root.</summary>
    public string Name { get; }

    public string FullPath { get; }
    public RegistryKey? Parent { get; }
    public bool IsRoot => Parent is null;

    /// <summary>First line in the source that mentions this key (a header or an ancestor's implied path).</summary>
    public int SourceLine { get; private set; }

    /// <summary>The file asked for this key to be removed (<c>[-key]</c>).</summary>
    public bool IsDeleted { get; set; }

    /// <summary>True when a <c>[key]</c> header exists for this key, as opposed to being only a path component of a deeper key.</summary>
    public bool HasHeader { get; set; }

    public IReadOnlyList<RegistryKey> Children => _children;
    public IReadOnlyList<RegistryValue> Values => _values;

    public RegistryKey GetOrAddChild(string name, int sourceLine, out bool added)
    {
        if (_childrenByName.TryGetValue(name, out var existing))
        {
            added = false;
            return existing;
        }

        var child = new RegistryKey(name, this, sourceLine);
        _childrenByName[name] = child;
        _children.Add(child);
        added = true;
        return child;
    }

    public RegistryKey? FindChild(string name) =>
        _childrenByName.TryGetValue(name, out var child) ? child : null;

    /// <summary>Adds a value, or replaces an earlier value of the same name (later lines win, as on import).</summary>
    public void SetValue(RegistryValue value)
    {
        if (_valueIndex.TryGetValue(value.Name, out var index))
        {
            _values[index] = value;
            return;
        }

        _valueIndex[value.Name] = _values.Count;
        _values.Add(value);
    }

    public RegistryValue? FindValue(string name) =>
        _valueIndex.TryGetValue(name, out var index) ? _values[index] : null;

    public IEnumerable<RegistryKey> Ancestors()
    {
        for (var key = Parent; key is not null && !key.IsRoot; key = key.Parent)
        {
            yield return key;
        }
    }
}

public sealed record RegistryParseIssue(int Line, string Message);

public sealed record RegistryDocument : DiagnosticDocument
{
    /// <summary>Virtual root whose children are the hives (<c>HKEY_LOCAL_MACHINE</c> etc.).</summary>
    public required RegistryKey Root { get; init; }

    /// <summary>Every key with a header or that holds values, in file order. Used for search and ordering.</summary>
    public required IReadOnlyList<RegistryKey> Keys { get; init; }

    public int KeyCount { get; init; }
    public int ValueCount { get; init; }

    /// <summary>Header text such as <c>Windows Registry Editor Version 5.00</c> or <c>REGEDIT4</c>; <c>null</c> if absent.</summary>
    public string? FormatHeader { get; init; }

    public IReadOnlyList<RegistryParseIssue> Issues { get; init; } = Array.Empty<RegistryParseIssue>();

    /// <summary>Total issues found, which can exceed <see cref="Issues"/> (capped).</summary>
    public int TotalIssueCount { get; init; }

    /// <summary>Parsing stopped early because of size limits; <see cref="TruncatedAtLine"/> is where.</summary>
    public bool IsTruncated { get; init; }

    public int TruncatedAtLine { get; init; }

    /// <summary>The whole file as text, for the raw source view.</summary>
    public required ITextLineSource RawSource { get; init; }

    /// <summary>Resolves a full key path (case-insensitive) to its key, or <c>null</c>.</summary>
    public RegistryKey? FindKey(string keyPath)
    {
        var key = Root;
        foreach (var segment in keyPath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            key = key.FindChild(segment);
            if (key is null)
            {
                return null;
            }
        }

        return ReferenceEquals(key, Root) ? null : key;
    }
}
