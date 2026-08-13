using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PitLaunch;

internal sealed record SupportBundleResult(string FilePath, IReadOnlyList<string> Entries);

/// <summary>
/// Creates a deliberately allow-listed diagnostic archive. The raw profile file is never copied:
/// profile names, ids, device ids, executable paths/arguments, window titles, controller names and
/// game process names are all omitted. Log and self-test text goes through a second redaction pass.
/// </summary>
internal sealed partial class SupportBundleService
{
    private const int MaxLogCharacters = 512 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _dataDirectory;
    private readonly string _profilesFile;
    private readonly string _logFile;
    private readonly string _selfTestFile;

    public SupportBundleService() : this(
        AppPaths.DataDirectory,
        AppPaths.ProfilesFile,
        AppPaths.LogFile,
        Path.Combine(AppPaths.DataDirectory, "self-test.json"))
    {
    }

    internal SupportBundleService(
        string dataDirectory,
        string profilesFile,
        string logFile,
        string selfTestFile)
    {
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _profilesFile = Path.GetFullPath(profilesFile);
        _logFile = Path.GetFullPath(logFile);
        _selfTestFile = Path.GetFullPath(selfTestFile);
    }

    public SupportBundleResult ExportFromDisk(string destinationPath)
    {
        List<string> warnings = [];
        ProfileDocument document = ReadProfiles(warnings);
        return Export(destinationPath, document, warnings);
    }

    public SupportBundleResult Export(string destinationPath, ProfileDocument document) =>
        Export(destinationPath, document ?? throw new ArgumentNullException(nameof(document)), []);

    private SupportBundleResult Export(
        string destinationPath,
        ProfileDocument document,
        IReadOnlyList<string> initialWarnings)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
            throw new ArgumentException("A destination path is required.", nameof(destinationPath));

        string destination = Path.GetFullPath(destinationPath);
        if (Directory.Exists(destination))
            throw new IOException("The support bundle destination is a directory.");
        if (File.Exists(destination))
            throw new IOException("A file already exists at the support bundle destination.");

