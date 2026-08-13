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
    private readonly IntegrationServer _integration;
    private readonly UpdateService _updates = new();
    private readonly TaskCompletionSource<bool> _updatePolicyReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _automationAllowed;
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
        bool policyConfigured = !string.IsNullOrWhiteSpace(AppInfo.ResolvedUpdatePolicyUrl);
        _automationAllowed = !policyConfigured;
        if (policyConfigured)
        {
            _coordinator.SetSwitchBlockReason(
                "PitLaunch is checking whether this version is still supported. Try again in a moment.");
        }
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
        _hotkeys.ToggleProfilePressed += () => DispatchToUi(ActivateToggleFromHotkeyAsync);

        _games = new GameDetectionService(() => _coordinator.Document);
        _games.ActivationRequested += request =>
            DispatchToUi(() => _coordinator.ActivateAsync(
                request.ProfileId,
                request.Source,
                request.ProcessName));
        _games.WarningRaised += message => DispatchToUi(() =>
        {
            Notify("Game automation needs attention", message, ToolTipIcon.Warning);
            return Task.CompletedTask;
        });

        _integration = new IntegrationServer(
            () => DispatchToUi(() => Task.FromResult(BuildIntegrationState())),
            profileId => DispatchToUi(() => ActivateForIntegrationAsync(profileId)),
            () => DispatchToUi(ActivateToggleForIntegrationAsync),
            () => DispatchToUi(async () =>
            {
                OperationReport report = await RestoreAllDisplaysAsync(false);
                return new IntegrationRestoreResult(!report.HasErrors, report.Summary);
            }));

        _coordinator.ProfilesChanged += () => DispatchToUi(() =>
        {
            RefreshIntegrations();
            _integration.NotifyProfilesChanged();
            _integration.NotifyStatusChanged();
            return Task.CompletedTask;
        });
        _coordinator.BusyChanged += (_, _) => _integration.NotifyStatusChanged();
        _coordinator.SwitchCompleted += completed => DispatchToUi(() =>
        {
            ShowSwitchNotification(completed);
            _integration.NotifyStatusChanged();
            return Task.CompletedTask;
        });

        _integration.Start();
        _instance.StartServer(request => DispatchToUi(() => HandleRequestAsync(request)));
        RefreshIntegrations();
        _ = CheckUpdatesAtStartupAsync();

        _form.BeginInvoke(async () => await HandleRequestAsync(initialRequest));
    }

    private async Task CheckUpdatesAtStartupAsync()
    {
        try
        {
            UpdatePolicyResult policy = await _updates.CheckPolicyAsync().ConfigureAwait(false);
            _automationAllowed = !policy.IsRequired;
            _coordinator.SetSwitchBlockReason(policy.IsRequired
                ? policy.Message + " Update PitLaunch before switching setups from the app, tray, hotkey, game detection, command line, or Stream Deck."
                : null);
            _updatePolicyReady.TrySetResult(true);
            await DispatchToUi(() =>
            {
                RefreshIntegrations();
                if (policy.IsRequired)
                {
                    _form.ShowWindow();
                    _form.ApplyStartupUpdateStatus(new UpdateStatus(
                        UpdateState.Required,
                        policy.Message,
                        Policy: policy));
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            UpdateStatus status = await _updates.CheckAsync(policy).ConfigureAwait(false);
            await DispatchToUi(() =>
            {
                _form.ApplyStartupUpdateStatus(status);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Error("Startup update check failed: " + ex.Message);
            _coordinator.SetSwitchBlockReason(null); // Policy failures deliberately fail open.
            _automationAllowed = true;
            _updatePolicyReady.TrySetResult(true);
            try
            {
                await DispatchToUi(() =>
                {
                    RefreshIntegrations();
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }
            catch { }
        }
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
        _trayMenu.Items.Add(MenuItem(
            "Toggle Desk / Rig",
            async (_, _) => await ToggleFromTrayAsync()));
        ToolStripMenuItem undo = MenuItem(
            "Undo last switch",
            async (_, _) => await UndoLastSwitchAsync());
        undo.Enabled = _coordinator.CanUndo && !_coordinator.IsBusy;
        _trayMenu.Items.Add(undo);
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
        if (request.Kind is LaunchRequestKind.ActivateProfile or LaunchRequestKind.ToggleProfile)
            await _updatePolicyReady.Task;

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
            case LaunchRequestKind.ToggleProfile:
                await ToggleFromCommandLineAsync();
                break;
            case LaunchRequestKind.UndoSwitch:
                await UndoLastSwitchAsync();
                break;
            case LaunchRequestKind.Exit:
                ExitApplication();
                break;
        }
    }

    private void RefreshIntegrations()
    {
        List<string> hotkeyWarnings = _hotkeys.RegisterProfiles(
            _coordinator.Document.Profiles,
            _coordinator.Document.Settings.ToggleHotkey);
        foreach (string warning in hotkeyWarnings) AppLog.Write(OperationSeverity.Warning, "Hotkeys: " + warning);
        if (!_hotkeys.EmergencyDisplayHotkeyRegistered && !_emergencyHotkeyWarningShown)
        {
            _emergencyHotkeyWarningShown = true;
            Notify("Emergency shortcut unavailable",
                $"{AppInfo.EmergencyDisplayHotkey} is already used by another app. Restore displays remains available from PitLaunch and its tray menu.",
                ToolTipIcon.Warning);
        }
        _games.Refresh(_automationAllowed);
        Profile? active = _coordinator.ActiveProfile;
        _tray.Text = active is null ? AppInfo.ProductName : TrimTrayText(AppInfo.ProductName + " - " + active.Name);
    }

    private async Task<OperationReport> RestoreAllDisplaysAsync(bool showWindow)
    {
        if (showWindow) _form.ShowWindow();
        OperationReport report = await _form.RestoreAllDisplaysAsync();
        ToolTipIcon icon = report.HasErrors
            ? ToolTipIcon.Error
            : report.HasWarnings ? ToolTipIcon.Warning : ToolTipIcon.Info;
        OperationMessage? important = report.Messages.FirstOrDefault(message => message.Severity == OperationSeverity.Error)
                                      ?? report.Messages.FirstOrDefault(message => message.Severity == OperationSeverity.Warning);
        Notify(report.Summary, important?.Message ?? "Every connected monitor is enabled.", icon);
        return report;
    }

    private IntegrationStateSnapshot BuildIntegrationState()
    {
        IntegrationProfileSnapshot[] profiles = _coordinator.Document.Profiles
            .Select(profile => new IntegrationProfileSnapshot(profile.Id, profile.Name, profile.Kind.ToString()))
            .ToArray();
        return new IntegrationStateSnapshot(
            profiles,
            _coordinator.Document.Runtime.ActiveProfileId,
            _coordinator.IsBusy);
    }

    private async Task<IntegrationActivationResult> ActivateForIntegrationAsync(Guid profileId)
    {
        ProfileSwitchCompleted? completed = await _coordinator.ActivateAsync(profileId, ActivationSource.Integration);
        if (completed is null)
        {
            return new IntegrationActivationResult(null, null, false, "That setup no longer exists in PitLaunch.");
        }

        return new IntegrationActivationResult(
            completed.Profile.Id,
            completed.Profile.Name,
            !completed.Report.HasErrors,
            completed.Report.Summary);
    }

    private async Task<IntegrationActivationResult> ActivateToggleForIntegrationAsync()
    {
        ProfileSwitchCompleted? completed = await _coordinator.ToggleDeskRigAsync(ActivationSource.Integration);
        return completed is null
            ? new IntegrationActivationResult(null, null, false, "Create a Desk and Sim Racing setup in PitLaunch first.")
            : new IntegrationActivationResult(
                completed.Profile.Id,
                completed.Profile.Name,
                !completed.Report.HasErrors,
                completed.Report.Summary);
    }

    private async Task ToggleFromTrayAsync()
    {
        bool found = await _form.ToggleDeskRigAsync(ActivationSource.Tray);
        if (!found)
        {
            Notify("No setup to switch to", "Create a Desk and Sim Racing setup first.", ToolTipIcon.Warning);
        }
    }

    private async Task ToggleFromCommandLineAsync()
    {
        ProfileSwitchCompleted? completed = await _coordinator.ToggleDeskRigAsync(ActivationSource.CommandLine);
        if (completed is null)
        {
            Notify("No setup to switch to", "Create a Desk and Sim Racing setup first.", ToolTipIcon.Warning);
        }
    }

    private async Task ActivateToggleFromHotkeyAsync()
    {
        ProfileSwitchCompleted? completed = await _coordinator.ToggleDeskRigAsync(ActivationSource.Hotkey);
        if (completed is null)
            Notify("Two setups needed", "Create both a Desk and Sim Racing setup to use the toggle hotkey.", ToolTipIcon.Warning);
    }

    private async Task UndoLastSwitchAsync()
    {
        if (!_coordinator.CanUndo)
        {
            Notify("Nothing to undo", "PitLaunch has no previous switch checkpoint.", ToolTipIcon.Warning);
            return;
        }

        OperationReport report = await _coordinator.UndoLastSwitchAsync();
        ToolTipIcon icon = report.HasErrors
            ? ToolTipIcon.Error
            : report.HasWarnings ? ToolTipIcon.Warning : ToolTipIcon.Info;
        Notify("Undo last switch", report.HasErrors ? report.Summary : "Returned to the previous setup.", icon);
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

    private Task<T> DispatchToUi<T>(Func<Task<T>> action)
    {
        if (_disposed || _form.IsDisposed) return Task.FromException<T>(new ObjectDisposedException(nameof(PitLaunchContext)));
        TaskCompletionSource<T> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _form.BeginInvoke(async () =>
            {
                try
                {
                    completion.SetResult(await action());
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
            _integration.Dispose();
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
