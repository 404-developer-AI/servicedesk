using Servicedesk.Api.Integrations;
using Servicedesk.Infrastructure.Integrations.Trmm;
using Xunit;

namespace Servicedesk.Api.Tests;

/// v0.1.10 — Remote Desktop sync plumbing: the per-agent checks parser,
/// the check matcher and the CSV export shape.
public sealed class TrmmRdsSyncTests
{
    // ---- ParseAgentChecks -------------------------------------------------

    [Fact]
    public void ParseAgentChecks_reads_embedded_check_result()
    {
        const string body = """
        [
          {
            "id": 42,
            "name": "Check-RDS",
            "check_type": "script",
            "readable_desc": "Script check: Check-RDS",
            "script": 7,
            "check_result": {
              "status": "passing",
              "stdout": "Remote Desktop server: TRUE\nLaatste login : 11-09-2026 09:07:19 (RDP)\nGebruiker     : DATAWOLK\\advizo\n",
              "stderr": "",
              "retcode": 0,
              "last_run": "2026-09-11T07:15:02.123456Z"
            }
          },
          {
            "id": 43,
            "name": "Disk C:",
            "check_type": "diskspace",
            "check_result": {}
          }
        ]
        """;

        var checks = TrmmApiClient.ParseAgentChecks(body);

        Assert.Equal(2, checks.Count);
        var rds = checks[0];
        Assert.Equal(42, rds.Id);
        Assert.Equal("script", rds.CheckType);
        Assert.Equal(7, rds.ScriptId);
        Assert.Equal("passing", rds.ResultStatus);
        Assert.Equal(0, rds.Retcode);
        Assert.Contains("TRUE", rds.Stdout);
        Assert.Equal(new DateTime(2026, 9, 11, 7, 15, 2, 123, DateTimeKind.Utc).AddTicks(4560), rds.LastRunUtc);

        var disk = checks[1];
        Assert.Null(disk.ResultStatus);
        Assert.Null(disk.Retcode);
        Assert.Null(disk.LastRunUtc);
    }

    [Fact]
    public void ParseAgentChecks_tolerates_empty_or_non_array_bodies()
    {
        Assert.Empty(TrmmApiClient.ParseAgentChecks("[]"));
        Assert.Empty(TrmmApiClient.ParseAgentChecks("{}"));
        Assert.Empty(TrmmApiClient.ParseAgentChecks("""{"results": []}"""));
    }

    // ---- SelectRdsCheck ---------------------------------------------------

    private static TrmmAgentCheck Check(long id, string? name, string? type, string? desc, string? stdout, DateTime? lastRun = null) =>
        new(id, name, type, desc, null, "passing", stdout, null, 0, lastRun);

    [Fact]
    public void SelectRdsCheck_matches_name_case_insensitively_on_name_or_description()
    {
        var checks = new List<TrmmAgentCheck>
        {
            Check(1, "Disk usage", "diskspace", "Disk space check", null),
            Check(2, "rds role", "script", "Script check: check-rds.ps1", "Remote Desktop server: TRUE"),
            Check(3, "Other script", "script", "Script check: Cleanup", "done"),
        };

        var picked = TrmmRdsSyncService.SelectRdsCheck(checks, "Check-RDS");

        Assert.NotNull(picked);
        Assert.Equal(2, picked!.Id);
    }

    [Fact]
    public void SelectRdsCheck_falls_back_to_output_marker_when_name_misses()
    {
        var checks = new List<TrmmAgentCheck>
        {
            Check(5, "Renamed check", "script", "Script check: Renamed", "Remote Desktop server: FALSE"),
        };

        var picked = TrmmRdsSyncService.SelectRdsCheck(checks, "Check-RDS");

        Assert.NotNull(picked);
        Assert.Equal(5, picked!.Id);
    }

