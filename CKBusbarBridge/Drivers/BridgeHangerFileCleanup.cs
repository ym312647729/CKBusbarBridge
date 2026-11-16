using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    // 快照只持有路径，关闭父文档后不能继续使用此前取得的 COM 包装对象。
    internal sealed class BridgeHangerCleanupDocument
    {
        internal string Path { get; }
        internal string Label { get; }
        internal bool Loaded { get; }
        internal string[] References { get; }
        internal BridgeHangerCleanupDocument(string path, bool loaded, IEnumerable<string> references, string label = null)
        { Path = path; Label = label ?? path; Loaded = loaded; References = references.ToArray(); }
    }

    internal interface IBridgeHangerCleanupSession
    {
        IReadOnlyList<BridgeHangerCleanupDocument> ReadDocuments(IReadOnlyList<string> modelPaths);
        void SaveDocuments(IReadOnlyList<string> paths);
        void CloseDocument(string path);
        void ReleaseMemory();
        void CloseProject();
        void ReopenProject();
        void Trace(string message);
    }

    internal sealed partial class BridgeHangerFileCleanup
    {
        private const int UnloadRounds = 3;

        private void ExecuteWithSession(string[] directories, Func<IEnumerable<string>> readReferences)
        {
            if (directories.Length == 0) return;
            var closeAttempted = false;
            var errors = new List<Exception>();
            try { ExecuteCleanup(directories, readReferences, errors, ref closeAttempted); }
            catch (Exception error)
            {
                // 无法取得可靠快照时停止删除，生成结果仍由外层非阻断收尾保留。
                _session.Trace("清理中止：" + error);
                errors.Add(error);
            }
            if (closeAttempted)
            {
                try { _session.ReopenProject(); }
                catch (Exception error) { _session.Trace("清理后总装重开未完成：" + error); errors.Add(error); }
            }
            if (errors.Count > 0) throw new IOException("部分废弃吊杆未清理，已保留生成结果。", new AggregateException(errors));
        }

        private void ExecuteCleanup(string[] directories, Func<IEnumerable<string>> readReferences, List<Exception> errors, ref bool closeAttempted)
        {
            // 所有候选一起处理：候选吊杆之间的内部引用只决定关闭次序，不能冒充有效外部引用。
            if (!UnloadCandidates(directories, readReferences))
            {
                _session.Trace("局部释放后仍有驻留或文件占用，关闭已保存的当前总装后重试一次。");
                closeAttempted = true;
                try
                {
                    _session.CloseProject();
                    _session.ReleaseMemory();
                    if (!UnloadCandidates(directories, readReferences)) _session.Trace("部分旧模型仍有占用，继续清理其余可删除文件。");
                }
                catch (Exception error) { _session.Trace("总装释放尝试未完成：" + error); errors.Add(error); }
            }

            foreach (var directory in directories)
            {
                try
                {
                    if (!Directory.Exists(directory)) { _session.Trace("已不存在：" + directory); continue; }
                    var state = ReadCleanupState(directories, readReferences);
                    // 关闭、保存和宿主事件都可能改变引用；真正删除前再查一次。
                    RejectLinks(directory);
                    var loaded = state.Documents.Where(d => d.Loaded && Under(directory, d.Path) && !state.Retained.ContainsKey(d.Path)).Select(d => d.Path).ToArray();
                    if (loaded.Length > 0) throw new IOException("删除前吊杆仍有驻留：" + string.Join("；", loaded));
                    if (!state.Retained.Keys.Any(p => Under(directory, p)))
                    {
                        RequireFilesReleased(state.Files.Where(p => Under(directory, p)));
                        Directory.Delete(directory, true);
                        if (Directory.Exists(directory)) throw new IOException("废弃吊杆目录删除未确认：" + directory);
                        _session.Trace("已删除：" + directory);
                    }
                    else DeleteUnreferencedFiles(directory, directories, readReferences);
                }
                catch (Exception error)
                {
                    // 废弃树相互独立；一处失败保留现场并继续其余清理，不回退任何已提交模型。
                    _session.Trace("未清理：" + directory + "；" + error);
                    errors.Add(new IOException("未能清理废弃目录：" + directory, error));
                }
            }
        }

        private bool UnloadCandidates(string[] directories, Func<IEnumerable<string>> readReferences)
        {
            string previous = null;
            for (var round = 1; round <= UnloadRounds; round++)
            {
                var state = ReadCleanupState(directories, readReferences);
                var loaded = UnreferencedLoadedPaths(directories, state);
                _session.Trace("释放轮次=" + round + "；待卸载=" + string.Join("；", loaded));
                // 先保存整批未引用生成文档，再关父级，防止父级关闭连带丢失子件的修改。
                _session.SaveDocuments(ChildFirst(loaded, state.Documents));
                foreach (var path in ParentFirst(loaded, state.Documents))
                {
                    // 每次 CloseDoc 都可能连带卸载子树或引发引用变化，必须重新取快照。
                    var current = ReadCleanupState(directories, readReferences);
                    if (current.Documents.Any(d => d.Loaded && Same(d.Path, path)) &&
                        !current.Retained.ContainsKey(path))
                        _session.CloseDocument(path);
                }
                _session.ReleaseMemory();
                state = ReadCleanupState(directories, readReferences);
                loaded = UnreferencedLoadedPaths(directories, state);
                var blockers = FileBlockers(state.Files.Where(p => !state.Retained.ContainsKey(p)));
                if (loaded.Length == 0 && blockers.Count == 0) return true;
                _session.Trace("释放后仍驻留=" + string.Join("；", loaded) + "；文件占用=" +
                    string.Join("；", blockers.Select(e => e.ToString())));
                var signature = string.Join("|", loaded.Concat(blockers.Select(e => e.Message)));
                // 两轮状态相同即升级，不反复对同一路径无限 CloseDoc。
                if (signature == previous) return false;
                previous = signature;
            }
            return false;
        }

        private CleanupState ReadCleanupState(string[] directories, Func<IEnumerable<string>> readReferences)
        {
            var files = new List<string>();
            foreach (var dir in directories.Where(Directory.Exists))
            {
                RejectLinks(dir);
                files.AddRange(Directory.GetFiles(dir, "*", SearchOption.AllDirectories));
            }
            var models = files.Where(IsModel).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            var documents = _session.ReadDocuments(models)
                ?? throw new InvalidOperationException("无法取得吊杆清理文档快照。");
            var seeds = readReferences()?.ToArray()
                ?? throw new InvalidOperationException("无法取得本次吊杆有效引用。");
            var retained = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in seeds) Protect(directories, retained, path, "当前生成绑定");
            foreach (var doc in documents.Where(d => !directories.Any(dir => Under(dir, d.Path))))
                foreach (var path in doc.References) Protect(directories, retained, path, doc.Label);
            // 被外部有效引用的候选，其依赖也必须保留，包括跨候选目录共享的零件。
            int count;
            do
            {
                count = retained.Count;
                foreach (var doc in documents.Where(d => retained.ContainsKey(d.Path)))
                    foreach (var path in doc.References) Protect(directories, retained, path, doc.Label);
            } while (count != retained.Count);
            foreach (var path in retained.Keys)
                if (!File.Exists(path)) throw new IOException("有效引用的吊杆模型文件缺失，不能完成清理：" + path + "；引用方=" + retained[path]);
            return new CleanupState { Files = files.ToArray(), Documents = documents, Retained = retained };
        }

        private void Protect(string[] directories, IDictionary<string, string> retained, string path, string owner)
        {
            if (!directories.Any(dir => Under(dir, path))) return;
            var fullPath = Path.GetFullPath(path);
            if (!retained.ContainsKey(fullPath)) retained.Add(fullPath, owner);
        }

        private void DeleteUnreferencedFiles(string directory, string[] directories, Func<IEnumerable<string>> readReferences)
        {
            foreach (var path in Directory.GetFiles(directory, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var state = ReadCleanupState(directories, readReferences);
                if (state.Retained.TryGetValue(path, out var owner))
                { _session.Trace("有效引用保留：" + path + "；引用方=" + owner); continue; }
                if (state.Documents.Any(d => d.Loaded && Same(d.Path, path))) throw new IOException("删除前吊杆再次驻留：" + path);
                RequireFilesReleased(new[] { path });
                File.Delete(path);
                if (File.Exists(path)) throw new IOException("废弃吊杆文件删除未确认：" + path);
                _session.Trace("已删除：" + path);
            }
            foreach (var dir in Directory.GetDirectories(directory, "*", SearchOption.AllDirectories)
                .OrderByDescending(p => p.Length).Concat(new[] { directory }))
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            var final = ReadCleanupState(directories, readReferences);
            if (final.Files.Any(p => Under(directory, p) && !final.Retained.ContainsKey(p)))
                throw new IOException("吊杆清理期间出现新的未清理文件：" + directory);
        }

        private string[] UnreferencedLoadedPaths(string[] directories, CleanupState state) => state.Documents
            .Where(d => d.Loaded && directories.Any(dir => Under(dir, d.Path)) && !state.Retained.ContainsKey(d.Path))
            .Select(d => d.Path).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();

        private string[] ParentFirst(string[] paths, IReadOnlyList<BridgeHangerCleanupDocument> documents)
        {
            var remaining = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            while (remaining.Count > 0)
            {
                var children = new HashSet<string>(documents.Where(d => remaining.Contains(d.Path))
                    .SelectMany(d => d.References.Where(p => !Same(p, d.Path))), StringComparer.OrdinalIgnoreCase);
                var next = remaining.Where(p => !children.Contains(p)).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
                // 在位外部引用可能形成环；只在本批可删范围内做有界关闭尝试，最终以后置条件为准。
                if (next.Length == 0) next = remaining.OrderBy(p => ModelOrder(p)).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).Take(1).ToArray();
                foreach (var path in next) { result.Add(path); remaining.Remove(path); }
            }
            return result.ToArray();
        }
        private string[] ChildFirst(string[] paths, IReadOnlyList<BridgeHangerCleanupDocument> documents) => ParentFirst(paths, documents).Reverse().ToArray();
        private int ModelOrder(string path) => string.Equals(Path.GetExtension(path), ".SLDDRW", StringComparison.OrdinalIgnoreCase) ? 0 :
            string.Equals(Path.GetExtension(path), ".SLDASM", StringComparison.OrdinalIgnoreCase) ? 1 : 2;
        private bool IsModel(string path) => new[] { ".SLDASM", ".SLDPRT", ".SLDDRW" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
        private bool Under(string dir, string path) => !string.IsNullOrWhiteSpace(path) && BridgeGenerationFileBackup.Under(dir, path);
        private bool Same(string left, string right) => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) && BridgeGenerationFileBackup.Same(left, right);

        private List<IOException> FileBlockers(IEnumerable<string> files)
        {
            var errors = new List<IOException>();
            foreach (var path in files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try { using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None)) { } }
                catch (IOException error)
                {
                    // 文件锁是可预见的待释放状态；保留原异常用于有界重试及最终事务日志。
                    errors.Add(new IOException("文件占用未释放：" + path, error));
                }
            }
            return errors;
        }
        private void RequireFilesReleased(IEnumerable<string> files)
        {
            var errors = FileBlockers(files);
            if (errors.Count > 0) throw new IOException("旧吊杆文件仍被占用，无法删除。详情见日志。", new AggregateException(errors));
        }
        private sealed class CleanupState
        {
            internal string[] Files;
            internal IReadOnlyList<BridgeHangerCleanupDocument> Documents;
            internal Dictionary<string, string> Retained;
        }
    }
}
