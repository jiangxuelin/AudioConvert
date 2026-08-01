using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace AudioConvert.Services
{
    public sealed class UnelevatedRunnerLauncher : IRunnerProcessLauncher
    {
        public static bool IsProcessElevated()
        {
            IntPtr tokenHandle = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out tokenHandle))
                {
                    return false;
                }

                int size = Marshal.SizeOf(typeof(int));
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!GetTokenInformation(
                            tokenHandle, TokenElevation, buffer, size, out _))
                    {
                        return false;
                    }

                    return Marshal.ReadInt32(buffer) != 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                if (tokenHandle != IntPtr.Zero)
                {
                    CloseHandle(tokenHandle);
                }
            }
        }

        public RunnerLaunchResult Launch(string runnerPath, string arguments)
        {
            if (string.IsNullOrWhiteSpace(runnerPath) || !File.Exists(runnerPath))
            {
                return RunnerLaunchResult.Failed(
                    "QQMusicDecryptRunner.exe was not found: " + runnerPath,
                    "unelevated_session");
            }

            IntPtr stdoutRead = IntPtr.Zero;
            IntPtr stdoutWrite = IntPtr.Zero;
            IntPtr stderrRead = IntPtr.Zero;
            IntPtr stderrWrite = IntPtr.Zero;
            PROCESS_INFORMATION processInformation = new PROCESS_INFORMATION();
            IntPtr userToken = IntPtr.Zero;
            IntPtr primaryToken = IntPtr.Zero;
            IntPtr environmentBlock = IntPtr.Zero;

            try
            {
                SECURITY_ATTRIBUTES securityAttributes = new SECURITY_ATTRIBUTES
                {
                    nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)),
                    bInheritHandle = true,
                    lpSecurityDescriptor = IntPtr.Zero
                };

                CreateRedirectedPipe(ref securityAttributes, out stdoutRead, out stdoutWrite);
                CreateRedirectedPipe(ref securityAttributes, out stderrRead, out stderrWrite);

                STARTUPINFO startupInfo = new STARTUPINFO
                {
                    cb = Marshal.SizeOf(typeof(STARTUPINFO)),
                    dwFlags = StartfUseStdHandles,
                    hStdOutput = stdoutWrite,
                    hStdError = stderrWrite,
                    hStdInput = IntPtr.Zero
                };

                string workingDirectory = Path.GetDirectoryName(runnerPath) ?? AppDomain.CurrentDomain.BaseDirectory;
                string commandLine = BuildCommandLine(runnerPath, arguments);

                string startupError;
                bool started = TryLaunchWithActiveUserToken(
                    commandLine, workingDirectory, startupInfo,
                    ref processInformation, ref userToken,
                    ref primaryToken, ref environmentBlock,
                    out startupError);

                if (!started)
                {
                    CleanupTokens(ref userToken, ref primaryToken, ref environmentBlock);

                    started = TryLaunchWithExplorerToken(
                        commandLine, workingDirectory, startupInfo,
                        ref processInformation, ref userToken,
                        ref primaryToken, ref environmentBlock,
                        out string explorerError);

                    if (!started)
                    {
                        CleanupTokens(ref userToken, ref primaryToken, ref environmentBlock);
                        CloseHandleIfNeeded(ref stdoutRead);
                        CloseHandleIfNeeded(ref stdoutWrite);
                        CloseHandleIfNeeded(ref stderrRead);
                        CloseHandleIfNeeded(ref stderrWrite);

                        return FallbackToDirectLaunch(
                            runnerPath, arguments,
                            startupError + " Fallback: " + explorerError);
                    }
                }

                CleanupTokens(ref userToken, ref primaryToken, ref environmentBlock);

                CloseHandleIfNeeded(ref stdoutWrite);
                CloseHandleIfNeeded(ref stderrWrite);

                uint pid = processInformation.dwProcessId;
                Process? process = null;
                try
                {
                    process = Process.GetProcessById(unchecked((int)pid));
                }
                catch
                {
                }

                CloseHandleIfNeeded(ref processInformation.hThread);
                CloseHandleIfNeeded(ref processInformation.hProcess);

                StreamReader stdoutReader = CreateReaderFromHandle(ref stdoutRead);
                StreamReader stderrReader = CreateReaderFromHandle(ref stderrRead);

                return new RunnerLaunchResult(() =>
                {
                    try { stdoutReader.Dispose(); } catch { }
                    try { stderrReader.Dispose(); } catch { }
                })
                {
                    Started = true,
                    Process = process,
                    StandardOutput = stdoutReader,
                    StandardError = stderrReader,
                    LaunchMode = "unelevated_session",
                };
            }
            catch (Exception ex)
            {
                CleanupTokens(ref userToken, ref primaryToken, ref environmentBlock);
                CloseHandleIfNeeded(ref processInformation.hThread);
                CloseHandleIfNeeded(ref processInformation.hProcess);
                CloseHandleIfNeeded(ref stdoutRead);
                CloseHandleIfNeeded(ref stdoutWrite);
                CloseHandleIfNeeded(ref stderrRead);
                CloseHandleIfNeeded(ref stderrWrite);

                return FallbackToDirectLaunch(runnerPath, arguments,
                    "Unelevated launch exception: " + ex.Message);
            }
        }

        private static RunnerLaunchResult FallbackToDirectLaunch(
            string runnerPath, string arguments, string priorError)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = runnerPath,
                    Arguments = arguments,
                    WorkingDirectory = Path.GetDirectoryName(runnerPath) ?? AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                ConfigureChildRuntimeEnvironment(psi);

                Process? process = Process.Start(psi);
                if (process == null)
                {
                    return RunnerLaunchResult.Failed(
                        priorError + " Direct fallback also failed to start.",
                        "direct_fallback");
                }

                Debug.WriteLine(
                    "UnelevatedRunnerLauncher: fell back to direct launch. Prior error: " + priorError);

                return new RunnerLaunchResult
                {
                    Started = true,
                    Process = process,
                    StandardOutput = process.StandardOutput,
                    StandardError = process.StandardError,
                    LaunchMode = "direct_fallback",
                };
            }
            catch (Exception ex)
            {
                return RunnerLaunchResult.Failed(
                    priorError + " Direct fallback exception: " + ex.Message,
                    "direct_fallback");
            }
        }

        private static void ConfigureChildRuntimeEnvironment(ProcessStartInfo processStartInfo)
        {
            string[] variablesToRemove =
            {
                "APP_CONFIG_FILE",
                "COMPLUS_Version",
                "COMPLUS_ApplicationMigrationRuntimeActivationConfigPath",
                "COR_ENABLE_PROFILING",
                "COR_PROFILER",
                "COR_PROFILER_PATH",
                "CORECLR_ENABLE_PROFILING",
                "CORECLR_PROFILER",
                "CORECLR_PROFILER_PATH"
            };

            foreach (string variableName in variablesToRemove)
            {
                processStartInfo.EnvironmentVariables.Remove(variableName);
            }
        }

        #region Token acquisition

        private static bool TryLaunchWithActiveUserToken(
            string commandLine,
            string workingDirectory,
            STARTUPINFO startupInfo,
            ref PROCESS_INFORMATION processInformation,
            ref IntPtr userToken,
            ref IntPtr primaryToken,
            ref IntPtr environmentBlock,
            out string startupError)
        {
            startupError = string.Empty;

            uint sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == 0xFFFFFFFF)
            {
                startupError = "Unable to resolve the active user session.";
                return false;
            }

            if (!WTSQueryUserToken(sessionId, out userToken))
            {
                startupError = "WTSQueryUserToken failed. Win32Error=" + Marshal.GetLastWin32Error();
                return false;
            }

            if (!DuplicateTokenEx(
                    userToken,
                    TokenAssignPrimary | TokenDuplicate | TokenQuery
                        | TokenAdjustDefault | TokenAdjustSessionId,
                    IntPtr.Zero,
                    SecurityImpersonation,
                    TokenPrimary,
                    out primaryToken))
            {
                startupError = "DuplicateTokenEx failed. Win32Error=" + Marshal.GetLastWin32Error();
                return false;
            }

            CreateEnvironmentBlock(out environmentBlock, primaryToken, false);

            if (!CreateProcessAsUser(
                    primaryToken, null, commandLine,
                    IntPtr.Zero, IntPtr.Zero, true,
                    CreateNoWindow | CreateUnicodeEnvironment,
                    environmentBlock, workingDirectory,
                    ref startupInfo, out processInformation))
            {
                startupError = "CreateProcessAsUser failed. Win32Error=" + Marshal.GetLastWin32Error();
                return false;
            }

            return true;
        }

        private static bool TryLaunchWithExplorerToken(
            string commandLine,
            string workingDirectory,
            STARTUPINFO startupInfo,
            ref PROCESS_INFORMATION processInformation,
            ref IntPtr userToken,
            ref IntPtr primaryToken,
            ref IntPtr environmentBlock,
            out string startupError)
        {
            startupError = string.Empty;
            uint activeSessionId = WTSGetActiveConsoleSessionId();

            Process? explorerProcess = null;
            Process[] explorerProcesses = Process.GetProcessesByName("explorer");
            for (int i = 0; i < explorerProcesses.Length; i++)
            {
                Process current = explorerProcesses[i];
                if ((uint)current.SessionId == activeSessionId)
                {
                    explorerProcess = current;
                    break;
                }

                if (explorerProcess == null)
                {
                    explorerProcess = current;
                }
            }

            if (explorerProcess == null)
            {
                startupError = "No explorer.exe process was found for fallback launch.";
                return false;
            }

            IntPtr explorerHandle = OpenProcess(ProcessQueryInformation, false, explorerProcess.Id);
            if (explorerHandle == IntPtr.Zero)
            {
                startupError = "OpenProcess(explorer.exe) failed. Win32Error="
                    + Marshal.GetLastWin32Error();
                return false;
            }

            try
            {
                if (!OpenProcessToken(
                        explorerHandle,
                        TokenAssignPrimary | TokenDuplicate | TokenQuery | TokenImpersonate,
                        out userToken))
                {
                    startupError = "OpenProcessToken(explorer.exe) failed. Win32Error="
                        + Marshal.GetLastWin32Error();
                    return false;
                }

                if (!DuplicateTokenEx(
                        userToken,
                        TokenAssignPrimary | TokenDuplicate | TokenQuery
                            | TokenAdjustDefault | TokenAdjustSessionId,
                        IntPtr.Zero,
                        SecurityImpersonation,
                        TokenPrimary,
                        out primaryToken))
                {
                    startupError = "DuplicateTokenEx(explorer.exe) failed. Win32Error="
                        + Marshal.GetLastWin32Error();
                    return false;
                }

                CreateEnvironmentBlock(out environmentBlock, primaryToken, false);

                if (!CreateProcessWithTokenW(
                        primaryToken, LogonWithProfile,
                        null, commandLine,
                        CreateNoWindow | CreateUnicodeEnvironment,
                        environmentBlock, workingDirectory,
                        ref startupInfo, out processInformation))
                {
                    startupError = "CreateProcessWithTokenW failed. Win32Error="
                        + Marshal.GetLastWin32Error();
                    return false;
                }

                return true;
            }
            finally
            {
                CloseHandle(explorerHandle);
            }
        }

        #endregion

        #region Pipe and handle helpers

        private static void CreateRedirectedPipe(
            ref SECURITY_ATTRIBUTES securityAttributes,
            out IntPtr readHandle,
            out IntPtr writeHandle)
        {
            if (!CreatePipe(out readHandle, out writeHandle, ref securityAttributes, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!SetHandleInformation(readHandle, HandleFlagInherit, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        private static StreamReader CreateReaderFromHandle(ref IntPtr handle)
        {
            SafeFileHandle safeHandle = new SafeFileHandle(handle, true);
            handle = IntPtr.Zero;
            FileStream stream = new FileStream(safeHandle, FileAccess.Read, 4096, false);
            return new StreamReader(stream, Encoding.UTF8);
        }

        private static void CleanupTokens(
            ref IntPtr userToken,
            ref IntPtr primaryToken,
            ref IntPtr environmentBlock)
        {
            if (environmentBlock != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environmentBlock);
                environmentBlock = IntPtr.Zero;
            }

            CloseHandleIfNeeded(ref primaryToken);
            CloseHandleIfNeeded(ref userToken);
        }

        private static void CloseHandleIfNeeded(ref IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                return;
            }

            CloseHandle(handle);
            handle = IntPtr.Zero;
        }

        private static string BuildCommandLine(string fileName, string arguments)
        {
            string quoted = "\"" + (fileName ?? string.Empty).Replace("\"", "\\\"") + "\"";
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return quoted;
            }

            return quoted + " " + arguments;
        }

        #endregion

        #region Constants

        private const uint StartfUseStdHandles = 0x00000100;
        private const uint CreateNoWindow = 0x08000000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const int SecurityImpersonation = 2;
        private const int TokenPrimary = 1;
        private const uint HandleFlagInherit = 0x00000001;
        private const uint TokenAssignPrimary = 0x0001;
        private const uint TokenDuplicate = 0x0002;
        private const uint TokenImpersonate = 0x0004;
        private const int TokenElevation = 20;
        private const uint TokenQuery = 0x0008;
        private const uint TokenAdjustDefault = 0x0080;
        private const uint TokenAdjustSessionId = 0x0100;
        private const uint ProcessQueryInformation = 0x0400;
        private const uint LogonWithProfile = 0x00000001;

        #endregion

        #region Native structs

        [StructLayout(LayoutKind.Sequential)]
        /// <summary>
        /// Windows API 鍒涘缓杩涚▼鏃朵娇鐢ㄧ殑瀹夊叏灞炴€х粨鏋勩€?        /// </summary>
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        /// <summary>
        /// Windows API 鍒涘缓杩涚▼鏃朵娇鐢ㄧ殑鍚姩绐楀彛鍜屽彞鏌勯厤缃粨鏋勩€?        /// </summary>
        private struct STARTUPINFO
        {
            public int cb;
            public string lpReserved;
            public string lpDesktop;
            public string lpTitle;
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
        /// <summary>
        /// Windows API 鍒涘缓杩涚▼鍚庤繑鍥炵殑杩涚▼鍜岀嚎绋嬪彞鏌勪俊鎭€?        /// </summary>
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        #endregion

        #region P/Invoke

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(
            out IntPtr hReadPipe,
            out IntPtr hWritePipe,
            ref SECURITY_ATTRIBUTES lpPipeAttributes,
            int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(
            IntPtr hObject, uint dwMask, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(
            IntPtr tokenHandle,
            int tokenInformationClass,
            IntPtr tokenInformation,
            int tokenInformationLength,
            out int returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            uint processAccess,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
            int processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(
            IntPtr processHandle,
            uint desiredAccess,
            out IntPtr tokenHandle);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

        [DllImport("kernel32.dll")]
        private static extern uint WTSGetActiveConsoleSessionId();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DuplicateTokenEx(
            IntPtr existingTokenHandle,
            uint desiredAccess,
            IntPtr tokenAttributes,
            int impersonationLevel,
            int tokenType,
            out IntPtr duplicateTokenHandle);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool CreateEnvironmentBlock(
            out IntPtr environment,
            IntPtr token,
            [MarshalAs(UnmanagedType.Bool)] bool inherit);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool DestroyEnvironmentBlock(IntPtr environment);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessAsUser(
            IntPtr hToken,
            string? lpApplicationName,
            string? lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcessWithTokenW(
            IntPtr hToken,
            uint dwLogonFlags,
            string? lpApplicationName,
            string? lpCommandLine,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        #endregion
    }
}
