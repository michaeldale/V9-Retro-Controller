-- Times are Unix milliseconds (UTC). Port of the Bluetrait v9x_* tables, keyed by a local id,
-- with the agent's install-ID UUID attached when the agent reports one (0.8.0+).

CREATE TABLE agents (
    id                       INTEGER PRIMARY KEY AUTOINCREMENT,
    install_id               TEXT UNIQUE,
    name                     TEXT    NOT NULL,
    host                     TEXT    NOT NULL,
    port                     INTEGER NOT NULL DEFAULT 9869,
    enabled                  INTEGER NOT NULL DEFAULT 1,
    notes                    TEXT,
    vnc_port                 INTEGER NOT NULL DEFAULT 5900,
    vnc_password             TEXT,          -- protected with ISecretProtector
    date_added               INTEGER NOT NULL,
    last_modified            INTEGER NOT NULL,

    status                   TEXT    NOT NULL DEFAULT 'unknown',
    last_poll_attempt        INTEGER,
    last_seen                INTEGER,
    last_error               TEXT,
    consecutive_failures     INTEGER NOT NULL DEFAULT 0,
    identity_conflict        TEXT,          -- install ID reported by a different machine at this address

    agent_version            TEXT,
    build_id                 TEXT,
    computer_name            TEXT,
    windows_version          TEXT,
    os_name                  TEXT,
    raw_version              INTEGER,
    winsock_version          INTEGER,
    capabilities             INTEGER NOT NULL DEFAULT 0,
    boot_counter             INTEGER,
    uptime_ms                INTEGER,
    desktop_ready            INTEGER NOT NULL DEFAULT 0,
    screen_width             INTEGER,
    screen_height            INTEGER,
    screen_bpp               INTEGER,
    windows_dir              TEXT,
    system_dir               TEXT,
    listen_address           TEXT,
    allowed_client           TEXT,
    pending_job              TEXT,

    cpu_summary              TEXT,
    ram_total                INTEGER,
    disk_total               INTEGER,
    disk_free                INTEGER,
    inventory_updated_at     INTEGER,
    inventory_agent_version  TEXT,
    inventory_boot_counter   INTEGER,
    inventory_hash           TEXT,

    last_screenshot_id       INTEGER,
    screenshot_at            INTEGER,

    power_token              TEXT,
    power_action             TEXT,
    power_requested_at       INTEGER
);

CREATE INDEX agents_host_port ON agents (host, port);

CREATE TABLE agent_inventory (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    agent_id      INTEGER NOT NULL REFERENCES agents (id) ON DELETE CASCADE,
    record_type   TEXT    NOT NULL,
    record_index  INTEGER NOT NULL,
    fields_json   TEXT    NOT NULL,
    date_added    INTEGER NOT NULL
);
CREATE INDEX agent_inventory_agent ON agent_inventory (agent_id, record_index);

CREATE TABLE agent_screenshots (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    agent_id    INTEGER NOT NULL REFERENCES agents (id) ON DELETE CASCADE,
    date_added  INTEGER NOT NULL,
    width       INTEGER NOT NULL,
    height      INTEGER NOT NULL,
    bpp         INTEGER NOT NULL,
    source      TEXT    NOT NULL,           -- cron, manual, live
    file_name   TEXT    NOT NULL,           -- PNG under <data>/screenshots/<agent_id>/
    thumb_png   BLOB    NOT NULL
);
CREATE INDEX agent_screenshots_agent ON agent_screenshots (agent_id, id);

CREATE TABLE agent_events (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    agent_id    INTEGER NOT NULL REFERENCES agents (id) ON DELETE CASCADE,
    date_added  INTEGER NOT NULL,
    type        TEXT    NOT NULL,
    message     TEXT    NOT NULL
);
CREATE INDEX agent_events_agent ON agent_events (agent_id, id);
CREATE INDEX agent_events_recent ON agent_events (id);

CREATE TABLE settings (
    key    TEXT PRIMARY KEY,
    value  TEXT NOT NULL
);
