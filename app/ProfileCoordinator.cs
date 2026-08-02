using System.Diagnostics;

namespace PitLaunch;

internal sealed class ProfileCoordinator : IDisposable
{
    private readonly ProfileRepository _repository;
    private readonly DisplayService _displays;
    private readonly AudioService _audio;
    private readonly WindowService _windows;
    private readonly AppService _apps;
    private readonly PowerService _power = new();
    private readonly ControllerService _controllers = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private AppSettings _lastSavedSettings;

    public ProfileDocument Document { get; }
    public bool IsBusy { get; private set; }

    public event Action? ProfilesChanged;
    public event Action<bool, string>? BusyChanged;
    public event Action<ProfileSwitchCompleted>? SwitchCompleted;

    public ProfileCoordinator(
        ProfileRepository repository,
        DisplayService displays,
        AudioService audio,
        WindowService windows,
        AppService apps)
    {
        _repository = repository;
        _displays = displays;
        _audio = audio;
        _windows = windows;
        _apps = apps;
        Document = repository.Load();
        Document.Settings.LaunchOnStartup = StartupRegistration.IsEnabled();
        Document.Settings.StartMinimized = StartupRegistration.StartsMinimized();
        _lastSavedSettings = CopySettings(Document.Settings);
    }

    public Profile? ActiveProfile => Document.Runtime.ActiveProfileId is Guid id
        ? Document.Profiles.FirstOrDefault(profile => profile.Id == id)
        : null;

    public List<AudioDeviceOption> ListAudioDevices(bool capture) => _audio.ListActiveEndpoints(capture);

    public List<ControllerDevice> ListControllers() => _controllers.ListConnected();

    /// <summary>Expected controllers for this setup that are not plugged in right now.</summary>
    public List<string> FindMissingControllers(Profile profile) =>
        profile.ExpectedControllers.Count == 0 ? [] : _controllers.FindMissing(profile.ExpectedControllers);

    public AudioSnapshot CaptureCurrentAudio() => _audio.Capture();

    public List<DisplayDeviceOption> ListConnectedDisplays() => _displays.ListConnectedDisplays();

    public DisplaySnapshot BuildDisplaySnapshot(DisplaySetupRequest request) => _displays.BuildSnapshot(request);

    public async Task<OperationReport> RestoreAllDisplaysAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        SetBusy(true, "Restoring all connected displays");
        Stopwatch stopwatch = Stopwatch.StartNew();
        OperationReport report = new("Restore all displays");

