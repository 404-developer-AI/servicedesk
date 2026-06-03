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
    [InlineData("Fwd: [#9001] still failing", "9001")]
    public void FindNumberInText_extracts_embedded_reference(string subject, string expectedDigits)
    {
        Assert.True(TicketReference.FindNumberInText(subject, "Ticket#", out _, out var digits));
        Assert.Equal(expectedDigits, digits);
    }

    [Theory]
    [InlineData("Invoice 1234 overdue")] // bare number in a subject must NOT match
    [InlineData("No reference here")]
    public void FindNumberInText_requires_a_hash(string subject)
    {
        Assert.False(TicketReference.FindNumberInText(subject, "Ticket#", out _, out _));
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
