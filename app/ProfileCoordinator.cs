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
    private readonly HdrService _hdr = new();
    private readonly ControllerService _controllers = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private AppSettings _lastSavedSettings;
    private string? _switchBlockReason;

    public ProfileDocument Document { get; }
    public bool IsBusy { get; private set; }
    public bool CanUndo => Document.Runtime.LastSwitchCheckpoint is not null;
    public string? SwitchBlockReason => Volatile.Read(ref _switchBlockReason);
    public bool IsHdrSupported
    {
        get
        {
            try { return _hdr.GetStatus().IsSupported; }
            catch { return false; }
        }
    }

    public event Action? ProfilesChanged;
    public event Action<bool, string>? BusyChanged;
    public event Action<ProfileSwitchCompleted>? SwitchCompleted;

    public void SetSwitchBlockReason(string? reason) =>
        Volatile.Write(ref _switchBlockReason, string.IsNullOrWhiteSpace(reason) ? null : reason.Trim());

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

    public List<PowerPlanOption> ListPowerPlans() => _power.ListPowerPlans();

    public HdrStatus GetHdrStatus()
    {
        try { return _hdr.GetStatus(); }
        catch { return new HdrStatus(false, null, 0, 0); }
    }

    public List<ControllerDevice> ListControllers() => _controllers.ListConnected();

    /// <summary>Expected controllers for this setup that are not plugged in right now.</summary>
    public List<string> FindMissingControllers(Profile profile) =>
        profile.ExpectedControllers.Count == 0 ? [] : _controllers.FindMissing(profile.ExpectedControllers);

    public AudioSnapshot CaptureCurrentAudio() => _audio.Capture();

    public List<DisplayDeviceOption> ListConnectedDisplays() => _displays.ListConnectedDisplays();

    public DisplaySnapshot BuildDisplaySnapshot(DisplaySetupRequest request) => _displays.BuildSnapshot(request);

    public ReadinessReport CheckReadiness(Profile profile)
    {
        ReadinessReport readiness = new();
        if (!Document.Profiles.Any(item => item.Id == profile.Id))
        {
            readiness.Error("Profile", "This setup no longer exists.");
            return readiness;
        }

        try
        {
            List<DisplayDeviceOption> connected = _displays.ListConnectedDisplays();
            HashSet<string> paths = connected.Select(display => display.DevicePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<MonitorSnapshot> required = profile.Display.Monitors.Where(display => display.Enabled).ToList();
            List<string> missing = required.Where(display => !paths.Contains(display.DevicePath))
                .Select(display => display.FriendlyName).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
            if (required.Count == 0)
                readiness.Error("Displays", "This setup does not enable any displays.");
            else if (missing.Count > 0)
                readiness.Error("Displays", "Not connected: " + string.Join(", ", missing) + ".");
            else
                readiness.Info("Displays", $"All {required.Count} required display{(required.Count == 1 ? " is" : "s are")} connected.");

            DisplayService.DisplayValidation validation = _displays.ValidateSnapshot(profile.Display);
            if (!validation.CanRestore)
                readiness.Error("Displays", "Windows rejected this layout: " + validation.Note + ".");
            else if (validation.Note is not null)
                readiness.Warn("Displays", validation.Note + ".");
        }
        catch (Exception ex) { readiness.Error("Displays", ex.Message); }

        try
        {
            List<AudioDeviceOption> playback = _audio.ListActiveEndpoints(false);
            List<AudioDeviceOption> capture = _audio.ListActiveEndpoints(true);
            CheckEndpoint(profile.Audio.Playback, playback, "playback", readiness);
            CheckEndpoint(profile.Audio.Communications, playback, "communications", readiness);
            CheckEndpoint(profile.Audio.Microphone, capture, "microphone", readiness);
            CheckDeviceId(profile.Discord.OutputDeviceId, playback, "Discord output", readiness);
            CheckDeviceId(profile.Discord.MicrophoneDeviceId, capture, "Discord microphone", readiness);
            foreach (GamePreset preset in profile.GamePresets)
            {
                CheckDeviceId(preset.AudioDeviceId, playback, $"{preset.ProcessName} output", readiness);
                if (!preset.CustomizeDiscord) continue;
                CheckDeviceId(preset.Discord.OutputDeviceId, playback, $"{preset.ProcessName} Discord output", readiness);
                CheckDeviceId(preset.Discord.MicrophoneDeviceId, capture, $"{preset.ProcessName} Discord microphone", readiness);
            }
        }
        catch (Exception ex) { readiness.Warn("Audio", ex.Message); }

        try
        {
            List<string> missingControllers = FindMissingControllers(profile);
            if (missingControllers.Count > 0)
                readiness.Warn("Controllers", "Not connected: " + string.Join(", ", missingControllers) + ".");
            else if (profile.ExpectedControllers.Count > 0)
                readiness.Info("Controllers", $"All {profile.ExpectedControllers.Count} expected device(s) connected.");
        }
        catch (Exception ex) { readiness.Warn("Controllers", ex.Message); }

        foreach (AppRule rule in profile.Apps.Where(rule => rule.StartOnActivate && AppService.IsLocalPath(rule.ExecutablePath)))
        {
            string path;
            try { path = Environment.ExpandEnvironmentVariables(rule.ExecutablePath); }
            catch { path = rule.ExecutablePath; }
            if (!File.Exists(path)) readiness.Warn("Apps", $"{rule.DisplayName} is missing: {path}");
        }

        if (!string.IsNullOrWhiteSpace(profile.PowerPlanGuid) &&
            !_power.ListPowerPlans().Any(plan => string.Equals(plan.Guid, profile.PowerPlanGuid, StringComparison.OrdinalIgnoreCase)))
        {
            readiness.Warn("Power", "The saved Windows power plan is no longer available.");
        }

        if (profile.EnableHdr.HasValue)
        {
            try
            {
                if (!_hdr.GetStatus().IsSupported)
                    readiness.Warn("HDR", "No active display reports HDR support.");
            }
            catch (Exception ex) { readiness.Warn("HDR", ex.Message); }
        }

        if (readiness.Items.Count == 0) readiness.Info("Setup", "Ready to switch.");
        return readiness;
    }

    private static void CheckEndpoint(
        AudioEndpointSnapshot? endpoint,
        IReadOnlyList<AudioDeviceOption> available,
        string role,
        ReadinessReport readiness)
    {
        if (endpoint is null) return;
        if (available.Any(device => string.Equals(device.Id, endpoint.DeviceId, StringComparison.OrdinalIgnoreCase)))
            readiness.Info("Audio", $"{endpoint.FriendlyName} is available for {role}.");
        else
            readiness.Warn("Audio", $"{endpoint.FriendlyName} is not available for {role}.");
    }

    private static void CheckDeviceId(
        string? deviceId,
        IReadOnlyList<AudioDeviceOption> available,
        string label,
        ReadinessReport readiness)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return;
        AudioDeviceOption? device = available.FirstOrDefault(option =>
            string.Equals(option.Id, deviceId, StringComparison.OrdinalIgnoreCase));
        if (device is null) readiness.Warn("Audio", $"The saved {label} is not connected.");
        else readiness.Info("Audio", $"{device.Name} is available for {label}.");
    }

    public Profile? FindDeskRigToggleTarget()
    {
        Profile? active = ActiveProfile;
        SetupKind wanted = active?.Kind == SetupKind.SimRacing ? SetupKind.Desk : SetupKind.SimRacing;
        Profile? target = Document.Profiles
            .Where(profile => profile.Kind == wanted)
            .OrderByDescending(profile => profile.LastUsedUtc ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        // Auto profiles from older versions still participate: choose the most recently used
        // profile other than the current one when no explicitly typed opposite exists.
        target ??= Document.Profiles
            .Where(profile => profile.Id != active?.Id)
            .OrderByDescending(profile => profile.LastUsedUtc ?? profile.CapturedAtUtc)
            .FirstOrDefault();
        return target;
    }

    public async Task<ProfileSwitchCompleted?> ToggleDeskRigAsync(ActivationSource source)
    {
        Profile? target = FindDeskRigToggleTarget();
        return target is null ? null : await ActivateAsync(target.Id, source).ConfigureAwait(false);
    }

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

    public async Task<ProfileSwitchCompleted?> ActivateAsync(
        Guid profileId,
        ActivationSource source,
        string? gameProcessName = null)
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        Profile? target = Document.Profiles.FirstOrDefault(profile => profile.Id == profileId);
        if (target is null)
        {
            _operationGate.Release();
            return null;
        }

        string? blocked = SwitchBlockReason;
        if (blocked is not null)
        {
            OperationReport blockedReport = new("Activate " + target.Name);
            blockedReport.Error("Update", blocked);
            _operationGate.Release();
            ProfileSwitchCompleted blockedResult = new(target, source, blockedReport);
            SwitchCompleted?.Invoke(blockedResult);
            return blockedResult;
        }

        SetBusy(true, "Switching to " + target.Name);
        Stopwatch stopwatch = Stopwatch.StartNew();
        OperationReport report = new("Activate " + target.Name);

        try
        {
            await Task.Run(() => ActivateCore(target, source, gameProcessName, report)).ConfigureAwait(false);
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

    private void ActivateCore(
        Profile target,
        ActivationSource source,
        string? gameProcessName,
        OperationReport report)
    {
        Profile? outgoing = ActiveProfile;
        if (IsSameProfileGameSession(outgoing, target, source))
        {
            ApplySameProfileGameSession(target, source, gameProcessName, report);
            return;
        }
        if (outgoing?.Id != target.Id) CaptureSwitchCheckpoint(outgoing, report);
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
            if (checkpoint?.Restore(report) == true)
            {
                Document.Runtime.LastSwitchCheckpoint = null;
                SaveDocument(report);
            }
        }

        if (!displaySucceeded)
        {
            report.Warn("PitLaunch", "Audio, applications, and window positions were left unchanged because the display layout failed.");
            SaveDocument(report);
            return;
        }

        CloseActiveGamePreset(report);

        if (outgoing is not null && outgoing.Id != target.Id)
        {
            _apps.CloseOnDeactivate(outgoing.Apps, report);
        }

        try { _audio.Restore(target.Audio, report); }
        catch (Exception ex) { report.Warn("Audio", ex.Message); }

        try { _hdr.SetEnabled(target.EnableHdr, report); }
        catch (Exception ex) { report.Warn("HDR", ex.Message); }

        try { _power.SetPowerPlan(target.PowerPlanGuid, report); }
        catch (Exception ex) { report.Warn("Power", ex.Message); }

        _apps.LaunchOnActivate(target.Apps, report);
        ApplyDiscord(target.Discord, report);

        if (source == ActivationSource.GameDetected && !string.IsNullOrWhiteSpace(gameProcessName))
        {
            ApplyGamePreset(target, gameProcessName, report);
        }

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

    internal static bool IsSameProfileGameSession(Profile? outgoing, Profile target, ActivationSource source) =>
        outgoing?.Id == target.Id && source is ActivationSource.GameDetected or ActivationSource.GameExited;

    private void ApplySameProfileGameSession(
        Profile target,
        ActivationSource source,
        string? gameProcessName,
        OperationReport report)
    {
        CloseActiveGamePreset(report);
        if (source == ActivationSource.GameDetected && !string.IsNullOrWhiteSpace(gameProcessName))
        {
            ApplyDiscord(target.Discord, report);
            ApplyGamePreset(target, gameProcessName, report);
        }
        else
        {
            try { _audio.Restore(target.Audio, report); }
            catch (Exception ex) { report.Warn("Audio", ex.Message); }
            _apps.LaunchOnActivate(target.Apps, report);
            ApplyDiscord(target.Discord, report);
            report.Info("Game preset", $"Restored the normal {target.Name} session settings.");
        }

        DateTimeOffset activatedAt = DateTimeOffset.UtcNow;
        Document.Runtime.ActiveProfileId = target.Id;
        Document.Runtime.LastSwitchUtc = activatedAt;
        target.LastUsedUtc = activatedAt;
        SaveDocument(report);
    }

    private void CloseActiveGamePreset(OperationReport report)
    {
        if (Document.Runtime.ActiveGamePresetId is not Guid presetId) return;
        (Profile Profile, GamePreset Preset) active = Document.Profiles
            .SelectMany(profile => profile.GamePresets.Select(preset => (Profile: profile, Preset: preset)))
            .FirstOrDefault(item => item.Preset.Id == presetId);
        if (active.Preset is not null)
        {
            GamePreset preset = active.Preset;
            SendDiscordSessionKeys(active.Profile, preset, report);
            _apps.CloseOnDeactivate(preset.Apps, report);
            report.Info("Game preset", $"Stopped overrides for {preset.ProcessName}.");
        }
        Document.Runtime.ActiveGamePresetId = null;
    }

    private void ApplyGamePreset(Profile profile, string processName, OperationReport report)
    {
        string normalized = GameDetectionService.NormalizeProcessName(processName);
        GamePreset? preset = profile.GamePresets.FirstOrDefault(item =>
            string.Equals(item.ProcessName, normalized, StringComparison.OrdinalIgnoreCase));
        if (preset is null) return;

        _apps.ApplyAudioToProcesses(
            [preset.ProcessName],
            preset.ProcessName,
            preset.AudioDeviceId,
            preset.VolumePercent,
            report,
            waitMilliseconds: 2500);
        _apps.LaunchOnActivate(preset.Apps, report);
        if (preset.CustomizeDiscord) ApplyDiscord(preset.Discord, report);
        SendDiscordSessionKeys(profile, preset, report);
        Document.Runtime.ActiveGamePresetId = preset.Id;
        report.Info("Game preset", preset.HasOverrides
            ? $"Applied the {preset.ProcessName} overrides."
            : $"{preset.ProcessName} uses the normal {profile.Name} settings.");
    }

    private static void SendDiscordSessionKeys(Profile profile, GamePreset preset, OperationReport report)
    {
        string muteHotkey = preset.CustomizeDiscord && !string.IsNullOrWhiteSpace(preset.Discord.MuteToggleHotkey)
            ? preset.Discord.MuteToggleHotkey
            : profile.Discord.MuteToggleHotkey;
        string deafenHotkey = preset.CustomizeDiscord && !string.IsNullOrWhiteSpace(preset.Discord.DeafenToggleHotkey)
            ? preset.Discord.DeafenToggleHotkey
            : profile.Discord.DeafenToggleHotkey;
        if (preset.ToggleDiscordMuteForSession)
            HotkeySender.Press(muteHotkey, "mute toggle", report);
        if (preset.ToggleDiscordDeafenForSession)
            HotkeySender.Press(deafenHotkey, "deafen toggle", report);
    }

    private void ApplyDiscord(DiscordSettings settings, OperationReport report)
    {
        if (settings is null || !settings.HasOverrides) return;
        if (settings.LaunchOnActivate)
        {
            if (AppService.FindProcessIdsByNames(["Discord", "DiscordCanary", "DiscordPTB"]).Count == 0)
            {
                _apps.LaunchOnActivate(
                    [new AppRule { ExecutablePath = "discord:", StartOnActivate = true }],
                    report);
            }
            else
            {
                report.Info("Discord", "Discord is already running.");
            }
        }
        _apps.ApplyAudioToProcesses(
            ["Discord", "DiscordCanary", "DiscordPTB"],
            "Discord",
            settings.OutputDeviceId,
            settings.VolumePercent,
            report,
            settings.LaunchOnActivate ? 4000 : 0);
        try { _audio.SetCommunicationsMicrophone(settings.MicrophoneDeviceId, report); }
        catch (Exception ex) { report.Warn("Discord", "Could not select its communications microphone: " + ex.Message); }
    }

    private void CaptureSwitchCheckpoint(Profile? outgoing, OperationReport report)
    {
        try
        {
            HdrStatus hdr = _hdr.GetStatus();
            Document.Runtime.LastSwitchCheckpoint = new SwitchCheckpoint
            {
                CapturedAtUtc = DateTimeOffset.UtcNow,
                ActiveProfileId = outgoing?.Id,
                Display = _displays.Capture(),
                Audio = _audio.Capture(),
                KeepAwake = _power.IsHolding,
                PowerPlanGuid = _power.GetActivePowerPlanGuid(),
                EnableHdr = hdr.IsSupported ? hdr.IsEnabled : null
            };

            // Write before touching displays. A crash or forced restart can then still undo the
            // last transition on the next launch.
            _repository.Save(Document);
            report.Info("Safety", "Saved the current hardware state for Undo.");
        }
        catch (Exception ex)
        {
            report.Warn("Safety", "Could not save an Undo checkpoint: " + ex.Message);
        }
    }

    public async Task<OperationReport> UndoLastSwitchAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        SetBusy(true, "Undoing the last switch");
        Stopwatch stopwatch = Stopwatch.StartNew();
        OperationReport report = new("Undo last switch");
        try
        {
            await Task.Run(() => UndoLastSwitchCore(report)).ConfigureAwait(false);
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
            SetBusy(false, report.HasErrors ? report.Summary : "Last switch undone");
            _operationGate.Release();
        }

        ProfilesChanged?.Invoke();
        return report;
    }

    private void UndoLastSwitchCore(OperationReport report)
    {
        SwitchCheckpoint? checkpoint = Document.Runtime.LastSwitchCheckpoint;
        if (checkpoint is null)
        {
            report.Warn("Undo", "There is no saved switch to undo.");
            return;
        }

        DisplayService.DisplayCheckpoint? rollback = null;
        try
        {
            rollback = _displays.CaptureCheckpoint();
            _displays.Restore(checkpoint.Display, report);
        }
        catch (Exception ex)
        {
            report.Error("Displays", ex.Message);
            rollback?.Restore(report);
            return;
        }

        Profile? outgoing = ActiveProfile;
        Profile? restoredProfile = checkpoint.ActiveProfileId.HasValue
            ? Document.Profiles.FirstOrDefault(profile => profile.Id == checkpoint.ActiveProfileId.Value)
            : null;

        if (outgoing is not null && outgoing.Id != restoredProfile?.Id)
            _apps.CloseOnDeactivate(outgoing.Apps, report);

        try { _audio.Restore(checkpoint.Audio, report); }
        catch (Exception ex) { report.Warn("Audio", ex.Message); }
        try { _hdr.SetEnabled(checkpoint.EnableHdr, report); }
        catch (Exception ex) { report.Warn("HDR", ex.Message); }
        try { _power.SetPowerPlan(checkpoint.PowerPlanGuid, report); }
        catch (Exception ex) { report.Warn("Power", ex.Message); }
        try { _power.SetKeepAwake(checkpoint.KeepAwake, report); }
        catch (Exception ex) { report.Warn("Power", ex.Message); }

        if (restoredProfile is not null)
        {
            _apps.LaunchOnActivate(restoredProfile.Apps, report);
            try { _windows.Restore(restoredProfile.Windows, report, restoredProfile.Apps.Any(app => app.StartOnActivate)); }
            catch (Exception ex) { report.Warn("Windows", ex.Message); }
            restoredProfile.LastUsedUtc = DateTimeOffset.UtcNow;
            report.Info("Undo", $"Returned to {restoredProfile.Name}.");
        }
        else
        {
            report.Info("Undo", "Restored the hardware state from before the last switch.");
        }

        Document.Runtime.ActiveProfileId = restoredProfile?.Id;
        Document.Runtime.LastSwitchUtc = DateTimeOffset.UtcNow;
        Document.Runtime.LastSwitchCheckpoint = null;
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
        Guid? previousActiveGamePresetId = Document.Runtime.ActiveGamePresetId;
        Document.Profiles.RemoveAt(index);
        if (Document.Runtime.ActiveProfileId == profileId) Document.Runtime.ActiveProfileId = null;
        if (Document.Runtime.ActiveGamePresetId is Guid activePresetId &&
            removed.GamePresets.Any(preset => preset.Id == activePresetId))
        {
            Document.Runtime.ActiveGamePresetId = null;
        }
        try { _repository.Save(Document); }
        catch
        {
            Document.Profiles.Insert(index, removed);
            Document.Runtime.ActiveProfileId = previousActiveProfileId;
            Document.Runtime.ActiveGamePresetId = previousActiveGamePresetId;
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
        profile.Discord ??= new DiscordSettings();
        profile.GamePresets ??= [];
        foreach (GamePreset preset in profile.GamePresets)
        {
            if (preset.Id == Guid.Empty) preset.Id = Guid.NewGuid();
            preset.ProcessName = GameDetectionService.NormalizeProcessName(preset.ProcessName);
            preset.AudioDeviceId ??= string.Empty;
            if (preset.VolumePercent.HasValue)
                preset.VolumePercent = Math.Clamp(preset.VolumePercent.Value, 0, 100);
            preset.Apps ??= [];
            preset.Discord ??= new DiscordSettings();
        }
        profile.GamePresets = profile.GamePresets
            .Where(preset => !string.IsNullOrWhiteSpace(preset.ProcessName))
            .GroupBy(preset => preset.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        profile.GameProcesses = profile.GameProcesses
            .Concat(profile.GamePresets.Select(preset => preset.ProcessName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (Document.Runtime.ActiveGamePresetId is Guid activePresetId &&
            !Document.Profiles.SelectMany(item => item.GamePresets).Any(preset => preset.Id == activePresetId))
        {
            Document.Runtime.ActiveGamePresetId = null;
        }
        _repository.Save(Document);
        ProfilesChanged?.Invoke();
    }

    public void SaveSettings()
    {
        Document.Settings.GamePollSeconds = Math.Clamp(Document.Settings.GamePollSeconds, 1, 30);
        Document.Settings.GameExitGraceSeconds = Math.Clamp(Document.Settings.GameExitGraceSeconds, 0, 300);
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
        GamePollSeconds = settings.GamePollSeconds,
        GameExitGraceSeconds = settings.GameExitGraceSeconds,
        ToggleHotkey = settings.ToggleHotkey,
        OnboardingCompleted = settings.OnboardingCompleted
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
