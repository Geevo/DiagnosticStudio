using DiagnosticStudio.Core.Archives;
using DiagnosticStudio.Core.Artifacts;

namespace DiagnosticStudio.Ingestion;

public readonly record struct ArtifactClassification(ArtifactType Type, string? Category, string? Subtype);

/// <summary>
/// Classifies a file by extension, name, path and a small content sniff. Never opens the file for anything
/// beyond reading its first few bytes.
/// </summary>
public static class ArtifactClassifier
{
    public const string CategoryEventLogs = "Event Logs";
    public const string CategoryRegistry = "Registry";
    public const string CategoryNetworking = "Networking";
    public const string CategoryWindowsUpdate = "Windows Update";
    public const string CategoryWindowsServicing = "Windows Servicing";
    public const string CategoryPower = "Power";
    public const string CategorySecurity = "Security";
    public const string CategoryIntune = "Intune";
    public const string CategoryNestedArchives = "Nested Archives";
    public const string CategoryOther = "Other";

    private const int SniffLength = 4096;

    private static readonly byte[] ZipLocalHeader = { 0x50, 0x4B, 0x03, 0x04 };
    private static readonly byte[] ZipEmpty = { 0x50, 0x4B, 0x05, 0x06 };
    private static readonly byte[] CabMagic = { 0x4D, 0x53, 0x43, 0x46 }; // "MSCF"
    private static readonly byte[] EvtxMagic = { 0x45, 0x6C, 0x66, 0x46, 0x69, 0x6C, 0x65, 0x00 }; // "ElfFile\0"

