using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using V9Retro.App.ViewModels;

namespace V9Retro.App.Views;

public partial class MainWindow : Window
{
    WindowNotificationManager? _notifications;

    public MainWindow()
    {
        InitializeComponent();
    }

    MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _notifications = new WindowNotificationManager(this) { Position = NotificationPosition.BottomRight, MaxItems = 4 };
        if (Vm is { } vm)
        {
            vm.ToastRaised += t => _notifications?.Show(new Notification(t.Title, t.Message,
                t.Status switch { "online" => NotificationType.Success, "offline" => NotificationType.Error, _ => NotificationType.Warning },
                TimeSpan.FromSeconds(6)));
            vm.Palette.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(PaletteViewModel.IsOpen) && vm.Palette.IsOpen)
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => PaletteBox.Focus());
            };
        }
    }

    void PaletteKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        switch (e.Key)
        {
            case Key.Down: vm.Palette.Move(1); e.Handled = true; break;
            case Key.Up: vm.Palette.Move(-1); e.Handled = true; break;
            case Key.Enter: vm.Palette.RunCommand.Execute(null); e.Handled = true; break;
            case Key.Escape: vm.Palette.IsOpen = false; e.Handled = true; break;
        }
    }

    void PaletteDoubleTapped(object? sender, TappedEventArgs e) => Vm?.Palette.RunCommand.Execute(null);

    void PaletteBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is { } vm) vm.Palette.IsOpen = false;
    }

    void PaletteCardPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
}
