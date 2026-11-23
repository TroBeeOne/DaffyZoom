namespace DaffyZoom;

/// <summary>
/// Where DaffyZoom keeps its state: the encrypted user store, its key, the TLS certificate and logs.
///
/// Per user rather than per directory, like DaffyDB's certificate: %LOCALAPPDATA%\DaffyZoom on Windows,
/// ~/.local/share/DaffyZoom elsewhere. DAFFYZOOM_HOME or --config-dir moves it, which is how a service
/// account or a test run gets a store of its own.
/// </summary>
internal static class AppPaths
{
    private static string? _home;

    public static string Home
    {
        get => _home ??= Environment.GetEnvironmentVariable("DAFFYZOOM_HOME") is { Length: > 0 } fromEnvironment
            ? Path.GetFullPath(fromEnvironment)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                                          Environment.SpecialFolderOption.Create),
                "DaffyZoom");
        set => _home = Path.GetFullPath(value);
    }

    public static string EnsureHome()
    {
        Directory.CreateDirectory(Home);
        return Home;
    }
}
