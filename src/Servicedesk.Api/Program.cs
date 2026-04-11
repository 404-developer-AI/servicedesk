using System.Reflection;
using Microsoft.OpenApi.Models;
using Servicedesk.Api.System;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddEnvironmentVariables(prefix: "SERVICEDESK_");

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Servicedesk API", Version = "v1" });
});

var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? Environment.GetEnvironmentVariable("SERVICEDESK_ConnectionStrings__Postgres");

if (string.IsNullOrWhiteSpace(connectionString))
{
    builder.Logging.AddConsole();
    Console.WriteLine("[startup] WARNING: no PostgreSQL connection string configured (set ConnectionStrings__Postgres or SERVICEDESK_ConnectionStrings__Postgres).");
}
else
{
    Console.WriteLine("[startup] PostgreSQL connection string loaded from configuration.");
}

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

var systemInfo = SystemInfo.Capture(Assembly.GetExecutingAssembly());

app.MapGet("/api/system/version", () => Results.Ok(new
{
    version = systemInfo.Version,
    commit = systemInfo.Commit,
    buildTime = systemInfo.BuildTime
}))
.WithName("GetSystemVersion")
.WithOpenApi();

app.MapGet("/api/system/time", () =>
{
    var nowUtc = DateTimeOffset.UtcNow;
    return Results.Ok(new
    {
        utc = nowUtc,
        timezone = TimeZoneInfo.Local.Id,
        offsetMinutes = (int)TimeZoneInfo.Local.GetUtcOffset(nowUtc.UtcDateTime).TotalMinutes
    });
})
.WithName("GetSystemTime")
.WithOpenApi();

app.Run();

public partial class Program { }

