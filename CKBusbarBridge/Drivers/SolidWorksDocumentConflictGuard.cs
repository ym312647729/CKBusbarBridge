using SolidWorks.Interop.sldworks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NLog;

namespace CKBusbarBridge.Drivers
{
    internal interface ISolidWorksDocumentConflictSession
    {
        IReadOnlyList<SolidWorksOpenDocumentSnapshot> ReadDocuments();
        void CloseDocument(SolidWorksOpenDocumentSnapshot document, bool discardChanges);
        IReadOnlyList<SolidWorksDocumentCloseState> ReadCloseStates(IReadOnlyCollection<string> paths);
        void PrepareForSecondClose(SolidWorksDocumentCloseState state);
        void WaitForCloseSettlement();
    }

    internal sealed class SolidWorksOpenDocumentSnapshot
    {
        internal SolidWorksOpenDocumentSnapshot(string path, string title, bool hasUnsavedChanges, bool visible = true)
        {
            FullPath = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path);
            Title = title;
            HasUnsavedChanges = hasUnsavedChanges;
            Visible = visible;
        }

        internal string FullPath { get; }
        internal string Title { get; }
        internal bool HasUnsavedChanges { get; }
        internal bool Visible { get; }
    }

    /// <summary>
    /// 打开和保存共用同名文档清理。只保留路径快照，关闭后重新枚举，不复用旧 COM 文档缓存。
    /// </summary>
    internal sealed partial class SolidWorksDocumentConflictGuard
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly ISolidWorksDocumentConflictSession _session;

        internal SolidWorksDocumentConflictGuard(ISldWorks application)
            : this(new DocumentSession(application))
        {
        }

        internal SolidWorksDocumentConflictGuard(ISolidWorksDocumentConflictSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
        }

        internal void PrepareForOpen(string path)
        {
            CloseConflicts(NormalizePaths(new[] { path }), NormalizePaths(new[] { path }));
        }

        internal void CloseSavedDocuments(IEnumerable<string> paths, Action<string> saveBeforeClose, bool requireUnload = false,
            IReadOnlyCollection<string> excludedPaths = null)
        {
            if (saveBeforeClose == null) throw new ArgumentNullException(nameof(saveBeforeClose));
            var selected = NormalizePaths(paths);
            var keepOpen = NormalizePaths(excludedPaths);
            if (selected.Overlaps(keepOpen)) throw new ArgumentException("待关闭文档与保留文档范围冲突。");
            CloseWithRecovery(document => selected.Contains(document.FullPath ?? string.Empty),
                keepOpen, discardChanges: false, requireUnload: requireUnload, saveBeforeClose: saveBeforeClose);
        }

        internal IReadOnlyCollection<string> CaptureOpenPaths()
        {
            return ReadWithBusyRecheck(() => _session.ReadDocuments(), "读取初始文档清单")
                .Where(document => !string.IsNullOrWhiteSpace(document.FullPath))
                .Select(document => document.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        internal void DiscardNewDocuments(string directory, IReadOnlyCollection<string> previouslyOpenPaths)
        {
            if (previouslyOpenPaths == null) throw new ArgumentNullException(nameof(previouslyOpenPaths));
            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var previous = NormalizePaths(previouslyOpenPaths);
            CloseMatchingDocuments(document => !string.IsNullOrWhiteSpace(document.FullPath)
                && document.FullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && !previous.Contains(document.FullPath), previous, discardChanges: true);
        }

        internal void DiscardOpenedDocuments(IEnumerable<string> paths, IReadOnlyCollection<string> previouslyOpenPaths)
        {
            if (previouslyOpenPaths == null) throw new ArgumentNullException(nameof(previouslyOpenPaths));
            var owned = NormalizePaths(paths);
            var previous = NormalizePaths(previouslyOpenPaths);
            CloseMatchingDocuments(document => owned.Contains(document.FullPath ?? string.Empty)
                && !previous.Contains(document.FullPath ?? string.Empty), previous, discardChanges: true);
        }

        /// <summary>PackAndGo 写入前关闭全部输出的旧文档，源模型必须保持打开以供打包。</summary>
        internal void PrepareForSave(IEnumerable<string> outputPaths, IEnumerable<string> sourcePaths)
        {
            var outputs = NormalizePaths(outputPaths);
            var sources = NormalizePaths(sourcePaths);
            var overwrittenSources = outputs.Intersect(sources, StringComparer.OrdinalIgnoreCase).ToArray();
            if (overwrittenSources.Length > 0)
            {
                throw new InvalidOperationException(
                    "保存目标与源模型路径相同，已停止覆盖源模型：" + string.Join("；", overwrittenSources));
            }

            CloseConflicts(outputs, sources);
        }

        /// <summary>原位保存只检查当前文档的同名冲突，不展开子件。</summary>
        internal void PrepareForSave(IModelDoc2 model)
        {
            if (model == null) throw new ArgumentNullException(nameof(model));
            var modelPaths = NormalizePaths(new[] { model.GetPathName() });
            CloseConflicts(modelPaths, modelPaths);
        }

        private void CloseConflicts(HashSet<string> targets, HashSet<string> keepOpenPaths)
        {
            if (targets.Count == 0) throw new ArgumentException("待打开或保存的模型路径不能为空。");
            var targetNames = new HashSet<string>(targets.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            CloseMatchingDocuments(document => !keepOpenPaths.Contains(document.FullPath ?? string.Empty)
                && MatchesName(document, targetNames), keepOpenPaths, discardChanges: false);
        }

        private void CloseMatchingDocuments(Func<SolidWorksOpenDocumentSnapshot, bool> matches,
            HashSet<string> keepOpenPaths, bool discardChanges)
        {
            CloseWithRecovery(matches, keepOpenPaths, discardChanges);
        }

        private static bool MatchesName(SolidWorksOpenDocumentSnapshot document, HashSet<string> targetNames)
        {
            return !string.IsNullOrWhiteSpace(document.FullPath)
                ? targetNames.Contains(Path.GetFileName(document.FullPath))
                : targetNames.Any(name => string.Equals(name, document.Title, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileNameWithoutExtension(name), document.Title, StringComparison.OrdinalIgnoreCase));
        }

        private static void ValidateConflicts(
            IReadOnlyList<SolidWorksOpenDocumentSnapshot> conflicts,
            IReadOnlyList<SolidWorksOpenDocumentSnapshot> documents,
            bool discardChanges)
        {
            foreach (var conflict in conflicts)
            {
                if (string.IsNullOrWhiteSpace(conflict.FullPath) || string.IsNullOrWhiteSpace(conflict.Title))
                    throw new InvalidOperationException("同名冲突文档无法确认路径或标题，未关闭该文档：" + conflict.Title);
                if (conflict.HasUnsavedChanges && !discardChanges)
                    throw new InvalidOperationException("同名冲突文档存在未保存修改，未关闭该文档：" + conflict.FullPath);
                if (documents.Any(document => !ReferenceEquals(document, conflict)
                    && !string.Equals(document.FullPath, conflict.FullPath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(document.Title, conflict.Title, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("同名冲突文档与当前源模型标题相同，无法安全关闭：" + conflict.FullPath);
            }
        }

        private static HashSet<string> NormalizePaths(IEnumerable<string> paths)
        {
            return new HashSet<string>((paths ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        }

        private static int ClosePriority(string path)
        {
            var extension = Path.GetExtension(path);
            if (string.Equals(extension, ".SLDDRW", StringComparison.OrdinalIgnoreCase)) return 0;
            return string.Equals(extension, ".SLDASM", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
        }

        private sealed partial class DocumentSession : ISolidWorksDocumentConflictSession
        {
            private readonly ISldWorks _application;

            internal DocumentSession(ISldWorks application)
            {
                _application = application ?? throw new ArgumentNullException(nameof(application));
            }

            public IReadOnlyList<SolidWorksOpenDocumentSnapshot> ReadDocuments()
            {
                var raw = _application.GetDocuments();
                var documents = raw as object[];
                var result = new List<SolidWorksOpenDocumentSnapshot>();
                if (raw == null) return result;
                if (documents == null) throw new InvalidOperationException("SolidWorks 已加载文档清单格式无效，不能确认缓存已清理。");
                try
                {
                    foreach (var item in documents)
                    {
                        var document = item as IModelDoc2;
                        if (document == null) throw new InvalidOperationException("SolidWorks 文档快照包含未知对象，已停止保存。");
                        result.Add(new SolidWorksOpenDocumentSnapshot(
                            document.GetPathName(), document.GetTitle(), document.GetSaveFlag(), document.Visible));
                    }
                    return result;
                }
                finally
                {
                    // 仅释放本次枚举取得的一层引用；快照不持有 RCW，也不释放调用方的模型句柄。
                    foreach (var document in documents)
                        SolidWorksFileHelper.ReleaseComReference(document);
                }
            }

            public void CloseDocument(SolidWorksOpenDocumentSnapshot document, bool discardChanges)
            {
                var documents = ReadNativeDocuments();
                try
                {
                    var model = documents.OfType<IModelDoc2>().SingleOrDefault(candidate =>
                        string.Equals(candidate.GetPathName(), document.FullPath, StringComparison.OrdinalIgnoreCase));
                    if (model == null) return;
                    if (!discardChanges && model.GetSaveFlag())
                        throw new InvalidOperationException("关闭前文档出现未保存修改，已保留文档：" + document.FullPath);
                    var title = model.GetTitle();
                    if (string.IsNullOrWhiteSpace(title) || documents.OfType<IModelDoc2>().Any(candidate =>
                        !string.Equals(candidate.GetPathName(), document.FullPath, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(candidate.GetTitle(), title, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("关闭前无法唯一定位文档，未关闭其他同名模型：" + document.FullPath);
                    _application.CloseDoc(title);
                }
                finally { foreach (var item in documents) SolidWorksFileHelper.ReleaseComReference(item); }
            }
        }
    }
}
