using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace V9Retro.Discovery;

/// <summary>DNS resource record types the browser understands.</summary>
public enum DnsRecordType : ushort
{
    A = 1,
    Ptr = 12,
    Txt = 16,
    Srv = 33,
    Any = 255,
}

/// <summary>Thrown when a packet is not a well-formed DNS message.</summary>
public sealed class DnsFormatException(string message) : FormatException(message);

/// <summary>SRV record data (RFC 2782).</summary>
public sealed record DnsSrv(ushort Priority, ushort Weight, ushort Port, string Target);

/// <summary>
/// One answer, authority or additional record. Exactly one of the typed data
/// properties is set for PTR, SRV, TXT and A; other types keep only <see cref="RawData"/>.
/// </summary>
public sealed record DnsRecord(
    string Name,
    DnsRecordType Type,
    ushort Class,
    bool CacheFlush,
    uint Ttl,
    byte[] RawData)
{
    public string? PtrTarget { get; init; }
    public DnsSrv? Srv { get; init; }
    public IReadOnlyList<string>? Txt { get; init; }
    public IPAddress? Address { get; init; }
}

/// <summary>
/// Minimal DNS message codec for mDNS / DNS-SD (RFC 1035, 6762, 6763). Reads
/// compressed names; builds uncompressed queries.
/// </summary>
public sealed class DnsMessage
{
    private const int HeaderLength = 12;
    private const int MaxPointerJumps = 16;
    private const int MaxNameLength = 255;

    public ushort Id { get; }
    public ushort Flags { get; }
    public bool IsResponse => (Flags & 0x8000) != 0;
    public int QuestionCount { get; }

    /// <summary>Answers, then authority, then additional records, in packet order.</summary>
    public IReadOnlyList<DnsRecord> Records { get; }

    private DnsMessage(ushort id, ushort flags, int questionCount, IReadOnlyList<DnsRecord> records)
    {
        Id = id;
        Flags = flags;
        QuestionCount = questionCount;
        Records = records;
    }

    public static bool TryParse(ReadOnlySpan<byte> packet, out DnsMessage? message)
    {
        try
        {
            message = Parse(packet);
            return true;
        }
        catch (DnsFormatException)
        {
            message = null;
            return false;
        }
    }

    public static DnsMessage Parse(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < HeaderLength)
            throw new DnsFormatException("truncated DNS header");

        ushort id = BinaryPrimitives.ReadUInt16BigEndian(packet);
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
        int questions = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
        int recordCount = BinaryPrimitives.ReadUInt16BigEndian(packet[6..])
            + BinaryPrimitives.ReadUInt16BigEndian(packet[8..])
            + BinaryPrimitives.ReadUInt16BigEndian(packet[10..]);

        int offset = HeaderLength;
        for (int i = 0; i < questions; i++)
        {
            ReadName(packet, ref offset);
            offset += 4;
            if (offset > packet.Length)
                throw new DnsFormatException("truncated DNS question");
        }

        var records = new List<DnsRecord>(Math.Min(recordCount, 64));
        for (int i = 0; i < recordCount; i++)
            records.Add(ReadRecord(packet, ref offset));

