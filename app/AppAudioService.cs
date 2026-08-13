using System.Runtime.InteropServices;
using System.Diagnostics;

namespace PitLaunch;

/// <summary>
/// Per-application audio controls. Volume uses the documented Core Audio session interfaces.
/// Routing uses the persisted audio-policy interface behind Windows' own Volume mixer. That
/// interface is internal and has changed once, so both known Windows variants are isolated here
/// and every failure falls back cleanly without changing the system-wide default device.
/// </summary>
internal sealed class AppAudioService
{
    private const uint ClsCtxAll = 23;
    private const int RpcChangedMode = unchecked((int)0x80010106);
    private const int AudioPolicySetEndpointSlot = 25;
    private const string AudioPolicyClass = "Windows.Media.Internal.AudioPolicyConfig";
    private const string MmDevicePrefix = @"\\?\SWD#MMDEVAPI#";
    private const string RenderInterfaceSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private static readonly Guid CurrentPolicyInterfaceId = new("AB3D4648-E242-459F-B02F-541C70306324");
    private static readonly Guid DownlevelPolicyInterfaceId = new("2A59116D-6C4F-45E0-A74F-707E3FEF9258");

    public void Apply(AppRule rule, IReadOnlyCollection<int> processIds, OperationReport report)
    {
        bool wantsRoute = !string.IsNullOrWhiteSpace(rule.AudioDeviceId);
        bool wantsVolume = rule.VolumePercent.HasValue;
        if (!wantsRoute && !wantsVolume) return;

        if (processIds.Count == 0)
        {
            report.Warn("App audio", $"{rule.DisplayName} is not running, so its audio settings could not be changed.");
            return;
        }

        if (wantsRoute)
        {
            AudioRouteResult route = SetPersistedRoute(processIds, rule.AudioDeviceId);
            if (route.Succeeded)
            {
                report.Info("App audio",
                    $"Routed {rule.DisplayName} to its saved output device for {route.ProcessCount} process" +
                    (route.ProcessCount == 1 ? "." : "es."));
            }
            else
            {
                report.Warn("App audio", $"Could not route {rule.DisplayName}: {route.Message}");
            }
        }

        if (!wantsVolume) return;

        int volume = Math.Clamp(rule.VolumePercent!.Value, 0, 100);
        Stopwatch timer = Stopwatch.StartNew();
        string? lastError = null;
        do
        {
            try
            {
                int changed = SetSessionVolume(rule.AudioDeviceId, processIds, volume / 100f);
                if (changed > 0)
                {
                    report.Info("App audio", $"Set {rule.DisplayName} to {volume}% volume.");
                    return;
                }
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                break;
            }
            Thread.Sleep(100);
        } while (timer.Elapsed < TimeSpan.FromSeconds(2));

        report.Warn("App audio", lastError is null
            ? $"{rule.DisplayName} has not opened an audio session yet; its volume was left unchanged."
            : $"Could not set {rule.DisplayName}'s volume: {lastError}");
    }

    internal static bool TryBuildRenderInterfaceId(string endpointId, out string interfaceId)
    {
        interfaceId = string.Empty;
        string clean = (endpointId ?? string.Empty).Trim();
        if (clean.Length == 0 || clean.Length > 512 || clean.Contains('\0')) return false;
        if (clean.StartsWith(MmDevicePrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!clean.EndsWith(RenderInterfaceSuffix, StringComparison.OrdinalIgnoreCase)) return false;
            interfaceId = clean;
            return true;
        }

        // IMMDevice endpoint ids returned by Windows have this shape. Requiring it prevents a
        // malformed profile from passing an arbitrary device-interface path into the policy API.
        if (!clean.StartsWith("{0.0.", StringComparison.OrdinalIgnoreCase) ||
            !clean.EndsWith('}') || clean.Contains('#') || clean.Contains('\\'))
        {
            return false;
        }

        interfaceId = MmDevicePrefix + clean + RenderInterfaceSuffix;
        return true;
    }

