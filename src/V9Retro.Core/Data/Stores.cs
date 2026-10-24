using System.Text.Json;
using Dapper;
using V9Retro.Core.Model;
using V9Retro.Protocol;

namespace V9Retro.Core.Data;

/// <summary>Agents table. User settings and poll state are written separately, so an edit in the
/// UI and a poll finishing at the same moment never overwrite each other's columns.</summary>
public sealed class AgentStore(Database db)
{
    static readonly string[] SettingsColumns =
        ["install_id", "name", "host", "port", "enabled", "notes", "vnc_port", "vnc_password", "last_modified"];

    static readonly string[] StateColumns =
    [
        "install_id", "status", "last_poll_attempt", "last_seen", "last_error", "consecutive_failures", "identity_conflict",
        "agent_version", "build_id", "computer_name", "windows_version", "os_name", "raw_version", "winsock_version",
        "capabilities", "boot_counter", "uptime_ms", "desktop_ready", "screen_width", "screen_height", "screen_bpp",
        "windows_dir", "system_dir", "listen_address", "allowed_client", "pending_job",
        "cpu_summary", "ram_total", "disk_total", "disk_free", "inventory_updated_at", "inventory_agent_version",
        "inventory_boot_counter", "inventory_hash", "last_screenshot_id", "screenshot_at",
        "power_token", "power_action", "power_requested_at",
    ];

