using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace QQMusicDecryptHook
{
    internal sealed class QqMusicDecryptFileResult
    {
        public string OutputPath { get; set; } = string.Empty;

        public string DetectedFormat { get; set; } = string.Empty;
    }

    internal static class QqMusicCommonDecryptor
    {
        private const string TargetDll = "QQMusicCommon.dll";
        private const int ObjectSize = 0x28;

        private static readonly string[] ConstructorExports =
        [
            "??0EncAndDesMediaFile@@QAE@XZ",
            "??0EncAndDesMediaFile@@QEAA@XZ"
        ];

        private static readonly string[] DestructorExports =
        [
            "??1EncAndDesMediaFile@@QAE@XZ",
            "??1EncAndDesMediaFile@@QEAA@XZ"
        ];

        private static readonly string[] OpenExports =
        [
            "?Open@EncAndDesMediaFile@@QAE_NPB_W_N1@Z",
            "?Open@EncAndDesMediaFile@@QEAA_NPEB_W_N0@Z",
            "?Open@EncAndDesMediaFile@@QEAA_NPEB_W_N1@Z"
        ];

        private static readonly string[] GetSizeExports =
        [
            "?GetSize@EncAndDesMediaFile@@QAEKXZ",
            "?GetSize@EncAndDesMediaFile@@QEAAKXZ"
        ];

        private static readonly string[] ReadExports =
        [
            "?Read@EncAndDesMediaFile@@QAEKPAEK_J@Z",
            "?Read@EncAndDesMediaFile@@QEAA_KPEAE_K_J@Z"
        ];

        public static QqMusicDecryptFileResult DecryptToFile(string inputPath, string outputDirectory)
        {
            if (!File.Exists(inputPath))
            {
                throw new FileNotFoundException("QQ Music encrypted file does not exist.", inputPath);
            }

            Directory.CreateDirectory(outputDirectory);

            IntPtr module = GetModuleHandleW(TargetDll);
            if (module == IntPtr.Zero)
            {
                module = LoadLibraryW(TargetDll);
            }

            if (module == IntPtr.Zero)
            {
                throw new InvalidOperationException($"Unable to load {TargetDll} from QQMusic process.");
            }

            ConstructorDelegate constructor = ResolveExport<ConstructorDelegate>(module, ConstructorExports, "EncAndDesMediaFile constructor");
            DestructorDelegate destructor = ResolveExport<DestructorDelegate>(module, DestructorExports, "EncAndDesMediaFile destructor");
            OpenDelegate open = ResolveExport<OpenDelegate>(module, OpenExports, "EncAndDesMediaFile::Open");
            GetSizeDelegate getSize = ResolveExport<GetSizeDelegate>(module, GetSizeExports, "EncAndDesMediaFile::GetSize");
            ReadDelegate read = ResolveExport<ReadDelegate>(module, ReadExports, "EncAndDesMediaFile::Read");

            IntPtr objectPtr = Marshal.AllocHGlobal(ObjectSize);
            IntPtr fileNamePtr = IntPtr.Zero;
            IntPtr bufferPtr = IntPtr.Zero;

            try
            {
                Marshal.Copy(new byte[ObjectSize], 0, objectPtr, ObjectSize);
                constructor(objectPtr);

                fileNamePtr = Marshal.StringToHGlobalUni(inputPath);
                bool opened = open(objectPtr, fileNamePtr, true, false);
                if (!opened)
                {
                    throw new InvalidOperationException("QQMusicCommon failed to open the selected QQ Music encrypted file.");
                }

                uint fileSize = getSize(objectPtr);
                if (fileSize == 0)
                {
                    throw new InvalidOperationException("QQMusicCommon returned an empty decrypted stream.");
                }

                bufferPtr = Marshal.AllocHGlobal((int)fileSize);
                uint actualSize = read(objectPtr, bufferPtr, fileSize, 0);
                if (actualSize == 0)
                {
                    throw new InvalidOperationException("QQMusicCommon read returned no decrypted audio bytes.");
                }

                byte[] audioData = new byte[actualSize];
                Marshal.Copy(bufferPtr, audioData, 0, (int)actualSize);

                string detectedFormat = DetectFormat(audioData);
                string outputPath = Path.Combine(
                    outputDirectory,
                    Path.GetFileNameWithoutExtension(inputPath) + "." + detectedFormat);

                File.WriteAllBytes(outputPath, audioData);

                return new QqMusicDecryptFileResult
                {
                    OutputPath = outputPath,
                    DetectedFormat = detectedFormat
                };
            }
            finally
            {
                try
                {
                    if (objectPtr != IntPtr.Zero)
                    {
                        destructor(objectPtr);
                    }
                }
                catch
                {
                }

                if (bufferPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(bufferPtr);
                }

                if (fileNamePtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(fileNamePtr);
                }

                if (objectPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(objectPtr);
                }
            }
        }

        private static string DetectFormat(byte[] data)
        {
            if (StartsWith(data, 0x4F, 0x67, 0x67, 0x53))
            {
                return "ogg";
            }

            if (StartsWith(data, 0x66, 0x4C, 0x61, 0x43))
            {
                return "flac";
            }

            if (StartsWith(data, 0x49, 0x44, 0x33) ||
                StartsWith(data, 0xFF, 0xFB) ||
                StartsWith(data, 0xFF, 0xF3) ||
                StartsWith(data, 0xFF, 0xF2))
            {
                return "mp3";
            }

            throw new InvalidOperationException(BuildInvalidAudioDataMessage(data));
        }

        private static string BuildInvalidAudioDataMessage(byte[] data)
        {
            int previewLength = Math.Min(data.Length, 16);
            var builder = new StringBuilder(previewLength * 3);
            for (int index = 0; index < previewLength; index++)
            {
                if (index > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(data[index].ToString("X2"));
            }

            string preview = builder.Length == 0 ? "<empty>" : builder.ToString();
            return $"QQMusicCommon returned invalid decrypted audio data. Header={preview}; Size={data.Length}.";
        }

        private static bool StartsWith(byte[] data, params byte[] header)
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

        private static T ResolveExport<T>(IntPtr module, string[] candidates, string symbolName) where T : Delegate
        {
            foreach (string candidate in candidates)
            {
                IntPtr address = GetProcAddress(module, candidate);
                if (address != IntPtr.Zero)
                {
                    return Marshal.GetDelegateForFunctionPointer<T>(address);
                }
            }

            throw new MissingMethodException($"{symbolName} export was not found in {TargetDll}. Tried: {string.Join(", ", candidates)}");
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string lpModuleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate IntPtr ConstructorDelegate(IntPtr thisPtr);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate void DestructorDelegate(IntPtr thisPtr);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        [return: MarshalAs(UnmanagedType.I1)]
        private delegate bool OpenDelegate(
            IntPtr thisPtr,
            IntPtr fileName,
            [MarshalAs(UnmanagedType.I1)] bool decrypt,
            [MarshalAs(UnmanagedType.I1)] bool reserved);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate uint GetSizeDelegate(IntPtr thisPtr);

        [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
        private delegate uint ReadDelegate(IntPtr thisPtr, IntPtr buffer, uint size, ulong offset);
    }
}
