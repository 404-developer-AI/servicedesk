namespace Servicedesk.Infrastructure.Eol;

/// HTTP surface for the endoflife.date registry. Implementations write
/// one <c>integration_audit</c> row per call and surface upstream
/// failures as <see cref="EolApiException"/>.
public interface IEolDataClient
{
    Task<IReadOnlyList<EolReleaseRow>> FetchWindowsAsync(CancellationToken ct);
    Task<IReadOnlyList<EolReleaseRow>> FetchWindowsServerAsync(CancellationToken ct);
}
