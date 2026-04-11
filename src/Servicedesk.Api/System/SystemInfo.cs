using System.Reflection;

namespace Servicedesk.Api.System;

public sealed record SystemInfo(string Version, string Commit, DateTimeOffset BuildTime)
{
    public static SystemInfo Capture(Assembly assembly)
    {
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "0.0.0";

        var version = informational;
        var commit = "unknown";
        var plusIndex = informational.IndexOf('+');
        if (plusIndex >= 0)
        {
            version = informational[..plusIndex];
            commit = informational[(plusIndex + 1)..];
            if (commit.Length > 7)
            {
                commit = commit[..7];
            }
        }

        var buildTime = File.GetLastWriteTimeUtc(assembly.Location);
        return new SystemInfo(version, commit, new DateTimeOffset(buildTime, TimeSpan.Zero));
    }
}
