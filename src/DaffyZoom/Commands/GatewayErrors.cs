namespace DaffyZoom.Commands;

/// <summary>
/// An error DaffyZoom reports itself, in answer to a daffyzoom.* command. Its <see cref="Number"/> is
/// one of <see cref="GatewayErrors"/>, and its message already starts with "DaffyZoom:", so whoever
/// sees it knows it did not come from the target.
/// </summary>
internal sealed class GatewayException(int number, string message) : Exception(Prefix(message))
{
    public int Number { get; } = number;

    /// <summary>The command it is about, when known - for the event that records the attempt.</summary>
    public string? Command { get; init; }

    private static string Prefix(string message) =>
        message.StartsWith("DaffyZoom:", StringComparison.Ordinal) ? message : "DaffyZoom: " + message;
}

/// <summary>Carries out daffyzoom.* commands for the relay, which recognises them and sends the answer.</summary>
internal interface IGatewayCommands
{
    /// <summary>
    /// Runs a command for <paramref name="caller"/>. A <see cref="GatewayException"/> is the answer
    /// when the command cannot be done; it reaches the client as an ordinary SQL Server error.
    /// </summary>
    Task<Tds.Reply> RunAsync(Proxy.ProxySession caller, DaffyCommand command, CancellationToken cancellationToken);
}

/// <summary>
/// DaffyZoom's error numbers: one range, severity 16, so applications can catch them by number and
/// people can recognise them on sight. DaffyDuo's are 501xx, so a client talking to DaffyZoom in
/// front of DaffyDuo can still tell the two apart. Login refusals stay SQL Server's own 18456.
/// </summary>
internal static class GatewayErrors
{
    public const byte Severity = 16;

    /// <summary>A daffyzoom.* command arrived with other SQL in the same batch.</summary>
    public const int NotAlone = 50301;

    /// <summary>An unknown daffyzoom.* command, or one called with the wrong arguments.</summary>
    public const int BadCommand = 50302;

    /// <summary>An admin command from a login that is not an admin.</summary>
    public const int NeedsAdmin = 50303;

    /// <summary>No such login or session, or a login that already exists.</summary>
    public const int NotFound = 50304;

    /// <summary>Refused by a rule: your own session or login, or something only done on the host.</summary>
    public const int Refused = 50305;

    /// <summary>A target could not be logged in to, so nothing was saved.</summary>
    public const int TargetFailed = 50306;
}
