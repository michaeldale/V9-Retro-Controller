using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using V9Retro.Core.Data;
using V9Retro.Core.Formatting;
using V9Retro.Core.Imaging;
using V9Retro.Core.Model;
using V9Retro.Protocol;
using V9Retro.Protocol.Imaging;

namespace V9Retro.Core.Services;

public sealed record PollResult(Agent Agent, bool Ok, string? Error, bool NeedsInventory, bool Skipped);

public sealed record ConnectionTest(AgentInfo Info, HelloInfo Hello, long RoundTripMs);

public enum PowerAction { Reboot, Shutdown, Reset }

/// <summary>Everything that talks to an agent and records what it learns: polls, inventory,
/// screenshots and power, plus adding, editing and removing agents. Shared by the background
/// sweep and the UI so both apply the same rules. Each agent's state changes run under a
/// per-agent lock, so a sweep and a click never interleave a read-modify-write.</summary>
public sealed class AgentService : IAgentService
{
    public const long LiveThrottleMs = 3000;

    readonly AgentStore _agents;
    readonly EventStore _events;
    readonly InventoryStore _inventory;
    readonly ScreenshotStore _screenshots;
    readonly AppSettings _settings;
    readonly AgentConnector _connector;
    readonly ChangeFeed _feed;
    readonly ISecretProtector _secrets;
    readonly string _screenshotDir;
    readonly ConcurrentDictionary<long, SemaphoreSlim> _locks = new();

    public AgentService(AgentStore agents, EventStore events, InventoryStore inventory, ScreenshotStore screenshots,
        AppSettings settings, AgentConnector connector, ChangeFeed feed, ISecretProtector secrets, string screenshotDir)
    {
        _agents = agents;
        _events = events;
        _inventory = inventory;
        _screenshots = screenshots;
        _settings = settings;
        _connector = connector;
        _feed = feed;
        _secrets = secrets;
        _screenshotDir = screenshotDir;
    }

    public IAgentConnector Connector => _connector;
    public ChangeFeed Feed => _feed;

    /// <summary>Who an action is logged as: the remote client's token name while the API serves a
    /// request, otherwise the Windows user.</summary>
    public static readonly AsyncLocal<string?> Actor = new();
    static string ActorName => Actor.Value ?? Environment.UserName;

    SemaphoreSlim LockFor(long id) => _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

