using DaffyZoom.Tds;

namespace DaffyZoom.Tests;

public class PreLoginTests
{
    [Fact]
    public void ServerResponseMirrorsExactlyTheOptionsAsked()
    {
        // As ODBC sends it: no FEDAUTHREQUIRED, so none must come back.
        var asked = new List<(byte, byte[])>
        {
            (TdsConstants.PreLoginOption.Version, [17, 0, 0, 0, 0, 0]),
            (TdsConstants.PreLoginOption.Encryption, [TdsConstants.Encryption.On]),
            (TdsConstants.PreLoginOption.InstOpt, [0x00]),
            (TdsConstants.PreLoginOption.ThreadId, [1, 2, 3, 4]),
            (TdsConstants.PreLoginOption.Mars, [1]),
            (TdsConstants.PreLoginOption.TraceId, new byte[36]),
        };

        var answer = PreLogin.Parse(PreLogin.BuildServerResponse(asked, TdsConstants.Encryption.On, mars: true));

        Assert.Equal(asked.Select(a => a.Item1), answer.Select(a => a.Option));
        Assert.Equal(TdsConstants.Encryption.On, PreLogin.Value(answer, TdsConstants.PreLoginOption.Encryption));
        Assert.Equal((byte)1, PreLogin.Value(answer, TdsConstants.PreLoginOption.Mars));
        Assert.Empty(answer.Single(a => a.Option == TdsConstants.PreLoginOption.ThreadId).Data);
        Assert.Empty(answer.Single(a => a.Option == TdsConstants.PreLoginOption.TraceId).Data);
        Assert.False(PreLogin.Has(answer, TdsConstants.PreLoginOption.FedAuthRequired));
    }

    [Fact]
    public void ClientRequestCarriesEncryptionAndMars()
    {
        var request = PreLogin.Parse(PreLogin.BuildClientRequest(TdsConstants.Encryption.On, mars: true));
        Assert.Equal(TdsConstants.Encryption.On, PreLogin.Value(request, TdsConstants.PreLoginOption.Encryption));
        Assert.Equal((byte)1, PreLogin.Value(request, TdsConstants.PreLoginOption.Mars));
        Assert.Equal(6, request.Single(o => o.Option == TdsConstants.PreLoginOption.Version).Data.Length);
    }
}

public class PacketTrackerTests
{
    private sealed class Recorder : IPacketListener
    {
        public List<(byte Type, bool Eom, byte[] Payload)> Packets { get; } = [];
        private MemoryStream _current = new();

        public void OnPacketStart(in PacketHeader header) => _current = new MemoryStream();
        public void OnPacketPayload(ReadOnlySpan<byte> payload) => _current.Write(payload);
        public void OnPacketEnd(in PacketHeader header) => Packets.Add((header.Type, header.IsEndOfMessage, _current.ToArray()));
    }

    private static byte[] Packet(byte type, byte status, byte[] payload)
    {
        int length = payload.Length + 8;
        return [type, status, (byte)(length >> 8), (byte)length, 0, 51, 1, 0, .. payload];
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(4096)]
    public void FindsTheSamePacketsHoweverTheBytesAreSplit(int chunk)
    {
        byte[] stream =
        [
            .. Packet(TdsConstants.PacketType.SqlBatch, 0x00, [1, 2, 3, 4, 5]),
            .. Packet(TdsConstants.PacketType.SqlBatch, 0x01, [6, 7]),
            .. Packet(TdsConstants.PacketType.Attention, 0x01, []),
            .. Packet(TdsConstants.PacketType.Rpc, 0x09, new byte[300]),
        ];

        var tracker = new PacketTracker();
        var recorder = new Recorder();
        for (int i = 0; i < stream.Length; i += chunk)
            tracker.Feed(stream.AsSpan(i, Math.Min(chunk, stream.Length - i)), recorder);

        Assert.False(tracker.Desynced);
        Assert.Equal(4, recorder.Packets.Count);
        Assert.Equal([1, 2, 3, 4, 5], recorder.Packets[0].Payload);
        Assert.False(recorder.Packets[0].Eom);
        Assert.True(recorder.Packets[1].Eom);
        Assert.Empty(recorder.Packets[2].Payload);
        Assert.Equal(300, recorder.Packets[3].Payload.Length);
    }

    [Fact]
    public void NonTdsBytesLatchDesync()
    {
        var tracker = new PacketTracker();
        tracker.Feed([0x53, 0x08, 0, 0, 0x20, 0, 0, 0, 0, 0], new Recorder());   // an SMP (MARS) header
        Assert.True(tracker.Desynced);
    }
}
