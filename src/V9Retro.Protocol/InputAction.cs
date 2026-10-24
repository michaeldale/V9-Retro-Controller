using V9Retro.Protocol.Wire;

namespace V9Retro.Protocol;

public enum MouseButton : byte { Left = 0, Right = 1, Middle = 2 }

/// <summary>One INPUT_REQUEST action. A batch of up to 256 is applied atomically, in order.</summary>
public abstract record InputAction
{
    public const int MaxActions = 256;
    public const int MaxDelayMs = 10_000;

    internal abstract void Write(PayloadWriter w);

    /// <summary>Absolute move to screen pixels (opcode 1).</summary>
    public sealed record MoveTo(int X, int Y) : InputAction
    {
        internal override void Write(PayloadWriter w) => w.U8(1).S32(X).S32(Y);
    }

    /// <summary>Mouse button down or up (opcode 2).</summary>
    public sealed record Button(MouseButton Which, bool Down) : InputAction
    {
        internal override void Write(PayloadWriter w) => w.U8(2).U8((byte)Which).U8(Down ? (byte)1 : (byte)0);
    }

    /// <summary>Wheel notches; positive scrolls away from the user (opcode 3).</summary>
    public sealed record Wheel(short Notches) : InputAction
    {
        internal override void Write(PayloadWriter w) => w.U8(3).S16(Notches);
    }

    /// <summary>Win32 virtual-key down or up (opcode 4).</summary>
    public sealed record Key(byte VirtualKey, bool Down) : InputAction
    {
        internal override void Write(PayloadWriter w) => w.U8(4).U8(VirtualKey).U8(Down ? (byte)1 : (byte)0);
    }

    /// <summary>Types 7-bit ASCII text through VkKeyScan on the guest (opcode 5).</summary>
    public sealed record TypeText(string Text) : InputAction
    {
        internal override void Write(PayloadWriter w)
        {
            foreach (var ch in Text)
                if (ch > 0x7F) throw new ArgumentException("typed text must be 7-bit ASCII");
            w.U8(5).U16((ushort)Text.Length);
            foreach (var ch in Text) w.U8((byte)ch);
        }
    }

    /// <summary>Pause between actions, at most 10 s (opcode 6).</summary>
    public sealed record Delay(ushort Milliseconds) : InputAction
    {
        internal override void Write(PayloadWriter w) =>
            w.U8(6).U16(Math.Min(Milliseconds, (ushort)MaxDelayMs));
    }

    /// <summary>Relative motion, for programs that read deltas (opcode 7, needs
    /// <see cref="Capabilities.InputRelative"/>).</summary>
    public sealed record MoveBy(int Dx, int Dy) : InputAction
    {
        internal override void Write(PayloadWriter w) => w.U8(7).S32(Dx).S32(Dy);
    }

    /// <summary>A left click at a point: move, down, up.</summary>
    public static IEnumerable<InputAction> Click(int x, int y, MouseButton button = MouseButton.Left)
    {
        yield return new MoveTo(x, y);
        yield return new Button(button, true);
        yield return new Button(button, false);
    }

    /// <summary>Press a chord such as Ctrl+Esc: every key down in order, then up in reverse.</summary>
    public static IEnumerable<InputAction> Chord(params byte[] virtualKeys)
    {
        foreach (var vk in virtualKeys) yield return new Key(vk, true);
        for (var i = virtualKeys.Length - 1; i >= 0; i--) yield return new Key(virtualKeys[i], false);
    }
}
