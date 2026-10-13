using System.Security.Claims;
using Servicedesk.Infrastructure.Auth;

namespace Servicedesk.Api.Auth;

/// The per-user feature flags that guard an API surface. Each maps to one
/// `users` column read through <see cref="IUserService"/>.
public enum UserFeature
{
    Assets,
    AdsolutOrders,
    AdsolutTimesheet,
    TimesheetBackoffice,
    Timesheet,
}

/// A per-user feature flag is an access boundary, not UI visibility: the
/// API behind a flagged feature answers 403 when the caller's flag is off,
/// whatever the role. Layered on top of the role policy (same shape as
/// <c>KbAccessFilter</c> and the Contracts checks). Admins are not exempt —
/// they can switch their own flag on.
///
/// One indexed primary-key read per request, uncached, so revoking a flag
/// takes effect on the very next call.
public static class FeatureFlagFilter
{
    public static TBuilder RequireFeature<TBuilder>(this TBuilder builder, UserFeature feature)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var idClaim = http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(idClaim, out var userId))
                return Results.Unauthorized();

            var users = http.RequestServices.GetRequiredService<IUserService>();
            if (!await IsEnabledAsync(users, userId, feature, http.RequestAborted))
                return Results.Forbid();

            return await next(context);
        });
        return builder;
    }

    public static async Task<bool> IsEnabledAsync(
        IUserService users, Guid userId, UserFeature feature, CancellationToken ct) => feature switch
    {
        UserFeature.Assets => await users.GetAssetsEnabledAsync(userId, ct),
        UserFeature.AdsolutOrders => await users.GetAdsolutOrdersEnabledAsync(userId, ct),
        UserFeature.AdsolutTimesheet => await users.GetAdsolutTimesheetEnabledAsync(userId, ct),
        UserFeature.TimesheetBackoffice => await users.GetTimesheetBackofficeEnabledAsync(userId, ct),
        UserFeature.Timesheet => (await users.GetTimesheetFlagsAsync(userId, ct)).Enabled,
        _ => false,
    };
}
