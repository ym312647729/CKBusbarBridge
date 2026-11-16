using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    internal interface IBaseModelLifecycleTransaction
    {
        void SaveBaseline();

        void BackupModelDirectory(string modelPath);

        void RegisterNewDirectory(string directoryPath);

        void RegisterCreatedFiles(IEnumerable<string> filePaths);

        void Restore();

        void Complete();
    }

    internal sealed class BaseModelTransactionDirectory
    {
        internal BaseModelTransactionDirectory(string path, bool existedBefore)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("事务目录不能为空。", nameof(path));
            }

            Path = System.IO.Path.GetFullPath(path);
            ExistedBefore = existedBefore;
        }

        internal string Path { get; }

        internal bool ExistedBefore { get; }
    }

    internal static class BaseModelTransactionPathGuard
    {
        internal static string EnsureSafeDescendant(string baseRoot, string targetPath)
        {
            if (string.IsNullOrWhiteSpace(baseRoot))
            {
                throw new ArgumentException("基座根目录不能为空。", nameof(baseRoot));
            }

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                throw new ArgumentException("事务目标路径不能为空。", nameof(targetPath));
            }

            var root = Path.GetFullPath(baseRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var target = Path.GetFullPath(targetPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootPrefix = root + Path.DirectorySeparatorChar;
            if (string.Equals(root, target, StringComparison.OrdinalIgnoreCase)
                || !target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"事务路径必须是基座根目录的严格子路径：根={root}；目标={target}");
            }

            return target;
        }

        internal static string EnsureSafeProjectModelDescendant(
            string projectRoot,
            string targetPath)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                throw new ArgumentException("项目根目录不能为空。", nameof(projectRoot));
            }

            var project = Path.GetFullPath(projectRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var target = Path.GetFullPath(targetPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var folderName in new[] { "基座", "绝缘子" })
            {
                var approvedRoot = Path.Combine(project, folderName)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var approvedPrefix = approvedRoot + Path.DirectorySeparatorChar;
                if (target.StartsWith(approvedPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return target;
                }
            }

            throw new InvalidOperationException(
                $"基座事务路径必须位于项目的基座或绝缘子子目录：项目={project}；目标={target}");
        }

        internal static string EnsureSafeProjectModelOrTemplate(
            string projectRoot,
            string targetPath,
            IEnumerable<string> exactTemplatePaths)
        {
            if (exactTemplatePaths == null)
            {
                throw new ArgumentNullException(nameof(exactTemplatePaths));
            }

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                throw new ArgumentException("事务目标路径不能为空。", nameof(targetPath));
            }

            var target = Path.GetFullPath(targetPath);
            if (exactTemplatePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Contains(target, StringComparer.OrdinalIgnoreCase))
            {
                return target;
            }

            return EnsureSafeProjectModelDescendant(projectRoot, target);
        }
    }

    internal sealed class FaceToFaceBaseLifecycleTransactionAdapter
        : IBaseModelLifecycleTransaction
    {
        private readonly IFaceToFaceBaseSecondaryOperations _operations;

        internal FaceToFaceBaseLifecycleTransactionAdapter(
            IFaceToFaceBaseSecondaryOperations operations)
        {
            _operations = operations
                ?? throw new ArgumentNullException(nameof(operations));
        }

        public void SaveBaseline()
        {
            _operations.SaveBaseline();
        }

        public void BackupModelDirectory(string modelPath)
        {
            _operations.BackupModelDirectory(modelPath);
        }

        public void RegisterNewDirectory(string directoryPath)
        {
            _operations.RegisterNewDirectory(directoryPath);
        }

        public void RegisterCreatedFiles(IEnumerable<string> filePaths)
        {
            _operations.RegisterCreatedFiles(filePaths);
        }

        public void Restore()
        {
            _operations.RestoreBaselineAndBackups();
        }

        public void Complete()
        {
            _operations.ClearTransactionArtifacts();
        }
    }
}
