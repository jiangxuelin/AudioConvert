using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace AudioConvert.Services
{
    public interface IAudioConverterService
    {
        Task<ConvertResult> ConvertToMp3Async(string inputPath, IProgress<string>? progress = null);

        Task<ConvertResult> ConvertAsync(
            string inputPath,
            AudioOutputFormat outputFormat,
            IProgress<string>? progress = null);

        Task<ConvertResult> CompressAsync(
            string inputPath,
            string audioBitrate,
            IProgress<string>? progress = null);

        Task<ConvertResult> MergeAsync(
            IReadOnlyList<string> inputPaths,
            AudioOutputFormat outputFormat,
            IProgress<string>? progress = null);

        Task<ConvertResult> TrimAsync(
            string inputPath,
            TimeSpan startTime,
            TimeSpan endTime,
            IProgress<string>? progress = null);
    }

    public sealed class AudioConverterService : IAudioConverterService
    {
        private const string Mp3Extension = ".mp3";
        private const string WavExtension = ".wav";
        private const string FlacExtension = ".flac";
        private const string OggExtension = ".ogg";
        private const string M4aExtension = ".m4a";
        private const string AacExtension = ".aac";
        private const string MggExtension = ".mgg";
        private const string MflacExtension = ".mflac";
        private const string NcmExtension = ".ncm";
        private const string KggExtension = ".kgg";
        private const string KgmaExtension = ".kgma";
        private const string KwmExtension = ".kwm";
        private const string SupportedExtensionsDescription = ".mp3, .wav, .flac, .ogg, .m4a, .aac, .mgg, .mflac, .ncm, .kgg, .kgma, .kwm";
        private const string PreparingMessage = "正在准备音频...";
        private const string DecodingNcmMessage = "正在解码 NCM...";
        private const string TranscodingMessage = "正在处理音频...";

        private static readonly TimeSpan[] MggWholeFlowRetryDelays =
        {
            TimeSpan.FromMilliseconds(2000),
            TimeSpan.FromMilliseconds(5000)
        };

        private static readonly ISet<string> SupportedInputExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Mp3Extension,
            WavExtension,
            FlacExtension,
            OggExtension,
            M4aExtension,
            AacExtension,
            MggExtension,
            MflacExtension,
            NcmExtension,
            KggExtension,
            KgmaExtension,
            KwmExtension
        };

        private static readonly ISet<string> SupportedNcmOutputExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Mp3Extension,
            FlacExtension
        };

        private readonly IFfmpegConverter _ffmpegConverter;
        private readonly INcmDecoder _ncmDecoder;
        private readonly IQqMusicMggDecryptService _mggDecryptService;
        private readonly IKggDecryptService _kggDecryptService;
        private readonly IKwmDecryptService _kwmDecryptService;
        private readonly Func<string> _outputDirectoryProvider;
        private readonly Func<string> _temporaryDirectoryFactory;

        public AudioConverterService()
            : this(
                new FfmpegConverter(),
                new NcmDecoder(),
                new QqMusicInjectedMggDecryptService(CreateRunnerLauncher()),
                new KggDecryptService(),
                new KwmDecryptService(),
                () => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                CreateTemporaryDirectory)
        {
        }

        public AudioConverterService(
            IFfmpegConverter ffmpegConverter,
            INcmDecoder ncmDecoder,
            IQqMusicMggDecryptService mggDecryptService,
            IKggDecryptService kggDecryptService,
            IKwmDecryptService kwmDecryptService,
            Func<string> outputDirectoryProvider,
            Func<string> temporaryDirectoryFactory)
        {
            _ffmpegConverter = ffmpegConverter ?? throw new ArgumentNullException(nameof(ffmpegConverter));
            _ncmDecoder = ncmDecoder ?? throw new ArgumentNullException(nameof(ncmDecoder));
            _mggDecryptService = mggDecryptService ?? throw new ArgumentNullException(nameof(mggDecryptService));
            _kggDecryptService = kggDecryptService ?? throw new ArgumentNullException(nameof(kggDecryptService));
            _kwmDecryptService = kwmDecryptService ?? throw new ArgumentNullException(nameof(kwmDecryptService));
            _outputDirectoryProvider = outputDirectoryProvider ?? throw new ArgumentNullException(nameof(outputDirectoryProvider));
            _temporaryDirectoryFactory = temporaryDirectoryFactory ?? throw new ArgumentNullException(nameof(temporaryDirectoryFactory));
        }

        public Task<ConvertResult> ConvertToMp3Async(string inputPath, IProgress<string>? progress = null) =>
            ConvertAsync(inputPath, AudioOutputFormat.Mp3, progress);

        public async Task<ConvertResult> ConvertAsync(
            string inputPath,
            AudioOutputFormat outputFormat,
            IProgress<string>? progress = null)
        {
            ConvertResult? validation = ValidateInput(inputPath);
            if (validation is not null)
            {
                return validation;
            }

            string outputPath = BuildOutputPath(inputPath, outputFormat.GetExtension());
            progress?.Report(PreparingMessage);

            try
            {
                return await WithPreparedInputAsync(
                    inputPath,
                    progress,
                    async preparedInputPath =>
                    {
                        if (string.Equals(GetNormalizedExtension(preparedInputPath), outputFormat.GetExtension(), StringComparison.OrdinalIgnoreCase))
                        {
                            CopyToOutput(preparedInputPath, outputPath);
                            return ConvertResult.Success(outputPath);
                        }

                        if (!_ffmpegConverter.IsAvailable)
                        {
                            return CreateFfmpegNotFoundResult();
                        }

                        progress?.Report(TranscodingMessage);
                        return await _ffmpegConverter.ConvertAudioAsync(preparedInputPath, outputPath, outputFormat);
                    });
            }
            catch (Exception exception)
            {
                return ConvertResult.Failure("Audio conversion failed: " + exception.Message);
            }
        }

        public async Task<ConvertResult> CompressAsync(
            string inputPath,
            string audioBitrate,
            IProgress<string>? progress = null)
        {
            ConvertResult? validation = ValidateInput(inputPath);
            if (validation is not null)
            {
                return validation;
            }

            if (string.IsNullOrWhiteSpace(audioBitrate))
            {
                return ConvertResult.Failure("Audio bitrate cannot be empty.");
            }

            string outputPath = BuildOutputPath(inputPath, "_compressed", Mp3Extension);
            progress?.Report(PreparingMessage);

            try
            {
                return await WithPreparedInputAsync(
                    inputPath,
                    progress,
                    async preparedInputPath =>
                    {
                        if (!_ffmpegConverter.IsAvailable)
                        {
                            return CreateFfmpegNotFoundResult();
                        }

                        progress?.Report("正在压缩音频...");
                        return await _ffmpegConverter.CompressToMp3Async(preparedInputPath, outputPath, audioBitrate);
                    });
            }
            catch (Exception exception)
            {
                return ConvertResult.Failure("Audio compression failed: " + exception.Message);
            }
        }

        public async Task<ConvertResult> MergeAsync(
            IReadOnlyList<string> inputPaths,
            AudioOutputFormat outputFormat,
            IProgress<string>? progress = null)
        {
            if (inputPaths.Count < 2)
            {
                return ConvertResult.Failure("请至少选择两个音频文件。");
            }

            foreach (string inputPath in inputPaths)
            {
                ConvertResult? validation = ValidateInput(inputPath);
                if (validation is not null)
                {
                    return validation;
                }
            }

            if (!_ffmpegConverter.IsAvailable)
            {
                return CreateFfmpegNotFoundResult();
            }

            string outputPath = BuildNamedOutputPath("merged_audio", outputFormat.GetExtension());

            return await ConvertWithTemporaryDirectoryAsync(async temporaryDirectory =>
            {
                var preparedInputs = new List<string>();
                for (int index = 0; index < inputPaths.Count; index++)
                {
                    string preparedPath = await PrepareInputForFfmpegAsync(
                        inputPaths[index],
                        temporaryDirectory,
                        progress,
                        $"merge_{index:000}");

                    preparedInputs.Add(preparedPath);
                }

                progress?.Report("正在合并音频...");
                return await _ffmpegConverter.MergeAudioAsync(preparedInputs, outputPath, outputFormat, progress);
            });
        }

        public async Task<ConvertResult> TrimAsync(
            string inputPath,
            TimeSpan startTime,
            TimeSpan endTime,
            IProgress<string>? progress = null)
        {
            ConvertResult? validation = ValidateInput(inputPath);
            if (validation is not null)
            {
                return validation;
            }

            if (endTime <= startTime)
            {
                return ConvertResult.Failure("结束时间必须大于开始时间。");
            }

            string outputPath = BuildOutputPath(inputPath, "_clip", Mp3Extension);
            progress?.Report(PreparingMessage);

            try
            {
                return await WithPreparedInputAsync(
                    inputPath,
                    progress,
                    async preparedInputPath =>
                    {
                        if (!_ffmpegConverter.IsAvailable)
                        {
                            return CreateFfmpegNotFoundResult();
                        }

                        progress?.Report("正在切割音频...");
                        return await _ffmpegConverter.TrimToMp3Async(preparedInputPath, outputPath, startTime, endTime);
                    });
            }
            catch (Exception exception)
            {
                return ConvertResult.Failure("Audio trim failed: " + exception.Message);
            }
        }

        private async Task<ConvertResult> WithPreparedInputAsync(
            string inputPath,
            IProgress<string>? progress,
            Func<string, Task<ConvertResult>> operationAsync)
        {
            string extension = GetNormalizedExtension(inputPath);
            if (!RequiresPreparation(extension))
            {
                return await operationAsync(inputPath);
            }

            return await ConvertWithTemporaryDirectoryAsync(async temporaryDirectory =>
            {
                string preparedInputPath = await PrepareInputForFfmpegAsync(
                    inputPath,
                    temporaryDirectory,
                    progress,
                    Path.GetFileNameWithoutExtension(inputPath));

                return await operationAsync(preparedInputPath);
            });
        }

        private async Task<string> PrepareInputForFfmpegAsync(
            string inputPath,
            string temporaryDirectory,
            IProgress<string>? progress,
            string outputBaseName)
        {
            string inputExtension = GetNormalizedExtension(inputPath);
            if (!RequiresPreparation(inputExtension))
            {
                return inputPath;
            }

            switch (inputExtension)
            {
                case MggExtension:
                case MflacExtension:
                    return await DecryptMggAsync(inputPath, temporaryDirectory, progress);
                case KggExtension:
                case KgmaExtension:
                    return await DecryptKggAsync(inputPath, temporaryDirectory, progress);
                case KwmExtension:
                    return await DecryptKwmAsync(inputPath, temporaryDirectory, progress);
                case NcmExtension:
                    return await DecodeNcmAsync(inputPath, temporaryDirectory, outputBaseName, progress);
                default:
                    return inputPath;
            }
        }

        private async Task<string> DecryptMggAsync(
            string inputPath,
            string temporaryDirectory,
            IProgress<string>? progress)
        {
            for (int attempt = 0; attempt <= MggWholeFlowRetryDelays.Length; attempt++)
            {
                QqMusicMggDecryptResult decryptResult = await _mggDecryptService.DecryptAsync(
                    inputPath,
                    temporaryDirectory,
                    progress);

                if (decryptResult.IsSuccess && !string.IsNullOrWhiteSpace(decryptResult.OutputPath))
                {
                    return ResolveDecodedFilePath(decryptResult.OutputPath!);
                }

                string errorMessage = decryptResult.ErrorMessage ?? "MGG decrypt failed.";
                bool canRetry = attempt < MggWholeFlowRetryDelays.Length && ShouldRetryWholeMggConversion(errorMessage);
                if (!canRetry)
                {
                    throw new InvalidOperationException(errorMessage);
                }

                progress?.Report($"QQ Music returned incomplete audio data. Retrying full decrypt ({attempt + 2}/{MggWholeFlowRetryDelays.Length + 1})...");
                await Task.Delay(MggWholeFlowRetryDelays[attempt]);
            }

            throw new InvalidOperationException("QQ Music conversion failed.");
        }

        private async Task<string> DecryptKggAsync(
            string inputPath,
            string temporaryDirectory,
            IProgress<string>? progress)
        {
            KggDecryptResult decryptResult = await _kggDecryptService.DecryptAsync(inputPath, temporaryDirectory, progress);
            if (!decryptResult.IsSuccess || string.IsNullOrWhiteSpace(decryptResult.OutputPath))
            {
                throw new InvalidOperationException(decryptResult.ErrorMessage ?? "KGG decrypt failed.");
            }

            return ResolveDecodedFilePath(decryptResult.OutputPath!);
        }

        private async Task<string> DecryptKwmAsync(
            string inputPath,
            string temporaryDirectory,
            IProgress<string>? progress)
        {
            KwmDecryptResult decryptResult = await _kwmDecryptService.DecryptAsync(inputPath, temporaryDirectory, progress);
            if (!decryptResult.IsSuccess || string.IsNullOrWhiteSpace(decryptResult.OutputPath))
            {
                throw new InvalidOperationException(decryptResult.ErrorMessage ?? "KWM decrypt failed.");
            }

            return ResolveDecodedFilePath(decryptResult.OutputPath!);
        }

        private async Task<string> DecodeNcmAsync(
            string inputPath,
            string temporaryDirectory,
            string outputBaseName,
            IProgress<string>? progress)
        {
            progress?.Report(DecodingNcmMessage);
            NcmDecodeResult decodeResult = await _ncmDecoder.DecodeAsync(inputPath, temporaryDirectory, outputBaseName);
            if (!decodeResult.IsSuccess || string.IsNullOrWhiteSpace(decodeResult.OutputPath))
            {
                throw new InvalidOperationException(decodeResult.ErrorMessage ?? "NCM decode failed.");
            }

            string decodedFilePath = ResolveDecodedFilePath(decodeResult.OutputPath!);
            string? decodedExtension = DetectAudioExtension(decodedFilePath);
            if (decodedExtension is null)
            {
                throw new InvalidOperationException("NCM decode result is invalid audio data.");
            }

            if (!SupportedNcmOutputExtensions.Contains(decodedExtension))
            {
                throw new InvalidOperationException("NCM decode produced an unsupported format: " + decodedExtension);
            }

            return decodedFilePath;
        }

        private async Task<ConvertResult> ConvertWithTemporaryDirectoryAsync(Func<string, Task<ConvertResult>> convertAsync)
        {
            string temporaryDirectory = _temporaryDirectoryFactory();

            try
            {
                return await convertAsync(temporaryDirectory);
            }
            finally
            {
                TryDeleteDirectory(temporaryDirectory);
            }
        }

        private ConvertResult? ValidateInput(string inputPath)
        {
            if (string.IsNullOrWhiteSpace(inputPath))
            {
                return ConvertResult.Failure("Input file path cannot be empty.");
            }

            if (!File.Exists(inputPath))
            {
                return ConvertResult.Failure("Input file does not exist: " + inputPath);
            }

            string inputExtension = GetNormalizedExtension(inputPath);
            if (!SupportedInputExtensions.Contains(inputExtension))
            {
                return ConvertResult.Failure("Only these file types are supported: " + SupportedExtensionsDescription + ".");
            }

            return null;
        }

        private string BuildOutputPath(string inputPath, string extension) =>
            BuildOutputPath(inputPath, string.Empty, extension);

        private string BuildOutputPath(string inputPath, string suffix, string extension)
        {
            string outputDirectory = _outputDirectoryProvider();
            string outputFileName = Path.GetFileNameWithoutExtension(inputPath) + suffix + extension;
            string outputPath = Path.Combine(outputDirectory, outputFileName);
            if (string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            {
                string safeSuffix = string.IsNullOrEmpty(suffix) ? "_converted" : suffix + "_converted";
                outputFileName = Path.GetFileNameWithoutExtension(inputPath) + safeSuffix + extension;
                outputPath = Path.Combine(outputDirectory, outputFileName);
            }

            return outputPath;
        }

        private string BuildNamedOutputPath(string fileNameWithoutExtension, string extension)
        {
            string outputDirectory = _outputDirectoryProvider();
            return Path.Combine(outputDirectory, fileNameWithoutExtension + extension);
        }

        private ConvertResult CreateFfmpegNotFoundResult()
        {
            return ConvertResult.Failure("ffmpeg.exe was not found. Expected under: " + _ffmpegConverter.FfmpegDirectory);
        }

        private static void CopyToOutput(string sourceFilePath, string outputPath)
        {
            if (!File.Exists(sourceFilePath))
            {
                throw new FileNotFoundException("Decoded output file was not found.", sourceFilePath);
            }

            string? outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new ArgumentException("Output directory is invalid: " + outputPath, nameof(outputPath));
            }

            Directory.CreateDirectory(outputDirectory);
            File.Copy(sourceFilePath, outputPath, overwrite: true);
        }

        private static bool RequiresPreparation(string extension) =>
            string.Equals(extension, MggExtension, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, MflacExtension, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, NcmExtension, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, KggExtension, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, KgmaExtension, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, KwmExtension, StringComparison.OrdinalIgnoreCase);

        private static string GetNormalizedExtension(string filePath)
        {
            return Path.GetExtension(filePath).ToLowerInvariant();
        }

        private static string ResolveDecodedFilePath(string decodedFilePath)
        {
            if (File.Exists(decodedFilePath))
            {
                return decodedFilePath;
            }

            string? directory = Path.GetDirectoryName(decodedFilePath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return decodedFilePath;
            }

            string baseName = Path.GetFileNameWithoutExtension(decodedFilePath);
            string[] candidates = Directory.GetFiles(directory, baseName + ".*");
            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return decodedFilePath;
        }

        private static IRunnerProcessLauncher CreateRunnerLauncher()
        {
            if (PackagedRunnerLauncher.IsCurrentProcessPackaged())
            {
                return new PackagedRunnerLauncher();
            }

            return UnelevatedRunnerLauncher.IsProcessElevated()
                ? new UnelevatedRunnerLauncher()
                : new DirectRunnerLauncher();
        }

        private static string CreateTemporaryDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "AudioConvert", "decoded", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static bool ShouldRetryWholeMggConversion(string? errorMessage)
        {
            if (string.IsNullOrWhiteSpace(errorMessage))
            {
                return false;
            }

            string value = errorMessage!;
            return value.IndexOf("invalid audio data", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("may still be initializing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("hook injection timed out", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("Timed out while injecting QQ Music hook", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("Timed out while waiting for QQ Music decrypt result", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("FFmpeg did not create", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("Invalid data found when processing input", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("cannot find sync word", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   value.IndexOf("low score", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string? DetectAudioExtension(string filePath)
        {
            try
            {
                using var stream = File.OpenRead(filePath);
                byte[] header = new byte[4];
                int bytesRead = stream.Read(header, 0, header.Length);
                if (bytesRead <= 0)
                {
                    return null;
                }

                ReadOnlySpan<byte> data = header.AsSpan(0, bytesRead);
                if (StartsWith(data, 0x4F, 0x67, 0x67, 0x53))
                {
                    return OggExtension;
                }

                if (StartsWith(data, 0x66, 0x4C, 0x61, 0x43))
                {
                    return FlacExtension;
                }

                if (StartsWith(data, 0x52, 0x49, 0x46, 0x46))
                {
                    return WavExtension;
                }

                if (StartsWith(data, 0x49, 0x44, 0x33) ||
                    StartsWith(data, 0xFF, 0xFB) ||
                    StartsWith(data, 0xFF, 0xF3) ||
                    StartsWith(data, 0xFF, 0xF2))
                {
                    return Mp3Extension;
                }
            }
            catch (Exception exception)
            {
                Debug.WriteLine("Failed to inspect decoded audio header for '" + filePath + "': " + exception);
            }

            return null;
        }

        private static bool StartsWith(ReadOnlySpan<byte> data, params byte[] header)
        {
            if (data.Length < header.Length)
            {
                return false;
            }

            for (int index = 0; index < header.Length; index++)
            {
                if (data[index] != header[index])
                {
                    return false;
                }
            }

            return true;
        }

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
            catch (Exception exception)
            {
                Debug.WriteLine("Failed to delete temporary directory '" + path + "': " + exception);
            }
        }
    }
}
