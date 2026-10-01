using DiagnosticStudio.Core.Artifacts;

namespace DiagnosticStudio.Tests.Core;

public class ArtifactTreeTests
{
    private static DiagnosticArtifact Make(string[] provenance, ArtifactType type = ArtifactType.TextLog, string? category = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = provenance[^1],
            OriginalPath = string.Join('/', provenance),
            Provenance = provenance,
            ArtifactType = type,
            Category = category,
        };

    [Fact]
    public void Files_tree_nests_archive_contents_under_the_archive_node()
    {
        var bundle = Make(new[] { "Bundle.zip" }, ArtifactType.Archive);
        var cab = Make(new[] { "Bundle.zip", "mdm.zip" }, ArtifactType.Archive);
        var log = Make(new[] { "Bundle.zip", "mdm.zip", "Logs", "agent.log" });
        var top = Make(new[] { "Bundle.zip", "readme.txt" });

        var roots = ArtifactTree.BuildFiles(new[] { log, top, cab, bundle });

        var root = Assert.Single(roots);
        Assert.Same(bundle, root.Artifact);
        Assert.Equal(new[] { "mdm.zip", "readme.txt" }, root.Children.Select(c => c.Name));

        var mdm = root.Children[0];
        Assert.Same(cab, mdm.Artifact);
        var logs = Assert.Single(mdm.Children);
        Assert.Null(logs.Artifact);
        Assert.Same(log, Assert.Single(logs.Children).Artifact);
    }

    [Fact]
    public void Files_tree_places_folders_before_files_and_sorts_alphabetically()
    {
        var items = new[]
        {
            Make(new[] { "dir", "b.log" }),
            Make(new[] { "dir", "A.log" }),
            Make(new[] { "dir", "sub", "x.log" }),
        };

        var dir = Assert.Single(ArtifactTree.BuildFiles(items));

        Assert.Equal(new[] { "sub", "A.log", "b.log" }, dir.Children.Select(c => c.Name));
    }

    [Fact]
    public void Logical_tree_groups_by_category_in_display_order_and_omits_empty_groups()
    {
        var items = new[]
        {
            Make(new[] { "b", "z.log" }, category: "Networking"),
            Make(new[] { "b", "a.reg" }, category: "Registry"),
            Make(new[] { "b", "m.log" }, category: "Intune"),
            Make(new[] { "b", "unknown.log" }, category: "Not A Known Category"),
            Make(new[] { "b", "none.log" }),
        };

        var groups = ArtifactTree.BuildLogical(items);

        Assert.Equal(new[] { "Intune", "Registry", "Networking", "Other" }, groups.Select(g => g.Name));
        Assert.Equal(2, groups.Last().Children.Count);
    }
}
