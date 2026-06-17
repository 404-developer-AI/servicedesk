using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Audit;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Integrations.Claude;

/// <inheritdoc cref="IClaudeApiClient"/>
public sealed class ClaudeApiClient : IClaudeApiClient
{
    public const string HttpClientName = "claude-api";

    /// Pinned Messages API version. Bump deliberately, never from config.
    private const string AnthropicVersion = "2023-06-01";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IProtectedSecretStore _secrets;
    private readonly ISettingsService _settings;
    private readonly IIntegrationAuditLogger _audit;
    private readonly ILogger<ClaudeApiClient> _logger;

    public ClaudeApiClient(
        IHttpClientFactory httpClientFactory,
        IProtectedSecretStore secrets,
        ISettingsService settings,
        IIntegrationAuditLogger audit,
        ILogger<ClaudeApiClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _secrets = secrets;
        _settings = settings;
        _audit = audit;
        _logger = logger;
    }

    public async Task<ClaudeApiResult> CreateProposalAsync(
        string systemPrompt,
        string userText,
        IReadOnlyList<ClaudeImageInput> images,
        CancellationToken ct)
    {
        var apiKey = await _secrets.GetAsync(ProtectedSecretKeys.ClaudeApiKey, ct);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ClaudeApiException("Claude API key is not configured.", httpStatus: null, upstreamErrorCode: "missing_api_key");

        var baseUrl = (await _settings.GetAsync<string>(SettingKeys.Claude.ApiBaseUrl, ct) ?? string.Empty).Trim();
        if (baseUrl.Length == 0) baseUrl = "https://api.anthropic.com";
        baseUrl = baseUrl.TrimEnd('/');
        var url = baseUrl + "/v1/messages";

        var model = (await _settings.GetAsync<string>(SettingKeys.Claude.Model, ct) ?? "claude-sonnet-4-6").Trim();
        var maxTokens = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Claude.MaxTokens, ct), 256, 8192);
        var effort = (await _settings.GetAsync<string>(SettingKeys.Claude.Effort, ct) ?? "off").Trim().ToLowerInvariant();
        var timeoutSeconds = Math.Clamp(await _settings.GetAsync<int>(SettingKeys.Claude.RequestTimeoutSeconds, ct), 10, 600);

        var body = BuildRequestBody(model, systemPrompt, userText, images, maxTokens, effort);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);
        request.Headers.Accept.ParseAdd("application/json");
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        var http = _httpClientFactory.CreateClient(HttpClientName);
        http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await http.SendAsync(request, ct);
            stopwatch.Stop();

            var status = (int)response.StatusCode;
            var requestId = ReadRequestId(response);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                var (upstreamCode, upstreamMessage) = TryParseError(responseBody);
                var outcome = status == 429 || status >= 500
                    ? IntegrationAuditOutcome.Warn
                    : IntegrationAuditOutcome.Error;
                await _audit.LogAsync(new IntegrationAuditEvent(
                    Integration: ClaudeEventTypes.Integration,
                    EventType: ClaudeEventTypes.ProposalCall,
                    Outcome: outcome,
                    Endpoint: url,
                    HttpStatus: status,
                    LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                    ErrorCode: upstreamCode ?? $"http_{status}",
                    Payload: new { model, requestId, snippet = Truncate(upstreamMessage ?? responseBody, 256) }), ct);
                throw new ClaudeApiException(
                    upstreamMessage ?? $"Claude API returned HTTP {status}.",
                    httpStatus: status,
                    upstreamErrorCode: upstreamCode);
            }

            var parsed = ParseSuccess(responseBody, model, requestId);
            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: ClaudeEventTypes.Integration,
                EventType: ClaudeEventTypes.ProposalCall,
                Outcome: IntegrationAuditOutcome.Ok,
                Endpoint: url,
                HttpStatus: status,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                Payload: new
                {
                    model = parsed.Model,
                    requestId,
                    stopReason = parsed.StopReason,
                    inputTokens = parsed.InputTokens,
                    outputTokens = parsed.OutputTokens,
                    images = images.Count,
                }), ct);
            return parsed;
        }
        catch (ClaudeApiException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            await _audit.LogAsync(new IntegrationAuditEvent(
                Integration: ClaudeEventTypes.Integration,
                EventType: ClaudeEventTypes.ProposalCall,
                Outcome: IntegrationAuditOutcome.Warn,
                Endpoint: url,
                HttpStatus: null,
                LatencyMs: (int)stopwatch.ElapsedMilliseconds,
                ErrorCode: "transport_error",
                Payload: new { model, message = ex.Message }), ct);
            throw new ClaudeApiException("Transport error calling the Claude API: " + ex.Message, ex);
        }
    }

    private static JsonObject BuildRequestBody(
        string model,
        string systemPrompt,
        string userText,
        IReadOnlyList<ClaudeImageInput> images,
        int maxTokens,
        string effort)
    {
        var content = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = userText },
        };
        foreach (var img in images)
        {
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = img.MediaType,
                    ["data"] = img.Base64Data,
                },
            });
        }

        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            ["system"] = systemPrompt,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = content },
            },
        };

        // Effort is opt-in and only honoured on models that support the
        // parameter — sending it to e.g. Haiku 4.5 returns a 400, so drop it
        // there rather than risk a hard failure.
        if (effort is "low" or "medium" or "high" && ModelSupportsEffort(model))
        {
            body["output_config"] = new JsonObject { ["effort"] = effort };
        }

        return body;
    }

    private static bool ModelSupportsEffort(string model)
    {
        var m = model.ToLowerInvariant();
        return m.Contains("sonnet-4-6")
            || m.Contains("opus-4-5")
            || m.Contains("opus-4-6")
            || m.Contains("opus-4-7")
            || m.Contains("opus-4-8")
            || m.Contains("fable")
            || m.Contains("mythos");
    }

    private static ClaudeApiResult ParseSuccess(string responseBody, string fallbackModel, string? requestId)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;

            var text = new StringBuilder();
            if (root.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in contentEl.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var t)
                        && t.GetString() == "text"
                        && block.TryGetProperty("text", out var txt))
                    {
                        text.Append(txt.GetString());
                    }
                }
            }

            var model = root.TryGetProperty("model", out var mEl) ? (mEl.GetString() ?? fallbackModel) : fallbackModel;
            var stopReason = root.TryGetProperty("stop_reason", out var srEl) ? srEl.GetString() : null;

            var inputTokens = 0;
            var outputTokens = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var itv)) inputTokens = itv;
                if (usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var otv)) outputTokens = otv;
            }

            return new ClaudeApiResult(text.ToString().Trim(), model, inputTokens, outputTokens, stopReason, requestId);
        }
        catch (JsonException ex)
        {
            throw new ClaudeApiException("Could not parse the Claude API response: " + ex.Message, ex);
        }
    }

    private static (string? Code, string? Message) TryParseError(string responseBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                var code = err.TryGetProperty("type", out var t) ? t.GetString() : null;
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : null;
                return (code, msg);
            }
        }
        catch (JsonException)
        {
            // Non-JSON error body (gateway HTML etc.) — fall through.
        }
        return (null, null);
    }

    private static string? ReadRequestId(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("request-id", out var ids))
            return ids.FirstOrDefault();
        if (response.Headers.TryGetValues("x-request-id", out var xids))
            return xids.FirstOrDefault();
        return null;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];
}
