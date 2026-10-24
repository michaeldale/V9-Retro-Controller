using Avalonia.Controls;
using Avalonia.Input;
using V9Retro.App.ViewModels;

namespace V9Retro.App.Views;

public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();
}

public partial class AgentsView : UserControl
{
    public AgentsView() => InitializeComponent();

    void GridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is AgentsViewModel vm) vm.OpenCommand.Execute(null);
    }

    void GridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is AgentsViewModel vm && sender is DataGrid grid)
            vm.SelectedRows = grid.SelectedItems.OfType<AgentRowViewModel>().ToList();
    }
}

public partial class DiscoverView : UserControl
{
    public DiscoverView() => InitializeComponent();

    void GridDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is DiscoverViewModel vm) vm.ConnectCommand.Execute(null);
    }
}

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
}

public partial class AgentEditWindow : Window
{
    public AgentEditWindow() => InitializeComponent();
}
