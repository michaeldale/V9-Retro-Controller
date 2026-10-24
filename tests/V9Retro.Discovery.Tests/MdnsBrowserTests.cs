using System.Net;

namespace V9Retro.Discovery.Tests;

public class MdnsBrowserTests
{
    private const string Instance = "WIN98-1a2b3c4d._v9xremote._tcp.local";
    private const string HostName = "v9x-1a2b3c4d.local";
    private const string InstallId = "1a2b3c4d-0000-4000-8000-000000000001";
    private static readonly IPAddress Source = IPAddress.Parse("192.168.1.99");
    private static readonly DateTimeOffset T0 = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private sealed class Recorder
    {
        public List<(string Kind, DiscoveredAgent Agent)> Events { get; } = [];

        public Recorder(MdnsBrowser browser)
        {
            browser.AgentFound += (_, e) => Events.Add(("found", e.Agent));
            browser.AgentUpdated += (_, e) => Events.Add(("updated", e.Agent));
            browser.AgentGone += (_, e) => Events.Add(("gone", e.Agent));
        }
    }

    private static string[] AllTxt(string version = "0.13.0") =>
        [$"id={InstallId}", $"ver={version}", "build=20261020-abcdef0", "dos=1", "os=4.10", "name=WIN98"];

    private static byte[] FullAnnouncement(uint ttl = 4500, string version = "0.13.0") =>
        new PacketBuilder(compress: false)
            .Ptr(MdnsBrowser.ServiceType, Instance, ttl)
            .Ptr(MdnsBrowser.ServiceEnumerationType, MdnsBrowser.ServiceType, ttl)
            .Srv(Instance, 9869, HostName, Math.Min(ttl, 120))
            .Txt(Instance, ttl, AllTxt(version))
            .A(HostName, "192.168.1.50", Math.Min(ttl, 120))
            .Build();

