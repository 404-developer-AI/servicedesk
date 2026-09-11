using Servicedesk.Infrastructure.Integrations.Trmm;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Pins the classification of the customer's Check-RDS.ps1 output
/// (v0.1.10). The script prints a Dutch "Laatste login" / "Gebruiker"
/// block; the parser accepts the English labels too so a later script
/// edit does not silently drop the login columns.
public sealed class RdsCheckOutputParserTests
{
    [Fact]
    public void True_with_rdp_login_parses_status_login_and_user()
    {
        const string stdout = "Remote Desktop server: TRUE\r\nLaatste login : 11-09-2026 09:07:19 (RDP)\r\nGebruiker     : DATAWOLK\\advizo\r\n";

        var r = RdsCheckOutputParser.Parse(stdout, 0, "passing");

        Assert.Equal(RdsStatus.Rds, r.Status);
        Assert.Equal(new DateTime(2026, 9, 11, 9, 7, 19), r.LastLoginLocal);
        Assert.Equal(DateTimeKind.Unspecified, r.LastLoginLocal!.Value.Kind);
        Assert.Equal("rdp", r.LastLoginKind);
        Assert.Equal("DATAWOLK\\advizo", r.LastLoginUser);
    }

    [Fact]
    public void Local_login_kind_is_normalised()
    {
        const string stdout = "Remote Desktop server: TRUE\nLaatste login : 01-02-2026 23:59:00 (Lokaal)\nGebruiker     : SRV01\\admin";

        var r = RdsCheckOutputParser.Parse(stdout, 0, "passing");

        Assert.Equal(RdsStatus.Rds, r.Status);
        Assert.Equal("local", r.LastLoginKind);
        Assert.Equal("SRV01\\admin", r.LastLoginUser);
    }

    [Fact]
    public void English_labels_are_accepted()
    {
        const string stdout = "Remote Desktop server: TRUE\nLast login : 05-03-2026 08:00:00 (RDP)\nUser : CORP\\jane";

        var r = RdsCheckOutputParser.Parse(stdout, 0, "passing");

        Assert.Equal(RdsStatus.Rds, r.Status);
        Assert.Equal(new DateTime(2026, 3, 5, 8, 0, 0), r.LastLoginLocal);
        Assert.Equal("CORP\\jane", r.LastLoginUser);
    }

    [Fact]
    public void True_without_login_block_still_counts_as_rds()
    {
        // The Security-log read is "nice to have" in the script; a
        // non-admin run prints a placeholder line instead of a timestamp.
        const string stdout = "Remote Desktop server: TRUE\nLaatste login : niet beschikbaar (geen toegang tot Security-log?).";

        var r = RdsCheckOutputParser.Parse(stdout, 0, "passing");

        Assert.Equal(RdsStatus.Rds, r.Status);
        Assert.Null(r.LastLoginLocal);
        Assert.Null(r.LastLoginUser);
    }

    [Fact]
    public void False_is_hidden_status()
    {
        var r = RdsCheckOutputParser.Parse("Remote Desktop server: FALSE\n", 0, "passing");

        Assert.Equal(RdsStatus.NotRds, r.Status);
        Assert.Null(r.LastLoginLocal);
    }

    [Fact]
    public void Flag_match_is_case_insensitive()
    {
        Assert.Equal(RdsStatus.Rds, RdsCheckOutputParser.Parse("remote desktop server: true", 0, "passing").Status);
        Assert.Equal(RdsStatus.NotRds, RdsCheckOutputParser.Parse("REMOTE DESKTOP SERVER: False", 0, "passing").Status);
    }

    [Fact]
    public void Exit_one_without_flag_line_is_failed()
    {
        // Script threw before printing the flag: stdout empty, stderr
        // carries "Script gefaald: …", TRMM marks the check failing.
        var r = RdsCheckOutputParser.Parse("", 1, "failing");

        Assert.Equal(RdsStatus.Failed, r.Status);
    }

    [Fact]
    public void Unrecognisable_output_with_exit_zero_is_failed_not_pending()
    {
        var r = RdsCheckOutputParser.Parse("Something else entirely", 0, "passing");

        Assert.Equal(RdsStatus.Failed, r.Status);
    }

    [Fact]
    public void No_result_yet_is_pending()
    {
        Assert.Equal(RdsStatus.Pending, RdsCheckOutputParser.Parse(null, null, "pending").Status);
        Assert.Equal(RdsStatus.Pending, RdsCheckOutputParser.Parse("", null, null).Status);
    }

    [Fact]
    public void Ran_but_printed_nothing_is_failed()
    {
        // A result exists (retcode set) but no output: the script always
        // prints the flag line, so this is a failure, not "pending".
        Assert.Equal(RdsStatus.Failed, RdsCheckOutputParser.Parse("", 0, "passing").Status);
    }
}
