using System.Runtime.InteropServices;
using System.Text;

namespace PitLaunch;

internal sealed record ControllerDevice(string Name, string Id);

/// <summary>
/// Lists connected game controllers (wheelbases, pedal sets, button boxes, handbrakes) by name.
///
/// Deliberately does not try to label a device as "the wheel" or "the pedals": whether pedals
/// are their own device depends entirely on the hardware. A Logitech G29 reports the wheel and
/// its pedals as a single device, while most direct-drive bases expose the pedals separately.
/// Guessing would be wrong on half the rigs out there, so a setup instead remembers the device
/// names it saw when it was created and reports anything missing next time.
/// </summary>
internal sealed class ControllerService
{
    private const int RimTypeHid = 2;
    private const uint RidiDeviceInfo = 0x2000000b;
    private const uint RidiDeviceName = 0x20000007;
    private const ushort UsagePageGenericDesktop = 0x01;
    private const ushort UsageJoystick = 0x04;
    private const ushort UsageGamepad = 0x05;
    private const ushort UsageMultiAxisController = 0x08;

    /// <summary>Counts from the last enumeration, so a self-test can tell "nothing plugged in" apart from "the query is broken".</summary>
    internal sealed record Diagnostics(int HidDevicesSeen, int InfoQueriesSucceeded, int ControllersFound);

    public List<ControllerDevice> ListConnected() => ListConnected(out _);

    public List<ControllerDevice> ListConnected(out Diagnostics diagnostics)
    {
        Dictionary<string, ControllerDevice> found = new(StringComparer.OrdinalIgnoreCase);
        int hidSeen = 0;
        int infoOk = 0;
        try
        {
            uint count = 0;
            uint size = (uint)Marshal.SizeOf<RawInputDeviceList>();
            if (GetRawInputDeviceList(null, ref count, size) != 0 || count == 0)
            {
                diagnostics = new Diagnostics(0, 0, 0);
                return [];
            }

            RawInputDeviceList[] devices = new RawInputDeviceList[count];
            if (GetRawInputDeviceList(devices, ref count, size) == unchecked((uint)-1))
            {
                diagnostics = new Diagnostics(0, 0, 0);
                return [];
            }

            foreach (RawInputDeviceList device in devices)
            {
                if (device.Type != RimTypeHid) continue;
                hidSeen++;
                if (!TryGetHidUsage(device.Device, out ushort usagePage, out ushort usage)) continue;
                infoOk++;
                if (usagePage != UsagePageGenericDesktop) continue;
                if (usage is not (UsageJoystick or UsageGamepad or UsageMultiAxisController)) continue;

                string path = GetDevicePath(device.Device);
                if (string.IsNullOrWhiteSpace(path)) continue;

                string name = GetProductName(path) ?? FriendlyFromPath(path);
                if (string.IsNullOrWhiteSpace(name)) continue;
                found[name] = new ControllerDevice(name, path);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not list game controllers: " + ex.Message);
        }

        diagnostics = new Diagnostics(hidSeen, infoOk, found.Count);
        return found.Values.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Names the setup expected that are not plugged in right now.</summary>
    public List<string> FindMissing(IEnumerable<string> expected)
    {
        HashSet<string> connected = ListConnected()
            .Select(device => device.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return expected
            .Where(name => !string.IsNullOrWhiteSpace(name) && !connected.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryGetHidUsage(IntPtr handle, out ushort usagePage, out ushort usage)
    {
        usagePage = 0;
        usage = 0;
        uint size = (uint)Marshal.SizeOf<RawDeviceInfo>();
        RawDeviceInfo info = new() { Size = size };
        if (GetRawInputDeviceInfo(handle, RidiDeviceInfo, ref info, ref size) == unchecked((uint)-1)) return false;
        if (info.Type != RimTypeHid) return false;
        usagePage = info.Hid.UsagePage;
        usage = info.Hid.Usage;
        return true;
    }

    private static string GetDevicePath(IntPtr handle)
    {
        uint size = 0;
        if (GetRawInputDeviceInfo(handle, RidiDeviceName, IntPtr.Zero, ref size) != 0 || size == 0) return string.Empty;
        IntPtr buffer = Marshal.AllocHGlobal((int)((size + 1) * 2));
        try
        {
            if (GetRawInputDeviceInfo(handle, RidiDeviceName, buffer, ref size) == unchecked((uint)-1)) return string.Empty;
            return Marshal.PtrToStringUni(buffer) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Reads the manufacturer's product string, which is what a person would recognise.</summary>
    private static string? GetProductName(string devicePath)
    {
        IntPtr handle = CreateFile(devicePath, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return null;
        try
        {
            StringBuilder builder = new(256);
            if (!HidD_GetProductString(handle, builder, (uint)(builder.Capacity * 2))) return null;
            string value = builder.ToString().Trim();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>Last resort when a device will not hand over a product string.</summary>
    private static string FriendlyFromPath(string path)
    {
        try
        {
            string[] parts = path.Split('#');
            return parts.Length >= 2 ? parts[1].ToUpperInvariant() : path;
        }
        catch
        {
            return path;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDeviceList
    {
        public IntPtr Device;
        public uint Type;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawHidInfo
    {
        public uint VendorId;
        public uint ProductId;
        public uint VersionNumber;
        public ushort UsagePage;
        public ushort Usage;
    }

    // Size must be the full RID_DEVICE_INFO: 8 bytes of header plus its union, whose largest
    // member is the 24-byte keyboard variant. Declaring only the 16-byte HID variant makes the
    // struct 24 bytes, and Windows then rejects cbSize and fails every query.
    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct RawDeviceInfo
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public int Type;
        [FieldOffset(8)] public RawHidInfo Hid;
    }

    [DllImport("user32.dll")]
    private static extern uint GetRawInputDeviceList(
        [In, Out] RawInputDeviceList[]? deviceList, ref uint numDevices, uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, ref RawDeviceInfo data, ref uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetProductString(IntPtr device, StringBuilder buffer, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string fileName, uint access, uint shareMode, IntPtr security,
        uint creationDisposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
