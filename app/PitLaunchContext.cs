namespace PitLaunch;

internal sealed class PitLaunchContext : ApplicationContext
{
    private readonly SingleInstance _instance;
    private readonly ProfileCoordinator _coordinator;
    private readonly MainForm _form;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _trayMenu;
    private readonly HotkeyService _hotkeys;
    private readonly GameDetectionService _games;
    private bool _emergencyHotkeyWarningShown;
    private bool _exiting;
    private bool _disposed;

    public PitLaunchContext(LaunchRequest initialRequest, SingleInstance instance)
    {
        _instance = instance;
        ProfileRepository repository = new();
        _coordinator = new ProfileCoordinator(
            repository,
            new DisplayService(),
            new AudioService(),
            new WindowService(),
            new AppService());
        _form = new MainForm(_coordinator);
        _ = _form.Handle;

        _trayMenu = new ContextMenuStrip
        {
            BackColor = UiTheme.Sidebar,
            ForeColor = UiTheme.Text,
            Font = UiTheme.UiFont,
            ShowImageMargin = true
        };
        _trayMenu.Opening += (_, _) => BuildTrayMenu();

        Icon trayIcon;
        try { trayIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application; }
        catch { trayIcon = SystemIcons.Application; }
        _tray = new NotifyIcon
        {
            Icon = trayIcon,
            Text = AppInfo.ProductName,
            Visible = true,
            ContextMenuStrip = _trayMenu
        };
        _tray.DoubleClick += (_, _) => _form.ShowWindow();

        _hotkeys = new HotkeyService();
        _hotkeys.Pressed += profileId => DispatchToUi(() => _form.ActivateProfileAsync(profileId, ActivationSource.Hotkey));
        _hotkeys.EmergencyDisplayRestorePressed += () => DispatchToUi(() => RestoreAllDisplaysAsync(false));

        _games = new GameDetectionService(() => _coordinator.Document);
        _games.ActivationRequested += (profileId, source) =>
            DispatchToUi(() => _form.ActivateProfileAsync(profileId, source));

        _coordinator.ProfilesChanged += () => DispatchToUi(() =>
        {
            RefreshIntegrations();
            return Task.CompletedTask;
        });
        _coordinator.SwitchCompleted += completed => DispatchToUi(() =>
        {
            ShowSwitchNotification(completed);
            return Task.CompletedTask;
        });

        _instance.StartServer(request => DispatchToUi(() => HandleRequestAsync(request)));
        RefreshIntegrations();

        _form.BeginInvoke(async () => await HandleRequestAsync(initialRequest));
    }

    private void BuildTrayMenu()
    {
        _trayMenu.Items.Clear();
        ToolStripMenuItem show = MenuItem("Open " + AppInfo.ProductName, (_, _) => _form.ShowWindow(), bold: true);
        _trayMenu.Items.Add(show);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(MenuItem(
            $"Restore all displays ({AppInfo.EmergencyDisplayHotkey})",
            async (_, _) => await RestoreAllDisplaysAsync(false)));
        _trayMenu.Items.Add(new ToolStripSeparator());

        foreach (Profile profile in _coordinator.Document.Profiles)
        {
            Profile captured = profile;
            ToolStripMenuItem item = MenuItem(profile.Name, async (_, _) =>
                await _form.ActivateProfileAsync(captured.Id, ActivationSource.Tray));
            item.Checked = _coordinator.Document.Runtime.ActiveProfileId == profile.Id;
            _trayMenu.Items.Add(item);
        }

        if (_coordinator.Document.Profiles.Count == 0)
        {
            _trayMenu.Items.Add(new ToolStripMenuItem("No profiles captured") { Enabled = false, ForeColor = UiTheme.Muted });
        }

        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add(MenuItem("Create setup...", async (_, _) =>
        {
            _form.ShowWindow();
            await _form.PromptCaptureAsync();
        }));
        _trayMenu.Items.Add(MenuItem("Exit", (_, _) => ExitApplication()));
    }

    private static ToolStripMenuItem MenuItem(string text, EventHandler click, bool bold = false)
    {
        ToolStripMenuItem item = new(text)
        {
            BackColor = UiTheme.Sidebar,
            ForeColor = UiTheme.Text,
            Font = bold ? new Font(UiTheme.UiFont, FontStyle.Bold) : UiTheme.UiFont
        };
        item.Click += click;
        return item;
    }

