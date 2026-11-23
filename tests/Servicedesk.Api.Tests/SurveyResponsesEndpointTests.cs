using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Surveys;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Results-page "Responses" list (GET /api/surveys/{id}/responses): admin
/// only like the rest of the survey results, and the sort / comment filter
/// map onto the fixed enum values (unknown input falls back to defaults).
public sealed class SurveyResponsesEndpointTests : IClassFixture<SharedHostFixture>
{
    private readonly SharedHostFixture _hosts;

    // The host (and so the stub repository) is shared by the class; the
    // recorder is cleared per test.
    private static readonly List<(Guid, SurveyResponseSort, SurveyResponseCommentFilter, int, int)> Recorded = new();

    public SurveyResponsesEndpointTests(SharedHostFixture hosts)
    {
        _hosts = hosts;
        hosts.Reset();
        Recorded.Clear();
    }

    private static readonly Guid SurveyId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");

    [Theory]
    [InlineData("Agent")]
    [InlineData("Customer")]
    public async Task Non_admin_is_refused(string role)
    {
        var factory = _hosts.Base;
        var host = WithStub(out var calls);
        var client = await factory.SignedInClientAsync(role, host, amr: "pwd");

        var response = await client.GetAsync($"/api/surveys/{SurveyId}/responses");

        Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Admin_gets_page_with_requested_sort_and_filter()
    {
        var factory = _hosts.Base;
        var host = WithStub(out var calls);
        var client = await factory.SignedInClientAsync("Admin", host);

        var response = await client.GetAsync(
            $"/api/surveys/{SurveyId}/responses?sort=ScoreAsc&comment=WithComment&offset=50&limit=50");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(calls);
        Assert.Equal((SurveyId, SurveyResponseSort.ScoreAsc, SurveyResponseCommentFilter.WithComment, 50, 50), call);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, body.GetProperty("total").GetInt32());
        var item = Assert.Single(body.GetProperty("items").EnumerateArray().ToList());
        Assert.Equal(42.86m, item.GetProperty("scorePct").GetDecimal());
        Assert.Equal("Could be faster", item.GetProperty("commentPreview").GetString());
    }

    [Fact]
    public async Task Unknown_sort_and_filter_fall_back_to_defaults_and_limit_is_clamped()
    {
        var factory = _hosts.Base;
        var host = WithStub(out var calls);
        var client = await factory.SignedInClientAsync("Admin", host);

        var response = await client.GetAsync(
            $"/api/surveys/{SurveyId}/responses?sort=score_asc;DROP&comment=99&offset=-5&limit=100000");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(calls);
        Assert.Equal((SurveyId, SurveyResponseSort.SubmittedDesc, SurveyResponseCommentFilter.All, 0, 200), call);
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithStub(
        out List<(Guid, SurveyResponseSort, SurveyResponseCommentFilter, int, int)> calls)
    {
        var recorded = Recorded;
        calls = recorded;
        var repo = InsightsRewindTests.PartialFake<ISurveyInvitationRepository>.Create((method, args) =>
        {
            if (method.Name != nameof(ISurveyInvitationRepository.ListResponsesForSurveyAsync))
                throw new NotSupportedException(method.Name);
            recorded.Add(((Guid)args![0]!, (SurveyResponseSort)args[1]!,
                (SurveyResponseCommentFilter)args[2]!, (int)args[3]!, (int)args[4]!));
            return Task.FromResult(new SurveyResponsePage(1, new[]
            {
                new SurveyResponseListRow(
                    Guid.NewGuid(), Guid.NewGuid(), 1042, "Printer offline", "jane@example.test",
                    "Jane Example", "Example Ltd", DateTime.UtcNow, 42.86m, "Could be faster"),
            }));
        });
        return _hosts.Host("survey-repo", builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ISurveyInvitationRepository>();
            services.AddSingleton(repo);
        }));
    }
}
