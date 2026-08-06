using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AudioConvert.Services
{
    public sealed class PackagedRunnerLauncher : IRunnerProcessLauncher
    {
        public static bool IsCurrentProcessPackaged()
        {
            int length = 0;
            int result = GetCurrentPackageFullName(ref length, null);
            return result != AppmodelErrorNoPackage;
        }

        public RunnerLaunchResult Launch(string runnerPath, string arguments)
        {
            if (string.IsNullOrWhiteSpace(runnerPath) || !File.Exists(runnerPath))
            {
                return RunnerLaunchResult.Failed(
                    "QQMusicDecryptRunner.exe was not found: " + runnerPath,
                    "packaged_no_breakaway");
            }

            IntPtr stdoutRead = IntPtr.Zero;
            IntPtr stdoutWrite = IntPtr.Zero;
            IntPtr stderrRead = IntPtr.Zero;
            IntPtr stderrWrite = IntPtr.Zero;
            IntPtr attributeList = IntPtr.Zero;
            IntPtr desktopAppPolicy = IntPtr.Zero;
            PROCESS_INFORMATION processInformation = new PROCESS_INFORMATION();

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

                IntPtr attributeListSize = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);

                attributeList = Marshal.AllocHGlobal(attributeListSize);
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                desktopAppPolicy = Marshal.AllocHGlobal(sizeof(int));
                Marshal.WriteInt32(desktopAppPolicy, ProcessCreationDesktopAppBreakawayDisableProcessTree);

                if (!UpdateProcThreadAttribute(
                        attributeList,
                        0,
                        (IntPtr)ProcThreadAttributeDesktopAppPolicy,
                        desktopAppPolicy,
                        (IntPtr)sizeof(int),
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                STARTUPINFOEX startupInfo = new STARTUPINFOEX
                {
                    StartupInfo = new STARTUPINFO
                    {
                        cb = Marshal.SizeOf(typeof(STARTUPINFOEX)),
                        dwFlags = StartfUseStdHandles,
                        hStdOutput = stdoutWrite,
                        hStdError = stderrWrite,
                        hStdInput = IntPtr.Zero
                    },
                    lpAttributeList = attributeList
                };

                string commandLine = BuildCommandLine(runnerPath, arguments);
                string workingDirectory = Path.GetDirectoryName(runnerPath) ?? AppDomain.CurrentDomain.BaseDirectory;

                if (!CreateProcessW(
                        null,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        true,
                        CreateNoWindow | ExtendedStartupInfoPresent,
                        IntPtr.Zero,
                        workingDirectory,
                        ref startupInfo,
                        out processInformation))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                CloseHandleIfNeeded(ref stdoutWrite);
                CloseHandleIfNeeded(ref stderrWrite);

                Process process = Process.GetProcessById(unchecked((int)processInformation.dwProcessId));
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
                    LaunchMode = "packaged_no_breakaway"
                };
            }
            catch (Exception ex)
            {
                CloseHandleIfNeeded(ref processInformation.hThread);
                CloseHandleIfNeeded(ref processInformation.hProcess);
                CloseHandleIfNeeded(ref stdoutRead);
                CloseHandleIfNeeded(ref stdoutWrite);
                CloseHandleIfNeeded(ref stderrRead);
                CloseHandleIfNeeded(ref stderrWrite);

                return RunnerLaunchResult.Failed(
                    "Failed to start packaged QQ Music decrypt runner: " + ex.Message,
                    "packaged_no_breakaway");
            }
            finally
            {
                if (attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }

                if (desktopAppPolicy != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(desktopAppPolicy);
                }
            }
        }

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
            string quotedFileName = "\"" + (fileName ?? string.Empty).Replace("\"", "\\\"") + "\"";
            return string.IsNullOrWhiteSpace(arguments)
                ? quotedFileName
                : quotedFileName + " " + arguments;
        }

        private const int AppmodelErrorNoPackage = 15700;
        private const uint StartfUseStdHandles = 0x00000100;
        private const uint CreateNoWindow = 0x08000000;
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint HandleFlagInherit = 0x00000001;
        private const int ProcessCreationDesktopAppBreakawayDisableProcessTree = 0x02;
        private const int ProcThreadAttributeDesktopAppPolicy = 0x00020012;

        [StructLayout(LayoutKind.Sequential)]
        private struct SECURITY_ATTRIBUTES
        {
            public int nLength;
            public IntPtr lpSecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bInheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct STARTUPINFO
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
        private struct STARTUPINFOEX
        {
            public STARTUPINFO StartupInfo;
            public IntPtr lpAttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackageFullName(
            ref int packageFullNameLength,
            StringBuilder? packageFullName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(
            IntPtr lpAttributeList,
            int dwAttributeCount,
            int dwFlags,
            ref IntPtr lpSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr lpAttributeList,
            uint dwFlags,
            IntPtr attribute,
            IntPtr lpValue,
            IntPtr cbSize,
            IntPtr lpPreviousValue,
            IntPtr lpReturnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcessW(
            string? lpApplicationName,
            string lpCommandLine,
            IntPtr lpProcessAttributes,
            IntPtr lpThreadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
            uint dwCreationFlags,
            IntPtr lpEnvironment,
            string lpCurrentDirectory,
            ref STARTUPINFOEX lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CreatePipe(
            out IntPtr hReadPipe,
            out IntPtr hWritePipe,
            ref SECURITY_ATTRIBUTES lpPipeAttributes,
            int nSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetHandleInformation(
            IntPtr hObject,
            uint dwMask,
            uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
    }
}
