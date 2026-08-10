using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PitLaunch;

internal sealed class DisplayService
{
    private const uint QdcAllPaths = 1;
    private const uint QdcOnlyActivePaths = 2;
    private const uint PathActive = 0x00000001;
    private const uint ModeIndexInvalid = 0xffffffff;
    private const uint ModeInfoTypeSource = 1;
    private const uint PixelFormat32Bpp = 4;
    private const uint ScanLineProgressive = 1;
    private const uint DeviceInfoGetTargetName = 2;
    private const uint DeviceInfoGetTargetPreferredMode = 3;
    private const uint SdcUseSuppliedDisplayConfig = 0x00000020;
    private const uint SdcValidate = 0x00000040;
    private const uint SdcApply = 0x00000080;
    private const uint SdcSaveToDatabase = 0x00000200;
    private const uint SdcAllowChanges = 0x00000400;
    private const int ErrorInsufficientBuffer = 122;

    public DisplaySnapshot Capture(OperationReport? report = null)
    {
        QueryPaths(QdcOnlyActivePaths, out PathInfo[] activePaths, out ModeInfo[] activeModes);
        QueryPaths(QdcAllPaths, out PathInfo[] allPaths, out ModeInfo[] allModes);

        Dictionary<string, MonitorSnapshot> monitors = new(StringComparer.OrdinalIgnoreCase);
        foreach (MonitorSnapshot monitor in ReadActiveMonitors(activePaths, activeModes))
        {
            monitors[monitor.DevicePath] = monitor;
        }

        foreach (PathInfo path in allPaths)
        {
            TargetDescriptor target = GetTarget(path);
            if (string.IsNullOrWhiteSpace(target.DevicePath) || monitors.ContainsKey(target.DevicePath)) continue;

            monitors[target.DevicePath] = CreateInactiveMonitor(path, target);
        }

        DisplaySnapshot result = new()
        {
            Monitors = monitors.Values
                .OrderByDescending(m => m.Enabled)
                .ThenByDescending(m => m.Primary)
                .ThenBy(m => m.X)
                .ThenBy(m => m.Y)
                .ThenBy(m => m.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
                .ToList()
        };

        int enabledCount = result.Monitors.Count(m => m.Enabled);
        report?.Info("Displays", $"Captured {enabledCount} active display{(enabledCount == 1 ? "" : "s")}.");
        return result;
    }

    public List<DisplayDeviceOption> ListConnectedDisplays()
    {
        List<MonitorSnapshot> monitors = DiscoverConnectedMonitors();
        return monitors
            .Select(monitor => new DisplayDeviceOption(
                monitor.DevicePath,
                monitor.FriendlyName,
                monitor.Enabled,
                monitor.Primary,
                monitor.Width,
                monitor.Height,
                monitor.RefreshHz))
            .OrderByDescending(device => device.IsPrimary)
            .ThenByDescending(device => device.IsActive)
            .ThenBy(device => device.FriendlyName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public DisplaySnapshot BuildAllConnectedSnapshot()
    {
        List<DisplayDeviceOption> devices = ListConnectedDisplays();
        if (devices.Count == 0)
        {
            throw new InvalidOperationException("Windows did not report any connected displays.");
        }

        DisplayDeviceOption primary = devices.FirstOrDefault(device => device.IsPrimary)
            ?? devices.FirstOrDefault(device => device.IsActive)
            ?? devices[0];
        return BuildSnapshot(new DisplaySetupRequest(
            devices.Select(device => device.DevicePath).ToList(),
            primary.DevicePath,
            DisplayLayoutMode.Recommended));
    }

    public DisplaySnapshot BuildSnapshot(DisplaySetupRequest request)
    {
        if (request.EnabledDevicePaths.Count == 0)
        {
            throw new InvalidOperationException("Choose at least one display.");
        }

        List<MonitorSnapshot> discovered = DiscoverConnectedMonitors();
        Dictionary<string, MonitorSnapshot> byPath = discovered.ToDictionary(
            monitor => monitor.DevicePath,
            StringComparer.OrdinalIgnoreCase);
        List<MonitorSnapshot> enabled = [];
        foreach (string devicePath in request.EnabledDevicePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!byPath.TryGetValue(devicePath, out MonitorSnapshot? monitor))
            {
                throw new InvalidOperationException("A selected display was disconnected. Refresh the device list and try again.");
            }

            MonitorSnapshot copy = CloneMonitor(monitor);
            if (copy.Width == 0 || copy.Height == 0)
            {
                throw new InvalidOperationException($"Windows did not report a usable resolution for {copy.FriendlyName}.");
            }
            enabled.Add(copy);
        }

        MonitorSnapshot primary = enabled.FirstOrDefault(monitor =>
            DevicePathEquals(monitor.DevicePath, request.PrimaryDevicePath)) ?? enabled[0];
        bool canKeepCurrent = enabled.All(monitor => monitor.Enabled) &&
                              (enabled.Count == 1 || enabled.Select(monitor => (monitor.X, monitor.Y)).Distinct().Count() == enabled.Count);

        foreach (MonitorSnapshot monitor in enabled)
        {
            monitor.Enabled = true;
            monitor.Primary = DevicePathEquals(monitor.DevicePath, primary.DevicePath);
        }

        bool keepCurrent = request.LayoutMode == DisplayLayoutMode.KeepCurrent ||
                           request.LayoutMode == DisplayLayoutMode.Recommended && canKeepCurrent;
        if (request.LayoutMode == DisplayLayoutMode.Custom && request.CustomPositions is { Count: > 0 })
        {
            // Hand-placed corners win outright. A display the user never moved keeps whatever
            // Windows reports, so adding a screen to a custom layout does not reset the rest.
            foreach (MonitorSnapshot monitor in enabled)
            {
                if (request.CustomPositions.TryGetValue(monitor.DevicePath, out MonitorPosition placed))
                {
                    monitor.X = placed.X;
                    monitor.Y = placed.Y;
                }
            }
            NormalizeAroundPrimary(enabled, primary);
        }
        else if (keepCurrent && canKeepCurrent)
        {
            NormalizeAroundPrimary(enabled, primary);
        }
        else
        {
            ArrangeDisplays(enabled, primary, request.LayoutMode);
        }

        HashSet<string> selectedPaths = enabled
            .Select(monitor => monitor.DevicePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<MonitorSnapshot> result = [.. enabled];
        foreach (MonitorSnapshot monitor in discovered.Where(monitor => !selectedPaths.Contains(monitor.DevicePath)))
        {
            MonitorSnapshot disabled = CloneMonitor(monitor);
            disabled.Enabled = false;
            disabled.Primary = false;
            disabled.X = 0;
            disabled.Y = 0;
            result.Add(disabled);
        }

        return new DisplaySnapshot { Monitors = result };
    }

    public DisplayCheckpoint CaptureCheckpoint()
    {
        QueryPaths(QdcOnlyActivePaths, out PathInfo[] paths, out ModeInfo[] modes);
        if (paths.Length == 0) throw new InvalidOperationException("Windows reported no active display paths.");
        return new DisplayCheckpoint(paths, modes);
    }

    public void Restore(DisplaySnapshot snapshot, OperationReport report)
    {
        List<MonitorSnapshot> desired = snapshot.Monitors.Where(m => m.Enabled).ToList();
        if (desired.Count == 0)
        {
            report.Warn("Displays", "This profile has no active displays. The current display setup was kept.");
            return;
        }

        QueryPaths(QdcOnlyActivePaths, out PathInfo[] activePaths, out ModeInfo[] activeModes);
        QueryPaths(QdcAllPaths, out PathInfo[] allPaths, out _);
        List<MonitorSnapshot> current = ReadActiveMonitors(activePaths, activeModes);
        List<PathCandidate> candidates = allPaths.Select(path => new PathCandidate(path, GetTarget(path))).ToList();
        HashSet<string> activePathKeys = activePaths.Select(PathKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> usedSources = new(StringComparer.OrdinalIgnoreCase);
        List<ResolvedMonitor> resolved = [];

        foreach (MonitorSnapshot monitor in desired)
        {
            PathCandidate? best = candidates
                .Where(candidate => DevicePathEquals(candidate.Target.DevicePath, monitor.DevicePath))
                .Where(candidate => candidate.Path.TargetInfo.TargetAvailable != 0)
                .Where(candidate => !usedSources.Contains(SourceKey(candidate.Path)))
                .OrderBy(candidate => CandidateScore(candidate, activePathKeys))
                .FirstOrDefault();

            if (best is null)
            {
                report.Warn("Displays", $"{monitor.FriendlyName} is not connected and was skipped.");
                continue;
            }

            usedSources.Add(SourceKey(best.Path));
            resolved.Add(new ResolvedMonitor(monitor, best.Path));
        }

        if (resolved.Count == 0)
        {
            report.Warn("Displays", "None of this profile's displays are connected. The current setup was kept.");
            return;
        }

        ResolvedMonitor primary = resolved.FirstOrDefault(item => item.Snapshot.Primary) ?? resolved[0];
        int originX = primary.Snapshot.X;
        int originY = primary.Snapshot.Y;
        bool capturedPrimaryPresent = resolved.Any(item => item.Snapshot.Primary);
        if (!capturedPrimaryPresent)
        {
            report.Warn("Displays", $"The captured primary display is missing. {primary.Snapshot.FriendlyName} was made primary.");
        }

        if (LayoutMatchesCurrent(resolved, current, primary, originX, originY))
        {
            report.Info("Displays", "Display layout is already active.");
            return;
        }

        (PathInfo[] selectedPaths, ModeInfo[] selectedModes) = BuildArrays(resolved, originX, originY, exactRefresh: true);

        uint validateFlags = SdcValidate | SdcUseSuppliedDisplayConfig | SdcAllowChanges;
        int validation = SetDisplayConfig(
            (uint)selectedPaths.Length,
            selectedPaths,
            (uint)selectedModes.Length,
            selectedModes,
            validateFlags);
        if (validation != 0)
        {
            (PathInfo[] relaxedPaths, ModeInfo[] relaxedModes) = BuildArrays(resolved, originX, originY, exactRefresh: false);
            int relaxedValidation = SetDisplayConfig(
                (uint)relaxedPaths.Length,
                relaxedPaths,
                (uint)relaxedModes.Length,
                relaxedModes,
                validateFlags);
            if (relaxedValidation != 0)
            {
                throw new DisplayApplyException(
                    $"Windows will not accept this display layout (errors {validation}, {relaxedValidation}). " +
                    "Create the setup again in PitLaunch and choose a different display arrangement.");
            }

            report.Warn("Displays", "Windows rejected the saved refresh rates, so the layout is applied with default refresh rates instead.");
            selectedPaths = relaxedPaths;
            selectedModes = relaxedModes;
        }

        uint applyFlags = SdcApply | SdcUseSuppliedDisplayConfig | SdcAllowChanges | SdcSaveToDatabase;
        int result = SetDisplayConfig(
            (uint)selectedPaths.Length,
            selectedPaths,
            (uint)selectedModes.Length,
            selectedModes,
            applyFlags);
        if (result != 0)
        {
            throw new DisplayApplyException($"Windows could not apply the display layout (error {result}). The previous layout was kept or restored.");
        }

        HashSet<string> expected = resolved.Select(item => item.Snapshot.DevicePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!WaitForActiveSet(expected, TimeSpan.FromSeconds(6)))
        {
            throw new DisplayApplyException("Windows accepted the layout but did not finish activating the expected displays.");
        }

        string names = string.Join(", ", resolved.Select(item => item.Snapshot.FriendlyName));
        report.Info("Displays", $"Restored {names}.");
    }

    public DisplayValidation ValidateSnapshot(DisplaySnapshot snapshot)
    {
        try
        {
            List<MonitorSnapshot> desired = snapshot.Monitors.Where(m => m.Enabled).ToList();
            if (desired.Count == 0)
            {
                return new DisplayValidation(false, "the profile has no active displays");
            }

            QueryPaths(QdcOnlyActivePaths, out PathInfo[] activePaths, out _);
            QueryPaths(QdcAllPaths, out PathInfo[] allPaths, out _);
            List<PathCandidate> candidates = allPaths.Select(path => new PathCandidate(path, GetTarget(path))).ToList();
            HashSet<string> activePathKeys = activePaths.Select(PathKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            HashSet<string> usedSources = new(StringComparer.OrdinalIgnoreCase);
            List<ResolvedMonitor> resolved = [];

            foreach (MonitorSnapshot monitor in desired)
            {
                PathCandidate? best = candidates
                    .Where(candidate => DevicePathEquals(candidate.Target.DevicePath, monitor.DevicePath))
                    .Where(candidate => candidate.Path.TargetInfo.TargetAvailable != 0)
                    .Where(candidate => !usedSources.Contains(SourceKey(candidate.Path)))
                    .OrderBy(candidate => CandidateScore(candidate, activePathKeys))
                    .FirstOrDefault();
                if (best is null) continue;
                usedSources.Add(SourceKey(best.Path));
                resolved.Add(new ResolvedMonitor(monitor, best.Path));
            }

            if (resolved.Count == 0)
            {
                return new DisplayValidation(false, "none of the profile's displays are connected right now");
            }

            ResolvedMonitor primary = resolved.FirstOrDefault(item => item.Snapshot.Primary) ?? resolved[0];
            (PathInfo[] paths, ModeInfo[] modes) = BuildArrays(resolved, primary.Snapshot.X, primary.Snapshot.Y, exactRefresh: true);
            uint validateFlags = SdcValidate | SdcUseSuppliedDisplayConfig | SdcAllowChanges;
            int exact = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, validateFlags);
            if (exact == 0) return new DisplayValidation(true, null);

            (paths, modes) = BuildArrays(resolved, primary.Snapshot.X, primary.Snapshot.Y, exactRefresh: false);
            int relaxed = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, validateFlags);
            if (relaxed == 0)
            {
                return new DisplayValidation(true, "Windows will use default refresh rates when switching to it");
            }

            return new DisplayValidation(false, $"Windows rejected the layout (error {exact})");
        }
        catch (Exception ex)
        {
            return new DisplayValidation(false, ex.Message);
        }
    }

    private static (PathInfo[] Paths, ModeInfo[] Modes) BuildArrays(
        IReadOnlyList<ResolvedMonitor> resolved,
        int originX,
        int originY,
        bool exactRefresh)
    {
        PathInfo[] selectedPaths = new PathInfo[resolved.Count];
        ModeInfo[] selectedModes = new ModeInfo[resolved.Count];

        for (int i = 0; i < resolved.Count; i++)
        {
            ResolvedMonitor item = resolved[i];
            MonitorSnapshot monitor = item.Snapshot;
            PathInfo path = item.Path;

            path.Flags = PathActive;
            path.SourceInfo.ModeInfoIndex = (uint)i;
            path.TargetInfo.ModeInfoIndex = ModeIndexInvalid;
            path.TargetInfo.Rotation = monitor.Rotation == 0 ? path.TargetInfo.Rotation : monitor.Rotation;
            path.TargetInfo.Scaling = monitor.Scaling == 0 ? path.TargetInfo.Scaling : monitor.Scaling;
            if (exactRefresh && monitor.RefreshNumerator > 0)
            {
                path.TargetInfo.RefreshRateNumerator = monitor.RefreshNumerator;
                path.TargetInfo.RefreshRateDenominator = Math.Max(1, monitor.RefreshDenominator);
                // SetDisplayConfig rejects a specified refresh rate whose scan-line ordering is
                // unspecified (error 87), which is what inactive candidate paths carry.
                if (monitor.ScanLineOrdering != 0)
                {
                    path.TargetInfo.ScanLineOrdering = monitor.ScanLineOrdering;
                }
                else if (path.TargetInfo.ScanLineOrdering == 0)
                {
                    path.TargetInfo.ScanLineOrdering = ScanLineProgressive;
                }
            }
            else if (!exactRefresh)
            {
                path.TargetInfo.RefreshRateNumerator = 0;
                path.TargetInfo.RefreshRateDenominator = 0;
                path.TargetInfo.ScanLineOrdering = 0;
            }

            int x = monitor.X - originX;
            int y = monitor.Y - originY;
            selectedPaths[i] = path;
            selectedModes[i] = CreateSourceMode(path.SourceInfo, monitor.Width, monitor.Height, monitor.PixelFormat, x, y);
        }

        return (selectedPaths, selectedModes);
    }

    private static List<MonitorSnapshot> DiscoverConnectedMonitors()
    {
        QueryPaths(QdcOnlyActivePaths, out PathInfo[] activePaths, out ModeInfo[] activeModes);
        Dictionary<string, MonitorSnapshot> active = ReadActiveMonitors(activePaths, activeModes)
            .ToDictionary(monitor => monitor.DevicePath, StringComparer.OrdinalIgnoreCase);
        QueryPaths(QdcAllPaths, out PathInfo[] allPaths, out _);

        Dictionary<string, MonitorSnapshot> connected = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string devicePath, MonitorSnapshot monitor) in active)
        {
            connected[devicePath] = CloneMonitor(monitor);
        }

        foreach (PathInfo path in allPaths)
        {
            if (path.TargetInfo.TargetAvailable == 0) continue;
            TargetDescriptor target = GetTarget(path);
            if (string.IsNullOrWhiteSpace(target.DevicePath) || connected.ContainsKey(target.DevicePath)) continue;
            connected[target.DevicePath] = CreateInactiveMonitor(path, target);
        }

        return connected.Values.ToList();
    }

    private static MonitorSnapshot CreateInactiveMonitor(PathInfo path, TargetDescriptor target)
    {
        uint width = 0;
        uint height = 0;
        uint refreshNumerator = 0;
        uint refreshDenominator = 1;
        uint scanLineOrdering = path.TargetInfo.ScanLineOrdering;
        if (TryGetPreferredMode(path, out TargetPreferredMode preferred))
        {
            width = preferred.Width > 0 ? preferred.Width : preferred.TargetMode.SignalInfo.ActiveSize.Width;
            height = preferred.Height > 0 ? preferred.Height : preferred.TargetMode.SignalInfo.ActiveSize.Height;
            refreshNumerator = preferred.TargetMode.SignalInfo.VSyncFrequency.Numerator;
            refreshDenominator = Math.Max(1, preferred.TargetMode.SignalInfo.VSyncFrequency.Denominator);
            if (preferred.TargetMode.SignalInfo.ScanLineOrdering != 0)
            {
                scanLineOrdering = preferred.TargetMode.SignalInfo.ScanLineOrdering;
            }
        }

        return new MonitorSnapshot
        {
            DevicePath = target.DevicePath,
            FriendlyName = target.FriendlyName,
            Enabled = false,
            Width = width,
            Height = height,
            PixelFormat = PixelFormat32Bpp,
            Rotation = path.TargetInfo.Rotation,
            Scaling = path.TargetInfo.Scaling,
            ScanLineOrdering = scanLineOrdering,
            RefreshNumerator = refreshNumerator,
            RefreshDenominator = refreshDenominator
        };
    }

    private static bool TryGetPreferredMode(PathInfo path, out TargetPreferredMode preferred)
    {
        preferred = new TargetPreferredMode
        {
            Type = DeviceInfoGetTargetPreferredMode,
            Size = (uint)Marshal.SizeOf<TargetPreferredMode>(),
            AdapterId = path.TargetInfo.AdapterId,
            Id = path.TargetInfo.Id
        };
        return DisplayConfigGetDeviceInfo(ref preferred) == 0;
    }

    private static MonitorSnapshot CloneMonitor(MonitorSnapshot monitor) => new()
    {
        DevicePath = monitor.DevicePath,
        FriendlyName = monitor.FriendlyName,
        Enabled = monitor.Enabled,
        Primary = monitor.Primary,
        Width = monitor.Width,
        Height = monitor.Height,
        X = monitor.X,
        Y = monitor.Y,
        PixelFormat = monitor.PixelFormat,
        Rotation = monitor.Rotation,
        Scaling = monitor.Scaling,
        ScanLineOrdering = monitor.ScanLineOrdering,
        RefreshNumerator = monitor.RefreshNumerator,
        RefreshDenominator = monitor.RefreshDenominator
    };

    private static void NormalizeAroundPrimary(IReadOnlyList<MonitorSnapshot> monitors, MonitorSnapshot primary)
    {
        int originX = primary.X;
        int originY = primary.Y;
        foreach (MonitorSnapshot monitor in monitors)
        {
            monitor.X -= originX;
            monitor.Y -= originY;
        }
    }

    private static void ArrangeDisplays(
        IReadOnlyList<MonitorSnapshot> monitors,
        MonitorSnapshot primary,
        DisplayLayoutMode layoutMode)
    {
        foreach (MonitorSnapshot monitor in monitors)
        {
            monitor.X = 0;
            monitor.Y = 0;
        }

        if (layoutMode == DisplayLayoutMode.Horizontal)
        {
            int x = 0;
            foreach (MonitorSnapshot monitor in monitors)
            {
                monitor.X = x;
                monitor.Y = (Height(primary) - Height(monitor)) / 2;
                x += Width(monitor);
            }
            NormalizeAroundPrimary(monitors, primary);
            return;
        }

        List<MonitorSnapshot> others = monitors.Where(monitor => !ReferenceEquals(monitor, primary)).ToList();
        if (others.Count >= 1)
        {
            MonitorSnapshot first = others[0];
            if (monitors.Count == 2)
            {
                first.X = Width(primary);
            }
            else
            {
                first.X = -Width(first);
            }
            first.Y = (Height(primary) - Height(first)) / 2;
        }
        if (others.Count >= 2)
        {
            MonitorSnapshot second = others[1];
            second.X = Width(primary);
            second.Y = (Height(primary) - Height(second)) / 2;
        }
        if (others.Count >= 3)
        {
            MonitorSnapshot auxiliary = others[2];
            auxiliary.X = (Width(primary) - Width(auxiliary)) / 2;
            auxiliary.Y = -Height(auxiliary);
        }

        int rightEdge = Width(primary);
        if (others.Count >= 2) rightEdge += Width(others[1]);
        foreach (MonitorSnapshot extra in others.Skip(3))
        {
            extra.X = rightEdge;
            extra.Y = (Height(primary) - Height(extra)) / 2;
            rightEdge += Width(extra);
        }
    }

    private static int Width(MonitorSnapshot monitor) => (int)Math.Min(monitor.Width, (uint)int.MaxValue);
    private static int Height(MonitorSnapshot monitor) => (int)Math.Min(monitor.Height, (uint)int.MaxValue);

    private static bool WaitForActiveSet(HashSet<string> expected, TimeSpan timeout)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed <= timeout)
        {
            try
            {
                QueryPaths(QdcOnlyActivePaths, out PathInfo[] paths, out _);
                HashSet<string> current = paths
                    .Select(GetTarget)
                    .Select(target => target.DevicePath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (expected.SetEquals(current)) return true;
            }
            catch
            {
            }

            Thread.Sleep(150);
        }

        return false;
    }

