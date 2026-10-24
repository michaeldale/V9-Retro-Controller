using System.Globalization;
using System.Text.RegularExpressions;
using V9Retro.Protocol;

namespace V9Retro.Core.Formatting;

/// <summary>Port of the plugin's v9x_format: friendly OS and CPU names, sizes and durations.</summary>
public static partial class V9xFormat
{
    [GeneratedRegex(@"(\d+)\.(\d+)")]
    private static partial Regex MajorMinor();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>With only INFO ("Windows 4.10") the edition is a guess; the inventory os record
    /// (build and sub-version) makes it exact.</summary>
    public static string OsName(string? windowsVersion, InventoryRecord? os = null)
    {
        var text = (windowsVersion ?? "").Trim();
        if (text.StartsWith("DOS", StringComparison.OrdinalIgnoreCase))
            return "MS-DOS " + text[3..].Trim();
        int? major = null, minor = null, build = null;
        var sub = "";
        if (os is not null)
        {
            major = Int(os.Get("major"));
            minor = Int(os.Get("minor"));
            build = Int(os.Get("build"));
            sub = (os.Get("sub_version") ?? os.Get("csd") ?? "").Trim().ToUpperInvariant();
            if ((Int(os.Get("platform")) ?? 1) == 2)
                return (os.Get("product_name") ?? $"Windows NT {major}.{minor}").Trim();
        }
        if (major is null && MajorMinor().Match(text) is { Success: true } m)
        {
            major = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            minor = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        }
        if (major == 4 && minor == 0)
        {
            if (build >= 1111 || sub is "B" or "C") return "Windows 95 OSR2" + (sub == "C" ? ".5" : "");
            return "Windows 95" + (sub == "A" ? " OSR1" : "");
        }
        if (major == 4 && minor == 10) return build >= 2222 || sub == "A" ? "Windows 98 SE" : "Windows 98";
        if (major == 4 && minor == 90) return "Windows Me";
        if (os?.Get("product_name") is { Length: > 0 } product) return product.Trim();
        return text.Length > 0 ? text : "Unknown";
    }

    /// <summary>The CPUID brand string when the chip has one; otherwise a name from vendor and
    /// family/model, which covers the Pentium and K6 era.</summary>
    public static string CpuName(InventoryRecord? cpu)
    {
        if (cpu is null) return "";
        var name = Spaces().Replace(cpu.Get("brand") ?? "", " ").Trim();
        if (name.Length == 0)
            name = CpuFamilyName(cpu.Get("vendor") ?? "", Int(cpu.Get("family")) ?? 0, Int(cpu.Get("model")) ?? 0);
        if (name.Length == 0 && cpu.Get("reg_identifier") is { Length: > 0 } id) name = id;
        if (name.Length == 0 && cpu.Get("type") is { Length: > 0 } type) name = type + " class CPU";
        var mhz = Int(cpu.Get("mhz")) ?? 0;
        if (mhz > 0 && !name.Contains("hz", StringComparison.OrdinalIgnoreCase)) name += $" @ {mhz} MHz";
        var count = Int(cpu.Get("count")) ?? 1;
        if (count > 1) name = $"{count} x {name}";
        return name.Trim();
    }

    static string CpuFamilyName(string vendor, int family, int model) => vendor switch
    {
        "GenuineIntel" => family switch
        {
            4 => "Intel 486",
            5 => model >= 4 ? "Intel Pentium MMX" : "Intel Pentium",
            6 => model switch
            {
                1 => "Intel Pentium Pro",
                3 or 5 => "Intel Pentium II",
                6 => "Intel Celeron (Mendocino)",
                >= 7 and <= 11 => "Intel Pentium III",
                _ => "Intel P6 family",
            },
            >= 15 => "Intel Pentium 4",
            _ => "Intel",
        },
        "AuthenticAMD" => family switch
        {
            4 => "AMD 486 / 5x86",
            5 => model switch
            {
                <= 3 => "AMD K5",
                <= 7 => "AMD K6",
                8 => "AMD K6-2",
                9 => "AMD K6-III",
                _ => "AMD K6-2+ / K6-III+",
            },
            6 => "AMD Athlon / Duron",
            _ => "AMD",
        },
        "CyrixInstead" => family == 6 ? "Cyrix 6x86MX / MII" : "Cyrix",
        "CentaurHauls" => "IDT WinChip / VIA C3",
        "RiseRiseRise" => "Rise mP6",
        "GenuineTMx86" => "Transmeta Crusoe",
        "NexGenDriven" => "NexGen Nx586",
        "UMC UMC UMC " => "UMC",
        "unknown" => "386/486 (no CPUID)",
        _ => vendor,
    };

    static int? Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static string Bytes(double? bytes, int precision = 1)
    {
        if (bytes is null) return "—";
        var value = bytes.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return i == 0 ? $"{(long)value} B" : value.ToString(value >= 100 ? "N0" : "N" + precision, CultureInfo.CurrentCulture) + " " + units[i];
    }

    /// <summary>255.9 MB of usable memory is a 256 MB machine.</summary>
    public static string Ram(long? bytes)
    {
        if (bytes is null or 0) return "—";
        var mb = bytes.Value / 1048576.0;
        return mb >= 1024 ? (mb / 1024).ToString("N1", CultureInfo.CurrentCulture) + " GB" : $"{(long)Math.Ceiling(mb)} MB";
    }

    public static string Duration(long? ms)
    {
        if (ms is null) return "—";
        var s = ms.Value / 1000;
        long d = s / 86400, h = s % 86400 / 3600, m = s % 3600 / 60;
        if (d > 0) return $"{d}d {h}h";
        if (h > 0) return $"{h}h {m}m";
        if (m > 0) return $"{m}m";
        return $"{s}s";
    }

    public static string Ago(long? unixMs, long? nowMs = null)
    {
        if (unixMs is null) return "never";
        var diff = Math.Max(0, ((nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) - unixMs.Value) / 1000);
        if (diff < 10) return "just now";
        if (diff < 60) return $"{diff}s ago";
        if (diff < 3600) return $"{diff / 60} min ago";
        if (diff < 86400) return $"{diff / 3600} h ago";
        return $"{diff / 86400} d ago";
    }

    public static string Winsock(int? version)
    {
        if (version is null or 0) return "—";
        if (version == 0x0020) return "DOS (Watt-32)";
        return $"{version & 0xFF}.{(version >> 8) & 0xFF}";
    }

    public static string StatusLabel(string? status) => status switch
    {
        "online" => "Online",
        "offline" => "Offline",
        "rebooting" => "Rebooting",
        "shutdown" => "Shut down",
        _ => "Not polled yet",
    };

    /// <summary>Configuration Manager problem codes the inventory reports for devices.</summary>
    public static string DeviceProblem(string? code) => code switch
    {
        "1" => "Not configured",
        "10" => "Cannot start",
        "12" => "Resource conflict",
        "14" => "Restart required",
        "18" => "Reinstall drivers",
        "22" => "Disabled",
        "24" => "Not present",
        "28" => "No driver",
        "31" => "Driver failed",
        null or "" => "",
        _ => "Problem " + code,
    };
}
