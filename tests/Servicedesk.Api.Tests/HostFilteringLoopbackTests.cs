using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Servicedesk.Api.Tests;

/// Regression guard for v0.0.46-48. install.sh pins SERVICEDESK_AllowedHosts
/// to the public domain so Kestrel's HostFiltering rejects forged Host headers,
/// but the Docker HEALTHCHECK calls localhost from inside the container.
/// Program.cs must therefore always include loopback in the allow-list without
/// dropping the configured domain and without throwing on the fixed-size
/// string[] shape that HostFilteringOptionsSetup assigns when reading from
/// config.
public sealed class HostFilteringLoopbackTests
{
    [Fact]
    public void PostConfigure_PreservesConfiguredDomain_AndAppendsLoopback()
    {
        // Reproduces the production registration shape:
        //   1. The framework's HostFilteringOptionsSetup assigns
        //      options.AllowedHosts = configValue.Split(';') — a fixed-size
        //      string[], not a List<string>.
        //   2. Program.cs's PostConfigure must append loopback without
        //      throwing on .Add (fails on string[]) and without dropping the
        //      configured domain.
        // v0.0.46 used Configure<> which ran first and made the framework
        // skip reading config → domain dropped. v0.0.47 used PostConfigure
        // but mutated the string[] in place → NotSupportedException at
        // startup. v0.0.48 reassigns a fresh List<string>.
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton<IConfigureOptions<HostFilteringOptions>>(
            new MimicHostFilteringConfigSetup("helpdesk.example.com"));
        services.PostConfigure<HostFilteringOptions>(o =>
        {
            var merged = new List<string>(o.AllowedHosts);
            foreach (var loopback in new[] { "localhost", "127.0.0.1", "[::1]" })
            {
                if (!merged.Contains(loopback)) merged.Add(loopback);
            }
            o.AllowedHosts = merged;
        });

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HostFilteringOptions>>().Value;

        Assert.Contains("helpdesk.example.com", options.AllowedHosts);
        Assert.Contains("localhost", options.AllowedHosts);
        Assert.Contains("127.0.0.1", options.AllowedHosts);
        Assert.Contains("[::1]", options.AllowedHosts);
    }

    /// Mirrors ASP.NET's internal HostFilteringOptionsSetup: assigns a
    /// fixed-size string[] (not List<string>) when reading from config.
    private sealed class MimicHostFilteringConfigSetup : IConfigureOptions<HostFilteringOptions>
    {
        private readonly string _value;
        public MimicHostFilteringConfigSetup(string value) => _value = value;

        public void Configure(HostFilteringOptions options)
        {
            if (options.AllowedHosts is null || options.AllowedHosts.Count == 0)
            {
                options.AllowedHosts = _value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
        }
    }
}
