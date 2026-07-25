using System.Runtime.InteropServices;
using System.Security.Principal;

namespace PitLaunch;

internal readonly record struct StartupTaskStatus(bool Exists, bool IsReady, string Detail)
{
    public static StartupTaskStatus NotInstalled() => new(
        false,
        false,
        "Optional delayed fallback is not installed.");

    public static StartupTaskStatus Unavailable(string detail) => new(false, false, detail);
}

internal static class StartupTaskRegistration
{
    private const string TaskName = "PitLaunch Beta Startup";
    private const int TaskCreateOrUpdate = 6;
    private const int TaskLogonInteractiveToken = 3;
    private const int TaskRunLevelLeastPrivilege = 0;
    private const int TaskTriggerLogon = 9;
    private const int TaskActionExecute = 0;
    private const int TaskInstancesIgnoreNew = 2;

    public const string ScheduledArgument = "--scheduled-startup";

    public static string CurrentUserSid()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value
            ?? throw new InvalidOperationException("Windows could not identify the current user account.");
    }

    public static void Install(string userSid)
    {
        _ = new SecurityIdentifier(userSid);
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The PitLaunch executable path is unavailable.");
        executable = Path.GetFullPath(executable);
        if (!File.Exists(executable))
        {
            throw new FileNotFoundException("The PitLaunch executable could not be found.", executable);
        }

        object? serviceObject = null;
        object? rootObject = null;
        object? definitionObject = null;
        object? triggerObject = null;
        object? actionObject = null;
        object? registeredTaskObject = null;
        try
        {
            serviceObject = CreateSchedulerService();
            dynamic service = serviceObject;
            service.Connect();
            rootObject = service.GetFolder("\\");
            dynamic root = rootObject;
            definitionObject = service.NewTask(0);
            dynamic definition = definitionObject;

            definition.RegistrationInfo.Author = AppInfo.ProductName;
            definition.RegistrationInfo.Description =
                "Delayed sign-in fallback for PitLaunch when normal Windows startup entries are skipped.";

            definition.Principal.UserId = userSid;
            definition.Principal.LogonType = TaskLogonInteractiveToken;
            definition.Principal.RunLevel = TaskRunLevelLeastPrivilege;

            definition.Settings.Enabled = true;
            definition.Settings.StartWhenAvailable = true;
            definition.Settings.DisallowStartIfOnBatteries = false;
            definition.Settings.StopIfGoingOnBatteries = false;
            definition.Settings.AllowDemandStart = true;
            definition.Settings.ExecutionTimeLimit = "PT0S";
            definition.Settings.MultipleInstances = TaskInstancesIgnoreNew;

            triggerObject = definition.Triggers.Create(TaskTriggerLogon);
            dynamic trigger = triggerObject;
            trigger.Id = "PitLaunchSignInFallback";
            trigger.UserId = userSid;
            trigger.Delay = "PT8S";
            trigger.Enabled = true;

            actionObject = definition.Actions.Create(TaskActionExecute);
            dynamic action = actionObject;
            action.Id = "StartPitLaunch";
            action.Path = executable;
            action.Arguments = ScheduledArgument;
            action.WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory;

            registeredTaskObject = root.RegisterTaskDefinition(
                TaskName,
                definition,
                TaskCreateOrUpdate,
                userSid,
                null,
                TaskLogonInteractiveToken,
                null);
            AppLog.Info("Reliable startup fallback installed for " + userSid + ".");
        }
        finally
        {
            ReleaseComObject(registeredTaskObject);
            ReleaseComObject(actionObject);
            ReleaseComObject(triggerObject);
            ReleaseComObject(definitionObject);
            ReleaseComObject(rootObject);
            ReleaseComObject(serviceObject);
        }
    }

    public static void Remove()
    {
        object? serviceObject = null;
        object? rootObject = null;
        try
        {
            serviceObject = CreateSchedulerService();
            dynamic service = serviceObject;
            service.Connect();
            rootObject = service.GetFolder("\\");
            dynamic root = rootObject;
            try
            {
                root.DeleteTask(TaskName, 0);
                AppLog.Info("Reliable startup fallback removed.");
            }
            catch (Exception ex) when (IsTaskMissing(ex))
            {
                AppLog.Info("Reliable startup fallback was already absent.");
            }
        }
        finally
        {
            ReleaseComObject(rootObject);
            ReleaseComObject(serviceObject);
        }
    }

    public static StartupTaskStatus GetStatus()
    {
        string? executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return StartupTaskStatus.Unavailable("Windows could not identify this PitLaunch file.");
        }

        object? serviceObject = null;
        object? rootObject = null;
        object? registeredTaskObject = null;
        object? definitionObject = null;
        object? actionObject = null;
        object? triggerObject = null;
        try
        {
            serviceObject = CreateSchedulerService();
            dynamic service = serviceObject;
            service.Connect();
            rootObject = service.GetFolder("\\");
            dynamic root = rootObject;
            try
            {
                registeredTaskObject = root.GetTask(TaskName);
            }
            catch (Exception ex) when (IsTaskMissing(ex))
            {
                return StartupTaskStatus.NotInstalled();
            }

            dynamic registeredTask = registeredTaskObject;
            definitionObject = registeredTask.Definition;
            dynamic definition = definitionObject;
            if ((int)definition.Actions.Count < 1 || (int)definition.Triggers.Count < 1)
            {
                return new StartupTaskStatus(true, false, "Installed fallback needs repair.");
            }

            actionObject = definition.Actions.Item(1);
            triggerObject = definition.Triggers.Item(1);
            dynamic action = actionObject;
            dynamic trigger = triggerObject;
            string path = Convert.ToString(action.Path) ?? string.Empty;
            string arguments = Convert.ToString(action.Arguments) ?? string.Empty;
            bool enabled = (bool)registeredTask.Enabled;
            int runLevel = (int)definition.Principal.RunLevel;
            int logonType = (int)definition.Principal.LogonType;
            int triggerType = (int)trigger.Type;

            bool ready = enabled &&
                         IsActionForExecutable(path, arguments, executable) &&
                         runLevel == TaskRunLevelLeastPrivilege &&
                         logonType == TaskLogonInteractiveToken &&
                         triggerType == TaskTriggerLogon;
            return ready
                ? new StartupTaskStatus(true, true, "Installed. PitLaunch still runs with normal permissions.")
                : new StartupTaskStatus(true, false, "Installed fallback needs repair.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not inspect reliable startup fallback: " + ex.Message);
            return StartupTaskStatus.Unavailable("Windows Task Scheduler status is unavailable.");
        }
        finally
        {
            ReleaseComObject(triggerObject);
            ReleaseComObject(actionObject);
            ReleaseComObject(definitionObject);
            ReleaseComObject(registeredTaskObject);
            ReleaseComObject(rootObject);
            ReleaseComObject(serviceObject);
        }
    }

    internal static bool IsActionForExecutable(string path, string arguments, string executable)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(executable)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(path), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(arguments.Trim(), ScheduledArgument, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static object CreateSchedulerService()
    {
        Type serviceType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("Windows Task Scheduler is unavailable.");
        return Activator.CreateInstance(serviceType)
            ?? throw new InvalidOperationException("Windows Task Scheduler could not be opened.");
    }

    private static bool IsTaskMissing(Exception exception) =>
        exception.HResult == unchecked((int)0x80070002);

    private static void ReleaseComObject(object? value)
    {
        if (value is null || !Marshal.IsComObject(value)) return;
        try { Marshal.FinalReleaseComObject(value); } catch { }
    }
}
