using ExoLauncher.Adapters;
using ExoLauncher.Models;

namespace ExoLauncher.Services;

public sealed class AppServices
{
    public SettingsService Settings { get; } = new();
    public LibraryService Library { get; private set; } = null!;
    public LaunchOrchestrator Launcher { get; private set; } = null!;
    public DependencyService Dependencies { get; } = new();
    public IReadOnlyList<IStoreAdapter> Adapters { get; private set; } = Array.Empty<IStoreAdapter>();

    public void Initialize()
    {
        Settings.Load();
        Adapters =
        [
            new LocalAdapter(),
            new SteamAdapter(),
            new EpicAdapter(),
            new GogAdapter(),
            new RiotAdapter(),
            new XboxAdapter(),
            new EaAdapter(),
            new UbisoftAdapter(),
            new BattleNetAdapter(),
            new AmazonAdapter(),
        ];
        Library = new LibraryService(Adapters);
        Launcher = new LaunchOrchestrator(Adapters, Settings, Dependencies);
    }

    public string AppVersion
    {
        get
        {
            try
            {
                var v = typeof(AppServices).Assembly.GetName().Version;
                return v is null ? "0.1.0" : $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch { return "0.1.0"; }
        }
    }
}