    static string Param(string column)
    {
        var parts = column.Split('_');
        return string.Concat(parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    static readonly string UpdateSettingsSql =
        $"UPDATE agents SET {string.Join(", ", SettingsColumns.Select(c => $"{c} = @{Param(c)}"))} WHERE id = @Id";

    static readonly string UpdateStateSql =
        $"UPDATE agents SET {string.Join(", ", StateColumns.Select(c => $"{c} = @{Param(c)}"))} WHERE id = @Id";

    public IReadOnlyList<Agent> All()
    {
        using var c = db.Open();
        return c.Query<Agent>("SELECT * FROM agents ORDER BY name COLLATE NOCASE").AsList();
    }

    public Agent? Get(long id)
    {
        using var c = db.Open();
        return c.QuerySingleOrDefault<Agent>("SELECT * FROM agents WHERE id = @id", new { id });
    }

    public Agent? FindByInstallId(string installId)
    {
        using var c = db.Open();
        return c.QuerySingleOrDefault<Agent>("SELECT * FROM agents WHERE install_id = @installId",
            new { installId = installId.ToLowerInvariant() });
    }

    public Agent? FindByAddress(string host, int port)
    {
        using var c = db.Open();
        return c.QueryFirstOrDefault<Agent>("SELECT * FROM agents WHERE host = @host COLLATE NOCASE AND port = @port",
            new { host, port });
    }

    public long Insert(Agent agent)
    {
        var now = Clock.Now();
        agent.DateAdded = now;
        agent.LastModified = now;
        using var c = db.Open();
        var columns = new[] { "date_added" }.Concat(SettingsColumns).Concat(StateColumns.Except(SettingsColumns)).ToArray();
        agent.Id = c.ExecuteScalar<long>(
            $"INSERT INTO agents ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select(x => "@" + Param(x)))}); SELECT last_insert_rowid();",
            agent);
        return agent.Id;
    }

    public void UpdateSettings(Agent agent)
    {
        agent.LastModified = Clock.Now();
        using var c = db.Open();
        c.Execute(UpdateSettingsSql, agent);
    }

    public void UpdateState(Agent agent)
    {
        using var c = db.Open();
        c.Execute(UpdateStateSql, agent);
    }

    public void Delete(long id)
    {
        using var c = db.Open();
        c.Execute("DELETE FROM agents WHERE id = @id", new { id });
    }
}

public sealed class EventStore(Database db)
{
    public AgentEvent Log(long agentId, string type, string message)
    {
        var now = Clock.Now();
        using var c = db.Open();
        var id = c.ExecuteScalar<long>(
            "INSERT INTO agent_events (agent_id, date_added, type, message) VALUES (@agentId, @now, @type, @message); SELECT last_insert_rowid();",
            new { agentId, now, type, message });
        return new AgentEvent(id, agentId, now, type, message);
    }

    public IReadOnlyList<AgentEvent> Recent(int limit = 20)
    {
        using var c = db.Open();
        return c.Query<AgentEvent>(
            "SELECT id, agent_id, date_added, type, message FROM agent_events ORDER BY id DESC LIMIT @limit", new { limit }).AsList();
    }

    public IReadOnlyList<AgentEvent> ForAgent(long agentId, int limit = 15)
    {
        using var c = db.Open();
        return c.Query<AgentEvent>(
            "SELECT id, agent_id, date_added, type, message FROM agent_events WHERE agent_id = @agentId ORDER BY id DESC LIMIT @limit",
            new { agentId, limit }).AsList();
    }
}

public sealed class InventoryStore(Database db)
{
    public void Replace(long agentId, IReadOnlyList<InventoryRecord> records)
    {
        var now = Clock.Now();
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        c.Execute("DELETE FROM agent_inventory WHERE agent_id = @agentId", new { agentId }, tx);
        for (var i = 0; i < records.Count; i++)
        {
            var json = JsonSerializer.Serialize(records[i].Fields.Select(f => new[] { f.Key, f.Value }));
            c.Execute(
                "INSERT INTO agent_inventory (agent_id, record_type, record_index, fields_json, date_added) VALUES (@agentId, @type, @i, @json, @now)",
                new { agentId, type = records[i].Type, i, json, now }, tx);
        }
        tx.Commit();
    }

    public IReadOnlyList<InventoryRecord> Get(long agentId)
    {
        using var c = db.Open();
        var rows = c.Query<InventoryRow>(
            "SELECT record_type, record_index, fields_json FROM agent_inventory WHERE agent_id = @agentId ORDER BY record_index",
            new { agentId });
        return rows.Select(r => new InventoryRecord(r.RecordType,
            (JsonSerializer.Deserialize<string[][]>(r.FieldsJson) ?? [])
                .Where(p => p.Length == 2).Select(p => new KeyValuePair<string, string>(p[0], p[1])).ToList())).ToList();
    }
}

public sealed class ScreenshotStore(Database db)
{
    public long Add(long agentId, int width, int height, int bpp, string source, string fileName, byte[] thumbPng)
    {
        var now = Clock.Now();
        using var c = db.Open();
        return c.ExecuteScalar<long>(
            """
            INSERT INTO agent_screenshots (agent_id, date_added, width, height, bpp, source, file_name, thumb_png)
            VALUES (@agentId, @now, @width, @height, @bpp, @source, @fileName, @thumbPng); SELECT last_insert_rowid();
            """,
            new { agentId, now, width, height, bpp, source, fileName, thumbPng });
    }

    public IReadOnlyList<ScreenshotRecord> ForAgent(long agentId, int limit = 10)
    {
        using var c = db.Open();
        return c.Query<ScreenshotRecord>(
            "SELECT id, agent_id, date_added, width, height, bpp, source, file_name FROM agent_screenshots WHERE agent_id = @agentId ORDER BY id DESC LIMIT @limit",
            new { agentId, limit }).AsList();
    }

    public ScreenshotRecord? Get(long id)
    {
        using var c = db.Open();
        return c.QuerySingleOrDefault<ScreenshotRecord>(
            "SELECT id, agent_id, date_added, width, height, bpp, source, file_name FROM agent_screenshots WHERE id = @id", new { id });
    }

    public byte[]? Thumbnail(long id)
    {
        using var c = db.Open();
        return c.ExecuteScalar<byte[]?>("SELECT thumb_png FROM agent_screenshots WHERE id = @id", new { id });
    }

    /// <summary>Keeps the newest <paramref name="keep"/>; returns the file names of removed rows.</summary>
    public IReadOnlyList<string> Prune(long agentId, int keep)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        var old = c.Query<(long Id, string FileName)>(
            "SELECT id, file_name FROM agent_screenshots WHERE agent_id = @agentId ORDER BY id DESC LIMIT -1 OFFSET @keep",
            new { agentId, keep }, tx).AsList();
        foreach (var (id, _) in old)
            c.Execute("DELETE FROM agent_screenshots WHERE id = @id", new { id }, tx);
        tx.Commit();
        return old.Select(o => o.FileName).ToList();
    }
}

public sealed class SettingsStore(Database db) : Services.ISettingsBackend
{
    public event Action? Changed;

    public string? Get(string key)
    {
        using var c = db.Open();
        return c.ExecuteScalar<string?>("SELECT value FROM settings WHERE key = @key", new { key });
    }

    public IReadOnlyDictionary<string, string> All()
    {
        using var c = db.Open();
        return c.Query<(string Key, string Value)>("SELECT key, value FROM settings").ToDictionary(x => x.Key, x => x.Value);
    }

    public void Set(string key, string value)
    {
        using var c = db.Open();
        c.Execute("INSERT INTO settings (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value",
            new { key, value });
        Changed?.Invoke();
    }
}
