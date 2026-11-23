using System.Net;
using System.Text;
using DaffyZoom.Proxy;
using DaffyZoom.Tds;

namespace DaffyZoom.Tests;

/// <summary>
/// DaffyZoom logging in to Azure SQL as an Entra ID application: the FEDAUTH exchange SqlClient uses
/// for Authentication=ActiveDirectoryServicePrincipal, played against fake servers, and the token
/// requests it makes, played against a fake Entra ID.
/// </summary>
public class EntraTests
{
    private const string StsUrl = "https://login.windows.net/7a1e5b0c-0000-4000-8000-000000000001/";
    private const string Spn = "https://database.windows.net/";

    [Fact]
    public void AnEntraApplicationMappingParsesAndRoundTrips()
    {
        var target = TargetEndpoint.Parse(
            "Server=tcp:myserver.database.windows.net,1433;Authentication=Active Directory Service Principal;" +
            "User Id=5ba10036-0000-4000-8000-000000000000@tenant-7;Password=s3cret;Initial Catalog=Sales");

        Assert.True(target.IsEntra);
        Assert.Equal("5ba10036-0000-4000-8000-000000000000", target.ClientId);
        Assert.Equal("tenant-7", target.TenantId);
        Assert.Contains("Entra ID application", target.DescribeSecurity());
        Assert.Equal(target, TargetEndpoint.Parse(target.ToConnectionString()));
        Assert.Contains("client secret", target.SecretName);

        var sql = TargetEndpoint.Parse("Server=h;User Id=u@x;Password=p");
        Assert.False(sql.IsEntra);
        Assert.Equal("u@x", sql.ClientId);                // only an Entra application splits off a tenant
        Assert.Null(sql.TenantId);
    }

    [Fact]
    public async Task LogsInWithATokenThroughARedirect()
    {
        // As Azure SQL: the gateway asks for a token and then redirects; the node asks again and accepts.
        await using var node = new FakeTdsServer(
            LoginReplies.Accepted("Sales", (TdsConstants.FeatureId.FedAuth, []), (TdsConstants.FeatureId.Utf8Support, [1])),
            fedAuthInfo: LoginReplies.FedAuthInfo(StsUrl, Spn));
        await using var gateway = new FakeTdsServer(LoginReplies.Redirect("127.0.0.1", node.Port),
            fedAuthInfo: LoginReplies.FedAuthInfo(StsUrl, Spn));

        var tokens = new FakeTokens("test-access-token");
        var target = EntraTarget(gateway.Port);
        await using var connection = await UpstreamConnector.ConnectAsync(
            target, UpstreamConnector.CreateValidationLogin(target), mars: false, Timeout(), tokens);

        Assert.Equal((ushort)node.Port, connection.RoutedTo?.Port);
        Assert.Equal([new FedAuthInfo(StsUrl, Spn), new FedAuthInfo(StsUrl, Spn)], tokens.Asked);

        var atNode = await node.Served;
        Assert.Equal((byte)1, PreLogin.Value(atNode.PreLogin, TdsConstants.PreLoginOption.FedAuthRequired));
        Assert.Equal("", atNode.Login.UserName);          // the token says who it is
        Assert.Equal("", atNode.Login.Password);          // and the secret never reaches the server
        Assert.Contains(TdsConstants.FeatureId.FedAuth, atNode.Login.FeatureIds);
        Assert.Equal([0x05, 0x01], FeatureData(atNode.Login, TdsConstants.FeatureId.FedAuth));   // MSAL library, echo; workflow
        Assert.Equal(UpstreamConnector.FedAuthToken("test-access-token", null), atNode.FedAuthToken);

        // The client is given the node's answer minus the FEDAUTH acknowledgement it never asked for.
        var relayed = LoginResponse.Parse(connection.RawLoginResponse.AsSpan(TdsConstants.HeaderLength));
        Assert.True(relayed.Succeeded);
        Assert.Equal([TdsConstants.FeatureId.Utf8Support], relayed.AcknowledgedFeatures);
        Assert.Equal(connection.Spid, (ushort)((connection.RawLoginResponse[4] << 8) | connection.RawLoginResponse[5]));
    }

