using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Servicedesk.Api.System;
using Xunit;

namespace Servicedesk.Api.Tests;

public sealed class ClientVersionGateMiddlewareTests
{
    private const string ServerVersion = "0.0.96";

    private static DefaultHttpContext BuildContext(string method, string path, string? clientVersion)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Path = path;
        if (clientVersion is not null)
        {
            ctx.Request.Headers[ClientVersionGateMiddleware.HeaderName] = clientVersion;
        }
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static ClientVersionGateMiddleware CreateMiddleware(Action<HttpContext>? onCall = null)
    {
        var systemInfo = new SystemInfo(ServerVersion, "abc1234", DateTimeOffset.UnixEpoch);
        return new ClientVersionGateMiddleware(ctx =>
        {
            onCall?.Invoke(ctx);
            return Task.CompletedTask;
        }, systemInfo);
    }

    private static Task Invoke(ClientVersionGateMiddleware middleware, DefaultHttpContext ctx)
        => middleware.InvokeAsync(ctx, NullLogger<ClientVersionGateMiddleware>.Instance);

    [Fact]
    public async Task Get_With_Outdated_Version_Passes()
    {
        var ctx = BuildContext("GET", "/api/tickets", "0.0.1");
        var called = false;
        var middleware = CreateMiddleware(_ => called = true);

        await Invoke(middleware, ctx);

        Assert.True(called);
        Assert.NotEqual(StatusCodes.Status426UpgradeRequired, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Post_With_Matching_Version_Passes()
    {
        var ctx = BuildContext("POST", "/api/tickets", ServerVersion);
        var called = false;
        var middleware = CreateMiddleware(_ => called = true);

        await Invoke(middleware, ctx);

        Assert.True(called);
    }

    [Fact]
    public async Task Post_Without_Header_Passes()
    {
        var ctx = BuildContext("POST", "/api/tickets", clientVersion: null);
        var called = false;
        var middleware = CreateMiddleware(_ => called = true);

        await Invoke(middleware, ctx);

        Assert.True(called);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task Write_With_Outdated_Version_Returns_426(string method)
    {
        var ctx = BuildContext(method, "/api/tickets", "0.0.1");
        var called = false;
        var middleware = CreateMiddleware(_ => called = true);

        await Invoke(middleware, ctx);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status426UpgradeRequired, ctx.Response.StatusCode);

        ctx.Response.Body.Position = 0;
        using var body = await JsonDocument.ParseAsync(ctx.Response.Body);
        Assert.Equal("client_version_outdated", body.RootElement.GetProperty("error").GetString());
        Assert.Equal(ServerVersion, body.RootElement.GetProperty("serverVersion").GetString());
    }

    [Fact]
    public async Task Hub_Negotiate_With_Outdated_Version_Passes()
    {
        var ctx = BuildContext("POST", "/hubs/presence/negotiate", "0.0.1");
        var called = false;
        var middleware = CreateMiddleware(_ => called = true);

        await Invoke(middleware, ctx);

        Assert.True(called);
    }
}
