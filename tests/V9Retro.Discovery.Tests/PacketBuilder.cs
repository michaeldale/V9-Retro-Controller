using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace V9Retro.Discovery.Tests;

/// <summary>Hand-builds DNS response packets, optionally with name compression.</summary>
internal sealed class PacketBuilder(bool compress, ushort id = 0)
{
    private readonly List<byte> _body = [];
    private readonly Dictionary<string, int> _suffixes = new(StringComparer.OrdinalIgnoreCase);
    private int _records;

    public PacketBuilder Ptr(string name, string target, uint ttl = 4500) =>
        Record(name, DnsRecordType.Ptr, ttl, false, data => Name(data, target));

    public PacketBuilder Srv(string name, ushort port, string target, uint ttl = 120) =>
        Record(name, DnsRecordType.Srv, ttl, true, data =>
        {
            data.AddRange([0, 0, 0, 0, (byte)(port >> 8), (byte)port]);
            Name(data, target);
        });

    public PacketBuilder Txt(string name, uint ttl, params string[] entries) =>
        Record(name, DnsRecordType.Txt, ttl, true, data =>
        {
            foreach (string entry in entries)
            {
                byte[] raw = Encoding.UTF8.GetBytes(entry);
                data.Add((byte)raw.Length);
                data.AddRange(raw);
            }
        });

    public PacketBuilder A(string name, string address, uint ttl = 120) =>
        Record(name, DnsRecordType.A, ttl, true, data => data.AddRange(IPAddress.Parse(address).GetAddressBytes()));

    public byte[] Build()
    {
        byte[] header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), 0x8400);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)_records);
        return [.. header, .. _body];
    }

    private PacketBuilder Record(string name, DnsRecordType type, uint ttl, bool cacheFlush, Action<List<byte>> writeData)
    {
        Name(_body, name);
        byte[] fixedPart = new byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart, (ushort)type);
        BinaryPrimitives.WriteUInt16BigEndian(fixedPart.AsSpan(2), (ushort)(cacheFlush ? 0x8001 : 0x0001));
        BinaryPrimitives.WriteUInt32BigEndian(fixedPart.AsSpan(4), ttl);
        _body.AddRange(fixedPart);
        int lengthAt = _body.Count - 2;

        // RDATA is written straight into the body so compression offsets stay right.
        int start = _body.Count;
        writeData(_body);
        int length = _body.Count - start;
        _body[lengthAt] = (byte)(length >> 8);
        _body[lengthAt + 1] = (byte)length;
        _records++;
        return this;
    }

    private void Name(List<byte> output, string name)
    {
        if (!ReferenceEquals(output, _body))
            throw new InvalidOperationException("names must be written into the body");

        string[] labels = name.Split('.');
        for (int i = 0; i < labels.Length; i++)
        {
            string suffix = string.Join('.', labels[i..]);
            if (compress && _suffixes.TryGetValue(suffix, out int pointer))
            {
                output.Add((byte)(0xC0 | (pointer >> 8)));
                output.Add((byte)pointer);
                return;
            }
            _suffixes.TryAdd(suffix, 12 + output.Count);
            byte[] raw = Encoding.UTF8.GetBytes(labels[i]);
            output.Add((byte)raw.Length);
            output.AddRange(raw);
        }
        output.Add(0);
    }
}
