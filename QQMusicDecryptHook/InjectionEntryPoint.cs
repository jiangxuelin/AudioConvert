using EasyHook;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace QQMusicDecryptHook
{
    /// <summary>
    /// EasyHook 注入 QQ 音乐进程后的入口，调用 QQ 音乐自身解密能力并通过管道返回结果。
    /// </summary>
    public sealed class InjectionEntryPoint : IEntryPoint
    {
        private readonly string _pipeName;
        private readonly string _inputPath;
        private readonly string _outputDirectory;

        public InjectionEntryPoint(
            RemoteHooking.IContext context,
            string pipeName,
            string inputPath,
            string outputDirectory,
            string runnerBaseDirectory,
            string hookPath,
            string inputExtension,
            bool launchedByTool)
        {
            _pipeName = pipeName;
            _inputPath = inputPath;
            _outputDirectory = outputDirectory;
        }

        public void Run(
            RemoteHooking.IContext context,
            string pipeName,
            string inputPath,
            string outputDirectory,
            string runnerBaseDirectory,
            string hookPath,
            string inputExtension,
            bool launchedByTool)
        {
            using var pipe = new PipeClient(_pipeName);

            try
            {
                RemoteHooking.WakeUpProcess();

                QqMusicDecryptFileResult result = QqMusicCommonDecryptor.DecryptToFile(
                    _inputPath,
                    _outputDirectory);
                pipe.SendResult(result.OutputPath, result.DetectedFormat);
            }
            catch (Exception ex)
            {
                pipe.SendError(ex.Message);
            }
        }
    }
}
