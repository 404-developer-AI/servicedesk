using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Integrations.Adsolut;
using Servicedesk.Infrastructure.Realtime;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Integrations;

/// HTTP surface for the Adsolut OAuth integration. The /authorize and
/// management endpoints are admin-gated; /callback is intentionally
/// anonymous because the cross-site redirect from Wolters Kluwer drops
/// the SameSite=Strict session cookie, so trust on /callback is carried
/// by the encrypted intent cookie instead.
public static partial class AdsolutEndpoints
{
    private const string IntentCookieName = "sd_adsolut_intent";
    private const int IntentCookieLifetimeMinutes = 10;
    private const string DataProtectionPurpose = "Servicedesk.Integrations.Adsolut.Intent.v1";
    private const string CallbackPath = "/api/admin/integrations/adsolut/callback";
    private const string SpaReturnPath = "/settings/integrations/adsolut";

    public static IEndpointRouteBuilder MapAdsolutEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin/integrations/adsolut")
            .WithTags("Integrations")
            .RequireAuthorization(AuthorizationPolicies.RequireAdmin);

        MapAdsolutConnectionRoutes(admin);
        MapAdsolutSyncRoutes(admin);
        MapAdsolutCoverageRoutes(admin);
        MapAdsolutDebugRoutes(admin);
        MapAdsolutErpRoutes(admin);

        // Anonymous — see header comment. Validation happens via the
        // tamper-evident intent cookie + the upstream code exchange.
        app.MapGet(CallbackPath, HandleCallback)
            .WithName("AdsolutCallback")
            .WithTags("Integrations")
            .AllowAnonymous()
            .RequireRateLimiting("auth");

        return app;
    }
}
