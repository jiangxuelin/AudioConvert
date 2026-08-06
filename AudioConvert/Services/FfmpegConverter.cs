using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace AudioConvert.Services
{
    public interface IFfmpegConverter
    {
        bool IsAvailable { get; }

        string FfmpegDirectory { get; }

        Task<ConvertResult> ConvertToMp3Async(string inputPath, string outputPath);

        Task<ConvertResult> ConvertAudioAsync(
            string inputPath,
            string outputPath,
            AudioOutputFormat outputFormat,
            string? audioBitrate = null);

        Task<ConvertResult> CompressToMp3Async(string inputPath, string outputPath, string audioBitrate);

        Task<ConvertResult> TrimToMp3Async(string inputPath, string outputPath, TimeSpan startTime, TimeSpan endTime);

        Task<ConvertResult> MergeAudioAsync(
            IReadOnlyList<string> inputPaths,
            string outputPath,
            AudioOutputFormat outputFormat,
            IProgress<string>? progress = null);
    }

    public sealed class FfmpegConverter : IFfmpegConverter
    {
        private const string ToolDirectoryName = "Tools";
        private const string ExecutableName = "ffmpeg.exe";
        private const string Mp3AudioCodec = "libmp3lame";
        private const string DefaultAudioBitrate = "192k";

        private readonly string _ffmpegPath;

        public FfmpegConverter()
            : this(AppDomain.CurrentDomain.BaseDirectory)
        {
        }

        internal FfmpegConverter(string applicationBaseDirectory)
        {
            if (string.IsNullOrWhiteSpace(applicationBaseDirectory))
            {
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(applicationBaseDirectory));
            }

            _ffmpegPath = Path.Combine(applicationBaseDirectory, ToolDirectoryName, ExecutableName);
        }

        public bool IsAvailable => File.Exists(_ffmpegPath);

        public string FfmpegDirectory => Path.GetDirectoryName(_ffmpegPath)!;

        public Task<ConvertResult> ConvertToMp3Async(string inputPath, string outputPath) =>
            ConvertAudioAsync(inputPath, outputPath, AudioOutputFormat.Mp3, DefaultAudioBitrate);

        public Task<ConvertResult> CompressToMp3Async(string inputPath, string outputPath, string audioBitrate) =>
            ConvertAudioAsync(inputPath, outputPath, AudioOutputFormat.Mp3, audioBitrate);

        public async Task<ConvertResult> ConvertAudioAsync(
            string inputPath,
            string outputPath,
            AudioOutputFormat outputFormat,
            string? audioBitrate = null)
        {
            if (!IsAvailable)
            {
                return ConvertResult.Failure("ffmpeg.exe was not found: " + FfmpegDirectory);
            }

            try
            {
                ValidateInputOutput(inputPath, outputPath);
                string? outputDirectory = Path.GetDirectoryName(outputPath);
                Directory.CreateDirectory(outputDirectory!);

                if (NeedsPathStaging(inputPath) || NeedsPathStaging(outputPath))
                {
                    return await ConvertWithStagingAsync(
                        inputPath,
                        outputPath,
                        outputFormat.GetExtension(),
                        (stagedInput, stagedOutput) => BuildConvertArguments(stagedInput, stagedOutput, outputFormat, audioBitrate));
                }

                return await RunFfmpegAsync(BuildConvertArguments(inputPath, outputPath, outputFormat, audioBitrate), outputPath);
            }
            catch (Exception exception)
            {
                return ConvertResult.Failure("Failed to start FFmpeg: " + exception.Message);
            }
        }

        public async Task<ConvertResult> TrimToMp3Async(
            string inputPath,
            string outputPath,
            TimeSpan startTime,
            TimeSpan endTime)
        {
            if (!IsAvailable)
            {
                return ConvertResult.Failure("ffmpeg.exe was not found: " + FfmpegDirectory);
            }

            if (endTime <= startTime)
            {
                return ConvertResult.Failure("End time must be greater than start time.");
            }

            try
            {
                ValidateInputOutput(inputPath, outputPath);
                string? outputDirectory = Path.GetDirectoryName(outputPath);
                Directory.CreateDirectory(outputDirectory!);

                if (NeedsPathStaging(inputPath) || NeedsPathStaging(outputPath))
                {
                    return await ConvertWithStagingAsync(
                        inputPath,
                        outputPath,
                        AudioOutputFormat.Mp3.GetExtension(),
                        (stagedInput, stagedOutput) => BuildTrimArguments(stagedInput, stagedOutput, startTime, endTime));
                }

                return await RunFfmpegAsync(BuildTrimArguments(inputPath, outputPath, startTime, endTime), outputPath);
            }
            catch (Exception exception)
            {
                return ConvertResult.Failure("Failed to trim audio: " + exception.Message);
            }
        }

        public async Task<ConvertResult> MergeAudioAsync(
            IReadOnlyList<string> inputPaths,
            string outputPath,
            AudioOutputFormat outputFormat,
            IProgress<string>? progress = null)
        {
            if (!IsAvailable)
            {
                return ConvertResult.Failure("ffmpeg.exe was not found: " + FfmpegDirectory);
            }

            if (inputPaths.Count < 2)
            {
                return ConvertResult.Failure("Please select at least two audio files to merge.");
            }

            string stagingDirectory = Path.Combine(
                Path.GetTempPath(),
                "AudioConvert",
                "merge",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(stagingDirectory);

            try
            {
                var normalizedFiles = new List<string>();
                for (int index = 0; index < inputPaths.Count; index++)
                {
                    string inputPath = inputPaths[index];
                    if (!File.Exists(inputPath))
                    {
                        return ConvertResult.Failure("Input file does not exist: " + inputPath);
                    }

                    progress?.Report($"正在准备合并文件 {index + 1}/{inputPaths.Count}...");
                    string stagedInputPath = Path.Combine(stagingDirectory, $"input_{index:000}{Path.GetExtension(inputPath)}");
                    string normalizedPath = Path.Combine(stagingDirectory, $"part_{index:000}.wav");
                    File.Copy(inputPath, stagedInputPath, overwrite: true);

                    ConvertResult normalizeResult = await RunFfmpegAsync(
                        ProcessCompat.BuildArguments(
                            "-y",
                            "-i",
                            stagedInputPath,
                            "-vn",
                            "-ac",
                            "2",
                            "-ar",
                            "44100",
                            "-codec:a",
                            "pcm_s16le",
                            normalizedPath),
                        normalizedPath);

                    if (!normalizeResult.IsSuccess)
                    {
                        return normalizeResult;
                    }

                    normalizedFiles.Add(normalizedPath);
                }

                string listPath = Path.Combine(stagingDirectory, "concat.txt");
                using (var writer = new StreamWriter(listPath, append: false))
                {
                    foreach (string normalizedFile in normalizedFiles)
                    {
                        writer.WriteLine("file '" + normalizedFile.Replace("\\", "/").Replace("'", "'\\''") + "'");
                    }
                }

                string stagedOutputPath = Path.Combine(stagingDirectory, "merged" + outputFormat.GetExtension());
                progress?.Report("正在合并音频...");
                ConvertResult mergeResult = await RunFfmpegAsync(
                    BuildMergeArguments(listPath, stagedOutputPath, outputFormat),
                    stagedOutputPath);

                if (!mergeResult.IsSuccess)
                {
                    return mergeResult;
                }

                string? outputDirectory = Path.GetDirectoryName(outputPath);
                if (string.IsNullOrWhiteSpace(outputDirectory))
                {
                    return ConvertResult.Failure("Invalid output path: " + outputPath);
                }

                Directory.CreateDirectory(outputDirectory);
                File.Copy(stagedOutputPath, outputPath, overwrite: true);
                return ConvertResult.Success(outputPath);
            }
            catch (Exception exception)
            {
                return ConvertResult.Failure("Failed to merge audio: " + exception.Message);
            }
            finally
            {
                TryDeleteDirectory(stagingDirectory);
            }
        }

        private static string BuildConvertArguments(
            string inputPath,
            string outputPath,
            AudioOutputFormat outputFormat,
            string? audioBitrate)
        {
            var arguments = new List<string>
            {
                "-y",
                "-i",
                inputPath,
                "-vn"
            };

            AddOutputCodecArguments(arguments, outputFormat, audioBitrate);
            arguments.Add("-map_metadata");
            arguments.Add("0");
            arguments.Add(outputPath);

            return ProcessCompat.BuildArguments(arguments.ToArray());
        }

        private static string BuildTrimArguments(
            string inputPath,
            string outputPath,
            TimeSpan startTime,
            TimeSpan endTime)
        {
            TimeSpan duration = endTime - startTime;
            return ProcessCompat.BuildArguments(
                "-y",
                "-ss",
                FormatTime(startTime),
                "-i",
                inputPath,
                "-t",
                FormatTime(duration),
                "-vn",
                "-codec:a",
                Mp3AudioCodec,
                "-b:a",
                DefaultAudioBitrate,
                "-map_metadata",
                "0",
                outputPath);
        }

        private static string BuildMergeArguments(
            string listPath,
            string outputPath,
            AudioOutputFormat outputFormat)
        {
            var arguments = new List<string>
            {
                "-y",
                "-f",
                "concat",
                "-safe",
                "0",
                "-i",
                listPath,
                "-vn"
            };

            AddOutputCodecArguments(arguments, outputFormat, DefaultAudioBitrate);
            arguments.Add(outputPath);

            return ProcessCompat.BuildArguments(arguments.ToArray());
        }

        private static void AddOutputCodecArguments(
            List<string> arguments,
            AudioOutputFormat outputFormat,
            string? audioBitrate)
        {
            switch (outputFormat)
            {
                case AudioOutputFormat.Wav:
                    arguments.Add("-codec:a");
                    arguments.Add("pcm_s16le");
                    break;
                case AudioOutputFormat.Flac:
                    arguments.Add("-codec:a");
                    arguments.Add("flac");
                    break;
                case AudioOutputFormat.Ogg:
                    arguments.Add("-codec:a");
                    arguments.Add("libvorbis");
                    arguments.Add("-q:a");
                    arguments.Add("5");
                    break;
                default:
                    arguments.Add("-codec:a");
                    arguments.Add(Mp3AudioCodec);
                    arguments.Add("-b:a");
                    arguments.Add(string.IsNullOrWhiteSpace(audioBitrate) ? DefaultAudioBitrate : audioBitrate!);
                    break;
            }
        }

        private async Task<ConvertResult> ConvertWithStagingAsync(
            string inputPath,
            string outputPath,
            string outputExtension,
            Func<string, string, string> argumentFactory)
        {
            string stagingDirectory = Path.Combine(
                Path.GetTempPath(),
                "AudioConvert",
                "ffmpeg",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(stagingDirectory);

            string stagedInputPath = Path.Combine(stagingDirectory, "input" + Path.GetExtension(inputPath));
            string stagedOutputPath = Path.Combine(stagingDirectory, "output" + outputExtension);

            try
            {
                File.Copy(inputPath, stagedInputPath, overwrite: true);

                ConvertResult stagedResult = await RunFfmpegAsync(argumentFactory(stagedInputPath, stagedOutputPath), stagedOutputPath);
                if (!stagedResult.IsSuccess)
                {
                    return stagedResult;
                }

                File.Copy(stagedOutputPath, outputPath, overwrite: true);
                return ConvertResult.Success(outputPath);
            }
            finally
            {
                TryDeleteDirectory(stagingDirectory);
            }
        }

        private async Task<ConvertResult> RunFfmpegAsync(string arguments, string outputPath)
        {
            using Process process = StartProcess(arguments);
            string standardError = await process.StandardError.ReadToEndAsync();
            await ProcessCompat.WaitForExitAsync(process);

            return BuildConvertResult(outputPath, process.ExitCode, standardError);
        }

        private Process StartProcess(string arguments)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _ffmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            return Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start ffmpeg.exe.");
        }

        private static void ValidateInputOutput(string inputPath, string outputPath)
        {
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(inputPath));
            }

            if (string.IsNullOrWhiteSpace(outputPath))
            {
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(outputPath));
            }

            if (!File.Exists(inputPath))
            {
                throw new FileNotFoundException("Input file does not exist.", inputPath);
            }

            string? outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new ArgumentException("Invalid output path: " + outputPath, nameof(outputPath));
            }
        }

        private static ConvertResult BuildConvertResult(string outputPath, int exitCode, string standardError)
        {
            if (exitCode == 0 && IsValidOutputFile(outputPath))
            {
                return ConvertResult.Success(outputPath);
            }

            string errorDetails = string.IsNullOrWhiteSpace(standardError)
                ? string.Empty
                : Environment.NewLine + standardError.Trim();

            return ConvertResult.Failure("FFmpeg did not create an output file. Exit code: " + exitCode + errorDetails);
        }

        private static bool IsValidOutputFile(string outputPath)
        {
            return File.Exists(outputPath) && new FileInfo(outputPath).Length > 0;
        }

        private static bool NeedsPathStaging(string path)
        {
            foreach (char character in path)
            {
                if (character > 0x7F || character == '"')
                {
                    return true;
                }
            }

            return false;
        }

        private static string FormatTime(TimeSpan value) =>
            value.ToString(@"hh\:mm\:ss\.fff");

        private static void TryDeleteDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return;
            }

            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
