using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace TimesheetMigrator;

/// HTTP client for a target Servicedesk install's secret-gated migration
/// surface. The token travels in the X-Timesheet-Import-Token header on
/// every call. Point BaseUrl at dev or prod — that's the whole "which
/// environment" switch.
public sealed class TargetClient : IDisposable
{
    private const string TokenHeader = "X-Timesheet-Import-Token";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public TargetClient(string baseUrl, string token)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.Add(TokenHeader, token);
    }

    public async Task<List<TargetTask>> GetTasksAsync(CancellationToken ct)
    {
        var resp = await _http.GetAsync("api/timesheet/import/tasks", ct);
        await EnsureOkAsync(resp, "GET tasks", ct);
        var body = await resp.Content.ReadFromJsonAsync<ListResponse<TargetTask>>(Json, ct);
        return body?.Items ?? new();
    }

    public async Task<List<TargetUser>> GetUsersAsync(CancellationToken ct)
    {
        var resp = await _http.GetAsync("api/timesheet/import/users", ct);
        await EnsureOkAsync(resp, "GET users", ct);
        var body = await resp.Content.ReadFromJsonAsync<ListResponse<TargetUser>>(Json, ct);
        return body?.Items ?? new();
    }

    public async Task<ImportBatchResponse> PostBatchAsync(ImportBatch batch, CancellationToken ct)
    {
        var resp = await _http.PostAsJsonAsync("api/timesheet/import/entries", batch, Json, ct);
        await EnsureOkAsync(resp, "POST entries", ct);
        return (await resp.Content.ReadFromJsonAsync<ImportBatchResponse>(Json, ct))!;
    }

    private static async Task EnsureOkAsync(HttpResponseMessage resp, string what, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync(ct);
        var hint = resp.StatusCode switch
        {
            HttpStatusCode.NotFound =>
                " — surface is disabled or unconfigured. Enable it and set the token under Settings → Timesheet → Migration import.",
            HttpStatusCode.Unauthorized => " — token rejected. Check TS_TARGET_TOKEN matches the configured import token.",
            _ => "",
        };
        throw new InvalidOperationException($"{what} failed: {(int)resp.StatusCode} {resp.StatusCode}{hint}\n{body}");
    }

    public void Dispose() => _http.Dispose();
}
