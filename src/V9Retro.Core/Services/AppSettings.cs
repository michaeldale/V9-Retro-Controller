using System.Globalization;

namespace V9Retro.Core.Services;

/// <summary>Typed settings over the key/value table. The first five match the plugin's
/// settings page, with the same defaults and limits.</summary>
public sealed class AppSettings
{
    readonly ISettingsBackend store;

    public AppSettings(ISettingsBackend store)
    {
        this.store = store;
        store.Changed += () => Changed?.Invoke();
    }

    public sealed record IntSetting(string Key, string Label, int Default, int Min, int Max, string Help);

    public static readonly IntSetting PollMinutes =
        new("poll_minutes", "Status poll interval (minutes)", 15, 1, 1440, "How often every enabled agent is polled in the background.");
    public static readonly IntSetting ScreenshotMinutes =
        new("screenshot_minutes", "Background screenshot interval (minutes)", 60, 0, 10080, "0 turns background screenshots off.");
    public static readonly IntSetting InventoryHours =
        new("inventory_hours", "Maximum inventory age (hours)", 24, 1, 720, "Inventory is also refreshed after a restart or agent update.");
    public static readonly IntSetting ScreenshotKeep =
        new("screenshot_keep", "Screenshots kept per agent", 10, 1, 100, "Older screenshots are deleted.");
    public static readonly IntSetting OfflineAfterFailures =
        new("offline_after_failures", "Failed polls before offline", 2, 1, 10, "Consecutive failures before an agent is marked offline.");

    public static IReadOnlyList<IntSetting> IntSettings { get; } =
        [PollMinutes, ScreenshotMinutes, InventoryHours, ScreenshotKeep, OfflineAfterFailures];

    public const string DiscoveryAutoBrowseKey = "discovery_auto_browse";
    public const string CloseToTrayKey = "close_to_tray";
    public const string UpdateCheckOnStartupKey = "update_check_on_startup";

    public event Action? Changed;

    public int Get(IntSetting s) =>
        int.TryParse(store.Get(s.Key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, s.Min, s.Max)
            : s.Default;

    public void Set(IntSetting s, int value)
    {
        store.Set(s.Key, Math.Clamp(value, s.Min, s.Max).ToString(CultureInfo.InvariantCulture));
    }

    public bool GetBool(string key, bool fallback) => store.Get(key) is { } v ? v == "1" : fallback;

    public void SetBool(string key, bool value)
    {
        store.Set(key, value ? "1" : "0");
    }

    public bool DiscoveryAutoBrowse
    {
        get => GetBool(DiscoveryAutoBrowseKey, true);
        set => SetBool(DiscoveryAutoBrowseKey, value);
    }

    /// <summary>A quiet update check shortly after starting (off by default, like the agent's).</summary>
    public bool UpdateCheckOnStartup
    {
        get => GetBool(UpdateCheckOnStartupKey, false);
        set => SetBool(UpdateCheckOnStartupKey, value);
    }

    public bool CloseToTray
    {
        get => GetBool(CloseToTrayKey, true);
        set => SetBool(CloseToTrayKey, value);
    }
}
