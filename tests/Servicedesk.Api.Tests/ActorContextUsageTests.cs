using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Servicedesk.Api.Auth;
using Xunit;

namespace Servicedesk.Api.Tests;

/// The signed-in user's id is read in one place only (ActorContext), so
/// "who is calling" cannot drift between endpoints (CLAUDE.md → Code structure;
/// ROADMAP #21).
public sealed class ActorContextUsageTests
{
    private static readonly Regex ClaimRead = new(
        @"FindFirst(Value)?\s*\(\s*ClaimTypes\.NameIdentifier", RegexOptions.Compiled);

    [Fact]
    public void Only_ActorContext_reads_the_user_id_claim()
    {
        var root = RepoRoot();
        var offenders = Directory
            .EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(p => !p.EndsWith($"Auth{Path.DirectorySeparatorChar}ActorContext.cs", StringComparison.Ordinal))
            .Where(p => ClaimRead.IsMatch(File.ReadAllText(p)))
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Read the user id via ActorContext.GetUserId / TryGetUserId instead of the claim: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void GetUserId_returns_the_claim_and_throws_without_one()
    {
        var id = Guid.NewGuid();
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, id.ToString())], "test")),
        };
        Assert.Equal(id, ActorContext.GetUserId(http));
        Assert.Equal(id, ActorContext.TryGetUserId(http));

        var anonymous = new DefaultHttpContext();
        Assert.Null(ActorContext.TryGetUserId(anonymous));
        Assert.Throws<InvalidOperationException>(() => ActorContext.GetUserId(anonymous));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("")]
    public void TryGetUserId_rejects_malformed_or_empty_ids(string claim)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, claim)], "test"));
        Assert.Null(ActorContext.TryGetUserId(user));
        Assert.Throws<InvalidOperationException>(() => ActorContext.GetUserId(user));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Servicedesk.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