        try
        {
            await Task.Run(() =>
            {
                DisplaySnapshot recovery = _displays.BuildAllConnectedSnapshot();
                int displayCount = recovery.Monitors.Count(monitor => monitor.Enabled);
                DisplayService.DisplayValidation validation = _displays.ValidateSnapshot(recovery);
                if (!validation.CanRestore)
                {
                    report.Error("Displays", "Windows could not validate the recovery layout: " + validation.Note + ".");
                    return;
                }

                DisplayService.DisplayCheckpoint checkpoint = _displays.CaptureCheckpoint();
                try
                {
                    _displays.Restore(recovery, report);
                    report.Info("Safety", $"Enabled every connected display ({displayCount}). Saved setups were not changed.");
                }
                catch (Exception ex)
                {
                    report.Error("Displays", ex.Message);
                    checkpoint.Restore(report);
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            report.Error("PitLaunch", ex.Message);
            AppLog.Error(ex.ToString());
        }
        finally
        {
            stopwatch.Stop();
            report.Duration = stopwatch.Elapsed;
            SetBusy(false, report.Summary);
            _operationGate.Release();
        }

        return report;
    }

    public Profile? FindProfile(string nameOrId)
    {
        if (Guid.TryParse(nameOrId, out Guid id))
        {
            Profile? byId = Document.Profiles.FirstOrDefault(profile => profile.Id == id);
            if (byId is not null) return byId;
        }

        return Document.Profiles.FirstOrDefault(profile =>
            string.Equals(profile.Name, nameOrId, StringComparison.CurrentCultureIgnoreCase));
    }

    public async Task<ProfileSwitchCompleted?> ActivateAsync(Guid profileId, ActivationSource source)
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        Profile? target = Document.Profiles.FirstOrDefault(profile => profile.Id == profileId);
        if (target is null)
        {
            _operationGate.Release();
            return null;
        }

        SetBusy(true, "Switching to " + target.Name);
        Stopwatch stopwatch = Stopwatch.StartNew();
        OperationReport report = new("Activate " + target.Name);

        try
        {
            await Task.Run(() => ActivateCore(target, report)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            report.Error("PitLaunch", ex.Message);
            AppLog.Error(ex.ToString());
        }
        finally
        {
            stopwatch.Stop();
            report.Duration = stopwatch.Elapsed;
            SetBusy(false, report.Summary);
            _operationGate.Release();
        }

        ProfileSwitchCompleted completed = new(target, source, report);
        SwitchCompleted?.Invoke(completed);
        ProfilesChanged?.Invoke();
        return completed;
    }

    private void ActivateCore(Profile target, OperationReport report)
    {
        Profile? outgoing = ActiveProfile;
        if (outgoing is not null && outgoing.Id != target.Id)
        {
            try
            {
                outgoing.Windows = _windows.Capture();
                report.Info("Windows", $"Saved {outgoing.Windows.Count} positions for {outgoing.Name}.");
            }
            catch (Exception ex)
            {
                report.Warn("Windows", "Could not save the outgoing window positions: " + ex.Message);
            }

        }

        DisplayService.DisplayCheckpoint? checkpoint = null;
        bool displaySucceeded = true;
        try
        {
            checkpoint = _displays.CaptureCheckpoint();
            _displays.Restore(target.Display, report);
        }
        catch (Exception ex)
        {
            displaySucceeded = false;
            report.Error("Displays", ex.Message);
            checkpoint?.Restore(report);
        }

        if (!displaySucceeded)
        {
            report.Warn("PitLaunch", "Audio, applications, and window positions were left unchanged because the display layout failed.");
            SaveDocument(report);
            return;
        }

        if (outgoing is not null && outgoing.Id != target.Id)
        {
            _apps.CloseOnDeactivate(outgoing.Apps, report);
        }

        try { _audio.Restore(target.Audio, report); }
        catch (Exception ex) { report.Warn("Audio", ex.Message); }

        _apps.LaunchOnActivate(target.Apps, report);

        bool waitForLaunchedApps = target.Apps.Any(app => app.StartOnActivate);
        try { _windows.Restore(target.Windows, report, waitForLaunchedApps); }
        catch (Exception ex) { report.Warn("Windows", ex.Message); }

        // Report a wheel or pedal set that is expected but unplugged. This is a warning, never a
        // failure: the setup is still perfectly usable without it.
        try
        {
            List<string> missing = FindMissingControllers(target);
            if (missing.Count > 0)
            {
                report.Warn("Controllers", $"Not connected: {string.Join(", ", missing)}.");
            }
            else if (target.ExpectedControllers.Count > 0)
            {
                report.Info("Controllers", $"All {target.ExpectedControllers.Count} expected device(s) connected.");
            }
        }
        catch (Exception ex) { report.Warn("Controllers", ex.Message); }

        try { _power.SetKeepAwake(target.KeepAwake, report); }
        catch (Exception ex) { report.Warn("Power", ex.Message); }

        DateTimeOffset activatedAt = DateTimeOffset.UtcNow;
        Document.Runtime.ActiveProfileId = target.Id;
        Document.Runtime.LastSwitchUtc = activatedAt;
        target.LastUsedUtc = activatedAt;

        SaveDocument(report);
    }

    public async Task<(Profile? Profile, OperationReport Report)> CaptureNewAsync(string name)
    {
        string cleanName = ValidateName(name, null);
        await _operationGate.WaitAsync().ConfigureAwait(false);
        SetBusy(true, "Capturing " + cleanName);
        Stopwatch stopwatch = Stopwatch.StartNew();
        OperationReport report = new("Capture " + cleanName);
        Profile? profile = null;

        try
        {
            profile = await Task.Run(() =>
            {
                Profile captured = new()
                {
                    Id = Guid.NewGuid(),
                    Name = cleanName,
                    CapturedAtUtc = DateTimeOffset.UtcNow,
                    Display = _displays.Capture(report),
                    Audio = _audio.Capture(report),
                    Windows = _windows.Capture(report)
                };
                VerifyCapturedDisplays(captured, report);
                Guid? previousActiveProfileId = Document.Runtime.ActiveProfileId;
                DateTimeOffset? previousLastSwitch = Document.Runtime.LastSwitchUtc;
                Document.Profiles.Add(captured);
                captured.LastUsedUtc = DateTimeOffset.UtcNow;
                Document.Runtime.ActiveProfileId = captured.Id;
                Document.Runtime.LastSwitchUtc = captured.LastUsedUtc;
                if (!SaveDocument(report))
                {
                    Document.Profiles.Remove(captured);
                    Document.Runtime.ActiveProfileId = previousActiveProfileId;
                    Document.Runtime.LastSwitchUtc = previousLastSwitch;
                    return null;
                }
                return captured;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            report.Error("Capture", ex.Message);
        }
        finally
        {
            stopwatch.Stop();
            report.Duration = stopwatch.Elapsed;
            SetBusy(false, profile is null ? "Capture failed" : "Profile captured");
            _operationGate.Release();
        }

        ProfilesChanged?.Invoke();
        return (profile, report);
    }

    public async Task<(Profile? Profile, OperationReport Report)> CreateConfiguredAsync(
        string name,
        SetupKind kind,
        RigDisplayVariant rigDisplay,
        DisplaySetupRequest displayRequest,
        AudioSnapshot audio)
    {
        string cleanName = ValidateName(name, null);
        await _operationGate.WaitAsync().ConfigureAwait(false);
        SetBusy(true, "Creating " + cleanName);
        Stopwatch stopwatch = Stopwatch.StartNew();
        OperationReport report = new("Capture " + cleanName);
        Profile? profile = null;

        try
        {
            profile = await Task.Run(() =>
            {
                DisplaySnapshot display = _displays.BuildSnapshot(displayRequest);
                DisplayService.DisplayValidation validation = _displays.ValidateSnapshot(display);
                if (!validation.CanRestore)
                {
                    report.Error("Displays", "Windows could not validate this layout: " + validation.Note + ". Try another arrangement.");
                    return null;
                }

                int enabledCount = display.Monitors.Count(monitor => monitor.Enabled);
                report.Info("Displays", $"Built and validated a {enabledCount}-display layout.");
                if (validation.Note is not null)
                {
                    report.Warn("Displays", validation.Note + ".");
                }

                Profile configured = new()
                {
                    Id = Guid.NewGuid(),
                    Name = cleanName,
                    Kind = kind,
                    RigDisplay = kind == SetupKind.SimRacing ? rigDisplay : RigDisplayVariant.Auto,
                    CapturedAtUtc = DateTimeOffset.UtcNow,
                    Display = display,
                    Audio = audio,
                    Windows = []
                };
                Document.Profiles.Add(configured);
                if (!SaveDocument(report))
                {
                    Document.Profiles.Remove(configured);
                    return null;
                }
                return configured;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            report.Error("Setup", ex.Message);
        }
        finally
        {
            stopwatch.Stop();
            report.Duration = stopwatch.Elapsed;
            SetBusy(false, profile is null ? "Setup was not created" : "Setup created");
            _operationGate.Release();
        }

        ProfilesChanged?.Invoke();
        return (profile, report);
    }

    public async Task<OperationReport> RecaptureAsync(Guid profileId)
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        Profile? profile = Document.Profiles.FirstOrDefault(item => item.Id == profileId);
        if (profile is null)
        {
            OperationReport missing = new("Recapture profile");
            missing.Error("Capture", "The profile no longer exists.");
            _operationGate.Release();
            return missing;
        }
        SetBusy(true, "Recapturing " + profile.Name);
        Stopwatch stopwatch = Stopwatch.StartNew();
        OperationReport report = new("Recapture " + profile.Name);

        try
        {
            await Task.Run(() =>
            {
                DisplaySnapshot originalDisplay = profile.Display;
                AudioSnapshot originalAudio = profile.Audio;
                List<WindowSnapshot> originalWindows = profile.Windows;
                DateTimeOffset originalCapturedAt = profile.CapturedAtUtc;
                DateTimeOffset? originalLastUsed = profile.LastUsedUtc;
                Guid? originalActiveProfileId = Document.Runtime.ActiveProfileId;
                DateTimeOffset? originalLastSwitch = Document.Runtime.LastSwitchUtc;
                bool committed = false;
                try
                {
                    profile.Display = _displays.Capture(report);
                    profile.Audio = _audio.Capture(report);
                    profile.Windows = _windows.Capture(report);
                    VerifyCapturedDisplays(profile, report);
                    profile.CapturedAtUtc = DateTimeOffset.UtcNow;
                    profile.LastUsedUtc = DateTimeOffset.UtcNow;
                    Document.Runtime.ActiveProfileId = profile.Id;
                    Document.Runtime.LastSwitchUtc = profile.LastUsedUtc;
                    committed = SaveDocument(report);
                }
                finally
                {
                    if (!committed)
                    {
                        profile.Display = originalDisplay;
                        profile.Audio = originalAudio;
                        profile.Windows = originalWindows;
                        profile.CapturedAtUtc = originalCapturedAt;
                        profile.LastUsedUtc = originalLastUsed;
                        Document.Runtime.ActiveProfileId = originalActiveProfileId;
                        Document.Runtime.LastSwitchUtc = originalLastSwitch;
                    }
                }
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            report.Error("Capture", ex.Message);
        }
        finally
        {
            stopwatch.Stop();
            report.Duration = stopwatch.Elapsed;
            SetBusy(false, report.Summary);
            _operationGate.Release();
        }

        ProfilesChanged?.Invoke();
        return report;
    }

    public void Rename(Guid profileId, string name)
    {
        Profile profile = Document.Profiles.First(item => item.Id == profileId);
        string previousName = profile.Name;
        profile.Name = ValidateName(name, profileId);
        try { _repository.Save(Document); }
        catch
        {
            profile.Name = previousName;
            throw;
        }
        ProfilesChanged?.Invoke();
    }

    public void Delete(Guid profileId)
    {
        int index = Document.Profiles.FindIndex(profile => profile.Id == profileId);
        if (index < 0) return;
        Profile removed = Document.Profiles[index];
        Guid? previousActiveProfileId = Document.Runtime.ActiveProfileId;
        Document.Profiles.RemoveAt(index);
        if (Document.Runtime.ActiveProfileId == profileId) Document.Runtime.ActiveProfileId = null;
        try { _repository.Save(Document); }
        catch
        {
            Document.Profiles.Insert(index, removed);
            Document.Runtime.ActiveProfileId = previousActiveProfileId;
            throw;
        }
        ProfilesChanged?.Invoke();
    }

    public void SaveProfile(Profile profile)
    {
        if (!Document.Profiles.Any(item => item.Id == profile.Id))
        {
            throw new InvalidOperationException("The profile no longer exists.");
        }

        profile.Name = ValidateName(profile.Name, profile.Id);
        profile.GameProcesses = profile.GameProcesses
            .Select(GameDetectionService.NormalizeProcessName)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _repository.Save(Document);
        ProfilesChanged?.Invoke();
    }

    public void SaveSettings()
    {
        Document.Settings.GamePollSeconds = Math.Clamp(Document.Settings.GamePollSeconds, 1, 30);
        AppSettings previous = CopySettings(_lastSavedSettings);
        try
        {
            StartupRegistration.SetEnabled(Document.Settings.LaunchOnStartup, Document.Settings.StartMinimized);
            _repository.Save(Document);
            _lastSavedSettings = CopySettings(Document.Settings);
        }
        catch
        {
            Document.Settings = previous;
            try { StartupRegistration.SetEnabled(previous.LaunchOnStartup, previous.StartMinimized); }
            catch (Exception rollbackError) { AppLog.Error("Could not roll back Windows startup settings: " + rollbackError.Message); }
            throw;
        }
        ProfilesChanged?.Invoke();
    }

    private static AppSettings CopySettings(AppSettings settings) => new()
    {
        LaunchOnStartup = settings.LaunchOnStartup,
        StartMinimized = settings.StartMinimized,
        ConfirmBeforeSwitch = settings.ConfirmBeforeSwitch,
        GameDetectionEnabled = settings.GameDetectionEnabled,
        GamePollSeconds = settings.GamePollSeconds
    };

    private void VerifyCapturedDisplays(Profile profile, OperationReport report)
    {
        DisplayService.DisplayValidation validation = _displays.ValidateSnapshot(profile.Display);
        if (validation.CanRestore && validation.Note is null)
        {
            report.Info("Displays", "Windows confirmed this display layout can be restored later.");
        }
        else if (validation.CanRestore)
        {
            report.Warn("Displays", "This layout can be restored, but " + validation.Note + ".");
        }
        else
        {
            report.Warn("Displays",
                "Heads up: Windows may refuse to restore this display layout later (" + validation.Note + "). " +
                "Create the setup again with a different display arrangement if switching fails.");
        }
    }

    private string ValidateName(string name, Guid? currentId)
    {
        string clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0) throw new InvalidOperationException("Enter a profile name.");
        if (clean.Length > 60) throw new InvalidOperationException("Profile names are limited to 60 characters.");
        if (Document.Profiles.Any(profile => profile.Id != currentId &&
                                             string.Equals(profile.Name, clean, StringComparison.CurrentCultureIgnoreCase)))
        {
            throw new InvalidOperationException("A profile with that name already exists.");
        }

        return clean;
    }

    private bool SaveDocument(OperationReport report)
    {
        try
        {
            _repository.Save(Document);
            return true;
        }
        catch (Exception ex)
        {
            report.Error("Storage", "Could not save profiles.json: " + ex.Message);
            return false;
        }
    }

    private void SetBusy(bool busy, string message)
    {
        IsBusy = busy;
        BusyChanged?.Invoke(busy, message);
    }

    public void Dispose()
    {
        // Release the keep-awake request so closing PitLaunch never leaves the machine unable to sleep.
        _power.Dispose();
        _operationGate.Dispose();
    }
}
