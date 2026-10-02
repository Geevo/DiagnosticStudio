using System.Runtime.InteropServices;

namespace DiagnosticStudio.Parsers.Etl;

/// <summary>
/// The parts of the Windows trace API (advapi32 <c>OpenTrace</c>/<c>ProcessTrace</c> and tdh.dll) needed to read an ETL
/// file. Only the offsets that are used are named; the layouts are those of the 64-bit structures in evntrace.h,
/// evntcons.h and tdh.h. The file is only read: nothing in it is run, and no trace session is started.
/// </summary>
internal static unsafe class EtlNative
{
    public const ulong InvalidHandle = ulong.MaxValue;
    public const uint ProcessTraceModeEventRecord = 0x10000000;

    public const int ErrorSuccess = 0;
    public const int ErrorInsufficientBuffer = 122;
    public const int ErrorNotFound = 1168;

    public const ushort HeaderFlag32Bit = 0x0020;
    public const ushort HeaderFlag64Bit = 0x0040;

    // EVENT_TRACE_LOGFILEW, 64-bit: 448 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 448)]
    public struct EventTraceLogfile
    {
        [FieldOffset(0)] public char* LogFileName;
        [FieldOffset(8)] public char* LoggerName;
        [FieldOffset(28)] public uint ProcessTraceMode;
        [FieldOffset(400)] public delegate* unmanaged<EventTraceLogfile*, int> BufferCallback;
        [FieldOffset(424)] public delegate* unmanaged<EventRecord*, void> EventRecordCallback;
        [FieldOffset(440)] public void* Context;
    }

    // EVENT_RECORD, 64-bit: 112 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 112)]
    public struct EventRecord
    {
        [FieldOffset(0)] public ushort Size;
        [FieldOffset(4)] public ushort Flags;
        [FieldOffset(8)] public uint ThreadId;
        [FieldOffset(12)] public uint ProcessId;
        [FieldOffset(16)] public long TimeStamp;
        [FieldOffset(24)] public Guid ProviderId;
        [FieldOffset(40)] public ushort EventId;
        [FieldOffset(42)] public byte Version;
        [FieldOffset(43)] public byte Channel;
        [FieldOffset(44)] public byte Level;
        [FieldOffset(45)] public byte Opcode;
        [FieldOffset(46)] public ushort Task;
        [FieldOffset(48)] public ulong Keyword;
        [FieldOffset(64)] public Guid ActivityId;
        [FieldOffset(86)] public ushort UserDataLength;
        [FieldOffset(96)] public byte* UserData;
        [FieldOffset(104)] public void* UserContext;
    }

    // TRACE_EVENT_INFO header; the property array follows at <see cref="PropertyArrayOffset"/>.
    public const int PropertyArrayOffset = 112;

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    public struct EventPropertyInfo
    {
        [FieldOffset(0)] public uint Flags;
        [FieldOffset(4)] public uint NameOffset;
        [FieldOffset(8)] public ushort InType;
        [FieldOffset(10)] public ushort OutType;
        [FieldOffset(12)] public uint MapNameOffset;
        [FieldOffset(8)] public ushort StructStartIndex;
        [FieldOffset(10)] public ushort StructMemberCount;
        [FieldOffset(16)] public ushort Count;
        [FieldOffset(18)] public ushort Length;
    }

    public const uint PropertyStruct = 0x1;
    public const uint PropertyParamLength = 0x2;
    public const uint PropertyParamCount = 0x4;
    public const uint PropertyWbemXmlFragment = 0x8;

    // Offsets in TRACE_EVENT_INFO.
    public const int InfoDecodingSource = 48;
    public const uint DecodingSourceTlg = 3;
    public const int InfoEventNameOffset = 92;
    public const int InfoProviderNameOffset = 52;
    public const int InfoTaskNameOffset = 68;
    public const int InfoOpcodeNameOffset = 72;
    public const int InfoEventMessageOffset = 76;
    public const int InfoPropertyCount = 100;
    public const int InfoTopLevelPropertyCount = 104;

    // TDH_IN_TYPE values that matter when a property is referred to by another one.
    public const ushort InTypeInt8 = 3;
    public const ushort InTypeUInt8 = 4;
    public const ushort InTypeInt16 = 5;
    public const ushort InTypeUInt16 = 6;
    public const ushort InTypeInt32 = 7;
    public const ushort InTypeUInt32 = 8;
    public const ushort InTypeInt64 = 9;
    public const ushort InTypeUInt64 = 10;
    public const ushort InTypeBinary = 14;
    public const ushort InTypeHexInt32 = 20;
    public const ushort InTypeHexInt64 = 21;
    public const ushort OutTypeIpv6 = 24;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern ulong OpenTraceW(EventTraceLogfile* logfile);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    public static extern int ProcessTrace(ulong* handles, uint handleCount, void* startTime, void* endTime);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    public static extern int CloseTrace(ulong handle);

    [DllImport("tdh.dll", ExactSpelling = true)]
    public static extern int TdhGetEventInformation(EventRecord* record, uint contextCount, void* context, byte* buffer, uint* bufferSize);

    [DllImport("tdh.dll", ExactSpelling = true)]
    public static extern int TdhGetEventMapInformation(EventRecord* record, char* mapName, byte* buffer, uint* bufferSize);

    [DllImport("tdh.dll", ExactSpelling = true)]
    public static extern int TdhFormatProperty(
        byte* eventInfo,
        byte* mapInfo,
        uint pointerSize,
        ushort inType,
        ushort outType,
        ushort propertyLength,
        ushort userDataLength,
        byte* userData,
        uint* bufferSize,
        char* buffer,
        ushort* userDataConsumed);
}
