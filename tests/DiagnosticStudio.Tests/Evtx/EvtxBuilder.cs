using System.Text;
using DiagnosticStudio.Parsers.Evtx;

namespace DiagnosticStudio.Tests.Evtx;

internal sealed record TestEvent(
    long RecordId,
    string Provider,
    uint EventId,
    byte Level,
    DateTime TimeUtc,
    string Computer,
    string Param1 = "",
    string Param2 = "");

/// <summary>
/// Writes small, valid EVTX files: file header, 64 KiB chunks, template-based records with inline and shared
/// template definitions. Used to test the reader without any real log data.
/// </summary>
internal sealed class EvtxBuilder
{
    private const int ChunkSize = 65536;
    private const string EventNamespace = "http://schemas.microsoft.com/win/2004/08/events/event";

    public List<long> RecordFileOffsets { get; } = new();
    public List<long> ChunkFileOffsets { get; } = new();

    public byte[] Build(IReadOnlyList<TestEvent> events, bool dirty = false, bool reverseChunkOrder = false)
    {
        var chunks = new List<byte[]>();
        var recordLocations = new List<(int Chunk, int Offset)>();

        var writer = new Writer(ChunkSize);
        var templateOffset = -1;
        var firstId = 0L;
        var lastId = 0L;
        var count = 0;

        void Finish()
        {
            if (count == 0)
            {
                return;
            }

            var chunk = writer.Buffer;
            WriteChunkHeader(chunk, firstId, lastId, writer.Position);
            chunks.Add(chunk);
            writer = new Writer(ChunkSize);
            templateOffset = -1;
            count = 0;
        }

        writer.Position = 512;
        foreach (var ev in events)
        {
            var saved = writer.Position;
            var savedTemplate = templateOffset;
            try
            {
                var start = writer.Position;
                templateOffset = WriteRecord(writer, ev, templateOffset);
                if (count == 0)
                {
                    firstId = ev.RecordId;
                }

                lastId = ev.RecordId;
                recordLocations.Add((chunks.Count, start));
                count++;
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException)
            {
                // Chunk full: close it and write this record at the start of a new one.
                writer.Position = saved;
                templateOffset = savedTemplate;
                Finish();
                writer.Position = 512;
                var start = writer.Position;
                templateOffset = WriteRecord(writer, ev, -1);
                firstId = ev.RecordId;
                lastId = ev.RecordId;
                recordLocations.Add((chunks.Count, start));
                count = 1;
            }
        }

        Finish();

        var order = Enumerable.Range(0, chunks.Count).ToList();
        if (reverseChunkOrder)
        {
            order.Reverse();
        }

        var file = new byte[4096 + (chunks.Count * ChunkSize)];
        WriteFileHeader(file, chunks.Count, events.Count == 0 ? 1 : events.Max(e => e.RecordId) + 1, dirty);
        ChunkFileOffsets.Clear();
        for (var i = 0; i < chunks.Count; i++)
        {
            ChunkFileOffsets.Add(0);
        }

        for (var slot = 0; slot < order.Count; slot++)
        {
            var chunkIndex = order[slot];
            var offset = 4096L + ((long)slot * ChunkSize);
            Buffer.BlockCopy(chunks[chunkIndex], 0, file, (int)offset, ChunkSize);
            ChunkFileOffsets[chunkIndex] = offset;
        }

        RecordFileOffsets.Clear();
        foreach (var (chunk, offset) in recordLocations)
        {
            RecordFileOffsets.Add(ChunkFileOffsets[chunk] + offset);
        }

        return file;
    }

    // ---- records ----