    [Fact]
    public async Task ASqlLoginIsNotAskedForATokenItCannotGive()
    {
        await using var server = new FakeTdsServer(LoginReplies.Accepted("Sales"), fedAuthInfo: LoginReplies.FedAuthInfo(StsUrl, Spn));

        var target = TargetEndpoint.Parse($"Server=tcp:127.0.0.1,{server.Port};User Id=u;Password=p;Encrypt=Optional;Connect Timeout=10");
        var ex = await Assert.ThrowsAsync<UpstreamException>(() => UpstreamConnector.ConnectAsync(
            target, UpstreamConnector.CreateValidationLogin(target), mars: false, Timeout(), new FakeTokens("x")));

        Assert.Equal(UpstreamFailure.Protocol, ex.Failure);
        Assert.Null(PreLogin.Value(server.PreLoginSeen!, TdsConstants.PreLoginOption.FedAuthRequired));
    }

    [Fact]
    public void OnlyTheUnrequestedAcknowledgementIsTakenOut()
    {
        byte[] original = LoginReplies.Accepted("Sales",
            (TdsConstants.FeatureId.SessionRecovery, [1, 2, 3]), (TdsConstants.FeatureId.FedAuth, []), (TdsConstants.FeatureId.Utf8Support, [1]));
        byte[] expected = LoginReplies.Accepted("Sales",
            (TdsConstants.FeatureId.SessionRecovery, [1, 2, 3]), (TdsConstants.FeatureId.Utf8Support, [1]));

        Assert.Equal(expected, LoginResponse.WithoutFeatureAck(original, TdsConstants.FeatureId.FedAuth));
        Assert.Equal(expected, LoginResponse.WithoutFeatureAck(expected, TdsConstants.FeatureId.FedAuth));
    }

    [Fact]
    public void FedAuthInfoIsRead()
    {
        var response = LoginResponse.Parse(LoginReplies.FedAuthInfo(StsUrl, Spn));
        Assert.Equal(new FedAuthInfo(StsUrl, Spn), response.FedAuth);
        Assert.False(response.Succeeded);
    }

    [Fact]
    public void TheTokenIsAskedForFromTheTenantTheServerNames()
    {
        var (endpoint, scope) = EntraTokenSource.Resolve(EntraTarget(1433), new FedAuthInfo(StsUrl, Spn));

        Assert.Equal("https://login.windows.net/7a1e5b0c-0000-4000-8000-000000000001/oauth2/v2.0/token", endpoint.ToString());
        Assert.Equal("https://database.windows.net//.default", scope);   // SqlClient's form, double slash and all

        var fallback = TargetEndpoint.Parse("Server=h;Authentication=ActiveDirectoryServicePrincipal;User Id=app@tenant-9;Password=s");
        Assert.Equal("https://login.microsoftonline.com/tenant-9/oauth2/v2.0/token",
            EntraTokenSource.Resolve(fallback, new FedAuthInfo("https://login.microsoftonline.com/common", Spn)).Endpoint.ToString());
    }

    [Theory]
    [InlineData("https://login.evil.example/tenant/", Spn)]                 // not Entra ID: the secret would go there
    [InlineData("http://login.microsoftonline.com/tenant/", Spn)]           // not HTTPS
    [InlineData(StsUrl, "https://graph.microsoft.com/")]                    // a token to something that is not Azure SQL
    [InlineData("https://login.microsoftonline.com/common", Spn)]           // no tenant, and none in the mapping
    public void ATokenIsOnlyAskedOfEntraIdAndOnlyForAzureSql(string stsUrl, string spn)
    {
        var ex = Assert.Throws<UpstreamException>(() => EntraTokenSource.Resolve(EntraTarget(1433), new FedAuthInfo(stsUrl, spn)));
        Assert.Equal(UpstreamFailure.Token, ex.Failure);
    }

