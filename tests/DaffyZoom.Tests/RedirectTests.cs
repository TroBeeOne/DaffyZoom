using System.Net;
using DaffyZoom.Proxy;
using DaffyZoom.Tds;

namespace DaffyZoom.Tests;

/// <summary>
/// A target that answers the login with a routing ENVCHANGE - Azure SQL's Redirect policy, an
/// availability group's read-only routing - is followed by DaffyZoom itself, so the client never
/// learns the address and cannot go around it. Played here by two fake servers: a "gateway" that
/// redirects and a "node" that accepts.
/// </summary>
public class RedirectTests
{
    [Fact]
    public async Task FollowsARedirectAndLogsInWhereItWasSent()
    {
        await using var node = new FakeTdsServer(LoginReplies.Accepted("Sales"));
        await using var gateway = new FakeTdsServer(LoginReplies.Redirect("127.0.0.1", node.Port));

        var target = Target(gateway.Port);
        await using var connection = await UpstreamConnector.ConnectAsync(
            target, UpstreamConnector.CreateValidationLogin(target), mars: false, Timeout());

        Assert.Equal(new RoutingTarget("127.0.0.1", (ushort)node.Port), connection.RoutedTo);
        Assert.Null(connection.Login.Routing);             // what the client will be sent is the node's answer
        Assert.Equal("Sales", connection.Login.Database);

        var atGateway = (await gateway.Served).Login;
        var atNode = (await node.Served).Login;
        Assert.Equal("127.0.0.1", atGateway.ServerName);
        Assert.Equal($"127.0.0.1,{node.Port}", atNode.ServerName);   // named as SqlClient names it
        Assert.Equal("svc_app", atNode.UserName);
        Assert.Equal("Svc!2026", atNode.Password);
        Assert.Equal("Sales", atNode.Database);
    }

    [Fact]
    public async Task ARedirectedHopNegotiatesItsOwnTls()
    {
        // The server name travels in TLS SNI, which is never sent for an IP address: redirect to a
        // name, and listen where that name resolves first.
        var localhost = (await Dns.GetHostAddressesAsync("localhost"))[0];
        using var certificate = FakeTdsServer.SelfSigned("gateway.test");
        await using var node = new FakeTdsServer(LoginReplies.Accepted("Sales"), certificate, localhost);
        await using var gateway = new FakeTdsServer(LoginReplies.Redirect("localhost", node.Port), certificate);

        var target = Target(gateway.Port, "Encrypt=Mandatory;TrustServerCertificate=True");
        await using var connection = await UpstreamConnector.ConnectAsync(
            target, UpstreamConnector.CreateValidationLogin(target), mars: false, Timeout());

        Assert.StartsWith("Tls12", connection.Security);
        Assert.Equal("localhost", (await node.Served).TlsHostName);
    }

    [Fact]
    public async Task ASecondRedirectIsRefused()
    {
        await using var node = new FakeTdsServer(LoginReplies.Redirect("127.0.0.1", 1));
        await using var gateway = new FakeTdsServer(LoginReplies.Redirect("127.0.0.1", node.Port));

        var target = Target(gateway.Port);
        var ex = await Assert.ThrowsAsync<UpstreamException>(() => UpstreamConnector.ConnectAsync(
            target, UpstreamConnector.CreateValidationLogin(target), mars: false, Timeout()));

        Assert.Equal(UpstreamFailure.Routed, ex.Failure);
        Assert.Contains("redirected it again", ex.Message);
    }

    [Fact]
    public async Task ARedirectToAnotherProtocolIsRefused()
    {
        await using var gateway = new FakeTdsServer(LoginReplies.Redirect("127.0.0.1", 5000, protocol: 1));

        var target = Target(gateway.Port);
        var ex = await Assert.ThrowsAsync<UpstreamException>(() => UpstreamConnector.ConnectAsync(
            target, UpstreamConnector.CreateValidationLogin(target), mars: false, Timeout()));

        Assert.Equal(UpstreamFailure.Protocol, ex.Failure);
    }

    [Fact]
    public void RoutingIsReadFromTheLoginResponse()
    {
        var response = LoginResponse.Parse(LoginReplies.Redirect("node7.worker.example", 11017));

        Assert.Equal(new RoutingTarget("node7.worker.example", 11017), response.Routing);
        Assert.True(response.LoginAcknowledged);          // acknowledged, and still not the place to stay
    }

    private static TargetEndpoint Target(int port, string encryption = "Encrypt=Optional") =>
        TargetEndpoint.Parse($"Server=tcp:127.0.0.1,{port};User Id=svc_app;Password=Svc!2026;Initial Catalog=Sales;" +
                             $"{encryption};Connect Timeout=10");

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;
}
