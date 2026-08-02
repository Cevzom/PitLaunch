using System.Runtime.InteropServices;

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
    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
        Continuous = 0x80000000
    }

    private bool _held;

    public bool IsHolding => _held;

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
}
