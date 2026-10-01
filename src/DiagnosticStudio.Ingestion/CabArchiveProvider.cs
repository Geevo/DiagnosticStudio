using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using DiagnosticStudio.Core.Archives;
using DiagnosticStudio.Core.Artifacts;
using DiagnosticStudio.Core.Ingestion;

namespace DiagnosticStudio.Ingestion;

/// <summary>
/// Extracts Windows cabinet (.cab) archives with the same protections as the ZIP provider: entry names are vetted
/// by <see cref="SafeEntryPath"/>, files are only ever created inside the destination, and size, count and total
/// limits are enforced as bytes arrive. Decompression is done by the Windows cabinet API; this class supplies every
/// file handle, so nothing the cabinet says can make the API write anywhere else.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CabArchiveProvider : IArchiveProvider
{
    public string Name => "CAB";

    public bool CanOpen(DiagnosticArtifact artifact) =>
        OperatingSystem.IsWindows()
        && artifact.ArtifactType == ArtifactType.Archive
        && artifact.Subtype == ArchiveFormats.Cab
        && artifact.ExtractedPath is not null;

    public Task<IReadOnlyList<ExtractedArchiveEntry>> ExtractAsync(
        DiagnosticArtifact artifact,
        string destinationDirectory,
        ArchiveExtractionContext context,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () =>
            {
                using var extraction = new CabExtraction(artifact, destinationDirectory, context, cancellationToken);
                return extraction.Run();
            },
            cancellationToken);
    }

    /// <summary>One extraction. The cabinet API is single-threaded per instance, so no locking is needed.</summary>
    private sealed class CabExtraction : IDisposable
    {
        // The name FDI is asked to open. Whatever it passes to the open callback is ignored apart from this check, so
        // the real (possibly non-ASCII) path never has to survive a trip through an ANSI string.
        private const string SourceName = "cabinet";
        private const int BufferSize = 81920;

        private readonly DiagnosticArtifact _artifact;
        private readonly string _destination;
        private readonly ArchiveExtractionContext _context;
        private readonly CancellationToken _token;
        private readonly Dictionary<nint, Stream> _sources = new();
        private readonly Dictionary<nint, OutputFile> _outputs = new();
        private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<ExtractedArchiveEntry> _results = new();
        private readonly byte[] _buffer = new byte[BufferSize];

        // Kept as fields: the native side calls them for as long as the context exists.
        private readonly CabNative.Alloc _alloc;
        private readonly CabNative.Free _free;
        private readonly CabNative.Open _open;
        private readonly CabNative.Read _read;
        private readonly CabNative.Write _write;
        private readonly CabNative.Close _close;
        private readonly CabNative.Seek _seek;
        private readonly CabNative.Notify _notify;

        private nint _nextHandle;
        private string? _stopReason;
        private bool _spansCabinets;

        public CabExtraction(DiagnosticArtifact artifact, string destination, ArchiveExtractionContext context, CancellationToken token)
        {
            _artifact = artifact;
            _destination = destination;
            _context = context;
            _token = token;
            _alloc = cb => Marshal.AllocHGlobal((nint)cb);
            _free = Marshal.FreeHGlobal;
            _open = OnOpen;
            _read = OnRead;
            _write = OnWrite;
            _close = OnClose;
            _seek = OnSeek;
            _notify = OnNotify;
        }

        public IReadOnlyList<ExtractedArchiveEntry> Run()
        {
            Directory.CreateDirectory(_destination);

            var erf = Marshal.AllocHGlobal(Marshal.SizeOf<CabNative.Erf>());
            Marshal.StructureToPtr(default(CabNative.Erf), erf, fDeleteOld: false);
            var info = Marshal.AllocHGlobal(Marshal.SizeOf<CabNative.CabinetInfo>());
            IntPtr fdi = IntPtr.Zero;

            try
            {
                try
                {
                    fdi = CabNative.FdiCreate(_alloc, _free, _open, _read, _write, _close, _seek, CabNative.CpuUnknown, erf);
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
                {
                    throw new NotSupportedException("The Windows cabinet library (cabinet.dll) is not available: " + ex.Message, ex);
                }

                if (fdi == IntPtr.Zero)
                {
                    throw new InvalidDataException("The cabinet extraction library could not be initialised.");
                }

                CheckIsCabinet(fdi, info);

                var ok = CabNative.FdiCopy(fdi, SourceName, string.Empty, 0, _notify, IntPtr.Zero, IntPtr.Zero);
                _token.ThrowIfCancellationRequested();

                if (!ok)
                {
                    HandleFailure(Marshal.PtrToStructure<CabNative.Erf>(erf));
                }

                return _results;
            }
            finally
            {
                if (fdi != IntPtr.Zero)
                {
                    CabNative.FdiDestroy(fdi);
                }

                Marshal.FreeHGlobal(info);
                Marshal.FreeHGlobal(erf);
                GC.KeepAlive(this);
            }
        }

        private void CheckIsCabinet(IntPtr fdi, IntPtr info)
        {
            var handle = OpenSource();
            try
            {
                if (!CabNative.FdiIsCabinet(fdi, handle, info))
                {
                    throw new InvalidDataException("The file is not a valid cabinet.");
                }

                var details = Marshal.PtrToStructure<CabNative.CabinetInfo>(info);
                if (details.HasPrev != 0 || details.HasNext != 0)
                {
                    _spansCabinets = true;
                    Report(
                        IngestionIssueSeverity.Warning,
                        _artifact.ProvenanceDisplay,
                        "This cabinet is part of a multi-cabinet set. Files that continue in another cabinet cannot be extracted.");
                }
            }
            finally
            {
                OnClose(handle);
            }
        }

        private void HandleFailure(CabNative.Erf erf)
        {
            if (_stopReason is not null)
            {
                Report(IngestionIssueSeverity.Error, _artifact.ProvenanceDisplay, "Extraction stopped: " + _stopReason);
                return;
            }

            if (_spansCabinets && erf.Type == CabNative.ErrorUserAbort)
            {
                return; // already reported: the rest of the set is not available
            }

            var message = $"The cabinet is damaged or uses an unsupported feature (error {erf.Oper}).";
            if (_results.Count == 0)
            {
                throw new InvalidDataException(message);
            }

            Report(IngestionIssueSeverity.Error, _artifact.ProvenanceDisplay, message + " Files extracted before the problem were kept.");
        }

        // ---- cabinet source and output streams ----

        private nint OnOpen(IntPtr pszFile, int oflag, int pmode)
        {
            var name = Marshal.PtrToStringAnsi(pszFile);
            if (!string.Equals(name, SourceName, StringComparison.Ordinal))
            {
                return -1; // FDI asked for another file (the next cabinet of a set), which is not available
            }

            try
            {
                return OpenSource();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return -1;
            }
        }

        private nint OpenSource()
        {
            var stream = new FileStream(
                _artifact.ExtractedPath!, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.RandomAccess);
            var handle = ++_nextHandle;
            _sources[handle] = stream;
            return handle;
        }

        private uint OnRead(nint hf, IntPtr pv, uint cb)
        {
            if (!_sources.TryGetValue(hf, out var stream))
            {
                return uint.MaxValue;
            }

            try
            {
                var total = 0;
                while (total < cb)
                {
                    var read = stream.Read(_buffer, 0, (int)Math.Min(_buffer.Length, cb - total));
                    if (read == 0)
                    {
                        break;
                    }

                    Marshal.Copy(_buffer, 0, pv + total, read);
                    total += read;
                }

                return (uint)total;
            }
            catch (IOException)
            {
                return uint.MaxValue;
            }
        }

        private uint OnWrite(nint hf, IntPtr pv, uint cb)
        {
            if (!_outputs.TryGetValue(hf, out var output))
            {
                return uint.MaxValue;
            }

            if (_token.IsCancellationRequested)
            {
                return uint.MaxValue;
            }

            try
            {
                var remaining = (long)cb;
                var offset = 0;
                while (remaining > 0)
                {
                    var chunk = (int)Math.Min(_buffer.Length, remaining);

                    // Count bytes as they are produced: the sizes stored in the cabinet are not trusted.
                    output.Written += chunk;
                    if (_context.Budget.TryAddBytes(chunk, output.Written) is { } violation)
                    {
                        output.Failed = true;
                        _stopReason ??= violation.Message;
                        return uint.MaxValue;
                    }

                    Marshal.Copy(pv + offset, _buffer, 0, chunk);
                    output.Stream.Write(_buffer, 0, chunk);
                    offset += chunk;
                    remaining -= chunk;
                }

                return cb;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                output.Failed = true;
                output.Error = ex.Message;
                return uint.MaxValue;
            }
        }

        private int OnClose(nint hf)
        {
            if (_sources.Remove(hf, out var source))
            {
                source.Dispose();
                return 0;
            }

            if (_outputs.Remove(hf, out var output))
            {
                // A finished entry is closed by FinishEntry. Arriving here means FDI gave up part-way through the
                // file (a write was refused, or the data is damaged), so what exists is incomplete: never keep it.
                output.Stream.Dispose();
                ExtractionFiles.TryDelete(output.Path);
                if (output.Error is not null)
                {
                    Report(IngestionIssueSeverity.Warning, output.Subject, "Entry skipped: " + output.Error);
                }

                return 0;
            }

            return -1;
        }

        private int OnSeek(nint hf, int distance, int origin)
        {
            if (!_sources.TryGetValue(hf, out var stream))
            {
                return -1;
            }

            try
            {
                var position = stream.Seek(distance, origin switch
                {
                    0 => SeekOrigin.Begin,
                    1 => SeekOrigin.Current,
                    _ => SeekOrigin.End,
                });
                return position > int.MaxValue ? -1 : (int)position;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
            {
                return -1;
            }
        }

        // ---- entries ----

        private nint OnNotify(int type, IntPtr pointer)
        {
            try
            {
                var notification = Marshal.PtrToStructure<CabNative.Notification>(pointer);
                return type switch
                {
                    CabNative.NotifyCopyFile => BeginEntry(notification),
                    CabNative.NotifyCloseFileInfo => FinishEntry(notification),
                    CabNative.NotifyNextCabinet => -1, // no further cabinets are available; already warned about
                    _ => 0,
                };
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // An exception must never cross back into native code.
                _stopReason ??= "Unexpected error while extracting: " + ex.Message;
                return -1;
            }
        }

        private nint BeginEntry(CabNative.Notification notification)
        {
            if (_token.IsCancellationRequested)
            {
                return -1;
            }

            var entryName = DecodeName(notification);
            var subject = _artifact.ProvenanceDisplay + " → " + entryName;

            if (!SafeEntryPath.TryResolve(_destination, entryName, out var target, out var reason))
            {
                Report(IngestionIssueSeverity.Warning, subject, "Entry skipped: " + reason);
                return 0;
            }

            // The size stored for a file is also the most FDI will write for it, so it can be checked up front.
            if (notification.Cb > _context.Budget.Limits.MaxSingleFileBytes)
            {
                Report(
                    IngestionIssueSeverity.Warning,
                    subject,
                    $"Entry skipped: declared size {notification.Cb:N0} bytes exceeds the single-file limit.");
                return 0;
            }

            if (_context.Budget.TryReserveFile() is { } fileViolation)
            {
                _stopReason = fileViolation.Message;
                return -1;
            }

            target = ExtractionFiles.MakeUnique(target, _used);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize);
                var handle = ++_nextHandle;
                _outputs[handle] = new OutputFile(stream, target, entryName.Replace('\\', '/'), subject);
                return handle;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Report(IngestionIssueSeverity.Warning, subject, "Entry skipped: " + ex.Message);
                return 0;
            }
        }

        private nint FinishEntry(CabNative.Notification notification)
        {
            if (!_outputs.Remove(notification.Hf, out var output))
            {
                return -1;
            }

            output.Stream.Dispose();
            if (output.Failed)
            {
                ExtractionFiles.TryDelete(output.Path);
                if (output.Error is not null)
                {
                    Report(IngestionIssueSeverity.Warning, output.Subject, "Entry skipped: " + output.Error);
                }

                return 1;
            }

            _results.Add(new ExtractedArchiveEntry(output.EntryPath, output.Path, output.Written));
            return 1;
        }

        private static string DecodeName(CabNative.Notification notification)
        {
            var raw = Marshal.PtrToStringAnsi(notification.Psz1) ?? string.Empty;
            if ((notification.Attribs & CabNative.NameIsUtf8) == 0)
            {
                return raw;
            }

            // The ANSI read above mangled non-ASCII bytes; re-read them as UTF-8.
            var length = 0;
            while (Marshal.ReadByte(notification.Psz1, length) != 0)
            {
                length++;
            }

            var bytes = new byte[length];
            Marshal.Copy(notification.Psz1, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        private void Report(IngestionIssueSeverity severity, string subject, string message) =>
            _context.ReportIssue(new IngestionIssue
            {
                Stage = IngestionStage.Extracting,
                Severity = severity,
                Subject = subject,
                Component = "CAB",
                Message = message,
            });

        public void Dispose()
        {
            // Normally empty; anything left means FDI was aborted part-way through an entry.
            foreach (var stream in _sources.Values)
            {
                stream.Dispose();
            }

            foreach (var output in _outputs.Values)
            {
                output.Stream.Dispose();
                ExtractionFiles.TryDelete(output.Path);
            }

            _sources.Clear();
            _outputs.Clear();
        }
    }

    private sealed class OutputFile
    {
        public OutputFile(Stream stream, string path, string entryPath, string subject)
        {
            Stream = stream;
            Path = path;
            EntryPath = entryPath;
            Subject = subject;
        }

        public Stream Stream { get; }
        public string Path { get; }
        public string EntryPath { get; }
        public string Subject { get; }
        public long Written { get; set; }
        public bool Failed { get; set; }
        public string? Error { get; set; }
    }
}
