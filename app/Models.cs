using System.Text.Json.Serialization;

namespace PitLaunch;

internal sealed class ProfileDocument
{
    public int SchemaVersion { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public RuntimeState Runtime { get; set; } = new();
    public List<Profile> Profiles { get; set; } = [];
}

internal sealed class AppSettings
{
    public bool LaunchOnStartup { get; set; }
    public bool StartMinimized { get; set; }
    public bool ConfirmBeforeSwitch { get; set; } = true;
    public bool GameDetectionEnabled { get; set; }
    public int GamePollSeconds { get; set; } = 2;

    /// <summary>
    /// How long a detected game may disappear before PitLaunch returns to the previous setup.
    /// This prevents launchers, updates, and short game restarts from causing an unwanted switch.
    /// </summary>
    public int GameExitGraceSeconds { get; set; } = 10;
    public string ToggleHotkey { get; set; } = string.Empty;
    public bool OnboardingCompleted { get; set; }
}

internal sealed class RuntimeState
{
    public Guid? ActiveProfileId { get; set; }
    public DateTimeOffset? LastSwitchUtc { get; set; }
    public SwitchCheckpoint? LastSwitchCheckpoint { get; set; }
    public Guid? ActiveGamePresetId { get; set; }
}

/// <summary>A restart-safe snapshot of the known-good state immediately before a switch.</summary>
internal sealed class SwitchCheckpoint
{
    public DateTimeOffset CapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public Guid? ActiveProfileId { get; set; }
    public DisplaySnapshot Display { get; set; } = new();
    public AudioSnapshot Audio { get; set; } = new();
    public bool KeepAwake { get; set; }
    public string PowerPlanGuid { get; set; } = string.Empty;
    public bool? EnableHdr { get; set; }
}

internal sealed class Profile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New profile";
    public DateTimeOffset CapturedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedUtc { get; set; }
    public SetupKind Kind { get; set; } = SetupKind.Auto;
    public RigDisplayVariant RigDisplay { get; set; } = RigDisplayVariant.Auto;
    public string Hotkey { get; set; } = string.Empty;
    public DisplaySnapshot Display { get; set; } = new();
    public AudioSnapshot Audio { get; set; } = new();
    public List<WindowSnapshot> Windows { get; set; } = [];
    public List<AppRule> Apps { get; set; } = [];
    public List<string> GameProcesses { get; set; } = [];
    public List<GamePreset> GamePresets { get; set; } = [];
    public DiscordSettings Discord { get; set; } = new();

    /// <summary>Hold the screen awake while this setup is active. Wheel input does not reset the Windows idle timer.</summary>
    public bool KeepAwake { get; set; }

    /// <summary>Game controllers this setup expects, by product name. Missing ones are reported on activation.</summary>
    public List<string> ExpectedControllers { get; set; } = [];

    /// <summary>Windows power scheme to activate, or empty to leave the current scheme alone.</summary>
    public string PowerPlanGuid { get; set; } = string.Empty;

    /// <summary>True/false changes HDR on capable active displays; null leaves HDR unchanged.</summary>
    public bool? EnableHdr { get; set; }

    [JsonIgnore]
    public string CaptureSummary
    {
        get
        {
            int enabled = Display.Monitors.Count(m => m.Enabled);
            string displayText = enabled == 1 ? "1 display" : $"{enabled} displays";
            return $"{displayText}, {Windows.Count} windows";
        }
    }

