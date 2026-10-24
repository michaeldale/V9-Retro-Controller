using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using V9Retro.Protocol.Imaging;

namespace V9Retro.App.Services;

static class Ui
{
    /// <summary>Runs on the UI thread: inline when already there, posted otherwise.</summary>
    public static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    public static Window? MainWindow =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    public static IStorageProvider? Storage => MainWindow?.StorageProvider;

    public static WriteableBitmap ToBitmap(BgraImage image)
    {
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using var fb = bitmap.Lock();
        for (var y = 0; y < image.Height; y++)
            Marshal.Copy(image.Pixels, y * image.Stride, fb.Address + y * fb.RowBytes, image.Stride);
        return bitmap;
    }

    public static Bitmap? FromPng(byte[]? png)
    {
        if (png is null || png.Length == 0) return null;
        using var stream = new MemoryStream(png);
        return new Bitmap(stream);
    }

    public static Bitmap? FromFile(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        return new Bitmap(stream);
    }

    public static string Describe(Exception ex) => ex switch
    {
        AggregateException { InnerException: { } inner } => Describe(inner),
        _ => ex.Message,
    };

    // ---- dialogs ---------------------------------------------------------------------

    static Window Dialog(string title, Control content, double width = 420) => new()
    {
        Title = title,
        Width = width,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false,
        Content = new Border { Padding = new Thickness(20), Child = content },
        Icon = AppIcon.Create(),
    };

    static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        foreach (var b in buttons) panel.Children.Add(b);
        return panel;
    }

    /// <summary>A yes/no confirmation. <paramref name="danger"/> styles the confirm button.</summary>
    public static async Task<bool> ConfirmAsync(string title, string message, string confirm = "OK", bool danger = false)
    {
        var owner = MainWindow;
        if (owner is null) return false;
        var ok = new Button { Content = confirm, IsDefault = true, MinWidth = 90 };
        if (danger) ok.Classes.Add("danger");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Buttons(cancel, ok));
        var window = Dialog(title, body);
        ok.Click += (_, _) => window.Close(true);
        cancel.Click += (_, _) => window.Close(false);
        return await window.ShowDialog<bool>(owner);
    }

    public static async Task<string?> PromptAsync(string title, string message, string initial = "", bool password = false,
        string confirm = "OK")
    {
        var owner = MainWindow;
        if (owner is null) return null;
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 10, 0, 0) };
        if (password) box.PasswordChar = '•';
        var ok = new Button { Content = confirm, IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90 };
        var body = new StackPanel();
        body.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(box);
        body.Children.Add(Buttons(cancel, ok));
        var window = Dialog(title, body);
        ok.Click += (_, _) => window.Close(box.Text ?? "");
        cancel.Click += (_, _) => window.Close(null);
        window.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        return await window.ShowDialog<string?>(owner);
    }

    public static async Task AlertAsync(string title, string message)
    {
        var owner = MainWindow;
        if (owner is null) return;
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 90 };
        var body = new StackPanel();
        body.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Buttons(ok));
        var window = Dialog(title, body, 480);
        ok.Click += (_, _) => window.Close();
        await window.ShowDialog(owner);
    }

    /// <summary>Shows a long code (a pairing code) with a Copy button.</summary>
    public static async Task ShowCodeAsync(string title, string message, string code)
    {
        var owner = MainWindow;
        if (owner is null) return;
        var box = new TextBox { Text = code, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"), Margin = new Thickness(0, 12, 0, 0) };
        var copy = new Button { Content = "Copy", MinWidth = 90 };
        var close = new Button { Content = "Done", IsDefault = true, IsCancel = true, MinWidth = 90 };
        var body = new StackPanel();
        body.Children.Add(new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(box);
        body.Children.Add(Buttons(copy, close));
        var window = Dialog(title, body, 560);
        copy.Click += async (_, _) =>
        {
            if (window.Clipboard is { } c) await c.SetTextAsync(code);
            copy.Content = "Copied";
        };
        close.Click += (_, _) => window.Close();
        await window.ShowDialog(owner);
    }

    /// <summary>Shows an image at full size in its own resizable window.</summary>
    public static void ShowImage(string title, IImage image, Size size)
    {
        var window = new Window
        {
            Title = title,
            Width = Math.Min(size.Width + 40, 1600),
            Height = Math.Min(size.Height + 60, 1000),
            Icon = AppIcon.Create(),
            Content = new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = new Image { Source = image, Stretch = Stretch.None },
            },
        };
        window.Show();
    }
}

/// <summary>Status text ("online", ...) to its colour.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    public static readonly StatusBrushConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value as string) switch
        {
            "online" => "OnlineBrush",
            "offline" => "OfflineBrush",
            "rebooting" => "RebootingBrush",
            "shutdown" => "ShutdownBrush",
            _ => "UnknownBrush",
        };
        return Application.Current?.TryGetResource(key, null, out var brush) == true ? brush : Brushes.Gray;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Disabled agents are drawn faded.</summary>
public sealed class EnabledOpacityConverter : IValueConverter
{
    public static readonly EnabledOpacityConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is false ? 0.5 : 1.0;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
