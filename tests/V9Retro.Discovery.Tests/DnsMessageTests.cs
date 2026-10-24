using System.Net;

namespace V9Retro.Discovery.Tests;

public class DnsMessageTests
{
    private const string Instance = "WIN98-1a2b3c4d._v9xremote._tcp.local";
    private const string HostName = "v9x-1a2b3c4d.local";

    // tests/host/mdns-announce.hex in v9x-remote-agent: the C encoder's announcement (no compression).
    private const string AgentAnnounceHex =
        "0000840000000005000000000a5f76397872656d6f7465045f746370056c6f63616c00000c00010000119400260e57494e39382d31613262336334640a5f76397872656d6f7465045f746370056c6f63616c00095f7365727669636573075f646e732d7364045f756470056c6f63616c00000c00010000119400170a5f76397872656d6f7465045f746370056c6f63616c000e57494e39382d31613262336334640a5f76397872656d6f7465045f746370056c6f63616c000021800100000078001a00000000268d0c7639782d3161326233633464056c6f63616c000e57494e39382d31613262336334640a5f76397872656d6f7465045f746370056c6f63616c00001080010000119400322769643d31613262336334642d303030302d343030302d383030302d303030303030303030303031097665723d302e382e300c7639782d3161326233633464056c6f63616c0000018001000000780004c0a80132";

    [Fact]
    public void ParsesAgentEncoderFixture()
    {
        DnsMessage message = DnsMessage.Parse(Convert.FromHexString(AgentAnnounceHex));

        Assert.True(message.IsResponse);
        Assert.Equal(5, message.Records.Count);
        Assert.Equal(Instance, message.Records[0].PtrTarget);
        Assert.Equal(MdnsBrowser.ServiceType, message.Records[1].PtrTarget);

        DnsRecord srv = message.Records[2];
        Assert.Equal(DnsRecordType.Srv, srv.Type);
        Assert.True(srv.CacheFlush);
        Assert.Equal(1, srv.Class);
        Assert.Equal(new DnsSrv(0, 0, 9869, HostName), srv.Srv);

        Assert.Equal(["id=1a2b3c4d-0000-4000-8000-000000000001", "ver=0.8.0"], message.Records[3].Txt);
        Assert.Equal(IPAddress.Parse("192.168.1.50"), message.Records[4].Address);
        Assert.Equal(120u, message.Records[4].Ttl);
    }

    [Fact]
    public void ReadsCompressedNamesInOwnersAndRdata()
    {
        byte[] packet = new PacketBuilder(compress: true, id: 0x1234)
            .Ptr(MdnsBrowser.ServiceType, Instance)
            .Srv(Instance, 9869, HostName)
            .Txt(Instance, 4500, "id=1a2b3c4d-0000-4000-8000-000000000001")
            .A(HostName, "10.0.0.7")
            .Build();
        byte[] uncompressed = new PacketBuilder(compress: false)
            .Ptr(MdnsBrowser.ServiceType, Instance)
            .Srv(Instance, 9869, HostName)
            .Txt(Instance, 4500, "id=1a2b3c4d-0000-4000-8000-000000000001")
            .A(HostName, "10.0.0.7")
            .Build();
        Assert.True(packet.Length < uncompressed.Length - 40, "builder should have compressed the names");

        DnsMessage message = DnsMessage.Parse(packet);

        Assert.Equal(0x1234, message.Id);
        Assert.Equal(Instance, message.Records[0].PtrTarget);
        Assert.Equal(Instance, message.Records[1].Name);
        Assert.Equal(HostName, message.Records[1].Srv!.Target);
        Assert.Equal(Instance, message.Records[2].Name);
        Assert.Equal(HostName, message.Records[3].Name);
        Assert.Equal(IPAddress.Parse("10.0.0.7"), message.Records[3].Address);
    }

    [Fact]
    public void SkipsQuestionsBeforeAnswers()
    {
        byte[] query = DnsMessage.BuildQuery(7, MdnsBrowser.ServiceType, DnsRecordType.Ptr);
        DnsMessage message = DnsMessage.Parse(query);

        Assert.False(message.IsResponse);
        Assert.Equal(1, message.QuestionCount);
        Assert.Empty(message.Records);
        Assert.Equal(7, message.Id);
        // QTYPE PTR, QCLASS IN without the QU bit.
        Assert.Equal(new byte[] { 0, 12, 0, 1 }, query[^4..]);
    }

    [Fact]
    public void RejectsPointerLoop()
    {
        byte[] packet = [0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0xC0, 12];
        Assert.False(DnsMessage.TryParse(packet, out _));
    }

    [Theory]
    [InlineData("00")]
    [InlineData("000084000000000100000000")]
    [InlineData("0000840000000001000000000161000001000100000078000401")]
    public void RejectsTruncatedPackets(string hex)
    {
        Assert.Throws<DnsFormatException>(() => DnsMessage.Parse(Convert.FromHexString(hex)));
    }
}
