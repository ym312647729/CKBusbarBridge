using CKBusbarBridge.Helpers;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CKBusbarBridge.Prototype;
using Newtonsoft.Json;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 侧对侧二次驱动的项目级文件事务。它不参与绝缘子宿主定位，
    /// 只负责让柜体、伸出排、绝缘子、基座和总装的混合修改共同提交或回滚。
    /// </summary>
    internal sealed class SideToSideProjectSecondaryTransaction
        : ISideToSideCabinetSecondaryTransaction
    {
        private const string BackupPrefix = ".CKBusbarBridge-侧对侧二次驱动-";

        private readonly ISldWorks _swApp;
        private readonly string _projectPath;
        private readonly string _mainAssemblyPath;
        private readonly string _backupRoot;
        private readonly HashSet<string> _baselineFiles =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _baselineSaved;
        internal string BackupDirectoryName => Path.GetFileName(_backupRoot);
        internal string BackupManifestHash { get; private set; }
        private const string ManifestName = "cabinet-backup.json";

        private sealed class Manifest
        {
            public string Assembly { get; set; }
            public Dictionary<string, string> Files { get; set; }
        }

        internal SideToSideProjectSecondaryTransaction(
            ISldWorks swApp,
            string projectPath,
            string mainAssemblyPath, string backupDirectoryName = null)
        {
            _swApp = swApp ?? throw new ArgumentNullException(nameof(swApp));
            _projectPath = RequireDirectory(projectPath, nameof(projectPath));
            _mainAssemblyPath = Path.GetFullPath(
                string.IsNullOrWhiteSpace(mainAssemblyPath)
                    ? throw new ArgumentException(
                        "总装配体路径不能为空。",
                        nameof(mainAssemblyPath))
                    : mainAssemblyPath);
            if (!IsUnderDirectory(_mainAssemblyPath, _projectPath))
            {
                throw new InvalidOperationException(
                    "侧对侧总装配体不在当前项目目录内：" + _mainAssemblyPath);
            }

            var parent = Directory.GetParent(_projectPath)?.FullName
                ?? throw new InvalidOperationException("项目目录没有可用父目录。");
            if (backupDirectoryName != null) RequireBackupDirectoryName(backupDirectoryName);
            _backupRoot = Path.Combine(parent, backupDirectoryName ?? BackupPrefix + Guid.NewGuid().ToString("N"));
        }

        public void SaveBaseline()
        {
            if (_baselineSaved)
            {
                throw new InvalidOperationException("侧对侧二次驱动基线已经保存。");
            }

            SaveOpenProjectDocuments();
            try
            {
                Directory.CreateDirectory(_backupRoot);
                foreach (var filePath in EnumerateProjectFiles(_projectPath))
                {
                    var relativePath = GetRelativePath(_projectPath, filePath);
                    var backupPath = Path.Combine(_backupRoot, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(backupPath));
                    File.Copy(filePath, backupPath, true);
                    _baselineFiles.Add(relativePath);
                }

                var manifest = new Manifest { Assembly = GetRelativePath(_projectPath, _mainAssemblyPath),
                    Files = _baselineFiles.ToDictionary(p => p, p => BridgeGenerationFileBackup.Hash(Path.Combine(_backupRoot, p)), StringComparer.OrdinalIgnoreCase) };
                var manifestPath = Path.Combine(_backupRoot, ManifestName);
                File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest));
                BackupManifestHash = BridgeGenerationFileBackup.Hash(manifestPath);
                _baselineSaved = true;
            }
            catch
            {
                TryDeleteBackupRoot();
                throw;
            }
        }

        public void Complete()
        {
            // 业务提交点之前已完成最终保存和严格反读。备份清理属于收尾，
            // 清理失败只会留下可人工删除的事务目录，不能反向触发一次不完整回滚。
            _baselineSaved = false;
            TryDeleteBackupRoot();
        }

        internal static void RequireBackupDirectoryName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || !name.StartsWith(BackupPrefix, StringComparison.Ordinal))
                throw new InvalidOperationException("侧对侧柜体恢复目录身份无效。");
        }

        internal static IReadOnlyCollection<string> VerifyPreparedBackup(string projectPath, string assemblyPath, string directoryName, string manifestHash)
        {
            RequireBackupDirectoryName(directoryName);
            var project = RequireDirectory(projectPath, nameof(projectPath));
            var backup = Path.Combine(Directory.GetParent(project).FullName, directoryName);
            var path = Path.Combine(backup, ManifestName);
            if (string.IsNullOrWhiteSpace(manifestHash) || !File.Exists(path) || BridgeGenerationFileBackup.Hash(path) != manifestHash)
                throw new InvalidOperationException("侧对侧柜体恢复清单缺失或被修改，保留当前模型和备份。");
            var manifest = JsonConvert.DeserializeObject<Manifest>(File.ReadAllText(path));
            if (manifest?.Files == null || manifest.Assembly != GetRelativePath(project, assemblyPath) ||
                !manifest.Files.ContainsKey(manifest.Assembly))
                throw new InvalidOperationException("侧对侧柜体恢复清单与目标总装不一致。");
            foreach (var item in manifest.Files)
            {
                var source = Path.GetFullPath(Path.Combine(backup, item.Key));
                var target = Path.GetFullPath(Path.Combine(project, item.Key));
                if (Path.IsPathRooted(item.Key) || !IsUnderDirectory(source, backup) || !IsUnderDirectory(target, project) ||
                    !File.Exists(source) || BridgeGenerationFileBackup.Hash(source) != item.Value)
                    throw new InvalidOperationException("侧对侧柜体恢复文件越界、缺失或被修改：" + item.Key);
            }
            return manifest.Files.Keys.ToArray();
        }

        internal static void RestoreInterrupted(ISldWorks application, string projectPath, string assemblyPath, string directoryName, string manifestHash)
        {
            // 所有恢复文件先核验完，再关闭模型和执行恢复。
            var files = VerifyPreparedBackup(projectPath, assemblyPath, directoryName, manifestHash);
            var transaction = new SideToSideProjectSecondaryTransaction(application, projectPath, assemblyPath, directoryName);
            foreach (var file in files) transaction._baselineFiles.Add(file);
            transaction._baselineSaved = true;
            transaction.Restore();
        }

        public void Restore()
        {
            EnsureBaselineSaved();
            var driver = new SolidWorksDriver(_swApp);
            driver.CloseOpenDocumentsUnderDirectoryConfirmed(_projectPath, null);

            foreach (var relativePath in _baselineFiles)
            {
                var backupPath = Path.Combine(_backupRoot, relativePath);
                var targetPath = Path.Combine(_projectPath, relativePath);
                RestoreFileAtomically(backupPath, targetPath);
            }

            foreach (var currentFile in EnumerateProjectFiles(_projectPath).ToArray())
            {
                var relativePath = GetRelativePath(_projectPath, currentFile);
                if (!_baselineFiles.Contains(relativePath))
                {
                    File.Delete(currentFile);
                }
            }
            DeleteEmptyDirectories(_projectPath);

            var restored = driver.OpenOrActivateDocument(_mainAssemblyPath) as IModelDoc2;
            if (restored == null
                || restored.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY)
            {
                throw new InvalidOperationException(
                    "侧对侧二次驱动回滚后无法重新打开总装配体："
                    + _mainAssemblyPath);
            }

            DeleteBackupRootConfirmed();
            _baselineSaved = false;
        }

        private void SaveOpenProjectDocuments()
        {
            var driver = new SolidWorksDriver(_swApp);
            var totalAssembly = driver.OpenOrActivateDocument(_mainAssemblyPath);
            if (totalAssembly == null)
            {
                throw new InvalidOperationException(
                    "无法打开侧对侧总装配体以保存事务基线："
                    + _mainAssemblyPath);
            }

            var errors = 0;
            var warnings = 0;
            var saved = totalAssembly.Save3(
                (int)(swSaveAsOptions_e.swSaveAsOptions_Silent
                    | swSaveAsOptions_e.swSaveAsOptions_SaveReferenced),
                ref errors,
                ref warnings);
            if (!saved || errors != 0)
            {
                throw new InvalidOperationException(
                    $"保存侧对侧二次驱动事务基线失败；errors={errors}；warnings={warnings}");
            }
        }

        private static IEnumerable<string> EnumerateProjectFiles(string directory)
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (!IsSolidWorksLockFile(file))
                {
                    yield return Path.GetFullPath(file);
                }
            }

            foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            {
                var attributes = File.GetAttributes(childDirectory);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                foreach (var file in EnumerateProjectFiles(childDirectory))
                {
                    yield return file;
                }
            }
        }

        private static void RestoreFileAtomically(string backupPath, string targetPath)
        {
            if (!File.Exists(backupPath))
            {
                throw new FileNotFoundException("事务备份文件不存在。", backupPath);
            }

            var targetDirectory = Path.GetDirectoryName(targetPath);
            Directory.CreateDirectory(targetDirectory);
            var stagedPath = Path.Combine(
                targetDirectory,
                ".restore-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.Copy(backupPath, stagedPath, true);
            try
            {
                if (File.Exists(targetPath))
                {
                    File.Replace(stagedPath, targetPath, null, true);
                }
                else
                {
                    File.Move(stagedPath, targetPath);
                }
            }
            finally
            {
                if (File.Exists(stagedPath))
                {
                    File.Delete(stagedPath);
                }
            }
        }

        private static void DeleteEmptyDirectories(string rootPath)
        {
            foreach (var directory in Directory.GetDirectories(rootPath)
                .OrderByDescending(path => path.Length))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
                DeleteEmptyDirectories(directory);
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory, false);
                }
            }
        }

        private void DeleteBackupRootConfirmed()
        {
            var parent = Directory.GetParent(_projectPath)?.FullName;
            if (string.IsNullOrWhiteSpace(parent)
                || !IsUnderDirectory(_backupRoot, parent)
                || !Path.GetFileName(_backupRoot).StartsWith(
                    BackupPrefix,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "拒绝删除未验证的侧对侧事务目录：" + _backupRoot);
            }
            if (Directory.Exists(_backupRoot))
            {
                Directory.Delete(_backupRoot, true);
            }
        }

        private void TryDeleteBackupRoot()
        {
            try { DeleteBackupRootConfirmed(); } catch { }
        }

        private void EnsureBaselineSaved()
        {
            if (!_baselineSaved || !Directory.Exists(_backupRoot))
            {
                throw new InvalidOperationException("侧对侧二次驱动事务基线不可用。");
            }
        }

        private static string RequireDirectory(string path, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("项目目录不能为空。", parameterName);
            }
            var fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
            {
                throw new DirectoryNotFoundException("项目目录不存在：" + fullPath);
            }
            return fullPath;
        }

        private static string GetRelativePath(string rootPath, string filePath)
        {
            var root = Path.GetFullPath(rootPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(filePath);
            if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "项目事务文件越出项目目录：" + fullPath);
            }
            return fullPath.Substring(root.Length);
        }

        private static bool IsUnderDirectory(string path, string directory)
        {
            var root = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(
                root,
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSolidWorksLockFile(string path)
        {
            return Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal);
        }
    }
}
