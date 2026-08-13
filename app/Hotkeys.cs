using System.Runtime.InteropServices;

namespace PitLaunch;

internal sealed class HotkeyService : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int EmergencyDisplayHotkeyId = 900;
    private const int ToggleProfileHotkeyId = 901;
    private const uint ModNoRepeat = 0x4000;
    private readonly Dictionary<int, Guid> _registrations = [];
    private int _nextId = 1000;

    public event Action<Guid>? Pressed;
    public event Action? EmergencyDisplayRestorePressed;
    public event Action? ToggleProfilePressed;
    public bool EmergencyDisplayHotkeyRegistered { get; private set; }
    public bool ToggleProfileHotkeyRegistered { get; private set; }

    public HotkeyService()
    {
        CreateHandle(new CreateParams
        {
            Caption = "PitLaunch hotkeys",
            Parent = new IntPtr(-3)
        });
    }

    public List<string> RegisterProfiles(IEnumerable<Profile> profiles, string? toggleHotkey = null)
    {
        Clear();
        List<string> warnings = [];
        uint emergencyModifiers = (uint)(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift) | ModNoRepeat;
        EmergencyDisplayHotkeyRegistered = RegisterHotKey(
            Handle,
            EmergencyDisplayHotkeyId,
            emergencyModifiers,
            (uint)Keys.F12);
        if (!EmergencyDisplayHotkeyRegistered)
        {
            warnings.Add($"Emergency display shortcut {AppInfo.EmergencyDisplayHotkey} is already in use.");
        }

        if (!string.IsNullOrWhiteSpace(toggleHotkey))
        {
            if (!HotkeyParser.TryParse(toggleHotkey, out HotkeyGesture toggle, out string error))
            {
                warnings.Add("Desk / Rig toggle: " + error);
            }
            else
            {
                ToggleProfileHotkeyRegistered = RegisterHotKey(
                    Handle,
                    ToggleProfileHotkeyId,
                    (uint)toggle.Modifiers | ModNoRepeat,
                    (uint)toggle.KeyCode);
                if (!ToggleProfileHotkeyRegistered)
                    warnings.Add($"Desk / Rig toggle: {toggleHotkey} is already in use.");
            }
        }

        foreach (Profile profile in profiles)
        {
            if (string.IsNullOrWhiteSpace(profile.Hotkey)) continue;
            if (!HotkeyParser.TryParse(profile.Hotkey, out HotkeyGesture gesture, out string error))
            {
                warnings.Add($"{profile.Name}: {error}");
                continue;
            }

            int id = _nextId++;
            if (!RegisterHotKey(Handle, id, (uint)gesture.Modifiers | ModNoRepeat, (uint)gesture.KeyCode))
            {
                warnings.Add($"{profile.Name}: {profile.Hotkey} is already in use.");
                continue;
            }

            _registrations[id] = profile.Id;
        }

        return warnings;
    }

    public void Clear()
    {
        if (EmergencyDisplayHotkeyRegistered)
        {
            UnregisterHotKey(Handle, EmergencyDisplayHotkeyId);
            EmergencyDisplayHotkeyRegistered = false;
        }
        if (ToggleProfileHotkeyRegistered)
        {
            UnregisterHotKey(Handle, ToggleProfileHotkeyId);
            ToggleProfileHotkeyRegistered = false;
        }
        foreach (int id in _registrations.Keys) UnregisterHotKey(Handle, id);
        _registrations.Clear();
        _nextId = 1000;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmHotkey && message.WParam.ToInt32() == EmergencyDisplayHotkeyId)
        {
            EmergencyDisplayRestorePressed?.Invoke();
        }
        else if (message.Msg == WmHotkey && message.WParam.ToInt32() == ToggleProfileHotkeyId)
        {
            ToggleProfilePressed?.Invoke();
        }
        else if (message.Msg == WmHotkey && _registrations.TryGetValue(message.WParam.ToInt32(), out Guid profileId))
        {
            Pressed?.Invoke(profileId);
        }

        base.WndProc(ref message);
    }

    public void Dispose()
    {
        Clear();
        DestroyHandle();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);
}

[Flags]
internal enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008
}

internal readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, Keys KeyCode);

internal static class HotkeyParser
{
    public static bool TryParse(string value, out HotkeyGesture gesture, out string error)
    {
        gesture = default;
        error = string.Empty;
        string[] parts = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "Hotkey is empty.";
            return false;
        }

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        Keys key = Keys.None;
        foreach (string part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control": modifiers |= HotkeyModifiers.Control; continue;
                case "alt": modifiers |= HotkeyModifiers.Alt; continue;
                case "shift": modifiers |= HotkeyModifiers.Shift; continue;
                case "win":
                case "windows": modifiers |= HotkeyModifiers.Win; continue;
            }

            if (key != Keys.None)
            {
                error = "Use one non-modifier key.";
                return false;
            }

            if (!Enum.TryParse(part, true, out key) || (key & Keys.KeyCode) == Keys.None)
            {
                error = $"{part} is not a recognized key.";
                return false;
            }
            key &= Keys.KeyCode;
        }

        if (key == Keys.None)
        {
            error = "Add a letter, number, or function key.";
            return false;
        }

        if (key is >= Keys.LButton and <= Keys.XButton2 ||
            key is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LShiftKey or Keys.RShiftKey
                or Keys.LControlKey or Keys.RControlKey or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin)
        {
            error = "Choose a keyboard key, not a mouse or modifier key.";
            return false;
        }

        bool isFunctionKey = key is >= Keys.F1 and <= Keys.F24;
        HotkeyModifiers primaryModifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Win;
        if (!isFunctionKey && (modifiers & primaryModifiers) == HotkeyModifiers.None)
        {
            error = "Hold Ctrl, Alt, or Win with that key, or use a function key on its own.";
            return false;
        }

        gesture = new HotkeyGesture(modifiers, key);
        return true;
    }
}

internal static class HotkeySender
{
    private const uint KeyUp = 0x0002;

    public static void Press(string value, string action, OperationReport report)
    {
        if (!HotkeyParser.TryParse(value, out HotkeyGesture gesture, out string error))
        {
            report.Warn("Discord", $"Could not send the {action} keybind: {error}");
            return;
        }

        List<Keys> modifiers = [];
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Control)) modifiers.Add(Keys.ControlKey);
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt)) modifiers.Add(Keys.Menu);
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift)) modifiers.Add(Keys.ShiftKey);
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Win)) modifiers.Add(Keys.LWin);
        try
        {
            foreach (Keys modifier in modifiers) KeyEvent((byte)modifier, 0, 0, UIntPtr.Zero);
            KeyEvent((byte)gesture.KeyCode, 0, 0, UIntPtr.Zero);
            Thread.Sleep(25);
            KeyEvent((byte)gesture.KeyCode, 0, KeyUp, UIntPtr.Zero);
            for (int index = modifiers.Count - 1; index >= 0; index--)
                KeyEvent((byte)modifiers[index], 0, KeyUp, UIntPtr.Zero);
            report.Info("Discord", $"Sent the {action} keybind.");
        }
        catch (Exception ex)
        {
            report.Warn("Discord", $"Could not send the {action} keybind: {ex.Message}");
        }
    }

    [DllImport("user32.dll", EntryPoint = "keybd_event")]
    private static extern void KeyEvent(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}
