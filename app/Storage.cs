using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PitLaunch;

internal static class AppPaths
{
    public static readonly string DataDirectory = ResolveDataDirectory();
    public static readonly string ProfilesFile = Path.Combine(DataDirectory, "profiles.json");
    public static readonly string LogFile = Path.Combine(DataDirectory, "pitlaunch.log");

    public static void EnsureDataDirectory() => Directory.CreateDirectory(DataDirectory);

    private static string ResolveDataDirectory()
    {
        string? overridePath = Environment.GetEnvironmentVariable("PITLAUNCH_DATA_DIR");
        return string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PitLaunch")
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(overridePath));
    }
}

internal static class AppLog
{
    private const long MaxLogBytes = 2 * 1024 * 1024;
    private static readonly object Gate = new();
    private static volatile bool _suppressed;

    public static void Info(string message) => Write(OperationSeverity.Info, message);
    public static void Error(string message) => Write(OperationSeverity.Error, message);
    public static void SuppressWrites() => _suppressed = true;

    public static void Write(OperationSeverity severity, string message)
    {
        if (_suppressed) return;
        try
        {
            AppPaths.EnsureDataDirectory();
            string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{severity.ToString().ToUpperInvariant()}] {message}{Environment.NewLine}";
            lock (Gate)
            {
                if (File.Exists(AppPaths.LogFile) && new FileInfo(AppPaths.LogFile).Length >= MaxLogBytes)
                {
                    File.Move(AppPaths.LogFile, AppPaths.LogFile + ".previous", true);
                }
                File.AppendAllText(AppPaths.LogFile, line, new UTF8Encoding(false));
            }
        }
        catch
        {
        }
    }
}

internal sealed class ProfileRepository
{
    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public ProfileRepository(string? filePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? AppPaths.ProfilesFile
            : Path.GetFullPath(filePath);
    }

    public string FilePath => _filePath;

    public ProfileDocument Load()
    {
        lock (_gate)
        {
            EnsureParentDirectory();
            if (TryLoad(FilePath, out ProfileDocument document, out string primaryError)) return document;

            bool primaryExists = File.Exists(FilePath);
            if (primaryExists)
            {
                string corruptPath = FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                try { File.Copy(FilePath, corruptPath, true); } catch { }
            }

            foreach ((string candidate, string label) in new[]
            {
                (FilePath + ".tmp", "temporary recovery file"),
                (FilePath + ".bak", "backup")
            })
            {
                if (!TryLoad(candidate, out ProfileDocument recovered, out _)) continue;
                try { File.Copy(candidate, FilePath, true); }
                catch (Exception ex) { AppLog.Error($"Recovered profiles from the {label}, but could not repair profiles.json: {ex.Message}"); }
                AppLog.Write(OperationSeverity.Warning, $"Recovered profiles from the {label} after profiles.json could not be loaded.");
                return recovered;
            }

            if (primaryExists)
            {
                AppLog.Error($"Could not load profiles.json or its recovery files. A clean document was opened. {primaryError}");
            }
            return new ProfileDocument();
        }
    }

    public void Save(ProfileDocument document)
    {
        lock (_gate)
        {
            Normalize(document);
            EnsureParentDirectory();
            string temp = FilePath + ".tmp";
            string backup = FilePath + ".bak";
            File.WriteAllText(temp, JsonSerializer.Serialize(document, _json), new UTF8Encoding(false));

            if (File.Exists(FilePath))
            {
                try { File.Copy(FilePath, backup, true); } catch { }
            }

            File.Move(temp, FilePath, true);
        }
    }

