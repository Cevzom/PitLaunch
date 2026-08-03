using System.IO.Pipes;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PitLaunch;

internal enum LaunchRequestKind
{
    Show,
    Chooser,
    Background,
    ActivateProfile,
    CaptureProfile,
    RestoreDisplays,
    ScheduledStartup,
    InstallStartupTask,
    RemoveStartupTask,
    Exit,
    SelfTest,
    ScanGames
}

internal sealed class LaunchRequest
{
    public LaunchRequestKind Kind { get; set; } = LaunchRequestKind.Show;
    public string Value { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;

    public static LaunchRequest Parse(string[] args)
    {
        LaunchRequest request = new();
        for (int i = 0; i < args.Length; i++)
        {
            string argument = args[i];
            switch (argument.ToLowerInvariant())
            {
                case "--background":
                case "--minimized":
                    request.Kind = LaunchRequestKind.Background;
                    break;
                case "--show":
                    request.Kind = LaunchRequestKind.Show;
                    break;
                case "--chooser":
                    request.Kind = LaunchRequestKind.Chooser;
                    break;
                case "--profile" when i + 1 < args.Length:
                    request.Kind = LaunchRequestKind.ActivateProfile;
                    request.Value = args[++i];
                    break;
                case "--capture" when i + 1 < args.Length:
                    request.Kind = LaunchRequestKind.CaptureProfile;
                    request.Value = args[++i];
                    break;
                case "--restore-displays":
                    request.Kind = LaunchRequestKind.RestoreDisplays;
                    break;
                case "--scheduled-startup":
                    request.Kind = LaunchRequestKind.ScheduledStartup;
                    break;
                case "--install-startup-task":
                    request.Kind = LaunchRequestKind.InstallStartupTask;
                    if (i + 1 < args.Length) request.Value = args[++i];
                    break;
                case "--remove-startup-task":
                    request.Kind = LaunchRequestKind.RemoveStartupTask;
                    break;
                case "--exit":
                    request.Kind = LaunchRequestKind.Exit;
                    break;
                case "--self-test":
                    request.Kind = LaunchRequestKind.SelfTest;
                    break;
                case "--scan-games":
                    request.Kind = LaunchRequestKind.ScanGames;
                    break;
                case "--output" when i + 1 < args.Length:
                    request.OutputPath = args[++i];
                    break;
            }
        }

        return request;
    }
}

internal sealed class SingleInstance : IDisposable
{
    private const string MutexName = "Local\\PitLaunch.ProfileManager.v2";
    private const string PipeName = "PitLaunch.ProfileManager.v2";
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _serverTask;

    public bool IsPrimary { get; }

    public SingleInstance()
    {
        _mutex = new Mutex(true, MutexName, out bool createdNew);
        IsPrimary = createdNew;
    }

    public bool Forward(LaunchRequest request)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        Exception? lastError = null;
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            try
            {
                using NamedPipeClientStream client = new(".", PipeName, PipeDirection.Out, PipeOptions.None);
                client.Connect(500);
                using StreamWriter writer = new(client, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                writer.WriteLine(JsonSerializer.Serialize(request));
                return true;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                lastError = ex;
                Thread.Sleep(150);
            }
            catch (Exception ex)
            {
                AppLog.Error("Could not contact the running PitLaunch instance: " + ex.Message);
                return false;
            }
        }

        AppLog.Error("Could not contact the running PitLaunch instance: " +
                     (lastError?.Message ?? "the command pipe did not become available."));
        return false;
    }

    public void StartServer(Func<LaunchRequest, Task> handler)
    {
        if (!IsPrimary || _serverTask is not null) return;
        _serverTask = Task.Run(async () =>
        {
            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    await using NamedPipeServerStream server = new(
                        PipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_cancellation.Token).ConfigureAwait(false);
                    using StreamReader reader = new(server, Encoding.UTF8, false, 1024, true);
                    string? line = await reader.ReadLineAsync(_cancellation.Token).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    LaunchRequest? request = JsonSerializer.Deserialize<LaunchRequest>(line);
                    if (request is not null) await handler(request).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    AppLog.Error("PitLaunch command pipe failed: " + ex.Message);
                    await Task.Delay(250, _cancellation.Token).ConfigureAwait(false);
                }
            }
        });
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        try { _serverTask?.Wait(1500); } catch { }
        _cancellation.Dispose();
        if (IsPrimary)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex.Dispose();
    }
}

