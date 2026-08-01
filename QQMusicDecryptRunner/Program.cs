using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EasyHook;
using Microsoft.Win32;

namespace QQMusicDecryptRunner
{
    
    internal static class Program
    {
        private const int PipeTimeoutMilliseconds = 30000;
        private const int InjectionTimeoutMilliseconds = 30000;
        private const int LaunchTimeoutMilliseconds = 30000;
        private const int CloseTimeoutMilliseconds = 5000;
        private const int HideWindowStabilizeMilliseconds = 5000;
        private const int SwHide = 0;
        private const int EventObjectCreate = 0x8000;
        private const int EventObjectShow = 0x8002;
        private const int EventSystemForeground = 0x0003;
        private const int WineventOutOfContext = 0x0000;
        private const int WineventSkipOwnProcess = 0x0002;
        private const uint CreateSuspended = 0x00000004;
        private const uint StartfUseShowWindow = 0x00000001;
        private const uint StartfUseSize = 0x00000002;
        private const uint StartfUsePosition = 0x00000004;
        private const int GwlExstyle = -20;
        private const int WsExToolwindow = 0x00000080;
        private const int WsExAppwindow = 0x00040000;
        private const uint PmRemove = 0x0001;

        private const uint TokenQuery = 0x0008;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const int TokenElevationInfo = 20;
        private const int TokenIntegrityLevelInfo = 25;
        private const int SecurityMandatoryHighRid = 0x3000;

        private static readonly (RegistryKey Root, string SubKey, string ValueName)[] RegistryPaths =
        {
            (Registry.LocalMachine, @"SOFTWARE\Tencent\QQMusic", "Install"),
            (Registry.LocalMachine, @"SOFTWARE\Tencent\QQMusic", "AppPath"),
            (Registry.CurrentUser,  @"SOFTWARE\Tencent\QQMusic", "Install"),
            (Registry.CurrentUser,  @"SOFTWARE\Tencent\QQMusic", "AppPath"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Tencent\QQMusic", "Install"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Tencent\QQMusic", "AppPath"),
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\QQMusic.exe", ""),
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\QQMusic.exe", "Path"),
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\QQMusic", "InstallLocation"),
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\QQMusic", "DisplayIcon"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\QQMusic", "InstallLocation"),
        };

        private const int MaxInjectionAttempts = 2;
        private const int ClrResetWaitMilliseconds = 5000;
        private const string ClrCacheErrorSignature = "Code: 15";
        private const string InvalidAudioDataSignature = "invalid decrypted audio data";
        private static readonly int[] DecryptReadinessRetryDelays =
        {
            2000,
            5000,
            8000
        };
        private static bool _exitProcessAfterFinally;
        private static int _exitProcessCode;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            QqMusicProcessContext? processContext = null;
            IDisposable? statusFileLogScope = null;

