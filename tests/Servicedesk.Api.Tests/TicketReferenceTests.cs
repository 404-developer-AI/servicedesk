using Servicedesk.Domain.Tickets;
using Xunit;

namespace Servicedesk.Api.Tests;

public class TicketReferenceTests
{
    [Theory]
    [InlineData("Ticket#", 1234, "Ticket#1234")]
    [InlineData("CASE-", 42, "CASE-42")]
    [InlineData("", 7, "Ticket#7")]
    [InlineData(null, 7, "Ticket#7")]
    public void Format_uses_configured_prefix(string? prefix, long number, string expected)
    {
        Assert.Equal(expected, TicketReference.Format(number, prefix));
    }

    [Theory]
    [InlineData("Ticket#1234", "1234")]
    [InlineData("ticket#1234", "1234")]   // case-insensitive
    [InlineData("Ticket #1234", "1234")]  // tolerated space after the word
    [InlineData("#1234", "1234")]         // bare hash
    [InlineData("1234", "1234")]          // bare digits
    [InlineData("[Ticket#1234]", "1234")] // bracketed (subject-style)
    [InlineData("  Ticket#1234  ", "1234")]
    [InlineData("Ticket#00042", "00042")] // leading zeros preserved
    public void TryParseDigits_accepts_reference_forms(string input, string expectedDigits)
    {
        Assert.Equal(expectedDigits, TicketReference.TryParseDigits(input, "Ticket#"));
    }

    [Theory]
    [InlineData("printer")]
    [InlineData("Ticket#")]      // no number
    [InlineData("abc123")]       // alphabetic lead that isn't the prefix word
    [InlineData("1234 broken")]  // trailing free text — not a whole-string ref
    [InlineData("")]
    [InlineData(null)]
    public void TryParseDigits_rejects_free_text(string? input)
    {
        Assert.Null(TicketReference.TryParseDigits(input, "Ticket#"));
    }

    [Fact]
    public void NormalizeSearchTerm_strips_reference_but_passes_free_text()
    {
        Assert.Equal("1234", TicketReference.NormalizeSearchTerm("Ticket#1234", "Ticket#"));
        Assert.Equal("printer jam", TicketReference.NormalizeSearchTerm("printer jam", "Ticket#"));
    }

    [Theory]
    [InlineData("Re: Printer broken [Ticket#1234]", "1234")]
    [InlineData("RE: account locked (Ticket#42)", "42")]
    [InlineData("re: ticket #77 follow-up", "77")]
    // Our tag wins over a foreign hash number that comes first in the subject.
    [InlineData("Re: Fout: #1100 ElevateDB connectiefout [Ticket#5321]", "5321")]
    public void FindNumberInText_extracts_embedded_reference(string subject, string expectedDigits)
    {
        Assert.True(TicketReference.FindNumberInText(subject, "Ticket#", out _, out var digits));
        Assert.Equal(expectedDigits, digits);
    }

    [Theory]
    [InlineData("Invoice 1234 overdue")] // bare number in a subject must NOT match
    [InlineData("No reference here")]
    // A bare hash number is someone else's reference, never ours (v0.1.16:
    // a supplier's error code "#1100" hijacked ticket 1100 of another customer).
    [InlineData("Re: Fout: #1100 ElevateDB connectiefout")]
    [InlineData("Fwd: [#9001] still failing")]
    [InlineData("Issue #12 on the printer")]
    [InlineData("MyTicket#1234")]        // prefix word glued onto a longer word
    public void FindNumberInText_requires_our_prefix(string subject)
    {
        Assert.False(TicketReference.FindNumberInText(subject, "Ticket#", out _, out _));
    }

    [Fact]
    public void FindNumberInText_follows_a_renamed_prefix_and_keeps_the_default()
    {
        Assert.True(TicketReference.FindNumberInText("Re: [CASE-55] broken", "CASE-", out var n, out _));
        Assert.Equal(55, n);
        // Tags sent before the rename, and migrated Zammad threads, keep threading.
        Assert.True(TicketReference.FindNumberInText("Re: [Ticket#56]", "CASE-", out n, out _));
        Assert.Equal(56, n);
        Assert.False(TicketReference.FindNumberInText("Re: Error #57", "CASE-", out _, out _));
    }

    [Fact]
    public void FindNumberInText_with_a_letterless_prefix_needs_the_bracketed_tag()
    {
        Assert.True(TicketReference.FindNumberInText("Re: printer [#88]", "#", out var n, out _));
        Assert.Equal(88, n);
        Assert.False(TicketReference.FindNumberInText("Re: Error #88", "#", out _, out _));
    }

    [Fact]
    public void Parsing_follows_a_renamed_prefix()
    {
        // Admin changed the prefix to "CASE#": the new form parses, and the
        // legacy "#1234" / bare number still resolve so nobody is stranded.
        Assert.Equal("55", TicketReference.TryParseDigits("CASE#55", "CASE#"));
        Assert.Equal("55", TicketReference.TryParseDigits("#55", "CASE#"));
        Assert.Equal("55", TicketReference.TryParseDigits("55", "CASE#"));
    }
}
