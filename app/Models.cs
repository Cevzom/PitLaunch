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
}

internal sealed class RuntimeState
{
    public Guid? ActiveProfileId { get; set; }
    public DateTimeOffset? LastSwitchUtc { get; set; }
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

    /// <summary>Hold the screen awake while this setup is active. Wheel input does not reset the Windows idle timer.</summary>
    public bool KeepAwake { get; set; }

    /// <summary>Game controllers this setup expects, by product name. Missing ones are reported on activation.</summary>
    public List<string> ExpectedControllers { get; set; } = [];

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
    KeepCurrent
}

internal sealed record DisplaySetupRequest(
    IReadOnlyList<string> EnabledDevicePaths,
    string PrimaryDevicePath,
    DisplayLayoutMode LayoutMode);

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
    GameDetected,
    GameExited
}

internal sealed record ProfileSwitchCompleted(Profile Profile, ActivationSource Source, OperationReport Report);