internal static class SelfTest
{
    public static int Run(string outputPath)
    {
        AppLog.SuppressWrites();
        List<object> checks = [];
        bool passed = true;

        void Check(string name, Action test)
        {
            try
            {
                test();
                checks.Add(new { name, passed = true, error = string.Empty });
            }
            catch (Exception ex)
            {
                passed = false;
                checks.Add(new { name, passed = false, error = ex.Message });
            }
        }

        Check("hotkey parser", () =>
        {
            if (!HotkeyParser.TryParse("Ctrl+Alt+D", out HotkeyGesture gesture, out _) || gesture.KeyCode != Keys.D)
                throw new InvalidOperationException("Valid hotkey was not parsed.");
            if (!HotkeyParser.TryParse("F9", out _, out _))
                throw new InvalidOperationException("A function-key hotkey was rejected.");
            if (HotkeyParser.TryParse("Ctrl+NoSuchKey", out _, out _))
                throw new InvalidOperationException("Invalid hotkey was accepted.");
            if (HotkeyParser.TryParse("K", out _, out _) || HotkeyParser.TryParse("Shift+K", out _, out _))
                throw new InvalidOperationException("An unsafe typing key was accepted as a global hotkey.");
            if (HotkeyParser.TryParse("Ctrl+LButton", out _, out _))
                throw new InvalidOperationException("A mouse button was accepted as a keyboard hotkey.");
            if (!HotkeyParser.TryParse(AppInfo.EmergencyDisplayHotkey, out HotkeyGesture emergency, out _) ||
                emergency.KeyCode != Keys.F12)
            {
                throw new InvalidOperationException("The emergency display hotkey is invalid.");
            }
        });

        Check("profile serialization", () =>
        {
            ProfileDocument document = new();
            document.Profiles.Add(new Profile
            {
                Name = "Self test",
                Kind = SetupKind.SimRacing,
                RigDisplay = RigDisplayVariant.TripleScreen
            });
            string json = JsonSerializer.Serialize(document);
            ProfileDocument? restored = JsonSerializer.Deserialize<ProfileDocument>(json);
            Profile? profile = restored?.Profiles.Single();
            if (profile?.Name != "Self test" ||
                profile.Kind != SetupKind.SimRacing ||
                profile.RigDisplay != RigDisplayVariant.TripleScreen)
                throw new InvalidOperationException("Profile round trip changed data.");
            if (JsonSerializer.Deserialize<AppSettings>("{}")?.ConfirmBeforeSwitch != true)
                throw new InvalidOperationException("Beta switch confirmation does not default to enabled.");
        });

        Check("startup launch requests", () =>
        {
            if (LaunchRequest.Parse(["--chooser"]).Kind != LaunchRequestKind.Chooser ||
                LaunchRequest.Parse(["--background"]).Kind != LaunchRequestKind.Background ||
                LaunchRequest.Parse(["--restore-displays"]).Kind != LaunchRequestKind.RestoreDisplays ||
                LaunchRequest.Parse([StartupTaskRegistration.ScheduledArgument]).Kind != LaunchRequestKind.ScheduledStartup ||
                LaunchRequest.Parse(["--install-startup-task", "S-1-5-21-1"]).Value != "S-1-5-21-1")
            {
                throw new InvalidOperationException("A startup launch command was not parsed.");
            }

            string executable = Path.Combine(Path.GetTempPath(), "PitLaunch startup test", "PitLaunch.exe");
            string chooser = StartupRegistration.BuildCommand(executable, false);
            string background = StartupRegistration.BuildCommand(executable, true);
            if (!StartupRegistration.IsRegistrationForExecutable(chooser, executable) ||
                !StartupRegistration.IsRegistrationForExecutable(background, executable))
            {
                throw new InvalidOperationException("A valid startup registration was not recognized.");
            }
            if (StartupRegistration.IsRegistrationForExecutable(chooser, Path.Combine(Path.GetTempPath(), "Moved", "PitLaunch.exe")) ||
                StartupRegistration.IsRegistrationForExecutable(chooser.Replace("--chooser", "--unknown"), executable))
            {
                throw new InvalidOperationException("A stale or malformed startup registration was accepted.");
            }
            if (!StartupTaskRegistration.IsActionForExecutable(executable, StartupTaskRegistration.ScheduledArgument, executable) ||
                StartupTaskRegistration.IsActionForExecutable(executable, "--background", executable) ||
                StartupTaskRegistration.IsActionForExecutable(Path.Combine(Path.GetTempPath(), "Moved", "PitLaunch.exe"), StartupTaskRegistration.ScheduledArgument, executable))
            {
                throw new InvalidOperationException("A scheduled startup action was matched incorrectly.");
            }
        });

        Check("settings persistence", () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "PitLaunch-settings-test-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(directory, "profiles.json");
            try
            {
                ProfileRepository repository = new(file);
                ProfileDocument document = new();
                document.Settings.LaunchOnStartup = true;
                document.Settings.StartMinimized = true;
                document.Settings.ConfirmBeforeSwitch = false;
                document.Settings.GameDetectionEnabled = true;
                document.Settings.GamePollSeconds = 7;
                repository.Save(document);

                AppSettings restored = new ProfileRepository(file).Load().Settings;
                if (!restored.LaunchOnStartup || !restored.StartMinimized || restored.ConfirmBeforeSwitch ||
                    !restored.GameDetectionEnabled || restored.GamePollSeconds != 7)
                {
                    throw new InvalidOperationException("Saved settings changed after a repository restart.");
                }
            }
            finally
            {
                try { Directory.Delete(directory, true); } catch { }
            }
        });

