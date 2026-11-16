using System;
using System.IO;

namespace CKBusbarBridge.Helpers
{
    /// <summary>
    /// 将一个场景模板目录中的缺失文件补齐到项目目录，同时保护已经驱动过的项目文件。
    /// </summary>
    internal static class ProjectTemplateCopier
    {
        internal static string CopyMissingTemplateFiles(
            string sourceDirectory,
            string projectPath,
            string mainAssemblyFileName,
            string sourceDisplayName)
        {
            if (string.IsNullOrWhiteSpace(sourceDirectory)
                || !Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException(
                    $"{sourceDisplayName}缓存目录不存在：{sourceDirectory}");
            }

            if (string.IsNullOrWhiteSpace(projectPath)
                || !Directory.Exists(projectPath))
            {
                throw new DirectoryNotFoundException($"项目目录不存在：{projectPath}");
            }

            if (string.IsNullOrWhiteSpace(mainAssemblyFileName))
            {
                throw new ArgumentException(
                    "根总装配体文件名不能为空。",
                    nameof(mainAssemblyFileName));
            }

            var sourceRoot = Path.GetFullPath(sourceDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var projectRoot = Path.GetFullPath(projectPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            foreach (var sourceChildDirectory in Directory.GetDirectories(
                sourceRoot,
                "*",
                SearchOption.AllDirectories))
            {
                var relativePath = GetRelativePath(sourceRoot, sourceChildDirectory);
                var destinationDirectory = Path.Combine(projectRoot, relativePath);
                if (File.Exists(destinationDirectory))
                {
                    throw new IOException(
                        $"{sourceDisplayName}目录与项目文件冲突：{destinationDirectory}");
                }

                Directory.CreateDirectory(destinationDirectory);
            }

            foreach (var sourceFile in Directory.GetFiles(
                sourceRoot,
                "*",
                SearchOption.AllDirectories))
            {
                var relativePath = GetRelativePath(sourceRoot, sourceFile);
                var destinationPath = Path.Combine(projectRoot, relativePath);
                if (Directory.Exists(destinationPath))
                {
                    throw new IOException(
                        $"{sourceDisplayName}文件与项目目录冲突：{destinationPath}");
                }

                if (File.Exists(destinationPath))
                {
                    // 项目中的同名文件可能已被参数驱动或人工修改，不能用模板覆盖。
                    continue;
                }

                CopyFileAtomically(sourceFile, destinationPath);
            }

            var mainAssemblyPath = Path.Combine(projectRoot, mainAssemblyFileName);
            if (Directory.Exists(mainAssemblyPath))
            {
                throw new IOException(
                    $"{sourceDisplayName}根总装配体路径被同名目录占用：{mainAssemblyPath}");
            }

            if (!File.Exists(mainAssemblyPath))
            {
                throw new FileNotFoundException(
                    $"{sourceDisplayName}复制完成但未找到根总装配体：{mainAssemblyPath}",
                    mainAssemblyPath);
            }

            return mainAssemblyPath;
        }

        private static string GetRelativePath(string rootDirectory, string childPath)
        {
            var rootWithSeparator = rootDirectory + Path.DirectorySeparatorChar;
            var fullChildPath = Path.GetFullPath(childPath);
            if (!fullChildPath.StartsWith(
                rootWithSeparator,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"模板缓存包含越界路径：{fullChildPath}");
            }

            return fullChildPath.Substring(rootWithSeparator.Length);
        }

        /// <summary>
        /// 先写入同目录临时文件再改名，避免复制中断后留下半个模型文件。
        /// </summary>
        private static void CopyFileAtomically(string sourcePath, string destinationPath)
        {
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrWhiteSpace(destinationDirectory))
            {
                throw new DirectoryNotFoundException(
                    $"无法确定模板文件的目标目录：{destinationPath}");
            }

            Directory.CreateDirectory(destinationDirectory);
            var temporaryPath = Path.Combine(
                destinationDirectory,
                ".__copying_" + Guid.NewGuid().ToString("N"));

            try
            {
                File.Copy(sourcePath, temporaryPath, false);
                try
                {
                    File.Move(temporaryPath, destinationPath);
                }
                catch (IOException) when (File.Exists(destinationPath))
                {
                    // 并发初始化时另一个调用可能已补齐同一文件，仍按“不覆盖”处理。
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
    }
}
