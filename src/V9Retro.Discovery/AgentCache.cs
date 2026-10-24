using System.Net;

namespace V9Retro.Discovery;

internal enum AgentChangeKind
{
    Found,
    Updated,
    Gone,
}

internal readonly record struct AgentChange(AgentChangeKind Kind, DiscoveredAgent Agent);

/// <summary>
/// Folds mDNS records that may arrive across several packets into one
/// <see cref="DiscoveredAgent"/> per service instance, keyed by instance name.
/// An instance is published once its SRV and an address are known; a TTL 0
/// PTR, SRV or TXT for it is a goodbye. Not thread-safe; callers lock.
/// </summary>
internal sealed class AgentCache
{
    private sealed class Instance(string label)
    {
        public string Label { get; } = label;
        public DnsSrv? Srv { get; set; }
        public Dictionary<string, string>? Txt { get; set; }
        public IPAddress? Source { get; set; }
        public DiscoveredAgent? Published { get; set; }
    }

    private static readonly string ServiceSuffix = "." + MdnsBrowser.ServiceType;

    private readonly Dictionary<string, Instance> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPAddress> _hosts = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DiscoveredAgent> Agents =>
        _instances.Values.Where(i => i.Published is not null).Select(i => i.Published!).ToList();

    public List<AgentChange> Apply(DnsMessage message, IPAddress source, DateTimeOffset now)
    {
        var changes = new List<AgentChange>();
        if (!message.IsResponse)
            return changes;

        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var goodbyes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (DnsRecord record in message.Records)
        {
            switch (record.Type)
            {
                case DnsRecordType.Ptr when record.PtrTarget is { } target
                                            && string.Equals(record.Name, MdnsBrowser.ServiceType, StringComparison.OrdinalIgnoreCase)
                                            && IsInstanceName(target):
                    if (record.Ttl == 0)
                        goodbyes.Add(target);
                    else
                    {
                        GetOrAdd(target, source);
                        touched.Add(target);
                    }
                    break;

                case DnsRecordType.Srv when record.Srv is { } srv && IsInstanceName(record.Name):
                    if (record.Ttl == 0)
                        goodbyes.Add(record.Name);
                    else
                    {
                        GetOrAdd(record.Name, source).Srv = srv;
                        touched.Add(record.Name);
                    }
                    break;

                case DnsRecordType.Txt when record.Txt is { } strings && IsInstanceName(record.Name):
                    if (record.Ttl == 0)
                        goodbyes.Add(record.Name);
                    else
                    {
                        GetOrAdd(record.Name, source).Txt = ParseTxt(strings);
                        touched.Add(record.Name);
                    }
                    break;

                case DnsRecordType.A when record.Address is { } address:
                    if (record.Ttl == 0)
                        _hosts.Remove(record.Name);
                    else
                        _hosts[record.Name] = address;
                    changedHosts.Add(record.Name);
                    break;
            }
        }

        foreach (string key in goodbyes)
        {
            touched.Remove(key);
            if (_instances.Remove(key, out Instance? gone) && gone.Published is not null)
                changes.Add(new AgentChange(AgentChangeKind.Gone, gone.Published with { LastSeen = now }));
        }

        if (changedHosts.Count > 0)
        {
            foreach ((string key, Instance instance) in _instances)
            {
                if (instance.Srv is { } srv && changedHosts.Contains(srv.Target))
                    touched.Add(key);
            }
        }

        foreach (string key in touched)
        {
            if (!_instances.TryGetValue(key, out Instance? instance))
                continue;
            instance.Source = source;
            DiscoveredAgent? agent = Build(instance, now);
            if (agent is null)
                continue;

            DiscoveredAgent? previous = instance.Published;
            instance.Published = agent;
            if (previous is null)
                changes.Add(new AgentChange(AgentChangeKind.Found, agent));
            else if (previous with { LastSeen = now } != agent)
                changes.Add(new AgentChange(AgentChangeKind.Updated, agent));
        }

        return changes;
    }

    public void Clear()
    {
        _instances.Clear();
        _hosts.Clear();
    }

    private static bool IsInstanceName(string name) =>
        name.Length > ServiceSuffix.Length && name.EndsWith(ServiceSuffix, StringComparison.OrdinalIgnoreCase);

    private Instance GetOrAdd(string fullName, IPAddress source)
    {
        if (!_instances.TryGetValue(fullName, out Instance? instance))
        {
            instance = new Instance(fullName[..^ServiceSuffix.Length]) { Source = source };
            _instances[fullName] = instance;
        }
        return instance;
    }

    private DiscoveredAgent? Build(Instance instance, DateTimeOffset now)
    {
        if (instance.Srv is not { } srv)
            return null;
        IPAddress? host = _hosts.TryGetValue(srv.Target, out IPAddress? address) ? address : instance.Source;
        if (host is null)
            return null;

        Dictionary<string, string> txt = instance.Txt ?? [];
        return new DiscoveredAgent
        {
            InstallId = Get(txt, "id")?.ToLowerInvariant(),
            InstanceName = instance.Label,
            Name = Get(txt, "name") ?? instance.Label,
            Host = host,
            Port = srv.Port,
            Version = Get(txt, "ver"),
            Build = Get(txt, "build"),
            IsDos = Get(txt, "dos") == "1",
            OsVersion = Get(txt, "os"),
            LastSeen = now,
        };
    }

    private static string? Get(Dictionary<string, string> txt, string key) =>
        txt.TryGetValue(key, out string? value) && value.Length > 0 ? value : null;

    // RFC 6763 6.4: keys are case-insensitive and the first occurrence wins.
    private static Dictionary<string, string> ParseTxt(IReadOnlyList<string> strings)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in strings)
        {
            int equals = entry.IndexOf('=');
            string key = equals < 0 ? entry : entry[..equals];
            if (key.Length == 0)
                continue;
            fields.TryAdd(key, equals < 0 ? "" : entry[(equals + 1)..]);
        }
        return fields;
    }
}
