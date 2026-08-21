using Microsoft.AspNetCore.Http;
using Servicedesk.Api.Auth;
using Servicedesk.Api.Tests.TestInfrastructure;
using Servicedesk.Infrastructure.Auth;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class DoubleSubmitCsrfMiddlewareTests
{
    private static (DefaultHttpContext Ctx, FakeAuditLogger Audit) BuildContext(
        string method, string path, string? cookie, string? header, string? cookieName = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (cookie is not null)
        {
            ctx.Request.Headers["Cookie"] = $"{cookieName ?? DoubleSubmitCsrfMiddleware.CookieName}={cookie}";
        }
        if (header is not null)
        {
            ctx.Request.Headers[DoubleSubmitCsrfMiddleware.HeaderName] = header;
        }
        ctx.Response.Body = new MemoryStream();
        return (ctx, new FakeAuditLogger());
    }

    private static DoubleSubmitCsrfMiddleware CreateMiddleware(Action<HttpContext>? onCall = null)
    {
        return new DoubleSubmitCsrfMiddleware(ctx =>
        {
            onCall?.Invoke(ctx);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Get_Request_Passes_Without_Token()
    {
        var (ctx, audit) = BuildContext("GET", "/api/audit", cookie: null, header: null);
        var called = false;
        var middleware = CreateMiddleware(_ => called = true);

        await middleware.InvokeAsync(ctx, audit);

        Assert.True(called);
        Assert.NotEqual(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Post_With_Matching_Cookie_And_Header_Passes()
    {
        var token = DoubleSubmitCsrfMiddleware.GenerateToken();
        var (ctx, audit) = BuildContext("POST", "/api/audit", cookie: token, header: token);
        var called = false;
        var middleware = CreateMiddleware(_ => called = true);

        await middleware.InvokeAsync(ctx, audit);

        Assert.True(called);
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task Post_With_Mismatched_Token_Returns_403_And_Audits()
    {
        var (ctx, audit) = BuildContext("POST", "/api/audit", cookie: "aaa", header: "bbb");
        var called = false;
        var middleware = CreateMiddleware(_ => called = true);

        await middleware.InvokeAsync(ctx, audit);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
        Assert.Contains(audit.Events, e => e.EventType == AuthEventTypes.CsrfRejected);
    }

    [Fact]
    public async Task Post_With_Missing_Token_Is_Rejected()
    {
        var (ctx, audit) = BuildContext("POST", "/api/audit", cookie: null, header: null);
        var called = false;
        var middleware = CreateMiddleware(_ => called = true);

        await middleware.InvokeAsync(ctx, audit);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    // v0.1.1 — the portal realm carries its own cookie. Before the split a
    // portal logout (and the Exit of an admin's shadow view) deleted the staff
    // token, after which every agent write — logout included — 403'd.
    [Theory]
    [InlineData("/api/portal/auth/logout")]
    [InlineData("/api/portal/tickets/abc/reply")]
    public async Task Portal_Paths_Use_The_Portal_Cookie(string path)
    {
        var token = DoubleSubmitCsrfMiddleware.GenerateToken();
        var (ctx, audit) = BuildContext(
            "POST", path, cookie: token, header: token, cookieName: DoubleSubmitCsrfMiddleware.PortalCookieName);
        var called = false;
        var middleware = CreateMiddleware(_ => called = true);

        await middleware.InvokeAsync(ctx, audit);

        Assert.True(called);
        Assert.Empty(audit.Events);
    }

    [Fact]
    public async Task Portal_Path_Rejects_The_Staff_Cookie()
    {
        var token = DoubleSubmitCsrfMiddleware.GenerateToken();
        var (ctx, audit) = BuildContext("POST", "/api/portal/auth/logout", cookie: token, header: token);
        var called = false;
        var middleware = CreateMiddleware(_ => called = true);

        await middleware.InvokeAsync(ctx, audit);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    // /api/portal/admin/* is called from the agent app, so it stays on the
    // staff cookie even though it sits under the portal prefix.
    [Fact]
    public async Task Portal_Admin_Path_Uses_The_Staff_Cookie()
    {
        var token = DoubleSubmitCsrfMiddleware.GenerateToken();
        var (ctx, audit) = BuildContext("POST", "/api/portal/admin/accounts/x/approve", cookie: token, header: token);
        var called = false;
        var middleware = CreateMiddleware(_ => called = true);

        await middleware.InvokeAsync(ctx, audit);

        Assert.True(called);
        Assert.Equal(DoubleSubmitCsrfMiddleware.CookieName,
            DoubleSubmitCsrfMiddleware.CookieNameFor("/api/portal/admin/accounts/x/approve"));
    }

    [Fact]
    public async Task Staff_Path_Rejects_The_Portal_Cookie()
    {
        var token = DoubleSubmitCsrfMiddleware.GenerateToken();
        var (ctx, audit) = BuildContext(
            "POST", "/api/auth/logout", cookie: token, header: token,
            cookieName: DoubleSubmitCsrfMiddleware.PortalCookieName);
        var called = false;
        var middleware = CreateMiddleware(_ => called = true);

        await middleware.InvokeAsync(ctx, audit);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status403Forbidden, ctx.Response.StatusCode);
    }

    [Theory]
    [InlineData("/api/auth/login")]
    [InlineData("/api/auth/setup/create-admin")]
    public async Task Exempt_Paths_Skip_Check(string path)
    {
        var (ctx, audit) = BuildContext("POST", path, cookie: null, header: null);
        var called = false;
        var middleware = CreateMiddleware(_ => called = true);

        await middleware.InvokeAsync(ctx, audit);

        Assert.True(called);
    }
}
