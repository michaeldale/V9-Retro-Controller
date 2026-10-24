using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using V9Retro.App.ViewModels;
using MouseButton = V9Retro.Protocol.MouseButton;

namespace V9Retro.App.Views;

public partial class AgentPageView : UserControl
{
    public AgentPageView() => InitializeComponent();
}

public partial class ConsoleTabView : UserControl
{
    public ConsoleTabView()
    {
        InitializeComponent();
        OutputBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) OutputBox.CaretIndex = OutputBox.Text?.Length ?? 0;
        };
    }

    void CommandKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ConsoleTab vm) return;
        switch (e.Key)
        {
            case Key.Enter: vm.RunCommandCommand.Execute(null); e.Handled = true; break;
            case Key.Up: vm.History(-1); e.Handled = true; break;
            case Key.Down: vm.History(1); e.Handled = true; break;
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control) && vm.Running:
                vm.CancelCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}

public partial class FilesTabView : UserControl
{
    public FilesTabView()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        });
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            if (DataContext is not FilesTab vm || e.DataTransfer.TryGetFiles() is not { } items) return;
            var paths = items.Select(i => i.TryGetLocalPath()).OfType<string>().ToList();
            if (paths.Count > 0) await vm.UploadFiles(paths);
        });
    }

    void GridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is FilesTab vm) vm.OpenCommand.Execute(null);
    }
}

public partial class ControlTabView : UserControl
{
    public ControlTabView() => InitializeComponent();

    /// <summary>Maps a point on the stretched image back to guest screen pixels.</summary>
    bool ToScreen(Point p, out int x, out int y)
    {
        x = y = 0;
        if (ScreenImage.Source is not { } source) return false;
        var size = source.Size;
        var bounds = ScreenImage.Bounds.Size;
        var scale = Math.Min(bounds.Width / size.Width, bounds.Height / size.Height);
        var offX = (bounds.Width - size.Width * scale) / 2;
        var offY = (bounds.Height - size.Height * scale) / 2;
        x = (int)((p.X - offX) / scale);
        y = (int)((p.Y - offY) / scale);
        return x >= 0 && y >= 0 && x < size.Width && y < size.Height;
    }

    void ImagePressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not ControlTab vm || !ToScreen(e.GetPosition(ScreenImage), out var x, out var y)) return;
        var point = e.GetCurrentPoint(ScreenImage).Properties;
        var button = point.IsRightButtonPressed ? MouseButton.Right : point.IsMiddleButtonPressed ? MouseButton.Middle : MouseButton.Left;
        _ = vm.ClickAt(x, y, button, e.ClickCount >= 2);
        e.Handled = true;
    }

    void ImageWheel(object? sender, PointerWheelEventArgs e)
    {
        if (DataContext is not ControlTab vm || !ToScreen(e.GetPosition(ScreenImage), out var x, out var y)) return;
        _ = vm.Scroll(x, y, e.Delta.Y > 0 ? 1 : -1);
        e.Handled = true;
    }
}

public partial class VncTabView : UserControl
{
    public VncTabView() => InitializeComponent();
}
