using System.Buffers.Binary;
using System.Text;

namespace DaffyZoom.Tds;

/// <summary>Little-endian payload builder. Mirrors <see cref="TdsCursor"/>.</summary>
internal sealed class TdsWriter
{
    private byte[] _buffer = new byte[256];
    private int _length;

    public int Length => _length;

    public void WriteByte(byte value)
    {
        Ensure(1);
        _buffer[_length++] = value;
    }

    public void WriteUInt16(ushort value)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_length), value);
        _length += 2;
    }

    public void WriteUInt16BigEndian(ushort value)
    {
        Ensure(2);
        BinaryPrimitives.WriteUInt16BigEndian(_buffer.AsSpan(_length), value);
        _length += 2;
    }

    public void WriteUInt32(uint value)
    {
        Ensure(4);
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    public void WriteInt32(int value)
    {
        Ensure(4);
        BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_length), value);
        _length += 4;
    }

    public void WriteUInt64(ulong value)
    {
        Ensure(8);
        BinaryPrimitives.WriteUInt64LittleEndian(_buffer.AsSpan(_length), value);
        _length += 8;
    }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        Ensure(value.Length);
        value.CopyTo(_buffer.AsSpan(_length));
        _length += value.Length;
    }

    /// <summary>B_VARCHAR: 1-byte character count, then UTF-16LE.</summary>
    public void WriteBVarChar(string value)
    {
        WriteByte((byte)value.Length);
        WriteBytes(Encoding.Unicode.GetBytes(value));
    }

    /// <summary>US_VARCHAR: 2-byte character count, then UTF-16LE.</summary>
    public void WriteUsVarChar(string value)
    {
        WriteUInt16((ushort)value.Length);
        WriteBytes(Encoding.Unicode.GetBytes(value));
    }

    /// <summary>Reserves a 2-byte little-endian length slot to be patched once the body is written.</summary>
    public int ReserveUInt16()
    {
        int position = _length;
        WriteUInt16(0);
        return position;
    }

    public void PatchUInt16(int position, ushort value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(position), value);

    public void PatchUInt32(int position, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(position), value);

    /// <summary>Patches a reserved slot with the number of bytes written after it.</summary>
    public void PatchLengthFrom(int reservedPosition) =>
        PatchUInt16(reservedPosition, (ushort)(_length - reservedPosition - 2));

    public byte[] ToArray() => _buffer.AsSpan(0, _length).ToArray();

    public ReadOnlyMemory<byte> AsMemory() => _buffer.AsMemory(0, _length);

    private void Ensure(int extra)
    {
        if (_length + extra <= _buffer.Length) return;
        int size = _buffer.Length;
        while (size < _length + extra) size *= 2;
        Array.Resize(ref _buffer, size);
    }
}
