using V9Retro.Core.Model;

namespace V9Retro.Core.Services;

/// <summary>Pushes store changes to listeners (view models), so nothing polls the database.
/// Events are raised on the thread that made the change; UI subscribers marshal themselves.</summary>
public sealed class ChangeFeed
{
    public event Action<Agent>? AgentChanged;
    public event Action<Agent>? AgentAdded;
    public event Action<long>? AgentRemoved;
    public event Action<AgentEvent>? EventLogged;
    public event Action<long, long>? ScreenshotAdded; // agent id, screenshot id
    public event Action<long>? InventoryChanged;

    internal void Changed(Agent a) => AgentChanged?.Invoke(a);
    internal void Added(Agent a) => AgentAdded?.Invoke(a);
    internal void Removed(long id) => AgentRemoved?.Invoke(id);
    internal void Logged(AgentEvent e) => EventLogged?.Invoke(e);
    internal void Screenshot(long agentId, long shotId) => ScreenshotAdded?.Invoke(agentId, shotId);
    internal void Inventory(long agentId) => InventoryChanged?.Invoke(agentId);
}
