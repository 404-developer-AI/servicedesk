namespace Servicedesk.Infrastructure.Integrations.Claude;

/// Raised when a Messages API call fails (transport error or a non-2xx
/// response). Mirrors the per-integration exception pattern (see Telavox):
/// the endpoint layer maps it to a 502 envelope carrying the upstream status
/// and error code so an admin can see what Anthropic returned.
public sealed class ClaudeApiException : Exception
{
    public int? HttpStatus { get; }
    public string? UpstreamErrorCode { get; }

    public ClaudeApiException(string message, int? httpStatus = null, string? upstreamErrorCode = null)
        : base(message)
    {
        HttpStatus = httpStatus;
        UpstreamErrorCode = upstreamErrorCode;
    }

    public ClaudeApiException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
