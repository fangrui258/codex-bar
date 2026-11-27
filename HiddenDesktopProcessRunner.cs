using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexBar;

internal sealed record ProcessCommandResult(int ExitCode, string StandardOutput, string StandardError);

internal static class HiddenDesktopProcessRunner
{
    private const uint GenericAll = 0x10000000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint StartfUseShowWindow = 0x00000001;
    private const uint StartfUseStdHandles = 0x00000100;
    private const short SwHide = 0;
    private const uint HandleFlagInherit = 0x00000001;
    private const int UoiName = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
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

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDesktop(
        string lpszDesktop,
        IntPtr lpszDevice,
        IntPtr pDevmode,
        uint dwFlags,
        uint dwDesiredAccess,
        IntPtr lpsa);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern IntPtr GetProcessWindowStation();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool GetUserObjectInformation(
        IntPtr hObj,
        int nIndex,
        StringBuilder pvInfo,
        int nLength,
        out int lpnLengthNeeded);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(
        out SafeFileHandle hReadPipe,
        out SafeFileHandle hWritePipe,
        ref SecurityAttributes lpPipeAttributes,
        int nSize);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(
        SafeFileHandle hObject,
        uint dwMask,
        uint dwFlags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string lpApplicationName,
        StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string lpCurrentDirectory,
        ref StartupInfo lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle hProcess, out uint lpExitCode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    public static async Task<ProcessCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<int>? onProcessStarted,
        CancellationToken cancellationToken)
    {
        var desktopName = $"CodexBarHidden-{Guid.NewGuid():N}";
        var desktopHandle = CreateDesktop(desktopName, IntPtr.Zero, IntPtr.Zero, 0, GenericAll, IntPtr.Zero);
        if (desktopHandle == IntPtr.Zero)
            return await RunStandardAsync(executable, arguments, workingDirectory, onProcessStarted, cancellationToken);

        try
        {
            var stationName = GetCurrentWindowStationName();
            var fullDesktopName = string.IsNullOrWhiteSpace(stationName)
                ? desktopName
                : $"{stationName}\\{desktopName}";

            return await RunOnDesktopAsync(
                executable, arguments, workingDirectory, fullDesktopName, onProcessStarted, cancellationToken);
        }
        finally
        {
            CloseDesktop(desktopHandle);
        }
    }

