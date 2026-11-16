using Newtonsoft.Json;
using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CKBusbarBridge.Helpers
{
    /// <summary>
    /// 缓存运行目录下的“模板.zip”。压缩包未变化时直接复用解压目录，
    /// 压缩包变化时先在临时目录完整解压，再原子替换旧缓存。
    /// </summary>
    internal static class TemplateArchiveCacheManager
    {
        private const int MarkerSchemaVersion = 1;
        private const string MarkerFileName = ".template-cache.json";
        private static readonly object SyncRoot = new object();

        /// <summary>
        /// 读取安装目录中的原始模板包，解压到当前用户可写且按安装路径隔离的缓存。
        /// </summary>
        internal static string GetTemplateRootPath()
        {
            var archivePath = Path.Combine(ResolveRuntimeDirectory(), "template", "模板.zip");
            var localAppData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                throw new DirectoryNotFoundException("无法定位当前用户的本地应用数据目录。");
            }

            using (var sha256 = SHA256.Create())
            {
                var normalizedArchivePath = Path.GetFullPath(archivePath).ToUpperInvariant();
                var pathHash = BitConverter.ToString(sha256.ComputeHash(Encoding.UTF8.GetBytes(normalizedArchivePath)))
                    .Replace("-", string.Empty);
                var cacheDirectory = Path.Combine(localAppData, "RD", "CKBusbarBridge", "Templates", pathHash, "模板");
                return EnsureCached(archivePath, cacheDirectory);
            }
        }

        /// <summary>
        /// 确保指定压缩包已安全解压到缓存目录。此处只校验压缩包结构和路径安全，
        /// 不校验面对面模板、基座模板或具体 SolidWorks 文件是否存在。
        /// </summary>
        internal static string EnsureCached(string zipPath, string cacheDirectory)
        {
            lock (SyncRoot)
            {
                if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
                {
                    throw new FileNotFoundException($"共享模板压缩包不存在：{zipPath}", zipPath);
                }

                if (string.IsNullOrWhiteSpace(cacheDirectory))
                {
                    throw new ArgumentException("模板缓存目录不能为空。", nameof(cacheDirectory));
                }

                var fullCacheDirectory = Path.GetFullPath(cacheDirectory);
                var zipInfo = new FileInfo(Path.GetFullPath(zipPath));
                if (!IsCurrentCache(fullCacheDirectory, zipInfo))
                {
                    RefreshCache(fullCacheDirectory, zipInfo);
                }

                return fullCacheDirectory;
            }
        }

        private static bool IsCurrentCache(string cacheDirectory, FileInfo zipInfo)
        {
            if (!Directory.Exists(cacheDirectory))
            {
                return false;
            }

            var markerPath = Path.Combine(cacheDirectory, MarkerFileName);
            if (!File.Exists(markerPath))
            {
                return false;
            }

            try
            {
                var marker = JsonConvert.DeserializeObject<TemplateArchiveCacheMarker>(
                    File.ReadAllText(markerPath, Encoding.UTF8));
                return marker != null
                    && marker.SchemaVersion == MarkerSchemaVersion
                    && string.Equals(marker.ZipFileName, zipInfo.Name, StringComparison.OrdinalIgnoreCase)
                    && marker.ZipLength == zipInfo.Length
                    && marker.ZipLastWriteTimeUtcTicks == zipInfo.LastWriteTimeUtc.Ticks;
            }
            catch (Exception exception) when (
                exception is IOException
                || exception is UnauthorizedAccessException
                || exception is JsonException)
            {
                return false;
            }
        }

        private static void RefreshCache(string cacheDirectory, FileInfo zipInfo)
        {
            var parentDirectory = Path.GetDirectoryName(cacheDirectory);
            if (string.IsNullOrWhiteSpace(parentDirectory))
            {
                throw new DirectoryNotFoundException($"无法确定模板缓存父目录：{cacheDirectory}");
            }

            Directory.CreateDirectory(parentDirectory);
            var operationId = Guid.NewGuid().ToString("N");
            var stagingContainer = cacheDirectory + ".__extracting_" + operationId;
            var backupDirectory = cacheDirectory + ".__backup_" + operationId;
            var cacheFolderName = Path.GetFileName(cacheDirectory);
            var stagingCacheDirectory = Path.Combine(stagingContainer, cacheFolderName);
            var oldCacheMoved = false;

            try
            {
                Directory.CreateDirectory(stagingContainer);
                ExtractZipSafely(
                    zipInfo.FullName,
                    stagingContainer,
                    stagingCacheDirectory);

                // 顶层目录是切换缓存所必需的结构；其下业务内容按需求不做校验。
                if (!Directory.Exists(stagingCacheDirectory))
                {
                    throw new DirectoryNotFoundException(
                        $"共享模板压缩包中未找到顶层目录“{cacheFolderName}”：{stagingCacheDirectory}");
                }

                var marker = new TemplateArchiveCacheMarker
                {
                    SchemaVersion = MarkerSchemaVersion,
                    ZipFileName = zipInfo.Name,
                    ZipLength = zipInfo.Length,
                    ZipLastWriteTimeUtcTicks = zipInfo.LastWriteTimeUtc.Ticks
                };
                File.WriteAllText(
                    Path.Combine(stagingCacheDirectory, MarkerFileName),
                    JsonConvert.SerializeObject(marker, Formatting.Indented),
                    new UTF8Encoding(false));

                if (Directory.Exists(cacheDirectory))
                {
                    Directory.Move(cacheDirectory, backupDirectory);
                    oldCacheMoved = true;
                }

                try
                {
                    Directory.Move(stagingCacheDirectory, cacheDirectory);
                }
                catch
                {
                    if (oldCacheMoved
                        && Directory.Exists(backupDirectory)
                        && !Directory.Exists(cacheDirectory))
                    {
                        Directory.Move(backupDirectory, cacheDirectory);
                        oldCacheMoved = false;
                    }

                    throw;
                }

                if (oldCacheMoved && Directory.Exists(backupDirectory))
                {
                    TryDeleteDirectory(backupDirectory);
                }
            }
            finally
            {
                // 解压失败发生在移动旧缓存之前，因此清理暂存目录不会影响可用旧缓存。
                TryDeleteDirectory(stagingContainer);
                if (oldCacheMoved
                    && Directory.Exists(backupDirectory)
                    && !Directory.Exists(cacheDirectory))
                {
                    Directory.Move(backupDirectory, cacheDirectory);
                }
            }
        }

        private static void ExtractZipSafely(
            string zipPath,
            string stagingContainer,
            string stagingCacheDirectory)
        {
            var allowedRoot = Path.GetFullPath(stagingCacheDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var allowedRootWithSeparator = allowedRoot + Path.DirectorySeparatorChar;

            using (var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var archive = new ZipArchive(
                stream,
                ZipArchiveMode.Read,
                false,
                Encoding.GetEncoding(936)))
            {
                foreach (var entry in archive.Entries)
                {
                    var entryName = (entry.FullName ?? string.Empty)
                        .Replace('/', Path.DirectorySeparatorChar)
                        .Replace('\\', Path.DirectorySeparatorChar);
                    if (string.IsNullOrWhiteSpace(entryName))
                    {
                        continue;
                    }

                    var destinationPath = Path.GetFullPath(Path.Combine(stagingContainer, entryName));
                    if (!string.Equals(destinationPath, allowedRoot, StringComparison.OrdinalIgnoreCase)
                        && !destinationPath.StartsWith(
                            allowedRootWithSeparator,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"共享模板压缩包包含非法路径：{entry.FullName}");
                    }

                    var isDirectory = entry.FullName.EndsWith("/", StringComparison.Ordinal)
                        || entry.FullName.EndsWith("\\", StringComparison.Ordinal)
                        || string.IsNullOrEmpty(entry.Name);
                    if (isDirectory)
                    {
                        Directory.CreateDirectory(destinationPath);
                        continue;
                    }

                    var destinationDirectory = Path.GetDirectoryName(destinationPath);
                    if (string.IsNullOrWhiteSpace(destinationDirectory))
                    {
                        throw new DirectoryNotFoundException(
                            $"无法确定共享模板条目的目标目录：{entry.FullName}");
                    }

                    Directory.CreateDirectory(destinationDirectory);
                    using (var input = entry.Open())
                    using (var output = new FileStream(
                        destinationPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None))
                    {
                        input.CopyTo(output);
                    }
                }
            }
        }

        private static string ResolveRuntimeDirectory()
        {
            var runtimeDirectory = BusBridgeDriver.RegDllPath();
            if (string.IsNullOrWhiteSpace(runtimeDirectory)
                || !Directory.Exists(runtimeDirectory))
            {
                runtimeDirectory = System.Environment.CurrentDirectory;
            }

            if (string.IsNullOrWhiteSpace(runtimeDirectory)
                || !Directory.Exists(runtimeDirectory))
            {
                throw new DirectoryNotFoundException($"无法定位插件运行目录：{runtimeDirectory}");
            }

            return runtimeDirectory;
        }

        private static void TryDeleteDirectory(string directoryPath)
        {
            try
            {
                if (Directory.Exists(directoryPath))
                {
                    Directory.Delete(directoryPath, true);
                }
            }
            catch
            {
                // 缓存已经完成切换时，临时或备份目录清理失败不影响当前模板使用。
            }
        }

        private sealed class TemplateArchiveCacheMarker
        {
            public int SchemaVersion { get; set; }

            public string ZipFileName { get; set; }

            public long ZipLength { get; set; }

            public long ZipLastWriteTimeUtcTicks { get; set; }
        }
    }
}
