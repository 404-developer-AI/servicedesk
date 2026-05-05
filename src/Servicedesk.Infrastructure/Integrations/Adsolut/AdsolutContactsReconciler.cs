using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Result of a contacts reconcile pass over one or more SD companies.
/// Surfaced verbatim by the worker (audit row payload) and by the manual
/// "Resync contacts" admin endpoint (response body).
public sealed class AdsolutContactsReconcileResult
{
    public int CompaniesScanned { get; set; }
    public int ContactsSeen { get; set; }
    public int ContactsCreated { get; set; }
    public int ContactsUpdated { get; set; }
    public int ContactsSkippedNoChange { get; set; }
    public int ContactsSkippedToggleOff { get; set; }
    public int ContactsSkippedLocalNewer { get; set; }
    public int ContactsSkippedNoEmail { get; set; }
    public int ContactsSkippedLinkConflict { get; set; }
    public int LinksReconciled { get; set; }
}

public interface IAdsolutContactsReconciler
{
    /// Reconciles a single SD company against Adsolut. Returns null when
    /// the company is not Adsolut-linked (no adsolut_id) or the integration
    /// isn't connected to a dossier — both signal "nothing to do" not error.
    Task<AdsolutContactsReconcileResult?> ReconcileCompanyAsync(
        Guid companyId,
        AdsolutContactsSyncOptions options,
        CancellationToken ct = default);

    /// Reconciles every Adsolut-linked SD company. Used by the slow
    /// background loop and skipped silently when both contacts-toggles
    /// are OFF.
    Task<AdsolutContactsReconcileResult> ReconcileAllAsync(
        AdsolutContactsSyncOptions options,
        CancellationToken ct = default);
}

public sealed class AdsolutContactsReconciler : IAdsolutContactsReconciler
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IAdsolutConnectionStore _connections;
    private readonly IAdsolutContactsClient _contactsClient;
    private readonly IAdsolutContactUpserter _upserter;
    private readonly ILogger<AdsolutContactsReconciler> _logger;

    public AdsolutContactsReconciler(
        NpgsqlDataSource dataSource,
        IAdsolutConnectionStore connections,
        IAdsolutContactsClient contactsClient,
        IAdsolutContactUpserter upserter,
        ILogger<AdsolutContactsReconciler> logger)
    {
        _dataSource = dataSource;
        _connections = connections;
        _contactsClient = contactsClient;
        _upserter = upserter;
        _logger = logger;
    }

    public async Task<AdsolutContactsReconcileResult?> ReconcileCompanyAsync(
        Guid companyId,
        AdsolutContactsSyncOptions options,
        CancellationToken ct = default)
    {
        var connection = await _connections.GetAsync(ct);
        if (connection?.AdministrationId is not Guid administrationId)
        {
            return null;
        }

        Guid? adsolutId;
        await using (var conn = await _dataSource.OpenConnectionAsync(ct))
        {
            adsolutId = await conn.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(
                "SELECT adsolut_id FROM companies WHERE id = @Id AND is_active = TRUE LIMIT 1",
                new { Id = companyId },
                cancellationToken: ct));
        }

        if (adsolutId is null)
        {
            return null;
        }

        var result = new AdsolutContactsReconcileResult { CompaniesScanned = 1 };
        await ReconcileOneAsync(administrationId, companyId, adsolutId.Value, options, result, ct);
        return result;
    }

    public async Task<AdsolutContactsReconcileResult> ReconcileAllAsync(
        AdsolutContactsSyncOptions options,
        CancellationToken ct = default)
    {
        var result = new AdsolutContactsReconcileResult();
        var connection = await _connections.GetAsync(ct);
        if (connection?.AdministrationId is not Guid administrationId)
        {
            return result;
        }

        if (!options.PullUpdateEnabled && !options.PullCreateEnabled)
        {
            // Both toggles off — would be a no-op for every company. Skip
            // the WK calls entirely so an idle install doesn't make
            // Adsolut traffic on the reconcile cadence.
            return result;
        }

        var pairs = await LoadAllPairsAsync(ct);
        foreach (var pair in pairs)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await ReconcileOneAsync(administrationId, pair.CompanyId, pair.AdsolutId, options, result, ct);
                result.CompaniesScanned++;
            }
            catch (AdsolutApiException ex)
            {
                // One bad company shouldn't block the rest of the sweep.
                // Per-row failure already wrote a Warn/Error row through
                // the invoker — nothing to add here.
                _logger.LogInformation(
                    "Adsolut contacts reconcile of company {CompanyId} ({AdsolutId}) failed: {Status} {Code}",
                    pair.CompanyId, pair.AdsolutId, ex.HttpStatus, ex.UpstreamErrorCode);
            }
        }
        return result;
    }

    private async Task ReconcileOneAsync(
        Guid administrationId,
        Guid companyId,
        Guid adsolutCustomerId,
        AdsolutContactsSyncOptions options,
        AdsolutContactsReconcileResult result,
        CancellationToken ct)
    {
        var freshContacts = await _contactsClient.ListCustomerContactsAsync(
            administrationId, adsolutCustomerId, ct);

        foreach (var inbound in freshContacts)
        {
            result.ContactsSeen++;
            var outcome = await _upserter.UpsertAsync(companyId, inbound, options, ct);
            switch (outcome)
            {
                case AdsolutContactUpsertOutcome.Updated:
                    result.ContactsUpdated++;
                    break;
                case AdsolutContactUpsertOutcome.Created:
                    result.ContactsCreated++;
                    break;
                case AdsolutContactUpsertOutcome.SkippedNoChange:
                    result.ContactsSkippedNoChange++;
                    break;
                case AdsolutContactUpsertOutcome.SkippedLocalNewer:
                    result.ContactsSkippedLocalNewer++;
                    break;
                case AdsolutContactUpsertOutcome.SkippedUpdateToggleOff:
                case AdsolutContactUpsertOutcome.SkippedCreateToggleOff:
                    result.ContactsSkippedToggleOff++;
                    break;
                case AdsolutContactUpsertOutcome.SkippedNoEmail:
                    result.ContactsSkippedNoEmail++;
                    break;
                case AdsolutContactUpsertOutcome.SkippedLinkCompanyMismatch:
                    result.ContactsSkippedLinkConflict++;
                    break;
            }
        }

        var freshIds = freshContacts.Select(c => c.Id).ToArray();
        result.LinksReconciled += await _upserter.ReconcileMissingLinksAsync(companyId, freshIds, ct);
    }

    private sealed class CompanyPairRow
    {
        public Guid CompanyId { get; set; }
        public Guid AdsolutId { get; set; }
    }

    private async Task<IReadOnlyList<CompanyPairRow>> LoadAllPairsAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        var rows = await conn.QueryAsync<CompanyPairRow>(new CommandDefinition(
            """
            SELECT id         AS CompanyId,
                   adsolut_id AS AdsolutId
            FROM companies
            WHERE is_active = TRUE
              AND adsolut_id IS NOT NULL
            ORDER BY adsolut_last_modified ASC NULLS FIRST
            """,
            cancellationToken: ct));
        return rows.AsList();
    }
}
