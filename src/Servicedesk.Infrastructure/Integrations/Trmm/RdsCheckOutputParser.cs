using System.Globalization;
using System.Text.RegularExpressions;

namespace Servicedesk.Infrastructure.Integrations.Trmm;

/// Classification of one server's Remote Desktop check (v0.1.10). Stored
/// verbatim in <c>trmm_agent_rds.status</c>; the Remote Desktop tab lists
/// <see cref="Rds"/> rows per client, hides <see cref="NotRds"/> rows and
/// parks everything else in its "Needs attention" section.
public static class RdsStatus
{
    /// Script exited cleanly and reported an RDS session host.
    public const string Rds = "rds";
    /// Script exited cleanly and reported "not an RDS host". Hidden.
    public const string NotRds = "not_rds";
    /// Script ran but failed (exit 1 / failing / unrecognisable output).
    public const string Failed = "failed";
    /// The agent has no check matching the configured script name.
    public const string NoCheck = "no_check";
    /// The check exists but has never produced a result on this agent.
    public const string Pending = "pending";
    /// The per-agent checks call to TRMM itself failed (transport/HTTP).
    public const string Error = "error";
}

public sealed record RdsCheckParseResult(
    string Status,
    DateTime? LastLoginLocal,
    string? LastLoginKind,
    string? LastLoginUser);

/// Parses the stdout of the customer's <c>Check-RDS.ps1</c> script check.
/// The script prints, in order:
/// <code>
/// Remote Desktop server: TRUE | FALSE
/// Laatste login : dd-MM-yyyy HH:mm:ss (RDP|Lokaal)     (only when TRUE)
/// Gebruiker     : DOMAIN\user
/// </code>
/// and exits 1 with a "Script gefaald: …" line on stderr when it could not
/// determine the role. The parser is deliberately tolerant: keyword match
/// is case-insensitive, both the Dutch and an English variant of the
/// login/user labels are accepted, and the login timestamp is kept as a
/// <b>local</b> (unzoned) time — it is the server's own clock as printed
/// by the script, not something we can convert to UTC.
public static class RdsCheckOutputParser
{
    private static readonly Regex FlagRegex = new(
        @"Remote\s+Desktop\s+server\s*:\s*(?<flag>TRUE|FALSE)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LastLoginRegex = new(
        @"^\s*(?:Laatste\s+login|Last\s+login)\s*:\s*(?<stamp>\d{2}-\d{2}-\d{4}\s+\d{2}:\d{2}:\d{2})\s*(?:\((?<kind>[^)]+)\))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex UserRegex = new(
        @"^\s*(?:Gebruiker|User)\s*:\s*(?<user>\S.*?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    /// <param name="stdout">Check result stdout (may be null/empty).</param>
    /// <param name="retcode">Script exit code as reported by TRMM.</param>
    /// <param name="resultStatus">TRMM check status: passing / failing / pending / null.</param>
    public static RdsCheckParseResult Parse(string? stdout, long? retcode, string? resultStatus)
    {
        var hasOutput = !string.IsNullOrWhiteSpace(stdout);
        var status = (resultStatus ?? string.Empty).Trim().ToLowerInvariant();

        // Never ran: TRMM reports "pending" (or nothing at all) and there
        // is no output to read. A check that ran but printed nothing is a
        // failure, not pending — the script always prints the flag line.
        if (!hasOutput && retcode is null && (status.Length == 0 || status == "pending"))
        {
            return new RdsCheckParseResult(RdsStatus.Pending, null, null, null);
        }

        var flag = hasOutput ? FlagRegex.Match(stdout!) : Match.Empty;
        if (!flag.Success)
        {
            return new RdsCheckParseResult(RdsStatus.Failed, null, null, null);
        }

        var isRds = flag.Groups["flag"].Value.Equals("TRUE", StringComparison.OrdinalIgnoreCase);
        if (!isRds)
        {
            return new RdsCheckParseResult(RdsStatus.NotRds, null, null, null);
        }

        DateTime? lastLogin = null;
        string? kind = null;
        var login = LastLoginRegex.Match(stdout!);
        if (login.Success
            && DateTime.TryParseExact(
                Regex.Replace(login.Groups["stamp"].Value, @"\s+", " "),
                "dd-MM-yyyy HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            lastLogin = DateTime.SpecifyKind(parsed, DateTimeKind.Unspecified);
            kind = NormalizeKind(login.Groups["kind"].Value);
        }

        string? user = null;
        var userMatch = UserRegex.Match(stdout!);
        if (userMatch.Success) user = userMatch.Groups["user"].Value.Trim();

        return new RdsCheckParseResult(RdsStatus.Rds, lastLogin, kind, user);
    }

    private static string? NormalizeKind(string raw)
    {
        var k = raw.Trim().ToLowerInvariant();
        if (k.Length == 0) return null;
        if (k == "rdp") return "rdp";
        if (k == "lokaal" || k == "local") return "local";
        return k;
    }
}
