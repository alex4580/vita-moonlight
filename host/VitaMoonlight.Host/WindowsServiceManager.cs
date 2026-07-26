using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VitaMoonlight.Host;

internal enum WindowsServiceState : uint
{
    NotInstalled = 0,
    Stopped = 1,
    StartPending = 2,
    StopPending = 3,
    Running = 4,
    ContinuePending = 5,
    PausePending = 6,
    Paused = 7,
}

internal static class WindowsServiceManager
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceControlStop = 0x00000001;
    private const int ErrorServiceDoesNotExist = 1060;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    internal static WindowsServiceState GetState(string serviceName)
    {
        if (!OperatingSystem.IsWindows()) return WindowsServiceState.NotInstalled;

        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the Windows service manager.");
        try
        {
            var service = OpenService(manager, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorServiceDoesNotExist) return WindowsServiceState.NotInstalled;
                throw new Win32Exception(error, $"Could not query the {serviceName} service.");
            }
            try
            {
                return QueryState(service, serviceName);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    internal static void Restart(string serviceName, string displayName)
    {
        Stop(serviceName, displayName);
        Start(serviceName, displayName);
    }

    internal static void Stop(string serviceName, string displayName)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows services are only available on Windows.");

        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the Windows service manager.");
        try
        {
            var service = OpenService(manager, serviceName, ServiceQueryStatus | ServiceStart | ServiceStop);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorServiceDoesNotExist)
                {
                    throw new InvalidOperationException($"The {displayName} Windows service is not installed.");
                }
                throw new Win32Exception(error, $"Could not control the {displayName} Windows service.");
            }
            try
            {
                var state = QueryState(service, displayName);
                if (state == WindowsServiceState.StartPending)
                {
                    WaitForState(service, displayName, WindowsServiceState.Running);
                    state = WindowsServiceState.Running;
                }
                if (state != WindowsServiceState.Stopped)
                {
                    if (state != WindowsServiceState.StopPending)
                    {
                        if (!ControlService(service, ServiceControlStop, out _))
                        {
                            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not stop {displayName}.");
                        }
                    }
                    WaitForState(service, displayName, WindowsServiceState.Stopped);
                }
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    internal static void Start(string serviceName, string displayName)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows services are only available on Windows.");

        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not open the Windows service manager.");
        try
        {
            var service = OpenService(manager, serviceName, ServiceQueryStatus | ServiceStart);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == ErrorServiceDoesNotExist)
                {
                    throw new InvalidOperationException($"The {displayName} Windows service is not installed.");
                }
                throw new Win32Exception(error, $"Could not control the {displayName} Windows service.");
            }
            try
            {
                var state = QueryState(service, displayName);
                if (state == WindowsServiceState.Running) return;
                if (state == WindowsServiceState.StartPending)
                {
                    WaitForState(service, displayName, WindowsServiceState.Running);
                    return;
                }
                if (state == WindowsServiceState.StopPending)
                {
                    WaitForState(service, displayName, WindowsServiceState.Stopped);
                }
                if (!StartService(service, 0, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start {displayName}.");
                }
                WaitForState(service, displayName, WindowsServiceState.Running);
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static WindowsServiceState QueryState(IntPtr service, string displayName)
    {
        if (!QueryServiceStatus(service, out var status))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not read the {displayName} service status.");
        }
        return (WindowsServiceState)status.CurrentState;
    }

    private static void WaitForState(IntPtr service, string displayName, WindowsServiceState desiredState)
    {
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < DefaultTimeout)
        {
            if (QueryState(service, displayName) == desiredState) return;
            Thread.Sleep(250);
        }
        throw new TimeoutException($"Timed out waiting for {displayName} to become {desiredState}.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        internal uint ServiceType;
        internal uint CurrentState;
        internal uint ControlsAccepted;
        internal uint Win32ExitCode;
        internal uint ServiceSpecificExitCode;
        internal uint CheckPoint;
        internal uint WaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr serviceManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus serviceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus serviceStatus);

    [DllImport("advapi32.dll", EntryPoint = "StartServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(IntPtr service, uint argumentCount, IntPtr arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr serviceHandle);
}
