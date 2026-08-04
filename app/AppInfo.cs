namespace PitLaunch;

internal static class AppInfo
{
    public const string ProductName = "PitLaunch Beta";
    public const string Version = "0.9.7-beta.1";
    public const string EmergencyDisplayHotkey = "Ctrl+Alt+Shift+F12";

    /// <summary>
    /// Where installed copies look for updates. Either a GitHub repository URL
    /// (https://github.com/owner/repo, read from its Releases) or a plain HTTPS folder holding the
    /// files produced by build-installer.ps1. Set this before publishing a build you intend to update.
    /// </summary>
    public const string UpdateFeedUrl = "https://github.com/Cevzom/PitLaunch";

    /// <summary>The feed used at runtime; the environment variable wins so a build can be tested against a local folder.</summary>
    public static string? ResolvedUpdateFeedUrl
    {
        get
        {
            string? overrideUrl = Environment.GetEnvironmentVariable("PITLAUNCH_UPDATE_URL");
            if (!string.IsNullOrWhiteSpace(overrideUrl)) return overrideUrl.Trim();
            return string.IsNullOrWhiteSpace(UpdateFeedUrl) ? null : UpdateFeedUrl;
        }
    }
}
