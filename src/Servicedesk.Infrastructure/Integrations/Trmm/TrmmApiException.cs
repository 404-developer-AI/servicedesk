namespace Servicedesk.Infrastructure.Integrations.Trmm;

/// Transport- and protocol-level failure when talking to Tactical RMM.
/// HTTP status (when one was received) and the normalised upstream error
/// code are carried on the exception so callers can render a precise
/// admin-facing message without re-parsing the response.
public sealed class TrmmApiException : Exception
{
    public int? HttpStatus { get; }
    public string? UpstreamErrorCode { get; }

    public TrmmApiException(string message, int? httpStatus = null, string? upstreamErrorCode = null)
        : base(message)
    {
        HttpStatus = httpStatus;
        UpstreamErrorCode = upstreamErrorCode;
    }

    public TrmmApiException(string message, Exception inner, int? httpStatus = null, string? upstreamErrorCode = null)
        : base(message, inner)
    {
        HttpStatus = httpStatus;
        UpstreamErrorCode = upstreamErrorCode;
    }
}
