using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Infrastructure.Triggers.Templating;
using Xunit;

namespace Servicedesk.Api.Tests;

public class TriggerTemplateRendererTests
{
    private static TriggerTemplateRenderer Renderer() =>
        new(NullLogger<TriggerTemplateRenderer>.Instance);

    private static TriggerRenderContext Ctx(
        Dictionary<string, string?>? strings = null,
        Dictionary<string, DateTime?>? dates = null,
        string? timezone = null,
        CultureInfo? culture = null)
    {
        return new TriggerRenderContext
        {
            StringValues = strings ?? new Dictionary<string, string?>(StringComparer.Ordinal),
            DateTimeValues = dates ?? new Dictionary<string, DateTime?>(StringComparer.Ordinal),
            DefaultTimeZoneId = timezone,
            Culture = culture ?? CultureInfo.InvariantCulture,
        };
    }

    [Fact]
    public void Empty_template_returns_empty()
    {
        Assert.Equal(string.Empty, Renderer().Render(string.Empty, TemplateEscapeMode.Html, Ctx()));
    }

    [Fact]
    public void Plain_template_passes_through()
    {
        var result = Renderer().Render("Hello world", TemplateEscapeMode.Html, Ctx());
        Assert.Equal("Hello world", result);
    }

    [Fact]
    public void Substitutes_simple_path()
    {
        var ctx = Ctx(new() { ["ticket.subject"] = "Cannot login" });
        Assert.Equal("Re: Cannot login", Renderer().Render("Re: #{ticket.subject}", TemplateEscapeMode.PlainText, ctx));
    }

    [Fact]
    public void Unknown_path_renders_as_empty()
    {
        var ctx = Ctx(new() { ["ticket.subject"] = "x" });
        Assert.Equal("Hello ", Renderer().Render("Hello #{ticket.unknown}", TemplateEscapeMode.PlainText, ctx));
    }

