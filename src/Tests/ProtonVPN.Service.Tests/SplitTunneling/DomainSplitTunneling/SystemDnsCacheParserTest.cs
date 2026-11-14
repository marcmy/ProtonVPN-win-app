using System.Linq;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Service.SplitTunneling.DomainSplitTunneling;

namespace ProtonVPN.Service.Tests.SplitTunneling.DomainSplitTunneling;

[TestClass]
public class SystemDnsCacheParserTest
{
    [TestMethod]
    public void Parse_ShouldReadSingleDnsCacheObject()
    {
        const string json = """
            {"Entry":"login.example.com","Data":"203.0.113.10","TimeToLive":240}
            """;

        SystemDnsCacheEntry[] entries = SystemDnsCacheParser.Parse(json).ToArray();

        entries.Should().ContainSingle();
        entries[0].Hostname.Should().Be("login.example.com");
        entries[0].IpAddress.ToString().Should().Be("203.0.113.10");
        entries[0].TimeToLiveSeconds.Should().Be(240);
    }

    [TestMethod]
    public void Parse_ShouldReadArrayAndIgnoreInvalidRows()
    {
        const string json = """
            [
              {"Entry":"api.example.com","Data":"203.0.113.11","TimeToLive":60},
              {"Entry":"ipv6.example.com","Data":"2001:db8::1","TimeToLive":"90"},
              {"Entry":"text.example.com","Data":"not-an-ip","TimeToLive":60},
              {"Entry":"","Data":"203.0.113.12","TimeToLive":60}
            ]
            """;

        SystemDnsCacheEntry[] entries = SystemDnsCacheParser.Parse(json).ToArray();

        entries.Should().HaveCount(2);
        entries.Select(entry => entry.Hostname)
            .Should().BeEquivalentTo("api.example.com", "ipv6.example.com");
        entries.Single(entry => entry.Hostname == "ipv6.example.com")
            .TimeToLiveSeconds.Should().Be(90);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("null")]
    [DataRow("{}")]
    [DataRow("{invalid")]
    public void Parse_ShouldReturnEmptyForNoUsableEntries(string json)
    {
        SystemDnsCacheParser.Parse(json).Should().BeEmpty();
    }
}
