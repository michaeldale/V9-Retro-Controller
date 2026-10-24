using Avalonia.Controls;
using Avalonia.Platform;

namespace V9Retro.App.Services;

/// <summary>The window and tray icon (Assets/AppIcon.png; the exe uses AppIcon.ico and the Mac
/// bundle an .icns made from the PNG by scripts/build-mac.sh).</summary>
static class AppIcon
{
    static WindowIcon? _cached;

    public static WindowIcon Create()
    {
        if (_cached is not null) return _cached;
        using var stream = AssetLoader.Open(new Uri("avares://V9RetroController/Assets/AppIcon.png"));
        return _cached = new WindowIcon(stream);
    }
}