    [Fact]
    public void Html_mode_escapes_substituted_value()
    {
        var ctx = Ctx(new() { ["ticket.customer.firstname"] = "<script>alert(1)</script>" });
        var rendered = Renderer().Render("Hi #{ticket.customer.firstname}", TemplateEscapeMode.Html, ctx);
        Assert.DoesNotContain("<script>", rendered, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_mode_does_not_escape_template_literal_markup()
    {
        // The surrounding markup the admin authored is part of the body and
        // must pass through. Only the substituted variable values get encoded.
        var ctx = Ctx(new() { ["ticket.subject"] = "Order #42" });
        var rendered = Renderer().Render("<p>Subject: #{ticket.subject}</p>", TemplateEscapeMode.Html, ctx);
        Assert.Equal("<p>Subject: Order #42</p>", rendered);
    }

    [Fact]
    public void PlainText_mode_strips_line_breaks_from_value()
    {
        // Header-injection guard: a newline inside a substituted value would
        // otherwise close the Subject: line and let an attacker forge headers.
        var ctx = Ctx(new() { ["ticket.subject"] = "evil\r\nBcc: a@b.c" });
        var rendered = Renderer().Render("Re: #{ticket.subject}", TemplateEscapeMode.PlainText, ctx);
        Assert.DoesNotContain("\r", rendered);
        Assert.DoesNotContain("\n", rendered);
        Assert.Contains("Bcc:", rendered);
    }

    [Fact]
    public void Dt_helper_formats_datetime_with_default_format()
    {
        var when = new DateTime(2026, 4, 27, 12, 30, 0, DateTimeKind.Utc);
        var ctx = Ctx(dates: new() { ["ticket.created_utc"] = when });
        var rendered = Renderer().Render("at #{dt(ticket.created_utc, \"yyyy-MM-dd HH:mm\")}",
            TemplateEscapeMode.PlainText, ctx);
        Assert.Equal("at 2026-04-27 12:30", rendered);
    }

    [Fact]
    public void Dt_helper_converts_to_supplied_timezone()
    {
        // 12:00 UTC on a date with no Brussels DST → 13:00 local (CET).
        var when = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var ctx = Ctx(dates: new() { ["ticket.created_utc"] = when });
        var tz = TryGetTzId("Europe/Brussels", "Romance Standard Time");
        if (tz is null) return; // host has no Europe/Brussels zone — skip silently
        var rendered = Renderer().Render(
            "at #{dt(ticket.created_utc, \"HH:mm\", \"" + tz + "\")}",
            TemplateEscapeMode.PlainText, ctx);
        Assert.Equal("at 13:00", rendered);
    }

    [Fact]
    public void Dt_helper_falls_back_to_utc_for_unknown_timezone()
    {
        var when = new DateTime(2026, 4, 27, 12, 30, 0, DateTimeKind.Utc);
        var ctx = Ctx(dates: new() { ["ticket.created_utc"] = when });
        var rendered = Renderer().Render(
            "at #{dt(ticket.created_utc, \"HH:mm\", \"Mars/Olympus\")}",
            TemplateEscapeMode.PlainText, ctx);
        Assert.Equal("at 12:30", rendered);
    }

    [Fact]
    public void Dt_helper_returns_empty_when_value_is_null()
    {
        var ctx = Ctx(dates: new() { ["ticket.due_utc"] = null });
        var rendered = Renderer().Render("Due: #{dt(ticket.due_utc, \"yyyy-MM-dd\")}",
            TemplateEscapeMode.PlainText, ctx);
        Assert.Equal("Due: ", rendered);
    }

    [Fact]
    public void Dt_helper_returns_empty_for_unknown_path()
    {
        var ctx = Ctx();
        var rendered = Renderer().Render("at #{dt(nonexistent, \"yyyy-MM-dd\")}",
            TemplateEscapeMode.PlainText, ctx);
        Assert.Equal("at ", rendered);
    }

    [Fact]
    public void Datetime_path_used_as_bare_placeholder_renders_empty()
    {
        // ticket.created_utc is a datetime; it must go through dt(...).
        var ctx = Ctx(dates: new() { ["ticket.created_utc"] = DateTime.UtcNow });
        var rendered = Renderer().Render("at #{ticket.created_utc}", TemplateEscapeMode.PlainText, ctx);
        Assert.Equal("at ", rendered);
    }

    [Fact]
    public void Multiple_placeholders_render_in_order()
    {
        var ctx = Ctx(new()
        {
            ["ticket.number"] = "1234",
            ["ticket.subject"] = "VPN down",
        });
        var rendered = Renderer().Render(
            "[#{ticket.number}] #{ticket.subject}",
            TemplateEscapeMode.PlainText, ctx);
        Assert.Equal("[1234] VPN down", rendered);
    }

    [Fact]
    public void Dangling_open_marker_is_preserved_verbatim()
    {
        var rendered = Renderer().Render("oops #{ticket.subject", TemplateEscapeMode.PlainText, Ctx());
        Assert.Equal("oops #{ticket.subject", rendered);
    }

    [Fact]
    public void Whitespace_inside_marker_is_tolerated()
    {
        var ctx = Ctx(new() { ["ticket.number"] = "42" });
        var rendered = Renderer().Render("#{ ticket.number }", TemplateEscapeMode.PlainText, ctx);
        Assert.Equal("42", rendered);
    }

    [Fact]
    public void Default_timezone_from_context_is_used_when_dt_omits_argument()
    {
        var when = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc);
        var tz = TryGetTzId("Europe/Brussels", "Romance Standard Time");
        if (tz is null) return;
        var ctx = Ctx(
            dates: new() { ["ticket.created_utc"] = when },
            timezone: tz);
        var rendered = Renderer().Render(
            "at #{dt(ticket.created_utc, \"HH:mm\")}",
            TemplateEscapeMode.PlainText, ctx);
        Assert.Equal("at 13:00", rendered);
    }

    private static string? TryGetTzId(params string[] candidates)
    {
        foreach (var id in candidates)
        {
            try { TimeZoneInfo.FindSystemTimeZoneById(id); return id; }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return null;
    }
}
