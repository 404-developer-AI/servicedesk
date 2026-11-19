using Servicedesk.Infrastructure.Tickets.Following;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.58 — the personal note on a follow is plain text: trimmed, line
/// breaks kept (CRLF folded to LF), other control characters dropped, and
/// an empty note means "no note".
public sealed class FollowNoteTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   \n\t ", null)]
    [InlineData("  Waiting on the supplier  ", "Waiting on the supplier")]
    [InlineData("Line one\r\nLine two\rLine three", "Line one\nLine two\nLine three")]
    [InlineData("Bell\u0007 and\u0000 nul", "Bell and nul")]
    [InlineData("<b>not html</b>", "<b>not html</b>")]
    public void Normalizes_to_plain_text(string? raw, string? expected)
        => Assert.Equal(expected, FollowNote.Normalize(raw));
}
