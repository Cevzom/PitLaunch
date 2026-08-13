using System.Runtime.InteropServices;
using System.Windows.Forms.Integration;

namespace PitLaunch;

internal sealed class MainForm : Form
{
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int HtCaption = 2;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int ResizeBorder = 7;

    private readonly PitLaunchView _view;
    private bool _allowExit;

    public MainForm(ProfileCoordinator coordinator)
    {
        Text = AppInfo.ProductName;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1160, 760);
        MinimumSize = new Size(940, 620);
        BackColor = Color.FromArgb(14, 16, 15);
        FormBorderStyle = FormBorderStyle.None;
        KeyPreview = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        _view = new PitLaunchView(coordinator, this);
        ElementHost host = new()
        {
            Dock = DockStyle.Fill,
            BackColor = BackColor,
            Child = _view
        };
        Controls.Add(host);

        FormClosing += HandleFormClosing;
        Resize += (_, _) => _view.UpdateWindowState(WindowState == FormWindowState.Maximized);
    }

    public Task PromptCaptureAsync() => _view.PromptCaptureAsync();

    public Task CaptureNamedAsync(string name) => _view.CaptureNamedAsync(name);

    public Task ActivateProfileAsync(Guid profileId, ActivationSource source) =>
        _view.ActivateProfileAsync(profileId, source);

    public Task<bool> ToggleDeskRigAsync(ActivationSource source) =>
        _view.ToggleDeskRigAsync(source);

    public Task<OperationReport> RestoreAllDisplaysAsync() => _view.RestoreAllDisplaysAsync();

    public void ApplyStartupUpdateStatus(UpdateStatus status) => _view.ApplyStartupUpdateStatus(status);

    public void ShowWindow()
    {
        _view.ExitStartupChooser();
        ShowWindowCore();
    }

    public void ShowStartupChooser()
    {
        _view.EnterStartupChooser();
        ShowWindowCore();
    }

    private void ShowWindowCore()
    {
        if (!Visible) Show();
        ShowInTaskbar = true;
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        _view.PlayWindowReveal();
    }

    internal void HideToTray()
    {
        _view.ExitStartupChooser();
        Hide();
        ShowInTaskbar = false;
    }

    internal void MinimizeWindow() => WindowState = FormWindowState.Minimized;

    internal void ToggleMaximize()
    {
        WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal
            : FormWindowState.Maximized;
    }

    internal void BeginWindowDrag()
    {
        if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
        ReleaseCapture();
        SendMessage(Handle, WmNcLeftButtonDown, HtCaption, 0);
    }

    public void PermitExit() => _allowExit = true;

    private void HandleFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowExit || !ShouldHideToTray(e.CloseReason)) return;
        e.Cancel = true;
        HideToTray();
    }

    internal static bool ShouldHideToTray(CloseReason reason) => reason == CloseReason.UserClosing;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;

        int rounded = 2;
        DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int));
        int borderColor = ColorTranslator.ToWin32(Color.FromArgb(52, 58, 53));
        DwmSetWindowAttribute(Handle, 34, ref borderColor, sizeof(int));
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmNcHitTest && WindowState == FormWindowState.Normal)
        {
            base.WndProc(ref message);
            if (message.Result.ToInt32() != 1) return;

            Point cursor = PointToClient(new Point(
                unchecked((short)(long)message.LParam),
                unchecked((short)((long)message.LParam >> 16))));
            bool left = cursor.X <= ResizeBorder;
            bool right = cursor.X >= ClientSize.Width - ResizeBorder;
            bool top = cursor.Y <= ResizeBorder;
            bool bottom = cursor.Y >= ClientSize.Height - ResizeBorder;

            if (left && top) message.Result = (IntPtr)HtTopLeft;
            else if (right && top) message.Result = (IntPtr)HtTopRight;
            else if (left && bottom) message.Result = (IntPtr)HtBottomLeft;
            else if (right && bottom) message.Result = (IntPtr)HtBottomRight;
            else if (left) message.Result = (IntPtr)HtLeft;
            else if (right) message.Result = (IntPtr)HtRight;
            else if (top) message.Result = (IntPtr)HtTop;
            else if (bottom) message.Result = (IntPtr)HtBottom;
            return;
        }

        base.WndProc(ref message);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, int wParam, int lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
