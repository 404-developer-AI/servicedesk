namespace Servicedesk.Infrastructure.Integrations.Adsolut;

/// Thrown by the Adsolut API clients when the upstream returns a non-2xx
/// status that the caller can't recover from at this layer. The sync worker
/// translates this into a tick-level failure (`adsolut_sync_state.last_error`
/// + `integration_audit` row). Carries the HTTP status so the worker can
/// distinguish 401 (force a fresh access token + retry once), 429 (back
/// off), and 5xx (treat as transient — try again next tick) without
/// re-parsing the message.
public sealed class AdsolutApiException : Exception
{
    public int? HttpStatus { get; }
    public string? UpstreamErrorCode { get; }

    public AdsolutApiException(string message, int? httpStatus = null, string? upstreamErrorCode = null)
        : base(message)
    {
        HttpStatus = httpStatus;
        UpstreamErrorCode = upstreamErrorCode;
    }

    public AdsolutApiException(string message, Exception inner, int? httpStatus = null)
        : base(message, inner)
    {
        HttpStatus = httpStatus;
    }
}
