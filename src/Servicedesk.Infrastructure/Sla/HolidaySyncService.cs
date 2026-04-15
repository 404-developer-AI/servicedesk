using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Servicedesk.Infrastructure.Sla;

public interface IHolidaySyncService
{
    Task SyncAsync(Guid schemaId, string countryCode, int year, CancellationToken ct);
    Task<IReadOnlyList<NagerCountry>> ListCountriesAsync(CancellationToken ct);
}

public sealed record NagerCountry(string CountryCode, string Name);

/// Wraps date.nager.at — a free, keyless public-holidays service that covers
/// 100+ countries. We sync one (schemaId, year, country) combination at a
/// time; existing 'nager' rows for that tuple are replaced, manual rows are
/// left untouched.
public sealed class HolidaySyncService : IHolidaySyncService
{
    private static readonly Uri ApiBase = new("https://date.nager.at/api/v3/");
    private readonly IHttpClientFactory _httpFactory;
    private readonly ISlaRepository _repo;
    private readonly ILogger<HolidaySyncService> _logger;

    public HolidaySyncService(
        IHttpClientFactory httpFactory,
        ISlaRepository repo,
        ILogger<HolidaySyncService> logger)
    {
        _httpFactory = httpFactory;
        _repo = repo;
        _logger = logger;
    }

    public async Task SyncAsync(Guid schemaId, string countryCode, int year, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(countryCode)) return;
        var url = new Uri(ApiBase, $"PublicHolidays/{year}/{countryCode.ToUpperInvariant()}");
        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        var items = await http.GetFromJsonAsync<NagerHoliday[]>(url, ct) ?? Array.Empty<NagerHoliday>();
        var mapped = items.Select(h => (Date: DateOnly.Parse(h.Date), Name: h.LocalName ?? h.Name ?? "")).ToList();
        await _repo.ReplaceNagerHolidaysAsync(schemaId, year, countryCode.ToUpperInvariant(), mapped, ct);
        _logger.LogInformation("Synced {Count} public holidays for schema {Schema} ({Country}/{Year}).",
            mapped.Count, schemaId, countryCode, year);
    }

    public async Task<IReadOnlyList<NagerCountry>> ListCountriesAsync(CancellationToken ct)
    {
        using var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        var items = await http.GetFromJsonAsync<NagerCountryRaw[]>(new Uri(ApiBase, "AvailableCountries"), ct)
                  ?? Array.Empty<NagerCountryRaw>();
        return items.Select(c => new NagerCountry(c.CountryCode, c.Name)).ToList();
    }

    private sealed record NagerHoliday(string Date, string? LocalName, string? Name);
    private sealed record NagerCountryRaw(string CountryCode, string Name);
}
