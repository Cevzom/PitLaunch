using Velopack;
using Velopack.Sources;

namespace PitLaunch;

internal enum UpdateState
{
    UpToDate,
    Available,
    NotConfigured,
    PortableCopy,
    Failed
}

internal sealed record UpdateStatus(UpdateState State, string Message, UpdateInfo? Update = null)
{
    public bool CanInstall => State == UpdateState.Available && Update is not null;
}

/// <summary>
/// Wraps Velopack. An installed copy updates itself by downloading only the parts that changed;
/// a portable copy (the zip) has no install to patch and is told to download the new zip instead.
/// </summary>
internal sealed class UpdateService
{
    private readonly Lazy<UpdateManager?> _manager = new(CreateManager);

    public static string CurrentVersion => AppInfo.Version;

    public bool IsInstalledCopy => _manager.Value?.IsInstalled == true;

    private static UpdateManager? CreateManager()
    {
        string? feed = AppInfo.ResolvedUpdateFeedUrl;
        if (string.IsNullOrWhiteSpace(feed)) return null;

        try
        {
            // A folder works too, which covers a network share and lets a build be tested offline.
            if (Directory.Exists(feed)) return new UpdateManager(new SimpleFileSource(new DirectoryInfo(feed)));

            IUpdateSource source = feed.Contains("github.com/", StringComparison.OrdinalIgnoreCase)
                ? new GithubSource(feed, accessToken: null, prerelease: true)
                : new SimpleWebSource(feed);
            return new UpdateManager(source);
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not prepare the update source: " + ex.Message);
            return null;
        }
    }

    public async Task<UpdateStatus> CheckAsync()
    {
        UpdateManager? manager = _manager.Value;
        if (manager is null)
        {
            return new UpdateStatus(UpdateState.NotConfigured,
                "No update feed is configured for this build.");
        }

        if (!manager.IsInstalled)
        {
            return new UpdateStatus(UpdateState.PortableCopy,
                "This is the portable copy, so it cannot patch itself. Install PitLaunch with Setup.exe to get small automatic updates.");
        }

        try
        {
            UpdateInfo? update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                return new UpdateStatus(UpdateState.UpToDate, $"PitLaunch {CurrentVersion} is up to date.");
            }

            string version = update.TargetFullRelease.Version.ToString();
            AppLog.Info($"Update available: {version}.");
            return new UpdateStatus(UpdateState.Available, $"Version {version} is available.", update);
        }
        catch (Exception ex)
        {
            AppLog.Error("Update check failed: " + ex.Message);
            return new UpdateStatus(UpdateState.Failed, "Could not reach the update server: " + ex.Message);
        }
    }

    /// <summary>Downloads the update (delta when possible) and reports 0-100 progress.</summary>
    public async Task<string?> DownloadAsync(UpdateInfo update, Action<int> progress)
    {
        UpdateManager? manager = _manager.Value;
        if (manager is null) return "The update source is unavailable.";
        try
        {
            await manager.DownloadUpdatesAsync(update, progress).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error("Update download failed: " + ex.Message);
            return ex.Message;
        }
    }

    /// <summary>Applies the downloaded update and restarts. Does not return when it succeeds.</summary>
    public string? ApplyAndRestart(UpdateInfo update)
    {
        UpdateManager? manager = _manager.Value;
        if (manager is null) return "The update source is unavailable.";
        try
        {
            AppLog.Info("Applying update and restarting.");
            manager.ApplyUpdatesAndRestart(update);
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error("Applying the update failed: " + ex.Message);
            return ex.Message;
        }
    }
}
