using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Servicedesk.Domain.Tickets;

/// Canonical human-facing ticket reference, e.g. <c>Ticket#1234</c>. The prefix
/// is admin-configurable (<c>Tickets.ReferencePrefix</c>); the factory default
/// is <see cref="DefaultPrefix"/>. This is the single source of truth for both
/// formatting (copy button, mail subjects, template variable) and parsing
/// (global search, ticket picker, inbound mail subject threading) so the two
/// sides can never drift.
///
/// Parsing is deliberately tolerant — surrounding brackets, a bare leading
/// <c>#</c>, a space after the prefix word, and any letter-casing all resolve —
/// yet anchored to the WHOLE term (see <see cref="TryParseDigits"/>) so a
/// free-text query such as "printer" is never mistaken for a reference. The
/// looser <see cref="FindNumberInText"/> is used only where a reference is
/// embedded in a larger string (an email subject); it requires the <c>#</c> to
/// be present so a stray "Order 1234" subject does not match.
public static class TicketReference
{
    public const string DefaultPrefix = "Ticket#";

    /// Formats a ticket number with the configured prefix, e.g. "Ticket#1234".
    public static string Format(long number, string? prefix)
        => (string.IsNullOrEmpty(prefix) ? DefaultPrefix : prefix)
           + number.ToString(CultureInfo.InvariantCulture);

    /// When <paramref name="input"/> is, in its entirety, a ticket reference,
    /// returns the bare digit string (leading zeros preserved); otherwise null.
    /// Whole-string anchored — free text returns null.
    public static string? TryParseDigits(string? input, string? prefix)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var m = AnchoredRegex(prefix).Match(input.Trim());
        return m.Success ? m.Groups["n"].Value : null;
    }

    /// Whole-string parse to a numeric ticket number. False for free text.
    public static bool TryParseNumber(string? input, string? prefix, out long number)
    {
        number = 0;
        var digits = TryParseDigits(input, prefix);
        return digits is not null
            && long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    /// If the term is a whole ticket reference, returns the bare digits so a SQL
    /// caller can feed it into both an FTS param and a <c>number::text =</c>
    /// param without caring which form the user pasted; otherwise returns the
    /// original term unchanged.
    public static string NormalizeSearchTerm(string? input, string? prefix)
        => TryParseDigits(input, prefix) ?? input ?? string.Empty;

    /// Finds the FIRST ticket reference embedded anywhere in a larger string
    /// (typically an email subject like "Re: Printer broken [Ticket#1234]").
    /// Requires a literal <c>#</c> so plain numbers in the subject are ignored.
    public static bool FindNumberInText(string? text, string? prefix, out long number, out string digits)
    {
        number = 0;
        digits = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = EmbeddedRegex(prefix).Match(text);
        if (!m.Success) return false;
        digits = m.Groups["n"].Value;
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    // ── regex construction (cached per prefix; the prefix changes rarely) ──

    private static readonly Regex s_leadingLetters = new("^[A-Za-z]+", RegexOptions.Compiled);
    private static readonly ConcurrentDictionary<string, Regex> s_anchored = new();
    private static readonly ConcurrentDictionary<string, Regex> s_embedded = new();

    private static string PrefixWord(string? prefix)
    {
        // The alphabetic lead of the prefix ("Ticket" from "Ticket#"). When the
        // prefix has no letters (e.g. "#"), there is no word part to strip.
        if (string.IsNullOrEmpty(prefix)) return "Ticket";
        var m = s_leadingLetters.Match(prefix);
        return m.Success ? m.Value : string.Empty;
    }

    private static Regex AnchoredRegex(string? prefix)
        => s_anchored.GetOrAdd(prefix ?? string.Empty, static p =>
        {
            var word = PrefixWord(p);
            var wordPart = word.Length == 0 ? string.Empty : $"(?:{Regex.Escape(word)}\\s*)?";
            // [Ticket#1234] / Ticket#1234 / Ticket 1234 / #1234 / 1234
            var pattern = $@"^\s*\[?\s*{wordPart}#?\s*(?<n>\d+)\s*\]?\s*$";
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        });

    private static Regex EmbeddedRegex(string? prefix)
        => s_embedded.GetOrAdd(prefix ?? string.Empty, static p =>
        {
            var word = PrefixWord(p);
            var wordPart = word.Length == 0 ? string.Empty : $"(?:{Regex.Escape(word)}\\s*)?";
            // The '#' is mandatory here so a bare number in a subject line
            // ("Invoice 1234") is never treated as a ticket reference.
            var pattern = $@"{wordPart}#\s*(?<n>\d+)";
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        });
}
