namespace V9Retro.Vnc;

/// <summary>
/// Keys the UI layer can name without depending on any UI toolkit. Map the toolkit's
/// key enum onto this, then use <see cref="KeysymMap"/> for the X11 keysym. Printable
/// characters are better sent with <see cref="KeysymMap.FromChar"/> from text input.
/// </summary>
public enum VncKey
{
    /// <summary>No key.</summary>
    None = 0,
#pragma warning disable CS1591 // The names are the documentation.
    A, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D0, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    Left, Up, Right, Down,
    Home, End, PageUp, PageDown, Insert, Delete,
    Enter, Escape, Tab, Backspace, Space,
    LeftShift, RightShift, LeftCtrl, RightCtrl, LeftAlt, RightAlt,
    LeftWin, RightWin, Menu,
    CapsLock, NumLock, ScrollLock, PrintScreen, Pause,
    NumPad0, NumPad1, NumPad2, NumPad3, NumPad4, NumPad5, NumPad6, NumPad7, NumPad8, NumPad9,
    Multiply, Add, Subtract, Decimal, Divide, NumPadEnter,
#pragma warning restore CS1591
}

/// <summary>X11 keysyms for <see cref="VncKey"/>, as the RFB KeyEvent carries them.</summary>
public static class KeysymMap
{
    /// <summary>Shift_L.</summary>
    public const uint ShiftL = 0xFFE1;
    /// <summary>Control_L.</summary>
    public const uint ControlL = 0xFFE3;
    /// <summary>Alt_L.</summary>
    public const uint AltL = 0xFFE9;
    /// <summary>Delete.</summary>
    public const uint Delete = 0xFFFF;

    private static readonly Dictionary<VncKey, uint> Map = Build();

    /// <summary>The keysym for a key, or null for <see cref="VncKey.None"/>.</summary>
    /// <remarks>Letters map to lowercase keysyms; the server adds Shift as the held modifiers say.</remarks>
    public static uint? ToKeysym(VncKey key) => Map.TryGetValue(key, out var keysym) ? keysym : null;

    /// <summary>The keysym for a typed character: Latin-1 maps directly, others use the 0x01000000 Unicode range.</summary>
    public static uint FromChar(char c) => c switch
    {
        '\n' or '\r' => 0xFF0D,
        '\t' => 0xFF09,
        '\b' => 0xFF08,
        < (char)0x100 => c,
        _ => 0x01000000u | c,
    };

    private static Dictionary<VncKey, uint> Build()
    {
        var map = new Dictionary<VncKey, uint>();
        for (int i = 0; i < 26; i++) map[VncKey.A + i] = (uint)('a' + i);
        for (int i = 0; i < 10; i++) map[VncKey.D0 + i] = (uint)('0' + i);
        for (int i = 0; i < 12; i++) map[VncKey.F1 + i] = 0xFFBEu + (uint)i;
        for (int i = 0; i < 10; i++) map[VncKey.NumPad0 + i] = 0xFFB0u + (uint)i;
        map[VncKey.Left] = 0xFF51;
        map[VncKey.Up] = 0xFF52;
        map[VncKey.Right] = 0xFF53;
        map[VncKey.Down] = 0xFF54;
        map[VncKey.Home] = 0xFF50;
        map[VncKey.End] = 0xFF57;
        map[VncKey.PageUp] = 0xFF55;
        map[VncKey.PageDown] = 0xFF56;
        map[VncKey.Insert] = 0xFF63;
        map[VncKey.Delete] = Delete;
        map[VncKey.Enter] = 0xFF0D;
        map[VncKey.Escape] = 0xFF1B;
        map[VncKey.Tab] = 0xFF09;
        map[VncKey.Backspace] = 0xFF08;
        map[VncKey.Space] = 0x0020;
        map[VncKey.LeftShift] = ShiftL;
        map[VncKey.RightShift] = 0xFFE2;
        map[VncKey.LeftCtrl] = ControlL;
        map[VncKey.RightCtrl] = 0xFFE4;
        map[VncKey.LeftAlt] = AltL;
        map[VncKey.RightAlt] = 0xFFEA;
        map[VncKey.LeftWin] = 0xFFEB;
        map[VncKey.RightWin] = 0xFFEC;
        map[VncKey.Menu] = 0xFF67;
        map[VncKey.CapsLock] = 0xFFE5;
        map[VncKey.NumLock] = 0xFF7F;
        map[VncKey.ScrollLock] = 0xFF14;
        map[VncKey.PrintScreen] = 0xFF61;
        map[VncKey.Pause] = 0xFF13;
        map[VncKey.Multiply] = 0xFFAA;
        map[VncKey.Add] = 0xFFAB;
        map[VncKey.Subtract] = 0xFFAD;
        map[VncKey.Decimal] = 0xFFAE;
        map[VncKey.Divide] = 0xFFAF;
        map[VncKey.NumPadEnter] = 0xFF8D;
        return map;
    }
}
