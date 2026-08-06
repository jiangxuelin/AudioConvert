using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AudioConvert.Services
{
    internal static class ProcessCompat
    {
        public static Task WaitForExitAsync(Process process, CancellationToken cancellationToken = default)
        {
            if (process.HasExited)
            {
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler? handler = null;
            CancellationTokenRegistration registration = default;

            handler = (_, _) =>
            {
                process.Exited -= handler;
                registration.Dispose();
                tcs.TrySetResult(null);
            };

            process.EnableRaisingEvents = true;
            process.Exited += handler;

            if (process.HasExited)
            {
                process.Exited -= handler;
                return Task.CompletedTask;
            }

            if (cancellationToken.CanBeCanceled)
            {
                registration = cancellationToken.Register(() =>
                {
                    process.Exited -= handler;
                    tcs.TrySetCanceled(cancellationToken);
                });
            }

            return tcs.Task;
        }

        public static string BuildArguments(params string[] arguments)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < arguments.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(' ');
                }

                builder.Append(Quote(arguments[i]));
            }

            return builder.ToString();
        }

        private static string Quote(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            if (!value.Contains(" ") && !value.Contains("\""))
            {
                return value;
            }

            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
