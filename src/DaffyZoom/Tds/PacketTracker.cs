using System.Buffers.Binary;

namespace DaffyZoom.Tds;

/// <summary>A packet header, decoded.</summary>
internal readonly record struct PacketHeader(byte Type, byte Status, int Length, ushort Spid, byte PacketId)
{
    public bool IsEndOfMessage => (Status & TdsConstants.PacketStatus.EndOfMessage) != 0;
    public bool ResetsConnection => (Status & (TdsConstants.PacketStatus.ResetConnection | TdsConstants.PacketStatus.ResetConnectionSkipTran)) != 0;
    public int PayloadLength => Length - TdsConstants.HeaderLength;
}

/// <summary>Told about packets as a relayed byte stream passes by.</summary>
internal interface IPacketListener
{
    void OnPacketStart(in PacketHeader header);

    /// <summary>Some or all of the current packet's payload. Only valid for the duration of the call.</summary>
    void OnPacketPayload(ReadOnlySpan<byte> payload);

    void OnPacketEnd(in PacketHeader header);
}

/// <summary>
/// Follows TDS packet boundaries through a byte stream without holding on to it.
///
/// The relay forwards whatever each socket read returns - part of a packet, several, a header split
/// down the middle - and only then shows the same bytes to this tracker, which works out where each
/// packet starts and ends and hands the payload through as spans of the relay's own buffer. It
/// copies nothing and allocates nothing; whoever listens decides what, if anything, to keep.
///
/// If the bytes stop looking like TDS it latches <see cref="Desynced"/> and goes quiet. That
/// affects only what DaffyZoom observes, never what it relays.
/// </summary>
internal sealed class PacketTracker
{
    private readonly byte[] _header = new byte[TdsConstants.HeaderLength];
    private int _headerFill;
    private int _payloadRemaining;
    private bool _inPayload;
    private PacketHeader _current;

    public bool Desynced { get; private set; }

    public void Feed(ReadOnlySpan<byte> data, IPacketListener listener)
    {
        while (!data.IsEmpty && !Desynced)
        {
            if (!_inPayload)
            {
                int take = Math.Min(TdsConstants.HeaderLength - _headerFill, data.Length);
                data[..take].CopyTo(_header.AsSpan(_headerFill));
                _headerFill += take;
                data = data[take..];

                if (_headerFill < TdsConstants.HeaderLength) return;
                _headerFill = 0;

                int length = BinaryPrimitives.ReadUInt16BigEndian(_header.AsSpan(2));   // big-endian
                if (length < TdsConstants.HeaderLength || !IsTdsPacketType(_header[0]))
                {
                    Desynced = true;
                    return;
                }

                _current = new PacketHeader(_header[0], _header[1], length,
                    BinaryPrimitives.ReadUInt16BigEndian(_header.AsSpan(4)), _header[6]);
                _payloadRemaining = length - TdsConstants.HeaderLength;
                _inPayload = true;
                listener.OnPacketStart(_current);
            }

            if (_payloadRemaining > 0 && !data.IsEmpty)
            {
                int take = Math.Min(_payloadRemaining, data.Length);
                listener.OnPacketPayload(data[..take]);
                _payloadRemaining -= take;
                data = data[take..];
            }

            if (_payloadRemaining == 0)
            {
                _inPayload = false;
                listener.OnPacketEnd(_current);
            }
        }
    }

    private static bool IsTdsPacketType(byte type) => type
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
}
