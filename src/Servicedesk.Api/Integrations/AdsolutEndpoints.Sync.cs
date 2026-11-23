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

public static partial class AdsolutEndpoints
{
    private static void MapAdsolutSyncRoutes(RouteGroupBuilder admin)
    {
        // v0.0.26 — Companies pull. The dossier picker (admin chooses
        // which Adsolut administration to sync) and the on-demand
        // "Sync now" + sync-state surface for the UI.
        admin.MapGet("/administrations", ListAdministrations).WithName("ListAdsolutAdministrations");
        admin.MapPost("/administration", SelectAdministration).WithName("SelectAdsolutAdministration");
        admin.MapGet("/sync", GetSyncState).WithName("GetAdsolutSyncState");
        admin.MapPost("/sync", TriggerSyncNow).WithName("TriggerAdsolutSyncNow");

        // v0.0.28 — manual "Resync contacts" admin escape valve. Triggered
        // from the company-detail page when an admin doesn't want to wait
        // for the slow reconcile loop (default 24h). Respects the contacts
        // pull-toggles — flipping them off then clicking resync is a no-op
        // by design.
        admin.MapPost("/companies/{companyId:guid}/resync-contacts", ResyncCompanyContacts)
            .WithName("ResyncAdsolutCompanyContacts");
    }

    // ---- v0.0.26 administrations + sync --------------------------------

    private static async Task<IResult> ListAdministrations(
        IAdsolutAdministrationsClient adminClient,
        IAdsolutConnectionStore connections,
        CancellationToken ct)
    {
        var connection = await connections.GetAsync(ct);
        if (connection is null)
        {
            return Results.BadRequest(new { error = "not_connected" });
        }
        try
        {
            var items = await adminClient.ListAsync(ct);
            return Results.Ok(new
            {
                items = items.Select(a => new
                {
                    id = a.Id,
                    name = a.Name,
                    code = a.Code,
                }),
                selectedId = connection.AdministrationId,
            });
        }
        catch (AdsolutApiException ex)
        {
            return Results.Json(new
            {
                error = "upstream_error",
                httpStatus = ex.HttpStatus,
                upstreamErrorCode = ex.UpstreamErrorCode,
                message = ex.Message,
            }, statusCode: 502);
        }
    }

