using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;

namespace YMacType.Settings
{
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (Array.Exists(
                args,
                argument => argument.Equals(
                    "--service",
                    StringComparison.OrdinalIgnoreCase)))
        {
            ServiceBase.Run(new MacTypeService());
            return 0;
        }

        var application = new App();
        application.InitializeComponent();
        return application.Run();
    }
}

// The "MacType" Windows service used when the original MacTray service is not
// installed. It runs as LocalSystem and, for every interactive session, keeps
// one 32-bit and one 64-bit hook host (MacLoader.exe / MacLoader64.exe
// --hook <service pid>) running with a SYSTEM token inside that session's
// default desktop. The hook hosts install the global WH_GETMESSAGE hook that
// makes Windows load MacType(64).Core.dll into every GUI process, including
// elevated ones.
internal sealed class MacTypeService : ServiceBase
{
    private const string ExpectedDirectory = @"C:\Program Files\MacType";

    private static readonly string[] HelperNames =
    {
        "MacLoader.exe",
        "MacLoader64.exe"
    };

    private readonly object _gate = new object();
    private readonly Dictionary<string, HelperState> _helpers =
        new Dictionary<string, HelperState>(StringComparer.OrdinalIgnoreCase);
    private Timer? _timer;
    private string _directory = ExpectedDirectory;
    private bool _stopping;

    public MacTypeService()
    {
        ServiceName = "MacType";
        CanStop = true;
        CanHandleSessionChangeEvent = true;
        AutoLog = false;
    }

    protected override void OnStart(string[] args)
    {
        // The service runs as LocalSystem, so it only launches helpers from the
        // protected install directory, never from wherever this exe was copied.
        _directory = Path.GetFullPath(
            AppDomain.CurrentDomain.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(
                _directory,
                ExpectedDirectory,
                StringComparison.OrdinalIgnoreCase))
        {
            Log("refusing to run from " + _directory);
            throw new InvalidOperationException(
                "YMacType service must run from " + ExpectedDirectory);
        }

        Log("service starting");
        _stopping = false;
        _timer = new Timer(_ => Reconcile(), null, 0, 3000);
    }

    protected override void OnStop()
    {
        Log("service stopping");
        _stopping = true;
        _timer?.Dispose();
        _timer = null;
        lock (_gate)
        {
            foreach (var helper in _helpers.Values)
                helper.Kill();
            _helpers.Clear();
        }
    }

    protected override void OnSessionChange(SessionChangeDescription change)
    {
        ThreadPool.QueueUserWorkItem(_ => Reconcile());
    }

    private void Reconcile()
    {
        lock (_gate)
        {
            if (_stopping)
                return;
            try
            {
                var active = ActiveSessions();
                if (active == null)
                    return; // enumeration failed: keep the current hooks
                var wanted = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                foreach (var session in active)
                {
                    foreach (var helperName in HelperNames)
                    {
                        var key = session + ":" + helperName;
                        wanted.Add(key);
                        EnsureHelper(key, session, helperName);
                    }
                }

                foreach (var key in new List<string>(_helpers.Keys))
                {
                    if (wanted.Contains(key))
                        continue;
                    // The session ended or is no longer interactive.
                    _helpers[key].Kill();
                    _helpers.Remove(key);
                }
            }
            catch (Exception exception)
            {
                Log("reconcile failed: " + exception.Message);
            }
        }
    }

    private void EnsureHelper(string key, int session, string helperName)
    {
        if (!_helpers.TryGetValue(key, out var state))
        {
            state = new HelperState();
            _helpers[key] = state;
        }

        if (state.IsRunning)
        {
            if (DateTime.UtcNow - state.Started > TimeSpan.FromMinutes(1))
                state.Failures = 0;
            return;
        }
        if (DateTime.UtcNow < state.NextAttempt)
            return;

        var path = Path.Combine(_directory, helperName);
        var core = Path.Combine(
            _directory,
            helperName.IndexOf("64", StringComparison.Ordinal) >= 0
                ? "MacType64.Core.dll"
                : "MacType.Core.dll");
        if (!File.Exists(path) || !File.Exists(core))
        {
            state.Failures = Math.Max(state.Failures, 6);
            state.ScheduleRetry();
            if (!state.Reported)
            {
                Log("missing " + path + " or " + core);
                state.Reported = true;
            }
            return;
        }

        state.Close();
        var error = NativeMethods.StartHelper(
            session, path, _directory, out var process);
        if (error == 0)
        {
            state.Process = process;
            state.Started = DateTime.UtcNow;
            state.Reported = false;
            Log("started " + helperName + " in session " + session);
            // Restarts that keep dying quickly back off instead of spinning.
            ++state.Failures;
            state.ScheduleRetry();
        }
        else
        {
            ++state.Failures;
            state.ScheduleRetry();
            Log(
                "starting " + helperName + " in session " + session +
                " failed, error " + error);
        }
    }

    private static List<int>? ActiveSessions()
    {
        var result = new List<int>();
        if (!NativeMethods.WTSEnumerateSessionsW(
                IntPtr.Zero, 0, 1, out var buffer, out var count))
            return null;
        try
        {
            var size = Marshal.SizeOf(typeof(NativeMethods.WtsSessionInfo));
            for (var index = 0; index != count; ++index)
            {
                var info = (NativeMethods.WtsSessionInfo)
                    Marshal.PtrToStructure(
                        new IntPtr(buffer.ToInt64() + (long)index * size),
                        typeof(NativeMethods.WtsSessionInfo))!;
                if (info.SessionId != 0 &&
                    info.State == NativeMethods.WtsActive)
                    result.Add(info.SessionId);
            }
        }
        finally
        {
            NativeMethods.WTSFreeMemory(buffer);
        }
        return result;
    }

    private static readonly object LogGate = new object();