    [Fact]
    public void ParsesAllTxtKeysIntoAgent()
    {
        using var browser = new MdnsBrowser();
        var recorder = new Recorder(browser);

        browser.ProcessPacket(FullAnnouncement(), Source, T0);

        var (kind, agent) = Assert.Single(recorder.Events);
        Assert.Equal("found", kind);
        Assert.Equal(InstallId, agent.InstallId);
        Assert.Equal("WIN98-1a2b3c4d", agent.InstanceName);
        Assert.Equal("WIN98", agent.Name);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), agent.Host);
        Assert.Equal(9869, agent.Port);
        Assert.Equal("0.13.0", agent.Version);
        Assert.Equal("20261020-abcdef0", agent.Build);
        Assert.True(agent.IsDos);
        Assert.Equal("4.10", agent.OsVersion);
        Assert.Equal(T0, agent.LastSeen);
        Assert.Equal(agent, Assert.Single(browser.Agents));
    }

    [Fact]
    public void CompressedReplyProducesSameAgent()
    {
        using var browser = new MdnsBrowser();
        var recorder = new Recorder(browser);
        byte[] packet = new PacketBuilder(compress: true, id: 99)
            .Ptr(MdnsBrowser.ServiceType, Instance, 10)
            .Srv(Instance, 9869, HostName, 10)
            .Txt(Instance, 10, AllTxt())
            .A(HostName, "192.168.1.50", 10)
            .Build();

        browser.ProcessPacket(packet, Source, T0);

        var (_, agent) = Assert.Single(recorder.Events);
        Assert.Equal("WIN98-1a2b3c4d", agent.InstanceName);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), agent.Host);
        Assert.Equal(InstallId, agent.InstallId);
    }

    [Fact]
    public void MergesRecordsAcrossPackets()
    {
        using var browser = new MdnsBrowser();
        var recorder = new Recorder(browser);

        // PTR alone: no port yet, nothing to publish.
        browser.ProcessPacket(new PacketBuilder(false).Ptr(MdnsBrowser.ServiceType, Instance).Build(), Source, T0);
        Assert.Empty(recorder.Events);

        // SRV arrives: published with the reply's source address until the A record shows up.
        browser.ProcessPacket(new PacketBuilder(false).Srv(Instance, 9869, HostName).Build(), Source, T0.AddSeconds(1));
        var (kind, agent) = Assert.Single(recorder.Events);
        Assert.Equal("found", kind);
        Assert.Equal(Source, agent.Host);
        Assert.Null(agent.InstallId);
        Assert.Equal("WIN98-1a2b3c4d", agent.Name);

        // TXT then A, each in its own packet.
        browser.ProcessPacket(new PacketBuilder(false).Txt(Instance, 4500, AllTxt()).Build(), Source, T0.AddSeconds(2));
        browser.ProcessPacket(new PacketBuilder(false).A(HostName, "192.168.1.50").Build(), Source, T0.AddSeconds(3));

        Assert.Equal(["found", "updated", "updated"], recorder.Events.Select(e => e.Kind));
        DiscoveredAgent final = recorder.Events[^1].Agent;
        Assert.Equal(InstallId, final.InstallId);
        Assert.Equal("WIN98", final.Name);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), final.Host);
        Assert.Equal(T0.AddSeconds(3), final.LastSeen);
        Assert.Single(browser.Agents);
    }

    [Fact]
    public void RepeatedAnnouncementOnlyRefreshesLastSeen()
    {
        using var browser = new MdnsBrowser();
        var recorder = new Recorder(browser);

        browser.ProcessPacket(FullAnnouncement(), Source, T0);
        browser.ProcessPacket(FullAnnouncement(), Source, T0.AddSeconds(1));

        Assert.Single(recorder.Events);
        Assert.Equal(T0.AddSeconds(1), Assert.Single(browser.Agents).LastSeen);
    }

    [Fact]
    public void ChangedTxtRaisesUpdated()
    {
        using var browser = new MdnsBrowser();
        var recorder = new Recorder(browser);

        browser.ProcessPacket(FullAnnouncement(version: "0.12.0"), Source, T0);
        browser.ProcessPacket(FullAnnouncement(version: "0.13.0"), Source, T0.AddSeconds(5));

        Assert.Equal(["found", "updated"], recorder.Events.Select(e => e.Kind));
        Assert.Equal("0.13.0", recorder.Events[1].Agent.Version);
    }

    [Fact]
    public void GoodbyeRemovesAgent()
    {
        using var browser = new MdnsBrowser();
        var recorder = new Recorder(browser);

        browser.ProcessPacket(FullAnnouncement(), Source, T0);
        browser.ProcessPacket(FullAnnouncement(ttl: 0), Source, T0.AddSeconds(9));

        Assert.Equal(["found", "gone"], recorder.Events.Select(e => e.Kind));
        Assert.Equal(InstallId, recorder.Events[1].Agent.InstallId);
        Assert.Empty(browser.Agents);

        // Coming back after the reboot is a fresh Found.
        browser.ProcessPacket(FullAnnouncement(), Source, T0.AddSeconds(60));
        Assert.Equal("found", recorder.Events[^1].Kind);
    }

    [Fact]
    public void GoodbyeForUnknownAgentIsSilent()
    {
        using var browser = new MdnsBrowser();
        var recorder = new Recorder(browser);

        browser.ProcessPacket(FullAnnouncement(ttl: 0), Source, T0);

        Assert.Empty(recorder.Events);
        Assert.Empty(browser.Agents);
    }

    [Fact]
    public void IgnoresQueriesAndOtherServices()
    {
        using var browser = new MdnsBrowser();
        var recorder = new Recorder(browser);

        browser.ProcessPacket(DnsMessage.BuildQuery(1, MdnsBrowser.ServiceType, DnsRecordType.Ptr), Source, T0);
        browser.ProcessPacket(new PacketBuilder(false)
            .Ptr("_http._tcp.local", "printer._http._tcp.local")
            .Srv("printer._http._tcp.local", 80, "printer.local")
            .Build(), Source, T0);
        browser.ProcessPacket([1, 2, 3], Source, T0);

        Assert.Empty(recorder.Events);
    }

    [Fact]
    public void KeepsInstancesApart()
    {
        using var browser = new MdnsBrowser();
        browser.ProcessPacket(FullAnnouncement(), Source, T0);
        browser.ProcessPacket(new PacketBuilder(true)
            .Ptr(MdnsBrowser.ServiceType, "WIN2K-99887766._v9xremote._tcp.local")
            .Srv("WIN2K-99887766._v9xremote._tcp.local", 9870, "v9x-99887766.local")
            .Txt("WIN2K-99887766._v9xremote._tcp.local", 4500, "id=99887766-0000-4000-8000-000000000002", "dos=0", "os=5.0", "name=WIN2K")
            .A("v9x-99887766.local", "192.168.1.51")
            .Build(), IPAddress.Parse("192.168.1.51"), T0);

        var agents = browser.Agents.OrderBy(a => a.Name).ToList();
        Assert.Equal(["WIN2K", "WIN98"], agents.Select(a => a.Name));
        Assert.False(agents[0].IsDos);
        Assert.Equal(9870, agents[0].Port);
    }
}
