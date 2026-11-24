using Xunit;

namespace Servicedesk.Api.Tests.TestInfrastructure;

/// The repository on disk, for guard tests that scan source files (file-size
/// budget, hard-rule guards).
public static class RepoFiles
{
    /// The repository root (the folder holding <c>Servicedesk.slnx</c>).
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Servicedesk.slnx")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// Files under <paramref name="relativeDir"/> matching <paramref name="pattern"/>,
    /// without build output (<c>bin</c>, <c>obj</c>) or <c>node_modules</c>.
    public static IEnumerable<string> Enumerate(string relativeDir, string pattern) =>
        Directory.EnumerateFiles(Path.Combine(Root(), relativeDir), pattern, SearchOption.AllDirectories)
            .Where(p => !IsGenerated(p));

    /// Repository-relative path with forward slashes ("src/Servicedesk.Api/…").
    public static string Relative(string path) => Path.GetRelativePath(Root(), path).Replace('\\', '/');

    private static bool IsGenerated(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}bin{sep}") || path.Contains($"{sep}obj{sep}") || path.Contains($"{sep}node_modules{sep}");
    }
}
