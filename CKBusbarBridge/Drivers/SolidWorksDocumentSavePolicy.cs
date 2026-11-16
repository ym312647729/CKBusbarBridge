using System;
using System.IO;

namespace CKBusbarBridge.Drivers
{
    internal sealed class SolidWorksDocumentSaveOutcome
    {
        internal SolidWorksDocumentSaveOutcome(bool saved, int errors, bool rebuildWarning, string detail)
        { Saved = saved; Errors = errors; RebuildWarning = rebuildWarning; Detail = detail; }

        internal bool Saved { get; }
        internal int Errors { get; }
        internal bool RebuildWarning { get; }
        internal string Detail { get; }
    }

    /// <summary>向原生接口提交当前文档保存并核验结果；不手动遍历或逐件保存子件。</summary>
    internal sealed class SolidWorksDocumentSavePolicy
    {
        internal SolidWorksSaveEntry CreateEntry(string path, int depth, bool isAssembly, bool isRoot,
            Func<bool> hasChanges, Func<bool> needsRebuild, Action ensureWritable,
            Func<bool> rebuild, Func<SolidWorksDocumentSaveOutcome> save, Action<Action> inRebuildContext = null,
            bool saveEvenIfClean = false)
        {
            // 待重建只触发首次保存；保存成功后不因残留重建状态反复重试。
            var pendingInitialSave = saveEvenIfClean || needsRebuild();
            if (pendingInitialSave || hasChanges()) ensureWritable();
            Action persist = () => Save(path, ensureWritable, needsRebuild, rebuild, save);
            return new SolidWorksSaveEntry(path, depth, isAssembly, isRoot,
                () => pendingInitialSave || hasChanges(), () =>
                {
                    if (needsRebuild() && inRebuildContext != null) inRebuildContext(persist);
                    else persist();
                    pendingInitialSave = false;
                });
        }

        internal bool SaveCurrent(SolidWorksSaveEntry document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            var saved = false;
            for (var attempt = 0; document.NeedsSave() && attempt < 3; attempt++)
            {
                document.Save();
                saved = true;
            }
            if (document.NeedsSave()) throw new IOException("文档保存后仍有待保存状态：" + document.Path);
            return saved;
        }

        internal void Save(string path, Action ensureWritable, Func<bool> needsRebuild,
            Func<bool> rebuild, Func<SolidWorksDocumentSaveOutcome> save)
        {
            ensureWritable();
            if (needsRebuild())
            {
                // 只求解本次待保存模型的活动配置。
                rebuild();
            }
            var outcome = save();
            if (!outcome.Saved || outcome.Errors != 0)
                throw new IOException("模型保存失败：" + outcome.Detail);
        }
    }

    /// <summary>求解需要活动文档；失败时同样恢复进入前上下文，不扩大保存或关闭范围。</summary>
    internal sealed class SolidWorksDocumentRebuildScope : IDisposable
    {
        private readonly Func<bool> _isTargetActive;
        private readonly Action _activateTarget;
        private readonly Action _restoreOriginal;
        private bool _restoreRequired;
        private Exception _failure;

        internal SolidWorksDocumentRebuildScope(Func<bool> isTargetActive, Action activateTarget, Action restoreOriginal)
        { _isTargetActive = isTargetActive; _activateTarget = activateTarget; _restoreOriginal = restoreOriginal; }

        internal bool Rebuild(Func<bool> rebuild)
        {
            try
            {
                if (!_isTargetActive())
                {
                    _restoreRequired = true;
                    _activateTarget();
                    if (!_isTargetActive()) throw new InvalidOperationException("保存求解前活动文档身份不符。");
                }
                var result = rebuild();
                if (!_isTargetActive()) throw new InvalidOperationException("保存求解过程中活动文档发生变化。");
                return result;
            }
            catch (Exception exception) { _failure = exception; throw; }
        }

        public void Dispose()
        {
            try
            {
                if (_restoreRequired || !_isTargetActive()) _restoreOriginal();
            }
            catch (Exception exception)
            {
                if (_failure != null) throw new AggregateException("模型求解及原活动上下文恢复均失败。", _failure, exception);
                throw new InvalidOperationException("模型求解后原活动上下文恢复失败。", exception);
            }
        }
    }
    internal sealed class SolidWorksSaveEntry
    {
        internal string Path { get; }
        internal int Depth { get; }
        internal bool IsAssembly { get; }
        internal bool IsRoot { get; }
        internal Func<bool> NeedsSave { get; }
        internal Action Save { get; }
        internal SolidWorksSaveEntry(string path, int depth, bool isAssembly, bool isRoot,
            Func<bool> needsSave, Action save)
        { Path = path; Depth = depth; IsAssembly = isAssembly; IsRoot = isRoot; NeedsSave = needsSave; Save = save; }
    }
}
