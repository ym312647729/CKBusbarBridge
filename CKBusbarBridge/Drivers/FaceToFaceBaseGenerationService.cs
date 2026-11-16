using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 编排独立基座复制、驱动、复用和装配。面对面与面对墙共用，差异只在位置计划。
    /// </summary>
    internal sealed class FaceToFaceBaseGenerationService
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private readonly Action<string> _closeGeneratedDocuments;
        private readonly IFaceToFaceBaseTemplateCopier _templateCopier;
        private readonly IFaceToFaceBaseDriver _driver;
        private readonly IFaceToFaceBaseInsulatorDriver _insulatorDriver;
        private readonly IFaceToFaceBaseAssembler _assembler;
        private readonly IFaceToFaceBaseDirectoryCleaner _directoryCleaner;

        internal FaceToFaceBaseGenerationService(
            IFaceToFaceBaseTemplateCopier templateCopier,
            IFaceToFaceBaseDriver driver,
            IFaceToFaceBaseAssembler assembler)
            : this(
                templateCopier,
                driver,
                NullFaceToFaceBaseInsulatorDriver.Instance,
                assembler,
                new FaceToFaceBaseDirectoryCleaner())
        {
        }

        internal FaceToFaceBaseGenerationService(
            IFaceToFaceBaseTemplateCopier templateCopier,
            IFaceToFaceBaseDriver driver,
            IFaceToFaceBaseInsulatorDriver insulatorDriver,
            IFaceToFaceBaseAssembler assembler)
            : this(
                templateCopier,
                driver,
                insulatorDriver,
                assembler,
                new FaceToFaceBaseDirectoryCleaner())
        {
        }

        internal FaceToFaceBaseGenerationService(
            IFaceToFaceBaseTemplateCopier templateCopier,
            IFaceToFaceBaseDriver driver,
            IFaceToFaceBaseAssembler assembler,
            IFaceToFaceBaseDirectoryCleaner directoryCleaner,
            Action<string> closeGeneratedDocuments = null)
            : this(
                templateCopier,
                driver,
                NullFaceToFaceBaseInsulatorDriver.Instance,
                assembler,
                directoryCleaner,
                closeGeneratedDocuments)
        {
        }

        internal FaceToFaceBaseGenerationService(
            IFaceToFaceBaseTemplateCopier templateCopier,
            IFaceToFaceBaseDriver driver,
            IFaceToFaceBaseInsulatorDriver insulatorDriver,
            IFaceToFaceBaseAssembler assembler,
            IFaceToFaceBaseDirectoryCleaner directoryCleaner,
            Action<string> closeGeneratedDocuments = null)
        {
            _templateCopier = templateCopier
                ?? throw new ArgumentNullException(nameof(templateCopier));
            _driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _insulatorDriver = insulatorDriver
                ?? throw new ArgumentNullException(nameof(insulatorDriver));
            _assembler = assembler ?? throw new ArgumentNullException(nameof(assembler));
            _directoryCleaner = directoryCleaner
                ?? throw new ArgumentNullException(nameof(directoryCleaner));
            _closeGeneratedDocuments = closeGeneratedDocuments;
        }

        /// <summary>
        /// 生成所有已启用柜孔对应的基座，并装配到当前总装配体。
        /// 同参数模型只在本次调用内缓存，后续位置直接装配同一个磁盘文件。
        /// </summary>
        internal void Generate(
            string projectPath,
            CabinetDriverParameters parameters,
            IDrawingNumberGenerator drawingNumbers,
            Action<string, int, int> reportProgress = null)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            Generate(
                projectPath,
                FaceToFaceBaseDrivePlanner.CreatePlans(parameters),
                drawingNumbers,
                FaceToFaceBaseModelMappingHelper.FaceToFaceOrderedPositions,
                reportProgress);
        }

        /// <summary>
        /// 按已计算的位置计划生成独立基座。面对面和面对墙共用此入口。
        /// </summary>
        internal void Generate(
            string projectPath,
            IReadOnlyList<FaceToFaceBasePositionPlan> positionPlans,
            IDrawingNumberGenerator drawingNumbers,
            IReadOnlyList<string> mappingPositionOrder,
            Action<string, int, int> reportProgress = null)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                throw new ArgumentException("项目路径不能为空。", nameof(projectPath));
            }

            var fullProjectPath = Path.GetFullPath(projectPath);
            if (!Directory.Exists(fullProjectPath))
            {
                throw new DirectoryNotFoundException($"项目目录不存在：{fullProjectPath}");
            }

            if (positionPlans == null)
            {
                throw new ArgumentNullException(nameof(positionPlans));
            }

            if (drawingNumbers == null)
            {
                throw new ArgumentNullException(nameof(drawingNumbers));
            }

            if (mappingPositionOrder == null || mappingPositionOrder.Count == 0)
            {
                throw new ArgumentException("模型对照映射位置列表不能为空。", nameof(mappingPositionOrder));
            }

            var baseRoot = Path.Combine(fullProjectPath, "基座");
            if (File.Exists(baseRoot))
            {
                throw new IOException($"基座输出路径已被文件占用：{baseRoot}");
            }

            var baseRootExisted = Directory.Exists(baseRoot);
            if (baseRootExisted
                && Directory.EnumerateFileSystemEntries(baseRoot).Any())
            {
                throw new IOException(
                    $"基座输出目录不是空目录，无法开始本次首次驱动：{baseRoot}");
            }

            if (positionPlans.Count == 0)
            {
                return;
            }

            var lifecyclePlan =
                FaceToFaceBaseLifecycleAdapter.CreateFirstGenerationPlan(positionPlans);
            var preparedModels = new Dictionary<string, string>(StringComparer.Ordinal);
            var insertedTokens = new List<FaceToFaceBaseAssemblyToken>();
            var completedPositions = 0;
            var totalSteps = positionPlans.Count + 1;
            try
            {
                Directory.CreateDirectory(baseRoot);
                foreach (var positionPlan in positionPlans)
                {
                    reportProgress?.Invoke($"正在生成独立基座及绝缘子 {completedPositions + 1}/{positionPlans.Count}：{positionPlan.PositionName}", completedPositions, totalSteps);
                    FaceToFaceBaseAssemblyToken elevatedBaseToken = null;
                    if (positionPlan.HasElevatedBase)
                    {
                        // 绝缘子若落在增高基座内，则在驱动该增高基座时一并装入。
                        var elevatedBasePath = GetOrCreateModel(
                            lifecyclePlan.ResolveTargetKey(
                                positionPlan.PositionName,
                                FaceToFaceBaseModelKind.ElevatedBase.ToString()),
                            baseRoot,
                            fullProjectPath,
                            positionPlan.PositionName,
                            positionPlan.ElevatedBaseModel,
                            preparedModels,
                            drawingNumbers,
                            positionPlan.Insulator.TargetBaseKind
                                == FaceToFaceBaseModelKind.ElevatedBase
                                    ? positionPlan.Insulator
                                    : null);
                        elevatedBaseToken = _assembler.InsertAtRoot(
                            positionPlan.PositionName,
                            FaceToFaceBaseModelKind.ElevatedBase,
                            elevatedBasePath,
                            positionPlan.RootCoordinateName);
                        insertedTokens.Add(elevatedBaseToken);
                    }

                    // 绝缘子若落在普通基座内，则在驱动该普通基座时一并装入。
                    var basePath = GetOrCreateModel(
                        lifecyclePlan.ResolveTargetKey(
                            positionPlan.PositionName,
                            FaceToFaceBaseModelKind.Base.ToString()),
                        baseRoot,
                        fullProjectPath,
                        positionPlan.PositionName,
                        positionPlan.BaseModel,
                        preparedModels,
                        drawingNumbers,
                        positionPlan.Insulator.TargetBaseKind
                            == FaceToFaceBaseModelKind.Base
                                ? positionPlan.Insulator
                                : null);
                    var baseToken = elevatedBaseToken == null
                        ? _assembler.InsertAtRoot(
                            positionPlan.PositionName,
                            FaceToFaceBaseModelKind.Base,
                            basePath,
                            positionPlan.RootCoordinateName)
                        : _assembler.InsertBaseOnElevated(
                            positionPlan.PositionName,
                            basePath,
                            elevatedBaseToken);
                    insertedTokens.Add(baseToken);

                    // 每个位置成套完成后立即重建保存，尽早暴露坐标系或引用错误。
                    reportProgress?.Invoke($"正在保存独立基座：{positionPlan.PositionName}", completedPositions, totalSteps);
                    _assembler.SaveAndRebuild();
                    completedPositions++;
                }

                // 全部位置装配成功后，写入带实例号的零部件全名对照表，供后续按位置精确读取。
                reportProgress?.Invoke("正在写入基座模型映射", completedPositions, totalSteps);
                var mappingJson = FaceToFaceBaseModelMappingHelper.BuildJson(
                    insertedTokens,
                    mappingPositionOrder);
                _assembler.WriteModelMapping(mappingJson);
                reportProgress?.Invoke("独立基座生成完成", totalSteps, totalSteps);
            }
            catch (Exception originalException)
            {
                Logger.Error(originalException, "独立基座生成失败，开始回滚：{0}", baseRoot);
                CKBusbarBridgePreview.FaceToWallPreviewTrace.Error("独立基座生成失败", originalException, baseRoot);
                try { reportProgress?.Invoke("基座生成失败，正在回滚清理", completedPositions, totalSteps); }
                catch (Exception reportException)
                {
                    Logger.Warn(reportException, "失败进度提示异常，继续回滚基座：{0}", baseRoot);
                }
                var cleanupErrors = RollbackGeneration(
                    baseRoot,
                    baseRootExisted,
                    insertedTokens,
                    originalException is FaceToFaceBaseAssemblyCleanupException);
                if (cleanupErrors.Count == 0)
                {
                    throw;
                }

                throw new InvalidOperationException(
                    originalException.Message
                        + Environment.NewLine
                        + "回滚清理警告："
                        + string.Join("；", cleanupErrors),
                    originalException);
            }
        }

        /// <summary>
        /// 保留旧测试和非 SolidWorks 调用方的兼容入口；生产流程会显式注入真实绝缘子驱动器。
        /// </summary>
        private sealed class NullFaceToFaceBaseInsulatorDriver
            : IFaceToFaceBaseInsulatorDriver
        {
            internal static readonly NullFaceToFaceBaseInsulatorDriver Instance
                = new NullFaceToFaceBaseInsulatorDriver();

            public string PrepareInsulator(
                string projectPath,
                FaceToFaceBaseInsulatorPlan plan,
                double baseWidth)
            {
                return null;
            }

            public void AssembleIntoHostBase(
                string hostBaseAssemblyPath,
                string insulatorAssemblyPath,
                FaceToFaceBaseInsulatorPlan plan)
            {
                // 测试兼容空实现：跳过宿主基座内绝缘子装配。
            }

            public void DriveExistingInsulator(
                string insulatorAssemblyPath,
                FaceToFaceBaseInsulatorPlan plan,
                double baseWidth)
            {
            }

            public void WritePlacement(
                string hostBaseAssemblyPath,
                FaceToFaceBaseInsulatorPlan plan)
            {
            }
        }

        private string GetOrCreateModel(
            string targetKey,
            string baseRoot,
            string projectPath,
            string positionName,
            FaceToFaceBaseModelPlan modelPlan,
            IDictionary<string, string> modelCache,
            IDrawingNumberGenerator drawingNumbers,
            FaceToFaceBaseInsulatorPlan hostInsulatorPlan)
        {
            string cachedPath;
            if (modelCache.TryGetValue(targetKey, out cachedPath))
            {
                // 复用时不复制现有模型，直接返回首次驱动模型的原始磁盘路径。
                // 首次驱动已按缓存签名把绝缘子装入宿主基座。
                return cachedPath;
            }

            var preparedModelPath = FaceToFaceBaseModelPreparation.PrepareNewModel(
                _templateCopier,
                _driver,
                _insulatorDriver,
                baseRoot,
                projectPath,
                positionName,
                modelPlan,
                drawingNumbers,
                hostInsulatorPlan);

            modelCache.Add(targetKey, preparedModelPath);
            return preparedModelPath;
        }

        private IReadOnlyList<string> RollbackGeneration(
            string baseRoot,
            bool baseRootExisted,
            IReadOnlyList<FaceToFaceBaseAssemblyToken> insertedTokens,
            bool insertedComponentCleanupUnconfirmed)
        {
            var cleanupErrors = new List<string>();
            var assemblyRollbackConfirmed = !insertedComponentCleanupUnconfirmed;
            if (insertedComponentCleanupUnconfirmed)
            {
                cleanupErrors.Add("失败步骤中已插入组件的即时清理未得到确认。");
            }

            if (insertedTokens.Count > 0)
            {
                try
                {
                    // 子装配按插入相反顺序删除，避免先删父级导致后续组件句柄失效。
                    _assembler.Rollback(insertedTokens.Reverse().ToArray());
                }
                catch (Exception exception)
                {
                    assemblyRollbackConfirmed = false;
                    cleanupErrors.Add("总装配体回滚失败：" + exception.Message);
                }
            }

            if (!assemblyRollbackConfirmed)
            {
                // 若组件删除未确认，删除磁盘模型会让总装配体残留组件指向不存在的文件。
                cleanupErrors.Add(
                    $"为保护残留组件引用，已保留基座目录，请人工检查总装配体后处理：{baseRoot}");
                return cleanupErrors;
            }

            if (Directory.Exists(baseRoot))
            {
                try
                {
                    _closeGeneratedDocuments?.Invoke(baseRoot);
                    Exception cleanupError;
                    if (!_directoryCleaner.TryDelete(baseRoot, out cleanupError))
                    {
                        Logger.Error(cleanupError, "基座回滚目录删除失败：{0}", baseRoot);
                        if (cleanupError != null) CKBusbarBridgePreview.FaceToWallPreviewTrace.Error("基座目录清理失败", cleanupError, baseRoot);
                        cleanupErrors.Add(
                            "基座目录清理失败："
                            + (cleanupError?.Message ?? "未知清理错误"));
                        return cleanupErrors;
                    }
                }
                catch (Exception cleanupError)
                {
                    Logger.Error(cleanupError, "基座回滚关闭或目录清理失败，保留目录：{0}", baseRoot);
                    CKBusbarBridgePreview.FaceToWallPreviewTrace.Error("基座回滚清理失败", cleanupError, baseRoot);
                    // 回滚阶段不得用清理异常覆盖最初的业务异常。
                    cleanupErrors.Add("基座目录清理失败：" + cleanupError.Message);
                    return cleanupErrors;
                }
            }

            try
            {
                if (baseRootExisted)
                {
                    Directory.CreateDirectory(baseRoot);
                }
            }
            catch (Exception exception)
            {
                cleanupErrors.Add("基座空目录恢复失败：" + exception.Message);
            }

            return cleanupErrors;
        }
    }
}
