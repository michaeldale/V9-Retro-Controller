using Avalonia.Data.Converters;

namespace V9Retro.App.ViewModels;

public static class Converters
{
    /// <summary>Discovery row button: "Open" for a saved agent, "Connect" to control it without saving.</summary>
    public static readonly IValueConverter KnownOpenLabel =
        new FuncValueConverter<bool, string>(known => known ? "Open" : "Connect");

    public static readonly IValueConverter PasswordHint =
        new FuncValueConverter<bool, string>(set => set ? "(saved; type to change)" : "none");

    public static readonly IValueConverter LiveLabel =
        new FuncValueConverter<int, string>(seconds => $"every {seconds} s");
}
