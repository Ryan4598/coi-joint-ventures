using Microsoft.Win32;

namespace JointVentures.Launcher;

/// <summary>
/// Locates a Steam game's install directory by reading the registry and
/// parsing Steam's libraryfolders.vdf to find all library paths.
/// </summary>
internal static class SteamLocator
{
    private const int CoiAppId = 1594320;
    private const string CoiFolderName = "Captain of Industry";
    private const string CoiExeName = "Captain of Industry.exe";

    public static string? FindGameDir()
    {
        var steamPath = GetSteamInstallPath();
        if (steamPath is null)
            return null;

        var libraryFolders = GetLibraryFolders(steamPath);

        foreach (var folder in libraryFolders)
        {
            var candidate = Path.Combine(folder, "steamapps", "common", CoiFolderName);
            if (File.Exists(Path.Combine(candidate, CoiExeName)))
                return candidate;
        }

        return null;
    }

    private static string? GetSteamInstallPath()
    {
        // Try 64-bit view first, then 32-bit
        string[] keys =
        [
            @"SOFTWARE\Valve\Steam",
            @"SOFTWARE\WOW6432Node\Valve\Steam"
        ];

        foreach (var subkey in keys)
        {
            using var key = Registry.LocalMachine.OpenSubKey(subkey);
            if (key?.GetValue("InstallPath") is string path && Directory.Exists(path))
                return path;
        }

        // Fallback: check current-user registry (Steam sets this too)
        using var cuKey = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
        if (cuKey?.GetValue("SteamPath") is string cuPath)
        {
            // SteamPath uses forward slashes
            cuPath = cuPath.Replace('/', '\\');
            if (Directory.Exists(cuPath))
                return cuPath;
        }

        return null;
    }

    private static List<string> GetLibraryFolders(string steamPath)
    {
        var folders = new List<string> { steamPath };

        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath))
            return folders;

        // Simple VDF parser — extract "path" values from libraryfolders.vdf.
        // The format is:  "path"		"D:\\Games\\Steam"
        foreach (var line in File.ReadLines(vdfPath))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
                continue;

            // Split on the second quoted value
            var parts = trimmed.Split('"');
            // Expected: "", "path", "", "", "D:\\Games\\Steam", ""
            if (parts.Length >= 5)
            {
                var libPath = parts[3].Replace("\\\\", "\\");
                if (Directory.Exists(libPath) && !folders.Contains(libPath, StringComparer.OrdinalIgnoreCase))
                    folders.Add(libPath);
            }
        }

        return folders;
    }
}
