using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AudioConvert.Services
{
    public interface IKwmDecryptService
    {
        Task<KwmDecryptResult> DecryptAsync(
            string inputPath,
            string outputDirectory,
            IProgress<string>? progress = null);
    }

    public sealed class KwmDecryptResult
    {
        public bool IsSuccess { get; private init; }

        public string? OutputPath { get; private init; }

        public string? DetectedFormat { get; private init; }

        public string? ErrorMessage { get; private init; }

        public static KwmDecryptResult Success(string outputPath, string detectedFormat) =>
            new() { IsSuccess = true, OutputPath = outputPath, DetectedFormat = detectedFormat };

        public static KwmDecryptResult Failure(string errorMessage) =>
            new() { IsSuccess = false, ErrorMessage = errorMessage };
    }

    public sealed class KwmDecryptService : IKwmDecryptService
    {
        private static readonly byte[] HeaderMagic = Encoding.ASCII.GetBytes("yeelion-kuwo-tme");
        private const int HeaderSize = 1024;
        private const int KeySize = 32;
        private const int MaxKeySearchBlocks = 468;
        private const int ReadBufferSize = 1024 * 1024;

        public Task<KwmDecryptResult> DecryptAsync(
            string inputPath,
            string outputDirectory,
            IProgress<string>? progress = null)
        {
            return Task.Run(() => DecryptCore(inputPath, outputDirectory, progress));
        }

        private KwmDecryptResult DecryptCore(
            string inputPath,
            string outputDirectory,
            IProgress<string>? progress)
        {
            if (!File.Exists(inputPath))
            {
                return KwmDecryptResult.Failure($"输入文件不存在：{inputPath}");
            }

            using FileStream inputStream = new(inputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (inputStream.Length <= HeaderSize)
            {
                return KwmDecryptResult.Failure("KWM 文件过小，无法读取音频数据。");
            }

            byte[] header = new byte[HeaderSize];
            if (!TryReadExact(inputStream, header, header.Length))
            {
                return KwmDecryptResult.Failure("KWM 文件头不完整。");
            }

            if (!header.AsSpan(0, HeaderMagic.Length).SequenceEqual(HeaderMagic))
            {
                return KwmDecryptResult.Failure("无效的 KWM 文件头。");
            }

            progress?.Report("正在解析 KWM 文件...");

            if (!TryExtractKey(inputStream, out byte[]? key, out string? errorMessage))
            {
                return KwmDecryptResult.Failure(errorMessage ?? "未能提取 KWM 解密密钥。");
            }

            inputStream.Position = HeaderSize;
            byte[] probe = new byte[16];
            int probeBytesRead = inputStream.Read(probe, 0, probe.Length);
            if (probeBytesRead <= 0)
            {
                return KwmDecryptResult.Failure("KWM 文件中未找到音频数据。");
            }

            DecryptBuffer(probe.AsSpan(0, probeBytesRead), key, 0);
            string detectedFormat = DetectOutputFormat(
                probe.AsSpan(0, probeBytesRead),
                GetHeaderFormatHint(header));

            Directory.CreateDirectory(outputDirectory);
            string outputPath = Path.Combine(
                outputDirectory,
                $"{Path.GetFileNameWithoutExtension(inputPath)}.{detectedFormat}");

            inputStream.Position = HeaderSize;
            progress?.Report("正在解密 KWM 音频数据...");

            using FileStream outputStream = File.Create(outputPath);
            byte[] buffer = new byte[ReadBufferSize];
            long offset = 0;
            int bytesRead;
            while ((bytesRead = inputStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                DecryptBuffer(buffer.AsSpan(0, bytesRead), key, offset);
                outputStream.Write(buffer, 0, bytesRead);
                offset += bytesRead;
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                return KwmDecryptResult.Failure($"解密后的输出文件未生成：{outputPath}");
            }

            return KwmDecryptResult.Success(outputPath, detectedFormat);
        }

        private static bool TryExtractKey(
            Stream inputStream,
            out byte[]? key,
            out string? errorMessage)
        {
            inputStream.Position = HeaderSize;

            byte[] candidate = new byte[KeySize];
            byte[] previous = new byte[KeySize];
            byte[] lastCandidate = new byte[KeySize];
            bool hasPrevious = false;

            for (int blockIndex = 0; blockIndex < MaxKeySearchBlocks; blockIndex++)
            {
                if (!TryReadExact(inputStream, candidate, candidate.Length))
                {
                    break;
                }

                Buffer.BlockCopy(candidate, 0, lastCandidate, 0, KeySize);

                if (hasPrevious && previous.AsSpan().SequenceEqual(candidate))
                {
                    key = candidate.ToArray();
                    errorMessage = null;
                    return true;
                }

                Buffer.BlockCopy(candidate, 0, previous, 0, KeySize);
                hasPrevious = true;
            }

            if (!hasPrevious)
            {
                key = null;
                errorMessage = "KWM 文件中没有足够的数据用于提取密钥。";
                return false;
            }

            key = RotateHalf(lastCandidate);
            errorMessage = null;
            return true;
        }

        private static byte[] RotateHalf(byte[] buffer)
        {
            byte[] rotated = new byte[buffer.Length];
            int half = buffer.Length / 2;

            Buffer.BlockCopy(buffer, half, rotated, 0, half);
            Buffer.BlockCopy(buffer, 0, rotated, half, half);

            return rotated;
        }

        private static void DecryptBuffer(Span<byte> buffer, ReadOnlySpan<byte> key, long offset)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                int keyIndex = (int)((offset + i) % key.Length);
                buffer[i] ^= key[keyIndex];
            }
        }

        private static string DetectOutputFormat(ReadOnlySpan<byte> probe, string? headerFormatHint)
        {
            if (probe.Length >= 4)
            {
                if (probe[0] == (byte)'f' && probe[1] == (byte)'L' && probe[2] == (byte)'a' && probe[3] == (byte)'C')
                {
                    return "flac";
                }

                if (probe[0] == (byte)'O' && probe[1] == (byte)'g' && probe[2] == (byte)'g' && probe[3] == (byte)'S')
                {
                    return "ogg";
                }

                if (probe[0] == (byte)'R' && probe[1] == (byte)'I' && probe[2] == (byte)'F' && probe[3] == (byte)'F')
                {
                    return "wav";
                }
            }

            if (probe.Length >= 3 &&
                probe[0] == (byte)'I' &&
                probe[1] == (byte)'D' &&
                probe[2] == (byte)'3')
            {
                return "mp3";
            }

            if (probe.Length >= 2 &&
                probe[0] == 0xFF &&
                (probe[1] & 0xE0) == 0xE0)
            {
                return "mp3";
            }

            return headerFormatHint ?? "mp3";
        }

        private static string? GetHeaderFormatHint(ReadOnlySpan<byte> header)
        {
            if (header.Length <= 0x30)
            {
                return null;
            }

            int length = Math.Min(32, header.Length - 0x30);
            ReadOnlySpan<byte> hintSpan = header.Slice(0x30, length);
            StringBuilder builder = new();

            foreach (byte value in hintSpan)
            {
                char character = (char)value;
                if (char.IsLetter(character))
                {
                    builder.Append(char.ToUpperInvariant(character));
                }
            }

            string hint = builder.ToString();
            if (hint.IndexOf("FLAC", StringComparison.Ordinal) >= 0)
            {
                return "flac";
            }

            if (hint.IndexOf("OGG", StringComparison.Ordinal) >= 0)
            {
                return "ogg";
            }

            if (hint.IndexOf("WAV", StringComparison.Ordinal) >= 0)
            {
                return "wav";
            }

            if (hint.IndexOf("APE", StringComparison.Ordinal) >= 0)
            {
                return "ape";
            }

            if (hint.IndexOf("MP3", StringComparison.Ordinal) >= 0)
            {
                return "mp3";
            }

            return null;
        }

        private static bool TryReadExact(Stream inputStream, byte[] buffer, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int bytesRead = inputStream.Read(buffer, totalRead, count - totalRead);
                if (bytesRead == 0)
                {
                    return false;
                }

                totalRead += bytesRead;
            }

            return true;
        }
    }
}