    private static async Task<IResult> SelectAdministration(
        [FromBody] SelectAdministrationRequest req,
        HttpContext http,
        IAdsolutAdministrationsClient adminClient,
        IAdsolutConnectionStore connections,
        IAdsolutSyncStateStore syncStateStore,
        IAuditLogger audit,
        ISettingsService settings,
        IProtectedSecretStore secrets,
        IIntegrationStatusNotifier statusNotifier,
        CancellationToken ct)
    {
        if (req.AdministrationId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "missing_administration_id" });
        }
        var connection = await connections.GetAsync(ct);
        if (connection is null)
        {
            return Results.BadRequest(new { error = "not_connected" });
        }

        try
        {
            await adminClient.ActivateAsync(req.AdministrationId, ct);
        }
        catch (AdsolutApiException ex)
        {
            return Results.Json(new
            {
                error = "activate_failed",
                httpStatus = ex.HttpStatus,
                upstreamErrorCode = ex.UpstreamErrorCode,
                message = ex.Message,
            }, statusCode: 502);
        }

        await connections.SaveAsync(connection with { AdministrationId = req.AdministrationId }, ct);

        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: "integration.adsolut.administration.selected",
            Actor: actor,
            ActorRole: role,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString(),
            Payload: new { administrationId = req.AdministrationId }), ct);
        await statusNotifier.NotifyStatusChangedAsync(
            AdsolutEventTypes.Integration,
            await AdsolutStateResolver.ComputeAsync(settings, secrets, connections, syncStateStore, ct),
            ct);
        return Results.Ok(new { administrationId = req.AdministrationId });
    }

    private static async Task<IResult> GetSyncState(
        IAdsolutSyncStateStore syncState,
        ISettingsService settings,
        CancellationToken ct)
    {
        var state = await syncState.GetAsync(ct);
        var intervalMinutes = await settings.GetAsync<int>(SettingKeys.Adsolut.SyncIntervalMinutes, ct);
        if (intervalMinutes <= 0) intervalMinutes = 5;
        var nextSyncUtc = Servicedesk.Infrastructure.Health.IntegrationsHealthAggregator
            .ComputeNextSyncUtc(state?.LastDeltaSyncUtc, intervalMinutes);

        return Results.Ok(state is null
            ? new
            {
                lastFullSyncUtc = (DateTime?)null,
                lastDeltaSyncUtc = (DateTime?)null,
                lastError = (string?)null,
                lastErrorUtc = (DateTime?)null,
                companiesSeen = 0,
                companiesUpserted = 0,
                companiesSkippedLoserInConflict = 0,
                updatedUtc = (DateTime?)null,
                nextSyncUtc,
                intervalMinutes,
            }
            : new
            {
                lastFullSyncUtc = state.LastFullSyncUtc,
                lastDeltaSyncUtc = state.LastDeltaSyncUtc,
                lastError = state.LastError,
                lastErrorUtc = state.LastErrorUtc,
                companiesSeen = state.CompaniesSeen,
                companiesUpserted = state.CompaniesUpserted,
                companiesSkippedLoserInConflict = state.CompaniesSkippedLoserInConflict,
                updatedUtc = (DateTime?)state.UpdatedUtc,
                nextSyncUtc = (DateTime?)nextSyncUtc,
                intervalMinutes,
            });
    }

    private static async Task<IResult> TriggerSyncNow(
        HttpContext http,
        IAdsolutSyncWorkerSignal signal,
        IAuditLogger audit,
        CancellationToken ct)
    {
        signal.RequestImmediateRun();
        var (actor, role) = ActorContext.Resolve(http);
        await audit.LogAsync(new AuditEvent(
            EventType: "integration.adsolut.sync.requested",
            Actor: actor,
            ActorRole: role,
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString()), ct);
        return Results.Accepted();
    }

    /// v0.0.28 — manual contacts resync for one company. Looks up the
    /// company's <c>adsolut_id</c>, fetches the full contacts list from
    /// the active dossier, runs the upserter + reconcile pass against it.
    /// Returns the same counters shape the reconcile worker writes to its
    /// audit row, plus the company's adsolut id so the UI can show "synced
    /// against UUID xxxx".
    private static async Task<IResult> ResyncCompanyContacts(
        Guid companyId,
        HttpContext http,
        ISettingsService settings,
        IAdsolutContactsReconciler reconciler,
        IIntegrationAuditLogger integrationAudit,
        IAuditLogger audit,
        CancellationToken ct)
    {
        var pullUpdate = await settings.GetAsync<bool>(SettingKeys.Adsolut.SyncPullContactsUpdate, ct);
        var pullCreate = await settings.GetAsync<bool>(SettingKeys.Adsolut.SyncPullContactsCreate, ct);
        var options = new AdsolutContactsSyncOptions(pullUpdate, pullCreate);

        var (actor, role) = ActorContext.Resolve(http);

        // Audit-log the request itself before doing the work, so even a
        // hung/failed run leaves a forensic trail of who clicked.
        await integrationAudit.LogAsync(new IntegrationAuditEvent(
            Integration: AdsolutEventTypes.Integration,
            EventType: AdsolutEventTypes.ContactsResyncRequested,
            Outcome: IntegrationAuditOutcome.Ok,
            ActorId: actor,
            ActorRole: role,
            Payload: new { companyId, pullUpdate, pullCreate }), ct);
        await audit.LogAsync(new AuditEvent(
            EventType: "integration.adsolut.contacts.resync_requested",
            Actor: actor,
            ActorRole: role,
            Target: companyId.ToString(),
            ClientIp: http.Connection.RemoteIpAddress?.ToString(),
            UserAgent: http.Request.Headers.UserAgent.ToString()), ct);

        var result = await reconciler.ReconcileCompanyAsync(companyId, options, ct);
        if (result is null)
        {
            return Results.NotFound(new
            {
                error = "company_not_linked",
                message = "Company is not linked to Adsolut, the integration is not connected, or no dossier is selected.",
            });
        }

        return Results.Ok(new
        {
            companyId,
            pullUpdate,
            pullCreate,
            result.CompaniesScanned,
            result.ContactsSeen,
            result.ContactsCreated,
            result.ContactsUpdated,
            result.ContactsSkippedNoChange,
            result.ContactsSkippedLocalNewer,
            result.ContactsSkippedToggleOff,
            result.ContactsSkippedNoEmail,
            result.ContactsSkippedLinkConflict,
            result.LinksReconciled,
        });
    }

    public sealed record SelectAdministrationRequest([property: Required] Guid AdministrationId);
}
