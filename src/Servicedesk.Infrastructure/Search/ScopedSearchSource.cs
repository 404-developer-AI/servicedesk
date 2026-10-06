using Servicedesk.Domain.Search;

namespace Servicedesk.Infrastructure.Search;

/// Defense-in-depth wrapper. Every source is registered behind this
/// decorator: it re-checks availability for the principal before handing
/// control to the inner source. If a future refactor accidentally removes
/// the availability check inside a source, the wrapper still returns an
/// empty group instead of leaking results.
public sealed class ScopedSearchSource : ISearchSource
{
    private readonly ISearchSource _inner;

    public ScopedSearchSource(ISearchSource inner) => _inner = inner;

    public string Kind => _inner.Kind;

    public bool IsAvailableFor(SearchPrincipal principal) => _inner.IsAvailableFor(principal);

    public Task<SearchGroup> SearchAsync(SearchRequest request, SearchPrincipal principal, CancellationToken ct)
    {
        if (principal is null)
            throw new ArgumentNullException(nameof(principal));
        if (!_inner.IsAvailableFor(principal))
            return Task.FromResult(new SearchGroup(_inner.Kind, Array.Empty<SearchHit>(), 0, false));
        if (!Performance.PerfRuntime.IsOn(Performance.PerfCollector.Http))
            return _inner.SearchAsync(request, principal, ct);
        return TimedAsync(request, principal, ct);
    }

    /// v0.1.24 — global search waits for its slowest source, so each source's
    /// duration is recorded (Performance → Background & realtime → Search).
    private async Task<SearchGroup> TimedAsync(SearchRequest request, SearchPrincipal principal, CancellationToken ct)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        var failed = false;
        try
        {
            return await _inner.SearchAsync(request, principal, ct);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            Performance.PerfRuntime.Recorder?.Current
                .SpanFor(new Performance.SpanKey("search", _inner.Kind, ""))
                .Record(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds, failed);
        }
    }
}
