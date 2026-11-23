using System.Buffers.Binary;
using DaffyZoom.Tds;

namespace DaffyZoom.Proxy;

/// <summary>
/// Reads the client's side of a session one TDS packet at a time, so the relay can see where each
/// request begins - to answer daffyzoom.* commands itself - before anything is forwarded.
///
/// The socket is still read in large pieces, into a buffer the packets are then cut from, so
/// splitting the stream into packets costs no more reads than relaying it in chunks did. A packet is
/// handed on whole, exactly as the client framed it.
/// </summary>
internal sealed class ClientPacketReader
{
    /// <summary>A packet's length field is 16 bits, so no packet is longer than this.</summary>
    private const int MaxPacket = ushort.MaxValue;

    private readonly Stream _stream;
    private byte[] _buffer;
    private int _start, _end;

    /// <param name="early">Bytes the login handshake already read past LOGIN7: the start of the session's traffic.</param>
    public ClientPacketReader(Stream stream, ReadOnlySpan<byte> early = default)
    {
        _stream = stream;
        _buffer = new byte[Math.Max(2 * (MaxPacket + 1), early.Length + MaxPacket + 1)];
        early.CopyTo(_buffer);
        _end = early.Length;
    }

    /// <summary>
    /// The next packet, header included, or null when the client has closed the connection between
    /// packets. Valid until the next call: the buffer is reused.
    /// </summary>
    public async ValueTask<ReadOnlyMemory<byte>?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!await FillAsync(TdsConstants.HeaderLength, cancellationToken)) return null;

        int length = BinaryPrimitives.ReadUInt16BigEndian(_buffer.AsSpan(_start + 2));
        if (length < TdsConstants.HeaderLength)
            throw new InvalidDataException($"the client sent a packet {length} bytes long, shorter than its own header");

        if (!await FillAsync(length, cancellationToken))
            throw new EndOfStreamException("the client disconnected part-way through a packet");

        var packet = _buffer.AsMemory(_start, length);
        _start += length;
        return packet;
    }

    /// <summary>
    /// Makes sure <paramref name="needed"/> bytes from the current position are in the buffer.
    /// False when the stream ends before any of them arrive; a stream that ends part-way is the
    /// caller's to report.
    /// </summary>
    private async ValueTask<bool> FillAsync(int needed, CancellationToken cancellationToken)
    {
        while (_end - _start < needed)
        {
            if (_buffer.Length - _start < needed)
            {
                // Slide what is left to the front; the buffer holds two of the largest packet, so it fits.
                _buffer.AsSpan(_start, _end - _start).CopyTo(_buffer);
                _end -= _start;
                _start = 0;
            }

            int read = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken);
            if (read == 0)
            {
                if (_end == _start) return false;
                throw new EndOfStreamException("the client disconnected part-way through a packet");
            }
            _end += read;
        }
        return true;
    }
}
