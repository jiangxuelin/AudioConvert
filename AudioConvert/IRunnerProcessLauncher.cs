using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AudioConvert
{
    public interface IRunnerProcessLauncher
    {
        RunnerLaunchResult Launch(string runnerPath, string arguments);
    }

    /// <summary>
    /// 淇濆瓨澶栭儴 Runner 杩涚▼鍙婂叾杈撳嚭璇诲彇鍣紝骞惰礋璐ｉ噴鏀惧拰缁堟杩涚▼璧勬簮銆?    /// </summary>
    public sealed class RunnerLaunchResult : IDisposable
    {
        public bool Started { get; set; }

        public Process? Process { get; set; }

        public StreamReader? StandardOutput { get; set; }

        public StreamReader? StandardError { get; set; }

        public string? LaunchMode { get; set; }

        public string? StartupError { get; set; }

        private Action? _disposeResources;

        public RunnerLaunchResult() { }

        public RunnerLaunchResult(Action disposeResources)
        {
            _disposeResources = disposeResources;
        }

        public void KillProcess()
        {
            try
            {
                if (Process != null && !Process.HasExited)
                {
                    Process.Kill();
                }
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            _disposeResources?.Invoke();
            _disposeResources = null;

            try { Process?.Dispose(); }
            catch { }
            Process = null;
        }

        public static RunnerLaunchResult Failed(string error, string launchMode)
        {
            return new RunnerLaunchResult
            {
                Started = false,
                StartupError = error,
                LaunchMode = launchMode,
            };
        }
    }

    /// <summary>
    /// 鐩存帴浠ュ綋鍓嶈繘绋嬫潈闄愬惎鍔ㄥ閮ㄨВ瀵?Runner銆?    /// </summary>
    public sealed class DirectRunnerLauncher : IRunnerProcessLauncher
    {
        public RunnerLaunchResult Launch(string runnerPath, string arguments)
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
                RunnerProcessStartInfoCompat.ConfigureChildRuntimeEnvironment(psi);

                Process? process = Process.Start(psi);
                if (process == null)
                {
                    return RunnerLaunchResult.Failed(
                        "Unable to start runner.", "direct");
                }

                return new RunnerLaunchResult
                {
                    Started = true,
                    Process = process,
                    StandardOutput = process.StandardOutput,
                    StandardError = process.StandardError,
                    LaunchMode = "direct",
                };
            }
            catch (Exception ex)
            {
                return RunnerLaunchResult.Failed(
                    "Failed to start runner: " + ex.Message, "direct");
            }
        }

    }

    public sealed class CmdRunnerLauncher : IRunnerProcessLauncher
    {
        public RunnerLaunchResult Launch(string runnerPath, string arguments)
        {
            try
            {
                string workingDirectory = Path.GetDirectoryName(runnerPath) ?? AppDomain.CurrentDomain.BaseDirectory;
                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                    Arguments = BuildCmdArguments(runnerPath, arguments),
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                RunnerProcessStartInfoCompat.ConfigureChildRuntimeEnvironment(psi);

                Process? process = Process.Start(psi);
                if (process == null)
                {
                    return RunnerLaunchResult.Failed(
                        "Unable to start QQMusicDecryptRunner.exe through cmd.exe.", "cmd");
                }

                return new RunnerLaunchResult
                {
                    Started = true,
                    Process = process,
                    StandardOutput = process.StandardOutput,
                    StandardError = process.StandardError,
                    LaunchMode = "cmd",
                };
            }
            catch (Exception ex)
            {
                return RunnerLaunchResult.Failed(
                    "Failed to start QQ Music decrypt runner through cmd.exe: " + ex.Message,
                    "cmd");
            }
        }

        private static string BuildCmdArguments(string runnerPath, string arguments)
        {
            return "/d /c call \"" + runnerPath + "\" " + arguments;
        }
    }

    public sealed class CleanEnvironmentRunnerLauncher : IRunnerProcessLauncher
    {
        public RunnerLaunchResult Launch(string runnerPath, string arguments)
        {
            string workingDirectory = Path.GetDirectoryName(runnerPath) ?? AppDomain.CurrentDomain.BaseDirectory;
            string runnerFileName = Path.GetFileName(runnerPath);
            string scriptPath = Path.Combine(
                workingDirectory,
                "launch-runner-" + Guid.NewGuid().ToString("N") + ".cmd");

            try
            {
                File.WriteAllLines(
                    scriptPath,
                    new[]
                    {
                        "@echo off",
                        "set APP_CONFIG_FILE=",
                        "set COMPLUS_Version=",
                        "set COMPLUS_ApplicationMigrationRuntimeActivationConfigPath=",
                        "set COR_ENABLE_PROFILING=",
                        "set COR_PROFILER=",
                        "set COR_PROFILER_PATH=",
                        "set CORECLR_ENABLE_PROFILING=",
                        "set CORECLR_PROFILER=",
                        "set CORECLR_PROFILER_PATH=",
                        "echo LOG^|cmd_script_started",
                        "if not exist \"%~dp0" + runnerFileName + "\" (",
                        "  echo ERROR^|Runner executable was not found beside the launch script: " + runnerFileName + " 1>&2",
                        "  exit /b 9009",
                        ")",
                        "\"%~dp0" + runnerFileName + "\" %AC_RUNNER_ARGUMENTS%",
                        "exit /b %ERRORLEVEL%"
                    },
                    Encoding.ASCII);

                var psi = new ProcessStartInfo
                {
                    FileName = ResolveCmdPath(),
                    Arguments = "/d /s /c \"\"" + scriptPath + "\"\"",
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                RunnerProcessStartInfoCompat.ConfigureChildRuntimeEnvironment(psi);
                psi.EnvironmentVariables["AC_RUNNER_ARGUMENTS"] = arguments;

                Process? process = Process.Start(psi);
                if (process == null)
                {
                    TryDeleteScript(scriptPath);
                    return RunnerLaunchResult.Failed(
                        "Unable to start runner through clean environment script.",
                        "cmd_script");
                }

                return new RunnerLaunchResult(() => TryDeleteScript(scriptPath))
                {
                    Started = true,
                    Process = process,
                    StandardOutput = process.StandardOutput,
                    StandardError = process.StandardError,
                    LaunchMode = "cmd_script",
                };
            }
            catch (Exception ex)
            {
                TryDeleteScript(scriptPath);
                return RunnerLaunchResult.Failed(
                    "Failed to start runner through clean environment script: " + ex.Message,
                    "cmd_script");
            }
        }

        private static string ResolveCmdPath()
        {
            string? comSpec = Environment.GetEnvironmentVariable("ComSpec");
            if (!string.IsNullOrWhiteSpace(comSpec) && File.Exists(comSpec))
            {
                return comSpec;
            }

            string? systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
            if (!string.IsNullOrWhiteSpace(systemRoot))
            {
                string candidate = Path.Combine(systemRoot, "System32", "cmd.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "cmd.exe");
        }

        private static void TryDeleteScript(string scriptPath)
        {
            try
            {
                if (File.Exists(scriptPath))
                {
                    File.Delete(scriptPath);
                }
            }
            catch
            {
            }
        }
    }

    public sealed class ShellRunnerLauncher : IRunnerProcessLauncher
    {
        public RunnerLaunchResult Launch(string runnerPath, string arguments)
        {
            try
            {
                string workingDirectory = Path.GetDirectoryName(runnerPath) ?? AppDomain.CurrentDomain.BaseDirectory;
                var psi = new ProcessStartInfo
                {
                    FileName = runnerPath,
                    Arguments = arguments,
                    WorkingDirectory = workingDirectory,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                Process? process = Process.Start(psi);
                if (process == null)
                {
                    return RunnerLaunchResult.Failed(
                        "Unable to start QQMusicDecryptRunner.exe through ShellExecute.", "shell");
                }

                return new RunnerLaunchResult
                {
                    Started = true,
                    Process = process,
                    LaunchMode = "shell",
                };
            }
            catch (Exception ex)
            {
                return RunnerLaunchResult.Failed(
                    "Failed to start QQ Music decrypt runner through ShellExecute: " + ex.Message,
                    "shell");
            }
        }
    }

    internal static class RunnerProcessStartInfoCompat
    {
        public static void ConfigureChildRuntimeEnvironment(ProcessStartInfo processStartInfo)
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
    }
}
