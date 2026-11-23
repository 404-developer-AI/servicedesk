using Microsoft.AspNetCore.Mvc.Testing;
using Servicedesk.Api.Auth;
using Servicedesk.Infrastructure.Settings;

namespace Servicedesk.Api.Tests.TestInfrastructure;

/// Signed-in HttpClients for host tests: a fake session for a user with the
/// given role, the session cookie, and a matching double-submit CSRF pair.
public static class SignedInClient
{
    /// A client signed in as <paramref name="role"/>. <paramref name="host"/>
    /// defaults to the factory itself (pass a derived host for extra fakes);
    /// <paramref name="amr"/> defaults to <c>pwd+mfa</c> for customers (the
    /// portal requires it) and <c>pwd</c> for staff.
    public static async Task<HttpClient> SignedInClientAsync(
        this SecurityBaselineFactory factory,
        string role,
        WebApplicationFactory<Program>? host = null,
        Guid? userId = null,
        string? amr = null)
    {
        var id = userId ?? Guid.NewGuid();
        factory.Sessions.Roles[id] = role;
        var sessionId = await factory.Sessions.CreateAsync(
            id, ip: null, userAgent: null, lifetime: TimeSpan.FromHours(1),
            amr: amr ?? (role == "Customer" ? "pwd+mfa" : "pwd"));
        return await factory.SessionClientAsync(sessionId, host);
    }

    /// A client carrying an existing session's cookie (plus CSRF pair).
    public static async Task<HttpClient> SessionClientAsync(
        this SecurityBaselineFactory factory, Guid sessionId, WebApplicationFactory<Program>? host = null)
    {
        var cookieName = await factory.Settings.GetAsync<string>(SettingKeys.Security.SessionCookieName);
        var csrf = DoubleSubmitCsrfMiddleware.GenerateToken();
        var client = (host ?? factory).CreateClient();
        client.DefaultRequestHeaders.Add(
            "Cookie", $"{cookieName}={sessionId}; {DoubleSubmitCsrfMiddleware.CookieName}={csrf}");
        client.DefaultRequestHeaders.Add(DoubleSubmitCsrfMiddleware.HeaderName, csrf);
        return client;
    }
}
