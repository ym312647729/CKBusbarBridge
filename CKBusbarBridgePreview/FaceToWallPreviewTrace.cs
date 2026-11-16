using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace CKBusbarBridgePreview
{
    /// <summary>
    /// 面对墙模型解析与铜排预览的独立诊断日志。
    /// 不依赖宿主 NLog 配置，确保 SolidWorks 内发生的 COM 异常能够写入插件目录。
    /// </summary>
    public static class FaceToWallPreviewTrace
    {
        // 开启预览诊断日志写盘，便于定位面对面/面对墙预览失败的具体阶段和异常堆栈。
        private const bool EnablePreviewDiagnosticLogWriting = true;
        private static readonly object PreviewLogWriteLock = new object();
        private static readonly Encoding PreviewLogEncoding = new UTF8Encoding(false);
        private static readonly string PreviewLogFilePath = ResolvePreviewLogFilePath();

        public static string LogFilePath => PreviewLogFilePath;

        public static void Info(string stageName, string detailMessage)
        {
            Write("INFO", stageName, detailMessage, null);
        }

        public static void Warning(string stageName, string detailMessage)
        {
            Write("WARN", stageName, detailMessage, null);
        }

        public static void Error(
            string stageName,
            Exception previewException,
            string detailMessage = null)
        {
            Write("ERROR", stageName, detailMessage, previewException);
        }

        /// <summary>
        /// 记录场景识别失败，并保留异常堆栈。
        /// </summary>
        public static void RecognitionError(
            string stageName,
            Exception recognitionException,
            string detailMessage = null)
        {
            Write(
                "RECOGNITION_ERROR",
                stageName,
                detailMessage,
                recognitionException,
                forceWrite: true);
        }

        private static void Write(
            string levelName,
            string stageName,
            string detailMessage,
            Exception previewException,
            bool forceWrite = false)
        {
            if (!forceWrite && !EnablePreviewDiagnosticLogWriting)
            {
                return;
            }

            try
            {
                var logDirectoryPath = Path.GetDirectoryName(PreviewLogFilePath);
                if (!string.IsNullOrWhiteSpace(logDirectoryPath))
                {
                    Directory.CreateDirectory(logDirectoryPath);
                }

                var logBuilder = new StringBuilder();
                logBuilder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                logBuilder.Append(" | ");
                logBuilder.Append(levelName ?? "INFO");
                logBuilder.Append(" | ");
                logBuilder.Append(string.IsNullOrWhiteSpace(stageName) ? "未命名阶段" : stageName);
                if (!string.IsNullOrWhiteSpace(detailMessage))
                {
                    logBuilder.Append(" | ");
                    logBuilder.Append(detailMessage);
                }

                if (previewException != null)
                {
                    logBuilder.AppendLine();
                    logBuilder.Append(previewException);
                }

                logBuilder.AppendLine();
                var completeLogText = logBuilder.ToString();
                lock (PreviewLogWriteLock)
                {
                    File.AppendAllText(PreviewLogFilePath, completeLogText, PreviewLogEncoding);
                }

                Debug.Write(completeLogText);
            }
            catch
            {
                // 诊断日志不得影响 SolidWorks 预览主流程。
            }
        }

        private static string ResolvePreviewLogFilePath()
        {
            try
            {
                var previewAssemblyDirectory = Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location);
                if (!string.IsNullOrWhiteSpace(previewAssemblyDirectory))
                {
                    return Path.Combine(
                        previewAssemblyDirectory,
                        "logs",
                        "face-to-wall-preview-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                }
            }
            catch
            {
                // 回退到临时目录。
            }

            return Path.Combine(
                Path.GetTempPath(),
                "CKBusbarBridge",
                "face-to-wall-preview-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
        }
    }
}
