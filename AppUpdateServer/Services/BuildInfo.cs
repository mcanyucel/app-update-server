using System.Reflection;

namespace AppUpdateServer.Services;

/// <summary>
/// What this build is, taken from the assembly rather than a file that has to
/// be kept up to date: the Docker build stamps the git tag into
/// <c>AssemblyInformationalVersion</c> as <c>1.2.3+&lt;commit sha&gt;</c>.
/// </summary>
public static class BuildInfo
{
    private const string DevelopmentVersion = "0.0.0-dev";

    static BuildInfo()
    {
        var informational = Assembly
            .GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        var parts = (informational ?? DevelopmentVersion).Split('+', 2);
        Version = parts[0];
        Commit = parts.Length == 2 && parts[1].Length > 0 ? parts[1] : null;
    }

    /// <summary>Semantic version, e.g. <c>1.2.3</c>, or <c>0.0.0-dev</c> outside CI.</summary>
    public static string Version { get; }

    /// <summary>Full commit sha the image was built from, when CI supplied one.</summary>
    public static string? Commit { get; }

    public static bool IsDevelopmentBuild => Version.StartsWith("0.0.0");

    public static string ShortCommit => Commit is { Length: >= 7 } ? Commit[..7] : "";

    /// <summary>What to show in the UI: <c>v1.2.3</c>, or <c>dev build</c> locally.</summary>
    public static string Display => IsDevelopmentBuild ? "dev build" : $"v{Version}";

    /// <summary>Version with the commit appended, for logs and tooltips.</summary>
    public static string DisplayWithCommit =>
        string.IsNullOrEmpty(ShortCommit) ? Display : $"{Display} · {ShortCommit}";
}
