using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;

namespace V9Retro.App.Services;

/// <summary>Copying from any DataGrid: right-click offers Copy cell, Copy row(s) and Copy all
/// (tab-separated, so it pastes into a spreadsheet), and Ctrl+C copies the selected rows.
/// Turned on for every grid by a style in App.axaml.</summary>
public static class GridCopy
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, bool>("Enabled", typeof(GridCopy));

    static readonly AttachedProperty<DataGridCell?> LastCellProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, DataGridCell?>("LastCell", typeof(GridCopy));

    static GridCopy()
    {
        EnabledProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
        {
            if (e.NewValue is true) Attach(grid);
        });
    }

    public static bool GetEnabled(DataGrid grid) => grid.GetValue(EnabledProperty);
    public static void SetEnabled(DataGrid grid, bool value) => grid.SetValue(EnabledProperty, value);

    static void Attach(DataGrid grid)
    {
        grid.CellPointerPressed += (_, e) => grid.SetValue(LastCellProperty, e.Cell);
        grid.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                e.Handled = true;
                await SetClipboard(grid, string.Join(Environment.NewLine, SelectedRows(grid)));
            }
        };

        var copyCell = new MenuItem { Header = "Copy cell" };
        copyCell.Click += async (_, _) =>
        {
            if (grid.GetValue(LastCellProperty) is { } cell) await SetClipboard(grid, CellText(cell));
        };
        var copyRows = new MenuItem { Header = "Copy row", InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
        copyRows.Click += async (_, _) => await SetClipboard(grid, string.Join(Environment.NewLine, SelectedRows(grid)));
        var copyAll = new MenuItem { Header = "Copy visible rows (with headers)" };
        copyAll.Click += async (_, _) => await SetClipboard(grid, AllRows(grid));

        var menu = grid.ContextMenu ?? new ContextMenu();
        var items = new List<object> { copyCell, copyRows, copyAll };
        if (menu.Items.Count > 0) items.Add(new Separator());
        for (var i = items.Count - 1; i >= 0; i--) menu.Items.Insert(0, items[i]);
        grid.ContextMenu = menu;
    }

    static Task SetClipboard(Visual from, string text) =>
        TopLevel.GetTopLevel(from)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;

    /// <summary>The text a cell shows: every text element in it, in order.</summary>
    static string CellText(DataGridCell cell) =>
        string.Join(" ", cell.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text).Where(t => !string.IsNullOrWhiteSpace(t)));

    /// <summary>Realised rows in display order. Grids virtualise, so rows scrolled far out of view
    /// are not included; selected rows are almost always on screen.</summary>
    static IEnumerable<DataGridRow> Rows(DataGrid grid) =>
        grid.GetVisualDescendants().OfType<DataGridRow>().Where(r => r.IsVisible).OrderBy(r => r.Index);

    static string RowText(DataGridRow row) =>
        string.Join("\t", row.GetVisualDescendants().OfType<DataGridCell>().Select(CellText));

    static IEnumerable<string> SelectedRows(DataGrid grid)
    {
        var selected = grid.SelectedItems.Cast<object>().ToHashSet();
        return Rows(grid).Where(r => r.DataContext is { } item && selected.Contains(item)).Select(RowText);
    }

    static string AllRows(DataGrid grid)
    {
        var header = string.Join("\t", grid.Columns.Where(c => c.IsVisible).Select(c => c.Header?.ToString() ?? ""));
        return string.Join(Environment.NewLine, Rows(grid).Select(RowText).Prepend(header));
    }
}
