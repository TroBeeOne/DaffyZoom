namespace DaffyZoom.Tds;

/// <summary>A whole message, plus the exact packet bytes it arrived in.</summary>
internal sealed record ReceivedMessage(TdsMessage Message, byte[] RawPackets, ushort Spid);

/// <summary>
/// Reads whole TDS messages during the handshake - PRELOGIN, LOGIN7, the login response - where
/// DaffyZoom has to understand what it is looking at before deciding what to do.
///
/// Once the handshake is over this is retired and a plain byte relay takes the stream over, so
/// <see cref="Drain"/> hands back anything already read but not yet used. In practice that is
/// nothing - neither side speaks until the other has answered - but a relay that silently dropped
/// bytes would be a desync waiting to happen.
///
/// The raw packets are kept because the target's login response is relayed to the client exactly
/// as the target framed it, SPID and all.
/// </summary>
internal sealed class TdsMessageReader(Stream stream)
{
    private readonly PacketFramer _framer = new();
    private readonly MessageAssembler _assembler = new();
    private readonly Queue<TdsPacket> _packets = new();
    private readonly byte[] _buffer = new byte[16 * 1024];
    private readonly MemoryStream _raw = new();

    /// <summary>Replaced when a TDS 7.4 connection switches to TLS part-way through.</summary>
    public Stream Stream { get; set; } = stream;

    /// <summary>Null means the connection closed before a whole message arrived.</summary>
    public async Task<ReceivedMessage?> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            while (_packets.TryDequeue(out var packet))
            {
                AppendRaw(packet);
                if (_assembler.Push(packet) is { } message)
                {
                    var received = new ReceivedMessage(message, _raw.ToArray(), packet.Spid);
                    _raw.SetLength(0);
                    return received;
                }
            }

            int read = await Stream.ReadAsync(_buffer, cancellationToken);
            if (read == 0) return null;

            foreach (var packet in _framer.Push(_buffer.AsSpan(0, read))) _packets.Enqueue(packet);

            if (_framer.Desynced)
                throw new InvalidDataException("the peer sent bytes that are not a TDS packet");
        }
    }

    /// <summary>Everything read but not yet returned, as raw bytes, in wire order.</summary>
    public byte[] Drain()
    {
        var rest = new MemoryStream();
        rest.Write(_raw.ToArray());
        while (_packets.TryDequeue(out var packet)) WriteRaw(rest, packet);
        rest.Write(_framer.Drain());
        _raw.SetLength(0);
        return rest.ToArray();
    }

    private void AppendRaw(in TdsPacket packet) => WriteRaw(_raw, packet);

    /// <summary>Rebuilds the 8-byte header the framer took apart. The Window byte is always zero.</summary>
    private static void WriteRaw(MemoryStream target, in TdsPacket packet)
    {
        int length = packet.Payload.Length + TdsConstants.HeaderLength;
        target.WriteByte(packet.Type);
        target.WriteByte(packet.Status);
        target.WriteByte((byte)(length >> 8));
        target.WriteByte((byte)length);
        target.WriteByte((byte)(packet.Spid >> 8));
        target.WriteByte((byte)packet.Spid);
        target.WriteByte(packet.PacketId);
        target.WriteByte(0);
        target.Write(packet.Payload);
    }
}
