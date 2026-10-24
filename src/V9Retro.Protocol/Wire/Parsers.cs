namespace V9Retro.Protocol.Wire;

/// <summary>Response payload decoders, kept free of I/O so they can be tested from bytes.
/// Layouts follow docs\protocol.md and mcp\v9x_mcp.py.</summary>
public static class Parsers
{
    public static V9xGuestException ParseError(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload, "ERROR_RESPONSE");
        var status = (V9xStatus)r.U32();
        var native = r.U32();
        var detail = r.AtEnd ? "" : r.AnsiString();
        return new V9xGuestException(status, native, detail);
    }

    static DesktopBlock ReadDesktop(ref PayloadReader r, bool withInstallId, out string? installId)
    {
        string pending = "", listen = "", allowed = "";
        bool ready = false;
        uint w = 0, h = 0, bpp = 0;
        installId = null;
        if (!r.AtEnd) pending = r.String();
        if (r.Remaining >= 16)
        {
            ready = r.U8() != 0;
            r.Skip(3);
            w = r.U32();
            h = r.U32();
            bpp = r.U32();
        }
        if (!r.AtEnd) listen = r.String();
        if (!r.AtEnd) allowed = r.String();
        if (withInstallId && !r.AtEnd)
        {
            var id = r.String();
            installId = id.Length > 0 ? id.ToLowerInvariant() : null;
        }
        r.EnsureEnd();
        return new DesktopBlock(pending, ready, w, h, bpp, listen, allowed);
    }

    public static HelloInfo ParseHello(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload, "HELLO_RESPONSE");
        var version = r.U16();
        r.U16();
        var caps = (Capabilities)r.U32();
        var maxPayload = r.U32();
        var boot = r.U32();
        var port = r.U16();
        var winsock = r.U16();
        var build = r.String();
        var desktop = ReadDesktop(ref r, false, out _);
        return new HelloInfo(version, caps, maxPayload, boot, port, winsock, build, desktop);
    }

    /// <param name="extended">True when the request carried INFO_FLAG_EXTENDED, so an
    /// install ID may follow allowed-client.</param>
    public static AgentInfo ParseInfo(ReadOnlySpan<byte> payload, bool extended)
    {
        var r = new PayloadReader(payload, "INFO_RESPONSE");
        var boot = r.U32();
        var uptime = r.U32();
        var caps = (Capabilities)r.U32();
        var port = r.U16();
        var winsock = r.U16();
        var raw = r.U32();
        var agentVersion = r.String();
        var build = r.String();
        var computer = r.String();
        var windows = r.String();
        var sysDir = r.String();
        var winDir = r.String();
        var curDir = r.String();
        var desktop = ReadDesktop(ref r, extended, out var installId);
        return new AgentInfo(boot, uptime, caps, port, winsock, raw, agentVersion, build, computer,
            windows, sysDir, winDir, curDir, desktop, installId);
    }

    public static PingResult ParsePing(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload, "PING_RESPONSE");
        return new PingResult(r.U32(), r.U32());
    }

    public static IReadOnlyList<InventoryRecord> ParseInventory(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload, "INVENTORY_RESPONSE");
        var count = r.U16();
        var records = new List<InventoryRecord>(count);
        for (var i = 0; i < count; i++)
        {
            var type = r.String();
            var fieldCount = r.U16();
            var fields = new List<KeyValuePair<string, string>>(fieldCount);
            for (var f = 0; f < fieldCount; f++)
            {
                var key = r.String();
                fields.Add(new(key, r.String()));
            }
            records.Add(new InventoryRecord(type, fields));
        }
        r.EnsureEnd();
        return records;
    }

    public static ExecResult ParseExecComplete(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 28) throw new V9xProtocolException("invalid EXEC_COMPLETE payload");
        var r = new PayloadReader(payload, "EXEC_COMPLETE");
        return new ExecResult((ExecResultCategory)r.U32(), r.S32(), r.U32(), r.U32(), r.U32(), r.U32(), (ExecFlags)r.U32());
    }

    public static FileStat ParseStat(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 16) throw new V9xProtocolException("invalid FILE_STAT_RESPONSE payload");
        var r = new PayloadReader(payload, "FILE_STAT_RESPONSE");
        var exists = r.U8() != 0;
        var dir = r.U8() != 0;
        r.U16();
        return new FileStat(exists, dir, r.U32(), r.U32(), r.U32());
    }

    public static IReadOnlyList<FileEntry> ParseList(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload, "FILE_LIST_RESPONSE");
        var count = r.U32();
        var entries = new List<FileEntry>((int)Math.Min(count, 4096));
        for (var i = 0u; i < count; i++)
        {
            var attributes = r.U32();
            var size = r.U32();
            entries.Add(new FileEntry(r.AnsiString(), size, attributes));
        }
        r.EnsureEnd();
        return entries;
    }

    public static ReadTextResult ParseReadText(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload, "FILE_READ_TEXT_RESPONSE");
        var size = r.U32();
        var lines = r.U32();
        var matched = r.U32();
        var returned = r.U32();
        var flags = r.U32();
        var length = (int)r.U32();
        var text = GuestText.Decode(r.Bytes(length));
        r.EnsureEnd();
        return new ReadTextResult(size, lines, matched, returned, (flags & 1) != 0, (flags & 2) != 0, text);
    }

    public static IReadOnlyList<ProcessEntry> ParseProcessList(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload, "PROCESS_LIST_RESPONSE");
        var count = r.U32();
        var list = new List<ProcessEntry>((int)Math.Min(count, 1024));
        for (var i = 0u; i < count; i++)
        {
            var pid = r.U32();
            var parent = r.U32();
            var threads = r.U32();
            var flags = r.U32();
            var job = r.U32();
            list.Add(new ProcessEntry(pid, parent, threads, flags, job, r.AnsiString()));
        }
        r.EnsureEnd();
        return list;
    }

    public static WindowListResult ParseWindowList(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload, "WINDOW_LIST_RESPONSE");
        var count = r.U32();
        var truncated = r.U32() != 0;
        var list = new List<WindowEntry>((int)Math.Min(count, 1024));
        for (var i = 0u; i < count; i++)
        {
            var handle = r.U32();
            var pid = r.U32();
            var flags = r.U32();
            var left = r.S32();
            var top = r.S32();
            var right = r.S32();
            var bottom = r.S32();
            var cls = r.AnsiString();
            list.Add(new WindowEntry(handle, pid, flags, left, top, right, bottom, cls, r.AnsiString()));
        }
        r.EnsureEnd();
        return new WindowListResult(list, truncated);
    }

    public static RegistryQueryResult ParseRegQuery(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload, "REG_QUERY_RESPONSE");
        var subkeyCount = r.U32();
        var valueCount = r.U32();
        var flags = r.U32();
        var subkeys = new List<string>((int)Math.Min(subkeyCount, 4096));
        for (var i = 0u; i < subkeyCount; i++) subkeys.Add(r.AnsiString());
        var values = new List<RegistryValue>((int)Math.Min(valueCount, 4096));
        for (var i = 0u; i < valueCount; i++)
        {
            var name = r.AnsiString();
            var type = (RegistryValueType)r.U32();
            var length = (int)r.U32();
            values.Add(new RegistryValue(name, type, r.Bytes(length).ToArray()));
        }
        r.EnsureEnd();
        return new RegistryQueryResult(subkeys, values, (flags & 1) != 0);
    }

    public static ScreenshotInfo ParseScreenshot(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload, "SCREENSHOT_RESPONSE");
        var info = new ScreenshotInfo(r.U32(), r.U32(), r.U32(), r.U32(), r.U32(), r.String());
        r.EnsureEnd();
        return info;
    }

    public static PowerAccepted ParsePowerAccepted(ReadOnlySpan<byte> payload)
    {
        var r = new PayloadReader(payload, "power acceptance");
        var boot = r.U32();
        var token = r.AtEnd ? "" : r.String();
        r.EnsureEnd();
        return new PowerAccepted(boot, token);
    }

    public static InputResult ParseInput(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 12) throw new V9xProtocolException("invalid INPUT_RESPONSE payload");
        var r = new PayloadReader(payload, "INPUT_RESPONSE");
        return new InputResult(r.U32(), r.S32(), r.S32());
    }
}