    [Fact]
    public void SelectRdsCheck_ignores_non_script_checks_even_when_name_matches()
    {
        var checks = new List<TrmmAgentCheck>
        {
            Check(9, "Check-RDS service", "winsvc", "Service check: Check-RDS", null),
        };

        Assert.Null(TrmmRdsSyncService.SelectRdsCheck(checks, "Check-RDS"));
    }

    [Fact]
    public void SelectRdsCheck_prefers_most_recently_run_candidate()
    {
        var checks = new List<TrmmAgentCheck>
        {
            Check(1, "Check-RDS", "script", null, "Remote Desktop server: FALSE", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
            Check(2, "Check-RDS", "script", null, "Remote Desktop server: TRUE", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)),
        };

        var picked = TrmmRdsSyncService.SelectRdsCheck(checks, "Check-RDS");

        Assert.Equal(2, picked!.Id);
    }

    [Fact]
    public void SelectRdsCheck_returns_null_when_nothing_matches()
    {
        var checks = new List<TrmmAgentCheck>
        {
            Check(1, "Cleanup", "script", "Script check: Cleanup", "ok"),
        };

        Assert.Null(TrmmRdsSyncService.SelectRdsCheck(checks, "Check-RDS"));
        Assert.Null(TrmmRdsSyncService.SelectRdsCheck(Array.Empty<TrmmAgentCheck>(), ""));
    }

    // ---- CSV export --------------------------------------------------------

    [Fact]
    public void BuildCsv_starts_with_excel_separator_directive_and_quotes_multiline_output()
    {
        var rows = new[]
        {
            new RemoteDesktopRow
            {
                Hostname = "SRV-01",
                ClientName = "[ACME] Acme, Inc.",
                ClientCode = "ACME",
                CompanyName = "Acme",
                SiteName = "Main",
                OsName = "Windows Server 2022 Standard",
                Status = RdsStatus.Rds,
                LastLoginLocal = new DateTime(2026, 9, 11, 9, 7, 19),
                LastLoginKind = "rdp",
                LastLoginUser = "ACME\\jane",
                LastRunUtc = new DateTime(2026, 9, 11, 7, 15, 2, DateTimeKind.Utc),
                Retcode = 0,
                Stdout = "Remote Desktop server: TRUE\nLaatste login : 11-09-2026 09:07:19 (RDP)",
            },
        };

        var csv = RemoteDesktopEndpoints.BuildCsv(rows);
        var lines = csv.Split('\n');

        Assert.Equal("sep=,", lines[0].TrimEnd('\r'));
        Assert.StartsWith("client_code,client_name,company,hostname,os,site,status,", lines[1]);
        Assert.Contains("ACME,\"Acme, Inc.\",Acme,SRV-01,Windows Server 2022 Standard,Main,Remote Desktop,2026-09-11 09:07:19,rdp,ACME\\jane,2026-09-11 07:15:02,0,\"Remote Desktop server: TRUE", csv);
    }

    [Fact]
    public void StripCode_drops_bracketed_prefix_only()
    {
        Assert.Equal("Acme Inc.", RemoteDesktopEndpoints.StripCode("[ACME] Acme Inc."));
        Assert.Equal("Plain name", RemoteDesktopEndpoints.StripCode("Plain name"));
        Assert.Equal("[ACME]", RemoteDesktopEndpoints.StripCode("[ACME]"));
    }

    [Theory]
    [InlineData(RdsStatus.Rds, "Remote Desktop")]
    [InlineData(RdsStatus.NotRds, "No Remote Desktop")]
    [InlineData(RdsStatus.Failed, "Check failed")]
    [InlineData(RdsStatus.NoCheck, "No check")]
    [InlineData(RdsStatus.Pending, "No result yet")]
    [InlineData(RdsStatus.Error, "Unreachable")]
    [InlineData(null, "Not checked")]
    public void StatusLabel_covers_every_status(string? status, string expected)
    {
        Assert.Equal(expected, RemoteDesktopEndpoints.StatusLabel(status));
    }
}
