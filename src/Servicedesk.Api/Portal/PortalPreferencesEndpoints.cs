using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Portal;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Portal;

/// v0.1.15 — the customer's own display preferences, kept on the account so
/// they follow the customer across devices. Display only: nothing here
/// changes what the customer may see. A shadow session reads the customer's
/// choice but can never store one (read-only middleware + own check).
public static class PortalPreferencesEndpoints
{
    public static IEndpointRouteBuilder MapPortalPreferencesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/portal/preferences")
            .WithTags("PortalPreferences")
            .RequireAuthorization(AuthorizationPolicies.RequireCustomer);

        group.MapGet("/conversation-order", GetConversationOrder).WithName("PortalGetConversationOrder").WithOpenApi();
        group.MapPut("/conversation-order", SetConversationOrder).WithName("PortalSetConversationOrder").WithOpenApi();
        return app;
    }

    public sealed record ConversationOrderPreference(string Order, string Source);
    public sealed record UpdateConversationOrderRequest(string? Order);

    private static async Task<Guid?> ActiveViewerIdAsync(HttpContext http, IPortalAccountRepository accounts, ISettingsService settings, CancellationToken ct)
    {
        if (!await PortalRequest.PortalEnabledAsync(settings, ct)) return null;
        var userId = PortalRequest.UserId(http);
        if (userId is null) return null;
        var viewer = await accounts.GetViewerAsync(userId.Value, ct);
        return viewer is { Status: PortalAccountStatus.Active } ? viewer.UserId : null;
    }

    private static async Task<IResult> GetConversationOrder(
        HttpContext http, IPortalAccountRepository accounts, ISettingsService settings, CancellationToken ct)
    {
        var userId = await ActiveViewerIdAsync(http, accounts, settings, ct);
        if (userId is null) return PortalRequest.Disabled();

        var own = PortalConversationOrder.Normalize(await accounts.GetConversationOrderAsync(userId.Value, ct));
        if (own is not null) return Results.Ok(new ConversationOrderPreference(own, "user"));

        var fallback = PortalConversationOrder.Resolve(null, await settings.GetAsync<string>(SettingKeys.Portal.ConversationOrder, ct));
        return Results.Ok(new ConversationOrderPreference(fallback, "default"));
    }

    private static async Task<IResult> SetConversationOrder(
        [FromBody] UpdateConversationOrderRequest req, HttpContext http,
        IPortalAccountRepository accounts, ISettingsService settings, CancellationToken ct)
    {
        if (PortalRequest.IsImpersonated(http)) return PortalRequest.ReadOnly();
        var userId = await ActiveViewerIdAsync(http, accounts, settings, ct);
        if (userId is null) return PortalRequest.Disabled();

        var order = PortalConversationOrder.Normalize(req.Order);
        if (order is null) return Results.BadRequest(new { error = "Order must be 'oldest' or 'newest'." });

        await accounts.SetConversationOrderAsync(userId.Value, order, ct);
        return Results.Ok(new ConversationOrderPreference(order, "user"));
    }
}
