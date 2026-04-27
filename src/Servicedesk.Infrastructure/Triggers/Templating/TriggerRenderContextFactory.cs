using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Servicedesk.Domain.Tickets;
using Servicedesk.Infrastructure.Auth;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Persistence.Taxonomy;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Triggers.Templating;

/// Builds the per-pass <see cref="TriggerRenderContext"/> snapshot:
/// resolves queue/priority/status names from <see cref="ITaxonomyRepository"/>,
/// the requester contact + company from <see cref="ICompanyRepository"/>, the
/// owner agent from <see cref="IUserService"/>, and reads
/// <see cref="ISettingsService"/> for the <c>config.app.*</c> namespace. The
/// trigger's locale + timezone overrides land in
/// <see cref="TriggerRenderContext.Culture"/> and
/// <see cref="TriggerRenderContext.DefaultTimeZoneId"/>; bad values are logged
/// and the renderer is given a safe fallback so a typoed locale never crashes
/// a trigger pass mid-mail.
public sealed class TriggerRenderContextFactory : ITriggerRenderContextFactory
{
    private readonly ITaxonomyRepository _taxonomy;
    private readonly ICompanyRepository _companies;
    private readonly IUserService _users;
    private readonly ISettingsService _settings;
    private readonly ILogger<TriggerRenderContextFactory> _logger;

    public TriggerRenderContextFactory(
        ITaxonomyRepository taxonomy,
        ICompanyRepository companies,
        IUserService users,
        ISettingsService settings,
        ILogger<TriggerRenderContextFactory> logger)
    {
        _taxonomy = taxonomy;
        _companies = companies;
        _users = users;
        _settings = settings;
        _logger = logger;
    }

    public async Task<TriggerRenderContext> BuildAsync(
        TriggerEvaluationContext ctx,
        string? triggerLocale,
        string? triggerTimeZone,
        CancellationToken ct)
    {
        var ticket = ctx.Ticket;

        var queueTask = _taxonomy.GetQueueAsync(ticket.QueueId, ct);
        var priorityTask = _taxonomy.GetPriorityAsync(ticket.PriorityId, ct);
        var statusTask = _taxonomy.GetStatusAsync(ticket.StatusId, ct);
        var contactTask = _companies.GetContactAsync(ticket.RequesterContactId, ct);
        var companyTask = ticket.CompanyId.HasValue
            ? _companies.GetCompanyAsync(ticket.CompanyId.Value, ct)
            : Task.FromResult<Domain.Companies.Company?>(null);
        var ownerTask = ticket.AssigneeUserId.HasValue
            ? _users.FindByIdAsync(ticket.AssigneeUserId.Value, ct)
            : Task.FromResult<ApplicationUser?>(null);
        var publicBaseUrlTask = _settings.GetAsync<string>(SettingKeys.App.PublicBaseUrl, ct);

        await Task.WhenAll(queueTask, priorityTask, statusTask, contactTask, companyTask, ownerTask, publicBaseUrlTask);

        var queue = await queueTask;
        var priority = await priorityTask;
        var status = await statusTask;
        var contact = await contactTask;
        var company = await companyTask;
        var owner = await ownerTask;
        var publicBaseUrl = (await publicBaseUrlTask)?.TrimEnd('/') ?? string.Empty;

        var (articleFrom, articleSubject) = ParseArticleMetadata(ctx.TriggeringEvent);

        var s = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ticket.number"] = ticket.Number.ToString(CultureInfo.InvariantCulture),
            ["ticket.subject"] = ticket.Subject,
            ["ticket.url"] = string.IsNullOrEmpty(publicBaseUrl)
                ? $"/tickets/{ticket.Number}"
                : $"{publicBaseUrl}/tickets/{ticket.Number}",

            ["ticket.queue.name"] = queue?.Name,
            ["ticket.priority.name"] = priority?.Name,
            ["ticket.status.name"] = status?.Name,
            ["ticket.owner.email"] = owner?.Email,

            ["ticket.customer.firstname"] = contact?.FirstName,
            ["ticket.customer.lastname"] = contact?.LastName,
            ["ticket.customer.email"] = contact?.Email,
            ["ticket.company.name"] = company?.Name,

            ["article.body_text"] = ctx.TriggeringEvent?.BodyText,
            ["article.from_email"] = articleFrom,
            ["article.subject"] = articleSubject ?? ticket.Subject,

            ["config.app.name"] = "Servicedesk",
            ["config.app.public_base_url"] = publicBaseUrl,
        };

        var dt = new Dictionary<string, DateTime?>(StringComparer.Ordinal)
        {
            ["now"] = ctx.UtcNow,
            ["ticket.created_utc"] = ticket.CreatedUtc,
            ["ticket.updated_utc"] = ticket.UpdatedUtc,
            ["ticket.due_utc"] = ticket.DueUtc,
            ["ticket.first_response_utc"] = ticket.FirstResponseUtc,
            ["ticket.resolved_utc"] = ticket.ResolvedUtc,
            ["ticket.closed_utc"] = ticket.ClosedUtc,
            ["article.created_utc"] = ctx.TriggeringEvent?.CreatedUtc,
        };

        var culture = ResolveCulture(triggerLocale);
        var timezone = string.IsNullOrWhiteSpace(triggerTimeZone) ? null : triggerTimeZone;

        return new TriggerRenderContext
        {
            StringValues = s,
            DateTimeValues = dt,
            DefaultTimeZoneId = timezone,
            Culture = culture,
        };
    }

    private CultureInfo ResolveCulture(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return CultureInfo.InvariantCulture;
        try
        {
            return CultureInfo.GetCultureInfo(locale);
        }
        catch (CultureNotFoundException)
        {
            _logger.LogWarning("Trigger locale '{Locale}' is not a recognised culture; falling back to invariant.", locale);
            return CultureInfo.InvariantCulture;
        }
    }

    private static (string? FromEmail, string? Subject) ParseArticleMetadata(TicketEvent? evt)
    {
        if (evt is null || string.IsNullOrWhiteSpace(evt.MetadataJson)) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(evt.MetadataJson);
            string? from = null, subject = null;
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.String)
                    from = f.GetString();
                if (doc.RootElement.TryGetProperty("subject", out var s) && s.ValueKind == JsonValueKind.String)
                    subject = s.GetString();
            }
            return (from, subject);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
