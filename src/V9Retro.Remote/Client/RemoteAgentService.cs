using V9Retro.Core.Model;
using V9Retro.Core.Services;
using V9Retro.Protocol;

namespace V9Retro.Remote.Client;

/// <summary><see cref="IAgentService"/> over the host's REST API. Agent connections the UI opens
/// itself (console, files, VNC) use <see cref="Connector"/>, which tunnels through the host.</summary>
sealed class RemoteAgentService(ApiClient api, ChangeFeed feed, IAgentConnector connector) : IAgentService
{
    public ChangeFeed Feed => feed;
    public IAgentConnector Connector => connector;

    public async Task<IReadOnlyList<Agent>> AllAsync(CancellationToken ct = default) =>
        await api.GetAsync<List<Agent>>("api/agents", ct).ConfigureAwait(false);

    public Task<Agent?> GetAsync(long id, CancellationToken ct = default) => api.GetOrNullAsync<Agent>($"api/agents/{id}", ct);

    public Task<Agent?> FindExistingAsync(string? installId, string host, int port, CancellationToken ct = default) =>
        api.GetOrNullAsync<Agent>($"api/agents/find?installId={Uri.EscapeDataString(installId ?? "")}&host={Uri.EscapeDataString(host)}&port={port}", ct);

    public async Task<IReadOnlyList<AgentEvent>> RecentEventsAsync(int limit = 20, CancellationToken ct = default) =>
        await api.GetAsync<List<AgentEvent>>($"api/events?limit={limit}", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<AgentEvent>> EventsAsync(long agentId, int limit = 15, CancellationToken ct = default) =>
        await api.GetAsync<List<AgentEvent>>($"api/agents/{agentId}/events?limit={limit}", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<InventoryRecord>> InventoryAsync(long agentId, CancellationToken ct = default) =>
        await api.GetAsync<List<InventoryRecord>>($"api/agents/{agentId}/inventory", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<ScreenshotRecord>> ScreenshotsAsync(long agentId, int limit = 10, CancellationToken ct = default) =>
        await api.GetAsync<List<ScreenshotRecord>>($"api/agents/{agentId}/screenshots?limit={limit}", ct).ConfigureAwait(false);

    public Task<byte[]?> ThumbnailAsync(long screenshotId, CancellationToken ct = default) =>
        api.GetBytesAsync($"api/screenshots/{screenshotId}/thumb", ct);

    public Task<byte[]?> ScreenshotPngAsync(long screenshotId, CancellationToken ct = default) =>
        api.GetBytesAsync($"api/screenshots/{screenshotId}/png", ct);

    public async Task<string?> VncPasswordAsync(long agentId, CancellationToken ct = default) =>
        (await api.GetAsync<TextValue>($"api/agents/{agentId}/vnc-password", ct).ConfigureAwait(false)).Value;

    public Task<PollResult> PollAsync(long agentId, bool throttle = false, CancellationToken ct = default) =>
        api.SendAsync<PollResult>(HttpMethod.Post, $"api/agents/{agentId}/poll?throttle={(throttle ? "true" : "false")}", ct: ct);

    public Task<Agent> RefreshInventoryAsync(long agentId, CancellationToken ct = default) =>
        api.SendAsync<Agent>(HttpMethod.Post, $"api/agents/{agentId}/inventory", ct: ct);

    sealed record ShotId(long Id);

    public async Task<long> TakeScreenshotAsync(long agentId, string source, CancellationToken ct = default) =>
        (await api.SendAsync<ShotId>(HttpMethod.Post, $"api/agents/{agentId}/screenshot?source={Uri.EscapeDataString(source)}", ct: ct)
            .ConfigureAwait(false)).Id;

    public Task<Agent> PowerAsync(long agentId, PowerAction action, CancellationToken ct = default) =>
        api.SendAsync<Agent>(HttpMethod.Post, $"api/agents/{agentId}/power/{action}", ct: ct);

    public Task<Agent> AddAsync(string name, string host, int port, string? notes = null, string? installId = null, bool enabled = true,
        CancellationToken ct = default) =>
        api.SendAsync<Agent>(HttpMethod.Post, "api/agents", new AddAgentRequest(name, host, port, notes, installId, enabled), ct);

    public Task<Agent> UpdateAsync(long id, string name, string host, int port, bool enabled, string? notes, int vncPort, string? vncPassword,
        bool changePassword, CancellationToken ct = default) =>
        api.SendAsync<Agent>(HttpMethod.Put, $"api/agents/{id}",
            new UpdateAgentRequest(name, host, port, enabled, notes, vncPort, vncPassword, changePassword), ct);

    public Task<Agent> ResolveIdentityConflictAsync(long id, CancellationToken ct = default) =>
        api.SendAsync<Agent>(HttpMethod.Post, $"api/agents/{id}/resolve-conflict", ct: ct);

    public Task DeleteAsync(long id, CancellationToken ct = default) => api.SendAsync(HttpMethod.Delete, $"api/agents/{id}", ct: ct);

    public Task LogActionAsync(long agentId, string message, CancellationToken ct = default) =>
        api.SendAsync(HttpMethod.Post, $"api/agents/{agentId}/log", new TextValue(message), ct);
}
