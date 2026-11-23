using System.Buffers.Binary;

namespace DaffyZoom.Tds;

/// <summary>Told about SMP frames as a MARS byte stream passes by.</summary>
internal interface ISmpListener
{
    /// <summary>A frame's header: SYN opens a channel, FIN closes it, ACK is flow control.</summary>
    void OnFrame(ushort channel, byte flags);

    /// <summary>Part or all of a DATA frame's payload - TDS bytes for that channel. Valid only during the call.</summary>
    void OnData(ushort channel, ReadOnlySpan<byte> payload);
}

/// <summary>
/// Follows MC-SMP, the framing MARS uses. With MARS on, after login every byte on the connection is
/// an SMP frame: a 16-byte header (SMID 0x53, flags, channel id, total length, sequence, window) and,
/// for DATA frames, a slice of one logical session's TDS stream. Several commands can then be in
/// flight at once, each on its own channel with its own strict request/response rhythm.
///
/// Like <see cref="PacketTracker"/> it copies nothing, holds nothing, and latches
/// <see cref="Desynced"/> rather than guess if the bytes stop making sense.
/// </summary>
internal sealed class SmpReader
{
    public const byte Syn = 0x01, Ack = 0x02, Fin = 0x04, Data = 0x08;
    private const byte Smid = 0x53;
    private const int HeaderLength = 16;

    private readonly byte[] _header = new byte[HeaderLength];
    private int _headerFill;
    private long _remaining;
    private bool _inPayload;
    private ushort _channel;
    private byte _flags;

    public bool Desynced { get; private set; }

    public void Feed(ReadOnlySpan<byte> data, ISmpListener listener)
    {
        while (!data.IsEmpty && !Desynced)
        {
            if (!_inPayload)
            {
                int take = Math.Min(HeaderLength - _headerFill, data.Length);
                data[..take].CopyTo(_header.AsSpan(_headerFill));
                _headerFill += take;
                data = data[take..];
                if (_headerFill < HeaderLength) return;
                _headerFill = 0;

                uint length = BinaryPrimitives.ReadUInt32LittleEndian(_header.AsSpan(4));
                if (_header[0] != Smid || length < HeaderLength)
                {
                    Desynced = true;
                    return;
                }

                _flags = _header[1];
                _channel = BinaryPrimitives.ReadUInt16LittleEndian(_header.AsSpan(2));
                _remaining = length - HeaderLength;
                listener.OnFrame(_channel, _flags);
                _inPayload = _remaining > 0;
                continue;
            }

            int chunk = (int)Math.Min(_remaining, data.Length);
            if ((_flags & Data) != 0) listener.OnData(_channel, data[..chunk]);
            _remaining -= chunk;
            data = data[chunk..];
            if (_remaining == 0) _inPayload = false;
        }
    }
}
