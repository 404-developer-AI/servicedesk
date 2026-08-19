using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Infrastructure.Portal;

/// Outcome of a Turnstile siteverify round-trip. <see cref="Success"/> is
/// only true when Cloudflare said success AND the action + hostname matched.
/// <see cref="Reason"/> is a short machine code for logs/audit, never shown
/// to the registrant verbatim (they only see "verification failed").
public sealed record TurnstileVerdict(bool Success, string Reason)
{
    public static TurnstileVerdict Ok { get; } = new(true, "ok");
    public static TurnstileVerdict Fail(string reason) => new(false, reason);
}

public interface ITurnstileVerifier
{
    /// Verifies a widget response token. FAIL CLOSED: any error (missing
    /// secret, network failure, timeout, malformed reply, action/hostname
    /// mismatch) yields Success = false.
    Task<TurnstileVerdict> VerifyAsync(
        string? responseToken, string? remoteIp, string expectedAction, string? expectedHostname, CancellationToken ct);
}

public sealed class TurnstileVerifier : ITurnstileVerifier
{
    public const string HttpClientName = "cloudflare-turnstile";
    public const string SiteVerifyUrl = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IProtectedSecretStore _secrets;
    private readonly ISettingsService _settings;
    private readonly ILogger<TurnstileVerifier> _logger;

    public TurnstileVerifier(
        IHttpClientFactory httpClientFactory,
        IProtectedSecretStore secrets,
        ISettingsService settings,
        ILogger<TurnstileVerifier> logger)
    {
        _httpClientFactory = httpClientFactory;
        _secrets = secrets;
        _settings = settings;
        _logger = logger;
    }

    public async Task<TurnstileVerdict> VerifyAsync(
        string? responseToken, string? remoteIp, string expectedAction, string? expectedHostname, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(responseToken) || responseToken.Length > 2048)
            return TurnstileVerdict.Fail("missing_token");

        var secret = await _secrets.GetAsync(ProtectedSecretKeys.PortalTurnstileSecret, ct);
        if (string.IsNullOrWhiteSpace(secret))
        {
            _logger.LogWarning("Turnstile is enabled but no secret key is configured — refusing registration (fail closed).");
            return TurnstileVerdict.Fail("secret_missing");
        }

        var timeoutSeconds = await _settings.GetAsync<int>(SettingKeys.Portal.TurnstileTimeoutSeconds, ct);
        if (timeoutSeconds <= 0) timeoutSeconds = 10;

        SiteVerifyResponse? body;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var form = new Dictionary<string, string>
            {
                ["secret"] = secret,
                ["response"] = responseToken,
            };
            if (!string.IsNullOrWhiteSpace(remoteIp)) form["remoteip"] = remoteIp;

            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsync(SiteVerifyUrl, new FormUrlEncodedContent(form), timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Turnstile siteverify returned HTTP {Status} — refusing registration.", (int)response.StatusCode);
                return TurnstileVerdict.Fail("http_" + (int)response.StatusCode);
            }
            body = await response.Content.ReadFromJsonAsync<SiteVerifyResponse>(cancellationToken: timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Turnstile siteverify timed out after {Seconds}s — refusing registration.", timeoutSeconds);
            return TurnstileVerdict.Fail("timeout");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Turnstile siteverify failed — refusing registration.");
            return TurnstileVerdict.Fail("network_error");
        }

        if (body is null) return TurnstileVerdict.Fail("empty_response");
        if (!body.Success)
        {
            var codes = body.ErrorCodes is { Length: > 0 } ? string.Join(",", body.ErrorCodes) : "unspecified";
            _logger.LogInformation("Turnstile rejected a registration token ({Codes}).", codes);
            return TurnstileVerdict.Fail("rejected:" + codes);
        }

        if (!string.IsNullOrEmpty(expectedAction)
            && !string.Equals(body.Action ?? string.Empty, expectedAction, StringComparison.Ordinal))
        {
            _logger.LogWarning("Turnstile action mismatch: expected {Expected}, got {Actual}.", expectedAction, body.Action);
            return TurnstileVerdict.Fail("action_mismatch");
        }

        if (!string.IsNullOrWhiteSpace(expectedHostname)
            && !string.Equals(body.Hostname ?? string.Empty, expectedHostname, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Turnstile hostname mismatch: expected {Expected}, got {Actual}.", expectedHostname, body.Hostname);
            return TurnstileVerdict.Fail("hostname_mismatch");
        }

        return TurnstileVerdict.Ok;
    }

    /// Shape of https://challenges.cloudflare.com/turnstile/v0/siteverify.
    internal sealed class SiteVerifyResponse
    {
        [JsonPropertyName("success")] public bool Success { get; set; }
        [JsonPropertyName("error-codes")] public string[]? ErrorCodes { get; set; }
        [JsonPropertyName("challenge_ts")] public string? ChallengeTs { get; set; }
        [JsonPropertyName("hostname")] public string? Hostname { get; set; }
        [JsonPropertyName("action")] public string? Action { get; set; }
        [JsonPropertyName("cdata")] public string? CData { get; set; }
    }
}
