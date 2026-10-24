using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using V9Retro.Core.Model;
using V9Retro.Discovery;

namespace V9Retro.Remote;

public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Agents leave the host with the VNC password blob replaced by a marker: it is DPAPI
    /// data for the host's user and means nothing elsewhere. Clients ask for the password itself.</summary>
    public const string PasswordMarker = "remote";

    public static Agent ForWire(Agent a)
    {
        if (a.VncPassword is null) return a;
        var copy = a.Clone();
        copy.VncPassword = PasswordMarker;
        return copy;
    }
}

public sealed record ErrorDto(string Kind, string Message, uint? Status = null, uint? NativeError = null, string? Detail = null);

public sealed record DiscoveredDto(string? InstallId, string InstanceName, string Name, string Host, int Port, string? Version,
    string? Build, bool IsDos, string? OsVersion, DateTimeOffset LastSeen)
{
    public static DiscoveredDto From(DiscoveredAgent a) =>
        new(a.InstallId, a.InstanceName, a.Name, a.Host.ToString(), a.Port, a.Version, a.Build, a.IsDos, a.OsVersion, a.LastSeen);

    public DiscoveredAgent ToAgent() => new()
    {
        InstallId = InstallId, InstanceName = InstanceName, Name = Name, Host = IPAddress.Parse(Host), Port = Port,
        Version = Version, Build = Build, IsDos = IsDos, OsVersion = OsVersion, LastSeen = LastSeen,
    };
}

/// <summary>One message on the event stream (host → client).</summary>
public sealed record StreamMessage(string Kind)
{
    public Agent? Agent { get; init; }
    public long? Id { get; init; }
    public long? ShotId { get; init; }
    public AgentEvent? Event { get; init; }
    public DiscoveredDto? Discovered { get; init; }
    public bool? Flag { get; init; }

    public const string AgentAdded = "agentAdded", AgentChanged = "agentChanged", AgentRemoved = "agentRemoved",
        EventLogged = "event", Screenshot = "screenshot", Inventory = "inventory", DiscoveryFound = "discovered",
        DiscoveryGone = "gone", Browsing = "browsing", Settings = "settings", Hello = "hello";
}

public sealed record HelloDto(string Name, string Machine, string Version, string User);

public sealed record AddAgentRequest(string Name, string Host, int Port, string? Notes, string? InstallId, bool Enabled);

public sealed record UpdateAgentRequest(string Name, string Host, int Port, bool Enabled, string? Notes, int VncPort,
    string? VncPassword, bool ChangePassword);

public sealed record TextValue(string? Value);

/// <summary>Address, certificate fingerprint and token in one pasteable string: "v9r1." then
/// base64url JSON. The fingerprint pins the host's self-signed certificate, so the token is only
/// ever sent to that host.</summary>
public sealed record PairingCode(string Url, string Fingerprint, string Token)
{
    const string Prefix = "v9r1.";

    public string Encode() => Prefix + Base64Url(JsonSerializer.SerializeToUtf8Bytes(this, ApiJson.Options));

    public static bool TryDecode(string? text, out PairingCode? code)
    {
        code = null;
        text = text?.Trim();
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        try
        {
            var b64 = text[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            code = JsonSerializer.Deserialize<PairingCode>(Convert.FromBase64String(b64), ApiJson.Options);
            return code is { Url.Length: > 0, Fingerprint.Length: > 0, Token.Length: > 0 };
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return false;
        }
    }

    internal static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>A byte stream over a WebSocket's binary messages, so the agent protocol and VNC run
/// through the host unchanged. A close from either side reads as end of stream.</summary>
public sealed class WebSocketStream(WebSocket socket, IDisposable? owner = null) : Stream
{
    ArraySegment<byte> _pending;

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_pending.Count == 0)
        {
            var temp = new byte[64 * 1024];
            while (true)
            {
                WebSocketReceiveResult result;
                try { result = await socket.ReceiveAsync(temp, ct).ConfigureAwait(false); }
                catch (WebSocketException ex) { throw new IOException("tunnel closed: " + ex.Message, ex); }
                if (result.MessageType == WebSocketMessageType.Close) return 0;
                if (result.Count == 0) continue;
                _pending = new ArraySegment<byte>(temp, 0, result.Count);
                break;
            }
        }
        var n = Math.Min(buffer.Length, _pending.Count);
        _pending.AsMemory(0, n).CopyTo(buffer);
        _pending = _pending.Slice(n);
        return n;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        try { await socket.SendAsync(buffer, WebSocketMessageType.Binary, true, ct).ConfigureAwait(false); }
        catch (WebSocketException ex) { throw new IOException("tunnel closed: " + ex.Message, ex); }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, default).GetAwaiter().GetResult();
    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer, offset, count, default).GetAwaiter().GetResult();
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                    socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).Wait(1000);
            }
            catch (Exception) { /* already gone */ }
            socket.Dispose();
            owner?.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Reads one whole text message, or null when the socket closes.</summary>
    public static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }
}