    [Fact]
    public async Task TokensAreRequestedOnceAndCached()
    {
        var entra = new FakeEntra(HttpStatusCode.OK, """{"token_type":"Bearer","expires_in":3599,"access_token":"issued-access-token"}""");
        var source = new EntraTokenSource(entra);
        var target = EntraTarget(1433);

        var tokens = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => source.GetTokenAsync(target, new FedAuthInfo(StsUrl, Spn), Timeout())));

        Assert.All(tokens, t => Assert.Equal("issued-access-token", t));
        Assert.Equal(1, entra.Requests);
        Assert.Contains("grant_type=client_credentials", entra.LastBody);
        Assert.Contains("client_id=5ba10036-0000-4000-8000-000000000000", entra.LastBody);
        Assert.Contains("client_secret=test-secret%21", entra.LastBody);
        Assert.Contains("scope=https%3A%2F%2Fdatabase.windows.net%2F%2F.default", entra.LastBody);
    }

    [Fact]
    public async Task EntraRefusalsAreReportedWithoutTheirTraceIds()
    {
        var entra = new FakeEntra(HttpStatusCode.Unauthorized,
            """{"error":"invalid_client","error_description":"AADSTS7000215: Invalid client secret provided. Trace ID: 1 Correlation ID: 2 Timestamp: 3"}""");
        var source = new EntraTokenSource(entra);

        var ex = await Assert.ThrowsAsync<UpstreamException>(() => source.GetTokenAsync(EntraTarget(1433), new FedAuthInfo(StsUrl, Spn), Timeout()));

        Assert.Equal(UpstreamFailure.Token, ex.Failure);
        Assert.EndsWith("AADSTS7000215: Invalid client secret provided.", ex.Message);

        // A failure is not remembered: the next login asks again.
        await Assert.ThrowsAsync<UpstreamException>(() => source.GetTokenAsync(EntraTarget(1433), new FedAuthInfo(StsUrl, Spn), Timeout()));
        Assert.Equal(2, entra.Requests);
    }

    [Fact]
    public void AFeatureIsAddedAheadOfTheTerminator()
    {
        var login = new Login7 { FeatureExt = [TdsConstants.FeatureId.Utf8Support, 1, 0, 0, 0, 1, TdsConstants.FeatureId.Terminator] };
        login.AddFeature(TdsConstants.FeatureId.FedAuth, [0x05, 0x01]);

        Assert.Equal([TdsConstants.FeatureId.Utf8Support, TdsConstants.FeatureId.FedAuth], login.FeatureIds);
        Assert.Equal(TdsConstants.FeatureId.Terminator, login.FeatureExt![^1]);

        var bare = new Login7();
        bare.AddFeature(TdsConstants.FeatureId.FedAuth, [0x04, 0x01]);
        Assert.Equal([TdsConstants.FeatureId.FedAuth], Login7.Parse(bare.Build()).FeatureIds);
    }

    private static TargetEndpoint EntraTarget(int port) => TargetEndpoint.Parse(
        $"Server=tcp:127.0.0.1,{port};Authentication=ActiveDirectoryServicePrincipal;User Id=5ba10036-0000-4000-8000-000000000000;" +
        "Password=test-secret!;Initial Catalog=Sales;Encrypt=Optional;Connect Timeout=10");

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    private static byte[] FeatureData(Login7 login, byte feature)
    {
        byte[] block = login.FeatureExt!;
        for (int position = 0; block[position] != TdsConstants.FeatureId.Terminator;)
        {
            int length = BitConverter.ToInt32(block, position + 1);
            if (block[position] == feature) return block[(position + 5)..(position + 5 + length)];
            position += 5 + length;
        }
        throw new InvalidOperationException($"no feature {feature}");
    }

    private sealed class FakeTokens(string token) : IAccessTokenSource
    {
        public List<FedAuthInfo> Asked { get; } = [];

        public Task<string> GetTokenAsync(TargetEndpoint target, FedAuthInfo info, CancellationToken cancellationToken)
        {
            lock (Asked) Asked.Add(info);
            return Task.FromResult(token);
        }
    }

    private sealed class FakeEntra(HttpStatusCode status, string body) : HttpMessageHandler
    {
        private int _requests;
        public int Requests => _requests;
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            LastBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            await Task.Delay(50, cancellationToken);   // long enough for concurrent callers to pile up
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
