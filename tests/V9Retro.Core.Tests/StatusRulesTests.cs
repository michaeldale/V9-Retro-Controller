using V9Retro.Core.Model;
using V9Retro.Core.Services;
using V9Retro.Protocol;

namespace V9Retro.Core.Tests;

public class StatusRulesTests
{
    const long Now = 1_800_000_000_000;

    static AgentInfo Info(uint boot, string pending = "", string? id = null, string version = "0.13.0") =>
        new(boot, 5000, Capabilities.Info | Capabilities.Inventory | Capabilities.ScreenshotBmp | Capabilities.FileRead,
            9869, 0x202, 0, version, "b", "RETRO98", "4.10.2222 A", "s", "w", "c",
            new DesktopBlock(pending, true, 800, 600, 16, "", ""), id);

    static Agent Online(long boot = 5) => new()
    {
        Id = 1, Name = "retro", Host = "10.0.0.5", Status = AgentStatus.Online, BootCounter = boot, AgentVersion = "0.13.0",
    };

    [Fact]
    public void First_contact_goes_online_and_attaches_install_id()
    {
        var o = StatusRules.Success(new Agent { Id = 1, Host = "h" }, Info(3, id: "abc"), Now);
        Assert.True(o.Ok);
        Assert.Equal(AgentStatus.Online, o.Agent.Status);
        Assert.Equal("abc", o.Agent.InstallId);
        Assert.Equal("Windows 98", o.Agent.OsName); // INFO alone has no build; inventory refines it to SE
        Assert.Contains(o.Events, e => e.Type == AgentEventType.Online && e.Message.StartsWith("First contact"));
    }

    [Fact]
    public void Boot_change_while_online_is_a_restart_and_version_change_an_update()
    {
        var o = StatusRules.Success(Online(5), Info(6, version: "0.13.1"), Now);
        Assert.Contains(o.Events, e => e.Type == AgentEventType.Restarted);
        Assert.Contains(o.Events, e => e.Type == AgentEventType.AgentUpdated);
    }

    [Fact]
    public void Reboot_needs_new_boot_counter_and_matching_token()
    {
        var rebooting = Online(5);
        rebooting.Status = AgentStatus.Rebooting;
        rebooting.PowerToken = "v9rtoken";
        rebooting.PowerAction = "reboot";
        rebooting.PowerRequestedAt = Now - 10_000;

        // Still the pre-reboot agent: same counter.
        var o = StatusRules.Success(rebooting, Info(5, pending: "v9rtoken"), Now);
        Assert.Equal(AgentStatus.Rebooting, o.Agent.Status);
        Assert.Empty(o.Events);

        // New counter but no token: an unrelated restart; not proof yet.
        o = StatusRules.Success(rebooting, Info(6, pending: ""), Now);
        Assert.Equal(AgentStatus.Rebooting, o.Agent.Status);

        o = StatusRules.Success(rebooting, Info(6, pending: "v9rtoken"), Now);
        Assert.Equal(AgentStatus.Online, o.Agent.Status);
        Assert.Null(o.Agent.PowerToken);
        Assert.Contains(o.Events, e => e.Type == AgentEventType.Rebooted);
    }

    [Fact]
    public void Reboot_times_out_after_five_minutes()
    {
        var rebooting = Online(5);
        rebooting.Status = AgentStatus.Rebooting;
        rebooting.PowerToken = "t";
        rebooting.PowerRequestedAt = Now - StatusRules.RebootGraceMs - 1;

        var answering = StatusRules.Success(rebooting, Info(5), Now);
        Assert.Equal(AgentStatus.Online, answering.Agent.Status);
        Assert.Contains(answering.Events, e => e.Type == AgentEventType.RebootTimeout);

        var silent = StatusRules.Failure(rebooting, "timed out", Now, 2);
        Assert.Equal(AgentStatus.Offline, silent.Agent.Status);
        Assert.Contains(silent.Events, e => e.Type == AgentEventType.RebootTimeout);
    }

    [Fact]
    public void Shutdown_is_not_an_outage()
    {
        var off = Online();
        off.Status = AgentStatus.Shutdown;
        off.PowerToken = "t";
        for (var i = 0; i < 5; i++) off = StatusRules.Failure(off, "refused", Now, 2).Agent;
        Assert.Equal(AgentStatus.Shutdown, off.Status);

        var back = StatusRules.Success(off, Info(6), Now);
        Assert.Equal(AgentStatus.Online, back.Agent.Status);
        Assert.Null(back.Agent.PowerToken);
        Assert.Contains(back.Events, e => e.Type == AgentEventType.Online);
    }

    [Fact]
    public void Offline_after_n_failures()
    {
        var a = Online();
        var first = StatusRules.Failure(a, "refused", Now, 2);
        Assert.Equal(AgentStatus.Online, first.Agent.Status);
        var second = StatusRules.Failure(first.Agent, "refused", Now, 2);
        Assert.Equal(AgentStatus.Offline, second.Agent.Status);
        Assert.Single(second.Events);
        var third = StatusRules.Failure(second.Agent, "refused", Now, 2);
        Assert.Empty(third.Events); // already offline: logged once
    }

    [Fact]
    public void Different_install_id_is_a_conflict_not_online()
    {
        var a = Online();
        a.InstallId = "aaa";
        var o = StatusRules.Success(a, Info(9, id: "bbb"), Now);
        Assert.False(o.Ok);
        Assert.Equal("bbb", o.Agent.IdentityConflict);
        Assert.Equal(AgentStatus.Online, o.Agent.Status); // unchanged; the UI shows the conflict
        Assert.Equal(5, o.Agent.BootCounter);
        Assert.Single(o.Events);

        // Logged once, not every poll.
        Assert.Empty(StatusRules.Success(o.Agent, Info(9, id: "bbb"), Now).Events);
    }

    [Fact]
    public void Inventory_due_rules()
    {
        var a = Online();
        a.Capabilities = (long)Capabilities.Inventory;
        Assert.True(StatusRules.NeedsInventory(a, 24, Now));
        a.InventoryUpdatedAt = Now - 1000;
        a.InventoryBootCounter = a.BootCounter;
        a.InventoryAgentVersion = a.AgentVersion;
        Assert.False(StatusRules.NeedsInventory(a, 24, Now));
        a.BootCounter = 99;
        Assert.True(StatusRules.NeedsInventory(a, 24, Now));
    }
}
