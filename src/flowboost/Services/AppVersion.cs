using System.Reflection;

namespace flowboost.Services;

/// <summary>Single source of the running app's semantic version, derived from the assembly metadata set in flowboost.csproj.</summary>
internal static class AppVersion
{
    /// <summary>Version as shown to users and sent in User-Agent headers, e.g. "0.1.1" (build metadata after '+' removed).</summary>
    public static string Display { get; } = Resolve();

    private static string Resolve()
    {
        var assembly = typeof(AppVersion).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) return informational.Split('+', 2)[0];
        var version = assembly.GetName().Version;
        return version is null ? "unknown" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }
}
