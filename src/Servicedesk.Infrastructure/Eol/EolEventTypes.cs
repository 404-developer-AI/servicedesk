namespace Servicedesk.Infrastructure.Eol;

/// Integration + event-type constants for <c>integration_audit</c> rows
/// written by the end-of-life data refresh worker. Distinct
/// <see cref="Integration"/> namespace ("eol") so the audit-log reader
/// on the TRMM integration page can filter on it without colliding with
/// the existing TRMM events.
public static class EolEventTypes
{
    public const string Integration = "eol";

    public const string FetchWindows       = "fetch_windows";
    public const string FetchWindowsServer = "fetch_windows_server";

    public const string RefreshStarted   = "refresh_started";
    public const string RefreshCompleted = "refresh_completed";
    public const string RefreshFailed    = "refresh_failed";
}
