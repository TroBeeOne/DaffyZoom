using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DaffyZoom.Tds;

namespace DaffyZoom.Proxy;

/// <summary>Where an Entra ID access token for a target comes from. Tests substitute their own.</summary>
internal interface IAccessTokenSource
{
    Task<string> GetTokenAsync(TargetEndpoint target, FedAuthInfo info, CancellationToken cancellationToken);
}

/// <summary>
/// Access tokens for an Entra ID application (a service principal) from its client secret: the
/// OAuth 2.0 client-credentials grant, one HTTPS POST to the tenant's token endpoint. It is what
/// MSAL does for SqlClient's Authentication=ActiveDirectoryServicePrincipal, without the dependency.
///
/// The target says where to ask (FEDAUTHINFO's STS URL) and for what (its SPN). Both are checked
/// against Microsoft's own before the secret goes anywhere: otherwise a server - or anyone in the
/// middle of a connection whose certificate is not validated - could name its own STS and collect
/// the secret, or ask for a token to some other resource and collect that.
///
/// Tokens are cached per token endpoint, application, scope and secret until five minutes before
/// they expire, and concurrent logins share one request, so a pool opening a hundred connections
/// asks Entra once. Neither the secret nor a token is ever logged.
/// </summary>
internal sealed class EntraTokenSource(HttpMessageHandler? handler = null) : IAccessTokenSource
{
    public static readonly EntraTokenSource Shared = new();

    /// <summary>Entra ID sign-in hosts, public and national clouds - the ones MSAL knows.</summary>
    private static readonly HashSet<string> AuthorityHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "login.microsoftonline.com", "login.windows.net", "login.microsoft.com", "sts.windows.net",
        "login.microsoftonline.us", "login.usgovcloudapi.net",
        "login.chinacloudapi.cn", "login.partner.microsoftonline.cn",
    };

    /// <summary>Azure SQL's resource ids (SPNs), public and national clouds.</summary>
    private static readonly HashSet<string> Resources = new(StringComparer.OrdinalIgnoreCase)
    {
        "https://database.windows.net/", "https://database.usgovcloudapi.net/", "https://database.chinacloudapi.cn/",
    };

    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private sealed record CachedToken(string AccessToken, DateTime ExpiresUtc, DateTime IssuedUtc);

    private readonly HttpClient _http = new(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private readonly ConcurrentDictionary<string, Lazy<Task<CachedToken>>> _cache = new();

    public async Task<string> GetTokenAsync(TargetEndpoint target, FedAuthInfo info, CancellationToken cancellationToken)
    {
        var (endpoint, scope) = Resolve(target, info);
        string key = $"{endpoint}|{target.ClientId}|{scope}|{Fingerprint(target.Password)}";

        while (true)
        {
            var entry = _cache.GetOrAdd(key, _ => new(() => RequestAsync(endpoint, target.ClientId, target.Password, scope)));
            CachedToken token;
            try
            {
                token = await entry.Value.WaitAsync(cancellationToken);
            }
            catch when (entry.Value.IsFaulted)
            {
                _cache.TryRemove(new(key, entry));   // a failure is not cached: the next login asks again
                throw;
            }

            var now = DateTime.UtcNow;
            if (token.ExpiresUtc - now > RefreshMargin || now - token.IssuedUtc < TimeSpan.FromMinutes(1)) return token.AccessToken;
            _cache.TryRemove(new(key, entry));
        }
    }

    /// <summary>
    /// The token endpoint and scope for a target's FEDAUTHINFO, or a refusal to use them. The
    /// tenant is the one the target names; "id@tenant" in the mapping is only a fallback.
    /// </summary>
    internal static (Uri Endpoint, string Scope) Resolve(TargetEndpoint target, FedAuthInfo info)
    {
        if (!Uri.TryCreate(info.StsUrl, UriKind.Absolute, out var sts) || sts.Scheme != Uri.UriSchemeHttps || !AuthorityHosts.Contains(sts.Host))
            throw new UpstreamException(UpstreamFailure.Token,
                $"the target asked for a token from '{info.StsUrl}', which is not a Microsoft Entra ID sign-in address; " +
                "the client secret was not sent");

        string resource = info.Spn.EndsWith('/') ? info.Spn : info.Spn + "/";
        if (!Resources.Contains(resource))
            throw new UpstreamException(UpstreamFailure.Token,
                $"the target asked for a token to '{info.Spn}', which is not Azure SQL; none was requested");

        string named = sts.AbsolutePath.Trim('/').Split('/')[0];
        string? tenant = named is { Length: > 0 } and not ("common" or "organizations") ? named : target.TenantId;
        if (string.IsNullOrEmpty(tenant))
            throw new UpstreamException(UpstreamFailure.Token,
                "the target did not name its Entra ID tenant; write the application as id@tenant in User Id");

        // As SqlClient does: the resource plus "/.default", even where that makes a double slash.
        string scope = info.Spn.EndsWith("/.default", StringComparison.Ordinal) ? info.Spn : info.Spn + "/.default";
        return (new Uri($"https://{sts.Host}/{Uri.EscapeDataString(tenant)}/oauth2/v2.0/token"), scope);
    }

    private async Task<CachedToken> RequestAsync(Uri endpoint, string clientId, string secret, string scope)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = secret,
                ["scope"] = scope,
            }),
        };

        string body;
        bool ok;
        int status;
        try
        {
            using var response = await _http.SendAsync(request);
            body = await response.Content.ReadAsStringAsync();
            ok = response.IsSuccessStatusCode;
            status = (int)response.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new UpstreamException(UpstreamFailure.Token,
                $"could not reach Entra ID at {endpoint.Host} for a token: {(ex is TaskCanceledException ? "no answer within 30s" : ex.Message)}", inner: ex);
        }

        JsonElement json = default;
        try
        {
            using var document = JsonDocument.Parse(body);
            json = document.RootElement.Clone();
        }
        catch (JsonException) { }

        if (ok && json.ValueKind == JsonValueKind.Object && json.TryGetProperty("access_token", out var accessToken)
            && accessToken.GetString() is { Length: > 0 } value)
        {
            int seconds = json.TryGetProperty("expires_in", out var expires) && ExpiresIn(expires) is { } s ? s : 3600;
            Log.Debug("upstream.token", $"Entra ID issued a token for application {clientId}, valid for {seconds / 60} min");
            var now = DateTime.UtcNow;
            return new CachedToken(value, now.AddSeconds(seconds), now);
        }

        string reason = json.ValueKind == JsonValueKind.Object && json.TryGetProperty("error_description", out var description)
            ? Shorten(description.GetString())
            : $"HTTP {status}";
        throw new UpstreamException(UpstreamFailure.Token, $"Entra ID would not issue a token for application {clientId}: {reason}");
    }

    private static int? ExpiresIn(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetInt32(out int n) => n,
        JsonValueKind.String when int.TryParse(value.GetString(), out int n) => n,
        _ => null,
    };

    /// <summary>"AADSTS7000215: Invalid client secret provided. ...", without the trace and correlation ids.</summary>
    private static string Shorten(string? description)
    {
        string text = (description ?? "").Split('\n')[0].Trim();
        int trace = text.IndexOf(" Trace ID:", StringComparison.Ordinal);
        return trace > 0 ? text[..trace] : text;
    }

    /// <summary>Tells secrets apart in the cache key without keeping another copy of one.</summary>
    private static string Fingerprint(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
}
