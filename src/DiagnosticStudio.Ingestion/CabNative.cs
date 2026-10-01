using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DiagnosticStudio.Ingestion;

/// <summary>
/// Bindings for the Windows cabinet extraction API (File Decompression Interface in <c>cabinet.dll</c>).
/// FDI decodes every cabinet compression type (none, MSZIP, LZX, Quantum). All of its file access goes through the
/// callbacks below, so the caller decides which streams it reads and writes; it never touches the file system itself.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CabNative
{
    private const string Library = "cabinet.dll";

    public const int CpuUnknown = -1;

    // FDINOTIFICATIONTYPE
    public const int NotifyCabinetInfo = 0;
    public const int NotifyPartialFile = 1;
    public const int NotifyCopyFile = 2;
    public const int NotifyCloseFileInfo = 3;
    public const int NotifyNextCabinet = 4;

    /// <summary>Set in <see cref="Notification.Attribs"/> when the stored file name is UTF-8.</summary>
    public const ushort NameIsUtf8 = 0x80;

    public const int ErrorUserAbort = 11;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr Alloc(uint cb);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void Free(IntPtr pv);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate nint Open(IntPtr pszFile, int oflag, int pmode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate uint Read(nint hf, IntPtr pv, uint cb);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate uint Write(nint hf, IntPtr pv, uint cb);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int Close(nint hf);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int Seek(nint hf, int dist, int seekType);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate nint Notify(int type, IntPtr notification);

    /// <summary>FDINOTIFICATION. Natural alignment: pointers sit on 8-byte boundaries in a 64-bit process.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Notification
    {
        public int Cb;
        public IntPtr Psz1;
        public IntPtr Psz2;
        public IntPtr Psz3;
        public IntPtr Pv;
        public nint Hf;
        public ushort Date;
        public ushort Time;
        public ushort Attribs;
        public ushort SetId;
        public ushort ICabinet;
        public ushort IFolder;
        public int Fdie;
    }

    /// <summary>FDICABINETINFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CabinetInfo
    {
        public int CbCabinet;
        public ushort CFolders;
        public ushort CFiles;
        public ushort SetId;
        public ushort ICabinet;
        public int FReserve;
        public int HasPrev;
        public int HasNext;
    }

    /// <summary>ERF. FDI keeps a pointer to it and writes errors into it later, so it must live in native memory.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Erf
    {
        public int Oper;
        public int Type;
        public int Error;
    }

    [DllImport(Library, EntryPoint = "FDICreate", CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr FdiCreate(
        Alloc alloc, Free free, Open open, Read read, Write write, Close close, Seek seek, int cpuType, IntPtr erf);

    [DllImport(Library, EntryPoint = "FDIDestroy", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FdiDestroy(IntPtr hfdi);

    [DllImport(Library, EntryPoint = "FDIIsCabinet", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FdiIsCabinet(IntPtr hfdi, nint hf, IntPtr cabinetInfo);

    [DllImport(Library, EntryPoint = "FDICopy", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FdiCopy(
        IntPtr hfdi, string cabinetName, string cabinetPath, int flags, Notify notify, IntPtr decrypt, IntPtr user);
}
