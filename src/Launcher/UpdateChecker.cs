using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace JointVentures.Launcher;

internal static class UpdateChecker
{
    private const string GitHubRepo = "Ryan4598/coi-joint-ventures";
    private static readonly string ReleasesUrl =
        $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
    public static readonly string ReleasesPage =
        $"https://github.com/{GitHubRepo}/releases/latest";

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Checks GitHub for a newer release. Returns the tag name if an update
    /// is available, or null if current/unreachable.
    /// </summary>
    public static async Task<string?> CheckForUpdateAsync()
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("JointVentures-Launcher");
            http.Timeout = TimeSpan.FromSeconds(5);

            var json = await http.GetStringAsync(ReleasesUrl);
            using var doc = JsonDocument.Parse(json);

            var tagName = doc.RootElement.GetProperty("tag_name").GetString();
            if (tagName is null) return null;

            // Strip leading 'v' if present (e.g. "v0.4.0" → "0.4.0")
            var remoteVersionStr = tagName.StartsWith('v') ? tagName[1..] : tagName;

            if (!Version.TryParse(remoteVersionStr, out var remoteVersion))
                return null;

            if (!Version.TryParse(CurrentVersion, out var localVersion))
                return null;

            return remoteVersion > localVersion ? tagName : null;
        }
        catch
        {
            // Network error, rate limited, no releases yet — silently ignore
            return null;
        }
    }
}
