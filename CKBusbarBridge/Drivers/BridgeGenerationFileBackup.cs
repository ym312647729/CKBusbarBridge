using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json;

namespace CKBusbarBridge.Drivers
{
    // 仅备份绑定总装与本功能输出；恢复前由SW会话确认相关文档均已关闭。
    internal sealed class BridgeGenerationFileBackup
    {
        private readonly string _assembly;
        private readonly string[] _roots;
        private readonly HashSet<string> _references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _copies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private BridgeGenerationFileBackup _checkpoint;
        internal string Location { get; private set; }
        internal IReadOnlyList<string> ReferenceFiles => _references.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        internal BridgeGenerationFileBackup(string assembly, params string[] roots)
        {
            _assembly = Path.GetFullPath(assembly);
            _roots = roots.Select(Path.GetFullPath).ToArray();
            var project = Path.GetDirectoryName(_assembly);
            if (_roots.Any(p => !Under(project, p))) throw new InvalidOperationException("备份范围必须位于目标项目内。");
            Location = Path.Combine(project, ".bridge-generation-" + Path.GetFileNameWithoutExtension(assembly));
        }
        internal void EnsureAvailable()
        {
            if (Directory.Exists(Location)) throw new InvalidOperationException("存在未收尾生成记录，请先核对恢复状态：" + Location);
            foreach (var root in _roots)
                if (File.Exists(root)) throw new IOException("输出目录被文件占用：" + root);
            foreach (var reference in _references) ValidateReference(reference);
        }
        internal void IncludeReferenceFiles(IEnumerable<string> paths)
        {
            if (Directory.Exists(Location)) throw new InvalidOperationException("备份开始后不能扩展引用文件范围。");
            var references = paths.Select(p =>
            {
                if (string.IsNullOrWhiteSpace(p)) throw new InvalidOperationException("普通基座补偿引用缺少文件路径。");
                return Path.GetFullPath(p);
            }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var path in references) ValidateReference(path);
            _references.UnionWith(references);
        }
        private void ValidateReference(string path)
        {
            var project = Path.GetDirectoryName(_assembly);
            if (!Under(project, path)) throw new InvalidOperationException("普通基座补偿引用超出项目范围：" + path);
            if (!File.Exists(path)) throw new FileNotFoundException("普通基座补偿引用文件缺失：" + path, path);
            // 精确文件清单也不能通过目录链接访问项目外文件。
            for (var current = path; current != null; current = Path.GetDirectoryName(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("普通基座补偿引用包含链接：" + current);
                if (Same(current, project)) break;
            }
        }
        internal void Capture()
        {
            EnsureAvailable();
            Directory.CreateDirectory(Location);
            using (File.Open(Path.Combine(Location, "transaction.state"), FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            try
            {
                foreach (var root in _roots.Where(Directory.Exists))
                {
                    RejectReparsePoints(root);
                    _directories.Add(root);
                    foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)) _directories.Add(dir);
                }
                var files = new[] { _assembly }.Concat(_references).Concat(_roots.Where(Directory.Exists)
                    .SelectMany(p => Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories)))
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    var copy = Path.Combine(Location, _copies.Count.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + ".backup");
                    using (var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var destination = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        // 创建后即登记，复制或校验中途失败也能清理本次产生的不完整副本。
                        _copies.Add(file, copy);
                        source.CopyTo(destination);
                    }
                    if (Hash(file) != Hash(copy)) throw new IOException("生成基线备份校验失败：" + file);
                }
                File.WriteAllText(Path.Combine(Location, "files.json"), JsonConvert.SerializeObject(_copies, Formatting.Indented));
            }
            catch (Exception error)
            {
                // Begin尚未完成，模型未进入写入阶段；只清理本次备份，不关闭或恢复当前总装。
                try { Finish(); }
                catch (Exception cleanupError)
                {
                    throw new AggregateException("生成基线备份失败且清理未完成，请核对：" + Location, error, cleanupError);
                }
                throw;
            }
        }
        internal bool Owns(string path) => Same(path, _assembly) ||
            !string.IsNullOrWhiteSpace(path) && (_references.Contains(Path.GetFullPath(path)) || _roots.Any(root => Under(root, path)));
        internal void CheckpointCopper()
        {
            if (!File.Exists(Path.Combine(Location, "transaction.state")) || _checkpoint != null)
                throw new InvalidOperationException("铜排阶段备份必须在初始备份完成后且仅建立一次。");
            // 新副本全部复制并校验成功后才切换恢复基线，不能先删除旧备份。
            var candidate = new BridgeGenerationFileBackup(_assembly, _roots)
            { Location = Path.Combine(Location, "copper-completed") };
            candidate.IncludeReferenceFiles(_references);
            candidate.Capture();
            try
            {
                File.WriteAllText(Path.Combine(Location, "checkpoint.json"),
                    JsonConvert.SerializeObject(new { Stage = "CopperCompleted", Backup = candidate.Location }));
            }
            catch (Exception error)
            {
                try { candidate.Finish(); }
                catch (Exception cleanupError)
                { throw new AggregateException("铜排阶段备份登记失败且清理未完成。", error, cleanupError); }
                throw;
            }
            _checkpoint = candidate;
        }
        internal void Restore()
        {
            if (_checkpoint != null) { _checkpoint.Restore(); return; }
            foreach (var root in _roots.Where(Directory.Exists))
            {
                RejectReparsePoints(root);
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    if (!_copies.ContainsKey(file)) File.Delete(file);
            }
            foreach (var item in _copies)
            {
                if (!Owns(item.Key)) throw new InvalidOperationException("恢复文件超出本批范围。");
                Directory.CreateDirectory(Path.GetDirectoryName(item.Key));
                File.Copy(item.Value, item.Key, true);
                if (Hash(item.Key) != Hash(item.Value)) throw new IOException("恢复后文件校验失败：" + item.Key);
            }
            foreach (var root in _roots.Where(Directory.Exists))
                foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                    .Concat(new[] { root }).OrderByDescending(p => p.Length).ToArray())
                    if (!_directories.Contains(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        internal void Finish()
        {
            if (_checkpoint != null) { _checkpoint.Finish(); _checkpoint = null; }
            File.Delete(Path.Combine(Location, "checkpoint.json"));
            // 只清理已登记的备份文件，失败保留state供下次入口诊断。
            foreach (var copy in _copies.Values) if (File.Exists(copy)) File.Delete(copy);
            var list = Path.Combine(Location, "files.json");
            if (File.Exists(list)) File.Delete(list);
            File.Delete(Path.Combine(Location, "transaction.state"));
            Directory.Delete(Location, false);
            _copies.Clear();
            _directories.Clear();
        }

        internal void VerifyRestored()
        {
            if (_checkpoint != null) { _checkpoint.VerifyRestored(); return; }
            if (_copies.Count == 0) throw new InvalidOperationException("缺少本次生成恢复基线。");
            foreach (var item in _copies)
            {
                // SolidWorks重开文档会重新生成会话锁；其内容不属于恢复基线。
                if (IsTransientLockFile(item.Key)) continue;
                if (!Owns(item.Key) || !File.Exists(item.Key) || !File.Exists(item.Value) || Hash(item.Key) != Hash(item.Value))
                    throw new IOException("恢复后的模型或生成记录与本次备份不一致：" + item.Key);
            }
            foreach (var root in _roots.Where(Directory.Exists))
            {
                RejectReparsePoints(root);
                var added = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(p => !IsTransientLockFile(p) && !_copies.ContainsKey(p));
                if (added != null) throw new IOException("恢复范围内仍存在本次新增文件：" + added);
            }
        }

        internal bool TryFinishRestoredTransaction(IEnumerable<string> currentReferences, Action verifyCurrentModel)
        {
            if (verifyCurrentModel == null) throw new ArgumentNullException(nameof(verifyCurrentModel));
            if (!Directory.Exists(Location)) return false;
            // 铜排检查点有独立恢复目标，不能按初始副本自动收尾。
            if (File.Exists(Path.Combine(Location, "checkpoint.json")) || Directory.EnumerateDirectories(Location).Any()) return false;
            var state = Path.Combine(Location, "transaction.state");
            var list = Path.Combine(Location, "files.json");
            if (!File.Exists(state) || !File.Exists(list)) return false;
            RejectReparsePoints(Location);
            var files = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(list),
                new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace, TypeNameHandling = TypeNameHandling.None });
            if (files == null || !files.ContainsKey(_assembly) || _copies.Count != 0)
                throw new InvalidOperationException("未收尾生成记录缺少完整总装恢复清单：" + Location);
            var references = new HashSet<string>(currentReferences.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath),
                StringComparer.OrdinalIgnoreCase);
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { state, list };
            foreach (var item in files)
            {
                if (IsTransientLockFile(item.Key) && !File.Exists(item.Key))
                {
                    if (File.Exists(item.Value)) File.Delete(item.Value);
                    continue;
                }
                ValidateReference(item.Key);
                if (!Owns(item.Key) && !references.Contains(Path.GetFullPath(item.Key)))
                    throw new InvalidOperationException("未收尾生成记录包含当前总装未引用的范围外文件：" + item.Key);
                var name = Path.GetFileName(item.Value);
                if (string.IsNullOrWhiteSpace(name) || !Same(Path.GetDirectoryName(item.Value), Location) || name.Length != 13 ||
                    !name.EndsWith(".backup", StringComparison.Ordinal) || !name.Take(6).All(char.IsDigit) || !expected.Add(item.Value))
                    throw new InvalidOperationException("未收尾生成备份路径无效：" + item.Value);
            }
            if (Directory.EnumerateFiles(Location).Any(p => !expected.Contains(p)))
                throw new InvalidOperationException("未收尾生成目录包含未登记文件，请先核对：" + Location);
            foreach (var item in files)
            {
                if (IsTransientLockFile(item.Key) && !File.Exists(item.Key)) continue;
                if (!Owns(item.Key)) _references.Add(Path.GetFullPath(item.Key));
                _copies.Add(Path.GetFullPath(item.Key), Path.GetFullPath(item.Value));
            }
            VerifyRestored();
            verifyCurrentModel();
            VerifyRestored();
            Finish();
            return true;
        }

        // 铜排检查点代表铜排和记录已经落盘；重新打开设计界面时，先恢复该检查点，
        // 再允许上层读取业务模型。只有文件清单和两次完整性校验都通过才会清理事务证据。
        internal bool TryFinishCopperCheckpoint(IEnumerable<string> currentReferences, Action verifyCurrentModel)
        {
            if (verifyCurrentModel == null) throw new ArgumentNullException(nameof(verifyCurrentModel));
            if (!Directory.Exists(Location) || !File.Exists(Path.Combine(Location, "checkpoint.json"))) return false;
            var references = new HashSet<string>(currentReferences.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath),
                StringComparer.OrdinalIgnoreCase);
            LoadCommittedCleanupManifest(references);
            if (_checkpoint == null) return false;
            _checkpoint.Restore();
            VerifyRestored();
            verifyCurrentModel();
            VerifyRestored();
            Finish();
            return true;
        }

        // 仅供调用方已确认最终Completed记录的专用入口；不能用铜排检查点代替最终模型验收。
        internal bool IsNewCommittedGeneration(IEnumerable<string> currentReferences, string recordPath, string generationId)
        {
            if (string.IsNullOrWhiteSpace(generationId)) throw new InvalidOperationException("已提交生成缺少可核验的生成编号。");
            if (!Directory.Exists(Location)) return false;
            var references = new HashSet<string>(currentReferences.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
            // 只读核验清单；旧Completed记录也可能来自回滚，不能据此丢弃失败事务的备份。
            var captured = new BridgeGenerationFileBackup(_assembly, _roots) { Location = Location };
            captured.LoadCommittedCleanupManifest(references);
            while (captured._checkpoint != null) captured = captured._checkpoint;
            if (!captured.Owns(recordPath)) throw new InvalidOperationException("已提交生成记录超出事务范围。");
            if (!captured._copies.TryGetValue(Path.GetFullPath(recordPath), out var copy)) return true;
            var previous = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(copy));
            return previous.Value<string>("GenerationId") != generationId;
        }

        internal bool TryFinishCommittedTransaction(IEnumerable<string> currentReferences, Action verifyCompletedModel)
        {
            if (verifyCompletedModel == null) throw new ArgumentNullException(nameof(verifyCompletedModel));
            if (!Directory.Exists(Location)) return false;
            var references = new HashSet<string>(currentReferences.Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath),
                StringComparer.OrdinalIgnoreCase);
            LoadCommittedCleanupManifest(references);
            var files = Directory.EnumerateFiles(Location, "*", SearchOption.AllDirectories)
                .ToDictionary(p => p, Hash, StringComparer.OrdinalIgnoreCase);
            var directories = Directory.EnumerateDirectories(Location, "*", SearchOption.AllDirectories).ToArray();
            verifyCompletedModel();
            if (Directory.EnumerateFiles(Location, "*", SearchOption.AllDirectories).Count() != files.Count ||
                files.Any(p => !File.Exists(p.Key) || Hash(p.Key) != p.Value) ||
                !new HashSet<string>(directories, StringComparer.OrdinalIgnoreCase).SetEquals(
                    Directory.EnumerateDirectories(Location, "*", SearchOption.AllDirectories)))
                throw new IOException("最终模型核验期间生成备份发生变化，保留恢复现场：" + Location);
            Finish();
            return true;
        }

        private void LoadCommittedCleanupManifest(ISet<string> currentReferences)
        {
            RejectReparsePoints(Location);
            var state = Path.Combine(Location, "transaction.state");
            var list = Path.Combine(Location, "files.json");
            if (!File.Exists(state) || !File.Exists(list) || _copies.Count != 0)
                throw new InvalidOperationException("待收尾事务缺少完整备份清单：" + Location);
            var files = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(list),
                new JsonSerializerSettings { ObjectCreationHandling = ObjectCreationHandling.Replace, TypeNameHandling = TypeNameHandling.None });
            if (files == null || !files.ContainsKey(_assembly))
                throw new InvalidOperationException("待收尾事务缺少总装备份：" + Location);
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { state, list };
            var checkpoint = Path.Combine(Location, "checkpoint.json");
            var child = Path.Combine(Location, "copper-completed");
            if (File.Exists(checkpoint))
            {
                var data = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(checkpoint));
                if ((string)data["Stage"] != "CopperCompleted" || !Same((string)data["Backup"], child) || !Directory.Exists(child))
                    throw new InvalidOperationException("待收尾铜排检查点身份不符：" + checkpoint);
                expected.Add(checkpoint);
                if (File.Exists(Path.Combine(child, "checkpoint.json")))
                    throw new InvalidOperationException("待收尾事务包含不支持的嵌套铜排检查点：" + child);
                _checkpoint = new BridgeGenerationFileBackup(_assembly, _roots) { Location = child };
                _checkpoint.LoadCommittedCleanupManifest(currentReferences);
            }
            if (Directory.EnumerateDirectories(Location).Any(p => _checkpoint == null || !Same(p, child)))
                throw new InvalidOperationException("待收尾备份包含未知子目录：" + Location);
            foreach (var item in files)
            {
                if (IsTransientLockFile(item.Key) && !File.Exists(item.Key))
                {
                    if (File.Exists(item.Value)) File.Delete(item.Value);
                    continue;
                }
                ValidateReference(item.Key);
                var path = Path.GetFullPath(item.Key);
                var owned = Owns(path);
                if (!owned && !currentReferences.Contains(path))
                    throw new InvalidOperationException("待收尾备份超出当前总装引用范围：" + item.Key);
                var name = Path.GetFileName(item.Value);
                if (!Same(Path.GetDirectoryName(item.Value), Location) || name.Length != 13 ||
                    !name.EndsWith(".backup", StringComparison.Ordinal) || !name.Take(6).All(char.IsDigit) ||
                    !File.Exists(item.Value) || !expected.Add(item.Value))
                    throw new InvalidOperationException("待收尾备份路径缺失或无效：" + item.Value);
                if (!owned) _references.Add(path);
                _copies.Add(path, Path.GetFullPath(item.Value));
            }
            if (Directory.EnumerateFiles(Location).Any(p => !expected.Contains(p)))
                throw new InvalidOperationException("待收尾备份包含未登记文件：" + Location);
        }
        internal static IReadOnlyList<string> VerifyRegisteredFiles(string project,
            IReadOnlyDictionary<string, string> files, ISet<string> frameFiles, bool forReadback)
        {
            var current = ReadRegisteredHashes(project, files.Keys);
            // 字节变化只触发业务核验，不能单独作为模型变化的结论。
            return files.Where(p => p.Value != current[p.Key]).Select(p => p.Key).ToArray();
        }
        internal static Dictionary<string, string> ReadRegisteredHashes(string project, IEnumerable<string> paths)
        {
            var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                if (!Under(project, path) || !File.Exists(path))
                    throw new InvalidOperationException("正式模型文件缺失或超出项目范围：" + path);
                current[path] = Hash(path);
            }
            return current;
        }
        internal static string Hash(string file)
        {
            // 总装和生成件可能仍由SolidWorks以读写句柄持有；校验只读文件，不应拒绝宿主的写访问权限。
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }
        internal static bool Same(string a, string b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        internal static bool Under(string root, string path) => !string.IsNullOrWhiteSpace(path) &&
            Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        private static bool IsTransientLockFile(string path) =>
            !string.IsNullOrWhiteSpace(path) && Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal);
        private void RejectReparsePoints(string root)
        {
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("生成目录不能为链接：" + root);
            foreach (var path in Directory.EnumerateFileSystemEntries(root))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("生成范围包含链接：" + path);
                if ((attributes & FileAttributes.Directory) != 0) RejectReparsePoints(path);
            }
        }
    }
}
