using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NLog;

namespace CKBusbarBridge.Helpers
{
    /// <summary>项目通用本地测试日志；目录由开发人员准备，业务代码不创建目录。</summary>
    internal sealed class ProjectLocalTestLog
    {
        private const long MaximumLogBytes = 200L * 1024 * 1024;
        private const string LogFilePrefix = "本地测试-";
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly object _writeLock = new object();
        private readonly Encoding _logEncoding = new UTF8Encoding(false);

        internal static ProjectLocalTestLog Default { get; } = new ProjectLocalTestLog();
        internal string DirectoryPath => @"G:\项目日志\CKBusbarBridge";
        internal bool IsEnabled { get; set; } = true;
        internal bool CanWrite => IsEnabled && Directory.Exists(DirectoryPath);

        internal void Write(string operationId, string stage, Func<string> createDetail)
        {
            if (!CanWrite) return;
            try
            {
                var logTime = DateTime.Now;
                var logDate = logTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var logEntry = logTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    + " | " + operationId + " | " + stage + System.Environment.NewLine
                    + createDetail() + System.Environment.NewLine;
                var entryBytes = _logEncoding.GetByteCount(logEntry);
                var logPath = Path.Combine(DirectoryPath, LogFilePrefix + logDate + ".log");
                lock (_writeLock)
                {
                    if (!CanWrite || !MakeRoom(logDate, entryBytes)) return;
                    File.AppendAllText(logPath, logEntry, _logEncoding);
                }
            }
            catch (Exception diagnosticError)
            {
                // 诊断是可选旁路；采集或写盘失败不能覆盖原业务异常或阻断生成。
                Logger.Warn(diagnosticError, "项目本地测试日志记录失败，阶段：{0}", stage);
            }
        }

        private bool MakeRoom(string currentDate, int entryBytes)
        {
            var dailyFiles = new DirectoryInfo(DirectoryPath).GetFiles(LogFilePrefix + "*.log")
                .Select(file => new { File = file, DateText = Path.GetFileNameWithoutExtension(file.Name).Substring(LogFilePrefix.Length) })
                .Where(item => DateTime.TryParseExact(item.DateText, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out _))
                .OrderBy(item => item.DateText, StringComparer.Ordinal).ToArray();
            var totalBytes = dailyFiles.Sum(item => item.File.Length);
            foreach (var dailyFile in dailyFiles)
            {
                if (totalBytes + entryBytes <= MaximumLogBytes) break;
                if (string.CompareOrdinal(dailyFile.DateText, currentDate) >= 0) continue;
                var oldFileBytes = dailyFile.File.Length;
                dailyFile.File.Delete();
                totalBytes -= oldFileBytes;
            }
            if (totalBytes + entryBytes <= MaximumLogBytes) return true;
            // 当天单独达到上限时暂停写入，保留当天复现证据，避免无限增长。
            Trace.TraceWarning("CKBusbarBridge 本地测试日志当日容量已达 200 MB，跳过本条记录。");
            return false;
        }
    }
}
