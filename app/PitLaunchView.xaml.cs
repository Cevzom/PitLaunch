using System.Diagnostics;
using Forms = System.Windows.Forms;
using Controls = System.Windows.Controls;
using Input = System.Windows.Input;
using Media = System.Windows.Media;
using Animation = System.Windows.Media.Animation;
using Shapes = System.Windows.Shapes;

namespace PitLaunch;

using Wpf = global::System.Windows;
using Fluent = global::Wpf.Ui.Controls;

public partial class PitLaunchView : Controls.UserControl
{
    private enum AppPage { Home, Profile, Settings }
    private enum DialogMode { None, Text, Confirm, SetupGuide }

    private readonly ProfileCoordinator _coordinator;
    private readonly MainForm _owner;
    private AppPage _page = AppPage.Home;
    private Guid? _selectedProfileId;
    private int _pollSeconds = 2;
    private string _hotkeyCommitted = string.Empty;
    private int _toastVersion;
    private int _busyVersion;
    private int _homeRefreshVersion;
    private bool _navIndicatorInitialized;
    private bool _startupChooser;
    private Controls.Button? _firstChooserButton;
    private DialogMode _dialogMode;
    private TaskCompletionSource<string?>? _textDialog;
    private TaskCompletionSource<bool>? _confirmDialog;
    private TaskCompletionSource<SetupGuideResult?>? _setupGuideDialog;
    private List<DisplayDeviceOption> _setupDisplayDevices = [];
    private readonly HashSet<string> _setupSelectedDisplays = new(StringComparer.OrdinalIgnoreCase);
    private string? _setupPrimaryDisplayPath;
    private SetupGuideIdentity? _setupGuideIdentity;

    internal PitLaunchView(ProfileCoordinator coordinator, MainForm owner)
    {
        InitializeComponent();
        _coordinator = coordinator;
        _owner = owner;

        Loaded += (_, _) =>
        {
            RefreshAll();
            PlayWindowReveal();
            Focus();
            Input.Keyboard.Focus(this);
        };

        _coordinator.ProfilesChanged += () => RunOnUi(RefreshAll);
        _coordinator.BusyChanged += (busy, message) => RunOnUi(() => SetBusy(busy, message));
        _coordinator.SwitchCompleted += completed => RunOnUi(() => ShowReport(completed.Report));
    }

    internal async Task PromptCaptureAsync()
    {
        AppLog.Info("Capture: Setup guide opened.");
        SetupGuideResult? setup = await ShowSetupGuideAsync();
        if (setup is null)
        {
            AppLog.Info("Capture: Setup guide canceled.");
            return;
        }
        AppLog.Info("Capture: Setup guide confirmed. Creating validated device plan.");
        try
        {
            (Profile? profile, OperationReport report) = await _coordinator.CreateConfiguredAsync(
                setup.Name,
                setup.Kind,
                setup.RigDisplay,
                setup.Display,
                setup.Audio);
            if (profile is null)
            {
                RunOnUi(() => ShowReport(report));
                return;
            }

            _selectedProfileId = profile.Id;
            RunOnUi(RefreshAll);
            // The guide's button is "Create and switch" — the user already committed, so skip the extra dialog.
            await ActivateProfileAsync(profile.Id, ActivationSource.User, bypassConfirm: true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Configured setup request failed: " + ex);
            RunOnUi(() => ShowError(ex.Message));
        }
    }

    internal async Task CaptureNamedAsync(
        string name,
        SetupKind kind = SetupKind.Auto,
        RigDisplayVariant rigDisplay = RigDisplayVariant.Auto)
    {
        try
        {
            (Profile? profile, OperationReport report) = await _coordinator.CaptureNewAsync(name);
            if (profile is not null)
            {
                profile.Kind = kind;
                profile.RigDisplay = kind == SetupKind.SimRacing ? rigDisplay : RigDisplayVariant.Auto;
                _coordinator.SaveProfile(profile);
                _selectedProfileId = profile.Id;
            }
            RunOnUi(() =>
            {
                RefreshAll();
                ShowReport(report);
            });
        }
        catch (Exception ex)
        {
            AppLog.Error("Capture request failed: " + ex);
            RunOnUi(() => ShowError(ex.Message));
        }
    }

    private string SuggestCaptureName()
    {
        HashSet<string> existing = _coordinator.Document.Profiles
            .Select(profile => profile.Name)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        for (int number = 1; number < 1000; number++)
        {
            string candidate = $"Setup {number}";
            if (!existing.Contains(candidate)) return candidate;
        }
        return "New setup";
    }

    private Task<SetupGuideResult?> ShowSetupGuideAsync()
    {
        CloseAnyDialog();
        _dialogMode = DialogMode.SetupGuide;
        _setupGuideDialog = new TaskCompletionSource<SetupGuideResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _setupGuideIdentity = null;
        _setupDisplayDevices = [];
        _setupSelectedDisplays.Clear();
        _setupPrimaryDisplayPath = null;

        bool hasDesk = _coordinator.Document.Profiles.Any(profile => EffectiveSetupKind(profile) == SetupKind.Desk);
        SetupKind suggestedKind = hasDesk ? SetupKind.SimRacing : SetupKind.Desk;
        List<RigDisplayOption> variants = CreateRigDisplayOptions();
        SetupGuideVariant.ItemsSource = variants;
        SetupGuideVariant.SelectedItem = variants.First(option => option.Value == RigDisplayVariant.Auto);
        SetupDeskChoice.IsChecked = suggestedKind == SetupKind.Desk;
        SetupSimChoice.IsChecked = suggestedKind == SetupKind.SimRacing;
        SetupGuideName.Text = suggestedKind == SetupKind.SimRacing ? "Sim Racing" : "Desk";
        SetupGuideVariantHost.Visibility = suggestedKind == SetupKind.SimRacing
            ? Wpf.Visibility.Visible
            : Wpf.Visibility.Collapsed;
        SetupGuideError.Visibility = Wpf.Visibility.Collapsed;
        SetupGuideHardwareError.Visibility = Wpf.Visibility.Collapsed;
        SetupGuideCaptureButton.IsEnabled = true;
        ShowSetupGuideStep(prepare: false, animate: false);

        SetupGuideLayer.Visibility = Wpf.Visibility.Visible;
        SetupGuideLayer.Opacity = 0;
        SetupGuideLayer.BeginAnimation(OpacityProperty, DoubleAnimationTo(1, 160));
        Dispatcher.BeginInvoke(() =>
        {
            SetupGuideName.Focus();
            SetupGuideName.SelectAll();
        });
        return _setupGuideDialog.Task;
    }

    private void SetupGuideType_Checked(object sender, Wpf.RoutedEventArgs e)
    {
        if (SetupGuideVariantHost is null || SetupGuideName is null) return;
        bool sim = SetupSimChoice.IsChecked == true;
        SetupGuideVariantHost.Visibility = sim ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        string current = SetupGuideName.Text.Trim();
        if (current.Length == 0 || current is "Desk" or "Sim Racing" || current.StartsWith("Setup ", StringComparison.Ordinal))
        {
            SetupGuideName.Text = sim ? "Sim Racing" : "Desk";
            SetupGuideName.SelectAll();
        }
    }

    private bool TryReadSetupIdentity(out SetupGuideIdentity result)
    {
        string name = SetupGuideName.Text.Trim();
        if (name.Length == 0)
        {
            SetupGuideError.Text = "Enter a setup name.";
            SetupGuideError.Visibility = Wpf.Visibility.Visible;
            SetupGuideName.Focus();
            result = default!;
            return false;
        }
        if (_coordinator.Document.Profiles.Any(profile =>
                string.Equals(profile.Name, name, StringComparison.CurrentCultureIgnoreCase)))
        {
            SetupGuideError.Text = "A setup with that name already exists.";
            SetupGuideError.Visibility = Wpf.Visibility.Visible;
            SetupGuideName.Focus();
            SetupGuideName.SelectAll();
            result = default!;
            return false;
        }

        SetupKind kind = SetupSimChoice.IsChecked == true ? SetupKind.SimRacing : SetupKind.Desk;
        RigDisplayVariant variant = kind == SetupKind.SimRacing && SetupGuideVariant.SelectedItem is RigDisplayOption option
            ? option.Value
            : RigDisplayVariant.Auto;
        SetupGuideError.Visibility = Wpf.Visibility.Collapsed;
        result = new SetupGuideIdentity(name, kind, variant);
        return true;
    }

    private void SetupGuideNext_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (!TryReadSetupIdentity(out SetupGuideIdentity setup)) return;
        _setupGuideIdentity = setup;
        SetupGuidePrepareTitle.Text = setup.Kind == SetupKind.SimRacing
            ? "Choose the sim rig hardware"
            : "Choose the desk hardware";
        PrepareSetupHardware(setup);
        ShowSetupGuideStep(prepare: true, animate: true);
    }

    private void PrepareSetupHardware(SetupGuideIdentity setup)
    {
        SetupGuideHardwareError.Visibility = Wpf.Visibility.Collapsed;
        SetupGuideCaptureButton.IsEnabled = true;
        try
        {
            _setupDisplayDevices = _coordinator.ListConnectedDisplays();
            if (_setupDisplayDevices.Count == 0)
            {
                throw new InvalidOperationException("PitLaunch could not find a connected display.");
            }

            SelectRecommendedDisplays(setup);
            RenderSetupDisplayChoices();
            SetupGuideLayoutPicker.ItemsSource = new List<DisplayLayoutOption>
            {
                new(DisplayLayoutMode.Recommended, "Recommended"),
                new(DisplayLayoutMode.Horizontal, "Line up horizontally"),
                new(DisplayLayoutMode.KeepCurrent, "Keep current positions")
            };
            SetupGuideLayoutPicker.SelectedIndex = 0;
            UpdateSetupMainPicker();
            PopulateSetupAudioPickers(setup);
            UpdateSetupPreview();
        }
        catch (Exception ex)
        {
            SetupGuideCaptureButton.IsEnabled = false;
            SetupGuideHardwareError.Text = ex.Message;
            SetupGuideHardwareError.Visibility = Wpf.Visibility.Visible;
            SetupGuideValidationStrip.Background = Brush("ErrorSoftBrush");
            SetupGuideValidationStrip.BorderBrush = Brush("ErrorBrush");
            SetupGuideValidationText.Text = "Device discovery needs attention before this setup can be created.";
        }
    }

