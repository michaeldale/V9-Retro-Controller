using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using V9Retro.Core.Formatting;
using V9Retro.Protocol;

namespace V9Retro.App.ViewModels;

/// <summary>A label/value line; Percent draws a usage bar, Warning a problem badge.</summary>
public sealed record InventoryLine(string Label, string Value, double? Percent = null, string? Warning = null)
{
    public bool HasBar => Percent is not null;
    public bool HasWarning => Warning is not null;
}

public sealed record InventorySection(string Title, IReadOnlyList<InventoryLine> Lines);

/// <summary>The plugin's inventory cards: processor, memory, drives, display and network,
/// Windows, BIOS, and devices grouped by class with Device Manager problem codes.</summary>
public sealed partial class InventoryTab(AgentPageViewModel page) : AgentTabViewModel(page, "Inventory")
{
    public ObservableCollection<InventorySection> Sections { get; } = new();

    [ObservableProperty] public partial string? Updated { get; set; }

    public override string? Unavailable => Page.Agent.Capabilities != 0 && !Page.Agent.Has(Capabilities.Inventory)
        ? "This agent does not report inventory (needs agent 0.7.0 or later)."
        : null;

    protected override async Task OnFirstShownAsync()
    {
        await ReloadAsync();
        if (Sections.Count == 0 && Page.Agent.IsOnline) await Refresh();
    }

    public async Task ReloadAsync()
    {
        if (!Page.IsSaved) return;
        try { Build(await Page.Services.Backend.Agents.InventoryAsync(Page.AgentId)); }
        catch (Exception ex) { Error = Services.Ui.Describe(ex); return; }
        Updated = Page.Agent.InventoryUpdatedAt is { } t ? "Updated " + V9xFormat.Ago(t) : null;
    }

    [RelayCommand]
    Task Refresh() => Run("Reading inventory…", async () =>
    {
        if (Page.IsSaved)
        {
            Page.Agent = await Page.Services.Backend.Agents.RefreshInventoryAsync(Page.AgentId);
            await ReloadAsync();
        }
        else
        {
            Build(await UseAsync(s => s.InventoryAsync(), TimeSpan.FromSeconds(30)));
            Updated = "Read just now (not saved)";
        }
    });

    static double Num(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    void Build(IReadOnlyList<InventoryRecord> records)
    {
        Sections.Clear();
        InventoryRecord? One(string type) => records.FirstOrDefault(r => r.Type == type);
        static void Line(List<InventoryLine> lines, string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) lines.Add(new InventoryLine(label, value.Trim()));
        }

        if (One("cpu") is { } cpu)
        {
            var l = new List<InventoryLine>();
            Line(l, "Processor", V9xFormat.CpuName(cpu));
            Line(l, "Vendor", cpu.Get("vendor"));
            Line(l, "Family / model / stepping", $"{cpu.Get("family")} / {cpu.Get("model")} / {cpu.Get("stepping")}".Trim(' ', '/'));
            Line(l, "Features", cpu.Get("features"));
            Line(l, "Registry", cpu.Get("reg_identifier"));
            Sections.Add(new InventorySection("Processor", l));
        }
        if (One("memory") is { } mem)
        {
            var l = new List<InventoryLine>();
            double total = Num(mem.Get("total_phys")), avail = Num(mem.Get("avail_phys"));
            if (total > 0)
                l.Add(new InventoryLine("Physical", $"{V9xFormat.Bytes(total - avail)} used of {V9xFormat.Ram((long)total)}", 100 * (total - avail) / total));
            double page = Num(mem.Get("total_page")), pageFree = Num(mem.Get("avail_page"));
            if (page > 0)
                l.Add(new InventoryLine("Swap", $"{V9xFormat.Bytes(page - pageFree)} used of {V9xFormat.Bytes(page)}", 100 * (page - pageFree) / page));
            Line(l, "Load", mem.Get("load") is { } load ? load + "%" : null);
            Sections.Add(new InventorySection("Memory", l));
        }
        var drives = records.Where(r => r.Type == "drive").ToList();
        if (drives.Count > 0)
        {
            var l = new List<InventoryLine>();
            foreach (var d in drives)
            {
                double total = Num(d.Get("total")), free = Num(d.Get("free"));
                var label = $"{d.Get("letter")}: {d.Get("label")}".Trim();
                if (total > 0)
                    l.Add(new InventoryLine(label, $"{V9xFormat.Bytes(total - free)} used of {V9xFormat.Bytes(total)} · {d.Get("fs")}", 100 * (total - free) / total));
                else
                    l.Add(new InventoryLine(label, d.Get("type") ?? ""));
            }
            Sections.Add(new InventorySection("Drives", l));
        }
        {
            var l = new List<InventoryLine>();
            if (One("display") is { } disp)
            {
                Line(l, "Display adapter", disp.Get("desc"));
                Line(l, "Mode", disp.Get("width") is { } w ? $"{w}×{disp.Get("height")}, {disp.Get("bpp")}-bit{(disp.Get("refresh") is { } hz ? $", {hz} Hz" : "")}" : null);
            }
            if (One("net") is { } net)
            {
                Line(l, "Host name", net.Get("hostname"));
                Line(l, "IP addresses", string.Join(", ", net.GetAll("ip")));
            }
            if (l.Count > 0) Sections.Add(new InventorySection("Display and network", l));
        }
        if (One("os") is { } os)
        {
            var l = new List<InventoryLine>();
            Line(l, "Product", V9xFormat.OsName(Page.Agent.WindowsVersion, os));
            Line(l, "Version", $"{os.Get("version_number")} {os.Get("sub_version") ?? os.Get("csd")}");
            Line(l, "DirectX", os.Get("directx"));
            Line(l, "Internet Explorer", os.Get("ie_version"));
            Line(l, "Registered owner", string.Join(", ", new[] { os.Get("registered_owner"), os.Get("registered_org") }.Where(s => !string.IsNullOrWhiteSpace(s))));
            Line(l, "Safe mode", os.Get("clean_boot") is { } cb && cb != "0" ? "Yes (" + cb + ")" : null);
            Sections.Add(new InventorySection("Windows", l));
        }
        if (One("bios") is { } bios)
        {
            var l = new List<InventoryLine>();
            Line(l, "BIOS", bios.Get("name") ?? bios.Get("version"));
            Line(l, "Date", bios.Get("date"));
            Line(l, "Video BIOS", bios.Get("video_version"));
            Sections.Add(new InventorySection("BIOS", l));
        }
        var devices = records.Where(r => r.Type == "device").ToList();
        foreach (var group in devices.GroupBy(d => d.Get("class") ?? "Other").OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var l = group.Select(d =>
            {
                var problem = d.Get("problem");
                return new InventoryLine(d.Get("desc") ?? "(unnamed)", d.Get("provider") ?? "",
                    Warning: string.IsNullOrEmpty(problem) ? null : V9xFormat.DeviceProblem(problem));
            }).ToList();
            Sections.Add(new InventorySection($"Devices: {group.Key}", l));
        }
        if (One("meta") is { } meta && meta.Get("truncated") == "1")
            Sections.Add(new InventorySection("Note", [new InventoryLine("Truncated", "The agent cut the device list short (16 KiB reply limit).")]));
    }
}
