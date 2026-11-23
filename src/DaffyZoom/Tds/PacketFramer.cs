using System.Buffers.Binary;

namespace DaffyZoom.Tds;

/// <summary>One complete TDS packet, header fields already decoded.</summary>
internal readonly record struct TdsPacket(byte Type, byte Status, ushort Spid, byte PacketId, byte[] Payload)
{
    public bool IsEndOfMessage => (Status & TdsConstants.PacketStatus.EndOfMessage) != 0;
}

/// <summary>
/// Reassembles a byte stream into TDS packets. A single TCP read may contain part of a packet,
/// exactly one, or several — never assume one read equals one packet.
///
/// If the stream turns out to be TLS (which happens when a real SQL Server negotiates login-packet
/// encryption) the type byte stops being a valid TDS packet type. We detect that, latch
/// <see cref="Desynced"/>, and stop pretending to frame.
/// </summary>
internal sealed class PacketFramer
{
    private byte[] _buffer = new byte[16 * 1024];
    private int _length;

    public bool Desynced { get; private set; }

    public List<TdsPacket> Push(ReadOnlySpan<byte> chunk)
    {
        var packets = new List<TdsPacket>();
        if (Desynced) return packets;

        Append(chunk);

        int consumed = 0;
        while (_length - consumed >= TdsConstants.HeaderLength)
        {
            var span = _buffer.AsSpan(consumed, _length - consumed);

            byte type = span[0];
            byte status = span[1];
            ushort packetLength = BinaryPrimitives.ReadUInt16BigEndian(span[2..]); // BIG-endian, the odd one out
            ushort spid = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);
            byte packetId = span[6];

            if (!IsKnownPacketType(type) || packetLength < TdsConstants.HeaderLength)
            {
                Desynced = true;
                break;
            }

            if (span.Length < packetLength) break; // wait for the rest

            byte[] payload = span[TdsConstants.HeaderLength..packetLength].ToArray();
            packets.Add(new TdsPacket(type, status, spid, packetId, payload));
            consumed += packetLength;
        }

        Consume(consumed);
        return packets;
    }

    /// <summary>Bytes still buffered — non-zero after Desynced latches means unframed remainder.</summary>
    public int Pending => _length;

    /// <summary>
    /// Hands back whatever is buffered but not yet framed, and forgets it. Used where a handshake
    /// finishes and a byte relay takes over the stream: anything already read belongs to the relay.
    /// </summary>
    public byte[] Drain()
    {
        byte[] rest = _buffer.AsSpan(0, _length).ToArray();
        _length = 0;
        return rest;
    }

    private static bool IsKnownPacketType(byte type) => type
        is TdsConstants.PacketType.SqlBatch
        or TdsConstants.PacketType.PreTds7Login
        or TdsConstants.PacketType.Rpc
        or TdsConstants.PacketType.TabularResult
        or TdsConstants.PacketType.Attention
        or TdsConstants.PacketType.BulkLoad
        or TdsConstants.PacketType.FedAuthToken
        or TdsConstants.PacketType.TransactionManager
        or TdsConstants.PacketType.Login7
        or TdsConstants.PacketType.Sspi
        or TdsConstants.PacketType.PreLogin;

    private void Append(ReadOnlySpan<byte> chunk)
    {
        if (_length + chunk.Length > _buffer.Length)
        {
            int size = _buffer.Length;
            while (size < _length + chunk.Length) size *= 2;
            Array.Resize(ref _buffer, size);
        }
        chunk.CopyTo(_buffer.AsSpan(_length));
        _length += chunk.Length;
    }

    private void Consume(int count)
    {
        if (count == 0) return;
        Array.Copy(_buffer, count, _buffer, 0, _length - count);
        _length -= count;
    }
}

/// <summary>
/// Joins packets into logical messages. A message ends at the packet whose status has the EOM bit
/// set, so a large LOGIN7 or result set spanning several packets arrives here as one payload.
/// </summary>
internal sealed class MessageAssembler
{
    private readonly MemoryStream _payload = new();
    private byte _type;
    private int _packetCount;

    /// <summary>Returns a complete message when the EOM bit arrives, otherwise null.</summary>
    public TdsMessage? Push(in TdsPacket packet)
    {
        if (_packetCount == 0) _type = packet.Type;
        _payload.Write(packet.Payload);
        _packetCount++;

        if (!packet.IsEndOfMessage) return null;

        var message = new TdsMessage(_type, _payload.ToArray(), _packetCount, packet.Status);
        _payload.SetLength(0);
        _packetCount = 0;
        return message;
    }
}

internal readonly record struct TdsMessage(byte Type, byte[] Payload, int PacketCount, byte FinalStatus);
