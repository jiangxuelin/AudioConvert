using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AudioConvert.Services
{
    public interface INcmDecoder
    {
        Task<NcmDecodeResult> DecodeAsync(string inputPath, string outputDirectory, string outputBaseName);
    }

    public sealed class NcmDecoder : INcmDecoder
    {
        private const string Mp3Format = "mp3";
        private const string FlacFormat = "flac";
        private const int MagicHeaderLength = 10;
        private const int AudioBufferSize = 0x8000;

        private static readonly byte[] MagicHeader =
        {
        0x43, 0x54, 0x45, 0x4E, 0x46, 0x44, 0x41, 0x4D, 0x01, 0x70
    };

        private static readonly byte[] CoreKey =
        {
        0x68, 0x7A, 0x48, 0x52, 0x41, 0x6D, 0x73, 0x6F,
        0x35, 0x6B, 0x49, 0x6E, 0x62, 0x61, 0x78, 0x57
    };

        private static readonly byte[] MetaKey =
        {
        0x23, 0x31, 0x34, 0x6C, 0x6A, 0x6B, 0x5F, 0x21,
        0x5C, 0x5D, 0x26, 0x30, 0x55, 0x3C, 0x27, 0x28
    };

        private static readonly Regex FormatRegex = new Regex(
            "\"format\"\\s*:\\s*\"(?<format>[^\"]+)\"",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public Task<NcmDecodeResult> DecodeAsync(string inputPath, string outputDirectory, string outputBaseName)
        {
            return Task.Run(() => DecodeCore(inputPath, outputDirectory, outputBaseName));
        }

        private NcmDecodeResult DecodeCore(string inputPath, string outputDirectory, string outputBaseName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(inputPath))
                {
                    throw new ArgumentException("Value cannot be null or whitespace.", nameof(inputPath));
                }

                if (string.IsNullOrWhiteSpace(outputDirectory))
                {
                    throw new ArgumentException("Value cannot be null or whitespace.", nameof(outputDirectory));
                }

                if (string.IsNullOrWhiteSpace(outputBaseName))
                {
                    throw new ArgumentException("Value cannot be null or whitespace.", nameof(outputBaseName));
                }

                Directory.CreateDirectory(outputDirectory);

                using (var inputStream = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    ValidateMagicHeader(inputStream);

                    byte[] encryptedKey = ReadSizedBlock(inputStream);
                    XorInPlace(encryptedKey, 0x64);
                    byte[] keyData = ParseKeyData(encryptedKey);
                    byte[] keyBox = BuildKeyBox(keyData);

                    string? formatHint = null;
                    byte[] encryptedMeta = ReadSizedBlock(inputStream);
                    if (encryptedMeta.Length > 0)
                    {
                        XorInPlace(encryptedMeta, 0x63);
                        formatHint = ParseFormatHint(encryptedMeta);
                    }

                    SkipExact(inputStream, 4);
                    SkipExact(inputStream, 5);
                    SkipCoverData(inputStream);

                    long audioStart = inputStream.Position;
                    string detectedFormat = DetectOutputFormat(inputStream, keyBox, formatHint);
                    string outputPath = Path.Combine(
                        outputDirectory,
                        Path.GetFileNameWithoutExtension(outputBaseName) + "." + detectedFormat);

                    inputStream.Position = audioStart;
                    WriteDecodedAudio(inputStream, outputPath, keyBox);
                    return NcmDecodeResult.Success(outputPath, detectedFormat);
                }
            }
            catch (Exception exception)
            {
                return NcmDecodeResult.Failure("NCM decode failed: " + exception.Message);
            }
        }

        private static void ValidateMagicHeader(Stream inputStream)
        {
            byte[] header = new byte[MagicHeaderLength];
            if (!TryReadExact(inputStream, header, header.Length) ||
                !header.SequenceEqual(MagicHeader))
            {
                throw new FormatException("Target file is not a valid NCM file.");
            }
        }

        private static byte[] ReadSizedBlock(Stream inputStream)
        {
            uint length = ReadUInt32LittleEndian(inputStream);
            if (length == 0)
            {
                return Array.Empty<byte>();
            }

            if (length > int.MaxValue)
            {
                throw new FormatException("NCM block is too large.");
            }

            byte[] buffer = new byte[length];
            if (!TryReadExact(inputStream, buffer, buffer.Length))
            {
                throw new FormatException("Unexpected end of NCM file.");
            }

            return buffer;
        }

        private static byte[] ParseKeyData(byte[] encryptedKey)
        {
            byte[] decrypted = DecryptAesEcb(encryptedKey, CoreKey);
            if (decrypted.Length <= 17)
            {
                throw new FormatException("Broken NCM file: invalid key data.");
            }

            byte[] keyData = new byte[decrypted.Length - 17];
            Buffer.BlockCopy(decrypted, 17, keyData, 0, keyData.Length);
            return keyData;
        }

        private static string? ParseFormatHint(byte[] encryptedMeta)
        {
            if (encryptedMeta.Length <= 22)
            {
                return null;
            }

            byte[] base64Bytes = new byte[encryptedMeta.Length - 22];
            Buffer.BlockCopy(encryptedMeta, 22, base64Bytes, 0, base64Bytes.Length);

            string base64 = Encoding.UTF8.GetString(base64Bytes);
            byte[] metaBytes = Convert.FromBase64String(base64);
            byte[] decryptedMeta = DecryptAesEcb(metaBytes, MetaKey);
            if (decryptedMeta.Length <= 6)
            {
                return null;
            }

            string metaJson = Encoding.UTF8.GetString(decryptedMeta, 6, decryptedMeta.Length - 6);
            Match match = FormatRegex.Match(metaJson);
            return match.Success ? match.Groups["format"].Value.ToLowerInvariant() : null;
        }

        private static byte[] DecryptAesEcb(byte[] data, byte[] key)
        {
            using (Aes aes = Aes.Create())
            {
                aes.Key = key;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.PKCS7;

                using (ICryptoTransform decryptor = aes.CreateDecryptor())
                {
                    return decryptor.TransformFinalBlock(data, 0, data.Length);
                }
            }
        }

        private static byte[] BuildKeyBox(byte[] keyData)
        {
            if (keyData.Length == 0)
            {
                throw new FormatException("Broken NCM file: empty RC4 key.");
            }

            byte[] keyBox = new byte[256];
            for (int i = 0; i < keyBox.Length; i++)
            {
                keyBox[i] = (byte)i;
            }

            int c = 0;
            int lastByte = 0;
            int keyOffset = 0;
            for (int i = 0; i < keyBox.Length; i++)
            {
                int swap = keyBox[i];
                c = (swap + lastByte + keyData[keyOffset]) & 0xFF;
                keyOffset = (keyOffset + 1) % keyData.Length;
                keyBox[i] = keyBox[c];
                keyBox[c] = (byte)swap;
                lastByte = c;
            }

            return keyBox;
        }

        private static string DetectOutputFormat(Stream inputStream, byte[] keyBox, string? formatHint)
        {
            byte[] probe = new byte[16];
            int bytesRead = inputStream.Read(probe, 0, probe.Length);
            if (bytesRead <= 0)
            {
                throw new FormatException("NCM file does not contain audio data.");
            }

            DecryptAudioBuffer(probe, bytesRead, keyBox, 0);
            return DetectExtension(probe, bytesRead, formatHint);
        }

        private static void WriteDecodedAudio(Stream inputStream, string outputPath, byte[] keyBox)
        {
            using (var outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[AudioBufferSize];
                long offset = 0;
                int bytesRead;
                while ((bytesRead = inputStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    DecryptAudioBuffer(buffer, bytesRead, keyBox, offset);
                    outputStream.Write(buffer, 0, bytesRead);
                    offset += bytesRead;
                }
            }
        }

        private static void DecryptAudioBuffer(byte[] buffer, int count, byte[] keyBox, long offset)
        {
            for (int i = 0; i < count; i++)
            {
                int j = (int)((offset + i + 1) & 0xFF);
                int index = (keyBox[j] + keyBox[(keyBox[j] + j) & 0xFF]) & 0xFF;
                buffer[i] ^= keyBox[index];
            }
        }

        private static string DetectExtension(byte[] buffer, int count, string? formatHint)
        {
            if (count >= 4 &&
                buffer[0] == (byte)'f' &&
                buffer[1] == (byte)'L' &&
                buffer[2] == (byte)'a' &&
                buffer[3] == (byte)'C')
            {
                return FlacFormat;
            }

            if (count >= 3 &&
                buffer[0] == (byte)'I' &&
                buffer[1] == (byte)'D' &&
                buffer[2] == (byte)'3')
            {
                return Mp3Format;
            }

            if (count >= 2 &&
                buffer[0] == 0xFF &&
                (buffer[1] & 0xE0) == 0xE0)
            {
                return Mp3Format;
            }

            if (string.Equals(formatHint, FlacFormat, StringComparison.OrdinalIgnoreCase))
            {
                return FlacFormat;
            }

            return Mp3Format;
        }

        private static void XorInPlace(byte[] buffer, byte mask)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                buffer[i] ^= mask;
            }
        }

        private static uint ReadUInt32LittleEndian(Stream inputStream)
        {
            byte[] bytes = new byte[4];
            if (!TryReadExact(inputStream, bytes, bytes.Length))
            {
                throw new FormatException("Unexpected end of NCM file.");
            }

            return BitConverter.ToUInt32(bytes, 0);
        }

        private static void SkipExact(Stream inputStream, uint byteCount)
        {
            if (byteCount == 0)
            {
                return;
            }

            if (inputStream.CanSeek)
            {
                if (inputStream.Length - inputStream.Position < byteCount)
                {
                    throw new FormatException("Unexpected end of NCM file.");
                }

                inputStream.Seek(byteCount, SeekOrigin.Current);
                return;
            }

            byte[] buffer = new byte[Math.Min(byteCount, 4096u)];
            uint remaining = byteCount;
            while (remaining > 0)
            {
                int bytesRead = inputStream.Read(buffer, 0, (int)Math.Min((uint)buffer.Length, remaining));
                if (bytesRead <= 0)
                {
                    throw new FormatException("Unexpected end of NCM file.");
                }

                remaining -= (uint)bytesRead;
            }
        }

        private static void SkipCoverData(Stream inputStream)
        {
            long lengthFieldPosition = inputStream.Position;
            uint firstLength = ReadUInt32LittleEndian(inputStream);
            if (firstLength == 0)
            {
                return;
            }

            if (inputStream.CanSeek && inputStream.Length - inputStream.Position >= 4)
            {
                uint secondLength = ReadUInt32LittleEndian(inputStream);
                long remainingAfterSecondLength = inputStream.Length - inputStream.Position;
                bool looksLikeDualLengthLayout =
                    secondLength <= remainingAfterSecondLength &&
                    firstLength >= secondLength &&
                    firstLength - secondLength <= 16;

                if (looksLikeDualLengthLayout)
                {
                    SkipExact(inputStream, secondLength);
                    return;
                }

                inputStream.Position = lengthFieldPosition + 4;
            }

            SkipExact(inputStream, firstLength);
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
