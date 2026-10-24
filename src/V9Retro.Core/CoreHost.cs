using V9Retro.Core.Data;
using V9Retro.Core.Services;

namespace V9Retro.Core;

/// <summary>Wires the store and services together. The desktop app owns one; a future service
/// (an MCP server, for example) builds its own over the same database file.</summary>
public sealed class CoreHost : IAsyncDisposable
{
    public CoreHost(string? dataDirectory = null)
    {
        DataDirectory = dataDirectory ?? DefaultDataDirectory();
        Directory.CreateDirectory(DataDirectory);
        Database = new Database(Path.Combine(DataDirectory, "v9retro.db"));
        Database.Migrate();

        SettingsStore = new SettingsStore(Database);
        Settings = new AppSettings(SettingsStore);
        Secrets = new DefaultSecretProtector();
        Feed = new ChangeFeed();
        Connector = new AgentConnector();
        Agents = new AgentService(new AgentStore(Database), new EventStore(Database), new InventoryStore(Database),
            new ScreenshotStore(Database), Settings, Connector, Feed, Secrets,
            Path.Combine(DataDirectory, "screenshots"));
        Maintenance = new MaintenanceService(Agents);
        Scheduler = new PollScheduler(Agents, Settings);
    }

    public string DataDirectory { get; }
    public Database Database { get; }
    public AppSettings Settings { get; }
    public SettingsStore SettingsStore { get; }
    public ISecretProtector Secrets { get; }
    public ChangeFeed Feed { get; }
    public AgentConnector Connector { get; }
    public AgentService Agents { get; }
    public MaintenanceService Maintenance { get; }
    public PollScheduler Scheduler { get; }

    /// <summary>%LOCALAPPDATA%\V9RetroController on Windows, ~/Library/Application Support/V9RetroController on macOS.</summary>
    /// <remarks>The V9RETRO_DATA environment variable overrides it (a second instance, a portable copy, tests).</remarks>
    public static string DefaultDataDirectory() =>
        Environment.GetEnvironmentVariable("V9RETRO_DATA") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
                "V9RetroController");

    public ValueTask DisposeAsync() => Scheduler.DisposeAsync();
}
