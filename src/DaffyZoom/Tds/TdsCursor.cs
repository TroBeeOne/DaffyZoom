using System.Buffers.Binary;
using System.Text;

namespace DaffyZoom.Tds;

/// <summary>
/// Forward-only reader over a TDS payload. Everything in TDS is little-endian except the packet
/// header length/SPID, so LE is the default here and BE is the explicitly-named case.
/// </summary>
internal ref struct TdsCursor(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;

    public int Position { get; private set; }

    public readonly int Remaining => _data.Length - Position;
    public readonly bool IsEmpty => Remaining <= 0;
    public readonly ReadOnlySpan<byte> Rest => _data[Position..];

    public void Seek(int position) => Position = position;
    public void Skip(int count) => Position += count;

    public byte ReadByte() => _data[Position++];

    public ushort ReadUInt16()
    {
        ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_data[Position..]);
        Position += 2;
        return value;
    }

    public ushort ReadUInt16BigEndian()
    {
        ushort value = BinaryPrimitives.ReadUInt16BigEndian(_data[Position..]);
        Position += 2;
        return value;
    }

    public uint ReadUInt32()
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(_data[Position..]);
        Position += 4;
        return value;
    }

    public int ReadInt32()
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(_data[Position..]);
        Position += 4;
        return value;
    }

    public ulong ReadUInt64()
    {
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(_data[Position..]);
        Position += 8;
        return value;
    }

    public ReadOnlySpan<byte> ReadBytes(int count)
    {
        var slice = _data.Slice(Position, count);
        Position += count;
        return slice;
    }

    /// <summary>B_VARCHAR: 1-byte character count, then UTF-16LE.</summary>
    public string ReadBVarChar()
    {
        int chars = ReadByte();
        return Encoding.Unicode.GetString(ReadBytes(chars * 2));
    }

    /// <summary>US_VARCHAR: 2-byte character count, then UTF-16LE.</summary>
    public string ReadUsVarChar()
    {
        int chars = ReadUInt16();
        return Encoding.Unicode.GetString(ReadBytes(chars * 2));
    }

    /// <summary>B_VARBYTE: 1-byte byte count, then raw bytes.</summary>
    public ReadOnlySpan<byte> ReadBVarByte() => ReadBytes(ReadByte());

    public readonly string PeekString(int offset, int charCount) =>
        Encoding.Unicode.GetString(_data.Slice(offset, charCount * 2));

    public readonly ReadOnlySpan<byte> Slice(int offset, int length) => _data.Slice(offset, length);
}
