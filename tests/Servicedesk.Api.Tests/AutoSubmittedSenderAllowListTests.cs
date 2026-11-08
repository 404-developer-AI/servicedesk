using Servicedesk.Infrastructure.Mail.Ingest;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class AutoSubmittedSenderAllowListTests
{
    [Theory]
    [InlineData("noreply@example.com", "noreply@example.com", true)]
    [InlineData("NoReply@Example.com", "noreply@example.com", true)]
    [InlineData("@example.com", "anyone@example.com", true)]
    [InlineData("example.com", "anyone@example.com", true)]
    [InlineData("a@x.example; @example.com", "b@example.com", true)]
    [InlineData("a@x.example\nexample.com", "b@example.com", true)]
    [InlineData("noreply@example.com", "other@example.com", false)]
    [InlineData("@example.com", "anyone@sub.example.com", false)]
    [InlineData("example.com", "anyone@example.com.evil", false)]
    [InlineData("example.com", "anyone@notexample.com", false)]
    [InlineData("", "anyone@example.com", false)]
    [InlineData(null, "anyone@example.com", false)]
    [InlineData("@", "anyone@example.com", false)]
    [InlineData("example.com", "not-an-address", false)]
    public void Matches_exact_addresses_and_exact_domains_only(string? list, string from, bool expected)
        => Assert.Equal(expected, AutoSubmittedSenderAllowList.Matches(list, from));
}
