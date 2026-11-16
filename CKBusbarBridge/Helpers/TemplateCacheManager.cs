using CKBusbarBridge.Models;
using Newtonsoft.Json;
using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace CKBusbarBridge.Helpers
{
    /// <summary>
    /// 管理插件 template 目录内的场景模板缓存。
    /// 压缩包发生变化时先解压并校验临时目录，再替换旧缓存，避免留下不完整模板。
    /// </summary>
    public static class TemplateCacheManager
    {
        private const int MarkerSchemaVersion = 3;
        private const string MarkerFileName = ".template-cache.json";
        private static readonly object SyncRoot = new object();

        /// <summary>
        /// 确保场景模板已经解压且缓存版本与 ZIP 一致，返回包含基架模板、柜体基座、铜排模板等目录的场景模板根路径。
        /// </summary>
        public static string GetTemplateRootPath(DesignScenarioId scenarioId)
        {
            lock (SyncRoot)
            {
                var dllDirectory = ResolveDllDirectory();
                var zipPath = Path.Combine(dllDirectory, DesignScenarioDefaults.GetTemplateZipRelativePath(scenarioId));
                if (!File.Exists(zipPath))
                {
                    throw new FileNotFoundException($"模板压缩包不存在：{zipPath}", zipPath);
                }

                var templateDirectory = Path.GetDirectoryName(zipPath);
                if (string.IsNullOrWhiteSpace(templateDirectory))
                {
                    throw new DirectoryNotFoundException($"无法确定模板目录：{zipPath}");
                }

                var cacheDirectory = Path.Combine(templateDirectory, DesignScenarioDefaults.GetTemplateCacheFolderName(scenarioId));
                var contentRelativePath = DesignScenarioDefaults.GetTemplateContentRelativePath(scenarioId);
                var contentDirectory = CombineOptional(cacheDirectory, contentRelativePath);
                var zipInfo = new FileInfo(zipPath);

                if (!IsCurrentCache(
                        cacheDirectory,
                        contentDirectory,
                        zipInfo,
                        contentRelativePath))
                {
                    RefreshCache(cacheDirectory, zipInfo, contentRelativePath);
                }

                contentDirectory = CombineOptional(cacheDirectory, contentRelativePath);
                if (!Directory.Exists(contentDirectory))
                {
                    throw new DirectoryNotFoundException(
                        $"模板缓存中未找到场景内容目录：{contentDirectory}");
                }

                return contentDirectory;
            }
        }

        private static string ResolveDllDirectory()
        {
            var dllDirectory = CKBusbarBridge.BusBridgeDriver.RegDllPath();
            if (string.IsNullOrWhiteSpace(dllDirectory) || !Directory.Exists(dllDirectory))
            {
                dllDirectory = Environment.CurrentDirectory;
            }

            if (string.IsNullOrWhiteSpace(dllDirectory) || !Directory.Exists(dllDirectory))
            {
                throw new DirectoryNotFoundException($"无法定位插件运行目录：{dllDirectory}");
            }

            return dllDirectory;
        }

        private static bool IsCurrentCache(
            string cacheDirectory,
            string contentDirectory,
            FileInfo zipInfo,
            string contentRelativePath)
        {
            if (!Directory.Exists(cacheDirectory)
                || !Directory.Exists(contentDirectory))
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
                var marker = JsonConvert.DeserializeObject<TemplateCacheMarker>(File.ReadAllText(markerPath, Encoding.UTF8));
                return marker != null
                    && marker.SchemaVersion == MarkerSchemaVersion
                    && string.Equals(marker.ZipFileName, zipInfo.Name, StringComparison.OrdinalIgnoreCase)
                    && marker.ZipLength == zipInfo.Length
                    && marker.ZipLastWriteTimeUtcTicks == zipInfo.LastWriteTimeUtc.Ticks
                    && string.Equals(
                        marker.ContentRelativePath ?? string.Empty,
                        contentRelativePath ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
            {
                return false;
            }
        }

        private static void RefreshCache(
            string cacheDirectory,
            FileInfo zipInfo,
            string contentRelativePath)
        {
            var parentDirectory = Path.GetDirectoryName(cacheDirectory);
            if (string.IsNullOrWhiteSpace(parentDirectory))
            {
                throw new DirectoryNotFoundException($"无法确定模板缓存父目录：{cacheDirectory}");
            }

            Directory.CreateDirectory(parentDirectory);

            var operationId = Guid.NewGuid().ToString("N");
            var stagingContainer = cacheDirectory + ".__extracting_" + operationId;
            var cacheFolderName = Path.GetFileName(cacheDirectory);
            var stagingCacheDirectory = Path.Combine(stagingContainer, cacheFolderName);
            var backupDirectory = cacheDirectory + ".__backup_" + operationId;
            var oldCacheMoved = false;

            try
            {
                Directory.CreateDirectory(stagingContainer);
                ExtractZipSafely(zipInfo.FullName, stagingContainer);

                if (!Directory.Exists(stagingCacheDirectory))
                {
                    throw new DirectoryNotFoundException(
                        $"模板压缩包中未找到顶层场景目录“{cacheFolderName}”：{stagingCacheDirectory}");
                }

                var stagingContentDirectory = CombineOptional(stagingCacheDirectory, contentRelativePath);
                if (!Directory.Exists(stagingContentDirectory))
                {
                    throw new DirectoryNotFoundException($"模板压缩包中未找到场景目录：{stagingContentDirectory}");
                }

                var marker = new TemplateCacheMarker
                {
                    SchemaVersion = MarkerSchemaVersion,
                    ZipFileName = zipInfo.Name,
                    ZipLength = zipInfo.Length,
                    ZipLastWriteTimeUtcTicks = zipInfo.LastWriteTimeUtc.Ticks,
                    ContentRelativePath = contentRelativePath ?? string.Empty
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
                    if (oldCacheMoved && Directory.Exists(backupDirectory) && !Directory.Exists(cacheDirectory))
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
            catch (InvalidDataException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new IOException($"刷新模板缓存失败。压缩包：{zipInfo.FullName}；缓存目录：{cacheDirectory}", ex);
            }
            finally
            {
                TryDeleteDirectory(stagingContainer);
            }
        }

        private static void ExtractZipSafely(string zipPath, string destinationDirectory)
        {
            var destinationRoot = Path.GetFullPath(destinationDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            using (var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, false, Encoding.GetEncoding(936)))
            {
                foreach (var entry in archive.Entries)
                {
                    var normalizedEntryName = (entry.FullName ?? string.Empty)
                        .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                    if (string.IsNullOrWhiteSpace(normalizedEntryName))
                    {
                        continue;
                    }

                    var destinationPath = Path.GetFullPath(Path.Combine(destinationDirectory, normalizedEntryName));
                    if (!destinationPath.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException($"模板压缩包包含非法路径：{entry.FullName}");
                    }

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destinationPath);
                        continue;
                    }

                    var destinationParent = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrWhiteSpace(destinationParent))
                    {
                        Directory.CreateDirectory(destinationParent);
                    }

                    using (var entryStream = entry.Open())
                    using (var outputStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        entryStream.CopyTo(outputStream);
                    }

                    File.SetLastWriteTime(destinationPath, entry.LastWriteTime.LocalDateTime);
                }
            }
        }

        private static string CombineOptional(string rootPath, string relativePath)
        {
            return string.IsNullOrWhiteSpace(relativePath)
                ? rootPath
                : Path.Combine(rootPath, relativePath);
        }

        private static void TryDeleteDirectory(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            {
                return;
            }

            try
            {
                Directory.Delete(directoryPath, true);
            }
            catch
            {
                // 缓存已完成切换时，临时/备份目录清理失败不影响当前模板使用。
            }
        }

        private sealed class TemplateCacheMarker
        {
            public int SchemaVersion { get; set; }

            public string ZipFileName { get; set; }

            public long ZipLength { get; set; }

            public long ZipLastWriteTimeUtcTicks { get; set; }

            public string ContentRelativePath { get; set; }
        }
    }
}
