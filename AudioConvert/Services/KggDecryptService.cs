using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace AudioConvert.Services
{
    public interface IKggDecryptService
    {
        Task<KggDecryptResult> DecryptAsync(
            string inputPath,
            string outputDirectory,
            IProgress<string>? progress = null);
    }

    public sealed class KggDecryptResult
    {
        public bool IsSuccess { get; private init; }
        public string? OutputPath { get; private init; }
        public string? ErrorMessage { get; private init; }

        public static KggDecryptResult Success(string outputPath) =>
            new() { IsSuccess = true, OutputPath = outputPath };

        public static KggDecryptResult Failure(string errorMessage) =>
            new() { IsSuccess = false, ErrorMessage = errorMessage };
    }

    public sealed class KggDecryptService : IKggDecryptService
    {
        private delegate void BufferDecryptor(Span<byte> buffer, long offset);

        private const int StandardHeaderLength = 0x100;
        private const int KgmaHeaderLength = 1024;
        private const int KgmaMinimumHeaderLength = 0x3C;
        private const uint KgmaEncryptionMode = 3;
        private const uint KggEncryptionMode = 5;
        private const int KugouRunnerTimeoutSeconds = 30;
        private const int KgmaCryptoKeyOffset = 0x2C;
        private const int KgmaCryptoKeyLength = 0x10;

        private static readonly byte[] KggMagic =
        {
        0x7C, 0xD5, 0x32, 0xEB, 0x86, 0x02, 0x7F, 0x4B,
        0xA8, 0xAF, 0xA6, 0x8E, 0x0F, 0xFF, 0x99, 0x14
    };

        private static readonly byte[] VprMagic =
        {
        0x05, 0x28, 0xBC, 0x96, 0xE9, 0xE4, 0x5A, 0x43,
        0x91, 0xAA, 0xBD, 0xD0, 0x7A, 0xF5, 0x36, 0x31
    };

        private static readonly byte[] KgmaSlot1Key = { 0x6C, 0x2C, 0x2F, 0x27 };

        private readonly string _defaultDbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Kugou8", "KGMusicV3.db");

        private readonly string _applicationBaseDirectory;

        public KggDecryptService()
            : this(AppDomain.CurrentDomain.BaseDirectory)
        {
        }

        internal KggDecryptService(string applicationBaseDirectory)
        {
            if (string.IsNullOrWhiteSpace(applicationBaseDirectory))
            {
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(applicationBaseDirectory));
            }

            _applicationBaseDirectory = applicationBaseDirectory;
        }

        public Task<KggDecryptResult> DecryptAsync(
            string inputPath,
            string outputDirectory,
            IProgress<string>? progress = null)
        {
            return Task.Run(() => DecryptCore(inputPath, outputDirectory, progress));
        }

        private KggDecryptResult DecryptCore(string inputPath, string outputDirectory, IProgress<string>? progress)
        {
            if (!File.Exists(inputPath))
            {
                return KggDecryptResult.Failure($"输入文件不存在：{inputPath}");
            }

            progress?.Report("正在解析酷狗加密文件...");
            using var fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] header = new byte[KgmaHeaderLength];
            int headerBytesRead = fs.Read(header, 0, header.Length);
            if (headerBytesRead < StandardHeaderLength)
            {
                return KggDecryptResult.Failure("文件头不完整，无法解析酷狗加密文件。");
            }

            ReadOnlySpan<byte> magic = header.AsSpan(0, KggMagic.Length);
            if (!magic.SequenceEqual(KggMagic) && !magic.SequenceEqual(VprMagic))
            {
                return KggDecryptResult.Failure("无效的酷狗加密文件头。");
            }

            uint offsetToAudio = BitConverter.ToUInt32(header, 0x10);
            uint encryptMode = BitConverter.ToUInt32(header, 0x14);

            return encryptMode switch
            {
                KgmaEncryptionMode => DecryptKgmaMode3(fs, header, headerBytesRead, offsetToAudio, inputPath, outputDirectory, progress),
                KggEncryptionMode => DecryptKggMode5(fs, header, offsetToAudio, inputPath, outputDirectory, progress),
                _ => KggDecryptResult.Failure($"不支持的酷狗加密模式：{encryptMode}")
            };
        }

        private KggDecryptResult DecryptKgmaMode3(
            FileStream inputStream,
            byte[] header,
            int headerBytesRead,
            uint offsetToAudio,
            string inputPath,
            string outputDirectory,
            IProgress<string>? progress)
        {
            if (headerBytesRead < KgmaMinimumHeaderLength)
            {
                return KggDecryptResult.Failure("KGMA 文件头不完整，无法读取 mode 3 所需元数据。");
            }

            uint cryptoSlot = BitConverter.ToUInt32(header, 0x18);
            if (cryptoSlot != 1)
            {
                return KggDecryptResult.Failure("KGMA 文件头与 mode 3 格式不匹配。");
            }

            byte[] slotBox = KugouMd5(KgmaSlot1Key);
            byte[] fileBox = BuildKgmaFileBox(header);

            inputStream.Seek(offsetToAudio, SeekOrigin.Begin);
            byte[] magicBuf = new byte[4];
            if (inputStream.Read(magicBuf, 0, magicBuf.Length) < magicBuf.Length)
            {
                return KggDecryptResult.Failure("KGMA 音频数据不完整。");
            }

            DecryptKgmaMode3Buffer(magicBuf.AsSpan(), slotBox, fileBox, 0);
            string extension = DetectExtension(magicBuf);

            progress?.Report("正在解密 KGMA 音频数据...");
            return WriteDecryptedAudio(
                inputStream,
                offsetToAudio,
                outputDirectory,
                inputPath,
                extension,
                (buffer, offset) => DecryptKgmaMode3Buffer(buffer, slotBox, fileBox, offset));
        }

        private KggDecryptResult DecryptKggMode5(
            FileStream inputStream,
            byte[] header,
            uint offsetToAudio,
            string inputPath,
            string outputDirectory,
            IProgress<string>? progress)
        {
            if (!File.Exists(_defaultDbPath))
            {
                return KggDecryptResult.Failure(
                    $"未找到酷狗数据库 KGMusicV3.db。{Environment.NewLine}预期路径：{_defaultDbPath}");
            }

            string? kugouDir = FindKugouDirectory();
            if (kugouDir == null)
            {
                return KggDecryptResult.Failure("未找到酷狗音乐安装目录，请确认已安装酷狗音乐。");
            }

            string infraPath = Path.Combine(kugouDir, "infra.dll");
            if (!File.Exists(infraPath))
            {
                return KggDecryptResult.Failure($"在酷狗安装目录中未找到 infra.dll。{Environment.NewLine}路径：{infraPath}");
            }

            uint audioHashLen = BitConverter.ToUInt32(header, 0x44);
            if (audioHashLen != 0x20)
            {
                return KggDecryptResult.Failure($"音频哈希长度无效（期望 0x20，实际 0x{audioHashLen:X2}）。");
            }

            string audioHash = Encoding.ASCII.GetString(header, 0x48, (int)audioHashLen);

            progress?.Report("正在读取酷狗解密密钥...");
            if (!TryResolveEncryptionKey(audioHash, infraPath, kugouDir, out string? ekey, out string? errorMessage))
            {
                return KggDecryptResult.Failure(errorMessage ?? "未找到解密密钥，请先用酷狗播放一次此文件后重试。");
            }

            IQmc2Decryptor? decryptor = Qmc2Factory.Create(ekey!);
            if (decryptor == null)
            {
                return KggDecryptResult.Failure("酷狗解密密钥解析失败。");
            }

            inputStream.Seek(offsetToAudio, SeekOrigin.Begin);
            byte[] magicBuf = new byte[4];
            if (inputStream.Read(magicBuf, 0, magicBuf.Length) < magicBuf.Length)
            {
                return KggDecryptResult.Failure("KGG 音频数据不完整。");
            }

            decryptor.Decrypt(magicBuf, 0);
            string extension = DetectExtension(magicBuf);

            progress?.Report("正在解密 KGG 音频数据...");
            return WriteDecryptedAudio(
                inputStream,
                offsetToAudio,
                outputDirectory,
                inputPath,
                extension,
                (buffer, offset) => decryptor.Decrypt(buffer, offset));
        }

        private bool TryResolveEncryptionKey(
            string audioHash,
            string infraPath,
            string kugouDirectory,
            out string? ekey,
            out string? errorMessage)
        {
            if (!Environment.Is64BitProcess &&
                TryResolveEncryptionKeyDirect(audioHash, infraPath, kugouDirectory, out ekey))
            {
                errorMessage = null;
                return true;
            }

            return TryResolveEncryptionKeyWithRunner(audioHash, infraPath, kugouDirectory, out ekey, out errorMessage);
        }

        private bool TryResolveEncryptionKeyDirect(
            string audioHash,
            string infraPath,
            string kugouDirectory,
            out string? ekey)
        {
            ekey = null;

            using var db = new KugouInfraDatabase();
            if (!db.Load(infraPath, kugouDirectory))
            {
                return false;
            }

            Dictionary<string, string>? ekeyDb = db.DumpEKeys(_defaultDbPath);
            if (ekeyDb == null || ekeyDb.Count == 0)
            {
                return false;
            }

            return ekeyDb.TryGetValue(audioHash, out ekey);
        }

        private bool TryResolveEncryptionKeyWithRunner(
            string audioHash,
            string infraPath,
            string kugouDirectory,
            out string? ekey,
            out string? errorMessage)
        {
            ekey = null;
            errorMessage = null;

            string? runnerPath = FindKugouInfraRunnerPath();
            if (runnerPath == null)
            {
                errorMessage =
                    $"当前主程序是 {(Environment.Is64BitProcess ? "64" : "32")} 位进程，而该版本酷狗的 infra.dll 是 x86，不能直接在当前进程加载。" +
                    $"{Environment.NewLine}未找到 KugouInfraRunner.exe。";
                return false;
            }

            try
            {
                IRunnerProcessLauncher launcher = IsPackagedProcess()
                    ? new UnelevatedRunnerLauncher()
                    : new DirectRunnerLauncher();

                using RunnerLaunchResult launchResult = launcher.Launch(
                    runnerPath,
                    ProcessCompat.BuildArguments(infraPath, kugouDirectory, _defaultDbPath, audioHash));

                if (!launchResult.Started || launchResult.Process == null)
                {
                    errorMessage = "无法启动 KugouInfraRunner.exe。"
                        + (string.IsNullOrWhiteSpace(launchResult.StartupError)
                            ? string.Empty
                            : Environment.NewLine + launchResult.StartupError)
                        + Environment.NewLine
                        + "launch_mode=" + (launchResult.LaunchMode ?? "unknown");
                    return false;
                }

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(KugouRunnerTimeoutSeconds));
                Task<string> stdoutTask = launchResult.StandardOutput?.ReadToEndAsync() ?? Task.FromResult(string.Empty);
                Task<string> stderrTask = launchResult.StandardError?.ReadToEndAsync() ?? Task.FromResult(string.Empty);
                ProcessCompat.WaitForExitAsync(launchResult.Process, timeoutCts.Token).GetAwaiter().GetResult();
                Task.WaitAll(new Task[] { stdoutTask, stderrTask });

                string stdout = stdoutTask.Result.Trim();
                string stderr = stderrTask.Result.Trim();

                foreach (string line in stdout.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (line.StartsWith("RESULT|", StringComparison.Ordinal))
                    {
                        ekey = line.Substring("RESULT|".Length).Trim();
                        if (!string.IsNullOrWhiteSpace(ekey))
                        {
                            return true;
                        }
                    }

                    if (line.StartsWith("ERROR|", StringComparison.Ordinal))
                    {
                        errorMessage = line.Substring("ERROR|".Length).Trim();
                    }
                }

                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    errorMessage = stderr;
                }

                errorMessage ??=
                    $"加载酷狗 infra.dll 失败。{Environment.NewLine}路径：{infraPath}{Environment.NewLine}" +
                    "这通常是因为主程序当前为 64 位，而该版本酷狗的 infra.dll 为 x86。"
                    + Environment.NewLine
                    + "launch_mode=" + (launchResult.LaunchMode ?? "unknown");
                return false;
            }
            catch (OperationCanceledException)
            {
                errorMessage = "读取酷狗解密密钥超时。";
                return false;
            }
            catch (Exception exception)
            {
                errorMessage = "读取酷狗解密密钥失败：" + exception.Message;
                return false;
            }
        }

        private static KggDecryptResult WriteDecryptedAudio(
            FileStream inputStream,
            uint offsetToAudio,
            string outputDirectory,
            string inputPath,
            string extension,
            BufferDecryptor decrypt)
        {
            string baseName = Path.GetFileNameWithoutExtension(inputPath);
            string outputPath = Path.Combine(outputDirectory, $"{baseName}.{extension}");
            Directory.CreateDirectory(outputDirectory);

            inputStream.Seek(offsetToAudio, SeekOrigin.Begin);
            using var outputStream = File.Create(outputPath);
            byte[] buffer = new byte[1024 * 1024];
            long offset = 0;
            int bytesRead;
            while ((bytesRead = inputStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                decrypt(buffer.AsSpan(0, bytesRead), offset);
                outputStream.Write(buffer, 0, bytesRead);
                offset += bytesRead;
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                return KggDecryptResult.Failure($"解密后的输出文件未生成：{outputPath}");
            }

            return KggDecryptResult.Success(outputPath);
        }

        private static void DecryptKgmaMode3Buffer(
            Span<byte> data,
            ReadOnlySpan<byte> slotBox,
            ReadOnlySpan<byte> fileBox,
            long offset)
        {
            for (int i = 0; i < data.Length; i++)
            {
                long absoluteOffset = offset + i;
                data[i] ^= fileBox[(int)(absoluteOffset % fileBox.Length)];
                data[i] ^= (byte)(data[i] << 4);
                data[i] ^= slotBox[(int)(absoluteOffset % slotBox.Length)];
                data[i] ^= XorCollapseOffset(absoluteOffset);
            }
        }

        private static byte[] BuildKgmaFileBox(byte[] header)
        {
            byte[] cryptoKey = new byte[KgmaCryptoKeyLength];
            Buffer.BlockCopy(header, KgmaCryptoKeyOffset, cryptoKey, 0, cryptoKey.Length);

            byte[] md5Box = KugouMd5(cryptoKey);
            byte[] fileBox = new byte[md5Box.Length + 1];
            Buffer.BlockCopy(md5Box, 0, fileBox, 0, md5Box.Length);
            fileBox[fileBox.Length - 1] = 0x6B;
            return fileBox;
        }

        private static byte[] KugouMd5(ReadOnlySpan<byte> source)
        {
            byte[] digest;
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                digest = md5.ComputeHash(source.ToArray());
            }

            byte[] result = new byte[digest.Length];
            for (int i = 0; i < digest.Length; i += 2)
            {
                result[i] = digest[14 - i];
                result[i + 1] = digest[15 - i];
            }

            return result;
        }

        private static byte XorCollapseOffset(long offset)
        {
            uint value = unchecked((uint)offset);
            return (byte)(value ^ (value >> 8) ^ (value >> 16) ^ (value >> 24));
        }

        private static string DetectExtension(ReadOnlySpan<byte> magic)
        {
            if (magic.Length >= 4)
            {
                if (magic[0] == 'f' && magic[1] == 'L' && magic[2] == 'a' && magic[3] == 'C')
                {
                    return "flac";
                }

                if (magic[0] == 'O' && magic[1] == 'g' && magic[2] == 'g' && magic[3] == 'S')
                {
                    return "ogg";
                }
            }

            return "mp3";
        }

        private string? FindKugouInfraRunnerPath()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
            Path.Combine(baseDirectory, "KugouInfraRunner.exe"),
            Path.Combine(baseDirectory, "Tools", "KugouInfraRunner.exe"),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "KugouInfraRunner", "bin", "Debug", "net472", "KugouInfraRunner.exe")),
            Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", "KugouInfraRunner", "bin", "Release", "net472", "KugouInfraRunner.exe"))
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

        private static bool IsPackagedProcess()
        {
            try
            {
                string packageName = Windows.ApplicationModel.Package.Current.Id.Name;
                return !string.IsNullOrWhiteSpace(packageName);
            }
            catch
            {
                return false;
            }
        }

        private static string? FindKugouDirectory()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\KuGou");
                if (key != null)
                {
                    string? kugou8 = key.GetValue("KuGou8") as string;
                    if (!string.IsNullOrEmpty(kugou8) && Directory.Exists(kugou8))
                    {
                        return kugou8;
                    }

                    string? appPath = key.GetValue("AppPath") as string;
                    if (!string.IsNullOrEmpty(appPath) && Directory.Exists(appPath))
                    {
                        foreach (string dir in Directory.GetDirectories(appPath))
                        {
                            if (File.Exists(Path.Combine(dir, "infra.dll")))
                            {
                                return dir;
                            }
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"Failed to query KuGou installation directory from registry: {exception}");
            }

            string[] fallbackPaths =
            {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "KuGou", "KGMusic"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "KuGou", "KGMusic"),
            @"D:\SoftWare\KGMusic",
            @"C:\KuGou\KGMusic",
        };

            foreach (string basePath in fallbackPaths)
            {
                if (!Directory.Exists(basePath))
                {
                    continue;
                }

                foreach (string dir in Directory.GetDirectories(basePath))
                {
                    if (File.Exists(Path.Combine(dir, "infra.dll")))
                    {
                        return dir;
                    }
                }
            }

            return null;
        }
    }

    internal sealed class KugouInfraDatabase : IDisposable
    {
        private const int SQLITE_OK = 0;
        private const int SQLITE_ROW = 100;
        private const int SQLITE_OPEN_READONLY = 1;
        private static readonly byte[] DbKey = Encoding.ASCII.GetBytes("7777B48756BA491BB4CEE771A3E2727E");

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string? lpPathName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeLibrary(IntPtr hModule);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Sqlite3OpenV2(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
            out IntPtr db, int flags, IntPtr vfs);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Sqlite3Key(IntPtr db, byte[] key, int keyLen);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Sqlite3PrepareV2(
            IntPtr db,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
            int sqlLen, out IntPtr stmt, out IntPtr tail);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Sqlite3Step(IntPtr stmt);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate IntPtr Sqlite3ColumnText(IntPtr stmt, int col);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Sqlite3Finalize(IntPtr stmt);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Sqlite3CloseV2(IntPtr db);

        private IntPtr _infraLib;
        private Sqlite3OpenV2? _open;
        private Sqlite3Key? _key;
        private Sqlite3PrepareV2? _prepare;
        private Sqlite3Step? _step;
        private Sqlite3ColumnText? _columnText;
        private Sqlite3Finalize? _finalize;
        private Sqlite3CloseV2? _close;

        public bool Load(string infraDllPath, string kugouDirectory)
        {
            SetDllDirectory(kugouDirectory);
            try
            {
                _infraLib = LoadLibrary(infraDllPath);
                if (_infraLib == IntPtr.Zero)
                {
                    return false;
                }
            }
            finally
            {
                SetDllDirectory(null);
            }

            bool ok = true;
            ok &= TryGetDelegate("sqlite3_open_v2", out _open);
            ok &= TryGetDelegate("sqlite3_key", out _key);
            ok &= TryGetDelegate("sqlite3_prepare_v2", out _prepare);
            ok &= TryGetDelegate("sqlite3_step", out _step);
            ok &= TryGetDelegate("sqlite3_column_text", out _columnText);
            ok &= TryGetDelegate("sqlite3_finalize", out _finalize);
            ok &= TryGetDelegate("sqlite3_close_v2", out _close);

            if (!ok)
            {
                Dispose();
                return false;
            }

            return true;
        }

        public Dictionary<string, string>? DumpEKeys(string dbPath)
        {
            if (_open == null)
            {
                return null;
            }

            int rc = _open(dbPath, out IntPtr db, SQLITE_OPEN_READONLY, IntPtr.Zero);
            if (rc != SQLITE_OK)
            {
                return null;
            }

            try
            {
                rc = _key!(db, DbKey, DbKey.Length);
                if (rc != SQLITE_OK)
                {
                    return null;
                }

                rc = _prepare!(
                    db,
                    "SELECT EncryptionKeyId, EncryptionKey FROM ShareFileItems WHERE EncryptionKey != ''",
                    -1,
                    out IntPtr stmt,
                    out _);
                if (rc != SQLITE_OK)
                {
                    return null;
                }

                try
                {
                    var result = new Dictionary<string, string>();
                    while ((rc = _step!(stmt)) == SQLITE_ROW)
                    {
                        IntPtr keyIdPtr = _columnText!(stmt, 0);
                        IntPtr keyPtr = _columnText(stmt, 1);
                        if (keyIdPtr != IntPtr.Zero && keyPtr != IntPtr.Zero)
                        {
                            string? keyId = PtrToStringUtf8(keyIdPtr);
                            string? keyVal = PtrToStringUtf8(keyPtr);
                            if (!string.IsNullOrEmpty(keyId) && !string.IsNullOrEmpty(keyVal))
                            {
                                string keyIdValue = keyId!;
                                string keyValue = keyVal!;
                                result[keyIdValue] = keyValue;
                            }
                        }
                    }

                    return result;
                }
                finally
                {
                    _finalize!(stmt);
                }
            }
            finally
            {
                _close!(db);
            }
        }

        private bool TryGetDelegate<T>(string name, out T? del) where T : Delegate
        {
            IntPtr ptr = GetProcAddress(_infraLib, name);
            if (ptr != IntPtr.Zero)
            {
                del = Marshal.GetDelegateForFunctionPointer<T>(ptr);
                return true;
            }

            del = default;
            return false;
        }

        private static string? PtrToStringUtf8(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
            {
                return null;
            }

            int length = 0;
            while (Marshal.ReadByte(ptr, length) != 0)
            {
                length++;
            }

            if (length == 0)
            {
                return string.Empty;
            }

            byte[] bytes = new byte[length];
            Marshal.Copy(ptr, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }

        public void Dispose()
        {
            if (_infraLib != IntPtr.Zero)
            {
                FreeLibrary(_infraLib);
                _infraLib = IntPtr.Zero;
            }
        }
    }
}
