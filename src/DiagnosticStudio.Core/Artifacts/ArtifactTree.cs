namespace DiagnosticStudio.Core.Artifacts;

/// <summary>Presentation-neutral tree node used to project artifacts into explorer hierarchies.</summary>
public sealed class ArtifactTreeNode
{
    private readonly List<ArtifactTreeNode> _children = new();

    public ArtifactTreeNode(string name, DiagnosticArtifact? artifact = null)
    {
        Name = name;
        Artifact = artifact;
    }

    public string Name { get; }

    /// <summary>Set for nodes that are artifacts; <c>null</c> for plain folders and category groups.</summary>
    public DiagnosticArtifact? Artifact { get; internal set; }

    public IReadOnlyList<ArtifactTreeNode> Children => _children;

    internal void Add(ArtifactTreeNode child) => _children.Add(child);

    internal void SortChildren(IComparer<ArtifactTreeNode> comparer)
    {
        _children.Sort(comparer);
        foreach (var child in _children)
        {
            child.SortChildren(comparer);
        }
    }
}

public static class ArtifactTree
{
    /// <summary>Logical explorer groups in display order. Categories not listed fall under "Other".</summary>
    public static readonly IReadOnlyList<string> LogicalCategoryOrder = new[]
    {
        "Intune",
        "Event Logs",
        "Registry",
        "Networking",
        "Windows Update",
        "Windows Servicing",
        "Power",
        "Security",
        "Nested Archives",
        "Other",
    };

    /// <summary>
    /// Builds the exact source hierarchy from each artifact's provenance, so archives appear as
    /// containers holding their extracted content. Never flattens provenance.
    /// </summary>
    public static IReadOnlyList<ArtifactTreeNode> BuildFiles(IEnumerable<DiagnosticArtifact> artifacts)
    {
        var roots = new List<ArtifactTreeNode>();
        var byPath = new Dictionary<string, ArtifactTreeNode>(StringComparer.Ordinal);

        foreach (var artifact in artifacts)
        {
            ArtifactTreeNode? parent = null;
            var key = string.Empty;
            for (var i = 0; i < artifact.Provenance.Count; i++)
            {
                var segment = artifact.Provenance[i];
                key = key + "\u0001" + segment;
                var isLast = i == artifact.Provenance.Count - 1;

                if (!byPath.TryGetValue(key, out var node))
                {
                    node = new ArtifactTreeNode(segment);
                    byPath[key] = node;
                    if (parent is null)
                    {
                        roots.Add(node);
                    }
                    else
                    {
                        parent.Add(node);
                    }
                }

                if (isLast)
                {
                    node.Artifact = artifact;
                }

                parent = node;
            }
        }

        var comparer = new FilesNodeComparer();
        roots.Sort(comparer);
        foreach (var root in roots)
        {
            root.SortChildren(comparer);
        }

        return roots;
    }

    /// <summary>Groups artifacts by logical category. Empty categories are omitted.</summary>
    public static IReadOnlyList<ArtifactTreeNode> BuildLogical(IEnumerable<DiagnosticArtifact> artifacts)
    {
        var groups = LogicalCategoryOrder.ToDictionary(c => c, _ => new List<DiagnosticArtifact>());
        foreach (var artifact in artifacts)
        {
            var category = artifact.Category is { } c && groups.ContainsKey(c) ? c : "Other";
            groups[category].Add(artifact);
        }

        var result = new List<ArtifactTreeNode>();
        foreach (var category in LogicalCategoryOrder)
        {
            var members = groups[category];
            if (members.Count == 0)
            {
                continue;
            }

            var group = new ArtifactTreeNode(category);
            foreach (var artifact in members.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(a => a.ProvenanceDisplay, StringComparer.OrdinalIgnoreCase))
            {
                group.Add(new ArtifactTreeNode(artifact.Name, artifact));
            }

            result.Add(group);
        }

        return result;
    }

    // Folders and containers first, then files; each alphabetical.
    private sealed class FilesNodeComparer : IComparer<ArtifactTreeNode>
    {
        public int Compare(ArtifactTreeNode? x, ArtifactTreeNode? y)
        {
            var xLeaf = x!.Children.Count == 0;
            var yLeaf = y!.Children.Count == 0;
            if (xLeaf != yLeaf)
            {
                return xLeaf ? 1 : -1;
            }

            return StringComparer.OrdinalIgnoreCase.Compare(x.Name, y.Name);
        }
    }
}
