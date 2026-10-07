using PhoneNumbers;

namespace Servicedesk.Infrastructure.Phones;

/// Static phone-number normalisation around libphonenumber-csharp. Returns
/// the canonical E.164 form ("+32498123456") or an empty string when the
/// input can't be parsed as a valid number in the given default region.
/// Stateless — the underlying PhoneNumberUtil instance is a singleton
/// inside libphonenumber and thread-safe.
public static class PhoneE164
{
    private static readonly PhoneNumberUtil Util = PhoneNumberUtil.GetInstance();

    /// Parses <paramref name="raw"/> as a phone number, applying
    /// <paramref name="defaultRegion"/> (ISO-3166 alpha-2) when the input
    /// has no leading "+". Returns true on a valid number; sets
    /// <paramref name="e164"/> to its canonical E.164 form. Returns false
    /// and an empty <paramref name="e164"/> on null/empty input or any
    /// parsing/validation failure — callers treat that as "no normalised
    /// number" and store an empty string in <c>phone_e164</c>, which the
    /// partial indices skip.
    public static bool TryNormalize(string? raw, string defaultRegion, out string e164)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            e164 = string.Empty;
            return false;
        }

        // v0.1.26 — cheap pre-check before libphonenumber, which signals
        // "not a number" by throwing: withheld caller ids ("anonymous",
        // "unknown") and stray text are common on the call path and each
        // one cost a first-chance exception. Fewer than 3 or more than 17
        // digits can never be a valid number.
        var digits = 0;
        foreach (var ch in raw) if (char.IsAsciiDigit(ch)) digits++;
        if (digits < 3 || digits > 17)
        {
            e164 = string.Empty;
            return false;
        }

        try
        {
            var number = Util.Parse(raw, defaultRegion);
            if (!Util.IsValidNumber(number))
            {
                e164 = string.Empty;
                return false;
            }
            e164 = Util.Format(number, PhoneNumberFormat.E164);
            return true;
        }
        catch (NumberParseException)
        {
            e164 = string.Empty;
            return false;
        }
    }

    /// Convenience overload that returns the normalised E.164 string or
    /// empty. Mirrors the pattern callers want at write-paths: "give me
    /// the string to put in phone_e164".
    public static string Normalize(string? raw, string defaultRegion) =>
        TryNormalize(raw, defaultRegion, out var e164) ? e164 : string.Empty;
}
