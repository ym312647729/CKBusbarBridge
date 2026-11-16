using CKBusbarBridge.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    internal enum BaseModelNameActionKind
    {
        Keep,
        AddDrawingNumber,
        RemoveDrawingNumber
    }

    internal static class BaseModelNamingPolicy
    {
        internal static BaseModelNameActionKind Resolve(
            bool hasDrawingNumberSuffix,
            bool targetIsTemplateDefault)
        {
            if (hasDrawingNumberSuffix)
            {
                return targetIsTemplateDefault
                    ? BaseModelNameActionKind.RemoveDrawingNumber
                    : BaseModelNameActionKind.Keep;
            }

            return targetIsTemplateDefault
                ? BaseModelNameActionKind.Keep
                : BaseModelNameActionKind.AddDrawingNumber;
        }
    }

    /// <summary>
    /// 基座文件名只允许按已确认的完整图号后缀变换。
    /// </summary>
    internal static class BaseModelFileNamePolicy
    {
        internal static string ResolveCurrentSuffix(
            string templateAssemblyBaseName,
            string currentAssemblyBaseName)
        {
            RequireName(templateAssemblyBaseName, nameof(templateAssemblyBaseName));
            RequireName(currentAssemblyBaseName, nameof(currentAssemblyBaseName));

            if (string.Equals(
                templateAssemblyBaseName,
                currentAssemblyBaseName,
                StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (!currentAssemblyBaseName.StartsWith(
                templateAssemblyBaseName + ".",
                StringComparison.OrdinalIgnoreCase))
            {
                throw new FormatException(
                    $"当前基座名称不属于模板根名：模板={templateAssemblyBaseName}，当前={currentAssemblyBaseName}");
            }

            var suffix = currentAssemblyBaseName.Substring(templateAssemblyBaseName.Length);
            ValidateSuffix(suffix, nameof(currentAssemblyBaseName));
            return suffix;
        }

        internal static string ReplaceSuffix(
            string currentBaseName,
            string currentSuffix,
            string targetSuffix)
        {
            RequireName(currentBaseName, nameof(currentBaseName));
            currentSuffix = currentSuffix ?? string.Empty;
            targetSuffix = targetSuffix ?? string.Empty;
            if (currentSuffix.Length > 0)
            {
                ValidateSuffix(currentSuffix, nameof(currentSuffix));
                if (!currentBaseName.EndsWith(
                    currentSuffix,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new FormatException(
                        $"模型名称未以已捕获图号后缀结尾，禁止猜测改名：{currentBaseName}；后缀={currentSuffix}");
                }
            }

            if (targetSuffix.Length > 0)
            {
                ValidateSuffix(targetSuffix, nameof(targetSuffix));
            }

            var rootName = currentSuffix.Length == 0
                ? currentBaseName
                : currentBaseName.Substring(0, currentBaseName.Length - currentSuffix.Length);
            return rootName + targetSuffix;
        }

        private static void ValidateSuffix(string suffix, string parameterName)
        {
            string contractPrefix;
            int serial;
            if (string.IsNullOrWhiteSpace(suffix)
                || suffix[0] != '.'
                || !DrawingNumberHelper.TryParse(
                    suffix.Substring(1),
                    out contractPrefix,
                    out serial))
            {
                throw new FormatException(
                    $"图号文件后缀格式无效：{suffix}（参数：{parameterName}）");
            }
        }

        private static void RequireName(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("模型基础名称不能为空。", parameterName);
            }
        }
    }

    internal sealed class BaseModelNameNormalizationPlan
    {
        internal BaseModelNameNormalizationPlan(
            string modelDirectory,
            string targetModelPath,
            IEnumerable<string> sourceFiles,
            IEnumerable<string> createdFiles,
            IEnumerable<string> obsoleteFiles)
        {
            ModelDirectory = Path.GetFullPath(modelDirectory);
            TargetModelPath = Path.GetFullPath(targetModelPath);
            SourceFiles = Array.AsReadOnly(sourceFiles.Select(Path.GetFullPath).ToArray());
            CreatedFiles = Array.AsReadOnly(createdFiles.Select(Path.GetFullPath).ToArray());
            ObsoleteFiles = Array.AsReadOnly(obsoleteFiles.Select(Path.GetFullPath).ToArray());
        }

        internal string ModelDirectory { get; }

        internal string TargetModelPath { get; }

        internal IReadOnlyList<string> SourceFiles { get; }

        internal IReadOnlyList<string> CreatedFiles { get; }

        internal IReadOnlyList<string> ObsoleteFiles { get; }
    }

    /// <summary>
    /// PackAndGo 前的纯名称规划；所有物理文档必须按同一个已捕获后缀精确变换。
    /// </summary>
    internal static class BaseModelNameNormalizationPolicy
    {
        internal static BaseModelNameNormalizationPlan Create(
            string modelDirectory,
            string sourceModelPath,
            IEnumerable<string> physicalDocumentPaths,
            string currentSuffix,
            string targetSuffix,
            Func<string, bool> targetExists)
        {
            if (physicalDocumentPaths == null)
            {
                throw new ArgumentNullException(nameof(physicalDocumentPaths));
            }

            if (targetExists == null)
            {
                throw new ArgumentNullException(nameof(targetExists));
            }

            var directory = Path.GetFullPath(modelDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var directoryPrefix = directory + Path.DirectorySeparatorChar;
            var sourceRoot = Path.GetFullPath(sourceModelPath);
            var sources = physicalDocumentPaths
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (sources.Length == 0)
            {
                throw new ArgumentException(
                    "PackAndGo 物理文档列表不能为空。",
                    nameof(physicalDocumentPaths));
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources)
            {
                if (!source.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"PackAndGo 文档越出批准模型目录：目录={directory}；文档={source}");
                }

                var targetBaseName = BaseModelFileNamePolicy.ReplaceSuffix(
                    Path.GetFileNameWithoutExtension(source),
                    currentSuffix,
                    targetSuffix);
                var target = Path.GetFullPath(Path.Combine(
                    directory,
                    targetBaseName + Path.GetExtension(source)));
                if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase)
                    && targetExists(target))
                {
                    throw new IOException("名称归一目标文件已存在，禁止覆盖：" + target);
                }

                map[source] = target;
            }

            string targetModelPath;
            if (!map.TryGetValue(sourceRoot, out targetModelPath))
            {
                throw new InvalidOperationException(
                    "PackAndGo 文档列表不包含根基座模型：" + sourceRoot);
            }

            var duplicateTarget = map.Values
                .GroupBy(path => path, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateTarget != null)
            {
                throw new InvalidOperationException(
                    "多个文档归一到同一目标文件：" + duplicateTarget.Key);
            }

            var changed = map
                .Where(pair => !string.Equals(
                    pair.Key,
                    pair.Value,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return new BaseModelNameNormalizationPlan(
                directory,
                targetModelPath,
                sources,
                changed.Select(pair => pair.Value),
                changed.Select(pair => pair.Key));
        }
    }
}
