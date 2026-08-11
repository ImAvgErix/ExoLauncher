using ExoLauncher.Adapters;

namespace ExoLauncher.Services;

/// <summary>WinUI-free auth surface used while compiling GogAdapter lifecycle tests.</summary>
public sealed class GogAuthService
{
    public static string AuthConfigPath =>
        Path.Combine(Helpers.PathHelper.AppDataDir, "gogdl", "credentials.json");

    public static string EffectiveAuthConfigPath => FindExistingAuthConfigPath() ?? AuthConfigPath;

    public static string? FindExistingAuthConfigPath() => null;

    public Task<AuthResult> SignInAsync(string gogdlPath, CancellationToken ct = default) =>
        Task.FromResult(new AuthResult
        {
            Ok = false,
            RequiresUserAction = true,
            Message = "GOG auth is unavailable in adapter unit tests.",
        });
}