    private static List<MonitorSnapshot> ReadActiveMonitors(PathInfo[] paths, ModeInfo[] modes)
    {
        List<MonitorSnapshot> monitors = [];
        foreach (PathInfo path in paths)
        {
            TargetDescriptor target = GetTarget(path);
            if (string.IsNullOrWhiteSpace(target.DevicePath)) continue;
            if (!TryReadSourceMode(path, modes, out SourceMode source)) continue;

            monitors.Add(new MonitorSnapshot
            {
                DevicePath = target.DevicePath,
                FriendlyName = target.FriendlyName,
                Enabled = true,
                Primary = source.X == 0 && source.Y == 0,
                Width = source.Width,
                Height = source.Height,
                X = source.X,
                Y = source.Y,
                PixelFormat = source.PixelFormat,
                Rotation = path.TargetInfo.Rotation,
                Scaling = path.TargetInfo.Scaling,
                ScanLineOrdering = path.TargetInfo.ScanLineOrdering,
                RefreshNumerator = path.TargetInfo.RefreshRateNumerator,
                RefreshDenominator = Math.Max(1, path.TargetInfo.RefreshRateDenominator)
            });
        }

        return monitors;
    }

    private static bool LayoutMatchesCurrent(
        IReadOnlyList<ResolvedMonitor> resolved,
        IReadOnlyList<MonitorSnapshot> current,
        ResolvedMonitor primary,
        int originX,
        int originY)
    {
        if (resolved.Count != current.Count) return false;

        foreach (ResolvedMonitor item in resolved)
        {
            MonitorSnapshot desired = item.Snapshot;
            MonitorSnapshot? live = current.FirstOrDefault(monitor =>
                DevicePathEquals(monitor.DevicePath, desired.DevicePath));
            if (live is null) return false;

            bool shouldBePrimary = DevicePathEquals(desired.DevicePath, primary.Snapshot.DevicePath);
            if (live.Primary != shouldBePrimary ||
                live.Width != desired.Width ||
                live.Height != desired.Height ||
                live.X != desired.X - originX ||
                live.Y != desired.Y - originY ||
                live.PixelFormat != desired.PixelFormat ||
                live.Rotation != desired.Rotation ||
                live.Scaling != desired.Scaling ||
                !RefreshRatesEqual(live, desired))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RefreshRatesEqual(MonitorSnapshot left, MonitorSnapshot right)
    {
        if (right.RefreshNumerator == 0) return true;
        ulong leftDenominator = Math.Max(1u, left.RefreshDenominator);
        ulong rightDenominator = Math.Max(1u, right.RefreshDenominator);
        return (ulong)left.RefreshNumerator * rightDenominator ==
               (ulong)right.RefreshNumerator * leftDenominator;
    }

    private static int CandidateScore(PathCandidate candidate, HashSet<string> activePathKeys)
    {
        int score = activePathKeys.Contains(PathKey(candidate.Path)) ? 0 : 20;
        if (candidate.Path.TargetInfo.TargetAvailable == 0) score += 30;
        if (candidate.Path.SourceInfo.ModeInfoIndex == ModeIndexInvalid) score += 2;
        return score;
    }

    private static ModeInfo CreateSourceMode(PathSourceInfo source, uint width, uint height, uint pixelFormat, int x, int y)
    {
        ModeInfo mode = new()
        {
            InfoType = ModeInfoTypeSource,
            Id = source.Id,
            AdapterId = source.AdapterId,
            Union0 = ((ulong)height << 32) | width,
            Union1 = ((ulong)unchecked((uint)x) << 32) | pixelFormat,
            Union2 = unchecked((uint)y)
        };
        return mode;
    }

    private static bool TryReadSourceMode(PathInfo path, ModeInfo[] modes, out SourceMode source)
    {
        source = default;
        uint index = path.SourceInfo.ModeInfoIndex;
        if (index == ModeIndexInvalid || index >= modes.Length) return false;
        ModeInfo mode = modes[index];
        if (mode.InfoType != ModeInfoTypeSource) return false;

        source = new SourceMode(
            (uint)(mode.Union0 & 0xffffffff),
            (uint)(mode.Union0 >> 32),
            (uint)(mode.Union1 & 0xffffffff),
            unchecked((int)(uint)(mode.Union1 >> 32)),
            unchecked((int)(uint)(mode.Union2 & 0xffffffff)));
        return true;
    }

    private static bool DevicePathEquals(string left, string right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string SourceKey(PathInfo path) =>
        $"{path.SourceInfo.AdapterId.HighPart}:{path.SourceInfo.AdapterId.LowPart}:{path.SourceInfo.Id}";

    private static string PathKey(PathInfo path) =>
        $"{SourceKey(path)}>{path.TargetInfo.AdapterId.HighPart}:{path.TargetInfo.AdapterId.LowPart}:{path.TargetInfo.Id}";

    private static TargetDescriptor GetTarget(PathInfo path)
    {
        TargetDeviceName target = new()
        {
            Type = DeviceInfoGetTargetName,
            Size = (uint)Marshal.SizeOf<TargetDeviceName>(),
            AdapterId = path.TargetInfo.AdapterId,
            Id = path.TargetInfo.Id
        };

        int result = DisplayConfigGetDeviceInfo(ref target);
        if (result != 0)
        {
            return new TargetDescriptor(string.Empty, "Display");
        }

        string name = string.IsNullOrWhiteSpace(target.MonitorFriendlyDeviceName)
            ? "Display"
            : target.MonitorFriendlyDeviceName.Trim();
        return new TargetDescriptor(target.MonitorDevicePath?.Trim() ?? string.Empty, name);
    }

    private static void QueryPaths(uint flags, out PathInfo[] paths, out ModeInfo[] modes)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int sizeResult = GetDisplayConfigBufferSizes(flags, out uint pathCount, out uint modeCount);
            if (sizeResult != 0)
            {
                throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed with error {sizeResult}.");
            }

            PathInfo[] pathBuffer = new PathInfo[pathCount];
            ModeInfo[] modeBuffer = new ModeInfo[modeCount];
            int queryResult = QueryDisplayConfig(
                flags,
                ref pathCount,
                pathBuffer,
                ref modeCount,
                modeBuffer,
                IntPtr.Zero);

            if (queryResult == ErrorInsufficientBuffer) continue;
            if (queryResult != 0)
            {
                throw new InvalidOperationException($"QueryDisplayConfig failed with error {queryResult}.");
            }

            Array.Resize(ref pathBuffer, (int)pathCount);
            Array.Resize(ref modeBuffer, (int)modeCount);
            paths = pathBuffer;
            modes = modeBuffer;
            return;
        }

        throw new InvalidOperationException("Windows display paths changed repeatedly while they were being read.");
    }

    internal sealed class DisplayCheckpoint
    {
        private readonly PathInfo[] _paths;
        private readonly ModeInfo[] _modes;

        internal DisplayCheckpoint(PathInfo[] paths, ModeInfo[] modes)
        {
            _paths = paths;
            _modes = modes;
        }

        public bool Restore(OperationReport report)
        {
            uint flags = SdcApply | SdcUseSuppliedDisplayConfig | SdcAllowChanges | SdcSaveToDatabase;
            int result = SetDisplayConfig((uint)_paths.Length, _paths, (uint)_modes.Length, _modes, flags);
            if (result == 0)
            {
                report.Warn("Displays", "The previous display layout was restored.");
                return true;
            }

            report.Error("Displays", $"The previous display layout could not be restored automatically (error {result}).");
            return false;
        }
    }

    internal sealed record DisplayValidation(bool CanRestore, string? Note);

    private sealed record PathCandidate(PathInfo Path, TargetDescriptor Target);
    private sealed record ResolvedMonitor(MonitorSnapshot Snapshot, PathInfo Path);
    private sealed record TargetDescriptor(string DevicePath, string FriendlyName);
    private readonly record struct SourceMode(uint Width, uint Height, uint PixelFormat, int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public uint RefreshRateNumerator;
        public uint RefreshRateDenominator;
        public uint ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathInfo
    {
        public PathSourceInfo SourceInfo;
        public PathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ModeInfo
    {
        public uint InfoType;
        public uint Id;
        public Luid AdapterId;
        public ulong Union0;
        public ulong Union1;
        public ulong Union2;
        public ulong Union3;
        public ulong Union4;
        public ulong Union5;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Region
    {
        public uint Width;
        public uint Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VideoSignalInfo
    {
        public ulong PixelRate;
        public Rational HSyncFrequency;
        public Rational VSyncFrequency;
        public Region ActiveSize;
        public Region TotalSize;
        public uint AdditionalSignalInfo;
        public uint ScanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TargetMode
    {
        public VideoSignalInfo SignalInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TargetPreferredMode
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
        public uint Width;
        public uint Height;
        public TargetMode TargetMode;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetDeviceName
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string MonitorFriendlyDeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string MonitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(
        uint flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        [Out] PathInfo[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] ModeInfo[] modeInfoArray,
        IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(
        uint numPathArrayElements,
        PathInfo[] pathArray,
        uint numModeInfoArrayElements,
        ModeInfo[] modeInfoArray,
        uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref TargetPreferredMode requestPacket);
}

internal sealed class DisplayApplyException : Exception
{
    public DisplayApplyException(string message) : base(message)
    {
    }
}
