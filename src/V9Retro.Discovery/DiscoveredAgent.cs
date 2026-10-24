using System.Net;

namespace V9Retro.Discovery;

/// <summary>A v9x remote agent advertised over mDNS / DNS-SD.</summary>
public sealed record DiscoveredAgent
{
    /// <summary>TXT <c>id</c>: the install ID, a lowercase v4 UUID. Null until TXT arrives.</summary>
    public string? InstallId { get; init; }

    /// <summary>Instance label, e.g. <c>WIN98-1a2b3c4d</c> (without the service suffix).</summary>
    public required string InstanceName { get; init; }

    /// <summary>TXT <c>name</c> (the computer name), or the instance label when absent.</summary>
    public required string Name { get; init; }

    /// <summary>The A record for the SRV target, or the address the reply came from.</summary>
    public required IPAddress Host { get; init; }

    /// <summary>SRV port of the agent's TCP protocol.</summary>
    public int Port { get; init; }

    /// <summary>TXT <c>ver</c>, the agent version.</summary>
    public string? Version { get; init; }

    /// <summary>TXT <c>build</c>, the build ID.</summary>
    public string? Build { get; init; }

    /// <summary>TXT <c>dos=1</c>.</summary>
    public bool IsDos { get; init; }

    /// <summary>TXT <c>os</c>, Windows major.minor.</summary>
    public string? OsVersion { get; init; }

    /// <summary>When a packet about this instance last arrived.</summary>
    public DateTimeOffset LastSeen { get; init; }
}

public sealed class DiscoveredAgentEventArgs(DiscoveredAgent agent) : EventArgs
{
    public DiscoveredAgent Agent { get; } = agent;
}
