using CKBusbarBridge.Helpers;
using CKBusbarBridge.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CKBusbarBridge.Drivers
{
    /// <summary>
    /// 编排侧对侧绝缘子准备、基座驱动/内部装配、总装插入、映射写入和失败回滚。
    /// </summary>
    internal sealed class SideToSideBaseGenerationService
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
        private readonly Action<string> _closeGeneratedDocuments;
        private const string InsulatorPreparationFailureDataKey =
            "SideToSideInsulatorPreparationFailure";

        private readonly ISideToSideBaseTemplateLocator _templateLocator;
        private readonly ISideToSideBaseDriver _driver;
        private readonly ISideToSideBaseInsulatorDriver _insulatorDriver;
        private readonly ISideToSideBaseAssembler _assembler;
        private readonly IFaceToFaceBaseDirectoryCleaner _directoryCleaner;

        internal SideToSideBaseGenerationService(
            ISideToSideBaseTemplateLocator templateLocator,
            ISideToSideBaseDriver driver,
            ISideToSideBaseInsulatorDriver insulatorDriver,
            ISideToSideBaseAssembler assembler,
            IFaceToFaceBaseDirectoryCleaner directoryCleaner,
            Action<string> closeGeneratedDocuments = null)
        {
            _templateLocator = templateLocator
                ?? throw new ArgumentNullException(nameof(templateLocator));
            _driver = driver ?? throw new ArgumentNullException(nameof(driver));
            _insulatorDriver = insulatorDriver
                ?? throw new ArgumentNullException(nameof(insulatorDriver));
            _assembler = assembler ?? throw new ArgumentNullException(nameof(assembler));
            _directoryCleaner = directoryCleaner
                ?? throw new ArgumentNullException(nameof(directoryCleaner));
            _closeGeneratedDocuments = closeGeneratedDocuments;
        }

        internal void Generate(
            string projectPath,
            SideToSideCabinetDriverParameters parameters,
            IDrawingNumberGenerator drawingNumbers,
            Action<string, int, int> reportProgress = null)
        {
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                throw new ArgumentException("项目目录不能为空。", nameof(projectPath));
            }

            // 纯规划器先完成全部数值校验，任何模型或目录写入都发生在此之后。
            var positions = SideToSideBaseDrivePlanner.CreatePlans(parameters);
            var lifecyclePlan =
                SideToSideBaseLifecycleAdapter.CreateFirstGenerationPlan(positions);
            var preparedModels = new Dictionary<string, string>(StringComparer.Ordinal);
            if (positions.Count == 0)
            {
                return;
            }

            foreach (var templateCheck in positions
                .SelectMany(position => position.Insulators.Select(insulator => new
                {
                    Position = position,
                    Insulator = insulator
                }))
                .GroupBy(item => item.Insulator.HostModelKind)
                .Select(group => group.First()))
            {
                ExecutePositionStage(
                    templateCheck.Position,
                    templateCheck.Insulator.HostModelKind,
                    "绝缘子模板预检",
                    () => _insulatorDriver.ValidateTemplateAvailable(
                        templateCheck.Insulator));
            }

            var fullProjectPath = Path.GetFullPath(projectPath);
            var baseRoot = Path.Combine(fullProjectPath, "基座");
            var baseRootExisted = Directory.Exists(baseRoot);
            if (baseRootExisted && Directory.EnumerateFileSystemEntries(baseRoot).Any())
            {
                throw new IOException("基座输出目录不是空目录，无法执行首次生成：" + baseRoot);
            }

            // 三种模板也在创建输出目录前一次性确认，避免第一位置完成后才发现后续模板缺失。
            var templatePaths = ResolveRequiredTemplatePaths(positions);
            var inserted = new List<SideToSideBaseAssemblyToken>();
            var completedPositions = 0;
            var totalSteps = positions.Count + 1;
            var insulatorDirectories = new Dictionary<string, BaseModelTransactionDirectory>(
                StringComparer.OrdinalIgnoreCase);
            try
            {
                Directory.CreateDirectory(baseRoot);
                foreach (var position in positions)
                {
                    reportProgress?.Invoke($"正在生成独立基座及绝缘子 {completedPositions + 1}/{positions.Count}：{position.PositionName}", completedPositions, totalSteps);
                    if (position.BaseModel.Kind == SideToSideBaseModelKind.Base1)
                    {
                        var base1Path = GetOrCreateHostWithInsulators(
                            lifecyclePlan,
                            preparedModels,
                            baseRoot,
                            fullProjectPath,
                            position,
                            position.BaseModel,
                            position.Insulators,
                            drawingNumbers,
                            templatePaths,
                            insulatorDirectories);
                        inserted.Add(ExecutePositionStage(
                            position,
                            SideToSideBaseModelKind.Base1,
                            "插入总装配体",
                            () => _assembler.InsertBase1AtRoot(position, base1Path)));
                    }
                    else
                    {
                        var elevatedPath = GetOrCreateHostWithInsulators(
                            lifecyclePlan,
                            preparedModels,
                            baseRoot,
                            fullProjectPath,
                            position,
                            position.ElevatedBaseModel,
                            position.Insulators,
                            drawingNumbers,
                            templatePaths,
                            insulatorDirectories);
                        var elevated = ExecutePositionStage(
                            position,
                            SideToSideBaseModelKind.ElevatedBase,
                            "插入总装配体",
                            () => _assembler.InsertElevatedAtRoot(position, elevatedPath));
                        inserted.Add(elevated);

                        var base2Path = GetOrCreateHostWithInsulators(
                            lifecyclePlan,
                            preparedModels,
                            baseRoot,
                            fullProjectPath,
                            position,
                            position.BaseModel,
                            new SideToSideInsulatorPlacementPlan[0],
                            drawingNumbers,
                            templatePaths,
                            insulatorDirectories);
                        inserted.Add(ExecutePositionStage(
                            position,
                            SideToSideBaseModelKind.Base2,
                            "装配到增高基座",
                            () => _assembler.InsertBase2OnElevated(
                                position,
                                base2Path,
                                elevated)));
                    }

                    ExecutePositionStage(
                        position,
                        position.BaseModel.Kind,
                        "总装重建与保存",
                        _assembler.SaveAndRebuild);
                    completedPositions++;
                }

                // 映射只有在四个候选位置全部处理成功后才写入总装自定义属性。
                try
                {
                    reportProgress?.Invoke("正在写入基座模型映射", completedPositions, totalSteps);
                    _assembler.WriteModelMapping(
                        SideToSideBaseModelMappingHelper.BuildJson(inserted));
                    reportProgress?.Invoke("独立基座生成完成", totalSteps, totalSteps);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException(
                        "总装配体/模型映射/写入失败：" + exception.Message,
                        exception);
                }
            }
            catch (Exception originalException)
            {
                Logger.Error(originalException, "侧对侧基座生成失败，开始回滚：{0}", baseRoot);
                CKBusbarBridgePreview.FaceToWallPreviewTrace.Error("侧对侧基座生成失败", originalException, baseRoot);
                try { reportProgress?.Invoke("基座生成失败，正在回滚清理", completedPositions, totalSteps); }
                catch (Exception reportException)
                {
                    Logger.Warn(reportException, "失败进度提示异常，继续回滚侧对侧基座：{0}", baseRoot);
                }
                var preserveInsulatorDirectories =
                    originalException.Data.Contains(InsulatorPreparationFailureDataKey);
                var cleanupErrors = Rollback(
                    fullProjectPath,
                    baseRoot,
                    baseRootExisted,
                    inserted,
                    insulatorDirectories.Values.ToArray(),
                    originalException is SideToSideBaseAssemblyCleanupException,
                    preserveInsulatorDirectories);
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

        private string GetOrCreateHostWithInsulators(
            BaseModelFirstGenerationPlan lifecyclePlan,
            IDictionary<string, string> preparedModels,
            string baseRoot,
            string projectPath,
            SideToSideBasePositionPlan position,
            SideToSideBaseModelPlan modelPlan,
            IReadOnlyList<SideToSideInsulatorPlacementPlan> placements,
            IDrawingNumberGenerator drawingNumbers,
            IReadOnlyDictionary<SideToSideBaseModelKind, string> templatePaths,
            IDictionary<string, BaseModelTransactionDirectory> transactionDirectories)
        {
            var targetKey = lifecyclePlan.ResolveTargetKey(
                position.PositionName,
                modelPlan.Kind.ToString());
            if (preparedModels.TryGetValue(targetKey, out var preparedPath))
            {
                return preparedPath;
            }

            preparedPath = PrepareHostWithInsulators(
                baseRoot,
                projectPath,
                position,
                modelPlan,
                placements,
                drawingNumbers,
                templatePaths,
                transactionDirectories);
            preparedModels.Add(targetKey, preparedPath);
            return preparedPath;
        }

        private string PrepareHostWithInsulators(
            string baseRoot,
            string projectPath,
            SideToSideBasePositionPlan position,
            SideToSideBaseModelPlan modelPlan,
            IReadOnlyList<SideToSideInsulatorPlacementPlan> placements,
            IDrawingNumberGenerator drawingNumbers,
            IReadOnlyDictionary<SideToSideBaseModelKind, string> templatePaths,
            IDictionary<string, BaseModelTransactionDirectory> transactionDirectories)
        {
            var outputDirectory = Path.Combine(
                baseRoot,
                position.PositionName,
                GetModelFolderName(modelPlan.Kind));
            var insulatorPaths = new List<string>();

            if (placements != null && placements.Count > 0)
            {
                if (placements.Any(placement => placement.HostModelKind != modelPlan.Kind))
                {
                    throw new InvalidOperationException(
                        BuildStagePrefix(position, modelPlan.Kind, "绝缘子计划校验")
                        + "绝缘子宿主类型与当前基座模型不一致。");
                }

                foreach (var placement in placements)
                {
                    var stageSuffix = "（" + placement.HostCoordinateName + "）";
                    var insulatorOutputDirectory = Path.GetFullPath(
                        ExecutePositionStage(
                            position,
                            modelPlan.Kind,
                            "绝缘子输出目录解析" + stageSuffix,
                            () => _insulatorDriver.ResolveOutputDirectory(
                                projectPath,
                                placement,
                                placement.BracketHostSpan)));
                    if (!transactionDirectories.ContainsKey(insulatorOutputDirectory))
                    {
                        // 只登记本次将创建或填充的目录；已存在非空缓存会复用，不在失败时删除。
                        var existedBefore = Directory.Exists(insulatorOutputDirectory);
                        if (!existedBefore
                            || !Directory.EnumerateFileSystemEntries(insulatorOutputDirectory).Any())
                        {
                            transactionDirectories.Add(
                                insulatorOutputDirectory,
                                new BaseModelTransactionDirectory(
                                    insulatorOutputDirectory,
                                    existedBefore));
                        }
                    }

                    try
                    {
                        insulatorPaths.Add(ExecutePositionStage(
                            position,
                            modelPlan.Kind,
                            "绝缘子准备" + stageSuffix,
                            () => _insulatorDriver.PrepareInsulator(
                                projectPath,
                                placement,
                                placement.BracketHostSpan)));
                    }
                    catch (Exception exception)
                    {
                        // 绝缘子准备失败时保留输出，便于检查 PackAndGo 结果；
                        // 其他阶段失败仍沿用完整事务回滚。
                        exception.Data[InsulatorPreparationFailureDataKey] = true;
                        throw;
                    }
                }
            }

            var path = ExecutePositionStage(
                position,
                modelPlan.Kind,
                "模型复制与驱动",
                () => _driver.PrepareModel(
                    modelPlan,
                    templatePaths[modelPlan.Kind],
                    outputDirectory,
                    drawingNumbers));

            if (placements != null && placements.Count > 0)
            {
                ExecutePositionStage(
                    position,
                    modelPlan.Kind,
                    "基座定位与绝缘子装配",
                    () => _driver.InstallInsulators(path, insulatorPaths, placements));
            }

            return path;
        }

        private static T ExecutePositionStage<T>(
            SideToSideBasePositionPlan position,
            SideToSideBaseModelKind modelKind,
            string stageName,
            Func<T> action)
        {
            try
            {
                return action();
            }
            catch (SideToSideBaseAssemblyCleanupException exception)
            {
                // 保留“失败组件即时清理未确认”信号，外层回滚才会保留被总装引用的文件。
                throw new SideToSideBaseAssemblyCleanupException(
                    BuildStagePrefix(position, modelKind, stageName) + exception.Message,
                    exception);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    BuildStagePrefix(position, modelKind, stageName) + exception.Message,
                    exception);
            }
        }

        private static void ExecutePositionStage(
            SideToSideBasePositionPlan position,
            SideToSideBaseModelKind modelKind,
            string stageName,
            Action action)
        {
            ExecutePositionStage(
                position,
                modelKind,
                stageName,
                () =>
                {
                    action();
                    return true;
                });
        }

        private static string BuildStagePrefix(
            SideToSideBasePositionPlan position,
            SideToSideBaseModelKind modelKind,
            string stageName)
        {
            return position.PositionName
                + "/"
                + GetModelFolderName(modelKind)
                + "/"
                + stageName
                + "失败：";
        }

        private IReadOnlyDictionary<SideToSideBaseModelKind, string> ResolveRequiredTemplatePaths(
            IReadOnlyList<SideToSideBasePositionPlan> positions)
        {
            var kinds = positions
                .SelectMany(position => position.HasElevatedBase
                    ? new[] { position.BaseModel.Kind, position.ElevatedBaseModel.Kind }
                    : new[] { position.BaseModel.Kind })
                .Distinct()
                .ToArray();
            var paths = new Dictionary<SideToSideBaseModelKind, string>();
            foreach (var kind in kinds)
            {
                paths[kind] = _templateLocator.GetTemplateAssemblyPath(kind);
            }

            return paths;
        }

        private IReadOnlyList<string> Rollback(
            string projectPath,
            string baseRoot,
            bool baseRootExisted,
            IReadOnlyList<SideToSideBaseAssemblyToken> inserted,
            IReadOnlyList<BaseModelTransactionDirectory> insulatorDirectories,
            bool cleanupUnconfirmed,
            bool preserveInsulatorDirectories)
        {
            var errors = new List<string>();
            string safeBaseRoot;
            try
            {
                safeBaseRoot = BaseModelTransactionPathGuard.EnsureSafeDescendant(
                    projectPath,
                    baseRoot);
            }
            catch (Exception exception)
            {
                errors.Add("基座目录事务校验失败：" + exception.Message);
                return errors;
            }

            var rollbackConfirmed = !cleanupUnconfirmed;
            if (cleanupUnconfirmed)
            {
                errors.Add("失败步骤中已插入组件的即时清理未得到确认。");
            }

            if (inserted.Count > 0)
            {
                try
                {
                    // 按插入相反顺序删除，确保基座2先于其父级增高基座移除。
                    _assembler.Rollback(inserted.Reverse().ToArray());
                }
                catch (Exception exception)
                {
                    rollbackConfirmed = false;
                    errors.Add("总装配体回滚失败：" + exception.Message);
                }
            }

            if (!rollbackConfirmed)
            {
                errors.Add("为保护总装残留引用，已保留本次基座目录：" + safeBaseRoot);
                return errors;
            }

            if (Directory.Exists(safeBaseRoot))
            {
                try
                {
                    _closeGeneratedDocuments?.Invoke(safeBaseRoot);
                }
                catch (Exception exception)
                {
                    Logger.Error(exception, "侧对侧基座回滚文档仍驻留，保留目录：{0}", safeBaseRoot);
                    CKBusbarBridgePreview.FaceToWallPreviewTrace.Error("侧对侧基座回滚清理失败", exception, safeBaseRoot);
                    errors.Add("基座文档关闭未确认，已保留目录：" + safeBaseRoot + "；" + exception.Message);
                    return errors;
                }
                if (!_directoryCleaner.TryDelete(safeBaseRoot, out var cleanupError))
                {
                    Logger.Error(cleanupError, "侧对侧基座回滚目录删除失败：{0}", safeBaseRoot);
                    if (cleanupError != null) CKBusbarBridgePreview.FaceToWallPreviewTrace.Error("侧对侧基座目录清理失败", cleanupError, safeBaseRoot);
                    errors.Add("基座目录清理失败：" + cleanupError?.Message);
                }
            }

            if (!preserveInsulatorDirectories)
            {
                foreach (var directory in insulatorDirectories
                    .OrderByDescending(item => item.Path.Length))
                {
                    string safeDirectory;
                    try
                    {
                        safeDirectory = BaseModelTransactionPathGuard
                            .EnsureSafeProjectModelDescendant(projectPath, directory.Path);
                    }
                    catch (Exception exception)
                    {
                        errors.Add(
                            "绝缘子事务目录安全校验失败："
                            + directory.Path
                            + "；"
                            + exception.Message);
                        continue;
                    }

                    try
                    {
                        // 总装引用已回滚后，先关闭绝缘子装配体和子零件，
                        // 再删除目录，避免 SolidWorks 文件句柄导致清理失败。
                        if (_closeGeneratedDocuments != null) _closeGeneratedDocuments(safeDirectory);
                        else _insulatorDriver.CloseOpenDocumentsUnderDirectory(safeDirectory);
                    }
                    catch (Exception exception)
                    {
                        errors.Add(
                            "绝缘子文档关闭失败："
                            + directory.Path
                            + "；"
                            + exception.Message);
                        continue;
                    }

                    if (Directory.Exists(safeDirectory)
                        && !_directoryCleaner.TryDelete(safeDirectory, out var cleanupError))
                    {
                        errors.Add(
                            "绝缘子目录清理失败："
                            + safeDirectory
                            + "；"
                            + cleanupError?.Message);
                        continue;
                    }

                    if (directory.ExistedBefore)
                    {
                        Directory.CreateDirectory(safeDirectory);
                    }
                }
            }

            if (baseRootExisted)
            {
                Directory.CreateDirectory(safeBaseRoot);
            }

            return errors;
        }

        private static string GetModelFolderName(SideToSideBaseModelKind kind)
        {
            switch (kind)
            {
                case SideToSideBaseModelKind.Base1:
                    return "基座1";
                case SideToSideBaseModelKind.Base2:
                    return "基座2";
                case SideToSideBaseModelKind.ElevatedBase:
                    return "增高基座";
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }
    }
}