    private async Task HandleRequestAsync(LaunchRequest request)
    {
        switch (request.Kind)
        {
            case LaunchRequestKind.Show:
                _form.ShowWindow();
                break;
            case LaunchRequestKind.Chooser:
                _form.ShowStartupChooser();
                break;
            case LaunchRequestKind.Background:
                break;
            case LaunchRequestKind.ActivateProfile:
            {
                Profile? profile = _coordinator.FindProfile(request.Value);
                if (profile is null)
                {
                    Notify("Profile not found", $"No profile named {request.Value} exists.", ToolTipIcon.Warning);
                    AppLog.Error("Command line profile not found: " + request.Value);
                    break;
                }
                await _form.ActivateProfileAsync(profile.Id, ActivationSource.CommandLine);
                break;
            }
            case LaunchRequestKind.CaptureProfile:
                if (string.IsNullOrWhiteSpace(request.Value))
                {
                    _form.ShowWindow();
                    await _form.PromptCaptureAsync();
                }
                else
                {
                    await _form.CaptureNamedAsync(request.Value);
                }
                break;
            case LaunchRequestKind.RestoreDisplays:
                await RestoreAllDisplaysAsync(false);
                break;
            case LaunchRequestKind.Exit:
                ExitApplication();
                break;
        }
    }

    private void RefreshIntegrations()
    {
        List<string> hotkeyWarnings = _hotkeys.RegisterProfiles(_coordinator.Document.Profiles);
        foreach (string warning in hotkeyWarnings) AppLog.Write(OperationSeverity.Warning, "Hotkeys: " + warning);
        if (!_hotkeys.EmergencyDisplayHotkeyRegistered && !_emergencyHotkeyWarningShown)
        {
            _emergencyHotkeyWarningShown = true;
            Notify("Emergency shortcut unavailable",
                $"{AppInfo.EmergencyDisplayHotkey} is already used by another app. Restore displays remains available from PitLaunch and its tray menu.",
                ToolTipIcon.Warning);
        }
        _games.Refresh();
        Profile? active = _coordinator.ActiveProfile;
        _tray.Text = active is null ? AppInfo.ProductName : TrimTrayText(AppInfo.ProductName + " - " + active.Name);
    }

    private async Task RestoreAllDisplaysAsync(bool showWindow)
    {
        if (showWindow) _form.ShowWindow();
        OperationReport report = await _form.RestoreAllDisplaysAsync();
        ToolTipIcon icon = report.HasErrors
            ? ToolTipIcon.Error
            : report.HasWarnings ? ToolTipIcon.Warning : ToolTipIcon.Info;
        OperationMessage? important = report.Messages.FirstOrDefault(message => message.Severity == OperationSeverity.Error)
                                      ?? report.Messages.FirstOrDefault(message => message.Severity == OperationSeverity.Warning);
        Notify(report.Summary, important?.Message ?? "Every connected monitor is enabled.", icon);
    }

    private void ShowSwitchNotification(ProfileSwitchCompleted completed)
    {
        ToolTipIcon icon = completed.Report.HasErrors
            ? ToolTipIcon.Error
            : completed.Report.HasWarnings ? ToolTipIcon.Warning : ToolTipIcon.Info;
        Notify(completed.Profile.Name, completed.Report.Summary, icon);
    }

    private void Notify(string title, string message, ToolTipIcon icon)
    {
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = message;
        _tray.BalloonTipIcon = icon;
        _tray.ShowBalloonTip(3500);
    }

    private Task DispatchToUi(Func<Task> action)
    {
        if (_disposed || _form.IsDisposed) return Task.CompletedTask;
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _form.BeginInvoke(async () =>
            {
                try
                {
                    await action();
                    completion.SetResult();
                }
                catch (Exception ex)
                {
                    AppLog.Error(ex.ToString());
                    completion.SetException(ex);
                }
            });
        }
        catch (Exception ex)
        {
            completion.SetException(ex);
        }
        return completion.Task;
    }

    private void ExitApplication()
    {
        if (_exiting) return;
        if (_coordinator.IsBusy)
        {
            Notify("Switch in progress", "Wait for the current setup switch to finish, then exit PitLaunch.", ToolTipIcon.Warning);
            return;
        }
        _exiting = true;
        AppLog.Info("PitLaunch exit requested.");
        _tray.Visible = false;
        _form.PermitExit();
        _form.Close();
        ExitThread();
    }

    private static string TrimTrayText(string text) => text.Length <= 63 ? text : text[..63];

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _tray.Visible = false;
            _games.Dispose();
            _hotkeys.Dispose();
            _tray.Dispose();
            _trayMenu.Dispose();
            _coordinator.Dispose();
            _form.Dispose();
        }
        base.Dispose(disposing);
    }
}
