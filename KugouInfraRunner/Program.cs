using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace KugouInfraRunner
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            if (args.Length < 4)
            {
                Console.WriteLine("ERROR|Missing arguments: infraDllPath, kugouDirectory, dbPath, audioHash.");
                return 1;
            }

            try
            {
                using var lookup = new KugouInfraLookup();
                if (!lookup.Load(args[0], args[1], out string? loadError))
                {
                    Console.WriteLine("ERROR|" + loadError);
                    return 2;
                }

                string? encryptionKey = lookup.QueryEncryptionKey(args[2], args[3], out string? queryError);
                if (string.IsNullOrWhiteSpace(encryptionKey))
                {
                    Console.WriteLine("ERROR|" + (queryError ?? "Encryption key was not found."));
                    return 3;
                }

                Console.WriteLine("RESULT|" + encryptionKey);
                return 0;
            }
            catch (Exception exception)
            {
                Console.WriteLine("ERROR|" + exception.Message);
                return 9;
            }
        }
    }

    internal sealed class KugouInfraLookup : IDisposable
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
            out IntPtr db,
            int flags,
            IntPtr vfs);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Sqlite3Key(IntPtr db, byte[] key, int keyLen);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Sqlite3PrepareV2(
            IntPtr db,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
            int sqlLen,
            out IntPtr stmt,
            out IntPtr tail);

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

        public bool Load(string infraDllPath, string kugouDirectory, out string? error)
        {
            error = null;

            if (!File.Exists(infraDllPath))
            {
                error = "infra.dll was not found: " + infraDllPath;
                return false;
            }

            SetDllDirectory(kugouDirectory);
            try
            {
                _infraLib = LoadLibrary(infraDllPath);
                if (_infraLib == IntPtr.Zero)
                {
                    error = "Failed to load infra.dll. Win32Error=" + Marshal.GetLastWin32Error();
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
                error = "infra.dll loaded, but sqlite exports were not found.";
                Dispose();
                return false;
            }

            return true;
        }

        public string? QueryEncryptionKey(string dbPath, string audioHash, out string? error)
        {
            error = null;
            if (_open == null)
            {
                error = "infra.dll was not initialized.";
                return null;
            }

            int rc = _open(dbPath, out IntPtr db, SQLITE_OPEN_READONLY, IntPtr.Zero);
            if (rc != SQLITE_OK)
            {
                error = "Failed to open KGMusicV3.db.";
                return null;
            }

            try
            {
                rc = _key!(db, DbKey, DbKey.Length);
                if (rc != SQLITE_OK)
                {
                    error = "Failed to unlock KGMusicV3.db.";
                    return null;
                }

                string safeAudioHash = audioHash.Replace("'", "''");
                string sql =
                    "SELECT EncryptionKey FROM ShareFileItems " +
                    "WHERE EncryptionKeyId = '" + safeAudioHash + "' AND EncryptionKey != '' LIMIT 1";

                rc = _prepare!(db, sql, -1, out IntPtr stmt, out _);
                if (rc != SQLITE_OK)
                {
                    error = "Failed to query encryption key from KGMusicV3.db.";
                    return null;
                }

                try
                {
                    rc = _step!(stmt);
                    if (rc != SQLITE_ROW)
                    {
                        error = "Encryption key was not found in KGMusicV3.db.";
                        return null;
                    }

                    IntPtr keyPtr = _columnText!(stmt, 0);
                    if (keyPtr == IntPtr.Zero)
                    {
                        error = "The encryption key value is empty.";
                        return null;
                    }

                    return PtrToStringUtf8(keyPtr);
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