    private bool TryLoad(string path, out ProfileDocument document, out string error)
    {
        document = new ProfileDocument();
        error = string.Empty;
        if (!File.Exists(path)) return false;

        try
        {
            document = JsonSerializer.Deserialize<ProfileDocument>(File.ReadAllText(path), _json)
                ?? throw new InvalidDataException("The file did not contain profile data.");
            Normalize(document);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void EnsureParentDirectory()
    {
        string? directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
    }

    private static void Normalize(ProfileDocument document)
    {
        document.SchemaVersion = Math.Max(1, document.SchemaVersion);
        document.Settings ??= new AppSettings();
        document.Runtime ??= new RuntimeState();
        document.Profiles ??= [];
        document.Settings.GamePollSeconds = Math.Clamp(document.Settings.GamePollSeconds, 1, 30);
        document.Settings.GameExitGraceSeconds = Math.Clamp(document.Settings.GameExitGraceSeconds, 0, 300);
        document.Settings.ToggleHotkey ??= string.Empty;
        // People upgrading from an older build already learned the app by creating a setup.
        // Only a genuinely empty first run should open the welcome guide.
        if (document.Profiles.Count > 0) document.Settings.OnboardingCompleted = true;

        if (document.Runtime.LastSwitchCheckpoint is SwitchCheckpoint checkpoint)
        {
            checkpoint.Display ??= new DisplaySnapshot();
            checkpoint.Display.Monitors ??= [];
            checkpoint.Audio ??= new AudioSnapshot();
            checkpoint.PowerPlanGuid ??= string.Empty;
        }

        foreach (Profile profile in document.Profiles)
        {
            if (profile.Id == Guid.Empty) profile.Id = Guid.NewGuid();
            if (string.IsNullOrWhiteSpace(profile.Name)) profile.Name = "Unnamed profile";
            profile.Hotkey ??= string.Empty;
            profile.Display ??= new DisplaySnapshot();
            profile.Display.Monitors ??= [];
            profile.Audio ??= new AudioSnapshot();
            profile.Windows ??= [];
            profile.Apps ??= [];
            profile.GameProcesses ??= [];
            profile.GamePresets ??= [];
            profile.Discord ??= new DiscordSettings();
            profile.ExpectedControllers ??= [];
            profile.PowerPlanGuid ??= string.Empty;
            NormalizeDiscord(profile.Discord);
            foreach (AppRule app in profile.Apps) NormalizeAppRule(app);

            profile.GameProcesses = profile.GameProcesses
                .Select(GameDetectionService.NormalizeProcessName)
                .Where(process => !string.IsNullOrWhiteSpace(process))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            foreach (string legacyProcess in profile.GameProcesses)
            {
                if (!profile.GamePresets.Any(preset =>
                        string.Equals(GameDetectionService.NormalizeProcessName(preset.ProcessName), legacyProcess,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    profile.GamePresets.Add(new GamePreset { ProcessName = legacyProcess });
                }
            }

            HashSet<Guid> presetIds = [];
            foreach (GamePreset preset in profile.GamePresets)
            {
                if (preset.Id == Guid.Empty || !presetIds.Add(preset.Id))
                {
                    preset.Id = Guid.NewGuid();
                    presetIds.Add(preset.Id);
                }
                preset.ProcessName = GameDetectionService.NormalizeProcessName(preset.ProcessName);
                preset.AudioDeviceId ??= string.Empty;
                if (preset.VolumePercent.HasValue)
                    preset.VolumePercent = Math.Clamp(preset.VolumePercent.Value, 0, 100);
                preset.Apps ??= [];
                foreach (AppRule app in preset.Apps) NormalizeAppRule(app);
                preset.Discord ??= new DiscordSettings();
                NormalizeDiscord(preset.Discord);
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
        }

        if (document.Runtime.ActiveGamePresetId is Guid activePresetId &&
            !document.Profiles.SelectMany(profile => profile.GamePresets).Any(preset => preset.Id == activePresetId))
        {
            document.Runtime.ActiveGamePresetId = null;
        }
    }

    private static void NormalizeDiscord(DiscordSettings settings)
    {
        settings.OutputDeviceId ??= string.Empty;
        settings.MicrophoneDeviceId ??= string.Empty;
        settings.MuteToggleHotkey ??= string.Empty;
        settings.DeafenToggleHotkey ??= string.Empty;
        if (settings.VolumePercent.HasValue)
            settings.VolumePercent = Math.Clamp(settings.VolumePercent.Value, 0, 100);
    }

    private static void NormalizeAppRule(AppRule app)
    {
        app.ExecutablePath ??= string.Empty;
        app.Arguments ??= string.Empty;
        app.WorkingDirectory ??= string.Empty;
        app.AudioDeviceId ??= string.Empty;
        app.LaunchOrder = Math.Clamp(app.LaunchOrder, -10000, 10000);
        app.DelayAfterStartSeconds = Math.Clamp(app.DelayAfterStartSeconds, 0, 300);
        app.ReadyTimeoutSeconds = Math.Clamp(app.ReadyTimeoutSeconds, 1, 300);
        if (app.VolumePercent.HasValue)
            app.VolumePercent = Math.Clamp(app.VolumePercent.Value, 0, 100);
    }
}

internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PitLaunch";
    private const string ShortcutName = "PitLaunch.lnk";

    public static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        ShortcutName);

    public static bool IsEnabled()
    {
        string? executable = Environment.ProcessPath;
        return executable is not null &&
               (IsRegistrationForExecutable(ReadValue(), executable) || IsShortcutForExecutable(executable));
    }

    public static bool HasAnyRegistration() =>
        !string.IsNullOrWhiteSpace(ReadValue()) || File.Exists(ShortcutPath);

    public static bool StartsMinimized()
    {
        string? executable = Environment.ProcessPath;
        if (executable is null) return false;
        return string.Equals(ReadValue()?.Trim(), BuildCommand(executable, true), StringComparison.OrdinalIgnoreCase) ||
               ShortcutMatches(executable, "--background");
    }

    public static bool IsFullyEnabled(bool startMinimized)
    {
        string? executable = Environment.ProcessPath;
        if (executable is null) return false;
        string arguments = startMinimized ? "--background" : "--chooser";
        return string.Equals(ReadValue()?.Trim(), BuildCommand(executable, startMinimized), StringComparison.OrdinalIgnoreCase) &&
               ShortcutMatches(executable, arguments);
    }

    public static void SetEnabled(bool enabled, bool startMinimized)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
            ?? throw new InvalidOperationException("Windows startup settings could not be opened.");

        if (enabled)
        {
            string executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("The PitLaunch executable path is unavailable.");
            key.SetValue(ValueName, BuildCommand(executable, startMinimized), RegistryValueKind.String);
            try
            {
                WriteStartupShortcut(executable, startMinimized ? "--background" : "--chooser");
            }
            catch
            {
                key.DeleteValue(ValueName, false);
                throw;
            }
        }
        else
        {
            key.DeleteValue(ValueName, false);
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
        }
    }

    internal static string BuildCommand(string executable, bool startMinimized)
    {
        if (string.IsNullOrWhiteSpace(executable))
            throw new ArgumentException("An executable path is required.", nameof(executable));
        string fullPath = Path.GetFullPath(executable);
        return $"\"{fullPath}\" {(startMinimized ? "--background" : "--chooser")}";
    }

    internal static bool IsRegistrationForExecutable(string? value, string executable)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(executable)) return false;
        try
        {
            string registered = value.Trim();
            return string.Equals(registered, BuildCommand(executable, false), StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(registered, BuildCommand(executable, true), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string? ReadValue()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
        return key?.GetValue(ValueName) as string;
    }

    internal static bool ShortcutMatches(string executable, string arguments)
    {
        if (!TryReadStartupShortcut(out string target, out string savedArguments)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(target), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(savedArguments.Trim(), arguments, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsShortcutForExecutable(string executable) =>
        ShortcutMatches(executable, "--chooser") || ShortcutMatches(executable, "--background");

    private static void WriteStartupShortcut(string executable, string arguments)
    {
        string? startupDirectory = Path.GetDirectoryName(ShortcutPath);
        if (!string.IsNullOrWhiteSpace(startupDirectory)) Directory.CreateDirectory(startupDirectory);

        object? shellObject = null;
        object? shortcutObject = null;
        try
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException("Windows could not create a startup shortcut.");
            shellObject = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Windows could not open the startup shortcut service.");
            dynamic shell = shellObject;
            shortcutObject = shell.CreateShortcut(ShortcutPath);
            dynamic shortcut = shortcutObject;
            shortcut.TargetPath = Path.GetFullPath(executable);
            shortcut.Arguments = arguments;
            shortcut.WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory;
            shortcut.IconLocation = executable + ",0";
            shortcut.Description = "Start " + AppInfo.ProductName + " after sign-in";
            shortcut.WindowStyle = 1;
            shortcut.Save();
        }
        finally
        {
            ReleaseComObject(shortcutObject);
            ReleaseComObject(shellObject);
        }
    }

    private static bool TryReadStartupShortcut(out string target, out string arguments)
    {
        target = string.Empty;
        arguments = string.Empty;
        if (!File.Exists(ShortcutPath)) return false;

        object? shellObject = null;
        object? shortcutObject = null;
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return false;
            shellObject = Activator.CreateInstance(shellType);
            if (shellObject is null) return false;
            dynamic shell = shellObject;
            shortcutObject = shell.CreateShortcut(ShortcutPath);
            dynamic shortcut = shortcutObject;
            target = (string)shortcut.TargetPath;
            arguments = (string)shortcut.Arguments;
            return !string.IsNullOrWhiteSpace(target);
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseComObject(shortcutObject);
            ReleaseComObject(shellObject);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.FinalReleaseComObject(value); } catch { }
    }
}