            try
            {
                args = ExpandRequestFileArguments(args);
                args = ConfigureStatusFileLogging(args, out statusFileLogScope);

                if (args.Length < 2)
                {
                    Console.Error.WriteLine("ERROR|Missing arguments: inputPath and outputDirectory are required.");
                    return 1;
                }

                string inputPath = args[0];
                string outputDirectory = args[1];

                if (!File.Exists(inputPath))
                {
                    Console.Error.WriteLine($"ERROR|Input file does not exist: {inputPath}");
                    return 1;
                }

                bool runnerElevated = IsCurrentProcessElevated();
                Console.WriteLine($"LOG|runner_elevated={runnerElevated}; runner_pid={Process.GetCurrentProcess().Id}; runner_session={Process.GetCurrentProcess().SessionId}; runner_is64bit={Environment.Is64BitProcess}");

                string hookPath = FindHookPath();
                if (!File.Exists(hookPath))
                {
                    Console.Error.WriteLine($"ERROR|QQMusicDecryptHook.dll was not found: {hookPath}");
                    return 3;
                }

                Console.WriteLine("LOG|runner_base_dir=" + AppDomain.CurrentDomain.BaseDirectory
                    + "; hook_path=" + hookPath);

                Directory.CreateDirectory(outputDirectory);
                bool hadInjectionTimeout = false;

                for (int attempt = 1; attempt <= MaxInjectionAttempts; attempt++)
                {
                    processContext = FindOrLaunchQqMusicProcess();
                    if (processContext == null)
                    {
                        Console.Error.WriteLine("ERROR|QQ Music was not found and could not be launched automatically."
                            + $" runner_elevated={runnerElevated}");
                        return 2;
                    }

                    if (processContext.Process.Id == Process.GetCurrentProcess().Id)
                    {
                        Console.Error.WriteLine("ERROR|Target process was incorrectly resolved to the decrypt runner itself.");
                        return 2;
                    }

                    int? targetIntegrity = GetProcessIntegrityLevel(processContext.Process.Id);
                    int targetSessionId = -1;
                    try { targetSessionId = processContext.Process.SessionId; } catch { }
                    Console.WriteLine($"LOG|target_pid={processContext.Process.Id}; target_session={targetSessionId}"
                        + $"; target_integrity={targetIntegrity?.ToString() ?? "unknown"}"
                        + $"; target_path={SafeGetProcessPath(processContext.Process)}"
                        + $"; reuse_existing={!processContext.LaunchedByTool}"
                        + $"; attempt={attempt}/{MaxInjectionAttempts}");

                    int result = 0;
                    for (int decryptAttempt = 1;
                         decryptAttempt <= DecryptReadinessRetryDelays.Length + 1;
                         decryptAttempt++)
                    {
                        result = InjectAndWaitForResult(
                            processContext, hookPath, inputPath, outputDirectory);

                        if (result != ExitCodeInvalidAudioData)
                        {
                            break;
                        }

                        if (decryptAttempt <= DecryptReadinessRetryDelays.Length)
                        {
                            int delay = DecryptReadinessRetryDelays[decryptAttempt - 1];
                            Console.WriteLine("LOG|QQMusicCommon returned encrypted-looking data; "
                                + $"waiting for QQ Music decrypt service readiness ({decryptAttempt + 1}/{DecryptReadinessRetryDelays.Length + 1})...");
                            Thread.Sleep(delay);
                        }
                    }

                    if (result == ExitCodeInvalidAudioData)
                    {
                        if (hadInjectionTimeout)
                        {
                            ForceProcessExitAfterFinally(7);
                        }

                        return 7;
                    }

                    if (result != ExitCodeClrCacheConflict &&
                        result != ExitCodeInjectionTimeout)
                    {
                        if (hadInjectionTimeout)
                        {
                            ForceProcessExitAfterFinally(result);
                        }

                        return result;
                    }

                    if (result == ExitCodeInjectionTimeout)
                    {
                        hadInjectionTimeout = true;
                    }

                    if (attempt < MaxInjectionAttempts)
                    {
                        if (result == ExitCodeClrCacheConflict)
                        {
                            Console.WriteLine("LOG|CLR cache conflict detected (Code: 15). "
                                + "Restarting QQ Music to reset .NET runtime state...");
                        }
                        else
                        {
                            Console.WriteLine("LOG|QQ Music hook injection timed out. "
                                + "Restarting QQ Music and retrying with a fresh process...");
                        }

                        KillQqMusicForClrReset(processContext.Process);
                        processContext = null;
                    }
                    else
                    {
                        Console.Error.WriteLine("ERROR|Injection failed after restart. "
                            + "Please close QQ Music manually and retry.");
                        if (hadInjectionTimeout)
                        {
                            ForceProcessExitAfterFinally(4);
                        }

                        return 4;
                    }
                }

                return 4;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("ERROR|" + ex.Message);
                return 9;
            }
            finally
            {
                if (processContext?.LaunchedByTool == true)
                {
                    ShutdownLaunchedQqMusic(processContext.Process);
                }

                statusFileLogScope?.Dispose();

                if (_exitProcessAfterFinally)
                {
                    Environment.Exit(_exitProcessCode);
                }
            }
        }

        private static void ForceProcessExitAfterFinally(int exitCode)
        {
            _exitProcessAfterFinally = true;
            _exitProcessCode = exitCode;
        }

        private static string[] ExpandRequestFileArguments(string[] args)
        {
            if (args.Length != 2 ||
                !string.Equals(args[0], "--request-file", StringComparison.OrdinalIgnoreCase))
            {
                return args;
            }

            string requestFilePath = args[1];
            try
            {
                string[] requestArgs = File.ReadAllLines(requestFilePath, Encoding.UTF8);
                if (requestArgs.Length == 0)
                {
                    Console.Error.WriteLine("ERROR|Request file is empty: " + requestFilePath);
                    return args;
                }

                return requestArgs;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("ERROR|Failed to read request file: " + exception.Message);
                return args;
            }
        }

        private static string[] ConfigureStatusFileLogging(
            string[] args,
            out IDisposable? statusFileLogScope)
        {
            statusFileLogScope = null;
            List<string> remainingArgs = new List<string>(args.Length);

            for (int index = 0; index < args.Length; index++)
            {
                string argument = args[index];
                if (!string.Equals(argument, "--status-file", StringComparison.OrdinalIgnoreCase))
                {
                    remainingArgs.Add(argument);
                    continue;
                }

                if (index + 1 >= args.Length)
                {
                    Console.Error.WriteLine("ERROR|Missing value for --status-file.");
                    continue;
                }

                string statusFilePath = args[++index];
                if (statusFileLogScope == null)
                {
                    statusFileLogScope = TryCreateStatusFileLogScope(statusFilePath);
                }
            }

            return remainingArgs.ToArray();
        }

        private static IDisposable? TryCreateStatusFileLogScope(string statusFilePath)
        {
            try
            {
                string? directory = Path.GetDirectoryName(statusFilePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                TextWriter originalOut = Console.Out;
                TextWriter originalError = Console.Error;
                StreamWriter statusWriter = new StreamWriter(
                    new FileStream(statusFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                    new UTF8Encoding(false))
                {
                    AutoFlush = true
                };

                object syncRoot = new object();
                Console.SetOut(new TeeTextWriter(originalOut, statusWriter, syncRoot));
                Console.SetError(new TeeTextWriter(originalError, statusWriter, syncRoot));

                return new StatusFileLogScope(originalOut, originalError, statusWriter);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("ERROR|Failed to create status file: " + exception.Message);
                return null;
            }
        }

        private const int ExitCodeClrCacheConflict = -15;
        private const int ExitCodeInvalidAudioData = -16;
        private const int ExitCodeInjectionTimeout = -17;

        private static string FindHookPath()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDirectory, "QQMusicDecryptHook.dll"),
                Path.Combine(baseDirectory, "MP3AudioConverter.QQMusicDecryptHook.dll")
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return candidates[0];
        }

        private static int InjectAndWaitForResult(
            QqMusicProcessContext processContext,
            string hookPath,
            string inputPath,
            string outputDirectory)
        {
            string pipeName = "QQMusicDecrypt_" + Guid.NewGuid().ToString("N");

            using (var pipeServer = new NamedPipeServerStream(
                pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous))
            {
                try
                {
                    Console.WriteLine("LOG|inject_start");
                    Exception? injectException;
                    bool injectionCompleted = TryInjectWithTimeout(
                        processContext,
                        hookPath,
                        pipeName,
                        inputPath,
                        outputDirectory,
                        out injectException);

                    if (!injectionCompleted)
                    {
                        Console.Error.WriteLine("ERROR|Timed out while injecting QQ Music hook.");
                        return ExitCodeInjectionTimeout;
                    }

                    if (injectException != null)
                    {
                        if (injectException.Message.Contains(ClrCacheErrorSignature))
                        {
                            return ExitCodeClrCacheConflict;
                        }

                        Console.Error.WriteLine("ERROR|" + injectException.Message);
                        return 4;
                    }

                    Console.WriteLine("LOG|inject_returned");
                }
                catch (Exception injectException)
                {
                    if (injectException.Message.Contains(ClrCacheErrorSignature))
                    {
                        return ExitCodeClrCacheConflict;
                    }

                    Console.Error.WriteLine("ERROR|" + injectException.Message);
                    return 4;
                }

                Console.WriteLine("LOG|pipe_wait_start");
                Task waitConnectionTask = pipeServer.WaitForConnectionAsync();
                if (!waitConnectionTask.Wait(PipeTimeoutMilliseconds))
                {
                    Console.Error.WriteLine("ERROR|Timed out while waiting for QQ Music decrypt result.");
                    return ExitCodeInjectionTimeout;
                }
                Console.WriteLine("LOG|pipe_connected");

                using (var reader = new StreamReader(pipeServer, Encoding.UTF8))
                {
                    while (true)
                    {
                        Task<string?> readTask = reader.ReadLineAsync();
                        if (!readTask.Wait(PipeTimeoutMilliseconds))
                        {
                            Console.Error.WriteLine("ERROR|QQ Music did not return a decrypt result in time.");
                            return 5;
                        }

                        string? line = readTask.Result;
                        if (line == null)
                        {
                            Console.Error.WriteLine("ERROR|QQ Music ended the decrypt pipe unexpectedly.");
                            return 6;
                        }

                        Console.WriteLine(line);

                        if (line.StartsWith("RESULT|", StringComparison.Ordinal))
                        {
                            return 0;
                        }

                        if (line.StartsWith("ERROR|", StringComparison.Ordinal))
                        {
                            if (line.IndexOf(InvalidAudioDataSignature, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                return ExitCodeInvalidAudioData;
                            }

                            return 7;
                        }
                    }
                }
            }
        }

        private static void KillQqMusicForClrReset(Process staleProcess)
        {
            try
            {
                if (!staleProcess.HasExited)
                {
                    Console.WriteLine($"LOG|Killing QQ Music PID={staleProcess.Id} to reset CLR state...");
                    staleProcess.Kill();
                    staleProcess.WaitForExit(ClrResetWaitMilliseconds);
                }
            }
            catch
            {
            }

            foreach (string processName in new[] { "QQMusic", "QQMusicService", "QQMusicPC" })
            {
                foreach (Process remaining in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        if (!remaining.HasExited)
                        {
                            Console.WriteLine($"LOG|Also killing related process {remaining.ProcessName} PID={remaining.Id}");
                            remaining.Kill();
                            remaining.WaitForExit(ClrResetWaitMilliseconds);
                        }
                    }
                    catch
                    {
                    }
                }
            }

            Thread.Sleep(2000);
        }

        private static QqMusicProcessContext? FindOrLaunchQqMusicProcess()
        {
            Process? running = FindRunningQqMusicProcess();
            if (running != null)
            {
                return new QqMusicProcessContext(running, launchedByTool: false);
            }

            string? exePath = FindInstallPath();
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return null;
            }

            Process launchedProcess = LaunchAndWaitForReady(exePath!);
            return new QqMusicProcessContext(launchedProcess, launchedByTool: true);
        }

        private static Process? FindRunningQqMusicProcess()
        {
            int currentProcessId = Process.GetCurrentProcess().Id;

            foreach (string processName in new[] { "QQMusic", "QQMusicService", "QQMusicPC" })
            {
                Process[] processes = Process.GetProcessesByName(processName);
                if (processes.Length == 0)
                {
                    continue;
                }

                foreach (Process process in processes)
                {
                    if (process.Id == currentProcessId)
                    {
                        continue;
                    }

                    try
                    {
                        if ((process.MainWindowHandle != IntPtr.Zero || IsQqMusicExecutable(process))
                            && IsProcessCompatibleForInjection(process))
                        {
                            return process;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            foreach (Process process in Process.GetProcesses())
            {
                if (process.Id == currentProcessId)
                {
                    continue;
                }

                try
                {
                    if (process.ProcessName.Equals("QQMusicDecryptRunner", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (process.ProcessName.IndexOf("QQMusic", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        IsQqMusicExecutable(process) &&
                        IsProcessCompatibleForInjection(process))
                    {
                        return process;
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static string? FindInstallPath()
        {
            foreach (var entry in RegistryPaths)
            {
                try
                {
                    using (RegistryKey? key = entry.Root.OpenSubKey(entry.SubKey, false))
                    {
                        if (key == null)
                        {
                            continue;
                        }

                        string? raw = key.GetValue(entry.ValueName)?.ToString()?.Trim();
                        if (string.IsNullOrWhiteSpace(raw))
                        {
                            continue;
                        }

                        string? resolved = ResolveExePath(raw!);
                        if (!string.IsNullOrWhiteSpace(resolved))
                        {
                            return resolved;
                        }
                    }
                }
                catch
                {
                }
            }

            return null;
        }

        private static string? ResolveExePath(string raw)
        {
            string value = raw.Trim('"', ' ');
            int commaIndex = value.IndexOf(',');
            if (commaIndex > 0)
            {
                value = value.Substring(0, commaIndex).Trim();
            }

            if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(value))
            {
                return value;
            }

            if (Directory.Exists(value))
            {
                foreach (string candidate in new[]
                {
                    Path.Combine(value, "QQMusic.exe"),
                    Path.Combine(value, "bin", "QQMusic.exe"),
                    Path.Combine(value, "QQMusicPC.exe"),
                    Path.Combine(value, "bin", "QQMusicPC.exe")
                })
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return null;
        }

        private static bool IsQqMusicExecutable(Process process)
        {
            try
            {
                string? mainModulePath = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(mainModulePath))
                {
                    return false;
                }

                string fileName = Path.GetFileName(mainModulePath);
                return fileName.Equals("QQMusic.exe", StringComparison.OrdinalIgnoreCase) ||
                       fileName.Equals("QQMusicPC.exe", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsCurrentProcessElevated()
        {
            IntPtr tokenHandle = IntPtr.Zero;
            try
            {
                if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out tokenHandle))
                {
                    return false;
                }

                int size = 4;
                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (!GetTokenInformation(tokenHandle, TokenElevationInfo, buffer, size, out _))
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

        private static int? GetProcessIntegrityLevel(int processId)
        {
            IntPtr hProcess = IntPtr.Zero;
            IntPtr tokenHandle = IntPtr.Zero;
            IntPtr buffer = IntPtr.Zero;

            try
            {
                hProcess = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
                if (hProcess == IntPtr.Zero)
                {
                    return null;
                }

                if (!OpenProcessToken(hProcess, TokenQuery, out tokenHandle))
                {
                    return null;
                }

                int requiredSize = 0;
                GetTokenInformation(tokenHandle, TokenIntegrityLevelInfo, IntPtr.Zero, 0, out requiredSize);
                if (requiredSize == 0)
                {
                    return null;
                }

                buffer = Marshal.AllocHGlobal(requiredSize);
                if (!GetTokenInformation(tokenHandle, TokenIntegrityLevelInfo, buffer, requiredSize, out _))
                {
                    return null;
                }

                IntPtr sidPtr = Marshal.ReadIntPtr(buffer);
                if (sidPtr == IntPtr.Zero)
                {
                    return null;
                }

                IntPtr subAuthorityCountPtr = GetSidSubAuthorityCount(sidPtr);
                if (subAuthorityCountPtr == IntPtr.Zero)
                {
                    return null;
                }

                byte subAuthorityCount = Marshal.ReadByte(subAuthorityCountPtr);
                if (subAuthorityCount == 0)
                {
                    return null;
                }

                IntPtr ridPtr = GetSidSubAuthority(sidPtr, (uint)(subAuthorityCount - 1));
                if (ridPtr == IntPtr.Zero)
                {
                    return null;
                }

                return (int)Marshal.ReadInt32(ridPtr);
            }
            catch
            {
                return null;
            }
            finally
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                if (tokenHandle != IntPtr.Zero) CloseHandle(tokenHandle);
                if (hProcess != IntPtr.Zero) CloseHandle(hProcess);
            }
        }

        private static bool IsProcessCompatibleForInjection(Process process)
        {
            try
            {
                int runnerSessionId = Process.GetCurrentProcess().SessionId;
                if (process.SessionId != runnerSessionId)
                {
                    return false;
                }
            }
            catch
            {
                return false;
            }

            bool runnerElevated = IsCurrentProcessElevated();
            if (runnerElevated)
            {
                return true;
            }

            int? targetIntegrity = GetProcessIntegrityLevel(process.Id);
            if (targetIntegrity == null)
            {
                return false;
            }

            return targetIntegrity.Value < SecurityMandatoryHighRid;
        }

        private static Process LaunchAndWaitForReady(string exePath)
        {
            try
            {
                return LaunchWithShellAndWaitForReady(exePath);
            }
            catch (Exception exception)
            {
                Console.WriteLine("LOG|QQ Music shell launch failed: " + exception.Message
                    + " Falling back to hidden CreateProcess launch.");
                return LaunchHiddenAndWaitForReady(exePath);
            }
        }

        private static Process LaunchWithShellAndWaitForReady(string exePath)
        {
            string workingDirectory = Path.GetDirectoryName(exePath) ?? AppDomain.CurrentDomain.BaseDirectory;

            Console.WriteLine("LOG|Launching QQ Music through the user shell: " + exePath);
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = workingDirectory,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized
            };

            Process? launchedProcess = Process.Start(startInfo);
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < LaunchTimeoutMilliseconds)
            {
                PumpMessages();
                Thread.Sleep(200);

                Process? running = FindRunningQqMusicProcess();
                if (running != null)
                {
                    HideProcessWindows(running.Id);
                    Console.WriteLine($"LOG|QQ Music started through shell (PID={running.Id}).");
                    return running;
                }

                if (launchedProcess != null)
                {
                    try
                    {
                        launchedProcess.Refresh();
                    }
                    catch
                    {
                    }
                }
            }

            throw new TimeoutException("Timed out while waiting for QQ Music to finish shell startup.");
        }

        private static Process LaunchHiddenAndWaitForReady(string exePath)
        {
            string workingDirectory = Path.GetDirectoryName(exePath) ?? AppDomain.CurrentDomain.BaseDirectory;
            string commandLine = "\"" + exePath + "\"";

            string desktopName = "QQMHidden_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            IntPtr hDesktop = CreateDesktopW(
                desktopName,
                IntPtr.Zero,
                IntPtr.Zero,
                0,
                DesktopAllAccess,
                IntPtr.Zero);
            bool useHiddenDesktop = hDesktop != IntPtr.Zero;
            if (!useHiddenDesktop)
            {
                Console.WriteLine("LOG|Hidden desktop creation failed. Win32Error="
                    + Marshal.GetLastWin32Error());
            }

            STARTUPINFO startupInfo = new STARTUPINFO
            {
                cb = Marshal.SizeOf(typeof(STARTUPINFO)),
                dwFlags = StartfUseShowWindow | StartfUsePosition | StartfUseSize,
                wShowWindow = SwHide,
                dwX = -32000,
                dwY = -32000,
                dwXSize = 1,
                dwYSize = 1,
                lpDesktop = useHiddenDesktop ? desktopName : null
            };

            if (!CreateProcessW(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CreateSuspended,
                IntPtr.Zero,
                workingDirectory,
                ref startupInfo,
                out PROCESS_INFORMATION processInformation))
            {
                if (useHiddenDesktop)
                {
                    CloseDesktop(hDesktop);
                }

                throw new InvalidOperationException($"Unable to launch QQ Music. Win32Error={Marshal.GetLastWin32Error()}");
            }

            try
            {
                using (var hiddenLaunch = new HiddenLaunchScope(processInformation.dwProcessId))
                {
                    ResumeThread(processInformation.hThread);

                    Stopwatch idleWatch = Stopwatch.StartNew();
                    while (idleWatch.ElapsedMilliseconds < 5000)
                    {
                        uint idleResult = WaitForInputIdle(processInformation.hProcess, 100);
                        hiddenLaunch.PumpAndHide();
                        if (idleResult == 0)
                        {
                            break;
                        }
                    }

                    bool launchedProcessExited = false;
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    while (stopwatch.ElapsedMilliseconds < LaunchTimeoutMilliseconds)
                    {
                        hiddenLaunch.PumpAndHide();
                        Thread.Sleep(100);

                        if (!launchedProcessExited)
                        {
                            try
                            {
                                IntPtr hCheck = OpenProcess(0x00100000, false, processInformation.dwProcessId);
                                if (hCheck == IntPtr.Zero)
                                {
                                    launchedProcessExited = true;
                                }
                                else
                                {
                                    uint exitCode;
                                    if (GetExitCodeProcess(hCheck, out exitCode) && exitCode != 259)
                                    {
                                        launchedProcessExited = true;
                                    }
                                    CloseHandle(hCheck);
                                }
                            }
                            catch
                            {
                            }
                        }

                        Process? running = FindRunningQqMusicProcess();
                        if (running != null)
                        {
                            hiddenLaunch.Stabilize(HideWindowStabilizeMilliseconds);
                            Console.WriteLine($"LOG|QQ Music started hidden (PID={running.Id}).");
                            return running;
                        }

                        if (launchedProcessExited && stopwatch.ElapsedMilliseconds > 5000)
                        {
                            throw new InvalidOperationException(
                                "QQ Music exited immediately after launch (possible single-instance protection). "
                                + "If QQ Music is already running as administrator, please close it and retry.");
                        }
                    }

                    throw new TimeoutException("Timed out while waiting for QQ Music to finish startup.");
                }
            }
            finally
            {
                CloseHandle(processInformation.hThread);
                CloseHandle(processInformation.hProcess);
                if (useHiddenDesktop)
                {
                    CloseDesktop(hDesktop);
                }
            }
        }

        private static void ShutdownLaunchedQqMusic(Process process)
        {
            try
            {
                if (process.HasExited)
                {
                    Console.WriteLine("LOG|The temporary QQ Music process has already exited.");
                    return;
                }
            }
            catch
            {
                Console.WriteLine("LOG|Unable to verify QQ Music process state; skipping automatic shutdown.");
                return;
            }

            Console.WriteLine("LOG|Closing the temporary QQ Music process...");

            try
            {
                HideProcessWindows(process.Id);
                PumpMessages();

                bool closeRequested = false;
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero)
                    {
                        closeRequested = process.CloseMainWindow();
                    }
                }
                catch
                {
                }

                if (closeRequested && process.WaitForExit(CloseTimeoutMilliseconds))
                {
                    Console.WriteLine("LOG|Temporary QQ Music process closed cleanly.");
                    return;
                }

                try
                {
                    process.Kill();
                    if (process.WaitForExit(CloseTimeoutMilliseconds))
                    {
                        Console.WriteLine("LOG|Temporary QQ Music process was terminated.");
                        return;
                    }
                }
                catch
                {
                }

                Console.WriteLine("LOG|Failed to close the temporary QQ Music process automatically.");
            }
            catch
            {
                Console.WriteLine("LOG|An error occurred while closing the temporary QQ Music process.");
            }
        }

        private static void PumpMessages()
        {
            while (PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, PmRemove))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }

        private static void SuppressWindow(IntPtr hwnd)
        {
            int exStyle = GetWindowLong(hwnd, GwlExstyle);
            SetWindowLong(hwnd, GwlExstyle, (exStyle | WsExToolwindow) & ~WsExAppwindow);
            ShowWindowAsync(hwnd, SwHide);
        }

        private static void HideProcessWindows(int processId)
        {
            foreach (IntPtr handle in EnumerateTopLevelWindows(processId))
            {
                SuppressWindow(handle);
            }
        }

        private static IEnumerable<IntPtr> EnumerateTopLevelWindows(int processId)
        {
            var handles = new List<IntPtr>();

            EnumWindows((hWnd, lParam) =>
            {
                GetWindowThreadProcessId(hWnd, out uint windowProcessId);
                if (windowProcessId == processId)
                {
                    handles.Add(hWnd);
                }

                return true;
            }, IntPtr.Zero);

            return handles;
        }

        private static void LogStage(
            string stage,
            string inputPath,
            QqMusicProcessContext? processContext = null,
            string? hookPath = null,
            Exception? exception = null,
            params (string Key, string Value)[] extraFields)
        {
            List<(string Key, string Value)> fields = CreateCommonFields(inputPath, processContext, hookPath);

            if (extraFields != null)
            {
                fields.AddRange(extraFields);
            }

            if (exception != null)
            {
                fields.Add(("exception_type", exception.GetType().FullName ?? exception.GetType().Name));
                fields.Add(("exception_message", exception.Message));
                fields.Add(("stack", exception.ToString()));
            }

            Console.WriteLine("LOG|" + BuildStructuredMessage(stage, fields));
        }

        private static void WriteStructuredError(
            string summary,
            string stage,
            string inputPath,
            QqMusicProcessContext? processContext = null,
            string? hookPath = null,
            Exception? exception = null,
            params (string Key, string Value)[] extraFields)
        {
            List<(string Key, string Value)> fields = CreateCommonFields(inputPath, processContext, hookPath);
            fields.Add(("stage", stage));

            if (extraFields != null)
            {
                fields.AddRange(extraFields);
            }

            if (exception != null)
            {
                fields.Add(("exception_type", exception.GetType().FullName ?? exception.GetType().Name));
                fields.Add(("exception_message", exception.Message));
                fields.Add(("stack", exception.ToString()));
            }

            Console.Error.WriteLine("ERROR|" + summary + " Details: " + BuildFieldList(fields));
        }

        private static List<(string Key, string Value)> CreateCommonFields(
            string inputPath,
            QqMusicProcessContext? processContext,
            string? hookPath)
        {
            List<(string Key, string Value)> fields = new List<(string Key, string Value)>
            {
                ("runner_base_dir", AppDomain.CurrentDomain.BaseDirectory),
                ("hook_path", hookPath ?? string.Empty),
                ("input_extension", Path.GetExtension(inputPath) ?? string.Empty),
                (
                    "launched_by_tool",
                    processContext == null ? "unknown" : processContext.LaunchedByTool ? "true" : "false"
                ),
            };

            if (processContext != null)
            {
                fields.Add(("qq_pid", processContext.Process.Id.ToString()));
                fields.Add(("qq_exe_path", SafeGetProcessPath(processContext.Process)));
            }

            return fields;
        }

        private static string BuildStructuredMessage(
            string stage,
            IEnumerable<(string Key, string Value)> fields)
        {
            return "stage=" + Sanitize(stage) + "; " + BuildFieldList(fields);
        }

        private static string BuildFieldList(IEnumerable<(string Key, string Value)> fields)
        {
            StringBuilder builder = new StringBuilder();
            bool hasAny = false;

            foreach ((string Key, string Value) field in fields)
            {
                if (string.IsNullOrWhiteSpace(field.Key))
                {
                    continue;
                }

                if (hasAny)
                {
                    builder.Append("; ");
                }

                builder.Append(field.Key);
                builder.Append('=');
                builder.Append(Sanitize(field.Value));
                hasAny = true;
            }

            return builder.ToString();
        }

        private static string SafeGetProcessPath(Process process)
        {
            try
            {
                return process.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string Sanitize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value!
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Trim();
        }

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
            [In] ref STARTUPINFO lpStartupInfo,
            out PROCESS_INFORMATION lpProcessInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr hThread);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint WaitForInputIdle(IntPtr hProcess, uint dwMilliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            uint processAccess,
            [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle,
            uint processId);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenProcessToken(
            IntPtr processHandle,
            uint desiredAccess,
            out IntPtr tokenHandle);

        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetTokenInformation(
            IntPtr tokenHandle,
            int tokenInformationClass,
            IntPtr tokenInformation,
            int tokenInformationLength,
            out int returnLength);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthority(IntPtr pSid, uint nSubAuthority);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthorityCount(IntPtr pSid);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr hmodWinEventProc,
            WinEventDelegate lpfnWinEventProc,
            uint idProcess,
            uint idThread,
            uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateDesktopW(
            string lpszDesktop,
            IntPtr lpszDevice,
            IntPtr pDevmode,
            uint dwFlags,
            uint dwDesiredAccess,
            IntPtr lpsa);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseDesktop(IntPtr hDesktop);

        private const uint DesktopAllAccess = 0x000F01FF;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private delegate void WinEventDelegate(
            IntPtr hWinEventHook,
            uint eventType,
            IntPtr hwnd,
            int idObject,
            int idChild,
            uint dwEventThread,
            uint dwmsEventTime);

        [StructLayout(LayoutKind.Sequential)]
        /// <summary>
        /// Windows 娑堟伅寰幆涓娇鐢ㄧ殑鍘熺敓娑堟伅缁撴瀯銆?        /// </summary>
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int pt_x;
            public int pt_y;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        /// <summary>
        /// Windows API 鍒涘缓 QQ 闊充箰杩涚▼鏃朵娇鐢ㄧ殑鍚姩閰嶇疆缁撴瀯銆?        /// </summary>
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
        /// <summary>
        /// Windows API 鍒涘缓 QQ 闊充箰杩涚▼鍚庤繑鍥炵殑杩涚▼鍜岀嚎绋嬪彞鏌勪俊鎭€?        /// </summary>
        private struct PROCESS_INFORMATION
        {
            public IntPtr hProcess;
            public IntPtr hThread;
            public uint dwProcessId;
            public uint dwThreadId;
        }

        private sealed class TeeTextWriter : TextWriter
        {
            private readonly TextWriter _primaryWriter;
            private readonly TextWriter _secondaryWriter;
            private readonly object _syncRoot;

            public TeeTextWriter(
                TextWriter primaryWriter,
                TextWriter secondaryWriter,
                object syncRoot)
            {
                _primaryWriter = primaryWriter;
                _secondaryWriter = secondaryWriter;
                _syncRoot = syncRoot;
            }

            public override Encoding Encoding => _primaryWriter.Encoding;

            public override void Write(char value)
            {
                lock (_syncRoot)
                {
                    _primaryWriter.Write(value);
                    _secondaryWriter.Write(value);
                }
            }

            public override void Write(string? value)
            {
                lock (_syncRoot)
                {
                    _primaryWriter.Write(value);
                    _secondaryWriter.Write(value);
                }
            }

            public override void WriteLine(string? value)
            {
                lock (_syncRoot)
                {
                    _primaryWriter.WriteLine(value);
                    _secondaryWriter.WriteLine(value);
                }
            }

            public override void Flush()
            {
                lock (_syncRoot)
                {
                    _primaryWriter.Flush();
                    _secondaryWriter.Flush();
                }
            }
        }

        private static bool TryInjectWithTimeout(
            QqMusicProcessContext processContext,
            string hookPath,
            string pipeName,
            string inputPath,
            string outputDirectory,
            out Exception? injectException)
        {
            Exception? workerException = null;

            Thread injectionThread = new Thread(() =>
            {
                try
                {
                    RemoteHooking.Inject(
                        processContext.Process.Id,
                        InjectionOptions.DoNotRequireStrongName,
                        hookPath,
                        hookPath,
                        pipeName,
                        inputPath,
                        outputDirectory,
                        AppDomain.CurrentDomain.BaseDirectory,
                        hookPath,
                        Path.GetExtension(inputPath) ?? string.Empty,
                        processContext.LaunchedByTool);
                }
                catch (Exception exception)
                {
                    workerException = exception;
                }
            })
            {
                IsBackground = true,
                Name = "QQMusicHookInjection"
            };

            injectionThread.Start();

            if (!injectionThread.Join(InjectionTimeoutMilliseconds))
            {
                injectException = null;
                return false;
            }

            injectException = workerException;
            return true;
        }

        private sealed class StatusFileLogScope : IDisposable
        {
            private readonly TextWriter _originalOut;
            private readonly TextWriter _originalError;
            private readonly TextWriter _statusWriter;

            public StatusFileLogScope(
                TextWriter originalOut,
                TextWriter originalError,
                TextWriter statusWriter)
            {
                _originalOut = originalOut;
                _originalError = originalError;
                _statusWriter = statusWriter;
            }

            public void Dispose()
            {
                Console.SetOut(_originalOut);
                Console.SetError(_originalError);
                _statusWriter.Dispose();
            }
        }

        /// <summary>
        /// 鍦ㄥ伐鍏蜂复鏃跺惎鍔?QQ 闊充箰鏃堕殣钘忓叾绐楀彛骞跺湪浣滅敤鍩熺粨鏉熸椂閲婃斁浜嬩欢閽╁瓙銆?        /// </summary>
        private sealed class HiddenLaunchScope : IDisposable
        {
            private readonly uint _processId;
            private readonly WinEventDelegate _hookCallback;
            private readonly IntPtr _showHookHandle;
            private readonly IntPtr _foregroundHookHandle;

            public HiddenLaunchScope(uint processId)
            {
                _processId = processId;
                _hookCallback = HandleWindowEvent;

                _showHookHandle = SetWinEventHook(
                    (uint)EventObjectCreate,
                    (uint)EventObjectShow,
                    IntPtr.Zero,
                    _hookCallback,
                    processId,
                    0,
                    WineventOutOfContext | WineventSkipOwnProcess);

                _foregroundHookHandle = SetWinEventHook(
                    (uint)EventSystemForeground,
                    (uint)EventSystemForeground,
                    IntPtr.Zero,
                    _hookCallback,
                    processId,
                    0,
                    WineventOutOfContext | WineventSkipOwnProcess);
            }

            public void PumpAndHide()
            {
                PumpMessages();
                HideProcessWindows((int)_processId);
            }

            public void Stabilize(int durationMilliseconds)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();
                while (stopwatch.ElapsedMilliseconds < durationMilliseconds)
                {
                    PumpAndHide();
                    Thread.Sleep(30);
                }
            }

            public void Dispose()
            {
                if (_showHookHandle != IntPtr.Zero)
                {
                    UnhookWinEvent(_showHookHandle);
                }

                if (_foregroundHookHandle != IntPtr.Zero)
                {
                    UnhookWinEvent(_foregroundHookHandle);
                }
            }

            private void HandleWindowEvent(
                IntPtr hWinEventHook,
                uint eventType,
                IntPtr hwnd,
                int idObject,
                int idChild,
                uint dwEventThread,
                uint dwmsEventTime)
            {
                if (hwnd != IntPtr.Zero)
                {
                    SuppressWindow(hwnd);
                }
            }
        }

        private sealed class QqMusicProcessContext
        {
            public QqMusicProcessContext(Process process, bool launchedByTool)
            {
                Process = process;
                LaunchedByTool = launchedByTool;
            }

            public Process Process { get; }

            public bool LaunchedByTool { get; }
        }
    }
}
