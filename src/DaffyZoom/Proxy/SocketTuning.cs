using System.Net.Sockets;

namespace DaffyZoom.Proxy;

/// <summary>
/// TCP keepalive on both halves of every session. DaffyZoom may run for months, and an idle pooled
/// connection whose peer vanished (a laptop closed, a firewall that forgot the flow) otherwise holds a
/// relay and an upstream login open forever. SqlClient sets similar values on its own sockets.
/// </summary>
internal static class SocketTuning
{
    public static void EnableKeepAlive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 4);
        }
        catch (SocketException)
        {
            // Not every platform accepts every option; keepalive is a safety net, not a requirement.
        }
    }
}
