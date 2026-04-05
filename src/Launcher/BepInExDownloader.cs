using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace JointVentures.Launcher;

/// <summary>
/// Downloads BepInEx 5.x from GitHub releases and extracts the needed files
/// (core DLLs + doorstop proxy) to a local cache directory.
/// </summary>
internal static class BepInExDownloader
{
    private const string ReleasesUrl = "https://api.github.com/repos/BepInEx/BepInEx/releases";
    private const string AssetPattern = "BepInEx_win_x64_";
    private const string VersionFile = ".bepinex-version";

    /// <summary>
    /// Ensures BepInEx is downloaded and extracted to the given cache directory.
    /// Returns true if files are ready, false on failure.
    /// </summary>
    public static async Task<bool> EnsureDownloadedAsync(string cacheDir, Action<string> log)
    {
        var coreDir = Path.Combine(cacheDir, "BepInEx", "core");
        var doorstopDll = Path.Combine(cacheDir, "BepInEx", "doorstop", "winhttp.dll");
        var versionPath = Path.Combine(cacheDir, VersionFile);

        // Already cached?
        if (File.Exists(Path.Combine(coreDir, "BepInEx.Preloader.dll"))
            && File.Exists(doorstopDll)
            && File.Exists(versionPath))
        {
            log($"BepInEx {File.ReadAllText(versionPath).Trim()} (cached)");
            return true;
        }

        log("Fetching latest BepInEx 5.x release info...");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("JointVentures-Launcher");
        http.Timeout = TimeSpan.FromSeconds(30);

        // ── Find the latest 5.x release and its win_x64 asset URL ──
        var (tag, downloadUrl) = await FindLatest5xReleaseAsync(http);
        if (downloadUrl is null)
        {
            log("ERROR: Could not find a BepInEx 5.x release with a win_x64 asset.");
            return false;
        }

        log($"Downloading BepInEx {tag}...");

        // ── Download the ZIP ──
        byte[] zipBytes;
        try
        {
            zipBytes = await http.GetByteArrayAsync(downloadUrl);
        }
        catch (Exception ex)
        {
            log($"ERROR: Download failed: {ex.Message}");
            return false;
        }

        log($"Extracting ({zipBytes.Length / 1024} KB)...");

        // ── Extract what we need ──
        try
        {
            // Clean previous partial extraction
            var bepinexDir = Path.Combine(cacheDir, "BepInEx");
            if (Directory.Exists(bepinexDir))
                Directory.Delete(bepinexDir, recursive: true);

            Directory.CreateDirectory(coreDir);
            Directory.CreateDirectory(Path.Combine(cacheDir, "BepInEx", "doorstop"));
            Directory.CreateDirectory(Path.Combine(cacheDir, "BepInEx", "plugins", "COIJointVentures"));

            using var zipStream = new MemoryStream(zipBytes);
            using var zip = new ZipArchive(zipStream, ZipArchiveMode.Read);

            foreach (var entry in zip.Entries)
            {
                if (entry.Length == 0) continue; // skip directories

                // BepInEx/core/*.dll → cache/BepInEx/core/
                if (entry.FullName.StartsWith("BepInEx/core/", StringComparison.OrdinalIgnoreCase)
                    && entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    entry.ExtractToFile(Path.Combine(coreDir, entry.Name), overwrite: true);
                }
                // winhttp.dll (doorstop proxy) at zip root → cache/BepInEx/doorstop/
                else if (entry.FullName.Equals("winhttp.dll", StringComparison.OrdinalIgnoreCase))
                {
                    entry.ExtractToFile(doorstopDll, overwrite: true);
                }
            }

            // Stamp version
            File.WriteAllText(versionPath, tag ?? "unknown");
        }
        catch (Exception ex)
        {
            log($"ERROR: Extraction failed: {ex.Message}");
            return false;
        }

        log($"BepInEx {tag} ready.");
        return true;
    }

    private static async Task<(string? tag, string? downloadUrl)> FindLatest5xReleaseAsync(HttpClient http)
    {
        try
        {
            var json = await http.GetStringAsync(ReleasesUrl);
            using var doc = JsonDocument.Parse(json);

            foreach (var release in doc.RootElement.EnumerateArray())
            {
                var tag = release.GetProperty("tag_name").GetString();
                if (tag is null || !tag.StartsWith("v5.")) continue;

                // Skip pre-releases
                if (release.GetProperty("prerelease").GetBoolean()) continue;

                // Find the win_x64 asset
                foreach (var asset in release.GetProperty("assets").EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString();
                    if (name is not null && name.StartsWith(AssetPattern) && name.EndsWith(".zip"))
                    {
                        var url = asset.GetProperty("browser_download_url").GetString();
                        return (tag, url);
                    }
                }
            }
        }
        catch { }

        return (null, null);
    }
}
