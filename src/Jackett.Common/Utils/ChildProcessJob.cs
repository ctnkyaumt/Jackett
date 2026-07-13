using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NLog;

namespace Jackett.Common.Utils
{
    /// <summary>
    /// Ties child processes to the lifetime of the current (Jackett) process using a Windows
    /// Job Object created with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. When Jackett exits for ANY
    /// reason - graceful shutdown, an unhandled crash, or being hard-killed via TerminateProcess
    /// (which is exactly how JackettTray stops JackettConsole) - the OS closes every handle the
    /// dying process owned, including this job's handle. Closing the last job handle makes Windows
    /// terminate every process still assigned to the job, along with their child processes.
    ///
    /// This is the only cleanup path that survives a hard kill: managed hooks (IDisposable,
    /// AppDomain.ProcessExit, finalizers) do NOT run when a process is terminated with
    /// TerminateProcess, so relying on them alone leaves orphans (e.g. the nodriver Python
    /// solver and the headless Chrome it spawns).
    ///
    /// The job handle is intentionally never closed by us: we want it to live exactly as long as
    /// the Jackett process so the OS closes it at process death. No-op on non-Windows.
    /// </summary>
    public static class ChildProcessJob
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static readonly object Sync = new object();
        private static IntPtr _jobHandle = IntPtr.Zero;
        private static bool _initialized;
        private static bool _usable;

        /// <summary>
        /// Assigns <paramref name="process"/> to the process-wide kill-on-close job so it (and its
        /// descendants) are terminated when Jackett dies. Safe to call for any child; failures are
        /// logged and swallowed so process startup is never blocked by cleanup wiring.
        /// </summary>
        public static void AddProcess(Process process)
        {
            if (process == null)
                return;
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return;

            try
            {
                EnsureJob();
                if (!_usable || _jobHandle == IntPtr.Zero)
                    return;

                if (!AssignProcessToJobObject(_jobHandle, process.Handle))
                {
                    var err = Marshal.GetLastWin32Error();
                    Logger.Warn($"Could not assign child process (PID {SafePid(process)}) to the cleanup job (Win32 error {err}); it may outlive Jackett.");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to register child process with the cleanup job.");
            }
        }

        private static void EnsureJob()
        {
            lock (Sync)
            {
                if (_initialized)
                    return;
                _initialized = true;

                var handle = CreateJobObject(IntPtr.Zero, null);
                if (handle == IntPtr.Zero)
                {
                    Logger.Warn($"CreateJobObject failed (Win32 error {Marshal.GetLastWin32Error()}); child processes will not be auto-terminated on exit.");
                    return;
                }

                var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                    {
                        LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    }
                };

                var length = Marshal.SizeOf(info);
                var infoPtr = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(info, infoPtr, false);
                    if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, infoPtr, (uint)length))
                    {
                        Logger.Warn($"SetInformationJobObject failed (Win32 error {Marshal.GetLastWin32Error()}); child processes will not be auto-terminated on exit.");
                        CloseHandle(handle);
                        return;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(infoPtr);
                }

                _jobHandle = handle;
                _usable = true;
            }
        }

        private static int SafePid(Process p)
        {
            try { return p.Id; } catch { return -1; }
        }

        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
        private const int JobObjectExtendedLimitInformation = 9;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }
    }
}
