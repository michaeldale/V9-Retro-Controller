using Xunit.Abstractions;

namespace V9Retro.Discovery.Tests;

/// <summary>Skipped unless V9RETRO_LIVE_MDNS=1, because it sends real mDNS traffic.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("V9RETRO_LIVE_MDNS") != "1")
            Skip = "live LAN browse; set V9RETRO_LIVE_MDNS=1 to run";
    }
}

public class LiveBrowseTests(ITestOutputHelper output)
{
    [LiveFact]
    public async Task BrowsesTheLan()
    {
        using var browser = new MdnsBrowser(output.WriteLine);
        browser.AgentFound += (_, e) => output.WriteLine($"found {e.Agent.InstanceName}");

        bool listening = browser.StartListening();
        output.WriteLine($"passive listener on 5353: {(listening ? "up" : "unavailable")}");

        IReadOnlyList<DiscoveredAgent> agents = await browser.BrowseAsync(TimeSpan.FromSeconds(3));

        output.WriteLine($"{agents.Count} agent(s) answered");
        foreach (DiscoveredAgent agent in agents)
        {
            output.WriteLine(
                $"{agent.InstanceName} {agent.Host}:{agent.Port} id={agent.InstallId} ver={agent.Version} " +
                $"build={agent.Build} os={agent.OsVersion} dos={agent.IsDos} name={agent.Name}");
        }
    }
}
