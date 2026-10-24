namespace V9Retro.Protocol;

/// <summary>Guest status codes carried in ERROR_RESPONSE (include\v9xremote\status.h).</summary>
public enum V9xStatus : uint
{
    Ok = 0,
    BadMagic = 1,
    UnsupportedVersion = 2,
    UnsupportedOperation = 3,
    InvalidPayload = 4,
    LimitExceeded = 5,
    Busy = 6,
    NotFound = 7,
    AccessDenied = 8,
    CreateFailed = 10,
    IoFailed = 11,
    Timeout = 12,
    Cancelled = 13,
    CrcMismatch = 14,
    InternalError = 16,
}

/// <summary>Base for everything this library throws.</summary>
public class V9xException : Exception
{
    public V9xException(string message) : base(message) { }
    public V9xException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The guest answered with ERROR_RESPONSE.</summary>
public sealed class V9xGuestException : V9xException
{
    public V9xGuestException(V9xStatus status, uint nativeError, string detail)
        : base(Describe(status, nativeError, detail))
    {
        Status = status;
        NativeError = nativeError;
        Detail = detail;
    }

    public V9xStatus Status { get; }
    public uint NativeError { get; }
    public string Detail { get; }

    static string Describe(V9xStatus status, uint native, string detail)
    {
        var text = $"agent error {status}";
        if (native != 0) text += $" (Win32 {native})";
        if (!string.IsNullOrEmpty(detail)) text += $": {detail}";
        return text;
    }
}

/// <summary>Connect, read or write failed, or timed out. The connection is unusable afterwards.</summary>
public class V9xTransportException : V9xException
{
    public V9xTransportException(string message) : base(message) { }
    public V9xTransportException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>The peer sent something that does not follow the protocol.</summary>
public sealed class V9xProtocolException : V9xException
{
    public V9xProtocolException(string message) : base(message) { }
}

/// <summary>The agent does not advertise what an operation needs.</summary>
public sealed class V9xUnsupportedException : V9xException
{
    public V9xUnsupportedException(string message) : base(message) { }
}
