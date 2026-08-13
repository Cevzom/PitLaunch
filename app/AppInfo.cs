namespace PitLaunch;

internal static class AppInfo
{
    public const string ProductName = "PitLaunch";
    public const string Version = "1.0.0";
    public const string EmergencyDisplayHotkey = "Ctrl+Alt+Shift+F12";

    /// <summary>
    /// Where installed copies look for updates. Either a GitHub repository URL
    /// (https://github.com/owner/repo, read from its Releases) or a plain HTTPS folder holding the
    /// files produced by build-installer.ps1. Set this before publishing a build you intend to update.
    /// </summary>
    public const string UpdateFeedUrl = "https://github.com/Cevzom/PitLaunch";

    /// <summary>
    /// Small, independently hosted policy document used to retire builds that are too old to
    /// update safely. A missing or invalid document never blocks the app; normal optional update
    /// checking continues instead.
    /// </summary>
    public const string UpdatePolicyUrl =
        "https://raw.githubusercontent.com/Cevzom/PitLaunch/main/docs/update-policy.json";

    public const string DownloadPageUrl = "https://github.com/Cevzom/PitLaunch/releases/latest";

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

    /// <summary>
    /// The minimum-version policy source. HTTPS URLs, file URLs and local paths are supported so
    /// release behavior can be tested offline. Set PITLAUNCH_UPDATE_POLICY_URL to "off" to disable
    /// policy lookup for a diagnostic run.
    /// </summary>
    public static string? ResolvedUpdatePolicyUrl
    {
        get
        {
            string? overrideUrl = Environment.GetEnvironmentVariable("PITLAUNCH_UPDATE_POLICY_URL");
            if (overrideUrl is not null)
            {
                string value = overrideUrl.Trim();
                if (value.Length == 0 || value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("disabled", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
                return value;
            }

            return string.IsNullOrWhiteSpace(UpdatePolicyUrl) ? null : UpdatePolicyUrl;
        }
    }
}
