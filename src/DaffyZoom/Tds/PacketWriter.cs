namespace DaffyZoom.Tds;

/// <summary>
/// Frames outbound messages into TDS packets: splits at the negotiated packet size and sets the
/// EOM status bit only on the final packet. The header length field is big-endian.
/// </summary>
internal sealed class PacketWriter(Stream stream)
{
    private byte _packetId = 1;

    public int PacketSize { get; set; } = TdsConstants.DefaultPacketSize;

    public ushort Spid { get; set; } = 51;

    public async Task WriteMessageAsync(byte type, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        int maxPayload = PacketSize - TdsConstants.HeaderLength;
        int offset = 0;

        do
        {
            int chunk = Math.Min(maxPayload, payload.Length - offset);
            bool isLast = offset + chunk >= payload.Length;

            var packet = new byte[TdsConstants.HeaderLength + chunk];
            packet[0] = type;
            packet[1] = isLast ? TdsConstants.PacketStatus.EndOfMessage : TdsConstants.PacketStatus.Normal;
            packet[2] = (byte)((packet.Length >> 8) & 0xFF);   // big-endian length
            packet[3] = (byte)(packet.Length & 0xFF);
            packet[4] = (byte)((Spid >> 8) & 0xFF);            // big-endian SPID
            packet[5] = (byte)(Spid & 0xFF);
            packet[6] = _packetId;
            packet[7] = 0;

            payload.Span.Slice(offset, chunk).CopyTo(packet.AsSpan(TdsConstants.HeaderLength));

            await stream.WriteAsync(packet, cancellationToken);

            _packetId = unchecked((byte)(_packetId + 1));
            offset += chunk;

            if (isLast) break;
        }
        while (true);

        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// One message framed as packets in a single buffer, numbered from 1 - for a writer that has to
    /// show the bytes to someone before they go, as the relay shows its own replies to its observers.
    /// </summary>
    public static byte[] Frame(byte type, ReadOnlySpan<byte> payload, int packetSize, ushort spid)
    {
        int maxPayload = packetSize - TdsConstants.HeaderLength;
        int packets = Math.Max(1, (payload.Length + maxPayload - 1) / maxPayload);
        var framed = new byte[payload.Length + packets * TdsConstants.HeaderLength];

        int offset = 0, at = 0;
        for (int i = 0; i < packets; i++)
        {
            int chunk = Math.Min(maxPayload, payload.Length - offset);
            int length = TdsConstants.HeaderLength + chunk;
            var packet = framed.AsSpan(at, length);
            packet[0] = type;
            packet[1] = i == packets - 1 ? TdsConstants.PacketStatus.EndOfMessage : TdsConstants.PacketStatus.Normal;
            packet[2] = (byte)(length >> 8);
            packet[3] = (byte)length;
            packet[4] = (byte)(spid >> 8);
            packet[5] = (byte)spid;
            packet[6] = unchecked((byte)(i + 1));
            packet[7] = 0;
            payload.Slice(offset, chunk).CopyTo(packet[TdsConstants.HeaderLength..]);
            offset += chunk;
            at += length;
        }

        return framed;
    }

    /// <summary>Each response message restarts packet numbering at 1.</summary>
    public void ResetPacketId() => _packetId = 1;
}
