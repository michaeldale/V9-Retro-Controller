using System.IO.Compression;
using V9Retro.Core.Data;
using V9Retro.Core.Imaging;
using V9Retro.Core.Model;
using V9Retro.Core.Services;
using V9Retro.Protocol;
using V9Retro.Protocol.Imaging;
using V9Retro.Protocol.Tests;
using V9Retro.Protocol.Wire;

namespace V9Retro.Core.Tests;

public sealed class CoreHostTests : IAsyncLifetime
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "v9retro-tests-" + Guid.NewGuid().ToString("N"));
    CoreHost _host = null!;
    FakeAgent _agent = null!;

    public Task InitializeAsync()
    {
        _host = new CoreHost(_dir);
        _agent = new FakeAgent();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _agent.DisposeAsync();
        await _host.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    static byte[] InfoPayload(uint boot, string id) =>
        new PayloadWriter().U32(boot).U32(1000).U32(0x3FFFFF).U16(9869).U16(0x202).U32(0)
            .String("0.13.0").String("b").String("RETRO").String("4.10.2222 A").String("s").String("w").String("c")
            .String("").U8(1).U8(0).U8(0).U8(0).U32(2).U32(2).U32(24).String("").String("").String(id)
            .ToArray();

    void ServeInfo(uint boot, string id) =>
        _agent.Handlers[MessageType.InfoRequest] = (r, _) => r.Send(MessageType.InfoResponse, InfoPayload(boot, id));

    [Fact]
    public void Migrations_are_idempotent()
    {
        Assert.Equal(0, _host.Database.Migrate());
    }

    [Fact]
    public async Task Poll_stores_state_events_and_install_id()
    {
        ServeInfo(4, "11111111-2222-4333-8444-555555555555");
        var added = _host.Agents.Add("Retro", "127.0.0.1", _agent.Port);
        var result = await _host.Agents.PollAsync(added.Id);
        Assert.True(result.Ok);
        Assert.True(result.NeedsInventory);
        var saved = _host.Agents.Get(added.Id)!;
        Assert.Equal(AgentStatus.Online, saved.Status);
        Assert.Equal("11111111-2222-4333-8444-555555555555", saved.InstallId);
        Assert.Equal("RETRO", saved.ComputerName);
        var events = _host.Agents.Events(added.Id);
        Assert.Contains(events, e => e.Type == AgentEventType.Added);
        Assert.Contains(events, e => e.Type == AgentEventType.Online);

        // Throttled: a second poll within 3 s is skipped.
        Assert.True((await _host.Agents.PollAsync(added.Id, throttle: true)).Skipped);
    }

    [Fact]
    public async Task Settings_edit_does_not_clobber_poll_state()
    {
        ServeInfo(4, "aaaaaaaa-2222-4333-8444-555555555555");
        var a = _host.Agents.Add("Retro", "127.0.0.1", _agent.Port);
        await _host.Agents.PollAsync(a.Id);
        _host.Agents.Update(a.Id, "Renamed", "127.0.0.1", _agent.Port, true, "note", 5900, "secret", true);
        var saved = _host.Agents.Get(a.Id)!;
        Assert.Equal("Renamed", saved.Name);
        Assert.Equal(AgentStatus.Online, saved.Status);
        Assert.Equal("secret", _host.Agents.VncPassword(saved));
        Assert.StartsWith(OperatingSystem.IsWindows() ? "dpapi:" : "b64:", saved.VncPassword);
    }

    [Fact]
    public async Task Discovery_moves_a_known_install_to_its_new_address()
    {
        ServeInfo(4, "bbbbbbbb-2222-4333-8444-555555555555");
        var a = _host.Agents.Add("Retro", "127.0.0.1", _agent.Port);
        await _host.Agents.PollAsync(a.Id);
        var moved = _host.Agents.ObserveDiscovered("bbbbbbbb-2222-4333-8444-555555555555", "192.168.7.7", 9869);
        Assert.Equal("192.168.7.7", moved!.Host);
        Assert.Equal("192.168.7.7", _host.Agents.Get(a.Id)!.Host);
        Assert.Contains(_host.Agents.Events(a.Id), e => e.Type == AgentEventType.AddressChanged);
        Assert.Null(_host.Agents.ObserveDiscovered("cccccccc-0000-4000-8000-000000000000", "1.2.3.4", 9869));
    }

    [Fact]
    public async Task Same_install_saved_twice_is_flagged()
    {
        const string id = "dddddddd-2222-4333-8444-555555555555";
        ServeInfo(4, id);
        var first = _host.Agents.Add("First", "127.0.0.1", _agent.Port);
        await _host.Agents.PollAsync(first.Id);
        var second = _host.Agents.Add("Second", "localhost", _agent.Port);
        var result = await _host.Agents.PollAsync(second.Id);
        Assert.False(result.Ok);
        Assert.Equal(id, _host.Agents.Get(second.Id)!.IdentityConflict);
        Assert.Null(_host.Agents.Get(second.Id)!.InstallId);
    }

    [Fact]
    public async Task Screenshot_is_stored_as_png_with_thumbnail_and_pruned()
    {
        ServeInfo(4, "eeeeeeee-2222-4333-8444-555555555555");
        var bmp = BmpDecoderTests.Sample24();
        _agent.Handlers[MessageType.ScreenshotRequest] = (r, _) => r.Send(MessageType.ScreenshotResponse,
            new PayloadWriter().U32(2).U32(2).U32(24).U32((uint)bmp.Length).U32(Crc32.Compute(bmp)).String(@"C:\S.BMP").ToArray());
        _agent.Handlers[MessageType.FileOpenRead] = async (r, _) =>
        {
            await r.Send(MessageType.FileReadChunk, new PayloadWriter().U32(0).Bytes(bmp).ToArray());
            await r.Send(MessageType.FileReadComplete, new PayloadWriter().U32((uint)bmp.Length).U32(Crc32.Compute(bmp)).ToArray());
        };
        var a = _host.Agents.Add("Retro", "127.0.0.1", _agent.Port);
        await _host.Agents.PollAsync(a.Id);
        _host.Settings.Set(AppSettings.ScreenshotKeep, 2);
        for (var i = 0; i < 3; i++) await _host.Agents.TakeScreenshotAsync(a.Id, "manual");

        var shots = _host.Agents.Screenshots(a.Id);
        Assert.Equal(2, shots.Count);
        var path = _host.Agents.ScreenshotPath(shots[0]);
        Assert.NotNull(path);
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(path)!).Length);
        Assert.Equal(0x89, File.ReadAllBytes(path)[0]);
        Assert.NotEmpty(_host.Agents.Thumbnail(shots[0].Id)!);
        Assert.Equal(shots[0].Id, _host.Agents.Get(a.Id)!.LastScreenshotId);
    }

    [Fact]
    public async Task Inventory_is_stored_and_summarised()
    {
        ServeInfo(4, "ffffffff-2222-4333-8444-555555555555");
        _agent.Handlers[MessageType.InventoryRequest] = (r, _) => r.Send(MessageType.InventoryResponse, new PayloadWriter().U16(3)
            .String("cpu").U16(3).String("vendor").String("AuthenticAMD").String("family").String("5").String("model").String("8")
            .String("memory").U16(1).String("total_phys").String("134217728")
            .String("drive").U16(4).String("letter").String("C").String("type").String("fixed").String("total").String("2000000000").String("free").String("500000000")
            .ToArray());
        var a = _host.Agents.Add("Retro", "127.0.0.1", _agent.Port);
        await _host.Agents.PollAsync(a.Id);
        var updated = await _host.Agents.RefreshInventoryAsync(a.Id);
        Assert.Equal("AMD K6-2", updated.CpuSummary);
        Assert.Equal(134217728, updated.RamTotal);
        Assert.Equal(2000000000, updated.DiskTotal);
        Assert.Equal(3, _host.Agents.Inventory(a.Id).Count);
        Assert.False(StatusRules.NeedsInventory(_host.Agents.Get(a.Id)!, 24, Clock.Now()));
    }

    [Fact]
    public async Task Sweep_polls_due_agents()
    {
        ServeInfo(4, "12121212-2222-4333-8444-555555555555");
        _host.Agents.Add("Retro", "127.0.0.1", _agent.Port);
        var disabled = _host.Agents.Add("Off", "127.0.0.1", 1);
        _host.Agents.Update(disabled.Id, "Off", "127.0.0.1", 1, false, null, 5900, null, false);
        _host.Settings.Set(AppSettings.ScreenshotMinutes, 0);
        var counts = await _host.Scheduler.SweepAsync();
        Assert.Equal(1, counts.Due);
        Assert.Equal(1, counts.Online);
    }

    [Fact]
    public void Ini_edit_keeps_other_lines_and_crlf()
    {
        const string ini = "[agent]\r\nport=9869\r\n\r\n[vnc]\r\nenabled=0\r\n\r\n[other]\r\nx=1\r\n";
        var updated = MaintenanceService.SetIniValue(ini, "vnc", "enabled", "1");
        Assert.Equal("[agent]\r\nport=9869\r\n\r\n[vnc]\r\nenabled=1\r\n\r\n[other]\r\nx=1\r\n", updated);
        var withPassword = MaintenanceService.SetIniValue(updated, "vnc", "password", "abc");
        Assert.Contains("[vnc]\r\nenabled=1\r\npassword=abc\r\n\r\n[other]", withPassword);
        var added = MaintenanceService.SetIniValue("[agent]\nport=1\n", "vnc", "enabled", "1");
        Assert.Equal("[agent]\r\nport=1\r\n\r\n[vnc]\r\nenabled=1\r\n", added);
        Assert.Equal((true, 5901, true), MaintenanceService.ParseVncIni("[vnc]\r\nenabled=1\r\nport=5901\r\npassword=x\r\n"));
    }

    [Fact]
    public void Png_round_trips_through_zlib()
    {
        var image = new BgraImage(3, 2, [1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255, 13, 14, 15, 255, 16, 17, 18, 255]);
        var png = PngEncoder.Encode(image);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png[..4]);
        // IDAT starts after signature (8) + IHDR chunk (25) + length/type (8).
        var idatLength = (png[33] << 24) | (png[34] << 16) | (png[35] << 8) | png[36];
        using var z = new ZLibStream(new MemoryStream(png, 41, idatLength), CompressionMode.Decompress);
        var raw = new MemoryStream();
        z.CopyTo(raw);
        Assert.Equal(2 * (1 + 9), raw.Length);
        var thumb = PngEncoder.Thumbnail(new BgraImage(640, 480, new byte[640 * 480 * 4]));
        Assert.Equal((320, 240), (thumb.Width, thumb.Height));
    }
}
