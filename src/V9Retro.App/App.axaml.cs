using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using V9Retro.App.Services;
using V9Retro.App.ViewModels;
using V9Retro.App.Views;
using V9Retro.Core;
using V9Retro.Core.Services;
using V9Retro.Remote;
using V9Retro.Remote.Client;

namespace V9Retro.App;

public partial class App : Application
{
    AppServices? _services;
    TrayIcon? _tray;
    MainWindow? _window;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownRequested += (_, _) => { if (_services is not null) _services.Quitting = true; };
            desktop.Exit += (_, _) =>
            {
                _tray?.Dispose();
                _services?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
            };
            _ = StartAsync(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Opens this computer's database, or connects to the host named in client.json.</summary>
    async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        UpdateInstaller.CleanUp();
        var config = ClientConfig.Load();
        IControllerBackend? backend = null;
        while (backend is null)
        {
            if (!config.IsRemote)
            {
                var local = new LocalBackend(new CoreHost());
                await local.StartAsync();
                backend = local;
                break;
            }
            var splash = Splash($"Connecting to {config.Url}…");
            splash.Show();
            try
            {
                backend = await RemoteBackend.ConnectAsync(config, new DefaultSecretProtector());
            }
            catch (Exception ex)
            {
                splash.Close();
                var choice = await StartupProblemAsync(config.Url ?? "the host", Ui.Describe(ex));
                if (choice == "quit")
                {
                    desktop.Shutdown();
                    return;
                }
                if (choice == "local")
                {
                    config.Mode = "local";
                    config.Save();
                }
                continue;
            }
            splash.Close();
        }

        _services = new AppServices(backend, config) { Restart = () => Restart(desktop) };
        var main = new MainWindowViewModel(_services);
        _window = new MainWindow { DataContext = main, Icon = AppIcon.Create() };
        if (backend.IsRemote) _window.Title = $"V9 Retro Controller — {backend.Location}";
        _window.Closing += (_, e) =>
        {
            if (_services.Quitting || !backend.Settings.CloseToTray) return;
            e.Cancel = true;
            _window.Hide();
        };
        desktop.MainWindow = _window;
        _window.Show();
        CreateTray(desktop, main);
    }

    static Window Splash(string text) => new()
    {
        Title = "V9 Retro Controller",
        Width = 380,
        SizeToContent = SizeToContent.Height,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterScreen,
        Icon = AppIcon.Create(),
        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                new ProgressBar { IsIndeterminate = true },
            },
        },
    };

    static Task<string> StartupProblemAsync(string url, string message)
    {
        var done = new TaskCompletionSource<string>();
        var window = new Window
        {
            Title = "V9 Retro Controller",
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Icon = AppIcon.Create(),
        };
        Button Choice(string label, string value)
        {
            var b = new Button { Content = label, MinWidth = 100 };
            b.Click += (_, _) => { done.TrySetResult(value); window.Close(); };
            return b;
        }
        window.Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Cannot connect to the host", FontSize = 18, FontWeight = FontWeight.SemiBold },
                new SelectableTextBlock { Text = $"{url}\n\n{message}", TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { Choice("Quit", "quit"), Choice("Use this computer", "local"), Choice("Retry", "retry") },
                },
            },
        };
        window.Closed += (_, _) => done.TrySetResult("quit");
        window.Show();
        return done.Task;
    }

    /// <summary>Starts a fresh copy of the app and quits this one (after switching local/remote).</summary>
    void Restart(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (Environment.ProcessPath is { } exe)
        {
            var args = Environment.GetCommandLineArgs().Skip(1);
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            // When run with "dotnet V9RetroController.dll" the process is dotnet; keep the dll argument.
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "V9RetroController.dll"));
            foreach (var a in args) start.ArgumentList.Add(a);
            Process.Start(start);
        }
        if (_services is not null) _services.Quitting = true;
        desktop.Shutdown();
    }

    void CreateTray(IClassicDesktopStyleApplicationLifetime desktop, MainWindowViewModel main)
    {
        var show = new NativeMenuItem("Show V9 Retro Controller");
        show.Click += (_, _) => ShowWindow();
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) =>
        {
            _services!.Quitting = true;
            desktop.Shutdown();
        };
        _tray = new TrayIcon
        {
            Icon = AppIcon.Create(),
            ToolTipText = "V9 Retro Controller",
            Menu = new NativeMenu { Items = { show, new NativeMenuItemSeparator(), quit } },
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ShowWindow();
        main.Dashboard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DashboardViewModel.OnlineCount) or nameof(DashboardViewModel.OfflineCount))
                _tray.ToolTipText = $"V9 Retro Controller: {main.Dashboard.OnlineCount} online, {main.Dashboard.OfflineCount} offline";
        };
    }

    void ShowWindow()
    {
        if (_window is null) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }
}
