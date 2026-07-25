using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PitLaunch;

internal sealed class WindowService
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const uint GwOwner = 4;
    private const int DwmwaCloaked = 14;

    public List<WindowSnapshot> Capture(OperationReport? report = null)
    {
        List<LiveWindow> windows = EnumerateWindows();
        Dictionary<string, int> occurrence = new(StringComparer.OrdinalIgnoreCase);
        List<WindowSnapshot> snapshots = [];

        foreach (LiveWindow window in windows)
        {
            WindowPlacement placement = new() { Length = Marshal.SizeOf<WindowPlacement>() };
            if (!GetWindowPlacement(window.Handle, ref placement)) continue;

            string key = MatchKey(window.ProcessPath, window.ProcessName, window.ClassName);
            occurrence.TryGetValue(key, out int matchIndex);
            occurrence[key] = matchIndex + 1;

            snapshots.Add(new WindowSnapshot
            {
                ProcessPath = window.ProcessPath,
                ProcessName = window.ProcessName,
                ClassName = window.ClassName,
                Title = window.Title,
                MatchIndex = matchIndex,
                Flags = placement.Flags,
                ShowCommand = placement.ShowCommand,
                MinX = placement.MinPosition.X,
                MinY = placement.MinPosition.Y,
                MaxX = placement.MaxPosition.X,
                MaxY = placement.MaxPosition.Y,
                Left = placement.NormalPosition.Left,
                Top = placement.NormalPosition.Top,
                Right = placement.NormalPosition.Right,
                Bottom = placement.NormalPosition.Bottom
            });
        }

        report?.Info("Windows", $"Captured {snapshots.Count} window position{(snapshots.Count == 1 ? "" : "s")}.");
        return snapshots;
    }

    public void Restore(IReadOnlyList<WindowSnapshot> snapshots, OperationReport report, bool waitForLaunchedApps)
    {
        if (snapshots.Count == 0)
        {
            report.Info("Windows", "No window positions were captured.");
            return;
        }

        HashSet<int> restoredSnapshotIndices = [];
        HashSet<IntPtr> usedHandles = [];
        Stopwatch stopwatch = Stopwatch.StartNew();

        TimeSpan timeout = waitForLaunchedApps ? TimeSpan.FromSeconds(4) : TimeSpan.FromMilliseconds(750);
        while (stopwatch.Elapsed < timeout && restoredSnapshotIndices.Count < snapshots.Count)
        {
            List<LiveWindow> live = EnumerateWindows();
            for (int i = 0; i < snapshots.Count; i++)
            {
                if (restoredSnapshotIndices.Contains(i)) continue;
                WindowSnapshot snapshot = snapshots[i];
                LiveWindow? match = FindMatch(snapshot, live, usedHandles);
                if (match is null) continue;

                Rectangle destination = Rectangle.FromLTRB(snapshot.Left, snapshot.Top, snapshot.Right, snapshot.Bottom);
                if (!IntersectsAnyScreen(destination))
                {
                    restoredSnapshotIndices.Add(i);
                    continue;
                }

                WindowPlacement placement = new()
                {
                    Length = Marshal.SizeOf<WindowPlacement>(),
                    Flags = snapshot.Flags,
                    ShowCommand = snapshot.ShowCommand,
                    MinPosition = new NativePoint(snapshot.MinX, snapshot.MinY),
                    MaxPosition = new NativePoint(snapshot.MaxX, snapshot.MaxY),
                    NormalPosition = new NativeRect(snapshot.Left, snapshot.Top, snapshot.Right, snapshot.Bottom)
                };

                if (SetWindowPlacement(match.Handle, ref placement))
                {
                    usedHandles.Add(match.Handle);
                    restoredSnapshotIndices.Add(i);
                }
            }

            if (restoredSnapshotIndices.Count < snapshots.Count) Thread.Sleep(250);
        }

        int missing = snapshots.Count - restoredSnapshotIndices.Count;
        report.Info("Windows", $"Restored {restoredSnapshotIndices.Count} window position{(restoredSnapshotIndices.Count == 1 ? "" : "s")}.");
        if (missing > 0)
        {
            report.Warn("Windows", $"{missing} captured window{(missing == 1 ? " was" : "s were")} not open or not on an available display.");
        }
    }

    private static LiveWindow? FindMatch(WindowSnapshot snapshot, List<LiveWindow> live, HashSet<IntPtr> used)
    {
        IEnumerable<LiveWindow> candidates = live.Where(window => !used.Contains(window.Handle));
        if (!string.IsNullOrWhiteSpace(snapshot.ProcessPath))
        {
            List<LiveWindow> exactPath = candidates
                .Where(window => string.Equals(window.ProcessPath, snapshot.ProcessPath, StringComparison.OrdinalIgnoreCase))
                .Where(window => string.Equals(window.ClassName, snapshot.ClassName, StringComparison.Ordinal))
                .ToList();

            LiveWindow? exactTitle = exactPath.FirstOrDefault(window =>
                string.Equals(window.Title, snapshot.Title, StringComparison.Ordinal));
            if (exactTitle is not null) return exactTitle;
            if (snapshot.MatchIndex >= 0 && snapshot.MatchIndex < exactPath.Count) return exactPath[snapshot.MatchIndex];
            if (exactPath.Count > 0) return exactPath[0];
        }

        List<LiveWindow> fallback = candidates
            .Where(window => string.Equals(window.ProcessName, snapshot.ProcessName, StringComparison.OrdinalIgnoreCase))
            .Where(window => string.Equals(window.ClassName, snapshot.ClassName, StringComparison.Ordinal))
            .ToList();
        LiveWindow? fallbackTitle = fallback.FirstOrDefault(window =>
            string.Equals(window.Title, snapshot.Title, StringComparison.Ordinal));
        if (fallbackTitle is not null) return fallbackTitle;
        if (snapshot.MatchIndex >= 0 && snapshot.MatchIndex < fallback.Count) return fallback[snapshot.MatchIndex];
        return fallback.FirstOrDefault();
    }

    private static bool IntersectsAnyScreen(Rectangle rectangle)
    {
        if (rectangle.Width <= 0 || rectangle.Height <= 0) return false;
        return Screen.AllScreens.Any(screen => Rectangle.Intersect(screen.WorkingArea, rectangle).Width >= 32 &&
                                               Rectangle.Intersect(screen.WorkingArea, rectangle).Height >= 32);
    }

    private static List<LiveWindow> EnumerateWindows()
    {
        List<LiveWindow> windows = [];
        uint currentProcessId = (uint)Environment.ProcessId;

        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle) || GetWindow(handle, GwOwner) != IntPtr.Zero) return true;
            if ((GetWindowLongPtr(handle, GwlExStyle).ToInt64() & WsExToolWindow) != 0) return true;
            if (IsCloaked(handle)) return true;

            GetWindowThreadProcessId(handle, out uint processId);
            if (processId == 0 || processId == currentProcessId) return true;

            string title = GetText(handle);
            if (string.IsNullOrWhiteSpace(title)) return true;

            try
            {
                using Process process = Process.GetProcessById((int)processId);
                string processName = process.ProcessName;
                string processPath = string.Empty;
                try { processPath = process.MainModule?.FileName ?? string.Empty; } catch { }
                windows.Add(new LiveWindow(handle, processPath, processName, GetClass(handle), title));
            }
            catch
            {
            }

            return true;
        }, IntPtr.Zero);

        return windows;
    }

    private static bool IsCloaked(IntPtr handle)
    {
        int cloaked = 0;
        int result = DwmGetWindowAttribute(handle, DwmwaCloaked, out cloaked, sizeof(int));
        return result == 0 && cloaked != 0;
    }

    private static string GetText(IntPtr handle)
    {
        int length = GetWindowTextLength(handle);
        if (length <= 0) return string.Empty;
        StringBuilder builder = new(length + 1);
        return GetWindowText(handle, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
    }

    private static string GetClass(IntPtr handle)
    {
        StringBuilder builder = new(256);
        return GetClassName(handle, builder, builder.Capacity) > 0 ? builder.ToString() : string.Empty;
    }

    private static string MatchKey(string path, string processName, string className) =>
        $"{(string.IsNullOrWhiteSpace(path) ? processName : path)}|{className}";

    private sealed record LiveWindow(IntPtr Handle, string ProcessPath, string ProcessName, string ClassName, string Title);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public readonly int X;
        public readonly int Y;
        public NativePoint(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;
        public NativeRect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
    }

    private delegate bool EnumWindowsCallback(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr handle, uint command);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr handle, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(IntPtr handle, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(IntPtr handle, ref WindowPlacement placement);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr handle, int attribute, out int value, int size);
}
