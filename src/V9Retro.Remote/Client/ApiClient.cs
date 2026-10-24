using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using V9Retro.Protocol;
using V9Retro.Remote.Server;

namespace V9Retro.Remote.Client;

/// <summary>HTTPS to a V9 Retro Controller host. The host's certificate is self-signed, so it is
/// trusted only if its SHA-256 fingerprint is the one from pairing; name and chain are not checked.
/// Host errors come back as the same exception types the local backend throws.</summary>
public sealed class ApiClient : IDisposable
{
    readonly HttpClient _http;
    readonly SocketsHttpHandler _handler;
    readonly string _token;

    public ApiClient(string baseUrl, string fingerprint, string token)
    {
        BaseUri = new Uri(baseUrl.TrimEnd('/') + "/");
        Fingerprint = fingerprint.Replace(":", "").Trim().ToUpperInvariant();
        _token = token;
        _handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Pinned },
        };
        _http = new HttpClient(_handler, disposeHandler: false) { BaseAddress = BaseUri, Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public Uri BaseUri { get; }
    public string Fingerprint { get; }

    bool Pinned(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors) =>
        certificate is not null
        && CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()))),
            Convert.FromHexString(Fingerprint));

    static Exception ToException(ErrorDto? e, HttpStatusCode status) => e?.Kind switch
    {
        "guest" => new V9xGuestException((V9xStatus)(e.Status ?? 16), e.NativeError ?? 0, e.Detail ?? ""),
        "unsupported" => new V9xUnsupportedException(e.Message),
        "transport" => new V9xTransportException(e.Message),
        "protocol" => new V9xProtocolException(e.Message),
        "agent" => new V9xException(e.Message),
        "argument" => new ArgumentException(e.Message),
        "invalid" => new InvalidOperationException(e.Message),
        "auth" => new RemoteAuthException(e.Message),
        _ => new V9xException(e?.Message ?? $"host answered {(int)status} {status}"),
    };

    async Task<HttpResponseMessage> SendCore(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new RemoteUnavailableException($"cannot reach the host at {BaseUri.Authority}: {Root(ex).Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new RemoteUnavailableException($"the host at {BaseUri.Authority} did not answer in time", ex);
        }
        if (response.IsSuccessStatusCode) return response;
        ErrorDto? error = null;
        try { error = await response.Content.ReadFromJsonAsync<ErrorDto>(ApiJson.Options, ct).ConfigureAwait(false); }
        catch (Exception) { /* not JSON */ }
        var status = response.StatusCode;
        response.Dispose();
        throw ToException(error, status);
    }

    static Exception Root(Exception ex) => ex.InnerException is { } inner ? Root(inner) : ex;

    public async Task<T> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var response = await SendCore(new HttpRequestMessage(HttpMethod.Get, path), ct).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<T>(ApiJson.Options, ct).ConfigureAwait(false))!;
    }

    /// <summary>GET that maps 404 / 204 to null.</summary>
    public async Task<T?> GetOrNullAsync<T>(string path, CancellationToken ct = default) where T : class
    {
        try
        {
            using var response = await SendCore(new HttpRequestMessage(HttpMethod.Get, path), ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NoContent) return null;
            return await response.Content.ReadFromJsonAsync<T>(ApiJson.Options, ct).ConfigureAwait(false);
        }
        catch (V9xException ex) when (ex.Message.Contains("404", StringComparison.Ordinal))
        {
            return null;
        }
    }

    public async Task<byte[]?> GetBytesAsync(string path, CancellationToken ct = default)
    {
        try
        {
            using var response = await SendCore(new HttpRequestMessage(HttpMethod.Get, path), ct).ConfigureAwait(false);
            return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        catch (V9xException ex) when (ex.Message.Contains("404", StringComparison.Ordinal))
        {
            return null;
        }
    }

    public async Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        using var response = await SendCore(Request(method, path, body), ct).ConfigureAwait(false);
        return (await response.Content.ReadFromJsonAsync<T>(ApiJson.Options, ct).ConfigureAwait(false))!;
    }

    public async Task SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        using var response = await SendCore(Request(method, path, body), ct).ConfigureAwait(false);
    }

    static HttpRequestMessage Request(HttpMethod method, string path, object? body) => new(method, path)
    {
        Content = body is null ? null : JsonContent.Create(body, body.GetType(), options: ApiJson.Options),
    };

    /// <summary>A WebSocket on the host (event stream or tunnel), pinned and authenticated the same way.</summary>
    public async Task<WebSocket> OpenWebSocketAsync(string pathAndQuery, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + _token);
        ws.Options.RemoteCertificateValidationCallback = Pinned;
        ws.Options.CollectHttpResponseDetails = true;
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var uri = new UriBuilder(new Uri(BaseUri, pathAndQuery)) { Scheme = BaseUri.Scheme == "https" ? "wss" : "ws" }.Uri;
        try
        {
            await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
            return ws;
        }
        catch (WebSocketException ex)
        {
            var headers = ws.HttpResponseHeaders;
            var status = ws.HttpStatusCode;
            ws.Dispose();
            string? Header(string name) => headers is not null && headers.TryGetValue(name, out var v) ? v.FirstOrDefault() : null;
            if (Header(ApiServer.ErrorHeader) is { } message)
                throw ToException(new ErrorDto(Header(ApiServer.ErrorKindHeader) ?? "agent", message), status);
            if (status == HttpStatusCode.Unauthorized) throw new RemoteAuthException("this token is not valid or was revoked on the host");
            throw new RemoteUnavailableException($"cannot reach the host at {BaseUri.Authority}: {Root(ex).Message}", ex);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }
}

/// <summary>The host could not be reached (network, host app closed, wrong address).</summary>
public sealed class RemoteUnavailableException(string message, Exception? inner = null)
    : V9xTransportException(message, inner ?? new IOException(message));

/// <summary>The host refused the token.</summary>
public sealed class RemoteAuthException(string message) : V9xException(message);
