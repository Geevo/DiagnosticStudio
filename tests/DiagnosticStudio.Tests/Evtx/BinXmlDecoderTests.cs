using System.Text;
using DiagnosticStudio.Parsers.Evtx;

namespace DiagnosticStudio.Tests.Evtx;

public sealed class BinXmlDecoderTests
{
    private const int Origin = 0x100;

    // Bytes that will sit at a known place in a chunk, so the offsets they hold (names, the template) can be worked out.
    private sealed class Stream(int origin)
    {
        public List<byte> Bytes { get; } = new();

        public int At => origin + Bytes.Count;

        public void Raw(params byte[] bytes) => Bytes.AddRange(bytes);

        public void U16(int value) => Bytes.AddRange(BitConverter.GetBytes((ushort)value));

        public void U32(int value) => Bytes.AddRange(BitConverter.GetBytes(value));

        // A name stored where it is used: its offset points just past the offset itself.
        public void Name(string name)
        {
            U32(At + 4);
            U32(0);
            U16(0);
            U16(name.Length);
            Bytes.AddRange(Encoding.Unicode.GetBytes(name));
            U16(0);
        }

        public void Text(string text)
        {
            Raw(0x05, 0x01);
            U16(text.Length);
            Bytes.AddRange(Encoding.Unicode.GetBytes(text));
        }
    }

    // <Data k="v"/> as some providers write it inside a template value: no fragment header, and an element header
    // of a token and the data size, without a dependency id.
    private static byte[] CompactFragment(int origin)
    {
        var f = new Stream(origin);
        f.Raw(0x41);
        f.U32(0);
        f.Name("Data");
        var attributesStart = f.Bytes.Count;
        f.U32(0);
        f.Raw(0x06);
        f.Name("k");
        f.Text("v");
        var attributesLength = f.Bytes.Count - attributesStart - 4;
        f.Raw(0x03, 0x00);

        var array = f.Bytes.ToArray();
        BitConverter.GetBytes(attributesLength).CopyTo(array, attributesStart);
        BitConverter.GetBytes(array.Length - 5 - 1).CopyTo(array, 1);
        return array;
    }

    // A record that is a template, <Root> holding one substitution, whose value is the fragment above.
    private static byte[] Record()
    {
        var length = CompactFragment(0).Length;
        var s = new Stream(Origin);
        s.Raw(0x0F, 0x01, 0x01, 0x00);
        s.Raw(0x0C, 0x01);
        s.U32(1); // template id
        s.U32(s.At + 4); // the definition follows
        s.U32(0); // next definition
        s.Raw(new byte[16]); // guid

        var body = new Stream(s.At + 4);
        body.Raw(0x0F, 0x01, 0x01, 0x00);
        body.Raw(0x01, 0xFF, 0xFF);
        body.U32(0);
        body.Name("Root");
        body.Raw(0x02, 0x0D);
        body.U16(0);
        body.Raw(0x21, 0x04, 0x00);
        s.U32(body.Bytes.Count);
        s.Bytes.AddRange(body.Bytes);

        s.U32(1); // one value
        s.U16(length);
        s.Raw(0x21, 0x00);
        s.Bytes.AddRange(CompactFragment(s.At));
        return s.Bytes.ToArray();
    }

    [Fact]
    public void A_nested_fragment_whose_elements_have_no_dependency_id_is_decoded()
    {
        var record = Record();
        var chunk = new byte[0x10000];
        record.CopyTo(chunk, Origin);

        var root = new BinXmlDecoder(chunk).DecodeRecord(Origin, Origin + record.Length);

        Assert.Equal("Root", root.Name);
        var data = Assert.Single(root.Elements);
        Assert.Equal("Data", data.Name);
        Assert.Equal("v", data.Attribute("k"));
    }
}
