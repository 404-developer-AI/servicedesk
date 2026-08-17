using Servicedesk.Infrastructure.Persistence.Tickets;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.0.101 — the ticket-list / picker search feeds the number arm of its
/// hit-set only when the term is a clean ticket number ("1234" / "#1234"),
/// compared as a typed bigint against the indexed column.
public sealed class TicketSearchNumberParsingTests
{
    [Theory]
    [InlineData("1234", 1234L)]
    [InlineData("#1234", 1234L)]
    [InlineData("  42  ", 42L)]
    [InlineData(" #7 ", 7L)]
    [InlineData("999999999999999999", 999999999999999999L)] // 18 digits, fits bigint
    public void Parses_clean_ticket_numbers(string input, long expected)
        => Assert.Equal(expected, TicketRepository.TryParseTicketNumber(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#")]
    [InlineData("0")]
    [InlineData("123abc")]
    [InlineData("abc123")]
    [InlineData("12 34")]
    [InlineData("-5")]
    [InlineData("+5")]
    [InlineData("1.5")]
    [InlineData("1234567890123456789")] // 19 digits — refused before long.Parse
    [InlineData("printer #1234")]
    public void Rejects_anything_that_is_not_a_bare_number(string? input)
        => Assert.Null(TicketRepository.TryParseTicketNumber(input));
}
