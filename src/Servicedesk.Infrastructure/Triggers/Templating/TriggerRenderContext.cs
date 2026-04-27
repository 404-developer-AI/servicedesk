using System.Globalization;

namespace Servicedesk.Infrastructure.Triggers.Templating;

/// Per-pass snapshot of every value the trigger template renderer is allowed
/// to substitute. Built once by <see cref="TriggerRenderContextFactory"/>
/// when a trigger matches, then handed to the renderer for each subject /
/// body / note field. The whitelist is intentionally narrow: every accepted
/// path is listed in <see cref="ITriggerTemplateRenderer"/> docs, and the
/// dictionary lookup means there is no reflection on the snapshot or the
/// underlying domain models.
///
/// Date/time values land in <see cref="DateTimeValues"/> rather than
/// <see cref="StringValues"/> so the <c>dt(path, format, tz)</c> helper can
/// format them in the trigger's locale + timezone instead of dumping the
/// raw <c>DateTime.ToString()</c> shape.
public sealed class TriggerRenderContext
{
    public required IReadOnlyDictionary<string, string?> StringValues { get; init; }
    public required IReadOnlyDictionary<string, DateTime?> DateTimeValues { get; init; }

    /// IANA time-zone id used when <c>dt(...)</c> omits the third argument.
    /// Empty / null falls back to UTC at format time.
    public string? DefaultTimeZoneId { get; init; }

    /// CultureInfo used as <see cref="IFormatProvider"/> for <c>dt(...)</c>.
    /// Always non-null; bad locale strings fall back to the invariant culture
    /// at build time so the renderer never has to defend against it.
    public required CultureInfo Culture { get; init; }
}
