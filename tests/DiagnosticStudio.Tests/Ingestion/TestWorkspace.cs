using System.IO.Compression;
using System.Text;
using DiagnosticStudio.Core.Archives;
using DiagnosticStudio.Core.Ingestion;
using DiagnosticStudio.Ingestion;

namespace DiagnosticStudio.Tests.Ingestion;

/// <summary>Temp folder that owns inputs and the ingestion working root for one test.</summary>
internal sealed class TestWorkspace : IDisposable
{
    public TestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "ds-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        WorkRoot = Path.Combine(Root, "work");
    }

    public string Root { get; }
    public string WorkRoot { get; }

    public string PathFor(string name) => Path.Combine(Root, name);

    public IngestionOptions Options(ExtractionLimits? limits = null) =>
        new() { WorkspaceRoot = WorkRoot, Limits = limits ?? new ExtractionLimits() };

    public static BundleIngestor CreateIngestor() => new(new IArchiveProvider[] { new ZipArchiveProvider(), new CabArchiveProvider() });

    public static byte[] BuildZip(Action<ZipArchive> populate)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            populate(archive);
        }

        return stream.ToArray();
    }

    public static void AddText(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    public static void AddBytes(ZipArchive archive, string entryName, byte[] content)
    {
        var entry = archive.CreateEntry(entryName);
        using var stream = entry.Open();
        stream.Write(content);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
