using System.Buffers.Binary;

namespace DaffyZoom.Tds;

/// <summary>
/// The shim that makes TDS 7.4 encryption possible: during the TLS handshake it wraps everything
/// written in a PRELOGIN packet and strips the headers off everything read, then gets out of the
/// way for the rest of the connection.
///
/// TDS 8.0 is plain TLS from byte zero, which is why <see cref="System.Net.Security.SslStream"/>
/// can sit straight on the socket for Strict. 7.4 is not: its handshake records travel *inside*
/// TDS packets of type 0x12, and only once the handshake finishes do the layers invert and TDS
/// start riding inside TLS. So SslStream is handed this instead of the socket. Until
/// <see cref="StopFraming"/> is called it adds and removes the 8-byte headers; after that it is a
/// pass-through, and it stays in the chain for the life of the connection.
///
/// The client does the same thing on its side — Microsoft.Data.SqlClient calls its own version
/// SslOverTdsStream — so the shape here is the mirror of what it expects.
/// </summary>
internal sealed class PreLoginTlsStream(Stream inner) : Stream
{
    /// <summary>
    /// The handshake happens before LOGIN7, so no packet size has been negotiated yet and the
    /// 4096-byte default is all either side is entitled to assume.
    /// </summary>
    private const int MaxPayload = TdsConstants.DefaultPacketSize - TdsConstants.HeaderLength;

    private readonly byte[] _header = new byte[TdsConstants.HeaderLength];

    private bool _framing = true;
    private byte _packetId = 1;

    // The payload of the packet currently being fed to SslStream, and how much of it has gone.
    private byte[] _payload = [];
    private int _taken;

    /// <summary>
    /// Called once the handshake returns. From here TLS records go on the wire unwrapped, because
    /// that is what the client switches to at the same moment.
    /// </summary>
    public void StopFraming() => _framing = false;

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_framing) return await inner.ReadAsync(buffer, cancellationToken);
        if (buffer.IsEmpty) return 0;

        // A packet with no payload is legal and says nothing, so keep going until there are bytes
        // to hand back or the connection ends.
        while (_taken >= _payload.Length)
            if (!await ReadPacketAsync(cancellationToken)) return 0;

        int count = Math.Min(buffer.Length, _payload.Length - _taken);
        _payload.AsMemory(_taken, count).CopyTo(buffer);
        _taken += count;
        return count;
    }

    public override async ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!_framing)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            return;
        }

        if (buffer.IsEmpty) return;

        // Each call is one complete message. The client reads packets until one carries the
        // end-of-message bit, so a flight that is not terminated leaves it waiting forever.
        _packetId = 1;
        int offset = 0;

        while (true)
        {
            int chunk = Math.Min(MaxPayload, buffer.Length - offset);
            bool last = offset + chunk >= buffer.Length;

            var packet = new byte[TdsConstants.HeaderLength + chunk];
            packet[0] = TdsConstants.PacketType.PreLogin;
            packet[1] = last ? TdsConstants.PacketStatus.EndOfMessage : TdsConstants.PacketStatus.Normal;
            BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)packet.Length);
            packet[6] = _packetId;

            buffer.Span.Slice(offset, chunk).CopyTo(packet.AsSpan(TdsConstants.HeaderLength));
            await inner.WriteAsync(packet, cancellationToken);

            _packetId = unchecked((byte)(_packetId + 1));
            offset += chunk;
            if (last) break;
        }

        await inner.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Reads one whole TDS packet and makes its payload the current one. False means the
    /// connection closed mid-handshake, which SslStream reports as a failed authentication.
    /// </summary>
    private async ValueTask<bool> ReadPacketAsync(CancellationToken cancellationToken)
    {
        if (!await FillAsync(_header, cancellationToken)) return false;

        int length = BinaryPrimitives.ReadUInt16BigEndian(_header.AsSpan(2));   // big-endian, as always
        if (length < TdsConstants.HeaderLength)
            throw new IOException($"TLS handshake packet claims {length} bytes, shorter than a TDS header");

        _payload = new byte[length - TdsConstants.HeaderLength];
        _taken = 0;
        return _payload.Length == 0 || await FillAsync(_payload, cancellationToken);
    }

    /// <summary>
    /// One read is not one packet: a handshake record can be split across TCP reads and several
    /// can share one, so a header or payload is filled to its exact length before it is used.
    /// </summary>
    private async ValueTask<bool> FillAsync(Memory<byte> target, CancellationToken cancellationToken)
    {
        int filled = 0;
        while (filled < target.Length)
        {
            int read = await inner.ReadAsync(target[filled..], cancellationToken);
            if (read == 0) return false;
            filled += read;
        }

        return true;
    }

    // SslStream uses the async paths for an async handshake; these are here because Stream
    // requires them, and they hold no separate logic that could drift out of step.
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    /// <summary>The socket belongs to the session, not to this wrapper, so disposal stops here.</summary>
    protected override void Dispose(bool disposing) { }
}
