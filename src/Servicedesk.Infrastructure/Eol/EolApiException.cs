namespace Servicedesk.Infrastructure.Eol;

public sealed class EolApiException : Exception
{
    public int? HttpStatus { get; }
    public string? UpstreamErrorCode { get; }

    public EolApiException(string message, int? httpStatus = null, string? upstreamErrorCode = null)
        : base(message)
    {
        HttpStatus = httpStatus;
        UpstreamErrorCode = upstreamErrorCode;
    }

    public EolApiException(string message, Exception inner, int? httpStatus = null, string? upstreamErrorCode = null)
        : base(message, inner)
    {
        HttpStatus = httpStatus;
        UpstreamErrorCode = upstreamErrorCode;
    }
}
