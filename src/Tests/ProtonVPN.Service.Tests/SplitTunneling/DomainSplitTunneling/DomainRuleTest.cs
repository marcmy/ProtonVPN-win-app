#nullable enable

using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ProtonVPN.Service.SplitTunneling.DomainSplitTunneling;

namespace ProtonVPN.Service.Tests.SplitTunneling.DomainSplitTunneling;

[TestClass]
public class DomainRuleTest
{
    [TestMethod]
    [DataRow(" Example.COM. ", "example.com")]
    [DataRow("*.Example.COM", "example.com")]
    public void TryCreate_ShouldNormalizeSupportedRules(string value, string expected)
    {
        bool result = DomainRule.TryCreate(value, out DomainRule? rule);

        result.Should().BeTrue();
        rule!.Domain.Should().Be(expected);
    }

    [TestMethod]
    [DataRow("https://example.com")]
    [DataRow("example.com/path")]
    [DataRow("exa*mple.com")]
    [DataRow("*example.com")]
    [DataRow("")]
    [DataRow(" ")]
    public void TryCreate_ShouldRejectInvalidRules(string value)
    {
        DomainRule.TryCreate(value, out _).Should().BeFalse();
    }

    [TestMethod]
    [DataRow("example.com")]
    [DataRow("www.example.com")]
    [DataRow("login.example.com")]
    [DataRow("api.foo.example.com")]
    [DataRow("EXAMPLE.COM.")]
    public void IsMatch_ShouldMatchApexAndSuffixBoundarySubdomains(string hostname)
    {
        DomainRule.TryCreate("example.com", out DomainRule? rule).Should().BeTrue();

        rule!.IsMatch(hostname).Should().BeTrue();
    }

    [TestMethod]
    [DataRow("badexample.com")]
    [DataRow("example.com.bad")]
    [DataRow("another.com")]
    [DataRow("")]
    public void IsMatch_ShouldRejectNonBoundaryMatches(string hostname)
    {
        DomainRule.TryCreate("example.com", out DomainRule? rule).Should().BeTrue();

        rule!.IsMatch(hostname).Should().BeFalse();
    }
}
#nullable enable