    async Task<T> Locked<T>(long id, Func<Task<T>> body, CancellationToken ct)
    {
        var gate = LockFor(id);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await body().ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    Agent Require(long id) => _agents.Get(id) ?? throw new InvalidOperationException($"agent {id} no longer exists");

    void Log(long agentId, string type, string message)
    {
        if (agentId <= 0) return;
        _feed.Logged(_events.Log(agentId, type, message));
    }

    void Save(Agent a)
    {
        if (!a.IsSaved) return;
        _agents.UpdateState(a);
        _feed.Changed(a);
    }

    // ---- reads -------------------------------------------------------------------------

    public IReadOnlyList<Agent> All() => _agents.All();
    public Agent? Get(long id) => _agents.Get(id);
    public IReadOnlyList<AgentEvent> RecentEvents(int limit = 20) => _events.Recent(limit);
    public IReadOnlyList<AgentEvent> Events(long agentId, int limit = 15) => _events.ForAgent(agentId, limit);
    public IReadOnlyList<InventoryRecord> Inventory(long agentId) => _inventory.Get(agentId);
    public IReadOnlyList<ScreenshotRecord> Screenshots(long agentId, int limit = 10) => _screenshots.ForAgent(agentId, limit);
    public byte[]? Thumbnail(long screenshotId) => _screenshots.Thumbnail(screenshotId);

    public byte[]? ScreenshotPng(long screenshotId)
    {
        var shot = _screenshots.Get(screenshotId);
        return shot is not null && ScreenshotPath(shot) is { } path ? File.ReadAllBytes(path) : null;
    }

    public string? ScreenshotPath(ScreenshotRecord shot)
    {
        var path = Path.Combine(_screenshotDir, shot.AgentId.ToString(), shot.FileName);
        return File.Exists(path) ? path : null;
    }

    public string? VncPassword(Agent a) => _secrets.Unprotect(a.VncPassword);

    // ---- polling -----------------------------------------------------------------------

    /// <summary>Polls INFO once and applies the status rules. With <paramref name="throttle"/>,
    /// a poll within 3 s of the last attempt is skipped (the agent page polls every few seconds).</summary>
    public Task<PollResult> PollAsync(long agentId, bool throttle = false, CancellationToken ct = default) =>
        Locked(agentId, async () =>
        {
            var before = Require(agentId);
            var now = Clock.Now();
            if (throttle && before.LastPollAttempt is { } last && now - last < LiveThrottleMs)
                return new PollResult(before, before.IsOnline, before.LastError, false, true);
            var result = await PollCoreAsync(before, ct).ConfigureAwait(false);
            return result;
        }, ct);

    async Task<PollResult> PollCoreAsync(Agent before, CancellationToken ct)
    {
        AgentInfo? info = null;
        string? error = null;
        try
        {
            info = await _connector.UseAsync(before.Host, before.Port, s => s.InfoAsync(ct), ct).ConfigureAwait(false);
        }
        catch (V9xException ex)
        {
            error = ex.Message;
        }
        var now = Clock.Now();
        StatusRules.Outcome outcome;
        if (info is not null)
        {
            long? owner = info.InstallId is { } id && before.InstallId != id ? _agents.FindByInstallId(id)?.Id : null;
            outcome = StatusRules.Success(before, info, now, owner);
        }
        else
        {
            outcome = StatusRules.Failure(before, error!, now, _settings.Get(AppSettings.OfflineAfterFailures));
        }
        foreach (var (type, message) in outcome.Events) Log(before.Id, type, message);
        Save(outcome.Agent);
        return new PollResult(outcome.Agent, outcome.Ok, outcome.Error,
            outcome.Ok && StatusRules.NeedsInventory(outcome.Agent, _settings.Get(AppSettings.InventoryHours), now), false);
    }

    // ---- inventory ---------------------------------------------------------------------

    public Task<Agent> RefreshInventoryAsync(long agentId, CancellationToken ct = default) =>
        Locked(agentId, () => RefreshInventoryCoreAsync(Require(agentId), ct), ct);

    async Task<Agent> RefreshInventoryCoreAsync(Agent before, CancellationToken ct)
    {
        if (!before.Has(Capabilities.Inventory))
            throw new V9xUnsupportedException("this agent does not report inventory (needs agent 0.7.0+)");
        var records = await _connector.UseAsync(before.Host, before.Port, s => s.InventoryAsync(ct), ct).ConfigureAwait(false);
        _inventory.Replace(before.Id, records);

        var a = before.Clone();
        var drives = records.Where(r => r.Type == "drive").ToList();
        double diskTotal = 0, diskFree = 0;
        foreach (var d in drives.Where(d => d.Get("type") == "fixed" && d.Get("total") is not null))
        {
            diskTotal += ParseDouble(d.Get("total"));
            diskFree += ParseDouble(d.Get("free"));
        }
        var os = records.FirstOrDefault(r => r.Type == "os");
        var cpu = records.FirstOrDefault(r => r.Type == "cpu");
        var memory = records.FirstOrDefault(r => r.Type == "memory");

        var hash = InventoryHash(records);
        if (!string.IsNullOrEmpty(before.InventoryHash) && before.InventoryHash != hash)
            Log(before.Id, AgentEventType.InventoryChanged, "Hardware inventory changed");

        a.InventoryUpdatedAt = Clock.Now();
        a.InventoryBootCounter = before.BootCounter;
        a.InventoryAgentVersion = before.AgentVersion;
        a.InventoryHash = hash;
        var cpuName = V9xFormat.CpuName(cpu);
        a.CpuSummary = cpuName.Length > 0 ? cpuName : null;
        a.RamTotal = memory?.Get("total_phys") is { } ram && long.TryParse(ram, out var r) ? r : null;
        a.DiskTotal = diskTotal > 0 ? (long)diskTotal : null;
        a.DiskFree = diskTotal > 0 ? (long)diskFree : null;
        a.OsName = V9xFormat.OsName(before.WindowsVersion, os);
        if (records.FirstOrDefault(r => r.Type == "meta")?.Get("install_id") is { Length: > 0 } id && a.InstallId is null
            && _agents.FindByInstallId(id) is null)
            a.InstallId = id.ToLowerInvariant();
        Save(a);
        _feed.Inventory(a.Id);
        return a;
    }

    static double ParseDouble(string? s) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>Fingerprint of the hardware that matters, to notice upgrades and swaps (same
    /// inputs as the plugin's: CPU identity, RAM in 4 MB steps, disks to 100 MB, device list).</summary>
    internal static string InventoryHash(IReadOnlyList<InventoryRecord> records)
    {
        var cpu = records.FirstOrDefault(r => r.Type == "cpu");
        var memory = records.FirstOrDefault(r => r.Type == "memory");
        var fingerprint = new
        {
            cpu = new[] { "vendor", "brand", "family", "model", "stepping", "count" }.Select(k => cpu?.Get(k) ?? "").ToArray(),
            ram = (long)Math.Round(ParseDouble(memory?.Get("total_phys")) / 1048576 / 4),
            disks = records.Where(r => r.Type == "drive")
                .Select(d => new[] { d.Get("letter") ?? "", d.Get("type") ?? "", d.Get("total") is { } t ? Math.Round(ParseDouble(t) / 1e8).ToString(System.Globalization.CultureInfo.InvariantCulture) : "" })
                .ToArray(),
            devices = records.Where(r => r.Type == "device").Select(d => $"{d.Get("class")}|{d.Get("desc")}")
                .Order(StringComparer.Ordinal).ToArray(),
        };
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(fingerprint)))).ToLowerInvariant();
    }

    // ---- screenshots -------------------------------------------------------------------

    /// <summary>Captures, stores a PNG plus a thumbnail, and prunes to the configured count.
    /// Throws <see cref="V9xGuestException"/> with BUSY while an exec or another capture runs.</summary>
    public async Task<long> TakeScreenshotAsync(long agentId, string source, CancellationToken ct = default)
    {
        var agent = Require(agentId);
        var (info, bmp) = await _connector.UseAsync(agent.Host, agent.Port,
            s => s.ScreenshotAsync(new ScreenshotOptions(), ct: ct), ct, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        var image = BmpDecoder.Decode(bmp);
        var png = PngEncoder.Encode(image);
        var thumb = PngEncoder.Encode(PngEncoder.Thumbnail(image), System.IO.Compression.CompressionLevel.Fastest);

        var dir = Path.Combine(_screenshotDir, agentId.ToString());
        Directory.CreateDirectory(dir);
        var fileName = $"{Clock.Now()}-{Guid.NewGuid().ToString("N")[..8]}.png";
        await File.WriteAllBytesAsync(Path.Combine(dir, fileName), png, ct).ConfigureAwait(false);
        var id = _screenshots.Add(agentId, image.Width, image.Height, (int)info.SourceBitsPerPixel, source, fileName, thumb);
        foreach (var old in _screenshots.Prune(agentId, _settings.Get(AppSettings.ScreenshotKeep)))
            TryDelete(Path.Combine(dir, old));

        await Locked(agentId, () =>
        {
            var a = Require(agentId).Clone();
            a.LastScreenshotId = id;
            a.ScreenshotAt = Clock.Now();
            a.ScreenWidth = (int)info.Width;
            a.ScreenHeight = (int)info.Height;
            Save(a);
            return Task.FromResult(true);
        }, ct).ConfigureAwait(false);
        _feed.Screenshot(agentId, id);
        return id;
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // ---- power -------------------------------------------------------------------------

    /// <summary>Sends reboot, shutdown or reset with a fresh token. The token comes back as the
    /// pending job after the restart, which is what later proves the reboot happened.</summary>
    public Task<Agent> PowerAsync(long agentId, PowerAction action, CancellationToken ct = default) =>
        Locked(agentId, async () =>
        {
            var before = Require(agentId);
            var token = V9xSession.NewPowerToken();
            await _connector.UseAsync(before.Host, before.Port, s => action switch
            {
                PowerAction.Reboot => s.RebootAsync(token, ct),
                PowerAction.Shutdown => s.ShutdownAsync(token, ct),
                _ => s.ResetAsync(token, ct),
            }, ct).ConfigureAwait(false);
            var a = before.Clone();
            a.Status = action == PowerAction.Shutdown ? AgentStatus.Shutdown : AgentStatus.Rebooting;
            a.PowerToken = token;
            a.PowerAction = action.ToString().ToLowerInvariant();
            a.PowerRequestedAt = Clock.Now();
            Save(a);
            Log(agentId, action switch
            {
                PowerAction.Reboot => AgentEventType.RebootRequested,
                PowerAction.Shutdown => AgentEventType.ShutdownRequested,
                _ => AgentEventType.ResetRequested,
            }, $"{action} requested by {ActorName}");
            return a;
        }, ct);

    // ---- adding and editing ------------------------------------------------------------

    /// <summary>The saved agent that is this machine: by install ID first, then by address.</summary>
    public Agent? FindExisting(string? installId, string host, int port) =>
        (installId is { Length: > 0 } ? _agents.FindByInstallId(installId) : null) ?? _agents.FindByAddress(host, port);

    public Agent Add(string name, string host, int port, string? notes = null, string? installId = null, bool enabled = true)
    {
        var agent = new Agent
        {
            Name = name.Trim(),
            Host = host.Trim(),
            Port = port,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            InstallId = installId?.ToLowerInvariant(),
            Enabled = enabled,
        };
        _agents.Insert(agent);
        Log(agent.Id, AgentEventType.Added, $"Added {agent.Address} by {ActorName}");
        _feed.Added(agent);
        return agent;
    }

    /// <summary>Saves user-editable fields. Changing the address resets the status, boot counter
    /// and inventory hash (another machine may answer there), as the plugin does.</summary>
    public Agent Update(long id, string name, string host, int port, bool enabled, string? notes, int vncPort, string? vncPassword,
        bool changePassword)
    {
        var before = Require(id);
        var a = before.Clone();
        a.Name = name.Trim();
        a.Host = host.Trim();
        a.Port = port;
        a.Enabled = enabled;
        a.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        a.VncPort = vncPort;
        if (changePassword) a.VncPassword = string.IsNullOrEmpty(vncPassword) ? null : _secrets.Protect(vncPassword);
        _agents.UpdateSettings(a);
        if (!string.Equals(before.Host, a.Host, StringComparison.OrdinalIgnoreCase) || before.Port != a.Port)
        {
            a.Status = AgentStatus.Unknown;
            a.BootCounter = null;
            a.InventoryHash = null;
            a.ConsecutiveFailures = 0;
            a.IdentityConflict = null;
            _agents.UpdateState(a);
            Log(id, AgentEventType.AddressChanged, $"Address changed {before.Address} → {a.Address}");
        }
        _feed.Changed(a);
        return a;
    }

    /// <summary>Accepts that the machine now answering at this address is the one to keep: takes its
    /// install ID and clears the conflict. Fails if another saved agent already owns that ID.</summary>
    public Agent ResolveIdentityConflict(long id)
    {
        var a = Require(id).Clone();
        if (a.IdentityConflict is not { } reported) return a;
        if (_agents.FindByInstallId(reported) is { } other && other.Id != id)
            throw new InvalidOperationException($"install ID {reported} already belongs to \"{other.Name}\"");
        a.InstallId = reported;
        a.IdentityConflict = null;
        a.Status = AgentStatus.Unknown;
        a.BootCounter = null;
        a.InventoryHash = null;
        _agents.UpdateState(a);
        Log(id, AgentEventType.IdentityConflict, $"Now tracking install {reported}");
        _feed.Changed(a);
        return a;
    }

    public void Delete(long id)
    {
        foreach (var shot in _screenshots.ForAgent(id, int.MaxValue))
            TryDelete(Path.Combine(_screenshotDir, id.ToString(), shot.FileName));
        _agents.Delete(id);
        _locks.TryRemove(id, out _);
        _feed.Removed(id);
    }

    /// <summary>Discovery saw an install at an address. When it is a saved agent whose address
    /// moved (DHCP), the stored address follows it. Returns the matching agent, if any.</summary>
    public Agent? ObserveDiscovered(string installId, string host, int port)
    {
        var a = _agents.FindByInstallId(installId);
        if (a is null) return null;
        if (string.Equals(a.Host, host, StringComparison.OrdinalIgnoreCase) && a.Port == port) return a;
        var moved = a.Clone();
        moved.Host = host;
        moved.Port = port;
        _agents.UpdateSettings(moved);
        Log(a.Id, AgentEventType.AddressChanged, $"Discovery found {a.Name} at {moved.Address} (was {a.Address})");
        _feed.Changed(moved);
        return moved;
    }

    /// <summary>Logs a user action (exec, file transfer, kill) to the agent's activity.</summary>
    public void LogAction(long agentId, string message) => Log(agentId, AgentEventType.Action, message);

    // ---- IAgentService (async surface shared with the remote client) --------------------

    public Task<IReadOnlyList<Agent>> AllAsync(CancellationToken ct = default) => Task.FromResult(All());
    public Task<Agent?> GetAsync(long id, CancellationToken ct = default) => Task.FromResult(Get(id));
    public Task<Agent?> FindExistingAsync(string? installId, string host, int port, CancellationToken ct = default) =>
        Task.FromResult(FindExisting(installId, host, port));
    public Task<IReadOnlyList<AgentEvent>> RecentEventsAsync(int limit = 20, CancellationToken ct = default) => Task.FromResult(RecentEvents(limit));
    public Task<IReadOnlyList<AgentEvent>> EventsAsync(long agentId, int limit = 15, CancellationToken ct = default) =>
        Task.FromResult(Events(agentId, limit));
    public Task<IReadOnlyList<InventoryRecord>> InventoryAsync(long agentId, CancellationToken ct = default) => Task.FromResult(Inventory(agentId));
    public Task<IReadOnlyList<ScreenshotRecord>> ScreenshotsAsync(long agentId, int limit = 10, CancellationToken ct = default) =>
        Task.FromResult(Screenshots(agentId, limit));
    public Task<byte[]?> ThumbnailAsync(long screenshotId, CancellationToken ct = default) => Task.FromResult(Thumbnail(screenshotId));
    public Task<byte[]?> ScreenshotPngAsync(long screenshotId, CancellationToken ct = default) => Task.FromResult(ScreenshotPng(screenshotId));
    public Task<string?> VncPasswordAsync(long agentId, CancellationToken ct = default) =>
        Task.FromResult(Get(agentId) is { } a ? VncPassword(a) : null);
    public Task<Agent> AddAsync(string name, string host, int port, string? notes = null, string? installId = null, bool enabled = true,
        CancellationToken ct = default) => Task.FromResult(Add(name, host, port, notes, installId, enabled));
    public Task<Agent> UpdateAsync(long id, string name, string host, int port, bool enabled, string? notes, int vncPort, string? vncPassword,
        bool changePassword, CancellationToken ct = default) =>
        Task.FromResult(Update(id, name, host, port, enabled, notes, vncPort, vncPassword, changePassword));
    public Task<Agent> ResolveIdentityConflictAsync(long id, CancellationToken ct = default) => Task.FromResult(ResolveIdentityConflict(id));
    public Task DeleteAsync(long id, CancellationToken ct = default)
    {
        Delete(id);
        return Task.CompletedTask;
    }
    public Task LogActionAsync(long agentId, string message, CancellationToken ct = default)
    {
        LogAction(agentId, message);
        return Task.CompletedTask;
    }
}