    public override string ToString() => Name;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum SetupKind
{
    Auto,
    Desk,
    SimRacing
}

[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum RigDisplayVariant
{
    Auto,
    SingleScreen,
    DualScreen,
    TripleScreen,
    QuadScreen,
    Ultrawide,
    Vr
}

internal sealed class DisplaySnapshot
{
    public List<MonitorSnapshot> Monitors { get; set; } = [];
}

internal enum DisplayLayoutMode
{
    Recommended,
    Horizontal,
    KeepCurrent,
    Custom
}

/// <summary>
/// <paramref name="CustomPositions"/> carries hand-placed top-left corners in display
/// coordinates, keyed by device path. It is only read for <see cref="DisplayLayoutMode.Custom"/>;
/// every other mode computes positions and ignores it.
/// </summary>
internal sealed record DisplaySetupRequest(
    IReadOnlyList<string> EnabledDevicePaths,
    string PrimaryDevicePath,
    DisplayLayoutMode LayoutMode,
    IReadOnlyDictionary<string, MonitorPosition>? CustomPositions = null);

internal readonly record struct MonitorPosition(int X, int Y);

internal sealed record DisplayDeviceOption(
    string DevicePath,
    string FriendlyName,
    bool IsActive,
    bool IsPrimary,
    uint Width,
    uint Height,
    double RefreshHz);

internal sealed class MonitorSnapshot
{
    public string DevicePath { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = "Display";
    public bool Enabled { get; set; }
    public bool Primary { get; set; }
    public uint Width { get; set; }
    public uint Height { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public uint PixelFormat { get; set; }
    public uint Rotation { get; set; }
    public uint Scaling { get; set; }
    public uint ScanLineOrdering { get; set; }
    public uint RefreshNumerator { get; set; }
    public uint RefreshDenominator { get; set; } = 1;

    [JsonIgnore]
    public double RefreshHz => RefreshDenominator == 0 ? 0 : (double)RefreshNumerator / RefreshDenominator;
}

internal sealed class AudioSnapshot
{
    public AudioEndpointSnapshot? Playback { get; set; }
    public AudioEndpointSnapshot? Communications { get; set; }
    public AudioEndpointSnapshot? Microphone { get; set; }
}

internal sealed class AudioEndpointSnapshot
{
    public string DeviceId { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = "Audio device";
}

internal sealed class WindowSnapshot
{
    public string ProcessPath { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public string ClassName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int MatchIndex { get; set; }
    public int Flags { get; set; }
    public int ShowCommand { get; set; }
    public int MinX { get; set; }
    public int MinY { get; set; }
    public int MaxX { get; set; }
    public int MaxY { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
}

internal sealed class AppRule
{
    public string ExecutablePath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public bool StartOnActivate { get; set; } = true;
    public bool CloseOnDeactivate { get; set; }
    public bool ForceClose { get; set; }
    public bool StartHidden { get; set; }

    /// <summary>Lower values start first. Rules with the same value retain their list order.</summary>
    public int LaunchOrder { get; set; }

    /// <summary>Delay after this application is handled before the next rule starts.</summary>
    public int DelayAfterStartSeconds { get; set; }

    /// <summary>Wait for the process to become responsive before continuing the sequence.</summary>
    public bool WaitForReady { get; set; }

    /// <summary>Maximum readiness wait. Defaults to 15 seconds for newly-created rules.</summary>
    public int ReadyTimeoutSeconds { get; set; } = 15;

    /// <summary>Preferred endpoint for this app. Empty leaves Windows' routing unchanged.</summary>
    public string AudioDeviceId { get; set; } = string.Empty;

    /// <summary>Per-app session volume from 0-100; null leaves it unchanged.</summary>
    public int? VolumePercent { get; set; }

    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ExecutablePath)) return "Application";
            try { return Path.GetFileNameWithoutExtension(ExecutablePath); }
            catch { return ExecutablePath; }
        }
    }
}

internal enum OperationSeverity
{
    Info,
    Warning,
    Error
}

internal sealed record OperationMessage(OperationSeverity Severity, string Area, string Message);

internal sealed class OperationReport
{
    public string Operation { get; }
    public List<OperationMessage> Messages { get; } = [];
    public TimeSpan Duration { get; set; }
    public bool HasErrors => Messages.Any(m => m.Severity == OperationSeverity.Error);
    public bool HasWarnings => Messages.Any(m => m.Severity == OperationSeverity.Warning);

    public OperationReport(string operation) => Operation = operation;

    public void Info(string area, string message) => Add(OperationSeverity.Info, area, message);
    public void Warn(string area, string message) => Add(OperationSeverity.Warning, area, message);
    public void Error(string area, string message) => Add(OperationSeverity.Error, area, message);

    private void Add(OperationSeverity severity, string area, string message)
    {
        Messages.Add(new OperationMessage(severity, area, message));
        AppLog.Write(severity, $"{area}: {message}");
    }

    public string Summary
    {
        get
        {
            bool displayRecovery = Operation == "Restore all displays";
            int warnings = Messages.Count(m => m.Severity == OperationSeverity.Warning);
            int errors = Messages.Count(m => m.Severity == OperationSeverity.Error);
            if (errors > 0) return $"Finished with {errors} error{(errors == 1 ? "" : "s")}";
            if (warnings > 0) return displayRecovery
                ? $"Displays restored with {warnings} warning{(warnings == 1 ? "" : "s")}"
                : $"Switched with {warnings} warning{(warnings == 1 ? "" : "s")}";
            if (displayRecovery) return "All displays restored";
            if (Operation.StartsWith("Capture ", StringComparison.Ordinal)) return "Profile captured";
            if (Operation.StartsWith("Recapture ", StringComparison.Ordinal)) return "Profile updated";
            return "Profile active";
        }
    }
}

internal enum ActivationSource
{
    User,
    Tray,
    Hotkey,
    CommandLine,
    Integration,
    GameDetected,
    GameExited
}

/// <summary>
/// Optional overrides applied only while one detected game is running. The parent profile still
/// owns displays, power, HDR, and normal applications; a preset changes only the game session.
/// </summary>
internal sealed class GamePreset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProcessName { get; set; } = string.Empty;
    public string AudioDeviceId { get; set; } = string.Empty;
    public int? VolumePercent { get; set; }
    public List<AppRule> Apps { get; set; } = [];
    public bool CustomizeDiscord { get; set; }
    public DiscordSettings Discord { get; set; } = new();
    public bool ToggleDiscordMuteForSession { get; set; }
    public bool ToggleDiscordDeafenForSession { get; set; }

    [JsonIgnore]
    public bool HasOverrides =>
        !string.IsNullOrWhiteSpace(AudioDeviceId) ||
        VolumePercent.HasValue ||
        Apps.Count > 0 ||
        CustomizeDiscord ||
        ToggleDiscordMuteForSession ||
        ToggleDiscordDeafenForSession;
}

/// <summary>
/// Discord controls for a setup or game preset. Output and volume target Discord's process;
/// microphone changes the Windows communications input, which Discord follows when its input is
/// set to Default. No account token or bot connection is used.
/// </summary>
internal sealed class DiscordSettings
{
    public bool LaunchOnActivate { get; set; }
    public string OutputDeviceId { get; set; } = string.Empty;
    public string MicrophoneDeviceId { get; set; } = string.Empty;
    public int? VolumePercent { get; set; }
    public string MuteToggleHotkey { get; set; } = string.Empty;
    public string DeafenToggleHotkey { get; set; } = string.Empty;

    [JsonIgnore]
    public bool HasOverrides =>
        LaunchOnActivate ||
        !string.IsNullOrWhiteSpace(OutputDeviceId) ||
        !string.IsNullOrWhiteSpace(MicrophoneDeviceId) ||
        VolumePercent.HasValue ||
        !string.IsNullOrWhiteSpace(MuteToggleHotkey) ||
        !string.IsNullOrWhiteSpace(DeafenToggleHotkey);
}

internal sealed record ProfileSwitchCompleted(Profile Profile, ActivationSource Source, OperationReport Report);

internal sealed record PowerPlanOption(string Guid, string Name, bool IsActive);

internal sealed record HdrStatus(bool IsSupported, bool? IsEnabled, int SupportedDisplayCount, int ActiveDisplayCount);

internal sealed record ReadinessItem(OperationSeverity Severity, string Area, string Message);

internal sealed class ReadinessReport
{
    public List<ReadinessItem> Items { get; } = [];
    public bool IsReady => Items.All(item => item.Severity == OperationSeverity.Info);
    public bool CanSwitch => Items.All(item => item.Severity != OperationSeverity.Error);

    public void Info(string area, string message) => Items.Add(new ReadinessItem(OperationSeverity.Info, area, message));
    public void Warn(string area, string message) => Items.Add(new ReadinessItem(OperationSeverity.Warning, area, message));
    public void Error(string area, string message) => Items.Add(new ReadinessItem(OperationSeverity.Error, area, message));
}
