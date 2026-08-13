using System.Runtime.InteropServices;

namespace PitLaunch;

/// <summary>Reads and changes Windows Advanced Color (HDR) state on active capable displays.</summary>
internal sealed class HdrService
{
    private const uint QdcOnlyActivePaths = 2;
    private const uint GetAdvancedColorInfo = 9;
    private const uint SetAdvancedColorState = 10;
    private const int ErrorInsufficientBuffer = 122;

    public HdrStatus GetStatus()
    {
        List<TargetKey> targets = ActiveTargets();
        List<bool> enabled = [];
        foreach (TargetKey target in targets)
        {
            if (TryGet(target, out bool supported, out bool isEnabled) && supported)
                enabled.Add(isEnabled);
        }

        bool? state = enabled.Count == 0
            ? null
            : enabled.All(value => value) ? true
            : enabled.All(value => !value) ? false
            : null;
        return new HdrStatus(enabled.Count > 0, state, enabled.Count, targets.Count);
    }

    public void SetEnabled(bool? enabled, OperationReport report)
    {
        if (!enabled.HasValue) return;
        List<TargetKey> targets = ActiveTargets();
        int supported = 0;
        int changed = 0;
        int failed = 0;
        foreach (TargetKey target in targets)
        {
            if (!TryGet(target, out bool isSupported, out bool current) || !isSupported) continue;
            supported++;
            if (current == enabled.Value) continue;

            SetAdvancedColor packet = new()
            {
                Header = new DeviceInfoHeader
                {
                    Type = SetAdvancedColorState,
                    Size = (uint)Marshal.SizeOf<SetAdvancedColor>(),
                    AdapterId = target.AdapterId,
                    Id = target.TargetId
                },
                EnableAdvancedColor = enabled.Value ? 1u : 0u
            };
            int result = DisplayConfigSetDeviceInfo(ref packet);
            if (result == 0) changed++;
            else failed++;
        }

        if (supported == 0)
        {
            report.Warn("HDR", "None of the active displays reported HDR support; HDR was left unchanged.");
            return;
        }
        if (changed > 0)
            report.Info("HDR", $"Turned HDR {(enabled.Value ? "on" : "off")} on {changed} display{(changed == 1 ? "" : "s")}.");
        else if (failed == 0)
            report.Info("HDR", $"HDR is already {(enabled.Value ? "on" : "off")} on every capable active display.");
        if (failed > 0)
            report.Warn("HDR", $"Windows refused the HDR change on {failed} display{(failed == 1 ? "" : "s")}.");
    }

    private static bool TryGet(TargetKey target, out bool supported, out bool enabled)
    {
        GetAdvancedColor packet = new()
        {
            Header = new DeviceInfoHeader
            {
                Type = GetAdvancedColorInfo,
                Size = (uint)Marshal.SizeOf<GetAdvancedColor>(),
                AdapterId = target.AdapterId,
                Id = target.TargetId
            }
        };
        int result = DisplayConfigGetDeviceInfo(ref packet);
        supported = result == 0 && (packet.Value & 0x1) != 0;
        enabled = result == 0 && (packet.Value & 0x2) != 0;
        return result == 0;
    }

    private static List<TargetKey> ActiveTargets()
    {
        QueryPaths(out PathInfo[] paths);
        return paths
            .Select(path => new TargetKey(path.TargetInfo.AdapterId, path.TargetInfo.Id))
            .Distinct()
            .ToList();
    }

    private static void QueryPaths(out PathInfo[] paths)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int sizeResult = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount);
            if (sizeResult != 0) throw new InvalidOperationException($"Windows could not read HDR-capable displays (error {sizeResult}).");
            PathInfo[] pathBuffer = new PathInfo[pathCount];
            ModeInfo[] modeBuffer = new ModeInfo[modeCount];
            int query = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, pathBuffer, ref modeCount, modeBuffer, IntPtr.Zero);
            if (query == ErrorInsufficientBuffer) continue;
            if (query != 0) throw new InvalidOperationException($"Windows could not read HDR state (error {query}).");
            Array.Resize(ref pathBuffer, (int)pathCount);
            paths = pathBuffer;
            return;
        }
        throw new InvalidOperationException("Display paths changed repeatedly while HDR state was read.");
    }

    private readonly record struct TargetKey(Luid AdapterId, uint TargetId);

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GetAdvancedColor
    {
        public DeviceInfoHeader Header;
        public uint Value;
        public uint ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SetAdvancedColor
    {
        public DeviceInfoHeader Header;
        public uint EnableAdvancedColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid : IEquatable<Luid>
    {
        public uint LowPart;
        public int HighPart;
        public readonly bool Equals(Luid other) => LowPart == other.LowPart && HighPart == other.HighPart;
        public override readonly bool Equals(object? value) => value is Luid other && Equals(other);
        public override readonly int GetHashCode() => HashCode.Combine(LowPart, HighPart);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIndex;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
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
    private struct PathInfo
    {
        public PathSourceInfo SourceInfo;
        public PathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ModeInfo
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

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths,
        ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topologyId);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")]
    private static extern int DisplayConfigGetDeviceInfo(ref GetAdvancedColor request);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")]
    private static extern int DisplayConfigSetDeviceInfo(ref SetAdvancedColor request);
}
