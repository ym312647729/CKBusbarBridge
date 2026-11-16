using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using CKBusbarBridge.Prototype;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 首次生成面对面式链路基架：先关闭总装并批量准备全部物理模型，
    /// 再重新打开总装，按界面位置集中装配。
    /// </summary>
    internal sealed class FaceToFaceFrameGenerationService
    {
        private const string FrameRootDirectoryName = "基架";
        private readonly IFaceToFaceFrameModelPreparationService _modelPreparationService;
        private readonly IFaceToFaceFrameInitialGenerationSession _generationSession;
        private readonly IFaceToFaceBaseDirectoryCleaner _directoryCleaner;

        internal FaceToFaceFrameGenerationService(
            string templateAssemblyPath,
            IFaceToFaceFrameDriver driver,
            IFaceToFaceFrameAssembler assembler)
            : this(
                templateAssemblyPath,
                driver,
                NullFaceToFaceFrameInsulatorDriver.Instance,
                assembler,
                new FaceToFaceBaseDirectoryCleaner())
        {
        }

        internal FaceToFaceFrameGenerationService(
            string templateAssemblyPath,
            IFaceToFaceFrameDriver driver,
            IFaceToFaceFrameAssembler assembler,
            IFaceToFaceBaseDirectoryCleaner directoryCleaner)
            : this(
                templateAssemblyPath,
                driver,
                NullFaceToFaceFrameInsulatorDriver.Instance,
                assembler,
                directoryCleaner)
        {
        }

        internal FaceToFaceFrameGenerationService(
            string templateAssemblyPath,
            IFaceToFaceFrameDriver driver,
            IFaceToFaceFrameInsulatorDriver insulatorDriver,
            IFaceToFaceFrameAssembler assembler)
            : this(
                templateAssemblyPath,
                driver,
                insulatorDriver,
                assembler,
                new FaceToFaceBaseDirectoryCleaner())
        {
        }

        internal FaceToFaceFrameGenerationService(
            Func<FaceToFaceFrameModelPlan, string> resolveTemplateAssemblyPath,
            IFaceToFaceFrameDriver driver,
            IFaceToFaceFrameAssembler assembler)
            : this(
                resolveTemplateAssemblyPath,
                driver,
                NullFaceToFaceFrameInsulatorDriver.Instance,
                assembler,
                new FaceToFaceBaseDirectoryCleaner())
        {
        }

        internal FaceToFaceFrameGenerationService(
            Func<FaceToFaceFrameModelPlan, string> resolveTemplateAssemblyPath,
            IFaceToFaceFrameDriver driver,
            IFaceToFaceFrameAssembler assembler,
            IFaceToFaceBaseDirectoryCleaner directoryCleaner)
            : this(
                resolveTemplateAssemblyPath,
                driver,
                NullFaceToFaceFrameInsulatorDriver.Instance,
                assembler,
                directoryCleaner)
        {
        }

        internal FaceToFaceFrameGenerationService(
            string templateAssemblyPath,
            IFaceToFaceFrameDriver driver,
            IFaceToFaceFrameInsulatorDriver insulatorDriver,
            IFaceToFaceFrameAssembler assembler,
            IFaceToFaceBaseDirectoryCleaner directoryCleaner)
            : this(
                CreateFixedTemplateResolver(templateAssemblyPath),
                driver,
                insulatorDriver,
                assembler,
                directoryCleaner)
        {
        }

        internal FaceToFaceFrameGenerationService(
            Func<FaceToFaceFrameModelPlan, string> resolveTemplateAssemblyPath,
            IFaceToFaceFrameDriver driver,
            IFaceToFaceFrameInsulatorDriver insulatorDriver,
            IFaceToFaceFrameAssembler assembler,
            IFaceToFaceBaseDirectoryCleaner directoryCleaner)
            : this(
                new FaceToFaceFrameModelPreparationService(
                    resolveTemplateAssemblyPath,
                    driver,
                    insulatorDriver),
                assembler,
                directoryCleaner)
        {
        }

        internal FaceToFaceFrameGenerationService(
            IFaceToFaceFrameModelPreparationService modelPreparationService,
            IFaceToFaceFrameAssembler assembler,
            IFaceToFaceBaseDirectoryCleaner directoryCleaner)
            : this(
                modelPreparationService,
                new LegacyOpenTotalAssemblySession(assembler),
                directoryCleaner)
        {
        }

        internal FaceToFaceFrameGenerationService(
            IFaceToFaceFrameModelPreparationService modelPreparationService,
            IFaceToFaceFrameInitialGenerationSession generationSession,
            IFaceToFaceBaseDirectoryCleaner directoryCleaner)
        {
            _modelPreparationService = modelPreparationService
                ?? throw new ArgumentNullException(nameof(modelPreparationService));
            _generationSession = generationSession
                ?? throw new ArgumentNullException(nameof(generationSession));
            _directoryCleaner = directoryCleaner
                ?? throw new ArgumentNullException(nameof(directoryCleaner));
        }

        internal IReadOnlyList<FaceToFaceFrameAssemblyToken> Generate(
            string projectPath,
            IEnumerable<FaceToFaceFramePlanInput> inputs,
            string bCabinetBasePositionName,
            IDrawingNumberGenerator drawingNumbers,
            FaceToFaceFrameInsulatorParameters insulatorParameters = null)
        {
            // 纯计算规划必须先于任何路径规范化、存在性检查或目录枚举。
            var positionPlans = FaceToFaceFrameDrivePlanner.CreatePlans(inputs);
            return GeneratePrepared(
                projectPath,
                positionPlans,
                bCabinetBasePositionName,
                drawingNumbers,
                insulatorParameters);
        }

        /// <summary>
        /// 按已经算好的位置计划先批量生成物理模型，再集中装配。
        /// 调用方必须先完成规划，不得在本方法前接触磁盘。
        /// </summary>
        internal IReadOnlyList<FaceToFaceFrameAssemblyToken> GeneratePrepared(
            string projectPath,
            IReadOnlyList<FaceToFaceFramePositionPlan> positionPlans,
            string ordinaryBasePositionName,
            IDrawingNumberGenerator drawingNumbers,
            FaceToFaceFrameInsulatorParameters insulatorParameters = null,
            Action<string, int, int> reportProgress = null)
        {
            if (positionPlans == null)
            {
                throw new ArgumentNullException(nameof(positionPlans));
            }

            // 数量为 0 的基架完全绕过绝缘子流程；只要任一基架需要绝缘子，
            // 就必须在接触磁盘或 SolidWorks 前完成公共驱动参数校验。
            if (positionPlans.Any(plan => plan.ModelPlan.InsulatorCount > 0)
                && insulatorParameters == null)
            {
                throw new ArgumentNullException(
                    nameof(insulatorParameters),
                    "存在需要装配绝缘子的基架，但未提供绝缘子驱动参数。");
            }

            if (string.IsNullOrWhiteSpace(projectPath))
            {
                throw new ArgumentException("项目路径不能为空。", nameof(projectPath));
            }

            if (string.IsNullOrWhiteSpace(ordinaryBasePositionName))
            {
                throw new ArgumentException("B柜普通基座位置名称不能为空。", nameof(ordinaryBasePositionName));
            }

            if (drawingNumbers == null)
            {
                throw new ArgumentNullException(nameof(drawingNumbers));
            }

            var fullProjectPath = Path.GetFullPath(projectPath);
            if (!Directory.Exists(fullProjectPath))
            {
                throw new DirectoryNotFoundException($"项目目录不存在：{fullProjectPath}");
            }

            if (positionPlans.Count == 0)
            {
                return new List<FaceToFaceFrameAssemblyToken>();
            }

            var frameRoot = Path.Combine(fullProjectPath, FrameRootDirectoryName);
            if (File.Exists(frameRoot))
            {
                throw new IOException($"基架输出路径已被文件占用：{frameRoot}");
            }

            var frameRootExisted = Directory.Exists(frameRoot);
            if (frameRootExisted
                && Directory.EnumerateFileSystemEntries(frameRoot).Any())
            {
                throw new IOException(
                    $"基架输出目录不是空目录，无法开始本次首次生成：{frameRoot}");
            }

            // 在关闭总装配体和创建基架目录之前预检本次涉及的全部物理模板。
            // 模板缺失属于输入部署错误，不应进入需要回滚 SolidWorks 会话的事务阶段。
            foreach (var modelPlan in positionPlans
                .Select(position => position.ModelPlan)
                .GroupBy(plan => plan.TemplateIdentity)
                .Select(group => group.First()))
            {
                _modelPreparationService.ResolveAndValidateTemplateAssemblyPath(modelPlan);
            }

            // 在关闭总装和创建磁盘目录前读取映射，失败时才能恢复调用前状态。
            var originalMapping = _generationSession.CaptureModelMapping();
            var preparedModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var preparedInsulators = new Dictionary<string, string>(StringComparer.Ordinal);
            var preparedPositions = new List<Tuple<FaceToFaceFramePositionPlan, string>>();
            var insertedTokens = new List<FaceToFaceFrameAssemblyToken>();
            var cleanupConfirmationRequiredForCurrentOperation = false;
            var physicalModelCount = positionPlans.Select(position => position.SourceName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var totalSteps = physicalModelCount + positionPlans.Count + 3;
            var completedSteps = 0;
            var physicalModelsPrepared = false;
            var assemblyPhaseStarted = false;
            IFaceToFaceFrameAssembler assembler = null;
            try
            {
                // 总装下的子件可能与基架模板引用同标题。首次生成期间先关闭总装，
                // 所有物理基架完成后再重新打开；这里不扫描或比较模板依赖名称。
                reportProgress?.Invoke("正在保存并关闭总装配体", completedSteps, totalSteps);
                _generationSession.SaveAndCloseTotalAssembly();
                completedSteps++;
                Directory.CreateDirectory(frameRoot);
                foreach (var positionPlan in positionPlans)
                {
                    var modelPlan = positionPlan.ModelPlan;
                    string modelPath;
                    // SourceName 是物理模型身份：同名位置复用首件，
                    // 不同名称即使参数完全相同也要分别准备。
                    var firstInstanceOfSource = !preparedModels.TryGetValue(
                        modelPlan.SourceName,
                        out modelPath);
                    if (firstInstanceOfSource)
                    {
                        reportProgress?.Invoke($"正在准备基架及绝缘子 {preparedModels.Count + 1}/{physicalModelCount}：{modelPlan.SourceName}", completedSteps, totalSteps);
                        cleanupConfirmationRequiredForCurrentOperation = true;
                        modelPath = _modelPreparationService.PrepareNewModel(
                            fullProjectPath,
                            frameRoot,
                            modelPlan,
                            drawingNumbers,
                            insulatorParameters,
                            preparedInsulators);

                        EnsurePreparedModelDocumentsClosed(modelPath);
                        preparedModels.Add(modelPlan.SourceName, modelPath);
                        completedSteps++;
                        cleanupConfirmationRequiredForCurrentOperation = false;
                    }

                    preparedPositions.Add(Tuple.Create(positionPlan, modelPath));
                }

                physicalModelsPrepared = true;
                reportProgress?.Invoke("正在重新打开总装配体", completedSteps, totalSteps);
                assembler = _generationSession.ReopenTotalAssembly();
                completedSteps++;
                assemblyPhaseStarted = true;
                foreach (var preparedPosition in preparedPositions)
                {
                    var positionPlan = preparedPosition.Item1;
                    reportProgress?.Invoke($"正在装配基架 {insertedTokens.Count + 1}/{preparedPositions.Count}：{positionPlan.SourceName}", completedSteps, totalSteps);
                    var modelPath = preparedPosition.Item2;
                    cleanupConfirmationRequiredForCurrentOperation = true;
                    var token = insertedTokens.Count == 0
                        ? assembler.InsertFirstFrame(
                            positionPlan,
                            modelPath,
                            ordinaryBasePositionName)
                        : assembler.InsertChainedFrame(
                            positionPlan,
                            modelPath,
                            insertedTokens[insertedTokens.Count - 1]);
                    cleanupConfirmationRequiredForCurrentOperation = false;

                    insertedTokens.Add(token);
                    completedSteps++;
                }

                reportProgress?.Invoke("正在完成配合、写入基架映射并保存", completedSteps, totalSteps);
                assembler.Complete(insertedTokens, originalMapping);
                reportProgress?.Invoke("基架装配保存完成", ++completedSteps, totalSteps);
                return insertedTokens;
            }
            catch (Exception originalException) when (!(originalException is FrameDriveInspectionRequiredException))
            {
                CKBusbarBridgePreview.FaceToWallPreviewTrace.Error("基架生成失败，开始恢复前", originalException, frameRoot);
                reportProgress?.Invoke("基架生成失败，正在恢复总装并清理", completedSteps, totalSteps);
                var failedOperationCleanupConfirmed = true;
                if (cleanupConfirmationRequiredForCurrentOperation)
                {
                    failedOperationCleanupConfirmed =
                        FaceToFaceFrameCleanupDiagnostics.TryGetCleanupConfirmation(
                            originalException,
                            out var explicitConfirmation)
                        && explicitConfirmation;
                }

                var preservePreparedRoot = false;
                if (assembler == null)
                {
                    try
                    {
                        assembler = _generationSession.ReopenTotalAssembly();
                    }
                    catch (Exception reopenException)
                    {
                        var warning = physicalModelsPrepared
                            ? "全部基架已生成，但重新打开原总装配体失败："
                                + reopenException.Message
                            : "基架生成失败后重新打开原总装配体失败："
                                + reopenException.Message;
                        FaceToFaceFrameCleanupDiagnostics.AppendData(
                            originalException,
                            FaceToFaceFrameCleanupDiagnostics.TotalAssemblyRestoreWarningDataKey,
                            warning);
                        preservePreparedRoot = physicalModelsPrepared;
                    }
                }

                var cleanupWarnings = RollbackGeneration(
                    frameRoot,
                    frameRootExisted,
                    insertedTokens,
                    originalMapping,
                    failedOperationCleanupConfirmed,
                    assembler,
                    assemblyPhaseStarted,
                    preservePreparedRoot);
                AttachCleanupWarnings(originalException, cleanupWarnings);

                try
                {
                    _generationSession.EnsureTotalAssemblyOpen();
                }
                catch (Exception restoreException)
                {
                    FaceToFaceFrameCleanupDiagnostics.AppendData(
                        originalException,
                        FaceToFaceFrameCleanupDiagnostics.TotalAssemblyRestoreWarningDataKey,
                        "恢复原总装配体失败：" + restoreException.Message);
                }

                throw;
            }
        }

        private void EnsurePreparedModelDocumentsClosed(string modelPath)
        {
            var modelDirectory = Path.GetDirectoryName(Path.GetFullPath(modelPath));
            FaceToFaceFrameCleanupConfirmation confirmation;
            try
            {
                confirmation = _generationSession.CloseDocumentsUnderDirectory(modelDirectory);
            }
            catch (Exception cleanupException)
            {
                confirmation = FaceToFaceFrameCleanupConfirmation.Unconfirmed(
                    "关闭已生成基架文档时发生异常：" + cleanupException.Message);
            }

            if (confirmation != null && confirmation.IsConfirmed)
            {
                return;
            }

            var exception = new InvalidOperationException(
                "已生成基架文档关闭失败或未得到确认：" + modelPath
                    + System.Environment.NewLine
                    + (confirmation?.Diagnostic ?? "未返回文档清理确认。"));
            FaceToFaceFrameCleanupDiagnostics.AttachCleanupConfirmation(
                exception,
                confirmation,
                modelDirectory,
                "批量生成阶段的基架文档关闭");
            throw exception;
        }

        private IReadOnlyDictionary<string, string> RollbackGeneration(
            string frameRoot,
            bool frameRootExisted,
            IReadOnlyList<FaceToFaceFrameAssemblyToken> insertedTokens,
            string originalMapping,
            bool failedOperationCleanupConfirmed,
            IFaceToFaceFrameAssembler assembler,
            bool assemblyPhaseStarted,
            bool preservePreparedRoot)
        {
            var cleanupWarnings = new Dictionary<string, string>();
            if (preservePreparedRoot)
            {
                var warning = $"全部物理基架已生成，因总装配体未能重新打开，已保留基架目录：{frameRoot}";
                cleanupWarnings[
                    FaceToFaceFrameCleanupDiagnostics.UnconfirmedCleanupWarningDataKey]
                    = warning;
                cleanupWarnings[FaceToFaceFrameCleanupDiagnostics.RetainedPathDataKey]
                    = Path.GetFullPath(frameRoot);
                LogRetainedFrameRoot(frameRoot, warning);
                return cleanupWarnings;
            }

            var assemblyCleanupConfirmed = failedOperationCleanupConfirmed;
            if (assemblyPhaseStarted)
            {
                if (assembler == null)
                {
                    assemblyCleanupConfirmed = false;
                    var warning = "总装配体未重新打开，无法回滚本次装配。";
                    cleanupWarnings[FaceToFaceFrameCleanupDiagnostics.RollbackWarningDataKey]
                        = warning;
                    LogRetainedFrameRoot(frameRoot, warning);
                }
                else
                {
                    try
                    {
                        assembler.Rollback(insertedTokens.Reverse().ToArray(), originalMapping);
                    }
                    catch (Exception rollbackException)
                    {
                        assemblyCleanupConfirmed = false;
                        var warning = "总装配体回滚未得到确认：" + rollbackException.Message;
                        cleanupWarnings[FaceToFaceFrameCleanupDiagnostics.RollbackWarningDataKey]
                            = warning;
                        LogRetainedFrameRoot(frameRoot, warning);
                    }
                }
            }

            if (!assemblyCleanupConfirmed)
            {
                var warning = $"组件或文档清理未得到确认，已保留完整基架目录：{frameRoot}";
                cleanupWarnings[
                    FaceToFaceFrameCleanupDiagnostics.UnconfirmedCleanupWarningDataKey]
                    = warning;
                cleanupWarnings[FaceToFaceFrameCleanupDiagnostics.RetainedPathDataKey]
                    = Path.GetFullPath(frameRoot);
                LogRetainedFrameRoot(frameRoot, warning);
                return cleanupWarnings;
            }

            if (Directory.Exists(frameRoot))
            {
                FaceToFaceFrameCleanupConfirmation documentCleanup;
                try
                {
                    documentCleanup = _generationSession
                        .CloseDocumentsUnderDirectory(frameRoot);
                }
                catch (Exception cleanupException)
                {
                    documentCleanup = FaceToFaceFrameCleanupConfirmation.Unconfirmed(
                        "确认基架目录文档关闭时发生异常：" + cleanupException.Message);
                }

                if (documentCleanup == null || !documentCleanup.IsConfirmed)
                {
                    var warning = "递归清理前无法确认基架目录下的文档均已关闭："
                        + (documentCleanup?.Diagnostic ?? "未返回确认结果");
                    cleanupWarnings[
                        FaceToFaceFrameCleanupDiagnostics.DocumentCleanupWarningDataKey]
                        = warning;
                    cleanupWarnings[FaceToFaceFrameCleanupDiagnostics.RetainedPathDataKey]
                        = Path.GetFullPath(frameRoot);
                    LogRetainedFrameRoot(frameRoot, warning);
                    return cleanupWarnings;
                }

                try
                {
                    Exception cleanupError;
                    if (!_directoryCleaner.TryDelete(frameRoot, out cleanupError))
                    {
                        var warning = "磁盘目录清理失败："
                            + (cleanupError?.Message ?? "未知清理错误");
                        cleanupWarnings[
                            FaceToFaceFrameCleanupDiagnostics.GeneratedDirectoryCleanupWarningDataKey]
                            = warning;
                        cleanupWarnings[FaceToFaceFrameCleanupDiagnostics.RetainedPathDataKey]
                            = Path.GetFullPath(frameRoot);
                        LogRetainedFrameRoot(frameRoot, warning);
                        return cleanupWarnings;
                    }
                }
                catch (Exception cleanupException)
                {
                    var warning = "磁盘目录清理异常：" + cleanupException.Message;
                    cleanupWarnings[
                        FaceToFaceFrameCleanupDiagnostics.GeneratedDirectoryCleanupWarningDataKey]
                        = warning;
                    cleanupWarnings[FaceToFaceFrameCleanupDiagnostics.RetainedPathDataKey]
                        = Path.GetFullPath(frameRoot);
                    LogRetainedFrameRoot(frameRoot, warning);
                    return cleanupWarnings;
                }
            }

            if (frameRootExisted)
            {
                try
                {
                    Directory.CreateDirectory(frameRoot);
                }
                catch (Exception restoreException)
                {
                    var warning = "恢复调用前的空目录状态失败：" + restoreException.Message;
                    cleanupWarnings[
                        FaceToFaceFrameCleanupDiagnostics.EmptyRootRestoreWarningDataKey]
                        = warning;
                    LogRetainedFrameRoot(frameRoot, warning);
                }
            }

            return cleanupWarnings;
        }

        private static void AttachCleanupWarnings(
            Exception originalException,
            IReadOnlyDictionary<string, string> cleanupWarnings)
        {
            foreach (var warning in cleanupWarnings)
            {
                FaceToFaceFrameCleanupDiagnostics.AppendData(
                    originalException,
                    warning.Key,
                    warning.Value);
            }
        }

        private static void LogRetainedFrameRoot(string frameRoot, string reason)
        {
            Debug.Print(
                "基架目录已保留，需人工检查总装配体后处理：{0}；{1}",
                frameRoot,
                reason);
        }

        private static Func<FaceToFaceFrameModelPlan, string> CreateFixedTemplateResolver(
            string templateAssemblyPath)
        {
            if (string.IsNullOrWhiteSpace(templateAssemblyPath))
            {
                throw new ArgumentException(
                    "基架模板总装配体路径不能为空。",
                    nameof(templateAssemblyPath));
            }

            return _ => templateAssemblyPath;
        }

        /// <summary>
        /// 保留既有可注入装配器构造入口，供不连接 SolidWorks 的契约测试使用。
        /// 生产首次生成入口使用真实会话，实际执行总装保存、关闭和重开。
        /// </summary>
        private sealed class LegacyOpenTotalAssemblySession
            : IFaceToFaceFrameInitialGenerationSession
        {
            private readonly IFaceToFaceFrameAssembler _assembler;

            internal LegacyOpenTotalAssemblySession(IFaceToFaceFrameAssembler assembler)
            {
                _assembler = assembler ?? throw new ArgumentNullException(nameof(assembler));
            }

            public string CaptureModelMapping()
            {
                return _assembler.CaptureModelMapping();
            }

            public void SaveAndCloseTotalAssembly()
            {
                // 兼容纯单元测试的旧构造入口；不连接 SolidWorks，不执行文档生命周期操作。
            }

            public FaceToFaceFrameCleanupConfirmation CloseDocumentsUnderDirectory(
                string directoryPath)
            {
                return _assembler.ConfirmDocumentsClosedUnderDirectory(directoryPath);
            }

            public IFaceToFaceFrameAssembler ReopenTotalAssembly()
            {
                return _assembler;
            }

            public void EnsureTotalAssemblyOpen()
            {
                // 兼容纯单元测试的旧构造入口。
            }
        }

        /// <summary>
        /// 兼容既有不含绝缘子的调用入口；数量为 0 时不会触发此实现。
        /// </summary>
        private sealed class NullFaceToFaceFrameInsulatorDriver
            : IFaceToFaceFrameInsulatorDriver
        {
            internal static readonly NullFaceToFaceFrameInsulatorDriver Instance
                = new NullFaceToFaceFrameInsulatorDriver();

            private NullFaceToFaceFrameInsulatorDriver()
            {
            }

            public string PrepareInsulator(
                string projectPath,
                FaceToFaceFrameInsulatorDrivePlan plan)
            {
                throw new InvalidOperationException("未配置面对面基架绝缘子驱动器。");
            }

            public void AssembleIntoFrame(
                string frameAssemblyPath,
                string insulatorAssemblyPath,
                FaceToFaceFrameModelPlan modelPlan)
            {
                throw new InvalidOperationException("未配置面对面基架绝缘子驱动器。");
            }

            public FaceToFaceFrameCleanupConfirmation CloseDocumentsAfterFailure(
                string frameAssemblyPath,
                string insulatorAssemblyPath)
            {
                return FaceToFaceFrameCleanupConfirmation.Unconfirmed(
                    "未配置面对面基架绝缘子驱动器，无法确认文档已关闭。");
            }
        }
    }
}
