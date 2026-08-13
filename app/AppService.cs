using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PitLaunch;

internal sealed class AppService
{
    private static readonly Regex ProtocolPattern = new("^[a-zA-Z][a-zA-Z0-9+.\\-]+:", RegexOptions.Compiled);
    private readonly AppAudioService _appAudio = new();

    public void CloseOnDeactivate(IEnumerable<AppRule> rules, OperationReport report)
    {
        foreach (AppRule rule in rules.Where(rule => rule.CloseOnDeactivate))
        {
            List<Process> processes = FindProcesses(rule);
            if (processes.Count == 0) continue;

            int closed = 0;
            foreach (Process process in processes)
            {
                using (process)
                {
                    try
                    {
                        if (process.Id == Environment.ProcessId) continue;
                        bool requested = process.CloseMainWindow();
                        if (requested && process.WaitForExit(2500))
                        {
                            closed++;
                            continue;
                        }

                        if (rule.ForceClose && !process.HasExited)
                        {
                            process.Kill(true);
                            process.WaitForExit(2000);
                            closed++;
                        }
                        else if (!process.HasExited)
                        {
                            report.Warn("Apps", $"{rule.DisplayName} did not close; it was left running.");
                        }
                    }
                    catch (Exception ex)
                    {
                        report.Warn("Apps", $"Could not close {rule.DisplayName}: {ex.Message}");
                    }
                }
            }

            if (closed > 0) report.Info("Apps", $"Closed {rule.DisplayName}.");
        }
    }

    public void LaunchOnActivate(IEnumerable<AppRule> rules, OperationReport report)
    {
        List<AppRule> ruleList = rules.ToList();
        foreach (AppRule rule in OrderForLaunch(ruleList))
        {
            LaunchRule(rule, report);
        }

        // A user may want PitLaunch to control Discord or a music player that Windows already
        // starts. Audio settings therefore apply even when "Start" is off.
        foreach (AppRule rule in ruleList.Where(rule =>
                     !rule.StartOnActivate &&
                     (!string.IsNullOrWhiteSpace(rule.AudioDeviceId) || rule.VolumePercent.HasValue)))
        {
            _appAudio.Apply(rule, FindProcessIds(rule), report);
        }
    }

    public void ApplyAudioToProcesses(
        IEnumerable<string> processNames,
        string displayName,
        string audioDeviceId,
        int? volumePercent,
        OperationReport report,
        int waitMilliseconds = 0)
    {
        if (string.IsNullOrWhiteSpace(audioDeviceId) && !volumePercent.HasValue) return;
        int remaining = Math.Clamp(waitMilliseconds, 0, 5000);
        List<int> processIds;
        do
        {
            processIds = FindProcessIdsByNames(processNames);
            if (processIds.Count > 0 || remaining <= 0) break;
            int delay = Math.Min(150, remaining);
            Thread.Sleep(delay);
            remaining -= delay;
        } while (remaining >= 0);

        AppRule audioRule = new()
        {
            ExecutablePath = displayName + ".exe",
            AudioDeviceId = audioDeviceId ?? string.Empty,
            VolumePercent = volumePercent
        };
        _appAudio.Apply(audioRule, processIds, report);
    }

