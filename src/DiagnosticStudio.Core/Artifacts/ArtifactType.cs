namespace DiagnosticStudio.Core.Artifacts;

/// <summary>
/// Physical/structural kind of an artifact. Logical grouping lives in
/// <see cref="DiagnosticArtifact.Category"/> and <see cref="DiagnosticArtifact.Subtype"/>.
/// </summary>
public enum ArtifactType
{
    Unknown = 0,
    TextLog,
    CommandOutput,
    EventLog,
    RegistryExport,
    Archive,
    Xml,
    Json,
    Html,
    Trace,
    Binary,
}
