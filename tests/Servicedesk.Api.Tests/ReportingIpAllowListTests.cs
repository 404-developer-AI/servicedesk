using System.Net;
using Servicedesk.Api.Reporting;
using Xunit;

namespace Servicedesk.Api.Tests;

/// The IP allow-list narrows the Reporting API beyond its pre-shared key.
/// These tests pin the matcher's fail-closed semantics: an empty list means
/// no restriction, but once entries exist only a positive match passes —
/// unparseable entries, a missing caller address, and family mismatches all
/// deny.
public sealed class ReportingIpAllowListTests
{
    private static IPAddress Ip(string s) => IPAddress.Parse(s);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t\n  ")]
    public void Empty_list_allows_everyone(string? list)
    {
        Assert.True(ReportingIpAllowList.IsAllowed(list, Ip("203.0.113.10")));
        Assert.True(ReportingIpAllowList.IsAllowed(list, null));
    }

    [Fact]
    public void Nonempty_list_denies_missing_caller_address()
    {
        Assert.False(ReportingIpAllowList.IsAllowed("203.0.113.10", null));
    }

    [Fact]
    public void Plain_ipv4_matches_exactly()
    {
        Assert.True(ReportingIpAllowList.IsAllowed("203.0.113.10", Ip("203.0.113.10")));
        Assert.False(ReportingIpAllowList.IsAllowed("203.0.113.10", Ip("203.0.113.11")));
    }

    [Fact]
    public void Multiple_entries_with_mixed_separators_all_work()
    {
        const string list = "203.0.113.10, 198.51.100.7;\n192.0.2.0/24";
        Assert.True(ReportingIpAllowList.IsAllowed(list, Ip("198.51.100.7")));
        Assert.True(ReportingIpAllowList.IsAllowed(list, Ip("192.0.2.200")));
        Assert.False(ReportingIpAllowList.IsAllowed(list, Ip("198.51.100.8")));
    }

    [Theory]
    [InlineData("198.51.100.0/24", "198.51.100.1", true)]
    [InlineData("198.51.100.0/24", "198.51.100.255", true)]
    [InlineData("198.51.100.0/24", "198.51.101.1", false)]
    [InlineData("198.51.100.16/28", "198.51.100.30", true)]
    [InlineData("198.51.100.16/28", "198.51.100.32", false)]
    [InlineData("0.0.0.0/0", "8.8.8.8", true)]
    public void Ipv4_cidr_matches_range(string list, string caller, bool expected)
    {
        Assert.Equal(expected, ReportingIpAllowList.IsAllowed(list, Ip(caller)));
    }

    [Fact]
    public void Cidr_base_with_host_bits_set_still_matches_its_range()
    {
        // Admin typo'd the network address; the range must still work
        // instead of silently matching nothing.
        Assert.True(ReportingIpAllowList.IsAllowed("192.168.1.5/24", Ip("192.168.1.200")));
        Assert.False(ReportingIpAllowList.IsAllowed("192.168.1.5/24", Ip("192.168.2.5")));
    }

    [Theory]
    [InlineData("2001:db8::/32", "2001:db8::1", true)]
    [InlineData("2001:db8::/32", "2001:db9::1", false)]
    [InlineData("2001:db8::5", "2001:db8::5", true)]
    [InlineData("2001:db8::5", "2001:db8::6", false)]
    public void Ipv6_entries_match(string list, string caller, bool expected)
    {
        Assert.Equal(expected, ReportingIpAllowList.IsAllowed(list, Ip(caller)));
    }

    [Fact]
    public void Ipv4_mapped_ipv6_caller_matches_plain_ipv4_entry()
    {
        Assert.True(ReportingIpAllowList.IsAllowed("203.0.113.10", Ip("::ffff:203.0.113.10")));
        Assert.True(ReportingIpAllowList.IsAllowed("203.0.113.0/24", Ip("::ffff:203.0.113.99")));
    }

    [Fact]
    public void Ipv4_entry_never_matches_real_ipv6_caller()
    {
        Assert.False(ReportingIpAllowList.IsAllowed("203.0.113.0/24", Ip("2001:db8::1")));
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("203.0.113.0/33")]
    [InlineData("203.0.113.0/-1")]
    [InlineData("203.0.113.0/abc")]
    [InlineData("2001:db8::/129")]
    public void Invalid_entries_never_match(string list)
    {
        Assert.False(ReportingIpAllowList.IsAllowed(list, Ip("203.0.113.10")));
        Assert.False(ReportingIpAllowList.IsAllowed(list, Ip("2001:db8::1")));
    }

    [Fact]
    public void Invalid_entry_does_not_break_valid_siblings()
    {
        const string list = "garbage, 203.0.113.10";
        Assert.True(ReportingIpAllowList.IsAllowed(list, Ip("203.0.113.10")));
        Assert.False(ReportingIpAllowList.IsAllowed(list, Ip("203.0.113.11")));
    }
}