    internal static List<int> FindProcessIdsByNames(IEnumerable<string> processNames)
    {
        HashSet<int> ids = [];
        foreach (string rawName in processNames)
        {
            string name = GameDetectionService.NormalizeProcessName(rawName);
            if (string.IsNullOrWhiteSpace(name)) continue;
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try { if (process.Id != Environment.ProcessId) ids.Add(process.Id); }
                    catch { }
                }
            }
        }
        return ids.ToList();
    }

    internal static IReadOnlyList<AppRule> OrderForLaunch(IEnumerable<AppRule> rules) => rules
        .Select((rule, index) => (Rule: rule, Index: index))
        .Where(item => item.Rule.StartOnActivate)
        .OrderBy(item => item.Rule.LaunchOrder)
        .ThenBy(item => item.Index)
        .Select(item => item.Rule)
        .ToList();

    private void LaunchRule(AppRule rule, OperationReport report)
    {
        if (string.IsNullOrWhiteSpace(rule.ExecutablePath)) return;
        if (IsLocalExecutable(rule.ExecutablePath) && IsRunning(rule))
        {
            report.Info("Apps", $"{rule.DisplayName} is already running.");
            WaitUntilReady(rule, report);
            _appAudio.Apply(rule, FindProcessIds(rule), report);
            DelayAfter(rule, report);
            return;
        }

        if (IsLocalPath(rule.ExecutablePath) && !File.Exists(Environment.ExpandEnvironmentVariables(rule.ExecutablePath)))
        {
            report.Warn("Apps", $"{rule.DisplayName} is missing and was skipped.");
            return;
        }

        try
        {
            string path = Environment.ExpandEnvironmentVariables(rule.ExecutablePath);
            bool useShell = !IsLocalExecutable(path);
            ProcessStartInfo start = new()
            {
                FileName = path,
                Arguments = rule.Arguments ?? string.Empty,
                WorkingDirectory = ResolveWorkingDirectory(rule, path),
                UseShellExecute = useShell,
                WindowStyle = rule.StartHidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
            };
            if (!useShell) start.CreateNoWindow = rule.StartHidden;
            using Process? process = Process.Start(start);
            report.Info("Apps", $"Started {rule.DisplayName}.");
            WaitUntilReady(rule, report, process);
            _appAudio.Apply(rule, FindProcessIds(rule, process?.Id), report);
        }
        catch (Exception ex)
        {
            report.Warn("Apps", $"Could not start {rule.DisplayName}: {ex.Message}");
        }

        DelayAfter(rule, report);
    }

    private static void WaitUntilReady(AppRule rule, OperationReport report, Process? started = null)
    {
        if (!rule.WaitForReady || !IsLocalExecutable(rule.ExecutablePath)) return;
        int timeoutSeconds = Math.Clamp(rule.ReadyTimeoutSeconds, 1, 300);
        Stopwatch timer = Stopwatch.StartNew();

        while (timer.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            List<Process> candidates = FindProcesses(rule);
            if (started is not null && candidates.All(process => process.Id != started.Id))
            {
                try { if (!started.HasExited) candidates.Add(started); }
                catch { }
            }

            try
            {
                foreach (Process process in candidates)
                {
                    try
                    {
                        if (process.HasExited) continue;
                        // Responding works for desktop apps. Services and tray apps commonly have
                        // no main window, so surviving the initial half-second is their readiness signal.
                        bool hasWindow = process.MainWindowHandle != IntPtr.Zero;
                        if ((hasWindow && process.Responding) || (!hasWindow && timer.ElapsedMilliseconds >= 500))
                        {
                            report.Info("Apps", $"{rule.DisplayName} is ready.");
                            return;
                        }
                    }
                    catch { }
                }
            }
            finally
            {
                foreach (Process process in candidates)
                {
                    if (ReferenceEquals(process, started)) continue;
                    process.Dispose();
                }
            }
            Thread.Sleep(100);
        }

        report.Warn("Apps", $"{rule.DisplayName} did not report ready within {timeoutSeconds} seconds; continuing.");
    }

    private static void DelayAfter(AppRule rule, OperationReport report)
    {
        int seconds = Math.Clamp(rule.DelayAfterStartSeconds, 0, 300);
        if (seconds == 0) return;
        report.Info("Apps", $"Waiting {seconds} second{(seconds == 1 ? "" : "s")} before the next application.");
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
    }

    private static string ResolveWorkingDirectory(AppRule rule, string path)
    {
        if (!string.IsNullOrWhiteSpace(rule.WorkingDirectory))
        {
            return Environment.ExpandEnvironmentVariables(rule.WorkingDirectory);
        }

        if (IsLocalExecutable(path)) return Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
        return AppContext.BaseDirectory;
    }

    private static List<Process> FindProcesses(AppRule rule)
    {
        if (!IsLocalExecutable(rule.ExecutablePath)) return [];
        string expanded = Environment.ExpandEnvironmentVariables(rule.ExecutablePath);
        string processName;
        try { processName = Path.GetFileNameWithoutExtension(expanded); }
        catch { return []; }
        if (string.IsNullOrWhiteSpace(processName)) return [];

        List<Process> matches = [];
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            try
            {
                string? runningPath = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(runningPath) &&
                    string.Equals(Path.GetFullPath(runningPath), Path.GetFullPath(expanded), StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(process);
                }
                else
                {
                    process.Dispose();
                }
            }
            catch
            {
                process.Dispose();
            }
        }

        return matches;
    }

    internal static List<int> FindProcessIds(AppRule rule, int? additionalProcessId = null)
    {
        List<Process> matches = FindProcesses(rule);
        try
        {
            HashSet<int> ids = matches.Select(process => process.Id).ToHashSet();
            if (additionalProcessId.HasValue) ids.Add(additionalProcessId.Value);
            return ids.ToList();
        }
        finally
        {
            foreach (Process process in matches) process.Dispose();
        }
    }

    private static bool IsRunning(AppRule rule)
    {
        List<Process> matches = FindProcesses(rule);
        try { return matches.Count > 0; }
        finally
        {
            foreach (Process process in matches) process.Dispose();
        }
    }

    internal static bool IsLocalPath(string value)
    {
        if (ProtocolPattern.IsMatch(value)) return false;
        try { return Path.IsPathRooted(Environment.ExpandEnvironmentVariables(value)); }
        catch { return false; }
    }

    internal static bool IsLocalExecutable(string value)
    {
        if (!IsLocalPath(value)) return false;
        try { return string.Equals(Path.GetExtension(value), ".exe", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
