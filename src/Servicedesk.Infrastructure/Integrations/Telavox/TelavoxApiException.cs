namespace Servicedesk.Infrastructure.Integrations.Telavox;

/// Thrown by <see cref="ITelavoxApiClient"/> when Telavox returns a non-2xx
/// the caller can't recover from at this layer. Mirrors AdsolutApiException
/// so admin endpoints can map both integrations to the same 502 shape.
/// HttpStatus is carried so callers can distinguish 401 (bad token —
/// surface "reconnect partner token" to admin), 404 (api-user already gone),
/// 429 (back off), and 5xx (transient — try again).
public sealed class TelavoxApiException : Exception
{
    public int? HttpStatus { get; }
    public string? UpstreamErrorCode { get; }

    public TelavoxApiException(string message, int? httpStatus = null, string? upstreamErrorCode = null)
        : base(message)
    {
        HttpStatus = httpStatus;
        UpstreamErrorCode = upstreamErrorCode;
    }

    public TelavoxApiException(string message, Exception inner, int? httpStatus = null)
        : base(message, inner)
    {
        HttpStatus = httpStatus;
    }
}
