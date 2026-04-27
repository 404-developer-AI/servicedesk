using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Servicedesk.Infrastructure.Triggers.Templating;

/// Pure string-interpolation renderer. No reflection (the snapshot is a
/// pre-resolved dictionary), no expression evaluator (only path lookup +
/// the <c>dt(...)</c> formatter). The parser is a single forward scan:
/// find <c>#{</c>, find the matching <c>}</c> on the same nesting level
/// (no nesting allowed), classify the body, substitute. Unknown paths
/// yield the empty string and log at Debug — mail templates that silently
/// drop a placeholder are easier to diagnose by reading the rendered
/// output than by parsing a stack trace.
public sealed class TriggerTemplateRenderer : ITriggerTemplateRenderer
{
    private readonly ILogger<TriggerTemplateRenderer> _logger;

    public TriggerTemplateRenderer(ILogger<TriggerTemplateRenderer> logger)
    {
        _logger = logger;
    }

    public string Render(string template, TemplateEscapeMode mode, TriggerRenderContext renderCtx)
    {
        if (string.IsNullOrEmpty(template)) return template ?? string.Empty;

        var sb = new StringBuilder(template.Length + 32);
        var i = 0;
        while (i < template.Length)
        {
            // Look for the next placeholder marker. Anything before it is
            // template literal text and passes through unchanged — only the
            // substituted value gets escaped, not the surrounding markup.
            var start = template.IndexOf("#{", i, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(template, i, template.Length - i);
                break;
            }
            sb.Append(template, i, start - i);

            var end = template.IndexOf('}', start + 2);
            if (end < 0)
            {
                // Dangling `#{` with no closer: leave the rest of the template
                // verbatim. An admin who typed an unclosed marker should see
                // their typo, not a silent truncation.
                sb.Append(template, start, template.Length - start);
                break;
            }

            var inner = template.AsSpan(start + 2, end - start - 2).Trim();
            var resolved = Resolve(inner, renderCtx);
            sb.Append(Escape(resolved, mode));

            i = end + 1;
        }
        return sb.ToString();
    }

    private string Resolve(ReadOnlySpan<char> inner, TriggerRenderContext renderCtx)
    {
        if (inner.IsEmpty) return string.Empty;

        if (inner.StartsWith("dt(") && inner[^1] == ')')
        {
            return ResolveDt(inner[3..^1], renderCtx);
        }

        var path = inner.ToString();
        if (renderCtx.StringValues.TryGetValue(path, out var s))
            return s ?? string.Empty;

        // dt-only paths (datetimes) are not valid bare placeholders — they
        // need formatting context. Silently empty + log so an admin can spot
        // the misuse in the run-history without crashing the trigger.
        if (renderCtx.DateTimeValues.ContainsKey(path))
        {
            _logger.LogDebug("Trigger template path '{Path}' is a datetime; use dt(...) to format.", path);
            return string.Empty;
        }

        _logger.LogDebug("Trigger template path '{Path}' not in whitelist; rendering as empty.", path);
        return string.Empty;
    }

    private string ResolveDt(ReadOnlySpan<char> args, TriggerRenderContext renderCtx)
    {
        // dt(path, "format")  or  dt(path, "format", "tz")
        var parts = SplitTopLevelArgs(args);
        if (parts.Count < 2)
        {
            _logger.LogDebug("Trigger template dt() needs at least 2 args (path, format).");
            return string.Empty;
        }

        var path = parts[0].Trim();
        var format = StripQuotes(parts[1].Trim());
        var tz = parts.Count >= 3 ? StripQuotes(parts[2].Trim()) : renderCtx.DefaultTimeZoneId;

        if (!renderCtx.DateTimeValues.TryGetValue(path, out var dtNullable))
        {
            _logger.LogDebug("Trigger template dt() path '{Path}' not in whitelist.", path);
            return string.Empty;
        }
        if (dtNullable is null) return string.Empty;
        var dt = DateTime.SpecifyKind(dtNullable.Value, DateTimeKind.Utc);

        TimeZoneInfo? zone = null;
        if (!string.IsNullOrWhiteSpace(tz))
        {
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(tz);
            }
            catch (TimeZoneNotFoundException)
            {
                _logger.LogWarning("Trigger template dt() unknown timezone '{Tz}'; falling back to UTC.", tz);
            }
            catch (InvalidTimeZoneException)
            {
                _logger.LogWarning("Trigger template dt() invalid timezone '{Tz}'; falling back to UTC.", tz);
            }
        }
        var local = zone is null ? dt : TimeZoneInfo.ConvertTimeFromUtc(dt, zone);

        if (string.IsNullOrEmpty(format)) format = "yyyy-MM-dd HH:mm";

        try
        {
            return local.ToString(format, renderCtx.Culture);
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "Trigger template dt() bad format string '{Format}'.", format);
            return string.Empty;
        }
    }

    private static List<string> SplitTopLevelArgs(ReadOnlySpan<char> args)
    {
        var result = new List<string>(3);
        var sb = new StringBuilder();
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < args.Length; i++)
        {
            var c = args[i];
            if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == '\'' && !inDouble) inSingle = !inSingle;
            if (c == ',' && !inSingle && !inDouble)
            {
                result.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0 || args.Length == 0 || args[^1] == ',') result.Add(sb.ToString());
        return result;
    }

    private static string StripQuotes(string s)
    {
        if (s.Length >= 2)
        {
            if ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\''))
                return s.Substring(1, s.Length - 2);
        }
        return s;
    }

    private static string Escape(string value, TemplateEscapeMode mode) => mode switch
    {
        TemplateEscapeMode.Html => WebUtility.HtmlEncode(value),
        TemplateEscapeMode.PlainText => StripLineBreaks(value),
        _ => value,
    };

    private static string StripLineBreaks(string value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        if (value.IndexOfAny(new[] { '\r', '\n' }) < 0) return value;
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c == '\r' || c == '\n') sb.Append(' ');
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
