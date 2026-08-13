using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PitLaunch;

/// <summary>
/// Keeps the screen alive while a setup asks for it.
///
/// Wheel and pedal input does not reset the Windows idle timer the way a mouse or keyboard
/// does, so a long stint with your hands on the wheel can still blank the screen or sleep the
/// machine. Holding a display request for as long as the setup is active is the documented fix.
/// </summary>
internal sealed class PowerService : IDisposable
{
    private static readonly Regex PlanPattern = new(
        "(?<guid>[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12})\\s*(?:\\((?<name>.*)\\))?\\s*(?<active>\\*)?\\s*$",
        RegexOptions.Compiled);
    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
        Continuous = 0x80000000
    }

    private bool _held;

    public bool IsHolding => _held;

    public List<PowerPlanOption> ListPowerPlans()
    {
        CommandResult result = RunPowerCfg("/LIST");
        if (result.ExitCode != 0) return [];
        return result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => PlanPattern.Match(line.Trim()))
            .Where(match => match.Success)
            .Select(match => new PowerPlanOption(
                match.Groups["guid"].Value.ToLowerInvariant(),
                string.IsNullOrWhiteSpace(match.Groups["name"].Value) ? "Power plan" : match.Groups["name"].Value.Trim(),
                match.Groups["active"].Success))
            .DistinctBy(plan => plan.Guid, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string GetActivePowerPlanGuid()
    {
        CommandResult result = RunPowerCfg("/GETACTIVESCHEME");
        if (result.ExitCode != 0) return string.Empty;
        Match match = PlanPattern.Match(result.Output.Trim());
        if (match.Success) return match.Groups["guid"].Value.ToLowerInvariant();
        Match guid = Regex.Match(result.Output, "[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}");
        return guid.Success ? guid.Value.ToLowerInvariant() : string.Empty;
    }

    public void SetPowerPlan(string? planGuid, OperationReport report)
    {
        if (string.IsNullOrWhiteSpace(planGuid)) return;
        if (!Guid.TryParse(planGuid, out Guid parsed))
        {
            report.Warn("Power", "The saved power plan identifier is invalid; the current plan was kept.");
            return;
        }

        string normalized = parsed.ToString("D");
        if (string.Equals(GetActivePowerPlanGuid(), normalized, StringComparison.OrdinalIgnoreCase))
        {
            report.Info("Power", "The requested Windows power plan is already active.");
            return;
        }

        CommandResult result = RunPowerCfg("/SETACTIVE " + normalized);
        if (result.ExitCode == 0)
            report.Info("Power", "Activated the saved Windows power plan.");
        else
            report.Warn("Power", "Windows could not activate the saved power plan: " + CleanError(result));
    }

    /// <summary>Starts or stops keeping the display and system awake. Safe to call repeatedly.</summary>
    public void SetKeepAwake(bool keepAwake, OperationReport? report = null)
    {
        if (keepAwake == _held) return;

        ExecutionState request = keepAwake
            ? ExecutionState.Continuous | ExecutionState.DisplayRequired | ExecutionState.SystemRequired
            : ExecutionState.Continuous;

        // A zero return means Windows refused the request outright.
        if (SetThreadExecutionState(request) == 0)
        {
            report?.Warn("Power", keepAwake
                ? "Windows refused to keep the screen awake for this setup."
                : "Windows refused to release the screen-awake request.");
            return;
        }

        _held = keepAwake;
        AppLog.Info(keepAwake
            ? "Power: holding the screen awake for the active setup."
            : "Power: released the screen-awake request.");
        report?.Info("Power", keepAwake
            ? "The screen will stay awake while this setup is active."
            : "Normal sleep and screensaver behaviour restored.");
    }

    /// <summary>Releases the request so the machine can sleep normally again once PitLaunch exits.</summary>
    public void Dispose()
    {
        if (!_held) return;
        try { SetThreadExecutionState(ExecutionState.Continuous); } catch { }
        _held = false;
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(ExecutionState flags);

    private static CommandResult RunPowerCfg(string arguments)
    {
        try
        {
            using Process process = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "powercfg.exe"),
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch { }
                return new CommandResult(-1, output, "powercfg timed out");
            }
            return new CommandResult(process.ExitCode, output, error);
        }
        catch (Exception ex) { return new CommandResult(-1, string.Empty, ex.Message); }
    }

    private static string CleanError(CommandResult result)
    {
        string message = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        return string.IsNullOrWhiteSpace(message) ? $"powercfg exited with code {result.ExitCode}" : message.Trim();
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);
}
