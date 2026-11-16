using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using CKBusbarBridgePreview;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace CKBusbarBridge.Drivers
{
    internal sealed class SolidWorksDocumentCloseState
    {
        internal SolidWorksDocumentCloseState(string path, IEnumerable<string> referencedBy = null,
            bool editingSketch = false, bool editingComponent = false)
        {
            FullPath = Path.GetFullPath(path);
            ReferencedBy = (referencedBy ?? Enumerable.Empty<string>()).Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            EditingSketch = editingSketch;
            EditingComponent = editingComponent;
        }

        internal string FullPath { get; }
        internal string[] ReferencedBy { get; }
        internal bool EditingSketch { get; }
        internal bool EditingComponent { get; }
        internal string Description => "文件=" + FullPath + "；草图编辑=" + EditingSketch
            + "；组件编辑=" + EditingComponent + "；引用方=" + string.Join("；", ReferencedBy);
    }

    internal sealed partial class SolidWorksDocumentConflictGuard
    {
        private void CloseWithRecovery(Func<SolidWorksOpenDocumentSnapshot, bool> matches,
            HashSet<string> keepOpenPaths, bool discardChanges, bool requireUnload = true,
            Action<string> saveBeforeClose = null)
        {
            var failures = new List<Exception>();
            var documents = ReadWithBusyRecheck(() => _session.ReadDocuments(), failures, "读取初始关闭文档清单");
            var protectedPaths = documents.Where(document => keepOpenPaths.Contains(document.FullPath ?? string.Empty))
                .Select(document => document.FullPath).ToArray();
            var targets = documents.Where(matches).ToArray();
            if (targets.Length == 0) return;
            var selectedPaths = NormalizePaths(targets.Select(document => document.FullPath));
            var pendingSaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ValidateConflicts(targets, documents, discardChanges || saveBeforeClose != null);
            var ordered = targets.OrderBy(document => ClosePriority(document.FullPath))
                .ThenBy(document => document.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
            CloseRound(ordered, selectedPaths, discardChanges, saveBeforeClose, pendingSaves, failures, 1);
            documents = ReadAfterClose(protectedPaths, failures);
            var remaining = Remaining(documents, matches, requireUnload, pendingSaves, failures);
            if (remaining.Length == 0) return;

            var states = Diagnose(remaining, selectedPaths, failures, requireUnload);
            ValidateConflicts(remaining, documents, discardChanges || saveBeforeClose != null);
            var secondOrder = OrderSecondClose(states);
            foreach (var state in secondOrder)
            {
                Logger.Warn("首次关闭后仍驻留，处理原因并准备第二次关闭：{0}", state.Description);
                FaceToWallPreviewTrace.Warning("文档第二次关闭准备", state.Description);
                try { _session.PrepareForSecondClose(state); }
                catch (Exception exception)
                {
                    failures.Add(exception);
                    throw CloseFailure("关闭失败原因处理未完成：" + state.Description, failures);
                }
            }

            _session.WaitForCloseSettlement();
            // 处理编辑状态可能改变标题或脏标记；第二次关闭必须使用新快照，重新保护未保存修改。
            documents = ReadAfterClose(protectedPaths, failures);
            remaining = Remaining(documents, matches, requireUnload, pendingSaves, failures);
            EnsureSelectedPaths(remaining, selectedPaths);
            ValidateConflicts(remaining, documents, discardChanges || saveBeforeClose != null);
            var secondDocuments = secondOrder.Select(state => remaining.SingleOrDefault(document =>
                string.Equals(document.FullPath, state.FullPath, StringComparison.OrdinalIgnoreCase)))
                .Where(document => document != null).ToArray();
            CloseRound(secondDocuments, selectedPaths, discardChanges, saveBeforeClose, pendingSaves, failures, 2);
            documents = ReadAfterClose(protectedPaths, failures);
            remaining = Remaining(documents, matches, requireUnload, pendingSaves, failures);
            if (remaining.Length == 0) return;
            _session.WaitForCloseSettlement();
            remaining = Remaining(ReadAfterClose(protectedPaths, failures), matches, requireUnload, pendingSaves, failures);
            if (remaining.Length == 0) return;
            var reasons = ReadFailureDescriptions(remaining, failures);
            if (pendingSaves.Count > 0)
                throw CloseFailure("第二次关闭前保存仍失败，已保留文档并停止后续操作：" + reasons, failures);
            throw CloseFailure("SolidWorks 第二次关闭未得到确认，缓存仍被占用，已停止打开或保存：" + reasons, failures);
        }

        private void CloseRound(IEnumerable<SolidWorksOpenDocumentSnapshot> documents, HashSet<string> selectedPaths, bool discardChanges,
            Action<string> saveBeforeClose, HashSet<string> pendingSaves, List<Exception> failures, int attempt)
        {
            foreach (var document in documents)
            {
                if (!TrySaveBeforeClose(document, saveBeforeClose, pendingSaves, failures, attempt)) continue;
                ValidateReferencedChanges(document.FullPath, selectedPaths, discardChanges, failures);
                try
                {
                    Logger.Info("关闭文档：第{0}次；文件={1}；丢弃本次修改={2}。", attempt, document.FullPath, discardChanges);
                    _session.CloseDocument(document, discardChanges);
                }
                catch (Exception exception)
                {
                    RecordCloseFailure(document, failures, attempt, exception);
                }
            }
        }

        private bool TrySaveBeforeClose(SolidWorksOpenDocumentSnapshot document, Action<string> saveBeforeClose,
            HashSet<string> pendingSaves, List<Exception> failures, int attempt)
        {
            if (saveBeforeClose == null) return true;
            try
            {
                pendingSaves.Add(document.FullPath);
                saveBeforeClose(document.FullPath);
                pendingSaves.Remove(document.FullPath);
                return true;
            }
            catch (Exception exception)
            {
                RecordCloseFailure(document, failures, attempt, exception);
                return false;
            }
        }

        private void RecordCloseFailure(SolidWorksOpenDocumentSnapshot document, List<Exception> failures,
            int attempt, Exception exception)
        {
            failures.Add(new InvalidOperationException("第" + attempt + "次保存或关闭文档失败：" + document.FullPath
                + "；HRESULT=0x" + exception.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture), exception));
            Logger.Warn(exception, "第{0}次关闭文档失败：{1}", attempt, document.FullPath);
        }

        private void ValidateReferencedChanges(string targetPath, HashSet<string> selectedPaths,
            bool discardChanges, List<Exception> failures)
        {
            // 保存回调可能改变子文档的脏标记。必须在关闭前读取，不能等父体连带卸载后才发现丢失。
            var documents = ReadWithBusyRecheck(() => _session.ReadDocuments(), failures, "读取关闭前修改状态");
            var dirty = documents.Where(document => document.HasUnsavedChanges
                && !(discardChanges && selectedPaths.Contains(document.FullPath ?? string.Empty))).ToArray();
            if (dirty.Length == 0 || !documents.Any(document =>
                string.Equals(document.FullPath, targetPath, StringComparison.OrdinalIgnoreCase))) return;
            var paths = NormalizePaths(documents.Select(document => document.FullPath));
            var states = ReadWithBusyRecheck(() => _session.ReadCloseStates(paths), failures, "核验关闭引用的未保存修改");
            if (states.Count != paths.Count || paths.Any(path => states.Count(state =>
                string.Equals(state.FullPath, path, StringComparison.OrdinalIgnoreCase)) != 1))
                throw CloseFailure("关闭引用清单缺失或重复，未关闭父文档：" + targetPath, failures);
            var byPath = states.ToDictionary(state => state.FullPath, StringComparer.OrdinalIgnoreCase);
            foreach (var document in dirty)
            {
                if (string.IsNullOrWhiteSpace(document.FullPath)) continue;
                var pending = new Stack<string>(new[] { document.FullPath });
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (pending.Count > 0)
                {
                    var path = pending.Pop();
                    if (!visited.Add(path)) continue;
                    if (string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase))
                        throw CloseFailure("待关闭文档或其实际引用存在未保存修改，已保留父文档及子文档："
                            + targetPath + "；未保存文档=" + document.FullPath, failures);
                    if (byPath.TryGetValue(path, out var state))
                        foreach (var owner in state.ReferencedBy) pending.Push(owner);
                }
            }
        }

        private IReadOnlyList<SolidWorksOpenDocumentSnapshot> ReadAfterClose(string[] protectedPaths, List<Exception> failures)
        {
            var documents = ReadWithBusyRecheck(() => _session.ReadDocuments(), failures, "读取关闭后状态");
            var lost = protectedPaths.Where(path => !documents.Any(document =>
                string.Equals(document.FullPath, path, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (lost.Length > 0)
                throw CloseFailure("关闭冲突文档时当前模型意外卸载，已停止保存：" + string.Join("；", lost), failures);
            return documents;
        }

        private static SolidWorksOpenDocumentSnapshot[] Remaining(IReadOnlyList<SolidWorksOpenDocumentSnapshot> documents,
            Func<SolidWorksOpenDocumentSnapshot, bool> matches, bool requireUnload,
            HashSet<string> pendingSaves, List<Exception> failures)
        {
            var lost = pendingSaves.Where(path => !documents.Any(document =>
                string.Equals(document.FullPath, path, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (lost.Length > 0)
                throw CloseFailure("保存失败的文档已卸载，无法确认修改已保存：" + string.Join("；", lost), failures);
            return documents.Where(document => matches(document)
                && (requireUnload || document.Visible || pendingSaves.Contains(document.FullPath))).ToArray();
        }

        private IReadOnlyList<SolidWorksDocumentCloseState> Diagnose(SolidWorksOpenDocumentSnapshot[] remaining,
            HashSet<string> selectedPaths, List<Exception> failures, bool requireUnload)
        {
            EnsureSelectedPaths(remaining, selectedPaths);
            IReadOnlyList<SolidWorksDocumentCloseState> states;
            try { states = ReadWithBusyRecheck(() => _session.ReadCloseStates(remaining.Select(document => document.FullPath).ToArray()), failures, "读取关闭失败原因"); }
            catch (Exception exception)
            {
                failures.Add(exception);
                throw CloseFailure("无法核验关闭失败原因，未执行盲目重试：" + string.Join("；", remaining.Select(document => document.FullPath)), failures);
            }
            foreach (var document in remaining)
                if (states.Count(state => string.Equals(state.FullPath, document.FullPath, StringComparison.OrdinalIgnoreCase)) != 1)
                    throw CloseFailure("关闭失败诊断缺失或重复：" + document.FullPath, failures);
            if (states.Any(state => !remaining.Any(document => string.Equals(document.FullPath, state.FullPath, StringComparison.OrdinalIgnoreCase))))
                throw CloseFailure("关闭诊断包含范围外文档，未执行处理或第二次关闭。", failures);
            foreach (var state in states)
            {
                var external = state.ReferencedBy.Where(path => !selectedPaths.Contains(path)).ToArray();
                if (requireUnload && external.Length > 0)
                    throw CloseFailure("文档仍被清理范围外的装配体或图纸引用，已保留引用方：" + state.FullPath
                        + "；引用方=" + string.Join("；", external), failures);
            }
            return states;
        }

        private static void EnsureSelectedPaths(IEnumerable<SolidWorksOpenDocumentSnapshot> documents, HashSet<string> selectedPaths)
        {
            var unexpected = documents.Where(document => !selectedPaths.Contains(document.FullPath ?? string.Empty)).ToArray();
            if (unexpected.Length > 0)
                throw new InvalidOperationException("关闭期间出现新的同名文档，未扩大清理范围：" + string.Join("；", unexpected.Select(document => document.FullPath)));
        }

        private static SolidWorksDocumentCloseState[] OrderSecondClose(IReadOnlyList<SolidWorksDocumentCloseState> states)
        {
            var pending = states.ToList();
            var ordered = new List<SolidWorksDocumentCloseState>();
            while (pending.Count > 0)
            {
                var parents = pending.Where(state => !state.ReferencedBy.Any(owner => pending.Any(candidate =>
                    string.Equals(candidate.FullPath, owner, StringComparison.OrdinalIgnoreCase))))
                    .OrderBy(state => ClosePriority(state.FullPath)).ThenBy(state => state.FullPath, StringComparer.OrdinalIgnoreCase).ToArray();
                if (parents.Length == 0)
                    throw new InvalidOperationException("待关闭文档存在循环引用，无法确定安全关闭顺序：" + string.Join("；", pending.Select(state => state.FullPath)));
                ordered.AddRange(parents);
                foreach (var parent in parents) pending.Remove(parent);
            }
            return ordered.ToArray();
        }

        private string ReadFailureDescriptions(SolidWorksOpenDocumentSnapshot[] remaining, List<Exception> failures)
        {
            try { return string.Join("；", _session.ReadCloseStates(remaining.Select(document => document.FullPath).ToArray()).Select(state => state.Description)); }
            catch (Exception exception)
            {
                failures.Add(exception);
                return string.Join("；", remaining.Select(document => document.FullPath)) + "；最终诊断读取失败=" + exception.Message;
            }
        }

        private static InvalidOperationException CloseFailure(string message, List<Exception> failures)
        {
            var reasons = failures.Select(failure => failure.GetBaseException())
                .Select(failure => failure.Message + " (HRESULT=0x"
                    + failure.HResult.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ")")
                .Distinct().ToArray();
            if (reasons.Length > 0) message += System.Environment.NewLine + "关闭过程原因：" + string.Join("；", reasons);
            var closeException = new InvalidOperationException(message, failures.Count == 0 ? null : new AggregateException(failures));
            Logger.Error(closeException, "文档关闭流程未完成。");
            FaceToWallPreviewTrace.Error("文档关闭失败", closeException);
            return closeException;
        }

        private T ReadWithBusyRecheck<T>(Func<T> read, List<Exception> failures, string stage)
        {
            try { return read(); }
            catch (COMException exception) when (exception.HResult == unchecked((int)0x80010001)
                || exception.HResult == unchecked((int)0x8001010A))
            {
                Logger.Warn(exception, "SolidWorks 暂时拒绝调用，等待后重新{0}。", stage);
                _session.WaitForCloseSettlement();
                try { return read(); }
                catch (Exception retryException)
                {
                    failures.Add(exception);
                    failures.Add(retryException);
                    throw CloseFailure(stage + "失败，SolidWorks 仍不可用，未执行无法核验的关闭。", failures);
                }
            }
            catch (Exception exception) when (exception is COMException || exception is IOException || exception is InvalidOperationException)
            {
                failures.Add(exception);
                throw CloseFailure(stage + "失败，未继续操作无法核验的文档。", failures);
            }
        }

        internal T ReadWithBusyRecheck<T>(Func<T> read, string stage)
            => ReadWithBusyRecheck(read, new List<Exception>(), stage);

        private sealed partial class DocumentSession
        {
            public IReadOnlyList<SolidWorksDocumentCloseState> ReadCloseStates(IReadOnlyCollection<string> paths)
            {
                var targets = NormalizePaths(paths);
                var documents = ReadNativeDocuments();
                try
                {
                    var owners = targets.ToDictionary(path => path, path => new List<string>(), StringComparer.OrdinalIgnoreCase);
                    foreach (var document in documents.OfType<IModelDoc2>())
                    {
                        foreach (var reference in ReadReferences(document))
                        {
                            if (!owners.TryGetValue(reference, out var holders)) continue;
                            var owner = document.GetPathName();
                            if (string.IsNullOrWhiteSpace(owner))
                                throw new InvalidOperationException("待关闭模型仍被未保存文档引用：" + document.GetTitle());
                            holders.Add(Path.GetFullPath(owner));
                        }
                    }
                    return documents.OfType<IModelDoc2>().Where(document =>
                            !string.IsNullOrWhiteSpace(document.GetPathName()) && targets.Contains(Path.GetFullPath(document.GetPathName())))
                        .Select(document => new SolidWorksDocumentCloseState(document.GetPathName(),
                            owners[Path.GetFullPath(document.GetPathName())], HasActiveSketch(document), IsEditingComponent(document)))
                        .ToArray();
                }
                finally { foreach (var document in documents) SolidWorksFileHelper.ReleaseComReference(document); }
            }

            private object[] ReadNativeDocuments()
            {
                var raw = _application.GetDocuments();
                if (raw == null) return new object[0];
                var documents = raw as object[];
                if (documents == null || documents.Any(item => !(item is IModelDoc2)))
                {
                    foreach (var document in documents ?? new object[0]) SolidWorksFileHelper.ReleaseComReference(document);
                    throw new InvalidOperationException("SolidWorks 关闭失败诊断的文档清单无效。");
                }
                return documents;
            }

            private static string[] ReadReferences(IModelDoc2 document)
            {
                var assembly = document as IAssemblyDoc;
                if (assembly != null)
                {
                    var raw = assembly.GetComponents(false);
                    var components = raw as object[];
                    if (raw != null && components == null)
                        throw new InvalidOperationException("装配体引用清单格式无效，不能确认文档占用：" + document.GetPathName());
                    try
                    {
                        if (components != null && components.Any(component => !(component is IComponent2)))
                            throw new InvalidOperationException("装配体引用清单包含未知对象，不能确认文档占用：" + document.GetPathName());
                        return (components ?? new object[0]).OfType<IComponent2>().Select(component => component.GetPathName())
                            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath)
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    }
                    finally { foreach (var component in components ?? new object[0]) SolidWorksFileHelper.ReleaseComReference(component); }
                }
                if (document.GetType() != (int)swDocumentTypes_e.swDocDRAWING) return new string[0];
                var extension = document.Extension;
                try
                {
                    var raw = extension.GetDependencies(true, false, false, false, false);
                    if (raw == null) return new string[0];
                    var values = raw as string[];
                    if (values == null || values.Length % 2 != 0)
                        throw new InvalidOperationException("图纸引用清单格式无效，不能确认文档占用：" + document.GetPathName());
                    return Enumerable.Range(0, values.Length / 2).Select(index => values[index * 2 + 1])
                        .Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).ToArray();
                }
                finally { SolidWorksFileHelper.ReleaseComReference(extension); }
            }

            private static bool HasActiveSketch(IModelDoc2 document)
            {
                var sketch = document.GetActiveSketch2();
                try { return sketch != null; }
                finally { SolidWorksFileHelper.ReleaseComReference(sketch); }
            }

            private static bool IsEditingComponent(IModelDoc2 document)
            {
                var assembly = document as IAssemblyDoc;
                var component = assembly?.GetEditTargetComponent();
                try
                {
                    return component != null && !string.Equals(component.GetPathName(), document.GetPathName(), StringComparison.OrdinalIgnoreCase);
                }
                finally { SolidWorksFileHelper.ReleaseComReference(component); }
            }

            public void PrepareForSecondClose(SolidWorksDocumentCloseState state)
            {
                var documents = ReadNativeDocuments();
                try
                {
                    var model = documents.OfType<IModelDoc2>().SingleOrDefault(document =>
                        string.Equals(document.GetPathName(), state.FullPath, StringComparison.OrdinalIgnoreCase));
                    if (model == null) return;
                    var editing = HasActiveSketch(model) || IsEditingComponent(model);
                    if (editing) ActivateForClose(state.FullPath);
                    CloseActiveSketch(model);
                    var assembly = model as IAssemblyDoc;
                    if (assembly != null && IsEditingComponent(model)) assembly.EditAssembly();
                    if (editing) model.ClearSelection2(true);
                    if (HasActiveSketch(model) || IsEditingComponent(model))
                        throw new InvalidOperationException("文档编辑状态未能退出，不能确认第二次关闭条件：" + state.FullPath);
                }
                finally
                {
                    // 诊断和修复只持有本次枚举句柄；第二次关闭前全部释放，再由调用方读取新快照。
                    foreach (var document in documents) SolidWorksFileHelper.ReleaseComReference(document);
                }
            }

            private void ActivateForClose(string expectedPath)
            {
                var errors = 0;
                var activated = _application.ActivateDoc3(Path.GetFileName(expectedPath), false,
                    (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors) as IModelDoc2;
                try
                {
                    if (activated == null) throw new InvalidOperationException("关闭前激活目标文档失败：" + expectedPath + "；errors=" + errors);
                    SolidWorksFileHelper.ValidateOpenedDocumentPath(expectedPath, activated.GetPathName(), errors, 0);
                }
                finally { SolidWorksFileHelper.ReleaseComReference(activated); }
            }

            private static void CloseActiveSketch(IModelDoc2 model)
            {
                var sketch = model.GetActiveSketch2() as ISketch;
                if (sketch == null) return;
                ISketchManager manager = null;
                try
                {
                    manager = model.SketchManager;
                    if (sketch.Is3D()) manager.Insert3DSketch(false);
                    else manager.InsertSketch(false);
                }
                finally
                {
                    SolidWorksFileHelper.ReleaseComReference(manager);
                    SolidWorksFileHelper.ReleaseComReference(sketch);
                }
            }

            public void WaitForCloseSettlement()
            {
                // 每批失败文档仅等待一次；不泵送消息，避免关闭过程中重入生成命令。
                Thread.Sleep(100);
            }
        }
    }
}
