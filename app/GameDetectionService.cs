using System.Diagnostics;

namespace PitLaunch;

internal sealed class GameDetectionService : IDisposable
{
    private readonly Func<ProfileDocument> _document;
    private readonly System.Threading.Timer _timer;
    private Guid? _trackedProfileId;
    private Guid? _restoreProfileId;
    private int _checking;

    public event Action<Guid, ActivationSource>? ActivationRequested;

    public GameDetectionService(Func<ProfileDocument> document)
    {
        _document = document;
        _timer = new System.Threading.Timer(Check, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Refresh()
    {
        ProfileDocument document = _document();
        int period = Math.Clamp(document.Settings.GamePollSeconds, 1, 30) * 1000;
        _timer.Change(document.Settings.GameDetectionEnabled ? period : Timeout.Infinite, period);
        if (!document.Settings.GameDetectionEnabled)
        {
            _trackedProfileId = null;
            _restoreProfileId = null;
        }
    }

    private void Check(object? state)
    {
        if (Interlocked.Exchange(ref _checking, 1) != 0) return;
        try
        {
            ProfileDocument document = _document();
            if (!document.Settings.GameDetectionEnabled) return;
            HashSet<string> running = new(StringComparer.OrdinalIgnoreCase);
            foreach (Process process in Process.GetProcesses())
            {
                try { running.Add(process.ProcessName); }
                catch { }
                finally { process.Dispose(); }
            }

            if (_trackedProfileId.HasValue)
            {
                Profile? tracked = document.Profiles.FirstOrDefault(profile => profile.Id == _trackedProfileId.Value);
                bool stillRunning = tracked is not null && tracked.GameProcesses
                    .Select(NormalizeProcessName)
                    .Any(running.Contains);
                if (!stillRunning)
                {
                    Guid? restore = _restoreProfileId;
                    _trackedProfileId = null;
                    _restoreProfileId = null;
                    if (restore.HasValue && document.Profiles.Any(profile => profile.Id == restore.Value))
                    {
                        ActivationRequested?.Invoke(restore.Value, ActivationSource.GameExited);
                    }
                }

                return;
            }

            foreach (Profile profile in document.Profiles)
            {
                if (profile.GameProcesses.Count == 0) continue;
                bool detected = profile.GameProcesses.Select(NormalizeProcessName).Any(running.Contains);
                if (!detected) continue;

                _trackedProfileId = profile.Id;
                _restoreProfileId = document.Runtime.ActiveProfileId;
                if (document.Runtime.ActiveProfileId != profile.Id)
                {
                    ActivationRequested?.Invoke(profile.Id, ActivationSource.GameDetected);
                }
                break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Game detection failed: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    internal static List<string> RunningProcessCandidates()
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                if (process.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(process.MainWindowTitle))
                {
                    names.Add(process.ProcessName);
                }
            }
            catch { }
            finally { process.Dispose(); }
        }

        return names.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    internal static string NormalizeProcessName(string value)
    {
        string trimmed = (value ?? string.Empty).Trim();
        try { return Path.GetFileNameWithoutExtension(trimmed); }
        catch { return trimmed; }
    }

    public void Dispose() => _timer.Dispose();
}