    // Zip containers that are documents/packages rather than evidence bundles.
    private static readonly HashSet<string> ZipBasedNonArchives = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".xlsx", ".pptx", ".jar", ".nupkg", ".vsix", ".apk", ".appx", ".msix", ".odt", ".ods",
    };

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".sys", ".bin", ".dat", ".pdb", ".msi", ".mui", ".cat", ".dmp", ".hve", ".db", ".sqlite",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico",
    };

    // Known command captures: (token in file name, category, subtype).
    private static readonly (string Token, string Category, string Subtype)[] CommandSignatures =
    {
        ("ipconfig", CategoryNetworking, "IpConfig"),
        ("netsh", CategoryNetworking, "Netsh"),
        ("nslookup", CategoryNetworking, "NsLookup"),
        ("route_print", CategoryNetworking, "Route"),
        ("certutil", CategorySecurity, "CertUtil"),
        ("powercfg", CategoryPower, "PowerCfg"),
        ("mpcmdrun", CategorySecurity, "Defender"),
        ("defender", CategorySecurity, "Defender"),
        ("wuauclt", CategoryWindowsUpdate, "WindowsUpdate"),
        ("usoclient", CategoryWindowsUpdate, "WindowsUpdate"),
    };

    private static readonly (string Token, string Category, string Subtype)[] LogSignatures =
    {
        ("cbs", CategoryWindowsServicing, "CBS"),
        ("dism", CategoryWindowsServicing, "DISM"),
        ("windowsupdate", CategoryWindowsUpdate, "WindowsUpdate"),
        ("intunemanagementextension", CategoryIntune, "IntuneManagementExtension"),
        ("agentexecutor", CategoryIntune, "AgentExecutor"),
        ("clienthealth", CategoryIntune, "ClientHealth"),
        ("healthscripts", CategoryIntune, "HealthScripts"),
        ("win32appinventory", CategoryIntune, "Win32AppInventory"),
        ("sidecar", CategoryIntune, "Sidecar"),
    };

    public static ArtifactClassification Classify(string name, IReadOnlyList<string> provenance, string? filePath)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        var stem = Path.GetFileNameWithoutExtension(name);
        var header = ReadHeader(filePath);

        if (IsZip(header) && !ZipBasedNonArchives.Contains(extension))
        {
            return new(ArtifactType.Archive, CategoryNestedArchives, ArchiveFormats.Zip);
        }

        if (StartsWith(header, CabMagic) || extension == ".cab")
        {
            return new(ArtifactType.Archive, CategoryNestedArchives, ArchiveFormats.Cab);
        }

        if (StartsWith(header, EvtxMagic) || extension == ".evtx")
        {
            return new(ArtifactType.EventLog, CategoryEventLogs, stem);
        }

        switch (extension)
        {
            case ".reg":
                return new(ArtifactType.RegistryExport, CategoryRegistry, null);
            case ".etl":
                return new(ArtifactType.Trace, CategoryOther, "ETL");
            case ".xml":
                return new(ArtifactType.Xml, CategoryFor(name), null);
            case ".json":
            case ".jsonl":
            case ".ndjson":
                return new(ArtifactType.Json, CategoryFor(name), null);
            case ".htm":
            case ".html":
                return new(ArtifactType.Html, CategoryFor(name), null);
            case ".csv":
            case ".tsv":
                // Only when it reads as text: a binary file with this extension is not a table.
                if (header.Length == 0 || LooksLikeText(header))
                {
                    return new(ArtifactType.Csv, CategoryFor(name), null);
                }

                break;
        }

        if (BinaryExtensions.Contains(extension) || (header.Length > 0 && !LooksLikeText(header)))
        {
            return new(ArtifactType.Binary, CategoryOther, null);
        }

        // XML under any other name (.mum, .manifest, .config, no extension...) is recognised by its declaration. Files
        // explicitly named as plain text stay text, whatever they start with.
        if (extension is not (".log" or ".txt") && StartsWithXmlDeclaration(header))
        {
            return new(ArtifactType.Xml, CategoryFor(name), null);
        }

        var lowerName = name.ToLowerInvariant();
        var inCommandFolder = provenance.Any(p =>
            p.Equals("Command", StringComparison.OrdinalIgnoreCase)
            || p.Equals("Commands", StringComparison.OrdinalIgnoreCase));
        foreach (var (token, category, subtype) in CommandSignatures)
        {
            if (lowerName.Contains(token, StringComparison.Ordinal)
                && (inCommandFolder || lowerName.Contains("output", StringComparison.Ordinal)))
            {
                return new(ArtifactType.CommandOutput, category, subtype);
            }
        }

        foreach (var (token, category, subtype) in LogSignatures)
        {
            if (lowerName.Contains(token, StringComparison.Ordinal))
            {
                return new(ArtifactType.TextLog, category, subtype);
            }
        }

        // Text without a recognised name: still readable, just not specially grouped.
        return new(ArtifactType.TextLog, CategoryOther, null);
    }

    private static string CategoryFor(string name)
    {
        var lower = name.ToLowerInvariant();
        foreach (var (token, category, _) in LogSignatures)
        {
            if (lower.Contains(token, StringComparison.Ordinal))
            {
                return category;
            }
        }

        return CategoryOther;
    }

    private static byte[] ReadHeader(string? path)
    {
        if (path is null)
        {
            return Array.Empty<byte>();
        }

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[SniffLength];
            var read = stream.Read(buffer, 0, buffer.Length);
            return read == buffer.Length ? buffer : buffer[..read];
        }
        catch (IOException)
        {
            return Array.Empty<byte>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<byte>();
        }
    }

    private static bool StartsWithXmlDeclaration(byte[] header)
    {
        ReadOnlySpan<byte> bytes = header;
        string text;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            text = System.Text.Encoding.UTF8.GetString(bytes[3..Math.Min(bytes.Length, 3 + 40)]);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            text = System.Text.Encoding.Unicode.GetString(bytes[2..Math.Min(bytes.Length, 2 + 80)]);
        }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            text = System.Text.Encoding.BigEndianUnicode.GetString(bytes[2..Math.Min(bytes.Length, 2 + 80)]);
        }
        else
        {
            text = System.Text.Encoding.ASCII.GetString(bytes[..Math.Min(bytes.Length, 40)]);
        }

        return text.TrimStart().StartsWith("<?xml", StringComparison.Ordinal);
    }

    private static bool IsZip(byte[] header) => StartsWith(header, ZipLocalHeader) || StartsWith(header, ZipEmpty);

    private static bool StartsWith(byte[] header, byte[] magic) =>
        header.Length >= magic.Length && header.AsSpan(0, magic.Length).SequenceEqual(magic);

    /// <summary>Heuristic: BOM'd or NUL-free data with few control characters is text. Empty files count as text.</summary>
    internal static bool LooksLikeText(byte[] header)
    {
        if (header.Length == 0)
        {
            return true;
        }

        if (header.Length >= 2
            && ((header[0] == 0xFF && header[1] == 0xFE) || (header[0] == 0xFE && header[1] == 0xFF)))
        {
            return true; // UTF-16 BOM
        }

        if (header.Length >= 3 && header[0] == 0xEF && header[1] == 0xBB && header[2] == 0xBF)
        {
            return true; // UTF-8 BOM
        }

        var nuls = 0;
        var control = 0;
        var oddNuls = 0;
        var evenNuls = 0;
        for (var i = 0; i < header.Length; i++)
        {
            var b = header[i];
            if (b == 0)
            {
                nuls++;
                if ((i & 1) == 0)
                {
                    evenNuls++;
                }
                else
                {
                    oddNuls++;
                }
            }
            else if (b < 0x20 && b != '\t' && b != '\r' && b != '\n' && b != 0x0C && b != 0x1B)
            {
                control++;
            }
        }

        if (nuls > 0)
        {
            // UTF-16 without BOM: ASCII-range text has a NUL in every other byte.
            var half = header.Length / 2;
            var utf16Like = (oddNuls > half * 0.6 && evenNuls == 0) || (evenNuls > half * 0.6 && oddNuls == 0);
            return utf16Like && control == 0;
        }

        return control * 100 < header.Length * 2; // under 2% stray control bytes
    }
}
