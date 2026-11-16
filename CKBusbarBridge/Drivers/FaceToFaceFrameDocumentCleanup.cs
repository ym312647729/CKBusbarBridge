using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace CKBusbarBridge.Drivers
{
    internal sealed class FaceToFaceFrameCleanupConfirmation
    {
        private FaceToFaceFrameCleanupConfirmation(bool isConfirmed, string diagnostic)
        {
            IsConfirmed = isConfirmed;
            Diagnostic = diagnostic ?? string.Empty;
        }

        internal bool IsConfirmed { get; }

        internal string Diagnostic { get; }

        internal static FaceToFaceFrameCleanupConfirmation Confirmed()
        {
            return new FaceToFaceFrameCleanupConfirmation(true, string.Empty);
        }

        internal static FaceToFaceFrameCleanupConfirmation Unconfirmed(string diagnostic)
        {
            return new FaceToFaceFrameCleanupConfirmation(
                false,
                string.IsNullOrWhiteSpace(diagnostic)
                    ? "文档清理未得到确认。"
                    : diagnostic);
        }
    }

    internal static class FaceToFaceFrameCleanupDiagnostics
    {
        internal const string CleanupConfirmedDataKey
            = "FaceToFaceFrameGenerationService.CleanupConfirmed";
        internal const string DocumentCleanupWarningDataKey
            = "FaceToFaceFrameGenerationService.DocumentCleanupWarning";
        internal const string RetainedPathDataKey
            = "FaceToFaceFrameGenerationService.RetainedPath";
        internal const string RollbackWarningDataKey
            = "FaceToFaceFrameGenerationService.RollbackWarning";
        internal const string UnconfirmedCleanupWarningDataKey
            = "FaceToFaceFrameGenerationService.UnconfirmedCleanupWarning";
        internal const string GeneratedDirectoryCleanupWarningDataKey
            = "FaceToFaceFrameGenerationService.GeneratedDirectoryCleanupWarning";
        internal const string EmptyRootRestoreWarningDataKey
            = "FaceToFaceFrameGenerationService.EmptyRootRestoreWarning";
        internal const string TotalAssemblyRestoreWarningDataKey
            = "FaceToFaceFrameGenerationService.TotalAssemblyRestoreWarning";

        internal static Exception AttachCleanupConfirmation(
            Exception primaryException,
            FaceToFaceFrameCleanupConfirmation confirmation,
            string retainedPath,
            string context)
        {
            if (primaryException == null)
            {
                throw new ArgumentNullException(nameof(primaryException));
            }

            confirmation = confirmation
                ?? FaceToFaceFrameCleanupConfirmation.Unconfirmed(
                    "未返回文档清理确认。" );
            primaryException.Data[CleanupConfirmedDataKey] = confirmation.IsConfirmed;
            if (!confirmation.IsConfirmed)
            {
                AppendData(
                    primaryException,
                    DocumentCleanupWarningDataKey,
                    (string.IsNullOrWhiteSpace(context) ? "文档清理" : context)
                        + "未得到确认："
                        + confirmation.Diagnostic);
                AppendRetainedPath(primaryException, retainedPath);
            }

            return primaryException;
        }

        internal static bool TryGetCleanupConfirmation(
            Exception exception,
            out bool isConfirmed)
        {
            isConfirmed = false;
            if (exception == null
                || !exception.Data.Contains(CleanupConfirmedDataKey)
                || !(exception.Data[CleanupConfirmedDataKey] is bool value))
            {
                return false;
            }

            isConfirmed = value;
            return true;
        }

        internal static void AppendData(Exception exception, string key, string value)
        {
            if (exception == null
                || string.IsNullOrWhiteSpace(key)
                || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var existing = exception.Data[key] as string;
            if (string.IsNullOrWhiteSpace(existing))
            {
                exception.Data[key] = value;
            }
            else if (existing.IndexOf(value, StringComparison.Ordinal) < 0)
            {
                exception.Data[key] = existing + System.Environment.NewLine + value;
            }
        }

        internal static void AppendRetainedPath(Exception exception, string retainedPath)
        {
            if (string.IsNullOrWhiteSpace(retainedPath))
            {
                return;
            }

            string fullPath;
            try { fullPath = Path.GetFullPath(retainedPath); }
            catch { fullPath = retainedPath; }
            AppendData(exception, RetainedPathDataKey, fullPath);
        }

        internal static string FormatOperatorMessage(Exception exception)
        {
            if (exception == null)
            {
                return string.Empty;
            }

            var lines = new List<string> { exception.Message };
            var keys = new[]
            {
                DocumentCleanupWarningDataKey,
                RollbackWarningDataKey,
                UnconfirmedCleanupWarningDataKey,
                GeneratedDirectoryCleanupWarningDataKey,
                EmptyRootRestoreWarningDataKey,
                TotalAssemblyRestoreWarningDataKey,
                RetainedPathDataKey
            };
            foreach (var key in keys)
            {
                var value = exception.Data[key] as string;
                if (!string.IsNullOrWhiteSpace(value)
                    && !lines.Contains(value, StringComparer.Ordinal))
                {
                    lines.Add(value);
                }
            }

            return string.Join(System.Environment.NewLine, lines);
        }

        internal static void LogOperatorMessage(Exception exception)
        {
            Debug.Print("面对面基架生成异常：{0}", FormatOperatorMessage(exception));
        }
    }

    internal interface IFaceToFaceFrameOpenDocument
    {
        string FilePath { get; }

        bool IsModified { get; }

        bool TrySave();

        void Close();
    }

    internal interface IFaceToFaceFrameDocumentSession
    {
        IReadOnlyList<IFaceToFaceFrameOpenDocument> GetOpenDocuments();
    }

    internal sealed class FaceToFaceFrameScopedDocumentCleaner
    {
        private const int MaximumClosePasses = 4;
        private const int CloseConfirmationDelayMilliseconds = 35;
        private readonly IFaceToFaceFrameDocumentSession _session;

        internal FaceToFaceFrameScopedDocumentCleaner(
            IFaceToFaceFrameDocumentSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        internal FaceToFaceFrameCleanupConfirmation CloseDocumentsUnderDirectory(
            string directoryPath)
        {
            return CloseDocumentsUnderDirectory(directoryPath, requireUnloadConfirmation: true);
        }

        internal FaceToFaceFrameCleanupConfirmation RequestCloseDocumentsUnderDirectory(
            string directoryPath)
        {
            return CloseDocumentsUnderDirectory(directoryPath, requireUnloadConfirmation: false);
        }

        private FaceToFaceFrameCleanupConfirmation CloseDocumentsUnderDirectory(
            string directoryPath,
            bool requireUnloadConfirmation)
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                return FaceToFaceFrameCleanupConfirmation.Unconfirmed(
                    "待确认目录为空。" );
            }

            string normalizedRoot;
            try
            {
                normalizedRoot = Path.GetFullPath(directoryPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
            }
            catch (Exception exception)
            {
                return FaceToFaceFrameCleanupConfirmation.Unconfirmed(
                    "无法规范化待确认目录：" + exception.Message);
            }

            var diagnostics = new List<string>();
            var closePasses = requireUnloadConfirmation ? MaximumClosePasses : 1;
            for (var pass = 0; pass < closePasses; pass++)
            {
                IReadOnlyList<IFaceToFaceFrameOpenDocument> documents;
                try
                {
                    documents = _session.GetOpenDocuments();
                }
                catch (Exception exception)
                {
                    diagnostics.Add(
                        $"枚举 SolidWorks 文档失败；目录={normalizedRoot}；{exception.Message}");
                    break;
                }

                if (documents == null)
                {
                    diagnostics.Add(
                        $"SolidWorks 文档枚举未返回结果；目录={normalizedRoot}");
                    break;
                }

                var current = documents
                    .Select(document => new { Document = document, Path = SafeGetPath(document) })
                    .Where(item => IsUnderDirectory(item.Path, normalizedRoot))
                    .OrderByDescending(item => item.Path.Length)
                    .ToArray();
                if (current.Length == 0)
                {
                    break;
                }

                var closedAny = false;
                foreach (var item in current)
                {
                    closedAny |= TrySaveAndCloseCurrentDocument(
                        item.Document,
                        item.Path,
                        normalizedRoot,
                        diagnostics);
                }

                // CloseDoc 可能连带卸载被父装配体引用的子件，使其余 COM 包装对象立即失效。
                // 因此每次关闭后都丢弃本轮快照，下一轮从 SolidWorks 重新获取当前文档。
                if (!closedAny)
                {
                    System.Threading.Thread.Sleep(CloseConfirmationDelayMilliseconds);
                }
            }

            if (!requireUnloadConfirmation)
            {
                return diagnostics.Count == 0
                    ? FaceToFaceFrameCleanupConfirmation.Confirmed()
                    : FaceToFaceFrameCleanupConfirmation.Unconfirmed(
                        string.Join("；", diagnostics));
            }

            IReadOnlyList<IFaceToFaceFrameOpenDocument> remainingDocuments;
            try
            {
                remainingDocuments = _session.GetOpenDocuments();
            }
            catch (Exception exception)
            {
                return FaceToFaceFrameCleanupConfirmation.Unconfirmed(
                    string.Join("；", diagnostics.Concat(new[]
                    {
                        $"复核 SolidWorks 文档失败；目录={normalizedRoot}；{exception.Message}"
                    })));
            }

            if (remainingDocuments == null)
            {
                diagnostics.Add($"复核 SolidWorks 文档未返回结果；目录={normalizedRoot}");
            }
            else
            {
                var remainingPaths = remainingDocuments
                    .Select(SafeGetPath)
                    .Where(path => IsUnderDirectory(path, normalizedRoot))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (remainingPaths.Length > 0)
                {
                    diagnostics.Add(
                        "仍有文档加载在待清理目录下：" + string.Join("、", remainingPaths));
                }

                // 本流程的目标文档均从确定的磁盘路径打开。无路径文档通常是用户尚未
                // 保存的新建模型，与目标目录没有可证明的归属关系，不能让它全局阻断生成。
            }

            return diagnostics.Count == 0
                ? FaceToFaceFrameCleanupConfirmation.Confirmed()
                : FaceToFaceFrameCleanupConfirmation.Unconfirmed(
                    string.Join("；", diagnostics));
        }

        private bool TrySaveAndCloseCurrentDocument(
            IFaceToFaceFrameOpenDocument document,
            string documentPath,
            string normalizedRoot,
            ICollection<string> diagnostics)
        {
            try
            {
                if (document.IsModified && !document.TrySave())
                {
                    diagnostics.Add("保存路径范围内文档失败：" + documentPath);
                    return false;
                }
            }
            catch (Exception exception)
            {
                if (IsDisconnected(exception))
                {
                    if (IsDocumentNoLongerOpen(
                        documentPath,
                        normalizedRoot,
                        out var recheckDiagnostic))
                    {
                        return true;
                    }

                    if (!string.IsNullOrWhiteSpace(recheckDiagnostic))
                    {
                        diagnostics.Add(recheckDiagnostic);
                    }
                }

                diagnostics.Add(
                    $"保存路径范围内文档异常：{documentPath}；{exception.Message}");
                return false;
            }

            try
            {
                document.Close();
                System.Threading.Thread.Sleep(CloseConfirmationDelayMilliseconds);
                return IsDocumentNoLongerOpen(documentPath, normalizedRoot, out _);
            }
            catch (Exception exception)
            {
                if (IsDisconnected(exception))
                {
                    if (IsDocumentNoLongerOpen(
                        documentPath,
                        normalizedRoot,
                        out var recheckDiagnostic))
                    {
                        return true;
                    }

                    if (!string.IsNullOrWhiteSpace(recheckDiagnostic))
                    {
                        diagnostics.Add(recheckDiagnostic);
                    }
                }

                diagnostics.Add(
                    $"关闭路径范围内文档异常：{documentPath}；{exception.Message}");
                return false;
            }
        }

        private static bool IsDisconnected(Exception exception)
        {
            return exception is COMException
                && exception.HResult == unchecked((int)0x80010108);
        }

        private bool IsDocumentNoLongerOpen(
            string documentPath,
            string normalizedRoot,
            out string diagnostic)
        {
            diagnostic = null;
            IReadOnlyList<IFaceToFaceFrameOpenDocument> currentDocuments;
            try
            {
                currentDocuments = _session.GetOpenDocuments();
            }
            catch (Exception exception)
            {
                diagnostic = "复核断开文档失败：" + exception.Message;
                return false;
            }

            if (currentDocuments == null)
            {
                diagnostic = "复核断开文档未返回文档集合。";
                return false;
            }

            var currentPaths = currentDocuments.Select(SafeGetPath).ToArray();
            if (currentPaths.Any(string.IsNullOrWhiteSpace))
            {
                diagnostic = $"复核断开文档时仍存在无法读取路径的文档：{normalizedRoot}";
                return false;
            }

            return !currentPaths.Any(path => PathsEqual(path, documentPath));
        }

        private static bool PathsEqual(string left, string right)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(left),
                    Path.GetFullPath(right),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool IsUnderDirectory(string filePath, string normalizedRoot)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return false;
            }

            string normalizedPath;
            try { normalizedPath = Path.GetFullPath(filePath); }
            catch { return false; }
            return normalizedPath.StartsWith(
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeGetPath(IFaceToFaceFrameOpenDocument document)
        {
            if (document == null)
            {
                return null;
            }

            try { return document.FilePath; }
            catch { return null; }
        }
    }

    internal sealed class FaceToFaceFrameDocumentCloseTarget
    {
        internal string FilePath { get; }
        internal string Title { get; }
        internal bool IsAssembly { get; }

        internal FaceToFaceFrameDocumentCloseTarget(string filePath, string title, bool isAssembly)
        {
            FilePath = Path.GetFullPath(filePath);
            Title = title;
            IsAssembly = isAssembly;
        }
    }

    /// <summary>按总装的确切引用路径释放文档；不保存，供正常关闭及文件回滚共用。</summary>
    internal sealed class FaceToFaceFrameReferencedDocumentCloser
    {
        private const int MaximumPasses = 8;
        private readonly Func<string, FaceToFaceFrameDocumentCloseTarget> _findOpenDocument;
        private readonly Action<string> _closeDocument;

        internal FaceToFaceFrameReferencedDocumentCloser(
            Func<string, FaceToFaceFrameDocumentCloseTarget> findOpenDocument,
            Action<string> closeDocument)
        {
            _findOpenDocument = findOpenDocument ?? throw new ArgumentNullException(nameof(findOpenDocument));
            _closeDocument = closeDocument ?? throw new ArgumentNullException(nameof(closeDocument));
        }

        internal void Close(IReadOnlyCollection<string> referencedPaths, ISet<string> mayRemainLoaded = null)
        {
            if (referencedPaths == null) throw new ArgumentNullException(nameof(referencedPaths));
            var paths = referencedPaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            var requiredPaths = mayRemainLoaded == null ? paths : paths.Where(path => !mayRemainLoaded.Contains(path)).ToArray();
            for (var pass = 0; pass < MaximumPasses; pass++)
            {
                // 只缓存字符串身份；关闭父装配体可能连带卸载同一轮内的其他文档。
                // 允许驻留的铜排只请求关闭一次。仍被引用时，CloseDoc 只释放其窗口。
                var targets = ReadOpenTargets(pass == 0 ? paths : requiredPaths).OrderBy(target => target.IsAssembly ? 0 : 1)
                    .ThenBy(target => target.FilePath, StringComparer.OrdinalIgnoreCase).ToArray();
                if (targets.Length == 0) return;
                foreach (var target in targets) CloseCurrent(target);
            }

            var remaining = ReadOpenTargets(requiredPaths);
            if (remaining.Count > 0)
                throw new InvalidOperationException("关闭总装配体后仍有其关联文档驻留在 SolidWorks 中："
                    + string.Join("；", remaining.Select(target => target.FilePath)));
        }

        private IReadOnlyList<FaceToFaceFrameDocumentCloseTarget> ReadOpenTargets(IEnumerable<string> paths)
        {
            return paths.Select(path => FindCurrent(path, null)).Where(target => target != null).ToArray();
        }

        private FaceToFaceFrameDocumentCloseTarget FindCurrent(string path, string previousTitle)
        {
            FaceToFaceFrameDocumentCloseTarget current;
            try { current = _findOpenDocument(path); }
            catch (COMException error)
            {
                if (IsDisconnected(error) && ConfirmAbsent(path, previousTitle, error)) return null;
                throw CloseFailure("读取引用文档身份失败", path, previousTitle, error);
            }

            if (current != null && !string.Equals(current.FilePath, path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("按路径查找的引用文档身份不符：目标=" + path
                    + "；实际=" + current.FilePath);
            return current;
        }

        private void CloseCurrent(FaceToFaceFrameDocumentCloseTarget previous)
        {
            // 每次 CloseDoc 前重新获取当前文档，绝不继续使用上一轮 COM 包装对象。
            var current = FindCurrent(previous.FilePath, previous.Title);
            if (current == null) return;
            if (string.IsNullOrWhiteSpace(current.Title))
                throw new InvalidOperationException("待关闭引用文档缺少标题：" + current.FilePath);
            var detail = "路径=" + current.FilePath + "；标题=" + current.Title
                + "；类型=" + (current.IsAssembly ? "装配体" : "零件");
            CKBusbarBridgePreview.FaceToWallPreviewTrace.Info("关闭总装引用文档", detail);
            try
            {
                _closeDocument(current.Title);
                CKBusbarBridgePreview.FaceToWallPreviewTrace.Info("关闭总装引用文档返回", detail);
            }
            catch (COMException error)
            {
                if (IsDisconnected(error) && ConfirmAbsent(current.FilePath, current.Title, error)) return;
                throw CloseFailure("关闭引用文档失败", current.FilePath, current.Title, error);
            }
        }

        private bool ConfirmAbsent(string path, string title, COMException original)
        {
            FaceToFaceFrameDocumentCloseTarget current;
            try { current = _findOpenDocument(path); }
            catch (COMException recheckError)
            {
                CKBusbarBridgePreview.FaceToWallPreviewTrace.Error("断连引用文档复核失败", recheckError, "路径=" + path);
                throw CloseFailure("引用文档断连且无法确认关闭状态", path, title, original);
            }

            if (current != null) return false;
            // CloseDoc 可能已完成卸载后才报告断连；仅在新查询确认路径不存在时接纳。
            CKBusbarBridgePreview.FaceToWallPreviewTrace.Warning("断连引用文档已确认关闭",
                "路径=" + path + "；标题=" + title + System.Environment.NewLine + original);
            return true;
        }

        private static bool IsDisconnected(COMException error)
        {
            return error.ErrorCode == unchecked((int)0x80010108);
        }

        private static InvalidOperationException CloseFailure(string stage, string path, string title, COMException error)
        {
            var detail = "路径=" + path + "；标题=" + (title ?? "未取得");
            CKBusbarBridgePreview.FaceToWallPreviewTrace.Error(stage, error, detail);
            return new InvalidOperationException(stage + "：" + detail, error);
        }
    }

    internal sealed class SolidWorksFaceToFaceFrameDocumentSession
        : IFaceToFaceFrameDocumentSession
    {
        private readonly ISldWorks _swApp;

        internal SolidWorksFaceToFaceFrameDocumentSession(ISldWorks swApp)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
        }

        public IReadOnlyList<IFaceToFaceFrameOpenDocument> GetOpenDocuments()
        {
            var rawDocuments = _swApp.GetDocuments();
            if (rawDocuments == null)
            {
                return new IFaceToFaceFrameOpenDocument[0];
            }

            var documents = rawDocuments as object[];
            if (documents == null)
            {
                throw new InvalidOperationException(
                    "SolidWorks GetDocuments 未返回文档数组。" );
            }

            return documents
                .OfType<IModelDoc2>()
                .Select(document => (IFaceToFaceFrameOpenDocument)
                    new SolidWorksOpenDocument(_swApp, document))
                .ToArray();
        }

        private sealed class SolidWorksOpenDocument : IFaceToFaceFrameOpenDocument
        {
            private readonly ISldWorks _swApp;
            private readonly IModelDoc2 _document;

            internal SolidWorksOpenDocument(ISldWorks swApp, IModelDoc2 document)
            {
                _swApp = swApp;
                _document = document;
            }

            public string FilePath => _document.GetPathName();

            public bool IsModified => _document.GetSaveFlag();

            public bool TrySave()
            {
                var errors = 0;
                var warnings = 0;
                var saved = _document.Save3(
                    (int)swSaveAsOptions_e.swSaveAsOptions_Silent,
                    ref errors,
                    ref warnings);
                return saved && errors == 0;
            }

            public void Close()
            {
                var title = _document.GetTitle();
                if (string.IsNullOrWhiteSpace(title))
                {
                    throw new InvalidOperationException(
                        "待关闭 SolidWorks 文档缺少标题。" );
                }

                _swApp.CloseDoc(title);
            }
        }
    }
}