    private static async Task<ProcessCommandResult> RunOnDesktopAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string fullDesktopName,
        Action<int>? onProcessStarted,
        CancellationToken cancellationToken)
    {
        var securityAttributes = new SecurityAttributes
        {
            nLength = Marshal.SizeOf<SecurityAttributes>(),
            bInheritHandle = true
        };

        SafeFileHandle? outRead = null;
        SafeFileHandle? outWrite = null;
        SafeFileHandle? errRead = null;
        SafeFileHandle? errWrite = null;
        SafeFileHandle? inRead = null;
        SafeFileHandle? inWrite = null;

        try
        {
            if (!CreatePipe(out outRead, out outWrite, ref securityAttributes, 0) ||
                !CreatePipe(out errRead, out errWrite, ref securityAttributes, 0) ||
                !CreatePipe(out inRead, out inWrite, ref securityAttributes, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!SetHandleInformation(outRead, HandleFlagInherit, 0) ||
                !SetHandleInformation(errRead, HandleFlagInherit, 0) ||
                !SetHandleInformation(inWrite, HandleFlagInherit, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var startupInfo = new StartupInfo
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                lpDesktop = fullDesktopName,
                dwFlags = StartfUseStdHandles | StartfUseShowWindow,
                wShowWindow = SwHide,
                hStdInput = inRead.DangerousGetHandle(),
                hStdOutput = outWrite.DangerousGetHandle(),
                hStdError = errWrite.DangerousGetHandle()
            };

            var commandLine = new StringBuilder(BuildCommandLine(executable, arguments));
            if (!CreateProcess(
                    executable,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    bInheritHandles: true,
                    CreateNoWindow,
                    IntPtr.Zero,
                    workingDirectory,
                    ref startupInfo,
                    out var processInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            CloseHandle(processInfo.hThread);
            using var processHandle = new SafeProcessHandle(processInfo.hProcess, ownsHandle: true);

            // Close parent ends of child-side pipe handles immediately so EOF triggers cleanly.
            outWrite.Dispose();
            outWrite = null;
            errWrite.Dispose();
            errWrite = null;
            inRead.Dispose();
            inRead = null;
            inWrite.Dispose();
            inWrite = null;

            onProcessStarted?.Invoke(processInfo.dwProcessId);

            using var outStream = new FileStream(outRead, FileAccess.Read);
            using var errStream = new FileStream(errRead, FileAccess.Read);
            using var outReader = new StreamReader(outStream, Encoding.UTF8);
            using var errReader = new StreamReader(errStream, Encoding.UTF8);

            var stdoutTask = Task.Run(() => outReader.ReadToEnd(), CancellationToken.None);
            var stderrTask = Task.Run(() => errReader.ReadToEnd(), CancellationToken.None);

            try
            {
                await WaitForHandleAsync(processHandle, cancellationToken);
                var outputs = await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(cancellationToken);
                if (!GetExitCodeProcess(processHandle, out var exitCode))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                return new ProcessCommandResult(unchecked((int)exitCode), outputs[0], outputs[1]);
            }
            catch
            {
                TryKillProcessTreeById(processInfo.dwProcessId);
                throw;
            }
        }
        finally
        {
            outWrite?.Dispose();
            errWrite?.Dispose();
            inRead?.Dispose();
            inWrite?.Dispose();
            outRead?.Dispose();
            errRead?.Dispose();
        }
    }

    private static async Task<ProcessCommandResult> RunStandardAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        Action<int>? onProcessStarted,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var arg in arguments)
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"Could not start {Path.GetFileName(executable)}.");

        onProcessStarted?.Invoke(process.Id);
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new ProcessCommandResult(process.ExitCode, stdout, stderr);
        }
        catch
        {
            TryKillProcessTree(process);
            throw;
        }
    }

    private static async Task WaitForHandleAsync(SafeProcessHandle processHandle, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var waitHandle = new SafeProcessWaitHandle(processHandle);

        var registeredWait = ThreadPool.RegisterWaitForSingleObject(
            waitHandle,
            (_, timedOut) => tcs.TrySetResult(!timedOut),
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: true);

        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        try
        {
            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            registeredWait.Unregister(null);
        }
    }

    private sealed class SafeProcessWaitHandle : WaitHandle
    {
        public SafeProcessWaitHandle(SafeProcessHandle processHandle)
        {
            SafeWaitHandle = new SafeWaitHandle(processHandle.DangerousGetHandle(), ownsHandle: false);
        }
    }

    private static string? GetCurrentWindowStationName()
    {
        var station = GetProcessWindowStation();
        if (station == IntPtr.Zero) return null;

        var buffer = new StringBuilder(128);
        return GetUserObjectInformation(station, UoiName, buffer, buffer.Capacity, out _)
            ? buffer.ToString()
            : null;
    }

    internal static string BuildCommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var sb = new StringBuilder();
        AppendQuotedArgument(sb, executable);
        foreach (var arg in arguments)
        {
            sb.Append(' ');
            AppendQuotedArgument(sb, arg);
        }
        return sb.ToString();
    }

    private static void AppendQuotedArgument(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            sb.Append(arg);
            return;
        }

        sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
            }
            else if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
            }
            else
            {
                if (backslashes > 0)
                {
                    sb.Append('\\', backslashes);
                    backslashes = 0;
                }
                sb.Append(c);
            }
        }

        if (backslashes > 0)
            sb.Append('\\', backslashes * 2);
        sb.Append('"');
    }

    private static void TryKillProcessTreeById(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            TryKillProcessTree(process);
        }
        catch
        {
        }
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }
}
