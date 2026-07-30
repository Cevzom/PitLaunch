using Velopack;

namespace PitLaunch;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Must run before any other startup work: on an installed copy this handles the
        // install/update/uninstall hooks and exits for them. It is a no-op for the portable zip.
        try
        {
            VelopackApp.Build().Run();
        }
        catch (Exception ex)
        {
            AppLog.Error("Velopack startup hook failed: " + ex.Message);
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        LaunchRequest request = LaunchRequest.Parse(args);
        if (request.Kind == LaunchRequestKind.SelfTest)
        {
            return SelfTest.Run(request.OutputPath);
        }

        if (request.Kind is LaunchRequestKind.InstallStartupTask or LaunchRequestKind.RemoveStartupTask)
        {
            return RunStartupTaskMaintenance(request);
        }

        bool scheduledFallback = request.Kind == LaunchRequestKind.ScheduledStartup;
        if (scheduledFallback)
        {
            try
            {
                AppSettings settings = new ProfileRepository().Load().Settings;
                if (!settings.LaunchOnStartup)
                {
                    AppLog.Info("Scheduled startup skipped because Start with Windows is off.");
                    return 0;
                }
                request.Kind = settings.StartMinimized
                    ? LaunchRequestKind.Background
                    : LaunchRequestKind.Chooser;
            }
            catch (Exception ex)
            {
                AppLog.Error("Scheduled startup could not read saved settings: " + ex.Message);
                return 1;
            }
        }

        using SingleInstance instance = new();
        if (!instance.IsPrimary)
        {
            if (scheduledFallback)
            {
                AppLog.Info("Scheduled startup skipped because PitLaunch is already running.");
                return 0;
            }
            return instance.Forward(request) ? 0 : 2;
        }

        RepairStartupRegistration();

        try
        {
            AppLog.Info($"{AppInfo.ProductName} {AppInfo.Version} started ({request.Kind}).");
            using PitLaunchContext context = new(request, instance);
            Application.Run(context);
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("Fatal startup error: " + ex);
            MessageBox.Show(
                "PitLaunch could not start. Details were written to " + AppPaths.LogFile + ".\n\n" + ex.Message,
                AppInfo.ProductName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int RunStartupTaskMaintenance(LaunchRequest request)
    {
        try
        {
            if (request.Kind == LaunchRequestKind.InstallStartupTask)
            {
                if (string.IsNullOrWhiteSpace(request.Value))
                {
                    throw new ArgumentException("The startup task user account was not provided.");
                }
                StartupTaskRegistration.Install(request.Value);
            }
            else
            {
                StartupTaskRegistration.Remove();
            }
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("Reliable startup maintenance failed: " + ex);
            return 1;
        }
    }

    private static void RepairStartupRegistration()
    {
        try
        {
            AppSettings settings = new ProfileRepository().Load().Settings;
            if (settings.LaunchOnStartup)
            {
                if (StartupRegistration.IsFullyEnabled(settings.StartMinimized)) return;
                StartupRegistration.SetEnabled(true, settings.StartMinimized);
                AppLog.Info("Windows startup entries repaired for the current PitLaunch file.");
            }
            else if (StartupRegistration.HasAnyRegistration())
            {
                StartupRegistration.SetEnabled(false, settings.StartMinimized);
                AppLog.Info("Disabled stale Windows startup entries.");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not repair Windows startup entries: " + ex.Message);
        }
    }
}
