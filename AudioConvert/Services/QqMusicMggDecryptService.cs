using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AudioConvert.Services
{
    public interface IQqMusicMggDecryptService
    {
        Task<QqMusicMggDecryptResult> DecryptAsync(
            string inputPath,
            string outputDirectory,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default(CancellationToken)
        );
    }

    /// <summary>
    /// 琛ㄧず QQ 闊充箰鍔犲瘑闊抽瑙ｅ瘑鍚庣殑杈撳嚭璺緞銆佹牸寮忔垨澶辫触鍘熷洜銆?    /// </summary>
    public sealed class QqMusicMggDecryptResult
    {
        private QqMusicMggDecryptResult() { }

        public bool IsSuccess { get; private set; }

        public string? OutputPath { get; private set; }

        public string? DetectedFormat { get; private set; }

        public string? ErrorMessage { get; private set; }

        public static QqMusicMggDecryptResult Success(string outputPath, string detectedFormat)
        {
            return new QqMusicMggDecryptResult
            {
                IsSuccess = true,
                OutputPath = outputPath,
                DetectedFormat = detectedFormat,
            };
        }

        public static QqMusicMggDecryptResult Failure(string errorMessage)
        {
            return new QqMusicMggDecryptResult
            {
                IsSuccess = false,
                ErrorMessage = errorMessage,
            };
        }
    }

    /// <summary>
    /// 閫氳繃鍚姩 Runner 骞舵敞鍏?QQ 闊充箰杩涚▼鏉ヨ皟鐢ㄥ叾鑷韩瑙ｅ瘑鑳藉姏銆?    /// </summary>
    public sealed class QqMusicInjectedMggDecryptService : IQqMusicMggDecryptService
    {
        private const int ProcessTimeoutSeconds = 180;
        private readonly IRunnerProcessLauncher _launcher;

        public QqMusicInjectedMggDecryptService()
            : this(null) { }

        public QqMusicInjectedMggDecryptService(IRunnerProcessLauncher? launcher)
        {
            _launcher = launcher ?? new DirectRunnerLauncher();
        }

        public async Task<QqMusicMggDecryptResult> DecryptAsync(
            string inputPath,
            string outputDirectory,
            IProgress<string>? progress = null,
            CancellationToken cancellationToken = default(CancellationToken)
        )
        {
            if (!File.Exists(inputPath))
            {
                return QqMusicMggDecryptResult.Failure("Input file does not exist: " + inputPath);
            }

            string? runnerPath = FindRunnerPath();
            if (runnerPath == null)
            {
                return QqMusicMggDecryptResult.Failure("QQMusicDecryptRunner.exe was not found.");
            }

            try
            {
                runnerPath = PrepareRunnerForLaunch(runnerPath);
            }
            catch (Exception exception)
            {
                return QqMusicMggDecryptResult.Failure(
                    "Failed to prepare QQ Music decrypt runner: " + exception.Message);
            }

            Directory.CreateDirectory(outputDirectory);
            return await DecryptOnceAsync(
                runnerPath,
                inputPath,
                outputDirectory,
                progress,
                cancellationToken);
        }

        private async Task<QqMusicMggDecryptResult> DecryptOnceAsync(
            string runnerPath,
            string inputPath,
            string outputDirectory,
            IProgress<string>? progress,
            CancellationToken cancellationToken
        )
        {
            string statusFilePath = CreateStatusFilePath();
            string requestFilePath = CreateRequestFilePath();
            File.WriteAllLines(
                requestFilePath,
                new[]
                {
                    inputPath,
                    outputDirectory,
                    "--status-file",
                    statusFilePath
                },
                Encoding.UTF8);

            string arguments = ProcessCompat.BuildArguments("--request-file", requestFilePath);
            RunnerLaunchResult? launchResult = null;

            try
            {
                launchResult = _launcher.Launch(runnerPath, arguments);

                if (!launchResult.Started)
                {
                    return QqMusicMggDecryptResult.Failure(
                        (launchResult.StartupError ?? "Unable to start QQMusicDecryptRunner.exe.")
                        + " (launch_mode=" + (launchResult.LaunchMode ?? "unknown") + ")");
                }

                progress?.Report("launch_mode=" + launchResult.LaunchMode);

                QqMusicMggDecryptResult? successResult = null;
                string? errorMessage = null;
                StringBuilder stderrBuilder = new StringBuilder();

                List<Task> outputTasks = new List<Task>();
                if (launchResult.StandardOutput != null)
                {
                    outputTasks.Add(Task.Run(
                        async () =>
                        {
                            while (true)
                            {
                                string? line = await launchResult.StandardOutput.ReadLineAsync();
                                if (line == null)
                                {
                                    break;
                                }

                                QqMusicMggDecryptResult? parsed = ParseLine(line, progress);
                                if (parsed == null)
                                {
                                    continue;
                                }

                                if (parsed.IsSuccess)
                                {
                                    successResult = parsed;
                                }
                                else if (string.IsNullOrWhiteSpace(errorMessage))
                                {
                                    errorMessage = parsed.ErrorMessage;
                                }
                            }
                        },
                        cancellationToken
                    ));
                }

                if (launchResult.StandardError != null)
                {
                    outputTasks.Add(Task.Run(
                        async () =>
                        {
                            while (true)
                            {
                                string? line = await launchResult.StandardError.ReadLineAsync();
                                if (line == null)
                                {
                                    break;
                                }

                                if (stderrBuilder.Length > 0)
                                {
                                    stderrBuilder.AppendLine();
                                }

                                stderrBuilder.Append(line);

                                if (line.StartsWith("ERROR|", StringComparison.Ordinal)
                                    && string.IsNullOrWhiteSpace(errorMessage))
                                {
                                    errorMessage = line.Substring("ERROR|".Length).Trim();
                                }
                            }
                        },
                        cancellationToken
                    ));
                }

                if (launchResult.Process != null)
                {
                    using (CancellationTokenSource timeoutCts =
                        CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        timeoutCts.CancelAfter(TimeSpan.FromSeconds(ProcessTimeoutSeconds));

                        try
                        {
                            await ProcessCompat.WaitForExitAsync(launchResult.Process, timeoutCts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            launchResult.KillProcess();

                            return QqMusicMggDecryptResult.Failure(
                                "Timed out waiting for QQ Music decrypt result."
                                + " (launch_mode=" + launchResult.LaunchMode + ")"
                            );
                        }
                    }
                }

                if (outputTasks.Count > 0)
                {
                    await Task.WhenAll(outputTasks);
                }

                if (launchResult.StandardOutput == null ||
                    (successResult == null && string.IsNullOrWhiteSpace(errorMessage)))
                {
                    string? statusErrorMessage;
                    QqMusicMggDecryptResult? statusSuccessResult =
                        ParseStatusFile(statusFilePath, progress, out statusErrorMessage);

                    if (successResult == null && statusSuccessResult != null)
                    {
                        successResult = statusSuccessResult;
                    }

                    if (string.IsNullOrWhiteSpace(errorMessage) &&
                        !string.IsNullOrWhiteSpace(statusErrorMessage))
                    {
                        errorMessage = statusErrorMessage;
                    }
                }

                if (successResult != null)
                {
                    string resolvedOutputPath = ResolveOutputPath(
                        successResult.OutputPath,
                        outputDirectory
                    );

                    return QqMusicMggDecryptResult.Success(
                        resolvedOutputPath,
                        successResult.DetectedFormat ?? string.Empty
                    );
                }

                if (!string.IsNullOrWhiteSpace(errorMessage))
                {
                    return QqMusicMggDecryptResult.Failure(
                        errorMessage!
                        + " (launch_mode=" + launchResult.LaunchMode
                        + "; status_log=" + statusFilePath + ")");
                }

                if (stderrBuilder.Length > 0)
                {
                    return QqMusicMggDecryptResult.Failure(
                        stderrBuilder.ToString().Trim()
                        + " (launch_mode=" + launchResult.LaunchMode
                        + "; status_log=" + statusFilePath + ")");
                }

                int exitCode = -1;
                try
                {
                    if (launchResult.Process != null)
                    {
                        exitCode = launchResult.Process.ExitCode;
                    }
                }
                catch
                {
                }

                return QqMusicMggDecryptResult.Failure(
                    "QQMusicDecryptRunner.exe failed. Exit code: " + exitCode
                    + " (launch_mode=" + launchResult.LaunchMode
                    + "; status_log=" + statusFilePath + ")"
                );
            }
            catch (Exception ex)
            {
                return QqMusicMggDecryptResult.Failure(
                    "Failed to start QQ Music decrypt runner: " + ex.Message
                );
            }
            finally
            {
                TryDeleteFile(requestFilePath);
                launchResult?.Dispose();
            }
        }

        private static QqMusicMggDecryptResult? ParseStatusFile(
            string statusFilePath,
            IProgress<string>? progress,
            out string? errorMessage)
        {
            errorMessage = null;
            QqMusicMggDecryptResult? successResult = null;

            if (!File.Exists(statusFilePath))
            {
                return null;
            }

            try
            {
                foreach (string line in File.ReadLines(statusFilePath, Encoding.UTF8))
                {
                    QqMusicMggDecryptResult? parsed = ParseLine(line, progress);
                    if (parsed == null)
                    {
                        continue;
                    }

                    if (parsed.IsSuccess)
                    {
                        successResult = parsed;
                    }
                    else if (string.IsNullOrWhiteSpace(errorMessage))
                    {
                        errorMessage = parsed.ErrorMessage;
                    }
                }
            }
            catch (Exception exception)
            {
                errorMessage = "Failed to read QQ Music decrypt status file: " + exception.Message;
            }

            return successResult;
        }

        private static QqMusicMggDecryptResult? ParseLine(string line, IProgress<string>? progress)
        {
            if (line.StartsWith("LOG|", StringComparison.Ordinal))
            {
                progress?.Report(line.Substring("LOG|".Length));
                return null;
            }

            if (line.StartsWith("ERROR|", StringComparison.Ordinal))
            {
                return QqMusicMggDecryptResult.Failure(line.Substring("ERROR|".Length).Trim());
            }

            if (line.StartsWith("RESULT|", StringComparison.Ordinal))
            {
                string[] parts = line.Split('|');
                if (parts.Length >= 3)
                {
                    return QqMusicMggDecryptResult.Success(parts[1].Trim(), parts[2].Trim());
                }

                return QqMusicMggDecryptResult.Failure("QQ Music returned an invalid decrypt result.");
            }

            return null;
        }

        private static string ResolveOutputPath(string? outputPath, string outputDirectory)
        {
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                string trimmedPath = outputPath!.Trim();
                if (File.Exists(trimmedPath))
                {
                    return trimmedPath;
                }

                string baseName = Path.GetFileNameWithoutExtension(trimmedPath);
                if (!string.IsNullOrWhiteSpace(baseName) && Directory.Exists(outputDirectory))
                {
                    string[] candidates = Directory.GetFiles(outputDirectory, baseName + ".*");
                    if (candidates.Length > 0)
                    {
                        return candidates[0];
                    }
                }
            }

            if (Directory.Exists(outputDirectory))
            {
                string[] files = Directory.GetFiles(outputDirectory);
                if (files.Length == 1)
                {
                    return files[0];
                }
            }

            return outputPath != null ? outputPath.Trim() : outputDirectory;
        }

        private static string CreateStatusFilePath()
        {
            string logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AudioConvert",
                "Logs",
                "QQMusicDecryptRunner");

            Directory.CreateDirectory(logDirectory);
            return Path.Combine(
                logDirectory,
                "status-" + Guid.NewGuid().ToString("N") + ".log");
        }

        private static string CreateRequestFilePath()
        {
            string requestDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AudioConvert",
                "Requests",
                "QQMusicDecryptRunner");

            Directory.CreateDirectory(requestDirectory);
            return Path.Combine(
                requestDirectory,
                "request-" + Guid.NewGuid().ToString("N") + ".args");
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static string? FindRunnerPath()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDirectory, "QQMusicDecryptRunner.exe"),
                Path.Combine(baseDirectory, "Tools", "QQMusicDecryptRunner.exe"),
                Path.Combine(baseDirectory, "MP3AudioConverter.QQMusicDecryptRunner.exe"),
                Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "QQMusicDecryptRunner", "bin", "Debug", "net472", "QQMusicDecryptRunner.exe")),
                Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "QQMusicDecryptRunner", "bin", "Release", "net472", "QQMusicDecryptRunner.exe"))
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        private static string PrepareRunnerForLaunch(string runnerPath)
        {
            string? sourceDirectory = Path.GetDirectoryName(runnerPath);
            if (string.IsNullOrWhiteSpace(sourceDirectory))
            {
                return runnerPath;
            }

            string stagingDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AudioConvert",
                "Runners",
                "QQMusicDecryptRunner");
            Directory.CreateDirectory(stagingDirectory);

            string[] fileNames =
            {
                "QQMusicDecryptRunner.exe",
                "QQMusicDecryptRunner.exe.config",
                "QQMusicDecryptHook.dll",
                "EasyHook.dll",
                "EasyHook32.dll",
                "EasyHook32Svc.exe",
                "EasyHook64.dll",
                "EasyHook64Svc.exe",
                "EasyLoad32.dll",
                "EasyLoad64.dll"
            };

            foreach (string fileName in fileNames)
            {
                string sourcePath = Path.Combine(sourceDirectory, fileName);
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                string targetPath = Path.Combine(stagingDirectory, fileName);
                CopyRunnerFile(sourcePath, targetPath);
            }

            string stagedRunnerPath = Path.Combine(stagingDirectory, "QQMusicDecryptRunner.exe");
            string stagedHookPath = Path.Combine(stagingDirectory, "QQMusicDecryptHook.dll");
            if (!File.Exists(stagedRunnerPath))
            {
                throw new FileNotFoundException("Staged QQMusicDecryptRunner.exe was not created.", stagedRunnerPath);
            }

            if (!File.Exists(stagedHookPath))
            {
                throw new FileNotFoundException("Staged QQMusicDecryptHook.dll was not created.", stagedHookPath);
            }

            return stagedRunnerPath;
        }

        private static void CopyRunnerFile(string sourcePath, string targetPath)
        {
            var sourceInfo = new FileInfo(sourcePath);
            File.Copy(sourcePath, targetPath, overwrite: true);
            File.SetLastWriteTimeUtc(targetPath, sourceInfo.LastWriteTimeUtc);
        }
    }
}

