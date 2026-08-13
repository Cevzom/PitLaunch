using System.Runtime.InteropServices;

namespace PitLaunch;

internal sealed record AudioDeviceOption(string Id, string Name);

internal sealed class AudioService
{
    public List<AudioDeviceOption> ListActiveEndpoints(bool capture)
    {
        List<AudioDeviceOption> options = [];
        object? enumeratorObject = null;
        IMMDeviceCollection? collection = null;
        try
        {
            enumeratorObject = new MMDeviceEnumeratorComObject();
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)enumeratorObject;
            int result = enumerator.EnumAudioEndpoints(capture ? DataFlow.Capture : DataFlow.Render, 0x1, out collection);
            if (result < 0 || collection is null) return options;
            if (collection.GetCount(out uint count) < 0) return options;

            for (uint index = 0; index < count; index++)
            {
                IMMDevice? device = null;
                try
                {
                    if (collection.Item(index, out device) < 0 || device is null) continue;
                    if (device.GetId(out string deviceId) < 0 || string.IsNullOrWhiteSpace(deviceId)) continue;
                    options.Add(new AudioDeviceOption(deviceId, GetFriendlyName(device) ?? "Audio device"));
                }
                finally
                {
                    ReleaseComObject(device);
                }
            }
        }
        catch
        {
        }
        finally
        {
            ReleaseComObject(collection);
            ReleaseComObject(enumeratorObject);
        }

        return options.OrderBy(option => option.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public AudioSnapshot Capture(OperationReport? report = null)
    {
        AudioSnapshot snapshot = new()
        {
            Playback = GetDefaultEndpoint(DataFlow.Render, Role.Multimedia),
            Communications = GetDefaultEndpoint(DataFlow.Render, Role.Communications),
            Microphone = GetDefaultEndpoint(DataFlow.Capture, Role.Multimedia)
                ?? GetDefaultEndpoint(DataFlow.Capture, Role.Console)
        };

        int count = new[] { snapshot.Playback, snapshot.Communications, snapshot.Microphone }.Count(item => item is not null);
        report?.Info("Audio", $"Captured {count} default audio endpoint{(count == 1 ? "" : "s")}.");
        return snapshot;
    }

    public void Restore(AudioSnapshot snapshot, OperationReport report)
    {
        SetEndpoint(snapshot.Playback, "playback device", [Role.Console, Role.Multimedia], report);
        SetEndpoint(snapshot.Communications, "communications device", [Role.Communications], report);
        SetEndpoint(snapshot.Microphone, "microphone", [Role.Console, Role.Multimedia, Role.Communications], report);
    }

    public void SetCommunicationsMicrophone(string deviceId, OperationReport report)
    {
        string clean = (deviceId ?? string.Empty).Trim();
        if (clean.Length == 0) return;
        AudioDeviceOption? device = ListActiveEndpoints(true)
            .FirstOrDefault(option => string.Equals(option.Id, clean, StringComparison.OrdinalIgnoreCase));
        AudioEndpointSnapshot endpoint = new()
        {
            DeviceId = clean,
            FriendlyName = device?.Name ?? "Saved Discord microphone"
        };
        SetEndpoint(endpoint, "Discord communications microphone", [Role.Communications], report);
    }

    private static void SetEndpoint(
        AudioEndpointSnapshot? endpoint,
        string label,
        IReadOnlyList<Role> roles,
        OperationReport report)
    {
        if (endpoint is null || string.IsNullOrWhiteSpace(endpoint.DeviceId))
        {
            report.Warn("Audio", $"No {label} was captured. The current default was kept.");
            return;
        }

        if (!IsEndpointAvailable(endpoint.DeviceId))
        {
            report.Warn("Audio", $"{endpoint.FriendlyName} is unavailable and was skipped.");
            return;
        }

        object? policyObject = null;
        try
        {
            policyObject = new PolicyConfigClientComObject();
            IPolicyConfig policy = (IPolicyConfig)policyObject;
            foreach (Role role in roles)
            {
                int result = policy.SetDefaultEndpoint(endpoint.DeviceId, role);
                if (result < 0) Marshal.ThrowExceptionForHR(result);
            }

            report.Info("Audio", $"Set {label} to {endpoint.FriendlyName}.");
        }
        catch (Exception ex)
        {
            report.Warn("Audio", $"Could not set {endpoint.FriendlyName}: {ex.Message}");
        }
        finally
        {
            ReleaseComObject(policyObject);
        }
    }

    private static AudioEndpointSnapshot? GetDefaultEndpoint(DataFlow flow, Role role)
    {
        object? enumeratorObject = null;
        IMMDevice? device = null;
        try
        {
            enumeratorObject = new MMDeviceEnumeratorComObject();
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)enumeratorObject;
            int result = enumerator.GetDefaultAudioEndpoint(flow, role, out device);
            if (result < 0 || device is null) return null;

            result = device.GetId(out string deviceId);
            if (result < 0 || string.IsNullOrWhiteSpace(deviceId)) return null;

            return new AudioEndpointSnapshot
            {
                DeviceId = deviceId,
                FriendlyName = GetFriendlyName(device) ?? "Audio device"
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseComObject(device);
            ReleaseComObject(enumeratorObject);
        }
    }

    private static bool IsEndpointAvailable(string deviceId)
    {
        object? enumeratorObject = null;
        IMMDevice? device = null;
        try
        {
            enumeratorObject = new MMDeviceEnumeratorComObject();
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)enumeratorObject;
            int result = enumerator.GetDevice(deviceId, out device);
            if (result < 0 || device is null) return false;
            result = device.GetState(out uint state);
            return result >= 0 && (state & 0x1) != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            ReleaseComObject(device);
            ReleaseComObject(enumeratorObject);
        }
    }

    private static string? GetFriendlyName(IMMDevice device)
    {
        IPropertyStore? store = null;
        PropVariant value = default;
        try
        {
            int result = device.OpenPropertyStore(0, out store);
            if (result < 0 || store is null) return null;

            PropertyKey key = new()
            {
                FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
                PropertyId = 14
            };
            result = store.GetValue(ref key, out value);
            if (result < 0 || value.ValueType != 31 || value.PointerValue == IntPtr.Zero) return null;
            return Marshal.PtrToStringUni(value.PointerValue);
        }
        finally
        {
            PropVariantClear(ref value);
            ReleaseComObject(store);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.FinalReleaseComObject(value); } catch { }
    }

    private enum DataFlow
    {
        Render,
        Capture,
        All
    }

    private enum Role
    {
        Console,
        Multimedia,
        Communications
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort ValueType;
        [FieldOffset(8)] public IntPtr PointerValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceShareMode
    {
        public uint Mode;
        public uint ExclusiveModeSettings;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private sealed class PolicyConfigClientComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(DataFlow dataFlow, uint stateMask, out IMMDeviceCollection? devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IMMDevice? endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice? device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid interfaceId, uint classContext, IntPtr activationParameters, out IntPtr interfacePointer);

        [PreserveSig]
        int OpenPropertyStore(uint accessMode, out IPropertyStore? properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint propertyCount);
        [PreserveSig] int GetAt(uint propertyIndex, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultPeriod, out long defaultPeriodValue, out long minimumPeriodValue);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out DeviceShareMode mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref DeviceShareMode mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, Role role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);
}