        return new DnsMessage(id, flags, questions, records);
    }

    /// <summary>
    /// Builds a one-question query. Sent from a port other than 5353 it is a
    /// legacy one-shot query and is answered unicast to the sender.
    /// </summary>
    public static byte[] BuildQuery(ushort id, string name, DnsRecordType type, bool unicastResponse = false)
    {
        var bytes = new List<byte>(64);
        Span<byte> header = stackalloc byte[HeaderLength];
        header.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);
        bytes.AddRange(header.ToArray());
        WriteName(bytes, name);
        Span<byte> tail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(tail, (ushort)type);
        BinaryPrimitives.WriteUInt16BigEndian(tail[2..], (ushort)(unicastResponse ? 0x8001 : 0x0001));
        bytes.AddRange(tail.ToArray());
        return bytes.ToArray();
    }

    internal static void WriteName(List<byte> output, string name)
    {
        foreach (string label in name.TrimEnd('.').Split('.'))
        {
            byte[] raw = Encoding.UTF8.GetBytes(label);
            if (raw.Length is < 1 or > 63)
                throw new ArgumentException($"invalid DNS label '{label}'", nameof(name));
            output.Add((byte)raw.Length);
            output.AddRange(raw);
        }
        output.Add(0);
    }

    private static DnsRecord ReadRecord(ReadOnlySpan<byte> packet, ref int offset)
    {
        string name = ReadName(packet, ref offset);
        if (offset + 10 > packet.Length)
            throw new DnsFormatException("truncated DNS record");

        var type = (DnsRecordType)BinaryPrimitives.ReadUInt16BigEndian(packet[offset..]);
        ushort rawClass = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 2)..]);
        uint ttl = BinaryPrimitives.ReadUInt32BigEndian(packet[(offset + 4)..]);
        int length = BinaryPrimitives.ReadUInt16BigEndian(packet[(offset + 8)..]);
        offset += 10;
        if (offset + length > packet.Length)
            throw new DnsFormatException("truncated DNS record data");

        int dataStart = offset;
        ReadOnlySpan<byte> data = packet.Slice(dataStart, length);
        offset += length;

        var record = new DnsRecord(name, type, (ushort)(rawClass & 0x7FFF), (rawClass & 0x8000) != 0, ttl, data.ToArray());
        switch (type)
        {
            case DnsRecordType.A:
                if (length != 4)
                    throw new DnsFormatException("invalid DNS A record");
                return record with { Address = new IPAddress(data) };

            case DnsRecordType.Ptr:
            {
                // Names inside RDATA may point anywhere earlier in the packet.
                int at = dataStart;
                return record with { PtrTarget = ReadName(packet, ref at, dataStart + length) };
            }

            case DnsRecordType.Srv:
            {
                if (length < 7)
                    throw new DnsFormatException("invalid DNS SRV record");
                int at = dataStart + 6;
                string target = ReadName(packet, ref at, dataStart + length);
                return record with
                {
                    Srv = new DnsSrv(
                        BinaryPrimitives.ReadUInt16BigEndian(data),
                        BinaryPrimitives.ReadUInt16BigEndian(data[2..]),
                        BinaryPrimitives.ReadUInt16BigEndian(data[4..]),
                        target),
                };
            }

            case DnsRecordType.Txt:
            {
                var strings = new List<string>();
                int at = 0;
                while (at < length)
                {
                    int size = data[at];
                    if (at + 1 + size > length)
                        throw new DnsFormatException("invalid DNS TXT record");
                    if (size > 0)
                        strings.Add(Encoding.UTF8.GetString(data.Slice(at + 1, size)));
                    at += 1 + size;
                }
                return record with { Txt = strings };
            }

            default:
                return record;
        }
    }

    /// <summary>
    /// Reads a possibly compressed name at <paramref name="offset"/> and moves
    /// <paramref name="offset"/> past it (past the first pointer, if any).
    /// <paramref name="limit"/> bounds the uncompressed part, e.g. to an RDATA field.
    /// </summary>
    internal static string ReadName(ReadOnlySpan<byte> packet, ref int offset, int limit = -1)
    {
        if (limit < 0)
            limit = packet.Length;

        var name = new StringBuilder();
        int position = offset;
        int end = -1;
        int jumps = 0;
        int encodedLength = 0;

        while (true)
        {
            if (position >= limit)
                throw new DnsFormatException("truncated DNS name");

            int length = packet[position];
            if (length == 0)
            {
                if (end < 0)
                    end = position + 1;
                break;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= limit)
                    throw new DnsFormatException("truncated DNS compression pointer");
                if (++jumps > MaxPointerJumps)
                    throw new DnsFormatException("DNS compression pointer loop");
                if (end < 0)
                    end = position + 2;
                int target = ((length & 0x3F) << 8) | packet[position + 1];
                if (target >= packet.Length)
                    throw new DnsFormatException("DNS compression pointer out of range");
                position = target;
                // Once we have jumped, the RDATA bound no longer applies.
                limit = packet.Length;
                continue;
            }

            if ((length & 0xC0) != 0)
                throw new DnsFormatException("invalid DNS label type");
            if (position + 1 + length > limit)
                throw new DnsFormatException("truncated DNS label");

            encodedLength += length + 1;
            if (encodedLength > MaxNameLength)
                throw new DnsFormatException("DNS name too long");

            if (name.Length > 0)
                name.Append('.');
            name.Append(Encoding.UTF8.GetString(packet.Slice(position + 1, length)));
            position += 1 + length;
        }

        offset = end;
        return name.ToString();
    }
}