    internal static void Log(string message)
    {
        try
        {
            lock (LogGate)
            {
                var directory = Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.CommonApplicationData),
                    "MacType", "Logs");
                Directory.CreateDirectory(directory);
                var file = Path.Combine(directory, "service.log");
                if (File.Exists(file) && new FileInfo(file).Length > 1024 * 1024)
                    File.Delete(file);
                File.AppendAllText(
                    file,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " +
                    message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
        }
        catch
        {
            // Logging must never take the service down.
        }
    }

    private sealed class HelperState
    {
        public IntPtr Process;
        public DateTime Started = DateTime.MinValue;
        public DateTime NextAttempt = DateTime.MinValue;
        public int Failures;
        public bool Reported;

        public bool IsRunning =>
            Process != IntPtr.Zero &&
            NativeMethods.WaitForSingleObject(Process, 0) ==
                NativeMethods.WaitTimeout;

        public void ScheduleRetry()
        {
            var seconds = Math.Min(60, 2 * Math.Max(1, Failures));
            NextAttempt = DateTime.UtcNow.AddSeconds(seconds);
        }

        public void Close()
        {
            if (Process != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(Process);
                Process = IntPtr.Zero;
            }
        }

        public void Kill()
        {
            if (Process != IntPtr.Zero)
            {
                NativeMethods.TerminateProcess(Process, 0);
                Close();
            }
        }
    }

    private static class NativeMethods
    {
        public const int WtsActive = 0;
        public const uint WaitTimeout = 0x102;

        private const uint TokenAllAccess = 0xF01FF;
        private const int SecurityImpersonation = 2;
        private const int TokenPrimary = 1;
        private const int TokenSessionId = 12;
        private const uint CreateUnicodeEnvironment = 0x400;
        private const uint CreateNoWindow = 0x08000000;
        private const uint StartfUseShowWindow = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        public struct WtsSessionInfo
        {
            public int SessionId;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? WinStationName;
            public int State;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int cb;
            public string? lpReserved;
            public string? lpDesktop;
            public string? lpTitle;
            public int dwX;
            public int dwY;
            public int dwXSize;
            public int dwYSize;
            public int dwXCountChars;
            public int dwYCountChars;
            public int dwFillAttribute;
            public uint dwFlags;
            public short wShowWindow;
            public short cbReserved2;
            public IntPtr lpReserved2;
            public IntPtr hStdInput;
            public IntPtr hStdOutput;
            public IntPtr hStdError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public int dwProcessId;
            public int dwThreadId;
        }

        [DllImport("wtsapi32.dll", SetLastError = true)]
        public static extern bool WTSEnumerateSessionsW(
            IntPtr server, int reserved, int version,
            out IntPtr sessions, out int count);

        [DllImport("wtsapi32.dll")]
        public static extern void WTSFreeMemory(IntPtr memory);

        [DllImport("kernel32.dll")]
        public static extern uint WaitForSingleObject(
            IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr process, uint code);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern int GetCurrentProcessId();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(
            IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(
            IntPtr existing, uint access, IntPtr attributes,
            int impersonationLevel, int tokenType, out IntPtr newToken);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool SetTokenInformation(
            IntPtr token, int informationClass,
            ref int information, int length);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(
            out IntPtr environment, IntPtr token, bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr environment);

        [DllImport("advapi32.dll", SetLastError = true,
            CharSet = CharSet.Unicode)]
        private static extern bool CreateProcessAsUserW(
            IntPtr token, string applicationName, StringBuilder commandLine,
            IntPtr processAttributes, IntPtr threadAttributes,
            bool inheritHandles, uint flags, IntPtr environment,
            string currentDirectory, ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        // Starts `path --hook <service pid>` with a SYSTEM token in the given
        // session's default desktop. Returns 0 and the process handle on
        // success, otherwise the Win32 error.
        public static int StartHelper(
            int session, string path, string directory, out IntPtr process)
        {
            process = IntPtr.Zero;
            var token = IntPtr.Zero;
            var primary = IntPtr.Zero;
            var environment = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(
                        GetCurrentProcess(), TokenAllAccess, out token))
                    return Marshal.GetLastWin32Error();
                if (!DuplicateTokenEx(
                        token, TokenAllAccess, IntPtr.Zero,
                        SecurityImpersonation, TokenPrimary, out primary))
                    return Marshal.GetLastWin32Error();
                var sessionId = session;
                if (!SetTokenInformation(
                        primary, TokenSessionId, ref sessionId, sizeof(int)))
                    return Marshal.GetLastWin32Error();
                if (!CreateEnvironmentBlock(out environment, primary, false))
                    environment = IntPtr.Zero;

                var startup = new StartupInfo
                {
                    cb = Marshal.SizeOf(typeof(StartupInfo)),
                    lpDesktop = @"winsta0\default",
                    dwFlags = StartfUseShowWindow,
                    wShowWindow = 0
                };
                var commandLine = new StringBuilder(
                    "\"" + path + "\" --hook " + GetCurrentProcessId());
                var flags = CreateNoWindow;
                if (environment != IntPtr.Zero)
                    flags |= CreateUnicodeEnvironment;
                if (!CreateProcessAsUserW(
                        primary, path, commandLine, IntPtr.Zero, IntPtr.Zero,
                        false, flags, environment, directory,
                        ref startup, out var info))
                    return Marshal.GetLastWin32Error();
                CloseHandle(info.hThread);
                process = info.hProcess;
                return 0;
            }
            finally
            {
                if (environment != IntPtr.Zero)
                    DestroyEnvironmentBlock(environment);
                if (primary != IntPtr.Zero)
                    CloseHandle(primary);
                if (token != IntPtr.Zero)
                    CloseHandle(token);
            }
        }
    }
}
}
