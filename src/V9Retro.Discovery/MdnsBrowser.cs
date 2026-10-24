using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace V9Retro.Discovery;

/// <summary>
/// Finds v9x remote agents over mDNS / DNS-SD. <see cref="BrowseAsync"/> sends a
/// one-shot legacy query and collects the unicast replies; <see cref="StartListening"/>
/// also follows announcements and goodbyes on the multicast group. Both feed one
/// agent table, and the events fire on thread-pool threads.
/// </summary>
public sealed class MdnsBrowser : IDisposable
{
    public const string ServiceType = "_v9xremote._tcp.local";
    public const string ServiceEnumerationType = "_services._dns-sd._udp.local";
    public const int MdnsPort = 5353;
    public static readonly IPAddress MdnsGroup = IPAddress.Parse("224.0.0.251");

    private const int ReceiveBufferSize = 9000;

    private readonly object _gate = new();
    private readonly AgentCache _cache = new();
    private readonly Action<string>? _log;
    private Socket? _listener;
    private CancellationTokenSource? _listenCts;
    private bool _disposed;

    /// <param name="log">Optional sink for diagnostics (socket failures, malformed packets).</param>
    public MdnsBrowser(Action<string>? log = null)
    {
        _log = log;
    }

    public event EventHandler<DiscoveredAgentEventArgs>? AgentFound;
    public event EventHandler<DiscoveredAgentEventArgs>? AgentUpdated;
    public event EventHandler<DiscoveredAgentEventArgs>? AgentGone;

    /// <summary>Every agent currently known, from browses and the listener.</summary>
    public IReadOnlyList<DiscoveredAgent> Agents
    {
        get
        {
            lock (_gate)
                return _cache.Agents;
        }
    }

    public bool IsListening => _listener is not null;

