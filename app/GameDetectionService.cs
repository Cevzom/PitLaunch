using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PitLaunch;

internal sealed record GameActivationRequest(Guid ProfileId, ActivationSource Source, string ProcessName);

internal sealed class GameDetectionService : IDisposable
{
    private readonly Func<ProfileDocument> _document;
    private readonly System.Threading.Timer _timer;
    private readonly HashSet<int> _trackedProcessIds = [];
    private Guid? _trackedProfileId;
    private Guid? _restoreProfileId;
    private string _trackedProcessName = string.Empty;
    private DateTimeOffset? _missingSinceUtc;
    private int _checking;

    public event Action<GameActivationRequest>? ActivationRequested;
    public event Action<string>? WarningRaised;

    public GameDetectionService(Func<ProfileDocument> document)
    {
        _document = document;
        _timer = new System.Threading.Timer(Check, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Refresh(bool automationAllowed = true)
    {
        ProfileDocument document = _document();
        int period = Math.Clamp(document.Settings.GamePollSeconds, 1, 30) * 1000;
        bool enabled = automationAllowed && document.Settings.GameDetectionEnabled;
        _timer.Change(enabled ? period : Timeout.Infinite, period);
        if (!enabled && _trackedProfileId.HasValue)
        {
            Guid? restore = _restoreProfileId ?? _trackedProfileId;
            string processName = _trackedProcessName;
            ResetTracking();
            if (restore.HasValue && document.Profiles.Any(profile => profile.Id == restore.Value))
            {
                ActivationRequested?.Invoke(new GameActivationRequest(
                    restore.Value,
                    ActivationSource.GameExited,
                    processName));
            }
        }
        else if (!enabled)
        {
            ResetTracking();
        }
    }

    private void Check(object? state)
    {
        if (Interlocked.Exchange(ref _checking, 1) != 0) return;
        try
        {
            ProfileDocument document = _document();
            if (!document.Settings.GameDetectionEnabled) return;
            List<ProcessSnapshot> running = ProcessSnapshot.Capture();

            if (_trackedProfileId.HasValue)
            {
                Profile? tracked = document.Profiles.FirstOrDefault(profile => profile.Id == _trackedProfileId.Value);
                bool stillRunning = tracked is not null && IsTrackedGroupRunning(tracked, running);
                if (stillRunning)
                {
                    _missingSinceUtc = null;
                    return;
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                _missingSinceUtc ??= now;
                int graceSeconds = Math.Clamp(document.Settings.GameExitGraceSeconds, 0, 300);
                if (!HasExitGraceElapsed(_missingSinceUtc, now, graceSeconds)) return;

                Guid? restore = _restoreProfileId ?? _trackedProfileId;
                string processName = _trackedProcessName;
                string trackedName = tracked?.Name ?? "tracked";
                ResetTracking();
                if (restore.HasValue && document.Profiles.FirstOrDefault(profile => profile.Id == restore.Value) is Profile previous)
                {
                    AppLog.Info($"Game detection: {trackedName} game closed after the {graceSeconds}s grace period, returning to {previous.Name}.");
                    ActivationRequested?.Invoke(new GameActivationRequest(
                        restore.Value,
                        ActivationSource.GameExited,
                        processName));
                }
                return;
            }

            List<(Profile Profile, List<ProcessSnapshot> Matches)> candidates = [];
            foreach (Profile profile in document.Profiles)
            {
                HashSet<string> configured = profile.GameProcesses
                    .Select(NormalizeProcessName)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (configured.Count == 0) continue;

                List<ProcessSnapshot> matches = running.Where(process => configured.Contains(process.Name)).ToList();
                if (matches.Count == 0) continue;

                candidates.Add((profile, matches));
            }

            if (candidates.Count > 0)
            {
                (Profile profile, List<ProcessSnapshot> matches) = candidates[0];
                if (candidates.Count > 1)
                {
                    string names = string.Join(", ", candidates.Select(candidate => candidate.Profile.Name));
                    string warning = $"The same running game matches multiple setups ({names}). {profile.Name} was selected; remove the duplicate game rule.";
                    AppLog.Write(OperationSeverity.Warning, "Game detection: " + warning);
                    WarningRaised?.Invoke(warning);
                }

                Guid? previous = document.Runtime.ActiveProfileId;
                _trackedProfileId = profile.Id;
                _restoreProfileId = previous == profile.Id ? null : previous;
                _trackedProcessName = matches[0].Name;
                _missingSinceUtc = null;
                _trackedProcessIds.Clear();
                foreach (ProcessSnapshot match in matches) _trackedProcessIds.Add(match.Id);
                ExpandTrackedDescendants(running);

                if (previous != profile.Id)
                {
                    AppLog.Info($"Game detection: {matches[0].Name} started, switching to {profile.Name}.");
                    ActivationRequested?.Invoke(new GameActivationRequest(
                        profile.Id,
                        ActivationSource.GameDetected,
                        matches[0].Name));
                }
                else
                {
                    AppLog.Info($"Game detection: {matches[0].Name} started on {profile.Name}; applying its game preset.");
                    ActivationRequested?.Invoke(new GameActivationRequest(
                        profile.Id,
                        ActivationSource.GameDetected,
                        matches[0].Name));
                }
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

    private bool IsTrackedGroupRunning(Profile tracked, List<ProcessSnapshot> running)
    {
        HashSet<string> configured = tracked.GameProcesses
            .Select(NormalizeProcessName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Pick up a relaunched executable even when Windows gave it a new process id.
        foreach (ProcessSnapshot process in running.Where(process => configured.Contains(process.Name)))
            _trackedProcessIds.Add(process.Id);

        // Launchers often hand off to a differently named child. Keep every descendant seen
        // during the session, including children whose original parent has already exited.
        ExpandTrackedDescendants(running);
        return running.Any(process => _trackedProcessIds.Contains(process.Id));
    }

    private void ExpandTrackedDescendants(List<ProcessSnapshot> running)
    {
        bool changed;
        do
        {
            changed = false;
            foreach (ProcessSnapshot process in running)
            {
                if (_trackedProcessIds.Contains(process.ParentId) && _trackedProcessIds.Add(process.Id))
                    changed = true;
            }
        } while (changed);
    }

    private void ResetTracking()
    {
        _trackedProfileId = null;
        _restoreProfileId = null;
        _trackedProcessName = string.Empty;
        _missingSinceUtc = null;
        _trackedProcessIds.Clear();
    }

    internal static List<string> RunningProcessCandidates() => ProcessSnapshot.Capture()
        .Where(process => process.HasWindow)
        .Select(process => process.Name)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    internal static string NormalizeProcessName(string value)
    {
        string trimmed = (value ?? string.Empty).Trim();
        try { return Path.GetFileNameWithoutExtension(trimmed); }
        catch { return trimmed; }
    }

    internal static bool HasExitGraceElapsed(DateTimeOffset? missingSinceUtc, DateTimeOffset nowUtc, int graceSeconds) =>
        missingSinceUtc.HasValue &&
        nowUtc - missingSinceUtc.Value >= TimeSpan.FromSeconds(Math.Clamp(graceSeconds, 0, 300));

    internal void CheckNowForTest() => Check(null);

    public void Dispose() => _timer.Dispose();

    private sealed record ProcessSnapshot(int Id, int ParentId, string Name, bool HasWindow)
    {
        private const uint SnapshotProcesses = 0x00000002;
        private static readonly IntPtr InvalidHandle = new(-1);

        public static List<ProcessSnapshot> Capture()
        {
            Dictionary<int, (int ParentId, string Name)> entries = CaptureProcessTree();
            HashSet<int> windowed = [];
            foreach (Process process in Process.GetProcesses())
            {
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(process.MainWindowTitle))
                        windowed.Add(process.Id);
                }
                catch { }
                finally { process.Dispose(); }
            }

            return entries.Select(entry => new ProcessSnapshot(
                    entry.Key,
                    entry.Value.ParentId,
                    NormalizeProcessName(entry.Value.Name),
                    windowed.Contains(entry.Key)))
                .ToList();
        }

        private static Dictionary<int, (int ParentId, string Name)> CaptureProcessTree()
        {
            Dictionary<int, (int ParentId, string Name)> result = [];
            IntPtr snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
            if (snapshot == InvalidHandle) return CaptureWithoutParents();
            try
            {
                ProcessEntry32 entry = new() { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
                if (!Process32First(snapshot, ref entry)) return result;
                do
                {
                    result[(int)entry.ProcessId] = ((int)entry.ParentProcessId, entry.ExecutableFile ?? string.Empty);
                    entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
                } while (Process32Next(snapshot, ref entry));
                return result;
            }
            finally { CloseHandle(snapshot); }
        }

        private static Dictionary<int, (int ParentId, string Name)> CaptureWithoutParents()
        {
            Dictionary<int, (int ParentId, string Name)> result = [];
            foreach (Process process in Process.GetProcesses())
            {
                try { result[process.Id] = (0, process.ProcessName); }
                catch { }
                finally { process.Dispose(); }
            }
            return result;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry32
        {
            public uint Size;
            public uint Usage;
            public uint ProcessId;
            public IntPtr DefaultHeapId;
            public uint ModuleId;
            public uint Threads;
            public uint ParentProcessId;
            public int PriorityClassBase;
            public uint Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExecutableFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
