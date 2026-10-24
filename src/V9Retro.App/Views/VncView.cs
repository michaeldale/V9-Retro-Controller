using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using V9Retro.App.ViewModels;
using V9Retro.Vnc;

namespace V9Retro.App.Views;

/// <summary>Renders a <see cref="VncClient"/>'s framebuffer and forwards mouse and keyboard.
/// Updates from the receive thread are coalesced into at most one copy per rendered frame.</summary>
public sealed class VncView : Control
{
    public static readonly StyledProperty<VncClient?> ClientProperty =
        AvaloniaProperty.Register<VncView, VncClient?>(nameof(Client));
    public static readonly StyledProperty<bool> ScaleToFitProperty =
        AvaloniaProperty.Register<VncView, bool>(nameof(ScaleToFit), true);
    public static readonly StyledProperty<bool> ViewOnlyProperty =
        AvaloniaProperty.Register<VncView, bool>(nameof(ViewOnly));

    WriteableBitmap? _bitmap;
    VncClient? _attached;
    int _pending;
    byte _buttons;
    readonly HashSet<Key> _sentDown = new();

    static VncView()
    {
        AffectsRender<VncView>(ScaleToFitProperty);
        AffectsMeasure<VncView>(ScaleToFitProperty);
        FocusableProperty.OverrideDefaultValue<VncView>(true);
    }

    public VncView()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    public VncClient? Client { get => GetValue(ClientProperty); set => SetValue(ClientProperty, value); }
    public bool ScaleToFit { get => GetValue(ScaleToFitProperty); set => SetValue(ScaleToFitProperty, value); }
    public bool ViewOnly { get => GetValue(ViewOnlyProperty); set => SetValue(ViewOnlyProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientProperty) Attach(change.GetNewValue<VncClient?>());
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Attach(null);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach(Client);
    }

    void Attach(VncClient? client)
    {
        if (ReferenceEquals(_attached, client)) return;
        if (_attached is not null)
        {
            _attached.Framebuffer.FramebufferUpdated -= OnUpdated;
            _attached.Framebuffer.Resized -= OnResized;
        }
        _attached = client;
        _bitmap = null;
        if (client is not null)
        {
            client.Framebuffer.FramebufferUpdated += OnUpdated;
            client.Framebuffer.Resized += OnResized;
            ScheduleCopy();
        }
        InvalidateMeasure();
        InvalidateVisual();
    }

    void OnUpdated(IReadOnlyList<VncRect> _) => ScheduleCopy();

    void OnResized(int w, int h) => ScheduleCopy();

    void ScheduleCopy()
    {
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        Dispatcher.UIThread.Post(CopyFrame, DispatcherPriority.Render);
    }

