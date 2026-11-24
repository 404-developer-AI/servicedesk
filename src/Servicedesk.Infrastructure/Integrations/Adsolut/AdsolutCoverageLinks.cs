using Dapper;
using Npgsql;

namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// The admin "link" actions on the Adsolut coverage page: tie a local
/// company or contact-company link to an existing Adsolut record. Only the
/// Adsolut id and last-modified are written — never the sync hash: the next
/// push or pull tick closes the loop with a single call, so linking cannot
/// start a hash storm. <c>adsolut_number</c> / <c>adsolut_alpha_code</c>
/// arrive with the next pull that touches the row.
public interface IAdsolutCoverageLinks
{
    /// False when the company does not exist or is inactive.
    Task<bool> LinkCompanyAsync(Guid companyId, Guid adsolutId, DateTime? lastModified, CancellationToken ct);

    /// False when the link is missing, its contact or company is inactive,
    /// or the company is not linked to Adsolut yet. Stamps
    /// <c>adsolut_active</c> so the derived <c>contacts.is_active</c>
    /// recompute treats the link as Adsolut-aware (Adsolut.md → Lessons
    /// learned #5).
    Task<bool> LinkContactAsync(Guid linkId, Guid adsolutContactId, DateTime? lastModified, CancellationToken ct);
}

public sealed class AdsolutCoverageLinks : IAdsolutCoverageLinks
{
    private readonly NpgsqlDataSource _dataSource;

    public AdsolutCoverageLinks(NpgsqlDataSource dataSource) => _dataSource = dataSource;

    public async Task<bool> LinkCompanyAsync(Guid companyId, Guid adsolutId, DateTime? lastModified, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE companies
               SET adsolut_id            = @adsolutId,
                   adsolut_last_modified = @lastModified,
                   updated_utc           = COALESCE(@lastModified, now())
             WHERE id = @companyId
               AND is_active = TRUE
            """,
            new { companyId, adsolutId, lastModified }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> LinkContactAsync(Guid linkId, Guid adsolutContactId, DateTime? lastModified, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE contact_companies cc
               SET adsolut_contact_id    = @adsolutContactId,
                   adsolut_last_modified = @lastModified,
                   adsolut_active        = TRUE,
                   updated_utc           = COALESCE(@lastModified, now())
              FROM contacts c, companies co
             WHERE cc.id = @linkId
               AND c.id = cc.contact_id
               AND co.id = cc.company_id
               AND c.is_active = TRUE
               AND co.is_active = TRUE
               AND co.adsolut_id IS NOT NULL
            """,
            new { linkId, adsolutContactId, lastModified }, cancellationToken: ct)) > 0;
    }
}
