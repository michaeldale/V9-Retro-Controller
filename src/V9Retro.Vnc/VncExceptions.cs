namespace V9Retro.Vnc;

/// <summary>Base class for every failure this library reports.</summary>
public class VncException : Exception
{
    /// <summary>Creates the exception.</summary>
    public VncException(string message) : base(message) { }

    /// <summary>Creates the exception with an inner cause.</summary>
    public VncException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The server sent something this client cannot parse or does not support.</summary>
public class VncProtocolException : VncException
{
    /// <summary>Creates the exception.</summary>
    public VncProtocolException(string message) : base(message) { }

    /// <summary>Creates the exception with an inner cause.</summary>
    public VncProtocolException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The server refused the connection before security, with a reason
/// (RFB's connection-failed reply). <see cref="Reason"/> is the server's text.
/// </summary>
public class VncConnectionRefusedException : VncException
{
    /// <summary>Creates the exception.</summary>
    public VncConnectionRefusedException(string reason)
        : base(string.IsNullOrEmpty(reason) ? "The VNC server refused the connection." : reason)
    {
        Reason = reason;
    }

    /// <summary>The reason the server gave, possibly empty.</summary>
    public string Reason { get; }
}

/// <summary>
/// Another viewer already holds the session: V9XVNC.EXE serves one viewer at a
/// time and turns the next away with a connection-failed reason.
/// </summary>
public sealed class VncViewerBusyException : VncConnectionRefusedException
{
    /// <summary>Creates the exception.</summary>
    public VncViewerBusyException(string reason) : base(reason) { }
}

/// <summary>VNC authentication failed: wrong password, or none given when one is required.</summary>
public sealed class VncAuthenticationException : VncException
{
    /// <summary>Creates the exception.</summary>
    public VncAuthenticationException(string message, bool passwordRequired = false) : base(message)
    {
        PasswordRequired = passwordRequired;
    }

    /// <summary>True when the server wants a password and the caller supplied none.</summary>
    public bool PasswordRequired { get; }
}
