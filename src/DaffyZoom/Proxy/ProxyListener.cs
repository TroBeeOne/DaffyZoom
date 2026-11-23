using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using DaffyZoom.Security;

namespace DaffyZoom.Proxy;

/// <summary>Everything a session needs from the process that is not specific to it.</summary>
internal sealed class ProxyContext
{
    public required UserStore Users { get; init; }
    public required SessionRegistry Sessions { get; init; }
    public X509Certificate2? Certificate { get; init; }

    /// <summary>Accept clients that will not encrypt. Off by default: mandatory encryption is the rule.</summary>
    public bool AllowPlaintext { get; init; }

    public bool Trace { get; init; }
    public string? TranscriptDirectory { get; init; }

    /// <summary>Answers daffyzoom.* commands. Without it they go to the target like any other SQL.</summary>
    public Commands.IGatewayCommands? Commands { get; set; }

    /// <summary>Observers attached to every relayed session, beyond the built-in counter and transcript.</summary>
    public Func<ProxySession, IEnumerable<IRelayObserver>>? ObserverFactory { get; set; }

    /// <summary>Told about every refused login (session, login name, reason code).</summary>
    public Action<ProxySession, string, string>? LoginRefused { get; set; }

    /// <summary>Told when a session has logged in to its target and relaying begins.</summary>
    public Action<ProxySession>? SessionStarted { get; set; }

    /// <summary>Told when a relayed session ends.</summary>
    public Action<ProxySession>? SessionEnded { get; set; }
}

/// <summary>Accepts client connections and gives each its own <see cref="FrontSession"/>.</summary>
internal sealed class ProxyListener(IPAddress address, int port, ProxyContext context)
{
    public IPEndPoint? Endpoint { get; private set; }

    /// <summary>
    /// <paramref name="onListening"/> runs once the port is bound, so a prompt started alongside
    /// knows the banner is out and the port was actually free.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken, Action? onListening = null)
    {
        var listener = new TcpListener(address, port);
        if (address.Equals(IPAddress.IPv6Any)) listener.Server.DualMode = true;
        listener.Start(backlog: 512);
        Endpoint = (IPEndPoint)listener.LocalEndpoint;

        onListening?.Invoke();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                string clientAddress = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
                var session = context.Sessions.Open(clientAddress);
                _ = Task.Run(() => new FrontSession(client, session, context).RunAsync(cancellationToken), CancellationToken.None);
            }
        }
        finally
        {
            listener.Stop();
        }
    }
}
