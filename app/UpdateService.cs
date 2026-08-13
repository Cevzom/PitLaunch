using Velopack;
using Velopack.Sources;

namespace PitLaunch;

internal enum UpdateState
{
    UpToDate,
    Available,
    Required,
    NotConfigured,
    PortableCopy,
    Failed
}

internal sealed record UpdateStatus(
    UpdateState State,
    string Message,
    UpdateInfo? Update = null,
    UpdatePolicyResult? Policy = null)
{
    public bool CanInstall => (State is UpdateState.Available or UpdateState.Required) && Update is not null;
    public bool IsRequired => State == UpdateState.Required || Policy?.IsRequired == true;
    public string? MinimumRequiredVersion => IsRequired ? Policy?.MinimumVersion : null;
    public string DownloadUrl => Policy?.DownloadUrl ?? AppInfo.DownloadPageUrl;
}

/// <summary>
/// Wraps Velopack. An installed copy updates itself by downloading only the parts that changed;
/// a portable copy (the zip) has no install to patch and is told to download the new zip instead.
/// </summary>
internal sealed class UpdateService
{
    private readonly Lazy<UpdateManager?> _manager;
    private readonly UpdatePolicyService _policy;

    public UpdateService() : this(new UpdatePolicyService())
    {
    }

    internal UpdateService(UpdatePolicyService policy) : this(policy, CreateManager)
    {
    }

    internal UpdateService(UpdatePolicyService policy, Func<UpdateManager?> managerFactory)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _manager = new Lazy<UpdateManager?>(managerFactory ?? throw new ArgumentNullException(nameof(managerFactory)));
    }

    public static string CurrentVersion => AppInfo.Version;

    public bool IsInstalledCopy => _manager.Value?.IsInstalled == true;

    /// <summary>Checks only the small support policy, without contacting or initializing Velopack.</summary>
    public Task<UpdatePolicyResult> CheckPolicyAsync(CancellationToken cancellationToken = default) =>
        _policy.CheckAsync(CurrentVersion, cancellationToken);

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

    public async Task<UpdateStatus> CheckAsync(CancellationToken cancellationToken = default)
    {
        UpdatePolicyResult policy = await CheckPolicyAsync(cancellationToken).ConfigureAwait(false);
        return await CheckAsync(policy, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<UpdateStatus> CheckAsync(
        UpdatePolicyResult policy,
        CancellationToken cancellationToken = default)
    {
        UpdateManager? manager = _manager.Value;
        if (manager is null)
        {
            if (policy.IsRequired) return RequiredStatus(policy, null, "No in-app update source is configured.");
            return new UpdateStatus(UpdateState.NotConfigured,
                "No update feed is configured for this build.", Policy: policy);
        }

        if (!manager.IsInstalled)
        {
            if (policy.IsRequired)
            {
                return RequiredStatus(policy, null,
                    "This portable copy cannot patch itself. Download and install the current release.");
            }
            return new UpdateStatus(UpdateState.PortableCopy,
                "This is the portable copy, so it cannot patch itself. Install PitLaunch with Setup.exe to get small automatic updates.",
                Policy: policy);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateInfo? update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (policy.IsRequired)
            {
                if (update is not null && !UpdateSatisfiesPolicy(update, policy))
                {
                    AppLog.Error(
                        $"The update feed offers {update.TargetFullRelease.Version}, below required {policy.MinimumVersion}.");
                    update = null;
                }
                return RequiredStatus(policy, update,
                    update is null ? "A suitable in-app package is not available yet." : string.Empty);
            }

            if (update is null)
            {
                return new UpdateStatus(
                    UpdateState.UpToDate,
                    $"PitLaunch {CurrentVersion} is up to date.",
                    Policy: policy);
            }

            string version = update.TargetFullRelease.Version.ToString();
            AppLog.Info($"Update available: {version}.");
            return new UpdateStatus(UpdateState.Available, $"Version {version} is available.", update, policy);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Error("Update check failed: " + ex.Message);
            if (policy.IsRequired)
            {
                return RequiredStatus(policy, null,
                    "The update package could not be checked. Check your connection and try again.");
            }
            return new UpdateStatus(
                UpdateState.Failed,
                "Could not reach the update server: " + ex.Message,
                Policy: policy);
        }
    }

    private static UpdateStatus RequiredStatus(UpdatePolicyResult policy, UpdateInfo? update, string note)
    {
        string message = policy.Message;
        if (!string.IsNullOrWhiteSpace(note)) message = message.TrimEnd() + " " + note.Trim();
        AppLog.Info($"Mandatory update policy requires {policy.MinimumVersion} or newer.");
        return new UpdateStatus(UpdateState.Required, message, update, policy);
    }

    private static bool UpdateSatisfiesPolicy(UpdateInfo update, UpdatePolicyResult policy) =>
        !string.IsNullOrWhiteSpace(policy.MinimumVersion) &&
        PitLaunchVersion.IsAtLeast(update.TargetFullRelease.Version.ToString(), policy.MinimumVersion);

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
