using System.IO.Compression;
using System.Reflection;

namespace JointVentures.Launcher;

/// <summary>
/// Extracts the embedded plugin ZIP to a local cache directory.
/// Re-extracts when the launcher version changes.
/// </summary>
internal static class BundleExtractor
{
    private const string ResourceName = "plugin-bundle.zip";
    private const string VersionFile = ".plugin-version";

    /// <summary>
    /// Returns the cache directory path. Extracts the plugin DLL from
    /// embedded resources if needed.
    /// </summary>
    public static string EnsureExtracted()
    {
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JointVentures");

        var currentVersion = Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString() ?? "dev";

        var versionPath = Path.Combine(cacheDir, VersionFile);
        var pluginDir = Path.Combine(cacheDir, "BepInEx", "plugins", "COIJointVentures");

        // Check if already extracted and up-to-date
        if (File.Exists(Path.Combine(pluginDir, "COIJointVentures.dll"))
            && File.Exists(versionPath)
            && File.ReadAllText(versionPath).Trim() == currentVersion)
        {
            return cacheDir;
        }

        // Extract plugin
        Directory.CreateDirectory(pluginDir);

        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                "Embedded plugin bundle not found. The launcher was not built correctly.");

        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            if (entry.Length == 0) continue;
            entry.ExtractToFile(Path.Combine(pluginDir, entry.Name), overwrite: true);
        }

        File.WriteAllText(versionPath, currentVersion);
        return cacheDir;
    }
}
