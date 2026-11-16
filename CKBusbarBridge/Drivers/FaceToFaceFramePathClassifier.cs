using CKBusbarBridge.Models;
using System;
using System.IO;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 面对面/面对墙基架物理模板来源与路径边界辅助规则。
    /// 面对墙额外允许大基架图号 5GX.355.026 和 5GX.355.027。
    /// </summary>
    internal static class FaceToFaceFramePathClassifier
    {
        private const string FrameDrawingNumberPrefix = "5GX.355.024";
        private const string SmallOffsetFrameDrawingNumberPrefix = "5GX.355.032";
        private const string Large1FrameDrawingNumberPrefix = "5GX.355.026";
        private const string Large2FrameDrawingNumberPrefix = "5GX.355.027";
        private const string FaceToFaceLargeOffsetDrawingNumberPrefix = "5GX.355.028";
        private const string SideToSidePhaseChangeDrawingNumberPrefix = "5GX.355.029";
        private const string AssemblyExtension = ".SLDASM";

        internal static bool TryGetFrameSourceName(
            string projectPath,
            string modelPath,
            out string sourceName,
            out string error)
        {
            bool useLargeFrameTemplate;
            return TryGetFrameSourceName(
                projectPath,
                modelPath,
                false,
                out sourceName,
                out useLargeFrameTemplate,
                out error);
        }

        internal static bool TryGetFrameSourceName(
            string projectPath,
            string modelPath,
            bool allowLargeFrame,
            out string sourceName,
            out bool useLargeFrameTemplate,
            out string error)
        {
            var accepted = TryGetFrameSourceName(
                projectPath,
                modelPath,
                allowLargeFrame,
                out sourceName,
                out FaceToWallFrameTemplateKind templateKind,
                out error);
            useLargeFrameTemplate = templateKind != FaceToWallFrameTemplateKind.Regular;
            return accepted;
        }

        internal static bool TryGetFrameSourceName(
            string projectPath,
            string modelPath,
            bool allowLargeFrame,
            out string sourceName,
            out FaceToWallFrameTemplateKind templateKind,
            out string error)
        {
            // 旧布尔入口只用于现有调用兼容；实际准入统一转换为明确设计场景。
            return TryGetFrameSourceName(
                projectPath,
                modelPath,
                allowLargeFrame ? DesignScenarioId.FaceToWall : DesignScenarioId.FaceToFace,
                out sourceName,
                out templateKind,
                out error);
        }

        internal static bool TryGetFrameSourceName(
            string projectPath,
            string modelPath,
            DesignScenarioId scenarioId,
            out string sourceName,
            out FaceToWallFrameTemplateKind templateKind,
            out string error)
        {
            return TryGetFrameSourceName(
                projectPath,
                modelPath,
                scenarioId,
                null,
                out sourceName,
                out templateKind,
                out error);
        }

        internal static bool TryGetFrameSourceName(
            string projectPath,
            string modelPath,
            DesignScenarioId scenarioId,
            SideToSideFrameBaseKind? sideToSideBaseKind,
            out string sourceName,
            out FaceToWallFrameTemplateKind templateKind,
            out string error)
        {
            sourceName = null;
            templateKind = FaceToWallFrameTemplateKind.Regular;
            error = null;

            if (scenarioId == DesignScenarioId.SideToSide
                && !sideToSideBaseKind.HasValue)
            {
                error = "侧对侧基架路径识别缺少基座类型。";
                return false;
            }

            if (!TryGetFullPath(projectPath, "项目目录", out var fullProjectPath, out error)
                || !TryGetFullPath(modelPath, "模型路径", out var fullModelPath, out error))
            {
                return false;
            }

            var frameRoot = Path.Combine(fullProjectPath, "基架");
            if (!TryGetRelativePathInside(frameRoot, fullModelPath, out var relativePath))
            {
                error = $"基架模型必须位于项目基架目录内：{fullModelPath}";
                return false;
            }

            var pathParts = relativePath.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);
            if (pathParts.Length < 2)
            {
                error = $"基架模型路径缺少 SourceName 一级目录：{fullModelPath}";
                return false;
            }

            if (pathParts.Length > 2)
            {
                error = $"基架装配体必须直接位于 SourceName 一级目录下：{fullModelPath}";
                return false;
            }

            var isRegularFrame = IsAssemblyWithPrefix(pathParts[1], FrameDrawingNumberPrefix);
            var isSmallOffsetFrame = IsAssemblyWithPrefix(
                pathParts[1],
                SmallOffsetFrameDrawingNumberPrefix);
            var isLarge1Frame = IsAssemblyWithPrefix(pathParts[1], Large1FrameDrawingNumberPrefix);
            var isLarge2Frame = IsAssemblyWithPrefix(pathParts[1], Large2FrameDrawingNumberPrefix);
            var isFaceToFaceLargeOffsetFrame = IsAssemblyWithPrefix(
                pathParts[1],
                FaceToFaceLargeOffsetDrawingNumberPrefix);
            var isSideToSidePhaseChangeFrame = IsAssemblyWithPrefix(
                pathParts[1],
                SideToSidePhaseChangeDrawingNumberPrefix);
            bool isAllowedByScenario;
            if (scenarioId == DesignScenarioId.FaceToWall)
            {
                isAllowedByScenario = isRegularFrame || isLarge1Frame || isLarge2Frame;
            }
            else if (scenarioId == DesignScenarioId.SideToSide)
            {
                isAllowedByScenario = sideToSideBaseKind == SideToSideFrameBaseKind.Base1
                    ? isSideToSidePhaseChangeFrame
                    : isRegularFrame || isSmallOffsetFrame || isFaceToFaceLargeOffsetFrame;
            }
            else
            {
                isAllowedByScenario = isRegularFrame
                    || isSmallOffsetFrame
                    || isFaceToFaceLargeOffsetFrame;
            }
            if (!isAllowedByScenario)
            {
                if (scenarioId == DesignScenarioId.FaceToWall)
                {
                    error = $"基架装配体文件名必须匹配 {FrameDrawingNumberPrefix}*.SLDASM、"
                        + $"{Large1FrameDrawingNumberPrefix}*.SLDASM 或 {Large2FrameDrawingNumberPrefix}*.SLDASM：{fullModelPath}";
                }
                else if (scenarioId == DesignScenarioId.SideToSide
                    && sideToSideBaseKind == SideToSideFrameBaseKind.Base1)
                {
                    error = $"侧对侧基座1基架装配体文件名必须匹配 "
                        + $"{SideToSidePhaseChangeDrawingNumberPrefix}*.SLDASM：{fullModelPath}";
                }
                else
                {
                    error = $"基架装配体文件名必须匹配 {FrameDrawingNumberPrefix}*.SLDASM、"
                        + $"{SmallOffsetFrameDrawingNumberPrefix}*.SLDASM 或 "
                        + $"{FaceToFaceLargeOffsetDrawingNumberPrefix}*.SLDASM：{fullModelPath}";
                }
                return false;
            }

            // SourceName 与文件名两项必须同时来自实际模型路径，不能由旧属性或组件实例号补齐。
            sourceName = pathParts[0];
            templateKind = isFaceToFaceLargeOffsetFrame
                ? FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner
                : isLarge2Frame
                ? FaceToWallFrameTemplateKind.Large2
                : isLarge1Frame
                    ? FaceToWallFrameTemplateKind.Large1
                    : FaceToWallFrameTemplateKind.Regular;
            return true;
        }

        internal static bool IsLargeFrameAssemblyFileName(string modelPath)
        {
            return TryGetFrameTemplateKindFromAssemblyFileName(modelPath, out var templateKind)
                && templateKind != FaceToWallFrameTemplateKind.Regular;
        }

        // 检查已有目录时允许识别所有模板，不能按本次目标场景过滤掉待替换的旧模板。
        internal static bool TryGetStoredTemplateIdentity(string modelPath, out FrameTemplateIdentity identity)
        {
            var fileName = Path.GetFileName(modelPath);
            var prefixes = new[] { FrameDrawingNumberPrefix, SmallOffsetFrameDrawingNumberPrefix,
                FaceToFaceLargeOffsetDrawingNumberPrefix, Large1FrameDrawingNumberPrefix,
                Large2FrameDrawingNumberPrefix, SideToSidePhaseChangeDrawingNumberPrefix };
            var identities = new[] { FrameTemplateIdentity.Regular024, FrameTemplateIdentity.FaceToFaceSmallOffset032,
                FrameTemplateIdentity.FaceToFaceLargeOffsetCorner028, FrameTemplateIdentity.FaceToWallLarge1,
                FrameTemplateIdentity.FaceToWallLarge2, FrameTemplateIdentity.SideToSidePhaseChange029 };
            for (var index = 0; index < prefixes.Length; index++)
            {
                if (IsAssemblyWithPrefix(fileName, prefixes[index]))
                {
                    identity = identities[index];
                    return true;
                }
            }

            identity = default(FrameTemplateIdentity);
            return false;
        }

        internal static FrameTemplateIdentity ResolveStoredTemplateIdentity(string modelPath, string propertyValue)
        {
            if (!TryGetStoredTemplateIdentity(modelPath, out var fileIdentity))
                throw new InvalidOperationException("无法识别已有基架主模型：" + modelPath);
            // 无属性的历史模型按图号识别；显式属性有误时禁止用文件名掩盖冲突。
            if (string.IsNullOrWhiteSpace(propertyValue)) return fileIdentity;
            if (!FrameTemplateIdentityMetadata.TryParse(propertyValue, out var identity) || identity != fileIdentity)
                throw new InvalidOperationException("已有基架模板属性与文件身份不一致：" + modelPath);
            return identity;
        }

        internal static bool TryGetFrameTemplateKindFromAssemblyFileName(
            string modelPath,
            out FaceToWallFrameTemplateKind templateKind)
        {
            templateKind = FaceToWallFrameTemplateKind.Regular;
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                return false;
            }

            try
            {
                var fileName = Path.GetFileName(modelPath);
                if (IsAssemblyWithPrefix(fileName, Large2FrameDrawingNumberPrefix))
                {
                    templateKind = FaceToWallFrameTemplateKind.Large2;
                    return true;
                }

                if (IsAssemblyWithPrefix(fileName, Large1FrameDrawingNumberPrefix))
                {
                    templateKind = FaceToWallFrameTemplateKind.Large1;
                    return true;
                }

                if (IsAssemblyWithPrefix(fileName, FaceToFaceLargeOffsetDrawingNumberPrefix))
                {
                    templateKind = FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner;
                    return true;
                }

                return IsAssemblyWithPrefix(fileName, FrameDrawingNumberPrefix)
                    || IsAssemblyWithPrefix(fileName, SmallOffsetFrameDrawingNumberPrefix);
            }
            catch (Exception exception)
                when (exception is ArgumentException
                    || exception is NotSupportedException
                    || exception is PathTooLongException)
            {
                return false;
            }
        }

        /// <summary>
        /// 根据设计树组件名称识别基架模板类型，不依赖组件对应文件的存储路径。
        /// </summary>
        internal static bool TryGetFrameTemplateKindFromComponentName(
            string componentName,
            out FaceToWallFrameTemplateKind templateKind)
        {
            templateKind = FaceToWallFrameTemplateKind.Regular;
            if (string.IsNullOrWhiteSpace(componentName))
            {
                return false;
            }

            var leafName = componentName.Trim();
            var hierarchySeparatorIndex = leafName.LastIndexOf('/');
            if (hierarchySeparatorIndex >= 0 && hierarchySeparatorIndex < leafName.Length - 1)
            {
                leafName = leafName.Substring(hierarchySeparatorIndex + 1);
            }

            if (leafName.StartsWith(Large2FrameDrawingNumberPrefix, StringComparison.OrdinalIgnoreCase))
            {
                templateKind = FaceToWallFrameTemplateKind.Large2;
                return true;
            }

            if (leafName.StartsWith(Large1FrameDrawingNumberPrefix, StringComparison.OrdinalIgnoreCase))
            {
                templateKind = FaceToWallFrameTemplateKind.Large1;
                return true;
            }

            if (leafName.StartsWith(FaceToFaceLargeOffsetDrawingNumberPrefix, StringComparison.OrdinalIgnoreCase))
            {
                templateKind = FaceToWallFrameTemplateKind.FaceToFaceLargeOffsetCorner;
                return true;
            }

            return leafName.StartsWith(FrameDrawingNumberPrefix, StringComparison.OrdinalIgnoreCase)
                || leafName.StartsWith(SmallOffsetFrameDrawingNumberPrefix, StringComparison.OrdinalIgnoreCase)
                || leafName.StartsWith(SideToSidePhaseChangeDrawingNumberPrefix, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsPathUnderDirectory(string directoryPath, string candidatePath)
        {
            if (!TryGetFullPath(directoryPath, "目录", out var fullDirectoryPath, out _)
                || !TryGetFullPath(candidatePath, "模型路径", out var fullCandidatePath, out _))
            {
                return false;
            }

            return TryGetRelativePathInside(fullDirectoryPath, fullCandidatePath, out _);
        }

        private static bool IsAssemblyWithPrefix(string fileName, string prefix)
        {
            return !string.IsNullOrWhiteSpace(fileName)
                && string.Equals(Path.GetExtension(fileName), AssemblyExtension, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileNameWithoutExtension(fileName).StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryGetRelativePathInside(
            string directoryPath,
            string candidatePath,
            out string relativePath)
        {
            relativePath = null;
            var normalizedRoot = EnsureTrailingSeparator(Path.GetFullPath(directoryPath));
            var normalizedCandidate = Path.GetFullPath(candidatePath);
            if (!normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 尾部分隔符用于阻止“基架-old”一类字符串前缀越过真实目录边界。
            relativePath = normalizedCandidate.Substring(normalizedRoot.Length);
            return !string.IsNullOrWhiteSpace(relativePath);
        }

        private static string EnsureTrailingSeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                || path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                    ? path
                    : path + Path.DirectorySeparatorChar;
        }

        private static bool TryGetFullPath(
            string path,
            string displayName,
            out string fullPath,
            out string error)
        {
            fullPath = null;
            error = null;
            if (string.IsNullOrWhiteSpace(path))
            {
                error = displayName + "不能为空。";
                return false;
            }

            try
            {
                fullPath = Path.GetFullPath(path.Trim());
                return true;
            }
            catch (Exception exception)
                when (exception is ArgumentException
                    || exception is NotSupportedException
                    || exception is PathTooLongException)
            {
                error = displayName + "无效：" + exception.Message;
                return false;
            }
        }
    }
}
