using System.Text;

namespace DiagnosticStudio.Tests.Ingestion;

/// <summary>
/// Writes small, valid cabinets with no compression and a single folder. Because the writer controls every name and
/// header field, tests can produce cabinets no real tool would: traversal names, forged sizes, multi-cabinet flags.
/// </summary>
internal sealed class CabBuilder
{
    private const int BlockSize = 32768;
    private readonly List<(string Name, byte[] Data, bool Utf8, int? DeclaredSize)> _entries = new();

    /// <summary>Marks the cabinet as continuing in a following one (adds the header fields that requires).</summary>
    public bool HasNext { get; set; }

    public CabBuilder Add(string name, byte[] data, bool utf8Name = false, int? declaredSize = null)
    {
        _entries.Add((name, data, utf8Name, declaredSize));
        return this;
    }

    public CabBuilder Add(string name, string text) => Add(name, Encoding.UTF8.GetBytes(text));

    public byte[] Build()
    {
        var stored = _entries.SelectMany(e => e.Data).ToArray();
        var blocks = new List<byte[]>();
        for (var offset = 0; offset < stored.Length; offset += BlockSize)
        {
            blocks.Add(stored[offset..Math.Min(stored.Length, offset + BlockSize)]);
        }

        var nextFields = HasNext ? Encoding.ASCII.GetBytes("next.cab\0disk2\0") : Array.Empty<byte>();
        var fileTable = new MemoryStream();
        var position = 0;
        foreach (var (name, data, utf8, declared) in _entries)
        {
            var nameBytes = utf8 ? Encoding.UTF8.GetBytes(name) : Encoding.Latin1.GetBytes(name);
            fileTable.Write(BitConverter.GetBytes((uint)(declared ?? data.Length)));
            fileTable.Write(BitConverter.GetBytes((uint)position));
            fileTable.Write(BitConverter.GetBytes((ushort)0));                       // folder
            fileTable.Write(BitConverter.GetBytes((ushort)(((2026 - 1980) << 9) | (7 << 5) | 23)));
            fileTable.Write(BitConverter.GetBytes((ushort)((14 << 11) | (12 << 5))));
            fileTable.Write(BitConverter.GetBytes((ushort)(utf8 ? 0x80 : 0x20)));    // attribs
            fileTable.Write(nameBytes);
            fileTable.WriteByte(0);
            position += data.Length;
        }

        var headerSize = 36 + nextFields.Length;
        var coffFiles = headerSize + 8;
        var coffData = coffFiles + (int)fileTable.Length;
        var dataSize = blocks.Sum(b => 8 + b.Length);
        var total = coffData + dataSize;

        var output = new MemoryStream();
        output.Write(Encoding.ASCII.GetBytes("MSCF"));
        output.Write(BitConverter.GetBytes(0u));
        output.Write(BitConverter.GetBytes((uint)total));
        output.Write(BitConverter.GetBytes(0u));
        output.Write(BitConverter.GetBytes((uint)coffFiles));
        output.Write(BitConverter.GetBytes(0u));
        output.WriteByte(3);                                                         // minor
        output.WriteByte(1);                                                         // major
        output.Write(BitConverter.GetBytes((ushort)1));                              // folders
        output.Write(BitConverter.GetBytes((ushort)_entries.Count));
        output.Write(BitConverter.GetBytes((ushort)(HasNext ? 0x0002 : 0)));         // flags
        output.Write(BitConverter.GetBytes((ushort)1234));                           // set id
        output.Write(BitConverter.GetBytes((ushort)0));                              // cabinet index
        output.Write(nextFields);

        output.Write(BitConverter.GetBytes((uint)coffData));                         // folder: first data block
        output.Write(BitConverter.GetBytes((ushort)blocks.Count));
        output.Write(BitConverter.GetBytes((ushort)0));                              // no compression

        output.Write(fileTable.ToArray());

        foreach (var block in blocks)
        {
            output.Write(BitConverter.GetBytes(0u));                                 // checksum 0 = not computed
            output.Write(BitConverter.GetBytes((ushort)block.Length));
            output.Write(BitConverter.GetBytes((ushort)block.Length));
            output.Write(block);
        }

        return output.ToArray();
    }
}
