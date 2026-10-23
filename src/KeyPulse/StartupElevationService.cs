using System;
using System.Diagnostics;

namespace KeyPulse
{
    /// <summary>
    /// TASK 10: the startup / elevation decisions that used to live inside MainWindow's Settings
    /// partial. Program.SetStartup / QueryStartupState remain the single startup authority and still
    /// own the Task Scheduler XML (logon trigger, HighestAvailable run level, battery flags); this
    /// service is the UI-free layer MainWindow talks to.
    /// </summary>
    internal static class StartupElevationService
    {
        internal const string RemoveAdminTaskArgument = "--remove-admin-task";
        internal const string SetupAdminTaskArgument = "--setup-admin-task";

        internal const string UnknownStateMessage =
            "Windows did not answer when asked whether KeyPulse starts with Windows, so this could not be checked. Nothing was changed. Try closing and reopening Settings.";

        internal readonly record struct StartupSnapshot(Program.StartupState State, bool AdminTaskInstalled, bool IsElevated)
        {
            public bool IsUnknown => State == Program.StartupState.Unknown;
            public bool IsStartupOn => State == Program.StartupState.On;

            /// <summary>ISSUE_9: the logon task exists and fires, but this session did not get elevated rights.</summary>
            public bool AdminTaskIneffective => AdminTaskInstalled && IsStartupOn && !IsElevated;
        }

        internal readonly record struct StatusText(string Text, string BrushKey);

        /// <summary>Test seam: replaces the Task Scheduler / registry query.</summary>
        internal static Func<StartupSnapshot>? SnapshotOverrideForTesting;

        /// <summary>Test seam: replaces Program.SetStartup.</summary>
        internal static Func<bool, (bool Ok, string Error)>? SetStartupOverrideForTesting;

        /// <summary>Test seam: replaces Process.Start for the UAC relaunch. Return false to simulate a declined prompt.</summary>
        internal static Func<ProcessStartInfo, bool>? ProcessStartOverrideForTesting;

        /// <summary>
        /// Blocking: asks Task Scheduler and the Run key. Call it off the UI thread. Re-asks Windows
        /// every time (InvalidateAdminTaskState first) and repairs the battery flags of an existing task.
        /// </summary>
        internal static StartupSnapshot LoadSnapshot()
        {
            var overrideSnapshot = SnapshotOverrideForTesting;
            if (overrideSnapshot != null) return overrideSnapshot();

            Program.InvalidateAdminTaskState();
            if (Program.IsAdminTaskInstalled())
            {
                Program.EnsureAdminTaskBatterySettings();
            }
            var state = Program.QueryStartupState();
            return new StartupSnapshot(state, Program.IsAdminTaskInstalled(), Program.IsElevated);
        }

        internal static bool SetStartup(bool enable, out string error)
        {
            var overrideSet = SetStartupOverrideForTesting;
            if (overrideSet != null)
            {
                var (ok, err) = overrideSet(enable);
                error = err;
                return ok;
            }

            return Program.SetStartup(enable, out error);
        }

        internal static StatusText DescribeStartup(StartupSnapshot snapshot)
        {
            if (snapshot.IsUnknown) return new StatusText(UnknownStateMessage, "AppWarningBrush");

            var text = snapshot.IsStartupOn
                ? (snapshot.AdminTaskInstalled
                    ? (snapshot.AdminTaskIneffective
                        ? "KeyPulse starts with Windows using its Administrator logon task, but this session is NOT running as Administrator - the task did not take effect on this account."
                        : "KeyPulse starts with Windows using its Administrator logon task.")
                    : "KeyPulse starts with Windows.")
                : "KeyPulse does not start with Windows.";
            return new StatusText(text, snapshot.AdminTaskIneffective ? "AppWarningBrush" : "AppTextMutedBrush");
        }

        internal static StatusText DescribeStartupChange(bool requestedState, bool ok, string error)
        {
            if (!ok) return new StatusText("Startup change failed: " + error, "AppDangerBrush");
            return new StatusText(
                requestedState
                    ? "KeyPulse will start hidden in the tray when Windows starts."
                    : "KeyPulse will no longer start with Windows.",
                "AppSuccessSoftBrush");
        }

        /// <summary>
        /// ISSUE_7 / --for-user: the elevated copy must create or remove the logon task for the person
        /// who clicked, not for whoever typed administrator credentials into the UAC prompt.
        /// </summary>
        internal static ProcessStartInfo BuildRelaunchStartInfo(string arguments, string forUser)
        {
            return new ProcessStartInfo
            {
                FileName = Program.ExePath,
                Arguments = arguments + " --for-user \"" + forUser + "\"",
                UseShellExecute = true,
                Verb = "runas"
            };
        }

        /// <summary>False (with the reason) when UAC was declined or the process could not start.</summary>
        internal static bool TryStartPrivilegeRelaunch(string arguments, out string error)
        {
            error = string.Empty;
            try
            {
                var forUser = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                var startInfo = BuildRelaunchStartInfo(arguments, forUser);

                var overrideStart = ProcessStartOverrideForTesting;
                if (overrideStart != null)
                {
                    if (overrideStart(startInfo)) return true;
                    error = "The operation was canceled by the user.";
                    return false;
                }

                using var elevated = Process.Start(startInfo);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Releases everything a replacement instance needs before this one exits: hotkeys, the
        /// monitor handle cache and the single-instance lock (Environment.Exit skips finally blocks).
        /// </summary>
        internal static void ReleaseForRelaunch()
        {
            try { HotkeyManager.DisableCaptureHook(); } catch { }
            try { HotkeyManager.Clear(); } catch { }
            try { HotkeyManager.Stop(); } catch { }

            try { ScreenBrightnessService.InvalidateMonitorCache(); } catch { }
            try { Program.ReleaseSingleInstanceLock(); } catch { }
        }
    }
}