    internal static bool IsRoutingInterfaceAvailable(out string reason)
    {
        reason = string.Empty;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134))
        {
            reason = "Per-application routing requires Windows 10 version 1803 or newer.";
            return false;
        }

        int initializeResult = RoInitialize(1);
        bool uninitialize = initializeResult >= 0;
        if (initializeResult < 0 && initializeResult != RpcChangedMode)
        {
            reason = $"Windows audio policy could not initialize (0x{initializeResult:X8}).";
            return false;
        }

        try
        {
            int lastResult = 0;
            foreach (bool currentVariant in PreferredPolicyVariants())
            {
                IntPtr factory = IntPtr.Zero;
                try
                {
                    lastResult = ActivatePolicyFactory(currentVariant, out factory);
                    if (lastResult >= 0 && factory != IntPtr.Zero)
                    {
                        return true;
                    }
                }
                finally { if (factory != IntPtr.Zero) Marshal.Release(factory); }
            }

            reason = $"No compatible Windows audio-policy interface was found (0x{lastResult:X8}).";
            return false;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
        finally
        {
            if (uninitialize) RoUninitialize();
        }
    }

    private static AudioRouteResult SetPersistedRoute(IReadOnlyCollection<int> processIds, string endpointId)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134))
            return new AudioRouteResult(false, 0, "per-application routing requires Windows 10 version 1803 or newer.");
        if (!TryBuildRenderInterfaceId(endpointId, out string interfaceId))
            return new AudioRouteResult(false, 0, "the saved output-device identifier is invalid.");

        IntPtr hstring = IntPtr.Zero;
        int initializeResult = RoInitialize(1); // RO_INIT_MULTITHREADED
        bool uninitialize = initializeResult >= 0;
        if (initializeResult < 0 && initializeResult != RpcChangedMode)
            return new AudioRouteResult(false, 0, $"Windows audio policy could not initialize (0x{initializeResult:X8}).");

        try
        {
            int stringResult = WindowsCreateString(interfaceId, (uint)interfaceId.Length, out hstring);
            if (stringResult < 0)
                return new AudioRouteResult(false, 0, $"Windows rejected the output-device identifier (0x{stringResult:X8}).");

            Exception? firstError = null;
            foreach (bool currentVariant in PreferredPolicyVariants())
            {
                IntPtr factoryPointer = IntPtr.Zero;
                try
                {
                    int factoryResult = ActivatePolicyFactory(currentVariant, out factoryPointer);
                    if (factoryResult < 0 || factoryPointer == IntPtr.Zero)
                        Marshal.ThrowExceptionForHR(factoryResult);

                    int changed = ApplyRoute(factoryPointer, processIds, hstring);
                    return new AudioRouteResult(true, changed, string.Empty);
                }
                catch (Exception ex)
                {
                    firstError ??= ex;
                }
                finally { if (factoryPointer != IntPtr.Zero) Marshal.Release(factoryPointer); }
            }

            string message = firstError is COMException com
                ? $"this Windows build does not expose a compatible routing interface (0x{com.HResult:X8})."
                : firstError?.Message ?? "Windows did not expose a compatible routing interface.";
            return new AudioRouteResult(false, 0, message);
        }
        finally
        {
            if (hstring != IntPtr.Zero) WindowsDeleteString(hstring);
            if (uninitialize) RoUninitialize();
        }
    }

    private static IEnumerable<bool> PreferredPolicyVariants()
    {
        bool currentFirst = Environment.OSVersion.Version.Build >= 21390;
        yield return currentFirst;
        yield return !currentFirst;
    }

    private static int ActivatePolicyFactory(bool currentVariant, out IntPtr factoryPointer)
    {
        factoryPointer = IntPtr.Zero;
        IntPtr classId = IntPtr.Zero;
        try
        {
            int stringResult = WindowsCreateString(AudioPolicyClass, (uint)AudioPolicyClass.Length, out classId);
            if (stringResult < 0) return stringResult;

            Guid interfaceId = currentVariant
                ? CurrentPolicyInterfaceId
                : DownlevelPolicyInterfaceId;
            return RoGetActivationFactory(classId, ref interfaceId, out factoryPointer);
        }
        finally
        {
            if (classId != IntPtr.Zero) WindowsDeleteString(classId);
        }
    }

    private static int ApplyRoute(IntPtr factory, IReadOnlyCollection<int> processIds, IntPtr endpoint)
    {
        IntPtr vtable = Marshal.ReadIntPtr(factory);
        if (vtable == IntPtr.Zero) throw new InvalidOperationException("Windows returned an invalid audio-policy interface.");
        IntPtr method = Marshal.ReadIntPtr(vtable, AudioPolicySetEndpointSlot * IntPtr.Size);
        if (method == IntPtr.Zero) throw new InvalidOperationException("Windows omitted the per-app routing method.");
        SetPersistedDefaultAudioEndpointDelegate setEndpoint =
            Marshal.GetDelegateForFunctionPointer<SetPersistedDefaultAudioEndpointDelegate>(method);

        int changed = 0;
        foreach (int processId in processIds.Where(processId => processId > 0).Distinct())
        {
            ThrowForPolicyFailure(setEndpoint(
                factory, (uint)processId, DataFlow.Render, Role.Multimedia, endpoint));
            ThrowForPolicyFailure(setEndpoint(
                factory, (uint)processId, DataFlow.Render, Role.Console, endpoint));
            ThrowForPolicyFailure(setEndpoint(
                factory, (uint)processId, DataFlow.Render, Role.Communications, endpoint));
            changed++;
        }
        return changed;
    }

    private static void ThrowForPolicyFailure(int result)
    {
        if (result < 0) Marshal.ThrowExceptionForHR(result);
    }

    private static int SetSessionVolume(string deviceId, IReadOnlyCollection<int> processIds, float volume)
    {
        object? enumeratorObject = null;
        IMMDeviceCollection? endpoints = null;
        try
        {
            enumeratorObject = Activator.CreateInstance(typeof(MMDeviceEnumeratorComObject));
            IMMDeviceEnumerator enumerator = (IMMDeviceEnumerator)enumeratorObject!;
            int changed = 0;
            HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);

            // Look at the preferred endpoint first. The supported session-volume API works on
            // whichever endpoint currently owns the app; routing itself remains unchanged.
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                IMMDevice? preferred = null;
                try
                {
                    if (enumerator.GetDevice(deviceId, out preferred) >= 0 && preferred is not null)
                    {
                        visited.Add(deviceId);
                        changed += SetEndpointSessionVolume(preferred, processIds, volume);
                    }
                }
                finally { ReleaseComObject(preferred); }
            }

            int result = enumerator.EnumAudioEndpoints(DataFlow.Render, 0x1, out endpoints);
            if (result < 0 || endpoints is null) Marshal.ThrowExceptionForHR(result);
            IMMDeviceCollection activeEndpoints = endpoints
                ?? throw new InvalidOperationException("Windows did not expose active audio outputs.");
            if (activeEndpoints.GetCount(out uint count) < 0) return changed;
            for (uint index = 0; index < count; index++)
            {
                IMMDevice? endpoint = null;
                try
                {
                    if (activeEndpoints.Item(index, out endpoint) < 0 || endpoint is null) continue;
                    string id = string.Empty;
                    try { endpoint.GetId(out id); } catch { }
                    if (!string.IsNullOrWhiteSpace(id) && !visited.Add(id)) continue;
                    changed += SetEndpointSessionVolume(endpoint, processIds, volume);
                }
                finally { ReleaseComObject(endpoint); }
            }
            return changed;
        }
        finally
        {
            ReleaseComObject(endpoints);
            ReleaseComObject(enumeratorObject);
        }
    }

    private static int SetEndpointSessionVolume(IMMDevice endpoint, IReadOnlyCollection<int> processIds, float volume)
    {
        object? managerObject = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            Guid managerId = typeof(IAudioSessionManager2).GUID;
            int result = endpoint.Activate(ref managerId, ClsCtxAll, IntPtr.Zero, out managerObject);
            if (result < 0 || managerObject is null) return 0;
            if (managerObject is not IAudioSessionManager2 manager) return 0;
            if (manager.GetSessionEnumerator(out sessions) < 0 || sessions is null) return 0;
            if (sessions.GetCount(out int count) < 0) return 0;

            int changed = 0;
            for (int index = 0; index < count; index++)
            {
                IAudioSessionControl? control = null;
                try
                {
                    if (sessions.GetSession(index, out control) < 0 || control is null) continue;
                    if (control is not IAudioSessionControl2 control2 ||
                        control2.GetProcessId(out uint processId) < 0 ||
                        !processIds.Contains((int)processId) ||
                        control is not ISimpleAudioVolume simpleVolume)
                    {
                        continue;
                    }

                    Guid eventContext = Guid.Empty;
                    if (simpleVolume.SetMasterVolume(volume, ref eventContext) >= 0) changed++;
                }
                finally { ReleaseComObject(control); }
            }
            return changed;
        }
        finally
        {
            ReleaseComObject(sessions);
            ReleaseComObject(managerObject);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.FinalReleaseComObject(value); } catch { }
    }

    private enum DataFlow { Render, Capture, All }
    private enum Role { Console, Multimedia, Communications }

    private sealed record AudioRouteResult(bool Succeeded, int ProcessCount, string Message);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SetPersistedDefaultAudioEndpointDelegate(
        IntPtr factory,
        uint processId,
        DataFlow flow,
        Role role,
        IntPtr deviceId);

    [ComImport]
    [Guid("AB3D4648-E242-459F-B02F-541C70306324")]
    [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    private interface IAudioPolicyConfigFactoryCurrent
    {
        int IncompleteAddContextVolumeChange();
        int IncompleteRemoveContextVolumeChanged();
        int IncompleteAddRingerVibrateStateChanged();
        int IncompleteRemoveRingerVibrateStateChange();
        int IncompleteSetVolumeGroupGainForId();
        int IncompleteGetVolumeGroupGainForId();
        int IncompleteGetActiveVolumeGroupForEndpointId();
        int IncompleteGetVolumeGroupsForEndpoint();
        int IncompleteGetCurrentVolumeContext();
        int IncompleteSetVolumeGroupMuteForId();
        int IncompleteGetVolumeGroupMuteForId();
        int IncompleteSetRingerVibrateState();
        int IncompleteGetRingerVibrateState();
        int IncompleteSetPreferredChatApplication();
        int IncompleteResetPreferredChatApplication();
        int IncompleteGetPreferredChatApplication();
        int IncompleteGetCurrentChatApplications();
        int IncompleteAddChatContextChanged();
        int IncompleteRemoveChatContextChanged();
        [PreserveSig]
        int SetPersistedDefaultAudioEndpoint(uint processId, DataFlow flow, Role role, IntPtr deviceId);
        [PreserveSig]
        int GetPersistedDefaultAudioEndpoint(
            uint processId,
            DataFlow flow,
            Role role,
            [MarshalAs(UnmanagedType.HString)] out string deviceId);
        [PreserveSig]
        int ClearAllPersistedApplicationDefaultEndpoints();
    }

    [ComImport]
    [Guid("2A59116D-6C4F-45E0-A74F-707E3FEF9258")]
    [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
    private interface IAudioPolicyConfigFactoryDownlevel
    {
        int IncompleteAddContextVolumeChange();
        int IncompleteRemoveContextVolumeChanged();
        int IncompleteAddRingerVibrateStateChanged();
        int IncompleteRemoveRingerVibrateStateChange();
        int IncompleteSetVolumeGroupGainForId();
        int IncompleteGetVolumeGroupGainForId();
        int IncompleteGetActiveVolumeGroupForEndpointId();
        int IncompleteGetVolumeGroupsForEndpoint();
        int IncompleteGetCurrentVolumeContext();
        int IncompleteSetVolumeGroupMuteForId();
        int IncompleteGetVolumeGroupMuteForId();
        int IncompleteSetRingerVibrateState();
        int IncompleteGetRingerVibrateState();
        int IncompleteSetPreferredChatApplication();
        int IncompleteResetPreferredChatApplication();
        int IncompleteGetPreferredChatApplication();
        int IncompleteGetCurrentChatApplications();
        int IncompleteAddChatContextChanged();
        int IncompleteRemoveChatContextChanged();
        [PreserveSig]
        int SetPersistedDefaultAudioEndpoint(uint processId, DataFlow flow, Role role, IntPtr deviceId);
        [PreserveSig]
        int GetPersistedDefaultAudioEndpoint(
            uint processId,
            DataFlow flow,
            Role role,
            [MarshalAs(UnmanagedType.HString)] out string deviceId);
        [PreserveSig]
        int ClearAllPersistedApplicationDefaultEndpoints();
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumeratorComObject { }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(DataFlow flow, uint stateMask, out IMMDeviceCollection? devices);
        [PreserveSig] int GetDefaultAudioEndpoint(DataFlow flow, Role role, out IMMDevice? endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice? device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid interfaceId,
            uint classContext,
            IntPtr activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object? interfaceObject);
        [PreserveSig] int OpenPropertyStore(uint accessMode, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid sessionGuid, uint streamFlags, out IntPtr sessionControl);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid sessionGuid, uint streamFlags, out IntPtr audioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator? sessionEnumerator);
        [PreserveSig] int RegisterSessionNotification(IntPtr notification);
        [PreserveSig] int UnregisterSessionNotification(IntPtr notification);
        [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
        [PreserveSig] int UnregisterDuckNotification(IntPtr notification);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int sessionCount);
        [PreserveSig] int GetSession(int sessionIndex, out IAudioSessionControl? sessionControl);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid groupingId);
        [PreserveSig] int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2 : IAudioSessionControl
    {
        [PreserveSig] new int GetState(out int state);
        [PreserveSig] new int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
        [PreserveSig] new int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);
        [PreserveSig] new int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
        [PreserveSig] new int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);
        [PreserveSig] new int GetGroupingParam(out Guid groupingId);
        [PreserveSig] new int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);
        [PreserveSig] new int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] new int UnregisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionIdentifier);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string sessionInstanceIdentifier);
        [PreserveSig] int GetProcessId(out uint processId);
        [PreserveSig] int IsSystemSoundsSession();
        [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolume(out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [DllImport("combase.dll")]
    private static extern int RoInitialize(uint initializationType);

    [DllImport("combase.dll")]
    private static extern void RoUninitialize();

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(
        IntPtr activatableClassId,
        ref Guid interfaceId,
        out IntPtr factory);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(
        [MarshalAs(UnmanagedType.LPWStr)] string source,
        uint length,
        out IntPtr value);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr value);
}
