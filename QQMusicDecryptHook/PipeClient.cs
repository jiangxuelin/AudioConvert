using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace QQMusicDecryptHook
{
    internal sealed class PipeClient : IDisposable
    {
        private readonly NamedPipeClientStream? _pipe;
        private readonly StreamWriter? _writer;
        private readonly object _syncRoot = new();

        public PipeClient(string pipeName, int retryCount = 10, int retryDelayMs = 300)
        {
            Exception? lastError = null;

            for (int i = 0; i < retryCount; i++)
            {
                try
                {
                    _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
                    _pipe.Connect(3000);
                    _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };
                    return;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    try { _pipe?.Dispose(); } catch { }
                    Thread.Sleep(retryDelayMs);
                }
            }

            throw new IOException($"Unable to connect to pipe \\\\.\\pipe\\{pipeName}: {lastError?.Message}", lastError);
        }

        public void SendLog(string message) => Send("LOG", message);

        public void SendError(string message) => Send("ERROR", message);

        public void SendResult(string outputPath, string detectedFormat) => Send("RESULT", outputPath, detectedFormat);

        private void Send(params string[] parts)
        {
            lock (_syncRoot)
            {
                _writer?.WriteLine(string.Join("|", parts));
            }
        }

        public void Dispose()
        {
            try { _writer?.Dispose(); } catch { }
            try { _pipe?.Dispose(); } catch { }
        }
    }
}
