using System;
using System.IO;
using System.Threading;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 隔离基座生成流程中的目录清理，便于处理 SolidWorks 延迟释放文件句柄的情况。
    /// </summary>
    internal interface IFaceToFaceBaseDirectoryCleaner
    {
        bool TryDelete(string directoryPath, out Exception cleanupError);
    }

    /// <summary>
    /// 对文件占用和短暂权限占用执行有限重试；其他异常仍直接向上传递。
    /// </summary>
    internal sealed class FaceToFaceBaseDirectoryCleaner
        : IFaceToFaceBaseDirectoryCleaner
    {
        private const int DefaultMaxAttempts = 5;
        private const int DefaultRetryDelayMilliseconds = 150;

        private readonly Action<string, bool> _deleteDirectory;
        private readonly Func<string, bool> _directoryExists;
        private readonly Action<int> _wait;
        private readonly int _maxAttempts;
        private readonly int _retryDelayMilliseconds;

        internal FaceToFaceBaseDirectoryCleaner()
            : this(
                Directory.Delete,
                Thread.Sleep,
                DefaultMaxAttempts,
                DefaultRetryDelayMilliseconds,
                Directory.Exists)
        {
        }

        internal FaceToFaceBaseDirectoryCleaner(
            Action<string, bool> deleteDirectory,
            Action<int> wait,
            int maxAttempts,
            int retryDelayMilliseconds,
            Func<string, bool> directoryExists = null)
        {
            _deleteDirectory = deleteDirectory
                ?? throw new ArgumentNullException(nameof(deleteDirectory));
            _wait = wait ?? throw new ArgumentNullException(nameof(wait));
            _directoryExists = directoryExists ?? Directory.Exists;
            if (maxAttempts <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxAttempts));
            }

            if (retryDelayMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(retryDelayMilliseconds));
            }

            _maxAttempts = maxAttempts;
            _retryDelayMilliseconds = retryDelayMilliseconds;
        }

        public bool TryDelete(string directoryPath, out Exception cleanupError)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                throw new ArgumentException("待清理目录不能为空。", nameof(directoryPath));
            }

            cleanupError = null;
            if (!_directoryExists(directoryPath))
            {
                return true;
            }

            for (var attempt = 1; attempt <= _maxAttempts; attempt++)
            {
                try
                {
                    _deleteDirectory(directoryPath, true);
                    cleanupError = null;
                    return true;
                }
                catch (Exception exception)
                    when (exception is IOException
                        || exception is UnauthorizedAccessException)
                {
                    cleanupError = exception;
                    if (attempt < _maxAttempts)
                    {
                        // SolidWorks 关闭文档后，Windows 文件句柄可能仍需短暂时间才真正释放。
                        _wait(_retryDelayMilliseconds);
                    }
                }
            }

            return false;
        }
    }
}