    private void SelectRecommendedDisplays(SetupGuideIdentity setup)
    {
        _setupSelectedDisplays.Clear();
        Profile? matchingProfile = _coordinator.Document.Profiles.FirstOrDefault(profile =>
            EffectiveSetupKind(profile) == setup.Kind);
        if (matchingProfile is not null)
        {
            foreach (MonitorSnapshot monitor in matchingProfile.Display.Monitors.Where(monitor => monitor.Enabled))
            {
                if (_setupDisplayDevices.Any(device => DevicePathEquals(device.DevicePath, monitor.DevicePath)))
                {
                    _setupSelectedDisplays.Add(monitor.DevicePath);
                }
            }
        }

        if (_setupSelectedDisplays.Count == 0 && setup.Kind == SetupKind.Desk)
        {
            foreach (DisplayDeviceOption device in _setupDisplayDevices.Where(device => device.IsActive))
            {
                _setupSelectedDisplays.Add(device.DevicePath);
            }
        }

        if (_setupSelectedDisplays.Count == 0 && setup.Kind == SetupKind.SimRacing)
        {
            HashSet<string> deskDisplays = _coordinator.Document.Profiles
                .Where(profile => EffectiveSetupKind(profile) == SetupKind.Desk)
                .SelectMany(profile => profile.Display.Monitors)
                .Where(monitor => monitor.Enabled)
                .Select(monitor => monitor.DevicePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<DisplayDeviceOption> candidates = _setupDisplayDevices
                .OrderBy(device => deskDisplays.Contains(device.DevicePath))
                .ThenBy(device => device.IsActive)
                .ThenByDescending(device => device.Width * (ulong)device.Height)
                .ToList();

            int requestedCount = DesiredRigDisplayCount(setup.RigDisplay);
            if (requestedCount == 0)
            {
                List<DisplayDeviceOption> dedicated = candidates
                    .Where(device => !deskDisplays.Contains(device.DevicePath) && !device.IsActive)
                    .ToList();
                requestedCount = Math.Max(1, dedicated.Count);
            }
            foreach (DisplayDeviceOption device in candidates.Take(Math.Min(requestedCount, candidates.Count)))
            {
                _setupSelectedDisplays.Add(device.DevicePath);
            }
        }

        if (_setupSelectedDisplays.Count == 0)
        {
            DisplayDeviceOption fallback = _setupDisplayDevices.FirstOrDefault(device => device.IsPrimary)
                ?? _setupDisplayDevices[0];
            _setupSelectedDisplays.Add(fallback.DevicePath);
        }

        string? savedPrimary = matchingProfile?.Display.Monitors.FirstOrDefault(monitor => monitor.Enabled && monitor.Primary)?.DevicePath;
        _setupPrimaryDisplayPath = _setupDisplayDevices
            .Where(device => _setupSelectedDisplays.Contains(device.DevicePath))
            .FirstOrDefault(device => DevicePathEquals(device.DevicePath, savedPrimary ?? string.Empty))?.DevicePath
            ?? _setupDisplayDevices.FirstOrDefault(device =>
                _setupSelectedDisplays.Contains(device.DevicePath) && device.IsPrimary)?.DevicePath
            ?? _setupDisplayDevices.First(device => _setupSelectedDisplays.Contains(device.DevicePath)).DevicePath;
    }

    private static int DesiredRigDisplayCount(RigDisplayVariant variant) => variant switch
    {
        RigDisplayVariant.SingleScreen or RigDisplayVariant.Ultrawide or RigDisplayVariant.Vr => 1,
        RigDisplayVariant.DualScreen => 2,
        RigDisplayVariant.TripleScreen => 3,
        RigDisplayVariant.QuadScreen => 4,
        _ => 0
    };

    private void RenderSetupDisplayChoices()
    {
        SetupGuideDisplayList.Children.Clear();
        SetupGuideDeviceCount.Text = _setupDisplayDevices.Count == 1
            ? "1 connected"
            : $"{_setupDisplayDevices.Count} connected";

        for (int index = 0; index < _setupDisplayDevices.Count; index++)
        {
            DisplayDeviceOption device = _setupDisplayDevices[index];
            System.Windows.Controls.Primitives.ToggleButton tile = new()
            {
                Tag = device.DevicePath,
                IsChecked = _setupSelectedDisplays.Contains(device.DevicePath),
                Style = (Wpf.Style)FindResource("SetupMonitorChoice"),
                ToolTip = device.FriendlyName
            };
            Wpf.Automation.AutomationProperties.SetName(tile, "Use " + device.FriendlyName);

            Controls.Grid layout = new();
            layout.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(34) });
            layout.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
            Controls.Border icon = new()
            {
                Width = 27,
                Height = 27,
                CornerRadius = new Wpf.CornerRadius(5),
                Background = device.IsActive ? Brush("AccentSoftBrush") : Brush("SurfaceRaisedBrush"),
                VerticalAlignment = Wpf.VerticalAlignment.Center
            };
            icon.Child = new Fluent.SymbolIcon
            {
                Symbol = Fluent.SymbolRegular.Desktop24,
                FontSize = 14,
                Foreground = device.IsActive ? Brush("AccentBrush") : Brush("MutedBrush"),
                HorizontalAlignment = Wpf.HorizontalAlignment.Center,
                VerticalAlignment = Wpf.VerticalAlignment.Center
            };
            layout.Children.Add(icon);
            Controls.StackPanel copy = new() { VerticalAlignment = Wpf.VerticalAlignment.Center };
            Controls.TextBlock name = Text(device.FriendlyName, 13, Brush("TextBrush"), Wpf.FontWeights.SemiBold);
            name.TextTrimming = Wpf.TextTrimming.CharacterEllipsis;
            copy.Children.Add(name);
            string mode = $"{device.Width} x {device.Height}  {device.RefreshHz:0.#} Hz";
            copy.Children.Add(Text(mode, 11.5, Brush("MutedBrush")));
            copy.Children.Add(Text(device.IsActive ? "Active now" : "Available", 11, device.IsActive ? Brush("AccentBrush") : Brush("FaintBrush")));
            Controls.Grid.SetColumn(copy, 1);
            layout.Children.Add(copy);
            tile.Content = layout;
            tile.Checked += SetupDisplayChoice_Changed;
            tile.Unchecked += SetupDisplayChoice_Changed;
            tile.Opacity = 0;
            tile.RenderTransform = new Media.TranslateTransform(0, 6);
            Animation.DoubleAnimation fade = StrongDoubleAnimationTo(1, 240);
            fade.BeginTime = TimeSpan.FromMilliseconds(index * 42);
            tile.BeginAnimation(OpacityProperty, fade);
            Animation.DoubleAnimation rise = StrongDoubleAnimationTo(0, 280);
            rise.BeginTime = TimeSpan.FromMilliseconds(index * 42);
            ((Media.TranslateTransform)tile.RenderTransform).BeginAnimation(Media.TranslateTransform.YProperty, rise);
            SetupGuideDisplayList.Children.Add(tile);
        }
    }

    private void SetupDisplayChoice_Changed(object sender, Wpf.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { Tag: string path } tile) return;
        if (tile.IsChecked == true)
        {
            _setupSelectedDisplays.Add(path);
        }
        else
        {
            if (_setupSelectedDisplays.Count == 1 && _setupSelectedDisplays.Contains(path))
            {
                tile.IsChecked = true;
                ShowSetupHardwareError("Keep at least one display selected.");
                return;
            }
            _setupSelectedDisplays.Remove(path);
        }

        if (_setupPrimaryDisplayPath is null || !_setupSelectedDisplays.Contains(_setupPrimaryDisplayPath))
        {
            _setupPrimaryDisplayPath = _setupDisplayDevices
                .First(device => _setupSelectedDisplays.Contains(device.DevicePath)).DevicePath;
        }
        SetupGuideHardwareError.Visibility = Wpf.Visibility.Collapsed;
        UpdateSetupMainPicker();
        UpdateSetupPreview();
    }

    private void UpdateSetupMainPicker()
    {
        List<DisplayPickerOption> options = _setupDisplayDevices
            .Where(device => _setupSelectedDisplays.Contains(device.DevicePath))
            .Select(device => new DisplayPickerOption(device.DevicePath, device.FriendlyName))
            .ToList();
        if (options.Count == 0) return;
        _setupPrimaryDisplayPath = options.Any(option => DevicePathEquals(option.DevicePath, _setupPrimaryDisplayPath ?? string.Empty))
            ? _setupPrimaryDisplayPath
            : options[0].DevicePath;
        SetupGuideMainPicker.ItemsSource = options;
        SetupGuideMainPicker.SelectedItem = options.First(option =>
            DevicePathEquals(option.DevicePath, _setupPrimaryDisplayPath!));
    }

    private void SetupGuideMainPicker_SelectionChanged(object sender, Controls.SelectionChangedEventArgs e)
    {
        if (SetupGuideMainPicker.SelectedItem is not DisplayPickerOption option) return;
        _setupPrimaryDisplayPath = option.DevicePath;
        UpdateSetupPreview();
    }

    private void SetupGuideLayoutPicker_SelectionChanged(object sender, Controls.SelectionChangedEventArgs e) =>
        UpdateSetupPreview();

    private void PopulateSetupAudioPickers(SetupGuideIdentity setup)
    {
        List<AudioDeviceOption> playback = _coordinator.ListAudioDevices(false);
        List<AudioDeviceOption> microphones = _coordinator.ListAudioDevices(true);
        AudioSnapshot current = _coordinator.CaptureCurrentAudio();
        Profile? matching = _coordinator.Document.Profiles.FirstOrDefault(profile =>
            EffectiveSetupKind(profile) == setup.Kind);

        AudioEndpointSnapshot? playbackChoice = AvailableEndpoint(playback, matching?.Audio.Playback)
            ?? AvailableEndpoint(playback, current.Playback)
            ?? ToEndpoint(playback.FirstOrDefault());
        AudioEndpointSnapshot? microphoneChoice = AvailableEndpoint(microphones, matching?.Audio.Microphone)
            ?? AvailableEndpoint(microphones, current.Microphone)
            ?? ToEndpoint(microphones.FirstOrDefault());

        FillAudioPicker(SetupGuidePlaybackPicker, playback, playbackChoice);
        FillAudioPicker(SetupGuideMicrophonePicker, microphones, microphoneChoice);
    }

    private static AudioEndpointSnapshot? AvailableEndpoint(
        IReadOnlyList<AudioDeviceOption> devices,
        AudioEndpointSnapshot? endpoint) =>
        endpoint is not null && devices.Any(device => string.Equals(device.Id, endpoint.DeviceId, StringComparison.Ordinal))
            ? endpoint
            : null;

    private static AudioEndpointSnapshot? ToEndpoint(AudioDeviceOption? option) => option is null
        ? null
        : new AudioEndpointSnapshot { DeviceId = option.Id, FriendlyName = option.Name };

    private DisplaySetupRequest CurrentSetupDisplayRequest()
    {
        if (_setupSelectedDisplays.Count == 0 || string.IsNullOrWhiteSpace(_setupPrimaryDisplayPath))
        {
            throw new InvalidOperationException("Choose at least one display and a main screen.");
        }
        DisplayLayoutMode layout = SetupGuideLayoutPicker.SelectedItem is DisplayLayoutOption option
            ? option.Value
            : DisplayLayoutMode.Recommended;
        List<string> ordered = _setupDisplayDevices
            .Where(device => _setupSelectedDisplays.Contains(device.DevicePath))
            .Select(device => device.DevicePath)
            .ToList();
        return new DisplaySetupRequest(ordered, _setupPrimaryDisplayPath, layout);
    }

    private void UpdateSetupPreview()
    {
        if (SetupGuidePreviewHost is null || SetupGuideLayoutPicker is null) return;
        try
        {
            DisplaySnapshot snapshot = _coordinator.BuildDisplaySnapshot(CurrentSetupDisplayRequest());
            Controls.Canvas preview = CreateMiniDisplayPreview(new Profile { Display = snapshot });
            preview.Opacity = 0;
            preview.BeginAnimation(OpacityProperty, StrongDoubleAnimationTo(1, 260));
            SetupGuidePreviewHost.Content = preview;
            MonitorSnapshot main = snapshot.Monitors.First(monitor => monitor.Enabled && monitor.Primary);
            int count = snapshot.Monitors.Count(monitor => monitor.Enabled);
            SetupGuidePreviewSummary.Text = $"{count} screen{(count == 1 ? string.Empty : "s")}  |  {main.FriendlyName} main";
            SetupGuideValidationStrip.Background = Brush("InfoSoftBrush");
            SetupGuideValidationStrip.BorderBrush = NewBrush("#35527B");
            SetupGuideValidationText.Text = "Ready. PitLaunch will ask Windows to validate this exact plan before it saves or switches.";
            SetupGuideCaptureButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            SetupGuideCaptureButton.IsEnabled = false;
            ShowSetupHardwareError(ex.Message);
        }
    }

    private void ShowSetupHardwareError(string message)
    {
        SetupGuideHardwareError.Text = message;
        SetupGuideHardwareError.Visibility = Wpf.Visibility.Visible;
        SetupGuideValidationStrip.Background = Brush("ErrorSoftBrush");
        SetupGuideValidationStrip.BorderBrush = Brush("ErrorBrush");
        SetupGuideValidationText.Text = "This plan is not ready yet.";
    }

    private void SetupGuideBack_Click(object sender, Wpf.RoutedEventArgs e) =>
        ShowSetupGuideStep(prepare: false, animate: true);

    private void ShowSetupGuideStep(bool prepare, bool animate)
    {
        Wpf.FrameworkElement outgoing = prepare ? SetupGuideIdentityStep : SetupGuidePrepareStep;
        Wpf.FrameworkElement incoming = prepare ? SetupGuidePrepareStep : SetupGuideIdentityStep;
        SetupGuideStepLabel.Text = prepare ? "STEP 2 OF 2" : "STEP 1 OF 2";
        SetupGuideBackButton.Visibility = prepare ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        SetupGuideNextButton.Visibility = prepare ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
        SetupGuideCaptureButton.Visibility = prepare ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;

        if (!animate)
        {
            outgoing.BeginAnimation(OpacityProperty, null);
            incoming.BeginAnimation(OpacityProperty, null);
            EnsureTranslate(outgoing).BeginAnimation(Media.TranslateTransform.YProperty, null);
            EnsureTranslate(incoming).BeginAnimation(Media.TranslateTransform.YProperty, null);
            outgoing.Visibility = Wpf.Visibility.Collapsed;
            outgoing.Opacity = 0;
            incoming.Visibility = Wpf.Visibility.Visible;
            incoming.Opacity = 1;
            EnsureTranslate(incoming).Y = 0;
            return;
        }

        incoming.Visibility = Wpf.Visibility.Visible;
        incoming.Opacity = 0;
        Media.TranslateTransform incomingTransform = EnsureTranslate(incoming);
        incomingTransform.Y = 9;
        Animation.DoubleAnimation fadeOut = StrongDoubleAnimationTo(0, 150);
        fadeOut.Completed += (_, _) => outgoing.Visibility = Wpf.Visibility.Collapsed;
        outgoing.BeginAnimation(OpacityProperty, fadeOut);
        EnsureTranslate(outgoing).BeginAnimation(Media.TranslateTransform.YProperty, StrongDoubleAnimationTo(-5, 170));
        incoming.BeginAnimation(OpacityProperty, StrongDoubleAnimationTo(1, 280));
        incomingTransform.BeginAnimation(Media.TranslateTransform.YProperty, StrongDoubleAnimationTo(0, 300));
    }

    private void SetupGuideCapture_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (!TryReadSetupIdentity(out SetupGuideIdentity identity))
        {
            ShowSetupGuideStep(prepare: false, animate: true);
            return;
        }

        try
        {
            DisplaySetupRequest display = CurrentSetupDisplayRequest();
            _ = _coordinator.BuildDisplaySnapshot(display);
            AudioEndpointSnapshot? playback = SelectedEndpoint(SetupGuidePlaybackPicker);
            AudioSnapshot audio = new()
            {
                Playback = playback,
                Communications = playback is null ? null : new AudioEndpointSnapshot
                {
                    DeviceId = playback.DeviceId,
                    FriendlyName = playback.FriendlyName
                },
                Microphone = SelectedEndpoint(SetupGuideMicrophonePicker)
            };
            CompleteSetupGuide(new SetupGuideResult(identity.Name, identity.Kind, identity.RigDisplay, display, audio));
        }
        catch (Exception ex)
        {
            ShowSetupHardwareError(ex.Message);
        }
    }

    private void SetupGuideCancel_Click(object sender, Wpf.RoutedEventArgs e) => CompleteSetupGuide(null);

    private void CompleteSetupGuide(SetupGuideResult? setup)
    {
        _setupGuideDialog?.TrySetResult(setup);
        _dialogMode = DialogMode.None;
        Animation.DoubleAnimation fade = DoubleAnimationTo(0, 130);
        fade.Completed += (_, _) => SetupGuideLayer.Visibility = Wpf.Visibility.Collapsed;
        SetupGuideLayer.BeginAnimation(OpacityProperty, fade);
    }

    private void SetupGuideLayer_MouseLeftButtonDown(object sender, Input.MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, SetupGuideLayer)) CompleteSetupGuide(null);
    }

    internal async Task ActivateProfileAsync(Guid profileId, ActivationSource source, bool bypassConfirm = false)
    {
        Profile? target = _coordinator.Document.Profiles.FirstOrDefault(profile => profile.Id == profileId);
        if (target is null) return;
        if (_coordinator.Document.Settings.ConfirmBeforeSwitch && !bypassConfirm)
        {
            if (_dialogMode != DialogMode.None)
            {
                AppLog.Info($"Switch to {target.Name} was ignored because another dialog was open.");
                ShowToast("Switch not started", "Finish the open dialog, then try again.", "WarningBrush");
                return;
            }
            if (!_owner.Visible) _owner.ShowWindow();
            bool confirmed = await ShowConfirmDialogAsync(
                $"Switch to {target.Name}?",
                "PitLaunch will change displays and sound devices. Screens may blink for a few seconds.",
                "Switch setup",
                false);
            if (!confirmed)
            {
                AppLog.Info($"Switch to {target.Name} was canceled ({source}).");
                return;
            }
        }

        _selectedProfileId = profileId;
        ProfileSwitchCompleted? completed = await _coordinator.ActivateAsync(profileId, source);
        if (_startupChooser && completed is not null && !completed.Report.HasErrors)
        {
            _owner.HideToTray();
        }
    }

    internal async Task<OperationReport> RestoreAllDisplaysAsync()
    {
        OperationReport report = await _coordinator.RestoreAllDisplaysAsync();
        RunOnUi(() => ShowReport(report));
        return report;
    }

    internal void EnterStartupChooser()
    {
        _startupChooser = true;
        if (_page != AppPage.Home)
        {
            CurrentPageElement().Visibility = Wpf.Visibility.Collapsed;
            _page = AppPage.Home;
            HomePage.Visibility = Wpf.Visibility.Visible;
            HomePage.Opacity = 1;
        }
        ApplyStartupChooserState();
        RefreshHome();
        Dispatcher.BeginInvoke(() => _firstChooserButton?.Focus());
    }

    internal void ExitStartupChooser()
    {
        if (!_startupChooser) return;
        _startupChooser = false;
        ApplyStartupChooserState();
        RefreshHome();
    }

    private void ApplyStartupChooserState()
    {
        SidebarColumn.Width = _startupChooser ? new Wpf.GridLength(0) : new Wpf.GridLength(216);
        SidebarHost.Visibility = _startupChooser ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
        StatsGrid.Visibility = _startupChooser ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
        CaptureButton.Visibility = _startupChooser ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
        StartupEyebrow.Visibility = _startupChooser ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        HomeTitle.Text = _startupChooser ? "How are you using this PC today?" : "Setups";
        HomeTitle.FontSize = _startupChooser ? 32 : 28;
        HomeSubtitle.Text = _startupChooser
            ? "Choose a setup and PitLaunch will restore its screens, sound, windows, and apps."
            : "Switch the whole PC between your desk and rig in one move.";
        SavedSetupsTitle.Text = _startupChooser ? "Choose a setup" : "Saved setups";
        EmptyStateTitle.Text = _startupChooser ? "No setups are ready yet" : "Create your first setup";
        EmptyStateCopy.Text = _startupChooser
            ? "Open PitLaunch after sign-in and create a Desk or Sim racing setup first."
            : "Start with Desk. When the rig is connected, create a second Sim racing setup.";
        HomePage.Margin = _startupChooser
            ? new Wpf.Thickness(52, 34, 52, 34)
            : new Wpf.Thickness(44, 30, 44, 34);
        FadeIn(HomePage);
    }

    internal void UpdateWindowState(bool maximized)
    {
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }

    internal void PlayWindowReveal()
    {
        if (!IsLoaded) return;
        Wpf.FrameworkElement page = CurrentPageElement();
        page.Opacity = 0.88;
        if (page.RenderTransform is not Media.TranslateTransform transform)
        {
            transform = new Media.TranslateTransform();
            page.RenderTransform = transform;
        }
        transform.Y = 8;
        page.BeginAnimation(OpacityProperty, DoubleAnimationTo(1, 170));
        transform.BeginAnimation(Media.TranslateTransform.YProperty, DoubleAnimationTo(0, 190));
    }

    private void RefreshAll()
    {
        RefreshNavigationState();
        RefreshHome();
        RefreshSettings();
        if (_page == AppPage.Profile) PopulateProfile();
    }

    private void RefreshHome()
    {
        Profile? active = _coordinator.ActiveProfile;
        CurrentStrip.Visibility = !_startupChooser && active is not null
            ? Wpf.Visibility.Visible
            : Wpf.Visibility.Collapsed;
        if (active is not null)
        {
            CurrentName.Text = active.Name;
            CurrentSummary.Text = BuildSummary(active);
        }

        int count = _coordinator.Document.Profiles.Count;
        ProfileCount.Text = _startupChooser
            ? count == 1 ? "1 choice" : $"{count} choices"
            : count == 1 ? "1 setup" : $"{count} setups";
        int activeDisplays = active?.Display.Monitors.Count(monitor => monitor.Enabled) ?? 0;
        int automated = _coordinator.Document.Profiles.Count(profile =>
            !string.IsNullOrWhiteSpace(profile.Hotkey) ||
            profile.GameProcesses.Count > 0 ||
            profile.Apps.Any(app => app.StartOnActivate));
        SetupStatValue.Text = count.ToString();
        DisplayStatValue.Text = activeDisplays.ToString();
        AutomationStatValue.Text = automated.ToString();
        if (!_startupChooser) AnimateStatBars(count, activeDisplays, automated);

        int refreshVersion = ++_homeRefreshVersion;
        ProfileWrap.Children.Clear();
        StartupProfileWrap.Children.Clear();
        _firstChooserButton = null;
        int cardIndex = 0;
        foreach (Profile profile in _coordinator.Document.Profiles
                     .OrderByDescending(item => item.Id == _coordinator.Document.Runtime.ActiveProfileId)
                     .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Controls.Border card = _startupChooser
                ? CreateStartupProfileCard(profile)
                : CreateProfileCard(profile);
            if (_startupChooser) StartupProfileWrap.Children.Add(card);
            else ProfileWrap.Children.Add(card);
            _ = AnimateCardEntryAsync(card, cardIndex++, refreshVersion);
        }
        Dispatcher.BeginInvoke(() => ProfileScroll.ScrollToTop());

        bool empty = count == 0;
        EmptyState.Visibility = empty ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        ProfileWrap.Visibility = empty || _startupChooser ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
        StartupProfileWrap.Visibility = empty || !_startupChooser ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
    }

    private void AnimateStatBars(int setups, int displays, int automated)
    {
        double setupTarget = setups == 0 ? 0 : Math.Min(1, 0.34 + setups * 0.16);
        double displayTarget = displays == 0 ? 0 : Math.Min(1, displays / 3d);
        double automationTarget = setups == 0 ? 0 : Math.Min(1, automated / (double)setups);
        AnimateStatBar(SetupStatBar, setupTarget, 0);
        AnimateStatBar(DisplayStatBar, displayTarget, 70);
        AnimateStatBar(AutomationStatBar, automationTarget, 140);
    }

    private static void AnimateStatBar(Wpf.FrameworkElement bar, double target, int delay)
    {
        if (bar.RenderTransform is not Media.ScaleTransform transform) return;
        Animation.DoubleAnimation animation = new(0, target, TimeSpan.FromMilliseconds(540))
        {
            BeginTime = TimeSpan.FromMilliseconds(delay),
            EasingFunction = new Animation.QuinticEase { EasingMode = Animation.EasingMode.EaseOut }
        };
        transform.BeginAnimation(Media.ScaleTransform.ScaleXProperty, animation);
    }

    private async Task AnimateCardEntryAsync(Controls.Border card, int index, int refreshVersion)
    {
        card.Opacity = 0;
        Media.TranslateTransform? lift = (card.RenderTransform as Media.TransformGroup)?.Children
            .OfType<Media.TranslateTransform>().FirstOrDefault();
        if (lift is not null) lift.Y = 10;
        await Task.Delay(45 + Math.Min(index, 6) * 50);
        if (refreshVersion != _homeRefreshVersion || card.Parent is null) return;
        card.BeginAnimation(OpacityProperty, StrongDoubleAnimationTo(1, 310));
        lift?.BeginAnimation(Media.TranslateTransform.YProperty, StrongDoubleAnimationTo(0, 330));
    }

    private Controls.Border CreateStartupProfileCard(Profile profile)
    {
        bool lastUsed = _coordinator.Document.Runtime.ActiveProfileId == profile.Id;
        bool sim = EffectiveSetupKind(profile) == SetupKind.SimRacing;
        Media.SolidColorBrush background = NewBrush(lastUsed ? "#1A2B25" : "#1D211E");
        Media.SolidColorBrush borderBrush = NewBrush(lastUsed ? "#3C7964" : "#303631");
        Media.Effects.DropShadowEffect shadow = new()
        {
            Color = Media.Colors.Black,
            BlurRadius = 16,
            ShadowDepth = 3,
            Opacity = lastUsed ? 0.22 : 0.14
        };
        Media.ScaleTransform scale = new(1, 1);
        Media.TranslateTransform lift = new();
        Media.TransformGroup motion = new();
        motion.Children.Add(scale);
        motion.Children.Add(lift);

        Controls.Border card = new()
        {
            MinHeight = 356,
            MaxWidth = 540,
            Margin = new Wpf.Thickness(0, 0, 12, 14),
            Padding = new Wpf.Thickness(16),
            CornerRadius = new Wpf.CornerRadius(8),
            Background = background,
            BorderBrush = borderBrush,
            BorderThickness = new Wpf.Thickness(1),
            HorizontalAlignment = Wpf.HorizontalAlignment.Stretch,
            RenderTransform = motion,
            RenderTransformOrigin = new Wpf.Point(0.5, 0.5),
            Effect = shadow
        };

        Controls.Grid layout = new();
        layout.RowDefinitions.Add(new Controls.RowDefinition { Height = new Wpf.GridLength(150) });
        layout.RowDefinitions.Add(new Controls.RowDefinition { Height = new Wpf.GridLength(54) });
        layout.RowDefinitions.Add(new Controls.RowDefinition { Height = new Wpf.GridLength(42) });
        layout.RowDefinitions.Add(new Controls.RowDefinition { Height = new Wpf.GridLength(42) });
        layout.RowDefinitions.Add(new Controls.RowDefinition { Height = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        layout.RowDefinitions.Add(new Controls.RowDefinition { Height = new Wpf.GridLength(40) });

        Controls.Border previewFrame = new()
        {
            CornerRadius = new Wpf.CornerRadius(7),
            Background = Brush("ChromeBrush"),
            BorderBrush = lastUsed ? Brush("AccentSoftBrush") : Brush("BorderSoftBrush"),
            BorderThickness = new Wpf.Thickness(1),
            ClipToBounds = true
        };
        Controls.Grid previewLayout = new();
        previewLayout.RowDefinitions.Add(new Controls.RowDefinition { Height = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        previewLayout.RowDefinitions.Add(new Controls.RowDefinition { Height = new Wpf.GridLength(30) });
        Controls.Viewbox preview = new()
        {
            Stretch = Media.Stretch.Uniform,
            Margin = new Wpf.Thickness(10, 7, 10, 2),
            Child = CreateMiniDisplayPreview(profile)
        };
        previewLayout.Children.Add(preview);
        Controls.Grid footer = new() { Background = Brush("SurfaceRaisedBrush"), Margin = new Wpf.Thickness(1, 0, 1, 1) };
        footer.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = Wpf.GridLength.Auto });
        Controls.TextBlock category = Text(GetSetupCategoryLabel(profile).ToUpperInvariant(), 9.5,
            sim ? Brush("InfoBrush") : Brush("FaintBrush"), Wpf.FontWeights.SemiBold);
        category.Margin = new Wpf.Thickness(9, 0, 0, 0);
        category.VerticalAlignment = Wpf.VerticalAlignment.Center;
        footer.Children.Add(category);
        Controls.TextBlock variant = Text(GetDisplayVariantLabel(profile).ToUpperInvariant(), 9.5,
            lastUsed ? Brush("AccentBrush") : Brush("MutedBrush"), Wpf.FontWeights.SemiBold);
        variant.Margin = new Wpf.Thickness(0, 0, 9, 0);
        variant.VerticalAlignment = Wpf.VerticalAlignment.Center;
        Controls.Grid.SetColumn(variant, 1);
        footer.Children.Add(variant);
        Controls.Grid.SetRow(footer, 1);
        previewLayout.Children.Add(footer);
        previewFrame.Child = previewLayout;
        layout.Children.Add(previewFrame);

        Controls.Grid heading = new() { Margin = new Wpf.Thickness(1, 12, 1, 4) };
        heading.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = Wpf.GridLength.Auto });
        Controls.TextBlock name = Text(profile.Name, 18, Brush("TextBrush"), Wpf.FontWeights.SemiBold);
        name.TextTrimming = Wpf.TextTrimming.CharacterEllipsis;
        name.VerticalAlignment = Wpf.VerticalAlignment.Center;
        heading.Children.Add(name);
        Controls.StackPanel badges = new() { Orientation = Controls.Orientation.Horizontal, VerticalAlignment = Wpf.VerticalAlignment.Center };
        badges.Children.Add(CreateBadge(GetDisplayVariantLabel(profile), sim ? "InfoBrush" : "MutedBrush",
            sim ? "InfoSoftBrush" : "SurfaceRaisedBrush"));
        if (lastUsed)
        {
            Controls.Border badge = CreateBadge("Last used", "AccentBrush", "AccentSoftBrush");
            badge.Margin = new Wpf.Thickness(6, 0, 0, 0);
            badges.Children.Add(badge);
        }
        Controls.Grid.SetColumn(badges, 1);
        heading.Children.Add(badges);
        Controls.Grid.SetRow(heading, 1);
        layout.Children.Add(heading);

        string playback = profile.Audio.Playback?.FriendlyName ?? "Keep current device";
        Controls.Border audio = CreateDetailRow(Fluent.SymbolRegular.Speaker224,
            Text(playback, 13, Brush("TextBrush"), Wpf.FontWeights.Medium), "Playback device");
        Controls.Grid.SetRow(audio, 2);
        layout.Children.Add(audio);
        Controls.Border apps = CreateDetailRow(Fluent.SymbolRegular.Apps24,
            CreateAppChips(profile), profile.Apps.Any(app => app.StartOnActivate) ? "Starts with this setup" : "No launch rules");
        Controls.Grid.SetRow(apps, 3);
        layout.Children.Add(apps);

        Controls.Button activate = Button("Use setup", "PrimaryButton");
        activate.HorizontalAlignment = Wpf.HorizontalAlignment.Stretch;
        activate.Click += async (_, _) => await ActivateProfileAsync(profile.Id, ActivationSource.User);
        Controls.Grid.SetRow(activate, 5);
        layout.Children.Add(activate);
        _firstChooserButton ??= activate;

        card.Child = layout;
        AttachCardMotion(card, background, borderBrush, shadow, scale, lift, lastUsed);
        return card;
    }

    private Controls.Border CreateProfileCard(Profile profile)
    {
        bool active = _coordinator.Document.Runtime.ActiveProfileId == profile.Id;
        Media.SolidColorBrush background = NewBrush(active ? "#1A2B25" : "#1D211E");
        Media.SolidColorBrush borderBrush = NewBrush(active ? "#3C7964" : "#303631");
        Media.Effects.DropShadowEffect shadow = new()
        {
            Color = Media.Colors.Black,
            BlurRadius = 14,
            ShadowDepth = 3,
            Opacity = active ? 0.2 : 0.12
        };
        Media.ScaleTransform scale = new(1, 1);
        Media.TranslateTransform lift = new();
        Media.TransformGroup motion = new();
        motion.Children.Add(scale);
        motion.Children.Add(lift);

        Controls.Border card = new()
        {
            MinHeight = 242,
            Margin = new Wpf.Thickness(0, 0, 0, 12),
            Padding = new Wpf.Thickness(18),
            CornerRadius = new Wpf.CornerRadius(8),
            Background = background,
            BorderBrush = borderBrush,
            BorderThickness = new Wpf.Thickness(1),
            HorizontalAlignment = Wpf.HorizontalAlignment.Stretch,
            RenderTransform = motion,
            RenderTransformOrigin = new Wpf.Point(0.5, 0.5),
            Effect = shadow
        };

        Controls.Grid layout = new();
        layout.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(224) });
        layout.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(18) });
        layout.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(16) });
        layout.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(108) });

        Controls.Border previewFrame = new()
        {
            Height = 204,
            CornerRadius = new Wpf.CornerRadius(7),
            Background = Brush("ChromeBrush"),
            BorderBrush = active ? Brush("AccentSoftBrush") : Brush("BorderSoftBrush"),
            BorderThickness = new Wpf.Thickness(1),
            ClipToBounds = true
        };
        Controls.Grid previewLayout = new();
        previewLayout.RowDefinitions.Add(new Controls.RowDefinition { Height = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        previewLayout.RowDefinitions.Add(new Controls.RowDefinition { Height = new Wpf.GridLength(34) });
        Controls.Viewbox previewView = new()
        {
            Stretch = Media.Stretch.Uniform,
            Margin = new Wpf.Thickness(10, 9, 10, 4),
            Child = CreateMiniDisplayPreview(profile)
        };
        previewLayout.Children.Add(previewView);
        Controls.Grid previewFooter = new()
        {
            Background = Brush("SurfaceRaisedBrush"),
            Margin = new Wpf.Thickness(1, 0, 1, 1)
        };
        previewFooter.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        previewFooter.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = Wpf.GridLength.Auto });
        int enabledDisplays = profile.Display.Monitors.Count(monitor => monitor.Enabled);
        bool sim = EffectiveSetupKind(profile) == SetupKind.SimRacing;
        Controls.TextBlock topology = Text(GetSetupCategoryLabel(profile).ToUpperInvariant(), 9.5,
            sim ? Brush("InfoBrush") : Brush("FaintBrush"), Wpf.FontWeights.SemiBold);
        topology.Margin = new Wpf.Thickness(10, 0, 0, 0);
        topology.VerticalAlignment = Wpf.VerticalAlignment.Center;
        previewFooter.Children.Add(topology);
        Controls.TextBlock online = Text(GetDisplayVariantLabel(profile).ToUpperInvariant(), 10.5,
            active ? Brush("AccentBrush") : Brush("MutedBrush"), Wpf.FontWeights.SemiBold);
        online.Margin = new Wpf.Thickness(0, 0, 10, 0);
        online.VerticalAlignment = Wpf.VerticalAlignment.Center;
        Controls.Grid.SetColumn(online, 1);
        previewFooter.Children.Add(online);
        Controls.Grid.SetRow(previewFooter, 1);
        previewLayout.Children.Add(previewFooter);
        previewFrame.Child = previewLayout;
        layout.Children.Add(previewFrame);

        Controls.Grid content = new();
        content.RowDefinitions.Add(new Controls.RowDefinition { Height = new Wpf.GridLength(36) });
        for (int row = 0; row < 4; row++) content.RowDefinitions.Add(new Controls.RowDefinition { Height = new Wpf.GridLength(42) });
        Controls.Grid titleLine = new();
        titleLine.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        titleLine.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = Wpf.GridLength.Auto });
        Controls.TextBlock name = Text(profile.Name, 17, Brush("TextBrush"), Wpf.FontWeights.SemiBold);
        name.TextTrimming = Wpf.TextTrimming.CharacterEllipsis;
        titleLine.Children.Add(name);
        Controls.StackPanel titleBadges = new() { Orientation = Controls.Orientation.Horizontal };
        titleBadges.Children.Add(CreateBadge(GetDisplayVariantLabel(profile), sim ? "InfoBrush" : "MutedBrush",
            sim ? "InfoSoftBrush" : "SurfaceRaisedBrush"));
        if (active)
        {
            Controls.Border badge = CreateBadge("Active", "AccentBrush", "AccentSoftBrush");
            badge.Margin = new Wpf.Thickness(6, 0, 0, 0);
            titleBadges.Children.Add(badge);
        }
        Controls.Grid.SetColumn(titleBadges, 1);
        titleLine.Children.Add(titleBadges);
        content.Children.Add(titleLine);

        string playback = profile.Audio.Playback?.FriendlyName ?? "Keep current device";
        AddCardDetail(content, 1, CreateDetailRow(Fluent.SymbolRegular.Speaker224,
            Text(playback, 13, Brush("TextBrush"), Wpf.FontWeights.Medium), "Default playback device"));
        AddCardDetail(content, 2, CreateDetailRow(Fluent.SymbolRegular.Apps24,
            CreateAppChips(profile), profile.Apps.Any(app => app.StartOnActivate) ? "Launches with this setup" : "No launch rules"));

        MonitorSnapshot? primary = profile.Display.Monitors.FirstOrDefault(monitor => monitor.Enabled && monitor.Primary)
                                   ?? profile.Display.Monitors.FirstOrDefault(monitor => monitor.Enabled);
        string resolution = primary is null
            ? "No active display"
            : $"{primary.Width} x {primary.Height} at {primary.RefreshHz:0.#} Hz";
        string displayDetail = primary is null
            ? "Connect a captured display to restore it"
            : $"{GetDisplayVariantLabel(profile)} - {primary.FriendlyName}";
        AddCardDetail(content, 3, CreateDetailRow(Fluent.SymbolRegular.Desktop24,
            Text(resolution, 13, Brush("TextBrush"), Wpf.FontWeights.Medium), displayDetail));

        DateTimeOffset? lastUsed = profile.LastUsedUtc ?? (active ? _coordinator.Document.Runtime.LastSwitchUtc : null);
        AddCardDetail(content, 4, CreateDetailRow(Fluent.SymbolRegular.Clock24,
            Text(FormatLastUsed(lastUsed), 13, Brush("TextBrush"), Wpf.FontWeights.Medium),
            $"Captured {profile.CapturedAtUtc.ToLocalTime():g}"));

        Controls.Grid.SetColumn(content, 2);
        layout.Children.Add(content);

        Controls.Grid commands = new();
        commands.RowDefinitions.Add(new Controls.RowDefinition { Height = Wpf.GridLength.Auto });
        commands.RowDefinitions.Add(new Controls.RowDefinition { Height = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        commands.RowDefinitions.Add(new Controls.RowDefinition { Height = Wpf.GridLength.Auto });
        Controls.Button details = IconButton(Fluent.SymbolRegular.Settings24, "Configure setup");
        details.HorizontalAlignment = Wpf.HorizontalAlignment.Right;
        details.Click += (_, _) => OpenProfile(profile.Id);
        commands.Children.Add(details);

        Controls.StackPanel stateBlock = new() { VerticalAlignment = Wpf.VerticalAlignment.Center };
        Controls.TextBlock stateLabel = Text(active ? "CURRENT MODE" : "READY", 9.5, active ? Brush("AccentBrush") : Brush("FaintBrush"), Wpf.FontWeights.SemiBold);
        stateLabel.HorizontalAlignment = Wpf.HorizontalAlignment.Center;
        stateBlock.Children.Add(stateLabel);
        Controls.TextBlock stateHint = Text(active ? "Applied" : "Saved", 11, Brush("MutedBrush"));
        stateHint.HorizontalAlignment = Wpf.HorizontalAlignment.Center;
        stateHint.Margin = new Wpf.Thickness(0, 4, 0, 0);
        stateBlock.Children.Add(stateHint);
        Controls.Grid.SetRow(stateBlock, 1);
        commands.Children.Add(stateBlock);

        Controls.Button activate = Button(active ? "Reapply" : "Switch", active ? "SecondaryButton" : "PrimaryButton");
        activate.MinWidth = 108;
        activate.Click += async (_, _) => await ActivateProfileAsync(profile.Id, ActivationSource.User);
        Controls.Grid.SetRow(activate, 2);
        commands.Children.Add(activate);
        Controls.Grid.SetColumn(commands, 4);
        layout.Children.Add(commands);

        card.Child = layout;
        card.MouseEnter += (_, _) =>
        {
            background.BeginAnimation(Media.SolidColorBrush.ColorProperty,
                new Animation.ColorAnimation(
                    active ? Media.Color.FromRgb(29, 54, 45) : Media.Color.FromRgb(36, 41, 37),
                    TimeSpan.FromMilliseconds(150)));
            borderBrush.BeginAnimation(Media.SolidColorBrush.ColorProperty,
                new Animation.ColorAnimation(active ? Media.Color.FromRgb(73, 143, 117) : Media.Color.FromRgb(67, 75, 68), TimeSpan.FromMilliseconds(150)));
            lift.BeginAnimation(Media.TranslateTransform.YProperty, DoubleAnimationTo(-3, 150));
            shadow.BeginAnimation(Media.Effects.DropShadowEffect.BlurRadiusProperty, DoubleAnimationTo(22, 150));
            shadow.BeginAnimation(Media.Effects.DropShadowEffect.OpacityProperty, DoubleAnimationTo(active ? 0.28 : 0.22, 150));
        };
        card.MouseLeave += (_, _) =>
        {
            Media.Color normal = active ? Media.Color.FromRgb(26, 43, 37) : Media.Color.FromRgb(29, 33, 30);
            background.BeginAnimation(Media.SolidColorBrush.ColorProperty,
                new Animation.ColorAnimation(normal, TimeSpan.FromMilliseconds(160)));
            borderBrush.BeginAnimation(Media.SolidColorBrush.ColorProperty,
                new Animation.ColorAnimation(active ? Media.Color.FromRgb(60, 121, 100) : Media.Color.FromRgb(48, 54, 49), TimeSpan.FromMilliseconds(160)));
            lift.BeginAnimation(Media.TranslateTransform.YProperty, DoubleAnimationTo(0, 160));
            shadow.BeginAnimation(Media.Effects.DropShadowEffect.BlurRadiusProperty, DoubleAnimationTo(14, 160));
            shadow.BeginAnimation(Media.Effects.DropShadowEffect.OpacityProperty, DoubleAnimationTo(active ? 0.2 : 0.12, 160));
            scale.BeginAnimation(Media.ScaleTransform.ScaleXProperty, DoubleAnimationTo(1, 90));
            scale.BeginAnimation(Media.ScaleTransform.ScaleYProperty, DoubleAnimationTo(1, 90));
        };
        card.PreviewMouseLeftButtonDown += (_, _) =>
        {
            scale.BeginAnimation(Media.ScaleTransform.ScaleXProperty, DoubleAnimationTo(0.994, 80));
            scale.BeginAnimation(Media.ScaleTransform.ScaleYProperty, DoubleAnimationTo(0.994, 80));
        };
        card.PreviewMouseLeftButtonUp += (_, _) =>
        {
            scale.BeginAnimation(Media.ScaleTransform.ScaleXProperty, DoubleAnimationTo(1, 110));
            scale.BeginAnimation(Media.ScaleTransform.ScaleYProperty, DoubleAnimationTo(1, 110));
        };
        return card;
    }

    private static void AttachCardMotion(
        Controls.Border card,
        Media.SolidColorBrush background,
        Media.SolidColorBrush borderBrush,
        Media.Effects.DropShadowEffect shadow,
        Media.ScaleTransform scale,
        Media.TranslateTransform lift,
        bool highlighted)
    {
        card.MouseEnter += (_, _) =>
        {
            background.BeginAnimation(Media.SolidColorBrush.ColorProperty,
                new Animation.ColorAnimation(
                    highlighted ? Media.Color.FromRgb(29, 54, 45) : Media.Color.FromRgb(36, 41, 37),
                    TimeSpan.FromMilliseconds(150)));
            borderBrush.BeginAnimation(Media.SolidColorBrush.ColorProperty,
                new Animation.ColorAnimation(
                    highlighted ? Media.Color.FromRgb(73, 143, 117) : Media.Color.FromRgb(67, 75, 68),
                    TimeSpan.FromMilliseconds(150)));
            lift.BeginAnimation(Media.TranslateTransform.YProperty, DoubleAnimationTo(-3, 150));
            shadow.BeginAnimation(Media.Effects.DropShadowEffect.BlurRadiusProperty, DoubleAnimationTo(22, 150));
            shadow.BeginAnimation(Media.Effects.DropShadowEffect.OpacityProperty,
                DoubleAnimationTo(highlighted ? 0.28 : 0.22, 150));
        };
        card.MouseLeave += (_, _) =>
        {
            background.BeginAnimation(Media.SolidColorBrush.ColorProperty,
                new Animation.ColorAnimation(
                    highlighted ? Media.Color.FromRgb(26, 43, 37) : Media.Color.FromRgb(29, 33, 30),
                    TimeSpan.FromMilliseconds(160)));
            borderBrush.BeginAnimation(Media.SolidColorBrush.ColorProperty,
                new Animation.ColorAnimation(
                    highlighted ? Media.Color.FromRgb(60, 121, 100) : Media.Color.FromRgb(48, 54, 49),
                    TimeSpan.FromMilliseconds(160)));
            lift.BeginAnimation(Media.TranslateTransform.YProperty, DoubleAnimationTo(0, 160));
            shadow.BeginAnimation(Media.Effects.DropShadowEffect.BlurRadiusProperty, DoubleAnimationTo(16, 160));
            shadow.BeginAnimation(Media.Effects.DropShadowEffect.OpacityProperty,
                DoubleAnimationTo(highlighted ? 0.22 : 0.14, 160));
            scale.BeginAnimation(Media.ScaleTransform.ScaleXProperty, DoubleAnimationTo(1, 90));
            scale.BeginAnimation(Media.ScaleTransform.ScaleYProperty, DoubleAnimationTo(1, 90));
        };
        card.PreviewMouseLeftButtonDown += (_, _) =>
        {
            scale.BeginAnimation(Media.ScaleTransform.ScaleXProperty, DoubleAnimationTo(0.992, 80));
            scale.BeginAnimation(Media.ScaleTransform.ScaleYProperty, DoubleAnimationTo(0.992, 80));
        };
        card.PreviewMouseLeftButtonUp += (_, _) =>
        {
            scale.BeginAnimation(Media.ScaleTransform.ScaleXProperty, DoubleAnimationTo(1, 110));
            scale.BeginAnimation(Media.ScaleTransform.ScaleYProperty, DoubleAnimationTo(1, 110));
        };
    }

    private Controls.Canvas CreateMiniDisplayPreview(Profile profile)
    {
        Controls.Canvas canvas = new() { Width = 220, Height = 136 };
        List<MonitorSnapshot> enabled = profile.Display.Monitors.Where(monitor => monitor.Enabled).ToList();
        if (enabled.Count > 0)
        {
            double minX = enabled.Min(monitor => monitor.X);
            double minY = enabled.Min(monitor => monitor.Y);
            double maxX = enabled.Max(monitor => monitor.X + monitor.Width);
            double maxY = enabled.Max(monitor => monitor.Y + monitor.Height);
            double worldWidth = Math.Max(1, maxX - minX);
            double worldHeight = Math.Max(1, maxY - minY);
            double scale = Math.Min(194 / worldWidth, 92 / worldHeight);
            double contentWidth = worldWidth * scale;
            double contentHeight = worldHeight * scale;
            double offsetX = (220 - contentWidth) / 2;
            double offsetY = 12 + (92 - contentHeight) / 2;

            for (int index = 0; index < enabled.Count; index++)
            {
                MonitorSnapshot monitor = enabled[index];
                Controls.Border display = new()
                {
                    Width = Math.Max(34, monitor.Width * scale),
                    Height = Math.Max(23, monitor.Height * scale),
                    CornerRadius = new Wpf.CornerRadius(4),
                    Background = monitor.Primary ? Brush("AccentSoftBrush") : Brush("SurfaceRaisedBrush"),
                    BorderBrush = monitor.Primary ? Brush("AccentBrush") : Brush("BorderBrush"),
                    BorderThickness = new Wpf.Thickness(monitor.Primary ? 2 : 1)
                };
                Controls.TextBlock number = Text((index + 1).ToString(), 10.5,
                    monitor.Primary ? Brush("AccentBrush") : Brush("MutedBrush"), Wpf.FontWeights.SemiBold);
                number.HorizontalAlignment = Wpf.HorizontalAlignment.Center;
                number.VerticalAlignment = Wpf.VerticalAlignment.Center;
                display.Child = number;
                Controls.Canvas.SetLeft(display, offsetX + (monitor.X - minX) * scale);
                Controls.Canvas.SetTop(display, offsetY + (monitor.Y - minY) * scale);
                canvas.Children.Add(display);
            }
        }

        List<MonitorSnapshot> disabled = profile.Display.Monitors.Where(monitor => !monitor.Enabled).ToList();
        for (int index = 0; index < disabled.Count; index++)
        {
            Shapes.Rectangle outline = new()
            {
                Width = 38,
                Height = 22,
                RadiusX = 3,
                RadiusY = 3,
                Stroke = Brush("FaintBrush"),
                StrokeThickness = 1,
                StrokeDashArray = new Media.DoubleCollection { 3, 2 },
                Opacity = 0.45
            };
            Controls.Canvas.SetLeft(outline, 12 + index * 46);
            Controls.Canvas.SetTop(outline, 110);
            canvas.Children.Add(outline);
        }

        if (profile.Display.Monitors.Count == 0)
        {
            Controls.TextBlock empty = Text("No display snapshot", 12, Brush("MutedBrush"));
            Controls.Canvas.SetLeft(empty, 57);
            Controls.Canvas.SetTop(empty, 58);
            canvas.Children.Add(empty);
        }
        return canvas;
    }

    private Controls.Border CreateDetailRow(Fluent.SymbolRegular symbol, Wpf.UIElement primary, string secondary)
    {
        Media.SolidColorBrush hoverBrush = NewBrush("#00171A18");
        Controls.Border row = new()
        {
            Height = 42,
            CornerRadius = new Wpf.CornerRadius(5),
            Background = hoverBrush,
            Padding = new Wpf.Thickness(5, 2, 4, 2)
        };
        Controls.Grid layout = new();
        layout.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(34) });
        layout.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(18) });
        Controls.Border iconBox = new()
        {
            Width = 27,
            Height = 27,
            CornerRadius = new Wpf.CornerRadius(5),
            Background = Brush("SurfaceRaisedBrush"),
            VerticalAlignment = Wpf.VerticalAlignment.Center
        };
        iconBox.Child = new Fluent.SymbolIcon
        {
            Symbol = symbol,
            FontSize = 14,
            Foreground = Brush("MutedBrush"),
            HorizontalAlignment = Wpf.HorizontalAlignment.Center,
            VerticalAlignment = Wpf.VerticalAlignment.Center
        };
        layout.Children.Add(iconBox);
        Controls.StackPanel copy = new() { VerticalAlignment = Wpf.VerticalAlignment.Center };
        if (primary is Controls.TextBlock primaryText)
        {
            primaryText.TextTrimming = Wpf.TextTrimming.CharacterEllipsis;
            primaryText.TextWrapping = Wpf.TextWrapping.NoWrap;
            primaryText.ToolTip = primaryText.Text;
        }
        copy.Children.Add(primary);
        Controls.TextBlock detail = Text(secondary, 11, Brush("MutedBrush"));
        detail.TextTrimming = Wpf.TextTrimming.CharacterEllipsis;
        detail.Margin = new Wpf.Thickness(0, 1, 0, 0);
        copy.Children.Add(detail);
        Controls.Grid.SetColumn(copy, 1);
        layout.Children.Add(copy);
        Fluent.SymbolIcon chevron = new()
        {
            Symbol = Fluent.SymbolRegular.ChevronRight24,
            FontSize = 13,
            Foreground = Brush("MutedBrush"),
            Opacity = 0,
            HorizontalAlignment = Wpf.HorizontalAlignment.Center,
            VerticalAlignment = Wpf.VerticalAlignment.Center
        };
        Controls.Grid.SetColumn(chevron, 2);
        layout.Children.Add(chevron);
        row.Child = layout;
        row.MouseEnter += (_, _) =>
        {
            hoverBrush.BeginAnimation(Media.SolidColorBrush.ColorProperty,
                new Animation.ColorAnimation(Media.Color.FromArgb(48, 58, 65, 59), TimeSpan.FromMilliseconds(120)));
            chevron.BeginAnimation(OpacityProperty, DoubleAnimationTo(1, 130));
        };
        row.MouseLeave += (_, _) =>
        {
            hoverBrush.BeginAnimation(Media.SolidColorBrush.ColorProperty,
                new Animation.ColorAnimation(Media.Colors.Transparent, TimeSpan.FromMilliseconds(140)));
            chevron.BeginAnimation(OpacityProperty, DoubleAnimationTo(0, 120));
        };
        return row;
    }

    private Wpf.FrameworkElement CreateAppChips(Profile profile)
    {
        List<AppRule> launched = profile.Apps.Where(app => app.StartOnActivate).ToList();
        if (launched.Count == 0) return Text("No apps configured", 13, Brush("TextBrush"), Wpf.FontWeights.Medium);

        Controls.WrapPanel chips = new() { VerticalAlignment = Wpf.VerticalAlignment.Center };
        foreach (AppRule app in launched.Take(1))
        {
            Controls.Border chip = new()
            {
                CornerRadius = new Wpf.CornerRadius(4),
                Background = Brush("SurfaceRaisedBrush"),
                Padding = new Wpf.Thickness(6, 2, 6, 2),
                Margin = new Wpf.Thickness(0, 0, 5, 0),
                MaxWidth = 100
            };
            Controls.TextBlock label = Text(app.DisplayName, 11.5, Brush("TextBrush"), Wpf.FontWeights.Medium);
            label.TextTrimming = Wpf.TextTrimming.CharacterEllipsis;
            chip.Child = label;
            chips.Children.Add(chip);
        }
        if (launched.Count > 1)
        {
            Controls.Border more = new()
            {
                CornerRadius = new Wpf.CornerRadius(4),
                Background = Brush("AccentSoftBrush"),
                Padding = new Wpf.Thickness(6, 2, 6, 2)
            };
            more.Child = Text($"+{launched.Count - 1}", 11.5, Brush("AccentBrush"), Wpf.FontWeights.SemiBold);
            chips.Children.Add(more);
        }
        return chips;
    }

    private static void AddCardDetail(Controls.Grid grid, int row, Controls.Border detail)
    {
        Controls.Grid.SetRow(detail, row);
        grid.Children.Add(detail);
    }

    private Controls.Border CreateBadge(string label, string foregroundKey, string backgroundKey)
    {
        Controls.Border badge = new()
        {
            CornerRadius = new Wpf.CornerRadius(5),
            Background = Brush(backgroundKey),
            Padding = new Wpf.Thickness(8, 3, 8, 3),
            VerticalAlignment = Wpf.VerticalAlignment.Center
        };
        badge.Child = Text(label, 10.5, Brush(foregroundKey), Wpf.FontWeights.SemiBold);
        return badge;
    }

    private static string FormatLastUsed(DateTimeOffset? value)
    {
        if (value is null) return "Not used yet";
        DateTimeOffset local = value.Value.ToLocalTime();
        TimeSpan elapsed = DateTimeOffset.Now - local;
        if (elapsed < TimeSpan.FromMinutes(1)) return "Just now";
        if (elapsed < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)elapsed.TotalMinutes)} min ago";
        if (elapsed < TimeSpan.FromHours(24)) return $"{Math.Max(1, (int)elapsed.TotalHours)} hr ago";
        if (elapsed < TimeSpan.FromHours(48)) return $"Yesterday at {local:t}";
        if (elapsed < TimeSpan.FromDays(7)) return local.ToString("ddd 'at' t");
        return local.ToString("g");
    }

    private static string BuildSummary(Profile profile)
    {
        int displays = profile.Display.Monitors.Count(monitor => monitor.Enabled);
        string displayText = displays == 1 ? "1 display" : $"{displays} displays";
        string windowText = profile.Windows.Count == 1 ? "1 window" : $"{profile.Windows.Count} windows";
        return $"{displayText} \u00B7 {windowText}";
    }

    private static SetupKind EffectiveSetupKind(Profile profile)
    {
        if (profile.Kind != SetupKind.Auto) return profile.Kind;
        string name = profile.Name.ToLowerInvariant();
        string[] simTerms = ["sim", "rig", "race", "racing", "wheel", "cockpit"];
        return simTerms.Any(name.Contains) ? SetupKind.SimRacing : SetupKind.Desk;
    }

    private static RigDisplayVariant EffectiveRigDisplay(Profile profile)
    {
        if (profile.RigDisplay != RigDisplayVariant.Auto) return profile.RigDisplay;
        List<MonitorSnapshot> enabled = profile.Display.Monitors.Where(monitor => monitor.Enabled).ToList();
        if (enabled.Count >= 4) return RigDisplayVariant.QuadScreen;
        if (enabled.Count == 3) return RigDisplayVariant.TripleScreen;
        if (enabled.Count == 2) return RigDisplayVariant.DualScreen;
        if (enabled.Count == 0) return RigDisplayVariant.Auto;
        double aspect = enabled[0].Height == 0 ? 0 : enabled[0].Width / (double)enabled[0].Height;
        return aspect >= 2.15 ? RigDisplayVariant.Ultrawide : RigDisplayVariant.SingleScreen;
    }

    private static string GetSetupCategoryLabel(Profile profile) =>
        EffectiveSetupKind(profile) == SetupKind.SimRacing ? "Sim racing" : "Desk";

    private static string GetDisplayVariantLabel(Profile profile)
    {
        bool sim = EffectiveSetupKind(profile) == SetupKind.SimRacing;
        return EffectiveRigDisplay(profile) switch
        {
            RigDisplayVariant.SingleScreen => sim ? "Single screen" : "Single display",
            RigDisplayVariant.DualScreen => sim ? "Dual screen" : "Dual display",
            RigDisplayVariant.TripleScreen => sim ? "Triple screen" : "Triple display",
            RigDisplayVariant.QuadScreen => sim ? "Quad screen" : "Quad display",
            RigDisplayVariant.Ultrawide => "Ultrawide",
            RigDisplayVariant.Vr => "VR",
            _ => "Display setup"
        };
    }

    private void OpenProfile(Guid profileId)
    {
        _selectedProfileId = profileId;
        PopulateProfile();
        ShowHardwareTab();
        Navigate(AppPage.Profile);
    }

    private Profile? SelectedProfile() => _selectedProfileId.HasValue
        ? _coordinator.Document.Profiles.FirstOrDefault(profile => profile.Id == _selectedProfileId.Value)
        : null;

    private void PopulateProfile()
    {
        Profile? profile = SelectedProfile();
        if (profile is null)
        {
            Navigate(AppPage.Home);
            return;
        }

        bool active = _coordinator.Document.Runtime.ActiveProfileId == profile.Id;
        ProfileTitle.Text = profile.Name;
        ProfileMeta.Text = BuildSummary(profile);
        ProfileCurrentBadge.Visibility = active ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        ProfileSwitchButton.Content = active ? "Reapply setup" : "Switch to setup";
        CapturedValue.Text = profile.CapturedAtUtc.ToLocalTime().ToString("g");
        WindowCountValue.Text = profile.Windows.Count.ToString();
        PopulateSetupIdentityPickers(profile);
        PopulateAudioPickers(profile);
        BuildMonitorMap(profile);

        HotkeyInput.Text = profile.Hotkey;
        _hotkeyCommitted = profile.Hotkey;
        AppsPanel.Children.Clear();
        foreach (AppRule app in profile.Apps) AddAppEditor(app);
        GamesPanel.Children.Clear();
        foreach (string process in profile.GameProcesses) AddGameEditor(process);
        if (profile.Apps.Count == 0) AddListEmptyState(AppsPanel, "No applications added");
        if (profile.GameProcesses.Count == 0) AddListEmptyState(GamesPanel, "No game processes added");
    }

    private void PopulateSetupIdentityPickers(Profile profile)
    {
        List<SetupKindOption> kinds =
        [
            new(SetupKind.Auto, "Auto-detect from setup name"),
            new(SetupKind.Desk, "Desk"),
            new(SetupKind.SimRacing, "Sim racing")
        ];
        List<RigDisplayOption> variants = CreateRigDisplayOptions();
        SetupKindPicker.ItemsSource = kinds;
        SetupKindPicker.SelectedItem = kinds.First(option => option.Value == profile.Kind);
        RigDisplayPicker.ItemsSource = variants;
        RigDisplayPicker.SelectedItem = variants.First(option => option.Value == profile.RigDisplay);
        UpdateRigDisplayPickerState();
    }

    private static List<RigDisplayOption> CreateRigDisplayOptions() =>
    [
        new(RigDisplayVariant.Auto, "Auto-detect from captured displays"),
        new(RigDisplayVariant.SingleScreen, "Single screen"),
        new(RigDisplayVariant.DualScreen, "Dual screen"),
        new(RigDisplayVariant.TripleScreen, "Triple screen"),
        new(RigDisplayVariant.QuadScreen, "Quad screen"),
        new(RigDisplayVariant.Ultrawide, "Ultrawide"),
        new(RigDisplayVariant.Vr, "VR")
    ];

    private void SetupKindPicker_SelectionChanged(object sender, Controls.SelectionChangedEventArgs e) =>
        UpdateRigDisplayPickerState();

    private void UpdateRigDisplayPickerState()
    {
        RigDisplayPicker.IsEnabled = SetupKindPicker.SelectedItem is not SetupKindOption { Value: SetupKind.Desk };
        RigDisplayPicker.ToolTip = RigDisplayPicker.IsEnabled
            ? "Choose how the sim-racing displays are arranged"
            : "Display variants are used by sim-racing setups";
    }

    private void SaveSetupIdentity_Click(object sender, Wpf.RoutedEventArgs e)
    {
        Profile? profile = SelectedProfile();
        if (profile is null) return;
        SetupKind previousKind = profile.Kind;
        RigDisplayVariant previousVariant = profile.RigDisplay;
        try
        {
            if (SetupKindPicker.SelectedItem is SetupKindOption kind) profile.Kind = kind.Value;
            if (RigDisplayPicker.SelectedItem is RigDisplayOption variant) profile.RigDisplay = variant.Value;
            _coordinator.SaveProfile(profile);
            ShowToast("Setup identity saved",
                $"Shown as {GetSetupCategoryLabel(profile)}, {GetDisplayVariantLabel(profile).ToLowerInvariant()}.",
                "AccentBrush");
        }
        catch (Exception ex)
        {
            profile.Kind = previousKind;
            profile.RigDisplay = previousVariant;
            PopulateSetupIdentityPickers(profile);
            ShowError(ex.Message);
        }
    }

    private void PopulateAudioPickers(Profile profile)
    {
        List<AudioDeviceOption> render = _coordinator.ListAudioDevices(false);
        List<AudioDeviceOption> capture = _coordinator.ListAudioDevices(true);
        FillAudioPicker(PlaybackPicker, render, profile.Audio.Playback);
        FillAudioPicker(CommunicationsPicker, render, profile.Audio.Communications);
        FillAudioPicker(MicrophonePicker, capture, profile.Audio.Microphone);
    }

    private static void FillAudioPicker(Controls.ComboBox picker, List<AudioDeviceOption> devices, AudioEndpointSnapshot? saved)
    {
        List<AudioPickerOption> options = [new AudioPickerOption(null, string.Empty, "Not set (keep current device)")];
        bool savedFound = false;
        foreach (AudioDeviceOption device in devices)
        {
            options.Add(new AudioPickerOption(device.Id, device.Name, device.Name));
            if (saved is not null && string.Equals(device.Id, saved.DeviceId, StringComparison.OrdinalIgnoreCase)) savedFound = true;
        }

        if (saved is not null && !string.IsNullOrWhiteSpace(saved.DeviceId) && !savedFound)
        {
            options.Insert(1, new AudioPickerOption(saved.DeviceId, saved.FriendlyName, saved.FriendlyName + " (unavailable)"));
        }

        picker.ItemsSource = options;
        string? targetId = string.IsNullOrWhiteSpace(saved?.DeviceId) ? null : saved!.DeviceId;
        picker.SelectedItem = options.FirstOrDefault(option => targetId is null
            ? option.Id is null
            : string.Equals(option.Id, targetId, StringComparison.OrdinalIgnoreCase)) ?? options[0];
    }

    private void SaveAudio_Click(object sender, Wpf.RoutedEventArgs e)
    {
        Profile? profile = SelectedProfile();
        if (profile is null) return;
        AudioSnapshot previousAudio = profile.Audio;
        AudioSnapshot selectedAudio = new()
        {
            Playback = SelectedEndpoint(PlaybackPicker),
            Communications = SelectedEndpoint(CommunicationsPicker),
            Microphone = SelectedEndpoint(MicrophonePicker)
        };
        try
        {
            profile.Audio = selectedAudio;
            _coordinator.SaveProfile(profile);
            ShowToast("Sound devices saved", "They apply the next time this setup is activated.", "AccentBrush");
        }
        catch (Exception ex)
        {
            profile.Audio = previousAudio;
            PopulateAudioPickers(profile);
            ShowError(ex.Message);
        }
    }

    private static AudioEndpointSnapshot? SelectedEndpoint(Controls.ComboBox picker) =>
        picker.SelectedItem is AudioPickerOption { Id: not null } option
            ? new AudioEndpointSnapshot { DeviceId = option.Id, FriendlyName = option.Name }
            : null;

    private void BuildMonitorMap(Profile profile)
    {
        MonitorCanvas.Children.Clear();
        MonitorListPanel.Children.Clear();
        List<MonitorSnapshot> enabled = profile.Display.Monitors.Where(monitor => monitor.Enabled).ToList();
        DisplayCount.Text = enabled.Count == 1 ? "1 active" : $"{enabled.Count} active";

        if (enabled.Count > 0)
        {
            double minX = enabled.Min(monitor => monitor.X);
            double minY = enabled.Min(monitor => monitor.Y);
            double maxX = enabled.Max(monitor => monitor.X + monitor.Width);
            double maxY = enabled.Max(monitor => monitor.Y + monitor.Height);
            double worldWidth = Math.Max(1, maxX - minX);
            double worldHeight = Math.Max(1, maxY - minY);
            double scale = Math.Min(620 / worldWidth, 132 / worldHeight);
            double contentWidth = worldWidth * scale;
            double contentHeight = worldHeight * scale;
            double offsetX = (680 - contentWidth) / 2;
            double offsetY = (170 - contentHeight) / 2;

            foreach (MonitorSnapshot monitor in enabled)
            {
                double width = Math.Max(72, monitor.Width * scale);
                double height = Math.Max(46, monitor.Height * scale);
                Controls.Border display = new()
                {
                    Width = width,
                    Height = height,
                    CornerRadius = new Wpf.CornerRadius(5),
                    Background = monitor.Primary ? Brush("AccentSoftBrush") : Brush("ChromeBrush"),
                    BorderBrush = monitor.Primary ? Brush("AccentBrush") : Brush("BorderBrush"),
                    BorderThickness = new Wpf.Thickness(monitor.Primary ? 2 : 1),
                    Padding = new Wpf.Thickness(10, 7, 10, 7)
                };
                Controls.StackPanel labels = new() { VerticalAlignment = Wpf.VerticalAlignment.Center };
                Controls.TextBlock monitorName = Text(monitor.FriendlyName, 12, Brush("TextBrush"), Wpf.FontWeights.SemiBold);
                monitorName.TextTrimming = Wpf.TextTrimming.CharacterEllipsis;
                labels.Children.Add(monitorName);
                labels.Children.Add(Text($"{monitor.Width} x {monitor.Height}", 10, Brush("MutedBrush")));
                display.Child = labels;
                Controls.Canvas.SetLeft(display, offsetX + (monitor.X - minX) * scale);
                Controls.Canvas.SetTop(display, offsetY + (monitor.Y - minY) * scale);
                MonitorCanvas.Children.Add(display);
            }
        }
        else
        {
            Controls.TextBlock empty = Text("No active displays captured", 13, Brush("MutedBrush"));
            Controls.Canvas.SetLeft(empty, 244);
            Controls.Canvas.SetTop(empty, 76);
            MonitorCanvas.Children.Add(empty);
        }

        foreach (MonitorSnapshot monitor in profile.Display.Monitors
                     .OrderByDescending(item => item.Enabled)
                     .ThenBy(item => item.X))
        {
            Controls.Grid row = new() { Height = 36 };
            row.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = Wpf.GridLength.Auto });
            row.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
            row.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = Wpf.GridLength.Auto });
            Shapes.Ellipse dot = new()
            {
                Width = 7,
                Height = 7,
                Fill = monitor.Enabled ? Brush("AccentBrush") : Brush("FaintBrush"),
                Margin = new Wpf.Thickness(1, 0, 10, 0),
                VerticalAlignment = Wpf.VerticalAlignment.Center
            };
            row.Children.Add(dot);
            Controls.TextBlock name = Text(monitor.FriendlyName + (monitor.Primary ? " (primary)" : ""), 12, Brush("TextBrush"));
            name.VerticalAlignment = Wpf.VerticalAlignment.Center;
            Controls.Grid.SetColumn(name, 1);
            row.Children.Add(name);
            string detail = monitor.Enabled
                ? $"{monitor.Width} x {monitor.Height}  {monitor.RefreshHz:0.#} Hz"
                : "Off";
            Controls.TextBlock state = Text(detail, 11, Brush("MutedBrush"));
            state.VerticalAlignment = Wpf.VerticalAlignment.Center;
            Controls.Grid.SetColumn(state, 2);
            row.Children.Add(state);
            MonitorListPanel.Children.Add(row);
        }
    }

    private void AddAppEditor(AppRule rule)
    {
        RemoveEmptyState(AppsPanel);
        Controls.TextBox path = CreateTextInput(rule.ExecutablePath);
        Controls.TextBox arguments = CreateTextInput(rule.Arguments);
        Controls.CheckBox start = Toggle("Start", rule.StartOnActivate);
        Controls.CheckBox close = Toggle("Close on leave", rule.CloseOnDeactivate);
        Controls.CheckBox force = Toggle("Force close", rule.ForceClose);
        Controls.CheckBox hidden = Toggle("Start hidden", rule.StartHidden);
        AppEditorState state = new(path, arguments, start, close, force, hidden);

        Controls.Border row = new()
        {
            Background = Brush("SurfaceBrush"),
            BorderBrush = Brush("BorderSoftBrush"),
            BorderThickness = new Wpf.Thickness(1),
            CornerRadius = new Wpf.CornerRadius(7),
            Padding = new Wpf.Thickness(14),
            Margin = new Wpf.Thickness(0, 0, 0, 8),
            Tag = state
        };
        Controls.Grid layout = new();
        layout.RowDefinitions.Add(new Controls.RowDefinition { Height = Wpf.GridLength.Auto });
        layout.RowDefinitions.Add(new Controls.RowDefinition { Height = Wpf.GridLength.Auto });
        layout.RowDefinitions.Add(new Controls.RowDefinition { Height = Wpf.GridLength.Auto });
        layout.RowDefinitions.Add(new Controls.RowDefinition { Height = Wpf.GridLength.Auto });

        Controls.Grid header = new();
        header.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        header.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = Wpf.GridLength.Auto });
        string displayName = string.IsNullOrWhiteSpace(rule.ExecutablePath) ? "New application" : rule.DisplayName;
        header.Children.Add(Text(displayName, 14, Brush("TextBrush"), Wpf.FontWeights.SemiBold));
        Controls.Button remove = IconButton(Fluent.SymbolRegular.Delete24, "Remove application");
        remove.Width = 32;
        remove.Height = 28;
        remove.Click += (_, _) =>
        {
            AppsPanel.Children.Remove(row);
            if (AppsPanel.Children.Count == 0) AddListEmptyState(AppsPanel, "No applications added");
        };
        Controls.Grid.SetColumn(remove, 1);
        header.Children.Add(remove);
        layout.Children.Add(header);

        path.Margin = new Wpf.Thickness(0, 10, 0, 7);
        Controls.Grid.SetRow(path, 1);
        layout.Children.Add(path);
        arguments.Margin = new Wpf.Thickness(0, 0, 0, 12);
        arguments.ToolTip = "Command-line arguments";
        Controls.Grid.SetRow(arguments, 2);
        layout.Children.Add(arguments);

        Controls.WrapPanel toggles = new();
        foreach (Controls.CheckBox toggle in new[] { start, close, force, hidden })
        {
            toggle.Margin = new Wpf.Thickness(0, 0, 22, 0);
            toggles.Children.Add(toggle);
        }
        Controls.Grid.SetRow(toggles, 3);
        layout.Children.Add(toggles);
        row.Child = layout;
        AppsPanel.Children.Add(row);
    }

    private void HotkeyInput_PreviewKeyDown(object sender, Input.KeyEventArgs e)
    {
        Input.Key key = e.Key == Input.Key.System ? e.SystemKey : e.Key;
        if (key == Input.Key.Escape) return;
        if (key == Input.Key.Tab && Input.Keyboard.Modifiers == Input.ModifierKeys.None) return;
        e.Handled = true;

        if (key is Input.Key.Back or Input.Key.Delete)
        {
            _hotkeyCommitted = string.Empty;
            HotkeyInput.Text = string.Empty;
            return;
        }

        string prefix = ModifierPrefix(Input.Keyboard.Modifiers);
        if (IsModifierKey(key))
        {
            HotkeyInput.Text = prefix + "...";
            return;
        }

        int virtualKey = Input.KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey <= 0) return;
        Keys keyCode = (Keys)virtualKey & Keys.KeyCode;
        if (keyCode == Keys.None) return;

        bool isFunctionKey = keyCode is >= Keys.F1 and <= Keys.F24;
        Input.ModifierKeys primaryModifiers = Input.ModifierKeys.Control | Input.ModifierKeys.Alt | Input.ModifierKeys.Windows;
        if (!isFunctionKey && (Input.Keyboard.Modifiers & primaryModifiers) == Input.ModifierKeys.None)
        {
            HotkeyInput.Text = _hotkeyCommitted;
            ShowToast("Add a modifier key", "Hold Ctrl, Alt, or Win with that key, or use a function key on its own.", "WarningBrush");
            return;
        }

        string gesture = prefix + keyCode;
        if (!HotkeyParser.TryParse(gesture, out _, out string error))
        {
            HotkeyInput.Text = _hotkeyCommitted;
            ShowToast("Hotkey not recorded", error, "WarningBrush");
            return;
        }

        _hotkeyCommitted = gesture;
        HotkeyInput.Text = gesture;
    }

    private void HotkeyInput_PreviewKeyUp(object sender, Input.KeyEventArgs e) => RevertPartialHotkey();

    private void HotkeyInput_LostFocus(object sender, Input.KeyboardFocusChangedEventArgs e) => RevertPartialHotkey();

    private void RevertPartialHotkey()
    {
        if (HotkeyInput.Text.EndsWith("...", StringComparison.Ordinal)) HotkeyInput.Text = _hotkeyCommitted;
    }

    private static bool IsModifierKey(Input.Key key) => key is Input.Key.LeftCtrl or Input.Key.RightCtrl
        or Input.Key.LeftAlt or Input.Key.RightAlt or Input.Key.LeftShift or Input.Key.RightShift
        or Input.Key.LWin or Input.Key.RWin;

    private static string ModifierPrefix(Input.ModifierKeys modifiers)
    {
        string prefix = string.Empty;
        if (modifiers.HasFlag(Input.ModifierKeys.Control)) prefix += "Ctrl+";
        if (modifiers.HasFlag(Input.ModifierKeys.Alt)) prefix += "Alt+";
        if (modifiers.HasFlag(Input.ModifierKeys.Shift)) prefix += "Shift+";
        if (modifiers.HasFlag(Input.ModifierKeys.Windows)) prefix += "Win+";
        return prefix;
    }

    private void AddGameEditor(string process)
    {
        RemoveEmptyState(GamesPanel);
        Controls.ComboBox input = new()
        {
            Style = GetStyle("Picker"),
            IsEditable = true,
            ItemsSource = GameDetectionService.RunningProcessCandidates(),
            Text = process,
            ToolTip = "Pick a running app from the list or type a process name, for example acs.exe"
        };
        GameEditorState state = new(input);
        Controls.Border row = new()
        {
            Background = Brush("SurfaceBrush"),
            BorderBrush = Brush("BorderSoftBrush"),
            BorderThickness = new Wpf.Thickness(1),
            CornerRadius = new Wpf.CornerRadius(7),
            Padding = new Wpf.Thickness(12),
            Margin = new Wpf.Thickness(0, 0, 0, 8),
            Tag = state
        };
        Controls.Grid layout = new();
        layout.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = Wpf.GridLength.Auto });
        layout.Children.Add(input);
        Controls.Button remove = IconButton(Fluent.SymbolRegular.Delete24, "Remove process");
        remove.Margin = new Wpf.Thickness(8, 1, 0, 1);
        remove.Click += (_, _) =>
        {
            GamesPanel.Children.Remove(row);
            if (GamesPanel.Children.Count == 0) AddListEmptyState(GamesPanel, "No game processes added");
        };
        Controls.Grid.SetColumn(remove, 1);
        layout.Children.Add(remove);
        row.Child = layout;
        GamesPanel.Children.Add(row);
    }

    private void AddListEmptyState(Controls.StackPanel panel, string text)
    {
        Controls.Border empty = new()
        {
            CornerRadius = new Wpf.CornerRadius(7),
            BorderBrush = Brush("BorderSoftBrush"),
            BorderThickness = new Wpf.Thickness(1),
            Padding = new Wpf.Thickness(16, 14, 16, 14),
            Tag = "empty"
        };
        empty.Child = Text(text, 12, Brush("MutedBrush"));
        panel.Children.Add(empty);
    }

    private static void RemoveEmptyState(Controls.StackPanel panel)
    {
        foreach (Controls.Border empty in panel.Children.OfType<Controls.Border>()
                     .Where(item => Equals(item.Tag, "empty")).ToList())
        {
            panel.Children.Remove(empty);
        }
    }

    private void RefreshSettings()
    {
        AppSettings settings = _coordinator.Document.Settings;
        StartupToggle.IsChecked = settings.LaunchOnStartup;
        ChooserToggle.IsChecked = !settings.StartMinimized;
        ConfirmSwitchToggle.IsChecked = settings.ConfirmBeforeSwitch;
        DetectionToggle.IsChecked = settings.GameDetectionEnabled;
        _pollSeconds = Math.Clamp(settings.GamePollSeconds, 1, 30);
        PollValue.Text = _pollSeconds.ToString();
        RefreshReliableStartupStatus();
    }

    private void RefreshReliableStartupStatus()
    {
        StartupTaskStatus status = StartupTaskRegistration.GetStatus();
        if (status.IsReady)
        {
            ReliableStartupActionText.Text = "Remove";
            ReliableStartupStatus.Text = _coordinator.Document.Settings.LaunchOnStartup
                ? status.Detail
                : "Installed but paused while Start with Windows is off.";
            ReliableStartupStatus.Foreground = Brush("AccentBrush");
        }
        else if (status.Exists)
        {
            ReliableStartupActionText.Text = "Repair";
            ReliableStartupStatus.Text = status.Detail;
            ReliableStartupStatus.Foreground = Brush("WarningBrush");
        }
        else
        {
            ReliableStartupActionText.Text = "Install";
            ReliableStartupStatus.Text = status.Detail;
            ReliableStartupStatus.Foreground = Brush("FaintBrush");
        }
    }

    private void RefreshNavigationState()
    {
        bool settings = _page == AppPage.Settings;
        SetupsNav.Foreground = settings ? Brush("MutedBrush") : Brush("AccentBrush");
        SettingsNav.Foreground = settings ? Brush("AccentBrush") : Brush("MutedBrush");
        AnimateNavigationIndicator(settings ? 46 : 0);
    }

    private void AnimateNavigationIndicator(double targetY)
    {
        Media.TranslateTransform background = EnsureTranslate(NavActiveBackground);
        Media.TranslateTransform pill = EnsureTranslate(NavActivePill);
        if (!_navIndicatorInitialized || !IsLoaded)
        {
            background.BeginAnimation(Media.TranslateTransform.YProperty, null);
            pill.BeginAnimation(Media.TranslateTransform.YProperty, null);
            background.Y = targetY;
            pill.Y = targetY;
            _navIndicatorInitialized = true;
            return;
        }

        double direction = Math.Sign(targetY - background.Y);
        Animation.DoubleAnimationUsingKeyFrames animation = new()
        {
            Duration = TimeSpan.FromMilliseconds(360)
        };
        animation.KeyFrames.Add(new Animation.EasingDoubleKeyFrame(
            targetY + direction * 3,
            Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(285)),
            new Animation.QuinticEase { EasingMode = Animation.EasingMode.EaseOut }));
        animation.KeyFrames.Add(new Animation.EasingDoubleKeyFrame(
            targetY,
            Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(360)),
            new Animation.QuadraticEase { EasingMode = Animation.EasingMode.EaseOut }));
        background.BeginAnimation(Media.TranslateTransform.YProperty, animation);
        pill.BeginAnimation(Media.TranslateTransform.YProperty, animation.Clone());
    }

    private void Navigate(AppPage target)
    {
        if (_page == target) return;
        Wpf.FrameworkElement outgoing = CurrentPageElement();
        _page = target;
        RefreshNavigationState();
        Wpf.FrameworkElement incoming = CurrentPageElement();
        incoming.Visibility = Wpf.Visibility.Visible;
        incoming.Opacity = 0;
        Media.TranslateTransform incomingTransform = EnsureTranslate(incoming);
        Media.TranslateTransform outgoingTransform = EnsureTranslate(outgoing);
        incomingTransform.Y = 12;

        Animation.DoubleAnimation fadeOut = StrongDoubleAnimationTo(0, 180);
        fadeOut.Completed += (_, _) => outgoing.Visibility = Wpf.Visibility.Collapsed;
        outgoing.BeginAnimation(OpacityProperty, fadeOut);
        outgoingTransform.BeginAnimation(Media.TranslateTransform.YProperty, StrongDoubleAnimationTo(-6, 190));
        incoming.BeginAnimation(OpacityProperty, StrongDoubleAnimationTo(1, 300));
        incomingTransform.BeginAnimation(Media.TranslateTransform.YProperty, StrongDoubleAnimationTo(0, 320));
    }

    private Wpf.FrameworkElement CurrentPageElement() => _page switch
    {
        AppPage.Profile => ProfilePage,
        AppPage.Settings => SettingsPage,
        _ => HomePage
    };

    private void ShowHardwareTab()
    {
        SwapProfilePanel(AutomationPanel, SnapshotPanel);
        SnapshotIndicator.Visibility = Wpf.Visibility.Visible;
        AutomationIndicator.Visibility = Wpf.Visibility.Collapsed;
        SnapshotTab.Foreground = Brush("TextBrush");
        AutomationTab.Foreground = Brush("MutedBrush");
    }

    private void ShowAutomationTab()
    {
        SwapProfilePanel(SnapshotPanel, AutomationPanel);
        SnapshotIndicator.Visibility = Wpf.Visibility.Collapsed;
        AutomationIndicator.Visibility = Wpf.Visibility.Visible;
        SnapshotTab.Foreground = Brush("MutedBrush");
        AutomationTab.Foreground = Brush("TextBrush");
    }

    private static void SwapProfilePanel(Wpf.FrameworkElement outgoing, Wpf.FrameworkElement incoming)
    {
        if (incoming.Visibility == Wpf.Visibility.Visible) return;
        incoming.Visibility = Wpf.Visibility.Visible;
        incoming.Opacity = 0;
        Media.TranslateTransform incomingTransform = EnsureTranslate(incoming);
        Media.TranslateTransform outgoingTransform = EnsureTranslate(outgoing);
        incomingTransform.Y = 9;
        Animation.DoubleAnimation fadeOut = StrongDoubleAnimationTo(0, 160);
        fadeOut.Completed += (_, _) => outgoing.Visibility = Wpf.Visibility.Collapsed;
        outgoing.BeginAnimation(OpacityProperty, fadeOut);
        outgoingTransform.BeginAnimation(Media.TranslateTransform.YProperty, StrongDoubleAnimationTo(-5, 180));
        incoming.BeginAnimation(OpacityProperty, StrongDoubleAnimationTo(1, 300));
        incomingTransform.BeginAnimation(Media.TranslateTransform.YProperty, StrongDoubleAnimationTo(0, 310));
    }

    private void SetBusy(bool busy, string message)
    {
        int version = ++_busyVersion;
        HomePage.IsHitTestVisible = !busy;
        ProfilePage.IsHitTestVisible = !busy;
        SettingsPage.IsHitTestVisible = !busy;
        if (busy)
        {
            _ = ShowBusyAfterDelayAsync(version, message);
            return;
        }

        Animation.DoubleAnimation fade = DoubleAnimationTo(0, 140);
        fade.Completed += (_, _) =>
        {
            if (version == _busyVersion) BusyLayer.Visibility = Wpf.Visibility.Collapsed;
        };
        BusyLayer.BeginAnimation(OpacityProperty, fade);
        BusySpinnerRotation.BeginAnimation(Media.RotateTransform.AngleProperty, null);
    }

    private async Task ShowBusyAfterDelayAsync(int version, string message)
    {
        await Task.Delay(140);
        if (version != _busyVersion) return;
        await Dispatcher.InvokeAsync(() =>
        {
            if (version != _busyVersion) return;
            BusyMessage.Text = message;
            BusyLayer.Visibility = Wpf.Visibility.Visible;
            BusyLayer.BeginAnimation(OpacityProperty, DoubleAnimationTo(1, 160));
            Animation.DoubleAnimation spin = new(0, 360, TimeSpan.FromMilliseconds(900))
            {
                RepeatBehavior = Animation.RepeatBehavior.Forever
            };
            BusySpinnerRotation.BeginAnimation(Media.RotateTransform.AngleProperty, spin);
        });
    }

    private void ShowReport(OperationReport report)
    {
        OperationMessage? important = report.Messages.FirstOrDefault(message => message.Severity == OperationSeverity.Error)
                                      ?? report.Messages.FirstOrDefault(message => message.Severity == OperationSeverity.Warning);
        string detail = important?.Message ?? $"Completed in {report.Duration.TotalSeconds:0.0}s";
        string brush = report.HasErrors ? "ErrorBrush" : report.HasWarnings ? "WarningBrush" : "AccentBrush";
        ShowToast(report.Summary, detail, brush);
        RefreshAll();
    }

    private void ShowError(string message) => ShowToast("Could not complete that", message, "ErrorBrush");

    private async void ShowToast(string title, string detail, string markerBrush)
    {
        int version = ++_toastVersion;
        ToastTitle.Text = title;
        ToastDetail.Text = detail;
        ToastMarker.Background = Brush(markerBrush);
        ToastPanel.Visibility = Wpf.Visibility.Visible;
        ToastPanel.BeginAnimation(OpacityProperty, DoubleAnimationTo(1, 170));
        if (ToastPanel.RenderTransform is Media.TranslateTransform transform)
        {
            transform.BeginAnimation(Media.TranslateTransform.YProperty, DoubleAnimationTo(0, 190));
        }

        await Task.Delay(3800);
        if (version != _toastVersion) return;
        Animation.DoubleAnimation fade = DoubleAnimationTo(0, 180);
        fade.Completed += (_, _) =>
        {
            if (version == _toastVersion) ToastPanel.Visibility = Wpf.Visibility.Collapsed;
        };
        ToastPanel.BeginAnimation(OpacityProperty, fade);
        if (ToastPanel.RenderTransform is Media.TranslateTransform hideTransform)
        {
            hideTransform.BeginAnimation(Media.TranslateTransform.YProperty, DoubleAnimationTo(12, 180));
        }
    }

    private Task<string?> ShowTextDialogAsync(
        string title,
        string body,
        string value,
        string confirmText,
        bool selectAll = true)
    {
        CloseAnyDialog();
        _dialogMode = DialogMode.Text;
        _textDialog = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        DialogTitle.Text = title;
        DialogBody.Text = body;
        DialogInput.Text = value;
        DialogInput.IsEnabled = true;
        DialogInput.Visibility = Wpf.Visibility.Visible;
        DialogInputLabel.Visibility = Wpf.Visibility.Visible;
        DialogInputHint.Visibility = Wpf.Visibility.Visible;
        DialogError.Visibility = Wpf.Visibility.Collapsed;
        DialogConfirm.Content = confirmText;
        DialogConfirm.IsEnabled = true;
        DialogConfirm.Style = GetStyle("PrimaryButton");
        ShowDialogLayer();
        Dispatcher.BeginInvoke(() =>
        {
            DialogInput.Focus();
            if (selectAll) DialogInput.SelectAll();
            else DialogInput.CaretIndex = DialogInput.Text.Length;
        });
        return _textDialog.Task;
    }

    private Task<bool> ShowConfirmDialogAsync(string title, string body, string confirmText, bool danger)
    {
        CloseAnyDialog();
        _dialogMode = DialogMode.Confirm;
        _confirmDialog = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        DialogTitle.Text = title;
        DialogBody.Text = body;
        DialogInput.Visibility = Wpf.Visibility.Collapsed;
        DialogInputLabel.Visibility = Wpf.Visibility.Collapsed;
        DialogInputHint.Visibility = Wpf.Visibility.Collapsed;
        DialogError.Visibility = Wpf.Visibility.Collapsed;
        DialogConfirm.Content = confirmText;
        DialogConfirm.IsEnabled = true;
        DialogConfirm.Style = GetStyle(danger ? "DangerButton" : "PrimaryButton");
        ShowDialogLayer();
        DialogConfirm.Focus();
        return _confirmDialog.Task;
    }

    private void ShowDialogLayer()
    {
        DialogLayer.Visibility = Wpf.Visibility.Visible;
        DialogLayer.BeginAnimation(OpacityProperty, DoubleAnimationTo(1, 150));
    }

    private void CompleteDialog(bool confirmed)
    {
        if (_dialogMode == DialogMode.SetupGuide)
        {
            CompleteSetupGuide(null);
            return;
        }
        if (_dialogMode == DialogMode.Text)
        {
            if (confirmed)
            {
                string value = DialogInput.Text.Trim();
                if (value.Length == 0)
                {
                    DialogError.Text = "Enter a setup name before capturing.";
                    DialogError.Visibility = Wpf.Visibility.Visible;
                    DialogInput.Focus();
                    return;
                }
                DialogConfirm.IsEnabled = false;
                DialogInput.IsEnabled = false;
                _textDialog?.TrySetResult(value);
            }
            else
            {
                _textDialog?.TrySetResult(null);
            }
        }
        else if (_dialogMode == DialogMode.Confirm)
        {
            if (confirmed) DialogConfirm.IsEnabled = false;
            _confirmDialog?.TrySetResult(confirmed);
        }

        _dialogMode = DialogMode.None;
        Animation.DoubleAnimation fade = DoubleAnimationTo(0, 120);
        fade.Completed += (_, _) => DialogLayer.Visibility = Wpf.Visibility.Collapsed;
        DialogLayer.BeginAnimation(OpacityProperty, fade);
    }

    private void CloseAnyDialog()
    {
        if (_dialogMode == DialogMode.Text) _textDialog?.TrySetResult(null);
        if (_dialogMode == DialogMode.Confirm) _confirmDialog?.TrySetResult(false);
        if (_dialogMode == DialogMode.SetupGuide) _setupGuideDialog?.TrySetResult(null);
        _dialogMode = DialogMode.None;
        DialogLayer.Visibility = Wpf.Visibility.Collapsed;
        SetupGuideLayer.Visibility = Wpf.Visibility.Collapsed;
    }

    private async void Capture_Click(object sender, Wpf.RoutedEventArgs e) => await PromptCaptureAsync();

    private async void RestoreDisplays_Click(object sender, Wpf.RoutedEventArgs e) => await RestoreAllDisplaysAsync();

    private void Settings_Click(object sender, Wpf.RoutedEventArgs e)
    {
        RefreshSettings();
        Navigate(AppPage.Settings);
    }

    private void Back_Click(object sender, Wpf.RoutedEventArgs e) => Navigate(AppPage.Home);

    private async void ProfileSwitch_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (SelectedProfile() is Profile profile) await ActivateProfileAsync(profile.Id, ActivationSource.User);
    }

    private void SnapshotTab_Click(object sender, Wpf.RoutedEventArgs e) => ShowHardwareTab();
    private void AutomationTab_Click(object sender, Wpf.RoutedEventArgs e) => ShowAutomationTab();

    private async void Rename_Click(object sender, Wpf.RoutedEventArgs e)
    {
        Profile? profile = SelectedProfile();
        if (profile is null) return;
        string? name = await ShowTextDialogAsync("Rename setup", "Choose a clear name for this profile.", profile.Name, "Rename");
        if (name is null) return;
        try
        {
            _coordinator.Rename(profile.Id, name);
            ShowToast("Setup renamed", name, "AccentBrush");
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void Delete_Click(object sender, Wpf.RoutedEventArgs e)
    {
        Profile? profile = SelectedProfile();
        if (profile is null) return;
        bool confirmed = await ShowConfirmDialogAsync(
            "Delete " + profile.Name + "?",
            "The captured hardware and window state will be removed from PitLaunch.",
            "Delete setup",
            true);
        if (!confirmed) return;
        try
        {
            _coordinator.Delete(profile.Id);
            _selectedProfileId = null;
            Navigate(AppPage.Home);
            ShowToast("Setup deleted", profile.Name, "WarningBrush");
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void Recapture_Click(object sender, Wpf.RoutedEventArgs e)
    {
        Profile? profile = SelectedProfile();
        if (profile is null) return;
        bool confirmed = await ShowConfirmDialogAsync(
            "Recapture " + profile.Name + "?",
            "Replace its saved displays, sound devices, and window positions with the current state.",
            "Recapture",
            false);
        if (!confirmed) return;
        OperationReport report = await _coordinator.RecaptureAsync(profile.Id);
        ShowReport(report);
    }

    private void AddApplication_Click(object sender, Wpf.RoutedEventArgs e)
    {
        using Forms.OpenFileDialog dialog = new()
        {
            Title = "Choose an application",
            Filter = "Applications (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(_owner) != Forms.DialogResult.OK) return;
        AddAppEditor(new AppRule { ExecutablePath = dialog.FileName });
    }

    private void AddGameProcess_Click(object sender, Wpf.RoutedEventArgs e)
    {
        AddGameEditor("");
        if (GamesPanel.Children[^1] is Controls.Border { Tag: GameEditorState state }) state.Process.Focus();
    }

    private void SaveAutomation_Click(object sender, Wpf.RoutedEventArgs e)
    {
        Profile? profile = SelectedProfile();
        if (profile is null) return;
        string hotkey = HotkeyInput.Text.Trim();
        if (!string.IsNullOrWhiteSpace(hotkey) && !HotkeyParser.TryParse(hotkey, out _, out string error))
        {
            ShowError(error);
            HotkeyInput.Focus();
            return;
        }

        List<AppRule> apps = AppsPanel.Children.OfType<Controls.Border>()
            .Select(row => row.Tag as AppEditorState)
            .Where(state => state is not null && !string.IsNullOrWhiteSpace(state.Path.Text))
            .Select(state => new AppRule
            {
                ExecutablePath = state!.Path.Text.Trim(),
                Arguments = state.Arguments.Text,
                StartOnActivate = state.Start.IsChecked == true,
                CloseOnDeactivate = state.Close.IsChecked == true,
                ForceClose = state.Force.IsChecked == true,
                StartHidden = state.Hidden.IsChecked == true
            })
            .ToList();
        List<string> gameProcesses = GamesPanel.Children.OfType<Controls.Border>()
            .Select(row => row.Tag as GameEditorState)
            .Where(state => state is not null && !string.IsNullOrWhiteSpace(state.Process.Text))
            .Select(state => state!.Process.Text.Trim())
            .ToList();

        string previousHotkey = profile.Hotkey;
        List<AppRule> previousApps = profile.Apps;
        List<string> previousGameProcesses = profile.GameProcesses;
        try
        {
            profile.Hotkey = hotkey;
            profile.Apps = apps;
            profile.GameProcesses = gameProcesses;
            _coordinator.SaveProfile(profile);
            ShowToast("Automation saved", profile.Name, "AccentBrush");
        }
        catch (Exception ex)
        {
            profile.Hotkey = previousHotkey;
            profile.Apps = previousApps;
            profile.GameProcesses = previousGameProcesses;
            PopulateProfile();
            ShowError(ex.Message);
        }
    }

    private void PollDown_Click(object sender, Wpf.RoutedEventArgs e)
    {
        _pollSeconds = Math.Max(1, _pollSeconds - 1);
        PollValue.Text = _pollSeconds.ToString();
    }

    private void PollUp_Click(object sender, Wpf.RoutedEventArgs e)
    {
        _pollSeconds = Math.Min(30, _pollSeconds + 1);
        PollValue.Text = _pollSeconds.ToString();
    }

    private void SaveSettings_Click(object sender, Wpf.RoutedEventArgs e)
    {
        ApplySettingsControls();
        try
        {
            _coordinator.SaveSettings();
            RefreshReliableStartupStatus();
            ShowToast("Settings saved", "Startup and detection preferences updated.", "AccentBrush");
        }
        catch (Exception ex)
        {
            RefreshSettings();
            ShowError(ex.Message);
        }
    }

    private void ApplySettingsControls()
    {
        AppSettings settings = _coordinator.Document.Settings;
        settings.LaunchOnStartup = StartupToggle.IsChecked == true;
        settings.StartMinimized = ChooserToggle.IsChecked != true;
        settings.ConfirmBeforeSwitch = ConfirmSwitchToggle.IsChecked == true;
        settings.GameDetectionEnabled = DetectionToggle.IsChecked == true;
        settings.GamePollSeconds = _pollSeconds;
    }

    private async void ReliableStartup_Click(object sender, Wpf.RoutedEventArgs e)
    {
        StartupTaskStatus status = StartupTaskRegistration.GetStatus();
        bool remove = status.IsReady;
        string title = remove ? "Remove reliable startup?" : "Install reliable startup?";
        string body = remove
            ? "This removes the delayed Task Scheduler fallback. Normal Start with Windows remains available."
            : "Windows will ask for administrator approval once. The fallback starts PitLaunch after sign-in at normal permission level, so PitLaunch and the apps it launches do not become administrator apps.";
        bool confirmed = await ShowConfirmDialogAsync(
            title,
            body,
            remove ? "Remove fallback" : status.Exists ? "Repair fallback" : "Install fallback",
            false);
        if (!confirmed) return;

        ReliableStartupButton.IsEnabled = false;
        try
        {
            if (!remove)
            {
                StartupToggle.IsChecked = true;
                ApplySettingsControls();
                _coordinator.SaveSettings();
            }

            int exitCode = await RunStartupTaskMaintenanceAsync(remove);
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    "Windows could not update the reliable startup task. Open the activity log for details.");
            }

            RefreshReliableStartupStatus();
            ShowToast(
                remove ? "Fallback removed" : "Reliable startup ready",
                remove
                    ? "Normal startup settings were left unchanged."
                    : "PitLaunch now has an extra delayed sign-in fallback.",
                remove ? "WarningBrush" : "AccentBrush");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            RefreshReliableStartupStatus();
            ShowToast("Approval canceled", "Normal startup remains enabled.", "WarningBrush");
        }
        catch (Exception ex)
        {
            RefreshSettings();
            ShowError(ex.Message);
        }
        finally
        {
            ReliableStartupButton.IsEnabled = true;
        }
    }

    private static async Task<int> RunStartupTaskMaintenanceAsync(bool remove)
    {
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The PitLaunch executable path is unavailable.");
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add(remove ? "--remove-startup-task" : "--install-startup-task");
        if (!remove) startInfo.ArgumentList.Add(StartupTaskRegistration.CurrentUserSid());

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows could not open the startup task helper.");
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private void OpenData_Click(object sender, Wpf.RoutedEventArgs e) => OpenPath(AppPaths.DataDirectory);
    private void OpenLog_Click(object sender, Wpf.RoutedEventArgs e) => OpenPath(AppPaths.LogFile);

    private void OpenPath(string path)
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            string target = File.Exists(path) || Directory.Exists(path) ? path : AppPaths.DataDirectory;
            using Process? process = Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void DialogConfirm_Click(object sender, Wpf.RoutedEventArgs e) => CompleteDialog(true);
    private void DialogCancel_Click(object sender, Wpf.RoutedEventArgs e) => CompleteDialog(false);

    private void DialogInput_KeyDown(object sender, Input.KeyEventArgs e)
    {
        if (e.Key != Input.Key.Enter) return;
        CompleteDialog(true);
        e.Handled = true;
    }

    private void DialogLayer_MouseLeftButtonDown(object sender, Input.MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, DialogLayer)) CompleteDialog(false);
    }

    private void Root_PreviewKeyDown(object sender, Input.KeyEventArgs e)
    {
        if (e.Key != Input.Key.Escape) return;
        if (_dialogMode != DialogMode.None) CompleteDialog(false);
        else if (_page != AppPage.Home) Navigate(AppPage.Home);
        else _owner.HideToTray();
        e.Handled = true;
    }

    private void TitleDrag_MouseLeftButtonDown(object sender, Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) _owner.ToggleMaximize();
        else _owner.BeginWindowDrag();
    }

    private void Minimize_Click(object sender, Wpf.RoutedEventArgs e) => _owner.MinimizeWindow();
    private void Maximize_Click(object sender, Wpf.RoutedEventArgs e) => _owner.ToggleMaximize();
    private void Close_Click(object sender, Wpf.RoutedEventArgs e) => _owner.HideToTray();

    private Controls.Button Button(string text, string style) => new()
    {
        Content = text,
        Style = GetStyle(style)
    };

    private Controls.Button IconButton(Fluent.SymbolRegular symbol, string tooltip)
    {
        Controls.Button button = new()
        {
            Content = new Fluent.SymbolIcon
            {
                Symbol = symbol,
                FontSize = 15,
                Foreground = Brush("MutedBrush")
            },
            ToolTip = tooltip,
            Style = GetStyle("IconButton")
        };
        Wpf.Automation.AutomationProperties.SetName(button, tooltip);
        return button;
    }

    private Controls.TextBox CreateTextInput(string value) => new()
    {
        Text = value,
        Style = GetStyle("TextInput")
    };

    private Controls.CheckBox Toggle(string label, bool value) => new()
    {
        Content = label,
        IsChecked = value,
        Style = GetStyle("ToggleSwitch")
    };

    private static Controls.TextBlock Text(string value, double size, Media.Brush brush, Wpf.FontWeight? weight = null) => new()
    {
        Text = value,
        FontSize = size,
        Foreground = brush,
        FontWeight = weight ?? Wpf.FontWeights.Normal
    };

    private Media.Brush Brush(string key) => (Media.Brush)FindResource(key);
    private Wpf.Style GetStyle(string key) => (Wpf.Style)FindResource(key);

    private static Media.SolidColorBrush NewBrush(string color) =>
        new((Media.Color)Media.ColorConverter.ConvertFromString(color));

    private static Animation.DoubleAnimation DoubleAnimationTo(double value, int milliseconds) => new()
    {
        To = value,
        Duration = TimeSpan.FromMilliseconds(milliseconds),
        EasingFunction = new Animation.CubicEase { EasingMode = Animation.EasingMode.EaseOut }
    };

    private static Animation.DoubleAnimation StrongDoubleAnimationTo(double value, int milliseconds) => new()
    {
        To = value,
        Duration = TimeSpan.FromMilliseconds(milliseconds),
        EasingFunction = new Animation.QuinticEase { EasingMode = Animation.EasingMode.EaseOut }
    };

    private static Media.TranslateTransform EnsureTranslate(Wpf.FrameworkElement element)
    {
        if (element.RenderTransform is Media.TranslateTransform transform) return transform;
        transform = new Media.TranslateTransform();
        element.RenderTransform = transform;
        return transform;
    }

    private static void AnimateTranslate(Wpf.FrameworkElement element, double y, int milliseconds) =>
        EnsureTranslate(element).BeginAnimation(Media.TranslateTransform.YProperty, DoubleAnimationTo(y, milliseconds));

    private static void FadeIn(Wpf.FrameworkElement element)
    {
        element.Opacity = 0;
        Media.TranslateTransform transform = EnsureTranslate(element);
        transform.Y = 8;
        element.BeginAnimation(OpacityProperty, StrongDoubleAnimationTo(1, 300));
        transform.BeginAnimation(Media.TranslateTransform.YProperty, StrongDoubleAnimationTo(0, 310));
    }

    private void RunOnUi(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    private sealed record AppEditorState(
        Controls.TextBox Path,
        Controls.TextBox Arguments,
        Controls.CheckBox Start,
        Controls.CheckBox Close,
        Controls.CheckBox Force,
        Controls.CheckBox Hidden);

    private sealed record GameEditorState(Controls.ComboBox Process);

    private sealed record AudioPickerOption(string? Id, string Name, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed record SetupKindOption(SetupKind Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record RigDisplayOption(RigDisplayVariant Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record DisplayLayoutOption(DisplayLayoutMode Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record DisplayPickerOption(string DevicePath, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record SetupGuideIdentity(string Name, SetupKind Kind, RigDisplayVariant RigDisplay);

    private sealed record SetupGuideResult(
        string Name,
        SetupKind Kind,
        RigDisplayVariant RigDisplay,
        DisplaySetupRequest Display,
        AudioSnapshot Audio);

    private static bool DevicePathEquals(string left, string right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
