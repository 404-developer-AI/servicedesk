using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Servicedesk.Api.Integrations;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Domain.Companies;
using Servicedesk.Infrastructure.Integrations.Telavox;
using Servicedesk.Infrastructure.Persistence.Companies;
using Servicedesk.Infrastructure.Secrets;
using Servicedesk.Infrastructure.Settings;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Telavox contact lookup (v0.1.35): invisible unless switched on with a
/// token, 401 + audit on a wrong token, name + company only on a hit.
public sealed class TelavoxContactLookupTests
{
    private const string Token = "test-token-0123456789abcdefghijklmnopqrstuv";
    private const string Url = "/api/public/telavox/contact-lookup?number=%2B32498123456";

    [Fact]
    public async Task Switched_off_answers_404_even_with_the_right_token()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Secrets.Set(ProtectedSecretKeys.TelavoxContactLookupToken, Token);
        var client = CreateClient(factory, out _);

        var response = await SendAsync(client, Url, Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Switched_on_without_a_token_answers_404()
    {
        using var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Telavox.ContactLookupEnabled, "true");
        var client = CreateClient(factory, out _);

        var response = await SendAsync(client, Url, Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    public async Task Missing_or_wrong_token_gets_401_and_is_audited(string? presented)
    {
        using var factory = Enabled();
        var client = CreateClient(factory, out var repo);
        repo.Rows.Add(Row("Jan", "Peeters", "Acme"));

        var response = await SendAsync(client, Url, presented);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("Peeters", await response.Content.ReadAsStringAsync());
        Assert.Contains(factory.Audit.Events, e => e.EventType == TelavoxEventTypes.ContactLookupDenied);
        Assert.Null(repo.LastNumber); // never reached the lookup
    }

    [Fact]
    public async Task Hit_returns_only_name_and_company()
    {
        using var factory = Enabled();
        var client = CreateClient(factory, out var repo);
        repo.Rows.Add(Row("Jan", "Peeters", "Acme"));

        var response = await SendAsync(client, Url, Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var props = json.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(new[] { "company", "label", "name" }, props);
        Assert.Equal("Jan Peeters · Acme", json.RootElement.GetProperty("label").GetString());
        Assert.Equal("Jan Peeters", json.RootElement.GetProperty("name").GetString());
        Assert.Equal("Acme", json.RootElement.GetProperty("company").GetString());
        Assert.Equal("+32498123456", repo.LastNumber);
    }

    [Fact]
    public async Task Unencoded_plus_in_the_query_still_matches()
    {
        using var factory = Enabled();
        var client = CreateClient(factory, out var repo);
        repo.Rows.Add(Row("Jan", "Peeters", "Acme"));

        // Telavox may substitute {system.caller} without encoding the '+'.
        var response = await SendAsync(client, "/api/public/telavox/contact-lookup?number=+32498123456", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("+32498123456", repo.LastNumber);
    }

    [Fact]
    public async Task No_match_answers_404()
    {
        using var factory = Enabled();
        var client = CreateClient(factory, out _);

        var response = await SendAsync(client, Url, Token);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Token_admin_routes_need_an_admin()
    {
        using var factory = new SecurityBaselineFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsync("/api/admin/integrations/telavox/contact-lookup/token", null);

        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"→ {(int)response.StatusCode}");
    }

    // ---- pure projection --------------------------------------------------

    [Fact]
    public void Several_contacts_at_one_company_show_the_company_not_a_guess()
    {
        var result = TelavoxContactLookup.Resolve(new[] { Row("Jan", "Peeters", "Acme"), Row("An", "Claes", "acme") });

        Assert.Equal(new TelavoxContactLookupResult(TelavoxContactLookup.MultipleContacts, "Acme"), result);
    }

    [Fact]
    public void Several_contacts_at_different_companies_show_no_company()
    {
        var result = TelavoxContactLookup.Resolve(new[] { Row("Jan", "Peeters", "Acme"), Row("An", "Claes", "Globex") });

        Assert.Equal(new TelavoxContactLookupResult(TelavoxContactLookup.MultipleContacts, ""), result);
    }

    [Fact]
    public void Label_skips_an_empty_part()
    {
        Assert.Equal("Multiple contacts", new TelavoxContactLookupResult("Multiple contacts", "").Label);
        Assert.Equal("Acme", new TelavoxContactLookupResult("", "Acme").Label);
    }

    [Fact]
    public void No_rows_resolve_to_nothing() => Assert.Null(TelavoxContactLookup.Resolve(Array.Empty<ContactPhoneLookupRow>()));

    [Theory]
    [InlineData(" 32498123456", "+32498123456")]
    [InlineData("+32498123456", "+32498123456")]
    [InlineData("0498123456", "0498123456")]
    [InlineData(null, "")]
    [InlineData("123456789012345678901234567890123", "")] // over the length cap
    public void Query_number_restores_a_decoded_plus(string? raw, string expected) =>
        Assert.Equal(expected, TelavoxContactLookupEndpoints.NormalizeQueryNumber(raw));

    // ---- helpers ----------------------------------------------------------

    private static SecurityBaselineFactory Enabled()
    {
        var factory = new SecurityBaselineFactory();
        factory.Settings.Set(SettingKeys.Telavox.ContactLookupEnabled, "true");
        factory.Settings.Set(SettingKeys.Telavox.DefaultCountryCode, "BE");
        factory.Secrets.Set(ProtectedSecretKeys.TelavoxContactLookupToken, Token);
        return factory;
    }

    private static HttpClient CreateClient(SecurityBaselineFactory factory, out FakeCompanyLookup repo)
    {
        var fake = CompanyLookupProxy.Create();
        repo = fake;
        var host = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ICompanyRepository>();
            services.AddSingleton((ICompanyRepository)fake.Proxy);
        }));
        return host.CreateClient();
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string url, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (token is not null) request.Headers.Add("Authorization", $"Bearer {token}");
        return client.SendAsync(request);
    }

    private static ContactPhoneLookupRow Row(string first, string last, string? company) => new(
        Guid.NewGuid(), "Member", first, last, $"{first}@example.test", "+32498123456", "", "", true,
        DateTime.UtcNow, DateTime.UtcNow, null, company is null ? null : Guid.NewGuid(), company, "primary");

    /// Only the phone lookup is implemented; any other repository call fails
    /// the test loudly.
    public sealed class FakeCompanyLookup
    {
        public List<ContactPhoneLookupRow> Rows { get; } = new();
        public string? LastNumber { get; set; }
        public object Proxy { get; set; } = null!;
    }

    public class CompanyLookupProxy : DispatchProxy
    {
        private FakeCompanyLookup _state = null!;

        public static FakeCompanyLookup Create()
        {
            var state = new FakeCompanyLookup();
            var proxy = DispatchProxy.Create<ICompanyRepository, CompanyLookupProxy>();
            ((CompanyLookupProxy)(object)proxy)._state = state;
            state.Proxy = proxy;
            return state;
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != nameof(ICompanyRepository.LookupContactsByPhoneE164Async))
                throw new NotSupportedException(method?.Name);
            _state.LastNumber = (string)args![0]!;
            return Task.FromResult<IReadOnlyList<ContactPhoneLookupRow>>(_state.Rows.ToList());
        }
    }
}
