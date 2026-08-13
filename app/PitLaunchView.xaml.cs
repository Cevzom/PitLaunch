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
    private enum AppPage { Home, Profile, Games, Integrations, Settings }
    private enum DialogMode { None, Text, Confirm, SetupGuide, GamePicker }

    private readonly ProfileCoordinator _coordinator;
    private readonly MainForm _owner;
    private AppPage _page = AppPage.Home;
    private Guid? _selectedProfileId;
    private Guid? _gamesProfileId;
    private Guid? _integrationsProfileId;
    private bool _refreshingFeaturePicker;
    private int _pollSeconds = 2;
    private int _gameExitGraceSeconds = 10;
    private string _hotkeyCommitted = string.Empty;
    private string _toggleHotkeyCommitted = string.Empty;
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
    private readonly Dictionary<string, MonitorPosition> _setupCustomPositions = new(StringComparer.OrdinalIgnoreCase);
    // The transform the live layout was last drawn with, so a dragged rectangle can be read
    // back into display coordinates without re-deriving the bounds mid-drag.
    private double _previewScale = 1;
    private double _previewOffsetX;
    private double _previewOffsetY;
    private double _previewMinX;
    private double _previewMinY;
    private Controls.Border? _arrangeOverlay;
    private Controls.Border? _arrangeCanvasHost;
    // Where each screen sat in the last interactive render, so a recomputed layout can glide
    // into place instead of teleporting. Keyed per canvas width: the compact panel and the
    // enlarged overlay are different coordinate spaces and must not animate across each other.
    private readonly Dictionary<string, Wpf.Point> _lastArrangementPixels = new(StringComparer.OrdinalIgnoreCase);
    private double _lastArrangementCanvasWidth;
    private Controls.Border? _draggingMonitor;
    private Wpf.Point _dragOrigin;
    private double _dragOriginLeft;
    private double _dragOriginTop;
    private readonly UpdateService _updates = new();
    private Velopack.UpdateInfo? _pendingUpdate;
    private UpdateStatus? _requiredUpdate;
    private int _setupGuideStep;

    internal PitLaunchView(ProfileCoordinator coordinator, MainForm owner)
    {
        InitializeComponent();
        _coordinator = coordinator;
        _owner = owner;
        StatusBarVersion.Text = AppInfo.Version;

        Loaded += (_, _) =>
        {
            RefreshAll();
            PlayWindowReveal();
            Focus();
            Input.Keyboard.Focus(this);
            Dispatcher.BeginInvoke(ShowOnboardingIfNeeded);
        };

        _coordinator.ProfilesChanged += () => RunOnUi(RefreshAll);
        _coordinator.BusyChanged += (busy, message) => RunOnUi(() => SetBusy(busy, message));
        _coordinator.SwitchCompleted += completed => RunOnUi(() => ShowReport(completed.Report));
    }

    private void ShowOnboardingIfNeeded()
    {
        if (_coordinator.Document.Settings.OnboardingCompleted ||
            _coordinator.Document.Profiles.Count > 0)
        {
            return;
        }

        OnboardingLayer.Visibility = Wpf.Visibility.Visible;
        OnboardingLayer.Opacity = 0;
        OnboardingLayer.BeginAnimation(OpacityProperty, StrongDoubleAnimationTo(1, 240));
        Dispatcher.BeginInvoke(() => OnboardingStartButton.Focus());
        AppLog.Info("Onboarding: First-run guide opened.");
    }

    private async void OnboardingStart_Click(object sender, Wpf.RoutedEventArgs e)
    {
        HideOnboarding();
        await Task.Delay(170);
        bool created = await PromptCaptureAsync();
        if (created)
        {
            CompleteOnboarding("Guided setup completed", hide: false);
        }
        else if (!_coordinator.Document.Settings.OnboardingCompleted &&
                 _coordinator.Document.Profiles.Count == 0)
        {
            ShowOnboardingIfNeeded();
        }
    }

    private void OnboardingSkip_Click(object sender, Wpf.RoutedEventArgs e) =>
        CompleteOnboarding("Guide skipped");

    private bool CompleteOnboarding(string reason, bool hide = true)
    {
        try
        {
            _coordinator.Document.Settings.OnboardingCompleted = true;
            _coordinator.SaveSettings();
        }
        catch (Exception ex)
        {
            _coordinator.Document.Settings.OnboardingCompleted = false;
            ShowError("PitLaunch could not save the quick-start choice: " + ex.Message);
            return false;
        }

        AppLog.Info("Onboarding: " + reason + ".");
        if (hide) HideOnboarding();
        return true;
    }

    private void HideOnboarding()
    {
        Animation.DoubleAnimation fade = DoubleAnimationTo(0, 150);
        fade.Completed += (_, _) => OnboardingLayer.Visibility = Wpf.Visibility.Collapsed;
        OnboardingLayer.BeginAnimation(OpacityProperty, fade);
    }

    internal async Task<bool> PromptCaptureAsync()
    {
        AppLog.Info("Capture: Setup guide opened.");
        SetupGuideResult? setup = await ShowSetupGuideAsync();
        if (setup is null)
        {
            AppLog.Info("Capture: Setup guide canceled.");
            return false;
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
                return false;
            }

            _selectedProfileId = profile.Id;
            RunOnUi(RefreshAll);
            // The guide's button is "Create and switch" â€” the user already committed, so skip the extra dialog.
            await ActivateProfileAsync(profile.Id, ActivationSource.User, bypassConfirm: true);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Configured setup request failed: " + ex);
            RunOnUi(() => ShowError(ex.Message));
            return false;
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
        Controls.Grid.SetColumnSpan(SetupGuideNameHost, suggestedKind == SetupKind.SimRacing ? 1 : 3);
        SetupGuideError.Visibility = Wpf.Visibility.Collapsed;
        SetupGuideHardwareError.Visibility = Wpf.Visibility.Collapsed;
        SetupGuideCaptureButton.IsEnabled = true;
        _setupGuideStep = 0;
        ShowSetupGuideStep(0, animate: false);

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
        Controls.Grid.SetColumnSpan(SetupGuideNameHost, sim ? 1 : 3);
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
        if (_setupGuideStep == 0)
        {
            if (!TryReadSetupIdentity(out SetupGuideIdentity setup)) return;
            _setupGuideIdentity = setup;
            PrepareSetupHardware(setup);
            ShowSetupGuideStep(1, animate: true);
            return;
        }

        if (_setupGuideStep == 1)
        {
            try
            {
                _ = _coordinator.BuildDisplaySnapshot(CurrentSetupDisplayRequest());
                SetupGuideHardwareError.Visibility = Wpf.Visibility.Collapsed;
                ShowSetupGuideStep(2, animate: true);
            }
            catch (Exception ex)
            {
                ShowSetupHardwareError(ex.Message);
            }
            return;
        }

        if (_setupGuideStep == 2)
        {
            try
            {
                UpdateSetupReview();
                ShowSetupGuideStep(3, animate: true);
            }
            catch (Exception ex)
            {
                ShowSetupGuideStep(1, animate: true);
                ShowSetupHardwareError(ex.Message);
            }
        }
    }

    private void PrepareSetupHardware(SetupGuideIdentity setup)
    {
        SetupGuideHardwareError.Visibility = Wpf.Visibility.Collapsed;
        SetupGuideNextButton.IsEnabled = true;
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
            RemoveArrangementOverlay();
            _setupCustomPositions.Clear();
            SetupGuideLayoutPicker.ItemsSource = new List<DisplayLayoutOption>
            {
                new(DisplayLayoutMode.Recommended, "Recommended"),
                new(DisplayLayoutMode.Horizontal, "Line up horizontally"),
                new(DisplayLayoutMode.KeepCurrent, "Keep current positions"),
                new(DisplayLayoutMode.Custom, "Custom - drag the preview")
            };
            SetupGuideLayoutPicker.SelectedIndex = 0;
            UpdateSetupMainPicker();
            PopulateSetupAudioPickers(setup);
            UpdateSetupPreview();
        }
        catch (Exception ex)
        {
            SetupGuideNextButton.IsEnabled = false;
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

    private void SetupGuideLayoutPicker_SelectionChanged(object sender, Controls.SelectionChangedEventArgs e)
    {
        // Choosing a computed arrangement discards hand placement, which is the only way back
        // from a custom layout the user no longer wants.
        if (SetupGuideLayoutPicker.SelectedItem is DisplayLayoutOption option &&
            option.Value != DisplayLayoutMode.Custom)
        {
            _setupCustomPositions.Clear();
        }
        UpdateSetupPreview();
    }

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
        return new DisplaySetupRequest(
            ordered,
            _setupPrimaryDisplayPath,
            layout,
            layout == DisplayLayoutMode.Custom ? _setupCustomPositions : null);
    }

    private void UpdateSetupPreview()
    {
        if (SetupGuidePreviewHost is null || SetupGuideLayoutPicker is null) return;
        try
        {
            DisplaySnapshot snapshot = _coordinator.BuildDisplaySnapshot(CurrentSetupDisplayRequest());
            // Only one canvas is interactive at a time: whichever renders last owns the
            // pixel-to-display transform that a drag reads back through.
            Controls.Canvas preview = CreateMiniDisplayPreview(
                new Profile { Display = snapshot }, interactive: _arrangeOverlay is null);
            // A drag must not fade the rectangle the pointer just released, so only animate the
            // first render of a given arrangement.
            if (_setupCustomPositions.Count == 0)
            {
                preview.Opacity = 0;
                preview.BeginAnimation(OpacityProperty, StrongDoubleAnimationTo(1, 260));
            }
            SetupGuidePreviewHost.Content = preview;
            MonitorSnapshot main = snapshot.Monitors.First(monitor => monitor.Enabled && monitor.Primary);
            int count = snapshot.Monitors.Count(monitor => monitor.Enabled);
            string hint = count > 1 ? "  |  drag to place" : string.Empty;
            SetupGuidePreviewSummary.Text = $"{count} screen{(count == 1 ? string.Empty : "s")}  |  {main.FriendlyName} main{hint}";
            // Never disable the toggle while the overlay is open, or the only way out would be
            // the Done button.
            if (SetupGuideExpandButton is not null)
                SetupGuideExpandButton.IsEnabled = count > 1 || _arrangeOverlay is not null;
            if (_arrangeCanvasHost is not null)
            {
                _arrangeCanvasHost.Child = CreateMiniDisplayPreview(
                    new Profile { Display = snapshot }, interactive: true, canvasWidth: 604, canvasHeight: 348);
            }
            SetupGuideValidationStrip.Background = Brush("InfoSoftBrush");
            SetupGuideValidationStrip.BorderBrush = NewBrush("#453A78");
            SetupGuideValidationText.Text = "Ready. PitLaunch will ask Windows to validate this exact plan before it saves or switches.";
            SetupGuideNextButton.IsEnabled = true;
            SetupGuideCaptureButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            SetupGuideNextButton.IsEnabled = false;
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
        SetupGuideNextButton.IsEnabled = false;
        SetupGuideCaptureButton.IsEnabled = false;
    }

    private void UpdateSetupReview()
    {
        SetupGuideIdentity identity = _setupGuideIdentity
            ?? throw new InvalidOperationException("Choose a setup name and type first.");
        DisplaySnapshot display = _coordinator.BuildDisplaySnapshot(CurrentSetupDisplayRequest());
        List<MonitorSnapshot> enabled = display.Monitors.Where(monitor => monitor.Enabled).ToList();
        MonitorSnapshot main = enabled.First(monitor => monitor.Primary);
        DisplayLayoutOption? layout = SetupGuideLayoutPicker.SelectedItem as DisplayLayoutOption;
        AudioEndpointSnapshot? playback = SelectedEndpoint(SetupGuidePlaybackPicker);
        AudioEndpointSnapshot? microphone = SelectedEndpoint(SetupGuideMicrophonePicker);

        SetupGuideReviewName.Text = identity.Name;
        SetupGuideReviewType.Text = identity.Kind == SetupKind.SimRacing ? "Sim racing" : "Desk";
        SetupGuideReviewDisplays.Text = $"{enabled.Count} screen{(enabled.Count == 1 ? string.Empty : "s")} · {main.FriendlyName} is main";
        SetupGuideReviewLayout.Text = layout?.Label ?? "Recommended";
        SetupGuideReviewOutput.Text = playback?.FriendlyName ?? "Leave Windows output unchanged";
        SetupGuideReviewMicrophone.Text = microphone?.FriendlyName ?? "Leave Windows microphone unchanged";
        SetupGuideValidationStrip.Background = Brush("InfoSoftBrush");
        SetupGuideValidationStrip.BorderBrush = NewBrush("#453A78");
        SetupGuideValidationText.Text = "Ready. Create it when this matches the place in front of you.";
        SetupGuideCaptureButton.IsEnabled = true;
    }

    private void SetupGuideBack_Click(object sender, Wpf.RoutedEventArgs e) =>
        ShowSetupGuideStep(_setupGuideStep - 1, animate: true);

    private Wpf.FrameworkElement[] SetupGuideSteps() =>
    [
        SetupGuideIdentityStep,
        SetupGuideDisplayStep,
        SetupGuideAudioStep,
        SetupGuideReviewStep
    ];

    private void ShowSetupGuideStep(int step, bool animate)
    {
        Wpf.FrameworkElement[] steps = SetupGuideSteps();
        int next = Math.Clamp(step, 0, steps.Length - 1);
        Wpf.FrameworkElement? outgoing = steps.FirstOrDefault(item => item.Visibility == Wpf.Visibility.Visible);
        Wpf.FrameworkElement incoming = steps[next];
        _setupGuideStep = next;
        SetupGuideStepLabel.Text = $"STEP {next + 1} OF {steps.Length}";
        SetupGuideBackButton.Visibility = next > 0 ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        SetupGuideNextButton.Visibility = next < steps.Length - 1 ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        SetupGuideCaptureButton.Visibility = next == steps.Length - 1 ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        if (next != 1) SetupGuideNextButton.IsEnabled = true;
        SetupGuideNextButton.Content = next switch
        {
            0 => "Choose displays",
            1 => "Choose sound",
            _ => "Review setup"
        };
        Wpf.Automation.AutomationProperties.SetName(SetupGuideNextButton, SetupGuideNextButton.Content.ToString() ?? "Continue setup");

        foreach (Wpf.FrameworkElement panel in steps)
        {
            if (ReferenceEquals(panel, incoming)) continue;
            if (!ReferenceEquals(panel, outgoing)) panel.Visibility = Wpf.Visibility.Collapsed;
        }

        if (!animate || outgoing is null || ReferenceEquals(outgoing, incoming))
        {
            incoming.BeginAnimation(OpacityProperty, null);
            EnsureTranslate(incoming).BeginAnimation(Media.TranslateTransform.YProperty, null);
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
            ShowSetupGuideStep(0, animate: true);
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
            ShowSetupGuideStep(1, animate: true);
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
        if (!ReferenceEquals(e.OriginalSource, SetupGuideLayer)) return;
        // Same one-layer-at-a-time rule as Escape: clicking beside the arrangement panel closes
        // it, not the half-built setup behind it.
        if (_arrangeOverlay is not null) CloseArrangementOverlay();
        else CompleteSetupGuide(null);
    }

    // A switch the user did not start from inside the window must never raise a modal dialog:
    // a game-detected switch would pop PitLaunch over a fullscreen game, where the prompt is
    // unreachable and the switch silently never happens. Hotkey and command line presses are
    // already a deliberate act, so a second confirmation only defeats them.
    internal static bool RequiresConfirmation(ActivationSource source) =>
        source is ActivationSource.User or ActivationSource.Tray;

    /// <summary>
    /// The whole confirm decision in one place: the user's setting, the per-call bypass used by
    /// flows that already asked, and the source policy above. Kept static so the self test can
    /// check every source without standing up a window.
    /// </summary>
    internal static bool ShouldConfirmSwitch(bool confirmBeforeSwitch, bool bypassConfirm, ActivationSource source) =>
        confirmBeforeSwitch && !bypassConfirm && RequiresConfirmation(source);

    internal async Task ActivateProfileAsync(Guid profileId, ActivationSource source, bool bypassConfirm = false)
    {
        Profile? target = _coordinator.Document.Profiles.FirstOrDefault(profile => profile.Id == profileId);
        if (target is null) return;

        // Required-update and startup-policy gates are enforced in the coordinator for every
        // entry point. Skip readiness/confirmation UI here so a tray click cannot leave a hidden
        // confirmation dialog behind the non-dismissible update screen.
        if (_coordinator.SwitchBlockReason is not null)
        {
            await _coordinator.ActivateAsync(profileId, source);
            return;
        }

        if (RequiresConfirmation(source) && !bypassConfirm)
        {
            ReadinessReport readiness = _coordinator.CheckReadiness(target);
            if (!readiness.CanSwitch)
            {
                _selectedProfileId = target.Id;
                PopulateProfile();
                ShowHardwareTab();
                Navigate(AppPage.Profile);
                ShowReadiness(readiness);
                ShowToast("Setup is not ready", "Open Hardware to see what must be connected or corrected.", "ErrorBrush");
                return;
            }

            if (!readiness.IsReady)
            {
                if (_dialogMode != DialogMode.None)
                {
                    ShowToast("Switch not started", "Finish the open dialog, then try again.", "WarningBrush");
                    return;
                }
                if (!_owner.Visible) _owner.ShowWindow();
                string detail = string.Join("\n", readiness.Items
                    .Where(item => item.Severity != OperationSeverity.Info)
                    .Take(5)
                    .Select(item => $"• {item.Area}: {item.Message}"));
                bool continueSwitch = await ShowConfirmDialogAsync(
                    $"Switch to {target.Name} anyway?",
                    "PitLaunch found a few things to check:\n\n" + detail,
                    "Switch anyway",
                    false);
                if (!continueSwitch) return;
                bypassConfirm = true;
            }
        }

        if (ShouldConfirmSwitch(_coordinator.Document.Settings.ConfirmBeforeSwitch, bypassConfirm, source))
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

    private async void ToggleSetup_Click(object sender, Wpf.RoutedEventArgs e)
    {
        bool found = await ToggleDeskRigAsync(ActivationSource.User);
        if (!found)
            ShowToast("Two setups needed", "Create both a Desk and Sim Racing setup to use one-tap toggle.", "WarningBrush");
    }

    internal async Task<bool> ToggleDeskRigAsync(ActivationSource source)
    {
        Profile? target = _coordinator.FindDeskRigToggleTarget();
        if (target is null) return false;
        await ActivateProfileAsync(target.Id, source);
        return true;
    }

    private async void UndoSwitch_Click(object sender, Wpf.RoutedEventArgs e)
    {
        OperationReport report = await _coordinator.UndoLastSwitchAsync();
        ShowReport(report);
        RefreshAll();
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
        SidebarColumn.Width = _startupChooser ? new Wpf.GridLength(0) : new Wpf.GridLength(240);
        SidebarHost.Visibility = _startupChooser ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
        StatsSection.Visibility = _startupChooser ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
        CaptureButton.Visibility = _startupChooser ? Wpf.Visibility.Collapsed : Wpf.Visibility.Visible;
        StartupEyebrow.Visibility = _startupChooser ? Wpf.Visibility.Visible : Wpf.Visibility.Collapsed;
        TitleRailColumn.Width = _startupChooser ? new Wpf.GridLength(0) : new Wpf.GridLength(240);
        // The chooser asks a question, so it drops the "PitLaunch <page>" lead-in and reads as one sentence.
        HomeTitleLead.Text = _startupChooser ? string.Empty : "PitLaunch ";
        HomeTitleAccent.Text = _startupChooser ? "How are you using this PC today?" : "Setups";
        HomeTitleAccent.Foreground = _startupChooser ? Brush("TextBrush") : Brush("AccentBrush");
        HomeTitle.FontSize = _startupChooser ? 34 : 38;
        HomeSubtitle.Text = _startupChooser
            ? "Choose a setup and PitLaunch will restore its screens, sound, windows, and apps."
            : "Switch the whole PC between your desk and rig in one move.";
        SavedSetupsTitle.Text = _startupChooser ? "Choose a setup" : "Saved setups";
        EmptyStateTitle.Text = _startupChooser ? "No setups are ready yet" : "Create your first setup";
        EmptyStateCopy.Text = _startupChooser
            ? "Open PitLaunch after sign-in and create a Desk or Sim racing setup first."
            : "Start with Desk. When the rig is connected, create a second Sim racing setup.";
        HomePage.Margin = _startupChooser
            ? new Wpf.Thickness(52, 34, 52, 14)
            : new Wpf.Thickness(44, 30, 44, 14);
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
        if (_page == AppPage.Games) RefreshGamesProfilePicker();
        if (_page == AppPage.Integrations) RefreshIntegrationsProfilePicker();
    }

    private void RefreshHome()
    {
        Profile? active = _coordinator.ActiveProfile;
        bool hasTogglePair = _coordinator.Document.Profiles.Count >= 2;
        ToggleSetupButton.Visibility = !_startupChooser && hasTogglePair
            ? Wpf.Visibility.Visible
            : Wpf.Visibility.Collapsed;
        UndoSwitchButton.Visibility = !_startupChooser && _coordinator.CanUndo
            ? Wpf.Visibility.Visible
            : Wpf.Visibility.Collapsed;
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
        Media.SolidColorBrush background = NewBrush(lastUsed ? "#14293A" : "#1C2024");
        Media.SolidColorBrush borderBrush = NewBrush(lastUsed ? "#2E7CA8" : "#333942");
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
        Media.SolidColorBrush background = NewBrush(active ? "#14293A" : "#1C2024");
        Media.SolidColorBrush borderBrush = NewBrush(active ? "#2E7CA8" : "#333942");
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
                    active ? Media.Color.FromRgb(26, 55, 76) : Media.Color.FromRgb(36, 41, 46),
                    TimeSpan.FromMilliseconds(150)));
            borderBrush.BeginAnimation(Media.SolidColorBrush.ColorProperty,
                new Animation.ColorAnimation(active ? Media.Color.FromRgb(58, 150, 198) : Media.Color.FromRgb(67, 74, 85), TimeSpan.FromMilliseconds(150)));
            lift.BeginAnimation(Media.TranslateTransform.YProperty, DoubleAnimationTo(-3, 150));
            shadow.BeginAnimation(Media.Effects.DropShadowEffect.BlurRadiusProperty, DoubleAnimationTo(22, 150));
            shadow.BeginAnimation(Media.Effects.DropShadowEffect.OpacityProperty, DoubleAnimationTo(active ? 0.28 : 0.22, 150));
        };
        card.MouseLeave += (_, _) =>
        {
            Media.Color normal = active ? Media.Color.FromRgb(20, 41, 58) : Media.Color.FromRgb(28, 32, 36);
            background.BeginAnimation(Media.SolidColorBrush.ColorProperty,
                new Animation.ColorAnimation(normal, TimeSpan.FromMilliseconds(160)));
            borderBrush.BeginAnimation(Media.SolidColorBrush.ColorProperty,
                new Animation.ColorAnimation(active ? Media.Color.FromRgb(46, 124, 168) : Media.Color.FromRgb(51, 57, 66), TimeSpan.FromMilliseconds(160)));
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
                    highlighted ? Media.Color.FromRgb(26, 55, 76) : Media.Color.FromRgb(36, 41, 46),
                    TimeSpan.FromMilliseconds(150)));
            borderBrush.BeginAnimation(Media.SolidColorBrush.ColorProperty,
                new Animation.ColorAnimation(
                    highlighted ? Media.Color.FromRgb(58, 150, 198) : Media.Color.FromRgb(67, 74, 85),
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
                    highlighted ? Media.Color.FromRgb(20, 41, 58) : Media.Color.FromRgb(28, 32, 36),
                    TimeSpan.FromMilliseconds(160)));
            borderBrush.BeginAnimation(Media.SolidColorBrush.ColorProperty,
                new Animation.ColorAnimation(
                    highlighted ? Media.Color.FromRgb(46, 124, 168) : Media.Color.FromRgb(51, 57, 66),
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

    private Controls.Canvas CreateMiniDisplayPreview(
        Profile profile,
        bool interactive = false,
        double canvasWidth = 220,
        double canvasHeight = 136)
    {
        Controls.Canvas canvas = new() { Width = canvasWidth, Height = canvasHeight };
        if (interactive) canvas.Background = Media.Brushes.Transparent;
        double frameWidth = canvasWidth - 26;
        double frameHeight = canvasHeight - 44;
        bool sameSpaceAsLastRender = interactive && Math.Abs(_lastArrangementCanvasWidth - canvasWidth) < 0.5;
        Dictionary<string, Wpf.Point> nextPixels = new(StringComparer.OrdinalIgnoreCase);
        List<MonitorSnapshot> enabled = profile.Display.Monitors.Where(monitor => monitor.Enabled).ToList();
        if (enabled.Count > 0)
        {
            double minX = enabled.Min(monitor => monitor.X);
            double minY = enabled.Min(monitor => monitor.Y);
            double maxX = enabled.Max(monitor => monitor.X + monitor.Width);
            double maxY = enabled.Max(monitor => monitor.Y + monitor.Height);
            double worldWidth = Math.Max(1, maxX - minX);
            double worldHeight = Math.Max(1, maxY - minY);
            double scale = Math.Min(frameWidth / worldWidth, frameHeight / worldHeight);
            double contentWidth = worldWidth * scale;
            double contentHeight = worldHeight * scale;
            double offsetX = (canvasWidth - contentWidth) / 2;
            double offsetY = 12 + (frameHeight - contentHeight) / 2;
            if (interactive)
            {
                _previewScale = scale;
                _previewOffsetX = offsetX;
                _previewOffsetY = offsetY;
                _previewMinX = minX;
                _previewMinY = minY;
            }

            for (int index = 0; index < enabled.Count; index++)
            {
                MonitorSnapshot monitor = enabled[index];
                Controls.Border display = new()
                {
                    // Read-only cards floor the size so a small screen stays visible. An
                    // interactive canvas must not: a rectangle drawn larger than it really is
                    // would refuse to sit beside its neighbour.
                    Width = interactive ? monitor.Width * scale : Math.Max(34, monitor.Width * scale),
                    Height = interactive ? monitor.Height * scale : Math.Max(23, monitor.Height * scale),
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
                double targetLeft = offsetX + (monitor.X - minX) * scale;
                double targetTop = offsetY + (monitor.Y - minY) * scale;
                Controls.Canvas.SetLeft(display, targetLeft);
                Controls.Canvas.SetTop(display, targetTop);
                if (interactive && enabled.Count > 1)
                {
                    display.Tag = monitor.DevicePath;
                    display.Cursor = Input.Cursors.SizeAll;
                    display.ToolTip = $"Drag to place {monitor.FriendlyName}";
                    AttachMonitorDrag(canvas, display);
                    AttachMonitorHover(display);
                    if (sameSpaceAsLastRender &&
                        _lastArrangementPixels.TryGetValue(monitor.DevicePath, out Wpf.Point previous) &&
                        (Math.Abs(previous.X - targetLeft) > 0.5 || Math.Abs(previous.Y - targetTop) > 0.5))
                    {
                        GlideTo(display, previous, targetLeft, targetTop);
                    }
                    nextPixels[monitor.DevicePath] = new Wpf.Point(targetLeft, targetTop);
                }
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

        if (interactive)
        {
            _lastArrangementCanvasWidth = canvasWidth;
            _lastArrangementPixels.Clear();
            foreach (KeyValuePair<string, Wpf.Point> entry in nextPixels) _lastArrangementPixels[entry.Key] = entry.Value;
        }
        return canvas;
    }

    /// <summary>Runs a rectangle from where it used to be to where it now belongs.</summary>
    private static void GlideTo(Controls.Border display, Wpf.Point from, double toLeft, double toTop)
    {
        // FillBehavior.Stop with the destination already set means the animation hands control
        // back cleanly, so a later drag reads the real position rather than a held animated one.
        display.BeginAnimation(Controls.Canvas.LeftProperty, new Animation.DoubleAnimation
        {
            From = from.X,
            To = toLeft,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new Animation.QuinticEase { EasingMode = Animation.EasingMode.EaseOut },
            FillBehavior = Animation.FillBehavior.Stop
        });
        display.BeginAnimation(Controls.Canvas.TopProperty, new Animation.DoubleAnimation
        {
            From = from.Y,
            To = toTop,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new Animation.QuinticEase { EasingMode = Animation.EasingMode.EaseOut },
            FillBehavior = Animation.FillBehavior.Stop
        });
    }

    private static Media.ScaleTransform EnsureScale(Wpf.FrameworkElement element)
    {
        if (element.RenderTransform is Media.ScaleTransform existing) return existing;
        Media.ScaleTransform scale = new(1, 1);
        element.RenderTransformOrigin = new Wpf.Point(0.5, 0.5);
        element.RenderTransform = scale;
        return scale;
    }

    private static void ScaleTo(Wpf.FrameworkElement element, double value, int milliseconds)
    {
        Media.ScaleTransform scale = EnsureScale(element);
        Animation.DoubleAnimation animation = new()
        {
            To = value,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            EasingFunction = new Animation.QuinticEase { EasingMode = Animation.EasingMode.EaseOut }
        };
        scale.BeginAnimation(Media.ScaleTransform.ScaleXProperty, animation);
        scale.BeginAnimation(Media.ScaleTransform.ScaleYProperty, animation);
    }

    private void AttachMonitorHover(Controls.Border display)
    {
        display.MouseEnter += (_, _) =>
        {
            if (_draggingMonitor is null) ScaleTo(display, 1.03, 140);
        };
        display.MouseLeave += (_, _) =>
        {
            if (!ReferenceEquals(_draggingMonitor, display)) ScaleTo(display, 1, 180);
        };
    }

    private void SetupGuideExpand_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (_arrangeOverlay is null) OpenArrangementOverlay();
        else CloseArrangementOverlay();
    }

    // The setup guide panel is 246px wide, which is enough to read a layout and not enough to
    // place one. This lifts the same canvas onto a surface big enough to work on.
    private void OpenArrangementOverlay()
    {
        Controls.Grid shell = new();
        shell.RowDefinitions.Add(new Controls.RowDefinition { Height = Wpf.GridLength.Auto });
        shell.RowDefinitions.Add(new Controls.RowDefinition { Height = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        shell.RowDefinitions.Add(new Controls.RowDefinition { Height = Wpf.GridLength.Auto });

        Controls.StackPanel header = new();
        header.Children.Add(Text("Arrange your screens", 15, Brush("TextBrush"), Wpf.FontWeights.SemiBold));
        Controls.TextBlock hint = Text(
            "Drag a screen to place it. Screens snap to each other and cannot overlap.",
            12, Brush("MutedBrush"));
        hint.Margin = new Wpf.Thickness(0, 4, 0, 12);
        header.Children.Add(hint);
        shell.Children.Add(header);

        Controls.Border host = new()
        {
            Background = Brush("WindowBrush"),
            BorderBrush = Brush("BorderSoftBrush"),
            BorderThickness = new Wpf.Thickness(1),
            CornerRadius = new Wpf.CornerRadius(7)
        };
        Controls.Grid.SetRow(host, 1);
        shell.Children.Add(host);
        _arrangeCanvasHost = host;

        Controls.Button done = new()
        {
            Content = "Done",
            Style = GetStyle("PrimaryButton"),
            HorizontalAlignment = Wpf.HorizontalAlignment.Right,
            Margin = new Wpf.Thickness(0, 12, 0, 0)
        };
        done.Click += (_, _) => CloseArrangementOverlay();
        Controls.Grid.SetRow(done, 2);
        shell.Children.Add(done);

        Controls.Border panel = new()
        {
            Width = 660,
            Height = 470,
            Padding = new Wpf.Thickness(18),
            Background = Brush("ChromeBrush"),
            BorderBrush = Brush("BorderBrush"),
            BorderThickness = new Wpf.Thickness(1),
            CornerRadius = new Wpf.CornerRadius(10),
            HorizontalAlignment = Wpf.HorizontalAlignment.Center,
            VerticalAlignment = Wpf.VerticalAlignment.Center,
            Child = shell
        };
        Controls.Panel.SetZIndex(panel, 50);
        SetupGuideLayer.Children.Add(panel);
        _arrangeOverlay = panel;
        SetupGuideExpandIcon.Symbol = Fluent.SymbolRegular.FullScreenMinimize24;

        // Rise into place rather than appear: the panel is covering content the user was just
        // looking at, so the movement explains where it came from.
        panel.Opacity = 0;
        Media.ScaleTransform grow = EnsureScale(panel);
        grow.ScaleX = 0.97;
        grow.ScaleY = 0.97;
        panel.BeginAnimation(OpacityProperty, StrongDoubleAnimationTo(1, 220));
        ScaleTo(panel, 1, 260);

        // The canvas is new, so there is nothing to glide from on this first render.
        _lastArrangementPixels.Clear();
        _lastArrangementCanvasWidth = 0;
        UpdateSetupPreview();
    }

    private void RemoveArrangementOverlay()
    {
        if (_arrangeOverlay is not null) SetupGuideLayer.Children.Remove(_arrangeOverlay);
        _arrangeOverlay = null;
        _arrangeCanvasHost = null;
        if (SetupGuideExpandIcon is not null) SetupGuideExpandIcon.Symbol = Fluent.SymbolRegular.FullScreenMaximize24;
        _lastArrangementPixels.Clear();
        _lastArrangementCanvasWidth = 0;
    }

    private void CloseArrangementOverlay()
    {
        if (_arrangeOverlay is Controls.Border closing)
        {
            _arrangeOverlay = null;
            _arrangeCanvasHost = null;
            SetupGuideExpandIcon.Symbol = Fluent.SymbolRegular.FullScreenMaximize24;
            _lastArrangementPixels.Clear();
            _lastArrangementCanvasWidth = 0;

            Animation.DoubleAnimation fade = StrongDoubleAnimationTo(0, 160);
            fade.Completed += (_, _) => SetupGuideLayer.Children.Remove(closing);
            closing.IsHitTestVisible = false;
            closing.BeginAnimation(OpacityProperty, fade);
            ScaleTo(closing, 0.97, 180);
        }
        UpdateSetupPreview();
    }

    // Dragging happens in preview pixels and is only converted back to display coordinates on
    // release. Re-deriving the layout on every mouse move would shift the bounds under the
    // cursor -- the rectangle would swim away from the pointer as it moved.
    private void AttachMonitorDrag(Controls.Canvas canvas, Controls.Border display)
    {
        display.MouseLeftButtonDown += (_, e) =>
        {
            // A glide still running would keep writing Canvas.Left underneath the pointer and
            // the rectangle would swim away from the cursor. Hand control back first.
            display.BeginAnimation(Controls.Canvas.LeftProperty, null);
            display.BeginAnimation(Controls.Canvas.TopProperty, null);
            _draggingMonitor = display;
            _dragOrigin = e.GetPosition(canvas);
            _dragOriginLeft = Controls.Canvas.GetLeft(display);
            _dragOriginTop = Controls.Canvas.GetTop(display);
            display.CaptureMouse();
            Controls.Panel.SetZIndex(display, 10);
            ScaleTo(display, 1.06, 120);
            e.Handled = true;
        };

        display.MouseMove += (_, e) =>
        {
            if (!ReferenceEquals(_draggingMonitor, display) || !display.IsMouseCaptured) return;
            Wpf.Point now = e.GetPosition(canvas);
            double left = _dragOriginLeft + (now.X - _dragOrigin.X);
            double top = _dragOriginTop + (now.Y - _dragOrigin.Y);
            (left, top) = SnapToNeighbours(canvas, display, left, top);
            (left, top) = PushOutOfNeighbours(canvas, display, left, top);
            Controls.Canvas.SetLeft(display, Math.Max(-40, Math.Min(canvas.Width + 40 - display.Width, left)));
            Controls.Canvas.SetTop(display, Math.Max(-40, Math.Min(canvas.Height + 40 - display.Height, top)));
        };

        display.MouseLeftButtonUp += (_, e) =>
        {
            if (!ReferenceEquals(_draggingMonitor, display)) return;
            e.Handled = true;
            display.ReleaseMouseCapture();
        };

        // Finishing here rather than on mouse-up covers the case where capture is taken away --
        // another window stealing focus mid-drag would otherwise leave the screen stuck lifted
        // and the next click would resume a drag the user had abandoned.
        display.LostMouseCapture += (_, _) =>
        {
            if (!ReferenceEquals(_draggingMonitor, display)) return;
            _draggingMonitor = null;
            Controls.Panel.SetZIndex(display, 0);
            ScaleTo(display, display.IsMouseOver ? 1.03 : 1, 200);
            CommitDraggedArrangement(canvas);
        };
    }

    /// <summary>Pulls a dragged edge onto a neighbour's edge, so screens line up the way they physically sit.</summary>
    private static (double Left, double Top) SnapToNeighbours(
        Controls.Canvas canvas,
        Controls.Border dragged,
        double left,
        double top)
    {
        const double threshold = 5;
        foreach (Controls.Border other in canvas.Children.OfType<Controls.Border>())
        {
            if (ReferenceEquals(other, dragged)) continue;
            double otherLeft = Controls.Canvas.GetLeft(other);
            double otherTop = Controls.Canvas.GetTop(other);
            foreach (double candidate in new[] { otherLeft - dragged.Width, otherLeft + other.Width, otherLeft })
            {
                if (Math.Abs(left - candidate) < threshold) left = candidate;
            }
            foreach (double candidate in new[] { otherTop, otherTop + other.Height - dragged.Height, otherTop + (other.Height - dragged.Height) / 2 })
            {
                if (Math.Abs(top - candidate) < threshold) top = candidate;
            }
        }
        return (left, top);
    }

    /// <summary>
    /// Windows never lets two screens occupy the same space, and neither does this: a rectangle
    /// dragged over a neighbour slides out along whichever axis it has travelled least far into,
    /// which lands it flush against the side it came from.
    /// </summary>
    private static (double Left, double Top) PushOutOfNeighbours(
        Controls.Canvas canvas,
        Controls.Border dragged,
        double left,
        double top)
    {
        List<Wpf.Rect> others = canvas.Children.OfType<Controls.Border>()
            .Where(border => !ReferenceEquals(border, dragged) && border.Tag is string)
            .Select(border => new Wpf.Rect(
                Controls.Canvas.GetLeft(border), Controls.Canvas.GetTop(border), border.Width, border.Height))
            .ToList();
        Wpf.Rect placed = PushOutOfRects(new Wpf.Rect(left, top, dragged.Width, dragged.Height), others);
        return (placed.X, placed.Y);
    }

    /// <summary>The overlap rule as plain geometry, so it can be checked without a window.</summary>
    internal static Wpf.Rect PushOutOfRects(Wpf.Rect dragged, IReadOnlyList<Wpf.Rect> others)
    {
        // A few passes so squeezing between two screens settles instead of oscillating.
        for (int pass = 0; pass < 4; pass++)
        {
            bool moved = false;
            foreach (Wpf.Rect other in others)
            {
                // Touching edges are not an overlap -- that is the arrangement we want.
                if (dragged.Left >= other.Right || dragged.Right <= other.Left ||
                    dragged.Top >= other.Bottom || dragged.Bottom <= other.Top)
                {
                    continue;
                }

                double outLeft = other.Left - dragged.Width - dragged.X;
                double outRight = other.Right - dragged.X;
                double outUp = other.Top - dragged.Height - dragged.Y;
                double outDown = other.Bottom - dragged.Y;
                double dx = Math.Abs(outLeft) <= Math.Abs(outRight) ? outLeft : outRight;
                double dy = Math.Abs(outUp) <= Math.Abs(outDown) ? outUp : outDown;
                if (Math.Abs(dx) <= Math.Abs(dy)) dragged.X += dx; else dragged.Y += dy;
                moved = true;
            }
            if (!moved) break;
        }
        return dragged;
    }

    private void CommitDraggedArrangement(Controls.Canvas canvas)
    {
        if (_previewScale <= 0) return;
        foreach (Controls.Border display in canvas.Children.OfType<Controls.Border>())
        {
            if (display.Tag is not string devicePath) continue;
            // A neighbour still gliding would report its animated position rather than the one
            // it is settling on, and that halfway value would be saved as the real layout.
            display.BeginAnimation(Controls.Canvas.LeftProperty, null);
            display.BeginAnimation(Controls.Canvas.TopProperty, null);
            double worldX = _previewMinX + (Controls.Canvas.GetLeft(display) - _previewOffsetX) / _previewScale;
            double worldY = _previewMinY + (Controls.Canvas.GetTop(display) - _previewOffsetY) / _previewScale;
            _setupCustomPositions[devicePath] = new MonitorPosition((int)Math.Round(worldX), (int)Math.Round(worldY));
        }

        if (SetupGuideLayoutPicker.ItemsSource is IEnumerable<DisplayLayoutOption> options)
        {
            DisplayLayoutOption? custom = options.FirstOrDefault(option => option.Value == DisplayLayoutMode.Custom);
            if (custom is not null && !ReferenceEquals(SetupGuideLayoutPicker.SelectedItem, custom))
            {
                // Assigning SelectedItem re-enters SelectionChanged, which would wipe the
                // positions just captured, so refresh explicitly instead of falling through.
                SetupGuideLayoutPicker.SelectedItem = custom;
                return;
            }
        }
        UpdateSetupPreview();
    }

    private Controls.Border CreateDetailRow(Fluent.SymbolRegular symbol, Wpf.UIElement primary, string secondary)
    {
        Media.SolidColorBrush hoverBrush = NewBrush("#0016191D");
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
                new Animation.ColorAnimation(Media.Color.FromArgb(48, 58, 66, 76), TimeSpan.FromMilliseconds(120)));
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
        PopulateSessionOptions(profile);
        ReadinessSummary.Text = "Check displays, sound, applications, and controllers without switching.";
        ReadinessSummary.Foreground = Brush("MutedBrush");
        ReadinessItemsPanel.Children.Clear();

        HotkeyInput.Text = profile.Hotkey;
        _hotkeyCommitted = profile.Hotkey;
        AppsPanel.Children.Clear();
        foreach (AppRule app in profile.Apps) AddAppEditor(app);
        if (profile.Apps.Count == 0) AddListEmptyState(AppsPanel, "No applications added");
    }

    private void RefreshGamesProfilePicker()
    {
        _refreshingFeaturePicker = true;
        try
        {
            List<ProfilePickerOption> options = _coordinator.Document.Profiles
                .Select(profile => new ProfilePickerOption(profile.Id, profile.Name))
                .ToList();
            Guid? desired = options.Any(option => option.Id == _gamesProfileId)
                ? _gamesProfileId
                : PreferredFeatureProfileId(options);
            GamesProfilePicker.ItemsSource = options;
            GamesProfilePicker.SelectedItem = options.FirstOrDefault(option => option.Id == desired);
            _gamesProfileId = desired;
        }
        finally
        {
            _refreshingFeaturePicker = false;
        }

        PopulateGamesEditor(SelectedGamesProfile());
    }

    private void RefreshIntegrationsProfilePicker()
    {
        _refreshingFeaturePicker = true;
        try
        {
            List<ProfilePickerOption> options = _coordinator.Document.Profiles
                .Select(profile => new ProfilePickerOption(profile.Id, profile.Name))
                .ToList();
            Guid? desired = options.Any(option => option.Id == _integrationsProfileId)
                ? _integrationsProfileId
                : PreferredFeatureProfileId(options);
            IntegrationsProfilePicker.ItemsSource = options;
            IntegrationsProfilePicker.SelectedItem = options.FirstOrDefault(option => option.Id == desired);
            _integrationsProfileId = desired;
        }
        finally
        {
            _refreshingFeaturePicker = false;
        }

        PopulateDiscordEditor(SelectedIntegrationsProfile()?.Discord ?? new DiscordSettings());
    }

    private Guid? PreferredFeatureProfileId(IReadOnlyCollection<ProfilePickerOption> options)
    {
        Guid? active = _coordinator.Document.Runtime.ActiveProfileId;
        if (active.HasValue && options.Any(option => option.Id == active.Value)) return active;
        return options.FirstOrDefault()?.Id;
    }

    private Profile? SelectedGamesProfile() => _gamesProfileId.HasValue
        ? _coordinator.Document.Profiles.FirstOrDefault(profile => profile.Id == _gamesProfileId.Value)
        : null;

    private Profile? SelectedIntegrationsProfile() => _integrationsProfileId.HasValue
        ? _coordinator.Document.Profiles.FirstOrDefault(profile => profile.Id == _integrationsProfileId.Value)
        : null;

    private void PopulateGamesEditor(Profile? profile)
    {
        GamesPanel.Children.Clear();
        if (profile is null)
        {
            AddListEmptyState(GamesPanel, "Create a setup before adding game presets");
            return;
        }

        foreach (GamePreset preset in profile.GamePresets) AddGameEditor(preset);
        if (profile.GamePresets.Count == 0) AddListEmptyState(GamesPanel, "No game presets for this setup");
    }

    private void PopulateDiscordEditor(DiscordSettings settings)
    {
        settings ??= new DiscordSettings();
        DiscordLaunchToggle.IsChecked = settings.LaunchOnActivate;
        DiscordVolumeInput.Text = settings.VolumePercent?.ToString() ?? string.Empty;
        DiscordMuteHotkeyInput.Text = settings.MuteToggleHotkey;
        DiscordDeafenHotkeyInput.Text = settings.DeafenToggleHotkey;
        DiscordOutputPicker.ItemsSource = CreateAudioPickerOptions(false, settings.OutputDeviceId, "Use setup output");
        DiscordOutputPicker.SelectedItem = SelectAudioOption(DiscordOutputPicker.ItemsSource, settings.OutputDeviceId);
        DiscordMicrophonePicker.ItemsSource = CreateAudioPickerOptions(true, settings.MicrophoneDeviceId, "Use setup microphone");
        DiscordMicrophonePicker.SelectedItem = SelectAudioOption(DiscordMicrophonePicker.ItemsSource, settings.MicrophoneDeviceId);
    }

    private List<AudioPickerOption> CreateAudioPickerOptions(bool capture, string? savedId, string inheritedLabel)
    {
        List<AudioPickerOption> options =
        [
            new(null, inheritedLabel, inheritedLabel),
            .. _coordinator.ListAudioDevices(capture)
                .Select(device => new AudioPickerOption(device.Id, device.Name, device.Name))
        ];
        if (!string.IsNullOrWhiteSpace(savedId) &&
            !options.Any(option => string.Equals(option.Id, savedId, StringComparison.OrdinalIgnoreCase)))
        {
            options.Add(new AudioPickerOption(savedId, "Unavailable endpoint", "Saved endpoint (not connected)"));
        }
        return options;
    }

    private static AudioPickerOption SelectAudioOption(object? itemsSource, string? savedId)
    {
        List<AudioPickerOption> options = (itemsSource as IEnumerable<AudioPickerOption>)?.ToList() ?? [];
        return options.FirstOrDefault(option =>
                   string.Equals(option.Id ?? string.Empty, savedId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
               ?? options.First();
    }

    private void PopulateSessionOptions(Profile profile)
    {
        KeepAwakeToggle.IsChecked = profile.KeepAwake;

        List<PowerPlanPickerOption> powerPlans =
        [
            new(string.Empty, "Leave the current power plan unchanged"),
            .. _coordinator.ListPowerPlans().Select(plan =>
                new PowerPlanPickerOption(plan.Guid, plan.Name + (plan.IsActive ? " (active)" : string.Empty)))
        ];
        PowerPlanPicker.ItemsSource = powerPlans;
        PowerPlanPicker.SelectedItem = powerPlans.FirstOrDefault(option =>
            string.Equals(option.Guid, profile.PowerPlanGuid, StringComparison.OrdinalIgnoreCase)) ?? powerPlans[0];

        List<HdrPickerOption> hdrOptions =
        [
            new(null, "Leave HDR unchanged"),
            new(true, "Turn HDR on"),
            new(false, "Turn HDR off")
        ];
        HdrModePicker.ItemsSource = hdrOptions;
        HdrModePicker.SelectedItem = hdrOptions.First(option => option.Value == profile.EnableHdr);
        HdrStatus hdr = _coordinator.GetHdrStatus();
        HdrModePicker.IsEnabled = hdr.IsSupported;
        HdrCapabilityText.Text = hdr.IsSupported
            ? $"{hdr.SupportedDisplayCount} active HDR-capable display{(hdr.SupportedDisplayCount == 1 ? string.Empty : "s")} detected."
            : "No active HDR-capable display detected; this option will become available when one is connected.";

        PopulateExpectedControllers(profile);
    }

    private void PopulateExpectedControllers(Profile profile)
    {
        ExpectedControllersPanel.Children.Clear();
        Dictionary<string, bool> controllers = _coordinator.ListControllers()
            .Select(device => device.Name)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(name => name, _ => true, StringComparer.CurrentCultureIgnoreCase);
        foreach (string saved in profile.ExpectedControllers)
            controllers.TryAdd(saved, false);

        if (controllers.Count == 0)
        {
            ExpectedControllersPanel.Children.Add(Text(
                "No wheel, pedals, gamepad, or button box is connected.", 12, Brush("FaintBrush")));
            return;
        }

        foreach ((string name, bool connected) in controllers.OrderBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            Controls.CheckBox check = new()
            {
                Content = connected ? name : name + " (not connected)",
                Tag = name,
                IsChecked = profile.ExpectedControllers.Contains(name, StringComparer.CurrentCultureIgnoreCase),
                Margin = new Wpf.Thickness(0, 0, 0, 7),
                Foreground = connected ? Brush("TextBrush") : Brush("WarningBrush")
            };
            ExpectedControllersPanel.Children.Add(check);
        }
    }

    private void RefreshControllers_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (SelectedProfile() is Profile profile) PopulateExpectedControllers(profile);
    }

    private void SaveSessionOptions_Click(object sender, Wpf.RoutedEventArgs e)
    {
        Profile? profile = SelectedProfile();
        if (profile is null) return;
        profile.KeepAwake = KeepAwakeToggle.IsChecked == true;
        profile.PowerPlanGuid = (PowerPlanPicker.SelectedItem as PowerPlanPickerOption)?.Guid ?? string.Empty;
        profile.EnableHdr = (HdrModePicker.SelectedItem as HdrPickerOption)?.Value;
        profile.ExpectedControllers = ExpectedControllersPanel.Children.OfType<Controls.CheckBox>()
            .Where(check => check.IsChecked == true && check.Tag is string)
            .Select(check => (string)check.Tag)
            .ToList();
        try
        {
            _coordinator.SaveProfile(profile);
            ShowToast("Session options saved", profile.Name, "AccentBrush");
            PopulateSessionOptions(profile);
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void CheckReadiness_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (SelectedProfile() is Profile profile) ShowReadiness(_coordinator.CheckReadiness(profile));
    }

    private void ShowReadiness(ReadinessReport readiness)
    {
        ReadinessItemsPanel.Children.Clear();
        ReadinessSummary.Text = readiness.IsReady
            ? "Ready to switch."
            : readiness.CanSwitch ? "Ready with warnings." : "Not ready yet.";
        ReadinessSummary.Foreground = readiness.IsReady
            ? Brush("AccentBrush")
            : readiness.CanSwitch ? Brush("WarningBrush") : Brush("ErrorBrush");

        foreach (ReadinessItem item in readiness.Items)
        {
            Media.Brush color = item.Severity switch
            {
                OperationSeverity.Error => Brush("ErrorBrush"),
                OperationSeverity.Warning => Brush("WarningBrush"),
                _ => Brush("MutedBrush")
            };
            Controls.TextBlock line = Text($"{item.Area} — {item.Message}", 12, color);
            line.TextWrapping = Wpf.TextWrapping.Wrap;
            line.Margin = new Wpf.Thickness(0, 0, 0, 5);
            ReadinessItemsPanel.Children.Add(line);
        }
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
        Controls.TextBox order = CreateTextInput(rule.LaunchOrder.ToString());
        Controls.TextBox delay = CreateTextInput(rule.DelayAfterStartSeconds.ToString());
        Controls.TextBox readyTimeout = CreateTextInput(rule.ReadyTimeoutSeconds.ToString());
        Controls.TextBox volume = CreateTextInput(rule.VolumePercent?.ToString() ?? string.Empty);
        Controls.CheckBox start = Toggle("Start", rule.StartOnActivate);
        Controls.CheckBox close = Toggle("Close on leave", rule.CloseOnDeactivate);
        Controls.CheckBox force = Toggle("Force close", rule.ForceClose);
        Controls.CheckBox hidden = Toggle("Start hidden", rule.StartHidden);
        Controls.CheckBox waitForReady = Toggle("Wait until responsive", rule.WaitForReady);
        List<AudioPickerOption> audioOptions =
        [
            new(null, "System default", "Leave app audio route unchanged"),
            .. _coordinator.ListAudioDevices(false).Select(device => new AudioPickerOption(device.Id, device.Name, device.Name))
        ];
        if (!string.IsNullOrWhiteSpace(rule.AudioDeviceId) &&
            !audioOptions.Any(option => string.Equals(option.Id, rule.AudioDeviceId, StringComparison.OrdinalIgnoreCase)))
        {
            audioOptions.Add(new AudioPickerOption(rule.AudioDeviceId, "Unavailable endpoint", "Saved endpoint (not connected)"));
        }
        Controls.ComboBox appAudio = new()
        {
            Style = GetStyle("Picker"),
            ItemsSource = audioOptions,
            SelectedItem = audioOptions.FirstOrDefault(option =>
                string.Equals(option.Id ?? string.Empty, rule.AudioDeviceId ?? string.Empty, StringComparison.OrdinalIgnoreCase))
                ?? audioOptions[0],
            MinWidth = 230
        };
        AppEditorState state = new(
            path, arguments, start, close, force, hidden, order, delay, waitForReady, readyTimeout, appAudio, volume);

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

        Controls.Expander advanced = new()
        {
            Header = "Launch order and app audio",
            Margin = new Wpf.Thickness(0, 12, 0, 0),
            Foreground = Brush("MutedBrush")
        };
        Controls.Grid advancedGrid = new() { Margin = new Wpf.Thickness(0, 10, 0, 0) };
        advancedGrid.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(82) });
        advancedGrid.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(12) });
        advancedGrid.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(105) });
        advancedGrid.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(12) });
        advancedGrid.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        advancedGrid.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(12) });
        advancedGrid.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(82) });

        Controls.StackPanel orderField = LabeledField("Order", order);
        Controls.StackPanel delayField = LabeledField("Delay (sec)", delay);
        Controls.StackPanel audioField = LabeledField("Output device", appAudio);
        Controls.StackPanel volumeField = LabeledField("Volume %", volume);
        Controls.Grid.SetColumn(delayField, 2);
        Controls.Grid.SetColumn(audioField, 4);
        Controls.Grid.SetColumn(volumeField, 6);
        advancedGrid.Children.Add(orderField);
        advancedGrid.Children.Add(delayField);
        advancedGrid.Children.Add(audioField);
        advancedGrid.Children.Add(volumeField);
        advancedGrid.RowDefinitions.Add(new Controls.RowDefinition { Height = Wpf.GridLength.Auto });
        advancedGrid.RowDefinitions.Add(new Controls.RowDefinition { Height = Wpf.GridLength.Auto });
        Controls.StackPanel readiness = new() { Orientation = Controls.Orientation.Horizontal, Margin = new Wpf.Thickness(0, 10, 0, 0) };
        waitForReady.Margin = new Wpf.Thickness(0, 0, 18, 0);
        readyTimeout.Width = 72;
        readiness.Children.Add(waitForReady);
        readiness.Children.Add(Text("Timeout (sec)", 12, Brush("MutedBrush")));
        readyTimeout.Margin = new Wpf.Thickness(8, 0, 0, 0);
        readiness.Children.Add(readyTimeout);
        Controls.Grid.SetRow(readiness, 1);
        Controls.Grid.SetColumnSpan(readiness, 7);
        advancedGrid.Children.Add(readiness);
        advanced.Content = advancedGrid;
        Controls.Grid.SetRow(advanced, 4);
        layout.Children.Add(advanced);
        row.Child = layout;
        AppsPanel.Children.Add(row);
    }

    private Controls.StackPanel LabeledField(string label, Wpf.FrameworkElement control)
    {
        Controls.StackPanel panel = new();
        panel.Children.Add(Text(label, 11, Brush("MutedBrush")));
        control.Margin = new Wpf.Thickness(0, 5, 0, 0);
        panel.Children.Add(control);
        return panel;
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

    private void ToggleHotkeyInput_PreviewKeyDown(object sender, Input.KeyEventArgs e)
    {
        Input.Key key = e.Key == Input.Key.System ? e.SystemKey : e.Key;
        if (key == Input.Key.Escape) return;
        if (key == Input.Key.Tab && Input.Keyboard.Modifiers == Input.ModifierKeys.None) return;
        e.Handled = true;

        if (key is Input.Key.Back or Input.Key.Delete)
        {
            _toggleHotkeyCommitted = string.Empty;
            ToggleHotkeyInput.Text = string.Empty;
            return;
        }

        string prefix = ModifierPrefix(Input.Keyboard.Modifiers);
        if (IsModifierKey(key))
        {
            ToggleHotkeyInput.Text = prefix + "...";
            return;
        }

        int virtualKey = Input.KeyInterop.VirtualKeyFromKey(key);
        Keys keyCode = (Keys)virtualKey & Keys.KeyCode;
        if (virtualKey <= 0 || keyCode == Keys.None) return;
        bool isFunctionKey = keyCode is >= Keys.F1 and <= Keys.F24;
        Input.ModifierKeys primary = Input.ModifierKeys.Control | Input.ModifierKeys.Alt | Input.ModifierKeys.Windows;
        if (!isFunctionKey && (Input.Keyboard.Modifiers & primary) == Input.ModifierKeys.None)
        {
            ToggleHotkeyInput.Text = _toggleHotkeyCommitted;
            ShowToast("Add a modifier key", "Hold Ctrl, Alt, or Win with that key, or use a function key.", "WarningBrush");
            return;
        }

        string gesture = prefix + keyCode;
        if (!HotkeyParser.TryParse(gesture, out _, out string error))
        {
            ToggleHotkeyInput.Text = _toggleHotkeyCommitted;
            ShowToast("Hotkey not recorded", error, "WarningBrush");
            return;
        }
        _toggleHotkeyCommitted = gesture;
        ToggleHotkeyInput.Text = gesture;
    }

    private void ToggleHotkeyInput_PreviewKeyUp(object sender, Input.KeyEventArgs e) => RevertPartialToggleHotkey();

    private void ToggleHotkeyInput_LostKeyboardFocus(object sender, Input.KeyboardFocusChangedEventArgs e) =>
        RevertPartialToggleHotkey();

    private void RevertPartialToggleHotkey()
    {
        if (ToggleHotkeyInput.Text.EndsWith("...", StringComparison.Ordinal))
            ToggleHotkeyInput.Text = _toggleHotkeyCommitted;
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

    private void AddGameEditor(string process) => AddGameEditor(new GamePreset { ProcessName = process });

    private void AddGameEditor(GamePreset preset)
    {
        RemoveEmptyState(GamesPanel);
        Controls.ComboBox input = new()
        {
            Style = GetStyle("Picker"),
            IsEditable = true,
            ItemsSource = GameDetectionService.RunningProcessCandidates(),
            Text = preset.ProcessName,
            ToolTip = "Pick a running app or type the game's process name, for example acs.exe"
        };
        Wpf.Automation.AutomationProperties.SetName(input, "Game process");

        Controls.ComboBox gameOutput = AudioPicker(false, preset.AudioDeviceId, "Use setup output", "Game output device");
        Controls.TextBox gameVolume = CreateTextInput(preset.VolumePercent?.ToString() ?? string.Empty);
        Wpf.Automation.AutomationProperties.SetName(gameVolume, "Game volume");
        Controls.TextBox extraApps = CreateTextInput(string.Join(Environment.NewLine,
            preset.Apps.Select(app => app.ExecutablePath)));
        extraApps.AcceptsReturn = true;
        extraApps.TextWrapping = Wpf.TextWrapping.NoWrap;
        extraApps.Height = 72;
        extraApps.VerticalContentAlignment = Wpf.VerticalAlignment.Top;
        Wpf.Automation.AutomationProperties.SetName(extraApps, "Preset applications");
        Controls.CheckBox closeApps = Toggle("Close these apps when the game ends",
            preset.Apps.Any(app => app.CloseOnDeactivate));

        Controls.CheckBox customizeDiscord = Toggle("Customize Discord for this game", preset.CustomizeDiscord);
        Controls.CheckBox discordLaunch = Toggle("Launch Discord", preset.Discord.LaunchOnActivate);
        Controls.CheckBox discordMuteSession = Toggle("Mute for this game session", preset.ToggleDiscordMuteForSession);
        Controls.CheckBox discordDeafenSession = Toggle("Deafen for this game session", preset.ToggleDiscordDeafenForSession);
        Controls.ComboBox discordOutput = AudioPicker(false, preset.Discord.OutputDeviceId, "Inherit setup Discord output", "Game Discord output");
        Controls.ComboBox discordMicrophone = AudioPicker(true, preset.Discord.MicrophoneDeviceId, "Inherit setup Discord microphone", "Game Discord microphone");
        Controls.TextBox discordVolume = CreateTextInput(preset.Discord.VolumePercent?.ToString() ?? string.Empty);
        Wpf.Automation.AutomationProperties.SetName(discordVolume, "Game Discord volume");

        Controls.StackPanel discordFields = new() { Margin = new Wpf.Thickness(0, 10, 0, 0) };
        discordLaunch.Margin = new Wpf.Thickness(0, 0, 0, 10);
        discordFields.Children.Add(discordLaunch);
        Controls.WrapPanel discordActions = new() { Margin = new Wpf.Thickness(0, 0, 0, 10) };
        discordMuteSession.Margin = new Wpf.Thickness(0, 0, 22, 0);
        discordActions.Children.Add(discordMuteSession);
        discordActions.Children.Add(discordDeafenSession);
        discordFields.Children.Add(discordActions);
        Controls.Grid discordGrid = new();
        discordGrid.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        discordGrid.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(10) });
        discordGrid.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        discordGrid.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(10) });
        discordGrid.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(82) });
        Controls.StackPanel discordOutputField = LabeledField("Output", discordOutput);
        Controls.StackPanel discordMicField = LabeledField("Microphone", discordMicrophone);
        Controls.StackPanel discordVolumeField = LabeledField("Volume %", discordVolume);
        Controls.Grid.SetColumn(discordMicField, 2);
        Controls.Grid.SetColumn(discordVolumeField, 4);
        discordGrid.Children.Add(discordOutputField);
        discordGrid.Children.Add(discordMicField);
        discordGrid.Children.Add(discordVolumeField);
        discordFields.Children.Add(discordGrid);
        discordFields.Children.Add(Text(
            "Microphone follows Windows communications input when Discord is set to Default.",
            11, Brush("FaintBrush")));
        discordFields.IsEnabled = preset.CustomizeDiscord;
        customizeDiscord.Checked += (_, _) => discordFields.IsEnabled = true;
        customizeDiscord.Unchecked += (_, _) => discordFields.IsEnabled = false;

        GameEditorState state = new(
            preset.Id,
            input,
            gameOutput,
            gameVolume,
            extraApps,
            closeApps,
            customizeDiscord,
            discordLaunch,
            discordMuteSession,
            discordDeafenSession,
            discordOutput,
            discordMicrophone,
            discordVolume);
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
        layout.RowDefinitions.Add(new Controls.RowDefinition { Height = Wpf.GridLength.Auto });
        layout.RowDefinitions.Add(new Controls.RowDefinition { Height = Wpf.GridLength.Auto });
        Controls.Grid header = new();
        header.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        header.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = Wpf.GridLength.Auto });
        header.Children.Add(input);
        Controls.Button remove = IconButton(Fluent.SymbolRegular.Delete24, "Remove game preset");
        remove.Margin = new Wpf.Thickness(8, 1, 0, 1);
        remove.Click += (_, _) =>
        {
            GamesPanel.Children.Remove(row);
            if (GamesPanel.Children.Count == 0) AddListEmptyState(GamesPanel, "No game processes added");
        };
        Controls.Grid.SetColumn(remove, 1);
        header.Children.Add(remove);
        layout.Children.Add(header);

        Controls.StackPanel fields = new() { Margin = new Wpf.Thickness(0, 10, 0, 0) };
        Controls.Grid gameAudioGrid = new();
        gameAudioGrid.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        gameAudioGrid.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(10) });
        gameAudioGrid.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(90) });
        Controls.StackPanel gameOutputField = LabeledField("Game output", gameOutput);
        Controls.StackPanel gameVolumeField = LabeledField("Game volume %", gameVolume);
        Controls.Grid.SetColumn(gameVolumeField, 2);
        gameAudioGrid.Children.Add(gameOutputField);
        gameAudioGrid.Children.Add(gameVolumeField);
        fields.Children.Add(gameAudioGrid);
        Controls.TextBlock appsLabel = Text("Apps to start (one .exe or link per line)", 11, Brush("MutedBrush"));
        appsLabel.Margin = new Wpf.Thickness(0, 12, 0, 5);
        fields.Children.Add(appsLabel);
        fields.Children.Add(extraApps);
        Controls.Grid appActions = new() { Margin = new Wpf.Thickness(0, 8, 0, 0) };
        closeApps.VerticalAlignment = Wpf.VerticalAlignment.Center;
        appActions.Children.Add(closeApps);
        Controls.Button browsePresetApp = Button("Browse app", "QuietButton");
        browsePresetApp.HorizontalAlignment = Wpf.HorizontalAlignment.Right;
        Wpf.Automation.AutomationProperties.SetName(browsePresetApp, "Browse preset application");
        browsePresetApp.Click += (_, _) =>
        {
            using Forms.OpenFileDialog dialog = new()
            {
                Title = "Choose an application for this game preset",
                Filter = "Applications (*.exe)|*.exe|All files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(_owner) != Forms.DialogResult.OK) return;
            string separator = string.IsNullOrWhiteSpace(extraApps.Text) ? string.Empty : Environment.NewLine;
            extraApps.Text += separator + dialog.FileName;
        };
        appActions.Children.Add(browsePresetApp);
        fields.Children.Add(appActions);
        customizeDiscord.Margin = new Wpf.Thickness(0, 14, 0, 0);
        fields.Children.Add(customizeDiscord);
        fields.Children.Add(discordFields);

        Controls.Expander advanced = new()
        {
            Header = preset.HasOverrides ? "Game preset · customized" : "Customize this game",
            Content = fields,
            Margin = new Wpf.Thickness(0, 5, 0, 0),
            Foreground = Brush("MutedBrush")
        };
        Controls.Grid.SetRow(advanced, 1);
        layout.Children.Add(advanced);
        row.Child = layout;
        GamesPanel.Children.Add(row);
    }

    private Controls.ComboBox AudioPicker(bool capture, string? savedId, string inheritedLabel, string automationName)
    {
        List<AudioPickerOption> options = CreateAudioPickerOptions(capture, savedId, inheritedLabel);
        Controls.ComboBox picker = new()
        {
            Style = GetStyle("Picker"),
            ItemsSource = options,
            SelectedItem = SelectAudioOption(options, savedId)
        };
        Wpf.Automation.AutomationProperties.SetName(picker, automationName);
        return picker;
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
        _gameExitGraceSeconds = Math.Clamp(settings.GameExitGraceSeconds, 0, 120);
        GraceValue.Text = _gameExitGraceSeconds == 1
            ? "1 sec"
            : $"{_gameExitGraceSeconds} sec";
        _toggleHotkeyCommitted = settings.ToggleHotkey;
        ToggleHotkeyInput.Text = settings.ToggleHotkey;
        DiagnosticsVersionText.Text = $"{AppInfo.ProductName} {AppInfo.Version}";
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
        AppPage active = _page == AppPage.Profile ? AppPage.Home : _page;
        SetupsNav.Foreground = active == AppPage.Home ? Brush("AccentBrush") : Brush("MutedBrush");
        GamesNav.Foreground = active == AppPage.Games ? Brush("AccentBrush") : Brush("MutedBrush");
        IntegrationsNav.Foreground = active == AppPage.Integrations ? Brush("AccentBrush") : Brush("MutedBrush");
        SettingsNav.Foreground = active == AppPage.Settings ? Brush("AccentBrush") : Brush("MutedBrush");
        double target = active switch
        {
            AppPage.Games => 38,
            AppPage.Integrations => 76,
            AppPage.Settings => 114,
            _ => 0
        };
        AnimateNavigationIndicator(target);
    }

    private void AnimateNavigationIndicator(double targetY)
    {
        Media.TranslateTransform pill = EnsureTranslate(NavActivePill);
        if (!_navIndicatorInitialized || !IsLoaded)
        {
            pill.BeginAnimation(Media.TranslateTransform.YProperty, null);
            pill.Y = targetY;
            _navIndicatorInitialized = true;
            return;
        }

        double direction = Math.Sign(targetY - pill.Y);
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
        pill.BeginAnimation(Media.TranslateTransform.YProperty, animation);
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
        AppPage.Games => GamesPage,
        AppPage.Integrations => IntegrationsPage,
        AppPage.Settings => SettingsPage,
        _ => HomePage
    };

    private void ShowHardwareTab()
    {
        SwapProfilePanel(AutomationPanel, SnapshotPanel);
        SnapshotIndicator.Visibility = Wpf.Visibility.Visible;
        AutomationIndicator.Visibility = Wpf.Visibility.Collapsed;
        // Active pill is accent-filled, so its label needs the dark ink to stay legible.
        SnapshotTab.Foreground = Brush("AccentInkBrush");
        AutomationTab.Foreground = Brush("MutedBrush");
    }

    private void ShowAutomationTab()
    {
        SwapProfilePanel(SnapshotPanel, AutomationPanel);
        SnapshotIndicator.Visibility = Wpf.Visibility.Collapsed;
        AutomationIndicator.Visibility = Wpf.Visibility.Visible;
        SnapshotTab.Foreground = Brush("MutedBrush");
        AutomationTab.Foreground = Brush("AccentInkBrush");
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
        // Only on entering the page: a routine refresh would discard an update the user just found.
        RefreshUpdateSection();
        Navigate(AppPage.Settings);
    }

    private void Games_Click(object sender, Wpf.RoutedEventArgs e)
    {
        RefreshGamesProfilePicker();
        Navigate(AppPage.Games);
    }

    private void Integrations_Click(object sender, Wpf.RoutedEventArgs e)
    {
        RefreshIntegrationsProfilePicker();
        Navigate(AppPage.Integrations);
    }

    private void GamesProfilePicker_SelectionChanged(object sender, Controls.SelectionChangedEventArgs e)
    {
        if (_refreshingFeaturePicker) return;
        _gamesProfileId = (GamesProfilePicker.SelectedItem as ProfilePickerOption)?.Id;
        PopulateGamesEditor(SelectedGamesProfile());
    }

    private void IntegrationsProfilePicker_SelectionChanged(object sender, Controls.SelectionChangedEventArgs e)
    {
        if (_refreshingFeaturePicker) return;
        _integrationsProfileId = (IntegrationsProfilePicker.SelectedItem as ProfilePickerOption)?.Id;
        PopulateDiscordEditor(SelectedIntegrationsProfile()?.Discord ?? new DiscordSettings());
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

    /// <summary>Opens the installed-game picker. Scanning runs off the UI thread so the dialog appears instantly.</summary>
    private async void AddGame_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (_dialogMode != DialogMode.None) return;
        _dialogMode = DialogMode.GamePicker;

        GamePickerList.Children.Clear();
        GamePickerScroll.Visibility = Wpf.Visibility.Collapsed;
        GamePickerScanning.Visibility = Wpf.Visibility.Visible;
        GamePickerHint.Text = "Not seeing your game? Use Choose custom path.";
        GamePickerLayer.Visibility = Wpf.Visibility.Visible;
        GamePickerLayer.BeginAnimation(OpacityProperty, DoubleAnimationTo(1, 150));

        Animation.DoubleAnimation spin = new(0, 360, TimeSpan.FromMilliseconds(900))
        {
            RepeatBehavior = Animation.RepeatBehavior.Forever
        };
        GamePickerSpinner.BeginAnimation(Media.RotateTransform.AngleProperty, spin);

        List<GameEntry> games = [];
        try
        {
            games = await Task.Run(() => new GameLibraryService().Scan());
        }
        catch (Exception ex)
        {
            AppLog.Error("Game scan failed: " + ex.Message);
        }

        if (_dialogMode != DialogMode.GamePicker) return;
        GamePickerSpinner.BeginAnimation(Media.RotateTransform.AngleProperty, null);
        GamePickerScanning.Visibility = Wpf.Visibility.Collapsed;

        HashSet<string> already = GamesPanel.Children.OfType<Controls.Border>()
            .Select(row => (row.Tag as GameEditorState)?.Process.Text.Trim() ?? string.Empty)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (games.Count == 0)
        {
            GamePickerHint.Text = "No installed games found. Use Choose custom path to pick the game's .exe yourself.";
        }
        else
        {
            foreach (GameEntry game in games) GamePickerList.Children.Add(CreateGameRow(game, already));
            GamePickerScroll.Visibility = Wpf.Visibility.Visible;
        }
    }

    private Controls.Border CreateGameRow(GameEntry game, HashSet<string> alreadyAdded)
    {
        bool added = alreadyAdded.Contains(game.ProcessName);
        Media.SolidColorBrush background = NewBrush("#1A1E22");
        Controls.Border row = new()
        {
            CornerRadius = new Wpf.CornerRadius(8),
            Background = background,
            Padding = new Wpf.Thickness(11, 9, 11, 9),
            Margin = new Wpf.Thickness(0, 0, 0, 7)
        };

        Controls.Grid layout = new();
        layout.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(46) });
        layout.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = new Wpf.GridLength(1, Wpf.GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new Controls.ColumnDefinition { Width = Wpf.GridLength.Auto });

        Controls.Border iconHost = new()
        {
            Width = 36,
            Height = 36,
            CornerRadius = new Wpf.CornerRadius(6),
            Background = Brush("SurfaceRaisedBrush"),
            VerticalAlignment = Wpf.VerticalAlignment.Center,
            ClipToBounds = true
        };
        Media.ImageSource? icon = TryGetExecutableIcon(game.ExecutablePath);
        if (icon is not null)
        {
            iconHost.Child = new Controls.Image { Source = icon, Width = 30, Height = 30 };
        }
        else
        {
            Controls.TextBlock initial = Text(game.Name[..1].ToUpperInvariant(), 15, Brush("MutedBrush"), Wpf.FontWeights.SemiBold);
            initial.HorizontalAlignment = Wpf.HorizontalAlignment.Center;
            initial.VerticalAlignment = Wpf.VerticalAlignment.Center;
            iconHost.Child = initial;
        }
        layout.Children.Add(iconHost);

        Controls.StackPanel copy = new() { VerticalAlignment = Wpf.VerticalAlignment.Center, Margin = new Wpf.Thickness(0, 0, 8, 0) };
        Controls.StackPanel titleRow = new() { Orientation = Controls.Orientation.Horizontal };
        Controls.TextBlock name = Text(game.Name, 13.5, Brush("TextBrush"), Wpf.FontWeights.SemiBold);
        name.TextTrimming = Wpf.TextTrimming.CharacterEllipsis;
        titleRow.Children.Add(name);
        if (game.IsSimRacing)
        {
            Controls.Border tag = CreateBadge("SIM", "AccentBrush", "AccentSoftBrush");
            tag.Margin = new Wpf.Thickness(7, 0, 0, 0);
            titleRow.Children.Add(tag);
        }
        copy.Children.Add(titleRow);
        Controls.TextBlock path = Text(game.ExecutablePath, 11, Brush("MutedBrush"));
        path.TextTrimming = Wpf.TextTrimming.CharacterEllipsis;
        path.Margin = new Wpf.Thickness(0, 2, 0, 0);
        path.ToolTip = game.ExecutablePath;
        copy.Children.Add(path);
        Controls.Grid.SetColumn(copy, 1);
        layout.Children.Add(copy);

        Controls.Button add = new()
        {
            Style = GetStyle(added ? "SecondaryButton" : "PrimaryButton"),
            Content = added ? "Added" : "+",
            Width = added ? 68 : 34,
            Height = 34,
            IsEnabled = !added,
            VerticalAlignment = Wpf.VerticalAlignment.Center
        };
        Wpf.Automation.AutomationProperties.SetName(add, "Add " + game.Name);
        add.Click += (_, _) =>
        {
            AddGameEditor(game.ProcessName);
            add.Content = "Added";
            add.Width = 68;
            add.IsEnabled = false;
            add.Style = GetStyle("SecondaryButton");
            ShowToast("Game added", $"Save game presets to switch to this setup when {game.Name} starts.", "AccentBrush");
        };
        Controls.Grid.SetColumn(add, 2);
        layout.Children.Add(add);

        row.Child = layout;
        row.MouseEnter += (_, _) => background.BeginAnimation(Media.SolidColorBrush.ColorProperty,
            new Animation.ColorAnimation(Media.Color.FromRgb(35, 42, 49), TimeSpan.FromMilliseconds(130)));
        row.MouseLeave += (_, _) => background.BeginAnimation(Media.SolidColorBrush.ColorProperty,
            new Animation.ColorAnimation(Media.Color.FromRgb(26, 30, 34), TimeSpan.FromMilliseconds(150)));
        return row;
    }

    /// <summary>Pulls the game's own icon out of its executable so the list is recognisable at a glance.</summary>
    private static Media.ImageSource? TryGetExecutableIcon(string executablePath)
    {
        try
        {
            using System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(executablePath);
            if (icon is null) return null;
            Media.Imaging.BitmapSource source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Wpf.Int32Rect.Empty,
                Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
    }

    private void GamePickerBrowse_Click(object sender, Wpf.RoutedEventArgs e)
    {
        CloseGamePicker();
        BrowseGameApplication_Click(sender, e);
    }

    private void GamePickerCancel_Click(object sender, Wpf.RoutedEventArgs e) => CloseGamePicker();

    private void GamePickerLayer_MouseLeftButtonDown(object sender, Input.MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, GamePickerLayer)) CloseGamePicker();
    }

    private void CloseGamePicker()
    {
        if (_dialogMode == DialogMode.GamePicker) _dialogMode = DialogMode.None;
        GamePickerSpinner.BeginAnimation(Media.RotateTransform.AngleProperty, null);
        Animation.DoubleAnimation fade = DoubleAnimationTo(0, 130);
        fade.Completed += (_, _) => GamePickerLayer.Visibility = Wpf.Visibility.Collapsed;
        GamePickerLayer.BeginAnimation(OpacityProperty, fade);
    }

    private void BrowseGameApplication_Click(object sender, Wpf.RoutedEventArgs e)
    {
        using Forms.OpenFileDialog dialog = new()
        {
            Title = "Choose the game or application to watch for",
            Filter = "Applications (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(_owner) != Forms.DialogResult.OK) return;

        string process = GameDetectionService.NormalizeProcessName(dialog.FileName);
        if (string.IsNullOrWhiteSpace(process)) return;
        if (GamesPanel.Children.OfType<Controls.Border>()
            .Select(row => row.Tag as GameEditorState)
            .Any(state => state is not null &&
                          string.Equals(state.Process.Text.Trim(), process, StringComparison.OrdinalIgnoreCase)))
        {
            ShowToast("Already watched", $"{process} is already in this setup's list.", "WarningBrush");
            return;
        }

        AddGameEditor(process);
        ShowToast("Game added", $"Save game presets to switch to this setup when {process} starts.", "AccentBrush");
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
                StartHidden = state.Hidden.IsChecked == true,
                LaunchOrder = ParseBoundedInt(state.Order.Text, 0, 999, 0),
                DelayAfterStartSeconds = ParseBoundedInt(state.Delay.Text, 0, 300, 0),
                WaitForReady = state.WaitForReady.IsChecked == true,
                ReadyTimeoutSeconds = ParseBoundedInt(state.ReadyTimeout.Text, 1, 300, 15),
                AudioDeviceId = (state.AudioDevice.SelectedItem as AudioPickerOption)?.Id ?? string.Empty,
                VolumePercent = string.IsNullOrWhiteSpace(state.Volume.Text)
                    ? null
                    : ParseBoundedInt(state.Volume.Text, 0, 100, 100)
            })
            .ToList();

        string previousHotkey = profile.Hotkey;
        List<AppRule> previousApps = profile.Apps;
        try
        {
            profile.Hotkey = hotkey;
            profile.Apps = apps;
            _coordinator.SaveProfile(profile);
            ShowToast("Applications saved", profile.Name, "AccentBrush");
        }
        catch (Exception ex)
        {
            profile.Hotkey = previousHotkey;
            profile.Apps = previousApps;
            PopulateProfile();
            ShowError(ex.Message);
        }
    }

    private void SaveGames_Click(object sender, Wpf.RoutedEventArgs e)
    {
        Profile? profile = SelectedGamesProfile();
        if (profile is null)
        {
            ShowError("Create a setup before adding game presets.");
            return;
        }

        List<GamePreset> gamePresets = GamesPanel.Children.OfType<Controls.Border>()
            .Select(row => row.Tag as GameEditorState)
            .Where(state => state is not null && !string.IsNullOrWhiteSpace(state.Process.Text))
            .Select(state => new GamePreset
            {
                Id = state!.Id == Guid.Empty ? Guid.NewGuid() : state.Id,
                ProcessName = GameDetectionService.NormalizeProcessName(state.Process.Text),
                AudioDeviceId = (state.GameOutput.SelectedItem as AudioPickerOption)?.Id ?? string.Empty,
                VolumePercent = string.IsNullOrWhiteSpace(state.GameVolume.Text)
                    ? null
                    : ParseBoundedInt(state.GameVolume.Text, 0, 100, 100),
                Apps = state.ExtraApps.Text
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(path => new AppRule
                    {
                        ExecutablePath = path,
                        StartOnActivate = true,
                        CloseOnDeactivate = state.CloseApps.IsChecked == true
                    })
                    .ToList(),
                CustomizeDiscord = state.CustomizeDiscord.IsChecked == true,
                ToggleDiscordMuteForSession = state.CustomizeDiscord.IsChecked == true && state.DiscordMuteSession.IsChecked == true,
                ToggleDiscordDeafenForSession = state.CustomizeDiscord.IsChecked == true && state.DiscordDeafenSession.IsChecked == true,
                Discord = new DiscordSettings
                {
                    LaunchOnActivate = state.DiscordLaunch.IsChecked == true,
                    OutputDeviceId = (state.DiscordOutput.SelectedItem as AudioPickerOption)?.Id ?? string.Empty,
                    MicrophoneDeviceId = (state.DiscordMicrophone.SelectedItem as AudioPickerOption)?.Id ?? string.Empty,
                    VolumePercent = string.IsNullOrWhiteSpace(state.DiscordVolume.Text)
                        ? null
                        : ParseBoundedInt(state.DiscordVolume.Text, 0, 100, 100)
                }
            })
            .ToList();
        if (gamePresets.Any(preset => preset.ToggleDiscordMuteForSession) &&
            string.IsNullOrWhiteSpace(profile.Discord.MuteToggleHotkey))
        {
            ShowError("Save the Discord mute keybind in Integrations before enabling a game's mute action.");
            return;
        }
        if (gamePresets.Any(preset => preset.ToggleDiscordDeafenForSession) &&
            string.IsNullOrWhiteSpace(profile.Discord.DeafenToggleHotkey))
        {
            ShowError("Save the Discord deafen keybind in Integrations before enabling a game's deafen action.");
            return;
        }
        List<string> gameProcesses = gamePresets.Select(preset => preset.ProcessName).ToList();
        string? duplicateProcess = gameProcesses
            .GroupBy(GameDetectionService.NormalizeProcessName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1)?.Key;
        if (!string.IsNullOrWhiteSpace(duplicateProcess))
        {
            ShowToast(
                "Duplicate game preset",
                $"{duplicateProcess} appears more than once in this setup. Keep one preset for that process.",
                "WarningBrush");
            return;
        }

        List<(string Process, string Profile)> conflicts = _coordinator.Document.Profiles
            .Where(other => other.Id != profile.Id)
            .SelectMany(other => other.GameProcesses
                .Where(process => gameProcesses.Contains(process, StringComparer.OrdinalIgnoreCase))
                .Select(process => (process, other.Name)))
            .ToList();
        if (conflicts.Count > 0)
        {
            (string process, string otherProfile) = conflicts[0];
            ShowToast(
                "Game already assigned",
                $"{process} already switches to {otherProfile}. Remove it there before assigning it to {profile.Name}.",
                "WarningBrush");
            return;
        }

        List<string> previousGameProcesses = profile.GameProcesses;
        List<GamePreset> previousGamePresets = profile.GamePresets;
        try
        {
            profile.GameProcesses = gameProcesses;
            profile.GamePresets = gamePresets;
            _coordinator.SaveProfile(profile);
            if (TryArmGameDetection(profile)) return;
            ShowToast("Game presets saved", profile.Name, "AccentBrush");
        }
        catch (Exception ex)
        {
            profile.GameProcesses = previousGameProcesses;
            profile.GamePresets = previousGamePresets;
            PopulateGamesEditor(profile);
            ShowError(ex.Message);
        }
    }

    private void SaveDiscord_Click(object sender, Wpf.RoutedEventArgs e)
    {
        Profile? profile = SelectedIntegrationsProfile();
        if (profile is null)
        {
            ShowError("Create a setup before configuring Discord.");
            return;
        }

        foreach ((string Value, string Label, Controls.TextBox Input) in new[]
                 {
                     (DiscordMuteHotkeyInput.Text.Trim(), "Discord mute", DiscordMuteHotkeyInput),
                     (DiscordDeafenHotkeyInput.Text.Trim(), "Discord deafen", DiscordDeafenHotkeyInput)
                 })
        {
            if (string.IsNullOrWhiteSpace(Value) || HotkeyParser.TryParse(Value, out _, out string discordError)) continue;
            ShowError($"{Label} keybind: {discordError}");
            Input.Focus();
            return;
        }

        DiscordSettings discord = new()
        {
            LaunchOnActivate = DiscordLaunchToggle.IsChecked == true,
            OutputDeviceId = (DiscordOutputPicker.SelectedItem as AudioPickerOption)?.Id ?? string.Empty,
            MicrophoneDeviceId = (DiscordMicrophonePicker.SelectedItem as AudioPickerOption)?.Id ?? string.Empty,
            VolumePercent = string.IsNullOrWhiteSpace(DiscordVolumeInput.Text)
                ? null
                : ParseBoundedInt(DiscordVolumeInput.Text, 0, 100, 100),
            MuteToggleHotkey = DiscordMuteHotkeyInput.Text.Trim(),
            DeafenToggleHotkey = DiscordDeafenHotkeyInput.Text.Trim()
        };
        if (profile.GamePresets.Any(preset => preset.ToggleDiscordMuteForSession) &&
            string.IsNullOrWhiteSpace(discord.MuteToggleHotkey))
        {
            ShowError("This setup has a game preset that uses Discord mute. Enter its keybind first.");
            DiscordMuteHotkeyInput.Focus();
            return;
        }
        if (profile.GamePresets.Any(preset => preset.ToggleDiscordDeafenForSession) &&
            string.IsNullOrWhiteSpace(discord.DeafenToggleHotkey))
        {
            ShowError("This setup has a game preset that uses Discord deafen. Enter its keybind first.");
            DiscordDeafenHotkeyInput.Focus();
            return;
        }

        DiscordSettings previous = profile.Discord;
        try
        {
            profile.Discord = discord;
            _coordinator.SaveProfile(profile);
            ShowToast("Discord saved", profile.Name, "AccentBrush");
        }
        catch (Exception ex)
        {
            profile.Discord = previous;
            PopulateDiscordEditor(previous);
            ShowError(ex.Message);
        }
    }

    private static int ParseBoundedInt(string value, int minimum, int maximum, int fallback) =>
        int.TryParse(value, out int parsed) ? Math.Clamp(parsed, minimum, maximum) : fallback;

    // Adding a game to a setup is only meaningful with the master detection switch on, so turn it
    // on rather than saving a rule that silently never fires. Returns true when it reported itself.
    private bool TryArmGameDetection(Profile profile)
    {
        if (profile.GameProcesses.Count == 0 || _coordinator.Document.Settings.GameDetectionEnabled) return false;
        try
        {
            _coordinator.Document.Settings.GameDetectionEnabled = true;
            _coordinator.SaveSettings();
            RefreshSettings();
            AppLog.Info($"Game detection was switched on automatically because {profile.Name} now watches for a game.");
            ShowToast(
                "Game presets saved, detection on",
                $"PitLaunch now watches for {string.Join(", ", profile.GameProcesses)} and switches to {profile.Name}.",
                "AccentBrush");
            return true;
        }
        catch (Exception ex)
        {
            _coordinator.Document.Settings.GameDetectionEnabled = false;
            AppLog.Error("Could not switch game detection on automatically: " + ex.Message);
            ShowToast(
                "Automation saved, but detection is off",
                "Turn on Game detection on the Games page to make it switch automatically.",
                "WarningBrush");
            return true;
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

    private void GraceDown_Click(object sender, Wpf.RoutedEventArgs e)
    {
        _gameExitGraceSeconds = Math.Max(0, _gameExitGraceSeconds - 1);
        GraceValue.Text = _gameExitGraceSeconds == 1 ? "1 sec" : $"{_gameExitGraceSeconds} sec";
    }

    private void GraceUp_Click(object sender, Wpf.RoutedEventArgs e)
    {
        _gameExitGraceSeconds = Math.Min(120, _gameExitGraceSeconds + 1);
        GraceValue.Text = _gameExitGraceSeconds == 1 ? "1 sec" : $"{_gameExitGraceSeconds} sec";
    }

    private void RefreshUpdateSection()
    {
        UpdateVersionText.Text = $"{AppInfo.ProductName} {AppInfo.Version}";
        UpdateProgress.Visibility = Wpf.Visibility.Collapsed;

        // Keep an update the background check already found, so arriving from the sidebar
        // banner lands on a ready-to-use Install button instead of clearing it.
        if (_pendingUpdate is not null)
        {
            InstallUpdateButton.Visibility = Wpf.Visibility.Visible;
            UpdateStatusText.Text = $"Version {_pendingUpdate.TargetFullRelease.Version} is available.";
            return;
        }

        InstallUpdateButton.Visibility = Wpf.Visibility.Collapsed;
        UpdateStatusText.Text = _updates.IsInstalledCopy
            ? "Installed copy. Updates download only the parts that changed."
            : "Portable copy. Install with Setup.exe to get small automatic updates.";
    }

    internal void ApplyStartupUpdateStatus(UpdateStatus status)
    {
        RunOnUi(() =>
        {
            if (status.IsRequired)
            {
                ShowRequiredUpdate(status);
                return;
            }
            if (!status.CanInstall) return;
            _pendingUpdate = status.Update;
            ShowUpdateBanner(status.Update!.TargetFullRelease.Version.ToString());
        });
    }

    private void ShowUpdateBanner(string version)
    {
        UpdateBannerVersion.Text = $"Version {version}";
        if (UpdateBanner.Visibility == Wpf.Visibility.Visible) return;

        UpdateBanner.Visibility = Wpf.Visibility.Visible;
        UpdateBanner.BeginAnimation(OpacityProperty, StrongDoubleAnimationTo(1, 420));
        UpdateBannerSlide.BeginAnimation(Media.TranslateTransform.YProperty, StrongDoubleAnimationTo(0, 460));

        // A slow breathing halo reads as "waiting for you" without nagging.
        Animation.DoubleAnimation pulse = new(1, 1.45, TimeSpan.FromMilliseconds(1600))
        {
            AutoReverse = true,
            RepeatBehavior = Animation.RepeatBehavior.Forever,
            EasingFunction = new Animation.SineEase { EasingMode = Animation.EasingMode.EaseInOut }
        };
        UpdateBannerPulse.BeginAnimation(Media.ScaleTransform.ScaleXProperty, pulse);
        UpdateBannerPulse.BeginAnimation(Media.ScaleTransform.ScaleYProperty, pulse);
        Animation.DoubleAnimation fade = new(0.3, 0.05, TimeSpan.FromMilliseconds(1600))
        {
            AutoReverse = true,
            RepeatBehavior = Animation.RepeatBehavior.Forever,
            EasingFunction = new Animation.SineEase { EasingMode = Animation.EasingMode.EaseInOut }
        };
        UpdateBannerHalo.BeginAnimation(OpacityProperty, fade);
    }

    private void UpdateBanner_Click(object sender, Wpf.RoutedEventArgs e)
    {
        RefreshSettings();
        RefreshUpdateSection();
        Navigate(AppPage.Settings);
    }

    private async void CheckUpdate_Click(object sender, Wpf.RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        InstallUpdateButton.Visibility = Wpf.Visibility.Collapsed;
        UpdateStatusText.Text = "Checking for updates...";
        try
        {
            UpdateStatus status = await _updates.CheckAsync();
            _coordinator.SetSwitchBlockReason(status.IsRequired
                ? status.Message + " Update PitLaunch before switching setups."
                : null);
            UpdateStatusText.Text = status.Message;
            if (status.IsRequired)
            {
                ShowRequiredUpdate(status);
            }
            else if (status.CanInstall)
            {
                _pendingUpdate = status.Update;
                InstallUpdateButton.Visibility = Wpf.Visibility.Visible;
                ShowUpdateBanner(status.Update!.TargetFullRelease.Version.ToString());
                ShowToast("Update available", status.Message, "AccentBrush");
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "Could not check for updates: " + ex.Message;
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void ShowRequiredUpdate(UpdateStatus status)
    {
        _coordinator.SetSwitchBlockReason(
            status.Message + " Update PitLaunch before switching setups from any control.");
        _requiredUpdate = status;
        _pendingUpdate = status.Update;
        RequiredUpdateVersionText.Text = string.IsNullOrWhiteSpace(status.MinimumRequiredVersion)
            ? "A SUPPORTED RELEASE IS REQUIRED"
            : $"VERSION {status.MinimumRequiredVersion} OR NEWER REQUIRED";
        RequiredUpdateBody.Text = status.Message;
        RequiredUpdateButton.Content = status.CanInstall
            ? "Install update and restart"
            : "Open the download page";
        RequiredUpdateButton.IsEnabled = true;
        RequiredUpdateProgress.Visibility = Wpf.Visibility.Collapsed;
        RequiredUpdateLayer.Visibility = Wpf.Visibility.Visible;
        RequiredUpdateLayer.Focus();
    }

    private async void RequiredUpdate_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (_requiredUpdate is null) return;
        if (!_requiredUpdate.CanInstall || _requiredUpdate.Update is null)
        {
            try
            {
                using Process? process = Process.Start(new ProcessStartInfo
                {
                    FileName = _requiredUpdate.DownloadUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { RequiredUpdateBody.Text = "Could not open the download page: " + ex.Message; }
            return;
        }

        if (_coordinator.IsBusy)
        {
            RequiredUpdateBody.Text = "Wait for the current setup switch to finish, then install the required update.";
            return;
        }

        RequiredUpdateButton.IsEnabled = false;
        RequiredUpdateProgress.Visibility = Wpf.Visibility.Visible;
        RequiredUpdateProgress.Value = 0;
        RequiredUpdateBody.Text = "Downloading the required update...";
        string? error = await _updates.DownloadAsync(
            _requiredUpdate.Update,
            percent => RunOnUi(() => RequiredUpdateProgress.Value = percent));
        if (error is not null)
        {
            RequiredUpdateBody.Text = "Download failed: " + error;
            RequiredUpdateProgress.Visibility = Wpf.Visibility.Collapsed;
            RequiredUpdateButton.IsEnabled = true;
            return;
        }

        RequiredUpdateBody.Text = "Restarting to finish the update...";
        string? applyError = _updates.ApplyAndRestart(_requiredUpdate.Update);
        RequiredUpdateBody.Text = "Could not apply the update: " + applyError;
        RequiredUpdateProgress.Visibility = Wpf.Visibility.Collapsed;
        RequiredUpdateButton.IsEnabled = true;
    }

    private async void InstallUpdate_Click(object sender, Wpf.RoutedEventArgs e)
    {
        if (_pendingUpdate is null) return;
        if (_coordinator.IsBusy)
        {
            ShowToast("Switch in progress", "Wait for the current setup switch to finish, then install.", "WarningBrush");
            return;
        }

        InstallUpdateButton.IsEnabled = false;
        CheckUpdateButton.IsEnabled = false;
        UpdateProgress.Visibility = Wpf.Visibility.Visible;
        UpdateProgress.Value = 0;
        UpdateStatusText.Text = "Downloading update...";

        string? error = await _updates.DownloadAsync(_pendingUpdate,
            percent => RunOnUi(() => UpdateProgress.Value = percent));
        if (error is not null)
        {
            UpdateProgress.Visibility = Wpf.Visibility.Collapsed;
            UpdateStatusText.Text = "Download failed: " + error;
            InstallUpdateButton.IsEnabled = true;
            CheckUpdateButton.IsEnabled = true;
            return;
        }

        UpdateStatusText.Text = "Restarting to finish the update...";
        // Succeeds by restarting the process, so anything after this only runs on failure.
        string? applyError = _updates.ApplyAndRestart(_pendingUpdate);
        UpdateProgress.Visibility = Wpf.Visibility.Collapsed;
        UpdateStatusText.Text = "Could not apply the update: " + applyError;
        InstallUpdateButton.IsEnabled = true;
        CheckUpdateButton.IsEnabled = true;
    }

    private void SaveSettings_Click(object sender, Wpf.RoutedEventArgs e)
    {
        ApplyGeneralSettingsControls();
        try
        {
            _coordinator.SaveSettings();
            RefreshReliableStartupStatus();
            ShowToast("Settings saved", "Startup and safety preferences updated.", "AccentBrush");
        }
        catch (Exception ex)
        {
            RefreshSettings();
            ShowError(ex.Message);
        }
    }

    private void SaveGameDetection_Click(object sender, Wpf.RoutedEventArgs e)
    {
        ApplyGameSettingsControls();
        try
        {
            _coordinator.SaveSettings();
            ShowToast("Game detection saved", DetectionToggle.IsChecked == true
                ? $"Checking every {_pollSeconds} seconds."
                : "Automatic game switching is off.", "AccentBrush");
        }
        catch (Exception ex)
        {
            RefreshSettings();
            ShowError(ex.Message);
        }
    }

    private void SaveToggleHotkey_Click(object sender, Wpf.RoutedEventArgs e)
    {
        string toggleHotkey = ToggleHotkeyInput.Text.Trim();
        if (!string.IsNullOrWhiteSpace(toggleHotkey) &&
            !HotkeyParser.TryParse(toggleHotkey, out _, out string hotkeyError))
        {
            ShowError(hotkeyError);
            ToggleHotkeyInput.Focus();
            return;
        }

        string previous = _coordinator.Document.Settings.ToggleHotkey;
        try
        {
            _coordinator.Document.Settings.ToggleHotkey = toggleHotkey;
            _coordinator.SaveSettings();
            ShowToast("Shortcut saved", string.IsNullOrWhiteSpace(toggleHotkey)
                ? "Desk ↔ Rig keyboard shortcut cleared."
                : toggleHotkey, "AccentBrush");
        }
        catch (Exception ex)
        {
            _coordinator.Document.Settings.ToggleHotkey = previous;
            _toggleHotkeyCommitted = previous;
            ToggleHotkeyInput.Text = previous;
            ShowError(ex.Message);
        }
    }

    private async void InstallStreamDeckPlugin_Click(object sender, Wpf.RoutedEventArgs e)
    {
        InstallStreamDeckPluginButton.IsEnabled = false;
        InstallStreamDeckPluginButton.Content = "Preparing...";
        try
        {
            bool downloaded = await StreamDeckPluginInstaller.OpenInstallerAsync();
            ShowToast(
                "Stream Deck installer opened",
                downloaded
                    ? "Approve the PitLaunch plugin in Stream Deck to finish."
                    : "Approve the local PitLaunch plugin package in Stream Deck to finish.",
                "AccentBrush");
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not open the Stream Deck plugin installer: " + ex.Message);
            ShowError(
                "PitLaunch could not open the Stream Deck plugin. Install the Elgato Stream Deck software first, " +
                "then try again. If it is already installed, the plugin may not be attached to the latest PitLaunch release yet.");
        }
        finally
        {
            InstallStreamDeckPluginButton.Content = "Install plugin";
            InstallStreamDeckPluginButton.IsEnabled = true;
        }
    }

    private void ApplyGeneralSettingsControls()
    {
        AppSettings settings = _coordinator.Document.Settings;
        settings.LaunchOnStartup = StartupToggle.IsChecked == true;
        settings.StartMinimized = ChooserToggle.IsChecked != true;
        settings.ConfirmBeforeSwitch = ConfirmSwitchToggle.IsChecked == true;
    }

    private void ApplyGameSettingsControls()
    {
        AppSettings settings = _coordinator.Document.Settings;
        settings.GameDetectionEnabled = DetectionToggle.IsChecked == true;
        settings.GamePollSeconds = _pollSeconds;
        settings.GameExitGraceSeconds = _gameExitGraceSeconds;
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
                ApplyGeneralSettingsControls();
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

    private void StatusBarRepoLink_Click(object sender, Wpf.RoutedEventArgs e)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo
            {
                FileName = AppInfo.UpdateFeedUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void OpenData_Click(object sender, Wpf.RoutedEventArgs e) => OpenPath(AppPaths.DataDirectory);
    private void OpenLog_Click(object sender, Wpf.RoutedEventArgs e) => OpenPath(AppPaths.LogFile);

    private void ExportSupportBundle_Click(object sender, Wpf.RoutedEventArgs e)
    {
        using Forms.SaveFileDialog dialog = new()
        {
            Title = "Export PitLaunch support bundle",
            Filter = "ZIP archive (*.zip)|*.zip",
            AddExtension = true,
            DefaultExt = "zip",
            FileName = $"PitLaunch-support-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(_owner) != Forms.DialogResult.OK) return;
        try
        {
            string destination = dialog.FileName;
            if (File.Exists(destination))
            {
                string directory = Path.GetDirectoryName(destination) ?? AppPaths.DataDirectory;
                string stem = Path.GetFileNameWithoutExtension(destination);
                destination = Path.Combine(directory, $"{stem}-{DateTime.Now:HHmmss}.zip");
            }
            SupportBundleResult result = new SupportBundleService().Export(destination, _coordinator.Document);
            ShowToast("Support bundle exported", $"Saved {result.Entries.Count} sanitized diagnostic files.", "AccentBrush");
            OpenPath(Path.GetDirectoryName(result.FilePath) ?? AppPaths.DataDirectory);
        }
        catch (Exception ex) { ShowError("Could not export support bundle: " + ex.Message); }
    }

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
        if (RequiredUpdateLayer.Visibility == Wpf.Visibility.Visible)
        {
            e.Handled = true;
            return;
        }
        if (OnboardingLayer.Visibility == Wpf.Visibility.Visible)
        {
            e.Handled = true;
            return;
        }
        // Escape backs out one layer at a time. Without this the arrangement overlay would fall
        // through to the guide underneath and throw away a setup mid-creation.
        if (_arrangeOverlay is not null) CloseArrangementOverlay();
        else if (_dialogMode == DialogMode.SetupGuide && _setupGuideStep > 0)
            ShowSetupGuideStep(_setupGuideStep - 1, animate: true);
        else if (_dialogMode != DialogMode.None) CompleteDialog(false);
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
        Controls.CheckBox Hidden,
        Controls.TextBox Order,
        Controls.TextBox Delay,
        Controls.CheckBox WaitForReady,
        Controls.TextBox ReadyTimeout,
        Controls.ComboBox AudioDevice,
        Controls.TextBox Volume);

    private sealed record GameEditorState(
        Guid Id,
        Controls.ComboBox Process,
        Controls.ComboBox GameOutput,
        Controls.TextBox GameVolume,
        Controls.TextBox ExtraApps,
        Controls.CheckBox CloseApps,
        Controls.CheckBox CustomizeDiscord,
        Controls.CheckBox DiscordLaunch,
        Controls.CheckBox DiscordMuteSession,
        Controls.CheckBox DiscordDeafenSession,
        Controls.ComboBox DiscordOutput,
        Controls.ComboBox DiscordMicrophone,
        Controls.TextBox DiscordVolume);

    private sealed record AudioPickerOption(string? Id, string Name, string Display)
    {
        public override string ToString() => Display;
    }

    private sealed record ProfilePickerOption(Guid Id, string Name)
    {
        public override string ToString() => Name;
    }

    private sealed record SetupKindOption(SetupKind Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record RigDisplayOption(RigDisplayVariant Value, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record PowerPlanPickerOption(string Guid, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record HdrPickerOption(bool? Value, string Label)
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