        string? parent = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(parent)) throw new IOException("The destination directory is unavailable.");
        Directory.CreateDirectory(parent);

        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        List<string> entries = [];
        List<string> warnings = [.. initialWarnings];
        try
        {
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (ZipArchive archive = new(output, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8))
            {
                WriteJson(archive, "system.json", BuildSystemSummary(), entries);
                WriteJson(archive, "profiles-sanitized.json", BuildSanitizedProfiles(document), entries);
                IReadOnlyList<string> privateProfileValues = CollectPrivateProfileValues(document);
                AddSanitizedLog(archive, _logFile, "logs/pitlaunch.log", privateProfileValues, entries, warnings);
                AddSanitizedLog(
                    archive,
                    _logFile + ".previous",
                    "logs/pitlaunch.previous.log",
                    privateProfileValues,
                    entries,
                    warnings);
                AddSanitizedSelfTest(archive, entries, warnings);

                WriteJson(archive, "manifest.json", new BundleManifest(
                    1,
                    AppInfo.ProductName,
                    AppInfo.Version,
                    DateTimeOffset.UtcNow,
                    "Allow-listed diagnostics only; raw profiles, identifiers, paths and arguments are excluded.",
                    warnings), entries);
            }

            File.Move(temporary, destination, false);
            return new SupportBundleResult(destination, entries.AsReadOnly());
        }
        catch
        {
            try { File.Delete(temporary); } catch { }
            throw;
        }
    }

    internal static SanitizedProfileDocument BuildSanitizedProfiles(ProfileDocument document)
    {
        List<Profile> profiles = document.Profiles ?? [];
        Guid? activeId = document.Runtime?.ActiveProfileId;
        int? activeProfile = null;
        List<SanitizedProfile> sanitized = [];
        for (int index = 0; index < profiles.Count; index++)
        {
            Profile profile = profiles[index];
            if (activeId == profile.Id) activeProfile = index + 1;
            List<SanitizedMonitor> monitors = (profile.Display?.Monitors ?? [])
                .Where(monitor => monitor.Enabled)
                .Select(monitor => new SanitizedMonitor(
                    monitor.Primary,
                    monitor.Width,
                    monitor.Height,
                    Math.Round(monitor.RefreshHz, 2),
                    monitor.X,
                    monitor.Y,
                    monitor.Rotation,
                    monitor.Scaling))
                .ToList();
            List<AppRule> apps = profile.Apps ?? [];
            sanitized.Add(new SanitizedProfile(
                index + 1,
                profile.Kind.ToString(),
                profile.RigDisplay.ToString(),
                monitors,
                profile.Audio?.Playback is not null,
                profile.Audio?.Communications is not null,
                profile.Audio?.Microphone is not null,
                profile.Windows?.Count ?? 0,
                apps.Count,
                apps.Count(rule => rule.StartOnActivate),
                apps.Count(rule => rule.CloseOnDeactivate),
                apps.Count(rule => rule.ForceClose),
                apps.Count(rule => rule.StartHidden),
                apps.Count(rule => !string.IsNullOrWhiteSpace(rule.AudioDeviceId)),
                apps.Count(rule => rule.VolumePercent.HasValue),
                apps.Count(rule => rule.WaitForReady),
                apps.Sum(rule => Math.Clamp(rule.DelayAfterStartSeconds, 0, 300)),
                profile.GameProcesses?.Count ?? 0,
                profile.GamePresets?.Count ?? 0,
                profile.GamePresets?.Sum(preset => preset.Apps?.Count ?? 0) ?? 0,
                profile.Discord?.HasOverrides == true,
                profile.GamePresets?.Count(preset => preset.CustomizeDiscord) ?? 0,
                profile.ExpectedControllers?.Count ?? 0,
                !string.IsNullOrWhiteSpace(profile.Hotkey),
                profile.KeepAwake,
                !string.IsNullOrWhiteSpace(profile.PowerPlanGuid),
                profile.EnableHdr switch { true => "Enabled", false => "Disabled", null => "Unchanged" }));
        }

        AppSettings settings = document.Settings ?? new AppSettings();
        return new SanitizedProfileDocument(
            Math.Max(1, document.SchemaVersion),
            new SanitizedSettings(
                settings.LaunchOnStartup,
                settings.StartMinimized,
                settings.ConfirmBeforeSwitch,
                settings.GameDetectionEnabled,
                Math.Clamp(settings.GamePollSeconds, 1, 30),
                Math.Clamp(settings.GameExitGraceSeconds, 0, 300),
                !string.IsNullOrWhiteSpace(settings.ToggleHotkey)),
            activeProfile,
            document.Runtime?.LastSwitchCheckpoint is not null,
            sanitized);
    }

    internal string SanitizeDiagnosticText(string value)
    {
        string text = value ?? string.Empty;
        foreach ((string path, string replacement) in SensitiveDirectories())
        {
            if (!string.IsNullOrWhiteSpace(path))
                text = text.Replace(path.TrimEnd(Path.DirectorySeparatorChar), replacement, StringComparison.OrdinalIgnoreCase);
        }

        text = BearerSecretRegex().Replace(text, "$1<redacted>");
        text = AssignedSecretRegex().Replace(text, "$1=<redacted>");
        text = UrlQueryRegex().Replace(text, "$1?<redacted>");
        text = EmailRegex().Replace(text, "<email>");
        text = GuidRegex().Replace(text, "<id>");
        return text;
    }

    private ProfileDocument ReadProfiles(List<string> warnings)
    {
        if (!File.Exists(_profilesFile))
        {
            warnings.Add("No saved profile file was present.");
            return new ProfileDocument();
        }

        try
        {
            string json = ReadSharedText(_profilesFile);
            return JsonSerializer.Deserialize<ProfileDocument>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                MaxDepth = 64
            }) ?? new ProfileDocument();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            warnings.Add("Saved profiles could not be read: " + SanitizeDiagnosticText(ex.Message));
            return new ProfileDocument();
        }
    }

    private void AddSanitizedLog(
        ZipArchive archive,
        string sourcePath,
        string entryName,
        IReadOnlyList<string> privateProfileValues,
        List<string> entries,
        List<string> warnings)
    {
        if (!File.Exists(sourcePath)) return;
        try
        {
            string text = ReadSharedText(sourcePath);
            if (text.Length > MaxLogCharacters)
                text = "[Older log content omitted.]" + Environment.NewLine + text[^MaxLogCharacters..];
            text = SanitizeDiagnosticText(text);
            foreach (string privateValue in privateProfileValues)
                text = text.Replace(privateValue, "<profile-data>", StringComparison.OrdinalIgnoreCase);
            WriteText(archive, entryName, text, entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add(Path.GetFileName(sourcePath) + " could not be included: " +
                         SanitizeDiagnosticText(ex.Message));
        }
    }

    private void AddSanitizedSelfTest(ZipArchive archive, List<string> entries, List<string> warnings)
    {
        if (!File.Exists(_selfTestFile)) return;
        try
        {
            using JsonDocument source = JsonDocument.Parse(ReadSharedText(_selfTestFile), new JsonDocumentOptions
            {
                MaxDepth = 16
            });
            JsonElement root = source.RootElement;
            bool? passed = TryBoolean(root, "passed");
            string timestamp = TryString(root, "timestamp");
            List<SanitizedSelfTestCheck> checks = [];
            if (root.TryGetProperty("checks", out JsonElement sourceChecks) &&
                sourceChecks.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement check in sourceChecks.EnumerateArray().Take(250))
                {
                    checks.Add(new SanitizedSelfTestCheck(
                        SanitizeDiagnosticText(TryString(check, "name")),
                        TryBoolean(check, "passed"),
                        SanitizeDiagnosticText(TryString(check, "error"))));
                }
            }
            WriteJson(archive, "self-test.json", new SanitizedSelfTest(passed, timestamp, checks), entries);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            warnings.Add("The latest self-test could not be included: " + SanitizeDiagnosticText(ex.Message));
        }
    }

    private static SystemSummary BuildSystemSummary() => new(
        AppInfo.ProductName,
        AppInfo.Version,
        DateTimeOffset.UtcNow,
        RuntimeInformation.OSDescription,
        RuntimeInformation.OSArchitecture.ToString(),
        RuntimeInformation.ProcessArchitecture.ToString(),
        RuntimeInformation.FrameworkDescription,
        Environment.Is64BitOperatingSystem,
        Environment.Is64BitProcess,
        CultureInfo.CurrentCulture.Name);

    private IEnumerable<(string Path, string Replacement)> SensitiveDirectories()
    {
        yield return (_dataDirectory, "%PITLAUNCH_DATA%");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%");
        yield return (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%");
        yield return (Path.GetTempPath(), "%TEMP%\\");
    }

    private static IReadOnlyList<string> CollectPrivateProfileValues(ProfileDocument document)
    {
        HashSet<string> values = new(StringComparer.OrdinalIgnoreCase);
        void Add(string? value)
        {
            string text = value?.Trim() ?? string.Empty;
            if (text.Length >= 3) values.Add(text);
        }
        void AddDisplay(DisplaySnapshot? display)
        {
            foreach (MonitorSnapshot monitor in display?.Monitors ?? [])
            {
                Add(monitor.DevicePath);
                Add(monitor.FriendlyName);
            }
        }
        void AddAudio(AudioSnapshot? audio)
        {
            foreach (AudioEndpointSnapshot? endpoint in new[] { audio?.Playback, audio?.Communications, audio?.Microphone })
            {
                Add(endpoint?.DeviceId);
                Add(endpoint?.FriendlyName);
            }
        }

        foreach (Profile profile in document.Profiles ?? [])
        {
            Add(profile.Name);
            Add(profile.PowerPlanGuid);
            AddDisplay(profile.Display);
            AddAudio(profile.Audio);
            foreach (WindowSnapshot window in profile.Windows ?? [])
            {
                Add(window.ProcessPath);
                Add(window.ProcessName);
                Add(window.ClassName);
                Add(window.Title);
            }
            foreach (AppRule app in profile.Apps ?? [])
            {
                Add(app.ExecutablePath);
                Add(app.Arguments);
                Add(app.WorkingDirectory);
                Add(app.AudioDeviceId);
            }
            Add(profile.Discord?.OutputDeviceId);
            Add(profile.Discord?.MicrophoneDeviceId);
            foreach (GamePreset preset in profile.GamePresets ?? [])
            {
                Add(preset.ProcessName);
                Add(preset.AudioDeviceId);
                Add(preset.Discord?.OutputDeviceId);
                Add(preset.Discord?.MicrophoneDeviceId);
                foreach (AppRule app in preset.Apps ?? [])
                {
                    Add(app.ExecutablePath);
                    Add(app.Arguments);
                    Add(app.WorkingDirectory);
                    Add(app.AudioDeviceId);
                }
            }
            foreach (string process in profile.GameProcesses ?? []) Add(process);
            foreach (string controller in profile.ExpectedControllers ?? []) Add(controller);
        }

        if (document.Runtime?.LastSwitchCheckpoint is SwitchCheckpoint checkpoint)
        {
            Add(checkpoint.PowerPlanGuid);
            AddDisplay(checkpoint.Display);
            AddAudio(checkpoint.Audio);
        }

        return values.OrderByDescending(value => value.Length).ToList();
    }

    private static string ReadSharedText(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static void WriteJson<T>(ZipArchive archive, string name, T value, List<string> entries) =>
        WriteText(archive, name, JsonSerializer.Serialize(value, Json), entries);

    private static void WriteText(ZipArchive archive, string name, string text, List<string> entries)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using Stream stream = entry.Open();
        using StreamWriter writer = new(stream, new UTF8Encoding(false));
        writer.Write(text);
        entries.Add(name);
    }

    private static bool? TryBoolean(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static string TryString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    [GeneratedRegex(@"(?i)\b(Bearer\s+)[A-Za-z0-9._~+/=-]+")]
    private static partial Regex BearerSecretRegex();

    [GeneratedRegex(@"(?i)\b([a-z0-9_-]*(?:password|passwd|pwd|token|secret|api[-_ ]?key|authorization|cookie)[a-z0-9_-]*)\b\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\r\n,;]+)")]
    private static partial Regex AssignedSecretRegex();

    [GeneratedRegex(@"(?i)\b(https://[^\s?#]+(?:/[^\s?#]*)?)\?[^\s#]+")]
    private static partial Regex UrlQueryRegex();

    [GeneratedRegex(@"(?i)(?<![\w.+-])[\w.+-]+@[\w.-]+\.[A-Z]{2,}(?![\w.-])")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b")]
    private static partial Regex GuidRegex();

    internal sealed record SanitizedProfileDocument(
        int SchemaVersion,
        SanitizedSettings Settings,
        int? ActiveProfile,
        bool UndoCheckpointAvailable,
        IReadOnlyList<SanitizedProfile> Profiles);

    internal sealed record SanitizedSettings(
        bool LaunchOnStartup,
        bool StartMinimized,
        bool ConfirmBeforeSwitch,
        bool GameDetectionEnabled,
        int GamePollSeconds,
        int GameExitGraceSeconds,
        bool ToggleHotkeyConfigured);

    internal sealed record SanitizedProfile(
        int Number,
        string Kind,
        string RigDisplay,
        IReadOnlyList<SanitizedMonitor> Displays,
        bool PlaybackAudioConfigured,
        bool CommunicationsAudioConfigured,
        bool MicrophoneConfigured,
        int WindowCount,
        int ApplicationRuleCount,
        int ApplicationsStarted,
        int ApplicationsClosed,
        int ApplicationsForceClosed,
        int ApplicationsStartedHidden,
        int ApplicationAudioRoutesConfigured,
        int ApplicationVolumesConfigured,
        int ApplicationsWaitingForReady,
        int TotalLaunchDelaySeconds,
        int GameTriggerCount,
        int GamePresetCount,
        int GamePresetApplicationCount,
        bool DiscordConfigured,
        int GamePresetDiscordOverrideCount,
        int ExpectedControllerCount,
        bool HotkeyConfigured,
        bool KeepAwake,
        bool PowerPlanConfigured,
        string HdrPreference);

    internal sealed record SanitizedMonitor(
        bool Primary,
        uint Width,
        uint Height,
        double RefreshHz,
        int X,
        int Y,
        uint Rotation,
        uint Scaling);

    private sealed record SystemSummary(
        string Product,
        string Version,
        DateTimeOffset GeneratedAtUtc,
        string OperatingSystem,
        string OsArchitecture,
        string ProcessArchitecture,
        string Framework,
        bool Is64BitOperatingSystem,
        bool Is64BitProcess,
        string Culture);

    private sealed record BundleManifest(
        int SchemaVersion,
        string Product,
        string Version,
        DateTimeOffset GeneratedAtUtc,
        string Privacy,
        IReadOnlyList<string> Warnings);

    private sealed record SanitizedSelfTest(bool? Passed, string Timestamp, IReadOnlyList<SanitizedSelfTestCheck> Checks);
    private sealed record SanitizedSelfTestCheck(string Name, bool? Passed, string Error);
}
