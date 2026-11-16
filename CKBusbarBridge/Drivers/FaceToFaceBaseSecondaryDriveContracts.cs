using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 普通基座被替换前，与首段基架之间需要跨组件生命周期保留的坐标系配合。
    /// </summary>
    internal sealed class FaceToFaceBaseFirstFrameAnchor
    {
        internal FaceToFaceBaseFirstFrameAnchor(
            string positionName,
            string frameComponentFullName,
            string frameCoordinateName)
        {
            PositionName = RequireText(positionName, nameof(positionName));
            FrameComponentFullName = RequireText(
                frameComponentFullName,
                nameof(frameComponentFullName));
            FrameCoordinateName = RequireText(
                frameCoordinateName,
                nameof(frameCoordinateName));
        }

        internal string PositionName { get; }

        internal string FrameComponentFullName { get; }

        internal string FrameCoordinateName { get; }

        private static string RequireText(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException("首基架锚点字段不能为空。", parameterName);
            }

            return value;
        }
    }

    internal sealed class FaceToFaceBaseNameNormalizationResult
    {
        internal FaceToFaceBaseNameNormalizationResult(
            string modelPath,
            IEnumerable<string> createdFiles,
            IEnumerable<string> obsoleteFiles)
        {
            ModelPath = Path.GetFullPath(modelPath);
            CreatedFiles = Array.AsReadOnly((createdFiles
                ?? throw new ArgumentNullException(nameof(createdFiles)))
                .Select(Path.GetFullPath)
                .ToArray());
            ObsoleteFiles = Array.AsReadOnly((obsoleteFiles
                ?? throw new ArgumentNullException(nameof(obsoleteFiles)))
                .Select(Path.GetFullPath)
                .ToArray());
        }

        internal string ModelPath { get; }

        internal IReadOnlyList<string> CreatedFiles { get; }

        internal IReadOnlyList<string> ObsoleteFiles { get; }
    }

    internal interface IFaceToFaceBaseSecondaryOperations
    {
        FaceToFaceBaseSecondarySnapshot CaptureCurrentSnapshot(
            CabinetDriverParameterSnapshot parameters);

        void SaveBaseline();

        void BackupModelDirectory(string modelPath);

        void RegisterNewDirectory(string directoryPath);

        void RegisterCreatedFiles(IEnumerable<string> filePaths);

        FaceToFaceBaseNameNormalizationResult NormalizeExistingModelNames(
            string modelPath,
            string templateAssemblyPath,
            string targetSuffix);

        FaceToFaceBaseFirstFrameAnchor DetachFirstFrameAnchor(
            FaceToFaceBasePhysicalModelSnapshot ordinaryBase);

        void RestoreFirstFrameAnchor(
            FaceToFaceBaseAssemblyToken ordinaryBase,
            FaceToFaceBaseFirstFrameAnchor anchor);

        void DeletePositioningMates(FaceToFaceBasePhysicalModelSnapshot model);

        void RemoveRootComponent(string componentFullName);

        FaceToFaceBaseAssemblyToken InsertAtRoot(
            string positionName,
            FaceToFaceBaseModelKind kind,
            string modelPath,
            string rootCoordinateName);

        FaceToFaceBaseAssemblyToken InsertBaseOnElevated(
            string positionName,
            string modelPath,
            FaceToFaceBaseAssemblyToken elevated);

        void WriteMapping(string mappingJson);

        FaceToFaceBaseSecondarySnapshot ValidateFinalState(
            CabinetDriverParameterSnapshot parameters);

        void RebuildAndSaveTotalAssembly();

        /// <summary>
        /// 在当前活动总装中刷新基座预拆卸后的内存引用树，不保存中间状态。
        /// </summary>
        void RebuildTotalAssemblyAfterDetach();

        IReadOnlyCollection<string> GetReferencedBaseModelPaths();

        void CloseModelDocument(string modelPath);

        void CloseModelDocuments(string directoryPath);

        void RestoreBaselineAndBackups();

        void ClearTransactionArtifacts();
    }

    /// <summary>
    /// 所有备份、恢复和清理目标都必须严格位于“项目/基座”下，禁止把根目录本身作为单模型目标。
    /// </summary>
    internal static class FaceToFaceBaseTransactionPathGuard
    {
        internal static string EnsureSafeDescendant(string baseRoot, string targetPath)
        {
            return BaseModelTransactionPathGuard.EnsureSafeDescendant(
                baseRoot,
                targetPath);
        }

        /// <summary>
        /// 基座事务还会驱动宿主内的项目级绝缘子；只批准“项目/基座”和“项目/绝缘子”两棵模型树。
        /// 总装、项目根目录及其它业务目录均不允许进入递归备份或清理。
        /// </summary>
        internal static string EnsureSafeProjectModelDescendant(
            string projectRoot,
            string targetPath)
        {
            return BaseModelTransactionPathGuard
                .EnsureSafeProjectModelDescendant(projectRoot, targetPath);
        }

        /// <summary>
        /// 总装可能直接引用模板缓存中的根模型；只额外放行调用方提供的精确模板路径，
        /// 不允许借此访问模板目录中的其它文件。
        /// </summary>
        internal static string EnsureSafeProjectModelOrTemplate(
            string projectRoot,
            string targetPath,
            IEnumerable<string> exactTemplatePaths)
        {
            return BaseModelTransactionPathGuard.EnsureSafeProjectModelOrTemplate(
                projectRoot,
                targetPath,
                exactTemplatePaths);
        }
    }

    internal sealed class FaceToFaceBaseNameNormalizationPlan
    {
        internal FaceToFaceBaseNameNormalizationPlan(
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
    internal static class FaceToFaceBaseNameNormalizationPolicy
    {
        internal static FaceToFaceBaseNameNormalizationPlan Create(
            string modelDirectory,
            string sourceModelPath,
            IEnumerable<string> physicalDocumentPaths,
            string currentSuffix,
            string targetSuffix,
            Func<string, bool> targetExists)
        {
            var sharedPlan = BaseModelNameNormalizationPolicy.Create(
                modelDirectory,
                sourceModelPath,
                physicalDocumentPaths,
                currentSuffix,
                targetSuffix,
                targetExists);
            return new FaceToFaceBaseNameNormalizationPlan(
                sharedPlan.ModelDirectory,
                sharedPlan.TargetModelPath,
                sharedPlan.SourceFiles,
                sharedPlan.CreatedFiles,
                sharedPlan.ObsoleteFiles);
        }
    }
}