    void CopyFrame()
    {
        Interlocked.Exchange(ref _pending, 0);
        if (_attached is not { } client) return;
        using var fb = client.Framebuffer.Lock();
        if (fb.Width == 0 || fb.Height == 0) return;
        if (_bitmap is null || _bitmap.PixelSize.Width != fb.Width || _bitmap.PixelSize.Height != fb.Height)
        {
            _bitmap = new WriteableBitmap(new PixelSize(fb.Width, fb.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            InvalidateMeasure();
        }
        using (var target = _bitmap.Lock())
        {
            var src = fb.Pixels;
            for (var y = 0; y < fb.Height; y++)
            {
                var row = src.Slice(y * fb.Stride, fb.Stride);
                unsafe
                {
                    fixed (byte* p = row)
                        Buffer.MemoryCopy(p, (void*)(target.Address + y * target.RowBytes), target.RowBytes, fb.Stride);
                }
            }
        }
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (ScaleToFit || _bitmap is null) return default;
        return new Size(_bitmap.PixelSize.Width, _bitmap.PixelSize.Height);
    }

    Rect ImageRect()
    {
        if (_bitmap is null) return default;
        double w = _bitmap.PixelSize.Width, h = _bitmap.PixelSize.Height;
        var scale = ScaleToFit ? Math.Min(Bounds.Width / w, Bounds.Height / h) : 1.0;
        if (double.IsNaN(scale) || scale <= 0) scale = 1;
        return new Rect((Bounds.Width - w * scale) / 2, (Bounds.Height - h * scale) / 2, w * scale, h * scale);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        if (_bitmap is null)
        {
            var text = new FormattedText(_attached is null ? "Not connected" : "Waiting for the first frame…",
                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 14, Brushes.Gray);
            context.DrawText(text, new Point((Bounds.Width - text.Width) / 2, (Bounds.Height - text.Height) / 2));
            return;
        }
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.MediumQuality }))
            context.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height), ImageRect());
    }

    // ---- mouse -----------------------------------------------------------------------

    bool ToRemote(Point p, out int x, out int y)
    {
        x = y = 0;
        if (_bitmap is null) return false;
        var r = ImageRect();
        x = (int)Math.Clamp((p.X - r.X) / r.Width * _bitmap.PixelSize.Width, 0, _bitmap.PixelSize.Width - 1);
        y = (int)Math.Clamp((p.Y - r.Y) / r.Height * _bitmap.PixelSize.Height, 0, _bitmap.PixelSize.Height - 1);
        return true;
    }

    static byte Mask(PointerPointProperties p) =>
        (byte)((p.IsLeftButtonPressed ? 1 : 0) | (p.IsMiddleButtonPressed ? 2 : 0) | (p.IsRightButtonPressed ? 4 : 0));

    void SendPointer(PointerEventArgs e, byte? extra = null)
    {
        if (ViewOnly || _attached is not { IsConnected: true } c || !ToRemote(e.GetPosition(this), out var x, out var y)) return;
        _buttons = Mask(e.GetCurrentPoint(this).Properties);
        _ = c.SendPointerAsync(x, y, (byte)(_buttons | (extra ?? 0)));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        SendPointer(e);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        SendPointer(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        SendPointer(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (ViewOnly || _attached is not { IsConnected: true } c || !ToRemote(e.GetPosition(this), out var x, out var y)) return;
        var bit = (byte)(e.Delta.Y > 0 ? 8 : 16);
        _ = WheelAsync(c, x, y, bit);
        e.Handled = true;
    }

    async Task WheelAsync(VncClient c, int x, int y, byte bit)
    {
        await c.SendPointerAsync(x, y, (byte)(_buttons | bit));
        await c.SendPointerAsync(x, y, _buttons);
    }

    // ---- keyboard --------------------------------------------------------------------

    /// <summary>Keys that produce no text are sent from KeyDown/KeyUp; printable text comes from
    /// TextInput (so layouts and Shift work), except with Ctrl or Alt held, when the key itself
    /// is sent so shortcuts like Ctrl+C reach the guest.</summary>
    static VncKey Map(Key key) => key switch
    {
        >= Key.A and <= Key.Z => VncKey.A + (key - Key.A),
        >= Key.D0 and <= Key.D9 => VncKey.D0 + (key - Key.D0),
        >= Key.F1 and <= Key.F12 => VncKey.F1 + (key - Key.F1),
        >= Key.NumPad0 and <= Key.NumPad9 => VncKey.NumPad0 + (key - Key.NumPad0),
        Key.Left => VncKey.Left, Key.Up => VncKey.Up, Key.Right => VncKey.Right, Key.Down => VncKey.Down,
        Key.Home => VncKey.Home, Key.End => VncKey.End, Key.PageUp => VncKey.PageUp, Key.PageDown => VncKey.PageDown,
        Key.Insert => VncKey.Insert, Key.Delete => VncKey.Delete, Key.Enter => VncKey.Enter, Key.Escape => VncKey.Escape,
        Key.Tab => VncKey.Tab, Key.Back => VncKey.Backspace, Key.Space => VncKey.Space,
        Key.LeftShift => VncKey.LeftShift, Key.RightShift => VncKey.RightShift,
        Key.LeftCtrl => VncKey.LeftCtrl, Key.RightCtrl => VncKey.RightCtrl,
        Key.LeftAlt => VncKey.LeftAlt, Key.RightAlt => VncKey.RightAlt,
        Key.LWin => VncKey.LeftWin, Key.RWin => VncKey.RightWin, Key.Apps => VncKey.Menu,
        Key.CapsLock => VncKey.CapsLock, Key.NumLock => VncKey.NumLock, Key.Scroll => VncKey.ScrollLock,
        Key.PrintScreen => VncKey.PrintScreen, Key.Pause => VncKey.Pause,
        Key.Multiply => VncKey.Multiply, Key.Add => VncKey.Add, Key.Subtract => VncKey.Subtract,
        Key.Decimal => VncKey.Decimal, Key.Divide => VncKey.Divide,
        _ => VncKey.None,
    };

    static bool Printable(VncKey k) => k is (>= VncKey.A and <= VncKey.Z) or (>= VncKey.D0 and <= VncKey.D9) or VncKey.Space
        or (>= VncKey.NumPad0 and <= VncKey.NumPad9) or VncKey.Multiply or VncKey.Add or VncKey.Subtract or VncKey.Decimal or VncKey.Divide;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (ViewOnly || _attached is not { IsConnected: true } c) return;
        var k = Map(e.Key);
        if (k == VncKey.None) return;
        var chord = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        if (Printable(k) && !chord) return; // TextInput sends it
        if (KeysymMap.ToKeysym(k) is { } sym)
        {
            _sentDown.Add(e.Key);
            _ = c.SendKeyAsync(sym, true);
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_attached is not { IsConnected: true } c || !_sentDown.Remove(e.Key)) return;
        if (KeysymMap.ToKeysym(Map(e.Key)) is { } sym) _ = c.SendKeyAsync(sym, false);
        e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (ViewOnly || _attached is not { IsConnected: true } c || string.IsNullOrEmpty(e.Text)) return;
        _ = TypeAsync(c, e.Text);
        e.Handled = true;
    }

    static async Task TypeAsync(VncClient c, string text)
    {
        foreach (var ch in text)
        {
            var sym = KeysymMap.FromChar(ch);
            await c.SendKeyAsync(sym, true);
            await c.SendKeyAsync(sym, false);
        }
    }

    protected override void OnLostFocus(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        // Release anything still held so the guest is not left with a stuck Ctrl or Alt.
        if (_attached is { IsConnected: true } c)
            foreach (var key in _sentDown)
                if (KeysymMap.ToKeysym(Map(key)) is { } sym) _ = c.SendKeyAsync(sym, false);
        _sentDown.Clear();
    }
}

/// <summary>A VNC session in its own resizable window, sharing the tab's connection.</summary>
public static class VncWindow
{
    public static void Open(VncTab tab)
    {
        var view = new VncView { [!VncView.ClientProperty] = new Avalonia.Data.Binding(nameof(VncTab.Client)) { Source = tab } };
        view[!VncView.ViewOnlyProperty] = new Avalonia.Data.Binding(nameof(VncTab.ViewOnly)) { Source = tab };
        var fb = tab.Client?.Framebuffer;
        var window = new Window
        {
            Title = $"{tab.Page.Name}: VNC",
            Width = Math.Clamp((fb?.Width ?? 800) + 16, 400, 1800),
            Height = Math.Clamp((fb?.Height ?? 600) + 40, 300, 1200),
            Icon = Services.AppIcon.Create(),
            Content = view,
        };
        window.Opened += (_, _) => view.Focus();
        window.Show();
    }
}