    private static int WriteRecord(Writer w, TestEvent ev, int templateOffset)
    {
        var start = w.Position;
        w.U32(0x00002A2A);
        var sizeAt = w.Position;
        w.U32(0);
        w.U64((ulong)ev.RecordId);
        w.U64((ulong)ev.TimeUtc.ToFileTimeUtc());

        // binary XML: fragment header, template instance
        w.U8(0x0F);
        w.U8(1);
        w.U8(1);
        w.U8(0);
        w.U8(0x0C);
        w.U8(1);
        w.U32(1);
        var definitionField = w.Position;
        w.U32(0);

        if (templateOffset < 0)
        {
            // Inline definition directly after the offset field.
            templateOffset = w.Position;
            w.PatchU32(definitionField, (uint)templateOffset);
            w.U32(0);
            w.Bytes(new byte[16]);
            var bodySizeAt = w.Position;
            w.U32(0);
            var bodyStart = w.Position;
            WriteTemplateBody(w);
            w.PatchU32(bodySizeAt, (uint)(w.Position - bodyStart));
        }
        else
        {
            w.PatchU32(definitionField, (uint)templateOffset);
        }

        WriteValues(w, ev);

        // pad to 8 bytes, then the trailing size copy
        var size = w.Position - start + 4;
        size = (size + 7) & ~7;
        while (w.Position - start < size - 4)
        {
            w.U8(0);
        }

        w.U32((uint)size);
        w.PatchU32(sizeAt, (uint)size);
        return templateOffset;
    }

    private static void WriteTemplateBody(Writer w)
    {
        w.U8(0x0F);
        w.U8(1);
        w.U8(1);
        w.U8(0);

        Element(w, "Event", () => Attr(w, "xmlns", () => Text(w, EventNamespace)), () =>
        {
            Element(w, "System", null, () =>
            {
                Element(w, "Provider", () => Attr(w, "Name", () => Subst(w, 0, 0x01)), null);
                Element(w, "EventID", null, () => Subst(w, 1, 0x06));
                Element(w, "Level", null, () => Subst(w, 2, 0x04));
                Element(w, "TimeCreated", () => Attr(w, "SystemTime", () => Subst(w, 3, 0x11)), null);
                Element(w, "EventRecordID", null, () => Subst(w, 4, 0x0A));
                Element(w, "Computer", null, () => Subst(w, 5, 0x01));
            });
            Element(w, "EventData", null, () =>
            {
                Element(w, "Data", () => Attr(w, "Name", () => Text(w, "Param1")), () => Subst(w, 6, 0x01));
                Element(w, "Data", () => Attr(w, "Name", () => Text(w, "Param2")), () => Subst(w, 7, 0x01));
            });
        });
        w.U8(0x00);
    }

    private static void WriteValues(Writer w, TestEvent ev)
    {
        var values = new (byte Type, byte[] Data)[]
        {
            (0x01, Encoding.Unicode.GetBytes(ev.Provider)),
            (0x06, BitConverter.GetBytes((ushort)ev.EventId)),
            (0x04, new[] { ev.Level }),
            (0x11, BitConverter.GetBytes((ulong)ev.TimeUtc.ToFileTimeUtc())),
            (0x0A, BitConverter.GetBytes((ulong)ev.RecordId)),
            (0x01, Encoding.Unicode.GetBytes(ev.Computer)),
            (0x01, Encoding.Unicode.GetBytes(ev.Param1)),
            (0x01, Encoding.Unicode.GetBytes(ev.Param2)),
        };

        w.U32((uint)values.Length);
        foreach (var (type, data) in values)
        {
            w.U16((ushort)data.Length);
            w.U8(type);
            w.U8(0);
        }

        foreach (var (_, data) in values)
        {
            w.Bytes(data);
        }
    }

    // ---- binary XML tokens ----

    private static void Element(Writer w, string name, Action? attributes, Action? content)
    {
        w.U8(attributes is null ? (byte)0x01 : (byte)0x41);
        w.U16(0xFFFF);
        w.U32(0);
        Name(w, name);
        if (attributes is not null)
        {
            var sizeAt = w.Position;
            w.U32(0);
            var start = w.Position;
            attributes();
            w.PatchU32(sizeAt, (uint)(w.Position - start));
        }

        if (content is null)
        {
            w.U8(0x03);
            return;
        }

        w.U8(0x02);
        content();
        w.U8(0x04);
    }

