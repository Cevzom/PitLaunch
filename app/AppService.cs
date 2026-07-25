using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PitLaunch;

internal sealed class AppService
{
    private static readonly Regex ProtocolPattern = new("^[a-zA-Z][a-zA-Z0-9+.\\-]+:", RegexOptions.Compiled);

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
        foreach (AppRule rule in rules.Where(rule => rule.StartOnActivate))
        {
            if (string.IsNullOrWhiteSpace(rule.ExecutablePath)) continue;
            if (IsLocalExecutable(rule.ExecutablePath) && IsRunning(rule))
            {
                report.Info("Apps", $"{rule.DisplayName} is already running.");
                continue;
            }

            if (IsLocalPath(rule.ExecutablePath) && !File.Exists(Environment.ExpandEnvironmentVariables(rule.ExecutablePath)))
            {
                report.Warn("Apps", $"{rule.DisplayName} is missing and was skipped.");
                continue;
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
            }
            catch (Exception ex)
            {
                report.Warn("Apps", $"Could not start {rule.DisplayName}: {ex.Message}");
            }
        }
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

    private static bool IsRunning(AppRule rule)
    {
        List<Process> matches = FindProcesses(rule);
        try { return matches.Count > 0; }
        finally
        {
            foreach (Process process in matches) process.Dispose();
        }
    }

    private static bool IsLocalPath(string value)
    {
        if (ProtocolPattern.IsMatch(value)) return false;
        try { return Path.IsPathRooted(Environment.ExpandEnvironmentVariables(value)); }
        catch { return false; }
    }

    private static bool IsLocalExecutable(string value)
    {
        if (!IsLocalPath(value)) return false;
        try { return string.Equals(Path.GetExtension(value), ".exe", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }
}
