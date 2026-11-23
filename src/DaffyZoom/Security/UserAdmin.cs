using DaffyZoom.Proxy;
using DaffyZoom.Tds;

namespace DaffyZoom.Security;

/// <summary>
/// What a login to a target said about a mapping: who answered and how, or why it failed.
/// <see cref="Resuming"/> is Azure SQL's "database not currently available", which a database that
/// paused itself says while it wakes up.
/// </summary>
internal sealed record TargetCheck(string? Summary, string? Detail, string? RoutedTo, string? Failure, string? Problem, bool Resuming)
{
    public bool Ok => Summary is not null;
}

/// <summary>
/// Changes to user mappings, made the same way whoever asks: `daffyzoom user ...` on the host,
/// `:user ...` at the prompt, or the daffyzoom.* commands in T-SQL. Asking, printing and refusing
/// are each caller's own business; what a change does to the store is decided here, once.
/// </summary>
internal static class UserAdmin
{
    /// <summary>
    /// Logs in to the target through the same connector the relay uses, so "validated" means "the
    /// relay will be able to do this", not merely "the connection string parsed".
    /// </summary>
    public static async Task<TargetCheck> CheckTargetAsync(TargetEndpoint endpoint, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await UpstreamConnector.ConnectAsync(
                endpoint, UpstreamConnector.CreateValidationLogin(endpoint), mars: false, cancellationToken);

            var login = connection.Login;
            string summary = $"{login.ServerProgram} {login.ServerVersion}".Trim();
            return new TargetCheck(summary,
                $"{summary}, database '{login.Database}', {connection.Security}, {connection.Elapsed.TotalMilliseconds:F0} ms",
                connection.RoutedTo?.ToString(), null, null, false);
        }
        catch (UpstreamException ex)
        {
            return new TargetCheck(null, null, null, ex.Failure.ToString(), ex.Message,
                ex.Response?.Errors.Any(e => e.Number == ServerMessages.DatabaseNotAvailable) == true);
        }
    }

    public static UserRecord Create(UserStore store, string login, string password, TargetEndpoint endpoint, string validation)
    {
        UserStore.ValidateLogin(login);
        var now = DateTime.UtcNow;
        var user = new UserRecord
        {
            Login = login,
            Enabled = true,
            PasswordHash = PasswordHasher.Hash(password),
            Target = endpoint.ToConnectionString(),
            CreatedUtc = now,
            UpdatedUtc = now,
            LastValidatedUtc = now,
            LastValidation = validation,
        };
        store.Save(user);
        return user;
    }

    public static void SetPassword(UserStore store, UserRecord user, string password)
    {
        user.PasswordHash = PasswordHasher.Hash(password);
        Touch(store, user);
    }

    public static void SetTarget(UserStore store, UserRecord user, TargetEndpoint endpoint, string validation)
    {
        user.Target = endpoint.ToConnectionString();
        user.LastValidatedUtc = DateTime.UtcNow;
        user.LastValidation = validation;
        Touch(store, user);
    }

    /// <summary>A successful test: when, and what answered. Not a change to the mapping, so not an update.</summary>
    public static void RecordValidation(UserStore store, UserRecord user, string validation)
    {
        user.LastValidatedUtc = DateTime.UtcNow;
        user.LastValidation = validation;
        store.Save(user);
    }

    public static void SetEnabled(UserStore store, UserRecord user, bool enabled)
    {
        user.Enabled = enabled;
        Touch(store, user);
    }

    public static void SetAdmin(UserStore store, UserRecord user, bool admin)
    {
        user.Admin = admin;
        Touch(store, user);
    }

    /// <summary>
    /// A new target for an existing mapping. A connection string without a password keeps the
    /// current one, as long as it names the same login: changing the server or the database should
    /// not mean typing the password again, but a different login's password is never guessed at.
    /// </summary>
    public static TargetEndpoint KeepPassword(TargetEndpoint updated, TargetEndpoint? current) =>
        updated.Password.Length == 0 && current is not null && current.Password.Length > 0
        && string.Equals(updated.UserName, current.UserName, StringComparison.OrdinalIgnoreCase)
        && updated.Authentication == current.Authentication
            ? updated with { Password = current.Password }
            : updated;

    public static TargetEndpoint? TryEndpoint(UserRecord user)
    {
        try { return user.Endpoint; }
        catch (FormatException) { return null; }
    }

    public static string DescribeTarget(UserRecord user) => TryEndpoint(user)?.Describe() ?? "(unreadable target)";

    private static void Touch(UserStore store, UserRecord user)
    {
        user.UpdatedUtc = DateTime.UtcNow;
        store.Save(user);
    }
}
