using Microsoft.Extensions.Logging;
using Servicedesk.Domain.Search;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Search;

/// Dispatcher across every registered <see cref="ISearchSource"/>. The
/// dropdown path (Type == null) hits the quick-scoped sources in parallel and
/// takes top-N from each; the full-page path (Type != null) runs a single
/// source, paginated.
///
/// Every source runs inside its own time budget (Search.SourceTimeoutMs): a
/// source that exceeds it is cancelled and contributes an empty group, so one
/// slow query can never hold the whole search hostage (the pre-v0.0.93
/// behaviour was a ~30 s dropdown stall whenever a single un-indexed source
/// hit the Npgsql command timeout). A source that throws is likewise isolated
/// to an empty group instead of failing the entire request.
///
/// v0.0.99: the fan-out is bounded by Search.MaxConcurrentSources. Every
/// source opens its own pooled connection, so an unbounded burst across ~20
/// sources times a few simultaneous searches could eat a large slice of the
/// Postgres connection budget; the semaphore keeps one request's footprint
/// predictable. Per-source timeouts still apply — the budget clock starts
/// when a source actually gets a slot, not while it queues.
public sealed class SearchService : ISearchService
{
    private const int TimeoutFloorMs = 250;
    private const int TimeoutCeilingMs = 30_000;
    private const int TimeoutFallbackMs = 5_000;

    private const int ConcurrencyFloor = 1;
    private const int ConcurrencyCeiling = 64;
    private const int ConcurrencyFallback = 8;

    private readonly IReadOnlyList<ISearchSource> _sources;
    private readonly ISettingsService _settings;
    private readonly ILogger<SearchService> _logger;

    public SearchService(
        IEnumerable<ISearchSource> sources,
        ISettingsService settings,
        ILogger<SearchService> logger)
    {
        _sources = sources.ToList();
        _settings = settings;
        _logger = logger;
    }

    public async Task<SearchResults> SearchAsync(
        SearchRequest request, SearchPrincipal principal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);

        if (string.IsNullOrWhiteSpace(request.Query))
            return new SearchResults(Array.Empty<SearchGroup>(), 0);

        IEnumerable<ISearchSource> sources = _sources.Where(s => s.IsAvailableFor(principal));
        if (!string.IsNullOrWhiteSpace(request.Type))
            sources = sources.Where(s => string.Equals(s.Kind, request.Type, StringComparison.OrdinalIgnoreCase));
        else if (request.Kinds is { Count: > 0 })
            sources = sources.Where(s => request.Kinds.Contains(s.Kind, StringComparer.OrdinalIgnoreCase));

        var timeoutMs = await GetSourceTimeoutMsAsync(ct);
        using var slots = new SemaphoreSlim(await GetMaxConcurrentSourcesAsync(ct));
        var tasks = sources.Select(s => RunThrottledAsync(s, request, principal, timeoutMs, slots, ct)).ToList();
        var groups = await Task.WhenAll(tasks);
        var total = groups.Sum(g => g.TotalInGroup);
        return new SearchResults(groups, total);
    }

    public IReadOnlyList<string> AvailableKindsFor(SearchPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return _sources.Where(s => s.IsAvailableFor(principal)).Select(s => s.Kind).ToList();
    }

    private async Task<SearchGroup> RunThrottledAsync(
        ISearchSource source, SearchRequest request, SearchPrincipal principal,
        int timeoutMs, SemaphoreSlim slots, CancellationToken ct)
    {
        await slots.WaitAsync(ct);
        try
        {
            return await RunGuardedAsync(source, request, principal, timeoutMs, ct);
        }
        finally
        {
            slots.Release();
        }
    }

    /// Runs one source under its own cancellation budget. Timeout and source
    /// failures both degrade to an empty group — the query text itself is
    /// never logged (only its length) because searches routinely contain
    /// customer names and other sensitive terms.
    private async Task<SearchGroup> RunGuardedAsync(
        ISearchSource source, SearchRequest request, SearchPrincipal principal,
        int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            return await source.SearchAsync(request, principal, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Search source {Kind} exceeded its {TimeoutMs} ms budget (query length {QueryLength}); returning an empty group.",
                source.Kind, timeoutMs, request.Query.Length);
            return new SearchGroup(source.Kind, Array.Empty<SearchHit>(), 0, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex,
                "Search source {Kind} failed (query length {QueryLength}); returning an empty group.",
                source.Kind, request.Query.Length);
            return new SearchGroup(source.Kind, Array.Empty<SearchHit>(), 0, false);
        }
    }

    private async Task<int> GetMaxConcurrentSourcesAsync(CancellationToken ct)
    {
        try
        {
            var raw = await _settings.GetAsync<int>(SettingKeys.Search.MaxConcurrentSources, ct);
            return Math.Clamp(raw, ConcurrencyFloor, ConcurrencyCeiling);
        }
        catch
        {
            return ConcurrencyFallback;
        }
    }

    private async Task<int> GetSourceTimeoutMsAsync(CancellationToken ct)
    {
        try
        {
            var raw = await _settings.GetAsync<int>(SettingKeys.Search.SourceTimeoutMs, ct);
            return Math.Clamp(raw, TimeoutFloorMs, TimeoutCeilingMs);
        }
        catch
        {
            return TimeoutFallbackMs;
        }
    }
}