    private static void Attr(Writer w, string name, Action value)
    {
        w.U8(0x06);
        Name(w, name);
        value();
    }

    private static void Name(Writer w, string name)
    {
        w.U32((uint)(w.Position + 4)); // inline: the name structure follows this field
        w.U32(0);
        w.U16(0);
        w.U16((ushort)name.Length);
        w.Bytes(Encoding.Unicode.GetBytes(name));
        w.U16(0);
    }

    private static void Text(Writer w, string text)
    {
        w.U8(0x05);
        w.U8(0x01);
        w.U16((ushort)text.Length);
        w.Bytes(Encoding.Unicode.GetBytes(text));
    }

    private static void Subst(Writer w, int index, byte type)
    {
        w.U8(0x0D);
        w.U16((ushort)index);
        w.U8(type);
    }

    // ---- headers ----

    private static void WriteChunkHeader(byte[] chunk, long firstId, long lastId, int freeSpace)
    {
        "ElfChnk\0"u8.CopyTo(chunk);
        BitConverter.GetBytes((ulong)firstId).CopyTo(chunk, 0x08);
        BitConverter.GetBytes((ulong)lastId).CopyTo(chunk, 0x10);
        BitConverter.GetBytes((ulong)firstId).CopyTo(chunk, 0x18);
        BitConverter.GetBytes((ulong)lastId).CopyTo(chunk, 0x20);
        BitConverter.GetBytes(128u).CopyTo(chunk, 0x28);
        BitConverter.GetBytes((uint)freeSpace).CopyTo(chunk, 0x30);
        BitConverter.GetBytes(Crc32.Compute(chunk.AsSpan(512, freeSpace - 512))).CopyTo(chunk, 0x34);
        var headerCrc = Crc32.Compute(chunk.AsSpan(0, 0x78));
        headerCrc = Crc32.Compute(chunk.AsSpan(0x80, 512 - 0x80), headerCrc);
        BitConverter.GetBytes(headerCrc).CopyTo(chunk, 0x7C);
    }

    private static void WriteFileHeader(byte[] file, int chunkCount, long nextRecordId, bool dirty)
    {
        "ElfFile\0"u8.CopyTo(file);
        BitConverter.GetBytes(0UL).CopyTo(file, 0x08);
        BitConverter.GetBytes((ulong)Math.Max(0, chunkCount - 1)).CopyTo(file, 0x10);
        BitConverter.GetBytes((ulong)nextRecordId).CopyTo(file, 0x18);
        BitConverter.GetBytes(128u).CopyTo(file, 0x20);
        BitConverter.GetBytes((ushort)1).CopyTo(file, 0x24);
        BitConverter.GetBytes((ushort)3).CopyTo(file, 0x26);
        BitConverter.GetBytes((ushort)4096).CopyTo(file, 0x28);
        BitConverter.GetBytes((ushort)chunkCount).CopyTo(file, 0x2A);
        BitConverter.GetBytes(dirty ? 1u : 0u).CopyTo(file, 0x78);
        BitConverter.GetBytes(Crc32.Compute(file.AsSpan(0, 120))).CopyTo(file, 0x7C);
    }

    private sealed class Writer
    {
        public Writer(int size)
        {
            Buffer = new byte[size];
        }

        public byte[] Buffer { get; }
        public int Position { get; set; }

        public void U8(byte value) => Buffer[Position++] = value;

        public void U16(ushort value)
        {
            BitConverter.GetBytes(value).CopyTo(Buffer, Position);
            Position += 2;
        }

        public void U32(uint value)
        {
            BitConverter.GetBytes(value).CopyTo(Buffer, Position);
            Position += 4;
        }

        public void U64(ulong value)
        {
            BitConverter.GetBytes(value).CopyTo(Buffer, Position);
            Position += 8;
        }

        public void Bytes(byte[] value)
        {
            value.CopyTo(Buffer, Position);
            Position += value.Length;
        }

        public void PatchU32(int at, uint value) => BitConverter.GetBytes(value).CopyTo(Buffer, at);
    }
}