        Check("Windows shutdown close behavior", () =>
        {
            if (!MainForm.ShouldHideToTray(CloseReason.UserClosing))
                throw new InvalidOperationException("The close button would not hide PitLaunch to the tray.");
            if (MainForm.ShouldHideToTray(CloseReason.WindowsShutDown) ||
                MainForm.ShouldHideToTray(CloseReason.TaskManagerClosing) ||
                MainForm.ShouldHideToTray(CloseReason.ApplicationExitCall))
            {
                throw new InvalidOperationException("PitLaunch would block a Windows shutdown or explicit exit.");
            }
        });

        Check("profile backup recovery", () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "PitLaunch-self-test-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(directory, "profiles.json");
            try
            {
                ProfileRepository repository = new(file);
                ProfileDocument backupVersion = new();
                backupVersion.Profiles.Add(new Profile { Name = "Recover me" });
                repository.Save(backupVersion);

                ProfileDocument latestVersion = new();
                latestVersion.Profiles.Add(new Profile { Name = "Latest version" });
                repository.Save(latestVersion);
                File.WriteAllText(file, "{not valid json", new UTF8Encoding(false));

                ProfileDocument recovered = repository.Load();
                if (recovered.Profiles.SingleOrDefault()?.Name != "Recover me")
                    throw new InvalidOperationException("The valid profile backup was not recovered.");
                if (new ProfileRepository(file).Load().Profiles.SingleOrDefault()?.Name != "Recover me")
                    throw new InvalidOperationException("The repaired profile file could not be loaded again.");
            }
            finally
            {
                try { Directory.Delete(directory, true); } catch { }
                try { File.Delete(directory + ".tmp"); } catch { }
                try { File.Delete(directory + ".bak"); } catch { }
            }
        });

        Check("failed profile save rollback", () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "PitLaunch-save-failure-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                using ProfileCoordinator coordinator = new(
                    new ProfileRepository(directory),
                    new DisplayService(),
                    new AudioService(),
                    new WindowService(),
                    new AppService());

                (Profile? captured, OperationReport captureReport) = coordinator.CaptureNewAsync("Should roll back")
                    .GetAwaiter().GetResult();
                if (captured is not null || !captureReport.HasErrors || coordinator.Document.Profiles.Count != 0 ||
                    coordinator.Document.Runtime.ActiveProfileId is not null)
                {
                    throw new InvalidOperationException("A failed profile capture remained active in memory.");
                }

                DisplaySnapshot originalDisplay = new();
                AudioSnapshot originalAudio = new();
                List<WindowSnapshot> originalWindows = [];
                Profile existing = new()
                {
                    Name = "Existing",
                    Display = originalDisplay,
                    Audio = originalAudio,
                    Windows = originalWindows,
                    CapturedAtUtc = DateTimeOffset.UnixEpoch
                };
                coordinator.Document.Profiles.Add(existing);
                OperationReport recaptureReport = coordinator.RecaptureAsync(existing.Id).GetAwaiter().GetResult();
                if (!recaptureReport.HasErrors || !ReferenceEquals(existing.Display, originalDisplay) ||
                    !ReferenceEquals(existing.Audio, originalAudio) || !ReferenceEquals(existing.Windows, originalWindows) ||
                    existing.CapturedAtUtc != DateTimeOffset.UnixEpoch || coordinator.Document.Runtime.ActiveProfileId is not null)
                {
                    throw new InvalidOperationException("A failed recapture did not restore the previous in-memory profile.");
                }
            }
            finally
            {
                try { Directory.Delete(directory, true); } catch { }
                try { File.Delete(directory + ".tmp"); } catch { }
                try { File.Delete(directory + ".bak"); } catch { }
            }
        });

        Check("display capture", () =>
        {
            DisplaySnapshot display = new DisplayService().Capture();
            if (!display.Monitors.Any(monitor => monitor.Enabled))
                throw new InvalidOperationException("No active displays were captured.");
        });

        Check("display setup discovery", () =>
        {
            DisplayService displays = new();
            List<DisplayDeviceOption> devices = displays.ListConnectedDisplays();
            if (devices.Count == 0)
                throw new InvalidOperationException("No connected displays were discovered.");
            DisplayDeviceOption? unusable = devices.FirstOrDefault(device => device.Width == 0 || device.Height == 0);
            if (unusable is not null)
                throw new InvalidOperationException($"{unusable.FriendlyName} has no preferred display mode.");

            DisplayDeviceOption selected = devices.FirstOrDefault(device => device.IsPrimary) ?? devices[0];
            DisplaySnapshot snapshot = displays.BuildSnapshot(new DisplaySetupRequest(
                [selected.DevicePath],
                selected.DevicePath,
                DisplayLayoutMode.Recommended));
            MonitorSnapshot monitor = snapshot.Monitors.Single(item => item.Enabled);
            if (!monitor.Primary || monitor.X != 0 || monitor.Y != 0)
                throw new InvalidOperationException("The generated single-display layout is invalid.");

            foreach (DisplayDeviceOption device in devices)
            {
                DisplaySnapshot devicePlan = displays.BuildSnapshot(new DisplaySetupRequest(
                    [device.DevicePath],
                    device.DevicePath,
                    DisplayLayoutMode.Recommended));
                DisplayService.DisplayValidation validation = displays.ValidateSnapshot(devicePlan);
                if (!validation.CanRestore)
                {
                    throw new InvalidOperationException(
                        $"Windows rejected the generated layout for {device.FriendlyName}: {validation.Note}");
                }
            }

            if (devices.Count > 1)
            {
                DisplayDeviceOption main = devices.Count >= 3 ? devices[1] : devices[0];
                DisplaySnapshot combined = displays.BuildSnapshot(new DisplaySetupRequest(
                    devices.Select(device => device.DevicePath).ToList(),
                    main.DevicePath,
                    DisplayLayoutMode.Recommended));
                DisplayService.DisplayValidation combinedValidation = displays.ValidateSnapshot(combined);
                if (!combinedValidation.CanRestore)
                {
                    throw new InvalidOperationException(
                        "Windows rejected the generated multi-display layout: " + combinedValidation.Note);
                }
                if (combined.Monitors.Where(monitor => monitor.Enabled).Count(monitor => monitor.Primary) != 1)
                    throw new InvalidOperationException("The generated multi-display layout has an invalid primary display.");
            }
        });

        Check("all-display recovery validation", () =>
        {
            DisplayService displays = new();
            DisplaySnapshot recovery = displays.BuildAllConnectedSnapshot();
            List<MonitorSnapshot> enabled = recovery.Monitors.Where(monitor => monitor.Enabled).ToList();
            if (enabled.Count == 0 || enabled.Count(monitor => monitor.Primary) != 1)
                throw new InvalidOperationException("The emergency recovery plan has an invalid display set or primary monitor.");
            DisplayService.DisplayValidation validation = displays.ValidateSnapshot(recovery);
            if (!validation.CanRestore)
                throw new InvalidOperationException("Windows rejected the emergency recovery plan: " + validation.Note);
        });

        Check("same display no-op", () =>
        {
            DisplayService displays = new();
            DisplaySnapshot current = displays.Capture();
            OperationReport report = new("Self test display restore");
            displays.Restore(current, report);
            if (report.HasErrors || !report.Messages.Any(message =>
                    message.Message.Contains("already active", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("The active display layout was not recognized as unchanged.");
            }
        });

        Check("display restore validation", () =>
        {
            DisplayService displays = new();
            DisplaySnapshot current = displays.Capture();
            MonitorSnapshot? primary = current.Monitors.FirstOrDefault(monitor => monitor.Enabled && monitor.Primary)
                ?? current.Monitors.FirstOrDefault(monitor => monitor.Enabled);
            if (primary is null) throw new InvalidOperationException("No active display to validate.");
            DisplaySnapshot single = new() { Monitors = [primary] };
            DisplayService.DisplayValidation validation = displays.ValidateSnapshot(single);
            if (!validation.CanRestore)
            {
                throw new InvalidOperationException("Windows rejected restoring the primary display layout: " + validation.Note);
            }
        });

        Check("missing display fallback", () =>
        {
            DisplaySnapshot unavailable = new()
            {
                Monitors =
                [
                    new MonitorSnapshot
                    {
                        DevicePath = @"\\?\DISPLAY#PITLAUNCH_SELF_TEST#0#{00000000-0000-0000-0000-000000000000}",
                        FriendlyName = "Unavailable self-test display",
                        Enabled = true,
                        Primary = true,
                        Width = 1920,
                        Height = 1080,
                        RefreshNumerator = 60,
                        RefreshDenominator = 1
                    }
                ]
            };
            OperationReport report = new("Self test missing display");
            new DisplayService().Restore(unavailable, report);
            if (report.HasErrors || !report.HasWarnings)
                throw new InvalidOperationException("An unavailable display was not skipped cleanly.");
        });

        Check("audio capture", () =>
        {
            AudioSnapshot audio = new AudioService().Capture();
            if (audio.Playback is null)
                throw new InvalidOperationException("No default playback endpoint was captured.");
        });

        Check("missing audio fallback", () =>
        {
            AudioEndpointSnapshot unavailable = new()
            {
                DeviceId = "{0.0.0.00000000}.{PITLAUNCH-MISSING-DEVICE}",
                FriendlyName = "Unavailable self-test audio device"
            };
            OperationReport report = new("Self test missing audio");
            new AudioService().Restore(new AudioSnapshot
            {
                Playback = unavailable,
                Communications = unavailable,
                Microphone = unavailable
            }, report);
            if (report.HasErrors || !report.HasWarnings)
                throw new InvalidOperationException("An unavailable audio device was not skipped cleanly.");
        });

        Check("window capture", () => _ = new WindowService().Capture());

        Check("keep-awake request", () =>
        {
            using PowerService power = new();
            power.SetKeepAwake(true);
            if (!power.IsHolding) throw new InvalidOperationException("Windows refused the keep-awake request.");
            power.SetKeepAwake(false);
            if (power.IsHolding) throw new InvalidOperationException("The keep-awake request was not released.");
        });

        Check("game library scan", () =>
        {
            List<GameEntry> games = new GameLibraryService().Scan();
            // A machine may have no launcher installed, so an empty list is valid. Every entry
            // that IS returned has to be usable: real file, sane name, and a matchable process.
            foreach (GameEntry game in games)
            {
                if (string.IsNullOrWhiteSpace(game.Name))
                    throw new InvalidOperationException("A game was found without a name.");
                if (!File.Exists(game.ExecutablePath))
                    throw new InvalidOperationException($"{game.Name} points at a missing file: {game.ExecutablePath}");
                if (string.IsNullOrWhiteSpace(game.ProcessName))
                    throw new InvalidOperationException($"{game.Name} produced an empty process name.");
            }
            if (games.Select(game => game.ExecutablePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != games.Count)
                throw new InvalidOperationException("The same executable was listed twice.");
        });

        Check("controller discovery", () =>
        {
            ControllerService controllers = new();
            List<ControllerDevice> devices = controllers.ListConnected(out ControllerService.Diagnostics probe);

            // A machine may genuinely have no wheel attached, so an empty result is valid. What is
            // NOT valid is failing to query the HID devices that do exist - that silently reported
            // "no controllers" on every machine until the RID_DEVICE_INFO size was corrected.
            if (probe.HidDevicesSeen > 0 && probe.InfoQueriesSucceeded == 0)
            {
                throw new InvalidOperationException(
                    $"Queried {probe.HidDevicesSeen} HID devices and every lookup failed; controller detection is broken.");
            }
            foreach (ControllerDevice device in devices)
            {
                if (string.IsNullOrWhiteSpace(device.Name))
                    throw new InvalidOperationException("A controller was discovered without a name.");
            }
            if (controllers.FindMissing(devices.Select(device => device.Name)).Count != 0)
                throw new InvalidOperationException("A connected controller was reported as missing.");
            if (controllers.FindMissing(["PitLaunch self-test absent device"]).Count != 1)
                throw new InvalidOperationException("An absent controller was not reported as missing.");
        });

        try
        {
            string path = string.IsNullOrWhiteSpace(outputPath)
                ? Path.Combine(AppPaths.DataDirectory, "self-test.json")
                : Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? AppContext.BaseDirectory);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                passed,
                timestamp = DateTimeOffset.Now,
                checks
            }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        }
        catch
        {
            return 2;
        }

        return passed ? 0 : 1;
    }
}