    /// <summary>
    /// Sends a PTR query for <see cref="ServiceType"/> from an ephemeral port on
    /// every up IPv4 interface to 224.0.0.251:5353, so agents reply unicast, and
    /// collects replies for <paramref name="duration"/>. Returns the agents that
    /// answered this browse. Cancellation ends the browse early and returns what
    /// arrived so far.
    /// </summary>
    public async Task<IReadOnlyList<DiscoveredAgent>> BrowseAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] query = DnsMessage.BuildQuery((ushort)Random.Shared.Next(1, 0x10000), ServiceType, DnsRecordType.Ptr);
        var sockets = new List<Socket>();
        var answered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (IPAddress address in LocalIPv4Addresses())
            {
                Socket? socket = TrySendQuery(address, query);
                if (socket is not null)
                    sockets.Add(socket);
            }
            if (sockets.Count == 0 && TrySendQuery(IPAddress.Any, query) is { } fallback)
                sockets.Add(fallback);
            if (sockets.Count == 0)
            {
                Log("mDNS browse: no IPv4 interface could send the query");
                return [];
            }

            using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            window.CancelAfter(duration);
            await Task.WhenAll(sockets.Select(s => ReceiveLoopAsync(s, answered, window.Token))).ConfigureAwait(false);
        }
        finally
        {
            foreach (Socket socket in sockets)
                socket.Dispose();
        }

        lock (_gate)
            return _cache.Agents.Where(a => answered.Contains(a.InstanceName)).ToList();
    }

    /// <summary>
    /// Binds UDP 5353 with address reuse, joins 224.0.0.251 on every up IPv4
    /// interface and processes announcements and goodbyes until
    /// <see cref="StopListening"/>. Returns false, after logging, when the port or
    /// group is unavailable; it never throws for socket errors.
    /// </summary>
    public bool StartListening()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_listener is not null)
                return true;
        }

        Socket? socket = null;
        try
        {
            socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.ExclusiveAddressUse = false;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));

            int joined = 0;
            foreach (IPAddress address in LocalIPv4Addresses())
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MdnsGroup, address));
                    joined++;
                }
                catch (SocketException ex)
                {
                    Log($"mDNS listener: could not join {MdnsGroup} on {address}: {ex.Message}");
                }
            }
            if (joined == 0)
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(MdnsGroup, IPAddress.Any));
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            Log($"mDNS listener unavailable on UDP {MdnsPort}: {ex.Message}");
            socket?.Dispose();
            return false;
        }

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            if (_listener is not null)
            {
                socket.Dispose();
                cts.Dispose();
                return true;
            }
            _listener = socket;
            _listenCts = cts;
        }
        _ = ReceiveLoopAsync(socket, null, cts.Token);
        return true;
    }

    public void StopListening()
    {
        Socket? socket;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            socket = _listener;
            cts = _listenCts;
            _listener = null;
            _listenCts = null;
        }
        cts?.Cancel();
        socket?.Dispose();
        cts?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopListening();
    }

    /// <summary>Feeds one received packet through the agent table and raises events.</summary>
    internal void ProcessPacket(ReadOnlySpan<byte> packet, IPAddress source, DateTimeOffset now, ISet<string>? answered = null)
    {
        if (!DnsMessage.TryParse(packet, out DnsMessage? message) || message is null)
        {
            Log($"mDNS: ignored malformed packet from {source}");
            return;
        }
        if (!message.IsResponse)
            return;

        List<AgentChange> changes;
        lock (_gate)
        {
            changes = _cache.Apply(message, source, now);
            if (answered is not null)
            {
                foreach (DnsRecord record in message.Records)
                {
                    if (record.Ttl == 0)
                        continue;
                    string? instance = record.Type == DnsRecordType.Ptr ? record.PtrTarget : record.Name;
                    if (instance is not null && instance.EndsWith("." + ServiceType, StringComparison.OrdinalIgnoreCase))
                        answered.Add(instance[..^(ServiceType.Length + 1)]);
                }
            }
        }

        foreach (AgentChange change in changes)
        {
            EventHandler<DiscoveredAgentEventArgs>? handler = change.Kind switch
            {
                AgentChangeKind.Found => AgentFound,
                AgentChangeKind.Updated => AgentUpdated,
                _ => AgentGone,
            };
            try
            {
                handler?.Invoke(this, new DiscoveredAgentEventArgs(change.Agent));
            }
            catch (Exception ex)
            {
                Log($"mDNS: {change.Kind} handler threw: {ex}");
            }
        }
    }

    private Socket? TrySendQuery(IPAddress local, byte[] query)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(local, 0));
            if (!local.Equals(IPAddress.Any))
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            socket.SendTo(query, new IPEndPoint(MdnsGroup, MdnsPort));
            return socket;
        }
        catch (SocketException ex)
        {
            Log($"mDNS browse: could not query from {local}: {ex.Message}");
            socket.Dispose();
            return null;
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, ISet<string>? answered, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[ReceiveBufferSize];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await socket.ReceiveFromAsync(buffer, SocketFlags.None, any, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize)
            {
                // ICMP port unreachable from an earlier send, or an oversized datagram.
                continue;
            }
            catch (SocketException ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                    Log($"mDNS receive failed: {ex.Message}");
                return;
            }

            var source = ((IPEndPoint)result.RemoteEndPoint).Address;
            ProcessPacket(buffer.AsSpan(0, result.ReceivedBytes), source, DateTimeOffset.UtcNow, answered);
        }
    }

    private static List<IPAddress> LocalIPv4Addresses()
    {
        var addresses = new List<IPAddress>();
        NetworkInterface[] interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces();
        }
        catch (NetworkInformationException)
        {
            return addresses;
        }

        foreach (NetworkInterface nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || !nic.SupportsMulticast
                || !nic.Supports(NetworkInterfaceComponent.IPv4))
                continue;
            foreach (UnicastIPAddressInformation unicast in nic.GetIPProperties().UnicastAddresses)
            {
                IPAddress address = unicast.Address;
                if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && !addresses.Contains(address))
                    addresses.Add(address);
            }
        }
        return addresses;
    }

    private void Log(string message) => _log?.Invoke(message);
}
